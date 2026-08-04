# OT Ventax decoder-isolation experiment

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

## Completed result

The experiment completed on 2026-07-24. Stage 1 showed that the independent
OP25 decoder materially outperformed the production Trunk Recorder lineage on
the same samples for all three recent North Bradley failures. Stage 2 then
captured four qualifying Cleveland failure-and-recovery events in which the OT
production path degraded or reached zero while the independent Ventax path
remained healthy.

These results reject a simultaneous failure of the common antenna,
multicoupler input, or incoming Cleveland waveform as the sufficient cause of
the observed production collapses. They localize the avoidable failure
downstream of the MCA208M split. The strongest current suspect is production
decoder tolerance because Stage 1 independently reproduced an advantage on
identical IQ. Stage 2 alone cannot completely separate the production dongle,
USB/host path, and decoder.

A follow-on same-production-source shadow experiment completed on 2026-07-26.
Across three qualifying natural Cleveland collapses, exact samples from the
production receiver remained continuously decodable by OP25 while production
Trunk Recorder collapsed. A subsequent fresh replay through the exact
production Trunk Recorder binary did not reproduce the sustained live
collapse. The waveform alone is therefore insufficient to cause the failure;
the remaining boundary is long-running live sample consumption, scheduling,
or decoder state rather than the RF plant, tuner, USB path, or retained source
samples.

## Pre-experiment conclusion

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

### Result

All four retained IQ/JSON pairs were copied read-only and their hashes matched
the corpus table below. The official boatbod OP25 decoder at commit
`b2e04c3f3f4ace2cd29e3b4cbf6b89e0955c6818` and the exact production Trunk
Recorder lineage were each run three times per input. The runs were
deterministic enough for the following total valid-control-message comparison:

| Trigger | OP25 total | TR median | TR range | OP25/TR |
| ---: | ---: | ---: | ---: | ---: |
| `1784732498025` | 2,513 | 424 | 419-424 | 5.93x |
| `1784732918021` | 1,928 | 908 | 907-909 | 2.12x |
| `1784754546018` | 1,254 | 473 | 473-473 | 2.65x |
| `1784584105012` | 714 | 1,078 | 1,061-1,090 | 0.66x |

OP25 decoded only the correct North Bradley identity: NAC `2AD`, WACN `BEE00`,
system `2A5`, RFSS `2`, site `26`. It produced no foreign NAC. The older
cross-geography capture favored TR, so the result is not a universal OP25
advantage. It is nevertheless a strong and repeated advantage on the three
recent natural failures that motivated this experiment and warranted Stage 2.

The exact production TR binary SHA-256 was
`8141e35ea4be28ba15036755b7287922675e051aaacee8d76fb6549e8f9a9d72`;
its matching library hash was
`115e1c6fbe4dabe85d3b15838b32912ee7631a826653b3c0ac9f73dd2e822588`.
The OP25 and TR result-summary hashes were respectively
`71cce8409e12f27fcd0c7e327782c19a91b0f19485d6255d254862f57025d5bc`
and
`399778713efbc8339761466a07038e1992782a253b8ed5990d186b1f8c217d8c`.
Wall-clock per-second alignment was affected by parallel replay slowdown, so
the raw deterministic totals are the authoritative Stage 1 comparison.

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

### Result

Stage 2 used stable Cleveland rather than tenuous North Bradley. Cleveland was
fixed at `851.050 MHz`, NAC `2A8`, WACN `BEE00`, system `2A5`, RFSS `2`, site
`49`. North Bradley live telemetry was excluded because it did not provide a
trustworthy healthy baseline.

The valid run began at `2026-07-24T18:37:50.558254027Z` and ended after the
event gate was met at `2026-07-24T20:07:02.496301571Z`. The comparator used:

`rtl_sdr U8 stdout -> stream_u8_to_fc32.py -> OP25 file-source decoder`

The spare RTL-SDR Blog V4, serial `00000002`, ran at 2.4 Msps, nominal gain
`20` (actual `19.7`), and PPM `-1`. It was attached directly to Ventax through
usbipd, not through the production hub or active USB extension.

