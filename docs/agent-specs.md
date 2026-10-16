# Remotely 1.0: agent specifications

Goal (Burim): a finished, polished Remotely with two WinUI 3 applications:

1. **Remotely**: the app every user runs on their PC to share it and to remote others (same-network token mode and server mode).
2. **Remotely Server Manager**: a WinUI 3 app that installs, runs and administers the server (today the server is only a console and service program).

"Most professional ever" is not something anyone can verify, so each agent below has acceptance checks that CI or a reviewer can verify. The bar is
the list of checks, not a claim.

## Ground rules for every agent

- **Verification is CI only.** WinUI 3 cannot be built or run on the Linux machine the agents work on. Every UI change is proven by the `build` workflow on
  real Windows x64 and ARM64. An agent does not report "done" until the pushed commit is green. If a screen cannot be checked, the report says so.
- **Facts only.** No invented APIs, no claims about behaviour that no test or CI run showed. Unknowns are reported as unknowns.
- **File ownership.** Each agent owns the paths listed and changes nothing else. A change needed elsewhere is requested from the integrator (A0) or the owner.
  Each agent works in its own git worktree and branch (`agent/<number>-<name>`); only A0 merges into `claude/wonderful-faraday-6sv2qe`.
- **Contracts first.** Shared interfaces (below) are written by A0 before Wave 1 starts, so agents code against them in parallel.
- **Evidence.** Each agent's report lists: what changed, which tests were added, the CI run id that proved it, what is not covered.
- **No new network services, no telemetry, no accounts.** Everything stays self-hosted (the original brief). Update checks (A17) contact only the project's own
  GitHub releases and are switchable off.
- **House style.** Match surrounding code; no comments that restate code; warnings are errors outside the WinUI project; MIT-compatible dependencies only.

## Shared contracts (written by A0 before Wave 1)

| Contract | Where | Purpose |
|---|---|---|
| `IServerAdmin` | `Remotely.Core/Admin` | Typed client for the server's admin API (invites, devices, revoke, status, audit, settings). Used by the Server Manager, the CLI and tests. |
| `ISettingsStore` / `IAddressBook` | `Remotely.Core/Storage` | Atomic JSON store with schema versions and migration; address book entries (name, id/address, notes, group, last used). |
| `IAppServices` (App) | `Remotely.App/Services` | Navigation, dialogs, toasts, theme, localisation, clipboard, file pickers, so pages are testable and do not reach into `App` statics. |
| Resource keys | `Remotely.App/Strings/en-US/Resources.resw` | Every user-visible string is a key; no literals in XAML or code-behind. |
| Design tokens | `Remotely.App/Themes/Tokens.xaml` | Colours, spacing, radii, type ramp, motion durations. Only A1 edits this file. |
| Protocol version table | `docs/protocol.md` | Each wire change gets a version number and a compatibility rule (older clients keep working or fail with a clear message). |

## Waves

- **Wave 0** A0 (me): contracts, branch layout, CI gates, screenshot publishing (see A19).
- **Wave 1** (parallel, disjoint paths): A1 to A12.
- **Wave 2** (needs Wave 1 merged and green): A13 to A19.
- **Wave 3** (hardening and release): A20 to A24. A22 and A23 also review the output of every earlier wave as it lands; A24 is the release gate.

---

## A0. Integrator (the main session)

**Mission.** Own the contracts, merge every agent branch, keep `main` of the working branch green, resolve conflicts, decide scope disputes.
**Owns.** Merge commits, `.github/workflows/*` (other agents request workflow changes), `docs/protocol.md`, `Remotely.slnx`, `Directory.Build.props`.
**Deliverables.** Contracts above; CI jobs for new projects; one merge per agent after its CI is green; running log `docs/changes-1.0.md`.
**Acceptance.** Working branch green after every merge; no agent branch merged with a red run.

## Wave 1

