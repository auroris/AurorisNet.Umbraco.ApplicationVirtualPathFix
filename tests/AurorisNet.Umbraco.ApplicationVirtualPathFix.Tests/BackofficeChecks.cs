using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Playwright;
using Umbraco.Cms.Web.Common.Hosting;
using Umbraco.Cms.Core.Services;
using static Microsoft.Playwright.Assertions;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests;

public sealed partial class SiteEndToEndTests
{
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

        // Persist the setting through the same authenticated API as the data-type editor.
        Directory.CreateDirectory(Path.Combine(site.Root, "wwwroot", "css"));
        await File.WriteAllTextAsync(Path.Combine(site.Root, "wwwroot", "css", "test-grid.css"),
            ".umb-block-grid { --test-layout-stylesheet: loaded; }", TestContext.Current.CancellationToken);
        var original = await page.EvaluateAsync<JsonElement>("""
            async id => {
              const { DataTypeService } = await import('@umbraco-cms/backoffice/external/backend-api');
              const result = await DataTypeService.getDataTypeById({ path: { id } });
              if (result.error) throw new Error(JSON.stringify(result.error));
              const data = result.data;
              const updated = { ...data, values: data.values.filter(value => value.alias !== 'layoutStylesheet') };
              updated.values.push({ alias: 'layoutStylesheet', value: '/css/test-grid.css' });
              const saved = await DataTypeService.putDataTypeById({ path: { id }, body: updated });
              if (saved.error) throw new Error(JSON.stringify(saved.error));
              return data;
            }
            """, dataType.Key.ToString());
        try
        {
            var custom = await page.RunAndWaitForResponseAsync(() => page.ReloadAsync(),
                response => new Uri(response.Url).AbsolutePath.EndsWith("/css/test-grid.css"));
            Assert.True(custom.Ok);
            Assert.Equal(site.Url("/css/test-grid.css").AbsolutePath, new Uri(custom.Url).AbsolutePath);

            await page.GotoAsync(site.Url($"/umbraco/section/settings/workspace/data-type/edit/{dataType.Key}").ToString());
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
                """, new { id = dataType.Key.ToString(), json = original.GetRawText() });
        }
    }

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

    private static async Task CheckPatchedAssetsAsync(IPage page, TestSite site, CancellationToken ct)
    {
        await using var assets = new AssetPatchTests.Assets();
        var root = site.Services.GetRequiredService<IBackOfficePathGenerator>().BackOfficeAssetsPath;
        var paths = assets.ModulePaths().Select(path => root + path[BackOfficeAssetPatches.AssetsRoot.Length..]).ToArray();
        Assert.Equal(BackOfficeAssetPatches.Modules.Count(), paths.Length);
        await page.EvaluateAsync("""
            async paths => {
              for (const path of paths.filter(path => path.endsWith('.js'))) {
                try { await import(path); }
                catch (error) { throw new Error(`Import failed: ${path}: ${error.stack ?? error}`); }
              }
            }
            """, paths);

        using var http = site.CreateHttpClient();
        foreach (var path in new[] { site.Options.Prefix + "/umbraco/login/login.js", root + "/packages/core/manifests.js", root + "/css/umb-css.css" })
        {
            var url = new Uri(site.Address, path);
            using var original = await http.GetAsync(url, ct);
            Assert.Equal(HttpStatusCode.OK, original.StatusCode);
            Assert.NotEmpty(await original.Content.ReadAsByteArrayAsync(ct));
            Assert.NotNull(original.Headers.ETag);
            if (!site.Options.Debug && path.Contains("/backoffice/", StringComparison.Ordinal))
                Assert.True(original.Headers.CacheControl?.MaxAge > TimeSpan.Zero, "Non-debug backoffice assets should be cacheable.");
            using var conditional = new HttpRequestMessage(HttpMethod.Get, url);
            conditional.Headers.IfNoneMatch.Add(original.Headers.ETag);
            using var cached = await http.SendAsync(conditional, ct);
            Assert.Equal(HttpStatusCode.NotModified, cached.StatusCode);
            Assert.Empty(await cached.Content.ReadAsByteArrayAsync(ct));
        }

        // The login view's existing asp-append-version must hash the file-provider output.
        var loginHtml = await http.GetStringAsync(site.Url("/umbraco/login"), ct);
        var script = Regex.Match(loginHtml, """<script\b[^>]*\bsrc="([^"]*/umbraco/login/login\.js[^"]*)"[^>]*>""");
        Assert.True(script.Success, "The login page must include its versioned script.");
        var scriptUrl = new Uri(site.Address, WebUtility.HtmlDecode(script.Groups[1].Value));
        var query = QueryHelpers.ParseQuery(scriptUrl.Query);
        Assert.False(query.ContainsKey("avpfix"));
        var scriptBytes = await http.GetByteArrayAsync(scriptUrl, ct);
        Assert.Equal(WebEncoders.Base64UrlEncode(SHA256.HashData(scriptBytes)), query["v"].ToString());
    }

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

    private static async Task AssertServerSentEventsAsync(IPage page, string hub)
    {
        await page.WaitForFunctionAsync("""
            hub => window.__testEventSources?.some(url => new URL(url).pathname.endsWith('/umbraco/' + hub))
            """, hub, new() { Timeout = 30_000 });
    }

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
            // This fixture deliberately gives the new page the same slug as the IIS prefix.
            // Both segments must survive; a textual duplicate check would reject its valid URL.
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
        await Expect(picker.Locator("uui-card-media")).ToHaveAttributeAsync("name", "E2E Upload");
        await page.Locator("[data-mark='workspace-action:Umb.WorkspaceAction.Document.SaveAndPublish']").ClickAsync();
        await Expect(page.Locator("umb-document-publish-modal")).Not.ToBeAttachedAsync();
        // Readback proves the picker saved the uploaded media, not the previously selected image.
        using var http = site.CreateHttpClient();
        string html = "";
        for (var attempt = 0; attempt < 30; attempt++)
        {
            html = await http.GetStringAsync(site.Url("/"), TestContext.Current.CancellationToken);
            if (html.Contains("e2e-upload", StringComparison.OrdinalIgnoreCase)) break;
            await Task.Delay(250, TestContext.Current.CancellationToken);
        }
        Assert.Contains("e2e-upload", html, StringComparison.OrdinalIgnoreCase);
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

    private static void AssertNoDoubledPrefix(string path, TestSite site)
    {
        if (site.Options.Prefix.Length > 0)
            Assert.False(path.StartsWith(site.Options.Prefix + site.Options.Prefix + "/", StringComparison.OrdinalIgnoreCase), path);
    }
}
