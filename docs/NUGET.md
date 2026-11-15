# Umbraco Application Virtual Path Fix

Backoffice fixes for Umbraco 18.3.1 sites hosted under a subdirectory, such as
`/SomeTestPath/umbraco`. Sign-in, backoffice assets, media, dashboard requests and
session renewal stay under the site's application path.

## What it does

- **Keeps sign-in inside the application path.** Login, management API calls,
  OAuth callbacks and password-reset and invitation links use the configured prefix.
- **Loads backoffice and package assets.** Import maps, cache-busted scripts,
  stylesheets and the Umbraco logo resolve under the application path.
- **Serves media under the prefix**, including media stored outside `wwwroot` or in
  a provider such as Azure Blob Storage, with ImageSharp crops and resizes.
- **Restores existing sessions.** Umbraco renews its token when a session may exist.
  A fresh browser starts sign-in directly, avoiding the initial token-refresh errors.
  This also applies to sites hosted at the root.
- **Reports its status in the backoffice.** A health check under Settings → Health
  Check shows whether every fix applied and how requests reach the site.

## Quick start

Add the package to an Umbraco 18.3.1 website. The floating version picks up the
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
`.AddComposers()` loads the package automatically. Sites hosted at the root can leave
`ApplicationVirtualPath` unset.

## Versions

The first three numbers of the package version are the Umbraco version it supports,
and the fourth counts fix revisions for that version. Some fixes patch the JavaScript
that Umbraco 18.3.1 serves, so the package depends on that exact version: upgrading
Umbraco without the matching package release makes restore fail with a version
conflict. Upgrade both together.

## Documentation

- [Fixes, hosting requirements and compatibility](https://github.com/auroris/UmbracoApplicationVirtualPathFix/blob/umbraco/18.3.1/docs/FIXES.md)
- [Supported Umbraco versions](https://github.com/auroris/UmbracoApplicationVirtualPathFix#supported-versions)
- [Version numbering and releases](https://github.com/auroris/UmbracoApplicationVirtualPathFix/blob/main/docs/VERSIONING.md)
- [Issue tracker](https://github.com/auroris/UmbracoApplicationVirtualPathFix/issues)

Licensed under the [MIT license](https://github.com/auroris/UmbracoApplicationVirtualPathFix/blob/umbraco/18.3.1/LICENSE).
