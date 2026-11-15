<h1 align="center">Umbraco Application Virtual Path Fix</h1>

<p align="center">
  Backoffice fixes for Umbraco 17 and 18 sites hosted under a subdirectory.<br>
  Keep login, assets, dashboard requests and session renewal under paths such as <code>/SomeTestPath/umbraco</code>.
</p>

<p align="center">
  <a href="LICENSE"><img alt="MIT license" src="docs/images/badge-license.svg"></a>
  <img alt=".NET 10" src="docs/images/badge-dotnet-10.svg">
</p>

<p align="center">
  <a href="https://www.nuget.org/packages/AurorisNet.Umbraco.ApplicationVirtualPathFix">NuGet package</a> ·
  <a href="#supported-versions">Supported versions</a> ·
  <a href="#version-numbering">Version numbering</a>
</p>

## What it does

`AurorisNet.Umbraco.ApplicationVirtualPathFix` lets a standard Umbraco site serve its
backoffice under an application virtual path. It registers itself through Umbraco's
composers.

- **Sign-in stays inside the path.** Login, management API calls, OAuth callbacks, and
  password-reset and invitation links use the prefix.
- **Backoffice assets load.** Import maps, scripts, stylesheets and the logo resolve
  under the path.
- **Media is served under the prefix**, including media outside `wwwroot` or in a
  provider such as Azure Blob Storage, with ImageSharp crops and resizes.
- **Sessions restore cleanly.** A fresh browser starts sign-in directly, avoiding
  Umbraco's initial token-refresh errors. This also helps sites at the root.
- **A health check reports the status** under Settings → Health Check.

Some fixes patch the JavaScript that Umbraco serves, so each package version supports
the Umbraco releases its tests have passed. [Fixes and compatibility](docs/FIXES.md)
describes each fix and the hosting requirements.

## Supported versions

| Umbraco | Tested and supported releases | Package versions | Runtime |
|---|---|---|---|
| 18 | <!-- umbraco-releases 18 -->18.0.0 to 18.3.1<!-- /umbraco-releases --> | `18.*` | .NET 10 |
| 17 | <!-- umbraco-releases 17 -->17.5.0 to 17.8.1<!-- /umbraco-releases --> | `17.*` | .NET 10 |

[`UmbracoReleases`](Directory.Build.props) lists each tested release. NuGet's
dependency range spans the oldest and newest of these. Check this table to see whether
your Umbraco release is tested and supported.

## Quick start

Add the package to a website running a [supported Umbraco release](#supported-versions).
Reference your site's Umbraco major version, so NuGet installs the newest package for it:

```xml
<PackageReference Include="AurorisNet.Umbraco.ApplicationVirtualPathFix" Version="18.*" />
```

On Umbraco 17, use `Version="17.*"`. Then set the site's application path in
`appsettings.json`:

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

Start the website and open `/SomeTestPath/umbraco`. For an IIS child application,
set `ApplicationVirtualPath` to the application's path.

## Version numbering

A package version is **`<newest supported Umbraco release>.<release count>`**. Version
`18.3.1.2` supports every Umbraco 18 release up to 18.3.1, and it is our second release
for 18.3.1.

| Package version | Supports | Release count |
|---|---|---|
| `18.3.1.2` | Every Umbraco 18 release up to 18.3.1 | Our second release for 18.3.1 |
| `17.8.1.1` | Umbraco 17.5.0 up to 17.8.1 | Our first release for 17.8.1 |
| `18.3.2.1` | Every Umbraco 18 release up to 18.3.2 | Our first release for 18.3.2, after adding it |

Each release publishes one version for each supported Umbraco major version, built from
the same source: the release that publishes `18.3.1.2` also publishes `17.8.1.1`.
[Version numbering and releases](docs/VERSIONING.md) explains the counts and how a
release is published.

## Documentation

| Guide | Read it for |
|---|---|
| [Fixes and compatibility](docs/FIXES.md) | What each fix changes, hosting requirements and test coverage. |
| [Developing](docs/DEVELOPING.md) | Building, testing and adding Umbraco releases. |
| [Version numbering and releases](docs/VERSIONING.md) | How versions are numbered and how a release is published. |

## License

[MIT](LICENSE)
