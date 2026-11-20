; Inno Setup script for the experimental, UNSIGNED Label Printer Agent.
; Per-user install: no administrator rights, nothing outside the user's profile, no service, no autostart.
; Build: ISCC /DAppVersion=<version> /DPublishDir=<publish dir> /DOutputDir=<dir> installer\LabelPrinterAgent.iss

#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef PublishDir
  #error PublishDir is required
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif

[Setup]
AppId={{6C0B3A6E-5B7D-4B43-9D1F-0E3A7C2D9A51}
AppName=Label Printer Agent (experimental)
AppVersion={#AppVersion}
AppVerName=Label Printer Agent {#AppVersion} (experimental, unsigned)
AppPublisher=Unsigned experimental build
DefaultDirName={localappdata}\Programs\LabelPrinterAgent
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=LabelPrinterAgent-{#AppVersion}-win-x64-unsigned-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=Label Printer Agent {#AppVersion} (experimental)
InfoBeforeFile=..\docs\installer-notice.txt
CloseApplications=no
SetupLogging=yes
SetupIconFile=..\src\LabelPrinter.Agent\app.ico
WizardSmallImageFile=wizard-small.bmp
VersionInfoCompany=Flow Poshak
VersionInfoProductName=Label Printer Agent
VersionInfoDescription=Label Printer Agent setup (experimental, unsigned)
VersionInfoCopyright=Copyright (c) Flow Poshak
UninstallDisplayIcon={app}\LabelPrinterAgentBg.exe

[Tasks]
Name: "autostart"; Description: "Start Label Printer Agent automatically when Windows starts (no window; recommended)"
Name: "desktopicon"; Description: "Create a desktop icon"

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs
Source: "..\docs\setup-guide.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\docs\recovery.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\docs\installer-notice.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Label Printer Agent (experimental)\Label Printer Agent"; Filename: "{app}\LabelPrinterAgentBg.exe"; Comment: "Open the Label Printer Agent control window"
Name: "{autodesktop}\Label Printer Agent"; Filename: "{app}\LabelPrinterAgentBg.exe"; Comment: "Open the Label Printer Agent control window"; Tasks: desktopicon
Name: "{autoprograms}\Label Printer Agent (experimental)\Setup guide"; Filename: "{app}\setup-guide.md"
Name: "{autoprograms}\Label Printer Agent (experimental)\Command prompt here"; Filename: "{cmd}"; Parameters: "/K ""cd /d ""{app}"" && LabelPrinterAgent.exe status"""; WorkingDir: "{app}"

[Registry]
; Autostart is an explicit installer choice (checked by default). It is written only on a FRESH install, so an
; upgrade never re-enables something the user switched off in the control window.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "LabelPrinterAgent"; ValueData: """{app}\LabelPrinterAgentBg.exe"" --background"; Flags: uninsdeletevalue; Tasks: autostart; Check: FreshInstall

[Run]
; Opens the control window after an interactive install. Silent installs never start anything.
Filename: "{app}\LabelPrinterAgentBg.exe"; Description: "Open Label Printer Agent now"; Flags: postinstall nowait skipifsilent

; Installing never pairs, selects a printer or prints. The program stays idle until the user does both.

[UninstallDelete]
; Program files only. The data directory (%LOCALAPPDATA%\LabelPrinterAgent) is handled in code below.

[Code]
const
  DataSubDir = 'LabelPrinterAgent';
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  RunValue = 'LabelPrinterAgent';

var
  WasInstalled: Boolean;

function FreshInstall(): Boolean;
begin
  Result := not WasInstalled;
end;

function InitializeSetup(): Boolean;
begin
  WasInstalled := FileExists(ExpandConstant('{localappdata}\Programs\LabelPrinterAgent\LabelPrinterAgent.exe'));
  Result := True;
end;

function AgentExe(): String;
begin
  Result := ExpandConstant('{app}\LabelPrinterAgent.exe');
end;

{ Asks a running agent to finish its current call and stop, so files can be replaced and no submission is cut. }
function StopAgent(): Boolean;
var
  Code: Integer;
begin
  Result := True;
  if FileExists(AgentExe()) then
  begin
    if not Exec(AgentExe(), 'stop --wait 45', '', SW_HIDE, ewWaitUntilTerminated, Code) then Code := 0;
    Result := (Code = 0);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not StopAgent() then
    Result := 'The Label Printer Agent is still running and did not stop. Run "LabelPrinterAgent.exe stop" and try again.';
end;

function InitializeUninstall(): Boolean;
begin
  Result := StopAgent();
  if not Result then
    MsgBox('The Label Printer Agent is still running. Stop it first (LabelPrinterAgent.exe stop).', mbError, MB_OK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usUninstall then
    { The autostart entry points at a file that is about to disappear. }
    RegDeleteValue(HKEY_CURRENT_USER, RunKey, RunValue);

  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\') + DataSubDir;
    if DirExists(DataDir) then
    begin
      { Silent uninstall never deletes data. Interactive uninstall asks, with "No" as the default. }
      if (not UninstallSilent()) and
         (MsgBox('Also delete the agent''s data?' + #13#10 + #13#10 +
                 DataDir + #13#10 + #13#10 +
                 'It holds the job journal (the record of what was sent to the printer), the pairing credential and settings.' + #13#10 +
                 'Without the journal the agent cannot tell which jobs were already submitted.' + #13#10 + #13#10 +
                 'Choose No to keep it (recommended).', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
        DelTree(DataDir, True, True, True);
    end;
  end;
end;
