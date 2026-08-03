# Incident Pipeline Architecture

Status: closed production decision, updated 2026-08-03.

## Final production decision

PizzaWave will keep the existing Qwen-driven incident pipeline as the supported
production implementation. Qwen proposes incident membership and presentation;
application code retains source ownership, validates membership, rejects
conflicts, preserves the durable work queue, and fails closed on malformed or
failed requests. This is an intentionally practical decision: the pipeline is
not perfect, but it is operating continuously and its limitations are better
understood than those of the attempted replacements.

The proposed specialized local membership model is deferred indefinitely. The
preserved evidence contains only 18 directly reviewed call relationships and
eight reviewed newer examples, far short of the corpus required to train and
evaluate a replacement honestly. It is not a release dependency.

Radio-source linkage remains supporting evidence in the transmission ledger and
review tools. It is not enabled as a production candidate source because the
bounded observation produced only two eligible cases from 664 calls and
improved no incident. When both its production and observation settings are
disabled, PizzaWave skips that candidate-building work entirely.

No further general incident-pipeline experiments are planned. Reopen this
decision only for a concrete production defect with a reproducible saved case
and a bounded acceptance test.

The current production incident pipeline remains active on OT and RPI. The
deferred replacement material below is preserved so a future project does not
have to rediscover its safety constraints, but no replacement release is now
scheduled. The July experiments are historical evidence and are archived under
[`archive/incident-pipeline-2026-07`](archive/incident-pipeline-2026-07/README.md).

## Product contract

An incident represents one real-world event assembled from one or more calls.
Internally, each call is a conversation segment containing one or more radio
transmissions. The call remains the canonical incident-membership unit; a
transmission is supporting evidence, not a separate membership object or an
incident.

The replacement must:

- avoid combining unrelated calls above all else;
- recover materially more legitimate incidents than the current pipeline;
- normally produce coherent multi-call incidents, not transmission-sized
  fragments;
- retain unresolved evidence for reconsideration instead of publishing it as a
  singleton or silently discarding it;
- support legitimate single-call incidents when the evidence itself describes
  a complete event;
- produce evidence-grounded titles, summaries, and locations;
- stay current across OT, RPI, and future systems using shared Paxan capacity;
- tolerate approximately ten minutes of ordinary delay, longer when it
  substantially reduces demand, and occasional bounded backlogs that drain;
- automate routine decisions. Human review is for a bounded ambiguous tail,
  not every incident.

The upstream evidence contract must preserve decoder-delimited transmissions,
radio source identifiers when available, and their parent conversation segment
(the user-facing call). The
implementation direction is defined in
[`transmission-ledger-architecture.md`](transmission-ledger-architecture.md).
This additional structure improves retrieval and review, but it does not make
radio identity a deterministic incident-membership rule.

No address, phrase, category, talkgroup, radio system, quality label, regex,
taxonomy, compatibility table, embedding score, or hand-tuned score may decide
incident membership. Retrieval may reduce context but is not proof.

## Deferred replacement design

The sections below preserve the accepted design for a possible future
specialized replacement. They are not the active delivery plan and do not block
the supported Qwen pipeline.

## Deferred model roles

The deferred replacement design has two model roles.

### Local membership model

PizzaWave will create and operate a small local model specialized for incident
membership. Its job is to turn a bounded evidence window into complete event
hypotheses, decide which observations belong together, identify likely
non-incidents, and retain uncertainty for later reconsideration.

The model will be trained from reviewed incident packages. It will not be
trained to recall database identifiers, row numbers, array positions, pair
numbers, or opaque tokens. The final output contract must preserve source
identity without asking the model to reproduce those values; that contract is
part of the training implementation package and must pass held-out identity and
coverage tests before live shadowing.

This model is the routine semantic membership authority after acceptance. It is
not a deterministic classifier disguised as regex, and it does not require a
second production request to approve every decision.

