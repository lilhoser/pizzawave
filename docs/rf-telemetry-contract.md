# Passive RF Telemetry Contract

Status: emission, persistence, analysis, and operator presentation validated on OT

Implementation checkpoints:

- Trunk Recorder `codex/rf-telemetry` at `8318dfb`.
- callstream `codex/rf-telemetry` at `1cdd5c4`.
- PizzaWave ingestion, analysis, and presentation are on `main` at `71663cc`.
- Both changed C++ translation units compile on OT in an isolated temporary
  tree. All 534 PizzaWave tests pass.

## OT Deployment Checkpoint

Deployed 2026-07-19 at 19:13 EDT with a backup at
`/var/backups/pizzawave/rf-telemetry-20260719T231317Z`.

- PizzaWave `main`: `de90c93`; backend hash `5ed6df2b...`.
- Trunk Recorder SHA-256: `877232c7a44e62abadec1f357beed13cc44ecc81b2d483aac89326444a3a1982`.
- callstream SHA-256: `72ea0030c23456b511e0678215176fc668e22ed722e5593fe97f0056f57681cb`.
- Eighteen initial samples parsed successfully: six for each of the three OT
  systems, with an exact 15-second cadence.
- Live call delivery, ingest, and transcription remained healthy with no
  queue backlog or dropped calls.
- No retune or reacquisition occurred naturally during the initial window.
  Leave telemetry enabled and validate those event types from the first
  natural occurrence; do not induce an RF interruption solely for validation.

Trunk Recorder writes control-channel retunes as single-line JSON records with
the `TR_RF` prefix. PizzaWave's callstream plugin uses TR's existing plugin API
to write periodic samples and reacquisition records with the `PIZZAWAVE_RF`
prefix. Both streams are retained in the Trunk Recorder journal.

## Events

- `rf_sample`: emitted every 15 seconds for each trunked system.
- `control_channel_retune`: emitted for every attempted control-channel retune.
- `control_channel_reacquired`: emitted when decode recovers after a continuous
  interval below 2 messages per second.

All events use `schemaVersion: 1` and include Unix time, system identity,
control channel, decode rate, frequency residual, source index, source center,
sample rate, configured source correction, source driver, and device arguments.
Retune events add the prior/requested channels and sources, reason, and result.
Reacquisition events add the continuous low-decode duration.

This path is passive. It does not open an SDR, change tuning, capture IQ, or
alter Trunk Recorder's retune behavior. Control-channel signal power and noise
are intentionally absent because current Trunk Recorder interfaces do not
provide trustworthy values for them.

## PizzaWave Persistence

The existing TR health collector owns ingestion so the system has only one
bounded journal reader. It accepts schema version 1 events with the correct
producer prefix, required typed fields, recognized event names, and plausible
timestamps. Identical JSON records are deduplicated by SHA-256.

Dense `rf_sample` rows are retained for eight days, covering the seven-day UI
lookback with collection margin. Rare retune and reacquisition events are
retained for 90 days. The authenticated
`/api/v1/system/rf/telemetry` endpoint supports time, system, event-type, and
bounded row-limit filters.

The authenticated `/api/v1/system/rf/telemetry-summary` endpoint returns
bounded per-site time series and transition narratives. Buckets adapt from one
minute for short windows to 30 minutes for the seven-day lookback. The Radio
Frequency page shows average/minimum decode rate, the 40 msg/sec strong-system
reference, average absolute frequency residual, and the natural channel-change
and recovery sequence. It uses its own System time window, not the global call
data selector.

Recommendations use the same local-baseline assessment shown on the RF page.
Related decode, zero-decode, retune, and capture symptoms remain a single
per-site finding. A finding is presented while degradation is active, for 24
hours after a severe episode, or when a reliable recurring schedule has
formed. Once none of those conditions remains, it moves to Finding History.
Passive samples and typed transitions are supporting evidence inside that
finding and do not create duplicate cards.

## Initial Validation

Initial validation required comparing the first natural retune and
reacquisition event with the existing human-readable TR log before RPI
promotion.

Validation completed at 2026-07-19 19:29 EDT. Hamilton generated a TDULC
retune from 855.2125 to 856.7625 MHz, three low-decode retunes through the
remaining configured channels, and reacquired 855.2125 MHz after nine seconds
of continuous low decode. All four structured retunes and the reacquisition
matched the adjacent human-readable log lines and were stored successfully.

PizzaWave ingestion candidate `26625f8` was deployed to OT with backend hash
`ecdf7a0b...`. It created 60 periodic rows plus the five transition events in
the first completed collection window. The authenticated API returned the full
typed channel/source chain. The schema migration also backfilled the pre-retune
frequency residual for events captured before that typed column was added.

## RPI Deployment Checkpoint

PizzaWave `main` at `71663cc` was deployed to RPI on 2026-07-19. RPI and OT
have matching deployable hashes: backend `0f2d33d0...` and web
`a7b32390...`.

RPI's Trunk Recorder emitter was deliberately rebased onto the exact installed
`766a553` source revision to avoid importing unrelated upstream changes. The
result is `ca787409`; the callstream emitter remains `1cdd5c4`. Installed
SHA-256 values are `4856146b...` for Trunk Recorder and `31ac526d...` for
callstream. The pre-install binaries and configuration are retained at
`/var/backups/pizzawave/rf-telemetry-rpi-20260720T004203Z`.

After the single required TR restart, RPI emitted 15-second typed samples for
ETV Raymond and Jackson plus typed low-decode retunes for the known disconnected
Jackson dongle. PizzaWave health, live ingest, transcription, and queues remained
normal. No retune behavior or limits were changed.
