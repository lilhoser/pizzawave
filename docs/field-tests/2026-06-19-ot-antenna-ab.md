# 2026-06-19 OT And Mobile Yagi Baselines

Purpose: compare receive performance across the OT fixed-site baseline and the
portable RPI/mobile-yagi rig before making hardware decisions.

## Fresh Session Handoff

Start here in a new Codex session:

1. Work from `C:\projects\pizzawave-radio-setup-field-baselines` on branch
   `codex/radio-setup-field-baselines`.
2. Read `AGENTS.md`, then this file, then
   `docs/rf-path-survey-mode.md`.
3. Use PizzaWave API endpoints for the field baseline. Do not use old
   user-local RF diagnostic skills or standalone scripts as the primary path.
4. `OT-DISCONE-01` is complete. The remaining OT fixed-site antenna baselines
   are skipped for now.
5. Next setup path is the portable RPI/mobile-yagi rig. The operator will use
   Site Setup for a full setup first, then capture a
   30-minute baseline through the PizzaWave API.
6. Run `RPI-MOBILE-YAGI-RTLSDR-01` first with one RTL-SDR. Then switch only the
   SDR hardware to an Airspy Mini, rerun the full Site Setup flow,
   and collect `RPI-MOBILE-YAGI-AIRSPY-MINI-01`.

The local `pizzawave-tr-rf-diagnostics` Codex skill was intentionally removed
to avoid split-brain. The repo docs are the source of truth.

Rules for comparable OT runs:

- Use the same OT host, trunk-recorder config, SDR devices, gain/error settings, and monitored systems unless a run explicitly says otherwise.
- Use 30-minute windows.
- Change one RF-path variable at a time.
- Record the exact start/end time from the measurement output.
- Compare per-system control-channel decode, zero-decode samples, retunes, call counts, no-transmission, no-source, and recorder exhaustion.
- Treat transcription quality as secondary evidence after RF and call-capture metrics.

Rules for mobile RPI/yagi runs:

- Use Site Setup for the full setup before each baseline.
- Record RF-path metadata from Setup: antenna, bearing/aim, polarization,
  placement, coax, adapters, SDR model/serial, gain, error, sample rate, selected
  system/site, and source plan.
- Keep the yagi location, aim, coax, adapters, and host fixed between the
  RTL-SDR and Airspy Mini runs unless a run explicitly says otherwise.
- Change one hardware variable at a time: RTL-SDR versus Airspy Mini.
- A one-SDR mobile setup is not directly comparable to the OT fixed-site
  multi-SDR discone baseline. Compare it as a separate portable-rig baseline.
- If the selected system requires more RF bandwidth than one RTL-SDR can cover,
  record the Setup RF validation verdict as partial or coverage-limited instead of
  treating the failure as an antenna-only result.

## API Collection Procedure

Use the PizzaWave API on the active rig.

1. Discover systems:

```text
GET /api/v1/system/radio-setup/source-plan
```

Use the returned `systems[].shortName` values as the systems to compare.

2. At the start of each run, record `start` as Unix seconds.

3. Wait 30 minutes without changing hardware, RF path, TR config, or SDR
   settings.

4. Record `end` as Unix seconds.

5. For each system, collect:

```text
GET /api/v1/troubleshoot/rf-analysis?system=<shortName>&start=<start>&end=<end>
```

6. Collect the broader by-system troubleshooting snapshot:

```text
GET /api/v1/troubleshoot?start=<start>&end=<end>&bySystem=true
```

7. Optionally collect raw stored TR health samples:

```text
GET /api/v1/troubleshoot/tr-health?start=<start>&end=<end>
```

Paste the raw JSON or a compact, lossless summary under the matching run below.
If API authentication is enabled, use the PizzaWave bearer token from the rig's
configured token path.

Do not use the Setup RF validation `control_channel_quality` experiment as the 30-minute
A/B evidence source. It is capped at 900 seconds and is intended for shorter
interactive validation checks.

## Planned Runs

| Run ID | RF Path | Location | Cable | Status | Notes |
| --- | --- | --- | --- | --- | --- |
| OT-DISCONE-01 | Current discone antenna/current hardware path | OT fixed site | Current cabling | Complete | Baseline before antenna swap. |
| OT-YAGI-FIXED-01 | Yagi antenna | OT fixed site | Current cabling | Skipped for now | Operator switched plan to mobile RPI/yagi setup. |
| OT-OMNI-TUNED-01 | Tuned omni antenna | OT fixed site | Current cabling | Complete | Baseline after antenna swap. |
| OT-DISCONE-LMR400-01 | Current discone antenna/current hardware path | OT fixed site | LMR-400 | Deferred | Repeat only if time permits later. |
| OT-YAGI-FIXED-LMR400-01 | Yagi antenna | OT fixed site | LMR-400 | Deferred | Repeat only if time permits later. |
| OT-OMNI-TUNED-LMR400-01 | Tuned omni antenna | OT fixed site | LMR-400 | Deferred | Repeat only if time permits later. |
| RPI-MOBILE-YAGI-RTLSDR-01 | Mobile yagi rig with one RTL-SDR | Mobile RPI test location | 50 ft LMR-400 | Complete | One-SDR portable-yagi baseline. Control-channel decode was strong; voice coverage was limited by the RTL-SDR window. |
| RPI-MOBILE-YAGI-AIRSPY-MINI-01 | Mobile yagi rig with Airspy Mini | Same mobile RPI test location | Same as RTL-SDR run | Complete | One-Airspy portable-yagi baseline. Control-channel decode was strong for Chattanooga and Cleveland/Bradley; voice coverage and recorder capacity were the limiting factors. |
| RPI-MOBILE-YAGI-DUAL-AIRSPY-MINI-01 | Mobile yagi rig with two Airspy Minis | Same mobile RPI test location | Same as Airspy Mini run | Complete | Two-Airspy portable-yagi baseline. Chattanooga stayed strong, North Bradley was moderate, and Cleveland/Bradley control-channel decode was poor in this window. |
| RPI-MOBILE-YAGI-AIRSPY-R2-01 | Mobile yagi rig with one Airspy R2 | Same mobile RPI test location | Same as Airspy Mini run | Complete | One-Airspy-R2 portable-yagi baseline. Full selected-window voice coverage and strong control-channel decode, with recorder capacity as the limiting factor. |
| RPI-MOBILE-YAGI-AIRSPY-R2-EXTENDED-01 | Mobile yagi rig with one Airspy R2 | Same mobile RPI test location | Same as Airspy R2 run | Complete | Extended Airspy R2 stability window from the 30-minute baseline start through the following morning. RF decode remained strong; recorder capacity remained the limiting factor. |

## Run Output

Paste or attach each command output under the matching run. Keep raw output intact.

### OT-DISCONE-01

Status: Complete

