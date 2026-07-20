# 2026-07-20 OT and RPI source-centering A-B-A

Date: 2026-07-20 (America/New_York)

## Question

The July 18 diagnosis showed that direct control-channel tuning could recover
decoder margin during some degraded intervals. The first sequential
source-center A/B on July 19 was inconclusive because RF conditions changed
between its baseline and candidate windows. This follow-up tested whether
moving the active source centers nearer the control channels produces a
repeatable improvement when followed by a restored-center confirmation.

The operator noted before the test that Hamilton was already centered on its
primary control channel and still degraded during the same broad event. The
experiment therefore treated Hamilton and Cleveland as unchanged controls and
did not assume that centering could be a complete fix.

## Result

Do not adopt the candidate source centers as a stabilization fix.

The candidate did not prevent transient fades. Raymond was worse during the
centered phase and was strongest after its original center was restored. North
Bradley's candidate-window average was higher, but Hamilton improved by a
similar amount without any center change. The broad recovery across unchanged
controls makes the North Bradley improvement non-causal in this sequential
test.

The result does not prove that source placement has no effect on decoder
margin. It shows that placement is neither sufficient to explain the observed
degradation nor supported as a permanent, catch-all correction.

## Configuration and safety controls

Only source center frequencies changed in phase B. Gains, device assignments,
frequency corrections, sample rates, channel lists, and
`controlRetuneLimit: 0` were unchanged. No retune-grace binary was installed.

| Host | Source | Original center | Candidate center |
| --- | ---: | ---: | ---: |
| OT | 0 | 770.515625 MHz | 769.912500 MHz |
| OT | 1 | 772.918750 MHz | 772.443750 MHz |
| RPI | 0 | 771.931250 MHz | 772.831250 MHz |

Original and candidate configs were staged before the run. Candidate install
and final restoration each used one controlled Trunk Recorder restart per
host. Automatic rollback timers were armed before candidate installation and
stopped only after manual restoration was verified.

Final active config hashes matched the saved originals:

- OT: `788a0a2eb76f26248463ffff09636d583a2a40c2a7c4d7c0313b618c06b57f01`
- RPI: `6ffff8692cfbc6b1d1ac385fe6e1879c319df2026d97b41f86967401040dc185`

Both `trunk-recorder` services finished active. Both PizzaWave health checks
returned `ok`, with no queue pressure.

## Fixed windows

| Phase | Configuration | Unix start | Unix end | Local time |
| --- | --- | ---: | ---: | --- |
| A1 | Original centers | 1784549940 | 1784551762 | 08:19:00-08:49:22 EDT |
| B | Candidate centers | 1784551822 | 1784553622 | 08:50:22-09:20:22 EDT |
| A2 | Restored original centers | 1784553720 | 1784555520 | 09:22:00-09:52:00 EDT |

A1 was extended by 22 seconds so its end aligned with the already established
boundary. B and A2 were exact 1,800-second windows. The final OT RF samples
became queryable after the normal five-minute collector commit at 09:57 EDT;
they were not missing.

## Persisted RF sample results

The table uses `rf_sample` rows from the exact windows. Decode is the average
of all stored per-system samples. Zero is the percentage of those samples with
zero decoded messages. Retune rows are the retained structured transition
rows, which are deduplicated evidence and not the exact TR health retune
counter.

| System | Phase | Samples | Avg decode | Zero samples | Retained retune rows |
| --- | --- | ---: | ---: | ---: | ---: |
| OT North Bradley | A1 | 122 | 20.23 msg/s | 1.6% | 9 |
| OT North Bradley | B | 120 | 28.12 msg/s | 2.5% | 6 |
| OT North Bradley | A2 | 120 | 21.11 msg/s | 2.5% | 9 |
| OT Hamilton | A1 | 122 | 9.15 msg/s | 5.7% | 20 |
| OT Hamilton | B | 120 | 16.09 msg/s | 0.8% | 4 |
| OT Hamilton | A2 | 120 | 23.41 msg/s | 0.8% | 4 |
| OT Cleveland | A1 | 122 | 39.47 msg/s | 0.8% | 2 |
| OT Cleveland | B | 120 | 39.75 msg/s | 0.0% | 0 |
| OT Cleveland | A2 | 120 | 36.49 msg/s | 0.0% | 0 |
| RPI Raymond | A1 | 122 | 20.33 msg/s | 13.9% | 16 |
| RPI Raymond | B | 121 | 18.50 msg/s | 19.0% | 20 |
| RPI Raymond | A2 | 120 | 38.31 msg/s | 0.0% | 0 |

