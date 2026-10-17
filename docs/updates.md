# Installers, upgrades and the update check

This page is the reference for how Remotely is installed, upgraded and updated. Everything below was written against the files in this repository; the section
"What is proven, and by what" says which statements a CI run or a test checks and which are not checked at all.

## The installers

Three installers are built per release (`installer/*.iss`, Inno Setup 6, compiled by the `installers` job or by `build\publish.ps1`):

| File | For | Contains |
|---|---|---|
| `Remotely-Setup-<version>-x64.exe` | Windows 11 x64 | the Remotely app, the Remotely Host (optional service), firewall rules for same-network sharing |
| `Remotely-Setup-<version>-arm64.exe` | Windows 11 ARM64 | the same, built natively for ARM64 |
| `Remotely-Server-Setup-<version>-x64.exe` | the always-on PC or VM | the server (as the `Remotely.Server` service) and the **Remotely Server Manager** app |

They are **unsigned for now** (decision recorded in `docs/agent-specs.md`): Windows SmartScreen warns the first time (More info, Run anyway). The update check below
therefore verifies a published SHA-256, and checks an Authenticode signature only when the file has one.

### Client installer: no questions for same-network use

1. Windows asks once whether to install **for all users** (administrator: the Host can run as a service, firewall rules are added) or **only for me** (no administrator).
2. **Additional tasks** has two boxes, both off by default: *Start Remotely when I sign in to Windows* and *Connect this PC to my own Remotely server (I have an invite)*.
   Pressing Next is enough for same-network sharing. Only the second box leads to one more page, where the invite can be pasted (it is optional there too).
3. Install. The Host service is started only when the PC was connected to a server (invite given now, or settings left by an earlier install).

*Start with Windows* writes the same registry value as the app's own **Start with Windows** setting: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value
`Remotely`, data `"<install folder>\Remotely.exe" --minimized` (`StartupCommand.Build` in `src/Remotely.App/Shell/Core/CommandLine.cs`). It is written as the person who
started Setup, even when Setup runs elevated. A later upgrade does not remove it. Uninstall removes it when it points at the uninstalled copy. With an all-users install
that was started by a different administrator account than the user, the uninstaller can only reach the registry of the account that runs it; this case is not tested.

### Silent installation

```
Remotely-Setup-<version>-x64.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART [switches]
```

| Switch | Meaning |
|---|---|
| `/SILENT` | progress window but no questions; `/VERYSILENT` shows nothing |
| `/SUPPRESSMSGBOXES` | never wait on a message box (use with both) |
| `/NORESTART` | never restart Windows |
| `/LOG="C:\path\setup.log"` | write Inno's log (also useful to see why a rollback happened) |
| `/CURRENTUSER` or `/ALLUSERS` | per-user (no administrator, no service, no firewall rules) or all users (needs administrator) |
| `/TASKS=startup` | tick *Start Remotely when I sign in*. `/TASKS=startup,connect` also ticks the invite page (not needed silently) |
| `/COMPONENTS=viewer` | install the app without the Host (default: both) |
| `/Invite=RMT1-...` and `/Name="Dad's PC"` | connect this PC to a server with an invite, and name it |
| `/Server=wss://host:8443/ws /Pin=<64 hex> /Code=XXXX-XXXX /Trust="<viewer id> <viewer id>"` | the long form of the invite |
| `/Insecure=1` | only for local tests over `ws://` |
| `/SimulateFailure=1` | makes the install fail at its last step on purpose, to test the rollback. Never use it otherwise |

The server installer takes `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=...` and `/Address=myhome.example.com` (optional, the name devices use to reach the server) and
`/SimulateFailure=1`. The standard Inno switches are documented in Inno Setup's "Setup Command Line Parameters" page. The installer returns exit code 0 on success and a
non-zero code when it fails or is rolled back.

### Server installer: the service and the Server Manager

It installs `Remotely.Server.exe` as the `Remotely.Server` service (TCP 8443 in Windows Firewall, runs as LocalService) and the Server Manager into the `Manager` folder
beside it (`C:\Program Files\Remotely Server\Manager\Remotely.ServerManager.exe`), with a Start menu entry and an *Open Remotely Server Manager* box at the end. That folder
layout is what the Manager's helper expects when it looks for the server (`ServerExeLocator` in `src/Remotely.Core/ServerManager/ServiceControl.cs`).

