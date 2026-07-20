# PizzaWave Work Queue

Last reconciled: 2026-07-19 20:43 EDT

This is the single queue for PizzaWave implementation and deployment work.
Only one item may be `Active` at a time. Investigation sessions may work
read-only, but must not deploy.

## Current Deployment State

| Host | PizzaWave source | Backend hash | Web hash | State |
| --- | --- | --- | --- | --- |
| RPI (`sdr1861`) | `main` at `71663cc` | `0f2d33d0...` | `a7b32390...` | Healthy; passive RF emitters active |
| OT (`omicrontheta`) | `main` at `71663cc` | `0f2d33d0...` | `a7b32390...` | Healthy; RF presentation and Recommendations validated |

The hosts share the same PizzaWave deployable build. Neither host runs the
experimental Trunk Recorder retune-grace binary. Both hosts run the passive RF
telemetry emitters; RPI's TR emitter is based on its exact prior `766a553`
revision rather than the newer upstream base used by OT.

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
  endpoint are validated on OT. The RF page now presents adaptive decode and
  frequency-error charts plus a causal control-channel transition table for
  2-hour through 7-day windows. Recommendation cards reuse the same current
  site assessment, combine related RF symptoms, retain typed transition
  evidence, and move aged-out patterns into Finding History. OT is healthy,
  the 10-inch layout retains all controls and content, and all 534 tests pass.
  The reviewed candidate was squash-merged to `main` as `71663cc`, pushed, and
  deployed to both hosts. RPI's passive emitters were installed at 20:42 EDT
  from TR `ca787409` and callstream `1cdd5c4`, with rollback artifacts under
  `/var/backups/pizzawave/rf-telemetry-rpi-20260720T004203Z`.

## Pending

1. Harden and test Trunk Recorder retune grace, then propose it upstream.
2. Add supervised Setup validation of every alternate control channel.
3. Repeat the controlled OT source-centering experiment.
4. Package 7: isolated Offline and Archive Calls workspaces.
5. Incident pipeline redesign, using its dedicated handoff and experimental
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
