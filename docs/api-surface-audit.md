# PizzaWave API Surface Audit

Last updated: 2026-05-21

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
  - Removed because Settings > Talkgroups > Apply is the single catalog mutation
    path and already regenerates the TR CSV when needed.

Removed associated stale code:

- `SummaryService`
- transcription-error retry queue method
- deprecated request DTOs
- frontend recommendation auto-apply plumbing

## Still Kept

Kept because these remain operator-facing and guarded by write auth:

- setup wizard endpoints;
- service restart endpoints;
- live ingest pause/resume;
- storage maintenance actions;
- settings save/test/model management;
- talkgroup catalog/profile Apply path;
- job delete/control for supported job types.

Also kept/added as read-only operational surfaces:

- `GET /api/v1/system/quality-check`
  - Returns a bounded local telemetry snapshot for cross-rig monitoring without
    requiring direct SQLite access from automations.

## Follow-Up

Continue auditing setup and calibration endpoints after the clean installer test.
Those endpoints are intentionally powerful but should remain setup-scoped and
clearly guarded.
