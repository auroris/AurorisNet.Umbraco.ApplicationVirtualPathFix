using System.Text;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Umbraco.Cms.Web.Common.Hosting;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix;

/// <summary>Prepares corrected assets once, before Umbraco's static-file middleware is built.</summary>
internal sealed class BackOfficeAssetFileProvider : IFileProvider
{
    private readonly IFileProvider _inner;
    private readonly Dictionary<string, IFileInfo> _patched = new(StringComparer.OrdinalIgnoreCase);

    public BackOfficeAssetFileProvider(IFileProvider inner, IBackOfficePathGenerator paths, string prefix, ILogger logger)
    {
        _inner = inner;
        // These are newly generated files. Static-file validators must describe them, not the
        // original bundles, whose timestamp and size may survive a change to a patch.
        var modified = DateTimeOffset.UtcNow;
        foreach (var module in BackOfficeAssetPatches.Modules)
        {
            var directory = module[..module.LastIndexOf('/')];
            var matches = inner.GetDirectoryContents(BackOfficeAssetPatches.AssetsRoot + directory)
                .Where(file => !file.IsDirectory && string.Equals(
                    BackOfficeAssetPatches.NormalizeModulePath(directory + "/" + file.Name), module, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matches.Length != 1)
            {
                logger.LogWarning("Expected one Umbraco bundle for {Module}, found {Count}. The application virtual path fix cannot patch it.",
                    module, matches.Length);
                continue;
            }

            Add(BackOfficeAssetPatches.AssetsRoot + directory + "/" + matches[0].Name, matches[0],
                source => BackOfficeAssetPatches.PatchModule(module, source, paths.BackOfficeAssetsPath, Missing));
        }

        Add("/umbraco/login/login.js", inner.GetFileInfo("/umbraco/login/login.js"),
            source => BackOfficeAssetPatches.PatchLoginScript(source, prefix, paths.BackOfficePath, Missing));
        Add("/umbraco/website/nonodes.css", inner.GetFileInfo("/umbraco/website/nonodes.css"),
            source => BackOfficeAssetPatches.PatchWebsiteStyles(source, Missing));

        void Add(string path, IFileInfo file, Func<string, string> patch)
        {
            if (!file.Exists)
            {
                logger.LogWarning("Umbraco asset {Path} is missing. The application virtual path fix cannot patch it.", path);
                return;
            }
            using var reader = new StreamReader(file.CreateReadStream());
            var bytes = Encoding.UTF8.GetBytes(patch(reader.ReadToEnd()));
            _patched.Add(path, new PatchedFile(file.Name, bytes, modified));
        }

        void Missing(string resource, string pattern)
            => logger.LogWarning(
                "Umbraco's {Resource} no longer contains {Pattern}, so that part of the application virtual path fix is not applied. "
                + "Install the AurorisNet.Umbraco.ApplicationVirtualPathFix release that matches this site's Umbraco version.",
                resource, pattern);
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
