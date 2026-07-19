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

A local Trunk Recorder branch, `codex/retune-hysteresis`, adds `controlRetuneGracePeriod` while preserving the upstream default of immediate retuning when the setting is `0`. Commit `f893508` built successfully on OT but was not installed on either live host.

Offline replay with `controlRetuneGracePeriod: 12` showed:

- an undecodable sample retuned at 12-second intervals rather than every three seconds;
- a decodable direct-centered sample acquired the correct site without a retune despite several initial low-rate checks;
- the same decodable sample with legacy behavior cycled three times before acquisition.

This is sufficient evidence for a controlled OT-only live trial, but not yet for an upstream pull request. The branch still needs live behavior across real fades, and it does not solve the RPI cold-start problem when only one control channel is configured.

## Artifacts

Artifacts remain on the measured hosts under `/var/lib/pizzawave/rf-surveys/manual`.

OT:

- `20260718T213110Z-ot-north-bradley-outage-iq`
- `20260718T214309Z-ot-north-bradley-live-plan-outage-iq`
- `20260718T220046Z-ot-source1-live-reference-decoder`
- `20260719-source-center-ab`

RPI:

- `20260718T215003Z-rpi-raymond-live-plan-healthy-iq`
- `20260718T220326Z-rpi-raymond-live-plan-degraded-iq`
- `20260718T220458Z-rpi-raymond-direct-centered-degraded-iq`
- `20260719-source-center-ab`

## Recommended next steps

1. Run repeated or longer OT North Bradley baseline/candidate pairs before adopting the candidate source centers permanently. Use Cleveland and Hamilton as contemporaneous controls.
2. Run an OT-only live trial of the retune branch with `controlRetuneGracePeriod` set to 12 seconds and `controlRetuneLimit` left at `0`. Preserve an immediate rollback binary and config.
3. Test explicit configuration of all known-good control-channel alternates, especially on RPI, so cold-start recovery does not depend on first decoding the primary.
4. If the grace trial succeeds, refine the upstream design around last-known-good preference and reacquisition dwell, then decide whether to fork or propose it upstream.
5. Add durable per-source RF telemetry independent of decoded message rate: absolute channel power, local noise floor, channel SNR, frequency residual, selected control channel, and retune state. Decode rate alone cannot distinguish RF loss from failure to reacquire.
6. Repeat the IQ comparison after the source-plan and retune changes. The success criterion is not only a higher average decode rate, but removal of long zero windows during modest input-level fades.

## Limits

The experiment identifies how the common software configuration turns marginal intervals into severe outages. It does not establish why two geographically separate antenna paths first lost margin on the same date. Weather, propagation, site-side changes, and common antenna-model behavior remain possible physical triggers, but no evidence collected here distinguishes among them.
