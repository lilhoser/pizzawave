# OT and RPI intermittent RF degradation diagnosis

Date: 2026-07-18 (America/New_York)

## Question

OT and RPI began showing unusually severe, intermittent control-channel decode degradation on 2026-07-17. The sites do not share an RF path, location, or receiver hardware. This experiment tested whether the common PizzaWave/Trunk Recorder stack was causing the RF loss, and whether direct control-channel tuning behaved differently from the deployed wideband source plans.

## Result

No recent PizzaWave deployment changed the receivers' RF input or active Trunk Recorder source plans. No pre-experiment USB reset, undervoltage, or thermal-throttling evidence was found.

The failure is a threshold interaction with two parts:

1. Real input-level variation reduces decoder margin. On RPI, the degraded wideband IQ sample was about 3.6 dB lower overall than the earlier healthy sample.
2. The deployed wideband/off-center source plans and rapid control-channel cycling magnify a marginal interval. OT's off-center plan made a decodable signal unusable. On RPI, captured wideband IQ remained decodable by a fresh offline decoder even while the live decoder was cycling and reporting zero.

The similar operator-visible behavior does not require a shared physical RF cause. Ordinary, independent fades can cross the same software threshold and then be amplified by the same source-plan and retune behavior on both systems.

## OT evidence

- Original active configuration mtime: 2026-06-02 14:23:55 EDT. It was not changed by the recent PizzaWave work.
- Six RTL devices enumerate. Serial `00000006` is unused, but its 60-second capture was effectively an idle ADC input and has no usable RF feed.
- Normal North Bradley source 0:
  - serial `00000003`
  - logical center 770.515625 MHz
  - primary control channel 769.606250 MHz
  - control channel is 909.375 kHz below the logical center
  - sample rate 2.4 Msps
- The matched-plan outage IQ showed the control-channel peak at the expected corrected position. The configured frequency correction is not the cause.
- The matched-plan IQ did not decode in offline Trunk Recorder replay at tested fine adjustments from -3 kHz through +3 kHz.
- A direct-centered IQ capture from the same normal receiver decoded the correct North Bradley system offline, reaching 25 msg/s.
- Strongest paired observation, 18:01-18:02 EDT:
  - temporary direct-tuned reference receiver, serial `00000002`: 37-40 msg/s continuously
  - normal live source 0, serial `00000003`: 0 msg/s while alternating control channels
  - both observations covered the same site and wall-clock interval

This rules out a North Bradley transmitter outage during the test. Direct tuning supplied enough decoder margin; the normal source plan did not.

## RPI evidence

- Active configuration mtime: 2026-07-10 13:45:15 EDT, approximately one week before the reported onset.
- Host power and temperature at test time:
  - `vcgencmd get_throttled`: `0x0`
  - CPU temperature: 57.6 C
  - no pre-experiment Airspy USB reset/disconnect evidence
- Normal Raymond source:
  - logical center 771.931250 MHz
  - primary control channel 773.781250 MHz
  - control channel is 1.850 MHz above logical center
  - sample rate 6 Msps
- Healthy matched-plan capture at 17:50 EDT:
  - mean spectral SNR proxy 15.17 dB
  - range 2.51-22.65 dB within one minute
  - overall complex-sample RMS about -51.90 dB relative to int16 full scale
- Degraded matched-plan capture at 18:03 EDT:
  - mean spectral SNR proxy 15.01 dB
  - range 5.37-21.26 dB
  - overall complex-sample RMS about -55.49 dB relative to int16 full scale, a 3.60 dB reduction from the healthy sample
- Degraded direct-centered capture at 18:04 EDT:
  - about 1.88 dB more control-channel power than the degraded matched-plan capture
  - about 2.38 dB more spectral SNR proxy
- Both degraded files identified the correct Raymond system and decoded control-channel messages in fresh offline Trunk Recorder replay.

The RPI wideband raw signal was still decodable. The live process's sustained zero state therefore cannot be attributed only to absent RF. The low-rate retune loop repeatedly leaves the primary channel and can prevent or delay reacquisition after the initial margin loss.

## Fixed-window source-center A/B

On 2026-07-19, each host ran a 30-minute baseline followed by a 30-minute candidate window. The candidate changed only source centers; `controlRetuneLimit` remained `0` throughout.

- OT baseline: Unix `1784462319` through `1784464119`
- RPI baseline: Unix `1784462320` through `1784464120`
- Candidate window on both hosts: Unix `1784464397` through `1784466197`
- OT candidate centers:
  - source 0: 770.515625 to 769.912500 MHz
  - source 1: 772.918750 to 772.443750 MHz
