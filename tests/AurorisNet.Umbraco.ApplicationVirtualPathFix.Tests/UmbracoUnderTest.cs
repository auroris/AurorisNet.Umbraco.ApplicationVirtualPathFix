using System.Reflection;
using AurorisNet.Umbraco.ApplicationVirtualPathFix.Assets;
using AurorisNet.Umbraco.ApplicationVirtualPathFix.UmbracoVersions;
using Umbraco.Cms.Core.Configuration;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests;

/// <summary>
/// The Umbraco release the tests run against, which the build chooses with <c>-p:UmbracoVersion</c>, and the releases the
/// package supports.
/// </summary>
internal static class UmbracoUnderTest
{
    /// <summary>The release of the loaded Umbraco assemblies, such as 18.2.1.</summary>
    internal static readonly Version Release = AssetPatcher.Release(new UmbracoVersion().SemanticVersion);

    /// <summary>
    /// The releases listed in <c>UmbracoReleases</c> in <c>Directory.Build.props</c>, in the order listed, which should
    /// be oldest first.
    /// </summary>
    internal static readonly Version[] SupportedReleases = typeof(UmbracoUnderTest).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().Single(attribute => attribute.Key == "UmbracoReleases").Value!
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Version.Parse).ToArray();

    /// <summary>The entries of the patch list that apply to <see cref="Release"/>, in list order.</summary>
    internal static AssetPatch[] Patches => AssetPatcher.For(Release);

    /// <summary>What the package does differently for <see cref="Release"/>'s major version.</summary>
    internal static UmbracoMajor Major => UmbracoMajor.For(Release);
}
