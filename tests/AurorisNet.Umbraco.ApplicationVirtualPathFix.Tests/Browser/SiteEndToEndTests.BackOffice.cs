using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using AurorisNet.Umbraco.ApplicationVirtualPathFix.Assets;
using AurorisNet.Umbraco.ApplicationVirtualPathFix.Diagnostics;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Playwright;
using Umbraco.Cms.Core.HealthChecks;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Web.Common.Hosting;
using static Microsoft.Playwright.Assertions;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests.Browser;

public sealed partial class SiteEndToEndTests
{
    /// <summary>
    /// Checks that the block grid loads its default and a custom layout stylesheet under the virtual path, and that block
    /// thumbnails load in the block catalogue and on the data type's block type cards. Saves the custom stylesheet and
    /// thumbnails through the management API and restores the data type afterwards.
    /// </summary>
    private static async Task CheckBlockGridAsync(IPage page, TestSite site)
    {
        var home = site.Services.GetRequiredService<IContentService>().GetRootContent().Single(content => content.ContentType.Alias == "home");
        var types = await site.Services.GetRequiredService<IDataTypeService>().GetByEditorAliasAsync("Umbraco.BlockGrid");
        var dataType = types.Single(type => type.Id == home.Properties.Single(property => property.Alias == "bodyText").PropertyType.DataTypeId);
        var workspace = site.Url($"/umbraco/section/content/workspace/document/edit/{home.Key}").ToString();
        var stylesheet = await page.RunAndWaitForResponseAsync(() => page.GotoAsync(workspace),
            response => new Uri(response.Url).AbsolutePath.EndsWith("/css/umbraco-blockgridlayout.css"));
        Assert.True(stylesheet.Ok);
        Assert.StartsWith(site.Url("/umbraco/backoffice/").AbsolutePath, new Uri(stylesheet.Url).AbsolutePath);
        await Expect(page.Locator("umb-property-editor-ui-block-grid")).ToBeVisibleAsync();

        // Save the settings through the authenticated API that the data type editor uses. The thumbnails use the format
        // the static file picker saves, which includes the web root folder.
        Directory.CreateDirectory(Path.Combine(site.Root, "wwwroot", "css"));
        await File.WriteAllTextAsync(Path.Combine(site.Root, "wwwroot", "css", "test-grid.css"),
            ".umb-block-grid { --test-layout-stylesheet: loaded; }", TestContext.Current.CancellationToken);
        Directory.CreateDirectory(Path.Combine(site.Root, "wwwroot", "images"));
        await File.WriteAllTextAsync(Path.Combine(site.Root, "wwwroot", "images", "test-thumbnail.svg"),
            """<svg xmlns="http://www.w3.org/2000/svg" width="40" height="30"><rect width="40" height="30" fill="#3544b1"/></svg>""",
            TestContext.Current.CancellationToken);
        var original = await page.EvaluateAsync<JsonElement>("""
            async id => {
              const { DataTypeService } = await import('@umbraco-cms/backoffice/external/backend-api');
              const result = await DataTypeService.getDataTypeById({ path: { id } });
              if (result.error) throw new Error(JSON.stringify(result.error));
              const data = result.data;
              const blocks = data.values.find(value => value.alias === 'blocks');
              if (!blocks?.value?.length) throw new Error('The block grid has no block types.');
              const updated = { ...data, values: data.values.filter(value => value.alias !== 'layoutStylesheet' && value.alias !== 'blocks') };
              updated.values.push({ alias: 'layoutStylesheet', value: '/css/test-grid.css' });
              updated.values.push({ ...blocks, value: blocks.value.map(block => ({ ...block, thumbnail: '/wwwroot/images/test-thumbnail.svg' })) });
              const saved = await DataTypeService.putDataTypeById({ path: { id }, body: updated });
              if (saved.error) throw new Error(JSON.stringify(saved.error));
              return data;
            }
            """, dataType.Key.ToString()).WaitAsync(Patience);
        var thumbnail = site.Url("/images/test-thumbnail.svg").ToString();
        try
        {
            var custom = await page.RunAndWaitForResponseAsync(() => page.ReloadAsync(),
                response => new Uri(response.Url).AbsolutePath.EndsWith("/css/test-grid.css"));
            Assert.True(custom.Ok);
            Assert.Equal(site.Url("/css/test-grid.css").AbsolutePath, new Uri(custom.Url).AbsolutePath);

            // The block catalogue shows each block type's thumbnail.
            await page.Locator("umb-block-grid-entries #createButton uui-button[look='placeholder']").First.ClickAsync();
            await AssertImageLoadsAsync(page.Locator("umb-block-catalogue-modal uui-card-block-type img").First, thumbnail);

            // So do the block type cards in the data type's settings.
            await page.GotoAsync(site.Url($"/umbraco/section/settings/workspace/data-type/edit/{dataType.Key}").ToString());
            await AssertImageLoadsAsync(page.Locator("umb-block-type-card img").First, thumbnail);
            var link = page.GetByRole(AriaRole.Link, new() { Name = "Link to default layout stylesheet", Exact = true });
            await Expect(link).ToBeVisibleAsync();
            var href = await link.GetAttributeAsync("href");
            Assert.StartsWith(site.Url("/umbraco/backoffice/").AbsolutePath, new Uri(new Uri(page.Url), href!).AbsolutePath);
        }
        finally
        {
            await page.EvaluateAsync("""
                async ({ id, json }) => {
                  const { DataTypeService } = await import('@umbraco-cms/backoffice/external/backend-api');
                  const result = await DataTypeService.putDataTypeById({ path: { id }, body: JSON.parse(json) });
                  if (result.error) throw new Error(JSON.stringify(result.error));
                }
                """, new { id = dataType.Key.ToString(), json = original.GetRawText() }).WaitAsync(Patience);
        }
    }

