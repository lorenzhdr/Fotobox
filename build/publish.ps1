<#
.SYNOPSIS
    Erstellt self-contained Publish-Ordner, portable ZIPs und (optional) Inno-Setup-Installer von Fotobox+.

.DESCRIPTION
    Je Architektur (x64, arm64):
      1. dotnet publish (Release, self-contained) nach artifacts/publish/<arch>
      2. VC++-Runtime: app-lokale Kopie der Microsoft.VC14x.CRT-DLLs (msvcp140*.dll, vcruntime140*.dll ...) in den
         Publish-Ordner, weil onnxruntime.dll sie braucht und self-contained .NET sie nicht mitliefert
      3. Statische Prüfung: Architektur nativer Dateien und Auflösbarkeit ihrer Import-Abhängigkeiten
      4. ZIP:       artifacts/FotoboxPlus-<Version>-<arch>-portable.zip
      5. Installer: artifacts/FotoboxPlus-Setup-<Version>-<arch>.exe (nur wenn ISCC.exe gefunden wird)

    Kompatibel mit PowerShell 7 und Windows PowerShell 5.1. Exit-Code ungleich 0 bei Fehlern.

.PARAMETER Version
    Versionsnummer (z. B. 1.2.3 oder 0.0.0-test). Standard: <Version> aus Directory.Build.props, sonst 1.0.0.

.PARAMETER Arch
    Zielarchitekturen: x64, arm64. Standard: beide.

.PARAMETER SkipInstaller
    Installer nicht bauen (nur Publish + ZIP).

.PARAMETER RequireInstaller
    Fehlt ISCC.exe, wird mit Fehler abgebrochen (statt nur zu warnen).

.PARAMETER RequireVcRuntime
    Die VC++-Runtime muss gefunden werden (Visual Studio mit vswhere, Ordner VC\Redist\MSVC\<Version>\<arch>) und neu
    genug sein (msvcp140.dll mindestens 14.40 und mindestens die Linker-Version von onnxruntime.dll). Sonst Abbruch
    mit Fehler. Ohne den Schalter nur eine Warnung: Das Paket enthält dann keine VC++-Runtime und die KI-Erkennung
    (ONNX Runtime) startet auf PCs ohne installierte VC++ Redistributable nicht.

.EXAMPLE
    pwsh build/publish.ps1 -Version 0.0.1 -SkipInstaller
