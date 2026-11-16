#Requires -Version 7.0
<#
.SYNOPSIS
Builds the package and runs its tests against each supported Umbraco release.

.DESCRIPTION
Reads the supported releases from UmbracoReleases in Directory.Build.props, unless -Releases names others. Builds the
test project once for each release, one at a time, into <ArtifactsPath>/<release>, then runs the tests for every
release at the same time. Builds run one at a time because Umbraco's build targets write appsettings-schema.json into
the project folder, and parallel restores can collide in the NuGet cache. Each release's quick tests run in one process,
and each browser case runs in a process of its own, because Umbraco 17 keeps the first site's services for the whole
process. Every process has its own temporary folder. Prints one line per release and exits with 1 when any release
fails.

.PARAMETER Releases
The Umbraco releases to test, such as 18.2.1. Defaults to every release in UmbracoReleases.

.PARAMETER Browser
Runs the browser cases instead of the quick tests. Install Chromium first, as docs/DEVELOPING.md describes.
AVPF_E2E_CASE selects cases as it does for dotnet test.

.PARAMETER Configuration
The build configuration. Defaults to Debug.

.PARAMETER ArtifactsPath
Where the builds, logs and temporary files go. Defaults to artifacts/umbraco-releases in the repository.

.PARAMETER Throttle
How many test processes run at once. Defaults to the number of releases for the quick tests, so every release's quick
tests run at once, and to half the processor count for the browser cases.

