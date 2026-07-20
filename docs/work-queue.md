# PizzaWave Work Queue

Last reconciled: 2026-07-20 10:05 EDT

This is the single queue for PizzaWave implementation and deployment work.
Only one item may be `Active` at a time. Investigation sessions may work
read-only, but must not deploy.

## Current Deployment State

| Host | PizzaWave source | Backend hash | Web hash | State |
| --- | --- | --- | --- | --- |
| RPI (`sdr1861`) | `main` at `ec5572f` | `4a6b67d8...` | `e05e0275...` | Healthy; passive RF emitters active; live RF controls verified |
| OT (`omicrontheta`) | `main` at `ec5572f` | `4a6b67d8...` | `e05e0275...` | Healthy; Cleveland retune-only state verified as degraded, not critical |

The hosts share the same PizzaWave deployable build. Neither host runs the
experimental Trunk Recorder retune-grace binary. Both hosts run the passive RF
telemetry emitters; RPI's TR emitter is based on its exact prior `766a553`
revision rather than the newer upstream base used by OT.

Cross-repository source state:

- callstream RF sampling/reacquisition telemetry is merged and pushed on
  callstream `main` at `1cdd5c4`; its temporary feature branch is retired;
- upstream Trunk Recorder `master` remains untouched at `382f5f2`;
- the Trunk Recorder telemetry and retune-grace candidates are consolidated in
  one clean local branch/worktree, `codex/rf-stabilization` at `602a637` under
  `C:\projects\trunk-recorder-rf-stabilization`. It is intentionally not merged
  into upstream `master` or deployed; hardening and upstream/fork disposition
  belong to the next RF task.

## Active

- None. RF stabilization is the next implementation priority. The incident
  pipeline redesign remains independently owned by its existing session and
  must not be merged or deployed as part of RF work.

## Recently Completed

- The supervised source-centering A-B-A on OT and RPI is complete and recorded
  in
  [field-tests/2026-07-20-source-centering-aba.md](field-tests/2026-07-20-source-centering-aba.md).
  Candidate centers were installed for one exact 30-minute phase, followed by
  an exact 30-minute restored-center confirmation. Raymond worsened under the
  candidate and was strongest after restoration. North Bradley's higher
  candidate average coincided with improvement on unchanged Hamilton and the
  candidate did not prevent short fades. Original configs were restored and
  hash-verified; both TR services and PizzaWave health checks finished clean.
- Thread source-control reconciliation confirmed that all Package 5-11,
  System, recovery, temporal-analysis, and RF work from this thread is present
  on `main`. Merged, patch-equivalent, or explicitly superseded local package
  branches and fully merged remote branches were retired. The
  `codex/incident-v3-analysis` worktree and the unique incident, transcription,
  embedding, and platform branches remain isolated for their respective owners.
- Cross-repository cleanup merged and pushed the deployed callstream telemetry
  to callstream `main`, then consolidated three experimental Trunk Recorder
  branches/two redundant worktrees into the single `codex/rf-stabilization`
  branch described above.
- Live RF status follow-up at `ec5572f`:
  - removed the two explanatory prose notes above the RF charts;
  - added a persistent per-browser site pin to the footer RF pill;
  - repaired popup site navigation so Performance / Radio Frequency opens on
    the selected site;
  - prevented elevated retunes alone from making a currently decoding site
    critical; they remain visible as degraded evidence;
  - rebuilt the production web assets, passed all 536 tests, pushed `main`, and
    deployed the identical backend/web hashes to OT and RPI.

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
  the 10-inch layout retains all controls and content, and all 535 tests pass.
  The reviewed candidate was squash-merged to `main` as `71663cc`, pushed, and
  deployed to both hosts. RPI's passive emitters were installed at 20:42 EDT
  from TR `ca787409` and callstream `1cdd5c4`, with rollback artifacts under
  `/var/backups/pizzawave/rf-telemetry-rpi-20260720T004203Z`.
  Repetitive low-decode loops retain one representative of each distinct
  channel transition per five-minute bucket, preventing a disconnected source
  from crowding another site's narrative or growing storage without bound.
  Existing TR health counters continue to retain the exact retune totals.

## Pending

1. RF stabilization:
   - run the simultaneous OT North Bradley receiver-role crossover documented
     in the July 20 field test;
   - use its role-versus-device result to choose either a short
     gain/attenuation challenge or live DSP/reacquisition investigation;
   - keep alternate-channel validation and Trunk Recorder retune grace as
     secondary recovery work, not as the presumed root-cause fix.
2. Incident pipeline redesign, using its dedicated handoff, worktree, and
   experimental branches. Another session owns this work; it must not be mixed
   into RF stabilization.
3. Package 7: isolated Offline and Archive Calls workspaces.

## Awaiting Disposition

- Preserve or retire the incident-v3, transcription bakeoff, embedding, and old
  platform-refactor branches after their owners review them. They are not work
  from the RF/System/package thread and were deliberately not merged here.

## Completion Rule

Each implementation item ends with one sequence:

1. Tests and production web build pass.
2. Changes are committed and reviewed.
3. The focused branch is squash-merged to `main` and pushed.
4. Deployment runs from the reviewed `main` state, except for an explicitly
   requested OT experiment.
5. Host, commit, hashes, time, and verification result are recorded here.
6. The task worktree is removed after its branch is safely retained.
