; Remotely Server installer (Inno Setup 6), x64, for the always-on PC or VM that runs your own coordinator (for example Windows Server 2025).
;   ISCC.exe /DVersion=0.4.0 installer\Remotely-Server.iss
; Installs two things: Remotely.Server.exe as the "Remotely.Server" service (runs as LocalService, opens its TCP port in Windows Firewall), and the Remotely Server
; Manager (the WinUI app that administers it) in the Manager sub-folder, with a Start menu entry. The Manager finds the server in the folder above its own, which is
; where the service runs from. At the end the installer offers to open the Manager (or, if you prefer a console, "Remotely Server info" shows an invite).
; The only question is how devices will reach this server (a name or address, optional). Silent:
;   Setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART [/Address=myhome.example.com]
; An install over an installed Remotely Server (same AppId) is an upgrade in place that keeps the server key and the device list (%ProgramData%\Remotely Server); if it
; fails it is rolled back (UpgradeSafety.isi). /SimulateFailure=1 makes the install fail at its last step on purpose (the CI uses it). Do not use it otherwise.
; The server is built for x64 only. Windows 11 on ARM runs it through its x64 emulation (ArchitecturesAllowed=x64compatible); the Manager in this installer is the x64 build too.

#ifndef Version
  #define Version "0.4.0"
#endif
#define Publish "..\publish\server"
#define PublishManager "..\publish\manager"

