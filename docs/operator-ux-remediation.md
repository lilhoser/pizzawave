# Operator UX Remediation

Last updated: 2026-07-18

This is the canonical execution tracker for the operator-facing PizzaWave
architecture review. Read it before beginning work and update it before ending
work so that progress does not depend on conversation history.

## Current Position

- Active package: 11 - Cleanup and final regression.
- Current milestone: Package 11 cleanup is implemented, deployed to the RPI,
  and live-verified. Operator acceptance remains. Package 7 remains a standalone
  outstanding feature outside this closeout sequence.
- Continuation: obtain Package 11 operator acceptance, then merge it to `main`
  and close the operator-remediation sequence.
- Last deployed code state: Package 11 dead-code and retired-editor removal plus
  an owned, lazy-loaded React Leaflet Dashboard map, deployed and verified on
  the RPI 2026-07-18.
- Package 9 final implementation commit before documentation closure: `2b6caa4`
- Operator verification: Packages 1, 2, 3, 4, 5, 6, 8, 9, and 10 accepted
- Next action: obtain Package 11 operator acceptance. Do not restart Trunk
  Recorder unless the operator explicitly requests it.

## Package 5 Final Handoff

Package 5 code is implemented, tested, deployed, exercised against live
hardware, and operator-accepted. The 2026-07-17 closure remediation was
reconciled with the merged redesign and live-verified on the RPI.

Final live state from 2026-07-10:

- Desired Setup version `1783704270742` selects ETV Raymond Hinds and Entergy
  Jackson Hinds on two serial-pinned Airspy Minis.
- Source 0 is `637862DC2E3A19D7`, center 771.931250 MHz, 6 MHz sample rate,
  gain 21, saved correction +3600 Hz. Source 1 is `637862DC2F5C0CD7`, center
  855.287500 MHz, 6 MHz sample rate, gain 21, saved correction +4155 Hz.
- RF Sweep `rfx-20260710172440-cd3af2279a6` passed both selected sites: ETV
  Raymond at 773.781250 MHz and Entergy Jackson at 855.287500 MHz.
- Apply & Resume initially faulted because the generated Jackson system pointed
  at nonexistent `talkgroups.jackson-ms-hinds-ms.csv`. Commit `60a566f` now
  resolves site RadioReference SID ownership through the imported catalog and
  rejects a draft before install if any referenced talkgroup file is missing.
- The same operator-selected plan was regenerated and applied without changing
  site, RF, source, gain, center, or correction choices. Applied hash is
  `4e70205f79b1aba9e8c8688ca36fae03da7a313e507babd0b14ada613b3817df`.
  Live config uses `talkgroups.mswin.csv` for ETV and
  `talkgroups.entergy.csv` for Jackson.
- Trunk Recorder was active after recovery and immediately decoded ETV RFSS
  002/site 008/system 2AD and Jackson RFSS 002/site 080/system 64F. The Setup
  UI still said `stale` at the last read because its live-activity timer had not
  yet received a fresh event; this is the first item to recheck, not evidence
  that the config remained faulted.
- Tylertown was correctly removed from the selected plan after genuine RF
  failure. Its current RadioReference Entergy control channels were weak at the
  Hinds installation, neither standalone demodulator decoded P25, and the
  isolated native 855.587500 MHz trial stayed at 0 messages/second. Do not
  weaken validation or force that remote site to pass.

Implemented Package 5 outcomes to preserve:

- RF Validation uses Preparation, Spectrum Inspection, Control-Channel Proof,
  Source Coverage, Call and Transcription Proof, and Verdict with concise
  operator guidance and standardized frequency-correction terminology.
- Source planning is server-owned and reviewable. Apply & Resume is the only
  live-config write boundary.
- Hardware & RF Path is one simple ordered physical-hardware list. The operator
  explicitly rejected upstream branches, trees, and SDR endpoint topology.
- Waterfall/RF evidence is source- and serial-bound. Measured signal offset is
  evidence and never silently replaces a saved SDR crystal correction.
- Spectrum/waterfall plots share one frequency geometry; selected-site control
  channels are red, persistent unidentified suspects are yellow, popup content
  fits, and the candidate table has stable alphabetical ordering.
- Candidate identity is not inferred from frequency-only matches across the
  statewide catalog. Candidates are limited to the active SDR span and require
  P25 evidence before acquiring a site identity.
- RF proof isolates one control channel, permits native Trunk Recorder proof
  after an inconclusive standalone OP25 probe, waits for scoped readiness before
  timing metrics, and uses exact saved per-device corrections.
- Setup owns append-only RF activity/evidence and the cross-session Experiments
  & Evidence viewer. Preparation tooling checks are once per applied revision
  with explicit Recheck after failure.
- Location authority is derived from selected sites and talkgroup jurisdiction;
  legacy Monitored Area records are compatibility data, not active Setup
  authority and are not shown as editable Setup locations.

Closure completed 2026-07-17: the final safety and operator-action remediation
passed all 498 backend tests and the production frontend build, was reconciled
with the merged redesign, deployed to the RPI, and live-verified with healthy
monitoring. The operator accepted Package 5 and directed that it be closed.

### 2026-07-11 Operational Follow-Up

This context was collected after the Package 5 worker handoff and must travel
with it. It is diagnostic evidence, not a request to alter the validated source
plan.

- ETV Raymond was stable at approximately 39-40 control-channel messages per
  second until 2026-07-10 19:13:20 EDT. It then fell through 26, 13, 11, 6,
  and 5 messages per second. At 19:13:38 it began retuning among 773.031250,
  773.531250, 773.281250, and 773.781250 MHz. Entergy remained near 38-40
  messages per second at the same time.
- Trunk Recorder remained one continuous process at the onset. There was no
  source reopen, service restart, USB disconnect/reset, libusb transfer error,
  overcurrent event, or Airspy-open failure in the available logs. ETV voice
  tuning errors remained roughly +400 to +1100 Hz, which does not support a
  large temperature-driven oscillator shift.
- The operator manually rebooted the RPI around 2026-07-11 00:54 EDT. After
  reboot, ETV averaged about 11.92 messages per second with a 34.45% zero rate,
  approximately 2,367 retunes, 852 calls, 49 no-transmission calls, and no
  source-coverage failures in the inspected overnight window. Entergy showed
  no decode because the applied config retained only 854.062500 MHz while the
  strong learned pre-reboot alternate had been 855.662500 MHz. Treat that as
  control-channel evidence to review, not permission to mutate Setup silently.
- A Data-only Reset was run around 2026-07-11 07:48 EDT. It preserved the
  applied TR config hash `e95b8867...`, left ingestion paused, and removed the
  Qdrant collection, which consequently returned 404 until normal reset
  recovery/recreation. Much pre-reset operational history and the previous
  boot's volatile kernel journal are no longer available.
- Current hardware checks after reboot found both Airspy Minis enumerated at
  480 Mbps on separate USB 2 root buses. Kernel USB autosuspend is disabled,
  Raspberry Pi temperature was approximately 56-57 C, and
  `vcgencmd get_throttled` returned `0x0`. These observations do not prove the
  Airspy enclosure temperature stayed within its rated range before reboot,
  because the Airspy exposes no temperature telemetry.
- The evidence does not show an Airspy physically going offline at 19:13. The
  failure is more consistent with an ETV-specific RF path, interference, site
  behavior, or one receiver's analog front end than a host-wide USB or thermal
  event. A future controlled diagnosis should swap receiver serials or RF
  branches one variable at a time and persist USB/thermal telemetry across
  boots.
- A separate live software defect was observed around 07:52 and 07:53 EDT:
  PizzaWave stopped Trunk Recorder, ran `airspy_info`, and restarted Trunk
  Recorder. This matches `SetupJobService.DetectSdrsAsync`, which performs
  disruptive inventory by design. No contemporaneous
  `/api/v1/setup/sdrs` request appeared in HTTP logs, so an in-process RF
  experiment or stale background operation is the likely caller but is not yet
  proven. Inventory must not interrupt monitoring unless an explicit disruptive
  Setup action owns the stop/restart boundary.
- The same inspection found a Setup request storm, including roughly 680
  `/api/v1/setup/status` requests in two minutes and approximately 494-496
  requests to several general endpoints in a 70-second slice. The status route
  itself does not run `airspy_info`, but the polling flood is a distinct
  frontend/session-lifecycle defect worth resolving after Package 5 acceptance.

The disruptive SDR inventory ownership and Setup polling storm described above
were resolved by the closure remediation. Historical RF observations remain
evidence only and do not reopen Package 5.

## Working Rules

- Complete one vertical milestone before starting another.
- Commit each coherent milestone separately.
- Build, test, deploy, and verify the live workflow before requesting operator
  inspection.
- Ask the operator in chat before material product or data-model choices.
- Record accepted decisions and operator verification here.
- Do not preserve obsolete RSW or standalone TR-draft behavior for backwards
  compatibility when Setup has replaced it.

## Packages

### 1. Setup State Authority

Status: complete

- [x] 1A. Replace stale whole-document Setup writes with versioned,
  field-scoped mutations.
- [x] 1B. Represent RadioReference identity per selected system/site and remove
  the global-SID selection assumption.
- [x] 1C. Persist waterfall/RF selections in Setup and replace the standalone
  TR-editor draft handoff with a Setup-owned calibration action.
- [x] 1D. Confirm pending-change and activity records cover every resulting
  Setup mutation.
- [x] Build and test.
- [x] Deploy and verify on the RPI.
- [x] Operator acceptance.

Acceptance:

- Multiple selected RR systems retain their own IDs and site data.
- Concurrent or delayed UI saves cannot silently overwrite newer Setup state.
- Waterfall and RF Sweep selections survive navigation and browser reload.
- Applying an RF candidate changes Setup desired state, creates an auditable
  pending change, and never writes an orphan standalone TR draft.
- Apply & Resume is the only workflow that installs the final TR config.

### 2. Talkgroups

Status: complete

- [x] Move one-time RR import provenance to the server.
- [x] Replace full-catalog writes with scoped, paged mutations.
- [x] Route category and TR-exclusion changes through Setup activity and apply
  state.
- [x] Deploy, verify, and obtain operator acceptance.

### 3. Loading And Status

Status: complete and operator-accepted

- [x] 3A. Give Dashboard, call-category pages, Setup, and System a shared
  retained-data refresh contract; separate monitoring state; scope automatic
  updates; and make search page-owned.
- [x] 3B. Apply the accepted refresh contract to Settings and specialized
  page-local data panels without preempting Package 4's independent System-tab
  architecture.
- [x] Add page-local loading, refreshing, error, retry, and last-updated state.
- [x] Keep last-good data visible during refresh.
- [x] Debounce search and stop unrelated full-page refreshes.
- [x] Separate API connectivity, TR monitoring state, and view-fetch failures.
- [x] Deploy, verify, and obtain operator acceptance.

### 4. System Workspace

Status: complete and operator-accepted

- [x] Load each System tab independently.
- [x] Make Refresh contextual to the visible tab.
- [x] Surface only job controls supported by each job.
- [x] Reorganize System navigation around operator tasks.
- [x] Deploy and verify.
- [x] Operator acceptance.

### 5. Setup UX

Status: complete and operator-accepted

Discovery findings:

- RF validation already persists RF sessions and experiment evidence, and the
  deployed Setup UI exposes Waterfall and RF Sweep. The canonical preflight,
  tool-prep, control-channel, source-coverage, voice/transcription, and verdict
  stages are not presented as one clear operator workflow. Frequency correction
  is also described inconsistently as error, base error, target error, offset,
  peak delta, and PPM.
- Config Draft is server-built, uses RF validation evidence, and produces a
  live-versus-candidate review. The selectable plan alternatives, fit decisions,
  tuning windows, and source centers shown before that review are still computed
  in the browser, so source planning is not yet a server-owned reviewable
  projection.
- Setup records a single ordered RF chain. Splitters and multicouplers can record
  an output count, but the model cannot represent downstream branches, and SDR
  chain items are not linked to detected/configured source hardware.
- RF experiments already write database rows and self-contained artifact files.
  Waterfall start/stop and final apply create Setup activity, but general RF
  runs, captures, and generated reports are not attached as first-class Setup
  evidence/activity records; the waterfall image report is generated only in
  the browser.

Accepted design:

- RF Validation will present one visible staged workflow: Preparation,
  Spectrum Inspection, Control-Channel Proof, Source Coverage, Call and
  Transcription Proof, and Verdict. Existing controls remain available inside
  the applicable stages.
- `Frequency correction` is the signed value saved to an SDR source. `Measured
  signal offset` is the observed displacement in spectrum evidence. `Correction
  change` is a trial value relative to the saved correction. PPM is derived
  technical information rather than a separate editable authority.
- Source planning belongs to RF Validation's Source Coverage stage. The server
  returns one recommended plan plus valid alternatives, with exact source
  assignments, centers, usable windows, covered and missed frequencies,
  evidence, assumptions, warnings, and a projection version tied to Setup.
  The browser sends operator intent and optional assignment constraints but
  does not calculate fit or tuning windows. Selecting a plan persists the
  server projection for review; stale projections are rejected and regenerated.
  Apply & Resume retains only final configuration comparison, Discard, and the
  guarded apply action.
- Hardware & RF Path remains a straightforward documented list of physical RF
  hardware in signal order. Setup does not model upstream paths, trees,
  branches, or SDR endpoints here. SDR Inventory is a separate prominent action
  at the top of the page.
- Each RF run has a stable run identity and append-only started and terminal
  activity events that the UI groups as one run. Terminal events reference
  first-class evidence records indexed in the database and stored in the RF
  artifact folder. Evidence records identify the site, stage, experiment,
  source hardware, RF-path and source-plan revisions, capture window, media
  type, size, and content hash. Measurements are immutable; operator notes are
  separate annotations. The server owns durable waterfall images and reports.
  Compact measurements, representative images, logs, configuration comparisons,
  reports, and required audio persist by default. Raw IQ retention is explicit
  opt-in with size and retention warnings.
- RF Validation includes one cross-session Experiments & Evidence history that
  defaults to the current site and can be searched and filtered across prior
  Setup sessions. Short experiments receive an editable operator name with an
  automatic descriptive default and may record a hypothesis or physical
  change. The viewer snapshots run inputs and revisions, supports side-by-side
  comparison, repeat with changed-context warnings, annotations, and copying
  successful settings into reviewed pending Setup changes. Historical evidence
  never changes live monitoring directly.
- Preparation runs one read-only required-software check on its first open for
  each applied Setup revision. A successful Apply & Resume that persists
  changes invalidates the prior result; navigation, discard, and failed apply
  do not. Failed checks expose Recheck. SDR Inventory and any generated or
  changed configuration remain separate explicit operations.
- Location context normally comes from the RadioReference jurisdiction attached
  to the call's talkgroup, with the selected site's RadioReference geography as
  fallback. Manual geographic boundaries are explicit overrides only when the
  imported data is missing or incorrect. Legacy Monitored Areas are not treated
  as RF coverage and are not silently retained as active location authority.
- Setup's three persistent guidance cards show current scope (selected sites and
  hardware), validation progress (the next task or blocking issue), and apply
  plus monitoring state. Last setup change is audit history and belongs in the
  Activity Log rather than the persistent guidance row.
- Control-Channel Proof defaults to one recommended run showing the selected
  site, source, control channel, estimated duration, and Run action, followed by
  live progress and measured results. Sample rate, gain sequence, correction
  changes, and candidate limits remain available under Advanced settings.