- RPI candidate center:
  - source 0: 771.931250 to 772.831250 MHz

The values below come from exactly 600 three-second message-rate samples per system in each window.

| System | Baseline avg | Candidate avg | Baseline zero | Candidate zero | Baseline retunes | Candidate retunes |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| OT North Bradley | 6.67 msg/s | 10.39 msg/s | 38.00% | 30.67% | 252 | 209 |
| OT Cleveland | 38.12 msg/s | 38.60 msg/s | 0.00% | 0.00% | 0 | 0 |
| OT Hamilton | 9.05 msg/s | 10.93 msg/s | 13.17% | 12.33% | 113 | 104 |
| RPI Raymond | 0.93 msg/s | 0.01 msg/s | 69.83% | 99.00% | 473 | 0 |
| RPI Jackson | 0.00 msg/s | 0.00 msg/s | 100.00% | 100.00% | 0 | 0 |

The OT candidate moved North Bradley in the intended direction: average decode increased by 55.8%, zero samples fell 7.33 percentage points, and retunes fell 17.1%. The unchanged Hamilton system also improved during the later window, however, so one sequential pair cannot assign all of the North Bradley gain to the center change. A longer or repeated paired test is required before making the candidate permanent.

The RPI comparison does not measure the candidate center fairly. The active configuration contains only the primary 773.781250 MHz control channel at process start. Alternate Raymond control channels are learned from decoded control messages. Raymond restarted during a severe outage, decoded almost nothing, learned no alternates, and consequently made no retune attempts. This exposes a separate cold-start recovery weakness: a receiver cannot try known alternates that are absent from the active configuration when the configured primary is not decodable.

All observed OT North Bradley and RPI Raymond voice frequencies remained inside the candidate source coverage. Neither candidate window produced a no-source coverage error.

Both original configurations were restored after evidence capture. During restoration, the first RPI file install briefly assigned the wrong group to the otherwise valid config file, so Trunk Recorder could not read it and exited once. Ownership was corrected to `root:trunk-recorder`, the stale fault marker created by that failed start was cleared, and the original config was started successfully. Final state on both hosts was `active/running`, with PizzaWave health `ok`; RPI live activity remained stale because Raymond was still not decoding.

## Trunk Recorder retune branch

Current upstream behavior checks decode approximately every three seconds and immediately round-robins to the next learned control channel below 2 msg/s. `controlRetuneLimit` is not a dwell or recovery control: values above zero terminate Trunk Recorder after the configured number of failed retune attempts. It must remain `0` for these systems.

A local Trunk Recorder branch, `codex/retune-hysteresis`, adds `controlRetuneGracePeriod` while preserving the upstream default of immediate retuning when the setting is `0`. Commit `f893508` is based on current upstream. Because OT's installed binary dates to 2026-04-29, a compatibility branch, `codex/retune-hysteresis-ot` at `58b5b3c`, applies the same change to upstream commit `766a553` from 2026-04-27 so the live trial does not include months of unrelated upstream changes.

Offline replay with `controlRetuneGracePeriod: 12` showed:

- an undecodable sample retuned at 12-second intervals rather than every three seconds;
- a decodable direct-centered sample acquired the correct site without a retune despite several initial low-rate checks;
- the same decodable sample with legacy behavior cycled three times before acquisition.

The OT compatibility build then ran for a fixed 30-minute live window from Unix `1784467399` through `1784469199`, with original source centers, `controlRetuneGracePeriod: 12`, and `controlRetuneLimit: 0`.

| System | Samples | Avg decode | Zero samples | Samples below 2 | Retunes | Calls concluded |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| OT North Bradley | 597 | 12.82 msg/s | 9.21% | 11.89% | 15 | 12 |
| OT Cleveland | 597 | 39.87 msg/s | 0.00% | 0.00% | 0 | 27 |
| OT Hamilton | 597 | 15.31 msg/s | 5.86% | 6.03% | 12 | 306 |

North Bradley experienced several real low-decode intervals. Retunes within a recovery cycle were 12 seconds apart, never three seconds apart. It returned to the primary and recovered multiple times. Compared with the original-center baseline, North Bradley average decode increased from 6.67 to 12.82 msg/s, zero samples fell from 38.00% to 9.21%, and retunes fell from 252 to 15. Hamilton showed the same direction of change, while Cleveland remained stable. No service exit, no-source event, or callstream failure occurred during the successful window.

