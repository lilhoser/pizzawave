# Incident V2 Shadow Replay Handoff

Date: 2026-06-19

## Worktree

- Path: `C:\projects\pizzawave-incident-v2-shadow-replay`
- Branch: `codex/incident-v2-shadow-replay-cleanbreak`
- Base: `codex/pizzawave-engine-cleanbreak`
- Current base commit: `580ae65 Remove accidental equals file`
- Work status: clean after committing the current v2 shadow/replay work.
- Current work is split into focused local commits on this branch. Use
  `git log --oneline` for the exact latest commit.

The old branch `codex/incident-v2-shadow-replay` was deleted. The previous
`codex/rf-path-survey` branch was merged into `codex/pizzawave-engine-cleanbreak`
and deleted. Cleanbreak has been pushed.

## Purpose

This worktree contains the side-by-side incident v2 shadow/replay effort. The
goal is to replace brittle incident adjudication with a structured evidence
pipeline:

- the model predicts structured claims and exact source quotes;
- the server validates those claims against transcripts and stored evidence;
- the server owns persistence decisions, conflicts, call membership, and title
  grounding;
- replay labels determine whether v2 is actually better than v1.

Do not deploy this branch or cut over live incident persistence. This is
architecture, replay, and shadow-validation work only.

## Key Files

- `docs/incident-architecture-diagnosis.md`
  Decision document and architectural diagnosis.
- `docs/incident-v2-replay-labels.json`
  Seed label file for scored replay validation.
- `pizzad/IncidentEvidenceV2.cs`
  Canonical v2 evidence records.
- `pizzad/IncidentEvidenceDecisionEngineV2.cs`
  Shadow decision engine and deterministic server guardrails.
- `pizzad/IncidentEvidencePromptV2.cs`
  Structured model prompt/schema contract.
- `pizzad/IncidentReplayCorpus.cs`
  Replay corpus export model/building logic.
- `pizzad/IncidentReplayShadowReport.cs`
  Shadow report comparison logic.
- `scripts/export_incident_replay_corpus.ps1`
  Read-only corpus export helper.
- `scripts/run_incident_v2_hypothesis_shadow.ps1`
  Model hypothesis runner and v2 shadow decision converter.
- `scripts/build_incident_shadow_report.ps1`
  v1/v2 comparison report builder.
- `scripts/score_incident_shadow_labels.ps1`
  Label scorer.

Related tests:

- `pizzad.Tests/IncidentEvidenceV2Tests.cs`
- `pizzad.Tests/IncidentEvidenceDecisionEngineV2Tests.cs`
- `pizzad.Tests/IncidentEvidencePromptV2Tests.cs`
- `pizzad.Tests/IncidentReplayCorpusBuilderTests.cs`
- `pizzad.Tests/IncidentReplayShadowReportBuilderTests.cs`

## Latest Validation

After the latest shadow replay guardrail work, tests passed:

```powershell
dotnet test C:\projects\pizzawave-incident-v2-shadow-replay\pizzad.Tests\pizzad.Tests.csproj --no-restore
```

Result:

- Passed: 198
- Failed: 0
- Skipped: 0

Latest labeled replay score:

- Corpus: `artifacts/incident-replay-corpus/incident-replay-corpus-combined-20260619T145731Z.json`
- V2 decisions: `artifacts/incident-replay-corpus/incident-v2-decisions-20260619T213917Z.json`
- Report: `artifacts/incident-replay-corpus/incident-shadow-report-20260619T213926Z.json`
- Score artifact: `artifacts/incident-replay-corpus/incident-shadow-label-score-20260619T213932Z.json`
- Labels total: 72
- Scored labels: 72
- Review-only labels: 0
- Passed scored labels: 72
- Failed scored labels: 0
- Pass rate on scored labels: 1.00

The latest score used saved model hypotheses plus deterministic server decision
changes. It did not call production write paths and did not mutate production
data.

Latest larger fresh-model replay:

- Corpus: `artifacts/incident-replay-corpus/incident-replay-corpus-20260619T181635Z.json`
- Scope: 24 read-only hours, 250 audit cases, 149 unique calls.
- Fresh model decisions: `artifacts/incident-replay-corpus/incident-v2-decisions-20260619T201719Z.json`
- Latest deterministic re-decision:
  `artifacts/incident-replay-corpus/incident-v2-decisions-20260619T213855Z.json`
- Latest report:
  `artifacts/incident-replay-corpus/incident-shadow-report-20260619T213927Z.json`
- Model/replay errors: 0.
- V1 accepted / V2 rejected: 0.
- V1 rejected / V2 accepted: 74 rows across 40 unique accepted call sets.
- Same acceptance, same call set: 114.
- Same acceptance, different call set: 38.
- Both reject: 24.

Manual spot review of the v2-only accepts found mostly concrete incidents that
the current path rejected, including crashes, fire alarms, medical dispatches,
shots fired, stolen firearm, burglary alarm, gas/CO, welfare, and animal calls.
Two false-positive classes found during review were fixed before this handoff:
mixed medical/police candidate windows and standalone lift-assist calls.

Rollout status:

- Ready for read-only live shadow telemetry. The service now has a
  disabled-by-default `AiInsights.IncidentV2ShadowEnabled` path that calls v2
  beside the existing incident extractor and only logs v2 decisions.
- Not ready for live persistence replacement.
- Live cutover remains blocked until read-only shadow telemetry runs in the
  service and confirms no material false creates, false drops, stale
  memberships, or bad title regressions on live traffic.

Fresh model replay after the prompt update completed:

- Fresh model decisions: `artifacts/incident-replay-corpus/incident-v2-decisions-20260619T171017Z.json`
- Latest deterministic re-decision from those raw hypotheses:
  `artifacts/incident-replay-corpus/incident-v2-decisions-20260619T172438Z.json`
- Latest fresh report:
  `artifacts/incident-replay-corpus/incident-shadow-report-20260619T172448Z.json`
- Latest fresh score:
  `artifacts/incident-replay-corpus/incident-shadow-label-score-20260619T172551Z.json`
- Fresh score: 64 passed, 8 failed, pass rate 0.8889.

Those fresh replay failures were membership-completeness gaps, not total
incident misses. V2 accepted the incident but dropped related continuation,
assignment, address, or response-update calls. A follow-up deterministic
server decision pass now retains source-backed continuation/update calls when
they attach to an accepted incident through concrete location evidence, parent
event evidence, responder-status evidence, or specific reference/update
language, while still rejecting routine transport and unrelated calls.

- 30586: aggressive dogs, dropped same-address follow-up 813061.
- 30554: 911 hangup, dropped same-address duplicate/update 812739.
- 30537: accident with injuries, dropped response/update 811647.
- 30429: commercial structure fire, dropped EMS/address-only support calls
  810041 and 810059.
- 30425: suicidal armed subject, dropped black-vehicle/firearm updates 810369
  and 810377.
- 30365: probable DOA, dropped PD/SO enroute/status update 809275.
- 30351 and 30343: stabbing incident, dropped assignment/card-check updates.

The latest deterministic re-decision from the same fresh raw hypotheses scores
72/72 on the current labels.

The full fresh replay required multiple batches because model calls are slow.
Use `-ReuseExistingHypothesesWhenAvailable` to resume from partial artifacts
without redoing completed rows.

## Important Design State

The current implementation intentionally changes authority boundaries:

- Model conflict labels are claims, not final authority.
- Only server-recognized two-sided conflict types block membership:
  - `location_conflict`
  - `person_conflict`
  - `vehicle_conflict`
  - `parent_event_conflict`
- Non-authoritative model conflict labels such as `unrelated_location` do not
  get to drop source-backed calls by themselves.
- This recovered the overdose chain cases where the model marked initial
  dispatch call `802437` as unrelated, even though it was source-backed context
  for the same incident sequence.
- Conflict spans cannot retain a rejected call by themselves. Conflict evidence
  can block or explain membership, but it is not proof that the conflicted call
  belongs to the incident.
- Standalone EMS assist, lift assist, non-emergency transport, hospital handoff,
  routine traffic stops, routine checks, and non-emergency operations chatter are
  rejected unless they attach to a source-backed parent emergency.
- Shadow title/detail output is synthesized from accepted grounded spans instead
  of trusting unsupported model prose.
- The replay prompt now tells the model not to emit null spans and to retain the
  source-backed subset when retrieval includes unrelated neighbor calls.

The current v2 path is still not production-ready. The expanded label set proves
the replay/shadow architecture is materially better on the labeled cases, not
that live persistence should be replaced.

## Known V1 Churn Area

Earlier incident bake work in the main checkout touched existing v1 files,
including:

- `pizzad/AutomaticInsightsService.cs`
- `pizzad/IncidentCandidateValidator.cs`
- `pizzad/IncidentReconciliationService.cs`
- `pizzad/CallAnchorExtractionService.cs`
- `pizzad/TranscriptLocationService.cs`
- related tests

Those v1 changes may include brittle regex or guard churn. Do not assume they
should be kept. This worktree currently keeps the v2 replay architecture
separate from that v1 churn.

## Recommended Next Steps

1. Export another read-only recent corpus and add labels for ordinary clean
   creates, ordinary clean updates, stale memberships, stale titles, and quiet
   negative cases.
2. Run another fresh model replay after expanding labels to verify the model
   still emits enough evidence for the server-owned continuation rules.
3. Add read-only live shadow telemetry only after repeated fresh model replays
   preserve positives and reject known false joins. Do not replace persistence.
4. Review synthesized titles from fresh model output. The current server
   synthesis is grounded, but some titles are still awkward because transcript
   text is noisy.

## Fresh Session Prompt

Use this prompt in a new agent session:

```text
Work in C:\projects\pizzawave-incident-v2-shadow-replay.
Read AGENTS.md and docs/incident-v2-shadow-replay-handoff.md first.
Continue the incident v2 shadow replay work. Do not deploy or mutate production
data. Start by running git status and reviewing the dirty files. The immediate
goal is to split the dirty work into reviewable pieces and preserve the v2
architecture/replay work while identifying v1 regex/guard churn that should be
discarded or isolated.
```
