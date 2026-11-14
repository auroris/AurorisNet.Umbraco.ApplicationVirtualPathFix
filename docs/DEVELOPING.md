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

Repeat the browser installation after upgrading Playwright. The tests create
their own SQLite databases and start Kestrel on free loopback ports. See
[Browser validation](FIXES.md#browser-validation) for the checks and their scope.

To build a package locally:

```powershell
dotnet pack src/AurorisNet.Umbraco.ApplicationVirtualPathFix -c Release -o artifacts/packages
```

Local builds use version `18.3.1.1`. The release workflow supplies its selected
revision as the MSBuild `Version` property when building and packing.

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
publication can finish. Release runs on this branch are serialized to keep
revision selection consistent. Ordinary pushes update the branch; publishing
starts when a maintainer runs Release.

The `main` branch keeps the workflow available in GitHub's Actions interface.
Selecting `umbraco/18.3.1` runs the workflow with this branch's source, scripts,
tests and documentation. The
[version-numbering reference](https://github.com/auroris/UmbracoApplicationVirtualPathFix/blob/main/docs/VERSIONING.md)
explains the branch and tag conventions across supported Umbraco versions.
