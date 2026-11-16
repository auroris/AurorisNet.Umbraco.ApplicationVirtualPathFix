using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Playwright;
using MimeKit;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using static Microsoft.Playwright.Assertions;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests.Browser;

/// <summary>
/// Drives new Umbraco sites with the starter kit through Chromium, one hosting case per test. Browser console errors,
/// uncaught exceptions, failed requests, HTTP errors, requests to the site's host outside the virtual path and unexpected
/// external requests fail a case, except the expected 401s when sign-out ends the session under the SSE connection or a
/// management API request that is still loading, and three known Umbraco errors that have nothing to do with the virtual
/// path. Errors in the site's log and warnings from this package also fail it.
/// </summary>
public sealed partial class SiteEndToEndTests(ITestOutputHelper output)
{
    private const string PageName = "Virtual path e2e page";

    /// <summary>
    /// How long a step waits for the site or the browser before it fails. Cases running side by side can slow a site far
    /// below its usual speed, so the limit is generous. Every wait ends as soon as its condition holds, so a passing case
    /// takes no longer because of it.
    /// </summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    private ITestOutputHelper _log = output;

    /// <summary>
    /// Checks that every affected feature works on one site that combines a nested prefix, HTTPS, media outside the web
    /// root and <c>Hosting:Debug=false</c>. The steps cover the front end, the error page, emails, the contact form,
    /// sign-in, the health check, patched modules and caching, session restore, editors, the block grid, publishing,
    /// preview, media, the Delivery API and sign-out.
    /// </summary>
    [Fact]
    public async Task Patched_features_work_under_a_nested_https_path()
    {
        var ct = TestContext.Current.CancellationToken;
        var log = StartCase("full");
        // Run each affected feature once, on one site that combines independent hosting settings.
        await using var site = await TestSite.StartAsync(new SiteOptions("/SomeTestPath/Nested",
            MediaOutsideWebRoot: true, Https: true, Debug: false), ct, log.DirectoryPath);
        await using (var browser = await BrowserSession.StartAsync(site, _log, log.DirectoryPath))
        {
            await browser.StepAsync("OpenAPI documents", () => CheckOpenApiAsync(site, ct));
            await browser.StepAsync("Front end: home and nested page", () => CheckFrontEndAsync(browser.Page, site));
            await browser.StepAsync("Forgotten password email", () => FollowPasswordResetEmailAsync(browser, site, ct));
            await browser.StepAsync("Built-in error page", () => CheckErrorPageAsync(site, ct));
            await browser.StepAsync("Contact form", () => SubmitContactFormAsync(site, ct));
            await browser.StepAsync("Backoffice sign-in", () => SignInAsync(browser.Page, site));
            await browser.StepAsync("Server context", () => CheckServerContextAsync(browser.Page, site));
            await browser.StepAsync("Health check", () => CheckHealthAsync(browser.Page, site));
            await browser.StepAsync("Patched modules and HTTP caching", () => CheckPatchedAssetsAsync(browser.Page, site, ct));
            await browser.StepAsync("Session restore after reload", () => ReloadAndRestoreSessionAsync(browser.Page, site));
            await browser.StepAsync("Cross-site session restore", () => RestoreFromExternalLinkAsync(browser, site));
            await browser.StepAsync("Rich text, code editors and themes", () => CheckEditorsAndThemesAsync(browser.Page, site));
            await browser.StepAsync("Block grid stylesheets", () => CheckBlockGridAsync(browser.Page, site));
            await browser.StepAsync("Invitation email", () => FollowInvitationEmailAsync(browser, site, ct));
            await browser.StepAsync("Create and publish a page", () => CreateAndPublishPageAsync(browser.Page, site, ct));
            await browser.StepAsync("Upload a file and open it", () => UploadMediaAsync(browser.Page, site, ct));
            await browser.StepAsync("Delivery API", () => CheckDeliveryApiAsync(site, ct));
            await browser.StepAsync("Sign out", () => SignOutAsync(browser.Page, site));
            await browser.AssertNoErrorsAsync();
        }

        Assert.Empty(await site.StopAsync());
    }

    /// <summary>
    /// Checks that a site behind an IIS path base, on a host without WebSockets, serves a page whose slug repeats the
    /// prefix and uses Server-Sent Events for the server event hub and preview. The case also covers the OpenAPI
    /// documents, sign-in, session restore, the Delivery API and sign-out.
    /// </summary>
    [Fact]
    public async Task Iis_path_base_preserves_repeated_slugs_and_sse_preview()
    {
        var ct = TestContext.Current.CancellationToken;
        var log = StartCase("iis");
        await using var site = await TestSite.StartAsync(new SiteOptions("/SomeTestPath",
            TestHost.Iis, WebSockets: false), ct, log.DirectoryPath);
        await using (var browser = await BrowserSession.StartAsync(site, _log, log.DirectoryPath))
        {
            await browser.StepAsync("OpenAPI documents", () => CheckOpenApiAsync(site, ct));
            await browser.StepAsync("Backoffice sign-in", () => SignInAsync(browser.Page, site));
            await browser.StepAsync("Server context and SSE", () => CheckServerContextAsync(browser.Page, site));
            await browser.StepAsync("Session restore after reload", () => ReloadAndRestoreSessionAsync(browser.Page, site));
            await browser.StepAsync("Repeated content slug and SSE preview", () => CreateAndPublishPageAsync(browser.Page, site, ct));
            await browser.StepAsync("Delivery API", () => CheckDeliveryApiAsync(site, ct));
            await browser.StepAsync("Sign out", () => SignOutAsync(browser.Page, site));
            await browser.AssertNoErrorsAsync();
        }

        Assert.Empty(await site.StopAsync());
    }

