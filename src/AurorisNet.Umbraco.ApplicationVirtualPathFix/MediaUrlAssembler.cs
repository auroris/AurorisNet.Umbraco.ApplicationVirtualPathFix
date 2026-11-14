using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Routing;
using IHostingEnvironment = Umbraco.Cms.Core.Hosting.IHostingEnvironment;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix;

/// <summary>Preserves the prefix already present in media URLs, including signed thumbnails.</summary>
internal sealed class MediaUrlAssembler(IUrlAssembler inner, IHostingEnvironment hosting, GlobalSettings settings) : IUrlAssembler
{
    private readonly string? _prefix = VirtualPathHosting.Prefix(hosting);
    private readonly string _mediaPath = hosting.ToAbsolute(settings.UmbracoMediaPath).TrimEnd('/') + "/";

    public Uri AssembleUrl(string path, Uri current, UrlMode mode)
    {
        var result = inner.AssembleUrl(path, current, mode);
        // The thumbnail factory passes an already-prefixed, signed media path to the default
        // assembler, which prepends the application path again. Correct only that duplication;
        // leave external URLs, content routes and custom assemblers' correct results intact.
        if (_prefix is null || !path.StartsWith(_mediaPath, StringComparison.OrdinalIgnoreCase))
            return result;

        var resultPath = result.GetSafeAbsolutePath();
        return resultPath.StartsWith(_prefix + _mediaPath, StringComparison.OrdinalIgnoreCase)
            ? result.Rewrite(resultPath[_prefix.Length..])
            : result;
    }
}
