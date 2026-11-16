using AurorisNet.Umbraco.ApplicationVirtualPathFix.Assets;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix.Patchsets;

/// <summary>The previews of media in the image cropper and the upload field.</summary>
internal static class MediaPatches
{
    /// <summary>The entries for these files, in the order the patcher reads them.</summary>
    internal static readonly AssetPatch[] Patches =
    [
        // Media previews. The server URL includes the virtual path, and so do Umbraco's media URLs, such as
        // "/portal/media/photo.jpg", so joining them repeats it. new URL resolves a path from the site root against the
        // origin alone, which keeps a single virtual path. The fallback to location.origin covers an empty server URL.
        new()
        {
            Path = "/umbraco/backoffice/packages/media/input-upload-field.element-????????.js",
            Edits =
            [
                // The image cropper's preview.
                new()
                {
                    Origin = new()
                    {
                        File = "src/packages/media/media/components/input-image-cropper/image-cropper-field.element.ts",
                        Code = "`${this._serverUrl}${this.src}`",
                    },
                    Find = """
                        this.src.startsWith("/") ? `${this._serverUrl}${this.src}` : this.src
                        """,
                    Replace = """
                        this.src.startsWith("/") ? new URL(this.src, this._serverUrl || location.origin).href : this.src
                        """,
                    Count = 1,
                },
                // The upload field's preview.
                new()
                {
                    Origin = new()
                    {
                        File = "src/packages/media/media/components/input-upload-field/input-upload-field.element.ts",
                        Code = "this._serverUrl + src",
                    },
                    Find = """
                        @fullUrl = @isBlob ? @src : this._serverUrl + @src
                        """,
                    Replace = """
                        @fullUrl = @isBlob ? @src : new URL(@src, this._serverUrl || location.origin).href
                        """,
                    Count = 1,
                },
            ],
        },
    ];
}
