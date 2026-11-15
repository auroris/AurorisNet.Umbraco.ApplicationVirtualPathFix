# Version numbering and releases

Choose a package release whose supported range includes the site's Umbraco release.
The first three numbers of a package version are the newest Umbraco release it
supports, and the fourth counts our releases for that Umbraco release. For example,
`AurorisNet.Umbraco.ApplicationVirtualPathFix` version `18.3.1.2` is our second
release with Umbraco 18.3.1 as its newest supported release, and it supports Umbraco
17.5.0 to 18.3.1.

## Package versions

The format is `<newest supported Umbraco release>.<release count>`.

| Part | Example | Controls |
|---|---|---|
| Newest supported Umbraco release | `18.3.1` | The upper end of the package's dependency range on `Umbraco.Cms.Web.Common` and `Umbraco.Cms.StaticAssets`. The lower end is the oldest supported release. |
| Release count | `2` | Which of our releases for that Umbraco release to install. |

The count starts at `1` for each newest supported release. A release that adds a
newer Umbraco release changes the first three numbers to that release and starts the
count again: after `18.3.1.2`, a release that adds Umbraco 18.3.2 is `18.3.2.1`. A
release that adds an older Umbraco release, such as a new 17.x, keeps the first three
numbers and takes the next count. A release can also move the lower end of the range;
the package's dependencies show it.

The package accepts only the Umbraco releases in its range, so restore fails with a
version conflict (NU1107) on any other release. A floating reference such as `18.*`
installs the newest package whose newest supported release is an 18.x, on Umbraco 17
as well as 18. If that package no longer supports the site's Umbraco release, restore
fails; reference the last package release that supports it instead. The [supported-version table](../README.md#supported-versions)
lists each range.

## Supported releases and branches

The `main` branch holds the source, tests, documentation, Marketplace listing metadata
and the Release workflow. `UmbracoReleases` in `Directory.Build.props` lists the
supported Umbraco releases, and `UmbracoMinimumVersion` and `UmbracoMaximumVersion`
set either end of the dependency range. Every listed release passed the tests when it
was added, and again after any change that might affect it, and the patch list in
`src/Patchsets` names the releases each change applies to. The [development guide](DEVELOPING.md#add-an-umbraco-release)
describes how to add an Umbraco release.

The [`umbraco/18.3.1`](https://github.com/auroris/UmbracoApplicationVirtualPathFix/tree/umbraco/18.3.1)
branch holds the source of `18.3.1.1`, our first release, which supported only
Umbraco 18.3.1. It receives no further changes.

## Release counts and tags

Open **Actions → Release → Run workflow** on GitHub and select `main`. Pushing to
`main` never starts a release. The workflow reads `UmbracoMaximumVersion` and, for a
new commit, chooses the count after the highest matching release tag. With no
matching tags, it starts at `1`. For example, a new release after `v18.3.1.1`
receives version `18.3.1.2`.

The tests run locally rather than in the workflow. Each supported Umbraco release
passes the fast and browser tests when it is added, and again after any change that
might affect it, as the [development guide](DEVELOPING.md#publish-a-release)
describes. The workflow builds the package and reserves the version tag at the
commit before publishing. It then
publishes to NuGet through trusted publishing and creates a GitHub release with the
package attached.

Retrying a tagged commit reuses that commit's reserved version so a partially
completed publication can finish. A new count is allocated only for the tip of
`main`, so re-running an older run cannot publish older code under a newer version,
and release runs are serialized to prevent competing selections. A reserved tag can
therefore leave a gap if a release is abandoned; preserve that tag so another commit
cannot reuse the version.

See the [development guide](DEVELOPING.md) for build commands and release setup.

## Marketplace listing

The package's NuGet metadata includes the `umbraco-marketplace` tag and the
repository's root URL as `PackageProjectUrl`. Umbraco Marketplace uses that URL
to find [`umbraco-marketplace.json`](../umbraco-marketplace.json) on `main`.
The JSON supplies the Developer Tools category, free license classification,
documentation link and issue tracker. The title, description and README come
from the NuGet package; compatibility comes from its Umbraco dependency range.

Push listing metadata changes to `main` before publishing a package release.
Changes to the NuGet tag, title or project URL require a new package release.
After publishing, use the [Marketplace validation tools](https://marketplace.umbraco.com/validate)
with package ID `AurorisNet.Umbraco.ApplicationVirtualPathFix` to check the
published listing inputs. See Umbraco's [listing guide](https://docs.umbraco.com/umbraco-dxp/marketplace/listing-your-package)
for synchronization and metadata requirements.