- Every RF Validation stage uses the same concise hierarchy: one-sentence goal,
  current status and single next action, primary working surface, measured
  results/evidence, and collapsed advanced or technical detail. Preparation
  collapses healthy tooling, Spectrum leads with capture and retained
  candidates, Source Coverage leads with the recommended plan and exceptions,
  Call and Transcription Proof exposes only the next eligible proof action, and
  Verdict leads with the decision, decisive blockers, evidence, and next step.
  The repeated six-stage status block becomes a compact progress stepper.
- A Waterfall Use decision belongs to the SDR that measured it. Setup persists
  both source index and hardware serial with the control channel, gain, sample
  rate, frequency correction, and measured quality. Control-Channel Proof runs
  only those exact SDR/control-channel pairs, rejects stale serial mappings,
  and carries the same per-SDR settings into P25, monitoring, and voice checks.
- Waterfall signal displacement remains `Measured signal offset` evidence and
  is never copied directly into source configuration. RF Sweep reconciles one
  PPM correction per SDR crystal, uses only strong agreeing observations,
  falls back to the saved correction for weak observations, and blocks
  contradictory strong measurements before hardware access. RF power evidence
  is the median of windows distributed across the capture and considers only
  the +/-8 kHz carrier neighborhood. TR proof duration starts after the scoped
  site/control channel emits its first decode-rate measurement; readiness
  timeout is blocked rather than scored as an empty failed trial.

- [x] Clarify RF validation stages and frequency-correction terminology.
- [x] Make source planning a server-owned, reviewable projection.
- [x] Keep RF-path documentation as one straightforward ordered hardware list.
- [x] Attach RF captures and reports to Setup evidence/activity.
- [x] Deploy and verify on the RPI.
- [x] Obtain operator acceptance.

### 6. Profiles And Alerts

Status: accepted

Discovery findings:

- Profiles model Police, Fire, EMS, Traffic, and Other, while the active catalog
  and navigation have a separate Utilities category. `DownstreamProfilePolicy`
  consequently treats Utilities as Other, so operators cannot express the
  first-class Utilities policy already implied by the product taxonomy.
- Profile edits live in component-local state and are excluded from Settings'
  dirty-section calculation. Browser unload, Settings navigation, active-profile
  switching, and reload protection therefore do not consistently see an
  unsaved profile draft.
- `Save Profiles` sends the currently edited profile as `activeProfileId`, so
  saving a profile draft also activates it. Editing and activation are separate
  operator intents and should not share one implicit write.
- The active-profile and profile talkgroup-hide endpoints mutate persisted
  configuration but currently check read authorization instead of write
  authorization.
- Alert rules scope talkgroups by decimal ID only. The catalog and profile model
  already use system plus talkgroup identity, so the same numeric ID on two
  systems can currently match the wrong system.
- The Alerts UI writes match types `police-code` and `both`, while
  `EngineAlertService` recognizes only `police_code`; all other values follow
  the keyword-only path. Police-code-only and combined rules therefore do not
  implement the policy shown in the editor.
- Alert matching is intentionally downstream of the active profile, but the
  Alerts editor does not show that effective scope. Documentation also says
  imported calls store alert matches, while the current transcription pipeline
  suppresses alert evaluation for imported calls.
- `ProcessingProfile.allowedTalkgroups` remains in the persisted contract but
  has no active policy consumer. The supported per-system profile talkgroup
  settings are the actual authority.

Accepted implementation decisions:

- Add first-class Utilities visibility with a migration-safe default that
  preserves current inclusion until an operator changes it.
- Integrate profile drafts into the shared Settings dirty-state and discard
  protection contract.
- Separate Save from Activate in both UI intent and API behavior; editing a
  profile must not silently change the live active profile.
- Require write authorization for every profile mutation and add focused API
  coverage.
- Replace alert talkgroup selections with required typed system-plus-talkgroup
  references. Numeric-only or unscoped talkgroup rules are unsupported and must
  be rejected rather than evaluated or migrated as cross-system rules.
- Normalize alert match types through one server-owned typed contract and test
  keyword-only, police-code-only, and combined behavior.
- Show the effective active-profile scope in Alerts and keep alert configuration
  separate from profile configuration rather than duplicating policy controls.
- Retire the unused `allowedTalkgroups` field as ignored legacy input instead of
  making it a second policy authority; the next normal profile save omits it.

Historical/offline call audit:

- The former Avalonia application provided local-folder and SFTP archive
  browsing, date filtering, download/cache, MP3 and Trunk Recorder `.bin`
  loading, progress/cancel handling, and a distinct offline/archive session.
- That operator workflow and its `OfflineCallManager` and `ArchiveSftpService`
  were removed during the persistent-service/web migration. The current web UI
  has no historical import page, Settings section, or supported start endpoint.
  Existing `sftp_import` and `local_import` job controls explicitly return that
  historical import jobs are no longer supported from the web application.
- The service still persists `is_imported`, retains imported-alert schema fields,
  accepts an internal `imported` argument in the ingest pipeline, and has a
  lower-priority backlog queue. No active caller submits a call with
  `imported=true`, so these pieces do not form a usable feature.
- If an imported pending row already exists, startup recovery can transcribe it
  as backlog work, but current pipeline suppression prevents alerts, embeddings,
  summaries, and incidents. Documentation that claims imported alert matches
  are stored describes the old intended behavior, not current execution.
- Restoring offline/archive calls would be a new bounded product package, not a
  Package 6 alert compatibility patch. It needs explicit source ownership,
  deduplication, resource limits, progress/cancel behavior, downstream policy,
  and separation from live transcription capacity before implementation.

- [x] Add first-class Utilities profile policy.
- [x] Protect unsaved profile changes.
- [x] Scope alert talkgroups by system and TG ID.
- [x] Correct alert match-type evaluation and profile mutation authorization.
- [x] Correct profile navigation and contextual controls.
- [x] Deploy and verify live service and UI artifacts.
- [x] Obtain operator acceptance.

### 7. Offline And Archive Calls

Status: deferred standalone outstanding feature; no further work in this
operator-remediation closeout

Restore offline/archive calls as a first-class persistent-service and web
workflow without allowing historical work to interfere with live monitoring.
The deleted Avalonia implementation is design evidence, not code to transplant
unchanged.

Accepted design contract:

- Imported data never merges into the live operational dataset. Both SFTP
  callstream archives and PizzaWave support packages open in physically
  isolated, persistent workspaces and cannot emit live alerts, notifications,
  recommendations, incidents, or mutations.
- Workspaces persist until explicit deletion. A top-level Workspaces area owns
  the library, package creation/import, storage, and processing jobs. Selecting
  a workspace opens workspace-specific Summary, Calls, Incidents, Performance,
  Configuration, Logs, and Processing views with an unmistakable context
  indicator; selecting Live System returns to the prior live page.
- Opening a package validates and inventories existing evidence only. New
  transcription, embeddings, incident analysis, or other derived processing is
  always operator-selected after a workload, duration, storage, token, and cost
  estimate.
- Support packages use a purpose-built versioned format, not the Backup and
  Restore archive. They include useful configuration, TR/talkgroup state,
  system facts, selected-window logs and metrics, calls, transcripts,
  incidents, decisions, jobs, and optionally audio. They exclude credentials,
  authentication tokens, private keys, scripts, binaries, models, caches, and
  vector-store data. Audio is explicit opt-in.
- Support packages and derived analysis use one round-trippable format and one
  UI. Exporting an analyzed workspace produces another complete support package
  containing immutable originating evidence plus labeled derived runs; a
  hash-linked delta/response format is deferred unless package size proves it
  necessary.
- Each processing run uses a workspace-local profile populated from locally
  available models/endpoints. Originating configuration remains visible
  evidence and is never applied automatically. Runs snapshot their software,
  pipeline, prompt, model, endpoint, and processing settings and coexist for
  comparison without overwriting original or prior results.
- Live receipt and durable persistence remain active during workspace work.
  Background work yields automatically under live queue age/growth, CPU,
  memory, disk, and endpoint pressure. Operators may pause, resume, or cancel,
  but cannot override automatic live protection.
- Import Priority is the deliberate high-priority mode. It continues accepting
  and durably storing live calls while deferring their transcription,
  embeddings, insights, and incident processing. It shows the live backlog,
  restores normal priority automatically on completion/failure/cancellation,
  and drains deferred live work normally. A PizzaWave restart disables Import
  Priority, pauses the workspace job, and resumes live processing first. The
  workflow never offers a control that drops incoming callstream payloads.
- Queue wait, active execution, paused/throttled time, and total wall-clock time
  are persisted separately for live and workspace stages. Per-attempt
  transcription and AI endpoint duration, calls, audio seconds, tokens,
  retries, failures, and pricing inputs are retained so outages are not
  misreported as compute time.
- SFTP profiles retain only non-secret connection facts; credentials remain at
  the credential boundary. Remote archives are read-only. Inventory defaults
  to 60 minutes, precedes download, and reports scope/cost/capacity. Repeated
  scans may append to a workspace, with durable per-scan provenance and
  payload/content-hash deduplication across overlapping or renamed files.
- Completed stage output survives pause, cancellation, restart, or later-stage
  failure. Resume handles unfinished work only; retries and new processing
  profiles create distinct attempts/runs. Partial state is visible and is
  removed only through an explicit run or workspace deletion action.
- Source packages are retained unchanged beside extracted and derived data.
  Storage is reported by category, preflight accounts for expanded/derived
  size, disk safety reserves can refuse or pause work, and nothing expires or
  deletes automatically.
- Preflight reports local/remote stage ownership, expected duration ranges,
  tokens, storage, and separate OpenAI-equivalent transcription/text costs with
  estimate confidence. Completed runs report actual work and use a versioned,
  editable rate card rather than permanently hard-coded provider prices.
- The current Backup and Restore format may contain the PizzaWave token and
  other configured credentials. Portable backup encryption and recovery-key
  management are a required focused follow-up; legacy unencrypted backups must
  be identified without silently deleting or rewriting them. This is separate
  from sanitized support packages.

Implementation handoff:

- `WorkspaceModels.cs` defines the central workspace catalog, processing-run,
  stage-attempt, and metric-delta contracts. `EngineDatabase.Workspaces.cs`
  persists the catalog and timing ledger in the live PizzaWave database; actual
  workspace evidence will live under separate physical workspace roots and is
  not represented as imported rows in the operational call tables.
- Stage attempts persist queue wait, active work, pauses, total wall time,
  endpoint time, items, audio duration, tokens, retries, failures, pricing
  version, and actual/estimated cost. Lifecycle validation prevents a terminal
  attempt from being restarted. Focused lifecycle tests and all 495 backend
  tests pass.
- This foundation has no API, UI, scheduler integration, or production
  deployment yet. It cannot affect live capture. A future standalone Package 7
  task should begin with the
  versioned support-package creator, validator, inventory, isolated filesystem
  store, Workspace Library, and read-only Summary view; deploy that complete
  operator-visible slice for review before adding processing or SFTP.
- Current live transcription recovery is not sufficient for Import Priority:
  audio and pending call state are durable, but scheduling still uses in-memory
  queues and startup recovery loads at most 5,000 pending calls. Replace that
  boundary with durable paged claiming before enabling Import Priority.

Feasibility evidence, using a fixed end of 2026-07-17 08:10 EDT:

- The combined seven-day sample contained 99,055 calls, 349.52 hours of audio,
  8,269 AI requests, and 18.96 million text tokens. The RPI retained about 6.94
  rather than exactly seven days. At the accepted OpenAI-equivalent text rates,
  the measured text work is about $59.69 before any cloud audio-transcription
  charge.
- Healthy 24-hour median/p95 ingest-to-transcription latency was 1.86/10.80
  seconds on OT and 0.79/2.49 seconds on RPI. A recent RPI endpoint outage made
  cumulative latency misleading: 1,193 calls waited over an hour and together
  accumulated about 5,600 waiting-hours, not compute-hours. Preserve separate
  queue, active, paused, endpoint, and wall timing throughout Package 7.
- A seven-day workspace is a substantial bounded batch workload, not an
  interactive import. Inventory and metadata-only opening are cheap; all new
  transcription, embeddings, and incident analysis remain explicit,
  estimated, cancellable stages subordinate to live work.

- [x] Define supported sources and ownership: versioned support-package upload
  and read-only SFTP callstream archives only for the first implementation.
  Arbitrary local/server folder ingest and live-database merge are unsupported.
- [x] Define canonical workspace identity and deduplication rules
  across repeated scans, renamed files, overlapping archives, and restored
  PizzaWave evidence using source identity, payload identity, and content hash.
- [ ] Run discovery, download, validation, import, transcription, and optional
  downstream processing as bounded auditable background jobs with progress,
  cancellation, resumability, and per-stage failure reporting.
- [ ] Keep live transcription capacity authoritative. Historical work must use
  a separately bounded queue, yield under live pressure, and expose estimated
  audio volume, storage cost, and completion time before starting.
- [ ] Preserve source provenance, original timestamps, system/talkgroup
  identity, audio hashes, import session identity, and operator-selected scope.
- [ ] Provide dry-run inventory and validation before persistence, including
  unsupported/corrupt formats, missing metadata, duplicates, and estimated work.
- [ ] Decide downstream policy explicitly for transcription, quality
  classification, alerts, embeddings, summaries, incidents, and geocoding.
  Historical records must never emit live email, autoplay, or live-event SSE
  notifications.
- [ ] Separate archive browsing and imported historical review from the live
  Dashboard while still allowing an operator to inspect calls, transcripts,
  provenance, and job results.
- [ ] Store SFTP credentials through the existing credential boundary; do not
  persist passwords or private-key passphrases in normal configuration or job
  artifacts.
- [ ] Add format fixtures, deduplication tests, queue-pressure tests, restart and
  resume tests, authorization tests, and bounded end-to-end API/UI coverage.
- [ ] Reconcile or remove the current dormant `is_imported`, imported-alert,
  backlog, and unsupported job-control compatibility paths so one implemented
  contract owns the feature.
- [ ] Update operational limits and archive/import documentation, then deploy,
  verify with a small non-production sample, and obtain operator acceptance.

### 8. System Information Quality

Status: complete and operator-accepted

Package 4 established System workspace ownership, independent page loading,
contextual refresh, and supported job controls. This package separately reviews
the information architecture and content quality across every System area.

Section acceptance checkpoints:

- [x] Recommendations — implemented, tested, deployed, live-verified, and
  operator-accepted.
- [x] Runtime — implemented, tested, deployed, live-verified, and operator-
  accepted.
- [x] Data — Storage, Backup and Restore, Reset, and Audit History are
  implemented, tested, deployed, live-verified, and operator-accepted.
- [x] Trunk Recorder — Summary, Config Viewer, and Logs implemented, deployed,
  live-verified, and operator-accepted.
- [x] Performance — implemented, tested, deployed, live-verified, and
  operator-accepted.
- [x] Cross-page time and layout consistency — implemented, tested, deployed,
  live-verified, and operator-accepted.

- [x] Inventory every System area, page, panel, metric, status, recommendation,
  history view, and action, including the API fields that supply them.
- [x] Interview the operator section by section and record the intended question,
  evidence, action, terminology, time behavior, and ownership boundary.
- [x] Define the operator question and decision supported by each item; remove,
  combine, relocate, or demote information that has no clear operational value.
- [x] Give each page a consistent hierarchy: current state, operational impact,
  recommended next action, and supporting evidence or technical detail.
- [x] Make summaries concise while preserving drill-down evidence, timestamps,
  measurement windows, units, provenance, and audit history where needed.
- [x] Standardize health states, severity, terminology, units, time windows,
  empty/loading/error states, and the distinction between live, retained,
  estimated, and historical information.
  The global call-data selector must not drive System metadata, logs, metrics,
  RF evidence, bandwidth, audit, or job history; each applicable System page
  owns an explicit local window selector.
- [x] Make recommendations actionable by linking to the exact safe control or
  workflow, and clearly distinguish observation, recommendation, and mutation.
