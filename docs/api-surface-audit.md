# PizzaWave API Surface Audit

Last updated: 2026-07-09

## Current Result

The active API was reviewed for operator value, runtime cost, write/delete risk,
and stale experiment surfaces.

Removed from the rig-side API:

- `POST /api/v1/calls/retry-transcription-errors`
  - Removed because retrying transcription errors can flood constrained rigs and
    shared GPU resources.
- `POST /api/v1/incidents/generate`
- `POST /api/v1/incidents/rebuild`
  - Removed because manual/backfill incident generation is deprecated. Live
    incident creation is forward-looking through the automatic RAG/LLM pipeline.
- `POST /api/v1/system/recommendations/{id}/apply`
  - Removed because recommendation cards should not directly mutate runtime
    policy. Recommendations now point operators to the relevant UI.
- `POST /api/v1/talkgroups/catalog/generate-tr-csv`
  - Removed because Setup > Talkgroups > Apply is the single catalog mutation
    path and already regenerates the TR CSV when needed.
- old migration and restore-under-setup entry points
  - Removed because Reset is now owned by `System > Backup > Reset`, restore is
    staged/applied from `System > Backup`, and Setup owns site state.

Removed associated stale code:

- `SummaryService`
- transcription-error retry queue method
- deprecated request DTOs
- frontend recommendation auto-apply plumbing

## Setup And Reset Boundaries

First-run is intentionally narrow. It prepares host prerequisites:

- trunk-recorder install/reuse;
- optional LM Link support;
- optional native Qdrant support.

Setup owns site/location state, RadioReference site/TG import, RF path evidence,
waterfall, P25 ID, RF sweep, source planning, TR config draft/apply, and Setup
activity. Entering Setup is read/browse safe; disruptive RF/apply steps are
write-auth guarded and bounded.

Reset is owned by `System > Backup > Reset`. The reset API can create a backup
first, can preserve Data Only audit history, and clears first-run/setup/site
state only according to selected presets.

## Still Kept And Guarded

Kept because these remain operator-facing and guarded by write auth:

- setup wizard endpoints;
- service restart endpoints;
- live ingest pause/resume;
- storage maintenance actions;
- settings save/test/model management;
- setup talkgroup import and system-level TR policy/category Apply path;
- profile-local talkgroup hide/show Apply path;
- scoped talkgroup catalog policy endpoint used by system recommendations;
- job delete/control for supported job types.

Also kept/added as read-only operational surfaces:

- `GET /api/v1/system/quality-check`
  - Returns a bounded local telemetry snapshot for cross-rig monitoring without
    requiring direct SQLite access from automations.

## Follow-Up

The remaining audit item is a clean-rig field run, not an unknown API design
task. During that run, verify that host-prerequisite jobs are bounded, write-auth
guarded, and fail safely:

- trunk-recorder build/reuse;
- LM Link install/support without accidental local chat-model load;
- native Qdrant install, including the Raspberry Pi 16 KB page-size path;
- first-run finish into Setup;
- Reset plus backup/restore recovery back to `System > Backup`.

Do not reintroduce historical import/backfill, broad transcription retry, or
standalone RF mutation scripts as web APIs.
