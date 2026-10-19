; Stringline installer (S18), built by build/publish.ps1 -Installer with Inno Setup 6:
;   ISCC /DAppVersion=0.1.0 /DAppNumericVersion=0.1.0 /DSourceDir=<publish folder> /DOutputDir=<folder> installer/Stringline.iss
; It installs the single-file Stringline.exe with its licence and notices, for the current user by default (no admin
; prompt) or for all users. Uninstalling leaves settings, profiles, sessions and logs in place.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef AppNumericVersion
  #define AppNumericVersion AppVersion
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish\Stringline"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif

[Setup]
; Never change the AppId: it is how upgrades and the uninstaller find an existing install.
AppId={{6E0B7C2A-5F1D-4C8E-9A3B-2D7F41C9E8B5}
AppName=Stringline
AppVersion={#AppVersion}
AppVerName=Stringline {#AppVersion}
AppPublisher=Frederick Duncan and Stringline contributors
AppPublisherURL=https://github.com/frddunc-glitch/Stringline
AppSupportURL=https://github.com/frddunc-glitch/Stringline/issues
AppUpdatesURL=https://github.com/frddunc-glitch/Stringline/releases
AppCopyright=Copyright (C) 2026 Frederick Duncan and Stringline contributors
VersionInfoVersion={#AppNumericVersion}
VersionInfoDescription=Stringline setup
DefaultDirName={autopf}\Stringline
DefaultGroupName=Stringline
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 2004 or later (SkiaSharp's WPF view needs it).
MinVersion=10.0.19041
SetupIconFile=..\src\Stringline.App\Assets\stringline.ico
UninstallDisplayIcon={app}\Stringline.exe
UninstallDisplayName=Stringline
OutputDir={#OutputDir}
OutputBaseFilename=Stringline-{#AppVersion}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\Stringline.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\OFL.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Stringline"; Filename: "{app}\Stringline.exe"; Comment: "A live train graph for SimSig"
Name: "{autodesktop}\Stringline"; Filename: "{app}\Stringline.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Stringline.exe"; Description: "{cm:LaunchProgram,Stringline}"; Flags: nowait postinstall skipifsilent
