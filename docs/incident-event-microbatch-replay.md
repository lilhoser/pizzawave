# Incident Event Micro-Batch Replay

Status: offline architecture experiment. It does not construct or modify live
PizzaWave incidents.

## Purpose

The earlier link shadow sampled only the newest eligible call on each timer
tick. It therefore skipped most traffic and made one model request for one new
call. This replay tests a different boundary:

- every source call in a frozen half-open time window is planned exactly once;
- calls are processed in chronological micro-batches bounded only by elapsed
  time and observation count;
- one bounded retrieval request proposes plausible earlier pairs for a batch;
- one grouped verification request independently evaluates all retrieved pairs
  from that batch;
- a candidate can only target an earlier opaque observation;
- application validation rejects future targets, unknown identifiers, and
  transcript evidence from the wrong endpoint;
- a missing or invalid link leaves the observation unresolved; it never proves
  that two observations describe different events.

Talkgroup names, categories, existing incident membership, and other semantic
labels are not included in the model prompt. Transcript text and timestamps are
the evidence surface. Timing is context, not proof of a link.

## Frozen snapshot

`scripts/export_incident_event_replay_snapshot.py` creates a calls-only SQLite
snapshot from a source database opened in read-only mode. It refuses to
overwrite an existing output and removes a partial output if export fails.

Example on a host with access to the source database:

```text
python3 scripts/export_incident_event_replay_snapshot.py \
  --source /var/lib/pizzawave/pizzad.db \
  --output /tmp/incident-replay.db \
  --start 1784603146 \
  --end 1784635663
```

The replay runner reads only the snapshot. The snapshot, prompts, responses,
and reports are local experiment artifacts and must not be committed.

## Planning-only check

```powershell
dotnet run --project tools/IncidentEventMicroBatchReplay/IncidentEventMicroBatchReplay.csproj -- `
  --database C:\path\to\incident-replay.db `
  --start 1784603146 `
  --end 1784635663 `
  --replay-id example-replay `
  --output artifacts/incident-event-microbatch-replay/example-replay `
  --model openai/gpt-oss-20b `
  --batch-size 12 `
  --batch-span-seconds 60 `
  --context-size 24 `
  --context-lookback-seconds 1200 `
  --dry-run
```

`manifest.json` records the snapshot hash, complete plan, resource bounds,
endpoint, and exact model identity. Reusing an output directory resumes only
when the frozen snapshot, plan, endpoint, and model still match. Each successful
batch is stored atomically and is not regenerated on resume.

## Model run

Remove `--dry-run` to call an OpenAI-compatible local endpoint. Add
`--max-batches N` for a bounded smoke test. Each `batch-NNNNN.json` records the
opaque token map, model decisions, deterministic validation errors, admitted
positive links, duration, and token usage. `report.json` summarizes progress.

A final-decision response must account for every supplied item exactly once.
Validation is fail-closed per decision: one invalid proposed link is left
unresolved without discarding valid decisions for other observations in the
same batch.

## Two-stage experiment

The candidate stage is deliberately high recall and has no authority to link
observations. It may return at most two earlier targets for one new observation.
Its reasons are diagnostic only. Run it with:

```powershell
dotnet run --project tools/IncidentEventMicroBatchReplay/IncidentEventMicroBatchReplay.csproj -- `
  --candidate-replay true `
  --database C:\path\to\incident-replay.db `
  --start 1784603146 `
  --end 1784635663 `
  --replay-id example-candidates `
  --output artifacts/incident-event-microbatch-replay/example-candidates `
  --model openai/gpt-oss-20b
```

The grouped verifier consumes those candidate batch files. The retriever's
selection and explanation are not sent as evidence; the verifier sees the exact
endpoint transcripts and must decide independently:

```powershell
dotnet run --project tools/IncidentEventMicroBatchReplay/IncidentEventMicroBatchReplay.csproj -- `
  --verification-replay true `
  --database C:\path\to\incident-replay.db `
  --start 1784603146 `
  --end 1784635663 `
  --replay-id example-verification `
  --candidate-directory artifacts/incident-event-microbatch-replay/example-candidates `
  --output artifacts/incident-event-microbatch-replay/example-verification `
  --model qwen/qwen3.6-35b-a3b@q8_0
```

This is an evaluation boundary, not a commitment to keep two large language
models loaded in production. Paxan's 24 GB GPU cannot be assumed to hold both
models concurrently. The experiment must separately establish whether a small
retriever, embedding retrieval, or a single-model alternative can preserve
candidate recall within the production resource envelope.

## Decision gates

This experiment is not a route around the gates in
`docs/incident-event-state-evaluation-gates.md`. A runtime design remains
rejected unless it demonstrates the required relationship precision and recall,
zero high-impact false links, and at least 1.5 times the 95th-percentile arrival
rate within the Paxan production envelope.

## 2026-07-21 experiment evidence

The frozen OT window covered `2026-07-21T03:05:46Z` through
`2026-07-21T12:07:43Z`. It contained 2,253 calls. The rejected live link runner
sampled only 102 calls from the same period. The micro-batch planner assigned all
2,253 calls exactly once across 421 one-minute batches.

The GPT-OSS 20B candidate pass completed all 421 batches on Paxan after one
structurally invalid batch was retried fail-closed. It produced 955 candidate
pairs, averaged 2.76 seconds per batch, and had a 9.36-second maximum. Candidate
count per batch had a median of two, 95th percentile of five, and maximum of
eleven; 74 batches produced no pair. On the existing six-case relationship
review, candidate retrieval included all three reviewed positive pairs, excluded
the one definite negative, and excluded both unresolved pairs. This is retrieval
evidence only, not membership accuracy.

Qwen 3.6 35B-A3B Q8 correctly handled the same six review cases as a final
verifier: three positive links verified, the definite negative rejected, and
both unresolved cases rejected. In grouped ordinary-traffic verification:

- Ventax processed ten requests containing 27 pairs at a 36.37-second average
  and 79.91-second maximum.
- Paxan processed the same ten requests at a 43.31-second average, 43.55-second
  median, about 70.71-second p90/p95 in this small sample, and 74.32-second
  maximum.
- Both runs verified the same two links. Direct transcript inspection supports
  both as plausible continuations.

Paxan therefore failed the 60-second tail-latency gate even though average
throughput was potentially adequate. A full 347-request Qwen run was not
performed because it would spend hours reconfirming a failed boundary.

Other available model paths did not solve both constraints:

- GPT-OSS as grouped final verifier rejected all three reviewed positive pairs.
- Qwen Q4 failed structured output on four of six review cases and verified no
  reviewed positive pair.
- GLM 4.7 Flash did not return a single reviewed pair within 120 seconds.
- Gemma 4 did not return the first ordinary batch within 180 seconds.

No live deployment is justified by this experiment. The architectural boundary
is worth retaining, but the production implementation should not alternate two
large resident models on Paxan. The next model experiment should keep the frozen
corpus and unchanged gates, replace GPT-OSS candidate retrieval with embeddings
or another small high-recall retriever, and evaluate a faster verifier that fits
beside that retriever on Paxan. Do not change persistence until that combination
passes both the fixed relationship review and production-shaped tail latency.
