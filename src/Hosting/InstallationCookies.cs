using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Configuration.Models;
using IHostingEnvironment = Umbraco.Cms.Core.Hosting.IHostingEnvironment;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix.Hosting;

/// <summary>
/// Gives each installation its own browser cookies even when several sites share a hostname, and redirects requests
/// whose virtual-path prefix differs in case so case-sensitive browser cookie paths stay consistent.
/// </summary>
public sealed class InstallationCookies
{
    // Umbraco reads these by name, including directly from Request.Cookies, and deletes some of them at logout.
    private readonly Dictionary<string, string> _fixedNames;
    private readonly PathString _virtualPath;

    /// <summary>
    /// Computes the installation's cookie suffix from the site name and installation ID, and builds the map of
    /// fixed-name cookies to isolate.
    /// </summary>
    public InstallationCookies(
        IConfiguration configuration,
        IHostingEnvironment hosting,
        IOptions<InstallationCookiesOptions>? options = null)
    {
        var mount = hosting.ApplicationVirtualPath.Trim('/');
        _virtualPath = mount.Length == 0 ? PathString.Empty : new PathString("/" + mount);
        var name = Regex.Replace((hosting.SiteName ?? "").ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (name.Length == 0) name = "site";
        if (name.Length > 32) name = name[..32].TrimEnd('-');
        var id = configuration["Umbraco:CMS:Global:Id"]?.Trim();
        if (Guid.TryParse(id, out var guid)) id = guid.ToString("D");
        // Hosts without a configured Global:Id fall back to their physical and virtual paths.
        if (string.IsNullOrEmpty(id)) id = hosting.ApplicationPhysicalPath + "\n" + _virtualPath;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(id));
        Suffix = "-" + name + "-" + Convert.ToHexStringLower(hash.AsSpan(0, 6));

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Constants.Web.PreviewCookieName,
            "UMB-DEBUG",
            ViewDataExtensions.TokenExternalSignInError,
            Constants.Web.CsrfValidationCookieName,
            Constants.Security.BackOfficeExternalCookieName,
        };
        if (options?.Value.AdditionalCookieNames is { } extra)
        {
            foreach (var extraName in extra)
            {
                if (!string.IsNullOrWhiteSpace(extraName))
                    names.Add(extraName.Trim());
            }
        }
        _fixedNames = names.ToDictionary(key => key, key => key + Suffix, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The installation-specific suffix appended to cookie names and backoffice auth keys.</summary>
    public string Suffix { get; }

    /// <summary>
    /// Registers <see cref="InstallationCookies"/> and post-configures Umbraco and ASP.NET Core cookie options to
    /// include <see cref="Suffix"/>.
    /// </summary>
    internal static void Configure(IServiceCollection services)
    {
        services.AddSingleton<InstallationCookies>();
        // Umbraco also uses AuthCookieName to derive the exposed backoffice cookie and when clearing the login.
        services.AddOptions<SecuritySettings>().PostConfigure<InstallationCookies>(
            (options, site) => options.AuthCookieName += site.Suffix);
#pragma warning disable CS0618 // Umbraco 18 still uses this option for access, refresh and PKCE token cookies.
        services.AddOptions<BackOfficeTokenCookieSettings>().PostConfigure<InstallationCookies>(
            (options, site) => options.SiteName += site.Suffix);
#pragma warning restore CS0618
        string[] schemes =
        [
            Constants.Security.BackOfficeExternalAuthenticationType,
            Constants.Security.BackOfficeTwoFactorAuthenticationType,
            Constants.Security.BackOfficeTwoFactorRememberMeAuthenticationType,
            IdentityConstants.ApplicationScheme,
            IdentityConstants.ExternalScheme,
            IdentityConstants.TwoFactorUserIdScheme,
            IdentityConstants.TwoFactorRememberMeScheme,
        ];
        foreach (var scheme in schemes)
            services.AddOptions<CookieAuthenticationOptions>(scheme).PostConfigure<InstallationCookies>(
                (options, site) => options.Cookie.Name = (options.Cookie.Name ?? CookieAuthenticationDefaults.CookiePrefix + scheme) + site.Suffix);
        services.AddOptions<SessionOptions>().PostConfigure<InstallationCookies>((options, site) => options.Cookie.Name += site.Suffix);
        services.AddOptions<CookieTempDataProviderOptions>().PostConfigure<InstallationCookies>((options, site) => options.Cookie.Name += site.Suffix);
        services.AddOptions<AntiforgeryOptions>().PostConfigure<InstallationCookies>((options, site) => options.Cookie.Name += site.Suffix);
    }

    /// <summary>
    /// Redirects requests whose virtual-path prefix differs in case, maps fixed-name request cookies to their
    /// installation-specific physical names, and scopes outgoing installation cookies to the virtual path.
    /// </summary>
    public Task Apply(HttpContext context, RequestDelegate next)
    {
        // IIS and Umbraco match mounts ignoring case. Browsers compare cookie paths with case sensitivity.
        // Redirect before any handler can issue or delete a cookie under another spelling of this mount.
        if (_virtualPath.HasValue && context.Request.PathBase.Add(context.Request.Path)
                .StartsWithSegments(_virtualPath, StringComparison.OrdinalIgnoreCase, out var matched, out var remaining)
            && !matched.Equals(_virtualPath, StringComparison.Ordinal))
        {
            context.Response.Redirect(_virtualPath.Add(remaining).ToUriComponent() + context.Request.QueryString,
                permanent: false, preserveMethod: true);
            return Task.CompletedTask;
        }
        var incoming = context.Request.Cookies;
        if (_fixedNames.Any(pair => incoming.ContainsKey(pair.Key) || incoming.ContainsKey(pair.Value)))
        {
            // Ignore old shared names even during a rolling deployment, when a sibling may still be using them.
            var cookies = new RequestCookies(incoming);
            foreach (var (logical, physical) in _fixedNames)
            {
                cookies.Remove(logical);
                if (cookies.Remove(physical, out var value)) cookies[logical] = value;
            }
            context.Features.Set<IRequestCookiesFeature>(new RequestCookiesFeature(cookies));
        }
        context.Features.Set<IResponseCookiesFeature>(new ResponseCookies(context, context.Response.Cookies, this));
        return next(context);
    }

    private sealed class RequestCookies(IRequestCookieCollection source) :
        Dictionary<string, string>(source, StringComparer.OrdinalIgnoreCase), IRequestCookieCollection
    {
        ICollection<string> IRequestCookieCollection.Keys => Keys;
        string? IRequestCookieCollection.this[string key] => this.GetValueOrDefault(key);
    }

    private sealed class ResponseCookies(HttpContext context, IResponseCookies inner, InstallationCookies site)
        : IResponseCookiesFeature, IResponseCookies
    {
        public IResponseCookies Cookies => this;

        public void Append(string key, string value) => Append(key, value, new CookieOptions());
        public void Delete(string key) => Delete(key, new CookieOptions());

        public void Append(string key, string value, CookieOptions options)
        {
            key = site._fixedNames.GetValueOrDefault(key, key);
            inner.Append(key, value, OptionsFor(key, options));
        }

        public void Delete(string key, CookieOptions options)
        {
            key = site._fixedNames.GetValueOrDefault(key, key);
            inner.Delete(key, OptionsFor(key, options));
        }

        private CookieOptions OptionsFor(string key, CookieOptions options)
        {
            // The suffix can precede _EXPOSED or a chunk number. __Host- cookies must keep Path=/.
            if (options.Path != "/" || key.StartsWith("__Host-", StringComparison.Ordinal)
                || !key.Contains(site.Suffix, StringComparison.Ordinal)) return options;
            var path = context.Request.PathBase.HasValue ? context.Request.PathBase.ToUriComponent() : site._virtualPath.ToUriComponent();
            return path.Length == 0 ? options : new CookieOptions(options) { Path = path };
        }
    }
}
