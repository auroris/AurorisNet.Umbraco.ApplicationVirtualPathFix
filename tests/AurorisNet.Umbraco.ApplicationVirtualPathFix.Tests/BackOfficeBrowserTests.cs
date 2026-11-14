using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Playwright;
using Umbraco.Cms.Api.Management.Services.NewsDashboard;
using Umbraco.Cms.Api.Management.ViewModels.NewsDashboard;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Sync;
using static Microsoft.Playwright.Assertions;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests;

public sealed class BackOfficeBrowserTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("", false)]
    [InlineData("/SomeTestPath", false)]
    [InlineData("/SomeTestPath", true)]
    public async Task Plain_Umbraco_loads_its_dashboard_and_renews_tokens_without_browser_errors(string prefix, bool existingPathBase)
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("virtual-path-fix-test-").FullName;
        Directory.CreateDirectory(Path.Combine(root, "wwwroot"));
        // Umbraco caches this across hosts; each case here represents a separate deployed site.
        typeof(global::Umbraco.Cms.Core.Hosting.HostingEnvironmentExtensions)
            .GetField("_backOfficePath", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, null);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(BackOfficeBrowserTests).Assembly.GetName().Name,
            ContentRootPath = root, EnvironmentName = "Development",
        });
        builder.WebHost.UseSetting(WebHostDefaults.StaticWebAssetsKey,
                Path.Combine(AppContext.BaseDirectory, typeof(BackOfficeBrowserTests).Assembly.GetName().Name + ".staticwebassets.runtime.json"))
            .UseStaticWebAssets().UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        const string email = "virtual-path@example.test", password = "VirtualPath!Test5278";
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:umbracoDbDSN"] = $"Data Source={Path.Combine(root, "umbraco.db")};Pooling=False",
            ["ConnectionStrings:umbracoDbDSN_ProviderName"] = "Microsoft.Data.Sqlite",
            ["Umbraco:CMS:Global:Id"] = Guid.NewGuid().ToString(),
            ["Umbraco:CMS:Global:UseHttps"] = "false",
            ["Umbraco:CMS:Hosting:ApplicationVirtualPath"] = prefix,
            ["Umbraco:CMS:Hosting:Debug"] = "false",
            ["Umbraco:CMS:ModelsBuilder:ModelsMode"] = "Nothing",
            ["Umbraco:CMS:TypeFinder:AdditionalAssemblyExclusionEntries:0"] = "xunit.",
            ["Umbraco:CMS:Unattended:InstallUnattended"] = "true",
            ["Umbraco:CMS:Unattended:UpgradeUnattended"] = "true",
            ["Umbraco:CMS:Unattended:UnattendedUserName"] = "Virtual path test",
            ["Umbraco:CMS:Unattended:UnattendedUserEmail"] = email,
            ["Umbraco:CMS:Unattended:UnattendedUserPassword"] = password,
        });
        // Standard Umbraco startup: no AppHost or explicit package registration.
        builder.CreateUmbracoBuilder().AddBackOffice().AddWebsite().AddComposers().Build();
        builder.Services.Replace(ServiceDescriptor.Singleton<IServerRoleAccessor, SingleServerRoleAccessor>());
        // Keep the real authenticated dashboard endpoint and UI, with deterministic news instead of an external feed.
        builder.Services.Replace(ServiceDescriptor.Singleton<INewsDashboardService, TestNews>());
        builder.Services.AddSingleton<IStartupFilter>(new SubdirectoryOnly(prefix, existingPathBase));
        var app = builder.Build();
        try
        {
            await app.BootUmbracoAsync();
            app.UseUmbraco().WithMiddleware(u => { u.UseBackOffice(); u.UseWebsite(); })
                .WithEndpoints(u => { u.UseBackOfficeEndpoints(); u.UseWebsiteEndpoints(); });
            await app.StartAsync(ct);
            var install = app.Services.GetServices<IHostedService>().OfType<BackgroundService>()
                .Single(service => service.GetType().Name == "UnattendedUpgradeBackgroundService");
            if (install.ExecuteTask is { } task) await task.WaitAsync(TimeSpan.FromSeconds(90), ct);
            var runtime = app.Services.GetRequiredService<IRuntimeState>();
            Assert.True(runtime.Level == RuntimeLevel.Run, runtime.BootFailedException?.ToString() ?? runtime.Level.ToString());
            var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            if (prefix.Length > 0)
            {
                using var http = new HttpClient { BaseAddress = address };
                using var response = await http.GetAsync("/umbraco", ct);
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            }
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync();
            await using var context = await browser.NewContextAsync();
            var page = await context.NewPageAsync();
            var errors = new ConcurrentQueue<string>();
            page.Console += (_, message) =>
            {
                if (message.Type == "error") errors.Enqueue($"Console: {message.Text} ({message.Location})");
            };
            page.PageError += (_, error) => errors.Enqueue($"JavaScript: {error}");
            try
            {
                var backoffice = prefix + "/umbraco";
                var response = await page.GotoAsync(new Uri(address, backoffice).ToString());
                Assert.True(response?.Ok, $"Backoffice returned HTTP {response?.Status}");
                await page.Locator("#username-input").FillAsync(email);
                await page.Locator("#password-input").FillAsync(password);
                await page.Locator("#umb-login-button").ClickAsync();
                var dashboard = page.Locator("umb-umbraco-news-dashboard");
                await Expect(dashboard.GetByRole(AriaRole.Heading, new() { Name = "Virtual path dashboard test", Exact = true }))
                    .ToBeVisibleAsync(new() { Timeout = 60_000 });
                await page.GetByRole(AriaRole.Button, new() { Name = "View Umbraco CMS system information and version number", Exact = true }).ClickAsync();
                var logo = page.Locator("umb-backoffice-header-logo img#logo");
                await Expect(logo).ToBeVisibleAsync();
                await Expect(logo).ToHaveJSPropertyAsync("complete", true);
                Assert.True(await logo.EvaluateAsync<bool>("image => image.complete && image.naturalWidth > 0"));
                Assert.StartsWith(prefix + "/umbraco/backoffice/", new Uri(await logo.EvaluateAsync<string>("image => image.src")).AbsolutePath);

                var token = page.WaitForResponseAsync(response =>
                    new Uri(response.Url).AbsolutePath == backoffice + "/management/api/v1/security/back-office/token"
                    && response.Request.PostData?.Contains("grant_type=refresh_token", StringComparison.Ordinal) == true);
                await page.ReloadAsync();
                Assert.True((await token).Ok, "Restoring the session must refresh its token successfully.");
                await Expect(dashboard.GetByRole(AriaRole.Heading, new() { Name = "Virtual path dashboard test", Exact = true }))
                    .ToBeVisibleAsync(new() { Timeout = 30_000 });
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                Assert.StartsWith(backoffice + "/", new Uri(page.Url).AbsolutePath);
                Assert.Empty(errors);
            }
            finally
            {
                output.WriteLine($"Last browser URL: {page.Url}");
                foreach (var error in errors) output.WriteLine(error);
            }
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
            await app.DisposeAsync();
            await Serilog.Log.CloseAndFlushAsync();
            // Only remove the isolated directory created by this test.
            var fullRoot = Path.GetFullPath(root);
            if (fullRoot.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(fullRoot).StartsWith("virtual-path-fix-test-", StringComparison.Ordinal))
                Directory.Delete(fullRoot, recursive: true);
        }
    }

    private sealed class TestNews : INewsDashboardService
    {
        public Task<NewsDashboardResponseModel> GetItemsAsync() => Task.FromResult(new NewsDashboardResponseModel
        {
            Items = [new() { Header = "Virtual path dashboard test", Priority = "High", Body = "Loaded through the management API." }],
        });
    }

    private sealed class SubdirectoryOnly(string prefix, bool existingPathBase) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextRequest) =>
            {
                if (prefix.Length == 0 || context.Request.Path.StartsWithSegments(prefix)) await nextRequest(context);
                else context.Response.StatusCode = StatusCodes.Status404NotFound;
            });
            // Also exercise the PathBase supplied by an IIS child application, without needing IIS.
            if (existingPathBase) app.UsePathBase(prefix);
            next(app);
        };
    }
}
