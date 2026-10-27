<#
.SYNOPSIS
    Erstellt self-contained Publish-Ordner, portable ZIPs und (optional) Inno-Setup-Installer von Fotobox+.

.DESCRIPTION
    Je Architektur (x64, arm64):
      1. dotnet publish (Release, self-contained) nach artifacts/publish/<arch>
      2. ZIP:       artifacts/FotoboxPlus-<Version>-<arch>-portable.zip
      3. Installer: artifacts/FotoboxPlus-Setup-<Version>-<arch>.exe (nur wenn ISCC.exe gefunden wird)

    Kompatibel mit PowerShell 7 und Windows PowerShell 5.1. Exit-Code ungleich 0 bei Fehlern.

.PARAMETER Version
    Versionsnummer (z. B. 1.2.3 oder 0.0.0-test). Standard: <Version> aus Directory.Build.props, sonst 1.0.0.

.PARAMETER Arch
    Zielarchitekturen: x64, arm64. Standard: beide.

.PARAMETER SkipInstaller
    Installer nicht bauen (nur Publish + ZIP).

.PARAMETER RequireInstaller
    Fehlt ISCC.exe, wird mit Fehler abgebrochen (statt nur zu warnen).

.EXAMPLE
    pwsh build/publish.ps1 -Version 0.0.1 -SkipInstaller
#>
[CmdletBinding()]
param(
    [string]$Version,
    [ValidateSet('x64', 'arm64')]
    [string[]]$Arch = @('x64', 'arm64'),
    [switch]$SkipInstaller,
    [switch]$RequireInstaller
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\Fotobox\Fotobox.csproj'
$issFile = Join-Path $repoRoot 'installer\Fotobox.iss'
$artifacts = Join-Path $repoRoot 'artifacts'

function Get-DefaultVersion {
    $props = Join-Path $repoRoot 'Directory.Build.props'
    if (Test-Path -LiteralPath $props) {
        $match = Select-String -LiteralPath $props -Pattern '<Version>\s*([^<\s]+)\s*</Version>' | Select-Object -First 1
        if ($match) { return $match.Matches[0].Groups[1].Value }
    }
    return '1.0.0'
}

function Find-Iscc {
    $cmd = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    $candidates = @()
    if (${env:ProgramFiles(x86)}) { $candidates += (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe') }
    if ($env:ProgramFiles) { $candidates += (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe') }
    if ($env:LOCALAPPDATA) { $candidates += (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe') }
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }
    return $null
}

function Invoke-Native {
    param([string]$Exe, [string[]]$Arguments)
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "'$Exe' wurde mit Exit-Code $LASTEXITCODE beendet."
    }
}

function New-PortableZip {
    param([string]$SourceDir, [string]$ZipPath)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    if (Test-Path -LiteralPath $ZipPath) { Remove-Item -LiteralPath $ZipPath -Force }
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $SourceDir, $ZipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)
}

try {
    if ([string]::IsNullOrWhiteSpace($Version)) { $Version = Get-DefaultVersion }
    if ($Version -notmatch '^\d+\.\d+\.\d+([-+][0-9A-Za-z.\-+]+)?$') {
        throw "Ungültige Version '$Version' (erwartet z. B. 1.2.3 oder 1.2.3-beta.1)."
    }
    $Arch = @($Arch | Select-Object -Unique)

    $iscc = $null
    if (-not $SkipInstaller) {
        $iscc = Find-Iscc
        if (-not $iscc) {
            if ($RequireInstaller) {
                throw 'ISCC.exe (Inno Setup 6) wurde nicht gefunden, aber -RequireInstaller ist gesetzt.'
            }
            Write-Warning 'ISCC.exe (Inno Setup 6) nicht gefunden - es werden nur Publish-Ordner und ZIPs erstellt.'
        }
        else {
            Write-Host "Inno Setup: $iscc"
        }
    }

    New-Item -ItemType Directory -Force -Path $artifacts | Out-Null

    foreach ($a in $Arch) {
        $rid = "win-$a"
        $publishDir = Join-Path $artifacts "publish\$a"
        $zipPath = Join-Path $artifacts "FotoboxPlus-$Version-$a-portable.zip"

        Write-Host ''
        Write-Host "=== Fotobox+ $Version ($rid) ===" -ForegroundColor Cyan

        if (Test-Path -LiteralPath $publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force }

        Invoke-Native 'dotnet' @(
            'publish', $project,
            '-c', 'Release',
            '-r', $rid,
            '--self-contained', 'true',
            "-p:Version=$Version",
            '-o', $publishDir
        )

        if (-not (Test-Path -LiteralPath (Join-Path $publishDir 'Fotobox.exe'))) {
            throw "Fotobox.exe fehlt in $publishDir."
        }

        New-PortableZip -SourceDir $publishDir -ZipPath $zipPath
        Write-Host "ZIP: $zipPath"

        if ($iscc) {
            Invoke-Native $iscc @(
                "/DAppVersion=$Version",
                "/DArch=$a",
                "/DSourceDir=$publishDir",
                "/O$artifacts",
                $issFile
            )
        }
    }

    Write-Host ''
    Write-Host 'Fertig. Artefakte:' -ForegroundColor Green
    Get-ChildItem -LiteralPath $artifacts -File |
        Where-Object { $_.Extension -in '.zip', '.exe' } |
        ForEach-Object { Write-Host ('  {0}  ({1:N1} MB)' -f $_.Name, ($_.Length / 1MB)) }
}
catch {
    Write-Host "FEHLER: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
