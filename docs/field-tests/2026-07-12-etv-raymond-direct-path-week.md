# 2026-07-12 ETV Raymond Direct-Path Week

Status: Complete

Purpose: determine whether bypassing the RF splitter provides stable ETV
Raymond control-channel reception over a full week and whether the prior
split-path result explains the observed RF failures.

## Study Definition

```text
Run: ETV-RAYMOND-DIRECT-PATH-WEEK-01
System: etv-raymond-hinds
RF path: Antenna direct to the ETV Airspy; splitter bypassed
Excluded path: Jackson's intentionally disconnected RF path
Started: 1783828800 / 2026-07-12T04:00:00Z / 2026-07-12 00:00:00 EDT
Ended:   1784465100 / 2026-07-19T12:45:00Z / 2026-07-19 08:45:00 EDT
Duration: 636300 seconds / 176.75 hours
```

The study was ended on Sunday morning instead of the originally planned
Sunday 9:00 PM endpoint because another experiment had begun. No evidence
after 8:45 AM EDT is included.

The monitor did not change configuration, restart services, alter the RF rig,
or mutate production data. PizzaWave application restarts performed by a
separate development session were not classified as RF or trunk-recorder
events unless they caused missing evidence. No sustained ingestion failure was
observed.

## Evidence And Method

The study used the PizzaWave API on the Raymond RPI:

```text
GET /api/v1/troubleshoot/tr-health?start=1783828800&end=1784465100
GET /api/v1/troubleshoot/rf-analysis?system=etv-raymond-hinds&start=1783828800&end=1784465100
GET /api/v1/health
```

Only `tr-health` rows with `scope=etv-raymond-hinds` were aggregated. Control
channel decode rate is the sum of `ccSummaryDecodeRateTotal` divided by the sum
of `ccSummaryDecodeLines`. Zero-decode percentage is the sum of
`ccSummaryDecodeZero` divided by the same line count. Tuning error is the
sample-weighted mean absolute error. No-transmission percentage uses concluded
calls as its denominator.

The dataset contains 2,120 of 2,121 expected five-minute Raymond buckets. One
bucket is missing on Thursday, July 16. This is 99.95% interval coverage.

## Executive Summary

The direct path produced excellent reception when conditions were favorable,
substantially outperforming the split-path baseline. It did not prevent severe
and prolonged control-channel failures.

Reception was bimodal: approximately 35-40 msg/sec during healthy periods,
then near-zero decode, extreme retuning, and elevated no-transmission outcomes
during bad periods. The splitter was completely bypassed throughout the study,
so splitter insertion loss is not a sufficient root-cause explanation.

The remaining evidence is consistent with a time-varying RF condition such as
propagation or multipath, interference or desense, antenna/feedline behavior,
or Airspy front-end conditions. The available telemetry cannot distinguish
among these categories because it lacks synchronized signal level, noise-floor,
spectrum, and IQ evidence.

## Daily Results

| Day | Decode | Zero decode | Retunes | Calls started/concluded | No transmission | Mean absolute tuning error |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Sun Jul 12 | 36.09 msg/sec | 4.20% | 936 | 5,142 / 5,141 | 234 / 4.6% | 592 Hz |
| Mon Jul 13 | 23.58 msg/sec | 29.07% | 8,508 | 5,414 / 5,415 | 338 / 6.2% | 599 Hz |
| Tue Jul 14 | 35.75 msg/sec | 3.49% | 912 | 7,142 / 7,139 | 370 / 5.2% | 587 Hz |
| Wed Jul 15 | 35.62 msg/sec | 3.49% | 1,320 | 6,367 / 6,370 | 388 / 6.1% | 584 Hz |
| Thu Jul 16 | 33.65 msg/sec | 5.61% | 1,912 | 5,436 / 5,441 | 350 / 6.4% | 592 Hz |
| Fri Jul 17 | 25.67 msg/sec | 23.95% | 7,300 | 4,167 / 4,169 | 384 / 9.2% | 581 Hz |
| Sat Jul 18 | 7.95 msg/sec | 59.15% | 18,712 | 3,222 / 3,210 | 490 / 15.3% | 595 Hz |
| Sun Jul 19, 00:00-08:45 | 0.08 msg/sec | 95.51% | 9,852 | 72 / 72 | 14 / 19.4% | 614 Hz |

Calls can cross bucket and day boundaries, so small started/concluded
differences are expected.

## Full-Study Aggregate

PizzaWave `rf-analysis` reported:

- Control-channel summary average: 26.95 msg/sec.
- Control-channel zero rate: 22.19%.
- Per-frequency message-rate samples: 211,933.
- Per-frequency zero rate: 22.52%.
- Retunes: 49,452.
- No-transmission outcomes: 2,568 of 36,957 concluded calls, or 6.95%.
- Recorder-exhausted outcomes: 0.
- Health rows: 2,120.

The full-study average is misleading by itself. Excellent and failed periods
cancel each other and make the aggregate resemble the split-path baseline even
though the direct path alternated between much better and much worse states.

## Split-Path Comparison

The prior 30-minute split-path baseline was:

- 14.58 msg/sec average control-channel decode.
- 22.4% zero decode.
- 119 retunes, equivalent to 238 per hour.
- 150 calls started, equivalent to 300 per hour.

