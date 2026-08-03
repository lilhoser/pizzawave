# Callstream version 2 two-hour linkage trial

The fixed window ran from `2026-07-30T17:38:27Z` through
`2026-07-30T19:38:27Z` on RPI. The trial was read-only with respect to
production incident data.

## Transport results

- PizzaWave received 483 calls. All 483 used Callstream schema version 2.
- All 483 calls had exact audio mapping. The transmission ranges covered every
  saved WAV exactly, with no gaps, overlaps, or sample-count failures.
- Callstream excluded 34 source-less decoder-floor fragments containing 20,960
  samples. It also omitted two complete records that contained no identified or
  acoustically non-empty transmissions.
- Sixteen source-less transmissions with measurable audio were retained.
- An identified source was available in 478 of 483 stored calls, or 98.97
  percent. The window contained 206 unique source identifiers; 126 appeared in
  more than one call.

## Linkage results

The analysis reproduced the production service's consecutive-appearance rule.
It did not connect every pair merely because the same source appeared somewhere
within the window.

| Maximum gap | Source-linked pairs | Same-system temporal pairs | Reduction |
| --- | ---: | ---: | ---: |
| 30 seconds | 152 | 1,424 | 89.33% |
| 1 minute | 200 | 2,459 | 91.87% |
| 2 minutes | 241 | 4,396 | 94.52% |
| 5 minutes | 322 | 10,219 | 96.85% |
| 10 minutes | 369 | 19,697 | 98.13% |
| 15 minutes | 401 | 28,665 | 98.60% |
| 30 minutes | 441 | 51,674 | 99.15% |
| 60 minutes | 470 | 85,446 | 99.45% |

At 60 minutes, 437 linked pairs used the same talkgroup and 33 crossed
talkgroups. Several high-frequency identifiers crossed operationally distinct
police, fire, and medical talkgroups. The most frequent identifier appeared in
34 calls. The median identifier appeared in two calls, while the 95th
percentile appeared in approximately 12 calls.

Forty-three source-identified transmissions contained only decoder-floor audio.
Twenty-four of the 470 hour-window links relied entirely on transmissions
without acoustic content. Removing those links in analysis left 446
acoustically supported pairs.

The existing production incident assignments did not provide a useful gold
comparison because 462 of the 470 hour-window pairs had at least one call that
was not assigned to a completed incident during the trial.

## Evidence that limits the hypothesis

Source identifier `1002082` appeared in 18 calls across four talkgroups. Within
one short sequence it carried a Byram Fire medical dispatch, appeared in brief
Fireground transmissions, and then appeared in the final 0.52 seconds of a
Byram Police Dispatch call. The samples contained real audio, so a simple
silence filter would not eliminate this linkage. This is consistent with a
shared dispatch console or other common radio use and demonstrates that a
decoded identifier is participant evidence, not incident identity.

Another identifier appeared in alternating Byram Police and Pafford EMS calls
only seconds apart. The associated transcripts were poor, so this case cannot
establish membership, but it reinforces the need to preserve talkgroup,
frequency-of-use, and time-gap context around each identifier.

## Conclusion

Source identifiers are useful retrieval evidence but are not incident
membership truth. They are available often enough to prioritize a much smaller
candidate set, especially at short gaps. They cannot replace semantic
membership because busy dispatch consoles legitimately appear in unrelated
calls and can cross talkgroups.

The current 60-minute maximum remains defensible only as a broad search
boundary. It should not behave as an automatic grouping rule. Source-linked
calls should be prioritized within the existing candidate window, while a
bounded route remains for calls without source evidence. Otherwise, the large
candidate reduction would become a recall failure.

Before enabling live candidate selection, acoustically empty transmissions must
stop contributing participant links even when a source identifier was decoded.
The ledger may retain those rows for diagnostics, but the linkage service needs
an explicit acoustic-content fact.

## Five-call review

The review package follows five consecutive appearances of radio `1002082`,
beginning with PizzaWave call `164806` and ending with call `164841`. It starts
with a Byram Fire medical dispatch, continues through Fireground calls, and ends
on Byram Police Dispatch. This tests the boundary where useful short-range
linkage can become a false incident join.
