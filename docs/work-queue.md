# PizzaWave Work Queue

Last reconciled: 2026-07-21 11:02 EDT

This is the single queue for PizzaWave implementation and deployment work.
Only one item may be `Active` at a time. Investigation sessions may work
read-only, but must not deploy.

## Current Deployment State

| Host | PizzaWave source | Backend hash | Web hash | State |
| --- | --- | --- | --- | --- |
| RPI (`sdr1861`) | `main` at `ec5572f` | `4a6b67d8...` | `e05e0275...` | Healthy; wide experiment removed, pre-wide narrow recorder and shadow restored |
| OT (`omicrontheta`) | `main` at `ec5572f` | `4a6b67d8...` | `e05e0275...` | Healthy; wide experiment removed, pre-wide narrow recorders and shadows restored |

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
  work remains isolated on `codex/initial-collapse-capture-live` at `313d247`
  for maintainer review and possible upstream submission;
- the exact older RPI compatibility candidate remains on
  `codex/initial-collapse-capture-rpi` at `393a0732`;
- the retune-grace experiment remains separately isolated on
  `codex/rf-stabilization` at `602a637`.

## Active

- Initial-collapse flight recording and paired-wide analysis are complete. RPI
  captured a shadow-instrumented
  event immediately after activation: both decoders collapsed together at
  onset, but the fixed-primary shadow recovered to 22-36 frames/s while the
  live decoder cycled alternates. OT subsequently captured North Bradley: both
  decoders again collapsed together at onset, but recovery diverged in the
  opposite direction, with live recovering to 36 while shadow remained at 1.
  Hamilton stayed healthy throughout. A paired 800 kHz OT capture now shows
  North Bradley's collapse was channel selective: outer-band power was stable
  within 0.14 dB while control-channel energy tracked recovery. A subsequent
  centered Hamilton pair showed the inverse amplitude behavior: control-channel
  energy rose about 2 dB into shared decoder failure while the outer spectrum
  stayed stable. Together these favor channel-local modulation destruction
  from dynamic simulcast/multipath over a simple fade. OT's two-system wide
  instrumentation also induced repeated Gardner/source-stall exits and was
  removed at 22:34 EDT. RPI then captured the same channel-local signature:
  decode collapsed with only 0.13 dB raw wide-power, 0.24 dB
  control-channel/outer-band, and 0.16 dB outer-floor differences. The common
  failure is modulation quality, with dynamic simulcast/multipath the leading
  cause and exact co-channel interference the remaining alternative. Both
  hosts have been restored to their pre-wide instrumentation. A repeatable
  offline IQ classifier now reproduces Raymond's channel-local signature. A
  new natural Raymond event at 08:11 EDT enabled an immediate gain-recovery
  test: an unchanged restart and LNA gain reductions from 15 to 12 and 9 all
  failed to restore normal decode. A separate prevention test then started
  gain 12 from a strong 13-26 frame/s baseline. Decode immediately fell to
  2-4 frame/s, cycled alternates, spent long intervals at zero, and reached
  only 7 frame/s at the five-minute boundary. Gain 15 was restored and the new
  process immediately sustained 9-30 frame/s on the primary. Gain 12 is too
  aggressive for this site. Gain 14 then passed a strong 11-32 frame/s healthy
  gate but admitted another natural collapse about 17 minutes later. That
  event reproduced shared live/shadow failure with only a 0.02 dB onset-power
  change; live spent 56 post-trigger samples at 0-1 frame/s and never sustained
  three samples at or above 10 in the retained minute. Gain 15 is restored and
  verified at 26-40 frame/s. Gain reduction did not prevent or weaken the
  collapse, so gain testing is complete. The retained-IQ demodulator comparison
  is now complete. Across three captures and three runs each, the existing FSK4
  control decoder beat stock CQPSK before and after every trigger while always
  decoding the correct site identity. Halving the CQPSK Gardner timing gain
  also improved every capture, but still trailed FSK4 substantially. This
  establishes a decoder-tolerance contributor on top of the real channel-local
  RF impairment. The next bounded test is control-channel-only FSK4 on RPI;
  the system-level modulation setting cannot be used because it would also
  switch traffic recorders and reject Phase 2 calls. The isolated TR candidate
  now provides that separation and passed all three retained-IQ replays with
  traffic QPSK/control FSK4 logged independently and FSK4 yields reproduced.
  A strong-baseline live RPI gate is next. OT uses
  RTL-SDR receivers behind an MCA208M while RPI uses Airspy receivers without
  that multicoupler;
  this hardware difference further weakens a single receiver/front-end-overload
  explanation. See
  [field-tests/2026-07-20-initial-collapse-flight-recorder.md](field-tests/2026-07-20-initial-collapse-flight-recorder.md).
  The replay procedure and exact results are in
  [field-tests/2026-07-21-p25-control-demodulator-replay.md](field-tests/2026-07-21-p25-control-demodulator-replay.md).
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
   - keep current-lineage and exact RPI compatibility candidates isolated for
     maintainer review; their paired-wide branches are no longer deployed;
   - retain the completed same-IQ demodulator result: FSK4 beat stock and
     half-timing CQPSK on all three Raymond captures, including every healthy
     pre-trigger portion and every collapsed post-trigger portion;
   - run one control-channel-only FSK4 live confirmation on RPI while preserving
     QPSK traffic recording and explicitly gating Phase 2 recording. Do not use
     the system-level `modulation: fsk4` setting, add antennas, or add another
     live wide recorder;
   - if FSK4 does not materially reduce the next natural collapse, stop decoder
     tuning and test the available BPF-800-M on RPI as the next hardware
     discriminator;
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