```text
Run: OT-DISCONE-01
RF path: Current discone antenna/current hardware path
Host: OT / Omicrontheta PizzaWave API, http://192.168.1.173:8080
Started: 1781875457 / 2026-06-19T13:24:17Z / 2026-06-19 09:24:17 EDT
Ended:   1781877257 / 2026-06-19T13:54:17Z / 2026-06-19 09:54:17 EDT
Duration: 1800 seconds

API endpoints used:
- GET /api/v1/system/radio-setup/source-plan
- GET /api/v1/troubleshoot/rf-analysis?system=<shortName>&start=1781875457&end=1781877257
- GET /api/v1/troubleshoot?start=1781875457&end=1781877257&bySystem=true
- GET /api/v1/troubleshoot/tr-health?start=1781875457&end=1781877257

Source-plan systems:
- whiteoakmt-cleveland: qpsk, CC 851.050000/851.562500 MHz, source 2, gain 20, error 5113 Hz.
- whiteoakmt-nbradley: qpsk, CC 769.606250/770.531250 MHz, sources 0/1, gain 20, errors 4923/4638 Hz.
- whiteoakmt-hamilton: qpsk, CC 855.212500 MHz, sources 3/4, gain 15.7, errors 5881/5583 Hz.

RF-analysis results:
- whiteoakmt-cleveland:
  - window: 6/19/2026 9:24 AM - 6/19/2026 9:54 AM
  - summary: CC summary average 38.00 msg/sec, CC zero 0.0%, 500 CC message-rate samples, 0 retunes.
  - CC summary zero rate: 0.00%
  - message-rate zero rate: 0.00%
  - retunes: 0
  - no transmissions: 0
  - recorder exhausted: 2
  - health rows: 5
  - retune targets: none
- whiteoakmt-nbradley:
  - window: 6/19/2026 9:24 AM - 6/19/2026 9:54 AM
  - summary: CC summary average 0.00 msg/sec, CC zero 100.0%, 500 CC message-rate samples, 490 retunes.
  - CC summary zero rate: 100.00%
  - message-rate zero rate: 96.40%
  - retunes: 490
  - no transmissions: 0
  - recorder exhausted: 0
  - health rows: 5
  - retune targets: 769.606250 MHz=197, 770.531250 MHz=197, 772.381250 MHz=196
- whiteoakmt-hamilton:
  - window: 6/19/2026 9:24 AM - 6/19/2026 9:54 AM
  - summary: CC summary average 22.57 msg/sec, CC zero 0.0%, 500 CC message-rate samples, 32 retunes.
  - CC summary zero rate: 0.00%
  - message-rate zero rate: 4.80%
  - retunes: 32
  - no transmissions: 18
  - recorder exhausted: 0
  - health rows: 5
  - retune targets: 856.237500 MHz=9, 856.762500 MHz=9, 857.237500 MHz=9, 855.212500 MHz=9

Raw TR-health bucket aggregate:
- Rows: 20 total; 5 buckets each for global, whiteoakmt-cleveland, whiteoakmt-hamilton, whiteoakmt-nbradley.
- Bucket starts covered: 2026-06-19 09:25:00 EDT through 2026-06-19 09:45:00 EDT.
- The later 09:50-09:55 bucket was intentionally not used for this run record because it can overlap the immediate post-end antenna swap.
- global: ccSummaryLines=21, ccSummaryZero=7, ccSummaryRateTotal=424, lowWarningLines=1500, lowWarningZero=506, retunes=522, callsStarted=385, callsConcluded=382, updateNotGrant=204, noTxRecorded=18, recorderExhausted=2, sampleStops=0, unableSource=0, tuningErrSamples=382, tuningErrTotalAbsHz=282230, tuningErrMaxAbsHz=1368.
- whiteoakmt-cleveland: ccSummaryLines=7, ccSummaryZero=0, ccSummaryRateTotal=266, lowWarningLines=500, lowWarningZero=0, retunes=0, callsStarted=66, callsConcluded=65, updateNotGrant=2, noTxRecorded=0, recorderExhausted=2, sampleStops=0, unableSource=0, tuningErrSamples=65, tuningErrTotalAbsHz=44844, tuningErrMaxAbsHz=889.
- whiteoakmt-hamilton: ccSummaryLines=7, ccSummaryZero=0, ccSummaryRateTotal=158, lowWarningLines=500, lowWarningZero=24, retunes=32, callsStarted=318, callsConcluded=316, updateNotGrant=200, noTxRecorded=18, recorderExhausted=0, sampleStops=0, unableSource=0, tuningErrSamples=316, tuningErrTotalAbsHz=236873, tuningErrMaxAbsHz=1368.
- whiteoakmt-nbradley: ccSummaryLines=7, ccSummaryZero=7, ccSummaryRateTotal=0, lowWarningLines=500, lowWarningZero=482, retunes=490, callsStarted=1, callsConcluded=1, updateNotGrant=2, noTxRecorded=0, recorderExhausted=0, sampleStops=0, unableSource=0, tuningErrSamples=1, tuningErrTotalAbsHz=513, tuningErrMaxAbsHz=513.

Troubleshoot source coverage for selected window:
- source 0 rtl=00000003,buflen=65536: 769.315625-771.715625 MHz, firstMatchCalls=0, coverableCalls=0, uniqueFrequencies=0.
- source 1 rtl=00000002,buflen=65536: 771.718750-774.118750 MHz, firstMatchCalls=0, coverableCalls=0, uniqueFrequencies=0.
- source 2 rtl=00000001,buflen=65536: 850.925000-853.325000 MHz, firstMatchCalls=30, coverableCalls=30, uniqueFrequencies=3.
- source 3 rtl=00000004,buflen=65536: 854.012500-856.412500 MHz, firstMatchCalls=68, coverableCalls=68, uniqueFrequencies=4.
- source 4 rtl=00000005,buflen=65536: 856.400000-858.800000 MHz, firstMatchCalls=84, coverableCalls=84, uniqueFrequencies=6.

Notes:
- /api/v1/troubleshoot returned a health summary labeled "Window: last 24h"; use the fixed-window rf-analysis and tr-health rows above for A/B evidence.
- rf-analysis returned hasEnoughPostChangeData=false for all systems because its confidence guidance expects more than this 30-minute A/B window. That flag is not treated as a failed collection here.
```

### OT-YAGI-FIXED-01

Status: Skipped for now

```text
Skipped for now. Operator switched to portable RPI/mobile-yagi setup after OT-DISCONE-01.
```

### OT-OMNI-TUNED-01

Status: Complete

