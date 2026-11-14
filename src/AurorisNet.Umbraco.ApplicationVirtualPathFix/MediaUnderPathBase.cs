using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.IO;
using IHostingEnvironment = Umbraco.Cms.Core.Hosting.IHostingEnvironment;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix;

/// <summary>
/// Serves media from Umbraco's media file system at the path requests carry under a path base.
/// </summary>
/// <remarks>
/// Umbraco mounts its media file system on the web root at <c>ToAbsolute("~/media")</c>, which
/// includes the application virtual path (<c>/portal/media</c>). Under a path base the request path is
/// <c>/media/...</c>, so files that do not also exist in <c>wwwroot/media</c> return 404: media stored
/// outside the web root (an absolute <c>UmbracoMediaPhysicalRootPath</c>) or in a provider such as
/// Azure Blob Storage. Mounting the same file system at the unprefixed path as well fixes both the
/// static-file handler and ImageSharp, which read the web root's file provider when the pipeline is
/// built. Call it after Umbraco has registered its media file provider and before the pipeline is
/// built, which is what an <c>UmbracoPipelineFilter</c>'s post-pipeline step does.
/// </remarks>
internal static class MediaUnderPathBase
{
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
        public IFileInfo GetFileInfo(string subpath)
            => TryMap(subpath, out var path) ? inner.GetFileInfo(path) : new NotFoundFileInfo(subpath);

        public IDirectoryContents GetDirectoryContents(string subpath)
            => TryMap(subpath, out var path) ? inner.GetDirectoryContents(path) : NotFoundDirectoryContents.Singleton;

        public IChangeToken Watch(string filter)
            => TryMap(filter, out var path) ? inner.Watch(path) : NullChangeToken.Singleton;

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
