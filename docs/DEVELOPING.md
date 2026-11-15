# Develop and release the fixes

Build the repository to produce `AurorisNet.Umbraco.ApplicationVirtualPathFix` for the
Umbraco releases listed in `UmbracoReleases` in `Directory.Build.props`, currently
<!-- umbraco-releases -->17.5.0 to 18.3.1<!-- /umbraco-releases -->. The package depends on the range from `UmbracoMinimumVersion` to
`UmbracoMaximumVersion`, so NuGet compiles it against the oldest supported release.
The tests run against the release in `UmbracoVersion`, the newest by default. Pass
`-p:UmbracoVersion=18.2.1`, for example, to test another, or test every supported
release with [`tests/Test-UmbracoReleases.ps1`](#test-every-supported-release).

## Build and test

Install the .NET 10 SDK, check out `main`, and run these commands from the
repository root:

```powershell
dotnet build tests/AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests
dotnet test --project tests/AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests --no-build -- --filter-not-class '*SiteEndToEndTests'
pwsh tests/AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests/bin/Debug/net10.0/playwright.ps1 install chromium
dotnet test --project tests/AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests --no-build -- --filter-class '*SiteEndToEndTests'
```

Run the fast asset, URL and middleware checks first, then the browser suite. Omit the
class filter to run everything in one invocation.
Repeat the browser installation after upgrading Playwright. The browser tests build
five starter-kit sites with their own SQLite databases and start Kestrel on free
loopback ports, one site at a time. Each site runs Umbraco's background jobs at most
once: the distributed jobs, the cache instruction sync and the server registration.
Those jobs commit database writes on their own schedule, and on a busy machine such a
commit can leave a save, sign-in or invitation that read the database just before
retrying a write that SQLite can never accept. One runs the full affected-feature journey;
the other four check specific hosting risks. Each step waits up to a minute for the
site or the browser, because several cases often run at once; a wait ends as soon as
its condition holds. When a step
fails, the test output includes the page's accessibility tree, and screenshots are
saved under `bin/Debug/net10.0/e2e-artifacts/<case>/<run>`, next to a `test-output.log`,
Playwright `trace.zip` and a copy of the site's JSON logs. The failed step also saves
a memory dump of the test process, which hosts the site, as `<step>.dmp`. If the site
stopped answering, open the dump with the `dotnet-dump` tool's `analyze` command:
`clrstack -all` and `dumpasync` show what the site was waiting on. Traces and logs are saved
for passing runs too, including failures detected during server shutdown. Open a trace with
`pwsh tests/AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests/bin/Debug/net10.0/playwright.ps1 show-trace <path-to-trace.zip>`.
Set `AVPF_KEEP_TEST_SITE=1` to keep each
site's folder in the temp directory, with its database and Umbraco logs. Run a
single test with `-- --filter-method "*Basic_authentication*"`, or select browser
cases with `AVPF_E2E_CASE`: `full`, `iis`, `proxy`, `proxy-root`, or `basic-auth`.
Comma-separated lists such as `iis,proxy` are supported; unselected cases skip
before installing a site. On Umbraco 17, run one case per process. Its view helpers
keep the first site's services in static readonly properties, so a second site in
the same process would use services that were already disposed. The suite skips
every case after the first on Umbraco 17 and says why, and
[`tests/Test-UmbracoReleases.ps1`](#test-every-supported-release) runs each case in its own
process. The sites start from
`SiteTemplate`, the views a new Umbraco 18.3.1 project ships with, which also work
on Umbraco 17. They install the starter kit for the release's major version: 17.0.0
on Umbraco 17 and 18.0.0 on Umbraco 18. Third-party
embeds and fonts receive fixed offline responses, and jQuery 3.6.0 is vendored
under `BrowserFixtures` with its MIT license and original integrity hash. `.gitattributes`
preserves those bytes across checkouts, including Windows with `core.autocrlf=true`. Unexpected
external requests fail the test. After NuGet restore and browser installation, the
suite does not need internet access. HTTPS cases use disposable certificates;
no certificate is installed in the machine's trust store. See
[Browser validation](FIXES.md#browser-validation) for the checks and their scope.

Both projects generate XML documentation, so every build checks the doc comments. The
compiler warns about malformed XML, references that do not resolve, parameter names
that do not match and public members without a comment. Keep the build free of these
warnings. The package includes the documentation file, which gives IntelliSense for
its public API.

Every patch is an entry in `src/Patchsets`. Entries that apply to every supported
release are in the files there, one per area of Umbraco. Entries for one major
version's releases are in that version's folder, such as `src/Patchsets/Umbraco18`,
and stay within it, so supporting a new major version adds a folder instead of
changing the others. When two major versions share code, as Umbraco 17's later
releases share 18's, each folder has its own entry and the area file defines the
edits once. `BackOfficeAssetPatches.cs` joins the entries into one list and explains how
to read an entry. An entry names a file by its path in the web root, with `-????????`
for a bundle's build hash, and lists its changes: it can move the file's quoted
`/umbraco/backoffice/` URLs under the asset root, apply edits, and add a preamble to
its first line. `From` and `Before` limit an entry to a range of Umbraco releases; an
entry without them applies to every supported release. At startup the file provider
takes the entries for the site's release and applies them top to bottom, so a file
named by several entries gets all of their changes, in order. Each edit names the
line of Umbraco's source it changes, and a hashed bundle without edits names a source
it is built from. The folder holds only these entries, so a reviewer can check every
change in one place. Each entry has a comment that says what it fixes, its code is
written as raw string literals that read as they do in Umbraco's file apart from
placeholders, and every value sits on its own labeled line. Keep new entries in the
same layout. `src/Assets/AssetPatch.cs` defines the entries, and
`src/Assets/AssetPatcher.cs` finds the files and applies the changes.

What else the package does differently for each Umbraco major version is in
`src/UmbracoVersions`. `UmbracoMajor` lists the supported major versions, and each has
a subclass in a folder of its own, such as `Umbraco18/Umbraco18.cs`. The subclass names
that version's patches and the parts of Umbraco's API it handles differently: today,
the path where Umbraco serves its API documents, Swashbuckle's `/umbraco/swagger` in 17
and `/umbraco/openapi` in 18. The composer picks the subclass for the site's release
at startup. Umbraco 17 and 18 both run on .NET 10, so one build serves both and a
subclass can use only the APIs of the oldest supported release. Code that every
supported version handles alike stays where it is, and when a new version changes an
API the package uses, the difference becomes a member of `UmbracoMajor` that each
subclass implements. `SupportedReleasesTests` checks that each major version's entries
stay within it and that the release under test has its major version's subclass.

`AssetPatchTests` also checks every patch against the restored Umbraco files without
starting a site. It fails if a path finds zero or multiple files, if a search pattern
does not match the number of times its patch expects, if a patch changes the file's
number of lines, or if a patched script does not parse as JavaScript. The parser,
Acornima, first reads the script as Umbraco installed it. Keeping generated line
numbers stable limits source-map drift to the edited lines; column mappings on
those lines are approximate. Run it on its own with
`dotnet test --project tests/AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests --no-build -- --filter-class '*AssetPatchTests'`.
When adding an Umbraco release, run these checks for it first, as
[Add an Umbraco release](#add-an-umbraco-release) describes.
Bundle hash changes alone need no patch edits. Code patches write minified
identifiers as `@name` placeholders, named after Umbraco's TypeScript source, so a
rebuild that only renames variables needs no edits either. A repeated `@name` must
match the same identifier, and `@@` stands for a literal `@`. A behavior change
expects exactly one match; URL rewrites expect one or more. The package embeds the
validated `UmbracoReleases` list for startup and health-check warnings. The dependency
range spans that list but can also admit untested releases between its bounds.

`PatchOriginTests` checks each patch against Umbraco's source maps: every match of
an edit must come from the line of Umbraco's TypeScript that the edit names, so a
pattern cannot quietly change different code. When a pattern stops matching, the
failure says whether Umbraco's source still contains that line, in which case only
the minified output changed and the pattern needs updating, or whether Umbraco
changed the code, in which case the patch needs another look. When a path finds no
file, the failure names the bundle that is now built from the patch's sources.

`AssetCoverageTests` independently scans the restored JavaScript and CSS for
root-absolute asset references. New or renamed files fail the scan unless a patch
handles them or an explicit exception explains the reference. Both scans use the
production file locator, including `Contains` filters, to resolve each patch to
exactly one installed file. It also reads Umbraco's TypeScript from the bundles'
source maps, and the unminified apps that
ship without maps, and finds each line that builds a URL in a way that can lose the
virtual path: the server URL joined to a dynamic value, `new URL` with a base, the
bare origin used to build a URL, and root-absolute paths. A line counts as patched when
each bundle containing it receives an edit that names it, which `PatchOriginTests`
confirms, or has its asset URLs moved. Any other line must carry a review in the test.
A new or changed line fails, and so does a review that Umbraco's code no longer matches.
These are conservative line checks, not a JavaScript parser; inspect each new match
when upgrading Umbraco. Patch checks and the browser journey cover the known URL
corrections.

`UrlTests` checks media-prefix boundaries, signed queries and fragments, import-map
entries and scopes, and OAuth callbacks without starting a site. `BackOfficeBodyTests`
uses the real body Tag Helper, including DI and view-context activation, to check
attributes, form actions, view scoping and session hints with configured cookie names
and SameSite modes. Middleware checks cover request-path restoration and version
warnings. `DecoratorTests` fails if the package leaves a member of an Umbraco interface
to its default implementation. Umbraco adds members that way, and a decorator written
before the member existed would otherwise run the default instead of forwarding.
Because the package compiles against the oldest supported release, a decorator class
cannot forward a member that a later release adds, as 18.3 added
`IForgotPasswordUriProvider.CanCreateForgotPasswordUriAsync`. The sign-in link
wrappers therefore use `ForwardingProxy`, which passes every call it does not replace
to Umbraco's service, and `DecoratorTests` checks that it reaches the service's own
implementation of such a member. A call to a member that a later release changed
fails only when it runs, as Umbraco 18 gave the pipeline filter's full constructor
two more parameters, so the composer uses the constructor and properties that both
versions have. `SupportedReleasesTests` resolves every type and member the compiled
package refers to against the release under test, which finds such a change without
running the code. It also checks that the dependency range spans `UmbracoReleases`
and the package embeds and accepts that exact list, that the release under test is
listed, and that every entry's `From` and `Before`
are release numbers covering at least one supported release. `HealthCheckTests`
covers the health check's results for a site at the root, a
working site under a virtual path, and a site with an unsupported Umbraco version, a
missing path base, an unapplied change and requests outside the virtual path. The
`full` browser case runs the check through the management API.

`AssetFileProviderTests` checks that startup prepares patched copies without changing
the original files, and that repeated reads neither repeat patching nor log more
warnings. Each patched copy's `Last-Modified` is the later of the Umbraco file's time
and the time of the commit the package was built from, so a second provider, as after
a restart or on another server, reports the same validators. The build reads that time
with `git log`. To build outside a Git checkout, pass it as
`-p:SourceTimestamp=2026-11-14T12:00:00Z`. A small HTTP host exercises the real static-file handler's content lengths,
HEAD requests, conditional responses and byte ranges. Middleware tests check that
prepared assets and rendered HTML compress correctly with either middleware ordering.
These checks do not install Umbraco or start another starter-kit site.

The `full` browser case imports every registered JavaScript module, requests real
static files with `If-None-Match`, and verifies the login script's content version.
It combines `/SomeTestPath/Nested`, HTTPS, external media and `Hosting:Debug=false`
in one site. The proxy cases are `proxy`
and `proxy-root`: the fixture changes the incoming scheme and host,
strips the prefix, then passes the original values through `UseForwardedHeaders`.
They model proxy input within Kestrel; they do not start a separate proxy server.

The fixture adds two images to the starter kit's home template to exercise the
media picker's published value through `.Url()` and signed `GetCropUrl()` output.
It supplies the crop helper's services explicitly because Umbraco's convenience
overload caches the first site's services in static readonly properties, while
this suite boots several independent sites in one process on Umbraco 18.

Run the workflow-script checks with Pester 5.7.1:

```powershell
Install-Module Pester -RequiredVersion 5.7.1 -Scope CurrentUser -Force -SkipPublisherCheck
Import-Module Pester -RequiredVersion 5.7.1
Invoke-Pester .github/scripts -CI
```

The release-count tests create disposable Git repositories. They cover initial and
subsequent release counts, counts that follow `UmbracoMaximumVersion` rather than the
release the tests run against, the .NET assembly version limit, tagged retries, stale
commits, releases from a branch other than `main`, checkouts that differ from the
release commit, and malformed version properties. The tests for the
[New Umbraco releases](#new-umbraco-releases) scripts check which NuGet releases count
as new, how a release is added to `Directory.Build.props` and the documents, and which
releases pass when given test summaries, without calling GitHub. None of them tag,
publish or change this repository.

Clone into a short path on Windows. Playwright's package contains deeply nested
files, and the build fails with `MSB3021` when a path exceeds 260 characters unless
long paths are enabled.

## Test every supported release

`tests/Test-UmbracoReleases.ps1` builds the test project once for each release in
`UmbracoReleases`, into `artifacts/umbraco-releases/<release>`, and then runs the
tests for all of them at the same time:

```powershell
pwsh tests/Test-UmbracoReleases.ps1
pwsh tests/Test-UmbracoReleases.ps1 -Browser
```

The builds run one at a time, because Umbraco's build targets write
`appsettings-schema.json` into the project folder and parallel restores can collide
in the NuGet cache. Each release's fast tests run in one process, and each browser
case runs in a process of its own, as Umbraco 17 needs. Every process has its own
temporary folder. The script prints one line per release, lists the failed tests,
and exits with 1 when any release fails. Pass `-Releases 18.2.1,18.3.0` to test some
releases, `-Throttle 2` to limit how many test processes run at once (by default,
every release's fast tests run at once, and browser cases half as many as the
machine has processors, because each keeps a site and Chromium busy), and
`-ArtifactsPath` to put the builds elsewhere. `-Browser` needs Chromium installed, as
[Build and test](#build-and-test) shows, and `AVPF_E2E_CASE` selects its cases.
`-SummaryPath results.json` also writes each release's result and failed tests as JSON.

### New Umbraco releases

The **New Umbraco releases** workflow runs every day. It looks on NuGet for stable
releases of the supported major versions that are newer than the newest listed
release of each, and tests them on a Windows runner with the fast tests and every
browser case. Then it reports:

- The releases that passed go on a branch named `umbraco-releases/<releases>`, added
  with `.github/scripts/Add-UmbracoRelease.ps1`, and a pull request opens for them.
  Merge it, then run the Release workflow to publish a package that lists them.
  Until then, sites on those releases see the package's unsupported-release warning.
- Each release that failed gets an issue titled `Umbraco <release> fails the tests`,
  listing the failed tests and linking the run, whose artifacts hold the logs, traces
  and screenshots. Close the issue once the cause is fixed, and the next run tests the
  release again.
- A stable release of a newer major version gets an issue titled
  `Support Umbraco <major>`, because it needs the steps in
  [Add an Umbraco major version](#add-an-umbraco-major-version) first.

The workflow skips a release whose branch or failure issue is open. To test
particular releases, or to see the results without opening anything, run it from
**Actions → New Umbraco releases → Run workflow** with its inputs. GitHub lets a
workflow open pull requests only once **Settings → Actions → General → Workflow
permissions → Allow GitHub Actions to create and approve pull requests** is on. Until
then, the workflow pushes the branch and opens an issue that links it. These runs are
the only tests that run on GitHub; the Release workflow only builds and publishes.

### Add an Umbraco release

The New Umbraco releases workflow adds releases that pass without changes. A release
that needs new patches, or one added by hand, takes these steps:

1. Run `.github/scripts/Add-UmbracoRelease.ps1 -Releases <release>`. It adds the
   release to `UmbracoReleases` in `Directory.Build.props` and, when the release is
   the newest, sets `UmbracoMaximumVersion` and `UmbracoVersion` to it, so the next
   package release is numbered after it, starting at `<release>.1`. It also updates
   the supported range that the documents show between `umbraco-releases` comments,
   and in `docs/NUGET.md`, which nuget.org shows without HTML.
2. Run `pwsh tests/Test-UmbracoReleases.ps1 -Releases <release>`. The fast tests
   show which entries no longer match, and `PatchOriginTests` says whether Umbraco
   changed the code or only its minified output.
3. Fix each entry that no longer matches:
   - For an entry in the release's major version folder, such as
     `Umbraco18Patches.cs`, set its `Before` to the new release, and add an entry with
     `From` set to the new release and `Before` set to the next major version's first
     release, such as `19.0.0`.
   - An entry in the area files applies to every major version, so copy it into each
     major version's folder, limited to that version, and remove it from the area file.
     Then change the copy in the release's major version folder as above. Keep the
     shared edits in the area file, and have the copies refer to them.
4. Run the fast tests for the new release again, then the browser tests with
   `pwsh tests/Test-UmbracoReleases.ps1 -Releases <release> -Browser`. The earlier
   releases need no new run: the new entries start at the new release, and each
   `Before` leaves the earlier releases' entries as they were.

### Add an Umbraco major version

Umbraco 19 runs on .NET 11, and NuGet picks a package's assembly by the site's .NET
version, so it needs a second build of the package:

1. Install the .NET 11 SDK, and add it to the workflows' `setup-dotnet` steps.
2. Target `net10.0;net11.0` in the package project, with each target's own range of
   Umbraco packages: 17 and 18 for .NET 10, 19 for .NET 11. Make the test project
   target .NET 11 when `UmbracoVersion` is a 19 release, and set the starter kit
   version for 19 in `Directory.Build.props`.
3. Add `src/Patchsets/Umbraco19/Umbraco19Patches.cs` and
   `src/UmbracoVersions/Umbraco19/Umbraco19.cs`, compile those folders into the .NET 11
   build only, and add `Umbraco19` to `UmbracoMajor.All` there, with
   `#if NET11_0_OR_GREATER`. The .NET 11 build compiles against Umbraco 19, so
   `Umbraco19` can use its API directly. An API that Umbraco 19 changed becomes a
   member of `UmbracoMajor`, which each version's subclass implements.
4. List the first 19 release in `UmbracoReleases` by hand, since the add script
   refuses a new major version. Then follow [Add an Umbraco release](#add-an-umbraco-release)
   from step 2, and update the documents that name the supported major versions.

## Build a package

To build a package locally:

```powershell
dotnet pack src -c Release -o artifacts/packages
```

Local builds use `UmbracoMaximumVersion` with a release count of 0, currently
`18.3.1.0`. Count 0 is never published, so a local package cannot be confused with a
release in NuGet's cache. The release workflow supplies its selected version as the
MSBuild `Version` property when building and packing. The assembly embeds its symbols and generated sources, and Source Link points
them at the commit on GitHub, so a site's debugger can step into the package.
`ContinuousIntegrationBuild`, which the release workflow sets, keeps the paths in them
deterministic.

## Publish a release

The Release workflow runs no tests. Each Umbraco release is tested once: when it is
added, by the New Umbraco releases workflow or locally, and again only after a change
that might affect it, locally. A new or
changed entry in `src/Patchsets` affects the releases from its `From` up to its
`Before`. A change to the package's other code, or a new `UmbracoMinimumVersion`,
can affect them all. Before releasing, run both tiers for those releases with
`-Releases`, as [Test every supported release](#test-every-supported-release) shows,
and run the release-script checks with Pester if you changed `.github/scripts`. Then
push the commit to `main`, open **Actions → Release → Run workflow** on GitHub and
select `main`. Pushes alone never start a release. The workflow:

1. Checks that it runs from `main`, then chooses the next
   `<UmbracoMaximumVersion>.<count>`, such as `18.3.1.2`, from the existing release
   tags.
2. Builds and packs the package.
3. Reserves a tag such as `v18.3.1.2` at the commit.
4. Publishes the package to NuGet and creates a GitHub release with the package
   attached.

NuGet trusted publishing is configured for the `AurorisNet` account, GitHub
repository `auroris/UmbracoApplicationVirtualPathFix`, workflow filename
`release.yml`, and no environment. The
workflow obtains a short-lived API key through `NuGet/login` using OIDC. See
[NuGet trusted publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing)
for the policy fields.

Re-running a tagged commit reuses its reserved version so a partially completed
publication can finish. A new count is only allocated for the tip of `main`, so
re-running an older failed run cannot publish older code under a newer version.
Release runs are serialized to keep the count consistent. The
[version-numbering reference](VERSIONING.md) explains how versions relate to the
supported Umbraco releases.

The package includes the `umbraco-marketplace` tag for Marketplace discovery, and
`umbraco-marketplace.json` holds the listing metadata. Push changes to that metadata
before releasing, then validate the published package using the
[Marketplace listing instructions](VERSIONING.md#marketplace-listing).
