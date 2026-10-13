# Remotely: architecture, and what has been verified

## Decisions (owner)

| Topic | Decision |
|---|---|
| Name, licence | Remotely, open source (MIT) |
| Targets | Windows 11 x64 and ARM64; macOS Intel and Apple silicon |
| Framework | .NET 10 on Windows and for the server; native Swift on macOS (WPF does not exist there) |
| Windows UI | WPF, reusing Portico's theme files (Dark/Light/High-contrast dictionaries, controls, icons, IBM Plex Sans) |
| No public servers | Everything is self-hosted: one small server you run on your own always-on PC or VM |
| Reach | Same network and from outside; direct peer-to-peer was planned (see "Not built") but all traffic currently goes through your server |
| Survive sign-out | Windows service (SYSTEM) plus a helper in the user's session; a no-administrator "user mode" is offered too |
| Security | End-to-end encrypted; the server cannot read a session; devices must be trusted by the host |
| Installers | Inno Setup (like Portico); self-signing is left to you (`build\sign.ps1`) |

## Components

```
Remotely.Core        wire protocol, device identity, mutual-auth key exchange, AES-GCM channel
Remotely.Client      coordinator connection (server-key pinning), relayed sessions, SecureSession
Remotely.HostCore    HostAgent: serves frames, input, clipboard, files; trust and control permissions
Remotely.ViewerCore  ViewerSession: frame pump, coalesced input, clipboard, file transfer
Remotely.Server      coordinator and relay: pairing, device list, blind relay, loopback admin API, Windows service
Remotely.Host        Windows host: service + session helper, or per-user mode; GDI capture, SendInput, clipboard, Ctrl+Alt+Del
Remotely.Viewer      Windows viewer (WPF, Portico theme)
Remotely.Probe       command-line test viewer, and a "fake host" for testing viewers on any OS
mac/RemotelyKit      the same protocol in Swift (CryptoKit), byte-compatible with the .NET side
mac/RemotelyHostMac  Mac host: ScreenCaptureKit, CGEvent, NSPasteboard, menu bar item, LaunchAgent
mac/RemotelyViewerMac, RemotelyUI, RemotelyCLI   Mac viewer app (SwiftUI, Portico palette), shared theme, command-line viewer
installer/           Inno Setup scripts (client, server)
```

### Security model
- **Identity:** each device has an ECDSA P-256 key. A device id is the SHA-256 of its public key.
- **Pairing:** a one-time code (15 minutes, single use, lockout after five wrong guesses per address) registers a device with the server. The server only ever holds public keys.
- **Server authenticity:** the server makes its own P-256 TLS certificate; every device pins the SHA-256 of its public key (shown at install). No domain name or certificate authority is involved.
- **Session:** viewer and host run an authenticated ephemeral ECDH over the relay (each signs its ephemeral key and the transcript), derive two AES-256-GCM keys with HKDF-SHA256, and use strictly increasing counters (tamper, replay and reorder are rejected). The server forwards ciphertext it cannot read.
- **Authorisation:** pairing with the server is not enough. The host keeps its own list of viewer ids allowed to **control** or only **watch**; nobody is trusted by default.
- **Files:** uploads go to a receive folder (never overwrite, size and SHA-256 checked, names sanitised); downloads are limited to a share folder, with no path parts accepted.

### Windows host design
A service in session 0 cannot see a desktop. The service takes the SYSTEM token of `winlogon.exe` in the active console session and starts a helper there. The helper attaches one dedicated thread to **whichever desktop currently has input** (`Default`, or `Winlogon` for the lock/sign-in screen and UAC), captures with GDI, and injects with `SendInput`. Two pipes (capture/clipboard, and input) keep mouse input from queueing behind a screen grab. The helper's pipes are SYSTEM-only and the service checks the connecting process id.

### Joining: invite and family mode
A new device joins with one **invite** (`RMT1-` + base64url of: version, the 32-byte server key pin, 8 code characters, the address, and a 3-byte checksum).
The code inside is a normal pairing code that the server can allow several uses (the invite command asks for 10 uses, 1 hour; a plain code is still
single use, 15 minutes). A host set up from an invite is in **family mode**: it accepts any device the server has paired, with listed view-only
devices still watching only. Trade-off, stated plainly: the per-PC trust list no longer protects against a stranger who gets a device paired with
your server, so the server's PC and the invites are the thing to protect. `trust-all off` brings the per-PC list back.

## What has been verified, and how

"CI" means GitHub's hosted machines, driven by `.github/workflows`. These are real Windows (x64: Windows Server 2025; ARM64: Windows 11 ARM) and macOS machines, but headless: they prove the plumbing and the code paths, not what a person sees on your screen.

