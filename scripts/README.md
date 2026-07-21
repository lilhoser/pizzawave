# scripts

This folder contains operational helpers for PizzaWave deployments.

## Current Helpers

| Script | Purpose |
| --- | --- |
| `build_pizzawave_deb.sh` | Builds the self-contained PizzaWave `.deb` package |
| `setup_pizzawave_engine.sh` | Installs `pizzad` from a published build |
| `pizzawave` | Package-style helper wrapper |
| `pizzawave_setup_admin.sh` | Root-only admin helper called by `pizzad` setup jobs |
| `setup_trunk_recorder.sh` | Source-build helper for trunk-recorder and callstream |
| `setup-lmstudio.sh` | Installs LM Studio CLI / LM Link support and conditional local embedding autoload |
| `setup-faster-whisper.sh` | Installs optional faster-whisper support |
| `setup-remote-faster-whisper-server.ps1` | Installs the Windows GPU faster-whisper HTTP server |
| `tr_tune.sh` | Guided trunk-recorder/RTL-SDR tuning helper |
| `prime_tr_health.py` | One-time import helper for trunk-recorder health history |
| `analyze_p25_collapse_iq.py` | Offline paired-wide P25 collapse classifier |
| `deploy_pizzad_web.ps1` | Fast frontend-only deploy helper; rebuilds and copies `wwwroot` without restarting `pizzad` |
| `deploy_pizzad_tar.ps1` | Automatic development deploy helper with hashed build reuse, live artifact comparison, per-stage timing, and health polling |

## Notes

The preferred release path is the `.deb` package. Direct tar deployment is for
development iteration only.

The tar helper automatically selects no-op, web-only, or backend deployment by
comparing source/output hashes with the live deployment manifest. The web-only
wrapper remains available when an explicit frontend-only operation is useful.

`pizzad` owns ongoing trunk-recorder health collection. Import helpers are for
one-time priming or controlled maintenance tasks.

## P25 collapse IQ analysis

`analyze_p25_collapse_iq.py` analyzes a completed paired-wide collapse capture
without contacting or changing a live service. It validates the wide IQ sample
count and optional narrow/wide group, aligns one-second spectral measurements
with the embedded decoder timeline, checks sample integrity and neighboring
12.5 kHz bands, and emits JSON that distinguishes broadband from channel-local
signatures.

It requires Python 3 and NumPy:

```bash
python3 scripts/analyze_p25_collapse_iq.py \
  wide-automatic.fc32 wide-automatic.json \
  --narrow-metadata narrow-automatic.json \
  --output analysis.json
```

The classification is deliberately bounded. A channel-local result supports
modulation impairment but cannot by itself distinguish simulcast multipath from
an exactly co-channel interferer.
