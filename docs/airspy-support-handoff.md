# Airspy Mini/R2 Support Hand-off

Date: 2026-06-21

## Goal

Add first-class Airspy Mini and Airspy R2 support throughout PizzaWave Setup, starting with the Airspy Mini on the RPI mobile Yagi rig.

The operator wants to swap the current RTL-SDR for an Airspy Mini first. Airspy R2 may be tested later, but R2 may overload the Raspberry Pi due to higher bandwidth and CPU/USB pressure.

## Session Boundary

This hand-off is for a new Codex session in a new worktree.

Do not use the current baseline worktree for the Airspy implementation. The current worktree remains focused on field baseline work.

Recommended new worktree name:

```powershell
git worktree add C:\projects\pizzawave-airspy-support -b codex/airspy-support <base>
```

Important: do not start from plain `main` unless the current Setup / first-run wizard changes from `C:\projects\pizzawave-radio-setup-field-baselines` have first been committed, merged, or intentionally ported. The RPI is running code from that worktree, and several relevant Setup/RF workflow paths are not represented by old `main`.

## Hard Constraints

- Do not deploy to OT / omicrontheta.
- Do not run RF sweeps, call-quality checks, or live measurements unless the operator explicitly asks.
- Use PizzaWave API-first workflow. Do not use old local RF diagnostic skills or standalone local RF scripts as the primary workflow.
- Prefer durable typed support for SDR devices over more `rtl=...` string branching.
- Preserve RTL-SDR support.
- Airspy support must be modeled separately from RTL-SDR because sample rates, gain behavior, tuning behavior, and source coverage differ.

## Current RPI State

Target RPI:

```text
ocroot@192.168.2.42
PizzaWave: http://192.168.2.42:8080
Current SDR: RTL-SDR, serial 00000002
Current RF path: mobile Yagi rig
```

Useful SSH key:

```powershell
$env:USERPROFILE\.ssh\pizzapi_rpi_test_ed25519
```

RPI already has Airspy user tools installed:

```text
/usr/bin/airspy_info
/usr/bin/airspy_rx
/usr/bin/rtl_test
```

With no Airspy connected, `airspy_info` currently reports:

```text
airspy_open() board 1 failed: AIRSPY_ERROR_NOT_FOUND (-5)
airspy_lib_version: 1.0.11
```

The installed `airspy_rx` expects frequency in MHz, not Hz:

```text
airspy_rx -f frequency_MHz
```

## Main Findings From Source Audit

### Already Partially Airspy-Aware

Setup RF validation has partial Airspy support:

- `pizzad/RfSurveyService.cs`
  - Infers `SdrType = "Airspy"` when the TR source device string contains `airspy`.
  - Tool prep checks `airspy_info` and `airspy_rx`.
  - SDR inventory can run `airspy_info`.
  - RF power scan has an Airspy branch and parses Airspy IQ as signed 16-bit I/Q.
  - P25 probe template supports `{sdr_type}`, `{device}`, and `{serial}` placeholders.

### Blocking Gaps

First-run setup remains RTL-first:

- `pizzad/SetupJobService.cs`
  - `/api/v1/setup/sdrs` runs `rtl_test` only.
  - It parses only RTL serial output.
  - UI message says “Detected possible RTL-SDR device(s).”
  - `sdr-prime` installs/checks RTL-SDR/GQRX only.

- `pizzad/EngineModels.cs`
  - `SetupSdrDeviceDto` has only `Index`, `Serial`, `Label`, `UsbLine`, `Warning`.
  - No SDR type, driver, device args, sample-rate options, gain model, or capability metadata.
  - `SetupTrConfigDraftRequest` and `SetupTrConfigSourcePlanRequest` accept `SdrSerials` as a string, not typed devices.
  - `SetupTrConfigSourceDto` also only carries `Serial`.

- `pizzad/SetupTrConfigBuilderService.cs`
  - Default sample rate is hardcoded as `2_400_000`.
  - Config generation always emits `device = rtl=...`.
  - Template patching calls `PatchRtlDevice`.
  - Source coverage is based on one global requested sample rate.
  - Warnings reference RTL-SDR-specific sample-rate assumptions.

- `pizzad/SetupCalibrationService.cs`
  - Extracts only RTL serials from TR `device` strings.
  - Coverage warnings say to add another RTL-SDR source.

- `pizzad/web/src/App.tsx`
  - First-run SDR detection writes `result.devices.map(device => device.serial)` into `trDraftSerials`.
  - Several UI strings and old guided sweep paths still say `rtl=...` or RTL-SDR.
  - Some legacy `tr_tune.sh` command builders remain in UI code. The current direction is API-first; avoid extending these for Airspy.

- `scripts/tr_health_collect.sh`
  - Source label extraction uses `rtl=(?<serial>...)` and falls back to `idxN`. This should understand Airspy source labels too.

### Concrete Bug To Fix Early

`pizzad/RfSurveyService.cs` currently builds Airspy RF scan commands like:

```text
airspy_rx -r <output> -f <frequencyHz> -a <sampleRate> ...
```

For the installed RPI `airspy_rx`, `-f` is MHz. This must become something like:

```text
airspy_rx -r <output> -f <frequencyMhz> -a <sampleRate> ...
```

