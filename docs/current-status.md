# PizzaWave Current Status

Last consolidated: 2026-07-27

Production OT and RPI are healthy and use the current production incident
pipeline. All retired incident replacement, shadow, canary, and ownership paths
are disabled. No incident training experiment owns production data.

## Active priorities

1. Build the local incident membership model from
   [`incident-model-training-handoff.md`](incident-model-training-handoff.md).
   The settled product and runtime contract is
   [`incident-pipeline-architecture.md`](incident-pipeline-architecture.md).
2. Continue RF stabilization from the active run books under
   [`field-tests`](field-tests/).
3. Implement the separately accepted Offline and Archive Calls workspace.

The incident project is no longer organized by numbered pipeline versions.
Historical trials, failure evidence, and old deployment state are archived under
[`archive/incident-pipeline-2026-07`](archive/incident-pipeline-2026-07/README.md).
Do not revive an archived design merely because its code or experiment name is
present in Git history.

## Repository and deployment rules

- Start new work from local `main` in a task-specific worktree.
- Keep OT and RPI production data unchanged during development and replay.
- Use non-mutating replay and shadow evaluation before changing incident
  ownership.
- Do not restart Trunk Recorder as part of a PizzaWave deploy.
- Verify the live manifest and `/api/v1/health` after any deployment.

The previous long-form project status is preserved at
[`archive/current-status-through-2026-07-20.md`](archive/current-status-through-2026-07-20.md).