    /// <summary>
    /// Checks that the health check is registered once and that, run through the management API as the Health Check
    /// dashboard runs it, it returns four successful results, one of them naming the site's prefix.
    /// </summary>
    private static async Task CheckHealthAsync(IPage page, TestSite site)
    {
        // The check appears once, whether or not Umbraco's own type scan also finds it.
        Assert.Single(site.Services.GetRequiredService<HealthCheckCollection>(), check => check is VirtualPathHealthCheck);
        // Run the Configuration group as the backoffice's Health Check dashboard does.
        var results = await page.EvaluateAsync<JsonElement>("""
            async id => {
              const { HealthCheckService } = await import('@umbraco-cms/backoffice/external/backend-api');
              const { data, error } = await HealthCheckService.postHealthCheckGroupByNameCheck({ path: { name: 'Configuration' } });
              if (error) throw new Error(JSON.stringify(error));
              return data.checks.find(check => check.id.toLowerCase() === id)?.results ?? null;
            }
            """, VirtualPathHealthCheck.CheckId).WaitAsync(Patience);
        Assert.True(results.ValueKind == JsonValueKind.Array, "The Configuration group must include the application virtual path check.");
        var statuses = results.EnumerateArray()
            .Select(result => (Type: result.GetProperty("resultType").GetString(), Message: result.GetProperty("message").GetString()!)).ToArray();
        Assert.Equal(4, statuses.Length);
        Assert.All(statuses, status => Assert.True(status.Type == "Success", $"{status.Type}: {status.Message}"));
        Assert.Contains(statuses, status => status.Message.Contains($"runs under {site.Options.Prefix},", StringComparison.Ordinal));
    }

    /// <summary>
    /// Waits until <paramref name="image"/> has the source <paramref name="url"/>, then checks that the image decodes.
    /// </summary>
    private static async Task AssertImageLoadsAsync(ILocator image, string url)
    {
        await Expect(image).ToHaveAttributeAsync("src", url);
        // decode() rejects when the image fails to load.
        await image.EvaluateAsync("image => image.decode()");
    }

