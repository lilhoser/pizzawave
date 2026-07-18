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
The distributable ZIP includes a local-only
[`reviewer.html`](../scripts/incident_asr_reviewer.html) interface that reveals
candidates after playback, stores progress in browser storage, and exports a
completed structured review without uploading audio or responses.

### Provisional human review: Aaron only

Only one independent review was available. Aaron completed all 18 items on
2026-07-18. The review is retained with SHA-256
`07DE7F96BCDA0A91A22DB9914D4AD2821F5A30876E2793245C6A53361145FC29`.
These results are explicitly provisional; they do not satisfy the planned
two-reviewer reconciliation protocol and are not a reference transcript set.

| Source | Accepted, all 18 | Accepted, 10 mostly intelligible or clear | Accepted, 5 clear |
|---|---:|---:|---:|
| Stored faster-whisper small | 7 | 7 | 5 |
| Parakeet TDT 0.6B v3 | 7 | 6 | 5 |
| Whisper large-v3-turbo | 6 | 5 | 5 |

Aaron rejected all three candidates on 8 of 18 items, including two clips rated
mostly intelligible. On the 12 deliberately high-disagreement items, seven had
all candidates rejected; the stored and Parakeet transcripts were each accepted
twice and Whisper once. On the six deliberately low-disagreement items, all
three sources were accepted on five. The sixth was rated unintelligible and
contained a loud tone, so rejection of empty transcripts is not evidence of a
missed spoken phrase.

This review subset deliberately samples disagreement extremes and duration
tertiles. Its acceptance counts are not population accuracy estimates. It does
show that:

- no tested ASR source earned authority as the single transcript;
- clear, agreeing transcripts can be reliable while disagreement strongly
  identifies cases that need unresolved alternatives or direct audio review;
- each engine has distinct failure behavior, including filler hallucinations,
  omitted speech, and runaway numeric repetition;
- application code must not turn agreement or disagreement into a fixed
  semantic acceptance rule. Multiple candidates, source audio, and uncertainty
  belong in the learned observation interpretation.

The scoring artifact is
`C:\projects\pizzawave-incident-experiment-20260717\asr-human-review-v1\score-provisional-aaron.json`
and is reproducible with
[`scripts/score_incident_asr_review.py`](../scripts/score_incident_asr_review.py).

### Multi-transcript sparse-case ablation

The original two-observation sparse block was rerun after adding the Whisper and
Parakeet outputs as alternate transcripts alongside the stored transcript. The
derived development package has SHA-256
`A7EF9F66AA93E7EC53FD135E46396F4B7FFD340C7970FF6104CBDB3F0D4157C9` and is
reproducible with
[`scripts/build_incident_multitranscript_package.py`](../scripts/build_incident_multitranscript_package.py).

| Model | Duration | Completion | Reasoning | Contract errors | Semantic result |
|---|---:|---:|---:|---:|---|
| Gemma 4 26B-A4B | 16.0 s | 1,780 | 1,370 | 1 | Removed the city-destruction interpretation but created a `note creation event`; altered one exact quote |
| Qwen 3.6 35B-A3B | 34.8 s | 4,859 | 3,585 | 0 | Preserved competing proper-name readings but created a `logistical or administrative` event |

GLM was not rerun. It had already exhausted all 8,192 completion tokens without
answering the smaller single-transcript sparse case, so this ablation would not
resolve a remaining operational question for that configuration.

Multiple transcripts materially reduced the dangerous semantic error, but they
did not stop either answering model from promoting a routine utterance into an
event account. This rejects a pipeline stage that jointly resolves transcript
uncertainty and decides event existence. Observation interpretation must retain
candidate readings without being allowed to create an event. Event hypotheses
must be proposed later from bounded relational evidence and independently
critiqued.

### Observation-interpretation sparse gate

A narrower contract was then tested on the same two observations. It contained
no event identifier, incident membership, category, severity, or state-change
field. Each model interpreted one observation at a time, and a separately
invoked critic reviewed only that interpretation and its source transcripts.

