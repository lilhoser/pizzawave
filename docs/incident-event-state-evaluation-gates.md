# Incident Event-State Evaluation Gates

Date frozen: 2026-07-19

Status: development and held-out gates frozen before any sealed held-out review

## Scope

These gates evaluate a blank-slate, shadow-only incident pipeline. Existing
incident records, V2/V3 decisions, model agreement, talkgroup identity,
retrieval rank, temporal proximity, and embeddings are not reference truth.

The six-case relationship package is development calibration only. One review
may guide falsification, but it cannot establish final accuracy. Held-out
acceptance requires the independent-review and reconciliation process in
`incident-event-state-corpus-protocol.md`.

## Fixed Evaluation Shape

- Use the frozen development and sealed held-out blocks without removing quiet
  or difficult observations.
- Freeze model, prompt, context, decoding, retrieval, software, and
  configuration identities before a scored run.
- Run three complete trials with identical inputs and declared randomness
  controls.
- Score challenge, ordinary development, and ordinary held-out results
  separately. Do not combine them into a weighted score.
- Record invalid output, timeout, model-routing failure, critic disagreement,
  and abstention as outcomes rather than retrying them out of the score.

## Source And Safety Gates

All are mandatory:

- 100% of admitted claims, relationships, and membership changes pass exact
  provenance and reference-integrity validation.
- Zero fabricated people, vehicles, locations, actions, causes, or dispositions
  in admitted held-out state.
- Zero production incident writes, production-table access, or automatic live
  projection during evaluation.
- Zero membership changes authorized only by metadata, retrieval score,
  proximity, model agreement, or a critic finding.
- 100% of reviewer-disputed or source-contradicted cases retain an explicit
  alternative, counterevidence item, or unresolved question.

Failure of any source or safety gate rejects the approach.

## Relationship Evidence Gates

On reviewer-agreed development and held-out relationships:

- precision at least 0.95;
- recall at least 0.85;
- unsupported-relationship rate no greater than 0.02;
- zero high-impact false relationships that would connect clearly distinct
  real-world events;
- deterministic contract failure rate no greater than 0.01 across all trials.

Pairwise comparison is an evaluation aid, not a required production call per
candidate pair. Missing relationships found only by a critic remain dissent and
must be reproposed or adjudicated before they can support hypothesis growth.

## Incremental Event-State Gates

Against reconciled open-world event accounts:

- observation-membership precision at least 0.97;
- observation-membership recall at least 0.90;
- false-merge rate no greater than 0.01 and zero high-impact false merges;
- false-split rate no greater than 0.05;
- supported-claim precision at least 0.98;
- at least 0.95 of reviewer-marked uncertainty and plausible alternatives
  remain visible in projected shadow state;
- median pairwise Jaccard similarity of observation membership across the three
  repeated runs at least 0.95, with every material instability reported.

Counts and denominators must accompany every rate. If the held-out sample is
too small for a rate to be informative, the corresponding zero-severe-error
gate still applies and the result is inconclusive rather than a pass.

## Paxan Operational Gates

A production-shaped candidate must be measured on Paxan without targeting the
Ventax laptop:

- no second always-resident transcription or reasoning model is required;
- normal call ingest and current transcription continue without starvation;
- sustained shadow processing capacity is at least 1.5 times the observed
  95th-percentile 15-minute call-arrival rate;
- after a declared peak block, the shadow queue returns to its pre-peak depth
  within 10 minutes;
- 95th-percentile observation-to-shadow-update latency is at most 60 seconds
  and 99th-percentile latency is at most 120 seconds;
- no GPU out-of-memory event, model eviction loop, or required production model
  reload occurs;
- a shadow timeout or malformed response cannot block ingest or mutate current
  incident state.

The production-shaped design should use at most one semantic update generation
for an incoming observation or bounded micro-batch. Learned critique is sampled
or offline unless Paxan measurements independently prove it can run without
violating these gates.

## Decision Rule

Reject the candidate if any mandatory source, safety, held-out quality, or Paxan
operational gate fails. Passing development is permission to open the sealed
held-out evaluation once—not permission to tune on held-out output, deploy, or
write incidents. Passing held-out and Paxan shadow gates permits a separately
approved read-only live shadow period only.