```text
Run: OT-OMNI-TUNED-01
RF path: Tuned omni antenna/current hardware path
Host: OT / Omicrontheta PizzaWave API, http://192.168.1.173:8080
Started: 1781987040 / 2026-06-20T20:24:00Z / 2026-06-20 16:24:00 EDT
Ended:   1781988840 / 2026-06-20T20:54:00Z / 2026-06-20 16:54:00 EDT
Duration: 1800 seconds

API endpoints used:
- GET /api/v1/system/radio-setup/source-plan
- GET /api/v1/troubleshoot/rf-analysis?system=<shortName>&start=1781987040&end=1781988840
- GET /api/v1/troubleshoot?start=1781987040&end=1781988840&bySystem=true
- GET /api/v1/troubleshoot/tr-health?start=1781987040&end=1781988840

Source-plan systems:
- whiteoakmt-cleveland: qpsk, CC 851.050000/851.562500 MHz, source 2, gain 20, error 5113 Hz.
- whiteoakmt-nbradley: qpsk, CC 769.606250/770.531250 MHz, sources 0/1, gain 20, errors 4923/4638 Hz.
- whiteoakmt-hamilton: qpsk, CC 855.212500 MHz, sources 3/4, gain 15.7, errors 5881/5583 Hz.

RF-analysis results:
- whiteoakmt-cleveland:
  - window: 6/20/2026 4:24 PM - 6/20/2026 4:54 PM
  - summary: CC summary average 40.14 msg/sec, CC zero 0.0%, 490 CC message-rate samples, 0 retunes.
  - CC summary zero rate: 0.00%
  - message-rate zero rate: 0.00%
  - retunes: 0
  - no transmissions: 2
  - recorder exhausted: 0
  - health rows: 5
  - retune targets: none
- whiteoakmt-nbradley:
  - window: 6/20/2026 4:24 PM - 6/20/2026 4:54 PM
  - summary: CC summary average 28.43 msg/sec, CC zero 0.0%, 490 CC message-rate samples, 3 retunes.
  - CC summary zero rate: 0.00%
  - message-rate zero rate: 0.41%
  - retunes: 3
  - no transmissions: 18
  - recorder exhausted: 7
  - health rows: 5
  - retune targets: 770.531250 MHz=1, 772.381250 MHz=1, 769.606250 MHz=1
- whiteoakmt-hamilton:
  - window: 6/20/2026 4:24 PM - 6/20/2026 4:54 PM
  - summary: CC summary average 25.57 msg/sec, CC zero 0.0%, 490 CC message-rate samples, 6 retunes.
  - CC summary zero rate: 0.00%
  - message-rate zero rate: 1.22%
  - retunes: 6
  - no transmissions: 8
  - recorder exhausted: 0
  - health rows: 5
  - retune targets: 857.237500 MHz=2, 856.237500 MHz=2, 856.762500 MHz=1, 855.212500 MHz=1

Raw TR-health bucket aggregate:
- Rows: 20 total; 5 buckets each for global, whiteoakmt-cleveland, whiteoakmt-hamilton, whiteoakmt-nbradley.
- Bucket starts covered: 2026-06-20 16:25:00 EDT through 2026-06-20 16:45:00 EDT.
- global: ccSummaryLines=21, ccSummaryZero=0, ccSummaryRateTotal=659, ccSummaryAverage=31.38, lowWarningLines=1470, lowWarningZero=8, retunes=9, callsStarted=369, callsConcluded=364, updateNotGrant=83, noTxRecorded=28, recorderExhausted=7, sampleStops=2, unableSource=1, tuningErrSamples=364, tuningErrTotalAbsHz=267282, tuningErrMaxAbsHz=1280.
- whiteoakmt-cleveland: ccSummaryLines=7, ccSummaryZero=0, ccSummaryRateTotal=281, ccSummaryAverage=40.14, lowWarningLines=490, lowWarningZero=0, retunes=0, callsStarted=36, callsConcluded=36, updateNotGrant=0, noTxRecorded=2, recorderExhausted=0, sampleStops=0, unableSource=0, tuningErrSamples=36, tuningErrTotalAbsHz=26223, tuningErrMaxAbsHz=876.
- whiteoakmt-hamilton: ccSummaryLines=7, ccSummaryZero=0, ccSummaryRateTotal=179, ccSummaryAverage=25.57, lowWarningLines=490, lowWarningZero=6, retunes=6, callsStarted=293, callsConcluded=288, updateNotGrant=81, noTxRecorded=8, recorderExhausted=0, sampleStops=0, unableSource=1, tuningErrSamples=288, tuningErrTotalAbsHz=219440, tuningErrMaxAbsHz=1280.
- whiteoakmt-nbradley: ccSummaryLines=7, ccSummaryZero=0, ccSummaryRateTotal=199, ccSummaryAverage=28.43, lowWarningLines=490, lowWarningZero=2, retunes=3, callsStarted=40, callsConcluded=40, updateNotGrant=2, noTxRecorded=18, recorderExhausted=7, sampleStops=0, unableSource=0, tuningErrSamples=40, tuningErrTotalAbsHz=21619, tuningErrMaxAbsHz=1093.

Troubleshoot source coverage for selected window:
- source 0 rtl=00000003,buflen=65536: center 770.515625 MHz, firstMatchCalls=5, coverableCalls=5, uniqueFrequencies=1.
- source 1 rtl=00000002,buflen=65536: center 772.918750 MHz, firstMatchCalls=4, coverableCalls=4, uniqueFrequencies=2.
- source 2 rtl=00000001,buflen=65536: center 852.125000 MHz, firstMatchCalls=28, coverableCalls=28, uniqueFrequencies=3.
- source 3 rtl=00000004,buflen=65536: center 855.212500 MHz, firstMatchCalls=76, coverableCalls=76, uniqueFrequencies=4.
- source 4 rtl=00000005,buflen=65536: center 857.600000 MHz, firstMatchCalls=89, coverableCalls=89, uniqueFrequencies=5.

Notes:
- Compared with OT-DISCONE-01, N Bradley changed from unusable to usable in this first 30-minute window: CC average 0.00 to 28.43 msg/sec, CC zero 100.0% to 0.0%, retunes 490 to 3, and calls concluded 1 to 40.
- Hamilton improved: CC average 22.57 to 25.57 msg/sec, retunes 32 to 6, no transmissions 18 to 8, and update-not-grant 200 to 81.
- Cleveland remained strong: CC average 38.00 to 40.14 msg/sec and retunes stayed at 0.
- Global CC zero buckets disappeared: ccSummaryZero 7 to 0, ccSummaryRateTotal 424 to 659, and retunes 522 to 9.
- N Bradley recorder exhaustion increased from 0 to 7 because it is now receiving traffic. Treat this as a capacity/source-plan follow-up, not an antenna failure.
```

### Optional LMR-400 Runs

Status: Deferred

```text
Deferred. Do not run until explicitly resumed.
```

### RPI-MOBILE-YAGI-RTLSDR-01

Status: Complete

