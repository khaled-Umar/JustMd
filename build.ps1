#Requires -Version 7.0
<#
.SYNOPSIS
    Builds, tests, publishes and packages JustMd into a Windows installer.

.DESCRIPTION
    1. Cleans the artifacts/ folder.
    2. Runs the unit tests (unless -SkipTests).
    3. Publishes src/JustMd to artifacts/publish.
    4. Compiles installer/JustMd.iss with Inno Setup 6 into artifacts/installer
       (unless -SkipInstaller) and writes a .sha256 checksum file next to it.

.PARAMETER Configuration
    Build configuration. Default: Release.

.PARAMETER Version
    Version to stamp on the binaries and the installer (a leading "v" is ignored).
    Default: the <Version> value in Directory.Build.props.

.PARAMETER SkipTests
    Do not run dotnet test.

.PARAMETER SkipInstaller
    Do not compile the Inno Setup installer.

.EXAMPLE
    ./build.ps1
.EXAMPLE
    ./build.ps1 -Version 1.2.0 -SkipTests
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $Version,
    [switch] $SkipTests,
    [switch] $SkipInstaller
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$RepoRoot      = $PSScriptRoot
$ArtifactsDir  = Join-Path $RepoRoot 'artifacts'
$PublishDir    = Join-Path $ArtifactsDir 'publish'
$InstallerDir  = Join-Path $ArtifactsDir 'installer'
$AppProject    = Join-Path $RepoRoot 'src/JustMd/JustMd.csproj'
$Solution      = Join-Path $RepoRoot 'JustMd.sln'
$InstallerIss  = Join-Path $RepoRoot 'installer/JustMd.iss'
$PropsFile     = Join-Path $RepoRoot 'Directory.Build.props'

function Write-Step([string] $Message) {
    Write-Host ''
    Write-Host "==> $Message" -ForegroundColor Cyan
}

# Runs a native command and throws if it fails, independent of the PowerShell version.
function Invoke-Native([string] $FilePath, [string[]] $Arguments) {
    Write-Host "> $FilePath $($Arguments -join ' ')" -ForegroundColor DarkGray
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "'$FilePath' failed with exit code $LASTEXITCODE."
    }
}

function Get-PropsVersion {
    if (-not (Test-Path $PropsFile)) {
        throw "Cannot determine the version: $PropsFile not found. Pass -Version explicitly."
    }
    [xml] $props = Get-Content -Raw $PropsFile
    $node = $props.SelectSingleNode('/Project/PropertyGroup/Version')
    if ($null -eq $node) { $node = $props.SelectSingleNode('/Project/PropertyGroup/VersionPrefix') }
    if ($null -eq $node -or [string]::IsNullOrWhiteSpace($node.InnerText)) {
        throw "No <Version> found in $PropsFile. Pass -Version explicitly."
    }
    return $node.InnerText.Trim()
}

function Find-Iscc {
    $command = Get-Command 'ISCC.exe' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) { return $command.Source }

    $candidates = @(
        ${env:ProgramFiles(x86)}, $env:ProgramFiles, (Join-Path ([string]$env:LOCALAPPDATA) 'Programs')
    ) | Where-Object { $_ } | ForEach-Object { Join-Path $_ 'Inno Setup 6\ISCC.exe' }

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) { return $candidate }
    }
    return $null
}

# --- Version -------------------------------------------------------------------------------

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = Get-PropsVersion
}
$Version = $Version.Trim() -replace '^[vV]', ''
if ($Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?([-+][0-9A-Za-z.+-]+)?$') {
    throw "Invalid version '$Version'. Expected something like 1.2.3 or 1.2.3-beta.1."
}

Write-Host "JustMd build - version $Version, configuration $Configuration"

# Find Inno Setup up front so a missing compiler fails before a long build.
$Iscc = $null
if (-not $SkipInstaller) {
    $Iscc = Find-Iscc
    if (-not $Iscc) {
        throw ("Inno Setup 6 compiler (ISCC.exe) not found on PATH, in Program Files, or in " +
            "%LOCALAPPDATA%\Programs\Inno Setup 6. Install it with:`n" +
            "    winget install --id JRSoftware.InnoSetup -e`n" +
            "or re-run with -SkipInstaller.")
    }
}

# --- Clean ---------------------------------------------------------------------------------

Write-Step 'Cleaning artifacts'
if (Test-Path $ArtifactsDir) {
    Remove-Item -Recurse -Force $ArtifactsDir
}
New-Item -ItemType Directory -Force $ArtifactsDir | Out-Null

# --- Test ----------------------------------------------------------------------------------

if ($SkipTests) {
    Write-Step 'Skipping tests'
}
else {
    Write-Step 'Running tests'
    if (-not (Test-Path $Solution)) { throw "Solution not found: $Solution" }
    Invoke-Native 'dotnet' @('test', $Solution, '-c', $Configuration, "-p:Version=$Version", '--nologo')
}

# --- Publish -------------------------------------------------------------------------------

Write-Step 'Publishing application'
if (-not (Test-Path $AppProject)) { throw "Application project not found: $AppProject" }
# Single framework-dependent x64 build: the installer checks for the x64 .NET Desktop Runtime,
# which arm64 Windows runs under emulation.
Invoke-Native 'dotnet' @('publish', $AppProject, '-c', $Configuration, '-r', 'win-x64', '--self-contained', 'false',
    '-o', $PublishDir, "-p:Version=$Version", '--nologo')

$exe = Join-Path $PublishDir 'JustMd.exe'
if (-not (Test-Path $exe)) { throw "Publish did not produce $exe." }

# --- Installer -----------------------------------------------------------------------------

if ($SkipInstaller) {
    Write-Step 'Skipping installer'
    Write-Host "Published to $PublishDir"
    return
}

Write-Step "Compiling installer with $Iscc"
Invoke-Native $Iscc @(
    "/DAppVersion=$Version",
    "/DPublishDir=$PublishDir",
    "/DOutputDir=$InstallerDir",
    $InstallerIss
)

$installer = Join-Path $InstallerDir "JustMd-Setup-$Version.exe"
if (-not (Test-Path $installer)) { throw "Installer was not produced at $installer." }

$file = Get-Item $installer
$hash = (Get-FileHash -Algorithm SHA256 $file.FullName).Hash.ToLowerInvariant()
$checksumFile = "$($file.FullName).sha256"
# sha256sum-compatible format: "<hash>  <file name>"
Set-Content -Path $checksumFile -Value "$hash  $($file.Name)" -Encoding ascii

Write-Step 'Done'
Write-Host ("Installer : {0}" -f $file.FullName)
Write-Host ("Size      : {0:N2} MB ({1:N0} bytes)" -f ($file.Length / 1MB), $file.Length)
Write-Host ("SHA256    : {0}" -f $hash)
Write-Host ("Checksum  : {0}" -f $checksumFile)

if ($env:GITHUB_OUTPUT) {
    "installer=$($file.FullName)" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
    "checksum=$checksumFile" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
}
