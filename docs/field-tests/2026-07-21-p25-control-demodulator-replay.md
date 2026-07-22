# P25 Control-Channel Demodulator Replay

Date: 2026-07-21

Host: RPI (`sdr1861`)

System: Raymond/Hinds (`etv-raymond-hinds`)

Status: complete; live candidate rolled back after one natural event

## Question

Do Raymond's collapses begin in the captured RF samples, or does the current
CQPSK control-channel demodulator turn recoverable modulation distortion into a
larger outage?

The gain experiment already rejected simple Airspy overload: gain 12 damaged a
healthy signal, gain 14 admitted another natural collapse, and gain 15 was
restored. This replay therefore held the RF samples, frequency, decoder state,
and observation window constant while changing only the control-channel
demodulator or one CQPSK loop parameter.

## Inputs and method

Three retained Raymond narrow-IQ captures were replayed from a file source:

| Capture | Live condition represented |
| --- | --- |
| `1784609251021` | paired live/shadow collapse |
| `1784635892019` | natural gain-15 collapse |
| `1784639657025` | natural gain-14 collapse |

Each file is 90 seconds long: 30 seconds before the automatic trigger and 60
seconds after it, at 96,774.193548 complex samples per second. The decoder was
pinned to the 773.781250 MHz primary so alternate-channel retunes could not
contaminate the comparison. Each final candidate ran three times. Evidence was
counted in the first ten three-second pre-trigger windows and the following
twenty post-trigger windows; the trailing end-of-file status sample was
excluded.

All 27 final comparison runs stayed on the primary and decoded the correct
identity: system `2AD`, WACN `BEE00`, NAC `2A4`.

The first loop screen was discarded because its intermediate replay binary did
not contain the retune-grace support and therefore did not stay fixed on the
primary. None of its numbers are used below. It remains only as an invalidated
artifact under the experiment directory.

## Decoder-family result

The existing FSK4 control decoder beat the existing CQPSK decoder on every
capture, both before and after the trigger.

| Capture | Decoder | Total valid messages | Pre-trigger | Post-trigger | Post-trigger zero windows |
| --- | --- | ---: | ---: | ---: | ---: |
| `1784609251021` | CQPSK | 475 | 254 | 223 | 7 |
|  | FSK4 | 691 | 379 | 312 | 3 |
| `1784635892019` | CQPSK | 598 | 274 | 324 | 1 |
|  | FSK4 | 854 | 389 | 465 | 0 |
| `1784639657025` | CQPSK | 260 | 133 | 126 | 16 |
|  | FSK4 | 578 | 221 | 357 | 5 |

Values are medians of three runs. Total-message ranges were only 4 messages
wide for FSK4 on the first two captures, 18 on the third, and at most 6 for
CQPSK. The result is therefore repeatable, not a favorable single run.

Compared with stock CQPSK, FSK4 produced 216, 256, and 318 more valid messages
across the three captures. Its post-trigger advantage was 89, 141, and 231
messages, respectively. It also improved the nominal pre-trigger portions by
125, 115, and 88 messages. That last result matters: the gain is not purchased
by making healthy reception worse.

## CQPSK loop screen

The same-IQ loop screen changed only the Gardner timing gain and Costas carrier
gain around the current CQPSK defaults:

- stock: timing `0.025`, carrier `0.008`;
- half timing: timing `0.0125`, carrier unchanged;
- double timing, half/double carrier, and combined slow/fast variants.

Doubling the timing and/or carrier gains was consistently harmful. Halving the
timing gain was the one robust single-parameter improvement. Three-run
confirmation produced:

| Capture | Stock CQPSK total | Half-timing CQPSK total | Half-timing pre/post |
| --- | ---: | ---: | ---: |
| `1784609251021` | 475 | 535 | 268 / 267 |
| `1784635892019` | 598 | 692 | 314 / 378 |
| `1784639657025` | 260 | 310 | 165 / 144 |

The half-timing candidate is a real CQPSK improvement, but it still trails
FSK4 by 156, 162, and 268 total messages. FSK4 also retains the larger
post-trigger yield on every capture. Adjusting the current CQPSK loop is
therefore useful secondary work, not the strongest stabilization candidate.

## Conclusion

The physical RF at both sites is experiencing channel-local modulation
distortion, with dynamic simulcast/multipath still the leading explanation and
exact co-channel interference the remaining plausible RF alternative. The
same Raymond IQ also establishes a second, software-side contributor: the
current Trunk Recorder CQPSK control decoder is substantially less tolerant of
that distortion than its existing FSK4 control decoder.

This is not evidence that the samples are clean or that software creates the
initial event. It is evidence that the current decoder amplifies a real,
recoverable RF impairment into a longer or deeper decode outage. Gain changes
cannot fix that behavior.