#>
[CmdletBinding()]
param(
    [string]$Version,
    [ValidateSet('x64', 'arm64')]
    [string[]]$Arch = @('x64', 'arm64'),
    [switch]$SkipInstaller,
    [switch]$RequireInstaller,
    [switch]$RequireVcRuntime
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

function Find-VcCrtDir {
    # Sucht den neuesten Ordner VC\Redist\MSVC\<Version>\<arch>\Microsoft.VC14*.CRT in allen Visual-Studio-Installationen.
    # Gibt den Pfad zurück oder $null.
    param([string]$Arch)

    if (-not ${env:ProgramFiles(x86)}) { return $null }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { return $null }

    $installs = @()
    try {
        $installs = @(& $vswhere -all -products '*' -property installationPath | Where-Object { $_ })
    }
    catch {
        Write-Warning "vswhere.exe ließ sich nicht ausführen: $($_.Exception.Message)"
        return $null
    }

    $found = @()
    foreach ($install in $installs) {
        $redist = Join-Path $install 'VC\Redist\MSVC'
        if (-not (Test-Path -LiteralPath $redist)) { continue }
        foreach ($verDir in @(Get-ChildItem -LiteralPath $redist -Directory -ErrorAction SilentlyContinue)) {
            if ($verDir.Name -notmatch '^\d+\.\d+\.\d+$') { continue }
            $archDir = Join-Path $verDir.FullName $Arch
            if (-not (Test-Path -LiteralPath $archDir)) { continue }
            $crtDirs = @(Get-ChildItem -LiteralPath $archDir -Directory -Filter 'Microsoft.VC14*.CRT' -ErrorAction SilentlyContinue |
                Sort-Object Name -Descending)
            foreach ($crt in $crtDirs) {
                if (Test-Path -LiteralPath (Join-Path $crt.FullName 'msvcp140.dll')) {
                    $found += [pscustomobject]@{ Version = [version]$verDir.Name; Path = $crt.FullName }
                    break
                }
            }
        }
    }

    if ($found.Count -eq 0) { return $null }
    return ($found | Sort-Object Version -Descending | Select-Object -First 1).Path
}

function Get-PeInfo {
    # Liest die für die Prüfung nötigen Felder einer PE-Datei (Machine, Linker-Version, Managed-Kennzeichen, Imports).
    # Gibt $null zurück, wenn die Datei keine (lesbare) PE-Datei ist.
    param([string]$Path)

    try {
        $b = [System.IO.File]::ReadAllBytes($Path)
        if ($b.Length -lt 0x100 -or $b[0] -ne 0x4D -or $b[1] -ne 0x5A) { return $null }

        $pe = [int][BitConverter]::ToUInt32($b, 0x3C)
        if ($pe -lt 0 -or ($pe + 24) -gt $b.Length) { return $null }
        if ($b[$pe] -ne 0x50 -or $b[$pe + 1] -ne 0x45 -or $b[$pe + 2] -ne 0 -or $b[$pe + 3] -ne 0) { return $null }

        $machine = [int][BitConverter]::ToUInt16($b, $pe + 4)
        $sectionCount = [int][BitConverter]::ToUInt16($b, $pe + 6)
        $optSize = [int][BitConverter]::ToUInt16($b, $pe + 20)
        $opt = $pe + 24
        if (($opt + $optSize) -gt $b.Length -or $optSize -lt 96) { return $null }

        $magic = [int][BitConverter]::ToUInt16($b, $opt)
        if ($magic -eq 0x10B) { $countOffset = 92; $dirOffset = 96 }
        elseif ($magic -eq 0x20B) { $countOffset = 108; $dirOffset = 112 }
        else { return $null }

        $linker = New-Object System.Version ([int]$b[$opt + 2]), ([int]$b[$opt + 3])
        $dirCount = 0
        if (($countOffset + 4) -le $optSize) { $dirCount = [long][BitConverter]::ToUInt32($b, $opt + $countOffset) }

        # Datenverzeichnis lesen: liefert RVA und Größe (0/0, wenn nicht vorhanden)
        $getDir = {
            param([int]$Index)
            if ($Index -ge $dirCount -or ($dirOffset + ($Index + 1) * 8) -gt $optSize) { return @(0L, 0L) }
            $o = $opt + $dirOffset + $Index * 8
            return @([long][BitConverter]::ToUInt32($b, $o), [long][BitConverter]::ToUInt32($b, $o + 4))
        }

        # Sektionstabelle direkt hinter dem Optional Header (je 40 Byte)
        $sections = @()
        $secTable = $opt + $optSize
        for ($i = 0; $i -lt $sectionCount; $i++) {
            $o = $secTable + 40 * $i
            if (($o + 40) -gt $b.Length) { return $null }
            $sections += [pscustomobject]@{
                VirtualSize = [long][BitConverter]::ToUInt32($b, $o + 8)
                VirtualAddress = [long][BitConverter]::ToUInt32($b, $o + 12)
                RawSize = [long][BitConverter]::ToUInt32($b, $o + 16)
                RawPointer = [long][BitConverter]::ToUInt32($b, $o + 20)
            }
        }

        $rvaToOffset = {
            param([long]$Rva)
            foreach ($s in $sections) {
                $span = $s.VirtualSize
                if ($s.RawSize -gt $span) { $span = $s.RawSize }
                if ($Rva -ge $s.VirtualAddress -and $Rva -lt ($s.VirtualAddress + $span)) {
                    return ($s.RawPointer + ($Rva - $s.VirtualAddress))
                }
            }
            return -1L
        }

        $clr = & $getDir 14
        $isManaged = ([long]$clr[0] -ne 0)

        $imports = New-Object 'System.Collections.Generic.List[string]'
        $imp = & $getDir 1
        if ([long]$imp[0] -ne 0) {
            $off = [long](& $rvaToOffset ([long]$imp[0]))
            if ($off -lt 0) { return $null }
            for ($n = 0; $n -lt 4096; $n++) {
                $d = $off + 20 * $n
                if (($d + 20) -gt $b.Length) { return $null }
                $allZero = $true
                for ($k = 0; $k -lt 20; $k++) {
                    if ($b[$d + $k] -ne 0) { $allZero = $false; break }
                }
                if ($allZero) { break }
                $nameRva = [long][BitConverter]::ToUInt32($b, [int]($d + 12))
                $nameOff = [long](& $rvaToOffset $nameRva)
                if ($nameOff -lt 0 -or $nameOff -ge $b.Length) { return $null }
                $end = [int]$nameOff
                while ($end -lt $b.Length -and $b[$end] -ne 0 -and ($end - $nameOff) -lt 260) { $end++ }
                if ($end -ge $b.Length) { return $null }
                $imports.Add([System.Text.Encoding]::ASCII.GetString($b, [int]$nameOff, $end - [int]$nameOff))
            }
        }

        return [pscustomobject]@{
            Machine = $machine
            IsManaged = $isManaged
            LinkerVersion = $linker
            Imports = $imports.ToArray()
        }
    }
    catch {
        Write-Warning "PE-Datei nicht lesbar: $([System.IO.Path]::GetFileName($Path)) ($($_.Exception.Message))"
        return $null
    }
}

function Copy-VcRuntime {
    # Kopiert die VC++-CRT app-lokal in den Publish-Ordner. Gibt $true zurück, wenn kopiert wurde.
    # Ohne -Require führen fehlende/zu alte Runtime nur zu einer Warnung ($false), mit -Require zum Abbruch.
    param([string]$PublishDir, [string]$Arch, [bool]$Require)

    $crtDir = Find-VcCrtDir -Arch $Arch
    if (-not $crtDir) {
        $msg = "Keine VC++-Runtime ($Arch) gefunden (Visual Studio mit 'VC\Redist\MSVC\<Version>\$Arch\Microsoft.VC14*.CRT' via vswhere)."
        if ($Require) { throw "$msg -RequireVcRuntime ist gesetzt." }
        Write-Warning "$msg Das Paket enthält dann keine VC++-Runtime: Auf PCs ohne installierte VC++ Redistributable steht die KI-Erkennung (ONNX Runtime) nicht zur Verfügung."
        return $false
    }

    $msvcp = Join-Path $crtDir 'msvcp140.dll'
    $fileVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($msvcp)
    $crtVersion = New-Object System.Version $fileVersion.FileMajorPart, $fileVersion.FileMinorPart
    $needed = New-Object System.Version 14, 40
    $ortPath = Join-Path $PublishDir 'onnxruntime.dll'
    $ortInfo = $null
    if (Test-Path -LiteralPath $ortPath) { $ortInfo = Get-PeInfo -Path $ortPath }
    if ($ortInfo) {
        $linker = New-Object System.Version $ortInfo.LinkerVersion.Major, $ortInfo.LinkerVersion.Minor
        if ($linker -gt $needed) { $needed = $linker }
    }

    if ($crtVersion -lt $needed) {
        $msg = "Die gefundene VC++-Runtime ($Arch) ist zu alt: msvcp140.dll $crtVersion, benötigt wird mindestens $needed."
        if ($Require) { throw "$msg -RequireVcRuntime ist gesetzt." }
        Write-Warning "$msg Sie wird nicht kopiert; auf PCs ohne installierte VC++ Redistributable steht die KI-Erkennung nicht zur Verfügung."
        return $false
    }

    $expectedMachine = 0x8664
    if ($Arch -eq 'arm64') { $expectedMachine = 0xAA64 }

    Write-Host "VC++-Runtime ($Arch, msvcp140.dll $crtVersion) wird app-lokal kopiert:"
    foreach ($dll in @(Get-ChildItem -LiteralPath $crtDir -Filter '*.dll' -File)) {
        # Die arm64-Redist enthält auch ARM64EC-DLLs mit x64-Kopf (z. B. vcruntime140_1.dll) für emulierten x64-Code;
        # native arm64-Dateien brauchen sie nicht. Nur DLLs der Zielarchitektur kopieren (fehlt etwas, meldet es die Importprüfung).
        $dllInfo = Get-PeInfo -Path $dll.FullName
        if (-not $dllInfo -or $dllInfo.Machine -ne $expectedMachine) {
            Write-Host "  ($($dll.Name) übersprungen: andere Architektur)"
            continue
        }
        Copy-Item -LiteralPath $dll.FullName -Destination $PublishDir -Force
        Write-Host "  $($dll.Name)"
    }
    return $true
}

function Test-NativeDependencies {
    # Statische Prüfung des Publish-Ordners: native Dateien haben die Zielarchitektur, und jeder Import ist auflösbar
    # (Publish-Ordner, API-Sets oder System32). VC++-Runtime-DLLs (msvcp140 ...) müssen im Publish-Ordner liegen,
    # weil sie auf frischen PCs nicht in System32 stehen. Mit -CrtAsWarning sind genau diese Verstöße nur Warnungen.
    param([string]$Dir, [string]$Arch, [bool]$CrtAsWarning = $false)

    $expected = 0x8664
    if ($Arch -eq 'arm64') { $expected = 0xAA64 }
    $system32 = Join-Path $env:windir 'System32'

    $local = @{}
    foreach ($f in @(Get-ChildItem -LiteralPath $Dir -File)) { $local[$f.Name.ToLowerInvariant()] = $true }

    $errors = New-Object 'System.Collections.Generic.List[string]'
    $warnings = New-Object 'System.Collections.Generic.List[string]'
    $nativeCount = 0

    foreach ($file in @(Get-ChildItem -LiteralPath $Dir -File | Where-Object { $_.Extension -in '.dll', '.exe' })) {
        $info = Get-PeInfo -Path $file.FullName
        if (-not $info -or $info.IsManaged) { continue }
        $nativeCount++

        # Microsoft.DiaSymReader.Native.<arch>.dll (Symbolleser der .NET-Runtime) darf in anderer Architektur beiliegen.
        if ($info.Machine -ne $expected -and $file.Name -notlike 'Microsoft.DiaSymReader.Native.*') {
            $errors.Add(('{0}: falsche Architektur (Machine 0x{1:X4}, erwartet 0x{2:X4} für {3})' -f $file.Name, $info.Machine, $expected, $Arch))
        }

        foreach ($import in @($info.Imports | Sort-Object -Unique)) {
            $name = $import.ToLowerInvariant()
            if ($name -match '^(msvcp|vcruntime|concrt|vccorlib)\d') {
                if (-not $local.ContainsKey($name)) {
                    $line = "{0}: importiert {1}, die im Publish-Ordner fehlt (VC++-Runtime muss app-lokal mitgeliefert werden)" -f $file.Name, $import
                    if ($CrtAsWarning) { $warnings.Add($line) } else { $errors.Add($line) }
                }
                continue
            }
            if ($local.ContainsKey($name)) { continue }
            if ($name.StartsWith('api-ms-win-') -or $name.StartsWith('ext-ms-')) { continue }
            if (Test-Path -LiteralPath (Join-Path $system32 $import)) { continue }
            $errors.Add(("{0}: importiert {1}, die weder im Publish-Ordner noch in System32 liegt" -f $file.Name, $import))
        }
    }

    foreach ($w in $warnings) { Write-Warning $w }
    if ($errors.Count -gt 0) {
        $list = ($errors | ForEach-Object { "  - $_" }) -join [Environment]::NewLine
        throw "Abhängigkeitsprüfung ($Arch) fehlgeschlagen:$([Environment]::NewLine)$list"
    }
    Write-Host "Abhängigkeitsprüfung ok ($nativeCount native Dateien)."
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

        $vcCopied = Copy-VcRuntime -PublishDir $publishDir -Arch $a -Require ([bool]$RequireVcRuntime)
        Test-NativeDependencies -Dir $publishDir -Arch $a -CrtAsWarning (-not $vcCopied)

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
