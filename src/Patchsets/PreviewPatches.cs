using AurorisNet.Umbraco.ApplicationVirtualPathFix.Assets;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix.Patchsets;

/// <summary>The backoffice's preview window.</summary>
internal static class PreviewPatches
{
    /// <summary>The entries for these files, in the order the patcher reads them.</summary>
    internal static readonly AssetPatch[] Patches =
    [
        // The preview window.
        new()
        {
            Path = "/umbraco/backoffice/packages/preview/index.js",
            Edits =
            [
                // The link to the published page. Content URLs include the virtual path, as media URLs do, so this edit
                // resolves them as the media previews do.
                new()
                {
                    Origin = new()
                    {
                        File = "src/packages/preview/context/preview.context.ts",
                        Code = "`${this.#serverUrl}${urlInfo.url}`",
                    },
                    Find = """
                        @urlInfo.url.startsWith("/") ? `${this.@serverUrl}${@urlInfo.url}` : @urlInfo.url
                        """,
                    Replace = """
                        @urlInfo.url.startsWith("/") ? new URL(@urlInfo.url, this.@serverUrl || location.origin).href : @urlInfo.url
                        """,
                    Count = 1,
                },
                // The address of the page being previewed. new URL replaces the last segment of its base, so a server
                // URL such as "https://example.com/portal" would lose "/portal". This edit adds a trailing slash first.
                new()
                {
                    Origin = new()
                    {
                        File = "src/packages/preview/context/preview.context.ts",
                        Code = "new URL(unique, host)",
                    },
                    Find = """
                        const @url = new URL(@unique, @host)
                        """,
                    Replace = """
                        const @url = new URL(@unique, @host.endsWith("/") ? @host : @host + "/")
                        """,
                    Count = 1,
                },
            ],
        },
    ];
}
