# UI testing and screenshots

The WinUI apps cannot be built or run on Linux, and the CI artifact download host is not reachable from every environment. So the `ui` workflow
(`.github/workflows/ui.yml`) runs the real apps on Windows x64 and ARM64 and commits the screenshots to the orphan branch **`ci-screenshots`**, where they can be read
through the GitHub contents API. The branch has one folder per run (`runs/run-<id>/<rid>/...`), a `latest/` copy, and a `README.md` index. It holds the five newest runs and is rewritten as one commit per run, so it cannot grow without bound.

## What a page check proves (`tools/ci/show-pages.ps1`, `tools/ci/show-manager-pages.ps1`)

For each page in the list the runner (`tests/Remotely.UITests`) starts the exe with `--show <Page>` and requires all of:

- the process is still running and no `app-crash.log` (Manager: `manager-crash.log`) was written;
- a visible, un-owned top-level window of the process appeared and kept its size for 600 ms;
- the UI Automation tree of that window has at least three named elements besides the caption buttons;
- no unexpected second window;
- the window screenshot is not blank (at least 6 distinct colours).

With `-StrictContent` (workflow input `strict_content`) it also requires an element whose class name or automation id is the page's class name. Whether the WinUI UIA tree exposes that is reported per page in `tree/<Page>.txt` (full tree dump) and is not assumed.

It does **not** prove the page looks right. A person or reviewer agent must look at the images. The baseline comparison only proves the picture has not changed since someone approved it.

## Flows (`tests/Remotely.UITests/Flows.cs`)

1. First run: the four onboarding steps, the typed PC name appears in the summary, Finish shows Home, `viewer.json` has `OnboardingDone` and the name.
2. Home: an incomplete token shows the "x of 12 characters" hint; a complete token nobody shows ends in a readable error (no exception text); the Share switch shows a token `XXXX-XXXX-XXXX` and the ready state, switching off shows the not-sharing state; Settings opens.

Strings are read from `src/Remotely.App/Strings/en-US/Resources.resw`, so a renamed key breaks the test instead of passing silently. Other windows or `ContentDialog`s appearing during a flow fail it.

## Variants (`tools/ci/ui-variants.txt`)

Theme dark/light (written to `viewer.json` / `manager.json` before launch), scaling 100/150/200% (`DisplayConfigSetDeviceInfo`), high contrast (`SystemParametersInfo`). If the runner refuses a change the variant is reported as `SKIPPED` with the reason in `summary.md`; check the run summary to see what actually worked. Albanian/English language variants are not present: the app has no language setting yet.

## Baselines and the diff

Baselines live in `docs/screenshots/ui/<rid>/<variant>/<Page>.png`. Text that changes between runs (IP addresses, device ids, tokens, times, version) is painted over in the compared copy; the published screenshot on `ci-screenshots` is not masked. A picture drifts when more than `max(24, 0.02% of the pixels)` pixels differ by more than 16 in some colour channel, or the size differs (`tests/Remotely.UITests/Imaging/ImageDiff.cs`, unit-tested in `tests/Remotely.Tests/ImageDiffTests.cs`). A missing baseline is reported as NEW and does not fail.

Updating baselines: run the `ui` workflow manually (`workflow_dispatch`) on the branch with `update_baseline` ticked; the `baselines` job commits the new images to that branch. Nothing else writes baselines.

## Running locally (Windows)

```
dotnet publish src/Remotely.App -c Release -r win-x64 -p:Platform=x64 -o out/app-win-x64
pwsh tools/ci/run-ui.ps1 -Rid win-x64 -App out/app-win-x64/Remotely.exe -Out ui-out
```
The runner overwrites `%LOCALAPPDATA%\Remotely\viewer.json` during the run (a backup is restored afterwards) and changes display scaling, so use a spare machine or VM. On Linux the project only compiles with `-p:EnableWindowsTargeting=true`.
