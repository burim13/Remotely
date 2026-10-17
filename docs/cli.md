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
| `--show PageName` | Opens the app on the page class with that short name (for example `SettingsPage`). Used by CI to start and photograph every screen. Skips the first-run flow, the tray icon and the single-instance check. If the string resources fail to load, the process exits with code 3. |
| `--selftest FILE` | Shares this PC on the network, connects to it with the displayed token, waits for a frame, sends input, checks that the used token is refused, writes `PASS` or `FAIL` plus details to FILE and exits (exit code 0 on pass). Does not use the single-instance check. |
| `--selftest-server FILE HOSTID` | The same through the configured server, against the host whose device id starts with HOSTID (the calls the My computers page makes). Does not use the single-instance check. |

## Only one Remotely per signed-in user

A second launch (without `--show` or a self-test) sends its arguments to the running Remotely and exits. The running window comes to the front and acts on them, so
`Remotely.exe --invite RMT1-...` works whether or not the app is open. The hand-over uses a named mutex and a named pipe that only the same user can open.
If the running copy does not answer within five seconds, the new process logs that and starts on its own.

## Tray icon, close to tray, start with Windows

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