- [x] Remove duplicated, contradictory, stale, low-signal, or implementation-
  centric content that obscures the operator's next decision.
- [x] Audit every job producer for dashboard visibility, asynchronous resource
  locking, truthful safe cancellation, and cleanup at cancellation boundaries.
- [x] Make routine storage maintenance automatic and show its job history;
  remove manual vacuum, optimize, and prune controls.
- [x] Add focused API and UI tests for the resulting information contracts.
- [x] Deploy and verify the live System workspace without restarting Trunk Recorder.
- [x] Obtain operator acceptance.

### 9. Temporal Pattern Analysis

Status: complete, deployed, live-verified, and operator-accepted

Accepted design:

- Store immutable measurements as evidence, typed signal interpretations as
  conditions, correlated time-bounded behavior as episodes, recurrence or
  persistence across episodes as patterns, and downstream consequences as
  impacts. Operator-facing findings wrap episodes, patterns, or standing target
  gaps. Relationships form an evidence graph rather than a strict tree.
- Consolidate related conditions into one actionable parent Recommendation.
  Contributing conditions remain visible in its evidence and on the owning
  Performance charts, but do not appear as separate Recommendation cards.
- Scope findings to the narrowest evidence-supported owner (site, SDR/source,
  RF path, rig, or downstream subsystem) and roll upward only when correlation
  supports it. Keep observed scope separate from cause hypotheses.
- Represent repeated symptom signatures, cadence, schedule association,
  persistence, and trends independently. A reliable twice-weekly signature is
  meaningful even without a stable day or time. Materially different symptom
  signatures remain separate patterns.
- Keep community targets visible even when a mature local baseline is worse.
  Local normality may prevent false anomaly claims but never turns a target gap
  into healthy performance.
- Use deterministic typed detectors as the authority for episodes, patterns,
  severity, confidence, equivalence, and material change. AI may summarize or
  phrase hypotheses but cannot invent evidence or own product behavior.
- Separate operator workflow state from derived activity. Workflow states are
  New, Unresolved, Investigating, Known Issue, Monitoring, Resolved, and
  Dismissed. Only operators change workflow state after creation; PizzaWave
  owns severity, confidence, activity, correlations, and evidence updates.
- Audit every PizzaWave and operator action. Notes are append-only. Operators
  can confirm or reject cause hypotheses, merge or split ambiguous patterns,
  mark historical maintenance intervals, and adjust status without altering
  measurements.
- A Known Issue leaves the primary action queue, accumulates matching episodes,
  and returns through a scheduled review summary or a material change. It is
  promoted immediately only when severity, scope, cadence, signature, or
  downstream impact materially changes. Dismissal can apply to one occurrence
  or the same structured owner/signature; neither uses exact decimal equality.
- Determine equivalence from typed condition/direction/co-occurrence features,
  ownership, duration/recurrence shape, impact types, severity bands, and
  baseline-aware statistical materiality. Raw decimal values remain evidence,
  not identity keys.
- Extend Recommendations with Active, Known Issues, and History views. Finding
  detail owns episodes, pattern/cadence, impacts, hypotheses, notes, audit, and
  diagnostic actions. Existing Performance charts remain the evidence owner;
  finding deep links select the entity/time range and render structured episode
  bands, condition markers, targets, and baselines rather than screenshots.
- Treat severity, finding confidence, and cause-hypothesis confidence as
  separate values. Cross-domain relationships distinguish co-occurrence,
  precedence, correlation, suspected contribution, and operator-confirmed
  cause; correlation is never stated as proof.
- Evaluate active episodes every five minutes, patterns hourly, and deeper
  calendar/baseline relationships daily. Exclude insufficient coverage from
  denominators and report observable windows explicitly.
- Preserve intentional maintenance and deployment periods as audited chart
  evidence while excluding them from baseline learning. The deploy helper owns
  an explicit start/end maintenance handshake; unexplained service restarts are
  not excluded automatically.
- Implement RF first end to end. The operator accepted that vertical slice and
  the shared Recommendations contracts as the Package 9 closure scope; extending
  temporal detectors to other domains is future product work.

RF implementation delivered 2026-07-17:

- Deterministic 28-day RF episode and recurrence analysis now groups decimal
  variations by typed symptom signature and recognizes regular recurrence even
  without consecutive days or a fixed schedule.
- One parent RF finding per configured site consolidates decode loss, control
  instability, general RF degradation, and capture degradation patterns. Typed
  episodes remain distinct below the parent instead of flooding the action queue.
- Active, Known Issues, and History views expose operator-owned workflow state,
  derived activity, append-only notes, cause hypotheses, and a complete audit.
- Direct links select the owning RF Performance page and render stored episode
  intervals as chart overlays. Existing RF charts and the 40 msg/s reference remain.
- The direct-deploy helper records explicit start/end maintenance intervals;
  these remain audited evidence and are excluded from pattern baselines.
- RPI live verification showed two parent RF findings for the two configured
  sites, no legacy RF cards in Active, and 64 visible episode bands for the
  selected ETV finding. Trunk Recorder remained PID 1595 with its July 11 start.

- [x] Detect sustained outages, recurrence, schedule association, trends,
  spikes, drops, and correlated typed symptoms for the RF-first vertical.
- [x] Use a hybrid contract: retain community targets such as 40 TR messages/s
  while learning a rig-local baseline that never hides the stronger target.
- [x] Surface concise findings in Recommendations and detailed evidence on the
  owning Metrics page; preserve reviewed/resolved finding history for 90 days.
- [x] Begin provisional recurrence findings after three comparable days and
  mature local baselines over approximately 28 days.
- [x] Test, deploy, verify, and obtain operator acceptance.

### 10. Recovery Workflows

Status: complete, deployed, nondestructively RPI-verified, and operator-accepted

Accepted design:

- There are exactly two recovery-related artifact types: a same-system Backup
  and a secret-free Support Package. A backup may be downloaded for safekeeping,
  but cross-system cloning or migration is unsupported. Support packages are
  shareable diagnostics and are never restorable.
- New backups are encrypted with an operator passphrase that PizzaWave never
  persists. Require at least 12 characters and exact confirmation. Keep the key
  only in process memory; browser disconnect or operator absence does not stop
  the job. If PizzaWave restarts, delete the incomplete archive and require a
  new job rather than persisting key material.
- Every backup includes configuration, credentials, the authentication token,
  database, app data, Trunk Recorder configuration/talkgroups, and Qdrant when
  enabled. The only selectable data scope is recorded audio: none, 24 hours,
  7 days, 30 days, 60 days, or all. Restore applies exactly the scope recorded
  at creation; it does not offer a second component mixer.
- Existing plaintext ZIP backups remain fully supported without warnings,
  conversion requirements, or download restrictions.
- Reset with backup requires the passphrase before work starts and cannot begin
  destructive stages until the encrypted backup is complete and verified.
  Reset without backup remains possible only through stronger confirmation and
  an explicit audit record.
- Restore upload, decryption, manifest review, and verification are non-
  disruptive. Large uploads are resumable in verified chunks, survive browser
  reloads/disconnects, and expire after 24 hours when incomplete. Destructive
  apply requires a second confirmation listing every service that will stop or
  restart, including Trunk Recorder.
- Before apply, create and verify a backup of current state using the restore
  passphrase. Automatically roll back failures before the restart boundary.
  Post-restart failures do not enter a restart loop; they expose a guided
  rollback using the pre-restore backup.
- Restore outcome is Completed, Completed with warnings, or Failed based on
  manifest restoration, SQLite integrity, configuration load, PizzaWave health,
  and included dependent-service checks. The result and stage log survive
  database replacement and remain visible until acknowledged.
- Data Only reset pauses PizzaWave ingest only during destructive work and does
  not restart Trunk Recorder. Site and Full reset stop capture and leave Trunk
  Recorder stopped until Setup is explicitly applied. Every transition is
  recorded by the reset job.
- Support Packages default to 24 hours of logs, redacted PizzaWave/TR config,
  service/resource state, jobs/operator actions, diagnostic metrics, recent
  errors, and build information. They always exclude the database, Qdrant,
  secrets, tokens, and credentials. Audio/transcript content requires a bounded
  explicit opt-in and privacy warning. A manifest reports scope, sizes,
  redactions, missing evidence, and privacy inclusions; unresolved secret-scan
  results prevent readiness.
- Support Packages retain locally for seven days by default, with configurable
  retention or cleanup disabled. Backups are never automatically deleted.
- Only one backup, restore, reset, or support-package job owns recovery resources
  at a time. Backup/support work is subordinate to live capture and safely
  cancellable before finalization; restore/reset has exclusive ownership before
  mutation and reports any blocker truthfully.
- Backup creation never pauses ingest or Trunk Recorder. Use online snapshots,
  immutable copies, and change detection; fail rather than publish a backup whose
  internal consistency cannot be proven.
- Live acceptance is non-destructive on both RPI and OT. Verify create/download,
  unlock, inventory, redaction, cleanup, resumable upload, decryption, validation,
  and staging. Prove restore apply/reset in automated tests and an isolated
  temporary installation unless the operator separately authorizes a live drill.

- [x] Run backup, support-package, restore-apply, and reset work through
  auditable background jobs that do not depend on the browser remaining open.
- [x] Implement encrypted same-system backups, verified resumable restore
  staging, online Qdrant snapshots, pre-restore safety backups, durable recovery
  results, secret-gated support packages, and explicit private-evidence scope.
- [x] Improve archive scope, verification, manifest, and completion presentation.
- [x] Deploy to RPI and complete nondestructive live verification: encrypted
  no-audio backup create/download, all 108 resumable upload chunks, unlock,
  627-entry manifest validation, stage cancellation, default support-package
  redaction/manifest review, artifact cleanup, health checks, and unchanged
  Trunk Recorder identity. Restore apply and reset remained isolated-test-only.
- [x] Obtain operator acceptance.

### 11. Cleanup

Status: implemented, deployed, and live-verified; operator acceptance pending

- [x] Split the Dashboard map into an owned, lazy-loaded feature boundary.
- [x] Confirm RSW is already absent; remove the standalone TR-editor routes and
  unreachable UI plus unused RF UI and other proven-unreachable top-level code.
- [x] Replace the hand-built map interaction engine with React Leaflet.
- [x] Update operator and architecture documentation.
- [x] Run the production frontend build, all 516 tests, RPI health checks, and
  live Dashboard map/browser verification.
- [ ] Obtain operator acceptance.

## Decision Log

- 2026-07-13: Package 6 profile Save and Activate are separate operator
  actions. Saving an edited profile must not silently make it active.
- 2026-07-13: Alert talkgroup scope requires system plus talkgroup identity.
  Numeric-only and unscoped legacy rules are unsupported; do not preserve an
  ambiguous any-system interpretation.
- 2026-07-13: Historical/offline call import is not a current first-class
  feature. The Avalonia local/SFTP archive workflow was removed, and the
  remaining database/pipeline fields are insufficient for operator use. Any
  restoration requires a separate bounded package and explicit downstream and
  resource policy.
- 2026-07-13: Add Offline And Archive Calls as Package 7 immediately after
  Profiles And Alerts. Shift System Information Quality, Recovery Workflows,
  and Cleanup to Packages 8, 9, and 10. Historical processing must remain
  subordinate to live monitoring and cannot emit live notifications.
- 2026-07-13: Leave Package 5 implemented and deployed but pending final
  operator acceptance. Do not run its bounded RF or call-transcription proof
  while a separate Codex session is observing Trunk Recorder decode rates over
  long windows. Advance to Package 6 without disturbing Trunk Recorder.
- 2026-07-13: Package 4's completed workspace/navigation scope does not cover a
  full content-quality audit of System. Add a dedicated package to review every
  System page for operational value, actionability, conciseness, consistency,
  and evidence quality.
- 2026-07-09: Use vertical milestones with deploy-and-inspect boundaries rather
  than a single large rewrite.
- 2026-07-09: Setup is the sole owner of desired monitoring configuration.
- 2026-07-09: Major product and data-model decisions require operator review in
  chat; routine implementation decisions do not.
- 2026-07-09: Operator accepted Package 1 after deployed verification.
- 2026-07-09: RR talkgroups import automatically once per selected RR system.
  Later updates require the operator to use that system's refresh control;
  refresh preserves operator policy and manual fields.
- 2026-07-09: Operator accepted Package 2 after the pagination stabilization
  and deployment-efficiency follow-up.
- 2026-07-09: Package 3 refresh failures keep last-good data visible, identify
  when it was last updated, and expose page-local retry rather than replacing
  useful content with a loading screen.
- 2026-07-09: Browser-to-PizzaWave transport is not a persistent operator
  status. Reconnection is silent; affected pages own request failures; the
  technical REST/SSE badge is removed; and a single exception-only delayed
  update pill appears after 90 seconds without a successful refresh.
- 2026-07-09: Trunk Recorder monitoring remains persistently visible and uses
  distinct active, warming, stale, faulted, intentionally stopped, and
  Setup-paused states with monitoring freshness kept separate from page fetch
  failures.
- 2026-07-09: Search is owned by Dashboard and call-category pages. Dashboard
  filtering is local, category requests wait briefly for typing to stop, and
  search does not reload unrelated status or pages.
- 2026-07-09: Automatic events refresh only affected shared data and relevant
  visible-page data. A genuine event-stream reconnection performs one
  contextual catch-up refresh; initial connection does not duplicate the
  initial page load.
- 2026-07-09: Temporary refresh failures retry indefinitely while their page
  remains relevant, backing off from approximately one second to a one-minute
  ceiling and resetting immediately after success. Definite authentication,
  permission, invalid-request, and missing-endpoint errors require operator
  action instead of automatic retry.
- 2026-07-09: Package 3A operator feedback moves category search into the
  existing title/sort header, removes the aggregate count cluster, and moves
  weak-call and talkgroup-selection controls into a compact More menu.
  Selection-only actions appear in a temporary contextual strip.
- 2026-07-09: Category talkgroup titles use the RadioReference description as
  the friendly name and preserve RadioReference's structured jurisdiction
  heading during import. Technical system, talkgroup ID, and alpha-tag identity
  remain visible as subdued secondary metadata; PizzaWave does not infer
  jurisdiction from the receiver site or alpha-tag abbreviation.
- 2026-07-09: Operator accepted and closed milestone 3A, including its compact
  category-header and structured talkgroup-naming follow-up.
- 2026-07-09: Settings forms load the active server configuration once when the
  page opens and then become an operator-owned draft. Events, timers, and
  reconnection do not refresh form fields. Initial load failure renders an
  unavailable state rather than plausible defaults; reloading server values is
  an explicit action that cannot silently discard unsaved edits.
- 2026-07-09: Read-only specialized panels load and retry only while visible,
  retain last-good data, and own their error, last-updated time, and Retry
  action. System Refresh updates the shared System summary and the visible
  specialized panel; hidden panels perform no refresh work.
- 2026-07-09: Settings supporting inventories load only with their visible tab,
  retry temporary initial failures, and never change editable selections or
  profile drafts. Refresh controls remain consolidated: one Settings reload,
  one System refresh, and panel-local Retry only while a load is failed.
- 2026-07-09: Operator accepted milestone 3B and closed Package 3 after live
  inspection of the consolidated Settings and System refresh behavior.
- 2026-07-09: Package 4 System navigation uses noun-based operator areas:
  Health, Processing, Data, Receiver, and Performance. System remembers the
  last area and page; direct status links still open their exact destination.
- 2026-07-09: Each visible System page owns its load, retry, failure, and
  Refresh work. Refresh updates only the visible page; persistent monitoring
  continues on its independent shared schedule.
