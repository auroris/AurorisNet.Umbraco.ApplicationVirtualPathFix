using System.Text;
using AurorisNet.Umbraco.ApplicationVirtualPathFix.BackOffice;
using AurorisNet.Umbraco.ApplicationVirtualPathFix.Hosting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Configuration.Models;
using IHostingEnvironment = Umbraco.Cms.Core.Hosting.IHostingEnvironment;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests;

/// <summary>
/// Checks per-installation cookie isolation, canonical virtual-path redirects, and scoped backoffice auth modules.
/// </summary>
public sealed class InstallationCookiesTests(AssetPatchTests.Assets assets) : IClassFixture<AssetPatchTests.Assets>
{
    /// <summary>Checks that Configure appends the installation suffix to Umbraco and ASP.NET Core cookie options.</summary>
    [Fact]
    public void Configure_suffixes_umbraco_and_aspnet_cookie_options()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Umbraco:CMS:Global:Id"] = id.ToString("D") })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IHostingEnvironment>(new TestHostingEnvironment("/portal", "My Portal!", @"C:\sites\portal"));
        InstallationCookies.Configure(services);
        using var provider = services.BuildServiceProvider();

        var cookies = provider.GetRequiredService<InstallationCookies>();
        Assert.Matches("^-my-portal-[0-9a-f]{12}$", cookies.Suffix);
        Assert.EndsWith(cookies.Suffix, provider.GetRequiredService<IOptions<SecuritySettings>>().Value.AuthCookieName);
#pragma warning disable CS0618
        Assert.EndsWith(cookies.Suffix, provider.GetRequiredService<IOptions<BackOfficeTokenCookieSettings>>().Value.SiteName);
#pragma warning restore CS0618
        Assert.EndsWith(cookies.Suffix, provider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme).Cookie.Name);
        Assert.EndsWith(cookies.Suffix, provider.GetRequiredService<IOptions<SessionOptions>>().Value.Cookie.Name);
        Assert.EndsWith(cookies.Suffix, provider.GetRequiredService<IOptions<CookieTempDataProviderOptions>>().Value.Cookie.Name);
        Assert.EndsWith(cookies.Suffix, provider.GetRequiredService<IOptions<AntiforgeryOptions>>().Value.Cookie.Name);
    }

    /// <summary>Checks that Apply redirects mismatched virtual-path casing with 307 while keeping method and query.</summary>
    [Fact]
    public async Task Apply_redirects_mismatched_virtual_path_casing_preserving_method_and_query()
    {
        var cookies = CreateCookies("/portal", "Portal");
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/PORTAL/orders/new";
        context.Request.QueryString = new QueryString("?return=%2Fhome");
        var reached = false;
        await cookies.Apply(context, _ => { reached = true; return Task.CompletedTask; });

        Assert.False(reached);
        Assert.Equal(StatusCodes.Status307TemporaryRedirect, context.Response.StatusCode);
        Assert.Equal("/portal/orders/new?return=%2Fhome", context.Response.Headers.Location.ToString());
    }

    /// <summary>Checks that Apply isolates built-in and host-added fixed cookies and scopes paths to the mount.</summary>
    [Fact]
    public async Task Apply_isolates_fixed_and_additional_cookies_and_scopes_cookie_path()
    {
        var cookies = CreateCookies("/portal", "Portal", "Custom.HostCookie");
        var context = new DefaultHttpContext();
        context.Request.PathBase = "/portal";
        context.Request.Path = "/page";
        context.Request.Headers.Cookie = $"{Constants.Web.PreviewCookieName}=shared-sibling; Custom.HostCookie{cookies.Suffix}=mine";

        string? previewSeen = null;
        string? customSeen = null;
        await cookies.Apply(context, ctx =>
        {
            previewSeen = ctx.Request.Cookies[Constants.Web.PreviewCookieName];
            customSeen = ctx.Request.Cookies["Custom.HostCookie"];
            ctx.Response.Cookies.Append(Constants.Web.PreviewCookieName, "active", new CookieOptions { HttpOnly = true });
            ctx.Response.Cookies.Append("__Host-umbRefreshToken" + cookies.Suffix, "refresh", new CookieOptions { Secure = true, HttpOnly = true });
            ctx.Response.Cookies.Delete("Custom.HostCookie", new CookieOptions { HttpOnly = true });
            return Task.CompletedTask;
        });

        Assert.Null(previewSeen);
        Assert.Equal("mine", customSeen);

        var setCookies = context.Response.Headers.SetCookie.Select(v => SetCookieHeaderValue.Parse(v!)).ToArray();
        var preview = Assert.Single(setCookies, c => c.Name.Value == Constants.Web.PreviewCookieName + cookies.Suffix);
        Assert.Equal("/portal", preview.Path.Value);
        var hostCookie = Assert.Single(setCookies, c => c.Name.Value == "__Host-umbRefreshToken" + cookies.Suffix);
        Assert.Equal("/", hostCookie.Path.Value);
        var deleted = Assert.Single(setCookies, c => c.Name.Value == "Custom.HostCookie" + cookies.Suffix);
        Assert.Equal("/portal", deleted.Path.Value);
        Assert.True(deleted.Expires <= DateTimeOffset.UtcNow);
    }

    /// <summary>Checks that BackOfficeAuthFiles scopes auth keys per installation and varies its content hash.</summary>
    [Fact]
    public void BackOfficeAuthFiles_scopes_auth_keys_and_varies_content_hash_by_installation()
    {
        var firstCookies = CreateCookies("/site-a", "Site A", id: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var secondCookies = CreateCookies("/site-b", "Site B", id: "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        using var warnings = new RecordedWarnings();

        var first = new BackOfficeAuthFiles(assets.Files, firstCookies, warnings);
        var second = new BackOfficeAuthFiles(assets.Files, secondCookies, warnings);

        Assert.NotEqual(first.ContentHash, second.ContentHash);
        var auth = first.GetFileInfo("/umbraco/backoffice/packages/core/auth/index.js");
        Assert.True(auth.Exists);
        using var authReader = new StreamReader(auth.CreateReadStream(), Encoding.UTF8);
        var authContent = authReader.ReadToEnd();
        Assert.Contains($"\"umb:auth{firstCookies.Suffix}\"", authContent);
        Assert.Contains($"\"umb:token-refresh{firstCookies.Suffix}\"", authContent);
        Assert.Contains($"\"umb:pkce{firstCookies.Suffix}\"", authContent);
        Assert.Contains("openInstallationAuthPopup(", authContent);

        var utils = first.GetFileInfo("/umbraco/backoffice/packages/core/utils/index.js");
        Assert.True(utils.Exists);
        using var utilsReader = new StreamReader(utils.CreateReadStream(), Encoding.UTF8);
        Assert.Contains($"\"umb:auth:redirect{firstCookies.Suffix}\"", utilsReader.ReadToEnd());
    }

    private static InstallationCookies CreateCookies(string virtualPath, string siteName, string? extraCookie = null, string? id = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(id is null ? [] : new Dictionary<string, string?> { ["Umbraco:CMS:Global:Id"] = id })
            .Build();
        var options = new InstallationCookiesOptions();
        if (extraCookie is not null) options.AdditionalCookieNames.Add(extraCookie);
        return new InstallationCookies(config, new TestHostingEnvironment(virtualPath, siteName, @"C:\sites\" + siteName), Options.Create(options));
    }
}
