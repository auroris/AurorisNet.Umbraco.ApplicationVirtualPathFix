# Umbraco Application Virtual Path Fix

[![MIT license](https://raw.githubusercontent.com/auroris/UmbracoApplicationVirtualPathFix/main/docs/images/badge-license.svg)](https://github.com/auroris/UmbracoApplicationVirtualPathFix/blob/main/LICENSE)
![.NET 10](https://raw.githubusercontent.com/auroris/UmbracoApplicationVirtualPathFix/main/docs/images/badge-dotnet-10.svg)

`AurorisNet.Umbraco.ApplicationVirtualPathFix` lets an Umbraco 17 or 18 site run under an
application virtual path, such as an IIS application at `/SomeTestPath` beneath the host's
root. Umbraco builds many backoffice, media, preview and sign-in URLs from the host root.
The package adds the path to each of them, so the backoffice at `/SomeTestPath/umbraco`
signs in, loads its scripts and stylesheets, shows media and previews, and renews sessions
under the path. It registers through Umbraco's composers and needs no code in the site.

A site at the root gains one fix: a fresh browser starts sign-in directly, and Umbraco's
initial token refresh runs only when a session may exist.
[What the package fixes](https://github.com/auroris/UmbracoApplicationVirtualPathFix/blob/main/docs/FIXES.md)
lists every URL the package corrects.

## Install

1. Add the package to a website that runs a [supported Umbraco release](#supported-versions).
   Reference the site's Umbraco major version, so NuGet installs the newest package for it:

   ```xml
   <PackageReference Include="AurorisNet.Umbraco.ApplicationVirtualPathFix" Version="18.*" />
   ```

   On Umbraco 17, use `Version="17.*"`. With central package management, a floating
   version needs `<CentralPackageFloatingVersionsEnabled>true</CentralPackageFloatingVersionsEnabled>`;
   otherwise give an exact version, such as `18.3.1.2`.
2. Set the site's application path in `appsettings.json`. For an IIS child application,
   use the application's path.

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

3. Start the site and open `/SomeTestPath/umbraco`. The [health check](#health-check)
   confirms that requests arrive under the path and that every patch applied.

## Supported versions

| Umbraco | Tested and supported releases | Package versions | Runtime |
|---|---|---|---|
| 18 | <!-- umbraco-releases 18 -->18.0.0 to 18.3.1<!-- /umbraco-releases --> | `18.*` | .NET 10 |
| 17 | <!-- umbraco-releases 17 -->17.5.0 to 17.8.1<!-- /umbraco-releases --> | `17.*` | .NET 10 |

[`UmbracoReleases`](https://github.com/auroris/UmbracoApplicationVirtualPathFix/blob/main/Directory.Build.props)
lists each tested release, and the package's NuGet dependency range spans the oldest to the
newest of them. Outside that range, restore stops with error `NU1107`; a site that
references `Umbraco.Cms.Web.Common` or `Umbraco.Cms.StaticAssets` directly gets warning
`NU1608` instead. On a release the table leaves out, including a prerelease, the startup
log and the health check warn, and the startup log names each patch that no longer applies.
Upgrade the package when you upgrade Umbraco.

A package version is `<newest supported Umbraco release>.<release count>`: `18.3.1.2` supports
every listed Umbraco 18 release up to 18.3.1 and is the second package release for 18.3.1.
Each release publishes one package for each supported major version from the same source,
such as `18.3.1.2` and `17.8.1.1`, and each major version counts on its own. Adding Umbraco
18.3.2 makes the next pair `18.3.2.1` and `17.8.1.2`.

## Hosting requirements

- **Requests reach the site with the prefix.** Either the path starts with the prefix, or
  the path base is already set: by IIS for a child application, or by `UseForwardedHeaders`
  with `X-Forwarded-Prefix` behind a proxy that strips the prefix. Route only the prefix to
  the site, because Kestrel also answers URLs outside it.
- **Absolute URLs built on the server**, such as email links, follow Umbraco's rules. Behind
  a proxy or load balancer, set `Umbraco:CMS:WebRouting:UmbracoApplicationUrl`, or apply
  `X-Forwarded-Proto` and `X-Forwarded-Host` with `UseForwardedHeaders` before `UseUmbraco()`.
- **Response compression:** register `UseResponseCompression()` before `UseUmbraco()`.
  Patched files compress like any other.
- **Several Umbraco sites on one host** share `Path=/` cookies. Give each its own
  `Umbraco:CMS:Security:BackOfficeTokenCookie:SiteName` and `Umbraco:CMS:Security:AuthCookieName`.
- **Third-party backoffice plugins** build their own URLs. The package prefixes their
  import-map entries, and each plugin builds its other URLs itself.
- **Restart the site after updating Umbraco.** The package patches Umbraco's backoffice
  files in memory at startup and leaves the files on disk unchanged.

When the site's own startup code needs the path, `VirtualPathHosting.Prefix(builder.Configuration)`
returns it with one leading slash, such as `/SomeTestPath`, or `null` for a site at the root.

Every listed release passes browser tests for five set-ups: HTTPS under a nested path with
media outside `wwwroot`, a path base set by the host as IIS does, a reverse proxy that
forwards the prefix, the same proxy at the root, and basic authentication. On 14 November
2026 the browser checks also passed against a published IIS child application: Windows
Server 2022, .NET 10.0.14, in-process hosting, external media storage, image signing and no
WebSockets.

## Health check

**Settings → Health Check → Configuration → Application virtual path** reports each part of
the fix, and the startup log carries the same warnings and errors. At the root, the check
reports the Umbraco release and notes that only the session restore fix applies.

| Check | Message when something is wrong | What to do |
|---|---|---|
| Umbraco release | The package supports Umbraco *list*, but the site runs Umbraco *release*. | Install a package version that lists the site's release. |
| Path base | The site is configured for `/SomeTestPath`, but this request arrived with the path base "". | Send requests with the prefix in their path, or set the path base through IIS or `X-Forwarded-Prefix`. |
| Patched files | Expected one Umbraco file for *path*, found *n*, so it is not patched. | The site's Umbraco files differ from the tested releases. Install a package version that lists the site's release. |
| Patched files | Umbraco's *file or change* *reason*, so that part of the fix is not applied. | As above. |
| Patched files | The backoffice files have not been patched yet. | The files are patched when Umbraco's pipeline starts. Run the check again once the site has started. |
| Requests outside the path | *n* requests have reached the site outside `/SomeTestPath` since it started, most recently *path*. | Route only the prefix to the site. Load balancer probes at the root also count. |

In the browser, a missing fix shows in the developer tools' Network tab as a request
without the prefix, such as `/umbraco/management/api/v1/...` in place of
`/SomeTestPath/umbraco/management/api/v1/...`, which fails or reaches whatever the host
serves at its root. Find the URL's row in
[What the package fixes](https://github.com/auroris/UmbracoApplicationVirtualPathFix/blob/main/docs/FIXES.md),
and report it in the [issue tracker](https://github.com/auroris/UmbracoApplicationVirtualPathFix/issues)
with the site's Umbraco release and package version. Umbraco's own security-header checks
request the host root, so their results describe the host rather than the site.

## Developing

Building needs the .NET 10 SDK, which [`global.json`](https://github.com/auroris/UmbracoApplicationVirtualPathFix/blob/main/global.json)
selects, and PowerShell 7. On Windows, clone to a short path: Playwright's package nests
deeply, and the build fails with `MSB3021` when a path exceeds 260 characters unless long
paths are enabled.

```powershell
# Build the package and its tests
dotnet build tests/AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests
# Quick tests: each patch against Umbraco's files and source maps, the middleware, file provider, Tag Helper and health check
dotnet test --project tests/AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests --no-build -- --filter-not-class '*SiteEndToEndTests'
# Browser tests: install Chromium once for each Playwright version, then run the cases
pwsh tests/AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests/bin/Debug/net10.0/playwright.ps1 install chromium
dotnet test --project tests/AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests --no-build -- --filter-class '*SiteEndToEndTests'
# Quick or browser tests against chosen releases, each built on its own and run in parallel
pwsh tests/Test-UmbracoReleases.ps1 -Releases 17.5.3,18.3.1
pwsh tests/Test-UmbracoReleases.ps1 -Releases 17.5.3,18.3.1 -Browser
# The workflow scripts' tests, with Pester 5.7.1
Invoke-Pester .github/scripts -CI
# A local package, numbered with release count 0, which is never published
dotnet pack src -c Release -o artifacts/packages
```

- The tests build against `UmbracoVersion` in `Directory.Build.props`. Add
  `-p:UmbracoVersion=17.5.3` to build against another listed release. Outside a Git
  checkout, also add `-p:SourceTimestamp=2026-11-14T12:00:00Z`.
- `AVPF_E2E_CASE` selects browser cases: `full`, `iis`, `proxy`, `proxy-root` and
  `basic-auth`. On Umbraco 17, run one case per process, because Umbraco 17 keeps the
  first site's services for the whole process; `Test-UmbracoReleases.ps1` does this.
  Results, Playwright traces and site logs go to `e2e-artifacts/<case>/<run>` in the test
  project's `bin/Debug/net10.0`, and `AVPF_KEEP_TEST_SITE=1` keeps each test site's folder.
- After changing a patch entry, test the releases from its `From` up to its `Before`. After
  changing other package code, test one release of each major version with both kinds of
  tests, and run the quick tests on every release when the code calls an Umbraco API the
  package has not used before.
- The Release workflow treats build warnings as errors, so keep the build free of warnings.

### Add an Umbraco release

The **New Umbraco releases** workflow checks NuGet every day for stable releases of the
supported major versions that are newer than the newest listed one, and runs both kinds of
tests on them:

- Releases that pass are listed on a branch named `umbraco-releases/<releases>`, with a
  pull request. Merge it, then [publish a release](#publish-a-release).
- A release that fails gets an issue titled `Umbraco <release> fails the tests`, which links
  the run and its logs, traces and screenshots. Fix it with the steps below. Closing the
  issue lets the next run test the release again.
- A stable release of a newer major version gets an issue titled `Support Umbraco <major>`,
  for the steps in [Add an Umbraco major version](#add-an-umbraco-major-version).

The workflow opens pull requests when **Settings → Actions → General → Workflow
permissions → Allow GitHub Actions to create and approve pull requests** is on, and
otherwise opens an issue that links the branch. Run it from **Actions → New Umbraco
releases → Run workflow** with its `releases` and `dry-run` inputs to test chosen releases.

To add a release by hand:

1. List it. The script adds it to `UmbracoReleases`, moves `UmbracoMaximumVersion` and
   `UmbracoVersion` to it when it is the newest, and rewrites the supported-versions table
   above.

   ```powershell
   pwsh .github/scripts/Add-UmbracoRelease.ps1 -Releases 18.3.2
   ```

2. Run the quick tests with `pwsh tests/Test-UmbracoReleases.ps1 -Releases 18.3.2`. Each
   failure names its patch entry, and `PatchOriginTests` says whether Umbraco changed the
   code or only its minified output.
3. Fix each entry that no longer matches, and keep the earlier releases' entries as they
   are. For an entry in a major version's file, such as
   `src/Patchsets/Umbraco18/Umbraco18Patches.cs`, set its `Before` to the new release, add
   a copy with `From` set to the new release and `Before` set to the next major version
   (`19.0.0`), and update the copy. An entry in an area file, such as `SignInPatches.cs`,
   applies to every major version: move it into each major version's file, limited to that
   version, keep its edits in the area file for the copies to share, and update the new
   release's copy. `src/Patchsets/BackOfficeAssetPatches.cs` explains the entry format.
4. Run the quick tests again, then the browser tests with
   `pwsh tests/Test-UmbracoReleases.ps1 -Releases 18.3.2 -Browser`.
5. [Publish a release](#publish-a-release).

### Add an Umbraco major version

Umbraco 19 runs on .NET 11, and NuGet picks a package's assembly by the site's .NET
version, so Umbraco 19 needs a second build of the package:

1. Install the .NET 11 SDK, add it to the workflows' `setup-dotnet` steps, and raise the SDK
   version in `global.json`.
2. Target `net10.0;net11.0` in the package project, with Umbraco 17 and 18 for .NET 10 and
   Umbraco 19 for .NET 11. Make the test project target .NET 11 when `UmbracoVersion` is a
   19 release, and add the Umbraco 19 starter kit to `Directory.Build.props`.
3. Add `src/Patchsets/Umbraco19/Umbraco19Patches.cs` and
   `src/UmbracoVersions/Umbraco19/Umbraco19Major.cs` to the .NET 11 build, and add
   `Umbraco19Major` to `UmbracoMajor.All` under `#if NET11_0_OR_GREATER`.
4. List the first 19 release in `UmbracoReleases` by hand, because the add script accepts
   only listed major versions. Add a row for Umbraco 19 to the supported-versions table,
   with `umbraco-releases 19` markers like the other rows, and update the text that names
   the supported major versions, including the repository's description on GitHub.
5. Continue from step 2 of [Add an Umbraco release](#add-an-umbraco-release).

### Publish a release

Open **Actions → Release → Run workflow** on GitHub and select `main`. The workflow runs no
tests, because each Umbraco release was tested when it was added. It chooses each version's
release count from the release tags, builds and packs each version, reserves a tag for each,
such as `v18.3.1.2` and `v17.8.1.1`, then publishes each package to NuGet through trusted
publishing and creates a GitHub release for each, marking the newest major version's as the
latest.

- **dry-run** stops after packing and keeps the packages as the run's `packages-<run>`
  artifact.
- **notes** is Markdown placed above GitHub's generated list of changes, which names merged
  pull requests only. Describe direct commits to `main` and anything a site must do when it
  updates.

Running Release again for a tagged commit reuses its tags, so a partial publication can
finish. Keep every `v*` tag: NuGet keeps every version it has published, so the counts must
go past them. Umbraco Marketplace reads [`umbraco-marketplace.json`](https://github.com/auroris/UmbracoApplicationVirtualPathFix/blob/main/umbraco-marketplace.json)
from `main`, so push changes to it before releasing.

## License

[MIT](https://github.com/auroris/UmbracoApplicationVirtualPathFix/blob/main/LICENSE)
