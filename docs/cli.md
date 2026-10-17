# Remotely.exe: command-line options

`Remotely.exe` is the Windows app. The options below are parsed in `src/Remotely.App/Shell/Core/CommandLine.cs` (covered by `tests/Remotely.Tests/ShellTests.cs`).
Options are case-insensitive. An option that needs a value and does not get one, and any unknown option, is ignored and noted in `%LOCALAPPDATA%\Remotely\logs\app-crash.log`.
The app has no console window, so there is no `--help` output; this page is the reference.

| Option | What it does |
|---|---|
| `--invite RMT1-...` | Joins the server in the invite (stores the address and pin, pairs this device), then shows **My computers**. If joining fails, a dialog shows the reason. The installer starts the app this way after installation. |
| `--pair CODE` | Pairs with the server that is already configured. Ignored when no server is configured. |
| `--connect-token TOKEN` | Opens **This network** and puts the token into the token box. It does not press Connect. (It fills the text box named `TokenBox` on the current page; the Connect page of a later version must keep that name or read `App.Options.ConnectToken` itself.) |
| `--minimized` | Starts in the notification area without showing the window. This is what start-with-Windows registers. Falls back to showing the window when the tray icon could not be created or the first-run flow is due. When another Remotely is already running, `--minimized` alone does nothing. |
| `--show PageName` | Opens the app on the page class with that short name (for example `SettingsPage`) and verifies it (contract below). Used by CI. Skips the first-run flow, the tray icon and the single-instance check. |
| `--show-result FILE` | With `--show`: where to write the result line. Default: `show-result.txt` next to `Remotely.exe` (or `%LOCALAPPDATA%\Remotely\logs\show-result.txt` if that folder is not writable). |
| `--selftest-shell FILE` | Shell self-test: this process acts as the first instance (test-only single-instance name), starts `Remotely.exe --invite <fake value> --instance-name <name>` and checks that the first instance receives exactly those arguments through the named pipe and that the second copy exits with code 0. Writes `PASS` or `FAIL` plus details to FILE; exit code 0 on pass. The fake invite is never processed. |
| `--instance-name NAME` | Test only: use NAME instead of the per-user single-instance name. |
| `--selftest FILE` | Shares this PC on the network, connects to it with the displayed token, waits for a frame, sends input, checks that the used token is refused, writes `PASS` or `FAIL` plus details to FILE and exits (exit code 0 on pass). Does not use the single-instance check. |
| `--selftest-server FILE HOSTID` | The same through the configured server, against the host whose device id starts with HOSTID (the calls the My computers page makes). Does not use the single-instance check. |

## `--show` contract (for CI)

1. The old result file is deleted at start. The app never exits on its own after success: CI photographs it and then kills it.
2. Success: within 15 s the page was navigated to without exception (`Frame.NavigationFailed` and exceptions from `Navigate` are caught), its `Loaded` event fired, its `Content` is not null and its size is at least 1x1. The file then contains one line: `LOADED <PageName> <w>x<h>` (integers, device-independent pixels of the page).
3. Failure: the file contains `FAILED <PageName> <reason>`, `app-crash.log` gets an entry, and the process exits with **code 3**. Reasons: no such page class; navigation failed or threw; `Loaded` did not fire in 15 s; no content; zero size; unhandled exception while starting; window could not be built; string resources did not load.
4. A CI check is: start with `--show X --show-result f.txt`, wait, assert the process has not exited and `f.txt` matches `^LOADED X \d+x\d+$`.

## Only one Remotely per signed-in user

A second launch (without `--show` or a self-test) sends its arguments to the running Remotely and exits. The running window comes to the front and acts on them, so
`Remotely.exe --invite RMT1-...` works whether or not the app is open. The hand-over uses a named mutex and a named pipe that only the same user can open.
If the running copy does not answer within five seconds, the new process logs that and starts on its own.

## Tray icon, close to tray, start with Windows

- The tray icon follows the taskbar theme: `Assets\tray-light.ico` (light glyph) on a dark taskbar, `tray-dark.ico` on a light one, chosen from `SystemUsesLightTheme` and re-chosen when Windows reports a colour change.
- The tray icon menu: **Open Remotely**, **Share this PC on my network** (checked while sharing), **Keep running in the tray when closed**, **Start with Windows**, **Quit**. Left-click opens the window.
- Close to tray is stored as `CloseToTray` in `%LOCALAPPDATA%\Remotely\viewer.json`. It only applies while the tray icon exists.
- Start with Windows writes the value `Remotely` under `HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run` with the command `"<path>\Remotely.exe" --minimized`. No administrator rights are needed. It is also stored as `StartWithWindows` in `viewer.json`; the registry value is what Windows reads.

## Keyboard shortcuts

| Keys | Action |
|---|---|
| Ctrl+1 ... Ctrl+9 | Open the 1st to 9th page in the navigation pane |
| Ctrl+, | Settings |
| F1 | Open the project page in the default browser |

## Settings file keys added by the shell

`OnboardingDone`, `CloseToTray`, `StartWithWindows`, `ShareOnStart` (start sharing on the network when the app opens; chosen in the first-run flow, off by default), `WindowX`, `WindowY`, `WindowWidth`, `WindowHeight`, `WindowMaximized`.

## Adding a page to the navigation

Put an attribute on the page class; nothing in the shell changes:

```csharp
[NavigationPage("share", "Nav_Share", Order = 30, Glyph = 0xE8AF)]
public sealed partial class SharePage : Page { ... }
```

`"share"` is the tag used by `IAppServices.Navigate("share")`; `"Nav_Share"` is a key in `Strings/*/Resources.resw`; `Glyph` is a Segoe Fluent Icons code point; lower `Order` sorts first
(built-in: This network 10, My computers 20). A page with the tag of a built-in page replaces it; the tag `settings` replaces the Settings page.
