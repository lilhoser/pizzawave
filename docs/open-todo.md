# PizzaWave Open TODO

## Immediate Handoff Tasks

- Start the next Codex session rooted at `C:\projects\pizzawave`.
- Pull/checkout branch `codex/pizzawave-engine-cleanbreak`.
- Confirm latest checkpoint:

```powershell
git log -1 --oneline
```

Expected checkpoint: the latest pushed handoff/status commit on
`codex/pizzawave-engine-cleanbreak`.

- Confirm solution projects are only `pizzad` and `pizzad.Tests`.
- Re-run:

```powershell
npm run build --prefix C:\projects\pizzawave\pizzad\web
dotnet test C:\projects\pizzawave\pizzawave.sln --configuration Release
dotnet build C:\projects\pizzawave\pizzawave.sln --configuration Release
```

- The hard cleanup and v1 incident/verifier/dashboard-map checkpoints have
  already been committed. Do not redo the talkgroup migration unless a deployed
  stack was restored from a pre-migration backup.

## Near-Term Operational Follow-Ups

- When running operational follow-ups, audit both deployed rigs with the same
  checklist unless a rig does not carry that site/system:
  - current service/runtime health;
  - transcription queue health and throughput;
  - 24h transcription quality snapshot, including reason breakdown, top problem
    talkgroups, and whether non-OK calls reached incidents or insight events;
  - dashboard 200/latency checks;
  - `pizzad` warnings, especially `Transcript post-processing failed`;
  - 24h RF/decode analysis by system, including CC summary decode rate,
    CC-zero rate, low-decode warnings, retunes, no-transmission outcomes, and
    recorder exhaustion;
  - previous equal-window comparison for RF/decode metrics when available.

- Watch RPI transcription quality after the faster-whisper rollout.
  - Initial audit completed 2026-05-14: faster-whisper kept up with live traffic
    and reduced headline poor-quality rate versus the equal-length pre-rollout
    window.
  - Added `repetitive` and `numeric_noise` quality reasons after the audit found
    repeated-token hallucinations being marked complete.
  - Continue watching whether the new quality reasons reduce bad inputs to
    summaries/incidents without over-filtering useful short radio traffic.
  - Continue monitoring RPI Hamilton after increasing both RPI sources from 3
    to 5 `digitalRecorders` on 2026-05-15. Compare recorder exhaustion,
    no-transmission outcomes, queue pressure, CPU/load symptoms, and call
    capture against the pre-change 24h window before making another capacity
    change.
    - Initial quick check showed the recorder increase likely improved RF/call
      capture but pushed RPI beyond transcription capacity during peak Hamilton
      traffic: recorder exhaustion fell from roughly 47.6 per 100 calls in the
      prior same-hours window to about 12.1 per 100 calls, while captured audio
      rose to about 22.7k audio seconds in the 14:00-16:00 window and created a
      live transcription backlog. Decide whether to keep the recorder increase
      and add transcription capacity/filtering, or back off capture capacity if
      the RPI cannot sustain the added audio.
    - Test RPI transcription capacity changes cautiously. Candidate experiment:
      increase faster-whisper concurrency to 2 workers with lower per-worker
      thread count, then watch load average, swap, ingest/transcribed
      audio-seconds/minute, queue depth, and transcript quality before keeping
      the change.
  - Continue monitoring RPI Hamilton after the 2026-05-15 calibration change:
    both RPI sources were changed from gain 28 to gain 32, while existing errors
    were kept (`rtl=00000001` at -1600 Hz, `rtl=00000002` at 1200 Hz). Compare
    CC summary decode rate, CC-zero rate, low-decode warnings, retunes,
    no-transmission outcomes, and recorder exhaustion against the pre-change
    window before making another gain/error change.
  - Keep incident-quality analysis in the recurring quality/performance
    experiment rotation for both rigs:
    - sample recent dashboard incidents and inspect whether grouped call
      transcripts support the title/detail;
    - flag routine/status/admin/facility chatter that should remain an insight
      instead of a dashboard incident;
    - check whether OK-labeled transcripts are still semantically garbled enough
      to mislead incident generation;
    - for Hamilton-only traffic, compare RPI incidents against omicrontheta
      incidents in the same time window and note which incidents both rigs agree
      on, which only one rig detected, and where transcript quality changes the
      conclusion.
    - treat the RPI-vs-OT agreement check as a standing "do they agree?"
      exercise, not just an ad hoc investigation.
  - Run controlled RPI transcription bakeoffs when incident quality looks
    suspect. Compare live faster-whisper transcripts against Whisper.net tiny and
    base on the same recent calls, then decide whether faster-whisper model size
    or quality gates should change.
    - Run bakeoffs off-rig: fetch the selected call audio from the rig, run
      competing transcription models on a workstation or other non-production
      host, and compare those transcripts against the rig's stored output.
    - Initial 2026-05-15 bakeoff on 24 recent RPI incident-source calls showed
      Whisper.net base had fewer artifact/review outputs than Whisper.net tiny,
      but averaged about 22.6s per call versus 13.5s for tiny. Live
      faster-whisper tiny was comparable on clean calls but produced several
      semantically garbled OK transcripts, so the next transcription experiment
      should test a larger faster-whisper model or targeted retry/fallback gates
      rather than treating current OK status as sufficient incident evidence.

