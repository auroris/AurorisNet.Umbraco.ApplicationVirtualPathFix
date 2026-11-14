using System.Security.Cryptography;
using System.Text;
using Umbraco.Cms.Core.Configuration;
using Umbraco.Cms.Web.Common.Hosting;
using IHostingEnvironment = Umbraco.Cms.Core.Hosting.IHostingEnvironment;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix;

/// <summary>Invalidate patched backoffice modules when either Umbraco or this package changes.</summary>
internal sealed class BackOfficePaths : IBackOfficePathGenerator
{
    public BackOfficePaths(IHostingEnvironment environment, IUmbracoVersion version)
    {
        var original = new UmbracoBackOfficePathGenerator(environment, version);
        BackOfficePath = original.BackOfficePath;
        BackOfficeVirtualDirectory = original.BackOfficeVirtualDirectory;
        BackOfficeCacheBustHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            original.BackOfficeCacheBustHash + BackOfficeAppUnderVirtualPath.BuildVersion)));
    }

    public string BackOfficePath { get; }
    public string BackOfficeVirtualDirectory { get; }
    public string BackOfficeCacheBustHash { get; }
    public string BackOfficeAssetsPath => BackOfficeVirtualDirectory + "/" + BackOfficeCacheBustHash;
}
