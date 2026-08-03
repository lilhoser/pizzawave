# Two-site incident quality audit - 2026-08-03

## Decision

Keep the current Qwen-based incident pipeline in production. Its model calls were completely reliable during this audit, its conflict checks prevented the clearest attempted false merges, and most newly created incidents ended with coherent multi-call membership.

The incident composition is improved but not finished. The remaining problem is now specific: conservative validation still misses some explicit single-event dispatches, and one cross-agency event remained split into two incidents. This supports a targeted correction, not another broad redesign or a rollback.

## Fixed audit window

- Window: 2026-08-03 01:36:41Z through 13:36:41Z (12 hours)
- OT and RPI were measured separately because they carry different traffic and have different RF and transcript-quality conditions.
- Counts below come from the exact Unix window. Create, update, and reject totals were recomputed from the immutable audit rows because the current quality-check summary limits its reason breakdown to 30 groups and therefore undercounted this window.
- Two RPI examples were older calls still awaiting or lacking a final incident when the audit began. They are labeled as carry-in checks and are excluded from the window's call and rate totals.

## Results

| Measure | OT | RPI |
|---|---:|---:|
| Calls | 3,177 | 1,474 |
| Complete transcripts | 2,818 (88.7%) | 1,019 (69.1%) |
| Incidents created | 43 (13.5 per 1,000 calls) | 11 (7.5 per 1,000 calls) |
| Accepted updates | 85 (26.8 per 1,000 calls) | 56 (38.0 per 1,000 calls) |
| Rejected candidates | 291 (91.6 per 1,000 calls) | 79 (53.6 per 1,000 calls) |
| AI requests | 543/543 succeeded | 203/203 succeeded |
| AI failures / truncated answers | 0 / 0 | 0 / 0 |
| Membership checks that changed the proposed call list | 271/389 (69.7%) | 76/135 (56.3%) |
| Calls added / removed by those checks | 99 / 188 | 49 / 84 |

The high call-list change rates are evidence that the final server-side check is doing substantial work. They are not accuracy scores: a change can correct a model proposal or remove a valid call.

### Membership size at the end of the window

The following sizes cover incidents created during the fixed window, after any updates that occurred before the audit ended.

| Size | OT | RPI |
|---|---:|---:|
| No calls | 0 | 0 |
| One call | 8 (18.6%) | 1 (9.1%) |
| Two calls | 22 | 4 |
| Three to five calls | 11 | 6 |
| Six to ten calls | 2 | 0 |
| More than ten calls | 0 | 0 |
| Average / maximum | 2.40 / 8 | 2.91 / 5 |

This is a healthier shape than a feed dominated by isolated calls, but it does not prove that every member belongs. The transcript checks below provide the semantic evidence.

## OT quality findings

### What worked

- The attempted combination of the Orange Plank Drive fall response with the separate Building Road chest-pain response was rejected because the locations conflicted. Incident 8130 ended with only the three Building Road calls.
- Repeated proposals that reused the 4835 Highway 58 fire calls for a Bonnie Oaks assault/arson report were rejected. No duplicate incident was persisted from those proposals.
- The Ford Street stolen-vehicle incident ended with only the supported Ford Street call; the suspicious unrelated call seen during collection was not retained.
- Larger incidents such as the Rosemary Drive crash/armed-suspect response and the stolen Nissan Rogue bulletin were coherent in the inspected transcripts.

### What still failed

- The Prime Imaging event was missed. Police call 1626770 requests EMS at 5441 Highway 153 for an irrational man, and EMS call 1626854 repeats the address as an unknown medical. Neither belongs to an incident. The validation rejected the paired proposal for lack of corroboration and later rejected the EMS call as too weak by itself.
- Incidents 8133 and 8136 appear to split one Rosemary Drive crash and suspect-perimeter event across Cleveland and Hamilton talkgroups. Calls in both incidents refer to the crash, Hancock, the perimeter, and the same possibly armed suspects.
- Incident 8132 is compositionally coherent as a fire-alarm/water-flow response, but its title contains the garbled phrase `2 gun smoke`. This is a narrative-quality defect rather than a membership error.

