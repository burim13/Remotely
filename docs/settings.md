# Settings, address book and policies

Written by A14. Code: `src/Remotely.Core/Storage/**` (tested on Linux), `src/Remotely.App/Views/Settings/**` (WinUI, built and photographed only by CI).

## Where things are stored (`%LOCALAPPDATA%\Remotely`)

| File | Holds | Owner of the class |
|---|---|---|
| `viewer.json` | name, theme, language, server address and pin, shell options, clipboard and keyboard choices, window position | `ViewerSettingsData` (Core), `ViewerSettings` (App) |
| `share-settings.json` | "Ask me before someone connects", "Let remembered PCs reconnect" | `ShareSettings` (A3) |
| `transfers.json` | receive folder, size limits, unpack folders | `FileTransferSettings` (A8) |
| `address-book.json` | saved PCs | `JsonAddressBook` |
| `recent-pcs.json` | old recent list, read once into the address book if `address-book.json` does not exist yet | `JsonRecentPcs` |

`viewer.json` and `address-book.json` carry `"SchemaVersion"` as their first property. A file without it is version 0 (what 0.3.0 and 0.4.0 wrote).
`share-settings.json` and `transfers.json` are not versioned by this work (they belong to other agents).

## File safety

- Writes go to `<file>.tmp`, are flushed to disk, then moved over the real file.
- An older file is migrated in memory, the original is copied once to `<file>.v<N>.bak` (never replaced by a later copy), and the new version is written on the next save.
- A file from a newer Remotely is read as far as understood and copied to `<file>.v<N>.bak` before anything is overwritten.
- A file that is not valid (not JSON, wrong value types) is moved to `<file>.damaged` and defaults are used. The Advanced page says so.
- A write that fails (disk full, locked) never throws into the UI; the change lasts until the app closes and the page says so.

## Migration from 0.3.0 and 0.4.0

The settings class is the same six values in the source of both (commits 8cc392b, d8e4166, 0d0e4dd), serialised together with the read-only `IsConfigured`. Schema 1 drops `IsConfigured`; every other key added since is optional with a default. `StorageTests.The_viewer_json_of_older_versions_still_loads` loads a sample that a test proves is what that class wrote. Only the tag `v0.2.0` exists in git, so which commit shipped as 0.3.0 or 0.4.0 was not checked beyond the identical source.
An older Remotely reading a newer `viewer.json` ignores the keys it does not know (tested with the old class shape).

## Settings pages

General, Sharing, Connection, Security, Appearance, Advanced, About and Address book, with a search box (every word must appear in a setting's name or help text). Each catalogued setting (`SettingsCatalog`) has an id, a default, limits, a label and a help string (`Settings_<Id>_Label` / `_Help` in both languages) and a test value; tests fail if one is missing.

| Setting | Section | Default | Stored in |
|---|---|---|---|
| DeviceName | General | Windows computer name | viewer.json |
| StartWithWindows | General | off | viewer.json and the Run registry value |
| CloseToTray | General | off | viewer.json |
| Language (system, en-US, sq-AL) | General | system | viewer.json, applied at next start |
| AskBeforeConnect | Sharing | on | share-settings.json |
| AllowRemembered | Sharing | on | share-settings.json |
| ShareOnStart | Sharing | off | viewer.json |
| KeyboardMode (auto, scancode, unicode) | Connection | auto | viewer.json |
| SyncClipboard | Connection | on | viewer.json |
| FileTransferEnabled | Connection | on | viewer.json |
| ReceiveFolder | Connection | Downloads | transfers.json |
| MaxItemMegabytes | Connection | 32768 | transfers.json |
| ReceiveQuotaMegabytes | Connection | 0 (no limit) | transfers.json |
| ExtractFolders | Connection | on | transfers.json |
| ClipboardConfirmMegabytes | Security | 8 | viewer.json |
| Theme | Appearance | system | viewer.json |

Not wired by this work (see the report): nothing in the session code reads `KeyboardMode`, `ClipboardConfirmMegabytes` or `FileTransferEnabled` yet.

Export writes only these catalogued, portable settings (not the server, pin, pairing, device key, window position, first-run state, PC name or address book). Import skips unknown or invalid values and values an administrator forces, and says how many. Reset puts every catalogued setting back to its default and leaves the server, pairing, first-run state, window position and address book.

## Address book

Entries: name, address or device id, group, notes, favourite, colour tag (red, amber, green, blue, gray, drawn with theme brushes and also written as a word), last used. No token or secret is stored. Two entries cannot share an address. Import/export is a JSON file (`"Format": "remotely-address-book"`); import merges by address, up to 2000 entries, and changes nothing if the file is not an export. The Connect screen's "recent PCs" are the entries that have a last-used time (`AddressBookRecentPcs`).

## Policies (managed PCs)

Read from `HKLM\Software\Policies\Remotely` (64-bit view), DWORD values; only a non-zero DWORD turns a policy on. Template: `docs/policy/Remotely.admx` and `docs/policy/en-US/Remotely.adml`; copy them to `C:\Windows\PolicyDefinitions` (and `...\en-US`) or a central store.

| Value | Effect |
|---|---|
| `DisableServerMode` | The server block in Settings is replaced by a message. |
| `ForceConsent` | "Ask me before someone connects" shows on and locked; import skips it. |
| `DisableFileTransfer` | "Allow file transfer" shows off and locked; import skips it. |

Honest limits: the settings page shows and locks these. Enforcement in the code that connects, shares and transfers is in other agents' files and is listed as a request to the integrator. The ADMX is checked as XML (names, values, string ids against the ADML); it has not been loaded in Group Policy tooling.
