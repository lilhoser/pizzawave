# Qwen 35B larger incident-link evaluation — August 2, 2026

## Question

Is Qwen 35B accurate enough to help decide whether a radio-linked call belongs
with an existing incident, even though repeated identical requests can
occasionally produce different answers?

This evaluation treats occasional variation as a measurable error source, not
an automatic disqualification. The important question is whether the model
makes unsafe decisions often enough to outweigh its usefulness.

## Safety and collection

Production continued using its existing candidate selection. Radio-linked
candidates were not used by production, incident records were not changed, and
no service was restarted.

The established read-only SQL collector found 101 complete real examples from
Unix 1785499652 through 1785695143. Each example preserved the proposed call,
the complete existing incident at collection time, and the adjacent appearance
of the same radio on the same system and talkgroup within 60 seconds.

The set covered 76 distinct incidents, 3 radio systems, 26 talkgroups,
incident sizes from 1 through 5 calls, and radio gaps from 4.025 through 59.670
seconds. Current incident membership was retained as context, not assumed to be
the correct answer.

The snapshot and replay utilities were raised from a 25-example diagnostic
limit to a 200-example limit. Model requests remained sequential to avoid
competing with production work.

## Model results

Qwen evaluated both the existing incident and the proposed call segment for
every example, for 202 total requests:

- Existing incidents: 76 described as one event and 25 described as containing
  more than one event.
- Proposed call segments: 82 described as one event, 16 uncertain, and 3
  described as containing more than one event.
- 63 examples were allowed to proceed to the later membership decision; 38
  were stopped for mixed or uncertain evidence.

All 202 requests completed with the requested
`qwen/qwen3.6-35b-a3b@q8_0` model identity. There were no request, timeout,
format, identity, or evidence-coverage failures. Every request recorded the
exact outbound request hash.

Request duration was 789 ms minimum, 2,395 ms median, 4,130 ms at the 95th
percentile, and 13,973 ms maximum. The 202 requests took 542,130 ms in total.
Token use was 306 minimum, 366 median, 844 at the 95th percentile, and 999
maximum, for 95,416 tokens total.

Within this run, no repeated exact incident request produced conflicting
answers. Earlier testing established that a byte-identical request can still
change classification across separate runs, so this result does not claim
perfect repeatability.

## Human check

Automated ranking examined the 63 examples the model allowed and emphasized
large incidents, long time spans, multiple talkgroups, frequent radios, and
long transcripts. Engineering inspection reduced the remaining human work to
three examples where an unsafe missed mixture is plausible:

1. A long traffic call that may also mention a separate bridge or
   transportation matter.
2. A five-call injury incident followed by a hospital report that may or may
   not concern the same patient.
3. Four calls concerning the same named person over about 51 minutes, included
   as a difficult long-span control.

The reviewer judged the long traffic segment and the Dale Thompson calls to be
one event, agreeing with Qwen. The reviewer judged the church injury and later
hospital report to be unrelated events, disagreeing with Qwen. This was one
unsafe miss among the three deliberately highest-risk allowed examples. It is
not an estimate of the model's overall error rate.

The instructions were revised to make Qwen explicitly compare every call's
specific location, person, vehicle, event type, cause, and chronology. They now
state that a shared channel, agency, broad event category, or similar injury is
not enough to establish one event, and they direct Qwen to stop when a shared
event is merely possible rather than supported.

The first attempted comparison after that source change accidentally reused an
older compiled dependency. Its unchanged request hashes exposed the mistake,
and those results were discarded. The corrected replay rebuilt the utility and
produced new request hashes.

With the revised instructions, Qwen classified the reviewed church and
hospital evidence as unrelated in both occurrences in the 101-example set and
in 20 of 20 additional identical requests. It preserved the reviewed Dale
Thompson incident in 20 of 20 requests and preserved the reviewed long traffic
segment in 20 of 20 requests. The five previously human-labeled examples also
retained all ten expected decisions.

Across the corrected 101-example replay, existing incidents were classified as
71 one-event and 30 multiple-event. Proposed call segments were classified as
76 one-event, 21 uncertain, and 4 multiple-event. Fifty-four examples could
continue to the later membership decision and 47 were stopped. Compared with
the original instructions, nine additional examples were stopped and none
were newly allowed. Inspection found that most additional stops involved
disconnected, vague, or badly transcribed evidence. Two appear to be plausible
same-event calls that may be lost opportunities rather than safety problems.

All 202 corrected requests completed with the requested model identity and no
request, timeout, format, identity, or evidence-coverage failure. Duration was
827 ms minimum, 2,534 ms median, 4,130 ms at the 95th percentile, and 14,390 ms
maximum, totaling 554,839 ms. Token use was 316 minimum, 436 median, 948 at the
95th percentile, and 1,095 maximum, totaling 106,091 tokens.

## Production health

Before and after inference, `pizzad` and `trunk-recorder` were active and Trunk
Recorder retained PID 126817. The final health response was `ok`, with queue
depth zero, no queue pressure, current live receiver activity, healthy recent
AI completions, and no pending transcription audio. The temporary model tunnel
and local review server were closed after the replay.

The incident-analysis backlog grew from 291 pending calls with 2 older than its
age target to 359 pending calls with 16 older than the target while the extended
evaluation used the same Qwen service as production. The health endpoint still
reported incident analysis as current, but this is evidence that future
observation-only evaluation must be sampled or paused when normal AI work is
behind.

## Decision

Keep Qwen 35B for this work. Imperfect repeatability is manageable when
uncertain, mixed, or conflicting evidence is handled conservatively. The
revised instructions corrected the reviewed unsafe miss, preserved the labeled
examples, and mainly traded questionable opportunities for safer stops.

Do not yet let this new use of Qwen change incident membership. The next step is
an observation-only live test using the revised instructions, strict sampling,
and an automatic pause whenever normal incident analysis falls behind. Record
recommendations without changing incidents, automatically rank disagreements,
and ask for human review only when a small number of high-impact cases remain.