Changing the system-level `modulation` setting to `fsk4` is not a safe live
test. Trunk Recorder uses that same setting for traffic recorders and explicitly
rejects Phase 2 calls when FSK4 is selected. The live discriminator must switch
only the control-channel decoder while leaving the Phase 1/Phase 2 call path on
QPSK.

## Artifacts

RPI artifact root:

`/var/lib/pizzawave/rf-surveys/manual/20260721T-rpi-raymond-demod-loop-replay`

Important retained artifacts:

- fixed decoder-family logs: `matrix/logs/`;
- decoder-family analysis: `analysis.json`, SHA-256
  `fe193bb007f47b5faa5a16432492c04959df4627b73d05b7b69c99dc848c4546`;
- valid fixed-primary CQPSK screen: `loops-fixed/logs/`;
- screen analysis: `loops-fixed-analysis.json`, SHA-256
  `82c4830ef52f69991d930cbe99b1bf68c42cc03094ff6ac6de1f12ed201523d6`;
- three-run half-timing confirmation: `winner/logs/`;
- winner analysis: `winner-analysis.json`, SHA-256
  `d01032e3c3df345089d2a1e1db95f97ec01e6fcfe8cbeb6213694b5d7094b9a9`;
- replay binary: `trunk-recorder-loop-replay-grace`, SHA-256
  `6356f39185011663852f0bce2f2fd1989ab40f20cd5b3b6cd0323f954e8390ba`.

The reusable analyzer is
`scripts/analyze_p25_demod_replay.py`. Replay-only CQPSK loop overrides remain
isolated in Trunk Recorder branch `codex/raymond-demod-replay`; they are not
merged or deployed.

## Control-path separation gate

Trunk Recorder branch `codex/rpi-control-fsk4` at commit `4bb829fd` adds an
optional per-system
`controlChannelModulation` setting. The existing `modulation` setting remains
unchanged and continues to select the traffic recorder. Both initial setup and
control-channel graph reconstruction use the new control-only value.

The candidate was built on RPI and replayed once across all three retained
captures with `modulation: qpsk` and `controlChannelModulation: fsk4`. Each log
reported both settings distinctly, stayed fixed on 773.781250 MHz, and decoded
the correct `2AD` / `BEE00` / `2A4` identity. Valid-message totals were 693,
855, and 580, matching the earlier FSK4 ranges. Live TR remained at PID 445238
with zero restarts throughout the parallel file-source runs, and PizzaWave
activity remained current.

Additional artifacts:

- control-only logs/configs: `control-only/`;
- control-only analysis: `control-only-analysis.json`, SHA-256
  `8a5e51186e2f8a52e87c1a915ad098c0869b0c029e1e9303652fc506c89a8a87`;
- control-only candidate binary, SHA-256
  `a830c62a32a72596e79b9d8573d4dac29bbc4e1ad45c8605f36d392543e6ed45`.

## Live deployment lineage and gate

Two initial live candidates were rejected because they were built from the
newer retune-grace source lineage rather than the exact older source used by
RPI. The first aborted in `Call_Stream::find_callstream(Call*)` with
`std::bad_alloc`. Moving the new virtual methods to the end of the interface
removed that immediate mismatch, but the second aborted in
`Call_Stream::make_rf_event/system_rates` while calling
`Source::get_driver()`. Both failures occurred only after loading the installed
callstream plugin, and both were immediately restored from the exact rollback.
They are plugin ABI failures, not RF or decoder results.

The final candidate was rebased onto RPI's exact pre-wide source lineage at
`c923e02c`. The live rollback binary was reproduced byte-for-byte before the
control-only patch was applied. An offline 90-second run then loaded the
installed callstream plugin, emitted 32 accelerated `PIZZAWAVE_RF` callbacks,
processed four real grants, and reached the expected end of the file without
an allocation failure. Its retained artifacts are under the experiment root's
`rpi-lineage/` directory.

The exact-lineage candidate was activated at Unix ms `1784648415522`
(2026-07-21 11:40:15 EDT):

- live binary SHA-256:
  `945bdcc91c882e4434902ea36fbc8f1356964ec64fb18d3c272b8adaf6cbda59`;
- live config SHA-256:
  `715af0a75af6e604a82be78216cf1e02fd786d467abb247db7129a4802e77f5e`;
- Raymond: `modulation: qpsk`, `controlChannelModulation: fsk4`, Airspy LNA
  gain 15;
- exact rollback:
  `/var/backups/pizzawave/rpi-control-fsk4-20260721T1101EDT`.

