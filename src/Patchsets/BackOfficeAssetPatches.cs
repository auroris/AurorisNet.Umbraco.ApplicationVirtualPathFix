namespace AurorisNet.Umbraco.ApplicationVirtualPathFix;

/// <summary>
/// Every change the package makes to the JavaScript and CSS files that Umbraco serves. Each change keeps a URL inside the
/// site's application virtual path, such as <c>/portal</c>.
/// </summary>
/// <remarks>
/// <para>
/// Umbraco builds these URLs from the site root, so under a virtual path they point outside the site. At startup,
/// <see cref="BackOfficeAssetFileProvider"/> takes the entries that apply to the site's Umbraco release, reads each file
/// they name, applies their changes in memory and serves the result at the file's usual URL. It reads the entries top to
/// bottom, so a file named by several entries gets their changes in that order. The files on disk stay unchanged.
/// <see cref="AssetPatcher"/> holds the code that finds the files and applies the changes.
/// </para>
/// <para>
/// Entries that apply to every supported release live in this folder, one file per area of Umbraco. Entries for one
/// major version's releases live in the subfolder named after it, such as <c>Umbraco18</c>, and stay within that major
/// version, so supporting a new major version adds a folder rather than changing these files. <see cref="Patches"/>
/// joins the area files in the order it lists them, then each major version's entries, oldest first.
/// <c>Path</c> names a file by its path in the web root, where <c>-????????</c> stands for the build hash in a bundle's
/// name. <c>From</c> and <c>Before</c> limit an entry to a range of Umbraco releases: it applies from <c>From</c> up to,
/// but not including, <c>Before</c>. An entry without them applies to every supported release. Umbraco 17's later
/// releases share code with 18's: 17.5 with 18.0, 17.6 with 18.1, 17.7 with 18.2, and 17.8 with 18.3. Each such entry
/// in one major version's folder has a counterpart in the other's with the same changes, which the area files define
/// once. <c>Contains</c>, when set, is text the file must contain, which picks the right
/// bundle when several share the name. <c>Source</c> names a source file the bundle is built from, which the
/// tests use to find the bundle when Umbraco renames it. An entry's changes apply in this order:
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
/// base URL of Umbraco's own HTTP client. Replacements keep every line break, so Umbraco's source maps still point at
/// the right lines.
/// </para>
/// </remarks>
internal static class BackOfficeAssetPatches
{
    /// <summary>Every entry, in the order the patcher reads them: the area files, then each major version's own.</summary>
    internal static readonly AssetPatch[] Patches =
    [
        .. AssetUrlPatches.Patches,
        .. BlockEditorPatches.Patches,
        .. MediaPatches.Patches,
        .. PreviewPatches.Patches,
        .. SignInPatches.Patches,
        .. WebsitePatches.Patches,
        .. UmbracoMajor.All.SelectMany(major => major.Patches),
    ];
}