### A1. Design system and theme (WinUI)
**Mission.** One consistent Portico-derived look in dark, light and high contrast, with motion that is subtle and respects the Windows "animation effects" setting.
**Owns.** `src/Remotely.App/Themes/**`, `src/Remotely.App/Controls/**`, `src/Remotely.App/Assets/**`.
**Deliverables.** `Tokens.xaml` (colour, spacing 4/8/12/16/24/32, radius, type ramp on IBM Plex Sans with Segoe UI fallback, elevation, motion); reusable controls
(PageHeader, Card, StatusPill, TokenBox with per-group editing and paste normalisation, DeviceTile, EmptyState, InlineBanner, SectionTitle); full icon set
as vector `PathIcon`s; app icon and tray icon in 16 to 256 px incl. dark/light tray variants; a hidden "Gallery" page (debug builds) that renders every control in
every state and theme.
**Acceptance.** Gallery screenshots in dark, light and high contrast at 100/150/200% scaling are published by CI (A19); contrast of every text/background pair
is at least 4.5:1 (an automated check over the tokens fails the build otherwise); no hard-coded colours outside `Tokens.xaml`.

### A2. App shell, navigation and first-run onboarding
**Mission.** The frame of the app and the first five minutes.
**Owns.** `src/Remotely.App/Shell/**`, `src/Remotely.App/Views/Onboarding/**`, `MainWindow*`, `App.xaml.cs`.
**Deliverables.** Title bar integrated with the nav pane (custom drag region, Mica backdrop where supported, solid fallback), window size/position memory,
single-instance app (second launch focuses the first and forwards `--invite`), tray icon with Share on/off and Open, close-to-tray option, start-with-Windows
option, first-run flow (name this PC, choose "share on my network", "connect to a server" with invite paste, finish) that can be skipped, keyboard shortcuts
(Ctrl+1/2/3 pages, F1 help, Ctrl+, settings), command-line options documented (`--invite`, `--pair`, `--minimized`, `--connect-token`).
**Acceptance.** UI tests (A19) walk the first-run flow; second instance forwards its arguments (test); window placement restored on a monitor that was removed
(falls back on-screen); crash log written for unhandled exceptions (exists today, extend with a user-visible "Something went wrong" dialog offering
"Copy details").

### A3. Share this PC (host experience)
**Mission.** Make being controlled safe, obvious and effortless.
**Owns.** `src/Remotely.App/Views/Share/**`, `ShareService.cs`, `src/Remotely.Core/Lan/LanHost*.cs` (UI-facing parts only; crypto belongs to A9).
**Deliverables.** Share card redesign: big token, expiry ring, copy, regenerate, QR code of the token for a phone (shown only when it helps), list of this PC's
addresses, state machine (Off, Ready, Connecting, In session, Error) with plain-language text; **consent prompt** option ("Ask me before someone connects",
default on for the first connection from a new PC, with Allow once / Always allow this PC / Deny); in-session **banner window** that always shows who is
connected, since when, with "Stop sharing" and "Pause control" (view only); notification toast on connect and disconnect; optional PIN for unattended
reconnect of known PCs without a new token ("remembered PCs", revocable list); session history.
**Acceptance.** Unit tests for the state machine and consent rules; selftest extended: consent denied means no frame; "Always allow" lets a second connect without
a new token only from the same device key; banner window opens during a selftest session (UI test).

### A4. Connect to a PC (controller experience)
**Mission.** From launch to a live screen in under ten seconds with one field.
**Owns.** `src/Remotely.App/Views/Connect/**`, `src/Remotely.Core/Lan/LanConnector.cs`, `LanDiscovery.cs`.
**Deliverables.** Token box with live validation and clear errors (already in `LanToken.TryParse`; add typo hints such as "0 looks like O, tokens never contain 0");
recent PCs (name, last used, one click to reconnect when the PC offers remembered access from A3); nearby PCs list (PCs currently sharing on the network show their
name and a lock; typing the token completes the connection); address fallback with history; progress with cancel; connection diagnosis page when it fails
(checks the network adapter, same subnet, firewall rule, UDP reply, TCP reach, and says which step failed); address book integration (A14).
**Acceptance.** Discovery tests (extend `LanTests`) for multiple PCs answering, IPv6-only networks reported clearly, and cancel; UI test: wrong token shows the
friendly message; diagnosis page reports "firewall" when the rule is removed on the CI runner.

