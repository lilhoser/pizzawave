# OT Ventax decoder-isolation handoff

Date: 2026-07-24

PizzaWave source baseline: `main` at `b03367d`

Target location: OT

Comparator host: Ventax, a high-end Windows laptop

## Purpose

Resume the RF root-cause experiment without moving the OT antenna, buying
unproven hardware, or changing the production receiver first.

The experiment has two deliberately separate stages:

1. Replay the exact same retained OT IQ through two independent decoder paths.
   This isolates decoder behavior from the antenna, tuner, USB path, and live
   propagation.
2. Only if the same-IQ result justifies it, run an independent live receiver on
   Ventax from another output of the existing multicoupler. This compares the
   complete receiver paths while keeping the antenna and incoming RF common.

Do not combine the stages. In particular, a different live dongle on a
different computer is not a decoder-only comparison.

## Current conclusion

The leading OT explanation is dynamic simulcast/multipath modulation
destruction at North Bradley and Hamilton. Confidence is moderate to high.
Exact-frequency co-channel interference remains a secondary North
Bradley-specific alternative, but retained network-ID evidence has not shown a
foreign NAC or site identity. Hamilton has no identified nearby continuous
control-channel reuse.

The evidence does not support the following as the primary cause of the
natural OT blips:

- broadband front-end overload;
- a common USB, host-load, or sample-delivery failure;
- antenna mistuning outside the desired band;
- the MCA208M by itself;
- one receiver model common to OT and RPI;
- a single common interferer affecting OT and RPI.

The current CQPSK decoder and live alternate-channel cycling can extend some
outages after the initial RF impairment. That is a recovery contributor, not
the established physical onset.

## Fixed OT physical baseline

Record this baseline in every result. Do not silently substitute a different
topology.

- Antenna: Amphenol PCTEL `MFBW7463`, a 746-869 MHz fiberglass base-station
  omni.
- Mounting: approximately 20 feet high on a PVC mast, about 16 inches from the
  metal building.
- Roof clearance: the complete radiator, not merely its tip, is above the
  roofline.
- Existing path:

  `MFBW7463 -> 15 ft LMR-200 -> MCA208M -> RTL-SDR -> Atolla powered USB hub -> 10 ft active USB extension -> OT Linux server`

- North Bradley's historically observed primary control channel is
  `769.606250 MHz`. Confirm the live primary before any live comparison instead
  of assuming it has not changed.

The antenna is in-band and its full radiator clears the roof. Randomly moving
this 20-foot installation is therefore not the next OT test. Its proximity to
the building can still influence its pattern, but that possibility does not
justify an uncontrolled mast move before the easier decoder and receiver-path
discriminators.

## Guardrails

- Stage 1 is offline. Do not restart or modify production Trunk Recorder or
  PizzaWave.
- Do not deploy an experimental Trunk Recorder branch.
- Do not change production gain, AGC, centering, alternate controls, recovery
  policy, antenna, coax, multicoupler, or filtering.
- Do not install the available BPF-800-M for this comparison. An exact-frequency
  P25 signal would pass through it, and OT evidence does not show broadband
  overload as the leading issue.
- Do not plug the live comparator into the production Atolla hub or the
  production 10-foot active USB extension.
- Do not commit IQ, converted baseband recordings, decoder databases, logs, or
  audio. Store them outside the repository.
- Treat a deployment, service restart, USB error, sample discontinuity, or
  missing clock alignment as a contaminated interval, not RF evidence.

## Stage 1: exact-sample offline decoder comparison

### Question

Given identical complex samples, does an independent P25 decoder retain valid
North Bradley control-channel frames when the current Trunk Recorder decoder
does not?

Ventax is suitable for this test. Offline replay removes live USB timing and
does not require a Linux server comparable to OT. The acceptance condition is
that both decoder runs consume the same sample sequence without dropped
samples, not that they use identical operating systems.

### Retained OT corpus

Start with these North Bradley captures:

