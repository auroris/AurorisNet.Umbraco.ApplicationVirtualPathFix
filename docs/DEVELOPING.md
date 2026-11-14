# Develop and release the Umbraco 18.3.1 fixes

Build this branch to produce `AurorisNet.Umbraco.ApplicationVirtualPathFix` for
Umbraco 18.3.1. The package and browser tests both read `UmbracoVersion` from
`Directory.Build.props`, which keeps their Umbraco dependency aligned.

## Build and test

Install the .NET 10 SDK, check out `umbraco/18.3.1`, and run these commands from
the repository root:

```powershell
dotnet build tests/AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests
pwsh tests/AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests/bin/Debug/net10.0/playwright.ps1 install chromium
dotnet test --project tests/AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests --no-build
```

Repeat the browser installation after upgrading Playwright. The tests build
starter-kit sites with their own SQLite databases and start Kestrel on free
loopback ports, one site at a time; a full run takes several minutes. When a step
fails, the test output includes the page's accessibility tree, and screenshots are
saved under `bin/Debug/net10.0/e2e-artifacts/<case>/<run>`, next to a `test-output.log`,
Playwright `trace.zip` and a copy of the site's JSON logs. Traces and logs are saved
for passing runs too, including failures detected during server shutdown. The release
workflow uploads these diagnostics even when a test fails. Open a trace with
`pwsh tests/AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests/bin/Debug/net10.0/playwright.ps1 show-trace <path-to-trace.zip>`.
Set `AVPF_KEEP_TEST_SITE=1` to keep each
site's folder in the temp directory, with its database and Umbraco logs. Run a
single test with `-- --filter-method "*Basic_authentication*"`, and limit the site
test to some hosting cases with `AVPF_E2E_CASE`, for example `kestrel` or
`root,iis-external-media-no-websockets` or `kestrel-https`. The sites start from
`SiteTemplate`, the views a new Umbraco 18.3.1 project ships with. Third-party
embeds and fonts receive fixed offline responses, and jQuery 3.6.0 is vendored
under `BrowserFixtures` with its MIT license and original integrity hash. `.gitattributes`
preserves those bytes across checkouts, including Windows with `core.autocrlf=true`. Unexpected
external requests fail the test. After NuGet restore and browser installation, the
suite does not need internet access. The HTTPS case uses a disposable certificate;
no certificate is installed in the machine's trust store. See
[Browser validation](FIXES.md#browser-validation) for the checks and their scope.

`AssetPatchTests` also checks every patch against the restored Umbraco files without
starting a site. It fails if a bundle pattern finds zero or multiple files, or if
any search pattern has no match. Run it on its own with
`dotnet test --project tests/AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests --no-build -- --filter-class '*AssetPatchTests'`.
When updating `UmbracoVersion`, build and run these checks before the full suite.
Bundle hash changes alone need no patch edits; the definitions in
`BackOfficeAssetPatches.cs` use `-????????` for hashed filenames. The package's
exact version dependency still identifies the release that has been validated.

`AssetCoverageTests` independently scans the restored JavaScript and CSS for
root-absolute asset references and reviews server-URL consumers and concatenations.
New or renamed files fail the scan unless a patch handles them or an explicit
exception explains the reference. These are conservative source checks, not a
JavaScript parser; inspect each new match when upgrading Umbraco.

The browser cases also import every registered JavaScript module and request real
static files with `If-None-Match`. `kestrel-external-media` uses `/a/b` with
`Hosting:Debug=false`. The proxy cases are `kestrel-no-websockets-proxy-https` and
`root-no-websockets-proxy-https`: the fixture changes the incoming scheme and host,
strips the prefix, then passes the original values through `UseForwardedHeaders`.
They model proxy input within Kestrel; they do not start a separate proxy server.

The fixture adds two images to the starter kit's home template to exercise the
media picker's published value through `.Url()` and signed `GetCropUrl()` output.
It supplies the crop helper's services explicitly because Umbraco's convenience
overload caches the first site's services in static readonly properties, while
this suite boots several independent sites in one process.

Run the release-script checks with Pester 5.7.1 (also run by the release workflow):

```powershell
Install-Module Pester -RequiredVersion 5.7.1 -Scope CurrentUser -Force -SkipPublisherCheck
Import-Module Pester -RequiredVersion 5.7.1
Invoke-Pester .github/scripts/Get-ReleaseVersion.Tests.ps1 -CI
```

These tests create disposable Git repositories and cover initial and subsequent
revisions, tagged retries, stale commits, branch/checkout mismatches and malformed
version properties. They do not tag or publish this repository.

Clone into a short path on Windows. Playwright's package contains deeply nested
files, and the build fails with `MSB3021` when a path exceeds 260 characters unless
long paths are enabled.

To build a package locally:

```powershell
dotnet pack src/AurorisNet.Umbraco.ApplicationVirtualPathFix -c Release -o artifacts/packages
```

Local builds use version `18.3.1.0`. Revision 0 is never published, so a local
package cannot be confused with a release in NuGet's cache. The release workflow
supplies its selected revision as the MSBuild `Version` property when building and
packing.

## Publish a revision

Push the intended changes to `umbraco/18.3.1`, then open **Actions → Release →
Run workflow** on GitHub and select that branch. The workflow:

1. Checks that the branch name matches the configured Umbraco version and
   chooses the next `18.3.1.<revision>` from the existing release tags.
2. Builds, installs Chromium and runs the browser tests before packing.
3. Reserves a tag such as `v18.3.1.2` at the tested commit.
4. Publishes the package to NuGet and creates a GitHub release with the package
   attached.

NuGet trusted publishing is configured for the `AurorisNet` account, GitHub
repository `auroris/UmbracoApplicationVirtualPathFix`, workflow filename
`release.yml`, and no environment. The
workflow obtains a short-lived API key through `NuGet/login` using OIDC. See
[NuGet trusted publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing)
for the policy fields.

Re-running a tagged commit reuses its reserved version so a partially completed
publication can finish. A new revision is only allocated for the branch tip, so
re-running an older failed run cannot publish older code under a newer version.
Release runs on this branch are serialized to keep revision selection consistent.
Ordinary pushes update the branch; publishing starts when a maintainer runs
Release.

The `main` branch keeps the workflow available in GitHub's Actions interface.
Selecting `umbraco/18.3.1` runs this branch's own copy of `release.yml`, with its
source, scripts, tests and documentation, so keep the copies in step. The
[version-numbering reference](https://github.com/auroris/UmbracoApplicationVirtualPathFix/blob/main/docs/VERSIONING.md)
explains the branch and tag conventions across supported Umbraco versions.

The package includes the `umbraco-marketplace` tag for Marketplace discovery.
Shared listing metadata lives on `main`. Push changes to that metadata before
releasing this branch, then validate the published package using the
[Marketplace listing instructions](https://github.com/auroris/UmbracoApplicationVirtualPathFix/blob/main/docs/VERSIONING.md#marketplace-listing).
