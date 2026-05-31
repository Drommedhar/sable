#ifndef AppVersion
#define AppVersion "0.0.0"
#endif

#ifndef SourceDir
#define SourceDir "."
#endif

#ifndef OutputDir
#define OutputDir "."
#endif

#ifndef OutputBaseFilename
#define OutputBaseFilename "sable-windows-x64-setup"
#endif

#ifndef InstallerIconFile
#define InstallerIconFile "..\..\..\img\sable.ico"
#endif

[Setup]
AppId={{B7C2E1A4-3F8D-4C6E-9A21-5D7E0F2B9C44}
AppName=Sable
AppVersion={#AppVersion}
AppVerName=Sable {#AppVersion}
AppPublisher=Sable
DefaultDirName={autopf64}\Sable
DefaultGroupName=Sable
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBaseFilename}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile={#InstallerIconFile}
UninstallDisplayIcon={app}\Sable.App.exe
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Sable"; Filename: "{app}\Sable.App.exe"
Name: "{autodesktop}\Sable"; Filename: "{app}\Sable.App.exe"; Tasks: desktopicon

[Registry]
; associate the .sable document type with Sable (per-user, cleaned up on uninstall)
Root: HKA; Subkey: "Software\Classes\.sable"; ValueType: string; ValueName: ""; ValueData: "Sable.Document"; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\Sable.Document"; ValueType: string; ValueName: ""; ValueData: "Sable Document"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Sable.Document\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\Sable.App.exe,0"
Root: HKA; Subkey: "Software\Classes\Sable.Document\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Sable.App.exe"" ""%1"""

[Run]
Filename: "{app}\Sable.App.exe"; Description: "Launch Sable"; Flags: nowait postinstall skipifsilent
