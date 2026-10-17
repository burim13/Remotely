# Remotely: security model and threat analysis

Written from the code on branch `claude/agent-09-security` (agent A9), after reading `src/Remotely.Core/Crypto`, `src/Remotely.Core/Lan`, `src/Remotely.Core/Protocol`,
`src/Remotely.Client`, `src/Remotely.HostCore`, `src/Remotely.ViewerCore`, `src/Remotely.Server`, and the places that store keys (`Remotely.Host/HostPaths.cs`,
`Remotely.App/ViewerSettings.cs`). Statements marked **(analysis)** are my reasoning about the code, not something a test or a run showed. Statements marked
**(unverified)** are things I could not check here. Test names are the real xUnit method names in `tests/Remotely.Tests`.

How to read the **Status** column:

- **Mitigated** has a named test that fails if the protection is removed.
- **Accepted** is a risk that remains by design or for now. The reason is stated.
- **Open** is a problem found in code that another agent owns. It has a skipped test in `CryptoKnownIssuesTests.cs` (the test states the right behaviour; remove the
  `Skip` when it is fixed) and an entry under "Open findings".

## 1. What the system does (from the code)

**Device identity.** Each device has an ECDSA P-256 key (`DeviceIdentity`). The device id is the lowercase hex SHA-256 of the public key's SubjectPublicKeyInfo DER.
Only the canonical 91-byte encoding is accepted when a key arrives from another party (see "Fixed in this pass").

**Server mode** (`Handshake.cs`, `SecureChannel.cs`). Viewer and host run a signed ephemeral ECDH (P-256) through the server's relay. Hello1 carries the initiator's
identity, an ephemeral key, a 32-byte nonce, the responder id it expects, and a signature over those. Hello2 carries the responder's identity, ephemeral key and nonce,
and a signature that covers a hash of Hello1. Keys: HKDF-SHA256, salt = SHA-256(nonceI || nonceR), info = "RMT1-keys" and the hash of both hellos; one 32-byte AES-256-GCM
key per direction. Frames are `counter(8, big-endian) || ciphertext || tag(16)`; nonce = 4 zero bytes || counter; associated data = `RMT1-data`; the receiver accepts only
the next expected counter. The server forwards bytes and never holds keys.

**Server authentication of devices** (`Hub.AuthenticateAsync`, `CoordinatorClient`). The server sends a random 32-byte challenge; the device signs `"RMT1-AUTH" || challenge`
with its device key. Unknown devices must present a pairing code (8 characters of a 32-character alphabet, 15 minutes by default, five wrong tries per remote address
then a 15-minute lockout). Over `wss` the client accepts only a server certificate whose SPKI SHA-256 equals the pinned value (compared with `FixedTimeEquals`); `wss` without a pin
and `ws` without an explicit flag are refused.

