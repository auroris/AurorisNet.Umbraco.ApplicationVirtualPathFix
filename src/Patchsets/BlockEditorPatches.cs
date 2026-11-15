namespace AurorisNet.Umbraco.ApplicationVirtualPathFix;

/// <summary>The block grid's layout stylesheets and the thumbnails of block types.</summary>
internal static class BlockEditorPatches
{
    /// <summary>The entries for these files, in the order the patcher reads them.</summary>
    internal static readonly AssetPatch[] Patches =
    [
        // The default layout stylesheet's URL is in a chunk whose name changes between releases, so each major version's
        // entries move it, in Umbraco17Patches and Umbraco18Patches.

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

        // A block grid's custom layout stylesheet. The default stylesheet takes another code path, which each major
        // version's entries handle.
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
