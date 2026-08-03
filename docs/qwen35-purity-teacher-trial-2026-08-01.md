# Qwen 35B Purity Teacher Trial — August 1, 2026

Status: complete; no-go for enabling the purity gate.

## Purpose

Test whether the already-running Qwen 3.6 35B model can identify evidence that
contains several real-world events more reliably than Qwen 3 4B. This is a
bounded offline teacher test. Qwen does not become the routine production
membership model, and no result changes incident membership.

## Stage 1: labeled anchor cases

Run the unchanged source-safe purity contract on the five cases reviewed on
August 1. The frozen labels are:

| Candidate | Existing incident | Candidate segment |
| --- | --- | --- |
| 1589590 | one event | multiple events |
| 1589728 | one event | one event |
| 1589854 | multiple events | one event |
| 1590618 | multiple events | one event |
| 1591358 | one event | one event |

The model must detect all three known mixed-evidence conditions. A miss on an
obvious mixed case is a stop condition because that is the gate's only purpose.
Clean-evidence false alarms and `unresolved` answers are recorded separately;
they may reduce throughput but do not permit a false merge.

## Stage 2: twenty fresh exact packages

Stage 2 runs only if the labeled anchors justify it.

- Site: Omicrontheta
- Start: Unix 1785594023, 2026-08-01T14:20:23Z
- Latest call at start: 1597804
- Maximum end: Unix 1785637223, 2026-08-02T02:20:23Z
- Maximum fresh cases: the earliest 20 unique eligible candidate/incident pairs
- Production participant-link candidate use remains false

The committed exact-incident SQL contract supplies cases. A local snapshot
utility validates and atomically preserves complete collector objects as they
appear, because live incident membership can change later. It deduplicates by
application-owned candidate-call and incident identities. It neither asks a
model to reproduce those identities nor writes to PizzaWave.

Together with the five labeled anchors, these twenty packages form the bounded
twenty-five-package evaluation. The five anchors are not counted again as fresh
cases.

## Historical linkage evidence

The preserved narrow participant-link trial contains 39 inspected source-linked
call pairs: 26 appeared to continue the same conversation, 6 were clearly or
very likely different conversations, and 7 were unclear. This evidence remains
an important challenge reference for radio-link false-positive risk.

Those pairs are not counted as exact purity packages because they do not all
preserve the complete candidate-plus-owning-incident context required by the
current contract. The older archive was sealed before the radio ledger and
incomplete-boundary handling were deployed. Historical evidence is therefore
used where its provenance fits; it is not presented as current incident
structure.

## Frozen measurements

- anchor mixed-evidence detection and clean-evidence preservation;
- `one_event`, `multiple_events`, and `unresolved` counts by evidence scope;
- malformed output, model identity, coverage, timeout, and request failures;
- prompt, completion, and total token use;
- request duration and production queue behavior;
- repeated-result stability on up to five difficult cases;
- number of fresh cases blocked before membership evaluation;
- disagreement with the 4B purity result where both are available.

Fresh production incidents are context, not gold. If the anchors pass, human
review is limited to at most five fresh cases, prioritizing 35B/4B disagreement,
`multiple_events`, `unresolved`, and high-impact apparent false merges. The
review is not expanded merely to obtain a favorable score.

## Decision rule

- Stop if Qwen 35B misses any obvious labeled mixed-evidence anchor.
- If anchors pass, collect and replay up to 20 fresh exact cases within the
  fixed window, producing 25 total packages with the anchors.
- Do not enable the purity gate or source-linked candidate use from this trial.
- A later production proposal requires review evidence that false mixed-event
  acceptance is absent from the bounded sample and that the exception workload
  remains practical.

## Results

The five labeled anchors passed exactly. Qwen 35B detected all three known
mixed-evidence conditions and preserved all seven clean scope decisions. The
anchor gate opened for two packages and blocked three.

The fixed fresh window produced all twenty exact packages. They covered two
systems and thirteen talkgroups. Incident sizes were one call (1), two calls
(5), three calls (4), four calls (5), and five calls (5). Radio-link gaps ranged
from 7.264 to 59.670 seconds. Source radios appeared in 3 to 688 observed
segments; only two exact frequencies repeated (202 and 339 each occurred twice).

Fresh dispositions were:

| Scope | One event | Multiple events | Unresolved |
| --- | ---: | ---: | ---: |
| Existing incident | 18 | 2 | 0 |
| Candidate segment | 18 | 0 | 2 |

The fresh gate opened 16 times and blocked 4. Combined with the anchors, the
twenty-five-package gate opened 18 times and blocked 7. Combined scope counts
were 21/4/0 for incidents and 22/1/2 for candidates.

All forty fresh requests completed with the requested identity
`qwen/qwen3.6-35b-a3b@q8_0`. There were no malformed, identity, coverage,
timeout, or request failures. Prompt tokens were 294 minimum, 379 median, 865
95th percentile, 986 maximum, and 19,762 total. Completion tokens were 12
minimum, 13 median, 18 95th percentile, 20 maximum, and 568 total. Total tokens
were 306 minimum, 397 median, 878 95th percentile, 999 maximum, and 20,330
total. Request duration was 901 ms minimum, 2,744 ms median, 4,191 ms 95th
percentile, 12,936 ms maximum, and 119,617 ms total.

