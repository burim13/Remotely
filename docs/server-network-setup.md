# Making your server reachable

This page explains what the Server Manager's setup wizard ("Make my server reachable") does, so you can also do it by hand. Nothing here calls a website
or an outside service: every check is between your PC, your router, your server and your own phone.

## 1. Address, name and port

- **Inside your network** devices use this PC's network address (for example `192.168.1.50`). The wizard lists the addresses it finds and prefers the one on the
  network adapter that has a router. Addresses starting `169.254.` (no network found) are never offered.
- **From outside** devices need a *public name or address* and a forwarded port. Type it as `myhome.example.com`, `myhome.example.com:9000`, an IPv4 address, or an
  IPv6 address in square brackets. Do not type `https://` or `wss://`. The same name can be set from the command line: `Remotely.Server.exe set-address myhome.example.com`.
- **Port**: 8443 (TCP) unless you chose another. Changing the port in the wizard writes it to the server's settings through the admin API; restart the server so it
  listens on the new port (the wizard does not claim the server reloads it by itself).
- **Dynamic DNS** (a name that keeps following a home address that changes): many routers have it built in; otherwise search for a provider you trust. Remotely only
  tells you this; it never contacts such a service.

## 2. Port and Windows Firewall

The wizard asks the operating system whether the port is free. If something is listening, it connects to it and compares the key it presents with the server's
key pin: only a match is reported as "your Remotely server", anything else as "in use by something else".

The firewall rule is the same one `Remotely.Server.exe install` creates: name `Remotely Server`, inbound, allow, TCP, the chosen local port
(`netsh advfirewall firewall add rule name="Remotely Server" dir=in action=allow protocol=TCP localport=<port>`). Adding it needs administrator permission; the Server Manager
window itself never runs as administrator and asks an elevated helper to do it (one Windows permission prompt).

## 3. Test from inside

The PC connects to the server on `127.0.0.1`, on its network address and, if you gave one, on the public name, and completes the TLS handshake comparing the key pin.

| Result | Meaning |
|---|---|
| Works | The server runs and the port is open to your network. |
| Answers on this PC but not on the network address | A firewall blocks it (Windows Firewall, or a security program from another company). |
| Does not answer on this PC | The server is not running, or listens on another port. |
| Answers with a different key | Another program uses the port, or the certificate was replaced. |

A failing *public name* from inside does not prove the outside is broken: some routers do not send a PC's own traffic back in. Only the outside test settles it.

## 4. Router: forward the port

In your router's settings (called "Port forwarding", "Virtual server", "NAT" or "Applications and gaming", depending on the brand) add a rule:

| Field | Value |
|---|---|
| Protocol | TCP |
| External (WAN) port | your port (8443 by default) |
| Internal (LAN) address | this PC's network address chosen in step 1 |
| Internal (LAN) port | the same port |

Also reserve that address for this PC in the router ("DHCP reservation" or "static lease"), otherwise the router may give the PC a different address one day and
forwarding stops working.

**Can it work at all?** Read the address of the router's internet side (WAN / Internet IP) from its status page.

- A *public* address: forwarding can work.
- `100.64.x.x` to `100.127.x.x` (carrier-grade NAT): your provider shares one public address between customers. Port forwarding on your router cannot work. Ask the
  provider for a public address, or run the server on a machine that has one.
- A private address (`10.x`, `172.16` to `172.31`, `192.168.x`): another router (modem or provider equipment) is in front of yours. Forward there too, or put your router in bridge mode.

A public WAN address does not prove forwarding works; the outside test does.

## 5. Optional: let the router open the port itself (UPnP / NAT-PMP)

**Off by default.** Routers that support UPnP or NAT-PMP open a port when a program asks, without the owner seeing it, and many do it badly, so manual forwarding stays the
main path. When you switch it on in the wizard nothing is sent until you press the button. The wizard then shows exactly what the router accepted (protocol, external
port, internal address and port, the router's outside address) and asks the router to remove it when you switch the option off or the server stops.

Details: mappings are requested with a one-hour lease and renewed while the Manager runs (some routers only accept permanent mappings; the wizard then retries without a lease). If a
router already forwards that port to another device, nothing is overridden.
This code was written for this project (UPnP IGD over SSDP/SOAP, and NAT-PMP per RFC 6886), and has only been tested against imitations of a router. It has **not**
been tried on a real router. If the router refuses to remove a mapping, the wizard keeps it in the list and you remove it in the router's own page.

## 6. Test from outside, with your phone

1. Turn Wi-Fi off on your phone so it uses mobile data.
2. Open the address the wizard shows in the phone's browser. It looks like `https://myhome.example.com:8443/check/XXXX`.
3. The browser warns that the connection is not private, because the server uses its own certificate. That is expected; continue anyway.
4. The wizard shows a green result when the server received the request from an address outside your network. A request from a private address is reported as
   "from inside" (the phone is probably still on Wi-Fi) and the wizard keeps waiting for up to 3 minutes.

This needs the server to serve the check page and to report who asked for it. If the server you run does not have that yet, the wizard says so instead of failing silently.

## 7. The certificate

- The server makes its own certificate on first run. Devices do not trust a company; they remember the **pin**, the SHA-256 of the server's public key, from the invite.
- The wizard shows the **pin** (what devices check) and the **fingerprint** (SHA-256 of the whole certificate, what a browser or certificate viewer shows).
- **Renewing** with the same key creates a new certificate (valid ten years), keeps the pin and keeps the old file as `server.pfx.bak-<time>` in the server's data folder. No device needs
  a new invite. Restart the server afterwards. The wizard advises renewal 60 days before expiry (a choice made in this program, not a rule of any standard).
- **Using your own certificate**: choose a `.pfx`/`.p12` file and its password. The wizard refuses, with a specific message: a file that is not a PFX, a wrong password (or a damaged
  file: the two cannot be told apart reliably), a file with certificates but no private key, an expired certificate, a certificate that is not valid yet, and keys that are neither RSA nor
  ECDSA. It warns when the certificate expires within 60 days or says it is not for servers. **A different key means a different pin: every paired device must be invited again.** The
  wizard says so before you install.