- Continue monitoring RPI queue health using:
  - pending audio seconds;
  - queue depth;
  - recent audio seconds ingested/transcribed;
  - calls/min.

- Watch RPI dashboard health after the 2026-05-14 post-transcription split.
  - `/api/v1/dashboard` was returning 200 in about 0.12-0.61s immediately after
    deploy.
  - Confirm the Live pill no longer reports intermittent 500s during sustained
    ingest/transcription load.
  - Check logs for `Transcript post-processing failed` warnings.

- Continue monitoring omicrontheta Hamilton retune/decode behavior after recent
  gain/config changes.
  - Use `System > Trunk Recorder > RF Analysis` after 48-72 hours of corrected
    post-change health rows.
  - Compare omicrontheta against RPI for whiteoakmt-hamilton using CC summary
    decode rate, low-decode warnings, retunes, and no-transmission outcomes.
  - CC stability/retune behavior affects both rigs and may be local signal/site
    related. Do not patch trunk-recorder for single-CC locking unless that
    decision is made explicitly later.
  - Include all omicrontheta systems in the same RF pass, especially
    whiteoakmt-cleveland recorder exhaustion and whiteoakmt-nbradley CC-zero and
    retune behavior.
  - Continue monitoring whiteoakmt-cleveland after increasing the OT source
    centered at 852.121875 MHz from 1 to 3 `digitalRecorders` on 2026-05-15.
    Compare recorder exhaustion, no-transmission outcomes, and call capture
    against the pre-change 24h window before making another capacity change.

- Revisit faster-whisper quality/performance only if RPI queue pressure returns.

- Watch omicrontheta transcription backlog after the 2026-05-14 deploy.
  - The service was healthy after deploy, but queue depth rose during downtime.
  - Let it drain before running large imports or manual AI backfills.

## Product/Code Follow-Ups

- Audit the full PizzaWave API surface.
  - Review every endpoint for operator value, runtime cost, write/delete risk,
    auth expectations, and whether it belongs on deployed rigs.
  - Remove or relocate endpoints that are only useful for experiments,
    development, or off-rig analysis.
  - Confirm expensive operational actions have clear guardrails and are not
    easy to trigger accidentally from the dashboard.

- Re-evaluate automatic recent import reconciliation before re-enabling it.
  - The startup-hosted recent 48h SFTP/local reconciliation was disabled after
    an OT deploy restart unexpectedly queued SFTP import job 244 on 2026-05-15.
  - If this feature returns, it needs explicit operator opt-in or stronger
    quiet-state gates: no active jobs, no pending transcription backlog, no
    live queue pressure, clear UI visibility, and safe deploy/restart behavior.
  - Also fix duplicate import behavior so re-importing an already-known live
    call cannot reset a completed transcript back to pending.

