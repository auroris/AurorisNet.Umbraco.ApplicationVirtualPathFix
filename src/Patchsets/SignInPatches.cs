namespace AurorisNet.Umbraco.ApplicationVirtualPathFix;

/// <summary>The sign-in page's script.</summary>
internal static class SignInPatches
{
    /// <summary>The entries for these files, in the order the patcher reads them.</summary>
    internal static readonly AssetPatch[] Patches =
    [
        // The sign-in page. Its script, the slim login app, builds Umbraco's URLs from the site root.
        new()
        {
            Path = "/umbraco/login/login.js",
            Edits =
            [
                // Requests to the sign-in API under "/umbraco/management/api/v1/security/back-office/".
                new()
                {
                    Origin = new()
                    {
                        File = "Umbraco.Web.UI.Login/src/contexts/auth.repository.ts",
                        Code = "new Request('/umbraco/management/api/v1/security/back-office/",
                    },
                    Find = """
                        new Request("/umbraco/
                        """,
                    Replace = """
                        new Request(@prefix + "/umbraco/
                        """,
                    Count = AssetEdit.Every,
                },
                // The server URL in the server context that the login app creates for Umbraco's other APIs.
                new()
                {
                    Origin = new()
                    {
                        File = "Umbraco.Web.UI.Login/src/controllers/slim-backoffice-initializer.ts",
                        Code = "const serverUrl = window.location.origin;",
                    },
                    Find = """
                        const @serverUrl = window.location.origin, @serverConnection = new @UmbServerConnection(@host, @serverUrl);
                        """,
                    Replace = """
                        const @serverUrl = window.location.origin + @prefix, @serverConnection = new @UmbServerConnection(@host, @serverUrl);
                        """,
                    Count = 1,
                },
                // The backoffice path in the same server context.
                new()
                {
                    Origin = new()
                    {
                        File = "Umbraco.Web.UI.Login/src/controllers/slim-backoffice-initializer.ts",
                        Code = "backofficePath: '/umbraco'",
                    },
                    Find = """
                        backofficePath: "/umbraco"
                        """,
                    Replace = """
                        backofficePath: @backOfficePath
                        """,
                    Count = 1,
                },
            ],
            // The login app calls Umbraco's public APIs through Umbraco's shared HTTP client, whose base URL it leaves
            // unset. The preamble imports that client from Umbraco's own module and sets its base URL to the site.
            Preamble = """
                import { umbHttpClient } from '@@umbraco-cms/backoffice/http-client'; umbHttpClient.setConfig({ baseUrl: window.location.origin + @prefix });
                """,
        },
    ];
}
