# PizzaWave Current Status

Last updated: 2026-05-14

This is the handoff note for starting a new Codex session rooted at
`C:\projects\pizzawave`.

## First Instruction For New Session

Start in `C:\projects\pizzawave`, read this file and `docs/open-todo.md`, then
run:

```powershell
git status --short
git log -1 --oneline
dotnet sln C:\projects\pizzawave\pizzawave.sln list
```

The expected branch is `codex/pizzawave-engine-cleanbreak`. The checkpoint
commit is `c2c2a5e Move transcript post-processing off transcription workers`
or a later handoff/status commit.

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

The following passed after the cleanup, migration, deploy-helper fix, and final
checkpoint:

```powershell
npm run build --prefix C:\projects\pizzawave\pizzad\web
dotnet test C:\projects\pizzawave\pizzawave.sln --configuration Release
dotnet build C:\projects\pizzawave\pizzawave.sln --configuration Release
dotnet publish C:\projects\pizzawave\pizzad\pizzad.csproj --configuration Release --runtime linux-x64 --self-contained false -p:PIZZAD_SKIP_WEB_BUILD=true
dotnet publish C:\projects\pizzawave\pizzad\pizzad.csproj --configuration Release --runtime linux-arm64 --self-contained false -p:PIZZAD_SKIP_WEB_BUILD=true
dotnet build C:\projects\pizzawave\pizzad\pizzad.csproj --configuration RELEASE_X64_CUDA -p:BuildProfile=Release_X64_CUDA -p:PIZZAD_SKIP_WEB_BUILD=true
```

The new lightweight test project currently covers talkgroup catalog behavior,
strict callstream payload parsing, police-code detection, and transcript
location helper null handling.

The latest post-transcription/dashboard cleanup checkpoint also passed:

```powershell
dotnet build C:\projects\pizzawave\pizzawave.sln
dotnet test C:\projects\pizzawave\pizzawave.sln --no-build
```

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

Backups remain on deployed hosts:

- `/var/lib/pizzawave/pizzad.db.pre-call-category-migration.bak`

The local `artifacts/migration-audit` folder was deleted so copied live DB
snapshots are no longer present in the workspace.

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
- Latest deployed commit: `c2c2a5e Move transcript post-processing off
  transcription workers`
- RPI direct deploy completed 2026-05-14 at about 13:48 EDT using the available
  local key `pizzapi_rpi_test_ed25519`.
- Post-deploy checks:
  - `pizzad.service` active;
  - `/api/v1/health` returned `status: ok`;
  - five `/api/v1/dashboard` checks returned HTTP 200 in roughly 0.12-0.61s;
  - no post-restart `fail`, `error`, `exception`, or `500` log entries were
    found.

## Recent Major Engine Changes

### faster-whisper on RPI

RPI transcription was falling behind with local Whisper.net. A long-lived
`faster-whisper` worker was added and deployed.

Observed result:

- RPI queue stopped saturating in the latest testing.
- RPI could keep up with live traffic in current testing.
- Quality audit found some repeated-token and numeric hallucinations, so quality
  reasons `repetitive` and `numeric_noise` were added.

### Post-Transcription Work Split

Police-code detection, transcript location extraction, stored call-location
writes, and geocoding now run in `TranscriptPostProcessingService`, a separate
hosted background queue. The transcription workers now write the transcript
result and enqueue post-processing instead of awaiting annotation/location work.

Dashboard location heat no longer extracts locations or performs live geocoding
while serving `/api/v1/dashboard`. It reads stored `call_locations` joined to
cached geocodes. This removes the previous dashboard load-time regex/geocoding
path that was causing slow dashboard responses and intermittent 500s on RPI.

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

The cleanup checkpoint is committed. A clean `git status --short` is expected
after pulling the pushed branch. If new local changes appear, treat them as
new-session/user-owned work.

After the new session starts, the likely next verification steps are:

1. Re-run `git status --short`.
2. Confirm `c2c2a5e` or a later pushed commit is checked out.
3. Check RPI dashboard and queue health after the post-transcription split.
4. Check deployed queue health, especially omicrontheta after deployment downtime.
