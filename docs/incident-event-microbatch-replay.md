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

The sparse link experiment uses the same candidate-backed source context but
does not require the model to generate a negative record for every candidate:

```powershell
dotnet run --project tools/IncidentEventMicroBatchReplay/IncidentEventMicroBatchReplay.csproj -- `
  --candidate-backed-verification-replay true `
  --sparse-link-mode true `
  --database C:\path\to\incident-replay.db `
  --start 1784603146 `
  --end 1784635663 `
  --replay-id example-sparse-links `
  --candidate-directory artifacts/incident-event-microbatch-replay/example-candidates `
  --output artifacts/incident-event-microbatch-replay/example-sparse-links `
  --model qwen/qwen3.5-27b@q4_k_m `
  --reasoning-effort none
```

The model must mark the envelope complete after examining every supplied
candidate and returns only positively supported candidate tokens. Omission
means no link; an empty link list is valid. Deterministic validation admits no
links if the envelope is incomplete or any proposal has an unknown or duplicate
candidate, gives one new observation multiple targets, lacks endpoint-owned
transcript evidence, or violates the typed schema. Use `--sparse-link-mode true`
with the review-package command to apply the same contract to the fixed review
gate.

A sparse proposal run can be adjudicated one proposed pair at a time without
rerunning its batch stage:

```powershell
dotnet run --project tools/IncidentEventMicroBatchReplay/IncidentEventMicroBatchReplay.csproj -- `
  --pairwise-adjudication-replay true `
  --database C:\path\to\incident-replay.db `
  --start 1784603146 `
  --end 1784635663 `
  --replay-id example-pairwise-adjudication `
  --candidate-directory artifacts/incident-event-microbatch-replay/example-candidates `
  --proposal-directory artifacts/incident-event-microbatch-replay/example-sparse-links `
  --output artifacts/incident-event-microbatch-replay/example-pairwise-adjudication `
  --model qwen/qwen3.6-35b-a3b@q8_0 `
  --reasoning-effort none
```

This stage verifies that the proposal manifest names the same frozen database,
candidate manifest, and sparse prompt. Each admitted source candidate is then
mapped to an isolated one-pair prompt. Unknown or duplicate source tokens,
source mismatches, model-routing errors, invalid evidence, and request failures
remain rejected. First-stage and pairwise artifacts stay separate so a later
report cannot hide which stage introduced or removed a link.

For a resource preflight, candidate replay can enumerate every chronologically
legal pair without calling a model or inspecting transcript content:

```powershell
dotnet run --project tools/IncidentEventMicroBatchReplay/IncidentEventMicroBatchReplay.csproj -- `
  --candidate-replay true `
  --exhaustive-mode true `
  --database C:\path\to\incident-replay.db `
  --start 1784603146 `
  --end 1784635663 `
  --replay-id example-exhaustive `
  --output artifacts/incident-event-microbatch-replay/example-exhaustive
```

This mode is an audit tool, not a proposed retrieval policy. It uses only the
frozen chronological batch and context boundaries and gives the resulting
pairs no membership authority.

The resource-compatible hybrid candidate replay embeds transcript text locally,
then unions a fixed number of nearest embedding neighbors with recent eligible
observations. It exposes neither score nor rank to the verifier and does not
filter by system, category, talkgroup, label, keyword, or regex:

```powershell
dotnet run --project tools/IncidentEventMicroBatchReplay/IncidentEventMicroBatchReplay.csproj -- `
  --candidate-replay true `
  --embedding-mode true `
  --database C:\path\to\incident-replay.db `
  --start 1784603146 `
  --end 1784635663 `
  --replay-id example-embedding-recent `
  --output artifacts/incident-event-microbatch-replay/example-embedding-recent `
  --endpoint http://127.0.0.1:1234/v1 `
  --model experiment/nomic-embed-text-v1.5 `
  --semantic-candidates 4 `
  --recent-candidates 4
