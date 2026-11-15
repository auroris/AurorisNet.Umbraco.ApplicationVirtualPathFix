BeforeAll {
    $sourceScript = Join-Path $PSScriptRoot 'Find-UmbracoReleases.ps1'
    <#
    .SYNOPSIS
    Creates a fixture repository whose Directory.Build.props lists the given releases, copies the script into it and
    returns the copy's path.
    #>
    function New-Fixture([string[]] $releases) {
        $fixture = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path "$fixture/.github/scripts" -Force | Out-Null
        Copy-Item -LiteralPath $sourceScript -Destination "$fixture/.github/scripts/Find-UmbracoReleases.ps1"
        Set-Content -LiteralPath "$fixture/Directory.Build.props" "<Project><PropertyGroup><UmbracoReleases>$($releases -join ';')</UmbracoReleases></PropertyGroup></Project>"
        "$fixture/.github/scripts/Find-UmbracoReleases.ps1"
    }
}

Describe 'Finding new Umbraco releases' {
    It 'returns the stable releases after the newest listed release of each supported major version' {
        $script = New-Fixture @('17.5.0', '17.8.1', '18.3.1')
        $found = & $script -Published @('17.4.2', '17.8.1', '17.8.2', '17.9.0', '18.3.1', '18.3.2', '18.4.0-rc1')
        $found.Releases | Should -Be @('17.8.2', '17.9.0', '18.3.2')
        $found.NewMajors | Should -BeNullOrEmpty
    }

    It 'reports the newest stable release of each newer major version on its own' {
        $script = New-Fixture @('17.8.1', '18.3.1')
        $found = & $script -Published @('18.3.1', '19.0.0-rc1', '19.0.0', '19.0.1', '20.0.0-beta1')
        $found.Releases | Should -BeNullOrEmpty
        $found.NewMajors | Should -Be @('19.0.1')
    }

    It 'finds nothing when the newest releases are listed' {
        $script = New-Fixture @('18.3.1')
        $found = & $script -Published @('18.3.0', '18.3.1', '18.4.0-rc1')
        $found.Releases | Should -BeNullOrEmpty
        $found.NewMajors | Should -BeNullOrEmpty
    }
}