- 2026-07-09: Jobs is status and history. The server declares operations each
  job supports in its current state; the UI does not infer controls from job
  status. Workflow-specific cancellation remains in the owning workspace, and
  completed-job cleanup remains the Data maintenance action rather than a
  per-row Delete control.
- 2026-07-09: Operator accepted Package 4 after live inspection of the
  task-based System workspace.
- 2026-07-10: Package 5 RF Validation uses the accepted Preparation, Spectrum
  Inspection, Control-Channel Proof, Source Coverage, Call and Transcription
  Proof, and Verdict stages while retaining existing controls within those
  stages. Operator-facing tuning terms distinguish saved frequency correction,
  measured signal offset, and trial correction change; PPM is derived only.
- 2026-07-10: Package 5 source planning is a server-owned, versioned projection
  in RF Validation's Source Coverage stage. The server owns recommendations,
  alternatives, assignments, tuning windows, coverage, evidence, assumptions,
  and warnings; the browser supplies operator intent and constraints. Apply &
  Resume is limited to final comparison, discard, and guarded application.
- 2026-07-10: Package 5 RF paths model one upstream antenna signal with ordered
  shared components and splitter/multicoupler branches ending at stable,
  detected SDR hardware identities or explicit unused outputs. Missing devices
  are not silently rebound. Multiple antennas, combiners, switches, and branch
  rejoining are deferred.
- 2026-07-10: Package 5 RF runs create grouped append-only Setup activity and
  first-class evidence metadata linked to immutable artifact files. Reports and
  waterfall images are server-owned. Compact evidence and required audio are
  retained by default; large raw IQ capture retention requires an explicit
  operator choice with storage guidance.
- 2026-07-10: Package 5 adds a cross-session Experiments & Evidence history,
  defaulted to the current site, for named short experiments. Operators can
  search, filter, compare, annotate, repeat, and copy successful settings into
  pending Setup changes; immutable history never applies directly to live
  monitoring.
- 2026-07-10: Package 5 Preparation performs one read-only required-software
  check per applied Setup revision on first open and offers Recheck after a
  failed result. Successful Apply & Resume changes invalidate the result; SDR
  access and configuration changes remain separate explicit actions.
- 2026-07-10: Package 5 location context uses imported RadioReference talkgroup
  jurisdiction first and selected-site geography as fallback. Manual geography
  is an explicit correction for missing or inaccurate source data, rather than
  an independent active-area list or an RF-coverage model.
- 2026-07-10: Package 5 replaces Setup's administrative summary cards with
  operator guidance for current site/hardware scope, validation progress and
  next blocker, and apply plus monitoring state. Last setup change moves to the
  Activity Log.
- 2026-07-10: Package 5 Control-Channel Proof leads with one recommended run,
  progress, and measured results. Sample rate, gain, correction changes, and
  candidate limits remain available as advanced settings rather than competing
  with the primary task.
- 2026-07-10: Package 5 RF Validation subpages share a concise goal, status and
  next-action, primary-task, evidence, and collapsed-details hierarchy. The
  stage status block becomes a compact stepper and healthy or advanced detail
  no longer competes with the operator's next task.

## Deployment Log

- `d8e7ec9`: starting baseline; asynchronous auditable backup jobs.
- `9c48f30`: Package 1 deployed; versioned Setup mutations, per-site RR
  identity, Setup-owned RF handoff, and explicit live baselining for previously
  unspecified source gain.
- `99f4c96`: Package 2 deployed; server-owned RR import provenance, explicit
  per-system refresh, server-paged catalog reads, and scoped Setup policy
  mutations.
- `28a6daa`: Package 2 pagination stabilization deployed; Talkgroups retains
  its last good page and retries a transient fetch, while rolling status
  summaries use stale-while-refresh caching instead of holding concurrent UI
  connections open. The deployment script now fails on native build, archive,
  upload, or remote-deploy errors instead of reusing stale artifacts.
- `2c56be5`: Package 3A deployed; Dashboard, call-category pages, Setup, and
  System retain last-good data, expose page-local freshness/error/retry state,
  use indefinite bounded backoff for temporary failures, and no longer mix
  browser transport with Trunk Recorder monitoring. Search is page-owned and
  automatic event refreshes are scoped to affected views.
- `fd16f1b`: Package 3A category-header follow-up deployed; category search is
  inline with the title and sort controls, secondary controls live in a More
  menu, selection mode has a contextual action strip, and catalog imports
  preserve RadioReference jurisdiction for friendly talkgroup names.
- `4263348`: Package 3B deployed; Settings uses one protected server-hydration
  boundary, supporting inventories load only with their visible tab, and System
  read-only panels retain last-good data behind one consolidated Refresh action.
- `54c8fe3`: Package 4 deployed; System uses task-based noun navigation,
  remembers the last area/page, loads visible pages independently, refreshes
  only the visible page, and renders only server-declared job operations.
- `6b18b1a`: Package 5 milestone 5A deployed; RF Validation uses six explicit
  proof stages, preserves the existing diagnostic controls, and distinguishes
  saved frequency correction, measured signal offset, and trial correction
  change throughout the operator workflow.
- `b38269e`: Package 5 RF Validation clarity follow-up deployed; Preparation
  performs one software-only check per applied configuration, stage guidance is
  concise and action-led, and advanced Spectrum and Control proof settings
  remain available behind collapsed detail.

## Verification Log

- 2026-07-09: Architecture review completed against deployed baseline. No code
  changes were made during the review.
- 2026-07-09: Milestone 1A passed the existing backend test suite and production
  frontend build. Setup mutations are serialized in the UI, version-checked by
  the server, and limited to the fields named by each operation.
- 2026-07-09: Milestone 1B passed the existing backend test suite and production
  frontend build. Systems & Sites now retains independent RR catalogs and
  stores RR identity on each selected site instead of a global Setup SID.
- 2026-07-09: Milestones 1C and 1D passed the existing backend test suite and
  production frontend build. Waterfall selections and accepted RF calibration
  now mutate versioned Setup state, create pending/audit records, and no longer
  write the standalone TR editor draft.
- 2026-07-09: Package 1 deployed to the RPI and passed live API/UI smoke checks.
  Systems & Sites loaded the independent MSWIN RR 4879 and Entergy RR 8202
  catalogs, retained the correct selected sites, and required no admin token
  for the read-only catalog load.
- 2026-07-09: Operator accepted Package 1. Package 2 discovery confirmed that
  RR import completion is browser-session state and Setup catalog edits still
  replace the full catalog document from the client.
- 2026-07-09: Milestone 2A passed the existing backend test suite and production
  frontend build. RR import provenance and refresh are now server-owned and
  auditable; the browser no longer sends parsed RR catalog rows or decides
  whether an import has already occurred.
- 2026-07-09: Milestones 2B and 2C passed the existing backend test suite and
  production frontend build. Setup catalog reads are server-paged and filtered;
  category and TR policy mutations are scoped, serialized, audited, and only
  TR-affecting changes create pending Apply & Resume work. Obsolete preview,
  save, and full-catalog replacement endpoints were removed. The last applied
  enabled-talkgroup set is retained so Discard can restore pending TR policy
  changes without undoing immediate category edits.
- 2026-07-09: Package 2 deployed to the RPI. The live legacy catalog completed
  its one-time provenance import with 2,529 MSWIN RR 4879 rows and 298 Entergy
  RR 8202 rows, adding no duplicates and preserving 15 existing TR exclusions
  and operator category assignments. Re-entering Talkgroups did not repeat the
  import; both per-system refresh controls became available after the paged
  catalog loaded, and the provenance migration created no pending Setup change.
- 2026-07-09: Package 2 pagination follow-up passed the production frontend
  build, production backend build, existing backend suite, and live RPI checks.
  Next, Last, and First completed repeatedly without `Failed to fetch`; the
  catalog endpoint remained fast, and cached status-summary requests dropped
  from multi-second waits to millisecond responses after warm-up.
- 2026-07-09: The development deploy helper was converted to hash-based
  automatic selection with fail-closed native commands and per-stage timing.
  Live RPI verification measured 3.1 seconds for a no-change health check, 6.7
  seconds for a cached web-only install without restarting `pizzad`, 62.9
  seconds for a cached full backend reinstall, and 74.4 seconds for a forced
  clean ARM64 backend build and reinstall, compared with the prior 202-261
  second full path. Checked-in web source provenance and incremental TypeScript
  metadata allow fresh worktrees to reuse verified generated assets.
- 2026-07-09: Operator accepted Package 2. Server-owned RR import provenance,
  paged/scoped catalog operations, Setup activity and apply-state integration,
  preserved operator policy, and stable pagination are the accepted baseline
  for subsequent packages.
- 2026-07-09: Package 3A passed the production frontend build. The existing
  backend suite is presently blocked before execution by five unchanged tests
  that still call the Package 2-removed `TalkgroupCatalogService.SaveAsync`;
  the automatic deployment's production backend build completed with zero
  warnings and zero errors.
- 2026-07-09: Package 3A deployed with the automatic helper. After two normal
  startup connection retries, `/api/v1/health` reported healthy on the live
  RPI with active Trunk Recorder data; Trunk Recorder was not restarted as a
  separate verification action.
- 2026-07-09: Live browser verification confirmed an active monitoring pill
  with a fresh checked time, no persistent PizzaWave connection or REST/SSE
  badge, page-local update times and Refresh actions on Dashboard, Setup, and
  System, immediate local Dashboard filtering, delayed Police server search,
  retained Police results while refreshing, and no browser console errors.
- 2026-07-09: The category-header follow-up passed all 393 backend tests and the
  production frontend build. It deployed through the automatic helper, and
  `/api/v1/health` returned healthy without a separate Trunk Recorder restart.
- 2026-07-09: Live Police-page verification confirmed the compact one-line
  header, More menu, contextual talkgroup-selection strip, description-first
  friendly names, active monitoring, and no browser console errors. The live
  catalog was intentionally not refreshed; jurisdiction prefixes will populate
  on the next explicit RadioReference refresh, while the structured importer is
  covered by parser and site-to-catalog mapping tests.
- 2026-07-09: Operator accepted the deployed 3A milestone and authorized its
  closure. Package 3 remains in progress at the 3B boundary.
- 2026-07-09: Package 3B preflight passed all 394 backend tests and the
  production frontend build. Settings form hydration, supporting inventories,
  and visible System panels now use the accepted consolidated refresh
  boundaries; deployment and live verification remain.
- 2026-07-09: Package 3B deployed through the automatic helper's web-only path
  in 3.2 seconds without restarting `pizzad` or Trunk Recorder. The live health
  endpoint reported healthy monitoring and a clear processing queue.
- 2026-07-09: Live browser verification confirmed one Settings reload control,
  no generic Settings Refresh controls, dirty-draft reload protection, no
  Profiles reload control, and exactly one persistent System Refresh control on
  Services, Backup, TR config restore, Queue, and Metrics/Bandwidth. Monitoring
  remained active and no page or panel error notice appeared.
- 2026-07-09: Operator accepted 3B and authorized closure of Package 3. Package
  4 remains unstarted at its discovery boundary.
- 2026-07-09: Package 4 passed the backend suite including eight focused job
  operation-policy cases and passed the production frontend build. The checked-
  in web assets were rebuilt from the current source.
- 2026-07-09: Package 4 deployed through the automatic helper's full backend
  path in 138.2 seconds. The ARM64 build completed with zero warnings and zero
  errors; `/api/v1/health` returned healthy with active Trunk Recorder data and
  a clear processing queue. Trunk Recorder was not restarted separately.
- 2026-07-09: Live browser verification confirmed Health, Processing, Data,
  Receiver, and Performance navigation; remembered area and page state; exact
  Queue and Processor status-link destinations; contextual Refresh labels for
  each inspected page; independently rendered Storage, Backup and Restore,
  Configuration Restore, Bandwidth, Jobs, Health, Processor, and Queue pages;
  no inferred or per-row deletion controls on terminal jobs; and no browser
  console errors. The live jobs API returned the supported-operations contract
  on all 23 rows and no operations on the 23 terminal jobs.
- 2026-07-09: Operator accepted Package 4 and authorized its closure. Package 5
  remains unstarted at its discovery and operator-interview boundary.
- 2026-07-10: Package 5 milestone 5A passed the 402-case backend suite and the
  production frontend build. The checked-in web assets were rebuilt from the
  current source.
- 2026-07-10: Milestone 5A deployed through the automatic helper's full backend
  path in 78.5 seconds. The ARM64 build completed with zero warnings and zero
  errors. `/api/v1/health` returned `ok` with clear processing queues; live
  Trunk Recorder activity became active after deployment and later reported
  stale after no new callstream or health data. Trunk Recorder was not
  restarted separately.
- 2026-07-10: Live browser verification confirmed all six RF Validation stages,
  the existing Waterfall and RF Sweep controls in their reviewed stages,
  preparation/source/call/verdict summaries, standardized correction and
  signal-offset labels, Source Coverage routing into Apply & Resume, remembered
  Source Coverage selection on return, and no browser console errors.
- 2026-07-10: The RF Validation clarity follow-up passed the 404-case backend
  suite, including applied-revision software-check lifecycle and non-mutating
  check coverage, and passed the production frontend build. The implementation
  is committed as `b38269e` but was not deployed because preflight found live
  ingest still paused for a data-only reset, with no active job record and 159
  calls dropped during the pause. The three persisted pending Setup changes
  remained present; no RF experiment, apply, reset, or live mutation was run.
- 2026-07-10: After the operator confirmed the reset was complete, `b38269e`
  deployed through the automatic helper's full backend path in 70.9 seconds.
  The ARM64 build completed with zero warnings and zero errors. Restart cleared
  the stale ingest pause; the processing queue remained empty. The three
  pending Setup changes, desired version, and applied configuration hash were
  preserved exactly. Trunk Recorder was not restarted separately.
- 2026-07-10: Live browser verification confirmed the compact stage stepper;
  concise Preparation, Source Coverage, Call and Transcription Proof, and
  Verdict guidance; primary Spectrum source/frequency/Start controls with
  advanced capture settings collapsed; one recommended Control proof run with
  advanced settings and permutation detail collapsed; and no browser console
  errors. The first-open software check recorded eight available tools with no
  warnings against the unchanged applied configuration hash. The experiment
  list remained only the prior passed SDR Inventory; no RF capture, sweep,
  Apply, or desired-Setup mutation ran. Overall health remained degraded by the
  post-reset embedding state and live TR activity was stale.
- 2026-07-10: Package 5 completion candidate replaces the legacy Location
  authority with RadioReference talkgroup jurisdiction and selected-site
  fallback, retains old area records only as inactive compatibility data, and
  exposes explicit boundary overrides. Setup's persistent guidance now covers
  current scope, validation next task, and apply plus monitoring state.
- 2026-07-10: Source Coverage is now a versioned server projection with one
  recommendation, alternatives, exact tuning windows, assignments,
  assumptions, and stale-review rejection. Apply & Resume is limited to final
  configuration review and guarded apply. RF paths now support one shared
  upstream signal and multiple ordered branches linked to detected SDR serials;
  duplicate hardware endpoints are rejected while incomplete drafts remain
  valid.
- 2026-07-10: Named RF experiments now record optional hypotheses and physical
  changes, append started and terminal Setup activity, and hash-index retained
  artifacts as first-class evidence. Experiments & Evidence provides
  cross-session site filtering, search, two-run comparison, and separate
  annotations. The completion candidate passes all 407 backend tests and the
  production frontend build; deployment and live end-to-end verification
  remain.
- 2026-07-10: Package 5 completion candidate `da62b8d` deployed through the
  automatic helper's full backend path in 61.2 seconds with zero build warnings
  or errors. The stable-SDR-label follow-up `7e6f9da` then deployed through the
  automatic web-only path in 1.8 seconds without restarting `pizzad` or Trunk
  Recorder.
