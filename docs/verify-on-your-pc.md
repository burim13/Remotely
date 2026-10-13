# Checking Remotely on your own PCs (about 20 minutes)

The automated checks ran on headless GitHub machines (see `docs/architecture.md`). These are the things only you can confirm on real
screens. Tick them off; send me anything that does not behave.

## Windows (x64 and your ARM PC)

1. **Install and connect.** Server first (`Remotely-Server-Setup...`), then the client installer, "for all users". Open **Remotely** on another
   PC, pair it, trust it on the host (`Remotely.Host.exe trust <id>`), connect.
2. **The normal desktop.** You see the PC. Move the mouse, type in Notepad, copy text on one side and paste on the other.
3. **Lock screen.** On the host press Win+L. From the viewer the status line should say *Lock / sign-in screen*; sign in through the viewer.
4. **Sign out, then connect.** Sign out of the host PC; you should still be able to connect and see the sign-in screen (service mode only).
5. **Restart the host, do not sign in.** Connect after it boots: you should see the sign-in screen.
6. **UAC.** On the host, run something "as administrator". The viewer should show the UAC dialog (secure desktop) and let you click Yes.
7. **Ctrl+Alt+Del.** Run `Remotely.Host.exe enable-sas` once on the host (administrator), then use the keyboard button in the viewer.
8. **Files.** Drag a file onto the session window; it appears in `C:\Users\Public\Documents\Remotely\Received`. Put a file in
   `...\Remotely\Share` on the host and fetch it with the download button.
9. **Two monitors / high-DPI.** The viewer shows all monitors as one wide picture. Check that clicking lands where you point at 125% and 150% scaling.

If something fails, run on the host: `Remotely.Host.exe diag capture.jpg` and look at `C:\ProgramData\Remotely\logs`.

## Your Intel Mac

1. Open the `.dmg`, drag both apps to Applications, right-click **Open** each.
2. In **Remotely Host** enter the server details; allow **Screen Recording** and **Accessibility** in System Settings, then reopen it.
3. From a Windows PC: connect, see the Mac's screen, click, type, copy and paste. (Needs macOS 14 or newer on the Mac.)
4. From the Mac, open **Remotely** and control a Windows PC; Command shortcuts (Cmd+C) should act like Ctrl+C there.

The Apple-silicon build is the same universal app; it was built and its protocol tested, but no Apple-silicon Mac was available to run a real capture.

## From outside your home network

1. Forward TCP 8443 on your router to the server PC; note your public name or dynamic-DNS name.
2. From a phone hotspot (not your home Wi-Fi), enter `wss://<that name>:8443/ws` plus the pin in a viewer and connect.
3. If it cannot connect: check the router's WAN address against "what is my IP" (CGNAT?), and that Windows Firewall has the "Remotely Server" rule.
