using Umbraco.Cms.Core.Configuration;
using Umbraco.Cms.Core.Semver;
using Umbraco.Cms.Web.Common.Hosting;
using IHostingEnvironment = Umbraco.Cms.Core.Hosting.IHostingEnvironment;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests;

/// <summary>Backoffice paths for a site under <paramref name="prefix"/>, with the cache-bust hash <c>hash</c>.</summary>
internal sealed class TestBackOfficePaths(string prefix) : IBackOfficePathGenerator
{
    public string BackOfficePath => prefix + "/umbraco";
    public string BackOfficeVirtualDirectory => BackOfficePath + "/backoffice";
    public string BackOfficeCacheBustHash => "hash";
    public string BackOfficeAssetsPath => BackOfficeVirtualDirectory + "/" + BackOfficeCacheBustHash;
}

/// <summary>Reports <paramref name="version"/> with an optional <paramref name="prerelease"/> as the site's Umbraco version.</summary>
internal sealed class TestUmbracoVersion(Version version, string prerelease = "") : IUmbracoVersion
{
    public Version Version => version;
    public string Comment => "";
    public Version AssemblyVersion => Version;
    public Version AssemblyFileVersion => Version;
    public SemVersion SemanticVersion => new(version.Major, version.Minor, version.Build, prerelease);
}

/// <summary>
/// A hosting environment for a site under <paramref name="prefix"/>. Members the tests don't use throw
/// <see cref="NotSupportedException"/>.
/// </summary>
internal sealed class TestHostingEnvironment(string prefix) : IHostingEnvironment
{
    public string ApplicationVirtualPath => prefix;
    /// <summary>
    /// Resolves an application-relative path such as <c>~/media</c> under the prefix, as Umbraco does, and returns other
    /// paths unchanged.
    /// </summary>
    public string ToAbsolute(string virtualPath) => virtualPath.StartsWith("~/", StringComparison.Ordinal)
        ? prefix.TrimEnd('/') + virtualPath[1..] : virtualPath;
    public string? SiteName => throw new NotSupportedException();
    public string ApplicationId => throw new NotSupportedException();
    public string ApplicationPhysicalPath => throw new NotSupportedException();
    public string LocalTempPath => throw new NotSupportedException();
    public bool IsDebugMode => throw new NotSupportedException();
    public bool IsHosted => throw new NotSupportedException();
    public Uri ApplicationMainUrl => throw new NotSupportedException();
    /// <summary>Throws <see cref="NotSupportedException"/>, because the tests map no physical paths.</summary>
    public string MapPathWebRoot(string path) => throw new NotSupportedException();
    /// <summary>Throws <see cref="NotSupportedException"/>, because the tests map no physical paths.</summary>
    public string MapPathContentRoot(string path) => throw new NotSupportedException();
    /// <summary>Throws <see cref="NotSupportedException"/>, because the tests need no application URL.</summary>
    public void EnsureApplicationMainUrl(Uri? currentApplicationUrl) => throw new NotSupportedException();
}