| Trigger | IQ SHA-256 | JSON SHA-256 | Why it matters |
| ---: | --- | --- | --- |
| `1784732498025` | `288a628bd22972d09be27416799b4adf75b7cc68468e9f51936d9853b8abd7c7` | `47fce684696251d61c1af6ca04a1d83fe6700b57974de078da8b0851310e4671` | Both decoders fell to zero with only a 0.16 dB narrow-power onset change, then recovered. |
| `1784732918021` | `1d013a8bd266f84eaaee529a5383e828fd411ad76ad1dfdfd08a93e84638b454` | `d0d98a4a6f7c871de7483d275dd497b6ce937d3bb0d4c0e7638f5d4e056dfbc4` | Both decoders fell; low-window narrow power was 2.47 dB below the healthy pre-trigger window. Live recovered quickly after retuning while the fixed-primary shadow stayed poor. |
| `1784754546018` | `d422e04801f2398fd43c9b105d3f78c415b4d04bc0143105de46f9ff0c2f34ae` | `57f3e7ea722483efadcede98969dff00a6cc3293aac31d2ae3780dcaf4998ac3` | Both decoders fell with a 2.34 dB in-channel loss and only +0.03 dB change in 12.5-45 kHz outer-band energy; Hamilton and Cleveland stayed healthy. |
| `1784584105012` | `845ab28446f73e0726514165ca0f38bbec3cfb5061819b0900ab577bd76c7100` | `842fe59d87151559a1c2c0459cd215668f6d70d7f0b96d97c42da0bbeecaa9c6` | Earlier cross-geography event: clean samples, roughly 0.4 dB power and 0.5 dB CNR change, low decode reproduced in replay, and Hamilton stayed healthy. |

The authoritative host directory is:

`/var/lib/pizzawave/rf-surveys/manual/20260720-initial-collapse-flight-recorder/ot`

Capture `1784732918021` is complete: `completedUnixMs` is `1784732978271`;
the IQ file is 69,120,000 bytes and the JSON file is 27,491 bytes.

Use at least one healthy control segment from the pre-trigger portion of each
capture. Do not score only the failed interval.

### Artifact acquisition

1. On Ventax, create an artifact directory outside the repository, for example
   `C:\temp\pizzawave-rf\ot-ventax-decoder`.
2. Resolve each exact JSON and IQ filename on OT with a read-only listing.
3. Copy the selected files to Ventax without modifying the originals.
4. Run `Get-FileHash -Algorithm SHA256` on every local copy.
5. Stop if a known hash differs.
6. Preserve the source JSON beside the IQ. It contains the sample rate,
   center/channel frequency, trigger boundary, and decoder timeline needed for
   alignment.

### Baseline decoder

Replay each capture through the same Trunk Recorder P25 control-decoder lineage
used for the retained event. Follow the existing fixed-primary file-source
method documented in
[2026-07-21-p25-control-demodulator-replay.md](2026-07-21-p25-control-demodulator-replay.md).

Use the production CQPSK control demodulator. Keep the control frequency fixed;
do not enable alternate-channel hunting or traffic following. Record the exact
TR commit, executable hash, configuration hash, command, run start/end time,
input hash, and output-log hash.

Run each input three times. Identical offline inputs should produce identical
counts. If they do not, investigate nondeterminism or sample loss before
comparing decoders.

### Independent decoder candidate

SDRTrunk on Windows is the first candidate because it is an independent P25
decoder and Ventax is an appropriate 64-bit Windows host. It is a candidate,
not a presumed winner.

Before writing a converter, inspect the exact SDRTrunk release and its source
to determine the supported recording input format. Do not assume that a raw
PizzaWave `.fc32` file can be renamed or wrapped with an invented header. The
PizzaWave files contain interleaved little-endian 32-bit floating-point I/Q
samples.

The ingestion gate is:

- no resampling unless the decoder cannot accept the original rate;
- no AGC, normalization, clipping, or filtering that is absent from the
  baseline path;
- exact preservation of source sample order and count;
- explicit center frequency, sample rate, and channel-frequency metadata;
- a round-trip or payload test proving that conversion did not alter the
  complex samples;
- a recorded converter version and output SHA-256.