Five difficult packages were replayed once more. Every disposition repeated,
including a decisive contradiction: candidates 1599386 and 1599412 carried the
same five-call incident 7969 with byte-equivalent established-call evidence,
yet the incident was classified `multiple_events` for the first request and
`one_event` for the second. Replaying those packages in the same order
reproduced both different answers. Incident 7970 again classified as multiple,
and candidates 1599708 and 1604544 again classified as unresolved. This shows
sequence- or request-sensitive behavior at the gate's ownership boundary even
though individual package replays appeared stable.

The collector initially reported zero cases because PowerShell/SSH quoting left
the remote SQLite parameters unset. The data was present. The corrected method
stripped the SQL byte-order mark, replaced `:start_unix` and `:end_unix` in
memory with the two frozen integer literals, and piped the resulting SQL to
`sqlite3 -readonly`. Eleven packages were recovered immediately and collection
eventually reached twenty. Future automation must not use remote `sqlite3 -cmd`
parameter arguments through this PowerShell path.

Production source-linked candidate use remained false. `pizzad` and
`trunk-recorder` stayed active, Trunk Recorder retained PID 126817, and the
health endpoint continued returning HTTP 200 during inference. The final health
check reported queue depth zero, no queue pressure, and healthy recent AI
completions. Incident analysis still had 337 pending calls, including 21 older
than its age target, but its health status remained `ok`; no queue-full condition
was observed. One ordinary embedding failure retried after backoff and was not
a purity-replay failure.

## Initial conclusion

This is a no-go for enabling the purity gate or source-linked membership. The
anchor accuracy is promising, but a gate cannot safely use two different purity
answers for identical incident evidence. Human review cannot repair that
mechanical consistency defect, so no review interface was created.

The next experiment should isolate the adapter boundary: record a canonical
hash of the exact request body, enforce deterministic decoding parameters, and
replay the incident 7969 request at least twenty times as the identical first
request in a fresh session. Continue semantic evaluation only after identical
request bodies produce one invariant disposition.

## Determinism follow-up — August 2, 2026

The adapter now serializes the exact outbound body once, hashes those exact
UTF-8 bytes, sends those same bytes, and records the SHA-256 with the result. It
keeps temperature at zero and now explicitly sends seed zero. A dedicated
diagnostic creates a new HTTP client for every iteration, captures every raw
response, saves atomically, and checks request-body and disposition invariance.

The frozen incident 7969 evidence from candidate package 1599386 was submitted
twenty times. All twenty transmitted bodies had SHA-256
`A58B0A9FC5AB8EB70BB5D1A6EE60460CEB27C01F58FB30C32D04F82734229ED6`.
All twenty responses used model identity `qwen/qwen3.6-35b-a3b@q8_0` and returned
`one_event`. Request duration was 780 ms minimum, 809 ms median, 1,026 ms 95th
percentile, and 4,141 ms maximum. Every request used 999 total tokens.

The same incident evidence from candidate package 1599412 produced the same
request hash and returned `one_event` twice. The twenty raw response envelopes
had different hashes because server-generated envelope fields varied, but the
parsed choice content was exactly the same JSON decision every time.

That first repeated sequence appeared consistent, but the wider rerun below
showed that it was not sufficient to establish consistency across separate
runs.

## Corrected twenty-five-example rerun — August 2, 2026

The five human-labeled examples and twenty saved field examples were run once
through the hashed request path with temperature zero and seed zero. All five
human-labeled examples were correct: all three known mixtures were detected and
all seven known single-event inputs were preserved.

Across all twenty-five examples, existing incidents were classified as 22
single-event and 3 multiple-event. Candidate radio segments were classified as
21 single-event, 1 multiple-event, and 3 uncertain. Eighteen examples were
allowed to continue to the later membership decision and seven were stopped.
All 50 requests used the requested Qwen 35B identity, recorded a non-empty
request hash, and completed without malformed output, timeout, identity, or
coverage failures. Request duration was 1,321 ms minimum, 2,836 ms median,
4,579 ms 95th percentile, and 17,634 ms maximum. Total token use was 25,776.

The model correctly stopped incident 7970, whose calls combined a breathing
emergency with an unrelated stuck truck and camper. It treated candidates
1599708, 1603248, and 1604544 as uncertain. The latter two appear related to
their incidents from the transcripts, so these are conservative missed
opportunities rather than unsafe additions.

One result changed materially from the earlier run: incident 7969 became
single-event, making its two packages agree. Candidate 1603248 moved from
single-event to uncertain. The exact candidate-1603248 request was then sent
ten more times and returned single-event all ten times. A second independent
ten-request run also returned single-event all ten times. The request SHA-256
was `282D94FF712E1BA3261DDF09739C425E31E6E8598272899D1342C47AA7088F7C`
in the original uncertain request and all twenty later single-event requests.

This is the critical finding: explicit seed zero and byte-identical request
bodies do not make Qwen 35B return the same classification across separate
runs in this serving configuration. Repetition inside one run can look stable
while disagreeing with another run. Human review cannot repair that runtime
property.

## Final decision

Do not use Qwen 35B as the production component that independently permits or
blocks incident membership. Keep production source-linked candidate use false.
The request hashes and replay tooling remain valuable diagnostics, and Qwen 35B
can still suggest offline labels or flag suspicious examples for review.

The recommended next engineering direction is to build a human-labeled saved
evaluation set and test a smaller locally controlled classifier against it.
Qwen suggestions may help select difficult examples, but they must not be
treated as the labels themselves. Require the smaller classifier to reproduce
the same answer across separate processes before considering any production
shadow run.
