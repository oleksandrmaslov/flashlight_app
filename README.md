# Iskra

Production Windows flashing tool for ARM Cortex-M targets supported by Black
Magic Probe. It drives the probe through `arm-none-eabi-gdb`, validates signed
firmware catalog metadata, and logs every flash attempt to SQLite.

> **Status:** WPF app, CLI, catalog/signature flow, logging, and installer are in place.

## Repository layout

```text
Iskra.sln
src/
  Iskra.Core/       Class library: services, state machine, models
  Iskra.Cli/        Console flasher
  Iskra.Wpf/        Operator UI
tests/
  Iskra.Core.Tests/ xUnit tests for the Core library
installer/
  Product.wxs       App MSI
  Bundle.wxs        Factory setup bundle
```

## Prerequisites

- Windows 10 / 11
- .NET 8 SDK for development builds
- ARM GNU Toolchain for development runs, unless using the setup bundle
- A Black Magic Probe attached to the target board

## Installer

Build the factory installer bundle:

```powershell
pwsh ./installer/build-installer.ps1 -Version 1.2.3
```

Use `installer/out/Iskra-<ver>-setup-x64.exe` on operator stations. It checks
for Windows 10/11 x64, detects an existing `arm-none-eabi-gdb.exe`, and installs
the embedded Arm GNU Toolchain 15.2.rel1 before Iskra when GDB is missing. The
sibling `Iskra-<ver>-x64.msi` is app-only: it uses an MSI-native x64 Windows
compatibility check and blocks a fresh install if `arm-none-eabi-gdb.exe` is not
already installed. Use the setup EXE for new factory PCs.

The build also emits `installer/out/Iskra-<ver>-preinstall-check.ps1`. Run it
before setup on a new station:

```powershell
powershell.exe -ExecutionPolicy Bypass -File .\Iskra-1.2.3-preinstall-check.ps1
```

After setup, run:

```powershell
"C:\Program Files\Iskra\Iskra.Cli.exe" --doctor
```

### Station checklist

Pre-install:

- [ ] Windows 10/11 x64.
- [ ] At least 3 GB free on the system drive.
- [ ] Installer can run elevated as administrator.
- [ ] `Iskra-<ver>-setup-x64.exe` is present.
- [ ] Black Magic Probe is available for final station acceptance.
- [ ] GitHub/network access is available if firmware comes from private GitHub releases.

Installer installs:

- [ ] Setup EXE: checks Windows 10/11 x64 before install.
- [ ] Setup EXE: installs Arm GNU Toolchain 15.2.rel1 when `arm-none-eabi-gdb.exe` is missing.
- [ ] MSI: blocks app install when `arm-none-eabi-gdb.exe` is missing.
- [ ] Iskra WPF app and `Iskra.Cli.exe`.
- [ ] Bundled `examples/catalog.json` and `examples/catalog.json.sig`.
- [ ] Installed `check-station.ps1` for later diagnostics.

Post-install acceptance:

- [ ] `Iskra.Cli --doctor` reports no failures.
- [ ] `Iskra.Cli --login` succeeds if private GitHub firmware is used.
- [ ] The WPF status strip shows `gdb` found and one BMP GDB COM port.
- [ ] A known-good board flashes once before handing the station to operators.

## Build

```powershell
$env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;$env:PATH"
dotnet build
```

## Run the CLI

```powershell
dotnet run --project src/Iskra.Cli -- `
  --catalog examples/catalog.json `
  --product ci-clop `
  --elf .\path\to\app.elf `
  --port \\.\COM30 `
  --power probe `
  --freq 1000000 `
  --connect-reset `
  --operator jdoe `
  --batch Lot-2026-05-25-A
```

## Test

```powershell
$env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;$env:PATH"
dotnet test
```

Detailed design and sprint status live in `CLAUDE.md`.

## License

TBD.