### A5. Remote session window
**Mission.** The screen you work in must feel native.
**Owns.** `src/Remotely.App/Session/**` (move `SessionWindow*` and `RemoteScreen.cs` here).
**Deliverables.** Floating, auto-hiding toolbar (Windows 11 style) with: monitor picker, fit / 100% / fill scaling, full screen (all monitors optional), zoom with
Ctrl+wheel in 100% mode, pan, session stats overlay (fps, latency, bandwidth, codec), keyboard capture toggle with a visible indicator, send Ctrl+Alt+Del,
Win key and Alt+Tab pass-through option, clipboard toggle, file panel (A8), end session; reconnect overlay with automatic retry and a countdown; view-only mode;
remote cursor drawn locally for zero-lag feel; high-DPI correct pointer mapping for any mix of monitor scales; window title and taskbar thumbnail show the PC name.
**Acceptance.** Unit tests for the coordinate mapping (letterboxing, monitor offsets, negative coordinates, DPI scaling); UI test opens a session on the CI runner
and checks toolbar buttons exist and respond; stats overlay values are non-zero during the selftest.

### A6. Capture and streaming engine
**Mission.** Lower latency and bandwidth without losing correctness.
**Owns.** `src/Remotely.HostWindows/Helper/DesktopCapturer.cs` (and new files in `HostWindows`), `src/Remotely.HostCore/Frame*.cs`, `ViewerCore/ViewerSession.cs` frame loops.
**Deliverables.** Desktop Duplication API capture (DXGI) with GDI fallback, per-monitor capture and monitor list in the hello message, dirty-region frames
(tile diff, only changed tiles encoded), adaptive quality from measured round-trip and bandwidth (target 30 fps on LAN, degrade gracefully), cursor shape sent
separately from pixels, optional hardware H.264 through Media Foundation **only if** a measurement on the CI runners shows it beats tiled JPEG on both x64 and
ARM64 (otherwise tiled JPEG stays; the decision and numbers are written to `docs/performance.md`), protocol version bump with fallback to today's full-frame JPEG
for older peers.
**Acceptance.** Benchmarks in CI (A20) print fps, bytes per frame, capture ms and encode ms for an idle desktop and for a moving window; the new path is not slower
than the old one on either architecture; old and new peers interoperate (test with a fake host and viewer of the previous protocol version).

### A7. Input, keyboard layouts, IME and clipboard fidelity
**Mission.** Typing and pointing behave exactly as on the local machine.
**Owns.** `src/Remotely.HostWindows/Helper/InputInjector.cs`, `ClipboardAccess.cs`, `src/Remotely.HostCore/InputEvent.cs`, `RemoteScreen` key handling (coordinate with A5).
**Deliverables.** Send text by Unicode (`SendInput` with `KEYEVENTF_UNICODE`) when the layouts differ, scan-code path for games and shortcuts, dead keys and AltGr,
IME composition passes through, sticky-key release on focus loss and on disconnect (host-side watchdog, already partly present), touch and pen events,
high-resolution wheel, horizontal scroll, mouse buttons 4 and 5, clipboard of text, HTML, images (PNG) and file lists with size limits and a confirmation for
large transfers, clipboard loop prevention, secure-desktop input when the host service is used.
**Acceptance.** Unit tests for encoding/decoding of new events and limits; CI integration tests inject keystrokes into a Notepad instance on the runner and read the
text back (Latin, accented characters, AltGr combinations where the runner layout allows; the report lists which layouts were actually exercised).

### A8. File transfer
**Mission.** Dragging a file in or out is reliable and visible.
**Owns.** `src/Remotely.HostCore/FileTransfer.cs`, `ViewerCore` file API, `src/Remotely.App/Session/Files/**`.
**Deliverables.** Transfer queue with progress, speed, cancel, retry and resume (offset negotiation), drag and drop in both directions, folder transfer (zip on the fly
with traversal protection), per-transfer integrity hash, receive folder opener and a setting for its location, size and total-quota limits, host-side confirmation
for incoming files when consent mode is on, clear errors (disk full, name clash, access denied).
**Acceptance.** Existing path-traversal and partial-file tests kept; new tests: resume after a dropped link, 2 GB sparse file streaming memory stays flat, folder with
nested names and Unicode, hash mismatch is detected, cancel removes the partial file.

