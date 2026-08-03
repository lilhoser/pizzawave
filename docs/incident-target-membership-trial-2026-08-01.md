# OT Exact-Incident Membership Trial — August 1, 2026

Status: machine trial and five-item human review complete. Production use
remains disabled.

## Fixed window

- Site: Omicrontheta
- Receiver time: Unix 1785543618 through 1785550818 inclusive
- UTC: 2026-08-01T00:20:18Z through 2026-08-01T02:20:18Z
- Baseline PizzaWave call ID: 1588994
- Production participant-link candidate use: false throughout
- Trunk Recorder PID: 126817 before and after
- PizzaWave and Trunk Recorder: healthy after the trial

No incidents, configuration, or services were changed. Trunk Recorder was not
restarted.

## Collection funnel

The read-only collector considered 2,165 adjacent appearances of identified
radios whose later call began in the fixed window. Mutually exclusive rejection
counts were:

- 1,431 outside the zero-to-sixty-second gap;
- 476 whose earlier linked call did not belong to an unmerged incident;
- 200 on a different talkgroup;
- 49 whose candidate transcript was not usable;
- 2 whose target incident was oversized or had unusable evidence;
- 1 whose candidate was already in the target incident.

Six raw eligible radio links reduced to five unique candidate/incident pairs.
The collector also excluded 311 possibly incomplete identified-source
transmissions before constructing adjacency. There were 163 source-less
transmissions in the window; these cannot produce a radio link.

The five unique gaps were 21, 27, 32, 55, and 55 seconds. Target incident sizes
were 2, 3, 4, 5, and 5 calls. Three cases came from Hamilton, one from North
Bradley, and one from Cleveland. The linked
radios had appeared in 14, 27, 254, 323, and 337 stored conversation segments,
so the set includes both uncommon and dispatcher-like frequent radios.

## Qwen result

Qwen 3 4B Instruct 2507 ran through the committed exact-incident adapter. It
made one request per pair and returned:

- 2 `include`;
- 3 `do_not_include`;
- 0 `unresolved`;
- 0 model, schema, identity, or coverage failures.

The five first-pass requests used 5,042 prompt tokens and 58 completion tokens.
Durations ranged from 2.20 to 2.50 seconds, with a median of 2.41 seconds. The
two difficult cases were each repeated twice. Their decisions were identical in
all three runs.

The shared LM Studio endpoint delayed an earlier batch wrapper while the main
Qwen model was generating. Once the endpoint was idle, all five requests
completed in about seventeen seconds including local process overhead. This is
a capacity scheduling concern, not a malformed-response problem.

## Critical evidence

The narrow question is more stable than the rejected whole-window grouping
task, but the five cases expose a more fundamental ownership problem:

- candidate call 1589590 itself contains dispatch traffic for both Hancock Road
  and Old Armstrong Circle. `include` preserves relevant Old Armstrong evidence
  but also imports unrelated Hancock traffic; `do_not_include` loses relevant
  evidence;
- target incident 7905 already combines dirt-bike-crash calls with a later drug
  overdose call. Candidate 1589854 appears related to the overdose portion, not
  to the incident as a coherent whole;
- target incident 7908 combines an overdose hospital report with a later fall
  and possible forced-entry event before the new candidate is considered.

Therefore a source-safe binary output contract is not sufficient when either
the canonical conversation segment or the target incident already contains
several real-world events. Radio linkage did not cause those existing mixtures,
but it exposes them.

## Decision

No-go for enabling source-linked candidate retrieval in production yet. Qwen's
five decisions are plausible and mechanically stable, but at least three of the
five examples do not present a clean binary membership question. Accuracy
against a forced binary label would conceal the actual error.

The prepared human review records three independent facts for each item:

1. include, do not include, or unsure for the candidate;
2. whether the existing incident already contains more than one event;
3. whether the candidate call itself contains more than one event.

## Recommended next step

Implement and shadow-test a purity gate before any further membership scaling.
Mixed or unclear conversation segments and mixed target incidents must remain
unresolved instead of receiving automatic binary membership. Do not enlarge the
membership evaluation until that prerequisite is tested.

## Human review result

The single reviewer completed all five items. Exact Qwen/reviewer agreement was
three of five:

| Candidate | Qwen | Reviewer | Existing incident mixed | Candidate mixed |
| --- | --- | --- | --- | --- |
| 1589590 | include | include | no | yes |
| 1589728 | do not include | unsure | no | no |
| 1589854 | include | unsure | yes | no |
| 1590618 | do not include | do not include | yes | no |
| 1591358 | do not include | do not include | no | no |

The reviewer never directly contradicted Qwen with the opposite binary choice.
However, Qwen made a definite decision in both cases the reviewer marked
`unsure`. Qwen returned no `unresolved` decisions at all. This is evidence that
the current adapter is too willing to force a binary answer even though the
schema permits unresolved evidence.

The structural concern was confirmed in three of five cases:

- one candidate conversation segment contained more than one event;
- two target incidents already contained more than one event.

Only two of five examples were clean binary questions. Qwen agreed with the
reviewer on one of those two; the reviewer marked the other unsure. A five-case
sample is too small for an accuracy claim, but it is sufficient to reject the
assumption that source-linked candidate membership is normally a clean binary
decision at the current data boundary.

The exact saved review result is preserved at
`docs/evidence/incident-target-membership-review-2026-08-01.json`.
