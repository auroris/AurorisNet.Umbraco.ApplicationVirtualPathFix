<h1 align="center">Umbraco Application Virtual Path Fix</h1>

<p align="center">
  Backoffice fixes for Umbraco 18 sites hosted under a subdirectory.<br>
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
backoffice under an application virtual path. Installing the package registers its
fixes through Umbraco's composer and middleware pipeline.

- **Keep sign-in inside the application path.** Login, management API calls,
  OAuth callbacks and password-reset and invitation links use the configured prefix.
- **Load backoffice and package assets.** Import maps, cache-busted scripts,
  stylesheets and the Umbraco logo resolve under the application path.
- **Serve media under the prefix**, including media stored outside `wwwroot` or in
  a provider such as Azure Blob Storage, with ImageSharp crops and resizes.
- **Restore an existing session.** Umbraco renews its token when a session may
  exist. A fresh browser starts sign-in directly, avoiding the initial
  token-refresh errors. This fix also applies to sites hosted at the root.
- **Report its status in the backoffice.** A health check under Settings → Health
  Check shows whether every fix applied and how requests reach the site.

Some fixes modify the JavaScript that Umbraco serves, so each package release
supports a range of Umbraco releases that its tests have passed. See
[Fixes and compatibility](docs/FIXES.md) for the affected behavior, hosting
requirements and validation scope.

## Supported versions

| Package release | Umbraco | Runtime |
|---|---|---|
| 18.3.1.2 and later | 18.2.0 to 18.3.1 | .NET 10 |
| [18.3.1.1](https://www.nuget.org/packages/AurorisNet.Umbraco.ApplicationVirtualPathFix/18.3.1.1) | 18.3.1 | .NET 10 |

On any Umbraco release outside a package's range, restore fails with a version
conflict. The source of every release from 18.3.1.2 on is on `main`; the
[`umbraco/18.3.1`](https://github.com/auroris/UmbracoApplicationVirtualPathFix/tree/umbraco/18.3.1)
branch holds the source of 18.3.1.1.

## Quick start

Add the package to a website running Umbraco 18.2.0 to 18.3.1. The floating version
picks up the newest release for Umbraco 18:

```xml
<PackageReference Include="AurorisNet.Umbraco.ApplicationVirtualPathFix" Version="18.*" />
```

Set the site's application path in `appsettings.json`:

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

Start the website and open `/SomeTestPath/umbraco`. Standard Umbraco startup with
`.AddComposers()` loads the package automatically. Kestrel applies the prefix as
`Request.PathBase`; an existing path base supplied by IIS is preserved. For an
IIS child application, match the setting to its application path. Sites hosted
at the root can leave `ApplicationVirtualPath` unset.

## Version numbering

Package versions use **`<newest supported Umbraco release>.<release count>`**. The
first three numbers are the newest Umbraco release the package supports, and its
dependencies show the oldest. The fourth number counts our releases for that Umbraco
release, starting at 1.

| Package version | Meaning |
|---|---|
| `18.3.1.1` | Our first release with Umbraco 18.3.1 as the newest supported release. It supports only 18.3.1. |
| `18.3.1.2` | Our second release with 18.3.1 as the newest supported release. It supports 18.2.0 to 18.3.1. |
| `18.3.2.1` | Our first release that adds a newer Umbraco release, such as 18.3.2. The count starts again. |

The Release workflow chooses the next release count and creates a tag such as
`v18.3.1.2`. See [Version numbering and releases](docs/VERSIONING.md) for how
releases are tested, numbered and published.

## Documentation

| Guide | Read it for |
|---|---|
| [Fixes and compatibility](docs/FIXES.md) | The Umbraco behavior each fix changes, hosting requirements and browser-test coverage. |
| [Developing and releasing](docs/DEVELOPING.md) | Building the package, testing every supported Umbraco release, and packing and publishing a release. |
| [Version numbering and releases](docs/VERSIONING.md) | How package versions relate to Umbraco releases, and how the Release workflow numbers and publishes them. |

## Developing

Open `UmbracoApplicationVirtualPathFix.slnx` with the .NET 10 SDK installed. The
solution contains the package and its standalone Umbraco browser tests. Build and
test commands are in [Developing and releasing](docs/DEVELOPING.md). The package's
nuget.org page shows [docs/NUGET.md](docs/NUGET.md) rather than this README, because
nuget.org renders neither HTML nor relative links.

## License

[MIT](LICENSE)