### A9. Security and cryptography hardening
**Mission.** A written threat model and code that matches it.
**Owns.** `src/Remotely.Core/Crypto/**`, `src/Remotely.Core/Lan/LanToken.cs`, `LanHandshake.cs`, `docs/security.md`, `tests/Remotely.Tests/Crypto*`.
**Deliverables.** Threat model (who can see what: LAN attacker, malicious server, stolen device key, shoulder-surfed token, malicious viewer) with each threat mapped to a
test or an explicit accepted risk; evaluate replacing the token handshake's PBKDF2-plus-ECDH with a real password-authenticated key exchange (SPAKE2 or OPAQUE)
using a vetted implementation, and implement it only if a licence-compatible, maintained library exists for .NET (if not, keep the current design and document the
limits as already stated); constant-time comparisons, key zeroisation, replay and reorder tests, fuzzing of every parser (invite, framing, input, file messages,
hello) with SharpFuzz or a property-based harness run for a fixed budget in CI; dependency vulnerability scan (`dotnet list package --vulnerable`) as a CI gate;
secrets never logged (test greps logs); audit log format for connection events; responsible-disclosure policy `SECURITY.md`.
**Acceptance.** All fuzz targets run N minutes in CI with no crash; the vulnerability gate passes; the threat model is reviewed by A22 and every "mitigated" row has a
test name next to it.

### A10. Server core
**Mission.** A server an operator can trust and a UI can drive.
**Owns.** `src/Remotely.Server/**`, `src/Remotely.Core/Admin/**`, `tests/Remotely.Tests/Server*`.
**Deliverables.** Admin API v1 (authenticated: loopback plus a local admin token stored with a restrictive ACL, rotatable) covering: status (version, uptime,
address, port, certificate pin and expiry, online counts), invites (create with lifetime and uses, list, revoke), devices (list, rename, revoke, block), audit log
(who connected to whom, when, result; ring-buffer on disk with size cap), settings (address, port, limits) with validation and hot reload where safe, backup and
restore of the data directory, certificate renewal and pin-change warning, structured logs with rotation, `/healthz` and a metrics endpoint (local only), rate
limiting on pairing and connect attempts per source address, graceful shutdown, data-file schema versions and migrations, IPv6 listening.
**Acceptance.** Every endpoint has tests against the real server over real sockets; unauthorised admin calls are refused (test); upgrade test: data written by the
0.3.0 and 0.4.0 servers loads in the new one; 24-hour soak is A20's job, this agent supplies the soak harness hooks.

### A11. Server Manager app (WinUI 3)
**Mission.** The server's face: install, see status, invite people, manage devices.
**Owns.** new `src/Remotely.ServerManager/**` (WinUI 3, same theme package as the app via a shared project `Remotely.UI` that A1 creates).
**Deliverables.** Dashboard (server running or stopped, address and port, reachability verdict, certificate pin and expiry, devices online, recent activity),
**Invite** page (create invite with lifetime and uses, big copy button, QR code, history with revoke), **Devices** page (search, online state, last seen, rename,
revoke, block, per-device audit), **Activity** page (connection log with filters and export CSV), **Settings** page (address, port, limits, start with Windows,
update channel), **Service** controls (start, stop, restart, install, uninstall with UAC elevation through a small elevated helper, never running the UI as admin),
tray icon with status colour, notification when a new device pairs, in-app **Backup and restore**, "Copy server info" button for support. Talks to the server
only through `IServerAdmin`.
**Acceptance.** Builds on x64 and ARM64; CI starts the real server, launches the Manager with `--selftest` that creates an invite, pairs a probe device, lists it,
revokes it and checks the server refuses it; screenshots of every page in light and dark are published.

