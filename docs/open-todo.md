# PizzaWave Open TODO

Last consolidated: 2026-05-27

This file is the active backlog. Historical checkpoints and deployment notes
belong in `docs/current-status.md`; completed work should not remain here
unless it creates a current regression test or follow-up.

## Current Priorities

1. Clean rig setup and setup API audit.
2. RF Path Survey setup/calibration mode.
3. Hourly PizzaWave quality monitoring.
4. Focused test coverage where it gives real signal.
5. Incident/RAG/geocoding quality improvements.
6. Documentation pruning and alignment.

## Clean Rig Setup And Setup API Audit

- Test the full installation/setup wizard on a clean rig or clean environment.
- Include native Qdrant install/detection, embeddings validation, admin-token
  setup, service controls, and the RPI 16 KB-page Qdrant path.
- Include the LM Studio split-routing scenario:
  - remote AI Insights model on a GPU host;
  - local embedding model on the rig;
  - disabled local chat-model JIT and disabled peer model discovery;
  - conditional local embedding autoload at `lmstudio.service` startup;
  - explicit model IDs and Qwen thinking-mode validation through
    `message.content`, `reasoning_content`, and `reasoning_tokens`.
- Keep backup/restore and Reset + Setup in the clean-rig regression test.
  Backup/restore field testing succeeded on the live RPI, so treat this as a
  regression scenario rather than an unknown feature spike.
- Fix restore/deploy frontend state handling so Settings cannot temporarily show
  stale defaults while the backend config has already been restored correctly.
- Audit setup, calibration, service, and admin endpoints during the setup flow
  for operator value, write-auth expectations, guardrails, bounded runtime/cost,
  safe failure behavior, and absence of hidden backfill/import/transcription
  flood behavior.
- Continue the remaining setup/calibration endpoint audit after the clean
  installer test. The first cleanup pass already removed deprecated rig-side
  mutation endpoints for transcription retries, manual incident
  generation/rebuild, recommendation auto-apply, and standalone TR CSV
  generation.
- Reintroduce a dedicated talkgroup enable/disable interface outside Setup.
  Setup should keep loading the catalog from selected RadioReference systems;
  operator policy such as disabling noisy TGs, incident eligibility, and future
  profile-specific TG rules needs its own catalog/policy surface.

## RF Path Survey

- Add the RF Path Survey setup/calibration mode described in
  `docs/rf-path-survey-mode.md`.
- The goal is a repeatable acceptance test for whether a specific
  antenna/coax/splitter/LNA/SDR/location can decode a target site.
- Capture enough evidence to produce a pass/partial/fail verdict before
  generating a trunk-recorder source plan:
  - rig hardware and RF-chain variables;
  - waterfall/RF evidence;
  - P25 control-channel decode results;
  - voice grant and recorder-start evidence;
  - source coverage and audio capture quality;
  - recommended TR source layout and rejected alternatives.
- For Raymond/MS bring-up, freeze one stable portable rig before more house
  testing. Preferred direction is an Airspy Mini/R2 single wider source with a
  compact 700/800 antenna/counterpoise path. Dual RTL remains supported, but in
  challenged RF it should use an active multicoupler such as `MCA780M`, not a
  passive splitter.

## Hourly Quality Monitoring

- Run `pizzawave-cross-rig-quality-check` as the single recurring quality
  monitor.
- Use the rig-local `/api/v1/system/quality-check` endpoint where possible for
  call, transcript, AI, evidence-verifier, incident, and audit windows.
- Analyze OT/omicrontheta as the Hamilton/TN quality target.
- Analyze relocated RPI independently as Raymond/MS when reachable. Do not run
  OT-vs-RPI comparison until a replacement Hamilton/TN RPI exists.
- Cover:
  - service/runtime health;
  - transcription queue health, throughput, pending calls, and pending audio;
  - 24h transcription quality with reason breakdown and whether non-OK calls
    reached incidents, alerts, or insight events;
  - LLM/AI backlog, load, token pressure, truncation, failures, and latency;
  - Qdrant and embedding health;
  - incident/RAG quality and evidence-verifier behavior;
  - accepted/rejected incident operations and prompt/candidate pressure;
  - dashboard incident/call grouping and map/geocoding misses;
  - RF/decode metrics when they materially explain capture, transcription, or
    incident quality.
- Watch for false joins, missed related calls, low-confidence incidents,
  noisy/short calls entering incidents, weak geolocation/time matches, and
  clear-address incidents that fail to map.
- Do not let the automation mutate data, rerun backfills, restart services,
  deploy, or purge historical data.