An initial OP25 counter watched the wrong queue, and OP25's local
`gr-osmosdr` live source degraded the input. All telemetry from those attempts
is excluded. The corrected counter wrapped the trunking callback, and an exact
replay counted all 789 TSBKs. A 20-second raw Cleveland capture decoded 792
TSBKs, or 39.6 messages/second. The corrected continuous stream then sustained
approximately 39-42 valid messages/second before the aligned-event run began.

Five OT episodes were aligned. The first was retained only as context because
its 45-second pre-window already overlapped a degraded lead-in. The other four
had healthy pre-event and recovered post-event boundaries on both paths and
therefore satisfy the completion gate:

| OT event (EDT) | OT event avg/min msg/s | Ventax event avg/min msg/s | OT reacquisitions | Result |
| --- | ---: | ---: | ---: | --- |
| 15:46:38-15:48:50 | 8.11 / 1 | 33.73 / 21 | 3 | Qualifying divergence |
| 15:50:08-15:56:23 | 15.04 / 0 | 33.74 / 12 | 2 | Qualifying divergence |
| 15:58:38-15:58:41 | 0 / 0 | 36.00 / 33 | 1 | Qualifying divergence |
| 16:04:08-16:04:56 | 10.50 / 0 | 34.39 / 15 | 2 | Qualifying divergence |

Ventax did not collapse with OT in any qualifying event. The source log
contained normal tuner setup and the expected user-cancel message from the
intentional shutdown, with no USB or sample-delivery error. The aligned
analysis SHA-256 was
`cdbe57c1aa7b916ff316ecf794dd11763846728bd280c27e9345565ee76366ec`.
The final live, source, and manifest log hashes were respectively:

- `58c844bf7a00535cdb25e5038498f0bceac9deac15fd55537ed98ed27f56e4c0`
- `7939f7f39701964a6d0e4ae3d3349d8d64fe4c678c1a494d0e19512235a1b6a5`
- `66482374f07ce0e6d33bda7519ef048574acf4bd28fafff684d17ace0ea47f0e`

Production Trunk Recorder restarted itself at 14:35:47 EDT after a Gardner
block error, before the valid Stage 2 start at 14:37:50. It did not contaminate
the valid interval. This experiment did not modify or restart production
Trunk Recorder or PizzaWave.

### Topology

Keep the antenna and multicoupler common, then separate everything practical:

`MFBW7463 -> 15 ft LMR-200 -> MCA208M`

- Production branch:
  `existing MCA output -> existing RTL-SDR -> Atolla hub -> active extension -> OT`
- Comparator branch:
  `different MCA output -> BNC-male/SMA-male adapter -> SMA female/female coupler -> 15 ft LMR-240 (SMA-male to N-male) -> N-female/SMA-male adapter -> separate matching RTL-SDR -> direct Ventax USB`

The comparator branch above records the actual field topology. The adapters
and 15-foot cable were not the intended short-jumper topology, but the
independent path's sustained 39-42 messages/second and survival through four
production failures show that this passive path was not the cause of the
observed divergence.

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
- Use a stable site with a trustworthy healthy baseline. Cleveland was selected
  for this run; North Bradley was excluded as too tenuous.
- Disable alternate-channel hunting and traffic following on the comparator.
- Synchronize OT and Ventax clocks with NTP and record measured clock offset.
- Record per-second valid control frames, NAC/site identity, received power,
  sample integrity, tuner/USB errors, and CPU.
- Do not judge the paths by RSSI alone. Simulcast failure can occur with almost
  unchanged total power.

There is no minimum elapsed-time requirement. Run until three cleanly aligned
natural failure-and-recovery events are captured on a stable site. Count an
event only when both paths have healthy pre-event boundaries and recovered
post-event boundaries. A sustained collapse is incomplete until recovery.

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

## Stage 3: same-production-source shadow

### Result

The remaining receiver-path confound was removed with a bounded,
non-persisting Cleveland shadow experiment whose capture phase ran from
2026-07-25 08:07 EDT through 2026-07-26 03:46 EDT. Production Trunk Recorder
and a passive Trunk Recorder
shadow consumed the existing source-2 sample stream from RTL-SDR serial
`00000001`. The existing collapse flight recorder retained the exact 96 ksps
complex samples around natural production failures. Those samples were then
replayed three times through the clean boatbod OP25 checkout at commit
`b2e04c3f3f4ace2cd29e3b4cbf6b89e0955c6818`.

