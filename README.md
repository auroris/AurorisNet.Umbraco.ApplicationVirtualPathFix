<h1 align="center">Umbraco Application Virtual Path Fix</h1>

<p align="center">
  Umbraco backoffice fixes for sites hosted under a subdirectory.<br>
  Keep login, assets, dashboard requests and session renewal under the site's application path.
</p>

<p align="center">
  <a href="LICENSE"><img alt="MIT license" src="docs/images/badge-license.svg"></a>
  <img alt=".NET 10" src="docs/images/badge-dotnet-10.svg">
</p>

<p align="center">
  <a href="https://www.nuget.org/packages/AurorisNet.Umbraco.ApplicationVirtualPathFix">NuGet package</a> ·
  <a href="#supported-versions">Source and version guides</a> ·
  <a href="#version-numbering">Version numbering</a>
</p>

## What it does

`AurorisNet.Umbraco.ApplicationVirtualPathFix` lets an Umbraco website serve its
backoffice under an application virtual path such as `/SomeTestPath/umbraco`.
The package registers through standard Umbraco startup and uses the existing
`Umbraco:CMS:Hosting:ApplicationVirtualPath` setting.

- **Keep sign-in inside the application path.** Login, management API calls and
  OAuth callbacks use the configured prefix.
- **Load the backoffice assets and dashboard.** Import maps, scripts, stylesheets
  and the Umbraco logo resolve under that prefix.
- **Renew existing sessions.** Umbraco validates and refreshes its tokens. A fresh
  browser starts sign-in directly, avoiding the initial token-refresh errors.

Each release targets one exact Umbraco version because some fixes modify the
JavaScript served by that version. Choose the package that matches the site.

## Supported versions

Find the source code, tests and implementation documentation on the matching
`umbraco/<version>` branch. Each branch's README and documentation describe that
Umbraco version. The `main` branch contains this overview, shared documentation
and the Release workflow.

| Umbraco | Runtime | Source | Version guide | First package release |
|---|---|---|---|---|
| 18.3.1 | .NET 10 | [umbraco/18.3.1](https://github.com/auroris/UmbracoApplicationVirtualPathFix/tree/umbraco/18.3.1) | [Installation and documentation](https://github.com/auroris/UmbracoApplicationVirtualPathFix/blob/umbraco/18.3.1/README.md) | [18.3.1.1](https://www.nuget.org/packages/AurorisNet.Umbraco.ApplicationVirtualPathFix/18.3.1.1) |

## Quick start

For an Umbraco 18.3.1 website, install its matching package:

```sh
dotnet add package AurorisNet.Umbraco.ApplicationVirtualPathFix --version 18.3.1.1
```

Set `Umbraco:CMS:Hosting:ApplicationVirtualPath` to the site's path, such as
`/SomeTestPath`, then open `/SomeTestPath/umbraco`. Standard Umbraco startup with
`.AddComposers()` loads the fixes automatically. Follow the
[Umbraco 18.3.1 quick start](https://github.com/auroris/UmbracoApplicationVirtualPathFix/blob/umbraco/18.3.1/README.md#quick-start)
for the complete configuration.

## Version numbering

Package versions use **`<Umbraco version>.<fix revision>`**. The first three
numbers identify the exact Umbraco version; the fourth counts our releases for
that version.

| Package version | Meaning |
|---|---|
| `18.3.1.1` | Fix revision 1 for Umbraco 18.3.1. |
| `18.3.1.2` | Fix revision 2 for the same Umbraco version. |

Revisions start at `1` for each Umbraco version. A newer fix revision continues
to target the same Umbraco version. When upgrading Umbraco, choose a package
whose first three numbers match the new version.

The Release workflow selects the next revision and creates a corresponding
tag, such as `v18.3.1.1`. See [Version numbering and releases](docs/VERSIONING.md)
for branch conventions and retry behavior.

## Documentation

| Guide | Read it for |
|---|---|
| [Version numbering and releases](docs/VERSIONING.md) | Matching package versions to Umbraco, choosing a branch and publishing a revision. |
| [Umbraco 18.3.1 fixes and compatibility](https://github.com/auroris/UmbracoApplicationVirtualPathFix/blob/umbraco/18.3.1/docs/FIXES.md) | Corrected behavior, hosting requirements and browser-test coverage for 18.3.1. |
| [Developing for Umbraco 18.3.1](https://github.com/auroris/UmbracoApplicationVirtualPathFix/blob/umbraco/18.3.1/docs/DEVELOPING.md) | Building the package, running Playwright and releasing changes from that branch. |

## License

[MIT](LICENSE)
