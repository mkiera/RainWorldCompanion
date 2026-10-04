$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$workflow = [IO.File]::ReadAllText((Join-Path $repo '.github/workflows/build-test.yml'))
$match = [regex]::Match($workflow, '(?s)- name: Work out a version for this branch build.*?run: \|\r?\n(?<code>.*?)(?=\r?\n      - name: Test)')
if (-not $match.Success) { throw 'Branch version step missing.' }
$code = [regex]::Replace($match.Groups['code'].Value, '(?m)^          ', '')
$code = $code.Replace('${{ github.run_number }}', '42')
$root = Join-Path ([IO.Path]::GetTempPath()) ('rwc-branch-version-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $root '.github') -Force | Out-Null
$previousOutput = $env:GITHUB_OUTPUT
$env:GITHUB_OUTPUT = Join-Path $root 'output.txt'
$previousLocation = Get-Location
function git {
    if ($args[0] -eq 'tag') { return @('v1.4.0','v1.4.1','v1.5.0','v1.4.2-beta.1') }
    if ($args[0] -eq 'describe') {
        if ($script:described -eq 'v1.5.0-1-gabc1234' -and $args -contains '--exclude=v1.5.0') { return 'v1.4.1-2-gabc1234' }
        return $script:described
    }
    throw 'Unexpected git command in branch version step.'
}
try {
    $policy = Join-Path $repo '.github/withdrawn-release-tags.txt'
    if (Test-Path $policy) { Copy-Item $policy (Join-Path $root '.github/withdrawn-release-tags.txt') }
    Set-Location $root
    foreach ($case in @(
        @('1.4.2', 'v1.4.2-beta.1-1-gabc1234', '1.4.2-beta.1.alpha.1'),
        @('1.4.1', 'v1.4.1-0-gabc1234', '1.4.1'),
        @('1.4.1', 'v1.4.1-1-gabc1234', '1.4.2-alpha.1'),
        @('1.4.1', 'v1.5.0-1-gabc1234', '1.4.2-alpha.2'),
        @('1.6.0', 'v1.4.1-1-gabc1234', '1.6.0-alpha.1')
    )) {
        Set-Content 'Directory.Build.props' ('<Project><PropertyGroup><Version>'+$case[0]+'</Version></PropertyGroup></Project>')
        $script:described = $case[1]
        $version = $null
        . ([scriptblock]::Create($code))
        if ($version -cne $case[2]) { throw "Expected $($case[2]), got $version for $($case[1])." }
    }
    Write-Output 'Passed 5 branch build version checks.'
} finally {
    Set-Location $previousLocation
    $env:GITHUB_OUTPUT = $previousOutput
    Remove-Item Function:git
}