**Same-network token mode** (`LanToken.cs`, `LanHandshake.cs`, `LanSecureSession.cs`, `LanHost.cs`). The host shows `XXXX-YYYY-ZZZZ`: a 4-character label (public; used to find the PC by UDP broadcast) and an
8-character secret. Both sides make a fresh P-256 ECDH key. Session keys are HKDF-SHA256 over (ECDH secret || PBKDF2-HMAC-SHA256(secret, host's 16-byte salt, 300000 rounds)), salt =
SHA-256(nonceC || nonceH), info = "RMTL1-keys" and the hash of both hellos. The controller's first encrypted message is the proof (`RMTL1-proof:` + its name); the host answers
`RMTL1-accepted` only if the proof decrypts, otherwise it closes the connection without sending anything else. A token works for one connection, lasts 10 minutes by default (`LanHostOptions.TokenLifetime`),
and is replaced after wrong tries. Only addresses on one of this PC's own subnets are served (`LanNetwork.IsOnLink`). **It is not a password-authenticated key exchange** (section 4).

**Authorisation.** Server mode: the host's own list decides who may view or control (`HostConfig`), with a "family mode" (`TrustAllPaired`) that accepts any device the server has paired. Token mode: `ShareService`
in the Windows app sets `IsTrusted = _ => true` and `CanControl = _ => true` ("the token already proved who is connecting"). The consent prompt recorded as decision 3 in `docs/agent-specs.md` is **not in this code yet**.

**Files.** Uploads go to a receive folder as `name.part`, size-limited (512 MB), SHA-256 checked, never overwrite, names sanitised. Downloads come from a share folder; names with path parts are refused.

## 2. Threat table

### 2.1 Attacker on the same network, no token

| # | Threat | What the code does | Status | Evidence |
|---|---|---|---|---|
| L1 | Reads a session by sniffing | All session traffic is AES-256-GCM under keys that need the ECDH secret and the token key. The token secret and its stretched key never appear in Hello1, Hello2, the proof or the answer. | Mitigated | `The_token_secret_and_the_stretched_key_never_appear_in_what_goes_on_the_wire`, `Channel_frame_layout_is_counter_then_AES_256_GCM_with_the_documented_nonce_and_associated_data` |
| L2 | Sniffs a connection and guesses the token offline | The proof is encrypted under a key that includes the ECDH secret, which needs one side's ephemeral private key. A passive observer has neither. **(analysis: relies on P-256 ECDH hardness; there is no test for the negative)** | Mitigated by design | none beyond L1 |
| L3 | Connects to the host and guesses the token online | At most 3 tries are reserved per token before any guess is checked; then the host replaces the token. Chance per token: at most 3 / 32^8 = 2.7e-12. Before this pass, 8 concurrent guesses were evaluated against a token limited to 3 (measured with a throw-away test: 8 wrong-token log lines in one burst of concurrent attempts, four runs). | Mitigated | `However_many_connections_arrive_at_once_only_three_tries_are_ever_checked_against_one_token`, `A_wrong_token_is_refused_and_three_wrong_tries_replace_the_token` (LanTests), `The_host_says_nothing_to_someone_who_does_not_know_the_token` |
| L4 | Pretends to be the host (answers discovery or connects first) and tests guesses offline against the controller's proof | **Possible.** Discovery replies are not authenticated; the attacker needs only the public label. See section 4 for the measured cost. | Accepted | `An_active_attacker_posing_as_the_host_can_test_token_guesses_offline_this_is_the_documented_limit` (shows the attack works; it is not a defence) |
| L5 | Fake host makes the controller believe it connected | The controller proceeds only when it can decrypt the answer, which needs the token key. | Mitigated | `A_wrong_token_gives_channels_that_cannot_read_each_other` |
| L6 | Denies service: burns tokens with wrong tries, floods handshakes | Three wrong tries replace the token, so any PC on the subnet can force that. The host accepts 8 handshakes at once with a 12 s timeout, from on-link addresses only. | Accepted | `A_wrong_token_is_refused_and_three_wrong_tries_replace_the_token` shows the burning; the flood limit has no test |
| L7 | Malformed hello, framing or discovery packets crash or hang the host | Every parser fails with its documented exception only. | Mitigated | `LAN_framing_survives_hostile_streams_and_roundtrips_valid_ones`, `LAN_framing_refuses_an_oversized_length_before_allocating_or_reading_it`, `Token_handshake_messages_are_either_accepted_or_a_handshake_error`, `The_discovery_responder_survives_hostile_packets_and_answers_only_a_well_formed_probe_for_its_label`, `The_discovery_finder_ignores_hostile_replies_and_still_accepts_a_good_one`, `Hellos_cut_short_at_any_length_are_refused` |
| L8 | Replays a recorded session | Fresh ephemeral keys and nonces per connection; strict counter inside a connection. | Mitigated | `Channel_refuses_replayed_dropped_reordered_reflected_and_foreign_frames` (both handshakes) |
| L9 | Alters a hello in transit | The token handshake has no signatures; the transcript hash in the key derivation makes the two sides' keys differ. | Mitigated | `Changing_any_part_of_the_token_hello2_in_transit_leaves_the_two_sides_unable_to_talk` |
| L10 | Reaches the host from another network | `IsOnLink` compares against the PC's own interface subnets; loopback only when tests allow it. **Only the function is tested, not a full connection from a foreign address.** | Partly mitigated | `Only_addresses_on_the_same_subnet_count_as_this_network` |
| L11 | Reads who is sharing | Label, TCP port and PC name are broadcast in discovery replies in clear. | Accepted | none |

### 2.2 Malicious or compromised server (server mode)

| # | Threat | What the code does | Status | Evidence |
|---|---|---|---|---|
| S1 | Reads session content | End-to-end AES-GCM; the server sees ciphertext, sizes and timing. | Mitigated | `Viewer_and_host_talk_end_to_end_through_the_relay`, `Ciphertext_does_not_contain_the_plaintext_and_directions_use_different_keys` |
| S2 | Man-in-the-middle between two devices | Both hellos are signed by the long-term device keys; the initiator names the responder id it expects and checks it. | Mitigated | `Initiator_rejects_a_responder_that_is_not_the_expected_device`, `Initiator_rejects_a_hello2_signed_by_someone_else`, `Tampering_with_hello1_is_detected`, `Tampering_with_hello2_is_detected`, `No_single_changed_bit_in_a_signed_hello_is_ever_accepted` |
| S3 | Tampers with, drops, duplicates or reorders relayed frames | The counter must match exactly; any change fails the GCM tag. | Mitigated | `A_relay_that_tampers_with_traffic_is_detected`, `Channel_refuses_replayed_dropped_reordered_reflected_and_foreign_frames`, `Channel_refuses_a_frame_that_is_cut_extended_or_has_a_changed_counter_or_tag` |
| S4 | Replays a recorded Hello1 to make the host ask for approval, or to take a session slot | The host cannot tell a replayed Hello1 from a new one (nothing fresh from the host is in what the initiator signs), so the trust decision runs again. The replayer gets no working channel: the keys need the initiator's ephemeral private key. The trust decision is never reached for a hello that fails authentication. | Accepted (limit). **Recommendation for the code that shows approval prompts: ask only after the first authenticated message of the session, not inside `isTrusted`.** | `A_recorded_hello1_replayed_to_the_host_gives_the_replayer_no_working_channel`, `A_hello_that_fails_authentication_never_reaches_the_trust_decision` |
| S5 | Lists a fake device under a trusted name | The viewer picks a target by device id; names come from the server. Nothing in the code makes the user compare ids out of band (a 12-hex-character short id exists, `DeviceIds.Short`). | Accepted | none |
| S6 | Adds a device that "family mode" hosts then trust | In `TrustAllPaired` mode any device the server pairs is trusted by the host, so whoever controls the server controls those PCs. Stated in `docs/architecture.md`. `trust-all off` restores the per-PC list. | Accepted by design | `Family_mode_trusts_any_paired_device_but_still_honours_view_only` shows the mode, not a defence |
| S7 | Forces a weaker mode | No negotiation exists. A wrong magic, version digit or message type is a hard error; `ws` and unpinned `wss` are refused by the client; a host Hello with another protocol version is refused by the viewer. | Mitigated | `There_is_no_downgrade_path_any_other_protocol_or_message_type_is_refused_outright`, `Plain_ws_is_refused_unless_explicitly_allowed`, `Over_tls_the_client_connects_only_to_the_pinned_server_key`, `A_host_with_a_different_protocol_version_or_no_hello_is_refused_at_the_start`. The client's check of `ServerHello.ProtocolVersion` is code-read only. |
| S8 | Re-uses a device's server-login signature at another server | The signed text is `"RMT1-AUTH" || challenge`: no server identity, no TLS binding **(analysis)**. A server a device chooses to talk to could obtain a login at another server as that device, but cannot open end-to-end sessions as it (Hello1 needs a fresh signature). | Accepted | none |
| S9 | Learns who talks to whom, when, and how much | Inherent in a relay. | Accepted | none |
| S10 | Brute-forces pairing codes | 32^8 codes, five wrong tries per address then lockout, codes single-use or limited-use (invites). | Mitigated | `Wrong_codes_are_refused_and_repeated_guessing_locks_the_address_out`, `A_pair_code_works_once_only`, `An_invite_code_works_the_stated_number_of_times_then_never_again` |
| S11 | Revoked device keeps working | Revoking removes the device from the store. A device that is already connected is not disconnected and can still list devices and open sessions until it drops. (Measured: DELETE returned 204, the same connection then opened a session and listed devices; its reconnect was refused.) | **Open** (owner A10) | `KnownIssue_a_device_revoked_while_connected_must_lose_its_connection` (skipped) |
| S12 | Local program on the server PC uses the admin API | `/admin` has no authentication; it is restricted to the loopback port and rejects `X-Forwarded-For`. Any local process can create pairing codes or remove devices. | Accepted | `The_admin_api_is_not_reachable_through_the_public_port` covers only the network side |

### 2.3 Stolen device key

| # | Threat | What the code does | Status | Evidence |
|---|---|---|---|---|
| K1 | Key file read from disk | Windows host service: PKCS#8 protected with DPAPI at `LocalMachine` scope, data directory ACL limited to SYSTEM and Administrators with inheritance off; user-mode host and the Windows app: DPAPI `CurrentUser`. The DPAPI "entropy" is a fixed string in the source (`Remotely.Host.v1`, `Remotely.Viewer.v1`), so it adds no secrecy. `Remotely.Probe` (a test tool) writes keys unprotected. macOS storage was not reviewed. | Accepted | none (Windows-specific, not testable on Linux CI) |
| K2 | Thief uses a stolen viewer key | Can sign in to the server and to hosts that trust that id, until the id is removed from the host's list or revoked at the server (see S11). | Accepted | none |
| K3 | Thief decrypts past recordings | Session keys come from ephemeral ECDH; the long-term key only signs. **(analysis)** | Mitigated by design | `Two_handshakes_between_the_same_devices_derive_different_keys` shows fresh keys per handshake, not the property itself |
| K4 | Thief impersonates a stolen host key to viewers | Possible; viewers expect that id. | Accepted | none |
| K5 | One key under several ids | The platform importer accepted a re-encoded public key (see "Fixed in this pass"). | Mitigated | `One_key_cannot_be_presented_under_a_second_device_id_by_re_encoding_it` |

### 2.4 Shoulder-surfed or photographed token

| # | Threat | What the code does | Status | Evidence |
|---|---|---|---|---|
| T1 | Someone on the subnet who saw the whole token connects first | They get full control, clipboard and file upload: the Windows app trusts the token holder and has no approval prompt yet. Limits: one connection per token, 10-minute lifetime, a new token on request. | Accepted until the approval prompt (decision 3) exists | `A_token_works_once_then_a_new_one_is_shown`, `An_unused_token_expires_and_is_replaced` |
| T2 | Saw only the 4-character label | Gives the attacker nothing beyond the public label. | Mitigated by design | none |
| T3 | Token read from a log | `LanHost.Start()` logs the full token. | **Open** (owner A3) | `KnownIssue_the_start_up_line_of_the_token_host_must_not_contain_the_token` (skipped) |
| T4 | Token printed through its object | `LanToken` is a record; its default text would print the secret. | Mitigated | `A_token_never_prints_its_secret` |

### 2.5 Malicious viewer (authenticated, attacks the host)

| # | Threat | What the code does | Status | Evidence |
|---|---|---|---|---|
| V1 | View-only viewer sends input, clipboard or files | The host ignores those messages unless the viewer has control, and offers no capabilities to view-only viewers. | Mitigated | `A_view_only_viewer_cannot_inject_input_or_touch_clipboard_or_files` |
| V2 | Upload name escapes the receive folder or hits a device name | Names are reduced to a safe file name; temp `.part` file; nothing overwritten. A reserved name with several extensions (`CON.tar.gz`) passes through unchanged; whether Windows treats that as a device was not tested. | Mitigated, with one **Open** question (owner A8) | `Files_upload_with_checksum_and_unsafe_names_are_neutralised`, `File_names_cannot_escape_the_folder_or_hit_device_names`, `File_names_always_come_out_safe`, `Hostile_file_and_clipboard_messages_never_write_outside_the_receive_folder`, `KnownIssue_reserved_device_names_with_several_extensions_must_be_renamed` (skipped) |
| V3 | Corrupt or oversized upload | Size over 512 MB refused; size and SHA-256 checked; a failed upload leaves nothing behind. There is no quota, so many 512 MB uploads can fill the disk. | Mitigated (content), Accepted (disk) | `A_corrupted_upload_is_rejected_and_leaves_nothing_behind` |
| V4 | Downloads a file outside the share folder | Names with any path part are refused; hidden, system, link and `.part` files are not listed. | Mitigated | `Shared_files_can_be_listed_and_downloaded_but_nothing_else_on_the_host`, `Hostile_file_and_clipboard_messages_never_write_outside_the_receive_folder` |
| V5 | Malformed protocol messages | Strict parsing; one bad message gets an error reply. | Mitigated | `Input_events_roundtrip_and_hostile_messages_are_rejected`, `Input_codec_never_throws_and_what_it_accepts_encodes_back_to_the_same_events`, `Frame_hello_and_id_parsers_never_throw_the_wrong_exception`, `Field_reader_only_ever_fails_with_a_format_error`, `Control_messages_and_relay_frames_never_throw_the_wrong_exception`, `Malformed_control_json_is_always_a_format_error_never_another_exception` |
| V6 | Input flood | A message holds at most 256 events; the host has no rate limit. | Accepted | none |
| V7 | Reads the host clipboard | A controlling viewer receives the host clipboard text on frame requests, by design (clipboard sync). | Accepted by design | `Clipboard_syncs_both_ways_without_echo` |
| V8 | Opens many sessions | `HostAgent` serves 4 at a time; `LanHost` serves one controller at a time. | Mitigated (host side) | no test |
| V9 | Anything a controlling viewer can do as the signed-in user | Inherent in remote control. | Accepted | none |

### 2.6 Malicious host (attacks the viewer)

| # | Threat | What the code does | Status | Evidence |
|---|---|---|---|---|
| H1 | Announces a huge file, sends more than announced, or sends wrong content | Size cap, byte count and SHA-256 are checked; the partial file is deleted. | Mitigated | `A_host_that_announces_an_absurd_file_size_is_refused_and_nothing_is_written`, `A_host_that_sends_more_than_it_announced_is_cut_off_and_leaves_no_file`, `A_host_that_sends_the_wrong_content_is_caught_by_the_checksum_and_leaves_no_file` |
| H2 | Hostile file names in the share listing | The viewer saves under the sanitised name inside its download folder. | Mitigated | `A_name_with_path_parts_from_a_host_listing_still_lands_inside_the_download_folder` |
| H3 | Garbage frames, lists, acks, unknown ids | Reported or ignored; the session continues. | Mitigated | `Garbage_frames_lists_acks_and_unknown_ids_from_a_host_do_not_end_the_session` |
| H4 | Wrong protocol version or no hello | Refused at the start. | Mitigated | `A_host_with_a_different_protocol_version_or_no_hello_is_refused_at_the_start` |
| H5 | Pretends to be the host in token mode | Needs the token (L5). | Mitigated | `A_wrong_token_gives_channels_that_cannot_read_each_other` |
| H6 | Pushes text into the viewer's clipboard | `ViewerSession` raises `ClipboardReceived` with the host's text; what the app does with it is outside this review. | Unknown (UI code, A5/A7) | none |
| H7 | Image decoder attack through a crafted JPEG | `FrameCodec` bounds the dimensions (1..32768) and checks lengths; the JPEG is decoded by the UI layer, which I did not review. | Unknown | none |

### 2.7 Downgrade and replay (cross-cutting)

| Threat | Rule | Evidence |
|---|---|---|
| Handshake downgrade | One version of each handshake, no negotiation, no fallback. The version is part of the magic and of the key labels (`RMT1`, `RMTL`, `RMT1-keys`, `RMTL1-keys`). **Rule for any future change:** a new handshake gets a new magic and key label; a client never retries an older handshake by itself; a host that does not understand it closes the connection and the controller shows "update the other PC". | `There_is_no_downgrade_path_any_other_protocol_or_message_type_is_refused_outright` |
| Key stretching downgrade | The 300000 rounds are a constant in the controller; the host cannot lower them. If they were carried in Hello2, a fake host could pick a low count and make guessing cheap, so they must stay a constant tied to the handshake version. | `Token_key_stretching_is_PBKDF2_HMAC_SHA256_with_300000_rounds_and_a_32_byte_result` |
| Frame replay, reorder, drop, reflection, cross-session | See L8, S3. | `Channel_refuses_replayed_dropped_reordered_reflected_and_foreign_frames`, `Channel_stops_instead_of_wrapping_its_counter` |
| Token or code reuse | Token: single use. Pairing code: single or limited use. | `A_token_works_once_then_a_new_one_is_shown`, `A_pair_code_works_once_only` |

## 3. Fixed in this pass (found by the new tests)

1. **Damaged curve identifier.** A public key whose curve OID had a changed bit made the Linux crypto backend throw `PlatformNotSupportedException`. Neither `DeviceIdentity.Verify`
   nor the handshakes expected it. Reachable with attacker-chosen bytes through the server's device login and through both handshakes in server mode and token mode. In `LanHost`
   such an exception is not in the handler's catch filter, so it would have escaped and left the socket undisposed **(analysis from the code, not run)**. Now a clean refusal (`CurveCheck.IsKeyError`). Test: `A_public_key_with_a_damaged_curve_identifier_is_refused_cleanly_everywhere`. Found by `Validly_signed_hellos_with_hostile_contents_are_still_only_handshake_errors` and `Public_key_and_identity_parsers_fail_with_the_documented_exception_only`
   (they failed before the fix). Whether Windows or macOS throw the same exception is **unverified**; the fix covers both exception types.
2. **One key, several device ids.** Setting the BIT STRING "unused bits" byte of a public key to 1 (when the key's last bit is 0) was accepted by the importer, so the same key could be
   presented with a different SHA-256, i.e. a different device id. Found by `No_single_changed_bit_in_a_signed_hello_is_ever_accepted` (bit 272 of Hello1, in about 5 of 8 runs). `DeviceIdentity.Verify` now accepts only the encoding the platform re-exports. Impact
   (analysis): it did not give an attacker a key; it let a key holder appear under extra ids, which matters for per-id lists (view-only, revocation). Test: `One_key_cannot_be_presented_under_a_second_device_id_by_re_encoding_it`.
   The `mac` workflow (run 38984932741) concluded success on the commit that introduced this check; I did not read which of its steps exercise Swift-made public keys beyond what `docs/architecture.md` says (Swift and .NET interop through the real server), and `mac/Sources/RemotelyKit/Crypto.swift` builds keys with CryptoKit's `derRepresentation`.
3. **More than three token guesses.** See L3. `LanTokenState.TryBeginAttempt` reserves the try before the guess is checked.
4. **Token printed by its record text.** `LanToken.ToString` hides the secret (T4).
5. **Key material kept in memory.** The ECDH secrets, HKDF output, concatenated input key material and the password bytes of the token are overwritten after use (`HandshakeCommon.Zero`, `LanToken.DeriveKey`). `LanTokenState.Clear()` overwrites the stretched token key.
   Not overwritten: the token and codes as .NET strings (immutable), the keys inside `AesGcm` (released by `Dispose`, the platform clears them), and arrays the caller owns (`ExportPkcs8`). The wiping itself has a test only for `HandshakeCommon.Zero`
   and `LanTokenState.Clear` (`Zeroing_helpers_overwrite_key_material_and_a_cleared_token_state_has_no_key_left`); that the other intermediate arrays are wiped is by code review. `LanHost` does not call `Clear()` yet (request below).

## 4. Token handshake: PAKE evaluation, and what an offline guess costs

**Question.** Replace "PBKDF2 plus ECDH" with a real password-authenticated key exchange (SPAKE2, OPAQUE or CPace) if a maintained, MIT/Apache/BSD .NET implementation exists and can be tested against published vectors.

**What I did and found.**

- The NuGet search service (`azuresearch-*.nuget.org`) is blocked from this environment (the proxy answers 403), so I could **not** search NuGet by keyword. I used a web search and then queried NuGet's package index (`api.nuget.org`) for specific package ids.
- Package ids `spake2`, `cpace` and `spake2plus` do not exist on NuGet (the index answers "blob not found"). Packages under other names might exist; I could not enumerate them.
- `OPAQUE.NET` (author Adam Craven, licence expression Apache-2.0, project github.com/channeladam/OPAQUE.NET) exists: versions `0.1.1-alpha` and `0.1.2-alpha`, registry entry last changed 2021-02-25, description begins "DON'T STORE PASSWORDS!". It is an alpha with no release since 2021, so it is not "maintained", and it predates the final OPAQUE specification (RFC 9807), so I cannot test it against the published vectors. OPAQUE is also an asymmetric (client-server, registration-based) protocol; this token is a one-time secret shared by two equal peers with no registration step **(analysis)**, so a balanced PAKE (SPAKE2, CPace) is the better fit.
- `BouncyCastle.Cryptography` 2.7.0 (MIT) contains types named `JPakeParticipant` and `Srp6Client`; a text search of the net6.0 assembly found no name containing `Spake2`, `CPace` or `OpaqueClient`. J-PAKE is a balanced PAKE, but it is a multi-round protocol with zero-knowledge proofs; I did not evaluate its parameters or find published vectors that I could run against it, and it would add a large dependency to `Remotely.Core`, which the single-file host publishes.
- `srp` (SRP-6a, Alexey Yakovlev, published 2022-03-28) exists; SRP-6a is an older asymmetric design. I did not read its licence text (the package metadata gives a link, no expression).

**Decision: the current design stays.** No candidate meets "maintained, licence-compatible, testable against published vectors". Writing SPAKE2 or CPace myself is exactly what the rule is meant to prevent. A future owner who wants to do it: SPAKE2 is RFC 9382 and CPace is a CFRG specification; both come with test vectors; the work would be a `RMTL2` handshake under the downgrade rule in section 2.7, with an explicit user-visible "update the other PC" failure and no automatic fallback.

**What an offline guess costs today.** Attack (shown to work by `An_active_attacker_posing_as_the_host_can_test_token_guesses_offline_this_is_the_documented_limit`): a party on the network answers the controller's Hello1 with its own Hello2 (it needs only the public label), receives the proof, and checks guesses with its own copy of the key schedule. A passive observer cannot (L2).

| Quantity | Value | Source |
|---|---|---|
| Secret space | 32^8 = 1,099,511,627,776 (2^40) | alphabet of 32 and length 8 in `LanToken` |
| Expected guesses | 2^39 = 549,755,813,888 | half the space |
| Cost of one guess | 90.99 ms on one core, 300000 rounds | measured on this machine: Intel Xeon @ 2.10 GHz, 4 vCPU, .NET 10, 20 runs averaged, 2026-10-16 |
| Work in SHA-256 compressions | about 2 x 300000 = 600,000 per guess; about 3.3e17 for the expected search | counted from PBKDF2-HMAC-SHA256 with one output block and precomputed key pads (approximate); this machine does about 6.6 million per second per core |
| Expected search on this machine | about 1,585 core-years (full space about 3,170) | 2^39 x 0.09099 s |
| Cores needed to finish the expected search inside the 10-minute token life | about 83 million of these cores (about 167 million for the full space) | 2^39 x 0.09099 s / 600 s |
| With 1,000,000 rounds instead (measured 275.24 ms) | about 4,795 core-years expected; 3.0x the cost | same method |
| Speed of GPUs or special hardware | **unknown, not measured here.** Divide the figures above by their speed-up over one of these cores. | |

What the numbers mean (analysis): an attacker has to be on the network, active, and present at the moment someone types the token. Cracking later is useless: the token is single use and expires, and the secret is used for nothing else. Beyond the 10-minute window the attacker gains only knowledge of a dead token. The unused token on the real host stays valid until it expires (default 10 minutes) or three wrong tries, so an attacker who recovers it inside that window can use it; the figure above is what that needs.

**Hardening applied without changing the wire format:** strict three-try reservation (L3), no secret in `ToString`, wiped intermediates, no logging of the token after start-up (until the A3 fix), and the key-stretching parameters pinned by a test. Raising the rounds is a one-line constant, but it changes the protocol for existing builds (a new build's controller derives a different key from an old build's host and every connection fails as "wrong token"), so it needs a version bump per section 2.7 and is the integrator's decision; 1,000,000 rounds costs a legitimate user about 0.28 s on this machine (one stretch per token on the host, one per connection on the controller).

## 5. Constant-time comparisons: audit

Checked every `SequenceEqual`, `Equals(` and `==`/`!=` on key-like names in `Crypto/*` and the LAN security files.

- Secret-dependent comparisons are done by the AES-GCM tag check (inside `AesGcm.Decrypt`, which the platform performs in constant time) or by `CryptographicOperations.FixedTimeEquals`: the TLS pin (`CoordinatorClient`) and the file checksum (`FileSink.Complete`).
- The remaining comparisons are of public values: the protocol magic, the device id the sender named, the 4-character label, and the fixed strings `RMTL1-proof:` and `RMTL1-accepted` compared with **already decrypted, authenticated** plaintext. The device-id comparison hashes a public key.
- Not constant time and on secrets: the server's pairing-code lookup (`PairingCodes.TryConsume`) uses a dictionary lookup by the code string. Timing of a hash lookup on a 40-bit random code is not a practical oracle, and the per-address lockout limits guesses to 5 per 15 minutes **(analysis)**. Accepted.
- Pinned by `Every_equality_check_in_the_security_code_is_on_a_public_value_or_constant_time_and_new_ones_need_review`: it lists the reviewed comparisons and fails on a new one in `Crypto/`, `LanToken.cs`, `LanHandshake.cs`, `LanSecureSession.cs`, `FramedStream.cs`.

## 6. Parser fuzzing

`CryptoFuzzTests.cs` with `CryptoFuzzHarness.cs`: a deterministic, seeded generator (noise, bit flips, truncation, extension, splices, boundary-valued length fields, zeroed ranges) feeds each target; the target must accept or fail with its documented exception; accepted input gets a property check. A failure prints the seed, iteration and input. It is a property-test harness with a fixed budget (4000 inputs per target by default; some targets are capped lower because each input costs a key stretch). It is not coverage-guided: SharpFuzz was not used.

| Parser | Test |
|---|---|
| Invite | `Invite_parser_never_throws_and_only_accepts_a_usable_server_address` (includes mutated invites with a recomputed checksum) |
| Token text | `Token_parser_never_throws_and_accepts_only_twelve_alphabet_characters` |
| LAN framing | `LAN_framing_survives_hostile_streams_and_roundtrips_valid_ones`, `LAN_framing_refuses_an_oversized_length_before_allocating_or_reading_it` |
| Input codec | `Input_codec_never_throws_and_what_it_accepts_encodes_back_to_the_same_events` |
| Frame, hello, message ids | `Frame_hello_and_id_parsers_never_throw_the_wrong_exception` |
| Control JSON, relay frame | `Control_messages_and_relay_frames_never_throw_the_wrong_exception` |
| Length-prefixed fields | `Field_reader_only_ever_fails_with_a_format_error` |
| Device handshake (random and validly signed hostile contents) | `Device_handshake_messages_are_either_accepted_or_a_handshake_error`, `Validly_signed_hellos_with_hostile_contents_are_still_only_handshake_errors` |
| Token handshake | `Token_handshake_messages_are_either_accepted_or_a_handshake_error` |
| Encrypted frames | `Encrypted_frames_that_are_changed_in_any_way_are_always_refused` |
| Keys and ids | `Public_key_and_identity_parsers_fail_with_the_documented_exception_only` |
| File and clipboard messages (through a real `HostAgent`) and file names | `Hostile_file_and_clipboard_messages_never_write_outside_the_receive_folder`, `File_names_always_come_out_safe` |
| Discovery packets (responder and finder) | `The_discovery_responder_survives_hostile_packets_and_answers_only_a_well_formed_probe_for_its_label`, `The_discovery_finder_ignores_hostile_replies_and_still_accepts_a_good_one` |
| Audit lines | `Lines_that_are_not_audit_lines_are_rejected_without_an_exception` |

Run more: `REMOTELY_FUZZ_ITERATIONS=60000 dotnet test tests/Remotely.Tests -c Release --filter FullyQualifiedName~Crypto`. One run at 60000 passed on all `CryptoFuzzTests` (1 m 7 s on the machine above). The default budget is what CI runs; the "N minutes" budget in the spec is expressed as an iteration count, not minutes.

## 7. Logs never contain secrets

`CryptoLogSecretsTests.cs` plants canary values and searches everything the classes logged.

- `The_token_host_and_the_host_agent_never_log_the_token_a_guess_or_what_the_viewer_sent_after_start`: `LanHost` and `HostAgent`, two wrong guesses, one real connection, clipboard text and an uploaded file's content as canaries, and the token before and after rotation. It asserts the log really contained the connection and file lines, so it is not vacuous.
- `The_host_connection_never_logs_the_pairing_code_whether_pairing_works_or_fails`: `HostConnection` with a wrong code and with a real one.
- `The_detector_itself_finds_a_planted_secret_in_any_spelling` checks the search.
- **Not covered:** the server's `ILogger` output (the test server cannot be given a log sink without changing server code; by reading `Hub.cs` and `RemotelyServer.cs` it logs short device ids, cleaned device names and the public TLS pin, never a code or key), the Windows app's own logging, and the macOS code.
- Known leaks: T3 (`LanHost.Start`) and `Invite.ToString` (owner of `Protocol/Invite.cs`): skipped tests in `CryptoKnownIssuesTests.cs`. Also `PairRequest` and `AuthResponse` are records whose text would show the code or key if anyone ever logged a control message; nothing does today (searched for logging calls in `src`).
- Server names in logs: `Hub` logs `'{Name}'` after removing control characters and cutting to 64 characters, so a device cannot forge a log line.

## 8. Audit log format for connection events

`AuditEvent` (in `Remotely.Core.Crypto`) defines version 1 of a one-line format; nothing in the app writes it yet (consumers: A3 share host, A10 server, A18 diagnostics).

```
2026-10-16T23:46:05.123Z remotely-audit v=1 event=connection.accepted mode="lan" peer="viewer-pc" addr="192.168.1.5" detail=""
```

- Events: `connection.attempt`, `connection.accepted`, `connection.refused`, `connection.closed`, `token.issued`, `token.rejected`, `token.expired`, `pairing.succeeded`, `pairing.failed`, `device.revoked`, `consent.granted`, `consent.denied`. Names never change; new ones are appended.
- Values are quoted, `\`, `"` and every control character are escaped, and each value is cut at 200 characters, so a peer-chosen name cannot start a line or add a field. There is no field for a token, code, key or message content.
- Tests: `An_audit_line_has_the_documented_shape`, `A_name_chosen_by_another_pc_cannot_start_a_new_line_or_add_a_field`, `Every_event_kind_has_its_own_stable_name_and_survives_a_round_trip`, `Long_values_are_cut_and_random_text_always_round_trips`, `The_format_has_no_field_that_could_carry_a_secret`.

## 9. Dependency vulnerability check

Command (works for any project or for the solution file):

```
dotnet list <project-or-Remotely.slnx> package --vulnerable --include-transitive
```

Windows-only projects on a Linux machine need `EnableWindowsTargeting=true` in the environment. Run on 2026-10-16 against nuget.org (`https://api.nuget.org/v3/index.json`), per project: `Remotely.Core`, `Client`, `HostCore`, `ViewerCore`, `Server`, `Host`, `HostWindows`, `Probe`, `Tests`, `App`, `ServerManager`, `UI`: **"has no vulnerable packages given the current sources" for all twelve**
(the solution-level command reported the nine projects that restore on Linux, with the same result). This says only that nuget.org's advisory data had no match at that time; the data changes, and it covers NuGet packages only (not the .NET runtime or Windows App SDK binaries beyond their package versions).
Package versions are fixed numbers in the project files (no floating versions). A CI gate needs a workflow step, which this agent may not edit; the step to add (fails the build when the output contains a vulnerability line):

```yaml
      - name: Vulnerable packages
        run: |
          dotnet list Remotely.slnx package --vulnerable --include-transitive 2>&1 | tee vuln.txt
          if grep -qE "has the following vulnerable packages|^\s+> " vuln.txt; then echo "vulnerable packages found"; exit 1; fi
```

(The patterns are what I expect `dotnet list package --vulnerable` to print for a finding: a "has the following vulnerable packages" sentence and `>` rows. I could not produce a positive case because nothing is vulnerable, so test the step once with a known-bad package version before trusting it.)

## 10. Open findings and requests

| Owner | Finding | Evidence |
|---|---|---|
| A3 (`LanHost.cs`) | `Start()` logs the full token. Also: call `LanTokenState.Clear()` on the replaced state in `Rotate`; pass `MaxFailures` as the third constructor argument of `LanTokenState` so the strict try count follows the option (default is 3); show any approval prompt after the first authenticated message, not inside `isTrusted` (S4); a handler exception that is not in the catch filter leaves the socket undisposed. | `KnownIssue_the_start_up_line_of_the_token_host_must_not_contain_the_token` |
| A10 (`Hub.cs`, admin API) | Revoking a connected device does not disconnect it (S11). | `KnownIssue_a_device_revoked_while_connected_must_lose_its_connection` |
| Integrator / Protocol | `Invite` and the control-message records print secrets in `ToString()`. Add the vulnerability step (section 9) to the `build` workflow. `docs/protocol.md` (A0) should adopt the version rule in section 2.7. | `KnownIssue_an_invite_must_not_print_its_pairing_code` |
| A8 (`FileTransfer.cs`) | Reserved device names followed by more than one extension or a space are not renamed. Unverified on Windows. | `KnownIssue_reserved_device_names_with_several_extensions_must_be_renamed` |
| A5 / A7 (UI) | What the viewer does with host-supplied clipboard text and JPEG data (H6, H7) was not reviewed. | none |

## 11. Not verified

- Anything on real Windows or macOS beyond what the `build` and `mac` workflows ran: DPAPI behaviour and ACLs (K1), the exact exception types of the Windows and macOS crypto backends, firewall and Wi-Fi isolation.
- Passive-attacker infeasibility (L2) and all cryptographic strength claims rest on the standard primitives (P-256 ECDH and ECDSA, AES-256-GCM, HKDF-SHA256, PBKDF2-HMAC-SHA256); the platform HKDF and PBKDF2 match published vectors (`The_platform_HKDF_and_PBKDF2_match_published_test_vectors`), nothing else was independently verified.
- No third-party review or coverage-guided fuzzing has been done. The fuzz budget is small by design.
- GPU or special-hardware speed for the offline guess (section 4).
