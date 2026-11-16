using System.Net;
using AurorisNet.Umbraco.ApplicationVirtualPathFix.Diagnostics;
using Umbraco.Cms.Core.HealthChecks;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests;

/// <summary>Checks the health check's results for working sites and for each kind of problem.</summary>
public sealed class HealthCheckTests
{
    /// <summary>
    /// Checks that a site at the root gets the version result and a note that it needs only the session restore fix.
    /// </summary>
    [Fact]
    public async Task A_site_at_the_root_needs_only_the_session_fix()
    {
        var results = await CheckAsync("/", "", new VirtualPathStatus(), UmbracoUnderTest.Release);
        Assert.Equal([StatusResultType.Success, StatusResultType.Info], results.Select(result => result.ResultType));
        Assert.Contains("at the root", results[1].Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Checks that a working site under a virtual path gets four successes: a supported version, a path base that matches
    /// in any case, every file patched, and no requests outside the virtual path.
    /// </summary>
    [Fact]
    public async Task A_working_site_under_a_virtual_path_reports_success()
    {
        var status = new VirtualPathStatus();
        status.AssetsPrepared(20);
        var results = await CheckAsync("/portal", "/Portal", status, UmbracoUnderTest.Release);
        Assert.All(results, result => Assert.Equal(StatusResultType.Success, result.ResultType));
        Assert.Equal(4, results.Length);
        Assert.Contains(results, result => result.Message.Contains("All 20 Umbraco files", StringComparison.Ordinal));
    }

    /// <summary>
    /// Checks that each problem gets its own result, in order: a version warning, a path base error that mentions
    /// <c>X-Forwarded-Prefix</c>, an error for the unapplied change, and a warning that counts the requests outside the
    /// virtual path and names the latest.
    /// </summary>
    [Theory]
    [InlineData("17.99.0")]
    [InlineData("19.0.0")]
    public async Task Version_path_base_patch_and_routing_problems_are_reported(string release)
    {
        var status = new VirtualPathStatus();
        status.AssetsPrepared(19);
        status.Problem("Umbraco's /umbraco/login/login.js no longer contains backofficePath, so that part of the fix is not applied.");
        status.RequestOutside("/healthz");
        status.RequestOutside("/umbraco");
        var results = await CheckAsync("/portal", "", status, Version.Parse(release));
        Assert.Equal([StatusResultType.Warning, StatusResultType.Error, StatusResultType.Error, StatusResultType.Warning],
            results.Select(result => result.ResultType));
        Assert.Contains($"supports Umbraco {ApplicationVirtualPathFixComposer.SupportedUmbracoVersions}", results[0].Message, StringComparison.Ordinal);
        Assert.Contains("X-Forwarded-Prefix", results[1].Message, StringComparison.Ordinal);
        Assert.Contains("login.js", results[2].Message, StringComparison.Ordinal);
        Assert.StartsWith("2 requests", results[3].Message, StringComparison.Ordinal);
        Assert.Contains("most recently /umbraco.", results[3].Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Checks that every message is HTML-encoded, because Umbraco renders health-check messages as HTML. A request
    /// outside the virtual path chooses its own path, so markup in that path, in the request's path base, in the
    /// configured prefix or in a recorded problem must reach the page as text. Decoded, each message reads as written.
    /// </summary>
    [Fact]
    public async Task Messages_are_encoded_as_HTML_text()
    {
        const string markup = "<img src=x onerror=alert(1)>";
        var status = new VirtualPathStatus();
        status.AssetsPrepared(19);
        status.Problem($"Umbraco's /umbraco/login/login.js no longer contains {markup}, so that part of the fix is not applied.");
        status.RequestOutside("/" + markup);
        var results = await CheckAsync("/a&b", "/" + markup, status, UmbracoUnderTest.Release);
        Assert.Equal(4, results.Length);
        Assert.All(results, result =>
        {
            Assert.DoesNotContain('<', result.Message);
            Assert.DoesNotContain('>', result.Message);
            // Every ampersand left in a message starts a character reference.
            Assert.DoesNotMatch("&(?!#x[0-9A-F]+;|amp;|lt;|gt;|quot;)", result.Message);
        });
        var text = results.Select(result => WebUtility.HtmlDecode(result.Message)).ToArray();
        // A path base is written as it appears in a URL, percent-encoded.
        Assert.Contains($"configured for /a&b, but this request arrived with the path base \"{new PathString("/" + markup)}\".", text[1],
            StringComparison.Ordinal);
        Assert.Equal($"Umbraco's /umbraco/login/login.js no longer contains {markup}, so that part of the fix is not applied.", text[2]);
        Assert.Contains($"outside /a&b since it started, most recently /{markup}. Route only /a&b to the site", text[3], StringComparison.Ordinal);
    }

    /// <summary>
    /// Runs the health check for a site configured with <paramref name="virtualPath"/>, a request whose path base is
    /// <paramref name="pathBase"/>, the recorded <paramref name="status"/> and Umbraco version
    /// <paramref name="umbraco"/>. Also checks that the check's ID is <see cref="VirtualPathHealthCheck.CheckId"/>.
    /// </summary>
    private static async Task<HealthCheckStatus[]> CheckAsync(string virtualPath, string pathBase, VirtualPathStatus status, Version umbraco)
    {
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext { Request = { PathBase = pathBase } } };
        var check = new VirtualPathHealthCheck(status, new TestHostingEnvironment(virtualPath), new TestUmbracoVersion(umbraco), http);
        Assert.Equal(Guid.Parse(VirtualPathHealthCheck.CheckId), check.Id);
        return (await check.GetStatusAsync()).ToArray();
    }
}
