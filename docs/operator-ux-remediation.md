# Operator UX Remediation

Last updated: 2026-07-09

This is the canonical execution tracker for the operator-facing PizzaWave
architecture review. Read it before beginning work and update it before ending
work so that progress does not depend on conversation history.

## Current Position

- Active package: 2 - Talkgroups
- Current milestone: Package 2 operator verification
- Working branch: `codex/operator-ux-review`
- Last deployed commit: `99f4c96`
- Operator verification: Package 1 accepted
- Next action: ask the operator to inspect Package 2 and confirm acceptance.

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

Status: in progress

- [x] Move one-time RR import provenance to the server.
- [x] Replace full-catalog writes with scoped, paged mutations.
- [x] Route category and TR-exclusion changes through Setup activity and apply
  state.
- [ ] Deploy, verify, and obtain operator acceptance.

### 3. Loading And Status

Status: pending

- [ ] Add page-local loading, refreshing, error, retry, and last-updated state.
- [ ] Keep last-good data visible during refresh.
- [ ] Debounce search and stop unrelated full-page refreshes.
- [ ] Separate API connectivity, TR monitoring state, and view-fetch failures.
- [ ] Deploy, verify, and obtain operator acceptance.

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

## Deployment Log

- `d8e7ec9`: starting baseline; asynchronous auditable backup jobs.
- `9c48f30`: Package 1 deployed; versioned Setup mutations, per-site RR
  identity, Setup-owned RF handoff, and explicit live baselining for previously
  unspecified source gain.
- `99f4c96`: Package 2 deployed; server-owned RR import provenance, explicit
  per-system refresh, server-paged catalog reads, and scoped Setup policy
  mutations.

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
