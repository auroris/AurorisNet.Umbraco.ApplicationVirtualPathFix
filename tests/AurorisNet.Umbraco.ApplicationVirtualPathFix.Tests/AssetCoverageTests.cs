using System.Text.RegularExpressions;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests;

public sealed class AssetCoverageTests(AssetPatchTests.Assets assets) : IClassFixture<AssetPatchTests.Assets>
{
    private const string AssetsPath = "/portal/umbraco/backoffice/hash";

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
            using var reader = new StreamReader(file.CreateReadStream());
            var source = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
            if (!references.IsMatch(source)) continue;
            examined++;
            var module = path.StartsWith(BackOfficeAssetPatches.AssetsRoot + "/", StringComparison.Ordinal)
                ? BackOfficeAssetPatches.NormalizeModulePath(path[BackOfficeAssetPatches.AssetsRoot.Length..]) : "";
            if (path == diagnosticModule)
            {
                // Allow only the known diagnostic constant, not other URLs added to this file.
                source = source.Replace("const U = \"/umbraco/backoffice/\";", "", StringComparison.Ordinal);
            }
            else if (BackOfficeAssetPatches.Modules.Contains(module))
                source = BackOfficeAssetPatches.PatchModule(module, source, AssetsPath, Unapplied);
            else if (path == "/umbraco/website/nonodes.css")
                source = BackOfficeAssetPatches.PatchWebsiteStyles(source, Unapplied);
            else if (path == "/umbraco/login/login.js")
                source = BackOfficeAssetPatches.PatchLoginScript(source, "/portal", "/portal/umbraco", Unapplied);
            foreach (Match match in references.Matches(source))
                unexpected.Add($"{path}:{source.AsSpan(0, match.Index).Count('\n') + 1}: {match.Value}");
        }
        Assert.True(examined > 0, "The restored asset tree must not be empty.");
        Assert.True(unexpected.Count == 0, "Unpatched asset references:\n" + string.Join('\n', unexpected));
    }

    [Fact]
    public async Task Server_url_consumers_are_patched_or_reviewed()
    {
        // New consumers need review even when the server URL is assigned to a minified variable.
        // Known URL behavior is checked by the patch tests and browser journey; scanning arbitrary
        // adjacent template expressions also matched unrelated string joins in these modules.
        var reviewed = new Dictionary<string, string>
        {
            ["/apps/app/app.element.js"] = "Creates the server context from the app's server-url attribute.",
            ["/apps/app/app-logo.element.js"] = "Appends the fixed graphics API endpoint.",
            ["/libs/extension-api/index.js"] = "Plugin manifests supply application-relative module paths.",
            ["/packages/core/auth/index.js"] = "Constructs OAuth callbacks from origin and the configured backoffice path.",
            ["/packages/core/server/index.js"] = "Stores and exposes the configured server URL.",
            ["/packages/core/umb-auth-view.element-????????.js"] = "Appends fixed login graphics API endpoints.",
            ["/packages/block/block-type/index.js"] = "Resolves a configured thumbnail with new URL, not concatenation.",
            ["/packages/block/block-catalogue-modal.token-????????.js"] = "Resolves configured thumbnails with new URL.",
            ["/packages/packages/package.repository-????????.js"] = "Plugin manifests supply application-relative module paths.",
            ["/packages/management-api/server-event.context-????????.js"] = "Appends the fixed server-event hub endpoint.",
        };
        var consumer = new Regex(@"\b_?serverUrl\b|getServerUrl\s*\(");
        var unexpected = new List<string>();
        var examined = 0;
        foreach (var (path, file) in assets.Enumerate(BackOfficeAssetPatches.AssetsRoot))
        {
            if (!path.EndsWith(".js", StringComparison.Ordinal)) continue;
            using var reader = new StreamReader(file.CreateReadStream());
            var source = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
            if (!consumer.IsMatch(source)) continue;
            examined++;
            var module = BackOfficeAssetPatches.NormalizeModulePath(path[BackOfficeAssetPatches.AssetsRoot.Length..]);
            if (BackOfficeAssetPatches.Modules.Contains(module))
                BackOfficeAssetPatches.PatchModule(module, source, AssetsPath, Unapplied);
            else if (!reviewed.ContainsKey(module))
                unexpected.Add($"{module}: unreviewed server URL consumer");
        }
        Assert.True(examined > 0, "The restored assets must contain server URL consumers.");
        Assert.True(unexpected.Count == 0, "Review server URL usage:\n" + string.Join('\n', unexpected));
    }

    private static void Unapplied(string resource, string reason) => Assert.Fail($"Umbraco's {resource} {reason}");
}
