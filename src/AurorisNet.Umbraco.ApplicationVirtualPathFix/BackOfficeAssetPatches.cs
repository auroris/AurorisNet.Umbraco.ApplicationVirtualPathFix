using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.FileProviders;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix;

/// <summary>The Umbraco files this package corrects, matched independently of their build hashes, and their changes.</summary>
internal static partial class BackOfficeAssetPatches
{
    internal const string AssetsRoot = "/umbraco/backoffice";

    /// <summary>Stands for an eight-character build hash in a bundle's name.</summary>
    internal const string HashPattern = "-????????";

    /// <summary>
    /// Every corrected file, by its path in the web root. Placeholders carry the identifiers of Umbraco's
    /// TypeScript, which minification replaces. Each edit names the source line it changes, which the tests
    /// check through Umbraco's source maps; a renamed bundle is found again through its source.
    /// </summary>
    internal static readonly AssetPatch[] Patches =
    [
        // These only need their quoted asset URLs moved under the configured assets root. Keep the
        // filenames in those URLs unchanged, including Monaco's worker hashes.
        new("/umbraco/backoffice/apps/app/app-error.element.js") { MovesAssetUrls = true },
        new("/umbraco/backoffice/apps/backoffice/components/backoffice-header-logo.element.js") { MovesAssetUrls = true },
        new("/umbraco/backoffice/apps/installer/shared/layout/installer-layout.element.js") { MovesAssetUrls = true },
        new("/umbraco/backoffice/external/monaco-editor/htmlMode-????????.js")
            { MovesAssetUrls = true, Source = "node_modules/monaco-editor/esm/vs/language/html/workerManager.js" },
        new("/umbraco/backoffice/external/monaco-editor/jsonMode-????????.js")
            { MovesAssetUrls = true, Source = "node_modules/monaco-editor/esm/vs/language/json/workerManager.js" },
        // Monaco's main chunk, named after one of its many modules.
        new("/umbraco/backoffice/external/monaco-editor/rust-????????.js") { MovesAssetUrls = true, Source = "src/external/monaco-editor/index.ts" },
        new("/umbraco/backoffice/external/monaco-editor/tsMode-????????.js")
            { MovesAssetUrls = true, Source = "node_modules/monaco-editor/esm/vs/language/typescript/workerManager.js" },
        // A shared chunk, also named after another of its modules.
        new("/umbraco/backoffice/packages/block/data-path-generator-for-element-data.function-????????.js")
            { MovesAssetUrls = true, Source = "src/packages/block/block-grid/context/constants.ts" },
        new("/umbraco/backoffice/packages/block/property-editor-ui-block-grid-layout-stylesheet.element-????????.js")
        {
            MovesAssetUrls = true,
            Source = "src/packages/block/block-grid/property-editors/block-grid-layout-stylesheet/property-editor-ui-block-grid-layout-stylesheet.element.ts",
        },
        new("/umbraco/backoffice/packages/core/manifests.js") { MovesAssetUrls = true },

        new("/umbraco/backoffice/css/umb-css.css", new Edit("url(/umbraco/backoffice/assets/", "url(../assets/", Edit.Every)),
        // Block thumbnails are site paths, resolved like the block grid's layout stylesheet. These bundles
        // read the private server URL field through a helper, which @privateGet matches.
        new("/umbraco/backoffice/packages/block/block-catalogue-modal.token-????????.js",
            new Edit("@imgSrc = @path ? new URL(@path, @privateGet(this, @serverUrl))?.href : void 0",
                "@imgSrc = @path ? new URL(@path.replace(/^\\/(?!\\/)/, \"\"), @privateGet(this, @serverUrl) + \"/\")?.href : void 0",
                origin: new("src/packages/block/block/modals/block-catalogue/block-catalogue-modal.element.ts", "new URL(path, this.#serverUrl)"))),
        new("/umbraco/backoffice/packages/block/block-type/index.js",
            new Edit("const @url = new URL(@value, @privateGet(this, @serverUrl));",
                "const @url = new URL(@value.replace(/^\\/(?!\\/)/, \"\"), @privateGet(this, @serverUrl) + \"/\");",
                origin: new("src/packages/block/block-type/components/block-type-card/block-type-card.element.ts", "new URL(value, this.#serverUrl)"))),
        new("/umbraco/backoffice/packages/block/property-editor-ui-block-grid.element-????????.js",
            new Edit("new URL(@transformServerPathToClientPath(@layoutStylesheet), this.@serverUrl).href",
                "new URL(@transformServerPathToClientPath(@layoutStylesheet).replace(/^\\/(?!\\/)/, \"\"), this.@serverUrl + \"/\").href",
                origin: new("src/packages/block/block-grid/block-grid-manager/block-grid-manager.context.ts",
                    "new URL(transformServerPathToClientPath(layoutStylesheet), this.#serverUrl)"))),
        new("/umbraco/backoffice/packages/media/input-upload-field.element-????????.js",
            new Edit("this.src.startsWith(\"/\") ? `${this._serverUrl}${this.src}` : this.src",
                "this.src.startsWith(\"/\") ? new URL(this.src, this._serverUrl || location.origin).href : this.src",
                origin: new("src/packages/media/media/components/input-image-cropper/image-cropper-field.element.ts", "`${this._serverUrl}${this.src}`")),
            new Edit("@fullUrl = @isBlob ? @src : this._serverUrl + @src",
                "@fullUrl = @isBlob ? @src : new URL(@src, this._serverUrl || location.origin).href",
                origin: new("src/packages/media/media/components/input-upload-field/input-upload-field.element.ts", "this._serverUrl + src"))),
        new("/umbraco/backoffice/packages/preview/index.js",
            new Edit("@urlInfo.url.startsWith(\"/\") ? `${this.@serverUrl}${@urlInfo.url}` : @urlInfo.url",
                "@urlInfo.url.startsWith(\"/\") ? new URL(@urlInfo.url, this.@serverUrl || location.origin).href : @urlInfo.url",
                origin: new("src/packages/preview/context/preview.context.ts", "`${this.#serverUrl}${urlInfo.url}`")),
            new Edit("const @url = new URL(@unique, @host)", "const @url = new URL(@unique, @host.endsWith(\"/\") ? @host : @host + \"/\")",
                origin: new("src/packages/preview/context/preview.context.ts", "new URL(unique, host)"))),
        new("/umbraco/backoffice/packages/tiptap/input-tiptap.element-????????.js",
            // The server's stylesheet root includes the virtual path (/portal/css), so a stylesheet saved with the
            // root (/css/rte.css) is no longer recognised and gets the root twice. Recognise the root without it too.
            new Edit("const @relativeHref = @stylesheet.startsWith(@rootPath) ? @stylesheet : `${@rootPath}${@stylesheet}`;\n"
                    + "  return new URL(@relativeHref, @serverUrl || window.location.origin).href;",
                "const avpfPath = new URL(@serverUrl || window.location.origin).pathname.replace(/\\/$/, \"\"), "
                    + "avpfRoot = avpfPath && @rootPath.startsWith(avpfPath + \"/\") ? @rootPath.slice(avpfPath.length) : \"\", "
                    + "@relativeHref = @stylesheet.startsWith(@rootPath) ? @stylesheet : avpfRoot && @stylesheet.startsWith(avpfRoot) ? avpfPath + @stylesheet : `${@rootPath}${@stylesheet}`;\n"
                    + "  return new URL(@relativeHref, @serverUrl || window.location.origin).href;",
                origin: new("src/packages/tiptap/contexts/resolve-stylesheet-href.function.ts", "new URL(relativeHref, serverUrl || window.location.origin)")))
        {
            MovesAssetUrls = true,
        },

        new("/umbraco/login/login.js",
            new Edit("new Request(\"/umbraco/", "new Request(@prefix + \"/umbraco/", Edit.Every,
                origin: new("Umbraco.Web.UI.Login/src/contexts/auth.repository.ts", "new Request('/umbraco/management/api/v1/security/back-office/")),
            new Edit("const @serverUrl = window.location.origin, @serverConnection = new @UmbServerConnection(@host, @serverUrl);",
                "const @serverUrl = window.location.origin + @prefix, @serverConnection = new @UmbServerConnection(@host, @serverUrl);",
                origin: new("Umbraco.Web.UI.Login/src/controllers/slim-backoffice-initializer.ts", "const serverUrl = window.location.origin;")),
            new Edit("backofficePath: \"/umbraco\"", "backofficePath: @backOfficePath",
                origin: new("Umbraco.Web.UI.Login/src/controllers/slim-backoffice-initializer.ts", "backofficePath: '/umbraco'")))
        {
            // The slim login does not configure the shared API client for its public API calls.
            Preamble = "import { umbHttpClient } from '@@umbraco-cms/backoffice/http-client'; "
                + "umbHttpClient.setConfig({ baseUrl: window.location.origin + @prefix }); ",
        },
        new("/umbraco/website/nonodes.css", new Edit("url(/umbraco/website/", "url(", Edit.Every)),
        // The preview badge's path attribute is the backoffice path its script was loaded from, virtual path included.
        new("/umbraco/website/preview.js",
            new Edit("fetch(`/umbraco/management/api/v1/preview`", "fetch(`${this.getAttribute(\"path\") ?? \"/umbraco\"}/management/api/v1/preview`",
                origin: new("/umbraco/website/preview.js", "fetch(`/umbraco/management/api/v1/preview`"))),
    ];

