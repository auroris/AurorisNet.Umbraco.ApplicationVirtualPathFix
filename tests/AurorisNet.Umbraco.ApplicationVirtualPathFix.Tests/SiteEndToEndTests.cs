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
    [InlineData("/SomeTestPath", false, false, true, true)] // HTTPS and cross-site secure-cookie restoration.
    public async Task Starter_kit_site_works_end_to_end(string prefix, bool existingPathBase, bool mediaOutsideWebRoot, bool webSockets, bool https = false)
    {
        var ct = TestContext.Current.CancellationToken;
        var name = $"{(prefix.Length == 0 ? "root" : existingPathBase ? "iis" : "kestrel")}{(mediaOutsideWebRoot ? "-external-media" : "")}{(webSockets ? "" : "-no-websockets")}";
        if (https) name += "-https";
        // Set AVPF_E2E_CASE to case names, such as kestrel or root,kestrel, to run just those hosting cases.
        if (Environment.GetEnvironmentVariable("AVPF_E2E_CASE") is { Length: > 0 } only && !only.Split(',').Contains(name))
            Assert.Skip($"AVPF_E2E_CASE selects {only}.");
        var log = TeeOutput.Create(_log, name);
        _log = log;
        await using var site = await TestSite.StartAsync(new SiteOptions(prefix, existingPathBase, mediaOutsideWebRoot, webSockets, Https: https), ct, log.DirectoryPath);
        await using (var browser = await BrowserSession.StartAsync(site, _log, log.DirectoryPath))
        {
            await browser.StepAsync("OpenAPI documents", () => CheckOpenApiAsync(site, ct));
            await browser.StepAsync("Front end: every page and image", () => CrawlFrontEndAsync(browser.Page, site));
            await browser.StepAsync("Forgotten password email", () => FollowPasswordResetEmailAsync(browser, site, ct));
            await browser.StepAsync("Backoffice sign-in", () => SignInAsync(browser.Page, site));
            await browser.StepAsync("Session restore after reload", () => ReloadAndRestoreSessionAsync(browser.Page, site));
            if (https)
                await browser.StepAsync("Cross-site session restore", () => RestoreFromExternalLinkAsync(browser, site));
            await browser.StepAsync("Rich text, code editors and themes", () => CheckEditorsAndThemesAsync(browser.Page, site));
            await browser.StepAsync("Invitation email", () => FollowInvitationEmailAsync(browser, site, ct));
            await browser.StepAsync("Create and publish a page", () => CreateAndPublishPageAsync(browser.Page, site, ct));
            await browser.StepAsync("Upload a file and open it", () => UploadMediaAsync(browser.Page, site, ct));
            await browser.AssertNoErrorsAsync();
        }

        Assert.Empty(await site.StopAsync());
    }

    [Fact]
    public async Task Basic_authentication_login_works_in_a_subdirectory()
    {
        var ct = TestContext.Current.CancellationToken;
        var log = TeeOutput.Create(_log, "kestrel-basic-auth");
        _log = log;
        await using var site = await TestSite.StartAsync(new SiteOptions("/SomeTestPath", BasicAuth: true), ct, log.DirectoryPath);
        await using (var browser = await BrowserSession.StartAsync(site, _log, log.DirectoryPath))
        {
            await browser.StepAsync("Two-factor redirect", async () =>
            {
                using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
                using var response = await http.GetAsync(site.Url("/umbraco/basic-auth/2fa"), ct);
                Assert.Equal(HttpStatusCode.Found, response.StatusCode);
                Assert.Equal(site.Url("/umbraco/basic-auth").AbsolutePath, response.Headers.Location?.OriginalString);
                await browser.Page.GotoAsync(new Uri(site.Address, response.Headers.Location!).ToString());
                await Expect(browser.Page.Locator("#username")).ToBeVisibleAsync();
            });
            await browser.StepAsync("Basic authentication", async () =>
            {
                var page = browser.Page;
                var response = await page.GotoAsync(site.Url("/").ToString());
                Assert.True(response?.Ok, $"The front end returned HTTP {response?.Status}");
                Assert.StartsWith(site.Url("/umbraco/basic-auth/login").AbsolutePath, new Uri(page.Url).AbsolutePath, StringComparison.OrdinalIgnoreCase);
                await page.Locator("#username").FillAsync(TestSite.Email);
                await page.Locator("#password").FillAsync("Incorrect password");
                await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
                await Expect(page.GetByText("Invalid username or password.", new() { Exact = true })).ToBeVisibleAsync();
                var action = await page.Locator("form").First.EvaluateAsync<string>("form => form.action");
                Assert.StartsWith(site.Url("/umbraco/basic-auth").AbsolutePath, new Uri(action).AbsolutePath, StringComparison.OrdinalIgnoreCase);
                await page.Locator("#username").FillAsync(TestSite.Email);
                await page.Locator("#password").FillAsync(TestSite.Password);
                await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
                await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == site.Url("/").AbsolutePath);
                await Expect(page.Locator("main")).ToBeVisibleAsync();
            });
            await browser.AssertNoErrorsAsync();
        }

        Assert.Empty(await site.StopAsync());
    }

    private static async Task CheckOpenApiAsync(TestSite site, CancellationToken ct)
    {
        using var http = site.CreateHttpClient();
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
        Assert.StartsWith(backoffice + "/", new Uri(page.Url).AbsolutePath);
    }

    private static async Task RestoreFromExternalLinkAsync(BrowserSession browser, TestSite site)
    {
        var page = browser.Page;
        await page.GotoAsync("https://outside.example.test/");
        var navigation = await page.RunAndWaitForResponseAsync(
            () => page.GetByRole(AriaRole.Link, new() { Name = "Backoffice", Exact = true }).ClickAsync(),
            response => response.Request.IsNavigationRequest && response.Url.StartsWith(site.Url("/umbraco").ToString()));
        Assert.Equal("cross-site", await navigation.HeaderValueAsync("x-test-fetch-site"));
        Assert.Equal("no", await navigation.HeaderValueAsync("x-test-refresh-cookie"));
        await Expect(page.Locator("umb-umbraco-news-dashboard")).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await Expect(page.Locator("umb-app")).ToHaveAttributeAsync("data-restore-session", "");
    }

    private static async Task CheckEditorsAndThemesAsync(IPage page, TestSite site)
    {
        var css = page.WaitForResponseAsync(response => new Uri(response.Url).AbsolutePath.EndsWith("/css/rte-content.css"));
        await page.EvaluateAsync("""
            async () => {
              await import('@umbraco-cms/backoffice/tiptap');
              await import('@umbraco-cms/backoffice/code-editor');
              const container = document.createElement('div');
              container.id = 'test-editors';
              container.style.cssText = 'position:fixed;inset:80px 20px 20px;z-index:10000;background:white;overflow:auto';
              const rte = document.createElement('umb-input-tiptap');
              rte.value = '<p>Initial text</p>';
              const code = document.createElement('umb-code-editor');
              code.language = 'json'; code.code = '{"invalid": }';
              code.style.cssText = 'display:block;height:250px';
              container.append(rte, code);
              document.querySelector('umb-app').shadowRoot.appendChild(container);
            }
            """);
        var stylesheet = await css;
        Assert.True(stylesheet.Ok);
        Assert.StartsWith(site.Url("/umbraco/backoffice/").AbsolutePath, new Uri(stylesheet.Url).AbsolutePath);
        var richText = page.Locator("#test-editors umb-input-tiptap [contenteditable=true]");
        await richText.FillAsync("Edited rich text");
        await Expect(richText).ToHaveTextAsync("Edited rich text");
        await page.WaitForFunctionAsync("() => document.querySelector('umb-app').shadowRoot.querySelector('umb-code-editor')?.editor?.monacoEditor");
        var errors = await page.EvaluateAsync<int>("""
            async () => {
              const { monaco } = await import('@umbraco-cms/backoffice/external/monaco-editor');
              const model = document.querySelector('umb-app').shadowRoot.querySelector('umb-code-editor').editor.monacoEditor.getModel();
              const worker = await (await monaco.languages.json.getWorker())(model.uri);
              return (await worker.doValidation(model.uri.toString())).length;
            }
            """).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(errors > 0, "The JSON worker should report the deliberately invalid JSON.");
        // The editor alone need not render any codicons. Find displays them and loads their font.
        await page.EvaluateAsync("""
            () => document.querySelector('umb-app').shadowRoot.querySelector('umb-code-editor')
              .editor.monacoEditor.getAction('actions.find').run()
            """);
        await Expect(page.Locator("#test-editors .find-widget")).ToBeVisibleAsync();
        Assert.True(await page.EvaluateAsync<bool>("""
            async () => {
              const fonts = await document.fonts.load('16px codicon');
              return fonts.length > 0 && fonts.every(font => font.status === 'loaded');
            }
            """), "The code editor's icon font must load successfully.");
        await page.Locator("#test-editors").EvaluateAsync("element => element.remove()");

        foreach (var (theme, filename) in new[] { ("umb-dark-theme", "dark.css"), ("umb-high-contrast-theme", "high-contrast.css") })
        {
            await page.EvaluateAsync("theme => localStorage.setItem('umb-theme-alias', theme)", theme);
            var response = await page.RunAndWaitForResponseAsync(() => page.ReloadAsync(),
                response => new Uri(response.Url).AbsolutePath.EndsWith("/css/" + filename));
            Assert.True(response.Ok);
            Assert.StartsWith(site.Url("/umbraco/backoffice/").AbsolutePath, new Uri(response.Url).AbsolutePath);
            await Expect(page.Locator("umb-umbraco-news-dashboard")).ToBeVisibleAsync();
        }
        await page.EvaluateAsync("localStorage.removeItem('umb-theme-alias')");
    }

    private static async Task FollowInvitationEmailAsync(BrowserSession browser, TestSite site, CancellationToken ct)
    {
        var existing = Directory.GetFiles(site.MailDirectory, "*.eml").ToHashSet();
        var status = await browser.Page.EvaluateAsync<int>("""
            async groupId => {
              const { UserService } = await import('@umbraco-cms/backoffice/external/backend-api');
              const result = await UserService.postUserInvite({ body: {
                email: 'invitee@example.test', userName: 'invitee@example.test', name: 'Invited editor', userGroupIds: [{ id: groupId }]
              }});
              return result.response.status;
            }
            """, global::Umbraco.Cms.Core.Constants.Security.EditorGroupKey.ToString());
        Assert.Equal(201, status);
        string? body = null;
        for (var attempt = 0; attempt < 60 && body is null; attempt++)
        {
            foreach (var file in Directory.GetFiles(site.MailDirectory, "*.eml").Where(file => !existing.Contains(file)))
            {
                try
                {
                    using var message = await MimeMessage.LoadAsync(file, ct);
                    body = message.HtmlBody ?? message.TextBody;
                }
                catch (IOException) { }
            }
            if (body is null) await Task.Delay(250, ct);
        }
        Assert.NotNull(body);
        var match = Regex.Match(body, "https?://[^\\s\"'<>]+flow=invite[^\\s\"'<>]*");
        Assert.True(match.Success, "The invitation should contain a login link.");
        var link = new Uri(WebUtility.HtmlDecode(match.Value));
        Assert.Equal(site.Address.Authority, link.Authority);
        Assert.Equal(site.Url("/umbraco/login").AbsolutePath, link.AbsolutePath);
        var page = await browser.NewPageAsync();
        try
        {
            await page.GotoAsync(link.ToString());
            await Expect(page.GetByRole(AriaRole.Textbox, new() { Name = "New password", Exact = true }))
                .ToBeVisibleAsync(new() { Timeout = 30_000 });
        }
        finally { await page.CloseAsync(); }
    }

    /// <summary>Creates a page under Home, publishes it and verifies drafts in preview.</summary>
    private async Task CreateAndPublishPageAsync(IPage page, TestSite site, CancellationToken ct)
    {
        // In the IIS case the content slug repeats the application prefix. The host has
        // already stripped the first occurrence; the package must not strip the second.
        var pageName = site.Options.ExistingPathBase ? site.Options.Prefix.Trim('/') : PageName;
        var slug = site.Options.ExistingPathBase ? pageName.ToLowerInvariant() : "virtual-path-e2e-page";
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
        await page.Locator("[data-mark='input:entity-name'] input").FillAsync(pageName);
        await page.GetByRole(AriaRole.Textbox, new() { Name = "Page Title" }).First.FillAsync(pageName);
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
        var url = site.Url($"/{slug}/");
        using var http = site.CreateHttpClient();
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
            Assert.Contains(pageName, await front.ContentAsync(), StringComparison.Ordinal);
            Assert.Empty(await LoadImagesAsync(front));
        }
        finally
        {
            await front.CloseAsync();
        }

        // Preview must render the unpublished draft, rather than accidentally opening the live page.
        var draftTitle = pageName + " unpublished draft";
        await page.GetByRole(AriaRole.Textbox, new() { Name = "Page Title" }).First.FillAsync(draftTitle);
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
            await Expect(frame.Locator("body")).ToContainTextAsync(draftTitle, new() { Timeout = 30_000 });
            // Saving another draft should refresh the already-open frame through SignalR,
            // including the IIS case where WebSockets are unavailable.
            var updatedTitle = pageName + " updated draft";
            await page.GetByRole(AriaRole.Textbox, new() { Name = "Page Title" }).First.FillAsync(updatedTitle);
            await page.Locator("[data-mark='workspace-action:Umb.WorkspaceAction.Document.Save']").ClickAsync();
            await Expect(preview.FrameLocator("iframe").Locator("body")).ToContainTextAsync(updatedTitle, new() { Timeout = 30_000 });
            var published = await http.GetStringAsync(url, ct);
            Assert.Contains(pageName, published);
            Assert.DoesNotContain(draftTitle, published);
            Assert.DoesNotContain(updatedTitle, published);
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
        // Ask the authenticated API for a signed resize URL, as the thumbnail components do.
        // Appending width ourselves bypasses URL generation and is invalid when HMAC is enabled.
        var key = Regex.Match(page.Url, @"/workspace/media/edit/([a-f0-9-]+)").Groups[1].Value;
        Assert.True(Guid.TryParse(key, out _), "The media workspace must identify the uploaded item.");
        var resizeUrl = await page.EvaluateAsync<string>("""
            async id => {
              const { ImagingService } = await import('@umbraco-cms/backoffice/external/backend-api');
              const result = await ImagingService.getImagingResizeUrls({ query: { id: [id], width: 100, height: 0 } });
              if (result.error) throw new Error(JSON.stringify(result.error));
              return result.data[0].urlInfos[0].url;
            }
            """, key);
        Assert.StartsWith(site.Url("/media/").AbsolutePath, new Uri(resizeUrl).AbsolutePath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("hmac=", resizeUrl, StringComparison.Ordinal);
        var resized = await page.Context.APIRequest.GetAsync(resizeUrl);
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

        public static async Task<BrowserSession> StartAsync(TestSite site, ITestOutputHelper output, string artifacts)
        {
            var playwright = await Playwright.CreateAsync();
            var browser = await playwright.Chromium.LaunchAsync();
            var context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = site.Options.Https });
            var session = new BrowserSession(playwright, browser, context, site, output, artifacts);
            await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });
            await context.RouteAsync("**/*", async route =>
            {
                var url = new Uri(route.Request.Url);
                if (url.Authority == site.Address.Authority)
                    await route.ContinueAsync();
                else if (url.AbsoluteUri == "https://outside.example.test/")
                    await route.FulfillAsync(new() { ContentType = "text/html", Body = $"<a href=\"{site.Url("/umbraco")}\">Backoffice</a>" });
                else if (url.AbsoluteUri == "https://code.jquery.com/jquery-3.6.0.min.js")
                    await route.FulfillAsync(new()
                    {
                        ContentType = "application/javascript",
                        Path = Path.Combine(AppContext.BaseDirectory, "BrowserFixtures", "jquery-3.6.0.min.js"),
                        Headers = new Dictionary<string, string> { ["Access-Control-Allow-Origin"] = "*" },
                    });
                else if (url.Host == "fonts.googleapis.com")
                    await route.FulfillAsync(new() { ContentType = "text/css", Body = "/* Offline test fonts: use the normal fallbacks. */" });
                else if (url.Host is "www.youtube.com" or "www.youtube-nocookie.com" or "www.openstreetmap.org")
                    await route.FulfillAsync(new() { ContentType = "text/html", Body = "<!doctype html><title>Offline embed</title>" });
                else
                {
                    session.Errors.Enqueue($"Unexpected external request: {url}");
                    await route.AbortAsync();
                }
            });
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

        public Task AssertNoErrorsAsync() => StepAsync("Browser errors", () =>
        {
            Assert.Empty(Errors);
            return Task.CompletedTask;
        });

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
            try
            {
                // Save passing traces too: the server-log assertion runs after the browser closes.
                await Context.Tracing.StopAsync(new() { Path = Path.Combine(_artifacts, "trace.zip") });
            }
            finally
            {
                try { await Context.DisposeAsync(); }
                finally
                {
                    try { await _browser.DisposeAsync(); }
                    finally { _playwright.Dispose(); }
                }
            }
        }
    }
}

/// <summary>
/// Writes test output to xUnit and to <c>e2e-artifacts/&lt;case&gt;/&lt;run&gt;/test-output.log</c>, which keeps the
/// diagnostics of passing runs too (for example as a CI artifact).
/// </summary>
internal sealed class TeeOutput(ITestOutputHelper inner, string file) : ITestOutputHelper
{
    public string Output => inner.Output;
    public string DirectoryPath => Path.GetDirectoryName(file)!;

    public static TeeOutput Create(ITestOutputHelper inner, string name)
    {
        var run = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
        var directory = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "e2e-artifacts", name, run)).FullName;
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
