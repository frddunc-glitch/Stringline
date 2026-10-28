; Stringline installer (S18), built by build/publish.ps1 -Installer with Inno Setup 6:
;   ISCC /DAppVersion=0.1.0 /DAppNumericVersion=0.1.0 /DSourceDir=<publish folder> /DOutputDir=<folder> installer/Stringline.iss
; With a certificate it also passes /DSign and /Sstringline=... so the installer and uninstaller are signed.
; It installs the single-file Stringline.exe with its licence and notices, for the current user by default (no admin
; prompt) or for all users.
;
; Uninstalling (S30, #14) always removes a saved SimSig password (Stringline.exe --forget-password), then asks whether to
; remove the settings, profiles, logs, help pages and unpacked files too (default No). A silent uninstall keeps them
; unless given /REMOVEUSERDATA:
;   unins000.exe /VERYSILENT /SUPPRESSMSGBOXES /REMOVEUSERDATA
; Sessions, recordings and exports in Documents\Stringline are the user's own files and are never removed.
;
; Updating: running a newer setup over an installed Stringline upgrades it in place. Setup finds the install by AppId,
; keeps its folder and per-user or all-users mode, closes a running Stringline through the Restart Manager (the app
; saves its session when Windows asks it to close; one that then does not exit, as 0.4.0 can, is ended) and replaces
; the files. A silent update:
;   Stringline-x.y.z-setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS
; Installing an older version over a newer one asks first; a silent one refuses (exit code 1) unless given
; /ALLOWDOWNGRADE. build/test-upgrade.ps1 checks all of this. A file a later version no longer ships must be listed
; under [InstallDelete], or the upgrade leaves it behind.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef AppNumericVersion
  #define AppNumericVersion AppVersion
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish\Stringline"
#endif
; Never change the AppId: it is how upgrades and the uninstaller find an existing install.
#define AppId "6E0B7C2A-5F1D-4C8E-9A3B-2D7F41C9E8B5"
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif

[Setup]
AppId={{{#AppId}}
AppName=Stringline
AppVersion={#AppVersion}
AppVerName=Stringline {#AppVersion}
AppPublisher=Frederick Duncan and Stringline contributors
AppPublisherURL=https://github.com/frddunc-glitch/Stringline
AppSupportURL=https://github.com/frddunc-glitch/Stringline/issues
AppUpdatesURL=https://github.com/frddunc-glitch/Stringline/releases
AppCopyright=Copyright (C) 2026 Frederick Duncan and Stringline contributors
VersionInfoVersion={#AppNumericVersion}
VersionInfoProductTextVersion={#AppVersion}
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
; force: an app that has been asked to close (and saved its session) but has not exited is ended, rather than the
; update failing. 0.4.0 can stay running after saving its session.
CloseApplications=force
; Close only Stringline itself, not other programs that happen to have a file of ours open.
CloseApplicationsFilter=Stringline.exe
#ifdef Sign
; publish.ps1 defines Sign and the "stringline" sign tool (build/sign.ps1) when a code-signing certificate is set up.
SignTool=stringline
SignedUninstaller=yes
#endif

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

[InstallDelete]
; Files earlier versions installed that this one no longer does (none yet).

[Icons]
Name: "{autoprograms}\Stringline"; Filename: "{app}\Stringline.exe"; Comment: "A live train graph for SimSig"
Name: "{autodesktop}\Stringline"; Filename: "{app}\Stringline.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Stringline.exe"; Description: "{cm:LaunchProgram,Stringline}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Before the files go: the app removes a saved password from its settings, leaving the rest (PRIVACY.md).
Filename: "{app}\Stringline.exe"; Parameters: "--forget-password"; Flags: runhidden waituntilterminated; RunOnceId: "ForgetPassword"

[Code]
const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{{#AppId}}_is1';

// The next dot-separated number of a version, taken off its front; -1 if there is none.
function NextPart(var Version: String): Integer;
var
  Dot: Integer;
begin
  Dot := Pos('.', Version);
  if Dot = 0 then Dot := Length(Version) + 1;
  Result := StrToIntDef(Copy(Version, 1, Dot - 1), -1);
  Delete(Version, 1, Dot);
end;

// The numeric part of a version ("0.2.0-beta+abc" -> 0.2.0), packed for comparing; False if it is not one.
function PackVersion(Version: String; var Packed: Int64): Boolean;
var
  Cut, Major, Minor, Patch: Integer;
begin
  Cut := Pos('+', Version);
  if Cut > 0 then Version := Copy(Version, 1, Cut - 1);
  Cut := Pos('-', Version);
  if Cut > 0 then Version := Copy(Version, 1, Cut - 1);
  Major := NextPart(Version);
  Minor := NextPart(Version);
  Patch := NextPart(Version);
  Result := (Major >= 0) and (Minor >= 0) and (Patch >= 0) and (Version = '');
  if Result then
    Packed := PackVersionComponents(Major, Minor, Patch, 0);
end;

// The newest version installed for this user or for all users, or False if there is none.
function InstalledVersion(var Version: String; var Packed: Int64): Boolean;
var
  Found: String;
  FoundPacked: Int64;
begin
  Result := False;
  if RegQueryStringValue(HKCU, UninstallKey, 'DisplayVersion', Found) and PackVersion(Found, FoundPacked) then
  begin
    Version := Found;
    Packed := FoundPacked;
    Result := True;
  end;
  if RegQueryStringValue(HKLM64, UninstallKey, 'DisplayVersion', Found) and PackVersion(Found, FoundPacked) and
    (not Result or (ComparePackedVersion(FoundPacked, Packed) > 0)) then
  begin
    Version := Found;
    Packed := FoundPacked;
    Result := True;
  end;
end;

function HasSwitch(Name: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), Name) = 0 then
      Result := True;
end;

// Installing an older version over a newer one is usually a mistake, so it asks first.
function InitializeSetup(): Boolean;
var
  Installed: String;
  Old, New: Int64;
begin
  Result := True;
  if not InstalledVersion(Installed, Old) or not PackVersion('{#AppVersion}', New) then
    Exit;
  if ComparePackedVersion(New, Old) >= 0 then
    Exit;
  Log(Format('Stringline %s is installed; this setup is the older %s.', [Installed, '{#AppVersion}']));
  if HasSwitch('/ALLOWDOWNGRADE') then
    Exit;
  Result := SuppressibleMsgBox(Format('Stringline %s is installed, which is newer than this setup''s %s.'#13#10#13#10 +
    'Install the older version over it? Your settings, profiles and sessions are kept, but a newer version may have ' +
    'saved them in a form the older one cannot read.', [Installed, '{#AppVersion}']),
    mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES;
  if not Result then
    Log('Not installing the older version.');
end;

// The user's own data that the uninstaller may remove (PRIVACY.md); never Documents\Stringline.
procedure RemoveUserData();
begin
  Log('Removing Stringline''s settings, profiles, logs, help pages and unpacked files.');
  DelTree(ExpandConstant('{userappdata}\Stringline'), True, True, True);
  DelTree(ExpandConstant('{localappdata}\Stringline'), True, True, True);
  DelTree(ExpandConstant('{%TEMP}\.net\Stringline'), True, True, True);
end;

// After the program has gone: remove the user's data if asked (/REMOVEUSERDATA, or Yes to the question).
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Remove: Boolean;
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;
  if HasSwitch('/REMOVEUSERDATA') then
    Remove := True
  else if UninstallSilent then
    Remove := False
  else
    Remove := MsgBox('Also remove your Stringline settings, sim profiles and logs?'#13#10#13#10 +
      'Your saved SimSig password has been removed already. Choose No to keep the rest for when you install ' +
      'Stringline again. Your sessions, recordings and exports in Documents\Stringline are kept either way.',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
  if Remove then
    RemoveUserData()
  else
    Log('Keeping Stringline''s settings, profiles and logs.');
end;
