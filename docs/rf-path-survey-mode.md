# Site Setup RF Validation

Setup is the unified site/location setup, calibration, TR config, talkgroup
import, and RF validation workflow. Its RF validation phase answers a field
question:

> Given this exact antenna, coax, connectors, splitter/multicoupler, LNA,
> filters, SDR, location, gain, error, sample rate, source plan, and site, will
> this rig reliably decode, capture, and transcribe this site?

The goal is not to replace normal PizzaWave monitoring. The goal is a
repeatable acceptance test that turns RF setup into measured evidence instead
of ad hoc GQRX screenshots, manual trunk-recorder experiments, and hardware
memory.

## Product Shape

Setup is available from the main navigation as `Setup`. It is re-entrant:
entering Setup does not stop live monitoring by itself. Disruptive steps such as
waterfall, RF sweep, and applying config stop or restart trunk-recorder only for
the bounded operation that needs exclusive SDR/TR access.

Setup owns the live desired site state, RF path, RF validation evidence, source
planning, config draft, apply/resume, and activity log. RF validation evidence is
stored against the current Setup session rather than as operator-managed saved
profiles.

For agent and operator work, the PizzaWave API is the primary diagnostic
surface. Do not treat old user-local Codex skills or standalone RF scripts as
the normal path. They are fallback/debug aids only when the API is unavailable
or when validating parser differences.

AI Insights is a prerequisite for guided mode. Manual/expert mode may run the
same deterministic tests without AI interpretation, but the product-quality
flow is AI-guided.

External RF/P25 tooling is also a first-class requirement. Setup should have a
prep stage that explains which tools are needed for the selected exercises,
detects what is already installed, and installs or guides installation of
missing dependencies through guarded setup jobs.

## Implemented API Workflow

Use these endpoints for current Setup and RF diagnostic work.

Setup desired/applied state:

- `GET /api/v1/setup/site`: load current Setup desired/applied state.
- `PATCH /api/v1/setup/site`: update desired location, systems/sites,
  talkgroups, hardware/RF path, selected source plan, or operator evidence.
- `POST /api/v1/setup/site/mark-applied`: record that reviewed changes were
  applied to the live monitoring configuration.
- `GET /api/v1/setup/site/activity`: load Setup activity/audit log.

RF validation state:

- `GET /api/v1/setup/site/rf`: load or create the current Setup RF validation
  session from desired Setup state.
- `GET /api/v1/setup/site/rf/{id}`: load RF profile, experiments, tool prep,
  and next experiment recommendations for the current Setup RF session.
- `GET /api/v1/setup/site/rf/{id}/config-draft`: build the TR config draft from
  Setup source planning and validated RF evidence.
- `POST /api/v1/setup/site/rf/{id}/tr/apply-source-draft`: apply the reviewed
  Setup TR config and clear the expert config editor draft.

Experiment execution:

- `POST /api/v1/setup/site/rf/{id}/experiments/run`
- `POST /api/v1/setup/site/rf/{id}/experiments/cancel`
- `GET /api/v1/setup/site/rf/{id}/sweep-progress`
- `POST /api/v1/setup/site/rf/{id}/sweep-insights`
- `POST /api/v1/setup/site/rf/{id}/waterfall/start`
- `GET /api/v1/setup/site/rf/{id}/waterfall`
- `POST /api/v1/setup/site/rf/{id}/waterfall/stop`

Supported experiment `type` values:

- `ground_truth_review`
- `tr_stopped_check`
- `sdr_inventory`
- `rf_power_scan`
- `control_channel_quality`
- `control_channel_p25_probe`
- `error_gain_sweep`
- `error_gain_sweep_cancel`
- `temp_tr_config_plan`
- `voice_capture_trial`
- `transcription_gate`
- `stability_verdict`

Troubleshooting and field comparisons:

