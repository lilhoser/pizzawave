# Passive RF Telemetry Contract

Status: emission and PizzaWave persistence validated on OT

Implementation checkpoints:

- Trunk Recorder `codex/rf-telemetry` at `8318dfb`.
- callstream `codex/rf-telemetry` at `1cdd5c4`.
- PizzaWave configuration and tests are on `codex/rf-telemetry`.
- Both changed C++ translation units compile on OT in an isolated temporary
  tree. All 529 PizzaWave tests pass.

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
