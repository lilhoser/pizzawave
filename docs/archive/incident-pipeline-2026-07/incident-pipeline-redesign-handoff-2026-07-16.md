# Incident Pipeline Redesign Handoff

Date: 2026-07-16

## Purpose

This document tees up a fresh engineering session to resume PizzaWave incident
pipeline design work. The immediate trigger was a clearly questionable live
decision exposed by the System > Performance > Incidents pipeline inspector,
but the task is **not** to patch that one decision.

The task is to determine which incident architecture should become authoritative,
measure whether it materially improves operator-facing incident quality, and
finish the work through replay and shadow evidence before changing live
persistence.

## Executive Summary

PizzaWave still has split and partly contradictory incident authority:

- models emit structured event classes, narratives, and proposed membership;
- retrieval supplies candidate calls;
- several server layers independently recognize event concepts, locations,
  conflicts, membership, and narrative support;
- the live final validator can reject a structured `emergency_event` because a
  separate transcript phrase recognizer did not match;
- V2 evidence/replay machinery and a later V3 frame/plan pipeline both exist in
  the repository;
- V3 has progressed beyond a paper design and includes guarded execution work,
  so a new session must establish its exact current runtime posture before
  proposing another architecture.

The repository already reached the correct high-level diagnosis in June:
incident existence and membership cannot remain a collection of keyword fixes
and late repair guards. Do not restart that cycle.

## Concrete July 16 Failure

The live pipeline inspector exposed two dropped attempts for call `67113`:

- `Gunshots reported near Byron Dr/Bradford Place`
- `Reported gunshots at 100 Byron Dr / Bradford Place`

The retained transcript was:

> We found 93 the area of 100 Byron Drive around Bradford Place Apartments. The
> caller stated that he heard approximately 10 gunshots, or what he believed to
> be gunshots.

Recorded evidence included:

- system: `etv-raymond-hinds`;
- talkgroup: Byram Police Dispatch, TG 42650;
- structured event class: `emergency_event`;
- `logisticsOnly: false`;
- a concrete candidate location around `100 Byron Dr` and Bradford Place;
- final rejection: `single-call incident lacks a strong emergency/event signal`.

This appears to be a false rejection. The same call was tried and rejected
twice.

### Why the live validator rejected it

`pizzad/IncidentCandidateValidator.cs` currently uses
`StrongIncidentAnchorPattern` as terminal authority in the single-call path.
The expression recognizes `shooting`, `shots fired`, and the standalone word
`gun`, but not `gunshot` or `gunshots`. The word-boundary expression for `gun`
does not match inside `gunshots`.

`BuildEvidenceEventText` includes the structured event class, but
`ValidateSingleCallIncident` still requires `HasStrongIncidentAnchor(...)`.
The phrase recognizer does not treat `emergency_event` as a strong signal.

There is also an internal policy inconsistency:

- `AutomaticInsightsService.HasActionableCallEvidence(...)` can treat a
  non-routine `emergency_event`, `traffic_event`, or `fire_alarm` as actionable;
- terminal single-call validation can nevertheless reject that same evidence
  because its separate phrase list did not match.

The recorded reason therefore means “the terminal phrase recognizer did not
match,” not “the transcript lacked an obvious event.” The operator-facing reason
overstates what was actually decided.

### Evidence that this is broader than one phrase

A read-only query of the live 24-hour incident-chain endpoint on 2026-07-16
found this exact rejection on:

- 31 evaluation attempts;
- 21 unique calls;
- 10 attempts classified as `emergency_event`;
- 10 classified as `traffic_event`;
- 11 classified as `public_safety_event`.

Questionable examples included candidates describing:

- a burglary in progress;
- an assault at a nursing home;
- a stalking report;
- rocks thrown at a vehicle;
- a traffic crash;
- the Byron Drive gunshot report.

This does **not** prove all 31 attempts should have been accepted. Structured
model classifications can also be wrong, and several rows looked routine or
ambiguous. It proves that this rejection reason is not a reliable semantic
assessment and that the architecture can disagree with itself.

The evidence above was gathered read-only. No incidents were recovered,
backfilled, or persisted.

