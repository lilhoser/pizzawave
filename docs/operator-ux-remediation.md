# Operator UX Remediation

Last updated: 2026-07-10

This is the canonical execution tracker for the operator-facing PizzaWave
architecture review. Read it before beginning work and update it before ending
work so that progress does not depend on conversation history.

## Current Position

- Active package: 5 - Setup UX
- Current milestone: Package 5 milestone 5A RF Validation stages and terminology
  deployed and verified; awaiting operator inspection
- Working branch: `codex/operator-ux-setup-ux`
- Last deployed commit: `6b18b1a`
- Operator verification: Packages 1, 2, 3, and 4 accepted
- Next action: obtain operator acceptance of milestone 5A, then implement the
  server-owned source-planning projection as milestone 5B.

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

Status: discovery complete; operator interview in progress

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
- RF-path topology is a tree limited to one upstream antenna signal that may
  split through splitters or multicouplers into multiple downstream branches.
  Each branch preserves ordered components and ends at a detected SDR linked by
  stable hardware identity, preferably its serial number, or is marked unused.
  Setup does not silently rebind missing hardware. The server rejects loops,
  duplicate SDR endpoints, and impossible paths while allowing incomplete
  drafts. Existing linear chains migrate to one branch. Multiple antennas,
  combiners, switches, and rejoining branches are outside Package 5.
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

- [x] Clarify RF validation stages and frequency-correction terminology.
- [ ] Make source planning a server-owned, reviewable projection.
- [ ] Support branched RF paths and source-linked SDR hardware.
- [ ] Attach RF captures and reports to Setup evidence/activity.
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
