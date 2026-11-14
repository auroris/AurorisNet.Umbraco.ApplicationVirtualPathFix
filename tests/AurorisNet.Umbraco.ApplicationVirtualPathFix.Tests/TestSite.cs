using System.Reflection;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Umbraco.Cms.Api.Management.Services.NewsDashboard;
using Umbraco.Cms.Api.Management.ViewModels.NewsDashboard;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Services;
using PublishBranchFilter = Umbraco.Cms.Core.Models.PublishBranchFilter;
using Umbraco.Cms.Core.Sync;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests;

/// <summary>How a disposable test site is hosted.</summary>
/// <param name="Prefix">The application virtual path, or "" for a site at the root.</param>
/// <param name="ExistingPathBase">Model an IIS child application: the path base is already set when the package runs.</param>
/// <param name="MediaOutsideWebRoot">Store media in an absolute folder outside wwwroot, as file-system providers such as Azure Blob Storage do.</param>
/// <param name="WebSockets">False models a host without WebSocket support, so SignalR falls back to Server-Sent Events.</param>
/// <param name="BasicAuth">Protect the front end with Umbraco's basic authentication and its login page.</param>
/// <param name="Https">Use a disposable self-signed certificate to exercise secure cookies.</param>
internal sealed record SiteOptions(
    string Prefix,
    bool ExistingPathBase = false,
    bool MediaOutsideWebRoot = false,
    bool WebSockets = true,
    bool BasicAuth = false,
    bool Https = false);

