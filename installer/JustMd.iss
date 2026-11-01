; JustMd installer - Inno Setup 6 (Unicode).
;
; Compile from the repository root with build.ps1, or directly:
;   ISCC.exe installer\JustMd.iss /DAppVersion=1.1.0
;            [/DPublishDir=<folder>] [/DOutputDir=<folder>]
;
; Relative PublishDir/OutputDir values are resolved against this script's folder.

#define AppName       "JustMd"
#define AppExeName    "JustMd.exe"
#define AppPublisher  "Khalid Omar Hanafy"
#define AppURL        "https://github.com/khaled-Umar/JustMd"
; Legacy value kept on purpose: it is what Windows stores in users' hash-protected UserChoice and
; the .md default, and the installer cannot rewrite UserChoice. It is never shown in the UI. Do not rename.
#define AppProgId     "JustMdViewer.Markdown"
#define AppRegKey     "Software\JustMd"

#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif

; Turns a path that may be relative to this script into an absolute one.
#define ResolvePath(str P) \
  (Pos(":", P) > 0 || Copy(P, 1, 2) == "\\") ? P : AddBackslash(SourcePath) + P

#define PublishPath  AddBackslash(ResolvePath(PublishDir))
#define OutputPath   AddBackslash(ResolvePath(OutputDir))
#define RepoRoot     AddBackslash(SourcePath) + ".."
#define IconFile     RepoRoot + "\assets\icon\JustMd.ico"
#define LicenseSrc   RepoRoot + "\LICENSE"
#define NoticesSrc   RepoRoot + "\THIRD-PARTY-NOTICES.md"

#if !FileExists(PublishPath + AppExeName)
  #error JustMd.exe not found in PublishDir. Run build.ps1 (or dotnet publish src/JustMd -c Release -o artifacts/publish) first, or pass /DPublishDir=<folder>.
#endif

#ifndef AppVersion
  ; Fall back to the published executable's file version (build.ps1 always passes AppVersion).
  #define AppVersion GetVersionNumbersString(PublishPath + AppExeName)
#endif
#if AppVersion == ""
  #error AppVersion is empty. Pass /DAppVersion=<version>.
#endif

; Numeric x.y.z part of AppVersion (drops any "-prerelease" or "+metadata" suffix) for VersionInfoVersion.
#define AppNumericVersion AppVersion
#if Pos("+", AppNumericVersion) > 0
  #define AppNumericVersion Copy(AppNumericVersion, 1, Pos("+", AppNumericVersion) - 1)
#endif
#if Pos("-", AppNumericVersion) > 0
  #define AppNumericVersion Copy(AppNumericVersion, 1, Pos("-", AppNumericVersion) - 1)
#endif

#if FileExists(IconFile)
  #define HaveIcon
  #define AppIconRef "{app}\JustMd.ico"
#else
  #define AppIconRef "{app}\" + AppExeName + ",0"
#endif

