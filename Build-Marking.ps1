param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$RunTests
)
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-TaskPackage([string]$Id, [string]$Version, [string]$Destination, [string]$ExpectedHash) {
    if (Test-Path -LiteralPath $Destination) { return }
    $taskCache = Join-Path $taskRoot '.build'
    New-Item -ItemType Directory -Path $taskCache -Force | Out-Null
    $taskPackage = Join-Path $taskCache "$Id.$Version.nupkg"
    $taskUrl = "https://api.nuget.org/v3-flatcontainer/$Id/$Version/$Id.$Version.nupkg"
    Invoke-WebRequest -UseBasicParsing -Uri $taskUrl -OutFile $taskPackage
    $taskActualHash = (Get-FileHash -LiteralPath $taskPackage -Algorithm SHA256).Hash
    if ($taskActualHash -ne $ExpectedHash) { throw "Package hash mismatch: $Id" }
    [IO.Compression.ZipFile]::ExtractToDirectory($taskPackage, $Destination)
}

Get-TaskPackage 'qrcoder' '1.4.3' (Join-Path $taskRoot 'packages\QRCoder.1.4.3') '8ADE16246255AE99986FC60B9532DE52B4DB196F00166DAB5EC916ECDBC2B4C2'
$taskReferenceRoot = Join-Path $taskRoot '.build\net40'
Get-TaskPackage 'microsoft.netframework.referenceassemblies.net40' '1.0.3' $taskReferenceRoot '54D6E20A1B61CAF79395D6D71D091265E81F5A5705AC4AE52AF45CA143C3C694'
$taskVsWhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $taskVsWhere)) { throw 'Visual Studio Build Tools / Visual Studio with MSBuild is required.' }
$taskMSBuild = & $taskVsWhere -latest -products '*' -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $taskMSBuild) { throw 'MSBuild was not found.' }
$taskFrameworkArg = '/p:TargetFrameworkRootPath=' + (Join-Path $taskReferenceRoot 'build')
& $taskMSBuild (Join-Path $taskRoot 'LaserGRBL.sln') /t:Build "/p:Configuration=$Configuration" $taskFrameworkArg /nologo /verbosity:minimal
if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }
if ($RunTests) {
    & $taskMSBuild (Join-Path $taskRoot 'LaserGRBL.Marking.Tests\LaserGRBL.Marking.Tests.csproj') /t:Build "/p:Configuration=$Configuration" $taskFrameworkArg /nologo /verbosity:minimal
    if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
    $taskArtifacts = Join-Path $taskRoot '.build\test-artifacts'
    New-Item -ItemType Directory -Path $taskArtifacts -Force | Out-Null
    $taskPreviousData = $env:LASERGRBL_MARKING_DATA_PATH
    $env:LASERGRBL_MARKING_DATA_PATH = Join-Path $taskArtifacts 'data'
    try {
        & (Join-Path $taskRoot "LaserGRBL.Marking.Tests\bin\$Configuration\LaserGRBL.Marking.Tests.exe") $taskArtifacts
        if ($LASTEXITCODE -ne 0) { throw 'Marking tests failed.' }
    } finally { $env:LASERGRBL_MARKING_DATA_PATH = $taskPreviousData }
}
