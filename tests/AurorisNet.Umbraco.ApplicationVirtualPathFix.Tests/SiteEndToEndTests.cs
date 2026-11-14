using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using MimeKit;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using static Microsoft.Playwright.Assertions;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests;

/// <summary>
/// End-to-end acceptance test: a new Umbraco site with the starter kit, driven through Chromium.
/// Every browser console error, uncaught exception, failed request or HTTP error fails the run, and
/// so does any error the site logs.
/// </summary>
public sealed partial class SiteEndToEndTests(ITestOutputHelper output)
{
    private const string PageName = "Virtual path e2e page";
    private ITestOutputHelper _log = output;

    [Theory]
    [InlineData("", false, false, true)]               // At the root.
    [InlineData("/SomeTestPath", false, false, true)]  // Kestrel subdirectory.
    [InlineData("/SomeTestPath", false, true, true)]   // Kestrel subdirectory, media outside wwwroot.
    [InlineData("/SomeTestPath", true, true, false)]   // IIS child application without WebSockets, media outside wwwroot.
    public async Task Starter_kit_site_works_end_to_end(string prefix, bool existingPathBase, bool mediaOutsideWebRoot, bool webSockets)
    {
        var ct = TestContext.Current.CancellationToken;
        var name = $"{(prefix.Length == 0 ? "root" : existingPathBase ? "iis" : "kestrel")}{(mediaOutsideWebRoot ? "-external-media" : "")}{(webSockets ? "" : "-no-websockets")}";
        // Set AVPF_E2E_CASE to case names, such as kestrel or root,kestrel, to run just those hosting cases.
        if (Environment.GetEnvironmentVariable("AVPF_E2E_CASE") is { Length: > 0 } only && !only.Split(',').Contains(name))
            Assert.Skip($"AVPF_E2E_CASE selects {only}.");
        await using var site = await TestSite.StartAsync(new SiteOptions(prefix, existingPathBase, mediaOutsideWebRoot, webSockets), ct);
        _log = TeeOutput.Create(_log, name);
        await using (var browser = await BrowserSession.StartAsync(site, _log, name))
        {
            await browser.StepAsync("OpenAPI documents", () => CheckOpenApiAsync(site, ct));
            await browser.StepAsync("Front end: every page and image", () => CrawlFrontEndAsync(browser.Page, site));
            await browser.StepAsync("Forgotten password email", () => FollowPasswordResetEmailAsync(browser, site, ct));
            await browser.StepAsync("Backoffice sign-in", () => SignInAsync(browser.Page, site));
            await browser.StepAsync("Session restore after reload", () => ReloadAndRestoreSessionAsync(browser.Page, site));
            await browser.StepAsync("Create and publish a page", () => CreateAndPublishPageAsync(browser.Page, site, ct));
            await browser.StepAsync("Upload a file and open it", () => UploadMediaAsync(browser.Page, site, ct));
            await browser.SettleAsync();
            Assert.Empty(browser.Errors);
        }

        Assert.Empty(await site.StopAsync());
    }

