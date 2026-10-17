# Remotely: capture and streaming performance

Everything here is either a number copied from a CI log (run id given) or marked as not measured. The CI machines are headless GitHub-hosted Windows
runners; they have no GPU and no real monitor, so these numbers say the code paths work and give a floor, not what a person sees on real hardware.

## What the engine does (protocol version 2, see docs/protocol.md)

- Capture: DXGI Desktop Duplication per display, GDI `BitBlt` as fallback and for the whole desktop of several displays. The path that ran is written to the host log
  (`Capture path: DXGI (...)` / `GDI (...)`), and if DXGI fails the reason is logged once and DXGI is retried after 15 s. `REMOTELY_CAPTURE=gdi` turns DXGI off.
- With DXGI, "Windows has no new frame" costs one call and no pixel copy; the viewer is answered "unchanged" without comparing anything.
- Per viewer, 64 x 64 pixel tiles are compared exactly with the last picture that viewer was sent; only changed rectangles are JPEG-encoded (in parallel).
  More than half the screen changed, or a new size/desktop/display: one full-size JPEG.
- The pointer image and position travel in their own messages, only when they change.
- Quality follows measurement: the viewer measures round trip (replies without pixels), bandwidth (reply bytes over time minus round trip and the host's reported
  work) and moves JPEG quality between 25 and 85; at quality 25 and still too slow it spaces requests up to 250 ms apart.
  Unit-tested with synthetic samples (`Quality_falls_on_a_slow_link_and_recovers_on_a_fast_one`); not yet tried on a real slow link.
- One request is outstanding at a time (as in version 1), so the frame rate on a link is bounded by round trip plus the host's work. Pipelining requests is not done.

## Measured on CI (build run 38984819333, commit 4454289, smoke job, host log lines)

Both runners report `Capture path: DXGI (Desktop Duplication on Microsoft Basic Render Driver / \\.\DISPLAY1)`, so Desktop Duplication works on these headless
runners (through the software adapter) in user mode and in service mode, and also on the `Winlogon` desktop on x64. A fallback to GDI was not triggered in this run,
so **the DXGI to GDI fallback was not exercised on CI** (the legacy full-frame path does run GDI on every CI session and is green, but the tile path's own fallback decision has no CI evidence).

| Runner | Situation | grab ms | diff+encode ms | result |
|---|---|---|---|---|
| x64, user mode | first frame (DXGI initial frame) | 37.3 | 20.4 | key frame 52.6 KB |
| x64, service | first frame | 29.2 | 4.3 | key frame 12.6 KB |
| x64, service | lock screen (Winlogon) | 7.6 and 1.2 | 2.2 and 4.9 | key frames 12.6 and 33.5 KB |
| ARM64, user mode | first frame | 47.4 | 7.7 | key frame 43.5 KB |
| ARM64, service | first frame | 14.3 | 4.2 | key frame 42.9 KB |
| ARM64, service | idle desktop, 10.1 s | 0.0 average | 0.0 average | 164 of 164 requests answered "unchanged" |

The first-frame grab includes creating the duplication. Frame sizes depend on what the runner's desktop showed at that moment.

## Not measured (and why)

- **Moving window, frames per second, bytes per partial frame, and old path against new path.** The smoke job only requests one or two frames per watch, and
  the workflow files are not editable by this work. `Remotely.Probe bench <host> --seconds 8 --activity` (src/Remotely.Probe/Bench.cs) prints
  `BENCH` lines for an idle desktop and a moving PowerShell window (fps, bytes per frame, host grab and encode ms, round trip) and `--legacy` measures the
  version 1 path for comparison. It needs a workflow step to run on CI (see the report's request to the integrator). It has been compiled but not run.
- **Hardware H.264 through Media Foundation.** Not built. The task allowed it only if a CI measurement shows it beating tiled JPEG on both x64 and ARM64. The CI
  runners have no GPU (the adapter above is the software renderer), so a hardware encoder could not be measured there at all, and no software-encoder measurement was
  made. Decision, therefore: **tiled JPEG stays**, because nothing measured supports a change, not because H.264 was shown to be slower.
- Real monitors, several displays, rotated or HDR displays (DXGI is refused for these and GDI is used, by design; untested on hardware), high-DPI scaling, slow links.
