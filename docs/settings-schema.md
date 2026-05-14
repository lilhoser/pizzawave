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
| `alerts` | alert rules and email notification settings |
| `profiles` | global filtering/profile behavior |
| `imports` | SFTP/local import defaults and guardrails |
| `trunkRecorder` | TR config paths, service names, patching behavior |
| `monitoredAreas` | system-to-area mappings used by geolocation |
| `auth` | simple token behavior for admin/write APIs |
| `ui` | branding and display preferences |

## Transcription

| Field | Meaning |
| --- | --- |
| `provider` | `whisper`, `fasterWhisper`, `lmstudio`, or OpenAI-compatible provider |
| `model` | selected model name |
| `maxParallelism` | worker concurrency for transcription |
| `fallbackModel` | optional slower/higher-quality retry model |
| `qualityPolicy` | thresholds for empty/inaudible/short/noisy classification |

Local providers should not require users to type filesystem paths in the UI.
PizzaWave manages model files and shows download/use/remove actions.

## AI Insights

| Field | Meaning |
| --- | --- |
| `enabled` | master switch for AI summaries, incidents, and troubleshooting insights |
| `endpoint` | OpenAI-compatible base URL |
| `model` | selected LLM |
| `apiKey` | optional key/token |
| `maxLookbackHours` | automatic backfill limit |
| `promptBudget` | maximum prompt size for normal mode |
| `compactPromptBudget` | stricter prompt size for compact mode |

Large imports do not automatically generate summaries/incidents. Summary
generation is a guarded action in the UI.

## Imports

SFTP imports copy remote `.bin` archive data into the local PizzaWave store.
Local imports ingest an existing local trunk-recorder recordings directory. Both
paths create normal call records and never modify the source data.

Important guardrails:

- quick import is limited;
- large import requires estimate and confirmation;
- imports run as jobs;
- transcription runs lower priority than live calls;
- imported alert matches suppress live/email notification.

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
| `opsCategory` | PizzaWave category: `police`, `fire`, `ems`, `traffic`, or `other` |
| `enabled` | when false, row stays in the catalog but is excluded from generated CSV |
| `source`, `notes` | operator/source metadata |

Talkgroup labels and categories are assigned once at ingest/import time. The
dashboard, alerts, summaries, incidents, and troubleshooting use stored call
data, not read-time catalog rewrites. Save catalog changes, generate the CSV,
then restart trunk-recorder for ingest changes to apply.

## Auth

The simple token protects write/admin operations when enabled. It is intended
for private networks, Tailscale, SSH tunnels, or reverse proxies. It is not a
replacement for public-internet authentication and TLS.
