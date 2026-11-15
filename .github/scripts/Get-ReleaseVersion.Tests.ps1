BeforeAll {
    $sourceScript = Join-Path $PSScriptRoot 'Get-ReleaseVersion.ps1'
    <#
    .SYNOPSIS
    Runs git with the given arguments, discards its output, and throws when git fails.
    #>
    function Invoke-Git {
        & git @args 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Git failed: $args" }
    }
    <#
    .SYNOPSIS
    Creates an empty commit in the fixture repository and returns its hash.
    #>
    function New-Commit {
        Invoke-Git commit --allow-empty -m fixture
        return git rev-parse HEAD
    }
}

Describe 'Release revision selection' {
    BeforeEach {
        $fixture = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path "$fixture/.github/scripts" -Force | Out-Null
        Copy-Item -LiteralPath $sourceScript -Destination "$fixture/.github/scripts/Get-ReleaseVersion.ps1"
        Set-Content -LiteralPath "$fixture/Directory.Build.props" '<Project><PropertyGroup><UmbracoMaximumVersion>18.3.1</UmbracoMaximumVersion></PropertyGroup></Project>'
        Push-Location $fixture
        Invoke-Git init -b main
        Invoke-Git config user.name 'Release fixture'
        Invoke-Git config user.email 'fixture@example.test'
        Invoke-Git config commit.gpgsign false
        $commit = New-Commit
        Invoke-Git update-ref refs/remotes/origin/main $commit
        $script = "$fixture/.github/scripts/Get-ReleaseVersion.ps1"
    }

    AfterEach { Pop-Location }

    It 'starts at revision one when there are no tags' {
        $release = & $script -BranchName 'main' -Commit $commit
        $release.Version | Should -Be '18.3.1.1'
        $release.Tag | Should -Be 'v18.3.1.1'
    }

    It 'numbers releases after the newest supported Umbraco release, not the release the tests run against' {
        Set-Content -LiteralPath "$fixture/Directory.Build.props" ('<Project><PropertyGroup><UmbracoMinimumVersion>18.2.0</UmbracoMinimumVersion>' +
            '<UmbracoMaximumVersion>18.3.1</UmbracoMaximumVersion><UmbracoVersion>18.2.1</UmbracoVersion></PropertyGroup></Project>')
        (& $script -BranchName 'main' -Commit $commit).Version | Should -Be '18.3.1.1'
    }

    It 'selects the next numeric revision and ignores unrelated or malformed tags' {
        Invoke-Git tag v18.3.1.9
        Invoke-Git tag v18.3.1.10
        Invoke-Git tag v18.3.1.011
        Invoke-Git tag v18.3.1.beta
        Invoke-Git tag v18.3.2.99
        $commit = New-Commit
        Invoke-Git update-ref refs/remotes/origin/main $commit
        (& $script -BranchName 'main' -Commit $commit).Version | Should -Be '18.3.1.11'
    }

    It 'refuses an untagged stale commit' {
        $newer = New-Commit
        Invoke-Git update-ref refs/remotes/origin/main $newer
        Invoke-Git checkout --detach $commit
        { & $script -BranchName 'main' -Commit $commit } | Should -Throw '*not the tip*'
    }

    It 'allocates the last supported revision' {
        Invoke-Git tag v18.3.1.65533
        $commit = New-Commit
        Invoke-Git update-ref refs/remotes/origin/main $commit
        (& $script -BranchName 'main' -Commit $commit).Version | Should -Be '18.3.1.65534'
    }

    It 'refuses a revision beyond the assembly version limit' {
        Invoke-Git tag v18.3.1.65534
        $commit = New-Commit
        Invoke-Git update-ref refs/remotes/origin/main $commit
        { & $script -BranchName 'main' -Commit $commit } | Should -Throw '*assembly version limit*'
    }

    It 'reuses a tagged revision when retrying after a newer release' {
        Invoke-Git tag v18.3.1.4
        $newer = New-Commit
        Invoke-Git tag v18.3.1.5
        Invoke-Git update-ref refs/remotes/origin/main $newer
        Invoke-Git checkout --detach $commit
        (& $script -BranchName 'main' -Commit $commit).Version | Should -Be '18.3.1.4'
    }

    It 'rejects malformed props: <Value>' -TestCases @(
        @{ Value = '18.3' }, @{ Value = '18.3.1.2' }, @{ Value = '18.03.1' }, @{ Value = 'invalid' }
    ) {
        param($Value)
        Set-Content -LiteralPath "$fixture/Directory.Build.props" "<Project><PropertyGroup><UmbracoMaximumVersion>$Value</UmbracoMaximumVersion></PropertyGroup></Project>"
        { & $script -BranchName 'main' -Commit $commit } | Should -Throw '*three numeric components*'
    }

    It 'rejects a release from a branch other than main' {
        { & $script -BranchName 'umbraco/18.3.1' -Commit $commit } | Should -Throw '*main branch*'
    }

    It 'rejects a checkout different from the requested release commit' {
        $null = New-Commit
        { & $script -BranchName 'main' -Commit $commit } | Should -Throw '*exact commit*'
    }
}