- Evaluate v1 LLM-managed incident generation.
  - Live incident generation now uses incremental site-local incident extraction
    instead of promoting summary events into incidents. It sends active incident
    state plus unassigned recent calls, lets the LLM update the incident list,
    and stores stable incident keys/status for partial and concluded incidents.
  - Validate prompt growth under real traffic. Watch candidate-call count,
    transcript truncation, payload size, model finish reasons, and whether the
    5-minute cadence keeps the UI responsive without silently waiting for a full
    60-minute window.
  - Monitor bounded carryover lossiness. Track how often unassigned eligible
    calls are dropped from LLM context before reaching the 60-minute state-window
    age, the average/min/max lifespan of calls in carryover, and whether high
    volume causes incidents to lose important context before they conclude.
    Alert/revisit caps if carryover lifespan gets materially shorter than the
    intended 60-minute incident memory.
  - Monitor evidence-verifier lossiness using `evidence_verifier_runs` on both
    rigs. Watch reviewed/model-reviewed call counts, truncated call counts,
    added/dropped/retained counts, verifier failures, and related `lm_usage`
    finish reasons. High truncated counts mean the verifier may still miss
    relevant source calls even when it repairs some LLM attribution mistakes.
    - Latest 2026-05-16 18:28 heartbeat: queues were healthy and AI was
      unblocked, but truncation remained high. OT had 420 verifier runs in the
      prior 3h, avg reviewed 40.8, avg truncated 32.7, max truncated 259, 887
      added, 147 dropped, avg retained 6.1, plus 48 incident-extraction
      truncations and 5 verifier truncations. RPI had 430 verifier runs, avg
      reviewed 47.6, avg truncated 77.7, max truncated 347, 1010 added, 16
      dropped, avg retained 4.9, plus 19 incident-extraction truncations and 1
      verifier truncation. Next engineering target should be prompt/output
      pressure, not queue capacity.
  - Check that incidents can span categories/talkgroups within a site, but do
    not merge unrelated county/site traffic.
  - Tighten incident title/detail conservatism. Recent examples still show LLM
    overreach from terse vehicle/plate/unit traffic, such as inferring stronger
    event labels than the evidence supports. Add prompt/validation rules that
    prefer neutral titles/details when calls only contain codes, plates, unit
    status, or partial location fragments.
  - Improve incident geocoding coverage. The dashboard now preserves all
    incident-linked geocoded call buckets and can use cached geocodes extracted
    from incident title/detail, but many incidents with clear addresses still do
    not map when call-level extraction is garbled and no usable geocode is
    cached. Add an explicit background geocoding/retry path for incident
    title/detail locations rather than doing live geocoding in the dashboard
    request path.
  - Confirm unassigned calls still remain useful in category raw/grouped-call
    views, and decide whether the category AI summaries view should stay visible
    now that summaries are no longer the promotion mechanism.
  - Keep using deterministic validation as a guardrail: require at least two
    source calls and concrete shared anchors before writing/updating dashboard
    incidents.
  - If v1 incident quality looks good after OT/RPI soak time, add a controlled
    incident backfill operation for recent history, potentially as far back as
    7 days. The backfill should rebuild incidents from stored calls/transcripts
    with explicit operator limits, prompt-size guardrails, and no rig-side
    transcription experiments.

- Monitor generic geocoding/location extraction after removing local geography
  assumptions.
  - Runtime no longer hard-codes Chattanooga-area places, local highway
    allowlists, `Tennessee`/`Georgia`, `countrycodes=us`, default
    Hamilton/Bradley/Cleveland areas, or fixed frontend map bounds.
  - Continue checking dashboard map rows for vague "heard as" values such as
    bare road suffixes/directions and for geocoder matches that are not
    justified by the transcript phrase.
  - Add a proper configured-area/bounds API to the dashboard if the operator
    still needs visible monitored-area boxes on the map; do not reintroduce
    hard-coded bounds in the frontend.
  - Consider a one-time broader cleanup/reprocess job for historical
    `call_locations` if old stale place-name rows continue to appear after the
    initial vague-location cleanup.

- Clean up and simplify the web UI.
  - The UI has accumulated experimental controls, diagnostics, and nice-to-have
    features.
  - Reorganize around the most important operator workflows and move secondary
    tools out of the primary path.

- Add unit and integration tests for the engine.
  - Basic tests now cover catalog import/generation, callstream parsing, and
    police-code detection.
  - Continue with transcription queue behavior, import guardrails, incident
    generation, settings validation, and dashboard API models.

- Fix calibration plan display for trunk-recorder source `error` values.
  - `SetupCalibrationService` currently reads source `error` through frequency
    normalization, so small Hz corrections such as `1200` can display as
    `1200000000` in `/api/v1/setup/calibration/plan`.

- Continue watching category/name drift after the 2026-05-14 deployed migration.
  - Final migration dry-runs showed zero remaining changes on omicrontheta and
    RPI after deploying the catalog-based ingest build.
  - Re-run the audit only if a stack is restored from an older backup or a TR
    catalog is replaced outside the PizzaWave settings flow.

- Add an operator-facing catalog drift diagnostic.
  - It should compare stored call category/name values against the current JSON
    catalog and report potential changes without mutating the DB.
  - This replaces the disposable migration script used during the 2026-05-14
    migration.

- Add an explicit one-time backfill path for stored transcript locations.
  - New calls now write `call_locations` during transcript post-processing.
  - Historical calls do not have `call_locations` unless they are reprocessed.
  - This should be an operator-triggered job or disposable script, not startup
    work.

- Consider separating geocoding into an even lower-priority queue if RPI load
  shows the current post-processing worker still competes with transcription.
  - The current implementation is already off the transcription worker path.
  - A dedicated geocode queue would make network geocode latency independently
    throttleable.

- Expand documentation as features stabilize.
  - Keep docs PizzaWave-first.
  - Do not reintroduce `PizzaStack` branding.
  - Keep old app history out of the main docs; unsupported app source is
    available from git history.
