# Insights Behavior Matrix

Current `pizzapi` Insights behavior by UI selection.

## Matrix

| Insights selection | Data source | Generates new LM summaries? | Persists output? |
|---|---|---|---|
| `Today` | persisted summaries + current live backlog | Yes, for eligible new live calls | Yes, only on successful LM result |
| `24h` | persisted summaries | No | No |
| `2d` | persisted summaries | No | No |
| `Week` | persisted summaries | No | No |
| `Range` | persisted summaries | No | No |

## Live Generation Rules

- Insights generation is enabled only when `lmLinkEnabled=true` and LM settings are valid.
- New live calls are queued for insights.
- Generation starts at threshold of 50 unsummarized calls.
- If LM processing fails, retry uses backoff; failed summaries are not persisted.

## Persistence Layout

Summaries are stored as:

`<APPDATA or ~/.config>/pizzawave/insights/<YYYY>/<MM>/<DD>/<HHmm>.json`

Only this hierarchy is used for summary storage.

## Startup Behavior

- On app startup, Insights loads from persisted summary files.
- After priming from history, only new live calls are candidates for generation.
