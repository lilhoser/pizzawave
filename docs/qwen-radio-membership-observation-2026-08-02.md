# Qwen radio-linked membership observation - August 2, 2026

## Purpose

This test observed how PizzaWave's production Qwen model handled a new use:
deciding whether a call found through transmitting-radio linkage belonged with
a specific existing incident. Results were logged only. They never changed
candidate selection, incident membership, or persistence.

## Evidence required for one decision

The proposed call had to be the later of two adjacent appearances of the same
positive radio identifier. Both calls had to use the same system and talkgroup,
with a measured gap from zero through 60 seconds. The earlier call had to belong
to an active, unmerged incident, while the proposed call had to be absent from
that incident. The complete incident was limited to one through five calls, and
all supplied calls required complete, usable transcripts.

Qwen first checked the existing incident and proposed call separately. It made
the membership decision only when both described one coherent event.

## Scheduler correction

The first deployment incorrectly refused to run whenever any old row remained
in the incident-analysis job table. That is not how PizzaWave defines a healthy
incident-analysis process: current calls can be completing normally while old
rows await periodic cleanup. The incorrect check prevented every Qwen request.

Commit `5ac8200` removed that check. The corrected OT settings used PizzaWave's
own healthy status, a maximum of 1,000 outstanding incident-analysis rows, and
a maximum latest-completed age of 60 minutes. Requests remained limited to one
at a time and no more than one every five minutes.

## Corrected OT run

- Run ID: `ot-qwen-radio-membership-observation-20260802-corrected`
- Start: Unix 1785710041 (`2026-08-02T22:34:01Z`)
- Baseline PizzaWave call ID: 1619978
- Stopped early: Unix 1785716682 (`2026-08-03T00:24:42Z`)
- Production radio-linked candidate use: false throughout
- Results persisted: none
- Qwen or request failures: none
- PizzaWave, Qwen, and Trunk Recorder: healthy after shutdown
- Trunk Recorder PID: 126817 before and after

During the observed period, 664 calls arrived and 596 had usable transcripts.
Only two exact candidate-and-incident cases reached Qwen.

### Case 1

- System: `whiteoakmt-cleveland`
- Incident: 8092, calls 1619836 and 1619964
- Proposed call: 1619850
- Radio gap: 23,558 ms
- Radio appearances in the supplied window: 10
- Incident check: one coherent event
- Proposed-call check: unresolved
- Membership decision: not attempted
- Incident request: 2,426 ms, 502 prompt tokens, 13 completion tokens
- Proposed-call request: 2,138 ms, 311 prompt tokens, 18 completion tokens

The proposed-call transcript was too garbled to support a reliable decision.
Stopping before membership was appropriate.

### Case 2

- System: `whiteoakmt-hamilton`
- Incident: 8095, call 1620380
- Proposed call: 1620382
- Radio gap: 31,948 ms
- Radio appearances in the supplied window: 18
- Incident check: one coherent event
- Proposed-call check: one coherent event
- Membership decision: do not add
- Incident request: 2,458 ms, 459 prompt tokens, 13 completion tokens
- Proposed-call request: 2,473 ms, 332 prompt tokens, 13 completion tokens
- Membership request: 3,962 ms, 683 prompt tokens, 14 completion tokens

The incident concerned a domestic disorder. The proposed call was a separate,
fragmentary exchange and included a possibly incomplete opening transmission.
Rejecting it was reasonable.

All five requests used model identity `qwen/qwen3.6-35b-a3b@q8_0`, returned
valid structured output, and recorded distinct request hashes.

## Decision

The code path works and Qwen behaved conservatively, but this narrowly defined
use did not demonstrate practical value. It produced two cases from hundreds
of calls, accepted none, and improved no incident. Continuing merely to enlarge
the sample would spend model and engineering time on a rare intersection of
conditions.

Do not enable this radio-linked membership path in production. The more useful
next experiment is a side-by-side replay of complete incident construction with
and without radio identity. That tests whether radio evidence improves how
incidents are formed and continued, instead of limiting it to attaching one
missed call to an already-existing small incident.
