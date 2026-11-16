using System.IO.Compression;
using System.Net;
using AurorisNet.Umbraco.ApplicationVirtualPathFix.Assets;
using AurorisNet.Umbraco.ApplicationVirtualPathFix.BackOffice;
using AurorisNet.Umbraco.ApplicationVirtualPathFix.Hosting;
using AurorisNet.Umbraco.ApplicationVirtualPathFix.UmbracoVersions;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Umbraco.Cms.Core.Configuration;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Web.Common.Hosting;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests;

/// <summary>Checks request paths in memory, response compression over HTTP, and the startup version warning.</summary>
public sealed class MiddlewareTests(AssetPatchTests.Assets assets) : IClassFixture<AssetPatchTests.Assets>
{
    /// <summary>
    /// Checks that only an HTML request under <c>/umbraco</c> with a differently cased prefix is redirected to the
    /// configured prefix, keeping its query string; scripts and site pages pass through.
    /// </summary>
    [Theory]
    [InlineData("/PORTAL/umbraco?code=a%2Fb&state=x", "text/html", true)]
    [InlineData("/PORTAL/umbraco/backoffice/hash/example.js", "application/javascript", false)]
    [InlineData("/PORTAL/content", "text/html", false)]
    public async Task Only_backoffice_navigations_canonicalize_prefix_casing(string path, string accept, bool redirect)
    {
        using var services = new ServiceCollection()
            .AddSingleton<IBackOfficePathGenerator>(new TestBackOfficePaths("/portal")).BuildServiceProvider();
        var app = new ApplicationBuilder(services);
        app.UsePathBase("/portal");
        app.UseUmbracoPathsUnderPathBase(UmbracoUnderTest.Major);
        var reachedHandler = false;
        app.Run(_ => { reachedHandler = true; return Task.CompletedTask; });
        var url = new Uri("http://site.test" + path);
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = url.AbsolutePath;
        context.Request.QueryString = new QueryString(url.Query);
        context.Request.Headers.Accept = accept;
        await app.Build()(context);
        Assert.Equal(!redirect, reachedHandler);
        Assert.Equal(redirect ? StatusCodes.Status302Found : StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(redirect ? "/portal/umbraco?code=a%2Fb&state=x" : "", context.Response.Headers.Location.ToString());
    }

    /// <summary>
    /// Checks that patched assets and the rendered backoffice page arrive intact through normal response compression.
    /// File-provider and body tests cover their uncompressed contents separately.
    /// </summary>
    [Fact]
    public async Task Patched_assets_and_rendered_html_use_normal_response_compression()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IBackOfficePathGenerator>(new TestBackOfficePaths("/portal"));
        builder.Services.AddControllersWithViews();
        builder.Services.AddTransient<ITagHelperComponent, BackOfficeBodyTagHelperComponent>();
        builder.Services.Configure<GlobalSettings>(options => options.UseHttps = false);
#pragma warning disable CS0618
        builder.Services.Configure<BackOfficeTokenCookieSettings>(options => { options.SameSite = "Strict"; options.SiteName = ""; });
#pragma warning restore CS0618
        builder.Services.AddResponseCompression();
        await using var app = builder.Build();
        app.Environment.WebRootFileProvider = new BackOfficeAssetFileProvider(assets.Files, new TestBackOfficePaths("/portal"), "/portal",
            UmbracoUnderTest.Release, NullLogger.Instance);
        app.UsePathBase("/portal");
        app.UseResponseCompression();
        app.UseUmbracoPathsUnderPathBase(UmbracoUnderTest.Major);
        app.UseUmbracoBackOfficeRewrites();
        app.UseStaticFiles();
        app.Run(async context =>
        {
            context.Response.ContentType = "text/html";
            var html = await BackOfficeBodyTests.RenderBodyAsync(context, "<umb-app lang=\"en\">café</umb-app>");
            await context.Response.WriteAsync(html);
        });
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var http = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
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
            using var response = await http.GetAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("gzip", response.Content.Headers.ContentEncoding);
            await using var stream = await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);
            using var reader = new StreamReader(new GZipStream(stream, CompressionMode.Decompress));
            Assert.Contains(expected, await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
        }
        await app.StopAsync(CancellationToken.None);
    }

    /// <summary>The release under test and unlisted versions above, below and inside the dependency range.</summary>
    public static TheoryData<Version, bool> StartupVersions
    {
        get
        {
            var supported = UmbracoUnderTest.SupportedReleases;
            return new()
            {
                { UmbracoUnderTest.Release, false },
                { new Version(supported[^1].Major, supported[^1].Minor, supported[^1].Build + 1), true },
                { new Version(supported[0].Major - 1, 0, 0), true },
                { new Version(17, 99, 0), true },
            };
        }
    }

    /// <summary>
    /// Checks that startup logs one warning naming the supported releases and the site's release when the site runs an
    /// unlisted Umbraco release, including one inside the dependency range, and none for the release under test.
    /// </summary>
    [Theory]
    [MemberData(nameof(StartupVersions))]
    public void Startup_warns_only_for_an_unsupported_Umbraco_release(Version release, bool warns)
    {
        using var logger = new RecordedWarnings();
        using var services = new ServiceCollection().AddLogging(logging => logging.AddProvider(logger))
            .AddSingleton<IUmbracoVersion>(new TestUmbracoVersion(release)).BuildServiceProvider();
        ApplicationVirtualPathFixComposer.WarnOnUnsupportedUmbracoVersion(services);
        Assert.Equal(warns ? 1 : 0, logger.Messages.Count);
        if (warns)
        {
            var message = Assert.Single(logger.Messages);
            Assert.Contains(ApplicationVirtualPathFixComposer.SupportedUmbracoVersions, message);
            Assert.Contains(release.ToString(), message);
        }
    }

    /// <summary>
    /// Checks that the middleware gives the rest of the pipeline the prefixed path and restores the original path
    /// afterwards, also when the pipeline throws, keeping the path base throughout.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Temporary_path_change_is_undone_even_when_downstream_throws(bool throws)
    {
        using var services = new ServiceCollection()
            .AddSingleton<IBackOfficePathGenerator>(new TestBackOfficePaths("/portal")).BuildServiceProvider();
        var app = new ApplicationBuilder(services);
        var context = new DefaultHttpContext();
        const string originalPath = "/umbraco/basic-auth";
        context.Request.PathBase = "/portal";
        context.Request.Path = originalPath;
        var failure = new InvalidOperationException("Downstream failure");
        app.UseUmbracoPathsUnderPathBase(UmbracoUnderTest.Major);
        app.Run(request =>
        {
            Assert.Equal("/portal" + originalPath, request.Request.Path.Value);
            Assert.Equal("/portal", request.Request.PathBase.Value);
            if (throws) throw failure;
            return Task.CompletedTask;
        });
        var pipeline = app.Build();
        if (throws)
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline(context)));
        else
            await pipeline(context);
        Assert.Equal(originalPath, context.Request.Path.Value);
        Assert.Equal("/portal", context.Request.PathBase.Value);
    }

    /// <summary>
    /// Checks that the middleware gives the virtual path back to requests for the API documents of the release under
    /// test's major version, and leaves another major version's documents path as it is.
    /// </summary>
    [Fact]
    public async Task Only_the_sites_major_version_gets_its_API_documents_path_restored()
    {
        using var services = new ServiceCollection()
            .AddSingleton<IBackOfficePathGenerator>(new TestBackOfficePaths("/portal")).BuildServiceProvider();
        foreach (var major in UmbracoMajor.All)
        {
            var app = new ApplicationBuilder(services);
            app.UseUmbracoPathsUnderPathBase(UmbracoUnderTest.Major);
            var seen = "";
            app.Run(context => { seen = context.Request.Path.Value!; return Task.CompletedTask; });
            var context = new DefaultHttpContext();
            context.Request.PathBase = "/portal";
            context.Request.Path = major.ApiDocuments.Add("/index.html");
            await app.Build()(context);
            var expected = (major == UmbracoUnderTest.Major ? "/portal" : "") + major.ApiDocuments + "/index.html";
            Assert.Equal(expected, seen);
        }
    }
}
