using System.IO.Compression;
using System.Net;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.Configuration;
using Umbraco.Cms.Core.Semver;
using System.Collections.Concurrent;
using Umbraco.Cms.Web.Common.Hosting;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests;

public sealed class MiddlewareTests
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
        app.UsePathBase("/portal");
        if (compression == CompressionOrder.Before) app.UseResponseCompression();
        app.UseBackOfficeAppUnderVirtualPath();
        if (compression == CompressionOrder.After) app.UseResponseCompression();
        app.Run(async context =>
        {
            context.Response.ContentType = context.Request.Path.Value!.EndsWith(".js") ? "application/javascript" : "text/html";
            await context.Response.WriteAsync(context.Request.Path.Value.EndsWith(".js")
                ? "new Request(\"/umbraco/test\"); const r = window.location.origin, o = new Rt(e, r); const x = {backofficePath: \"/umbraco\"};"
                : "<umb-app lang=\"en\"></umb-app>");
        });
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Accept.ParseAdd("text/html");
        http.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip");
        foreach (var path in new[] { "/portal/umbraco", "/portal/umbraco/login/login.js" })
        {
            using var response = await http.GetAsync(Address(app) + path, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(compression == CompressionOrder.Before, response.Content.Headers.ContentEncoding.Contains("gzip"));
            await using var stream = await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);
            using var reader = new StreamReader(compression == CompressionOrder.Before
                ? new GZipStream(stream, CompressionMode.Decompress) : stream);
            var body = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
            Assert.Contains(path.EndsWith(".js") ? "umbHttpClient.setConfig" : "backoffice-path=\"/portal/umbraco\"", body);
            Assert.Contains("/portal", body);
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
        app.UseBackOfficeAppUnderVirtualPath();
        app.Run(async context =>
        {
            context.Response.ContentType = "text/html";
            await context.Response.WriteAsync("<umb-app lang=\"en\"></umb-app>");
        });
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Accept.ParseAdd("text/html");
        if (fetchSite is not null) http.DefaultRequestHeaders.Add("Sec-Fetch-Site", fetchSite);
        if (cookie) http.DefaultRequestHeaders.Add("Cookie", "umbRefreshToken=opaque");
        using var response = await http.GetAsync(Address(app) + "/umbraco" + (callback ? "?code=opaque" : ""), TestContext.Current.CancellationToken);
        Assert.Equal(restore, (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Contains("data-restore-session"));
        Assert.True(response.Headers.CacheControl?.NoStore);
        await app.StopAsync(CancellationToken.None);
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
                context.Response.ContentType = "application/javascript";
                await context.Response.Body.WriteAsync(encoded);
            }
        });
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var http = new HttpClient();
        var url = Address(app) + "/portal/umbraco/login/login.js";
        Assert.Equal(encoded, await http.GetByteArrayAsync(url, TestContext.Current.CancellationToken));
        http.DefaultRequestHeaders.IfNoneMatch.ParseAdd("\"test\"");
        using var response = await http.GetAsync(url, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        await app.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public void Startup_warns_only_for_a_different_Umbraco_version(int patch, bool expectedWarning)
    {
        using var logger = new RecordedLogs();
        using var services = new ServiceCollection().AddLogging(logging => logging.AddProvider(logger))
            .AddSingleton<IUmbracoVersion>(new TestVersion(patch)).BuildServiceProvider();
        ApplicationVirtualPathFixComposer.WarnOnUmbracoVersionMismatch(services);
        Assert.Equal(expectedWarning ? 1 : 0, logger.Warnings.Count);
        if (expectedWarning)
        {
            Assert.Contains("18.3.1", logger.Warnings.Single());
            Assert.Contains("18.3.2", logger.Warnings.Single());
        }
    }

    [Fact]
    public async Task Missing_patterns_are_logged_once_each_across_requests()
    {
        using var logger = new RecordedLogs();
        await using var app = CreateApp(logger);
        app.UsePathBase("/portal");
        app.UseBackOfficeAppUnderVirtualPath();
        app.Run(context => context.Response.WriteAsync("/* upstream changed */"));
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var http = new HttpClient();
        for (var attempt = 0; attempt < 3; attempt++)
            await http.GetStringAsync(Address(app) + "/portal/umbraco/login/login.js", TestContext.Current.CancellationToken);
        Assert.Equal(3, logger.Warnings.Count); // Three distinct login patterns, not nine requests/patterns.
        Assert.Equal(3, logger.Warnings.Distinct().Count());
        Assert.All(logger.Warnings, message => Assert.Contains("login.js", message));
        await app.StopAsync(CancellationToken.None);
    }

    private sealed class TestVersion(int patch) : IUmbracoVersion
    {
        public Version Version => new(18, 3, patch);
        public string Comment => "";
        public Version AssemblyVersion => Version;
        public Version AssemblyFileVersion => Version;
        public SemVersion SemanticVersion => new(18, 3, patch);
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

    private static WebApplication CreateApp(ILoggerProvider? logger = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        if (logger is not null) builder.Logging.AddProvider(logger);
        builder.Services.AddSingleton<IBackOfficePathGenerator, Paths>();
        builder.Services.Configure<GlobalSettings>(options => options.UseHttps = false);
#pragma warning disable CS0618
        builder.Services.Configure<BackOfficeTokenCookieSettings>(options => { options.SameSite = "Strict"; options.SiteName = ""; });
#pragma warning restore CS0618
        builder.Services.AddResponseCompression();
        return builder.Build();
    }

    private static string Address(WebApplication app)
        => app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();

    private sealed class Paths : IBackOfficePathGenerator
    {
        public string BackOfficePath => "/portal/umbraco";
        public string BackOfficeVirtualDirectory => BackOfficePath + "/backoffice";
        public string BackOfficeCacheBustHash => "hash";
        public string BackOfficeAssetsPath => BackOfficeVirtualDirectory + "/" + BackOfficeCacheBustHash;
    }
}

