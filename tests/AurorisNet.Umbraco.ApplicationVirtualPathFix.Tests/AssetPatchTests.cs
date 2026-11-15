using System.Text.RegularExpressions;
using Acornima;
using Microsoft.Extensions.FileProviders;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests;

/// <summary>
/// Checks every patch against the restored Umbraco files without starting a site, and tests how edits match and replace
/// code.
/// </summary>
public sealed class AssetPatchTests(AssetPatchTests.Assets assets) : IClassFixture<AssetPatchTests.Assets>
{
    private const string AssetsPath = "/portal/umbraco/backoffice/cache-hash";
    private static readonly PatchContext Context = new("/portal", "/portal/umbraco", AssetsPath);

    /// <summary>The entries that apply to the Umbraco release under test, one test case each, named as in test output.</summary>
    public static TheoryData<string> Patches => new(UmbracoUnderTest.Patches.Select(patch => patch.ToString()));

    /// <summary>
    /// Checks that <paramref name="entry"/> finds exactly one installed file and applies every change without changing
    /// the line count. Quoted asset URLs must move under the served asset root, and a script must parse as JavaScript
    /// both as installed and after patching.
    /// </summary>
    [Theory]
    [MemberData(nameof(Patches))]
    public async Task Every_patch_applies_to_the_installed_Umbraco_file(string entry)
    {
        var patch = UmbracoUnderTest.Patches.Single(patch => patch.ToString() == entry);
        var files = AssetPatcher.Locate(assets.Files, patch);
        Assert.True(files.Length == 1,
            $"Expected exactly one file for {entry}; found {files.Length}: {string.Join(", ", files.Select(file => file.Path))}");
        var source = await ReadAsync(files[0].File);
        var patched = AssetPatcher.Patch(patch, source, Context, Unapplied);
        Assert.NotEqual(source, patched);
        // Keep generated line numbers stable; replacements can still shift source-map columns.
        Assert.Equal(source.Count('\n'), patched.Count('\n'));

        if (patch.MovesAssetUrls)
        {
            // Each quoted URL, worker file names included, must move under the served asset root. Unquoted paths in
            // comments, such as the build notes in manifests.js, are prose and don't count.
            foreach (Match url in Regex.Matches(source, """["'`](/umbraco/backoffice/[^"'`\r\n]+)["'`]"""))
            {
                var original = url.Groups[1].Value;
                Assert.Contains(AssetsPath + original[AssetPatcher.AssetsRoot.Length..], patched, StringComparison.Ordinal);
                Assert.DoesNotContain(url.Value, patched, StringComparison.Ordinal);
            }
        }

        if (patch.Path.EndsWith(".js", StringComparison.Ordinal))
        {
            AssertParses(source, files[0].Path + " as installed");
            AssertParses(patched, files[0].Path + " after patching");
        }
    }

    /// <summary>
    /// Checks that every preamble fits on the first line and every edit keeps the number of lines it replaces, so patched
    /// files keep Umbraco's line numbers. For the Umbraco release under test, it also checks that no file gets its asset
    /// URLs moved twice or more than one preamble, which a second entry for the same file could otherwise add.
    /// </summary>
    [Fact]
    public void Patches_keep_line_numbers_and_apply_each_kind_of_change_once()
    {
        foreach (var patch in BackOfficeAssetPatches.Patches)
        {
            Assert.False((patch.Preamble ?? "").Contains('\n'), $"{patch}: the preamble must stay on the first line.");
            foreach (var edit in patch.Edits)
                Assert.True(edit.Find.Count('\n') == edit.Replace.Count('\n'), $"{patch}: {edit} changes the number of lines.");
        }
        foreach (var file in UmbracoUnderTest.Patches.GroupBy(patch => patch.Path, StringComparer.OrdinalIgnoreCase))
        {
            Assert.True(file.Count(patch => patch.MovesAssetUrls) <= 1, $"{file.Key}: more than one entry moves its asset URLs.");
            Assert.True(file.Count(patch => patch.Preamble is not null) <= 1, $"{file.Key}: more than one entry adds a preamble.");
        }
    }

