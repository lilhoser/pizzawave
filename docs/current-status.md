# PizzaWave Current Status

Last updated: 2026-05-16

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
commit is the latest pushed handoff/status commit on that branch.

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
location helper null handling plus generic location extraction guards for vague
road-type fragments.

The latest incident/verifier/dashboard-map/geography-portability checkpoints
also passed:

```powershell
dotnet build C:\projects\pizzawave\pizzawave.sln --configuration Release --no-restore
dotnet test C:\projects\pizzawave\pizzawave.sln --configuration Release --no-restore --no-build
npm run build
```

For local Windows builds, set `MSBuildEnableWorkloadResolver=false` if the
installed workload manifests are broken. The web build was run from
`C:\projects\pizzawave\pizzad\web` using the Visual Studio bundled `npm.cmd`
because `npm` was not on the default PATH.

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
- Latest deployed code includes the 2026-05-16 incident/verifier/dashboard-map
  checkpoint, local-geography cleanup, and map selection usability updates from
  `codex/pizzawave-engine-cleanbreak`.
- RPI direct deploy completed again on 2026-05-16 using the available local key
  `pizzapi_rpi_test_ed25519`.
- Post-deploy checks:
  - `pizzad.service` active;
  - `/api/v1/health` returned `status: ok`;
  - `/api/v1/dashboard` returned HTTP 200 in roughly 0.5s after the cached-only
    map fallback deploy;
  - no post-restart `fail`, `error`, `exception`, or `500` log entries were
    found.

## Recent Major Engine Changes

### V1 LLM-Managed Incidents

Live incident generation now uses an incremental site-local LLM incident-state
pass instead of promoting category AI summaries into incidents. The service
sends active incident state plus new/carryover candidate calls on a 5-minute
cadence, stores stable incident keys/status, and concludes stale managed
incidents after the rolling state window.

Important behavior:

- incidents may span categories/talkgroups within the same site;
- category is only a label, not a grouping qualifier;
- deterministic validation still requires at least two source calls and concrete
  shared anchors before writing/updating an incident;
- old incidents were purged on omicrontheta and RPI when this paradigm was
  deployed;
- a future controlled backfill, potentially up to 7 days, remains on the TODO
  list if the new approach holds up.

### Evidence Verifier

A second LLM pass now verifies incident evidence attribution after the incident
extractor proposes title/detail/call IDs. For each incident it reviews selected
and nearby calls, classifies them as `supporting`, `related_context`,
`unrelated`, or `contradicts`, and deterministic code adds/drops/retains call
links before final validation.

Telemetry:

- token usage for verifier calls is recorded in `lm_usage`;
- verifier behavior/lossiness is recorded in `evidence_verifier_runs`;
- watch reviewed/model-reviewed calls, truncated calls, added/dropped/retained
  calls, failures, and LM finish reasons.

Post-deploy observations on 2026-05-16:

- omicrontheta verifier calls were succeeding after compacting the response
  schema; earlier `max_tokens=1800` truncations stopped after the fix;
- RPI AI resumed after the transcription queue drained below the AI gate;
- verifier lossiness is still real under Hamilton volume, with many nearby calls
  omitted by prompt/call-count guardrails.

### Dashboard Incidents And Map

The dashboard incident explorer is not intentionally capped at 10. It shows
unique incidents for the selected range/profile. Category pages show incidents
that touch that category, so one incident can appear on multiple category pages.

The map now:

- shows only geolocated incident-linked locations, not every geolocated raw
  call;
- preserves up to 120 location groups instead of 30;
- uses all call IDs in a geocoded location bucket for incident matching while
  still displaying only a compact call sample in the popup;
- uses cached geocodes extracted from incident title/detail as a display
  fallback when call-level extraction missed a location.
- starts at a closer zoom based on the returned point spread instead of a fixed
  broad local zoom;
- makes the selected map dot pale yellow, larger, and pulsing;
- uses a wider selected-location panel with larger, less cramped source-call
  evidence cards.

The incident query ceiling was raised from 200 to 1000 after an apparent
"capped at 50" investigation. The live 50 count was coincidental, but the fixed
ceiling was a future risk.

The geography/geocoding path was made product-generic:

- removed hard-coded Chattanooga-area place extraction and local highway
  allowlists from runtime location extraction;
- removed hard-coded `Tennessee`, `Georgia`, and `countrycodes=us` from
  Nominatim queries;
- removed default Hamilton/Bradley/Cleveland monitored areas from runtime and
  installer templates;
- removed hard-coded frontend map bounds and now centers the map from actual
  geocoded points;
- removed stale vague road-type geocode artifacts from deployed DBs:
  omicrontheta deleted 28 `call_locations` and 24 `geocode_cache` rows; RPI
  deleted 50 `call_locations` and 29 `geocode_cache` rows.

The `Boulevard -> Murphy USA, Signal Mountain Boulevard...` map row was caused
by deterministic extraction allowing the bare word `Boulevard`, followed by
Nominatim returning a plausible-looking but unjustified match. That row is now
gone. Separate incident title/detail overreach still exists when the LLM infers
too much from terse vehicle/plate/unit traffic; handle that in incident
quality/verifier work, not geocoding.

Do not perform live geocoding inside `/api/v1/dashboard`. A trial fallback that
called Nominatim synchronously made dashboard requests take roughly 70-80s. The
deployed fallback is cached-only and keeps dashboard latency normal. Remaining
unmapped incidents with clear street/address text need a background incident
geocoding/retry path, not synchronous dashboard geocoding.

Current post-deploy API spot check from the 2026-05-16 18:28 heartbeat:

- omicrontheta dashboard: 52 unique incidents, 120 map rows, 19 map-linked
  rows; queue depth 0 and pending transcriptions 0;
- RPI dashboard: 73 unique incidents, 120 map rows, 28 map-linked rows; queue
  depth 11, pending transcriptions 12 / 160s audio, and transcribing faster
  than ingest.

Both rigs served the latest map-usability bundle after deploy:

- `assets/index-bQZ1jR4f.js`
- `assets/index-B26rCV-w.css`

### API And Experiment Cleanup

The rig-side transcription experiment API/UI and diagnostic result persistence
were removed. Model bakeoffs should run off-rig against copied audio samples so
production queues and rig CPU are not affected. The category-page AI summaries
view was removed from the UI; category pages now focus on incidents and raw
calls while AI summaries are treated as internal/interstitial data.

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
parallel deploy hit a shared `obj/project.assets.json` race. If a `dotnet
publish --no-restore -r <rid>` fails with `NETSDK1047` for the other runtime,
rerun that publish once without `--no-restore` so the assets file includes that
RID.

## Worktree Caveat

The cleanup and incident/verifier/dashboard-map checkpoints are committed. A
clean `git status --short` is expected after pulling the pushed branch. If new
local changes appear, treat them as new-session/user-owned work.

After the new session starts, the likely next verification steps are:

1. Re-run `git status --short`.
2. Confirm the latest pushed handoff/status commit is checked out.
3. Check RPI dashboard and queue health after the incident/verifier deploy.
4. Continue monitoring verifier lossiness and incident geocoding coverage.
