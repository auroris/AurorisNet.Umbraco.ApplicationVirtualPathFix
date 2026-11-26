using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AurorisNet.Umbraco.ApplicationVirtualPathFix.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix.BackOffice;

/// <summary>
/// Scopes Umbraco's browser-side auth broadcast messages, refresh locks, popup targets and temporary redirect state
/// per installation so applications mounted at different paths on one origin can sign in independently.
/// </summary>
internal sealed class BackOfficeAuthFiles : IFileProvider
{
    private readonly Dictionary<string, IFileInfo> _files = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>SHA-256 hash of the scoped module contents, folded into the backoffice cache-bust hash.</summary>
    public string ContentHash { get; }

    /// <summary>
    /// Reads Umbraco's backoffice auth and utils modules from <paramref name="environment"/> and rewrites their
    /// origin-scoped keys with <see cref="InstallationCookies.Suffix"/>.
    /// </summary>
    public BackOfficeAuthFiles(IWebHostEnvironment environment, InstallationCookies cookies, ILogger<BackOfficeAuthFiles>? logger = null)
        : this(environment.WebRootFileProvider, cookies, logger)
    {
    }

    internal BackOfficeAuthFiles(IFileProvider files, InstallationCookies cookies, ILogger? logger = null)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var suffix = cookies.Suffix;
        Add("auth", source =>
        {
            foreach (var key in new[] { "umb:auth", "umb:token-refresh", "umb:pkce" })
                source = ReplaceChecked(source, JsonSerializer.Serialize(key), JsonSerializer.Serialize(key + suffix), "auth");

            // External providers can supply their own popup name. Keep reserved browser targets such as _blank.
            return ReplaceChecked(source, "window.open(", "openInstallationAuthPopup(", "auth") + $$"""

                function openInstallationAuthPopup(url, target, features) {
                  const name = target && !target.startsWith("_") ? target + {{JsonSerializer.Serialize(suffix)}} : target;
                  return window.open(url, name, features);
                }
                """;
        });
        Add("utils", source => ReplaceChecked(source, "\"umb:auth:redirect\"", JsonSerializer.Serialize("umb:auth:redirect" + suffix), "utils"));
        ContentHash = Convert.ToHexStringLower(hash.GetHashAndReset());

        string ReplaceChecked(string input, string token, string replacement, string module)
        {
            var updated = input.Replace(token, replacement, StringComparison.Ordinal);
            if (ReferenceEquals(updated, input) || updated == input)
                logger?.LogWarning(
                    "BackOfficeAuthFiles did not find expected token {Token} in Umbraco {Module} module; backoffice session isolation may be incomplete.",
                    token, module);
            return updated;
        }

        void Add(string module, Func<string, string> update)
        {
            var path = "/umbraco/backoffice/packages/core/" + module + "/index.js";
            var original = files.GetFileInfo(path);
            if (!original.Exists) return;
            using var reader = new StreamReader(original.CreateReadStream());
            var bytes = Encoding.UTF8.GetBytes(update(reader.ReadToEnd()));
            _files.Add(path, new ModuleFile(original, bytes));
            hash.AppendData(bytes);
        }
    }

    public IFileInfo GetFileInfo(string subpath) =>
        _files.TryGetValue("/" + subpath.TrimStart('/'), out var file) ? file : new NotFoundFileInfo(subpath);

    public IDirectoryContents GetDirectoryContents(string subpath) => NotFoundDirectoryContents.Singleton;
    public IChangeToken Watch(string filter) => NullChangeToken.Singleton;

    private sealed class ModuleFile(IFileInfo original, byte[] bytes) : IFileInfo
    {
        public string Name => original.Name;
        public long Length => bytes.LongLength;
        public DateTimeOffset LastModified => original.LastModified;
        public bool Exists => true;
        public bool IsDirectory => false;
        public string? PhysicalPath => null;
        public Stream CreateReadStream() => new MemoryStream(bytes, writable: false);
    }
}