Qwen failed both observations at a 4,096-token limit. It used 3,505 and 3,262
reasoning tokens and returned truncated JSON. At 8,192 tokens both Qwen and
Gemma returned contract-valid interpretations and critiques:

| Interpreter | Observation | Interpret | Critique | Semantic result |
|---|---|---:|---:|---|
| Qwen 3.6 35B-A3B | `call:25113` | 33.2 s | 41.6 s | Preserved three readings but added unsupported `route or entity` and `heading to` framing; same-model critic did not reject it |
| Qwen 3.6 35B-A3B | `call:25115` | 28.7 s | 13.0 s | Invented a shared-content statement about call ID and timestamp; critic endorsed it |
| Gemma 4 26B-A4B | `call:25113` | 24.3 s | 9.5 s | Stayed close to transcript wording but omitted any unresolved question about the contradictory readings; critic endorsed the omission |
| Gemma 4 26B-A4B | `call:25115` | 10.1 s | 5.6 s | Conservatively preserved the three incompatible fragments |

Cross-model critique did not repair the failure. Gemma approved both Qwen
interpretations. It explicitly noticed that Qwen's call-ID/timestamp statement
was not supported by its cited quotations, then declined to return a finding.
Qwen approved both Gemma interpretations and did not identify the missing
dangerous-case uncertainty.

This rejects a learned observation-normalization stage as a required semantic
authority in the candidate architecture. Exact quotation checks establish that
text exists; neither same-model nor cross-model critique established that a
model statement was entailed by that text. The two-call cost also required four
serial generations and up to 74.8 seconds for one observation, which is not a
credible Paxan production default.

The raw transcript candidates and audio reference should therefore remain the
observation evidence. A later learned event reasoner may review those sources
directly, but an intermediate model paraphrase must not replace or outrank them.
The interpretation contract and runners remain experiment scaffolding, not an
accepted production layer. The busy corpus was intentionally not run after the
sparse gate failed.

Successful sparse artifacts are stored under
`C:\projects\pizzawave-incident-experiment-20260717\observation-interpretation-v1`.
Their SHA-256 hashes are:

- Qwen `call:25113`: `0C3389C768BCE38CFF9211BC84C86362EFB6B5C680345C8D75200F4804FAE3A2`
- Qwen `call:25115`: `819EB9C7B8E33C75658D7BE0FC5A634A3458511E766CCF623DE972EE5A6E8354`
- Gemma `call:25113`: `9BC385C2E48741BFD95834860D769446126C3815B763DBA9CF05DF1D7ECFF513`
- Gemma `call:25115`: `C590071F70A4B4E629D2B9CB1AB1DB5C9040D934F9A235C908205B5534C02F11`
- Gemma-on-Qwen cross critiques: `4CDE9D040FBF0DF55A5935ECBE1260C0C4A1D05F6E2EF72096522425F76C5DAF`, `2F0D8B6114F760423F642DC3F379119CE92725494C142E1077FC6635CB1E06DF`
- Qwen-on-Gemma cross critiques: `16FF9E7945CA2B8AF0D627A927FB3278AB041E8B67F2F10161D4403892398C36`, `86D77247ECA6C175109BCF1ADAB74E81817250FFD40E80F669D05D222E62E8FE`

### Direct-audio sparse gate

