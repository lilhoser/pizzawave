# scripts

This folder contains operational scripts used with PizzaPi + Trunk Recorder deployments.

## `setup_trunk_recorder.sh`

Builds and installs Trunk Recorder + `callstream`, creates systemd service, and wires helper tooling.

New in this branch:

- Installs TR health collector script to `/usr/local/bin/tr_health_collect.sh`
- Installs and enables `tr-health-collector.timer` (every 5 minutes)
- Writes flat-file metrics to `/var/lib/pizzapi/tr-health/summary_5m.csv`

## `tr_health_collect.sh`

Collects rolling Trunk Recorder health metrics from journald for a fixed window (default 5 minutes),
then appends `global` and per-system rows to:

- `/var/lib/pizzapi/tr-health/summary_5m.csv`

It also appends per-source rows using `scope=source:<rtl-serial>` with source-specific
sample-stop and tuning-error rollups.

Columns include decode/retune/call counters plus tuning error and sample-stop counts.

Manual run:

```bash
sudo /usr/local/bin/tr_health_collect.sh 5
```

## Troubleshooting UI Integration

Current `pizzapi` troubleshooting prefers this collector output directly:

- Source file: `/var/lib/pizzapi/tr-health/summary_5m.csv`
- Settings mode: `Collector CSV (preferred)`
- Remote retrieval: same SSH connector used by diagnostics

If collector output is unavailable, `pizzapi` can optionally warn and fall back to raw-log mode.

## `tr_tune.sh`

Unified tuning workflow helper for:

- control-channel sweeps
- error sweeps
- device bakeoff runs