    [Fact]
    public async Task Basic_authentication_login_works_in_a_subdirectory()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var site = await TestSite.StartAsync(new SiteOptions("/SomeTestPath", BasicAuth: true), ct);
        _log = TeeOutput.Create(_log, "kestrel-basic-auth");
        await using (var browser = await BrowserSession.StartAsync(site, _log, "kestrel-basic-auth"))
        {
            await browser.StepAsync("Basic authentication", async () =>
            {
                var page = browser.Page;
                var response = await page.GotoAsync(site.Url("/").ToString());
                Assert.True(response?.Ok, $"The front end returned HTTP {response?.Status}");
                Assert.StartsWith(site.Url("/umbraco/basic-auth/login").AbsolutePath, new Uri(page.Url).AbsolutePath, StringComparison.OrdinalIgnoreCase);
                await page.Locator("#username").FillAsync(TestSite.Email);
                await page.Locator("#password").FillAsync(TestSite.Password);
                await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
                await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == site.Url("/").AbsolutePath);
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            });
            await browser.SettleAsync();
            Assert.Empty(browser.Errors);
        }

        Assert.Empty(await site.StopAsync());
    }

    private static async Task CheckOpenApiAsync(TestSite site, CancellationToken ct)
    {
        using var http = new HttpClient();
        using var document = await http.GetAsync(site.Url("/umbraco/openapi/management.json"), ct);
        Assert.Equal(HttpStatusCode.OK, document.StatusCode);
        Assert.Contains("\"openapi\"", await document.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        using var ui = await http.GetAsync(site.Url("/umbraco/openapi/"), ct);
        Assert.Equal(HttpStatusCode.OK, ui.StatusCode);
    }

    /// <summary>Visits every front-end page linked from the home page and checks that each image loads.</summary>
    private async Task CrawlFrontEndAsync(IPage page, TestSite site)
    {
        var home = site.Url("/");
        var queue = new Queue<Uri>([home]);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { home.AbsolutePath };
        var visited = 0;
        while (queue.TryDequeue(out var url) && visited < 60)
        {
            var response = await page.GotoAsync(url.ToString());
            Assert.True(response?.Ok, $"{url.AbsolutePath} returned HTTP {response?.Status}");
            visited++;
            var broken = await LoadImagesAsync(page);
            Assert.True(broken.Length == 0, $"Images failed to load on {url.AbsolutePath}: {string.Join(", ", broken)}");

            foreach (var href in await page.EvaluateAsync<string[]>("() => [...document.querySelectorAll('a[href]')].map(a => a.href)"))
            {
                if (Uri.TryCreate(href, UriKind.Absolute, out var link) && IsFrontEndPage(site, link) && seen.Add(link.AbsolutePath))
                    queue.Enqueue(new Uri(link.GetLeftPart(UriPartial.Path)));
            }
        }

        _log.WriteLine($"Visited {visited} front-end pages: {string.Join(", ", seen)}");
        Assert.True(visited >= 3, $"Expected the starter kit's pages, but only {visited} page(s) were found.");
    }

    private static bool IsFrontEndPage(TestSite site, Uri link)
        => link.Scheme is "http" or "https"
            && string.Equals(link.Authority, site.Address.Authority, StringComparison.OrdinalIgnoreCase)
            && link.AbsolutePath.StartsWith(site.Url("/").AbsolutePath, StringComparison.OrdinalIgnoreCase)
            && !link.AbsolutePath.StartsWith(site.Url("/umbraco").AbsolutePath, StringComparison.OrdinalIgnoreCase)
            && !Path.HasExtension(link.AbsolutePath);

    /// <summary>Loads lazy images too, and returns the ones that failed.</summary>
    private static Task<string[]> LoadImagesAsync(IPage page) => page.EvaluateAsync<string[]>("""
        async () => {
          const images = [...document.images];
          for (const image of images) image.loading = 'eager';
          await Promise.all(images.filter(i => !i.complete).map(i => new Promise(done => {
            i.addEventListener('load', done, { once: true });
            i.addEventListener('error', done, { once: true });
            setTimeout(done, 15000);
          })));
          return images
            .filter(i => i.currentSrc && (!i.complete || (i.naturalWidth === 0 && !/\.svg(\?|$)/i.test(i.currentSrc))))
            .map(i => i.currentSrc);
        }
        """);

    /// <summary>Requests a password reset from the login page and follows the emailed link.</summary>
    private async Task FollowPasswordResetEmailAsync(BrowserSession browser, TestSite site, CancellationToken ct)
    {
        var page = browser.Page;
        var response = await page.GotoAsync(site.Url("/umbraco").ToString());
        Assert.True(response?.Ok, $"The backoffice returned HTTP {response?.Status}");
        // A click that lands before the login element has finished rendering does not switch views.
        var emailInput = page.Locator("#email input");
        for (var attempt = 1; !await emailInput.IsVisibleAsync(); attempt++)
        {
            await page.Locator("#forgot-password").ClickAsync();
            try
            {
                await emailInput.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5_000 });
            }
            catch (TimeoutException) when (attempt < 3)
            {
            }
        }

        await emailInput.FillAsync(TestSite.Email);
        await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();

        var email = await WaitForFileAsync(site.MailDirectory, "*.eml", TimeSpan.FromSeconds(30), ct);
        MimeMessage message;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                message = await MimeMessage.LoadAsync(email, ct);
                break;
            }
            catch (IOException) when (attempt < 20)
            {
                // Umbraco may still be writing the file.
                await Task.Delay(250, ct);
            }
        }

        var body = message.HtmlBody ?? message.TextBody ?? "";
        var match = ResetLink().Match(body);
        Assert.True(match.Success, $"No reset link in the email: {body}");
        var link = new Uri(WebUtility.HtmlDecode(match.Value));
        _log.WriteLine($"Reset link: {link.GetLeftPart(UriPartial.Path)}");
        Assert.Equal(site.Address.Authority, link.Authority);
        Assert.Equal(site.Url("/umbraco/login").AbsolutePath, link.AbsolutePath);

        var resetPage = await browser.NewPageAsync();
        response = await resetPage.GotoAsync(link.ToString());
        Assert.True(response?.Ok, $"The reset link returned HTTP {response?.Status}");
        // The page validates the reset code through the API before it offers the form.
        await Expect(resetPage.GetByRole(AriaRole.Textbox, new() { Name = "New password", Exact = true }))
            .ToBeVisibleAsync(new() { Timeout = 30_000 });
        await resetPage.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await resetPage.CloseAsync();
    }

    private static async Task SignInAsync(IPage page, TestSite site)
    {
        var response = await page.GotoAsync(site.Url("/umbraco").ToString());
        Assert.True(response?.Ok, $"The backoffice returned HTTP {response?.Status}");
        await page.Locator("#username-input").FillAsync(TestSite.Email);
        await page.Locator("#password-input").FillAsync(TestSite.Password);
        await page.Locator("#umb-login-button").ClickAsync();
        var dashboard = page.Locator("umb-umbraco-news-dashboard");
        await Expect(dashboard.GetByRole(AriaRole.Heading, new() { Name = TestSite.NewsHeading, Exact = true }))
            .ToBeVisibleAsync(new() { Timeout = 60_000 });

        await page.GetByRole(AriaRole.Button, new() { Name = "View Umbraco CMS system information and version number", Exact = true }).ClickAsync();
        var logo = page.Locator("umb-backoffice-header-logo img#logo");
        await Expect(logo).ToBeVisibleAsync();
        await Expect(logo).ToHaveJSPropertyAsync("complete", true);
        Assert.True(await logo.EvaluateAsync<bool>("image => image.complete && image.naturalWidth > 0"));
        Assert.StartsWith(site.Url("/umbraco/backoffice/").AbsolutePath, new Uri(await logo.EvaluateAsync<string>("image => image.src")).AbsolutePath);
        await page.Keyboard.PressAsync("Escape");
    }

    private static async Task ReloadAndRestoreSessionAsync(IPage page, TestSite site)
    {
        var backoffice = site.Url("/umbraco").AbsolutePath;
        var token = page.WaitForResponseAsync(response =>
            new Uri(response.Url).AbsolutePath == backoffice + "/management/api/v1/security/back-office/token"
            && response.Request.PostData?.Contains("grant_type=refresh_token", StringComparison.Ordinal) == true);
        await page.ReloadAsync();
        Assert.True((await token).Ok, "Restoring the session must refresh its token successfully.");
        var dashboard = page.Locator("umb-umbraco-news-dashboard");
        await Expect(dashboard.GetByRole(AriaRole.Heading, new() { Name = TestSite.NewsHeading, Exact = true }))
            .ToBeVisibleAsync(new() { Timeout = 30_000 });
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        Assert.StartsWith(backoffice + "/", new Uri(page.Url).AbsolutePath);
    }

    /// <summary>Creates a page under Home with the first allowed document type, publishes it and opens it.</summary>
    private async Task CreateAndPublishPageAsync(IPage page, TestSite site, CancellationToken ct)
    {
        await page.GotoAsync(site.Url("/umbraco/section/content").ToString());
        // The workspace's router slot overlaps the tree item's actions button, so send the click to it.
        var actions = page.GetByRole(AriaRole.Button, new() { Name = "View actions for 'Home'", Exact = true });
        await Expect(actions).ToBeAttachedAsync(new() { Timeout = 30_000 });
        await actions.DispatchEventAsync("click");
        // data-mark attributes are the selectors Umbraco's own acceptance tests use.
        // Home's actions menu, not the always-visible create button of the Content root.
        await page.Locator("umb-entity-action-list [data-mark='entity-action:Umb.EntityAction.Document.Create']")
            .Filter(new() { Visible = true }).First.ClickAsync();
        var documentTypes = page.Locator("uui-ref-node-document-type");
        await Expect(documentTypes.First).ToBeVisibleAsync(new() { Timeout = 30_000 });
        // The starter kit's Content Page needs only a page title.
        var names = await documentTypes.EvaluateAllAsync<string[]>("types => types.map(type => type.name ?? '')");
        var index = Math.Max(0, Array.FindIndex(names, name => name.StartsWith("Content Page", StringComparison.Ordinal)));
        _log.WriteLine($"Document types: {string.Join(", ", names)}; using {names.ElementAtOrDefault(index)}");
        await documentTypes.Nth(index).ClickAsync();
        await page.Locator("[data-mark='input:entity-name'] input").FillAsync(PageName);
        await page.GetByRole(AriaRole.Textbox, new() { Name = "Page Title" }).First.FillAsync(PageName);
        await page.Locator("[data-mark='workspace-action:Umb.WorkspaceAction.Document.SaveAndPublish']").ClickAsync();

        // Invariant documents publish straight away; a variant document asks which variants to publish.
        var confirm = page.Locator("umb-document-publish-modal, umb-document-save-and-publish-modal")
            .GetByRole(AriaRole.Button, new() { NameRegex = PublishButton() });
        try
        {
            await confirm.First.ClickAsync(new() { Timeout = 3_000 });
        }
        catch (TimeoutException)
        {
        }

        // The new page is published under Home, which the starter kit serves at the site root.
        var url = site.Url("/virtual-path-e2e-page/");
        using var http = new HttpClient();
        HttpStatusCode status = default;
        for (var attempt = 0; attempt < 30 && status != HttpStatusCode.OK; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(1_000, ct);
            using var probe = await http.GetAsync(url, ct);
            status = probe.StatusCode;
        }

        Assert.Equal(HttpStatusCode.OK, status);
        var front = await page.Context.NewPageAsync();
        try
        {
            var response = await front.GotoAsync(url.ToString());
            Assert.True(response?.Ok, $"The published page returned HTTP {response?.Status}");
            Assert.Contains(PageName, await front.ContentAsync(), StringComparison.Ordinal);
            Assert.Empty(await LoadImagesAsync(front));
        }
        finally
        {
            await front.CloseAsync();
        }

        // Preview opens a window whose frame shows the page under the site's path.
        var preview = await page.RunAndWaitForPopupAsync(() =>
            page.Locator("[data-mark='workspace-action:Umb.WorkspaceAction.Document.SaveAndPreview']").ClickAsync());
        try
        {
            IFrame? frame = null;
            for (var attempt = 0; attempt < 60 && frame is null; attempt++)
            {
                frame = preview.Frames.FirstOrDefault(candidate => candidate != preview.MainFrame
                    && Uri.TryCreate(candidate.Url, UriKind.Absolute, out var url) && url.Scheme.StartsWith("http", StringComparison.Ordinal));
                if (frame is null)
                    await Task.Delay(500, ct);
            }

            Assert.NotNull(frame);
            _log.WriteLine($"Preview frame: {new Uri(frame.Url).AbsolutePath}");
            Assert.StartsWith(site.Url("/").AbsolutePath, new Uri(frame.Url).AbsolutePath, StringComparison.OrdinalIgnoreCase);
            await frame.WaitForLoadStateAsync(LoadState.NetworkIdle);
            Assert.Contains(PageName, await frame.ContentAsync(), StringComparison.Ordinal);
        }
        finally
        {
            await preview.CloseAsync();
        }
    }

    /// <summary>Uploads an image to the media root, then requests it and a resized version under the site's path.</summary>
    private async Task UploadMediaAsync(IPage page, TestSite site, CancellationToken ct)
    {
        var file = Path.Combine(site.Root, "e2e-upload.png");
        using (var image = new Image<Rgba32>(320, 200, Color.CornflowerBlue))
            await image.SaveAsPngAsync(file, ct);

        await page.GotoAsync(site.Url("/umbraco/section/media").ToString());
        await page.Locator("umb-dropzone-media input[type=file], umb-dropzone input[type=file]").First.SetInputFilesAsync(file);
        // Several media types accept an image; pick Image if Umbraco asks.
        var picker = page.Locator("umb-dropzone-media-type-picker-modal");
        try
        {
            await picker.GetByText("Image", new() { Exact = true }).First.ClickAsync(new() { Timeout = 3_000 });
        }
        catch (TimeoutException)
        {
        }

        // Umbraco names the item after the file: "E2E Upload".
        var item = page.GetByText("E2E Upload", new() { Exact = true }).First;
        await Expect(item).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await item.ClickAsync();
        // Only the file's own URL contains its name; backoffice routes use keys.
        // Every place the backoffice shows the file, the image preview and the Info view's links,
        // must address it under the site's path exactly once.
        var references = page.Locator("a[href*='e2e-upload'], img[src*='e2e-upload']");
        var addresses = new List<Uri>();
        foreach (var workspaceView in new[] { "Umb.WorkspaceView.Media.Edit", "Umb.WorkspaceView.Media.Info" })
        {
            await page.Locator($"[data-mark='workspace:view-link:{workspaceView}']").ClickAsync();
            await Expect(references.First).ToBeAttachedAsync(new() { Timeout = 30_000 });
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            foreach (var found in await references.EvaluateAllAsync<string[]>("elements => elements.map(e => e.getAttribute('href') ?? e.getAttribute('src'))"))
            {
                var address = new Uri(new Uri(new Uri(page.Url), found).GetLeftPart(UriPartial.Path));
                _log.WriteLine($"Media reference ({workspaceView}): {address.AbsolutePath}");
                Assert.StartsWith(site.Url("/media/").AbsolutePath, address.AbsolutePath, StringComparison.OrdinalIgnoreCase);
                addresses.Add(address);
            }
        }

        var media = addresses[0];

        var original = await page.Context.APIRequest.GetAsync(media.ToString());
        Assert.True(original.Ok, $"The uploaded file returned HTTP {original.Status}");
        Assert.Equal(320, Image.Identify(await original.BodyAsync()).Width);
        var resized = await page.Context.APIRequest.GetAsync(media + "?width=100");
        Assert.True(resized.Ok, $"The resized file returned HTTP {resized.Status}");
        Assert.Equal(100, Image.Identify(await resized.BodyAsync()).Width);

        var view = await page.Context.NewPageAsync();
        try
        {
            var response = await view.GotoAsync(media.ToString());
            Assert.True(response?.Ok, $"Opening the uploaded file returned HTTP {response?.Status}");
        }
        finally
        {
            await view.CloseAsync();
        }
    }

    private static async Task<string> WaitForFileAsync(string directory, string pattern, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (Directory.EnumerateFiles(directory, pattern).FirstOrDefault() is { } file)
                return file;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"No {pattern} file appeared in {directory}.");
            await Task.Delay(250, ct);
        }
    }

    [GeneratedRegex("""https?://[^\s"'<>]+flow=reset-password[^\s"'<>]*""")]
    private static partial Regex ResetLink();

    [GeneratedRegex("publish", RegexOptions.IgnoreCase)]
    private static partial Regex PublishButton();

    /// <summary>One Chromium context for a site, recording every error the browser reports.</summary>
    private sealed class BrowserSession : IAsyncDisposable
    {
        private readonly IPlaywright _playwright;
        private readonly IBrowser _browser;
        private readonly TestSite _site;
        private readonly ITestOutputHelper _output;
        private readonly string _artifacts;
        private readonly ConcurrentDictionary<string, bool> _externalHosts = new(StringComparer.OrdinalIgnoreCase);
        private string _step = "start";

        private BrowserSession(IPlaywright playwright, IBrowser browser, IBrowserContext context, TestSite site, ITestOutputHelper output, string artifacts)
        {
            _playwright = playwright;
            _browser = browser;
            Context = context;
            _site = site;
            _output = output;
            _artifacts = artifacts;
        }

        public IBrowserContext Context { get; }

        public IPage Page { get; private set; } = null!;

        public ConcurrentQueue<string> Errors { get; } = new();

        public static async Task<BrowserSession> StartAsync(TestSite site, ITestOutputHelper output, string name)
        {
            var playwright = await Playwright.CreateAsync();
            var browser = await playwright.Chromium.LaunchAsync();
            var context = await browser.NewContextAsync();
            var artifacts = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "e2e-artifacts", name)).FullName;
            var session = new BrowserSession(playwright, browser, context, site, output, artifacts);
            // Context-wide, so every page, popup and new tab is checked.
            context.Console += (_, message) =>
            {
                if (message.Type == "error")
                    session.Errors.Enqueue($"[{session._step}] Console: {message.Text} ({message.Location})");
            };
            context.WebError += (_, error) => session.Errors.Enqueue($"[{session._step}] JavaScript: {error.Error}");
            context.RequestFailed += (_, request) =>
            {
                // Navigating away cancels requests that are still running; that is not a failure.
                if (request.Failure?.Contains("ERR_ABORTED", StringComparison.Ordinal) != true)
                    session.Errors.Enqueue($"[{session._step}] Request failed: {request.Method} {request.Url} {request.Failure}");
            };
            context.Response += (_, response) =>
            {
                if (response.Status >= 400)
                    session.Errors.Enqueue($"[{session._step}] HTTP {response.Status}: {response.Request.Method} {response.Url}");
            };
            context.Request += (_, request) =>
            {
                if (Uri.TryCreate(request.Url, UriKind.Absolute, out var url) && url.Scheme.StartsWith("http", StringComparison.Ordinal)
                    && !string.Equals(url.Authority, site.Address.Authority, StringComparison.OrdinalIgnoreCase))
                    session._externalHosts.TryAdd(url.Host, true);
            };
            session.Page = await context.NewPageAsync();
            return session;
        }

        public Task<IPage> NewPageAsync() => Context.NewPageAsync();

        public async Task StepAsync(string name, Func<Task> step)
        {
            _step = name;
            _output.WriteLine($"Step: {name}");
            try
            {
                await step();
            }
            catch
            {
                await CaptureAsync(name);
                throw;
            }
        }

        /// <summary>Gives late console messages from the last step a moment to arrive.</summary>
        public async Task SettleAsync()
        {
            _step = "end";
            await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        }

        private async Task CaptureAsync(string step)
        {
            var file = string.Concat(step.Split(Path.GetInvalidFileNameChars())).Replace(' ', '-');
            foreach (var (page, index) in Context.Pages.Select((page, index) => (page, index)))
            {
                try
                {
                    _output.WriteLine($"Page {index} at failure: {page.Url}");
                    await page.ScreenshotAsync(new() { Path = Path.Combine(_artifacts, $"{file}-{index}.png"), FullPage = true, Timeout = 10_000 });
                    _output.WriteLine(await page.Locator("body").AriaSnapshotAsync(new() { Timeout = 10_000 }));
                }
                catch (Exception exception)
                {
                    // Capturing must never hide the step's own failure.
                    _output.WriteLine($"Could not capture page {index}: {exception.Message}");
                }
            }

            _output.WriteLine($"Screenshots: {_artifacts}");
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var error in Errors)
                _output.WriteLine(error);
            if (!_externalHosts.IsEmpty)
                _output.WriteLine($"External hosts contacted: {string.Join(", ", _externalHosts.Keys.Order())}");
            await Context.DisposeAsync();
            await _browser.DisposeAsync();
            _playwright.Dispose();
        }
    }
}

/// <summary>
/// Writes test output to xUnit and to <c>e2e-artifacts/&lt;case&gt;/test-output.log</c>, which keeps the
/// diagnostics of passing runs too (for example as a CI artifact).
/// </summary>
internal sealed class TeeOutput(ITestOutputHelper inner, string file) : ITestOutputHelper
{
    public string Output => inner.Output;

    public static TeeOutput Create(ITestOutputHelper inner, string name)
    {
        var directory = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "e2e-artifacts", name)).FullName;
        var file = Path.Combine(directory, "test-output.log");
        File.WriteAllText(file, "");
        return new TeeOutput(inner, file);
    }

    public void Write(string message)
    {
        inner.Write(message);
        File.AppendAllText(file, message);
    }

    public void Write(string format, params object[] args) => Write(string.Format(format, args));

    public void WriteLine(string message)
    {
        inner.WriteLine(message);
        File.AppendAllText(file, message + Environment.NewLine);
    }

    public void WriteLine(string format, params object[] args) => WriteLine(string.Format(format, args));
}
