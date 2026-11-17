# Umbraco Application Virtual Path Fix

`AurorisNet.Umbraco.ApplicationVirtualPathFix` lets an Umbraco 17 or 18 site run under an
application virtual path, such as an IIS application at `/SomeTestPath`.

![MIT license](https://raw.githubusercontent.com/auroris/AurorisNet.Build/main/badges/license-mit.svg)
![.NET 10](https://raw.githubusercontent.com/auroris/AurorisNet.Build/main/badges/dotnet-10.svg)
![Umbraco 17 and 18](https://raw.githubusercontent.com/auroris/AurorisNet.Build/main/badges/umbraco-17-18.svg)

Umbraco builds many backoffice, media, preview and sign-in URLs from the host root. The
package adds the configured path to each of them, so the backoffice at
`/SomeTestPath/umbraco` signs in, loads its scripts and stylesheets, shows media and
previews, and renews sessions under the path. It does this in three ways:

- It patches Umbraco's backoffice scripts and stylesheets in memory at startup and serves the
  patched copies at the original URLs.
- It adds middleware that moves the path into the request's path base, and restores the path
  for the Umbraco features that expect it there.
- It wraps Umbraco's import map, media URL and sign-in link services, so the URLs they build
  include the path.

A site at the root gains one fix: a fresh browser starts sign-in directly, and Umbraco's
initial token refresh runs only when a session may exist.
[What the package fixes](https://github.com/auroris/AurorisNet.Umbraco.ApplicationVirtualPathFix/blob/main/docs/FIXES.md)
lists every URL the package corrects.

## Install

1. Reference the package with your site's Umbraco major version, so NuGet installs the newest
   package for it:

   ```xml
   <PackageReference Include="AurorisNet.Umbraco.ApplicationVirtualPathFix" Version="18.*" />
   ```

   On Umbraco 17, use `Version="17.*"`. With central package management, a floating version
   needs `CentralPackageFloatingVersionsEnabled` set to `true`; otherwise give an exact
   package version.
2. Set the site's application path in `appsettings.json`. For an IIS child application, use
   the application's path.

   ```json
   {
     "Umbraco": {
       "CMS": {
         "Hosting": {
           "ApplicationVirtualPath": "/SomeTestPath"
         }
       }
     }
   }
   ```

3. Start the site and open `/SomeTestPath/umbraco`.

The package registers itself through an Umbraco composer, so the site needs no code. The
**Application virtual path** health check confirms that requests arrive under the path and
that every patch applied.

## Hosting requirements

- **Requests reach the site with the prefix.** Either the request path starts with the
  prefix, which the package moves into the path base, or the host has set the path base
  already: IIS for a child application, or `UseForwardedHeaders` with `X-Forwarded-Prefix`
  behind a proxy that strips the prefix. Route only the prefix to the site, because Kestrel
  also answers requests outside it.
- **Absolute URLs built on the server**, such as the links in emails, start from Umbraco's
  application URL. Behind a proxy that changes the host or scheme, apply `X-Forwarded-Host`
  and `X-Forwarded-Proto` with `UseForwardedHeaders` before `UseUmbraco()`, or set
  `Umbraco:CMS:WebRouting:UmbracoApplicationUrl`.
- **Response compression.** Register `UseResponseCompression()` before `UseUmbraco()`.
  Patched files compress like any other file.
- **Several Umbraco sites on one host** need different cookie names, because a browser sends
  a host's `Path=/` cookies to every application on it. Give each site its own
  `Umbraco:CMS:Security:BackOfficeTokenCookie:SiteName` and
  `Umbraco:CMS:Security:AuthCookieName`.
- **Third-party backoffice plugins.** The package adds the prefix to their import map
  entries. Each plugin builds its other URLs itself.
- **Restart the site after updating Umbraco.** The package patches Umbraco's backoffice files
  in memory at startup and leaves the files on disk unchanged.

When the site's own startup code needs the path, `VirtualPathHosting.Prefix(builder.Configuration)`
returns it with one leading slash, such as `/SomeTestPath`, or `null` for a site at the root.

The browser tests cover five set-ups: HTTPS under a nested path with media outside
`wwwroot`, a path base set by the host as IIS does, a reverse proxy that forwards the prefix,
the same proxy at the root, and basic authentication.

## Health check

**Settings → Health Check → Configuration → Application virtual path** reports each part of
the fix, and the startup log carries the same warnings and errors. At the root, the check
reports the Umbraco release and notes that only the session restore fix applies.

| Check | Message when something is wrong | What to do |
|---|---|---|
| Umbraco release | The package supports Umbraco *list*, but the site runs Umbraco *release*. | Install a package version that lists the site's release. |
| Path base | The site is configured for `/SomeTestPath`, but this request arrived with the path base "". | Send requests with the prefix in their path, or set the path base through IIS or `X-Forwarded-Prefix`. |
| Patched files | Expected one Umbraco file for *path*, found *n*, so it is not patched. | The site's Umbraco files differ from the tested releases. Install a package version that lists the site's release. |
| Patched files | Umbraco's *file* *reason*, so that part of the fix is not applied. | As above. |
| Patched files | The backoffice files have not been patched yet. | The files are patched when Umbraco's pipeline starts. Run the check again once the site has started. |
| Requests outside the path | *n* requests have reached the site outside `/SomeTestPath` since it started, most recently *path*. | Route only the prefix to the site. Load balancer probes at the root also count. |

In the browser, a missing fix shows in the developer tools' Network tab as a request without
the prefix, such as `/umbraco/management/api/v1/...` in place of
`/SomeTestPath/umbraco/management/api/v1/...`. Find the URL's row in
[What the package fixes](https://github.com/auroris/AurorisNet.Umbraco.ApplicationVirtualPathFix/blob/main/docs/FIXES.md),
and report it in the
[issue tracker](https://github.com/auroris/AurorisNet.Umbraco.ApplicationVirtualPathFix/issues)
with the site's Umbraco release and package version.

## Supported versions

| Umbraco | Supported releases | Runtime |
|---|---|---|
| 18 | <!-- umbraco-releases 18 -->18.0.0 to 18.3.2<!-- /umbraco-releases --> | .NET 10 |
| 17 | <!-- umbraco-releases 17 -->17.5.0 to 17.8.2<!-- /umbraco-releases --> | .NET 10 |

Every release in these ranges is tested.
[`Directory.Build.props`](https://github.com/auroris/AurorisNet.Umbraco.ApplicationVirtualPathFix/blob/main/Directory.Build.props)
lists them in `UmbracoReleases`, and the package's Umbraco dependency spans the oldest to the
newest. Upgrade the package when you upgrade Umbraco.

Outside that range, restore stops with error `NU1107`. A site that references
`Umbraco.Cms.Web.Common` or `Umbraco.Cms.StaticAssets` directly gets warning `NU1608`
instead. On a release the list leaves out, including a prerelease, the startup log and the
health check warn, and the startup log names each patch that no longer applies.

## Documentation

| Guide | Covers |
|---|---|
| [What the package fixes](https://github.com/auroris/AurorisNet.Umbraco.ApplicationVirtualPathFix/blob/main/docs/FIXES.md) | Each URL that Umbraco builds without the path, the result with the package, and how the fixes apply. |

## Developing

Building needs the .NET 10 SDK, which `global.json` selects, and PowerShell 7 for the
scripts. On Windows, clone to a short path unless long paths are enabled: the test matrix
builds Playwright's files about 200 characters below the repository root.

```powershell
dotnet build tests/AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests
# The quick tests, on the newest supported Umbraco release
dotnet test --project tests/AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests --no-build -- --filter-not-class '*SiteEndToEndTests'
# Chromium for the browser cases: install once after the build, and again after a Playwright update
pwsh tests/AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests/bin/Debug/net10.0/playwright.ps1 install chromium
# The browser cases, on the newest supported Umbraco release
dotnet test --project tests/AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests --no-build -- --filter-class '*SiteEndToEndTests'
# The full matrix: the quick tests, then the browser cases, on every release in UmbracoReleases
pwsh tests/Test-UmbracoReleases.ps1
pwsh tests/Test-UmbracoReleases.ps1 -Browser
# Chosen releases
pwsh tests/Test-UmbracoReleases.ps1 -Releases 17.5.3,18.3.1
pwsh tests/Test-UmbracoReleases.ps1 -Releases 17.5.3,18.3.1 -Browser
# Add an Umbraco release by hand, with AurorisNet.Build cloned beside this repository
pwsh ../AurorisNet.Build/scripts/Add-UmbracoRelease.ps1 -Releases 18.3.2
# A local package, numbered with count 0
dotnet pack src/AurorisNet.Umbraco.ApplicationVirtualPathFix.csproj -c Release -o artifacts/packages
```

The quick tests check each patch against Umbraco's files and source maps, and test the
middleware, file provider, Tag Helper and health check. The browser cases drive new Umbraco
sites with the starter kit through Chromium.

`Test-UmbracoReleases.ps1` builds each release into `artifacts/umbraco-releases/<release>`,
prints one line per release and exits with 1 when a release fails. The full matrix runs on a
developer's machine. GitHub tests the newest supported release, and each new Umbraco release
when it appears.

`Add-UmbracoRelease.ps1` adds the release to `UmbracoReleases`, moves `UmbracoMaximumVersion`
and `UmbracoVersion` to the newest listed release, and rewrites the ranges in the
**Supported versions** table.

When you run the tests:

- `dotnet build` and `dotnet test` use `UmbracoVersion` in `Directory.Build.props`. Add
  `-p:UmbracoVersion=17.5.3` to build against another listed release. Outside a Git
  checkout, also add `-p:SourceTimestamp=2026-11-14T12:00:00Z`: the build reads the commit
  time from Git, and patched files use it as their `Last-Modified` time.
- `AVPF_E2E_CASE` selects browser cases: `full`, `iis`, `proxy`, `proxy-root` and
  `basic-auth`, separated by commas. Umbraco 17 keeps the first site's services for the whole
  process, so run one case per process on it, as `Test-UmbracoReleases.ps1` does.
- Each browser case writes its output, Playwright trace and site log to
  `e2e-artifacts/<case>/<run>` in the test project's output folder. `AVPF_KEEP_TEST_SITE=1`
  keeps each test site's folder.
- After changing a patch entry, test the releases from its `From` up to its `Before`. After
  changing other package code, run the full matrix.
- CI and the Release workflow treat build warnings as errors.

### Fix a release that fails

A new Umbraco release that fails the tests gets an issue titled
`Umbraco <release> fails the tests`, which links the run and its logs. To fix it:

1. Add the release with `Add-UmbracoRelease.ps1`, then run the quick tests with
   `pwsh tests/Test-UmbracoReleases.ps1 -Releases 18.3.2`. Each failure names its patch
   entry, and `PatchOriginTests` says whether Umbraco changed the code or only its minified
   output.
2. Fix each entry that no longer matches, and keep the earlier releases' entries as they are.
   For an entry in a major version's file, such as
   `src/Patchsets/Umbraco18/Umbraco18Patches.cs`, set its `Before` to the new release, add a
   copy with `From` set to the new release and `Before` set to the next major version
   (`19.0.0`), and update the copy. An entry in an area file, such as `SignInPatches.cs`,
   applies to every major version: move it into each major version's file, limited to that
   version, keep its edits in the area file for the copies to share, and update the new
   release's copy. `src/Patchsets/BackOfficeAssetPatches.cs` explains the entry format.
3. Run the quick tests again, then the browser cases with
   `pwsh tests/Test-UmbracoReleases.ps1 -Releases 18.3.2 -Browser`.
4. Commit the changes to `main`, with the release listed, and close the issue. Then start
   `release.yml` by hand to publish.

### Add an Umbraco major version

`Add-UmbracoRelease.ps1` and the daily workflow accept releases of the listed major versions
only. When Umbraco publishes a new major version, the workflow opens an issue titled
`Support Umbraco <major>`, and a person adds it. Umbraco 19 runs on .NET 11, and NuGet picks
a package's assembly by the site's .NET version, so Umbraco 19 needs a second build of the
package:

1. Install the .NET 11 SDK, raise the SDK in `global.json`, and add the SDK to the
   workflows' `setup-dotnet` steps.
2. Target `net10.0;net11.0` in the package project, with Umbraco 17 and 18 for .NET 10 and
   Umbraco 19 for .NET 11. Make the test project target .NET 11 when `UmbracoVersion` is a
   19 release, and add the Umbraco 19 starter kit to `Directory.Build.props`.
3. Add `src/Patchsets/Umbraco19/Umbraco19Patches.cs` and
   `src/UmbracoVersions/Umbraco19/Umbraco19Major.cs` to the .NET 11 build, and add
   `Umbraco19Major` to `UmbracoMajor.All` under `#if NET11_0_OR_GREATER`.
4. Add its first release to `UmbracoReleases` in `Directory.Build.props`, and set
   `UmbracoMaximumVersion` and `UmbracoVersion` to it.
5. Add a row to the **Supported versions** table with `umbraco-releases 19` markers like
   those in the rows above, and add the major version to the text and badge that name the
   supported versions.
6. Test the release and fix its patches, from step 1 of **Fix a release that fails**.

## Releases

The package follows Umbraco. Each day `umbraco-releases.yml` looks on nuget.org for new
Umbraco releases and tests the package on each. A release that passes is added to
`UmbracoReleases` and to the **Supported versions** table, and `release.yml` publishes the
package to nuget.org and GitHub. A release that fails gets an issue.

A package version is `<newest supported Umbraco release>.<count>`, such as `18.3.2.1`. Each
release publishes one package for each supported major version, such as `18.3.2.1` and
`17.8.2.1`, so a site can float on `18.*` or `17.*`. The count starts at 1 for each Umbraco
release.

The [release guide](https://github.com/auroris/AurorisNet.Build/blob/main/docs/releasing.md)
describes the workflows and how to start a release by hand.

## License

[MIT](https://github.com/auroris/AurorisNet.Umbraco.ApplicationVirtualPathFix/blob/main/LICENSE)
