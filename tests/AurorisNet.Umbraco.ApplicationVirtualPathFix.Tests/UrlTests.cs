using AurorisNet.Umbraco.ApplicationVirtualPathFix.BackOffice;
using AurorisNet.Umbraco.ApplicationVirtualPathFix.Media;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.Manifest;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Routing;
using IHostingEnvironment = Umbraco.Cms.Core.Hosting.IHostingEnvironment;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests;

/// <summary>Checks the media URL, import map and OAuth callback corrections without starting a site.</summary>
public sealed class UrlTests
{
    /// <summary>
    /// Checks that the media URL assembler removes only a duplicated virtual path from media URLs and keeps the rest of
    /// Umbraco's result exactly, signed queries and fragments included. A URL that needs no change comes back as the
    /// same instance.
    /// </summary>
    [Theory]
    [InlineData("/portal", "~/media", "/portal/media/image.png", "/portal/portal/media/image.png", "/portal/media/image.png")]
    [InlineData("/portal", "~/media", "/portal/media/image.png", "https://site.test/portal/portal/media/image.png", "https://site.test/portal/media/image.png")]
    [InlineData("/portal", "~/media", "/portal/media/image.png", "/portal/portal/media/image.png", "/portal/media/image.png", "")]
    [InlineData("/portal", "~/media", "/portal/media/icon%20name.svg", "/portal/portal/media/icon%20name.svg", "/portal/media/icon%20name.svg", "#icon?x=1")]
    [InlineData("/portal", "~/media", "/portal/media/icon%20name.svg", "https://site.test/portal/portal/media/icon%20name.svg", "https://site.test/portal/media/icon%20name.svg", "#icon")]
    [InlineData("/a/b", "~/uploads", "/a/b/uploads/image.png", "/a/b/a/b/uploads/image.png", "/a/b/uploads/image.png")]
    [InlineData("/portal", "~/media", "/portal/media/image.png", "/portal/media/image.png", "/portal/media/image.png")]
    [InlineData("/portal", "~/media", "/media/image.png", "/portal/media/image.png", "/portal/media/image.png")]
    [InlineData("/portal", "~/media", "/portal/articles/", "/portal/portal/articles/", "/portal/portal/articles/")]
    [InlineData("/portal", "~/media", "/portal/mediastore/image.png", "/portal/portal/mediastore/image.png", "/portal/portal/mediastore/image.png")]
    [InlineData("/portal", "~/media", "https://cdn.test/media/image.png", "https://cdn.test/media/image.png", "https://cdn.test/media/image.png")]
    [InlineData("/portal", "~/media", "/portal/media/image.png", "https://cdn.test/assets/image.png", "https://cdn.test/assets/image.png")]
    [InlineData("/", "~/media", "/media/image.png", "/media/image.png", "/media/image.png")]
    public void Media_rewrite_preserves_everything_except_a_duplicated_media_prefix(
        string prefix, string mediaPath, string input, string upstream, string expected,
        string suffix = "?width=100&hmac=A%2Bb%2F%3D#crop")
    {
        // The default suffix holds a signed query, whose exact bytes the signature covers, and a fragment that callers
        // keep.
        var current = new Uri("https://site.test/portal/");
        var upstreamUri = new Uri(upstream + suffix, UriKind.RelativeOrAbsolute);
        var mode = upstreamUri.IsAbsoluteUri ? UrlMode.Absolute : UrlMode.Relative;
        var inner = new StubAssembler((path, origin, requestedMode) =>
        {
            Assert.Equal(input + suffix, path);
            Assert.Same(current, origin);
            Assert.Equal(mode, requestedMode);
            return upstreamUri;
        });
        var assembler = new MediaUrlAssembler(inner, new TestHostingEnvironment(prefix), new GlobalSettings { UmbracoMediaPath = mediaPath });

        var actual = assembler.AssembleUrl(input + suffix, current, mode);

        Assert.Equal(expected + suffix, actual.OriginalString);
        if (upstream == expected) Assert.Same(upstreamUri, actual);
    }

