namespace AurorisNet.Umbraco.ApplicationVirtualPathFix;

/// <summary>What the package does differently for Umbraco 18.</summary>
internal sealed class Umbraco18 : UmbracoMajor
{
    /// <summary>18.</summary>
    internal override int Number => 18;

    /// <summary>The entries in <see cref="Umbraco18Patches"/>.</summary>
    internal override AssetPatch[] Patches => Umbraco18Patches.Patches;

    /// <summary>
    /// Umbraco 18 serves its API documents and their UI with Microsoft.AspNetCore.OpenApi, under
    /// <c>/umbraco/openapi</c>.
    /// </summary>
    internal override PathString ApiDocuments => "/umbraco/openapi";
}
