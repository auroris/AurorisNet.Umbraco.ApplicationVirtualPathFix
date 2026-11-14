using Microsoft.Extensions.DependencyInjection.Extensions;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Manifest;
using Umbraco.Cms.Web.Common.ApplicationBuilder;
using Umbraco.Cms.Web.Common.Hosting;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix;

/// <summary>
/// Registers the application virtual path, backoffice asset and authentication fixes for Umbraco 18.3.0–18.3.1.
/// </summary>
/// <remarks>
/// The backoffice page carries an import map whose entries are root-absolute: the core ones point
/// at <c>/umbraco/backoffice/...</c> and package ones at <c>/App_Plugins/...</c>. When rendering
/// it, Umbraco replaces the backoffice virtual directory with the cache-busted assets path, but
/// under a virtual path that directory is <c>/portal/umbraco/backoffice</c>, which never occurs in
/// the map, so the core entries stay unhashed and every entry is fetched from the root. Prefixing
/// each root-absolute entry with the virtual path before Umbraco renders the map lets its own
/// replacement add the hash and keeps package modules on the same URL the SPA uses for their
/// manifests, so the browser evaluates each module once. The auth entry also wraps Umbraco 18.3's
/// initial refresh attempt: a page without a refresh-token cookie starts the login flow directly.
/// </remarks>
public sealed class ApplicationVirtualPathFixComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        var services = builder.Services;
        services.AddVirtualPathHosting(builder.Config);
        services.Configure<UmbracoPipelineOptions>(options => options.AddFilter(new UmbracoPipelineFilter(
            "AurorisNet.Umbraco.ApplicationVirtualPathFix", prePipeline: app =>
            {
                if (VirtualPathHosting.Prefix(builder.Config) is { } prefix)
                    app.UsePathBase(prefix);
                app.UseBackOfficeAssetsUnderPathBase();
                app.UseBackOfficeAppUnderVirtualPath();
            })));
        services.Replace(ServiceDescriptor.Singleton<IBackOfficePathGenerator, BackOfficePaths>());
        var inner = services.LastOrDefault(d => d.ServiceType == typeof(IPackageManifestService))
            ?? throw new InvalidOperationException(
                $"{nameof(IPackageManifestService)} is not registered; update {nameof(ApplicationVirtualPathFixComposer)} for this Umbraco version.");

        services.Remove(inner);
        services.Add(new ServiceDescriptor(
            typeof(IPackageManifestService),
            provider => new VirtualPathImportMapService(Resolve(provider, inner), provider.GetRequiredService<IBackOfficePathGenerator>()),
            inner.Lifetime));
    }

    private static IPackageManifestService Resolve(IServiceProvider provider, ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationInstance is IPackageManifestService instance)
            return instance;
        if (descriptor.ImplementationFactory is not null)
            return (IPackageManifestService)descriptor.ImplementationFactory(provider);
        return (IPackageManifestService)ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!);
    }

    private sealed class VirtualPathImportMapService(IPackageManifestService inner, IBackOfficePathGenerator paths) : IPackageManifestService
    {
        private const string BackOfficeAssets = "/umbraco/backoffice";

        public Task<IEnumerable<PackageManifest>> GetAllPackageManifestsAsync() => inner.GetAllPackageManifestsAsync();

        public Task<IEnumerable<PackageManifest>> GetPublicPackageManifestsAsync() => inner.GetPublicPackageManifestsAsync();

        public Task<IEnumerable<PackageManifest>> GetPrivatePackageManifestsAsync() => inner.GetPrivatePackageManifestsAsync();

        public async Task<PackageManifestImportmap> GetPackageManifestImportmapAsync()
        {
            PackageManifestImportmap map = await inner.GetPackageManifestImportmapAsync();
            var virtualDirectory = paths.BackOfficeVirtualDirectory.TrimEnd('/');
            // "/portal/umbraco/backoffice" -> "/portal"
            var virtualPath = virtualDirectory[..^BackOfficeAssets.Length];
            var imports = Rewrite(map.Imports, virtualPath);
            // Keep the original module (and its context tokens) shared by the wrapper's re-exports.
            imports["@aurorisnet/umbraco-application-virtual-path-fix/upstream-auth"] = imports["@umbraco-cms/backoffice/auth"];
            imports["@umbraco-cms/backoffice/auth"] = virtualPath + "/App_Plugins/AurorisNet.Umbraco.ApplicationVirtualPathFix/auth.js?v=" + BackOfficeAppUnderVirtualPath.BuildVersion;
            return new PackageManifestImportmap
            {
                Imports = imports,
                Scopes = map.Scopes?.ToDictionary(scope => Rewrite(scope.Key, virtualPath), scope => Rewrite(scope.Value, virtualPath)),
            };
        }

        private static Dictionary<string, string> Rewrite(Dictionary<string, string> entries, string virtualPath)
            => entries.ToDictionary(entry => entry.Key, entry => Rewrite(entry.Value, virtualPath));

        private static string Rewrite(string value, string virtualPath)
        {
            var isRootAbsolute = value.StartsWith('/') && !value.StartsWith("//", StringComparison.Ordinal);
            var alreadyPrefixed = value.StartsWith(virtualPath + "/", StringComparison.Ordinal);
            return isRootAbsolute && !alreadyPrefixed ? virtualPath + value : value;
        }
    }
}
