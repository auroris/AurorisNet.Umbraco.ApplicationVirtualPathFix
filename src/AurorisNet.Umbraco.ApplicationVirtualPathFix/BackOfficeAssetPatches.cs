namespace AurorisNet.Umbraco.ApplicationVirtualPathFix;

/// <summary>
/// Every change the package makes to the JavaScript and CSS files that Umbraco 18.3.1 serves. Each change keeps a URL
/// inside the site's application virtual path, such as <c>/portal</c>.
/// </summary>
/// <remarks>
/// <para>
/// Umbraco builds these URLs from the site root, so under a virtual path they point outside the site. At startup,
/// <see cref="BackOfficeAssetFileProvider"/> reads each file listed here, applies its changes in memory and serves the
/// result at the file's usual URL. The files on disk stay unchanged. <see cref="AssetPatcher"/> holds the code that
/// finds the files and applies the changes.
/// </para>
/// <para>
/// <c>Path</c> names the file by its path in the web root, where <c>-????????</c> stands for the build hash in a
/// bundle's name. <c>Source</c> names a source file the bundle is built from, which the tests use to find the bundle
/// when Umbraco renames it. The changes apply in this order:
/// </para>
/// <list type="number">
/// <item><c>MovesAssetUrls</c> replaces <c>/umbraco/backoffice/</c> at the start of each quoted URL with the site's
/// cache-busted assets path, such as <c>/portal/umbraco/backoffice/{hash}/</c>. The rest of each URL stays the
/// same.</item>
/// <item>Each of the <c>Edits</c> replaces the code in <c>Find</c> with the code in <c>Replace</c>. <c>Find</c> must
/// occur exactly <c>Count</c> times, or at least once for <c>AssetEdit.Every</c>. Otherwise the edit is skipped, and
/// the startup log and the health check report it. <c>Origin</c> names the line of Umbraco's TypeScript that the edit
/// changes, and the tests use Umbraco's source maps to check that every match comes from that line.</item>
/// <item><c>Preamble</c> is code placed at the start of the first line.</item>
/// </list>
/// <para>
/// Minification renames variables between Umbraco builds, so <c>Find</c> and <c>Replace</c> write them as placeholders
/// named after Umbraco's TypeScript. <c>@serverUrl</c> matches whatever name the minifier gave <c>#serverUrl</c>, and
/// <c>Replace</c> writes the same name back. <c>@prefix</c> and <c>@backOfficePath</c> insert the site's configured
/// paths as JavaScript strings, such as <c>"/portal"</c> and <c>"/portal/umbraco"</c>. <c>@@</c> is a literal
/// <c>@</c>.
/// </para>
/// <para>
/// Each replacement changes only how a URL is built. It uses standard string and URL functions, the page's origin, the
/// preview badge's <c>path</c> attribute and the values the original code already used. The login preamble sets the
/// base URL of Umbraco's own HTTP client.
/// Replacements keep every line break, so Umbraco's source maps still point at the right lines.
/// </para>
/// </remarks>
internal static class BackOfficeAssetPatches
{
    /// <summary>The corrected files, grouped by the problem their changes solve.</summary>
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

