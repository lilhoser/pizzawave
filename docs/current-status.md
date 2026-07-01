# PizzaWave Current Status

Last updated: 2026-07-01

This is the handoff note for starting a new Codex session rooted at
`C:\projects\pizzawave`.

## Latest Incident V3 Status

The current incident v3 tuning phase is closed. Keep the v3 safety and executor
validation simulation changes, but keep v3 live mutation disabled until
transcription quality has been measured and improved. See
`docs/incident-v3-closure-status.md` for the current decision, known-good v3
work, remaining verification, and next recommended task.

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

## 2026-05-22 Checkpoint

Current master TODO list:

1. Clean Rig Setup + Setup API Audit.
2. Grow focused test lanes only where they provide real signal.
3. Hourly PizzaWave Quality Monitoring, including incident/RAG quality.
4. Refresh/prune docs.

- 2026-05-24 Raymond/MS RF bring-up produced a new product follow-up:
  `docs/rf-path-survey-mode.md`. The setup/calibration flow needs a repeatable
  RF Path Survey that records rig hardware variables, captures RF/waterfall
  evidence, tests candidate P25 control channels, observes voice grants, checks
  source coverage/audio capture, and produces an explicit pass/partial/fail
  verdict plus recommended trunk-recorder source plan.
- 2026-05-24 Raymond/MS diagnostic antenna rig plan:
  - Purpose: temporary high-gain directional RF survey rig, not the portable or
    permanent PizzaWave antenna. Use it to determine which direction/side of a
    house or site has decodable 700/800 MHz public-safety signal, then use that
    evidence to place the portable or stationary rig.
  - Target sites: MSWIN ETV Raymond around 770-774 MHz and nearby Hinds
    simulcast candidates around 851-853 MHz.
  - Planned antenna: Ventev/TerraWave `T08011YD11206` directional Yagi,
    746-896 MHz, about 11.2 dBi, N-female connector, roughly 48 degree
    horizontal and 40 degree vertical beamwidth. Mount/hold vertically polarized
    for land-mobile/public-safety receive tests unless evidence says otherwise.
  - Planned feed line: 50 ft LMR-400 with N-male at the Yagi end and BNC-male at
    the receiver end. Expected cable loss is roughly 1.8-2.0 dB at 770-850 MHz,
    before connector/adapter losses.
  - Existing filter: Scanner Master `BPF-800M`, 769-872 MHz public-safety
    bandpass filter. This remains useful as a test/control part, especially
    ahead of wideband active gain, but the preferred 700/800 multicoupler option
    below already has a purpose-fit 730-870 MHz passband. In clean RF, test with
    and without the `BPF-800M`; in high-gain Yagi or LNA tests, prefer keeping a
    bandpass filter ahead of active gain/distribution and score by P25
    decode/call quality rather than raw waterfall brightness.
  - Preferred distribution: Stridsberg `MCA780M` active receiver multicoupler,
    700/800 MHz service band, 730-870 MHz (-3 dB bandwidth), 4 outputs,
    +4 dB nominal gain, 3.5 dB nominal noise figure, 22 dB minimum
    port-to-port isolation, BNC standard connectors with N-input/TNC options,
    powered from clean 12 VDC. It covers both ETV Raymond 770-774 MHz and Hinds
    851-853 MHz while rejecting more out-of-band RF than broad 25 MHz-1 GHz
    multicouplers.
  - Broad lab alternative: Stridsberg `MCA208M` active receiver multicoupler,
    BNC-female input/outputs, 25 MHz-1 GHz, 50 ohm, powered from a clean 12 V
    supply. Prefer this only if the diagnostic kit also needs broad VHF/UHF
    scanner/lab coverage. If using the `MCA208M` for 700/800 PizzaWave tests,
    keep the `BPF-800M` before it when overload/intermod is plausible.
  - SDR connections: use short flexible BNC-to-SMA pigtails from multicoupler
    outputs to RTL-SDR Blog V4 or Airspy receivers. Avoid rigid adapter stacks
    hanging off the multicoupler or SDRs. Terminate unused multicoupler outputs
    with 50 ohm loads when making repeatable measurements.
  - Baseline assembly order:
    `Yagi -> 50 ft LMR-400 -> optional BPF-800M -> MCA780M input -> short
    BNC/SMA pigtails -> SDRs`.
  - First-test discipline: before using multiple receivers, test
    `Yagi -> LMR-400 -> optional BPF-800M -> one SDR` directly, then insert the
    multicoupler
    and confirm the same site/control-channel decode behavior survives.
  - Scoring: judge by P25 decode and call-capture evidence, not waterfall
    brightness alone. Record control-channel decode rates, RFSS/site ID, grants,
    recorder starts, no-transmission outcomes, and whether voice audio is usable.
  - Simulcast caution: the best Yagi aim may be the heading that reduces
    multipath or suppresses a competing simulcast transmitter, not necessarily
    the brightest signal peak.
  - Mobile rig candidate A, simplest/known working partial coverage:
    Remtronix 700/800-style antenna with DIY counterpoise/ground-plane, direct
    to Airspy Mini/R2 or one RTL-SDR, no splitter, one wide source where the SDR
    can cover the target site. For ETV Raymond with one RTL this is partial
    coverage only; with Airspy, test a single wider source before adding any
    distribution hardware.
  - Mobile rig candidate B, dual-RTL portable full-coverage attempt:
    Remtronix antenna with DIY counterpoise/ground-plane, `BPF-800M`, optional
    RTL-SDR Blog wideband LNA, Stridsberg `MCA780M` active multicoupler, and two
    RTL-SDR Blog V4 dongles. This is the preferred dual-RTL candidate in
    RF-challenged environments. Avoid a traditional passive 2-way splitter
    unless signal margin is already proven; expect roughly 3.5 dB or more loss
    per splitter output before jumper/connector losses. If using the RTL-SDR
    wideband LNA, place filtering before/near gain when overload is possible,
    enable/prove bias-tee power deliberately, and record LNA placement in the RF
    survey profile.
  - Mobile rig candidate C, vehicle/mobile antenna:
    DPD-style 760-960 MHz or 800 MHz mobile/NMO antenna on an actual vehicle
    roof or other real counterpoise. A mag mount without a vehicle roof or
    comparable conductive plane is not equivalent to the advertised mobile
    antenna system.
  - Ground-plane/counterpoise notes:
    handheld whips such as Remtronix/Diamond do not require a large external
    plane to function, but they can benefit from a repeatable counterpoise when
    attached to an SDR instead of a handheld radio body. For 770-850 MHz, DIY
    radial wires around 3.4-3.8 inches attached to the connector shield are the
    right scale. Mobile mag-mount/NMO antennas are different: they expect a
    vehicle roof or comparable conductive counterpoise.
  - Stationary rig candidate A, broad receive:
    quality discone mounted high with low-loss coax. A discone includes its own
    cone/radial structure and does not need a separate RF ground plane, but an
    outdoor mast/coax still need normal safety/lightning grounding. A PVC mast
    can physically support a receive discone if mechanically adequate, but it is
    not a safety ground.
  - Stationary rig candidate B, ETV Raymond fixed omni:
    DPD 769-775 MHz 700 MHz outdoor/base vertical, low-loss coax, `BPF-800M`,
    then Airspy or MCA780M/SDRs. This is narrow and site-focused, not a general
    851-853 MHz Hinds antenna.
  - Stationary rig candidate C, Hinds/800 fixed omni:
    DPD 851-869 MHz 800 MHz outdoor/base vertical or similar 800 MHz public
    safety omni, low-loss coax, `BPF-800M`, then Airspy or MCA780M/SDRs.
  - Stationary rig candidate D, directional:
    Ventev/TerraWave 746-896 MHz Yagi when the diagnostic rig proves a useful
    bearing. Score by decode/call quality rather than raw signal strength.
  - Stationary rig selected omni path:
    buy qty 2 PCTEL/Maxrad `MFBW7463` fiberglass omnis, 746-869 MHz. This is
    the preferred broad stationary omni choice for covering both ETV Raymond
    770-774 MHz and Hinds/800 MHz candidates without committing to a narrow
    DPD-only band. Use low-loss coax, optional `BPF-800M`, and Airspy or
    `MCA780M`/SDRs depending on whether the stationary build is single-source or
    dual-RTL.
  - Shared shopping-list guidance for RF experiments:
    buy shared RF-chain parts once and move them between diagnostic/mobile/
    stationary tests where practical. One `BPF-800M` can serve the Yagi
    diagnostic rig, Airspy single-source tests, or multicoupler/dual-RTL tests.
    One `MCA780M` can serve both the diagnostic Yagi rig and any 700/800
    dual-RTL mobile or stationary test. Prefer extra inexpensive
    pigtails/terminators over duplicate filters or multicouplers.
    - Qty 1: Ventev/TerraWave `T08011YD11206` Yagi, 746-896 MHz, N-female
      connector, for temporary directional diagnostics.
    - Qty 1: 50 ft LMR-400 feed line, N-male antenna end to BNC-male receiver
      end, for the Yagi diagnostic rig.
    - Qty 1: Scanner Master `BPF-800M`, 769-872 MHz bandpass filter. Share this
      across Yagi, Airspy, and dual-RTL tests; treat it as optional/control when
      the `MCA780M` is already in the chain.
    - Qty 1: Stridsberg `MCA780M` active 700/800 MHz multicoupler. Share this
      across Yagi diagnostic and dual-RTL tests. Use `MCA208M` instead only if a
      broader 25 MHz-1 GHz lab multicoupler is required.
    - Qty 1: clean 12 V power supply for `MCA780M`, if not included.
    - Qty 2-4: 50 ohm BNC terminators for unused `MCA780M` outputs. Buy enough
      to terminate all unused ports in the largest planned test.
    - Qty 2-4: short flexible BNC-male to SMA-male pigtails for SDR outputs.
      Use one per RTL-SDR/Airspy connected to the multicoupler; extras are useful
      because these are wear items and reduce connector stress.
    - Qty 1: short flexible BNC-female to SMA-male pigtail for direct single-SDR
      testing from the BNC-male LMR-400 feed without the multicoupler.
    - Qty 1: temporary Yagi support, such as a small tripod, light stand, or
      clamp/mast that can hold vertical polarization while aiming.
    - Qty 2: PCTEL/Maxrad `MFBW7463` fiberglass omni antennas, 746-869 MHz, for
      the broad stationary 700/800 MHz omni path.
    - Qty 1: RTL-SDR Blog wideband LNA for the first LNA experiment. Do not buy
      multiple LNAs until one filtered/bias-tee-powered test proves useful.
    - Qty 1: DIY Remtronix counterpoise kit: connector/shield attachment plus
      3-4 radials around 3.4-3.8 inches. This is cheap enough to build once and
      reuse.
  - Optional qty 1: DPD 760-960 MHz mobile antenna only if a real vehicle roof
      or comparable counterpoise will be part of the test. Without that, it may
      not beat the Remtronix/direct-counterpoise path.
