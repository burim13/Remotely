# Troubleshooting and diagnostics

When something fails, work down this page. Every error that has a code (`RMT-1006` and so on) has its own page in [errors](errors/README.md) that says what it means and what to do.

## Where things are

| What | Where |
|---|---|
| Remotely app: rolling log | `%LOCALAPPDATA%\Remotely\logs\app-yyyyMMdd-NNN.log` |
| Remotely app: start-up and crash files (older, still written) | `%LOCALAPPDATA%\Remotely\logs\app-startup.log`, `app-crash.log` |
| Host program (service or user mode): rolling log | `%ProgramData%\Remotely\logs\host-yyyyMMdd-NNN.log` (user mode: `%LOCALAPPDATA%\Remotely\logs`) |
| Server Manager: rolling log and crash file | `%LOCALAPPDATA%\Remotely Server Manager\logs\manager-yyyyMMdd-NNN.log`, `manager-crash.log` |
| Server: structured log (JSON lines) | `%ProgramData%\Remotely Server\logs\server.log` |
| Manager helper (the administrator step) | `%ProgramData%\Remotely Server\manager-helper.log` |
| Diagnostics switches | `diagnostics.json` next to `viewer.json` (app) or `manager.json` (Manager) |
| Crash dumps (only if switched on) | `%LOCALAPPDATA%\Remotely\crashdumps` and `%LOCALAPPDATA%\Remotely Server Manager\crashdumps` |

A log file is closed and a new one started when it passes 2 MB or the day changes; the newest 10 files are kept. Times are UTC. Lines look like
`2026-10-17T09:30:00.123Z INFO  [source] message`; the stack trace of an exception follows on lines that start with four spaces.

## Open the Diagnostics page

In the Remotely app: **Diagnostics** in the navigation pane (or `Remotely.exe --show DiagnosticsPage`). In the Server Manager: **Diagnostics**.

- **Detail in the log**: Normal (default) writes information, warnings and errors. Everything (debug) adds detail. The change applies at once.
- **Save a crash dump when the program crashes**: off by default. A dump is a copy of the program's memory and can contain keys and tokens. It is written to a folder on this PC (the newest 3 are kept) and is never put into the export or sent anywhere. It uses `MiniDumpWriteDump` from `dbghelp.dll` and has been compiled by CI but not run on a Windows PC by its author. The program's single crash hook (`LogCrash`) is also called for some errors it handles itself, so a dump can be written for those too.
- **Export diagnostics**: makes a zip you can read and send to whoever helps you.
- **Network test**: see below.

## What the export contains, and what it does not

Contains: program, version and .NET version, operating system, every network adapter with its status and addresses, the state of the Remotely firewall rules (`Remotely same-network (TCP)`, `Remotely same-network (UDP)` for the app, `Remotely Server` for the Manager) and of the Remotely services (`Remotely.Host`, `Remotely.Server`), settings that are on a short allow-list (theme, window position, "start with Windows" and similar; any other setting is listed by name with its value left out), the last 500 lines of each log, and the last lines of the crash files.

Does not contain: private keys, device keys, tokens, invites, pairing codes, the server's admin token, crash dumps, transferred files, screen or clipboard content.

How that is enforced: the log writer and the export both pass text through `Redactor` (`src/Remotely.Core/Diagnostics/Redactor.cs`). It removes invites (`RMT1-...`), same-network tokens (`K7P4-4NX2-QMH9`, any case), bearer tokens, anything that follows words such as token, secret, password, pin, key, invite or "pairing code", any run of 32 or more key-like characters (keys, hashes, the admin token), and any value a program registered as secret. Limits, stated so nobody relies on more than is true:

- A secret with none of those shapes that nobody registered (for example a bare 8-character pairing code in the middle of a sentence) is not recognised.
- A lowercase token whose 12 characters are all hex digits (about 1 in 20000) is kept because it looks like a short device id (`1a2b-3c4d-5e6f`), which is public and which logs rely on.
- GUIDs are kept. 64-character hex values are removed, including the server's public key pin and full device ids.
- IP addresses, PC names and user names inside paths are **not** removed. Read the zip before you share it.
- The test `No_planted_secret_reaches_the_diagnostics_zip_whatever_file_it_started_in` plants such secrets in a log, a crash file, a settings file, adapter, firewall and service text and checks the zip. It proves this for those inputs, not for every possible input.

## Network test

Diagnostics, **Network test** (or `--show NetworkTestPage`). It checks, in order: a network adapter with an IPv4 address; whether a typed address is on the same network; whether PCs answer the UDP search (port 47811); whether the TCP port (47810) accepts a connection; and, if a server is configured, whether the server's address accepts a TCP connection and how long that took (three tries; best, average and worst). The first four are `LanDiagnosis`, the same code as the Connection check. With no address or token typed, a silent network is reported as a note, not a failure. The server check sends only the TCP handshake, no login and no identity. The messages of the checks are English in both languages (they come from the shared code).

## Common situations

- **"No PC on this network is showing a token"** (RMT-1006): the other PC is not sharing, is on another network, or the network blocks broadcasts. Type the other PC's address next to the token.
- **"Could not reach the PC"** (RMT-1007): usually the Windows Firewall rule on the other PC. Check `Remotely same-network (TCP)` and `(UDP)`: the diagnostics export (firewall.txt) shows whether they exist; they are created by the installer for all-users installs only.
- **"The server could not be reached"** (RMT-3012): run the Network test; check the address, that the service runs, and the router and firewall for the server port.
- **Pairing fails** (RMT-3006 to RMT-3009): see the pages; codes last 15 minutes by default and repeated wrong tries lock an address out.

## Where error codes are shown today

The catalogue (`ErrorCatalogue` in `Remotely.Core.Diagnostics`) can name any exception, string or exit code. The Network test and the Diagnostics pages show codes. Other screens show a code only when their code calls the catalogue; **no other screen does yet** (this was not changed because the screens belong to other parts of the program). `ErrorCatalogue.Label(exception)` returns the text to append.

## Connection quality indicator

`ConnectionQualityMonitor` (`Remotely.Core.Diagnostics`) is the logic only: the session window feeds it round-trip times, received frames and stalls, and reads a `QualitySnapshot` (Good, Fair, Poor, Lost, Unknown, with a reason; strings `Diag_Quality_Level_*` and `Diag_Quality_Reason_*`). The limits (Good up to 100 ms median round trip and 40 ms jitter, Fair up to 250 ms and 100 ms, Lost after 5 s of silence, a better level shown only after holding 3 s) are starting values chosen by the author, not measured on real networks. Nothing shows it in the session window yet.

## No telemetry

Nothing in this feature contacts any server. The only network traffic it causes is the Network test, and only when someone presses Run.
