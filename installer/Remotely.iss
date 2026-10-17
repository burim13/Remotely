; Remotely installer (Inno Setup 6). One per architecture, built by build\publish.ps1 or the CI "installers" job:
;   ISCC.exe /DArch=arm64 /DVersion=0.4.0 installer\Remotely.iss
;   ISCC.exe /DArch=x64   /DVersion=0.4.0 installer\Remotely.iss
; At the start Windows asks (once): for all users (administrator: the Host can run as a Windows service that survives sign-out and shows the
; lock screen) or only for me (no administrator: the Host runs while you are signed in). Same choice as Portico's per-user installer, plus the service.
; Same-network sharing needs nothing from the wizard: the "Additional tasks" page has two boxes, both off, and "Next" is all that is needed. The tasks are
;   startup  Start Remotely when I sign in to Windows (it waits in the tray; same as the app's own "Start with Windows" setting)
;   connect  Connect this PC to my own server: shows one page for the invite that "Remotely Server info" prints on your server
; Silent install for scripts and tests:  Setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART [/TASKS=startup] [/Invite=RMT1-... /Name="Dad's PC"]
;   The long form still works: /Server=wss://host:8443/ws /Pin=<hex> /Code=XXXX-XXXX /Trust=<viewer id>
;   add /CURRENTUSER for the no-administrator mode (or /ALLUSERS), /Insecure=1 only for local testing over ws://, /COMPONENTS=viewer to leave out the Host.
;   An install over an installed Remotely (same AppId) is an upgrade in place: see UpgradeSafety.isi for how a failed upgrade is rolled back.
;   /SimulateFailure=1 makes the install fail at its last step on purpose (the CI uses it to prove the rollback). Do not use it otherwise.

#ifndef Arch
  #define Arch "x64"
#endif
#ifndef Version
  #define Version "0.4.0"
#endif
#define Publish "..\publish\" + Arch

[Setup]
AppId={{A979AC82-5CEB-4AFD-9C80-A0AC7E51F7E0}
AppName=Remotely
AppVersion={#Version}
AppVerName=Remotely {#Version}
AppPublisher=Remotely (open source)
DefaultDirName={autopf}\Remotely
UsePreviousAppDir=yes
DisableDirPage=yes
DefaultGroupName=Remotely
DisableProgramGroupPage=yes
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\publish
OutputBaseFilename=Remotely-Setup-{#Version}-{#Arch}
Compression=lzma2/ultra
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=Remotely
UninstallDisplayIcon={app}\Remotely.exe
SetupIconFile=..\src\Remotely.App\Assets\remotely.ico
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Types]
Name: "full"; Description: "Both: control other computers, and let this PC be controlled"
Name: "custom"; Description: "Choose what to install"; Flags: iscustom

[Components]
Name: "viewer"; Description: "Remotely: control other computers, and share this PC on your network with a token"; Types: full custom
Name: "host"; Description: "Remotely Host: let your other devices control this PC"; Types: full custom

[Tasks]
Name: "startup"; Description: "Start Remotely when I sign in to Windows (it waits in the notification area)"; Flags: unchecked; Components: viewer
Name: "connect"; Description: "Connect this PC to my own Remotely server (I have an invite)"; Flags: unchecked

[Files]
Source: "{#Publish}\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: viewer
Source: "{#Publish}\host\Remotely.Host.exe"; DestDir: "{app}"; Flags: ignoreversion; Components: host

[Icons]
Name: "{group}\Remotely"; Filename: "{app}\Remotely.exe"; Components: viewer
Name: "{group}\Uninstall Remotely"; Filename: "{uninstallexe}"

[Run]
; Same-network sharing listens on TCP 47810 (token connections) and UDP 47811 (finding the PC by its token). Only on private and domain networks, never public Wi-Fi.
; Only an all-users install can add firewall rules; a per-user install is asked by Windows the first time instead.
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""Remotely same-network (TCP)"" dir=in action=allow program=""{app}\Remotely.exe"" protocol=TCP localport=47810 profile=private,domain"; Flags: runhidden waituntilterminated; Components: viewer; Check: IsAdminInstallMode
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""Remotely same-network (UDP)"" dir=in action=allow program=""{app}\Remotely.exe"" protocol=UDP localport=47811 profile=private,domain"; Flags: runhidden waituntilterminated; Components: viewer; Check: IsAdminInstallMode
; Start with Windows: the same value the app's own setting writes (HKCU Run, "Remotely", command with --minimized), written as the person who started Setup even when
; Setup runs elevated. Configuring and starting the Host is done in the Pascal script below (CurStepChanged) so that a failure can be detected and rolled back.
Filename: "{sys}\reg.exe"; Parameters: "add ""HKCU\Software\Microsoft\Windows\CurrentVersion\Run"" /v Remotely /t REG_SZ /d ""\""{app}\Remotely.exe\"" --minimized"" /f"; Flags: runhidden waituntilterminated runasoriginaluser; Tasks: startup; Components: viewer
Filename: "{app}\Remotely.exe"; Parameters: "{code:ViewerArgs}"; Description: "Open Remotely"; Flags: nowait postinstall skipifsilent runasoriginaluser; Components: viewer

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Remotely same-network (TCP)"""; Flags: runhidden waituntilterminated; RunOnceId: "RemoveFirewallTcp"; Check: IsAdminInstallMode
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Remotely same-network (UDP)"""; Flags: runhidden waituntilterminated; RunOnceId: "RemoveFirewallUdp"; Check: IsAdminInstallMode
; Removes the service (or the sign-in entry). Your settings, device key and logs are never touched.
Filename: "{app}\Remotely.Host.exe"; Parameters: "uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveHostService"; Check: IsAdminInstallMode
Filename: "{app}\Remotely.Host.exe"; Parameters: "uninstall --user"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveHostUser"; Check: not IsAdminInstallMode

; RULE (as in Portico): the installer and uninstaller never touch user data. The host's key, settings and logs live in
; %ProgramData%\Remotely (service) or %LocalAppData%\Remotely (user), the viewer's in %LocalAppData%\Remotely, all outside {app}.
; A newer installer over an installed Remotely (same AppId) is an in-place upgrade of the program files only; if the upgrade fails it is rolled back (UpgradeSafety.isi).

[Code]
#include "UpgradeSafety.isi"

var
  HostPage: TInputQueryWizardPage;

function Param(Name: String): String;
begin
  Result := ExpandConstant('{param:' + Name + '|}');
end;

// Index 0 = invite, 1 = this PC's name, 2 = server, 3 = pin, 4 = code, 5 = viewer ids. Only 0 and 1 are shown (and only when the "connect" task is ticked);
// the rest come from the command line (scripts, tests).
function HostPageValue(Index: Integer; ParamName: String): String;
begin
  if (Index <= 1) and (not WizardSilent) then
  begin
    if WizardIsTaskSelected('connect') then Result := Trim(HostPage.Values[Index]) else Result := '';
  end
  else Result := Param(ParamName);
  if (Index = 1) and (Result = '') then Result := GetComputerNameString;
end;

function InviteValue: String;
begin
  Result := HostPageValue(0, 'Invite');
end;

function IsInsecure: Boolean;
begin
  Result := Param('Insecure') = '1';
end;

function UserFlag: String;
begin
  if IsAdminInstallMode then Result := '' else Result := '--user ';
end;

procedure InitializeWizard;
begin
  HostPage := CreateInputQueryPage(wpSelectTasks, 'Connect this PC to your server',
    'Paste the invite from your own Remotely server.',
    'On the server, open "Remotely Server info" and copy the invite. It holds the address, the key and a one-time code, so there is nothing else to type.');
  HostPage.Add('Invite:', False);
  HostPage.Add('This PC''s name:', False);
  HostPage.Values[0] := Param('Invite');
  HostPage.Values[1] := Param('Name');
  if HostPage.Values[1] = '' then HostPage.Values[1] := GetComputerNameString;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  // Same-network use needs no questions: the component choice is left to /COMPONENTS, the invite page only appears when "connect" was ticked.
  if PageID = wpSelectComponents then Result := True;
  if PageID = HostPage.ID then Result := not WizardIsTaskSelected('connect');
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Text: String;
begin
  Result := True;
  if CurPageID = HostPage.ID then
  begin
    Text := Trim(HostPage.Values[0]);
    if Text = '' then Exit;
    if (CompareText(Copy(Text, 1, 5), 'RMT1-') <> 0) or (Length(Text) < 60) then
    begin
      MsgBox('That does not look like a Remotely invite. It starts with RMT1- and is one long line. Copy all of it from "Remotely Server info" on your server.', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

// Set the host up only if an invite was given (wizard or /Invite=) or the long-form /Server= was passed on the command line (silent).
function NeedsSetup: Boolean;
begin
  Result := (InviteValue <> '') or (Param('Server') <> '');
end;

function HostConfigExists: Boolean;
begin
  if IsAdminInstallMode then Result := FileExists(ExpandConstant('{commonappdata}\Remotely\host.json'))
  else Result := FileExists(ExpandConstant('{localappdata}\Remotely\host.json'));
end;

function HostSetupArgs(Param: String): String;
var
  Ids, One: String;
  P: Integer;
begin
  if InviteValue <> '' then
  begin
    Result := 'setup ' + UserFlag + '--invite "' + InviteValue + '" --name "' + HostPageValue(1, 'Name') + '"';
    Ids := Trim(ExpandConstant('{param:Trust|}'));
  end
  else
  begin
    Result := 'setup ' + UserFlag + '--server "' + ExpandConstant('{param:Server|}') + '" --code "' + ExpandConstant('{param:Code|}') + '" --name "' + HostPageValue(1, 'Name') + '"';
    if ExpandConstant('{param:Pin|}') <> '' then Result := Result + ' --pin "' + ExpandConstant('{param:Pin|}') + '"';
    if IsInsecure then Result := Result + ' --insecure';
    Ids := Trim(ExpandConstant('{param:Trust|}'));
  end;
  while Ids <> '' do
  begin
    P := Pos(' ', Ids);
    if P = 0 then begin One := Ids; Ids := ''; end
    else begin One := Copy(Ids, 1, P - 1); Ids := Trim(Copy(Ids, P + 1, MaxInt)); end;
    if One <> '' then Result := Result + ' --trust "' + One + '"';
  end;
end;

// The viewer joins the same server with the same invite (it works for several devices).
function ViewerArgs(Param: String): String;
begin
  if InviteValue <> '' then Result := '--invite "' + InviteValue + '"' else Result := '';
end;

function HostInstallArgs(Param: String): String;
begin
  Result := 'install ' + UserFlag;
end;

// Stop a running viewer or user-mode host before the program files are replaced. Data is not touched.
procedure StopRemotely();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM Remotely.exe /F /T', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function InitializeSetup(): Boolean;
begin
  StopRemotely();
  Result := True;
end;

// Let go of the program files before they are replaced: stop the service (it would otherwise be restarted by its recovery settings) and any leftover helper.
// Then keep a copy of the installed program folder, so a failed upgrade can put it back.
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  if IsAdminInstallMode then StopServiceAndWait('Remotely.Host', 30);
  RunQuiet(ExpandConstant('{sys}\taskkill.exe'), '/IM Remotely.Host.exe /F /T');
  Sleep(500);
  BackupForUpgrade(ExpandConstant('{app}'), 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{A979AC82-5CEB-4AFD-9C80-A0AC7E51F7E0}_is1');
  Result := '';
end;

// A fresh install that fails leaves no half-registered service behind (an upgrade is restored from the backup instead).
procedure FailHostStep(const Reason: String);
begin
  if not UpgradeHaveBackup then RunQuiet(ExpandConstant('{app}\Remotely.Host.exe'), 'uninstall ' + UserFlag);
  FailInstall(Reason);
end;

// Connects the Host to the server (only when an invite or server details were given), starts it when it is configured (a new setup, or settings left by an earlier
// install, so an upgrade keeps working without new details), and checks that it runs. The same commands you can run by hand: Remotely.Host.exe setup / install.
procedure ConfigureAndStartHost;
var
  Host: String;
  Code: Integer;
begin
  if not WizardIsComponentSelected('host') then Exit;
  Host := ExpandConstant('{app}\Remotely.Host.exe');
  if NeedsSetup then
  begin
    if not WizardSilent then WizardForm.StatusLabel.Caption := 'Connecting this PC to your server...';
    Code := RunQuiet(Host, HostSetupArgs(''));
    if Code <> 0 then
    begin
      // An expired invite must not undo the installation: the app is installed, and Settings in the app takes a new invite.
      Log('Remotely.Host.exe setup failed with exit code ' + IntToStr(Code));
      if not WizardSilent then
        MsgBox('This PC could not be connected to your server (the invite may have expired). Remotely is installed. Open Remotely, Settings, and paste a new invite.', mbInformation, MB_OK);
    end;
  end;
  if HostConfigExists then
  begin
    if not WizardSilent then WizardForm.StatusLabel.Caption := 'Starting Remotely Host...';
    Code := RunQuiet(Host, HostInstallArgs(''));
    if Code <> 0 then FailHostStep('Remotely Host could not be started (exit code ' + IntToStr(Code) + ').');
    if IsAdminInstallMode and (not WaitForServiceRunning('Remotely.Host', 30)) then
      FailHostStep('The Remotely.Host service did not reach the running state within 30 seconds.');
  end;
  if Param('SimulateFailure') = '1' then FailHostStep('Simulated failure (requested with /SimulateFailure=1).');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then ConfigureAndStartHost;
end;

// After a failed upgrade (Setup has already undone what it could): put the previous program folder back and start the previous Host again.
procedure DeinitializeSetup();
begin
  if UpgradeFailed and UpgradeHaveBackup then
  begin
    if IsAdminInstallMode then StopServiceAndWait('Remotely.Host', 30);
    RunQuiet(ExpandConstant('{sys}\taskkill.exe'), '/IM Remotely.Host.exe /F /T');
    RunQuiet(ExpandConstant('{sys}\taskkill.exe'), '/IM Remotely.exe /F /T');
    Sleep(500);
    RestoreFromUpgradeBackup;
    if IsAdminInstallMode then RunQuiet(ExpandConstant('{sys}\sc.exe'), 'start Remotely.Host')
    else if HostConfigExists then RunQuiet(UpgradeAppDir + '\Remotely.Host.exe', 'install --user');
  end;
  if UpgradeFailed then ExitAfterFailedInstall;
end;

function InitializeUninstall(): Boolean;
begin
  StopRemotely();
  Result := True;
end;

// Remove the start-with-Windows value only if it points at this installation (the person may have set it up by hand for another copy).
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Value: String;
begin
  if CurUninstallStep = usUninstall then
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Remotely', Value) then
      if Pos(Lowercase(ExpandConstant('{app}\Remotely.exe')), Lowercase(Value)) > 0 then
        RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Remotely');
end;