No persisted false merge was confirmed in the high-risk OT sample. The strongest false-merge candidates were blocked, which is materially better than the documented June examples where conflicting events remained together.

## RPI quality findings

### What worked

- The US-471 vehicle fire, Reagan Street medical response, and other inspected multi-call incidents were coherent.
- The 179 Tazan Avenue fall was initially rejected but was later created with its dispatch and clearing transmission. This shows that repeated processing can recover some early misses.
- The Highway 25/Sand Hill crash incident contains four related fire-dispatch calls. The suspected unrelated Byram registration traffic was not present in the final membership.

### What still failed

- Carry-in check: call 194777 gives 414 Old Wesson Road and describes a juvenile struck in the head by another juvenile. Although the call predates the fixed window, it was repeatedly processed and rejected during the window as hospital or transport traffic because it aired on an EMS operations talkgroup. It remains outside an incident.
- Carry-in check: call 194889 dispatches a response to a domestic call at Tony Lane/175th. It predates the fixed window and remains outside an incident.
- Call 195075 dispatches police to a disturbance at 460 Spring Ridge Road and describes the involved person. It was repeatedly rejected as lacking a strong event signal and remains outside an incident.
- Incident 907 correctly groups a Highway 32 East structure-fire response, but the title is only `Fire`.

RPI's lower creation rate cannot be read as a model-quality ranking against OT. Only 69.1% of RPI calls had complete transcripts, versus 88.7% on OT, and the missed-event examples show that talkgroup context and short or distorted transcripts interact with the conservative validation rules.

## Comparison with earlier implementations

- Earlier June windows generally created about 8 to 12 incidents per 1,000 calls. OT is above that range at 13.5; RPI is below it at 7.5. Traffic mix and transcript coverage make the direction more useful than a direct site ranking.
- Earlier windows repeatedly had truncated model answers and occasional endpoint failures. This audit had 746 successful requests out of 746 and no truncation, a clear operational improvement.
- June evidence included persisted incidents that combined distinct locations and events. In this audit, the clearest conflicting-location proposals were rejected and the inspected final memberships were clean. That is a substantive improvement.
- The remaining errors have shifted toward missed explicit dispatches, cross-agency fragmentation, and weak titles. The system is no longer failing primarily because the model endpoint is unreliable.

## Operational checks and limitations

- Both sites were healthy at the final check. Trunk Recorder remained active on the expected unchanged PIDs: OT 126817 and RPI 3826478. AI completions and embedding services were healthy.
- RPI's `pizzad` briefly restarted around 12:05Z during a requested presentation-only deployment and recovered. Trunk Recorder did not restart, and no incident configuration or model changed. A separate requested UI deployment restarted `pizzad` on both sites after 13:36:41Z, outside the measured window.
- This audit inspected the highest-risk chains and concrete transcript evidence; it did not produce human labels for every call. Automated counts describe behavior and workload, not semantic accuracy.
- Membership sizes are the end-of-window state and can reflect revisions made after creation. The audit rows are immutable, but current incident membership is not.
- The quality-check endpoint's 30-group reason cap caused its create/update/reject summary to undercount the exact window (OT reported 41/81/274 instead of 43/85/291; RPI reported 10/56/77 instead of 11/56/79). The report uses direct read-only aggregation of the same immutable audit table.

## One next step

Turn the four confirmed missed dispatches (Prime Imaging, Old Wesson Road, Tony Lane, and Spring Ridge Road), the Rosemary/Hancock split, and the two successfully blocked conflicting-location proposals into automated replay tests; then adjust the server-side event validation and cross-agency linking rules until the misses and split pass without weakening the existing conflict protection.
