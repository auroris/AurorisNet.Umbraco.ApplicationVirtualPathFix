using Microsoft.AspNetCore.Mvc.Razor.TagHelpers;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Web.Common.Hosting;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests;

/// <summary>Checks the body Tag Helper component through ASP.NET Core's real <see cref="BodyTagHelper"/>.</summary>
public sealed class BackOfficeBodyTests
{
    private const string BackOfficeView = "/umbraco/UmbracoBackOffice/Index.cshtml";
    private const string AppHtml = "<umb-app lang=\"fr\" keep-user-logged-in>café</umb-app>";

    /// <summary>
    /// Checks that the backoffice page keeps its markup and gets no-store caching, and that under a virtual path it gets
    /// HTML-encoded <c>data-path-base</c> and <c>backoffice-path</c> attributes. The page never contains the request's
    /// host or a <c>server-url</c> attribute.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("/portal")]
    [InlineData("/a/b")]
    [InlineData("/a&b")]
    public async Task Backoffice_attributes_name_the_path_base_and_preserve_existing_markup(string prefix)
    {
        using var services = CreateServices(prefix);
        var context = CreateContext(services, prefix);
        context.Request.Scheme = "https";
        var html = await RenderBodyAsync(context, AppHtml);
        Assert.Contains("lang=\"fr\" keep-user-logged-in>café</umb-app>", html);
        // The browser supplies the origin, so the Host header never reaches the page.
        Assert.DoesNotContain("server-url", html);
        Assert.DoesNotContain("site.test", html);
        if (prefix.Length == 0)
        {
            Assert.DoesNotContain("data-path-base", html);
            Assert.DoesNotContain("backoffice-path", html);
        }
        else
        {
            var encoded = System.Text.Encodings.Web.HtmlEncoder.Default.Encode(prefix);
            Assert.Contains($"data-path-base=\"{encoded}\"", html);
            Assert.Contains($"backoffice-path=\"{encoded}/umbraco\"", html);
        }
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
    }

    /// <summary>
    /// Checks that <c>data-restore-session</c> appears when the refresh cookie arrives, or with strict cookies when a
    /// cross-site navigation may have withheld it, but not on an OAuth callback without the cookie.
    /// </summary>
    [Theory]
    [InlineData(false, "none", false, false)]
    [InlineData(true, "same-origin", false, true)]
    [InlineData(false, "cross-site", false, true)]
    [InlineData(false, "cross-site", true, false)]
    [InlineData(false, null, false, false)]
    [InlineData(true, null, false, true)]
    public async Task Session_hint_handles_cookies_and_cross_site_callbacks(bool cookie, string? fetchSite, bool callback, bool restore)
    {
        using var services = CreateServices("");
        var context = CreateContext(services, "");
        if (fetchSite is not null) context.Request.Headers["Sec-Fetch-Site"] = fetchSite;
        if (cookie) context.Request.Headers.Cookie = "umbRefreshToken=opaque";
        if (callback) context.Request.QueryString = new QueryString("?code=opaque");
        await AssertSessionHintAsync(context, restore);
    }

    /// <summary>
    /// Checks that the session hint looks for the refresh cookie under its configured name, with the <c>__Host-</c>
    /// prefix over HTTPS.
    /// </summary>
    [Theory]
    [InlineData(false, "umbRefreshTokentenant", true)]
    [InlineData(false, "umbRefreshToken", false)]
    [InlineData(true, "__Host-umbRefreshTokentenant", true)]
    [InlineData(true, "umbRefreshTokentenant", false)]
    [InlineData(true, "__Host-umbRefreshTokenother", false)]
    public async Task Session_hint_uses_the_configured_cookie_name_and_https_prefix(bool https, string cookie, bool restore)
    {
        using var services = CreateServices(siteName: "tenant");
        var context = CreateContext(services);
        context.Request.Scheme = https ? "https" : "http";
        context.Request.Headers.Cookie = cookie + "=opaque";
        await AssertSessionHintAsync(context, restore);
    }

    /// <summary>
    /// Checks that with Lax or None cookies, a cross-site navigation restores the session only when the cookie arrives,
    /// because browsers send those cookies on such navigations.
    /// </summary>
    [Theory]
    [InlineData(SameSiteMode.Lax, false)]
    [InlineData(SameSiteMode.Lax, true)]
    [InlineData(SameSiteMode.None, false)]
    [InlineData(SameSiteMode.None, true)]
    public async Task Non_strict_cross_site_navigation_relies_on_cookie_presence(SameSiteMode sameSite, bool cookie)
    {
        using var services = CreateServices(sameSite: sameSite);
        var context = CreateContext(services);
        context.Request.Scheme = "https";
        context.Request.Headers["Sec-Fetch-Site"] = "cross-site";
        if (cookie) context.Request.Headers.Cookie = "__Host-umbRefreshToken=opaque";
        await AssertSessionHintAsync(context, cookie);
    }

    /// <summary>
    /// Checks that the basic-auth login and two-factor forms post under the virtual path once, keeping their fields and
    /// query strings, and that a site at the root keeps its forms as they are.
    /// </summary>
    [Theory]
    [InlineData("Login", "/portal")]
    [InlineData("TwoFactor", "/a/b")]
    [InlineData("Login", "")]
    public async Task Basic_auth_forms_have_one_prefix_and_keep_fields_and_query_strings(string view, string prefix)
    {
        using var services = CreateServices(prefix);
        var context = CreateContext(services, prefix);
        context.Request.Method = "POST";
        var html = $"<form action=\"{prefix}{prefix}/umbraco/basic-auth/login?returnUrl=%2F&amp;x=1\"><input name=\"__RequestVerificationToken\" value=\"opaque\"></form>";
        var expected = $"<form action=\"{prefix}/umbraco/basic-auth/login?returnUrl=%2F&amp;x=1\"><input name=\"__RequestVerificationToken\" value=\"opaque\"></form>";
        Assert.Equal(expected, await RenderBodyAsync(context, html, $"/umbraco/BasicAuthLogin/{view}.cshtml"));
    }

