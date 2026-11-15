using System.IO.Compression;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Logging.Abstractions;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.Configuration;
using Umbraco.Cms.Core.Semver;
using System.Collections.Concurrent;
using Umbraco.Cms.Web.Common.Hosting;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests;

public sealed class MiddlewareTests(AssetPatchTests.Assets assets) : IClassFixture<AssetPatchTests.Assets>
{
    public enum CompressionOrder { None, Before, After }

    [Theory]
    [InlineData("/PORTAL/umbraco?code=a%2Fb&state=x", "text/html", true)]
    [InlineData("/PORTAL/umbraco/backoffice/hash/example.js", "application/javascript", false)]
    [InlineData("/PORTAL/content", "text/html", false)]
    public async Task Only_backoffice_navigations_canonicalize_prefix_casing(string path, string accept, bool redirect)
    {
        await using var app = CreateApp();
        app.UsePathBase("/portal");
        app.UseUmbracoPathsUnderPathBase();
        app.Run(context => context.Response.WriteAsync("Reached the handler"));
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        http.DefaultRequestHeaders.Accept.ParseAdd(accept);
        using var response = await http.GetAsync(Address(app) + path, TestContext.Current.CancellationToken);
        Assert.Equal(redirect ? HttpStatusCode.Found : HttpStatusCode.OK, response.StatusCode);
        if (redirect) Assert.Equal("/portal/umbraco?code=a%2Fb&state=x", response.Headers.Location?.OriginalString);
        await app.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData(CompressionOrder.None)]
    [InlineData(CompressionOrder.Before)]
    [InlineData(CompressionOrder.After)]
    public async Task Rewrites_remain_valid_with_response_compression(CompressionOrder compression)
    {
        await using var app = CreateApp();
        app.Environment.WebRootFileProvider = new BackOfficeAssetFileProvider(assets.Files, new Paths(), "/portal", NullLogger.Instance);
        app.UsePathBase("/portal");
        if (compression == CompressionOrder.Before) app.UseResponseCompression();
        app.UseUmbracoPathsUnderPathBase();
        app.UseBackOfficeAppUnderVirtualPath();
        if (compression == CompressionOrder.After) app.UseResponseCompression();
        app.UseUmbracoBackOfficeRewrites();
        app.UseStaticFiles();
        app.Run(async context =>
        {
            context.Response.ContentType = "text/html";
            await context.Response.WriteAsync("<umb-app lang=\"en\"></umb-app>");
        });
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Accept.ParseAdd("text/html");
        http.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip");
        foreach (var (path, expected) in new[]
        {
            ("/portal/umbraco", "backoffice-path=\"/portal/umbraco\""),
            ("/portal/umbraco/login/login.js", "umbHttpClient.setConfig"),
            ("/portal/umbraco/backoffice/hash/packages/core/manifests.js", "/portal/umbraco/backoffice/hash/"),
            ("/portal/umbraco/backoffice/hash/css/umb-css.css", "url(../assets/"),
        })
        {
            using var response = await http.GetAsync(Address(app) + path, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var compressed = compression == CompressionOrder.Before || (compression == CompressionOrder.After && path != "/portal/umbraco");
            Assert.Equal(compressed, response.Content.Headers.ContentEncoding.Contains("gzip"));
            await using var stream = await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);
            using var reader = new StreamReader(compressed
                ? new GZipStream(stream, CompressionMode.Decompress) : stream);
            var body = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
            Assert.Contains(expected, body);
        }
        await app.StopAsync(CancellationToken.None);
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
        await using var app = CreateApp();
        using var body = new MemoryStream();
        var context = CreateContext(app, body);
        context.Request.PathBase = PathString.Empty;
        if (fetchSite is not null) context.Request.Headers["Sec-Fetch-Site"] = fetchSite;
        if (cookie) context.Request.Headers.Cookie = "umbRefreshToken=opaque";
        if (callback) context.Request.QueryString = new QueryString("?code=opaque");
        await AssertSessionHintAsync(app, context, body, restore);
    }

