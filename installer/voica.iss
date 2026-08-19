; Inno Setup script for the Voica installer.
;
; Builds a per-user installer around the self-contained single-file publish, so a plain
; user gets Program-files-style placement, a Start menu entry and a proper uninstall record
; without ever seeing a UAC prompt. The two bare .exe assets stay on the releases page for
; people who know which one they want; this is the default path for everyone else.
;
; Build (version comes from the tag in CI, defaults here so a local run works):
;   ISCC.exe /DAppVersion=0.6.2 installer\voica.iss
; Expects the publish output at out\self\Voica.exe relative to the repository root.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#define AppName "Voica"
#define AppPublisher "Inhum"
#define AppURL "https://voica.ru"

[Setup]
; Never change AppId — it is what ties an upgrade to the installation it replaces.
AppId={{5C7C9133-4FC4-412B-B7AD-F603E15838C2}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL=https://github.com/Inhum/voica-win
AppUpdatesURL=https://github.com/Inhum/voica-win/releases
VersionInfoVersion={#AppVersion}

; Per-user install: {autopf} resolves to {localappdata}\Programs under PrivilegesRequired=lowest,
; which keeps UAC out of the picture entirely. An unsigned installer asking for elevation shows
; the "Publisher: Unknown" dialog, which is the scariest thing we could put in front of a user.
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE

; Matches SupportedOSPlatformVersion in Voica.csproj (Windows 10 1809).
MinVersion=10.0.17763
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Installing over a running copy: let the Restart Manager find and close it. Voica is tray-only,
; so it must not be AppMutex — that check fires first and only puts up a "close all instances of it
; now" box, leaving the user to hunt for a window that does not exist, and failing outright on a
; silent install. CloseApplications spots the process through the file it locks and closes it
; itself, showing the standard "applications are using files" page in the wizard.
CloseApplications=yes
CloseApplicationsFilter=*.exe
RestartApplications=no

OutputDir=..\out\installer
OutputBaseFilename=Voica-Setup-{#AppVersion}
SetupIconFile=..\src\Voica\Resources\voica.ico
UninstallDisplayIcon={app}\Voica.exe
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\out\self\Voica.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\Voica.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\Voica.exe"; Tasks: desktopicon

[Run]
; Tray-only app: nowait, or the installer would sit there waiting for it to exit.
Filename: "{app}\Voica.exe"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

; Note: uninstall deliberately leaves %APPDATA%\Voica alone (history, settings, API key).
; Wiping user data belongs to the app's own "Delete all data" flow, which asks for a typed
; confirmation — an uninstaller has no business doing it silently.