    /// <summary>
    /// Checks that the component ignores the backoffice page's head and the body of every other view, including
    /// Umbraco's login page: it reads no content and sets no headers.
    /// </summary>
    [Theory]
    [InlineData(BackOfficeView, "head")]
    [InlineData("/umbraco/UmbracoLogin/Index.cshtml", "body")]
    [InlineData("/Views/Home.cshtml", "body")]
    [InlineData("/Views/CustomBackOffice.cshtml", "body")]
    public async Task Other_views_and_head_content_are_not_processed(string view, string tag)
    {
        using var services = CreateServices();
        var context = CreateContext(services);
        var output = new TagHelperOutput(tag, [], (_, _) => throw new InvalidOperationException("Must not read unrelated content."));
        await ProcessAsync(context, view, output);
        Assert.False(output.IsContentModified);
        Assert.Empty(context.Response.Headers);
    }

    /// <summary>
    /// Checks that the component builds on content another component already changed, and keeps the output's
    /// pre-content and post-content.
    /// </summary>
    [Fact]
    public async Task Existing_component_changes_are_preserved()
    {
        using var services = CreateServices();
        var context = CreateContext(services);
        var output = new TagHelperOutput("body", [], (_, _) => throw new InvalidOperationException("Must use the already modified content."));
        output.Content.SetHtmlContent(AppHtml + "<p>Added by another component</p>");
        output.PreContent.AppendHtml("<header>Before</header>");
        output.PostContent.AppendHtml("<footer>After</footer>");
        await ProcessAsync(context, BackOfficeView, output);
        Assert.Contains("data-path-base=\"/portal\"", output.Content.GetContent());
        Assert.Contains("<p>Added by another component</p>", output.Content.GetContent());
        Assert.Equal("<header>Before</header>", output.PreContent.GetContent());
        Assert.Equal("<footer>After</footer>", output.PostContent.GetContent());
    }

    /// <summary>
    /// Renders the backoffice body for <paramref name="context"/> and checks that <c>data-restore-session</c> appears
    /// exactly when <paramref name="restore"/> is <see langword="true"/>, that no cookie value enters the page, and that
    /// the response is marked <c>no-store</c>.
    /// </summary>
    private static async Task AssertSessionHintAsync(HttpContext context, bool restore)
    {
        var html = await RenderBodyAsync(context, AppHtml);
        Assert.Equal(restore, html.Contains("data-restore-session"));
        Assert.DoesNotContain("opaque", html);
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
    }

    /// <summary>
    /// Runs <paramref name="html"/> through the body Tag Helper as the body of <paramref name="view"/> and returns the
    /// resulting content.
    /// </summary>
    /// <remarks>
    /// Uses ASP.NET Core's real <see cref="BodyTagHelper"/>, so the component comes from dependency injection and
    /// receives its view context as it does in a view. The browser suite covers Umbraco's compiled views; these checks
    /// need no server or Razor view of their own.
    /// </remarks>
    internal static async Task<string> RenderBodyAsync(HttpContext context, string html, string view = BackOfficeView)
    {
        var output = new TagHelperOutput("body", [], (_, _) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent().SetHtmlContent(html)));
        await ProcessAsync(context, view, output);
        if (!output.IsContentModified) output.Content.SetHtmlContent(await output.GetChildContentAsync());
        return output.Content.GetContent();
    }

    /// <summary>
    /// Runs <see cref="BodyTagHelper"/> over <paramref name="output"/> as if <paramref name="view"/> were rendering it.
    /// </summary>
    private static async Task ProcessAsync(HttpContext context, string view, TagHelperOutput output)
    {
        var helper = new BodyTagHelper(context.RequestServices.GetRequiredService<ITagHelperComponentManager>(),
            context.RequestServices.GetRequiredService<ILoggerFactory>())
        {
            ViewContext = new ViewContext { HttpContext = context, ExecutingFilePath = view },
        };
        var tagContext = new TagHelperContext(output.TagName, [], new Dictionary<object, object>(), "test-body");
        helper.Init(tagContext);
        await helper.ProcessAsync(tagContext, output);
    }

    /// <summary>
    /// Builds the services the component needs: MVC's Tag Helper infrastructure, backoffice paths under
    /// <paramref name="prefix"/>, and refresh-cookie settings with <paramref name="siteName"/> and
    /// <paramref name="sameSite"/>.
    /// </summary>
    private static ServiceProvider CreateServices(string prefix = "/portal", string siteName = "", SameSiteMode sameSite = SameSiteMode.Strict)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllersWithViews();
        services.AddTransient<ITagHelperComponent, BackOfficeBodyTagHelperComponent>();
        services.AddSingleton<IBackOfficePathGenerator>(new TestBackOfficePaths(prefix));
        services.Configure<GlobalSettings>(options => options.UseHttps = false);
#pragma warning disable CS0618
        services.Configure<BackOfficeTokenCookieSettings>(options => { options.SameSite = sameSite.ToString(); options.SiteName = siteName; });
#pragma warning restore CS0618
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Creates an HTTP request for <c>/umbraco</c> on <c>site.test</c> with <paramref name="prefix"/> as its path base.
    /// </summary>
    private static DefaultHttpContext CreateContext(IServiceProvider services, string prefix = "/portal")
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("site.test");
        context.Request.PathBase = prefix;
        context.Request.Path = "/umbraco";
        return context;
    }
}
