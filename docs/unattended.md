# Unattended access: the app, the service and the pipe

Written by agent A13. It describes what is in the code on branch `claude/agent-13-service-integration`, what a test or a CI run showed, and what nothing has shown yet.
Statements marked **(unverified)** are not backed by a test or a run.

## What the feature is

Remotely in a signed-in window cannot see the Windows lock screen, the sign-in screen or the dimmed screen that Windows shows for administrator (UAC) prompts. The Windows
service `Remotely.Host` can: it runs as SYSTEM, starts a capture helper in the console session with the SYSTEM token of `winlogon.exe`, and the helper captures whichever desktop
has input (`docs/architecture.md`, "Windows host design"). Before this work only a connection **through your server** could use that. Now a **same-network token** connection can too:

1. An administrator allows it once (`Remotely.Host.exe unattended lan on`, or the switch on the page, which asks Windows for administrator rights).
2. The signed-in user switches "Share this PC through the service" on, on the **Unattended access** page of the app.
3. The service listens for tokens (the same `LanHost` the app uses) and the page shows the token. A PC on the same network types it and gets the screen the service captures, lock screen included.

## Parts

| Part | Where | Notes |
|---|---|---|
| Page | `src/Remotely.App/Views/Unattended/UnattendedPage.xaml(.cs)` | `[NavigationPage("unattended", "Unattended_Nav", Order = 40)]`. Service state with install/start/stop/remove, a live status line, lock screen and UAC explanation, sharing through the service, Ctrl+Alt+Del policy switch, stay-awake switch. |
| Controller | `.../UnattendedController.cs` | No screen. Polls the service once a second while a page needs it (or while the service is listening, so its questions are still answered with the page closed). Never trusts a pipe before checking who owns it. |
| Elevation | `.../ElevatedHost.cs`, `Protocol/ElevationRules.cs` | One fixed argument list per action (`ElevatedAction`), one Windows prompt per click, no retry. Refuses a `Remotely.Host.exe` that is not below Program Files. |
| Service state | `.../ServiceControl.cs` | The service manager's own API (`QueryServiceStatusEx`), not the text of `sc.exe` (whose words depend on the Windows language). |
| Shared protocol | `.../Protocol/*.cs` | Pipe messages, framing, client, status sentences. No WinUI, no Windows-only calls. **Compiled into three projects**: the app (it is in the app's tree), `Remotely.Host` (linked in `Remotely.Host.csproj`) and the unit tests (linked in `Remotely.Tests.csproj`). |
| Service side, logic | `src/Remotely.Host/Unattended/Core/*.cs` | Pipe rules, pipe server loop, question relay, `ServiceLan`, keep-awake, settings file. No Windows-only calls, so the Linux tests compile them (linked in `Remotely.Tests.csproj`). |
| Service side, Windows | `src/Remotely.Host/Unattended/WindowsGlue.cs`, `Commands/UnattendedCommand.cs`, `Service/HostService.cs` | The access list on the pipe, who the client is, who is signed in, the power request, firewall rules, the registry value for Ctrl+Alt+Del. |
| Self-test | `.../UnattendedSelfTest.cs` | `Remotely.exe --selftest-service FILE [Desktop]` once the shell calls it (see "Requests"). |

## Who makes the token (a change from the task wording)

The task text says the app creates the token and hands it to the service. `LanHost` (owned by A3) creates its own token inside and offers no way to give it one, and the token is replaced by the
host itself after every use, after three wrong tries and when it expires. The service therefore **creates the token and the app reads it** over the pipe. The security is the same
(the token only ever travels over the pipe that only the signed-in user can open) and the app needs no extra step to learn the next token. If the app must create it, `LanHostOptions` needs an
`InitialToken` and a way to hand the next one in (a change in `Remotely.Core/Lan/LanHost.cs`); this is listed under "Requests".

## The pipe

Name `\\.\pipe\Remotely.Unattended`. One message = 4-byte big-endian length (1 to 16384) and that many bytes of UTF-8 JSON, request then reply, one at a time per connection. A longer announced
length ends the conversation before anything is allocated. Commands (`PipeCommands`): `status`, `start`, `stop`, `new-token`, `answer` (with the id of the open question and `once` or `deny`), `end-session`.
Every reply carries the full status (`UnattendedStatus`: whether sharing through the service is allowed, listening, the token and its expiry, the port, whether the screen helper is connected,
the desktop of the newest frame, who is connected and since when, the open question, whether Windows is being kept awake).

Not on the pipe, on purpose: anything that changes what is allowed (allow sharing through the service, ask-before-connecting, keep-awake, Ctrl+Alt+Del policy, install/remove). Those go through
`Remotely.Host.exe` run as administrator and write `%ProgramData%\Remotely\unattended.json` (SYSTEM and administrators only) or the registry.

### Access list

`PipeAccessPolicy.Sddl(userSid)` is `D:P(D;;0x1F01FF;;;NU)(A;;0x1F01FF;;;SY)(A;;0x12019B;;;<signed-in user's SID>)`: no inheritance; network logons denied first; SYSTEM (the service) all access; the one
user read/write data and attributes, read permissions and synchronize, **without** the right to create another instance of the pipe. Nobody else is named: not Everyone, Users, Authenticated Users or
Administrators (an elevated process of the same user has the same user SID and is covered). The service finds the user with `WTSQueryUserToken` on the console session and makes a new pipe instance
whenever that user changes (sign-out, fast user switching); with nobody signed in only SYSTEM remains. The first instance is created with `FirstPipeInstance`, so the service refuses to start the pipe
if another program already owns the name (it logs that and keeps retrying).

### Second check, after a client connected

Before reading a byte the service checks the client again (`PipeAccessPolicy.Decide`, using `RunAsClient` and `GetNamedPipeClientSessionId`): the user SID equals the signed-in user's, the client is in the console
session, and it did not come over the network (the token contains the NETWORK group). A client that fails gets no reply at all and the connection is closed (and logged: `Refused a program that connected to the unattended pipe (WrongUser)`).

### The client checks the server

Any program can create a pipe with this name before the service does. The app therefore sends nothing until `GetNamedPipeServerProcessId` equals the process id the service manager reports for `Remotely.Host`
(`UnattendedController.VerifyServer`). A mismatch shows "Something other than the Remotely service is using the service's connection name".

### Questions ("Allow this PC?")

The service has no screen. With "ask before connecting" on (the default), the question is put in the status (`Question`), the app shows its normal question window (`ShareUiHost.AskAsync`, A3's window) and
answers with `answer`. If the app has not asked for the status in the last six seconds the question is refused at once; if it is not answered within the gate's 25 seconds it is refused. "Always allow" is never offered
through the service: remembered PCs are switched off there (`AllowResume = false`, empty in-memory list), so a PC needs a token every time. "Ask" can be switched off by an administrator for a PC that nobody sits at.

## Threat note

| Threat | What the code does | Status |
|---|---|---|
| Another local user talks to the service through the pipe | The access list names only SYSTEM and the signed-in user; the second check compares SID and session. | Logic tested on Linux (`UnattendedAccessTests`). That **Windows enforces** the list and refuses a second local user is **(unverified)**: needs a Windows run (see "CI steps to add"). |
| A user connected over the network (SMB) | Deny ACE for NETWORK; second check refuses a client whose token has the NETWORK group. | Logic tested; enforcement **(unverified)**. |
| Same user, another session (remote desktop) | Second check requires the console session. | Logic tested; **(unverified)** on Windows. |
| A program squats the pipe name before the service | `FirstPipeInstance` in the service; the app checks the server's process id before sending anything. The squatter learns nothing: the app has sent nothing. | Client behaviour tested with a fake server (`Nothing_is_sent_to_a_program_that_is_not_the_service`); the process-id comparison itself is **(unverified)**. |
| Malware running as the signed-in user | It can do what the user can: read the token from the pipe, start or stop listening **if an administrator allowed sharing**, answer a question "once". It cannot allow sharing, turn questions off, or change the Ctrl+Alt+Del policy (administrator rights needed). This is the same trust the app's own "Share this PC" already places in the signed-in user. | Accepted. |
| Someone swaps `Remotely.Host.exe` before the user clicks an administrator action | The app asks for administrator rights only for `Remotely.Host.exe` below `Program Files`; a per-user install cannot use the page for the service. The check is on the path text only (no junction or folder-permission check). | Path rule tested; junctions **(unverified)**. |
| A PC on the network guesses the token | Unchanged token rules (A9): 8 secret characters, 3 tries, single use, 10 minutes. Through the service the same `LanHost` runs. | Existing tests; the service path is tested end to end on Linux (`UnattendedServiceTests`). |
| Service listens although it should not | `LanAllowed` is off until an administrator turns it on; turning it off stops a running listener within a few seconds (the service re-reads the file every 2 seconds and checks every 3) and removes the firewall rules. Listening is not remembered across a service restart. | Tested (`When_the_administrator_switches_sharing_off...`, `Until_an_administrator_allows_it...`). |
| The unattended code takes the service down | Its two tasks are wrapped: any exception is logged and the service, the helper and the server connection keep running. | By construction; a crash test of `Guarded` was not written. |

## Stay awake

While a viewer's frames are being captured (through the server or with a token), and the switch is on (default), the service holds a Windows power request (`PowerCreateRequest`/`PowerSetRequest`) for
"system required" and "display required", and lets go 30 seconds after the last frame (`SessionActivity`). The same applies to the user-only host. The logic (hold on first frame, one request however many frames,
release after 30 s, release at once when switched off or when stopping) is tested with a fake. That Windows really stays awake and the display stays on is **(unverified)**: it needs a PC that would sleep.

## Ctrl+Alt+Del

The existing `enable-sas` command writes `SoftwareSASGeneration = 1` under `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System`; the page's switch runs `unattended sas on|off` (off deletes the value).
Values: 0 none, 1 services, 2 Ease of Access applications, 3 both (the Windows policy "Disable or enable software Secure Attention Sequence"). The CI step "secure desktop (after Ctrl+Alt+Del) captured as Winlogon" runs `enable-sas` (value 1) first and then sends Ctrl+Alt+Del through the service.

## What the lock screen and UAC support does and does not cover

- Shown by CI (before this work, unchanged): the service captures the `Winlogon` desktop on the x64 runner after `LockWorkStation`; on the ARM64 runner it does after Ctrl+Alt+Del.
- Not shown by anything: typing a password or PIN into the sign-in screen, clicking an administrator (UAC) prompt, a real PC with a real lock screen.
- Windows Hello (fingerprint, face) and security keys need the person at the PC; they cannot be done from another PC. Whether an account that has *only* Hello and no password can be signed in with a typed PIN
  remotely is **(unverified)**. Accounts with the policy "Interactive logon: Do not require CTRL+ALT+DEL" disabled need Ctrl+Alt+Del first, which is what the policy switch is for.

## Service recovery

`install` sets `sc failure ... actions= restart/5000/restart/5000/restart/5000` (reset period one day, as before) and now also `sc failureflag 1`, so Windows also restarts the service when it stops
itself with an error code. The page states this as what the installer sets; it does not read the configuration back from Windows. Whether `failureflag` takes effect on the CI runner was not checked.

## The command line

`Remotely.Host.exe unattended status | lan on|off | ask on|off | keep-awake on|off | sas on|off | pipe status|start|stop|new-token|end-session`, and `service-start`, `service-stop`. Exit code 0 means it
happened. `lan on` also adds two Windows Firewall rules for `Remotely.Host.exe` (TCP 47810, UDP 47811, private and domain networks only) because the installer's rules name `Remotely.exe` and do not cover the
service; `lan off` and `uninstall` remove them. `pipe ...` is a small client for scripts and CI; it prints `token:` and `port:` lines.

## What the tests show, and what only Windows can

Linux tests (`tests/Remotely.Tests/Unattended*.cs`, 108 tests): framing and JSON (limits, hostile bytes); the access list parsed back to its entries; the second check for every combination of user, session and network; the pipe server over a real
pipe with a fake "who is this" (refused client gets no answer and the backend never runs; oversize, half and garbage messages; a failure inside the service; the signed-in user changing; nobody signed in);
the client refusing to send to a program that is not the service; the question relay; keep-awake; the settings file; the page's strings in both languages; the rule for asking for administrator rights; the status line; and
sharing by token through the service end to end over real sockets (allowed or not, the lock-screen desktop reported, a question with yes/no/nobody there, ending the session, switching it off).
Deliberately broken versions of three checks (no allow-list check, any question id accepted, wrong user let in) were caught by these tests.

Only Windows can show: that the pipe's access list refuses a second local user, that `RunAsClient` reads the right user and session, `WTSQueryUserToken`, the power request, the firewall rules, the elevated actions and
their exit codes, that the page draws and responds, and a token session capturing `Winlogon` through the service. The CI step "open every listed page" proves only that `Remotely.exe --show UnattendedPage` stays alive
for a few seconds; it does not prove the page rendered.

## CI steps to add (workflows are not mine to edit)

In the `smoke` job's service section, before the lock-screen block (so the PC is unlocked), and again after `LockWorkStation` for x64:

```powershell
& $hostExe unattended lan on
& $hostExe unattended ask off          # a headless runner cannot answer a question
$o = & $hostExe unattended pipe status; $o
Check "pipe: the signed-in user reaches the service" (($o -join "`n") -match 'ok: True')
Check "sharing through the service is allowed" (($o -join "`n") -match 'lan-allowed: True')
$t = Start-Process $appExe -ArgumentList '--selftest-service', "$PWD\selftest-service.txt" -PassThru
if (-not $t.WaitForExit(120000)) { $t.Kill() }
Check "token session through the service: frame received" ((Test-Path "$PWD\selftest-service.txt") -and ((Get-Content "$PWD\selftest-service.txt" -TotalCount 1) -eq 'PASS'))
# after LockWorkStation, x64 only:
$t = Start-Process $appExe -ArgumentList '--selftest-service', "$PWD\selftest-service-lock.txt", 'Winlogon' -PassThru
# a second local user must be refused (try; the runner may not allow Start-Process -Credential):
net user rmtother "Zq9!x-Rmt-$(Get-Random)" /add
# run '$hostExe unattended pipe status' as rmtother and expect 'pipe: AccessDenied' (or 'Unreachable'), never 'ok: True'
```

The service log (`C:\ProgramData\Remotely\logs`) already prints in the smoke output. Lines to look for: `The unattended-access pipe is ready for S-1-5-21-...`, `Listening for tokens on port`, and for a refused
program `Refused a program that connected to the unattended pipe`.

## Not built

- Listening automatically when the service starts (so a token is needed even with nobody signed in). Listening is switched on by the signed-in user and is not remembered across a restart.
- Remembered PCs through the service (no list to manage, no way to revoke from the app).
- A banner window for a session that runs through the service (A3's banner is bound to the app's own sharing). The page shows who is connected and offers Disconnect; the question window is A3's.
- Showing the service's recovery settings read back from Windows.
