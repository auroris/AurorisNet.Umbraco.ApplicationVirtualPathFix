namespace AurorisNet.Umbraco.ApplicationVirtualPathFix;

/// <summary>Files Umbraco serves on the website rather than in the backoffice.</summary>
internal static class WebsitePatches
{
    /// <summary>The entries for these files, in the order the patcher reads them.</summary>
    internal static readonly AssetPatch[] Patches =
    [
        // The stylesheet of the page Umbraco shows while no content is published. Its fonts sit next to it in
        // "/umbraco/website/", so relative URLs find them under any virtual path.
        new()
        {
            Path = "/umbraco/website/nonodes.css",
            Edits =
            [
                new()
                {
                    Find = """
                        url(/umbraco/website/
                        """,
                    Replace = """
                        url(
                        """,
                    Count = AssetEdit.Every,
                },
            ],
        },

        // The badge Umbraco shows on pages opened in preview. Its "End preview" button sends a DELETE request to the
        // preview API at the site root. The badge element's path attribute holds the backoffice path, virtual path
        // included, and "/umbraco" stays the fallback when the attribute is missing. The script ships without a source
        // map, so the edit's origin names the script itself.
        new()
        {
            Path = "/umbraco/website/preview.js",
            Edits =
            [
                new()
                {
                    Origin = new()
                    {
                        File = "/umbraco/website/preview.js",
                        Code = "fetch(`/umbraco/management/api/v1/preview`",
                    },
                    Find = """
                        fetch(`/umbraco/management/api/v1/preview`
                        """,
                    Replace = """
                        fetch(`${this.getAttribute("path") ?? "/umbraco"}/management/api/v1/preview`
                        """,
                    Count = 1,
                },
            ],
        },
    ];
}
