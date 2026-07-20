# P25 retune-state deterministic replay

Date: 2026-07-20 (America/New_York)

## Question

The July 18 Raymond capture remained decodable in a fresh offline Trunk
Recorder process while the live process had been cycling control channels and
reporting sustained zero decode. This experiment tested a foundational
software explanation: does same-source retuning preserve damaged P25 decoder
state that prevents reacquisition?

This is not a receiver, antenna, source-center, or live-time A/B. Both decoder
variants consumed the same stored complex samples in the same order.

## Decoder behavior under test

The baseline was Trunk Recorder `codex/rf-stabilization` at `602a637`.
On a same-source P25 control-channel retune it calls `tune_freq()`, which:

- changes the translating filter offset;
- clears the autotune offset; and
- resets Costas carrier phase and frequency.

It does not reconstruct the channelizer, frequency-locked loop, Gardner timing
recovery, AGC, slicer, or P25 frame assembler.

The experimental variant changed only this path. It disconnected and destroyed
the complete `p25_trunking` graph, constructed a new graph at the requested
frequency, and reconnected it to the same source. No configuration schema,
OP25 implementation, source behavior, message-rate threshold, or production
service was changed.

## Input and method

The experiment ran on RPI against the July 18 degraded Raymond artifact:

- IQ file: `20260718T220326Z-rpi-raymond-live-plan-degraded-iq/replay-20s-fc32.iq`
- format: 32-bit complex float
- duration: 20 seconds
- sample rate: 6 Msps
- source center: 771.931250 MHz
- primary control channel: 773.781250 MHz

Each run began on three known Raymond candidates that do not decode at this
location, then returned to the primary:

1. 773.031250 MHz
2. 773.531250 MHz
3. 773.281250 MHz
4. 773.781250 MHz

With the normal three-second low-decode check, this deliberately exposed the
decoder to approximately nine seconds of noise-only tuning before it reached
the captured primary. `controlRetuneGracePeriod` was zero and
`controlWarnRate` was `-1`.

An initial repeating-file run confirmed that both variants acquired the
primary. That run was not used for the comparison because process/filter
startup time changed the repeating file phase. The measured experiment used a
non-repeating file and alternated baseline and full-reset binaries for five
runs each. Every run therefore reached each channel at the same sample offset
and ended at the same EOF.

Both binaries were built from the same clean source snapshot on the RPI. After
the baseline linked, the one experimental source file was substituted and the
incremental build recompiled only `monitor_systems.cc` before relinking.

## Result

Full P25 graph reconstruction did not materially improve reacquisition.

| Metric | Persistent-state baseline | Full graph reset |
| --- | ---: | ---: |
| Runs | 5 | 5 |
| Correct Raymond site decoded | 5/5 | 5/5 |
| Correct system/WACN/NAC decoded | 5/5 | 5/5 |
| Median primary-to-site latency | 1.413 s | 1.369 s |
| Median primary-to-system latency | 1.867 s | 1.804 s |
| Primary rate samples | 20 | 20 |
| Mean primary decode | 20.00 msg/s | 19.65 msg/s |
| Primary decode range | 10-30 msg/s | 11-31 msg/s |
| Interpolator out-of-bounds messages | 0 | 0 |

The roughly 44-63 ms latency difference is operationally immaterial and is
not accompanied by higher decode throughput. It is much smaller than Trunk
Recorder's three-second health interval. Both variants identified RFSS 002,
site 008, system 2AD, WACN BEE00, and NAC 2A4 on every run.

The full-reset implementation is therefore not a supported stabilization fix
and was not deployed. The local experimental source change should remain only
as reproducibility evidence, not as a production candidate.

## Interpretation

The result rejects the narrow theory that residual state inside the P25
channelizer/carrier/timing/framing graph is sufficient to explain the observed
long live outages. A fresh graph is neither necessary for this degraded sample
nor measurably better after deliberate noise-channel visits.

Together with the earlier evidence, the remaining distinction is now:

- stored samples from a reported degradation are decodable;
- source centering does not prevent the failure class;
- complete P25 graph reset does not improve deterministic recovery; and
- the severe behavior appears in the long-running live pipeline.

That moves the investigation one ownership boundary earlier than the decoder:
sample delivery, GNU Radio scheduling/backpressure, SDR/USB continuity, and
host resource contention. The geographic separation does not rule these out;
OT and RPI share the Trunk Recorder/GNU Radio/PizzaWave runtime architecture
even though they do not share transmitters, receivers, or RF paths.

## Recommended next experiment

Instrument live sample continuity through the existing receive graph and wait
for a natural degradation. This should replace the proposed receiver-role
crossover as the highest-value next test.

Use one monotonic timestamp and emit low-volume counters at one-second cadence:

1. complex samples delivered by the SDR source versus the configured sample
   rate, including the longest wall-clock gap between work calls;
2. samples consumed at the P25 graph input and emitted after channelization;
3. symbols emitted by timing recovery and valid frames emitted by OP25; and
4. process CPU, run-queue pressure, I/O wait, memory pressure, temperature,
   undervoltage, and kernel USB/SDR error counters.

Keep a bounded local ring of these counters and retain approximately 60 seconds
before through 120 seconds after decode falls below 2 msg/s. On one event per
host, also retain a short IQ segment from the same source branch so the exact
delivered samples can be replayed. Do not change control-channel lists, source
centers, gains, or retune policy during the observation.

The localization is direct:

- missing or late source samples implicate the SDR, USB transport, or driver;
- continuous source output but stalled downstream counts implicate GNU Radio
  scheduling or backpressure;
- continuous samples and symbols but missing frames, while the retained segment
  decodes fresh, implicate live decoder/queue interaction beyond simple graph
  state; and
- continuous pipeline counts accompanied by falling channel power/SNR support
  a real propagation, interference, or simulcast impairment.

This experiment requires current event-local evidence only. It does not require
historical known-good control-channel state or broader channel-scoring
scaffolding.

## Artifacts

Durable artifacts are on RPI at:

`/var/lib/pizzawave/rf-surveys/manual/20260720T-rpi-raymond-demod-state-replay`

They include both replay binaries, their hashes, all configs and logs, the
full-reset source file, the fixed-run harness, and machine-readable
`analysis.json`. The production `trunk-recorder` and `pizzad` services remained
active; neither service was restarted or replaced for this experiment.

## Limits

This is a controlled test of one concrete decoder-state mechanism against one
captured Raymond interval. It does not recreate hours of live runtime, USB
transport behavior, host contention, or OT's simulcast environment. It rules
out a proposed catch-all reset, not every possible stateful fault in GNU Radio
or Trunk Recorder.