- 2026-05-24 Raymond/MS live RF checkpoint:
  - The RPI is now at `10.0.0.115` (`sdr1861`) and has been testing MSWIN ETV
    Raymond.
  - The current useful one-SDR experiment is intentionally partial coverage:
    `rtl=00000002`, center `772.718750 MHz`, rate `2.4M`, error `1200`, gain
    `49` (driver uses `49.6`), control channel `773.781250`, modulation `qpsk`.
    This covers roughly `771.568750-773.868750 MHz`, so it captures upper voice
    grants such as `771.656250` and `771.981250` while rejecting lower grants
    around `770.x`, `771.106250`, and `771.356250` as out of source coverage.
  - Post-clean-slate survey with this config stored 39 calls: 27
    `complete/ok`, 9 `poor_quality/repetitive`, 1 `poor_quality/empty`, 1
    `poor_quality/numeric_noise`, and 1 `poor_quality/too_short`. Captured
    frequencies were `771.656250` and `771.981250`.
  - One SDR direct on `00000002` repeatedly decoded RFSS `002`, site `008` on
    `773.781250`. The passive splitter/two-RTL path was materially worse in the
    same location and should not be treated as a stable baseline in challenged
    RF.
  - Candidate ETV Raymond control channels tested from the current indoor path:
    `773.031250`, `773.281250`, and `773.531250` produced no usable decode;
    `773.781250` was the only feasible CC. Hinds Simulcast 1/2 851-853 MHz
    control-channel candidates produced no usable decode from the tested indoor
    antenna state.
  - A clean data slate was started on 2026-05-24. `pizzad.db` was backed up to
    `/var/lib/pizzawave/pizzad.db.bak-clean-slate-20260524T203014Z`, runtime DB
    tables were wiped, Qdrant `pizzawave_calls` was deleted, and TR local
    runtime artifacts were cleared. Note: older audio files under
    `/var/lib/pizzawave/audio` were later found still present, even though the
    DB was clean and new rows were post-wipe.
  - Practical next step: freeze one stable rig before more house testing.
    Preferred portable direction is Airspy Mini/R2 as a single wider source
    with a compact 700/800 antenna/counterpoise path. Dual RTL remains supported
    but should use an active multicoupler such as `MCA780M`, not a passive
    splitter, in RF-challenged environments.

