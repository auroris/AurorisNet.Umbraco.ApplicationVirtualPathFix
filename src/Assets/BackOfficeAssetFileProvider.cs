using System.Globalization;
using System.Reflection;
using System.Text;
using AurorisNet.Umbraco.ApplicationVirtualPathFix.Diagnostics;
using AurorisNet.Umbraco.ApplicationVirtualPathFix.Patchsets;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Umbraco.Cms.Web.Common.Hosting;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix.Assets;

/// <summary>
/// Serves patched copies of Umbraco's backoffice files from the web root. The copies are prepared once, before Umbraco's
/// static-file middleware is built.
/// </summary>
internal sealed class BackOfficeAssetFileProvider : IFileProvider
{
    /// <summary>
    /// The time of the commit the package was built from, or <see cref="DateTimeOffset.MinValue"/> when the build
    /// could not read it.
    /// </summary>
    internal static readonly DateTimeOffset SourceTimestamp = DateTimeOffset.TryParse(
        typeof(BackOfficeAssetFileProvider).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "SourceTimestamp")?.Value,
        CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp) ? timestamp : DateTimeOffset.MinValue;

    private readonly IFileProvider _inner;
    private readonly Dictionary<string, IFileInfo> _patched = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Patches each file that the entries of <see cref="BackOfficeAssetPatches.Patches"/> for the site's Umbraco release
    /// name, for the site's paths, and keeps the results in memory. A file named by several entries gets them all, in
    /// list order.
    /// </summary>
    /// <param name="inner">The web root's file provider, which supplies Umbraco's files and serves every other file.</param>
    /// <param name="paths">The site's backoffice paths, which the patched files refer to.</param>
    /// <param name="prefix">The application virtual path, such as <c>/portal</c>.</param>
    /// <param name="umbraco">The site's Umbraco release, such as 18.2.1, which selects the entries that apply.</param>
    /// <param name="logger">Receives a warning for each file or change that could not be patched.</param>
    /// <param name="status">Records the number of patched files and each problem for the health check, when supplied.</param>
    public BackOfficeAssetFileProvider(IFileProvider inner, IBackOfficePathGenerator paths, string prefix, Version umbraco, ILogger logger,
        VirtualPathStatus? status = null)
    {
        _inner = inner;
        var context = new PatchContext(prefix, paths.BackOfficePath, paths.BackOfficeAssetsPath);
        var files = new Dictionary<string, (IFileInfo File, List<AssetPatch> Patches)>(StringComparer.OrdinalIgnoreCase);
        foreach (var patch in AssetPatcher.For(umbraco))
        {
            var found = AssetPatcher.Locate(inner, patch);
            if (found.Length != 1)
            {
                logger.LogWarning("Expected one Umbraco file for {Path}, found {Count}. The application virtual path fix cannot patch it.",
                    patch.Path, found.Length);
                status?.Problem($"Expected one Umbraco file for {patch.Path}, found {found.Length}, so it is not patched.");
                continue;
            }

            var (path, file) = found[0];
            if (!files.TryGetValue(path, out var entry))
                files[path] = entry = (file, []);
            entry.Patches.Add(patch);
        }

        foreach (var (path, (file, patches)) in files)
        {
            // Physical files open unbuffered, so read them in 64 KB blocks. With StreamReader's 1 KB default, reading
            // took most of the preparation time.
            using var reader = new StreamReader(file.CreateReadStream(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1 << 16);
            var bytes = Encoding.UTF8.GetBytes(AssetPatcher.Patch(patches, reader.ReadToEnd(), context, Unapplied));
            // Cache validators must change with the bytes and agree across restarts and servers. New bytes come only
            // from a newer Umbraco file or package, so use the later of their times. A different application path
            // changes the URL instead.
            _patched.Add(path, new PatchedFile(file.Name, bytes, file.LastModified > SourceTimestamp ? file.LastModified : SourceTimestamp));
        }
        status?.AssetsPrepared(_patched.Count);

        // Logs a change that could not be applied and records it for the health check.
        void Unapplied(string resource, string reason)
        {
            logger.LogWarning(
                "Umbraco's {Resource} {Reason}, so that part of the application virtual path fix is not applied. "
                + "Install an AurorisNet.Umbraco.ApplicationVirtualPathFix release that supports this site's Umbraco version.",
                resource, reason);
            status?.Problem($"Umbraco's {resource} {reason}, so that part of the fix is not applied.");
        }
    }

    /// <summary>
    /// Returns the patched copy of the file at <paramref name="subpath"/> when one was prepared, and otherwise the web
    /// root's file.
    /// </summary>
    public IFileInfo GetFileInfo(string subpath)
        => _patched.TryGetValue("/" + subpath.TrimStart('/'), out var file) ? file : _inner.GetFileInfo(subpath);

    /// <summary>Returns the web root's directory listing, which describes the original files.</summary>
    public IDirectoryContents GetDirectoryContents(string subpath) => _inner.GetDirectoryContents(subpath);

    /// <summary>
    /// Watches the web root's files. The patched copies stay as prepared at startup, so a changed Umbraco file takes
    /// effect after a restart.
    /// </summary>
    public IChangeToken Watch(string filter) => _inner.Watch(filter);

    /// <summary>A patched copy of an Umbraco file, held in memory.</summary>
    private sealed class PatchedFile(string name, byte[] bytes, DateTimeOffset modified) : IFileInfo
    {
        public bool Exists => true;
        public bool IsDirectory => false;
        public string Name => name;
        public long Length => bytes.LongLength;
        public DateTimeOffset LastModified => modified;
        // The static-file middleware sends a file with a physical path straight from disk, which would serve the
        // original bytes. Null makes it read CreateReadStream.
        public string? PhysicalPath => null;

        /// <summary>Opens a read-only stream over the patched bytes.</summary>
        public Stream CreateReadStream() => new MemoryStream(bytes, writable: false);
    }
}
