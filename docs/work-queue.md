# PizzaWave Work Queue

Last reconciled: 2026-07-20 19:10 EDT

This is the single queue for PizzaWave implementation and deployment work.
Only one item may be `Active` at a time. Investigation sessions may work
read-only, but must not deploy.

## Current Deployment State

| Host | PizzaWave source | Backend hash | Web hash | State |
| --- | --- | --- | --- | --- |
| RPI (`sdr1861`) | `main` at `ec5572f` | `4a6b67d8...` | `e05e0275...` | Healthy; Raymond flight recorder and fixed-primary shadow active |
| OT (`omicrontheta`) | `main` at `ec5572f` | `4a6b67d8...` | `e05e0275...` | Healthy; North Bradley/Hamilton flight recorders and shadows active |

The hosts share the same PizzaWave deployable build. Neither host runs the
experimental Trunk Recorder retune-grace binary. Both hosts run the passive RF
telemetry emitters, bounded initial-collapse flight recorder, and passive
fixed-primary shadow decoder described below; RPI's TR build remains based on
its exact prior `766a553` lineage rather than the newer upstream base used by
OT.

Cross-repository source state:

- callstream RF sampling/reacquisition telemetry is merged and pushed on
  callstream `main` at `1cdd5c4`; its temporary feature branch is retired;
- local and remote Trunk Recorder `master` remain aligned and untouched at
  `382f5f2`; current-lineage telemetry, flight-recorder, and passive-shadow
  work remains isolated on `codex/initial-collapse-capture-live` at `51920b1`
  for maintainer review and possible upstream submission;
- the exact older RPI compatibility candidate remains on
  `codex/initial-collapse-capture-rpi` at `c923e02c`;
- the retune-grace experiment remains separately isolated on
  `codex/rf-stabilization` at `602a637`.

## Active

- Initial-collapse flight recording and passive shadow decoding are active on
  RPI Raymond and OT North Bradley/Hamilton. RPI captured a shadow-instrumented
  event immediately after activation: both decoders collapsed together at
  onset, but the fixed-primary shadow recovered to 22-36 frames/s while the
  live decoder cycled alternates. OT subsequently captured North Bradley: both
  decoders again collapsed together at onset, but recovery diverged in the
  opposite direction, with live recovering to 36 while shadow remained at 1.
  Hamilton stayed healthy throughout. See
  [field-tests/2026-07-20-initial-collapse-flight-recorder.md](field-tests/2026-07-20-initial-collapse-flight-recorder.md).
  The incident pipeline redesign remains independently owned by its existing
  session and must not be merged or deployed as part of RF work.

## Recently Completed

- The first natural initial-collapse IQ capture is complete on RPI Raymond.
  Live decode reached 0 msg/s on the unchanged 773.781250 MHz primary while a
  fresh replay of the exact trigger-aligned samples fell only to 8 msg/s and
  immediately recovered. IQ power fell approximately 3.45 dB at the same edge
  while spectral centroid remained stable. This establishes a real modest RF
  dip amplified into a full live-decoder collapse; it rejects both total RF
  loss and a pure counter artifact. The recorder remains active to obtain OT
  evidence.
- Deterministic P25 retune-state replay is complete and recorded in
  [field-tests/2026-07-20-p25-retune-state-replay.md](field-tests/2026-07-20-p25-retune-state-replay.md).
  Five fixed-sample runs per decoder variant forced three noise-only Raymond
  channel visits before returning to the captured primary. Both persistent
  and fully reconstructed P25 graphs decoded the correct site and system in
  5/5 runs. Mean primary rates were 20.00 and 19.65 msg/s, respectively, with
  effectively identical acquisition latency. Full graph reset is not a
  supported stabilization fix and was not deployed. Durable configs, logs,
  binaries, hashes, source, and machine-readable analysis remain on RPI under
  `/var/lib/pizzawave/rf-surveys/manual/20260720T-rpi-raymond-demod-state-replay`.
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
   - retain OT North Bradley capture `1784584105012` as the completed cross-
     geography discriminator: its IQ power/CNR changed only about 0.4/0.5 dB,
     sample continuity was clean, and a fresh replay reproduced the low decode;
     Hamilton remained healthy on the same host;
   - retain Raymond capture `1784582765019` and its 78-sample live/shadow
     timeline as the first result; isolated replay independently reproduced
     repeated decode losses in the captured IQ, while live alternate-channel
     hopping amplified the production outage after the primary recovered;
   - keep current-lineage candidate `51920b1` and exact RPI compatibility
     candidate `c923e02c` isolated for maintainer review even though their
     coherent artifacts are now deployed experimentally;
   - use one bounded wider pre-channelizer capture, plus offline modulation-
     quality analysis, to distinguish North Bradley in-channel distortion from
     front-end behavior; do not add broad host metrics or another recovery
     policy experiment;
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