- RPI relocation note for the next session:
  - The RPI will be disconnected on 2026-05-22 between 08:30 and 09:00 EDT for
    relocation to Raymond, MS, where it will permanently live.
  - The user will perform a migration at the Raymond site.
  - Until a replacement Hamilton/TN RPI exists, do not run OT-vs-RPI bakeoff or
    agreement/disagreement comparisons. The RPI will no longer be monitoring the
    same RF geography as omicrontheta.
  - The hourly quality automation should continue full OT/omicrontheta analysis
    when run. If the relocated RPI is reachable, analyze it independently as the
    Raymond/MS site.
  - Work will continue from Raymond, MS, not this workstation, until next
    Tuesday.
- Live RPI backup/restore field test succeeded.
  - A fresh RPI backup was created and restored through the setup wizard.
  - Restore copied the backed-up SQLite DB, config, token, TR config/talkgroups,
    recorded audio, app data, and Qdrant data.
  - Restore now removes stale SQLite `-wal`/`-shm` sidecars before opening the
    restored DB, avoiding malformed-schema failures caused by mixing a restored
    DB with old live WAL state.
  - Post-restore setup now starts in validation mode instead of forcing the
    operator back through the Stack choice, derives `callstream configured` from
    the restored TR config, auto-runs required checks after Apply Restore, and
    exits setup automatically when those checks pass.
  - The monitored-area mismatch found during the RPI test was fixed generically:
    setup reads live TR `systems[].shortName` values from the restored config
    and reconciles the matching monitored area without hard-coded system names
    or geography.
  - Direct comparison of the latest backup archive against live RPI config
    showed transcription, AI Insights, embeddings, TR, branding, and profiles
    restored correctly. Differences were expected/defaulted: alert playback
    gained `repeatCount: 1`, and locations gained the corrected TR system
    shortName.
  - Remaining polish: stale frontend state/cache briefly showed old/default
    settings after restore/redeploy. Backend config and settings API were
    correct; add a cache-bust/forced settings reload follow-up.
