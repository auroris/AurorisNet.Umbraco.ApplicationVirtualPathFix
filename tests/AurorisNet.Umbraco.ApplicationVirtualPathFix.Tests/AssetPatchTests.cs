using System.Text.RegularExpressions;
using Microsoft.Extensions.FileProviders;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests;

public sealed class AssetPatchTests(AssetPatchTests.Assets assets) : IClassFixture<AssetPatchTests.Assets>
{
    private const string AssetsPath = "/portal/umbraco/backoffice/cache-hash";

    public static IEnumerable<object[]> Modules()
        => BackOfficeAssetPatches.Modules.Select(module => new object[] { module });

    [Theory]
    [MemberData(nameof(Modules))]
    public async Task Every_module_patch_matches_the_installed_Umbraco_bundle(string module)
    {
        var directory = module[..module.LastIndexOf('/')];
        var matches = assets.Files.GetDirectoryContents(BackOfficeAssetPatches.AssetsRoot + directory)
            .Where(file => !file.IsDirectory
                && BackOfficeAssetPatches.NormalizeModulePath(directory + "/" + file.Name) == module)
            .ToArray();
        Assert.True(matches.Length == 1,
            $"Expected exactly one bundle for {module}; found {matches.Length}: {string.Join(", ", matches.Select(file => file.Name))}");
        var source = await ReadAsync(matches[0]);
        var patched = BackOfficeAssetPatches.PatchModule(module, source, AssetsPath, Unapplied);
        Assert.NotEqual(source, patched);

        // Check the actual URL values, including worker filenames, against the served asset root.
        // Unquoted paths in comments (such as manifests.js's build notes) do not count.
        foreach (Match url in Regex.Matches(source, """["'`](/umbraco/backoffice/[^"'`\r\n]+)["'`]"""))
        {
            var original = url.Groups[1].Value;
            Assert.Contains(AssetsPath + original[BackOfficeAssetPatches.AssetsRoot.Length..], patched, StringComparison.Ordinal);
            Assert.DoesNotContain(url.Value, patched, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("/packages/tiptap/input-tiptap.element-Ab12_-Xy.js", "/packages/tiptap/input-tiptap.element-????????.js")]
    [InlineData("/packages/tiptap/input-tiptap.element-Z9y8X7w6.js", "/packages/tiptap/input-tiptap.element-????????.js")]
    [InlineData("/packages/tiptap/input-tiptap.element.js", "/packages/tiptap/input-tiptap.element.js")]
    [InlineData("/packages/core/manifests.js", "/packages/core/manifests.js")]
    [InlineData("/packages/other/input-tiptap.element-Ab12_-Xy.js", "/packages/other/input-tiptap.element-????????.js")]
    [InlineData("/packages/tiptap/input-tiptap.element-Ab12_Xy.js", "/packages/tiptap/input-tiptap.element-Ab12_Xy.js")]
    [InlineData("/packages/tiptap/input-tiptap.element-Ab12_Xy99.js", "/packages/tiptap/input-tiptap.element-Ab12_Xy99.js")]
    [InlineData("/packages/tiptap/input-tiptap.element-Ab12_-Xy.js.map", "/packages/tiptap/input-tiptap.element-Ab12_-Xy.js.map")]
    public void Bundle_normalization_replaces_only_the_eight_character_hash(string path, string expected)
    {
        Assert.Equal(expected, BackOfficeAssetPatches.NormalizeModulePath(path));
    }

    [Fact]
    public void Shared_asset_rewrite_preserves_filenames_and_other_urls()
    {
        const string source = """
            // Files are copied into /umbraco/backoffice/css/ during the build.
            const css = "/umbraco/backoffice/css/dark.css";
            const worker = new Worker('/umbraco/backoffice/external/monaco-editor/assets/json.worker-New_Ha-h.js');
            const logo = html`<img src="/umbraco/backoffice/assets/logo.svg?v=1#image">`;
            const api = "/umbraco/management/api/v1/media";
            const external = "https://cdn.example.test/umbraco/backoffice/assets/logo.svg";
            """;
        const string expected = """
            // Files are copied into /umbraco/backoffice/css/ during the build.
            const css = "/portal/umbraco/backoffice/cache-hash/css/dark.css";
            const worker = new Worker('/portal/umbraco/backoffice/cache-hash/external/monaco-editor/assets/json.worker-New_Ha-h.js');
            const logo = html`<img src="/portal/umbraco/backoffice/cache-hash/assets/logo.svg?v=1#image">`;
            const api = "/umbraco/management/api/v1/media";
            const external = "https://cdn.example.test/umbraco/backoffice/assets/logo.svg";
            """;
        Assert.Equal(expected, BackOfficeAssetPatches.PatchModule("/packages/core/manifests.js", source, AssetsPath, Unapplied));
    }

    [Fact]
    public void Asset_root_is_encoded_for_javascript_and_html_attributes()
    {
        const string source = """const image = html`<img src="/umbraco/backoffice/assets/logo.svg">`;""";
        var patched = BackOfficeAssetPatches.PatchModule("/packages/core/manifests.js", source,
            "/a'b&c`d\"e/umbraco/backoffice/hash", Unapplied);
        Assert.Equal("""const image = html`<img src="/a%27b%26c%60d%22e/umbraco/backoffice/hash/assets/logo.svg">`;""", patched);
    }

    [Theory]
    // Umbraco 18.2.1 built the same code with other minified identifiers.
    [InlineData("/packages/block/property-editor-ui-block-grid.element-????????.js",
        "return new URL(ti(o), this.#s).href;",
        "return new URL(ti(o).replace(/^\\/(?!\\/)/, \"\"), this.#s + \"/\").href;")]
    [InlineData("/packages/preview/index.js",
        "return s?.url ? s.url.startsWith(\"/\") ? `${this.#r}${s.url}` : s.url : null;\nconst r = new URL(s, e), i = new URLSearchParams(r.search);",
        "return s?.url ? s.url.startsWith(\"/\") ? new URL(s.url, this.#r || location.origin).href : s.url : null;\n"
            + "const r = new URL(s, e.endsWith(\"/\") ? e : e + \"/\"), i = new URLSearchParams(r.search);")]
    public void Code_patches_follow_renamed_identifiers(string module, string source, string expected)
    {
        Assert.Equal(expected, BackOfficeAssetPatches.PatchModule(module, source, AssetsPath, Unapplied));
    }

    [Theory]
    [InlineData("s.startsWith(\"/\") ? `${this.#o}${s}` : s;", "new URL(s, this.#o).href;")]
    [InlineData("s.startsWith(\"/\") ? `${this.#o}${t}` : s;", null)] // A repeated placeholder is the same identifier.
    [InlineData("xs.startsWith(\"/\") ? `${this.#o}${s}` : s;", null)] // Placeholders match whole identifiers,
    [InlineData("s.startsWith(\"/\") ? `${this.#o}${s}` : sx;", null)]
    [InlineData("a.s.startsWith(\"/\") ? `${this.#o}${s}` : s;", null)] // and an opening one is not a property.
    public void Placeholders_match_whole_identifiers(string source, string? expected)
    {
        var edit = new BackOfficeAssetPatches.Edit("@url.startsWith(\"/\") ? `${this.@server}${@url}` : @url", "new URL(@url, this.@server).href");
        var reasons = new List<string>();
        Assert.Equal(expected ?? source, BackOfficeAssetPatches.Apply(source, "test.js", edit, (_, reason) => reasons.Add(reason)));
        Assert.Equal(expected is null ? 1 : 0, reasons.Count);
    }

    [Fact]
    public void Replacements_insert_identifiers_and_values_literally()
    {
        // A .NET replacement string would read "${" and "$1"; inserted values are not read as placeholders.
        var edit = new BackOfficeAssetPatches.Edit("@a + @b", "`${@a}${@b}` + @suffix");
        var values = new Dictionary<string, string> { ["suffix"] = "\"@a $1\"" };
        Assert.Equal("`${x}${y}` + \"@a $1\";", BackOfficeAssetPatches.Apply("x + y;", "test.js", edit, Unapplied, values));
        Assert.Throws<InvalidOperationException>(() => BackOfficeAssetPatches.Apply("x + y;", "test.js", new("@a + @b", "@c"), Unapplied));
    }

    [Fact]
    public void Edits_apply_only_with_the_expected_number_of_matches()
    {
        const string source = "a + 1; b + 1;";
        var reasons = new List<string>();
        void Record(string resource, string reason) => reasons.Add($"{resource} {reason}");

        Assert.Equal(source, BackOfficeAssetPatches.Apply(source, "test.js", new("@x + 1", "@x + 2"), Record));
        Assert.Equal("test.js matches @x + 1 2 times instead of 1", Assert.Single(reasons));
        Assert.Equal("a + 2; b + 2;", BackOfficeAssetPatches.Apply(source, "test.js", new("@x + 1", "@x + 2", 2), Record));
        Assert.Equal("a + 2; b + 2;", BackOfficeAssetPatches.Apply(source, "test.js",
            new("@x + 1", "@x + 2", BackOfficeAssetPatches.Edit.Every), Record));
        Assert.Equal(source, BackOfficeAssetPatches.Apply(source, "test.js",
            new("@x + 3", "@x + 4", BackOfficeAssetPatches.Edit.Every), Record));
        Assert.Equal(["test.js matches @x + 1 2 times instead of 1", "test.js no longer contains @x + 3"], reasons);
    }

    [Theory]
    [InlineData("/portal")]
    [InlineData("/a@b")] // The configured path is inserted as text, not read as a placeholder.
    public async Task Every_login_patch_matches_the_installed_script(string prefix)
    {
        var source = await ReadAsync(assets.Files.GetFileInfo("/umbraco/login/login.js"));
        var patched = BackOfficeAssetPatches.PatchLoginScript(source, prefix, prefix + "/umbraco", Unapplied);
        Assert.Contains($"new Request(\"{prefix}\" + \"/umbraco/", patched, StringComparison.Ordinal);
        Assert.Contains($"window.location.origin + \"{prefix}\"", patched, StringComparison.Ordinal);
        Assert.Contains($"backofficePath: \"{prefix}/umbraco\"", patched, StringComparison.Ordinal);
        Assert.Contains("umbHttpClient.setConfig", patched, StringComparison.Ordinal);
        Assert.DoesNotContain("new Request(\"/umbraco/", patched, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Website_styles_patch_matches_the_installed_stylesheet()
    {
        var source = await ReadAsync(assets.Files.GetFileInfo("/umbraco/website/nonodes.css"));
        var patched = BackOfficeAssetPatches.PatchWebsiteStyles(source, Unapplied);
        Assert.NotEqual(source, patched);
        Assert.DoesNotContain("url(/umbraco/website/", patched, StringComparison.Ordinal);
        Assert.Contains("url(", patched, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_search_patterns_are_reported_for_every_asset()
    {
        const string changed = "/* Assets used to live under /umbraco/backoffice/. */";
        var failures = new List<string>();
        void Record(string resource, string reason) => failures.Add(resource + " " + reason);
        foreach (var module in BackOfficeAssetPatches.Modules)
        {
            failures.Clear();
            BackOfficeAssetPatches.PatchModule(module, changed, AssetsPath, Record);
            Assert.NotEmpty(failures);
        }
        failures.Clear();
        BackOfficeAssetPatches.PatchLoginScript(changed, "/portal", "/portal/umbraco", Record);
        Assert.Equal(3, failures.Count);
        failures.Clear();
        BackOfficeAssetPatches.PatchWebsiteStyles(changed, Record);
        Assert.Single(failures);
    }

    private static void Unapplied(string resource, string reason) => Assert.Fail($"Umbraco's {resource} {reason}");

    private static async Task<string> ReadAsync(IFileInfo file)
    {
        Assert.True(file.Exists, $"Missing Umbraco asset: {file.Name}");
        using var reader = new StreamReader(file.CreateReadStream());
        return await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Reads the same restored static assets as the test site, without starting a server.</summary>
    public sealed class Assets : IAsyncDisposable
    {
        private readonly WebApplication _app;

        public Assets()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(AssetPatchTests).Assembly.GetName().Name,
                ContentRootPath = AppContext.BaseDirectory,
                EnvironmentName = "Development",
            });
            builder.WebHost.UseSetting(WebHostDefaults.StaticWebAssetsKey,
                    Path.Combine(AppContext.BaseDirectory, typeof(AssetPatchTests).Assembly.GetName().Name + ".staticwebassets.runtime.json"))
                .UseStaticWebAssets();
            builder.Logging.ClearProviders();
            _app = builder.Build();
        }

        public IFileProvider Files => _app.Environment.WebRootFileProvider;

        public IEnumerable<(string Path, IFileInfo File)> Enumerate(string directory)
        {
            foreach (var file in Files.GetDirectoryContents(directory))
            {
                var path = directory + "/" + file.Name;
                if (file.IsDirectory)
                {
                    foreach (var child in Enumerate(path)) yield return child;
                }
                else if (path.EndsWith(".js", StringComparison.Ordinal) || path.EndsWith(".css", StringComparison.Ordinal))
                    yield return (path, file);
            }
        }

        public string[] ModulePaths() => Enumerate(BackOfficeAssetPatches.AssetsRoot)
            .Where(asset => BackOfficeAssetPatches.Modules.Contains(
                BackOfficeAssetPatches.NormalizeModulePath(asset.Path[BackOfficeAssetPatches.AssetsRoot.Length..])))
            .Select(asset => asset.Path).ToArray();

        public ValueTask DisposeAsync() => _app.DisposeAsync();
    }
}
