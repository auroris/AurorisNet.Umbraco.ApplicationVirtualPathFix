using Umbraco.Cms.Core.Manifest;
using Umbraco.Cms.Web.Common.Hosting;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix;

/// <summary>
/// Adds the virtual path to the root-absolute entries of the backoffice import map and maps
/// <c>@umbraco-cms/backoffice/auth</c> to the package's <c>auth.js</c>.
/// </summary>
/// <remarks>
/// Umbraco's import map entries are root-absolute: core modules point at <c>/umbraco/backoffice/...</c> and package
/// modules at <c>/App_Plugins/...</c>. When Umbraco renders the map, it replaces the backoffice virtual directory with
/// the cache-busted assets path. Under a virtual path that directory is <c>/portal/umbraco/backoffice</c>, which the map
/// never contains, so core entries would keep unhashed URLs and every entry would load from the root. With the prefix
/// added first, Umbraco's replacement adds the hash, and package modules load from the same URL the backoffice uses for
/// their manifests, so the browser evaluates each module once.
/// <para>
/// The package's <c>auth.js</c> sets the server URL under a virtual path and skips Umbraco's initial token refresh on
/// pages where no session can exist, so those pages start sign-in directly. It applies at the root too.
/// </para>
/// </remarks>
internal sealed class VirtualPathImportMapService(IPackageManifestService inner, IBackOfficePathGenerator paths) : IPackageManifestService
{
    private const string BackOfficeAssets = "/umbraco/backoffice";

    /// <summary>Returns Umbraco's package manifests unchanged.</summary>
    public Task<IEnumerable<PackageManifest>> GetAllPackageManifestsAsync() => inner.GetAllPackageManifestsAsync();

    /// <summary>Returns Umbraco's public package manifests unchanged.</summary>
    public Task<IEnumerable<PackageManifest>> GetPublicPackageManifestsAsync() => inner.GetPublicPackageManifestsAsync();

    /// <summary>Returns Umbraco's private package manifests unchanged.</summary>
    public Task<IEnumerable<PackageManifest>> GetPrivatePackageManifestsAsync() => inner.GetPrivatePackageManifestsAsync();

    /// <summary>
    /// Returns Umbraco's import map with the virtual path added to root-absolute imports and scopes, and with
    /// <c>@umbraco-cms/backoffice/auth</c> mapped to the package's wrapper.
    /// </summary>
    public async Task<PackageManifestImportmap> GetPackageManifestImportmapAsync()
    {
        PackageManifestImportmap map = await inner.GetPackageManifestImportmapAsync();
        var virtualDirectory = paths.BackOfficeVirtualDirectory.TrimEnd('/');
        // "/portal/umbraco/backoffice" -> "/portal"
        var virtualPath = virtualDirectory[..^BackOfficeAssets.Length];
        var imports = Rewrite(map.Imports, virtualPath);
        // The wrapper imports and re-exports the original module under this name, so every importer shares one
        // instance of it and its context tokens.
        imports["@aurorisnet/umbraco-application-virtual-path-fix/upstream-auth"] = imports["@umbraco-cms/backoffice/auth"];
        imports["@umbraco-cms/backoffice/auth"] = virtualPath + "/App_Plugins/AurorisNet.Umbraco.ApplicationVirtualPathFix/auth.js?v=" + BackOfficePaths.BuildVersion;
        Dictionary<string, Dictionary<string, string>>? scopes = null;
        if (map.Scopes is not null)
        {
            // When two scopes become the same URL, the last one wins, as when Umbraco merges manifests.
            scopes = new Dictionary<string, Dictionary<string, string>>();
            foreach (var scope in map.Scopes)
                scopes[Rewrite(scope.Key, virtualPath)] = Rewrite(scope.Value, virtualPath);
        }

        return new PackageManifestImportmap
        {
            Imports = imports,
            Scopes = scopes,
        };
    }

    /// <summary>Copies <paramref name="entries"/>, passing each value through <see cref="Rewrite(string, string)"/>.</summary>
    private static Dictionary<string, string> Rewrite(Dictionary<string, string> entries, string virtualPath)
        => entries.ToDictionary(entry => entry.Key, entry => Rewrite(entry.Value, virtualPath));

    /// <summary>
    /// Prefixes a root-absolute <paramref name="value"/> with <paramref name="virtualPath"/>. Returns other values,
    /// including protocol-relative URLs and paths that already carry the prefix, unchanged.
    /// </summary>
    private static string Rewrite(string value, string virtualPath)
    {
        var isRootAbsolute = value.StartsWith('/') && !value.StartsWith("//", StringComparison.Ordinal);
        var alreadyPrefixed = value.StartsWith(virtualPath + "/", StringComparison.Ordinal);
        return isRootAbsolute && !alreadyPrefixed ? virtualPath + value : value;
    }
}
