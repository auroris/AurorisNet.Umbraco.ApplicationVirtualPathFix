using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests;

/// <summary>Checks the list of supported Umbraco releases and the release ranges in the patch list.</summary>
public sealed class SupportedReleasesTests
{
    /// <summary>
    /// Checks that <c>UmbracoReleases</c> lists distinct releases oldest first, and that the package's dependency range
    /// runs from the first of them to the last.
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
        Assert.Equal($"{releases[0]} to {releases[^1]}", ApplicationVirtualPathFixComposer.SupportedUmbracoVersions);
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
    /// Checks that the release under test is one of the supported releases. Testing another release, as when checking a
    /// new Umbraco release, fails only this test until the release is added to <c>UmbracoReleases</c>.
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