If current SDRTrunk cannot ingest these samples with a verified lossless path,
choose another independent decoder that can. Do not weaken the isolation by
substituting a new live recording for the retained IQ.

Configure only the North Bradley primary control channel and disable audio or
traffic-channel work that is irrelevant to control decoding. Record valid
control messages per second, decoded WACN/system/NAC, acquisition time, rejected
or near-valid network IDs if exposed, and any input underrun or decoder error.
Run each input three times.

### Common scoring

Align both decoders to the JSON trigger in one-second windows. For every run,
report:

- valid P25 control messages per second;
- first valid message and recovery time;
- longest consecutive zero/low-decode interval;
- WACN, system ID, NAC, and any nonlocal identity;
- healthy-window and failed-window totals;
- whether the input was consumed without drops;
- CPU only as a capacity check, not as an RF metric.

Use `scripts/analyze_p25_collapse_iq.py` for the common IQ power and
sample-integrity record. Use `scripts/analyze_p25_demod_replay.py` for the
existing TR replay summary where applicable.

### Stage 1 decision

| Result | Interpretation | Action |
| --- | --- | --- |
| Independent decoder materially outperforms TR on the same failed samples and does not lose healthy-window frames | Decoder tolerance is a practical contributor | Repeat on the full corpus, then consider a bounded decoder integration or configuration test |
| Both decoders fail at the same boundaries | The recorded waveform is damaged beyond both decoders | Proceed to Stage 2 only if a hardware-path discriminator is still useful; prioritize simulcast geometry |
| Results differ only after sample conversion | Conversion is a confounder | Fix or reject the conversion; do not draw a decoder conclusion |
| Repeated runs differ | The offline harness is not deterministic | Fix the harness before proceeding |
| A foreign NAC or site identity appears reproducibly | Co-channel evidence becomes materially stronger | Preserve exact words/timestamps and investigate the identified transmitter before hardware changes |

The minimum Stage 1 result is all four captures, healthy and failed windows,
three runs per decoder, verified hashes, and one concise comparison table.

## Stage 2: independent live chain

Run this only after Stage 1 is complete and its reason for proceeding is
written down.

### Topology

Keep the antenna and multicoupler common, then separate everything practical:

`MFBW7463 -> 15 ft LMR-200 -> MCA208M`

- Production branch:
  `existing MCA output -> existing RTL-SDR -> Atolla hub -> active extension -> OT`
- Comparator branch:
  `different MCA output -> short known-good 50-ohm jumper -> separate matching RTL-SDR -> direct Ventax USB port`

Use a separate powered hub on Ventax only if a direct port is electrically or
mechanically impractical. Never share the production Atolla hub or active USB
extension. Sharing them would make a common USB power, hub, or extension fault
indistinguishable from RF.

Use the same RTL-SDR model and hardware revision when possible. Photograph and
record serial numbers, MCA output numbers, jumper identity/length, and Ventax
USB port.

### Controls

- Confirm all unused MCA outputs are correctly terminated before and after the
  test.
- Disable tuner AGC on both paths.
- Match nominal tuner gain, sample rate, channel bandwidth, and frequency
  centering.
- Calibrate PPM independently; do not copy one dongle's correction to the
  other.
- Keep North Bradley fixed on the verified primary for the initial run.
- Disable alternate-channel hunting and traffic following on the comparator.
- Synchronize OT and Ventax clocks with NTP and record measured clock offset.
- Record per-second valid control frames, NAC/site identity, received power,
  sample integrity, tuner/USB errors, and CPU.
- Do not judge the paths by RSSI alone. Simulcast failure can occur with almost
  unchanged total power.

Run for 48 hours or until three cleanly aligned natural North Bradley blips,
whichever is later. Hamilton and Cleveland telemetry on OT remain same-host
controls.

### Crossover sequence

If the two live paths behave differently:

1. Swap only the two MCA208M output ports. Leave receivers, USB paths, hosts,
   and software unchanged.
2. If the behavior does not follow the MCA port, swap only the two physical
   RTL-SDR units while software and hosts remain unchanged.
3. Do not make both swaps together.

Interpret what the failure follows:

