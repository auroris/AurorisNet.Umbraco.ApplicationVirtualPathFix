using AurorisNet.Umbraco.ApplicationVirtualPathFix.Assets;
using AurorisNet.Umbraco.ApplicationVirtualPathFix.UmbracoVersions.Umbraco17;
using AurorisNet.Umbraco.ApplicationVirtualPathFix.UmbracoVersions.Umbraco18;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix.UmbracoVersions;

/// <summary>
/// What the package does differently for one Umbraco major version: the backoffice patches that only its releases
/// need, and the parts of Umbraco's API that it handles differently. Each supported major version has a subclass in a
/// folder of its own here, and a folder of the same name under <c>Patchsets</c> for its patches.
/// </summary>
/// <remarks>
/// <para>
/// Umbraco 17 and 18 both run on .NET 10, so one build of the package serves both, and <see cref="For"/> picks the
/// subclass for the site's release at runtime. That build compiles against the oldest supported release, so a subclass
/// can use only the APIs that release has. Code that every supported major version handles alike stays outside these
/// folders.
/// </para>
/// <para>
/// Supporting a new major version adds its folders, its subclass and its entry in <see cref="All"/>, and changes no
/// other major version's. Umbraco 19 runs on .NET 11, so its subclass belongs to a .NET 11 build of the package, where
/// it can use Umbraco 19's API directly. <c>docs/DEVELOPING.md</c> lists the steps.
/// </para>
/// </remarks>
internal abstract class UmbracoMajor
{
    /// <summary>The supported major versions, oldest first.</summary>
    internal static readonly UmbracoMajor[] All = [new Umbraco17Major(), new Umbraco18Major()];

    /// <summary>The major version number, such as 18.</summary>
    internal abstract int Number { get; }

    /// <summary>
    /// The entries for this major version's releases only, in the order the patcher reads them. Each lies within this
    /// major version.
    /// </summary>
    internal abstract AssetPatch[] Patches { get; }

    /// <summary>The path under which Umbraco serves its API documents and their UI outside production.</summary>
    internal abstract PathString ApiDocuments { get; }

    /// <summary>
    /// Returns the major version of the Umbraco release <paramref name="release"/>, such as 18.2.1. A release of an
    /// unsupported major version gets the newest older one, or the oldest supported, as the closest match.
    /// </summary>
    internal static UmbracoMajor For(Version release) => All.LastOrDefault(major => major.Number <= release.Major) ?? All[0];
}
