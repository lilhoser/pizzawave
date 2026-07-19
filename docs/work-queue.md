# PizzaWave Work Queue

Last reconciled: 2026-07-19 19:00 EDT

This is the single queue for PizzaWave implementation and deployment work.
Only one item may be `Active` at a time. Investigation sessions may work
read-only, but must not deploy.

## Current Deployment State

| Host | PizzaWave source | Backend hash | Web hash | State |
| --- | --- | --- | --- | --- |
| RPI (`sdr1861`) | `main` at `99adf8a` | `96594b14...` | `23672a49...` | Healthy |
| OT (`omicrontheta`) | `09bffda` | `097c740a...` | `62e684c5...` | Healthy, eight commits behind `main` |

The hosts do **not** currently run the same PizzaWave build. Neither host runs
the experimental Trunk Recorder retune-grace binary.

## Active

- OT-only validation of passive RF telemetry before deployment elsewhere:
  - Trunk Recorder `codex/rf-telemetry` at `8318dfb` emits retune events;
  - callstream `codex/rf-telemetry` at `1cdd5c4` emits periodic samples and
    reacquisition events;
  - PizzaWave config generation enables the 15-second sample stream.
  Both C++ objects compile on OT in an isolated temporary tree and all 529
  PizzaWave tests pass. Nothing from this item has been deployed.

## Pending

1. RF telemetry after OT validation:
   - implement PizzaWave ingestion/storage;
   - add analysis and operator presentation;
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
