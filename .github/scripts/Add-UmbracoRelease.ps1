#Requires -Version 7.0
<#
.SYNOPSIS
Adds Umbraco releases to the releases the package supports.

.DESCRIPTION
Adds each release to UmbracoReleases in Directory.Build.props, keeping the list oldest first, and sets
UmbracoMinimumVersion, UmbracoMaximumVersion and UmbracoVersion to the oldest and newest listed releases. Then writes
the new range, such as 17.5.0 to 18.3.2, between the <!-- umbraco-releases --> and <!-- /umbraco-releases --> markers
in README.md and the Markdown files in docs. nuget.org shows docs/NUGET.md without HTML, so that file states the range
as plain text, and the script replaces the old range there. A release that is already listed changes nothing. A
release of a major version that the list does not include stops the script, because supporting it takes the changes
that docs/DEVELOPING.md describes.

.PARAMETER Releases
The releases to add, such as 18.3.2. A value can list several, separated by commas.

.PARAMETER Repository
The repository's root folder. Defaults to the one this script is in.

.OUTPUTS
The releases it added.
#>
param(
    [Parameter(Mandatory)][string[]] $Releases,
    [string] $Repository = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
)

$ErrorActionPreference = 'Stop'
$split = [StringSplitOptions]::RemoveEmptyEntries -bor [StringSplitOptions]::TrimEntries
$Releases = @($Releases | ForEach-Object { $_.Split(',', $split) })

<#
.SYNOPSIS
Reads a text file and returns its text and whether it starts with a UTF-8 byte order mark, so it can be written back
the same way.
#>
function Read-Text([string] $path) {
    $bytes = [IO.File]::ReadAllBytes($path)
    $bom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    [pscustomobject]@{ Text = [Text.UTF8Encoding]::new($false).GetString($bytes, $bom ? 3 : 0, $bytes.Length - ($bom ? 3 : 0)); Bom = $bom }
}

<#
.SYNOPSIS
Writes text to a file as UTF-8, with a byte order mark only when the file had one.
#>
function Write-Text([string] $path, [string] $text, [bool] $bom) {
    [IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($bom))
}

$propsPath = Join-Path $Repository 'Directory.Build.props'
$props = Read-Text $propsPath
$list = [regex]::Match($props.Text, '<UmbracoReleases>([^<]*)</UmbracoReleases>')
if (-not $list.Success) { throw 'Directory.Build.props has no UmbracoReleases.' }
$listed = [Collections.Generic.List[version]]::new()
$list.Groups[1].Value.Split(';', $split) | ForEach-Object { $listed.Add([version] $_) }
$majors = @($listed | ForEach-Object Major | Sort-Object -Unique)
$before = @($listed | Sort-Object)
$oldRange = "$($before[0]) to $($before[-1])"

$added = foreach ($text in $Releases) {
    if ($text -notmatch '^\d+\.\d+\.\d+$') { throw "$text is not a release number such as 18.3.2." }
    $release = [version] $text
    if ($release.Major -notin $majors) {
        throw "Umbraco $release is a major version the package does not support yet. docs/DEVELOPING.md lists the steps."
    }
    if ($listed.Contains($release)) { continue }
    $listed.Add($release)
    $release.ToString()
}
if (-not $added) { return }

$sorted = @($listed | Sort-Object)
$text = $props.Text
$text = $text -replace '<UmbracoReleases>[^<]*</UmbracoReleases>', "<UmbracoReleases>$($sorted -join ';')</UmbracoReleases>"
$text = $text -replace '<UmbracoMinimumVersion>[^<]*</UmbracoMinimumVersion>', "<UmbracoMinimumVersion>$($sorted[0])</UmbracoMinimumVersion>"
$text = $text -replace '<UmbracoMaximumVersion>[^<]*</UmbracoMaximumVersion>', "<UmbracoMaximumVersion>$($sorted[-1])</UmbracoMaximumVersion>"
$text = $text -replace '<UmbracoVersion>[^<]*</UmbracoVersion>', "<UmbracoVersion>$($sorted[-1])</UmbracoVersion>"
Write-Text $propsPath $text $props.Bom

# The documents state the range between markers, so the update cannot touch examples that mention other releases.
$range = "$($sorted[0]) to $($sorted[-1])"
$documents = @(Join-Path $Repository 'README.md') + @(Get-ChildItem (Join-Path $Repository 'docs') -Filter *.md -ErrorAction SilentlyContinue | ForEach-Object FullName)
foreach ($path in $documents | Where-Object { Test-Path -LiteralPath $_ }) {
    $document = Read-Text $path
    $updated = [regex]::Replace($document.Text, '(<!-- umbraco-releases -->)[^<]*(<!-- /umbraco-releases -->)', "`${1}$range`${2}")
    if ([IO.Path]::GetFullPath($path) -eq [IO.Path]::GetFullPath((Join-Path $Repository 'docs' 'NUGET.md'))) {
        $updated = $updated.Replace($oldRange, $range)
    }
    if ($updated -cne $document.Text) { Write-Text $path $updated $document.Bom }
}

$added
