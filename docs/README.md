# PizzaWave Documentation

PizzaWave is a radio-call monitoring stack built around a persistent Linux
service named `pizzad`, trunk-recorder, the callstream plugin, a local
SQLite/audio store, and a bundled web UI served by the engine.

## Current Architecture

`pizzad` runs on the same Linux host as trunk-recorder. It listens for
callstream TCP payloads on localhost, stores call metadata in SQLite, stores
audio under `/var/lib/pizzawave/audio`, transcribes calls, evaluates alert
rules, generates AI summaries/incidents with local Qdrant-backed retrieval,
collects trunk-recorder health, serves REST/SSE APIs, and hosts the React web
UI.

| Purpose | Path |
| --- | --- |
| Engine binary | `/opt/pizzawave/pizzad` |
| Engine config | `/etc/pizzawave/pizzad.json` |
| Engine database | `/var/lib/pizzawave/pizzad.db` |
| Audio store | `/var/lib/pizzawave/audio` |
| Import cache | `/var/lib/pizzawave/import-cache` |
| Service | `pizzad.service` |
| Web UI | `http://<host>:8080` |

## Main Guides

- [Quickstart](quickstart.md): install, first-run prerequisites, Setup, and basic validation.
- [Deployment](deployment.md): package flow, service operations, and updates.
- [Backup and Restore](backup-restore.md): full-state backups and staged restore.
- [Reset and Site Setup](reset-and-setup.md): clean data, reset site state, or return to first-run before rebuilding Setup.
- [Building](building.md): local development, self-contained packages, and deploy helpers.
- [Testing](testing.md): BVT and medium feature-test lanes.
- [Settings Schema](settings-schema.md): current `pizzad.json` sections.
- [LM Studio AI Routing](lmstudio-ai-routing.md): local embeddings, remote LLMs,
  LM Link, Qwen thinking controls, and startup model loading.
- [Remote faster-whisper](remote-faster-whisper.md): GPU transcription host,
  bearer auth, and Tailscale use.
- [Quick Reference](quick-reference.md): commands, paths, and API probes.
- [Operational Limits](operational-limits.md): queue pressure, AI limits, imports, and hardware notes.
- [Insights Behavior](insights-behavior-matrix.md): AI summaries, incidents, and alerts.
- [Config Examples](config-examples-explained.md): trunk-recorder/callstream examples.
- [SDR Setup](getting_started_with_sdrs.md): SDR and trunk-recorder concepts used by Setup.
- [Email Troubleshooting](email-smtp-troubleshooting.md): SMTP alert setup.
- [Current Status](current-status.md): active development handoff notes.
- [Open TODO](open-todo.md): working backlog.

## Runtime Flow

1. trunk-recorder records radio calls and callstream sends completed call
   payloads to `127.0.0.1:9123`.
2. `pizzad` persists call metadata and audio before doing expensive work.
3. The transcription queue processes calls using the configured engine.
4. Quality classification marks empty, inaudible, short, noisy, or failed calls.
5. Alert matching runs for live and imported calls. Imported calls store matches
   but suppress live/email notification.
6. Post-transcription metadata, embeddings, AI summaries, and incidents are
   generated within configured guardrails.
7. The web UI receives live status through SSE and reads server-computed models
   through REST APIs.

## First-Run and Setup

After installing the package, open the web UI and complete first-run
prerequisites. First-run handles:

- existing trunk-recorder detection/reuse or fresh trunk-recorder source-build;
- optional LM Link host support for AI Insights;
- optional native Qdrant host support.

After first-run finishes, PizzaWave opens Setup. Setup owns location, systems
and sites, talkgroups, RF path, SDR inventory, waterfall/RF validation, source
planning, TR config apply, and monitoring resume. Settings owns app behavior
such as transcription, AI, embeddings, alerts, playback, security, and service
settings.

## Public Interfaces

`pizzad` exposes OpenAPI-documented REST endpoints under `/api/v1` and a live
event stream at `/api/v1/events/stream`.

Important endpoint groups:

- `/api/v1/health`
- `/api/v1/dashboard`
- `/api/v1/categories/{category}`
- `/api/v1/calls/{id}` and `/api/v1/calls/{id}/audio`
- `/api/v1/alerts`
- `/api/v1/incidents`
- `/api/v1/system/*`
  - includes `/api/v1/system/quality-check`, a read-only operational snapshot
    used by recurring quality monitoring.
- `/api/v1/settings/*`
- `/api/v1/imports/*`
- `/api/v1/events/stream`

## Security Model

The default target is private LAN, Tailscale, SSH tunnel, or a protected reverse
proxy. The built-in token is simple admin protection, not a public-internet
security boundary. The callstream ingest listener should remain localhost-only.

Write/admin token changes and protected config writes use the installed
sudo-backed setup helper on Linux. Alert SMTP passwords are stored in the local
PizzaWave credential store and are not returned through settings/setup status or
backup/restore metadata.
