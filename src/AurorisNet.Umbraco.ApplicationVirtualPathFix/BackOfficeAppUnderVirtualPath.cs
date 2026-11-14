using System.Collections.Concurrent;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Web.Common.Hosting;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix;

/// <summary>
/// Supplies the backoffice's virtual path and session hint, and corrects the slim login's URLs.
/// </summary>
/// <remarks>
/// The SPA builds its management API, OAuth and post-login URLs from <c>window.location.origin</c>
/// plus a hard-coded <c>/umbraco</c>, so under a path base every call leaves the prefix. The
/// <c>umb-app</c> element accepts <c>server-url</c> and <c>backoffice-path</c> attributes that
/// change both, but the page that renders it is a precompiled view in Umbraco.Cms.StaticAssets and
/// this project's Razor setup does not override those, so the attributes are added to the served
/// HTML instead. Sites at the root keep Umbraco's own browser-side defaults. Umbraco 18.3's slim
/// login also hard-codes the root URLs; its served script is patched under a virtual path. The auth
/// module wrapper uses a session hint to avoid trying to refresh before the first token exchange.
/// The cookie itself stays HTTP-only.
/// <para>
/// Only the responses the package rewrites are buffered: navigations to backoffice and login pages
/// (requests that accept HTML), the slim login bundle, the few backoffice modules with root-absolute
/// URLs (the header logo and the block grid's layout stylesheet) and the stylesheet of Umbraco's
/// built-in website pages (Page Not Found, No Content). Everything else
/// under <c>/umbraco</c>, including SignalR's Server-Sent Events and long-polling transports and
/// static files, streams through untouched, and empty responses such as 304 are never written to.
/// </para>
/// </remarks>
internal static class BackOfficeAppUnderVirtualPath
{
    /// <summary>Tells the auth module wrapper to run Umbraco's normal session restore.</summary>
    internal const string RestoreSessionAttribute = "data-restore-session";

    private static readonly PathString BackOfficeRoot = "/umbraco";
    private static readonly PathString LoginScript = "/umbraco/login/login.js";
    private static readonly PathString WebsiteStyles = "/umbraco/website/nonodes.css";
    internal static readonly string BuildVersion = typeof(BackOfficeAppUnderVirtualPath).Assembly.ManifestModule.ModuleVersionId.ToString("N");
    private static readonly PathString[] Untouched = ["/umbraco/backoffice", "/umbraco/management", "/umbraco/delivery"];

