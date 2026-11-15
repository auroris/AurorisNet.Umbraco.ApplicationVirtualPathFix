using System.Reflection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.Configuration;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Manifest;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Web.Common.ApplicationBuilder;
using Umbraco.Cms.Web.Common.Hosting;
using IHostingEnvironment = Umbraco.Cms.Core.Hosting.IHostingEnvironment;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix;

/// <summary>
/// Registers the package's fixes when Umbraco composes the site: hosting under the application virtual path,
/// backoffice assets, media, sign-in links and session restore.
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
        // Umbraco 18 gave the filter's full constructor two more parameters, so the package, compiled against Umbraco 17,
        // uses the constructor and properties that both versions have.
        services.Configure<UmbracoPipelineOptions>(options => options.PipelineFilters.Insert(0, new UmbracoPipelineFilter(Name)
        {
            PrePipeline = app =>
            {
                WarnOnUnsupportedUmbracoVersion(app.ApplicationServices);
                var umbraco = AssetPatcher.Release(app.ApplicationServices.GetRequiredService<IUmbracoVersion>().SemanticVersion);
                if (VirtualPathHosting.Prefix(app.ApplicationServices.GetRequiredService<IHostingEnvironment>()) is { } prefix)
                {
                    var status = app.ApplicationServices.GetRequiredService<VirtualPathStatus>();
                    var environment = app.ApplicationServices.GetRequiredService<IWebHostEnvironment>();
                    environment.WebRootFileProvider = new BackOfficeAssetFileProvider(environment.WebRootFileProvider,
                        app.ApplicationServices.GetRequiredService<IBackOfficePathGenerator>(), prefix, umbraco,
                        app.ApplicationServices.GetRequiredService<ILoggerFactory>().CreateLogger(Name), status);
                    app.UseWhen(context => !context.Request.PathBase.HasValue, branch => branch.UsePathBase(prefix));
                    // Kestrel also serves requests that arrive outside the virtual path. Count them for the health check.
                    app.Use((context, next) =>
                    {
                        if (!context.Request.PathBase.HasValue) status.RequestOutside(context.Request.Path);
                        return next(context);
                    });
                }
                app.UseUmbracoPathsUnderPathBase(UmbracoMajor.For(umbraco));
            },
            PostPipeline = app => app.UseMediaUnderPathBase(),
        }));
        services.Replace(ServiceDescriptor.Singleton<IBackOfficePathGenerator, BackOfficePaths>());
        // ManagementApiComposer registers the default URL assembler again after this composer runs. A collection
        // builder registers in UmbracoBuilder.Build(), after every composer, so the decorator wraps the final
        // registration and the package needs no reference to the optional management API.
        builder.WithCollectionBuilder<MediaUrlAssemblerRegistration>();
        Decorate<IPackageManifestService>(services, required: true,
            (provider, inner) => new VirtualPathImportMapService(inner, provider.GetRequiredService<IBackOfficePathGenerator>()));
        // The management API registers these providers, and a site can leave it out.
        Decorate<IInviteUriProvider>(services, required: false,
            (provider, inner) => SignInLinks.Invitations(inner, provider.GetRequiredService<IHostingEnvironment>()));
        Decorate<IForgotPasswordUriProvider>(services, required: false,
            (provider, inner) => SignInLinks.PasswordResets(inner, provider.GetRequiredService<IHostingEnvironment>()));
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
    /// The exact Umbraco releases validated for this package, recorded by the build.
    /// </summary>
    private static readonly Version[] SupportedReleases = typeof(ApplicationVirtualPathFixComposer).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().Single(attribute => attribute.Key == "UmbracoReleases").Value!
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Version.Parse).ToArray();

    /// <summary>The supported Umbraco releases, listed for startup warnings and the health check.</summary>
    internal static string SupportedUmbracoVersions => string.Join(", ", SupportedReleases.AsEnumerable());

    /// <summary>
    /// Returns whether the package was validated against the exact release <paramref name="umbraco"/> reports.
    /// Prereleases are not validated by testing their final release.
    /// </summary>
    internal static bool SupportsUmbraco(IUmbracoVersion umbraco)
    {
        var version = umbraco.SemanticVersion;
        return string.IsNullOrEmpty(version.Prerelease) && SupportedReleases.Contains(AssetPatcher.Release(version));
    }

    /// <summary>Logs a warning when the site runs an Umbraco release the package does not support.</summary>
    internal static void WarnOnUnsupportedUmbracoVersion(IServiceProvider services)
    {
        var umbraco = services.GetRequiredService<IUmbracoVersion>();
        if (SupportsUmbraco(umbraco))
            return;

        // NuGet's range can admit untested releases between its bounds. Direct references can also override the
        // dependency range with NU1608, so check the validated list at runtime.
        services.GetRequiredService<ILoggerFactory>().CreateLogger(Name).LogWarning(
            "{Package} supports Umbraco {Supported}, but this site runs Umbraco {Actual}. Some of its fixes patch the "
            + "backoffice scripts of those releases and may not apply. Install a package release that supports the site's "
            + "Umbraco version.",
            Name, SupportedUmbracoVersions, umbraco.SemanticVersion);
    }
}
