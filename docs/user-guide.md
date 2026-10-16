# Remotely: setting it up

## Quick start: two PCs on the same network (no server)

1. Install `Remotely-Setup-<version>-x64.exe` (or `-arm64`) on both PCs. Leave the invite question empty.
2. On the PC you want to control: open **Remotely**, turn on **Share this PC**. It shows a token like `K7P4-4NX2-QMH9`.
3. On the PC you sit at: open **Remotely**, type the token under **Connect to a PC**, press **Connect**. The other PC is found on the network by itself.

The token works once and changes after 10 minutes or after three wrong tries; the PC only answers PCs on its own subnet. If your network blocks discovery
(some Wi-Fi guest networks do), open "Cannot find it? Enter its address" and type the other PC's address. Limits of this mode: it runs inside the signed-in
Windows session, so it cannot show the lock screen or send Ctrl+Alt+Del (use the server mode with the Remotely Host service for that); the token is a
short code protected by a slow key derivation, not a formal password-authenticated key exchange.

## Reaching PCs from outside your network (server mode)

Remotely lets your own devices reach each other, with no public servers. You run one small **server** yourself; every connection is
**end-to-end encrypted** between the two devices, so the server only introduces them and relays bytes it cannot read.

```
  your laptop (viewer)  <-- encrypted -->  your server  <-- encrypted -->  the PC or Mac you control (host)
```

## What you install where

| Where | What | Installer |
|---|---|---|
| The always-on PC or VM (for example your Windows Server 2025 VM) | **Remotely Server** | `Remotely-Server-Setup-<version>-x64.exe` |
| Each Windows PC you want to control | **Remotely Host** (inside the client installer) | `Remotely-Setup-<version>-x64.exe` or `-arm64.exe` |
| Each device you control things *from* | **Remotely** (the app, inside the same installer; or the Mac app) | same / `Remotely-<version>-mac.dmg` |

The installers are built by the GitHub **Actions** tab (artifacts of the latest *build* run), or on a Windows PC with `build\publish.ps1`.
They are unsigned unless you sign them with your own certificate (`build\sign.ps1`, same approach as Portico), so Windows SmartScreen shows
"Windows protected your PC" the first time: click **More info** then **Run anyway**, or right-click the file, Properties, **Unblock**.

## 1. The server (once)

1. Run `Remotely-Server-Setup-...exe` on the server PC. It asks one optional question: the **name or address devices use to reach the server**.
   Leave it empty to use this PC's home-network address (fine at home); enter your public name (for example `myhome.example.com`) to
   reach it from outside. It then installs a Windows service called *Remotely.Server*, opens TCP port **8443** in Windows Firewall,
   and shows an **invite** at the end. (Start menu: *Remotely Server info: invite a new device* shows a fresh invite any time and copies it
   to the clipboard.)
2. **Make the server reachable from outside your network** (only if you want that; at home you can skip this). It is the one thing no
   software can do for you:
   - On your router, forward **TCP 8443** to the server PC. (Look for "port forwarding" or "virtual server".)
   - Use a name that follows your home address: a free dynamic-DNS name, or your router's own, and enter it as the address
     (`Remotely.Server.exe set-address myhome.example.com` changes it later).
   - Check that your internet provider gives you a real public address. If your router's "WAN IP" differs from what a "what is my IP" web
     page shows, you are behind **CGNAT** and port forwarding cannot work: ask the provider for a public address, or put the server on a
     machine that has one.

The **invite** is one line starting `RMT1-`. It holds the server address, the server's key fingerprint (the "pin") and a pairing code. It works
for **1 hour and up to 10 devices**; run *Remotely Server info* again for a fresh one. Devices refuse any server that does not match the pin,
so nobody can stand in for your server, even without a domain name or a public certificate.

## 2. A Windows PC you want to control

Run the client installer. It first asks **who the install is for**:

- **For all users (administrator, one UAC prompt)**: Remotely Host runs as a Windows **service**. It starts at boot, keeps working after
  you sign out, shows the **sign-in and lock screens**, and can answer UAC prompts. Use this for a PC you want to reach when nobody is signed in.
- **Only for me (no administrator)**: Remotely Host runs while you are signed in (like Portico). It cannot show the lock/sign-in screen
  or type into windows running as administrator.

Then it asks for **one thing: paste the invite** (and optionally rename the PC). That is the whole set-up. The installer also opens Remotely
(the viewer) on this PC already joined to your server.

By default every device that has joined your server may connect to this PC ("family mode"): the server only lets devices in that someone
with access to the server invited. If you would rather list the devices one by one, turn it off with `Remotely.Host.exe trust-all off` and
add each with `Remotely.Host.exe trust <viewer id>` (ids are shown in the viewer's Settings).

Useful host commands (administrator prompt for the service; add `--user` for the no-administrator install):

```
Remotely.Host.exe trust-all on|off               any paired device may connect (on, the default after an invite) or only listed ones
Remotely.Host.exe trust <viewer id>              allow control          (add --view-only to let someone only watch)
Remotely.Host.exe untrust <viewer id>            remove them
Remotely.Host.exe enable-sas                     allow Ctrl+Alt+Del from the viewer (service mode)
Remotely.Host.exe diag capture.jpg               what this PC allows, plus one screenshot
```

Files from a viewer land in `Documents\Remotely\Received` (service mode: `C:\Users\Public\Documents\Remotely\Received`). A viewer can
only download files you put in the `Share` folder next to it: nothing else on the PC is reachable.

## 3. Controlling a PC from Windows

Open **Remotely** (Start menu). First run shows a welcome screen with one box: **paste the invite**, press Connect. (The installer does this for
you.) Your computers appear with a green dot when online; click one to connect. "Set up by hand" in Settings is there for the rare case
where you only have the address, pin and a code.

In a session: your mouse and keyboard control the other PC while the window has focus. Toolbar: **Ctrl+Alt+Del**, **send a file** (or drag files
onto the screen), **get a file** from the host's Share folder, **clipboard sharing** on/off. The status line shows whether you are seeing the normal
desktop or the lock/sign-in screen.

## 4. A Mac

Open `Remotely-<version>-mac.dmg` (universal: Intel and Apple silicon). Drag **Remotely** (viewer) and/or **Remotely Host** to Applications and
open them with right-click, **Open** the first time (they are signed ad hoc, not by Apple).

**Remotely Host** shows a small menu bar item and a settings window: paste the invite (or run `remotely-host-mac setup --invite RMT1-...`). The Mac viewer's first screen asks for the invite the same way. macOS asks for two
permissions that only you can grant: **Screen Recording** and **Accessibility** (System Settings, Privacy & Security). Allow both, then reopen it.
To start it at login, run `"/Applications/Remotely Host.app/Contents/MacOS/remotely-host-mac" install`.

Honest limits on macOS: capture needs **macOS 14 or newer**, and macOS does not let any app show the *login window* to a remote viewer, so a
Mac host works while someone is signed in (turn on automatic login for an unattended Mac). Keyboard shortcuts map Ctrl on the other computer to
Command here (switch off in the host settings); Command on a Mac viewer is sent as Ctrl.

## 5. Safety, in plain words

- A device can connect only if it has joined **your server** (someone with access to the server made the invite). In "family mode" (the default after an
  invite) that is enough; with `trust-all off` a host additionally requires each viewer's id to be on its own list. Family mode means you rely on
  your server's pairing instead of a per-PC list: keep the server's PC secure and don't hand out invites you don't mean to.
- Everything between viewer and host is encrypted with keys only those two devices hold; the server cannot read or change a session.
- To remove a device: `Remotely.Host.exe untrust <id>` on the host, and `Remotely.Server.exe remove <id>` on the server.
- Open source: the code is in this repository; nothing phones home.