    /// <summary>The patch for a file in the web root, if any.</summary>
    internal static AssetPatch? For(string path)
        => Array.Find(Patches, patch => string.Equals(patch.Path, NormalizePath(path), StringComparison.OrdinalIgnoreCase));

    /// <summary>The files a patch applies to, which should be exactly one.</summary>
    internal static (string Path, IFileInfo File)[] Locate(IFileProvider files, AssetPatch patch)
    {
        if (!patch.Path.Contains(HashPattern, StringComparison.Ordinal))
            return files.GetFileInfo(patch.Path) is { Exists: true, IsDirectory: false } file ? [(patch.Path, file)] : [];
        var directory = patch.Path[..patch.Path.LastIndexOf('/')];
        return files.GetDirectoryContents(directory)
            .Where(file => !file.IsDirectory && string.Equals(NormalizePath(directory + "/" + file.Name), patch.Path, StringComparison.OrdinalIgnoreCase))
            .Select(file => (directory + "/" + file.Name, file)).ToArray();
    }

    // Preserve the distinction between stable files and hashed chunks sharing the same stem.
    internal static string NormalizePath(string path) => BundleHash().Replace(path, HashPattern);

    /// <summary>Applies a patch, passing each change that does not apply to <paramref name="unapplied"/>.</summary>
    internal static string Patch(AssetPatch patch, string source, PatchContext context, Action<string, string> unapplied)
    {
        if (patch.MovesAssetUrls)
        {
            // The replacement is inserted into JS strings and HTML template attributes. Encode
            // delimiters as URL characters so unusual application paths remain valid in both.
            var root = new PathString(context.AssetsPath).ToUriComponent().Replace("'", "%27", StringComparison.Ordinal)
                .Replace("`", "%60", StringComparison.Ordinal).Replace("&", "%26", StringComparison.Ordinal) + "/";
            var matched = false;
            source = QuotedAssetRoot().Replace(source, match =>
            {
                matched = true;
                return match.Value[..1] + root;
            });
            if (!matched) unapplied(patch.Path, "no longer contains " + QuotedAssetRoot());
        }

        // @prefix and @backOfficePath insert the configured paths as JavaScript strings.
        var values = new Dictionary<string, string>
        {
            ["prefix"] = JsonSerializer.Serialize(context.PathBase),
            ["backOfficePath"] = JsonSerializer.Serialize(context.BackOfficePath),
        };
        foreach (var edit in patch.Edits)
            source = Apply(source, patch.Path, edit, unapplied, values);
        return patch.Preamble is { } preamble ? Expand(preamble, null, values, preamble) + source : source;
    }