- Rig migration mode is implemented for moving a rig to a different
  geography/site without carrying old RF/call state.
  - `System > Backup > Begin Migration...` only enters migration setup mode and
    pauses live ingest; it does not clear data.
  - The setup wizard now has a `Migration` step with migration profile
    export/import, `Reset For New Site`, and cancel controls.
  - `Reset For New Site` clears calls/audio/incidents/jobs/metrics/geocodes/
    recommendation baselines/Qdrant call vectors/talkgroup catalog/profile TG
    overlays/monitored areas, backs up and removes old TR config/talkgroups, and
    preserves portable settings such as transcription, AI Insights, embeddings,
    auth, server/storage paths, and alert playback.
  - Existing alert rules are preserved but disabled for review after reset.
  - CLI fallback exists:
    `sudo /usr/lib/pizzawave/scripts/pizzawave_setup_admin.sh begin-migration`
    and `cancel-migration`.
- Migration profile review notes:
  - Export/import is now in the migration wizard, not the normal Backup page.
  - Migration profiles can carry path settings such as app data, DB, audio, and
    Qdrant paths, but the operator must review hardware-sensitive AI,
    transcription, embedding, and worker/concurrency settings when moving
    between unlike systems.
  - Credentials are not exported as cleartext. Alert SMTP passwords are resolved
    through the local credential store; imported redacted/blank passwords do not
    overwrite an existing stored secret.
  - Migration reset rotates the admin token using the sudo-backed setup helper.
- Credential storage was restored after the settings refactor:
  - Alert SMTP passwords are stored in the local PizzaWave credential store and
    represented in config by the marker
    `__pizzawave_secret__:alerts.smtp.password`.
  - Windows uses DPAPI-protected storage under app data credentials.
  - Linux uses owner-only credential files under app data credentials.
  - Settings/setup status no longer returns the SMTP password.
  - Protected admin-token writes use the existing sudo-backed setup helper.
- 2026-05-22 incident-yield fix:
  - Deterministic validation now accepts a strong single-call emergency/event
    when a multi-call candidate is pruned down to one concrete supporting call.
  - This specifically targets legitimate single-dispatch incidents such as MVC,
    crash, fire/smoke, blocking roadway, violence, rescue/entrapment, or
    concrete BOLOs that were being over-suppressed after the prompt-pressure
    hardening.

## 2026-05-21 Checkpoint

- Settings > Talkgroups RPI-screen validation is complete.
- Recurring quality monitoring was consolidated into one hourly heartbeat:
  `pizzawave-cross-rig-quality-check`.
  - Scope through 2026-05-21: omicrontheta, RPI, and OT-vs-RPI comparison.
  - Scope after the 2026-05-22 RPI relocation: OT/omicrontheta remains the
    primary Hamilton/TN quality target; a reachable relocated RPI should be
    analyzed independently as Raymond/MS, not compared against OT.
  - Focus: transcription quality/throughput, queue and pending audio, LLM/AI
    backlog/load/token pressure/truncation/failures/latency, Qdrant/embedding
    health, incident/RAG quality, evidence verifier behavior, accepted/rejected
    operations, prompt/candidate pressure, dashboard grouping, and
    geocoding/map misses. Same-geography rig agreement was useful before the RPI
    relocation, but should be disabled after the RPI leaves Hamilton/TN.
  - RF is now secondary unless it explains capture/transcription/incident
    differences.
  - 2026-05-21 18:08 EDT: OT/omicrontheta was manually switched from local
    Whisper.net/base to local faster-whisper for a live bakeoff. Future
    automation runs should compare OT transcription quality/performance before
    and after that boundary, with special attention to queue depth, short/noisy
    transcript buckets, alert/address recall, AI load interference, and
    incident reject volume.
- Dashboard direct links are fixed as of 2026-05-21 after several focus-state
  corrections. Incident/call links now drive the visible page deterministically,
  open the target card, and no longer reset back to page 1 after focus is
  consumed. The user confirmed the link behavior is fixed on RPI.
- API audit cleanup removed deprecated rig-side mutation endpoints for
  transcription retries, manual incident generation/rebuild, recommendation
  auto-apply, and standalone TR CSV generation. See
  `docs/api-surface-audit.md`.
- Test strategy has been split into a fast BVT lane and a medium feature-test
  lane. BVT runs deterministic unit tests in PR validation; feature tests run
  scheduled/manual temp-host API checks. See `docs/testing.md`.
- Talkgroup profile enable/disable is downstream policy only. Generated
  trunk-recorder CSV now follows catalog `enabled` state, not active profile
  overrides, so switching profiles does not silently stop capture.
- System > Backup now creates full-state archives and restore is staged through
  setup mode. The restore wizard verifies manifest/checksums before applying,
  then forces setup checks to run before normal operation resumes. See
  `docs/backup-restore.md`.
- Added read-only `/api/v1/system/quality-check` for recurring rig quality
  automation. It returns one bounded snapshot for calls, transcript quality,
  AI usage/truncation, evidence-verifier activity, incident operations, and
  recent incidents using the same timestamp semantics as the service.
- Deployed the quality-check endpoint to both omicrontheta and RPI. Post-deploy
  health checks returned OK with transcription queues at zero. RPI application
  backup `pizzawave-backup-1861-20260521T184458Z.zip` was created and copied to
  `artifacts/rpi-backups/`; SHA-256 verified as
  `8356bd4a716932b50efee7e62b4f1bdffc907ab7ad29339cf9869b06efc3b29d`.
