using AurorisNet.Umbraco.ApplicationVirtualPathFix.Assets;
using AurorisNet.Umbraco.ApplicationVirtualPathFix.Patchsets.Umbraco18;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix.Patchsets.Umbraco17;

/// <summary>
/// The entries for Umbraco 17 releases only. <see cref="BackOfficeAssetPatches"/> explains how to read an entry.
/// </summary>
/// <remarks>
/// Every entry here lies within Umbraco 17, so supporting another major version never changes this file. Umbraco 17's
/// later releases share code with 18's: 17.5 with 18.0, 17.6 with 18.1, 17.7 with 18.2, and 17.8 with 18.3. Such an
/// entry makes the same changes as its counterpart in <see cref="Umbraco18Patches"/>, using edits that the area files
/// in the parent folder define once.
/// </remarks>
internal static class Umbraco17Patches
{
    /// <summary>The entries, in the order the patcher reads them.</summary>
    internal static readonly AssetPatch[] Patches =
    [
        // The block grid's default layout stylesheet, "/umbraco/backoffice/css/umbraco-blockgridlayout.css". The bundler
        // defines it in a shared chunk named after one of the chunk's modules, and that name changes between releases.

        // In 17.5 and 17.6 the chunk is one of two named constants-????????.js, so Contains picks the one with the
        // stylesheet.
        new()
        {
            Path = "/umbraco/backoffice/packages/block/constants-????????.js",
            From = "17.5.0",
            Before = "17.7.0",
            Contains = "umbraco-blockgridlayout.css",
            Source = "src/packages/block/block-grid/context/constants.ts",
            MovesAssetUrls = true,
        },

        // 17.7.
        new()
        {
            Path = "/umbraco/backoffice/packages/block/data-path-element-data-query.function-????????.js",
            From = "17.7.0",
            Before = "17.8.0",
            Source = "src/packages/block/block-grid/context/constants.ts",
            MovesAssetUrls = true,
        },

        // 17.8 and later 17 releases.
        new()
        {
            Path = "/umbraco/backoffice/packages/block/data-path-generator-for-element-data.function-????????.js",
            From = "17.8.0",
            Before = "18.0.0",
            Source = "src/packages/block/block-grid/context/constants.ts",
            MovesAssetUrls = true,
        },

        // The rich text editor's stylesheets, which RichTextEditorPatches explains. 17.5 builds each link inline in a
        // forEach.
        new()
        {
            Path = "/umbraco/backoffice/packages/tiptap/input-tiptap.element-????????.js",
            From = "17.5.0",
            Before = "17.6.0",
            MovesAssetUrls = true,
            Edits = RichTextEditorPatches.LinkBuiltInForEach,
        },

        // 17.6 builds the links inline in an arrow function passed to map.
        new()
        {
            Path = "/umbraco/backoffice/packages/tiptap/input-tiptap.element-????????.js",
            From = "17.6.0",
            Before = "17.7.0",
            MovesAssetUrls = true,
            Edits = RichTextEditorPatches.LinksBuiltInMap,
        },

        // 17.7 and later 17 releases resolve stylesheets in a separate function.
        new()
        {
            Path = "/umbraco/backoffice/packages/tiptap/input-tiptap.element-????????.js",
            From = "17.7.0",
            Before = "18.0.0",
            MovesAssetUrls = true,
            Edits = RichTextEditorPatches.LinkResolvedByFunction,
        },

        // In 17.5 and 17.6 the login app calls the password-reset and invitation APIs through its own copy of the
        // generated API client, created without a base URL, so the requests go to the site root. The edit gives that
        // copy the base URL that the preamble in SignInPatches gives the shared client.
        new()
        {
            Path = "/umbraco/login/login.js",
            From = "17.5.0",
            Before = "17.7.0",
            Edits = SignInPatches.OwnClientBaseUrl,
        },
    ];
}
