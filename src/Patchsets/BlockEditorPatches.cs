namespace AurorisNet.Umbraco.ApplicationVirtualPathFix;

/// <summary>The block grid's layout stylesheets and the thumbnails of block types.</summary>
internal static class BlockEditorPatches
{
    /// <summary>The entries for these files, in the order the patcher reads them.</summary>
    internal static readonly AssetPatch[] Patches =
    [
        // The block grid's default layout stylesheet, "/umbraco/backoffice/css/umbraco-blockgridlayout.css". The bundler
        // defines it in a shared chunk named after one of the chunk's modules, and that name changes between releases.
        // Each name has an entry for the Umbraco 17 releases that use it and one for the 18 releases.

        // In 17.5, 17.6, 18.0 and 18.1 the chunk is one of two named constants-????????.js, so Contains picks the one
        // with the stylesheet.
        new()
        {
            Path = "/umbraco/backoffice/packages/block/constants-????????.js",
            From = "17.5.0",
            Before = "17.7.0",
            Contains = "umbraco-blockgridlayout.css",
            Source = "src/packages/block/block-grid/context/constants.ts",
            MovesAssetUrls = true,
        },
        new()
        {
            Path = "/umbraco/backoffice/packages/block/constants-????????.js",
            From = "18.0.0",
            Before = "18.2.0",
            Contains = "umbraco-blockgridlayout.css",
            Source = "src/packages/block/block-grid/context/constants.ts",
            MovesAssetUrls = true,
        },

        // 17.7 and 18.2.
        new()
        {
            Path = "/umbraco/backoffice/packages/block/data-path-element-data-query.function-????????.js",
            From = "17.7.0",
            Before = "17.8.0",
            Source = "src/packages/block/block-grid/context/constants.ts",
            MovesAssetUrls = true,
        },
        new()
        {
            Path = "/umbraco/backoffice/packages/block/data-path-element-data-query.function-????????.js",
            From = "18.2.0",
            Before = "18.3.0",
            Source = "src/packages/block/block-grid/context/constants.ts",
            MovesAssetUrls = true,
        },

        // 17.8 and 18.3 and later.
        new()
        {
            Path = "/umbraco/backoffice/packages/block/data-path-generator-for-element-data.function-????????.js",
            From = "17.8.0",
            Before = "18.0.0",
            Source = "src/packages/block/block-grid/context/constants.ts",
            MovesAssetUrls = true,
        },
        new()
        {
            Path = "/umbraco/backoffice/packages/block/data-path-generator-for-element-data.function-????????.js",
            From = "18.3.0",
            Source = "src/packages/block/block-grid/context/constants.ts",
            MovesAssetUrls = true,
        },

        // The link to the default layout stylesheet in a block grid's settings.
        new()
        {
            Path = "/umbraco/backoffice/packages/block/property-editor-ui-block-grid-layout-stylesheet.element-????????.js",
            Source = "src/packages/block/block-grid/property-editors/block-grid-layout-stylesheet/property-editor-ui-block-grid-layout-stylesheet.element.ts",
            MovesAssetUrls = true,
        },

        // Site paths. Block thumbnails and custom block grid stylesheets are saved as paths from the site root, such as
        // "/media/thumbnail.png", without the virtual path. new URL resolves such a path against the origin alone,
        // which drops the virtual path. Each edit removes the leading slash and resolves the path against the server
        // URL with a trailing slash, so it stays under the virtual path. Absolute and protocol-relative URLs resolve as
        // before.

        // Thumbnails in the dialog where editors choose a block to add. This bundle reads the private #serverUrl field
        // through a helper function, which @privateGet matches.
        new()
        {
            Path = "/umbraco/backoffice/packages/block/block-catalogue-modal.token-????????.js",
            Edits =
            [
                new()
                {
                    Origin = new()
                    {
                        File = "src/packages/block/block/modals/block-catalogue/block-catalogue-modal.element.ts",
                        Code = "new URL(path, this.#serverUrl)",
                    },
                    Find = """
                        @imgSrc = @path ? new URL(@path, @privateGet(this, @serverUrl))?.href : void 0
                        """,
                    Replace = """
                        @imgSrc = @path ? new URL(@path.replace(/^\/(?!\/)/, ""), @privateGet(this, @serverUrl) + "/")?.href : void 0
                        """,
                    Count = 1,
                },
            ],
        },

        // Thumbnails on the block cards in a block editor's settings. This bundle also reads #serverUrl through
        // @privateGet.
        new()
        {
            Path = "/umbraco/backoffice/packages/block/block-type/index.js",
            Edits =
            [
                new()
                {
                    Origin = new()
                    {
                        File = "src/packages/block/block-type/components/block-type-card/block-type-card.element.ts",
                        Code = "new URL(value, this.#serverUrl)",
                    },
                    Find = """
                        const @url = new URL(@value, @privateGet(this, @serverUrl));
                        """,
                    Replace = """
                        const @url = new URL(@value.replace(/^\/(?!\/)/, ""), @privateGet(this, @serverUrl) + "/");
                        """,
                    Count = 1,
                },
            ],
        },

        // A block grid's custom layout stylesheet. The default stylesheet takes another code path, and the entries at
        // the top of this list move its URL.
        new()
        {
            Path = "/umbraco/backoffice/packages/block/property-editor-ui-block-grid.element-????????.js",
            Edits =
            [
                new()
                {
                    Origin = new()
                    {
                        File = "src/packages/block/block-grid/block-grid-manager/block-grid-manager.context.ts",
                        Code = "new URL(transformServerPathToClientPath(layoutStylesheet), this.#serverUrl)",
                    },
                    Find = """
                        new URL(@transformServerPathToClientPath(@layoutStylesheet), this.@serverUrl).href
                        """,
                    Replace = """
                        new URL(@transformServerPathToClientPath(@layoutStylesheet).replace(/^\/(?!\/)/, ""), this.@serverUrl + "/").href
                        """,
                    Count = 1,
                },
            ],
        },
    ];
}
