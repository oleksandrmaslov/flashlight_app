# CLAUDE.md — FlashlightApp

Session handoff. Read this fully at the start of any new Claude session in this repo.

---

## Goal

Production Windows flashing tool for any ARM Cortex-M target supported by
Black Magic Probe (PY32, STM32, NXP, GD32, etc.). Drives the probe via
`arm-none-eabi-gdb`. Designed to mass-flash ~500 devices per batch from
non-developer factory operators. Firmware is fetched from private GitHub
repos; each release carries the target stack metadata (BMP target match
string + flash size + display part number) so the app can verify the right
firmware is paired with the right hardware. Every attempt is logged to SQLite.

**UI language: Ukrainian only** (operator-facing strings in WPF and CLI).
Log payloads, error codes (`E_*`), and developer-only diagnostics stay
ASCII / English.

The full architecture proposal lives in the firmware repo's prior session
transcript. This file is the *condensed* working copy of those decisions.

---

## Status snapshot (2026-05-25)

### Done

- Architecture proposal complete (catalog, GitHub auth, state machine, error
  codes, security, MVP plan, UI screens).
- Sibling repo cloned at `C:\Users\Alexandr\flashlight_app\`.
- .NET 8 SDK installed **per-user** at
  `C:\Users\IMT - Teilnehmer\AppData\Local\Microsoft\dotnet\` (not on system PATH).
- Solution scaffolded — `FlashlightApp.sln` with three projects, builds clean.
- CLI stub (`FlashlightApp.Cli`) parses the planned option surface; does NOT
  flash yet.
- Initial commit `5e26828` pushed to `origin/main`.
- Empty `tests/FlashlightApp.Core.Tests/` ready for content.

### Not yet done (Sprint 1, in order — one commit per chunk)

1. **`GdbProcess` wrapper** in `FlashlightApp.Core` — spawns
   `arm-none-eabi-gdb`, parses stdout line-by-line into events. Generic:
   takes COM port + power + frequency + connect-reset; nothing PY32-specific.
2. **`FlashStateMachine`** — 7 states (IDLE → PREPARING → PROBE_CHECK →
   ATTACH → LOADING → VERIFYING → FINALIZING → PASS/FAIL → LOGGED → IDLE),
   emits typed outcomes with `error_code`. Accepts an expected
   `bmp_target_match` string; verifies `swdp_scan` output contains it
   (mismatch → `E_TARGET_MISMATCH`).
3. **`SqliteLogStore`** — single-row append per attempt; schema below.
4. **CLI glue** — `Program.cs` runs the state machine, prints PASS/FAIL,
   writes the log row.
5. **xUnit tests** — `FlashOptions.Parse` cases + state-machine transitions
   driven by fixture gdb output (PY32 + at least one STM32 sample).

**Sprint 1 done = 50 PASS in a row on the bench with the BMP + the PY32
target (acceptance hardware). Code stays target-agnostic — no PY32 strings
in core logic.**

### Beyond Sprint 1

| Sprint | Deliverable |
|---|---|
| 2 | Signed `catalog.json` + local cache (SHA-256 verify, sideload-from-folder, atomic downloads) |
| 3 | GitHub App + Device Flow auth, refresh token in DPAPI LocalMachine, private firmware download |
| 4 | WPF UI (5 screens: Home / Flash / History / Catalog / Settings), batch locking, CSV export, WiX installer chaining ARM toolchain MSI |

---

## Design decisions locked in (don't relitigate without reason)

- **Stack:** .NET 8 + WPF, single-file self-contained `.exe`. (User said "use
  any language" but committed to .NET — switching now costs days.)
- **gdb shipping:** chained full ARM toolchain MSI inside our installer
  (NOT vendoring just `gdb.exe`). App detects existing install at the
  standard ARM path; prompts to re-run installer if missing.
- **Target support:** any MCU that Black Magic Probe's `swdp_scan` recognises.
  Code in `FlashlightApp.Core` MUST stay target-agnostic — no PY32 strings
  in the state machine, gdb wrapper, or log writer. The catalog entry per
  product declares the target stack; the app verifies a match at flash time.
- **Catalog target descriptor** (per product entry, finalised in Sprint 2):
  - `bmp_match` — substring expected in `monitor swdp_scan` output
    (e.g. `"PY32F002A"`, `"STM32F103"`). Used by the state machine to
    fail fast with `E_TARGET_MISMATCH` if wrong board is plugged in.
  - `flash_kb` — flash size in KB. Drives the timeout budget and a
    sanity check against the elf section sizes.
  - `part_number` — display string shown to operators (`"PY32F002Ax5"`).
- **UI language:** Ukrainian only. No i18n framework; strings hardcoded in
  WPF and CLI. Error codes (`E_*`) stay English / ASCII; the UI maps each
  to a Ukrainian hint line.
- **MVP bench target:** `pocket-light` product, PY32F002Ax5 board. This is
  the *acceptance test* for Sprint 1, NOT a hardcoded assumption in code.
- **Operator identity:** free-text dropdown at app start, stored per-station.
- **Trust root:** signed `catalog.json` (Ed25519, public key embedded in
  exe). Firmware integrity = SHA-256 from the signed catalog.
- **GitHub auth:** GitHub App + Device Flow. Refresh token in Windows DPAPI,
  `LocalMachine` scope. No PATs.
- **Logging:** SQLite per station; CSV export per batch.
- **Production safety:** batches lock the firmware version. The app NEVER
  auto-updates firmware silently during a batch.
- **Hardware-in-the-loop testing** is available — use it from day one of
  Sprint 1 implementation. Don't build to a mock.

### Exact gdb invocation the state machine must reproduce

Sourced verbatim from the firmware repo's `rules.mk:135-137`:

```
arm-none-eabi-gdb.exe -nx --batch
  -ex "set confirm off"
  -ex "set pagination off"
  -ex "target extended-remote \\.\COM30"
  [-ex "monitor tpwr enable"]            # when --power probe
  -ex "monitor frequency 1000000"
  [-ex "monitor connect_rst enable"]     # when --connect-reset
  -ex "monitor swdp_scan"
  -ex "attach 1"
  -ex "load"
  -ex "compare-sections"
  -ex "kill"
  -ex "quit"
  "<path-to-elf>"