- 2026-07-10: Live APIs and browser verification confirmed the three guidance
  cards; derived Location context with all three legacy area records inactive;
  no Monitored Area, sync, or old save wording; one migrated RF branch with two
  serial-labeled Airspy endpoint choices; the server-recommended two-window,
  three-site Source Coverage projection; cross-session Experiments & Evidence;
  and final-only Apply & Resume review with no Sources subpage. No browser
  console errors appeared. No plan selection, RF run, path edit, discard, or
  apply occurred. Desired version `1783684339423`, applied hash `e95b8867...`,
  and the three existing pending categories remained unchanged. Operator
  end-to-end acceptance remains.
- 2026-07-10: Operator rejected the RF branch/tree editor as confusing and
  superseded the earlier topology decision. Hardware & RF Path returns to one
  ordered documented hardware list, and SDR Inventory moves to a prominent
  top-of-page action. The branch DTO, validation, frontend types, controls, and
  styling were removed rather than hidden.
- 2026-07-10: Simplified Hardware & RF Path commit `7014194` deployed through
  the automatic helper's full backend path in 76.0 seconds with zero build
  warnings or errors. Live browser verification confirmed SDR Inventory at the
  top, the restored single ordered hardware list, no branch/tree controls, and
  no browser console errors. The operator's active desired version
  `1783690411070`, two current systems, two SDR sources, applied hash
  `e95b8867...`, and pending categories Systems/Sites, Control Channels, and RF
  Path remained unchanged. `/api/v1/health` was degraded only because live
  Trunk Recorder activity was stale shortly after the pizzad restart.
- 2026-07-10: Spectrum/waterfall alignment fix `c5dc837` gives the waterfall
  the spectrum plot's same normalized left label margin and right inset instead
  of stretching frequency bins across the full canvas. The production web build
  passed and the automatic helper deployed it web-only in 5.9 seconds without
  restarting PizzaWave or Trunk Recorder. Retained live evidence showed the
  774.785 MHz spectrum spike aligned with its waterfall column, with no browser
  console errors. Health was `ok`; desired version `1783690411070`, applied
  hash `e95b8867...`, systems, SDR sources, and pending categories were unchanged.
- 2026-07-10: RF candidate review commits `f2f3a7e` and `3a42513` retain red
  selected-site control-channel bars and add full yellow bars for every current
  suspected carrier. One shared signal-to-noise-ratio plus persistence rating
  drives Strong, Steady, and Weak hover text and table colors. The candidates
  table now sorts stably by site name and frequency instead of re-ranking on
  every signal update. Production builds passed; automatic web-only deployments
  completed in 7.4 and 3.9 seconds without restarting PizzaWave or Trunk
  Recorder. Live hover checks covered a weak 771.984 MHz suspect and the strong
  selected 773.781 MHz control channel; table order remained alphabetical and
  selectable, with no browser console errors. Health and active Setup state were
  unchanged at desired version `1783690411070` and applied hash `e95b8867...`.
- 2026-07-10: Follow-up commits `687951d`, `b8cdab2`, `80dcebc`, and `4de9599`
  correct over-eager control-channel inference. Spectrum suspects now need five
  seconds of persistence for a yellow bar and eight seconds for table promotion;
  ratings use a 12-second window and decay slowly. Selected-site matching is
  limited to an actual +/-8 kHz carrier distance, including per-FFT-bin checks,
  so adjacent traffic no longer supplies stale control-channel metrics.
- 2026-07-10: Frequency-only matching against all 175 cached MSWIN sites was
  removed. An unselected carrier remains `Unidentified carrier` until P25
  evidence proves identity; it can no longer inherit labels such as Poplarville
  South Pearl River or Red Water Leake, nor can selecting it silently add that
  guessed site to Setup. Failed P25 probes no longer remain as candidates.
- 2026-07-10: Spectrum popups now use a fitted, wrapping 460 px layout. Retained
  history forces its latest frame into both spectrum and table so their ratings
  agree. Production builds passed and automatic web-only follow-ups completed
  without restarting PizzaWave or Trunk Recorder. Live retained verification
  showed ETV 773.781 MHz Strong at +4.395 kHz in both views, ETV 773.281 MHz
  Steady at +6.348 kHz, no false statewide site names, no console errors, health
  `ok`, and unchanged active Setup version `1783690411070`/hash `e95b8867...`.
- 2026-07-10: Active-span containment commit `f01ab68` limits suspected control
  channels and retained candidates to the current SDR center and sample-rate
  window. A 6 MHz capture centered near 773 MHz can no longer display cached or
  inferred 850 MHz candidates.
- 2026-07-10: Source-bound handoff commit `232eba7` removes the source-switch
  rewrite that previously reassigned all Waterfall selections to the newest
  source. Setup now preserves the same frequency independently for multiple
  SDRs, records the hardware serial, and sends exact source/channel/gain/sample-
  rate/correction measurements to RF Sweep instead of scanning a source-channel
  cross product. The server validates serial ownership before hardware access
  and carries each measurement through candidate follow-up. All 410 backend
  tests and the production frontend build passed. The automatic helper completed
  a full deployment in 96.9 seconds with zero build warnings or errors. Live
  health was `ok`; the new web asset loaded with no browser warnings or errors.
  No RF run or Setup mutation occurred. Desired version `1783690411070`, applied
  hash `e95b8867...`, two systems, two SDR sources, zero RF selections, and the
  pending Systems/Sites, Control Channels, and RF Path categories were unchanged.
- 2026-07-10: Operator RF Sweep `rfx-20260710152147-6cabfc4757c` exposed three
  reliability defects. ETV Raymond passed RF/P25 but was falsely failed because
  Airspy/TR initialization consumed its 45-second proof window. Tylertown used
  weak, mutually inconsistent measured offsets (+91 and -3344 Hz) as separate
  crystal corrections instead of retaining source 1's saved +4155 Hz baseline.
  The two-second RF screen analyzed only its first 1024 samples and selected the
  same +23.4375 kHz edge bin for both Tylertown channels while their strongest
  signals were about 1.28 MHz away. The source pairing itself was correct.
- 2026-07-10: Reliability remediation `5b37ac7` separates measured signal
  offsets from configuration, reconciles one correction per SDR in PPM, derives
  persisted serials from configured hardware identity, replaces the single IQ
  snapshot with nine median 4096-sample FFT windows and an +/-8 kHz carrier
  window, waits up to 90 seconds for scoped TR decode readiness before timing a
  proof, and retains the exact P25 pass marker in evidence. All 417 backend
  tests and the production frontend build passed. The automatic helper completed
  a full deployment in 125.1 seconds with zero warnings or errors. Live health
  was `ok`; both Airspy serials and all three migrated measured-offset fields
  were present, the new web asset loaded, and no browser warnings/errors appeared.
  No RF run or Setup mutation occurred during verification. Desired version
  `1783696822034`, applied hash `e95b8867...`, retained selections, and pending
  Systems/Sites, Control Channels, and RF Path categories were unchanged.
- 2026-07-10: Operator rerun `rfx-20260710155932-990ee246d6e` confirmed the
  source-bound correction repair executed, but exposed a validation ownership
  defect. The standalone OP25 probe opened source 0, detected repeated P25
  synchronization patterns on ETV 773.781250 MHz, and discarded its frames
  during error checking. RF Sweep reported the inaccurate generic "no P25
  evidence" result and hard-gated the native Trunk Recorder proof. After the
  sweep restored the unchanged live configuration, Trunk Recorder identified
  ETV RFSS 002/site 008 and system 2AD and decoded 5 messages/second within six
  seconds, proving the RF path was not the cause of that failure.
- 2026-07-10: Native-proof remediation `07c3360` keeps standalone OP25 results
  reviewable but lets every non-tool-failed candidate continue to the bounded,
  source-scoped Trunk Recorder proof. Passing native decode metrics now owns the
  monitorable P25 decision; a standalone tool failure still blocks safely.
  Synchronization followed by discarded frames is reported explicitly instead
  of as absent P25 evidence. The focused 43-test RF suite and all 419 backend
  tests passed. The automatic backend deployment completed in 78.6 seconds with
  zero build warnings or errors. Live PizzaWave and Trunk Recorder services and
  health were `active`/`ok`. No RF experiment or Setup mutation was performed
  during verification: the latest experiment remains the operator's failed
  rerun, desired version `1783696822034`, applied hash `e95b8867...`, the two
  source serials/corrections, selected systems, and pending changes are
  unchanged.
- 2026-07-10: Operator rerun `rfx-20260710161343-608837722ea` reached native
  proof and exposed a single-channel isolation defect. ETV passed standalone
  P25, averaged 22 messages/second, and captured one real audio call, but the
  temporary candidate config retained ETV's alternate control channels and
  introduced retunes that produced 20% zero-decode samples. The 855.587500 MHz
  Tylertown candidate was blocked because that same config construction also
  retained 857.837500 MHz outside the temporary source window.
- 2026-07-10: Candidate-isolation fix `f728256` makes both the proof profile and
  generated temporary Trunk Recorder system contain exactly the candidate
  control channel. Alternate channels can no longer create proof-window
  retunes or source-coverage blockers. The focused 22-test multi-site suite and
  all 419 backend tests passed. Automatic backend deployment completed in 88.2
  seconds with zero build warnings or errors. Live health was `ok`; PizzaWave
  and Trunk Recorder were active. No new scan or Setup mutation was performed:
  latest evidence remains the operator's rerun, desired version
  `1783696822034`, applied hash `e95b8867...`, and both source serials and
  corrections are unchanged.
- 2026-07-10: Operator rerun `rfx-20260710165804-4c5ac081514` confirmed
  single-channel isolation but exposed two remaining ownership defects. ETV
  captured six real audio calls, while the sweep had replaced source 0's saved
  +3600 Hz correction with one carrier's +4395 Hz signal offset; continuity
  fell to 10.5 messages/second with 53.3% zero-decode. Tylertown's isolated
  proof passed RF coverage validation, then Trunk Recorder exited because the
  generated system referenced the nonexistent
  `/etc/trunk-recorder/talkgroups.tylertown-ms-walthall-ms.csv`.
- 2026-07-10: Source-correction and proof-catalog fix `82da743` preserves a
  configured SDR's exact saved hardware correction for every candidate, keeps
  Waterfall carrier offsets as evidence, and only derives a correction from
  strong observations when no saved correction exists. Temporary proofs use
  the installed base talkgroup catalog and allow unknown calls because labels
  are not required for control-channel proof. The focused 44-test RF suite and
  all 420 backend tests passed. Automatic backend deployment completed in 96.6
  seconds with zero build warnings or errors. Live health was `ok`; PizzaWave
  and Trunk Recorder were active. No new scan or Setup mutation was performed;
  desired version `1783696822034`, applied hash `e95b8867...`, and exact source
  corrections +3600/+4155 Hz remain unchanged.
- 2026-07-10: Apply & Resume for the operator-selected ETV Raymond and Entergy
  Jackson plan faulted because Config Draft generated a nonexistent site-level
  `talkgroups.jackson-ms-hinds-ms.csv`. Catalog-ownership fix `60a566f` resolves
  a selected site's RadioReference SID through the imported talkgroup catalog
  owner (`4879` to `mswin`, `8202` to `entergy`) and refuses to install any
  future draft whose referenced talkgroup file is missing. All 421 backend
  tests passed and the automatic backend deployment completed in 103.6 seconds
  with zero warnings or errors. The same already-selected plan was regenerated
  and applied through the signed-in Setup workflow without changing systems,
  sources, centers, gains, or corrections. Live config now references
  `talkgroups.mswin.csv` and `talkgroups.entergy.csv`; Trunk Recorder is active
  and decoded ETV RFSS 002/site 008/system 2AD and Jackson RFSS 002/site
  080/system 64F immediately after restart.
- 2026-07-11: Package 5 acceptance review found four remaining Setup UX
  regressions. Removed-site RF sweep evidence was still projected into active
  Control-Channel Proof, Call and Transcription Proof had lost its run actions,
  Apply & Resume did not enforce those proof gates, and Activity Log remained a
  numbered Setup step. The continuation on `codex/package5-fixes` filters active
  readiness to currently selected sites while retaining historical evidence,
  restores bounded call-capture and transcription actions, enforces both proof
  gates in the UI and apply API, and moves Setup activity to System > Data >
  Audit History. Production frontend build and focused apply-readiness tests
  pass. Commit `212c0df` deployed through the automatic ARM64 backend path in
  185.9 seconds. The RPI serves `assets/index-B9znewgt.js`; health is `ok`,
  PizzaWave and Trunk Recorder are active, and monitoring remains active. The
  live desired version `1783704270742`, selected ETV Raymond/Jackson systems,
  and pending-apply state were read-only verified and were not changed.
  Operator acceptance remains pending.
- 2026-07-11: Live review found the restored Call and Transcription Proof
  controls exposed two implementation stages, displayed a disabled
  transcription button, described required proof as optional, and retained
  experiment-authoring distractions. The follow-up replaces them with one
  guided call-and-transcription action, automatically advances after call audio
  passes, reports a stopped/failed stage clearly, states the duration and
  monitoring impact, and hides experiment authoring during this required gate.
  The production frontend build and all 424 backend tests pass. Commit
  `74d0419` deployed through the automatic web-only path in 6.3 seconds without
  restarting PizzaWave or Trunk Recorder. Live review confirmed one enabled
  `Run call & transcription proof` action, explicit duration/monitoring text,
  two concise result states, and no experiment-authoring controls. Final
  operator acceptance remains.
- 2026-07-11: The operator's first guided Step 5 run was blocked because live
  Source 1 remained at the approved 855.287500 MHz center while Config Draft
  dynamically moved it to 856.300000 MHz after incorporating observed voice
  frequencies. The source's existing 6 MHz window already covered the complete
  plan, so the drift was unnecessary and made proof impossible before final
  Apply. Config Draft now preserves each configured center whenever that window
  covers every planned frequency and recenters only when coverage requires it.
  All 426 backend tests pass. Commit `a61db21` deployed through the automatic
  ARM64 backend path in 186.6 seconds. Read-only live comparison confirmed both
  Config Draft centers now match the approved live centers at 771.931250 and
  855.287500 MHz. Health is `ok`, monitoring is active, and PizzaWave and Trunk
  Recorder are active. The prior blocked run remains audit evidence; the
  operator may rerun Step 5.
- 2026-07-11: Live Dashboard investigation found 483 recent extracted location
  rows but zero mapped locations because all 610 geocode-cache entries were
  negative. The redesigned location resolver ignored the valid bounded Hinds
  County area unless it was marked as an explicit override, then queried
  addresses against the synthetic label `ETV Raymond Hinds` and worldwide
  bounds. The fix reuses a configured bounded area only when it matches the
  active system and shares a meaningful place token with the derived site or
  jurisdiction context, preserving the existing stale-area protection that
  rejects old Tennessee authority. Configured areas are also resolvable by ID
  during geocoding. Focused location tests and all 427 backend tests pass.
  Commit `3a7b864` deployed through the automatic ARM64 backend path in 227.3
  seconds with zero build warnings or errors. PizzaWave and Trunk Recorder are
  active and health is `ok`; Trunk Recorder's activation timestamp remained
  17:11:15 EDT, confirming the deploy did not interrupt the direct-path RF
  monitor. No historical location rows were backfilled. Live map population
  awaits the next newly transcribed call containing an extractable location.