    /// <summary>
    /// Applies <paramref name="edit"/> when it finds the number of matches it expects. Otherwise leaves the source
    /// unchanged and passes <paramref name="unapplied"/> a reason that completes "Umbraco's {resource} ...".
    /// </summary>
    internal static string Apply(string source, string resource, Edit edit, Action<string, string> unapplied,
        IReadOnlyDictionary<string, string>? values = null)
    {
        var found = edit.Pattern.Count(source);
        if (found == 0 || (edit.Count != Edit.Every && found != edit.Count))
        {
            unapplied(resource, found == 0
                ? "no longer contains " + edit.Find
                : $"matches {edit.Find} {found} times instead of {edit.Count}");
            return source;
        }
        return edit.Pattern.Replace(source, match => Expand(edit.Replace, match, values, edit.Find));
    }

    // Expands with an evaluator: a .NET replacement string would treat "${" in JavaScript as a substitution.
    private static string Expand(string template, Match? match, IReadOnlyDictionary<string, string>? values, string pattern)
        => Placeholder().Replace(template, placeholder =>
        {
            if (placeholder.Value == "@@") return "@";
            var name = placeholder.Groups[1].Value;
            return match?.Groups[name] is { Success: true } group ? group.Value
                : values?.GetValueOrDefault(name) ?? throw new InvalidOperationException($"{pattern} does not define {placeholder.Value}.");
        });

