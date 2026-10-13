# Remotely

Your own remote desktop: Windows 11 (x64 and ARM64) and macOS (Intel and Apple silicon), built on .NET 10, with **no public servers**.
You run a small server yourself; every connection is end-to-end encrypted between your own devices. The Windows apps use Portico's theme.

- **Set it up:** [docs/user-guide.md](docs/user-guide.md)
- **How it works and what has been verified:** [docs/architecture.md](docs/architecture.md)
- **Check it on your own PCs (20 minutes):** [docs/verify-on-your-pc.md](docs/verify-on-your-pc.md)
- **Open source:** MIT licence (see `LICENSE`).

| Part | What it is |
|---|---|
| `src/Remotely.Server` | The coordinator and relay you run on your always-on PC or VM. Windows service, pinned self-made TLS key. |
| `src/Remotely.Host` | Windows host: SYSTEM service plus a helper in your session (survives sign-out, sees the lock screen), or a no-administrator per-user mode. |
| `src/Remotely.Viewer` | The Windows viewer (WPF, Portico theme). |
| `mac/` | The Mac host, viewer and command-line viewer (Swift, universal binaries). |
| `src/Remotely.Core`, `Client`, `HostCore`, `ViewerCore` | The shared protocol, crypto and session logic. |
| `installer/` | Inno Setup scripts for the server and the client. |

## Build and test
```
dotnet build Remotely.slnx
dotnet test tests/Remotely.Tests          # 58 tests; runs the real server over real sockets
build\publish.ps1                         # on Windows with Inno Setup: all installers into publish\
mac/build.sh                              # on a Mac: universal apps and a .dmg
```
CI (`.github/workflows`) builds everything and runs the whole chain on real Windows x64 and ARM64 machines and a Mac.
