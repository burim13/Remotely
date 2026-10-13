# Prototype: service + session helper

Goal: **prove, on your real machines, that a Windows service can show you the screen of the logged-in user, the lock screen, and the
sign-in screen, and send it to another device through your own server.** Nothing here is the final product. There is no input control
yet, no video codec (single JPEG frames on request), and the installer is a plain command.

**Everything below was compiled and unit-tested on Linux only. None of the Windows-specific code has been run on Windows.** Expect to
find problems; the point of this prototype is to find them early. The `diag` command and the log are there so you can paste back
exactly what happened.

## What you need
- The server running on your Windows Server 2025 VM (x64) and reachable from where you test (see "Server" below).
- `Remotely.Host.exe` for the machine under test (`win-arm64` for your ARM PC, `win-x64` otherwise).
- `Remotely.Probe` for the machine you watch from (Windows x64/ARM64 exe, or the macOS Intel build).

Get the files from the **build** workflow's artifacts on GitHub (Actions tab), or build them yourself with the .NET 10 SDK:
```
dotnet publish src/Remotely.Host  -c Release -r win-arm64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o out/host
dotnet publish src/Remotely.Probe -c Release -r win-x64    --self-contained -p:PublishSingleFile=true -o out/probe
dotnet publish src/Remotely.Server -c Release -r win-x64   --self-contained -p:PublishSingleFile=true -o out/server
```
Unsigned executables: Windows SmartScreen will warn. Right-click the file, Properties, Unblock (as with Portico).

## 0. Quick check without any server (2 minutes)
In an **administrator** terminal on the PC under test:
```
Remotely.Host.exe diag capture.jpg
```
Open `capture.jpg`: it should be your screen. Then paste me the `diag` output. This tells us the OS, architecture, whether `winlogon.exe`'s
token can be borrowed (I expect this to succeed only when run as SYSTEM, so an access error here from a normal administrator prompt is not by itself a problem), and whether GDI capture works at all.

To try the same as SYSTEM (what the service will be), use Sysinternals PsExec: `psexec -s -i Remotely.Host.exe diag capture-system.jpg`.

## 1. Server (on the VM)
1. Copy `Remotely.Server.exe` and `appsettings.json` to a folder, e.g. `C:\Remotely\Server`.
2. Run `Remotely.Server.exe` once in a terminal. It prints **`Server key pin ...`**. Keep that value: devices pin to it.
3. Open the public port (default 8443) in Windows Firewall, and forward it on your router to the VM. This is the step that decides
   "works from outside the network"; see `docs/architecture.md`.
4. On the VM, get a one-time pairing code (loopback only): `curl.exe -X POST http://127.0.0.1:8181/admin/pair-codes`
   (Run it as a Windows service later; for now a terminal is fine.)

## 2. Watcher (viewer side)
```
Remotely.Probe pair --server wss://<your address>:8443/ws --pin <server key pin> --code XXXX-XXXX --name "My laptop"
```
It prints this viewer's **device id**. Keep it for the next step.

## 3. Host (the PC under test), administrator terminal
```
Remotely.Host.exe setup --server wss://<your address>:8443/ws --pin <server key pin> --code YYYY-YYYY --name "Dad's PC" --trust <viewer device id>
Remotely.Host.exe install
```
(`YYYY-YYYY` is a second pairing code from the server. Codes work once and expire after 15 minutes.)
Look at `C:\ProgramData\Remotely\logs\host-<date>.log`. You should see, in order: the host starting, "Started helper (pid ...)",
"Helper connected; screen capture is available.", "Paired with the coordinator...", "Online as ...".

## 4. Watch
On the watcher: `Remotely.Probe list` shows the host as online and prints its device id. Then:
```
Remotely.Probe watch <host id or its first characters> --out frames --count 5 --interval 1000
```
Each line shows size and the **desktop** the image came from: `Default` is the normal desktop, `Winlogon` is the lock/sign-in/UAC screen.

## The test matrix (this is what the prototype exists to answer)
Run `watch` and look at the images for each situation, and note the log lines and any error text:

| # | Situation on the PC under test | Expected if the design works |
|---|---|---|
| 1 | You are signed in, desktop visible | `Default`, your desktop |
| 2 | Press Win+L (locked) | `Winlogon`, the lock screen |
| 3 | Sign out (stay at the sign-in screen) | `Winlogon`, sign-in screen; log shows helper (re)started |
| 4 | Restart the PC, do **not** sign in | after boot, `watch` works while only the sign-in screen is showing |
| 5 | Signed in, trigger a UAC prompt (run something "as administrator") | `Winlogon`, the UAC dialog (or a black image: report it) |
| 6 | Sign in again after #3/#4 | helper relaunches into the new session, `Default` returns |
| 7 | Stop the server for a minute, start it again | host logs the failure then reconnects by itself |

Test 4 and 5 are the ones I am least sure about. Possible findings: GDI capture of the secure desktop returns a black image (then I switch
to DXGI Desktop Duplication), or `SetThreadDesktop` fails (then the helper design changes). Either answer is useful.

## Cleanup
`Remotely.Host.exe uninstall` (administrator). Settings, key and logs stay in `C:\ProgramData\Remotely`; delete that folder to forget
the device. Remove the device from the server with `curl.exe -X DELETE http://127.0.0.1:8181/admin/devices/<id>`.

## Known limits of this prototype
- View only. No mouse or keyboard control yet.
- Pull model: each frame is requested by the viewer; no continuous video.
- The helper runs as SYSTEM in the user's session so it can follow the lock screen and UAC. The final design should run a normal-user helper
  for the normal desktop and use the SYSTEM helper only for the secure desktop.
- `Remotely.Probe` stores its key unprotected in `~/.remotely-probe`. The real viewer will not.
- Only the primary-session case is handled (no multiple simultaneous console sessions, no RDP sessions).
