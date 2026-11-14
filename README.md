<h1 align="center">Umbraco Application Virtual Path Fix</h1>

<p align="center">
  Backoffice fixes for Umbraco 18.3.1 sites hosted under a subdirectory.<br>
  Keep login, assets, dashboard requests and session renewal under paths such as <code>/SomeTestPath/umbraco</code>.
</p>

<p align="center">
  <a href="LICENSE"><img alt="MIT license" src="docs/images/badge-license.svg"></a>
  <img alt=".NET 10" src="docs/images/badge-dotnet-10.svg">
</p>

<p align="center">
  <a href="https://www.nuget.org/packages/AurorisNet.Umbraco.ApplicationVirtualPathFix/18.3.1.1">NuGet package</a> ·
  <a href="https://github.com/auroris/UmbracoApplicationVirtualPathFix/tree/main">All supported versions</a>
</p>

## What it does

`AurorisNet.Umbraco.ApplicationVirtualPathFix` lets a standard Umbraco 18.3.1 site
serve its backoffice under an application virtual path. Installing the package
registers its fixes through Umbraco's composer and middleware pipeline.

- **Keep sign-in inside the application path.** Login, management API calls,
  OAuth callbacks and password-reset and invitation links use the configured prefix.
- **Load backoffice and package assets.** Import maps, cache-busted scripts,
  stylesheets and the Umbraco logo resolve under the application path.
- **Serve media under the prefix**, including media stored outside `wwwroot` or in
  a provider such as Azure Blob Storage, with ImageSharp crops and resizes.
- **Restore an existing session.** Umbraco renews its token when a session may
  exist. A fresh browser starts sign-in directly, avoiding the initial
  token-refresh errors. This fix also applies to sites hosted at the root.

This branch targets **Umbraco 18.3.1 and .NET 10**. The package requires that exact
Umbraco version because some fixes modify the served upstream JavaScript.
See [Fixes and compatibility](docs/FIXES.md) for the affected behavior, hosting
requirements and validation scope.

## Quick start

In an Umbraco 18.3.1 website, add the package. The floating version picks up the
latest fix revision for Umbraco 18.3.1:

```xml
<PackageReference Include="AurorisNet.Umbraco.ApplicationVirtualPathFix" Version="18.3.1.*" />
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

## Documentation

| Guide | Read it for |
|---|---|
| [Fixes and compatibility](docs/FIXES.md) | The Umbraco 18.3.1 behavior each fix changes, hosting requirements and browser-test coverage. |
| [Developing and releasing](docs/DEVELOPING.md) | Building this branch, running Playwright, packing and publishing a revision for Umbraco 18.3.1. |
| [Package version numbering](https://github.com/auroris/UmbracoApplicationVirtualPathFix/blob/main/docs/VERSIONING.md) | How Umbraco versions, package revisions, branches and release tags relate. |

## Developing

Check out `umbraco/18.3.1` and open `UmbracoApplicationVirtualPathFix.slnx` with the
.NET 10 SDK installed. The solution contains the package and its standalone
Umbraco browser tests. Build and test commands are in
[Developing and releasing](docs/DEVELOPING.md).

Package versions on this branch follow `18.3.1.<revision>`. Revision 1 is
`18.3.1.1`; the release workflow selects the next revision automatically. The
package's nuget.org page shows [docs/NUGET.md](docs/NUGET.md) rather than this
README, because nuget.org renders neither HTML nor relative links.

## License

[MIT](LICENSE)
