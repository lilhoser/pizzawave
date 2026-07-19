# PizzaWave Work Queue

Last reconciled: 2026-07-19 19:39 EDT

This is the single queue for PizzaWave implementation and deployment work.
Only one item may be `Active` at a time. Investigation sessions may work
read-only, but must not deploy.

## Current Deployment State

| Host | PizzaWave source | Backend hash | Web hash | State |
| --- | --- | --- | --- | --- |
| RPI (`sdr1861`) | `main` at `99adf8a` | `96594b14...` | `23672a49...` | Healthy |
| OT (`omicrontheta`) | RF ingestion candidate `26625f8` | `ecdf7a0b...` | `23672a49...` | Healthy; RF emission and persistence validated |

The hosts now share the same web build, but RPI's PizzaWave backend remains at
the earlier `99adf8a` state. Neither host runs the experimental Trunk Recorder
retune-grace binary. Only OT runs the passive RF telemetry build.

## Active

- RF telemetry analysis and operator presentation:
  - Trunk Recorder `codex/rf-telemetry` at `8318dfb` emits retune events;
  - callstream `codex/rf-telemetry` at `1cdd5c4` emits periodic samples and
    reacquisition events;
  - PizzaWave config generation enables the 15-second sample stream.
  The matching artifacts were deployed to OT at 2026-07-19 19:13 EDT. All
  three configured systems emit valid samples at exact 15-second intervals;
  TR, callstream, PizzaWave ingest, and transcription remained healthy. No
  Hamilton naturally produced four retunes followed by reacquisition at
  2026-07-19 19:29 EDT. Structured events matched the human-readable log and
  retained the complete channel/source sequence. PizzaWave schema-v1 parsing,
  deduplicated storage, bounded retention, migration, and authenticated query
  endpoint are validated on OT; all 531 tests pass. Add analysis and operator
  presentation before considering RPI promotion.

## Pending

1. RF telemetry after analysis/operator presentation:
   - promote the tested build to RPI.
2. Harden and test Trunk Recorder retune grace, then propose it upstream.
3. Add supervised Setup validation of every alternate control channel.
4. Repeat the controlled OT source-centering experiment.
5. Package 7: isolated Offline and Archive Calls workspaces.
6. Incident pipeline redesign, using its dedicated handoff and experimental
   branches. It must not be mixed into RF or operational packages.

## Awaiting Disposition

- Preserve, merge, or retire the incident-v3, transcription bakeoff, embedding,
  and old platform-refactor branches after their owners review them.
- Review and retire obsolete local branch names after confirming no session
  still refers to them. Their worktrees have already been removed.

## Completion Rule

Each implementation item ends with one sequence:

1. Tests and production web build pass.
2. Changes are committed and reviewed.
3. The focused branch is squash-merged to `main` and pushed.
4. Deployment runs from the reviewed `main` state, except for an explicitly
   requested OT experiment.
5. Host, commit, hashes, time, and verification result are recorded here.
6. The task worktree is removed after its branch is safely retained.