### A12. Server setup, reachability and certificate experience
**Mission.** "Make my server reachable from outside" without a networking lesson.
**Owns.** `src/Remotely.ServerManager/Setup/**`, `src/Remotely.Server/Network/**`, `installer/Remotely-Server.iss` (wizard parts).
**Deliverables.** First-run wizard in the Manager: choose address (detects local addresses, optional public name), checks port availability, adds the firewall rule
(elevated helper), tests reachability from the inside, explains router forwarding with the exact values to enter and a "test from outside" step that uses the
user's own phone (it shows a short URL-free instruction and a local check page served by the server on the chosen port; no third-party service is called);
optional UPnP/NAT-PMP port mapping behind an explicit switch, using a vetted library, default off; dynamic DNS hint text only; certificate: self-signed with pin
as today, show the pin and fingerprint, renewal before expiry, optional import of the user's own certificate (PFX) with clear validation errors.
**Acceptance.** Wizard unit tests with fake network probes; UPnP code behind an interface with a fake in tests (real routers cannot be tested in CI, so this is
reported as untested on real hardware); PFX import tests with valid, expired and wrong-password files.

## Wave 2

### A13. Host service integration and unattended access
**Mission.** The app and the Windows service feel like one product.
**Owns.** `src/Remotely.Host/**` (service, helper), the app's `Settings/Unattended` page.
**Deliverables.** Unattended-access page in the app that shows the service state and offers install, start, stop, uninstall (elevated helper, never a prompt loop);
lock-screen and UAC session support explained in plain text with a live status line; service recovery settings; the app detects the service and offers
"use the service for same-network sharing too" so token sessions can reach the lock screen (host service listens for LAN tokens when enabled; this needs a
contract with A3: the token is created by the app and handed to the service through a local named pipe with an ACL for the signed-in user only); Ctrl+Alt+Del policy
switch; power requests so a sharing PC does not sleep during a session; Windows Hello or password-less sign-in caveats documented.
**Acceptance.** CI installs the service, enables LAN mode through the pipe, connects with a token from the app on the same runner, and captures the Winlogon
desktop on the x64 runner (as the existing lock-screen check does); named-pipe ACL test refuses another user (test with a second local user if the runner allows;
otherwise reported untested).

### A14. Settings, profiles and address book
**Mission.** Everything configurable lives in one tidy place and survives upgrades.
**Owns.** `src/Remotely.App/Views/Settings/**`, `src/Remotely.Core/Storage/**`.
**Deliverables.** Settings pages (General, Sharing, Connection, Security, Appearance, Advanced, About) with search; address book (groups, favourites, notes, colour tag,
import and export as JSON), settings export and import, reset to defaults, settings migration tests from the 0.3.0 and 0.4.0 `viewer.json`, policy overrides read
from `HKLM\Software\Policies\Remotely` for managed PCs (disable server mode, force consent, disable file transfer) with an ADMX template in the repo.
**Acceptance.** Migration tests; ADMX validated by loading it with the Windows policy tooling on the CI runner if available (otherwise XML-schema checked and
reported as not loaded in Group Policy); every setting has a default, a test, and a help string.

### A15. Localisation and accessibility
**Mission.** Usable by everyone, in more than one language.
**Owns.** `src/Remotely.App/Strings/**`, `src/Remotely.ServerManager/Strings/**`, accessibility fixes across XAML (coordinated through A1 for controls).
**Deliverables.** All strings in `.resw`, language picker, pseudo-localisation build to catch truncation, right-to-left layout test, languages: English plus those Burim
chooses (**decision needed**; machine translation is flagged and must be reviewed by a speaker before release), Narrator names and landmarks on every control, full
keyboard navigation with visible focus, high-contrast pass, text scaling to 200%, reduced-motion respect, `AutomationProperties` coverage report.
**Acceptance.** Automated UIA scan (A19) finds no unnamed interactive element; pseudo-localised screenshots show no clipped text; every key exists in every language file
(test).