The first live start used an incomplete runtime library path and could not load the installed `libcallstream.so`; it was rolled back immediately. The corrected isolated override included the installed plugin directory and completed normally. This was a trial-packaging error, not a crash in the retune change.

The live result is strong evidence that longer dwell materially improves recovery and eliminates wasteful churn. It does not prove that 12 seconds is the best value, does not prevent the initial RF/decode collapse, and does not solve the RPI cold-start problem when only one control channel is configured. The experimental binary and config were rolled back after the fixed window; OT finished on its original `/usr/local/bin/trunk-recorder`, and RPI was never changed. Final verification showed both hosts' `trunk-recorder` and `pizzad` services active, PizzaWave health `ok`, and live TR activity current.

PizzaWave's RF-analysis API returned only 500 message-rate samples for the 30-minute window, while the journal contained 597 after startup. The API had no hard 500-row cap; it included only complete five-minute health buckets and silently omitted the partial bucket at each boundary. Its percentage therefore differed from the complete raw-window calculation above.

## Setup recovery-channel repair

On 2026-07-19, Setup was found to retain all four RadioReference control-channel candidates for each RPI site in desired state while writing only the single RF-selected channel to the active Trunk Recorder config. The RF selection was incorrectly used as an allowlist rather than as the preferred-channel ordering signal. Config Draft also collapsed each system to its single best live RF candidate, and Setup reconciliation compared only a stack-wide aggregate channel list.

The repair now:

- keeps the proven/preferred channel first while retaining authoritative alternates;
- reports active-versus-desired control-channel drift per system;
- allows Call Quality proof before an additive recovery-channel-only apply while continuing to block source, system-removal, or channel-replacement drift;
- compares stored system definitions by frequency values instead of collection object identity, preventing successful proof records from being deleted on each Setup refresh; and
- includes exact partial boundary samples in fixed-window RF analysis.

After call-capture proof passed with 10 real audio calls and transcription proof passed with 11 usable transcripts, Setup applied and restarted the RPI config with Raymond `[773.781250, 773.031250, 773.281250, 773.531250]` MHz and Jackson `[855.287500, 855.537500, 855.987500, 856.812500]` MHz. `controlRetuneLimit` remained `0`; no retune-grace binary was installed. Raymond cold-started on 773.781250 MHz, produced 28 msg/s in its first three-second report, and reached 37-41 msg/s within 38 seconds. Jackson continued cycling because its assigned receiver remains disconnected.

## Artifacts

Artifacts remain on the measured hosts under `/var/lib/pizzawave/rf-surveys/manual`.

OT:

- `20260718T213110Z-ot-north-bradley-outage-iq`
- `20260718T214309Z-ot-north-bradley-live-plan-outage-iq`
- `20260718T220046Z-ot-source1-live-reference-decoder`
- `20260719-source-center-ab`
- `20260719-retune-grace-live`

RPI:

- `20260718T215003Z-rpi-raymond-live-plan-healthy-iq`
- `20260718T220326Z-rpi-raymond-live-plan-degraded-iq`
- `20260718T220458Z-rpi-raymond-direct-centered-degraded-iq`
- `20260719-source-center-ab`

## Recommended next steps

The repeated source-center test is complete. See
[2026-07-20-source-centering-aba.md](2026-07-20-source-centering-aba.md).
Its A-B-A result does not support adopting the candidate centers as a
stabilization fix: Raymond worsened during the centered phase, North Bradley's
apparent gain was confounded by simultaneous improvement on unchanged
Hamilton, and both targets decoded cleanly after their original centers were
restored.

1. Run the simultaneous OT North Bradley receiver-role crossover specified in
   the July 20 record. It is now the highest-value experiment because it
   separates source geometry from a physical SDR/feed leg without another
   sequential time confound.
2. If the crossover identifies a receiver/front-end effect, follow with a
   short randomized gain or stepped-attenuation challenge during degradation.
3. If captured IQ remains decodable while only the live chain fails, focus the
   next implementation work on live DSP/reacquisition state rather than RF
   center placement.
4. Keep alternate-channel validation and retune grace as secondary recovery
   work. Neither should be presented as the root-cause investigation.
5. Add per-source channel power, noise floor, SNR, frequency residual, and
   device identity only where needed to discriminate the crossover result;
   decode rate alone cannot separate RF loss from failure to reacquire.

## Limits

The experiment identifies how the common software configuration turns marginal intervals into severe outages. It does not establish why two geographically separate antenna paths first lost margin on the same date. Weather, propagation, site-side changes, and common antenna-model behavior remain possible physical triggers, but no evidence collected here distinguishes among them.