/// <summary>
/// A disposable site built like <c>dotnet new umbraco</c> with the starter kit: SQLite, Kestrel,
/// unattended install, standard Umbraco startup with no explicit package registration.
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

    public string MailDirectory => Path.Combine(Root, "mail");

    /// <summary>An absolute URL for a path relative to the application, such as <c>/umbraco</c>.</summary>
    public Uri Url(string path) => new(Address, Options.Prefix + path);

    public HttpClient CreateHttpClient() => new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (request, _, _, errors) =>
            errors == SslPolicyErrors.None || request.RequestUri?.Authority == Address.Authority,
    });

    public static async Task<TestSite> StartAsync(SiteOptions options, CancellationToken cancellationToken, string? artifacts = null)
    {
        var root = Directory.CreateTempSubdirectory("virtual-path-fix-test-").FullName;
        Directory.CreateDirectory(Path.Combine(root, "wwwroot"));
        Directory.CreateDirectory(Path.Combine(root, "mail"));
        // A new Umbraco site starts with view imports and block partials; the starter kit relies on them.
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

            // The first start installs Umbraco and imports the starter kit. Like a real site, restart
            // it afterwards: some routes (basic authentication's, for example) are only mapped when
            // Umbraco is already installed at startup.
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
    /// The starter kit 18.0.0 imports its content unpublished when Umbraco installs unattended;
    /// publish it as an editor would.
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

    private static Dictionary<string, string?> Settings(string root, SiteOptions options)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:umbracoDbDSN"] = $"Data Source={Path.Combine(root, "umbraco.db")};Pooling=False",
            ["ConnectionStrings:umbracoDbDSN_ProviderName"] = "Microsoft.Data.Sqlite",
            ["Umbraco:CMS:Global:Id"] = Guid.NewGuid().ToString(),
            ["Umbraco:CMS:Global:UseHttps"] = "false",
            // Keep image authorization enabled across both boots, like a normally installed site.
            ["Umbraco:CMS:Imaging:HMACSecretKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)),
            // Emails are written to a folder so the test can follow the links in them.
            ["Umbraco:CMS:Global:Smtp:From"] = "umbraco@example.test",
            ["Umbraco:CMS:Global:Smtp:DeliveryMethod"] = "SpecifiedPickupDirectory",
            ["Umbraco:CMS:Global:Smtp:PickupDirectoryLocation"] = Path.Combine(root, "mail"),
            ["Umbraco:CMS:WebRouting:ApplicationUrlDetection"] = "FirstRequest",
            ["Umbraco:CMS:Hosting:ApplicationVirtualPath"] = options.Prefix,
            // As in the template's Development settings. Debug also serves backoffice modules with
            // no-cache; the HTTP checks below exercise conditional (304) requests.
            ["Umbraco:CMS:Hosting:Debug"] = "true",
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

    private static async Task<WebApplication> BootAsync(string root, SiteOptions options, Dictionary<string, string?> settings, CancellationToken cancellationToken)
    {
        // Umbraco caches these across hosts in one process; each site here represents a separate deployment.
        typeof(global::Umbraco.Cms.Core.Hosting.HostingEnvironmentExtensions)
            .GetField("_backOfficePath", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, null);
        // The "friendly" extension methods views use (Url(), Value(), ...) keep the first host's services.
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
        // Standard Umbraco startup: no AppHost or explicit package registration.
        builder.CreateUmbracoBuilder().AddBackOffice().AddWebsite().AddComposers().Build();
        builder.Services.Replace(ServiceDescriptor.Singleton<IServerRoleAccessor, SingleServerRoleAccessor>());
        // Keep the real authenticated dashboard endpoint and UI, with deterministic news instead of an external feed.
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
            // Windows TLS cannot use CreateSelfSigned's ephemeral private key. Reimport the PFX
            // so the key lasts until certificate disposal, without installing a trusted certificate.
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
    /// Stops the site and returns the errors it logged, plus any warning from this package (such as a
    /// patch that no longer matches Umbraco's scripts).
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
                // Only judge the site under test, not the install run before its restart.
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

    private static void CopyLogs(string root, string? artifacts)
    {
        var source = Path.Combine(root, "umbraco", "Logs");
        if (artifacts is null || !Directory.Exists(source)) return;
        var destination = Directory.CreateDirectory(Path.Combine(artifacts, "server-logs")).FullName;
        foreach (var file in Directory.EnumerateFiles(source, "*.json"))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
    }

    private static void DeleteRoot(string root)
    {
        // Set AVPF_KEEP_TEST_SITE=1 to inspect a site's database, logs and files after a run.
        if (Environment.GetEnvironmentVariable("AVPF_KEEP_TEST_SITE") == "1")
            return;
        // Only remove the isolated directory created for this site.
        var fullRoot = Path.GetFullPath(root);
        if (fullRoot.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(fullRoot).StartsWith("virtual-path-fix-test-", StringComparison.Ordinal))
        {
            try
            {
                Directory.Delete(fullRoot, recursive: true);
            }
            catch (IOException)
            {
                // A file still held open must not hide the test's own result.
            }
        }
    }

    private sealed class TestNews : INewsDashboardService
    {
        public Task<NewsDashboardResponseModel> GetItemsAsync() => Task.FromResult(new NewsDashboardResponseModel
        {
            Items = [new() { Header = NewsHeading, Priority = "High", Body = "Loaded through the management API." }],
        });
    }

    /// <summary>Models how requests reach the site: only under the prefix, optionally with a path base or without WebSockets.</summary>
    private sealed class HostShape(SiteOptions options) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextRequest) =>
            {
                // A reverse proxy or IIS application boundary only forwards the site's own path,
                // so anything that escapes the prefix fails the browser checks.
                if (options.Prefix.Length > 0 && !context.Request.Path.StartsWithSegments(options.Prefix))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                if (!options.WebSockets)
                {
                    // As IIS without the WebSocket Protocol feature: nothing can accept a WebSocket
                    // (HTTP/1.1 upgrade or extended CONNECT), so SignalR stops offering them.
                    context.Features.Set<IHttpUpgradeFeature>(null);
                    context.Features.Set<IHttpExtendedConnectFeature>(null);
                    context.Features.Set<IHttpWebSocketFeature>(null);
                }

                if (context.Request.Path == options.Prefix + "/umbraco")
                {
                    // Playwright's intercepted request headers omit some browser-added headers.
                    // Report what the server actually received for the cross-site cookie test.
                    context.Response.Headers["X-Test-Fetch-Site"] = context.Request.Headers["Sec-Fetch-Site"].ToString();
                    context.Response.Headers["X-Test-Refresh-Cookie"] = context.Request.Cookies.Keys.Any(key => key.Contains("umbRefreshToken", StringComparison.Ordinal)) ? "yes" : "no";
                }

                await nextRequest(context);
            });
            // Also exercise the PathBase supplied by an IIS child application, without needing IIS.
            if (options.ExistingPathBase)
                app.UsePathBase(options.Prefix);
            next(app);
        };
    }
}