    /// <summary>
    /// Checks that the import map decorator prefixes root-absolute imports and scopes, including a path that only
    /// starts with the prefix's text, such as <c>/portal-other</c>. Relative, absolute, protocol-relative and prefixed
    /// URLs stay as they are, and the auth module maps to the package's wrapper. Umbraco's cached map stays unchanged,
    /// so a second call returns the same result.
    /// </summary>
    [Fact]
    public async Task Import_map_rewrites_root_urls_and_scopes_without_mutating_cached_input()
    {
        var source = new PackageManifestImportmap
        {
            Imports = new()
            {
                ["@umbraco-cms/backoffice/auth"] = "/umbraco/backoffice/packages/core/auth/index.js",
                ["plugin"] = "/App_Plugins/Example/main.js",
                ["prefixed"] = "/portal/App_Plugins/Example/other.js",
                ["sibling"] = "/portal-other/main.js",
                ["relative"] = "./module.js",
                ["absolute"] = "https://cdn.test/module.js",
                ["network"] = "//cdn.test/module.js",
            },
            Scopes = new()
            {
                ["/App_Plugins/Example/"] = new() { ["dependency"] = "/App_Plugins/Dependency/main.js" },
                ["/portal/already/"] = new() { ["dependency"] = "/portal/dependency.js" },
                ["./relative/"] = new() { ["dependency"] = "./dependency.js" },
                ["https://cdn.test/"] = new() { ["dependency"] = "//cdn.test/dependency.js" },
            },
        };
        var service = new VirtualPathImportMapService(new Manifests(source), new TestBackOfficePaths("/portal"));

        var result = await service.GetPackageManifestImportmapAsync();

        Assert.Equal("/portal/App_Plugins/Example/main.js", result.Imports["plugin"]);
        Assert.Equal(source.Imports["prefixed"], result.Imports["prefixed"]);
        Assert.Equal("/portal/portal-other/main.js", result.Imports["sibling"]);
        foreach (var name in new[] { "relative", "absolute", "network" })
            Assert.Equal(source.Imports[name], result.Imports[name]);
        Assert.Equal("/portal/App_Plugins/Dependency/main.js", result.Scopes!["/portal/App_Plugins/Example/"]["dependency"]);
        Assert.Equal("/portal/dependency.js", result.Scopes["/portal/already/"]["dependency"]);
        Assert.Equal("./dependency.js", result.Scopes["./relative/"]["dependency"]);
        Assert.Equal("//cdn.test/dependency.js", result.Scopes["https://cdn.test/"]["dependency"]);
        Assert.Equal("/portal/umbraco/backoffice/packages/core/auth/index.js",
            result.Imports["@aurorisnet/umbraco-application-virtual-path-fix/upstream-auth"]);
        Assert.Equal("/portal/App_Plugins/AurorisNet.Umbraco.ApplicationVirtualPathFix/auth.js?v=" + BackOfficePaths.BuildVersion,
            result.Imports["@umbraco-cms/backoffice/auth"]);

        Assert.Equal("/App_Plugins/Example/main.js", source.Imports["plugin"]);
        Assert.Equal("/umbraco/backoffice/packages/core/auth/index.js", source.Imports["@umbraco-cms/backoffice/auth"]);
        Assert.DoesNotContain("@aurorisnet/umbraco-application-virtual-path-fix/upstream-auth", source.Imports.Keys);
        Assert.Equal("/App_Plugins/Dependency/main.js", source.Scopes["/App_Plugins/Example/"]["dependency"]);
        // Umbraco can return the same cached map for the next request.
        var again = await service.GetPackageManifestImportmapAsync();
        Assert.Equal(result.Imports.OrderBy(entry => entry.Key), again.Imports.OrderBy(entry => entry.Key));
    }

