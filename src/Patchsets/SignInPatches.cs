namespace AurorisNet.Umbraco.ApplicationVirtualPathFix;

/// <summary>The sign-in page's script and the backoffice's sign-in view.</summary>
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

        // In 17.5, 17.6 and 18.0 the login app calls the password-reset and invitation APIs through its own copy of the
        // generated API client instead, created without a base URL, so the requests go to the site root. Each entry
        // gives that copy the base URL the preamble above gives the shared client.
        new()
        {
            Path = "/umbraco/login/login.js",
            From = "17.5.0",
            Before = "17.7.0",
            Edits = OwnClientBaseUrl,
        },
        new()
        {
            Path = "/umbraco/login/login.js",
            From = "18.0.0",
            Before = "18.1.0",
            Edits = OwnClientBaseUrl,
        },

        // The backoffice's own sign-in view, shown after sign-out. It builds the addresses of its logos and background from
        // the server URL, which it reads from Umbraco's server context only after its first render, so that render
        // requests them from the site root. The edit starts the view with the site's URL, which the server context then
        // confirms.
        new()
        {
            Path = "/umbraco/backoffice/packages/core/umb-auth-view.element-????????.js",
            Edits =
            [
                new()
                {
                    Origin = new()
                    {
                        File = "src/packages/core/auth/components/umb-auth-view.element.ts",
                        Code = "private _serverUrl = '';",
                    },
                    Find = """
                        this._serverUrl = ""
                        """,
                    Replace = """
                        this._serverUrl = window.location.origin + @prefix
                        """,
                    Count = 1,
                },
            ],
        },
    ];

    /// <summary>The edit that creates the login app's own API client with the site's base URL.</summary>
    private static AssetEdit[] OwnClientBaseUrl =>
    [
        new()
        {
            Origin = new()
            {
                File = "Umbraco.Web.UI.Login/src/api/client.gen.ts",
                Code = "export const client = createClient(createConfig<ClientOptions2>());",
            },
            Find = """
                @client = @createClient(@createConfig())
                """,
            Replace = """
                @client = @createClient(@createConfig({ baseUrl: window.location.origin + @prefix }))
                """,
            Count = 1,
        },
    ];
}
