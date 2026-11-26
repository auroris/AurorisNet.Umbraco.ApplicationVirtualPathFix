# Health checks, developing, and releases

## Health check messages

**Settings → Health Check → Configuration → Application virtual path** (and the startup log) report:

| Check | Warning / error | Action |
|---|---|---|
| Umbraco release | The package supports Umbraco *list*, but the site runs Umbraco *release*. | Install a package version that supports the site's Umbraco release. |
| Path base | Configured for `/SomeTestPath`, but request arrived with path base `""`. | Forward the prefix in the request path, or set `PathBase` via IIS or `X-Forwarded-Prefix`. |
| Patched files | Expected one file / patch did not apply. | Install a package version matching the site's Umbraco release. |
| Patched files | Backoffice files have not been patched yet. | Wait for startup to finish and re-run the check. |
| Requests outside the path | *n* requests reached the site outside the prefix. | Route only the prefix to the site (root load-balancer probes also count). |

If a missing fix shows up in browser DevTools as an unprefixed `/umbraco/...` request, check [FIXES.md](FIXES.md) and open an issue with the Umbraco and package versions.

Note: Outside the tested range, NuGet restore fails with `NU1107` (or warns `NU1608` if `Umbraco.Cms.Web.Common` / `Umbraco.Cms.StaticAssets` are referenced directly).

## Developing

Requires the .NET 10 SDK (`global.json`) and PowerShell 7. On Windows, clone to a short path unless long paths are enabled (Playwright builds deep paths in the test matrix).

```powershell
dotnet build tests/AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests
# Quick tests (newest supported Umbraco release)
dotnet test --project tests/AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests --no-build -- --filter-not-class '*SiteEndToEndTests'
# Install Chromium once after build / Playwright updates
pwsh tests/AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests/bin/Debug/net10.0/playwright.ps1 install chromium
# Browser E2E tests
dotnet test --project tests/AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests --no-build -- --filter-class '*SiteEndToEndTests'
# Full release matrix (runs locally, not on GitHub)
pwsh tests/Test-UmbracoReleases.ps1
pwsh tests/Test-UmbracoReleases.ps1 -Browser
pwsh tests/Test-UmbracoReleases.ps1 -Releases 17.5.3,18.3.1 -Browser
# Add an Umbraco release manually (requires sibling ../AurorisNet.Build)
pwsh ../AurorisNet.Build/scripts/Add-UmbracoRelease.ps1 -Releases 18.3.2
# Local package (count 0)
dotnet pack src/AurorisNet.Umbraco.ApplicationVirtualPathFix.csproj -c Release -o artifacts/packages
```

Useful test options:

- `-p:UmbracoVersion=17.5.3` builds against another listed release. Outside Git, pass `-p:SourceTimestamp=2026-11-14T12:00:00Z`.
- `AVPF_E2E_CASE` selects browser cases (`full`, `iis`, `proxy`, `proxy-root`, `basic-auth`). Run one case per process on Umbraco 17.
- `AVPF_KEEP_TEST_SITE=1` preserves temporary test sites; traces and logs go to `e2e-artifacts/<case>/<run>` in the test output folder.
- CI and Release treat warnings as errors.

## Fix a failing Umbraco release

1. Add the release with `Add-UmbracoRelease.ps1` and run `pwsh tests/Test-UmbracoReleases.ps1 -Releases <release>`. `PatchOriginTests` distinguishes Umbraco code changes from minification changes.
2. Keep earlier releases' patch entries intact: set `Before` on the existing entry to `<release>`, add a copy with `From = <release>` and `Before = <next major>`, and update the copy. For shared area files (e.g. `SignInPatches.cs`), split into per-major files while keeping shared edit helpers in the area file.
3. Re-run quick tests and `-Browser`, commit to `main`, close the failure issue, and run `release.yml`.

## Add an Umbraco major version

1. Update the .NET SDK in `global.json` and workflows if required.
2. Multi-target (e.g. `net10.0;net11.0`) in the package project, update the test project target and starter kit in `Directory.Build.props`.
3. Add `src/Patchsets/Umbraco<N>/` and `src/UmbracoVersions/Umbraco<N>/`, registering it in `UmbracoMajor.All`.
4. Add the first release to `UmbracoReleases`, `UmbracoMaximumVersion`, and `UmbracoVersion` in `Directory.Build.props`.
5. Add a matching `<!-- umbraco-releases <major> -->` line and badge update in `README.md`.
6. Run the tests and update patches as above.

## Releases

Versions follow `<newest supported Umbraco release>.<count>` (for example `18.3.2.1` and `17.8.2.1`). See the shared [release guide](https://github.com/auroris/AurorisNet.Build/blob/main/docs/releasing.md).