- WSL2 Debian restore test staged and applied that RPI backup successfully.
  The restore copied 28,367 files, restored the SQLite DB/config/token/TR
  config/talkgroups/audio/Qdrant data, forced setup back to incomplete
  `stack`, and the restored config booted in setup mode. During this test, a
  hidden startup post-processing recovery path was found and removed so pizzad
  no longer sweeps old completed calls for missing post-processing artifacts at
  service start.

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
- profile-disabled talkgroups remain captured/transcribed but are excluded from
  dashboard visibility, alerts, embeddings, and incident creation for that
  active profile;
- catalog-deleted talkgroups are excluded from the generated TR CSV;
- deleted rows leave the catalog and require re-import/manual add to return;
- trunk-recorder only needs restart after catalog changes that regenerate the
  capture CSV.

2026-05-21 deploy corrected the profile/catalog split. The active profile is a
runtime policy overlay for UI visibility and downstream alert/embedding/incident
participation; switching profiles and enabling/disabling a TG in the profile no
longer requires a service restart. Catalog deletion remains a capture change:
Settings > Talkgroups > Apply saves the draft, regenerates the TR CSV, and
restarts trunk-recorder/pizzad only when the catalog itself changed.

The old profile talkgroup picker, separate high-load TG list, and immediate
mutation buttons were removed. High Load is now a filter in the single
Talkgroups table. Enable/disable/delete edit a local draft; Apply is the only
backend mutation point and warns before service-affecting changes.

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
- Latest direct deploy: 2026-05-22 from
  `codex/pizzawave-engine-cleanbreak`.
- Post-deploy checks:
  - `pizzad.service` active;
  - `/api/v1/health` returned `status: ok`;
  - queue depth `0`, pending transcriptions `0`, live ingest running;
  - `/api/v1/dashboard` returned HTTP 200 in roughly 0.10s;
  - no recent `fail`, `error`, `exception`, or `500` lines in the last 80
    `pizzad` journal entries.
  - `/etc/pizzawave/pizzad.json` is `root:pizzawave` mode `0660`, so web
    Settings saves can persist changes.
- Important paths:
  - Config: `/etc/pizzawave/pizzad.json`
  - DB: `/var/lib/pizzawave/pizzad.db`
  - Audio: `/var/lib/pizzawave/audio`
  - TR config: `/etc/trunk-recorder/config.json`

### RPI

- Host: `10.0.0.115`
- Hostname observed over SSH: `sdr1861`
- SSH user: `ocroot`
- SSH key currently used from this workstation:
  `G:\My Drive\Backups\creds\pizzapi_rpi_test_ed25519`.
- Role: Raspberry Pi 5 trunk-recorder/PizzaWave host
- Current transcription provider: remote/OpenAI-compatible faster-whisper
  endpoint on the LAN GPU host (`http://paxan:9187/v1` in the latest check).
- Model: `small`, two live transcription workers.
- PizzaWave URL: `http://10.0.0.115:8080`
- Latest direct deploy: 2026-05-24 from local workspace
  `C:\projects\pizzawave`, using the direct ARM64 tar helper and key
  `G:\My Drive\Backups\creds\pizzapi_rpi_test_ed25519`.
- Latest deployed web bundle is `assets/index-D56-cznD.js` /
  `assets/index-ouIm0RcW.css`.
- Latest 2026-05-24 deploy also fixed Settings/Admin token reveal so the token
  is stored in browser local storage for protected service actions, and changed
  setup job helper path selection to prefer the installed
  `/usr/lib/pizzawave/scripts/pizzawave_setup_admin.sh` path accepted by
  sudoers before the bundled `/opt/...` fallback. The Services restart endpoint
  was verified against `trunk-recorder.service`.
- Earlier feature deployed: System > Metrics > Bandwidth backed by
  `/api/v1/system/remote-bandwidth`. This report is derived from stored calls,
  audio files, and `lm_usage`; it excludes local/loopback endpoints so the RPI's
  current local AI endpoint (`http://localhost:1234/v1`) is not counted as
  remote RTX traffic.
- Post-deploy checks:
  - `pizzad.service` active;
  - `/api/v1/health` returned `status: ok`;
  - queue depth `0`, pending transcriptions `0`, live ingest running;
  - `/api/v1/system/remote-bandwidth` returned HTTP 200 and reported remote
    host `paxan`, transcription included, local AI excluded, and no missing
    audio files in the all-time check;
  - no recent deploy-related `fail`, `error`, `exception`, or `500` lines in the
    `pizzad` journal; the only matching lines in the final check were normal
    HTTP 200 request-finished logs containing the word `error` in the grep
    pattern context.
  - `/etc/pizzawave/pizzad.json` is `root:pizzawave` mode `0660`, so web
    Settings saves can persist changes. The direct tar deploy helper now repairs
    this ownership/mode using `sudo test -f`; the previous helper silently
    skipped the repair when the SSH user could not traverse `/etc/pizzawave`.