## Operational Watch Items

- Continue watching RPI queue health after relocation as Raymond/MS bring-up:
  pending audio seconds, queue depth, recent audio seconds ingested/transcribed,
  and calls per minute.
- Keep RPI dashboard/system load under observation after the post-transcription
  split and LM Studio CPU cap. Recheck clean live-only windows before changing
  the embedding architecture.
- Keep TR resource pressure in the quality monitor. Compare high-load
  talkgroups, recorder counts, no-transmission outcomes, and RF decode quality
  before reducing capture capacity.
- Continue monitoring omicrontheta RF health, including all systems in the same
  RF pass. Pay special attention to whiteoakmt-cleveland recorder exhaustion
  and whiteoakmt-nbradley CC-zero/retune behavior.
- Investigate whether trunk-recorder autotune belongs in the RF troubleshooting
  playbook. If adopted, define when to recommend it, required TR config
  changes, restart expectations, and before/after success metrics.
- Revisit faster-whisper quality/performance only if queue pressure or incident
  evidence quality shows a current problem.

## Incident, RAG, And Geocoding Quality

- Evaluate v1 LLM-managed incident generation through the hourly quality
  monitor.
- Validate prompt growth under real traffic: candidate-call count, transcript
  truncation, payload size, model finish reasons, and UI responsiveness.
- Monitor carryover lossiness: how often eligible unassigned calls age out
  before reaching incident context, and whether high volume causes incidents to
  lose important context before they conclude.
- Monitor evidence-verifier lossiness through `evidence_verifier_runs`:
  reviewed/model-reviewed calls, truncated calls, added/dropped/retained calls,
  verifier failures, and related `lm_usage` finish reasons.
- Continue watching incident-extraction truncation and output pressure. Do not
  add retries unless there is a capped, low-priority, telemetry-backed design
  that cannot flood the shared LM pipeline.
- Expand the Qdrant/RAG quality study with adversarial samples. Record false
  joins, missed related calls, geolocation boost behavior, same-category but
  unrelated traffic, short/noisy-call exclusions, and whether retrieval lowers
  LLM prompt pressure.
- Check that incidents can span categories/talkgroups within a site without
  merging unrelated county/site traffic.
- Tighten incident title/detail conservatism for terse vehicle, plate, unit
  status, code, or partial-location traffic. Prefer neutral titles/details when
  evidence is thin.
- Expand anchors beyond deterministic extraction. A later pass should let the
  verifier/extractor emit structured anchor support and conflicts so validation
  can compare strong matches, soft conflicts, and bridge language without
  relying only on regex.
- Confirm the routine compliance/status guard does not reject legitimate
  same-event updates.
- Improve incident geocoding coverage with an explicit background geocoding or
  retry path for incident title/detail locations. Do not do live geocoding in
  `/api/v1/dashboard`.
- Monitor generic location extraction after removing local geography
  assumptions. Watch for vague "heard as" values and geocoder matches not
  justified by the transcript phrase.
- Add a configured-area/bounds API only if operators still need visible
  monitored-area boxes on the map. Do not reintroduce hard-coded frontend
  bounds.
- If live v1 incident quality holds up, plan a one-time purge of stale
  historical incident/call data instead of rebuilding old incidents.

## Tests

- Keep BVT fast and deterministic.
- Keep medium feature tests bounded and useful for scheduled/manual CI.
- Next useful additions:
  - deterministic fake-LLM incident extraction tests;
  - seeded dashboard API model/shape tests;
  - setup wizard temp-config validation tests;
  - targeted transcription queue behavior tests.

## Documentation

- Keep docs aligned with the simplified API/setup flow.
- Keep PizzaWave-only branding and avoid reintroducing old app names.
- Keep unsupported old-app history out of main docs; git history is enough.
- Keep LM Studio AI routing docs aligned with observed LM Link behavior.
- Preserve the custom RPI Qdrant build path if Qdrant is upgraded or the Pi is
  rebuilt with a 16 KB page-size kernel.

## Conditional / Not Active

- Do not redo the talkgroup migration unless a deployed stack was restored from
  a pre-migration backup or a TR catalog was replaced outside the PizzaWave
  settings flow.
- Do not re-add in-app historical import/reconciliation, transcription
  benchmark, rig-side transcription experiment APIs, or broad backfill
  machinery. Use explicit one-off offline procedures when historical recovery or
  model bakeoff work is needed.
- Do not compare relocated RPI findings against omicrontheta until both rigs
  monitor the same geography again.
- Do not patch trunk-recorder for single-control-channel locking unless that
  decision is made explicitly later.
