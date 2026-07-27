# Incident V2 Rollout Readiness

Date: 2026-06-19

## Decision

Incident v2 is ready for a read-only shadow rollout.

Incident v2 is not ready to replace live incident persistence yet.

## Evidence

- Unit tests: 198 passed, 0 failed.
- Labeled replay: 72 passed, 0 failed.
- Larger fresh replay: 250 decisions, 0 model/replay errors.
- Larger fresh replay had 0 rows where v1 accepted and v2 rejected.
- Larger fresh replay had 74 rows where v1 rejected and v2 accepted, across 40
  unique accepted call sets.

Manual review of the v2-only accepted groups found mostly concrete incidents
that the current path missed: crashes, fire alarms, medical dispatches, shots
fired, stolen firearm, burglary alarm, gas/CO, welfare, and animal calls.

Two false-positive classes were found and fixed:

- mixed medical, police, and traffic candidate windows;
- standalone lift-assist calls without a parent emergency.

## Rollout Gate

The next rollout should be read-only shadow telemetry only:

- enable `AiInsights.IncidentV2ShadowEnabled` only when ready to sample live
  traffic;
- run v2 alongside the current path;
- do not write v2 incidents;
- log v1/v2 decision, accepted call IDs, rejected call IDs, title, category,
  reasons, and conflicts;
- sample v2-only accepts and v2-only rejects daily;
- block live cutover if v2 creates routine/assist/transport incidents, drops
  current valid incidents, or produces materially worse memberships.

Live persistence replacement should require at least one clean live-shadow
period after this replay evidence.

## Latest Artifacts

- Labeled score:
  `artifacts/incident-replay-corpus/incident-shadow-label-score-20260619T213932Z.json`
- 250-case fresh replay report:
  `artifacts/incident-replay-corpus/incident-shadow-report-20260619T213927Z.json`
- 250-case fresh model decisions:
  `artifacts/incident-replay-corpus/incident-v2-decisions-20260619T201719Z.json`
- 250-case deterministic re-decision:
  `artifacts/incident-replay-corpus/incident-v2-decisions-20260619T213855Z.json`