    /// <summary>
    /// Checks that <see cref="AssetPatch.Contains"/> keeps, among the files that match an entry's name, only those whose
    /// text includes it, as it picks one of the two <c>constants-????????.js</c> chunks of Umbraco 17.5, 17.6, 18.0 and
    /// 18.1.
    /// </summary>
    [Fact]
    public void Contains_picks_the_bundle_with_its_text()
    {
        var root = Directory.CreateTempSubdirectory("asset-patch-contains-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "block"));
            File.WriteAllText(Path.Combine(root, "block", "constants-AAAAAAAA.js"), "export const a = 1;");
            File.WriteAllText(Path.Combine(root, "block", "constants-BBBBBBBB.js"), "const c = \"/umbraco/backoffice/css/umbraco-blockgridlayout.css\";");
            using var files = new PhysicalFileProvider(root);
            Assert.Equal(2, AssetPatcher.Locate(files, new AssetPatch { Path = "/block/constants-????????.js" }).Length);
            var patch = new AssetPatch { Path = "/block/constants-????????.js", Contains = "umbraco-blockgridlayout.css" };
            Assert.Equal("/block/constants-BBBBBBBB.js", Assert.Single(AssetPatcher.Locate(files, patch)).Path);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Checks that the package's own <c>auth.js</c> parses as a JavaScript module.</summary>
    [Fact]
    public async Task Package_scripts_parse()
    {
        AssertParses(await ReadAsync(assets.Files.GetFileInfo("/App_Plugins/AurorisNet.Umbraco.ApplicationVirtualPathFix/auth.js")), "auth.js");
    }

    /// <summary>
    /// Checks that <see cref="AssetPatcher.NormalizePath"/> replaces an eight-character hash before <c>.js</c>
    /// with <see cref="AssetPatcher.HashPattern"/>, and leaves stable names, other lengths and source maps as
    /// they are.
    /// </summary>
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
        Assert.Equal(expected, AssetPatcher.NormalizePath(path));
    }

    /// <summary>
    /// Checks that moving asset URLs rewrites quoted <c>/umbraco/backoffice/</c> URLs in strings and templates, keeps
    /// their file names, queries and fragments, and leaves comments, API paths and external URLs unchanged.
    /// </summary>
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
        Assert.Equal(expected, AssetPatcher.Patch(Manifests, source, Context, Unapplied));
    }

    /// <summary>
    /// Checks that quotes, backticks and ampersands in the asset root are percent-encoded, so an unusual application
    /// path stays valid in JavaScript strings and HTML attributes.
    /// </summary>
    [Fact]
    public void Asset_root_is_encoded_for_javascript_and_html_attributes()
    {
        const string source = """const image = html`<img src="/umbraco/backoffice/assets/logo.svg">`;""";
        var patched = AssetPatcher.Patch(Manifests, source, Context with { AssetsPath = "/a'b&c`d\"e/umbraco/backoffice/hash" }, Unapplied);
        Assert.Equal("""const image = html`<img src="/a%27b%26c%60d%22e/umbraco/backoffice/hash/assets/logo.svg">`;""", patched);
    }

