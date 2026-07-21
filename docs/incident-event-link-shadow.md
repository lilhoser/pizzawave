# Incident Event Link Shadow

Date: 2026-07-20

Status: local implementation; unscheduled and shadow-only.

## Decision

The next incident experiment constructs event membership through one-sided
positive links. For each new source observation, a learned proposer may either:

- propose one source-grounded link to one bounded, retrieved shadow event; or
- abstain.

It cannot declare a distinct event, split an event, assign a category or call
role, construct a hypothesis, or mutate event state. An abstention, malformed
response, inexact citation, or unknown candidate leaves the new observation in
an unresolved singleton event. Absence of a link is never evidence of a split.

## Ownership

Retrieval supplies at most eight opaque candidate tokens and a bounded sample of
their source observations. It is routing only.

The model returns one candidate token or abstains, a natural-language
relationship statement, uncertainty, and literal transcript citations from the
new observation and selected candidate.

Application code owns observation and event identifiers, verifies candidate
membership and exact quotations, maps valid positive evidence to one shadow
link, creates unresolved singletons, and projects event membership. It does not
use event vocabularies, talkgroup rules, regex recognition, categories, role
labels, confidence thresholds, or model-declared separation.

The append-only link ledger and projections use dedicated shadow tables. The
link interfaces cannot write `incidents` or `incident_calls`.

## Implemented Boundary

- `IncidentEventStateLinkPrompt` exposes only `propose_link` and `abstain`.
- `IncidentEventStateLinkContractValidator` validates bounded candidates and
  exact transcript citations.
- `IncidentEventStateLinkShadowCoordinator` converts invalid output and
  abstention into unresolved singleton projections.
- `IncidentEventStateLinkProjector` alone changes projected membership.
- `EngineDatabase` appends each link attempt and resulting projection in one
  transaction with content hashes and no-update/no-delete triggers.
- A first observation with no candidates becomes a singleton without invoking a
  model.

There is deliberately no HTTP endpoint, production-incident adapter, or writer
to production incident tables. A disabled-by-default hosted runner is available
for an explicitly configured live shadow experiment.

## First Local Gate

The offline proposer adapter replayed the frozen link-only prompt over all 18
already-reviewed development pairs using Paxan's advertised
`qwen/qwen3.6-35b-a3b@q8_0` identity. No new review was requested and the sealed
held-out corpus remained unopened.

Results:

- 18 reviewed pairs, including 11 reviewer-confirmed relationships;
- 16 contract-valid responses;
- 6 admitted positive links, all 6 reviewer-confirmed;
- positive-link precision `1.000` on this small development set;
- relationship recall `6/11` (`0.545`);
- 12 observations left as unresolved singletons;
- mean generation time 22.6 seconds, range 11.7 to 43.9 seconds;
- score artifact SHA-256
  `59F9005ED6A44B70879AA3D0E8FC7DD693E0C609DAFB2CC4A84C01B8E354FDA8`.

The two invalid responses proposed reviewer-confirmed links but included at
least one inexact extra quotation. They correctly remained singletons. The
validator was not relaxed after seeing those results.

For comparison, reinterpreting the previously frozen typed-evidence outputs
under the one-sided policy admitted 8/8 correct GPT-OSS links with `8/11`
relationship recall. The old Qwen outputs admitted 8 correct and 1 false link.
Those are transformations of old responses, not runs of the new prompt.

The same frozen link-only prompt was then run directly with GPT-OSS 20B on the
Ventax experiment host. It produced 17 contract-valid responses and admitted 4
positive links. All 4 matched the existing review, for observed positive-link
precision `1.000`, but relationship recall was only `4/11` (`0.364`). Mean
generation time was 3.7 seconds, range 3.3 to 4.4 seconds. The score artifact
SHA-256 is
`5B93CB232C8640F115329F1E59FF0C3EB3841A6AB63FF01926519D00DABC53ED`.
Ventax was used only for this offline comparison; its model was unloaded and
the SSH tunnel was closed afterward.

## Consequence

The one-sided boundary materially limits harm: misses become extra singletons
instead of false model-declared splits. Qwen has better recall but much higher
latency; GPT-OSS is faster but substantially less complete. Both have too little
recall and too little development evidence to justify production authority. A
bounded live shadow may gather passive operational evidence without making
either model authoritative.

## Bounded Live Shadow

