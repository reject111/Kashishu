; Kashishu per-user Windows installer.
; Build with build-installer.ps1 (passes /DAppVersion=<version> to ISCC).

#ifndef AppVersion
  #define AppVersion "0.1.14"
#endif

[Setup]
AppId={{889B1133-C1C8-49D1-B3C2-0D37A15FC8FC}
AppName=Kashishu
AppVersion={#AppVersion}
AppVerName=Kashishu {#AppVersion}
AppPublisher=reject111
AppPublisherURL=https://github.com/reject111/LyricifyIsland
AppSupportURL=https://github.com/reject111/LyricifyIsland/issues
AppUpdatesURL=https://github.com/reject111/LyricifyIsland/releases
DefaultDirName={localappdata}\Programs\Kashishu
DefaultGroupName=Kashishu
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
MinVersion=10.0.17763
OutputDir=..\dist
OutputBaseFilename=Kashishu-Setup-{#AppVersion}
SetupIconFile=..\packaging\kashishu.ico
UninstallDisplayIcon={app}\Kashishu.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
VersionInfoVersion={#AppVersion}
VersionInfoCompany=reject111
VersionInfoDescription=Kashishu Setup
VersionInfoProductName=Kashishu
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\dist\Kashishu.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\THIRD_PARTY_NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\Kashishu"; Filename: "{app}\Kashishu.exe"
Name: "{userdesktop}\Kashishu"; Filename: "{app}\Kashishu.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Kashishu.exe"; Description: "{cm:LaunchProgram,Kashishu}"; Flags: nowait postinstall skipifsilent

[Registry]
; The app writes its own auto-start entry; remove it when the app is uninstalled.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "LyricifyIsland"; Flags: dontcreatekey uninsdeletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "Kashishu"; Flags: dontcreatekey uninsdeletevalue