[Setup]
; Never change AppId: it identifies the installation for upgrades and uninstall.
AppId={{D4A2B600-A41A-4CCE-A389-9A792780A23C}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}/issues
AppUpdatesURL={#AppURL}/releases
AppCopyright=Copyright (c) {#GetDateTimeString('yyyy', '', '')} {#AppPublisher}
VersionInfoVersion={#AppNumericVersion}
VersionInfoProductVersion={#AppNumericVersion}
VersionInfoProductTextVersion={#AppVersion}
VersionInfoDescription={#AppName} Setup
VersionInfoCompany={#AppPublisher}

; Per-user (no admin) by default; the user may choose an all-users install in a dialog,
; or on the command line with /ALLUSERS or /CURRENTUSER.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=commandline dialog

; .NET 10 x64 app: 64-bit Windows only (x64, and arm64 via x64 emulation), installed in 64-bit mode.
ArchitecturesAllowed=x64compatible or arm64
ArchitecturesInstallIn64BitMode=x64compatible or arm64

DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
UninstallDisplayName={#AppName}
UninstallDisplayIcon={#AppIconRef}
ChangesAssociations=yes
CloseApplications=yes
RestartApplications=no
#if FileExists(LicenseSrc)
LicenseFile={#LicenseSrc}
#endif
#ifdef HaveIcon
SetupIconFile={#IconFile}
#endif

OutputDir={#OutputPath}
OutputBaseFilename={#AppName}-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
#if Ver >= EncodeVer(6, 6, 0)
WizardStyle=modern dynamic
#else
WizardStyle=modern
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
AssocGroup=File associations:
AssocTask=Associate Markdown files (.md, .markdown) with {#AppName}
ContextMenuTask=Add 'Open with {#AppName}' to the right-click menu of Markdown files
ContextMenuVerb=Open with {#AppName}
MarkdownTypeName=Markdown Document
AppDescription=A lightweight viewer for Markdown files.
DefaultAppNote=Windows keeps the app you already use for Markdown files as the default, so it may ask once how you want to open them. To use {#AppName}, choose it and select "Always", or pick it in Settings > Apps > Default apps.
WebView2Missing={#AppName} needs the Microsoft Edge WebView2 Runtime to display documents, and it was not found on this computer.%n%nWindows 11 and most up-to-date Windows 10 PCs already include it. Do you want to open the Microsoft download page now?%n%nThe installation will finish either way.
DotNetMissing={#AppName} needs the Microsoft .NET 10 Desktop Runtime (x64), and it was not found on this computer. The app will not start until it is installed.%n%nDo you want to open the .NET 10 download page now? Choose ".NET Desktop Runtime" for x64 (also on Arm-based PCs).%n%nThe installation will finish either way.

[Tasks]
Name: "associate"; Description: "{cm:AssocTask}"; GroupDescription: "{cm:AssocGroup}"
Name: "contextmenu"; Description: "{cm:ContextMenuTask}"; GroupDescription: "{cm:AssocGroup}"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; Files from 1.0.0 (shipped as JustMdViewer), which Inno does not remove on upgrade.
Type: files; Name: "{app}\JustMdViewer.*"
Type: files; Name: "{autoprograms}\JustMdViewer.lnk"
Type: files; Name: "{autodesktop}\JustMdViewer.lnk"

[Files]
Source: "{#PublishPath}*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"
#ifdef HaveIcon
Source: "{#IconFile}"; DestDir: "{app}"; Flags: ignoreversion
#endif
#if FileExists(LicenseSrc)
Source: "{#LicenseSrc}"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
#endif
#if FileExists(NoticesSrc)
Source: "{#NoticesSrc}"; DestDir: "{app}"; Flags: ignoreversion
#endif

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; HKA = HKLM for all-users installs, HKCU for per-user installs.
;
; Always: register the executable so it is offered under "Open with" for Markdown files,
; without claiming any extension.
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#AppName}"
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{#AppIconRef}"
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".md"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".markdown"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".mdown"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".mkd"; ValueData: ""

; "associate" task: ProgID for Markdown documents.
Root: HKA; Subkey: "Software\Classes\{#AppProgId}"; ValueType: string; ValueName: ""; ValueData: "{cm:MarkdownTypeName}"; Flags: uninsdeletekey; Tasks: associate
Root: HKA; Subkey: "Software\Classes\{#AppProgId}"; ValueType: string; ValueName: "FriendlyTypeName"; ValueData: "{cm:MarkdownTypeName}"; Tasks: associate
Root: HKA; Subkey: "Software\Classes\{#AppProgId}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{#AppIconRef}"; Tasks: associate
Root: HKA; Subkey: "Software\Classes\{#AppProgId}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""; Tasks: associate

; "associate" task: offer the ProgID for each extension. Only our own value is added and removed;
; the shared extension keys and other apps' entries are left alone.
Root: HKA; Subkey: "Software\Classes\.md\OpenWithProgids"; ValueType: string; ValueName: "{#AppProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.markdown\OpenWithProgids"; ValueType: string; ValueName: "{#AppProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.mdown\OpenWithProgids"; ValueType: string; ValueName: "{#AppProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.mkd\OpenWithProgids"; ValueType: string; ValueName: "{#AppProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate

; "associate" task: Capabilities + RegisteredApplications so the app is listed in Settings > Default apps.
Root: HKA; Subkey: "{#AppRegKey}"; Flags: uninsdeletekeyifempty; Tasks: associate
Root: HKA; Subkey: "{#AppRegKey}\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "{#AppName}"; Flags: uninsdeletekey; Tasks: associate
Root: HKA; Subkey: "{#AppRegKey}\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "{cm:AppDescription}"; Tasks: associate
Root: HKA; Subkey: "{#AppRegKey}\Capabilities"; ValueType: string; ValueName: "ApplicationIcon"; ValueData: "{#AppIconRef}"; Tasks: associate
Root: HKA; Subkey: "{#AppRegKey}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".md"; ValueData: "{#AppProgId}"; Tasks: associate
Root: HKA; Subkey: "{#AppRegKey}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".markdown"; ValueData: "{#AppProgId}"; Tasks: associate
Root: HKA; Subkey: "{#AppRegKey}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mdown"; ValueData: "{#AppProgId}"; Tasks: associate
Root: HKA; Subkey: "{#AppRegKey}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mkd"; ValueData: "{#AppProgId}"; Tasks: associate
Root: HKA; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "{#AppName}"; ValueData: "{#AppRegKey}\Capabilities"; Flags: uninsdeletevalue; Tasks: associate

; "contextmenu" task: static "Open with JustMd" verb per extension under SystemFileAssociations,
; so it shows whichever app is the default. On Windows 11 a classic verb like this appears under
; "Show more options" (Shift+F10); the compact menu requires a packaged app.
; Only our JustMd subkey is removed on uninstall; parent keys only if they end up empty.
#dim CtxExts[4] {".md", ".markdown", ".mdown", ".mkd"}
#define CtxIndex
#sub EmitContextMenuEntries
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\{#CtxExts[CtxIndex]}"; Flags: uninsdeletekeyifempty; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\{#CtxExts[CtxIndex]}\shell"; Flags: uninsdeletekeyifempty; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\{#CtxExts[CtxIndex]}\shell\{#AppName}"; ValueType: string; ValueName: ""; ValueData: "{cm:ContextMenuVerb}"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\{#CtxExts[CtxIndex]}\shell\{#AppName}"; ValueType: string; ValueName: "MUIVerb"; ValueData: "{cm:ContextMenuVerb}"; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\{#CtxExts[CtxIndex]}\shell\{#AppName}"; ValueType: string; ValueName: "Icon"; ValueData: "{#AppIconRef}"; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\{#CtxExts[CtxIndex]}\shell\{#AppName}"; ValueType: string; ValueName: "MultiSelectModel"; ValueData: "Player"; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\{#CtxExts[CtxIndex]}\shell\{#AppName}\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""; Tasks: contextmenu
#endsub
#for {CtxIndex = 0; CtxIndex < DimOf(CtxExts); CtxIndex++} EmitContextMenuEntries

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent; Check: RuntimesPresent

[Code]
const
  WebView2ClientKey = 'Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  WebView2DownloadUrl = 'https://developer.microsoft.com/microsoft-edge/webview2/';
  DotNetSharedFx = 'Microsoft.WindowsDesktop.App';
  DotNetMajorPrefix = '10.';
  DotNetDownloadUrl = 'https://dotnet.microsoft.com/download/dotnet/10.0';
  ProgId = '{#AppProgId}';
  LegacyAppName = 'JustMdViewer';
  LegacyExeName = 'JustMdViewer.exe';
  LegacyRegKey = 'Software\JustMdViewer';

var
  { True when another app already owns at least one Markdown extension. }
  OtherDefaultExists: Boolean;
  { Runtime detection results, evaluated once by CheckRuntimes. }
  RuntimesChecked, DotNetPresent, WebView2Present: Boolean;

function WebView2VersionValid(const Version: String): Boolean;
begin
  Result := (Version <> '') and (Version <> '0.0.0.0');
end;

{ Evergreen WebView2 Runtime detection as documented by Microsoft: a non-empty 'pv' value
  under the EdgeUpdate client key, per-machine (32-bit and 64-bit views) or per-user. }
function IsWebView2Installed: Boolean;
var
  Version: String;
begin
  Result :=
    (RegQueryStringValue(HKLM32, 'SOFTWARE\' + WebView2ClientKey, 'pv', Version) and WebView2VersionValid(Version)) or
    (RegQueryStringValue(HKLM64, 'SOFTWARE\' + WebView2ClientKey, 'pv', Version) and WebView2VersionValid(Version)) or
    (RegQueryStringValue(HKCU, 'Software\' + WebView2ClientKey, 'pv', Version) and WebView2VersionValid(Version));
end;

{ Folder of the x64 .NET installation: on arm64 Windows the x64 runtime lives in dotnet\x64. }
function DotNetX64Root: String;
begin
  if IsArm64 then
    Result := ExpandConstant('{commonpf64}\dotnet\x64')
  else
    Result := ExpandConstant('{commonpf64}\dotnet');
end;

{ .NET 10 Desktop Runtime detection. The app ships an x64 launcher (single win-x64 publish), so
  the x64 runtime is required on every machine, including arm64 (where it runs emulated).
  Looks for a 10.x version registered by the .NET installer under InstalledVersions\x64
  (32-bit registry view), or a 10.x x64 shared framework folder. }
function IsDotNetDesktopRuntimeInstalled: Boolean;
var
  Names: TArrayOfString;
  I: Integer;
  FindRec: TFindRec;
begin
  Result := False;
  if RegGetValueNames(HKLM32, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\' + DotNetSharedFx, Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if Pos(DotNetMajorPrefix, Names[I]) = 1 then
      begin
        Log('Found .NET Desktop Runtime ' + Names[I] + ' (x64) in the registry.');
        Result := True;
        Exit;
      end;

  if FindFirst(DotNetX64Root + '\shared\' + DotNetSharedFx + '\' + DotNetMajorPrefix + '*', FindRec) then
  try
    repeat
      if FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0 then
      begin
        Log('Found .NET Desktop Runtime folder ' + FindRec.Name + '.');
        Result := True;
        Exit;
      end;
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

procedure CheckRuntimes;
begin
  if RuntimesChecked then
    Exit;
  DotNetPresent := IsDotNetDesktopRuntimeInstalled;
  WebView2Present := IsWebView2Installed;
  RuntimesChecked := True;
end;

{ [Run] Check: offer to launch the app only when it can actually start. }
function RuntimesPresent: Boolean;
begin
  CheckRuntimes;
  Result := DotNetPresent and WebView2Present;
end;

{ The current handler of an extension: the per-user UserChoice if any (read only, never written),
  otherwise the default value of the merged HKCR key. }
function CurrentHandler(const Ext: String): String;
begin
  if not RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\' + Ext + '\UserChoice', 'ProgId', Result) then
    Result := '';
  if Result = '' then
    if not RegQueryStringValue(HKCR, Ext, '', Result) then
      Result := '';
end;

{ Claims an extension as default only when no app handles it yet, so a fresh machine opens
  Markdown files directly. Existing defaults are left to Windows and the user. }
procedure ClaimExtensionIfUnowned(const Ext: String);
var
  Handler: String;
begin
  Handler := CurrentHandler(Ext);
  if Handler = '' then
  begin
    if RegWriteStringValue(HKA, 'Software\Classes\' + Ext, '', ProgId) then
      Log('Set default handler of ' + Ext + ' to ' + ProgId + ' (it had none).');
  end
  else if (CompareText(Handler, ProgId) <> 0) and (CompareText(Handler, 'Applications\{#AppExeName}') <> 0) then
  begin
    Log(Ext + ' is already handled by ' + Handler + '; leaving it unchanged.');
    OtherDefaultExists := True;
  end;
end;

procedure ReleaseExtensionIfOwned(const Ext: String);
var
  Value: String;
begin
  if RegQueryStringValue(HKA, 'Software\Classes\' + Ext, '', Value) and (CompareText(Value, ProgId) = 0) then
    RegDeleteValue(HKA, 'Software\Classes\' + Ext, '');
  RegDeleteKeyIfEmpty(HKA, 'Software\Classes\' + Ext + '\OpenWithProgids');
  RegDeleteKeyIfEmpty(HKA, 'Software\Classes\' + Ext);
end;

procedure ForgetExtension(const Ext: String);
begin
  RegDeleteValue(HKA, 'Software\Classes\' + Ext + '\OpenWithProgids', ProgId);
  ReleaseExtensionIfOwned(Ext);
end;

{ Reinstall/upgrade with the "associate" task unticked: remove everything that task writes
  (an earlier install may have written it), keeping the Applications "Open with" registration. }
procedure RemoveAssociation;
begin
  Log('File association task not selected; removing any earlier association.');
  RegDeleteKeyIncludingSubkeys(HKA, 'Software\Classes\' + ProgId);
  ForgetExtension('.md');
  ForgetExtension('.markdown');
  ForgetExtension('.mdown');
  ForgetExtension('.mkd');
  RegDeleteKeyIncludingSubkeys(HKA, '{#AppRegKey}\Capabilities');
  RegDeleteKeyIfEmpty(HKA, '{#AppRegKey}');
  RegDeleteValue(HKA, 'Software\RegisteredApplications', '{#AppName}');
end;

procedure RemoveContextMenuVerb(const Ext, Verb: String);
var
  ExtKey: String;
begin
  ExtKey := 'Software\Classes\SystemFileAssociations\' + Ext;
  RegDeleteKeyIncludingSubkeys(HKA, ExtKey + '\shell\' + Verb);
  RegDeleteKeyIfEmpty(HKA, ExtKey + '\shell');
  RegDeleteKeyIfEmpty(HKA, ExtKey);
end;

{ Reinstall/upgrade with the "contextmenu" task unticked: remove our verb an earlier install added. }
procedure RemoveContextMenu;
begin
  Log('Context menu task not selected; removing any earlier context menu entry.');
  RemoveContextMenuVerb('.md', '{#AppName}');
  RemoveContextMenuVerb('.markdown', '{#AppName}');
  RemoveContextMenuVerb('.mdown', '{#AppName}');
  RemoveContextMenuVerb('.mkd', '{#AppName}');
end;

{ Removes what 1.0.0 registered under its old name, except the ProgID, which is kept (see AppProgId).
  Safe to run on every install: anything already gone is simply skipped. }
procedure RemoveLegacyRegistration;
begin
  RegDeleteKeyIncludingSubkeys(HKA, 'Software\Classes\Applications\' + LegacyExeName);
  RegDeleteKeyIncludingSubkeys(HKA, LegacyRegKey);
  RegDeleteValue(HKA, 'Software\RegisteredApplications', LegacyAppName);
  RemoveContextMenuVerb('.md', LegacyAppName);
  RemoveContextMenuVerb('.markdown', LegacyAppName);
  RemoveContextMenuVerb('.mdown', LegacyAppName);
  RemoveContextMenuVerb('.mkd', LegacyAppName);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ErrorCode: Integer;
begin
  if CurStep <> ssPostInstall then
    Exit;

  RemoveLegacyRegistration;

  if not WizardIsTaskSelected('contextmenu') then
    RemoveContextMenu;

  if WizardIsTaskSelected('associate') then
  begin
    ClaimExtensionIfUnowned('.md');
    ClaimExtensionIfUnowned('.markdown');
    ClaimExtensionIfUnowned('.mdown');
    ClaimExtensionIfUnowned('.mkd');
  end
  else
    RemoveAssociation;

  CheckRuntimes;

  if not DotNetPresent then
  begin
    Log('.NET 10 Desktop Runtime (x64) was not found.');
    if SuppressibleMsgBox(CustomMessage('DotNetMissing'), mbInformation, MB_YESNO, IDNO) = IDYES then
      ShellExecAsOriginalUser('open', DotNetDownloadUrl, '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
  end;

  if not WebView2Present then
  begin
    Log('Microsoft Edge WebView2 Runtime was not found.');
    if SuppressibleMsgBox(CustomMessage('WebView2Missing'), mbInformation, MB_YESNO, IDNO) = IDYES then
      ShellExecAsOriginalUser('open', WebView2DownloadUrl, '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
  end;
end;

{ Adds a short note to the finish page when Windows will keep another default app. }
procedure CurPageChanged(CurPageID: Integer);
var
  Delta: Integer;
begin
  if (CurPageID <> wpFinished) or not OtherDefaultExists then
    Exit;

  WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10#13#10 + CustomMessage('DefaultAppNote');
  Delta := WizardForm.AdjustLabelHeight(WizardForm.FinishedLabel);
  WizardForm.RunList.Top := WizardForm.RunList.Top + Delta;
  WizardForm.YesRadio.Top := WizardForm.YesRadio.Top + Delta;
  WizardForm.NoRadio.Top := WizardForm.NoRadio.Top + Delta;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;

  ReleaseExtensionIfOwned('.md');
  ReleaseExtensionIfOwned('.markdown');
  ReleaseExtensionIfOwned('.mdown');
  ReleaseExtensionIfOwned('.mkd');
end;
