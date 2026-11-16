# Developing

This page covers building the package, running its tests and adding Umbraco releases.
The source comments explain how the code works: `src/Patchsets/BackOfficeAssetPatches.cs`
explains the patch entries, and each test class says what it checks.

Each folder under `src` holds one part of the package, in a namespace named after the
folder. The root holds the public API: the composer and `VirtualPathHosting`. In the test
project, the quick tests sit at the root and the browser tests in `Browser`.

## Requirements

- The .NET 10 SDK and PowerShell 7.
- On Windows, a short clone path. Playwright's package nests deeply, and the build fails
  with `MSB3021` when a path exceeds 260 characters unless long paths are enabled.

## Build

```powershell
dotnet build tests/AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests
```

The tests build against the Umbraco release in `UmbracoVersion` in
[`Directory.Build.props`](../Directory.Build.props), the newest supported release. Add
`-p:UmbracoVersion=17.5.3` to build against another supported release.

Both projects generate XML documentation, so the compiler checks every doc comment. Keep
the build free of warnings. Outside a Git checkout, add
`-p:SourceTimestamp=2026-11-14T12:00:00Z`, because the build reads the commit time from
Git for the patched files' `Last-Modified` time.

To build a package:

```powershell
dotnet pack src -c Release -o artifacts/packages
```

A local package is numbered `<UmbracoMaximumVersion>.0`, which NuGet writes as `18.3.1`.
Published versions count from 1, so NuGet's cache keeps local and published packages
apart. [Version numbering and releases](VERSIONING.md) covers publishing.

## Quick tests

```powershell
dotnet test --project tests/AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests --no-build -- --filter-not-class '*SiteEndToEndTests'
```

The quick tests check each patch against the installed Umbraco files and Umbraco's
source maps, scan Umbraco's scripts for URLs that can lose the virtual path, and test
the middleware, file provider, Tag Helper and health check, all without starting a site.
Run one class with `-- --filter-class '*AssetPatchTests'`, or one test with
`--filter-method` and its name.

## Browser tests

Install Chromium once, and again after upgrading Playwright. Then run the browser cases:

```powershell
pwsh tests/AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests/bin/Debug/net10.0/playwright.ps1 install chromium
dotnet test --project tests/AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests --no-build -- --filter-class '*SiteEndToEndTests'
```