[Voxtral Mini 3B](https://huggingface.co/mistralai/Voxtral-Mini-3B-2507)
was tested because its dedicated transcription and audio-understanding modes
fit in substantially less memory than the prior text models. The development
run used BF16 Transformers inference on Ventax. It peaked at approximately
9.6 GB decimal GPU memory (8.9 GiB reserved).

On `call:25113`, dedicated transcription returned:

> `You can just make a note it's going to be Pon Express Tupelo, Mississippi. Pon Express Tupelo, Mississippi.`

This independently rejects the stored `blow this city` phrase and agrees with
the core place-name reading from Whisper and Parakeet. On the nearly empty
`call:25115`, dedicated transcription returned `So.`

The same model's general audio-instruction mode was not safe. It answered the
first clip conservatively, but hallucinated `I'm going to be a little bit more
specific` repeatedly until the 512-token limit on the second clip. Direct audio
input therefore does not make a generative semantic answer source-grounded.
Voxtral audio understanding is rejected as incident or observation semantic
authority under this configuration.

Dedicated transcription was then run over the existing 18-item blind-review
sample:

| Calls | Model load | Warm inference | Calls/min | Average/call | Audio | RTF | Peak reserved |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 18 | 7.15 s | 14.86 s | 72.7 | 0.826 s | 82.56 s | 0.1800 | 8.92 GiB |

The clear clips remained strong: Voxtral exactly or materially matched Aaron's
reviewer transcript on the same five clear items where all prior engines were
accepted. The noisy items did not establish an advantage. Voxtral produced
obvious wrong phrases on several calls and a runaway repetition of `7.` on
`item-09`, which consumed 7.08 seconds by itself. Aaron did not review Voxtral
blindly, so this comparison is diagnostic rather than a new acceptance count.

Voxtral is eligible only as another transcription candidate. It has not earned
single-transcript authority or a production recommendation. Its Ventax
throughput does not establish Paxan capacity, and its roughly 9.6 GB footprint
cannot coexist with Paxan's observed 22.3 GB production LLM residency on a
24 GB GPU. Any Paxan trial must explicitly measure model switching or a
different deployment topology rather than assume simultaneous residency.

The sparse artifact SHA-256 is
`4440E1B51535FE7F578564F22105B5CDA8D2B9D2CAF07788D9BCFC99E7AC4936`.
The 18-call transcription artifact SHA-256 is
`EE13F86AB6F4E46FE5F88152687ABCFEE4851EA94CC5E77E3E29BB9FB294FCE0`.
Both are stored under
`C:\projects\pizzawave-incident-experiment-20260717` and are reproducible with
[`scripts/run_incident_direct_audio_bakeoff.py`](../scripts/run_incident_direct_audio_bakeoff.py).

A four-candidate supplemental blind package is ready under
`C:\projects\pizzawave-incident-experiment-20260717\asr-human-review-voxtral-v1`.
It contains the same 18 audio clips, with stored, Whisper, Parakeet, and Voxtral
candidate identities independently hidden for each item. The distributable ZIP
excludes the answer key and has SHA-256
`14A3FB70A4521F9D7D2A8EC8EAF797D6866A56731258FB7DB83040C261D11518`.
Its review package SHA-256 is
`491D5D8A917DC17B83D488B3889B4C5F0317B4CBD167DDA063E2F83F61DB2F2C`.
Because the available reviewer has already heard these clips, the result will
be a supplemental within-reviewer comparison, not an independent second review.

## Architectural consequence

Do not build the replacement pipeline around one model call that converts an
arbitrary time window into incident records. None of the tested models earned
that role.

The next development experiment should test a decomposed evidence ledger
without a learned paraphrase between source evidence and event reasoning:

1. Retain audio references and all transcript candidates as first-class source
   evidence; do not select or paraphrase one into authoritative observation text.
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

- Define a compact human adjudication worksheet for source-grounded claims,
  relationship evidence, missed events, false events, over-merges, and splits.
- Select representative development cases from the already-open development
  corpus without inspecting held-out data.
- Extend the blind listening package with Voxtral as a fourth transcription
  candidate if another human review pass is available; do not infer acceptance
  from text similarity alone.
- If Voxtral remains competitive after blind review, measure transcription-only
  load, switching, queue recovery, and concurrent production-LLM impact on Paxan.
- Test proposer/critic separation on those adjudicated cases.
- Freeze quantitative acceptance gates before any held-out evaluation.
- Keep all results in shadow artifacts; do not write live incident state.
