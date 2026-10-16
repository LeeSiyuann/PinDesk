[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z]+(\.[0-9A-Za-z]+)*)?$')]
    [string]$Version,

    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryPath = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryPath 'apps/windows/PinDesk.Windows/PinDesk.Windows.csproj'
$distributionPath = Join-Path $repositoryPath 'dist'
$stagingPath = Join-Path $repositoryPath ('artifacts/package-windows-' + [Guid]::NewGuid().ToString('N'))
$archivePath = Join-Path $distributionPath "PinDesk-windows-$Runtime-$Version.zip"

if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw "Windows application project was not found: $projectPath"
}
if (Test-Path -LiteralPath $archivePath) {
    throw "Package already exists. Select a new version or remove the old package intentionally: $archivePath"
}

New-Item -ItemType Directory -Path $distributionPath -Force | Out-Null
New-Item -ItemType Directory -Path $stagingPath -Force | Out-Null

# Keep the runtime and recovery entry point together; users need no .NET installation.
& dotnet publish $projectPath --configuration Release --runtime $Runtime --self-contained true `
    -p:Version=$Version -p:PublishSingleFile=false -p:PublishTrimmed=false --output $stagingPath
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$executablePath = Join-Path $stagingPath 'PinDesk.Windows.exe'
if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
    throw "Published package did not contain the application executable: $executablePath"
}
foreach ($document in @('LICENSE', 'README.md')) {
    $sourcePath = Join-Path $repositoryPath $document
    if (Test-Path -LiteralPath $sourcePath -PathType Leaf) {
        Copy-Item -LiteralPath $sourcePath -Destination $stagingPath
    }
}

@"
PinDesk $Version ($Runtime)

Extract all ZIP contents to a folder and run PinDesk.Windows.exe.
The .NET runtime is included. This preview executable is not Authenticode signed.
See README.md and the repository's specs/acceptance.md for supported behavior and verification limits.
"@ | Set-Content -LiteralPath (Join-Path $stagingPath 'START-HERE.txt') -Encoding utf8

Compress-Archive -Path (Join-Path $stagingPath '*') -DestinationPath $archivePath -CompressionLevel Optimal
$digest = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Output "Created $archivePath"
Write-Output "SHA256 $digest"
# Leave the uniquely named staging folder available for package inspection; do not recursively delete paths.
