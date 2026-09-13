param(
    [string]$Configuration = 'Release',
    [string]$ModVersion = '1.0.7',
    [string]$Channel = 'stable',
    [string]$OutputDirectory,
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
function Get-PackageHash([string]$Path) {
    $sha = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($Path)
    try { return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
    finally { $stream.Dispose(); $sha.Dispose() }
}
$projectRoot = Split-Path -Parent $PSScriptRoot
$modProject = Join-Path $projectRoot 'src/RWCompanion.Mod/RWCompanion.Mod.csproj'
if (!$OutputDirectory) { $OutputDirectory = Join-Path $projectRoot "src/RWCompanion.Mod/bin/$Configuration/package" }
$packageOutput = [IO.Path]::GetFullPath($OutputDirectory)
if (!$SkipBuild) {
    & dotnet build $modProject -c $Configuration "-p:CompanionModVersion=$ModVersion" -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'The companion mod build failed.' }
}
New-Item -ItemType Directory -Path $packageOutput -Force | Out-Null
$packageStage = Join-Path $packageOutput ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $packageStage 'plugins') -Force | Out-Null
try {
    $modInfo = Get-Content -LiteralPath (Join-Path $projectRoot 'src/RWCompanion.Mod/modinfo.json') -Raw | ConvertFrom-Json
    $modInfo.version = $ModVersion
    $utf8 = New-Object System.Text.UTF8Encoding $false
    [IO.File]::WriteAllText((Join-Path $packageStage 'modinfo.json'), ($modInfo | ConvertTo-Json -Depth 10), $utf8)
    Copy-Item -LiteralPath (Join-Path $projectRoot 'src/RWCompanion.Mod/rainmeadow.json') -Destination $packageStage
    foreach ($dll in @('RWCompanion.Mod.dll', 'RainWorldCompanion.LiveProtocol.dll')) {
        Copy-Item -LiteralPath (Join-Path $projectRoot "src/RWCompanion.Mod/bin/$Configuration/net48/$dll") -Destination (Join-Path $packageStage 'plugins')
    }
    $hashes = [ordered]@{}
    Get-ChildItem -LiteralPath $packageStage -Recurse -File | ForEach-Object {
        $relative = $_.FullName.Substring($packageStage.Length + 1).Replace('\', '/')
        $hashes[$relative] = Get-PackageHash $_.FullName
    }
    $manifest = @{modId='rwcompanion'; version=$ModVersion; protocolVersion=1; minimumAppVersion='1.3.0'; files=$hashes}
    [IO.File]::WriteAllText((Join-Path $packageStage 'companion-manifest.json'), ($manifest | ConvertTo-Json -Depth 10), $utf8)
    $zipPath = Join-Path $packageOutput 'rwcompanion.zip'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    Add-Type -AssemblyName System.IO.Compression
    $temporaryZip = Join-Path $packageOutput ([Guid]::NewGuid().ToString('N') + '.zip')
    $archive = [IO.Compression.ZipFile]::Open($temporaryZip, [IO.Compression.ZipArchiveMode]::Create)
    try {
        Get-ChildItem -LiteralPath $packageStage -Recurse -File | ForEach-Object {
            $entryName = $_.FullName.Substring($packageStage.Length + 1).Replace('\', '/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $entryName) | Out-Null
        }
    }
    finally { $archive.Dispose() }
    Move-Item -LiteralPath $temporaryZip -Destination $zipPath -Force
    $release = @{version=$ModVersion; protocolVersion=1; minimumAppVersion='1.3.0'; channel=$Channel; packageUrl='rwcompanion.zip'; sha256=(Get-PackageHash $zipPath)}
    [IO.File]::WriteAllText((Join-Path $packageOutput 'release.json'), ($release | ConvertTo-Json), $utf8)
}
finally {
    $resolvedStage = [IO.Path]::GetFullPath($packageStage)
    if (!$resolvedStage.StartsWith($packageOutput + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected package staging directory.' }
    Remove-Item -LiteralPath $resolvedStage -Recurse -Force
}