## Do Not Apply the Tempting Local Fix

Adding `gunshots?` to the regular expression would recover this one wording, but
it would not resolve the authority problem. Similar fixes have accumulated in
the past without producing durable quality gains.

A vocabulary addition may eventually be reasonable as low-level normalization
or a defensive fallback, but only after the authoritative evidence contract and
evaluation plan are settled. It must not be presented as the incident-pipeline
redesign.

## Prior Work That Must Be Read First

Read these files completely before changing code:

1. `docs/incident-architecture-diagnosis.md`
2. `docs/incident-v2-shadow-replay-handoff.md`
3. `docs/incident-v2-rollout-readiness.md`
4. `docs/incident-v2-replay-labels.json`

The June V2 work established:

- a canonical structured evidence direction;
- exact source quote validation;
- server-owned persistence and conflict decisions;
- replay corpus export and shadow comparison;
- human-label scoring;
- 72/72 passing labels in the latest recorded deterministic re-decision;
- a 250-case fresh replay with no model/replay errors;
- readiness for read-only shadowing, but explicitly **not** for replacing live
  persistence.

Those results are useful but not conclusive. The label set was partly selected
from known failures, deterministic changes were rescored against saved model
hypotheses, and live cutover was deliberately withheld.

## Current Repository State to Reconcile

The repository also contains a newer V3 design:

- `pizzad/IncidentFrameBuilderV3.cs`
- `pizzad/IncidentPlanExecutorV3.cs`
- `pizzad.Tests/IncidentFrameBuilderV3Tests.cs`
- `pizzad.Tests/IncidentPlanExecutorV3Tests.cs`
- `scripts/score_incident_v3_plan_shadow.ps1`

Configuration currently includes:

- `AiInsights.IncidentV2ShadowEnabled`;
- `AiInsights.IncidentV3FrameShadowEnabled`;
- `AiInsights.IncidentV3FrameCandidateLimit`;
- `AiInsights.IncidentV3PlanExecutorEnabled`;
- `AiInsights.IncidentV3PlanExecutorDryRun`.

`AutomaticInsightsService` contains both V2 shadow execution and V3 frame,
resolver, plan, and guarded execution paths. Recent history includes work to
guard V3 updates, hold unsafe membership changes, and simulate executor
validation.

Do not assume from names alone that V2, V3, or the legacy path owns any specific
runtime decision. Establish from current code, configuration, deployed state,
and audit records:

1. which path proposes candidates;
2. which path can write creates;
3. which path can write updates;
4. which paths are shadow-only or dry-run;
5. where legacy validation can veto or reshape V2/V3 output;
6. which path generated the July 16 Byron Drive rejection;
7. what audit evidence is recorded at every boundary.

There may be another active Codex task working on incident V3. Coordinate before
editing overlapping files. Do not overwrite or silently supersede that work.

## Product Questions the Redesign Must Answer

The new session should make these decisions explicit:

### What constitutes an incident?

Define the minimum evidence required for:

- a new incident;
- a continuation or operational update;
- a candidate that remains unresolved;
- a safe rejection.

Do not reduce this to an ever-growing event vocabulary. A strong transcript,
structured classification, location, talkgroup context, temporal context, and
corroboration can have different evidentiary roles without any one of them being
unconditional authority.

### Who owns each decision?

There should be one unambiguous owner for:

- candidate retrieval;
- event existence;
- call membership;
- conflict detection;
- incident identity;
- title and detail grounding;
- persistence;
- operator-facing explanations.

Retrieval similarity is not proof. Model prose is not proof. An existing title
is not proof. Regular expressions are appropriate for extraction and
normalization, not as the final semantic judge.

### How are uncertainty and disagreement represented?

When structured model evidence and deterministic checks disagree, the system
needs a first-class result such as held-for-review, insufficiently grounded, or
conflicting evidence. It should not manufacture a confident but inaccurate
reason such as “lacks a strong signal.”

### What does the operator need to understand?

The Incident Pipeline Inspector should be able to explain, chronologically:

- which calls entered consideration and why;
- what grounded event claims were extracted;
- what membership and identity decisions followed;
- which evidence supported or contradicted each decision;
- why the final incident was created, updated, held, or dropped.

