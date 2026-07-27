# Settings Schema

The current engine config lives at:

```text
/etc/pizzawave/pizzad.json
```

The web Settings page edits the same file. Some changes apply immediately;
others require restarting `pizzad`. The UI should make restart-required changes
visible.

## Top-Level Sections

| Section | Purpose |
| --- | --- |
| `engine` | bind addresses, storage paths, setup state, profile defaults |
| `ingest` | callstream listener and pause/drop behavior |
| `transcription` | provider, model, concurrency, model management |
| `aiInsights` | LLM endpoint, model, prompt budgets, lookback guardrails |
| `embeddings` | embedding endpoint, model, and local Qdrant vector settings |
| `alerts` | alert rules and email notification settings |
| `profiles` | profile-owned effective TG catalog and visibility behavior |
| `trunkRecorder` | TR config paths, service names, patching behavior |
| `monitoredAreas` | system-to-area mappings used by geolocation |
| `auth` | simple token behavior for admin/write APIs |
| `ui` | branding and display preferences |

## Transcription

| Field | Meaning |
| --- | --- |
| `provider` | `whisper`, `faster-whisper`, `remote-faster-whisper`, `lmstudio`, or OpenAI-compatible provider |
| `model` | selected model name |
| `maxParallelism` | worker concurrency for transcription |
| `fallbackModel` | optional slower/higher-quality retry model |
| `qualityPolicy` | thresholds for empty/inaudible/short/noisy classification |

Local providers should not require users to type filesystem paths in the UI.
PizzaWave manages model files and shows download/use/remove actions.

Use `remote-faster-whisper` when transcription is offloaded to a LAN GPU host
running the OpenAI-compatible faster-whisper server. In that mode,
`openAiBaseUrl` points at the server `/v1` endpoint and `openAiModel` names the
remote model.

## AI Insights

| Field | Meaning |
| --- | --- |
| `enabled` | master switch for AI summaries, incidents, and troubleshooting insights |
| `executionMode` | operator intent: `local`, `remote`, or `lmlink`; the endpoint URL still decides where requests are sent |
| `endpoint` | OpenAI-compatible base URL |
| `model` | selected LLM |
| `apiKey` | optional key/token |
| `maxLookbackHours` | maximum recent range for manual AI summary/incident generation |
| `promptBudget` | maximum prompt size for normal mode |
| `compactPromptBudget` | stricter prompt size for compact mode |

Historical imports are not an in-app feature. Summary generation is guarded and
limited to recent stored calls so transcription, incident creation, and LLM work
cannot be flooded from the web UI.

For remote GPU LLMs, point `openAiBaseUrl` at the remote host/Tailnet endpoint
instead of localhost and use a loadable LM Studio model ID, not a loaded runtime
alias/moniker. For example, use `qwen/qwen3.6-35b-a3b@q8_0` instead of a custom
serving alias such as `paxan-qwen3.6-35b-a3b@q8_0`. On client rigs that should
not run LLMs, disable LM Studio peer model discovery and JIT/local model loading
so model names cannot resolve locally by accident.

Some Qwen thinking GGUFs do not honor request-side `enable_thinking=false`
through LM Studio. If a model emits JSON in `reasoning_content` instead of
`message.content`, add this as the first line of the model's Template Jinja and
reload the model:

```jinja
{%- set enable_thinking = false %}
```

## Embeddings / Qdrant

| Field | Meaning |
| --- | --- |
| `enabled` | master switch for embedding completed, eligible transcripts |
| `executionMode` | operator intent: `local`, `remote`, or `lmlink`; startup preload helpers only act when this is `local` |
| `openAiBaseUrl` | OpenAI-compatible `/v1` endpoint used for `/embeddings` |
| `openAiModel` | embedding model name; must return `vectorSize` dimensions |
| `qdrantBaseUrl` | Qdrant HTTP endpoint; normally local to the rig |
| `qdrantServiceName` | systemd unit used for status/restart actions |
| `qdrantStoragePath` | native Qdrant data path included in backup/restore |
| `collection` | Qdrant collection used for call vectors |
| `vectorSize` | expected embedding vector dimension |

The recommended split for client rigs is local embeddings plus remote LLM:

```json
{
  "aiInsights": {
    "executionMode": "remote",
    "openAiBaseUrl": "http://paxan:1234/v1",
    "openAiModel": "qwen/qwen3.6-35b-a3b@q8_0"
  },
  "embeddings": {
    "executionMode": "local",
    "openAiBaseUrl": "http://localhost:1234/v1",
    "openAiModel": "text-embedding-nomic-embed-text-v1.5"
  }
}
```

When LM Studio JIT loading is disabled, `setup-lmstudio.sh` installs a systemd
startup hook that loads the configured embedding model only when embeddings are
enabled, `executionMode` is `local`, and the embedding endpoint is
localhost:1234.

## Profiles And Talkgroups

