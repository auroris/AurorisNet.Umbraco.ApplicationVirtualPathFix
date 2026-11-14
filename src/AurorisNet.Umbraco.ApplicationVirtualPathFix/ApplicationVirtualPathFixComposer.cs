using System.Reflection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.Configuration;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Manifest;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services.OperationStatus;
using Umbraco.Cms.Web.Common.ApplicationBuilder;
using Umbraco.Cms.Web.Common.Hosting;
using IHostingEnvironment = Umbraco.Cms.Core.Hosting.IHostingEnvironment;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix;

/// <summary>
/// Registers the application virtual path, backoffice asset, media and authentication fixes for Umbraco 18.3.1.
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
/// initial refresh attempt: a page where no session can exist starts the login flow directly.
/// <para>
/// The pipeline filter runs before Umbraco's own filters (ImageSharp among them) so that every
/// part of the pipeline sees the same path base on Kestrel as under an IIS child application.
/// </para>
/// </remarks>
public sealed class ApplicationVirtualPathFixComposer : IComposer
{
    private const string Name = "AurorisNet.Umbraco.ApplicationVirtualPathFix";

    public void Compose(IUmbracoBuilder builder)
    {
        var services = builder.Services;
        services.AddVirtualPathHosting();
        services.Configure<UmbracoPipelineOptions>(options => options.PipelineFilters.Insert(0, new UmbracoPipelineFilter(
            Name,
            prePipeline: app =>
            {
                WarnOnUmbracoVersionMismatch(app.ApplicationServices);
                if (VirtualPathHosting.Prefix(app.ApplicationServices.GetRequiredService<IHostingEnvironment>()) is { } prefix)
                    app.UsePathBase(prefix);
                app.UseUmbracoPathsUnderPathBase();
                app.UseBackOfficeAppUnderVirtualPath();
            },
            postPipeline: app => app.UseMediaUnderPathBase())));
        services.Replace(ServiceDescriptor.Singleton<IBackOfficePathGenerator, BackOfficePaths>());
        Decorate<IPackageManifestService>(services, required: true,
            (provider, inner) => new VirtualPathImportMapService(inner, provider.GetRequiredService<IBackOfficePathGenerator>()));
        // Registered by the management API, which a site can leave out.
        Decorate<IInviteUriProvider>(services, required: false,
            (provider, inner) => new VirtualPathInviteUriProvider(inner, provider.GetRequiredService<IHostingEnvironment>()));
        Decorate<IForgotPasswordUriProvider>(services, required: false,
            (provider, inner) => new VirtualPathForgotPasswordUriProvider(inner, provider.GetRequiredService<IHostingEnvironment>()));
    }

    private static void Decorate<TService>(IServiceCollection services, bool required, Func<IServiceProvider, TService, TService> decorate)
        where TService : class
    {
        var inner = services.LastOrDefault(d => d.ServiceType == typeof(TService) && !d.IsKeyedService);
        if (inner is null)
        {
            if (!required)
                return;
            throw new InvalidOperationException(
                $"{typeof(TService).Name} is not registered; update {nameof(ApplicationVirtualPathFixComposer)} for this Umbraco version.");
        }

        services.Remove(inner);
        services.Add(new ServiceDescriptor(typeof(TService), provider => decorate(provider, Resolve<TService>(provider, inner)), inner.Lifetime));
    }

    private static TService Resolve<TService>(IServiceProvider provider, ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationInstance is TService instance)
            return instance;
        if (descriptor.ImplementationFactory is not null)
            return (TService)descriptor.ImplementationFactory(provider);
        return (TService)ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!);
    }

    private static void WarnOnUmbracoVersionMismatch(IServiceProvider services)
    {
        var expected = typeof(ApplicationVirtualPathFixComposer).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "UmbracoVersion")?.Value;
        var actual = services.GetRequiredService<IUmbracoVersion>().SemanticVersion;
        if (!Version.TryParse(expected, out var built)
            || (built.Major == actual.Major && built.Minor == actual.Minor && built.Build == actual.Patch))
            return;

        // Restore usually fails first (NU1107), but a site that references Umbraco.Cms.Web.Common or
        // Umbraco.Cms.StaticAssets directly only gets a warning (NU1608) and runs anyway.
        services.GetRequiredService<ILoggerFactory>().CreateLogger(Name).LogWarning(
            "{Package} was built for Umbraco {Expected}, but this site runs Umbraco {Actual}. Some of its fixes patch "
            + "that version's backoffice scripts and may not apply. Install the package release for the site's Umbraco version.",
            Name, expected, actual);
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
            Dictionary<string, Dictionary<string, string>>? scopes = null;
            if (map.Scopes is not null)
            {
                // Last one wins when two scopes become the same URL, as Umbraco does when merging manifests.
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

        private static Dictionary<string, string> Rewrite(Dictionary<string, string> entries, string virtualPath)
            => entries.ToDictionary(entry => entry.Key, entry => Rewrite(entry.Value, virtualPath));

        private static string Rewrite(string value, string virtualPath)
        {
            var isRootAbsolute = value.StartsWith('/') && !value.StartsWith("//", StringComparison.Ordinal);
            var alreadyPrefixed = value.StartsWith(virtualPath + "/", StringComparison.Ordinal);
            return isRootAbsolute && !alreadyPrefixed ? virtualPath + value : value;
        }
    }

    /// <summary>Umbraco 18.3.1 replaces the application URL's path with <c>/umbraco/login</c>, dropping the virtual path.</summary>
    private static Attempt<Uri, UserOperationStatus> WithVirtualPath(Attempt<Uri, UserOperationStatus> attempt, IHostingEnvironment hosting)
    {
        if (!attempt.Success || attempt.Result is not { } uri || VirtualPathHosting.Prefix(hosting) is not { } prefix
            || uri.AbsolutePath.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
            return attempt;

        return Attempt.SucceedWithStatus(attempt.Status, new UriBuilder(uri) { Path = prefix + uri.AbsolutePath }.Uri);
    }

    private sealed class VirtualPathInviteUriProvider(IInviteUriProvider inner, IHostingEnvironment hosting) : IInviteUriProvider
    {
        public async Task<Attempt<Uri, UserOperationStatus>> CreateInviteUriAsync(IUser invitee)
            => WithVirtualPath(await inner.CreateInviteUriAsync(invitee), hosting);
    }

    private sealed class VirtualPathForgotPasswordUriProvider(IForgotPasswordUriProvider inner, IHostingEnvironment hosting) : IForgotPasswordUriProvider
    {
        public async Task<Attempt<Uri, UserOperationStatus>> CreateForgotPasswordUriAsync(IUser user)
            => WithVirtualPath(await inner.CreateForgotPasswordUriAsync(user), hosting);

        public Task<Attempt<UserOperationStatus>> CanCreateForgotPasswordUriAsync() => inner.CanCreateForgotPasswordUriAsync();
    }
}
