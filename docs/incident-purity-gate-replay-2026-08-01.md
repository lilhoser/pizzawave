# Incident Purity Gate Replay — August 1, 2026

Status: contract and replay tooling implemented; Qwen 3 4B purity gate rejected
for production use.

## Question tested

Before asking whether a candidate conversation segment belongs in an existing
incident, can Qwen 3 4B Instruct 2507 reliably determine both of these facts?

1. Every call already in the incident concerns one real-world event.
2. The candidate conversation segment concerns one real-world event.

The membership question is allowed only when both answers are `one_event`.
`multiple_events`, `unresolved`, a malformed response, or a model failure blocks
membership evaluation.

## Frozen labels

The five examples and labels came from the completed single-reviewer exact
membership trial. They were frozen before this replay.

| Candidate | Incident should be | Candidate should be | Gate should open |
| --- | --- | --- | --- |
| 1589590 | one event | multiple events | no |
| 1589728 | one event | one event | yes |
| 1589854 | multiple events | one event | no |
| 1590618 | multiple events | one event | no |
| 1591358 | one event | one event | yes |

The gate labels address evidence purity only. They do not assert that a clean
candidate belongs in a clean incident. Membership remains a separate decision.

## Frozen acceptance measures

Because this is a five-case smoke test, the measures are diagnostic rather than
accuracy estimates:

- detect both mixed incidents;
- preserve all three clean incidents;
- detect the one mixed candidate;
- preserve all four clean candidates;
- block all three cases containing mixed evidence;
- use `unresolved` when the transcript is too ambiguous to support a purity
  decision.

## Contract implementation

The implementation keeps source identities outside the model prompt. The model
sees every complete transcript in the bounded input and returns only one of
`one_event`, `multiple_events`, or `unresolved`. The application maps that
decision back to its own incident or call identity. Inputs with missing
evidence, duplicate identities, more than five incident calls, or more than one
candidate conversation segment fail instead of being truncated.

The implementation is not connected to incident creation or persistence.

## Qwen result

Qwen 3 4B Instruct 2507 produced ten first-pass decisions with no transport,
schema, identity, or coverage failures:

| Candidate | Incident result | Candidate result | Gate opened |
| --- | --- | --- | --- |
| 1589590 | one event | one event | yes |
| 1589728 | one event | one event | yes |
| 1589854 | multiple events | one event | no |
| 1590618 | one event | one event | yes |
| 1591358 | one event | one event | yes |

The first pass used 5,114 total tokens. Individual requests took 1.67 to 3.05
seconds once the shared endpoint was available.

Measured against the frozen review labels:

- mixed-incident detection: 1 of 2;
- clean-incident preservation: 3 of 3;
- mixed-candidate detection: 0 of 1;
- clean-candidate preservation: 4 of 4;
- mixed-evidence cases blocked: 1 of 3;
- overall gate result matched the frozen expectation: 3 of 5;
- `unresolved` decisions: 0.

The two missed mixed-evidence cases were repeated twice. All eight repeated
incident and candidate decisions were identical to the first pass. In
particular, Qwen repeatedly treated the candidate containing both Hancock Road
and Old Armstrong Circle as one event, and repeatedly treated incident 7908's
overdose and later fall or forced-entry calls as one event.

The replay used the exact saved transcripts, talkgroup names, call order, and
timestamps shown to the reviewer. The review artifact did not retain stop times
or system short names for four cases, so the replay supplied a zero duration and
a neutral system name for those prompt fields. Those missing metadata fields do
not explain failure to distinguish the explicitly different addresses,
patients, and service requests in the transcript text, but a larger evaluation
must preserve the complete collector input directly.

## Decision

No-go for this Qwen 3 4B purity gate. It is mechanically safe and stable, but it
fails the reason it exists: it opened membership evaluation for two of the
three cases with known mixed evidence. Its consistent answers make the errors
repeatable, not reliable.

Do not deploy this gate, enable source-linked candidate use, or scale membership
label production on the strength of this result. Prompt adjustment alone would
be another unmeasured special case unless evaluated on a broader frozen set.

## Recommended next step

Run the same strict purity contract with the already-running Qwen 3.6 35B model
as a bounded offline teacher on a fresh, automatically preserved set of 25
source-linked cases. Send only disagreements, `unresolved` answers, and detected
mixed evidence to the single reviewer. If the larger model cannot detect the
obvious mixed examples and use `unresolved` appropriately, stop pursuing an LLM
purity gate and move the ownership boundary upstream: mixed conversation
segments must be represented as evidence containing several event mentions
rather than forced into one incident-membership decision.
