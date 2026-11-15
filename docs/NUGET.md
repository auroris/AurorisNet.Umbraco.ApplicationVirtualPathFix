# Umbraco Application Virtual Path Fix

Backoffice fixes for Umbraco 17 and 18 sites hosted under a subdirectory, such as
`/SomeTestPath/umbraco`. Sign-in, backoffice assets, media, dashboard requests and
session renewal stay under the site's application path.

## What it does

- **Sign-in stays inside the path.** Login, management API calls, OAuth callbacks, and
  password-reset and invitation links use the prefix.
- **Backoffice assets load.** Import maps, scripts, stylesheets and the logo resolve
  under the path.
- **Media is served under the prefix**, including media outside `wwwroot` or in a
  provider such as Azure Blob Storage, with ImageSharp crops and resizes.
- **Sessions restore cleanly.** A fresh browser starts sign-in directly, avoiding
  Umbraco's initial token-refresh errors. This also helps sites at the root.
- **A health check reports the status** under Settings → Health Check.

## Quick start

Add the package to a website running Umbraco 18.0.0 to 18.3.1 or 17.5.0 to 17.8.1.
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

Start the website and open `/SomeTestPath/umbraco`.

## Versions

A package version is the newest Umbraco release it supports, followed by our release
count: 18.3.1.2 supports every Umbraco 18 release up to 18.3.1, and 17.8.1.1 supports
Umbraco 17.5.0 up to 17.8.1. NuGet's dependency range spans the oldest and newest
supported releases. Check the
[supported versions](https://github.com/auroris/UmbracoApplicationVirtualPathFix#supported-versions)
to see whether your Umbraco release is tested and supported, and upgrade the package
when you upgrade Umbraco.

## Documentation

- [Fixes and compatibility](https://github.com/auroris/UmbracoApplicationVirtualPathFix/blob/main/docs/FIXES.md)
- [Version numbering and releases](https://github.com/auroris/UmbracoApplicationVirtualPathFix/blob/main/docs/VERSIONING.md)
- [Issue tracker](https://github.com/auroris/UmbracoApplicationVirtualPathFix/issues)

Licensed under the [MIT license](https://github.com/auroris/UmbracoApplicationVirtualPathFix/blob/main/LICENSE).
