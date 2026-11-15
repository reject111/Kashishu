; Lyricify Island per-user Windows installer.
; Build with build-installer.ps1 (passes /DAppVersion=<version> to ISCC).

#ifndef AppVersion
  #define AppVersion "0.1.13"
#endif

[Setup]
AppId={{889B1133-C1C8-49D1-B3C2-0D37A15FC8FC}
AppName=Lyricify Island
AppVersion={#AppVersion}
AppVerName=Lyricify Island {#AppVersion}
AppPublisher=reject111
AppPublisherURL=https://github.com/reject111/LyricifyIsland
AppSupportURL=https://github.com/reject111/LyricifyIsland/issues
AppUpdatesURL=https://github.com/reject111/LyricifyIsland/releases
DefaultDirName={localappdata}\Programs\LyricifyIsland
DefaultGroupName=Lyricify Island
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
MinVersion=10.0.17763
OutputDir=..\dist
OutputBaseFilename=LyricifyIsland-Setup-{#AppVersion}
SetupIconFile=..\packaging\lyricify-island.ico
UninstallDisplayIcon={app}\LyricifyIsland.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
VersionInfoVersion={#AppVersion}
VersionInfoCompany=reject111
VersionInfoDescription=Lyricify Island Setup
VersionInfoProductName=Lyricify Island
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\dist\LyricifyIsland.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\THIRD_PARTY_NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\Lyricify Island"; Filename: "{app}\LyricifyIsland.exe"
Name: "{userdesktop}\Lyricify Island"; Filename: "{app}\LyricifyIsland.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\LyricifyIsland.exe"; Description: "{cm:LaunchProgram,Lyricify Island}"; Flags: nowait postinstall skipifsilent

[Registry]
; The app writes its own auto-start entry; remove it when the app is uninstalled.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "LyricifyIsland"; Flags: dontcreatekey uninsdeletevalue