    /// <summary>
    /// Checks that a site behind a TLS-terminating reverse proxy, which passes the public host, scheme and prefix in
    /// forwarded headers, keeps its public URLs: the home page and its images, sign-in, the server URL, session restore
    /// after a reload and from another site, and sign-out. An empty <paramref name="prefix"/> covers a site at the root.
    /// </summary>
    [Theory]
    [InlineData("/SomeTestPath")]
    [InlineData("")]
    public async Task Forwarded_headers_preserve_public_urls(string prefix)
    {
        var ct = TestContext.Current.CancellationToken;
        var log = StartCase(prefix.Length == 0 ? "proxy-root" : "proxy");
        await using var site = await TestSite.StartAsync(new SiteOptions(prefix,
            TestHost.ReverseProxy, Https: true), ct, log.DirectoryPath);
        await using (var browser = await BrowserSession.StartAsync(site, _log, log.DirectoryPath))
        {
            await browser.StepAsync("Public home page", async () =>
            {
                var response = await browser.Page.GotoAsync(site.Url("/").ToString());
                Assert.True(response?.Ok);
                await Expect(browser.Page.Locator("main")).ToBeVisibleAsync();
                Assert.Empty(await LoadImagesAsync(browser.Page));
            });
            await browser.StepAsync("Backoffice sign-in", () => SignInAsync(browser.Page, site));
            await browser.StepAsync("Forwarded server URL", () => CheckServerContextAsync(browser.Page, site));
            await browser.StepAsync("Session restore after reload", () => ReloadAndRestoreSessionAsync(browser.Page, site));
            await browser.StepAsync("Cross-site session restore", () => RestoreFromExternalLinkAsync(browser, site));
            await browser.StepAsync("Sign out", () => SignOutAsync(browser.Page, site));
            await browser.AssertNoErrorsAsync();
        }

        Assert.Empty(await site.StopAsync());
    }

