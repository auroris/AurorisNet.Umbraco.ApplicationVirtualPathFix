using System.Text.RegularExpressions;
using AurorisNet.Umbraco.ApplicationVirtualPathFix.Assets;
using Microsoft.Extensions.FileProviders;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests;

/// <summary>
/// Scans Umbraco's restored assets and sources for URLs that can lose the virtual path and that no patch or review
/// covers.
/// </summary>
public sealed partial class AssetCoverageTests(AssetPatchTests.Assets assets) : IClassFixture<AssetPatchTests.Assets>
{
    private static readonly PatchContext Context = new("/portal", "/portal/umbraco", "/portal/umbraco/backoffice/hash");

    /// <summary>
    /// Checks that, after patching, no restored JavaScript or CSS file still refers to <c>/umbraco/backoffice/</c>,
    /// <c>/umbraco/website/</c> or <c>/umbraco/login/</c> after a quote or parenthesis, apart from one known diagnostic
    /// constant.
    /// </summary>
    [Fact]
    public async Task Root_absolute_asset_references_are_patched_or_explained()
    {
        // This module's /umbraco/backoffice/ constant classifies stack frames, and nothing fetches it.
        const string diagnosticModule = "/umbraco/backoffice/packages/core/utils/index.js";
        var references = new Regex("""["'`(]/umbraco/(?:backoffice|website|login)/""");
        var unexpected = new List<string>();
        var patches = LocatePatches(assets.Files, UmbracoUnderTest.Patches);
        var examined = 0;
        foreach (var (path, file) in assets.Enumerate("/umbraco"))
        {
            var source = await ReadAsync(file);
            if (!references.IsMatch(source)) continue;
            examined++;
            if (path == diagnosticModule)
            {
                // Remove only the known diagnostic constant, so any other URL added to this file still fails the test.
                source = source.Replace("const U = \"/umbraco/backoffice/\";", "", StringComparison.Ordinal);
            }
            else
                source = AssetPatcher.Patch(patches[path], source, Context, Unapplied);
            foreach (Match match in references.Matches(source))
                unexpected.Add($"{path}:{source.AsSpan(0, match.Index).Count('\n') + 1}: {match.Value}");
        }
        Assert.True(examined > 0, "The restored asset tree must not be empty.");
        Assert.True(unexpected.Count == 0, "Unpatched asset references:\n" + string.Join('\n', unexpected));
    }

    /// <summary>
    /// Checks that a filename match excluded by <see cref="AssetPatch.Contains"/> still fails both coverage scans.
    /// An edit's source location also counts only in the bundle that receives the edit.
    /// </summary>
    [Fact]
    public async Task Coverage_uses_only_the_bundle_selected_by_content()
    {
        const string excluded = "/block/constants-AAAAAAAA.js";
        const string selected = "/block/constants-BBBBBBBB.js";
        const string asset = """const asset = "/umbraco/backoffice/assets/logo.svg";""";
        const string sourceFile = "src/url.ts";
        const string sourceLine = "const url = serverUrl + path;";
        var root = Directory.CreateTempSubdirectory("asset-coverage-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "block"));
            File.WriteAllText(root + excluded, asset + sourceLine);
            File.WriteAllText(root + selected, asset + sourceLine + "/* selected */");
            using var files = new PhysicalFileProvider(root);
            Assert.Equal(2, AssetPatcher.Locate(files, new AssetPatch { Path = "/block/constants-????????.js" }).Length);
            var patch = new AssetPatch
            {
                Path = "/block/constants-????????.js",
                Contains = "/* selected */",
                MovesAssetUrls = true,
                Edits = [new()
                {
                    Find = sourceLine,
                    Replace = "const url = path;",
                    Origin = new() { File = sourceFile, Code = sourceLine },
                }],
            };
            var patches = LocatePatches(files, [patch]);
            Assert.Equal(selected, Assert.Single(patches).Key);
            foreach (var path in new[] { excluded, selected })
            {
                var patched = AssetPatcher.Patch(patches[path], await ReadAsync(files.GetFileInfo(path)), Context, Unapplied);
                Assert.Equal(path == excluded, AssetPatcher.QuotedAssetRoot().IsMatch(patched));
                Assert.Equal(path == selected, IsPatched(sourceFile, asset, [path], patches));
                Assert.Equal(path == selected, IsPatched(sourceFile, sourceLine, [path], patches));
            }
            // A source copied into both bundles must be fixed in both.
            Assert.False(IsPatched(sourceFile, asset, [selected, excluded], patches));
            Assert.False(IsPatched(sourceFile, sourceLine, [selected, excluded], patches));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Checks that every line of Umbraco's sources that builds a URL in a way that can lose the virtual path is patched
    /// or reviewed, and that every review still matches a line.
    /// </summary>
    [Fact]
    public async Task Url_construction_in_Umbraco_sources_is_patched_or_reviewed()
    {
        // The bundles' source maps carry Umbraco's TypeScript, and the apps without maps ship unminified, so URL
        // construction can be found by name. A line is patched when an edit names it, which PatchOriginTests confirms
        // against the source maps, or when its bundle has its asset URLs moved. Every review must still match a line;
        // a review without one means Umbraco changed that code.
        var sources = await ReadSourcesAsync();
        var patches = LocatePatches(assets.Files, UmbracoUnderTest.Patches);
        var reviews = Reviews.Where(review => review.AppliesTo(UmbracoUnderTest.Release)).ToArray();
        var unexpected = new List<string>();
        var found = new HashSet<ReviewedLine>();
        foreach (var (file, (text, bundles)) in sources)
        {
            var lines = text.Split('\n');
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index].Trim();
                if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith("/*", StringComparison.Ordinal)
                    || line.StartsWith('*') || !ConstructsUrl(line, file) || IsPatched(file, line, bundles, patches))
                    continue;
                if (reviews.FirstOrDefault(review => review.File == file && line.Contains(review.Code, StringComparison.Ordinal)) is { } known)
                    found.Add(known);
                else
                    unexpected.Add($"{file}:{index + 1}: {line}");
            }
        }