- 2026-07-13: Package 6 implementation is complete locally without contacting
  the RPI or disturbing Trunk Recorder. Profiles now have independent Utilities
  policy, participate in Settings dirty/discard protection, and separate Save
  from Activate. Active-profile and profile-talkgroup mutations require write
  authorization. Alerts show their effective active-profile scope, use exact
  system-plus-TG catalog selections, reject numeric-only scope, and share one
  canonical keyword, police-code, or either-criterion contract between UI and
  server. The unused profile `allowedTalkgroups` authority was removed. All 437
  backend tests and the production frontend build pass. Deployment and live
  operator verification were initially deferred while another session observed
  Trunk Recorder decode rates over long windows.
- 2026-07-13: After the operator cleared the deployment boundary, Package 6
  commit `fa4e24f` deployed to RPI `sdr1861` through the automatic ARM64 full
  backend path in 164.3 seconds with zero build warnings or errors. Direct-LAN
  SSH was unavailable, so the verified Tailscale address for the same host was
  used. `pizzad.service` restarted and health returned `ok`; queue pressure was
  false, AI completions and embeddings were healthy, and live Trunk Recorder
  activity was current. `trunk-recorder.service` remained active with its
  original July 11 activation timestamp. The deployed page references web
  bundle `assets/index-C9RorDzI.js`. Operator workflow acceptance remains.
- 2026-07-13: Operator acceptance follow-up `b67465b` removes the three
  read-only/selection guidance strings and the editor-level Active/Activate
  button. The page-header profile dropdown remains the sole activation control;
  the Settings profile dropdown only selects an editing target. The production
  build passed and the automatic helper deployed the follow-up web-only in 5.2
  seconds without restarting PizzaWave or Trunk Recorder. Live health remained
  `ok`, live TR activity was current, and the deployed bundle
  `assets/index-ePRLZjSL.js` contains none of the removed strings or activation
  function. The operator approved Package 6.
- 2026-07-13: Package 7 discovery confirmed that current callstream `.bin`
  payloads preserve authoritative metadata and audio, imported rows already use
  a lower-priority queue, and durable import requires new persisted session/item
  ownership rather than restoring the Avalonia manager. The operator deferred
  Package 7 before source and downstream-policy decisions were made; no Package
  7 code or live state changed.
- 2026-07-13: Package 8 began with a source/API inventory and a read-only review
  of every deployed System page. The review found repeated Receiver/RF evidence,
  Setup-owned source planning in Receiver Summary, unloaded AI/Bandwidth reports
  presented as `Open` health states, receiver decode rate under Calls, excessive
  default transcription samples, primary-level implementation details, missing
  Audit/Logs context, and inconsistent status language. The durable System
  information contract now records all 19 pages, their operator decisions,
  evidence sources, and ownership boundaries. Implementation is in progress.
- 2026-07-13: Package 8 implementation `e2d69d3` adds a shared operator-question,
  impact, next-action, and evidence hierarchy to all System pages. It removes
  Setup-owned source planning from Receiver Summary; reduces RF Performance to
  four primary charts with one baseline statement; removes unloaded AI and
  bandwidth pseudo-statuses and receiver decode rate from unrelated summaries;
  bounds transcription evidence; standardizes service and recommendation state
  language; and demotes process, database, endpoint, ledger, raw config, and
  normal receiver details behind disclosures. Audit uses the shared refresh
  contract and TR config restores are paginated. The production build and all
  437 existing backend tests passed. The automatic helper deployed it web-only
  in 5.2 seconds without restarting PizzaWave or Trunk Recorder.
- 2026-07-13: Live browser review covered Health, Services, Receiver Summary,
  RF Performance, Transcription, Audit History, Performance Overview, and
  Bandwidth with no console warnings or errors. Follow-up `e52f35e` demotes
  dense per-system receiver diagnostic strings behind `View metrics` and
  deployed web-only in 4.6 seconds. The live bundle is
  `assets/index-i4jaZTxE.js`; service activation timestamps remained unchanged,
  health is `ok`, queue pressure is false, and live TR activity is current.
  Focused System API and UI information-contract tests raise the suite to 440
  passing tests. Operator acceptance remains.
- 2026-07-14: Recommendations follow-up `1016222` adds a red current-problem
  count to the left-navigation System item and replaces the misleading global
  control-channel decode-zero average with one finding per affected Trunk
  Recorder system. Live API and raw TR journal evidence confirmed Jackson at
  0.0 msg/s and 100% zero samples while ETV Raymond remained healthy at 40.0
  msg/s. The deployed card names Jackson and includes Raymond as the healthy
  peer context; the former global finding moved to Recently Resolved. All 445
  tests passed. Live browser verification confirmed the badge and corrected
  card. PizzaWave and Trunk Recorder remained active, and Trunk Recorder kept
  its `Sat 2026-07-11 17:11:15 EDT` activation timestamp.
- 2026-07-14: Recommendations follow-up `9f1aef5` reapplies severity-first
  ordering after finding lifecycle synchronization; the persistence query had
  been replacing the intended order with last-observed order. All 446 tests
  passed, and the deployed API returned Critical, High, Medium, then Low. A
  read-only evidence audit confirmed the Raymond retune card is accurate but
  newer than the operator's 9:21-10:21 report: that fixed hour contains 8 raw
  retunes, while stored 10:50-11:05 buckets contain 0, 8, and 4 retunes. Decode
  remained strong with no zero summaries in those three buckets, supporting
  medium rather than high severity. PizzaWave and Trunk Recorder remained
  active, and Trunk Recorder retained its July 11 activation timestamp.
- 2026-07-14: Recommendations follow-ups `060944f` and `95fbaf9` replace
  metric-level cards with condition-level findings. Each affected TR site now
  gets one RF-stability card combining decode rate, zero-decode, retunes,
  frequency targets, two-hour context, the 40 msg/s reference, and peer context.
  Low decode joins that same condition when below 15 msg/s or at least 25%
  below a meaningful local two-hour rate. Queue pressure absorbs AI queue
  blocking, and AI service failures plus truncation become one incident-
  generation finding. Service liveness, resources, bandwidth, Qdrant, ingest
  pause, and talkgroup policy remain separate because their owner or action
  differs. Live verification returned exactly one card per affected RF site,
  no active legacy decode/retune card, and matching Raymond headline/target
  evidence (24 retunes; four frequencies x 6). All 446 tests passed. Both
  services remained active and Trunk Recorder retained its July 11 activation
  timestamp.
- 2026-07-14: Recommendations follow-up `f97771a` removes the separate
  Recently Resolved section and combines all resolved findings into the
  collapsible 90-day Finding history table. Each history row now includes a
  persisted resolution: either the current evidence no longer met the finding
  threshold, or a related metric-level finding was superseded by its combined
  condition-level finding. The migration backfills existing history using the
  same distinction, with a narrow timing check to avoid falsely classifying old
  calls findings as superseded. All 446 tests passed. The deployed API returned
  13 history rows with no empty resolutions, and live browser verification
  confirmed the single collapsible history table and populated Resolution
  column. PizzaWave and Trunk Recorder remained active, and Trunk Recorder
  retained its `Sat 2026-07-11 17:11:15 EDT` activation timestamp.
- 2026-07-14: Runtime implementation `d7b41e4` standardizes Services,
  Resources, Queue, and Jobs around concise numeric state, one supporting
  table, and collapsed technical evidence. Services now includes LM Studio or
  its completion route without assuming where LMLink runs the model. Resources
  adds passive host memory, per-stack process CPU/RSS, `lsusb` facts, and
  filtered kernel USB warnings without opening, probing, resetting, or
  detaching devices. Queue keeps live ingest controls above numeric ingest and
  audio-throughput KPIs. Jobs adds live counts, a truthful empty state, polling,
  and producer-declared cancellation. The complete job-producer audit found
  only the setup and backup runners: both now serialize start races, setup work
  has one execution lane and linked lifetime ownership, all active jobs appear
  on the dashboard, and cancellation respects safe transaction boundaries;
  calibration sweep cancellation stops its process tree and restores TR.
  Follow-up `29f948d` distinguishes the local LM Studio service from a healthy
  relayed completion route and updates the resource recommendation destination.
  The production bundle is `assets/index-BNT5VGbU.js` with
  `assets/index-Csw3t_mN.css`, and all 452 tests pass. Live APIs returned health
  `ok`, clear queues, healthy AI and embeddings, four stack process rows, six
  USB device rows, and one retained July 11 kernel USB warning. The live jobs
  API and database both truthfully contained zero jobs. Browser verification
  found no console errors or table overflow across all four Runtime pages.
  PizzaWave and Trunk Recorder remained active, and Trunk Recorder retained its
  `Sat 2026-07-11 17:11:15 EDT` activation timestamp.
- 2026-07-14: Runtime Services correction `0c9e635` removes the duplicated
  action-card/KPI presentation and provides exactly one evidence-and-action card
  for each of PizzaWave, Trunk Recorder, LM Studio/AI, Vector Search, and
  Embeddings. Service actions are compact 24-pixel controls, the technical
  service table is immediately visible, and Trunk Recorder lists its current
  metric and per-system issues instead of reporting an unexplained issue-row
  count. The TR health contract also stops treating message-rate sample volume
  as a fault by itself; zero-rate evidence owns that condition. The production
  bundle is `assets/index-BUs1UfN5.js` with `assets/index-C6P32BDY.css`, and all
  454 tests pass. Live browser verification found five service cards, no
  duplicate status cards, no page overflow or console errors, and the visible
  technical table. PizzaWave health returned `ok`; Trunk Recorder remained
  active and retained its `Sat 2026-07-11 17:11:15 EDT` activation timestamp.
- 2026-07-14: Runtime Services/resources review `1e39c8e` through `74f62bc`
  merges Resources into Services and removes the duplicate subpage and process
  table. Host CPU is now a passive `/proc/stat` KPI; each service card reports
  cgroup-wide CPU and resident memory, including LM Studio's four detached
  daemon/LMLink processes. The LM Studio one-shot unit is presented as Running,
  `Launcher exited`, and main PID `N/A`, rather than as a failed workload.
  Retained USB kernel evidence is separated from current health: the live host
  has one event since boot but zero current actionable issues, so the shelf is
  collapsed and Resources remains healthy. The generic resource banner was
  removed, and the collapsed measurement shelf now explains methodology without
  repeating live statuses. The deployed bundle is `assets/index-C7MEIiTx.js`
  with `assets/index-KEOqhkvj.css`; all 459 tests pass. Live browser verification
  found five service cards, only Services/Queue/Jobs Runtime tabs, compact
  24-pixel controls, a visible technical table, collapsed historical USB and
  methodology shelves, no page overflow, and no console errors. PizzaWave
  health is `ok`, queues are clear, and Trunk Recorder retained its
  `Sat 2026-07-11 17:11:15 EDT` activation timestamp.
- 2026-07-14: Runtime CPU presentation follow-up `73a8041` keeps Linux's raw
  core-equivalent process CPU in the API evidence but adds and displays a
  whole-host percentage for each service. Operator cards now use the familiar
  0-100% convention where 100% means the entire machine is saturated; on this
  four-core host, a raw TR value around 129% rendered as about 32%. The display
  is capped at 100%, the measurement explanation states the convention, and all
  460 tests pass. Live API and browser verification confirmed normalized values
  for PizzaWave, Trunk Recorder, LM Studio, and Qdrant with no console errors.
  PizzaWave health is `ok`, queues are clear, and Trunk Recorder retained its
  `Sat 2026-07-11 17:11:15 EDT` activation timestamp.
- 2026-07-14: Runtime load presentation follow-up `8e769de` removes the invalid
  conversion of Linux load average into a percent-of-host value. The KPI is now
  `1-Minute Load`; it retains the numeric load average and states the host's
  four-logical-CPU comparison point, explaining that values above 4.00 indicate
  queued or I/O-blocked work. The focused UI contract suite passes. A web-only
  deployment completed without restarting PizzaWave or Trunk Recorder, and live
  browser verification confirmed the corrected text with no console errors.
- 2026-07-14: Runtime Queue redesign `17832e1` limits the page to five
  queue-owned KPIs, removes the repetitive Current Status and generic warnings/
  blockers tables, and replaces them with one actionable condition card that
  appears only while ingest is paused, queue pressure exists, or the backlog is
  growing. Queue Composition now separates priority, standard, deferred,
  backlog, and database-pending work; the oldest pending calls appear only when
  present and paginate ten rows at a time. Live-ingest controls are compact and
  remain above the KPIs, `Pause Until Queue Clear` is disabled for an empty
  queue, and the page explicitly warns that pausing discards calls while Trunk
  Recorder continues. The production build and all 460 tests pass. The web-only
  deployment installed `assets/index-Ouv5qkiE.js` and
  `assets/index-D_VURoHj.css` without restarting PizzaWave. Live API evidence
  showed ingest accepting calls, a clear queue, 59.3 audio seconds/min entering
  and 61.6 completing; the deployed bundle contains the new controls and no
  former status or warnings table. Trunk Recorder retained its
  `Sat 2026-07-11 17:11:15 EDT` activation timestamp.
- 2026-07-14: Runtime Jobs redesign `7099ea2` replaces historical row counts
  with Running, Waiting, Needs Attention, and Recent Completion summaries;
  normal active work no longer renders as a warning. Active work is always
  visible above locally-windowed job history, whose explicit 24-hour, 2-day,
  7-day, and 30-day selector is independent of the global call-data range.
  Operation and outcome filters, human-readable names, compact safe controls,
  accurate empty states, progress, timing, results, and expandable exact
  timestamps/cancellation evidence/raw paginated logs make the table both
  actionable and appropriately sized. Failed jobs remain current until a later
  successful run of the same operation supersedes them; canceling jobs use a
  persisted update timestamp for five-minute stall evidence. The schema upgrade
  path and all 461 tests pass. Full ARM deployment completed in 212.9 seconds;
  PizzaWave became healthy after its startup migration, the live jobs API
  truthfully returned no records, and browser verification found independent
  history-window behavior, no horizontal page overflow, and no console errors.
  Trunk Recorder retained its `Sat 2026-07-11 17:11:15 EDT` activation timestamp.
- 2026-07-14: Data Storage redesign `1868510` replaces sampled storage cards
  with exact database, recorded-audio, vector-data, PizzaWave-total, and
  disk-free measurements. File traversal reports read errors and marks totals
  partial instead of silently presenting incomplete evidence. Manual Vacuum,
  Optimize, and Prune controls and their write endpoint are removed. A
  serialized daily worker now runs only SQLite `PRAGMA optimize` and 30-day
  completed-job/history pruning, records exact affected counts and duration as
  a normal non-cancellable Jobs entry, and never runs Vacuum. Compact
  maintenance history is visible on Storage while technical table counts and
  paths remain collapsed. The production bundle and all 463 tests pass. Full
  ARM deployment completed in 214.0 seconds with assets
  `assets/index-DyM5n_dL.js` and `assets/index-n29SkVoa.css`; the first automatic
  run completed successfully in about 50 ms. Live exact evidence reported 2.4
  GB of PizzaWave data, 23,272 audio files, 49 Qdrant files, zero read errors,
  and 84% disk free. Browser verification found no page overflow or console
  errors. Trunk Recorder retained its `Sat 2026-07-11 17:11:15 EDT` activation
  timestamp. Operator acceptance followed with removal of one conversational
  implementation note; that wording-only fix will deploy with the next Data
  section.