Only Cleveland `collapseCapture` and `collapseShadow` were enabled. The
experiment did not change RF settings, gain, centering, control channels,
persistence, recovery policy, or PizzaWave. The production binary SHA-256 was
`8141e35ea4be28ba15036755b7287922675e051aaacee8d76fb6549e8f9a9d72`.
The experiment config SHA-256 was
`b31cc0467a753625e0e9146ee6210871f75944c759fa84cdd66342d5a59b25ad`;
the exact pre-experiment config was preserved at
`/var/backups/pizzawave/ot-cleveland-same-source-20260725T120601Z-config.json`
with SHA-256
`6b1b2d1f36e4473f5809fd23a159617d0380f288b1839a346dfe621ef56f0bac`.
At `2026-07-26T08:41:20Z`, that exact backup was restored with
`root:trunk-recorder` ownership and mode `0640`; Trunk Recorder was restarted
once. The live config hash again matched the pre-experiment hash, both
`trunk-recorder` and `pizzad` were active, Cleveland resumed at up to 40 valid
messages/second, and PizzaWave reported current live Trunk Recorder activity.
The overall health endpoint remained degraded only because of the pre-existing
stale incident-analysis backlog, not live ingestion.

Three natural events met the fixed gate: a healthy production pre-boundary, a
production collapse to at most three messages/second, a recovered production
post-boundary, complete finite samples, the correct 851.050 MHz Cleveland
primary, and three Cleveland-specific OP25 replays.

| Trigger (Unix ms) | Production and passive-shadow behavior | OP25 valid messages per run | OP25 pre/post avg; min msg/s | Result |
| --- | --- | ---: | --- | --- |
| `1785008526013` | Production reached 0 while passive shadow was 39, then production recovered | 3342 / 3342 / 3342 | 36.8 / 37.3; 15 / 21 | Qualifying divergence |
| `1785030138014` | Production fell to 1 and recovered; passive shadow remained at 0-1 | 3385 / 3385 / 3385 | 37.8 / 37.517; 12 / 24 | Qualifying divergence |
| `1785051903013` | Both Trunk Recorder decoders fell to 0-1; production recovered while passive shadow remained at 0-1 | 3327 / 3327 / 3327 | 36.9 / 37.0; 15 / 21 | Qualifying divergence |

Every replay decoded only NAC `2A8`, WACN `BEE00`, system `2A5`, RFSS `2`,
site `49`, with no foreign NAC. Each 90-second input contained exactly
8,640,000 finite, nonzero complex samples with no adjacent repeats. Remote and
local hashes matched. The qualifying input JSON/IQ hashes and aligned-analysis
hashes were:

| Trigger | Metadata SHA-256 | IQ SHA-256 | Aligned analysis SHA-256 |
| --- | --- | --- | --- |
| `1785008526013` | `9fd4ead86dc1bde39bd2bc9ee2b7337b82d4b2280ab07b3c82d615cb4d751b8c` | `2481dc8c42216b6675a8b49085291d879d82ec88f3218492d7d630ae9c4e53fb` | `e75f0969ac1e1bf3fc7d870ccd7e4b4cb9684165bc43b2bd74511606c20c9f04` |
| `1785030138014` | `78052b9fe6c2d3fe3fe26e51b5fe9435cc84f10cd306f8dc984da037e6ee90ce` | `c14b7b6149033adc0f098bd958f78bb080996d69f7645d15f60499c60f18ed3e` | `a19285cb928c7347ad1f89412f1b4511ca878d65a05bc6b29d8f17a4748b5476` |
| `1785051903013` | `8db204acc19b62a20c503e149aa1f0b84a5f0eaa0979102e1cc1c69d3c81d6b5` | `afa79d86736a42921419364a1be8cb81ca499042f4b929611a33da008b89942d` | `de8faf7ae621269676f9596189017937165377fc27c095ef988aec02e8122b89` |

An earlier contextual capture, trigger `1784984637021`, was excluded from the
formal gate because its pre-window lacked a healthy production boundary. It
nevertheless produced 3350 valid OP25 messages on each of three runs while
both Trunk Recorder paths averaged only about 4.8 messages/second before the
trigger. Its aligned-analysis SHA-256 was
`879aa291ab48328978112c32c7cdb236403d15fccc912c955558d93a4c105d38`.

### Interpretation