        Assert.True(sources.Count > 1000, $"Expected Umbraco's sources from the restored source maps, found {sources.Count}.");
        Assert.True(unexpected.Count == 0, "Patch or review these URL constructions:\n" + string.Join('\n', unexpected));
        var missing = reviews.Where(review => !found.Contains(review)).Select(review => $"{review.File}: {review.Code}").ToArray();
        Assert.True(missing.Length == 0, "Umbraco no longer contains these reviewed lines; check whether the review still applies:\n"
            + string.Join('\n', missing));
    }

    /// <summary>
    /// Returns whether <paramref name="line"/> builds a URL in a shape that loses the virtual path when one of its parts
    /// differs from what the code assumes.
    /// </summary>
    private static bool ConstructsUrl(string line, string file)
        // The server URL followed directly by a dynamic part, which may already include the virtual path.
        => ServerUrlJoin().IsMatch(line)
            // new URL(path, base): a root-absolute path replaces the base's path, virtual path included.
            || UrlWithBase().IsMatch(line)
            // The bare origin used to build a URL, rather than compared with one.
            || OriginComparison().Replace(line, "").Contains("location.origin", StringComparison.Ordinal)
            // Root-absolute paths, except the routes of the generated API client, which adds the server URL.
            || (!file.EndsWith(".gen.ts", StringComparison.Ordinal) && RootPath().IsMatch(line));

    /// <summary>
    /// Resolves entries with the same filename and content filters as the production file provider. An ambiguous or
    /// missing file fails the test; production would skip that entry and warn.
    /// </summary>
    private static ILookup<string, AssetPatch> LocatePatches(IFileProvider files, IEnumerable<AssetPatch> patches)
        => patches.Select(patch => (Assert.Single(AssetPatcher.Locate(files, patch)).Path, Patch: patch))
            .ToLookup(entry => entry.Path, entry => entry.Patch, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns whether every bundle containing <paramref name="line"/> receives a patch for it: an edit names the
    /// source line, or it contains a quoted asset URL and that bundle has its asset URLs moved.
    /// </summary>
    private static bool IsPatched(string file, string line, IEnumerable<string> bundles, ILookup<string, AssetPatch> patches)
        => bundles.All(bundle => patches[bundle].Any(patch =>
            patch.Edits.Any(edit => edit.Origin is { } origin && origin.File == file && line.Contains(origin.Code, StringComparison.Ordinal))
            || (patch.MovesAssetUrls && AssetPatcher.QuotedAssetRoot().IsMatch(line))));

    /// <summary>
    /// A line of Umbraco's source that needs no patch: its file, a fragment of its code, and the reason. <c>From</c> and
    /// <c>Before</c> limit the review to the Umbraco releases whose source contains the line, as they limit patches.
    /// </summary>
    private sealed record ReviewedLine(string File, string Code, string Reason, string? From = null, string? Before = null)
    {
        /// <summary>Returns whether the review applies to the Umbraco release <paramref name="umbraco"/>.</summary>
        public bool AppliesTo(Version umbraco)
            => (From is null || umbraco >= Version.Parse(From)) && (Before is null || umbraco < Version.Parse(Before));
    }

    private static readonly ReviewedLine[] Reviews =
    [
        new("/umbraco/backoffice/apps/app/app.element.js", "this.serverUrl = window.location.origin;",
            "The default, which the server-url attribute replaces under a virtual path."),
        new("src/packages/core/auth/auth.context.ts", "new URL(this.#postLogoutRedirectUri, window.location.origin)",
            "The redirect URI is already absolute, built from the configured backoffice path."),
        new("src/packages/core/auth/auth.context.ts", "`${window.location.origin}${this.#backofficePath}",
            "The configured backoffice path includes the virtual path."),
        new("src/packages/core/router/router-slot/util/anchor.ts", "new URL(href, document.baseURI)",
            "Resolves a link's href as the browser does."),
        new("src/packages/core/temporary-file/temporary-file.server.data-source.ts", "url: '/umbraco/management/api/v1/temporary-file'",
            "tryXhrRequest adds the API client's base URL, which is the server URL."),
        new("src/packages/core/utils/deprecation/deprecation-origin.ts", "UMB_CORE_PATH_MARKER = '/umbraco/backoffice/'",
            "Classifies stack frames; it is not fetched.", From: "17.6.0", Before: "18.0.0"),
        new("src/packages/core/utils/deprecation/deprecation-origin.ts", "UMB_CORE_PATH_MARKER = '/umbraco/backoffice/'",
            "Classifies stack frames; it is not fetched.", From: "18.1.0"),
        new("src/packages/core/utils/path/ensure-local-path.function.ts", "new URL(path, window.location.origin)",
            "Checks that a path is on this origin; the path keeps its virtual path."),
        new("src/packages/core/utils/path/ensure-local-path.function.ts", "new URL(window.location.origin)",
            "The fallback for a path on another origin; the backoffice stores only its own routes."),
        new("src/packages/core/utils/path/stored-path.function.ts", "new URL(path, window.location.origin)",
            "Checks that a stored route is on this origin; the route keeps its virtual path."),
        new("src/packages/core/utils/path/umbraco-path.function.ts", "`/umbraco/management/api/${version}${path}`",
            "An exported helper that the backoffice does not call."),
        new("src/packages/core/workspace/utils/check-will-navigate-away.function.ts", "new URL(newUrl, window.location.origin)",
            "Compares two pathnames on this origin, which both include the virtual path.", From: "17.6.0", Before: "18.0.0"),
        new("src/packages/core/workspace/utils/check-will-navigate-away.function.ts", "new URL(newUrl, window.location.origin)",
            "Compares two pathnames on this origin, which both include the virtual path.", From: "18.1.0"),
        new("src/packages/documents/document-redirect-management/dashboard-redirect-management.element.ts",
            "url.slice(window.location.origin.length)", "Shortens a URL for display.", From: "17.8.0", Before: "18.0.0"),
        new("src/packages/documents/document-redirect-management/dashboard-redirect-management.element.ts",
            "url.slice(window.location.origin.length)", "Shortens a URL for display.", From: "18.3.0"),
        new("src/packages/documents/documents/user-permissions/document/repository/document-permission.server.data.ts",
            "fetch(`/umbraco/management/api/v1/document/${id}/permissions`",
            "Registered but not called by the backoffice; without a bearer token it fails at the root too."),
        new("src/packages/documents/documents/workspace/context/document-workspace.context.ts",
            "new URL(previewUrlData.url, window.document.baseURI)",
            "The server's preview URL includes the virtual path; the browser journey opens it.", From: "17.5.0", Before: "17.6.0"),
        new("src/packages/documents/documents/workspace/context/document-workspace.context.ts",
            "new URL(previewUrlData.url, window.document.baseURI)",
            "The server's preview URL includes the virtual path; the browser journey opens it.", From: "18.0.0", Before: "18.1.0"),
        new("src/packages/media/media/url/info-app/media-link.function.ts", "new URL(url, base)", "Reads only the protocol.",
            From: "17.7.2", Before: "18.0.0"),
        new("src/packages/media/media/url/info-app/media-link.function.ts", "new URL(url, base)", "Reads only the protocol.", From: "18.2.2"),
        new("src/packages/packages/package/repository/package.repository.ts", "`${serverUrl}${e.",
            "Package manifests use application-relative paths."),
        new("src/packages/preview/context/preview.context.ts", "new URL(`${url.pathname}?${params.toString()}`, host)",
            "The pathname comes from the patched preview URL, so it keeps the virtual path."),
        new("src/packages/preview/controllers/preview.controller.ts", "new URL(previewUrlData.url, window.document.baseURI)",
            "The server's preview URL includes the virtual path; the browser journey opens it.", From: "17.6.0", Before: "18.0.0"),
        new("src/packages/preview/controllers/preview.controller.ts", "new URL(previewUrlData.url, window.document.baseURI)",
            "The server's preview URL includes the virtual path; the browser journey opens it.", From: "18.1.0"),
        new("src/packages/preview/preview-apps/preview-environments.element.ts", "new URL(previewUrlData.url, window.document.baseURI)",
            "The same server preview URL as the document workspace's, which includes the virtual path.", From: "17.5.0", Before: "17.6.0"),
        new("src/packages/preview/preview-apps/preview-environments.element.ts", "new URL(previewUrlData.url, window.document.baseURI)",
            "The same server preview URL as the document workspace's, which includes the virtual path.", From: "18.0.0", Before: "18.1.0"),
        new("Umbraco.Web.UI.Login/src/contexts/auth.context.ts", "new URL(returnPath, window.location.origin)",
            "Checks that the server's ReturnUrl, which includes the virtual path, is on this origin."),
    ];

    /// <summary>
    /// Reads Umbraco's own sources, each with the bundles that contain it: the TypeScript in each source map, or the
    /// script itself when it has no map. Skips third-party code, translations, mocks and tests.
    /// </summary>
    private async Task<Dictionary<string, (string Text, List<string> Bundles)>> ReadSourcesAsync()
    {
        var sources = new Dictionary<string, (string Text, List<string> Bundles)>(StringComparer.Ordinal);
        foreach (var (path, file) in assets.Enumerate("/umbraco"))
        {
            // Third-party libraries and translations construct no Umbraco URLs.
            if (!path.EndsWith(".js", StringComparison.Ordinal) || path.Contains("/external/", StringComparison.Ordinal)
                || path.Contains("/assets/lang/", StringComparison.Ordinal) || path.EndsWith("/mockServiceWorker.js", StringComparison.Ordinal))
                continue;
            if (await SourceMap.ReadAsync(assets.Files.GetFileInfo(path + ".map")) is not { } map)
            {
                sources[path] = (await ReadAsync(file), [path]);
                continue;
            }

            for (var index = 0; index < map.Sources.Length; index++)
            {
                var name = map.Sources[index];
                if (map.Contents[index] is not { } text || NotUmbracoSource().IsMatch(name)) continue;
                if (sources.TryGetValue(name, out var existing))
                    existing.Bundles.Add(path);
                else
                    sources[name] = (text, [path]);
            }
        }
        return sources;
    }

    /// <summary>Reads <paramref name="file"/> as text.</summary>
    private static async Task<string> ReadAsync(IFileInfo file)
    {
        using var reader = new StreamReader(file.CreateReadStream());
        return await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Fails the test with the reason a change could not be applied.</summary>
    private static void Unapplied(string resource, string reason) => Assert.Fail($"Umbraco's {resource} {reason}");

    /// <summary>
    /// Matches a server URL, as a variable or <c>getServerUrl()</c>, joined directly to a dynamic part in a template
    /// literal or with <c>+</c>.
    /// </summary>
    [GeneratedRegex("""(?:(?:\b|[#_])server_?url|getServerUrl\(\))\s*(?:\}\s*\$\{|\+\s*(?![\s'"`]))""", RegexOptions.IgnoreCase)]
    private static partial Regex ServerUrlJoin();

    /// <summary>Matches <c>new URL(</c> called with a second argument, the base.</summary>
    [GeneratedRegex(@"new URL\((?:[^()]|\([^()]*\))+,")]
    private static partial Regex UrlWithBase();

    /// <summary>
    /// Matches <c>location.origin</c> compared with another value, tested with <c>startsWith</c>, or passed as a
    /// <c>postMessage</c> target origin.
    /// </summary>
    [GeneratedRegex(@"[!=]==\s*(?:window\.)?location\.origin|(?:window\.)?location\.origin\s*[!=]==|\.startsWith\(\s*(?:window\.)?location\.origin\s*\)|postMessage\(.*,\s*(?:window\.)?location\.origin\s*\)")]
    private static partial Regex OriginComparison();

    /// <summary>
    /// Matches a quoted root-absolute path under <c>/umbraco/</c>, <c>/App_Plugins/</c> or <c>/media/</c>.
    /// </summary>
    [GeneratedRegex("""['"`]/(?:umbraco|App_Plugins|media)/""")]
    private static partial Regex RootPath();

    /// <summary>Matches source map entries for dependencies, mocks, tests and stories, which the scan skips.</summary>
    [GeneratedRegex(@"node_modules|/mocks/|\.test\.ts$|\.stories\.ts$")]
    private static partial Regex NotUmbracoSource();
}
