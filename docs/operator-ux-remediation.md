# Operator UX Remediation

Last updated: 2026-07-09

This is the canonical execution tracker for the operator-facing PizzaWave
architecture review. Read it before beginning work and update it before ending
work so that progress does not depend on conversation history.

## Current Position

- Active package: 1 - Setup state authority
- Current milestone: 1A - versioned, field-scoped Setup mutations
- Working branch: `codex/operator-ux-review`
- Last deployed commit: `d8e7ec9`
- Operator verification: not requested yet
- Next action: define the server-owned Setup mutation contract and replace
  whole-document UI writes without changing the visible workflow.

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

Status: in progress

- [ ] 1A. Replace stale whole-document Setup writes with versioned,
  field-scoped mutations.
- [ ] 1B. Represent RadioReference identity per selected system/site and remove
  the global-SID selection assumption.
- [ ] 1C. Persist waterfall/RF selections in Setup and replace the standalone
  TR-editor draft handoff with a Setup-owned calibration action.
- [ ] 1D. Confirm pending-change and activity records cover every resulting
  Setup mutation.
- [ ] Build and test.
- [ ] Deploy and verify on the RPI.
- [ ] Operator acceptance.

Acceptance:

- Multiple selected RR systems retain their own IDs and site data.
- Concurrent or delayed UI saves cannot silently overwrite newer Setup state.
- Waterfall and RF Sweep selections survive navigation and browser reload.
- Applying an RF candidate changes Setup desired state, creates an auditable
  pending change, and never writes an orphan standalone TR draft.
- Apply & Resume is the only workflow that installs the final TR config.

### 2. Talkgroups

Status: pending

- [ ] Move one-time RR import provenance to the server.
- [ ] Replace full-catalog writes with scoped, paged mutations.
- [ ] Route category and TR-exclusion changes through Setup activity and apply
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

## Deployment Log

- `d8e7ec9`: current deployed baseline; asynchronous auditable backup jobs.

## Verification Log

- 2026-07-09: Architecture review completed against deployed baseline. No code
  changes were made during the review.
