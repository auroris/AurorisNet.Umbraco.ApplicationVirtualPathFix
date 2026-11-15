using Umbraco.Cms.Core.Configuration.Models;
using IHostingEnvironment = Umbraco.Cms.Core.Hosting.IHostingEnvironment;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix;

/// <summary>
/// Reads the application virtual path, <c>Umbraco:CMS:Hosting:ApplicationVirtualPath</c> (for example <c>/portal</c>),
/// and adds it to Umbraco's OAuth callback settings.
/// </summary>
/// <remarks>
/// Umbraco strips the prefix from incoming URLs before content routing and adds it to the links it generates. The
/// package also applies the prefix as the path base. When a request still carries the prefix, the path base removes it.
/// When the host has already moved it into the path base, as an IIS child application does, the request passes
/// unchanged. The package also prefixes the three <c>Umbraco:CMS:Security:AuthorizeCallback*PathName</c> settings, so
/// OpenIddict registers the prefixed OAuth callbacks. One setting covers both hosting styles. The package's composer
/// registers all of this with Umbraco's value of the setting, so a path configured in code applies too.
/// </remarks>
public static class VirtualPathHosting
{
    /// <summary>The configuration key that holds the application virtual path.</summary>
    public const string SettingKey = "Umbraco:CMS:Hosting:ApplicationVirtualPath";

    /// <summary>
    /// Returns the configured prefix, such as <c>/portal</c>, or <see langword="null"/> when the site runs at the root.
    /// </summary>
    /// <remarks>
    /// Reads <paramref name="configuration"/> directly. The package itself reads Umbraco's hosting environment, which
    /// also reflects a path configured in code.
    /// </remarks>
    public static string? Prefix(IConfiguration configuration)
    {
        var virtualPath = configuration[SettingKey]?.Trim().Trim('/');
        return string.IsNullOrWhiteSpace(virtualPath) ? null : "/" + virtualPath;
    }

    /// <summary>
    /// Returns the prefix Umbraco adds to the paths it generates, such as <c>/portal</c>, or <see langword="null"/> when
    /// the site runs at the root.
    /// </summary>
    internal static string? Prefix(IHostingEnvironment hosting)
    {
        // Umbraco's ToAbsolute builds paths on this value, so derive the prefix from it too.
        var virtualPath = hosting.ApplicationVirtualPath.TrimEnd('/');
        return virtualPath.Length == 0 ? null : virtualPath;
    }

    /// <summary>
    /// Prefixes Umbraco's three OAuth callback path settings with the virtual path after configuration is bound, so
    /// OpenIddict registers the callbacks under the prefix. Settings that already start with the prefix stay as they are.
    /// </summary>
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

    /// <summary>
    /// Returns <paramref name="path"/> with a leading slash, prefixed with <paramref name="prefix"/> unless it already
    /// starts with it.
    /// </summary>
    private static string WithPrefix(string path, string prefix)
    {
        // Umbraco accepts these settings with or without a leading slash.
        var rooted = path.StartsWith('/') ? path : "/" + path;
        return rooted.StartsWith(prefix + "/", StringComparison.Ordinal) ? rooted : prefix + rooted;
    }
}