### A16. macOS parity (Swift) (runs last, after the Windows work is merged and green)
**Mission.** The Mac apps get the same-network token mode and the polish pass.
**Owns.** `mac/**`.
**Deliverables.** Swift implementation of the token handshake (`RMTL1`), discovery and listener with test vectors generated by the .NET implementation (known-answer
tests as done for the server protocol), Mac "Share this Mac" and "Connect with a token" screens in the Portico palette, session window parity (scaling, toolbar,
clipboard), notarisation-ready build settings (actual notarisation needs Burim's Apple developer account: **decision needed**).
**Acceptance.** Swift tests with .NET-generated vectors; CI interop: Swift controller to .NET host and .NET controller to Swift host with a token, on macOS and Windows
runners as the existing server interop does. Honest limit to document: macOS capture permissions cannot be granted in CI, so the Mac host side uses the fake frame source there.

### A17. Installers and updater
**Mission.** One installer, no questions, safe upgrades.
**Owns.** `installer/**`, `src/Remotely.App/Update/**`, `src/Remotely.ServerManager/Update/**`, `build/**`.
**Deliverables.** Unified client installer (app, optional host service, firewall rules, start menu, optional start with Windows, per-user or all-users), server
installer that installs the Server Manager plus the service, in-place upgrade and rollback, **update check** against the project's GitHub releases (optional, off by
default, notify only; downloads verify a published SHA-256 and, when signed, the Authenticode signature before offering to run), silent switches documented,
uninstall keeps data (existing rule), `winget` manifest and a `scoop` manifest in the repo, MSIX packaging evaluated and the decision written down (the app is
unpackaged today; MSIX would change firewall, service and file-access behaviour).
**Acceptance.** Existing installer smoke checks stay; new checks: upgrade from the previous release's installer keeps settings, address book and service; rollback test;
updater verifies a good and a tampered package against a local test feed.

