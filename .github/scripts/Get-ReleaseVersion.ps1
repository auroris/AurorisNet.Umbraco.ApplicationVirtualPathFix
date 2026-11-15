<#
.SYNOPSIS
Chooses the package version and release tag for the next release from main.

.DESCRIPTION
Reads UmbracoMaximumVersion, the newest Umbraco release the package supports, from Directory.Build.props, and checks
that the release runs from main and that the checkout is the commit being released. The package version is that
Umbraco release followed by a release count. A commit that already carries a release tag reuses that count, so a
retried release can finish. Any other commit must be the tip of main on origin, and it gets the count after the highest
existing tag for the same Umbraco release, starting at 1.

.PARAMETER BranchName
The branch being released, which must be main.

.PARAMETER Commit
The full hash of the commit being released. It must match the checked-out HEAD.

.OUTPUTS
An object with Version, such as 18.3.1.2, and Tag, such as v18.3.1.2.
#>
param(
    [Parameter(Mandatory)][string] $BranchName,
    [Parameter(Mandatory)][string] $Commit
)

$ErrorActionPreference = 'Stop'
[xml] $settings = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../../Directory.Build.props')
$umbracoVersion = [string] $settings.Project.PropertyGroup.UmbracoMaximumVersion
if ($umbracoVersion -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
    throw 'UmbracoMaximumVersion must contain exactly three numeric components.'
}
if ($BranchName -cne 'main') {
    throw "Release from the main branch, not $BranchName."
}

$headCommit = git rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $headCommit -cne $Commit) {
    throw 'The checkout must be the exact commit being released.'
}
$tags = @(git tag --list "v$umbracoVersion.*")
if ($LASTEXITCODE -ne 0) { throw 'Could not read release tags.' }
$pattern = '^v' + [regex]::Escape($umbracoVersion) + '\.([1-9][0-9]*)$'
$revisions = @($tags | ForEach-Object {
    if ($_ -cmatch $pattern) { [int] $Matches[1] }
})
$headTags = @(git tag --points-at $Commit --list "v$umbracoVersion.*")
if ($LASTEXITCODE -ne 0) { throw 'Could not read tags for the release commit.' }
$headRevisions = @($headTags | ForEach-Object {
    if ($_ -cmatch $pattern) { [int] $Matches[1] }
})
$revision = 1
if ($headRevisions.Count -gt 0) {
    # A retried release reuses the version reserved for this commit, even after a newer commit has been released.
    $revision = [int] ($headRevisions | Measure-Object -Maximum).Maximum
} else {
    # Allocate a new revision only for the branch tip. Re-running an old workflow run checks out its original commit,
    # and that would publish older code under the newest revision.
    $branchTip = git rev-parse --verify --quiet "refs/remotes/origin/$BranchName"
    if ($LASTEXITCODE -ne 0 -or $branchTip -cne $Commit) {
        throw "Commit $Commit is not the tip of $BranchName. Run Release again from the branch to publish its latest commit."
    }
    if ($revisions.Count -gt 0) {
        $revision = [int] ($revisions | Measure-Object -Maximum).Maximum + 1
    }
}
if ($revision -gt 65534) { throw 'The revision exceeds the .NET assembly version limit.' }

[pscustomobject]@{
    Version = "$umbracoVersion.$revision"
    Tag = "v$umbracoVersion.$revision"
}