The same branch-point samples present while production Trunk Recorder fell to
zero or one message/second supported roughly 37 valid Cleveland
messages/second in OP25 on all three qualifying events. The deterministic
totals and correct identity
exclude replay randomness and foreign-system acquisition. Because the samples
came from the production receiver after its RF, tuner, USB, and narrow-channel
path, none of those upstream components is sufficient to explain these
captured collapses. The capture proves that clean samples were available at
the branch point; it does not prove that the long-running live decoder consumed
every sample without a downstream gap or backpressure event.

The passive Trunk Recorder shadow also alternated between healthy and pinned
0-1 states while consuming the same source. In the first event it stayed
healthy when production failed; in the next two it remained pinned while
production recovered. This makes decoder state/history or implementation
tolerance the best-supported ownership boundary. Recovery grace remains a
useful secondary hardening measure only after the initial decoder failure is
remediated; it is not the root-cause fix indicated by this evidence.

### Fresh production-binary replay

The three qualifying Cleveland files were then replayed three times each
through a newly initialized, isolated process using the exact production Trunk
Recorder executable and decoder library. Their SHA-256 values were
`8141e35ea4be28ba15036755b7287922675e051aaacee8d76fb6549e8f9a9d72`
and
`115e1c6fbe4dabe85d3b15838b32912ee7631a826653b3c0ac9f73dd2e822588`.
The finite `osmosdr` file source ran at 96 ksps with `repeat=false` and
`throttle=true`; the only configured control channel was Cleveland
`851.050 MHz`. No production process or configuration was touched.

| Trigger | Fresh TR valid messages per run | OP25 valid messages per run | Fresh TR as percent of OP25 | Longest fresh-TR interval at 0-3 msg/s |
| --- | ---: | ---: | ---: | ---: |
| `1785008526013` | 2666 / 2666 / 2666 | 3342 / 3342 / 3342 | 79.8% | 1 second |
| `1785030138014` | 2890 / 2890 / 2890 | 3385 / 3385 / 3385 | 85.4% | 1 second |
| `1785051903013` | 2464 / 2464 / 2464 | 3327 / 3327 / 3327 | 74.1% | 1 second |

Every fresh-TR run stayed on the primary, decoded the correct Cleveland
identity, consumed the finite input without a reported underrun, overrun, or
dropped sample, and ended with the expected file-source EOF status. Totals
were identical across repetitions. The first run's one-second telemetry phase
differed slightly from runs two and three, so total counts and longest low
intervals are authoritative; a wall-clock split at exactly 30 seconds is not.
The analysis and manifest SHA-256 values were respectively:

- `5a42bedccbb8ded34c5d5d4845d74b506dcf961c87a68def21575264cb2ecebf`
- `904bd5a561c40f6ed4f445b89193b23317b7c1dffd2d26cf9575d8da532d2ae9`

Fresh Trunk Recorder did not reproduce the sustained live pinned state. It
decoded across every recorded trigger and never remained at 0-3
messages/second for more than one second. OP25 retained a material throughput
advantage, showing greater demodulation margin, but that difference does not
establish that the TR decoder algorithm alone caused the live outages. The
result instead requires a live-only condition: accumulated state, failure to
consume samples available at the branch point, or scheduling/backpressure
between that point and valid-frame output.

### Live pipeline instrumentation

The next experiment instrumented the exact long-running Trunk Recorder path
without changing demodulation or recovery behavior. Private experiment commit
`7f33d46aaecc0c095316cfe52dcab1d3eef7088f` added cumulative counters for
source input, channelized output, timing-recovery output, sliced symbols,
frame-assembler input, and the parallel capture branch. Before deployment, a
real-time replay of Cleveland trigger `1785008526013` produced 2669 valid
messages versus 2666 in the preceding uninstrumented fresh replay. Live and
passive-shadow counters were identical throughout that validation.

The instrumented build then ran on OT from 2026-07-26 15:40:35 through
16:40:35 EDT. The fixed journal artifact is
`C:\temp\tr-pipeline-full-hour.log`, SHA-256
`896b01d4f3c41b8735eaffd0ae3a4abc01c061a95422234cebdc0b4a16e23453`.
It contains 3595 one-second Cleveland samples. The hour did not contain a
sustained Cleveland collapse: the median live rate was 30 messages/second,
29 samples were zero, and no consecutive interval at 0-3 messages/second
lasted longer than two seconds.