        // The block grid's default layout stylesheet, defined in a shared chunk that the bundler names after another
        // of its modules.
        new()
        {
            Path = "/umbraco/backoffice/packages/block/data-path-generator-for-element-data.function-????????.js",
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

        // A block grid's custom layout stylesheet. The default stylesheet takes another code path, and the first group
        // of patches moves its URL.
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

        // Media previews. The server URL includes the virtual path, and so do Umbraco's media URLs, such as
        // "/portal/media/photo.jpg", so joining them repeats it. new URL resolves a path from the site root against the
        // origin alone, which keeps a single virtual path. The fallback to location.origin covers an empty server URL.
        new()
        {
            Path = "/umbraco/backoffice/packages/media/input-upload-field.element-????????.js",
            Edits =
            [
                // The image cropper's preview.
                new()
                {
                    Origin = new()
                    {
                        File = "src/packages/media/media/components/input-image-cropper/image-cropper-field.element.ts",
                        Code = "`${this._serverUrl}${this.src}`",
                    },
                    Find = """
                        this.src.startsWith("/") ? `${this._serverUrl}${this.src}` : this.src
                        """,
                    Replace = """
                        this.src.startsWith("/") ? new URL(this.src, this._serverUrl || location.origin).href : this.src
                        """,
                    Count = 1,
                },
                // The upload field's preview.
                new()
                {
                    Origin = new()
                    {
                        File = "src/packages/media/media/components/input-upload-field/input-upload-field.element.ts",
                        Code = "this._serverUrl + src",
                    },
                    Find = """
                        @fullUrl = @isBlob ? @src : this._serverUrl + @src
                        """,
                    Replace = """
                        @fullUrl = @isBlob ? @src : new URL(@src, this._serverUrl || location.origin).href
                        """,
                    Count = 1,
                },
            ],
        },

        // The preview window.
        new()
        {
            Path = "/umbraco/backoffice/packages/preview/index.js",
            Edits =
            [
                // The link to the published page. Content URLs include the virtual path, like the media URLs above, so
                // this edit resolves them the same way.
                new()
                {
                    Origin = new()
                    {
                        File = "src/packages/preview/context/preview.context.ts",
                        Code = "`${this.#serverUrl}${urlInfo.url}`",
                    },
                    Find = """
                        @urlInfo.url.startsWith("/") ? `${this.@serverUrl}${@urlInfo.url}` : @urlInfo.url
                        """,
                    Replace = """
                        @urlInfo.url.startsWith("/") ? new URL(@urlInfo.url, this.@serverUrl || location.origin).href : @urlInfo.url
                        """,
                    Count = 1,
                },
                // The address of the page being previewed. new URL replaces the last segment of its base, so a server
                // URL such as "https://example.com/portal" would lose "/portal". This edit adds a trailing slash first.
                new()
                {
                    Origin = new()
                    {
                        File = "src/packages/preview/context/preview.context.ts",
                        Code = "new URL(unique, host)",
                    },
                    Find = """
                        const @url = new URL(@unique, @host)
                        """,
                    Replace = """
                        const @url = new URL(@unique, @host.endsWith("/") ? @host : @host + "/")
                        """,
                    Count = 1,
                },
            ],
        },

        // The rich text editor, Tiptap. MovesAssetUrls moves the URL of its content stylesheet, rte-content.css.
        new()
        {
            Path = "/umbraco/backoffice/packages/tiptap/input-tiptap.element-????????.js",
            MovesAssetUrls = true,
            Edits =
            [
                // Custom stylesheets. The server reports its stylesheet root with the virtual path, "/portal/css", so a
                // stylesheet saved as "/css/rte.css" fails the startsWith check and becomes "/portal/css/css/rte.css".
                // The edit adds two variables. avpfPath is the server URL's path, "/portal", and avpfRoot is the
                // stylesheet root without it, "/css". A stylesheet that starts with avpfRoot gets only avpfPath in
                // front. The names start with avpf, short for the package's name, so they cannot clash with Umbraco's
                // minified names. The edit spans two lines and leaves the second one unchanged.
                new()
                {
                    Origin = new()
                    {
                        File = "src/packages/tiptap/contexts/resolve-stylesheet-href.function.ts",
                        Code = "new URL(relativeHref, serverUrl || window.location.origin)",
                    },
                    Find = """
                        const @relativeHref = @stylesheet.startsWith(@rootPath) ? @stylesheet : `${@rootPath}${@stylesheet}`;
                          return new URL(@relativeHref, @serverUrl || window.location.origin).href;
                        """,
                    Replace = """
                        const avpfPath = new URL(@serverUrl || window.location.origin).pathname.replace(/\/$/, ""), avpfRoot = avpfPath && @rootPath.startsWith(avpfPath + "/") ? @rootPath.slice(avpfPath.length) : "", @relativeHref = @stylesheet.startsWith(@rootPath) ? @stylesheet : avpfRoot && @stylesheet.startsWith(avpfRoot) ? avpfPath + @stylesheet : `${@rootPath}${@stylesheet}`;
                          return new URL(@relativeHref, @serverUrl || window.location.origin).href;
                        """,
                    Count = 1,
                },
            ],
        },

        // The sign-in page. Its script, the slim login app, builds Umbraco's URLs from the site root.
        new()
        {
            Path = "/umbraco/login/login.js",
            Edits =
            [
                // Requests to the sign-in API under "/umbraco/management/api/v1/security/back-office/".
                new()
                {
                    Origin = new()
                    {
                        File = "Umbraco.Web.UI.Login/src/contexts/auth.repository.ts",
                        Code = "new Request('/umbraco/management/api/v1/security/back-office/",
                    },
                    Find = """
                        new Request("/umbraco/
                        """,
                    Replace = """
                        new Request(@prefix + "/umbraco/
                        """,
                    Count = AssetEdit.Every,
                },
                // The server URL in the server context that the login app creates for Umbraco's other APIs.
                new()
                {
                    Origin = new()
                    {
                        File = "Umbraco.Web.UI.Login/src/controllers/slim-backoffice-initializer.ts",
                        Code = "const serverUrl = window.location.origin;",
                    },
                    Find = """
                        const @serverUrl = window.location.origin, @serverConnection = new @UmbServerConnection(@host, @serverUrl);
                        """,
                    Replace = """
                        const @serverUrl = window.location.origin + @prefix, @serverConnection = new @UmbServerConnection(@host, @serverUrl);
                        """,
                    Count = 1,
                },
                // The backoffice path in the same server context.
                new()
                {
                    Origin = new()
                    {
                        File = "Umbraco.Web.UI.Login/src/controllers/slim-backoffice-initializer.ts",
                        Code = "backofficePath: '/umbraco'",
                    },
                    Find = """
                        backofficePath: "/umbraco"
                        """,
                    Replace = """
                        backofficePath: @backOfficePath
                        """,
                    Count = 1,
                },
            ],
            // The login app calls Umbraco's public APIs through Umbraco's shared HTTP client, whose base URL it leaves
            // unset. The preamble imports that client from Umbraco's own module and sets its base URL to the site.
            Preamble = """
                import { umbHttpClient } from '@@umbraco-cms/backoffice/http-client'; umbHttpClient.setConfig({ baseUrl: window.location.origin + @prefix });
                """,
        },

        // The stylesheet of the page Umbraco shows while no content is published. Its fonts sit next to it in
        // "/umbraco/website/", so relative URLs find them under any virtual path.
        new()
        {
            Path = "/umbraco/website/nonodes.css",
            Edits =
            [
                new()
                {
                    Find = """
                        url(/umbraco/website/
                        """,
                    Replace = """
                        url(
                        """,
                    Count = AssetEdit.Every,
                },
            ],
        },

        // The badge Umbraco shows on pages opened in preview. Its "End preview" button sends a DELETE request to the
        // preview API at the site root. The badge element's path attribute holds the backoffice path, virtual path
        // included, and "/umbraco" stays the fallback when the attribute is missing. The script ships without a source
        // map, so the edit's origin names the script itself.
        new()
        {
            Path = "/umbraco/website/preview.js",
            Edits =
            [
                new()
                {
                    Origin = new()
                    {
                        File = "/umbraco/website/preview.js",
                        Code = "fetch(`/umbraco/management/api/v1/preview`",
                    },
                    Find = """
                        fetch(`/umbraco/management/api/v1/preview`
                        """,
                    Replace = """
                        fetch(`${this.getAttribute("path") ?? "/umbraco"}/management/api/v1/preview`
                        """,
                    Count = 1,
                },
            ],
        },
    ];
}