```text
Run: RPI-MOBILE-YAGI-RTLSDR-01
RF path: Mobile yagi rig with one RTL-SDR
Host: RPI PizzaWave API, http://100.105.110.92:8080/
Setup RF validation session: rf-20260619190023-17be178afd6
Started: 1781992965 / 2026-06-20T22:02:45Z / 2026-06-20 18:02:45 EDT
Ended:   1781994765 / 2026-06-20T22:32:45Z / 2026-06-20 18:32:45 EDT
Duration: 1800 seconds

API endpoints used:
- GET /api/v1/system/radio-setup/source-plan
- GET /api/v1/system/radio-setup/rf-20260619190023-17be178afd6
- GET /api/v1/troubleshoot/rf-analysis?system=chattanooga-simulcast-hamilton-t&start=1781992965&end=1781994765
- GET /api/v1/troubleshoot?start=1781992965&end=1781994765&bySystem=true
- GET /api/v1/troubleshoot/tr-health?start=1781992965&end=1781994765
- GET /api/v1/status?start=1781992965&end=1781994765

Source plan and RF path:
- System/site: chattanooga-simulcast-hamilton-t, qpsk.
- Control channels: 855.212500, 856.237500, 856.762500, 857.237500 MHz.
- Observed/configured voice frequencies used by source plan: 855.987500, 856.087500, 856.762500, 857.237500 MHz.
- Required range: 855.212500-857.237500 MHz, recommended center 856.225000 MHz.
- Source 0: rtl=00000002,buflen=65536, RTL-SDR serial 00000002, center 856.225000 MHz, sample rate 2.400000 MS/s, gain 28, error 0 Hz, digitalRecorders=4.
- Source window from troubleshoot: 855.025000-857.425000 MHz.
- RF path metadata from workspace: Yagi antenna, 11 dB gain, N out; 50 ft LMR-400, N to BNC; SDR input SMA.

RF-analysis result:
- chattanooga-simulcast-hamilton-t:
  - window: 6/20/2026 6:02 PM - 6/20/2026 6:32 PM
  - summary: CC summary average 39.50 msg/sec, CC zero 0.0%, 500 CC message-rate samples, 0 retunes.
  - CC summary zero rate: 0.00%
  - message-rate zero rate: 0.00%
  - retunes: 0
  - no transmissions: 2
  - recorder exhausted: 0
  - health rows: 5
  - retune targets: none

Raw TR-health bucket aggregate:
- Rows: 10 total; 5 buckets each for global and chattanooga-simulcast-hamilton-t.
- Bucket starts covered: 2026-06-20 18:05:00 EDT through 2026-06-20 18:25:00 EDT.
- global: ccSummaryLines=8, ccSummaryZero=0, ccSummaryRateTotal=316, ccSummaryAverage=39.50, lowWarningLines=500, lowWarningZero=0, retunes=0, callsStarted=117, callsConcluded=117, updateNotGrant=1, noTxRecorded=2, recorderExhausted=0, sampleStops=0, unableSource=397, tuningErrSamples=117, tuningErrTotalAbsHz=98071, tuningErrMaxAbsHz=1011, trCpuMax=25.8%, trRssMax=138.94 MB, hostTempMax=56.75 C.
- chattanooga-simulcast-hamilton-t: ccSummaryLines=8, ccSummaryZero=0, ccSummaryRateTotal=316, ccSummaryAverage=39.50, lowWarningLines=500, lowWarningZero=0, retunes=0, callsStarted=117, callsConcluded=117, updateNotGrant=1, noTxRecorded=2, recorderExhausted=0, sampleStops=0, unableSource=397, tuningErrSamples=117, tuningErrTotalAbsHz=98071, tuningErrMaxAbsHz=1011.

Troubleshoot source coverage for selected window:
- source 0 rtl=00000002,buflen=65536: 855.025000-857.425000 MHz, firstMatchCalls=66, coverableCalls=66, uniqueFrequencies=3.
- TR also logged 397 no-source outcomes in the health buckets. Recent examples included voice grants at 854.387500, 854.912500, 857.437500, 857.487500, 857.712500, 857.937500, and 858.237500 MHz, which fall outside the one RTL-SDR source window.

Traffic/status:
- /api/v1/status for the fixed window returned calls=66, incidents=0, alerts=0, tokens=19210.

Notes:
- This is a portable-rig baseline, not a same-site comparison against OT-DISCONE-01.
- Control-channel decode was strong and stable across the fixed window: 39.50 msg/sec average, 0.0% zero decode, and 0 retunes.
- The one-RTL source plan is coverage-limited for this site. It reliably covers the selected control channel and some voice traffic, but many voice grants fall outside the configured 2.4 MS/s window.
- Treat the no-source count as an expected one-SDR RTL-SDR limitation for this site, not as an antenna-only failure.
```

### RPI-MOBILE-YAGI-AIRSPY-MINI-01

Status: Complete

