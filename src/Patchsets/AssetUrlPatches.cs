using AurorisNet.Umbraco.ApplicationVirtualPathFix.Assets;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix.Patchsets;

/// <summary>
/// Backoffice files that load their assets from fixed URLs: images, the code editor's worker scripts, the theme
/// stylesheets and an icon font.
/// </summary>
internal static class AssetUrlPatches
{
    /// <summary>The entries for these files, in the order the patcher reads them.</summary>
    internal static readonly AssetPatch[] Patches =
    [
        // Fixed asset URLs. These files load backoffice assets from "/umbraco/backoffice/...", which under a virtual
        // path is outside the site. MovesAssetUrls points each URL at the site's assets path.

        // The background illustration of the page shown when the backoffice cannot start.
        new()
        {
            Path = "/umbraco/backoffice/apps/app/app-error.element.js",
            MovesAssetUrls = true,
        },

        // The Umbraco logo in the backoffice header.
        new()
        {
            Path = "/umbraco/backoffice/apps/backoffice/components/backoffice-header-logo.element.js",
            MovesAssetUrls = true,
        },

        // The background illustration of the installer.
        new()
        {
            Path = "/umbraco/backoffice/apps/installer/shared/layout/installer-layout.element.js",
            MovesAssetUrls = true,
        },

        // The worker scripts that the code editor, Monaco, starts for HTML and CSS.
        new()
        {
            Path = "/umbraco/backoffice/external/monaco-editor/htmlMode-????????.js",
            Source = "node_modules/monaco-editor/esm/vs/language/html/workerManager.js",
            MovesAssetUrls = true,
        },

        // The worker script that the code editor starts for JSON.
        new()
        {
            Path = "/umbraco/backoffice/external/monaco-editor/jsonMode-????????.js",
            Source = "node_modules/monaco-editor/esm/vs/language/json/workerManager.js",
            MovesAssetUrls = true,
        },

        // The code editor's main chunk, which lists all of its worker scripts. The bundler names it after one of its
        // many modules.
        new()
        {
            Path = "/umbraco/backoffice/external/monaco-editor/rust-????????.js",
            Source = "src/external/monaco-editor/index.ts",
            MovesAssetUrls = true,
        },

        // The worker script that the code editor starts for TypeScript and JavaScript.
        new()
        {
            Path = "/umbraco/backoffice/external/monaco-editor/tsMode-????????.js",
            Source = "node_modules/monaco-editor/esm/vs/language/typescript/workerManager.js",
            MovesAssetUrls = true,
        },

        // The dark and high-contrast theme stylesheets.
        new()
        {
            Path = "/umbraco/backoffice/packages/core/manifests.js",
            MovesAssetUrls = true,
        },

        // The backoffice stylesheet loads the code editor's icon font from "/umbraco/backoffice/assets/". A relative
        // URL resolves next to the stylesheet, which the browser already loads from the site's assets path.
        new()
        {
            Path = "/umbraco/backoffice/css/umb-css.css",
            Edits =
            [
                new()
                {
                    Find = """
                        url(/umbraco/backoffice/assets/
                        """,
                    Replace = """
                        url(../assets/
                        """,
                    Count = AssetEdit.Every,
                },
            ],
        },
    ];
}
