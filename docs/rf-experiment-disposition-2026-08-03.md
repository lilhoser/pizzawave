# Trunk Recorder RF experiment disposition, 2026-08-03

This is a commit-by-commit audit against upstream `ebe770d`. The private
`lilhoser/trunk-recorder-paxan-experiments` repository remains an experiment
archive, not a PizzaWave distribution or permanent fork. None of the runtime
experiment commits below adds automated tests.

| Commits | Classification | Deployment and evidence | Default behavior | Disposition |
|---|---|---|---|---|
| `8318dfb`, `14d53c64`, `ca787409` | Telemetry only: structured control-channel retune events | Ancestor of current OT and RPI builds; isolated compile and live retune/reacquisition observations | Always emits telemetry; does not alter retuning | Possible standalone upstream candidate after schema review and automated retune-reason/result tests |
| `50b82fd5`, `8bcda833`, `e786f34` | Diagnostic tooling: triggered IQ flight recorder | Enabled on OT and RPI; coherent builds, calibration captures, natural captures, and replay evidence | Off unless a capture directory is set and `collapseCapture` is enabled | Review only as a separate diagnostic feature with lifecycle, file, failure, and resource tests |
| `f991d050`, `4965b804`, `e86300d`, `c4d8aef4` | General ownership correction inside the flight recorder | ASan and coherent-build evidence; deployed with the recorder | Has no effect without the recorder | Fold into a cleaned flight-recorder proposal; it is not independently useful |
| `6ae03faf`, `c923e02c`, `51920b1` | Diagnostic tooling: passive fixed-primary shadow decoder | Enabled on OT and RPI; replay and live event comparisons | Off unless `collapseShadow` is enabled | Possible opt-in diagnostic feature after resource, lifecycle, and API tests; not a stabilization fix |
| `313d2471`, `393a0732` | Diagnostic and behavioral experiment: paired-wide capture | Previously deployed and rolled back on both hosts; OT saw about 1 GiB use, decoder failures, source stalls, and 13 restarts | Effectively off unless capture and a wide target rate are enabled | Reject the current form; retain only as experiment evidence |
| `0f2cdb5b`, `3dc914ef` | Diagnostic hardening: timer quota rearm | Deployed as ancestors on OT and RPI; exposed restart capture storms | Inert when capture or quota reset is disabled | Superseded by persistent quota logic; do not submit alone |
| `7e03a80e`, `2f6ca268` | Reproducible bug fix within diagnostic tooling: persistent quota restoration | Active on OT and RPI; restored history after restart and prevented repeat captures | Inert when capture or quota reset is disabled | Consider only with the flight recorder and filesystem, clock, and malformed-file tests |
| `602a637c` | Behavioral experiment: configurable retune grace | OT 30-minute trial improved decode rate and retune churn, then was rolled back; not deployed on RPI | Zero preserves upstream behavior | Possible independent proposal only after deterministic threshold, timer, and retune-limit tests across sites |
| `11d0b568` | Diagnostic tooling: CQPSK loop overrides through environment variables | Never deployed; 27 offline runs on three Raymond captures | Defaults match upstream | Do not upstream environment hooks; keep in replay tooling or turn the study into tests |
| `1ecc8551`, `4bb829fd` | Behavioral experiment and possible general capability: separate control-channel modulation | RPI trial only, then rolled back; evidence across captures was inconsistent | Inherits traffic modulation by default | Not ready as a stabilization change; needs an independent use case plus configuration, graph rebuild, Phase 2, and ABI tests |
| `7f33d46` | Diagnostic telemetry: P25 pipeline counters | Active on OT; absent on RPI; depends on capture/shadow stack | Counters are emitted with that stack | Keep with a future diagnostic-feature proposal, not standalone product behavior |
| Provenance portion of `d64f6d3` | Telemetry and plugin ABI contract | Active on OT; absent on RPI | Adds fields unconditionally | Never submit this mixed commit. Track 1 replaces it with factual `started_from_update` only. |
| Source-affinity portion of `d64f6d3` | Behavioral experiment | Active on all three OT systems; absent on RPI; no documentation, tests, replay, or independent use case | Code default is false, but OT configuration explicitly enables it | Not upstream-ready. Define general multi-source semantics, failure policy, observability, and independent tests first. |
| `beff7394` | Private diagnostic documentation | No runtime deployment | No runtime effect | Keep private as archive documentation |

OT currently has `sourceAffinity=true` on Cleveland, North Bradley, and
Hamilton. North Bradley and Hamilton also have `collapseCapture=true` and
`collapseShadow=true`; Cleveland omits both keys. The global capture window is
30 seconds before and 60 seconds after the trigger, with one event and a
six-hour cooldown/reset. RPI has capture and shadow enabled with persistent
quota behavior, but no source affinity or separate control-channel modulation.

Because source affinity is active production behavior, removing or changing it
requires an explicit deployment and restart plan. Its presence in the current
binary is not evidence that the feature is accepted or upstream-ready.
