; Remotely installer (Inno Setup 6). One per architecture, built by build\publish.ps1 or the CI "installers" job:
;   ISCC.exe /DArch=arm64 /DVersion=0.3.0 installer\Remotely.iss
;   ISCC.exe /DArch=x64   /DVersion=0.3.0 installer\Remotely.iss
; At the start the installer asks: for all users (administrator: the Host runs as a Windows service that survives sign-out and shows the
; lock screen) or only for me (no administrator: the Host runs while you are signed in). Same choice as Portico's per-user installer, plus the service.
; The only thing the wizard asks for is the invite that "Remotely Server info" prints on your server (it holds the address, key pin and a pairing code).
; Silent install for scripts and tests:  Setup.exe /VERYSILENT /SUPPRESSMSGBOXES /Invite=RMT1-... /Name="Dad's PC"
;   The long form still works: /Server=wss://host:8443/ws /Pin=<hex> /Code=XXXX-XXXX /Trust=<viewer id>
;   add /CURRENTUSER for the no-administrator mode, /Insecure=1 only for local testing over ws://

#ifndef Arch
  #define Arch "x64"
#endif
#ifndef Version
  #define Version "0.3.0"
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
SetupIconFile=..\src\Remotely.Viewer\Assets\remotely.ico
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
Name: "viewer"; Description: "Remotely: control your other computers from this PC"; Types: full custom
Name: "host"; Description: "Remotely Host: let your other devices control this PC"; Types: full custom

[Files]
Source: "{#Publish}\viewer\Remotely.exe"; DestDir: "{app}"; Flags: ignoreversion; Components: viewer
Source: "{#Publish}\host\Remotely.Host.exe"; DestDir: "{app}"; Flags: ignoreversion; Components: host

[Icons]
Name: "{group}\Remotely"; Filename: "{app}\Remotely.exe"; Components: viewer
Name: "{group}\Uninstall Remotely"; Filename: "{uninstallexe}"

[Run]
; Configure and start the host. Both commands are the same ones you can run by hand (Remotely.Host.exe setup / install).
Filename: "{app}\Remotely.Host.exe"; Parameters: "{code:HostSetupArgs}"; Flags: runhidden waituntilterminated; Components: host; StatusMsg: "Connecting this PC to your server..."; Check: NeedsSetup
Filename: "{app}\Remotely.Host.exe"; Parameters: "{code:HostInstallArgs}"; Flags: runhidden waituntilterminated; Components: host; StatusMsg: "Starting Remotely Host..."; Check: ShouldStartHost
Filename: "{app}\Remotely.exe"; Parameters: "{code:ViewerArgs}"; Description: "Open Remotely"; Flags: nowait postinstall skipifsilent runasoriginaluser; Components: viewer

[UninstallRun]
; Removes the service (or the sign-in entry). Your settings, device key and logs are never touched.
Filename: "{app}\Remotely.Host.exe"; Parameters: "uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveHostService"; Check: IsAdminInstallMode
Filename: "{app}\Remotely.Host.exe"; Parameters: "uninstall --user"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveHostUser"; Check: not IsAdminInstallMode

; RULE (as in Portico): the installer and uninstaller never touch user data. The host's key, settings and logs live in
; %ProgramData%\Remotely (service) or %LocalAppData%\Remotely (user), the viewer's in %LocalAppData%\Remotely, all outside {app}.
; A newer installer over an installed Remotely (same AppId) is an in-place upgrade of the program files only.

[Code]
var
  HostPage: TInputQueryWizardPage;

function Param(Name: String): String;
begin
  Result := ExpandConstant('{param:' + Name + '|}');
end;

// Index 0 = invite, 1 = this PC's name, 2 = server, 3 = pin, 4 = code, 5 = viewer ids. Only 0 and 1 are shown; the rest come from the command line (scripts, tests).
function HostPageValue(Index: Integer; ParamName: String): String;
begin
  if (Index <= 1) and (not WizardSilent) then Result := Trim(HostPage.Values[Index])
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
  HostPage := CreateInputQueryPage(wpSelectComponents, 'Connect this PC to your server',
    'Paste the invite from your own Remotely server.',
    'On the server, open "Remotely Server info" and copy the invite. It holds the address, the key and a one-time code, so there is nothing else to type. ' +
    'Leave it empty to connect this PC later.');
  HostPage.Add('Invite:', False);
  HostPage.Add('This PC''s name:', False);
  HostPage.Values[0] := Param('Invite');
  HostPage.Values[1] := Param('Name');
  if HostPage.Values[1] = '' then HostPage.Values[1] := GetComputerNameString;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = HostPage.ID) and (not WizardIsComponentSelected('host')) and (not WizardIsComponentSelected('viewer'));
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

// Start (or restart) the host when it was just set up, or when an earlier install left its settings behind: an upgrade keeps working without new details.
function ShouldStartHost: Boolean;
begin
  Result := NeedsSetup or HostConfigExists;
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
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  if IsAdminInstallMode then
  begin
    Exec(ExpandConstant('{sys}\sc.exe'), 'stop Remotely.Host', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(3000);
  end;
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM Remotely.Host.exe /F /T', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  StopRemotely();
  Result := True;
end;
