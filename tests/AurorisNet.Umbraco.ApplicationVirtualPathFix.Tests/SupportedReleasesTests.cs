using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using AurorisNet.Umbraco.ApplicationVirtualPathFix.Assets;
using AurorisNet.Umbraco.ApplicationVirtualPathFix.Patchsets;
using AurorisNet.Umbraco.ApplicationVirtualPathFix.UmbracoVersions;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests;

/// <summary>Checks the list of supported Umbraco releases and the release ranges in the patch list.</summary>
public sealed class SupportedReleasesTests
{
    /// <summary>
    /// Checks that <c>UmbracoReleases</c> lists distinct releases oldest first, and that the package's dependency range
    /// runs from the first of them to the last. The package must embed and accept exactly the same list.
    /// </summary>
    [Fact]
    public void The_package_range_spans_the_listed_releases()
    {
        var releases = UmbracoUnderTest.SupportedReleases;
        Assert.NotEmpty(releases);
        Assert.Equal(releases.Order().Distinct(), releases);
        var metadata = typeof(ApplicationVirtualPathFixComposer).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToArray();
        Assert.Equal(releases[0], Version.Parse(metadata.Single(attribute => attribute.Key == "UmbracoMinimumVersion").Value!));
        Assert.Equal(releases[^1], Version.Parse(metadata.Single(attribute => attribute.Key == "UmbracoMaximumVersion").Value!));
        Assert.Equal(releases, metadata.Single(attribute => attribute.Key == "UmbracoReleases").Value!
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Version.Parse));
        Assert.Equal(string.Join(", ", releases.AsEnumerable()), ApplicationVirtualPathFixComposer.SupportedUmbracoVersions);
        Assert.All(releases, release => Assert.True(ApplicationVirtualPathFixComposer.SupportsUmbraco(new TestUmbracoVersion(release))));
    }

    /// <summary>A prerelease does not inherit support from its validated final release.</summary>
    [Fact]
    public void A_prerelease_is_not_a_validated_release()
    {
        Assert.False(ApplicationVirtualPathFixComposer.SupportsUmbraco(new TestUmbracoVersion(UmbracoUnderTest.Release, "rc.1")));
    }

    /// <summary>
    /// Checks that every entry's <c>From</c> and <c>Before</c> are three-part release numbers, that <c>From</c> comes
    /// before <c>Before</c>, and that each entry applies to at least one supported release, so the list holds no entry
    /// that can never run.
    /// </summary>
    [Fact]
    public void Every_entry_applies_to_a_supported_release()
    {
        foreach (var patch in BackOfficeAssetPatches.Patches)
        {
            foreach (var bound in new[] { patch.From, patch.Before }.OfType<string>())
                Assert.True(Version.TryParse(bound, out var release) && release.Build >= 0 && release.Revision < 0,
                    $"{patch}: {bound} is not a release number such as 18.2.0.");
            if (patch is { From: { } from, Before: { } before })
                Assert.True(Version.Parse(from) < Version.Parse(before), $"{patch}: From must come before Before.");
            Assert.True(UmbracoUnderTest.SupportedReleases.Any(patch.AppliesTo), $"{patch} applies to no supported release.");
        }
    }

    /// <summary>
    /// Checks that the package defines what it does for the release under test's major version, as a release of a new
    /// major version needs its own <see cref="UmbracoMajor"/>, and that <see cref="UmbracoMajor.All"/> lists each major
    /// version once, oldest first.
    /// </summary>
    [Fact]
    public void The_major_version_under_test_is_defined()
    {
        Assert.Equal(UmbracoUnderTest.Release.Major, UmbracoUnderTest.Major.Number);
        var numbers = UmbracoMajor.All.Select(major => major.Number).ToArray();
        Assert.Equal(numbers.Order().Distinct(), numbers);
    }

    /// <summary>
    /// Checks that each major version's entries start at one of its releases and end by the next major version's first
    /// release at the latest, so supporting another major version never changes them.
    /// </summary>
    [Fact]
    public void Each_major_versions_entries_stay_within_it()
    {
        foreach (var major in UmbracoMajor.All)
        {
            foreach (var patch in major.Patches)
            {
                Assert.True(patch.From is { } from && Version.Parse(from).Major == major.Number,
                    $"{patch} must start at an Umbraco {major.Number} release.");
                Assert.True(patch.Before is { } before && Version.Parse(before) <= new Version(major.Number + 1, 0, 0),
                    $"{patch} must end by Umbraco {major.Number + 1}.0.0.");
            }
        }
    }

    /// <summary>
    /// Checks that every type and member the package's compiled code refers to exists in the Umbraco release under test.
    /// The package compiles against the oldest supported release, so a member that a later release removed or changed,
    /// as Umbraco 18 gave the pipeline filter's full constructor two more parameters, would otherwise fail only when the
    /// code that uses it runs. References that need the generic arguments of the code that makes them are skipped. Each
    /// of the package's types must also load, which fails when an Umbraco interface it implements gained a member without
    /// a default implementation.
    /// </summary>
    [Fact]
    public void Every_type_and_member_the_package_uses_exists()
    {
        var assembly = typeof(ApplicationVirtualPathFixComposer).Assembly;
        using var stream = File.OpenRead(assembly.Location);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var tokens = metadata.TypeReferences.Select(handle => MetadataTokens.GetToken(handle))
            .Concat(metadata.MemberReferences.Select(handle => MetadataTokens.GetToken(handle)));
        var missing = new List<string>();
        foreach (var token in tokens)
        {
            try
            {
                assembly.ManifestModule.ResolveMember(token);
            }
            catch (Exception exception) when (exception is TypeLoadException or MissingMemberException or FileNotFoundException)
            {
                missing.Add(exception.Message);
            }
            catch (ArgumentException)
            {
                // The reference uses the generic parameters of its enclosing type or method.
            }
        }

        // Loading each of the package's own types checks that its base types and interfaces exist and that it still
        // implements every member they require.
        try
        {
            assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            missing.AddRange(exception.LoaderExceptions.Select(loader => loader?.Message ?? "A type failed to load."));
        }

        Assert.True(missing.Count == 0, $"Umbraco {UmbracoUnderTest.Release} lacks what the package uses:\n" + string.Join('\n', missing));
    }

    /// <summary>
    /// Checks that the release under test is one of the supported releases. Testing an unlisted release fails this
    /// check until the release is added to <c>UmbracoReleases</c>.
    /// </summary>
    [Fact]
    public void The_release_under_test_is_supported()
    {
        Assert.Contains(UmbracoUnderTest.Release, UmbracoUnderTest.SupportedReleases);
    }

    /// <summary>
    /// Checks that an entry applies from its <c>From</c> release up to, but not including, its <c>Before</c> release, and
    /// that an entry without them applies to every release.
    /// </summary>
    [Theory]
    [InlineData(null, null, "18.0.0", true)]
    [InlineData("18.2.0", "18.3.0", "18.1.1", false)]
    [InlineData("18.2.0", "18.3.0", "18.2.0", true)]
    [InlineData("18.2.0", "18.3.0", "18.2.9", true)]
    [InlineData("18.2.0", "18.3.0", "18.3.0", false)]
    [InlineData("18.3.0", null, "19.0.0", true)]
    public void Entries_apply_from_their_first_release_until_their_last(string? from, string? before, string release, bool applies)
    {
        var patch = new AssetPatch { Path = "/test.js", From = from, Before = before };
        Assert.Equal(applies, patch.AppliesTo(Version.Parse(release)));
    }
}
