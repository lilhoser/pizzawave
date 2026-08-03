# Qwen incident-membership semantic shadow

## Purpose

This fixed OT shadow tests one narrow question: after a shared radio retrieves
an additional same-talkgroup call recorded within 60 seconds, can the small
Qwen model decide whether that call belongs with the surrounding calls without
destabilizing decisions about calls already present?

This is not a model selection exercise, a transcription test, or a production
membership change. The installed model is `qwen3-4b-instruct-2507`, loaded in
LM Studio as `pizzawave-membership-adapter` with an 8,192-token context, one
parallel request, and CPU placement beside the existing Qwen services.

## Frozen input and output

- A package is created only when the existing narrow participant-link shadow
  adds at least one candidate.
- The baseline side contains at most five existing candidates. New calls are
  retained first; the remaining calls are those closest in time to the added
  evidence.
- The participant side contains the same baseline calls plus one added call.
- Both sides use the source-bound constrained adapter. Each request returns
  only one `member`, `not_member`, `unresolved`, or `non_incident` choice bound
  directly to an application-owned source object.
- The model never emits call identifiers, observation identifiers, indices,
  positions, hashes, or transcript copies.
- Results are written only to the PizzaWave service log. They do not enter the
  incident database or production candidate selection.
- A bounded queue with one reader isolates model latency from normal incident
  processing. A fixed Unix end time prevents an unattended open-ended run.

## Frozen measurements

The analysis will report:

1. candidate cycles offered, packages completed, packages skipped because the
   queue was full, and failures;
2. exact model identity, model-request count, prompt tokens, completion tokens,
   total tokens, and elapsed time for each baseline and participant package;
3. added calls classified as event members, unresolved evidence, or clearly
   non-incident evidence;
4. shared baseline calls whose grouping or residual disposition changed when
   the radio-retrieved call was added;
5. identity, coverage, malformed-output, timeout, and model-identity errors;
6. confirmation that production participant-candidate use remained false and
   no shadow membership result was persisted;
7. queue growth and whether the single model worker remained current;
8. PizzaWave, Trunk Recorder, main Qwen, transcription, and embedding health
   before and after the trial.

The capacity gate is no queue drops and no sustained shadow backlog at the
normal five-minute incident cadence. The integrity gate is zero identity or
coverage errors and zero shadow persistence. Semantic outcomes are descriptive
until compared with human judgment; existing incidents and the model's own
repeat answers are not gold truth.

## Fixed trial result

- OT receiver window: 2026-07-31 15:12:08Z through 17:12:25Z.
- Baseline PizzaWave call identifier: 1577520.
- Participant-link candidate use remained false and every semantic result
  recorded `shadowResultPersisted=false`.
- Twenty-two candidate cycles differed. Seven only added radio evidence to
  calls already in the baseline. Fifteen contained newly retrieved calls; two
  had no baseline candidate with which to construct a comparison. The remaining
  thirteen packages completed.
- There were no queue drops, model failures, timeouts, malformed choices,
  identity mismatches, or source-coverage errors. Every response identified
  `pizzawave-membership-adapter`.
- The thirteen comparisons used 345 model requests and 279,929 total tokens.
  Median combined baseline-plus-participant time was 49.3 seconds; the 95th
  percentile and maximum were 54.6 seconds. The worker stayed current.
- Nine added calls remained unresolved. Four were placed in hypotheses and none
  were classified non-incident. That headline is misleading: two of the four
  were one-call hypotheses, not calls joined to surrounding evidence.
- Only two added calls joined any existing package call. Only one joined a call
  to which the radio ledger directly linked it, and that hypothesis also merged
  an unrelated EMS fall call. The other grouped addition did not join its
  directly linked call and instead merged unrelated EMS, sheriff, and fire
  traffic.
- Adding one call changed the grouping or residual disposition of 17 of the 65
  shared baseline call observations, across seven of thirteen packages.

## Invalid assumptions exposed by the trial

The shadow package builder did not guarantee that the exact call on the other
side of the source-identifier link was visible to the model. Four of thirteen
packages omitted that direct counterpart. This happens in part because the
production candidate list excludes calls already owned by an active incident,
while the production incident prompt supplies those incidents separately. The
new adapter supplied only candidate calls and therefore did not reproduce the
actual adjudication context.

The comparison also treated any event hypothesis containing the added call as
acceptance. That incorrectly counted a one-call hypothesis as a successful
radio-supported membership decision. The corrected interpretation distinguishes
joining an existing group, becoming a one-call hypothesis, and remaining
residual evidence.

## Decision

Do not prepare a five-call human review from this run and do not enable this
adapter for production candidate adjudication.

The source-bound transport and capacity mechanics passed. The semantic design
did not. A replacement experiment must bind a candidate decision against the
complete application-owned event context, including the exact radio-linked
conversation segment and any active incident that owns it. Its metrics must
separate joining that event from creating a singleton. Repeating the current
whole-candidate-window prompt, collecting more examples, or asking people to
review its output would measure a known-invalid formulation.

After analysis, the semantic shadow was disabled with a validated configuration
change, PizzaWave was restarted and returned healthy, Trunk Recorder remained
on PID 126817, and only `pizzawave-membership-adapter` was unloaded. The main
Qwen and transcription-sense models remained loaded.
