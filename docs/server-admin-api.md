# Remotely Server: admin API v1

Served on the **loopback admin port** only (default 8181; `Remotely:AdminPort`), on IPv4 `127.0.0.1` and, where the machine supports it, IPv6 `::1`.
Requests that arrive on the public port, from a non-loopback address, or with an `X-Forwarded-For` header get `404`.

The typed client is `Remotely.Core.Admin.ServerAdminClient` (implements `IServerAdmin`, plus restore, certificate renewal, token rotation, metrics):

```csharp
using var admin = ServerAdminClient.ForDataDirectory();          // reads admin.token on every call, default data directory
var status = await admin.GetStatusAsync();
```

## Authentication

Every `/admin/v1/*` call needs `Authorization: Bearer <token>`. The token is in `admin.token` in the data directory
(`%ProgramData%\Remotely Server`, or `~/.local/share/RemotelyServer`). Missing or wrong token: `401` and an `admin.unauthorized` audit entry (at most 10 per minute are written).

- Created by the server (or by `Remotely.Server.exe install`, run as administrator) on first start; 256 random bits.
- Unix: file mode 600. Windows: access list with inheritance removed: SYSTEM, Administrators, LocalService (the service account) and the account that created it. **The Windows list is only exercised by CI starting the real server; no test asserts it** (see "Not verified").
- Rotate with `POST /admin/v1/token/rotate`; the old token stops working at once, the file is replaced in one step and keeps its access list.
- If the file cannot be read or written the server still runs; `/admin/v1` answers `503` and the log says why.
- Memory holds only a SHA-256 of the token; comparison is constant time.

**Older endpoints** `POST /admin/pair-codes?uses=&minutes=`, `GET /admin/devices`, `DELETE /admin/devices/{id}` are unchanged and need no token (the installers and CI call them). Set `Remotely:LegacyAdminRequiresToken=true` to demand the token there too; the command-line commands send the token when the account can read it.
Anyone who can open a TCP connection to 127.0.0.1 on the server PC can use these, so the token protects `/admin/v1` but not the older invite creation unless that option is on.

## Errors

JSON `{"error": "plain sentence"}` with 400 (invalid), 401, 404, 409 (cannot do that now), 413, 503.

## Endpoints

