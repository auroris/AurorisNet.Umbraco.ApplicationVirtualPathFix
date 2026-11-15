# Fixes and compatibility

The package keeps the backoffice's URLs inside the application virtual path set by
`Umbraco:CMS:Hosting:ApplicationVirtualPath`. It registers through `.AddComposers()`
and sets the path base in a pipeline filter that runs before Umbraco's middleware.

## What it fixes

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
| A fresh browser tries a token refresh before it has a refresh cookie. | It starts sign-in directly. Existing sessions renew as usual. |

At startup the package reads Umbraco's installed backoffice files, applies the entries
in [`src/Patchsets`](../src/Patchsets/) for the site's Umbraco release in memory, and
serves the results at the original URLs, with matching lengths and cache validators.
The files on disk stay unchanged, so restart the site after updating Umbraco. Each
entry names the file, the releases it applies to, the code it replaces and why;
`BackOfficeAssetPatches.cs` explains how to read them. A Tag Helper Component adds the
path base and a session hint to the backoffice page, and corrects basic-auth form
actions. Tokens stay in HTTP-only cookies.

## Hosting requirements

The package supports the Umbraco releases in the
[supported-versions table](../README.md#supported-versions), on .NET 10. Restore stops
with error NU1107 outside the package's dependency range; a site that references
`Umbraco.Cms.Web.Common` or `Umbraco.Cms.StaticAssets` directly gets warning NU1608
instead. On a release the table does not list, the startup log and the health check
report it, and the startup log names each patch that no longer applies.

- **The prefix must reach the site.** Requests arrive with the prefix in the path, or
  with the path base already set: by IIS for a child application, or by
  `UseForwardedHeaders` with `X-Forwarded-Prefix` behind a proxy that strips it. Route
  only the prefix to the site, because Kestrel also answers unprefixed URLs.
- **IIS child applications:** set `ApplicationVirtualPath` to the application's path.
- **Absolute URLs built on the server**, such as email links, follow Umbraco's usual
  rules. Behind a proxy or load balancer, set `UmbracoApplicationUrl`, or apply
  `X-Forwarded-Proto` and `X-Forwarded-Host` with `UseForwardedHeaders` before
  `UseUmbraco()`.
- **Response compression:** register `UseResponseCompression()` before `UseUmbraco()`.
  Patched files compress like any other.
- **Several Umbraco sites on one host** share `Path=/` cookies, so give each its own
  `Umbraco:CMS:Security:BackOfficeTokenCookie:SiteName` and
  `Umbraco:CMS:Security:AuthCookieName`.
- **Third-party plugins** build their own URLs. The package prefixes their import-map
  entries, and the plugins handle the rest.

## Health check

Settings → Health Check → Configuration → **Application virtual path** reports:

- whether the package supports the site's Umbraco release;
- the path base that requests arrive with;
- each change that could not be applied;
- requests that reached the site outside the virtual path.

The startup log carries the same warnings. Umbraco's own security-header checks request
the host root, so their results describe the host rather than the site.

## Testing

Every supported release passes the quick tests and five browser cases. The quick tests
check each patch against the installed Umbraco files and Umbraco's source maps, scan
Umbraco's scripts for URLs that could lose the virtual path, and test the middleware,
file provider, Tag Helper and health check. The browser cases run Chromium against
disposable starter-kit sites:

| Case | Site | Checks |
|---|---|---|
| `full` | HTTPS at `/SomeTestPath/Nested`, media outside `wwwroot`, debug mode off | The whole journey: front end, emails, error page, contact form, sign-in, health check, patched modules and caching, session restore, editors and themes, block grid, publishing and preview, media and crops, the Delivery API and sign-out |
| `iis` | HTTP at `/SomeTestPath`, path base set before the package, no WebSockets | Sign-in, session restore, server events over SSE, a slug that repeats the prefix, preview over SSE, the Delivery API and sign-out |
| `proxy` | HTTPS at `/SomeTestPath` behind a proxy that forwards prefix, host and scheme | Front end, sign-in, the public server URL, session restore, cross-site cookies and sign-out |
| `proxy-root` | The same proxy at the site root | The same checks, with every URL on the public host |
| `basic-auth` | HTTP at `/SomeTestPath` with basic authentication | The two-factor redirect, a failed and a successful login, and the front end |

A case fails on browser console errors, uncaught exceptions, failed requests, HTTP
errors, server errors, package warnings and any request outside the virtual path.
`SiteEndToEndTests.cs` lists the few Umbraco errors it ignores, which have nothing to do
with the virtual path. On 14 November 2026 the browser checks also passed against a
published IIS child application: Windows Server 2022, .NET 10.0.14, `/SomeTestPath`,
in-process hosting, external media storage, image signing and no WebSockets.
[Developing](DEVELOPING.md) shows how to run the tests.
