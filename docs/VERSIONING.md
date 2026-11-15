# Version numbering and releases

A package version is `<newest supported Umbraco release>.<release count>`. Version
`18.3.1.2` supports every Umbraco 18 release up to 18.3.1, and it is our second release
for 18.3.1.

## One version per Umbraco major version

Each release publishes one version for each supported Umbraco major version, built from
the same source:

| Version | Supports |
|---|---|
| `18.3.1.2` | Every Umbraco 18 release up to 18.3.1 |
| `17.8.1.1` | Umbraco 17.5.0 up to 17.8.1 |

Reference your site's major version, such as `18.*` or `17.*`, so NuGet installs the
newest package for it. Both versions of a release depend on the whole supported range,
from the oldest supported Umbraco release to the newest, so either one installs on
Umbraco 17 or 18. The [supported-versions table](../README.md#supported-versions) lists
the tested releases.

## Release counts

The count starts at 1 for each Umbraco release, and each major version counts on its
own:

- Adding Umbraco 18.3.2 makes the next Umbraco 18 version `18.3.2.1`. The Umbraco 17
  version of the same release takes its next count, `17.8.1.2`.
- Adding Umbraco 17.8.2 makes the next Umbraco 17 version `17.8.2.1`. The Umbraco 18
  version takes its next count.

## Publish a release

Open **Actions → Release → Run workflow** on GitHub and select `main`. Pushes never start
a release. The workflow runs no tests, because each Umbraco release was tested when it
was added, as [Developing](DEVELOPING.md) describes. It:

1. chooses each version's count from the release tags;
2. builds and packs each version;
3. reserves a tag for each, such as `v18.3.1.2` and `v17.8.1.1`, before publishing any;
4. publishes each package to NuGet and creates a GitHub release for each. The Umbraco 18
   release is marked as the latest.

Running Release again for a tagged commit reuses its tags, so a partial publication can
finish. A new count goes only to the tip of `main`. Keep every release tag, including
`v18.3.1.1`, whose package is deprecated: NuGet keeps every version it has published, so
the workflow must count past it.

NuGet trusted publishing is configured for the `AurorisNet` account, the repository
`auroris/UmbracoApplicationVirtualPathFix`, the workflow `release.yml` and no
environment. See [NuGet trusted publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing).

## Marketplace listing

The package carries the `umbraco-marketplace` tag, and Umbraco Marketplace reads
[`umbraco-marketplace.json`](../umbraco-marketplace.json) from `main` for its category,
license, documentation link and issue tracker. Push changes to that file before
releasing. After publishing, check the listing with the
[Marketplace validation tools](https://marketplace.umbraco.com/validate) and package ID
`AurorisNet.Umbraco.ApplicationVirtualPathFix`.
