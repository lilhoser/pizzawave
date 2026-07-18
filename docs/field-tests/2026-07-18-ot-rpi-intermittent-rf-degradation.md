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

## Artifacts

Artifacts remain on the measured hosts under `/var/lib/pizzawave/rf-surveys/manual`.

OT:

- `20260718T213110Z-ot-north-bradley-outage-iq`
- `20260718T214309Z-ot-north-bradley-live-plan-outage-iq`
- `20260718T220046Z-ot-source1-live-reference-decoder`

RPI:

- `20260718T215003Z-rpi-raymond-live-plan-healthy-iq`
- `20260718T220326Z-rpi-raymond-live-plan-degraded-iq`
- `20260718T220458Z-rpi-raymond-direct-centered-degraded-iq`

The original OT configuration was restored after the experiment. Both `trunk-recorder` and `pizzad` were active and PizzaWave health returned `status: ok` on both hosts.

## Recommended next steps

1. Run a controlled 30-minute fixed-window source-plan A/B on each target, using the RF analysis API and exact Unix timestamps:
   - current plan
   - a plan that moves the source center materially closer to the primary control channel without unnecessarily sacrificing voice-channel coverage
2. On OT, first test dedicating the lower North Bradley source more closely to 769.606250 MHz while retaining the second North Bradley source for upper voice channels.
3. Change control-channel recovery behavior so a short low-rate interval does not immediately cycle every learned candidate at three-second dwell times. Prefer the last known-good primary, require sustained failure before leaving it, and give it a meaningful reacquisition dwell before moving again.
4. Add durable per-source RF telemetry independent of decoded message rate: absolute channel power, local noise floor, channel SNR, frequency residual, selected control channel, and retune state. Decode rate alone cannot distinguish RF loss from failure to reacquire.
5. Repeat the IQ comparison after the source-plan and retune changes. The success criterion is not only a higher average decode rate, but removal of long zero windows during modest input-level fades.

## Limits

The experiment identifies how the common software configuration turns marginal intervals into severe outages. It does not establish why two geographically separate antenna paths first lost margin on the same date. Weather, propagation, site-side changes, and common antenna-model behavior remain possible physical triggers, but no evidence collected here distinguishes among them.