    /// <summary>
    /// Root-absolute URLs in Umbraco 18.3.1's backoffice modules, keyed by each module's path under the
    /// backoffice assets. Relative URLs resolve from the served module, keeping the prefix and asset hash.
    /// </summary>
    private static readonly Dictionary<string, (string Pattern, string Replacement)[]> ModulePatches = new(StringComparer.Ordinal)
    {
        // The logo in the header's system-information popover.
        ["/apps/backoffice/components/backoffice-header-logo.element.js"] =
        [
            ("src=\"/umbraco/backoffice/assets/umbraco-logo.svg\"",
                "src=${new URL(\"../../../assets/umbraco-logo.svg\", import.meta.url).href}"),
        ],
        // The block grid's default layout stylesheet.
        ["/packages/block/data-path-generator-for-element-data.function-Ck1qUJio.js"] =
        [
            ("const c = \"/umbraco/backoffice/css/umbraco-blockgridlayout.css\"",
                "const c = new URL(\"../../css/umbraco-blockgridlayout.css\", import.meta.url).pathname"),
        ],
        // The block grid layout-stylesheet setting's link to that default.
        ["/packages/block/property-editor-ui-block-grid-layout-stylesheet.element-Kdskf0vB.js"] =
        [
            ("href=\"/umbraco/backoffice/css/umbraco-blockgridlayout.css\"",
                "href=${new URL(\"../../css/umbraco-blockgridlayout.css\", import.meta.url).pathname}"),
        ],
        // A custom layout stylesheet's site path, which must resolve under the server URL's path too.
        ["/packages/block/property-editor-ui-block-grid.element-_WZbZIkq.js"] =
        [
            ("new URL(oi(o), this.#s).href", "new URL(oi(o).replace(/^\\/(?!\\/)/, \"\"), this.#s + \"/\").href"),
        ],
        // Media and published-page URLs from the server already include the virtual path, so they
        // resolve against the server URL's origin instead of being appended to the whole server URL.
        ["/packages/media/input-upload-field.element-WuB12A7G.js"] =
        [
            ("this.src.startsWith(\"/\") ? `${this._serverUrl}${this.src}` : this.src",
                "this.src.startsWith(\"/\") ? new URL(this.src, this._serverUrl || location.origin).href : this.src"),
            ("e = i ? t : this._serverUrl + t", "e = i ? t : new URL(t, this._serverUrl || location.origin).href"),
        ],
        ["/packages/preview/index.js"] =
        [
            ("s.url.startsWith(\"/\") ? `${this.#o}${s.url}` : s.url",
                "s.url.startsWith(\"/\") ? new URL(s.url, this.#o || location.origin).href : s.url"),
            // The preview frame resolves the document key against the server URL, which only keeps
            // the server URL's path when it ends with a slash.
            ("const n = new URL(s, t)", "const n = new URL(s, t.endsWith(\"/\") ? t : t + \"/\")"),
        ],
        ["/packages/tiptap/input-tiptap.element-C2HEaB-_.js"] =
        [
            ("\"/umbraco/backoffice/css/rte-content.css\"",
                "new URL(\"../../css/rte-content.css\", import.meta.url).pathname"),
        ],
        ["/packages/core/manifests.js"] =
        [
            ("css: \"/umbraco/backoffice/css/dark.css\"",
                "css: new URL(\"../../css/dark.css\", import.meta.url).pathname"),
            ("css: \"/umbraco/backoffice/css/high-contrast.css\"",
                "css: new URL(\"../../css/high-contrast.css\", import.meta.url).pathname"),
        ],
        // Vite's worker constructors contain root URLs, independent of the import map.
        ["/external/monaco-editor/rust-CWMmcrOS.js"] =
        [
            WorkerPatch("editor.worker-LB_7Ltyq.js"),
            WorkerPatch("json.worker-DQOG4ivs.js"),
            WorkerPatch("css.worker-B3BImrxk.js"),
            WorkerPatch("html.worker-Cq-Lc34v.js"),
            WorkerPatch("ts.worker-DMY5tkUi.js"),
        ],
        // These language modules already use new URL(..., import.meta.url).
        ["/external/monaco-editor/jsonMode-C77h2I9n.js"] =
            [("\"/umbraco/backoffice/external/monaco-editor/assets/", "\"./assets/")],
        ["/external/monaco-editor/htmlMode-BQH4tjQN.js"] =
            [("\"/umbraco/backoffice/external/monaco-editor/assets/", "\"./assets/")],
        ["/external/monaco-editor/tsMode-rn8r1OtC.js"] =
            [("\"/umbraco/backoffice/external/monaco-editor/assets/", "\"./assets/")],
    };

    private static (string Pattern, string Replacement) WorkerPatch(string file)
        => ($"\"/umbraco/backoffice/external/monaco-editor/assets/{file}\"", $"new URL(\"./assets/{file}\", import.meta.url)");

    private enum Target
    {
        None,
        Page,
        LoginScript,
        Module,
        WebsiteStyles,
    }