Calls per hour are traffic-dependent and are not a direct RF-quality measure.
The normalized comparison is retained only for context.

| Period | Decode | Zero decode | Retunes/hour | Calls/hour | No transmission | Tuning error |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Tue-Thu direct path | 35.01 msg/sec | 4.19% | 57.6 | 263.4 | 5.8% | 587 Hz |
| Fri before 6:30 PM | 32.40 msg/sec | 9.37% | 124.8 | 195.2 | 8.0% | 579 Hz |
| Fri after 6:30 PM | 3.15 msg/sec | 72.73% | 907.6 | 100.9 | 16.8% | 596 Hz |
| Sat plus Sun through 8:45 AM | 5.84 msg/sec | 68.90% | 872.2 | 100.6 | 15.4% | 595 Hz |

The direct path clearly beat the split-path baseline in its healthy state and
performed much worse during degraded periods. Bypassing the splitter improved
available healthy-state margin but did not eliminate the underlying failure.

## Time-Of-Day And Transition Evidence

- Monday began badly. Midnight-6:00 AM averaged 6.78 msg/sec with 73.15%
  zero decode, then recovered to 35.62 msg/sec during the evening.
- Tuesday through Thursday were the most stable days.
- Friday deteriorated sharply. The 6:00-6:30 PM interval measured 22.56
  msg/sec and 22.22% zero decode. The 6:30-7:00 PM interval fell to 8.56
  msg/sec and 55.56% zero decode. The 7:30-8:00 PM interval fell to 3.00
  msg/sec and 66.67% zero decode. The first complete 30-minute zero-decode
  interval began at 10:30 PM.
- Saturday recovered intermittently but did not establish sustained hourly
  recovery. A strong 7:30-8:00 PM interval reached 26.00 msg/sec, 0% zero
  decode, and 104 retunes, then immediately collapsed again.
- Sunday midnight-6:00 AM averaged 0.01 msg/sec with 99.07% zero decode.
  The final 7:45-8:45 AM hour remained severely degraded at 0.24 msg/sec,
  76.47% zero decode, and 753 retunes.

This resembles a time-dependent pattern, but it is not proven to be a weekend
effect. Only one complete Saturday was observed, Sunday was partial, and Monday
overnight was also poor.

## Utility-Power Evidence

On Sunday, July 12, utility power failed at approximately 2:15 PM EDT and was
restored at approximately 6:00 PM EDT. The RPI remained on backup power.

Equal adjacent 3.75-hour windows show:

| Power period | Decode | Zero decode | Retunes | No transmission | Tuning error |
| --- | ---: | ---: | ---: | ---: | ---: |
| Before, 10:30 AM-2:15 PM | 30.40 msg/sec | 10.45% | 340 | 4.5% | 596 Hz |
| Outage, 2:15-6:00 PM | 34.52 msg/sec | 4.48% | 224 | 4.9% | 577 Hz |
| Restored, 6:00-9:45 PM | 38.54 msg/sec | 0% | 32 | 5.4% | 598 Hz |

There was no RF degradation synchronized with either the outage or power
restoration. The power event does not explain the later failures, although
time-of-day remains a confounding variable.

## No-Transmission And Tuning Findings

- No-transmission outcomes rose from 5.8% during Tuesday-Thursday to 15.4%
  during the degraded Saturday/Sunday period.
- A zero no-transmission count during complete decode loss is not success. No
  calls concluded, so there were no outcomes to classify.
- Mean absolute tuning error remained broadly stable, generally around
  580-614 Hz. Gross frequency-calibration drift is not supported as the
  primary cause.
- Tuning error is unavailable during total loss and is measured only on calls
  that survive far enough to produce evidence. This creates survivor bias.

## Service And Data-Quality Findings

- The PizzaWave API and live TR health path remained reachable.
- Brief live-TR stale flags between five-minute health samples cleared at the
  next sample boundary and were treated as cadence artifacts.
- Expected PizzaWave application restarts did not create sustained ingestion
  gaps and were not classified as RF or TR anomalies.
- Jackson's intentionally disconnected path was excluded from every aggregate.
- The one missing five-minute bucket does not materially affect the result.

## Conclusion

Bypassing the splitter improved healthy-state performance but did not eliminate
the underlying failure. The splitter may consume useful link margin, but it is
not a sufficient root-cause explanation for the severe time-varying outages.

The repeated combination of zero control-channel decode, high retune counts,
continued service reachability, and mostly stable tuning error points to loss
of usable control-channel acquisition rather than a PizzaWave service outage
or simple gross tuning offset.

## Recommended Next Experiment

Do not use another long sequential direct-versus-split comparison as the next
primary experiment. Temporal RF variation is large enough to confound it.

Capture synchronized one-minute RSSI, SNR, noise floor, control-channel decode,
and retune telemetry, with short spectrum or IQ snapshots during both healthy
and failed states. Add an independent reference receiver, preferably through a
characterized coupler or controlled common feed, to distinguish a common RF
path change from Airspy front-end behavior. A short, randomized stepped-
attenuator test during degradation can then distinguish overload/desense from
weak-signal or multipath behavior more effectively than another week-long
sequential A/B run.