    /// <summary>
    /// Checks that at the root the import map keeps its URLs and its missing scopes, while the auth module still maps to
    /// the package's wrapper.
    /// </summary>
    [Fact]
    public async Task Root_import_map_keeps_original_urls_and_absent_scopes()
    {
        var source = new PackageManifestImportmap
        {
            Imports = new()
            {
                ["@umbraco-cms/backoffice/auth"] = "/umbraco/backoffice/packages/core/auth/index.js",
                ["plugin"] = "/App_Plugins/Example/main.js",
            },
        };
        var service = new VirtualPathImportMapService(new Manifests(source), new TestBackOfficePaths(""));

        var result = await service.GetPackageManifestImportmapAsync();

        Assert.Null(result.Scopes);
        Assert.Equal(source.Imports["plugin"], result.Imports["plugin"]);
        Assert.Equal(source.Imports["@umbraco-cms/backoffice/auth"],
            result.Imports["@aurorisnet/umbraco-application-virtual-path-fix/upstream-auth"]);
        Assert.StartsWith("/App_Plugins/AurorisNet.Umbraco.ApplicationVirtualPathFix/auth.js?v=", result.Imports["@umbraco-cms/backoffice/auth"]);
    }

    /// <summary>
    /// Checks that custom OAuth callback paths, with or without a leading slash or already prefixed, get the virtual
    /// path exactly once, and that a site at the root keeps them as configured.
    /// </summary>
    [Theory]
    [InlineData("/portal", "/portal/custom/login", "/portal/custom/logout", "/portal/custom/error")]
    [InlineData("/a/b", "/a/b/custom/login", "/a/b/custom/logout", "/a/b/custom/error")]
    [InlineData("/", "custom/login", "/custom/logout", "/custom/error")]
    public void Custom_callbacks_accept_both_path_forms_and_an_existing_prefix(
        string prefix, string login, string logout, string error)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHostingEnvironment>(new TestHostingEnvironment(prefix));
        services.Configure<SecuritySettings>(settings =>
        {
            settings.AuthorizeCallbackPathName = "custom/login";
            settings.AuthorizeCallbackLogoutPathName = "/custom/logout";
            settings.AuthorizeCallbackErrorPathName = prefix.TrimEnd('/') + "/custom/error";
        });
        services.AddVirtualPathHosting();
        using var provider = services.BuildServiceProvider();

        var settings = provider.GetRequiredService<IOptions<SecuritySettings>>().Value;

        Assert.Equal(login, settings.AuthorizeCallbackPathName);
        Assert.Equal(logout, settings.AuthorizeCallbackLogoutPathName);
        Assert.Equal(error, settings.AuthorizeCallbackErrorPathName);
    }

    /// <summary>A URL assembler that returns what <paramref name="assemble"/> builds.</summary>
    private sealed class StubAssembler(Func<string, Uri, UrlMode, Uri> assemble) : IUrlAssembler
    {
        /// <summary>Returns the URL the test's function builds for <paramref name="path"/>.</summary>
        public Uri AssembleUrl(string path, Uri current, UrlMode mode) => assemble(path, current, mode);
    }

    /// <summary>A manifest service that returns <paramref name="map"/> as Umbraco's import map.</summary>
    private sealed class Manifests(PackageManifestImportmap map) : IPackageManifestService
    {
        /// <summary>Returns the same map instance on every call, as Umbraco's cache does.</summary>
        public Task<PackageManifestImportmap> GetPackageManifestImportmapAsync() => Task.FromResult(map);
        /// <summary>Throws <see cref="NotSupportedException"/>, because the tests read only the import map.</summary>
        public Task<IEnumerable<PackageManifest>> GetAllPackageManifestsAsync() => throw new NotSupportedException();
        /// <summary>Throws <see cref="NotSupportedException"/>, because the tests read only the import map.</summary>
        public Task<IEnumerable<PackageManifest>> GetPublicPackageManifestsAsync() => throw new NotSupportedException();
        /// <summary>Throws <see cref="NotSupportedException"/>, because the tests read only the import map.</summary>
        public Task<IEnumerable<PackageManifest>> GetPrivatePackageManifestsAsync() => throw new NotSupportedException();
    }
}