    /// <summary>Register after <c>UsePathBase</c> and before <c>UseUmbraco</c>.</summary>
    public static IApplicationBuilder UseBackOfficeAppUnderVirtualPath(this IApplicationBuilder app)
    {
        var services = app.ApplicationServices;
        var paths = services.GetRequiredService<IBackOfficePathGenerator>();
        var backOfficePath = paths.BackOfficePath;
        var basicAuthPath = new PathString(backOfficePath + "/basic-auth");
        // After UseUmbracoPathsUnderPathBase, hashed requests carry the virtual directory in their path.
        var modules = new Dictionary<PathString, string>();
        foreach (var module in ModulePatches.Keys)
        {
            modules[new PathString(paths.BackOfficeAssetsPath + module)] = module;
            modules[new PathString("/umbraco/backoffice" + module)] = module;
        }

#pragma warning disable CS0618 // Umbraco 18's token handler still uses these options; the replacement is scheduled for 19.
        var cookies = services.GetRequiredService<IOptions<BackOfficeTokenCookieSettings>>().Value;
#pragma warning restore CS0618
        var global = services.GetRequiredService<IOptions<GlobalSettings>>().Value;
        var strictCookies = string.Equals(cookies.SameSite, "Strict", StringComparison.OrdinalIgnoreCase);
        var missingPatches = new MissingPatchLog(services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("AurorisNet.Umbraco.ApplicationVirtualPathFix"));

        return app.Use(async (context, next) =>
        {
            var request = context.Request;
            var target = Classify(request, modules, basicAuthPath, out var module);
            if (target == Target.None)
            {
                await next(context);
                return;
            }

            var originalBody = context.Response.Body;
            var acceptEncoding = request.Headers.AcceptEncoding;
            using var buffer = new MemoryStream();
            context.Response.Body = buffer;
            // A later response-compression middleware must leave the buffered bytes as text.
            // Compression registered before UseUmbraco still compresses the patched response.
            request.Headers.Remove("Accept-Encoding");
            try
            {
                await next(context);
            }
            finally
            {
                context.Response.Body = originalBody;
                if (acceptEncoding.Count > 0)
                    request.Headers.AcceptEncoding = acceptEncoding;
            }

            // 304 and 204 responses must not be written to at all, and leaving any other empty
            // response unstarted lets outer status-code middleware still render it.
            if (buffer.Length == 0)
                return;

            byte[] bytes = buffer.ToArray();
            // Custom handlers may serve precompressed files regardless of Accept-Encoding.
            // Never decode those binary bytes as UTF-8.
            byte[]? patched = context.Response.StatusCode != StatusCodes.Status200OK
                || context.Response.Headers.ContentEncoding.Count > 0 ? null : target switch
            {
                Target.Page => PatchPage(context, bytes),
                Target.LoginScript => PatchLoginScript(request, bytes),
                Target.Module => PatchModule(module!, bytes),
                Target.WebsiteStyles => PatchWebsiteStyles(bytes),
                _ => null,
            };
            if (patched is not null)
            {
                bytes = patched;
                if (context.Response.ContentLength is not null)
                    context.Response.ContentLength = bytes.Length;
            }

            await originalBody.WriteAsync(bytes, context.RequestAborted);
        });

        byte[]? PatchPage(HttpContext context, byte[] bytes)
        {
            if (context.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) != true)
                return null;

            var request = context.Request;
            var html = Encoding.UTF8.GetString(bytes);
            if (request.PathBase.HasValue && request.Path.StartsWithSegments(basicAuthPath))
            {
                // MVC's basic-auth routes contain the virtual path already. Correct their
                // generated form actions while preserving PathBase for Url.Content assets.
                var doubled = HtmlEncoder.Default.Encode(request.PathBase + basicAuthPath);
                var corrected = HtmlEncoder.Default.Encode(basicAuthPath.Value!);
                html = html.Replace("\"" + doubled, "\"" + corrected, StringComparison.Ordinal);
            }
            if (html.Contains("<umb-app ", StringComparison.Ordinal))
            {
                var attributes = new StringBuilder();
                var cookieName = (global.UseHttps || request.IsHttps ? "__Host-" : "") + "umbRefreshToken" + cookies.SiteName;
                if (ShouldRestoreSession(request, cookieName, strictCookies))
                    attributes.Append(RestoreSessionAttribute).Append(' ');
                if (request.PathBase.HasValue)
                {
                    var serverUrl = $"{request.Scheme}://{request.Host}{request.PathBase}";
                    attributes.Append($"server-url=\"{HtmlEncoder.Default.Encode(serverUrl)}\" ");
                    attributes.Append($"backoffice-path=\"{HtmlEncoder.Default.Encode(backOfficePath)}\" ");
                }

                html = html.Replace("<umb-app ", "<umb-app " + attributes, StringComparison.Ordinal);
                // The session hint is specific to this request, including immediately after sign-in.
                context.Response.Headers.CacheControl = "no-store";
            }

            if (request.PathBase.HasValue)
            {
                // Razor versions the original asset. Also invalidate it when the package's patch changes.
                html = html.Replace(LoginScript + "?", LoginScript + $"?avpfix={BuildVersion}&amp;", StringComparison.Ordinal);
            }

            return Encoding.UTF8.GetBytes(html);
        }

        byte[] PatchLoginScript(HttpRequest request, byte[] bytes)
        {
            var prefix = JsonSerializer.Serialize(request.PathBase.Value);
            // These are the three root assumptions in Umbraco 18.3's slim login bundle.
            // Scope the replacements to this asset, leaving return-URL origin checks intact.
            var script = Encoding.UTF8.GetString(bytes);
            script = missingPatches.Replace(script, "login.js", "new Request(\"/umbraco/", $"new Request({prefix} + \"/umbraco/");
            script = missingPatches.Replace(script, "login.js", "const r = window.location.origin, o = new Rt(e, r);",
                $"const r = window.location.origin + {prefix}, o = new Rt(e, r);");
            script = missingPatches.Replace(script, "login.js", "backofficePath: \"/umbraco\"",
                $"backofficePath: {JsonSerializer.Serialize(backOfficePath)}");
            // The slim login never configures the shared API client; manifest and password-reset
            // calls otherwise still use the origin even after its server context is corrected.
            script = "import { umbHttpClient } from '@umbraco-cms/backoffice/http-client';\n"
                + $"umbHttpClient.setConfig({{ baseUrl: window.location.origin + {prefix} }});\n" + script;
            return Encoding.UTF8.GetBytes(script);
        }