    /// <summary>A file Umbraco serves, by its path in the web root, and the changes it needs.</summary>
    internal sealed class AssetPatch(string path, params Edit[] edits)
    {
        internal string Path { get; } = path;
        internal Edit[] Edits { get; } = edits;

        /// <summary>Moves quoted <c>/umbraco/backoffice/</c> URLs under the configured assets root, before the edits.</summary>
        internal bool MovesAssetUrls { get; init; }

        /// <summary>Inserted at the start of the first line, so the file keeps its line numbers.</summary>
        internal string? Preamble { get; init; }

        /// <summary>A source the bundle is built from, which finds the bundle again when Umbraco renames it.</summary>
        internal string? Source { get; init; }

        public override string ToString() => Path;
    }

    /// <summary>The site's paths that patched files refer to.</summary>
    internal sealed record PatchContext(string PathBase, string BackOfficePath, string AssetsPath);

    /// <summary>A line of Umbraco's source, by the file a source map names and a fragment of its code.</summary>
    internal sealed record SourceLine(string File, string Code);

    /// <summary>Replaces code of a known shape when it occurs the expected number of times.</summary>
    /// <remarks>
    /// Umbraco's minified bundles rename variables between builds, so <c>@name</c> in <see cref="Find"/> matches
    /// one whole identifier or <c>#private</c> name, and a repeated <c>@name</c> must be the same identifier.
    /// <see cref="Replace"/> inserts those identifiers, or values supplied when the edit is applied. <c>@@</c>
    /// stands for a literal <c>@</c>, as in <c>@@umbraco-cms</c>.
    /// </remarks>
    internal sealed class Edit(string find, string replace, int count = 1, SourceLine? origin = null)
    {
        /// <summary>Changes every match, of which there must be at least one.</summary>
        internal const int Every = 0;

        internal string Find { get; } = find;
        internal string Replace { get; } = replace;
        internal int Count { get; } = count;

        /// <summary>The line of Umbraco's source this edit changes.</summary>
        internal SourceLine? Origin { get; } = origin;

        internal Regex Pattern { get; } = Compile(find);

        public override string ToString() => Find;

        private static Regex Compile(string find)
        {
            var defined = new HashSet<string>(StringComparer.Ordinal);
            var pattern = Placeholder().Replace(Regex.Escape(find), match =>
            {
                if (match.Value == "@@") return "@";
                var name = match.Groups[1].Value;
                // Never match part of a longer identifier. One that opens the pattern is not a property either.
                var before = match.Index == 0 ? @"(?<![\w$#.])" : @"(?<![\w$#])";
                return before + (defined.Add(name) ? $@"(?<{name}>#?[A-Za-z_$][\w$]*)" : $@"\k<{name}>") + @"(?![\w$])";
            });
            return new Regex(pattern, RegexOptions.CultureInvariant);
        }
    }

    [GeneratedRegex(@"-[A-Za-z0-9_-]{8}(?=\.js$)", RegexOptions.CultureInvariant)]
    private static partial Regex BundleHash();

    // Requiring an opening quote excludes prose such as the theme module's asset-path comment.
    [GeneratedRegex("""["'`]/umbraco/backoffice/""", RegexOptions.CultureInvariant)]
    internal static partial Regex QuotedAssetRoot();

    [GeneratedRegex(@"@@|@([A-Za-z]\w*)", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();
}
