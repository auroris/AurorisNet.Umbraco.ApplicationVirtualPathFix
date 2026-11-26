namespace AurorisNet.Umbraco.ApplicationVirtualPathFix.Hosting;

/// <summary>
/// Options for <see cref="InstallationCookies"/>, allowing a host to add its own fixed-name cookies to the
/// installation-scoped cookie map.
/// </summary>
public sealed class InstallationCookiesOptions
{
    /// <summary>
    /// Additional fixed cookie names to isolate per installation alongside Umbraco's built-in fixed cookie names.
    /// </summary>
    public ISet<string> AdditionalCookieNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}
