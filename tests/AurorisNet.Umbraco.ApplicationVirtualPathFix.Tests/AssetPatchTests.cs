using System.Text.RegularExpressions;
using Acornima;
using Microsoft.Extensions.FileProviders;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests;

public sealed class AssetPatchTests(AssetPatchTests.Assets assets) : IClassFixture<AssetPatchTests.Assets>
{
    private const string AssetsPath = "/portal/umbraco/backoffice/cache-hash";
    private static readonly BackOfficeAssetPatches.PatchContext Context = new("/portal", "/portal/umbraco", AssetsPath);

    public static TheoryData<string> Patches => new(BackOfficeAssetPatches.Patches.Select(patch => patch.Path));

    [Theory]
    [MemberData(nameof(Patches))]
    public async Task Every_patch_applies_to_the_installed_Umbraco_file(string path)
    {
        var patch = BackOfficeAssetPatches.Patches.Single(patch => patch.Path == path);
        var files = BackOfficeAssetPatches.Locate(assets.Files, patch);
        Assert.True(files.Length == 1,
            $"Expected exactly one file for {path}; found {files.Length}: {string.Join(", ", files.Select(file => file.Path))}");
        var source = await ReadAsync(files[0].File);
        var patched = BackOfficeAssetPatches.Patch(patch, source, Context, Unapplied);
        Assert.NotEqual(source, patched);
        // The original source map still describes every line of the patched file.
        Assert.Equal(source.Count('\n'), patched.Count('\n'));

        if (patch.MovesAssetUrls)
        {
            // Check the actual URL values, including worker filenames, against the served asset root.
            // Unquoted paths in comments (such as manifests.js's build notes) do not count.
            foreach (Match url in Regex.Matches(source, """["'`](/umbraco/backoffice/[^"'`\r\n]+)["'`]"""))
            {
                var original = url.Groups[1].Value;
                Assert.Contains(AssetsPath + original[BackOfficeAssetPatches.AssetsRoot.Length..], patched, StringComparison.Ordinal);
                Assert.DoesNotContain(url.Value, patched, StringComparison.Ordinal);
            }
        }

        if (path.EndsWith(".js", StringComparison.Ordinal))
        {
            AssertParses(source, files[0].Path + " as installed");
            AssertParses(patched, files[0].Path + " after patching");
        }
    }

