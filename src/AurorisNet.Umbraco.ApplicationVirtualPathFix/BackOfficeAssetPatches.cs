using System.Text.Json;
using System.Text.RegularExpressions;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix;

/// <summary>Matches known bundles independently of their build hashes and patches their source.</summary>
internal static partial class BackOfficeAssetPatches
{
    internal const string AssetsRoot = "/umbraco/backoffice";

    // These modules only need their quoted asset URLs moved under the configured assets root.
    // Keep the filenames in those URLs unchanged, including Monaco's worker hashes.
    private static readonly HashSet<string> AssetUrlModules = new(StringComparer.OrdinalIgnoreCase)
    {
        "/apps/backoffice/components/backoffice-header-logo.element.js",
        "/apps/app/app-error.element.js",
        "/apps/installer/shared/layout/installer-layout.element.js",
        "/packages/block/data-path-generator-for-element-data.function-????????.js",
        "/packages/block/property-editor-ui-block-grid-layout-stylesheet.element-????????.js",
        "/packages/tiptap/input-tiptap.element-????????.js",
        "/packages/core/manifests.js",
        "/external/monaco-editor/rust-????????.js",
        "/external/monaco-editor/jsonMode-????????.js",
        "/external/monaco-editor/htmlMode-????????.js",
        "/external/monaco-editor/tsMode-????????.js",
    };

    // Other source changes remain explicit and are checked against the installed assets. Placeholders
    // carry the identifiers of Umbraco's TypeScript sources, which minification replaces.
    private static readonly Dictionary<string, Edit[]> CodePatches = new(StringComparer.OrdinalIgnoreCase)
    {
        ["/css/umb-css.css"] =
        [
            new("url(/umbraco/backoffice/assets/", "url(../assets/", Edit.Every),
        ],
        ["/packages/block/property-editor-ui-block-grid.element-????????.js"] =
        [
            new("new URL(@transformServerPathToClientPath(@layoutStylesheet), this.@serverUrl).href",
                "new URL(@transformServerPathToClientPath(@layoutStylesheet).replace(/^\\/(?!\\/)/, \"\"), this.@serverUrl + \"/\").href"),
        ],
        ["/packages/media/input-upload-field.element-????????.js"] =
        [
            new("this.src.startsWith(\"/\") ? `${this._serverUrl}${this.src}` : this.src",
                "this.src.startsWith(\"/\") ? new URL(this.src, this._serverUrl || location.origin).href : this.src"),
            new("@fullUrl = @isBlob ? @src : this._serverUrl + @src",
                "@fullUrl = @isBlob ? @src : new URL(@src, this._serverUrl || location.origin).href"),
        ],
        ["/packages/preview/index.js"] =
        [
            new("@urlInfo.url.startsWith(\"/\") ? `${this.@serverUrl}${@urlInfo.url}` : @urlInfo.url",
                "@urlInfo.url.startsWith(\"/\") ? new URL(@urlInfo.url, this.@serverUrl || location.origin).href : @urlInfo.url"),
            new("const @url = new URL(@unique, @host)", "const @url = new URL(@unique, @host.endsWith(\"/\") ? @host : @host + \"/\")"),
        ],
    };

    // @prefix and @backOfficePath are the configured paths as JavaScript strings.
    private static readonly Edit[] LoginScriptEdits =
    [
        new("new Request(\"/umbraco/", "new Request(@prefix + \"/umbraco/", Edit.Every),
        new("const @serverUrl = window.location.origin, @serverConnection = new @UmbServerConnection(@host, @serverUrl);",
            "const @serverUrl = window.location.origin + @prefix, @serverConnection = new @UmbServerConnection(@host, @serverUrl);"),
        new("backofficePath: \"/umbraco\"", "backofficePath: @backOfficePath"),
    ];

    private static readonly Edit WebsiteStylesEdit = new("url(/umbraco/website/", "url(", Edit.Every);

    internal static IEnumerable<string> Modules => AssetUrlModules.Concat(CodePatches.Keys);

    // Preserve the distinction between stable files and hashed chunks sharing the same stem.
    internal static string NormalizeModulePath(string path) => BundleHash().Replace(path, "-????????");

    internal static string PatchModule(string module, string source, string assetsPath, Action<string, string> unapplied)
    {
        if (AssetUrlModules.Contains(module))
        {
            // The replacement is inserted into JS strings and HTML template attributes. Encode
            // delimiters as URL characters so unusual application paths remain valid in both.
            var root = new PathString(assetsPath).ToUriComponent().Replace("'", "%27", StringComparison.Ordinal)
                .Replace("`", "%60", StringComparison.Ordinal).Replace("&", "%26", StringComparison.Ordinal) + "/";
            var matched = false;
            var result = QuotedAssetRoot().Replace(source, match =>
            {
                matched = true;
                return match.Value[..1] + root;
            });
            if (!matched) unapplied(module, "no longer contains " + QuotedAssetRoot());
            return result;
        }

        foreach (var edit in CodePatches[module])
            source = Apply(source, module, edit, unapplied);
        return source;
    }

    internal static string PatchLoginScript(string source, string pathBase, string backOfficePath, Action<string, string> unapplied)
    {
        var prefix = JsonSerializer.Serialize(pathBase);
        var values = new Dictionary<string, string> { ["prefix"] = prefix, ["backOfficePath"] = JsonSerializer.Serialize(backOfficePath) };
        foreach (var edit in LoginScriptEdits)
            source = Apply(source, "login.js", edit, unapplied, values);
        // The slim login does not configure the shared API client for its public API calls.
        return "import { umbHttpClient } from '@umbraco-cms/backoffice/http-client';\n"
            + $"umbHttpClient.setConfig({{ baseUrl: window.location.origin + {prefix} }});\n" + source;
    }

    internal static string PatchWebsiteStyles(string source, Action<string, string> unapplied)
        => Apply(source, "nonodes.css", WebsiteStylesEdit, unapplied);

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

        // Expand placeholders with an evaluator: a .NET replacement string would treat "${" in JavaScript as a substitution.
        return edit.Pattern.Replace(source, match => Placeholder().Replace(edit.Replace, placeholder =>
        {
            var name = placeholder.Groups[1].Value;
            return match.Groups[name] is { Success: true } group ? group.Value
                : values?.GetValueOrDefault(name) ?? throw new InvalidOperationException($"{edit.Find} does not define {placeholder.Value}.");
        }));
    }

    /// <summary>Replaces code of a known shape when it occurs the expected number of times.</summary>
    /// <remarks>
    /// Umbraco's minified bundles rename variables between builds, so <c>@name</c> in <see cref="Find"/> matches
    /// one whole identifier or <c>#private</c> name, and a repeated <c>@name</c> must be the same identifier.
    /// <see cref="Replace"/> inserts those identifiers, or values supplied when the edit is applied.
    /// </remarks>
    internal sealed class Edit(string find, string replace, int count = 1)
    {
        /// <summary>Changes every match, of which there must be at least one.</summary>
        internal const int Every = 0;

        internal string Find { get; } = find;
        internal string Replace { get; } = replace;
        internal int Count { get; } = count;
        internal Regex Pattern { get; } = Compile(find);

        private static Regex Compile(string find)
        {
            var defined = new HashSet<string>(StringComparer.Ordinal);
            var pattern = Placeholder().Replace(Regex.Escape(find), match =>
            {
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
    private static partial Regex QuotedAssetRoot();

    [GeneratedRegex(@"@([A-Za-z]\w*)", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();
}