Each case installs a starter-kit site with its own SQLite database, starts it on a free
loopback port and drives it with Chromium. [Testing](FIXES.md#testing) lists the five
cases. After restore and the Chromium install, the tests run offline, and HTTPS cases
use disposable certificates that stay out of the machine's trust store.

- **Select cases** with `AVPF_E2E_CASE`, such as `$env:AVPF_E2E_CASE = 'iis,proxy'`.
  The cases are `full`, `iis`, `proxy`, `proxy-root` and `basic-auth`.
- **On Umbraco 17, run one case per process**, because Umbraco 17 keeps the first site's
  services for the whole process. Set `AVPF_E2E_CASE` to one case, or use
  [`Test-UmbracoReleases.ps1`](#test-other-umbraco-releases), which runs each case in
  its own process. The suite skips the other cases and says why.
- **Results** go to `e2e-artifacts/<case>/<run>` in the test project's output folder,
  `bin/Debug/net10.0`: the test output, a Playwright trace and the site's logs.
  `Test-UmbracoReleases.ps1` puts them under
  `artifacts/umbraco-releases/<release>/artifacts/bin/AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests/debug`.
  A failed step adds a screenshot and a memory dump of the test process, which hosts the
  site. Open a trace with `playwright.ps1 show-trace <trace.zip>`. If the site stopped
  answering, open the dump with `dotnet-dump analyze`, where `clrstack -all` and
  `dumpasync` show what it was waiting on.
- **Keep the sites** with `$env:AVPF_KEEP_TEST_SITE = '1'`. Each site's folder, with its
  database and Umbraco logs, stays in the temp directory.

## Test other Umbraco releases

[`tests/Test-UmbracoReleases.ps1`](../tests/Test-UmbracoReleases.ps1) builds the tests
for each release you name, then runs them in parallel:

```powershell
pwsh tests/Test-UmbracoReleases.ps1 -Releases 17.5.3,18.3.1
pwsh tests/Test-UmbracoReleases.ps1 -Releases 17.5.3,18.3.1 -Browser
```

Without `-Releases`, it tests every release in `UmbracoReleases`. `-Browser` runs the
browser cases instead of the quick tests and follows `AVPF_E2E_CASE`. `-Throttle` limits
how many test processes run at once, and `-SummaryPath results.json` also writes the
results as JSON. The script prints one line per release, lists the failed tests and
exits with 1 when any release fails.

Each release is tested when it is added. After that, test the releases a change can
affect:

- **A patch entry** affects the releases from its `From` up to its `Before`, or every
  release when it has neither.
- **Other package code** can affect every release. Spot-check one release of each major
  version, such as 17.5.3 and 18.3.1, with both kinds of tests. When the change calls
  Umbraco code the package has not called before, run the quick tests on every release:
  `SupportedReleasesTests` checks that each release has every type and member the
  package uses.
- **Workflow scripts** have [their own tests](#workflow-scripts).

## Support a new Umbraco release

The package supports an Umbraco release once `UmbracoReleases` lists it and both kinds of
tests pass on it.

### New Umbraco releases workflow

The **New Umbraco releases** workflow looks on NuGet every day for stable releases of the
supported major versions that are newer than the newest listed one, and runs both kinds
of tests on them:

- Releases that pass are listed on a branch named `umbraco-releases/<releases>`, with a
  pull request. Merge it, then [publish a release](VERSIONING.md#publish-a-release).
- A release that fails gets an issue titled `Umbraco <release> fails the tests`, which
  links the run. The run's artifacts hold the logs, traces and screenshots. Fix the
  patches as [Add a release by hand](#add-a-release-by-hand) describes. Closing the issue
  lets the next run test the release again.
- A stable release of a newer major version gets an issue titled
  `Support Umbraco <major>`, for the steps in
  [Support a new Umbraco major version](#support-a-new-umbraco-major-version).

The workflow opens pull requests once **Settings → Actions → General → Workflow
permissions → Allow GitHub Actions to create and approve pull requests** is on. Until
then, it pushes the branch and opens an issue that links it. To test chosen releases, or
to see the results without opening anything, run it from **Actions → New Umbraco
releases → Run workflow** with its `releases` and `dry-run` inputs.

### Add a release by hand

1. List the release:

   ```powershell
   pwsh .github/scripts/Add-UmbracoRelease.ps1 -Releases 18.3.2
   ```

   The script adds it to `UmbracoReleases`. When it is the newest release, the script
   also sets `UmbracoMaximumVersion` and `UmbracoVersion` to it, so the dependency range
   and the tests include it. Then it updates the supported releases in `README.md` and
   `docs/NUGET.md`.
2. Run the quick tests on it with `pwsh tests/Test-UmbracoReleases.ps1 -Releases 18.3.2`.
   Each failure names its patch entry, and `PatchOriginTests` says whether Umbraco
   changed the code or only its minified output.
3. Fix each entry that no longer matches, and leave the earlier releases' entries as
   they are:
   - For an entry in the major version's folder, such as `Umbraco18Patches.cs`, set its
     `Before` to the new release. Add a copy with `From` set to the new release and
     `Before` set to the next major version, such as `19.0.0`, and update the copy.
   - An entry in an area file applies to every major version. Move it into each major
     version's folder, limited to that version, and keep its edits in the area file for
     the copies to share. Then update the new release's copy as above.
4. Run the quick tests again, then the browser cases with
   `pwsh tests/Test-UmbracoReleases.ps1 -Releases 18.3.2 -Browser`. The earlier releases
   keep their entries, so they need no new run.

## Support a new Umbraco major version

Umbraco 19 runs on .NET 11, and NuGet picks a package's assembly by the site's .NET
version, so Umbraco 19 needs a second build of the package:

1. Install the .NET 11 SDK, and add it to the workflows' `setup-dotnet` steps.
2. Target `net10.0;net11.0` in the package project, with Umbraco 17 and 18 for .NET 10
   and Umbraco 19 for .NET 11. Make the test project target .NET 11 when `UmbracoVersion`
   is a 19 release, and add the Umbraco 19 starter kit to `Directory.Build.props`.
3. Add `src/Patchsets/Umbraco19/Umbraco19Patches.cs` and
   `src/UmbracoVersions/Umbraco19/Umbraco19Major.cs`, compiled into the .NET 11 build only.
   Add `Umbraco19Major` to `UmbracoMajor.All` under `#if NET11_0_OR_GREATER`. When Umbraco 19
   changes an API the package uses, add a member to `UmbracoMajor` and implement it in
   each version's class.
4. List the first 19 release in `UmbracoReleases` by hand, because the add script
   accepts only the major versions already listed. Add it to the supported releases in
   `docs/NUGET.md`, and add a row to the README's supported-versions table with an
   `<!-- umbraco-releases 19 -->` marker. Update the other text that names the supported
   major versions.
5. Continue from step 2 of [Add a release by hand](#add-a-release-by-hand).

## Workflow scripts

The workflows run the scripts in `.github/scripts`. Test them with Pester 5.7.1:

```powershell
Install-Module Pester -RequiredVersion 5.7.1 -Scope CurrentUser -Force -SkipPublisherCheck
Import-Module Pester -RequiredVersion 5.7.1
Invoke-Pester .github/scripts -CI
```

The tests work in disposable folders and Git repositories, so this repository and
GitHub stay untouched.

## Documents

- `docs/NUGET.md` is the package's page on nuget.org, which shows Markdown without HTML.
  `Add-UmbracoRelease.ps1` rewrites its list of supported releases after
  "running Umbraco".
- The README's supported-versions table marks each major version's releases with
  `umbraco-releases` comments, which `Add-UmbracoRelease.ps1` rewrites.
