using System.Security.Cryptography;
using System.Text;
using Umbraco.Cms.Core.Configuration;
using Umbraco.Cms.Web.Common.Hosting;
using IHostingEnvironment = Umbraco.Cms.Core.Hosting.IHostingEnvironment;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix;

/// <summary>
/// Generates Umbraco's backoffice paths with a cache-bust hash that changes with both Umbraco and this package, so
/// browsers fetch the patched modules again after either changes.
/// </summary>
internal sealed class BackOfficePaths : IBackOfficePathGenerator
{
    internal static readonly string BuildVersion = typeof(BackOfficePaths).Assembly.ManifestModule.ModuleVersionId.ToString("N");

    /// <summary>
    /// Copies Umbraco's backoffice paths and replaces the cache-bust hash with a SHA-256 hash of Umbraco's hash and
    /// <see cref="BuildVersion"/>.
    /// </summary>
    public BackOfficePaths(IHostingEnvironment environment, IUmbracoVersion version)
    {
        var original = new UmbracoBackOfficePathGenerator(environment, version);
        BackOfficePath = original.BackOfficePath;
        BackOfficeVirtualDirectory = original.BackOfficeVirtualDirectory;
        BackOfficeCacheBustHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            original.BackOfficeCacheBustHash + BuildVersion)));
    }

    public string BackOfficePath { get; }
    public string BackOfficeVirtualDirectory { get; }
    public string BackOfficeCacheBustHash { get; }
    public string BackOfficeAssetsPath => BackOfficeVirtualDirectory + "/" + BackOfficeCacheBustHash;
}
