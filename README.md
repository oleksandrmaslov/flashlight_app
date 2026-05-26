# Iskra

Production flashing tool for PY32F0xx-based flashlight firmware. Drives a
Black Magic Probe via `arm-none-eabi-gdb` and logs every flash attempt.

> **Status:** Sprint 1 scaffold. Not yet functional.

## Repository layout

```
Iskra.sln
src/
  Iskra.Core/       Class library — services, state machine, models
  Iskra.Cli/        Console flasher (Sprint 1 deliverable)
tests/
  Iskra.Core.Tests/ xUnit tests for the Core library
```

WPF UI lands in Sprint 4 as `src/Iskra.Wpf/`.

## Prerequisites

- Windows 10 / 11
- .NET 8 SDK (`dotnet --version` reports 8.x)
- ARM GNU Toolchain — `arm-none-eabi-gdb` on PATH or at the default install
  location (`C:\Program Files (x86)\Arm GNU Toolchain arm-none-eabi\<ver>\bin`)
- A Black Magic Probe attached to the target board

## Build

```powershell
dotnet build
```

## Run the CLI (Sprint 1)

Sprint 1 target — flashing a single device end-to-end from the command line:

```powershell
dotnet run --project src/Iskra.Cli -- `
  --elf .\path\to\app.elf `
  --port \\.\COM30 `
  --power probe `
  --freq 1000000 `
  --connect-reset `
  --product ci-clop `
  --operator jdoe `
  --batch Lot-2026-05-25-A
```

The current `Program.cs` is a stub that parses the option surface only.
Real gdb subprocess + state machine + SQLite logging arrive in the next commit.

## Test

```powershell
dotnet test
```

## Roadmap

| Sprint | Deliverable |
|---|---|
| 1 | Console flasher; reproduces `make flash-bmp` end to end + SQLite log |
| 2 | Catalog + cache (signed manifest, SHA-256 verify, sideload mode) |
| 3 | GitHub App auth + private firmware download |
| 4 | WPF UI, batch locking, CSV export, installer |

Detailed design lives in the firmware repo's session notes.

## License

TBD.
