#Requires -Version 7.0
<#
.SYNOPSIS
Builds the package and runs its tests against each supported Umbraco release.

.DESCRIPTION
Reads the supported releases from UmbracoReleases in Directory.Build.props, unless -Releases names others. Builds the
test project once for each release, one at a time, into <ArtifactsPath>/<release>, then runs the tests for every
release at the same time. Builds run one at a time because Umbraco's build targets write appsettings-schema.json into
the project folder, and parallel restores can collide in the NuGet cache. Each release's tests run in their own process
with their own temporary folder. Prints one line per release and exits with 1 when any release fails.

.PARAMETER Releases
The Umbraco releases to test, such as 18.2.1. Defaults to every release in UmbracoReleases.

.PARAMETER Browser
Runs the browser cases instead of the fast tests. Install Chromium first, as docs/DEVELOPING.md describes.
AVPF_E2E_CASE selects cases as it does for dotnet test.

.PARAMETER Configuration
The build configuration. Defaults to Debug.

.PARAMETER ArtifactsPath
Where the builds, logs and temporary files go. Defaults to artifacts/umbraco-releases in the repository.

.PARAMETER Throttle
How many releases run their tests at once. Defaults to all of them.
#>
param(
    [string[]] $Releases,
    [switch] $Browser,
    [string] $Configuration = 'Debug',
    [string] $ArtifactsPath,
    [int] $Throttle = 0
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$project = Join-Path $PSScriptRoot 'AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests'
if (-not $ArtifactsPath) { $ArtifactsPath = Join-Path $repository 'artifacts/umbraco-releases' }
if (-not $Releases) {
    [xml] $settings = Get-Content -LiteralPath (Join-Path $repository 'Directory.Build.props')
    $Releases = ([string] $settings.Project.PropertyGroup.UmbracoReleases).Split(';',
        [StringSplitOptions]::RemoveEmptyEntries -bor [StringSplitOptions]::TrimEntries)
}
if ($Throttle -le 0) { $Throttle = $Releases.Count }

# Build one release at a time.
$results = [Collections.Generic.List[object]]::new()
$built = foreach ($release in $Releases) {
    $folder = Join-Path $ArtifactsPath $release
    New-Item -ItemType Directory -Force $folder | Out-Null
    $log = Join-Path $folder 'build.log'
    Write-Host "Building for Umbraco $release"
    dotnet build $project -c $Configuration --artifacts-path (Join-Path $folder 'artifacts') "-p:UmbracoVersion=$release" -nologo *> $log
    if ($LASTEXITCODE -eq 0) { $release }
    else { $results.Add([pscustomobject]@{ Release = $release; Result = 'build failed'; Tests = ''; Log = $log; Failed = @() }) }
}

# Test every release at once, each in its own process, so environment variables stay apart.
$test = {
    param($release, $folder, $configuration, $browser)
    $temp = Join-Path $folder 'temp'
    New-Item -ItemType Directory -Force $temp | Out-Null
    $env:TEMP = $temp
    $env:TMP = $temp
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $assembly = Join-Path $folder "artifacts/bin/AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests/$($configuration.ToLowerInvariant())/AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests.dll"
    $log = Join-Path $folder ($browser ? 'browser.log' : 'fast.log')
    $filter = $browser ? '--filter-class' : '--filter-not-class'
    dotnet $assembly $filter '*SiteEndToEndTests' *> $log
    $exitCode = $LASTEXITCODE
    $lines = Get-Content $log
    $counts = ($lines | Select-String -Pattern '^\s+(total|failed|succeeded|skipped): \d+' | ForEach-Object { $_.Line.Trim() }) -join ', '
    [pscustomobject]@{
        Release = $release
        Result = $exitCode -eq 0 ? 'passed' : 'failed'
        Tests = $counts
        Log = $log
        Failed = @($lines | Select-String -Pattern '^failed (.+?)( \(\d.*\))?$' | ForEach-Object { $_.Matches[0].Groups[1].Value })
    }
}
$jobs = foreach ($release in $built) {
    while (@(Get-Job -State Running | Where-Object Name -like 'umbraco-release-*').Count -ge $Throttle) { Start-Sleep -Milliseconds 500 }
    Write-Host "Testing Umbraco $release"
    Start-Job -Name "umbraco-release-$release" -ScriptBlock $test `
        -ArgumentList $release, (Join-Path $ArtifactsPath $release), $Configuration, $Browser.IsPresent
}
if ($jobs) { $jobs | Receive-Job -Wait -AutoRemoveJob | ForEach-Object { $results.Add($_) } }

$results | Sort-Object { [version] $_.Release } | Format-Table Release, Result, Tests, Log -AutoSize | Out-String -Width 250 | Write-Host
foreach ($result in $results | Where-Object { $_.Failed.Count -gt 0 }) {
    Write-Host "Umbraco $($result.Release) failed:"
    $result.Failed | ForEach-Object { Write-Host "  $_" }
}
exit (@($results | Where-Object Result -ne 'passed').Count -eq 0 ? 0 : 1)
