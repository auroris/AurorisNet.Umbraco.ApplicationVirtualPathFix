# AurorisNet.Umbraco.ApplicationVirtualPathFix

Fixes Umbraco 18.3.1 backoffice hosting under an application virtual path,
including login, management API URLs, import maps, assets and the header logo.
It also avoids the initial token-refresh errors on a fresh browser session,
both at the root and under a virtual path. Umbraco still validates and renews
tokens; the package exposes only the presence of its HTTP-only refresh cookie.

This is an independent MIT-licensed package. It depends only on Umbraco and
ASP.NET Core; UmbracoAppHost, AppBlocks and IdentityServer are not required.

## Installation

```sh
dotnet add package AurorisNet.Umbraco.ApplicationVirtualPathFix --version 18.3.1.1
```

Use Umbraco's existing setting in `appsettings.json`:

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

Then open `/SomeTestPath/umbraco`. The composer and pipeline filter register
automatically with the standard Umbraco `.AddComposers()` startup; no additional
`Program.cs` calls or package settings are needed. For root hosting, omit the
setting. Kestrel uses `UsePathBase`; an existing IIS child-application path base
is preserved. This package does not configure IIS bindings or reverse proxies.

## Compatibility

This branch targets .NET 10 and **Umbraco 18.3.1 only**. The exact NuGet
dependency is intentional: some fixes patch the served upstream JavaScript
bundle. Verify the browser tests and adjust those patches before supporting a
different Umbraco version. Patched asset URLs change with each package build,
and backoffice HTML is not cached because cookie presence varies per request.

The package does not rewrite URLs in arbitrary third-party plugins. Those
plugins must respect the configured virtual path themselves.

## Building and testing from this repository

Open `UmbracoApplicationVirtualPathFix.slnx`, or run the commands below from the
repository root with the .NET 10 SDK. No sibling repositories are needed.

```powershell
dotnet build tests/AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests
pwsh tests/AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests/bin/Debug/net10.0/playwright.ps1 install chromium
dotnet test --project tests/AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests
dotnet pack src/AurorisNet.Umbraco.ApplicationVirtualPathFix -c Release -o artifacts/packages
```

The standalone tests use plain Umbraco with SQLite and Kestrel, exercise login
and session renewal at the root and under a subdirectory, and fail on browser
console errors or uncaught JavaScript exceptions. AppHost retains its separate
browser test, including its welcome dashboard's report and pagination.

## Releases

Each `umbraco/<version>` branch supports one exact Umbraco version, configured
once in `Directory.Build.props`. Package versions add a local revision:
`18.3.1.1`, `18.3.1.2`, and so on. Revisions start at 1 for each Umbraco version.
The library dependency and the browser tests use that same configured version.

After publishing the repository to GitHub, keep `release.yml` on the default
branch so GitHub displays **Actions → Release → Run workflow**. Select the
matching version branch, initially `umbraco/18.3.1`. The workflow:

1. Chooses the next revision from that Umbraco version's release tags.
2. Builds and runs the Playwright tests before packing the NuGet package.
3. Reserves its `v18.3.1.1`-style tag at the tested commit.
4. Publishes to NuGet as `AurorisNet` through OIDC trusted publishing, then creates
   a GitHub release with the package attached.

Re-running a tagged commit reuses its version, allowing a failed
publication to finish. The tag is reserved before publishing so a different
commit cannot reuse that package version. Releases on the same branch run one
at a time. Nothing publishes automatically on a normal push.

The NuGet trusted publishing policy must name the GitHub repository and the
workflow filename **`release.yml`**, with no environment. The workflow uses
`NuGet/login` to obtain a short-lived API key; no stored NuGet API key is needed.
See [NuGet trusted publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing).

To support another Umbraco version, create its matching branch, update
`UmbracoVersion`, check the upstream patches and run the browser tests before
releasing. Local builds default to `<UmbracoVersion>.1`; release builds supply
the automatically selected revision without committing version bumps.