The inspector is an observability surface, not the source of pipeline policy.

## Required Evaluation Discipline

Do not judge a redesign by unit tests or a handful of anecdotes alone.

### Build a representative, versioned corpus

Include:

- ordinary clean creates;
- ordinary clean updates;
- legitimate single-call incidents;
- multi-call continuations;
- routine traffic and administrative chatter;
- transport and hospital handoffs;
- ambiguous or noisy transcription;
- same-category but unrelated calls;
- conflicting locations, people, and vehicles;
- stale membership and stale-title cases;
- known false creates, false joins, false drops, and missed updates;
- the July 16 Byron Drive call and the other questionable July 16 drops.

Avoid a corpus dominated by known historical failures. Preserve a blind or
held-out set so repeated iteration does not merely memorize the review set.

### Label outcomes at the right level

Score more than binary accept/reject:

- event existence;
- correct incident identity;
- exact call membership;
- primary versus continuation roles;
- grounded location;
- grounded category;
- grounded title/detail facts;
- correct final action;
- useful and truthful explanation.

Track uncertainty and reviewer disagreement rather than forcing every row into
a false binary label.

### Compare complete pipelines

Run legacy, V2, V3, and proposed changes side by side over the same frozen input.
Report:

- false creates and false drops;
- incorrect joins and splits;
- missed or stale updates;
- membership precision and recall;
- narrative grounding defects;
- decision churn across repeated model runs;
- latency, timeout, and malformed-output behavior;
- regressions on ordinary traffic, not only improvements on watchlist cases.

Every claimed improvement should identify which labeled cases improved, which
regressed, and whether the change generalizes to the held-out corpus.

### Use shadow mode before persistence

Do not alter live persistence while exploring hypotheses. Use replay first, then
read-only live shadow telemetry. A live cutover needs a defined observation
period and explicit quality gates.

Never backfill or “recover” dropped candidates into production merely because a
new version would accept them. Historical recovery needs its own deliberate,
reviewable workflow.

## Recommended First Work Session

1. Read `AGENTS.md` and all four prior V2 documents listed above.
2. Inspect `git status`, worktrees, branches, and recent incident-related commits.
3. Create a new worktree from the intended integration base; do not work in the
   user's existing checkout.
4. Produce a concise current-state diagram of legacy, V2, and V3 authority and
   runtime flags. Verify it against code rather than assuming from naming.
5. Locate existing replay artifacts or regenerate a read-only corpus if the old
   artifacts were intentionally untracked or are unavailable.
6. Add the July 16 examples to a review dataset without changing live behavior.
7. Audit the existing labels for selection bias and identify the missing
   ordinary/negative/held-out cases.
8. Propose two or three falsifiable architectural hypotheses and the metrics
   that would distinguish them.
9. Review the proposal with the operator before implementing a new production
   path.

## Safety and Scope

- Do not mutate production data during diagnosis, replay, or shadow evaluation.
- Do not enable a write path merely because a feature flag exists.
- Do not deploy incident persistence changes without explicit approval.
- Do not restart Trunk Recorder as part of this work.
- Treat LM Studio/LMLink topology as distributed until verified; a localhost
  endpoint does not prove the model is physically local.
- Preserve exact timestamps and call IDs in replay evidence.
- Keep generated telemetry and local corpora out of git unless intentionally
  curated and scrubbed as test fixtures.

## Fresh Session Prompt

```text
Resume PizzaWave incident-pipeline redesign using
docs/incident-pipeline-redesign-handoff-2026-07-16.md as the controlling handoff.
Read AGENTS.md and every prerequisite document named in the handoff before
changing code. Start with a read-only architecture and runtime audit that
reconciles the legacy, V2, and V3 paths. Do not patch the Byron Drive case with
a keyword regex, mutate production data, or enable live writes. Use the existing
replay/shadow machinery, expand the evaluation corpus beyond known failures,
and propose falsifiable design hypotheses with measurable quality gates. Check
for another active incident-V3 worktree or Codex task before editing overlapping
files. Interview me when a product or operational policy choice materially
changes what should count as an incident.
```
