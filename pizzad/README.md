# PizzaWave Engine (`pizzad`)

`pizzad` is the clean-break PizzaWave Engine. It is designed to run persistently on the same Linux host as trunk-recorder and own durable ingest, SQLite storage, audio files, transcription queueing, REST/SSE APIs, TR health summaries, SFTP import jobs, and the bundled web UI.

## Build

```bash
dotnet publish ./pizzad/pizzad.csproj -c Release -o ./artifacts/pizzad
```

## Visual Studio Local Run

Use the `pizzad local` launch profile. It reads the checked-in development config at `pizzad/config/pizzad.local.json`, binds the web/API server to `http://127.0.0.1:18080`, and listens for callstream TCP payloads on `127.0.0.1:19123`.

Runtime state is written under `artifacts/`, which is intentionally ignored by git:

- `artifacts/pizzad-local.db`
- `artifacts/pizzad-local-audio/`
- `artifacts/pizzad-local-import-cache/`
- `artifacts/pizzad-local.token`

After starting from Visual Studio, verify the running instance with:

```powershell
.\scripts\smoke_pizzad.ps1 -UseRunningServer -HttpPort 18080 -CallstreamPort 19123
```

Useful local URLs:

- `http://127.0.0.1:18080`
- `http://127.0.0.1:18080/api/v1/health`
- `http://127.0.0.1:18080/swagger`

## Install

```bash
sudo ./scripts/setup_pizzawave_engine.sh --publish-dir ./artifacts/pizzad
```

The same flow is exposed through the package-style helper:

```bash
sudo ./scripts/pizzawave install-engine --publish-dir ./artifacts/pizzad
sudo ./scripts/pizzawave configure-callstream --config /etc/trunk-recorder/config.json
```

The installer creates:

- `/opt/pizzawave/pizzad`
- `/etc/pizzawave/pizzad.json`
- `/etc/pizzawave/pizzad.token`
- `/var/lib/pizzawave/pizzad.db`
- `/var/lib/pizzawave/audio`
- `pizzad.service`

## Ingest

The v1 live ingest path is the existing callstream TCP binary protocol. `pizzad` listens on `127.0.0.1:9123` by default. Patch trunk-recorder callstream config with:

```bash
sudo ./scripts/pizzawave_configure_callstream.py --config /etc/trunk-recorder/config.json
```

The helper creates a timestamped backup before writing.

## Security

`pizzad` uses simple token auth for write/admin APIs. This is intended for private LAN, Tailscale, SSH tunnel, or reverse-proxy deployments. Do not expose `pizzad` directly to the public internet without network-layer protection and TLS.

## Current Implementation Notes

- SQLite WAL is the canonical store.
- Audio is stored on the local filesystem.
- SFTP import APIs estimate ranges, enforce guardrails, download `.bin` files into local cache, ingest them through the same callstream pipeline, and track remote file status in SQLite.
- Incident generation is intentionally range-triggered. Generated incidents are persisted and shown by the dashboard.
- Recent 48-hour SFTP reconciliation runs automatically when SFTP import is enabled.
