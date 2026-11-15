using System.Reflection;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Umbraco.Cms.Api.Management.Services.NewsDashboard;
using Umbraco.Cms.Api.Management.ViewModels.NewsDashboard;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Services;
using PublishBranchFilter = Umbraco.Cms.Core.Models.PublishBranchFilter;
using Umbraco.Cms.Core.Sync;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests;

/// <summary>How requests reach a test site.</summary>
public enum TestHost
{
    /// <summary>Directly, with the prefix in the request path.</summary>
    Kestrel,

    /// <summary>With the prefix already moved into the path base, as an IIS child application does.</summary>
    Iis,

    /// <summary>
    /// Through a TLS-terminating proxy that strips the prefix and passes the public host, scheme and prefix in forwarded
    /// headers.
    /// </summary>
    ReverseProxy,
}

/// <summary>How a disposable test site is hosted.</summary>
/// <param name="Prefix">
/// The application virtual path, such as <c>/SomeTestPath</c>, or an empty string for a site at the root.
/// </param>
/// <param name="Host">How the prefix and the public origin reach the application.</param>
/// <param name="MediaOutsideWebRoot">
/// Stores media in an absolute folder outside <c>wwwroot</c>, so the site serves it through Umbraco's media file system, as
/// it does media in a provider such as Azure Blob Storage.
/// </param>
/// <param name="WebSockets">
/// <see langword="false"/> models a host without WebSocket support, so SignalR falls back to Server-Sent Events.
/// </param>
/// <param name="BasicAuth">
/// Protects the front end with Umbraco's basic authentication and redirects visitors to its login page.
/// </param>
/// <param name="Https">Serves HTTPS with a disposable self-signed certificate, which exercises secure cookies.</param>
/// <param name="Debug">
/// Sets <c>Umbraco:CMS:Hosting:Debug</c>. With the default, as in the template's Development settings, Umbraco serves
/// backoffice modules with <c>no-cache</c>.
/// </param>
internal sealed record SiteOptions(
    string Prefix,
    TestHost Host = TestHost.Kestrel,
    bool MediaOutsideWebRoot = false,
    bool WebSockets = true,
    bool BasicAuth = false,
    bool Https = false,
    bool Debug = true)
{
    public bool ExistingPathBase => Host == TestHost.Iis;
}

/// <summary>
/// A disposable site built like a <c>dotnet new umbraco</c> project with the starter kit: SQLite, Kestrel, an unattended
/// install, and standard Umbraco startup, which finds the package's composer as a real site does.
/// </summary>
internal sealed class TestSite : IAsyncDisposable
{
    public const string Email = "virtual-path@example.test";
    public const string Password = "VirtualPath!Test5278";
    public const string NewsHeading = "Virtual path dashboard test";

    private readonly WebApplication _app;
    private readonly DateTimeOffset _startedAt;
    private bool _stopped;
    private string? _artifacts;

    /// <summary>
    /// Wraps the running <paramref name="app"/>, whose files are in <paramref name="root"/>. <see cref="StopAsync"/>
    /// ignores log entries from before <paramref name="startedAt"/>, which belong to the install run.
    /// </summary>
    private TestSite(WebApplication app, string root, SiteOptions options, Uri address, DateTimeOffset startedAt)
    {
        _app = app;
        Root = root;
        Options = options;
        Address = address;
        _startedAt = startedAt;
    }

    public SiteOptions Options { get; }

    public string Root { get; }

    public Uri Address { get; }

    public IServiceProvider Services => _app.Services;

    public string MailDirectory => Path.Combine(Root, "mail");

    /// <summary>
    /// Returns the absolute URL of <paramref name="path"/>, a path relative to the application such as <c>/umbraco</c>.
    /// </summary>
    public Uri Url(string path) => new(Address, Options.Prefix + path);

    /// <summary>
    /// Creates an HTTP client that accepts this site's disposable certificate and follows redirects when
    /// <paramref name="followRedirects"/> is <see langword="true"/>.
    /// </summary>
    public HttpClient CreateHttpClient(bool followRedirects = true) => new(new HttpClientHandler
    {
        AllowAutoRedirect = followRedirects,
        ServerCertificateCustomValidationCallback = (request, _, _, errors) =>
            errors == SslPolicyErrors.None || request.RequestUri?.Authority == Address.Authority,
    });

