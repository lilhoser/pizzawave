# Operator UX Remediation

Last updated: 2026-07-09

This is the canonical execution tracker for the operator-facing PizzaWave
architecture review. Read it before beginning work and update it before ending
work so that progress does not depend on conversation history.

## Current Position

- Active package: 4 - System Workspace
- Current milestone: Package 3 closed and operator-accepted; Package 4 ready for discovery
- Working branch: `codex/operator-ux-loading-status`
- Last deployed commit: `4263348`
- Operator verification: Packages 1, 2, and 3 accepted
- Next action: begin fresh Package 4 repository discovery and operator
  interview before changing the System workspace architecture.

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

Status: pending

- [ ] Load each System tab independently.
- [ ] Make Refresh contextual to the visible tab.
- [ ] Surface only job controls supported by each job.
- [ ] Reorganize System navigation around operator tasks.
- [ ] Deploy, verify, and obtain operator acceptance.

### 5. Setup UX

Status: pending

- [ ] Clarify RF validation stages and absolute error terminology.
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