    /// <summary>
    /// Checks that the backoffice's server context reports the site's public origin and prefix as its server URL. On a host
    /// without WebSockets, also checks that the server event hub connects with Server-Sent Events.
    /// </summary>
    private static async Task CheckServerContextAsync(IPage page, TestSite site)
    {
        var serverUrl = await page.EvaluateAsync<string>("""
            async () => {
              const { UMB_SERVER_CONTEXT } = await import('@umbraco-cms/backoffice/server');
              return (await document.querySelector('umb-app').getContext(UMB_SERVER_CONTEXT)).getServerUrl();
            }
            """);
        Assert.Equal(site.Address.GetLeftPart(UriPartial.Authority) + site.Options.Prefix, serverUrl);
        if (!site.Options.WebSockets)
            await AssertServerSentEventsAsync(page, "serverEventHub");
    }

    /// <summary>
    /// Checks that every patched backoffice module imports in the browser from the cache-busted assets path, that three
    /// patched files answer conditional requests with 304, and that the login page's script version is the hash of the
    /// patched script. Outside debug mode, also checks that backoffice assets are cacheable on the releases where Umbraco
    /// makes them so.
    /// </summary>
    private static async Task CheckPatchedAssetsAsync(IPage page, TestSite site, CancellationToken ct)
    {
        var files = site.Services.GetRequiredService<IWebHostEnvironment>().WebRootFileProvider;
        var root = site.Services.GetRequiredService<IBackOfficePathGenerator>().BackOfficeAssetsPath;
        var backOffice = AssetPatcher.AssetsRoot + "/";
        var patches = UmbracoUnderTest.Patches.Where(patch => patch.Path.StartsWith(backOffice, StringComparison.Ordinal)).ToArray();
        var paths = patches.Select(patch => Assert.Single(AssetPatcher.Locate(files, patch)).Path).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => root + path[AssetPatcher.AssetsRoot.Length..]).ToArray();
        Assert.Equal(patches.Select(patch => patch.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count(), paths.Length);
        await page.EvaluateAsync("""
            async paths => {
              for (const path of paths.filter(path => path.endsWith('.js'))) {
                try { await import(path); }
                catch (error) { throw new Error(`Import failed: ${path}: ${error.stack ?? error}`); }
              }
            }
            """, paths);

        using var http = site.CreateHttpClient();
        // Umbraco 17.6 and 18.1 started serving backoffice assets with a max-age outside debug mode.
        var cacheable = !site.Options.Debug && UmbracoUnderTest.Release is not ({ Major: 17, Minor: 5 } or { Major: 18, Minor: 0 });
        foreach (var path in new[] { site.Options.Prefix + "/umbraco/login/login.js", root + "/packages/core/manifests.js", root + "/css/umb-css.css" })
        {
            var url = new Uri(site.Address, path);
            using var original = await http.GetAsync(url, ct);
            Assert.Equal(HttpStatusCode.OK, original.StatusCode);
            Assert.NotEmpty(await original.Content.ReadAsByteArrayAsync(ct));
            Assert.NotNull(original.Headers.ETag);
            if (cacheable && path.Contains("/backoffice/", StringComparison.Ordinal))
                Assert.True(original.Headers.CacheControl?.MaxAge > TimeSpan.Zero, "Non-debug backoffice assets should be cacheable.");
            using var conditional = new HttpRequestMessage(HttpMethod.Get, url);
            conditional.Headers.IfNoneMatch.Add(original.Headers.ETag);
            using var cached = await http.SendAsync(conditional, ct);
            Assert.Equal(HttpStatusCode.NotModified, cached.StatusCode);
            Assert.Empty(await cached.Content.ReadAsByteArrayAsync(ct));
        }

        // The login view's asp-append-version must hash the patched file that the file provider serves.
        var loginHtml = await http.GetStringAsync(site.Url("/umbraco/login"), ct);
        var script = Regex.Match(loginHtml, """<script\b[^>]*\bsrc="([^"]*/umbraco/login/login\.js[^"]*)"[^>]*>""");
        Assert.True(script.Success, "The login page must include its versioned script.");
        var scriptUrl = new Uri(site.Address, WebUtility.HtmlDecode(script.Groups[1].Value));
        var query = QueryHelpers.ParseQuery(scriptUrl.Query);
        Assert.False(query.ContainsKey("avpfix"));
        var scriptBytes = await http.GetByteArrayAsync(scriptUrl, ct);
        Assert.Equal(WebEncoders.Base64UrlEncode(SHA256.HashData(scriptBytes)), query["v"].ToString());
    }