- Current RPI recommendation state after deploy: one medium
  `tr-resource-pressure` finding remains. Temperature was normal in the latest
  check (`58.7 C`), but TR remains capture-resource heavy: `trunk-recorder`
  was about `68%` CPU and `3.0 GB` RSS, swap was full, and a TR-launched
  `ffmpeg ... loudnorm` process briefly consumed about `94%` CPU. Treat this
  as the next RPI responsiveness risk now that the dashboard rendering issue is
  fixed.
- 2026-05-19 TR performance trial:
  - disabled trunk-recorder `audio_postprocess.loudnorm` and
    `loudnorm_two_pass` on both RPI and omicrontheta;
  - disabled chatty TGs `1101`, `1129`, `2065`, `2066`, `2067`, `2121`, and
    `47151` in each rig's JSON catalog and regenerated the TR CSV;
  - restarted `trunk-recorder` on both rigs;
  - RPI after 15 minutes: TR CPU averaged about `57%` and fell to about `49%`
    by the end, TR RSS stayed around `160 MB`, recorder exhaustion dropped from
    `63` to `2` per 15 minutes, callstream sends dropped from `169` to `93`,
    no `ffmpeg/loudnorm` processes appeared, and the transcription queue stayed
    clear;
  - OT after 15 minutes: TR CPU averaged about `44%`, TR RSS stayed around
    `245 MB`, recorder exhaustion was `0`, no `ffmpeg/loudnorm` processes
    appeared, and the transcription queue stayed clear.
- RPI backups from the TR performance trial:
  - `/etc/trunk-recorder/config.json.pre-perf-trial-20260519-162851.bak`
  - `/var/lib/pizzawave/appdata/talkgroups.json.pre-chatty-disable-20260519-162851.bak`
  - `/etc/trunk-recorder/talkgroups.csv.pre-chatty-disable-20260519-162851.bak`
- omicrontheta backups from the TR performance trial:
  - `/etc/trunk-recorder/config.json.pre-perf-trial-20260519-162856.bak`
  - `/var/lib/pizzawave/appdata/talkgroups.json.pre-chatty-disable-20260519-162856.bak`
  - `/etc/trunk-recorder/talkgroups.csv.pre-chatty-disable-20260519-162856.bak`

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
- once the new approach holds up, prefer a one-time purge of stale historical
  incident/call data over rebuilding old incidents. The UI is only useful across
  roughly the last week of data, and older history becomes increasingly hard to
  interpret after architecture changes.

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
- 2026-05-19 RAG quality heartbeat: embeddings and queues were healthy on both
  rigs, but AI truncation/verifier pressure remained active. RPI had 67
  truncated AI calls in 3h and 25,857 verifier-truncated calls; OT had 59
  truncated AI calls in 3h and 8,603 verifier-truncated calls. OT also showed a
  likely false/misleading “TBI Command 62 Carter Lane” incident grouping where
  Law Mutual Aid compliance/contact updates at different addresses were grouped
  as one incident.
- 2026-05-19 follow-up deploy: evidence verifier prompt pressure was reduced by
  capping verifier review calls at 14, shortening candidate transcripts, lowering
  verifier output tokens, filtering weak semantic-only candidates, and rejecting
  routine compliance/status rollups unless calls share a concrete event anchor.
  Post-deploy telemetry showed OT verifier runs with max reviewed calls down to
  8 and no verifier truncation; RPI had not run a new verifier pass yet and still
  had a separate incident-extraction truncation at `max_tokens=1800`.
- 2026-05-19 second follow-up deploy: incident extraction itself was made more compact by
  lowering its prompt budget, shortening transcript excerpts, limiting prompt
  candidates to 40, returning at most 8 incident items, and removing
  `call_evidence` from the extractor schema. The evidence verifier remains the
  place where call-level evidence is reviewed. First post-deploy extraction
  cycles on both rigs used 21 candidates and about 12k payloads, with no
  truncation or failure in the observed window.
- 2026-05-19/20 RAG quality follow-up found three active issues: low-confidence
  noisy incidents can still be accepted, incident extraction can still truncate
  at `max_tokens=1800`, and duplicate proposals whose calls already belong to
  existing/concluded incidents were audited as `database upsert returned no row`.
  Deployed follow-up rejects low-confidence proposals unless at least two
  non-noisy calls share the title/detail anchor, rejects single-anchor noisy
  rollups like the OT Maple Street example, caps extraction output to 5 items
  with shorter details, and preflights owned call overlap before DB upsert so
  duplicate/overlap cases get explicit audit reasons. Initial post-deploy
  telemetry showed no new `finish_reason=length` entries, no new
  `database upsert returned no row` audit entries, and OT rejected two weak
  single-anchor proposals.
- 2026-05-20 follow-up added first-class call anchors. Post-transcription
  processing now writes durable `call_anchors` rows for deterministic addresses,
  intersections, highway/mile-marker pairs, vehicle tags, and stored
  transcription-derived locations. Incident validation now loads those stored
  anchors for the recent incident window instead of relying only on ad hoc
  incident-time text extraction. This is intended to make cases like shared
  I-75 mile marker incidents survive minor vehicle-description drift while still
  rejecting single-anchor/noisy rollups. The build was deployed to both RPI and
  omicrontheta; both services came back healthy and the new table was created.