- `GET /api/v1/troubleshoot?start=<unix>&end=<unix>&bySystem=true`
- `GET /api/v1/troubleshoot/rf-analysis?system=<shortName>&start=<unix>&end=<unix>`
- `GET /api/v1/troubleshoot/tr-health?start=<unix>&end=<unix>`

For 30-minute antenna A/B baselines, record exact Unix start/end timestamps and
use the troubleshooting/RF-analysis endpoints for the fixed window. Do not use
`control_channel_quality` as the sole 30-minute A/B evidence path because that
Setup RF experiment is capped at 900 seconds by design. It is still useful
inside the interactive workflow for shorter bounded CC checks.

## Inputs

Survey inputs are seeded from completed setup:

- monitored areas, systems, and site labels;
- current trunk-recorder systems and control channels;
- current source centers, sample rates, gains, errors, modulation, device
  serials, and recorder counts;
- detected SDR inventory;
- configured transcription provider and AI Insights readiness.

The operator adds reusable RF-path metadata:

- antenna brand/model/type, with yagi supported as a first-class path;
- antenna aim/bearing, polarization, height, indoor/outdoor placement, and
  location notes;
- connector/adaptor chain;
- coax type and length;
- splitter or multicoupler;
- LNA, placement, power method, and bias tee state;
- band-pass filters;
- SDR model, serial, USB port/controller notes, and whether it is Airspy or
  RTL-SDR;
- manual observations, such as "signal disappears when antenna removed".

RTL-SDR and Airspy must be modeled separately because their usable sample rates,
bandwidth, tuning behavior, and source coverage options differ materially.

RadioReference or other imported ground-truth frequency data is a first-class
input. Unknown coverage is allowed, but any source-coverage verdict must say
when it is based on incomplete ground truth.

## Persistence

Surveys persist in both the database and self-contained artifact folders.

The database indexes survey history for browsing, filtering, recommendations,
and System integration:

- survey/session ID;
- site/system label;
- RF path profile summary;
- SDR type and serials;
- verdict and stability result;
- best control channel;
- source plan summary;
- decode/capture/transcription scores;
- recommendation state.

Each completed or in-progress Setup RF session also writes an artifact folder:

```text
/var/lib/pizzawave/rf-surveys/site-setup/
  survey.json
  input-profile.json
  tr-config-before.json
  tr-config-candidate.json
  control-channel-results.json
  source-coverage-results.json
  voice-capture-results.json
  transcription-results.json
  ai-interpretations.json
  recommendations.json
  notes.json
  logs/
  audio-samples/
```

Completed experiment results are immutable. Operators may add annotations,
notes, and tags after completion, but measured results and AI interpretations
remain preserved as-run.

## Experiment Loop

Setup RF validation is not a strictly linear wizard. It is a wizard-like
experiment loop inside Setup.

Each experiment records:

- the hypothesis being tested;
- the physical setup the operator must use;
- temporary TR config changes, if any;
- actions run by PizzaWave;
- measurements collected;
- AI interpretation of measured facts;
- ranked follow-up exercises;
- user decision: run, skip, annotate, stop, apply, export, or move to verdict.

The LLM interprets factual results from the test that just ran. It should not
invent measurements, substitute estimates for tooling, or ask broad open-ended
diagnostic questions as the main flow. It should propose a few concrete
follow-up exercises from the valid experiment types.

Example: if no P25 frames are decoded, the AI may recommend:

- bypass splitter/multicoupler and retest the direct SDR path;
- run an antenna-disconnect control to detect local spurs;
- sweep gain down/up on the same control channel;
- test alternate control channels from ground-truth data;
- record a yagi bearing/position change and retest.

The loop continues until the tool has no useful next experiment, enough evidence
exists for a verdict, or the user chooses to move on.

## Tool Prep

Setup needs tooling before it can run credible experiments. The prep stage
should be explicit in the UI and should run before the first measurement.

The prep stage should:

- inspect the host OS, CPU architecture, attached SDRs, and current setup state;
- show required and optional tools for the selected Setup RF path;
- explain why each tool is needed;
- detect installed versions and missing dependencies;
- install or update missing tools through bounded, guarded setup jobs;
- record tool versions in the Setup RF artifact folder;
- refuse experiments whose required tooling is unavailable.

Likely tool categories:

- SDR enumeration and low-level access for RTL-SDR and Airspy;
- P25 control-channel/frame testing before voice capture;
- trunk-recorder temporary config validation and bounded run control;
- audio inspection for captured samples;
- configured PizzaWave transcription provider for the transcription gate.

The exact P25 tool choice should remain implementation-specific until verified
on target hosts. Do not assume "P25" is a single portable binary. The design
should support whichever validated toolchain can reliably report P25 frame/sync
presence, NAC/RFSS/site ID when available, control-channel decode quality, and
grant evidence on the target architecture.

trunk-recorder remains the acceptance path for source plans and voice capture,
but lower-level P25 tooling is expected to be necessary before voice trials. It
lets Setup distinguish "no P25 frames here" from "TR config/source plan is
wrong" earlier in the exercise loop.

## TR Ownership And Temporary Configs

Setup must make clear that trunk-recorder can be briefly paused during
RF validation measurements and normal coverage will pause during that bounded
measurement window. No Setup RF step should leave trunk-recorder stopped when
the step finishes, fails, or is canceled; the service must be restarted as part
of cleanup when PizzaWave paused it.

Experiments may create temporary trunk-recorder configs. Any temporary config
change requires a TR restart. Every such experiment must:

- snapshot the active config first;
- write the candidate config through the guarded setup/admin path;
- restart TR for the measurement window;
- collect bounded logs/metrics;
- restore or move to the next candidate config deliberately;
- restart TR before returning control to the operator.

The final "apply now" action is separate from running temporary experiments.
Apply must be guarded, create a backup, explain the coverage impact, and then
restart TR.

## Setup RF Phases

### 1. Preflight

- Confirm setup is complete enough to seed site/source/device data.
- Confirm AI Insights readiness for guided mode.
- Confirm transcription readiness because transcription quality is a hard gate.
- Detect attached SDRs and classify RTL-SDR versus Airspy.
- Load current TR config and candidate ground-truth frequencies.
- Warn that TR must stop and live coverage will pause.

### 2. Tool Prep

- Detect required SDR, P25, TR-control, audio-inspection, and transcription
  tooling.
- Show missing tools and what each one will be used for.
- Install or update missing dependencies through setup jobs when supported.
- Persist tool versions and prep results with the Setup RF session.
- Block only the experiments whose required tools are missing; allow unrelated
  profile/review work to continue.

### 3. Control Channel Exercises

For each selected candidate control channel:

- run bounded decode windows;
- report P25 frame/sync presence;
- decode NAC/RFSS/site ID when available;
- report control-channel decode-rate distribution;
- count zero-decode windows, retunes, and dropouts;
- capture grants and neighbor/site messages when available;
- measure stability across multiple windows.

If decode works briefly and then collapses, the session must preserve that as a
stability failure rather than treating the channel as usable.

### 4. Source Coverage Exercises

Compare the candidate source plan against known or observed site frequencies:

- control channels;
- voice frequencies from RadioReference/imported data when available;
- observed grants from the control-channel exercise;
- RTL-SDR and Airspy sample-rate/source-window limits;
- off-center plans that miss voice channels.

The tool should recommend one-SDR, Airspy, dual-RTL, or other multi-source
plans based on measurable coverage. If ground truth is incomplete, mark the
coverage result as unknown/incomplete.

### 5. Voice Capture And Transcription Exercises

A Setup RF verdict cannot pass until real calls are captured and transcribed.

For bounded capture trials:

- count grants, recorder starts, clean stops, no-source outcomes,
  no-transmission outcomes, and recorder exhaustion;
