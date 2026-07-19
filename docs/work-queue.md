# PizzaWave Work Queue

Last reconciled: 2026-07-19 16:35 EDT

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

- Reconcile source control and deployment ownership.
  - Local `main` through `9299a28` is published to `origin/main`.
  - Merged and superseded package worktrees have been removed. The canonical
    `main` and isolated incident-v3 worktrees remain.
  - Add deployment locking and record the source commit in deployment manifests.
  - Bring OT and RPI to the same reviewed `main` build only after deployment
    ownership is established.

## Pending

1. RF telemetry, delivered in bounded stages:
   - define the passive Trunk Recorder event contract;
   - implement and test TR event emission;
   - deploy to OT only and validate;
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
4. Deployment runs from `main` under the deployment lock.
5. Host, commit, hashes, time, and verification result are recorded here.
6. The task worktree is removed after its branch is safely retained.
