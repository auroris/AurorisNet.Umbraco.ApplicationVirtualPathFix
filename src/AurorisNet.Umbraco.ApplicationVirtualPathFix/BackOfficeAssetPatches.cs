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

    // Other source changes remain explicit and are checked against the installed assets.
    private static readonly Dictionary<string, (string Pattern, string Replacement)[]> CodePatches = new(StringComparer.OrdinalIgnoreCase)
    {
        ["/css/umb-css.css"] =
        [
            ("url(/umbraco/backoffice/assets/", "url(../assets/"),
        ],
        ["/packages/block/property-editor-ui-block-grid.element-????????.js"] =
        [
            ("new URL(oi(o), this.#s).href", "new URL(oi(o).replace(/^\\/(?!\\/)/, \"\"), this.#s + \"/\").href"),
        ],
        ["/packages/media/input-upload-field.element-????????.js"] =
        [
            ("this.src.startsWith(\"/\") ? `${this._serverUrl}${this.src}` : this.src",
                "this.src.startsWith(\"/\") ? new URL(this.src, this._serverUrl || location.origin).href : this.src"),
            ("e = i ? t : this._serverUrl + t", "e = i ? t : new URL(t, this._serverUrl || location.origin).href"),
        ],
        ["/packages/preview/index.js"] =
        [
            ("s.url.startsWith(\"/\") ? `${this.#o}${s.url}` : s.url",
                "s.url.startsWith(\"/\") ? new URL(s.url, this.#o || location.origin).href : s.url"),
            ("const n = new URL(s, t)", "const n = new URL(s, t.endsWith(\"/\") ? t : t + \"/\")"),
        ],
    };

    internal static IEnumerable<string> Modules => AssetUrlModules.Concat(CodePatches.Keys);

    // Preserve the distinction between stable files and hashed chunks sharing the same stem.
    internal static string NormalizeModulePath(string path) => BundleHash().Replace(path, "-????????");

    internal static string PatchModule(string module, string source, string assetsPath, Action<string, string> missing)
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
            if (!matched) missing(module, QuotedAssetRoot().ToString());
            return result;
        }

        foreach (var (pattern, replacement) in CodePatches[module])
            source = Replace(source, module, pattern, replacement, missing);
        return source;
    }

    internal static string PatchLoginScript(string source, string pathBase, string backOfficePath, Action<string, string> missing)
    {
        var prefix = JsonSerializer.Serialize(pathBase);
        source = Replace(source, "login.js", "new Request(\"/umbraco/", $"new Request({prefix} + \"/umbraco/", missing);
        source = Replace(source, "login.js", "const r = window.location.origin, o = new Rt(e, r);",
            $"const r = window.location.origin + {prefix}, o = new Rt(e, r);", missing);
        source = Replace(source, "login.js", "backofficePath: \"/umbraco\"",
            $"backofficePath: {JsonSerializer.Serialize(backOfficePath)}", missing);
        // The slim login does not configure the shared API client for its public API calls.
        return "import { umbHttpClient } from '@umbraco-cms/backoffice/http-client';\n"
            + $"umbHttpClient.setConfig({{ baseUrl: window.location.origin + {prefix} }});\n" + source;
    }

    internal static string PatchWebsiteStyles(string source, Action<string, string> missing)
        => Replace(source, "nonodes.css", "url(/umbraco/website/", "url(", missing);

    private static string Replace(string source, string resource, string pattern, string replacement, Action<string, string> missing)
    {
        if (!source.Contains(pattern, StringComparison.Ordinal)) missing(resource, pattern);
        return source.Replace(pattern, replacement, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"-[A-Za-z0-9_-]{8}(?=\.js$)", RegexOptions.CultureInvariant)]
    private static partial Regex BundleHash();

    // Requiring an opening quote excludes prose such as the theme module's asset-path comment.
    [GeneratedRegex("""["'`]/umbraco/backoffice/""", RegexOptions.CultureInvariant)]
    private static partial Regex QuotedAssetRoot();
}
