; Per-user installer: no administrator rights needed to install, except one prompt to trust the certificate
; that the Windows 11 menu entry is signed with.
; Builds from a release tag pass /DAppVersion=X.Y.Z.
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

[Setup]
AppId={{ECF0B5BB-81DD-438B-94DD-2A0DDF6E92F8}
AppName=WinDeepSearch
AppVersion={#AppVersion}
AppPublisher=WinDeepSearch
DefaultDirName={localappdata}\Programs\WinDeepSearch
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir=..\artifacts
OutputBaseFilename=WinDeepSearch-Setup
SetupIconFile=..\src\WinDeepSearch\Assets\WinDeepSearch.ico
UninstallDisplayIcon={app}\WinDeepSearch.exe
WizardStyle=modern
; The menu command may still be loaded by Windows when upgrading; let Setup close it.
CloseApplications=force
Compression=lzma2
SolidCompression=yes

[Files]
Source: "..\artifacts\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"

[Icons]
Name: "{userprograms}\WinDeepSearch"; Filename: "{app}\WinDeepSearch.exe"

[Run]
Filename: "{app}\WinDeepSearch.exe"; Parameters: "--register"; Flags: waituntilterminated; \
  StatusMsg: "Adding WinDeepSearch to the right-click menu. Windows asks once to trust its certificate."
Filename: "{app}\WinDeepSearch.exe"; Description: "Open WinDeepSearch"; Flags: postinstall nowait skipifsilent

[UninstallRun]
Filename: "{app}\WinDeepSearch.exe"; Parameters: "--unregister"; Flags: waituntilterminated; RunOnceId: "UnregisterMenus"
