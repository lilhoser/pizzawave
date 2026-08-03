# Narrow participant-link shadow trial

## Fixed scope

- OT receiver window: 2026-07-31 12:07:32Z through 14:07:32Z
  (Unix 1785499652 through 1785506852, inclusive).
- Baseline PizzaWave call identifier: 1574216.
- Calls received during the window: 942.
- Production participant-link candidate use stayed disabled.
- PizzaWave and Trunk Recorder remained healthy. Trunk Recorder retained process
  126817, which started before this trial.

The deployed shadow policy evaluated each raw participant link independently.
It retained a link only when both calls used the same talkgroup and the gap was
no more than 60 seconds. Possibly incomplete transmission boundaries had
already been excluded by the participant-link service. Radio frequency was
preserved as evidence but was not used as an automatic exclusion.

## Link reduction

The fixed-window reconstruction found 1,124 raw participant links involving a
trial call:

| Raw link group | Links |
| --- | ---: |
| Same talkgroup, no more than 60 seconds | 238 |
| Same talkgroup, 61 through 120 seconds | 108 |
| Same talkgroup, more than 120 seconds | 696 |
| Different talkgroups | 82 |

The narrow rule admitted 238 links and excluded 886, a 78.8 percent reduction.
Across the 24 actual candidate-preparation cycles, PizzaWave observed 3,887
rolling link instances and admitted 900, a 76.8 percent reduction. Every cycle
logged production candidate use as false.

Incomplete-boundary filtering removed another 187 reconstructed links. Forty-five
of those would otherwise have met the same-talkgroup and 60-second conditions.

## Candidate effect

Twenty-three candidate cycles produced a measurable shadow difference.

| Measure | Result |
| --- | ---: |
| Baseline candidate observations | 316 |
| Candidate observations with narrow radio evidence | 367 |
| Added candidate observations | 51 |
| Unique added calls | 50 |
| Existing candidate observations augmented with radio evidence | 61 |
| Unique augmented calls | 51 |
| Baseline candidates displaced | 0 |

The narrow rule increased the candidate observations presented for possible
semantic adjudication by 16.1 percent. It did not remove or displace any
baseline candidate.

The preceding broad-window trial added 154 observations to 283 baseline
observations, a 54.4 percent increase. Absolute additions fell by 66.9 percent
in this trial despite this window containing more calls. Normalized additions
fell from 20.3 to 5.4 per 100 received calls. The windows had different radio
traffic, so this is a directional comparison rather than a controlled
same-input replay.

## Semantic inspection

The 51 added observations were explained by 39 actual call pairs. All 39 pair
transcripts were inspected conservatively:

- 26 pairs appear to continue the same conversation;
- 6 pairs are clearly or very likely different conversations; and
- 7 pairs remain unclear because the transcript does not supply enough reliable
  content.

This is engineering inspection, not human-reviewed gold truth. Ambiguous audio
was retained locally rather than turning unclear cases into positive labels.

The useful pairs include medical handoffs, license-return follow-ups, public
works coordination, and a multi-call training exercise. The false pairs include
back-to-back vehicle checks, a warrant return followed by an unrelated call,
and unrelated dispatch traffic carried by the same console radio.

Frequently observed radios remain important but cannot be treated as an
automatic rejection. They participated in 31 of the 39 inspected pairs and 27
of the 51 added observations. Some of those pairs were strong continuations and
some were false. Frequency is therefore context for semantic adjudication, not
a membership rule.

Twenty of the 39 pairs included at least one call that could not start an
incident because it began from an update rather than a grant. Thirteen pairs
had an update-started later call. This is an important benefit: radio evidence
can retrieve a continuation that is intentionally unable to create an incident
on its own.

Only one pair was already in the same persisted incident; 38 were unassigned.
No pair was placed in different persisted incidents. This is not accuracy
evidence because existing incident membership is not gold truth. Shadow code
did not persist membership and the production flag remained false throughout.

## Decision

Do not enable participant-linked candidate retrieval in production yet.

The narrow filter is substantially better than the one-hour policy, and the
majority of inspected pairs are useful. It still admits clear false links, so a
radio match, same talkgroup, and short gap are not sufficient membership proof.

The next test should run the existing semantic incident adjudication on the
baseline and narrow candidate sets in parallel without persisting either
shadow result. It should measure which additional calls Qwen accepts, rejects,
or leaves unresolved and whether any baseline decision changes merely because
the additional context is present. That directly tests the intended use of
radio identifiers as retrieval evidence. Another candidate-only window or
another reviewer interface would not answer that question.

## Qwen semantic-shadow contract check

Implementation of that proposed test stopped before deployment because the
available Qwen interfaces do not satisfy the frozen membership-output contract.

The current production incident extractor asks Qwen to return call identifiers.
The newer rolling-hypothesis prompt avoids identifiers by asking the model to
copy each complete evidence record, including its transcript, into the output.
Both mechanisms are explicitly excluded by the settled architecture.

The pilot contract instead requires a constrained decoder to bind each
`member`, `not-member`, `unresolved`, or `non-incident` choice directly to an
application-owned source object while generation is occurring. LM Studio's
OpenAI-compatible endpoint returns text or JSON after generation. Its structured
output support does not return application-bound decision cells, so PizzaWave
would still have to recover identity from a generated field, sequence position,
or copied transcript.

No semantic shadow was deployed, no OT configuration changed, and no Qwen trial
was started. A free-form JSON substitute would make the experiment incapable of
validating the intended production design. The next decision is whether to
implement a lower-level constrained inference adapter, initially backed by
Qwen, or to reserve that adapter work for the small membership model and stop
the Qwen membership experiment.
