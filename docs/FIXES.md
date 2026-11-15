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
| The backoffice app builds API and OAuth URLs from the origin and `/umbraco`. | Under a virtual path the app's server URL is the browser's origin plus the configured path, and its backoffice path includes the configured path too. OAuth callbacks are registered with that prefix. Sites at the root keep Umbraco's defaults. |
| A backoffice bookmark uses different casing for the application prefix, which the server accepts but the SPA router does not. | Backoffice HTML navigations redirect to the configured prefix casing, retaining the query string. |
| The slim login script uses root URLs for login, two-factor verification and its shared API client. | The served script uses the application prefix for those requests, including public package manifests and password resets. |
| Password-reset and invitation emails link to `/umbraco/login` at the host root. | The emailed links include the application path. |
| Media stored outside `wwwroot` (an absolute `UmbracoMediaPhysicalRootPath`, or a provider such as Azure Blob Storage) is mounted only at the prefixed path, which requests under a path base never match. | Media is served under the prefix from any media file system, and ImageSharp crops and resizes apply to it. |
| The thumbnail URL assembler adds the prefix again to signed media URLs, invalidating their image-processing signatures. | Already-prefixed media paths retain one prefix, their original query string and any URL fragment. |
| Basic authentication, the OpenAPI documents and the management API contract compare `Request.Path` with a path that includes the virtual path. With `RedirectToLoginPage`, every visitor is redirected in a loop. | Those requests see the path shape Umbraco expects, so the basic-authentication login page and the OpenAPI documents work under the prefix. |
| The logo in the header's system-information popover uses a root-absolute URL. | The logo loads under the configured application prefix and asset hash. |
| The error screen and installer/upgrader layout load their illustration from the host root. | Both load the illustration from the configured backoffice asset root. |
| The backoffice stylesheet loads Monaco's codicon font from the host root. | The font loads relative to the stylesheet under the application prefix, including icons in the editor's Find widget. |
| The block grid editor loads its default layout stylesheet from `/umbraco/backoffice/css/...`, and resolves a custom layout stylesheet's site path against the host root. | Both stylesheets load under the prefix. |
| The block catalogue and the block type cards in a block editor's settings resolve thumbnail site paths against the host root. | Block thumbnails load under the prefix. |
| The media upload field and the preview prepend the server URL to media and page URLs that already include the virtual path. | Image previews, file links and the preview frame use the URL once. |
| The website's preview badge ends preview with a request to the host root, so preview stays on. | End preview reaches the management API under the prefix and reloads the page without preview. |
| Tiptap's default stylesheet, Monaco's worker scripts and the dark/high-contrast theme stylesheets use root URLs. | They load from the configured backoffice asset root, including the application prefix and asset hash. |
| A rich text stylesheet saved with the stylesheet root, such as `/css/rte.css`, no longer matches the server's root, which includes the virtual path, so the root is added twice. | Stylesheets saved with or without the root load from it once. |
| Basic-authentication routes already include the application prefix, so MVC adds it twice to form actions and redirects. | Login forms and two-factor redirects contain the prefix once; `Url.Content` still sees the original path base. |
| The stylesheet of Umbraco's built-in Page Not Found, No Content, Maintenance and Upgrading pages loads its fonts and background from the root. | They load from beside the stylesheet, under the prefix. |
| Initial authentication attempts a token refresh before a fresh browser has received a refresh cookie. | The auth wrapper starts the normal sign-in flow directly when no session can exist. Existing sessions continue through Umbraco's token validation and renewal. |

Under a virtual path the backoffice HTML names its path base, and the auth wrapper
sets the app's server URL to the origin the browser loaded the page from plus that
path. Umbraco uses the browser's origin the same way at the root. Apart from that, the
auth wrapper receives only a session hint in the backoffice HTML: whether the
refresh cookie was sent, or whether the browser reports that it may have withheld
it. Umbraco's cookie is `SameSite=Strict` by default, so it is left off navigations
that another site starts, such as a backoffice link in web mail
(`Sec-Fetch-Site: cross-site`); those pages still run Umbraco's normal session
restore. Browsers omit that header on plain-HTTP sites other than `localhost`, where
cookie presence alone decides. Token values stay in HTTP-only cookies. The HTML uses
`Cache-Control: no-store` so the hint describes the current request. Patched asset
URLs include the package build identifier so a deployment replaces cached modules.

