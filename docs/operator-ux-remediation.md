# Operator UX Remediation

Last updated: 2026-07-11

This is the canonical execution tracker for the operator-facing PizzaWave
architecture review. Read it before beginning work and update it before ending
work so that progress does not depend on conversation history.

## Current Position

- Active package: 5 - Setup UX
- Current milestone: Package 5 implementation and fault recovery deployed;
  final operator end-to-end acceptance and package closure remain
- Continuation branch: `main` after the final Package 5 integration
- Last deployed code commit: `60a566f`
- Latest handoff commit before this update: `0d7328a`
- Operator verification: Packages 1, 2, 3, and 4 accepted
- Next action: verify monitoring status has advanced from post-restart stale to
  active, perform one concise end-to-end review of the applied ETV Raymond plus
  Entergy Jackson Setup, then record operator acceptance or the exact remaining
  defect. Do not rerun Tylertown as a prerequisite for closure.

## Package 5 Final Handoff

Package 5 code is implemented, tested, deployed, and exercised against live
hardware. The package is not yet marked operator-accepted only because the
operator ended the session immediately after monitoring recovery. There is no
known code change queued or uncommitted at this handoff.

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

Remaining closure work only:

1. Read `/api/v1/health` and `/api/v1/setup/site`; confirm Trunk Recorder is no
   longer faulted and the applied ETV/Jackson hash and desired version above are
   unchanged. Do not restart Trunk Recorder merely to verify it.
2. Ask the operator to inspect Setup once end to end, especially Apply & Resume,
   Activity Log, Call and Transcription Proof, and Verdict. Preserve current
   controls and wording unless the operator identifies a concrete defect.
3. If monitoring is healthy and the operator accepts the workflow, change the
   Package 5 status below to `complete and operator-accepted` and record the
   acceptance in the dated log. Package 6 may then begin.
4. If monitoring is still stale but systemd is active and scoped decode lines
   continue, diagnose the live-activity status boundary separately; do not
   alter the validated source plan or catalog ownership fix.

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

Continuation order on the next machine:

1. Pull `origin/main` and confirm a clean worktree before doing any work.
2. Check post-reset health, ingestion state, Qdrant collection recovery, and
   current TR decode/retune behavior without restarting services merely to
   inspect them.
3. Complete the concise Package 5 operator acceptance pass described above.
4. Investigate disruptive SDR inventory ownership and the Setup polling storm
   as separate defects. Do not conflate either with the original 19:13 signal
   loss without new evidence.
5. Once Package 5 is accepted, update this tracker and begin Package 6.

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

Status: implementation complete and deployed; final operator acceptance pending

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
- [ ] Deploy, verify, and obtain operator acceptance.

### 6. Profiles And Alerts

Status: pending

- [ ] Add first-class Utilities profile policy.
- [ ] Protect unsaved profile changes.
- [ ] Scope alert talkgroups by system and TG ID.
- [ ] Correct profile navigation and contextual controls.
- [ ] Deploy, verify, and obtain operator acceptance.

### 7. Recovery Workflows

Status: pending

- [ ] Run restore/reset work through auditable background jobs where required.
- [ ] Improve archive scope, verification, and completion presentation.
- [ ] Deploy, verify, and obtain operator acceptance.

### 8. Cleanup

Status: pending

- [ ] Split the frontend by product ownership.
- [ ] Remove dead RSW, standalone TR-editor, and unused RF UI code.
- [ ] Replace the hand-built map interaction engine.
- [ ] Update operator and architecture documentation.
- [ ] Run final regression verification and obtain operator acceptance.

## Decision Log

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
