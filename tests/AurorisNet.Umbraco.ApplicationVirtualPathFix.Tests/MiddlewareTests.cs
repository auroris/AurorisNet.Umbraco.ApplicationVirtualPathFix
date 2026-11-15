using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Umbraco.Cms.Core.Configuration;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.Semver;
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
    public async Task Patched_assets_and_rendered_html_use_normal_response_compression(CompressionOrder compression)
    {
        await using var app = CreateApp();
        app.Environment.WebRootFileProvider = new BackOfficeAssetFileProvider(assets.Files, new Paths(), "/portal", NullLogger.Instance);
        app.UsePathBase("/portal");
        if (compression == CompressionOrder.Before) app.UseResponseCompression();
        app.UseUmbracoPathsUnderPathBase();
        if (compression == CompressionOrder.After) app.UseResponseCompression();
        app.UseUmbracoBackOfficeRewrites();
        app.UseStaticFiles();
        app.Run(async context =>
        {
            context.Response.ContentType = "text/html";
            var html = await BackOfficeBodyTests.RenderBodyAsync(context, "<umb-app lang=\"en\">café</umb-app>");
            await context.Response.WriteAsync(html);
        });
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Accept.ParseAdd("text/html");
        http.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip");
        foreach (var (path, expected) in new[]
        {
            ("/portal/umbraco", "backoffice-path=\"/portal/umbraco\" lang=\"en\">café</umb-app>"),
            ("/portal/umbraco/login/login.js", "umbHttpClient.setConfig"),
            ("/portal/umbraco/backoffice/hash/packages/core/manifests.js", "/portal/umbraco/backoffice/hash/"),
            ("/portal/umbraco/backoffice/hash/css/umb-css.css", "url(../assets/"),
        })
        {
            using var response = await http.GetAsync(Address(app) + path, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var compressed = compression != CompressionOrder.None;
            Assert.Equal(compressed, response.Content.Headers.ContentEncoding.Contains("gzip"));
            await using var stream = await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);
            using var reader = new StreamReader(compressed ? new GZipStream(stream, CompressionMode.Decompress) : stream);
            Assert.Contains(expected, await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
        }
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
    public async Task Temporary_path_change_is_undone_even_when_downstream_throws(bool throws)
    {
        await using var app = CreateApp();
        var context = new DefaultHttpContext();
        const string originalPath = "/umbraco/basic-auth";
        context.Request.PathBase = "/portal";
        context.Request.Path = originalPath;
        var failure = new InvalidOperationException("Downstream failure");
        app.UseUmbracoPathsUnderPathBase();
        app.Run(request =>
        {
            Assert.Equal("/portal" + originalPath, request.Request.Path.Value);
            Assert.Equal("/portal", request.Request.PathBase.Value);
            if (throws) throw failure;
            return Task.CompletedTask;
        });
        var pipeline = ((IApplicationBuilder)app).Build();
        if (throws)
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline(context)));
        else
            await pipeline(context);
        Assert.Equal(originalPath, context.Request.Path.Value);
        Assert.Equal("/portal", context.Request.PathBase.Value);
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

    private static WebApplication CreateApp()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IBackOfficePathGenerator, Paths>();
        builder.Services.AddControllersWithViews();
        builder.Services.AddTransient<ITagHelperComponent, BackOfficeBodyTagHelperComponent>();
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