- persist sample audio for the session;
- run configured transcription on captured samples;
- classify transcription quality;
- fail validation if transcription quality is below PizzaWave's useful-call
  bar, even if RF decode and capture look acceptable.

If no real traffic occurs, the verdict must remain incomplete or partial. Do not
declare a passing verdict on control-channel evidence alone.

### 6. AI Interpretation And Recommendations

AI receives bounded evidence packets:

- Setup RF profile and physical RF path;
- setup-derived site/source data;
- ground-truth frequency data when available;
- current experiment measurements;
- prior experiments in this session;
- allowed next experiment types.

AI returns structured JSON:

- interpretation summary;
- confidence;
- blocking issue category;
- ranked next experiments;
- whether a verdict is possible;
- source-plan recommendation if enough evidence exists;
- RF-path recommendations.

Session-local recommendations are shown inside Setup throughout the exercise.

When the user exits Setup, System recommendations should learn from the completed
Setup RF evidence. If the session produced applicable recommendations that were
not completed in Setup, add System recommendation cards. If the user clicked
"apply" and all applicable recommendations were applied during Setup, no System
card is needed.

AI-driven System recommendations must cite the Setup RF session evidence they are
based on.

## Verdicts

The final verdict must account for decode, source coverage, capture,
transcription, and stability.

- `PASS STABLE`: control decode, source coverage, voice capture, transcription,
  and stability all meet the acceptance bar.
- `PASS PARTIAL`: useful limited operation, but one or more dimensions are
  incomplete or limited.
- `INCOMPLETE TRAFFIC`: RF/control evidence exists, but not enough real calls
  were captured and transcribed to pass.
- `FAIL RF`: no reliable P25 control-channel frames.
- `FAIL UNSTABLE`: decode/capture works briefly but fails across required
  stability windows.
- `FAIL COVERAGE`: control-channel decode works, but source coverage misses
  required or observed voice frequencies.
- `FAIL AUDIO`: grants start recorders, but audio is absent, near-silent,
  clipped, or otherwise unusable.
- `FAIL TRANSCRIPTION`: RF/capture works, but transcription quality is below
  PizzaWave's acceptance bar.
- `FAIL HARDWARE PATH`: a simpler/direct path works, but the tested splitter,
  multicoupler, LNA, cable, adapter chain, USB layout, or other path change
  fails.

Minimum stable-pass target for MVP:

- at least three control-channel measurement windows, such as 3 x 5 minutes;
- no unexplained decode collapse during those windows;
- at least one voice/transcription trial with real captured calls;
- no passing verdict without usable transcriptions.

## MVP Scope

MVP should include:

- setup-seeded reusable survey profiles;
- web UI in setup calibration and System;
- database index plus self-contained artifact folders;
- explicit tool prep and dependency installation/detection;
- RTL-SDR and Airspy-aware source coverage logic;
- manual RF-path profile entry;
- RadioReference/imported ground-truth frequency entry or selection;
- TR-stopping workflow with explicit coverage warning;
- required P25 frame/control-channel testing before voice trials;
- temporary TR config experiments with guarded restart/restore behavior;
- control-channel stability exercises;
- source-coverage analysis;
- bounded voice capture and transcription quality gate;
- AI-guided interpretation and follow-up experiment recommendations;
- survey-local recommendations;
- System recommendation handoff after survey exit;
- guarded apply-now and export-plan outcomes.

Defer from MVP:

- CLI;
- IQ capture/replay;
- fully automated yagi bearing sweeps. Bearing/position are notes in MVP.

Implemented in Setup after this original MVP split:

- live waterfall/spectrum visualization;
- waterfall screen grab export;
- selected-control-channel signal checks;
- P25 identify from the waterfall candidate table;
- waterfall-to-RF-sweep handoff for selected control channels, gain, sample
  rate, source, and recommended error.
