# Same-network sharing: consent and remembered PCs

Written by A3. Decisions by Burim: the first connection from a new PC is approved on the sharing PC (Allow once / Always allow this PC / Deny); a PC allowed "always" may
reconnect later without a new token and can be removed from a list. Nothing here edits the token handshake (`LanToken.cs`, `LanHandshake.cs`, `LanSecureSession.cs`, owned by A9).

## Identity of a remembered PC

A PC is identified by a long-term ECDSA P-256 device key (`DeviceIdentity`); its device id is the SHA-256 of the public key. The name a PC gives is only a label and is never used
to recognise it. On this PC the key is the same file the app already keeps for the server mode (`viewer.key`, protected with the current user's Windows data protection).

## First connection (token) with device proof

1. The controller types the token. The existing token handshake (RMTL1) runs unchanged; the controller's name field carries a trailing marker (`ControllerHello`, control character
   U+001F followed by `RMD1`, counted inside the 60-character limit) to say "I can prove a device key".
2. Inside the already encrypted channel, before the host starts the session, the host sends a Challenge (fresh 32-byte nonce, the host's public key, the host's name). The controller
   answers with its public key and an ECDSA signature over: a label, the nonce, the host's public key and its name (`DeviceProof`).
3. The host runs the consent rules (`ConsentGate`) and tells the controller Granted (and whether it was remembered) or Denied. Only after Granted does the host start the screen/input
   code, so a denied PC never gets a frame (test: `Denied_means_the_other_pc_is_told_no_and_no_frame_is_ever_captured`).
4. "Always allow" stores the controller's public key in `remembered-pcs.json`. The controller learns the host's public key from the Challenge and can save it (the address book, A4/A14).
   That key arrived through the channel the token protects, so it is as trustworthy as the token handshake: it is trust on first use anchored by the token.

An older controller sends no marker: the host skips step 2, can only offer Allow once (never Always), and an older host shows an invisible control character at the end of the name.

## Reconnecting without a token ("resume")

The controller connects to the host's address (saved at first use) and runs the same mutually authenticated handshake the server mode already uses (`InitiatorHandshake` /
`ResponderHandshake`, magic `RMT1`): each side signs its ephemeral ECDH key with its long-term key, the controller names the exact host device id it expects, keys come from HKDF over
the ECDH secret and the transcript. The host accepts only if the controller's device id is on the remembered list. The host's listener tells the two handshakes apart by the first
4 bytes of the first frame (`RMT1` or `RMTL`) and replays that frame into the unchanged token code (`ReplayStream`). After the handshake the controller sends one encrypted
confirmation byte (`0xE5`); the host drops the connection if it does not arrive (see "Replay" below).

## Threat notes (what is and is not protected)

| Threat | Result |
|---|---|
| LAN attacker without any key, token unknown | Cannot resume (needs a valid signature from a listed key) and cannot pass the token handshake. Unchanged limits of the token design apply (A9). |
| Active man in the middle on resume | Both sides sign; the controller checks the expected host id, the host checks the list. Tested: wrong host id and wrong controller key are refused. |
| Replay of a recorded resume Hello1 | The signature is valid, so the host answers, but keys need the original ephemeral private key. The first encrypted confirmation never arrives and the connection is dropped before any session or banner starts. Tested: `A_recorded_resume_hello_replayed_by_someone_else_never_starts_a_session`. A replayed connection can occupy one of 8 handshake slots for up to 12 s. |
| Same name, different key | Not recognised (`A_different_key_with_the_same_name_is_not_recognised`). |
| Edited `remembered-pcs.json` | An entry whose id does not match its public key is ignored. Someone who can write the user's profile folder can add their own key; that attacker already runs as the user and could also read the device key, so this is an accepted risk. |
| Stolen controller device key | Whoever holds the key can reconnect without token or question while sharing is on, until the entry is removed. Mitigations: the list can be edited, the switch "Let remembered PCs reconnect without a token" turns resume off at once, and sharing must be on. The key file is protected by Windows data protection for the current user. |
| Malicious viewer already allowed | Can do what any controlling viewer can; "Pause control" (view only) and "Stop sharing" on the always-visible banner end or limit it. |
| Nobody answering the question | Counts as Deny after 25 seconds. A failing or missing question window also counts as Deny. |

## Not done, and why

- **PIN for unattended reconnect.** The task text lists it as optional; the recorded decision is the remembered-PC list, which is what was built. No PIN exists.
- **Finding a remembered PC by device id on the network.** `LanDiscovery` answers only for the current token label, so a controller reconnects by a saved address (and port). If the
  address changes the saved entry fails with the message "Could not reach the PC". Discovery by device id needs a change in `LanDiscovery.cs` (not owned by A3).
- **Controller-side integration.** `DeviceProof.RespondAsync` (after `LanConnector.ConnectAsync` with `ControllerHello.Encode(name)`) and `LanResume.ConnectAsync` exist and are tested,
  but the connect screen (A4) has to call them and save the `HostAnnouncement`.

## Proposals for A9 (not applied by A3)

1. Expose a channel-binding value from `LanSecureSession` (a hash of the transcript) so `DeviceProof` can sign it instead of a separate nonce. The nonce is sufficient today because the
   whole exchange runs inside the encrypted channel; binding would remove the need to reason about that.
2. Carry the "device proof supported" flag in `Hello1` instead of the name marker.
