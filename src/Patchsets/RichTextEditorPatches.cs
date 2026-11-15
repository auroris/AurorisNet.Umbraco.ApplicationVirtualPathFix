namespace AurorisNet.Umbraco.ApplicationVirtualPathFix;

/// <summary>
/// The edits for the rich text editor's stylesheets. Umbraco 17 and 18 share each shape of the code they change, so the
/// entries in <see cref="Umbraco17Patches"/> and <see cref="Umbraco18Patches"/> apply them to the releases with each
/// shape.
/// </summary>
/// <remarks>
/// The rich text editor is Tiptap. Each entry's MovesAssetUrls moves the URL of its content stylesheet, rte-content.css,
/// and each edit fixes custom stylesheets. The server reports its stylesheet root with the virtual path, "/portal/css",
/// so a stylesheet saved as "/css/rte.css" fails the startsWith check and becomes "/portal/css/css/rte.css". Each edit
/// adds two variables. avpfPath is the virtual path, "/portal", and avpfRoot is the stylesheet root without it, "/css".
/// A stylesheet that starts with avpfRoot gets only avpfPath in front. The names start with avpf, short for the
/// package's name, so they cannot clash with Umbraco's minified names. The code that builds the link has three shapes,
/// one edit for each.
/// </remarks>
internal static class RichTextEditorPatches
{
    /// <summary>
    /// The edit for a link built inline in a forEach, as in 17.5 and 18.0. The code has no server URL, so the edit reads
    /// the virtual path from the data-path-base attribute that the package adds to umb-app; at the root it is absent
    /// and the code behaves as before. avpfFullRoot holds the stylesheet root the private field returns.
    /// </summary>
    internal static AssetEdit[] LinkBuiltInForEach =>
    [
        new()
        {
            Origin = new()
            {
                File = "src/packages/tiptap/components/input-tiptap/input-tiptap.element.ts",
                Code = "stylesheet.startsWith('http') || stylesheet.startsWith(this.#stylesheetRootPath)",
            },
            Find = """
                const @linkHref = @stylesheet.startsWith("http") || @stylesheet.startsWith(@privateGet(this, @stylesheetRootPath)) ? @stylesheet : `${@privateGet(this, @stylesheetRootPath)}${@stylesheet}`;
                """,
            Replace = """
                const avpfFullRoot = @privateGet(this, @stylesheetRootPath), avpfPath = document.querySelector("umb-app[data-path-base]")?.dataset.pathBase ?? "", avpfRoot = avpfPath && avpfFullRoot.startsWith(avpfPath + "/") ? avpfFullRoot.slice(avpfPath.length) : "", @linkHref = @stylesheet.startsWith("http") || @stylesheet.startsWith(avpfFullRoot) ? @stylesheet : avpfRoot && @stylesheet.startsWith(avpfRoot) ? avpfPath + @stylesheet : `${avpfFullRoot}${@stylesheet}`;
                """,
            Count = 1,
        },
    ];

    /// <summary>
    /// The edit for links built inline in an arrow function passed to map, as in 17.6 and 18.1. It gives the function a
    /// body, so it can declare the same variables as <see cref="LinkBuiltInForEach"/>, and returns the same result.
    /// </summary>
    internal static AssetEdit[] LinksBuiltInMap =>
    [
        new()
        {
            Origin = new()
            {
                File = "src/packages/tiptap/components/input-tiptap/input-tiptap.element.ts",
                Code = "stylesheet.startsWith('http') || stylesheet.startsWith(this.#stylesheetRootPath)",
            },
            Find = """
                (@stylesheet) => @stylesheet.startsWith("http") || @stylesheet.startsWith(@privateGet(this, @stylesheetRootPath)) ? @stylesheet : `${@privateGet(this, @stylesheetRootPath)}${@stylesheet}`
                """,
            Replace = """
                (@stylesheet) => { const avpfFullRoot = @privateGet(this, @stylesheetRootPath), avpfPath = document.querySelector("umb-app[data-path-base]")?.dataset.pathBase ?? "", avpfRoot = avpfPath && avpfFullRoot.startsWith(avpfPath + "/") ? avpfFullRoot.slice(avpfPath.length) : ""; return @stylesheet.startsWith("http") || @stylesheet.startsWith(avpfFullRoot) ? @stylesheet : avpfRoot && @stylesheet.startsWith(avpfRoot) ? avpfPath + @stylesheet : `${avpfFullRoot}${@stylesheet}`; }
                """,
            Count = 1,
        },
    ];

    /// <summary>
    /// The edit for a link resolved by a separate function, as in 17.7 and 18.2 and later, which has the server URL.
    /// avpfPath comes from its path. The edit spans two lines and leaves the second one unchanged.
    /// </summary>
    internal static AssetEdit[] LinkResolvedByFunction =>
    [
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
    ];
}
