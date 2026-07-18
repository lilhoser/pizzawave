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
| `deploy_pizzad_web.ps1` | Fast frontend-only deploy helper; rebuilds and copies `wwwroot` without restarting `pizzad` |
| `deploy_pizzad_tar.ps1` | Automatic development deploy helper with hashed build reuse, live artifact comparison, per-stage timing, and health polling |

## Incident Pipeline Experiment Helpers

| Script | Purpose |
| --- | --- |
| `run_incident_observation_interpretation_bakeoff.py` | Runs a development-only, single-observation interpretation and same-model critique with strict source-provenance validation; refuses sealed held-out paths |
| `run_incident_observation_cross_critic.py` | Re-critiques a validated interpretation artifact with a separately loaded model; refuses sealed held-out paths |
| `run_incident_direct_audio_bakeoff.py` | Runs transcription and source-grounded audio understanding against explicitly selected development clips; refuses sealed held-out paths |
| `convert_direct_audio_bakeoff_to_asr_artifact.py` | Converts selected direct-audio transcription results into the common ASR artifact contract for blind review |

## Notes

The preferred release path is the `.deb` package. Direct tar deployment is for
development iteration only.

The tar helper automatically selects no-op, web-only, or backend deployment by
comparing source/output hashes with the live deployment manifest. The web-only
wrapper remains available when an explicit frontend-only operation is useful.

`pizzad` owns ongoing trunk-recorder health collection. Import helpers are for
one-time priming or controlled maintenance tasks.