- 2026-05-20 local follow-up adds catalog-backed incident eligibility, generic
  institutional-operations chatter rejection, and operator-visible incident
  audit decisions. Talkgroup catalog rows now carry `incidentEligible`; routine
  calls from ineligible TGs are skipped unless the transcript itself contains a
  strong generic emergency/event signal. The validator also rejects operational
  chatter proposals without a strong anchor, avoiding local/site-specific
  hospital or facility names. System > Metrics > Incidents now shows recent
  `incident_operation_audit` accept/reject reasons. Watch extraction output
  truncation and same-site incident agreement only when two rigs are monitoring
  the same geography before adding further prompt/schema changes.
- 2026-05-20 transport follow-up: transport was separated from generic
  operations chatter. Hospital handoff/transport calls should no longer create
  standalone dashboard incidents, but they remain eligible as supporting calls
  for a parent event such as a crash, fire, rescue, assault, pursuit, or medical
  emergency. Valet remains generic operational chatter. The incident extraction
  prompt/schema was tightened to return at most 3 changed incident items with a
  smaller active-incident context and an explicit empty-response path.
  Post-deploy cleanup backed up both DB/catalog files with suffix
  `20260520T154941Z`, marked generic operations catalog rows
  `incidentEligible=false`, removed recent valet/engineering/shuttle-only false
  incidents from RPI and OT, and restored the RPI elevator-entrapment incident
  because it had a strong event anchor.

### RAG-Assisted Incident Extraction

Current local work has shifted live incident extraction away from a full
rolling candidate dump toward local Qdrant-backed retrieval:

- only complete, `quality_reason='ok'`, non-imported calls enter the live
  incident retrieval path;
- catalog rows can exclude routine institutional/operations talkgroups from the
  live incident retrieval path, with a strong emergency/event transcript signal
  as the escape hatch;
- stored call geolocation rows are used as a first-class retrieval score boost;
- stored call anchors in `call_anchors` are generated immediately after
  transcription and used as deterministic validation evidence;
- a local embedding worker can generate OpenAI-compatible embeddings and upsert
  call vectors into a per-rig Qdrant collection;
- embedding jobs are persisted in `call_embedding_jobs` for new eligible calls;
  broad embedding backfill is intentionally removed;
- active incidents are sent to the LLM as compact state, with strongest recent
  call IDs rather than full call history;
- candidate rows include advisory vector/semantic, geolocation, time, and total
  scores in the prompt;
- incident extraction runs every 90 seconds instead of every 5 minutes;
- incident extraction does not retry failed/truncated LLM calls;
- accepted and rejected incident recommendations are recorded in
  `incident_operation_audit` with call IDs, score metadata, and rejection
  reasons.

The initial validation path used a bounded historical Qdrant embedding dry run
as background work, then compared retrieval quality against known incidents.
Going forward, avoid adding broad historical rebuild/backfill features unless
there is a specific operator-facing need; stale call data should usually be
purged once the live pipeline is stable.

2026-05-19 Qdrant/native rollout checkpoint:

- Deployed the local Qdrant/embeddings build to omicrontheta and RPI.
- Replaced the temporary omicrontheta Docker Qdrant experiment with native
  `qdrant.service`, bound to `127.0.0.1:6333`, with storage at
  `/var/lib/pizzawave/qdrant`.
- RPI uses a native ARM64 Qdrant `1.18.0` binary built with
  `JEMALLOC_SYS_WITH_LG_PAGE=14` because the Pi kernel uses 16 KB pages. The
  stock upstream ARM64 tarball aborts on this kernel with jemalloc page-size
  errors.
- Configured OT embeddings to use LM Studio
  `text-embedding-nomic-embed-text-v1.5` at `http://127.0.0.1:1234/v1`.
- Configured RPI embeddings to use the local LM Studio/lmlink endpoint
  `http://localhost:1234/v1` with the same embedding model.
- During the initial OT study, a now-removed embedding backfill endpoint was
  used for a bounded 24h/500-call experiment. RPI then queued and completed
  roughly the latest 24h/1k calls as background embedding work without
  transcription backlog.
- Final deploy health:
  - OT: `pizzad.service` active, `qdrant.service` active, embeddings `ok`,
    Qdrant points about `1489`, failed embedding calls `0`.
  - RPI: `pizzad.service` active, `qdrant.service` active, embeddings `ok`,
    Qdrant points about `1037`, failed embedding calls `0`.
- The setup wizard now includes a Vector DB/Qdrant step, native install action,
  Qdrant detection, validation, settings fields, and System > Services Qdrant
  service health/actions matching the pizzad/TR service pattern.
- Read-only quality sample against 4 recent multi-call incidents found 4/4 with
  all known source calls retrieved in the top 20. Top extra matches were often
  semantically nearby EMS/fire calls, so the next study should focus on false
  joins and whether geolocation/time reranking plus LLM adjudication rejects
  same-category but unrelated traffic.

Post-deploy observations on 2026-05-16:

- omicrontheta verifier calls were succeeding after compacting the response
  schema; earlier `max_tokens=1800` truncations stopped after the fix;
- RPI AI resumed after the transcription queue drained below the AI gate;
- verifier lossiness is still real under Hamilton volume, with many nearby calls
  omitted by prompt/call-count guardrails.

### Dashboard Incidents And Map

