# Incident V3 Closure Status

Date: 2026-07-01

This document closes the current incident v3 tuning phase. The safety work is
worth keeping, but live v3 expansion should pause until transcription quality is
measured and improved. The Ooltewah Georgetown miss showed that weak or mangled
transcripts can erase the street and event facts that v3 needs before resolver
tuning can matter.

## Current Decision

- Merge the known-good v3 safety and simulation changes into `main`.
- Keep v3 shadow/scoring enabled.
- Keep the v3 executor in dry-run.
- Keep `allowLiveUpdateCurrent` disabled.
- Do not enable live `create_new` or `detach_create`.
- Do not start another v3 live trial until transcription quality has materially
  improved and the improved transcript path has been re-scored against real
  missed and near-missed incidents.

## Known-Good V3 Work

The current branch added server-side protections and scoring visibility around
v3 update plans:

- live mutation requires explicit runtime approval;
- `create_new` and `detach_create` remain outside live scope;
- update-current plans are blocked when they drop current membership;
- update-current plans are blocked when they replace fresh current membership;
- update-current plans are blocked when they add calls that accepted legacy
  audit already rejected;
- new handoff calls are not allowed to become unsafe current-incident updates;
- the scorer classifies update risks, create classifications, and drop reasons;
- executor validation simulation records whether dry-run update candidates would
  be accepted or rejected by the live executor boundary.

This is useful safety infrastructure. It should stay.

## Evidence At Closure

Latest executor-simulation monitor artifact:

`artifacts/incident-v3-plan-shadow/20260701T202608Z-executor-simulation-monitor/incident-v3-executor-validation-simulation-report.md`

Latest score artifacts from that run:

- OT:
  `artifacts/incident-v3-plan-shadow/20260701T202608Z-executor-simulation-monitor/ot/incident-v3-plan-shadow-score-20260701T202750Z.json`
- RPI:
  `artifacts/incident-v3-plan-shadow/20260701T202608Z-executor-simulation-monitor/rpi/incident-v3-plan-shadow-score-20260701T202749Z.json`

Observed state:

- no accepted `v3_update_current` audit rows appeared while dry-run was active;
- runtime flags remained in the intended safety state;
- the first post-simulation RPI `update_current` candidate was rejected by
  executor validation simulation because it would have added a call previously
  excluded by accepted legacy audit;
- no accepted unsafe v3 membership mutation was observed.

That is enough to preserve the code, but not enough to justify another live
trial.

## Why V3 Live Work Is Paused

The current limiting problem is not another resolver special case. The stronger
signal is transcription quality.

The Ooltewah Georgetown closure miss had multiple mangled transcripts, including
forms like `Oodlewall Georgetown`, `woodwall Georgetown`, and phrases where the
closure itself lost the usable street anchor. With that input, v3 can be safe and
still fail to connect or report the incident. More resolver tuning risks fitting
around bad text instead of improving the evidence that every downstream stage
uses.

Treat transcription quality as a separate prerequisite, not as a v3 patch item.

## Work Remaining After Transcription Improves

Before considering another live v3 trial:

1. Run a transcription bakeoff on real PizzaWave audio, including missed,
   weakly reported, and correctly reported incidents.
2. Score transcripts by operational facts, not just word error rate:
   location/street, unit/address, event type, closure/blockage status, injury or
   hazard details, and continuation cues.
3. Re-run incident v3 shadow scoring against the improved transcript path.
4. Confirm executor validation simulation rejects unsafe `update_current`
   candidates over a meaningful window.
5. Confirm no simulation-accepted candidate drops current calls, replaces fresh
   current membership, or adds calls that accepted legacy audit rejected.
6. Only then consider a human-approved narrow live retry for `update_current`.
   Keep `create_new` and `detach_create` blocked until separately justified.

## Recommended Next Step

Start a separate transcription-quality task. Use the existing bakeoff workflow to
compare the current provider against stronger local/offloaded options on real
call audio, with the Ooltewah Georgetown miss included as a required case.
