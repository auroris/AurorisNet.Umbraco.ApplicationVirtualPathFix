# Version numbering and releases

Choose a package whose first three version numbers match the site's Umbraco
version. The fourth number identifies a release of our fixes for that version.
For example, `AurorisNet.Umbraco.ApplicationVirtualPathFix` version `18.3.1.2`
targets Umbraco 18.3.1 and is fix revision 2.

## Package versions

The format is `<Umbraco major>.<Umbraco minor>.<Umbraco patch>.<fix revision>`.

| Part | Example | Controls |
|---|---|---|
| Umbraco version | `18.3.1` | The exact `Umbraco.Cms` dependency supported by the package. |
| Fix revision | `2` | Which release of the fixes to install for that Umbraco version. |

Revisions start at `1` for each Umbraco version and advance independently. The
sequence `18.3.1.1`, `18.3.1.2`, `18.3.1.3` represents successive releases for
Umbraco 18.3.1. Each of those packages retains the exact `18.3.1` dependency.

When upgrading Umbraco, select the corresponding package release as part of the
same update. Upstream JavaScript and hosting behavior can change between
Umbraco releases, so the package's fixes must be checked for that version.
The [supported-version table](../README.md#supported-versions) links to the
available source branches and their installation instructions.

## Branches and documentation

The `main` branch provides the project overview, shared documentation and the
Release workflow. Source, tests, build settings and implementation documentation
live on `umbraco/<version>` branches, beginning with
[`umbraco/18.3.1`](https://github.com/auroris/UmbracoApplicationVirtualPathFix/tree/umbraco/18.3.1).

Each version branch sets `UmbracoVersion` in `Directory.Build.props`. The package
and its tests read that value, and the release script checks that it matches
the branch name. That branch's README describes the applicable installation,
behavior, requirements and validation scope.

## Release revisions and tags

Open **Actions → Release → Run workflow** and select the intended version
branch. Keeping `release.yml` on `main` makes the workflow available in GitHub's
Actions interface; selecting the version branch supplies its source and scripts.

For a new commit, the workflow chooses the next revision after the highest
matching release tag. With no matching tags, it starts at revision `1`. For
example, a new release after `v18.3.1.9` receives version `18.3.1.10`.

The workflow builds the package, runs the browser tests and reserves the version
tag at the tested commit before publishing. It then publishes to NuGet through
trusted publishing and creates a GitHub release with the package attached.
Publishing starts when a maintainer runs Release; an ordinary push updates the
branch without starting a release.

Retrying a tagged commit reuses that commit's reserved version so a partially
completed publication can finish. A different commit receives a new revision,
and release runs on the same branch are serialized to prevent competing
revision selections. A reserved tag can therefore leave a gap if a release is
abandoned; preserve that tag so another commit cannot reuse the version.

Follow the version branch's development guide for build commands and release
setup. The [Umbraco 18.3.1 guide](https://github.com/auroris/UmbracoApplicationVirtualPathFix/blob/umbraco/18.3.1/docs/DEVELOPING.md)
covers the first supported version.