- 2026-07-14: Data Backup and Restore redesign `e9c2dea` makes archive creation,
  local inventory, external archive upload, staged verification, and deliberate
  apply/cancel one coherent workflow. Reset is now a separate Data subpage with
  mutually exclusive Data Only, Site Reset, and Full Reset scopes plus an
  explicit pre-reset backup safeguard. The Help modal and duplicated empty
  Restore card are removed; essential scope appears beside the owning control,
  backup contents remain expandable, paths are demoted, and local archive
  actions are compact. A staged restore is durable across navigation and reload
  and displays included areas, verification checks, warnings, and technical
  provenance before Apply is available. Live validation caught an empty-body
  response for the absence of a staged restore; follow-ups `d7b4b31` and
  `6aa965b` corrected the endpoint to return the explicit JSON value `null`.
  The production bundle and all 464 tests pass. Full ARM deployment completed
  in 158.2 seconds, followed by backend-only corrective deployments. Live
  evidence showed one intact 729 MB local archive, a 3.0 GB estimate across
  24,187 source files for the seven-day audio scope, exactly one selected Reset
  scope, no page overflow, and no console errors. The approved Storage wording
  correction also deployed. Trunk Recorder retained its
  `Sat 2026-07-11 17:11:15 EDT` activation timestamp. Operator acceptance is
  pending.
- 2026-07-14: Backup sizing follow-ups `d84d367` and `16c5965` initially
  misdiagnosed the operator's wide-screen report as an individual KPI-card
  problem. The actual defect was the `1100px` cap on the complete Backup and
  Restore layout, which was hidden by the 1280-pixel validation viewport and
  left roughly half the operator's wider display unused. Follow-up `629df63`
  removes the page-level cap, restores the normal KPI fill behavior, and also
  gives Reset the same full-width contract. At a deliberate 2560-pixel
  verification viewport, Backup cards and inventory now consume the same
  2,352-pixel content width as Storage's primary grid, with no overflow or
  console errors. The web-only deploy did not restart PizzaWave or Trunk
  Recorder.
- 2026-07-14: Storage width follow-up `73b13b3` removes the remaining
  `1100px` cap from Automatic Maintenance. At the 2560-pixel verification
  viewport, Storage KPIs, Automatic Maintenance, and technical details now all
  use the same 2,352-pixel card width; the maintenance table uses the full
  2,333-pixel inner content width. No overflow or console errors were present,
  and the web-only deploy restarted neither service.
- 2026-07-14: Responsive Data sizing follow-up `6d0c005` separates full-width
  section ownership from KPI-card sizing. Storage and Backup sections/tables
  remain fluid, while their KPI cards are bounded to 220–280 pixels on normal
  displays, reflow below 800 pixels, and collapse to one column below 520
  pixels. Coarse-pointer Backup/Restore and Reset actions receive 40-pixel
  minimum touch targets. Deployed verification covered 2560x1400, 1280x800,
  1024x600, and 800x480: KPI cards remained bounded, tables used internal
  scrolling when needed, and no viewport had page-level horizontal overflow or
  console errors. The web-only deploy restarted neither service.
- 2026-07-14: System-wide responsive audit extends the sizing contract across
  all 17 Recommendations, Runtime, Data, Trunk Recorder, and Performance
  subpages. System navigation now wraps, full-width section owners remain
  fluid, sparse KPI cards stop at 280 pixels, ordinary service cards stop at
  360 pixels, recommendation cards stop at 520 pixels, and chart/evidence grids
  stop at 620 pixels per item. Dense tables scroll inside their owner instead
  of widening the page, while coarse-pointer controls receive 40-pixel minimum
  targets without enlarging desktop controls. Live checks at 2560x1400,
  1024x600, and 800x480 found no document or System-workspace horizontal
  overflow. The audit caught and corrected an initial rule that stretched the
  Calls chart beyond 2,200 pixels; Calls is now a 520-pixel chart in an
  860-pixel owner, RF/transcription charts retain their 520/620-pixel sizing,
  transcription sample evidence stops at 1,260 pixels, and Finding History's
  760-pixel table scrolls inside its 627-pixel compact owner. The deployed
  assets are `assets/index-GjNZtEif.js` and `assets/index-BVeIGuRj.css`.
  Deployment used the current `sdr1861` Tailscale route `100.105.110.92` and
  restarted neither PizzaWave nor Trunk Recorder; stale RPI route guidance in
  `docs/current-status.md` was corrected.
- 2026-07-15: Data Reset safety follow-up makes the three exposed scopes the
  only supported API scopes and rejects concurrent, unknown, or combined reset
  requests before any state change. A requested safety backup now completes
  before PizzaWave pauses ingest, so a failed or long-running archive no longer
  discards live calls before destructive work begins. The page states the exact
  operational boundary: PizzaWave ingest remains paused after reset, Trunk
  Recorder is not stopped, and the operator resumes ingest from Runtime / Queue
  after reset and any required Setup work. Site Reset now identifies preserved
  host/processing settings, while Full Reset identifies admin-token
  regeneration. All 469 tests pass. Full ARM deployment installed
  `assets/index-CXWeZFVf.js` and `assets/index-BUeNY8Du.css`; PizzaWave restarted
  healthy, ingest returned accepting with zero dropped calls, and Trunk Recorder
  retained its `Sat 2026-07-11 17:11:15 EDT` activation timestamp. At 1024x600,
  both Reset cards use the full 801-pixel content width, the three scope choices
  remain balanced at 255 pixels each, and neither the document nor System
  workspace overflows horizontally. Operator acceptance is pending.
- 2026-07-15: Reset presentation follow-up moves the destructive Run action
  into the Recovery Safeguard panel so scope selection, live-operation impact,
  recovery choices, and execution form one deliberate sequence instead of two
  distant cards. The recorded-audio window is now a compact 112-140-pixel
  selector rather than inheriting the generic 560-pixel Settings control width.
  Web-only deployment installed `assets/index-Ba0_ZXyQ.js` and
  `assets/index-D0ooUx5m.css` without restarting either service. At 1024x600,
  the safeguard panel is 781 pixels wide, the Run button sits 14 pixels from
  its top edge, the audio selector renders at 112 pixels, and neither the page
  nor System workspace overflows horizontally. Operator acceptance followed.
- 2026-07-15: Data Audit History replaces the setup-only activity table with a
  locally scoped 24-hour, 2-day, 7-day, 30-day, or 90-day history of meaningful
  setup/settings changes, terminal job outcomes, and resolved Recommendation
  findings. It has an independent event-type filter and 25-row pagination and
  does not use the global call-data selector. Job rows remain concise and link
  directly to Runtime / Jobs, which continues to own progress, cancellation,
  raw logs, and detailed history. Resolved findings moved off Recommendations;
  repeated resolution cycles for the same condition are consolidated within
  the selected window while retaining occurrence count and first-to-last
  evidence span. Successful general Settings and processing-profile mutations
  now append durable activity records containing section/field names and safe
  identifiers, never setting values or secrets; an audit-write failure is
  logged without misreporting the already-successful configuration save. All
  470 tests pass. Full ARM deployment completed in 83.1 seconds and the final
  web-only consolidation follow-ups completed without service restarts, ending
  with assets `assets/index-B0ovrI9K.js` and `assets/index-YwsNYuK5.css`. Live
  seven-day evidence consolidated 144 raw events into 77 rows; the Findings filter
  consolidated 75 resolutions into 8 condition rows. At 1024x600 the page has
  no document-level overflow, and the 878-pixel evidence table scrolls inside
  its 801-pixel owner. Direct Jobs navigation and local filtering passed with
  no browser errors. PizzaWave restarted healthy at
  `Wed 2026-07-15 08:37:14 EDT`; Trunk Recorder retained its
  `Sat 2026-07-11 17:11:15 EDT` activation timestamp. Operator acceptance is
  pending.
- 2026-07-15: Audit History disclosure follow-up removes the narrow Evidence /
  View column. Each history row now has subtle hover, focus, and expanded-state
  color and opens its evidence immediately below the owning row by pointer,
  Enter, or Space. Only one shelf can be open; changing page or filter closes
  it. The evidence owner is exactly 160 pixels high and internally scrollable,
  so large JSON records cannot consume the page. At 1024x600 the five-column
  table fits its 799-pixel owner without horizontal scrolling or document
  overflow. Pointer switching, keyboard expansion, and single-shelf behavior
  passed with no browser errors. All 470 tests pass. Web-only deployment
  installed `assets/index-BGscLEqB.js` and `assets/index-tZpTJazK.css` without
  restarting PizzaWave or Trunk Recorder. Operator acceptance followed,
  completing the Package 8 Data section.
- 2026-07-15: Trunk Recorder Summary now leads with the monitored site that
  needs action instead of presenting a stack-wide sea of red. Structured
  per-site health attributes decode rate, decode-zero samples, calls, no-audio
  outcomes, retunes, latest evidence, configured source, and USB device to the
  exact site; one failed source no longer makes every monitored site appear
  unavailable. The page uses its own 2-hour, 6-hour, 24-hour, or 3-day RF-health
  selector and leaves the global call-data selector untouched. It retains the
  40 msg/s strong-system reference, links RF work directly to Setup, presents
  source assignments immediately, and keeps stack-wide and raw bucket evidence
  in a bounded collapsed shelf. The server now honors the requested summary
  window rather than silently aggregating the latest 24 hours. All 472 tests
  pass. Full ARM deployment completed in 111.2 seconds with assets
  `assets/index-BM07emcU.js` and `assets/index-v-dQFfKp.css`; PizzaWave restarted
  healthy at `Wed 2026-07-15 09:20:44 EDT`, ingest remained accepting with no
  dropped calls, and Trunk Recorder retained its
  `Sat 2026-07-11 17:11:15 EDT` activation timestamp. Live two-hour evidence
  identifies Raymond as healthy at 39.6 msg/s with 0.0% zero-decode samples and
  isolates the disconnected Jackson source as unavailable at 0.0 msg/s and
  100.0% zero-decode samples. At 1024x600 neither the document nor System
  workspace overflows; only the intentionally scrollable raw-evidence table
  exceeds its owner. The technical shelf is collapsed by default and the local
  RF selector changed windows without changing the global 24-hour selector.
  Operator acceptance is pending.

- Remote transcription outage follow-up (2026-07-16): Paxan's Windows startup
  task launched faster-whisper as SYSTEM and the CUDA model loaded in 15.5
  seconds, but the Tailscale path did not become reachable from the PizzaWave
  node until the operator logged in at 7:03 AM. Windows Tailscale unattended
  mode is now enabled. PizzaWave now probes the configured remote
  faster-whisper health endpoint every 30 seconds, confirms an outage after two
  failures, raises a high/critical Recommendation, shows live endpoint state on
  Transcription Performance, and supports administrator outage/recovery email.
  Transient endpoint failures remain pending and retry after recovery instead
  of becoming terminal engine failures. The prior outage left 416 terminal
  failures under the old behavior; the remaining queued calls drained after
  network recovery.
- 2026-07-15: Trunk Recorder Summary threshold follow-up removes the brittle
  site-wide warning caused by any no-audio rate at or above 5%. Decode,
  zero-decode, call activity, no-audio outcomes, and retunes now receive
  independent green, yellow, or red assessments. A site-local baseline
  becomes authoritative after at least 72 stored buckets spanning 12 hours;
  concise static fallbacks are identified until that history matures, while
  40 msg/s remains visible as the strong-system reference. ETV's live 24-hour
  evidence is now Healthy: 35.6 msg/s decode, 3.5% zero-decode, 5.3% no-audio,
  and all five KPIs within local behavior. Jackson remains correctly
  Unavailable, with decode, zero-decode, and call activity red while no-audio
  and retune evidence that cannot be evaluated is yellow. The Configuration /
  Callstream / Technical paths
  strip and stack-wide signal table were removed; the collapsed shelf now owns
  only bounded raw RF-health samples. Every site KPI is a compact link to
  Performance / Radio Frequency, automatically enables by-system evidence, and
  selects Decode, Calls and audio, or RF events. Radio Frequency now supports
  that chart-category filter and includes by-system CC-summary and message-rate
  zero charts. All 473 tests pass. Full ARM deployment completed in 145.4
  seconds, a corrective backend package in 111.8 seconds, and the final web-only
  package in 4.8 seconds. The final green/yellow/red semantics package completed
  in 115.5 seconds with assets `assets/index-C7g5JG13.js` and
  `assets/index-hhTWy-B7.css`. PizzaWave is healthy and active from
  `Wed 2026-07-15 10:51:18 EDT`; ingest is accepting with zero dropped calls,
  and Trunk Recorder retained its `Sat 2026-07-11 17:11:15 EDT` activation
  timestamp. At 1024x600 Summary and the filtered Performance view have no
  document overflow. Zero Decode navigation displayed all four matching
  by-system charts with no browser errors. Operator acceptance is pending.
- 2026-07-15: RF ownership follow-up removes the remaining contradictory
  Recommendation path. Recommendations had independently activated on five or
  more retunes in 15 minutes even when the site's locally learned retune rate
  considered that behavior normal. It now calls the same localized per-site
  assessment contract as RF Performance; separate static decode/zero/retune
  triggers no longer create the finding. The Recommendation is driven by the
  two-hour assessment, names the affected site's general RF-performance
  condition, and links directly to RF Performance with the two-hour window and
  by-system charts selected. Live evidence is consistent: ETV is Needs review
  on both surfaces with only No Audio yellow, while its retune metric is green;
  Jackson is Unavailable on both surfaces. Trunk Recorder Summary no longer
  presents health or raw RF evidence. It is a configuration explorer showing
  capture topology, two balanced source-assignment cards, tuning windows,
  sample rates, recorder capacity, nested site coverage, and a configured-sites
  table; Active Config and Logs remain separate tabs. RF health cards, their
  independent local selector, category-linked KPIs, charts, analysis, and raw
  samples now live together under Performance / Radio Frequency. All 473 tests
  pass. Full ARM deployment completed in 95.4 seconds and the final window-
  alignment and ownership-copy web deployments ended in 5.3 seconds with assets
  `assets/index-0bevGHsD.js` and `assets/index-BWL8oqtA.css`. PizzaWave is
  healthy and active from `Wed 2026-07-15 12:41:39 EDT`; ingest is accepting
  with zero dropped calls, and Trunk Recorder retained its
  `Sat 2026-07-11 17:11:15 EDT` activation timestamp. At 1024x600 neither page
  overflows horizontally; the source cards are 395 pixels each and the site
  table fits its 781-pixel owner. Recommendation navigation, page ownership,
  and browser logs passed. Operator acceptance is pending.
- 2026-07-15: Trunk Recorder Summary follow-up removes the duplicate Configured
  Sites table. Source Assignments is now the single configuration view; each
  nested site shows its system type, control channel, observed range, and
  concise talkgroup filename within the assigned source card. All 473 tests
  pass. The web-only ARM package deployed in 13.3 seconds with assets
  `assets/index-Ci_Rh07p.js` and `assets/index-jxe8bHx_.css`; PizzaWave was not
  restarted. Live validation showed no duplicate heading, horizontal overflow,
  or browser errors. Operator acceptance is pending.
- 2026-07-15: Trunk Recorder Active Config is replaced by a strictly read-only
  Config Viewer. It catalogs the active configuration, unapplied drafts, the
  complete legacy/automatic config backup family, and meaningful RF-workflow
  artifacts without restore, apply, edit, or delete controls. The live host has
  204 viewable artifacts: one active config, 197 backups, and six RF artifacts.
  Type filtering, contextual search, touch-sized selection, recorded workflow
  provenance, structured system/source counts, fixed-height raw JSON, copy, and
  an active-config comparison keep the large history usable. Setup and RF
  workflows now record durable provenance for new safety backups; older rows
  state when origin is unavailable. A narrowly allowlisted helper reads only
  `/etc/trunk-recorder/config.json` and its sibling artifact family; arbitrary
  paths are rejected. Live validation found and corrected protected-backup read
  permissions before acceptance. All 475 tests pass. The final full ARM package
  deployed in 150.1 seconds and the final web-only refinement in 5.4 seconds
  with assets `assets/index-BcBqk2Hq.js` and
  `assets/index-DC_wAk4Q.css`. PizzaWave is healthy and active from
  `Wed 2026-07-15 13:25:36 EDT`; Trunk Recorder retained its
  `Sat 2026-07-11 17:11:15 EDT` activation timestamp. Protected legacy backup,
  RF artifact, active comparison, search, bounded JSON scrolling, responsive
  containment, and browser logs passed. Operator acceptance is pending.
