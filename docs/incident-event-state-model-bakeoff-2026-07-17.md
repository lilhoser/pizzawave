# Incident Event-State Model Bakeoff — 2026-07-17

## Purpose

This development-only experiment tests whether a local language model can turn a
bounded chronological set of transcript observations directly into open-world
incident hypotheses. It does not test or modify live persistence, and it does
not use the sealed held-out corpus.

The experiment is deliberately independent of v3 labels, talkgroups,
categories, embeddings, and prior incident assignments. Model input contains
only observation identifiers, timestamps, transcript identifiers, and transcript
text. Retrieval metadata is not treated as evidence.

## Frozen contract

- Corpus: `primary-corpus-v1` development blind-review package
- Corpus SHA-256: `E6854560B1AC445B01F2C48CF81DE3B51A8DC6A5A6079F58D66F685701374DA4`
- Prompt identity: `incident-event-state-transcript-only-v1`
- Temperature: `0`
- Maximum completion tokens: `8192`
- Context length: `32768`
- Structured output: strict JSON schema
- Evidence rule: every claim, relationship, and alternative must contain a
  nonempty provenance list with a verbatim quote from the referenced transcript
- Uncertainty: `0` means little uncertainty; `1` means highly uncertain
- Abstention: an empty hypothesis list is valid

The runner is
[`scripts/run_incident_event_state_model_bakeoff.ps1`](../scripts/run_incident_event_state_model_bakeoff.ps1).
It refuses paths containing `heldout-sealed`, fingerprints the corpus, model
input, prompt, and request, and preserves the raw response before parsing and
validation.

## Results

All models ran as Q4_K_M GGUFs on Ventax, an RTX 5090 Laptop GPU with 24 GB of
VRAM. Only one experiment model was loaded at a time.

| Model | Observations | Duration | Finish | Completion | Reasoning | Contract errors | Result |
|---|---:|---:|---|---:|---:|---:|---|
| Qwen 3.6 35B-A3B | 0 | 1.8 s | stop | 172 | 153 | 0 | Correctly returned no hypotheses |
| Qwen 3.6 35B-A3B | 2 | 25.8 s | stop | 3,558 | 3,129 | 0 | Invented a logistics/transport account from one likely-bad transcript; ignored the second observation |
| Qwen 3.6 35B-A3B | 133 | 65.8 s | length | 8,192 | 8,192 | 1 | Exhausted the completion entirely as reasoning and returned no JSON |
| Gemma 4 26B-A4B | 0 | 1.6 s | stop | 129 | 108 | 0 | Correctly returned no hypotheses |
| Gemma 4 26B-A4B | 2 | 9.0 s | stop | 1,049 | 700 | 0 | Interpreted likely transcription garbage as a possible literal threat to destroy a city |
| Gemma 4 26B-A4B | 133 | 35.1 s | stop | 3,583 | 1,591 | 3 | Produced four plausible-looking accounts but altered three purportedly verbatim quotations |
| GLM 4.7 Flash | 0 | 13.3 s | stop | 1,621 | 1,605 | 0 | Correctly returned no hypotheses, with high reasoning overhead |
| GLM 4.7 Flash | 2 | 73.8 s | length | 8,192 | 8,192 | 1 | Exhausted the completion entirely as reasoning and returned no JSON |

The GLM busy-block run was not performed. Failure to answer the two-observation
case within the same 8,192-token contract already rejects this configuration;
a larger request would not answer a remaining viability question.

The first Qwen sparse run predates the runner's added corpus and request
fingerprints, but used the same corpus, prompt identity, schema, model settings,
and input projection. Two earlier Qwen attempts failed inside the runner after a
model request because Windows PowerShell lacks APIs used by the initial
validation implementation. Those compatibility defects were corrected before
the recorded empty and busy runs. Failed attempts are not counted as model
results.

Raw artifacts are stored outside the repository under
`C:\projects\pizzawave-incident-experiment-20260717`.

## Findings

### A valid quote is not a valid interpretation

Strict provenance blocks fabricated source text, but it cannot stop a model
from attaching an alarming or otherwise unsupported interpretation to real but
incorrect ASR text. Gemma's sparse result demonstrates this directly.

### Whole-window synthesis is not operationally reliable

Qwen failed to answer the 133-observation block, while Gemma completed it with
contract violations. GLM failed at only two observations. Increasing token
allowances would increase latency and does not address unsupported semantics.

### Plausible output is not proof

Gemma's four busy-block accounts look plausible. Without human adjudication
against source audio and observation-level evidence, that appearance is not a
quality measurement. These outputs must not be used to validate the proposed
architecture or to populate incidents.