```text
Run: RPI-MOBILE-YAGI-AIRSPY-MINI-01
RF path: Mobile yagi rig with Airspy Mini
Host: RPI PizzaWave API, http://100.105.110.92:8080/
Setup RF validation session: rf-20260621130654-dc2eae36859
Started: 1782161922 / 2026-06-22T20:58:42Z / 2026-06-22 16:58:42 EDT
Ended:   1782163722 / 2026-06-22T21:28:42Z / 2026-06-22 17:28:42 EDT
Duration: 1800 seconds

API endpoints used:
- GET /api/v1/system/radio-setup/source-plan
- GET /api/v1/system/radio-setup
- GET /api/v1/troubleshoot/rf-analysis?system=chattanooga-simulcast-hamilton-t&start=1782161922&end=1782163722
- GET /api/v1/troubleshoot/rf-analysis?system=cleveland-bradley-tn&start=1782161922&end=1782163722
- GET /api/v1/troubleshoot?start=1782161922&end=1782163722&bySystem=true
- GET /api/v1/troubleshoot/tr-health?start=1782161922&end=1782163722
- GET /api/v1/status?start=1782161922&end=1782163722

Raw API artifacts:
- rpi-mobile-yagi-airspy-mini-source-plan-1782161922-1782163722.json
- rpi-mobile-yagi-airspy-mini-rf-analysis-chattanooga-1782161922-1782163722.json
- rpi-mobile-yagi-airspy-mini-rf-analysis-cleveland-1782161922-1782163722.json
- rpi-mobile-yagi-airspy-mini-troubleshoot-1782161922-1782163722.json
- rpi-mobile-yagi-airspy-mini-troubleshoot-postfix-1782161922-1782163722.json
- rpi-mobile-yagi-airspy-mini-tr-health-1782161922-1782163722.json
- rpi-mobile-yagi-airspy-mini-status-1782161922-1782163722.json

Source plan and RF path:
- Setup RF validation session rf-20260621130654-dc2eae36859: draft, guided, site label airspy-test-1, system chattanooga-simulcast-hamilton-t, source plan applied 1 source definition.
- Source 0: airspy=637862DC2E3A19D7, Airspy serial 637862DC2E3A19D7, center 853.131250 MHz, sample rate 6.000000 MS/s, gain 15, error 0 Hz.
- Source window from troubleshoot: 850.131250-856.131250 MHz, digitalRecorders=4.
- Source-plan systems:
  - chattanooga-simulcast-hamilton-t: qpsk, CC 855.212500 MHz, observed voice range 854.387500-858.437500 MHz, proposed source 0. The endpoint warns that TR config has no voice channel list and calibration is using observed call frequencies.
  - cleveland-bradley-tn: qpsk, CC 851.050000 MHz, observed voice range 851.300000-853.200000 MHz, proposed source 0. The endpoint warns that TR config has no voice channel list and calibration is using observed call frequencies.

RF-analysis results:
- chattanooga-simulcast-hamilton-t:
  - window: 6/22/2026 4:58 PM - 6/22/2026 5:28 PM
  - summary: CC summary average 39.00 msg/sec, CC zero 0.0%, 400 CC message-rate samples, 0 retunes.
  - CC summary zero rate: 0.00%
  - message-rate zero rate: 0.00%
  - retunes: 0
  - no transmissions: 0
  - recorder exhausted: 34
  - health rows: 4 in the first RF-analysis response; raw TR-health later included 5 stored buckets.
  - retune targets: none
- cleveland-bradley-tn:
  - window: 6/22/2026 4:58 PM - 6/22/2026 5:28 PM
  - summary: CC summary average 37.00 msg/sec, CC zero 0.0%, 400 CC message-rate samples, 2 retunes.
  - CC summary zero rate: 0.00%
  - message-rate zero rate: 0.25%
  - retunes: 2
  - no transmissions: 6
  - recorder exhausted: 16
  - health rows: 4 in the first RF-analysis response; raw TR-health later included 5 stored buckets.
  - retune targets: 851.562500 MHz=1, 851.050000 MHz=1

Raw TR-health bucket aggregate:
- Rows: 15 total; 5 buckets each for global, chattanooga-simulcast-hamilton-t, and cleveland-bradley-tn.
- Bucket starts covered: 2026-06-22 17:00:00 EDT through 2026-06-22 17:20:00 EDT.
- global: ccSummaryLines=16, ccSummaryZero=0, ccSummaryRateTotal=608, ccSummaryAverage=38.00, lowWarningLines=1000, lowWarningZero=1, retunes=2, callsStarted=285, callsConcluded=284, updateNotGrant=22, noTxRecorded=6, recorderExhausted=56, sampleStops=0, unableSource=437, tuningErrSamples=284, tuningErrTotalAbsHz=560792, tuningErrMaxAbsHz=3061, trCpuMax=108%, trRssMax=199.97 MB, hostTempMax=61.15 C.
- chattanooga-simulcast-hamilton-t: ccSummaryLines=8, ccSummaryZero=0, ccSummaryRateTotal=312, ccSummaryAverage=39.00, lowWarningLines=500, lowWarningZero=0, retunes=0, callsStarted=154, callsConcluded=153, updateNotGrant=0, noTxRecorded=0, recorderExhausted=36, sampleStops=0, unableSource=437, tuningErrSamples=153, tuningErrTotalAbsHz=292727, tuningErrMaxAbsHz=2781.
- cleveland-bradley-tn: ccSummaryLines=8, ccSummaryZero=0, ccSummaryRateTotal=296, ccSummaryAverage=37.00, lowWarningLines=500, lowWarningZero=1, retunes=2, callsStarted=131, callsConcluded=131, updateNotGrant=22, noTxRecorded=6, recorderExhausted=20, sampleStops=0, unableSource=0, tuningErrSamples=131, tuningErrTotalAbsHz=268065, tuningErrMaxAbsHz=3061.

Troubleshoot source coverage for selected window:
- source 0 airspy=637862DC2E3A19D7: 850.131250-856.131250 MHz, firstMatchCalls=155, coverableCalls=155, uniqueFrequencies=7.
- TR logged 437 no-source outcomes in the stored health buckets, all attributed to chattanooga-simulcast-hamilton-t.
- Recent no-source examples in the troubleshooting payload included 856.087500, 856.762500, 857.237500, 857.437500, 857.487500, 857.712500, 857.937500, 858.237500, and 858.437500 MHz.

Traffic/status:
- /api/v1/status for the fixed window returned calls=155, incidents=1, alerts=0, tokens=49107.

Notes:
- This is a portable-rig baseline, not a same-site comparison against OT-DISCONE-01.
- Control-channel decode was strong and stable for both systems: Chattanooga averaged 39.00 msg/sec with 0 retunes; Cleveland/Bradley averaged 37.00 msg/sec with 2 retunes.
- Compared with the one-RTL mobile yagi run, the Airspy source can monitor both Chattanooga and Cleveland/Bradley control channels in one source, but the configured 6 MS/s source window still does not cover the high Chattanooga voice channels above 856.131250 MHz.
- Recorder capacity was a clear bottleneck with both systems active on one source: global recorderExhausted=56 across the stored buckets.
- The initial troubleshoot source-plan summary under-reported Chattanooga's observed voice range because it only considered calls inside the selected 30-minute window. This was fixed after the run: post-fix troubleshoot output reports Chattanooga 854.387500-858.437500 MHz as only partially covered by source 0, matching the no-source logs and the Setup source-plan endpoint.
```

### RPI-MOBILE-YAGI-DUAL-AIRSPY-MINI-01

Status: Complete

