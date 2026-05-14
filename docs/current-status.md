# PizzaWave Current Status

Last updated: 2026-05-14

This is the handoff note for starting a new Codex session rooted at
`C:\projects\pizzawave`.

## First Instruction For New Session

Start in `C:\projects\pizzawave`, read this file and `docs/open-todo.md`, then
run:

```powershell
git status --short
dotnet sln C:\projects\pizzawave\pizzawave.sln list
```

The active repo root should be `C:\projects\pizzawave`, not the now-removed
`pizzapi` subfolder.

## Active Architecture

PizzaWave uses `pizzad`, a persistent .NET 9 Linux service that owns ingest,
storage, transcription, alerts, AI summaries/incidents, trunk-recorder health,
imports, REST/SSE APIs, and the bundled React web UI.

Current active solution projects:

- `pizzad`
- `pizzad.Tests`

`pizzalib` has been removed. Callstream parsing, audio conversion,
transcription providers, SMTP alerting, talkgroup resolution, and police-code
annotation now live in `pizzad`.

## Hard Cleanup Completed

The current worktree has removed unsupported app/tool source from the active
tree:

- removed `pizzacmd`;
- removed `pizzapi`;
- removed `pizzaui`;
- removed `pizzalib`;
- removed `tools/TranscriptionBakeoff`;
- removed stale root launch settings that referenced old apps;
- removed documentation screenshots and docs image assets;
- removed `scripts/pizzapi-upgrade.sh`;
- rewrote docs around current PizzaWave behavior;
- changed branding language from `PizzaStack`/`Pizzastack` to `PizzaWave`.

Important Windows note: this previous Codex session started with
`C:\projects\pizzawave\pizzapi` as its root, so Windows held the empty
`pizzapi` directory open. All tracked files inside it are deleted. After opening
a new session rooted at `C:\projects\pizzawave`, remove the empty directory if it
still exists:

```powershell
Remove-Item -LiteralPath C:\projects\pizzawave\pizzapi -Force
```

Git does not track empty directories, so this is only filesystem cleanup.

## Branding

Use `PizzaWave` for product/stack branding. Do not introduce `PizzaStack` or
`Pizzastack` in user-facing docs or UI. The setup/display field is now
`PizzaWave name`.

## Verification Already Run

The following should be re-run after the current catalog/pizzalib removal pass:

```powershell
npm run build --prefix C:\projects\pizzawave\pizzad\web
dotnet test C:\projects\pizzawave\pizzawave.sln --configuration Release
dotnet build C:\projects\pizzawave\pizzawave.sln --configuration Release
```

The new lightweight test project currently covers talkgroup catalog behavior,
strict callstream payload parsing, and police-code detection.

## Talkgroup Catalog

PizzaWave now owns a JSON talkgroup catalog at
`trunkRecorder.talkgroupCatalogPath`. The trunk-recorder talkgroup CSV is
generated output at `trunkRecorder.talkgroupsPath`.

Rules:

- category and `talkgroup_name` are decided once at ingest/import time;
- read paths do not silently re-enrich historical calls from the catalog;
- catalog edits affect future calls only;
- disabled and deleted talkgroups are excluded from the generated TR CSV;
- disabled rows remain visible and can be re-enabled;
- deleted rows leave the catalog and require re-import/manual add to return;
- trunk-recorder should be restarted after saving catalog changes.

The one-time deployed DB migration was run on 2026-05-14 for omicrontheta and
RPI after installing JSON catalogs generated from the deployed TR CSV files.
Final live dry-runs reported zero remaining catalog/category/name changes on
both stacks. The disposable migration script was removed after use.

## Deployed Systems

### omicrontheta

- Host: `192.168.1.173`
- SSH user: `lilhoser`
- Role: x64 Linux trunk-recorder/PizzaWave host
- PizzaWave URL: `http://192.168.1.173:8080`
- Important paths:
  - Config: `/etc/pizzawave/pizzad.json`
  - DB: `/var/lib/pizzawave/pizzad.db`
  - Audio: `/var/lib/pizzawave/audio`
  - TR config: `/etc/trunk-recorder/config.json`

### RPI

- Host: `192.168.2.42`
- SSH user: `ocroot`
- SSH key documented in earlier local setup may still be named
  `pizzapi_rpi_test_ed25519`; docs now use `pizzawave_rpi_ed25519` as the
  preferred neutral name. Confirm the actual key name before deploy.
- Role: Raspberry Pi 5 trunk-recorder/PizzaWave host
- Current transcription provider: `faster-whisper`
- Model: `tiny`, `int8`, CPU, one worker
- PizzaWave URL: `http://192.168.2.42:8080`

## Recent Major Engine Changes

### faster-whisper on RPI

RPI transcription was falling behind with local Whisper.net. A long-lived
`faster-whisper` worker was added and deployed.

Observed result:

- RPI queue stopped saturating in the latest testing.
- RPI could keep up with live traffic in current testing.
- Quality audit found some repeated-token and numeric hallucinations, so quality
  reasons `repetitive` and `numeric_noise` were added.

### TR RF Health Split

TR health decode metrics were split into:

- `CC summary decode rate`: periodic `Control Channel Decode Rates` msg/sec
  lines;
- `Low-decode warnings`: warning/retune symptom lines.

The collector, SQLite schema, historical prime script, API, dashboard KPI,
recommendations, and System UI now use the split model.

### RF Analysis Tool

`System > Trunk Recorder > RF Analysis` reports:

- CC summary decode rate;
- CC summary zero rate;
- low-decode warnings;
- low-warning zero rate;
- retunes;
- no-transmission rate;
- recorder exhaustion;
- previous equal-window comparison;
- retune targets from recent journald when available;
- recommended next checks.

Use this after 48-72 hours of post-change data before judging omicrontheta
gain/RF changes.

## Useful Commands

Health:

```powershell
ssh -o BatchMode=yes -o IdentitiesOnly=yes lilhoser@192.168.1.173 'curl -fsS http://127.0.0.1:8080/api/v1/health'
ssh -i $env:USERPROFILE\.ssh\pizzawave_rpi_ed25519 -o BatchMode=yes -o IdentitiesOnly=yes ocroot@192.168.2.42 'curl -fsS http://127.0.0.1:8080/api/v1/health'
```

Deploy omicrontheta:

```powershell
C:\projects\pizzawave\scripts\deploy_pizzad_tar.ps1 -HostName lilhoser@192.168.1.173 -Rid linux-x64
```

Deploy RPI:

```powershell
C:\projects\pizzawave\scripts\deploy_pizzad_tar.ps1 -HostName ocroot@192.168.2.42 -SshKey $env:USERPROFILE\.ssh\pizzawave_rpi_ed25519 -Rid linux-arm64
```

Do not run x64 and arm64 deploys in parallel from the same workspace. A previous
parallel deploy hit a shared `obj/project.assets.json` race.

## Worktree Caveat

The repo is intentionally very dirty after the cleanup and prior engine work.
Do not revert broad file sets. Treat uncommitted changes as user-owned unless
you inspect them and can prove they are generated/stale.

After the new session starts, the likely next verification steps are:

1. Re-run `git status --short`.
2. Re-run the web build, solution tests, solution build, and linux-x64/linux-arm64 publish checks.
3. Decide whether to stage/commit the cleanup as a large checkpoint.