    /// <summary>
    /// Signs out through Umbraco's auth context and checks that the logout redirect stays under the virtual path. Then
    /// checks that the backoffice shows the login form, also after a reload, and that the refresh cookie is gone.
    /// </summary>
    private static async Task SignOutAsync(IPage page, TestSite site)
    {
        var logout = page.WaitForResponseAsync(response => response.Request.IsNavigationRequest
            && new Uri(response.Url).AbsolutePath == site.Url("/umbraco/logout").AbsolutePath);
        await page.EvaluateAsync("""
            async () => {
              const { UMB_AUTH_CONTEXT } = await import('@umbraco-cms/backoffice/auth');
              const auth = await document.querySelector('umb-app').getContext(UMB_AUTH_CONTEXT);
              await auth.signOut();
            }
            """);
        Assert.True((await logout).Ok);
        await page.WaitForURLAsync(site.Url("/umbraco/logout").ToString());
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Sign in with Umbraco", Exact = true })).ToBeVisibleAsync();
        var navigation = await page.GotoAsync(site.Url("/umbraco").ToString());
        Assert.True(navigation?.Ok);
        await Expect(page.Locator("#username-input")).ToBeVisibleAsync();
        await page.ReloadAsync();
        await Expect(page.Locator("#username-input")).ToBeVisibleAsync();
        Assert.DoesNotContain(await page.Context.CookiesAsync(), cookie => cookie.Name.Contains("umbRefreshToken", StringComparison.Ordinal));
    }

    /// <summary>
    /// Waits until <paramref name="page"/> has an open <c>EventSource</c> connection to the SignalR hub
    /// <paramref name="hub"/>, as recorded by the browser session's page script.
    /// </summary>
    private static async Task AssertServerSentEventsAsync(IPage page, string hub)
    {
        await page.WaitForFunctionAsync("""
            hub => window.__testEventSources?.some(url => new URL(url).pathname.endsWith('/umbraco/' + hub))
            """, hub);
    }

