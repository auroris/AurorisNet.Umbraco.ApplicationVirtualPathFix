using Umbraco.Cms.Web.Common.Hosting;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix;

/// <summary>
/// Puts the virtual path back onto <c>Request.Path</c> for the Umbraco 18.3.1 features that compare
/// it with a path that already includes the virtual path, when the site runs under a path base
/// (<c>UsePathBase</c>, or an IIS child application).
/// </summary>
/// <remarks>
/// Several Umbraco components build their paths from <c>GetBackOfficePath()</c>, which includes
/// the application virtual path, and match them against <c>Request.Path</c>, which under a path base
/// no longer carries that prefix:
/// <list type="bullet">
/// <item>The rewrite rule that serves the cache-busted backoffice assets
/// (<c>{ApplicationVirtualPath}/umbraco/backoffice/{hash}/</c>). Without it every script and
/// stylesheet returns 404.</item>
/// <item>Basic authentication: the login and two-factor routes and the middleware's own exemption
/// for them. Without it, a site with <c>RedirectToLoginPage</c> redirects every visitor in a loop.</item>
/// <item>The non-production OpenAPI documents and UI, and the management API contract.</item>
/// </list>
/// Restoring the shape those components expect, for just these requests, lets them match. Static
/// files under <c>/umbraco/basic-auth</c> are left alone because the static-file handler serves those
/// directly. Does nothing when no virtual path is configured, and each case becomes redundant once
/// Umbraco accounts for the path base upstream.
/// </remarks>
internal static class UmbracoPathsUnderPathBase
{
    private static readonly PathString AssetsRoot = "/umbraco/backoffice";
    private static readonly PathString BasicAuth = "/umbraco/basic-auth";
    private static readonly PathString OpenApi = "/umbraco/openapi";
    private static readonly PathString ManagementContract = "/umbraco/management/api/openapi.json";

    /// <summary>Register after <c>UsePathBase</c> and before <c>UseUmbraco</c>.</summary>
    public static IApplicationBuilder UseUmbracoPathsUnderPathBase(this IApplicationBuilder app)
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
            if (!originalPathBase.HasValue || !ComparedWithVirtualPath(originalPath, hashedAssets))
            {
                await next(context);
                return;
            }

            request.Path = virtualPath.Add(originalPath);
            // Basic-auth routes already contain the prefix, so MVC prepends PathBase twice.
            // Keep PathBase for Url.Content and custom views; correct only these redirect URLs.
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
                request.PathBase = originalPathBase;
            }
        });
    }

    private static bool ComparedWithVirtualPath(PathString path, PathString hashedAssets)
        => path.StartsWithSegments(hashedAssets)
            || (path.StartsWithSegments(BasicAuth) && !Path.HasExtension(path.Value))
            || path.StartsWithSegments(OpenApi)
            || path.Equals(ManagementContract);
}
