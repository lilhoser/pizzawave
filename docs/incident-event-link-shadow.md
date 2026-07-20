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

There is deliberately no scheduler, HTTP endpoint, dependency-injection
registration, production-incident adapter, deployment configuration, or live
writer.

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

## Consequence

The one-sided boundary materially limits harm: misses become extra singletons
instead of false model-declared splits. The first Qwen run nevertheless has too
little recall, too little development evidence, and too much latency to justify
live shadow scheduling or production authority.

Do not request new manual labels. Existing reviews remain smoke evidence only;
ordinary operator corrections may later be recorded as passive evaluation
evidence. A further model comparison may reuse these same reviewed inputs, but
must not tune the prompt or validator to their answers.

Only positive-link precision can justify further shadow work. Missed links
remain visible as extra singleton events and must not be repaired with static
semantic rules.