```text
Run: RPI-MOBILE-YAGI-DUAL-AIRSPY-MINI-01
RF path: Mobile yagi rig with two Airspy Minis
Host: RPI PizzaWave API, http://100.105.110.92:8080/
Setup RF validation session: rf-20260623125453-f5655f48c99
Started: 1782228246 / 2026-06-23T15:24:06Z / 2026-06-23 11:24:06 EDT
Ended:   1782230046 / 2026-06-23T15:54:06Z / 2026-06-23 11:54:06 EDT
Duration: 1800 seconds

API endpoints used:
- GET /api/v1/system/radio-setup/source-plan
- GET /api/v1/system/radio-setup
- GET /api/v1/troubleshoot/rf-analysis?system=chattanooga-simulcast-hamilton-t&start=1782228246&end=1782230046
- GET /api/v1/troubleshoot/rf-analysis?system=cleveland-bradley-tn&start=1782228246&end=1782230046
- GET /api/v1/troubleshoot/rf-analysis?system=north-bradley-bradley-tn&start=1782228246&end=1782230046
- GET /api/v1/troubleshoot?start=1782228246&end=1782230046&bySystem=true
- GET /api/v1/troubleshoot/tr-health?start=1782228246&end=1782230046
- GET /api/v1/status?start=1782228246&end=1782230046

Raw API artifacts:
- rpi-mobile-yagi-dual-airspy-mini-source-plan-1782228246-1782230046.json
- rpi-mobile-yagi-dual-airspy-mini-radio-setup-1782228246-1782230046.json
- rpi-mobile-yagi-dual-airspy-mini-rf-analysis-chattanooga-1782228246-1782230046.json
- rpi-mobile-yagi-dual-airspy-mini-rf-analysis-cleveland-1782228246-1782230046.json
- rpi-mobile-yagi-dual-airspy-mini-rf-analysis-north-bradley-1782228246-1782230046.json
- rpi-mobile-yagi-dual-airspy-mini-troubleshoot-1782228246-1782230046.json
- rpi-mobile-yagi-dual-airspy-mini-tr-health-1782228246-1782230046.json
- rpi-mobile-yagi-dual-airspy-mini-status-1782228246-1782230046.json

Source plan and RF path:
- Setup RF validation session rf-20260623125453-f5655f48c99: source_plan_applied, guided, site label two-airspy-minis, source plan applied 2 SDR source windows for 3 systems.
- Source 0: airspy=637862DC2E3A19D7, center 769.606250 MHz, sample rate 6.000000 MS/s, gain 15, error 0 Hz, covered systems: north-bradley-bradley-tn.
- Source 1: airspy=637862DC2F5C0CD7, center 853.131250 MHz, sample rate 6.000000 MS/s, gain 15, error 0 Hz, covered systems: chattanooga-simulcast-hamilton-t and cleveland-bradley-tn.
- Source windows from troubleshoot:
  - source 0: 766.606250-772.606250 MHz, digitalRecorders=4, firstMatchCalls=31, coverableCalls=31, uniqueFrequencies=3.
  - source 1: 850.131250-856.131250 MHz, digitalRecorders=4, firstMatchCalls=72, coverableCalls=72, uniqueFrequencies=7.
- Source-plan systems:
  - chattanooga-simulcast-hamilton-t: qpsk, CC 855.212500 MHz, observed voice range 854.387500-858.437500 MHz, proposed source 1, partially covered.
  - cleveland-bradley-tn: qpsk, CC 851.050000 MHz, observed voice range 851.300000-853.200000 MHz, proposed source 1, covered.
  - north-bradley-bradley-tn: qpsk, CC 769.606250 MHz, observed voice range 770.218750-771.981250 MHz, proposed source 0, covered.

RF-analysis results:
- chattanooga-simulcast-hamilton-t:
  - window: 6/23/2026 11:24 AM - 6/23/2026 11:54 AM
  - summary: CC summary average 39.12 msg/sec, CC zero 0.0%, 500 CC message-rate samples, 0 retunes.
  - CC summary zero rate: 0.00%
  - message-rate zero rate: 0.00%
  - retunes: 0
  - no transmissions: 20
  - recorder exhausted: 0
  - health rows: 5
  - retune targets: none
- cleveland-bradley-tn:
  - window: 6/23/2026 11:24 AM - 6/23/2026 11:54 AM
  - summary: CC summary average 4.62 msg/sec, CC zero 25.0%, 500 CC message-rate samples, 219 retunes.
  - CC summary zero rate: 25.00%
  - message-rate zero rate: 37.20%
  - retunes: 219
  - no transmissions: 8
  - recorder exhausted: 0
  - health rows: 5
  - retune targets: 851.562500 MHz=147, 851.050000 MHz=146
- north-bradley-bradley-tn:
  - window: 6/23/2026 11:24 AM - 6/23/2026 11:54 AM
  - summary: CC summary average 16.62 msg/sec, CC zero 12.5%, 500 CC message-rate samples, 12 retunes.
  - CC summary zero rate: 12.50%
  - message-rate zero rate: 2.00%
  - retunes: 12
  - no transmissions: 30
  - recorder exhausted: 0
  - health rows: 5
  - retune targets: 772.381250 MHz=4, 770.531250 MHz=4, 769.606250 MHz=4

Raw TR-health bucket aggregate:
- Rows: 20 total; 5 buckets each for global, chattanooga-simulcast-hamilton-t, cleveland-bradley-tn, and north-bradley-bradley-tn.
- Bucket starts covered: 2026-06-23 11:25:00 EDT through 2026-06-23 11:45:00 EDT.
- global: ccSummaryLines=24, ccSummaryZero=3, ccSummaryRateTotal=483, ccSummaryAverage=20.12, lowWarningLines=1500, lowWarningZero=196, retunes=231, callsStarted=258, callsConcluded=256, updateNotGrant=50, noTxRecorded=58, recorderExhausted=0, sampleStops=0, unableSource=417, tuningErrSamples=256, tuningErrTotalAbsHz=505067, tuningErrMaxAbsHz=3129, trCpuMax=150%, trRssMax=258.58 MB, hostTempMax=66.65 C.
- chattanooga-simulcast-hamilton-t: ccSummaryLines=8, ccSummaryZero=0, ccSummaryRateTotal=313, ccSummaryAverage=39.12, lowWarningLines=500, lowWarningZero=0, retunes=0, callsStarted=153, callsConcluded=151, updateNotGrant=0, noTxRecorded=20, recorderExhausted=0, sampleStops=0, unableSource=387, tuningErrSamples=151, tuningErrTotalAbsHz=297357, tuningErrMaxAbsHz=3051.
- cleveland-bradley-tn: ccSummaryLines=8, ccSummaryZero=2, ccSummaryRateTotal=37, ccSummaryAverage=4.62, lowWarningLines=500, lowWarningZero=186, retunes=219, callsStarted=21, callsConcluded=21, updateNotGrant=24, noTxRecorded=8, recorderExhausted=0, sampleStops=0, unableSource=0, tuningErrSamples=21, tuningErrTotalAbsHz=53309, tuningErrMaxAbsHz=3129.
- north-bradley-bradley-tn: ccSummaryLines=8, ccSummaryZero=1, ccSummaryRateTotal=133, ccSummaryAverage=16.62, lowWarningLines=500, lowWarningZero=10, retunes=12, callsStarted=84, callsConcluded=84, updateNotGrant=26, noTxRecorded=30, recorderExhausted=0, sampleStops=0, unableSource=30, tuningErrSamples=84, tuningErrTotalAbsHz=154401, tuningErrMaxAbsHz=2617.

Troubleshoot source coverage for selected window:
- source 0 airspy=637862DC2E3A19D7: 766.606250-772.606250 MHz, firstMatchCalls=31, coverableCalls=31, uniqueFrequencies=3.
- source 1 airspy=637862DC2F5C0CD7: 850.131250-856.131250 MHz, firstMatchCalls=72, coverableCalls=72, uniqueFrequencies=7.
- TR logged 417 no-source outcomes in the stored health buckets, mostly from Chattanooga high voice channels outside source 1's configured window.
- Recent no-source examples in the troubleshooting payload included 856.087500, 857.437500, 857.487500, 857.712500, 857.937500, and 858.237500 MHz.

Traffic/status:
- /api/v1/status for the fixed window returned calls=103, incidents=2, alerts=0, tokens=34284.

Notes:
- This is a portable-rig baseline, not a same-site comparison against OT-DISCONE-01.
- Chattanooga control-channel decode remained strong: 39.12 msg/sec average, 0.0% zero decode, and 0 retunes.
- Cleveland/Bradley control-channel decode was poor in this window despite being within source 1's nominal source window: 4.62 msg/sec average, 25.0% CC zero, 37.20% message-rate zero, and 219 retunes.
- North Bradley was usable but not strong: 16.62 msg/sec average, 12.5% CC zero, and 12 retunes.
- The second Airspy eliminated recorder exhaustion in this window, but it did not solve Chattanooga high-channel no-source outcomes because source 1 still covered only 850.131250-856.131250 MHz.
- Global TR CPU reached 150% and host temperature reached 66.65 C. That is materially higher than the one-Airspy run and should be watched if keeping two Airspy Minis active on the RPI.
```

