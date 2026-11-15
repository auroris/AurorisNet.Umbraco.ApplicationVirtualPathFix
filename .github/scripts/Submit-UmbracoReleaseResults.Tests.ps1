BeforeAll {
    $script = Join-Path $PSScriptRoot 'Submit-UmbracoReleaseResults.ps1'
}

Describe 'Reporting the results of new Umbraco releases' {
    It 'passes a release only when every summary passed it, and exits with 1 when one failed' {
        $fast = Join-Path $TestDrive 'fast.json'
        $browser = Join-Path $TestDrive 'browser.json'
        Set-Content -LiteralPath $fast '[{"Release":"18.3.2","Result":"passed","Tests":"total: 150","Failures":[]},{"Release":"17.8.2","Result":"passed","Tests":"total: 148","Failures":[]}]'
        Set-Content -LiteralPath $browser '[{"Release":"18.3.2","Result":"passed","Tests":"passed: full","Failures":[]},{"Release":"17.8.2","Result":"failed","Tests":"failed: full","Failures":["full: Patched_features_work_under_a_nested_https_path"]}]'
        $output = pwsh -NoProfile -File $script -Releases '18.3.2,17.8.2' -Summaries "$fast,$browser" -DryRun
        $LASTEXITCODE | Should -Be 1
        $output | Should -Contain 'Passed: 18.3.2'
        $output | Should -Contain 'Failed: 17.8.2'
        ($output -join "`n") | Should -BeLike '*Would open the issue "Umbraco 17.8.2 fails the tests"*full: Patched_features_work_under_a_nested_https_path*'
        ($output -join "`n") | Should -BeLike '*push umbraco-releases/18.3.2 and open the pull request "Add Umbraco 18.3.2"*'
    }

    It 'fails every release when a summary is missing, as when its tests never ran' {
        $fast = Join-Path $TestDrive 'fast-only.json'
        Set-Content -LiteralPath $fast '[{"Release":"18.3.2","Result":"passed","Tests":"total: 150","Failures":[]}]'
        $output = pwsh -NoProfile -File $script -Releases '18.3.2' -Summaries "$fast,$(Join-Path $TestDrive 'browser-missing.json')" -DryRun
        $LASTEXITCODE | Should -Be 1
        $output | Should -Contain 'Failed: 18.3.2'
        ($output -join "`n") | Should -BeLike '*The browser-missing tests did not run.*'
    }

    It 'exits with 0 when every release passed' {
        $fast = Join-Path $TestDrive 'all-fast.json'
        Set-Content -LiteralPath $fast '[{"Release":"18.3.2","Result":"passed","Tests":"total: 150","Failures":[]}]'
        $output = pwsh -NoProfile -File $script -Releases '18.3.2' -Summaries $fast -DryRun
        $LASTEXITCODE | Should -Be 0
        $output | Should -Contain 'Passed: 18.3.2'
    }
}
