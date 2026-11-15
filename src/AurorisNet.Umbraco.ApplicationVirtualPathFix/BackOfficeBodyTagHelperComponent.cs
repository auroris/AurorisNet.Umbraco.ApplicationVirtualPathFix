using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Web.Common.Hosting;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix;

/// <summary>Applies the HTML fixes while Umbraco's existing compiled views render their body.</summary>
#pragma warning disable CS0618 // Umbraco 18's token handler still uses these cookie options.
internal sealed class BackOfficeBodyTagHelperComponent(
    IBackOfficePathGenerator paths, IOptions<BackOfficeTokenCookieSettings> cookies, IOptions<GlobalSettings> global)
    : TagHelperComponent
#pragma warning restore CS0618
{
    [ViewContext]
    public ViewContext ViewContext { get; set; } = null!;

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        if (context.TagName != "body") return;
        var backOffice = ViewContext.ExecutingFilePath == "/umbraco/UmbracoBackOffice/Index.cshtml";
        var basicAuth = ViewContext.ExecutingFilePath is "/umbraco/BasicAuthLogin/Login.cshtml" or "/umbraco/BasicAuthLogin/TwoFactor.cshtml";
        var request = ViewContext.HttpContext.Request;
        if (!backOffice && !(basicAuth && request.PathBase.HasValue)) return;

        // Preserve content already changed by another component, including suppressed output.
        var content = output.IsContentModified ? output.Content : await output.GetChildContentAsync();
        var html = content.GetContent();
        var patched = html;
        if (basicAuth)
        {
            // MVC's basic-auth routes include the prefix, so Url.Action adds PathBase twice.
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
                // Only the path base: auth.js adds the origin the browser loaded the page from, as Umbraco itself
                // does at the root, so the backoffice depends on neither the Host header nor forwarded headers.
                attributes.Append($"data-path-base=\"{HtmlEncoder.Default.Encode(request.PathBase.ToString())}\" ");
                attributes.Append($"backoffice-path=\"{HtmlEncoder.Default.Encode(paths.BackOfficePath)}\" ");
            }
            patched = html.Replace("<umb-app ", "<umb-app " + attributes, StringComparison.Ordinal);
            // Only the presence of a possible session enters the HTML; tokens stay HTTP-only.
            ViewContext.HttpContext.Response.Headers.CacheControl = "no-store";
        }
        if (patched != html) output.Content.SetHtmlContent(patched);
    }

    private static bool ShouldRestoreSession(HttpRequest request, string cookieName, bool strictCookies)
    {
        if (request.Cookies.ContainsKey(cookieName)) return true;
        if (!strictCookies || request.Query.ContainsKey("code")) return false;
        // Strict cookies may have been withheld on an external navigation. With no Fetch
        // Metadata header (plain HTTP away from localhost), cookie presence alone decides.
        return request.Headers["Sec-Fetch-Site"] == "cross-site";
    }
}
