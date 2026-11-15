namespace AurorisNet.Umbraco.ApplicationVirtualPathFix;

/// <summary>The rich text editor's stylesheets.</summary>
internal static class RichTextEditorPatches
{
    /// <summary>The entries for these files, in the order the patcher reads them.</summary>
    internal static readonly AssetPatch[] Patches =
    [
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
    ];
}