    /// <summary>
    /// Checks that the Delivery API returns content routes and media URLs under the virtual path, with the prefix once.
    /// </summary>
    private static async Task CheckDeliveryApiAsync(TestSite site, CancellationToken ct)
    {
        using var http = site.CreateHttpClient();
        using var response = await http.GetAsync(site.Url("/umbraco/delivery/api/v2/content?take=100"), ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var items = json.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.NotEmpty(items);
        foreach (var item in items)
        {
            var path = item.GetProperty("route").GetProperty("path").GetString()!;
            Assert.StartsWith(site.Url("/").AbsolutePath, path, StringComparison.OrdinalIgnoreCase);
            // In the IIS case the fixture gives a new page the same slug as the prefix, so its valid URL repeats the
            // prefix. Compare that URL exactly, because the doubled-prefix check would reject it.
            if (site.Options.ExistingPathBase && item.GetProperty("name").GetString() == site.Options.Prefix.Trim('/'))
                Assert.Equal(site.Url(site.Options.Prefix.ToLowerInvariant() + "/").AbsolutePath, path);
            else
                AssertNoDoubledPrefix(path, site);
        }
        var media = FindMediaUrls(json.RootElement).ToArray();
        Assert.NotEmpty(media);
        foreach (var url in media)
        {
            var uri = new Uri(site.Address, url);
            Assert.StartsWith(site.Url("/media/").AbsolutePath, uri.AbsolutePath, StringComparison.OrdinalIgnoreCase);
            AssertNoDoubledPrefix(uri.AbsolutePath, site);
        }
    }

    /// <summary>
    /// Picks the uploaded media <paramref name="mediaKey"/> as the home page's hero image and publishes. Then checks that
    /// the front end renders its original and signed crop URLs under the virtual path, and that both images load, the
    /// crop at 100 by 60 pixels.
    /// </summary>
    private static async Task CheckRenderedMediaAsync(IPage page, TestSite site, string mediaKey)
    {
        var home = site.Services.GetRequiredService<IContentService>().GetRootContent().Single(content => content.ContentType.Alias == "home");
        await page.GotoAsync(site.Url($"/umbraco/section/content/workspace/document/edit/{home.Key}").ToString());
        var picker = page.Locator("umb-input-rich-media").Filter(new() { Visible = true }).First;
        await picker.GetByRole(AriaRole.Button, new() { Name = "Remove", Exact = true }).ClickAsync();
        await page.Locator("umb-confirm-modal").GetByRole(AriaRole.Button, new() { Name = "Remove", Exact = true }).ClickAsync();
        await picker.GetByRole(AriaRole.Button, new() { Name = "Choose", Exact = true }).ClickAsync();
        var modal = page.Locator("umb-media-picker-modal");
        await Expect(modal).ToBeVisibleAsync();
        await modal.Locator($"uui-card-media[data-mark='media:{mediaKey}']").ClickAsync();
        await modal.GetByRole(AriaRole.Button, new() { Name = "Choose", Exact = true }).ClickAsync();
        await Expect(modal).Not.ToBeAttachedAsync();
        await Expect(picker.Locator("uui-card-media")).ToHaveAttributeAsync("name", UploadedMediaName());
        await page.Locator("[data-mark='workspace-action:Umb.WorkspaceAction.Document.SaveAndPublish']").ClickAsync();
        await Expect(page.Locator("umb-document-publish-modal")).Not.ToBeAttachedAsync();
        // Reading the page and the saved value back confirms that the picker saved the uploaded media, not the previous
        // image.
        using var http = site.CreateHttpClient();
        var ct = TestContext.Current.CancellationToken;
        await WaitUntilAsync(async () => (await http.GetStringAsync(site.Url("/"), ct)).Contains("e2e-upload", StringComparison.OrdinalIgnoreCase),
            () => "The home page does not show the picked image.", ct);
        Assert.Contains(mediaKey, site.Services.GetRequiredService<IContentService>().GetById(home.Key)!
            .GetValue<string>("HeroBackgroundImage"), StringComparison.OrdinalIgnoreCase);
        var front = await page.Context.NewPageAsync();
        try
        {
            await front.GotoAsync(site.Url("/").ToString());
            foreach (var id in new[] { "test-media-original", "test-media-crop" })
            {
                var image = front.Locator("#" + id);
                await Expect(image).ToBeVisibleAsync();
                await Expect(image).ToHaveJSPropertyAsync("complete", true);
                Assert.True(await image.EvaluateAsync<bool>("image => image.naturalWidth > 0"));
                var url = new Uri(await image.EvaluateAsync<string>("image => image.src"));
                Assert.StartsWith(site.Url("/media/").AbsolutePath, url.AbsolutePath, StringComparison.OrdinalIgnoreCase);
                AssertNoDoubledPrefix(url.AbsolutePath, site);
                Assert.Contains("e2e-upload", url.AbsolutePath, StringComparison.OrdinalIgnoreCase);
                if (id == "test-media-crop")
                {
                    Assert.Contains("hmac=", url.Query, StringComparison.Ordinal);
                    Assert.Equal(100, await image.EvaluateAsync<int>("image => image.naturalWidth"));
                    Assert.Equal(60, await image.EvaluateAsync<int>("image => image.naturalHeight"));
                }
            }
        }
        finally { await front.CloseAsync(); }
    }

    /// <summary>
    /// Returns the <c>url</c> of every object in <paramref name="element"/>, at any depth, that also has a
    /// <c>mediaType</c>.
    /// </summary>
    private static IEnumerable<string> FindMediaUrls(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("mediaType", out _) && element.TryGetProperty("url", out var url))
                yield return url.GetString()!;
            foreach (var property in element.EnumerateObject())
                foreach (var found in FindMediaUrls(property.Value)) yield return found;
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray())
                foreach (var found in FindMediaUrls(child)) yield return found;
    }

    /// <summary>Checks that <paramref name="path"/> does not start with the site's prefix twice.</summary>
    private static void AssertNoDoubledPrefix(string path, TestSite site)
    {
        if (site.Options.Prefix.Length > 0)
            Assert.False(path.StartsWith(site.Options.Prefix + site.Options.Prefix + "/", StringComparison.OrdinalIgnoreCase), path);
    }
}