The counters nevertheless exposed a repeatable live-only disturbance. The
Cleveland capture, live-decoder, and passive-shadow counters reset together
369 times even though Cleveland remained on 851.050 MHz. Of those resets, 363
occurred 0.7-1.2 seconds after another system changed RF source during a
control-channel retune: 341 followed North Bradley and 22 followed Hamilton.
North Bradley alone attempted 528 retunes during the hour, including 348 that
changed source. Cleveland commonly fell from its healthy 39-42 messages/second
range to 0-15 immediately after those graph changes, then recovered.

The implementation explains the coupling. A P25 retune covered by a different
source locks the shared GNU Radio top block, disconnects and replaces the
retuning system's P25 block, reconnects it, and unlocks the shared graph. The
instrumentation shows that this operation reconstructs scheduler accounting
for unrelated Cleveland blocks and briefly degrades Cleveland decoding. All
instrumented stages continued to advance during the observed short low-rate
intervals, so the hour did not show a persistent input-consumption stall or
identify one internal demodulator stage as the origin.

This result corrects the narrower early observation that every reset followed
North Bradley: the complete hour shows that cross-source retunes by either
tenuous system cause the shared-graph disturbance. It does not yet prove that
the short disturbance is sufficient to create the previously observed
sustained Cleveland pinned state, but it establishes a concrete mechanism by
which unreachable alternate control channels at one site perturb a healthy,
unrelated site.

After the fixed hour, OT was restored to the exact pre-test executable,
decoder library, and configuration. Their SHA-256 values are respectively
`8141e35ea4be28ba15036755b7287922675e051aaacee8d76fb6549e8f9a9d72`,
`115e1c6fbe4dabe85d3b15838b32912ee7631a826653b3c0ac9f73dd2e822588`,
and `6b1b2d1f36e4473f5809fd23a159617d0380f288b1839a346dfe621ef56f0bac`.
Trunk Recorder, PizzaWave, and Cleveland identity/rate checks passed after
restoration.

### Cross-source hang and source-affinity guard

On 2026-07-27, the production Trunk Recorder process stopped producing RF
telemetry while its systemd unit remained active. PizzaWave therefore showed
`Waiting for RF samples`; PizzaWave itself was healthy. The last live activity
was at approximately 13:48:18 EDT, immediately after North Bradley moved from
source 0 to source 1 for learned control channel 772.381250 MHz while Hamilton
also moved between sources. A live debugger showed Trunk Recorder's main
thread blocked in `gr::top_block_impl::restart()` while joining a GNU Radio
worker thread. Restarting only Trunk Recorder at 23:33 EDT restored RF samples
and the Setup display.

Private experiment commit
`dd326fe2` added an opt-in per-system `sourceAffinity` guard. The default is
`false`, preserving existing Trunk Recorder behavior. When enabled, a control
channel inside the system's assigned source remains eligible, but a configured
or learned channel requiring another source is skipped without disconnecting,
replacing, or reconnecting GNU Radio blocks. Telemetry records both
`sourceAffinityEnabled` and `sourceAffinityBlocked`.

The guarded OT binary and configuration were activated at 23:42:05 EDT on
2026-07-27. Their SHA-256 values are respectively
`f66b554754d923f28bfa0913258b9ee338b23e56d5155162e6b5252796aada1e`
and `f4ca38352f6346d0a80bd179bf5560689629f1fe4273e98131e2fee2c83fd0a5`;
the decoder library remained
`ac5d231a4a7a7663497e7555b0d4c90be72232c60920f5445ab2bdbd48bce081`.
The exact pre-test config, binary, and library are preserved under
`/var/backups/pizzawave/source-affinity-20260728T033837Z`. One contaminated
setup attempt at 23:39:03 EDT wrote an empty config because of command quoting;
Trunk Recorder rejected it, the exact config backup was restored, and normal
decoding was verified before the valid deployment.

