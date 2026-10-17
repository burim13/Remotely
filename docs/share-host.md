# Share this PC: host experience (A3)

## Parts

| Part | Where | Notes |
|---|---|---|
| State machine (Off, Ready, Connecting, AwaitingConsent, InSession, Error) | `Remotely.Core/Lan/Consent/ShareStateMachine.cs` | Plain class, unit tested. Each move returns false when not allowed from the current state. |
| Consent rules | `ConsentGate.cs` | Remembered PC: no question. Asking off: let in. Otherwise ask; no answer, error or no way to ask means Deny. |
| Remembered PCs, history, settings | `RememberedDevices.cs`, `SessionHistory.cs`, `ShareCoordinator.cs` | JSON files in the app data folder (`remembered-pcs.json`, `share-history.json`, `share-settings.json`), written temp-then-move. |
| Device proof, resume | `DeviceProof.cs`, `LanResume.cs`; design in `docs/lan-remembered-pcs.md` | |
| Pause control (view only) | `HostSessionChannel.cs` | Drops mouse, keyboard, clipboard, Ctrl+Alt+Del and file messages; screen requests still work. |
| Host hooks | `LanHost.cs` | New options `Consent`, `Identity`, `AllowResume`; new events `HandshakeStarted`, `HandshakeFailed`, `ConsentRequested`, `ConsentResolved`, `SessionStarted`, `SessionEnded`; `EndSession()`, `SetControlPausedAsync()`. The old events and `Start`/`NewToken` are unchanged. |
| QR code | `QrCode.cs` | Own encoder (no package, nothing to license beyond this repository's MIT): byte mode, versions 1-10, 4 error-correction levels. |
| App | `Remotely.App/ShareService.cs`, `Views/Share/*` | `ShareCard` (UserControl), `SharePage`, `SessionBannerWindow`, `ConsentWindow`, `ShareUiHost`, `ShareToasts`, `ShareSelfTests`. |

## QR code: what was checked

`tests/Remotely.Tests/QrCodeTests.cs` has structural tests and a switch (`QR_DUMP_DIR`) that writes images. On the Linux build machine 712 images (all four error-correction levels,
lengths 1 to 213 plus the token, versions 1 to 10) were decoded with an independent reader (zxing-cpp 3.x from PyPI, used by hand, not part of the repository): all 712 decoded to
the exact text. Not covered by that run: level L at version 10, any text over 213 bytes, and a phone camera.
The token screen draws a token (14 characters, version 1) as black squares on a white background with a 4-module quiet zone.
What a phone does with the text is up to the phone; the QR holds only the token text.

## Hosting the card elsewhere (for the integrator; HomePage is A4's)

- `new ShareCard()` is a self-contained UserControl: put it in a column of `HomePage.xaml` in place of the old Share border (`<local:ShareCard />` with
  `xmlns:local="using:Remotely.App.Views.Share"`). It attaches the question window, banner and toasts when it loads. Remove the share code from `HomePage.xaml.cs`
  (`OnShareToggled`, `RefreshToken`, `OnCopyToken`, `OnNewToken`, the `ShareBar`).
- `SharePage` is the whole screen (card, remembered PCs, history). Add a navigation item in `MainWindow` with `typeof(SharePage)`.
- `ShareService` keeps its old members (`Start(Action<string>)`, `Host`, `IsSharing`, `StopAsync`, `DisposeAsync`), so old callers still compile. `ShareService.Start`
  attaches the windows itself when called on the UI thread, so even the old Home card shows the question window.
- Toasts go through `ShareService.ToastSink` (default `ShareToasts.Show`, using `AppNotificationManager`); the shell may point it at `IAppServices.Toast`.

## Self-test hook (A2 owns `SelfTest.cs` / `App.xaml.cs`)

`ShareSelfTests.RunAsync(path)` writes PASS or FAIL like the other self-tests; `ShareSelfTests.RunCoreAsync(log, DispatcherQueue.GetForCurrentThread())` can be appended inside
`SelfTest.RunAsync`. Suggested: a flag `--selftest-share result.txt` in `App.xaml.cs` calling `Environment.Exit(await ShareSelfTests.RunAsync(args[i + 1]))`.
It checks: a denied PC gets no session and is in the history; Always allow opens a session and the banner window exists while it runs and is gone afterwards; pause control is
reflected; the same device key reconnects without a token; another key and a removed key are refused.
The existing `--selftest` still passes the token flow without a question window because `ShareService` answers "allow once" itself when the process was started with `--selftest`.