[Setup]
AppId={{4AD4465B-1297-47FA-96EB-767F6966DD9D}
AppName=Remotely Server
AppVersion={#Version}
AppVerName=Remotely Server {#Version}
AppPublisher=Remotely (open source)
DefaultDirName={autopf}\Remotely Server
UsePreviousAppDir=yes
DisableDirPage=yes
DefaultGroupName=Remotely Server
DisableProgramGroupPage=yes
PrivilegesRequired=admin
OutputDir=..\publish
OutputBaseFilename=Remotely-Server-Setup-{#Version}-x64
Compression=lzma2/ultra
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=Remotely Server
UninstallDisplayIcon={app}\Remotely.Server.exe
SetupIconFile=..\src\Remotely.App\Assets\remotely.ico
SetupLogging=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#Publish}\Remotely.Server.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Publish}\appsettings.json"; DestDir: "{app}"; Flags: ignoreversion onlyifdoesntexist
Source: "{#PublishManager}\*"; DestDir: "{app}\Manager"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Remotely Server Manager"; Filename: "{app}\Manager\Remotely.ServerManager.exe"; WorkingDir: "{app}\Manager"
; Needs administrator rights because it reads the server key.
Name: "{group}\Remotely Server info: invite a new device"; Filename: "{app}\Remotely.Server.exe"; Parameters: "info --pause"; WorkingDir: "{app}"
Name: "{group}\Uninstall Remotely Server"; Filename: "{uninstallexe}"

[Run]
; Installing and starting the service (and the optional address) is done in [Code] (CurStepChanged) so that a failure can be detected and rolled back.
Filename: "{app}\Manager\Remotely.ServerManager.exe"; Description: "Open Remotely Server Manager"; Flags: nowait postinstall skipifsilent runasoriginaluser
Filename: "{app}\Remotely.Server.exe"; Parameters: "info --pause"; Description: "Show the invite for your first device in a console window"; Flags: postinstall skipifsilent unchecked

[UninstallRun]
; Your device list and key (%ProgramData%\Remotely Server) are kept.
Filename: "{app}\Remotely.Server.exe"; Parameters: "uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveServerService"

[Code]
#include "UpgradeSafety.isi"

var
  AddressPage: TInputQueryWizardPage;

procedure InitializeWizard;
begin
  AddressPage := CreateInputQueryPage(wpWelcome, 'How will your devices reach this server?',
    'This goes into the invite your devices use to join.',
    'At home, leave this empty: the invite uses this PC''s home-network address. To reach the server from outside your home, enter your public name or ' +
    'address (for example myhome.example.com) and forward TCP port 8443 on your router to this PC; docs\server-network-setup.md in the Remotely repository ' +
    'lists the exact steps and how to test them with your phone. You can change the name later with: Remotely.Server.exe set-address <name>');
  AddressPage.Add('Name or address (optional):', False);
  AddressPage.Values[0] := ExpandConstant('{param:Address|}');
end;

// Same rule as the Server Manager's setup wizard: a host name or IP address, no spaces, no https:// in front.
function IsPlausibleAddress(const S: String): Boolean;
var
  I: Integer;
  C: Char;
begin
  Result := True;
  if S = '' then Exit;
  if (Length(S) > 253) or (Pos('://', S) > 0) then
  begin
    Result := False;
    Exit;
  end;
  for I := 1 to Length(S) do
  begin
    C := S[I];
    if not (((C >= 'a') and (C <= 'z')) or ((C >= 'A') and (C <= 'Z')) or ((C >= '0') and (C <= '9')) or
            (C = '.') or (C = '-') or (C = ':') or (C = '[') or (C = ']')) then
    begin
      Result := False;
      Exit;
    end;
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = AddressPage.ID then
    if not IsPlausibleAddress(Trim(AddressPage.Values[0])) then
    begin
      MsgBox('Type only a name such as myhome.example.com, or an IP address, with no spaces and without https:// in front. You can leave it empty.', mbError, MB_OK);
      Result := False;
    end;
end;

function AddressValue(Param: String): String;
begin
  if WizardSilent then Result := ExpandConstant('{param:Address|}') else Result := Trim(AddressPage.Values[0]);
end;

function HasAddress: Boolean;
begin
  Result := AddressValue('') <> '';
end;

procedure StopManager;
begin
  RunQuiet(ExpandConstant('{sys}\taskkill.exe'), '/IM Remotely.ServerManager.exe /F /T');
end;

function InitializeSetup(): Boolean;
begin
  StopManager;
  Result := True;
end;

// Let go of the program files (the service, the Manager) and keep a copy of the installed folder, so a failed upgrade can put it back.
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopServiceAndWait('Remotely.Server', 30);
  RunQuiet(ExpandConstant('{sys}\taskkill.exe'), '/IM Remotely.Server.exe /F /T');
  StopManager;
  Sleep(500);
  BackupForUpgrade(ExpandConstant('{app}'), 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{4AD4465B-1297-47FA-96EB-767F6966DD9D}_is1');
  Result := '';
end;

// A fresh install that fails leaves no half-registered service or firewall rule behind (an upgrade is restored from the backup instead).
procedure FailServerStep(const Reason: String);
begin
  if not UpgradeHaveBackup then RunQuiet(ExpandConstant('{app}\Remotely.Server.exe'), 'uninstall');
  FailInstall(Reason);
end;

// The same commands you can run by hand: Remotely.Server.exe install / set-address. "install" creates or updates the service, opens the firewall port and starts it.
procedure ConfigureAndStartServer;
var
  Server: String;
  Code: Integer;
begin
  Server := ExpandConstant('{app}\Remotely.Server.exe');
  if not WizardSilent then WizardForm.StatusLabel.Caption := 'Starting your Remotely server...';
  Code := RunQuiet(Server, 'install');
  if Code <> 0 then FailServerStep('The Remotely.Server service could not be installed or started (exit code ' + IntToStr(Code) + ').');
  if not WaitForServiceRunning('Remotely.Server', 30) then
    FailServerStep('The Remotely.Server service did not reach the running state within 30 seconds.');
  if HasAddress then
  begin
    Code := RunQuiet(Server, 'set-address "' + AddressValue('') + '"');
    if Code <> 0 then Log('Remotely.Server.exe set-address failed with exit code ' + IntToStr(Code));
  end;
  if ExpandConstant('{param:SimulateFailure|}') = '1' then FailServerStep('Simulated failure (requested with /SimulateFailure=1).');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then ConfigureAndStartServer;
end;

// After a failed upgrade (Setup has already undone what it could): put the previous folder back and start the previous service again.
procedure DeinitializeSetup();
begin
  if UpgradeFailed and UpgradeHaveBackup then
  begin
    StopServiceAndWait('Remotely.Server', 30);
    RunQuiet(ExpandConstant('{sys}\taskkill.exe'), '/IM Remotely.Server.exe /F /T');
    StopManager;
    Sleep(500);
    RestoreFromUpgradeBackup;
    RunQuiet(ExpandConstant('{sys}\sc.exe'), 'start Remotely.Server');
  end;
end;

function InitializeUninstall(): Boolean;
begin
  StopManager;
  Result := True;
end;

// The Manager's own "start with Windows" value is removed only if it points at this installation.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Value: String;
begin
  if CurUninstallStep = usUninstall then
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Remotely Server Manager', Value) then
      if Pos(Lowercase(ExpandConstant('{app}\Manager\Remotely.ServerManager.exe')), Lowercase(Value)) > 0 then
        RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Remotely Server Manager');
end;