    /// <summary>Checks that code patches apply to the same code built with different minified identifiers.</summary>
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
        Assert.Equal(expected, AssetPatcher.Patch(BackOfficeAssetPatches.Patches.Single(patch => patch.Path == path), source, Context, Unapplied));
    }

    /// <summary>
    /// Checks that a placeholder matches one whole identifier, that a repeated placeholder matches the same one, and
    /// that a placeholder opening the pattern skips property names. A rejected source stays unchanged and reports one
    /// reason.
    /// </summary>
    [Theory]
    [InlineData("s.startsWith(\"/\") ? `${this.#o}${s}` : s;", "new URL(s, this.#o).href;")]
    [InlineData("s.startsWith(\"/\") ? `${this.#o}${t}` : s;", null)] // A repeated placeholder is the same identifier.
    [InlineData("xs.startsWith(\"/\") ? `${this.#o}${s}` : s;", null)] // Placeholders match whole identifiers,
    [InlineData("s.startsWith(\"/\") ? `${this.#o}${s}` : sx;", null)]
    [InlineData("a.s.startsWith(\"/\") ? `${this.#o}${s}` : s;", null)] // and an opening one skips property names.
    public void Placeholders_match_whole_identifiers(string source, string? expected)
    {
        var edit = new AssetEdit { Find = "@url.startsWith(\"/\") ? `${this.@server}${@url}` : @url", Replace = "new URL(@url, this.@server).href" };
        var reasons = new List<string>();
        Assert.Equal(expected ?? source, AssetPatcher.Apply(source, "test.js", edit, (_, reason) => reasons.Add(reason)));
        Assert.Equal(expected is null ? 1 : 0, reasons.Count);
    }

    /// <summary>
    /// Checks that replacements insert captured identifiers and supplied values as literal text, that an undefined
    /// placeholder throws, and that <c>@@</c> stands for <c>@</c> in both the pattern and the replacement.
    /// </summary>
    [Fact]
    public void Replacements_insert_identifiers_and_values_literally()
    {
        // A .NET replacement string would treat "${" and "$1" as substitutions. Inserted values stay literal too, so
        // the "@a" inside suffix is not expanded.
        var edit = new AssetEdit { Find = "@a + @b", Replace = "`${@a}${@b}` + @suffix" };
        var values = new Dictionary<string, string> { ["suffix"] = "\"@a $1\"" };
        Assert.Equal("`${x}${y}` + \"@a $1\";", AssetPatcher.Apply("x + y;", "test.js", edit, Unapplied, values));
        Assert.Throws<InvalidOperationException>(() => AssetPatcher.Apply("x + y;", "test.js", new() { Find = "@a + @b", Replace = "@c" }, Unapplied));
        // @@ is a literal @ in both the pattern and the replacement.
        Assert.Equal("import '@umbraco-cms/x';", AssetPatcher.Apply("import '@umbraco-cms/y';", "test.js",
            new() { Find = "'@@umbraco-cms/@name'", Replace = "'@@umbraco-cms/x'" }, Unapplied));
    }

    /// <summary>
    /// Checks that an edit applies only when it finds the number of matches it expects, or at least one for
    /// <see cref="AssetEdit.Every"/>, and otherwise leaves the source unchanged and reports why.
    /// </summary>
    [Fact]
    public void Edits_apply_only_with_the_expected_number_of_matches()
    {
        const string source = "a + 1; b + 1;";
        var reasons = new List<string>();
        // Collects each unapplied change as its resource followed by the reason.
        void Record(string resource, string reason) => reasons.Add($"{resource} {reason}");

        Assert.Equal(source, AssetPatcher.Apply(source, "test.js", new() { Find = "@x + 1", Replace = "@x + 2" }, Record));
        Assert.Equal("test.js matches @x + 1 2 times instead of 1", Assert.Single(reasons));
        Assert.Equal("a + 2; b + 2;", AssetPatcher.Apply(source, "test.js", new() { Find = "@x + 1", Replace = "@x + 2", Count = 2 }, Record));
        Assert.Equal("a + 2; b + 2;", AssetPatcher.Apply(source, "test.js",
            new() { Find = "@x + 1", Replace = "@x + 2", Count = AssetEdit.Every }, Record));
        Assert.Equal(source, AssetPatcher.Apply(source, "test.js",
            new() { Find = "@x + 3", Replace = "@x + 4", Count = AssetEdit.Every }, Record));
        Assert.Equal(["test.js matches @x + 1 2 times instead of 1", "test.js no longer contains @x + 3"], reasons);
    }

    /// <summary>
    /// Checks that every login edit applies to the installed <c>login.js</c> for <paramref name="prefix"/>, and that the
    /// preamble that configures the shared API client starts the first line, ahead of Umbraco's code.
    /// </summary>
    [Theory]
    [InlineData("/portal")]
    [InlineData("/a@b")] // The configured path is inserted as text, so its @ starts no placeholder.
    public async Task Every_login_patch_matches_the_installed_script(string prefix)
    {
        var source = await ReadAsync(assets.Files.GetFileInfo("/umbraco/login/login.js"));
        var patched = AssetPatcher.Patch(AssetPatcher.For("/umbraco/login/login.js", UmbracoUnderTest.Release), source,
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

    /// <summary>
    /// Checks that the patches for the website's <c>nonodes.css</c> and <c>preview.js</c> apply to the installed files:
    /// the file at <paramref name="path"/> loses <paramref name="removed"/> and gains <paramref name="added"/>.
    /// </summary>
    [Theory]
    [InlineData("/umbraco/website/nonodes.css", "url(/umbraco/website/", "url(")]
    [InlineData("/umbraco/website/preview.js", "fetch(`/umbraco/", "fetch(`${this.getAttribute(\"path\") ?? \"/umbraco\"}/management/api/v1/preview`")]
    public async Task Website_asset_patches_match_the_installed_files(string path, string removed, string added)
    {
        var source = await ReadAsync(assets.Files.GetFileInfo(path));
        var patched = AssetPatcher.Patch(AssetPatcher.For(path, UmbracoUnderTest.Release), source, Context, Unapplied);
        Assert.DoesNotContain(removed, patched, StringComparison.Ordinal);
        Assert.Contains(added, patched, StringComparison.Ordinal);
    }

    /// <summary>
    /// Checks that when an Umbraco file no longer contains the code its patch expects, the patch reports each change it
    /// cannot apply: one report per edit, plus one for moving asset URLs.
    /// </summary>
    [Fact]
    public void Missing_search_patterns_are_reported_for_every_asset()
    {
        const string changed = "/* Assets used to live under /umbraco/backoffice/. */";
        var failures = new List<string>();
        foreach (var patch in BackOfficeAssetPatches.Patches)
        {
            failures.Clear();
            AssetPatcher.Patch(patch, changed, Context, (resource, reason) => failures.Add(resource + " " + reason));
            Assert.Equal(patch.Edits.Length + (patch.MovesAssetUrls ? 1 : 0), failures.Count);
        }
    }

    private static AssetPatch Manifests => AssetPatcher.For("/umbraco/backoffice/packages/core/manifests.js", UmbracoUnderTest.Release).Single();

    /// <summary>Fails the test with the reason a change could not be applied.</summary>
    private static void Unapplied(string resource, string reason) => Assert.Fail($"Umbraco's {resource} {reason}");

    /// <summary>
    /// Fails the test, naming <paramref name="description"/> and the error's position, when <paramref name="script"/>
    /// does not parse as a JavaScript module.
    /// </summary>
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

    /// <summary>Reads <paramref name="file"/> as text, failing the test when the file is missing.</summary>
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

        /// <summary>
        /// Builds, without starting it, a web application whose web root includes the restored Umbraco packages' static
        /// web assets, as the test project's runtime manifest lists them.
        /// </summary>
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

        /// <summary>The web root's file provider, which serves the restored Umbraco static assets.</summary>
        public IFileProvider Files => _app.Environment.WebRootFileProvider;

        /// <summary>
        /// Lists every JavaScript and CSS file under <paramref name="directory"/>, recursively, with its path.
        /// </summary>
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

        /// <summary>Returns the web root paths of the installed files that the patches apply to.</summary>
        public string[] PatchedPaths() => UmbracoUnderTest.Patches
            .SelectMany(patch => AssetPatcher.Locate(Files, patch)).Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        /// <summary>Disposes the web application.</summary>
        public ValueTask DisposeAsync() => _app.DisposeAsync();
    }
}