```

Wall-clock timeout: scaled to flash size, floor 15 s. For Sprint 1 use a
flat 15 s (PY32F002A flash is 32 KB; anything slower means BMP/USB trouble).
For larger MCUs the budget becomes `max(15s, 5s + flash_kb * 0.4s)` —
revisit during Sprint 2 once the catalog `flash_kb` field is wired.
Failure code on timeout: `E_TIMEOUT`.

### SQLite schema

```sql
CREATE TABLE flash_attempts (
  id              INTEGER PRIMARY KEY AUTOINCREMENT,
  ts_utc          TEXT    NOT NULL,
  operator        TEXT    NOT NULL,
  station_id      TEXT    NOT NULL,
  batch_id        TEXT    NOT NULL,
  product_id      TEXT    NOT NULL,
  firmware_version TEXT   NOT NULL,
  firmware_sha256 TEXT    NOT NULL,
  target_bmp_match TEXT   NOT NULL,   -- expected from catalog (e.g. "PY32F002A")
  target_detected TEXT,                -- raw swdp_scan match line we picked
  target_flash_kb INTEGER NOT NULL,    -- from catalog
  com_port        TEXT    NOT NULL,
  probe_serial    TEXT,
  power_mode      TEXT    NOT NULL,
  connect_rst     INTEGER NOT NULL,
  bmp_frequency_hz INTEGER NOT NULL,
  result          TEXT    NOT NULL,
  error_code      TEXT,
  error_message   TEXT,
  duration_ms     INTEGER NOT NULL,
  gdb_tail        TEXT
);
```

### Error code taxonomy

`E_PROBE_NOT_FOUND`, `E_PROBE_BUSY`, `E_SCAN_NO_TARGET`, `E_TARGET_MISMATCH`,
`E_ATTACH_FAILED`, `E_LOAD_FAILED`, `E_VERIFY_MISMATCH`, `E_TIMEOUT`,
`E_GDB_CRASHED`, `E_FW_HASH_MISMATCH`. Each maps to a one-line **Ukrainian**
operator hint in the UI (table lives in `FlashlightApp.Core/ErrorHints.cs`
once written).

---

## Repository layout

```
FlashlightApp.sln
src/
  FlashlightApp.Core/        Class library — services, state machine, models
    FlashOptions.cs
  FlashlightApp.Cli/         Console flasher (Sprint 1)
    Program.cs               (current stub: arg parsing only)
tests/
  FlashlightApp.Core.Tests/  xUnit (empty)