### Qwen presentation model

Qwen 3.6 35B-A3B remains the higher-capability local model for work performed
after membership is established:

- grounded incident titles;
- summaries;
- location extraction and revision;
- difficult fallback or exception analysis.

Qwen is not the routine membership generator. Presentation work may be delayed,
batched, or revised without delaying the application-owned incident identity.
Material title changes retain history and appear with an `Updated` badge whose
callout shows the previous title.

The current Paxan LM Studio tuning is intentional: 65,536-token context,
parallelism four, flash attention, and the other operator-configured settings
must not be changed incidentally.

## Training authority

Qwen and GPT-OSS may generate independent candidate packages offline. Their
agreement is useful silver training data, not truth. Disagreements and
high-impact examples are prioritized for human review.

Gold data means a package was reviewed under frozen instructions, received the
required independent reviews, and had disagreements adjudicated. Model
confidence is not calibrated probability and is not an admission threshold.

The working budget is up to approximately $2,000. The initial target is:

- tens of thousands of synthetic or teacher-produced silver packages;
- roughly 600–1,000 corrected training packages;
- roughly 300–400 adjudicated gold packages kept separate for evaluation;
- approximately 900–1,400 total human-reviewed packages, adjusted after a
  50–100-package pilot measures review time and disagreement rate.

The review UI presents complete model-produced incidents and evidence. Reviewers
remove or add calls, split or combine incidents, mark missed incidents, and
approve or correct title, summary, and location through controls rather than
typing labeling terminology. Their actions create labels automatically.

## Runtime flow

1. PizzaWave appends eligible observations to a pending-evidence ledger.
2. A bounded scheduler selects new evidence, fairly rotates unresolved evidence,
   and supplies a small retrieved set of active incident evidence.
3. The local membership model produces complete event hypotheses in one
   semantic generation.
4. Application code validates contract integrity, source provenance, complete
   coverage, idempotency, and conflicts. It does not reinterpret semantics.
5. Accepted hypotheses create or extend incidents through the audited writer.
   Unresolved evidence remains eligible in later windows.
6. Qwen generates or revises title, summary, and location from accepted event
   evidence. Presentation history is append-only.

Malformed output, unavailable inference, and ambiguous source ownership fail
closed without losing pending observations. Existing incident membership is
never destructively rewritten by a model response.

## Rejected patterns

Do not reintroduce:

- global reconstruction from a large mixed window using Qwen;
- exhaustive pair comparisons or a pair proposer/critic;
- model-returned IDs, indices, positions, pair keys, or hashes;
- copying entire transcripts in output merely to recover identity;
- a second request to the same model described as independent verification;
- confidence cutoffs or model agreement treated as semantic proof;
- standalone-first publishing followed by after-the-fact grouping;
- already-published singleton incidents as the working memory;
- human approval of every routine incident;
- endless prompt or model bake-offs without frozen acceptance criteria.

## Release gates

One implementation and evaluation cycle is authorized. The gates are frozen
before training:

- no recurring or systematic false-merge pattern and fewer unrelated-call
  merges than the current pipeline;
- clear improvement in legitimate-incident capture;
- coherent multi-call events instead of fragmented singletons;
- bounded unresolved workload and no silent observation loss;
- grounded presentation and valid source provenance;
- stable results on a sealed gold set, with counts and severe examples shown;
- combined OT/RPI demand plus measured headroom remains current under ordinary
  load and drains an allowed burst backlog automatically.

The sequence is offline held-out evaluation, one non-mutating OT shadow,
bounded OT canary, OT ownership, then RPI ownership. Each deployment has a
no-backfill fence and rollback. Trunk Recorder is never restarted for an
incident-pipeline rollout.

If the student fails the frozen semantic gates, stop and report the failure; do
not invent a renamed architecture. If semantics pass but capacity fails, tune
batching/scheduling or consider hardware without changing the semantic task.