```

The exact embedding endpoint and model identity are frozen in the manifest.
Vectors are cached only in the ignored replay directory. Transcriptless source
observations remain in the full-coverage plan but cannot become semantic
candidates because they cannot supply transcript evidence.

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

The sparse positive-link contract was then tested with the same Qwen 3.5 27B
runtime. It again passed the six-case review gate with three of three reviewed
positive links, no false link, both reviewer-unresolved pairs withheld, and no
contract error. Case latency ranged from 23.46 to 52.21 seconds.

On the same first ten candidate-bearing ordinary requests, covering 26
retrieved pairs, the sparse contract returned and admitted two links with no
invalid proposal or failed request. Direct transcript inspection supports both:
the unit-23 lost-lumber continuation and the officer arriving at the Westside
Drive male-behind-the-store call. It did not reproduce the dense contract's
Tennessee false link. Average request latency was 18.65 seconds, median was
1.32 seconds, interpolated p90 was 46.72 seconds, interpolated p95 was 48.70
seconds, and maximum was 50.68 seconds. The ten requests used 10,801 prompt
tokens and only 455 completion tokens; empty-link responses completed in about
0.95 to 1.40 seconds. The sparse boundary therefore passed this bounded smoke
test.

No live deployment is justified yet. Full-coverage chronological planning,
high-recall retrieval, opaque identifiers, source evidence, sparse positive
proposals, and candidate-backed fail-closed validation are the surviving
architecture pieces. The grouped verifier and dense one-decision-per-observation
contract are rejected.

The sparse result qualifies the boundary for a larger frozen evaluation, not
for persistence. Ten early candidate-bearing requests do not provide enough
negative diversity or tail evidence. There is also no production resource plan
yet: these candidates were precomputed by GPT-OSS, while the dense 27B verifier
was loaded separately. Paxan cannot be assumed to keep both large models
resident. The next complete-pipeline experiment should first replace the large
candidate model with a resource-compatible high-recall retriever, such as the
existing small embedding path, and prove candidate recall on the fixed review
cases. Only then should the sparse verifier run a stratified or complete frozen
window with semantic inspection, repeated-run stability, and the unchanged
60-second p95 gate. Do not change persistence until that resource-feasible
pipeline passes.

An exhaustive chronological preflight then tested whether retrieval could be
removed entirely. It deterministically enumerated every earlier context or
same-batch observation for every new observation. The 2,253-observation window
produced 59,804 pairs, or 26.54 per observation. Per-batch pair count had a
median of 130, p95 of 285, and maximum of 354. This is rejected before model
inference: peak prompts cannot be assumed to fit the verifier's 8,192-token
runtime, and evaluating tens of thousands of obviously weak pairs is not a
credible Paxan resource plan. Some learned, bounded retrieval is required, but
its output remains routing rather than proof.

A local-Paxan Nomic Embed Text v1.5 Q4_K_M pass then tested a four-nearest plus
four-recent union over the same window. The 84 MB, 768-dimensional embedding
model coexisted with Qwen 3.6 and embedded 2,232 transcript-bearing observations
in about 28 seconds; 21 transcriptless observations remained planned but
unembedded. Candidate generation produced 16,386 pairs, or 7.27 per planned
observation. Per-batch pair count had a median of 37, p95 of 74, and maximum of
94. The experiment-only local embedding model was unloaded afterward.

The hybrid set contained 521 of the 955 GPT-OSS-proposed pairs. That 54.6%
overlap is comparison evidence, not recall: neither retriever's ordinary
proposals are reference truth. The hybrid must not be tuned to imitate GPT-OSS.
Its remaining falsifiable question is whether the resource-feasible hybrid plus
sparse verifier preserves reviewed links and behaves safely on production-shaped
traffic. Until that complete pipeline is tested, candidate recall remains
unresolved.

That question was then tested on the first ten hybrid batches. Qwen 3.5 27B
Q4_K_M processed 393 candidate pairs with no request or contract failure. It
averaged 9.2 seconds per batch, had a 34.6-second interpolated p95 and
43.2-second maximum, and used 44,555 tokens. It admitted the supported West
Side Drive link and rejected the misleading Tennessee pair, but missed the
supported lost-lumber link even though retrieval supplied it. The hybrid
retriever therefore passed these known-pair checks while the verifier failed
recall.

The identical batches were then run through the resident Qwen 3.6 35B-A3B Q8_0
model under shared endpoint load. All ten requests and contracts completed; the
20.8-second average, 33.9-second interpolated p95, and 36.6-second maximum were
within the bounded latency gate. It recovered both supported links but also
admitted two unsupported links: the known Tennessee/person-name challenge and
an unrelated match based on generic validity language about warrants and
vehicle registration. Four admitted links therefore contained two supported
and two unsupported relationships.

Both tested verifier configurations are rejected for authority. The experiment
also establishes that endpoint-owned transcript identifiers provide reference
integrity but not semantic proof: Qwen 3.6 cited the correct records while
inventing relationships between them. Do not open the sealed held-out corpus,
tune the prompt to these inspected cases, or change incident persistence based
on these runs.

As a post-hoc architectural diagnostic, the four Qwen 3.6 batch proposals were
then presented independently through the unchanged sparse contract. The two
supported links were admitted in 16.3 and 14.0 seconds. The generic-validity
and Tennessee/person-name false links were rejected in 4.1 and 3.2 seconds.
All four outputs were contract-valid, for 38.4 seconds total model time. This
single inspected trial is not accuracy or stability evidence, but it shows that
candidate competition inside the batch contributed to the false admissions
and that pairwise adjudication is worth testing as a separate stage. It does
not justify persistence or a full-window run.