### A18. Diagnostics and supportability
**Mission.** When something fails on a user's PC, the cause is findable without a debugger.
**Owns.** `src/Remotely.Core/Diagnostics/**`, `Help` pages for diagnostics in both apps.
**Deliverables.** Central logging (rolling files, levels, redaction of tokens, keys and invites, tested), "Export diagnostics" zip (versions, OS, network adapters, firewall
rule state, last N log lines, settings without secrets, last crash), in-app **Network test** (same checks as A4's diagnosis, plus server reachability and latency),
connection quality indicator, error code catalogue (`RMT-xxxx`) with a help page per code, local crash dumps option, no telemetry.
**Acceptance.** A test proves no secret ever appears in a diagnostics zip (plants canary secrets, greps); each error code in the catalogue is raised by at least one
test and has a doc entry (test cross-checks the two lists).

### A19. UI test automation and screenshot pipeline
**Mission.** Make the UI verifiable by me, from Linux, with evidence.
**Owns.** `tests/Remotely.UITests/**`, `.github/workflows/ui.yml` (with A0), `docs/screenshots/**`.
**Deliverables.** UI Automation (UIA, `System.Windows.Automation` or FlaUI under its licence check) test project that launches the apps on the Windows runners, drives
each page and flow, and fails on unexpected dialogs; screenshots of every page and state in dark, light, high contrast and 100/150/200% scaling, on x64 and ARM64;
a step that **commits the screenshots to a dedicated `ci-screenshots` branch** so they can be read through the GitHub API (the artifact download host is blocked from
the agent environment, which is why the images were never inspected so far); image-diff against approved baselines with a tolerance, baseline update by explicit
command only.
**Acceptance.** A deliberate one-pixel-visible change fails the diff in a test branch; screenshots appear on `ci-screenshots` after a run and A23 reviews them visually.

## Wave 3

### A20. Performance, reliability and soak lab
**Mission.** Numbers, not adjectives.
**Owns.** `tests/Remotely.Bench/**`, `.github/workflows/soak.yml`, `docs/performance.md`.
**Deliverables.** Latency (input to pixel) measured with a test pattern on the runner; fps and bandwidth for idle, scrolling, video-like content; memory and handle-leak
tracking over a 30-minute session and a nightly 6-hour soak; reconnect behaviour under packet loss and delay using a local TCP proxy that injects faults; server with
500 simulated devices and 50 simultaneous relayed sessions (fake hosts); results trended in the workflow summary; regression budgets that fail the build.
**Acceptance.** Budgets are written after the first measurement and then enforced; the report states the hardware limits of GitHub runners so nobody reads the numbers as
real-world latency over a home internet connection.

### A21. Documentation, help and website
**Mission.** A person who has never heard of Remotely can succeed alone.
**Owns.** `docs/**` (except generated screenshots and `security.md`), `README.md`, in-app Help pages, `LICENSE`, `THIRD-PARTY-NOTICES.md`.
**Deliverables.** README with a 60-second quick start and honest feature table; user guide (same network, server mode, Mac), administrator guide (server, backups,
upgrades, security recommendations, router forwarding), troubleshooting by error code, FAQ ("is it safe", "what can the server see"), architecture, protocol, security
(A9) linked; in-app Help pages with the same text; generated third-party notices (a build step lists NuGet licences and fails on an unapproved licence); changelog
following Keep a Changelog; a static project page under `docs/` ready for GitHub Pages.
**Acceptance.** A link checker runs in CI; every claim in the feature table points to a test or CI check; screenshots in the docs come from A19, not hand-made.

### A22. Quality reviewer 1 (code and security review of every wave)
**Mission.** An independent read of each merged wave, not anchored on the author's conclusions.
**Owns.** nothing; produces `docs/review-wave-N.md` and issues.
**Deliverables.** For each wave: review of the diff for correctness bugs, concurrency problems, resource leaks, error handling, API misuse, and spec deviations; reproduce
each claimed bug with a failing test before reporting it; verified findings only.
**Acceptance.** Every verified finding has a failing test or a precise trace; the integrator fixes or rejects each with a reason.

### A23. Quality reviewer 2 (UX and accessibility review)
**Mission.** Look at the screenshots and use the flows as a first-time user would.
**Owns.** nothing; produces `docs/ux-review-wave-N.md`.
**Deliverables.** Reads the `ci-screenshots` branch images, checks consistency, spacing, truncation, contrast, empty and error states, wording (plain language, no jargon),
flow length counts (clicks from launch to connected), and compares against the acceptance lists of A1 to A5 and A11.
**Acceptance.** A numbered issue list with the screenshot path for each; zero open "blocker" items before release.

### A24. Release gate and release engineering
**Mission.** Nothing ships unless the checklist is green.
**Owns.** `build/release/**`, `.github/workflows/release.yml`, `docs/release-checklist.md`.
**Deliverables.** One workflow that builds, tests, runs the UI tests, signs when a certificate secret is present (certificate purchase and storage are Burim's
decision: **needed**; without it installers are unsigned and SmartScreen warns, as documented today), produces installers for x64 and ARM64, the Mac `.dmg`, checksums,
SBOM (CycloneDX), release notes from the changelog, publishes a **draft** release; a checklist that must be ticked by a human before publishing (manual test on two real
PCs on one network, one real remote connection through a forwarded port, upgrade from the previous release).
**Acceptance.** A dry-run release from the working branch produces all files; the checklist document lists the manual steps that CI cannot do.

---

## What CI cannot prove (stays on the manual checklist)

- Two different PCs on one network (Windows Firewall prompt, Wi-Fi client isolation, VLANs).
- A connection through a real router with port forwarding, and UPnP on real routers.
- Real GPUs, multi-monitor setups, touch hardware, real keyboard layouts beyond what the runner has.
- How the screens look to a person (the screenshot review by A23 is the closest substitute; Burim's own eyes remain the final check).
- Mac screen-capture permission dialogs.

## Decisions (Burim, recorded)

1. **Languages:** English is the default, Albanian (`sq-AL`) the second. The first Albanian strings are machine-written and must be read by a fluent speaker before release;
   until then the release notes say so.
2. **Code signing:** installers stay unsigned for now. Burim will look for a free route (for example a free open-source signing programme); A24 keeps the signing hook
   switched off until a certificate exists. SmartScreen warnings stay documented.
3. **Consent:** yes. The first connection from a new PC must be approved on the sharing PC (Allow once / Always allow / Deny).
4. **Remembered PCs:** yes, after the sharer approves a PC once it may reconnect without a new token; revocable list.
5. **UPnP:** recommendation, accepted as the default plan: offer automatic router port mapping as an **opt-in switch, off by default**, in the Server Manager. Reason: UPnP
   opens a hole in the router without the owner seeing it and many routers implement it badly, so it should never be silent. When on, the Manager shows exactly which
   mapping it created and removes it when the server stops. Manual forwarding instructions stay the primary path.
6. **Update checks:** yes, opt-in and notify-only, against this repository's GitHub releases, verifying the published SHA-256 (A17).
7. **macOS:** in scope as the **last phase** (A16 runs after the Windows apps are finished, not in parallel).
