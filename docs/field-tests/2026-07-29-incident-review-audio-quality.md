# Incident Review Audio Quality Check

Date: 2026-07-29

Status: the first measurement pass and small listening comparison are complete.
Do not expand incident-membership review production from this evidence.

## Question

The first 11 incident-membership review windows were difficult to judge because
many recordings could not be understood. This check asks whether the limiting
problem is recording quality rather than the transcription model or incident
grouping design, and whether simple audio processing can recover useful speech.

This is not another transcription-model comparison. It does not change live
recordings, Trunk Recorder, production incidents, or transcription settings.

## Inputs

- Reviewer export:
  `incident-membership-review-local-reviewer-1.json`
- Review package content hash:
  `A0A32EFE9E9DC9735F1EFEC9BBB09B47EBD77C069C5FE1E8F11BFBBAF87F362A`
- Preserved training archive hash:
  `33F1CD7C892482F53C691D518A57BA7706638B2E21BE4DFB04EE0B6DB6CBFFFB`
- 66 mono, 16-bit, 8 kHz PCM recordings from the development-only review
  package

The sealed held-out outcomes were not opened.

## What The 11 Reviews Show

- 11 review windows were completed.
- 66 recordings were presented.
- 22 recordings were marked as having unintelligible audio.
- 48 recordings were left as unsure membership decisions.
- 13 recordings were included in incidents.
- 5 recordings were marked do not include.

This is enough evidence to pause a paid review pilot. It is not a representative
estimate of all PizzaWave recordings because the review windows were selected
for ambiguous grouping decisions.

## Recording Measurements

| Human audio label | Recordings | Mean duration | Mean level | Mean near-silent portion | Mean active audio |
| --- | ---: | ---: | ---: | ---: | ---: |
| Marked unintelligible | 22 | 2.51 seconds | -55.20 dBFS | 88.1% | 0.34 seconds |
| Not marked unintelligible | 44 | 10.27 seconds | -45.78 dBFS | 77.1% | 3.23 seconds |

The unintelligible group was about 9.4 dB quieter by whole-file root mean square
level, much shorter, and contained about one tenth as much audio above -45 dBFS.
Several files were effectively silent. None of the 66 files showed meaningful
clipping.

These measurements support the reviewer's observation that this is not solely a
word-choice problem in transcription. They do not prove that increasing volume
will recover speech. Increasing the level of a recording containing noise or a
failed voice decode only produces louder noise.

## System, Talkgroup, Frequency, And Time Limits

Every one of the 66 reviewed recordings, and every one of the 417 recordings in
the preserved development audio collection, came from `etv-raymond-hinds`.
There is no second system or receiver site in this evidence. It therefore cannot
answer whether one PizzaWave site is worse than another.

The reviewed recordings cover only these local-hour groups:

| Local hour | Recordings | Marked unintelligible | Unsure membership |
| ---: | ---: | ---: | ---: |
| 11 | 12 | 6 | 8 |
| 19 | 24 | 6 | 18 |
| 20 | 30 | 10 | 22 |

The seven voice frequencies each contain only 6 to 13 reviewed recordings.
Their unintelligible counts range from 1 of 8 to 6 of 13. That is not enough to
declare a frequency or talkgroup defective. A later comparison must deliberately
sample multiple systems, sites, talkgroups, frequencies, and hours rather than
reusing the incident-membership selection.

## Radio-Health Comparison

The exact first review interval, 2026-07-12 11:30-11:40 EDT, had a 36.00
control-channel messages-per-second average, no zero summary samples, a 2.00%
zero rate among per-frequency samples, four retunes, no recorder exhaustion, and
four no-transmission outcomes.

The exact second review interval, 2026-07-13 19:40-20:00 EDT, had a 22.33
control-channel messages-per-second average, no zero summary samples, no zero
per-frequency samples, no retunes, no recorder exhaustion, and ten
no-transmission outcomes.

The second interval was weaker, but neither interval had a complete
control-channel loss. Poor voice recordings occurred while the control channel
was decoding. Control-channel health does not measure the signal or decode
quality of each assigned voice frequency, so this neither proves nor disproves
a voice-path radio problem.

The current passive radio telemetry has no synchronized per-call received power,
signal-to-noise ratio, noise floor, or retained voice-channel samples. A causal
radio-quality claim would exceed the evidence.

## Existing Product Blind Spots

The current Setup voice-capture check passes when it finds any real audio file.
It does not measure silence, active speech duration, level, or distortion. The
transcription check passes when it finds at least one transcript that existing
text rules call usable.

For the three days surrounding these recordings, PizzaWave reported 11,525 of
16,847 calls as usable transcripts and only one call as unusable audio. The
human review marked 22 of its 66 selected recordings unintelligible. The samples
are not directly comparable, but the product's unusable-audio count is clearly
not a human intelligibility measure.

## Loudness Processing Constraint

Do not simply re-enable Trunk Recorder `loudnorm` on RPI. The documented
2026-05-19 trial found an `ffmpeg` loudness-normalization process using about 94%
CPU. Disabling it reduced 15-minute recorder exhaustion on RPI from 63 to 2 and
kept the transcription queue clear.

The current listening comparison instead operates after capture, on copies. It
includes:

