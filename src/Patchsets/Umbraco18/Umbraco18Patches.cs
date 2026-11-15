namespace AurorisNet.Umbraco.ApplicationVirtualPathFix;

/// <summary>
/// The entries for Umbraco 18 releases only. <see cref="BackOfficeAssetPatches"/> explains how to read an entry.
/// </summary>
/// <remarks>
/// Every entry here lies within Umbraco 18, so supporting another major version never changes this file. Umbraco 18's
/// releases share code with 17's later ones: 18.0 with 17.5, 18.1 with 17.6, 18.2 with 17.7, and 18.3 with 17.8. Such an
/// entry makes the same changes as its counterpart in <see cref="Umbraco17Patches"/>, using edits that the area files
/// in the parent folder define once.
/// </remarks>
internal static class Umbraco18Patches
{
    /// <summary>The entries, in the order the patcher reads them.</summary>
    internal static readonly AssetPatch[] Patches =
    [
        // The block grid's default layout stylesheet, "/umbraco/backoffice/css/umbraco-blockgridlayout.css". The bundler
        // defines it in a shared chunk named after one of the chunk's modules, and that name changes between releases.

        // In 18.0 and 18.1 the chunk is one of two named constants-????????.js, so Contains picks the one with the
        // stylesheet.
        new()
        {
            Path = "/umbraco/backoffice/packages/block/constants-????????.js",
            From = "18.0.0",
            Before = "18.2.0",
            Contains = "umbraco-blockgridlayout.css",
            Source = "src/packages/block/block-grid/context/constants.ts",
            MovesAssetUrls = true,
        },

        // 18.2.
        new()
        {
            Path = "/umbraco/backoffice/packages/block/data-path-element-data-query.function-????????.js",
            From = "18.2.0",
            Before = "18.3.0",
            Source = "src/packages/block/block-grid/context/constants.ts",
            MovesAssetUrls = true,
        },

        // 18.3 and later 18 releases.
        new()
        {
            Path = "/umbraco/backoffice/packages/block/data-path-generator-for-element-data.function-????????.js",
            From = "18.3.0",
            Before = "19.0.0",
            Source = "src/packages/block/block-grid/context/constants.ts",
            MovesAssetUrls = true,
        },

        // The rich text editor's stylesheets, which RichTextEditorPatches explains. 18.0 builds each link inline in a
        // forEach.
        new()
        {
            Path = "/umbraco/backoffice/packages/tiptap/input-tiptap.element-????????.js",
            From = "18.0.0",
            Before = "18.1.0",
            MovesAssetUrls = true,
            Edits = RichTextEditorPatches.LinkBuiltInForEach,
        },

        // 18.1 builds the links inline in an arrow function passed to map.
        new()
        {
            Path = "/umbraco/backoffice/packages/tiptap/input-tiptap.element-????????.js",
            From = "18.1.0",
            Before = "18.2.0",
            MovesAssetUrls = true,
            Edits = RichTextEditorPatches.LinksBuiltInMap,
        },

        // 18.2 and later 18 releases resolve stylesheets in a separate function.
        new()
        {
            Path = "/umbraco/backoffice/packages/tiptap/input-tiptap.element-????????.js",
            From = "18.2.0",
            Before = "19.0.0",
            MovesAssetUrls = true,
            Edits = RichTextEditorPatches.LinkResolvedByFunction,
        },

        // In 18.0 the login app calls the password-reset and invitation APIs through its own copy of the generated API
        // client, created without a base URL, so the requests go to the site root. The edit gives that copy the base URL
        // that the preamble in SignInPatches gives the shared client.
        new()
        {
            Path = "/umbraco/login/login.js",
            From = "18.0.0",
            Before = "18.1.0",
            Edits = SignInPatches.OwnClientBaseUrl,
        },
    ];
}
