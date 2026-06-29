# AI Insights Behavior

AI Insights is the single master feature area for LLM usage in PizzaWave. When
disabled, PizzaWave should not create new AI summaries, incidents, or LLM-backed
troubleshooting suggestions.

## Feature Behavior

| Feature | Source | Output | Notes |
| --- | --- | --- | --- |
| AI Summaries | individual or grouped calls | category-page summary cards | May be shown per category |
| Incidents | two or more related calls | dashboard/category incident cards | A single call is not an incident |
| Troubleshooting Insights | system metrics/charts | diagnostic suggestions | Requires explicit user action |
| Token Usage | all AI calls | stats and history | Shown in System |

## Incident Rules

An incident is a generated event with at least two related source calls. The
same source call should not appear in multiple unrelated incidents. Incidents
should show time range, confidence, category coloring, related calls, transcripts
and audio.

Incident generation is summary-based in v1. It does not use vector similarity or
additional embedding services.

## Quality Pruning

Calls classified as empty, fully inaudible, failed, or otherwise unusable should
be excluded from summary windows. Calls that merely contain an inaudible marker
inside an otherwise useful transcript should not be discarded solely for that
reason.

## Imports

Imported calls run through normal ingest and transcription. Alert matches are
stored, but live/email notifications are suppressed. Large imported ranges do
not automatically create summaries or incidents.

## Prompt Budget

Normal and compact modes both enforce prompt budgets. Compact mode should use a
more aggressive budget. The engine should never send unbounded transcripts to an
LLM.
