using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Routing;
using IHostingEnvironment = Umbraco.Cms.Core.Hosting.IHostingEnvironment;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix;

/// <summary>
/// Corrects media URLs that Umbraco's URL assembler prefixes twice, such as signed thumbnail URLs, so each keeps a single
/// virtual path.
/// </summary>
internal sealed class MediaUrlAssembler(IUrlAssembler inner, IHostingEnvironment hosting, GlobalSettings settings) : IUrlAssembler
{
    private readonly string? _prefix = VirtualPathHosting.Prefix(hosting);
    private readonly string _mediaPath = hosting.ToAbsolute(settings.UmbracoMediaPath).TrimEnd('/') + "/";

    /// <summary>
    /// Assembles the URL with Umbraco's assembler, then removes the duplicate virtual path when <paramref name="path"/>
    /// is a media path that already carried it. The query, including a signature, and the fragment stay intact.
    /// </summary>
    public Uri AssembleUrl(string path, Uri current, UrlMode mode)
    {
        var result = inner.AssembleUrl(path, current, mode);
        // The thumbnail factory passes the default assembler a signed media path that already has the prefix, and the
        // assembler adds it again. Correct only that duplication, so external URLs, content routes and other
        // assemblers' correct results pass through unchanged.
        if (_prefix is null || !path.StartsWith(_mediaPath, StringComparison.OrdinalIgnoreCase))
            return result;

        var resultPath = result.GetSafeAbsolutePath();
        if (!resultPath.StartsWith(_prefix + _mediaPath, StringComparison.OrdinalIgnoreCase))
            return result;

        // Build the URL here because Umbraco's Uri.Rewrite helper drops the fragment. Keep the fragment and the signed
        // query.
        return result.IsAbsoluteUri
            ? new Uri(result.GetLeftPart(UriPartial.Authority) + resultPath[_prefix.Length..] + result.Query + result.Fragment)
            : new Uri(result.OriginalString[_prefix.Length..], UriKind.Relative);
    }
}