### RPI-MOBILE-YAGI-AIRSPY-R2-01

Status: Complete

```text
Run: RPI-MOBILE-YAGI-AIRSPY-R2-01
RF path: Mobile yagi rig with one Airspy R2
Host: RPI PizzaWave API, http://100.105.110.92:8080/
Setup RF validation session: rf-20260623163819-30c714b9080
Started: 1782246286 / 2026-06-23T20:24:46Z / 2026-06-23 16:24:46 EDT
Ended:   1782248086 / 2026-06-23T20:54:46Z / 2026-06-23 16:54:46 EDT
Duration: 1800 seconds

API endpoints used:
- GET /api/v1/system/radio-setup/source-plan
- GET /api/v1/system/radio-setup
- GET /api/v1/troubleshoot/rf-analysis?system=chattanooga-simulcast-hamilton-t&start=1782246286&end=1782248086
- GET /api/v1/troubleshoot/rf-analysis?system=cleveland-bradley-tn&start=1782246286&end=1782248086
- GET /api/v1/troubleshoot?start=1782246286&end=1782248086&bySystem=true
- GET /api/v1/troubleshoot/tr-health?start=1782246286&end=1782248086
- GET /api/v1/status?start=1782246286&end=1782248086

Raw API artifacts:
- rpi-mobile-yagi-airspy-r2-source-plan-1782246286-1782248086.json
- rpi-mobile-yagi-airspy-r2-radio-setup-1782246286-1782248086.json
- rpi-mobile-yagi-airspy-r2-rf-analysis-chattanooga-1782246286-1782248086.json
- rpi-mobile-yagi-airspy-r2-rf-analysis-cleveland-1782246286-1782248086.json
- rpi-mobile-yagi-airspy-r2-troubleshoot-1782246286-1782248086.json
- rpi-mobile-yagi-airspy-r2-tr-health-1782246286-1782248086.json
- rpi-mobile-yagi-airspy-r2-status-1782246286-1782248086.json

Source plan and RF path:
- Setup RF validation session rf-20260623163819-30c714b9080: source_plan_applied, guided, site label airspy-r2, source plan applied 1 SDR source window for 2 systems.
- Source 0: airspy=637862DC2E457DD7, center 854.743750 MHz, sample rate 10.000000 MS/s, gain 15, error 0 Hz, covered systems: chattanooga-simulcast-hamilton-t and cleveland-bradley-tn.
- Source window from troubleshoot: 849.743750-859.743750 MHz, digitalRecorders=4, firstMatchCalls=200, coverableCalls=200, uniqueFrequencies=15.
- Source-plan systems:
  - chattanooga-simulcast-hamilton-t: qpsk, CC 855.212500 MHz, observed voice range 854.387500-858.437500 MHz, proposed source 0, covered.
  - cleveland-bradley-tn: qpsk, CC 851.050000 MHz, observed voice range 851.300000-853.200000 MHz, proposed source 0, covered.

RF-analysis results:
- chattanooga-simulcast-hamilton-t:
  - window: 6/23/2026 4:24 PM - 6/23/2026 4:54 PM
  - summary: CC summary average 39.71 msg/sec, CC zero 0.0%, 500 CC message-rate samples, 0 retunes.
  - CC summary zero rate: 0.00%
  - message-rate zero rate: 0.00%
  - retunes: 0
  - no transmissions: 4
  - recorder exhausted: 295
  - health rows: 5
  - retune targets: none
- cleveland-bradley-tn:
  - window: 6/23/2026 4:24 PM - 6/23/2026 4:54 PM
  - summary: CC summary average 37.14 msg/sec, CC zero 0.0%, 500 CC message-rate samples, 0 retunes.
  - CC summary zero rate: 0.00%
  - message-rate zero rate: 0.00%
  - retunes: 0
  - no transmissions: 0
  - recorder exhausted: 55
  - health rows: 5
  - retune targets: none

Raw TR-health bucket aggregate:
- Rows: 15 total; 5 buckets each for global, chattanooga-simulcast-hamilton-t, and cleveland-bradley-tn.
- Bucket starts covered: 2026-06-23 16:25:00 EDT through 2026-06-23 16:45:00 EDT.
- global: ccSummaryLines=14, ccSummaryZero=0, ccSummaryRateTotal=538, ccSummaryAverage=38.43, lowWarningLines=1000, lowWarningZero=0, retunes=0, callsStarted=349, callsConcluded=348, updateNotGrant=0, noTxRecorded=4, recorderExhausted=350, sampleStops=0, unableSource=0, tuningErrSamples=348, tuningErrTotalAbsHz=59914, tuningErrMaxAbsHz=1212, trCpuMax=165%, trRssMax=210.78 MB, hostTempMax=66.65 C.
- chattanooga-simulcast-hamilton-t: ccSummaryLines=7, ccSummaryZero=0, ccSummaryRateTotal=278, ccSummaryAverage=39.71, lowWarningLines=500, lowWarningZero=0, retunes=0, callsStarted=290, callsConcluded=290, updateNotGrant=0, noTxRecorded=4, recorderExhausted=295, sampleStops=0, unableSource=0, tuningErrSamples=290, tuningErrTotalAbsHz=52726, tuningErrMaxAbsHz=1212.
- cleveland-bradley-tn: ccSummaryLines=7, ccSummaryZero=0, ccSummaryRateTotal=260, ccSummaryAverage=37.14, lowWarningLines=500, lowWarningZero=0, retunes=0, callsStarted=59, callsConcluded=58, updateNotGrant=0, noTxRecorded=0, recorderExhausted=55, sampleStops=0, unableSource=0, tuningErrSamples=58, tuningErrTotalAbsHz=7188, tuningErrMaxAbsHz=213.

Troubleshoot source coverage for selected window:
- source 0 airspy=637862DC2E457DD7: 849.743750-859.743750 MHz, firstMatchCalls=200, coverableCalls=200, uniqueFrequencies=15.
- The selected-window source plan shows Chattanooga and Cleveland/Bradley fully covered.
- TR logged 0 no-source outcomes in the stored health buckets.

Traffic/status:
- /api/v1/status for the fixed window returned calls=200, incidents=2, alerts=0, tokens=41966.

Notes:
- This is a portable-rig baseline, not a same-site comparison against OT-DISCONE-01.
- The Airspy R2's 10 MS/s source window covered both Chattanooga and Cleveland/Bradley selected-window voice traffic with no no-source outcomes.
- Control-channel decode was strong and stable for both systems: Chattanooga averaged 39.71 msg/sec with 0 retunes; Cleveland/Bradley averaged 37.14 msg/sec with 0 retunes.
- Recorder capacity became the primary bottleneck: global recorderExhausted=350 across stored buckets with digitalRecorders=4.
- RPI load was high: TR CPU max 165%, host temperature max 66.65 C. The R2 did not appear to overload the RPI thermally in this 30-minute window, but CPU headroom is materially reduced.
```

