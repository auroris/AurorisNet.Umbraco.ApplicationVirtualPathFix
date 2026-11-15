using Microsoft.AspNetCore.Mvc.Razor.TagHelpers;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Web.Common.Hosting;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests;

public sealed class BackOfficeBodyTests
{
    private const string BackOfficeView = "/umbraco/UmbracoBackOffice/Index.cshtml";
    private const string AppHtml = "<umb-app lang=\"fr\" keep-user-logged-in>café</umb-app>";

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

    private static async Task AssertSessionHintAsync(HttpContext context, bool restore)
    {
        var html = await RenderBodyAsync(context, AppHtml);
        Assert.Equal(restore, html.Contains("data-restore-session"));
        Assert.DoesNotContain("opaque", html);
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
    }

    // Use the real BodyTagHelper, including DI and ViewContext property activation. The browser
    // suite exercises Umbraco's compiled views; these checks need no server or custom Razor view.
    internal static async Task<string> RenderBodyAsync(HttpContext context, string html, string view = BackOfficeView)
    {
        var output = new TagHelperOutput("body", [], (_, _) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent().SetHtmlContent(html)));
        await ProcessAsync(context, view, output);
        if (!output.IsContentModified) output.Content.SetHtmlContent(await output.GetChildContentAsync());
        return output.Content.GetContent();
    }

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

    private static ServiceProvider CreateServices(string prefix = "/portal", string siteName = "", SameSiteMode sameSite = SameSiteMode.Strict)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllersWithViews();
        services.AddTransient<ITagHelperComponent, BackOfficeBodyTagHelperComponent>();
        services.AddSingleton<IBackOfficePathGenerator>(new Paths(prefix));
        services.Configure<GlobalSettings>(options => options.UseHttps = false);
#pragma warning disable CS0618
        services.Configure<BackOfficeTokenCookieSettings>(options => { options.SameSite = sameSite.ToString(); options.SiteName = siteName; });
#pragma warning restore CS0618
        return services.BuildServiceProvider();
    }

    private static DefaultHttpContext CreateContext(IServiceProvider services, string prefix = "/portal")
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("site.test");
        context.Request.PathBase = prefix;
        context.Request.Path = "/umbraco";
        return context;
    }

    private sealed class Paths(string prefix) : IBackOfficePathGenerator
    {
        public string BackOfficePath => prefix + "/umbraco";
        public string BackOfficeVirtualDirectory => BackOfficePath + "/backoffice";
        public string BackOfficeCacheBustHash => "hash";
        public string BackOfficeAssetsPath => BackOfficeVirtualDirectory + "/" + BackOfficeCacheBustHash;
    }
}
