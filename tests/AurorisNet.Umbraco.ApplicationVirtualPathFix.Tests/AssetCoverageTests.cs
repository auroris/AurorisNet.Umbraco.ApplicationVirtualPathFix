using System.Text.RegularExpressions;
using Microsoft.Extensions.FileProviders;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests;

public sealed partial class AssetCoverageTests(AssetPatchTests.Assets assets) : IClassFixture<AssetPatchTests.Assets>
{
    private static readonly BackOfficeAssetPatches.PatchContext Context = new("/portal", "/portal/umbraco", "/portal/umbraco/backoffice/hash");

    [Fact]
    public async Task Root_absolute_asset_references_are_patched_or_explained()
    {
        // This string classifies stack frames; it is not a fetched URL.
        const string diagnosticModule = "/umbraco/backoffice/packages/core/utils/index.js";
        var references = new Regex("""["'`(]/umbraco/(?:backoffice|website|login)/""");
        var unexpected = new List<string>();
        var examined = 0;
        foreach (var (path, file) in assets.Enumerate("/umbraco"))
        {
            var source = await ReadAsync(file);
            if (!references.IsMatch(source)) continue;
            examined++;
            if (path == diagnosticModule)
            {
                // Allow only the known diagnostic constant, not other URLs added to this file.
                source = source.Replace("const U = \"/umbraco/backoffice/\";", "", StringComparison.Ordinal);
            }
            else if (BackOfficeAssetPatches.For(path) is { } patch)
                source = BackOfficeAssetPatches.Patch(patch, source, Context, Unapplied);
            foreach (Match match in references.Matches(source))
                unexpected.Add($"{path}:{source.AsSpan(0, match.Index).Count('\n') + 1}: {match.Value}");
        }
        Assert.True(examined > 0, "The restored asset tree must not be empty.");
        Assert.True(unexpected.Count == 0, "Unpatched asset references:\n" + string.Join('\n', unexpected));
    }