### RPI-MOBILE-YAGI-AIRSPY-R2-EXTENDED-01

Status: Complete

```text
Run: RPI-MOBILE-YAGI-AIRSPY-R2-EXTENDED-01
RF path: Mobile yagi rig with one Airspy R2
Host: RPI PizzaWave API, http://100.105.110.92:8080/
Setup RF validation session: rf-20260623163819-30c714b9080
Started: 1782246286 / 2026-06-23T20:24:46Z / 2026-06-23 16:24:46 EDT
Ended:   1782303711 / 2026-06-24T12:21:51Z / 2026-06-24 08:21:51 EDT
Duration: 57425 seconds

API endpoints used:
- GET /api/v1/system/radio-setup/source-plan
- GET /api/v1/system/radio-setup
- GET /api/v1/troubleshoot/rf-analysis?system=chattanooga-simulcast-hamilton-t&start=1782246286&end=1782303711
- GET /api/v1/troubleshoot/rf-analysis?system=cleveland-bradley-tn&start=1782246286&end=1782303711
- GET /api/v1/troubleshoot?start=1782246286&end=1782303711&bySystem=true
- GET /api/v1/troubleshoot/tr-health?start=1782246286&end=1782303711
- GET /api/v1/status?start=1782246286&end=1782303711

Raw API artifacts:
- rpi-mobile-yagi-airspy-r2-extended-source-plan-1782246286-1782303711.json
- rpi-mobile-yagi-airspy-r2-extended-radio-setup-1782246286-1782303711.json
- rpi-mobile-yagi-airspy-r2-extended-rf-analysis-chattanooga-1782246286-1782303711.json
- rpi-mobile-yagi-airspy-r2-extended-rf-analysis-cleveland-1782246286-1782303711.json
- rpi-mobile-yagi-airspy-r2-extended-troubleshoot-1782246286-1782303711.json
- rpi-mobile-yagi-airspy-r2-extended-tr-health-1782246286-1782303711.json
- rpi-mobile-yagi-airspy-r2-extended-status-1782246286-1782303711.json

Source plan and RF path:
- Setup RF validation session rf-20260623163819-30c714b9080: source_plan_applied, guided, site label airspy-r2, source plan applied 1 SDR source window for 2 systems.
- Source 0: airspy=637862DC2E457DD7, center 854.743750 MHz, sample rate 10.000000 MS/s, gain 15, error 0 Hz, covered systems: chattanooga-simulcast-hamilton-t and cleveland-bradley-tn.
- Source window from troubleshoot: 849.743750-859.743750 MHz, digitalRecorders=4, firstMatchCalls=4178, coverableCalls=4178, uniqueFrequencies=15.
- Source-plan systems:
  - chattanooga-simulcast-hamilton-t: qpsk, CC 855.212500 MHz, observed voice range 854.387500-858.437500 MHz, proposed source 0, covered.
  - cleveland-bradley-tn: qpsk, CC 851.050000 MHz, observed voice range 851.300000-853.200000 MHz, proposed source 0, covered.

RF-analysis results:
- chattanooga-simulcast-hamilton-t:
  - window: 6/23/2026 4:24 PM - 6/24/2026 8:21 AM
  - summary: CC summary average 39.04 msg/sec, CC zero 0.0%, 19000 CC message-rate samples, 0 retunes.
  - CC summary zero rate: 0.00%
  - message-rate zero rate: 0.00%
  - retunes: 0
  - no transmissions: 98
  - recorder exhausted: 3316
  - health rows: 190
  - retune targets: none
- cleveland-bradley-tn:
  - window: 6/23/2026 4:24 PM - 6/24/2026 8:21 AM
  - summary: CC summary average 39.93 msg/sec, CC zero 0.0%, 19000 CC message-rate samples, 0 retunes.
  - CC summary zero rate: 0.00%
  - message-rate zero rate: 0.00%
  - retunes: 0
  - no transmissions: 14
  - recorder exhausted: 491
  - health rows: 190
  - retune targets: none

Raw TR-health bucket aggregate:
- Rows: 570 total; 190 buckets each for global, chattanooga-simulcast-hamilton-t, and cleveland-bradley-tn.
- Bucket starts covered: 2026-06-23 16:25:00 EDT through 2026-06-24 08:10:00 EDT.
- global: ccSummaryLines=566, ccSummaryZero=0, ccSummaryRateTotal=22350, ccSummaryAverage=39.49, lowWarningLines=38000, lowWarningZero=0, retunes=0, callsStarted=9278, callsConcluded=9279, updateNotGrant=18, noTxRecorded=112, recorderExhausted=3807, sampleStops=0, unableSource=0, tuningErrSamples=9279, tuningErrTotalAbsHz=1747610, tuningErrMaxAbsHz=1962, trCpuMax=165%, trRssMax=520.61 MB, hostTempMax=67.75 C.
- chattanooga-simulcast-hamilton-t: ccSummaryLines=283, ccSummaryZero=0, ccSummaryRateTotal=11049, ccSummaryAverage=39.04, lowWarningLines=19000, lowWarningZero=0, retunes=0, callsStarted=7960, callsConcluded=7961, updateNotGrant=14, noTxRecorded=98, recorderExhausted=3316, sampleStops=0, unableSource=0, tuningErrSamples=7961, tuningErrTotalAbsHz=1562926, tuningErrMaxAbsHz=1962.
- cleveland-bradley-tn: ccSummaryLines=283, ccSummaryZero=0, ccSummaryRateTotal=11301, ccSummaryAverage=39.93, lowWarningLines=19000, lowWarningZero=0, retunes=0, callsStarted=1318, callsConcluded=1318, updateNotGrant=4, noTxRecorded=14, recorderExhausted=491, sampleStops=0, unableSource=0, tuningErrSamples=1318, tuningErrTotalAbsHz=184684, tuningErrMaxAbsHz=257.

Troubleshoot source coverage for selected window:
- source 0 airspy=637862DC2E457DD7: 849.743750-859.743750 MHz, firstMatchCalls=4178, coverableCalls=4178, uniqueFrequencies=15.
- The selected-window source plan shows Chattanooga and Cleveland/Bradley fully covered.
- TR logged 0 no-source outcomes in the stored health buckets.

Traffic/status:
- /api/v1/status for the fixed window returned calls=4178, incidents=44, alerts=0, tokens=1325791.

Notes:
- This is an extended stability window, not a strict 30-minute A/B window.
- The Airspy R2 path remained RF-stable overnight: both systems had 0.00% CC zero, 0.00% message-rate zero, 0 retunes, and 0 no-source outcomes.
- Recorder capacity remained the primary limiting factor: global recorderExhausted=3807 across the stored buckets with digitalRecorders=4.
- RPI resource use stayed high but bounded in this window: TR CPU max 165%, TR RSS max 520.61 MB, and host temperature max 67.75 C.
```

## Comparison Notes

Fill this after runs complete.

| System | Best run | Evidence | Caveats |
| --- | --- | --- | --- |
| TBD | TBD | TBD | TBD |
