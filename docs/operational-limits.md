# Operational Limits

Operational limits and protections in current builds.

## Insights Pipeline

| Limit | Value | Purpose |
|---|---|---|
| Unsummarized threshold | `50` calls | trigger summary generation |
| Pending live call cap | `1000` calls | bound memory growth under load |
| Adaptive batch size | `50/100/150/200` | catch up when queue depth rises |
| Backoff on LM failures | up to `300s` | avoid repeated failing requests |
| Prompt size cap | `120000` chars | bound LM request size |
| Tile cap per category | `18` | prevent UI overload |
| Daily digest per category | top `10` | bounded email digest size |
| Summary retention | `30` days | bound disk usage |

## UI and In-Memory Call List

| Limit | Key | Default |
|---|---|---|
| Auto cleanup enabled | `AutoCleanupCalls` | `true` |
| Max calls retained | `MaxCallsToKeep` | `100` |

## Guidance

- For high-volume systems, keep `AutoCleanupCalls=true`.
- Keep `TraceLevelApp` at `Warning`/`Error` in always-on deployments unless debugging.
- Ensure LM host capacity is sized for peak call rates if Insights is enabled.