Profiles are runtime policy overlays. They do not change what trunk-recorder
captures unless the underlying catalog is edited and applied. Switching the
active profile affects dashboard visibility plus downstream alerts, embeddings,
and incident creation without a service restart.

| Field | Meaning |
| --- | --- |
| `profiles.activeProfileId` | profile currently used for dashboard and downstream filtering |
| `profiles.items[].includePolice` | include Police calls in views and downstream processing |
| `profiles.items[].includeFire` | include Fire calls in views and downstream processing |
| `profiles.items[].includeEMS` | include EMS calls in views and downstream processing |
| `profiles.items[].includeTraffic` | include Traffic calls in views and downstream processing |
| `profiles.items[].includeUtilities` | include Utilities calls independently from Other |
| `profiles.items[].includeOther` | include remaining Other calls |
| `profiles.items[].talkgroups[].systemShortName` | system half of the exact talkgroup identity |
| `profiles.items[].talkgroups[].id` | decimal TG id being overridden by this profile |
| `profiles.items[].talkgroups[].enabled` | optional profile-specific enable/disable state; false suppresses dashboard visibility, alerts, embeddings, and incidents but still captures/transcribes |
| `profiles.items[].talkgroups[].label` | optional profile-specific display label |
| `profiles.items[].talkgroups[].category` | optional profile-specific PizzaWave category |
| `profiles.items[].talkgroups[].incidentEligible` | legacy compatibility field; profile enable/disable is the operator-facing incident/alert/embedding gate |

Setup > Talkgroups uses a draft-and-Apply model. Profile-only changes save
without restarting services. Catalog/capture changes regenerate
`trunkRecorder.talkgroupsPath` and require trunk-recorder/pizzad restart.

Saving a profile and activating a profile are separate actions. Saving edits
does not silently change the active runtime policy.

## Alerts

Alert rules run only for live calls allowed by the active profile. Historical
or imported calls are not currently evaluated by the live alert pipeline.

| Field | Meaning |
| --- | --- |
| `alerts.rules[].matchType` | `keyword`, `police_code`, or `keyword_or_police_code` |
| `alerts.rules[].keywords` | comma-separated keywords or phrases |
| `alerts.rules[].policeCodes` | comma-separated normalized police codes |
| `alerts.rules[].talkgroups[].systemShortName` | required system scope for a selected talkgroup |
| `alerts.rules[].talkgroups[].id` | required TG ID paired with the system; an empty list means all active-profile talkgroups |
| `alerts.rules[].frequency` | `realtime`, `hourly`, or `daily` notification throttle |

Numeric-only alert talkgroup entries are unsupported because TG IDs are not
globally unique across systems.

## Talkgroups

`trunkRecorder.talkgroupCatalogPath` points to PizzaWave's JSON talkgroup
catalog. `trunkRecorder.talkgroupsPath` points to the generated CSV consumed by
trunk-recorder.

Catalog rows include:

| Field | Meaning |
| --- | --- |
| `id` | numeric talkgroup ID |
| `mode` | trunk-recorder mode, usually `D` |
| `alphaTag`, `description`, `tag` | source labels used to build display names |
| `sourceCategory` | original imported category text |
| `opsCategory` | PizzaWave category: `police`, `fire`, `ems`, `traffic`, `utilities`, or `other` |
| `enabled` | when false, row stays in the catalog but is excluded from generated CSV |
| `incidentEligible` | when false, routine calls from the talkgroup are excluded from incident extraction unless the call text contains a strong generic emergency/event signal |
| `source`, `notes` | operator/source metadata |

Talkgroup labels and categories are assigned once at ingest time. The dashboard,
alerts, summaries, incidents, and troubleshooting use stored call data, not
read-time catalog rewrites. Save catalog changes, generate the CSV, then restart
trunk-recorder for ingest changes to apply.

`incidentEligible` is an incident-generation guard only; it does not change
trunk-recorder ingestion. CSV import/preview defaults generic institutional
operations talkgroups such as maintenance, facilities, valet, parking, shuttle,
and housekeeping away from incidents, while public-safety dispatch/rescue/fire/EMS
labels remain eligible. The runtime validator still permits a normally
ineligible talkgroup when the transcript has a strong emergency/event anchor, so
routine operations chatter is filtered without hard-coding site-specific names.
Medical transport or hospital handoff calls are not standalone incidents, but
they can still be retained as supporting calls when a parent emergency event is
present.

## Auth

The admin token protects write/admin/setup/service operations when enabled. It
is a random bearer secret stored in `/etc/pizzawave/pizzad.token`; browser API
requests send it as `Authorization: Bearer <token>` after the operator enters it
once. New installs default to token auth for writes while leaving read-only
dashboard APIs open.

This is intended for private networks, Tailscale, SSH tunnels, or reverse
proxies. It is not a replacement for public-internet authentication and TLS.
