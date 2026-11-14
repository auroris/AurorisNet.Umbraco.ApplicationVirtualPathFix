using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Web.Common.Hosting;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix;

/// <summary>
/// Supplies the backoffice's virtual path and refresh-cookie presence, and corrects the slim login's URLs.
/// </summary>
/// <remarks>
/// The SPA builds its management API, OAuth and post-login URLs from <c>window.location.origin</c>
/// plus a hard-coded <c>/umbraco</c>, so under a path base every call leaves the prefix. The
/// <c>umb-app</c> element accepts <c>server-url</c> and <c>backoffice-path</c> attributes that
/// change both, but the page that renders it is a precompiled view in Umbraco.Cms.StaticAssets and
/// this project's Razor setup does not override those, so the attributes are added to the served
/// HTML instead. Umbraco 18.3's slim login also hard-codes the root URLs; its served script is patched
/// under a virtual path. The auth module wrapper uses a cookie-presence attribute to avoid trying
/// to refresh before the first token exchange. The cookie itself stays HTTP-only.
/// </remarks>
internal static class BackOfficeAppUnderVirtualPath
{
    private static readonly PathString BackOfficeRoot = "/umbraco";
    private static readonly PathString LoginScript = "/umbraco/login/login.js";
    private const string HeaderLogoScript = "/apps/backoffice/components/backoffice-header-logo.element.js";
    internal static readonly string BuildVersion = typeof(BackOfficeAppUnderVirtualPath).Assembly.ManifestModule.ModuleVersionId.ToString("N");
    private static readonly PathString[] Untouched =
        ["/umbraco/backoffice", "/umbraco/management", "/umbraco/delivery", "/umbraco/reports", "/umbraco/report-download"];

    /// <summary>Register after <c>UsePathBase</c> and before <c>UseUmbraco</c>.</summary>
    public static IApplicationBuilder UseBackOfficeAppUnderVirtualPath(this IApplicationBuilder app)
    {
        var paths = app.ApplicationServices.GetRequiredService<IBackOfficePathGenerator>();
        var backOfficePath = paths.BackOfficePath;
        var headerLogoScript = new PathString(paths.BackOfficeAssetsPath + HeaderLogoScript);
#pragma warning disable CS0618 // Umbraco 18's token handler still uses these options; the replacement is scheduled for 19.
        var cookies = app.ApplicationServices.GetRequiredService<IOptions<BackOfficeTokenCookieSettings>>().Value;
#pragma warning restore CS0618
        var global = app.ApplicationServices.GetRequiredService<IOptions<GlobalSettings>>().Value;

        return app.Use(async (context, next) =>
        {
            var request = context.Request;
            var patchHeaderLogo = request.PathBase.HasValue &&
                (request.Path == headerLogoScript || request.Path == new PathString("/umbraco/backoffice" + HeaderLogoScript));
            if (!ShouldPatch(request, patchHeaderLogo))
            {
                await next(context);
                return;
            }

            var originalBody = context.Response.Body;
            using var buffer = new MemoryStream();
            context.Response.Body = buffer;
            try
            {
                await next(context);

                byte[] bytes = buffer.ToArray();
                if (context.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true)
                {
                    var serverUrl = $"{request.Scheme}://{request.Host}{request.PathBase}";
                    var cookieName = (global.UseHttps || request.IsHttps ? "__Host-" : "") + "umbRefreshToken" + cookies.SiteName;
                    var refreshAttribute = request.Cookies.ContainsKey(cookieName) ? "data-has-refresh-token " : "";
                    var html = Encoding.UTF8.GetString(bytes).Replace(
                        "<umb-app ",
                        $"<umb-app {refreshAttribute}server-url=\"{HtmlEncoder.Default.Encode(serverUrl)}\" backoffice-path=\"{HtmlEncoder.Default.Encode(backOfficePath)}\" ",
                        StringComparison.Ordinal);
                    if (request.PathBase.HasValue)
                    {
                        // Razor versions the original asset. Also invalidate it when the package's patch changes.
                        html = html.Replace(LoginScript + "?", LoginScript + $"?avpfix={BuildVersion}&amp;", StringComparison.Ordinal);
                    }
                    bytes = Encoding.UTF8.GetBytes(html);
                    // Cookie presence is specific to this request, including immediately after sign-in.
                    context.Response.Headers.CacheControl = "no-store";
                }
                else if (request.PathBase.HasValue && request.Path == LoginScript && context.Response.StatusCode == StatusCodes.Status200OK)
                {
                    var prefix = JsonSerializer.Serialize(request.PathBase.Value);
                    // These are the three root assumptions in Umbraco 18.3's slim login bundle.
                    // Scope the replacements to this asset, leaving return-URL origin checks intact.
                    var script = Encoding.UTF8.GetString(bytes)
                        .Replace("new Request(\"/umbraco/", $"new Request({prefix} + \"/umbraco/", StringComparison.Ordinal)
                        .Replace("const r = window.location.origin, o = new Rt(e, r);",
                            $"const r = window.location.origin + {prefix}, o = new Rt(e, r);", StringComparison.Ordinal)
                        .Replace("backofficePath: \"/umbraco\"", $"backofficePath: {JsonSerializer.Serialize(backOfficePath)}", StringComparison.Ordinal);
                    // The slim login never configures the shared API client; manifest and password-reset
                    // calls otherwise still use the origin even after its server context is corrected.
                    script = "import { umbHttpClient } from '@umbraco-cms/backoffice/http-client';\n"
                        + $"umbHttpClient.setConfig({{ baseUrl: window.location.origin + {prefix} }});\n" + script;
                    bytes = Encoding.UTF8.GetBytes(script);
                }
                else if (patchHeaderLogo && context.Response.StatusCode == StatusCodes.Status200OK)
                {
                    var script = Encoding.UTF8.GetString(bytes).Replace(
                        "src=\"/umbraco/backoffice/assets/umbraco-logo.svg\"",
                        "src=${new URL(\"../../../assets/umbraco-logo.svg\", import.meta.url).href}", StringComparison.Ordinal);
                    bytes = Encoding.UTF8.GetBytes(script);
                }
                if (context.Response.ContentLength is not null)
                    context.Response.ContentLength = bytes.Length;

                await originalBody.WriteAsync(bytes, context.RequestAborted);
            }
            finally
            {
                context.Response.Body = originalBody;
            }
        });
    }

    private static bool ShouldPatch(HttpRequest request, bool patchHeaderLogo)
    {
        // In particular, never replace the response stream of a SignalR WebSocket upgrade.
        if (!HttpMethods.IsGet(request.Method) || request.Headers.Upgrade.Count > 0)
            return false;
        if (patchHeaderLogo)
            return true;
        if (!request.Path.StartsWithSegments(BackOfficeRoot))
            return false;
        foreach (PathString prefix in Untouched)
        {
            if (request.Path.StartsWithSegments(prefix))
                return false;
        }

        return true;
    }
}