The fixed 324-second validation window from 23:42:11 through 23:47:35 EDT is
preserved locally as
`C:\temp\pizzawave-rf\ot-source-affinity\ot-source-affinity-20260728T034211Z-034735Z.log`,
SHA-256
`87942b1cad1d87e447fea94cc4e89c84c6bfd36e467f81736d7c6d987f3d6d1b`.
The guard blocked 24 North Bradley and 21 Hamilton cross-source attempts,
including repeated North Bradley attempts to use 772.381250 MHz. No
cross-source move succeeded. Across 324 one-second shadow samples for each
instrumented system, source-input counters advanced monotonically with zero
pipeline resets. Cleveland produced 108 sampled rates averaging 39.90
messages/second (minimum 29, maximum 40), with no zero-rate sample, no retune,
and no service restart. Trunk Recorder and PizzaWave remained active, the live
RF endpoint continued to populate, and `RF waiting` did not recur.

This short controlled window strongly confirms that preventing cross-source
control-channel moves removes the previously measured shared-graph resets. It
does not yet establish a permanent product policy: source affinity trades
access to learned control channels outside the assigned source for isolation
of unrelated systems. The guarded build remains active on OT for extended
observation.

The extended observation through 16:59:20 EDT on 2026-07-28 covered 17 hours
17 minutes without a Trunk Recorder or PizzaWave restart. Its filtered journal
artifact is
`C:\temp\pizzawave-rf\ot-source-affinity\ot-source-affinity-overnight-20260728T205920Z.log.gz`,
SHA-256
`0e4d798f728c9a008b4b58892aa7cf16e8da6089b96d7528389588f0e4820745`.
The guard blocked 4496 North Bradley and 1802 Hamilton cross-source attempts;
zero cross-source move succeeded. North Bradley and Hamilton each provided
more than 62200 one-second pipeline samples with zero counter reset. Their
mean live rates were 5.89 and 14.19 messages/second respectively.

Cleveland provided 20755 sampled rates averaging 39.991 messages/second. Only
two samples were zero, each isolated; the longest interval at 0-3
messages/second was one sample. Those two samples caused Cleveland to rotate
between its two configured control channels within source 2, not to change
sources, and did not reset the instrumented shared pipeline. At the end of the
window Cleveland was healthy at 40 messages/second, Hamilton was healthy at
24.25, and North Bradley was recovering at 11.28. The original systemd process
remained active with PID 1350902 and zero automatic restarts. This extended
result upgrades the source-affinity finding from a short mechanistic check to
strong live evidence that cross-source graph reconstruction caused the prior
shared-pipeline resets and scheduler hang.

### Hamilton CQPSK loop exact-sample proof and production trial

On 2026-08-04, the slower CQPSK loop candidate was tested as an opt-in,
per-system Trunk Recorder setting. The candidate used Gardner `gain_mu`
`0.0125`, Costas alpha `0.004`, and omega-relative limit `0.1`; stock values
were `0.025`, `0.008`, and `0.1`. Earlier screening on the same spare dongle
and RF path favored the candidate on Hamilton but did not remove changing-RF
as a sequential-test confound, so it was not sufficient for production by
itself.

The exact-sample proof used an OT-native build based on the exact deployed
source/ABI lineage at commit
`d64f6d3bf7af924aa6c16936ff2b09bc19908d40`. Experiment commit
`cdff4e97` added independently configurable live and passive-shadow QPSK loop
parameters while preserving stock defaults. The installed binary and decoder
library SHA-256 values were respectively
`212e0c06deb03453c1832940b31a9b2a2d8e3a6a7f7aa352fb7461feaa84e8f8`
and
`ac5d231a4a7a7663497e7555b0d4c90be72232c60920f5445ab2bdbd48bce081`.
An initial binary built from the wrong older lineage failed at plugin startup;
the automatic health gate restored the prior executable and config. That
startup failure is excluded from RF evidence and reinforces the requirement to
build experiments on the exact production ABI lineage.

The passive phase kept Hamilton production live decoding on stock settings and
ran the tuned passive shadow from the identical source samples. A strict
same-IQ gate accepted only seconds where both paths were on 855.2125 MHz and
their source-input and channelized-sample counters matched. Across 109 accepted
seconds, stock live averaged 13.25 messages/second and tuned shadow averaged
15.56; tuned won 62 paired seconds, stock won 29, and 18 tied. Stock recorded
two zero seconds and a four-second longest run at 0-3 messages/second, compared
with one zero and a two-second longest low run for tuned. In the neutral subset
where the pair mean was at most 10, the paths were effectively tied (5.55
stock versus 5.39 tuned), so this phase supported a bounded trial rather than a
universal decoder conclusion. The filtered evidence and analysis SHA-256
values are respectively
`821060ed85df6fc35865cab426c39f970043f16781d3b40b87415b90833a8dd6`
and
`df9f90dbcc451f8aab5176054c7d2f9c2ff84b10adf45df45004ff15bede82c1`.

