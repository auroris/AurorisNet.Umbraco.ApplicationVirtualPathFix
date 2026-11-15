using System.Reflection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.Configuration;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Manifest;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services.OperationStatus;
using Umbraco.Cms.Web.Common.ApplicationBuilder;
using Umbraco.Cms.Web.Common.Hosting;
using IHostingEnvironment = Umbraco.Cms.Core.Hosting.IHostingEnvironment;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix;

/// <summary>
/// Registers the package's fixes for Umbraco 18.3.1 when Umbraco composes the site: hosting under the application
/// virtual path, backoffice assets, media, sign-in links and session restore.
/// </summary>
/// <remarks>
/// The pipeline filter runs before Umbraco's own filters, ImageSharp among them, so the rest of the pipeline sees the
/// same path base on Kestrel as under an IIS child application.
/// </remarks>
public sealed class ApplicationVirtualPathFixComposer : IComposer
{
    private const string Name = "AurorisNet.Umbraco.ApplicationVirtualPathFix";

    /// <summary>
    /// Registers the fixes with <paramref name="builder"/>: the OAuth callback settings, the status and health check,
    /// the body Tag Helper component, the backoffice path generator, a pipeline filter that patches assets and applies
    /// the path base, and decorators for Umbraco's import map, media URLs and sign-in links.
    /// </summary>
    public void Compose(IUmbracoBuilder builder)
    {
        var services = builder.Services;
        services.AddVirtualPathHosting();
        services.AddSingleton<VirtualPathStatus>();
        builder.HealthChecks().Add<VirtualPathHealthCheck>();
        services.AddTransient<ITagHelperComponent, BackOfficeBodyTagHelperComponent>();
        services.Configure<UmbracoPipelineOptions>(options => options.PipelineFilters.Insert(0, new UmbracoPipelineFilter(
            Name,
            prePipeline: app =>
            {
                WarnOnUmbracoVersionMismatch(app.ApplicationServices);
                if (VirtualPathHosting.Prefix(app.ApplicationServices.GetRequiredService<IHostingEnvironment>()) is { } prefix)
                {
                    var status = app.ApplicationServices.GetRequiredService<VirtualPathStatus>();
                    var environment = app.ApplicationServices.GetRequiredService<IWebHostEnvironment>();
                    environment.WebRootFileProvider = new BackOfficeAssetFileProvider(environment.WebRootFileProvider,
                        app.ApplicationServices.GetRequiredService<IBackOfficePathGenerator>(), prefix,
                        app.ApplicationServices.GetRequiredService<ILoggerFactory>().CreateLogger(Name), status);
                    app.UseWhen(context => !context.Request.PathBase.HasValue, branch => branch.UsePathBase(prefix));
                    // Kestrel also serves requests that arrive outside the virtual path. Count them for the health check.
                    app.Use((context, next) =>
                    {
                        if (!context.Request.PathBase.HasValue) status.RequestOutside(context.Request.Path);
                        return next(context);
                    });
                }
                app.UseUmbracoPathsUnderPathBase();
            },
            postPipeline: app => app.UseMediaUnderPathBase())));
        services.Replace(ServiceDescriptor.Singleton<IBackOfficePathGenerator, BackOfficePaths>());
        // ManagementApiComposer registers the default URL assembler again after this composer runs. A collection
        // builder registers in UmbracoBuilder.Build(), after every composer, so the decorator wraps the final
        // registration and the package needs no reference to the optional management API.
        builder.WithCollectionBuilder<MediaUrlAssemblerRegistration>();
        Decorate<IPackageManifestService>(services, required: true,
            (provider, inner) => new VirtualPathImportMapService(inner, provider.GetRequiredService<IBackOfficePathGenerator>()));
        // The management API registers these providers, and a site can leave it out.
        Decorate<IInviteUriProvider>(services, required: false,
            (provider, inner) => new VirtualPathInviteUriProvider(inner, provider.GetRequiredService<IHostingEnvironment>()));
        Decorate<IForgotPasswordUriProvider>(services, required: false,
            (provider, inner) => new VirtualPathForgotPasswordUriProvider(inner, provider.GetRequiredService<IHostingEnvironment>()));
    }

    /// <summary>Registers the media URL decorator in <c>UmbracoBuilder.Build()</c>, after every composer has run.</summary>
    private sealed class MediaUrlAssemblerRegistration : ICollectionBuilder
    {
        /// <summary>Decorates the final <see cref="IUrlAssembler"/> registration with <see cref="MediaUrlAssembler"/>.</summary>
        public void RegisterWith(IServiceCollection services)
            => Decorate<IUrlAssembler>(services, required: true,
                (provider, inner) => new MediaUrlAssembler(inner, provider.GetRequiredService<IHostingEnvironment>(),
                    provider.GetRequiredService<IOptions<GlobalSettings>>().Value));
    }

    /// <summary>
    /// Replaces the last unkeyed registration of <typeparamref name="TService"/> with one that passes the original
    /// service to <paramref name="decorate"/>, keeping its lifetime. When no registration exists, returns if
    /// <paramref name="required"/> is <see langword="false"/> and otherwise throws <see cref="InvalidOperationException"/>.
    /// </summary>
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

