# Localisation and accessibility

Languages: English (en-US, the default) and Albanian (sq-AL). The Albanian is machine-written and has not been reviewed by a speaker; `docs/albanian-review.md` lists every string next to its English original for that review.

## Where the text lives

- `src/Remotely.App/Strings/<language>/Resources.resw` and `src/Remotely.ServerManager/Strings/<language>/Resources.resw`. Keys look like `Area_Name`. `{0}`, `{1}` are `string.Format` placeholders and must be identical in every language.
- Code reads a string with `Loc.Get("Key")` (`Remotely.UI.Localization`); the Server Manager wraps it as `Ui.T`, the setup wizard as `T`/`F`. A missing key shows the key itself, never a crash.
- Shared controls in `src/Remotely.UI` look their keys up in whichever app hosts them, so a key such as `Banner_Dismiss` must exist in every app that shows the control (today only Remotely.App does).
- Library projects (`Remotely.Core` and others) may return resource keys (state machines, validators); both apps' tables are checked for them.

## Adding a string

1. Add the key to `en-US` and `sq-AL` (and use it). Do not put text in XAML or in code.
2. Run `python3 tools/loc/pseudo.py` to refresh the pseudo-locale.
3. Run `python3 tools/loc/albanian-review.py` to refresh the review sheet (after committing, because it reads the author of each line from git blame).
4. `dotnet test tests/Remotely.Tests -c Release --filter FullyQualifiedName~Localization` must pass.

## The checks (all run on Linux, no Windows needed)

| Test class | What it proves |
|---|---|
| `LocalizationScannerTests` | Every literal in XAML (`Text`, `Content`, `Header`, `PlaceholderText`, `ToolTipService.ToolTip`, `AutomationProperties.Name`, ...) and in code (assignments to those properties, `SetName`, toasts, banners, dialog titles, builder helpers, and any other sentence-like literal) is a resource key or is listed with a reason in `tools/loc/allow-literals.txt`. A stale allow-list entry fails. |
| `LocalizationParityTests` | Same keys and placeholders in en-US, sq-AL and qps-ploc; values are valid format strings; no empty or duplicate keys; every key the code asks for exists. Printed, not failing: unused keys, Albanian values identical to English. |
| `LocalizationPseudoTests` | The committed qps-ploc files are derived from the current English (accents stripped give back the English), at least 40% longer, every letter accented, placeholders untouched. |
| `AccessibilityTests` | Static analysis of the XAML and code, see below. |
| `LocalizationDocsTests` | `docs/albanian-review.md` is current. |

Limits of the scanner: it reads text, it does not understand types. A literal built by concatenation in a place no rule looks at, or a one-word literal passed to an unlisted helper, is not found. Messages returned by library code (for example the problem text of `Invite.TryParse` in `Remotely.Core`) are English only; localising them needs error codes in the library and is not done.

## Pseudo-localisation

`tools/loc/pseudo.py` writes `Strings/qps-ploc/Resources.resw` for both apps: `Save` becomes `[Šåvé ‹lörém›]`. Unaccented text on screen is text that bypassed the resources; clipped text shows where a language 40 to 100% longer will not fit. Windows picks the folder when the display language list contains `qps-ploc`; a language picker inside the app does not exist yet. Whether the build embeds the pseudo-locale and Windows resolves it has to be seen on a Windows machine; the tests only check the files. `python3 tools/loc/pseudo.py --check` exits 1 when the committed files are out of date.

## Accessibility

Static checks (`AccessibilityTests`, exceptions in `tools/loc/allow-a11y.txt`):

- every interactive control (buttons, text boxes, toggles, combo boxes, selectable lists, menu items, token boxes) has `AutomationProperties.Name`, visible text, a `Header`, `LabeledBy`, or is named in the code-behind;
- controls built in code have `Content`, `Header` or a `SetName`;
- no `UseSystemFocusVisuals="False"`, no `IsTabStop="False"` on a control, no positive `TabIndex` (tab order is the layout order);
- no `Tapped`/`PointerPressed` handler on an element that cannot take keyboard focus (in XAML), and each `+=` pointer handler in code is listed;
- no hard-coded colours (a fixed colour ignores Windows high contrast); `Palette` returns the system text colour in high contrast;
- no `IsTextScaleFactorEnabled="False"`, no fixed `Height` on text controls;
- animations only through `Remotely.UI.Motion`, which turns them off when Windows animations are off;
- both main windows declare a Navigation and a Main landmark.

Run-time check: `Remotely.exe --uia-scan result.txt [pages.txt]` (see `docs/cli.md`) opens each page and lists interactive controls whose automation peer reports an empty name. It sees only what exists after the page loaded. It has not been run in CI yet: it needs a step in `build.yml` (integrator).

Not covered by any check: what Narrator actually reads, contrast of text with `Opacity` below 1 in high contrast, text clipping at 200% (needs the pseudo-locale screenshots at 200% scaling from the UI test pipeline), right-to-left layout (neither language is right-to-left), and the order in which Narrator visits landmarks.
