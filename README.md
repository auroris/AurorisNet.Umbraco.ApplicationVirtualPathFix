# Application Virtual Path Fix

Lets Umbraco 17 and 18 sites run under a virtual path (such as an IIS child application at `/SomeTestPath`) by fixing backoffice assets, media URLs, preview, login, and initial token refresh.

![MIT license](https://raw.githubusercontent.com/auroris/AurorisNet.Build/main/badges/license-mit.svg)
![.NET 10](https://raw.githubusercontent.com/auroris/AurorisNet.Build/main/badges/dotnet-10.svg)
![Umbraco 17 and 18](https://raw.githubusercontent.com/auroris/AurorisNet.Build/main/badges/umbraco-17-18.svg)

At startup, the package patches Umbraco's backoffice scripts/stylesheets in memory, wraps Umbraco's import-map, media URL, and login-link services, and normalizes request path base in middleware. Sites hosted at `/` also get the initial token-refresh fix so fresh browsers go straight to sign-in.

## Install

Add the package matching your Umbraco major version (`18.*` or `17.*`). It registers automatically via a composer.

```xml
<PackageReference Include="AurorisNet.Umbraco.ApplicationVirtualPathFix" Version="18.*" />
```

Set the virtual path in `appsettings.json`, then open `/SomeTestPath/umbraco`:

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

## Hosting notes

- **Path prefix:** Requests must arrive with the prefix in the path, or with `PathBase` already set by IIS or `UseForwardedHeaders` (`X-Forwarded-Prefix`). Route only the prefix to the site.
- **Behind a reverse proxy:** Apply `X-Forwarded-Host` and `X-Forwarded-Proto` with `UseForwardedHeaders` before `UseUmbraco()`, or set `Umbraco:CMS:WebRouting:UmbracoApplicationUrl`.
- **Response compression:** Register `UseResponseCompression()` before `UseUmbraco()`.
- **Multiple Umbraco sites on one host:** Give each site unique `Umbraco:CMS:Security:BackOfficeTokenCookie:SiteName` and `Umbraco:CMS:Security:AuthCookieName` values.
- **Health check:** Check **Settings → Health Check → Configuration → Application virtual path** to verify patches and path base.
- **Startup helper:** `VirtualPathHosting.Prefix(builder.Configuration)` returns the normalized prefix (e.g. `/SomeTestPath`) or `null` at root.

## Supported versions

- Umbraco 18: <!-- umbraco-releases 18 -->18.0.0 to 18.3.2<!-- /umbraco-releases --> (.NET 10)
- Umbraco 17: <!-- umbraco-releases 17 -->17.5.0 to 17.8.2<!-- /umbraco-releases --> (.NET 10)

Upgrade this package whenever you upgrade Umbraco—each supported release is explicitly tested.

## Documentation

- [What the package fixes](https://github.com/auroris/AurorisNet.Umbraco.ApplicationVirtualPathFix/blob/main/docs/FIXES.md)
- [Health check messages, developing, and releases](https://github.com/auroris/AurorisNet.Umbraco.ApplicationVirtualPathFix/blob/main/docs/developing.md)

## License

[MIT](https://github.com/auroris/AurorisNet.Umbraco.ApplicationVirtualPathFix/blob/main/LICENSE)
