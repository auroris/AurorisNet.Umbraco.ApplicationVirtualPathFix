using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.IO;
using IHostingEnvironment = Umbraco.Cms.Core.Hosting.IHostingEnvironment;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix.Media;

/// <summary>
/// Serves media from Umbraco's media file system at the path requests carry under a path base.
/// </summary>
/// <remarks>
/// Umbraco mounts its media file system on the web root at <c>ToAbsolute("~/media")</c>, which includes the
/// application virtual path (<c>/portal/media</c>). Under a path base, requests arrive as <c>/media/...</c>, so a file
/// returns 404 unless it also exists in <c>wwwroot/media</c>. That affects media stored outside the web root (an
/// absolute <c>UmbracoMediaPhysicalRootPath</c>) and in providers such as Azure Blob Storage. Mounting the same file
/// system at the unprefixed path serves those files to the static-file handler and to ImageSharp, which both read the
/// web root's file provider when the pipeline is built.
/// </remarks>
internal static class MediaUnderPathBase
{
    /// <summary>
    /// Adds Umbraco's media file system to the web root at the media path without the virtual path, such as
    /// <c>/media</c>. Does nothing at the root, when the media file system has no file provider, or when the media path
    /// lies outside the virtual path.
    /// </summary>
    /// <remarks>
    /// Call it after Umbraco registers its media file provider and before the pipeline is built, as the post-pipeline
    /// step of an <c>UmbracoPipelineFilter</c> does.
    /// </remarks>
    public static void UseMediaUnderPathBase(this IApplicationBuilder app)
    {
        var services = app.ApplicationServices;
        var hosting = services.GetRequiredService<IHostingEnvironment>();
        if (VirtualPathHosting.Prefix(hosting) is not { } virtualPath)
            return;

        var media = services.GetRequiredService<MediaFileManager>();
        if (!media.FileSystem.TryCreateFileProvider(out IFileProvider? mediaFiles))
            return;

        var global = services.GetRequiredService<IOptions<GlobalSettings>>().Value;
        var mediaRequestPath = hosting.ToAbsolute(global.UmbracoMediaPath);
        if (!mediaRequestPath.StartsWith(virtualPath + "/", StringComparison.OrdinalIgnoreCase))
            return;

        var webHost = services.GetRequiredService<IWebHostEnvironment>();
        webHost.WebRootFileProvider = new CompositeFileProvider(
            webHost.WebRootFileProvider,
            new MountedFileProvider(mediaRequestPath[virtualPath.Length..], mediaFiles));
    }

    /// <summary>Exposes <paramref name="inner"/> under <paramref name="mountPath"/>, such as <c>/media</c>.</summary>
    private sealed class MountedFileProvider(PathString mountPath, IFileProvider inner) : IFileProvider
    {
        /// <summary>
        /// Returns the mounted file for a path below the mount path, and a missing file for any other path.
        /// </summary>
        public IFileInfo GetFileInfo(string subpath)
            => TryMap(subpath, out var path) ? inner.GetFileInfo(path) : new NotFoundFileInfo(subpath);

        /// <summary>
        /// Lists the mounted directory for a path below the mount path, and returns a missing directory for any other path.
        /// </summary>
        public IDirectoryContents GetDirectoryContents(string subpath)
            => TryMap(subpath, out var path) ? inner.GetDirectoryContents(path) : NotFoundDirectoryContents.Singleton;

        /// <summary>
        /// Watches the mounted files for a filter below the mount path, and returns a token that never changes otherwise.
        /// </summary>
        public IChangeToken Watch(string filter)
            => TryMap(filter, out var path) ? inner.Watch(path) : NullChangeToken.Singleton;

        /// <summary>
        /// Converts <paramref name="subpath"/> to a path in the mounted provider when it lies below the mount path.
        /// Returns <see langword="false"/> for the mount path itself and for paths outside it.
        /// </summary>
        private bool TryMap(string subpath, out string path)
        {
            var requested = new PathString(subpath.StartsWith('/') ? subpath : "/" + subpath);
            if (requested.StartsWithSegments(mountPath, out PathString remaining) && remaining.HasValue)
            {
                path = remaining.Value!;
                return true;
            }

            path = string.Empty;
            return false;
        }
    }
}