1. The unchanged original.
2. Peak normalization capped at 30 dB of gain.
3. A gentle 180-3400 Hz speech-frequency filter followed by the same bounded
   gain.

If either processed copy helps, the next design should preserve the original and
create a derived audio stream outside Trunk Recorder's capture-critical path.

## Listening Comparison Result

The reviewer completed all 12 comparisons:

| Answer | Recordings |
| --- | ---: |
| None are understandable | 7 |
| Speech frequencies and volume adjusted | 3 |
| No meaningful difference | 2 |
| Volume adjusted | 0 |
| Original | 0 |

Eight of the comparison recordings had previously been marked unintelligible.
Of those eight, six remained unintelligible, one showed no meaningful
difference, and one favored the speech-frequency-filtered copy. The simple
filter therefore recovered at most one of eight previously unintelligible
recordings in this small check. Volume adjustment alone recovered none.

This result rejects loudness normalization as the primary remedy. A
speech-frequency filter may improve some recordings, but the evidence does not
support putting it into the production path or assuming that it can rescue the
missing information.

## Small-Model Transcript Usefulness Check

Qwen3 1.7B Q8 was loaded beside the main Qwen model with graphics-card offload
disabled, a 2,048-token context, and one parallel request. It used about 1.7 GiB
of system memory. The main model remained loaded and responsive.

The small model received one transcript at a time and returned only one of
`useful`, `context_only`, or `unusable`. It never received a call identifier,
source key, index, position, or neighboring transcript. The evaluation program
attached each answer to the source call after the response. This made the
mapping deterministic without asking the model to reproduce source identity.

The final fixed prompt classified the 66 reviewed transcripts as follows:

| Model decision | Calls |
| --- | ---: |
| Useful | 25 |
| Useful only with nearby calls | 2 |
| Unusable | 39 |

The mean response time was 568 milliseconds. The model called 19 of the 22
human-marked unintelligible recordings unusable, but that apparent agreement is
not enough. The human mark describes the audio, while the model saw only the
transcript.

More importantly, the model rejected 5 of the 13 calls the reviewer included
in an incident. Those five are not all marginal fragments. Rejected transcripts
included an animal-control exchange at 144 Blueberry Hill Road, a medical call
at 260 Low Circle, and a reported shot fired at 4136 Old Jackson Road. An
automatic exclusion rule would therefore remove useful incident evidence.

The result rejects Qwen3 1.7B as an authority for admitting transcripts into an
incident window. It may be used in a read-only observation mode to count and
inspect suspicious transcripts, but its answer must not drop a call, make a
membership decision, or convert unresolved evidence into a standalone
incident. The source call and original transcript must remain stored and
auditable.

## Cross-System Production Comparison

The existing System > Performance summaries for the same three-day period show
a large difference between RPI Raymond and the three OT systems:

| Host and system | Calls | Existing text rules called usable | Very short calls |
| --- | ---: | ---: | ---: |
| RPI `etv-raymond-hinds` | 16,847 | 68.4% | 14.2% |
| OT `whiteoakmt-hamilton` | 19,587 | 88.5% | included in OT total below |
| OT `whiteoakmt-cleveland` | 3,665 | 94.3% | included in OT total below |
| OT `whiteoakmt-nbradley` | 1,219 | 94.7% | included in OT total below |

Across all 24,471 OT calls, 1.37% were classified as too short. Across the RPI
calls, 14.22% were classified as too short. These are text-rule outcomes rather
than human intelligibility judgments, but the scale of the difference warrants
a balanced recording-quality audit by host and system. It is evidence against
treating the reviewed Raymond sample as a general transcription-model result.

## Decision After The Listening Comparison

- Do not spend more time on volume-only processing.
- Do not run another transcription-model comparison.
- Treat unintelligible calls as missing semantic evidence and prevent them from
  entering membership training as ordinary examples.
- Measure the problem with a balanced human-reviewed sample from each system
  and site.
- Add a human intelligibility measurement to the existing System > Performance
  workflow. Do not infer intelligibility from transcript length or repetition
  rules.

The minimum balanced audit should report, for each system and site, the fraction
of recordings humans can understand, the fraction made understandable by the
approved processing method, and results by talkgroup, voice frequency, local
hour, and matched five-minute radio-health bucket. Groups with few reviewed
recordings must remain visibly inconclusive.

## Reproduction

Run the standard-library-only analysis tool against a review package and its
reviewer export:

```powershell
python scripts/analyze_incident_review_audio.py `
  --package <review-package.json> `
  --review <reviewer-export.json> `
  --output artifacts/audio-quality-check `
  --comparison-count 12 `
  --listening-review <pizzawave-audio-processing-review.json>
```

The output contains `measurements.csv`, `measurements.json`, `summary.json`, the
listening page, processed copies, and `listening-results.json`. The source
recordings remain unchanged.

Run the transcript-usefulness evaluation separately:

```powershell
python scripts/evaluate_transcript_sense_model.py `
  --package <review-package.json> `
  --review <reviewer-export.json> `
  --output artifacts/transcript-sense-check `
  --model pizzawave-transcript-sense
```

This writes `results.csv`, `results.json`, and `summary.json`. It performs no
production writes.
