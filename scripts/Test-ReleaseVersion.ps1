$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot 'Get-ReleaseVersion.ps1'
foreach ($case in @(
    @('1.4.0', 'Fix', '1.4.1'),
    @('v1.4.9', 'Fix', '1.4.10'),
    @('1.4.9', 'Feature', '1.5.0'),
    @('1.4.9', 'Breaking', '2.0.0')
)) {
    $actual = & $scriptPath -PreviousStable $case[0] -ChangeType $case[1] -ProposedVersion $case[2]
    if ($actual -ne $case[2]) { throw "Expected $($case[2]), got $actual" }
}
foreach ($case in @(
    @('1.4.0', 'Fix', '1.5.0'),
    @('1.4.0-beta.1', 'Fix', '1.4.1'),
    @('1.04.0', 'Fix', '1.4.1'),
    @('1.4.0', 'Feature', '1.4.1')
)) {
    $rejected = $false
    try { & $scriptPath -PreviousStable $case[0] -ChangeType $case[1] -ProposedVersion $case[2] | Out-Null }
    catch { $rejected = $true }
    if (-not $rejected) { throw "Accepted an invalid release choice: $case" }
}
Write-Output 'Passed 8 release version checks.'