| Claim | Evidence |
|---|---|
| Protocol, crypto, server, host logic, viewer logic | 50 automated tests on Linux, running the real server over real sockets; deliberate breakage of three safety checks was caught by the matching test |
| Service launches a SYSTEM helper in the console session and captures the screen | CI on **Windows x64 and Windows 11 ARM64** |
| Mouse input is injected | CI reads the cursor position after a remote move (exact on both architectures) |
| Clipboard, upload with checksum, shared-folder download, path-traversal refusal | CI, both architectures, both service and user-only mode |
| Lock screen is captured | CI on **x64**: `desktop=Winlogon` (real image). On the ARM64 runner the input desktop stays `Default` after `LockWorkStation` until Ctrl+Alt+Del is sent; after that `Winlogon` is captured (`Access is denied` seen once at the switch, now retried) |
| Ctrl+Alt+Del | Service mode reports the capability and sends it; the secure desktop appears afterwards in the captured frames |
| Installers | Inno scripts compile in CI; the silent installers are run in CI (server, then client for all users, then client for the current user, then uninstall) |
| TLS pinning | CI installs the server with its installer and pairs over `wss://` with the pin; a wrong pin is refused |
| The Windows viewer launches, pairs, lists devices and opens a live session | CI launches the real `Remotely.exe` on Windows and checks the host saw it connect |
| The macOS `.dmg` is a universal build | The `mac` workflow builds the Intel + Apple-silicon binaries, checks each one with `lipo -archs` for both `x86_64` and `arm64`, and attaches the `.dmg` to the draft release (CI run 38467937154). Nothing was launched on a real Mac |
| Joining with an invite | CI, Windows x64 and ARM64: the server prints an invite; a service host is set up from the invite alone (family mode, no pin, code or trust list) and a viewer started with `--invite` joins and connects to it; the installers do the same silently. CI, macOS: a Swift viewer joins with the invite and a Swift host in family mode lets it in. The invite string is also checked against a known value built outside both implementations |
| Windows viewer appearance | Screenshots taken by CI on a real Windows x64 runner: [first run: paste the invite](screenshots/viewer-welcome.jpg), [device list](screenshots/viewer-devices.jpg), [live session](screenshots/viewer-session.jpg) (the session shows the runner's own desktop, so it repeats inside itself) |
| Swift code is byte-compatible with .NET | Known-answer vectors generated by .NET (AES-GCM frame, HKDF output, ECDSA signature, field framing), plus CI interop between the Swift host and the .NET viewer, and the .NET host and the Swift viewer, through the real server |

Last full green run: build 38462452002 (Windows x64 and ARM64 end-to-end, installer smoke on both, draft release) and mac 38467937154 (Swift tests, both-direction interop with the .NET side, universal `.dmg`), both on commit ab3d6ff or its descendants. Bugs the CI found along the way (viewer start-up crash from duplicate icon keys, a Swift pairing race, wrong-pin errors not reported) are fixed in the history.

## Not verified (needs your hands)

- **A real person's screen.** Everything above ran on headless CI machines. Real monitors, multiple monitors, HDR, high-DPI scaling, and a physical lock screen on your PCs are untested. (`Remotely.Host.exe diag capture.jpg` and `docs/prototype.md` give you a five-minute check.)
- **UAC prompts** (secure desktop with a dialog) were not triggered.
- **Remote input into a real application**: the cursor and the injected key events were observed, not typing into an application.
- **Mac screen capture and input.** ScreenCaptureKit and CGEvent need the Screen Recording and Accessibility permissions, which can only be granted by a person at a real Mac. The Intel-Mac and Apple-silicon builds compile and their protocol is tested; capture and input are not.
- **Internet reach.** Your router's port forwarding, your internet provider (CGNAT), and dynamic DNS cannot be tested from here.
- **Performance** on slow links or at high resolutions. Video is a stream of JPEG frames pulled on demand (sent only when the screen changed), not a hardware video codec.

## Not built (honest list)

- **Direct peer-to-peer** (hole punching) with relay fallback: all traffic goes through your server. Safe and simple; fine on a home network, costs your server's upload bandwidth.
- **Hardware video codec** (H.264/HEVC), multi-monitor selection, audio forwarding, session recording, chat, address book, multi-user accounts and roles. Family use is covered by the device trust lists.
- **Code signing:** the installers and apps are unsigned (macOS: ad hoc). Windows shows SmartScreen once; macOS needs right-click Open and re-asks for the two permissions after every rebuild.
- **macOS login window and "survive log-off"**: macOS does not allow an app to capture the login screen; the Mac host is a login-item LaunchAgent.
- **Auto-update.** Install a newer installer over the old one; settings and keys are never touched.