The bounded production trial ran for 60 minutes beginning at 14:12:09 EDT.
Only Hamilton live decoding used the tuned loop; its passive shadow remained
stock. The same-IQ primary-channel gate accepted 1269 seconds. Tuned live
averaged 22.35 messages/second versus 19.56 for stock shadow and won 796 paired
seconds; stock won 177 and 296 tied. Tuned had 23 seconds at 0-3 versus 41 for
stock, with longest low runs of one and three seconds respectively. In the
neutral pair-mean-at-most-10 subset, tuned averaged 7.63 versus 6.32 for stock.
Across all Hamilton production telemetry during the hour, 241 samples averaged
19.68, the minimum was 4, and none were at or below 3. Cleveland remained
stable across 239 samples at mean 39.97 and minimum 39. Trunk Recorder and
PizzaWave remained active with zero restart or fatal event, and Cleveland,
Hamilton, and North Bradley identities remained correct. The full journal,
paired analysis, and system analysis SHA-256 values are respectively
`537d7c480778d51b2d7d25ab370490ea96db4445c2944a05f805236aba6693bc`,
`a72d8e9541cd40831131c4063782bbc9f2a21ebf9bd77249bd54ecec0e3196a7`,
and
`3542eeb984277f38dd2e1135ce7b9f0a61de9ade799fb0c7fb4b9e80059c330a`.

The trial supports retaining the slower loop for Hamilton only. It does not
support changing Trunk Recorder defaults, applying the values to North Bradley
or Cleveland without their own exact-sample gates, or replacing Trunk Recorder
with OP25. Source affinity remains a separate necessary protection against
cross-system graph reconstruction. Exact rollback state is preserved under
`/var/backups/pizzawave/hamilton-loop-shadow-20260804T180000Z` and
`/var/backups/pizzawave/hamilton-loop-production-20260804T181200Z`; local
evidence is under
`C:\temp\pizzawave-rf\ot-hamilton-loop-production-trial`.

The retained production setting then completed its first overnight observation
from 14:12:10 EDT on 2026-08-04 through 12:55:37 EDT on 2026-08-05. The strict
same-IQ, same-primary-channel gate accepted 49813 paired seconds. Tuned live
averaged 12.17 messages/second versus 9.67 for the stock passive shadow and won
29768 seconds; stock won 11129 and 8916 tied. Tuned recorded 102 zero seconds
and 7164 seconds at 0-3 messages/second, compared with 168 and 10164 for stock.
Both paths had an eight-second longest low run. In the neutral
pair-mean-at-most-10 subset, tuned averaged 6.48 versus 5.09 for stock. The
unfiltered RF-analysis window was worse than the preceding equal window, so
the slower loop is a mitigation rather than a cure for the nighttime channel
impairment. The compressed journal and paired-analysis SHA-256 values are
respectively
`5bb398e972b214ea339c3de0433d05fc07595ef442d08bd323a2e49380333e99`
and
`00579587a2b1afd407c648b20fe61ec1346102e7190284aa9f1f28aa542b0e7a`.
This overnight result removes the remaining Hamilton observation gate and
supports retaining the site-specific setting.

## Purchase gate

Buy nothing before Stage 1.

Stage 2 used the available spare matching RTL-SDR, an unused MCA208M output,
the documented 15-foot LMR-240 adapter chain, and direct Ventax USB. Do not buy
another antenna, preamplifier, attenuator, filter, or multicoupler from the
resulting evidence.

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
| OT antenna facts, no-purchase decision, Ventax host suitability, two-stage variable isolation, independent USB topology, results, and acceptance matrix | This experiment record |
| Exact-production-source shadow method, three-event gate, OP25 replays, fresh production-binary TR replay, live pipeline counters, and cross-system retune coupling | This experiment record |
| Hamilton same-dongle screening, exact-sample stock-versus-tuned shadow proof, bounded production trial, retained site-specific setting, and rollback lineage | This experiment record |