**Architectures.** The server is built for x64 only; `ArchitecturesAllowed=x64compatible` also lets it install on Windows 11 ARM64, where it runs through Windows' x64
emulation (not tested on ARM64 hardware beyond the CI runner, see below). The Manager *is* built for ARM64 too (the `app` job publishes `Remotely.ServerManager-win-arm64`),
but the server installer carries only the x64 build, so on ARM64 Windows the Manager also runs emulated. An ARM64 server installer would need an ARM64 server build, which
nothing produces today.

### Upgrade in place, rollback, uninstall

* Running a newer installer over an installed one (same `AppId`) is an upgrade in place of the program files. Settings, the address book, the device key, the server key
  and the device list live outside the install folder and are never touched. The Host service is stopped, the files replaced, the service started again with its settings.
* **Rollback.** Before anything is replaced, the installed folder is copied to Setup's temporary folder. After the files are in place, Setup starts the Host (or the server
  service) and checks that it reaches the running state (30 seconds). If that fails, Setup stops with an error and a non-zero exit code, puts the old files back
  (`robocopy /MIR`, so files the new version added are removed again), restores the old version number shown in *Apps & features*, and starts the old service. A fresh
  install that fails removes the service it just registered. An expired invite alone does **not** roll the installation back: the app is installed and Settings takes a new one.
  The logic is in `installer/UpgradeSafety.isi`.
* **Uninstall** removes the program files, the service, the firewall rules and the start-with-Windows value, and keeps all data (`%ProgramData%\Remotely`,
  `%LocalAppData%\Remotely`, `%ProgramData%\Remotely Server`).

## The update check (opt-in, notify only)

Both apps (the Remotely app and the Server Manager) have an **Updates** section. It is **off until the person turns it on**; a damaged or missing settings file also means off.

* When on, the app asks `https://api.github.com/repos/burim13/Remotely/releases` at most once a day (at start-up). **Check now** asks immediately, even while the switch is
  off, because it is an explicit click. Nothing else is contacted; there is no telemetry and no account.
* It only **tells**. A banner appears while an update is available; **Download** fetches the installer into `%LocalAppData%\Remotely\updates` (Server Manager:
  `%LocalAppData%\Remotely Server Manager\updates`); **Open the installer** is a separate click and Windows then asks for permission as usual. The app never starts an
  installer by itself and never installs silently.
* **A release is offered only if it publishes its checksums.** The release must carry `SHA256SUMS.txt` (lines `<64 hex>  <file name>`, as `sha256sum` writes them; the build
  writes it with `build\checksums.ps1`) with a line for the installer of this PC. A newer release without that file or line is reported as "not offered" and nothing is downloaded.
* **The download is verified.** The installer is hashed while it downloads; if the SHA-256 differs from the published one, the file is deleted and refused. Size limits apply
  (feed 2 MB, sums 64 KB, installer 400 MB). Download addresses must start with `https://github.com/burim13/Remotely/releases/download/`. Right before it is opened the file is
  hashed once more; if it changed since the check it is not opened.
* **Authenticode.** After the hash matches, Windows PowerShell's `Get-AuthenticodeSignature` is asked about the file. *Valid* passes; *NotSigned* passes (the installers are unsigned
  for now, and the screen says so); anything else, including a signature that is present but not trusted or that no longer matches the file, is refused and the file deleted.
  This means that, once a self-made certificate (`build\sign.ps1`) is used, the signature is "present but not trusted" on other PCs and such a package would be refused: keep
  the installers unsigned until there is a certificate that Windows trusts.
* Versions are compared as semantic versions; pre-releases are ignored. A version can be skipped (**Skip this version**); a newer one is offered again.

Wiring into the apps (the shell and the Settings page belong to other agents): in `Remotely.App`, call `AppUpdates.StartBackgroundCheck()` once after the main window exists, add
`<update:UpdateBanner />` at the top of the shell and `<update:UpdateSettingsSection />` to the Settings page (`xmlns:update="using:Remotely.App.Update"`). The Server Manager has the same
classes in `Remotely.ServerManager.Update` (`ManagerUpdates`). The words are resource keys `Update_*`; `UpdatePresentation.EnglishText` lists them with their English text and is used when
a key is missing from `Resources.resw`, so adding them to the resource files only translates them.

### Release checklist (manual steps)

1. Run the `build` workflow with `release_tag` (creates a draft release with the installers).
2. Attach `SHA256SUMS.txt` (artifact `Installer-sha256`) to the release. *The `release` job currently attaches `release/*.exe` only; see the request to the integrator.* Without it the update
   check will report the release as not offered.