    /// <summary>
    /// Creates a site in a new temporary folder, installs Umbraco with the starter kit and restarts it, as a real site
    /// restarts after installing. Then publishes the imported content, adds two test images to the home template and
    /// returns the running site. When startup fails, copies the logs to <paramref name="artifacts"/> and deletes the
    /// folder.
    /// </summary>
    public static async Task<TestSite> StartAsync(SiteOptions options, CancellationToken cancellationToken, string? artifacts = null)
    {
        var root = Directory.CreateTempSubdirectory("virtual-path-fix-test-").FullName;
        Directory.CreateDirectory(Path.Combine(root, "wwwroot"));
        Directory.CreateDirectory(Path.Combine(root, "mail"));
        // A new Umbraco site starts with view imports and block partials, which the starter kit relies on.
        var template = Path.Combine(AppContext.BaseDirectory, "SiteTemplate");
        foreach (var file in Directory.EnumerateFiles(template, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(root, Path.GetRelativePath(template, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        try
        {
            var settings = Settings(root, options);

            // The first start installs Umbraco and imports the starter kit. Restart afterwards, as a real site does:
            // Umbraco maps some routes, such as basic authentication's, only when it is already installed at startup.
            var installer = await BootAsync(root, options, settings, cancellationToken);
            try
            {
                await installer.StopAsync(CancellationToken.None);
            }
            finally
            {
                await installer.DisposeAsync();
            }

            var startedAt = DateTimeOffset.UtcNow;
            var app = await BootAsync(root, options, settings, cancellationToken);
            try
            {
                PublishImportedContent(app);
                // Render the hero picker's media URL and a signed crop URL with Razor, next to the starter kit's own
                // background image. The crop passes this site's services explicitly: the parameterless overload caches
                // its generator in a static readonly property, which would carry an earlier site's HMAC key into later
                // sites in this process.
                await File.AppendAllTextAsync(Path.Combine(root, "Views", "Home.cshtml"), """

                    @inject Umbraco.Cms.Core.Media.IImageUrlGenerator TestImageUrlGenerator
                    @inject Umbraco.Cms.Core.Models.PublishedContent.IPublishedValueFallback TestValueFallback
                    @inject Umbraco.Cms.Core.Routing.IPublishedUrlProvider TestUrlProvider
                    @if (Model.HeroBackgroundImage is { } testImage)
                    {
                        <img id="test-media-original" src="@testImage.Url()" alt="Test original" />
                        <img id="test-media-crop" src="@testImage.GetCropUrl(TestImageUrlGenerator, TestValueFallback, TestUrlProvider, width: 100, height: 60)" alt="Test crop" />
                    }
                    """, cancellationToken);
                var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
                return new TestSite(app, root, options, address, startedAt) { _artifacts = artifacts };
            }
            catch
            {
                await app.StopAsync(CancellationToken.None);
                await app.DisposeAsync();
                await Serilog.Log.CloseAndFlushAsync();
                throw;
            }
        }
        catch
        {
            CopyLogs(root, artifacts);
            DeleteRoot(root);
            throw;
        }
    }

    /// <summary>
    /// Publishes each unpublished root item with its descendants, as an editor would, and fails the test if any item fails
    /// to publish. The starter kit 18.0.0 imports its content unpublished when Umbraco installs unattended.
    /// </summary>
    private static void PublishImportedContent(WebApplication app)
    {
        var content = app.Services.GetRequiredService<IContentService>();
        foreach (var root in content.GetRootContent().Where(item => !item.Published))
        {
            foreach (var result in content.PublishBranch(root, PublishBranchFilter.IncludeUnpublished, ["*"]))
                Assert.True(result.Success, $"Publishing {result.Content?.Name} failed: {result.Result}");
        }
    }

    /// <summary>
    /// Returns the configuration for a site in <paramref name="root"/>: a SQLite database, an unattended install with the
    /// test user, emails written to the <c>mail</c> folder, and the hosting choices in <paramref name="options"/>.
    /// </summary>
    private static Dictionary<string, string?> Settings(string root, SiteOptions options)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:umbracoDbDSN"] = $"Data Source={Path.Combine(root, "umbraco.db")};Pooling=False",
            ["ConnectionStrings:umbracoDbDSN_ProviderName"] = "Microsoft.Data.Sqlite",
            ["Umbraco:CMS:Global:Id"] = Guid.NewGuid().ToString(),
            ["Umbraco:CMS:Global:UseHttps"] = "false",
            // One HMAC key for both boots keeps image URL signing enabled, as on a normally installed site.
            ["Umbraco:CMS:Imaging:HMACSecretKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)),
            // Write emails to a folder, so the tests can follow the links in them.
            ["Umbraco:CMS:Global:Smtp:From"] = "umbraco@example.test",
            ["Umbraco:CMS:Global:Smtp:DeliveryMethod"] = "SpecifiedPickupDirectory",
            ["Umbraco:CMS:Global:Smtp:PickupDirectoryLocation"] = Path.Combine(root, "mail"),
            ["Umbraco:CMS:WebRouting:ApplicationUrlDetection"] = "FirstRequest",
            ["Umbraco:CMS:Hosting:ApplicationVirtualPath"] = options.Prefix,
            // With true, as in the template's Development settings, Umbraco serves backoffice modules with no-cache.
            ["Umbraco:CMS:Hosting:Debug"] = options.Debug.ToString(),
            ["Umbraco:CMS:DeliveryApi:Enabled"] = "true",
            ["Umbraco:CMS:DeliveryApi:PublicAccess"] = "true",
            ["Umbraco:CMS:TypeFinder:AdditionalAssemblyExclusionEntries:0"] = "xunit.",
            ["Umbraco:CMS:Unattended:InstallUnattended"] = "true",
            ["Umbraco:CMS:Unattended:UpgradeUnattended"] = "true",
            ["Umbraco:CMS:Unattended:UnattendedUserName"] = "Virtual path test",
            ["Umbraco:CMS:Unattended:UnattendedUserEmail"] = Email,
            ["Umbraco:CMS:Unattended:UnattendedUserPassword"] = Password,
        };
        if (options.MediaOutsideWebRoot)
            settings["Umbraco:CMS:Global:UmbracoMediaPhysicalRootPath"] = Directory.CreateDirectory(Path.Combine(root, "media-store")).FullName;
        if (options.BasicAuth)
        {
            settings["Umbraco:CMS:BasicAuth:Enabled"] = "true";
            settings["Umbraco:CMS:BasicAuth:RedirectToLoginPage"] = "true";
        }

        return settings;
    }

    /// <summary>
    /// Builds and starts one host for the site on a free loopback port, with <paramref name="settings"/> and the hosting
    /// shape in <paramref name="options"/>. Clears Umbraco's process-wide caches first, so each site behaves as a separate
    /// deployment. Waits for the unattended install or upgrade and checks that Umbraco reaches
    /// <see cref="RuntimeLevel.Run"/>.
    /// </summary>
    private static async Task<WebApplication> BootAsync(string root, SiteOptions options, Dictionary<string, string?> settings, CancellationToken cancellationToken)
    {
        // Umbraco caches the backoffice path in a static field for the whole process. Clear it, because each site here is
        // a separate deployment.
        typeof(global::Umbraco.Cms.Core.Hosting.HostingEnvironmentExtensions)
            .GetField("_backOfficePath", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, null);
        // The "friendly" extension methods that views call, such as Url() and Value(), keep the first host's services in
        // static fields. Clear those too.
        foreach (var type in AppDomain.CurrentDomain.GetAssemblies()
                     .Where(assembly => assembly.GetName().Name?.StartsWith("Umbraco.", StringComparison.Ordinal) == true)
                     .SelectMany(assembly => assembly.GetTypes())
                     .Where(type => type is { IsAbstract: true, IsSealed: true, Namespace: "Umbraco.Extensions" }
                         && type.Name.StartsWith("Friendly", StringComparison.Ordinal)))
        {
            foreach (var field in type.GetFields(BindingFlags.Static | BindingFlags.NonPublic)
                         .Where(field => field is { IsInitOnly: false, IsLiteral: false } && !field.FieldType.IsValueType))
                field.SetValue(null, null);
        }

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(TestSite).Assembly.GetName().Name,
            ContentRootPath = root,
            EnvironmentName = "Development",
        });
        builder.WebHost.UseSetting(WebHostDefaults.StaticWebAssetsKey,
                Path.Combine(AppContext.BaseDirectory, typeof(TestSite).Assembly.GetName().Name + ".staticwebassets.runtime.json"))
            .UseStaticWebAssets().UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(settings);
        // Standard Umbraco startup, without an AppHost: AddComposers() finds the package's composer, as on a real site.
        builder.CreateUmbracoBuilder().AddBackOffice().AddWebsite().AddDeliveryApi().AddComposers().Build();
        builder.Services.Replace(ServiceDescriptor.Singleton<IServerRoleAccessor, SingleServerRoleAccessor>());
        // The news dashboard keeps its real authenticated endpoint and UI but shows fixed news instead of an external feed.
        builder.Services.Replace(ServiceDescriptor.Singleton<INewsDashboardService, TestNews>());
        builder.Services.AddSingleton<IStartupFilter>(new HostShape(options));

        X509Certificate2? certificate = null;
        if (options.Https)
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            // Windows TLS cannot use the ephemeral private key that CreateSelfSigned creates. Reimporting the PFX keeps the
            // key until the certificate is disposed, without installing anything in the trust store.
            certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null);
            builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        }
        var app = builder.Build();
        if (certificate is not null) app.Lifetime.ApplicationStopped.Register(certificate.Dispose);
        try
        {
            await app.BootUmbracoAsync();
            app.UseUmbraco().WithMiddleware(u => { u.UseBackOffice(); u.UseWebsite(); })
                .WithEndpoints(u => { u.UseBackOfficeEndpoints(); u.UseWebsiteEndpoints(); });
            await app.StartAsync(cancellationToken);
            var install = app.Services.GetServices<IHostedService>().OfType<BackgroundService>()
                .Single(service => service.GetType().Name == "UnattendedUpgradeBackgroundService");
            if (install.ExecuteTask is { } task)
                await task.WaitAsync(TimeSpan.FromMinutes(3), cancellationToken);
            var runtime = app.Services.GetRequiredService<IRuntimeState>();
            Assert.True(runtime.Level == RuntimeLevel.Run, runtime.BootFailedException?.ToString() ?? runtime.Level.ToString());
            return app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Stops the site, copies its logs to the artifacts folder when one was given, and returns the errors it logged after
    /// the restart, plus any warning from this package, such as a patch that no longer matches Umbraco's scripts. Returns
    /// an empty list when the site has already stopped.
    /// </summary>
    public async Task<IReadOnlyList<string>> StopAsync()
    {
        if (_stopped)
            return [];
        _stopped = true;
        await _app.StopAsync(CancellationToken.None);
        await _app.DisposeAsync();
        await Serilog.Log.CloseAndFlushAsync();
        CopyLogs(Root, _artifacts);

        var problems = new List<string>();
        var logs = Path.Combine(Root, "umbraco", "Logs");
        if (!Directory.Exists(logs))
            return problems;
        foreach (var file in Directory.EnumerateFiles(logs, "*.json"))
        {
            foreach (var line in File.ReadLines(file))
            {
                using var entry = JsonDocument.Parse(line);
                var properties = entry.RootElement;
                // Skip entries from the install run before the restart; only the site under test counts.
                if (properties.TryGetProperty("@t", out var t) && t.GetDateTimeOffset() < _startedAt)
                    continue;
                var level = properties.TryGetProperty("@l", out var l) ? l.GetString() : "Information";
                var source = properties.TryGetProperty("SourceContext", out var s) ? s.GetString() : null;
                var isError = level is "Error" or "Fatal";
                var isPackageWarning = level == "Warning" && source?.StartsWith("AurorisNet", StringComparison.Ordinal) == true;
                if (isError || isPackageWarning)
                {
                    var template = properties.TryGetProperty("@mt", out var mt) ? mt.GetString() : "";
                    var exception = properties.TryGetProperty("@x", out var x) ? " " + x.GetString() : "";
                    problems.Add($"{level} {source}: {template}{exception}");
                }
            }
        }

        return problems;
    }

    /// <summary>Stops the site and deletes its folder, unless <c>AVPF_KEEP_TEST_SITE=1</c> keeps it.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync();
        }
        finally
        {
            DeleteRoot(Root);
        }
    }

    /// <summary>
    /// Copies the site's JSON logs from <paramref name="root"/> to a <c>server-logs</c> folder under
    /// <paramref name="artifacts"/>, when given.
    /// </summary>
    private static void CopyLogs(string root, string? artifacts)
    {
        var source = Path.Combine(root, "umbraco", "Logs");
        if (artifacts is null || !Directory.Exists(source)) return;
        var destination = Directory.CreateDirectory(Path.Combine(artifacts, "server-logs")).FullName;
        foreach (var file in Directory.EnumerateFiles(source, "*.json"))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
    }

    /// <summary>
    /// Deletes the site's temporary folder <paramref name="root"/>, unless <c>AVPF_KEEP_TEST_SITE=1</c> keeps it. A file
    /// that is still open leaves the folder in place and writes a note to the test output.
    /// </summary>
    private static void DeleteRoot(string root)
    {
        // Set AVPF_KEEP_TEST_SITE=1 to inspect a site's database, logs and files after a run.
        if (Environment.GetEnvironmentVariable("AVPF_KEEP_TEST_SITE") == "1")
            return;
        // Delete only a folder that this class created in the temp directory.
        var fullRoot = Path.GetFullPath(root);
        if (fullRoot.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(fullRoot).StartsWith("virtual-path-fix-test-", StringComparison.Ordinal))
        {
            try
            {
                Directory.Delete(fullRoot, recursive: true);
            }
            catch (IOException exception)
            {
                // Report a file that is still open instead of throwing, so the test's own result stays visible.
                TestContext.Current.TestOutputHelper?.WriteLine($"Could not remove test site {fullRoot}: {exception.Message}");
            }
        }
    }

    /// <summary>Supplies fixed news to the dashboard in place of Umbraco's external feed.</summary>
    private sealed class TestNews : INewsDashboardService
    {
        /// <summary>Returns one news item headed <see cref="TestSite.NewsHeading"/>.</summary>
        public Task<NewsDashboardResponseModel> GetItemsAsync() => Task.FromResult(new NewsDashboardResponseModel
        {
            Items = [new() { Header = NewsHeading, Priority = "High", Body = "Loaded through the management API." }],
        });
    }

    /// <summary>
    /// Models how requests reach the site: only under the prefix and, as <see cref="SiteOptions"/> chooses, through an IIS
    /// path base, a reverse proxy, or a host without WebSockets.
    /// </summary>
    private sealed class HostShape(SiteOptions options) : IStartupFilter
    {
        /// <summary>
        /// Adds middleware ahead of the site's pipeline: a 404 for requests outside the prefix, the WebSocket and IIS path
        /// base choices, the reverse proxy's forwarded headers, and diagnostic headers on the backoffice page.
        /// </summary>
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextRequest) =>
            {
                // A reverse proxy or IIS application forwards only the site's own path, so a request that escapes the
                // prefix fails the browser checks.
                if (options.Prefix.Length > 0 && !context.Request.Path.StartsWithSegments(options.Prefix))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                if (!options.WebSockets)
                {
                    // Behave like IIS without the WebSocket Protocol feature: no request can open a WebSocket, by HTTP/1.1
                    // upgrade or extended CONNECT, so SignalR stops offering them.
                    context.Features.Set<IHttpUpgradeFeature>(null);
                    context.Features.Set<IHttpExtendedConnectFeature>(null);
                    context.Features.Set<IHttpWebSocketFeature>(null);
                }

                if (context.Request.Path == options.Prefix + "/umbraco")
                {
                    // Playwright's intercepted requests omit some headers the browser adds. Report what the server
                    // received, for the cross-site cookie test.
                    context.Response.Headers["X-Test-Fetch-Site"] = context.Request.Headers["Sec-Fetch-Site"].ToString();
                    context.Response.Headers["X-Test-Refresh-Cookie"] = context.Request.Cookies.Keys.Any(key => key.Contains("umbRefreshToken", StringComparison.Ordinal)) ? "yes" : "no";
                }

                await nextRequest(context);
            });
            // Set the path base as an IIS child application does, so the tests cover that host without IIS.
            if (options.ExistingPathBase)
                app.UsePathBase(options.Prefix);
            if (options.Host == TestHost.ReverseProxy)
            {
                app.Use(async (context, nextRequest) =>
                {
                    // Model a trusted TLS-terminating proxy that forwards to an HTTP backend with a different host and
                    // removes the public prefix from the request path.
                    context.Request.Headers["X-Forwarded-Host"] = context.Request.Host.Value;
                    context.Request.Headers["X-Forwarded-Proto"] = context.Request.Scheme;
                    context.Request.Headers["X-Forwarded-Prefix"] = context.Request.Path.Value![..options.Prefix.Length];
                    context.Request.Host = new HostString("backend.internal", 8080);
                    context.Request.Scheme = "http";
                    if (options.Prefix.Length > 0)
                    {
                        Assert.True(context.Request.Path.StartsWithSegments(options.Prefix, out var rest));
                        context.Request.Path = rest;
                    }
                    await nextRequest(context);
                });
                app.UseForwardedHeaders(new ForwardedHeadersOptions
                {
                    ForwardedHeaders = ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedPrefix,
                });
            }
            next(app);
        };
    }
}