Also add `-s <serial>` when an Airspy serial is known.

## Recommended Implementation Plan

### 1. Add A Typed SDR Model

Introduce a shared setup SDR type that can represent both RTL and Airspy.

Suggested fields:

```text
index
type                 RTL-SDR | Airspy | Unknown
serial
label
driver              usually osmosdr
deviceArgs          rtl=..., airspy=..., airspy=<serial>, etc.
usbLine
sampleRateOptions
defaultSampleRate
gainMode            rtl-tuner-gain | airspy-linearity | airspy-sensitivity | airspy-stage-gains
defaultGain
warning
```

This should replace the current first-run `SdrSerials` plumbing rather than adding more comma-delimited string conventions.

### 2. Update SDR Detection

Update `/api/v1/setup/sdrs` to:

- pause TR only when needed;
- run `rtl_test -t`;
- run `airspy_info`;
- run `lsusb`;
- parse RTL and Airspy devices into the same typed DTO;
- show missing tools separately from no devices found;
- restart TR when PizzaWave paused it.

Do not rely only on `lsusb`; Airspy serials should come from `airspy_info` when available.

### 3. Update First-run TR Source Planning

Replace `SdrSerials` with typed selected devices in:

- `SetupTrConfigDraftRequest`
- `SetupTrConfigSourcePlanRequest`
- `SetupTrConfigSourceDto`
- web types and UI state

Source planning should use each selected device's sample-rate/capability profile instead of one global sample rate.

Initial conservative defaults:

```text
RTL-SDR: 2.4 MS/s
Airspy Mini: start with 3.0 MS/s or 6.0 MS/s only after RPI load is verified
Airspy R2: expose as experimental / high-load risk, prefer operator confirmation
```

The exact TR/osmosdr Airspy device string must be verified on RPI with actual hardware connected before finalizing. Do not assume it blindly.

### 4. Update TR Config Generation

Generate source definitions based on typed device:

- RTL should preserve existing behavior such as `rtl=<serial>,buflen=65536`.
- Airspy should emit the verified gr-osmosdr/trunk-recorder device string.
- Preserve RTL defaults such as `buflen=65536`.
- Use Airspy-appropriate gain defaults and sample rates.
- Keep `driver = osmosdr` unless testing proves a different TR driver is needed.

### 5. Update Setup RF Validation

Fix and harden:

- Airspy RF scan command frequency units.
- Airspy serial selector with `airspy_rx -s`.
- Airspy gain controls.
- Source/device labels.
- Candidate ranking and source coverage display for wider Airspy windows.

Avoid extending old `tr_tune.sh` flows. The combined RF sweep and Setup APIs are the intended path.

### 6. Update Tests

Add focused tests for:

- mixed RTL/Airspy SDR detection parsing;
- Airspy source plan DTOs;
- Airspy TR config generation;
- RTL behavior unchanged;
- Airspy RF scan command uses MHz and includes serial when present;
- coverage planner uses per-device sample-rate windows.

Existing tests to review:

- `pizzad.Tests/SetupTrConfigBuilderServiceTests.cs`
- `pizzad.Tests/SetupCalibrationServiceTests.cs`

### 7. Update Docs

Docs already state the design goal in `docs/rf-path-survey-mode.md`, but older operator docs still say RTL-only.

Update:

- `docs/getting_started_with_sdrs.md`
- `docs/quickstart.md`
- `docs/config-examples-explained.md`
- `scripts/README.md`
- possibly `docs/rf-path-survey-mode.md` with concrete Airspy implementation notes once verified.

## Important File References

Backend:

- `pizzad/SetupJobService.cs`
- `pizzad/SetupTrConfigBuilderService.cs`
- `pizzad/SetupCalibrationService.cs`
- `pizzad/RfSurveyService.cs`
- `pizzad/EngineModels.cs`
- `pizzad/TrConfigSourceCoverageValidator.cs`

Frontend:

- `pizzad/web/src/App.tsx`
- `pizzad/web/src/types.ts`
- `pizzad/web/src/api.ts`
- `pizzad/web/src/style.css`

Scripts/docs:

- `scripts/pizzawave_setup_admin.sh`
- `scripts/setup_trunk_recorder.sh`
- `scripts/tr_health_collect.sh`
- `docs/rf-path-survey-mode.md`
- `docs/getting_started_with_sdrs.md`
- `docs/open-todo.md`

## Validation Guidance

Before Airspy hardware is attached:

- run unit tests;
- test DTO/API shape with mocked Airspy detection output;
- verify generated TR config JSON structurally;
- verify `airspy_rx` command rendering only, not capture.

After Airspy Mini is attached:

- run `/api/v1/setup/sdrs`;
- run `airspy_info`;
- verify the TR/osmosdr device string with a short controlled TR start;
- run only the operator-requested Setup steps;
- do not run full RF sweeps unless explicitly asked.

## Baseline Work Continuation

The current `C:\projects\pizzawave-radio-setup-field-baselines` worktree should remain focused on baseline work for the RPI Yagi rig and later Airspy comparison evidence.

Do not let the Airspy implementation session mutate field-test notes unless the operator asks for that session to record measured evidence.