    [Fact]
    public async Task Conditional_responses_and_preencoded_content_are_not_rewritten()
    {
        await using var app = CreateApp();
        app.UsePathBase("/portal");
        app.UseBackOfficeAppUnderVirtualPath();
        byte[] encoded = [0x1f, 0x8b, 0x00, 0xff, 0xfe];
        app.Run(async context =>
        {
            if (context.Request.Headers.IfNoneMatch == "\"test\"")
                context.Response.StatusCode = StatusCodes.Status304NotModified;
            else
            {
                context.Response.Headers.ContentEncoding = "gzip";
                context.Response.ContentType = "text/html";
                await context.Response.Body.WriteAsync(encoded);
            }
        });
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Accept.ParseAdd("text/html");
        var url = Address(app) + "/portal/umbraco";
        Assert.Equal(encoded, await http.GetByteArrayAsync(url, TestContext.Current.CancellationToken));
        http.DefaultRequestHeaders.IfNoneMatch.ParseAdd("\"test\"");
        using var response = await http.GetAsync(url, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        await app.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Startup_warns_only_for_a_different_Umbraco_version(bool differentVersion)
    {
        var installed = new UmbracoVersion().Version;
        var actual = differentVersion ? new Version(installed.Major, installed.Minor, installed.Build + 1) : installed;
        using var logger = new RecordedLogs();
        using var services = new ServiceCollection().AddLogging(logging => logging.AddProvider(logger))
            .AddSingleton<IUmbracoVersion>(new TestVersion(actual)).BuildServiceProvider();
        ApplicationVirtualPathFixComposer.WarnOnUmbracoVersionMismatch(services);
        Assert.Equal(differentVersion ? 1 : 0, logger.Warnings.Count);
        if (differentVersion)
        {
            Assert.Contains(installed.ToString(), logger.Warnings.Single());
            Assert.Contains(actual.ToString(), logger.Warnings.Single());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Temporary_middleware_changes_are_undone_even_when_downstream_throws(bool throws)
    {
        await using var app = CreateApp();
        using var body = new MemoryStream();
        var context = CreateContext(app, body);
        const string originalPath = "/umbraco/basic-auth";
        context.Request.Path = originalPath;
        context.Request.Headers.AcceptEncoding = new Microsoft.Extensions.Primitives.StringValues(["gzip", "br"]);
        var failure = new InvalidOperationException("Downstream failure");
        app.UseUmbracoPathsUnderPathBase();
        app.UseBackOfficeAppUnderVirtualPath();
        app.Run(async request =>
        {
            Assert.Equal("/portal" + originalPath, request.Request.Path.Value);
            Assert.Equal("/portal", request.Request.PathBase.Value);
            Assert.NotSame(body, request.Response.Body);
            Assert.Equal(0, request.Request.Headers.AcceptEncoding.Count);
            request.Response.ContentType = "text/html";
            await request.Response.WriteAsync("<form action=\"/portal/portal/umbraco/basic-auth\"></form>");
            if (throws) throw failure;
        });

        var pipeline = ((IApplicationBuilder)app).Build();
        if (throws)
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline(context)));
        else
            await pipeline(context);

        Assert.Equal(originalPath, context.Request.Path.Value);
        Assert.Equal("/portal", context.Request.PathBase.Value);
        Assert.Equal(new[] { "gzip", "br" }, context.Request.Headers.AcceptEncoding.ToArray());
        Assert.Same(body, context.Response.Body);
        Assert.Equal(throws ? "" : "<form action=\"/portal/umbraco/basic-auth\"></form>", Encoding.UTF8.GetString(body.ToArray()));
    }

    [Theory]
    [InlineData("/umbraco/management/api/test", "text/html", "GET", false)]
    [InlineData("/umbraco/delivery/api/test", "text/html", "GET", false)]
    [InlineData("/umbraco/serverEventHub", "text/event-stream", "GET", false)]
    [InlineData("/umbraco/backoffice/unregistered.js", "application/javascript", "GET", false)]
    [InlineData("/umbraco/backoffice/css/umb-css.css", "text/html", "GET", false)]
    [InlineData("/umbraco/umbraco/backoffice/hash/css/umb-css.css", "text/html", "GET", false, "/umbraco")]
    [InlineData("/umbraco/login/login.js", "text/html", "GET", false)]
    [InlineData("/umbraco/website/nonodes.css", "text/html", "GET", false)]
    [InlineData("/content", "text/html", "GET", false)]
    [InlineData("/umbraco", "text/html", "POST", false)]
    [InlineData("/umbraco", "text/html", "GET", true)]
    public async Task Unrelated_requests_keep_the_original_stream_and_headers(
        string path, string accept, string method, bool upgrade, string prefix = "/portal")
    {
        await using var app = CreateApp(configure: services => services.AddSingleton<IBackOfficePathGenerator>(new Paths(prefix)));
        using var body = new MemoryStream();
        var context = CreateContext(app, body);
        context.Request.Path = path;
        context.Request.PathBase = prefix;
        context.Request.Method = method;
        context.Request.Headers.Accept = accept;
        context.Request.Headers.AcceptEncoding = "gzip";
        if (upgrade) context.Request.Headers.Upgrade = "websocket";
        byte[] bytes = [0x80, 0xff];
        app.UseBackOfficeAppUnderVirtualPath();
        app.Run(async request =>
        {
            Assert.Same(body, request.Response.Body);
            Assert.Equal("gzip", request.Request.Headers.AcceptEncoding.ToString());
            await request.Response.Body.WriteAsync(bytes);
        });

        await ((IApplicationBuilder)app).Build()(context);

        Assert.Equal(bytes, body.ToArray());
    }

    [Fact]
    public async Task Rewritten_content_length_counts_utf8_bytes()
    {
        await using var app = CreateApp();
        using var body = new MemoryStream();
        var context = CreateContext(app, body);
        const string html = "<umb-app lang=\"en\">café</umb-app>";
        app.UseBackOfficeAppUnderVirtualPath();
        app.Run(async request =>
        {
            request.Response.ContentType = "text/html";
            request.Response.ContentLength = Encoding.UTF8.GetByteCount(html);
            await request.Response.WriteAsync(html);
        });

        await ((IApplicationBuilder)app).Build()(context);

        var bytes = body.ToArray();
        var rewritten = Encoding.UTF8.GetString(bytes);
        Assert.Contains("server-url=\"http://site.test/portal\"", rewritten);
        Assert.Contains("café", rewritten);
        Assert.True(bytes.Length > Encoding.UTF8.GetByteCount(html));
        Assert.Equal(bytes.LongLength, context.Response.ContentLength);
    }

    [Theory]
    [InlineData(false, "umbRefreshTokentenant", true)]
    [InlineData(false, "umbRefreshToken", false)]
    [InlineData(true, "__Host-umbRefreshTokentenant", true)]
    [InlineData(true, "umbRefreshTokentenant", false)]
    [InlineData(true, "__Host-umbRefreshTokenother", false)]
    public async Task Session_hint_uses_the_configured_cookie_name_and_https_prefix(bool https, string cookie, bool restore)
    {
        await using var app = CreateApp(configure: services =>
        {
#pragma warning disable CS0618
            services.Configure<BackOfficeTokenCookieSettings>(options => options.SiteName = "tenant");
#pragma warning restore CS0618
        });
        using var body = new MemoryStream();
        var context = CreateContext(app, body);
        context.Request.Scheme = https ? "https" : "http";
        context.Request.Headers.Cookie = cookie + "=opaque";
        await AssertSessionHintAsync(app, context, body, restore);
    }

    [Theory]
    [InlineData(SameSiteMode.Lax, false)]
    [InlineData(SameSiteMode.Lax, true)]
    [InlineData(SameSiteMode.None, false)]
    [InlineData(SameSiteMode.None, true)]
    public async Task Non_strict_cross_site_navigation_relies_on_cookie_presence(SameSiteMode sameSite, bool cookie)
    {
        await using var app = CreateApp(configure: services =>
        {
#pragma warning disable CS0618
            services.Configure<BackOfficeTokenCookieSettings>(options => options.SameSite = sameSite.ToString());
#pragma warning restore CS0618
        });
        using var body = new MemoryStream();
        var context = CreateContext(app, body);
        context.Request.Scheme = "https";
        context.Request.Headers["Sec-Fetch-Site"] = "cross-site";
        if (cookie) context.Request.Headers.Cookie = "__Host-umbRefreshToken=opaque";
        await AssertSessionHintAsync(app, context, body, cookie);
    }

    private static async Task AssertSessionHintAsync(WebApplication app, HttpContext context, MemoryStream body, bool restore)
    {
        app.UseBackOfficeAppUnderVirtualPath();
        app.Run(request =>
        {
            request.Response.ContentType = "text/html";
            return request.Response.WriteAsync("<umb-app lang=\"en\"></umb-app>");
        });
        await ((IApplicationBuilder)app).Build()(context);
        Assert.Equal(restore, Encoding.UTF8.GetString(body.ToArray()).Contains("data-restore-session"));
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
    }

    private static DefaultHttpContext CreateContext(WebApplication app, Stream body)
    {
        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.Response.Body = body;
        context.Request.Method = "GET";
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("site.test");
        context.Request.PathBase = "/portal";
        context.Request.Path = "/umbraco";
        context.Request.Headers.Accept = "text/html";
        return context;
    }

    private sealed class TestVersion(Version version) : IUmbracoVersion
    {
        public Version Version => version;
        public string Comment => "";
        public Version AssemblyVersion => Version;
        public Version AssemblyFileVersion => Version;
        public SemVersion SemanticVersion => new(version.Major, version.Minor, version.Build);
    }

    private sealed class RecordedLogs : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<string> Warnings { get; } = new();
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level == LogLevel.Warning;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (level == LogLevel.Warning) Warnings.Enqueue(formatter(state, exception));
        }
        public void Dispose() { }
    }

    private static WebApplication CreateApp(Action<IServiceCollection>? configure = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IBackOfficePathGenerator, Paths>();
        builder.Services.Configure<GlobalSettings>(options => options.UseHttps = false);
#pragma warning disable CS0618
        builder.Services.Configure<BackOfficeTokenCookieSettings>(options => { options.SameSite = "Strict"; options.SiteName = ""; });
#pragma warning restore CS0618
        builder.Services.AddResponseCompression();
        configure?.Invoke(builder.Services);
        return builder.Build();
    }

    private static string Address(WebApplication app)
        => app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();

    private sealed class Paths(string prefix = "/portal") : IBackOfficePathGenerator
    {
        public string BackOfficePath => prefix + "/umbraco";
        public string BackOfficeVirtualDirectory => BackOfficePath + "/backoffice";
        public string BackOfficeCacheBustHash => "hash";
        public string BackOfficeAssetsPath => BackOfficeVirtualDirectory + "/" + BackOfficeCacheBustHash;
    }
}

