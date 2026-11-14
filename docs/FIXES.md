# Fixes for Umbraco 18.3.1

The package keeps the backoffice's URLs inside the application virtual path
configured by `Umbraco:CMS:Hosting:ApplicationVirtualPath`. It registers through
the standard `.AddComposers()` startup and uses an Umbraco pipeline filter, placed
ahead of Umbraco's own filters, to apply the path base and its middleware.

## Corrected behavior

| Umbraco 18.3.1 behavior | Result with the package |
|---|---|
| The backoffice asset rewrite expects the virtual path in `Request.Path`, after the host has moved it into `Request.PathBase`. | Cache-busted asset requests reach Umbraco's script and stylesheet handlers. |
| Import maps contain root-absolute core and package URLs. | Root-absolute imports and scopes carry the application prefix, and Umbraco can add its asset hash. |
| The backoffice app builds API and OAuth URLs from the origin and `/umbraco`. | Under a virtual path the served app receives `server-url` and `backoffice-path` values that include the configured path. OAuth callbacks are registered with that prefix. Sites at the root keep Umbraco's defaults. |
| The slim login script uses root URLs for login, two-factor verification and its shared API client. | The served script uses the application prefix for those requests, including public package manifests and password resets. |
| Password-reset and invitation emails link to `/umbraco/login` at the host root. | The emailed links include the application path. |
| Media stored outside `wwwroot` (an absolute `UmbracoMediaPhysicalRootPath`, or a provider such as Azure Blob Storage) is mounted only at the prefixed path, which requests under a path base never match. | Media is served under the prefix from any media file system, and ImageSharp crops and resizes apply to it. |
| Basic authentication, the OpenAPI documents and the management API contract compare `Request.Path` with a path that includes the virtual path. With `RedirectToLoginPage`, every visitor is redirected in a loop. | Those requests see the path shape Umbraco expects, so the basic-authentication login page and the OpenAPI documents work under the prefix. |
| The logo in the header's system-information popover uses a root-absolute URL. | The logo resolves relative to the served module, retaining its prefix and asset hash. |
| The block grid editor loads its default layout stylesheet from `/umbraco/backoffice/css/...`, and resolves a custom layout stylesheet's site path against the host root. | Both stylesheets load under the prefix. |
| The media upload field and the preview prepend the server URL to media and page URLs that already include the virtual path. | Image previews, file links and the preview frame use the URL once. |
| The stylesheet of Umbraco's built-in Page Not Found, No Content, Maintenance and Upgrading pages loads its fonts and background from the root. | They load from beside the stylesheet, under the prefix. |
| Initial authentication attempts a token refresh before a fresh browser has received a refresh cookie. | The auth wrapper starts the normal sign-in flow directly when no session can exist. Existing sessions continue through Umbraco's token validation and renewal. |

The auth wrapper receives only a session hint in the backoffice HTML: whether the
refresh cookie was sent, or whether the browser reports that it may have withheld
it. Umbraco's cookie is `SameSite=Strict` by default, so it is left off navigations
that another site starts, such as a backoffice link in web mail
(`Sec-Fetch-Site: cross-site`); those pages still run Umbraco's normal session
restore. Browsers omit that header on plain-HTTP sites other than `localhost`, where
cookie presence alone decides. Token values stay in HTTP-only cookies. The HTML uses
`Cache-Control: no-store` so the hint describes the current request. Patched asset
URLs include the package build identifier so a deployment replaces cached modules.

The package buffers only the responses it may rewrite: browser navigations under
`/umbraco` outside the backoffice assets and the management and delivery APIs, the
slim login script, the backoffice modules listed above and the built-in pages'
stylesheet. Everything else, including SignalR's WebSocket, Server-Sent Events and
long-polling transports, static files and API calls, streams through untouched.

## Hosting and compatibility

Use this branch's package with **Umbraco 18.3.1 on .NET 10**. The package depends
on exactly `18.3.1` of `Umbraco.Cms.Web.Common` and `Umbraco.Cms.StaticAssets`, so
upgrading a site's Umbraco without the matching package release makes restore fail
with a version conflict (NU1107). Upgrade both together. A site that references
those two packages directly at another version gets only a NuGet warning (NU1608);
the package then logs a warning at startup, and another for each patch that no
longer finds its pattern.

Requests must reach the site with the prefix still in the path, or with the path
base already set: by IIS for a child application, or by
`UseForwardedHeaders` with `X-Forwarded-Prefix` behind a proxy that strips it. In
an IIS child application, set `ApplicationVirtualPath` to that application's path.
Kestrel uses the same setting to establish the path base. Route only the prefix to
the site; Kestrel otherwise also answers unprefixed URLs.

Under a virtual path, `server-url` comes from the request's scheme and host. Behind
a reverse proxy or load balancer, apply `X-Forwarded-Proto` and `X-Forwarded-Host`
with `UseForwardedHeaders` before `UseUmbraco`, or have the proxy preserve the
`Host` header. `UmbracoApplicationUrl` and `BackOfficeHost` alone do not change it.

Several Umbraco sites on one host, such as sibling IIS applications, share
`Path=/` cookies. Give each site its own
`Umbraco:CMS:Security:BackOfficeTokenCookie:SiteName` and `Umbraco:CMS:Security:AuthCookieName`.

Third-party plugins remain responsible for URLs they construct themselves. The
package prefixes their import-map entries, but does not rewrite arbitrary plugin
code. Umbraco's security-header health checks request the host root rather than
the application path; that is outside the package's scope.

## Browser validation

The Playwright suite builds disposable sites the way `dotnet new umbraco` does with
the starter kit: SQLite, Kestrel, unattended install and standard startup. It runs
each site through Chromium:

| Case | Hosting |
|---|---|
| Site root | Open `/` and `/umbraco`. |
| Kestrel subdirectory | Open `/SomeTestPath/...`; the fixture returns 404 for requests outside `/SomeTestPath`. |
| Kestrel subdirectory, external media | As above, with media stored in a folder outside `wwwroot`. |
| IIS child application | The fixture sets the path base before the package runs and removes WebSocket support, as IIS without the WebSocket Protocol feature does, with external media. |
| Basic authentication | Kestrel subdirectory with the basic-authentication login page enabled. |

The first four cases each:

- request the OpenAPI documents;
- visit every front-end page reachable from the home page and check that each image loads;
- request a password reset and follow the emailed link to the new-password form;
- sign in to the backoffice and check the dashboard and the header logo;
- reload and check that the session is restored through a token refresh;
- create a page, publish it and open it on the front end;
- upload an image, check every link to it in the backoffice, then open it and a resized version.

The basic-authentication case opens the site, signs in on Umbraco's login page and
checks that the home page loads.

Any browser console error, uncaught JavaScript exception, failed request or HTTP
error fails the test, and so does any error the site logs or any warning from the
package. The starter kit embeds third-party content (Google Fonts, jQuery, YouTube,
OpenStreetMap), so the suite needs internet access, and an outage there can fail
it. These checks run over HTTP on Kestrel. Live IIS deployment and HTTPS require
their own deployment validation. See [Developing and releasing](DEVELOPING.md) to
run the suite.