The dashboard incident pane paginates 10 incidents at a time for RPI browser
responsiveness, but the API result itself is not capped at 10. Dashboard is the
primary incident view. Category pages show raw calls by talkgroup for that
category.

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
- uses a selected-location panel with compact incident/source-call evidence.

2026-05-19 RPI dashboard responsiveness follow-up:

- Added a short server-side `/api/v1/dashboard` cache with minute-rounded range
  keys and concurrent-build coalescing so stale or duplicate dashboard clients
  do not force repeated expensive dashboard rebuilds.
- The dashboard incidents pane now paginates 10 incidents at a time with
  top-right pagination controls.
- The RPI dashboard web bundle was made lower-paint: removed the rendered
  location list below the map, removed map tile CSS filters, backdrop blur,
  pulsing selected-node animation, heavy shadows, and dashboard card gradients.
- The user confirmed the dashboard became very fast after this web bundle was
  deployed. Remaining sluggishness should be investigated as system/TR load,
  not dashboard API latency or browser cache.

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

The prior map-usability bundle after deploy was:

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

### 2026-05-27 OT/Paxan AI Routing Checkpoint

- OT/omicrontheta LM Studio is no longer allowed to expose/load local chat
  models by default. JIT local model loading and peer discovery were disabled
  operationally after local Qwen/GPT-OSS models were found on OT.
- OT `aiInsights.openAiModel` should point at the explicit Paxan LM Studio model
  ID `qwen/qwen3.6-35b-a3b@q8_0`; OT embeddings remain local with
  `text-embedding-nomic-embed-text-v1.5`.
- The AI model picker now prefers LM Studio's native model list so it displays
  loadable model IDs rather than loaded runtime aliases/monikers.
- 2026-05-28 follow-up deployed this fix to omicrontheta and changed its saved
  AI Insights model from the old runtime alias to
  `qwen/qwen3.6-35b-a3b@q8_0`.
- Paxan's Unsloth Qwen3.6 GGUF required a Template Jinja override:
  `{%- set enable_thinking = false %}` as the first line. After reload, direct
  Paxan and OT-through-LM-Link tests returned valid JSON in `message.content`,
  empty `reasoning_content`, `reasoning_tokens=0`, and `finish_reason=stop`.
- `setup-lmstudio.sh` now installs a conditional local embedding preload hook.
  It loads the configured embedding model on LM Studio startup only when
  embeddings are enabled, `executionMode=local`, and the embedding endpoint is
  localhost:1234.
- Setup/settings UI now exposes `executionMode` for AI Insights and embeddings
  so operator intent is visible even though the base URL remains the actual
  request destination.

Health:

```powershell
ssh -o BatchMode=yes -o IdentitiesOnly=yes lilhoser@192.168.1.173 'curl -fsS http://127.0.0.1:8080/api/v1/health'
ssh -i 'G:\My Drive\Backups\creds\pizzapi_rpi_test_ed25519' -o BatchMode=yes -o IdentitiesOnly=yes ocroot@10.0.0.115 'curl -fsS http://127.0.0.1:8080/api/v1/health'
```

Deploy omicrontheta:

```powershell
C:\projects\pizzawave\scripts\deploy_pizzad_tar.ps1 -HostName lilhoser@192.168.1.173 -Rid linux-x64
```

Deploy RPI:

```powershell
C:\projects\pizzawave\scripts\deploy_pizzad_tar.ps1 -HostName ocroot@10.0.0.115 -SshKey 'G:\My Drive\Backups\creds\pizzapi_rpi_test_ed25519' -Rid linux-arm64
```

Rebuild the RPI-compatible native Qdrant binary if Qdrant is upgraded or the
Pi is rebuilt with the same 16 KB page-size kernel:

```powershell
ssh lilhoser@192.168.1.173 'git clone --depth 1 --branch v1.18.0 https://github.com/qdrant/qdrant.git /tmp/qdrant-src-check'
ssh lilhoser@192.168.1.173 'cd /tmp/qdrant-src-check && sudo docker buildx build --platform linux/arm64 --build-arg JEMALLOC_SYS_WITH_LG_PAGE=14 --build-arg PROFILE=release -t pizzawave-qdrant-arm64-16k:v1.18.0 --load .'
ssh lilhoser@192.168.1.173 'cid=$(sudo docker create pizzawave-qdrant-arm64-16k:v1.18.0); sudo docker cp "$cid:/qdrant/qdrant" /tmp/qdrant-arm64-16k; sudo docker rm "$cid"'
scp lilhoser@192.168.1.173:/tmp/qdrant-arm64-16k $env:TEMP\qdrant-arm64-16k
scp -i $env:USERPROFILE\.ssh\pizzapi_rpi_test_ed25519 $env:TEMP\qdrant-arm64-16k ocroot@192.168.2.42:/tmp/qdrant-arm64-16k
ssh -i $env:USERPROFILE\.ssh\pizzapi_rpi_test_ed25519 ocroot@192.168.2.42 'chmod 0755 /tmp/qdrant-arm64-16k; sudo install -m 0755 /tmp/qdrant-arm64-16k /usr/local/bin/qdrant; sudo /opt/pizzawave/pizzad/scripts/setup-qdrant.sh'
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
