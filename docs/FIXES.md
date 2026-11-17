# What the package fixes

Each row names a URL that Umbraco 17 or 18 builds without the application virtual path set
in `Umbraco:CMS:Hosting:ApplicationVirtualPath`, and the result with the package. Use it to
match a request that fails in the browser with the fix that covers it. The
[README](../README.md) covers installation, hosting requirements and the health check.

| Umbraco behavior | With the package |
|---|---|
| The rewrite rule for cache-busted backoffice assets looks for the virtual path in `Request.Path`, after the host has moved it to `Request.PathBase`. | Script and stylesheet requests reach Umbraco's handlers. |
| The import map contains root-absolute URLs. | Imports and scopes carry the prefix. |
| The backoffice builds API and OAuth URLs from the origin and `/umbraco`. | It uses the origin plus the configured path, and the OAuth callbacks include the prefix. |
| A bookmark with a differently cased prefix reaches the server, but the backoffice router rejects it. | Page requests under `/umbraco` redirect to the configured casing and keep the query string. |
| The sign-in page sends its requests to the host root. | Sign-in, two-factor, password-reset and invitation requests use the prefix. |
| Password-reset and invitation emails link to `/umbraco/login` at the host root. | The links include the path. |
| Media outside `wwwroot`, or in a provider such as Azure Blob Storage, is mounted at the prefixed path, which requests under a path base never match. | Media is served under the prefix, to the static-file handler and to ImageSharp. |
| The URL assembler adds the prefix a second time to media URLs that already have it, such as signed thumbnail URLs, which breaks their signatures. | Media URLs keep one prefix, their query string and their fragment. |
| Basic authentication, the API documents and the management API contract compare `Request.Path` with a path that includes the virtual path. With `RedirectToLoginPage`, basic authentication redirects in a loop. | They see the path Umbraco expects. The API documents load under `/umbraco/swagger` on Umbraco 17 and `/umbraco/openapi` on Umbraco 18. |
| Basic-auth routes already contain the prefix, so MVC adds it again to form actions and redirects. | Form actions and redirects contain the prefix once. |
| Backoffice images, fonts, stylesheets and workers load from the host root: the header logo, the sign-in view's logos after sign-out, the error and installer illustrations, Monaco's icon font and workers, Tiptap's content stylesheet, and the dark and high-contrast themes. | They load from the backoffice assets path under the prefix. |
| The block grid's default and custom layout stylesheets and block thumbnails resolve against the host root. | They load under the prefix. |
| The image cropper and the upload field add the server URL to media URLs that already include the path. | Their previews use the path once. |
| The preview window adds the server URL to content URLs that already include the path, and drops the path from the address of the page it previews. | The link to the published page and the preview frame use the path once. |
| The website's preview badge ends preview at the host root, so preview stays on. | **End preview** reaches the management API under the prefix. |
| A rich text stylesheet saved with the stylesheet root, such as `/css/rte.css`, gets the root twice. | Stylesheets load once, saved with or without the root. |
| The stylesheet of Umbraco's built-in website pages, such as the page for a missing URL and the page shown while no content is published, loads its fonts and backgrounds from the host root. | They load under the prefix. |
| A fresh browser tries a token refresh before it has a refresh cookie. This row applies at the root too. | It starts sign-in directly. Existing sessions renew as usual. |

## How the fixes apply

**Patched files.** At startup the package reads Umbraco's installed backoffice files, applies
the entries in [`src/Patchsets`](../src/Patchsets/) for the site's Umbraco release in memory,
and serves the results at the original URLs. Each entry names the file, the releases it
applies to, the code it replaces and why.
[`BackOfficeAssetPatches.cs`](../src/Patchsets/BackOfficeAssetPatches.cs) explains how to
read them. A change that does not match the site's files is skipped, and the startup log and
the health check report it.

**Caching.** A patched file's `Last-Modified` time is the later of Umbraco's file time and
the time of the commit the package was built from. The backoffice's cache-busting hash
combines Umbraco's hash with the package build, so browsers fetch the patched files again
after either changes.

**Path base.** A pipeline filter that runs before Umbraco's middleware moves the prefix from
the request path into the path base, unless the host has set the path base already. For
cache-busted assets, basic-auth routes, the API documents and the management API contract,
it then puts the prefix back onto `Request.Path`, where Umbraco compares it.

**Backoffice page.** A Tag Helper Component adds `data-restore-session` to the `umb-app`
element when the browser may hold a session: the refresh cookie arrived, or the cookie is
`SameSite=Strict` and another site started the navigation. Under a virtual path it also adds
the path base and the backoffice path, and corrects the form actions on the basic-auth
pages. The backoffice page is sent with `Cache-Control: no-store`. The tokens stay in
HTTP-only cookies.

**Services.** Wrappers around Umbraco's import map, media URL assembler and sign-in link
providers add the prefix to the URLs they build, and the import map points
`@umbraco-cms/backoffice/auth` at the package's module, which skips the initial token
refresh on a page without `data-restore-session`. The package also adds the prefix to the
three `Umbraco:CMS:Security:AuthorizeCallback*PathName` settings.
