using AurorisNet.Umbraco.ApplicationVirtualPathFix.Assets;
using AurorisNet.Umbraco.ApplicationVirtualPathFix.Patchsets.Umbraco17;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix.UmbracoVersions.Umbraco17;

/// <summary>What the package does differently for Umbraco 17.</summary>
internal sealed class Umbraco17Major : UmbracoMajor
{
    /// <summary>17.</summary>
    internal override int Number => 17;

    /// <summary>The entries in <see cref="Umbraco17Patches"/>.</summary>
    internal override AssetPatch[] Patches => Umbraco17Patches.Patches;

    /// <summary>Umbraco 17 serves its API documents and their UI with Swashbuckle, under <c>/umbraco/swagger</c>.</summary>
    internal override PathString ApiDocuments => "/umbraco/swagger";
}
