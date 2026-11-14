# Fixes for Umbraco 18.3.1

The package keeps the backoffice's URLs inside the application virtual path
configured by `Umbraco:CMS:Hosting:ApplicationVirtualPath`. It registers through
the standard `.AddComposers()` startup and uses an Umbraco pipeline filter to
apply its middleware before the backoffice pipeline.

## Corrected behavior

| Umbraco 18.3.1 behavior | Result with the package |
|---|---|
| The backoffice asset rewrite expects the virtual path in `Request.Path`, after the host has moved it into `Request.PathBase`. | Cache-busted asset requests reach Umbraco's script and stylesheet handlers. |
| Import maps contain root-absolute core and package URLs. | Root-absolute imports and scopes carry the application prefix, and Umbraco can add its asset hash. |
| The backoffice app builds API and OAuth URLs from the origin and `/umbraco`. | The served app receives `server-url` and `backoffice-path` values that include the configured path. OAuth callbacks are registered with that prefix. |
| The slim login script uses root URLs for login, two-factor verification and its shared API client. | The served script uses the application prefix for those requests, including public package manifests. |
| The logo in the header's system-information popover uses a root-absolute URL. | The logo resolves relative to the served module, retaining its prefix and asset hash. |
| Initial authentication attempts a token refresh before a fresh browser has received a refresh cookie. | The auth wrapper starts the normal sign-in flow directly when that cookie is absent. Existing sessions continue through Umbraco's token validation and renewal. |

The auth wrapper receives only cookie presence in the backoffice HTML. Token
values stay in HTTP-only cookies. The HTML uses `Cache-Control: no-store` so its
cookie-presence marker describes the current request. Patched asset URLs include
the package build identifier so a deployment replaces cached modules.

## Hosting and compatibility

Use this branch's package with **Umbraco 18.3.1 on .NET 10**. The NuGet dependency
requires exactly `18.3.1`. Changing Umbraco versions can change the JavaScript
patterns the package patches; select the corresponding package release when
upgrading the site.

Configure IIS bindings and reverse-proxy forwarding through the host's normal
deployment settings. In an IIS child application, set `ApplicationVirtualPath`
to that application's path. Kestrel uses the same setting to establish the path
base.

Third-party plugins remain responsible for URLs they construct themselves.
The package prefixes their import-map entries, but does not rewrite arbitrary
plugin code. WebSocket upgrades and the management, delivery and report endpoints
bypass the HTML patching middleware.

## Browser validation

The Playwright suite creates disposable plain Umbraco sites with SQLite and
Kestrel on Windows. It tests three cases:

| Case | Request behavior |
|---|---|
| Site root | Open `/umbraco` and complete native sign-in. |
| Kestrel subdirectory | Open `/SomeTestPath/umbraco`; the fixture returns 404 for requests outside `/SomeTestPath`. |
| Existing path base | The fixture sets the path base before the package runs, modeling an IIS child application's request shape. |

Every case checks the populated native dashboard, the logo in the
system-information popover, and a successful token refresh after reloading.
Browser console errors and uncaught JavaScript exceptions fail the test.
The fixture stops the browser and site afterwards.

These checks run over HTTP on Kestrel. Live IIS deployment and HTTPS require
their own deployment validation. See [Developing and releasing](DEVELOPING.md)
to run the suite.