Known bundles are matched by directory and filename, with `-????????` representing
an eight-character build hash where needed. Fixed filenames remain exact matches.
Asset URL corrections share one rewrite of quoted `/umbraco/backoffice/` paths;
worker filenames retain the hashes supplied by Umbraco. Changes to JavaScript
behavior remain explicit patches. Their placeholders stand for minified variable
names, so a rebuild that only renames variables still matches. Each patch applies
only when it finds the expected number of matches: exactly one for a behavior
change, and one or more for a URL rewrite. Compatibility tests check every registered
bundle and search pattern against the installed Umbraco assets, including bundles
the browser scenarios do not load. They also check, through Umbraco's source maps,
that each change hits the line of Umbraco's TypeScript it names, and that every
patched script still parses. To review every change, read
[`BackOfficeAssetPatches.cs`](../src/AurorisNet.Umbraco.ApplicationVirtualPathFix/BackOfficeAssetPatches.cs):
it lists each patched file with the code it finds, the code that replaces it, and a
note on why.

At startup, a file provider reads those installed assets and prepares patched
copies in memory. Umbraco's static-file middleware serves them at their original
URLs, with lengths and cache validators for the patched content. A patched file's
`Last-Modified` is the later of the Umbraco file's time and the time of the commit
the package was built from, so its validators agree across restarts and servers.
Patches preserve generated line numbers, but replacements shift columns on edited
lines. Umbraco's original source maps are therefore approximate on those lines in
the browser's developer tools. The original files stay unchanged. Restart
the site after changing an upstream asset or patch.

A Tag Helper Component applies the HTML corrections while Razor renders the body
of Umbraco's backoffice and two basic-auth views. It adds the app's URL attributes
and session hint and corrects basic-auth form actions. Other views are untouched.
The package does not replace response streams or alter `Accept-Encoding`.
The login view's existing `asp-append-version` versions the patched script bytes;
no additional query parameter is needed.

## Hosting and compatibility

Use this branch's package with **Umbraco 18.3.1 on .NET 10**. The package depends
on exactly `18.3.1` of `Umbraco.Cms.Web.Common` and `Umbraco.Cms.StaticAssets`, so
upgrading a site's Umbraco without the matching package release makes restore fail
with a version conflict (NU1107). Upgrade both together. A site that references
those two packages directly at another version gets only a NuGet warning (NU1608);
the package then logs a warning at startup. Asset preparation also logs warnings
for missing or ambiguous bundles, and for each patch that it skips because its pattern
no longer matches the expected number of times.

Requests must reach the site with the prefix still in the path, or with the path
base already set: by IIS for a child application, or by
`UseForwardedHeaders` with `X-Forwarded-Prefix` behind a proxy that strips it. In
an IIS child application, set `ApplicationVirtualPath` to that application's path.
Kestrel uses the same setting to establish the path base. Route only the prefix to
the site; Kestrel otherwise also answers unprefixed URLs.

An existing host-supplied path base is preserved, including when a content slug
repeats the application prefix. Patched static files and rendered HTML use normal
ASP.NET Core response compression. Register `UseResponseCompression()` before the
static-file and MVC handlers, normally before `UseUmbraco()`. The package has no
special compression-ordering requirement.

The backoffice's server URL comes from the browser, as at the root, so the backoffice
does not depend on the `Host` header or on forwarded headers. Absolute URLs that
Umbraco builds on the server, such as the links in emails, follow Umbraco's usual
rules: behind a reverse proxy or load balancer, set `UmbracoApplicationUrl`, or apply
`X-Forwarded-Proto` and `X-Forwarded-Host` with `UseForwardedHeaders` before `UseUmbraco`.

The package adds an **Application virtual path** check to the backoffice, under
Settings → Health Check → Configuration. The check reports:

- whether the package matches the site's Umbraco version;
- the path base that requests arrive with;
- every change that could not be applied;
- requests that reached the site outside the virtual path, which Kestrel also answers.

The startup log carries the same warnings.

Several Umbraco sites on one host, such as sibling IIS applications, share
`Path=/` cookies. Give each site its own
`Umbraco:CMS:Security:BackOfficeTokenCookie:SiteName` and `Umbraco:CMS:Security:AuthCookieName`.

Third-party plugins remain responsible for URLs they construct themselves. The
package prefixes their import-map entries, but does not rewrite arbitrary plugin
code. Umbraco's security-header health checks request the host root rather than
the application path; that is outside the package's scope.

## Browser validation

The Playwright suite builds disposable sites the way `dotnet new umbraco` does with
the starter kit: SQLite, Kestrel, unattended install and standard startup. It uses
five sites: one comprehensive journey through the affected features and four
focused hosting checks. Each site has its own database because these settings
affect startup, authentication and URL generation.