- MCA port or RF jumper: passive RF branch;
- physical dongle: receiver;
- Ventax versus OT after dongle crossover: USB/host or decoder;
- decoder on same retained IQ: decoder;
- both independent live paths at the same time: incoming RF/simulcast
  impairment.

## Purchase gate

Buy nothing before Stage 1.

Stage 2 should use an available spare matching RTL-SDR, an unused MCA208M
output, a short known-good 50-ohm jumper, and a direct Ventax USB port if those
items are already available. Do not buy another antenna, preamplifier,
attenuator, filter, or multicoupler from the current evidence.

A receiver specifically designed for P25 simulcast, such as a scanner using
true I/Q demodulation, could later serve as a diagnostic benchmark. It is not a
drop-in PizzaWave receiver and should be purchased only if Stage 1 and Stage 2
leave a clear question that such a benchmark would answer.

## Permanent-record coverage audit

The durable experiment record is split intentionally:

| Topic | Permanent record |
| --- | --- |
| Initial narrow captures, passive shadow, OT cross-geography test, paired narrow/wide results, Hamilton/Raymond comparison, OT Gardner/source-stall restarts, hardware-path correction, gain 15/12/9/14 trials, frequency reuse/NAC replay, persistent capture rearm, and post-quota paired results | [2026-07-20-initial-collapse-flight-recorder.md](2026-07-20-initial-collapse-flight-recorder.md) |
| Same-IQ CQPSK/FSK4/half-timing replay, two wrong-lineage plugin ABI failures, exact-lineage live control-only FSK4 gate, natural event, same-IQ comparison, and rollback | [2026-07-21-p25-control-demodulator-replay.md](2026-07-21-p25-control-demodulator-replay.md) |
| Current conclusions, remaining work, isolation rules, and ownership boundaries | [../work-queue.md](../work-queue.md) |
| OT antenna facts, no-purchase decision, Ventax host suitability, two-stage variable isolation, independent USB topology, crossover plan, and acceptance matrix | This handoff |

The field log now also records previously omitted North Bradley capture
`1784732918021` with source-verified hashes. Routine healthy monitor polls and
known PizzaWave-only deployment gaps are not individually duplicated because
they produced no RF result; the permanent record retains the relevant health,
restart, quota, and exclusion conclusions.

## Exact next steps on Ventax

1. Check out PizzaWave `main` at or after `b03367d` and read this handoff plus
   the two linked field-test records.
2. Create the external artifact directory and copy the four retained capture
   pairs from OT read-only.
3. Verify and record every source/destination hash.
4. Freeze the baseline TR replay lineage and produce deterministic three-run
   results.
5. Inspect the current independent decoder's recording format and implement or
   select a lossless ingestion path with sample-count/payload tests.
6. Run the same corpus three times through the independent decoder.
7. Commit only the comparison tables, commands, hashes, and conclusions to this
   handoff or a dated result document. Never commit IQ or generated decoder
   artifacts.
8. Decide explicitly whether Stage 2 is warranted. If it is, inventory the
   spare RTL-SDR, MCA output, jumper, and direct Ventax USB port before touching
   the production RF path.

## Completion criteria

The OT experiment is ready for a hardware or software recommendation only when
one of these is true:

- the same-IQ corpus shows a repeatable independent-decoder advantage without a
  healthy-window regression;
- three aligned live events show both independent receiver paths fail together;
- crossover testing localizes a repeatable difference to an MCA branch,
  dongle, host/USB path, or decoder;
- reproducible foreign NAC/site evidence identifies a co-channel source.

Until then, the best-supported OT fix direction is improving simulcast-tolerant
demodulation or using a more site-selective receive pattern, not adding gain or
filtering.

## External references

- [PCTEL fiberglass base-station omnidirectional antennas](https://pctel.com/antenna-product/fiberglass-base-station-omnidirectional-antennas/)
- [DigiKey MFBW7463 product page](https://www.digikey.com/en/products/detail/amphenol-pctel/MFBW7463/13687599)
- [SDRTrunk releases and platform requirements](https://github.com/DSheirer/sdrtrunk/releases)
