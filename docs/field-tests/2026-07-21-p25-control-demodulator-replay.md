# P25 Control-Channel Demodulator Replay

Date: 2026-07-21

Host: RPI (`sdr1861`)

System: Raymond/Hinds (`etv-raymond-hinds`)

Status: offline replay and control-path separation complete; live confirmation pending

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

Trunk Recorder commit `73fd134b` adds an optional per-system
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

## Next steps

1. During a strong Raymond baseline, deploy the validated experimental binary
   to RPI,
   enable FSK4 only for its control channel, and require immediate healthy
   control decode plus successful Phase 2 call recording. Roll back at once if
   either gate fails.
2. If the gates pass, hold that single change through the next natural Raymond
   event and compare outage depth/duration with the three retained CQPSK
   captures. Restore the prior binary and configuration after one conclusive
   event unless the result is clearly superior and operationally safe.
3. If live FSK4 still collapses, stop decoder tuning and test the existing
   BPF-800-M on RPI as the next hardware discriminator; do not add an antenna.