The operational gate passed. TR remained active at PID `500187` with zero
restarts. Raymond stayed on 773.781250 MHz and produced approximately 21-42
valid messages/s. PizzaWave returned to current health after one transient
stale reading, continued receiving periodic RF health, and ingested live
calls. A real Phase 2 recorder started with `TDMA: true` and `QPSK: true`,
confirming that only the control demodulator changed. The candidate was then
held for one natural Raymond event.

## Natural-event result

Capture `1784648731014` triggered at 2026-07-21 11:45:31 EDT and completed at
11:46:31. The 69,677,408-byte IQ file and matching JSON were stable across two
checks. Their SHA-256 values are:

- IQ: `c27b23fe4d63de770ba82bd88a460860f61f1ac8fee1c6bce110dd58c2c0c00d`;
- JSON: `35d3dc83573c921960bc542171ded1d1acf33efb567c5e816649f029499eb2e7`.

This was a real received-waveform event, not a process or sample-delivery
failure. Trigger-aligned live/shadow rates were 1/1 frame/s. Narrow-channel IQ
power at the trigger was 4.93 dB below the healthy pre-event median. Every
sample was finite, with no zero, repeated-adjacent, or clipped samples.

The live FSK4-control process recovered much faster than the three retained
CQPSK-control events: it had 10 post-trigger samples at 0-1 frame/s, made three
alternate-frequency transitions, and sustained at least three primary samples
above 10 frame/s beginning about ten seconds after the trigger. The earlier
events had 31, 45, and 56 post-trigger samples at 0-1 and 8, 14, and 20
transitions. Phase 2 QPSK traffic continued before and after recovery.

That cross-event difference was not enough to credit FSK4, because the new RF
event was not the same as the older events. A fixed-primary replay therefore
ran the new IQ once through both decoder families using the same validated
replay binary and the same 30-second pre/60-second post windows. Both decoded
the correct `2AD` / `BEE00` / `2A4` identity and stayed on 773.781250 MHz:

| Decoder | All valid messages | Pre-trigger | Post-trigger | Post zero / <=1 windows |
| --- | ---: | ---: | ---: | ---: |
| CQPSK | 2,217 | 618 | 1,599 | 1 / 2 |
| FSK4 | 2,161 | 590 | 1,571 | 1 / 2 |

CQPSK produced 56 more messages overall and both decoders had the same number
of deep post-trigger windows. This event therefore does not show a material
FSK4 advantage. Combined with the earlier three-capture result, the bounded
conclusion is that FSK4 is more tolerant of some Raymond impairment shapes but
is not a general solution for every physical RF event.

The candidate was rolled back after the comparison. At 11:59:46 EDT the exact
prior binary and configuration were restored from
`/var/backups/pizzawave/rpi-control-fsk4-20260721T1101EDT`. Verified SHA-256
values are `15824b7f8083d4b824d10d6e06a22b81a75eca93b4a7b8aa8499e76d299e0449`
for the binary and
`6bcb77f651fd76c6036528275f3ec088cf0d56a4667b169d0f47536260facc02`
for the config. TR returned as PID `506742` with zero restarts, both gains at
15, PizzaWave current, primary decode at 28-39 frame/s after a subsequent RF
dip, and a verified Phase 2 recorder with `QPSK: true`.

Fixed-primary replay artifacts are under
`fixed-new-event-1784648731014/`. Config/log SHA-256 values are:

- CQPSK config/log: `a5f25a565dcc9b06ca009818b9da35155ecb64794c5a342c61d9bbfab1849876` /
  `8f7a3b47ff988942a0b527db0296037013abdb1f1efde6f1d0b1a541146e9112`;
- FSK4 config/log: `1314191a7366a6ebb619ce84b6d6d03eb996c4bc35303b517ca1e57eb895dd5f` /
  `39d6de661d616d042ca17c7ececefcb759f70a1563dd4949355408bf3b4976ae`.

## Next steps

1. Keep the exact restored CQPSK binary/config and gain 15 baseline in service.
   Do not deploy FSK4 as the general stabilization fix from this evidence.
2. Test the existing BPF-800-M inline on RPI's Raymond RF path. The
   [published specification](https://www.scannermaster.com/BPF_800_M_p/24-531520.htm)
   gives a 769-872 MHz passband, which includes the 773.781250 MHz control
   channel. This is a direct discriminator for out-of-band/cellular front-end
   stress without adding an antenna.
3. First require that insertion does not materially reduce a healthy Raymond
   baseline. If that gate passes, retain one natural event with the filter and
   compare its raw IQ, integrity, depth, and duration with the four unfiltered
   captures. Persistent same-signature failure would reject out-of-band
   overload and leave dynamic simulcast/multipath or exact co-channel
   interference as the likely physical causes.