    /// <summary>
    /// Checks that basic authentication works under the virtual path: the two-factor route redirects to the prefixed login
    /// page, a wrong password shows the error while the form still posts under the prefix, and the right password opens
    /// the front end.
    /// </summary>
    [Fact]
    public async Task Basic_authentication_login_works_in_a_subdirectory()
    {
        var ct = TestContext.Current.CancellationToken;
        var log = StartCase("basic-auth");
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

    /// <summary>
    /// Skips the test when <c>AVPF_E2E_CASE</c> is set and omits <paramref name="name"/>. Otherwise starts copying the test
    /// output to a new artifacts folder for the case and returns that output.
    /// </summary>
    private TeeOutput StartCase(string name)
    {
        // Skip before installing a site, so unselected cases finish at once.
        if (Environment.GetEnvironmentVariable("AVPF_E2E_CASE") is { Length: > 0 } only && !only.Split(',').Contains(name))
            Assert.Skip($"AVPF_E2E_CASE selects {only}.");
        var log = TeeOutput.Create(_log, name);
        _log = log;
        return log;
    }

    /// <summary>
    /// Checks that Umbraco's built-in error page for a missing URL links its stylesheet under the virtual path, and that
    /// every font and image in that stylesheet loads.
    /// </summary>
    private static async Task CheckErrorPageAsync(TestSite site, CancellationToken ct)
    {
        using var http = site.CreateHttpClient();
        var missing = site.Url("/no-such-page/");
        using var response = await http.GetAsync(missing, ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var href = Regex.Match(await response.Content.ReadAsStringAsync(ct), """<link\b[^>]*\bhref="([^"]*nonodes\.css[^"]*)""").Groups[1].Value;
        var stylesheet = new Uri(missing, WebUtility.HtmlDecode(href));
        Assert.Equal(site.Url("/umbraco/website/nonodes.css").AbsolutePath, stylesheet.AbsolutePath);
        var assets = Regex.Matches(await http.GetStringAsync(stylesheet, ct), """url\(['"]?([^'")]+)""")
            .Select(match => new Uri(stylesheet, match.Groups[1].Value)).Distinct().ToArray();
        Assert.NotEmpty(assets);
        foreach (var asset in assets)
        {
            using var file = await http.GetAsync(asset, ct);
            Assert.True(file.IsSuccessStatusCode, $"{asset.AbsolutePath} returned HTTP {(int)file.StatusCode}");
        }
    }

    /// <summary>
    /// Submits the starter kit's contact form, an Umbraco surface form, and checks that its action and the redirect after
    /// posting keep the virtual path.
    /// </summary>
    private static async Task SubmitContactFormAsync(TestSite site, CancellationToken ct)
    {
        var contact = site.Url("/contact/");
        using var http = site.CreateHttpClient(followRedirects: false);
        var form = Regex.Match(await http.GetStringAsync(contact, ct), "<form[\\s\\S]*?</form>").Value;
        Assert.Contains($"action=\"{contact.AbsolutePath}\"", form, StringComparison.Ordinal);
        var fields = Regex.Matches(form, """<input\b[^>]*\bname="(__RequestVerificationToken|ufprt)"[^>]*\bvalue="([^"]*)""")
            .ToDictionary(match => match.Groups[1].Value, match => WebUtility.HtmlDecode(match.Groups[2].Value));
        Assert.Equal(2, fields.Count);
        fields["Name"] = "Visitor";
        fields["Email"] = "visitor@example.test";
        fields["Message"] = "Hello from under the virtual path.";
        using var response = await http.PostAsync(contact, new FormUrlEncodedContent(fields), ct);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(contact.AbsolutePath, response.Headers.Location?.OriginalString);
    }

    /// <summary>
    /// Checks that the management API's OpenAPI document and UI load under the virtual path: Umbraco 17 serves them with
    /// Swashbuckle under <c>/umbraco/swagger</c>, and Umbraco 18 under <c>/umbraco/openapi</c>.
    /// </summary>
    private static async Task CheckOpenApiAsync(TestSite site, CancellationToken ct)
    {
        var swagger = UmbracoUnderTest.Release.Major < 18;
        using var http = site.CreateHttpClient();
        using var document = await http.GetAsync(site.Url(swagger ? "/umbraco/swagger/management/swagger.json" : "/umbraco/openapi/management.json"), ct);
        Assert.Equal(HttpStatusCode.OK, document.StatusCode);
        Assert.Contains("\"openapi\"", await document.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        using var ui = await http.GetAsync(site.Url(swagger ? "/umbraco/swagger/" : "/umbraco/openapi/"), ct);
        Assert.Equal(HttpStatusCode.OK, ui.StatusCode);
    }

    /// <summary>Checks that the home page and a nested blog post load at their prefixed URLs with all their images.</summary>
    private static async Task CheckFrontEndAsync(IPage page, TestSite site)
    {
        foreach (var path in new[] { "/", "/blog/my-blog-post/" })
        {
            var url = site.Url(path);
            var response = await page.GotoAsync(url.ToString());
            Assert.True(response?.Ok, $"{url.AbsolutePath} returned HTTP {response?.Status}");
            Assert.Equal(url.AbsolutePath, new Uri(page.Url).AbsolutePath);
            await Expect(page.Locator("main")).ToBeVisibleAsync();
            var broken = await LoadImagesAsync(page);
            Assert.True(broken.Length == 0, $"Images failed to load on {url.AbsolutePath}: {string.Join(", ", broken)}");
        }
    }

    /// <summary>
    /// Loads every image on <paramref name="page"/>, lazy ones included, and returns the URLs of those that failed or are
    /// still loading after <see cref="Patience"/>. An SVG with no intrinsic width counts as loaded.
    /// </summary>
    private static Task<string[]> LoadImagesAsync(IPage page) => page.EvaluateAsync<string[]>("""
        async timeout => {
          const images = [...document.images];
          for (const image of images) image.loading = 'eager';
          await Promise.all(images.filter(i => !i.complete).map(i => new Promise(done => {
            i.addEventListener('load', done, { once: true });
            i.addEventListener('error', done, { once: true });
            setTimeout(done, timeout);
          })));
          return images
            .filter(i => i.currentSrc && (!i.complete || (i.naturalWidth === 0 && !/\.svg(\?|$)/i.test(i.currentSrc))))
            .map(i => i.currentSrc);
        }
        """, Patience.TotalMilliseconds);

    /// <summary>
    /// Requests a password reset from the login page and checks that the emailed link points at the prefixed login page on
    /// the site's host. Then opens the link and waits for the new-password form.
    /// </summary>
    private async Task FollowPasswordResetEmailAsync(BrowserSession browser, TestSite site, CancellationToken ct)
    {
        var page = browser.Page;
        var response = await page.GotoAsync(site.Url("/umbraco").ToString());
        Assert.True(response?.Ok, $"The backoffice returned HTTP {response?.Status}");
        // A click that lands before the login element finishes rendering leaves the view unchanged, so keep clicking until
        // the form appears.
        var emailInput = page.Locator("#email input");
        await WaitClickingAsync(page.Locator("#forgot-password"), () => emailInput.IsVisibleAsync(),
            () => "The forgotten-password form did not open.", ct);

        await emailInput.FillAsync(TestSite.Email);
        await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();

        await FollowEmailLinkAsync(browser, site, ResetLink(), ct);
    }

    /// <summary>
    /// Signs in to the backoffice, opened with the prefix in lowercase, and waits for the dashboard's news. Then opens the
    /// system information from the header logo and checks that the logo loads from under the virtual path.
    /// </summary>
    private static async Task SignInAsync(IPage page, TestSite site)
    {
        // Open the backoffice with the prefix in lowercase, as a bookmark with different casing would on IIS.
        var address = new Uri(site.Address, site.Options.Prefix.ToLowerInvariant() + "/umbraco");
        var response = await page.GotoAsync(address.ToString());
        Assert.True(response?.Ok, $"The backoffice returned HTTP {response?.Status}");
        await page.Locator("#username-input").FillAsync(TestSite.Email);
        await page.Locator("#password-input").FillAsync(TestSite.Password);
        await page.Locator("#umb-login-button").ClickAsync();
        var dashboard = page.Locator("umb-umbraco-news-dashboard");
        await Expect(dashboard.GetByRole(AriaRole.Heading, new() { Name = TestSite.NewsHeading, Exact = true })).ToBeVisibleAsync();

        await page.GetByRole(AriaRole.Button, new() { Name = "View Umbraco CMS system information and version number", Exact = true }).ClickAsync();
        var logo = page.Locator("umb-backoffice-header-logo img#logo");
        await Expect(logo).ToBeVisibleAsync();
        await Expect(logo).ToHaveJSPropertyAsync("complete", true);
        Assert.True(await logo.EvaluateAsync<bool>("image => image.complete && image.naturalWidth > 0"));
        Assert.StartsWith(site.Url("/umbraco/backoffice/").AbsolutePath, new Uri(await logo.EvaluateAsync<string>("image => image.src")).AbsolutePath);
        await page.Keyboard.PressAsync("Escape");
    }

    /// <summary>
    /// Reloads the backoffice and checks that it restores the session by refreshing its token under the virtual path, then
    /// shows the dashboard at a prefixed URL.
    /// </summary>
    private static async Task ReloadAndRestoreSessionAsync(IPage page, TestSite site)
    {
        var backoffice = site.Url("/umbraco").AbsolutePath;
        var token = page.WaitForResponseAsync(response =>
            new Uri(response.Url).AbsolutePath == backoffice + "/management/api/v1/security/back-office/token"
            && response.Request.PostData?.Contains("grant_type=refresh_token", StringComparison.Ordinal) == true);
        await page.ReloadAsync();
        Assert.True((await token).Ok, "Restoring the session must refresh its token successfully.");
        var dashboard = page.Locator("umb-umbraco-news-dashboard");
        await Expect(dashboard.GetByRole(AriaRole.Heading, new() { Name = TestSite.NewsHeading, Exact = true })).ToBeVisibleAsync();
        Assert.StartsWith(backoffice + "/", new Uri(page.Url).AbsolutePath);
    }

    /// <summary>
    /// Follows a link to the backoffice from another site, a navigation on which the browser withholds the SameSite=Strict
    /// refresh cookie, and checks that the page still restores the session and shows the dashboard.
    /// </summary>
    private static async Task RestoreFromExternalLinkAsync(BrowserSession browser, TestSite site)
    {
        var page = browser.Page;
        await page.GotoAsync("https://outside.example.test/");
        var navigation = await page.RunAndWaitForResponseAsync(
            () => page.GetByRole(AriaRole.Link, new() { Name = "Backoffice", Exact = true }).ClickAsync(),
            response => response.Request.IsNavigationRequest && response.Url.StartsWith(site.Url("/umbraco").ToString()));
        Assert.Equal("cross-site", await navigation.HeaderValueAsync("x-test-fetch-site"));
        Assert.Equal("no", await navigation.HeaderValueAsync("x-test-refresh-cookie"));
        await Expect(page.Locator("umb-umbraco-news-dashboard")).ToBeVisibleAsync();
        await Expect(page.Locator("umb-app")).ToHaveAttributeAsync("data-restore-session", "");
    }

    /// <summary>
    /// Checks that the rich text editor loads its content stylesheet and two configured stylesheets under the virtual path,
    /// that the code editor's JSON worker and icon font load, and that the dark and high-contrast themes load their
    /// stylesheets.
    /// </summary>
    private static async Task CheckEditorsAndThemesAsync(IPage page, TestSite site)
    {
        // Configure two rich text stylesheets: one relative to the stylesheet root, as the picker saves it, and one that
        // includes the root, which Umbraco also accepts.
        string[] stylesheets = ["test-rte.css", "test-rte-root.css"];
        Directory.CreateDirectory(Path.Combine(site.Root, "wwwroot", "css"));
        foreach (var name in stylesheets)
            await File.WriteAllTextAsync(Path.Combine(site.Root, "wwwroot", "css", name), "p { --test-rte-stylesheet: loaded; }",
                TestContext.Current.CancellationToken);
        var configured = stylesheets.Select(name => page.WaitForResponseAsync(response => new Uri(response.Url).AbsolutePath.EndsWith("/" + name))).ToArray();
        var css = page.WaitForResponseAsync(response => new Uri(response.Url).AbsolutePath.EndsWith("/css/rte-content.css"));
        await page.EvaluateAsync("""
            async () => {
              await import('@umbraco-cms/backoffice/tiptap');
              await import('@umbraco-cms/backoffice/code-editor');
              const { UmbPropertyEditorConfigCollection } = await import('@umbraco-cms/backoffice/property-editor');
              const container = document.createElement('div');
              container.id = 'test-editors';
              container.style.cssText = 'position:fixed;inset:80px 20px 20px;z-index:10000;background:white;overflow:auto';
              const rte = document.createElement('umb-input-tiptap');
              // Up to 17.6 and 18.1 the editor adds its stylesheets only when an extension brings styles, as the heading
              // extension does. A real editor's configuration always has extensions.
              rte.configuration = new UmbPropertyEditorConfigCollection([
                { alias: 'extensions', value: ['Umb.Tiptap.Heading'] },
                { alias: 'stylesheets', value: ['/test-rte.css', '/css/test-rte-root.css'] },
              ]);
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
        foreach (var (response, name) in (await Task.WhenAll(configured)).Zip(stylesheets))
        {
            Assert.True(response.Ok, $"{response.Url} returned HTTP {response.Status}");
            Assert.Equal(site.Url("/css/" + name).AbsolutePath, new Uri(response.Url).AbsolutePath);
        }
        var richText = page.Locator("#test-editors umb-input-tiptap [contenteditable=true]");
        await richText.FillAsync("Edited rich text");
        await Expect(richText).ToHaveTextAsync("Edited rich text");
        await page.WaitForFunctionAsync("() => document.querySelector('umb-app').shadowRoot.querySelector('umb-code-editor')?.editor?.monacoEditor");
        // Monaco sets up its JSON support in the background after the editor starts, and until then it refuses requests
        // for the JSON worker with "JSON not registered!". Ask again until it answers.
        var errors = await page.EvaluateAsync<int>("""
            async () => {
              const { monaco } = await import('@umbraco-cms/backoffice/external/monaco-editor');
              const model = document.querySelector('umb-app').shadowRoot.querySelector('umb-code-editor').editor.monacoEditor.getModel();
              for (;;) {
                try {
                  const worker = await (await monaco.languages.json.getWorker())(model.uri);
                  return (await worker.doValidation(model.uri.toString())).length;
                } catch (error) {
                  if (!String(error).includes('JSON not registered')) throw error;
                  await new Promise(resolve => setTimeout(resolve, 250));
                }
              }
            }
            """).WaitAsync(Patience);
        Assert.True(errors > 0, "The JSON worker should report the deliberately invalid JSON.");
        // The editor may render no codicons on its own. The Find widget shows some, which loads their font.
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

        // Umbraco 17.5 names the theme stylesheets dark.theme.css and high-contrast.theme.css.
        var suffix = UmbracoUnderTest.Release is { Major: 17, Minor: 5 } ? ".theme.css" : ".css";
        foreach (var (theme, filename) in new[] { ("umb-dark-theme", "dark" + suffix), ("umb-high-contrast-theme", "high-contrast" + suffix) })
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

    /// <summary>
    /// Invites a user through the management API and checks that the emailed link points at the prefixed login page on the
    /// site's host. Then opens the link and waits for the new-password form.
    /// </summary>
    private async Task FollowInvitationEmailAsync(BrowserSession browser, TestSite site, CancellationToken ct)
    {
        var status = await browser.Page.EvaluateAsync<int>("""
            async groupId => {
              const { UserService } = await import('@umbraco-cms/backoffice/external/backend-api');
              const result = await UserService.postUserInvite({ body: {
                email: 'invitee@example.test', userName: 'invitee@example.test', name: 'Invited editor', userGroupIds: [{ id: groupId }]
              }});
              return result.response.status;
            }
            """, global::Umbraco.Cms.Core.Constants.Security.EditorGroupKey.ToString()).WaitAsync(Patience, ct);
        Assert.Equal(201, status);
        await FollowEmailLinkAsync(browser, site, InvitationLink(), ct);
    }

    /// <summary>
    /// Creates and publishes a page under Home and checks it on the front end. Then checks that preview shows unpublished
    /// drafts and refreshes when another draft is saved, and that the website's End preview button ends preview through
    /// the prefixed management API.
    /// </summary>
    private async Task CreateAndPublishPageAsync(IPage page, TestSite site, CancellationToken ct)
    {
        // In the IIS case the page's slug repeats the application prefix. The host removes the first occurrence, and the
        // package must keep the second.
        var pageName = site.Options.ExistingPathBase ? site.Options.Prefix.Trim('/') : PageName;
        var slug = site.Options.ExistingPathBase ? pageName.ToLowerInvariant() : "virtual-path-e2e-page";
        await page.GotoAsync(site.Url("/umbraco/section/content").ToString());
        // The workspace's router slot overlaps the tree item's actions button, so dispatch the click to the button.
        var actions = page.GetByRole(AriaRole.Button, new() { Name = "View actions for 'Home'", Exact = true });
        await Expect(actions).ToBeAttachedAsync();
        await actions.DispatchEventAsync("click");
        // data-mark attributes are the selectors Umbraco's own acceptance tests use. This one is in Home's actions menu,
        // not the Content root's create button, which is always visible.
        await page.Locator("umb-entity-action-list [data-mark='entity-action:Umb.EntityAction.Document.Create']")
            .Filter(new() { Visible = true }).First.ClickAsync();
        var documentTypes = page.Locator("uui-ref-node-document-type");
        await Expect(documentTypes.First).ToBeVisibleAsync();
        // The starter kit's Content Page needs only a page title.
        var names = await documentTypes.EvaluateAllAsync<string[]>("types => types.map(type => type.name ?? '')");
        var index = Array.FindIndex(names, name => name.StartsWith("Content Page", StringComparison.Ordinal));
        Assert.True(index >= 0, $"The starter kit's Content Page is missing. Found: {string.Join(", ", names)}");
        _log.WriteLine($"Document types: {string.Join(", ", names)}; using {names.ElementAtOrDefault(index)}");
        await documentTypes.Nth(index).ClickAsync();
        await page.Locator("[data-mark='input:entity-name'] input").FillAsync(pageName);
        await page.GetByRole(AriaRole.Textbox, new() { Name = "Page Title" }).First.FillAsync(pageName);
        await page.Locator("[data-mark='workspace-action:Umb.WorkspaceAction.Document.SaveAndPublish']").ClickAsync();

        // An invariant document publishes at once, and the workspace then moves from its create route to the new document's
        // edit route. A variant document first asks which variants to publish. Wait for whichever comes, so a dialog that
        // appears late is still confirmed.
        var confirm = page.Locator("umb-document-publish-modal, umb-document-save-and-publish-modal")
            .GetByRole(AriaRole.Button, new() { NameRegex = PublishButton() }).First;
        await WaitClickingAsync(confirm, () => Task.FromResult(page.Url.Contains("/workspace/document/edit/", StringComparison.Ordinal)),
            () => $"Umbraco did not create the page: the workspace is still at {new Uri(page.Url).AbsolutePath}.", ct);

        // The new page is published under Home, which the starter kit serves at the site root.
        var url = site.Url($"/{slug}/");
        using var http = site.CreateHttpClient();
        var status = default(HttpStatusCode);
        await WaitUntilAsync(async () =>
        {
            using var probe = await http.GetAsync(url, ct);
            status = probe.StatusCode;
            return status == HttpStatusCode.OK;
        }, () => $"The published page {url.AbsolutePath} still returns HTTP {(int)status}.", ct);

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

        // Preview must render the unpublished draft, not the live page.
        var draftTitle = pageName + " unpublished draft";
        var updatedTitle = pageName + " updated draft";
        await page.GetByRole(AriaRole.Textbox, new() { Name = "Page Title" }).First.FillAsync(draftTitle);
        var preview = await page.RunAndWaitForPopupAsync(() =>
            page.Locator("[data-mark='workspace-action:Umb.WorkspaceAction.Document.SaveAndPreview']").ClickAsync());
        try
        {
            if (!site.Options.WebSockets)
            {
                await Expect(preview.Locator("umb-app")).ToBeAttachedAsync();
                await AssertServerSentEventsAsync(preview, "PreviewHub");
            }
            // The preview window loads the page into a frame of its own.
            var frame = preview.FrameLocator("iframe").Locator("body");
            await Expect(frame).ToContainTextAsync(draftTitle);
            var frameUrl = new Uri(await frame.EvaluateAsync<string>("body => body.ownerDocument.URL"));
            _log.WriteLine($"Preview frame: {frameUrl.AbsolutePath}");
            Assert.StartsWith(site.Url("/").AbsolutePath, frameUrl.AbsolutePath, StringComparison.OrdinalIgnoreCase);
            // Saving another draft refreshes the open frame through SignalR, also in the IIS case without WebSockets.
            await page.GetByRole(AriaRole.Textbox, new() { Name = "Page Title" }).First.FillAsync(updatedTitle);
            await page.Locator("[data-mark='workspace-action:Umb.WorkspaceAction.Document.Save']").ClickAsync();
            await Expect(frame).ToContainTextAsync(updatedTitle);
            var published = await http.GetStringAsync(url, ct);
            Assert.Contains(pageName, published);
            Assert.DoesNotContain(draftTitle, published);
            Assert.DoesNotContain(updatedTitle, published);
        }
        finally
        {
            await preview.CloseAsync();
        }

        // While preview is active, the website shows a badge. Its End preview button calls the management API and reloads
        // the page, which must then render without preview.
        var website = await page.Context.NewPageAsync();
        try
        {
            await website.GotoAsync(url.ToString());
            var end = website.Locator("umb-website-preview button.end");
            await Expect(end).ToBeVisibleAsync();
            var reloaded = website.WaitForResponseAsync(response => response.Request.IsNavigationRequest);
            var ended = await website.RunAndWaitForResponseAsync(() => end.ClickAsync(),
                response => response.Request.Method == "DELETE" && new Uri(response.Url).AbsolutePath.EndsWith("/management/api/v1/preview"));
            Assert.True(ended.Ok, $"Ending preview returned HTTP {ended.Status}");
            Assert.Equal(site.Url("/umbraco/management/api/v1/preview").AbsolutePath, new Uri(ended.Url).AbsolutePath);
            Assert.True((await reloaded).Ok);
            await Expect(website.Locator("umb-website-preview")).ToHaveCountAsync(0);
            Assert.DoesNotContain(updatedTitle, await website.ContentAsync());
        }
        finally
        {
            await website.CloseAsync();
        }
    }

    /// <summary>
    /// Uploads an image to the media root and checks that the backoffice's links to it, the file itself and a signed
    /// resized version are under the virtual path. Then checks the image on the front end with
    /// <see cref="CheckRenderedMediaAsync"/>.
    /// </summary>
    private async Task UploadMediaAsync(IPage page, TestSite site, CancellationToken ct)
    {
        var file = Path.Combine(site.Root, "e2e-upload.png");
        using (var image = new Image<Rgba32>(320, 200, Color.CornflowerBlue))
            await image.SaveAsPngAsync(file, ct);

        await page.GotoAsync(site.Url("/umbraco/section/media").ToString());
        await page.Locator("umb-dropzone-media input[type=file], umb-dropzone input[type=file]").First.SetInputFilesAsync(file);
        // Several media types accept an image, so Umbraco may ask which one to create; pick Image if it does. Wait for
        // whichever comes, the question or the new item, so a question that appears late is still answered. Umbraco names
        // the item after the file: "E2E Upload", or "e2e-upload.png" in the releases that keep the file name. Only the
        // created item is a link; the card shown while the file uploads has the file's name too, but opens nothing.
        var choice = page.Locator("umb-dropzone-media-type-picker-modal").GetByText("Image", new() { Exact = true }).First;
        var item = page.GetByRole(AriaRole.Link, new() { NameRegex = UploadedMediaName() }).First;
        await WaitClickingAsync(choice, async () => await item.IsVisibleAsync() && !await choice.IsVisibleAsync(),
            () => "The uploaded image did not appear in the media section.", ct);
        await item.ClickAsync();
        // Only the file's own URL contains its name; backoffice routes use keys. The image preview and the Info view's
        // links must each address the file under the site's path, with the prefix once.
        var references = page.Locator("a[href*='e2e-upload'], img[src*='e2e-upload']");
        var addresses = new List<Uri>();
        foreach (var workspaceView in new[] { "Umb.WorkspaceView.Media.Edit", "Umb.WorkspaceView.Media.Info" })
        {
            await page.Locator($"[data-mark='workspace:view-link:{workspaceView}']").ClickAsync();
            await Expect(references.First).ToBeAttachedAsync();
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
        // Ask the authenticated API for a signed resize URL, as the thumbnail components do. A width appended by hand
        // skips URL generation and fails HMAC validation.
        var key = Regex.Match(page.Url, @"/workspace/media/edit/([a-f0-9-]+)").Groups[1].Value;
        Assert.True(Guid.TryParse(key, out _), "The media workspace must identify the uploaded item.");
        var resizeUrl = await page.EvaluateAsync<string>("""
            async id => {
              const { ImagingService } = await import('@umbraco-cms/backoffice/external/backend-api');
              const result = await ImagingService.getImagingResizeUrls({ query: { id: [id], width: 100, height: 0 } });
              if (result.error) throw new Error(JSON.stringify(result.error));
              return result.data[0].urlInfos[0].url;
            }
            """, key).WaitAsync(Patience, ct);
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
        await CheckRenderedMediaAsync(page, site, key);
    }

    /// <summary>
    /// Waits for an email containing the expected link, checks its public host and prefixed login path, and opens it.
    /// Both reset and invitation links must validate through the API before the new-password form appears.
    /// </summary>
    private async Task FollowEmailLinkAsync(BrowserSession browser, TestSite site, Regex pattern, CancellationToken ct)
    {
        Uri? link = null;
        await WaitUntilAsync(async () =>
        {
            foreach (var file in Directory.EnumerateFiles(site.MailDirectory, "*.eml"))
            {
                try
                {
                    using var message = await MimeMessage.LoadAsync(file, ct);
                    var match = pattern.Match(message.HtmlBody ?? message.TextBody ?? "");
                    if (!match.Success) continue;
                    link = new Uri(WebUtility.HtmlDecode(match.Value));
                    return true;
                }
                catch (IOException)
                {
                    // Umbraco may still be writing the file.
                }
            }
            return false;
        }, () => $"No email containing {pattern} arrived.", ct);
        Assert.NotNull(link);
        _log.WriteLine($"Email link: {link.GetLeftPart(UriPartial.Path)}");
        Assert.Equal(site.Address.Authority, link.Authority);
        Assert.Equal(site.Url("/umbraco/login").AbsolutePath, link.AbsolutePath);
        var page = await browser.NewPageAsync();
        try
        {
            var response = await page.GotoAsync(link.ToString());
            Assert.True(response?.Ok, $"The email link returned HTTP {response?.Status}");
            await Expect(page.GetByRole(AriaRole.Textbox, new() { Name = "New password", Exact = true })).ToBeVisibleAsync();
        }
        finally { await page.CloseAsync(); }
    }

    /// <summary>
    /// Checks <paramref name="done"/> every 250 ms until it returns true. Throws a <see cref="TimeoutException"/> with the
    /// message from <paramref name="failure"/> when <see cref="Patience"/> runs out first.
    /// </summary>
    private static async Task WaitUntilAsync(Func<Task<bool>> done, Func<string> failure, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!await done())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException(failure());
            await Task.Delay(250, ct);
        }
    }

    /// <summary>
    /// Waits like <see cref="WaitUntilAsync"/>, and meanwhile clicks <paramref name="button"/> whenever it is visible. This
    /// suits a dialog that Umbraco shows in some setups and not in others, and a click that is lost while a view is still
    /// rendering. Unlike a short look for the button, it still clicks a button that appears late.
    /// </summary>
    private static Task WaitClickingAsync(ILocator button, Func<Task<bool>> done, Func<string> failure, CancellationToken ct)
        => WaitUntilAsync(async () =>
        {
            if (await done())
                return true;
            if (await button.IsVisibleAsync())
            {
                try
                {
                    await button.ClickAsync(new() { Timeout = 5_000 });
                }
                catch (TimeoutException)
                {
                    // The button went away meanwhile, so check again.
                }
            }
            return false;
        }, failure, ct);

    /// <summary>Matches the password-reset link in Umbraco's email.</summary>
    [GeneratedRegex("""https?://[^\s"'<>]+flow=reset-password[^\s"'<>]*""")]
    private static partial Regex ResetLink();

    /// <summary>Matches the invitation link in Umbraco's email.</summary>
    [GeneratedRegex("""https?://[^\s"'<>]+flow=invite[^\s"'<>]*""")]
    private static partial Regex InvitationLink();

    /// <summary>Matches the name Umbraco gives the uploaded test image, with or without the file's own name.</summary>
    [GeneratedRegex("^(E2E Upload|e2e-upload\\.png)$")]
    private static partial Regex UploadedMediaName();

    /// <summary>Matches the publish dialog's confirm button by name, in any case.</summary>
    [GeneratedRegex("publish", RegexOptions.IgnoreCase)]
    private static partial Regex PublishButton();

    /// <summary>One Chromium browser context for a site, which records every error the browser reports.</summary>
    private sealed class BrowserSession : IAsyncDisposable
    {
        private readonly IPlaywright _playwright;
        private readonly IBrowser _browser;
        private readonly TestSite _site;
        private readonly ITestOutputHelper _output;
        private readonly string _artifacts;
        private readonly ConcurrentDictionary<string, bool> _externalHosts = new(StringComparer.OrdinalIgnoreCase);
        private string _step = "start";

        /// <summary>
        /// Holds the Playwright objects for <paramref name="site"/> and writes diagnostics to <paramref name="output"/> and
        /// the <paramref name="artifacts"/> folder.
        /// </summary>
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

        /// <summary>
        /// Launches Chromium with tracing and a page script that records <c>EventSource</c> connections. Every wait in the
        /// context defaults to <see cref="Patience"/>. The context serves fixed offline responses for known external hosts
        /// and records console errors, script errors, failed requests, HTTP errors, requests to the site's host outside the
        /// virtual path and unexpected external requests. Returns the session with one open page.
        /// </summary>
        public static async Task<BrowserSession> StartAsync(TestSite site, ITestOutputHelper output, string artifacts)
        {
            var playwright = await Playwright.CreateAsync();
            IBrowser? browser = null;
            try
            {
                browser = await playwright.Chromium.LaunchAsync();
                var context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = site.Options.Https });
                context.SetDefaultTimeout((float)Patience.TotalMilliseconds);
                context.SetDefaultNavigationTimeout((float)Patience.TotalMilliseconds);
                Assertions.SetDefaultExpectTimeout((float)Patience.TotalMilliseconds);
                var session = new BrowserSession(playwright, browser, context, site, output, artifacts);
                await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });
                await context.AddInitScriptAsync("""
                    window.__testEventSources = [];
                    const NativeEventSource = window.EventSource;
                    window.EventSource = class extends NativeEventSource {
                      constructor(url, options) {
                        super(url, options);
                        this.addEventListener('open', () => window.__testEventSources.push(this.url));
                      }
                    };
                    """);
                await context.RouteAsync("**/*", async route =>
                {
                    var url = new Uri(route.Request.Url);
                    if (url.Authority == site.Address.Authority)
                    {
                        // Check the path here, as the request leaves. A request that the page cancels, such as an image
                        // whose address changes before it loads, gets no response for the other checks to see.
                        if (!session.IsUnderVirtualPath(url))
                            session.Errors.Enqueue($"[{session._step}] Request outside the virtual path: {route.Request.Method} {url}");
                        await route.ContinueAsync();
                    }
                    else if (url.AbsoluteUri == "https://outside.example.test/")
                        await route.FulfillAsync(new() { ContentType = "text/html", Body = $"<a href=\"{site.Url("/umbraco")}\">Backoffice</a>" });
                    else if (url.AbsoluteUri == "https://code.jquery.com/jquery-3.6.0.min.js")
                        await route.FulfillAsync(new()
                        {
                            ContentType = "application/javascript",
                            Path = Path.Combine(AppContext.BaseDirectory, "Browser", "Fixtures", "jquery-3.6.0.min.js"),
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
                // Listen on the context, so every page, popup and new tab is checked.
                context.Console += (_, message) =>
                {
                    if (message.Type == "error"
                        && !((session.IsSigningOutHub(message.Location) || session.IsLoadingDuringSignOut(message.Location))
                            && message.Text.StartsWith("Failed to load resource: the server responded with a status of 401", StringComparison.Ordinal)))
                        session.Errors.Enqueue($"[{session._step}] Console: {message.Text} ({message.Location})");
                };
                context.WebError += (_, error) =>
                {
                    if (IsKnownUmbracoError(error.Error))
                    {
                        output.WriteLine($"Ignored a known Umbraco {UmbracoUnderTest.Release} error: {error.Error.Split('\n')[0]}");
                        return;
                    }
                    session.Errors.Enqueue($"[{session._step}] JavaScript: {error.Error}");
                };
                context.RequestFailed += (_, request) =>
                {
                    // Navigating away cancels requests that are still running, which is expected.
                    if (request.Failure?.Contains("ERR_ABORTED", StringComparison.Ordinal) != true)
                        session.Errors.Enqueue($"[{session._step}] Request failed: {request.Method} {request.Url} {request.Failure}");
                };
                context.Response += (_, response) =>
                {
                    // Umbraco revokes the session before SignalR sends its SSE close message, so that POST gets a 401, on a
                    // root site too, as does a management API request that is still loading. Allow only those 401s.
                    if (response.Status == 401 && response.Request.Method == "POST" && session.IsSigningOutHub(response.Url))
                    {
                        output.WriteLine("Expected 401 for the server-event hub's SSE close message after sign-out.");
                        return;
                    }
                    if (response.Status == 401 && response.Request.Method == "GET" && session.IsLoadingDuringSignOut(response.Url))
                    {
                        output.WriteLine($"Expected 401 for a request that was still loading when the session ended: {response.Url}");
                        return;
                    }
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
            catch
            {
                // The caller cannot dispose a session it never received. Disposing the browser also closes any context
                // or page created before the failure.
                try
                {
                    if (browser is not null) await browser.DisposeAsync();
                }
                finally
                {
                    playwright.Dispose();
                }
                throw;
            }
        }

        /// <summary>
        /// Returns whether <paramref name="error"/>, an uncaught exception with its stack, is a known bug of the Umbraco
        /// release under test that has nothing to do with the virtual path. The tags input of Umbraco 17.5 and 18.0 sets
        /// the width of an element that is not always rendered, which throws during a re-render; 17.6 and 18.1 check for
        /// the element first. In every supported release, a document workspace controller can read a variant's language
        /// before the languages have loaded, and the sign-in page stops waiting for its translations after two seconds and
        /// rejects with "Localization not available", which a busy machine can exceed. A translation request that fails
        /// outright still fails the case as an HTTP error.
        /// </summary>
        private static bool IsKnownUmbracoError(string error)
        {
            var frame = error.Split('\n').ElementAtOrDefault(1) ?? "";
            return (UmbracoUnderTest.Release is { Major: 17, Minor: 5 } or { Major: 18, Minor: 0 }
                    && error.StartsWith("TypeError: Cannot read properties of null (reading 'style')", StringComparison.Ordinal)
                    && frame.Contains("/packages/tags/tags-input.element-", StringComparison.Ordinal))
                || (error.StartsWith("TypeError: Cannot read properties of undefined (reading 'isDefault')", StringComparison.Ordinal)
                    && frame.Contains("/packages/documents/document-allow-edit-invariant-from-non-default-controller-base-", StringComparison.Ordinal))
                // The page rejects with a bare string, which has no stack.
                || error.Trim().TrimStart(':').Trim() == "Localization not available";
        }

        /// <summary>Opens another page in the session's context, where the same error recording applies.</summary>
        public Task<IPage> NewPageAsync() => Context.NewPageAsync();

        /// <summary>
        /// Returns whether <paramref name="url"/> is the server event hub while signing out on a host without WebSockets,
        /// where the SSE close message gets the expected 401.
        /// </summary>
        private bool IsSigningOutHub(string url)
            => _step == "Sign out" && !_site.Options.WebSockets
                && Uri.TryCreate(url, UriKind.Absolute, out var address)
                && address.GetLeftPart(UriPartial.Path) == _site.Url("/umbraco/serverEventHub").ToString();

        /// <summary>
        /// Returns whether <paramref name="url"/> is a management API address under the virtual path while signing out.
        /// The backoffice can still be loading, such as the content tree after a cross-site restore, when Umbraco ends
        /// the session, so such a GET gets a 401, on a root site too. A request outside the virtual path would get a 404
        /// from the host instead.
        /// </summary>
        private bool IsLoadingDuringSignOut(string url)
            => _step == "Sign out" && url.StartsWith(_site.Url("/umbraco/management/api/").ToString(), StringComparison.Ordinal);

        /// <summary>
        /// Returns whether <paramref name="url"/>, an address on the site's host, is at or under the virtual path. Case does
        /// not matter, as on IIS.
        /// </summary>
        private bool IsUnderVirtualPath(Uri url)
            => PathString.FromUriComponent(url).StartsWithSegments(_site.Options.Prefix, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Runs <paramref name="step"/> under <paramref name="name"/>, which labels the errors recorded meanwhile. When the
        /// step fails, saves a memory dump of this process, which hosts the site, and captures every open page before
        /// rethrowing.
        /// </summary>
        public Task StepAsync(string name, Func<Task> step) => RunStepAsync(name, step, dump: true);

        /// <summary>
        /// Checks, as a final step, that the browser reported no errors. A failure here saves no memory dump, because the
        /// errors happened during earlier steps.
        /// </summary>
        public Task AssertNoErrorsAsync() => RunStepAsync("Browser errors", () =>
        {
            Assert.Empty(Errors);
            return Task.CompletedTask;
        }, dump: false);

        /// <summary>
        /// Runs <paramref name="step"/> under <paramref name="name"/>. When the step fails, saves a memory dump if
        /// <paramref name="dump"/> is true, then captures every open page before rethrowing.
        /// </summary>
        private async Task RunStepAsync(string name, Func<Task> step, bool dump)
        {
            _step = name;
            _output.WriteLine($"Step: {name}");
            try
            {
                await step();
            }
            catch
            {
                var file = string.Concat(name.Split(Path.GetInvalidFileNameChars())).Replace(' ', '-');
                // Dump first, while whatever the site was stuck on is most likely still running.
                if (dump)
                    await DumpAsync(file);
                await CaptureAsync(file);
                throw;
            }
        }

        /// <summary>
        /// Saves a memory dump of this process, which hosts the site, as <paramref name="file"/>.dmp in the artifacts
        /// folder. When a step fails because the site stopped answering, the dump shows what the site was waiting on, for
        /// example through <c>dotnet-dump analyze</c> and its <c>dumpasync</c> command.
        /// </summary>
        private async Task DumpAsync(string file)
        {
            var path = Path.Combine(_artifacts, file + ".dmp");
            try
            {
                await new DiagnosticsClient(Environment.ProcessId).WriteDumpAsync(DumpType.WithHeap, path, logDumpGeneration: false, CancellationToken.None);
                _output.WriteLine($"Memory dump: {path}");
            }
            catch (Exception exception)
            {
                // Dumping must never hide the step's own failure.
                _output.WriteLine($"Could not save a memory dump: {exception.Message}");
            }
        }

        /// <summary>
        /// Saves a full-page screenshot of every open page, named after <paramref name="file"/>, and writes each page's URL
        /// and accessibility snapshot to the test output.
        /// </summary>
        private async Task CaptureAsync(string file)
        {
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

        /// <summary>
        /// Writes the recorded errors and the external hosts contacted to the test output, saves the trace, and closes the
        /// browser.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            foreach (var error in Errors)
                _output.WriteLine(error);
            if (!_externalHosts.IsEmpty)
                _output.WriteLine($"External hosts contacted: {string.Join(", ", _externalHosts.Keys.Order())}");
            try
            {
                // Save the trace for passing runs too, because the server-log check runs after the browser closes.
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

    /// <summary>
    /// Creates an empty <c>test-output.log</c> in a new run folder for the case <paramref name="name"/> under
    /// <c>e2e-artifacts</c>, and returns an output that writes to it and to <paramref name="inner"/>.
    /// </summary>
    public static TeeOutput Create(ITestOutputHelper inner, string name)
    {
        var run = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
        var directory = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "e2e-artifacts", name, run)).FullName;
        var file = Path.Combine(directory, "test-output.log");
        File.WriteAllText(file, "");
        return new TeeOutput(inner, file);
    }

    /// <summary>Writes <paramref name="message"/> to xUnit and appends it to the log file.</summary>
    public void Write(string message)
    {
        inner.Write(message);
        File.AppendAllText(file, message);
    }

    /// <summary>Formats <paramref name="format"/> with <paramref name="args"/> and writes the result to both outputs.</summary>
    public void Write(string format, params object[] args) => Write(string.Format(format, args));

    /// <summary>Writes <paramref name="message"/> as a line to xUnit and to the log file.</summary>
    public void WriteLine(string message)
    {
        inner.WriteLine(message);
        File.AppendAllText(file, message + Environment.NewLine);
    }

    /// <summary>
    /// Formats <paramref name="format"/> with <paramref name="args"/> and writes the result as a line to both outputs.
    /// </summary>
    public void WriteLine(string format, params object[] args) => WriteLine(string.Format(format, args));
}
