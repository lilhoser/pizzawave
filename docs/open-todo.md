# PizzaWave Open TODO

Last consolidated: 2026-07-20

The active cross-package queue and deployment baseline are maintained in
[work-queue.md](work-queue.md). Repository and worktree detail from the latest
reconciliation is in
[archived reconciliation audit](archive/reconciliation-audit-2026-07-19.md).

The active operator-facing remediation program is tracked in
[operator-ux-remediation.md](operator-ux-remediation.md).

The operator-remediation sequence is closed. Package 11 was implemented,
tested, deployed, live-verified, and operator-accepted on 2026-07-19.

Current priority order:

1. RF stabilization: the source-centering A-B-A is complete and did not support
   permanent candidate centers. Next run the simultaneous OT North Bradley
   receiver-role crossover from
   [field-tests/2026-07-20-source-centering-aba.md](field-tests/2026-07-20-source-centering-aba.md),
   then choose a receiver/front-end or live DSP follow-up from that result.
   Alternate-channel validation and retune grace remain secondary recovery
   work.
2. Build the local incident membership model from
   [incident-model-training-handoff.md](incident-model-training-handoff.md).
   Keep Qwen 3.6 35B-A3B for grounded presentation work as specified in
   [incident-pipeline-architecture.md](incident-pipeline-architecture.md).
3. Package 7 isolated Offline and Archive Calls workspaces.

Standalone outstanding feature after those higher priorities:

- Package 7 - implement isolated Offline and Archive Calls workspaces from the
  accepted design. The persistence/timing foundation is complete but not
  deployed; the support-package creator, validator, isolated store, Workspace
  Library, Summary UI, processing scheduler, and SFTP workflow remain undone.