When explicitly enabled, the runner samples at most one newly transcribed call
per configured interval. It advances to the newest call instead of accumulating
a model backlog, retrieves a bounded set of prior shadow events by embedding
similarity plus recent same-system shadow state, and permits only one positive
link or abstention. The first activation
sets a current-call fence and does not backfill history.

The model receives transcripts and timestamps only. Talkgroup names, categories,
radio labels, audio paths, and other call metadata are removed from the prompt.
Retrieval generates opaque candidates but is not proof of membership. Model
failures, malformed replies, and invalid citations are recorded as unresolved
singletons. The runner begins half an interval after process startup to reduce
overlap with the existing incident schedule.

The default configuration remains off. A bounded experiment can enable:

```json
{
  "aiInsights": {
    "incidentEventLinkShadowEnabled": true,
    "incidentEventLinkShadowIntervalSeconds": 300,
    "incidentEventLinkShadowLookbackMinutes": 120,
    "incidentEventLinkShadowCandidateLimit": 4
  }
}
```

## Initial OT Live Evidence

The bounded runner was enabled on Omicrontheta on 2026-07-20. The legacy v2 and
v3 shadow comparisons were disabled in the same configuration change to offset
model load; production incident construction was not changed. RPI was not
deployed. The initial backend manifest hash was
`eef3d7e4c88538b8cb90e394b2295c2f06b959821958bb8321e246af67104e19`.
The pre-experiment config is preserved on OT as
`/etc/pizzawave/pizzad.json.codex-link-shadow-20260720T2024Z.bak` with SHA-256
`1ade5bf6aac8a4a6ecca833b0242fc0d8b941574e147734d7d26627e60b692f4`.

The startup fence skipped historical calls. Three scheduled observations had no
candidate and became application-created unresolved singletons without model
calls. Live evidence then showed that embedding-only retrieval had omitted a
prior same-system shadow event, so candidate generation was broadened to fill
unused vector slots with recent same-system shadow state. This changes only what
the model may compare; it does not establish a relationship.

The next observation received two opaque candidates. The verified
`qwen/qwen3.6-35b-a3b@q8_0` proposer abstained, and the observation remained an
unresolved singleton. The call took 33,031 ms and used 729 prompt tokens plus 279
completion tokens (1,008 total). There was no proposer error, AI completion
health remained green, and transcription queue depth returned to zero. This is
operational smoke evidence only; it does not establish relationship accuracy.

Do not request new manual labels. Existing reviews remain smoke evidence only;
ordinary operator corrections may later be recorded as passive evaluation
evidence. A further model comparison may reuse these same reviewed inputs, but
must not tune the prompt or validator to their answers.

Only positive-link precision can justify further shadow work. Missed links
remain visible as extra singleton events and must not be repaired with static
semantic rules.

## Hybrid-Candidate Sparse Verifier Smoke

On 2026-07-21, the frozen OT replay window was used to test the next bounded
shape without writing live or production state. Candidate routing used the
union of four nearest local Nomic embedding results and four most recent
eligible observations. It did not filter or rank by system, talkgroup, label,
category, keyword, or regex. Qwen 3.5 27B Q4_K_M then received only the opaque
candidate pairs and the sparse positive-link contract.

The first ten chronological micro-batches contained 393 candidate pairs. All
10 requests completed, all responses passed deterministic validation, and no
request timed out. Average request latency was 9.2 seconds, interpolated p95 was
34.6 seconds, and the maximum was 43.2 seconds. The requests used 44,555 total
tokens. These measurements pass the small-sample contract and 60-second p95
smoke gates, but they are not sustained-capacity evidence.

Semantic inspection rejects this configuration for promotion:

- `call:1404871` correctly linked to `call:1404845`; both describe the same
  West Side Drive behind-the-store contact.
- `call:1404933` correctly did not link to `call:1404895`; the shared Tennessee
  and person-name fragments were not sufficient evidence.
- `call:1404831` failed to link to candidate `call:1404821`; the supplied pair
  is the same lost-lumber truck event. Because the pair was present in the
  candidate set, this is verifier recall failure rather than retrieval failure.

The run therefore admitted one of the two clear positive relationships in this
slice and produced no observed false link. This tiny slice cannot estimate an
accuracy rate, but the concrete miss is enough to withhold authority. Do not
repair it with special vocabulary, metadata rules, or prompt tuning against
these inspected answers. The replay artifact identity is
`ot-20260721-embedding-recent-sparse-qwen35-27b-q4-smoke-v1`.
