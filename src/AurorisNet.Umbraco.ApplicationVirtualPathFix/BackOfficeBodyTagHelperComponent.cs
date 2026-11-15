using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Web.Common.Hosting;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix;

/// <summary>
/// Adds the package's attributes to the backoffice page's <c>umb-app</c> element and corrects the basic-auth form
/// actions, while Umbraco's compiled views render the body.
/// </summary>
#pragma warning disable CS0618 // Umbraco 18's token handler still reads these obsolete cookie options.
internal sealed class BackOfficeBodyTagHelperComponent(
    IBackOfficePathGenerator paths, IOptions<BackOfficeTokenCookieSettings> cookies, IOptions<GlobalSettings> global)
    : TagHelperComponent
#pragma warning restore CS0618
{
    [ViewContext]
    public ViewContext ViewContext { get; set; } = null!;

    /// <summary>
    /// Adds attributes to the <c>umb-app</c> element on the backoffice page: <c>data-restore-session</c> when a session
    /// may exist and, under a virtual path, <c>data-path-base</c> and <c>backoffice-path</c>. The response then gets
    /// <c>Cache-Control: no-store</c>. Under a virtual path, also removes the doubled path base from the form actions on
    /// the basic-auth pages.
    /// </summary>
    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        if (context.TagName != "body") return;
        var backOffice = ViewContext.ExecutingFilePath == "/umbraco/UmbracoBackOffice/Index.cshtml";
        var basicAuth = ViewContext.ExecutingFilePath is "/umbraco/BasicAuthLogin/Login.cshtml" or "/umbraco/BasicAuthLogin/TwoFactor.cshtml";
        var request = ViewContext.HttpContext.Request;
        if (!backOffice && !(basicAuth && request.PathBase.HasValue)) return;

        // Start from content another component has already changed, including suppressed output.
        var content = output.IsContentModified ? output.Content : await output.GetChildContentAsync();
        var html = content.GetContent();
        var patched = html;
        if (basicAuth)
        {
            // MVC's basic-auth routes already include the prefix, so Url.Action adds the path base a second time.
            var path = paths.BackOfficePath + "/basic-auth";
            patched = html.Replace("\"" + HtmlEncoder.Default.Encode(request.PathBase + path),
                "\"" + HtmlEncoder.Default.Encode(path), StringComparison.Ordinal);
        }
        else if (html.Contains("<umb-app ", StringComparison.Ordinal))
        {
            var attributes = new StringBuilder();
            var cookieName = (global.Value.UseHttps || request.IsHttps ? "__Host-" : "") + "umbRefreshToken" + cookies.Value.SiteName;
            var strictCookies = string.Equals(cookies.Value.SameSite, "Strict", StringComparison.OrdinalIgnoreCase);
            if (ShouldRestoreSession(request, cookieName, strictCookies))
                attributes.Append("data-restore-session ");
            if (request.PathBase.HasValue)
            {
                // Write only the path base. auth.js adds the origin the browser loaded the page from, as Umbraco does at
                // the root, so the server URL depends on neither the Host header nor forwarded headers.
                attributes.Append($"data-path-base=\"{HtmlEncoder.Default.Encode(request.PathBase.ToString())}\" ");
                attributes.Append($"backoffice-path=\"{HtmlEncoder.Default.Encode(paths.BackOfficePath)}\" ");
            }
            patched = html.Replace("<umb-app ", "<umb-app " + attributes, StringComparison.Ordinal);
            // The page now depends on the request's cookies, so keep it out of caches. Only the hint that a session may
            // exist enters the HTML; the tokens stay in HTTP-only cookies.
            ViewContext.HttpContext.Response.Headers.CacheControl = "no-store";
        }
        if (patched != html) output.Content.SetHtmlContent(patched);
    }

    /// <summary>
    /// Returns whether the browser may hold a session: the refresh cookie <paramref name="cookieName"/> arrived, or, with
    /// <paramref name="strictCookies"/>, the browser may have withheld it on a cross-site navigation. An OAuth callback,
    /// which carries a <c>code</c> query, counts only when the cookie arrived.
    /// </summary>
    private static bool ShouldRestoreSession(HttpRequest request, string cookieName, bool strictCookies)
    {
        if (request.Cookies.ContainsKey(cookieName)) return true;
        if (!strictCookies || request.Query.ContainsKey("code")) return false;
        // Browsers withhold SameSite=Strict cookies on a navigation that another site started. Without a Sec-Fetch-Site
        // header (plain HTTP away from localhost), the cookie alone decides.
        return request.Headers["Sec-Fetch-Site"] == "cross-site";
    }
}