    [Fact]
    public void Patches_keep_line_numbers_and_name_each_file_once()
    {
        Assert.Equal(BackOfficeAssetPatches.Patches.Length,
            BackOfficeAssetPatches.Patches.Select(patch => patch.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var patch in BackOfficeAssetPatches.Patches)
        {
            Assert.False((patch.Preamble ?? "").Contains('\n'), $"{patch.Path}: the preamble must stay on the first line.");
            foreach (var edit in patch.Edits)
                Assert.True(edit.Find.Count('\n') == edit.Replace.Count('\n'), $"{patch.Path}: {edit} changes the number of lines.");
        }
    }

    [Fact]
    public async Task Package_scripts_parse()
    {
        AssertParses(await ReadAsync(assets.Files.GetFileInfo("/App_Plugins/AurorisNet.Umbraco.ApplicationVirtualPathFix/auth.js")), "auth.js");
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
        Assert.Equal(expected, BackOfficeAssetPatches.NormalizePath(path));
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
        Assert.Equal(expected, BackOfficeAssetPatches.Patch(Manifests, source, Context, Unapplied));
    }

    [Fact]
    public void Asset_root_is_encoded_for_javascript_and_html_attributes()
    {
        const string source = """const image = html`<img src="/umbraco/backoffice/assets/logo.svg">`;""";
        var patched = BackOfficeAssetPatches.Patch(Manifests, source, Context with { AssetsPath = "/a'b&c`d\"e/umbraco/backoffice/hash" }, Unapplied);
        Assert.Equal("""const image = html`<img src="/a%27b%26c%60d%22e/umbraco/backoffice/hash/assets/logo.svg">`;""", patched);
    }

    [Theory]
    // Umbraco 18.2.1 built the same code with other minified identifiers.
    [InlineData("/umbraco/backoffice/packages/block/property-editor-ui-block-grid.element-????????.js",
        "return new URL(ti(o), this.#s).href;",
        "return new URL(ti(o).replace(/^\\/(?!\\/)/, \"\"), this.#s + \"/\").href;")]
    [InlineData("/umbraco/backoffice/packages/preview/index.js",
        "return s?.url ? s.url.startsWith(\"/\") ? `${this.#r}${s.url}` : s.url : null;\nconst r = new URL(s, e), i = new URLSearchParams(r.search);",
        "return s?.url ? s.url.startsWith(\"/\") ? new URL(s.url, this.#r || location.origin).href : s.url : null;\n"
            + "const r = new URL(s, e.endsWith(\"/\") ? e : e + \"/\"), i = new URLSearchParams(r.search);")]
    public void Code_patches_follow_renamed_identifiers(string path, string source, string expected)
    {
        Assert.Equal(expected, BackOfficeAssetPatches.Patch(BackOfficeAssetPatches.Patches.Single(patch => patch.Path == path), source, Context, Unapplied));
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
        // @@ is a literal @ in both the pattern and the replacement.
        Assert.Equal("import '@umbraco-cms/x';", BackOfficeAssetPatches.Apply("import '@umbraco-cms/y';", "test.js",
            new("'@@umbraco-cms/@name'", "'@@umbraco-cms/x'"), Unapplied));
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
        var patched = BackOfficeAssetPatches.Patch(BackOfficeAssetPatches.For("/umbraco/login/login.js")!, source,
            Context with { PathBase = prefix, BackOfficePath = prefix + "/umbraco" }, Unapplied);
        Assert.Contains($"new Request(\"{prefix}\" + \"/umbraco/", patched, StringComparison.Ordinal);
        Assert.Contains($"window.location.origin + \"{prefix}\"", patched, StringComparison.Ordinal);
        Assert.Contains($"backofficePath: \"{prefix}/umbraco\"", patched, StringComparison.Ordinal);
        Assert.DoesNotContain("new Request(\"/umbraco/", patched, StringComparison.Ordinal);
        // The shared API client is configured on the first line, ahead of Umbraco's own first statement.
        var firstLine = patched[..patched.IndexOf('\n')];
        Assert.StartsWith("import { umbHttpClient } from '@umbraco-cms/backoffice/http-client'; "
            + $"umbHttpClient.setConfig({{ baseUrl: window.location.origin + \"{prefix}\" }}); ", firstLine, StringComparison.Ordinal);
        Assert.EndsWith(source[..source.IndexOf('\n')], firstLine, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/umbraco/website/nonodes.css", "url(/umbraco/website/", "url(")]
    [InlineData("/umbraco/website/preview.js", "fetch(`/umbraco/", "fetch(`${this.getAttribute(\"path\") ?? \"/umbraco\"}/management/api/v1/preview`")]
    public async Task Website_asset_patches_match_the_installed_files(string path, string removed, string added)
    {
        var source = await ReadAsync(assets.Files.GetFileInfo(path));
        var patched = BackOfficeAssetPatches.Patch(BackOfficeAssetPatches.For(path)!, source, Context, Unapplied);
        Assert.DoesNotContain(removed, patched, StringComparison.Ordinal);
        Assert.Contains(added, patched, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_search_patterns_are_reported_for_every_asset()
    {
        const string changed = "/* Assets used to live under /umbraco/backoffice/. */";
        var failures = new List<string>();
        foreach (var patch in BackOfficeAssetPatches.Patches)
        {
            failures.Clear();
            BackOfficeAssetPatches.Patch(patch, changed, Context, (resource, reason) => failures.Add(resource + " " + reason));
            Assert.Equal(patch.Edits.Length + (patch.MovesAssetUrls ? 1 : 0), failures.Count);
        }
    }

    private static BackOfficeAssetPatches.AssetPatch Manifests => BackOfficeAssetPatches.For("/umbraco/backoffice/packages/core/manifests.js")!;

    private static void Unapplied(string resource, string reason) => Assert.Fail($"Umbraco's {resource} {reason}");

    private static void AssertParses(string script, string description)
    {
        try
        {
            new Parser().ParseModule(script, description);
        }
        catch (ParseErrorException error)
        {
            Assert.Fail($"{description} is not valid JavaScript at {error.LineNumber}:{error.Column}: {error.Message}");
        }
    }

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

        /// <summary>The installed files that the patches apply to.</summary>
        public string[] PatchedPaths() => BackOfficeAssetPatches.Patches
            .SelectMany(patch => BackOfficeAssetPatches.Locate(Files, patch)).Select(file => file.Path).ToArray();

        public ValueTask DisposeAsync() => _app.DisposeAsync();
    }
}