| Call | Body / query | Result |
|---|---|---|
| `GET status` | | `ServerStatus`: version, startedUtc, address (saved, else this PC's LAN address), running port, tlsEnabled, certificatePin, certificateExpiresUtc, devicesTotal, devicesOnline, sessionsActive. With TLS off: pin `""`, expiry `0001-01-01`. |
| `GET metrics` | | `ServerMetricsSnapshot`: counters since start (connections accepted/rate-limited, pairings, sessions, frames and bytes relayed, unauthorised admin calls) and process figures (working set, managed heap, threads). |
| `POST invites` | `{lifetimeMinutes 1..1440, uses 1..20}` | `InviteInfo` (id, inviteText `RMT1-...`, expiresUtc, usesAllowed, usesLeft). `409` if the address is unknown and cannot be guessed. |
| `GET invites` | | active invites (not expired, uses left). Held in memory: a restart forgets them. |
| `DELETE invites/{id}` | | 204, or 404 |
| `GET devices` | | `AdminDevice[]` sorted by name. `lastSeenUtc` of an online device is when it connected. |
| `PUT devices/{id}/name` | `{name}` | 204. Control characters removed, 64 characters at most; empty is 400. |
| `PUT devices/{id}/blocked` | `{blocked}` | 204. Blocking disconnects the device and ends its sessions; it sees itself as unpaired, and pairing is refused with "blocked" whatever the code. Blocked devices are hidden from other devices' lists. |
| `DELETE devices/{id}` | | 204. Forgets and disconnects; the device can join again with a new invite. |
| `GET audit?since=&max=` | ISO time; max 1..5000 (default 500) | `AuditEntry[]`, newest first, only entries after `since`. |
| `GET settings` / `PUT settings` | `ServerSettings` | `PUT` returns `{settings, restartRequired, restartRequiredFor}`. |
| `GET backup` | | zip |
| `POST restore` | zip, up to 32 MiB | `RestoreResult` |
| `POST certificate/renew` | `{newKey, confirmPinChange}` | `CertificateRenewal` |
| `POST token/rotate` | | `{token}` |
| `GET /healthz` (no token) | | `ok`, or 503 `stopping`; also on the public port |

### Audit kinds
`pair` (ok, failed, refused, rate-limited), `auth` (refused: blocked device), `connect` (a viewer asked for a host: ok or failed with the reason), `session.end`, `invite.create`, `invite.revoke`, `device.rename|revoke|block|unblock`, `settings`, `backup`, `restore`, `cert.renew`, `token.rotate`, `admin.unauthorized`.
Stored in `audit.log` and `audit.old.log` in the data directory as JSON lines; together they never exceed `Remotely:AuditMaxBytes` (default 2 MiB, minimum 64 KiB). Oldest entries are dropped first. No keys or tokens are written.

### Settings
`address` (name or address devices use; saved in `address.txt`, the file `set-address` writes), `port`, `maxDevices` (1..10000, default 1000), `maxSessionsPerDevice` (1..64, default 8), `requireTls`.
Saved in `settings.json` (schema 1); saved values win over `appsettings.json`.
**Applied at once:** address, maxDevices, maxSessionsPerDevice. **Need a restart:** port, requireTls (Kestrel binds when it starts). A port must be free (checked) and different from the admin port.

### Backup and restore
A backup zip holds `manifest.json`, `devices.json`, and when present `settings.json`, `address.txt`, `server.pfx`. **It contains the TLS private key; keep it as private as the data directory.** The token and logs are not included.
`ServerAdminClient.CreateBackupAsync(folder)` downloads and writes the file on the caller's side, so the folder need not be writable by the service account.
Restore validates everything first (entry names, sizes, schema versions, certificate) and refuses with 400 otherwise, changing nothing. Before applying it saves the current state to `backups/before-restore-*.zip`. Devices not in the backup, or blocked in it, are disconnected. A certificate in the backup takes effect for new connections at once; `pinChanged` true with a `warning` means devices that pinned the old key refuse this server until they join again.

### Certificate renewal
`newKey:false` (default): same key, new validity period; **the pin does not change**, devices keep working. `newKey:true` changes the pin and every device must join again, so it needs `confirmPinChange:true` (else 409). The new certificate is used for new connections immediately; the previous file is kept as `server.pfx.prev`. Note the certificate is valid for 10 years from first run; this server does not renew by itself.

## Limits per source address
`Remotely:Limits:ConnectAttemptsPerMinute` (120), `PairAttemptsPerMinute` (20, right or wrong), `SessionRequestsPerMinute` (60, per device); `0` switches one off (needed to run a load test from a single machine). Token-bucket: a burst of that size, refilled evenly over a minute. IPv6 sources count as their /64; IPv4-mapped addresses as IPv4. Beyond the limit: HTTP 429 on connect, a clear message to the device otherwise. The existing lockout (five wrong codes, 15 minutes) still applies. Behind a NAT, all devices share one source address.

## Data files and schema versions
| File | Schema | Notes |
|---|---|---|
| `devices.json` | 2 | `{schemaVersion, devices[]}`. Schema 1 (servers up to 0.4.0, a bare array) is read, rewritten as 2, and kept as `devices.json.v1.bak`. A newer or damaged file stops the server with a plain message and is never overwritten. |
| `settings.json` | 1 | as above |
| `audit*.log`, `logs/` | | JSON lines |
| `server.pfx`, `admin.token`, `address.txt` | | plain |

## Logs
`logs/server.log` in the data directory, one JSON object per line (`time`, `level`, `category`, `message`, `exception`). Rolled to `server-yyyyMMdd-HHmmss.log` at `Remotely:LogMaxFileMegabytes` (10) or at a new UTC day; `Remotely:LogFilesKept` (14) rolled files kept. `Remotely:FileLogging=false` turns it off. Console logging is unchanged. A full queue drops lines instead of slowing the server.

## Shutdown
On stop the server refuses new connections, closes every device connection with a "server is stopping" close, cuts off any that stay after 2 seconds, and writes last-seen times and the audit log to disk. `Remotely:ShutdownSeconds` (10) bounds the host's wait.

## Soak harness hooks
`tests/Remotely.Tests/ServerSoakHarness.cs`: `FakeFleet.StartAsync(server, hosts, viewers)` pairs and connects fake devices; `RunSessionsAsync` runs encrypted echo sessions through the relay; `ChurnAsync` reconnects some; `ServerAdminClient.GetMetricsAsync()` supplies counters and process memory/threads. Start the server with `TestServer.NoLimits`.

## Not verified
Windows access list of `admin.token` (compiles on Linux; only started, not inspected, by CI); IPv6 listening depends on the machine (the test returns without checking where `::1` is unavailable, as on the machine this was developed on); behaviour of a non-elevated Server Manager reading the token on a real install.