### Model-generated uncertainty is not a safety boundary

The sparse models assigned uncertainty to their interpretations, but still
created event accounts. Uncertainty is useful telemetry; it is not a substitute
for evidence validation or an abstention gate.

## Audio transcription follow-up

The transcript-only failures justified a development-audio check before further
event-model tuning. Two additional ASR engines were run over all 417 development
calls on Ventax through a long-lived Python/PyTorch worker:

- [OpenAI Whisper large-v3-turbo](https://huggingface.co/openai/whisper-large-v3-turbo)
- [NVIDIA Parakeet TDT 0.6B v3](https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3)

| Engine | Calls | Audio | Model load | Inference | Warm calls/min | Average seconds/call | RTF | Failures |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| Whisper large-v3-turbo | 417 | 3,273 s | 2.42 s cached | 49.67 s | 506.9 | 0.119 | 0.0152 | 0 |
| Parakeet TDT 0.6B v3 | 417 | 3,273 s | 2.87 s cached | 86.52 s | 290.8 | 0.207 | 0.0264 | 0 |

The first uncached downloads and loads were approximately 47 seconds for
Whisper and 71 seconds for Parakeet. These are setup observations, not stable
cold-start measurements, because they include network transfer and local model
cache population.

Both engines have ample throughput on Ventax. Neither has yet earned a quality
recommendation. On one 133-call block, median normalized word-edit disagreement
was 0.690 between the stored transcript and Whisper, 0.833 between the stored
transcript and Parakeet, and 0.778 between Whisper and Parakeet. Whisper emitted
`Thank you.` for 16 of those calls, while Parakeet returned 11 empty transcripts.
Those are review signals, not automatic error labels.

The sparse false-alarm call is nevertheless decisive evidence of error
propagation:

| Source | Transcript |
|---|---|
| Stored faster-whisper small | `You can just make a note, it's going to be PON express to blow this city, PON express to blow this city.` |
| Whisper large-v3-turbo | `Oh, you can just make a note. It's going to be Pond Express, Tupelo, Mississippi. Pond Express, Tupelo, Mississippi.` |
| Parakeet TDT 0.6B v3 | `Oh, you can just make another's gonna be Pawn Express, Tipolo, Mississippi. Pawn Express, Tipolo, Mississippi.` |

The two stronger ASR engines independently agree on the core place/name
sequence and contradict the stored phrase that the event models interpreted as
logistics or a threat. This does not prove either new transcript word-for-word;
it proves that one transcript cannot safely be treated as observed truth.

The reproducible harness is
[`scripts/run_incident_asr_bakeoff.py`](../scripts/run_incident_asr_bakeoff.py).
Full raw artifacts are under
`C:\projects\pizzawave-incident-experiment-20260717\asr`.

A blind 18-call listening package was generated from all 417 development calls
using duration tertiles and high/low three-way transcript disagreement. It does
not select on transcript words, talkgroups, incident history, or application
labels. Candidate identities are hidden from reviewers; the answer key is kept
separate. The package is under
`C:\projects\pizzawave-incident-experiment-20260717\asr-human-review-v1` and is
reproducible with
[`scripts/prepare_incident_asr_review.py`](../scripts/prepare_incident_asr_review.py).

## Architectural consequence

Do not build the replacement pipeline around one model call that converts an
arbitrary time window into incident records. None of the tested models earned
that role.

The next development experiment should test a decomposed evidence ledger:

1. Produce observation-level interpretations that preserve competing readings
   and may abstain, including multiple transcript candidates and an explicit
   assessment of transcript reliability.
2. Propose relationships only between bounded observations or existing
   hypotheses, with evidence for the relationship itself.
3. Maintain hypotheses as revisable state rather than as completed incident
   labels.
4. Run an independent critique step that can reject unsupported claims,
   over-merges, and false certainty before projection.
5. Score proposals against human-adjudicated development cases, including source
   audio, before freezing gates and opening the sealed held-out corpus.

Candidate retrieval may reduce the comparisons required, but retrieval scores,
time proximity, metadata, and embeddings remain routing aids rather than proof
of incident membership.

## Next gates

- Define a compact human adjudication worksheet for observation interpretation,
  relationship evidence, missed events, false events, over-merges, and splits.
- Select representative development cases from the already-open development
  corpus without inspecting held-out data.
- Compare transcript-only interpretation with audio-grounded interpretation to
  measure how often ASR corruption causes false incident semantics.
- Test proposer/critic separation on those adjudicated cases.
- Freeze quantitative acceptance gates before any held-out evaluation.
- Keep all results in shadow artifacts; do not write live incident state.