```

WPF project lands in Sprint 4 as `src/FlashlightApp.Wpf/`.

---

## Environment quirks (this lab machine)

- Windows user is `IMT - Teilnehmer`, but project files live under
  `C:\Users\Alexandr\` — two different profiles on one box.
- `dotnet` is **not** on the system PATH. At the start of any PowerShell
  tool call that needs `dotnet`, prepend:
  ```powershell
  $env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;$env:PATH"
  ```
- Git config locally has `user.email = oleksandr.tidal@gmail.com`. The
  intended GitHub author email is `oleksandrmaslov08@gmail.com`. Either:
  - one-time fix: `git config user.email "oleksandrmaslov08@gmail.com"`
  - or pass `--author="Oleksandr Maslov <oleksandrmaslov08@gmail.com>"` per commit.
- No `winget` available.
- Firmware repo is sibling: `c:\Users\Alexandr\py32f0-template-project\`.
- BMP + target board: available for hardware-in-the-loop from any session
  that needs to do real flashing (confirm with the user at session start).

---

## Build / run / test

```powershell
$env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;$env:PATH"
dotnet build
dotnet test
dotnet run --project src/FlashlightApp.Cli -- --help
```

The current CLI stub accepts the full Sprint 1 option surface and just
echoes parsed values — it does not invoke gdb yet.

---

## Coordination with the firmware repo

The app flashes firmware released from any product repo (`pocket-light-firmware`
is the first) via GitHub Releases. Release asset naming convention:

```
<product-id>_v<X.Y.Z>_<part-number>.elf
<product-id>_v<X.Y.Z>_<part-number>.elf.sha256
target.json                              # one per release
```

Examples:
- `pocket-light_v1.0.0_PY32F002Ax5.elf`
- `headlamp_v2.1.0_STM32F103C8.elf`

`target.json` (uploaded as a release asset) declares the target stack so the
catalog generator and the app can verify firmware ↔ hardware pairing:

```json
{
  "product_id":   "pocket-light",
  "version":      "1.0.0",
  "part_number":  "PY32F002Ax5",
  "bmp_match":    "PY32F002A",
  "flash_kb":     32,
  "elf_sha256":   "<hex>"
}
```

If you change the firmware build artefact, rename releases, or omit
`target.json`, the catalog parser here breaks. Coordinate before renaming.

A separate `firmware-catalog` repo (TBD) will hold the signed `catalog.json`
asset (an aggregation of per-product `target.json` files) and is the single
source of truth for what operators can flash.

---

## How to be a useful assistant on this codebase

### What's been working — keep doing

- **Recommend one option clearly.** Don't just list trade-offs. The user
  wants direction; they will push back if they disagree.
- **Show trade-off tables** when there are 3+ options. Compact and scannable.
- **Verify before claiming done** — build, run, parse output. Not "should work."
- **Ask before visible/destructive actions:** pushing to GitHub, installing
  toolchains, force operations. Local edits and builds can proceed freely.
- **Batch decisions** via `AskUserQuestion` (2–4 questions in one tool call).
- **Save quirks to `~/.claude/memory`** when discovered (this machine's PATH,
  user email mismatch, lab-vs-personal profile split — all caught here, all
  worth remembering).

### What to do less of

- Don't pre-write 1000-word design docs without being asked. Concise
  architecture + sprint plan beats exhaustive proposal.
- Don't pad with "let me summarise what we just did." The diff is the summary.
- Don't pile on "any more questions?" rounds. Ask only what's blocking.
- Don't skip `Read` before `Write` on files generated by `dotnet new` (or any
  template that creates a starter file). It will error and waste a turn.
- TodoWrite for short linear setup sequences is overkill; for the multi-day
  Sprint 1 implementation, use it.

### User profile (build context, not gossip)

- Owns this app repo (`oleksandrmaslov/flashlight_app`) and the firmware repo
  (`oleksandrmaslov/pocket-light-firmware`).
- Direct, action-oriented; prefers to be shown options and pick quickly.
- Comfortable in C# / .NET though primary domain is embedded C.
- Building for non-developer factory operators — keep that audience in mind
  for UX choices (giant PASS/FAIL band, single primary action, etc.).
- Comfortable letting the assistant proceed once a decision is made; doesn't
  need permission to re-ask.

---

## Memory file (firmware repo)

The firmware-side Claude has a project memory at
`~/.claude/projects/c--Users-Alexandr-py32f0-template-project/memory/flashlight_app_project.md`
pointing at this repo. Keep both in sync: if you change a decision here,
update that file too.
