# Remotely

A self-hosted remote desktop for Windows 11 (x64 and ARM64) and macOS (Intel and Apple silicon).
No public servers: you run the coordinator yourself. Open source. Built on .NET 10.

Status: planning. See [docs/architecture.md](docs/architecture.md).

## Build and test
Requires the .NET 10 SDK.
```
dotnet build Remotely.slnx
dotnet test tests/Remotely.Tests
```
