# Quickstart

This guide assumes a Linux host that will run trunk-recorder and `pizzad` on the
same machine. Ubuntu 24.04 LTS is the primary target; Raspberry Pi OS/Debian
ARM64 is the second target.

## Install

Use the generated `.deb` package whenever possible:

```bash
sudo apt install ./pizzawave_0.1.0_amd64.deb
```

For Raspberry Pi OS/Debian ARM64:

```bash
sudo apt install ./pizzawave_0.1.0_arm64.deb
```

The package installs the engine under `/opt/pizzawave/pizzad`, creates the
`pizzawave` user, creates `/etc/pizzawave/pizzad.json` if missing, enables
`pizzad.service`, and serves the web UI on port `8080`.

## Open the Wizard

Open:

```text
http://<pizzawave-host>:8080
```

If setup has not been completed, the web UI enters the first-run wizard.

Required wizard gates:

1. Stack choice: reuse an existing trunk-recorder installation or build a fresh one.
2. Trunk-recorder config and callstream patching.
3. Talkgroup CSV import or creation.
4. Transcription engine and model.
5. Monitored area mappings for geolocation.
6. Final validation and enablement.

Optional wizard gates:

- AI Insights through LM Link or another OpenAI-compatible endpoint.
- Vector DB / Qdrant embeddings for incident matching.
- Email alerts.
- SFTP archive import.
- Local import from an existing trunk-recorder recordings directory.
- RTL-SDR calibration.

## Validate the Service

```bash
systemctl status pizzad --no-pager
curl -fsS http://127.0.0.1:8080/api/v1/health
```

Expected health response includes `status`, `databasePath`, `audioRoot`,
`queueDepth`, and `serverTimeUtc`.

## Validate Call Ingest

In the web UI:

1. Open **System**.
2. Check the status bar for calls and queue state.
3. Open a category page such as **Police** or **Fire**.
4. Confirm live calls appear, including pending/untranscribed calls.

At the shell:

```bash
journalctl -u pizzad -f
```

You should see live call ingest and queue activity when trunk-recorder completes
calls.

## Validate Transcription

Open **Settings -> Transcription** and test the selected engine. For local
Whisper models, PizzaWave manages model download/use/remove operations. For
faster-whisper, confirm the sidecar dependencies are installed by the package or
setup helper.

On small ARM systems, use queue pressure rather than a single call/minute number
to judge health. If the queue grows during live traffic, switch to a faster
model, pause ingestion temporarily, or investigate RF/call volume.

## Validate AI Insights

Open **Settings -> AI Insights**:

1. Enable AI Insights.
2. Choose the execution intent: `local`, `remote`, or `lmlink`.
3. Configure the chat `/v1` endpoint. For a rig that should use a GPU server
   such as Paxan, point the base URL at that remote host, not localhost.
4. Use the LM Studio model ID from the picker, such as
   `qwen/qwen3.6-35b-a3b@q8_0`, not a loaded runtime alias/moniker.
5. Fetch available models.
6. Select a model.
7. Test the connection.

For Qwen thinking models, confirm the endpoint returns final JSON in
`message.content`, not `reasoning_content`. With LM Studio and some Unsloth
Qwen GGUFs, this may require adding this as the first line of the model's
Template Jinja:

```jinja
{%- set enable_thinking = false %}
```

## Validate Embeddings / Qdrant

Open **Settings -> Embeddings**:

1. Enable embeddings.
2. Choose the execution intent. `local` means embedding inference runs on this
   rig; `remote`/`lmlink` means another OpenAI-compatible endpoint generates
   vectors.
3. Configure the embedding `/v1` endpoint and model.
4. Keep Qdrant local unless there is a specific operational reason to move it.
5. Test the connection.

If LM Studio JIT loading is disabled, the setup helper installs a systemd hook
that preloads the configured embedding model only when embeddings are enabled,
`executionMode` is `local`, and the embedding endpoint is localhost:1234.

Large historical imports do not automatically generate summaries/incidents.

## Common Service Commands

```bash
sudo systemctl restart pizzad
sudo systemctl status pizzad --no-pager
journalctl -u pizzad -n 100 --no-pager
```

Use the web UI restart buttons for normal operation when available.
