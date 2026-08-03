# Source-Bound Incident Membership Output Contract

Status: the source-binding mechanism remains valid. The open-window grouping
procedure described below is retired after the July 31 Qwen shadow. It must not
be enabled in production. The replacement is the exact-existing-incident
decision contract described in
`docs/incident-target-membership-adapter-2026-07-31.md`.

## Decision

An ordinary JSON or free-text membership response cannot safely identify its
sources without asking the model to reproduce an identifier, ordinal, position,
hash, opaque token, or transcript. The membership model therefore does not
return a parseable membership document.

PizzaWave owns a constrained decision form. Each evidence observation has an
application object containing two separate parts:

- a private source identity retained by PizzaWave;
- model-visible evidence containing time, transcript, neutral radio context,
  and audio duration, but no database identifier or application token.

For each application-owned event-hypothesis slot, the constrained decoder
presents one application-bound decision cell for every source. The model may
choose only `member` or `not_member` in that cell. After event hypotheses are complete,
every source not assigned to an event receives exactly one constrained choice:
`unresolved` or `non-incident`. The membership output contains no generated
prose, source key, or application incident identity. Qwen generates presentation
only after PizzaWave accepts membership.

The decoder adapter passes the original source binding object into the capture
API. PizzaWave never reconstructs identity from generated text or output order.
Forced form scaffolding and private identities are excluded from training loss.

## Integrity invariants

Application validation, not semantic reinterpretation, enforces:

1. every active hypothesis has a decision cell for every bound source;
2. an active hypothesis contains at least one member;
3. a source belongs to no more than one hypothesis;
4. a source assigned to a hypothesis has no residual disposition;
5. every other source is explicitly unresolved or non-incident;
6. bindings from another inference session are rejected;
7. completion is one-way and immutable;
8. malformed or incomplete capture fails closed, leaving ledger evidence pending.

An entirely unresolved window is valid and creates no singleton incidents.
A one-source hypothesis is valid only as a semantic model decision; gold
evaluation separately measures unsupported singleton fragmentation.

## Proof tests

`IncidentMembershipOutputContractTests` proves:

- two observations with identical transcript text retain distinct application
  identities without those identities appearing in model-visible evidence;
- recording decision cells in a different order maps to the same source;
- permuting input source order does not change identity mapping;
- a missing decision cell fails;
- missing final coverage fails;
- double membership and member-plus-residual coverage fail;
- a binding from another session fails;
- a fully unresolved window remains pending without publishing singletons.

`IncidentMembershipConstrainedAdapterTests` additionally proves:

- the adapter can produce several hypotheses while giving every source exactly
  one final disposition;
- duplicate transcripts remain separately bound without model-generated
  identity;
- input reordering does not change source mapping;
- private call and observation identities never enter the model prompt;
- a model failure, model-identity mismatch, malformed choice, oversized source
  window, or incomplete result fails closed;
- a window with no supported hypothesis carries all uncertain evidence forward;
- the OpenAI-compatible transport uses a strict one-field response schema.

This contract authorizes no live persistence. A real inference adapter must
implement application-bound constrained cells; accepting a free-form JSON
substitute would violate the contract and requires stopping the project.