| Case | Configuration and coverage |
|---|---|
| `full` | Kestrel at `/SomeTestPath/Nested`, HTTPS, media outside `wwwroot` and `Hosting:Debug=false`. Runs the journey below, including secure cookies and cross-site session restoration. |
| `iis` | Plain HTTP at `/SomeTestPath`, with the path base supplied before the package and WebSockets disabled. Checks OpenAPI, sign-in, session restore, server-event SSE, a content slug repeating the prefix, draft preview over SSE, Delivery API URLs and sign-out. |
| `proxy` | HTTPS at `/SomeTestPath`, forwarded prefix, host and scheme. Checks the home page and images, sign-in, the public server URL, session restore, cross-site cookies and sign-out. |
| `proxy-root` | The same focused proxy checks at the site root, guarding against the internal backend host leaking into URLs. |
| `basic-auth` | Plain HTTP subdirectory with basic authentication enabled. Checks the two-factor redirect, an invalid password followed by a successful login, and the front end. |

The `full` case:

- request the OpenAPI documents;
- visit the home page and one nested blog page, checking their paths and images;
- request a password reset and follow the emailed link to the new-password form;
- load Umbraco's built-in error page with its fonts and background, and submit the contact form to check that its action and redirect keep the virtual path;
- sign in to the backoffice and check the dashboard and the header logo;
- run the health check as the backoffice dashboard does, and expect every result to succeed;
- open the backoffice using a differently cased prefix, import every patched JavaScript module, and check real static-file 304 responses;
- reload and check that the session is restored through a token refresh;
- invite an editor and follow the emailed link to the password form;
- edit rich text with configured stylesheets saved with and without the stylesheet root, ask the JSON worker to validate invalid code, open the editor's Find widget and check its icon font, and load dark and high-contrast themes;
- open a block grid with its default stylesheet, persist a custom stylesheet and block thumbnails on its data type, and check both stylesheet URLs, the thumbnails in the block catalogue and the data type's block cards, and the setting's default-stylesheet link;
- create a page, publish it and open it on the front end;
- preview unpublished changes, save a second draft, verify that the open preview updates while the public page remains unchanged, then end preview from the website's preview badge;
- upload an image, check every link to it in the backoffice, then open it and a signed resized version returned by the imaging API;
- select that image through the home page's media picker, publish it, and verify the rendered original and signed crop;
- check the Delivery API's route and media URLs;
- sign out, verify the prefixed logout redirect, and reload to check the session stays signed out.

The other cases reuse only the steps relevant to their hosting risks; they do not
repeat the nested-page, email, editor, stylesheet or media-upload journey.
File-provider tests check the prepared bytes, lengths, HEAD requests, conditional
responses and byte ranges through the real static-file middleware. Component tests
use the real body Tag Helper to check view scoping, URL attributes, basic-auth forms,
session hints and custom cookie settings. The full browser journey also verifies
that the login script's version matches the served bytes. Middleware tests cover
normal response compression, request-path restoration and startup version warnings;
file-provider tests verify that missing patches are reported during startup only.
URL tests cover prefix boundaries, signed queries and fragments, import maps and
OAuth callbacks without starting a site. A source scan reads Umbraco's TypeScript from
the shipped source maps and requires every URL construction that could lose the virtual
path to be patched or reviewed. The IIS-style case observes an actual
EventSource open event for both the backoffice server-events hub and the preview
hub. Each site has image signing enabled with a fresh test key.

Browser console errors, uncaught JavaScript exceptions, failed requests and HTTP
errors fail the test, as do server errors and package warnings. One expected response
is recorded separately: without WebSockets, Umbraco revokes the session before
SignalR sends its SSE close message, causing a 401 on the server-event hub's POST
during sign-out. This also occurs on a root site. The starter kit's external
dependencies receive deterministic local responses; unexpected external requests
fail. Browser routing disables its HTTP
cache, so conditional-response behavior is tested separately with an HTTP client.
The automated suite runs over HTTP and HTTPS on Kestrel. On 14 November 2026,
the browser checks also passed against a published IIS child application on
Windows Server 2022 and .NET 10.0.14: `/SomeTestPath`, in-process hosting (`w3wp`),
external media storage, image signing enabled and WebSockets disabled. This included
the repeated content slug, updating draft preview, signed image resizing and a
separate basic-authentication run. That was a deployment check; the automated
IIS-style case still uses Kestrel.

See [Developing and releasing](DEVELOPING.md) to run the suite and view the saved
traces, screenshots and server logs.
