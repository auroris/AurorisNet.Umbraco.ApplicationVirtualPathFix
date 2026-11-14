using Umbraco.Cms.Core.Configuration.Models;
using IHostingEnvironment = Umbraco.Cms.Core.Hosting.IHostingEnvironment;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix;

/// <summary>
/// Hosts the whole site, backoffice included, under <c>Umbraco:CMS:Hosting:ApplicationVirtualPath</c>
/// (for example <c>/portal</c>).
/// </summary>
/// <remarks>
/// Umbraco strips the prefix from incoming URLs before content routing and prepends it to the links
/// it generates. The site also applies it as the path base, which strips the prefix when the request
/// still carries it and is a no-op when the host has already moved it into the path base, as an IIS
/// child application does, and prefixes the three <c>Umbraco:CMS:Security:AuthorizeCallback*PathName</c>
/// settings so OpenIddict registers the prefixed OAuth callback. One setting covers both hosting
/// styles. The package's composer registers this automatically, using Umbraco's own value of the
/// setting so a path configured in code is honoured too.
/// </remarks>
public static class VirtualPathHosting
{
    public const string SettingKey = "Umbraco:CMS:Hosting:ApplicationVirtualPath";

    /// <summary>The configured prefix, such as <c>/portal</c>, or null when the site is at the root.</summary>
    /// <remarks>Reads configuration directly. The package itself uses Umbraco's hosting environment.</remarks>
    public static string? Prefix(IConfiguration configuration)
    {
        var virtualPath = configuration[SettingKey]?.Trim().Trim('/');
        return string.IsNullOrWhiteSpace(virtualPath) ? null : "/" + virtualPath;
    }

    /// <summary>The prefix Umbraco itself prepends to the paths it generates, or null at the root.</summary>
    internal static string? Prefix(IHostingEnvironment hosting)
    {
        // Umbraco's ToAbsolute appends to this value, so derive the prefix the same way.
        var virtualPath = hosting.ApplicationVirtualPath.TrimEnd('/');
        return virtualPath.Length == 0 ? null : virtualPath;
    }

    internal static IServiceCollection AddVirtualPathHosting(this IServiceCollection services)
    {
        services.AddOptions<SecuritySettings>().PostConfigure<IHostingEnvironment>((security, hosting) =>
        {
            if (Prefix(hosting) is not { } prefix)
                return;
            security.AuthorizeCallbackPathName = WithPrefix(security.AuthorizeCallbackPathName, prefix);
            security.AuthorizeCallbackLogoutPathName = WithPrefix(security.AuthorizeCallbackLogoutPathName, prefix);
            security.AuthorizeCallbackErrorPathName = WithPrefix(security.AuthorizeCallbackErrorPathName, prefix);
        });
        return services;
    }

    private static string WithPrefix(string path, string prefix)
    {
        // Umbraco accepts these settings with or without a leading slash.
        var rooted = path.StartsWith('/') ? path : "/" + path;
        return rooted.StartsWith(prefix + "/", StringComparison.Ordinal) ? rooted : prefix + rooted;
    }
}
