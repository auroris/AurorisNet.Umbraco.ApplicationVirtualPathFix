using Umbraco.Cms.Core.Configuration;
using Umbraco.Cms.Core.HealthChecks;
using IHostingEnvironment = Umbraco.Cms.Core.Hosting.IHostingEnvironment;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix;

/// <summary>
/// Shows in the backoffice what the startup log reports, with how requests reach the site: the Umbraco version,
/// the path base requests arrive with, changes that could not be applied, and requests outside the virtual path.
/// </summary>
[HealthCheck(CheckId, "Application virtual path", Description = "Checks the fixes for hosting the site under an application virtual path.",
    Group = "Configuration")]
internal sealed class VirtualPathHealthCheck(VirtualPathStatus status, IHostingEnvironment hosting, IUmbracoVersion umbraco, IHttpContextAccessor http)
    : HealthCheck
{
    internal const string CheckId = "6c5d7a2e-91f4-4b3a-8e0d-3f2b9c6a1e47";

    public override Task<IEnumerable<HealthCheckStatus>> GetStatusAsync() => Task.FromResult<IEnumerable<HealthCheckStatus>>(Check().ToArray());

    public override HealthCheckStatus ExecuteAction(HealthCheckAction action)
        => throw new InvalidOperationException("The application virtual path check has no actions.");

    private IEnumerable<HealthCheckStatus> Check()
    {
        yield return ApplicationVirtualPathFixComposer.MismatchedUmbracoVersion(umbraco) is { } built
            ? Status(StatusResultType.Warning, $"The package was built for Umbraco {built}, but the site runs Umbraco {umbraco.SemanticVersion}. "
                + "Install the package release for the site's Umbraco version.")
            : Status(StatusResultType.Success, $"The package matches the site's Umbraco version, {umbraco.SemanticVersion}.");

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

    private static HealthCheckStatus Status(StatusResultType type, string message) => new(message) { ResultType = type };
}