The field log now also records previously omitted North Bradley capture
`1784732918021` with source-verified hashes. Routine healthy monitor polls and
known PizzaWave-only deployment gaps are not individually duplicated because
they produced no RF result; the permanent record retains the relevant health,
restart, quota, and exclusion conclusions.

## Next step

Retain Hamilton's opt-in slower loop. Run the next site-specific gate on North
Bradley with production left on stock loop values and only its passive
same-sample shadow using `gain_mu=0.0125` and Costas alpha `0.004`. Observe one
complete natural nighttime deterioration and recovery, and promote those values
only if the tuned shadow materially reduces low and zero decoding without
damaging healthy periods. In parallel, promote the per-system loop settings and
source-affinity guard as separate reviewed Trunk Recorder changes with
configuration documentation and focused tests. Do not change global loop
defaults. PizzaWave owns ensuring that every control channel provided during
Setup fits the site's chosen source; Trunk Recorder owns safely handling
control channels it later discovers at runtime. Do not restore the demonstrated
cross-source graph-rebuild hazard merely to repeat it. These changes are not a
retune grace period and do not replace Trunk Recorder's P25 decoder.

## Trunk Recorder source access from Ventax

The Paxan-local Trunk Recorder experiment branches are published in the private
[trunk-recorder-paxan-experiments](https://github.com/lilhoser/trunk-recorder-paxan-experiments)
repository. This avoids copying a local working directory and keeps the
experimental branches away from upstream `TrunkRecorder/trunk-recorder`.

Clone it on Ventax:

```powershell
git clone https://github.com/lilhoser/trunk-recorder-paxan-experiments.git C:\projects\trunk-recorder-paxan-experiments
git -C C:\projects\trunk-recorder-paxan-experiments branch -r
```

For the exact OT deployed experiment lineage, check out:

```powershell
git -C C:\projects\trunk-recorder-paxan-experiments switch -c ot-collapse-rearm origin/codex/collapse-auto-rearm-ot
git -C C:\projects\trunk-recorder-paxan-experiments rev-parse HEAD
```

The expected full commit is
`7e03a80e23be94c75553f155d55fb84acb7b03c6`. It contains the OT RF telemetry,
triggered narrow recorder, fixed-primary shadow, and restart-persistent capture
quota used by this investigation.

The offline decoder-reference branch is
`codex/raymond-demod-replay` at
`1ecc8551919689a07823300985b3a074c6e0b424`. It contains file-source P25 loop
overrides and the separated control-channel modulation experiment, but it is
based on a different Trunk Recorder source lineage. Inspect or port its focused
changes; do not merge the whole branch into the OT lineage without reconciling
the base difference.

The private repository README lists every published branch, exact commit,
purpose, and lineage boundary. The archive contains source and Git history
only. Production configurations, binaries, IQ files, credentials, and host
backups remain excluded.

## Completion criteria

The OT experiment is ready for a hardware or software recommendation only when
one of these is true:

- the same-IQ corpus shows a repeatable independent-decoder advantage without a
  healthy-window regression;
- three aligned live events show both independent receiver paths fail together;
- three aligned live events show one path repeatedly fails while the other
  stays healthy, localizing the problem downstream of their common split;
- crossover testing localizes a repeatable difference to an MCA branch,
  dongle, host/USB path, or decoder;
- reproducible foreign NAC/site evidence identifies a co-channel source.

The first and third criteria are now satisfied, and both the
same-production-source shadow discriminator and Hamilton's exact-sample loop
comparison have completed. The fresh production-binary replay did not
reproduce the sustained live failure, so replacing the control-channel decoder
is not supported. Live instrumentation instead found a shared-scheduler
disturbance: cross-source retunes for one system repeatedly reset accounting
and depress decoding in an unrelated healthy system. Source affinity addresses
that boundary. Separately, Hamilton's same-IQ proof and 60-minute production
trial support its retained, site-specific slower CQPSK loop. They do not
support a global default change. Gain, filtering, or recovery grace as the
primary general remedy remains unsupported.

## External references

- [PCTEL fiberglass base-station omnidirectional antennas](https://pctel.com/antenna-product/fiberglass-base-station-omnidirectional-antennas/)
- [DigiKey MFBW7463 product page](https://www.digikey.com/en/products/detail/amphenol-pctel/MFBW7463/13687599)
- [SDRTrunk releases and platform requirements](https://github.com/DSheirer/sdrtrunk/releases)