Primary-channel-only results support the same interpretation:

- North Bradley primary 769.606250 MHz: 20.23, 28.60, and 21.65 msg/s in
  A1, B, and A2. Primary zero rates were 1.6%, 0.8%, and 0.0%.
- Hamilton primary 855.212500 MHz, with no center change: 9.88, 16.23, and
  23.61 msg/s. Its primary zero rate was 0.0% in every phase.
- Raymond primary 773.781250 MHz: 23.18, 21.53, and 38.31 msg/s. Primary zero
  rates were 1.9%, 5.8%, and 0.0%.

## Within-window behavior

The fixed averages hide short fades that matter operationally:

- During B, North Bradley's rolling live decode fell to about 6.3 msg/s with
  30% zero samples and elevated retuning before recovering above 30 msg/s.
- During B, Raymond later fell to about 11.5 msg/s with 39% zero samples and
  elevated retuning before partially recovering.
- Hamilton improved during B and continued improving during A2 even though its
  center never changed.
- During A2, Raymond remained continuously decodable on its original,
  substantially off-center source and finished near 38-40 msg/s.

These observations rule out a simple claim that moving the center prevents
the failure mode.

## Interpretation

1. Raymond provides a negative result for the candidate: its centered phase
   was worse than A1, and its restored original center was best.
2. North Bradley's B average cannot be assigned to centering because unchanged
   Hamilton improved concurrently and B still contained a pronounced fade.
3. Hamilton can retune and degrade while exactly centered, so center placement
   cannot own the whole failure class.
4. Both target systems operated cleanly on their original centers after the
   broad recovery. The original offset is therefore not independently
   sufficient to cause the outage.
5. Source placement can remain a secondary margin optimization, but it should
   not displace investigation of the RF path, individual receiver, and live DSP
   or decoder state.

## Recommended next experiment

Run a simultaneous OT North Bradley receiver-role crossover during an active
fade. This avoids another time-confounded sequential center comparison and
requires no historical control-channel scoring machinery.

Use the two receivers from the July 18 paired observation. Keep their existing
antenna and distribution arrangement fixed, and record whether they use a
common distribution device or separate feed legs:

1. Confirm North Bradley's primary 769.606250 MHz is in a sustained low-decode
   interval on the normal live source.
2. For 10 minutes, keep the normal wideband source on its current physical SDR
   and run the second SDR as a direct-centered reference on the same primary.
3. Swap the wideband and direct-centered roles between the two physical SDRs,
   retaining each device's own correction and gain, and observe for 10 minutes.
4. Restore the original roles for a final 10 minutes.
5. Record per-receiver primary decode, zero intervals, source/device identity,
   frequency residual, and any USB or DSP warnings. Do not change alternate
   channel lists or retune behavior during the crossover.

Interpret the crossover by role and device:

- If the direct-centered role wins on both physical SDRs, source geometry is a
  real margin factor, though still not a complete fix.
- If one SDR/feed leg wins in both roles, investigate that receiver, cable,
  port, correction, or front end.
- If both receivers degrade together, favor a common RF-path, propagation, or
  simulcast condition.
- If captured IQ remains decodable in a fresh decoder while only the live
  decoder fails, prioritize live DSP/reacquisition state.

This is the next experiment because it separates the main remaining causes in
one contemporaneous observation. A gain or attenuation challenge should
follow only if the crossover points to the receiver/front end.

## Artifacts

The complete live artifacts remain on both measured hosts at:

`/var/lib/pizzawave/rf-surveys/manual/20260720-active-degradation-source-centering`

Each directory contains the original and candidate configs, exact phase
boundary files, and `result.txt`.
