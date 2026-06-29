# Agent Instructions

This repository contains the PizzaWave engine, web UI, setup tooling, RF survey
workflow, deployment scripts, and operational documentation.

## Worktree Rule

Always create a local git worktree before doing repo work.

- Do not make changes directly in a user's existing checkout.
- Use a branch name with a clear prefix, such as `codex/`, `claude/`, or
  `agent/`.
- Prefer a sibling directory under `C:\projects`, for example:

```powershell
git worktree add C:\projects\pizzawave-my-task -b codex/my-task <base-branch>
```

- If a task must continue in an existing worktree, first run `git status` and
  identify unrelated dirty changes. Do not overwrite or revert user work.

## Build And Test

- Backend tests:

```powershell
dotnet test C:\projects\pizzawave\pizzad.Tests\pizzad.Tests.csproj --no-restore
```

- Web UI lives under `pizzad/web`.
- Built web assets are checked into `pizzad/wwwroot`.
- If web source changes, rebuild the UI before committing generated assets.

## Repo Map

- `pizzad/`: engine, API server, AI incident logic, setup, RF survey, TR health,
  transcription, embeddings, and web hosting.
- `pizzad.Tests/`: .NET test suite.
- `pizzad/web/`: React/Vite web UI source.
- `scripts/`: deployment, setup, diagnostics, and replay helpers.
- `docs/`: operator docs, status docs, architecture notes, and field notes.

## Radio Setup And RF Diagnostics

- Treat [docs/rf-path-survey-mode.md](docs/rf-path-survey-mode.md) as the
  canonical Radio Setup workflow reference.
- For field antenna comparisons, use the relevant log under `docs/field-tests/`
  as the run book and evidence record.
- Prefer PizzaWave API endpoints for Radio Setup, TR health, and RF analysis.
  Do not use old user-local Codex RF diagnostic skills or standalone scripts as
  the primary workflow.
- For 30-minute antenna A/B baselines, record exact Unix start/end timestamps
  and use the troubleshooting/RF analysis API for that fixed window. Do not use
  `control_channel_quality` for 30-minute A/B evidence because that experiment
  is intentionally capped at 900 seconds.

## Engineering Rules

- Prefer existing services and patterns over new abstractions.
- Keep changes scoped to the requested task.
- Do not mutate production data, rerun backfills, purge history, or restart
  unrelated services unless the user explicitly asks.
- Treat RAG and embeddings as retrieval aids, not proof of incident membership.
- For incident architecture work, prefer side-by-side replay or shadow telemetry
  before changing live persistence.
- When diagnosing AI completion endpoints, do not assume a service calling
  `localhost` means the model is physically loaded on that same host. LM Studio
  LMLink can expose a local OpenAI-compatible endpoint that forwards to a model
  running on another machine. In this repo's operating environment, local
  `localhost:1234` completion calls may be normal when LM Studio/LMLink is in
  use. Verify the LM Studio/LMLink topology and generation health before calling
  this an anomaly.

## Durable Design

- Prefer durable architectural fixes over narrow special-case patches.
- Avoid regex-driven fixes when a structured parser, schema, typed model,
  database constraint, replay test, or server-side evidence contract would solve
  the problem more generally.
- Regex is acceptable for low-level extraction and normalization, such as
  addresses, route markers, spoken numbers, 10-codes, punctuation cleanup, and
  obvious status tags.
- Keep any remaining regex behind named helpers with focused tests. Do not let
  scattered regex checks become the authority for product behavior.
- When fixing a bug, look for the ownership boundary that should prevent the
  whole class of failure, not just the observed input string.

## Git Hygiene

- Commit focused changes with a clear message.
- Do not include temporary artifacts, local telemetry dumps, or unrelated
  generated files unless they are intentionally part of the task.
- Before committing, run `git status --short` and review the exact file list.
- Push only when the user asks or when the task explicitly includes publishing.
