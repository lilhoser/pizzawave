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

Do not request new manual labels. Existing reviews remain smoke evidence only;
ordinary operator corrections may later be recorded as passive evaluation
evidence. A further model comparison may reuse these same reviewed inputs, but
must not tune the prompt or validator to their answers.

Only positive-link precision can justify further shadow work. Missed links
remain visible as extra singleton events and must not be repaired with static
semantic rules.