- 2026-07-15: Trunk Recorder Logs now uses a dedicated read-only journald API
  instead of loading the full troubleshooting dataset and inaccurately labeling
  a 300-line tail as the last hour. The page defaults to a true one-hour local
  range, offers six-hour, 24-hour, and custom start/end controls independent of
  the global call-data selector, and displays 250 chronological raw lines in a
  fixed-height scroll area with copy and Older/Newer navigation. Pagination uses
  an opaque timestamp-and-entry token so same-timestamp journal entries are not
  duplicated or skipped. Live validation exposed and corrected both reverse-
  cursor semantics and journald byte-array messages containing TR terminal color
  codes. Two consecutive pages returned without duplicates, message text remained
  readable, the viewer contained 250 lines without document overflow, and browser
  logs were empty. An operator-requested follow-up condenses the title, range, page count,
  copy, and pagination controls into one 58-pixel responsive toolbar so log
  content begins near the top of the page; custom dates wrap only when selected.
  All 477 tests pass. The final assets are `assets/index-DmrxIlUZ.js` and
  `assets/index-JqqNSUtJ.css`. PizzaWave is healthy
  and active from `Wed 2026-07-15 13:52:04 EDT`; Trunk Recorder retained its
  `Sat 2026-07-11 17:11:15 EDT` activation timestamp. Operator acceptance is
  pending.
- 2026-07-15: Performance / Radio Frequency replaces the disjointed global
  chart dump, manual RF Analysis, and raw five-minute sample table with one
  site-scoped performance workflow. Window, site, Signal/Capture/Events chart
  category, and local-baseline controls share one compact row. Decode rate,
  zero-decode share, calls recorded, calls without audio, retunes, and
  exceptional capture interruptions use adaptive 15-minute through three-hour
  buckets; the three-day range now retains its complete evidence instead of
  silently showing only 24 hours. Every chart compares the selected site with
  its own historical behavior, while decode also keeps the 40 msg/s strong-
  system reference. Charts provide five rounded time labels, five numeric
  Y-axis labels with plain units, point details, full-width two-column desktop
  layout, and a single-column 1024-pixel layout. Live validation caught and
  corrected both local/UTC bucket comparison and an empty future bucket that
  created false zero values. Raymond's deployed series now remains near
  39-40 msg/s while Jackson remains isolated at zero. The formerly blank
  Review in Setup action now opens RF Validation / Control-Channel Proof,
  preselects the affected site's control-channel evidence, and names the site
  in the handoff. All 481 tests pass. The final web assets are
  `assets/index-DJTdUuE9.js` and `assets/index-BJRpQB9k.css`; PizzaWave is
  healthy and active from `Wed 2026-07-15 23:04:22 EDT`, and Trunk Recorder
  retained its `Sat 2026-07-11 17:11:15 EDT` activation timestamp. Operator
  Operator acceptance followed after removing a standalone explanatory cost-
  basis sentence that read like implementation commentary. The web-only
  follow-up deployed in 6.3 seconds with assets `assets/index-B3Rs5tZe.js` and
  `assets/index-bLu_YG9U.css`; PizzaWave and Trunk Recorder retained their
  activation timestamps and zero restart counts.
- 2026-07-16: System UI copy must answer an operator question or support an
  operator decision in its owning context. Do not insert conversational,
  defensive, implementation-explaining, or LLM-sounding notes into arbitrary
  page locations unless the operator explicitly requests that explanation in
  the product. Keep necessary qualifications inside the label, value, tooltip,
  disclosure, or technical provenance that owns them.
- 2026-07-16: Radio Frequency layout follow-up removes the redundant site-
  availability callout above the site cards. The two site cards now divide the
  available desktop width evenly and stack at the 10-inch touch breakpoint.
  Charts keep their native aspect ratio and use three balanced columns on wide
  desktops, two on ordinary desktops, and one on the touch layout instead of
  stretching two plots across the entire page. The focused UI contract suite
  passes. The web-only ARM package deployed in 6.4 seconds with assets
  `assets/index-BE1Mbo8l.js` and `assets/index-Bd6XLJ9W.css`; neither PizzaWave
  nor Trunk Recorder was restarted. Live measurements show 843-pixel site cards
  and 559-pixel proportional chart cards at 1920x1080, plus full-width 801-pixel
  cards with proportional plots and no overflow at 1024x600.
- 2026-07-16: Performance / Incidents is rebuilt as two equal operator views:
  complete pipeline behavior and generated-output quality. It owns an
  independent 24-hour, two-day, or week selector instead of inheriting the
  global call-data window. A lightweight aggregate endpoint returns complete
  accepted/rejected decision counts and adaptive buckets without transferring
  large audit metadata or silently hitting the existing 250-row evidence cap.
  The pipeline half shows total, accepted, rejected, acceptance share, and a
  time chart; acceptance share is explicitly descriptive rather than a target.
  The output half shows incidents created, mapped-location coverage, retained
  calls per incident, concrete review gaps, and a creation chart. Category and
  retained-call distributions are descriptive. Single-call incidents, model
  confidence, and short transcripts are no longer treated as defects by
  themselves. Review rows are limited to visible gaps such as missing mapped
  location, title/detail, category, or retained calls and open the affected
  Dashboard incident directly. Recent raw decisions remain in a collapsed,
  paginated, horizontally contained evidence table. All 482 tests pass. The
  full ARM package deployed in 58.1 seconds with assets
  `assets/index-nZuZcOyx.js` and `assets/index-CeWlbHy2.css`. Live 24-hour
  evidence returned 248 complete decisions (134 accepted, 114 rejected) across
  25 aligned buckets and 20 generated incidents. Two-day selection changed the
  local total to 668 while the global selector remained at 24 hours. At
  1280x720 both halves are balanced 523-pixel columns; at 1024x600 they stack
  into the available 801-pixel width without document overflow. The 980-pixel
  decision table scrolls inside its 801-pixel owner. Direct incident navigation
  opened and expanded the requested incident, browser logs were clean,
  PizzaWave is healthy with zero restarts after its expected deploy activation,
  and Trunk Recorder retained its `Sat 2026-07-11 17:11:15 EDT` activation.
  Operator acceptance is pending.
- 2026-07-16: Remote transcription outage recovery now owns durable endpoint
  episodes, validates `ok=true` and the configured model, and keeps outage email
  deduplication across PizzaWave restarts. Historical incident catch-up is split
  into bounded site/time cohorts so recovered calls older than the former
  60-minute rolling window are actually evaluated before acknowledgment. The
  incident-analysis queue is now durable instead of an in-memory, 1,000-call
  capped list, so service restarts and longer outages cannot silently discard
  pending analysis work.
  Alert matches and incident history retain original call times; email and
  browser playback are suppressed when processing is more than 60 minutes late.
  System > Jobs adds an explicit, cancellable, one-call-at-a-time failed-
  transcription recovery operation that yields to live and existing backlog
  work. System > Performance > Transcription shows the durable endpoint outage
  history. Windows Tailscale setup now explains the persistence and limitations
  of `tailscale up --unattended=true`.
- 2026-07-16: The remote-transcription follow-up keeps Endpoint Outage History
  visible on Performance / Transcription even when its selected window is
  empty. Recoverable failed transcripts now produce one medium Recommendations
  finding for the fixed last-24-hour evidence window, linking directly to a
  collapsed, explicitly optional Recovery Tools section below Active Jobs.
  System subpages now share a compact area-colored identity band with an icon,
  title, and one-line purpose so page composition is clearer without adding
  oversized hero content.
- 2026-07-16: Empty failed-transcription recovery and endpoint-outage windows
  now point operators toward the available longer windows without asserting
  that older evidence exists. The global color selector adds Red, Purple, and
  Green alongside Blue and Orange. A UI-wide accent audit routes System identity
  bands, active Setup/RF workflow states, informational callouts, interactive
  code/links, and call-activity heatmap intensity through the selected theme.
  Health, severity, category, and multi-series chart colors remain semantic and
  independent of the cosmetic theme.
- 2026-07-16: Performance / AI Usage now separates operator health, capacity,
  cost comparison, trends, failure evidence, and raw request provenance. The
  compact KPI row retains selected-window, month, and All-Time token totals,
  adds request success rate, and explicitly labels the existing OpenAI text
  inference comparison at $2 per million input tokens and $8 per million output
  tokens as an estimate rather than an actual bill or transcription price.
  Request outcomes and prompt/completion tokens use two aligned time charts;
  activity attribution remains concise; duplicate failure tables are combined
  with bounded individual evidence; and the raw ledger is subordinate,
  collapsible, and server-paginated. The API no longer silently derives charts
  and pagination from only the newest 500 rows: summaries, failures, and buckets
  use the complete selected window while each ledger response returns 20 rows
  plus a truthful total. The production build and all 491 backend tests pass.
  Full ARM deployment completed in 250.6 seconds with assets
  `assets/index-WcSrt8nO.js` and `assets/index-DPRt9--4.css`. Live evidence
  returned 411 complete selected-window requests, 20 page-one ledger rows, 411
  total rows, and 18 time buckets. PizzaWave is healthy and active from
  `Thu 2026-07-16 19:36:22 EDT` with zero restarts; Trunk Recorder retained its
  `Sat 2026-07-11 17:11:15 EDT` activation timestamp and zero restarts. Operator
  acceptance is pending.
- 2026-07-16: Performance / Bandwidth now preserves Selected Range, This Month,
  and All Time totals while adding complete-window Data Transferred and Request
  Volume timelines with five readable time labels. Usage remains attributed
  only to PizzaWave remote-transcription and AI workflows. The raw Bandwidth
  Activity ledger and technical provenance are subordinate disclosures, and
  ledger pagination is server-backed at 20 rows per page. The API derives its
  adaptive one-, two-, or six-hour buckets from the complete selected window
  instead of a limited raw-entry page. All 492 backend tests and the production
  web build pass. The full ARM package deployed in 224.6 seconds with assets
  `assets/index-CI-mbMJz.js` and `assets/index-on8wp5q5.css`. Live 24-hour
  evidence returned 5,854 summarized requests, 5,854 bucketed requests, 43
  time/activity rows, 20 distinct rows on each of ledger pages one and two, and
  no page overlap. Browser verification at 1280 pixels found both charts, both
  collapsed disclosures, no document overflow, and no warnings or errors.
  PizzaWave is healthy and active from `Thu 2026-07-16 20:22:37 EDT` with zero
  restarts; Trunk Recorder retained its `Sat 2026-07-11 17:11:15 EDT`
  activation timestamp and zero restarts. Operator acceptance followed.
- 2026-07-16: The final Package 8 global pass removes the page-level `Refresh
  XYZ` control from System and standardizes global status controls. The top
  monitoring state and footer Jobs count now navigate to their owning Runtime
  pages; the duplicate footer Profile control is removed; the call-data window
  is explicitly labeled; and Queue and CPU pills retain their detailed
  tooltips without expanding into long status sentences. Runtime / Services no
  longer requests or displays RF receiver-health evidence, technical systemd
  details, or a measurement-explanation disclosure. Five separate service
  cards are replaced by one near-real-time visualization with paired CPU and
  resident-memory plots on a shared timeline, consistent service colors,
  current values, states, and compact safe controls. A lightweight live-resource
  endpoint avoids polling USB and kernel evidence; it updates every five seconds
  and feeds the same host CPU sample to the Services KPI and global footer.
  Host resource KPIs and passive USB evidence remain. The production build and
  all 493 backend tests pass. The full ARM package deployed in 137.6 seconds
  with assets `assets/index-D7R7apiC.js` and `assets/index-C1jfx4CX.css`; the
  shared-sample web follow-up deployed in 7.0 seconds with final asset
  `assets/index-C5RuoWVS.js`. Live resource evidence returned normalized host
  percentages and resident memory for PizzaWave, Trunk Recorder, LM
  Studio/LMLink, and Qdrant. Browser verification observed six samples per
  series before the follow-up, matching Host CPU values after it, no warnings
  or errors, no page refresh control, and no document overflow across all 17
  System destinations at 1280 pixels. PizzaWave is healthy and active from
  `Thu 2026-07-16 20:44:08 EDT` with zero restarts; Trunk Recorder retained its
  `Sat 2026-07-11 17:11:15 EDT` activation timestamp and zero restarts. Operator
  acceptance of this final pass followed after the Services layout and axis
  alignment follow-ups.
- 2026-07-16: Runtime / Services follow-up places the live CPU/memory chart and
  compact service breakdown side by side on desktop, with a slightly wider
  chart column. They stack below 1100 pixels to preserve the 10-inch touch
  layout. The focused 15-test UI contract suite and production build pass. The
  web-only package deployed in 6.4 seconds with assets
  `assets/index-CiBgHO55.js` and `assets/index-CwTOPEwp.css`; PizzaWave was not
  restarted. Live verification measured 588- and 435-pixel columns at a
  1280-pixel viewport, with no document overflow or browser warnings.
- 2026-07-16: The Services breakdown follow-up replaces independent per-row
  grids with a semantic table so Service, State, CPU, Memory, and Controls use
  shared columns and remain left-aligned. CPU and memory plots now have five
  labeled Y-axis levels with subtle gridlines. The focused UI suite and
  production build pass; the web-only package deployed in 6.7 seconds with
  assets `assets/index-DPojLiMp.js` and `assets/index-DuGB3HdQ.css` without
  restarting PizzaWave. Live browser geometry confirmed identical X positions
  for every cell in each data column, ten chart gridlines, no page overflow,
  and no warnings or errors.
- 2026-07-17: Performance / Radio Frequency adds a seven-day local lookback to
  its existing two-hour, six-hour, 24-hour, and three-day windows. The persisted
  selector accepts 168 hours. The focused UI suite and production build pass;
  the web-only package deployed in 6.6 seconds with asset
  `assets/index-DerOlPIf.js` without restarting PizzaWave. Live selection loaded
  the seven-day view with no browser errors or page overflow.
- 2026-07-17: Package 5 closure remediation was first deployed from a stale
  pre-redesign branch and temporarily replaced the current web interface. The
  fixes were reapplied on top of merged redesign commit `4468224`, all 498
  backend tests and the production frontend build passed, and commit `8d6d5bd`
  deployed to the RPI in 225.8 seconds. Live HTML and browser verification
  confirmed redesigned assets `assets/index-CyjY3E38.js` and
  `assets/index-DuGB3HdQ.css`, including the nested System / Performance UI.
  Health was `ok` with clear queues. Trunk Recorder remained PID 1595 with its
  unchanged July 11 start timestamp; no SDR inventory or Setup apply ran.
- 2026-07-17: The operator accepted Package 5 and directed that it be marked
  complete. Package 5 is closed.
- 2026-07-17: Package 7 was reclassified as a standalone outstanding feature.
  Its accepted design and persistence/timing foundation remain the handoff, but
  no additional Package 7 implementation will be done in this remediation
  closeout. Active work advances to Packages 9, 10, and 11.
- 2026-07-17: The operator accepted and closed Package 9 after the RF-first
  temporal-finding implementation and Recommendations UX closeout. The final
  UI uses concise severity/type cards, visually receded Dormant findings, a
  focused opaque drawer with operator notes and paginated activity, and a
  grouped History ledger that collapsed 262 live history records to 24 rows.
  All 502 tests passed; the final web-only deployment left Trunk Recorder at
  PID 1595 with its unchanged July 11 start. Cross-domain temporal-detector
  expansion remains future product work and is not a Package 9 closure blocker.
