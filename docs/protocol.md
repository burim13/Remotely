# Remotely: protocol version table

Messages inside the encrypted viewer/host session start with one kind byte. Other agents append their own sections below the capture section.
Rule for every wire change: it gets a version number here and a compatibility rule, and a test with a fake peer of the previous version.

## Id ranges

| Range | Direction | Owner |
|---|---|---|
| 0x01-0x14 | viewer to host | version 1 (frame request, input, clipboard, secure attention, files) |
| 0x20-0x2F | viewer to host | A6 capture engine |
| 0x80-0x94, 0xFF | host to viewer | version 1 |
| 0xA0-0xAF | host to viewer | A6 capture engine |

## Capture engine (A6)

### Version 2: tiles, monitors, cursor

**Compatibility rule.** The hello's version byte stays `1` forever (version 1 viewers, including the macOS app, refuse any other value). Version 2 is announced
by an extension after the capability byte, which version 1 viewers ignore: a version 1 viewer reads exactly `0x80, 1, capabilities`.
A version 2 viewer treats a hello without the extension as a version 1 host. A version 2 host sends tiles only to a viewer that asked for them with `0x20`;
a viewer that never does gets the version 1 full-frame JPEG (`0x81`/`0x82`) as before. A viewer sends `0x20` or `0x21` only when the hello advertised
`Monitors`; a host whose capture source cannot tile answers `0x20` with the usual `0xFF` error.

Hello: `0x80, 1, caps, 2, monitorList`. Capability bits added to hosts that support this: `Monitors` (16: extension, `0x20`, tiled frames, `0xA3`) and
`CursorShape` (32: `0xA1`, `0xA2`).

Integers are big-endian.

| Id | Dir | Body | Meaning |
|---|---|---|---|
| 0x20 StreamConfig | V to H | flags(1: 1 tiles, 2 cursor) quality(1: 0 = host default, else 10..95) monitor(i8: -1 whole virtual desktop, else display index) | Switch on tiled frames, pick a display, ask for a JPEG quality. May be sent again at any time. A change of tiles-flag or display makes the next frame a full picture. |
| 0x21 KeyFrameRequest | V to H | none | Next frame reply is a complete picture. |
| 0xA0 TileFrame | H to V | flags(1: bit0 full picture) monitor(u8, 0xFF = whole desktop) width(i32) height(i32) desktopLen(1) desktop captureUs(u32) encodeUs(u32) tileCount(u16) then per tile: x,y,w,h (u16 each) len(u32) jpeg | Answers a `0x01` request. A full picture is exactly one tile covering the whole area. Otherwise each tile is painted over the previous picture. captureUs/encodeUs are the host's time for this update. |
| 0xA1 CursorShape | H to V | id(u64) hotspotX(u16) hotspotY(u16) width(u16) height(u16) BGRA straight alpha | Pointer image, sent once per image id per viewer (max 256 x 256). |
| 0xA2 CursorState | H to V | visible(1) shapeId(u64) x(i32) y(i32) | Pointer position in virtual-desktop pixels. Sent before the frame or `0x82` reply of the same request, only when it changed. |
| 0xA3 MonitorList | H to V | monitor list | The displays changed since the hello. |

Monitor list: `count(1)` then per display `index(1) primary(1) x(i32) y(i32) width(i32) height(i32) nameLen(1) name`. x and y are virtual-desktop pixels (the primary
display is at 0,0; others may be negative). Mouse input stays normalised 0..65535 across the whole virtual desktop, so a viewer showing one display maps
its pointer with these rectangles.

Replies still complete the viewer's request in the same way: `0xA0` or `0x82` (or `0xFF`) ends one `0x01` request; `0xA1`-`0xA3` do not.

Host behaviour behind the wire (not visible to viewers): the capture source keeps, per viewer, a copy of the last picture that viewer was sent and compares
64 x 64 pixel tiles exactly (no hashing, so a missed update cannot persist). Changed tiles are merged into rectangles; if more than half the screen changed,
or the display, desktop or size changed, a single full-size JPEG is sent instead. Output is capped below the 1 MiB relay limit by lowering quality, and by
falling back to a full picture.

Helper pipe (service to helper, not seen by viewers): a Capture request with a 16 byte payload ending in `0x54` is a tile request (`FrameWire.EncodeRequest`);
an empty payload is the version 1 call and returns `FrameCodec` bytes as before.

Tests: `tests/Remotely.Tests/FrameTests.cs` (wire formats, malformed input, a decoder copied from version 1 reading the version 2 hello, a hand-written version 1
viewer against a version 2 host, a hand-written version 1 host against a version 2 viewer, tile reconstruction under 150 random edits).