3. Fill in the manifests: `powershell -File build\update-manifests.ps1 -Version <x.y.z> -SumsFile SHA256SUMS.txt`, then submit them (below).
4. Publish the draft release. Releases that are still drafts are not visible to the apps.

## winget and scoop (manual submission)

`packaging/winget/Burim13.Remotely`, `packaging/winget/Burim13.RemotelyServer` and `packaging/scoop/remotely.json` describe the installers. **Submitting them is a manual step** that
needs Burim's GitHub account: winget manifests go to a pull request against `microsoft/winget-pkgs`; the scoop manifest goes to a bucket (your own, or a pull request to an
existing one). Until `build\update-manifests.ps1` has filled in the version and the hashes, the hash fields hold the visible placeholder `REPLACE_WITH_SHA256_FROM_SHA256SUMS_TXT`
so they cannot be submitted by accident. The `installers` job proves the script fills every hash from the real `SHA256SUMS.txt`. **Not tested here:** `winget validate`, a real
`winget install`, `scoop install`. The package identifiers (`Burim13.Remotely`, `Burim13.RemotelyServer`) are proposals. The scoop manifest installs for the current user (`/CURRENTUSER`).

## MSIX: evaluated, not adopted for 1.0

Remotely is unpackaged today (Windows App SDK self-contained, `WindowsPackageType=None`). MSIX would change what the installer does in four ways. These points come from
Microsoft's documented behaviour of MSIX packages as the author knows it; **none of it was tested for this project**, and the decision below does not depend on testing it.

1. **Signing.** An MSIX package must be signed with a certificate the PC trusts, even for sideloading. The decision to keep the installers unsigned for now rules MSIX out until a certificate exists.
2. **The Host service.** The Host runs as a Windows service so that it survives sign-out and shows the lock screen. A packaged app can declare a service only with special capabilities and
   under restrictions; the Host's "per user or all users" choice and its `install` / `uninstall` commands would have to be redone.
3. **Files.** Packaged apps see a virtualized view of some user-data locations. Remotely's data (`%LocalAppData%\Remotely`, `%ProgramData%\Remotely`) is shared by the app, the Host service, the
   CLI and the installer-time tools; a different view for the app would break that sharing.
4. **Firewall.** Rules can be declared in the manifest, but the current rules are tied to the installed `Remotely.exe` path and managed by `netsh`; the package would need a different mechanism.

**Decision (A17):** do not ship MSIX in 1.0. Keep the Inno Setup installers, the winget and scoop manifests and the opt-in update check. Revisit only if a trusted code-signing certificate exists
**and** the Host service is split from the app so that the app could be packaged alone. If revisited, the first experiments are: a packaged app reading the data folders, and a packaged service
starting at boot.

## What is proven, and by what

CI (`build` workflow, Windows x64 and ARM64) checks, in `installer-smoke`: the server and client installers silent for all users and for the current user; the services, firewall rules,
Start menu entries; the installed app's same-network self-test; uninstall keeping data; the Server Manager installed beside the server and started (process alive only: it is **not** shown to
render); an upgrade from the **v0.2.0 release installers** (downloaded from GitHub and compared with the SHA-256 GitHub shows for them) to this build for both the client and the server,
keeping the device key, a planted address-book file and the server data; a failing upgrade (`/SimulateFailure=1`) that is rolled back; the start-with-Windows value. The `installers` job
runs the update tests on Windows with the real installers: SHA256SUMS.txt lists each installer with its real hash, a good copy passes the whole pipeline, a copy with one flipped bit is refused,
and the real PowerShell Authenticode check reports unsigned, signed and tampered files as described above. Linux tests (`tests/Remotely.Tests/Update*.cs`) run the checker and downloader against
a local HTTP feed that looks like the GitHub releases API: a good package, a tampered package, a wrong length, an oversize package, a release without a published hash, an address outside the
trusted prefix, plain http, server errors, the opt-in rules and the once-a-day rule.

**Not covered:** the WinUI update banner and Updates section (they compile in CI but nobody has seen them; the screenshot job does not open them until the shell and Settings host them);
a real run against `api.github.com` (the feed format was taken from the GitHub documentation and from the v0.2.0 release response, not from a published release that has `SHA256SUMS.txt`);
a signed installer; any ARM64 hardware other than the GitHub runner; the address book of the app itself (the test plants a file in the data folder to prove the installer does not touch it);
`winget` and `scoop` themselves.