.PARAMETER SummaryPath
Also writes the results to this JSON file: one object per release with Release, Result, Tests and Failures, where
Failures lists each failed test, prefixed with its browser case. The New Umbraco releases workflow reads it.
#>
param(
    [string[]] $Releases,
    [switch] $Browser,
    [string] $Configuration = 'Debug',
    [string] $ArtifactsPath,
    [int] $Throttle = 0,
    [string] $SummaryPath
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$project = Join-Path $PSScriptRoot 'AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests'
if (-not $ArtifactsPath) { $ArtifactsPath = Join-Path $repository 'artifacts/umbraco-releases' }
# pwsh -File passes "18.2.1,18.3.0" as one string, so split each value on commas too.
if ($Releases) { $Releases = @($Releases | ForEach-Object { $_.Split(',', [StringSplitOptions]::RemoveEmptyEntries -bor [StringSplitOptions]::TrimEntries) }) }
if (-not $Releases) {
    [xml] $settings = Get-Content -LiteralPath (Join-Path $repository 'Directory.Build.props')
    $Releases = ([string] $settings.Project.PropertyGroup.UmbracoReleases).Split(';',
        [StringSplitOptions]::RemoveEmptyEntries -bor [StringSplitOptions]::TrimEntries)
}
# Each browser case runs a site and Chromium, which keep about two processors busy, so by default run as many as half
# the processors at once.
if ($Throttle -le 0) { $Throttle = $Browser ? [Math]::Max(1, [Math]::Floor([Environment]::ProcessorCount / 2)) : $Releases.Count }

# The browser cases, as SiteEndToEndTests names them. The quick tests run as one unnamed case.
$cases = @($null)
if ($Browser) {
    $cases = @('full', 'iis', 'proxy', 'proxy-root', 'basic-auth')
    if ($env:AVPF_E2E_CASE) {
        $selected = $env:AVPF_E2E_CASE.Split(',', [StringSplitOptions]::RemoveEmptyEntries -bor [StringSplitOptions]::TrimEntries)
        $unknown = @($selected | Where-Object { $_ -notin $cases })
        if ($unknown) { throw "AVPF_E2E_CASE names unknown cases: $($unknown -join ', '). The cases are $($cases -join ', ')." }
        $cases = $selected
    }
}

# Build one release at a time.
$results = [Collections.Generic.List[object]]::new()
$built = foreach ($release in $Releases) {
    $folder = Join-Path $ArtifactsPath $release
    New-Item -ItemType Directory -Force $folder | Out-Null
    $log = Join-Path $folder 'build.log'
    Write-Host "Building for Umbraco $release"
    dotnet build $project -c $Configuration --artifacts-path (Join-Path $folder 'artifacts') "-p:UmbracoVersion=$release" -nologo *> $log
    if ($LASTEXITCODE -eq 0) { $release }
    else { $results.Add([pscustomobject]@{ Release = $release; Case = $null; Result = 'build failed'; Tests = ''; Log = $log; Failed = @() }) }
}

# Run each case in its own process, so environment variables and Umbraco's process-wide state stay apart.
$test = {
    param($release, $folder, $configuration, $case)
    $name = $case ? "browser-$case" : 'quick'
    $temp = Join-Path $folder "temp/$name"
    New-Item -ItemType Directory -Force $temp | Out-Null
    $env:TEMP = $temp
    $env:TMP = $temp
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:AVPF_E2E_CASE = $case
    $assembly = Join-Path $folder "artifacts/bin/AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests/$($configuration.ToLowerInvariant())/AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests.dll"
    $log = Join-Path $folder "$name.log"
    $filter = $case ? '--filter-class' : '--filter-not-class'
    dotnet $assembly $filter '*SiteEndToEndTests' *> $log
    $exitCode = $LASTEXITCODE
    $lines = Get-Content $log
    $counts = @($lines | Select-String -Pattern '^\s+(total|failed|succeeded|skipped): (\d+)')
    $failed = @($lines | Select-String -Pattern '^failed (.+?)( \(\d.*\))?$' | ForEach-Object { $_.Matches[0].Groups[1].Value })
    # The other browser cases skip, so a browser case passes only when its own test succeeded.
    $ran = -not $case -or @($counts | Where-Object { $_.Matches[0].Groups[1].Value -eq 'succeeded' -and $_.Matches[0].Groups[2].Value -ne '0' }).Count -gt 0
    if ($exitCode -eq 0 -and -not $ran) { $failed += 'No test ran for this case.' }
    [pscustomobject]@{
        Release = $release
        Case = $case
        Result = $exitCode -eq 0 -and $ran ? 'passed' : 'failed'
        Tests = ($counts | ForEach-Object { $_.Line.Trim() }) -join ', '
        Log = $log
        Failed = $failed
    }
}
$jobs = foreach ($release in $built) {
    foreach ($case in $cases) {
        while (@(Get-Job -State Running | Where-Object Name -like 'umbraco-release-*').Count -ge $Throttle) { Start-Sleep -Milliseconds 500 }
        Write-Host ($case ? "Testing Umbraco $release, case $case" : "Testing Umbraco $release")
        Start-Job -Name ($case ? "umbraco-release-$release-$case" : "umbraco-release-$release") -ScriptBlock $test `
            -ArgumentList $release, (Join-Path $ArtifactsPath $release), $Configuration, $case
    }
}
if ($jobs) { $jobs | Receive-Job -Wait -AutoRemoveJob | ForEach-Object { $results.Add($_) } }

# Summarize each release in one line. A browser line lists the cases that passed and failed.
$summary = foreach ($group in $results | Group-Object Release) {
    $outcomes = @($group.Group)
    if (-not $Browser -or $outcomes[0].Result -eq 'build failed') { $outcomes[0]; continue }
    $passed = @($outcomes | Where-Object Result -eq 'passed')
    $failed = @($outcomes | Where-Object Result -ne 'passed')
    [pscustomobject]@{
        Release = $group.Name
        Result = $failed ? 'failed' : 'passed'
        Tests = (@(
                if ($passed) { "passed: $($passed.Case -join ', ')" }
                if ($failed) { "failed: $($failed.Case -join ', ')" }
            ) -join '; ')
        Log = Split-Path $outcomes[0].Log
    }
}
$summary | Sort-Object { [version] $_.Release } | Format-Table Release, Result, Tests, Log -AutoSize | Out-String -Width 250 | Write-Host
foreach ($outcome in $results | Where-Object Result -ne 'passed' | Sort-Object -Stable { [version] $_.Release }) {
    Write-Host "Umbraco $($outcome.Release)$($outcome.Case ? ", case $($outcome.Case)" : '') $($outcome.Result): $($outcome.Log)"
    $outcome.Failed | ForEach-Object { Write-Host "  $_" }
}
if ($SummaryPath) {
    $records = foreach ($line in $summary | Sort-Object { [version] $_.Release }) {
        $failures = foreach ($outcome in $results | Where-Object { $_.Release -eq $line.Release -and $_.Result -ne 'passed' }) {
            $prefix = $outcome.Case ? "$($outcome.Case): " : ''
            if ($outcome.Failed) { $outcome.Failed | ForEach-Object { "$prefix$_" } } else { "$prefix$($outcome.Result)" }
        }
        [pscustomobject]@{ Release = $line.Release; Result = $line.Result; Tests = $line.Tests; Failures = @($failures) }
    }
    New-Item -ItemType Directory -Force (Split-Path -Parent ([IO.Path]::GetFullPath($SummaryPath))) | Out-Null
    ConvertTo-Json -InputObject @($records) -Depth 4 | Set-Content -LiteralPath $SummaryPath -Encoding utf8
}
exit (@($results | Where-Object Result -ne 'passed').Count -eq 0 ? 0 : 1)
