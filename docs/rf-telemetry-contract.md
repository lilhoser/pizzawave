# Passive RF Telemetry Contract

Status: implemented; pending OT-only runtime validation

Implementation checkpoints:

- Trunk Recorder `codex/rf-telemetry` at `8318dfb`.
- callstream `codex/rf-telemetry` at `1cdd5c4`.
- PizzaWave configuration and tests are on `codex/rf-telemetry`.
- Both changed C++ translation units compile on OT in an isolated temporary
  tree. All 529 PizzaWave tests pass. No live host has been changed.

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
Reacquisition events add low-decode duration and retune count.

This path is passive. It does not open an SDR, change tuning, capture IQ, or
alter Trunk Recorder's retune behavior. Control-channel signal power and noise
are intentionally absent because current Trunk Recorder interfaces do not
provide trustworthy values for them.

## Initial Validation

Build the matching Trunk Recorder and callstream branches, enable
`rf_telemetry`, and run them on OT first. Verify normal samples, one supervised
retune, and one reacquisition event against the existing human-readable TR log.
Do not promote to RPI or add PizzaWave persistence until the OT evidence agrees.
