using System.Globalization;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Umbraco.Cms.Web.Common.Hosting;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix;

/// <summary>Prepares corrected assets once, before Umbraco's static-file middleware is built.</summary>
internal sealed class BackOfficeAssetFileProvider : IFileProvider
{
    /// <summary>When the package's source was committed, or <see cref="DateTimeOffset.MinValue"/> for a build outside Git.</summary>
    internal static readonly DateTimeOffset SourceTimestamp = DateTimeOffset.TryParse(
        typeof(BackOfficeAssetFileProvider).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "SourceTimestamp")?.Value,
        CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp) ? timestamp : DateTimeOffset.MinValue;

    private readonly IFileProvider _inner;
    private readonly Dictionary<string, IFileInfo> _patched = new(StringComparer.OrdinalIgnoreCase);

    public BackOfficeAssetFileProvider(IFileProvider inner, IBackOfficePathGenerator paths, string prefix, ILogger logger,
        VirtualPathStatus? status = null)
    {
        _inner = inner;
        var context = new BackOfficeAssetPatches.PatchContext(prefix, paths.BackOfficePath, paths.BackOfficeAssetsPath);
        foreach (var patch in BackOfficeAssetPatches.Patches)
        {
            var files = BackOfficeAssetPatches.Locate(inner, patch);
            if (files.Length != 1)
            {
                logger.LogWarning("Expected one Umbraco file for {Path}, found {Count}. The application virtual path fix cannot patch it.",
                    patch.Path, files.Length);
                status?.Problem($"Expected one Umbraco file for {patch.Path}, found {files.Length}, so it is not patched.");
                continue;
            }

            var (path, file) = files[0];
            // Physical files open unbuffered, so read in large blocks: StreamReader's 1 KB default made
            // reading the patched bundles most of their preparation time.
            using var reader = new StreamReader(file.CreateReadStream(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1 << 16);
            var bytes = Encoding.UTF8.GetBytes(BackOfficeAssetPatches.Patch(patch, reader.ReadToEnd(), context, Unapplied));
            // Cache validators must change with the bytes, yet agree across restarts and servers. New bytes come
            // from a newer Umbraco file or package, and a different application path changes the URL instead.
            _patched.Add(path, new PatchedFile(file.Name, bytes, file.LastModified > SourceTimestamp ? file.LastModified : SourceTimestamp));
        }
        status?.AssetsPrepared(_patched.Count);

        void Unapplied(string resource, string reason)
        {
            logger.LogWarning(
                "Umbraco's {Resource} {Reason}, so that part of the application virtual path fix is not applied. "
                + "Install the AurorisNet.Umbraco.ApplicationVirtualPathFix release that matches this site's Umbraco version.",
                resource, reason);
            status?.Problem($"Umbraco's {resource} {reason}, so that part of the fix is not applied.");
        }
    }

    public IFileInfo GetFileInfo(string subpath)
        => _patched.TryGetValue("/" + subpath.TrimStart('/'), out var file) ? file : _inner.GetFileInfo(subpath);

    public IDirectoryContents GetDirectoryContents(string subpath) => _inner.GetDirectoryContents(subpath);

    public IChangeToken Watch(string filter) => _inner.Watch(filter);

    private sealed class PatchedFile(string name, byte[] bytes, DateTimeOffset modified) : IFileInfo
    {
        public bool Exists => true;
        public bool IsDirectory => false;
        public string Name => name;
        public long Length => bytes.LongLength;
        public DateTimeOffset LastModified => modified;
        // A physical path would let SendFile bypass CreateReadStream and send the original bytes.
        public string? PhysicalPath => null;
        public Stream CreateReadStream() => new MemoryStream(bytes, writable: false);
    }
}
