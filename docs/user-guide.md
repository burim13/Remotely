# Remotely: setting it up

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
| Each device you control things *from* | **Remotely** (the viewer, inside the same installer; or the Mac app) | same / `Remotely-<version>-mac.dmg` |

The installers are built by the GitHub **Actions** tab (artifacts of the latest *build* run), or on a Windows PC with `build\publish.ps1`.
They are unsigned unless you sign them with your own certificate (`build\sign.ps1`, same approach as Portico), so Windows SmartScreen shows
"Windows protected your PC" the first time: click **More info** then **Run anyway**, or right-click the file, Properties, **Unblock**.

## 1. The server (once)

1. Run `Remotely-Server-Setup-...exe` on the server PC. It installs a Windows service called *Remotely.Server*, opens TCP port **8443**
   in Windows Firewall, and shows you the **server key pin** and a **pairing code** at the end. (Start menu: *Remotely Server: address, key and
   pairing code* shows them again and makes a new code.)
2. **Make the server reachable from outside your network.** This is the one thing no software can do for you:
   - On your router, forward **TCP 8443** to the server PC. (Look for "port forwarding" or "virtual server".)
   - Use a name that follows your home address: a free dynamic-DNS name, or your router's own. You will give devices `wss://<that name>:8443/ws`.
   - Check that your internet provider gives you a real public address. If your router's "WAN IP" differs from what a "what is my IP" web
     page shows, you are behind **CGNAT** and port forwarding cannot work: ask the provider for a public address, or put the server on a
     machine that has one.
3. Devices inside your home network can use the server's local address (for example `wss://192.168.1.20:8443/ws`) with the same pin.

The **pin** is a fingerprint of the server's key. Devices refuse any server that does not match it, so nobody can stand in for your server,
even without a domain name or a public certificate.

## 2. A Windows PC you want to control

Run the client installer. It first asks **who the install is for**:

- **For all users (administrator, one UAC prompt)**: Remotely Host runs as a Windows **service**. It starts at boot, keeps working after
  you sign out, shows the **sign-in and lock screens**, and can answer UAC prompts. Use this for a PC you want to reach when nobody is signed in.
- **Only for me (no administrator)**: Remotely Host runs while you are signed in (like Portico). It cannot show the lock/sign-in screen
  or type into windows running as administrator.

Then enter the server address, the pin, a pairing code, and a name for this PC. To allow a viewer, paste its **device id** (shown in the
viewer's Settings) in the last box, or add it later with `Remotely.Host.exe trust <id>`. **Nobody can connect until you trust them.**

Useful host commands (administrator prompt for the service; add `--user` for the no-administrator install):

```
Remotely.Host.exe trust <viewer id>              allow control          (add --view-only to let someone only watch)
Remotely.Host.exe untrust <viewer id>            remove them
Remotely.Host.exe enable-sas                     allow Ctrl+Alt+Del from the viewer (service mode)
Remotely.Host.exe diag capture.jpg               what this PC allows, plus one screenshot
```

Files from a viewer land in `Documents\Remotely\Received` (service mode: `C:\Users\Public\Documents\Remotely\Received`). A viewer can
only download files you put in the `Share` folder next to it: nothing else on the PC is reachable.

## 3. Controlling a PC from Windows

Open **Remotely** (Start menu). First run: *Settings* asks for the server address, pin and a pairing code. Copy **this device's id** and
give it to each host's `trust` command. Your computers appear with a green dot when online; click one to connect.

In a session: your mouse and keyboard control the other PC while the window has focus. Toolbar: **Ctrl+Alt+Del**, **send a file** (or drag files
onto the screen), **get a file** from the host's Share folder, **clipboard sharing** on/off. The status line shows whether you are seeing the normal
desktop or the lock/sign-in screen.

## 4. A Mac

Open `Remotely-<version>-mac.dmg` (universal: Intel and Apple silicon). Drag **Remotely** (viewer) and/or **Remotely Host** to Applications and
open them with right-click, **Open** the first time (they are signed ad hoc, not by Apple).

**Remotely Host** shows a small menu bar item and a settings window (server address, pin, pairing code, trusted viewer ids). macOS asks for two
permissions that only you can grant: **Screen Recording** and **Accessibility** (System Settings, Privacy & Security). Allow both, then reopen it.
To start it at login, run `"/Applications/Remotely Host.app/Contents/MacOS/remotely-host-mac" install`.

Honest limits on macOS: capture needs **macOS 14 or newer**, and macOS does not let any app show the *login window* to a remote viewer, so a
Mac host works while someone is signed in (turn on automatic login for an unattended Mac). Keyboard shortcuts map Ctrl on the other computer to
Command here (switch off in the host settings); Command on a Mac viewer is sent as Ctrl.

## 5. Safety, in plain words

- A device can connect only if the host has **trusted its id**. Pairing with the server alone is not enough.
- Everything between viewer and host is encrypted with keys only those two devices hold; the server cannot read or change a session.
- To remove a device: `Remotely.Host.exe untrust <id>` on the host, and `Remotely.Server.exe remove <id>` on the server.
- Open source: the code is in this repository; nothing phones home.
