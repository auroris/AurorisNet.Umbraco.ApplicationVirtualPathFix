# What the package fixes

Each row names a URL that Umbraco 17 or 18 builds without the application virtual path set
in `Umbraco:CMS:Hosting:ApplicationVirtualPath`, and the result once the package corrects
it. The [README](../README.md) covers installation, hosting requirements and the health
check.

| Umbraco behavior | With the package |
|---|---|
| The asset rewrite looks for the virtual path in `Request.Path`, after the host has moved it to `Request.PathBase`. | Cache-busted script and stylesheet requests reach Umbraco's handlers. |
| Import maps contain root-absolute URLs. | Imports and scopes carry the prefix. |
| The backoffice builds API and OAuth URLs from the origin and `/umbraco`. | It uses the origin plus the configured path, and OAuth callbacks include the prefix. |
| A bookmark with a differently cased prefix reaches the server, but the backoffice router rejects it. | Backoffice page requests redirect to the configured casing and keep the query string. |
| The login page sends its requests to the host root. | Login, two-factor, password-reset and invitation requests use the prefix. |
| Password-reset and invitation emails link to `/umbraco/login` at the host root. | The links include the path. |
| Media outside `wwwroot`, or in a provider such as Azure Blob Storage, is mounted at the prefixed path, which requests under a path base never match. | Media is served under the prefix, with ImageSharp crops and resizes. |
| The thumbnail URL assembler prefixes signed media URLs a second time, which breaks their signatures. | Prefixed media URLs keep one prefix, their query string and their fragment. |
| Basic authentication, the API documents and the management API contract compare `Request.Path` with a path that includes the virtual path, so basic authentication redirects in a loop. | They see the path Umbraco expects. The API documents load under `/umbraco/swagger` on Umbraco 17 and `/umbraco/openapi` on Umbraco 18. |
| Basic-auth routes already contain the prefix, so MVC adds it again to form actions and redirects. | Forms and redirects contain the prefix once. |
| Backoffice images, fonts, stylesheets and workers load from the host root: the header logo, the sign-in view's logos after sign-out, the error and installer illustration, Monaco's icon font and workers, Tiptap's stylesheet, and the dark and high-contrast themes. | They load from the backoffice asset root under the prefix. |
| The block grid's default and custom layout stylesheets and block thumbnails resolve against the host root. | They load under the prefix. |
| The media upload field and preview add the server URL to URLs that already include the path. | Image previews, file links and the preview frame use the URL once. |
| The website's preview badge ends preview at the host root, so preview stays on. | End preview reaches the management API under the prefix. |
| A rich text stylesheet saved with the stylesheet root, such as `/css/rte.css`, gets the root twice. | Stylesheets load once, saved with or without the root. |
| Umbraco's Page Not Found, No Content, Maintenance and Upgrading pages load fonts and backgrounds from the host root. | They load under the prefix. |
| A fresh browser tries a token refresh before it has a refresh cookie. This row applies at the root too. | It starts sign-in directly. Existing sessions renew as usual. |

## How the fixes apply

At startup the package reads Umbraco's installed backoffice files, applies the entries in
[`src/Patchsets`](../src/Patchsets/) for the site's Umbraco release in memory, and serves
the results at the original URLs, with matching lengths and cache validators. Each entry
names the file, the releases it applies to, the code it replaces and why;
`BackOfficeAssetPatches.cs` explains how to read them. A pipeline filter that runs before
Umbraco's middleware sets the path base. A Tag Helper Component adds the path base and a
session hint to the backoffice page and corrects basic-auth form actions. Decorators on
Umbraco's import map, media URL assembler and sign-in link providers add the prefix to the
URLs they build. Tokens stay in HTTP-only cookies.
