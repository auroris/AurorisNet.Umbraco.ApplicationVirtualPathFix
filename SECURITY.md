# Security

## Report a vulnerability

Report a vulnerability in private, with
[GitHub's vulnerability report form](https://github.com/auroris/UmbracoApplicationVirtualPathFix/security/advisories/new).
You also reach it from this repository's **Security** tab, under **Report a vulnerability**.
Please keep the details out of public issues until a fix is published.

A report is easier to act on when it includes:

- the package version and the site's Umbraco release;
- how the site is hosted: its application virtual path, and whether IIS, Kestrel or a
  reverse proxy serves it;
- the steps that show the problem, and what someone gains by it.

## Versions that get fixes

A fix is published as a new package version for each supported Umbraco major version.
The [supported-versions table](README.md#supported-versions) lists them. Earlier package
versions stay as they are, so install the newest version for your site's Umbraco major
version.

## What the package covers

The package changes how Umbraco's backoffice builds its URLs: it patches the scripts
Umbraco serves, wraps Umbraco's sign-in module and adds a health check. A vulnerability
in any of that belongs here. For a vulnerability in Umbraco itself, follow
[Umbraco's security policy](https://github.com/umbraco/Umbraco-CMS/security/policy).