        byte[] PatchModule(string module, byte[] bytes)
        {
            var script = Encoding.UTF8.GetString(bytes);
            foreach (var (pattern, replacement) in ModulePatches[module])
                script = missingPatches.Replace(script, module, pattern, replacement);
            return Encoding.UTF8.GetBytes(script);
        }

        byte[] PatchWebsiteStyles(byte[] bytes)
        {
            // The stylesheet of Umbraco's own Page Not Found, No Content, Maintenance and Upgrading
            // pages loads its fonts and background from the root. Resolve them next to the stylesheet.
            var styles = missingPatches.Replace(Encoding.UTF8.GetString(bytes), "nonodes.css", "url(/umbraco/website/", "url(");
            return Encoding.UTF8.GetBytes(styles);
        }
    }

    private static Target Classify(HttpRequest request, Dictionary<PathString, string> modules, PathString basicAuthPath, out string? module)
    {
        module = null;
        // In particular, never replace the response stream of a SignalR WebSocket upgrade.
        if (request.Headers.Upgrade.Count > 0)
            return Target.None;
        // Failed basic-auth POSTs render the same forms as GETs, including their action URLs.
        if ((HttpMethods.IsGet(request.Method) || HttpMethods.IsPost(request.Method))
            && request.Path.StartsWithSegments(basicAuthPath)
            && request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase))
            return Target.Page;
        if (!HttpMethods.IsGet(request.Method))
            return Target.None;
        if (request.PathBase.HasValue)
        {
            if (modules.TryGetValue(request.Path, out module))
                return Target.Module;
            if (request.Path == LoginScript)
                return Target.LoginScript;
            if (request.Path == WebsiteStyles)
                return Target.WebsiteStyles;
        }

        if (!request.Path.StartsWithSegments(BackOfficeRoot))
            return Target.None;
        foreach (PathString prefix in Untouched)
        {
            if (request.Path.StartsWithSegments(prefix))
                return Target.None;
        }

        // Pages are only patched for browser navigations, which always accept HTML. API calls,
        // scripts, images and SignalR transports (text/event-stream, long polling) do not.
        return request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase)
            ? Target.Page
            : Target.None;
    }

    /// <summary>
    /// Whether the auth module wrapper should run Umbraco's normal session restore.
    /// </summary>
    /// <remarks>
    /// A missing refresh cookie only means "no session" when the browser would have sent it.
    /// SameSite=Strict cookies, Umbraco's default, are left off navigations another site starts
    /// (a link in web mail, for example), which the browser reports as <c>Sec-Fetch-Site: cross-site</c>.
    /// Umbraco can still restore those sessions through other tabs and a same-origin token request,
    /// so restore there. Browsers omit the header on plain-HTTP sites other than localhost; without
    /// it, cookie presence decides. An OAuth callback that carries a code creates the session
    /// through the code exchange, so it never needs a restore, even after an external sign-in.
    /// </remarks>
    private static bool ShouldRestoreSession(HttpRequest request, string cookieName, bool strictCookies)
    {
        if (request.Cookies.ContainsKey(cookieName))
            return true;
        if (!strictCookies || request.Query.ContainsKey("code"))
            return false;
        return request.Headers["Sec-Fetch-Site"] == "cross-site";
    }

    /// <summary>Logs once per pattern when an upstream bundle no longer contains what a patch expects.</summary>
    private sealed class MissingPatchLog(ILogger logger)
    {
        private readonly ConcurrentDictionary<string, bool> _reported = new(StringComparer.Ordinal);

        public string Replace(string text, string resource, string pattern, string replacement)
        {
            if (text.Contains(pattern, StringComparison.Ordinal))
                return text.Replace(pattern, replacement, StringComparison.Ordinal);
            if (_reported.TryAdd(resource + "\n" + pattern, true))
            {
                logger.LogWarning(
                    "Umbraco's {Resource} no longer contains {Pattern}, so that part of the application virtual path fix is not applied. "
                    + "Install the AurorisNet.Umbraco.ApplicationVirtualPathFix release that matches this site's Umbraco version.",
                    resource, pattern);
            }

            return text;
        }
    }
}
