using System.Reflection;
using Umbraco.Cms.Core.Configuration;
using Umbraco.Cms.Core.HealthChecks;
using Umbraco.Cms.Core.Semver;
using IHostingEnvironment = Umbraco.Cms.Core.Hosting.IHostingEnvironment;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests;

public sealed class HealthCheckTests
{
    private static readonly Version Built = Version.Parse(typeof(ApplicationVirtualPathFixComposer).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().Single(attribute => attribute.Key == "UmbracoVersion").Value!);

    [Fact]
    public async Task A_site_at_the_root_needs_only_the_session_fix()
    {
        var results = await CheckAsync("/", "", new VirtualPathStatus(), Built);
        Assert.Equal([StatusResultType.Success, StatusResultType.Info], results.Select(result => result.ResultType));
        Assert.Contains("at the root", results[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_working_site_under_a_virtual_path_reports_success()
    {
        var status = new VirtualPathStatus();
        status.AssetsPrepared(20);
        var results = await CheckAsync("/portal", "/Portal", status, Built);
        Assert.All(results, result => Assert.Equal(StatusResultType.Success, result.ResultType));
        Assert.Equal(4, results.Length);
        Assert.Contains(results, result => result.Message.Contains("All 20 Umbraco files", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Version_path_base_patch_and_routing_problems_are_reported()
    {
        var status = new VirtualPathStatus();
        status.AssetsPrepared(19);
        status.Problem("Umbraco's /umbraco/login/login.js no longer contains backofficePath, so that part of the fix is not applied.");
        status.RequestOutside("/healthz");
        status.RequestOutside("/umbraco");
        var results = await CheckAsync("/portal", "", status, new Version(Built.Major, Built.Minor + 1, 0));
        Assert.Equal([StatusResultType.Warning, StatusResultType.Error, StatusResultType.Error, StatusResultType.Warning],
            results.Select(result => result.ResultType));
        Assert.Contains($"built for Umbraco {Built}", results[0].Message, StringComparison.Ordinal);
        Assert.Contains("X-Forwarded-Prefix", results[1].Message, StringComparison.Ordinal);
        Assert.Contains("login.js", results[2].Message, StringComparison.Ordinal);
        Assert.StartsWith("2 requests", results[3].Message, StringComparison.Ordinal);
        Assert.Contains("most recently /umbraco.", results[3].Message, StringComparison.Ordinal);
    }

    private static async Task<HealthCheckStatus[]> CheckAsync(string virtualPath, string pathBase, VirtualPathStatus status, Version umbraco)
    {
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext { Request = { PathBase = pathBase } } };
        var check = new VirtualPathHealthCheck(status, new Hosting(virtualPath), new TestVersion(umbraco), http);
        Assert.Equal(Guid.Parse(VirtualPathHealthCheck.CheckId), check.Id);
        return (await check.GetStatusAsync()).ToArray();
    }

    private sealed class TestVersion(Version version) : IUmbracoVersion
    {
        public Version Version => version;
        public string Comment => "";
        public Version AssemblyVersion => Version;
        public Version AssemblyFileVersion => Version;
        public SemVersion SemanticVersion => new(version.Major, version.Minor, version.Build);
    }

    private sealed class Hosting(string virtualPath) : IHostingEnvironment
    {
        public string ApplicationVirtualPath => virtualPath;
        public string ToAbsolute(string path) => throw new NotSupportedException();
        public string? SiteName => throw new NotSupportedException();
        public string ApplicationId => throw new NotSupportedException();
        public string ApplicationPhysicalPath => throw new NotSupportedException();
        public string LocalTempPath => throw new NotSupportedException();
        public bool IsDebugMode => throw new NotSupportedException();
        public bool IsHosted => throw new NotSupportedException();
        public Uri? ApplicationMainUrl => throw new NotSupportedException();
        public string MapPathWebRoot(string path) => throw new NotSupportedException();
        public string MapPathContentRoot(string path) => throw new NotSupportedException();
        public void EnsureApplicationMainUrl(Uri? currentApplicationUrl) => throw new NotSupportedException();
    }
}