    [Fact]
    public async Task Url_construction_in_Umbraco_sources_is_patched_or_reviewed()
    {
        // The bundles' source maps carry Umbraco's TypeScript, and the apps that ship without maps are
        // unminified, so URL construction can be found by name. A line is patched when an edit names it,
        // which PatchOriginTests confirms against the source maps, or when its bundle's asset URLs are moved.
        // Every review below must still match a line: a missing one means Umbraco changed that code.
        var sources = await ReadSourcesAsync();
        var unexpected = new List<string>();
        var found = new HashSet<ReviewedLine>();
        foreach (var (file, (text, bundles)) in sources)
        {
            var lines = text.Split('\n');
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index].Trim();
                if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith("/*", StringComparison.Ordinal)
                    || line.StartsWith('*') || !ConstructsUrl(line, file) || IsPatched(file, line, bundles))
                    continue;
                if (Reviews.FirstOrDefault(review => review.File == file && line.Contains(review.Code, StringComparison.Ordinal)) is { } known)
                    found.Add(known);
                else
                    unexpected.Add($"{file}:{index + 1}: {line}");
            }
        }

        Assert.True(sources.Count > 1000, $"Expected Umbraco's sources from the restored source maps, found {sources.Count}.");
        Assert.True(unexpected.Count == 0, "Patch or review these URL constructions:\n" + string.Join('\n', unexpected));
        var missing = Reviews.Where(review => !found.Contains(review)).Select(review => $"{review.File}: {review.Code}").ToArray();
        Assert.True(missing.Length == 0, "Umbraco no longer contains these reviewed lines; check whether the review still applies:\n"
            + string.Join('\n', missing));
    }

    /// <summary>Shapes of URL construction that lose the virtual path when a part is not what the code assumes.</summary>
    private static bool ConstructsUrl(string line, string file)
        // The server URL followed directly by a dynamic part, which may already include the virtual path.
        => ServerUrlJoin().IsMatch(line)
            // new URL(path, base): a root-absolute path replaces the base's path, virtual path included.
            || UrlWithBase().IsMatch(line)
            // The bare origin used to build a URL, rather than compared with one.
            || OriginComparison().Replace(line, "").Contains("location.origin", StringComparison.Ordinal)
            // Root-absolute paths, except the routes of the generated API client, which adds the server URL.
            || (!file.EndsWith(".gen.ts", StringComparison.Ordinal) && RootPath().IsMatch(line));

    /// <summary>Whether a patch changes the line: an edit names it, or a bundle containing it has its asset URLs moved.</summary>
    private static bool IsPatched(string file, string line, IEnumerable<string> bundles)
        => BackOfficeAssetPatches.Patches.SelectMany(patch => patch.Edits).Any(edit =>
                edit.Origin is { } origin && origin.File == file && line.Contains(origin.Code, StringComparison.Ordinal))
            || (BackOfficeAssetPatches.QuotedAssetRoot().IsMatch(line)
                && bundles.Any(bundle => BackOfficeAssetPatches.For(bundle) is { MovesAssetUrls: true }));

    /// <summary>A line of Umbraco's source that needs no patch, by its file and a fragment of its code.</summary>
    private sealed record ReviewedLine(string File, string Code, string Reason);

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
            "Classifies stack frames; it is not fetched."),
        new("src/packages/core/utils/path/ensure-local-path.function.ts", "new URL(path, window.location.origin)",
            "Checks that a path is on this origin; the path keeps its virtual path."),
        new("src/packages/core/utils/path/ensure-local-path.function.ts", "new URL(window.location.origin)",
            "The fallback for a path on another origin; the backoffice stores only its own routes."),
        new("src/packages/core/utils/path/stored-path.function.ts", "new URL(path, window.location.origin)",
            "Checks that a stored route is on this origin; the route keeps its virtual path."),
        new("src/packages/core/utils/path/umbraco-path.function.ts", "`/umbraco/management/api/${version}${path}`",
            "An exported helper that the backoffice does not call."),
        new("src/packages/core/workspace/utils/check-will-navigate-away.function.ts", "new URL(newUrl, window.location.origin)",
            "Compares two pathnames on this origin, which both include the virtual path."),
        new("src/packages/documents/document-redirect-management/dashboard-redirect-management.element.ts",
            "url.slice(window.location.origin.length)", "Shortens a URL for display."),
        new("src/packages/documents/documents/user-permissions/document/repository/document-permission.server.data.ts",
            "fetch(`/umbraco/management/api/v1/document/${id}/permissions`",
            "Registered but not called by the backoffice; without a bearer token it fails at the root too."),
        new("src/packages/media/media/url/info-app/media-link.function.ts", "new URL(url, base)", "Reads only the protocol."),
        new("src/packages/packages/package/repository/package.repository.ts", "`${serverUrl}${e.",
            "Package manifests use application-relative paths."),
        new("src/packages/preview/context/preview.context.ts", "new URL(`${url.pathname}?${params.toString()}`, host)",
            "The pathname comes from the patched preview URL, so it keeps the virtual path."),
        new("src/packages/preview/controllers/preview.controller.ts", "new URL(previewUrlData.url, window.document.baseURI)",
            "The server's preview URL includes the virtual path; the browser journey opens it."),
        new("Umbraco.Web.UI.Login/src/contexts/auth.context.ts", "new URL(returnPath, window.location.origin)",
            "Checks that the server's ReturnUrl, which includes the virtual path, is on this origin."),
    ];

    /// <summary>Umbraco's own sources, with the bundles that contain each: TypeScript from source maps, or the script itself.</summary>
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

    private static async Task<string> ReadAsync(IFileInfo file)
    {
        using var reader = new StreamReader(file.CreateReadStream());
        return await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
    }

    private static void Unapplied(string resource, string reason) => Assert.Fail($"Umbraco's {resource} {reason}");

    [GeneratedRegex("""(?:(?:\b|[#_])server_?url|getServerUrl\(\))\s*(?:\}\s*\$\{|\+\s*(?![\s'"`]))""", RegexOptions.IgnoreCase)]
    private static partial Regex ServerUrlJoin();

    [GeneratedRegex(@"new URL\((?:[^()]|\([^()]*\))+,")]
    private static partial Regex UrlWithBase();

    [GeneratedRegex(@"[!=]==\s*(?:window\.)?location\.origin|(?:window\.)?location\.origin\s*[!=]==|\.startsWith\(\s*(?:window\.)?location\.origin\s*\)|postMessage\(.*,\s*(?:window\.)?location\.origin\s*\)")]
    private static partial Regex OriginComparison();

    [GeneratedRegex("""['"`]/(?:umbraco|App_Plugins|media)/""")]
    private static partial Regex RootPath();

    [GeneratedRegex(@"node_modules|/mocks/|\.test\.ts$|\.stories\.ts$")]
    private static partial Regex NotUmbracoSource();
}
