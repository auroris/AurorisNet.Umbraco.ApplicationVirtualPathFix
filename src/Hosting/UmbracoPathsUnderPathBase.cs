using AurorisNet.Umbraco.ApplicationVirtualPathFix.UmbracoVersions;
using Umbraco.Cms.Web.Common.Hosting;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix.Hosting;

/// <summary>
/// Puts the virtual path back onto <c>Request.Path</c> for the Umbraco features that compare it with a path that
/// includes the virtual path, when the site runs under a path base (<c>UsePathBase</c>, or an IIS child application).
/// </summary>
/// <remarks>
/// Several Umbraco components build paths with <c>GetBackOfficePath()</c>, which includes the application virtual
/// path, and compare them with <c>Request.Path</c>, from which the path base has removed that prefix:
/// <list type="bullet">
/// <item>The rewrite rule that serves the cache-busted backoffice assets
/// (<c>{ApplicationVirtualPath}/umbraco/backoffice/{hash}/</c>). Without the fix, every script and stylesheet returns
/// 404.</item>
/// <item>Basic authentication: the login and two-factor routes, and the middleware's exemption for them. Without the
/// fix, a site with <c>RedirectToLoginPage</c> redirects every visitor in a loop.</item>
/// <item>The API documents and their UI outside production, under the path that the site's
/// <see cref="UmbracoMajor.ApiDocuments"/> names, and the management API contract.</item>
/// </list>
/// The middleware restores the expected path for these requests only. The static-file handler serves files under
/// <c>/umbraco/basic-auth</c> directly, so their paths stay as they are. Each case becomes redundant once Umbraco
/// accounts for the path base.
/// </remarks>
internal static class UmbracoPathsUnderPathBase
{
    private static readonly PathString AssetsRoot = "/umbraco/backoffice";
    private static readonly PathString BasicAuth = "/umbraco/basic-auth";
    private static readonly PathString ManagementContract = "/umbraco/management/api/openapi.json";

    /// <summary>
    /// Adds middleware that restores the virtual path for the requests in this class's remarks and corrects the doubled
    /// prefix in basic-auth redirects. It also redirects page requests under <c>/umbraco</c> whose prefix differs from
    /// the configured one only in case. Adds nothing when no virtual path is configured. <paramref name="umbraco"/> is
    /// the site's Umbraco major version.
    /// </summary>
    /// <remarks>Register it after <c>UsePathBase</c> and before <c>UseUmbraco</c>.</remarks>
    public static IApplicationBuilder UseUmbracoPathsUnderPathBase(this IApplicationBuilder app, UmbracoMajor umbraco)
    {
        var paths = app.ApplicationServices.GetRequiredService<IBackOfficePathGenerator>();
        var virtualDirectory = paths.BackOfficeVirtualDirectory;
        if (virtualDirectory == AssetsRoot.Value)
            return app;

        // "/portal/umbraco/backoffice" -> "/portal"
        var virtualPath = new PathString(virtualDirectory[..^AssetsRoot.Value!.Length]);
        var hashedAssets = AssetsRoot.Add("/" + paths.BackOfficeCacheBustHash);
        return app.Use(async (context, next) =>
        {
            var request = context.Request;
            var originalPath = request.Path;
            var originalPathBase = request.PathBase;
            // The path base matches the prefix in any case, but the backoffice router compares its base URL exactly.
            // Redirect page navigations to the configured spelling.
            if (string.Equals(originalPathBase.Value, virtualPath.Value, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(originalPathBase.Value, virtualPath.Value, StringComparison.Ordinal)
                && originalPath.StartsWithSegments("/umbraco")
                && (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
                && request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.Redirect(virtualPath.Add(originalPath).ToUriComponent() + request.QueryString);
                return;
            }
            if (!originalPathBase.HasValue || !ComparedWithVirtualPath(originalPath, hashedAssets, umbraco.ApiDocuments))
            {
                await next(context);
                return;
            }

            request.Path = virtualPath.Add(originalPath);
            // Basic-auth routes already contain the prefix, so MVC adds the path base a second time to their redirects.
            // Keep the path base for Url.Content and custom views, and correct only these Location headers.
            if (originalPath.StartsWithSegments(BasicAuth))
            {
                var doubled = originalPathBase.Add(virtualPath).Add(BasicAuth).Value!;
                var corrected = virtualPath.Add(BasicAuth).Value!;
                context.Response.OnStarting(() =>
                {
                    var location = context.Response.Headers.Location.ToString();
                    if (location.StartsWith(doubled, StringComparison.OrdinalIgnoreCase)
                        && (location.Length == doubled.Length || location[doubled.Length] is '/' or '?' or '#'))
                        context.Response.Headers.Location = corrected + location[doubled.Length..];
                    return Task.CompletedTask;
                });
            }
            try
            {
                await next(context);
            }
            finally
            {
                request.Path = originalPath;
            }
        });
    }

    /// <summary>
    /// Returns whether Umbraco compares <paramref name="path"/> with a path that includes the virtual path: hashed
    /// backoffice assets, basic-auth routes other than files, the API documents and their UI under
    /// <paramref name="apiDocuments"/>, and the management API contract.
    /// </summary>
    private static bool ComparedWithVirtualPath(PathString path, PathString hashedAssets, PathString apiDocuments)
        => path.StartsWithSegments(hashedAssets)
            || (path.StartsWithSegments(BasicAuth) && !Path.HasExtension(path.Value))
            || path.StartsWithSegments(apiDocuments)
            || path.Equals(ManagementContract);
}
