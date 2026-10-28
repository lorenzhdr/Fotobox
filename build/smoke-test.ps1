<#
.SYNOPSIS
    Startet das veröffentlichte Fotobox.exe in einem isolierten Benutzerprofil und prüft das Log auf Startfehler.

.DESCRIPTION
    1. Legt einen leeren Temp-Ordner mit Profil an und biegt USERPROFILE und LOCALAPPDATA nur für den Start darauf um
       (die App ermittelt %LOCALAPPDATA% über Environment.GetFolderPath, das über "%USERPROFILE%\AppData\Local" aufgelöst
       wird - deshalb wird USERPROFILE mit umgebogen). Echte Einstellungen und Logs bleiben unberührt.
    2. Startet Fotobox.exe, wartet -Seconds Sekunden und beendet den Prozess. Endet er vorher, ist das ein Fehler.
    3. Gibt das Log aus und schlägt fehl, wenn es unbehandelte Ausnahmen oder einen Startfehler enthält.
       Kamera-Meldungen sind auf Rechnern ohne Kamera (z. B. CI-Runner) erwartet und kein Fehler.

    Kompatibel mit PowerShell 7 und Windows PowerShell 5.1. Exit-Code ungleich 0 bei Fehlern.

.PARAMETER PublishDir
    Publish-Ordner mit Fotobox.exe (z. B. artifacts/publish/x64). Er muss zur Architektur des Rechners passen.

.PARAMETER Seconds
    Laufzeit in Sekunden, bevor der Prozess beendet wird. Standard: 20.

.EXAMPLE
    pwsh build/smoke-test.ps1 -PublishDir artifacts/publish/x64
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PublishDir,
    [int]$Seconds = 20
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$tempRoot = $null
$proc = $null
$failed = $false

try {
    $PublishDir = (Resolve-Path -LiteralPath $PublishDir).Path
    $exe = Join-Path $PublishDir 'Fotobox.exe'
    if (-not (Test-Path -LiteralPath $exe)) {
        throw "Fotobox.exe fehlt in $PublishDir."
    }
    if ($Seconds -lt 1) { throw 'Seconds muss mindestens 1 sein.' }

    $tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('fotobox-smoke-' + [guid]::NewGuid().ToString('N'))
    $profileDir = Join-Path $tempRoot 'Profile'
    $localAppData = Join-Path $profileDir 'AppData\Local'
    New-Item -ItemType Directory -Force -Path $localAppData | Out-Null

    $oldProfile = $env:USERPROFILE
    $oldLocal = $env:LOCALAPPDATA
    try {
        $env:USERPROFILE = $profileDir
        $env:LOCALAPPDATA = $localAppData
        $proc = Start-Process -FilePath $exe -WorkingDirectory $PublishDir -PassThru
    }
    finally {
        $env:USERPROFILE = $oldProfile
        $env:LOCALAPPDATA = $oldLocal
    }
    # Handle früh öffnen, damit der Exit-Code auch bei schnell endenden Prozessen lesbar bleibt.
    $null = $proc.Handle
    Write-Host "Fotobox.exe gestartet (PID $($proc.Id)), Wartezeit $Seconds s ..."

    for ($i = 1; $i -le $Seconds; $i++) {
        Start-Sleep -Seconds 1
        $proc.Refresh()
        if ($proc.HasExited) {
            $code = $proc.ExitCode
            $earlyLogDir = Join-Path $localAppData 'Fotobox\logs'
            if (Test-Path -LiteralPath $earlyLogDir) {
                foreach ($f in @(Get-ChildItem -LiteralPath $earlyLogDir -Filter 'fotobox-*.log' -File)) {
                    Write-Host "--- $($f.Name) ---"
                    Write-Host (Get-Content -LiteralPath $f.FullName -Raw -Encoding UTF8)
                }
            }
            throw "Fotobox.exe endete nach $i s von selbst (Exit-Code $code)."
        }
    }

    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    $null = $proc.WaitForExit(10000)

    $logDir = Join-Path $localAppData 'Fotobox\logs'
    $logs = @()
    if (Test-Path -LiteralPath $logDir) {
        $logs = @(Get-ChildItem -LiteralPath $logDir -Filter 'fotobox-*.log' -File)
    }
    if ($logs.Count -eq 0) {
        throw 'Keine Logdatei (fotobox-*.log) im isolierten Profil gefunden: Die Isolierung griff nicht oder die App hat nicht geloggt.'
    }

    $logText = ''
    foreach ($log in $logs) {
        $content = Get-Content -LiteralPath $log.FullName -Raw -Encoding UTF8
        if ($null -eq $content) { $content = '' }
        Write-Host "--- $($log.Name) ---"
        Write-Host $content
        $logText += $content
    }

    $fatal = @(
        'Unbehandelte Ausnahme',
        'Nicht beobachtete Task-Ausnahme',
        'Die Fotobox konnte nicht gestartet werden',
        'Einzelinstanz-Sperre'
    )
    $hits = @($fatal | Where-Object { $logText.Contains($_) })
    if ($hits.Count -gt 0) {
        throw "Das Log enthält Fehler: $($hits -join '; ')."
    }

    Write-Host "Smoke-Test ok: Fotobox.exe lief $Seconds s ohne unbehandelte Fehler." -ForegroundColor Green
}
catch {
    Write-Host "FEHLER: $($_.Exception.Message)" -ForegroundColor Red
    $failed = $true
}
finally {
    if ($proc) {
        try {
            if (-not $proc.HasExited) {
                Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
                $null = $proc.WaitForExit(5000)
            }
        }
        catch { }
    }
    if ($tempRoot -and (Test-Path -LiteralPath $tempRoot)) {
        try { Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue } catch { }
    }
}

if ($failed) { exit 1 }
