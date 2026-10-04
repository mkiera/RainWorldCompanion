param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^v?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')]
    [string] $PreviousStable,

    [Parameter(Mandatory = $true)]
    [ValidateSet('Fix', 'Feature', 'Breaking')]
    [string] $ChangeType,

    [string] $ProposedVersion
)

$ErrorActionPreference = 'Stop'
$parts = $PreviousStable.TrimStart('v').Split('.')
$major = [int]$parts[0]
$minor = [int]$parts[1]
$patch = [int]$parts[2]
$next = switch ($ChangeType) {
    'Fix' { '{0}.{1}.{2}' -f $major, $minor, ($patch + 1) }
    'Feature' { '{0}.{1}.0' -f $major, ($minor + 1) }
    'Breaking' { '{0}.0.0' -f ($major + 1) }
}
if ($ProposedVersion -and $ProposedVersion -cne $next) {
    throw "$ChangeType changes after $PreviousStable require $next, not $ProposedVersion."
}
Write-Output $next
