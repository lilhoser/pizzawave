# Remote faster-whisper Transcription

PizzaWave can offload transcription from constrained nodes to a LAN GPU host by
using the `remote-faster-whisper` transcription provider. The GPU host runs a
small OpenAI-compatible HTTP service, and each `pizzad` instance posts stored
call audio to `/v1/audio/transcriptions`.

This is separate from LM Studio. LM Studio remains the chat/insights service for
summaries, incidents, and troubleshooting. The remote faster-whisper server is a
latency-sensitive audio transcription service.

## GPU Host Setup

On the Windows GPU host:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\setup-remote-faster-whisper-server.ps1 `
  -InstallRoot C:\pizzawave\remote-faster-whisper `
  -Model small `
  -HostName 0.0.0.0 `
  -Port 9187 `
  -Device cuda `
  -ComputeType float16 `
  -CpuThreads 4 `
  -Workers 1 `
  -ApiKey "<long-random-token>" `
  -CreateStartupTask `
  -StartNow
```

The setup script creates a Python venv, installs the required packages, copies
`scripts/remote_faster_whisper_server.py`, and optionally registers a Windows
startup task named `PizzaWave Remote faster-whisper`.

The server defaults are tuned for short English public-safety radio clips:

- `language=en`
- `beam_size=5`
- `temperature=0`
- `repetition_penalty=1.0`
- `no_repeat_ngram_size=0`
- `condition_on_previous_text=false`
- `compression_ratio_threshold=2.4`
- `log_prob_threshold=-1.0`
- `no_speech_threshold=0.6`

These can be overridden with matching `PIZZAWAVE_FW_*` environment variables or
command-line flags on the server. Keep `condition_on_previous_text` disabled for
PizzaWave because each call is independent and prior-call context can increase
radio-code/address hallucinations.

For Tailscale or any non-loopback exposure, configure a bearer token on the
server and put the same value in PizzaWave's transcription API key field:

```powershell
$env:PIZZAWAVE_FW_API_KEY = "<long-random-token>"
```

or pass:

```powershell
--api-key <long-random-token>
```

The server accepts unauthenticated requests only when no API key is configured.
When an API key is configured, `/v1/audio/transcriptions` requires:

```text
Authorization: Bearer <long-random-token>
```

Validate locally:

```powershell
Invoke-RestMethod http://127.0.0.1:9187/health
Invoke-RestMethod http://127.0.0.1:9187/v1/models
```

Validate from each PizzaWave node:

```bash
curl -fsS http://<gpu-host-ip>:9187/health
```

Open the Windows firewall for TCP `9187` if the nodes cannot reach the health
endpoint.

## Remote Access With Tailscale

For a roaming Raspberry Pi or any node that may leave the local LAN, run the
remote faster-whisper endpoint over the same Tailnet used for LM Link instead
of using a LAN-only address.

Recommended setup:

1. Install and log in to Tailscale on the PizzaWave node and the GPU host.
2. On a Windows GPU host, enable unattended mode so the Tailnet is available
   after reboot and before an interactive user signs in:

   ```powershell
   tailscale up --unattended=true
   ```

   This changes Tailscale's persistent Windows service profile (`ForceDaemon`)
   rather than only the current process. It is intended to survive reboot and
   reconnect before login. Recheck it after logging out of Tailscale,
   reinstalling Tailscale, resetting its profile, or applying machine policy;
   unattended mode cannot compensate for expired authentication or unavailable
   host networking.

3. Confirm the node can resolve/reach the GPU host over the Tailnet:

   ```bash
   tailscale status
   curl -fsS http://<gpu-host-tailnet-name>:9187/health
   ```

4. Set the PizzaWave transcription base URL to the Tailnet name or Tailscale IP:

   ```json
   {
     "transcription": {
       "provider": "remote-faster-whisper",
       "openAiBaseUrl": "http://<gpu-host-tailnet-name>:9187/v1",
       "openAiModel": "small",
       "openAiApiKey": "<same-token-configured-on-server>"
     }
   }
   ```

5. Save the Transcription settings and restart `pizzad` if prompted.

Do not add a separate VPN stack for this. PizzaWave only needs an
OpenAI-compatible HTTP endpoint; Tailscale should be treated as host networking.
If exposing the faster-whisper port on `0.0.0.0`, restrict the Windows firewall
rule to trusted LAN/Tailscale profiles where practical.

## PizzaWave Node Configuration

On each node, set:

```json
{
  "transcription": {
    "provider": "remote-faster-whisper",
    "openAiBaseUrl": "http://<gpu-host-ip>:9187/v1",
    "openAiModel": "small",
    "openAiApiKey": "<same-token-configured-on-server>",
    "liveTranscriptionWorkers": 2,
    "livePressureQueueDepth": 200
  }
}
```

Restart `pizzad` after changing the provider. The node persists call audio
locally before transcription. Temporary endpoint, timeout, throttling, and
server failures remain pending and are retried after the endpoint health check
recovers; invalid requests still become terminal engine failures so
configuration and payload defects remain visible.

## Model Selection

Start with `small` on an RTX-class GPU. Move to `medium` only after confirming
the service can keep up with peak combined RPI and OT ingest. Watch:

- `pendingTranscriptions`
- `recentAudioSecondsIngestedPerMinute`
- `recentAudioSecondsTranscribedPerMinute`
- remote server request latency and realtime factor
- transcript quality on noisy police/fire/EMS calls

## Rollback

If the GPU host is unavailable or quality is unacceptable:

1. Set the node provider back to `faster-whisper`.
2. Restore the previous local model/device/compute settings.
3. Restart `pizzad`.
4. Retry failed transcriptions from System > Jobs.

## Operational Notes

- The GPU host is now a shared dependency for every node using this provider.
- Do not run large unrelated GPU jobs without watching queue depth.
- Keep LM Studio and remote faster-whisper on separate ports and treat them as
  separate services.
- Configure administrative outage email in Settings > Alerts if the remote
  endpoint is an operational dependency. PizzaWave sends one outage notice
  after the configured delay and one recovery notice.
- PizzaWave validates that `/health` returns `ok=true` and the configured model,
  then stores each confirmed outage and recovery independently of the browser.
  Endpoint outage history appears under System > Performance > Transcription.
- Calls processed more than 60 minutes after ingest retain alert and incident
  history with their original event timestamps, but real-time email and browser
  playback are suppressed.
- Eligible calls awaiting incident analysis are persisted independently of the
  PizzaWave process. A restart or a backlog larger than the in-memory working
  set no longer acknowledges those calls before analysis.
- Terminal transcription failures are not retried automatically. System > Jobs
  offers an explicit, cancellable recovery job. It processes one retained audio
  call at a time and waits while live or existing backlog transcription work is
  active; an already in-flight call is the cancellation boundary.
- `liveTranscriptionWorkers` controls concurrent HTTP transcription requests
  from each node; increase cautiously because multiple nodes can multiply load.
