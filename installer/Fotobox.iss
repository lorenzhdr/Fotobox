; Inno Setup 6.3+ Skript fuer Fotobox+.
; Aufruf (siehe build/publish.ps1):
;   ISCC /DAppVersion=1.0.0 /DArch=x64 /DSourceDir=<publish-ordner> /O<ausgabeordner> installer\Fotobox.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish\" + Arch
#endif
#if (Arch != "x64") && (Arch != "arm64")
  #error Arch muss "x64" oder "arm64" sein.
#endif

; Rein numerische Dateiversion (x.y.z.0) aus AppVersion, z. B. 1.2.3-beta.1 -> 1.2.3.0
#define VersionText Str(AppVersion)
#define VersionNoPre Copy(VersionText, 1, Pos("-", VersionText + "-") - 1)
#define NumericVersion Copy(VersionNoPre, 1, Pos("+", VersionNoPre + "+") - 1) + ".0"

#define AppName "Fotobox+"
#define AppExe "Fotobox.exe"
; Dateiname des Installers ohne "+" (GitHub-Release-Assets: Sonderzeichen werden evtl. umbenannt).
#define FileBase "FotoboxPlus"

[Setup]
; Feste GUID - niemals aendern (identifiziert die Installation fuer Updates/Deinstallation).
AppId={{53A1DE88-5A3F-4B7B-90D0-6BA0D8091438}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Fotobox+
VersionInfoVersion={#NumericVersion}
VersionInfoProductName={#AppName}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
SetupIconFile=..\src\Fotobox\Assets\fotobox.ico
OutputBaseFilename={#FileBase}-Setup-{#AppVersion}-{#Arch}
; OutputDir wird per /O von build/publish.ps1 gesetzt.
OutputDir=..\artifacts
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
MinVersion=10.0.19041
; Pro Benutzer ohne Admin-Rechte; im Dialog auf "fuer alle Benutzer" umschaltbar.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
CloseApplications=yes
RestartApplications=no
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif

[Languages]
#if FileExists(CompilerPath + "Languages\German.isl")
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
#else
  #pragma message "WARNUNG: German.isl wurde in der Inno-Setup-Installation nicht gefunden - Installer wird nur auf Englisch erstellt."
#endif
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; Update ueber Version 1.x: alte Startmenue-Verknuepfung "Fotobox" entfernen, sonst gaebe es neben "Fotobox+" ein Duplikat.
Type: files; Name: "{autoprograms}\Fotobox.lnk"

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent runasoriginaluser

; Absichtlich keine [UninstallDelete]-Eintraege: %LOCALAPPDATA%\Fotobox (Einstellungen, Logs, Overlays)
; und die gespeicherten Fotos bleiben bei der Deinstallation erhalten.
