# Remotely: architecture and phase plan

Written 2026-10-13 from the owner's answers. Items marked **VERIFY** are design intentions I have not yet
proven; they get a prototype before anything depends on them. Items marked **OPEN** need an owner answer.

## Decisions taken (owner)

| Topic | Decision |
|---|---|
| App name | Remotely |
| Targets | Windows 11 x64 and ARM64; macOS Intel and Apple silicon |
| Framework | .NET 10 (Windows, server); native Swift on macOS (WPF does not exist there) |
| UI | WPF on Windows, reusing Portico's theme (see "Theme") |
| Standalone | Not a Portico module |
| Users | The owner and family only |
| Survive logoff | Yes: Windows service and macOS LaunchDaemon, unattended access |
| No public servers | Everything is self-hosted; the app must work on and off the home network |
| Connection | Direct peer-to-peer first, relay fallback |
| Security | End-to-end encrypted; the relay cannot read the session |
| Signing | Self-signed (Windows cert on the owner's build PC; macOS ad hoc) |
| Licence | Open source (licence file to choose: **OPEN**, MIT unless told otherwise) |
| Server host | The owner's Windows Server 2025 VM |

## Components

```
Remotely.sln
src/
  Remotely.Core        Wire protocol, crypto, framing, shared models. No UI. net10.0
  Remotely.Server      Coordinator: device registry, signaling, relay. Windows service. net10.0
  Remotely.Host        Windows service (SYSTEM) + per-session agent. win-x64, win-arm64
  Remotely.Viewer      WPF app: device list, session window. win-x64, win-arm64
  Remotely.Theme       Resource dictionaries copied from Portico (see below)
mac/
  RemotelyMac          Swift: host daemon + viewer, universal binary
installer/             Inno Setup scripts (Windows) and pkg scripts (macOS)
build/                 publish and sign scripts, modelled on Portico's
tests/
docs/
```

### Server (on the Windows Server 2025 VM)
- ASP.NET Core (Kestrel) hosted as a Windows service. One listening port.
- Duties: register devices by public key, let a viewer ask to reach a device, exchange connection
  candidates for peer-to-peer, and forward encrypted bytes when peer-to-peer fails.
- It stores device public keys and names, nothing else. It never holds session keys.
- TLS uses a self-generated server certificate. Clients **pin the server's public-key fingerprint at
  pairing**, so no domain name or public CA is needed.
- **OPEN, blocking for "outside the network":** the VM must be reachable from the internet: a public IP or
  one router port forwarded to the VM, and a dynamic-DNS name if the home IP changes. A VM does not change this
  requirement. Also check the ISP does not use CGNAT (router WAN IP vs. an external "what is my IP" lookup).

### Identity and pairing
- Each device generates an ECDSA P-256 identity key on first run. Private keys are stored with DPAPI
  (Windows) or Keychain (macOS).
- Pairing: the server operator creates a one-time pairing code; the device presents it with its public key
  and the server's fingerprint is shown and pinned. Viewer and host then trust each other by public key
  (trust on first use), with an optional approve-on-the-host prompt per connection.
- Family accounts: a small local user list on the server with roles (admin, family). Simple, no external IdP.

### Session security (end to end)
- Per-session ephemeral ECDH (P-256), signed by each side's identity key, then HKDF-derived keys and
  AES-256-GCM with per-direction counters. All of these exist in the .NET base library and in Apple CryptoKit,
  so the Windows and macOS sides interoperate without third-party crypto. **VERIFY** with cross-platform test vectors.
- The relay forwards ciphertext only.

### Connection path
1. Both sides connect out to the coordinator over WSS (works through NAT).
2. Peer-to-peer: UDP hole punching using the coordinator as the introducer (**VERIFY** success rate on the
   owner's networks). 3. Fallback: relay through the coordinator over the same WSS connection.
Phase 1 ships relay only, so remote access works before peer-to-peer exists.

### Windows host
- A Windows service running as SYSTEM keeps the connection to the coordinator and survives logoff.
- Screen capture cannot happen in session 0. The service launches a helper in the active console session
  (and on the secure desktop for the lock screen and UAC). **VERIFY** this with a prototype before building on it.
- Capture: DXGI Desktop Duplication. Encode: Media Foundation H.264, hardware where present, software fallback.
  **VERIFY** the encoder is available and fast on Windows 11 ARM64, on the owner's ARM machine.
- Input: `SendInput`; clipboard and file transfer over the encrypted channel.
- Install needs administrator rights (one UAC prompt), unlike Portico's per-user installer.

### Windows viewer
- WPF. Decode with Media Foundation, render to a GPU-backed surface. **VERIFY** frame pacing against a
  `WriteableBitmap` baseline.

### macOS
- Swift. ScreenCaptureKit for capture, VideoToolbox for H.264, CGEvent for input.
- Needs Screen Recording and Accessibility permissions, granted by the user in System Settings.
  A LaunchDaemon keeps the host running after logoff (**VERIFY** what is capturable at the login window).
- Built as a universal binary (x86_64 + arm64). The owner has only an Intel Mac, so **the Apple silicon half
  will compile but be untested**; it needs a tester.
- Distribution: ad hoc signed; first launch uses right-click, Open.

## Theme (copied from Portico, not reinvented)
Source: `burim13/portico` at `src/Docket.App/Themes/{Light,Dark,HighContrast,Glass}.xaml`,
`Styles/Controls.xaml`, `Styles/Icons.xaml`, `Shell/ThemeManager.cs`, font IBM Plex Sans (OFL).
Known values from Dark: panel `#1C2127`, surface `#2B3239`, text `#F2F4F5`, accent `#F7BE55`; radii 14 / 8 / pill;
type scale 17/16/14/13/12/11. The macOS app mirrors the same colours, font and radii natively.

## Installer
One installer per architecture offers two modes (Inno Setup privileges dialog; exact behaviour **VERIFY** when built):
- **Full (admin, one UAC prompt):** service + session helper. Unattended access, survives logoff, reaches the lock screen and UAC.
- **User only (no admin):** per-user app started at login, like Portico. Works only while someone is logged in; cannot show the
  login screen; cannot send input into elevated windows (Windows blocks it for a non-elevated process).
Both use the same viewer and protocol. Inno Setup 6, one installer per architecture, `build/publish.ps1` modelled on Portico's, signed with the
owner's self-signed cert, upgrades in place, **never touches user data**. Installs the host service, viewer,
and (optionally) the server component. Same SmartScreen caveat as Portico (`docs/code-signing.md` there).

## Phases (each ends with something running and tested before the next starts)
1. **Foundation:** solution, Core protocol and crypto with tests, Server (register, pair, relay), Windows host
   service + session helper, WPF viewer with Portico theme, screen + mouse + keyboard over the relay, installers
   for x64 and ARM64. Exit test: control the ARM PC from another network through the VM.
2. Clipboard, multi-monitor, file transfer.
3. Peer-to-peer hole punching with relay fallback.
4. macOS host and viewer (Intel tested; Apple silicon compiled, needs a tester).
5. Audio, chat, session recording, address book, family accounts and roles.

## Testing limits
This build environment is Linux. I can compile and unit-test `Core` and `Server` here. I cannot run the WPF
apps, the Windows service, screen capture, or macOS code here: those need the owner's machines, and I will say so
for each change rather than claim it works.

## Phase 1 progress

| Piece | State |
|---|---|
| `Remotely.Core`: identity (ECDSA P-256), mutual-auth ECDH handshake, AES-256-GCM channel, wire framing, control protocol | Built, 11 unit tests pass (Linux) |
| `Remotely.Server`: pairing codes + lockout, device store, signalling, blind relay, TLS with self-generated cert, loopback admin API, Windows-service hosting | Built, run for real on Linux |
| `Remotely.Client`: coordinator connection with server-key pinning, relayed sessions, `SecureSession` | Built |
| Integration tests (13): pairing, lockout, unpaired refusal, admin port isolation, end-to-end encrypted echo through the relay, untrusted-viewer refusal, tamper detection, session teardown, TLS pin accept/reject, restart persistence | Pass, stable over repeated runs (Linux) |
| `Remotely.Host` (service + session helper), `Remotely.Viewer` (WPF, Portico theme), screen/input, installers | **Not started.** Need Windows to run; see "Testing limits". |

The server is not yet verified as a running Windows service on Server 2025: only built and run on Linux here.

## Running the server (current state)
```
dotnet run --project src/Remotely.Server
# prints: Server key pin (give this to devices when pairing): <hex>
curl -X POST http://127.0.0.1:8181/admin/pair-codes      # on the server itself; loopback only
```
Settings (`appsettings.json`, section `Remotely`): `PublicPort` (8443), `AdminPort` (8181), `TlsEnabled`, `DataDir`.
