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
  --model qwen/qwen3.6-35b-a3b@q8_0 `
  --reasoning-effort none
```

`--reasoning-effort` and `--reasoning-tokens` are optional and are recorded in
the verification manifest. Use them only when the endpoint advertises those
options for the selected model. They are inference-mode controls, not a
substitute for the unchanged accuracy and latency gates.

The candidate-backed chronological variant keeps the retriever's bounded
context but uses the chronological link contract instead of a pair-verifier
contract:

```powershell
dotnet run --project tools/IncidentEventMicroBatchReplay/IncidentEventMicroBatchReplay.csproj -- `
  --candidate-backed-verification-replay true `
  --database C:\path\to\incident-replay.db `
  --start 1784603146 `
  --end 1784635663 `
  --replay-id example-candidate-backed `
  --candidate-directory artifacts/incident-event-microbatch-replay/example-candidates `
  --output artifacts/incident-event-microbatch-replay/example-candidate-backed `
  --model qwen/qwen3.6-35b-a3b@q8_0
```

Application validation admits a proposed link only when the selected endpoint
pair appeared in retrieval output. A chronological but unretrieved target is
left unresolved.

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

Qwen 3.6 35B-A3B Q8 correctly handled the same six review cases with the
chronological link-only contract: three positive links verified, the definite
negative left unresolved, and both reviewer-unresolved cases left unresolved.
That result did not establish the accuracy of the later grouped pair-verifier
contract. In grouped ordinary-traffic verification:

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

Qwen 3.5 9B Q8 was then tested in isolation on Paxan with full GPU offload.
With the model's default reasoning mode, all six requests returned empty answer
content after 37.6 to 44.7 seconds because the 1,200-token output allowance was
consumed before a structured answer was emitted. LM Studio advertises `off` and
`on` as the model's supported reasoning modes. Repeating the unchanged review
with `reasoning_effort=none` produced structurally valid answers in 3.5 to 28.9
seconds, but rejected all three reviewed positive links. It therefore had zero
false positives and zero positive recall. The ordinary-traffic replay was not
run because the model had already failed the accuracy gate.

The grouped pair-verifier contract was subsequently run against the six review
cases. Qwen 3.6 Q8 verified only one of three positive pairs with default
reasoning, with reasoning disabled, and with low reasoning capped at 512 tokens.
Changing the negative decision name from `reject` to `unresolved` and aligning
the weak-signal wording with the chronological contract did not change that
result. The grouped pair-verifier boundary is therefore rejected for false
negative behavior; its earlier ordinary-traffic smoke was not accuracy proof.

The candidate-backed chronological variant retained the successful link-only
decision contract and deterministically blocked targets outside retrieval. On
ten frozen ordinary-traffic requests it produced 26 decisions, admitted four
links, had no invalid decisions or failed requests, averaged 35.97 seconds, and
had a 65.20-second p90 and 65.88-second p95/maximum. Direct transcript inspection
found three plausible continuations and one clear semantic-only false link: two
different person checks were joined because both mentioned Tennessee. This
variant fails both the zero-high-impact-false-link gate and the 60-second tail
gate.

Gemma 4 26B-A4B Q4 was also tested locally on Paxan with full GPU offload and
reasoning disabled. It completed the six chronological review cases in 3.16 to
4.02 seconds each, but linked only two of three reviewed positives. Every case
also contained at least one contract-invalid unresolved decision that selected
a target, and one positive decision used `propose_link` as its relationship
statement. It failed the accuracy and contract gates, so no ordinary-traffic
run was performed.

Qwen 3.5 27B Q4_K_M was then tested locally on Paxan with full GPU offload,
8,192 tokens of context, one parallel slot, and reasoning disabled. It passed
the unchanged six-case chronological review gate: all three reviewed positives
were linked, the definite negative and both reviewer-unresolved cases were
withheld, and all six decisions were contract-valid. Request latency ranged
from 5.66 to 52.42 seconds.

That small review result did not generalize to the output contract under
ordinary candidate-backed traffic. Across ten requests and 26 decisions, the
model admitted two links; transcript inspection supports both as genuine
continuations. However, 24 non-link decisions were contract-invalid because the
model selected and cited a target while declaring the decision unresolved. The
run averaged 25.29 seconds per request, had a 15.62-second median, an
approximately 63.46-second interpolated p95, and a 66.24-second maximum. It
therefore failed both the zero-invalid-decision gate and the 60-second p95 gate.
The larger frozen run was not performed, and Qwen 3.6 was restored with its
previous Q8_0, 65,536-context, four-parallel-slot configuration.

No live deployment is justified by this experiment. Full-coverage chronological
planning, high-recall retrieval, opaque identifiers, source evidence, and
candidate-backed fail-closed validation remain useful. Neither tested verifier
contract is acceptable: grouped pairs lose reviewed positives, while the
chronological linker admits a semantic-only false link and misses tail latency.
The dense 27B result adds a separate warning: a model can pass the small review
set perfectly while failing the ordinary output contract and latency gates.

Before spending time on another model, the next falsifiable experiment should
remove mandatory negative-decision generation. A strict sparse verifier would
emit only positively supported link proposals inside a completion envelope;
omitted candidates would remain unlinked. Deterministic code would validate
opaque observation tokens, evidence ownership, retrieval eligibility, schema
completeness, and fail closed on malformed output. This is an output-boundary
hypothesis, not evidence that Qwen 3.5 27B is acceptable. It must rerun the same
review and frozen ordinary-traffic gates, including semantic inspection and the
60-second p95 limit. Do not change persistence until one complete pipeline
passes both gates.