    /// <summary>
    /// Creates the service <paramref name="descriptor"/> registers, from its instance, factory or implementation type.
    /// </summary>
    private static TService Resolve<TService>(IServiceProvider provider, ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationInstance is TService instance)
            return instance;
        if (descriptor.ImplementationFactory is not null)
            return (TService)descriptor.ImplementationFactory(provider);
        return (TService)ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!);
    }

    /// <summary>
    /// Returns the Umbraco version the package was built for when <paramref name="umbraco"/> reports a different one,
    /// and <see langword="null"/> when they match or the build recorded no version.
    /// </summary>
    internal static string? MismatchedUmbracoVersion(IUmbracoVersion umbraco)
    {
        var expected = typeof(ApplicationVirtualPathFixComposer).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "UmbracoVersion")?.Value;
        var actual = umbraco.SemanticVersion;
        return !Version.TryParse(expected, out var built)
            || (built.Major == actual.Major && built.Minor == actual.Minor && built.Build == actual.Patch) ? null : expected;
    }

    /// <summary>
    /// Logs a warning when the site runs a different Umbraco version from the one the package was built for.
    /// </summary>
    internal static void WarnOnUmbracoVersionMismatch(IServiceProvider services)
    {
        var umbraco = services.GetRequiredService<IUmbracoVersion>();
        if (MismatchedUmbracoVersion(umbraco) is not { } expected)
            return;

        // NuGet restore usually stops a mismatched site with NU1107. A site that references Umbraco.Cms.Web.Common or
        // Umbraco.Cms.StaticAssets directly gets only warning NU1608 and still runs.
        services.GetRequiredService<ILoggerFactory>().CreateLogger(Name).LogWarning(
            "{Package} was built for Umbraco {Expected}, but this site runs Umbraco {Actual}. Some of its fixes patch "
            + "that version's backoffice scripts and may not apply. Install the package release for the site's Umbraco version.",
            Name, expected, umbraco.SemanticVersion);
    }

    /// <summary>
    /// Adds the virtual path to the root-absolute entries of the backoffice import map and maps
    /// <c>@umbraco-cms/backoffice/auth</c> to the package's <c>auth.js</c>.
    /// </summary>
    /// <remarks>
    /// Umbraco's import map entries are root-absolute: core modules point at <c>/umbraco/backoffice/...</c> and package
    /// modules at <c>/App_Plugins/...</c>. When Umbraco renders the map, it replaces the backoffice virtual directory
    /// with the cache-busted assets path. Under a virtual path that directory is <c>/portal/umbraco/backoffice</c>,
    /// which the map never contains, so core entries would keep unhashed URLs and every entry would load from the root.
    /// With the prefix added first, Umbraco's replacement adds the hash, and package modules load from the same URL the
    /// backoffice uses for their manifests, so the browser evaluates each module once.
    /// <para>
    /// The package's <c>auth.js</c> sets the server URL under a virtual path and skips Umbraco 18.3's initial token
    /// refresh on pages where no session can exist, so those pages start sign-in directly. It applies at the root too.
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

    /// <summary>
    /// Adds the virtual path to a successful invitation or password-reset link. Umbraco 18.3.1 replaces the application
    /// URL's path with <c>/umbraco/login</c>, which drops the virtual path. A link that already has it is returned as is.
    /// </summary>
    private static Attempt<Uri, UserOperationStatus> WithVirtualPath(Attempt<Uri, UserOperationStatus> attempt, IHostingEnvironment hosting)
    {
        if (!attempt.Success || attempt.Result is not { } uri || VirtualPathHosting.Prefix(hosting) is not { } prefix
            || uri.AbsolutePath.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
            return attempt;

        return Attempt.SucceedWithStatus(attempt.Status, new UriBuilder(uri) { Path = prefix + uri.AbsolutePath }.Uri);
    }

    /// <summary>Adds the virtual path to the invitation links Umbraco emails to new users.</summary>
    private sealed class VirtualPathInviteUriProvider(IInviteUriProvider inner, IHostingEnvironment hosting) : IInviteUriProvider
    {
        /// <summary>Creates Umbraco's invitation link for <paramref name="invitee"/> and adds the virtual path.</summary>
        public async Task<Attempt<Uri, UserOperationStatus>> CreateInviteUriAsync(IUser invitee)
            => WithVirtualPath(await inner.CreateInviteUriAsync(invitee), hosting);
    }

    /// <summary>Adds the virtual path to the password-reset links Umbraco emails to users.</summary>
    private sealed class VirtualPathForgotPasswordUriProvider(IForgotPasswordUriProvider inner, IHostingEnvironment hosting) : IForgotPasswordUriProvider
    {
        /// <summary>Creates Umbraco's password-reset link for <paramref name="user"/> and adds the virtual path.</summary>
        public async Task<Attempt<Uri, UserOperationStatus>> CreateForgotPasswordUriAsync(IUser user)
            => WithVirtualPath(await inner.CreateForgotPasswordUriAsync(user), hosting);

        /// <summary>Returns whether Umbraco's provider can create password-reset links.</summary>
        public Task<Attempt<UserOperationStatus>> CanCreateForgotPasswordUriAsync() => inner.CanCreateForgotPasswordUriAsync();
    }
}
