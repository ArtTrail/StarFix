; StarFix Windows installer (Inno Setup).
;
; Per-user install (no admin/UAC needed) into {localappdata}\StarFix — deliberately separate
; from the app's own user-data folder (%AppData%\StarFix, i.e. Roaming — config, session logs,
; results history, the downloaded Gaia catalog), which lives elsewhere and is never touched by
; install or uninstall. AppId is a fixed GUID so future versions upgrade in place instead of
; installing side-by-side.
;
; Build: requires publish\win-x64\ to already exist (dotnet publish -c Release -r win-x64
; --self-contained true -o publish\win-x64), then run from the installer\ directory:
;   "C:\Users\<you>\AppData\Local\Programs\Inno Setup 6\ISCC.exe" StarFix.iss

#define MyAppName "StarFix"
#define MyAppVersion "1.1.0"
#define MyAppPublisher "Art Trail"
#define MyAppURL "https://github.com/ArtTrail/StarFix"
#define MyAppExeName "StarFix.exe"

[Setup]
AppId={{49643980-6D6D-4A5F-8D74-2CC7D1958686}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={localappdata}\{#MyAppName}
DefaultGroupName={#MyAppName}
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
OutputDir=..\publish
OutputBaseFilename=StarFix-Setup-v{#MyAppVersion}
SetupIconFile=..\Assets\StarFix.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "..\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent
