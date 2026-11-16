using System.Text.Encodings.Web;
using Umbraco.Cms.Core.Configuration;
using Umbraco.Cms.Core.HealthChecks;
using IHostingEnvironment = Umbraco.Cms.Core.Hosting.IHostingEnvironment;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix.Diagnostics;

/// <summary>
/// Reports the fix's status under Settings → Health Check: whether the package supports the Umbraco version, whether
/// requests arrive with the configured path base, whether every patch applied, and whether requests reached the site
/// outside the virtual path.
/// </summary>
[HealthCheck(CheckId, "Application virtual path", Description = "Checks the fixes for hosting the site under an application virtual path.",
    Group = "Configuration")]
internal sealed class VirtualPathHealthCheck(VirtualPathStatus status, IHostingEnvironment hosting, IUmbracoVersion umbraco, IHttpContextAccessor http)
    : HealthCheck
{
    internal const string CheckId = "6c5d7a2e-91f4-4b3a-8e0d-3f2b9c6a1e47";

    /// <summary>
    /// Returns the check's results. The path base result describes the request that runs the check.
    /// </summary>
    public override Task<IEnumerable<HealthCheckStatus>> GetStatusAsync() => Task.FromResult<IEnumerable<HealthCheckStatus>>(Check().ToArray());

    /// <summary>Throws <see cref="InvalidOperationException"/>, because the check offers no actions.</summary>
    public override HealthCheckStatus ExecuteAction(HealthCheckAction action)
        => throw new InvalidOperationException("The application virtual path check has no actions.");

    /// <summary>
    /// Produces the results in order: the Umbraco version, then at the root a note that only the session restore fix
    /// applies. Under a virtual path it continues with the request's path base, the patched files or each problem, and
    /// the requests outside the virtual path. Each message is written as plain text, which <see cref="Status"/> encodes.
    /// </summary>
    private IEnumerable<HealthCheckStatus> Check()
    {
        yield return ApplicationVirtualPathFixComposer.SupportsUmbraco(umbraco)
            ? Status(StatusResultType.Success, $"The package supports the site's Umbraco version, {umbraco.SemanticVersion}.")
            : Status(StatusResultType.Warning, $"The package supports Umbraco {ApplicationVirtualPathFixComposer.SupportedUmbracoVersions}, "
                + $"but the site runs Umbraco {umbraco.SemanticVersion}. Install a package release that supports the site's Umbraco version.");

        if (VirtualPathHosting.Prefix(hosting) is not { } prefix)
        {
            yield return Status(StatusResultType.Info, "The site runs at the root, so it needs only the session restore fix.");
            yield break;
        }

        var pathBase = http.HttpContext?.Request.PathBase ?? PathString.Empty;
        yield return pathBase.Equals(prefix, StringComparison.OrdinalIgnoreCase)
            ? Status(StatusResultType.Success, $"The site runs under {prefix}, and this request arrived with that path base.")
            : Status(StatusResultType.Error, $"The site is configured for {prefix}, but this request arrived with the path base \"{pathBase}\". "
                + "Requests must reach the site with the prefix in their path, or with the path base set by IIS or X-Forwarded-Prefix.");

        if (status.PatchedFiles is not { } patched)
            yield return Status(StatusResultType.Warning, "The backoffice files have not been patched yet; that happens when Umbraco's pipeline starts.");
        else if (status.Problems.Count == 0)
            yield return Status(StatusResultType.Success, $"All {patched} Umbraco files that the fix corrects were patched.");
        foreach (var problem in status.Problems)
            yield return Status(StatusResultType.Error, problem);

        yield return status.RequestsOutside is var outside && outside == 0
            ? Status(StatusResultType.Success, $"No requests have reached the site outside {prefix} since it started.")
            : Status(StatusResultType.Warning, $"{outside} requests have reached the site outside {prefix} since it started, most recently "
                + $"{status.LastRequestOutside}. Route only {prefix} to the site, unless these are load balancer probes.");
    }

    /// <summary>
    /// Creates a result of <paramref name="type"/> from <paramref name="message"/>, which is plain text, and HTML-encodes
    /// it.
    /// </summary>
    /// <remarks>
    /// Umbraco renders a result's message as HTML, in the backoffice and in notification emails. A message can quote a
    /// request: the path of a request outside the virtual path comes from whoever sent it, signed in or not. Encoding
    /// every message here keeps such text from reaching the page as markup.
    /// </remarks>
    private static HealthCheckStatus Status(StatusResultType type, string message)
        => new(HtmlEncoder.Default.Encode(message)) { ResultType = type };
}
