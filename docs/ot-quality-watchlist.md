# OT Quality Watchlist

Running evidence log for the recurring OT/omicrontheta quality checks.

Scope:
- Target: `lilhoser@192.168.1.173`
- Cadence: hourly post-deploy monitoring as of `2026-06-16T21:31:58Z`
- Remote checks are read-only.
- Compare each latest hour with the preceding hour, using broader 8-hour context when the hourly sample is sparse.

## Active Watch Items

| Item | Status | Evidence To Correlate | Potential Fix Direction |
| --- | --- | --- | --- |
| Incident extraction truncation | Monitor | `/api/v1/system/quality-check` AI failures/truncated, `lm_usage.finish_reason='length'`, prompt/completion tokens, activity=`automatic insights` | Mostly mitigated again: `2026-06-09 01:24Z` had `0` length failures after the `2026-06-08 09:24Z` spike. Revisit only if high single-digit or worse failures return, parse failures appear, or truncation drops major incident evidence. |
| AI routing/model availability | Active | `lm_usage` failures, request/response model IDs, `No models loaded` errors, `response_model` drift from expected `paxan-qwen3.6-35b-a3b@q8_0`; `2026-06-11 09:31Z` showed `12` automatic-insights failures; `2026-06-15 09:40Z` had `223/223` successes but all rows still returned `qwen3.6-35b-a3b` | Verify Paxan/LM Studio model load stability and response model normalization. Restore expected response model identifier or document the intentional change; alert on transient model unloads because they skip incident extraction work. |
| Verifier retained calls narrowed by ownership | Active | `evidenceVerifier.retentionMismatches`, audit reasons like `verifier retained N call(s), final ownership retained 1`, duplicate/ownership rejection rows; fixed regression case `3220` around calls `637003`, `637049`, `637117`; residual live cases `3230` around calls `641061`, `641065`, `641069` and Dierke overdose around calls `647381`, `647399`, `647409`, `647417` | Commit `2a99716` fixed injury/highway continuation retention, but live evidence shows same-event continuations can still be narrowed when shared identity comes from event/suspect/vehicle/cross-agency dispatch facts rather than a strong normalized location anchor. Next fix should use structured event-identity support beyond location-only anchors without broadening unrelated candidate joins. |
| Strong multi-call events rejected by title/detail support | Active | `2026-06-17 06:34Z` missed Hwy 95 motorcycle wreck: calls `767763`, `767769`, `767777`, `767779`, `767875`, `767903`; audit rows rejected `767763,767769,767779,767875,767903` with parent evidence `MVC with injuries, unresponsive patient`, strong `hwy 95` geo/time scoring, and reasons `only 1 call(s) match the incident title/detail` / `only 0 call(s) match the incident title/detail`; none of those calls were linked in `incident_calls`. `2026-06-17 07:34Z` also had unlinked rejected multi-call vehicle-stop/BOLO-like cluster `768325`, `768337`, `768339`, `768341`, `768345` with silver Chevy/sedan/temp tag and attempted stop context. `2026-06-17 09:36Z` missed suicide-attempt/wrist-cut cluster `768913`, `768915`, `768919` with EMS and police/church contact transcripts; rejected as no corroborating related calls or weak single-call signal and no linked incident rows. `2026-06-17 10:38Z` missed domestic/self-harm/machete cluster `769303`, `769305`, `769307`; audit parent evidence said `domestic violence with machete`, call `769307` explicitly mentions machete, but no linked incident rows. | Separate incident-existence validation from narrative/title support. If retained calls independently establish a shared emergency event and anchor, accept the incident and ground or simplify the title from retained transcript evidence instead of rejecting the whole incident because the proposed title/detail wording is under-supported. |
| Single-call incident acceptance | Watch | Incident call-count distribution, accepted single-call incidents, reject buckets for single-call strong signal/noisy/routine/anchor missing | Tune single-dispatch acceptance only with representative true positives/false positives; avoid broad yield changes from aggregate counts alone. |
| Incident title/source evidence drift | Active | Titles/details that assert facts not present in retained transcripts; old examples `3171` and `3144` with `machete`; post-grounding `2026-06-15 09:40Z` shows fewer rewrites but still includes `Vehicle accident at 195 Windcrest Pl W` under a carbon-monoxide key, `Shooting at 1240 Brookfield Ct NE` with weak retained transcript detail, and generic `Police call at Shady's near cemetery` | Narrative grounding is preventing unsupported phrases from being saved, but fallback now masks some supported incident semantics or accepts too-vague category/location incidents. Next fix should make unsupported-narrative fallback acceptance require a supported strong event concept, and when a concept is supported by retained transcripts, preserve that concept instead of reducing to `Police/Fire/EMS/Traffic call`. |
| Generic incident key reuse / row overwrite | Monitor | Generic keys with repeated accepted creates: `INC-20260610-001` had `7` accepted creates since `09:29Z` and `11` total; `2026-06-11 17:32Z` broadened to `INC-20260611-001/002/003/004/005/007/012`; exact `INC-YYYYMMDD-NNN` keys stayed fixed after deploy, but `2026-06-14 09:36Z` found short generic keys `INC-001` and `INC-003` accepted as creates against old May incident rows; server-owned immutable incident identifiers deployed `2026-06-16T21:31:58Z`; no row-overwrite pattern observed in post-deploy hourly checks through `2026-06-17T05:34Z`; unknown shortened key recurred `2026-06-17T10:10Z`/`10:21Z` as `llm:whiteoakmt-hamilton:769081` for actual key `llm:whiteoakmt-hamilton:769081:fire-citizen-assist-at-6939`; `2026-06-17T11:35Z` had shortened `llm:whiteoakmt-hamilton:769703:elevator-entrapment-at-erlanger-med` while the full-key incident `...-medical` already retained calls `769703`, `769707`, `769709`, `769725` | Keep monitoring for `rejected:unknown model incident_id`, accepted creates using old generic keys, or missing creates caused by the stricter identifier contract. Supported cleanup: stop exposing long database keys to the model and use short per-prompt aliases mapped back server-side. |
| Membership assembly still admits unrelated calls | Active | Post `2026-06-17T21:22:05Z` membership gate still admitted unrelated calls. Existing-update example: incident `3788`, key `llm:whiteoakmt-hamilton:780913:event`, moved from calls `780913,780945` at audit rows `29230`/`29231` to `780801,780913,780945` at row `29233`; transcripts show South Creek crash, Thrasher/Harper speeding complaint, and Suck Creek crash. Create examples: incident `3796` joined `781123` Wheeler/Wilson chest-pain call with `781987` 1201 West North Main unconscious-person call at row `29246`; incident `3799` kept only `782611,782621` for an unconscious patient at `9469 Bradmore Ln`, but its title/detail still said fire alarm because row `29257` initially mixed those calls with false fire alarm calls `782045,782101`. Latest confirmation: as of `2026-06-18 00:48Z`, incident `3803`, key `llm:whiteoakmt-cleveland:783137:event`, currently contains both Rico Road / Candies Creek trash-fire calls `782975,783137,783275,783439` and Old Eureka Road calls `783269,783339`. | The gate helped with some weak calls, but the pipeline still needs one final server-side membership object used consistently for both creates and updates. New members should need event anchors beyond semantic/time similarity when locations or incident facts conflict, and title/detail should be accepted only after final membership is known. |
| Cross-agency incident collation | Investigate | Incident `2948` / `INC-20260606-014`; incident pair `3016`/`3017`; incident pair `3149`/`3150`; incident `3220`; related calls `583813`, `583831`, `583839`, `583869`, `583885`, `583907`, `584025`, `584085`, `584109`, `584139`, `595043`, `595053`, `595065`, `624411`, `624429`, `624439`, `637003`, `637049`, `637117`; audit redirects/rejects for wider candidate sets | Code fix for highway marker/landmark anchors deployed at commit `8dfae80`; continue auditing why existing owned calls and same-event candidates do not merge cleanly when old rows lack post-processed anchors. |
| Highway mile-marker geocoding | Investigate | Incident `2948`; incident pair `3016`/`3017`; incident `3220`; old call location rows for `583813`, `583869`, `583907`, `595043`, `595053`, `595065` extracted only bare/missing highway anchors; new call `622585` missed `mile marker 350, high 75 northbound`; new call `637003` missed `three mile marker, Interstate 75 Southbound`; cache rows often `confidence=0` | Structured marker/landmark extraction was added for common I-24/I-75 phrasing, but needs speech variant coverage such as `high 75`/`highway 75` plus marker-before-highway phrasing. Already-processed calls still need either safe reprocessing/backfill or a manual repair path. Dedicated mile-marker/exit coordinate resolution remains separate from extraction. |
| Historical quality-check incident metrics | Investigate | Repeat queries for the same closed 8-hour window, `incidents.last_seen`, incident updates/conclusions after window end | Stabilize monitoring by using immutable create/update/audit counts or overlap semantics for historical incident windows instead of only current `last_seen` within the window. |
| Medical transport/logistics suppression | Active | Reject buckets for `medical_transport_context` and standalone transport/hospital handoff, representative call IDs/transcripts. `2026-06-17 07:34Z` rejected calls `768279`, `768281`, `768283` as medical transport without parent event, but `768279` transcript says chest pain, altered mental status, PD en route, and patient description; no linked incident row. `2026-06-17 08:35Z` rejected possible chlorine ingestion calls `768673`, `768697`, `768759`, `768761` as standalone transport/hospital handoff even though dispatch and EMS encode mention drank chlorine, PD on scene, poison control, and no linked incident row. | Keep suppressing true routine handoffs, but do not classify initial dispatch, poisoning/overdose evidence, or on-scene emergency updates as transport/logistics when the retained transcript contains a parent medical emergency such as chest pain, altered mental status, breathing difficulty, CPR, seizure, overdose, poisoning/ingestion, or unconsciousness. |
| Geocode-related joins/rejections | Watch | Reject reasons mentioning location disagreement/anchor support, cached geocode confidence, map-linked incidents | Preserve weak-anchor handling for low/unmapped geocodes; fix only when strong transcript anchors are being ignored. |
| Transcription quality and throughput | Watch | Queue depth, pending audio, ingest/transcribe rates, realtime factor, quality reasons (`repetitive`, `empty`, `too_short`, `numeric_noise`) | Tune faster-whisper/VAD/workers only if backlog grows or quality reasons spike with representative audio evidence. |
| Embedding/Qdrant health | Watch | Runtime embedding status, endpoint OK, Qdrant OK, queue depth, failed/pending embedding jobs, `No models loaded` errors | Fix LM Studio/Qdrant/autoload only on actual failures; avoid touching when queue and failed counts remain zero. |
| RF/TR as explanatory context | Watch | TR health samples, decode zero rate, retunes, no-transmission/recorder exhaustion when correlated with capture or transcript gaps | Treat RF changes as secondary unless they explain call capture, transcription, or incident quality regressions. |

Correction:
- Prior notes that framed single-call incident category inheritance as a fix candidate are superseded. Single-call incidents should remain TG-category derived; category concerns should be evaluated only for multi-call presentation and source-card clarity.

## Evidence Log

### 2026-06-04 09:21Z Check

Window: `2026-06-04T01:21Z` to `2026-06-04T09:21Z`; compared with `2026-06-03T17:21Z` to `2026-06-04T01:21Z`.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active.
- Queues healthy: transcription queue `0`, pending transcriptions `0`, pending audio `0`.
- Throughput healthy: ingest/transcribe both about `3.6 calls/min`; average transcription realtime factor `0.156`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`, pending `0`.
- Calls fell from `4,395` to `2,420`; incident yield rose from `11.15` to `12.40 incidents / 1,000 calls`.
- AI routing correct: request model `qwen/qwen3.6-35b-a3b@q8_0`, response model `paxan-qwen3.6-35b-a3b@q8_0`.
- AI truncation improved but persisted: `8/245` requests ended with `finish_reason=length`; all were incident extraction at `max_tokens=1800`.
- Verifier narrowing remained noisy: `69` retention mismatches; common accepted reasons included `verifier retained N call(s), final ownership retained 1`.
- Single-call incidents remained common: `19/30` incidents had one linked call.
- Top reject buckets: single-call lacks strong event signal `21`, medical transport lacks parent event `21`, only one call matches title/detail `17`.

Current read:
- No service, queue, transcription, or embedding intervention needed.
- Keep watching incident extraction truncation and verifier/ownership narrowing.
- Review representative single-call medical/fire event titles categorized as `police` before changing category logic; this may reflect police TG source categories rather than a broken event classifier.

### 2026-06-04 17:21Z Check

Window: `2026-06-04T09:21Z` to `2026-06-04T17:21Z`; compared with `2026-06-04T01:21Z` to `2026-06-04T09:21Z`.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active.
- Queues healthy: transcription queue `0`, pending transcriptions `0`, pending audio `0`.
- Throughput healthy under higher volume: ingest about `7.3 calls/min`, transcribe about `7.5 calls/min`, average transcription realtime factor `0.133`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`, pending `0`.
- Calls rose from `2,424` to `3,710`; incident yield rose from `7.01` to `8.36 incidents / 1,000 calls`.
- AI routing remained correct: request model `qwen/qwen3.6-35b-a3b@q8_0`, response model `paxan-qwen3.6-35b-a3b@q8_0`.
- AI truncation persisted and increased: `12/278` requests ended with `finish_reason=length`; all sampled rows were incident extraction at `max_tokens=1800` with completion tokens pinned at `1800`.
- Verifier narrowing worsened: retention mismatches rose from `69` to `124`; common accepted reasons included `verifier retained 2/3/4/5/6 call(s), final ownership retained 1`.
- Single-call incidents remained dominant: `24/31` incidents had one linked call.
- Top reject buckets: single-call lacks strong event signal `37`, only one call matches title/detail `28`, unsupported emergency/event title/detail `17`, noisy/routine single-call source `15`, medical transport lacks parent event `12`.
- Transcript quality still acceptable but noisy: `3,374` OK complete calls, `237` repetitive, `44` empty, `44` too short, `9` numeric noise, plus one `marker_only` and one `overexpanded`.

Correlated category evidence:
- Incident `2788`, `Shots Fired / Multiple Casualties at 3352 Charter Dr`, category `other`; retained call `535497` came from `HC DISASTER3` with category `other` and transcript text indicating multiple reports of shots fired.
- Incident `2776`, `Structure Fire at Gopherty's Main Street`, category `police`; retained call `531177` came from `UTC Police Disp` with category `police` and transcript text indicating a fully engulfed fire.
- Incident `2758`, `Assault at 801 River Blvd between W Main and W 19th St`, category `ems`; retained call `537473` came from `HC EMS-MAIN` and transcript says assault with EMS needed.
- Incidents `2771` and `2772` are medical-emergency titles categorized `fire` because the retained calls came from Bradley fire/rescue talkgroup `5511`.

Current read:
- No service, queue, transcription, embedding, or RF intervention needed.
- Incident extraction truncation is still worth fixing, but the failure rate is not causing a yield collapse.
- Verifier/ownership narrowing is now the largest repeated metric issue: it may be duplicate protection, but `124` mismatches in one 8-hour window is high enough to audit with representative call ownership chains.
- Category semantics now has enough evidence for a candidate fix: overall incident category should probably derive from strong event semantics even for single-call incidents, while source sub-cards keep TG-derived categories.

### 2026-06-05 01:21Z Check

Window: `2026-06-04T17:21Z` to `2026-06-05T01:21Z`; compared with `2026-06-04T09:21Z` to `2026-06-04T17:21Z`.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active.
- Queues healthy: transcription queue `0`, pending transcriptions `0`, pending audio `0`.
- Throughput healthy: ingest about `6.6 calls/min`, transcribe about `6.7 calls/min`, average transcription realtime factor `0.096`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`, pending `0`.
- Calls rose from `3,711` to `4,179`; latest-window incident yield was `11.73 incidents / 1,000 calls`.
- AI routing remained correct: request model `qwen/qwen3.6-35b-a3b@q8_0`, response model `paxan-qwen3.6-35b-a3b@q8_0`.
- AI truncation improved from `12/278` to `8/285`, but sampled failure rows were still incident extraction at `max_tokens=1800` with completion tokens pinned at `1800`.
- Verifier narrowing improved from `124` to `99` retention mismatches but remains high.
- Single-call incidents remained dominant: `33/49` incidents had one linked call.
- Top reject buckets: medical transport lacks parent event `23`, only one call matches title/detail `21`, single-call lacks strong event signal `20`, unsupported emergency/event title/detail `20`, only one call supports title/detail anchor `14`.
- Transcript quality remained acceptable but noisy: `3,807` OK complete calls, `283` repetitive, `44` too short, `37` empty, `6` numeric noise, `2` marker-only.

Correlated category evidence:
- Single-call medical or EMS-like incidents still appear under source categories such as `fire` or `police`: examples include incident `2765` (`Medical emergency at 13601 Washington St`, category `fire`), `2763` (`Fall victim at 1680 Muddy Creek Rd`, category `police`), and `2818` (`Aggressive patient at Bradley Health Care`, category `police`).
- Crash/MVC categorization still follows retained source categories in some single-call cases: incident `2812` (`MVC on Ringold Rd Southbound Ramp`) is category `fire`; incident `2792` (`Multi-Vehicle Crash I-75 NB near Mile 175`) is category `other`.

Monitoring caveat:
- The previous 8-hour window (`2026-06-04T09:21Z` to `2026-06-04T17:21Z`) reported `31` incidents when checked at `17:21Z`, but only `14` incidents when the same window was re-queried at `01:21Z`. This is strong evidence that the quality-check incident aggregate is not stable for historical comparisons, likely because it counts current incidents by `last_seen` within the queried window and later incident updates can move rows out of the old window.

Current read:
- No service, queue, transcription, embedding, or RF intervention needed.
- Keep the incident-extraction truncation item open; the rate improved, but the failure mode is unchanged.
- Keep verifier/ownership narrowing open; the metric improved but is still high enough to audit.
- Promote historical quality-check incident metrics to `Investigate`: before using before/after incident volume as a hard signal, monitoring should account for mutable `last_seen` behavior or rely on audit/create/update counts.

### 2026-06-05 09:21Z Check

Window: `2026-06-05T01:21Z` to `2026-06-05T09:21Z`; compared with `2026-06-04T17:21Z` to `2026-06-05T01:21Z`.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active.
- Queues effectively healthy: transcription queue `0`, pending transcriptions `1`, pending audio `13s`.
- Throughput healthy: ingest about `2.6 calls/min`, transcribe about `2.5 calls/min`, average transcription realtime factor `0.088`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`, pending `0`.
- Calls fell from `4,179` to `2,343`; latest-window incident yield was `11.95 incidents / 1,000 calls`.
- AI routing remained correct: request model `qwen/qwen3.6-35b-a3b@q8_0`, response model `paxan-qwen3.6-35b-a3b@q8_0`.
- AI truncation increased slightly from `8/285` to `10/230`; sampled failures were still incident extraction at `max_tokens=1800` with completion tokens pinned at `1800`.
- Verifier narrowing improved from `99` to `48` retention mismatches.
- Single-call incidents remained common: `19/28` incidents had one linked call.
- Top reject buckets: only one call matches title/detail `38`, single-call lacks strong event signal `32`, unsupported emergency/event title/detail `18`, only one call supports title/detail anchor `18`, only zero calls match title/detail `11`.
- Transcript quality remained acceptable: `2,177` OK complete calls, `126` repetitive, `15` too short, `13` empty, `10` numeric noise, `2` marker-only.

Correlated category evidence:
- Event/category semantics still follows retained source category in several single-call cases: incident `2830` (`Fall victim at 7488 Back Valley Rd`, category `police`), incident `2796` (`MVC with injuries, South Hwy 341 at Bakern-Hearn`, category `fire`), and incident `2792` from the prior run (`Multi-Vehicle Crash I-75 NB near Mile 175`, category `other`).
- Mixed-source incident `2835`, `Medical assist at 1 Washington St`, landed as category `ems` with retained call categories `police,fire`, which is a positive example of event-level category inference for a multi-call incident.
- Mixed-source incident `2822`, `MVC with injuries at Munstreet and Dooley St`, landed as category `traffic` with retained call categories `police,fire`, also consistent with event-level category inference when multiple calls are retained.

Monitoring caveat:
- Historical incident count drift repeated. The `2026-06-04T17:21Z` to `2026-06-05T01:21Z` window reported `49` incidents when checked at `01:21Z`, but `38` when re-queried at `09:21Z`. This further confirms that the quality-check endpoint's incident aggregate is mutable and should not be used alone for strict before/after comparisons.

Current read:
- No service, queue, transcription, embedding, or RF intervention needed.
- Keep incident extraction truncation open; rate is still nonzero and failure mode is unchanged.
- Verifier/ownership narrowing improved this run but remains a watch item across the multi-run trend.
- Category semantics has a sharper shape now: multi-call mixed-source events can categorize correctly, while single-call events still often inherit source categories. That points toward a targeted single-call event-category fix rather than a broad category rewrite.
- Historical incident metrics remain an `Investigate` item; use audit creates/updates/rejects and immutable operation timestamps for trend comparisons until the endpoint is fixed.

### 2026-06-05 17:21Z Check

Window: `2026-06-05T09:21Z` to `2026-06-05T17:21Z`; compared with `2026-06-05T01:21Z` to `2026-06-05T09:21Z`.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active.
- Queues healthy: transcription queue `0`, pending transcriptions `0`, pending audio `13s`.
- Throughput healthy under higher live rate: ingest about `9.5 calls/min`, transcribe about `9.9 calls/min`, average transcription realtime factor `0.091`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`, pending `0`.
- Calls rose from `2,343` to `3,253`; latest-window incident yield was `8.61 incidents / 1,000 calls`.
- AI routing remained correct: request model `qwen/qwen3.6-35b-a3b@q8_0`, response model `paxan-qwen3.6-35b-a3b@q8_0`.
- AI truncation persisted and increased slightly from `10/230` to `12/241`; sampled failures were still incident extraction at `max_tokens=1800` with completion tokens pinned at `1800`.
- Verifier narrowing rebounded from `48` to `95` retention mismatches.
- Single-call incidents remained common: `16/28` incidents had one linked call.
- Top reject buckets: single-call lacks strong event signal `26`, medical transport lacks parent event `21`, only one call matches title/detail `20`, unsupported emergency/event title/detail `12`.
- Transcript quality remained acceptable: `2,973` OK complete calls, `204` repetitive, `32` empty, `32` too short, `11` numeric noise, `1` marker-only.

Correlated category evidence:
- Single-call category inheritance remains visible: incident `2845` (`Apartment Fire at 900 Mountain Creek Rd - Unit 154`) is category `police`; retained call category is `police`.
- Medical/EMS-like single-call events still inherit fire or police categories: incident `2860` (`Medical assist at 3800 W View Dr NE`) category `fire`; incident `2849` (`Stroke/Altered Mental Status at 7102 Elmbrook Ln`) category `police`; incident `2843` (`Overdose at 343 Mustang St - Police Response`) category `fire`.
- MVC single-call events continue to follow source category: incident `2851` (`MVC with injuries on Baddison Parkway`) category `fire`; incidents `2858` and `2859` are MVC titles categorized `police`.
- Multi-call category examples were less prominent this run, but prior positive evidence still suggests mixed-source multi-call events can classify correctly.

Current read:
- No service, queue, transcription, embedding, or RF intervention needed.
- This run reinforces existing fix candidates rather than adding a new one.
- Incident extraction truncation and verifier/ownership narrowing remain active watch items.
- Single-call event-category inheritance remains the clearest targeted incident-display fix candidate.

### 2026-06-06 01:21Z Check

Window: `2026-06-05T17:21Z` to `2026-06-06T01:21Z`; compared with `2026-06-05T09:21Z` to `2026-06-05T17:21Z`.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active.
- Queues healthy: transcription queue `0`, pending transcriptions `0`, pending audio `0`.
- Throughput healthy: ingest about `5.9 calls/min`, transcribe about `6.0 calls/min`, average transcription realtime factor `0.113`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`, pending `0`.
- Calls rose from `3,255` to `4,599`; latest-window incident yield was `10.65 incidents / 1,000 calls`.
- AI routing remained correct: request model `qwen/qwen3.6-35b-a3b@q8_0`, response model `paxan-qwen3.6-35b-a3b@q8_0`.
- AI truncation stayed flat at `12` failures but rate improved from `12/241` to `12/312`; sampled failures were still incident extraction at `max_tokens=1800` with completion tokens pinned at `1800`.
- Verifier narrowing rose from `95` to `130` retention mismatches.
- Single-call incidents remained common: `27/49` incidents had one linked call.
- Top reject buckets: only one call matches title/detail `28`, medical transport lacks parent event `26`, unsupported emergency/event title/detail `22`, single-call lacks strong event signal `21`.
- Transcript quality remained acceptable: `4,205` OK complete calls, `283` repetitive, `48` too short, `46` empty, `15` numeric noise, `1` marker-only, `1` overexpanded.

Correlated category evidence:
- Single-call medical/EMS-like incidents continued to inherit fire category: incident `2900` (`Diabetic emergency at 7704 Canyon Dr`, category `fire`), incident `2898` (`Unconscious person at 402 Lincrest Rd`, category `fire`), incident `2884` (`Medical assist at 540 Central Avenue NW`, category `fire`), and incident `2866` (`Fall/Medical Assist at 209 Joist Drive`, category `fire`).
- Single-call crash/MVC events continued to inherit police category: incident `2888` (`MVC at Cherry St and E Ave`, category `police`), incident `2882` (`MVA at 4530 Frontage Road NW involving Jeep and Honda`, category `police`), and incident `2871` (`Crash at 4396 Amnicola Highway`, category `police`).
- Positive mixed-source examples persisted: incident `2837` (`MVC on I-75 with injuries and potential entrapment`) landed as `ems` with retained call categories `ems,fire,police`; incident `2879` (`MVC with injuries at 2500 Block East 43rd Street`) landed as `traffic` with retained categories `ems,fire,police`.

Current read:
- No service, queue, transcription, embedding, or RF intervention needed.
- Keep incident extraction truncation open; count is flat but still recurring.
- Verifier/ownership narrowing remains the largest repeated metric issue this run.
- Category evidence continues to point to a targeted single-call event-category fix while preserving the existing mixed-source event-category behavior.

### 2026-06-06 09:21Z Check

Window: `2026-06-06T01:21Z` to `2026-06-06T09:21Z`; compared with `2026-06-05T17:21Z` to `2026-06-06T01:21Z`.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active.
- Queues healthy: transcription queue `0`, pending transcriptions `0`, pending audio `0`.
- Throughput healthy: ingest about `2.0 calls/min`, transcribe about `2.1 calls/min`, average transcription realtime factor `0.153`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`, pending `0`.
- Calls fell from `4,597` to `2,427`; latest-window incident yield was `14.83 incidents / 1,000 calls`.
- AI routing remained correct: request model `qwen/qwen3.6-35b-a3b@q8_0`, response model `paxan-qwen3.6-35b-a3b@q8_0`.
- AI truncation improved from `12/311` to `5/244`. Four sampled failures were incident extraction at `max_tokens=1800`; one was evidence verifier truncation at `max_tokens=1600`.
- Verifier narrowing improved from `129` to `55` retention mismatches.
- Single-call incidents remained common: `20/36` incidents had one linked call.
- Top reject buckets: only one call matches title/detail `26`, medical transport lacks parent event `24`, single-call lacks strong event signal `21`, only zero calls match title/detail `17`.
- Transcript quality remained acceptable: `2,242` OK complete calls, `141` repetitive, `24` too short, `12` empty, `8` numeric noise.

Correlated category evidence:
- Single-call medical/EMS-like incidents still inherit non-EMS source categories: incident `2928` (`Toddler difficulty breathing at 9516 Dayton Pike Apt 711`) category `police`; incident `2918` (`Medical assist at 71 Lee Hwy American House`) category `police`; incident `2929` (`Chest pain call at 822 West 13th Street`) category `fire`; incident `2911` (`Fall at 8613 Brookshadow Dr`) category `fire`.
- Single-call crash/MVC events still inherit police category: incident `2836` (`MVC at 550 E Fort St SE`) category `police`; incident `2926` (`Car wrecked into ditch near Genuine Road`) category `police`.
- Mixed-source category behavior still has positive examples: incident `2919` (`CPR in progress at 2114 Davenport Street`) category `ems` with retained categories `fire,police,ems`; incident `2922` (`Medical alarm at 3 Girls in Tomahawk Circle NW`) category `ems` with retained categories `ems,police`; incident `2842` (`Medical Assist at 824 Council Road NE`) category `ems` with retained categories `ems,fire`.

Current read:
- No service, queue, transcription, embedding, or RF intervention needed.
- Truncation improved, but keep the item open because incident extraction still truncates and evidence verifier had one truncation.
- Verifier/ownership narrowing improved this run but remains a multi-run watch item.
- Category evidence continues to support a targeted single-call event-category fix while preserving mixed-source event-category behavior.

### 2026-06-06 17:22Z Check

Window: `2026-06-06T09:22Z` to `2026-06-06T17:22Z`; compared with `2026-06-06T01:22Z` to `2026-06-06T09:22Z`.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active.
- Queues healthy: transcription queue `0`, pending transcriptions `0`, pending audio `0`.
- Throughput healthy: ingest about `5.2 calls/min`, transcribe about `5.8 calls/min`, average transcription realtime factor `0.092`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`, pending `0`.
- Calls rose from `2,426` to `2,857`; latest-window incident yield was `11.90 incidents / 1,000 calls`.
- AI routing remained correct: request model `qwen/qwen3.6-35b-a3b@q8_0`, response model `paxan-qwen3.6-35b-a3b@q8_0`.
- AI truncation worsened from `5/244` to `16/262`; sampled failures were all incident extraction at `max_tokens=1800`.
- Verifier narrowing rose from `55` to `88` retention mismatches.
- Single-call incidents remained dominant: `23/34` incidents had one linked call.
- Top reject buckets: single-call lacks strong event signal `37`, medical transport lacks parent event `33`, only one call matches title/detail `20`, unsupported emergency/event title/detail `13`.
- Transcript quality remained acceptable: `2,608` OK complete calls, `182` repetitive, `39` empty, `18` too short, `10` numeric noise.

Correlated category evidence:
- Single-call medical/EMS-like incidents continued to inherit fire or police categories: incident `2961` (`Medical Assist Red Barn Ln`) category `fire`; incident `2966` (`Stroke-like symptoms 1627 E Hill St`) category `fire`; incident `2958` (`Unconscious female and child on Cottonport Rd`) category `police`; incident `2948` (`Medical emergency at Bunion Stride Dr, Army Reserve`) category `fire`.
- Single-call vehicle/fire or MVC events continued to inherit police category: incident `2956` (`Vehicle fire at 3906 Retro Hughes Rd`) category `police`; incident `2960` (`MVC Minute Drive with no injuries`) category `police`; incident `2953` (`MVC on Merchant Pike, unknown injuries reported`) category `police`.
- Mixed-source behavior still looks better: incident `2965` (`MVC 1346 Hickory Valley Rd into building`) landed as `traffic` with retained call categories `police,fire,ems`; incident `2962` (`EMS 128 Gravitt Rd Fall`) landed as `ems` with retained call categories `ems,police`.

Current read:
- No service, queue, transcription, embedding, or RF intervention needed.
- Incident extraction truncation is the main regression this run, but it is a recurrence of an existing watch item rather than a new class of failure.
- Verifier/ownership narrowing also worsened but remains within the previously observed pattern.
- Single-call category inheritance remains the clearest targeted incident-display fix candidate.

### 2026-06-07 01:22Z Check

Window: `2026-06-06T17:22Z` to `2026-06-07T01:22Z`; compared with `2026-06-06T09:22Z` to `2026-06-06T17:22Z`.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active.
- Queues healthy: transcription queue `0`, pending transcriptions `0`, pending audio `0`.
- Throughput healthy: ingest about `8.9 calls/min`, transcribe about `9.2 calls/min`, average transcription realtime factor `0.098`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`, pending `0`.
- Calls rose from `2,858` to `4,135`; latest-window incident yield was `8.71 incidents / 1,000 calls`.
- AI routing remained correct: request model `qwen/qwen3.6-35b-a3b@q8_0`, response model `paxan-qwen3.6-35b-a3b@q8_0`.
- AI truncation improved from `16/262` to `9/280`; sampled failures were incident extraction at `max_tokens=1800`.
- Verifier narrowing rose from `88` to `100` retention mismatches.
- Single-call incidents remained common: `18/36` incidents had one linked call.
- Top reject buckets: only one call matches title/detail `29`, single-call lacks strong event signal `18`, only one call supports title/detail anchor `18`, only zero calls match title/detail `16`, medical transport lacks parent event `14`.
- Transcript quality remained acceptable: `3,795` OK complete calls, `262` repetitive, `39` empty, `27` too short, `10` numeric noise, `1` marker-only, `1` overexpanded.

Correlated category evidence:
- Single-call emergency events continued to inherit source categories: incident `2938` (`Vehicle Fire on US Highway 64 near Exit 11 causing traffic issues`) category `police`; incident `2940` (`Fire Alarm at 3614 L Wood Lane`) category `police`; incident `2980` (`Medical assist, 9123 N Agree Valley Rd`) category `fire`; incident `2974` (`Unresponsive female at 1704 Laurel Ln, EMS/Fire responding`) category `fire`.
- Mixed-source category behavior continued to look better: incident `2950` (`Assault at 630 W 14th St Court`) category `ems` with retained categories `police,ems`; incident `2939` (`Possible Overdose at Petco, Palmhaf Pkwy`) category `ems` with retained categories `police,ems`; incident `2972` (`Medical assist at 205 Broad St for unconscious male`) category `ems` with retained categories `police,ems`.

Current read:
- No service, queue, transcription, embedding, or RF intervention needed.
- Truncation improved from the previous run but remains recurring.
- Verifier/ownership narrowing remains elevated and should stay open.
- Category evidence remains consistent: preserve mixed-source event-category behavior, but fix single-call event category inheritance.

### 2026-06-07 09:22Z Check

Window: `2026-06-07T01:22Z` to `2026-06-07T09:22Z`; compared with `2026-06-06T17:22Z` to `2026-06-07T01:22Z`.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active.
- Queues effectively healthy: transcription queue `0`, pending transcriptions `1`, pending audio `10s`.
- Throughput healthy at low overnight volume: ingest/transcribe both about `0.9 calls/min`, average transcription realtime factor `0.086`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`, pending `0`.
- Calls fell from `4,136` to `2,808`; latest-window incident yield was `7.48 incidents / 1,000 calls`.
- AI routing remained correct: request model `qwen/qwen3.6-35b-a3b@q8_0`, response model `paxan-qwen3.6-35b-a3b@q8_0`.
- AI truncation improved slightly from `9/280` to `8/253`; sampled failures were incident extraction at `max_tokens=1800`.
- Verifier narrowing stayed flat at `100` retention mismatches.
- Single-call incidents were very dominant in the current endpoint view: `17/21` incidents had one linked call.
- Top reject buckets: only one call matches title/detail `17`, single-call lacks strong event signal `16`, unsupported emergency/event title/detail `14`, only one call supports title/detail anchor `13`, medical transport lacks parent event `12`.
- Transcript quality remained acceptable: `2,599` OK complete calls, `157` repetitive, `23` too short, `21` empty, `7` numeric noise, `1` marker-only.

Correlated category evidence:
- Single-call category inheritance continues: incident `3003` (`Possible assault/beat-up female on McConnell School Ln`) category `fire`; incident `2999` (`Possible DOA infant at 612 Maple St near Poplar St`) category `fire`; incident `3002` (`MVC involving truck and power pole on County Rd 616`) category `police`; incident `2998` (`MVC with injuries at 2806 Emicola Hwy near Appling and Stewart Creek`) category `fire`; incident `2991` (`Medical Emergency at 410 Market Street`) category `police`.
- Mixed-source categorization remains better: incident `2996` (`Medical Emergency Chest Pain at 5007 Hunter Village Dr`) category `ems` with retained categories `fire,ems`; incident `2993` (`MVC with Injuries at 3700 Block Call of Unab`) category `traffic` with retained categories `police,fire`.

Current read:
- No service, queue, transcription, embedding, or RF intervention needed.
- This run reinforces existing watch items without adding a new failure class.
- Incident extraction truncation remains recurring but improved slightly.
- Verifier/ownership narrowing remains elevated and flat.
- Single-call category inheritance remains the clearest targeted incident-display fix candidate.

### 2026-06-07 User Review Correction

Superseded:
- The prior `single-call category inheritance` fix candidate is removed from the active list. Single-call incidents should stay TG-category derived. Future category review should focus on multi-call presentation only: source call cards/sub-cards stay TG-derived, while the parent incident can be evaluated separately when there is mixed-source event evidence.

New correlated evidence:
- Incident `2948` / `INC-20260606-014`, `Vehicle Fire/Explosion on I-75 North at Mile 14`, currently owns only fire calls `583813`, `583869`, and `583907`.
- Related calls in the same event window include police/sheriff/mutual-aid traffic and response calls: `583831`, `583839`, `583885`, `584025`, `584085`, `584109`, `584139`, `584397`, `584661`, and `584663`.
- Audit rows repeatedly recognized broader compatible candidate sets and redirected them to `INC-20260606-014`, but the incident's owned/displayed call set stayed narrow. Examples include audit rows `19607`, `19611`, `19624`, `19630`, `19636`, and `19643`; several later rows rejected broader sets with `only 1 call(s) match the incident title/detail`, `only 0 call(s) match the incident title/detail`, or duplicate-existing-incident evidence.
- The location extractor/geocoder did not preserve the definitive mile-marker anchor. Calls `583813`, `583869`, and `583907` extracted only `Interstate 75`; cache query `Interstate 75, Hamilton County, TN` returned `precision=none`, `confidence=0`, `latitude=0`, `longitude=0`. Police/mutual-aid rows mentioning `13.5`, `14.4`, `exit 11`, and `Lee Highway` were mostly unlocated or mis-extracted.

Current read:
- The I-75 vehicle-fire/fireworks event is a supported fix candidate for cross-agency incident collation and highway mile-marker geocoding.
- This is not evidence for changing single-call category semantics.

Repair/fix follow-up:
- Incident `2948` was manually repaired on OT at audit row `20225`; linked calls increased from `3` to `22`, covering fire dispatch/fireground/tac, Hamilton police/sheriff, Cleveland law mutual aid, traffic diversion/shutdown, and EMS burn transport evidence.
- Local code fix study points: preserve already-owned target-incident calls when a redirected candidate updates an existing incident, keep same-pass `existingByKey` state current after an upsert, and retain bounded response-continuation calls for major highway incidents when at least one exact event anchor is already present.

### 2026-06-07 17:22Z Check

Window: `2026-06-07T09:22Z` to `2026-06-07T17:22Z`; compared with `2026-06-07T01:22Z` to `2026-06-07T09:22Z`.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active.
- Queues healthy: transcription queue `0`, pending transcriptions `0`, pending audio `0`.
- Throughput healthy: ingest about `7.0 calls/min`, transcribe about `7.4 calls/min`, average transcription realtime factor `0.120`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`, pending `0`, model `text-embedding-nomic-embed-text-v1.5`.
- Calls fell from `2,804` to `1,990`; latest-window incident yield was `11.06 incidents / 1,000 calls`.
- AI routing remained correct: request model `qwen/qwen3.6-35b-a3b@q8_0`, response model `paxan-qwen3.6-35b-a3b@q8_0`.
- AI truncation worsened from `8/253` to `21/214`; all failures were `finish_reason=length` on incident extraction at `max_tokens=1800`.
- Verifier narrowing improved numerically from `100` to `57` retention mismatches, but ownership narrowing remains visible in audit rows.
- Single-call incidents remained common: current endpoint view had `22` incidents, with recent single-call accepted examples including `3018` / `INC-20260607-HWY11-MVC`.
- Top reject buckets: medical transport lacks parent event `36`, single-call lacks strong event signal `22`, unsupported emergency/event title/detail `22`, only one call matches title/detail `12`, only one call supports title/detail anchor `10`.
- Transcript quality remained acceptable: `1,802` OK complete calls, `142` repetitive, `21` empty, `19` too short, `6` numeric noise.

Correlated evidence:
- The I-24/Barn Nursery split remains live: incident `3016` owns call `595053`; incident `3017` owns calls `595043,595065`.
- Existing rows for the split incident were not reprocessed after the `8dfae80` extractor deployment: call `595053` still only has weak location `Interstate 24` with `provider=none`, and calls `595043`/`595065` still have no location/anchor rows.
- Audit rows `20374` and `20375` show the system later considered wider sets including `595043`, `595053`, `595063`, `595065`, and `595089`, but rejected them after existing-incident overlap because the remaining unclaimed call validation reduced to noisy/routine evidence for `INC-20260607-022`.
- New incident `3018` (`MVC Hwy 11 N near CR 326, elderly males injured`) was accepted as a legitimate single-call event, but location extraction is noisy: call `595403` produced weak/false anchors like `1046 On Highway`, `County Near County Road`, and `Mcminn On The Road`; only `Highway 11` geocoded weakly at confidence `0.35`.

Current read:
- Service, queue, transcription, embedding, and RF health are fine; no infrastructure intervention needed.
- The deployed marker/landmark extractor should help future calls, but it does not repair already-processed calls or already-split incidents.
- Active fix candidates are now narrower: add a safe post-processing/incident repair path for known incident splits, and tighten noisy rural highway/location extraction around numeric dispatch codes.
- Incident extraction truncation regressed this run and should remain open, but the more actionable incident-quality issue is still structured ownership/anchor handling.

### 2026-06-08 01:23Z Check

Window: `2026-06-07T17:23Z` to `2026-06-08T01:23Z`; compared with `2026-06-07T09:22Z` to `2026-06-07T17:22Z`.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active.
- Queues healthy: transcription queue `0`, pending transcriptions `0`, pending audio `0`.
- Throughput healthy: ingest about `6.9 calls/min`, transcribe about `7.0 calls/min`, average transcription realtime factor `0.097`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`, pending `0`, model `text-embedding-nomic-embed-text-v1.5`.
- Calls rose from `1,990` to `2,883`; latest-window incident yield was `17.34 incidents / 1,000 calls`.
- AI routing remained correct: request model `qwen/qwen3.6-35b-a3b@q8_0`, response model `paxan-qwen3.6-35b-a3b@q8_0`; no successful local-route rows observed.
- AI truncation improved from `21/214` to `11/307`; all failures were `finish_reason=length` on incident extraction at `max_tokens=1800`.
- Evidence verifier truncation was `0`; average reviewed calls `4.76`, added `60`, dropped `25`.
- Verifier/ownership narrowing rose from `57` to `87` retention mismatches, still below the earlier `100` high-water mark.
- Incident call-count distribution for current-window incidents: `30` single-call, `11` two-call, `5` three-call, `3` four-call, and `1` six-call incidents.
- Top reject buckets: single-call lacks strong event signal `31`, medical transport lacks parent event `20`, only one call matches title/detail `19`, only one call supports title/detail anchor `18`, noisy/routine single-call source `12`, only zero calls match title/detail `12`.
- Transcript quality remained acceptable: `2,653` OK complete calls, `173` repetitive, `28` empty, `20` too short, `8` numeric noise, `1` marker-only.

Correlated evidence:
- Multi-call MVC grouping has some good examples: `3045` / `INC-20260607-MAHAN-GAP-MVC` retained `6` calls; `3054` / `INC-20260607-009` retained `4` calls; `3035` / `INC-20260607-040` retained `4` calls; `3024` / `INC-20260607-WALKER-VALLEY-MVC` retained `3` calls.
- Single-dispatch true positives are still accepted, as expected: `3056` allergic reaction, `3055` smoke detector alarm, `3004` fall victim, `3053` Walgreens fire alarm, `3050` MVC with injuries, and multiple single-call MVC/fire alarm rows.
- Suspicious title/location noise persists in some accepted incidents: `3044` (`Fall/MVC Gaines Way & Chilly Ct`), `3033` (`Fire at 4538 Clonts / Black Cadillac Pursuit`), and `3014` retitled to `Burglar Alarm at 308 W 47th St` after previously unrelated title history. These support continued watch on incident title drift and noisy location extraction, not immediate broad logic changes.
- New reconciliation audit rows show the deployed strong-anchor path working in at least some cases: `20736` redirected to active incident `INC-20260607-004` with `shared strong anchor x3`, and `20726` updated the incident with reconciled active-incident evidence.

Current read:
- No service, queue, transcription, embedding, or RF intervention needed.
- Incident extraction truncation improved materially from the previous window but remains a recurring watch item.
- Evidence-verifier truncation is effectively solved in this window (`0`), while verifier/ownership narrowing remains a watch item.
- The system should keep baking without code changes from this run alone; the next concrete fix still looks like a safe repair/reprocess path for known split incidents plus targeted cleanup for noisy rural/highway location extraction.

### 2026-06-08 09:24Z Check

Window: `2026-06-08T01:25Z` to `2026-06-08T09:25Z`; compared with `2026-06-07T17:23Z` to `2026-06-08T01:23Z`.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active.
- Queues effectively healthy: transcription queue `0`, pending transcriptions `1`, pending audio `13s`.
- Throughput healthy at lower overnight volume: ingest/transcribe both about `2.0 calls/min`, average transcription realtime factor `0.127`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`, pending `0`, model `text-embedding-nomic-embed-text-v1.5`.
- Calls fell from `2,883` to `1,595`; latest-window incident yield was `11.29 incidents / 1,000 calls`.
- AI routing remained correct: request model `qwen/qwen3.6-35b-a3b@q8_0`, response model `paxan-qwen3.6-35b-a3b@q8_0`; no successful local-route rows observed.
- AI truncation worsened from `11/307` to `31/279`; all failures were `finish_reason=length` on incident extraction at `max_tokens=1800`.
- Evidence verifier truncation remained solved for this window: average truncated calls `0`, max truncated calls `0`.
- Verifier/ownership narrowing improved from `87` to `68` retention mismatches.
- Incident call-count distribution for current-window incidents: `11` single-call, `3` two-call, `2` three-call, and `2` four-call incidents.
- Top reject buckets: single-call lacks strong event signal `57`, only one call supports title/detail anchor `23`, only one call matches title/detail `21`, noisy/routine single-call source `16`, medical transport lacks parent event `15`.
- Transcript quality remained acceptable: `1,455` OK complete calls, `95` repetitive, `22` too short, `16` empty, `6` numeric noise, `1` pending.

Correlated evidence:
- Legitimate multi-call incidents were retained: `3061` (`Unconscious Patient 7450 Davis Mill Circle`) with `4` calls, `3063` (`Pursuit at Ringgold Rd and Germantown`) with `4` calls, `3069` (`BOLO Black Ford Focus on Booth Road/Howard Ave`) with `2` calls, and `2995` (`Unconscious person at 3 Caramels Circle`) with `3` calls.
- Legitimate single-dispatch events were accepted, consistent with current policy: pursuit `3059`, fall `3066`, fall `3060`, Bates MVC `3057`, and allergic reaction `2999`.
- Some accepted titles still show noisy location/transcription artifacts: `3068` (`Scashy St N`), `2995` (`3 Caramels Circle`), and `2997` (`206 Gila Drive`) should stay in the noisy-location/title-drift watch lane.
- The rejection spike was dominated by single-call weak/noisy candidates rather than obvious missed single-dispatch emergencies in the sampled top rows. Examples include audit rows with locations like `thirteen beach st`, `raster ave`, `he has cut his way`, and bare `i 24`/`i 75`.

Current read:
- No service, queue, transcription, embedding, or RF intervention needed.
- Evidence-verifier truncation remains effectively solved, and ownership narrowing improved.
- Incident extraction truncation is the meaningful regression this run (`31` length failures) and should be prioritized if it repeats in the next window.
- The system can keep baking for incident collation, but the active fix candidate is incident-extraction prompt/budget compaction plus continued noisy-location/title cleanup.

### 2026-06-08 17:24Z Check

Window: `2026-06-08T09:25Z` to `2026-06-08T17:25Z`; compared with `2026-06-08T01:25Z` to `2026-06-08T09:25Z`.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active. API is currently listening on port `8080`.
- Queues effectively healthy: transcription queue `0`, pending transcriptions `1`, pending audio `14s`.
- Throughput healthy under higher daytime volume: ingest about `11.2 calls/min`, transcribe about `11.3 calls/min`, average transcription realtime factor `0.122`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`, pending `0`, model `text-embedding-nomic-embed-text-v1.5`.
- Calls rose from `1,592` to `3,260`; latest-window incident yield was `9.82 incidents / 1,000 calls` using the current endpoint incident count.
- Historical incident-count caveat repeated: the preceding closed window now reports `11` incidents through `/quality-check`, while the earlier live check saw a higher count. Use audit creates/updates/rejects for trend confidence.
- Audit activity rose with traffic: current audit rows show `55` creates, `71` updates, and `193` rejects since `09:25Z`; the quality-check snapshot reported `54` creates and `73` updates because it was generated just before the newest audit row.
- AI routing remained correct: `335` successful rows used request model `qwen/qwen3.6-35b-a3b@q8_0` and response model `paxan-qwen3.6-35b-a3b@q8_0`; no successful local-route rows observed.
- AI truncation improved sharply from `31/279` to `2/339` length failures. Additional failures were transient model availability/API errors: `3` `No models loaded` rows and `1` HTTP 500 around `16:56Z-16:57Z`, followed by successful rows through `17:25Z`.
- Evidence verifier truncation remains effectively solved: average truncated calls `0.02`, max truncated calls `1`.
- Verifier/ownership narrowing rose from `68` to `126` retention mismatches; added/dropped calls also rose from `27/14` to `59/23`.
- Incident call-count distribution for current-window incidents: `21` single-call, `4` two-call, `6` three-call, and `1` eight-call incident.
- Top reject buckets: single-call lacks strong event signal `42`, unsupported title/detail `26`, medical transport lacks parent event `25`, only one call matches title/detail `20`, noisy/routine single-call source `16`, concrete-location conflict `10`.
- Transcript quality remained acceptable: `2,927` OK complete calls, `232` repetitive, `49` empty, `45` too short, `5` numeric noise, `1` pending, `1` marker-only.

Correlated evidence:
- Multi-call emergency grouping continues to work in representative cases: `3080` (`Stroke Alert at Live Care Center`) retained `3` calls, `3074` (`Unconscious Patient at Studio 6`) retained `3`, `3079` (`Seizure at 328 E Main St, Apt 104`) retained `2`, and one current-window incident retained `8` calls.
- Legitimate single-dispatch incidents are still accepted, consistent with current policy: `3059` vehicle wreck, `3061` CPR/resuscitation, `3069` vehicle pursuit, `3082` automatic fire alarm, `3085` MVC, and `3073` MVC injuries.
- Ownership narrowing remains visible in audit rows even when the resulting incident is plausible: examples include `21521` (`verifier retained 6 call(s), final ownership retained 3`), `21523` (`4` to `3`), `21526` (`3` to `1`), and `21530` (`2` to `1`).
- The location/title rejection lane still has noisy but useful examples: audit `21503` rejected two fire-alarm candidates because only one supported the concrete location, while audit `21504` rejected a five-call highway set because only one matched the title/detail. These support continued targeted auditing, not a broad rejection-threshold change.
- The transient `No models loaded` incident did not affect successful routing after recovery; treat it as a watch signal for LM Studio/Paxan availability, separate from the local embedding health item.

Current read:
- No service, queue, transcription, embedding, RF, or code intervention is supported by this run alone.
- Incident extraction truncation is no longer the immediate regression; it improved from `31` to `2` length failures.
- Verifier/ownership narrowing remains the main incident-quality watch item, but this run did not surface a fresh specific split comparable to the I-75 or I-24 cases.
- The system should keep baking without code changes from this run alone; next actionable work remains safe repair/reprocess tooling for known split incidents and targeted ownership/anchor audits with concrete call sets.

### 2026-06-09 01:24Z Check

Window: `2026-06-08T17:25Z` to `2026-06-09T01:25Z`; compared with `2026-06-08T09:25Z` to `2026-06-08T17:25Z`.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active.
- Queues healthy: transcription queue `0`, pending transcriptions `0`, pending audio `0`.
- Throughput healthy: ingest about `6.6 calls/min`, transcribe about `7.0 calls/min`, average transcription realtime factor `0.079`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`, pending `0`, model `text-embedding-nomic-embed-text-v1.5`.
- Calls rose from `3,261` to `3,925`; latest-window incident yield was `10.19 incidents / 1,000 calls` using the current endpoint incident count.
- AI routing remained correct: `360` successful rows used request model `qwen/qwen3.6-35b-a3b@q8_0` and response model `paxan-qwen3.6-35b-a3b@q8_0`; no successful local-route rows observed.
- AI truncation cleared: current quality check showed `358/358` successes, `0` failures, and `0` truncations. Direct `lm_usage` rows since `17:24Z` showed `360` successful `finish_reason=stop` rows and no failures.
- Evidence verifier truncation stayed solved: average truncated calls `0`, max truncated calls `0`.
- Verifier/ownership narrowing rose again from `126` to `148` retention mismatches; added/dropped calls rose from `59/23` to `96/28`.
- Incident call-count distribution for current-window incidents: `21` single-call, `12` two-call, `5` three-call, and `2` four-call incidents.
- Top reject buckets: only one call matches title/detail `40`, medical transport lacks parent event `27`, single-call lacks strong event signal `26-27`, concrete-location conflict `18`, only one call supports title/detail anchor `17`, unsupported title/detail `13-14`.
- Transcript quality remained acceptable: `3,580` OK complete calls, `259` repetitive, `41` too short, `35` empty, `8` numeric noise, and `2` marker-only.

Correlated evidence:
- Representative multi-call incident grouping still works: `3074` (`CPR in progress at 2240 Curtis Ln SE`) retained `4` calls; `3071` (`MVC with injuries at NW 25th St and Key St`) retained `3`; `3108` (`Commercial fire alarm at 2501 Market Street`) retained `3`; `3104` (`MVC at 9500 Dallas Hollow Rd`) retained `2`; `3075` (`Vehicle crash with injuries on Hwy 411 at Thomas Rd`) retained `2`.
- Legitimate single-dispatch incidents are still accepted, consistent with current policy: `3080` vehicle fire, `3094` vehicle off roadway, `3093` two-vehicle accident, `3098` hazmat, `3099` MVC with injuries, `3079` unconscious patient, and `3086` stroke response.
- Reconciliation has positive examples: audit rows for `INC-NEW-MVC-DALLAS` repeatedly redirected compatible candidates to the active incident with `shared call id`, `shared strong anchor x1`, time proximity, and compatible category.
- Ownership narrowing remains the main recurring signal: examples include audit rows `22040` (`3` retained to `2`), `22036` (`4` to `3`), `22030` (`4` to `3`), `22025` (`7` to `5`), `21976` (`3` to `1`), and `21966` (`5` to `1`).
- The concrete-location conflict lane stayed elevated but sampled rows look like mixed-location protection rather than an obvious missed join: examples include `INC-NEW-FIRE-WENTWORTH` rejects where one call supported `Wentworth` while others cited `Spring Creek Rd`, `Treywind Circle`, or noisy text like `reach anyone that way`.

Current read:
- No service, queue, transcription, embedding, RF, or code intervention is supported by this run alone.
- Incident extraction truncation should be downgraded to monitor after a clean zero-failure window.
- Verifier/ownership narrowing remains the largest incident-quality watch item, but this run does not add a new specific repair case.
- The system should keep baking without code changes from this run alone; next actionable work remains safe repair/reprocess tooling for known split incidents and targeted ownership/anchor audits with concrete call sets.

### 2026-06-09 09:24Z Check

Window: `2026-06-09T01:25Z` to `2026-06-09T09:25Z`; compared with `2026-06-08T17:25Z` to `2026-06-09T01:25Z`.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active.
- Queues healthy: transcription queue `0`, pending transcriptions `0`, pending audio `0`.
- Throughput healthy at lower overnight volume: ingest about `2.4 calls/min`, transcribe about `2.5 calls/min`, average transcription realtime factor `0.090`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`, pending `0`, model `text-embedding-nomic-embed-text-v1.5`.
- Calls fell from `3,927` to `2,083`; latest-window incident yield was `16.32 incidents / 1,000 calls` using the current endpoint incident count.
- AI routing remained correct: `253` successful rows used request model `qwen/qwen3.6-35b-a3b@q8_0` and response model `paxan-qwen3.6-35b-a3b@q8_0`; no successful local-route rows observed.
- AI truncation remained cleared: quality check and direct `lm_usage` rows both showed `0` failures and `0` truncations.
- Evidence verifier truncation remained low: average truncated calls `0.03`, max truncated calls `3`.
- Verifier/ownership narrowing improved sharply from `148` to `45` retention mismatches; added/dropped calls fell from `96/28` to `53/31`.
- Incident call-count distribution for current-window incidents: `20` single-call, `10` two-call, `2` three-call, and `2` four-call incidents.
- Top reject buckets: single-call lacks strong event signal `37`, only one call supports title/detail anchor `19`, medical transport lacks parent event `17`, only one call matches title/detail `17`, concrete-location conflict `13`, unsupported title/detail `11`.
- Transcript quality remained acceptable: `1,882` OK complete calls, `147` repetitive, `24` too short, `21` empty, and `9` numeric noise.

Correlated evidence:
- Representative multi-call grouping still works: `3060` (`Smoke Alarm at Refreshment Ln`) retained `4` calls; `3116` (`Traffic accident on I-24 Westbound`) retained `4`; `3125` (`CPR in progress at 15556 Olds Road`) retained `3`; `3128` overdose retained `2`; `3141` wires down retained `2`.
- Legitimate single-dispatch incidents are still accepted, consistent with current policy: `3133` vehicle fire, `3140` small accident, `3134` fall, `3124` fall, `3121` CPR, `3118` fire response, and `3111` fire response.
- Reconciliation has positive examples: `INC-NEW-FIRE-ORCHARDGNOME` candidates redirected to the active incident with `shared call id`, `shared strong anchor x1`, time proximity, and compatible category.
- New highway marker miss: incident `3133` / `INC-350MM-VFIRE`, call `622585`, transcript says `mile marker 350, high 75 northbound ... vehicle fire ... right hand shoulder`. The incident title captured the event, but the call has no `call_locations` or `call_anchors`, so `high 75 northbound` was not normalized to an I-75 mile-marker anchor.
- Noisy highway/lane extraction also persists: incident `3135` / `INC-46-LANE-BLK` used calls `622555`, `622611`, and `622615`; location rows include false anchors like `M Gonna Close Down Lane` and `Is There A Lane`, which geocoded to provider `none`, confidence `0`.
- The wires-down location is partially extracted but not mapped: call `623167` produced `Intersection Of Alibam Avenue / Oats Highway` and separate weak locations, both geocode provider `none`, confidence `0`. This looks like either transcription spelling drift or a local-road naming gap, not a service failure.

Current read:
- No service, queue, transcription, embedding, RF, or broad incident-pipeline intervention is supported by this run alone.
- Incident extraction truncation remains quiet; keep it at monitor.
- Verifier/ownership narrowing improved materially and should remain a watch item, not the active problem this run.
- New supported fix candidate: expand highway marker extraction for speech variants such as `high 75` / `highway 75` and marker-before-highway phrasing, then consider safe post-processing for already accepted incidents like `3133`.

### 2026-06-09 17:25Z Check

Window: `2026-06-09T09:26Z` to `2026-06-09T17:26Z`; compared with `2026-06-09T01:25Z` to `2026-06-09T09:25Z`.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active.
- Queues effectively healthy: transcription queue `1`, pending transcriptions `1`, pending audio `7-18s`; no queue pressure.
- Throughput healthy under daytime volume: ingest about `7.7-7.8 calls/min`, transcribe about `7.9 calls/min`, average transcription realtime factor `0.112`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`, pending `0`, model `text-embedding-nomic-embed-text-v1.5`.
- Calls rose from `2,083` to `3,336`; latest-window incident yield was `10.19 incidents / 1,000 calls` using the current endpoint incident count.
- AI routing remained correct: direct `lm_usage` rows since `09:25Z` showed request model `qwen/qwen3.6-35b-a3b@q8_0` and response model `paxan-qwen3.6-35b-a3b@q8_0` for all `258` successful rows; no successful local-route rows observed.
- AI truncation remained operationally cleared: quality-check showed `257/257` successes, `0` failures, and `0` truncations. Direct rows included `28` successes tagged `recovered_after_truncation_split`, with no `finish_reason=length` failures.
- Evidence verifier truncation remained low: average truncated calls `0.05`, max truncated calls `3`.
- Verifier/ownership narrowing rose from `45` to `62` retention mismatches; added/dropped calls were `55/18`.
- Incident call-count distribution for current-window incidents: `21` single-call, `7` two-call, `3` three-call, `1` four-call, `1` five-call, and `1` six-call incident.
- Top reject buckets: single-call lacks strong event signal `51`, medical transport lacks parent event `33`, unsupported title/detail `29`, only one call matches title/detail `27`, concrete-location conflict `12`, only one call supports title/detail anchor `8`.
- Transcript quality remained acceptable: `3,039` OK complete calls, `201` repetitive, `48` too short, `35` empty, `10` numeric noise, `2` pending, and `1` marker-only.

Correlated evidence:
- New clear split incident: `3149` / `INC-20260609-014` (`Unconscious Call 5946 Brainerd Rd`) owns police call `624429`, while `3150` / `INC-20260609-015` (`Unconscious Male 5946 Brainerd Rd`) owns fire calls `624411` and `624439`.
- The transcripts describe the same real-world event within `66s`: `624411` dispatches Squad 13 to `5946 Brainer Road` for an unconscious call; `624429` dispatches police for an unconscious party outside `5946 Brinard Road`, male by the entrance sign, blue shirt/shorts; `624439` says PD is also responding to the unconscious male.
- Location extraction/geocoding did not provide a strong shared anchor: `624411` only had weak `Loin Brainer Road` / `Brainer Road`; `624429` had weak `5946 Brinard Road`; `624439` had weak `Brainer Road`. All related geocode rows were provider `none`, confidence `0`.
- Audit rows show the system repeatedly saw broader related sets but ended with ownership narrowing: `22478` considered `624411`, `624429`, and `624439` together but classified as medical transport/logistics; `22482` and `22486` rejected `624429 + 624439` as having `0` corroborating related calls; later rows updated `INC-014` with `624429` and `INC-015` with `624411,624439`.
- Representative positive grouping still exists in the same window: `3170` brush fire retained `6` calls, `3160` fire alarm retained `5`, `3146` lift assist retained `4`, and `3172` fire alarm retained `3`.

Current read:
- No service, transcription, embedding, RF, or AI routing intervention is needed.
- Incident extraction truncation remains quiet; keep it at monitor.
- The meaningful new signal is incident collation/ownership with weak/misspelled locations: `3149`/`3150` is a fresh same-event split with concrete call evidence.
- Supported fix direction: continue the structured model-derived event-location anchor work and add a safe repair/reprocess path for accepted split incidents; avoid brittle phrase-specific regex.

### 2026-06-10 01:27Z Check

Window: `2026-06-09T17:27Z` to `2026-06-10T01:27Z`; compared with `2026-06-09T09:26Z` to `2026-06-09T17:26Z`.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active.
- Queues healthy: transcription queue `0`, pending transcriptions `0`, pending audio `0`; no queue pressure.
- Throughput healthy: ingest about `8.9 calls/min`, transcribe about `9.0 calls/min`, average transcription realtime factor `0.118`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`, pending `0`, model `text-embedding-nomic-embed-text-v1.5`.
- Calls rose from `3,336` to `3,751`; latest-window incident yield was `13.33 incidents / 1,000 calls` using the current endpoint incident count.
- AI routing remained correct: direct `lm_usage` rows since `17:27Z` showed `336` successful rows using request model `qwen/qwen3.6-35b-a3b@q8_0` and response model `paxan-qwen3.6-35b-a3b@q8_0`; no successful local-route rows observed.
- AI truncation remained operationally cleared: quality-check showed `335/335` successes, `0` failures, and `0` truncations. Direct rows included `29` successes tagged `recovered_after_truncation_split`, with no `finish_reason=length` failures.
- Evidence verifier truncation remained low: average truncated calls `0.05`, max truncated calls `2`.
- Verifier/ownership narrowing worsened from `62` to `113` retention mismatches; added/dropped calls were `66/33`.
- Incident call-count distribution for current-window incidents: `35` single-call, `9` two-call, `3` three-call, `2` four-call, and `1` six-call incident.
- Top reject buckets: single-call lacks strong event signal `32`, only one call matches title/detail `27`, only one call supports title/detail anchor `18`, medical transport lacks parent event `15`, noisy/routine single-call source `14`, unsupported title/detail `14`, single-call lacks concrete anchor `12`.
- Transcript quality remained acceptable: `3,455` OK complete calls, `201` repetitive, `43` empty, `43` too short, `6` numeric noise, `2` marker-only, and `1` pending.

Correlated evidence:
- New high-signal retention miss: incident `3220` / `INC-NEW-TRAFFIC-I75-MM3` (`MVC with injuries on I-75 SB at MM3, exit 2-3`) owns only call `637049`, even though audit row `23191` redirected candidate `INC-NEW-MVC-I75-MM3` with calls `637003`, `637049`, and `637117` to the active incident using `shared call id`, `shared strong anchor x1`, time proximity, and compatible category.
- The three calls look like one event: `637003` gives location `three mile marker, Interstate 75 Southbound, between 153 and Eastwood Road`; `637049` gives `mile marker 3, I-75 southbound, between exit 2 and exit 3` with injuries; `637117` gives patient details for a juvenile female with left leg pain and an adult male with left hand bleeding.
- The dropped location call `637003` is not owned by another incident. Only `637049` is linked in `incident_calls`.
- Anchor extraction explains the miss: `637049` has strong `highway_mile_marker` anchor `i-75|mm:3`, but `637003` only has weak `location_hint` anchors for `Interstate 75` and `153 And Eastwood Road`; `637117` has no location anchor. Geocodes for the I-75 rows were weak (`0.35`) or none.
- Audit rows `23175` and `23192` show the verifier retained `2` then `3` calls, but the final accepted incident retained only `637049`; this looks like validation/retention pruning after reconciliation rather than duplicate ownership protection.
- Positive grouping still exists elsewhere in the window: `3195` stroke response retained `6` calls, `3206` residential fire retained `4`, `3219` medical assist retained `4`, and `3187` Broad St crash retained `3`.

Current read:
- No service, transcription, embedding, RF, or AI routing intervention is needed.
- Incident extraction truncation remains quiet; the split fallback appears to be absorbing would-be length failures.
- Verifier/ownership narrowing is the main active incident-quality issue again, now with a fresh MVC/highway-mile-marker case that matches the current fix direction.
- Supported fix direction: prioritize model-derived event-location anchors and safe incident repair/reprocess for accepted incidents; this case should be used alongside `3149`/`3150` as a regression fixture.

### 2026-06-10 Machete Title Check

Observed:
- User reported two current incidents titled with `machete` despite retained transcripts not appearing to say it.
- Confirmed incident `3144` / `INC-20260609-004` title `Subject with Machete on Georgetown Rd/Blackburn` retains calls `636903` and `636931`; neither retained transcript mentions machete. They describe a white male with long hair/bun, neck brace, no shirt, walking toward the liquor store on Georgetown.
- Confirmed incident `3171` / `INC-20260609-003` title `Subject with Machete at 2345 Blackburn Rd SE` retains call `636785`; the transcript is a suicide-threat call at `2345 Blackburn Road SE` with vehicles/children context, not a machete call.
- Found unlinked source call `636833` on Bradley Sheriff dispatch: `male possibly had a machete ... walking down the road ... put it away whenever the caller drove by`. This call is not owned by either machete-titled incident.
- Audit evidence shows likely title/source drift after pruning: `3171` was accepted with `verifier retained 4 call(s), final ownership retained 1`; `3144` was accepted with wider sets including `636879`, `636903`, and `636931`, then later narrowed to `636903`/`636931`.

Current read:
- This is not transcription quality failure: one transcript has the machete evidence, but it was not retained.
- This is an incident title/detail support failure after verifier/ownership pruning. The system can leave a title/detail fact sourced from dropped calls, or conflate nearby public-safety calls into the retained incident narrative.
- Supported fix direction: after final retained calls are known, validate or regenerate title/detail from only retained transcripts; if a key fact such as weapon/event type is unsupported by retained calls, either retain the supporting call or retitle the incident.

### 2026-06-10 09:29Z Check

Window: `2026-06-10T01:29Z` to `2026-06-10T09:29Z`; compared with `2026-06-09T17:27Z` to `2026-06-10T01:27Z`. This window includes about one hour before and seven hours after deploy commit `2a99716`.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active.
- Queues healthy: transcription queue `0`, pending transcriptions `0`, pending audio `0`; no queue pressure.
- Throughput healthy at lower overnight rate: ingest about `2.4 calls/min`, transcribe about `2.5 calls/min`, average transcription realtime factor `0.140`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`, pending `0`, model `text-embedding-nomic-embed-text-v1.5`.
- Calls fell from `3,751` to `1,938`; latest-window incident yield was `7.74 incidents / 1,000 calls` using the current endpoint incident count.
- AI routing remained healthy at endpoint level: quality check showed `248/248` successes, `0` failures, and `0` truncations.
- Evidence verifier truncation stayed solved: average truncated calls `0`, max truncated calls `0`.
- Verifier/ownership narrowing improved from `113` to `57` retention mismatches, but post-deploy accepted rows still include narrowing such as `verifier retained 7 call(s), final ownership retained 1`, `5 -> 1`, `4 -> 1`, and `3 -> 1`.
- Incident call-count distribution for current-window incidents: `6` single-call, `3` two-call, `3` three-call, `1` four-call, `1` six-call, and `1` seven-call incident.
- Top reject buckets: medical transport lacks parent event `19`, only one call supports title/detail anchor `19`, single-call lacks strong event signal `19`, only one call matches title/detail `18`, noisy/routine single-call source `13`, unsupported title/detail `12`.
- Transcript quality remained acceptable: `1,771` OK complete calls, `121` repetitive, `26` too short, `17` empty, `2` numeric noise, and `1` marker-only.

Correlated evidence:
- The deployed retention fix appears to address the exact `3220` / I-75 MM3 class it was written for: audit rows still redirect `INC-NEW-TRAFFIC-I75-MM3` to the active incident, and the window-level retention mismatch count is lower than the prior run.
- A new residual same-event miss remains: incident `3230` / `INC-6220-SHELF-DISORDER` owns only call `641065`, while audit rows `23451` and `23453` show the verifier retained `3` then `4` calls but final ownership retained `1`.
- The dropped calls look related on transcript evidence: `641061` says a silver Lexus from `6220 Shaliford` / `Roca`, female party Briggs, possibly armed with a knife, tried to kick in a door; `641065` says disorder with a weapon at `6220 Shelf Road`, suspect possibly leaving toward the state line; `641069` says she left in a silver HB and names Taylor Briggs. Call `641059` is weaker but still says a female party may leave in a silver Lexus toward the state line.
- Anchor evidence explains the miss: only `641065` has the concrete `6220 Shelf Road` anchor. `641061` has no persisted anchor despite the noisy address, and `641069` only has a generic `Station` landmark anchor. The current deployed continuation rules do not preserve this type of name/vehicle/suspect continuity when the location anchor is weak or absent.
- Positive grouping still exists: incident `3225` retained `4` calls for the I-75 Black Hyundai Kona welfare/BOLO sequence after redirects with shared strong anchors, and `INC-75-NB-6.6` repeatedly redirected compatible candidates to the active incident with shared anchor/time/category evidence.

Current read:
- No service, transcription, embedding, RF, AI routing, or verifier truncation intervention is needed.
- The deploy did not make the system broadly worse; the main metric improved from `113` to `57` retention mismatches.
- The current residual problem is narrower: final ownership still drops same-event continuation calls when the shared event identity comes from name/vehicle/suspect/action evidence rather than a strong normalized location anchor.
- This is important enough to interrupt: the next fix should extend retained-call validation to use structured event-identity support from retained transcripts, not just location anchors or brittle phrase matching.

### 2026-06-10 17:29Z Check

Window: `2026-06-10T09:29Z` to `2026-06-10T17:29Z`; compared with `2026-06-10T01:29Z` to `2026-06-10T09:29Z`.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active.
- Queues healthy under live load: transcription queue `1-2`, pending transcriptions `2`, pending audio `28-56s`, no pressure.
- Throughput healthy: ingest about `7.6 calls/min`, transcribe about `7.8 calls/min`, average transcription realtime factor `0.122`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`, pending `0`, model `text-embedding-nomic-embed-text-v1.5`.
- Calls rose from `1,938` to `3,408`; incident yield rose from `7.74` to `8.80 incidents / 1,000 calls` using the current endpoint incident count.
- AI routing remained correct: direct `lm_usage` rows since `09:29Z` showed `253` successful automatic-insights rows using request model `qwen/qwen3.6-35b-a3b@q8_0` and response model `paxan-qwen3.6-35b-a3b@q8_0`; `55` rows were `recovered_after_truncation_split`, with no failures or `finish_reason=length`.
- Evidence verifier truncation stayed solved: average truncated calls `0`, max truncated calls `0`.
- Verifier/ownership narrowing improved again from `57` to `46` retention mismatches; added/dropped calls were `45/16`.
- Incident call-count distribution for current-window incidents: `15` single-call, `5` two-call, `4` three-call, `2` four-call, and `1` seven-call incident among the incidents still visible in the queried window.
- Top reject buckets: only one call matches title/detail `26`, single-call lacks strong event signal `19`, medical transport lacks parent event `17`, single-call lacks concrete anchor `11`, noisy/routine single-call source `8`, concrete-location conflict `7`, unsupported title/detail `6`.
- Transcript quality remained acceptable: `3,091` OK complete calls, `217` repetitive, `49` empty, `39` too short, `6` numeric noise, `4` marker-only, and `2` pending.

Correlated evidence:
- Positive regression evidence: `3238` / `INC-20260610-I24-WEST-MVC` retained `7` calls for a tractor-trailer crash on I-24 West near mile marker 88. Calls `643023` and `643145` have `model_event_location` highway-mile-marker anchors `i-24|mm:88`, and the incident correctly retained THP and PSAP follow-up traffic.
- Positive multi-source grouping still works: `3256` accident with injury retained `4` calls, `3257` fall retained `3`, `3245` medical emergency retained `3`, and `3253` residential fire retained `2` calls across fire/police dispatch context.
- Residual same-event miss remains: incident `3225` / `INC-20260610-001` now titled `Unconscious patient at Dierke Rd SE, possible overdose` retained fire calls `647381` and `647399`, while audit rows considered `647381`, `647399`, `647409`, and `647417` together but rejected or narrowed the set.
- The dropped Dierke calls look related on transcript evidence: `647381` says `375 Dierke Road southeast` and unconscious; `647399` says an `88-year-old female` at `275/375 Dirt-cube southeast`, accidental overdose; `647409` says possible overdose, fire on scene, patient unconscious; `647417` sends EMS to `275 Dyrki Road southeast` for unconscious. Location extraction produced divergent weak anchors like `375 dierke rd`, `275 30 rd`, and `dyrki rd`, so the location path did not unify the event.
- Concrete-location conflict protection looks useful in sampled rows: the `Lottie/Wadi/Laudy Lane` overdose candidates were protected from over-joining when calls cited different noisy addresses, even though one later single-call overdose incident was created from the strongest retained support.

Current read:
- No service, transcription, embedding, RF, AI routing, or verifier truncation intervention is needed.
- The deploy continues to look directionally positive: retention mismatches fell from `113` to `57` to `46` across the last two windows, while strong highway-marker grouping now has a good live positive example.
- The remaining actionable issue is the same one identified at `09:29Z`: same-event continuation can still be lost when cross-agency calls share event semantics but only weak/noisy locations. The Dierke overdose case should be added as a regression fixture alongside `3230` and the older split cases.

### 2026-06-11 01:30Z Check

Window: `2026-06-10T17:30Z` to `2026-06-11T01:30Z`; compared with `2026-06-10T09:29Z` to `2026-06-10T17:29Z`.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active.
- Queues healthy: transcription queue `0-1`, pending transcriptions `1`, pending audio `12-27s`, no pressure.
- Throughput healthy under high volume: ingest about `8.5 calls/min`, transcribe about `8.8 calls/min`, average transcription realtime factor `0.122`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`, pending `0`, model `text-embedding-nomic-embed-text-v1.5`.
- Calls rose from `3,408` to `4,266`; incident yield fell from `8.80` to `5.63 incidents / 1,000 calls` using the current endpoint incident count.
- AI endpoint summary remained clean: `256/256` successes, `0` failures, `0` truncations. No service/routing intervention indicated.
- Evidence verifier truncation rose slightly from `0` to average `0.28`, max `6`; still low, but no longer a perfect-zero window.
- Verifier/ownership narrowing improved again from `46` to `36` retention mismatches; added/dropped calls were `30/32`.
- Top reject buckets: only one call matches title/detail `27`, medical transport lacks parent event `22`, unsupported title/detail `18`, single-call lacks strong event signal `17`, only one call supports title/detail anchor `12`, concrete-location conflict `12`.
- Transcript quality remained acceptable: `3,873` OK complete calls, `309` repetitive, `39` empty, `29` too short, `15` numeric noise, and `1` pending.

Correlated evidence:
- New high-priority data-integrity issue: generic model incident keys are being reused for unrelated accepted creates, causing old incident rows to be overwritten rather than new incidents being created.
- Current incident row `3225` has `incident_key=INC-20260610-001`, `created_at_utc=2026-06-10T04:34:32Z`, but it now shows `MVC with vehicle fire and entrapment at Greenwood Rd` with calls `656543,656561` from `2026-06-11T01:21Z`.
- Audit evidence for `INC-20260610-001` shows `7` accepted creates since `2026-06-10T09:29Z` and `11` accepted creates total. The same key has been used for unrelated call sets including `641339...`, `645813/645861`, `647381/647399`, `650815...`, `652461`, `654615`, and `656543/656561`.
- The problem is not isolated to one key: since `09:29Z`, repeated accepted creates also occurred for `INC-20260610-002` (`3` creates), `INC-20260610-004` (`2`), `INC-20260610-003` (`2`), and `INC-20260610-013` (`2`).
- Current examples show unrelated row reuse: `3228` / `INC-20260610-004` was created at `2026-06-10T06:18Z` but now shows `Possible Stroke at Tennessee State Veterans Home` with call `656077`; `3229` / `INC-20260610-002` was created at `2026-06-10T07:56Z` but now shows a pursuit/shots-fired sequence with calls `654637,654699,654703,655289,655301,655381,655469`.
- Positive incident collation still exists: the pursuit/shots-fired row itself retained a coherent seven-call sequence, and high-volume operational/transcription health was fine.

Current read:
- No service, transcription, embedding, RF, AI routing, or queue intervention is needed.
- The retention-mismatch trend continues to improve, so the previous continuation-retention fix still looks directionally positive.
- The meaningful regression is a separate incident identity bug: model-generated generic incident keys are colliding and accepted `create incident` paths are updating existing rows. This can erase/replace incident titles, details, time spans, and call ownership for unrelated older incidents.
- Prioritized fix: make incident identity server-owned. If an incoming create uses a generic key that already exists, only update the existing row after explicit reconciliation overlap; otherwise mint a distinct key and preserve the older incident. Add a test fixture for repeated `INC-YYYYMMDD-001` creates with disjoint calls to prove the old row is not overwritten.

### 2026-06-11 09:31Z Check

Window: `2026-06-11T01:31Z` to `2026-06-11T09:31Z`; compared with `2026-06-10T17:30Z` to `2026-06-11T01:30Z`.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active.
- Queues healthy: transcription queue `0`, pending transcriptions `0`, pending audio `0`, no pressure.
- Throughput healthy: ingest about `5.0 calls/min`, transcribe about `5.3-5.4 calls/min`, average transcription realtime factor about `0.108`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`, pending `0`, model `text-embedding-nomic-embed-text-v1.5`.
- Calls fell from `4,266` to `2,097`; incident yield rose from `5.63` to `5.72 incidents / 1,000 calls` using the current endpoint incident count.
- AI endpoint summary regressed: quality check showed `234` requests, `222` successes, `12` failures, `0` truncations.
- Direct `lm_usage` rows show `11` failures from `2026-06-11T02:46:05Z` to `03:04:51Z` with `No models loaded`, plus `1` failure at `03:09:28Z` with `Model unloaded`.
- AI response model also drifted: after the failures, successful automatic-insights rows returned `response_model=qwen3.6-35b-a3b` for `182` successes, while earlier rows in the same window returned the expected `paxan-qwen3.6-35b-a3b@q8_0` for `40` successes. Request model stayed `qwen/qwen3.6-35b-a3b@q8_0`.
- Evidence verifier truncation stayed low: average truncated calls `0.04`, max `1`.
- Verifier/ownership narrowing rose from `36` to `51` retention mismatches; added/dropped calls were `45/10`.
- Top reject buckets: single-call lacks strong event signal `34`, only one call matches title/detail `29`, unsupported title/detail `10`, only one call supports title/detail anchor `9`, medical transport lacks parent event `6`, single-call lacks concrete anchor `6`.
- Transcript quality remained acceptable: `1,926` OK complete calls, `119` repetitive, `25` empty, `19` too short, and `8` numeric noise.

Correlated evidence:
- The generic-key overwrite problem continued. Since `2026-06-11T01:30Z`, repeated accepted creates occurred for `INC-20260611-001` (`4` accepted creates), `INC-20260610-001` (`3`), `INC-20260610-003` (`2`), and `INC-20260610-002` (`2`).
- Current old rows remain overwritten by unrelated later events: row `3225`, created `2026-06-10T04:34Z` with key `INC-20260610-001`, now shows `Fall call at Park Hill Apartments on Harrison Pike` with calls `658101,658283,658297`; row `3229`, created `2026-06-10T07:56Z` with key `INC-20260610-002`, now shows `Domestic assault in progress at Serenity Dr NE` with call `658527`; row `3232`, created `2026-06-10T10:42Z` with key `INC-20260610-003`, now shows `Subject Douglas Crawford at Overhead Bridge AP40` with calls `658387,658443`.
- The key-reuse bug also affected the new day key: row `3280` / `INC-20260611-001` has already absorbed multiple disjoint accepted-create call sets before settling on `EMS/Fire Response to Chest Pain at Parkwood Trail NW` with calls `660277,660287,660291,660381`.
- Positive incident grouping still exists despite the identity issue: retained current incidents include `3281` suicide attempt with children present, `3280` chest-pain response with four calls, `3283` infant medical emergency, and `3284` fire alarm.

Current read:
- No transcription, embedding, RF, or queue intervention is needed.
- Two items remain important enough to interrupt. First, generic model incident keys are still overwriting unrelated incident rows and should be fixed before relying on dashboard incident history. Second, AI routing/model availability regressed with transient unloaded-model failures and response-model drift away from the expected Paxan identifier.
- Prioritized fixes: make incident identity server-owned and update-only after reconciliation overlap; separately verify Paxan/LM Studio model loading and response_model normalization for `qwen/qwen3.6-35b-a3b@q8_0`.

### 2026-06-11 17:32Z Check

Window: `2026-06-11T09:32Z` to `2026-06-11T17:32Z`; compared with `2026-06-11T01:31Z` to `2026-06-11T09:31Z`.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active.
- Queues healthy: transcription queue `0`, pending transcriptions `0-1`, pending audio `0-15s`, no pressure.
- Throughput healthy under higher daytime volume: ingest about `8.9-9.0 calls/min`, transcribe about `9.2 calls/min`, average transcription realtime factor about `0.127`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`, pending `0`, model `text-embedding-nomic-embed-text-v1.5`.
- Calls rose from `2,097` to `3,142`; incident yield rose from `5.72` to `5.73 incidents / 1,000 calls` using the current endpoint incident count.
- AI endpoint failure count recovered: quality check showed `235/235` successes, `0` failures, and `0` truncations.
- AI response model drift persisted: direct `lm_usage` rows since `09:31Z` show all `235` successful automatic-insights rows returning `response_model=qwen3.6-35b-a3b` rather than expected `paxan-qwen3.6-35b-a3b@q8_0`. Request model stayed `qwen/qwen3.6-35b-a3b@q8_0`.
- Evidence verifier truncation returned to solved: average truncated calls `0`, max `0`.
- Verifier/ownership narrowing improved from `51` to `42` retention mismatches; added/dropped calls were `35/12`.
- Top reject buckets: single-call lacks strong event signal `32`, medical transport lacks parent event `18`, only one call matches title/detail `17`, unsupported title/detail `12`, noisy/routine single-call source `12`, generic routine/status title `9`.
- Transcript quality remained acceptable: `2,841` OK complete calls, `211` repetitive, `45` empty, `37` too short, and `8` numeric noise.

Correlated evidence:
- Generic-key overwrite is still active and broader than the prior run. Since `09:31Z`, repeated accepted creates occurred for `INC-20260611-001` (`5` creates), `INC-20260611-004` (`4`), `INC-20260611-003` (`3`), `INC-20260611-002` (`3`), and two each for `INC-20260611-012`, `INC-20260611-007`, and `INC-20260611-005`.
- Current row `3280` / `INC-20260611-001`, created at `2026-06-11T05:27Z`, now shows `Assault at 4570 Furniture Rd NW` with only call `666461`. The same key had accepted-create call sets such as `661091/661105/661127`, `661205/661209/661221`, `663615/664045/664051/664123`, `665367/665813/665819`, and `666461`.
- Current row `3288` / `INC-20260611-002`, created at `2026-06-11T08:56Z`, now shows `EMS response, chest pain, 6424 Paw Paw Trail` with calls `665645,665659,665771`, after earlier unrelated accepted-create call sets including `661093` and `664451/664463`.
- Current rows `3289` and `3290` also show the same pattern: `INC-20260611-003` moved through unrelated create sets before `Police response, trespassing, 4319 14th Avenue`; `INC-20260611-004` moved through unrelated create sets before `Fire/EMS response, fall, 205 Pearl Street`.
- Incident collation still has positive examples where stable non-generic keys are used, including `3301` fire at Wall's Apartments, `3303` medical call at Stormy Ridge, and `3291` MVC with injuries at Hickory Valley/Oaks.

Current read:
- No transcription, embedding, RF, queue, or AI availability intervention is needed.
- The AI unload failure from the prior window cleared, but response-model normalization remains out of spec.
- The generic incident-key overwrite remains the highest-priority active issue and is now broader across same-day generic keys. Dashboard incident history and linked-call history for affected generic keys should be considered unstable until server-owned identity is fixed.
- Prioritized fix remains unchanged: do not trust model-generated generic `INC-YYYYMMDD-NNN` as database identity. Mint server-owned incident keys on create unless reconciliation proves overlap with an existing incident; add tests covering repeated generic-key creates with disjoint call sets.

### 2026-06-12 01:33Z Check

Window: `2026-06-11T17:33Z` to `2026-06-12T01:33Z`; compared with `2026-06-11T09:33Z` to `2026-06-11T17:33Z`. This window includes the `2026-06-11T20:17Z` deploy of server-owned generic-key handling and narrative grounding.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active.
- Queues healthy: transcription queue `0`, pending transcriptions `0`, pending audio `0`, no pressure.
- Throughput healthy: ingest about `7.9 calls/min`, transcribe about `8.2 calls/min`, average transcription realtime factor about `0.125`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`, pending `0`, model `text-embedding-nomic-embed-text-v1.5`.
- Calls rose from about `3,146` to `4,050`; current-window incident yield was about `8.15 incidents / 1,000 calls` using current `last_seen` semantics.
- AI had `268` requests, `267` successes, `1` failure, and `0` endpoint truncations. The single failure was `The operation was canceled` at `2026-06-11T20:17:21Z`, exactly at the deploy boundary.
- Post-deploy AI had `187/187` successes and no failures, but response model drift persisted: all post-deploy successes returned `response_model=qwen3.6-35b-a3b` while request model stayed `qwen/qwen3.6-35b-a3b@q8_0`.
- Evidence verifier reviewed about `5.29` calls on average; average truncated calls `0.50`, max truncated `13`, added/dropped `55/21`, retention mismatches `45`.
- Top current reject buckets: single-call lacks strong event signal `27`, medical transport lacks parent emergency/event evidence `25`, only one call matches title/detail `15`, only one call supports title/detail anchor `11`, only zero calls match title/detail `8`, unsupported emergency/event signal `7`.
- Transcript quality remained acceptable: `3,670` OK complete calls, `224` repetitive, `80` empty, `55` too short, `17` numeric noise, and `1` marker-only.

Correlated evidence:
- Generic-key overwrite fix appears effective after deploy. Exact `INC-YYYYMMDD-NNN` keys still appear in intermediate verifier/ownership audit rows, but there were `0` repeated accepted creates under exact generic keys after `2026-06-11T20:17Z`.
- The only exact generic DB rows touched after deploy were old rows `3288` / `INC-20260611-002` and `3289` / `INC-20260611-003`, updated shortly after deploy with existing-key overlap; no post-deploy repeated-create overwrite pattern was observed.
- New narrative grounding is active: post-deploy audit rows show `13` accepted incidents with `narrative:rewrote unsupported model narrative from retained transcript evidence`.
- The grounding fix prevents unsupported phrases from being saved, but it introduced a new acceptance-quality issue: some unsupported narratives are rewritten to low-value generic titles instead of being rejected. Examples include `3327` / `llm:whiteoakmt-cleveland:674687:ems-response-to-building-on-stucky-dr-nw` titled `Police call at Stucky Dr NW`, `3322` / `INC-TRAILER-002` titled `Police call at Grand Road`, `3325` / `INC_20260611_BRAD_FIRE_CANCEL` titled `Police call at 40 Beard County Rd`, and `3319` / `INC-NEW-Ringgold-Medical` titled `Fire call at 4145 Ringgold Rd`.
- Some grounding rewrites are useful and should be preserved: `3316` became `Unconscious person at 1125 N Congress Parkway`, and the transcript says the patient passed out but is breathing; `3315` elevator rescue, `3314` stroke, and `3313` smoke/electrical smell are also supported by retained transcripts.
- Single-call accepted distribution post-deploy was high but mixed: `9` single-call incidents among post-deploy current incidents. Good single-call examples include `3328` automatic fire alarm at `3831 Wilcox Blvd`, `3323` unconscious female near King St, and `3318` unconscious person at Center for Business and Treatment Health. Weak examples are the generic `Police call` fallbacks above.

Current read:
- No transcription, embedding, RF, queue, or service intervention is needed.
- Generic incident-key overwrite should remain on watch but is no longer the highest-priority active issue after deploy.
- The meaningful new fix candidate is narrative fallback acceptance: if grounding removes unsupported event semantics and can only produce a generic `Police/Fire/EMS call` title, the candidate should usually be rejected unless retained transcripts contain a supported strong event concept.
- Prioritized fix: tighten `IncidentNarrativeGrounder` integration so `WasRewritten` with no supported fallback event concept fails validation instead of saving a generic category/location incident. Keep the positive grounded rewrites for clearly supported concepts like unconscious, stroke, elevator rescue, smoke/fire, seizure, overdose, and MVC.

### 2026-06-12 09:34Z Check

Window: `2026-06-12T01:34Z` to `2026-06-12T09:34Z`; compared with `2026-06-11T17:34Z` to `2026-06-12T01:34Z`.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active.
- Queues healthy: transcription queue `0`, pending transcriptions `0`, pending audio `0`, no pressure.
- Throughput healthy at lower overnight volume: ingest about `3.4 calls/min`, transcribe about `3.8 calls/min`, average transcription realtime factor about `0.157`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`, pending `0`, model `text-embedding-nomic-embed-text-v1.5`.
- Calls fell from `4,047` to `2,204`; incident yield rose from about `8.65` to `10.44 incidents / 1,000 calls` using current `last_seen` semantics.
- Incident call-count distribution improved versus the prior window: prior had `19` single-call incidents among `35`; current had `6` single-call incidents among `23`, with most current incidents retaining `2-3` calls.
- AI endpoint summary was clean: `247/247` successes, `0` failures, `0` truncations. Direct rows still showed response-model drift: all rows returned `response_model=qwen3.6-35b-a3b` with request model `qwen/qwen3.6-35b-a3b@q8_0`.
- Evidence verifier truncation returned to solved: average truncated calls `0`, max `0`, average reviewed calls `4.86`, added/dropped `57/11`, retention mismatches `41`.
- Top reject buckets: only one call matches title/detail `25`, single-call lacks strong event signal `16`, unsupported title/detail signal `15`, medical transport lacks parent event `13`, only one call supports title/detail anchor `11`, only zero calls match title/detail `10`.
- Transcript quality remained acceptable: `2,039` OK complete calls, `124` repetitive, `22` too short, `14` empty, and `5` numeric noise.

Correlated evidence:
- Generic-key overwrite fix continued to hold. There were `0` repeated accepted creates under exact `INC-YYYYMMDD-NNN` keys in the current window and `0` since the `2026-06-11T20:17Z` deploy.
- Exact generic keys still appear for single create/update paths and intermediate verifier/ownership audit rows, for example `INC-20260612-0512-MEDICAL`, `INC-20260612-0509-MVA`, `INC-20260612-0442-RINGLE`, and `INC-20260612-0428-RHEA`, but no same-key repeated-create overwrite pattern was observed.
- Narrative grounding rewrites dropped from `13` accepted rewrites in the post-deploy slice ending `01:33Z` to `6` in the current window, but the same fallback-quality issue remains.
- Current weak fallback examples: `3350` / `INC-20260612-0428-RHEA` titled `Police call at 29090 Kiley Rd`; `3333` / `llm:whiteoakmt-hamilton:676455:residential-burglary-at-998-julian-road` titled `Police call at 998 Julian Road` even though the transcript describes a burglary; `3332` / `llm:whiteoakmt-hamilton:674631:mvc-dry-valley-rd` titled `Police call at 500 Dry Valley Road` even though the transcript describes vehicles and injuries; `3329` retitled `Fire call at 1217 Jennifer Ln` after an active-incident redirect.
- Positive narrative/grouping examples remain: `3352` two-car MVA with injuries on Highway 151, `3353` unconscious female at Cherokee Ln with possible broken hip, `3351` unconscious female at Ringgold Rd, `3349` chest pain, `3343` fall at Harrington Ln, and `3339` hit-and-run MVC at Ringel Rd.
- Some non-emergency operational-looking incidents still appear, including `3345` access issue at North Cleveland Towers, `3341` maintenance entry at North Cleveland Towers, and `3336` water flow/meter shutoff. These should stay in the title/source-drift bucket unless repeated evidence shows they are intended fire-service incidents.

Current read:
- No service, transcription, embedding, RF, queue, AI availability, verifier truncation, or generic-key overwrite intervention is needed.
- The system should keep baking operationally.
- The same active fix candidate remains: narrative grounding should reject unsupported proposals when the only fallback is a generic category/location title, while preserving grounded rewrites that retain a supported event concept.

### 2026-06-12 17:34Z Check

Window: `2026-06-12T09:34Z` to `2026-06-12T17:34Z`; compared with `2026-06-12T01:34Z` to `2026-06-12T09:34Z`.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active.
- Queues healthy: transcription queue `0`, pending transcriptions `0`, pending audio `0`, no pressure.
- Throughput healthy under higher daytime volume: ingest about `11.3 calls/min`, transcribe about `11.5 calls/min`, average transcription realtime factor about `0.095`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`, pending `0`, model `text-embedding-nomic-embed-text-v1.5`.
- Calls rose from `2,203` to `3,619`; incident yield eased from about `10.44` to `9.39 incidents / 1,000 calls` using current `last_seen` semantics.
- Incident call-count distribution shifted back toward single-call incidents: prior had `6` single-call incidents among `23`; current had `14` single-call incidents among `34`.
- AI endpoint summary was clean: `244/244` successes, `0` failures, `0` endpoint truncations. Direct rows still showed response-model drift: all rows returned `response_model=qwen3.6-35b-a3b` with request model `qwen/qwen3.6-35b-a3b@q8_0`.
- Evidence verifier truncation stayed solved: average truncated calls `0.01`, max `1`, average reviewed calls `5.39`, added/dropped `39/17`, retention mismatches `29`.
- Top reject buckets: medical transport lacks parent event `30`, only one call matches title/detail `23`, single-call lacks strong event signal `19`, unsupported title/detail signal `19`, only one call supports concrete incident location while others cite different locations `12`, only one call supports title/detail anchor `10`.
- Transcript quality remained acceptable: `3,246` OK complete calls, `266` repetitive, `57` too short, `36` empty, `7` numeric noise, and `4` marker-only.

Correlated evidence:
- Generic-key overwrite fix continued to hold. There were `0` repeated accepted creates under exact `INC-YYYYMMDD-NNN` keys in the current window and `0` since the `2026-06-11T20:17Z` deploy.
- Narrative grounding rewrites increased from `6` accepted rewrites in the `09:34Z` check to `17` in this window. The same fallback-quality issue is now stronger: some fallback titles mask supported event semantics, not just unsupported model speculation.
- Weak/generic fallback examples: `3384` / `llm:whiteoakmt-cleveland:682823:stabbing-2069-old-charleston-rd-ne` is now `Police call at 2069 Old Charleston Road NE`; retained transcripts only support the location/drone response, so the original stabbing key was unsupported, but the resulting incident is too generic to be useful. `3370` / THP traffic stop is `Police call at I-75 NB near mile marker 30`, based on plate/traffic-stop style transcripts.
- Supported-event masking examples: `3377` / `INC-MVC-POPLAR` is titled `Police call at 1201 Poplar Street involving blue max and company van`, while the retained transcript says negative injuries but clearly describes two vehicles and damage. `3365` / `INC-20260612-CPR-STANFORD` is `Fire call at 29 Elaine Drive`; transcript supports a weak/unable-to-walk medical call, not CPR, but the fallback loses the useful medical complaint. `3379` / `INC-MED-36KENT` says `Medical assist ... female fall`; transcript supports a fall with controlled bleeding, not a generic assist label.
- Positive grouping and incident quality still exist: `3372` tractor-trailer/vehicle fire on I-75 NB retained a multi-call sequence with redirects; `3386` and `3385` vehicle-fire rows look supported; `3352` MVA, `3353` unconscious/broken hip, `3387` unconscious male at American House, and `3380` stroke are plausible supported incidents.
- Verifier/ownership narrowing improved in aggregate from `41` to `29`, so this window does not support reverting the retention/collation changes.

Current read:
- No service, transcription, embedding, RF, queue, AI availability, verifier truncation, or generic-key overwrite intervention is needed.
- Generic-key overwrite should stay in monitor; three post-deploy checks have found no repeated generic-key create overwrite.
- The active fix candidate is now better supported: narrative grounding needs a second pass that rejects unsupported proposals when only a generic category/location title remains, and preserves supported event semantics from retained transcripts instead of reducing them to `Police/Fire/EMS call`.

### 2026-06-13 01:35Z Check

Window: `2026-06-12T17:35Z` to `2026-06-13T01:35Z`; compared with `2026-06-12T09:35Z` to `2026-06-12T17:35Z`.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active.
- Queues healthy: transcription queue `2-3`, pending transcriptions `2-3`, pending audio `34-52s`, no sustained pressure.
- Throughput healthy: ingest about `6.1-6.2 calls/min`, transcribe about `6.0 calls/min`, average transcription realtime factor about `0.085`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`, pending `0`, model `text-embedding-nomic-embed-text-v1.5`.
- Calls rose from `3,625` to `3,957`; incident yield fell from `9.93` to `7.33 incidents / 1,000 calls` using current `last_seen` semantics.
- Incident call-count distribution modestly improved: prior had `16` single-call incidents among `36`; current had `13` single-call incidents among `29`.
- AI endpoint summary was clean: `259/259` successes, `0` failures, `0` endpoint truncations. Direct rows still showed response-model drift: all rows returned `response_model=qwen3.6-35b-a3b` with request model `qwen/qwen3.6-35b-a3b@q8_0`; `41` rows were marked `recovered_after_truncation_split` but finished `stop`.
- Evidence verifier truncation stayed solved: average truncated calls `0.01`, max `1`, average reviewed calls `5.07`, added/dropped `47/26`, retention mismatches `40`.
- Top reject buckets: medical transport lacks parent event `27`, only one call matches title/detail `24`, single-call lacks strong event signal `16`, only one call supports title/detail anchor `14`, only zero calls match title/detail `12`, unsupported title/detail signal `11`.
- Transcript quality remained acceptable: `3,603` OK complete calls, `257` repetitive, `39` empty, `37` too short, `14` numeric noise, `2` pending OK, and `2` marker-only.

Correlated evidence:
- Generic-key overwrite fix continued to hold. There were `0` repeated accepted creates under exact `INC-YYYYMMDD-NNN` keys in the current window and `0` since the `2026-06-11T20:17Z` deploy.
- Narrative rewrites dropped from `17` to `14`, but the fallback-quality issue remains active. Weak examples include `3415` / `INC-20260612-BREINER` titled `Fire call at 3826 Brainerd Rd` from a transcript that only clearly supports a Brainerd Road location, and `3410` / `vehicle-pursuit-on-n-highway-27-near-roc` retitled `Police call at Riverside Dr/UTC Arena` even though the retained transcript describes a motorcycle unable to maintain lane and hitting a truck.
- Other weak or masked narrative examples: `3407` / fire alarm at Matt Circle became `Fire call at 11800 Matt Circle SE`; `3395` / fire at School St became `EMS call at School Street after patient tray incident`; `3414` kept `Medical Assist: Abdominal Pain` even though the transcript supports abdominal pain and transport but not the generic `medical assist` phrase.
- Positive grounded rewrites still exist and should not be lost: `3408` stroke at 30th Street NE, `3404` chest pain at Countrywood Dr SE, `3396` MVC with injuries at I-75 SB mile marker 5, and `3401` non-breathing/back-valley medical call are plausibly supported by retained transcript evidence.
- Verifier/ownership narrowing worsened from `29` to `40` retention mismatches, but current false-join controls also rejected `4` concrete-location disagreement cases and `14` title/detail-anchor cases. This is not enough by itself to justify reverting the retention/collation changes.

Current read:
- No service, transcription, embedding, RF, queue, AI availability, verifier truncation, or generic-key overwrite intervention is needed.
- Generic-key overwrite should stay in monitor; four post-deploy checks have found no repeated generic-key create overwrite.
- The active fix candidate is unchanged and remains supported: narrative grounding needs a second pass that rejects candidates when only a generic category/location fallback remains, and preserves supported event semantics from retained transcripts instead of reducing them to `Police/Fire/EMS call`.

### 2026-06-13 09:35Z Check

Window: `2026-06-13T01:35Z` to `2026-06-13T09:35Z`; compared with `2026-06-12T17:35Z` to `2026-06-13T01:35Z`.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active.
- Queues healthy: transcription queue `0`, pending transcriptions `0`, pending audio `0`, no pressure.
- Throughput healthy at lower overnight volume: ingest about `3.1 calls/min`, transcribe about `3.1 calls/min`, average transcription realtime factor about `0.103`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`, pending `0`, model `text-embedding-nomic-embed-text-v1.5`.
- Calls fell from `3,955` to `2,516`; incident yield rose from `7.33` to `12.72 incidents / 1,000 calls` using current `last_seen` semantics.
- Incident call-count distribution improved: prior had `13` single-call incidents among `29`; current had `10` single-call incidents among `32`, with most current incidents retaining `2-3` calls.
- AI endpoint summary was clean: `224/224` successes, `0` failures, `0` endpoint truncations. Direct rows still showed response-model drift: all rows returned `response_model=qwen3.6-35b-a3b` with request model `qwen/qwen3.6-35b-a3b@q8_0`; `34` rows were marked `recovered_after_truncation_split` but finished `stop`.
- Evidence verifier truncation stayed low: average truncated calls `0.06`, max `3`, average reviewed calls `5.01`, added/dropped `29/11`, retention mismatches `25`.
- Top reject buckets: only one call matches title/detail `26`, medical transport lacks parent event `25`, single-call lacks strong event signal `24`, concrete-location disagreement `18`, unsupported title/detail signal `12`, generic routine/status title `8`.
- Transcript quality remained acceptable: `2,319` OK complete calls, `148` repetitive, `31` too short, `14` empty, and `4` numeric noise.

Correlated evidence:
- Generic-key overwrite fix continued to hold. There were `0` repeated accepted creates under exact `INC-YYYYMMDD-NNN` keys in the current window and `0` since the `2026-06-11T20:17Z` deploy.
- Narrative rewrites dropped from `14` to `11`, but the fallback-quality issue remains active. `3449` / `lady-dr-subject` was saved as `Police call at Lady Drive` even though retained transcripts support a white male with no shirt and dark shorts wielding an axe near Lady Drive. `3446` / `jenkins-rd-medical` became `Fire call at 26 Jenkins Rd` even though the transcript supports panic attack and possible fall/head hit.
- Other weak or masked narrative examples: `3443` / Soddy Daisy medical call became `Fire call at Montlake Rd` despite a fall from floor context; `3441` / suspect search became `Police call at Stay Straight On That Road`; `3424` / Highway 39 single vehicle in ditch with injury became `Police call at Highway 39 at mile marker 8`.
- Positive incident quality still exists: `3437` MVC with injuries at I-24 EB mile 182.5 retained a multi-call sequence with redirects; `3450` unconscious patient at Wilson St, `3435` difficulty breathing at Walker Rd, `3439` bike crash, and `3434` suicide intervention look plausibly supported by retained transcripts.
- Verifier/ownership narrowing improved from `40` to `25` retention mismatches, so the current window does not support reverting collation/ownership changes. The large `concrete incident location while other calls cite different locations` reject bucket (`18`) is worth watching, but sampled rows look more like false-join protection than a new missed-join regression.

Current read:
- No service, transcription, embedding, RF, queue, AI availability, verifier truncation, or generic-key overwrite intervention is needed.
- Generic-key overwrite should stay in monitor; five post-deploy checks have found no repeated generic-key create overwrite.
- The system should keep baking operationally, but the active fix candidate remains narrative grounding: reject candidates when only a generic category/location fallback remains, and preserve supported event semantics from retained transcripts instead of reducing them to `Police/Fire/EMS call`.

### 2026-06-13 17:36Z Check

Window: `2026-06-13T09:36Z` to `2026-06-13T17:36Z`; compared with `2026-06-13T01:36Z` to `2026-06-13T09:36Z`.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active.
- Queues healthy: transcription queue `0`, pending transcriptions `0`, pending audio `0`, no pressure.
- Throughput healthy: ingest about `3.6 calls/min`, transcribe about `4.0 calls/min`, average transcription realtime factor about `0.152`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`, pending `0`, model `text-embedding-nomic-embed-text-v1.5`.
- Calls were nearly flat, from `2,514` to `2,588`; incident yield fell from `12.33` to `9.66 incidents / 1,000 calls` using current `last_seen` semantics.
- Incident call-count distribution improved slightly: prior had `10` single-call incidents among `31`; current had `8` single-call incidents among `25`, including one `7`-call and one `5`-call incident.
- AI endpoint summary was clean: `254/254` successes, `0` failures, `0` endpoint truncations. Direct rows still showed response-model drift: all rows returned `response_model=qwen3.6-35b-a3b` with request model `qwen/qwen3.6-35b-a3b@q8_0`; `51` rows were marked `recovered_after_truncation_split` but finished `stop`.
- Evidence verifier truncation was solved in this window: average truncated calls `0`, max `0`, average reviewed calls `4.73`, added/dropped `49/11`, retention mismatches `38`.
- Top reject buckets: only one call matches title/detail `32`, single-call lacks strong event signal `20`, medical transport lacks parent event `14`, noisy/routine single-call source `13`, only one call supports title/detail anchor `11`, unsupported title/detail signal `7`.
- Transcript quality remained acceptable: `2,344` OK complete calls, `174` repetitive, `36` too short, `25` empty, and `9` numeric noise.

Correlated evidence:
- Generic-key overwrite fix continued to hold. There were `0` repeated accepted creates under exact `INC-YYYYMMDD-NNN` keys in the current window and `0` since the `2026-06-11T20:17Z` deploy.
- Narrative rewrites rose from `11` to `15` accepted rows. The same fallback-quality issue remains active: `3470` / fire alarm Fairmont became `Fire call at 842 Fairmont Ave`, `3468` / medical emergency Georgetown became `EMS call at Georgetown Rd`, `3466` / gas leak became `Police call at 1603 E Main St`, and `3464` / fall at Mural Rd became `Fire call at 908 Mural Rd`.
- Positive grounded examples still exist: `3471` unconscious male at 302 Brady Point Road, `3465` vehicle accident at I-75 near Paul Huff exit ramp, `3463` vehicle accident at Mackie Ave, and `3456` non-breathing patient with landing-zone context are plausibly supported by retained transcripts.
- Verifier/ownership narrowing worsened from `25` to `38` retention mismatches, but direct audit still shows useful false-join protection: only `5` concrete-location disagreement rejects versus `18` in the prior window, and no generic-key create overwrite. This window alone does not support reverting collation/ownership changes.
- Two `rejected:narrative failed validation` rows appeared, which suggests the newer narrative validation path is rejecting some weak proposals, but it is not yet preventing generic fallback saves.

Current read:
- No service, transcription, embedding, RF, queue, AI availability, verifier truncation, or generic-key overwrite intervention is needed.
- Generic-key overwrite should stay in monitor; six post-deploy checks have found no repeated generic-key create overwrite.
- The system should keep baking operationally. The only supported fix candidate remains narrative grounding: reject candidates when only a generic category/location fallback remains, and preserve supported event semantics from retained transcripts instead of reducing them to `Police/Fire/EMS call`.

### 2026-06-14 01:36Z Check

Window: `2026-06-13T17:36Z` to `2026-06-14T01:36Z`; compared with `2026-06-13T09:36Z` to `2026-06-13T17:36Z`.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active. `trunk-recorder` restarted during the window at `2026-06-13 20:52Z`, but call volume, queue health, and transcription throughput do not show a capture-quality regression.
- Queues healthy: transcription queue `0`, pending transcriptions `0`, pending audio `0`, no pressure.
- Throughput healthy under higher volume: ingest about `8.5 calls/min`, transcribe about `8.6 calls/min`, average transcription realtime factor about `0.096`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`, pending `0`, model `text-embedding-nomic-embed-text-v1.5`.
- Calls rose from `2,590` to `3,851`; incident yield fell from `10.04` to `7.01 incidents / 1,000 calls` using current `last_seen` semantics.
- Incident call-count distribution shifted toward single-call incidents: prior had `9` single-call incidents among `26`; current had `15` single-call incidents among `27`.
- AI endpoint summary was clean: `242/242` successes, `0` failures, `0` endpoint truncations. Direct rows still showed response-model drift: all rows returned `response_model=qwen3.6-35b-a3b` with request model `qwen/qwen3.6-35b-a3b@q8_0`; `29` rows were marked `recovered_after_truncation_split` but finished `stop`.
- Evidence verifier truncation stayed solved: average truncated calls `0`, max `0`, average reviewed calls `5.31`, added/dropped `42/15`, retention mismatches `34`.
- Top reject buckets: single-call lacks strong event signal `27`, only one call supports title/detail anchor `22`, only one call matches title/detail `21`, medical transport lacks parent event `18`, unsupported title/detail signal `16`, generic routine/status title `10`.
- Transcript quality remained acceptable: `3,534` OK complete calls, `227` repetitive, `49` too short, `23` empty, `13` numeric noise, and `1` marker-only.

Correlated evidence:
- Generic-key overwrite fix continued to hold. There were `0` repeated accepted creates under exact `INC-YYYYMMDD-NNN` keys in the current window and `0` since the `2026-06-11T20:17Z` deploy.
- Narrative rewrites rose from `15` to `23` accepted rows, and this remains the dominant quality problem. Current generic or masked fallback examples include `3498` / MVC Kinsor Rd saved as `Police call at Kinsor Rd`, `3501` / MVC Oak saved as `Police call at 3145 Oak Hill Road`, `3499` / MBC Daisy Dallas Rd saved as `Traffic call at Daisy Dallas Rd`, and `3489` / medical emergency at Francisco Rd saved as `Fire call at 1571 Francisco Rd`.
- Other weak fallback examples: `3496` / tunnel became `Police call at 4100 12th Avenue`, `3494` / Cherokee assist became `Fire call at 42 Cherokee Boulevard`, `3492` / fire response at Call of Parkway became `Fire call at 635 Call of Parkway NW resolved as accidental`, and `3487` / MVC at North Lee Hwy became `Police call at 6289 N Lee Hwy involving Chevy O and Malibu`.
- Positive incident quality still exists: `3486` hit-and-run MVC at Middle Valley Rd retained traffic category and injury context; `3488` child locked in vehicle at Sportsman's retained concrete vehicle/location details; `3479` seizure at Ringgold Rd and `3484` chest pain at Wilson St are plausibly supported.
- Verifier/ownership narrowing improved slightly from `38` to `34` retention mismatches, and verifier truncation stayed zero. This window does not support reverting collation/ownership changes.

Current read:
- No service, transcription, embedding, RF, queue, AI availability, verifier truncation, or generic-key overwrite intervention is needed.
- Generic-key overwrite should stay in monitor; seven post-deploy checks have found no repeated generic-key create overwrite.
- The system should keep baking operationally. The only supported fix candidate remains narrative grounding: reject candidates when only a generic category/location fallback remains, and preserve supported event semantics from retained transcripts instead of reducing them to `Police/Fire/EMS/Traffic call`.

### 2026-06-14 09:36Z Check

Window: `2026-06-14T01:36Z` to `2026-06-14T09:36Z`; compared with `2026-06-13T17:36Z` to `2026-06-14T01:36Z`.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active.
- Queues healthy: transcription queue `0`, pending transcriptions `0`, pending audio `0`, no pressure.
- Throughput healthy at low overnight volume: ingest about `1.5 calls/min`, transcribe about `1.7 calls/min`, average transcription realtime factor about `0.084`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`, pending `0`, model `text-embedding-nomic-embed-text-v1.5`.
- Calls fell from `3,849` to `2,426`; incident yield rose from `7.79` to `13.19 incidents / 1,000 calls` using current `last_seen` semantics.
- Incident call-count distribution remains single-call heavy: prior had `16` single-call incidents among `30`; current had `20` single-call incidents among `32`.
- AI endpoint summary was clean: `244/244` successes, `0` failures, `0` endpoint truncations. Direct rows still showed response-model drift: all rows returned `response_model=qwen3.6-35b-a3b` with request model `qwen/qwen3.6-35b-a3b@q8_0`; `52` rows were marked `recovered_after_truncation_split` but finished `stop`.
- Evidence verifier truncation stayed solved: average truncated calls `0`, max `0`, average reviewed calls `4.22`, added/dropped `37/17`, retention mismatches `33`.
- Top reject buckets: single-call lacks strong event signal `25`, only one call matches title/detail `22`, medical transport lacks parent event `16`, unsupported title/detail signal `14`, only one call supports title/detail anchor `11`, operational chatter lacks event anchor `11`.
- Transcript quality remained acceptable: `2,213` OK complete calls, `160` repetitive, `25` too short, `22` empty, and `5` numeric noise.

Correlated evidence:
- Exact date-stamped generic keys remained fixed: `0` repeated accepted creates under exact `INC-YYYYMMDD-NNN` keys in the current window and `0` since the `2026-06-11T20:17Z` deploy.
- A related identity bug appeared for short generic keys. At `2026-06-14T01:39Z`, accepted-create rows used `INC-001` and `INC-003`, updating old DB rows `1372` and `1350` created on `2026-05-16` and `2026-05-15`. Current row `1372` is now `Traffic call at 1220 Burnt Mill Rd` with calls `712513,712729,712767`; current row `1350` is now `Police call at 244 Canyon Circle` with call `712773`.
- This is not the same repeated-create pattern as the old `INC-YYYYMMDD-NNN` loop, but it is the same class of trust bug: model-supplied placeholder incident identity can still overwrite old incident rows when the placeholder is short-form rather than date-stamped.
- Narrative fallback quality remains active but not new. Current examples include `3535` / fell patient at Longstreet saved as `Fire call at 16 Longstreet Road`, `3514` / Rossville Blvd disorder saved as `Police call at 2717 Rossville Blvd`, `3510` / Lane St medical assist saved as `Fire call at 939 Lane Street NE`, and `3505` / child assault Hwy 58 saved as `Police call at Old Highway 58`.
- Positive incident quality still exists: `3534` smoke/AC issue at Lawford Way, `3533` chest pain at Dooley St, `3532` difficulty breathing at Fagan St, `3531` EMS manpower assist at Jenkins Rd, `3528` stroke alert, and `3517` MVC with possible head injury are plausible supported incidents.
- Verifier/ownership narrowing was steady, from `34` to `33` retention mismatches, with verifier truncation still zero. This window does not support reverting collation/ownership changes.

Current read:
- No service, transcription, embedding, RF, queue, AI availability, or verifier truncation intervention is needed.
- New supported fix candidate: extend server-owned incident identity handling to short placeholder keys such as `INC-001`, `INC-002`, `INC-003`, and underscore variants. These should be treated as untrusted create identities unless there is explicit reconciliation overlap with an existing incident.
- Narrative grounding remains the other active fix candidate: reject candidates when only a generic category/location fallback remains, and preserve supported event semantics from retained transcripts instead of reducing them to `Police/Fire/EMS/Traffic call`.

### 2026-06-14 17:37Z Check

Window: `2026-06-14T09:37Z` to `2026-06-14T17:37Z`; compared with `2026-06-14T01:36Z` to `2026-06-14T09:36Z`.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active.
- Queues healthy: transcription queue `0`, pending transcriptions `0`, pending audio `0`, no pressure.
- Throughput healthy under higher daytime load: ingest about `9.6 calls/min`, transcribe about `9.8 calls/min`, average transcription realtime factor about `0.120`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`, pending `0`, model `text-embedding-nomic-embed-text-v1.5`.
- Calls fell from `2,426` to `2,069`; incident yield eased from `13.19` to `12.08 incidents / 1,000 calls` using current `last_seen` semantics.
- Incident call-count distribution improved from the single-call-heavy prior window: prior had `20` single-call incidents among `32`; current had `10` single-call incidents among `25`.
- AI endpoint summary was clean: `195/195` successes, `0` failures, `0` endpoint truncations. Direct rows still showed response-model drift: all rows returned `response_model=qwen3.6-35b-a3b` with request model `qwen/qwen3.6-35b-a3b@q8_0`; `41` rows were marked `recovered_after_truncation_split` but finished `stop`.
- Evidence verifier truncation stayed solved: average truncated calls `0`, max `0`, average reviewed calls `5.25`, added/dropped `18/12`, retention mismatches `20`.
- Top reject buckets: single-call lacks strong event signal `20`, medical transport lacks parent event `16`, unsupported title/detail signal `11`, only one call matches title/detail `11`, single-call lacks concrete anchor `8`, noisy/routine single-call source `5`.
- Transcript quality remained acceptable: `1,868` OK complete calls, `111` repetitive, `42` empty, `38` too short, `9` numeric noise, and `1` marker-only.

Correlated evidence:
- The short-generic key issue did not recur in this window: no accepted creates under short placeholder keys like `INC-001`/`INC_001`, and no repeated creates under date-stamped `INC-YYYYMMDD-NNN` keys.
- The short-generic identity bug remains active because the prior `09:36Z` window proved `INC-001` and `INC-003` can still update old May rows. This run only says there was no additional occurrence.
- Narrative rewrites dropped from `12` rows in the prior direct audit to `9` current rows. Weak fallback examples remain: `3561` / stroke at Key St became `Fire call at 132 Key St SW`; `3555` / difficulty breathing at Westside Dr repeatedly became `EMS call at 2900 Westside Dr NW`; `3535` / fell patient Longstreet remained `Fire call at 16 Longstreet Road`.
- Positive incident quality is visible: `3559` black smoke, `3558` fall with transport, `3557` hit-and-run postal vehicle, `3556` fall with head/back injury, `3552` unconscious patient, `3546` tire smoke on I-75 SB, `3545` seizure, and `3542` MVC at Northmore/Brainerd are plausible supported incidents.
- Verifier/ownership narrowing improved materially from `33` to `20` retention mismatches, with verifier truncation still zero. This window does not support reverting collation/ownership changes.

Current read:
- No service, transcription, embedding, RF, queue, AI availability, verifier truncation, or new generic-key overwrite intervention is needed.
- Keep the short-placeholder identity fix active from the prior run: treat `INC-001`, `INC-002`, `INC_003`, etc. as untrusted create identities unless explicit reconciliation overlap exists.
- The system should keep baking operationally. Narrative grounding remains the other supported fix candidate: reject candidates when only a generic category/location fallback remains, and preserve supported event semantics from retained transcripts instead of reducing them to `Police/Fire/EMS/Traffic call`.

### 2026-06-15 01:38Z Check

Window: `2026-06-14T17:38Z` to `2026-06-15T01:38Z`; compared with `2026-06-14T09:37Z` to `2026-06-14T17:37Z`.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active.
- Queues healthy: transcription queue `0`, pending transcriptions `1`, pending audio `10s`, no pressure.
- Throughput healthy under high storm/weather volume: ingest about `6.4 calls/min`, transcribe about `6.5 calls/min`, average transcription realtime factor about `0.118`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`, pending `0`, model `text-embedding-nomic-embed-text-v1.5`.
- Calls rose from `2,079` to `4,131`; incident yield fell from `12.51` to `7.50 incidents / 1,000 calls` using current `last_seen` semantics.
- Incident call-count distribution improved materially despite higher call volume: prior had `8` single-call incidents among `26`; current had `8` single-call incidents among `31`, including one `12`-call incident.
- AI endpoint summary was clean: `280/280` successes, `0` failures, `0` endpoint truncations. Direct rows still showed response-model drift: all rows returned `response_model=qwen3.6-35b-a3b` with request model `qwen/qwen3.6-35b-a3b@q8_0`; `42` rows were marked `recovered_after_truncation_split` but finished `stop`.
- Evidence verifier truncation remained low but no longer zero: average truncated calls `0.22`, max `4`, average reviewed calls `6.00`, added/dropped `100/21`, retention mismatches `54`.
- Top reject buckets: only one call matches title/detail `17`, only zero calls match title/detail `15`, noisy/routine single-call source `10`, concrete-location disagreement `8`, unsupported title/detail signal `7`, only one call supports title/detail anchor `7`.
- Transcript quality remained acceptable: `3,771` OK complete calls, `263` repetitive, `47` empty, `37` too short, `11` numeric noise, and `1` pending OK.

Correlated evidence:
- No additional short-placeholder overwrite occurred in this window. There were no accepted creates under `INC-001`/`INC_001` style keys, and no repeated accepted creates under date-stamped `INC-YYYYMMDD-NNN` keys.
- The short-generic identity bug remains active because the `2026-06-14 09:36Z` window proved `INC-001` and `INC-003` can still update old May rows. This run only says there was no additional occurrence.
- Narrative rewrites rose from `9` to `18` accepted rows. Weak fallback examples remain: `3562` / MVC I-75 SB mile marker 11 became `Traffic call at I-75 SB mile marker 11`; `3561` / stroke at Key St repeatedly remained `Fire call at 132 Key St SW`; `3569` / medical 58 became `Chest pain at Highway 58`.
- Positive incident quality was strong during the weather-heavy window: `3593` residential fire on Northwind/Harrison Pike, `3589` residential fire at Stonehaven, `3587` vehicle fire on I-75 NB at Exit 11 with debris, `3584` MVC with injuries/tree down, `3582` overdose at Walnut, `3580` tree falling on house, `3576` explosion at Goodson, and `3575` wires down at Reeds Lake Rd are plausible supported incidents.
- Verifier/ownership narrowing increased from `20` to `54` retention mismatches, but the call volume roughly doubled and several multi-call storm/fire incidents retained large sets (`13 -> 11`, `9 -> 6`, `7 -> 6`). This is worth watching but does not by itself support reverting collation/ownership changes.

Current read:
- No service, transcription, embedding, RF, queue, AI availability, or generic-key overwrite intervention is needed.
- Keep watching verifier retention during weather-heavy traffic; current evidence shows higher narrowing but also successful large incident retention.
- Keep the short-placeholder identity fix active from the `09:36Z` finding: treat `INC-001`, `INC-002`, `INC_003`, etc. as untrusted create identities unless explicit reconciliation overlap exists.
- Narrative grounding remains the other supported fix candidate: reject candidates when only a generic category/location fallback remains, and preserve supported event semantics from retained transcripts instead of reducing them to `Police/Fire/EMS/Traffic call`.

### 2026-06-15 09:40Z Check

Window: `2026-06-15T01:40Z` to `2026-06-15T09:40Z`; compared with `2026-06-14T17:38Z` to `2026-06-15T01:38Z`.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active.
- Queues healthy: transcription queue `0`, pending transcriptions `0-1`, pending audio `0-13s`, no pressure.
- Throughput healthy at lower overnight volume: ingest about `3.7 calls/min`, transcribe about `3.9 calls/min`, average transcription realtime factor about `0.100`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`, pending `0`, model `text-embedding-nomic-embed-text-v1.5`.
- Calls fell from `4,131` to `2,076`; incident yield rose from `7.50` to `8.19 incidents / 1,000 calls` using the endpoint incident count.
- Incident call-count distribution remained acceptable: prior had `8` single-call incidents among `31`; current endpoint shows `17` incidents total, while direct current `last_seen` query showed `17` incidents and sampled accepted creates stayed restrained.
- AI endpoint summary was clean: `223/223` successes, `0` failures, `0` endpoint truncations. Direct rows still showed response-model drift: all rows returned `response_model=qwen3.6-35b-a3b` with request model `qwen/qwen3.6-35b-a3b@q8_0`; `38` rows were marked `recovered_after_truncation_split` but finished `stop`.
- Evidence verifier truncation stayed low: average truncated calls `0.02`, max `1`, average reviewed calls `5.17`, added/dropped `36/29`, retention mismatches `22`.
- Top reject buckets: single-call lacks strong event signal `29`, only one call matches title/detail `20`, medical transport lacks parent event `18`, only one call supports title/detail anchor `13`, low confidence lacks two strong anchored calls `10`, noisy/routine single-call source `9`.
- Transcript quality remained acceptable: `1,861` OK complete calls, `146` repetitive, `34` empty, `28` too short, `5` numeric noise, `1` overexpanded, and `1` pending OK.

Correlated evidence:
- No additional short-placeholder overwrite occurred in this window. There were no accepted creates under `INC-001`/`INC_001` style keys, and no repeated accepted creates under date-stamped `INC-YYYYMMDD-NNN` keys.
- The short-generic identity bug remains active because the `2026-06-14 09:36Z` window proved `INC-001` and `INC-003` can still update old May rows. This run only says there was no additional occurrence.
- Narrative rewrite volume dropped sharply from `18` to `3` direct accepted rows, but the same quality class remains. Examples: `3600` has key `carbon-monoxide-alarm-at-195-windcrest-p` but title `Vehicle accident at 195 Windcrest Pl W`; `3599` has title `Shooting at 1240 Brookfield Ct NE` while retained detail is weak; `3597` remains generic as `Police call at Shady's near cemetery`.
- Positive incident quality was visible in current rows: `3611` vehicle in ditch with locked doors, `3610` fall with injury, `3608` fire alarm activation resolved, `3607` drug overdose, `3606` wires down cancelled, `3603` chest pain, `3602` CO alarm/generator issue, and `3598` assault with suspect in custody are plausible supported incidents.
- Verifier/ownership narrowing improved from `54` to `22` retention mismatches as storm traffic eased, and verifier truncation stayed near zero. This does not support reverting collation/ownership changes.

Current read:
- No service, transcription, embedding, RF, queue, AI availability, verifier truncation, or new generic-key overwrite intervention is needed.
- Keep the short-placeholder identity fix active from the `2026-06-14 09:36Z` finding: treat `INC-001`, `INC-002`, `INC_003`, etc. as untrusted create identities unless explicit reconciliation overlap exists.
- Narrative grounding remains the other supported fix candidate, but this window shows improvement in rewrite volume. The fix should still reject candidates when only a generic category/location fallback remains, and preserve supported event semantics from retained transcripts instead of reducing them to `Police/Fire/EMS/Traffic call`.

### 2026-06-16 22:32Z Post-Deploy Hourly Check

Window: approximately `2026-06-16T21:32Z` to `2026-06-16T22:32Z`, first hourly check after the `2026-06-16T21:31:58Z` deployment.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active. `pizzad` uptime was about `1h` after deploy.
- Queues healthy: transcription queue `0`, pending transcriptions `0`, pending audio `0`, no pressure.
- Throughput healthy: health endpoint showed recent ingest/transcribe about `6.7/7.1 calls/min`, average transcription realtime factor about `0.136`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`, pending `0`, model `text-embedding-nomic-embed-text-v1.5`.
- Last-hour calls: `429` total, `6,803s` audio. Transcript quality: `395` OK complete, `20` repetitive, `11` empty, `2` too short, `1` numeric noise.
- AI endpoint summary clean: `34/34` successes, `0` failures, `0` truncations.
- Evidence verifier: `13` runs, average reviewed calls `3.15`, average/max truncated calls `0/0`, added/dropped `1/2`, retention mismatches `3`.
- Incidents: `2` current endpoint incidents, `2` creates, `5` updates, `22` rejects. Approximate yield: `4.66 incidents / 1,000 calls`.
- Top reject buckets: medical transport lacks parent event `7`, single-call lacks strong event signal `4`, noisy/routine single-call source `2`, conflicting concrete locations `2`, plus isolated low-confidence, generic routine/status, and unsupported title/detail rejects.

Post-deploy identity and title evidence:
- No `rejected:unknown model incident_id` rows in the first hour. The stricter identifier contract has not caused an obvious rejection spike yet.
- No new generic fallback titles were created after deploy. The two new incidents were `Unconscious person at 1760 Enfield Street` and `Unconscious person at 1716 Stampfield Street`.
- Existing updates did not overwrite canonical title/detail in the visible audit. One row explicitly showed the new preservation path: `accepted:update incident; narrative:preserved existing title/detail after unsupported update proposal` for `INC-POWER-DOWNED-LINES`.
- No active-incident redirect rows appeared after deploy. The prior `redirected to active incident INC-75-SOUTH-OBJECT` row remains pre-deploy context only.
- Updated legacy keys still exist and can receive exact-key updates when the model copies an active incident id, for example `INC-POWER-DOWNED-LINES`, `INC-MED-4011-OAKLAND`, and `INC-SMOKE-260-CRAWL`. This is expected under the immutable-key rule; the monitor should watch whether these old model-looking keys remain stable rather than trying to rename them.

Current read:
- No user-facing regression or immediate fix candidate from this first post-deploy hour.
- Continue hourly monitoring for unknown identifier rejections, create volume collapse, duplicate siblings from rejected unknown IDs, generic-title creates, and any existing title/detail drift after deploy.

### 2026-06-16 23:32Z Post-Deploy Hourly Check

Window: approximately `2026-06-16T22:32Z` to `2026-06-16T23:32Z`; compared with the first post-deploy hour.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active. `pizzad` uptime was about `2h` after deploy.
- Queues healthy: transcription queue `0`, pending transcriptions `1`, pending audio `24s`, no pressure.
- Throughput healthy: health endpoint showed recent ingest/transcribe about `7.1/7.2 calls/min`, average transcription realtime factor about `0.106`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`, pending `0`, model `text-embedding-nomic-embed-text-v1.5`.
- Last-hour calls: `331` total, `5,317s` audio. Transcript quality: `304` OK complete, `16` repetitive, `8` empty, `1` pending OK, `1` numeric noise, `1` too short.
- AI endpoint summary clean: `20/20` successes, `0` failures, `0` truncations.
- Evidence verifier: `1` run, reviewed `5` calls, truncated `0`, added/dropped `2/1`, retention mismatches `0`.
- Incidents: `4` current endpoint incidents, `4` creates, `6` updates, `20` rejects. Approximate yield: `12.08 incidents / 1,000 calls`.
- Top reject buckets: medical transport lacks parent event `4`, unknown model incident id `4`, single-call lacks strong event signal `3`, generic routine/status title `3`, only one call matches title/detail `2`, only one call supports title/detail anchor `2`.

Post-deploy identity and title evidence:
- New supported issue: the stricter identifier contract correctly rejected `4` rows where the model returned `llm:whiteoakmt-cleveland:762733`, but the actual active incident key is `llm:whiteoakmt-cleveland:762733:possible-propane-leak-at-tudor-springs-c`.
- The repeated unknown-id rejects affected the same propane leak incident candidate at `22:48Z`, `23:00Z`, and `23:25Z`, with call sets expanding from `762733,762747,762751,762843` to include `763085`.
- This is not a database overwrite regression. It is evidence that prompt-visible identifiers are too long and semantically shaped, so the model can return a plausible prefix instead of the exact key.
- No generic fallback titles were created after deploy. New titles were specific: `Possible propane leak at Tudor Springs Church Rd SE`, `Fire assist/lift assist at 4302 St Elm Ave`, `BB gun injury at 227 Big Pine Ln`, and `Lift assist at 2009 Peterson Dr, elderly female fallen in garage`.
- The new generic-title rejection path fired once: `rejected:unsupported narrative lacked a specific evidence-backed fallback`.
- No active-incident redirect rows appeared after deploy.

Current read:
- Service, transcription, embedding, queue, AI, verifier truncation, and generic-title behavior remain healthy.
- Supported fix candidate: stop exposing full database incident keys to the model. Send short per-prompt aliases for active incidents, such as `existing_1`, and map aliases back to immutable database keys server-side. This keeps identifiers immutable without relying on the model to copy long title-derived keys exactly.

### 2026-06-17 00:33Z Post-Deploy Hourly Check

Window: approximately `2026-06-16T23:33Z` to `2026-06-17T00:33Z`; compared with the prior post-deploy hour.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active. `pizzad` uptime was about `3h` after deploy.
- Queues healthy: transcription queue `0`, pending transcriptions `0`, pending audio `0`, no pressure.
- Throughput healthy: health endpoint showed recent ingest/transcribe about `8.9/9.1 calls/min`, average transcription realtime factor about `0.117`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`, pending `0`, model `text-embedding-nomic-embed-text-v1.5`.
- Last-hour calls: `438` total, `6,757s` audio. Transcript quality: `399` OK complete, `27` repetitive, `6` too short, `3` empty, `3` numeric noise.
- AI endpoint summary clean: `30/30` successes, `0` failures, `0` truncations.
- Evidence verifier: `4` runs, average reviewed calls `3.75`, average/max truncated calls `0/0`, added/dropped `8/0`, retention mismatches `0`.
- Incidents: `3` current endpoint incidents, `3` creates, `1` update, `19` rejects. Approximate yield: `6.85 incidents / 1,000 calls`.
- Top reject buckets: only one call matches title/detail `6`, medical transport lacks parent event `4`, unsupported narrative lacked specific fallback `2`, only zero calls match title/detail `2`, plus isolated standby-only, low-confidence, noisy/routine single-call, and unsupported emergency-signal rejects.

Post-deploy identity and title evidence:
- The prior hour's unknown-id issue did not continue in the latest hour: `0` `rejected:unknown model incident_id` rows in the current one-hour window. The two-hour endpoint still shows `5` total, all from the prior propane leak issue at or before `23:32Z`.
- No generic fallback titles were created after deploy in this window. New titles were specific: `Medical Assist ALS Breathing Difficulty at 712 Adams Rd`, `Head injury at 3330 Pleasant Grove Church Rd`, and `Downed Wires at 11200 Keith Rd/Levin Ln`.
- The new unsupported-generic fallback rejection path fired `2` times, both for assault-related proposals that lacked enough specific retained evidence to safely title as incidents.
- No active-incident redirect rows appeared after deploy.
- Verifier truncation and retention mismatches remained zero in the latest hour.

Current read:
- No new user-facing regression in this hour. Service, transcription, embedding, queue, AI, verifier truncation, and generic-title behavior remain healthy.
- Keep the per-prompt alias fix candidate active from the prior hour, but the immediate unknown-id symptom did not repeat in this latest window.

### 2026-06-17 01:33Z Post-Deploy Hourly Check

Window: approximately `2026-06-17T00:33Z` to `2026-06-17T01:33Z`; compared with the prior post-deploy hour.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active. `pizzad` uptime was about `4h` after deploy.
- Queues healthy: transcription queue `0`, pending transcriptions `0`, pending audio `0`, no pressure.
- Throughput healthy: health endpoint showed recent ingest/transcribe about `5.4/5.5 calls/min`, average transcription realtime factor about `0.117`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`, pending `0`, model `text-embedding-nomic-embed-text-v1.5`.
- Last-hour calls: `414` total, `6,662s` audio. Transcript quality: `382` OK complete, `20` repetitive, `8` empty, `2` too short, `1` numeric noise, `1` overexpanded.
- AI endpoint summary clean: `30/30` successes, `0` failures, `0` truncations.
- Evidence verifier: `10` runs, average reviewed calls `5.8`, average/max truncated calls `0/0`, added/dropped `12/1`, retention mismatches `6`.
- Incidents: `2` current endpoint incidents, `2` creates, `4` updates, `20` rejects. Approximate yield: `4.83 incidents / 1,000 calls`.
- Top reject buckets: only one call matches title/detail `6`, single-call lacks strong event signal `5`, routine status/compliance rollup lacks shared concrete event anchor `2`, only zero calls match title/detail `2`, plus isolated near-threshold pair similarity, concrete-location disagreement, overlap validation, and medical transport rejects.

Post-deploy identity and title evidence:
- No `rejected:unknown model incident_id` rows in the latest hour. The prior propane-leak prefix issue did not recur.
- No generic fallback titles were created after deploy in this window. New titles were specific: `Gas Leak at 105 Philwood Drive` and `Unconscious female at 32 Back Valley Rd`.
- Existing title/detail preservation path fired once for `llm:whiteoakmt-hamilton:764879:unconscious-female-at-32-back-valley-rd`, with `accepted:update incident; narrative:preserved existing title/detail after unsupported update proposal`.
- No active-incident redirect rows appeared after deploy.
- The downed-wires incident at `11200 Keith Rd/Levin Ln` showed one near-threshold ownership rejection: verifier retained `5` calls but ownership retained `3`, then rejected unclaimed calls because pair similarity was `0.19` below the `0.20` threshold. This is worth watching, but it is a single near-boundary case and the incident still retained `3` to `4` calls across accepted updates.
- Verifier truncation remained zero. Retention mismatches rose from `0` to `6`, but the increase is tied to a small number of ownership-pruning cases rather than service or model failure.

Current read:
- No user-facing regression in this hour. Service, transcription, embedding, queue, AI, verifier truncation, identifier behavior, and generic-title behavior remain healthy.
- Keep watching the per-prompt alias fix candidate from the prior hour and the near-threshold ownership pruning around the downed-wires incident, but neither requires interruption yet.

### 2026-06-17 02:34Z Post-Deploy Hourly Check

Window: approximately `2026-06-17T01:34Z` to `2026-06-17T02:34Z`; compared with the prior post-deploy hour.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active. `pizzad` uptime was about `5h` after deploy.
- Queues healthy: transcription queue `0`, pending transcriptions `0`, pending audio `0`, no pressure.
- Throughput healthy: health endpoint showed recent ingest/transcribe about `4.8/5.2 calls/min`, average transcription realtime factor about `0.100`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`, pending `0`, model `text-embedding-nomic-embed-text-v1.5`.
- Last-hour calls: `392` total, `6,156s` audio. Transcript quality: `361` OK complete, `24` repetitive, `3` empty, `3` too short, `1` numeric noise.
- AI endpoint summary clean: `28/28` successes, `0` failures, `0` truncations.
- Evidence verifier: `8` runs, average reviewed calls `5.75`, average/max truncated calls `0.125/1`, added/dropped `13/0`, retention mismatches `0`.
- Incidents: `2` current endpoint incidents, `2` creates, `0` updates, `21` rejects. Approximate yield: `5.10 incidents / 1,000 calls`.
- Top reject buckets: only one call matches title/detail `7`, single-call lacks strong event signal `3`, two-call pair similarity `0.15` below `0.20` `3`, only zero calls match title/detail `3`, concrete-location disagreement `3`, plus isolated generic routine/status and medical transport rejects.

Post-deploy identity and title evidence:
- No `rejected:unknown model incident_id` rows in the latest hour. The prior propane-leak prefix issue has not recurred for three consecutive hourly checks.
- No generic fallback titles were created after deploy in this window. New titles were specific: `Burn pile fire on County Rd 279 knocked down` and `Downed live power lines on driveway, Brad Fire Main`.
- New watch item, low severity: `Downed live power lines on driveway, Brad Fire Main` appears to include a channel or talkgroup label (`Brad Fire Main`) in the canonical title. This is not the old generic fallback problem, but it is a title-cleanliness issue to watch for recurrence.
- No active-incident redirect rows appeared after deploy.
- The prior downed-wires `11200 Keith Rd/Levin Ln` ownership-pruning issue did not recur in this latest one-hour window.
- Verifier truncation was technically nonzero but negligible: max `1` truncated reviewed call in `8` runs, with `0` retention mismatches.

Current read:
- No user-facing regression in this hour. Service, transcription, embedding, queue, AI, identifier behavior, generic-title behavior, and verifier behavior remain healthy.
- Continue watching the per-prompt alias fix candidate, but the immediate unknown-id symptom has stayed quiet. Also watch whether title text starts leaking talkgroup/channel labels such as `Brad Fire Main`; one instance is not enough to interrupt.

### 2026-06-17 03:34Z Post-Deploy Hourly Check

Window: approximately `2026-06-17T02:34Z` to `2026-06-17T03:34Z`; compared with the prior post-deploy hour.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active. `pizzad` uptime was about `6h` after deploy.
- Queues healthy: transcription queue `0`, pending transcriptions `1`, pending audio `23s`, no pressure.
- Throughput healthy: health endpoint showed recent ingest/transcribe about `7.3/7.6 calls/min`, average transcription realtime factor about `0.117`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`, pending `0`, model `text-embedding-nomic-embed-text-v1.5`.
- Last-hour calls: `363` total, `5,540s` audio. Transcript quality: `327` OK complete, `26` repetitive, `6` empty, `3` too short, `1` pending OK.
- AI endpoint summary clean: `39/39` successes, `0` failures, `0` truncations.
- Evidence verifier: `13` runs, average reviewed calls `6.15`, average/max truncated calls `0.69/5`, added/dropped `8/2`, retention mismatches `5`.
- Incidents: `2` current endpoint incidents, `2` creates, `4` updates, `29` rejects. Approximate yield: `5.51 incidents / 1,000 calls`.
- Top reject buckets: single-call lacks strong event signal `7`, only one call matches title/detail `6`, only one call supports title/detail anchor `4`, unsupported narrative lacked specific fallback `3`, unsupported emergency/event signal `2`, medical transport lacks parent event `2`.

Post-deploy identity and title evidence:
- No `rejected:unknown model incident_id` rows in the latest hour. The prior propane-leak prefix issue has not recurred for four consecutive hourly checks.
- No generic fallback titles were created after deploy in this window. New titles were specific: `Traffic stop of black Dodge Durango on 136th Memorial` and `Overdose/CPR at 714 19th St NE`.
- The low-severity talkgroup-label title leak did not newly recur in the one-hour incident list, though the prior `Downed live power lines on driveway, Brad Fire Main` title remains visible in the two-hour context.
- No active-incident redirect rows appeared after deploy.
- Verifier truncation increased from negligible to max `5`, but this remains small relative to `13` verifier runs and there was no endpoint truncation, service issue, or create/update collapse. Retention mismatches rose from `0` to `5`, mostly around large verifier-retained sets narrowed by ownership.
- The generic-title rejection path continued to do useful work: `3` rows rejected as unsupported narrative lacking a specific evidence-backed fallback.

Current read:
- No user-facing regression in this hour. Service, transcription, embedding, queue, AI, identifier behavior, and generic-title behavior remain healthy.
- Keep watching verifier truncation and retention mismatches because they rose this hour, but the observed level is not enough to interrupt. Keep the per-prompt alias fix candidate active as a supported but not urgent cleanup.

### 2026-06-17 04:33Z Post-Deploy Hourly Check

Window: approximately `2026-06-17T03:33Z` to `2026-06-17T04:33Z`; compared with the prior post-deploy hour and checked against the broader two-hour context.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active. `pizzad` uptime was about `7h` after deploy.
- Queues healthy: transcription queue `0`, pending transcriptions `0`, pending audio `0`, no pressure.
- Throughput healthy: health endpoint showed recent ingest/transcribe about `5.7/5.9 calls/min`, average transcription realtime factor about `0.143`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`, pending `0`, model `text-embedding-nomic-embed-text-v1.5`.
- Last-hour calls: `324` total, `4,687s` audio. Transcript quality: `289` OK complete, `26` repetitive, `7` too short, `1` empty, `1` numeric noise.
- AI endpoint summary clean: `26/26` successes, `0` failures, `0` truncations.
- Evidence verifier: `10` runs, average reviewed calls `5.5`, average/max truncated calls `0/0`, added/dropped `7/1`, retention mismatches `4`.
- Incidents: `0` current endpoint incidents, `0` creates, `8` updates, `17` rejects. The two-hour context still had `2` creates over `693` calls, so this does not currently look like a create-volume collapse.
- Top buckets: accepted update `8`, single-call lacks strong event signal `3`, only one call matches title/detail `3`, verifier retained `6` calls but ownership retained `2` `3`, unsupported emergency/event signal `3`, medical transport lacks parent event `2`, unsupported narrative lacked a specific fallback `2`.

Post-deploy identity and title evidence:
- No `rejected:unknown model incident_id` rows in the latest hour. The prior propane-leak prefix issue remains quiet.
- No generic fallback titles were created after deploy in this window. There were no creates in the latest hour; the two-hour created incidents remained specific: `Overdose/CPR at 714 19th St NE` and `Traffic stop of black Dodge Durango on 136th Memorial`.
- The generic-title rejection path fired `2` times as `rejected:unsupported narrative lacked a specific evidence-backed fallback`.
- No active-incident redirect rows appeared after deploy.
- Verifier truncation dropped back to zero. Retention mismatches decreased slightly from `5` to `4`.
- Watch item: the `Overdose/CPR at 714 19th St NE` incident repeatedly updated while ownership narrowed retained candidates to call IDs `766501` and `766549`. Several accepted update rows show the verifier retaining `3` to `7` calls, then final ownership retaining only `1` or `2`. This is not yet a regression because the incident still updates and the stricter ownership filter is expected to prune unrelated candidates, but repeated narrowing on the same incident is worth checking if missed sibling calls become visible.

Current read:
- No user-facing regression in this hour. Service, transcription, embedding, queue, AI, identifier behavior, generic-title behavior, and verifier truncation are healthy.
- Keep watching the per-prompt alias fix candidate and ownership narrowing around multi-candidate medical incidents, but there is no supported reason to interrupt right now.

### 2026-06-17 05:34Z Post-Deploy Hourly Check

Window: approximately `2026-06-17T04:34Z` to `2026-06-17T05:34Z`; compared with the prior post-deploy hour and checked against the broader two-hour context.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active. `pizzad` uptime was about `8h` after deploy.
- Queues healthy: transcription queue `0`, pending transcriptions `0`, pending audio `0`, no pressure.
- Throughput healthy: health endpoint showed recent ingest/transcribe about `2.4/2.6 calls/min`, average transcription realtime factor about `0.102`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`, pending `0`, model `text-embedding-nomic-embed-text-v1.5`.
- Last-hour calls: `243` total, `3,687s` audio. Transcript quality: `222` OK complete, `14` repetitive, `4` too short, `2` empty, `1` numeric noise.
- AI endpoint summary clean: `24/24` successes, `0` failures, `0` truncations.
- Direct `lm_usage` rows for the hour showed `24` successful rows through `2026-06-17T05:30:56Z`; request model stayed `qwen/qwen3.6-35b-a3b@q8_0`, response model stayed the known drifted value `qwen3.6-35b-a3b`, finish reason was `stop`, and `5` rows were marked `recovered_after_truncation_split`.
- Evidence verifier: `4` runs, average reviewed calls `3.75`, average/max truncated calls `0/0`, added/dropped `3/1`, retention mismatches `0`.
- Incidents: `2` current endpoint incidents, `3` creates, `3` updates, `15` rejects. Approximate create yield: `12.35 creates / 1,000 calls`, but the one-hour denominator is small and the two-hour context remained reasonable at `3` creates over `565` calls.
- Top buckets: verifier retained `2` calls but ownership retained `1` `6`, only one call matches title/detail `5`, unsupported emergency/event signal `4`, medical transport lacks parent event `3`, accepted update `3`, accepted create `3`, plus isolated single-call and unsupported-narrative rejects.

Post-deploy identity and title evidence:
- No `rejected:unknown model incident_id` rows in the latest hour. The prior propane-leak prefix issue remains quiet.
- No generic fallback titles from the blocked list were created after deploy in this window. New created titles were `Possible heart attack at 2201 Applebrook Dr office`, `Fall victim call at 4586 CLA`, and `Unresponsive 91-year-old female at 4222 Linton Ave`.
- The generic-title rejection path fired once as `rejected:unsupported narrative lacked a specific evidence-backed fallback`.
- No active-incident redirect rows appeared after deploy.
- Verifier truncation stayed at zero, and retention mismatches dropped from `4` to `0`.
- Watch item: `Fall victim call at 4586 CLA` is a low-value title compared with the better medical titles created in the same hour. It is not one of the blocked generic fallbacks, and its audit row has parent evidence `fall victim call`, but the `CLA` suffix and the word `call` make it worth tracking with the broader title-cleanliness item.
- Watch item continuation: ownership narrowing remained visible on the `Unresponsive 91-year-old female at 4222 Linton Ave` incident. The audit repeatedly accepted updates for only call `767183` after verifier retention, with `extractor_or_existing` metadata saying the call was not present in the current RAG candidate set. This does not prove a missed join yet, but it is evidence to correlate if nearby same-event medical traffic appears on the dashboard.

Current read:
- No user-facing regression in this hour. Service, transcription, embedding, queue, AI availability, identifier behavior, generic-title blocking, and verifier truncation are healthy.
- Keep watching response-model normalization drift, title cleanliness, and ownership narrowing around medical incidents. None crossed the notification threshold in this sample.

### 2026-06-17 06:34Z Post-Deploy Hourly Check

Window: approximately `2026-06-17T05:34Z` to `2026-06-17T06:34Z`; compared with the prior post-deploy hour and checked against the broader two-hour context.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active. `pizzad` uptime was about `9h` after deploy.
- Queues healthy: transcription queue `0`, pending transcriptions `0`, pending audio `0`, no pressure.
- Throughput healthy: health endpoint showed recent ingest/transcribe about `4.1/4.2 calls/min`, average transcription realtime factor about `0.110`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`, pending `0`, model `text-embedding-nomic-embed-text-v1.5`.
- Last-hour calls: `196` total, `2,947s` audio. Transcript quality: `171` OK complete, `15` repetitive, `6` empty, `2` numeric noise, `2` too short.
- AI endpoint summary clean: `32/32` successes, `0` failures, `0` truncations.
- Direct `lm_usage` rows for the hour showed `32` successful rows; request model stayed `qwen/qwen3.6-35b-a3b@q8_0`, response model stayed the known drifted value `qwen3.6-35b-a3b`, finish reason was `stop`, and `5` rows were marked `recovered_after_truncation_split`.
- Evidence verifier: `11` runs, average reviewed calls `4.73`, average/max truncated calls `0/0`, added/dropped `7/1`, retention mismatches `6`.
- Incidents: `2` current endpoint incidents, `2` creates, `8` updates, `11` rejects. Approximate create yield: `10.20 creates / 1,000 calls`, but the one-hour denominator is small and the two-hour context had `5` creates over `439` calls.
- Top buckets: accepted update `8`, verifier retained `3` calls but ownership retained `2` `4`, only one call matches title/detail `4`, verifier retained `6` calls but ownership retained `5` `2`, single-call lacks anchor/location `2`, accepted create `2`, only zero calls match title/detail `2`.

Post-deploy identity and title evidence:
- No `rejected:unknown model incident_id` rows in the latest hour. The prior propane-leak prefix issue remains quiet.
- No blocked generic fallback titles were created after deploy in this window. New created titles were `EMS response to 2416 Haven Cove Ln for possible stroke` and `Woman stuck in drain behind Wildwood Ave shelter`.
- No active-incident redirect rows appeared after deploy.
- Verifier truncation stayed at zero, but retention mismatches rose from `0` to `6`.
- AI availability remained healthy. Response-model normalization drift persists but is unchanged: successful rows returned `qwen3.6-35b-a3b` while request model remained explicit `qwen/qwen3.6-35b-a3b@q8_0`.

Supported missed-incident evidence:
- A legitimate multi-call Hwy 95 motorcycle wreck was rejected and is not linked to any incident. Calls checked: `767763`, `767769`, `767777`, `767779`, `767875`, `767903`; `incident_calls` had no rows for any of them.
- Representative transcripts:
  - `767763`: `Highway 95`, wrecked motorcycle, unable to talk, gurgling.
  - `767777`: units responding to `95`, patient not wearing a helmet, bleeding from the head.
  - `767779`: `Highway 95`, unresponsive, gurgling noises, not wearing helmet, head trauma.
  - `767903`: near `22.59` / `22.58`, `highway 95`, relay to medics, approximately `20` to `25` minutes ago.
- Audit rows at `06:01Z`, `06:05Z`, and `06:06Z` rejected overlapping call sets with parent evidence `MVC with injuries, unresponsive patient`. The metadata scored several retained calls with `geo=1.00`, close time scores, and location `hwy 95`, but rejected them as `only 1 call(s) match the incident title/detail` or `only 0 call(s) match the incident title/detail`.
- This looks like a real missed incident, not a remote service failure, an identifier-contract failure, or a generic-title fallback failure.

Current read:
- Service, transcription, embedding, queue, AI availability, identifier behavior, generic-title blocking, and verifier truncation are healthy.
- User attention is warranted because the post-deploy guardrails still missed a high-confidence multi-call emergency event. The likely fix is architectural rather than special-case regex: incident existence should be accepted from the retained shared event evidence and anchor, while title/detail grounding should repair or simplify the narrative instead of rejecting the incident itself.

### 2026-06-17 07:34Z Post-Deploy Hourly Check

Window: approximately `2026-06-17T06:34Z` to `2026-06-17T07:34Z`; compared with the prior post-deploy hour and checked against the broader two-hour context.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active. `pizzad` uptime was about `10h` after deploy.
- Queues healthy: transcription queue `0`, pending transcriptions `0`, pending audio `0`, no pressure.
- Throughput healthy: health endpoint showed recent ingest/transcribe about `2.1/2.2 calls/min`, average transcription realtime factor about `0.086`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`, pending `0`, model `text-embedding-nomic-embed-text-v1.5`.
- Last-hour calls: `152` total, `2,274s` audio. Transcript quality: `140` OK complete, `7` repetitive, `3` too short, `2` empty.
- AI endpoint summary clean: `27/27` successes, `0` failures, `0` truncations.
- Direct `lm_usage` rows for the hour showed `27` successful rows; request model stayed `qwen/qwen3.6-35b-a3b@q8_0`, response model stayed the known drifted value `qwen3.6-35b-a3b`, finish reason was `stop`, and `3` rows were marked `recovered_after_truncation_split`.
- Evidence verifier: `10` runs, average reviewed calls `4.1`, average/max truncated calls `0/0`, added/dropped `0/2`, retention mismatches `2`.
- Incidents: `0` current endpoint incidents, `0` creates, `4` updates, `16` rejects. The two-hour context still had `2` creates over `348` calls, so this is not yet a broad create-volume collapse, but the current hour had no accepted creates.
- Top buckets: medical transport lacks parent event `4`, accepted update `4`, unsupported emergency/event signal `3`, single-call lacks strong event signal `3`, noisy/routine single-call `2`, verifier retained `3` calls but ownership retained `2` `2`.

Post-deploy identity and title evidence:
- No `rejected:unknown model incident_id` rows in the latest hour.
- No blocked generic fallback titles were created after deploy in this window. There were no creates in the latest hour.
- No active-incident redirect rows appeared after deploy.
- Verifier truncation stayed at zero. Retention mismatches improved from `6` to `2`.
- AI availability remained healthy. Response-model normalization drift persists but is unchanged: successful rows returned `qwen3.6-35b-a3b` while request model remained explicit `qwen/qwen3.6-35b-a3b@q8_0`.

Supported incident-quality evidence:
- The prior missed Hwy 95 motorcycle wreck did not newly appear as an accepted incident in the latest two-hour context.
- New medical suppression miss candidate: calls `768279`, `768281`, `768283` were rejected as `medical_transport_context lacks parent emergency/event evidence` and had no `incident_calls` rows. Call `768279` transcript says chest pain, altered mental status, PD en route, and a patient description. Calls `768281` and `768283` look like follow-up logistics, but the initial retained call contains parent emergency evidence.
- New vehicle-stop/BOLO-like miss candidate: calls `768325`, `768337`, `768339`, `768341`, `768345` were rejected as `incident title/detail introduces unsupported emergency/event signal` and had no linked incident row. Transcripts include a silver Chevy/sedan, southbound on North Orchard, last known direction, tried pulling it over, and a temp tag. This may be below interrupt severity alone, but it supports the same pattern: a shared public-safety event is being evaluated through proposed title/detail support rather than retained event evidence.
- These findings are not explained by transcription backlog, AI failure, endpoint truncation, embeddings, Qdrant, or identifier rejection.

Current read:
- Service health is clean, but incident quality still needs attention. The same general failure class continued into this hour: valid or plausible public-safety events are being rejected because narrative/title validation is doing incident-existence work, and medical transport suppression can miss parent emergency evidence in the retained call set.
- Proposed fix direction remains non-regex and evidence-based: accept/reject incident existence from retained call evidence and event anchors first; then independently ground, simplify, or reject the model's narrative/title without discarding the incident when the event itself is supported.

### 2026-06-17 08:35Z Post-Deploy Hourly Check

Window: approximately `2026-06-17T07:35Z` to `2026-06-17T08:35Z`; compared with the prior post-deploy hour and checked against the broader two-hour context.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active. `pizzad` uptime was about `11h` after deploy.
- Queues healthy: transcription queue `0`, pending transcriptions `0`, pending audio `0`, no pressure.
- Throughput healthy: health endpoint showed recent ingest/transcribe about `1.9/1.9 calls/min`, average transcription realtime factor about `0.129`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`, pending `0`, model `text-embedding-nomic-embed-text-v1.5`.
- Last-hour calls: `170` total, `2,551s` audio. Transcript quality: `162` OK complete and `8` repetitive, with no empty, too-short, or numeric-noise rows in the quality summary.
- AI endpoint summary clean: `27/27` successes, `0` failures, `0` truncations.
- Direct `lm_usage` rows for the hour showed `27` successful rows; request model stayed `qwen/qwen3.6-35b-a3b@q8_0`, response model stayed the known drifted value `qwen3.6-35b-a3b`, finish reason was `stop`, and `2` rows were marked `recovered_after_truncation_split`.
- Evidence verifier: `4` runs, average reviewed calls `3.5`, average/max truncated calls `0/0`, added/dropped `4/3`, retention mismatches `0`.
- Incidents: `2` current endpoint incidents, `3` creates, `3` updates, `23` rejects. Approximate create yield: `17.65 creates / 1,000 calls`; the denominator is small, and the accepted incidents were specific medical/fire events rather than generic title leakage.
- Top buckets: two-call pair similarity below threshold `4`, verifier retained `2` calls but ownership retained `1` `4`, accepted update `3`, only one call matches title/detail `3`, operational chatter lacks strong event anchor `3`, verifier retained `4` calls but ownership retained `3` `2`, medical transport lacks parent event `2`, unsupported narrative lacked specific fallback `2`, accepted create `2`.

Post-deploy identity and title evidence:
- No `rejected:unknown model incident_id` rows in the latest hour.
- No blocked generic fallback titles were created after deploy in this window.
- New accepted titles were specific: `Commercial fire alarm at 700 Ladd Ave`, `Medical emergency at 4001 Rossville Blvd field`, and `Stroke at 524 Hamphill Ave`.
- One useful grounding row appeared: `accepted:create incident; narrative:rewrote unsupported model narrative from retained transcript evidence` for call `768623`, resulting in `Stroke at 524 Hamphill Ave`.
- No active-incident redirect rows appeared after deploy.
- Verifier truncation stayed at zero, and retention mismatches dropped from `2` to `0`.
- AI availability remained healthy. Response-model normalization drift persists but is unchanged: successful rows returned `qwen3.6-35b-a3b` while request model remained explicit `qwen/qwen3.6-35b-a3b@q8_0`.

Supported incident-quality evidence:
- Positive counterexample: the 4001 Rossville Boulevard field medical event was accepted and correctly linked calls `768565`, `768577`, and `768583`. The retained transcripts include party laying in a field, possible heart attack, chest pains, and responders being guided to the patient.
- Positive counterexample: the single-call possible stroke at `524 Hamphill Ave` was accepted with a grounded title.
- Continuing miss: possible chlorine ingestion at `930 Berry Street NE` was rejected as `standalone transport/hospital handoff lacks a parent emergency/event anchor` and has no linked incident rows. Calls checked: `768673`, `768697`, `768759`, `768761`.
- Representative chlorine-ingestion transcripts:
  - `768673`: caller says a female drank chlorine and they do not know who she is.
  - `768697`: EMS/dispatch context says stay until police get on scene; caller said she drank chlorine and not alcohol.
  - `768759`: EMS encode says chief complaint was drinking chlorine, no odor from mouth, vital signs, arrival at facility in two to three minutes.
  - `768761`: poison control not available; more discussion once in the emergency room; no actual evidence of ingestion.
- This is not explained by transcription backlog, AI failure, endpoint truncation, embeddings, Qdrant, or identifier rejection. It is another example of the medical/logistics suppression path discarding a parent emergency even when the retained set includes initial dispatch and EMS evidence.

Current read:
- Service health remains clean, and the title/identifier deployment is still behaving as intended.
- Incident quality still needs a fix. The strongest current issue is medical/logistics suppression: true transport handoffs should stay filtered, but a retained call set with dispatch, police response, poison-control/ingestion language, or other parent emergency evidence should be accepted as an incident and grounded from that evidence.

### 2026-06-17 09:36Z Post-Deploy Hourly Check

Window: approximately `2026-06-17T08:36Z` to `2026-06-17T09:36Z`; compared with the prior post-deploy hour and checked against the broader two-hour context.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active. `pizzad` uptime was about `12h` after deploy.
- Queues healthy: transcription queue `0`, pending transcriptions `0`, pending audio `0`, no pressure.
- Throughput healthy: health endpoint showed recent ingest/transcribe about `3.0/3.0 calls/min`, average transcription realtime factor about `0.123`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`, pending `0`, model `text-embedding-nomic-embed-text-v1.5`.
- Last-hour calls: `128` total, `1,829s` audio. Transcript quality: `115` OK complete, `8` repetitive, `4` too short, `1` empty.
- AI endpoint summary clean: `20/20` successes, `0` failures, `0` truncations.
- Direct `lm_usage` rows for the hour showed `20` successful rows; request model stayed `qwen/qwen3.6-35b-a3b@q8_0`, response model stayed the known drifted value `qwen3.6-35b-a3b`, finish reason was `stop`, and `6` rows were marked `recovered_after_truncation_split`.
- Evidence verifier: `5` runs, average reviewed calls `5.0`, average/max truncated calls `0/0`, added/dropped `1/5`, retention mismatches `2`.
- Incidents: `1` current endpoint incident, `1` create, `4` updates, `16` rejects. Approximate create yield: `7.81 creates / 1,000 calls`; the two-hour context had `4` creates over `299` calls, so no broad create collapse.
- Top buckets: only one call matches title/detail `5`, accepted update `4`, medical transport lacks parent event `3`, single-call lacks strong event signal `2`, single-call lacks anchor/location `2`, plus isolated create, no-corroborating-calls, verifier-retained-none, unsupported emergency-signal, and two-call pair-similarity rejects.

Post-deploy identity and title evidence:
- No `rejected:unknown model incident_id` rows in the latest hour.
- No blocked generic fallback titles were created after deploy in this window.
- New accepted title was specific: `Lift assist at 5656 Mountain Oaks Ln`, with call `768853` later linked to the incident created from `768901`.
- No active-incident redirect rows appeared after deploy.
- Verifier truncation stayed at zero. Retention mismatches stayed low at `2`.
- AI availability remained healthy. Response-model normalization drift persists but is unchanged: successful rows returned `qwen3.6-35b-a3b` while request model remained explicit `qwen/qwen3.6-35b-a3b@q8_0`.

Supported incident-quality evidence:
- New missed emergency cluster: calls `768913`, `768915`, and `768919` describe a suicide attempt / wrist-cut response, but none were linked in `incident_calls`.
- Representative transcripts:
  - `768913`: EMS dispatch says `suicide attempt` at `533 Central ... International Worship Center`.
  - `768915`: police/EMS context says she cut her wrist and asks whether units should stand by.
  - `768919`: officer reports contact with her at the church.
- Audit row `28859` rejected `768913`, `768915`, `768919` as `only 0 call(s) have corroborating related calls`. Later rows `28863` and `28865` rejected `768913` alone as `single-call incident lacks a strong emergency/event signal`.
- This is a distinct, high-confidence miss from the prior Hwy 95 and chlorine-ingestion examples. It is not explained by transcription backlog, AI failure, endpoint truncation, embeddings, Qdrant, or identifier rejection.
- Additional lower-confidence watch item: call `768883` transcript appears to mention a crashed vehicle that left the scene near Jersey Pike, but it was single-call and anchor quality is weaker. Keep as secondary evidence only.

Current read:
- Service health remains clean, and the title/identifier deployment is still behaving as intended.
- Incident quality is still the active problem. The system is missing clear emergency events even when multiple retained transcripts corroborate them; the fix should separate event existence/retention from title-detail support and make emergency-parent evidence such as suicide attempt, wrist cut, poisoning, crash with injury, and altered mental status survive the suppression layers.

### 2026-06-17 10:38Z Post-Deploy Hourly Check

Window: approximately `2026-06-17T09:38Z` to `2026-06-17T10:38Z`; compared with the prior post-deploy hour and checked against the broader two-hour context.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active. `pizzad` uptime was about `13h` after deploy.
- Queues healthy: transcription queue `0`, pending transcriptions `0`, pending audio `0`, no pressure.
- Throughput healthy: health endpoint showed recent ingest/transcribe about `3.1/3.2 calls/min`, average transcription realtime factor about `0.135`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`, pending `0`, model `text-embedding-nomic-embed-text-v1.5`.
- Last-hour calls: `162` total, `2,252s` audio. Transcript quality: `146` OK complete, `8` repetitive, `5` empty, `2` numeric noise, `1` too short.
- AI endpoint summary clean: `20/20` successes, `0` failures, `0` truncations.
- Direct `lm_usage` rows for the hour showed `20` successful rows; request model stayed `qwen/qwen3.6-35b-a3b@q8_0`, response model stayed the known drifted value `qwen3.6-35b-a3b`, finish reason was `stop`, and `1` row was marked `recovered_after_truncation_split`.
- Evidence verifier: `2` runs, average reviewed calls `3.5`, average/max truncated calls `0/0`, added/dropped `1/0`, retention mismatches `0`.
- Incidents: `2` current endpoint incidents, `2` creates, `1` update, `14` rejects. Approximate create yield: `12.35 creates / 1,000 calls`; the two-hour context had `3` creates over `293` calls, so no broad create collapse.
- Top buckets: single-call lacks strong event signal `4`, generic routine/status title `3`, only one call matches title/detail `2`, unknown model incident ID `2`, accepted create `2`, plus isolated unsupported emergency-signal, missing anchor/location, update, and noisy/routine rejects.

Post-deploy identity and title evidence:
- Unknown model incident ID reappeared after several quiet hours: two rows rejected `llm:whiteoakmt-hamilton:769081`, while the actual active key is `llm:whiteoakmt-hamilton:769081:fire-citizen-assist-at-6939`.
- No row overwrite occurred. The reject behavior protected the existing row, but this confirms the long-key-copying problem still exists.
- No blocked generic fallback titles were created after deploy in this window.
- New accepted titles were specific enough for their evidence: `Fire citizen assist at 6939` and `88yo female trauma at Erlanger via Med-17`. The fire citizen-assist title is low-value but supported by calls `768849` and `769081`.
- No active-incident redirect rows appeared after deploy.
- Verifier truncation stayed at zero. Retention mismatches were zero.
- AI availability remained healthy. Response-model normalization drift persists but is unchanged: successful rows returned `qwen3.6-35b-a3b` while request model remained explicit `qwen/qwen3.6-35b-a3b@q8_0`.

Supported incident-quality evidence:
- New missed emergency cluster: calls `769303`, `769305`, and `769307` describe a domestic/self-harm situation with a machete, but none were linked in `incident_calls`.
- Representative transcripts:
  - `769303`: domestic at `2058 Roland ... Drive NE`, still getting further.
  - `769305`: caller says she is threatening boyfriend and might try to kill herself.
  - `769307`: no weapons except a machete they put away in the house; both have been drinking.
- Audit row `28882` rejected these calls as `only 1 call(s) match the incident title/detail`, even though its parent evidence was `domestic violence with machete`. Row `28884` later rejected overlapping calls as `incident title/detail introduces unsupported emergency/event signal`.
- This is not the older hallucinated-machete problem. In this case, the word `machete` is actually present in call `769307`.
- This miss is not explained by transcription backlog, AI failure, endpoint truncation, embeddings, Qdrant, or identifier rejection.

Current read:
- Service health remains clean, and the immutable-identifier deployment is preventing row overwrite.
- User attention is still warranted because the same incident-quality failure continues: clear emergency events are rejected when proposed title/detail support fails, rather than accepting the event from retained call evidence and grounding the narrative separately. The per-prompt alias fix for active incident IDs also remains supported because the model again returned a shortened key.

### 2026-06-17 11:38Z Post-Deploy Hourly Check

Window: approximately `2026-06-17T10:38Z` to `2026-06-17T11:38Z`; compared with the prior post-deploy hour and checked against the broader two-hour context.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active. `pizzad` uptime was about `14h` after deploy.
- Queues healthy: transcription queue `0`, pending transcriptions `0`, pending audio `0`, no pressure.
- Throughput healthy: health endpoint showed recent ingest/transcribe about `7.0/7.3 calls/min`, average transcription realtime factor about `0.129`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`, pending `0`, model `text-embedding-nomic-embed-text-v1.5`.
- Last-hour calls: `264` total, `4,035s` audio. Transcript quality: `243` OK complete, `12` repetitive, `6` too short, `3` empty.
- AI endpoint summary clean: `32/32` successes, `0` failures, `0` truncations.
- Direct `lm_usage` rows for the hour showed `32` successful rows; request model stayed `qwen/qwen3.6-35b-a3b@q8_0`, response model stayed the known drifted value `qwen3.6-35b-a3b`, finish reason was `stop`, and `7` rows were marked `recovered_after_truncation_split`.
- Evidence verifier: `10` runs, average reviewed calls `6.2`, average/max truncated calls `0.5/2`, added/dropped `6/3`, retention mismatches `4`.
- Incidents: `5` current endpoint incidents, `5` creates, `4` updates, `17` rejects. Approximate create yield: `18.94 creates / 1,000 calls`; the two-hour context had `7` creates over `429` calls.
- Top buckets: accepted create `5`, low confidence lacks two strong anchored calls `4`, accepted update `4`, single-call lacks strong event signal `3`, single-call lacks anchor/location `3`, only one call matches title/detail `2`, plus isolated unknown model incident ID, location disagreement, verifier-retained ownership rows, unsupported emergency-signal, medical transport, and zero-title-match rejects.

Post-deploy identity and title evidence:
- One unknown model incident ID row appeared: `llm:whiteoakmt-hamilton:769703:elevator-entrapment-at-erlanger-med`. The actual full incident key is `llm:whiteoakmt-hamilton:769703:elevator-entrapment-at-erlanger-medical`.
- This did not lose or overwrite the incident. The full-key elevator entrapment incident retained calls `769703`, `769707`, `769709`, and `769725`.
- No blocked generic fallback titles were created after deploy in this window.
- New accepted titles were specific and mostly good: `Commercial fire alarm at 17 Parkway Drive`, `MVC with injuries on Springbrook Dr SE`, `Elevator entrapment at Erlanger Medical Center`, `Order investigation at 1210 Taff Highway`, and `MVC Goodwill Rd SE / Springplace Rd SE with injuries and smoke`.
- No active-incident redirect rows appeared after deploy.
- Verifier truncation was nonzero but small: max `2` in `10` runs. No endpoint truncation, no parse failures, and no `finish_reason=length`.
- AI availability remained healthy. Response-model normalization drift persists but is unchanged: successful rows returned `qwen3.6-35b-a3b` while request model remained explicit `qwen/qwen3.6-35b-a3b@q8_0`.

Incident-quality evidence:
- Positive signal: this hour accepted and linked several multi-call events that the recent failure class would have missed if it were total:
  - Elevator entrapment at Erlanger retained calls `769703`, `769707`, `769709`, `769725`.
  - MVC Goodwill Rd SE / Springplace Rd SE retained calls `769785`, `769787`, `769805` across EMS, sheriff, and fire traffic, including injuries and smoke.
  - Commercial fire alarm at 17 Parkway Drive expanded from `5` owned calls to `9` owned calls after update.
- Watch item, lower severity: call `769423` appears to be a clear single-dispatch EMS event, `difficulty breathing 1300 South Lee Highway SW, DP Apartments, Apartment 14`, but was rejected as lacking a concrete structured anchor/location hint. This is a representative location-extraction/anchor miss, not enough alone to interrupt after several prior notifications.
- Watch item, lower severity: call `769329` mentions an `81 year old male` with possible loss/level of consciousness but weak/garbled location; rejection as medical transport without parent evidence may be reasonable until the transcript/location quality is clearer.

Current read:
- This hour is improved overall: creates and multi-call joins recovered, runtime health is clean, and the identifier contract continues to prevent overwrites.
- Existing fix candidates remain: per-prompt aliases for model-visible incident IDs, and separating incident existence from title/detail support. No new distinct issue crossed the notification threshold in this sample.

### 2026-06-17 12:39Z Post-Deploy Hourly Check

Window: approximately `2026-06-17T11:39Z` to `2026-06-17T12:39Z`; compared with the prior post-deploy hour and checked against the broader two-hour context.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active. `pizzad` uptime was about `15h` after deploy.
- Queues healthy: transcription queue `0`, pending transcriptions `0`, pending audio `0`, no pressure.
- Throughput healthy: health endpoint showed recent ingest/transcribe about `5.4/5.4 calls/min`, average transcription realtime factor about `0.164`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`, pending `0`, model `text-embedding-nomic-embed-text-v1.5`.
- Last-hour calls: `322` total, `5,018s` audio. Transcript quality: `284` OK complete, `19` repetitive, `9` empty, `7` too short, `3` numeric noise.
- AI endpoint summary clean: `34/34` successes, `0` failures, `0` truncations.
- Direct `lm_usage` rows for the hour showed `34` successful rows; request model stayed `qwen/qwen3.6-35b-a3b@q8_0`, response model stayed the known drifted value `qwen3.6-35b-a3b`, finish reason was `stop`, and there were no failure rows.
- Evidence verifier: `16` runs, average reviewed calls `5.88`, average/max truncated calls `0/0`, added/dropped `13/2`, retention mismatches `7`.
- Incidents: `2` current endpoint incidents, `1` create, `10` updates, `17` rejects. The two-hour context had `6` creates over `601` calls, so no broad create collapse.
- Top buckets: accepted update `10`, verifier retained `5` calls but ownership retained `1` `3`, only zero calls match title/detail `2`, single-call lacks strong event signal `2`, verifier retained `3` calls but ownership retained `1` `2`, routine/status rollup lacks shared anchor `2`, unclaimed calls after existing-incident overlap failed anchor validation `2`, plus isolated title-detail, medical transport, noisy/routine, duplicate, and ownership-pruning rejects.

Post-deploy identity and title evidence:
- No `rejected:unknown model incident_id` rows in the latest hour.
- No blocked generic fallback titles were created after deploy in this window.
- New accepted title was specific: `Commercial Structure Fire Green Acres Market Georgetown Rd NW`.
- No active-incident redirect rows appeared after deploy.
- Verifier truncation stayed at zero, but retention mismatches rose from `4` to `7`. The rise is concentrated in ownership pruning around existing incidents rather than AI or service failure.
- AI availability remained healthy. Response-model normalization drift persists but is unchanged: successful rows returned `qwen3.6-35b-a3b` while request model remained explicit `qwen/qwen3.6-35b-a3b@q8_0`.

Incident-quality evidence:
- Positive signal: the Green Acres Market commercial structure fire retained `8` calls across fire and EMS/status traffic: `770297`, `770301`, `770307`, `770315`, `770337`, `770353`, `770365`, `770483`. Transcripts include commercial structure fire, smoke, Green Acres Market / Georgetown Road, breaker shut off, AC unit source, and no extension/hazards.
- Positive signal: the Goodwill/Springplace MVC continued updating and added calls `769989` and `770171` to the existing incident, preserving a cross-talkgroup traffic/EMS/fire event.
- Expected pruning: several rejects around the older Springbrook MVC and Parkway fire alarm were duplicate or unclaimed-call validation rows against incidents that already exist.
- Watch item: the ownership step narrowed some retained sets aggressively, including `verifier retained 5 call(s), final ownership retained 1`, but this hour's representative checks did not show a newly missed high-confidence incident distinct from already logged failure classes.

Current read:
- Service health and AI health are clean. The incident pipeline had a better hour for multi-call creates/updates, especially the structure fire and ongoing MVC.
- Existing fix candidates remain active, but this sample does not add a new distinct interruption-worthy issue.

### 2026-06-17 13:39Z Post-Deploy Hourly Check

Window: approximately `2026-06-17T12:39Z` to `2026-06-17T13:39Z`; compared with the prior post-deploy hour and checked against the broader two-hour context.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active.
- Queues healthy: transcription queue `0`, pending transcriptions `0`, pending audio `0`, no pressure.
- Throughput healthy: health endpoint showed recent ingest/transcribe about `6.4/6.6 calls/min`, average transcription realtime factor about `0.103`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`, pending `0`, model `text-embedding-nomic-embed-text-v1.5`.
- Last-hour calls: `397` total, `6,147s` audio. Transcript quality: `368` OK complete, `20` repetitive, `4` empty, `3` too short, `1` marker-only, `1` numeric noise.
- AI endpoint summary clean: `36/36` successes, `0` failures, `0` truncations.
- Direct `lm_usage` rows for the hour showed successful rows only; request model stayed `qwen/qwen3.6-35b-a3b@q8_0`, response model stayed the known drifted value `qwen3.6-35b-a3b`, finish reason was `stop`, and `13` rows were marked `recovered_after_truncation_split`.
- Evidence verifier: `13` runs, average reviewed calls `5.23`, average/max truncated calls `0/0`, added/dropped `2/0`, retention mismatches `5`.
- Incidents: `1` current endpoint incident, `3` creates, `5` updates, `18` rejects. Approximate create yield: `7.56 creates / 1,000 calls`; the two-hour context had `4` creates over `723` calls, so no broad create collapse.
- Top buckets: single-call lacks strong event signal `6`, accepted update `5`, only one call matches title/detail `3`, accepted create `3`, unsupported emergency/event signal `3`, verifier retained `3` calls but ownership retained `2` `2`, only zero calls match title/detail `2`, verifier retained `4` calls but ownership retained `3` `2`, plus isolated title/detail, medical transport, unsupported narrative, and noisy/routine rejects.

Post-deploy identity and title evidence:
- No `rejected:unknown model incident_id` rows in the latest hour.
- No row overwrite occurred.
- No blocked generic fallback titles were created after deploy in this window.
- No active-incident redirect rows appeared after deploy.
- New accepted incident `3747`, `llm:whiteoakmt-hamilton:770811:carbon-monoxide-alarm-at-13-ox-highway`, title `Carbon monoxide alarm at 13 Ox Highway`, was supported by call `770811`.
- AI availability remained healthy. Response-model normalization drift persists but is unchanged.

Incident-quality evidence:
- New supported false-positive watch item: incident `3745`, `llm:whiteoakmt-hamilton:770563:signal-mountain-fire-test-at-08-36-hours`, title `Signal Mountain Fire test at 08:36 hours`, was accepted in the two-hour context from calls `770563` and `770575`.
- Representative transcripts:
  - `770563`: `Signal Mountain Fire is conducting a test on June 17th, 2026 at 08-5-Hour.`
  - `770575`: `Please, test at 08.36 hours. Everyone have a great shift.`
- This is not a transcription, queue, embedding, or model availability problem. It is a false incident acceptance from explicit radio-test language.
- Lower-severity title-quality watch: incident `3746`, `Fire dispatch at 371 Borders Loop, Signal Mountain`, retained calls `770391`, `770479`, and `770505`; the transcripts describe a lift assist for an `80-year-old male` at `371 Borders Loop`. The event appears real, but the title is generic and less useful than the retained evidence.
- Sampled rejected calls `771139`, `771237`, and `771247` were a speeding-vehicle complaint and disposition/status traffic, not a clear missed emergency. Sampled `771065` was a truck hazard complaint with long-duration wording, and rejection as a weak single-call event appears reasonable.

Current read:
- The identifier and title-preservation deployment is still protecting rows: no overwrite, no unknown identifier acceptance, no active-incident redirect, and no generic fallback create.
- The meaningful new issue is a false create for explicit test traffic. A general fix should suppress explicit radio tests, tone tests, and shift-check tests before incident creation, without weakening true alarm or dispatch traffic.

### 2026-06-17 14:40Z Post-Deploy Hourly Check

Window: approximately `2026-06-17T13:40Z` to `2026-06-17T14:40Z`; compared with the prior post-deploy hour and checked against the broader two-hour context.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active.
- Queues healthy: transcription queue `2`, pending transcriptions `3`, pending audio `88s`, no pressure.
- Throughput healthy: health endpoint showed recent ingest/transcribe about `11.2/11.5 calls/min`, average transcription realtime factor about `0.137`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`, pending `0`, model `text-embedding-nomic-embed-text-v1.5`.
- Last-hour calls: `622` total, `10,469s` audio. Transcript quality: `548` OK complete, `55` repetitive, `9` empty, `5` too short, `3` pending, `1` numeric noise, `1` overexpanded.
- AI endpoint summary clean: `30/30` successes, `0` failures, `0` truncations.
- Direct `lm_usage` rows for the hour showed successful rows only; request model stayed `qwen/qwen3.6-35b-a3b@q8_0`, response model stayed the known drifted value `qwen3.6-35b-a3b`, finish reason was `stop`, and `9` rows were marked `recovered_after_truncation_split`.
- Evidence verifier: `6` runs, average reviewed calls `4.67`, average/max truncated calls `0/0`, added/dropped `0/3`, retention mismatches `1`.
- Incidents: `2` current endpoint incidents, `2` creates, `2` updates, `21` rejects. Approximate create yield: `3.22 creates / 1,000 calls`; the two-hour context had `6` creates over `1,015` calls, so creates are lower than the previous hour but not collapsed.
- Top buckets: only one call matches title/detail `6`, single-call lacks strong event signal `5`, accepted update `2`, accepted create `2`, low confidence lacks two strong anchored calls `2`, only zero calls have corroborating related calls `2`, plus isolated generic routine/status title, low pair similarity, verifier retained `3` calls but ownership retained `1`, medical transport, missing structured anchor, zero title/detail matches.

Post-deploy identity and title evidence:
- No `rejected:unknown model incident_id` rows in the latest hour.
- No row overwrite occurred.
- No blocked generic fallback titles were created after deploy in this window.
- No active-incident redirect rows appeared after deploy.
- New accepted titles were specific fire-alarm titles: `Automatic fire alarm at 975 East Strait of Erlanger` and `Automatic fire alarm at 4313 Kain Avenue`.
- AI availability remained healthy. Response-model normalization drift persists but is unchanged.

Incident-quality evidence:
- New high-confidence missed emergency: call `772163` from Dade County Fire/EMS says `2044 Highway 299, lot number 22`, `75-year-old male`, and `he was pinned between the tree and the UTV`. The audit row `29002` rejected calls `772123` and `772163` as `only 1 call(s) match the incident title/detail`.
- This is a legitimate single-dispatch rescue or trauma incident with a concrete location and mechanism. It should not depend on multiple title/detail matches before incident existence is accepted.
- Additional medical miss evidence: call `771503` dispatches chest pain for a `78-year-old female` with shortness of breath and AFib history; call `772150` is a hospital report for a `78-year-old female` with chest pain since last night, high blood pressure, and treatment details. Audit rows `28985`, `28989`, and `29003` rejected overlapping candidates as only one call matching title/detail. This reinforces the active medical-event suppression problem.
- Rejected livestock/roadway calls `771665`, `771741`, and `771953` look like a real road hazard involving sheep blocking lanes and later owner contact. They may be below the incident threshold depending on desired scope, so keep them as lower-priority watch evidence rather than a fix driver.
- The existing residential fire alarm at `74 I-Go-Gap Rd` was under-linked: only call `770941` is attached, while `770905` and `770917` appear related cancellation/treatment traffic. This is lower severity because the incident exists, but it shows ownership is still stricter than the retained evidence.

Current read:
- Runtime, transcription, embeddings, and AI remain healthy. The immutable identifier contract continues to protect existing rows.
- Incident quality still needs a real fix. The most important supported change remains separating event existence from title/detail support: create or update the incident when retained evidence contains a concrete emergency, then independently ground and simplify the title/details.

### 2026-06-17 15:42Z Post-Deploy Hourly Check

Window: approximately `2026-06-17T14:42Z` to `2026-06-17T15:42Z`; rows before the `2026-06-17T15:01:41Z` event-existence validator deployment are context only.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active. `pizzad` restarted at `2026-06-17 11:01:41 EDT`.
- Queues healthy: transcription queue `0`, pending transcriptions `0`, pending audio `0`, no pressure.
- Throughput healthy: health endpoint showed recent ingest/transcribe about `8.5/9.0 calls/min`, average transcription realtime factor about `0.144`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`, pending `0`, model `text-embedding-nomic-embed-text-v1.5`.
- Last-hour calls: `656` total, `11,007s` audio. Transcript quality: `599` OK complete, `45` repetitive, `8` too short, `4` empty.
- AI endpoint summary clean: `31/31` successes, `0` failures, `0` truncations.
- Direct `lm_usage` rows after `15:01:41Z` showed successful rows only; request model stayed `qwen/qwen3.6-35b-a3b@q8_0`, response model stayed the known drifted value `qwen3.6-35b-a3b`, finish reason was `stop`, and `8` rows were marked `recovered_after_truncation_split`.
- Evidence verifier: `7` runs, average reviewed calls `4.71`, average/max truncated calls `0.14/1`, added/dropped `5/4`, retention mismatches `4`.
- Incidents: `3` current endpoint incidents, `3` creates, `8` updates, `14` rejects. Approximate create yield: `4.57 creates / 1,000 calls`.

Post-deploy identity and title evidence:
- No `rejected:unknown model incident_id` rows appeared after the `15:01:41Z` deploy.
- No row overwrite occurred.
- No blocked generic fallback titles were created after deploy.
- No active-incident redirect rows appeared.
- Positive signal: the service accepted a real single-call gas-line strike after the new validator: incident `3752`, `llm:whiteoakmt-hamilton:773063:gas-line-strike-at-615-melville-avenue`, title `Gas line strike at 615 Melville Avenue`.
- Positive signal: existing incident update at audit row `29038` preserved the existing title/detail after an unsupported update proposal.

Incident-quality evidence:
- New high-confidence miss after the validator deploy: call `772863` says `single vehicles that flip with entrapment. Fire is on scene` near `Highway 39` / `Carol Drive`, but audit row `29028` rejected it as `single-call incident source is noisy or routine`. This is a real rescue or crash with entrapment and should survive the single-call path.
- Ownership is still too strict for some updates: gas-line incident `3752` retained only call `773063`, while related calls `773021`, `773143`, and `773329` describe the same gas-line strike / active gas leak / evacuation at Melville or Melview Avenue. Audit rows `29022`, `29026`, and `29036` show verifier retained `3` or `4` calls but final ownership retained `1`.
- Title-quality watch: incident `3753` is titled `Wanted vehicle pursuit I-75 NB mile 16`, but sampled transcripts look more like reckless/wanted-vehicle BOLO traffic than an actual pursuit:
  - `772655`: red driver BOLO on `I-75` northbound, swerving and almost hitting vehicles.
  - `773649`: BOLO for semi/trailer going toward Athens, driver moving all over the cab, possible medical issue.
  - `773455`: status/license return, not a pursuit update.
- Accepted domestic verbal incident `3751` looks supported: calls `772785`, `772791`, and `772827` describe the same domestic verbal at `40 and Newton Road SE`, later moving to the station.

Current read:
- Runtime, transcription, embeddings, AI, immutable identifiers, and title preservation are healthy.
- The validator change is only partially effective. It did accept the gas-line strike, but the single-call emergency path still rejected a flipped vehicle with entrapment as noisy/routine, and ownership still pruned obvious related gas-leak calls. The next fix should focus on the noisy/routine classifier and ownership pruning, not the model prompt.

### 2026-06-17 16:43Z Post-Deploy Hourly Check

Window: approximately `2026-06-17T15:43Z` to `2026-06-17T16:43Z`; all sampled rows are after the `2026-06-17T15:01:41Z` event-existence validator deployment.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active. `pizzad` uptime was about `1h 42m` after deploy.
- Queues healthy: transcription queue `0`, pending transcriptions `0`, pending audio `0`, no pressure.
- Throughput healthy: health endpoint showed recent ingest/transcribe about `9.0/9.3 calls/min`, average transcription realtime factor about `0.109`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`, pending `0`, model `text-embedding-nomic-embed-text-v1.5`.
- Last-hour calls: `492` total, `7,317s` audio. Transcript quality: `469` OK complete, `17` repetitive, `5` too short, `1` numeric noise.
- AI endpoint summary clean: `31/31` successes, `0` failures, `0` truncations.
- Direct `lm_usage` rows for the hour showed successful rows only; request model stayed `qwen/qwen3.6-35b-a3b@q8_0`, response model stayed the known drifted value `qwen3.6-35b-a3b`, finish reason was `stop`, and `11` rows were marked `recovered_after_truncation_split`.
- Evidence verifier: `9` runs, average reviewed calls `5.22`, average/max truncated calls `0.11/1`, added/dropped `5/5`, retention mismatches `2`.
- Incidents: `1` current endpoint incident, `1` create, `4` updates, `21` rejects. Approximate create yield: `2.03 creates / 1,000 calls`.

Post-deploy identity and title evidence:
- No `rejected:unknown model incident_id` rows appeared.
- No row overwrite occurred.
- No blocked generic fallback titles were created.
- No active-incident redirect rows appeared.
- New incident `3754`, `llm:whiteoakmt-hamilton:774739:warrant-service-at-4307-on-bonnie-and-br`, title `Warrant service at 4307 on Bonnie and Bram Rd`, was created but included one unrelated call.

Incident-quality evidence:
- New false join: incident `3754` retained call `774571`, which is a separate gray truck / vehicle all over the road BOLO, along with calls `774739` and `774763`, which are warrant-service traffic. The gray-truck call should not be part of the warrant-service incident.
- New missed road hazard: call `774803` says there is `metal siding in the road` and asks to notify transportation, but it was rejected as `low confidence lacks two strong anchored calls`. This is a legitimate road-hazard single-call candidate.
- New missed medical event: calls `774493` and `774495` both describe a `67-year-old male` at `7913 Higgins Lane` with weakness, vomiting, and inability to walk, but audit row `29054` rejected them because pair similarity was `0.14`, below the `0.20` threshold. The transcripts are clearly the same patient/location.
- New missed crash or roadway event evidence:
  - `774491` says a gray Nissan Rogue was hit by a burgundy SUV on I-40 around mile markers `253/254`, with damage, but was rejected as a standalone transport/hospital handoff because the caller was on an off-ramp and mentioned the hospital.
  - `774263` says `1800` / Highway `28` and `10 mile rollover`, but was rejected as lacking a strong emergency/event signal. The strong-signal vocabulary does not appear to catch `rollover` / `rolled over`.
- Ongoing ownership pruning issue: gas-line incident `3752` still only links call `773063`, while related calls `773021`, `773143`, `773329`, `773757`, and `774011` describe the same gas-line strike, active leak, evacuation, leak stopped, and command terminated/rendered safe.

Current read:
- Runtime, transcription, embeddings, AI, immutable identifiers, and title preservation remain healthy.
- Incident quality is still unstable after the validator deployment. The next repair should target three server-side areas: false-join ownership filtering, single-call road-hazard acceptance, and medical/crash phrase handling such as `vomiting`, `unable to walk`, `rollover`, and hit-and-run damage without letting those phrases become brittle title rules.

### 2026-06-17 17:45Z Early Post-Assembler Check

Window: first minutes after the `2026-06-17T17:31:25Z` server-owned membership assembler deployment. Rows before that deployment are context only.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active. `pizzad` active timestamp was `2026-06-17 13:31:25 EDT`.
- Queues healthy: queue depth `0`, backlog depth `0`, pending transcriptions `1-2`, pending audio `15-36s`, no pressure.
- Throughput healthy: recent ingest/transcribe about `9.5-9.6/9.9-10.0 calls/min`, average transcription realtime factor about `0.114`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, pending `0`, failed `0`, model `text-embedding-nomic-embed-text-v1.5`.
- Last-hour calls: `575` total, `9,021s` audio. Transcript quality: `528` OK complete, `38` repetitive, `6` too short, `2` empty, `1` pending.
- AI endpoint summary clean: post-assembler rows showed `6/6` successful model calls, request model `qwen/qwen3.6-35b-a3b@q8_0`, response model `qwen3.6-35b-a3b`, finish reason `stop`, and no failures or truncation.
- No incident rows were created after `2026-06-17T17:31:25Z`.
- No generic fallback incident titles were created after `2026-06-17T17:31:25Z`.

Post-assembler incident evidence:
- Audit rows after deploy were limited: `29091` accepted assembler membership for call `775137`, `29092` rejected the same candidate as unsupported narrative, `29093` and `29095` rejected call `775859` as unsupported or medical-transport context, and `29094` updated an existing delayed-accident incident with calls `775107` and `775137`.
- The delayed-accident duplicate remains from before the assembler deployment:
  - Incident `3756`, `llm:whiteoakmt-hamilton:774997:delayed-accident-at-281-lally-st-between`, title `Delayed accident at 281 Lally St between E 5th and E 4th`, contains call `774997`.
  - Incident `3758`, `llm:whiteoakmt-hamilton:774997:delayed-accident-at-281-n-lally-st`, title `Delayed accident at 281 N Lally St`, contains calls `775107` and `775137`.
  - Call `774997`: delayed accident at `281 Lally Street between East Fifth and East Fourth`.
  - Call `775107`: delayed accident at `281 North Lout/North Lally` between cross streets.
  - Call `775137`: same caller standing by at `281 North Lowy Street`.
- This is not a new post-assembler duplicate creation. It is evidence that the new assembler does not merge or repair sibling incidents that already exist.

Current read:
- Runtime, transcription, embeddings, AI, title preservation, and generic-title rejection are healthy in the first post-assembler sample.
- The remaining issue is duplicate-sibling repair. The new membership assembler prevents some bad future membership choices, but it does not yet merge two already-created incidents that share the same real-world event. Keep watching for new sibling duplicates after `2026-06-17T17:31:25Z`; if they appear, the assembler still needs more work. If only old siblings remain, the follow-up is a separate read-audit/manual-review merge path rather than another live membership tweak.

### 2026-06-17 18:46Z Post-Redesign Hourly Check

Window: latest hour ending around `2026-06-17T18:47Z`; rows before the `2026-06-17T18:05:30Z` redesign deployment are context only.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active. `pizzad` active timestamp was `2026-06-17 14:05:30 EDT`.
- Queues healthy: queue depth `0`, backlog depth `0`, pending transcriptions `0`, pending audio `0s`, no pressure.
- Throughput healthy: recent ingest/transcribe about `11.5/12.4 calls/min`, average transcription realtime factor about `0.113`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, pending `0`, failed `0`, model `text-embedding-nomic-embed-text-v1.5`.
- Last-hour calls: `670` total, `10,454s` audio. Transcript quality: `602` OK complete, `48` repetitive, `11` empty, `7` too short, `2` numeric noise.
- AI endpoint summary clean: `38/38` successes, `0` failures, `0` truncations. Post-redesign direct rows: request model `qwen/qwen3.6-35b-a3b@q8_0`, response model `qwen3.6-35b-a3b`, finish reason `stop`; `2` rows carried `recovered_after_truncation_split`.
- Evidence verifier healthy: `20` runs, average reviewed calls `5.5`, average/max truncated calls `0/0`, added/dropped `7/6`, retention mismatches `0`.
- Incidents endpoint for the hour: `5` creates, `11` updates, `9` rejects, approximate create yield `7.46 creates / 1,000 calls`. Post-redesign audit rows specifically showed `4` accepted creates and `9` accepted updates, plus assembler-retention audit rows.

Post-redesign identity and title evidence:
- New create keys are now title-independent: examples include `llm:whiteoakmt-hamilton:776693:event`, `llm:whiteoakmt-hamilton:776719:event`, `llm:whiteoakmt-hamilton:776831:event`, and `llm:whiteoakmt-cleveland:777089:event`.
- No `rejected:unknown model incident_id` rows appeared after `2026-06-17T18:05:30Z`.
- No generic fallback titles such as `Police call`, `EMS call`, `Fire call`, `Traffic call`, or `Public safety call` were created after `2026-06-17T18:05:30Z`.
- No model failures, no local-model routing, no `finish_reason=length`, and no reasoning-content regression observed.
- No `server_sibling_merge_repair` rows appeared in this first sample.

Incident-quality evidence:
- Positive signal: incident `3763`, `llm:whiteoakmt-hamilton:776831:event`, title `Power pole fire on East 19/206`, now retains calls `776889` and `776907`. Sampled transcripts support the same wires-down or power-pole-fire event near East 19th / Watkins / Buckley. The assembler correctly excluded unrelated commercial-alarm calls `776897` and `776915`.
- Positive signal: incident `3761`, `llm:whiteoakmt-hamilton:776693:event`, title `MVC with injuries on Highway 52 West`, retains dispatch and related EMS/handoff traffic for a multi-vehicle crash with injuries.
- Positive signal: incident `3764`, `llm:whiteoakmt-cleveland:777089:event`, title `Vehicle abandoned in road at 2024 Room SE`, retains the vehicle-in-road call plus vehicle return/update calls.
- New supported watch item: pre-existing incident `3760`, `llm:whiteoakmt-cleveland:776157:fall-from-bridge-disturbance-at-1332-wee`, still retained unrelated call `775859` after post-redesign updates.
  - `775859`: `24646 Court and Lane Southeast`, `91-year-old female`, pacemaker, facial drooping, EMS route.
  - `776051`, `776157`, `776175`, and `776275`: disturbance / female at `1332/1342 Weeks`, hospital mention, and report that the female fell off a bridge or porch.
  - The unrelated medical call appears to be preserved because existing incident calls are kept unless the verifier explicitly hard-rejects them. The redesign does not yet actively prune old bad calls when updating an existing incident.
- Existing duplicate watch remains: delayed-accident sibling incidents `3756` and `3758` are still separate because their last-seen times were outside the rolling repair window by the `18:05:30Z` deployment. This is not a new post-redesign duplicate, but it shows that the live rolling repair does not clean up older active duplicates.

Current read:
- Runtime, transcription, embeddings, AI routing, truncation behavior, new key generation, and generic-title rejection are healthy.
- The main remaining fix candidate is historical and existing-row repair: the new live path handles new keys and some future membership decisions, but it still preserves bad existing incident calls unless the verifier directly rejects them, and it does not repair older sibling duplicates outside the rolling state window.

### 2026-06-17 19:47Z Post-Redesign Hourly Check

Window: latest hour ending around `2026-06-17T19:47Z`; rows before the `2026-06-17T18:05:30Z` redesign deployment are context only.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active. `pizzad` active timestamp remained `2026-06-17 14:05:30 EDT`.
- Queues healthy: queue depth `0`, backlog depth `0`, pending transcriptions `0`, pending audio `0s`, no pressure.
- Throughput healthy: recent ingest/transcribe about `12.4/13.0 calls/min`, average transcription realtime factor about `0.101`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, pending `0`, failed `0`, model `text-embedding-nomic-embed-text-v1.5`.
- Last-hour calls: `714` total, `11,944s` audio. Transcript quality: `636` OK complete, `60` repetitive, `9` empty, `6` too short, `2` numeric noise, `1` overexpanded.
- AI endpoint summary clean: `41/41` successes, `0` failures, `0` truncations.
- Evidence verifier healthy by counters: `22` runs, average reviewed calls `4.05`, average/max truncated calls `0/0`, added/dropped `6/5`, retention mismatches `0`.
- Incident endpoint: `9` creates, `13` updates, `6` rejects, approximate create yield `12.61 creates / 1,000 calls`. This is higher than the preceding hour's `5` creates over `670` calls, but not obviously collapsed or runaway by volume alone.

Post-redesign identity and title evidence:
- New create keys continue to use the title-independent `:event` format, for example `llm:whiteoakmt-cleveland:778877:event`, `llm:whiteoakmt-hamilton:778327:event`, `llm:whiteoakmt-cleveland:778145:event`, and `llm:whiteoakmt-hamilton:778669:event`.
- No `rejected:unknown model incident_id` rows appeared after `2026-06-17T18:05:30Z`.
- No generic fallback titles such as `Police call`, `EMS call`, `Fire call`, `Traffic call`, or `Public safety call` were observed after deploy.
- No model failures, no local-model routing, no `finish_reason=length`, and no reasoning-content regression observed.
- No `server_sibling_merge_repair` rows appeared in this sample.

New regression evidence:
- High-priority supported regression: incident `3763`, `llm:whiteoakmt-hamilton:776831:event`, is being alternately updated with two different fire events because the server accepts the model's exact existing `incident_id` before enforcing same-event evidence against the existing incident membership.
- Audit chain for `llm:whiteoakmt-hamilton:776831:event`:
  - `29110` at `18:20:30Z`: create with calls `776831,776889`.
  - `29120/29121` at `18:36:48Z`: update retained calls `776897,776915` after excluding true power-pole calls `776831,776859,776889,776907`.
  - `29123/29124` at `18:43:15Z`: update switched back to calls `776889,776907`, excluding commercial-alarm calls `776897,776915`.
  - `29126/29127` at `18:49:56Z`: update switched again to calls `776897,776915`.
  - `29134/29135` at `18:58:11Z` and `29139/29140` at `19:10:28Z`: update switched back to calls `776889,776907`.
- Transcript evidence shows these are separate events:
  - `776889`: `206 East 19`, power pole fire, no one around it, fire department en route.
  - `776907`: `240 19th Street` / South Watkins / Buckley, changed to wires down, power pole smoking white smoke.
  - `776897`: `1110 Market Street`, commercial fire alarm.
  - `776915`: responding to `1110 Market Street`.
- Current database row happens to be correct at the time of sampling: title `Power pole fire on East 19/206`, calls `776889,776907`. The regression is still real because audit rows prove the same incident was incorrectly overwritten with unrelated commercial-alarm membership multiple times.

Other incident-quality notes:
- The previous existing-row repair watch remains active: old unrelated memberships can persist unless directly hard-rejected.
- The older delayed-accident duplicate remains outside the rolling repair window.
- Recent created incidents sampled in the endpoint output looked specific rather than generic: stroke call, harassment report, possible overdose, child welfare check, auto theft report, possible `10-99`, and fire alarm / MVC style rows. Some vehicle-stop rows may need future policy review, but they are not the primary supported regression in this window.

Current read:
- Runtime, transcription, embeddings, AI routing, truncation behavior, and generic-title rejection remain healthy.
- The redesign has a live correctness bug: exact existing model incident identifiers still bypass evidence-based target selection. The fix should require same-event evidence between retained calls and existing incident calls before allowing an exact-id update. If the retained calls are a different event, the server should either create a new server-owned incident key from those retained calls or reject the model update; it must not overwrite the existing incident membership just because the model copied an existing identifier.

### 2026-06-17 20:47Z Post-Exact-Identifier-Gate Check

Window: latest hour ending around `2026-06-17T20:48Z`; rows before the `2026-06-17T20:20:18Z` exact-identifier evidence-gate deployment are context only.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active. `pizzad` active timestamp was `2026-06-17 16:20:18 EDT`.
- Queues healthy: queue depth `0`, backlog depth `0`, pending transcriptions `1`, pending audio about `10s`, no pressure.
- Throughput healthy: recent ingest/transcribe about `13.2/13.4 calls/min`, average transcription realtime factor about `0.116`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, pending `0`, failed `0`, model `text-embedding-nomic-embed-text-v1.5`.
- Last-hour calls: `695` total, `11,391s` audio. Transcript quality: `633` OK complete, `46` repetitive, `8` empty, `5` too short, `2` numeric noise, `1` pending.
- AI endpoint summary mostly clean: `46/47` successes, `1` failure, `0` truncations. No `finish_reason=length` issue surfaced in endpoint counters.
- Evidence verifier healthy by counters: `22` runs, average reviewed calls `3.86`, average/max truncated calls `0/0`, added/dropped `4/4`, retention mismatches `0`.
- Incident endpoint: `9` creates, `12` updates, `11` rejects, approximate create yield `12.95 creates / 1,000 calls`.

Post-exact-identifier-gate evidence:
- Positive signal: after `20:20:18Z`, exact-id update audit reasons changed from `accepted exact existing incident_id` to `accepted exact existing incident_id after retained evidence match`.
- Positive signal: no `rejected:unknown model incident_id` rows appeared after the deploy.
- Positive signal: no generic fallback titles such as `Police call`, `EMS call`, `Fire call`, `Traffic call`, or `Public safety call` were observed after deploy.
- Positive signal: previously unstable incident `3763`, key `llm:whiteoakmt-hamilton:776831:event`, remained `Power pole fire on East 19/206` with calls `776889,776907`.

New regression evidence:
- The exact-id gate prevents full replacement with a different event, but it still allows unrelated new calls to be added when one existing call remains in the retained set.
- Incident `3775`, key `llm:whiteoakmt-cleveland:778881:event`, title `Possible Q&A/subject at North Dakota St and CVS area`, now contains calls `778881,779093,779317,780183`.
- Transcript evidence shows mixed events:
  - `778881`: Cleveland Police call around `CVS`, `North Dakota Street`, possible subject, vehicle descriptions.
  - `779093`: EMS route to `100 Mustang Drive Northwest`.
  - `779317`: Sheriff backup for EMS at `100 Mustang Drive Northwest` for an intoxicated foster patient.
  - `780183`: hospital encode for a `67-year-old female`, low blood pressure, from across the street.
- Audit evidence:
  - `29181` and `29182` at `20:27:23Z` retained only `778881`.
  - `29184` at `20:33:58Z` updated the same incident with `778881,779187`.
  - `29187` and `29188` at `20:41:02Z` accepted `778881,779093,779317,780183` and excluded `779187`.
- This points to a narrower remaining bug: an existing incident update can pass the target evidence gate because one retained call overlaps the existing incident, while unrelated newly retained calls are still admitted by the membership assembler.

Current read:
- Runtime, transcription, embeddings, AI routing, truncation behavior, new key generation, generic-title rejection, and whole-row identifier overwrite behavior are healthy.
- A new fix is supported: for existing incident updates, require every newly added call to have strong same-event evidence against existing target calls or an already accepted new member. Overlap with one old call must not authorize unrelated new calls to ride along.

### 2026-06-17 21:47Z Post-Membership-Gate Check

Window: latest hour ending around `2026-06-17T21:48Z`; rows before the `2026-06-17T21:22:05Z` membership-gate deployment are context only.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active. `pizzad` active timestamp was `2026-06-17 17:22:05 EDT`.
- Queues healthy: queue depth `0`, backlog depth `0`, pending transcriptions `1`, pending audio about `30s`, no pressure.
- Throughput healthy: recent ingest/transcribe about `10.9/11.2 calls/min`, average transcription realtime factor about `0.104`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, pending `0`, failed `0`, model `text-embedding-nomic-embed-text-v1.5`.
- Last-hour calls: `662` total, `10,684s` audio. Transcript quality: `609` OK complete, `40` repetitive, `7` too short, `3` empty, `2` numeric noise, `1` marker-only.
- AI endpoint summary clean: `46/46` successes, `0` failures, `0` truncations.
- Evidence verifier healthy by counters: `22` runs, average reviewed calls `4.0`, average/max truncated calls `0/0`, added/dropped `4/6`, retention mismatches `0`.
- Incident endpoint: `10` creates, `12` updates, `12` rejects, approximate create yield `15.11 creates / 1,000 calls`.

Post-membership-gate evidence:
- Positive signal: exact-id update reasons after `21:22:05Z` continue to say `accepted exact existing incident_id after retained evidence match`, not the older unqualified exact-id acceptance.
- Positive signal: audit row `29218` rejected calls `780889,780913,780945` with reason `assembler retained no event-member calls; verifier_rejected=[780889,780913]; weak=[780945]`.
- Positive signal: audit row `29223` retained only calls `781331,781411` for `llm:whiteoakmt-hamilton:781331:event` and explicitly excluded weak/unrelated call `781425`.
- Positive signal: audit row `29226` rejected weak call `781447` instead of attaching it to an existing incident.
- No post-deploy `rejected:unknown model incident_id` rows observed.
- No generic fallback titles such as `Police call`, `EMS call`, `Fire call`, `Traffic call`, or `Public safety call` were observed after deploy.
- No post-deploy title/detail overwrite evidence surfaced in this sample.

Current read:
- Runtime, transcription, embeddings, AI routing, truncation behavior, new key generation, generic-title rejection, title preservation, and whole-row identifier overwrite behavior are healthy.
- The first post-membership-gate sample shows the intended behavior: weak or unrelated proposed calls were excluded or rejected rather than added to an existing incident because one old call made the update valid.
- No new supported regression or fix candidate is present yet; keep monitoring because the post-deploy sample is still short.

### 2026-06-17 22:47Z Post-Membership-Gate Check

Window: latest hour ending around `2026-06-17T22:48Z`; rows before the `2026-06-17T21:22:05Z` membership-gate deployment are context only.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active. `pizzad` active timestamp remained `2026-06-17 17:22:05 EDT`.
- Queues healthy: queue depth `0-1`, backlog depth `0`, pending transcriptions `0-2`, pending audio `0-20s`, no pressure.
- Throughput healthy: recent ingest/transcribe about `7.9/8.2 calls/min`, average transcription realtime factor about `0.115`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, pending `0`, failed `0`, model `text-embedding-nomic-embed-text-v1.5`.
- Last-hour calls: `553` total, `9,150s` audio. Transcript quality: `509` OK complete, `32` repetitive, `6` too short, `4` empty, `1` pending, `1` marker-only.
- AI endpoint summary clean: quality-check reported `44/44` successes, `0` failures, `0` truncations. Direct rows since `21:47Z` showed request model `qwen/qwen3.6-35b-a3b@q8_0`, response model `qwen3.6-35b-a3b`, finish reason `stop`, and no errors.
- Evidence verifier counters remained healthy: `26` runs, average reviewed calls `4.58`, average/max truncated calls `0/0`, added/dropped `8/12`, retention mismatches `0`.
- Incident endpoint: `7` creates, `16` updates, `5` rejects, approximate create yield `12.66 creates / 1,000 calls`.

Positive signals:
- Several weak or unrelated proposed calls were excluded after the deploy, including audit rows `29247`, `29249`, `29253`, `29258`, `29261`, and `29263`.
- No post-deploy `rejected:unknown model incident_id` rows were observed.
- No generic fallback titles such as `Police call`, `EMS call`, `Fire call`, `Traffic call`, or `Public safety call` were created.
- No service, queue, transcription, AI routing, truncation, embedding, or Qdrant issue surfaced.

New regression evidence:
- Existing-update membership is still not fully fixed. Incident `3788`, key `llm:whiteoakmt-hamilton:780913:event`, was `780913,780945` at audit rows `29230`/`29231`, then audit row `29233` accepted `780801,780913,780945`.
  - `780801`: crash at `1800 South Creek Road` near the cement plant.
  - `780913`: Thrasher Pike / Harper Road speeding or near-collision complaint.
  - `780945`: corrected crash location `1201 Suck Creek Road`, vehicle out of roadway.
  - These transcripts do not support one incident even though the update was accepted after the new gate.
- Create-path membership is also still mixing calls before the incident is saved. Incident `3796`, key `llm:whiteoakmt-hamilton:781123:event`, joined `781123` and `781987` at audit row `29246`.
  - `781123`: `Wheeler Avenue` / `Wilson Street`, 70-year-old female with chest pain and difficulty breathing.
  - `781987`: `1201 West North Main`, apartment 316, unconscious patient breathing.
  - The title follows `781987`; the retained calls do not show the same event.
- Title/detail grounding can still become stale after membership correction. Incident `3799`, key `llm:whiteoakmt-hamilton:782045:event`, currently keeps calls `782611,782621`, both about an unconscious female at `9469 Bradmore Lane / Cambridge Square`, but the saved title/detail still say fire alarm. Audit row `29257` initially mixed those calls with false fire alarm calls `782045,782101`; row `29258` then kept only `782611,782621`, leaving the old title/detail behind.

Current read:
- Infrastructure and model routing are healthy.
- The membership gate improved some exclusions, but it did not solve the core evaluation-pipeline issue. The remaining fix should not be another wording tweak or isolated threshold. The pipeline needs one final server-owned membership decision for both creates and updates, and title/detail acceptance must happen after that final call set is known.

### 2026-06-17 23:48Z Post-Membership-Gate Check

Window: latest hour ending around `2026-06-17T23:48Z`; rows before the `2026-06-17T21:22:05Z` membership-gate deployment are context only.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active. `pizzad` active timestamp remained `2026-06-17 17:22:05 EDT`.
- Queues healthy: queue depth `0`, backlog depth `0`, pending transcriptions `0`, pending audio `0s`, no pressure.
- Throughput healthy: recent ingest/transcribe about `8.8/9.3 calls/min`, average transcription realtime factor about `0.107`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, pending `0`, failed `0`, model `text-embedding-nomic-embed-text-v1.5`.
- Last-hour calls: `511` total, `8,040s` audio. Transcript quality: `472` OK complete, `30` repetitive, `4` empty, `3` too short, `1` pending, `1` numeric noise.
- AI endpoint summary clean: `44/44` successes, `0` failures, `0` truncations. Direct rows since `22:48Z` used request model `qwen/qwen3.6-35b-a3b@q8_0`, response model `qwen3.6-35b-a3b`, finish reason `stop`, with no errors; `9` rows were marked `recovered_after_truncation_split`.
- Evidence verifier counters stayed healthy: `21` runs, average reviewed calls `3.76`, average/max truncated calls `0/0`, added/dropped `5/8`, retention mismatches `0`.
- Incident endpoint: `13` creates, `6` updates, `16` rejects, approximate create yield `25.44 creates / 1,000 calls`. The higher create yield came with lower call volume and did not show a service failure.

Incident-quality evidence:
- Positive signal: multiple weak or rejected candidate calls were excluded, including audit rows `29276`, `29278`, `29282`, `29290`, `29299`, and `29301`.
- Sibling repair produced accepted merge rows at `29292` and `29293` for candidate traffic or trailer-fire siblings; no bad sibling merge was proven in this sample.
- Continuing issue, already covered by the active watch item: incident `3803`, key `llm:whiteoakmt-cleveland:783137:event`, briefly accepted Old Eureka Road calls `783269` and `783339` alongside Rico Road / Candies Creek trash-fire calls at rows `29294`/`29295`; a later row `29299` removed `783269` and `783339`. This shows the system can self-correct some mixed memberships, but still admits them transiently.
- Continuing title-quality issue: incident `3810`, key `llm:whiteoakmt-hamilton:783465:event`, title `Vehicle trapped in bathroom at 2020 Gumbar Road`, appears to follow a single transcript that says `car already trapped inside the bathroom`. This looks like transcription or transcript-interpretation error rather than a multi-call join problem.

Current read:
- Runtime, transcription throughput, embeddings, AI routing, truncation behavior, and queue health are stable.
- No new distinct fix candidate appeared beyond the already-supported redesign need: use one final server-owned membership set for both creates and updates, then generate or preserve title/detail from that final set only.
- Because the significant pipeline issue was already reported in the previous hour and this sample mostly repeats or partially self-corrects that pattern, no additional user interruption is needed for this run.

### 2026-06-18 00:48Z Post-Membership-Gate Check

Window: latest hour ending around `2026-06-18T00:48Z`; rows before the `2026-06-17T21:22:05Z` membership-gate deployment are context only.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active. `pizzad` active timestamp remained `2026-06-17 17:22:05 EDT`.
- Queues healthy: queue depth `0`, backlog depth `0`, pending transcriptions `0`, pending audio `0s`, no pressure.
- Throughput healthy: recent ingest/transcribe about `9.2/9.4 calls/min`, average transcription realtime factor about `0.103`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`, model `text-embedding-nomic-embed-text-v1.5`; one embedding job was pending for a fresh call at sample time.
- Last-hour calls: `503` total, `7,893s` audio. Transcript quality: `456` OK complete, `35` repetitive, `6` empty, `6` too short.
- AI endpoint summary clean: `52/52` successes, `0` failures, `0` truncations. Direct rows since `23:48Z` used request model `qwen/qwen3.6-35b-a3b@q8_0`, response model `qwen3.6-35b-a3b`, finish reason `stop`, with no errors.
- Evidence verifier counters stayed healthy: `29` runs, average reviewed calls `3.76`, average/max truncated calls `0/0`, added/dropped `6/6`, retention mismatches `0`.
- Incident endpoint: `8` creates, `14` updates, `12` rejects, approximate create yield `15.90 creates / 1,000 calls`.

Incident-quality evidence:
- Positive signal: the membership gate continued excluding weak or unrelated proposed calls in rows including `29312`, `29318`, `29324`, `29327`, `29330`, `29342`, and `29340`.
- Positive signal: no `rejected:unknown model incident_id` rows, no generic fallback titles, no `finish_reason=length`, no local-model routing, and no title/detail update overwrite reason appeared in this sample.
- Continuing issue: incident `3803`, key `llm:whiteoakmt-cleveland:783137:event`, is currently mixed. Current database calls are `782975,783137,783269,783275,783339,783439`. Transcripts split into at least two locations:
  - `782975`, `783137`, `783275`, and `783439`: Rico Road / Candies Creek trash-fire or burn-law event.
  - `783269` and `783339`: Old Eureka Road / Beaver Dam / 373 Old Eureka Road.
- This is the same already-reported membership problem, but now the mixed call set is the current saved state rather than only a transient audit row.

Current read:
- Runtime, transcription, embeddings, AI routing, truncation behavior, queue health, and rejection of unsupported generic titles are stable.
- The only supported fix direction remains the same: one final server-owned membership decision for both creates and updates, followed by title/detail generation or preservation from that final membership set only.
- No new separate fix candidate appeared beyond the already-reported membership redesign issue.

### 2026-06-18 01:52Z Post-Final-Membership Check

Window: latest hour ending around `2026-06-18T01:52Z`; rows before the `2026-06-18T01:48:42Z` final-membership deployment are context only.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active. `pizzad` active timestamp is `2026-06-17 21:48:42 EDT`, with `0` restarts.
- Queues healthy: queue depth `0`, backlog depth `0`, pending transcriptions `1`, pending audio `7s`, no pressure.
- Throughput healthy: recent ingest/transcribe about `6.4/6.8 calls/min`, average transcription realtime factor about `0.138`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, pending `0`, failed `0`, model `text-embedding-nomic-embed-text-v1.5`.
- Last-hour calls: `428` total, `6,958s` audio. Transcript quality: `385` OK complete, `29` repetitive, `6` too short, `5` empty, `2` numeric noise, `1` pending.
- AI endpoint summary clean: `40/40` successes, `0` failures, `0` truncations.
- Evidence verifier counters stayed healthy: `20` runs, average reviewed calls `7.05`, average truncated calls `1.6`, max truncated calls `5`, added/dropped `2/4`, retention mismatches `0`.
- Incident endpoint: `2` creates, `17` updates, `2` rejects in the last hour.

Post-deploy regression evidence:
- The new final-membership pass fired immediately at audit row `29380`, incident `3822`, key `llm:whiteoakmt-hamilton:784951:event`. It retained only calls `784951` and `784957` from an assembler-retained set of `11` calls and pruned `9` calls.
- Manual transcript review shows the pruned calls are mostly legitimate follow-up calls for the same Harrison Bay paddleboard water rescue, not conflicting incidents:
  - `784961`: fire dispatch to Harrison Bay / South Point Trailhead / 5000 Campground Drive for a paddleboarder currently in the water.
  - `785001`: sheriff update that a paddleboarder is treading water with a life jacket and State Park has contact.
  - `785003`: fireground call about 5000 Campground, marina circle, and a subject 50 feet or yards off the water.
  - `785029`: fire call about launching a boat for 5000 Campground / paddleboarder about 50 yards off the waterway.
  - `785065`: fireground update that a paddleboarder is stranded and then headed back toward shore.
  - `785081`: fire dispatch asks about a reported drowning at Harrison Bay; response says paddleboarder is en route to shore.
  - `785091`: rescue command says park rangers have a boat and one swimmer in the water.
- This means the final membership pass fixed one failure mode, but introduced an over-pruning risk for same-event follow-up calls whose transcripts use slightly different or noisy location wording.

Current read:
- Runtime, transcription, embeddings, AI routing, and queue health are stable.
- The new final membership check is active and prevents broad mixed saves, but it is too strict for legitimate follow-up traffic on the same incident. This needs attention before we trust this deployment as a full fix.

Correction deployed at `2026-06-18T02:46:46Z`:
- Root cause: final membership validation was allowed to replace the saved call list with the validator's smaller matched subset. That recreated a later-layer membership rewrite.
- Additional cause: weak transcript-derived address and location hints, such as `1701 Highway`, `Highway 50`, and `Highway 58`, were treated as hard final-membership conflict anchors.
- Code correction: final validation now only accepts or rejects the retained membership set and does not rewrite it. Weak transcript-derived address, intersection, and location-hint anchors no longer create hard final-membership conflicts.
- Monitor next: verify that legitimate same-event continuation calls stay attached while explicit concrete conflicts still get removed.

### 2026-06-18 03:10Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T03:10Z`; rows before the `2026-06-18T02:46:46Z` final-membership over-pruning correction are context only.

Observed:
- Services healthy after deploy: `pizzad`, `trunk-recorder`, and `qdrant` active. `pizzad` active timestamp is `2026-06-17 23:09:22 EDT`, with `0` restarts.
- Queues healthy: queue depth `0`, backlog depth `0`, pending transcriptions `0`, pending audio `0s`, no pressure.
- Throughput healthy: recent ingest/transcribe about `6.8/7.1 calls/min`, average transcription realtime factor about `0.114`.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, pending `0`, failed `0`, model `text-embedding-nomic-embed-text-v1.5`.
- Last-hour calls: `489` total, `7,658s` audio. Transcript quality: `441` OK complete, `35` repetitive, `10` empty, `2` too short, `1` numeric noise.
- AI summary mostly clean: `38` requests, `37` successes, `1` failure, `0` truncations.
- Evidence verifier counters stayed healthy: `16` runs, average reviewed calls `5.5`, average/max truncated calls `0/0`, added/dropped `3/1`, retention mismatches `0`.
- Incident endpoint: `4` creates, `10` updates, `6` rejects in the last hour.

Evidence reviewed:
- Incident `3827`, key `llm:whiteoakmt-cleveland:786597:event`, was saved before this run as `Vehicle pursuit near O'Coley winery and Apple Orchard Place`.
- Retained calls `786597`, `786707`, and `786723` describe a vehicle off roadway or crash near Water Level Highway, O'Coley winery, and Apple Orchard Place. The retained transcripts do not support a pursuit.
- Root cause in code: the title grounder treated generic shared words such as `vehicle` as partial support for the title lead `Vehicle pursuit`, so the unsupported event type `pursuit` was not fatal.
- Related continuation risk remains: domestic incident `3826`, key `llm:whiteoakmt-hamilton:786327:event`, repeatedly excluded calls `786375` and `786395`, which look like same-incident vehicle/person follow-up calls. This needs more samples before another membership change.

Action taken:
- Deployed title-grounding correction at `2026-06-18T03:09:22Z`.
- Code now requires concrete event types in incident titles to be supported by retained call evidence. Generic shared words can no longer partially support an unsupported event type.
- Code now recognizes retained vehicle-off-roadway and roadway-hazard evidence as specific fallback support, so a bad model title can become a supported traffic title instead of staying wrong or becoming a generic public-safety title.
- Full solution test suite passed before deploy: `114/114`.
- Automation boundary updated to `2026-06-18T03:09:22Z`.

Residual risk:
- There were no post-`03:09:22Z` incident audit rows at the time of this check, so the deployment is service-verified but not yet behaviorally proven by a fresh incident.
- The existing stale title on incident `3827` was not manually repaired because production data should not be mutated directly during this bake. A future evidence-backed update should rewrite it if the final retained call set still supports the traffic narrative.
- The possible missed continuation calls on incident `3826` remain on watch; avoid changing membership again until repeated evidence shows the same failure pattern after the latest title correction.

### 2026-06-18 03:23Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T03:23Z`; rows before the `2026-06-18T03:09:22Z` title-grounding deployment are context only.

Observed:
- Services healthy after deploy: `pizzad`, `trunk-recorder`, and `qdrant` active. `pizzad` active timestamp is `2026-06-17 23:23:09 EDT`, with `0` restarts.
- Queues healthy: queue depth `2`, backlog depth `0`, pending transcriptions `3`, pending audio `41s`, no pressure.
- Throughput healthy: recent ingest/transcribe about `7.8/8.1 calls/min`, average transcription realtime factor about `0.182` in the first post-restart sample.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, pending `0`, failed `0`, model `text-embedding-nomic-embed-text-v1.5`.
- Last-hour calls: `471` total, `7,386s` audio. Transcript quality: `423` OK complete, `35` repetitive, `7` empty, `3` pending, `2` too short, `1` numeric noise.
- AI summary mostly clean: `38` requests, `36` successes, `2` failures, `0` truncations. Direct rows show one failure at the prior deploy boundary from cancellation; successful rows used request model `qwen/qwen3.6-35b-a3b@q8_0`, response model `qwen3.6-35b-a3b`, finish reason `stop`.
- Evidence verifier counters stayed healthy: `18` runs, average reviewed calls `5.56`, average/max truncated calls `0/0`, added/dropped `5/1`, retention mismatches `0`.
- Incident endpoint: `4` creates, `13` updates, `3` rejects in the last hour.

Evidence reviewed:
- Positive proof for the `03:09:22Z` title-grounding deploy: incident `3827`, key `llm:whiteoakmt-cleveland:786597:event`, was rewritten from unsupported pursuit language to `Vehicle off roadway at O'Coley winery and Apple Orchard Place`. Current retained calls are `786597`, `786707`, and `786723`, matching the vehicle-off-roadway evidence.
- New incident `3828`, key `llm:whiteoakmt-hamilton:787099:event`, retained calls `787099` and `787121`. Transcripts support the saved title `Large fire at mulch plant on Infantry Rd near Dodson Ave`.
- New missed-incident evidence: audit row `29422` rejected calls `787109`, `787119`, and `787125` as `routine status/compliance rollup lacks a shared concrete event anchor`.
  - `787109`: jail subject not completely responsive, fire and EMS en route, possible fentanyl.
  - `787119`: jail subject not completely responsive, possible fentanyl, fire and EMS en route.
  - `787125`: `105 South Duke`, Sheriff's Department, subject not completely responsive, possible overdose, squad and medic response.
- Root cause in code: routine-status rejection ran before corroborated emergency evidence could validate the group, and existing emergency wording did not cover `not completely responsive` or `fentanyl`.

Action taken:
- Deployed emergency-evidence validation correction at `2026-06-18T03:23:09Z`.
- Code now prevents routine-status rejection from overriding multiple corroborating emergency-evidence calls.
- Code now treats `not completely responsive` as unresponsive-patient evidence and `fentanyl` as overdose evidence in both validation and narrative grounding.
- Full solution test suite passed before deploy: `114/114`.
- Automation boundary updated to `2026-06-18T03:23:09Z`.

Residual risk:
- There were no post-`03:23:09Z` incident audit rows at the time of this check, so the new emergency-validation correction is service-verified but not yet behaviorally proven by a fresh incident.
- The possible overdose represented by row `29422` was not manually repaired because production data should not be mutated directly during this bake.
- The domestic continuation watch remains active: incident `3826`, key `llm:whiteoakmt-hamilton:786327:event`, continues to exclude likely same-incident follow-up calls `786375` and `786395`. Do not change this path until another post-`03:23:09Z` sample confirms the same failure mode.

### 2026-06-18 03:38Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T03:39Z`; rows before the `2026-06-18T03:23:09Z` emergency-validation deployment are context unless needed for comparison.

Observed:
- Services healthy after deploy: `pizzad`, `trunk-recorder`, and `qdrant` active. `pizzad` active timestamp is `2026-06-17 23:36:56 EDT`, with `0` restarts.
- Queues healthy: queue depth `1`, pending transcriptions `0`, pending audio `34s`, no pressure.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`.
- Last-hour calls: `484` total, `7,680s` audio. Transcript quality: `445` complete OK, `28` repetitive, `8` empty, `1` pending, `1` numeric noise, `1` too short.
- AI summary mostly clean: `39` requests, `37` successes, `2` failures, `0` truncations.
- Evidence verifier counters stayed healthy: `20` runs, average reviewed calls `5.55`, average/max truncated calls `0/0`, added/dropped `6/2`, retention mismatches `0`.
- Incident endpoint: `3` creates, `16` updates, `5` rejects in the last hour.

Evidence reviewed:
- Positive proof for the `03:23:09Z` emergency-validation deploy: the possible overdose did create after the routine-status fix. Incident `3829`, key `llm:whiteoakmt-hamilton:787125:event`, was saved as `Possible overdose at 105 S Duke St`.
- Remaining issue: the saved incident used only call `787125`; earlier audit row `29422` had grouped `787109`, `787119`, and `787125` together before rejection. Those calls describe the same jail or Sheriff's Department emergency with `not completely responsive`, `possible fentanyl`, fire, EMS, squad, and medic response.
- Root cause in code: candidate review was still too dependent on a concrete location anchor. The two police-side continuation calls had the same emergency signal and time proximity but no concrete address, so they were not reviewed after the single address-bearing call created the incident.
- Separate watch-only item: row `29432` rejected single traffic call `787413` even though metadata carried `suck creek rd`; wait for repeats before changing this path.

Action taken:
- Deployed verifier candidate-review broadening at `2026-06-18T03:36:56Z`.
- Code now lets close, same-dominant-emergency candidates with a strong incident signal reach the evidence verifier even when only one call has the concrete location. The verifier and final membership pass still decide whether those calls are saved.
- Full solution test suite passed before deploy: `114/114`.
- Automation boundary updated to `2026-06-18T03:36:56Z`.

Residual risk:
- The `03:36:56Z` deployment is service-verified but not yet behaviorally proven by a fresh post-deploy same-emergency incident.
- No production incidents were manually repaired. The possible overdose may only gain `787109` and `787119` if a future update re-evaluates that incident.
- Next check should confirm whether broader emergency candidate review improves missed continuation calls without creating false joins.

### 2026-06-18 03:42Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T03:42Z`; rows before the `2026-06-18T03:36:56Z` candidate-review deployment are context unless needed for comparison.

Observed:
- Services healthy after deploy: `pizzad`, `trunk-recorder`, and `qdrant` active. `pizzad` active timestamp is `2026-06-17 23:42:01 EDT`, with `0` restarts.
- Queues healthy: queue depth `0`, pending transcriptions `0`, pending audio `0s`, no pressure.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`.
- Last-hour calls: `473` total, `7,400s` audio. Transcript quality: `433` complete OK, `26` repetitive, `11` empty, `2` too short, `1` numeric noise.
- AI summary mostly clean: `37` requests, `35` successes, `2` failures, `0` truncations.
- Evidence verifier counters stayed healthy: `19` runs, average reviewed calls `5.42`, average/max truncated calls `0/0`, added/dropped `6/2`, retention mismatches `0`.
- Incident endpoint: `3` creates, `15` updates, `4` rejects in the last hour.

Evidence reviewed:
- Post-`03:36:56Z` audit row `29438` updated incident `3829`, key `llm:whiteoakmt-hamilton:787125:event`, but still retained only call `787125`.
- This showed the first candidate-review broadening was incomplete. It relaxed the review filter, but it still only worked on calls that were already present in vector or RAG candidate lists. Calls `787109` and `787119` could still be absent from those candidate lists.

Action taken:
- Deployed deterministic nearby same-emergency candidate sourcing at `2026-06-18T03:42:01Z`.
- Code now adds nearby calls from the rolling recent-call window into evidence review when they are close in time, share the same dominant emergency evidence class, and have a strong incident signal. This is before verifier review; verifier acceptance and final membership still control what gets saved.
- Full solution test suite passed before deploy: `114/114`.
- Automation boundary updated to `2026-06-18T03:42:01Z`.

Residual risk:
- There were no post-`03:42:01Z` incident audit rows at the time of this check, so the candidate-source correction is service-verified but not yet behaviorally proven.
- Watch the next run for both sides of this change: same-emergency continuation calls should be reviewed, but unrelated emergency calls near in time must still be rejected by the verifier and final membership.

### 2026-06-18 03:49Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T03:52Z`; rows before the `2026-06-18T03:42:01Z` nearby-candidate-source deployment are context unless needed for comparison.

Observed:
- Services healthy after deploy: `pizzad`, `trunk-recorder`, and `qdrant` active. `pizzad` active timestamp is `2026-06-17 23:52:08 EDT`, with `0` restarts.
- Queues healthy: queue depth `0`, pending transcriptions `1`, pending audio `11s`, no pressure.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`.
- Last-hour calls: `453` total, `7,194s` audio. Transcript quality: `416` complete OK, `22` repetitive, `10` empty, `3` too short, `1` pending, `1` numeric noise.
- AI summary mostly clean: `37` requests, `36` successes, `1` failure, `0` truncations.
- Evidence verifier counters stayed healthy: `20` runs, average reviewed calls `5.5`, average/max truncated calls `0/2`, added/dropped `8/1`, retention mismatches `0`.
- Incident endpoint: `3` creates, `15` updates, `4` rejects in the last hour.

Evidence reviewed:
- Positive proof for the `03:42:01Z` nearby-candidate-source deploy: incident `3828`, key `llm:whiteoakmt-hamilton:787099:event`, added call `787127`, a same-fire follow-up that says units will check out the fire and meet on Infantry Street.
- Continuing issue: the same incident still excluded call `787113`. Manual transcript review shows `787113` is a police dispatch relaying the same 2239 Infantry or Inventory Road / Dodson Avenue / Taylor Street smoke or asthma report tied to the fire.
- Root cause in code: the house-number same-event link still required the same operational context. That allowed same-talkgroup or same-category continuations, but it was too strict for cross-agency emergency relays when police and fire were handling the same address and event.
- Row `29443` grouped four calls but was rejected because the proposed narrative lacked evidence-backed fallback. That is not a saved false join, but it remains useful as a watch case for over-broad candidate sourcing.

Action taken:
- Deployed cross-agency same-address emergency linkage at `2026-06-18T03:52:08Z`.
- Code now allows same-system, same-dominant-emergency, same significant house-number calls to link across talkgroups and categories when both calls have strong incident signals. Police-only and generic public-safety cases remain excluded from this relaxation.
- Full solution test suite passed before deploy: `114/114`.
- Automation boundary updated to `2026-06-18T03:52:08Z`.

Residual risk:
- The `03:52:08Z` deployment is service-verified but not yet behaviorally proven by a fresh post-deploy incident update.
- Next check should verify whether `787113` or similar cross-agency same-address calls attach without allowing unrelated same-number emergency calls to join.

### 2026-06-18 04:04Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T04:08Z`; rows before the `2026-06-18T03:52:08Z` cross-agency address-link deployment are context unless needed for comparison.

Observed:
- Services healthy after deploy: `pizzad`, `trunk-recorder`, and `qdrant` active. `pizzad` active timestamp is `2026-06-18 00:07:27 EDT`, with `0` restarts.
- Queues healthy: queue depth `0`, pending transcriptions `0`, pending audio `0s`, no pressure.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`.
- Last-hour calls: `430` total, `6,741s` audio. Transcript quality: `398` complete OK, `20` repetitive, `7` empty, `4` too short, `1` numeric noise.
- AI summary mostly clean: `36` requests, `35` successes, `1` failure, `0` truncations.
- Evidence verifier counters stayed healthy: `20` runs, average reviewed calls `5.9`, average/max truncated calls `0/3`, added/dropped `11/1`, retention mismatches `0`.
- Incident endpoint: `3` creates, `13` updates, `6` rejects in the last hour.

Evidence reviewed:
- The cross-agency same-address linkage still did not attach `787113` to fire incident `3828`. Post-boundary rows `29444` and `29446` retained `787099`, `787121`, and `787127`, but excluded `787113`.
- Manual transcript review found the missing reason: `787113` says the same address as spoken words, `twenty-two thirty-nine` and `two-two three-nine`, while the accepted fire call has numeric `2239 Infantry Road`. The significant-number linker only compared digit strings, so it could not prove the same address across the police and fire transcripts.
- Row `29450` created incident `3830`, `EMS response to Cookout for 31yo male attempt`, with calls `787405`, `787415`, `787445`, `787473`, `787689`, and `787715`. The retained calls share Cookout / 825 25th Street / EMS patient loaded / Cookout command terminated, so it is not a proven bad join. The wording remains awkward and should be watched for title cleanup.
- Row `29449` rejected an attempted overdose update that mixed existing overdose call `787125` with unrelated later overdose calls `787879` and `787883`, which is a positive signal for false-join control.

Action taken:
- Deployed spoken street-address number support at `2026-06-18T04:07:27Z`.
- Code now extracts significant address numbers from nearby spoken street-address phrases such as `twenty-two thirty-nine ... road` or `two two three nine ... road`, then uses those numbers in the same-address emergency linkage.
- Full solution test suite passed before deploy: `114/114`.
- Automation boundary updated to `2026-06-18T04:07:27Z`.

Residual risk:
- The `04:07:27Z` deployment is service-verified but not yet behaviorally proven by a fresh post-deploy incident update.
- Watch for false joins from spoken-number extraction, especially unrelated emergency calls that share a spoken number but not the same street, landmark, or event context.
- Watch incident `3830` for title quality. The membership looks defensible, but `31yo male attempt` is not a clean title.

### 2026-06-18 04:19Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T04:21Z`; rows before the `2026-06-18T04:07:27Z` spoken-address deployment are context unless needed for comparison.

Observed:
- Services healthy after deploy: `pizzad`, `trunk-recorder`, and `qdrant` active. `pizzad` active timestamp is `2026-06-18 00:21:06 EDT`, with `0` restarts.
- Queues healthy: queue depth `0`, pending transcriptions `0`, pending audio `0s`, no pressure.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`.
- Last-hour calls: `385` total, `5,993s` audio. Transcript quality: `357` complete OK, `15` repetitive, `7` empty, `5` too short, `1` numeric noise.
- AI summary mostly clean: `37` requests, `36` successes, `1` failure, `0` truncations.
- Evidence verifier counters stayed healthy: `19` runs, average reviewed calls `6.53`, average/max truncated calls `0/3`, added/dropped `11/2`, retention mismatches `0`.
- Incident endpoint: `2` creates, `10` updates, `11` rejects in the last hour.

Evidence reviewed:
- Post-`04:07:27Z` rows `29451` and `29452` still excluded call `787113` from fire incident `3828`, while retaining `787099`, `787121`, `787127`, and `787151`.
- Manual transcript review showed the first spoken-address parser still missed `787113` because it required the street suffix to appear immediately after number words. The transcript says `twenty-two, thirty-nine, inventory road` and `two-two, three-nine, the road`, so a street-name or filler token appears between the spoken number and suffix.
- Positive false-join control: rows `29453` through `29458` rejected weak, noisy, routine, or transport-only candidates rather than saving them.

Action taken:
- Deployed spoken-address parser correction at `2026-06-18T04:21:06Z`.
- Code now accepts a short street-name or filler gap between the spoken address number and the street suffix, so phrases like `twenty two thirty nine inventory road` can yield address number `2239`.
- Full solution test suite passed before deploy: `114/114`.
- Automation boundary updated to `2026-06-18T04:21:06Z`.

Residual risk:
- The `04:21:06Z` deployment is service-verified but not yet behaviorally proven by a fresh post-deploy incident update.
- Watch the next run for `787113` or similar spoken-number cross-agency address calls attaching, and for any false joins from loose spoken-number matching.

### 2026-06-18 04:34Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T04:38Z`; rows before the `2026-06-18T04:21:06Z` spoken-address-gap deployment are context unless needed for comparison.

Observed:
- Services healthy after deploy: `pizzad`, `trunk-recorder`, and `qdrant` active. `pizzad` active timestamp is `2026-06-18 00:37:45 EDT`, with `0` restarts.
- Queues healthy: queue depth `0`, pending transcriptions `0`, pending audio `0s`, no pressure.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`.
- Last-hour calls: `323` total, `4,942s` audio. Transcript quality: `302` complete OK, `13` repetitive, `4` empty, `4` too short.
- AI summary clean: `37` requests, `37` successes, `0` failures, `0` truncations.
- Evidence verifier counters stayed healthy: `19` runs, average reviewed calls `6.79`, average/max truncated calls `0/3`, added/dropped `10/1`, retention mismatches `0`.
- Incident endpoint: `3` creates, `10` updates, `12` rejects in the last hour.

Evidence reviewed:
- There were no fresh post-`04:21:06Z` rows for fire incident `3828`, so the spoken-address-gap fix has not yet been behaviorally proven.
- New incident `3831`, key `llm:whiteoakmt-hamilton:788075:event`, saved a multi-talkgroup welfare-check BOLO for Virginia Hernandez in a red Lexus with tag `4883 RH`. It retained calls `788075`, `788083`, `788085`, `788115`, and `788161`, but excluded `788077` and `788123`.
- Manual transcript review shows `788077` is likely another relay of the same welfare-check BOLO, but noisy: it includes a red vehicle, E35/Lexus-like wording, heading back toward Chattanooga, and a distorted tag. `788123` is weaker and may be too noisy to retain.
- Positive false-join control: row `29459` rejected standalone hospital handoff; rows `29461` and `29467` handled single-call candidates without broad grouping.

Action taken:
- Deployed police broadcast identity linkage at `2026-06-18T04:37:45Z`.
- Code now gives repeated police BOLO or welfare-check broadcasts a strong server link when they are close in time and share enough identity tokens, including person names, vehicle descriptors, plate digits, or phonetic plate letters. The change still requires police broadcast context and same system, and verifier plus final membership still control persistence.
- Full solution test suite passed before deploy: `114/114`.
- Automation boundary updated to `2026-06-18T04:37:45Z`.

Residual risk:
- The `04:37:45Z` deployment is service-verified but not yet behaviorally proven by a fresh post-deploy incident update.
- Watch incident `3831` for whether noisy but same-event BOLO relays attach, and for false joins between unrelated BOLOs that share generic vehicle or direction language.
- Watch incident `3830` for title quality. The membership still looks defensible, but `31yo male attempt` remains awkward.

### 2026-06-18 04:49Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T04:49Z`; rows before the `2026-06-18T04:37:45Z` police broadcast identity deployment are context unless needed for comparison.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active. `pizzad` active timestamp is `2026-06-18 00:37:45 EDT`, with `0` restarts.
- Queues healthy: queue depth `0`, pending transcriptions `0`, pending audio `0s`, no pressure.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`, pending `1` very recent call.
- Last-hour calls: `283` total, `4,517s` audio. Transcript quality: `266` complete OK, `13` repetitive, `3` too short, `1` pending.
- AI summary clean: `35` requests, `35` successes, `0` failures, `0` truncations.
- Evidence verifier counters stayed healthy: `18` runs, average reviewed calls `6.28`, average/max truncated calls `0.5/3`, added/dropped `9/2`, retention mismatches `0`.
- Incident endpoint: `3` creates, `7` updates, `13` rejects in the last hour.

Evidence reviewed:
- Post-`04:37:45Z` audit rows are sparse. No fresh welfare-check BOLO update has occurred yet, so the police broadcast identity linkage is not behaviorally proven.
- Post-boundary rows `29468` and `29469` updated incident `3830`, key `llm:whiteoakmt-cleveland:787405:event`, and retained the existing Cookout incident calls while excluding `787917`. Manual transcript review supports the exclusion: `787917` is a destination-to-Riley-health-care transport-style call and does not mention Cookout, the 31-year-old patient, or the earlier drowning-attempt response.
- Row `29470` rejected single-call commercial-alarm candidate `788267`. Transcript review supports the rejection as a weak single-call alarm candidate without strong incident evidence.
- Row `29471` rejected two calls at Waterlevel and London Drive. The transcripts may describe a road-obstruction or search-style public safety call, but the evidence is not clear enough to support a code change on this run. Keep it as a watch case only if similar road-hazard pairs repeat with clearer wording.

Action taken:
- No code change. The last deployment boundary remains `2026-06-18T04:37:45Z`.
- Automation prompt already points at the current boundary and now watches for same-event BOLO relays still not attaching and false joins caused by BOLO identity linkage.

Residual risk:
- The `04:37:45Z` deployment remains service-verified but not yet behaviorally proven by a fresh post-boundary welfare-check or BOLO incident update.
- Incident `3830` still has an awkward title, but the saved call membership remains defensible and the post-boundary update did not reintroduce the unrelated transport call.
- Watch for repeated Waterlevel/London-style road-hazard pairs being rejected only if future transcripts provide clearer hazard evidence.

### 2026-06-18 05:04Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T05:04Z`; rows before the `2026-06-18T04:37:45Z` police broadcast identity deployment are context unless needed for comparison.

Observed:
- Services healthy: `pizzad`, `trunk-recorder`, and `qdrant` active. `pizzad` active timestamp is `2026-06-18 00:37:45 EDT`, with `0` restarts.
- Queues healthy: queue depth `0`, pending transcriptions `0`, pending audio `0s`, no pressure.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`, pending `0`.
- Last-hour calls: `312` total, `4,834s` audio. Transcript quality: `292` complete OK, `17` repetitive, `2` too short, `1` numeric noise.
- AI summary clean: `40` requests, `40` successes, `0` failures, `0` truncations.
- Evidence verifier counters stayed healthy: `20` runs, average reviewed calls `5.55`, average/max truncated calls `0.2/3`, added/dropped `8/4`, retention mismatches `0`.
- Incident endpoint: `5` creates, `8` updates, `13` rejects in the last hour.

Evidence reviewed:
- Post-`04:37:45Z` BOLO behavior looks controlled. Incident `3831`, key `llm:whiteoakmt-hamilton:788075:event`, remained at the same five retained Virginia Hernandez / red Lexus calls after rows `29472`, `29473`, `29478`, and `29479`.
- The rejected BOLO-adjacent calls were not strong evidence to retain. Call `788149` says only a short-party or road-check phrase and has no shared person, vehicle, tag, or welfare-check evidence. Call `788123` has welfare-check and Ohio/right-state-of-mind wording, but no person name, no vehicle, no tag, and no clear destination, so the exclusion is acceptable.
- New incident `3833`, motorcycle fire at `178 Horton Rd SE`, has supported title/detail and defensible membership: `788345` reports the motorcycle fire and rider, `788387` gives likely Bates Pike location context, and `788433` says the fire is in a ditch next to the road.
- New incident `3834`, possible home invasion at Rockford Lane, has supported title/detail and defensible membership: `788347` reports a possible prowler at Rockford Lane, and `788377` updates the same call to home invasion with the caller still on the line.
- Rejected call `788341` was a vehicle/tag return without incident evidence. Rejection is appropriate.

Action taken:
- No code change. The last deployment boundary remains `2026-06-18T04:37:45Z`.
- No automation prompt update was needed.

Residual risk:
- The police broadcast identity change has an early positive signal because weak candidates stayed out of incident `3831`, but it still has not proven that it can attach a noisy same-event relay that was previously missed.
- Incident `3830` remains a title-quality watch item because `31yo male attempt` is awkward, but no new evidence supports a code change yet.

### 2026-06-18 05:19Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T05:19Z`; rows before the `2026-06-18T04:37:45Z` police broadcast identity deployment are context unless needed for comparison.

Observed:
- Services healthy before deploy: `pizzad`, `trunk-recorder`, and `qdrant` active. `pizzad` active timestamp was `2026-06-18 00:37:45 EDT`, with `0` restarts.
- Queues healthy: queue depth `0`, pending transcriptions `0`, pending audio `0s`, no pressure.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`, pending `0`.
- Last-hour calls: `322` total, `4,928s` audio. Transcript quality: `302` complete OK, `17` repetitive, `1` empty, `1` numeric noise, `1` too short.
- AI summary clean before deploy: `34` requests, `34` successes, `0` failures, `0` truncations.
- Evidence verifier counters stayed healthy: `17` runs, average reviewed calls `4.82`, average/max truncated calls `0/0`, added/dropped `1/5`, retention mismatches `0`.
- Incident endpoint: `4` creates, `9` updates, `8` rejects in the last hour.

Evidence reviewed:
- A concrete false join appeared after the `04:37:45Z` boundary. Incident `3834`, key `llm:whiteoakmt-hamilton:788347:event`, began as a supported Chattanooga Police possible prowler/home-invasion event at Rockford Lane with calls `788347` and `788377`.
- Row `29481` temporarily included same-address CPD detail call `788353`, which explicitly says `4505 Rockford Lane`, lights and gun lasers in the windows, and caller under the bed.
- Rows `29483` through `29485` then saved calls `788347`, `788377`, and `788445`, while excluding `788353` as verifier-rejected and `788349` as weak. Manual transcript review shows `788445` is a Rhea Sheriff call saying only that someone left in a Honda Civic with a gun. It has no Rockford Lane, no home invasion, no caller-on-line detail, and no shared person or vehicle with the CPD event.
- The root cause was an overly broad server-side shortcut: `SameOperationalContext` treated all same-category calls, such as police, as the same operational context. That allowed cross-talkgroup police semantic similarity to satisfy membership support without a shared concrete anchor or identity.
- Positive control still held for BOLO incident `3831`: weak candidates `788149` and `788123` stayed out; no BOLO false join was observed.

Action taken:
- Deployed operational-context tightening at `2026-06-18T05:21:54Z`.
- Code no longer treats broad category equality as the same operational context. Same-category calls from different talkgroups must now link through stronger evidence such as shared concrete location, same-address emergency linkage, BOLO or welfare-check identity, or another structured anchor.
- Full solution test suite passed before deploy: `114/114`.
- Post-deploy verification: health OK, `pizzad` active since `2026-06-18 01:21:54 EDT`, `0` restarts, queue depth `0`, pending audio `0s`, embedding endpoint OK, Qdrant OK, embedding queue `0`, failed embeddings `0`.
- The one AI failure visible immediately after deploy was `The operation was canceled` at `2026-06-18T05:21:53Z`, exactly at the service restart boundary; the next automatic-insights row at `05:22:04Z` succeeded with `finish_reason=stop`.
- Automation boundary updated to `2026-06-18T05:21:54Z`.

Residual risk:
- The `05:21:54Z` deployment is service-verified but not behaviorally proven by fresh post-deploy incident operations.
- Next run should verify that broad same-category cross-talkgroup semantic joins no longer save unrelated calls, while legitimate cross-talkgroup continuations still attach when they have shared address, identity, or another strong event anchor.
- Incident `3834` remains historically polluted until normal live updates repair it or a separate manual/data repair path is chosen; this bake loop should not mutate existing production incident rows directly.

### 2026-06-18 05:34Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T05:36Z`; rows before the `2026-06-18T05:21:54Z` operational-context deployment are context unless needed for comparison.

Observed:
- Services healthy before deploy: health OK, queue depth `0`, pending audio `0s`, embedding endpoint OK, Qdrant OK, embedding queue `0`, failed embeddings `0`.
- `pizzad` was active since `2026-06-18 01:21:54 EDT`, with `0` restarts before this run's deploy.
- Last-hour calls: `303` total, `4,662s` audio. Transcript quality: `286` complete OK, `13` repetitive, `2` empty, `1` numeric noise, `1` too short.
- AI usage: `35` requests, `34` successes, `1` failure, `0` truncations. The visible failure remains aligned with the prior service restart boundary.
- Evidence verifier counters stayed healthy: `16` runs, average reviewed calls `4.19`, average/max truncated calls `0/0`, added/dropped `4/4`, retention mismatches `0`.
- Incident endpoint: `2` creates, `8` updates, `11` rejects in the last hour.

Evidence reviewed:
- The Rockford Lane incident was still wrong after the `05:21:54Z` deployment. Post-boundary rows `29494` through `29496` saved incident `3834`, key `llm:whiteoakmt-hamilton:788347:event`, with calls `788347`, `788377`, and unrelated Rhea Sheriff call `788445`.
- Manual transcript review: `788347` is a Chattanooga Police possible prowler call at `4505 Rockford Lane`; `788377` updates the same event to home invasion with the caller on the line; `788445` says only that someone left in a Honda Civic with a gun and has no Rockford Lane, home invasion, caller, address, or shared identity evidence.
- The audit reason exposed the actual remaining fault: `final membership retained 3/3 assembler call(s): server-owned validated event membership; validator matched 2/3 call(s) but did not rewrite membership`. Because `788445` was already on the existing incident, assembly preserved it. Final validation proved only the two Chattanooga Police calls, but the persistence path kept the larger set.
- Same-address detail call `788353` remains a separate watch case. Its transcript supports the Rockford Lane event, but the verifier rejected it in the current rows. This run focused on removing the false member first.

Action taken:
- Deployed final-validator-subset membership correction at `2026-06-18T05:39:56Z`.
- Code now treats the server validator's matched subset as authoritative for unmatched calls. Calls outside that subset are saved only if server-side evidence links them back to the validated subset through concrete anchors, same-address emergency linkage, BOLO or welfare-check identity linkage, or same-talkgroup same-event evidence.
- Semantic or vector similarity alone no longer preserves validator-unmatched calls in this final save path.
- Full solution test suite passed before deploy: `114/114`.
- Post-deploy verification: health OK, `pizzad` active since `2026-06-18 01:39:56 EDT`, `0` restarts, queue depth `0`, pending audio reported as `8s` from one very recent transcription, embedding endpoint OK, Qdrant OK, embedding queue `0`, failed embeddings `0`.
- Automation boundary updated to `2026-06-18T05:39:56Z`.
- Immediate behavior proof: rows `29497` and `29498` updated incident `3834` with only calls `788347` and `788377`. Audit reason: `final membership retained 2/3 assembler call(s): server-owned validated event membership; validator subset pruned 1 unlinked call(s)`. This removed the Rhea Sheriff call `788445` through normal live processing, not manual data repair.

Residual risk:
- Next run should verify that validator-unmatched calls are no longer saved without concrete server linkage, and that legitimate continuation calls are not dropped when they do have same-talkgroup, same-address, identity, or other structured same-event evidence.
- Incident `3834` is now cleaned by live update for the false member `788445`, but the missed same-address detail call `788353` remains worth watching because the verifier rejected it.

### 2026-06-18 05:49Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T05:50Z`; rows before the `2026-06-18T05:39:56Z` final-validator-subset deployment are context unless needed for comparison.

Observed:
- Services healthy: health OK, queue depth `0`, pending transcriptions `0`, pending audio `0s`.
- `pizzad` active timestamp is `2026-06-18 01:39:56 EDT`, with `0` restarts.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, pending `0`, failed `0`.
- Last-hour calls: `307` total, `4,443s` audio. Transcript quality: `288` complete OK, `16` repetitive, `2` empty, `1` numeric noise.
- AI routing clean after the `05:39:56Z` deploy: post-boundary rows `29403` through `29410` all used request model `qwen/qwen3.6-35b-a3b@q8_0`, finish reason `stop`, and had no failures or truncation.
- Evidence verifier counters stayed healthy: `18` runs, average reviewed calls `4.00`, average/max truncated calls `0/0`, added/dropped `6/4`, retention mismatches `0`.
- Incident endpoint: `3` creates, `10` updates, `12` rejects in the last hour.

Evidence reviewed:
- Positive proof for the latest deploy: Rockford Lane incident `3834`, key `llm:whiteoakmt-hamilton:788347:event`, now has calls `788347`, `788353`, and `788377`. The unrelated Rhea Sheriff call `788445` is not retained.
- Rows `29499` and `29500` show the same incident accepting same-address detail call `788353` and excluding `788445` as weak or unrelated. This repairs both observed Rockford Lane problems through normal live processing.
- New incident `3835`, key `llm:whiteoakmt-hamilton:788885:event`, is defensible despite validator-subset notes. Calls `788873`, `788885`, and `788887` are all same talkgroup, close in time, and describe an unconscious or possibly unconscious person around Gunbarrel, the Lottery Office, Goodwin Road, and nearby landmarks. No false join found.
- Rejected calls `788393`, `788807`, and `788857` were manually checked. `788393` is garbled Cleveland Towers text and rejection is appropriate. `788807` is too thin to create. `788857` is a single-call welfare check for Norma Waters at `1246 Lewis Street NE` with confusion and school-bus wording; it may be a legitimate single-dispatch public-safety incident, but one borderline row is not enough evidence for a general code change.

Action taken:
- No code change.
- No automation prompt update needed. The current boundary remains `2026-06-18T05:39:56Z`.

Residual risk:
- Watch for repeated single-call welfare checks with concrete person, address, and safety or medical concern being rejected as weak. If this repeats, the general fix should be to define a grounded single-call welfare-check acceptance invariant, not to add a one-off phrase rule.
- Continue watching for legitimate continuation calls being dropped by final validator-subset pruning; no such drop was confirmed in this run.

### 2026-06-18 06:04Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T06:05Z`; rows before the `2026-06-18T05:39:56Z` final-validator-subset deployment are context unless needed for comparison.

Observed:
- Services healthy before deploy: health OK, queue depth `0`, pending transcriptions `1`, pending audio `12s`; this cleared after deploy.
- `pizzad` was active since `2026-06-18 01:39:56 EDT`, with `0` restarts before this run's deploy.
- Embeddings healthy before and after deploy: endpoint OK, Qdrant OK, queue `0`, failed `0`.
- Last-hour calls: `278` total, `4,084s` audio. Transcript quality: `261` complete OK, `13` repetitive, `2` empty, `1` pending, `1` too short.
- AI usage: `33` requests, `32` successes, `1` failure, `0` truncations. Post-`05:39:56Z` model rows used the expected Paxan Qwen request model and finished with `stop`; no local routing or length finish was observed.
- Evidence verifier counters stayed healthy: `16` runs, average reviewed calls `3.81`, average/max truncated calls `0/0`, added/dropped `7/3`, retention mismatches `0`.
- Incident endpoint: `1` create, `7` updates, `18` rejects in the last hour.

Evidence reviewed:
- The `05:39:56Z` final-validator-subset behavior still looks good. Rockford Lane incident `3834` remains concluded with the correct Chattanooga Police calls `788347`, `788353`, and `788377`; unrelated Rhea Sheriff call `788445` stayed out.
- New incident `3835`, unconscious person at the Lottery Office near Goodwin Road, remains defensible. Calls `788873`, `788885`, and `788887` are close in time, same talkgroup, and all describe an unconscious or possibly unconscious person near the same location.
- Rejected call `788857` repeated after the prior run. It is a concrete single-call welfare check for Norma Waters at `1246 Lewis Street Northeast` with confusion and possible safety or medical concern. This is no longer just a one-off watch item.
- Rejected call `789047` is a concrete single-call vehicle incident at `1928 Central Avenue` where a black Wrangler hit a pole. The validator rejected it because the crash vocabulary did not include hit-fixed-object wording.
- Rejected call `788955` is a burglar-alarm-style dispatch. I did not change alarm handling because prior monitoring has intentionally treated weak single-call alarms cautiously, and this row alone does not justify broadening that class.
- Rejected call `788965` includes a patient who left a hospital with an IV, but the transcript ends with disregard/cancel wording. Rejection remains acceptable.

Action taken:
- Deployed single-call incident taxonomy correction at `2026-06-18T06:08:04Z`.
- Code now treats grounded welfare-check or well-being-check wording as a strong single-call incident signal when the call also has a concrete anchor and is not noisy, routine, or transport-only.
- Code now treats vehicle hit pole, tree, guardrail, barrier, building, wall, fence, or ditch wording as a strong single-call incident signal under the same concrete-anchor requirement.
- The generic-title filter no longer treats every occurrence of `check` as routine. It now treats only routine checks or status checks as generic, so a title like `Welfare check at 1246 Lewis Street Northeast` can be validated.
- Added focused tests for concrete single-call welfare checks and vehicle-hit-pole calls.
- Full solution test suite passed before each deploy: `116/116`.
- Post-deploy verification: health OK, `pizzad` active since `2026-06-18 02:08:04 EDT`, `0` restarts, queue depth `0`, pending audio `0s`, embedding endpoint OK, Qdrant OK, embedding queue `0`, failed embeddings `0`.
- Automation boundary updated to `2026-06-18T06:08:04Z`.

Residual risk:
- The `06:08:04Z` deployment is service-verified but not yet behaviorally proven; no post-boundary incident rows had arrived immediately after deploy.
- Next run should verify that concrete single-call welfare checks and vehicle-hit-fixed-object reports create when appropriate, and that the generic-title change does not allow routine or status checks to become incidents.
- Continue watching for legitimate continuation calls being dropped by final validator-subset pruning; no such drop was confirmed in this run.

### 2026-06-18 06:19Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T06:20Z`; rows before the `2026-06-18T06:08:04Z` single-call taxonomy deployment are context unless needed for comparison.

Observed:
- Services healthy before deploy: health OK, queue depth `0`, pending transcriptions `0`, pending audio `0s`.
- `pizzad` was active since `2026-06-18 02:08:04 EDT`, with `0` restarts before this run's deploy.
- Embeddings healthy before and after deploy: endpoint OK, Qdrant OK, queue `0`, failed `0`.
- Last-hour calls before deploy: `285` total, `4,144s` audio. Transcript quality: `267` complete OK, `16` repetitive, `1` empty, `1` too short.
- AI usage before deploy: `34` requests, `33` successes, `1` failure, `0` truncations. Post-boundary model rows used the expected Paxan Qwen request model, finished with `stop`, and had no local routing or length finish.
- Evidence verifier counters stayed healthy: `15` runs, average reviewed calls `3.60`, average/max truncated calls `0/0`, added/dropped `7/2`, retention mismatches `0`.
- Incident endpoint before deploy: `3` creates, `5` updates, `18` rejects in the last hour.

Evidence reviewed:
- The `06:08:04Z` single-call taxonomy deployment was service-verified but still had no direct behavior proof in this window. No post-boundary welfare-check or vehicle-hit-fixed-object create had arrived yet.
- New incident `3837`, key `llm:whiteoakmt-hamilton:789113:event`, created correctly for shots fired at `4547 Trisha Drive` near Mary Hall Lane with calls `789113`, `789133`, and `789145`.
- A later update exposed a continuation-link gap. Audit row `29519` retained only `3/6` calls and excluded `789131`, `789151`, and `789233`, even though manual transcript review supports them as the same event.
- `789131` is a Chattanooga Police burglary-in-progress continuation in the same area. `789151` is a Hamilton Sheriff relay for black males with masks trying to get inside and the reporting party shooting at the back door. `789233` references the same burglary-in-progress at Trisha and a high-speed response.
- The failure was not a model routing, queue, transcription, embedding, or verifier truncation problem. The server link allowed exact address and strongest-anchor evidence, but close same-system continuation calls that did not repeat the exact address could still be excluded.

Action taken:
- Deployed controlled same-event continuation linkage at `2026-06-18T06:23:43Z`.
- Code now lets close same-system calls link when they share distinctive continuation tokens or explicit same-event wording, while still requiring a strong incident signal and final server-owned membership before saving.
- The validator now treats shots-fired and shot-at wording as strong incident evidence.
- Full solution test suite passed before deploy: `116/116`.
- Post-deploy verification: health OK, `pizzad` active since `2026-06-18 02:23:43 EDT`, `0` restarts, queue depth `0`, pending audio `0s`, embedding endpoint OK, Qdrant OK, embedding queue `0`, failed embeddings `0`.
- Automation boundary updated to `2026-06-18T06:23:43Z`.
- Immediate behavior proof: row `29522` accepted an update for incident `3837` with calls `789113`, `789131`, `789133`, `789145`, `789151`, and `789233`. Row `29523` then rejected a duplicate new proposal containing those calls plus weak call `789281`, so the duplicate path did not create a sibling incident.

Residual risk:
- The continuation-link change has one positive proof and still needs more volume. Watch for false joins caused by shared continuation tokens without a concrete same-event anchor.
- The `06:08:04Z` single-call taxonomy deployment is still not behaviorally proven. Continue watching concrete welfare checks and vehicle-hit-fixed-object reports.
- Continue watching post-update titles and details for the Trisha Drive incident; the saved title is still supported by retained evidence.

### 2026-06-18 06:34Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T06:35Z`; rows before the `2026-06-18T06:23:43Z` continuation-link deployment are context unless needed for comparison.

Observed:
- Services healthy before and after deploy: health OK, queue depth `0`, pending transcriptions `0`, pending audio `0s`.
- `pizzad` was active since `2026-06-18 02:23:43 EDT` before this run's deploy, with `0` restarts.
- Embeddings healthy before and after deploy: endpoint OK, Qdrant OK, queue `0`, failed `0`.
- Last-hour calls before deploy: `302` total, `4,466s` audio.
- Throughput before deploy: ingest about `5.7 calls/min`, transcribe about `6.0 calls/min`, average transcription realtime factor about `0.097`.
- AI usage before deploy: `38` requests, `38` successes, `0` failures, `0` truncations. No local routing or length finish was observed in this run.
- Evidence verifier counters stayed acceptable but are worth watching: `19` runs, average reviewed calls `4.84`, average/max truncated calls `0.32/5`, added/dropped `6/3`, retention mismatches `0`.
- Incident endpoint before deploy: `4` creates, `9` updates, `14` rejects in the last hour.

Evidence reviewed:
- The `06:23:43Z` continuation-link deployment had stronger positive proof. Audit rows `29526` and `29527` retained Trisha Drive incident `3837` with calls `789113`, `789131`, `789133`, `789145`, `789151`, `789233`, `789349`, `789357`, `789371`, and `789431`.
- Manual transcript review supports the added calls. `789349`, `789357`, and `789371` are broadcasts for four black males armed with an AR and pistol after the Charlie Channel burglary-in-progress call. `789431` says the shots came from the resident at `4547`, who shot at the door as people tried to break into the home.
- A separate sibling-merge fault appeared. Audit row `29524` created a single-call sibling incident for call `789139`; row `29525` merged that sibling into the Trisha Drive shots-fired incident, temporarily saving call `789139` with unrelated shots-fired calls.
- Manual transcript review shows `789139` is a fire/EMS lift-assist call for a `76 year old female` at `803 Smallwood Street`. It has no Trisha Drive, no burglary, no shots-fired, no BOLO identity, and no shared concrete event evidence.
- Normal processing later repaired the bad merge. Rows `29526` and `29527` excluded `789139` from incident `3837`. That proves the final membership path is stricter than the sibling-merge repair path, but it also proves the repair path can still create a transient bad save.
- Fire alarm incident `3836` grew to calls `789087`, `789099`, `789241`, `789259`, `789301`, `789317`, and `789409`. Manual review of the added calls supports the same fire-alarm event at Morgan Road with gate access, interior check, and termination updates.

Action taken:
- Deployed sibling-merge tightening at `2026-06-18T06:38:11Z`.
- Code now requires every call in a sibling incident to attach through strong non-semantic server evidence before the sibling can be merged.
- Semantic or vector similarity alone can no longer trigger server sibling repair.
- A sibling incident with any unlinked call is left unmerged instead of being unioned into the target incident.
- Full solution test suite passed before deploy: `116/116`.
- Post-deploy verification: health OK, `pizzad` active since `2026-06-18 02:38:11 EDT`, `0` restarts, queue depth `0`, pending audio `0s`, embedding endpoint OK, Qdrant OK, embedding queue `0`, failed embeddings `0`.
- Automation boundary updated to `2026-06-18T06:38:11Z`.

Residual risk:
- The `06:38:11Z` deployment is service-verified but not yet behaviorally proven; no post-boundary incident rows had arrived immediately after deploy.
- Next run should verify that sibling repair does not reintroduce unrelated calls, especially isolated single-call incidents near a larger active incident.
- Continue watching for the opposite failure: a true duplicate sibling incident left unmerged even though every call has strong non-semantic same-event evidence.
- Continue watching verifier truncation because this run showed a small nonzero maximum of `5` truncated review calls, although no retention mismatch or observed bad decision was tied to that counter.

### 2026-06-18 06:49Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T06:50Z`; rows before the `2026-06-18T06:38:11Z` sibling-merge-tightening deployment are context unless needed for comparison.

Observed:
- Services healthy: health OK, queue depth `0`, pending transcriptions `0`, pending audio `0s`.
- `pizzad` active since `2026-06-18 02:38:11 EDT`, with `0` restarts.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, failed `0`.
- Last-hour calls: `286` total, `4,255s` audio. Direct quality counts: `266` OK, `16` repetitive, `2` too short.
- Throughput healthy at a quieter capture rate: ingest about `2.0 calls/min`, transcribe about `2.1 calls/min`, average transcription realtime factor about `0.088`.
- AI routing clean: `31` requests, `31` successes, `0` failures, `0` truncations. Recent automatic-insights rows used request model `qwen/qwen3.6-35b-a3b@q8_0`, response model `qwen3.6-35b-a3b`, and finish reason `stop`.
- Evidence verifier counters: `15` runs, average reviewed calls `5.67`, average/max truncated calls `0.87/7`, added/dropped `8/2`, retention mismatches `0`.
- Incident endpoint: `3` creates, `6` updates, `11` rejects in the last hour.

Evidence reviewed:
- Post-boundary audit had no server sibling-merge repair rows.
- Row `29529` accepted an update for Trisha Drive incident `3837` with calls `789113`, `789131`, `789133`, `789145`, `789151`, `789233`, `789349`, `789357`, `789371`, and `789431`. This is the supported shots-fired, burglary-in-progress, and BOLO continuation set reviewed in the prior run.
- Direct database check confirms incident `3837` does not include unrelated lift-assist call `789139`.
- Row `29530` rejected a duplicate new fire-alarm proposal because all retained calls were already owned by the Morgan Road fire alarm incident. This is expected behavior and did not create a sibling.
- Direct database check confirms incident `3836` has only same-event Morgan Road fire-alarm calls: `789087`, `789099`, `789241`, `789259`, `789301`, `789317`, and `789409`.
- No generic fallback titles, existing-title rewrites, unknown incident identifier rejections, local-model routing, parse failures, queue pressure, embedding failures, or Qdrant failures were observed.

Action taken:
- No code change.
- No deployment.
- No automation prompt update needed. The current deployment boundary remains `2026-06-18T06:38:11Z`.

Residual risk:
- The sibling-merge tightening has a clean first post-boundary window, but no positive sibling-merge row has appeared yet. Continue watching for both false sibling merges and true duplicate siblings left unmerged.
- Verifier truncation is still nonzero, with max `7` in this window. No observed bad decision is tied to it yet, but it should remain on the watch list.
- The `06:08:04Z` single-call taxonomy deployment still needs behavior proof for concrete welfare-check and vehicle-hit-fixed-object creates.

### 2026-06-18 07:04Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T07:05Z`; rows before the `2026-06-18T06:38:11Z` sibling-merge-tightening deployment are context unless needed for comparison.

Observed:
- Services healthy before and after deploy: health OK, queue depth `0`, pending transcriptions `0`, pending audio `0s`.
- `pizzad` was active since `2026-06-18 02:38:11 EDT` before this run's deploy, with `0` restarts.
- Embeddings healthy before and after deploy: endpoint OK, Qdrant OK, queue `0`, pending `0`, failed `0`.
- Last-hour calls before deploy: `271` total, `3,993s` audio. Direct quality counts: `256` OK, `12` repetitive, `2` too short, `1` numeric noise.
- Throughput before deploy: ingest about `4.5 calls/min`, transcribe about `4.6 calls/min`, average transcription realtime factor about `0.131`.
- AI routing before deploy: `33` requests, `33` successes, `0` failures, `0` truncations. Recent rows used request model `qwen/qwen3.6-35b-a3b@q8_0`, response model `qwen3.6-35b-a3b`, and finish reason `stop`.
- Evidence verifier counters before deploy: `14` runs, average reviewed calls `6.93`, average/max truncated calls `2.07/8`, added/dropped `7/1`, retention mismatches `0`.
- Incident endpoint before deploy: `4` creates, `8` updates, `6` rejects in the last hour.

Evidence reviewed:
- A second false sibling merge occurred after the `06:38:11Z` deployment. Row `29535` created single-call incident `3839` for call `789515`; row `29536` merged it into Trisha Drive incident `3837`.
- Manual transcript review shows `789515` is a possible overdose dispatch at Hixson or Red Bank address text: `Station 1`, `47 Hanan-McClell road`, `4017 McHale road`, `possible overdose`. It has no shots fired, burglary, masks, vehicle BOLO, Trisha Drive, Mary Hall Lane, or shared event evidence with incident `3837`.
- Direct database check confirmed incident `3837` included `789515` before this run's deploy.
- The first sibling-merge tightening blocked semantic-only repair but did not block the newer continuation-token link. The bad link was enabled by two general weaknesses: dominant event class used broad talkgroup labels such as fire dispatch instead of transcript event evidence, and explicit continuation links could pass without compatible event class.
- Rows `29531`, `29533`, and `29529` still show the core Trisha Drive continuation set itself is supported: `789113`, `789131`, `789133`, `789145`, `789151`, `789233`, `789349`, `789357`, `789371`, and `789431`.
- Rejections `789477`, `789563`, and `789565` were reviewed. `789477` is a thin storage-building entry report, `789563` is an emergency backup request for a traffic stop, and `789565` is noisy medical or transport wording. None justified a code change in this run.

Action taken:
- Deployed event-class and continuation-token tightening at `2026-06-18T07:07:04Z`.
- Dominant event class now comes from transcript event evidence instead of broad category or talkgroup labels.
- Police event class now includes shots-fired, gunshot, shot-at, armed, pistol, rifle, and AR wording.
- Explicit same-event continuation links now require compatible event class.
- Generic location and agency tokens such as road, street, station, fire, EMS, medical, bank, and county no longer count as distinctive continuation tokens.
- Full solution test suite passed before deploy: `116/116`.
- Post-deploy verification: health OK, `pizzad` active since `2026-06-18 03:07:04 EDT`, `0` restarts, queue depth `0`, pending audio `0s`, embedding endpoint OK, Qdrant OK, embedding queue `0`, failed embeddings `0`.
- The one visible AI failure was `The operation was canceled` at `2026-06-18T07:07:04Z`, exactly at the service restart boundary. The preceding automatic-insights rows succeeded with finish reason `stop`.
- Automation boundary updated to `2026-06-18T07:07:04Z`.

Residual risk:
- The `07:07:04Z` deployment is service-verified but not yet behaviorally proven; no post-boundary incident rows had arrived immediately after deploy.
- Incident `3837` still contained `789515` immediately before deploy. This bake loop should not manually repair production data; next run should verify whether normal live processing removes it.
- Continue watching for true continuation calls that now fail because event-class evidence is too strict, especially cross-talkgroup police relays.
- Continue watching verifier truncation because this run reached max `8` truncated review calls without a confirmed bad outcome.

### 2026-06-18 07:19Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T07:20Z`; rows before the `2026-06-18T07:07:04Z` event-class-tightening deployment are context unless needed for comparison.

Observed:
- Services healthy before and after deploy: health OK, queue depth `0`, pending transcriptions `0`, pending audio `0s`.
- `pizzad` was active since `2026-06-18 03:07:04 EDT` before this run's deploy, with `0` restarts.
- Embeddings healthy before and after deploy: endpoint OK, Qdrant OK, queue `0`, pending `0`, failed `0`.
- Last-hour calls before deploy: `240` total, `3,609s` audio.
- Throughput before deploy: ingest about `3.4 calls/min`, transcribe about `3.6 calls/min`, average transcription realtime factor about `0.116`.
- AI routing before deploy: `33` requests, `32` successes, `1` failure, `0` truncations. The failure was the expected service-restart cancellation from the `07:07:04Z` deployment; later automatic-insights rows succeeded with finish reason `stop`.
- Evidence verifier counters before deploy: `15` runs, average reviewed calls `7.53`, average/max truncated calls `2.80/8`, added/dropped `7/1`, retention mismatches `0`.
- Incident endpoint before deploy: `2` creates, `10` updates, `7` rejects in the last hour.

Evidence reviewed:
- Positive proof from the `07:07:04Z` deployment: normal live processing removed unrelated possible-overdose call `789515` from Trisha Drive incident `3837`.
- Direct database check showed incident `3837` now had calls `789113`, `789131`, `789133`, `789145`, `789151`, `789233`, `789349`, `789357`, and `789431`. It no longer had `789515`.
- A new over-tightening appeared. Rows `29543` and `29544` dropped call `789371` from incident `3837` as weak or unrelated.
- Manual transcript review supports `789371` as a same-event BOLO relay despite noisy speech-to-text. It says a BOLO from Charlie Channel, `Dricia Drive`, four black males, armed with an AR and pistol, high rate of speed, and vehicle timing. It matches supported relay calls `789349` and `789357`.
- The root cause was transcript-grounded event class overreacting to a noisy phrase: `The Fire Dricia Drive`. The bare word `fire` caused the relay to classify as `fire_or_hazard`, even though the same transcript contains stronger police-event evidence.
- Rejected calls `789565` and `789619` did not justify additional changes. `789565` remained noisy medical wording. `789619` contained assault/fight language but the model narrative lacked a specific evidence-backed fallback; keep watching for repeated concrete single-call assault rejects before broadening.

Action taken:
- Deployed bare-fire event-class refinement at `2026-06-18T07:21:28Z`.
- Specific fire evidence such as fire alarm, smoke, flames, gas leak, explosion, and typed fire phrases still classify as fire evidence.
- A bare occurrence of `fire` no longer overrides concrete police, medical, traffic, or road-hazard evidence.
- Full solution test suite passed before deploy: `116/116`.
- Post-deploy verification: health OK, `pizzad` active since `2026-06-18 03:21:28 EDT`, `0` restarts, queue depth `0`, pending audio `0s`, embedding endpoint OK, Qdrant OK, embedding queue `0`, failed embeddings `0`.
- The one visible AI failure after deploy was `The operation was canceled` at `2026-06-18T07:21:28Z`, exactly at the service restart boundary. Rows immediately before it succeeded with finish reason `stop`.
- Automation boundary updated to `2026-06-18T07:21:28Z`.

Residual risk:
- The `07:21:28Z` deployment is service-verified but not yet behaviorally proven; no post-boundary incident rows had arrived immediately after deploy.
- Next run should verify that `789371` or an equivalent noisy BOLO relay can attach without re-admitting unrelated medical or fire calls.
- Continue watching verifier truncation because average/max truncation remains elevated at `2.80/8`, although no bad outcome was tied to truncation in this run.

### 2026-06-18 07:34Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T07:35Z`; rows before the `2026-06-18T07:21:28Z` bare-fire-refinement deployment are context unless needed for comparison.

Observed:
- Services healthy: health OK, queue depth `1`, pending transcriptions `2`, pending audio `16s`; no queue pressure.
- `pizzad` active since `2026-06-18 03:21:28 EDT`, with `0` restarts.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, pending `1`, failed `0`.
- Last-hour calls: `198` total, `3,112s` audio. Direct quality counts: `182` OK, `11` repetitive, `3` too short, `1` numeric noise, `1` empty.
- Throughput healthy at lower traffic: ingest about `3.7 calls/min`, transcribe about `3.6 calls/min`, average transcription realtime factor about `0.096`.
- AI routing after the restart was clean: latest successful rows used request model `qwen/qwen3.6-35b-a3b@q8_0`, response model `qwen3.6-35b-a3b`, and finish reason `stop`. The only recent failure was the expected service-restart cancellation at `07:21:28Z`.
- Evidence verifier counters remain elevated but without a tied failure: `10` runs, average reviewed calls `8.00`, average/max truncated calls `4.20/8`, added/dropped `6/1`, retention mismatches `0`.
- Incident endpoint: `2` creates, `6` updates, `7` rejects in the last hour.

Evidence reviewed:
- Post-boundary row `29547` rejected call `789515` as medical-transport context lacking parent emergency evidence. This confirms the unrelated possible-overdose call did not reattach to Trisha Drive after the `07:21:28Z` deployment.
- Direct database check showed Trisha Drive incident `3837` is concluded with calls `789113`, `789131`, `789133`, `789145`, `789151`, `789233`, `789349`, `789357`, and `789431`. It no longer has unrelated overdose call `789515`.
- Call `789371` is still absent. There was no post-boundary Trisha Drive update that could prove whether the bare-fire refinement restores that noisy BOLO relay. Keep this as unproven rather than failed.
- Row `29548` created incident `3840`, `Missing Person Search: Cindell Reed, Hwy 193 Area`, with calls `789793`, `789829`, `789835`, `789837`, `789839`, and `789845`.
- Manual transcript review supports incident `3840`: the calls are police broadcasts for Cindell or Cindell Reed, date of birth around `3/17/86`, last seen near Mapco or Highway `193/192`, white Jeep Compass, missing-person entry, phone off, and agency lookout or stop-hold instructions. No false join or unsupported title fact was found.

Action taken:
- No code change.
- No deployment.
- No automation prompt update needed. The current deployment boundary remains `2026-06-18T07:21:28Z`.

Residual risk:
- The `07:21:28Z` deployment has a clean early signal because unrelated `789515` stayed out, but it still has not proved whether noisy same-event BOLO relay `789371` can reattach.
- Continue watching verifier truncation. The average/max values are now `4.20/8`; no bad row was tied to truncation in this run, but it is the metric most likely to explain missed related calls if it rises further.
- The `06:08:04Z` single-call taxonomy change still lacks a clear post-deploy welfare-check or vehicle-hit-fixed-object proof case.

### 2026-06-18 07:49Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T07:56Z`; rows before the final `2026-06-18T07:55:16Z` missing-person broadcast identity deployment are context unless needed for comparison.

Observed:
- Services healthy before and after deploy: health OK, queue depth `0`, pending transcriptions `0`, pending audio `0s`.
- `pizzad` is active since `2026-06-18 03:55:16 EDT`, with `0` restarts.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, pending `0`, failed `0`.
- Last-hour calls after deploy: `176` total, `2,833s` audio. Direct quality counts: `161` OK, `9` repetitive, `4` too short, `2` empty.
- Throughput after deploy: ingest about `1.8 calls/min`, transcribe about `2.0 calls/min`; the immediate post-restart realtime-factor sample was `0`.
- AI routing stayed on Paxan Qwen: `30` requests, `28` successes, `2` failures, `0` truncations. The latest rows before the final deploy used request model `qwen/qwen3.6-35b-a3b@q8_0`, response model `qwen3.6-a3b`, and finish reason `stop`; the visible failures align with service restarts.
- Evidence verifier counters: `13` runs, average reviewed calls `7.00`, average/max truncated calls `2.08/8`, added/dropped `3/1`, retention mismatches `0`.
- Incident endpoint: `3` creates, `7` updates, `7` rejects in the last hour.

Evidence reviewed:
- Positive proof from the `07:21:28Z` deployment held: post-boundary row `29547` rejected unrelated possible-overdose call `789515`, and Trisha Drive incident `3837` remained concluded without it.
- Incident `3840`, `Missing Person Search: Cindell Reed, Hwy 193 Area`, is a valid missing-person broadcast incident, but rows `29550` and `29554` excluded legitimate same-event relay calls `789865` and `789831`.
- Manual transcript review supports both dropped calls. `789865` relays a Walker County missing-person BOLO for Cindell Reed, white Jeep Compass, date of birth `3/17/86`, Mapco or Highway `193`, phone off, and NCIC entry. `789831` carries the same missing-person broadcast with the same person, physical description, white Jeep Compass, phone off, and entered-as-missing wording.
- The root cause was that the police broadcast identity link required cleaner BOLO or missing-person phrasing and a high shared-token ratio. Noisy transcript wording such as `entered as missing`, `she's missing`, and `single-free female` failed the broadcast-context and police-event-class gates even though the retained calls shared strong identity tokens.
- Exclusion of weak call `789777` still looks correct because it is mainly a closing or status reference without enough identity detail. Rejections `789857` and `789563` remain watch items only, not evidence for a code change.

Action taken:
- Deployed missing-person broadcast identity broadening at `2026-06-18T07:55:16Z`.
- Broadcast identity context and police event class now recognize grounded missing-person wording such as `missing since`, `entered as missing`, `reported missing`, `she's missing`, and `missing female` without treating the bare word `missing` alone as enough.
- Police BOLO or missing-person relays can now link when they share at least three identity tokens with at least two strong tokens, such as dates, numbers, vehicle identifiers, plate fragments, or longer person and vehicle words, even when transcript noise lowers the shared-token ratio.
- Full solution test suite passed before the final deploy: `116/116`.
- Post-deploy verification: health OK, `pizzad` active since `2026-06-18 03:55:16 EDT`, `0` restarts, queue depth `0`, pending audio `0s`, embedding endpoint OK, Qdrant OK, embedding queue `0`, failed embeddings `0`.
- Automation boundary updated to `2026-06-18T07:55:16Z`.

Residual risk:
- The `07:55:16Z` deployment is service-verified but not behaviorally proven yet. Next runs should verify that `789831`, `789865`, or equivalent noisy missing-person relays attach through normal live processing.
- Watch for false BOLO or missing-person joins if common identity words such as color, vehicle type, or agency area overlap without enough distinctive tokens.
- Verifier truncation still hit max `8`; no bad row is tied to truncation in this run, but it remains the most important quality counter to watch.

### 2026-06-18 08:04Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T08:08Z`; rows before the `2026-06-18T07:55:16Z` missing-person identity deployment are context unless needed for comparison.

Observed:
- Services healthy before and after deploy: health OK, queue depth `0`, pending transcriptions `0`, pending audio `0s`.
- `pizzad` is active since `2026-06-18 04:07:37 EDT`, with `0` restarts.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, pending `0`, failed `0`.
- Last-hour calls after deploy: `174` total, `2,808s` audio. Direct quality counts: `162` OK, `8` repetitive, `2` empty, `2` too short.
- Throughput after deploy: ingest about `4.1 calls/min`, transcribe about `4.2 calls/min`, average transcription realtime factor about `0.123`.
- AI routing clean: latest rows used request model `qwen/qwen3.6-35b-a3b@q8_0`, response model `qwen3.6-35b-a3b`, and finish reason `stop`. Two rows reported `recovered_after_truncation_split`, but both succeeded and no finish reason was `length`.
- Evidence verifier counters: `13` runs, average reviewed calls `6.77`, average/max truncated calls `1.46/7`, added/dropped `3/1`, retention mismatches `0`.
- Incident endpoint: `2` creates, `7` updates, `7` rejects in the last hour.

Evidence reviewed:
- Positive proof for the `07:55:16Z` missing-person identity deployment: row `29557` updated incident `3840` to retain `789865` and exclude only weak call `789777`; row `29558` saved the update. Incident `3840` now has calls `789793`, `789829`, `789835`, `789837`, `789839`, `789845`, and `789865`.
- Call `789831` remains absent from incident `3840`, but there was no post-boundary audit row showing it was reviewed and rejected. Treat it as unproven candidate sourcing, not a failed membership decision yet.
- New issue: rows `29560` and `29561` rejected two concrete single-call violent police incidents as weak. Call `789961` is a domestic-unknown dispatch with a concrete area and apartment description. Call `789975` reports a disorder with weapon involved, a street address, a gun pulled on the caller, choking, and threats.
- The root cause was in the server fallback membership path: it rejected any transcript containing routine status words before checking for stronger event evidence. Call `789975` contained later clear/unit-status words, so the routine check overrode weapon and choking evidence. Call `789961` also exposed that concrete domestic dispatch wording was not part of the validator's strong single-call event taxonomy.

Action taken:
- Deployed concrete single-call domestic and weapon-event fallback correction at `2026-06-18T08:07:37Z`.
- Routine status wording can still reject weak calls, but it no longer suppresses stronger incident evidence such as weapon, gun, choking, threats, assault, or domestic dispatch language.
- Concrete domestic, domestic unknown, domestic disturbance, domestic dispute, and domestic violence wording now count as strong event signals when the call also has a concrete anchor or location hint and is not transport-only.
- Full solution test suite passed before deploy: `116/116`.
- Post-deploy verification: health OK, `pizzad` active since `2026-06-18 04:07:37 EDT`, `0` restarts, queue depth `0`, pending audio `0s`, embedding endpoint OK, Qdrant OK, embedding queue `0`, failed embeddings `0`.
- Automation boundary updated to `2026-06-18T08:07:37Z`.

Residual risk:
- The `08:07:37Z` deployment is service-verified but not behaviorally proven yet. Next runs should verify that `789961`, `789975`, or equivalent concrete domestic or weapon calls create incidents through normal live processing.
- Watch for false single-call incidents from generic domestic or weapon words without a concrete anchor. The existing concrete-anchor requirement should limit that risk.
- Call `789831` remains an unproven missing-person relay candidate because it has not reappeared in post-boundary audit rows.

### 2026-06-18 08:19Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T08:19Z`; rows before the `2026-06-18T08:07:37Z` single-call domestic and weapon deployment are context unless needed for comparison.

Observed:
- Services healthy: health OK, queue depth `0`, pending transcriptions `0`, pending audio `0s`.
- `pizzad` is active since `2026-06-18 04:07:37 EDT`, with `0` restarts.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, pending `0`, failed `0`.
- Last-hour calls: `167` total, about `2,594s` audio. Direct quality counts: `157` OK, `7` repetitive, `2` too short, `1` empty.
- Throughput healthy: ingest about `2.9 calls/min`, transcribe about `3.1 calls/min`, average transcription realtime factor about `0.147`.
- AI routing clean after the deploy: recent rows used request model `qwen/qwen3.6-35b-a3b@q8_0`, response model `qwen3.6-35b-a3b`, and finish reason `stop`; no `length` finish reason or local-model routing was observed.
- Evidence verifier counters improved in this window: `13` runs, average reviewed calls `6.54`, average/max truncated calls `0.46/6`, added/dropped `2/1`, retention mismatches `0`.
- Incident endpoint: `2` creates, `7` updates, `7` rejects in the last hour.

Evidence reviewed:
- Positive proof for the `07:55:16Z` missing-person identity deployment held. Rows `29562` and `29563` retained `789865` on missing-person incident `3840` and excluded only weak closing/status call `789777`.
- Active BOLO incident `3841` updated cleanly with calls `789825` and `789843`; no false BOLO identity join was observed.
- The `08:07:37Z` domestic and weapon deployment has no behavioral proof yet for `789961` or `789975`; neither call reappeared in post-boundary audit rows.
- Post-boundary row `29565` rejected call `789983` as lacking a strong event signal. Manual transcript review supports the rejection: it contains a possible address, a white four-door SUV, and building or parking description, but no clear domestic, weapon, assault, medical, fire, crash, or other concrete incident event.
- Pre-boundary operational-chatter row `29559` remains acceptable: calls `789879` and `789881` are registration/parking/vehicle-location chatter without a strong incident event.
- No generic fallback titles, title rewrites without retained evidence, unknown incident identifiers, sibling merges, retention mismatches, queue pressure, embedding failures, Qdrant failures, or model-routing regressions were observed.

Action taken:
- No code change.
- No deployment.
- No automation prompt update needed. The current deployment boundary remains `2026-06-18T08:07:37Z`.

Residual risk:
- The domestic and weapon single-call fix still needs positive live proof from a post-boundary row.
- Call `789831` remains an unproven missing-person relay candidate because it has not reappeared in audit rows.
- Continue watching for false single-call incidents from generic domestic or weapon wording, but this run did not show that failure.

### 2026-06-18 08:34Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T08:38Z`; rows before the `2026-06-18T08:07:37Z` single-call domestic and weapon deployment are context unless needed for comparison.

Observed:
- Services healthy before and after deploy: health OK, queue depth `0`, pending transcriptions `0`, pending audio `0s`.
- `pizzad` is active since `2026-06-18 04:37:11 EDT`, with `0` restarts.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, pending `0`, failed `0`.
- Last-hour calls after deploy: `173` total, `2,545s` audio. Direct quality counts: `156` OK, `12` repetitive, `3` too short, `2` empty.
- Throughput healthy: ingest about `2.7 calls/min`, transcribe about `2.8 calls/min`, average transcription realtime factor about `0.093`.
- AI routing clean: `33` requests, `33` successes, `0` failures, `0` truncations. Recent rows used request model `qwen/qwen3.6-35b-a3b@q8_0`, response model `qwen3.6-35b-a3b`, and finish reason `stop`.
- Evidence verifier counters were clean: `16` runs, average reviewed calls `5.69`, average/max truncated calls `0/0`, added/dropped `2/0`, retention mismatches `0`.
- Incident endpoint: `3` creates, `9` updates, `7` rejects in the last hour.

Evidence reviewed:
- Accepted incident `3842`, `Medical assist at Loyalty and Executive Park`, retained calls `790087` and `790093`. Transcript review supports the core event: dispatch to Loyalty / `400 Executive Park Drive NW` for a `50-year-old male` with chest pain and cardiac history. Dropped calls `790117` and `790119` are weak responder-status or cancellation/context calls and do not currently justify a code change.
- Accepted incident `3843`, `Medical Emergency: Difficulty Breathing at 1 E 11th St`, retained calls `790109`, `790113`, `790129`, and `790131`. Transcript review supports the cross-category medical event: fire dispatch, police caution/staging, address `1 East 11th Street`, apartment `211`, difficulty breathing, chest pain, and medical response.
- New issue: row `29566` rejected call `790097` as `medical_transport_context lacks parent emergency/event evidence`. Manual transcript review shows it is not a transport handoff. It is an EMS dispatch to `370 Cleo Circle` for a `79-year-old` male with heart-history wording, clamminess, and weakness, even though it also says negative on chest pain and difficulty breathing.
- Root cause: the early standalone-logistics prefilter trusted the model's `medical_transport_context` label without checking whether the retained transcript itself contained parent medical-emergency symptoms. The validator also missed common symptom wording such as clammy, weakness, heart issues, shortness of breath, altered mental status, and plural chest pains.
- Existing positive signals held: missing-person incident `3840` still retained `789865`; BOLO incident `3841` did not absorb unrelated calls; no false sibling merge or unknown incident identifier appeared.

Action taken:
- Deployed medical-symptom parent-emergency correction at `2026-06-18T08:37:11Z`.
- The standalone logistics prefilter now allows a model-labeled medical transport candidate to reach normal validation when the retained transcript contains parent emergency evidence after removing explicit negations such as negative chest pain or negative difficulty breathing.
- The validator's strong medical-event signal list now includes common dispatch symptom wording such as chest pains, shortness of breath, clammy, weakness, heart issues, altered mental status, and diaphoretic.
- Full solution test suite passed before deploy: `116/116`.
- Post-deploy verification: health OK, `pizzad` active since `2026-06-18 04:37:11 EDT`, `0` restarts, queue depth `0`, pending audio `0s`, embedding endpoint OK, Qdrant OK, embedding queue `0`, failed embeddings `0`.
- Automation boundary updated to `2026-06-18T08:37:11Z`.

Residual risk:
- The `08:37:11Z` deployment is service-verified but not behaviorally proven yet. Next runs should verify that `790097` or equivalent symptom-bearing EMS dispatches create incidents through normal live processing.
- Watch for false medical single-call incidents from weak words such as `weak` when they are not patient symptoms. The deployed pattern requires symptom-style phrasing and the normal single-call concrete-anchor checks still apply.
- Continue watching weak responder-status calls like `790119`. They may be related context, but this run did not show a user-facing incident-quality failure from excluding them.

### 2026-06-18 08:49Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T08:54Z`; rows before the `2026-06-18T08:37:11Z` medical symptom prefilter deployment are context unless needed for comparison.

Observed:
- Services stayed healthy before and after deploy: health OK, queue depth `0`, pending transcriptions `0`, pending audio `0s`.
- `pizzad` is active since `2026-06-18 04:53:51 EDT`, with `0` restarts after the deploy in this run.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, pending `0`, failed `0`.
- Last-hour calls before deploy: `202` total, `2,952s` audio. Direct quality counts: `184` OK, `14` repetitive, `2` empty, `2` too short.
- Throughput was healthy: ingest about `3.8 calls/min`, transcribe about `4.1 calls/min`, average transcription realtime factor about `0.104`.
- AI routing clean: `32` requests, `32` successes, `0` failures, `0` truncations. Recent rows used request model `qwen/qwen3.6-35b-a3b@q8_0`, response model `qwen3.6-35b-a3b`, and finish reason `stop`.
- Evidence verifier counters were clean: `15` runs, average reviewed calls `6.4`, average/max truncated calls `0.27/2`, added/dropped `1/0`, retention mismatches `0`.
- Incident endpoint: `2` creates, `8` updates, `9` rejects in the last hour.

Evidence reviewed:
- Post-boundary rows `29578` and `29579` cleanly updated incident `3842` with calls `790087` and `790093`, while excluding weak responder-context calls `790119` and `790159`. Manual transcript review supports that decision; `790159` asks for the caller location at Executive Park but adds no new emergency facts.
- Post-boundary row `29575` rejected calls `790183` and `790249` as medical transport lacking parent event evidence. Manual transcript review supports no code change: `790183` is an ambiguous MedCom transport or handoff fragment with no concrete scene anchor, while `790249` is an unrelated financial report.
- New issue: rows `29576` and `29577` rejected a mixed candidate that included already accepted `1 E 11th St` medical calls plus a separate strong medical event at `6200 Hixson Pike`: calls `790223`, `790231`, `790235`, `790239`, and `790241` describe an unconscious or semi-comatose 33-year-old inhaling aerosols at a concrete address.
- Root cause: the assembler had no recovery path when a new candidate mixed verifier-rejected or already-owned selected calls with a coherent standalone event subset. Once the original selected group collapsed, the remaining valid Hixson Pike calls were left as weak/unrelated instead of being revalidated as their own server-owned event.

Action taken:
- Deployed mixed-candidate standalone event recovery at `2026-06-18T08:53:51Z`.
- The assembler can now recover a standalone server-owned event only on the create path, only after excluding already-owned and hard verifier-rejected calls, and only if the existing event validator proves the remaining call subset is a valid incident. Final server-owned membership validation still runs before persistence.
- A post-deploy row, `29583`, exposed an adjacent miss: calls `790145` and `790163` likely describe a patient with a stab wound at Common Spirit ER Bed 15, but `stab` and `stabbed` wording were not treated as strong event evidence.
- Deployed stabbing-wording support at `2026-06-18T08:59:19Z`. The strong-event and corroborated-emergency patterns now recognize `stab` and `stabbed`, and title grounding can produce a specific stabbing fallback narrative from retained transcript evidence.
- Full solution test suite passed before deploy: `116/116`.
- Post-deploy verification: health OK, `pizzad` active since `2026-06-18 04:59:19 EDT`, `0` restarts, queue depth `0`, pending audio `0s`, embedding endpoint OK, Qdrant OK, embedding queue `0`, failed embeddings `0`.
- Automation boundary updated to `2026-06-18T08:59:19Z`.

Residual risk:
- The `08:59:19Z` deployment is service-verified but not behaviorally proven yet. Next runs should verify that the Hixson Pike unconscious-patient calls, the Common Spirit possible-stab-wound calls, or equivalent mixed-candidate standalone event subsets create incidents through normal live processing.
- Watch for false new incidents from unrelated leftovers in mixed candidates. The recovery path is limited to create candidates and still requires the normal event validator plus final membership validation.
- Watch for false violent-event titles from incidental words such as `stab` in non-incident contexts. The normal concrete-anchor and multi-call corroboration checks still apply.

### 2026-06-18 09:04Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T09:08Z`; rows before the `2026-06-18T08:59:19Z` mixed-candidate and stabbing deployment are context unless needed for comparison.

Observed:
- Services healthy before and after deploy: health OK, queue depth `0`, pending transcriptions `0`, pending audio `0s`.
- `pizzad` is active since `2026-06-18 05:07:24 EDT`, with `0` restarts after the deploy in this run.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, pending `0`, failed `0`.
- Last-hour calls before deploy: `190` total, `2,796s` audio. Direct quality counts: `173` OK, `13` repetitive, `2` empty, `2` too short.
- Throughput remained healthy: ingest about `2.4 calls/min`, transcribe about `2.4 calls/min`, average transcription realtime factor about `0.172`.
- AI routing clean: `33` requests, `33` successes, `0` failures, `0` truncations. Recent rows used request model `qwen/qwen3.6-35b-a3b@q8_0`, response model `qwen3.6-35b-a3b`, and finish reason `stop`.
- Evidence verifier counters were clean: `16` runs, average reviewed calls `6.81`, average/max truncated calls `0.69/3`, added/dropped `2/0`, retention mismatches `0`.
- Incident endpoint: `2` creates, `7` updates, `12` rejects in the last hour.

Evidence reviewed:
- Post-boundary row `29586` showed the `08:59:19Z` stabbing-wording patch was not sufficient by itself. The mixed candidate still rejected `790145` and `790163` as weak after excluding already accepted `1 E 11th St` calls. Manual transcript review still supports a likely same-event pair: `790145` says a patient showed up with a stab wound, and `790163` gives Common Spirit ER Bed 15 patient/subject context.
- Root cause: adding `stab` as a strong event signal did not make the two-call pair corroborate. The validator's multi-call path still required token or concrete-anchor similarity; it did not recognize close same-talkgroup hospital or ER patient-context continuation when only one call repeated the violent event word.
- Post-boundary row `29585` rejected single call `790053` as weak. Manual transcript review shows a concrete carbon monoxide alarm event at or near `42nd DuPont Street` / Office Ridge and Tombers, with the alarm reactivating and everyone out of the house. Root cause: carbon monoxide and CO alarm wording was absent from the strong event taxonomy and fallback title grounding.
- Post-boundary row `29584` rejected `790203` as medical transport lacking parent emergency evidence. Manual transcript review supports no code change in this run: it reads as a hospital handoff or transport report with vitals and ETA, not a scene dispatch.

Action taken:
- Deployed hospital-context continuation and carbon-monoxide alarm support at `2026-06-18T09:07:24Z`.
- The validator now allows close same-system, same-talkgroup pairs to corroborate when one call has strong event evidence and both calls carry patient plus hospital, ER, medical-center, facility, or bed context. This is intended for dispatch continuations such as the Common Spirit ER stab-wound pair, not for cross-talkgroup semantic joins.
- Carbon monoxide and CO alarm wording now count as strong event evidence, and title grounding can produce a specific carbon-monoxide-alarm fallback narrative from retained transcript evidence.
- Full solution test suite passed before deploy: `116/116`.
- Post-deploy verification: health OK, `pizzad` active since `2026-06-18 05:07:24 EDT`, `0` restarts, queue depth `0`, pending audio `0s`, embedding endpoint OK, Qdrant OK, embedding queue `0`, failed embeddings `0`.
- Automation boundary updated to `2026-06-18T09:07:24Z`.

Residual risk:
- The `09:07:24Z` deployment is service-verified but not behaviorally proven yet. Next runs should verify that the Common Spirit stab-wound pair, the carbon monoxide alarm call, or equivalent events create incidents through normal live processing.
- Watch for false joins from same-talkgroup hospital or ER context where only one call has true event evidence. The rule is limited to close same-system, same-talkgroup pairs and still requires patient plus hospital/ER context on both calls.
- Watch for false single-call fire or hazmat incidents from incidental carbon-monoxide wording, but the normal concrete-anchor and noisy/routine filters still apply.

### 2026-06-18 09:19Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T09:22Z`; rows before the `2026-06-18T09:07:24Z` hospital-context and carbon-monoxide deployment are context unless needed for comparison.

Observed:
- Services healthy before and after deploy: health OK, queue depth `0`, pending transcriptions `0`, pending audio `0s`.
- `pizzad` is active since `2026-06-18 05:21:45 EDT`, with `0` restarts after the deploy in this run.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, pending `0`, failed `0`.
- Last-hour calls before deploy: `177` total, `2,631s` audio. Direct quality counts: `163` OK, `10` repetitive, `2` empty, `2` too short.
- Throughput remained healthy: ingest about `2.9 calls/min`, transcribe about `3.1 calls/min`, average transcription realtime factor about `0.089`.
- AI routing clean: `34` requests, `34` successes, `0` failures, `0` truncations. Recent rows used request model `qwen/qwen3.6-35b-a3b@q8_0`, response model `qwen3.6-35b-a3b`, and finish reason `stop`.
- Evidence verifier counters were clean: `18` runs, average reviewed calls `7`, average/max truncated calls `1/4`, added/dropped `2/0`, retention mismatches `0`.
- Incident endpoint: `2` creates, `6` updates, `18` rejects in the last hour.

Evidence reviewed:
- Post-boundary row `29591` still rejected the Common Spirit possible-stab-wound pair after excluding already accepted `1 E 11th St` calls. This showed the `09:07:24Z` hospital-context continuation was not enough when the candidate had an existing-incident target: the assembler recovery was disabled whenever target resolution had selected an existing incident, even if all existing calls were then hard-rejected by the verifier.
- Post-boundary row `29590` still rejected the carbon monoxide alarm call `790053` for the same target-reset reason: the coherent standalone call was left as weak after the mixed candidate targeted existing incident `3843` and the already accepted `1 E 11th St` calls were rejected.
- Post-boundary row `29592` rejected calls `790399`, `790405`, and `790411` because the narrative lacked a specific evidence-backed fallback. Manual transcript review supports a true medical emergency: 70-year-old female, cardiac dispatch, chest not rising or falling, and Highway 193 / Pace Road location context.
- Rows `29595` and `29596` did not justify a code change. `790155` is a vague THP/excavator-in-median fragment without a concrete event anchor, and `790329` is a single address/location handoff without a strong emergency/event signal.

Action taken:
- Deployed mixed-candidate target reset and cardiac fallback grounding at `2026-06-18T09:21:45Z`.
- When every already accepted target call is removed by verifier or ownership checks, the assembler can now recover a valid unowned standalone subset and force the save path back to a new incident instead of updating the previously selected target incident. The normal event validator and final membership validation still run before persistence.
- Title grounding now treats non-breathing phrases such as chest not rising or falling as non-breathing patient evidence, and can use cardiac wording as a specific fallback title when retained transcripts support it.
- Full solution test suite passed before deploy: `116/116`.
- Post-deploy verification: health OK, `pizzad` active since `2026-06-18 05:21:45 EDT`, `0` restarts, queue depth `0`, pending audio `0s`, embedding endpoint OK, Qdrant OK, embedding queue `0`, failed embeddings `0`.
- Automation boundary updated to `2026-06-18T09:21:45Z`.

Residual risk:
- The `09:21:45Z` deployment is service-verified but not behaviorally proven yet. Next runs should verify that the Common Spirit possible-stab-wound pair, the carbon monoxide alarm call, the Highway 193 cardiac event, or equivalent mixed-target standalone subsets create incidents through normal live processing.
- Watch for false new incidents when a model mixes an existing incident with weak unrelated leftovers. Recovery still requires a valid event subset, but this is a new target-reset path.
- Watch for generic cardiac wording producing specific titles without enough retained evidence. The grounding change only affects fallback selection after retained calls pass validation.

### 2026-06-18 09:34Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T09:37Z`; rows before the `2026-06-18T09:21:45Z` mixed-target reset deployment are context unless needed for comparison.

Observed:
- Services healthy before and after deploy: health OK, queue depth `0`, pending transcriptions `0`, pending audio `0s`.
- `pizzad` is active since `2026-06-18 05:36:51 EDT`, with `0` restarts after the deploy in this run.
- Embeddings healthy: endpoint OK, Qdrant OK, queue `0`, pending `0`, failed `0`.
- Last-hour calls before deploy: `183` total, `2,615s` audio. Direct quality counts: `171` OK, `8` repetitive, `3` empty, `1` too short.
- Throughput remained healthy: ingest about `2.8 calls/min`, transcribe about `2.9 calls/min`, average transcription realtime factor about `0.109`.
- AI routing had one deploy-time cancellation row at `09:21:45Z`; all later rows succeeded with request model `qwen/qwen3.6-35b-a3b@q8_0`, response model `qwen3.6-35b-a3b`, and finish reason `stop`. No finish reason `length` or model-routing regression was observed.
- Evidence verifier counters were clean: `15` runs, average reviewed calls `7.87`, average/max truncated calls `1.27/4`, added/dropped `3/0`, retention mismatches `0`.
- Incident endpoint: `2` creates, `3` updates, `17` rejects in the last hour.

Evidence reviewed:
- Positive proof for mixed-target recovery arrived: rows `29597` and `29598` created a standalone Common Spirit possible-stab-wound incident from calls `790145` and `790163` after pruning the unrelated already accepted `1 E 11th St` calls.
- New regression: row `29599` then immediately merged duplicate incident `3844` into existing incident `3843`, leaving unrelated Common Spirit possible-stab-wound calls `790145` and `790163` on the `1 E 11th St` breathing incident. Remote DB confirms incident `3843` now contains `790109`, `790113`, `790129`, `790131`, `790145`, and `790163`.
- Root cause: sibling-incident merge repair still allowed broad medical or patient-context compatibility to override specific event mismatch. The target incident was difficulty breathing / chest pain at `1 E 11th St`; the duplicate incident was a possible stab wound at Common Spirit ER Bed 15. The event concepts were incompatible even though both mentioned patient or medical context.
- Accepted seizure incident `3845` with calls `790467`, `790471`, `790483`, and `790497` looks structurally sane from telemetry: final membership pruned one unlinked call, retained a four-call event, and used a specific seizure title. No false join was evident from audit rows.

Action taken:
- Deployed sibling-merge specific-event compatibility guard at `2026-06-18T09:36:51Z`.
- Sibling merge repair now refuses to merge two active incidents when both sides have specific event concepts and those concepts do not overlap. This blocks merges such as difficulty breathing plus stabbing, while still allowing repairs for duplicate incidents that share a specific event concept.
- Full solution test suite passed before deploy: `116/116`.
- Post-deploy verification: health OK, `pizzad` active since `2026-06-18 05:36:51 EDT`, `0` restarts, queue depth `0`, pending audio `0s`, embedding endpoint OK, Qdrant OK, embedding queue `0`, failed embeddings `0`.
- Automation boundary updated to `2026-06-18T09:36:51Z`.

Residual risk:
- Historical bad membership from row `29599` remains in production because this bake is not allowed to manually repair incidents, rerun backfills, or mutate remote production data directly. Watch for normal live processing to remove `790145` and `790163` from incident `3843`; otherwise report it in the final summary as a known stale artifact.
- The `09:36:51Z` sibling-merge guard is service-verified but not behaviorally proven yet. Next runs should verify there are no new sibling merges across incompatible event concepts and no duplicate siblings left unmerged when both sides truly share the same specific event.
- Keep watching for over-tightening: a true continuation that changes wording from symptoms to a more precise diagnosis may need careful evidence, not a broad merge.

### 2026-06-18 09:49Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T09:50Z`; rows before the `2026-06-18T09:36:51Z` sibling-event-concept guard are context unless needed for comparison.

Observed:
- Services are healthy: health OK, queue depth `0`, pending transcriptions `0`, pending audio `0s`.
- `pizzad` is active since `2026-06-18 05:36:51 EDT`, with `0` restarts after the latest deploy.
- Embeddings are healthy: endpoint OK, Qdrant OK, queue `0`, pending `0`, failed `0`.
- Last-hour calls: `171` total, `2,470s` audio, about `17.5` active incidents per 1,000 calls using the quality endpoint's three recent incidents.
- Direct transcript quality counts: `160` OK, `7` repetitive, `2` empty, `2` too short.
- Throughput stayed healthy: ingest about `3.5 calls/min`, transcribe about `3.7 calls/min`, average transcription realtime factor about `0.128`.
- AI routing after the current boundary is clean: latest rows used request model `qwen/qwen3.6-35b-a3b@q8_0`, response model `qwen3.6-35b-a3b`, and finish reason `stop`. The one failure in the last-hour aggregate was the earlier `09:21:45Z` deploy-time cancellation, not a post-boundary model regression.
- Evidence verifier counters stayed clean: `17` runs, average reviewed calls `6.53`, average/max truncated calls `0.94/4`, added/dropped `4/3`, retention mismatches `0`.
- Incident endpoint aggregate: `4` creates, `5` updates, `15` rejects in the last hour. Post-boundary audit rows showed accepted updates only; no new sibling merge repair row appeared.

Evidence reviewed:
- Post-boundary rows `29606` through `29612` updated only the Highway 193 medical incident `3846` and the Westonia Drive seizure incident `3845`. No post-boundary audit row merged sibling incidents with incompatible specific event concepts.
- Incident `3845` retained calls `790467`, `790471`, `790483`, and `790497`; transcript spot-check supports a coherent seizure response at `3205 Westonia / West Donia Drive` with Medic 13 and East Ridge Fire context.
- The same `3845` updates excluded `790487`, `790621`, and `790627`. Spot-check supports no code change here: `790487` is a separate Highway 123 / Highway 193 dispatch fragment, while `790621` and `790627` read like later hospital or transport/encode traffic rather than scene-dispatch membership that should override final membership.
- Remote DB still shows historical bad membership from pre-guard row `29599`: incident `3843` contains `790145` and `790163` alongside the original `1 E 11th St` breathing calls. That is stale production data from before the `09:36:51Z` guard, not a new post-boundary failure.

Action taken:
- No code change and no deployment in this run.
- Watchlist updated with the clean post-boundary evidence and the remaining stale-membership risk.
- Automation boundary remains `2026-06-18T09:36:51Z`.

Residual risk:
- Continue watching whether normal live processing removes stale calls `790145` and `790163` from incident `3843`. This bake is not allowed to manually repair production incidents or rerun backfills.
- Continue watching for a true post-boundary incompatible sibling merge, and for the opposite failure mode where true duplicate siblings with matching specific event concepts are left unmerged.
- Continue watching whether hospital/transport-style continuations are being excluded appropriately rather than silently dropping scene-dispatch evidence.

### 2026-06-18 10:04Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T10:12Z`; rows before the `2026-06-18T09:36:51Z` sibling-event-concept guard are context unless needed for comparison. This run changed the boundary twice: the first `10:08:23Z` deployment was disproven immediately, and the corrected deployment boundary is now `2026-06-18T10:11:39Z`.

Observed:
- Services were healthy before and after both deploys: health OK, queue depth `0`, pending transcriptions `0`, pending audio `0s`.
- Corrected deploy service state: `pizzad` active since `2026-06-18 06:11:39 EDT`, with `0` restarts.
- Embeddings stayed healthy: endpoint OK, Qdrant OK, queue `0`, pending `0`, failed `0`.
- Last-hour calls after the corrected deploy: `176` total, `2,761s` audio. Direct quality was stable enough for this bake; before deploy it was `161` OK, `9` repetitive, `4` empty, and `2` too short.
- Throughput stayed healthy after deploy: ingest about `2.1 calls/min`, transcribe about `2.4 calls/min`, average transcription realtime factor about `0.135`.
- AI routing stayed clean after deploy: latest rows used request model `qwen/qwen3.6-35b-a3b@q8_0`, response model `qwen3.6-35b-a3b`, and finish reason `stop`. The one failure in the last-hour aggregate remained the earlier `09:21:45Z` deploy-time cancellation.
- Evidence verifier counters stayed clean: `16` runs, average reviewed calls `5.63`, average/max truncated calls `0.13/1`, added/dropped `5/3`, retention mismatches `0`.
- Incident endpoint aggregate after the corrected deploy: `5` creates, `8` updates, `7` rejects in the last hour.

Evidence reviewed:
- Post-`09:36:51Z` rows `29613` and `29614` updated Highway 193 cardiac incident `3846` but still excluded call `790405`, even though transcript review shows `790405` is the strongest symptom-bearing same-event call: cardiac dispatch, 70-year-old female, chest not rising or falling.
- Calls `790399`, `790405`, and `790411` form a split same-event dispatch chain. `790399` supplies Medic 13 and Highway 193 / Pace Road context, `790405` supplies the cardiac and non-breathing facts, and `790411` supplies address/detail clarification near Highway 193 and Pace Road.
- Root cause found before editing: the assembler only saw `790399` and `790411` as accepted existing membership, then treated new call `790405` as weak because the accepted calls did not individually repeat the cardiac evidence and the location detail was split across talkgroups or messages.
- First fix deployed at `2026-06-18T10:08:23Z`: added guarded same-system, same-talkgroup dispatch-detail linkage when exactly one call has strong incident evidence and the paired call has concrete location detail plus explicit address or continuation wording.
- The first fix failed immediately. Rows `29621` and `29622` still excluded `790405` and then rejected the two-call `790399,790411` update because the pair similarity was below threshold. The missed detail was the comma in `2004, Highway 193`; the normal address extractor did not turn it into a concrete anchor.
- Corrected fix deployed at `2026-06-18T10:11:39Z`: the split-dispatch link now also accepts explicit address-detail wording with a significant road or route number when punctuation prevents normal concrete-anchor extraction.
- Row `29626`, before the corrected boundary, created incident `3848` with calls `790639` and `790653`. Transcript review supports the incident: a not-responsive or unconscious 50-year-old female around Glenwood Drive and Citico Avenue, with matching age, patient, clothing, and response context.

Action taken:
- Updated `AutomaticInsightsService` only. The change is not a call-id or phrase-specific repair; it adds a guarded non-semantic evidence link for very close same-system, same-talkgroup split dispatch details.
- Full solution test suite passed before each deploy: `116/116` for the `10:08:23Z` attempt and `116/116` for the corrected `10:11:39Z` deployment.
- Deployed twice through `.\scripts\deploy_pizzad_tar.ps1 -HostName lilhoser@192.168.1.173 -Rid linux-x64`.
- Post-corrected-deploy verification: health OK, `pizzad` active since `2026-06-18 06:11:39 EDT`, `0` restarts, queue depth `0`, pending audio `0s`, embedding endpoint OK, Qdrant OK, embedding queue `0`, failed embeddings `0`.
- Automation boundary updated to `2026-06-18T10:11:39Z`.

Residual risk:
- The corrected `10:11:39Z` deployment is service-verified but not behaviorally proven yet; no audit rows had landed after that exact boundary when this run ended. Next runs should verify whether normal live processing attaches `790405` to incident `3846` or handles the next similar split dispatch correctly.
- Watch for false joins from same-talkgroup split dispatch-detail linkage when an address detail belongs to the next separate dispatch rather than the preceding symptom-bearing call. The rule is intentionally narrow: same system, same talkgroup, within six minutes, exactly one strong incident call, explicit address or continuation wording, and concrete location detail or a significant road/route number.
- Historical stale membership from row `29599` remains: incident `3843` still includes `790145` and `790163`, and this bake is not allowed to manually repair production data or rerun backfills.
- Incident `3845` is now down to calls `790467`, `790471`, and `790497`; call `790483` was verifier-rejected in row `29623`. The remaining set still supports the seizure incident, but the dropped cross-agency fire-dispatch call should be watched for a repeated pattern.

### 2026-06-18 10:19Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T10:19Z`; rows before the corrected `2026-06-18T10:11:39Z` split-dispatch-detail deployment are context unless needed for comparison.

Observed:
- Services are healthy: health OK, queue depth `0`, pending transcriptions `1`, pending audio `9s`; this is not backlog pressure.
- `pizzad` remains active since `2026-06-18 06:11:39 EDT`, with `0` restarts after the corrected deploy.
- Embeddings remain healthy: endpoint OK, Qdrant OK, queue `0`, pending `0`, failed `0`.
- Last-hour calls: `180` total, `2,917s` audio, about `16.7` active incidents per 1,000 calls using the quality endpoint's three recent incidents.
- Direct transcript quality counts: `164` OK, `9` repetitive, `5` empty, `1` pending, `1` too short.
- Throughput remained healthy: ingest about `3.2 calls/min`, transcribe about `3.1 calls/min`, average transcription realtime factor about `0.096`.
- AI routing after the corrected boundary is clean: latest rows used request model `qwen/qwen3.6-35b-a3b@q8_0`, response model `qwen3.6-35b-a3b`, and finish reason `stop`. The one failure in the last-hour aggregate remains the earlier deploy-time cancellation, not a current model issue.
- Evidence verifier counters stayed clean: `16` runs, average reviewed calls `6.25`, average/max truncated calls `0.31/3`, added/dropped `6/3`, retention mismatches `0`.
- Incident endpoint aggregate: `5` creates, `9` updates, `4` rejects in the last hour.

Evidence reviewed:
- Post-corrected-boundary rows `29627` through `29632` updated only seizure incident `3845`. No post-boundary rows exercised the Highway 193 cardiac incident `3846`, so the split-dispatch-detail fix is still not behaviorally proven.
- The seizure updates were structurally sane. Rows `29627` and `29630` excluded weak or unrelated calls `790487`, `790621`, and `790627`; row `29632` retained `790467`, `790471`, `790483`, and `790497`.
- This also reversed the pre-corrected transient drop of `790483`; the remote DB currently shows incident `3845` retaining all four supported seizure calls.
- Remote DB still shows `3846` with only `790399` and `790411`. That is stale membership from before the corrected deployment, because no normal live update touched `3846` in the post-corrected window. No manual repair or backfill was performed.
- Remote DB still shows stale pre-guard bad membership on `3843`: calls `790145` and `790163` remain attached to the `1 E 11th St` breathing incident.
- New incident `3848` remains a plausible two-call unconscious-person incident from the prior run: calls `790639` and `790653` are still the saved membership.

Action taken:
- No code change and no deployment in this run.
- Watchlist updated with the post-corrected clean rows and the remaining behavior-proof gap.
- Automation boundary remains `2026-06-18T10:11:39Z`.

Residual risk:
- Continue watching for a fresh normal update to `3846` or a similar split dispatch to prove the corrected split-dispatch link attaches symptom-bearing calls such as `790405`.
- Continue watching for false joins from the same-talkgroup split-dispatch-detail link when an address detail belongs to a separate adjacent dispatch.
- Keep stale `3843` and stale `3846` membership in the final report as historical artifacts unless normal live processing fixes them.

### 2026-06-18 10:34Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T10:34Z`; rows before the corrected `2026-06-18T10:11:39Z` split-dispatch-detail deployment are context unless needed for comparison.

Observed:
- Services are healthy: health OK, queue depth `0`, pending transcriptions `1`, pending audio `0s`.
- `pizzad` remains active since `2026-06-18 06:11:39 EDT`, with `0` restarts after the corrected deploy.
- Embeddings remain healthy: endpoint OK, Qdrant OK, queue `0`, pending `0`, failed `0`.
- Last-hour calls: `173` total, `2,967s` audio, about `5.8` active incidents per 1,000 calls using the quality endpoint's one recent incident.
- Direct transcript quality counts: `149` OK, `16` repetitive, `4` empty, `2` too short, `1` pending, `1` marker-only. Repetitive calls are higher than the prior run but not yet a throughput or incident-quality driver.
- Throughput remained healthy: ingest about `3.0 calls/min`, transcribe about `3.2 calls/min`, average transcription realtime factor about `0.132`.
- AI routing was clean: `35` requests, `35` successes, `0` failures, `0` truncations. Recent rows used request model `qwen/qwen3.6-35b-a3b@q8_0`, response model `qwen3.6-35b-a3b`, and finish reason `stop`; two recent rows marked `recovered_after_truncation_split` but succeeded.
- Evidence verifier counters stayed clean: `17` runs, average reviewed calls `5.71`, average/max truncated calls `0.24/3`, added/dropped `4/3`, retention mismatches `0`.
- Incident endpoint aggregate: `3` creates, `11` updates, `7` rejects in the last hour.

Evidence reviewed:
- Post-corrected-boundary rows `29627` through `29639` showed no sibling merge repair, no incompatible event merge, and no false split-dispatch join.
- Incident `3848` remained stable. Rows `29633` through `29637` updated it with the same two-call set `790639,790653`; transcript review from the prior run supports the unconscious-person event.
- Incident `3845` remained stable on `790467,790471,790483,790497` after rows `29627` through `29632`, so the earlier transient drop of `790483` did not persist.
- No post-corrected-boundary row touched Highway 193 cardiac incident `3846`; the split-dispatch-detail fix remains unproven against `790405`.
- Rejected row `29635`, call `790783`, appears acceptable to leave rejected: transcript suggests a Subaru Forester struck a deer on Hunter Road and is out of the roadway, with no clear injury, active hazard, or strong event anchor.
- Rejected row `29639`, call `790857`, appears acceptable to leave rejected: transcript is a noisy broken-down vehicle or abandoned vehicle fragment on I-24 eastbound without a usable concrete location anchor.
- Rejected row `29638`, call `790825`, is a watch item but not enough for a code change: transcript says EMS is en route to `1308/3308 East Main Street` for a sick party requesting police. This may indicate a missing grounded single-call `sick party` taxonomy, but one thin example is not enough to safely broaden medical single-call acceptance.

Action taken:
- No code change and no deployment in this run.
- Watchlist updated with clean post-boundary rows and the new `sick party` taxonomy watch item.
- Automation boundary remains `2026-06-18T10:11:39Z`.

Residual risk:
- Continue watching for a fresh normal update to `3846` or a similar split dispatch to prove the corrected split-dispatch link attaches symptom-bearing calls such as `790405`.
- Watch for repeated concrete sick-party dispatches with address and response evidence being rejected as weak. If repeated, a guarded single-call taxonomy addition may be warranted; avoid broadening on the current single example.
- Continue watching repetitive transcript count; it rose to `16` in the hour but queues and incident behavior remain clean.
- Keep stale `3843` and stale `3846` membership in the final report as historical artifacts unless normal live processing fixes them.

### 2026-06-18 10:49Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T10:50Z`; rows before the corrected `2026-06-18T10:11:39Z` split-dispatch-detail deployment are context unless needed for comparison. This run changed the monitoring boundary to `2026-06-18T10:54:17Z`.

Observed:
- Services were healthy before and after deploy: health OK, queue depth `0`, pending transcriptions `0`, pending audio `0s`.
- Pre-deploy service state: `pizzad` active since `2026-06-18 06:11:39 EDT`, with `0` restarts. Post-deploy state: `pizzad` active since `2026-06-18 06:54:17 EDT`, with `0` restarts.
- Embeddings stayed healthy after deploy: endpoint OK, Qdrant OK, queue `0`, pending `0`, failed `0`.
- Last-hour calls: `193` total, `3,146s` audio. Direct transcript quality counts: `168` OK, `18` repetitive, `5` empty, `1` marker-only, `1` too short.
- Throughput stayed healthy after deploy: ingest about `3.9 calls/min`, transcribe about `3.9 calls/min`, average transcription realtime factor about `0.065`.
- AI routing was clean: `32` requests, `32` successes, `0` failures, `0` truncations. Recent rows used request model `qwen/qwen3.6-35b-a3b@q8_0`, response model `qwen3.6-35b-a3b`, and finish reason `stop`.
- Evidence verifier counters stayed clean: `15` runs, average reviewed calls `5.73`, average/max truncated calls `0.20/3`, added/dropped `4/0`, retention mismatches `0`.
- Incident endpoint aggregate: `4` creates, `9` updates, `10` rejects in the last hour.

Evidence reviewed:
- Post-`10:11:39Z` rows `29640` through `29651` showed no sibling merge repair, no incompatible event merge, and no false split-dispatch join.
- Row `29651` created incident `3851` as `Vehicle off roadway at 1051 High St NE` from single retained call `791021`. Remote DB detail was only `Police dispatch audio reports: 1051 High Street, North East.`
- Transcript review for `791021` does not support a vehicle-off-roadway event: `1051 High Street, North East. Have a female, beaten her husband in the vehicle at front.`
- Root cause: the narrative and validator traffic-event regex accepted bare `1051` as a no-separator `10-51` traffic code. That allowed a street address to ground an unsupported vehicle-off-roadway title and could also act as false strong event evidence.
- Secondary gap: physical-assault wording such as `beaten` was not in the strong event or title-grounding taxonomy, so the retained transcript could not produce the better supported `Assault` fallback.
- Other reviewed rows did not justify code changes in this run: incident `3849` with calls `790603,790635` is a supported chest-pain event; incident `3850` with call `790929` is a supported possible-stroke event; row `29648` correctly excluded weak/unrelated call `790649` while keeping incident `3848` on `790639,790653`.

Action taken:
- Updated `IncidentNarrativeGrounder` and `IncidentCandidateValidator`.
- Numeric 10-code evidence now requires an explicit separator, such as `10-51` or `10 51`, instead of matching bare address-like values such as `1051`.
- Physical-assault wording now includes general variants such as `beaten`, `beating`, `battery`, `battered`, and `fighting` for strong event detection and fallback narrative grounding.
- Full solution test suite passed before deploy: `116/116`.
- Deployed through `.\scripts\deploy_pizzad_tar.ps1 -HostName lilhoser@192.168.1.173 -Rid linux-x64` at `2026-06-18T10:54:17Z`.
- Post-deploy verification: health OK, `pizzad` active since `2026-06-18 06:54:17 EDT`, `0` restarts, queue depth `0`, pending audio `0s`, embedding endpoint OK, Qdrant OK, embedding queue `0`, failed embeddings `0`.
- Automation boundary updated to `2026-06-18T10:54:17Z`.

Residual risk:
- Existing incident `3851` remains a historical bad title because this bake is not allowed to manually repair production incidents or rerun backfills.
- Watch future rows for address-like values such as `1051`, `1049`, `1050`, or `1052` incorrectly producing traffic titles or strong event evidence. Explicit separated radio codes remain supported.
- Watch for real no-separator numeric 10-code transcripts that may now be missed. This is an intentional tradeoff: avoiding address false positives is safer than treating bare four-digit street numbers as event codes.
- Watch whether physical-assault calls now ground to assault without creating false violent-event incidents from incidental fight or battery wording.

### 2026-06-18 11:10Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T11:10Z`; rows before the `2026-06-18T10:54:17Z` numeric 10-code title deployment are context unless needed for comparison. This run changed the monitoring boundary to `2026-06-18T11:13:18Z`.

Observed:
- Services were healthy before and after deploy: health OK, queue depth `0`, pending transcriptions `0`, pending audio `0s`.
- Pre-deploy service state: `pizzad` active since `2026-06-18 06:54:17 EDT`, with `0` restarts. Post-deploy state: `pizzad` active since `2026-06-18 07:13:18 EDT`, with `0` restarts.
- Embeddings stayed healthy after deploy: endpoint OK, Qdrant OK, queue `0`, pending `0`, failed `0`.
- Last-hour calls: `239` total, `3,964s` audio. Direct transcript quality counts: `205` OK, `25` repetitive, `5` empty, `3` too short, `1` marker-only.
- Throughput stayed healthy after deploy: ingest about `4.8 calls/min`, transcribe about `4.9 calls/min`, average transcription realtime factor about `0.135`.
- AI routing was clean: `32` requests, `32` successes, `0` failures, `0` truncations. Recent rows used request model `qwen/qwen3.6-35b-a3b@q8_0`, response model `qwen3.6-35b-a3b`, and finish reason `stop`; two rows reported `recovered_after_truncation_split` but succeeded.
- Evidence verifier counters stayed clean: `15` runs, average reviewed calls `4.40`, average/max truncated calls `0.20/3`, added/dropped `7/1`, retention mismatches `0`.
- Incident endpoint aggregate: `5` creates, `8` updates, `9` rejects in the last hour.

Evidence reviewed:
- Post-`10:54:17Z` rows `29652` through `29661` showed no persisted false numeric 10-code title and no new false vehicle-off-roadway title. Pre-existing incident `3851` remains unchanged because manual repair is outside bake authority.
- Rows `29652` and `29653` logged accepted sibling-candidate merges around possible-stroke incident `3850`, but the final saved update did not persist the broad candidate union. Rows `29654`, `29656`, and `29660` pruned weak/unrelated calls and kept saved membership at `790929`.
- The pruning avoided a false saved join with unrelated sick-party/police-assist call `790825`, which is good. Transcript: EMS to East Main for a sick party requesting police.
- New miss: the same pruning also dropped likely same-event call `790933`. Transcript review supports that it belongs with retained call `790929`: `790929` gives `1201 Boynton Drive` and possible stroke; `790933`, twelve seconds later on the same EMS dispatch talkgroup, gives the same dispatch's patient details: 60-year-old, facial numbness, back pain, Ladder 1 response, and Medic 13 responding.
- Root cause: the existing split-dispatch-detail linkage only handled companion calls that supplied address/location detail. It did not cover the inverse adjacent-transmission pattern where the event call supplies address and event type, while the next same-talkgroup call supplies patient/symptom or response details without repeating location.
- Later hospital encode call `791073` likely describes the same transported patient with right-sided weakness and facial numbness, but it is a different hospital talkgroup and was not used as the edit driver in this run.
- Post-`10:54:17Z` row `29659` created incident `3852`, `Chest pain at 760 Georgetown Rd`, from calls `791015` and `791043` after an earlier single-call attempt was rejected for lacking a concrete anchor. This did not show a numeric 10-code title regression.

Action taken:
- Updated `AutomaticInsightsService` only.
- Added guarded immediate same-talkgroup patient-detail linkage: very close same-system, same-talkgroup pairs can receive strong server-side membership credit when exactly one call has strong incident evidence, the event call already has concrete location or significant road/route location, the companion has patient/symptom or response detail, the companion has no separate concrete location, and the companion is not routine or transport-only text.
- Full solution test suite passed before deploy: `116/116`.
- Deployed through `.\scripts\deploy_pizzad_tar.ps1 -HostName lilhoser@192.168.1.173 -Rid linux-x64` at `2026-06-18T11:13:18Z`.
- Post-deploy verification: health OK, `pizzad` active since `2026-06-18 07:13:18 EDT`, `0` restarts, queue depth `0`, pending audio `0s`, embedding endpoint OK, Qdrant OK, embedding queue `0`, failed embeddings `0`.
- Automation boundary updated to `2026-06-18T11:13:18Z`.

Residual risk:
- The `11:13:18Z` deployment is service-verified but not behaviorally proven yet. Next runs should verify whether normal live processing attaches `790933` to incident `3850` or handles the next similar address/event-plus-patient-detail split dispatch.
- Watch for false joins from the new immediate patient-detail branch when adjacent same-talkgroup patient detail actually belongs to a separate event. The guard is intentionally narrower than the address-detail branch: same system, same talkgroup, within ninety seconds, exactly one strong incident call, event call already has location, companion has no separate location, and routine/transport-only text is excluded.
- Existing incident `3851` remains a historical bad title from before the `10:54:17Z` numeric 10-code fix.
- Continue watching repetitive transcript count; it rose to `25` in the latest hour but queues and incident behavior remained healthy.

### 2026-06-18 11:25Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T11:25Z`; rows before the `2026-06-18T11:13:18Z` same-talkgroup patient-detail deployment are context unless needed for comparison. This run changed the monitoring boundary to `2026-06-18T11:27:09Z`.

Observed:
- Services were healthy before and after deploy: health OK, queue depth `0`, pending transcriptions `0`, pending audio `0s`.
- Pre-deploy service state: `pizzad` active since `2026-06-18 07:13:18 EDT`, with `0` restarts. Post-deploy state: `pizzad` active since `2026-06-18 07:27:09 EDT`, with `0` restarts.
- Embeddings stayed healthy after deploy: endpoint OK, Qdrant OK, queue `0`, pending `0`, failed `0`.
- Last-hour calls: `271` total, `4,397s` audio. Direct transcript quality counts: `239` OK, `22` repetitive, `5` empty, `4` too short, `1` marker-only.
- Throughput stayed healthy after deploy: ingest about `4.5 calls/min`, transcribe about `4.9 calls/min`, average transcription realtime factor about `0.093`.
- AI routing was clean: `30` requests, `30` successes, `0` failures, `0` truncations. Recent rows used request model `qwen/qwen3.6-35b-a3b@q8_0`, response model `qwen3.6-35b-a3b`, and finish reason `stop`.
- Evidence verifier counters stayed clean: `14` runs, average reviewed calls `3.57`, average/max truncated calls `0/0`, added/dropped `7/1`, retention mismatches `0`.
- Incident endpoint aggregate: `5` creates, `7` updates, `10` rejects in the last hour.

Evidence reviewed:
- The `11:13:18Z` immediate patient-detail deployment was behaviorally disproven. Rows `29662` and `29664`, both post-boundary, still excluded `790933` from possible-stroke incident `3850`. Current DB membership remains `790929` only.
- Root cause: `790933` contains `fire response`. The patient-detail branch required exactly one strong incident call, but the global strong-signal pattern still treats bare `fire` as strong in some paths. As a result, `790933` looked like a second strong event call and the patient-detail branch did not run.
- The failed rows did not create a false persisted join: `790825`, `790935`, and hospital encode call `791073` were also excluded. That avoided a broad saved union, but the intended `790933` still stayed out.
- No new false vehicle-off-roadway or address-like numeric 10-code title appeared after the `10:54:17Z` deployment. Pre-existing incident `3851` remains unchanged as expected.
- New incident `3853`, `Medical assist at 5911 Snow Hill Rd, toddler patient`, is a single-call active incident from `791155`. No immediate false-join pattern was evident from the audit rows; keep watching title specificity and single-call acceptance.
- Row `29668` rejected calls `791269,791311` as standalone transport/hospital handoff without a parent emergency/event anchor. This is consistent with the current transport guard and did not justify a change in this run.

Action taken:
- Updated `AutomaticInsightsService` only.
- Corrected the immediate same-talkgroup patient-detail linkage to ignore response-resource wording such as `fire response` when deciding which side is the primary event call for this branch. This keeps global fire evidence unchanged while preventing response-detail wording from blocking the split-dispatch patient-detail link.
- Full solution test suite passed before deploy: `116/116`.
- Deployed through `.\scripts\deploy_pizzad_tar.ps1 -HostName lilhoser@192.168.1.173 -Rid linux-x64` at `2026-06-18T11:27:09Z`.
- Post-deploy verification: health OK, `pizzad` active since `2026-06-18 07:27:09 EDT`, `0` restarts, queue depth `0`, pending audio `0s`, embedding endpoint OK, Qdrant OK, embedding queue `0`, failed embeddings `0`.
- Automation boundary updated to `2026-06-18T11:27:09Z`.

Residual risk:
- The corrected `11:27:09Z` deployment is service-verified but not behaviorally proven yet. Next runs should verify whether normal live processing attaches `790933` to incident `3850` or handles the next similar patient-detail split dispatch.
- Watch for false joins from immediate patient-detail linkage when response-resource wording appears in an adjacent but separate dispatch.
- Keep watching address-like numeric values after the `10:54:17Z` fix; no post-fix recurrence was seen in this run.
- Continue watching repetitive transcript count; it remained elevated at `22`, but queues, throughput, AI, and incident behavior were not under pressure.

### 2026-06-18 11:40Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T11:40Z`; rows before the corrected `2026-06-18T11:27:09Z` patient-detail response-wording deployment are context unless needed for comparison. This run changed the monitoring boundary to `2026-06-18T11:42:44Z`.

Observed:
- Services were healthy before and after deploy: health OK, queue depth `0`, pending transcriptions `0`, pending audio `0s`.
- Pre-deploy service state: `pizzad` active since `2026-06-18 07:27:09 EDT`, with `0` restarts. Post-deploy state: `pizzad` active since `2026-06-18 07:42:44 EDT`, with `0` restarts.
- Embeddings stayed healthy after deploy: endpoint OK, Qdrant OK, queue `0`, pending `0`, failed `0`.
- Last-hour calls: `302` total, `5,002s` audio. Direct transcript quality counts: `267` OK, `23` repetitive, `7` empty, `5` too short.
- Throughput stayed healthy after deploy: ingest about `5.0 calls/min`, transcribe about `5.6 calls/min`, average transcription realtime factor about `0.082`.
- AI routing was clean: `33` requests, `33` successes, `0` failures, `0` truncations.
- Evidence verifier counters stayed clean: `18` runs, average reviewed calls `4.5`, average/max truncated calls `0/0`, added/dropped `8/1`, retention mismatches `0`.

Evidence reviewed:
- The corrected `11:27:09Z` patient-detail deployment is now behaviorally proven. Rows `29671` through `29676` updated possible-stroke incident `3850` with retained calls `790929` and `790933`; final membership retained `2/2` even though the validator matched only `1/2`, and normal live processing excluded weak or unrelated calls instead of broad-unioning them.
- New regression: row `29680` merged duplicate incident `3853` into lift-assist incident `3854`, leaving unrelated call `791155` attached to call `791099`.
- Transcript review proves a concrete-location conflict: `791099` is a lift assist at `8227 Trubidor Way`; `791155` is an unresponsive three-year-old at `5911 Snow Hill Road / Life Care`.
- Root cause: the sibling event-concept guard did not catch the case because the lift-assist transcript used `fell`, while the concept pattern only recognized `fall`. The stronger invariant is concrete membership-anchor compatibility, not another spelling variant.
- Rows `29678` and `29679` created heart-problem incident `3855` from calls `791341`, `791355`, and `791375`; the saved set looked coherent.
- Rows `29681` and `29682` safely updated chest-pain incident `3852`, retained `791015` and `791043`, excluded `791311`, and preserved the existing supported title/detail after an unsupported update proposal.

Action taken:
- Updated `AutomaticInsightsService` only.
- Added a concrete-location conflict guard to sibling-incident merge repair. A sibling repair is now blocked when both sides have incompatible concrete membership anchors, unless a strong police BOLO identity link connects the calls.
- Full solution test suite passed before deploy: `116/116`.
- Deployed through `.\scripts\deploy_pizzad_tar.ps1 -HostName lilhoser@192.168.1.173 -Rid linux-x64` at `2026-06-18T11:42:44Z`.
- Post-deploy verification: health OK, `pizzad` active since `2026-06-18 07:42:44 EDT`, `0` restarts, queue depth `0`, pending audio `0s`, embedding endpoint OK, Qdrant OK, embedding queue `0`, failed embeddings `0`.
- Automation boundary updated to `2026-06-18T11:42:44Z`.

Residual risk:
- Existing incident `3854` remains stale bad membership with `791155` attached, because this bake is not allowed to manually repair production incidents or rerun backfills.
- The `11:42:44Z` concrete-location conflict guard is service-verified but not behaviorally proven yet. Next runs should verify that sibling repair no longer merges incidents with conflicting concrete anchors.
- Watch for true duplicate sibling incidents left unmerged because their concrete anchors differ in transcript wording even though they are the same location.
- Continue watching repetitive transcript count; it remains elevated, but queues, throughput, AI, and incident behavior were not under pressure.

### 2026-06-18 11:55Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T11:59Z`; rows before the `2026-06-18T11:42:44Z` sibling-location conflict guard are context unless needed for comparison. This run changed the monitoring boundary to `2026-06-18T11:58:36Z`.

Observed:
- Services were healthy before and after deploy. Post-deploy health OK; queue depth `1`, pending transcriptions `2`, pending audio `55s`, and queue pressure false.
- Post-deploy `pizzad` is active since `2026-06-18 07:58:36 EDT`, with `0` restarts.
- Embeddings stayed healthy after deploy: endpoint OK, Qdrant OK, queue `0`, pending `1`, failed `0`.
- Last-hour calls: `321` total, `5,435s` audio. Direct transcript quality counts: `284` OK, `21` repetitive, `8` too short, `5` empty, `2` pending, `1` numeric-noise.
- Throughput stayed healthy: ingest about `5.8 calls/min`, transcribe about `5.7 calls/min`, average transcription realtime factor about `0.159`.
- AI routing was clean: `38` requests, `38` successes, `0` failures, `0` truncations.
- Evidence verifier counters stayed clean: `23` runs, average reviewed calls `5.57`, average/max truncated calls `0/0`, added/dropped `11/0`, retention mismatches `0`.

Evidence reviewed:
- The `11:42:44Z` concrete-location guard was behaviorally disproven almost immediately. Row `29695`, post-boundary, ran sibling merge repair and merged duplicate incidents `3856`, `3858`, and `3857` into stale lift-assist incident `3854`.
- The saved incident `3854` now contains four unrelated calls: `791099` lift assist at `8227 Trubidor Way`; `791155` unresponsive three-year-old at `5911 Snow Hill Road / Life Care`; `791343` unconscious 91-year-old at likely `1302/302 Phyllis Lane`; and `791361` unconscious male at likely `1302 Fillers/Phyllis Lane`.
- Root cause: the first location guard used only strong membership anchors. Transcript address anchors are stored as weak hints and were intentionally excluded from positive membership evidence, so the sibling repair path still had no hard conflict signal for these stale or single-call incidents.
- Positive signal from the same window: rows `29685` and `29686` updated `3854` to retain only `791099` and exclude verifier-rejected `791155` before the later sibling repair reintroduced unrelated calls. That means normal membership validation can prune correctly; the remaining bad path was duplicate-incident repair.
- Row `29687` rejected a broad update to heart-problem incident `3855` because only one call supported the concrete location while other calls cited different locations. This is the desired rejection behavior.
- Rows `29689` through `29694` created standalone single-call incidents for the unresponsive-child and likely Phyllis/Fillers Lane unconscious-patient calls. The issue was not standalone recovery; it was later sibling merge repair unioning those incidents into `3854`.

Action taken:
- Updated `AutomaticInsightsService` only.
- Sibling merge repair now uses a separate weak-location conflict collector for the repair path. Weak transcript address, intersection, landmark, and location hints can block unsafe sibling repair, but they still do not become positive membership evidence elsewhere.
- Added an internal target conflict check before sibling repair. If an existing target incident already contains incompatible concrete location anchors across different calls, it cannot absorb more sibling incidents unless a strong police BOLO identity link applies.
- Full solution test suite passed before deploy: `116/116`.
- Deployed through `.\scripts\deploy_pizzad_tar.ps1 -HostName lilhoser@192.168.1.173 -Rid linux-x64` at `2026-06-18T11:58:36Z`.
- Post-deploy verification: health OK, `pizzad` active since `2026-06-18 07:58:36 EDT`, `0` restarts, queue pressure false, embedding endpoint OK, Qdrant OK, embedding queue `0`, failed embeddings `0`.
- Automation boundary updated to `2026-06-18T11:58:36Z`.

Residual risk:
- Existing incident `3854` remains stale and now contains `791155`, `791343`, and `791361` in addition to the original `791099`, because this bake is not allowed to manually repair production incidents or rerun backfills.
- The `11:58:36Z` sibling weak-location guard is service-verified but not behaviorally proven yet. Next runs should confirm that `3854` does not absorb more sibling incidents and that similar multi-location sibling repairs are blocked.
- Watch for true duplicate siblings left unmerged when weak transcript address hints differ because of transcription noise. This tradeoff is deliberate for sibling repair: leaving a duplicate is safer than merging distinct locations into one incident.

### 2026-06-18 12:10Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T12:10Z`; rows before the `2026-06-18T11:58:36Z` weak-location sibling guard are context unless needed for comparison.

Observed:
- Services are healthy: health OK, queue depth `0`, pending transcriptions `0`, pending audio `0s`, and queue pressure false.
- `pizzad` is active since `2026-06-18 07:58:36 EDT`, with `0` restarts after the latest deployment.
- Embeddings remain healthy: endpoint OK, Qdrant OK, queue `0`, pending `0`, failed `0`.
- Last-hour calls: `341` total, with `307` OK transcripts, `20` repetitive, `7` too short, `6` empty, and `1` numeric-noise.
- Throughput is healthy: ingest about `8.0 calls/min`, transcribe about `8.1 calls/min`, average transcription realtime factor about `0.111`.
- AI routing is clean: `32` requests, `32` successes, `0` failures, `0` truncations.
- Evidence verifier counters remain clean: `20` runs, average reviewed calls `6.0`, average/max truncated calls `0/0`, retention mismatches `0`.

Evidence reviewed:
- Post-`11:58:36Z` rows are sparse: row `29696` rejected weak call `791537`, and row `29697` created single-call MVC incident `3859`. No post-boundary sibling-merge repair occurred.
- Incident `3859`, `MVC at 300 Aux Highway, BMW vs tour bus`, is supported by retained call `791701`: transcript says crash, negative injuries, `300 Aux Highway`, BMW versus tour bus. The title and detail are grounded enough for a single-call traffic incident.
- Rejected call `791537` is a police dispatch at `2708 Taylor Street` for a female with dementia who walked off and returned. It may be a welfare-check-adjacent or missing/wandered-person taxonomy watch item, but the transcript also includes cancellation and weak event framing. One example is not enough to broaden acceptance.
- Stale incident `3854` still contains unrelated calls `791155`, `791343`, and `791361` from before the latest guard. No manual repair or backfill was performed.

Action taken:
- No code change and no deployment in this run.
- Watchlist updated with the clean post-boundary signal and the new watch-only dementia/walked-off/cancelled-call example.
- Automation boundary remains `2026-06-18T11:58:36Z`.

Residual risk:
- The `11:58:36Z` weak-location sibling guard still needs a real post-boundary sibling-repair attempt to prove it blocks conflicting-location merges.
- Watch for repeated concrete dementia, wandering, or returned-person welfare calls being rejected as weak before considering any taxonomy change. Avoid accepting cancellation-only or resolved status calls without an active welfare concern.
- Stale bad membership on `3854` remains in the final report as a historical artifact.

### 2026-06-18 12:25Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T12:25Z`; rows before the `2026-06-18T11:58:36Z` weak-location sibling guard are context unless needed for comparison. Deployment boundary changed during this run to `2026-06-18T12:29:07Z`.

Observed:
- Pre-change telemetry was healthy: health OK, queue depth `0`, queue pressure false, pending transcriptions `0`, pending audio `0s`, Qdrant OK, embedding endpoint OK, embedding queue `0`, pending embeddings `0`, failed embeddings `0`.
- Last-hour quality was stable: `429` calls, `382` OK transcripts, `32` repetitive, `8` too short, `6` empty, and `1` numeric-noise.
- AI routing stayed clean: `35` requests, `35` successes, `0` failures, `0` truncations.
- Evidence verifier counters stayed clean: `21` runs, average reviewed calls about `6.0`, average/max truncated calls `0/0`, retention mismatches `0`.
- Post-deploy verification: `pizzad` active since `2026-06-18 08:29:07 EDT`, `0` restarts, health OK, queue pressure false, Qdrant OK, embedding endpoint OK, embedding queue `0`, failed embeddings `0`.

Evidence reviewed:
- Row `29703` rejected a five-call Soddy Daisy accident-with-injuries event at Green Pond Road because final membership validation saw conflicting concrete locations without a shared location anchor. Transcript review showed the retained calls were the same rollover and power-lines-down event: fire and police calls said `1138 Green Pond/Greenpond Road`, while an EMS call said `1188 Green Pond Road 1138 Green Pond Road`. The failure mode was not a legitimate separate-location conflict; it was transcript spacing and corrected-address wording causing `Greenpond` and `Green Pond` to miss location compatibility.
- Row `29704` created incident `3860`, `MVC I-75 South at mile marker 20 involving Kia and Ultima`, with calls `791977`, `792105`, `792139`, `792145`, and `792167`. Transcript review supports the call set: I-75 south near mile marker `20`/`20.6`, Kia versus blue Ultima/Altima wording, airbag impact, back-pain patients, and exit-ramp continuation.
- Rows `29698`, `29700`, `29701`, and `29702` were acceptable weak or routine single-call rejects on review. Row `29699` rejected two THP D6 off-road/ditch calls with weak location and damage evidence; keep watch, but this was not strong enough for a general taxonomy change.

Action taken:
- Updated `IncidentCandidateValidator` only.
- Concrete location compatibility now also compares compacted location parts with punctuation and spaces removed. This keeps the existing concrete-location conflict guard intact while allowing transcript variants like `Greenpond Road` and `Green Pond Road` to share a location anchor after house numbers are stripped.
- No new tests were added. Existing Release test suite passed: `116/116`.
- Deployed through `.\scripts\deploy_pizzad_tar.ps1 -HostName lilhoser@192.168.1.173 -Rid linux-x64`.
- Automation boundary updated to `2026-06-18T12:29:07Z`.

Residual risk:
- The Green Pond incident was rejected before the deployment and was not manually repaired or backfilled.
- The compact-location compatibility change needs live proof on a future same-road spacing variant. Watch for false joins between distinct events on similarly named roads, though the existing event, time, verifier, and final membership checks still apply.
- The `11:58:36Z` weak-location sibling guard remains behaviorally unproven because no post-boundary sibling repair occurred before the `12:29:07Z` deployment.

### 2026-06-18 12:40Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T12:40Z`; rows before the `2026-06-18T12:29:07Z` compact-location deployment are context unless needed for comparison. Deployment boundary changed during this run to `2026-06-18T12:51:04Z`.

Observed:
- Services were healthy before changes: health OK, queue pressure false, pending audio `0s`, and no work-blocked or AI-blocked reason.
- `pizzad` was active since `2026-06-18 08:29:07 EDT` with `0` restarts before the first change in this run.
- Embeddings and Qdrant were healthy: endpoint OK, Qdrant OK, embedding queue `0`, pending `0`, failed `0`.
- Last-hour quality stayed stable: `440` calls, `394` OK transcripts plus `2` additional OK rows, `31` repetitive, `6` too short, `5` empty, and `2` numeric-noise.
- AI routing stayed clean: `33` requests, `33` successes, `0` failures, `0` truncations.
- Evidence verifier stayed clean: `19` runs, average reviewed calls about `6.3`, average/max truncated calls `0/0`, retention mismatches `0`.
- Post-deploy verification after the final build: `pizzad` active since `2026-06-18 08:51:04 EDT`, `0` restarts, health OK, queue depth `0`, pending audio `0s`, queue pressure false, Qdrant OK, embedding endpoint OK, embedding queue `0`, failed embeddings `0`.

Evidence reviewed:
- Rows `29705` and `29706` proved the `12:29:07Z` compact-location fix. The previously rejected Green Pond rollover became incident `3861`, `MVC with injuries at 1138 Greenpond Rd, white sedan in ditch`, retaining calls `792027`, `792047`, `792101`, `792123`, and `792285`.
- Row `29708` still rejected a same-event I-75 MVC update because call `792061` had bad extracted location text `88 on the left ln` from `10-88 on the left lane`. That false address-like anchor created a hard concrete-location conflict.
- Rows `29710` and `29711` then accepted an update to incident `3860`, but the current saved membership became `792061`, `792139`, `792145`, `792167`, and `792191`. It dropped original title-supporting calls `791977` and `792105`, leaving a stale title/detail that still says Kia/Ultima while the retained set now emphasizes lane movement, ambulance request, back-pain patients, and a Tesla-related follow-up.
- The stale membership exposed a second invariant failure: transcripts such as `I-75 South at the 20.6` and `I-75 south of the 20` were not consistently becoming highway-mile-marker anchors, while roadway instruction fragments such as `exit 11 now on Lee Highway` could become fake address anchors in existing fixtures.

Action taken:
- Updated `TranscriptLocationService`, `CallAnchorExtractionService`, `IncidentCandidateValidator`, and `AutomaticInsightsService`.
- Location plausibility and final membership filtering now reject traffic-lane and roadway-instruction fragments such as `88 on the left lane` or `11 now on the Lee Highway` as concrete location anchors, including stored weak location rows.
- Highway marker extraction now supports route-first phrasing such as `I-75 South at the 20.6` and `I-75 south of the 20`, producing rounded companion anchors where applicable.
- No new tests were added. Existing Release test suite passed after correction: `116/116`.
- Deployed through `.\scripts\deploy_pizzad_tar.ps1 -HostName lilhoser@192.168.1.173 -Rid linux-x64`.
- Automation boundary updated to `2026-06-18T12:51:04Z`.

Residual risk:
- Incident `3860` currently has stale bad membership from rows `29710` and `29711`; no manual repair or backfill was performed.
- The `12:51:04Z` build is service-verified but not behaviorally proven yet. Next runs should confirm that a normal live update restores or stops worsening I-75 MVC membership and that lane or exit-instruction fragments no longer create hard location conflicts.
- Watch for true addresses with unusual names being filtered by the new roadway-instruction guard, though the filter is limited to numeric fragments followed by lane-position or `now on` / `off` road-instruction wording.

### 2026-06-18 12:55Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T12:55Z`; rows before the `2026-06-18T12:51:04Z` roadway-fragment and route-first marker deployment are context unless needed for comparison. Deployment boundary remains `2026-06-18T12:51:04Z`.

Observed:
- Health stayed clean: `/health` returned OK, `pizzad` was active since `2026-06-18 08:51:04 EDT` with `0` restarts, queue depth `0`, queue pressure false, pending transcriptions `0`, pending audio `0s`, and no AI blocked reason.
- Last-hour quality remained stable: `476` calls, `431` complete OK transcripts, `31` repetitive, `5` empty, `4` too short, `3` numeric-noise, and `3` pending.
- AI routing and verifier health remained clean: `33` AI requests, `33` successes, `0` failures, `0` truncations; verifier ran `19` times with average/max truncated calls `0/0` and retention mismatches `0`.
- Incident volume did not collapse: `3` creates, `4` updates, and `13` rejects in the last hour. Recent saved incidents remained `3861` and `3860`.

Evidence reviewed:
- Post-boundary row `29716` updated Green Pond incident `3861` with the same supported five-call set: `792027`, `792047`, `792101`, `792123`, and `792285`.
- Post-boundary row `29717` rejected a mixed Green Pond candidate containing the same already-owned `3861` calls plus weak call `792331`; this is an acceptable duplicate or weak-leftover rejection rather than a regression.
- Incident `3860` remains stale with calls `792061`, `792139`, `792145`, `792167`, and `792191`, still missing earlier title-supporting calls `791977` and `792105`. No manual repair or backfill was performed, and there has not yet been a normal post-`12:51:04Z` update proving restoration.
- Stale internally conflicting incident `3854` still contains `791099`, `791155`, `791343`, and `791361`, but no new sibling absorption was seen in this narrow post-boundary window.

Action taken:
- No code change and no deployment in this run.
- Watchlist updated with post-boundary health, Green Pond stability, and the still-unproven I-75 restoration risk.
- Automation boundary remains `2026-06-18T12:51:04Z`.

Residual risk:
- The `12:51:04Z` roadway-fragment and route-first highway-marker fix is service-verified but still needs live behavioral proof on incident `3860` or a similar I-75 marker event.
- Incident `3860` and stale incident `3854` remain historical bad memberships until normal live processing changes them; direct repair is outside bake authority.
- Continue watching for false filtering of real unusual road names and for false joins between similarly named routes after compact location matching.

### 2026-06-18 13:10Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T13:10Z`; rows before the `2026-06-18T12:51:04Z` roadway-fragment and route-first marker deployment are context unless needed for comparison. Deployment boundary changed during this run to `2026-06-18T13:14:47Z`.

Observed:
- Health stayed clean before and after the change: `/health` returned OK, queue depth `0`, queue pressure false, pending transcriptions `0`, pending audio `0s`, and no AI blocked reason.
- `pizzad` was active since `2026-06-18 08:51:04 EDT` with `0` restarts before the change, then active since `2026-06-18 09:14:47 EDT` with `0` restarts after deployment.
- Last-hour quality stayed acceptable: `465` calls, `422` complete OK transcripts, `34` repetitive, `4` numeric-noise, `3` too short, and `2` empty.
- AI routing stayed clean: `36` requests, `36` successes, `0` failures, `0` endpoint truncations before the change; post-deploy summary was `35/35` successes.
- Verifier retention mismatches stayed `0`, but verifier truncation reappeared on large candidates: average truncated calls about `0.86`, max `9`. This is now a watch item for the expanded I-75 candidate set.

Evidence reviewed:
- Rows `29718` and `29719` merged sibling candidates into the I-75 MVC event through shared strong anchors and time proximity. Candidate titles included vehicle or traffic-stop investigation wording near I-75 mile marker 20.
- Rows `29720` and `29721` updated incident `3860` to a fourteen-call set and restored original title-supporting call `791977`, but dropped title-supporting call `792105` as weak or unrelated. Call `792105` says a blue Ultima, a `52`-year-old white female, and airbag impact; that is same-event vehicle and patient detail for the retained Kia/Ultima crash, not a separate event.
- Rows `29728` and `29729` later removed the false lane-fragment call `792061` as verifier-rejected and rejected weak call `792793`, so the `12:51:04Z` roadway-fragment fix started to clean up the specific false-location failure. However, `792105` was still absent from saved incident `3860`.
- Current incident `3860` retained calls `791977`, `792139`, `792145`, `792167`, `792191`, `792233`, `792455`, `792459`, `792517`, `792583`, `792607`, `792631`, `792635`, and `792657`. The set includes likely same-scene vehicle and tag follow-up calls, but still needs watch for administrative or traffic-stop detail being retained beyond the supported crash event.
- Green Pond incident `3861` stayed stable on calls `792027`, `792047`, `792101`, `792123`, and `792285`. Stale incident `3854` did not expand in this window.

Action taken:
- Updated `AutomaticInsightsService`.
- Added a guarded same-talkgroup traffic-crash detail link. It only gives strong server-side membership credit when one close same-system, same-talkgroup call has primary crash scene text and a concrete road or marker anchor, the companion call has vehicle plus patient, injury, or airbag detail, the companion has no conflicting concrete location, and both calls share a distinctive traffic-crash identity token such as a vehicle model or other non-generic identifier.
- No new tests were added. Existing Release test suite passed: `116/116`.
- Deployed through `.\scripts\deploy_pizzad_tar.ps1 -HostName lilhoser@192.168.1.173 -Rid linux-x64`.
- Post-deploy verification: `pizzad` active, `0` restarts, health OK, queue depth `0`, queue pressure false, pending audio `0s`, AI successes `35/35`, retention mismatches `0`.
- Automation boundary updated to `2026-06-18T13:14:47Z`.

Residual risk:
- The new traffic-crash detail link is service-verified but not behaviorally proven. A normal live update still needs to show that `792105` or equivalent vehicle/patient/airbag detail is retained without admitting tag-only, tow-only, traffic-stop, pedestrian, or administrative calls.
- Incident `3860` remains historically stale until normal live processing updates it; no manual repair or backfill was performed.
- Verifier truncation is nonzero on the large I-75 candidate set. Watch whether truncation drops material evidence or lets over-broad membership survive.

### 2026-06-18 13:25Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T13:25Z`; rows before the `2026-06-18T13:14:47Z` traffic-crash detail deployment are context unless needed for comparison. Deployment boundary changed during this run to `2026-06-18T13:29:25Z`.

Observed:
- Health stayed clean before and after the change: `/health` returned OK, queue depth `0`, queue pressure false, pending transcriptions `0`, pending audio `0s`, and no AI blocked reason.
- `pizzad` was active since `2026-06-18 09:14:47 EDT` with `0` restarts before the change, then active since `2026-06-18 09:29:25 EDT` with `0` restarts after deployment.
- Last-hour quality remained acceptable before the change: `440` calls, `397` complete OK transcripts, `34` repetitive, `4` numeric-noise, `3` empty, and `2` too short.
- AI routing was clean before the change: `34` requests, `34` successes, `0` failures, `0` endpoint truncations. The post-deploy one-hour aggregate showed `33/34` successes with one failure at the deploy boundary; service health and queue state were clean afterward, so this is watch-only unless it repeats.
- Evidence verifier still had `0` retention mismatches, but large-candidate verifier truncation persisted: average truncated calls about `1.48`, max `9`.

Evidence reviewed:
- Rows `29730` and `29731` kept Green Pond incident `3861` stable with calls `792027`, `792047`, `792101`, `792123`, and `792285`.
- Rows `29733` through `29735` updated I-75 incident `3860` after the `13:14:47Z` traffic-crash detail deployment. Normal live processing still did not restore call `792105`, and the incident currently retains `791977`, `792139`, `792145`, `792167`, `792181`, `792191`, `792233`, `792455`, `792459`, `792517`, `792583`, `792607`, `792631`, `792635`, and `792657`. Call `792181` is supported additional-patient crash context, but `792105` remains unproven because the later candidate did not include it.
- Row `29732` rejected calls `792973`, `793005`, and `793011` as a concrete-location conflict. Transcript review showed a likely true same-event unconscious-person call: fire dispatch to `4830 Highway 58, apartment 118`, police detail for a male in his 40s in a Nissan slumped over the steering wheel near noisy `848-30 / 30 Highway 58` wording, and EMS detail for `118 between Oakwood Drive and Goldling Way` with the same unconscious male in a Nissan. The failure was a general location-normalization issue: an apartment or unit/detail fragment became a hard concrete location, and route-number comparison discarded `58` when stripping house numbers.
- Row `29736` was a noisy/routine single-call reject on review.
- Stale internally conflicting incident `3854` did not expand in this window.

Action taken:
- Updated `TranscriptLocationService` and `IncidentCandidateValidator`.
- Numeric relation-to-road fragments such as `118 between Oakwood Drive`, `120 near Main Street`, or similar `number + between/near/around/by + road` wording are no longer treated as plausible concrete locations. This keeps apartment, unit, and relative-location details from creating hard final-membership conflicts.
- Concrete-location comparison now strips only a leading house number and preserves route numbers. This allows noisy variants such as `4830 Highway 58` and `30 Highway 58` to share the `Highway 58` route context instead of losing all numeric road identity.
- No new tests were added. Existing Release test suite passed: `116/116`.
- Deployed through `.\scripts\deploy_pizzad_tar.ps1 -HostName lilhoser@192.168.1.173 -Rid linux-x64`.
- Post-deploy verification: `pizzad` active, `0` restarts, health OK, queue depth `0`, queue pressure false, pending audio `0s`, no AI blocked reason, retention mismatches `0`.
- Automation boundary updated to `2026-06-18T13:29:25Z`.

Residual risk:
- The `13:29:25Z` location-detail and route-number compatibility fix is service-verified but not behaviorally proven. A normal live update still needs to show the row-`29732` class can create or update without hard location conflict.
- Preserving route numbers after stripping house numbers can make different events on the same numbered route look more compatible. Existing event, time, verifier, and membership checks still apply, but watch same-route false joins closely.
- Watch for real numbered place names near cross streets being over-filtered by the new relation-to-road fragment guard.
- The `13:14:47Z` traffic-crash detail link remains unproven for `792105`, and verifier truncation remains nonzero on large I-75 candidates.

### 2026-06-18 13:40Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T13:40Z`; rows before the `2026-06-18T13:29:25Z` location-detail and route-number deployment are context unless needed for comparison. Deployment boundary changed during this run to `2026-06-18T13:44:08Z`.

Observed:
- Health stayed clean before and after the change: `/health` returned OK, queue depth `0`, queue pressure false, pending transcriptions `0`, pending audio `0s`, and no AI blocked reason.
- `pizzad` was active since `2026-06-18 09:29:25 EDT` with `0` restarts before the change, then active since `2026-06-18 09:44:08 EDT` with `0` restarts after deployment.
- Last-hour quality after deployment remained acceptable: `414` calls, `377` complete OK transcripts, `29` repetitive, `4` too short, `2` empty, and `2` numeric-noise.
- The post-deploy one-hour AI aggregate included `31` successes out of `33` requests, `2` failures, and `0` truncations; the latest failure timestamp matched the deploy restart second, so this is watch-only unless it repeats.
- Verifier retention mismatches stayed `0`, but large-candidate verifier truncation persisted: average truncated calls about `1.89`, max `9`.

Evidence reviewed:
- Post-boundary row `29737` rejected single call `793201` as weak even though the transcript contained a concrete police dispatch near `134 North Market Street` and an Amazon or box-truck driver who `hit their fence`. The existing vehicle-hit-fixed-object taxonomy matched `hit a fence` or `hit the fence` style wording but missed possessive determiners, so a concrete fixed-object collision report was under-classified.
- Row `29738` rejected calls `793219` and `793229` as medical transport without parent emergency evidence. Transcript review showed a repeated address and a noisy cancellation for `difficulty reading` or likely `difficulty breathing`; there was not enough non-cancelled emergency evidence for a safe taxonomy change.
- Rows `29739` and `29740` updated I-75 incident `3860` to a cleaner thirteen-call set and removed previously retained calls `792455` and `792459`. The incident still lacks earlier same-event detail call `792105`, but that call was not present in the later candidate set, so the `13:14:47Z` traffic-crash detail link remains unproven rather than disproven.
- Row `29741` rejected a routine or administrative rollup candidate; transcript review did not show a shared concrete incident anchor.
- Row `29742` created a single-call non-emergent fire manpower request at `3730 Fagan Street`. This is consistent with prior lift-assist or manpower incident handling and did not show a false join.
- Green Pond incident `3861` stayed stable on its five-call set, and stale internally conflicting incident `3854` did not absorb more siblings in this window.

Action taken:
- Updated `IncidentCandidateValidator` and `IncidentNarrativeGrounder`.
- Vehicle-hit-fixed-object evidence now accepts possessive determiners before the fixed object, such as `hit their fence`, `hit his pole`, or `hit her wall`, while keeping the existing requirement for a fixed object term.
- Title grounding now has a specific `Vehicle hit fixed object` fallback for the same possessive fixed-object wording instead of falling back to a generic or unsupported title.
- No new tests were added. Existing Release test suite passed: `116/116`.
- Deployed through `.\scripts\deploy_pizzad_tar.ps1 -HostName lilhoser@192.168.1.173 -Rid linux-x64`.
- Post-deploy verification: `pizzad` active, `0` restarts, health OK, queue depth `0`, queue pressure false, pending audio `0s`, embeddings and Qdrant OK.
- Automation boundary updated to `2026-06-18T13:44:08Z`.

Residual risk:
- The `13:44:08Z` possessive fixed-object wording fix is service-verified but not behaviorally proven. A future row-`29737` class report still needs to create or update through normal live processing.
- Watch for false single-call fixed-object incidents from non-emergency property-damage chatter, although the fixed-object taxonomy still requires concrete collision wording and the normal anchor and noise checks still apply.
- The I-75 `3860` traffic-crash detail fix remains unproven for call `792105`, and verifier truncation remains nonzero on large candidate sets.

### 2026-06-18 13:55Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T13:55Z`; rows before the `2026-06-18T13:44:08Z` possessive fixed-object deployment are context unless needed for comparison. Deployment boundary changed during this run to `2026-06-18T13:59:56Z`.

Observed:
- Health stayed clean before and after the change: `/health` returned OK, queue depth `0`, queue pressure false, pending transcriptions fell from `1` to `0`, pending audio fell from `15s` to `0s`, and no AI blocked reason appeared.
- `pizzad` was active since `2026-06-18 09:44:08 EDT` with `0` restarts before the change, then active since `2026-06-18 09:59:56 EDT` with `0` restarts after deployment.
- Runtime after deployment showed embeddings enabled, Qdrant OK, embedding endpoint OK, embedding queue `0`, failed embeddings `0`, and pending embeddings `0`.
- Last-hour quality before the change was stable: `434` calls, `392` complete OK transcripts, `29` repetitive, `6` too short, `5` empty, `1` numeric-noise, and `1` pending.
- AI routing stayed serviceable but still reflected the deploy-boundary failures from the prior restart: `31` successes out of `33` requests, `2` failures, and `0` truncations before this change.
- Verifier retention mismatches stayed `0`, but large-candidate truncation remained nonzero: average truncated calls about `2.29`, max `9`.

Evidence reviewed:
- Row `29747` and row `29748` proved the `13:29:25Z` location-detail and route-number fix on the row-`29732` class. The previously rejected unconscious-male event around Highway 58 created as incident `3866` with calls `793005` and `793011`, despite the noisy `118 between Oakwood Drive` location text.
- Row `29749` was a high-confidence sibling-merge regression. Server-side sibling repair merged duplicate incidents `3865` and `3863` into fall incident `3864`, leaving current incident `3864` with unrelated medical calls: `792745` at `82820 E 26th Street` for a low fall, `792863` with a fall or diabetic clear, `792947` at `18 W 28th Street` for a diabetic emergency, and `793117` at `3730 Fagan Street` for a diabetic response. These are separate dispatch locations and separate medical events.
- Rows `29750` and `29751` showed normal validation recognized the conflict on a later update, but the already-merged incident `3864` remained stale with the unrelated calls. No manual repair or backfill was performed.
- Rows `29753` and `29754` pruned I-75 incident `3860` down to calls `791977`, `792139`, `792181`, `792607`, and `792631`. This removed much of the earlier over-broad membership, but it also dropped likely same-event continuation calls `792145`, `792167`, and `792233` while still retaining administrative/tag-like calls `792607` and `792631`. This is a watch item, not a new code change in this run.
- Row `29752` rejected a single-call candidate lacking a concrete structured anchor or location hint; no safe taxonomy change was supported by that row.

Action taken:
- Updated `AutomaticInsightsService`.
- Sibling-incident merge repair now runs the final server-owned membership validation over the entire proposed union before persisting the repair. If final validation rejects the union or would prune any merged call, repair is refused instead of writing a sibling merge.
- The existing pairwise merge guards remain in place; this adds a final safety gate for cases where weak transcript-derived locations or later validator logic can prove the union is internally inconsistent.
- No new tests were added. Existing Release test suite passed: `116/116`.
- Deployed through `.\scripts\deploy_pizzad_tar.ps1 -HostName lilhoser@192.168.1.173 -Rid linux-x64`.
- Post-deploy verification: `pizzad` active, `0` restarts, health OK, queue depth `0`, queue pressure false, pending audio `0s`, embeddings and Qdrant OK.
- Automation boundary updated to `2026-06-18T13:59:56Z`.

Residual risk:
- The `13:59:56Z` sibling-repair final-validation gate is service-verified but not behaviorally proven. The next runs should show sibling repair refusing unions like row `29749`, or true duplicate repair proceeding only when the full union remains valid.
- Stale bad membership remains on incident `3864` until normal live processing rewrites it; direct repair is outside bake authority.
- The stricter repair gate may leave some true duplicate sibling incidents unmerged when a call lacks enough structured support. That is preferable to unioning conflicting incidents, but it needs monitoring.
- I-75 incident `3860` is still mixed: current membership is smaller and cleaner than the earlier large set, but it still misses likely same-event continuation and still retains some administrative/tag-like calls.

### 2026-06-18 14:10Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T14:10Z`; rows before the `2026-06-18T13:59:56Z` sibling-repair final-validation deployment are context unless needed for comparison. Deployment boundary remains `2026-06-18T13:59:56Z`.

Observed:
- Health was clean: `/health` returned OK, queue depth `0`, queue pressure false, no AI blocked reason, and runtime later showed pending transcriptions `0` with pending audio `0s`.
- `pizzad` was active since `2026-06-18 09:59:56 EDT` with `0` restarts.
- Runtime showed embeddings enabled, Qdrant OK, embedding endpoint OK, embedding queue `0`, failed embeddings `0`, and pending embeddings `0`.
- Last-hour quality was acceptable: `463` calls, `415` complete OK transcripts, `28` repetitive, `10` empty, `7` too short, `1` numeric-noise, `1` overexpanded, and `1` pending at the quality-check sample time.
- The last-hour quality aggregate still showed `31` successes out of `33` AI requests, `2` failures, and `0` endpoint truncations, but direct `lm_usage` rows after `2026-06-18T13:59:56Z` showed only successful automatic-insights calls. Two successful rows required `recovered_after_truncation_split`; watch only unless recovery fails or `finish_reason=length` returns.
- Evidence verifier retention mismatches stayed `0`. Large-candidate verifier truncation remained nonzero: average truncated calls about `1.50`, max `7`.

Evidence reviewed:
- Rows `29755` and `29756` created and updated route-address unconscious-male incident `3866` with calls `792973`, `793005`, and `793011`, while excluding weak call `792993`. This is continued positive evidence for the `13:29:25Z` relation-to-road and route-number compatibility fix.
- Row `29757` rejected call `793585` as routine status. Transcript review showed an EMS test or transfer-style exchange on Oakland Drive, so no incident-quality change is supported.
- Row `29758` rejected call `793609` as weak. Transcript review showed a single police call where the caller wanted to speak to an officer about a pit bull killing cats at `265 Lower East Street NE`. It may be a local service request, but it does not support a broad emergency-event taxonomy change.
- There were no post-boundary `server sibling merge repair` rows, so the `13:59:56Z` final-validation gate has not yet been behaviorally proven or disproven.
- Incident `3864` still contains stale bad membership with calls `792745`, `792863`, `792947`, and `793117`; it did not absorb more siblings in this sample.
- Incident `3860` remains stale and mixed with calls `791977`, `792139`, `792181`, `792607`, and `792631`; no normal live update in this sample proved the traffic-crash detail linkage for call `792105`.

Action taken:
- No code change and no deploy. The post-boundary evidence did not show a new high-confidence invariant failure.
- Updated this watchlist and final report draft only.

Residual risk:
- The sibling-repair final-validation gate is still pending live proof because no sibling repair was attempted after the boundary.
- Stale bad membership remains on incidents `3864` and `3860`; no direct production repair or backfill was performed.
- Call `792993` looks plausibly related to incident `3866` but was excluded as weak. Because the incident still retained three stronger calls and the dropped call mostly repeats vehicle/patient context, this is a watch item rather than enough evidence for another membership-link change.
- Verifier truncation is lower than the prior I-75 spike but still nonzero on larger candidates.

### 2026-06-18 14:25Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T14:25Z`; rows before the `2026-06-18T13:59:56Z` sibling-repair final-validation deployment are context unless needed for comparison. Deployment boundary changed during this run to `2026-06-18T14:28:23Z`.

Observed:
- Health was clean before the change: `/health` returned OK, queue depth `0`, queue pressure false, pending transcriptions `0`, pending audio `0s`, and no AI blocked reason.
- `pizzad` was active since `2026-06-18 09:59:56 EDT` with `0` restarts before the change.
- Runtime before the change showed embeddings enabled, Qdrant OK, embedding endpoint OK, embedding queue `0`, failed embeddings `0`, and pending embeddings `1`.
- Last-hour quality before the change was acceptable: `456` calls, `412` complete OK transcripts, `25` repetitive, `8` empty, `8` too short, `1` numeric-noise, `1` overexpanded, and `1` pending.
- The one-hour quality aggregate still showed `34` successes out of `36` AI requests, `2` failures, and `0` endpoint truncations, but direct `lm_usage` rows after `2026-06-18T14:10Z` showed only successful automatic-insights calls. Two rows used `recovered_after_truncation_split`; watch only unless recovery fails or `finish_reason=length` returns.
- Evidence verifier retention mismatches stayed `0`. Large-candidate verifier truncation improved but remained nonzero: average truncated calls about `0.71`, max `5`.

Evidence reviewed:
- Rows `29759` and `29760` continued to update route-address unconscious-male incident `3866` with calls `792973`, `793005`, and `793011`, again excluding weaker vehicle-context call `792993`.
- Rows `29761` and `29762` created single-call incident `3867` for call `793033`, an EMS dispatch to `6101 Lee Highway` / Sam's Club for a female in her 30s with heart problems. This is a supported single-call medical event.
- Rows `29763`, `29767`, and `29768` rejected low-similarity or weak-location mixed candidates; transcript review did not show a shared real-world event that justified a broad membership change.
- Rows `29764` and `29765` rejected routine or weak Cleveland calls; transcript review did not show enough concrete incident evidence for a safe taxonomy change.
- Row `29769` was a high-confidence single-call rejection bug. Call `793877` was a welfare check at `1 Covenant Drive NE` for a little girl screaming and possibly in distress, but final validation rejected it as noisy or routine. The failure was not the welfare-check taxonomy itself; the single-call routine filter ran before the strong incident and location checks, saw routine radio wording such as `copy`, and did not see a deterministic transcript anchor for the spoken-number address.
- There were still no post-boundary `server sibling merge repair` rows, so the `13:59:56Z` final-validation gate remains unproven.
- Incidents `3864` and `3860` stayed stale; neither absorbed additional siblings in this sample.

Action taken:
- Updated `IncidentCandidateValidator`.
- The single-call path now separates truly noisy text from routine/status wording. Noisy text is still rejected early, but routine words alone no longer suppress a retained single-call candidate when the call has both a strong incident signal and a concrete anchor or location hint.
- This generalizes beyond the row-`29769` welfare-check case: routine words such as `copy`, `clear`, `go ahead`, or `on scene` no longer override otherwise concrete single-call emergency or public-safety dispatch evidence.
- No new tests were added. Existing Release test suite passed: `116/116`.
- Deployed through `.\scripts\deploy_pizzad_tar.ps1 -HostName lilhoser@192.168.1.173 -Rid linux-x64`.
- Post-deploy verification: `pizzad` active since `2026-06-18 10:28:23 EDT`, `0` restarts, health OK, queue pressure false, no AI blocked reason, embeddings enabled, Qdrant OK, embedding endpoint OK, embedding queue `0`, failed embeddings `0`, and pending embeddings `0`. A small transient transcription backlog existed immediately after restart, but it was not under pressure.
- Automation boundary updated to `2026-06-18T14:28:23Z`.

Residual risk:
- The `14:28:23Z` routine-word refinement is service-verified but not behaviorally proven. A normal live update still needs to show a row-`29769` class welfare check or equivalent concrete single-call dispatch is accepted.
- Watch for false single-call incidents where routine radio or status chatter contains an incidental strong-event word plus weak location text. The concrete anchor or location-hint requirement remains in place, but this branch is broader than the prior early routine reject.
- The `13:59:56Z` sibling-repair final-validation gate remains unproven because no post-boundary sibling repair has exercised it.
- Stale bad membership remains on incidents `3864` and `3860`; no direct production repair or backfill was performed.

### 2026-06-18 14:40Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T14:40Z`; rows before the `2026-06-18T14:28:23Z` routine-word refinement deployment are context unless needed for comparison. Deployment boundary remains `2026-06-18T14:28:23Z`.

Observed:
- Health was clean: `/health` returned OK, queue depth `1`, queue pressure false, pending transcriptions `1`, pending audio `24s`, and no AI blocked reason. Runtime showed no live, priority, or backlog queue pressure.
- `pizzad` was active since `2026-06-18 10:28:23 EDT` with `0` restarts.
- Runtime showed embeddings enabled, Qdrant OK, embedding endpoint OK, embedding queue `0`, failed embeddings `0`, pending embeddings `0`, and no embedding error.
- Last-hour quality was acceptable: `507` calls, `458` complete OK transcripts, `28` repetitive, `9` empty, `9` too short, `1` numeric-noise, `1` overexpanded, and `1` pending.
- AI was healthy after the deploy: direct `lm_usage` rows after `2026-06-18T14:28:23Z` were all successful, with one `recovered_after_truncation_split` row and no `finish_reason=length`.
- Evidence verifier retention mismatches stayed `0`. Large-candidate verifier truncation continued to improve but remained nonzero: average truncated calls about `0.47`, max `3`.

Evidence reviewed:
- Rows `29770` and `29771` created fire-alarm incident `3868` with calls `793889`, `793939`, and `794043` around `418 McCauley Avenue`. A later update at rows `29774` through `29776` retained `793939` and `794043` while dropping initial dispatch call `793889`. The current title and detail are still supported by retained call `793939`.
- The dropped initial fire-alarm dispatch `793889` appears related to incident `3868`, but it was not enough evidence for a new code change because the incident stayed valid, the title/detail remained grounded, and recent sibling and final-membership fixes intentionally avoid preserving verifier-rejected calls without stronger proof.
- Rows `29772`, `29773`, and `29777` rejected single-call or traffic/admin candidates lacking a strong emergency/event signal. Transcript review supported these rejects: `793285` was a prior-day trespass phone-call request, `793271` was a slow tractor-trailer traffic observation, and `793609` was a caller wanting to speak to an officer about a pit bull killing cats.
- No post-boundary evidence showed the `14:28:23Z` routine-word refinement creating false single-call incidents. It also has not yet been positively proven by a row-`29769` class welfare-check acceptance.
- There were still no post-boundary `server sibling merge repair` rows, so the `13:59:56Z` final-validation gate remains unproven.
- Incidents `3864` and `3860` stayed stale but did not worsen in this sample.

Action taken:
- No code change and no deploy. The post-boundary evidence did not show a new invariant failure.
- Updated this watchlist and final report draft only.

Residual risk:
- The `14:28:23Z` routine-word refinement still needs positive live proof on a concrete welfare-check or similar single-call dispatch.
- Incident `3868` dropping related initial dispatch call `793889` is a watch item for legitimate existing-member pruning, but not enough by itself to weaken verifier-rejected-call handling.
- The `13:59:56Z` sibling-repair final-validation gate remains unproven because no post-boundary sibling repair has exercised it.
- Stale bad membership remains on incidents `3864` and `3860`; no direct production repair or backfill was performed.

### 2026-06-18 14:55Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T14:55Z`; rows before the `2026-06-18T14:28:23Z` routine-word refinement deployment are context unless needed for comparison. Deployment boundary changed during this run to `2026-06-18T14:58:48Z`.

Observed:
- Health was clean before and after the change: `/health` returned OK, queue depth `0`, queue pressure false, no AI blocked reason, and no pending audio. Runtime after deploy showed pending transcriptions `0` and pending audio `0s`.
- `pizzad` was active since `2026-06-18 10:28:23 EDT` with `0` restarts before the change, then active since `2026-06-18 10:58:48 EDT` with `0` restarts after deployment.
- Runtime after deploy showed embeddings enabled, Qdrant OK, embedding endpoint OK, embedding queue `0`, failed embeddings `0`, pending embeddings `0`, and no embedding error.
- Last-hour quality before the change was acceptable: `481` calls, `434` complete OK transcripts, `28` repetitive, `8` empty, `7` too short, `1` marker-only, `1` numeric-noise, `1` overexpanded, and `1` pending.
- AI was clean: `35/35` successes, `0` failures, `0` endpoint truncations, and direct `lm_usage` rows after the prior boundary all had `finish_reason=stop`.
- Evidence verifier retention mismatches stayed `0`. Large-candidate verifier truncation continued to improve: average truncated calls about `0.33`, max `3` before the change, and about `0.19`, max `2` immediately after deploy.

Evidence reviewed:
- Rows `29778` and `29779` restored the related initial fire-alarm dispatch call `793889` to incident `3868`, bringing the incident back to calls `793889`, `793939`, and `794043`. This resolved the watch-only pruning concern from the prior run through normal live processing.
- Row `29781` disproved the `14:28:23Z` routine-word refinement. Call `793877`, the `1 Covenant Drive NE` welfare check for a little girl screaming and possibly in distress, was still rejected as noisy or routine. Metadata showed a weak location row `s a one covenant dr ne`, and the call had a persisted `location_hint` anchor, but the validator still lacked a concrete address anchor because `one Covenant Drive` was not extracted as an address.
- The first attempted fix in this run was too broad: allowing any stored weak location hint to satisfy the single-call location gate caused the existing test `Validate_RejectsSingleCallWhenOnlyAnchorIsWeakLocationHint` to fail. That failure was correct because it would have accepted weak location text such as `Is There A Lane`.
- Rows `29780`, `29783`, and `29785` were reasonable rejects on transcript review: the candidates were service-call, traffic/status, or operational chatter without a strong emergency/event anchor.
- Row `29784` created lift-assist incident `3869` with calls `794255` and `794259`; transcript review showed a valid two-call fire/EMS response for a 69-year-old female unable to get up near McDaniel Road.
- There were still no post-boundary `server sibling merge repair` rows, so the `13:59:56Z` final-validation gate remains unproven.

Action taken:
- Updated `CallAnchorExtractionService`.
- The anchor extractor now recognizes spoken single-digit street addresses, such as `one Covenant Drive`, as deterministic weak address anchors. These anchors can satisfy validator membership checks that need an address or location hint.
- The rejected broader approach of treating all persisted weak `location_hint` rows as single-call location hints was not kept. Weak `location_hint` rows remain excluded from concrete conflict and merge decisions.
- No new tests were added. Existing Release test suite passed after the narrower fix: `116/116`.
- Deployed through `.\scripts\deploy_pizzad_tar.ps1 -HostName lilhoser@192.168.1.173 -Rid linux-x64`.
- Post-deploy verification: `pizzad` active, `0` restarts, health OK, queue depth `0`, queue pressure false, pending transcriptions `0`, pending audio `0s`, no AI blocked reason, embeddings enabled, Qdrant OK, embedding endpoint OK.
- Automation boundary updated to `2026-06-18T14:58:48Z`.

Residual risk:
- The `14:58:48Z` spoken single-digit address anchor fix is service-verified but not behaviorally proven. A normal live update still needs to show row-`29781` class welfare checks passing final validation.
- Watch for false single-call incidents caused by incidental phrases like `one ... drive` or `two ... road`. The regex requires a street-name token plus a road suffix, and the normal strong-event, noise, routine, and final-membership checks still apply.
- The `13:59:56Z` sibling-repair final-validation gate remains unproven because no post-boundary sibling repair has exercised it.
- Stale bad membership remains on incidents `3864` and `3860`; no direct production repair or backfill was performed.

### 2026-06-18 15:10Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T15:10Z`; rows before the `2026-06-18T14:58:48Z` spoken single-digit address-anchor deployment are context unless needed for comparison. Deployment boundary changed during this run to `2026-06-18T15:13:55Z`.

Observed:
- Health stayed clean before and after the change: `/health` returned OK, queue depth `0`, queue pressure false, pending transcriptions `0`, pending audio `0s`, and no AI blocked reason.
- `pizzad` was active since `2026-06-18 10:58:48 EDT` with `0` restarts before the change, then active since `2026-06-18 11:13:55 EDT` with `0` restarts after deployment.
- Runtime after deploy showed embeddings enabled, Qdrant OK, embedding endpoint OK, embedding queue `0`, failed embeddings `0`, pending embeddings `0`, and no embedding error.
- Last-hour quality remained acceptable: `476` calls, `429` complete OK transcripts, `31` repetitive, `9` too short, `6` empty, and `1` marker-only.
- AI was clean in the quality window: `33/33` successes, `0` failures, `0` endpoint truncations, and direct `lm_usage` rows after the prior boundary all had `finish_reason=stop`.
- Evidence verifier retention mismatches stayed `0`. Verifier truncation was still low but nonzero: average truncated calls about `0.18`, max `2`.

Evidence reviewed:
- Row `29786` accepted a normal update to lift-assist incident `3869` with calls `794255` and `794259`; no bad sibling repair or false continuation join appeared.
- Row `29787` rejected calls `794575`, `794581`, and `794597` as `unsupported narrative lacked a specific evidence-backed fallback`. Transcript review showed a coherent I-75 northbound traffic-crash event: `794575` reported another `1050` that rear-ended the other vehicle, `794581` gave I-75 northbound / Blue Compass access context, and `794597` gave `I-75 North` lane and response detail.
- The rejected event did not justify restoring bare `1050` as standalone traffic-crash evidence because the earlier numeric 10-code fix intentionally prevents address-like numbers from grounding traffic incidents. The invariant failure was narrower: `rear-ended` was not recognized as vehicle-accident evidence for validation, event class, category normalization, or title fallback.
- Incident `3868` stayed healthy after normal live processing restored initial fire-alarm call `793889`; incident `3869` remained grounded. Stale bad membership on incidents `3860` and `3864` did not worsen in this sample.
- The `14:58:48Z` spoken single-digit address-anchor fix was service-verified but still not behaviorally proven by a post-boundary welfare-check acceptance.

Action taken:
- Updated `IncidentCandidateValidator`, `IncidentNarrativeGrounder`, and `AutomaticInsightsService`.
- Added `rear-end`, `rear ended`, `rear-ended`, and `rear-ending` wording as traffic-crash / vehicle-accident evidence for validation, title fallback, event-class handling, and category normalization.
- Did not add bare `1050` as standalone evidence; separated forms such as explicit traffic 10-codes remain handled by the existing guarded path.
- No new tests were added. Existing Release test suite passed after the change: `116/116`.
- Deployed through `.\scripts\deploy_pizzad_tar.ps1 -HostName lilhoser@192.168.1.173 -Rid linux-x64`.
- Post-deploy verification: `pizzad` active, `0` restarts, health OK, queue depth `0`, queue pressure false, pending transcriptions `0`, pending audio `0s`, no AI blocked reason, embeddings enabled, Qdrant OK, embedding endpoint OK.
- Automation boundary updated to `2026-06-18T15:13:55Z`.

Residual risk:
- The rear-end crash wording fix is service-verified but not behaviorally proven by a live accepted row yet. Watch for a normal update creating or accepting the row-`29787` class event.
- Watch for false traffic incidents caused by incidental rear-end wording, especially without a crash scene, vehicle, patient, injury, lane, or highway context.
- The `14:58:48Z` spoken single-digit address-anchor fix still needs positive live proof on a row-`29781` class welfare-check dispatch.
- The `13:59:56Z` sibling-repair final-validation gate remains unproven because no post-boundary sibling repair has exercised it.
- Stale bad membership remains on incidents `3864` and `3860`; no direct production repair or backfill was performed.

### 2026-06-18 15:25Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T15:25Z`; rows before the `2026-06-18T15:13:55Z` rear-end crash wording deployment are context unless needed for comparison. Deployment boundary changed during this run to `2026-06-18T15:29:27Z`.

Observed:
- Health stayed acceptable: `/health` returned OK, queue pressure false, no AI blocked reason, and only a small live backlog immediately after deploy (`5` queue depth, `6` pending transcriptions, about `85s` pending audio).
- `pizzad` was active since `2026-06-18 11:13:55 EDT` with `0` restarts before the change, then active since `2026-06-18 11:29:27 EDT` with `0` restarts after deployment.
- Runtime after deploy showed embeddings enabled, Qdrant OK, embedding endpoint OK, embedding queue `0`, failed embeddings `0`, pending embeddings `0`, and no embedding error.
- Last-hour quality remained acceptable: `465` calls, `414` complete OK transcripts, `35` repetitive, `9` too short, `6` empty, and `1` marker-only.
- AI was clean in the quality window: `31/31` successes, `0` failures, `0` endpoint truncations, and latest AI work at `2026-06-18T15:28:36Z`.
- Evidence verifier retention mismatches stayed `0`. Verifier truncation was still low but nonzero: average truncated calls about `0.14`, max `2`.

Evidence reviewed:
- Row `29791`, after the `15:13:55Z` rear-end wording deployment, rejected a mixed traffic candidate with calls `794517` and `794615`. Transcript review showed `794615` was a different Highway 193 / Oliver Lane burnt-plastic type call, so rejecting the pair was correct.
- The same row exposed a valid leftover subset: call `794517` reported a motor vehicle accident with possible injury on I-75 North, involving an old Kia Optima / Tahoe and a female driver crying and unable to communicate. It had no persisted call anchor, so the mixed-candidate recovery path could not save it as a single-call traffic-crash event after excluding unrelated `794615`.
- The earlier row-`29787` rear-end calls `794575`, `794581`, and `794597` still had not been accepted by normal live processing at this checkpoint, so the `15:13:55Z` rear-end wording deployment remained service-verified but not behaviorally proven.
- Row `29792` created incident `3870`, `Unconscious person at 4616 Rossville Blvd between 46th and 47th St`, with calls `793965`, `794035`, and `794173`. Transcript review found a grounded unconscious-person dispatch plus police/EMS continuation evidence; the parent-evidence metadata had a stray fire-alarm phrase, but the saved title/detail did not assert an unsupported fire alarm.
- Rows `29788` and `29789` kept fire-alarm incident `3868` stable with calls `793889`, `793939`, and `794043`. No post-boundary sibling repair row appeared.
- Stale bad membership on incidents `3860` and `3864` did not worsen in this sample.

Action taken:
- Updated `IncidentCandidateValidator`.
- Single-call traffic-crash validation can now treat roadway/highway context as a location hint when, and only when, the retained text also has strong crash evidence such as MVC, motor vehicle accident, collision, crash, rear-end wording, or injury crash wording.
- The new roadway hint is limited to route, interstate, highway, direction, exit, ramp, mile-marker, lane, shoulder, median, or vehicle-roadway context. It does not make generic crash words acceptable without a location-like cue.
- No new tests were added. Existing Release test suite passed after the change: `116/116`.
- Deployed through `.\scripts\deploy_pizzad_tar.ps1 -HostName lilhoser@192.168.1.173 -Rid linux-x64`.
- Post-deploy verification: `pizzad` active, `0` restarts, health OK, queue pressure false, no AI blocked reason, embeddings enabled, Qdrant OK, embedding endpoint OK.
- Automation boundary updated to `2026-06-18T15:29:27Z`.

Residual risk:
- The traffic-crash roadway-location hint fix is service-verified but not behaviorally proven by a live accepted row yet. Watch for row-`29791` class I-75 MVC single-call subsets to recover without accepting unrelated Highway 193 or lane-only calls.
- Watch for false single-call traffic incidents where incidental route-like numbers plus generic accident wording pass the new hint. The event still needs strong crash wording and final membership validation.
- The `15:13:55Z` rear-end crash wording fix and `14:58:48Z` spoken single-digit address-anchor fix both still need positive live proof.
- The `13:59:56Z` sibling-repair final-validation gate remains unproven because no post-boundary sibling repair has exercised it.
- Stale bad membership remains on incidents `3864` and `3860`; no direct production repair or backfill was performed.

### 2026-06-18 15:40Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T15:40Z`; rows before the `2026-06-18T15:29:27Z` traffic-crash roadway-location hint deployment are context unless needed for comparison. Deployment boundary changed during this run to `2026-06-18T15:43:53Z`.

Observed:
- Health stayed acceptable: `/health` returned OK, queue pressure false, and no AI blocked reason. Immediately after deploy the live backlog was small (`3` queue depth, `4` pending transcriptions, about `44s` pending audio).
- `pizzad` was active since `2026-06-18 11:29:27 EDT` with `0` restarts before the change, then active since `2026-06-18 11:43:53 EDT` with `0` restarts after deployment.
- Runtime after deploy showed embeddings enabled, Qdrant OK, embedding endpoint OK, embedding queue `0`, failed embeddings `0`, pending embeddings `0`, and no embedding error.
- Last-hour quality remained acceptable: `455` calls, `401` complete OK transcripts, `37` repetitive, `10` empty, `6` too short, and `1` marker-only.
- AI was clean in the quality window: `36/36` successes, `0` failures, `0` endpoint truncations, and latest AI work at `2026-06-18T15:38:57Z`.
- Evidence verifier retention mismatches stayed `0`. Verifier truncation was `0` in this window.

Evidence reviewed:
- Rows `29796` and `29797` proved the `15:29:27Z` traffic-crash roadway-location hint by creating incident `3871` from call `794517`, the I-75 North MVC with possible injury and Kia Optima / Tahoe evidence, instead of continuing to reject it.
- The same proof exposed a title/detail grounding regression. The created incident `3871` was saved as `Vehicle accident at Oliver Ln/Hwy 193`, but retained call `794517` did not support Oliver Lane or Highway 193. That location came from unrelated pruned call `794615`, which had been excluded earlier.
- Rows `29800` through `29802` then created a sibling from call `794575` and merged it into incident `3871`. Transcript review suggests `794575` may be a same-scene I-75 North rear-end continuation, but the incident title still retained the unsupported Oliver Lane / Highway 193 location. Current incident `3871` has calls `794517` and `794575` with that stale unsupported title.
- Row `29803` rejected a routine/status rollup around `795169` and `795179`; no evidence supported a change from that reject in this run.
- Incident `3870` remained grounded as an unconscious-person event at `4616 Rossville Boulevard`; the saved title/detail did not carry the stray fire-alarm phrase that appeared in model parent evidence.

Action taken:
- Updated `IncidentNarrativeGrounder`.
- Unsupported-narrative fallback now takes fallback locations only from retained call transcripts, not from the rejected model title/detail text.
- Narrative support now rejects compound claimed locations such as slash-separated locations or multiple road parts unless at least one distinctive location token is present in the retained-call evidence. This targets mixed-candidate leakage like `Oliver Ln/Hwy 193` without invalidating simple retained titles that currently rely on model-normalized address wording.
- No new tests were added. Existing Release test suite passed after the change: `116/116`.
- Deployed through `.\scripts\deploy_pizzad_tar.ps1 -HostName lilhoser@192.168.1.173 -Rid linux-x64`.
- Post-deploy verification: `pizzad` active, `0` restarts, health OK, queue pressure false, no AI blocked reason, embeddings enabled, Qdrant OK, embedding endpoint OK.
- Automation boundary updated to `2026-06-18T15:43:53Z`.

Residual risk:
- Current incident `3871` still has stale unsupported location text until normal live processing rewrites it; direct production repair and backfill are outside this bake authority.
- Watch for compound unsupported locations from pruned mixed candidates leaking into new titles or surviving existing-title validation.
- The location support guard is intentionally narrow. It validates slash-separated or multi-road claimed locations but still allows simple model-normalized address titles unless other title/detail support fails.
- The `15:29:27Z` traffic-crash roadway-location hint, `15:13:55Z` rear-end wording, and `14:58:48Z` spoken single-digit address-anchor fixes still need more live proof.
- The `13:59:56Z` sibling-repair final-validation gate remains unproven beyond this non-conflicting merge path.
- Stale bad membership remains on incidents `3864` and `3860`; no direct production repair or backfill was performed.

### 2026-06-18 15:55Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T15:55Z`; rows before the `2026-06-18T15:43:53Z` compound narrative-location deployment are context unless needed for comparison. Deployment boundary changed during this run to `2026-06-18T15:57:25Z`.

Observed:
- Health stayed clean: `/health` returned OK, queue depth `0`, queue pressure false, pending transcriptions `0`, pending audio `0s`, and no AI blocked reason.
- `pizzad` was active since `2026-06-18 11:43:53 EDT` with `0` restarts before the change, then active since `2026-06-18 11:57:25 EDT` with `0` restarts after deployment.
- Runtime after deploy showed embeddings enabled, Qdrant OK, embedding endpoint OK, embedding queue `0`, failed embeddings `0`, pending embeddings `0`, and no embedding error.
- Last-hour quality remained acceptable: `470` calls, `411` complete OK transcripts, `43` repetitive, `9` empty, and `7` too short.
- AI was clean in the quality window: `34/34` successes, `0` failures, `0` endpoint truncations, and latest AI work at `2026-06-18T15:52:44Z`.
- Evidence verifier retention mismatches stayed `0`. Verifier truncation was very low: average truncated calls about `0.06`, max `1`.

Evidence reviewed:
- Normal live processing after the `15:43:53Z` deployment did rewrite incident `3871` away from the unsupported `Oliver Ln/Hwy 193` location. That proved the compound-location leak fix on the exact stale incident.
- The same update exposed a narrower title quality regression: incident `3871` became `Vehicle accident at Left Side Of The Road` and retained only call `794575`. The location was supported by retained text but is still too generic to be a useful incident title location.
- The current retained call `794575` supports a rear-end accident, but `Left Side Of The Road` is a generic roadway-position phrase, not a concrete event location. It should not be appended to a title as if it were a place.
- Rows after the boundary did not show sibling repair merging incompatible concrete locations. The `3871` sibling path remains watch-only for membership quality because it dropped `794517`, but the evidence was not strong enough for a separate membership rule change in this run.
- Row `29803` rejected a Cleveland difficulty-breathing / Acorn Lane pair as routine status rollup; the transcripts looked like split dispatch/location detail, but this was not enough for a new split-dispatch change without more representative proof.

Action taken:
- Updated `IncidentNarrativeGrounder`.
- Narrative grounding now treats generic roadway-position phrases such as `left side of the road`, `right hand lane`, or `middle shoulder` as invalid title locations unless they are tied to a concrete highway or route.
- Retained-call fallback titles also skip those generic roadway-position phrases, so a title can fall back to `Vehicle accident` instead of `Vehicle accident at Left Side Of The Road`.
- No new tests were added. Existing Release test suite passed after the change: `116/116`.
- Deployed through `.\scripts\deploy_pizzad_tar.ps1 -HostName lilhoser@192.168.1.173 -Rid linux-x64`.
- Post-deploy verification: `pizzad` active, `0` restarts, health OK, queue depth `0`, queue pressure false, pending transcriptions `0`, pending audio effectively clear, no AI blocked reason, embeddings enabled, Qdrant OK, embedding endpoint OK.
- Automation boundary updated to `2026-06-18T15:57:25Z`.

Residual risk:
- Current incident `3871` still has stale `Left Side Of The Road` title text until normal live processing rewrites it; direct production repair and backfill are outside this bake authority.
- Watch for false title rewrites where a useful but terse roadway-side description was the only available location. The fix only suppresses generic position phrases when they lack concrete highway or route context.
- The `15:43:53Z` compound narrative-location fix is proven on stale incident `3871`; the new `15:57:25Z` generic-roadway-position title fix still needs live proof.
- The `15:29:27Z` traffic-crash roadway-location hint and `15:13:55Z` rear-end wording fixes need continued proof that they recover true crash events without accepting weak lane-only traffic chatter.
- The `13:59:56Z` sibling-repair final-validation gate remains unproven for incompatible-location sibling attempts.
- Stale bad membership remains on incidents `3864` and `3860`; no direct production repair or backfill was performed.

### 2026-06-18 16:10Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T16:11Z`; rows before the `2026-06-18T15:57:25Z` generic roadway-title-location deployment are context unless needed for comparison. Deployment boundary unchanged.

Observed:
- Health stayed clean: `/health` returned OK, queue depth `0`, queue pressure false, pending transcriptions `0`, pending audio `0s`, and no AI blocked reason.
- `pizzad` was active since `2026-06-18 11:57:25 EDT` with `0` restarts.
- Runtime showed embeddings enabled, Qdrant OK, embedding endpoint OK, embedding queue `0`, failed embeddings `0`, pending embeddings `0`, and no embedding error.
- Last-hour quality remained acceptable: `477` calls, `427` complete OK transcripts, `36` repetitive, `8` empty, `4` too short, and `2` numeric-noise poor-quality calls.
- AI was clean in the quality window: `33/33` successes, `0` failures, `0` endpoint truncations, and latest AI work at `2026-06-18T16:10:53Z`.
- Evidence verifier retention mismatches stayed `0`. Verifier truncation remained low: average truncated calls about `0.06`, max `1`.

Evidence reviewed:
- No post-boundary row exercised incident `3871`, so its stale `Vehicle accident at Left Side Of The Road` title remains unproven after the `15:57:25Z` fix. Direct production repair and backfill remain outside scope.
- Post-boundary rows `29812` and `29813` created incident `3874`, and row `29815` updated it. Transcript review supports the membership: calls `795041`, `795043`, and `795115` all reference an unconscious male response at `1335 Sonora Lane`; the `Crabtree Road` text in `795115` appears to be a cross-street or approach detail, not a conflicting event.
- Post-boundary rows `29814` and `29816` rejected THP traffic chatter around tags, tire change, shoulder, and mile-marker wording. Transcript review did not show strong crash or emergency evidence, so the rejects look appropriate and do not suggest a traffic-crash roadway-hint regression.
- Current incidents `3860`, `3864`, and `3871` still contain known stale membership/title debt from earlier pre-boundary rows. No normal live update in this run worsened those incidents.

Action taken:
- No code change and no deployment.
- Updated this watchlist with the post-boundary clean-health signal and the remaining stale-title risk on incident `3871`.

Residual risk:
- Incident `3871` still needs a normal live update to prove the `15:57:25Z` generic roadway-position title-location guard rewrites away from `Left Side Of The Road`.
- Continue watching for useful roadway-side descriptions being over-filtered when concrete highway or route context exists.
- Continue watching for THP traffic chatter or tag/tire/shoulder administrative calls accidentally becoming traffic incidents under the single-call traffic-crash roadway hint.
- Stale bad membership remains on incidents `3864`, `3860`, and `3871`; no direct production repair or backfill was performed.

### 2026-06-18 16:25Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T16:25Z`; rows before the `2026-06-18T15:57:25Z` generic roadway-title-location deployment are context unless needed for comparison. Deployment boundary unchanged.

Observed:
- Health stayed clean: `/health` returned OK, queue depth `0`, queue pressure false, pending transcriptions `1`, pending audio `22s`, and no AI blocked reason. Runtime separately showed pending transcriptions and pending audio at `0`, so the small pending count looked transient rather than backlog.
- `pizzad` remained active since `2026-06-18 11:57:25 EDT` with `0` restarts.
- Runtime showed embeddings enabled, Qdrant OK, embedding endpoint OK, embedding queue `0`, failed embeddings `0`, pending embeddings `0`, and no embedding error.
- Last-hour quality remained acceptable: `538` calls, `482` complete OK transcripts, `38` repetitive, `9` empty, `6` too short, `2` numeric-noise poor-quality calls, and `1` pending call.
- AI was clean in the quality window: `35/35` successes, `0` failures, `0` endpoint truncations, and latest AI work at `2026-06-18T16:23:36Z`.
- Evidence verifier retention mismatches stayed `0`. Verifier truncation remained low: average truncated calls about `0.06`, max `1`.

Evidence reviewed:
- Incident `3871` still has stale `Vehicle accident at Left Side Of The Road` title text. No post-boundary audit row touched that incident, so there is still no live proof or disproof of the `15:57:25Z` generic roadway-position title-location guard.
- Rows `29817` and `29819` created and updated incident `3875` for `9607 Barbie Road, Unit 83`. Transcript review supports the retained two-call membership: `795673` dispatches to `9607 Barbie Road Unit 83` for `possible struggle`, and `795701` repeats the address/unit with age, patient, and response detail. The `Falcon Road` text appears to be cross-street detail, not a separate event.
- Row `29818` rejected a THP traffic pair where one call described a vehicle or object out of the roadway and the other was a tag check. This looks like an appropriate reject and not a missed traffic crash.
- Row `29820` rejected a Cleveland/Bradley medical-transport pair. Transcript review showed explicit non-emergency or convalescent transport context, no concrete dispatch anchor, and no parent emergency evidence; the reject looks appropriate despite fall/head-bump wording in one call.
- No post-boundary sibling repair occurred, and no current incident worsened through normal live processing in this run.

Action taken:
- No code change and no deployment.
- Updated this watchlist with the post-boundary evidence and continued stale-title watch on incident `3871`.

Residual risk:
- Incident `3871` still needs a normal live update to prove the `15:57:25Z` generic roadway-position title-location guard rewrites away from `Left Side Of The Road`.
- Continue watching for over-filtering of useful roadway-side descriptions when concrete highway or route context exists.
- Continue watching for medical transport rejects where true parent emergency evidence is present; row `29820` did not provide that proof.
- Stale bad membership remains on incidents `3864`, `3860`, and `3871`; no direct production repair or backfill was performed.

### 2026-06-18 16:40Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T16:40Z`; rows before the `2026-06-18T15:57:25Z` generic roadway-title-location deployment are context unless needed for comparison. Deployment boundary changed during this run to `2026-06-18T16:47:52Z`.

Observed:
- Pre-deploy health was OK with queue pressure false, no AI blocked reason, and a small transient transcription backlog: queue depth `3` to `4`, pending transcriptions `4` to `5`, and pending audio up to `74s`.
- `pizzad` was active since `2026-06-18 11:57:25 EDT` with `0` restarts before the change.
- Pre-deploy runtime showed embeddings enabled, Qdrant OK, embedding endpoint OK, embedding queue `0`, failed embeddings `0`, pending embeddings `0`, and no embedding error.
- Last-hour quality remained acceptable: `572` calls, `507` complete OK transcripts, `44` repetitive, `7` empty, `7` too short, `3` numeric-noise poor-quality calls, and `4` pending calls.
- AI was clean in the quality window: `30/30` successes, `0` failures, `0` endpoint truncations, and latest AI work at `2026-06-18T16:36:28Z`.
- Evidence verifier retention mismatches stayed `0`. Verifier truncation remained low: average truncated calls about `0.07`, max `1`.

Evidence reviewed:
- Row `29824` rejected a likely true two-call Memorial Hospital automatic-fire-alarm event because only one call supported the model concrete incident location. Transcript review showed both calls were the same event: `796125` dispatched Memorial Hospital at noisy `2525 Dittles Avenue` for an auto fire alarm, and `796165` continued the response to Memorial Hospital at noisy `25 25 the sales Avenue` with off-street details and fire-alarm wording. The failure mode was noisy street text creating a concrete-location conflict even though the calls shared a named landmark and specific event concept.
- Row `29823` rejected a likely true overdose dispatch/update sequence as a routine/status rollup. Transcript review showed `796113` dispatched an overdose at Avery-Johns Park involving a male in a bathroom, while `796201` and `796245` were same-event condition/cancel updates. The failure mode was that routine update calls caused the whole candidate to reject before the validator recovered the strong single-call dispatch.
- Row `29822` rejected a Lifeforce weather/transport conversation. Transcript review showed parent event context but no concrete incident anchor and a flight-availability discussion, so no change was made for that row.
- Incident `3871` still has stale `Vehicle accident at Left Side Of The Road` title text. No post-boundary audit row touched it in this run, so the `15:57:25Z` title-location guard still lacks live proof.

Action taken:
- Updated `IncidentCandidateValidator`.
- Final membership location conflict handling now allows a noisy concrete-location conflict to be bridged only when the calls share a named landmark and an overlapping specific event concept, such as `fire_alarm`. This is intentionally local to final validation and does not broaden global RAG or anchor sourcing.
- Routine/status rollups can now recover one strong single-call incident after pruning routine/status calls. This is limited to cases where the remaining call independently passes single-call validation.
- Named landmarks now count as a single-call location hint when paired with a strong incident signal; transport-only and hospital-handoff-only text still requires parent emergency evidence and remains rejectable.
- Existing Release test suite passed after the change: validator subset `32/32`; full suite `116/116`.
- Deployed through `.\scripts\deploy_pizzad_tar.ps1 -HostName lilhoser@192.168.1.173 -Rid linux-x64`.
- Post-deploy verification: `pizzad` active since `2026-06-18 12:47:52 EDT`, `0` restarts, health OK, queue depth `0`, queue pressure false, pending transcriptions `1`, pending audio `6s`, no AI blocked reason, embeddings enabled, Qdrant OK, embedding endpoint OK, embedding queue `0`, failed embeddings `0`, pending embeddings `0`, and no embedding error.
- Automation boundary updated to `2026-06-18T16:47:52Z`.

Residual risk:
- The `16:47:52Z` named-landmark and routine-rollup recovery changes need live proof on row-`29823` and row-`29824` class incidents.
- Watch for false joins where different events share the same named landmark and a broad event concept, especially hospitals, parks, schools, and stations.
- Watch for false single-call incidents where routine chatter contains an incidental strong-event word plus a named landmark.
- Incident `3871` still needs a normal live update to prove the `15:57:25Z` generic roadway-position title-location guard rewrites away from `Left Side Of The Road`.
- Stale bad membership remains on incidents `3864`, `3860`, and `3871`; no direct production repair or backfill was performed.

### 2026-06-18 16:55Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T17:00Z`; rows before the `2026-06-18T16:47:52Z` named-landmark and routine-rollup recovery deployment are context unless needed for comparison.

Observed:
- Health stayed clean after the `16:47:52Z` deployment: `pizzad` active since `2026-06-18 12:47:52 EDT`, `0` restarts, queue depth `0`, queue pressure false, no pending transcriptions, no pending audio, and no AI blocked reason.
- Runtime showed embeddings enabled, Qdrant OK, embedding endpoint OK, embedding queue `0`, failed embeddings `0`, pending embeddings `0`, and no embedding error.
- Last-hour quality remained acceptable: `643` calls, `42` AI requests, `42` AI successes, `0` AI failures, `0` endpoint truncations, `23` verifier runs, `0` verifier retention mismatches, `5` incident creates, `8` updates, and `18` rejects.

Evidence reviewed:
- Rows `29834` and `29835` recovered call `795949` as a standalone `2626 Walker Road` heart-problems incident after a mixed candidate also contained unrelated Camp Jordan calls. This is an early positive signal that mixed-candidate standalone recovery still works after the latest deployment.
- Row `29843` later linked fire manpower call `795805` to the same `2626 Walker Road` incident. Transcript review supports same-address response context: `795805` says `2626 Walker Road ... for manpower`, and `795949` requests response to `2626 Walker Road` for a heart-problems patient.
- Rows `29838`, `29839`, `29845`, and `29846` rejected attempts to merge or expand across the unrelated `336 Camp Jordan Parkway`, `6698 Plus Court`, and `2626 Walker Road` medical calls. The latest sibling-repair full-union validation correctly blocked the broader merge, though stale pre-boundary membership still leaves call `795575` on incident `3876`.
- Row `29840` created a supported reckless-driver BOLO from calls `796545` and `796593`; both calls describe the same white wagon or Wagoneer on southbound I-75 near marker `21`.
- Row `29841` rejected an order-of-protection call as weak, and row `29842` rejected a non-emergency medical transport pair. Transcript review supports both rejects.
- No post-boundary row exercised the new Memorial Hospital named-landmark conflict bridge or Avery-Johns Park routine-rollup single-call recovery yet.
- Incident `3871` still has stale `Vehicle accident at Left Side Of The Road` title text. No post-boundary audit row touched it in this run.

Action taken:
- No code change and no deployment.
- Updated this watchlist with the post-boundary clean signal and the remaining stale-membership/title risks.

Residual risk:
- The `16:47:52Z` named-landmark and routine-rollup recovery changes still need direct live proof on row-`29823` and row-`29824` class incidents.
- Stale pre-boundary membership remains on incident `3876`, where call `795575` appears unrelated to the `336 Camp Jordan Parkway` heart-problems event. Current post-boundary sibling repair rejected broader merges involving that stale member, but manual repair or backfill remains outside bake authority.
- Incident `3871` still needs a normal live update to prove the `15:57:25Z` generic roadway-position title-location guard rewrites away from `Left Side Of The Road`.
- Continue watching for false joins from named landmarks, same-address response linkage, and sibling repair around stale incidents.

### 2026-06-18 17:10Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T17:18Z`; rows before the final `2026-06-18T17:17:53Z` deployment are context unless needed for comparison. Deployment boundary changed during this run to `2026-06-18T17:17:53Z`.

Observed:
- Pre-change health was clean: `pizzad` active since `2026-06-18 12:47:52 EDT`, `0` restarts, queue depth `0`, queue pressure false, no pending transcriptions, no pending audio, and no AI blocked reason.
- Runtime showed embeddings enabled, Qdrant OK, embedding endpoint OK, embedding queue `0`, failed embeddings `0`, pending embeddings `0`, and no embedding error.
- Last-hour quality was clean on AI/verifier health: `657` calls, `40/40` AI successes, `0` AI failures, `0` endpoint truncations, `22` verifier runs, average verifier truncation about `0.05`, max `1`, and `0` verifier retention mismatches.

Evidence reviewed:
- Rows `29847` through `29849` proved a post-boundary sibling-repair regression. A bad four-call semantic/RAG-only medical sibling was created from `795831`, `795885`, `795907`, and `796361`, then merged into incident `3877`, the `2626 Walker Road` heart-problems incident, leaving incident `3877` with unrelated hospital handoff and `6698 Palms Court` bleeding calls.
- Transcript review showed the bad sibling was not one coherent event: `795885` was a concrete `6698 Palms Court` bleeding dispatch; `795831` and `795907` were separate Memorial Hospital handoff reports; `796361` was a Parkridge Hospital handoff. None repeated the `2626 Walker Road` incident anchor except already-retained calls `795805` and `795949`.
- The immediate failure mode was that sibling repair accepted a union even when final membership validation only proved a subset. The underlying create path also let an unanchored transport/hospital-handoff-heavy cluster survive because several calls shared generic medical semantics without a shared parent event anchor.
- Rows `29845` and `29846` showed the sibling full-union final-validation gate did block one broader bad merge, but row `29849` showed that subset-only validation was still not strict enough for destructive sibling repair.
- Rows `29851`, `29853`, `29854`, and `29855` were reviewed as reasonable rejects: operational chatter, weak single-call road-hazard/service text, non-emergency medical transport, and a weak two-call pair below similarity threshold.
- Row `29856` created incident `3881`, a supported law-enforcement subject-transfer/perimeter incident involving `796132`, `796383`, and `797021`. Transcript review showed a coherent THP/Rhea County subject/perimeter/transfer thread.
- Post-final-boundary rows `29858` and `29859` updated incident `3880` while retaining only call `796775`, the concrete `1952 South State Creek Road` elevated-heart-rate dispatch, and excluding hospital handoff calls `796355` and `796979`. This is an early clean signal for the unanchored transport/hospital-handoff cluster guard.

Action taken:
- Updated `AutomaticInsightsService` so final membership only retains validator-unmatched calls with a strong server-side link (`>=0.78`), not broad low-confidence compatible-event links.
- Updated sibling-incident merge repair so a final validation result that says the validator matched only a subset is rejected, even when the retained call count did not shrink.
- Updated `IncidentCandidateValidator` so clusters dominated by unanchored transport or hospital-handoff calls are rejected unless the validator can recover one concrete non-transport dispatch as a valid standalone event. Transport or hospital handoff calls that share the parent scene anchor or explicitly reference the parent event remain eligible.
- Existing Release tests passed after the combined fix: validator subset `32/32`, reconciliation subset `4/4`, full suite `116/116`.
- Deployed through `.\scripts\deploy_pizzad_tar.ps1 -HostName lilhoser@192.168.1.173 -Rid linux-x64`. The first deploy at about `17:14Z` contained only the sibling/retention gate; the final deploy at `2026-06-18T17:17:53Z` includes the combined sibling, retention, and transport-cluster guard.
- Post-deploy verification: `pizzad` active since `2026-06-18 13:17:53 EDT`, `0` restarts, health OK, queue depth `0`, queue pressure false, runtime pending transcriptions `1`, pending audio `26s`, no AI blocked reason, embeddings enabled, Qdrant OK, embedding endpoint OK, embedding queue `0`, failed embeddings `0`, pending embeddings `0`, and no embedding error.
- Initial post-final-boundary audit showed only rows `29858` and `29859`, which retained the concrete South State Creek dispatch and excluded unrelated hospital handoff calls.

Residual risk:
- Incident `3877` remains stale with unrelated calls `795831`, `795885`, `795907`, and `796361` attached; no direct production repair or backfill was performed.
- The `17:17:53Z` fix has an early clean signal for pruning unanchored hospital handoff calls, but still needs live proof that sibling repair rejects subset-only unions.
- Watch for true transport follow-up calls being over-pruned when they do not repeat an address but do explicitly reference the parent crash, fire, overdose, or medical event.
- The `16:47:52Z` Memorial Hospital named-landmark bridge and Avery-Johns Park routine-rollup recovery still need direct live proof.
- Incident `3871` still needs a normal live update to prove the `15:57:25Z` generic roadway-position title-location guard rewrites away from `Left Side Of The Road`.

### 2026-06-18 17:25Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T17:29Z`; rows before the final `2026-06-18T17:28:39Z` deployment are context unless needed for comparison. Deployment boundary changed during this run to `2026-06-18T17:28:39Z`.

Observed:
- Pre-change health was clean: `pizzad` active since `2026-06-18 13:17:53 EDT`, `0` restarts, queue depth `0`, queue pressure false, no pending transcriptions, no pending audio, and no AI blocked reason.
- Runtime showed embeddings enabled, Qdrant OK, embedding endpoint OK, embedding queue `0`, failed embeddings `0`, pending embeddings `0`, and no embedding error.
- Last-hour quality stayed clean on AI and verifier health: `618` calls, `46/46` AI successes, `0` AI failures, `0` endpoint truncations, `27` verifier runs, average verifier truncation about `0.04`, max `1`, and `0` verifier retention mismatches.

Evidence reviewed:
- Rows `29858` and `29859` remained an early clean signal for the `17:17:53Z` transport/handoff guard: incident `3880` retained only call `796775`, the concrete `1952 South State Creek Road` elevated-heart-rate dispatch, while excluding unrelated Erlanger handoff calls `796355` and `796979`.
- Row `29861` cleanly updated reckless-driver incident `3878` with the same supported two-call white-wagon I-75 BOLO set.
- Row `29860` rejected call `796647` as lacking a strong emergency/event signal. Transcript review showed a concrete EMS dispatch to `6519 Ringgold Road, room 114` for a `65 year old male` who `feels like it's racing` and cannot concentrate, with police responding. The failure mode was a missing general medical rhythm/palpitation wording path, not a location or verifier problem.

Action taken:
- Broadened medical event wording in `IncidentCandidateValidator` to recognize heart-rhythm/palpitation dispatch evidence such as `heart racing`, `elevated heart rate`, `palpitations`, and dispatch phrasing like `feels like it is racing`.
- Updated `AutomaticInsightsService` event-class compatibility so the same wording maps to medical evidence instead of a generic public-safety fallback.
- Updated `IncidentNarrativeGrounder` so retained palpitation or heart-racing evidence can ground a specific `Heart problems` fallback title instead of an unsupported generic medical title.
- Existing Release tests passed after the change: validator and narrative focused subset `38/38`; full suite `116/116`.
- Deployed through `.\scripts\deploy_pizzad_tar.ps1 -HostName lilhoser@192.168.1.173 -Rid linux-x64`.
- Post-deploy verification: `pizzad` active since `2026-06-18 13:28:39 EDT`, `0` restarts, health OK, queue depth `0`, queue pressure false, runtime pending transcriptions `1`, pending audio `20s`, no AI blocked reason, embeddings enabled, Qdrant OK, embedding endpoint OK, embedding queue `0`, failed embeddings `0`, pending embeddings `0`, and no embedding error.
- No incident operation had landed after the final service start at the time of verification.

Residual risk:
- The `17:28:39Z` heart-rhythm wording fix needs live proof on a row-`29860`-class single-call medical dispatch.
- Watch for false medical incidents from incidental `racing` wording; the pattern is intentionally limited to heart, heart-rate, palpitation, or `feels like it is racing` symptom phrasing.
- The `17:17:53Z` sibling/transport-cluster guard still needs live proof that sibling repair rejects subset-only unions.
- Stale bad membership remains on incidents `3877`, `3864`, `3860`, and `3871`; no direct production repair or backfill was performed.

### 2026-06-18 17:40Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T17:40Z`; rows before the `2026-06-18T17:28:39Z` heart-rhythm wording deployment are context unless needed for comparison.

Observed:
- Health stayed clean after the `17:28:39Z` deployment: `pizzad` active since `2026-06-18 13:28:39 EDT`, `0` restarts, queue depth `0`, queue pressure false, no pending transcriptions, no pending audio, and no AI blocked reason.
- Runtime showed embeddings enabled, Qdrant OK, embedding endpoint OK, embedding queue `0`, failed embeddings `0`, pending embeddings `0`, and no embedding error.
- Last-hour quality stayed clean on AI and verifier health: `609` calls, `46/46` AI successes, `0` AI failures, `0` endpoint truncations, `28` verifier runs, average verifier truncation about `0.04`, max `1`, and `0` verifier retention mismatches.

Evidence reviewed:
- Rows `29865` and `29866` proved the `17:28:39Z` heart-rhythm wording fix. The system recovered call `796647` from a mixed candidate and created incident `3882` as `Heart problems at 6519 Ringgold Road`; transcript review supports the single-call membership.
- Rows `29867` and `29868` are a clean signal for the `17:17:53Z` subset-only sibling repair guard. The system rejected attempts to merge incident `3882` with unrelated South State Creek incident `3880` because validation only matched `1/2` calls and did not prove the full union.
- Rows `29858`, `29859`, and `29862` continued to keep incident `3880` on the single concrete South State Creek elevated-heart-rate dispatch while excluding unrelated Erlanger hospital handoff calls.
- Rows `29871` and `29872` updated incident `3881` while excluding weak call `796453`. Transcript review suggests `796453` may be same-unit field context for the same subject/perimeter event, but it lacks enough distinctive event or location linkage for a safe general algorithm change in this run. Treat similar dropped `2354` or deputy-context continuations as watch-only evidence unless repeated.
- Rows `29863` and `29864` rejected a standalone EMS dispatch to `McCallie and Central` / Exxon as medical transport context. The transcript has a patient and vehicle location but no clear strong symptom or emergency wording; no change was made.

Action taken:
- No code change and no deployment.
- Updated this watchlist with direct proof of the heart-rhythm fix, clean sibling-repair behavior, and the watch-only possible dropped law-enforcement continuation.

Residual risk:
- Watch for repeated missed police/THP field-context continuations like `796453` when a unit, deputy count, or subject transfer thread strongly ties calls together without repeating the original event wording.
- Continue watching for false medical incidents from incidental `racing` wording without heart, heart-rate, palpitation, or patient context.
- Stale bad membership remains on incidents `3877`, `3864`, `3860`, and `3871`; no direct production repair or backfill was performed.

### 2026-06-18 17:55Z / 18:04Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T17:55Z`; deployment boundary changed to `2026-06-18T18:04:28Z`.

Observed:
- Pre-change health was clean: `pizzad` active since `2026-06-18 13:28:39 EDT`, `0` restarts, queue depth `0`, queue pressure false, no pending transcriptions, no pending audio, and no AI blocked reason.
- Runtime showed embeddings enabled, Qdrant OK, embedding endpoint OK, embedding queue `0`, failed embeddings `0`, pending embeddings `0`, and no embedding error.
- Last-hour quality stayed clean on AI and verifier health: `565` calls, `46/46` AI successes, `0` AI failures, `0` endpoint truncations, `28` verifier runs, average verifier truncation about `0.14`, max `2`, and `0` verifier retention mismatches.

Evidence reviewed:
- Rows `29887` and `29888` were a high-confidence regression on seizure incident `3884`: the system dropped strong Jane Manor call `796969` and retained different-location Buckley Street seizure call `796973`, while preserving the `Seizure at 114 Jane Manor Circle` title/detail.
- Transcript review supports the failure finding. Calls `796751`, `796863`, and `796969` are same-event Jane Manor / Outdating Pike seizure evidence; call `796973` is a separate Buckley Street seizure dispatch.
- Rows `29892` and `29893` later self-corrected incident `3884` before the new deploy by retaining `796751`, `796863`, and `796969` and excluding `796973`, so no direct data repair was performed.
- Incident `3883` remained well grounded on calls `797519`, `797529`, and `797537` for chest pain at `523 Callaway Court`.
- Rows `29891`, `29895`, `29896`, and `29897` were reviewed as acceptable rejects: transport/handoff without parent emergency evidence, weak THP/EMS pair, routine civil process, and a single traffic-license/status call without a concrete incident anchor.

Action taken:
- Added a final-membership guard in `AutomaticInsightsService`: an existing incident update is rejected when a prior retained source call backed the existing concrete narrative location, that protected call is dropped, and the proposed retained set contains located calls with no compatible protected anchor.
- Added focused regression tests for blocking the Jane Manor to Buckley replacement, allowing the correct Jane Manor set, and avoiding false blocks when the old concrete title was not backed by prior retained source calls.
- Focused tests passed: `AutomaticInsightsServiceMembershipTests`, `IncidentCandidateValidatorTests`, and `IncidentNarrativeGrounderTests`, `41/41`.
- Full Release suite passed: `119/119`.
- Deployed through `.\scripts\deploy_pizzad_tar.ps1 -HostName lilhoser@192.168.1.173 -Rid linux-x64`.
- Post-deploy verification: `pizzad` active since `2026-06-18 14:04:28 EDT`, `0` restarts, health OK, queue depth `0`, queue pressure false, no AI blocked reason, embeddings enabled, Qdrant OK, embedding endpoint OK, embedding queue `0`, failed embeddings `0`, pending embeddings `0`, and no embedding error. The final check had one pending transcription / `14s` audio, still not under pressure.
- Initial verification found no incident operation after the new service start. Rows `29898` and `29899` then landed as a clean post-deploy signal: incident `3884` stayed on Jane Manor calls `796751`, `796863`, and `796969`, while excluding hospital handoff-like seizure call `797423` and unrelated East Brainerd chest-pain call `797809`.

Residual risk:
- Watch for row-`29887` class updates still replacing prior concrete-location-supported members with different-address calls.
- Watch for legitimate title/location corrections being blocked when the old title was not actually supported by prior retained calls; the new guard is explicitly limited to source-backed existing narrative anchors.
- Stale bad membership remains on incidents `3877`, `3864`, `3860`, and `3871`; no direct production repair or backfill was performed.

### 2026-06-18 18:10Z / 18:12Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T18:10Z`; deployment boundary changed to `2026-06-18T18:12:47Z`.

Observed:
- Pre-change health was clean after the `18:04:28Z` deployment: `pizzad` active since `2026-06-18 14:04:28 EDT`, `0` restarts, queue depth `0`, queue pressure false, no pending transcriptions, no pending audio, and no AI blocked reason.
- Runtime showed embeddings enabled, Qdrant OK, embedding endpoint OK, embedding queue `0`, failed embeddings `0`, pending embeddings `0`, and no embedding error.
- Last-hour quality was mostly clean but captured the deploy-window restart cancellation: `570` calls, `47` AI requests, `47/47` successes before the second deploy check; after deploy, the latest quality snapshot showed `42/43` AI successes with one `The operation was canceled` failure at `18:12:47Z`, followed by a successful local-model request at `18:13:19Z`. Verifier health stayed clean with `0` retention mismatches.

Evidence reviewed:
- Rows `29898` and `29899` were clean post-`18:04:28Z` proof for seizure incident `3884`: it stayed on Jane Manor calls `796751`, `796863`, and `796969`, while excluding unrelated or handoff-adjacent calls.
- Rows `29900` and `29901` disproved the first existing-anchor guard for additive conflicts. Chest-pain incident `3883` retained its supported `523 Callaway Court` calls `797519`, `797529`, and `797537`, but also absorbed call `797833`, a separate `7301 East Brainerd Road` chest-pain dispatch, while preserving the Callaway Court title.
- Row `29902` rejected call `797443` as unsupported narrative. Transcript review showed a concrete `272 Boynton Drive` / Hummingbird Lane 77-year-old fall dispatch, but the title/detail proposal did not preserve a specific evidence-backed fallback. This is watch-only for now because it is a single example and the next change focused on the higher-risk false join.

Action taken:
- Tightened the existing-anchor guard in `AutomaticInsightsService` so an existing incident update is rejected when it would add or replace located calls that conflict with a prior retained source-backed concrete narrative anchor.
- Added a focused regression test for the `3883` class case: Callaway Court chest-pain incident plus separate East Brainerd chest-pain dispatch.
- Focused tests passed: `AutomaticInsightsServiceMembershipTests`, `IncidentCandidateValidatorTests`, and `IncidentNarrativeGrounderTests`, `42/42`.
- Full Release suite passed: `120/120`.
- Deployed through `.\scripts\deploy_pizzad_tar.ps1 -HostName lilhoser@192.168.1.173 -Rid linux-x64`.
- Post-deploy verification: `pizzad` active since `2026-06-18 14:12:47 EDT`, `0` restarts, health OK, queue depth `0`, queue pressure false, no pending transcriptions, no pending audio, no AI blocked reason, embeddings enabled, Qdrant OK, embedding endpoint OK, embedding queue `0`, failed embeddings `0`, pending embeddings `0`, and no embedding error.
- Row `29903` landed after the second deploy and created incident `3885`, a supported Bart Whitt Circle false fire alarm with fire dispatch, alarm-location detail, and false-alarm outcome. No immediate post-deploy regression was visible.

Residual risk:
- Watch for row-`29900` class additive same-symptom/different-address joins still surviving, especially when the protected original call remains in membership.
- Watch for legitimate multi-location event updates being over-blocked when the old title was source-backed but a later call is a true transport or explicit parent-event continuation at a different location.
- Row `29902` suggests a possible fall-dispatch narrative fallback gap; revisit only if similar concrete fall calls continue to reject.
- Stale bad membership remains on incidents `3883`, `3877`, `3864`, `3860`, and `3871`; no direct production repair or backfill was performed.

### 2026-06-18 18:25Z / 18:27Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T18:25Z`; deployment boundary changed to `2026-06-18T18:27:53Z`.

Observed:
- Pre-change health was clean after the `18:12:47Z` deployment: `pizzad` active since `2026-06-18 14:12:47 EDT`, `0` restarts, queue depth `0`, queue pressure false, no pending transcriptions, no pending audio, no AI blocked reason, embeddings enabled, Qdrant OK, embedding endpoint OK, embedding queue `0`, failed embeddings `0`, pending embeddings `0`, and no embedding error.
- Last-hour quality showed `580` calls, `43` AI requests, `42/43` AI successes, no truncated AI responses, `27` verifier runs, average verifier truncation `0.52`, maximum verifier truncation `3`, and `0` retention mismatches. The only AI failure was the deploy-window `The operation was canceled` event at `18:12:47Z`, followed by successful local-model routing.

Evidence reviewed:
- Rows `29905` through `29907` disproved the `18:12:47Z` additive guard. Chest-pain incident `3883` accepted separate East Brainerd calls `797809` and `797833`, dropped the prior Callaway Court calls `797519`, `797529`, and `797537`, and rewrote title/detail from final membership.
- Rows `29910` and `29911` then reduced incident `3883` to single East Brainerd call `797833` under the old incident id. The current incident list showed `3883` as `Chest pain at 601 East Brinard Road`, leaving stale bad membership/title caused by normal live processing.
- Transcript review supports the conflict finding. Calls `797519`, `797529`, and `797537` describe chest pain at `523 Callaway Court`; calls `797809` and `797833` describe a separate chest-pain dispatch around `601/7301 East Brainerd Road apartment C8`.
- Row `29912` created a supported Cleveland High School / Raider Drive seizure incident `3887` from calls `798255`, `798291`, and `798323`, with no obvious false join.
- Row `29904` rejected a concrete `353 Apollo Drive` 86-year-old male sick-call dispatch as `medical_transport_context`. This remains watch-only until repeated examples justify a general sick-call or sick-party taxonomy change.

Action taken:
- Corrected the existing-incident concrete-anchor conflict guard to use transcript-extracted weak address, intersection, and location hints as blockers inside this guard, while keeping those weak hints out of positive membership evidence elsewhere.
- Added a focused regression test for the Callaway Court to East Brainerd overwrite case where the old source calls only expose their location through transcript extraction.
- Focused tests passed: `AutomaticInsightsServiceMembershipTests`, `IncidentCandidateValidatorTests`, and `IncidentNarrativeGrounderTests`, `43/43`.
- Full Release suite passed: `121/121`.
- Deployed through `.\scripts\deploy_pizzad_tar.ps1 -HostName lilhoser@192.168.1.173 -Rid linux-x64`.
- Post-deploy verification: `pizzad` active since `2026-06-18 14:27:53 EDT`, `0` restarts, health OK, queue depth `0`, queue pressure false, no pending transcriptions, no pending audio, no AI blocked reason, embeddings enabled, Qdrant OK, embedding endpoint OK, embedding queue `0`, failed embeddings `0`, pending embeddings `0`, and no embedding error.
- No incident operation had landed after the new service start at the time of the post-deploy audit check, so the new guard is deployed but not yet live-proven.

Residual risk:
- Watch for row-`29905` class existing-incident rewrites still replacing source-backed concrete incidents when the source address only exists as a weak transcript extraction.
- Watch for false over-blocks caused by weak transcript location hints inside the existing-incident conflict guard; they should block conflicts only, not create positive links.
- Stale bad membership remains on incident `3883` with call `797833` under the old incident id; no direct production repair or backfill was performed.
- Row `29904` remains a possible sick-call taxonomy gap, but the evidence is not broad enough for a safe invariant change yet.

### 2026-06-18 18:40Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T18:40Z`; deployment boundary unchanged at `2026-06-18T18:27:53Z`.

Observed:
- Health stayed clean: `pizzad` active since `2026-06-18 14:27:53 EDT`, `0` restarts, queue depth `0`, queue pressure false, `1` pending transcription, `17s` pending audio, no AI blocked reason, embeddings enabled, Qdrant OK, embedding endpoint OK, embedding queue `0`, failed embeddings `0`, pending embeddings `0`, and no embedding error.
- Last-hour quality showed `583` calls, `42` AI requests, `41/42` AI successes, no truncated AI responses, `26` verifier runs, average verifier truncation `0.85`, maximum verifier truncation `5`, and `0` retention mismatches. The single AI failure remained the known deploy-window cancellation pattern rather than an ongoing routing issue.

Evidence reviewed:
- Row `29913` was an early post-boundary clean signal: an East Brainerd chest-pain candidate against incident `3883` was rejected because only one call supported the concrete incident location while other calls cited different locations.
- Rows `29916` and `29917` recovered incident `3883` back to the Callaway Court membership, retaining calls `797519`, `797529`, `797537`, and `798151`.
- Row `29919` was a direct proof of the `18:27:53Z` guard: the system rejected a proposed update containing East Brainerd calls `797809` and `797833` against the old Callaway incident key with reason `final membership would add or replace existing concrete incident anchor with conflicting location evidence`.
- Current incident state confirms the final persistence is clean for this issue: incident `3883` is titled `Chest pain at 523 Callaway Court` and retains Callaway dispatch/detail calls `797519`, `797529`, `797537`, plus likely Medic 15 Memorial hospital encode follow-up `798151`; it no longer retains East Brainerd calls `797809` or `797833`.
- Row `29920` rejected call `798321`, a Cleveland/THP transcript mentioning Highway 64, Melbourne Brewer Road, `single vehicle`, and EMS on scene, as lacking a strong event signal. This may be a single-vehicle crash wording gap, but `single vehicle` alone is ambiguous enough that no safe taxonomy change is justified from one row.

Action taken:
- No code change. The latest deployed guard produced the intended reject and normal live processing restored the previously stale `3883` membership without direct data repair.
- Updated this watchlist and final report section with the proof and the new watch-only traffic wording candidate.

Residual risk:
- Continue watching for weak transcript location hints over-blocking legitimate transport or hospital follow-up calls. The retained `798151` follow-up did not trigger an over-block, which is a useful clean signal.
- Watch for repeated row-`29920` class traffic calls where `single vehicle` plus EMS/lane/highway context clearly means a crash, but avoid adding broad single-vehicle wording without more evidence.
- Stale bad membership remains on older incidents `3877`, `3864`, `3860`, and `3871`; incident `3883` is no longer stale in the current live incident list.

### 2026-06-18 18:55Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T18:55Z`; deployment boundary unchanged at `2026-06-18T18:27:53Z`.

Observed:
- Health remained clean: `pizzad` active since `2026-06-18 14:27:53 EDT`, `0` restarts, queue depth `0`, queue pressure false, no pending transcriptions, no pending audio, no AI blocked reason, embeddings enabled, Qdrant OK, embedding endpoint OK, embedding queue `0`, failed embeddings `0`, pending embeddings `0`, and no embedding error.
- Last-hour quality showed `602` calls, `42` AI requests, `41/42` AI successes, no truncated AI responses, `23` verifier runs, average verifier truncation `0.87`, maximum verifier truncation `5`, and `0` retention mismatches.

Evidence reviewed:
- Rows `29921` and `29922` kept seizure incident `3887` on its supported Cleveland High School / Raider Drive call set and excluded weak unrelated call `798409`.
- Rows `29924` and `29925` gave another direct proof of the existing-anchor conflict guard. The assembler produced an East Brainerd chest-pain set against the old Callaway incident key, but final membership rejected it with `final membership would add or replace existing concrete incident anchor with conflicting location evidence`.
- Current incident `3883` still has clean Callaway Court membership: `797519`, `797529`, `797537`, and likely Medic 15 follow-up `798151`.
- Rows `29927` through `29929` created two supported new incidents: `3888`, an MVC on Black Fox Road SW and South Lee Highway SW with minor injuries, and `3889`, a chest-pain medical assist around `2500 points south southeast` / room or apartment `316`.
- Row `29926` again rejected the Highway 64 / Melbourne Brewer Road `single vehicle` call pair as transport or hospital-handoff lacking a parent event anchor. Transcript review still does not justify broadening crash evidence from this wording alone.
- Rows `29930` through `29932` rejected low-confidence public-safety/property calls: missing property at `1020 W 37th Street`, suspicious-person discomfort at `1857 White Strawberry Lane NE`, and possible store theft at `3103 Dodson Avenue`. These are watch-only because they are concrete police-service calls, but the transcripts are weak enough that accepting them would risk broad routine/property chatter.

Action taken:
- No code change. The latest guard continues to block the high-risk false join pattern, and no new accepted incident showed an immediate membership regression.
- Updated this watchlist and final report section.

Residual risk:
- Incident `3889` has an awkward but transcript-grounded title/location phrase, `2500 South Southeast`. Watch for repeated model-normalized but awkward place-name titles before changing title normalization.
- Continue watching whether concrete low-severity police-service calls such as suspicious person or shoplifting are being under-accepted, but avoid a broad acceptance rule until repeated examples show a clear operational incident pattern.
- Continue watching the `single vehicle` traffic wording gap; it needs stronger repeated crash context before a safe taxonomy change.

### 2026-06-18 19:10Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T19:10Z`; deployment boundary unchanged at `2026-06-18T18:27:53Z`.

Observed:
- Health remained clean: `pizzad` active since `2026-06-18 14:27:53 EDT`, `0` restarts, queue depth `0`, queue pressure false, no pending transcriptions, no pending audio, no AI blocked reason, embeddings enabled, Qdrant OK, embedding endpoint OK, embedding queue `0`, failed embeddings `0`, pending embeddings `0`, and no embedding error.
- Last-hour quality showed `593` calls, `42` AI requests, `41/42` AI successes, no truncated AI responses, `22` verifier runs, average verifier truncation `0.64`, maximum verifier truncation `5`, and `0` retention mismatches.

Evidence reviewed:
- Rows `29938` and `29939` updated MVC incident `3888` while excluding weak/unrelated Highway 64 / Melbourne Brewer Road call `798321`, preserving the supported Black Fox Road / South Lee Highway title and membership.
- Rows `29941` and `29942` kept seizure incident `3887` on the supported Cleveland High School call set and excluded unrelated cafeteria-detail call `798409`.
- Row `29943` created incident `3891` from calls `799211` and `799213`, a supported fall dispatch for a 74-year-old female around `6307 C Haven` near Lake Peninsula and a noisy `Sandu` / `Sandwich` Drive phrase. The title is awkward but grounded in retained transcript text.
- Row `29936` created incident `3890`, a supported fire/lift-assist style call at `6931 Anabela Lane` / noisy `a view lane`, with a 73-year-old male needing help up. The incident is acceptable, but the exact location spelling remains transcript-noisy.
- Rows `29933`, `29934`, `29937`, and `29940` were reviewed as acceptable rejects or watch-only: weak medical/logistics pairs, the repeated Highway 64 single-vehicle candidate without enough crash wording, and low-similarity unrelated medical/service fragments.

Action taken:
- No code change. No new post-boundary row showed a high-confidence membership regression, deployment issue, queue problem, or AI routing failure.
- Updated this watchlist and final report section.

Residual risk:
- Watch for repeated awkward but grounded location titles such as `Lake Peninsula And Sandu Drive` and `2500 South Southeast`; one-off transcript noise is not enough for a safe title-normalization change.
- Continue watching the Highway 64 / Melbourne Brewer Road `single vehicle` pattern. The current behavior correctly avoids broad acceptance, but a repeated crash-dispatch pattern may justify guarded traffic wording later.

### 2026-06-18 19:25Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T19:25Z`; deployment boundary changed to `2026-06-18T19:32:55Z`.

Observed:
- Health remained clean after deployment: `pizzad` active since `2026-06-18 15:32:55 EDT`, `0` restarts, queue depth `0`, queue pressure false, no pending transcriptions, no pending audio, no AI blocked reason, embeddings enabled, Qdrant OK, embedding endpoint OK, embedding queue `0`, failed embeddings `0`, pending embeddings `0`, and no embedding error.
- Last-hour quality showed `602` calls, `44` AI requests, `43/44` AI successes, no truncated AI responses, `25` verifier runs, average verifier truncation `0.32`, maximum verifier truncation `5`, and `0` retention mismatches.

Evidence reviewed:
- Rows `29944` and `29945` created fire-assistance incident `3892` from call `799031`, with retained evidence around `331` or `371 Borders Loop` and noisy `off a date and pike` wording.
- Row `29936` had separately created incident `3890` around `6931 Anabela Lane` / noisy `a view lane`; row `29947` then merged `3890` into `3892` through server sibling repair.
- Transcript review showed the row `29947` sibling repair was a false join: the retained call set mixed Borders Loop / Dayton Pike fire assistance with Anabela Lane lift-assist evidence. This disproved the prior sibling-repair checks because the conflicting location signal could live in weak transcript extraction or incident narrative rather than the strict positive membership anchors.
- Rows `29952`, `29953`, `29960`, and `29961` kept Memorial Station 15 locker-fire incident `3893` on calls `799299` and `799333` while excluding weak call `799451`; the repeated conflict-guard reject on the same retained call set appears harmless so far because the accepted membership remains correct.
- Immediate post-deploy row `29962` was a normal update to existing single-call incident `3894` with call `798835`; no sibling repair, location conflict, queue issue, or deployment regression appeared after the new boundary.

Action taken:
- Changed sibling-incident merge repair so call and incident narrative location anchors can block a repair when the target and duplicate incidents have incompatible locations. These anchors are used only as merge blockers, not as positive membership evidence.
- Added `loop` to deterministic street suffix handling across call-anchor extraction and transcript location plausibility, so addresses such as `331 Borders Loop` can participate in conflict detection.
- Added focused tests for the false Borders Loop / Anabela Lane sibling merge and a same-location Anabela Lane variant.
- Tests passed: focused incident membership/narrative/validator filter `45/45`; full `pizzad.Tests` suite `123/123`.
- Deployed to OT through `scripts/deploy_pizzad_tar.ps1` at boundary `2026-06-18T19:32:55Z`.

Residual risk:
- Incident `3892` still has stale bad membership from row `29947` because direct production data repair, purge, and backfill are outside this bake authority.
- Watch for true duplicate sibling incidents left unmerged because narrative or weak transcript location blockers are too strict, especially noisy same-road or same-apartment phrasing.
- Watch for repeated harmless-looking existing-anchor conflict rejects on already-correct incidents such as `3893`; one duplicate reject with a correct accepted membership is not enough for a new code change.

### 2026-06-18 19:40Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T19:40Z`; deployment boundary unchanged at `2026-06-18T19:32:55Z`.

Observed:
- Health remained clean: `pizzad` active since `2026-06-18 15:32:55 EDT`, `0` restarts, queue depth `1`, queue pressure false, `2` pending transcriptions, `26` pending audio seconds, no AI blocked reason, embeddings enabled, Qdrant OK, embedding endpoint OK, embedding queue `0`, failed embeddings `0`, pending embeddings `0`, and no embedding error.
- Last-hour quality showed `596` calls, `45` AI requests, `44/45` AI successes, no truncated AI responses, `26` verifier runs, average verifier truncation `0.12`, maximum verifier truncation `2`, and `0` retention mismatches.

Evidence reviewed:
- Post-boundary row `29962` was a routine update to existing single-call incident `3894` with call `798835`; no location conflict or sibling repair was involved.
- Post-boundary row `29963` created incident `3895`, `Power line fire at 5-6 S Washington St`, from calls `799715`, `799731`, and `799839`. Transcript review supports a common Squad 7 tree or limb on fire under or across power lines near South Washington Street, with noisy address variants including `568`, `5, 8`, and `5-6`.
- No post-boundary sibling repair row appeared. The only sibling repair in the hour remained pre-boundary row `29947`, the already documented false merge into stale incident `3892`.
- Current incident `3893` still retained only calls `799299` and `799333`; repeated conflict-guard rejects on that already-correct call set remain watch-only.

Action taken:
- No code change. The latest deployment has an early clean signal: normal creates and updates continued, no new sibling merge was attempted, and no immediate over-block or queue/runtime issue appeared.
- Updated this watchlist and final report section.

Residual risk:
- Incident `3895` has noisy but supported address wording. Do not broaden title normalization from this single example; keep watching for repeated hyphenated spoken-number address artifacts.
- Incident `3892` remains stale with `799059` and `799089` attached to `799031` because manual production repair remains outside bake authority.
- The sibling narrative-location blocker still needs a direct live proof where a post-boundary duplicate sibling candidate is rejected, or a true same-location sibling is allowed.

### 2026-06-18 19:55Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T19:55Z`; deployment boundary unchanged at `2026-06-18T19:32:55Z`.

Observed:
- Health remained clean: `pizzad` active since `2026-06-18 15:32:55 EDT`, `0` restarts, queue depth `0`, queue pressure false, no pending transcriptions, no pending audio, no AI blocked reason, embeddings enabled, Qdrant OK, embedding endpoint OK, embedding queue `0`, failed embeddings `0`, pending embeddings `0`, and no embedding error.
- Last-hour quality showed `603` calls, `40` AI requests, `39/40` AI successes, no truncated AI responses, `23` verifier runs, average verifier truncation `0.04`, maximum verifier truncation `1`, and `0` retention mismatches.

Evidence reviewed:
- Rows `29964` through `29970` updated chest-pain incident `3889` to calls `798769`, `798797`, `798799`, and `799053`, while excluding weak/unrelated call `798881`.
- Transcript review supports keeping `799053` as a same-event transport/return-to-service continuation: `Nation's loaded for transport with EMS`, fire command returning, and command termination language. Excluding `798881` is acceptable because it only has weak on-scene/command wording around the noisy room or apartment `316` phrase.
- Rows `29967` and `29968` again kept locker-fire incident `3893` on calls `799299` and `799333` while rejecting a conflicting update; this remains watch-only because accepted membership is still correct.
- No post-boundary sibling repair row appeared. The only sibling repair in the hour remained pre-boundary row `29947`.

Action taken:
- No code change. No evidence supported another algorithm change, and health remained clean.
- Updated this watchlist and final report section.

Residual risk:
- Incident `3889` still has awkward but grounded title text, `2500 South Southeast`; the added transport continuation does not by itself justify a title-normalization change.
- Incident `3892` remains stale with unrelated `799059` and `799089` attached to `799031` because manual production repair remains outside bake authority.
- The sibling narrative-location blocker still needs direct live proof on a post-boundary sibling repair candidate.

### 2026-06-18 20:10Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T20:10Z`; deployment boundary unchanged at `2026-06-18T19:32:55Z`.

Observed:
- Health remained clean: `pizzad` active since `2026-06-18 15:32:55 EDT`, `0` restarts, queue depth `1`, queue pressure false, `2` pending transcriptions, `49` pending audio seconds, no AI blocked reason, embeddings enabled, Qdrant OK, embedding endpoint OK, embedding queue `0`, failed embeddings `0`, pending embeddings `0`, and no embedding error.
- Last-hour quality showed `602` calls, `46` AI requests, `41/46` AI successes, no truncated AI responses, `24` verifier runs, average verifier truncation `0.54`, maximum verifier truncation `3`, and `0` retention mismatches.
- The five AI failures were clustered around `2026-06-18T19:59Z` through `20:00Z`: one `peer_keepalive_timeout`, three `No models loaded` HTTP 400 responses, and one cancellation at deploy boundary time. AI requests resumed successfully from `20:03Z` through `20:08Z`, so this is watch-only for now.

Evidence reviewed:
- Rows `29971` through `29980` around incident `3895` showed a transient mixed-candidate issue. Call `799877` appears to describe a separate St. Elmer / FEMA Avenue wires or transformer event, not the South Washington Street power-line fire saved as incident `3895`.
- Row `29971` briefly accepted `799877` into `3895`, but later rows `29972`, `29978`, and `29980` rejected broader updates. Current incident `3895` now retains only supported South Washington calls `799715`, `799731`, and `799839`; no manual repair is needed.
- Call `799871` was also reviewed and appears to be the same St. Elmer / West 45th or 46th wires event as `799877`; it was excluded from `3895`.
- Rows `29973` and `29974` rejected weak fall or medical-transport mixed candidates against incident `3891`; current `3891` membership remains the supported two-call fall set.
- No post-boundary sibling repair row appeared. The only known bad sibling repair remains stale pre-boundary incident `3892`.

Action taken:
- No code change. The only new bad signal was transient and current saved incident state self-corrected through normal live processing.
- Updated this watchlist and final report section.

Residual risk:
- The transient `3895` / `799877` acceptance suggests same-event-type utility or fire calls at different noisy street locations can still briefly enter an existing incident before later validation rejects them. Do not change code from this single self-corrected example; keep watching for persistence or repeats.
- The local model route had a short `No models loaded` / keepalive failure cluster but recovered without queue pressure or ongoing AI blockage. Notify only if it recurs or begins affecting incident creation.
- The sibling narrative-location blocker still needs direct live proof on a post-boundary sibling repair candidate.

### 2026-06-18 20:25Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T20:25Z`; deployment boundary unchanged at `2026-06-18T19:32:55Z`.

Observed:
- Health remained clean: `pizzad` active since `2026-06-18 15:32:55 EDT`, `0` restarts, queue depth `2`, queue pressure false, `3` pending transcriptions, `42` pending audio seconds, no AI blocked reason, embeddings enabled, Qdrant OK, embedding endpoint OK, embedding queue `0`, failed embeddings `0`, pending embeddings `0`, and no embedding error.
- Last-hour quality showed `577` calls, `48` AI requests, `43/48` AI successes, no truncated AI responses, `22` verifier runs, average verifier truncation `0.68`, maximum verifier truncation `3`, and `0` retention mismatches.
- The AI failure count still reflects the prior `19:59Z` to `20:00Z` local-model keepalive / `No models loaded` cluster. The latest nineteen AI requests from `20:02Z` through `20:25Z` succeeded, including three recovered-after-truncation-split requests.

Evidence reviewed:
- Rows `29981` through `29984`, `29987`, and `29988` rejected weak traffic, test-radio, routine-rollup, and unanchored single-call candidates. None showed an over-acceptance regression.
- Rows `29985` and `29986` repeated the watch-only South Washington power-line pattern: an accepted audit row considered retaining separate St. Elmer / FEMA Avenue wires call `799877` and likely unrelated call `799951` with incident `3895`, then a paired reject blocked the broader rollup. Current incident `3895` still retains only supported South Washington calls `799715`, `799731`, and `799839`.
- The `3895` state is currently clean despite the repeated transient candidate acceptance. No manual repair is needed and no code change is justified without a persistent save or a repeat that survives current-state validation.
- No post-boundary sibling repair row appeared. The latest sibling narrative-location guard remains pending a direct proof row.

Action taken:
- No code change. Current saved incident state, queue health, AI recovery, embeddings, and Qdrant are clean.
- Updated this watchlist and final report section.

Residual risk:
- The repeated `3895` transient candidate acceptance remains the main watch item. If a future run shows `799877`, `799951`, or another different-location utility/fire call persisting on `3895`, treat it as a high-confidence membership regression.
- The short local-model failure cluster has recovered, but repeated `No models loaded` errors should be escalated if they recur in the newest requests.
- The sibling narrative-location blocker still needs direct live proof on a post-boundary sibling repair candidate.

### 2026-06-18 20:40Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T20:40Z`; deployment boundary advanced to `2026-06-18T20:46:09Z`.

Observed:
- Health before deploy was clean: `pizzad` active since `2026-06-18 15:32:55 EDT`, `0` restarts, queue depth `0`, queue pressure false, no pending transcriptions, no pending audio, no AI blocked reason, embeddings enabled, Qdrant OK, embedding endpoint OK, embedding queue `0`, failed embeddings `0`, pending embeddings `0`, and no embedding error.
- Last-hour quality showed `540` calls, `48` AI requests, `44/48` AI successes, no truncated AI responses, `22` verifier runs, average verifier truncation `0.82`, maximum verifier truncation `3`, and `0` retention mismatches. The remaining AI failures were still from the old recovered local-model outage cluster.
- After deploy, `pizzad` was active with `0` restarts, queue depth `0`, queue pressure false, `1` pending transcription, `36` pending audio seconds, embeddings and Qdrant OK, and no AI blocked reason.

Evidence reviewed:
- Rows `29990` and `29991` converted South Washington power-line incident `3895` from calls `799715`, `799731`, and `799839` down to `799715` and `799731`. Transcript review shows `799839` is a same-event on-scene South Washington command/update call with the limb on the main power line also on fire, so dropping it was a high-confidence membership regression.
- Rows `29992` and `29993` again mixed South Washington calls with unrelated utility/fire calls `799877` and `799951`; the existing concrete-anchor conflict guard rejected the broader conflicting set, but the prior accepted update had already pruned `799839`.
- Current incident `3895` still has only calls `799715` and `799731`. Manual repair remains outside bake authority, so this is residual stale state to watch for normal live correction.
- Row `29994` created incident `3896` from calls `799975` and `799989`; transcript review supports a distinct Valley View Avenue / South Street wires-down and tree-down event.
- Row `29996` created incident `3897` from calls `800893`, `800902`, `800927`, and `800941`; transcript review supports a domestic assault or physical fight at `349 Longview Drive SE`.
- No post-boundary sibling repair row appeared.

Action taken:
- Added a guarded existing-call retention path: if an already-retained call is hard-rejected by a later mixed verifier pass but still supports the existing incident narrative's concrete street location and compatible event evidence, it can be retained. The safeguard is limited to existing source-backed calls and does not make weak or narrative locations positive evidence for adding new calls.
- Added focused tests for the `3895` class case: retain a same-scene South Washington fire follow-up, but do not retain a different-location FEMA Avenue utility fire.
- Tests passed: `dotnet test pizzad.Tests/pizzad.Tests.csproj --filter AutomaticInsightsServiceMembershipTests --no-restore` passed `9/9`; full `dotnet test pizzad.Tests/pizzad.Tests.csproj --no-restore` passed `125/125`.
- Deployed to OT through `.\scripts\deploy_pizzad_tar.ps1 -HostName lilhoser@192.168.1.173 -Rid linux-x64`; deployment completed and `pizzad` is active. Updated the automation boundary to `2026-06-18T20:46:09Z`.

Residual risk:
- Existing incident `3895` remains stale until normal live processing touches it again; watch for restoration of `799839` without retaining unrelated `799871`, `799877`, `799951`, or `799829`.
- The new retention safeguard could over-retain an old source-backed call if it shares a street-name token and broad event wording with an existing narrative. Watch for false retention on existing incident updates after `20:46:09Z`.
- The sibling narrative-location blocker still needs direct live proof on a post-boundary sibling repair candidate.

### 2026-06-18 20:55Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T20:55Z`; deployment boundary unchanged at `2026-06-18T20:46:09Z`.

Observed:
- Health remained clean: `pizzad` active since `2026-06-18 16:46:09 EDT`, `0` restarts, queue depth `0`, queue pressure false, no pending transcriptions, no pending audio, no AI blocked reason, embeddings enabled, Qdrant OK, embedding endpoint OK, embedding queue `0`, failed embeddings `0`, pending embeddings `0`, and no embedding error.
- Last-hour quality showed `488` calls, `44` AI requests, `40/44` AI successes, no truncated AI responses, `20` verifier runs, average verifier truncation `0.90`, maximum verifier truncation `3`, and `0` retention mismatches.
- The latest twenty AI usage rows all succeeded; the visible failure count still comes from the older pre-recovery local-model outage cluster.

Evidence reviewed:
- No post-`20:46:09Z` incident operation rows appeared. The latest audit row remained pre-boundary row `29997` at `20:42:01Z`, a weak single-call traffic reject.
- Current incident `3895` is still stale with only calls `799715` and `799731`; same-event call `799839` has not been restored because no post-boundary live update touched the incident.
- Current incident `3896` remains a supported two-call Valley View Avenue / South Street wires-down event from calls `799975` and `799989`.
- Current incident `3897` remains a supported four-call domestic assault or active physical fight at `349 Longview Drive SE`.
- Stale pre-boundary bad memberships remain visible on incidents `3892`, `3877`, `3864`, and `3854`; no manual repair was performed.

Action taken:
- No code change. There was no post-boundary evidence to prove or disprove the `20:46:09Z` retention safeguard.
- Updated this watchlist and final report section.

Residual risk:
- The `20:46:09Z` deployment still needs direct live proof: watch for `3895` to restore `799839` without retaining different-location utility/fire calls.
- No sibling repair occurred after the `19:32:55Z` boundary, so the sibling narrative-location blocker also remains pending direct proof.
- Current health is clean, but the lack of post-deploy incident operations means this run is observational only.

### 2026-06-18 21:10Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T21:16Z`; deployment boundary advanced to `2026-06-18T21:14:20Z`.

Observed:
- Pre-fix health was clean: queue depth `0`, no pending transcriptions, no pending audio, no AI blocked reason, `pizzad` active with `0` restarts, Qdrant active, embeddings OK, embedding endpoint OK, embedding queue `0`, failed embeddings `0`, pending embeddings `0`, and no embedding error.
- Pre-fix quality showed `436` calls, `37/37` AI successes, no AI truncation, `19` verifier runs, average verifier truncation `0.32`, maximum verifier truncation `3`, and `0` retention mismatches.
- Post-deploy health stayed clean: `pizzad` active since `2026-06-18 17:14:20 EDT`, queue depth `0`, no pending transcriptions or audio, Qdrant active, embeddings OK, and no embedding errors.
- Post-deploy quality showed `416` calls, `34/35` AI successes, no AI truncation, `17` verifier runs, average verifier truncation `0.35`, maximum verifier truncation `3`, and `0` retention mismatches. The single AI failure was `The operation was canceled` at `21:14:19Z`, exactly during the deploy restart window; subsequent AI rows at `21:14:55Z` and `21:15:11Z` succeeded.

Evidence reviewed:
- Row `30002` created incident `3900`, `Traffic control on street`, from calls `801161` and `801169`. The retained transcripts only supported generic Cleveland Police traffic or status chatter: `take that on the street` and `6-7s traffic`. There was no concrete event anchor, landmark, crash, injury, fire, EMS, or other strong event evidence. This was a high-confidence false incident.
- Current incident `3900` remains stale as an active pre-fix incident with calls `801161` and `801169`; no manual repair was performed.
- Supported nearby incidents stayed reasonable: `3898` fire alarm at `4119 Highway 1`, `3901` unconscious patient on the `900 block Central Ave`, and `3902` MVC with injuries at `McCauley Ave and Derby St`.
- Row `30010`, after the `21:14:20Z` deployment, updated supported MVC incident `3902` with calls `801449`, `801451`, and `801501`, so the new guard did not block normal traffic-crash processing.

Action taken:
- Added a narrow validator guard that rejects generic traffic-control/status chatter when there is no strong incident signal and no concrete or landmark event anchor.
- Added focused tests proving the row-`30002` class false incident is rejected while real crash-related traffic control with a concrete address remains valid.
- Tests passed: `dotnet test pizzad.Tests/pizzad.Tests.csproj --filter IncidentCandidateValidatorTests --no-restore` passed `34/34`; full `dotnet test pizzad.Tests/pizzad.Tests.csproj --no-restore` passed `127/127`.
- Deployed to OT through `.\scripts\deploy_pizzad_tar.ps1 -HostName lilhoser@192.168.1.173 -Rid linux-x64`; deployment completed and `pizzad` is active. Updated the automation boundary to `2026-06-18T21:14:20Z`.

Residual risk:
- Watch for row-`30002` class generic traffic-control/status chatter still being saved after `21:14:20Z`.
- Watch for false over-rejection of legitimate crash, fire, EMS, utility, or police events that include traffic-control wording but have strong event evidence or concrete anchors.
- Incident `3900` remains a stale pre-fix false incident until normal live retention or conclusion behavior handles it; no manual repair was performed.
- The `20:46:09Z` existing-call retention guard still needs direct live proof on incident `3895`.

### 2026-06-18 21:25Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T21:30Z`; deployment boundary advanced to `2026-06-18T21:30:03Z`.

Observed:
- Pre-fix health was acceptable: queue depth `1`, `2` pending transcriptions, `44` pending audio seconds, no queue pressure, no AI blocked reason, `pizzad` active since `2026-06-18 17:14:20 EDT`, `0` restarts, Qdrant active, embeddings OK, embedding queue `0`, failed embeddings `0`, pending embeddings `0`, and no embedding error.
- Pre-fix quality showed `412` calls, `32/33` AI successes, no AI truncation, `17` verifier runs, average verifier truncation `0.18`, maximum verifier truncation `3`, and `0` retention mismatches. The visible AI failure remained the deploy-window cancellation from the prior run.
- Post-deploy health stayed clean: `pizzad` active since `2026-06-18 17:30:03 EDT`, queue depth `0`, queue pressure false, no blocked reason, Qdrant active, embeddings OK, failed embeddings `0`, pending embeddings `1`, and no embedding error.

Evidence reviewed:
- Post-`21:14:20Z` row `30011`/`30012` updated MVC incident `3902` at `McCauley Ave and Derby St`, retaining calls `801449`, `801451`, and `801501` while excluding weak Memorial handoff call `801163`. This is positive evidence that the generic traffic-control guard did not block a real traffic-crash update.
- Row `30014` rejected call `801557`, a concrete police dispatch to `1720 Newcastle Drive NE` for a vehicle parked in the middle of the road and described as a hazard. This was a false reject caused by operational-chatter filtering seeing `parking` before road-hazard evidence.
- Row `30013` rejected a mixed candidate containing unrelated LifeForce handoff call `801075` and THP call `801113`, where `801113` described a tractor-trailer or tanker versus a guardrail on `I-24 east of the 163`, blocking lane one. This was a false leftover miss because the transcript did not use the existing crash, accident, or MVC wording.
- Rows `30016` through `30019`, before the new deployment boundary, only updated already supported incidents `3898`, `3901`, and `3902`; no new sibling repair or existing-anchor conflict appeared.

Action taken:
- Added bounded road-hazard evidence for vehicle parked, stopped, or disabled in the roadway or blocking the roadway.
- Added bounded vehicle-versus-fixed-object crash evidence for vehicle, truck, tractor-trailer, tanker, or trailer versus guardrail, barrier, pole, tree, ditch, wall, or fence.
- Applied those concepts consistently across validator strong signals, validator event concepts, narrative fallback grounding, dominant event class, and category normalization.
- Added focused tests for row-`30014` vehicle-in-roadway hazard acceptance, row-`30013` vehicle-versus-guardrail crash acceptance, generic traffic-control false-positive rejection, and narrative fallbacks.
- Tests passed: `dotnet test pizzad.Tests/pizzad.Tests.csproj --filter "IncidentCandidateValidatorTests|IncidentNarrativeGrounderTests" --no-restore` passed `44/44`; full `dotnet test pizzad.Tests/pizzad.Tests.csproj --no-restore` passed `131/131`.
- Deployed to OT through `.\scripts\deploy_pizzad_tar.ps1 -HostName lilhoser@192.168.1.173 -Rid linux-x64`; deployment completed and `pizzad` is active. Updated the automation boundary to `2026-06-18T21:30:03Z`.

Residual risk:
- Watch for row-`30014` class concrete vehicle-in-roadway hazards still being rejected as operational chatter.
- Watch for row-`30013` class vehicle/truck/tanker/trailer versus fixed-object traffic crashes still being rejected when transcripts omit crash, accident, or MVC wording.
- Watch for false positives from incidental parking or versus wording without public roadway hazard context or vehicle plus fixed-object context.
- The `20:46:09Z` existing-call retention guard on incident `3895` and the `21:14:20Z` generic traffic-control guard still need direct post-boundary proof.
- Stale pre-fix false incident `3900` remains visible; no manual repair was performed.

### 2026-06-18 21:40Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T21:43Z`; deployment boundary advanced to `2026-06-18T21:43:15Z`.

Observed:
- Pre-fix health was clean: queue depth `0`, no pending transcriptions, no pending audio, no AI blocked reason, `pizzad` active since `2026-06-18 17:30:03 EDT`, `0` restarts, Qdrant active, embeddings OK, embedding queue `0`, failed embeddings `0`, pending embeddings `0`, and no embedding error.
- Quality showed `432` calls, `36/37` AI successes, no AI truncation, latest AI use at `21:39:57Z`, `22` verifier runs, average verifier truncation `0`, maximum verifier truncation `0`, and `0` retention mismatches. The lone AI failure remained the prior deployment-window cancellation.
- Post-deploy health stayed acceptable: `pizzad` active since `2026-06-18 17:43:15 EDT`, `0` restarts, queue depth `5`, `6` pending transcriptions, `100` pending audio seconds, queue pressure false, no blocked reason, Qdrant active, embeddings OK, embedding queue `0`, failed embeddings `0`, pending embeddings `0`, and no embedding error.

Evidence reviewed:
- Rows `30030` and `30031` proved partial success of the `21:30:03Z` road-hazard/fixed-object deployment: the I-24 tractor-trailer or tanker versus guardrail event was recovered from calls `801113` and `801519`, while unrelated lane-blocked call `801705` was excluded.
- The same rows exposed a title-grounding miss: incident `3903` retained supported traffic-crash calls but preserved generic title `Traffic control on street`; its detail supported `I-24 WB mile 57.2 tractor-trailer vs guardrail crash`.
- Row `30032` still rejected road-hazard call `801557` as lacking a concrete structured anchor or location hint. The transcript contained the actual address as `1720, Newcastle Drive, northeast`; the comma-separated numeric address and directional suffix were not extracted as an anchor.
- Rows `30025` through `30029` did not show sibling repair, existing-anchor replacement, retention mismatch, Qdrant, embedding, or transcription quality regressions.

Action taken:
- Added comma-tolerant numeric address extraction with optional directional suffixes in transcript anchor extraction and transcript location extraction, covering forms such as `1720, Newcastle Drive, northeast`.
- Added a narrative-grounding override so generic traffic-control titles are rewritten when retained evidence supports a specific fallback such as `Vehicle hit fixed object`.
- Added focused tests for comma-separated directional address anchors, the exact Newcastle Drive vehicle-in-roadway hazard, and generic traffic-control title rewrite for the I-24 truck-versus-guardrail event.
- Tests passed: `dotnet test pizzad.Tests/pizzad.Tests.csproj --filter "CallAnchorExtractionServiceTests|IncidentCandidateValidatorTests|IncidentNarrativeGrounderTests" --no-restore` passed `50/50`; full `dotnet test pizzad.Tests/pizzad.Tests.csproj --no-restore` passed `133/133`.
- Deployed to OT through `.\scripts\deploy_pizzad_tar.ps1 -HostName lilhoser@192.168.1.173 -Rid linux-x64`; deployment completed and `pizzad` is active. Updated the automation boundary to `2026-06-18T21:43:15Z`.

Residual risk:
- Watch for row-`30032` class comma-separated directional addresses still failing validator location hints.
- Watch for row-`30031` class true vehicle-versus-fixed-object incidents still preserving generic traffic-control titles after normal live updates.
- Watch for false address anchors from comma-separated non-address numbers and false title rewrites where generic traffic-control wording is actually the best supported title.
- Stale pre-fix incidents `3900` and `3903` remain visible until normal live processing touches or concludes them; no manual repair was performed.
- The `20:46:09Z` existing-call retention guard on incident `3895` still needs direct live proof.

### 2026-06-18 21:55Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T22:03Z`; deployment boundary advanced to `2026-06-18T22:03:01Z`.

Observed:
- Pre-fix health was clean: queue depth `1`, pending transcriptions `2`, no queue pressure, no AI blocked reason, `pizzad` active since `2026-06-18 17:43:15 EDT`, Qdrant active, embeddings OK, embedding queue `0`, failed embeddings `0`, pending embeddings `0`, and no embedding error.
- Pre-fix quality showed `472` calls, `38/39` AI successes, no AI truncation, `23` verifier runs, average verifier truncation `0`, maximum verifier truncation `0`, and `0` retention mismatches. The one AI failure was still the older deployment-window cancellation.
- Post-deploy health stayed clean: `pizzad` active since `2026-06-18 18:03:01 EDT`, queue depth `0`, no pending transcriptions, no pending audio, no queue pressure, no AI blocked reason, Qdrant active, embeddings OK, embedding queue `0`, failed embeddings `0`, pending embeddings `0`, and no embedding error.
- Post-deploy quality showed `476` calls, `38/39` AI successes, no AI truncation, `22` verifier runs, average verifier truncation `0`, maximum verifier truncation `0`, and `0` retention mismatches. The single AI failure remained pre-deploy.

Evidence reviewed:
- Rows `30040` through `30042` proved the `21:43:15Z` deployment: incident `3904` was created from call `801557` as `Tipped vehicle hazard on Newcastle Dr NE`, and incident `3903` was rewritten to `Vehicle hit fixed object at I-24` from calls `801113` and `801519`.
- Rows `30036` through `30038` exposed a separate false-reject path: calls `801263`, `801471`, and `801565` were rejected solely because the model supplied unknown synthetic incident IDs. Transcript review found plausible standalone events: tree-down traffic control near Walnut/Daisy Dallas, Highway 27 windshield damage from an object thrown by a truck, and a vehicle burglary at `3115 Dodson Avenue`.
- Row `30043` created supported incident `3905`, an MVC with injuries at South Seminole Drive and Brainerd Road, without verifier truncation or membership drift.
- Row `30044` correctly rejected call `801357` as medical transport or EMS assist context without parent emergency evidence.
- Post-boundary row `30045` cleanly updated existing MVC incident `3902` with calls `801449`, `801451`, and `801501`; it did not exercise the unknown-model-ID path and did not show membership drift.
- No post-deploy candidate had yet exercised the unknown-model-ID path, so immediate proof for that branch remains pending; current regression evidence is limited to clean service health, one clean normal update, and the absence of new queue, AI, verifier, embedding, or Qdrant failures.

Action taken:
- Changed incident identity handling so unknown model-supplied incident IDs are ignored after server-side evidence matching; new incidents now receive server-owned keys instead of being hard-rejected solely because the model invented or shortened an ID.
- Applied the same rule in both the high-level target resolver and `IncidentIdentity.ResolveManagedIncidentKey`.
- Broadened guarded evidence and fallback wording for vehicle burglary or theft, tree-down roadway hazards, and windshield-hit roadway damage so the validator, event concepts, dominant evidence class, category normalization, and narrative grounding stay aligned.
- Added focused tests for unknown synthetic incident IDs, concrete vehicle burglary, highway windshield damage, tree-down traffic-control hazards with extractable intersection text, and narrative fallbacks for burglary and tree-down hazards.
- Tests passed: `dotnet test pizzad.Tests/pizzad.Tests.csproj --filter "IncidentIdentityTests|IncidentCandidateValidatorTests|IncidentNarrativeGrounderTests" --no-restore` passed `59/59`; full `dotnet test pizzad.Tests/pizzad.Tests.csproj --no-restore` passed `138/138`.
- Deployed to OT through `.\scripts\deploy_pizzad_tar.ps1 -HostName lilhoser@192.168.1.173 -Rid linux-x64`; deployment completed and `pizzad` is active. The new boundary is `2026-06-18T22:03:01Z`.

Residual risk:
- Watch for `rejected:unknown model incident_id` after `22:03:01Z`; it should no longer be a hard-reject reason when server-side evidence supports a create or update.
- Watch for duplicate incidents from ignored unknown IDs if the model supplies a malformed ID for an actually existing incident and evidence matching fails.
- Calls `801263`, `801471`, and `801565` remain pre-fix rejects; no backfill or manual repair was performed. Normal live processing may or may not revisit them.
- Bare `Walnut and Daisy Dallas` did not become a positive concrete intersection anchor; that remains intentionally unfixed until there is stronger evidence that broad bare-name intersections are safe.

### 2026-06-18 22:10Z Autonomous Bake Run

Window: latest hour ending around `2026-06-18T22:13Z`; deployment boundary advanced to `2026-06-18T22:13:07Z`.

Observed:
- Pre-fix health was clean: queue depth `0`, no queue pressure, no AI blocked reason, `pizzad` active since `2026-06-18 18:03:01 EDT`, Qdrant active, embeddings OK, embedding queue `0`, failed embeddings `0`, pending embeddings `0`, and no embedding error.
- Pre-fix quality showed `499` calls, `35/36` AI successes, no AI truncation, `19` verifier runs, average verifier truncation `0`, maximum verifier truncation `0`, and `0` retention mismatches. The one AI failure was still the older deployment-window cancellation.
- Post-deploy health stayed clean: `pizzad` active since `2026-06-18 18:13:07 EDT`, queue depth `0`, queue pressure false, no blocked reason, Qdrant active, embeddings OK, failed embeddings `0`, and no embedding error. Runtime briefly showed `1` pending embedding and health briefly showed `1` pending transcription / `15` pending audio seconds, both without pressure.
- Post-deploy quality still reflected mostly pre-boundary operations: `499` calls, `34/35` AI successes, no AI truncation, `19` verifier runs, average verifier truncation `0.05`, maximum verifier truncation `1`, and `0` retention mismatches.

Evidence reviewed:
- Rows `30047` and `30048` updated MVC incident `3905`, retaining calls `802129` and `802133` while excluding unrelated call `802153`. This is a clean pre-boundary update and no create/update collapse appeared.
- Row `30046` created incident `3906`, a real CPR/non-breathing emergency from call `802453`, but saved the unsupported title location `804 Continue St SE`. The transcript was `Engine 3 ... 804, continue the street southeast ... CPR in progress, 6 year old female, turning blue...`; `continue the street southeast` is command-like transcript text, not a reliable street name.
- Current incidents `3903`, `3904`, and `3905` remained supported by retained transcripts. Stale pre-fix incident `3900` remains concluded and visible.
- No post-`22:13:07Z` incident candidate had yet exercised the new location guard, so direct live proof is pending.

Action taken:
- Added a narrow location plausibility guard that rejects numeric and bare street-like locations where the street name is actually dispatch command/status wording such as `continue the street`, `copy street`, `clear street`, `route street`, `respond street`, or `showing street`.
- Made narrative grounding reject numeric title/detail locations that fail the shared plausibility check, so a real emergency can still be accepted but fallback titles do not retain command-word street names.
- Added focused tests for transcript anchor extraction, transcript location extraction, and narrative fallback on the `804, continue the street southeast` CPR case.
- Tests passed: `dotnet test pizzad.Tests/pizzad.Tests.csproj --filter "CallAnchorExtractionServiceTests|IncidentCandidateValidatorTests|IncidentNarrativeGrounderTests" --no-restore` passed `58/58`; full `dotnet test pizzad.Tests/pizzad.Tests.csproj --no-restore` passed `141/141`.
- Deployed to OT through `.\scripts\deploy_pizzad_tar.ps1 -HostName lilhoser@192.168.1.173 -Rid linux-x64`; deployment completed and `pizzad` is active. The new boundary is `2026-06-18T22:13:07Z`.

Residual risk:
- Incident `3906` remains stale with the pre-fix `804 Continue St SE` title until normal live processing touches or concludes it; no manual repair was performed.
- Watch for row-`30046` class command-word street names still entering anchors, transcript locations, or titles after `22:13:07Z`.
- Watch for over-blocking of legitimate street names that begin with words like `Continue`, though that appears low risk compared with the command phrase `continue the street`.
- The `22:03:01Z` unknown-model-ID deployment still needs direct live proof on a post-boundary candidate with an unknown synthetic incident ID.

## Final Report Draft

Working summary so far:
- The infrastructure side stayed healthy through the bake: service health, queue pressure, transcription throughput, Qdrant, embeddings, AI routing, and verifier truncation were not the main source of trouble.
- The main failures were server-side incident evaluation issues: membership was being rewritten too late, weak transcript locations became hard conflicts, title support allowed generic token overlap to hide unsupported concrete event types, routine-status filtering could override corroborated emergency evidence, verifier review could miss same-emergency continuation calls that lacked the one concrete address, same-address emergency calls could be dropped when they crossed police, fire, or EMS talkgroups, spoken address numbers did not match numeric address numbers, repeated police BOLO or welfare-check relays could be dropped when identity evidence was noisy, broad category equality allowed unrelated same-category cross-talkgroup calls to join, existing bad members could survive because final validation proved a smaller subset but persistence kept the larger saved set, concrete single-call events were sometimes rejected because the validator taxonomy missed welfare-check and vehicle-hit-fixed-object wording, legitimate same-event continuation calls could be dropped when they did not repeat the strongest location anchor, sibling-merge repair could temporarily union unrelated calls into a larger incident when it relied on semantic similarity, continuation-token linkage could still cross event types when broad talkgroup labels polluted event-class detection, and noisy transcript words such as bare `fire` could wrongly override stronger event evidence.
- Deployed corrections so far: final validation no longer rewrites membership solely to a smaller validator subset; weak transcript-derived locations no longer create hard final conflicts; title grounding now requires concrete event types to be supported by retained evidence; emergency evidence now wins over routine-status rejection when multiple calls corroborate the emergency; close same-emergency candidates can now be sourced from recent calls and sent to verifier review even when only one retained call has a concrete location; same-address emergency evidence can now link across police, fire, and EMS operational contexts when the event class and address number match; spoken street-address numbers now participate in the same-address link, including a short gap between the number and street suffix; repeated police BOLO or welfare-check broadcasts can now link through shared identity tokens; broad category equality no longer counts as same operational context for membership; validator-unmatched calls now need concrete server-side linkage before they can survive final persistence; grounded single-call welfare checks and vehicle-hit-fixed-object reports now count as strong incident signals when they also have concrete anchors; close same-system continuation calls can now link through distinctive shared event tokens or explicit same-event references when the retained evidence still passes final membership; sibling-merge repair now requires every call in the sibling incident to have strong non-semantic server evidence before merging; event-class compatibility now comes from transcript event evidence; generic location or agency words no longer count as distinctive continuation tokens; bare `fire` no longer overrides stronger police, medical, traffic, or road-hazard event evidence; model-labeled medical transport candidates can now proceed when the retained transcript contains parent medical-emergency symptoms; unknown model-supplied incident IDs are ignored after evidence matching and replaced with server-owned incident keys instead of causing hard rejection.
- Proven improvement: the unsupported pursuit title on incident `3827` was rewritten to a vehicle-off-roadway title after the `03:09:22Z` deploy.
- Proven improvement: the possible overdose rejected at row `29422` created as incident `3829` after the `03:23:09Z` deploy, but it created with only one of three likely same-event calls.
- Proven improvement: after the `05:39:56Z` deploy, rows `29497` and `29498` pruned unrelated Rhea Sheriff call `788445` from Rockford Lane incident `3834` through normal live processing.
- Proven improvement: rows `29499` and `29500` then added same-address Rockford Lane detail call `788353` while keeping unrelated `788445` out, so incident `3834` now has the correct Chattanooga Police call set.
- Proven improvement: after the `06:23:43Z` deploy, row `29522` updated Trisha Drive shots-fired incident `3837` to retain same-event continuation calls `789131`, `789151`, and `789233`; row `29523` rejected a duplicate new proposal instead of creating a sibling incident.
- Negative proof caught and fixed: row `29525` temporarily merged unrelated lift-assist call `789139` into Trisha Drive incident `3837`; the `06:38:11Z` deployment removes the semantic-only sibling-merge path that allowed that transient bad save.
- Early clean signal: after the `06:38:11Z` deploy, row `29529` kept Trisha Drive incident `3837` on the supported ten-call set without `789139`, and row `29530` rejected an already-owned Morgan Road fire-alarm duplicate instead of creating or merging a sibling.
- Negative proof caught and fixed: row `29536` merged unrelated possible overdose call `789515` into Trisha Drive incident `3837`; the `07:07:04Z` deployment makes continuation and sibling links require transcript-grounded compatible event class.
- Proven improvement: after the `07:07:04Z` deploy, normal live processing removed unrelated possible-overdose call `789515` from Trisha Drive incident `3837`.
- Negative proof caught and fixed: rows `29543` and `29544` dropped likely valid BOLO relay `789371` because noisy transcript text contained `Fire Dricia Drive`; the `07:21:28Z` deployment stops bare `fire` from overriding stronger police-event evidence.
- Early clean signal: after the `07:21:28Z` deploy, row `29547` rejected unrelated possible-overdose call `789515` instead of reattaching it to Trisha Drive, and new missing-person incident `3840` had a supported six-call broadcast set.
- Negative proof caught and fixed: rows `29550` and `29554` dropped supported missing-person relay calls `789831` and `789865` from incident `3840`; the `07:55:16Z` deployment broadens grounded missing-person broadcast wording and allows stronger identity-token overlap to link noisy same-event relays.
- Proven improvement: after the `07:55:16Z` deploy, row `29557` added supported missing-person relay call `789865` to incident `3840` while still excluding weak closing/status call `789777`.
- Negative proof caught and fixed: rows `29560` and `29561` rejected concrete single-call domestic or weapon incidents because routine-status wording could override stronger event evidence and because domestic dispatch wording was absent from the strong single-call taxonomy; the `08:07:37Z` deployment corrects both paths.
- Early clean signal: after the `08:07:37Z` deploy, row `29565` rejected a location and vehicle-description update without a strong incident event, so the new domestic and weapon taxonomy did not broaden that weak case.
- Negative proof caught and fixed: row `29566` rejected a symptom-bearing EMS dispatch at `370 Cleo Circle` because the model labeled it `medical_transport_context`; the `08:37:11Z` deployment lets parent medical-emergency symptoms override the standalone-logistics prefilter and broadens missing symptom wording.
- Proven improvement: after the `21:43:15Z` deploy, rows `30040` through `30042` created the Newcastle Drive vehicle-in-roadway hazard and rewrote the I-24 fixed-object crash away from the stale generic traffic-control title.
- Negative proof caught and fixed: rows `30036` through `30038` rejected plausible standalone events solely because the model supplied unknown synthetic incident IDs; the `22:03:01Z` deployment ignores unknown IDs after server-side evidence matching and mints server-owned keys.
- Negative proof caught and fixed: row `30046` saved a real CPR incident with an unsupported `804 Continue St SE` location from command-like transcript text; the `22:13:07Z` deployment blocks command-word street names in transcript locations and narrative grounding.
- Negative proof caught and fixed: rows `29576` and `29577` rejected a coherent `6200 Hixson Pike` unconscious-patient event after a mixed candidate also contained already accepted `1 E 11th St` medical calls; the `08:53:51Z` deployment lets the assembler recover a standalone validated event subset after excluding owned and hard-rejected calls.
- Negative proof caught and fixed: post-recovery row `29583` still rejected likely Common Spirit ER stab-wound calls because `stab` and `stabbed` wording were not treated as strong event evidence; the `08:59:19Z` deployment adds those general stabbing variants to event validation and narrative grounding.
- Negative proof caught and fixed: post-boundary row `29586` still rejected the Common Spirit ER stab-wound pair because strong event wording alone did not satisfy two-call corroboration, and row `29585` rejected a concrete carbon monoxide alarm call because that event wording was absent from the taxonomy; the `09:07:24Z` deployment adds same-talkgroup hospital patient-context corroboration and carbon-monoxide alarm grounding.
- Negative proof caught and fixed: post-boundary rows `29590` and `29591` still rejected standalone carbon-monoxide and Common Spirit stab-wound evidence because recovery was disabled after target resolution selected an existing incident whose accepted calls were later verifier-rejected; row `29592` also rejected a Highway 193 cardiac event because title grounding lacked a specific non-breathing or cardiac fallback. The `09:21:45Z` deployment adds the mixed-candidate target reset and cardiac fallback grounding.
- Negative proof caught and fixed: rows `29597` and `29598` proved mixed-target recovery by creating the Common Spirit possible-stab-wound incident, but row `29599` then wrongly merged it into the unrelated `1 E 11th St` breathing incident through sibling repair. The `09:36:51Z` deployment blocks sibling merges when both incidents have specific, non-overlapping event concepts.
- Early clean signal: after the `09:36:51Z` sibling-event-concept guard, rows `29606` through `29612` showed only accepted updates to the Highway 193 medical incident and the Westonia Drive seizure incident, with no new incompatible sibling merge. The stale `3843` bad membership from row `29599` remains present because manual repair and backfill are outside the bake authority.
- Negative proof caught and fixed: rows `29613` and `29614` still dropped symptom-bearing Highway 193 cardiac call `790405` from incident `3846` because the accepted membership split event facts and address detail across neighboring calls. The first `10:08:23Z` split-dispatch-link deploy was immediately disproven by rows `29621` and `29622` because `2004, Highway 193` was not extracted as a concrete anchor; the corrected `10:11:39Z` deployment adds punctuation-tolerant road or route number support for explicit address-detail continuations.
- Early clean signal after the corrected `10:11:39Z` deploy: rows `29627` through `29632` touched only seizure incident `3845`, retained its supported four-call membership, and did not show a false split-dispatch join. The Highway 193 cardiac fix remains unproven because `3846` did not receive a post-corrected update in this run.
- Continued clean signal after the corrected `10:11:39Z` deploy: rows `29633` through `29639` showed no sibling repair, no incompatible event merge, and no false split-dispatch join. Incident `3848` stayed on its supported two-call unconscious-person set. The Highway 193 cardiac fix remains unproven because `3846` still did not receive a post-corrected update.
- Negative proof caught and fixed: row `29651` created incident `3851` as `Vehicle off roadway at 1051 High St NE`, but retained call `791021` only supported a police assault-like dispatch at `1051 High Street`. The `10:54:17Z` deployment stops bare address-like values such as `1051` from matching numeric 10-code event patterns, while still accepting explicit `10-51` or `10 51`, and adds general physical-assault wording to strong-event and title-grounding evidence.
- Negative proof caught and fixed: post-`10:54:17Z` rows around possible-stroke incident `3850` avoided saving unrelated call `790825`, but also dropped same-dispatch patient-detail call `790933` even though it followed retained address/event call `790929` by twelve seconds on the same EMS dispatch talkgroup. The `11:13:18Z` deployment adds guarded immediate same-talkgroup patient-detail linkage for this address/event-plus-patient-detail split pattern.
- Negative proof caught and fixed: post-`11:13:18Z` rows `29662` and `29664` still dropped `790933` because response-resource wording such as `fire response` made the companion detail call look like a second strong event call. The corrected `11:27:09Z` deployment ignores response-resource wording only when choosing the primary event side for this immediate patient-detail branch.
- Proven improvement: after the corrected `11:27:09Z` patient-detail deployment, rows `29671` through `29676` retained both `790929` and `790933` on possible-stroke incident `3850` while still excluding weak or unrelated nearby calls.
- Negative proof caught and fixed: row `29680` merged unrelated unresponsive-child call `791155` at `5911 Snow Hill Road / Life Care` into lift-assist incident `3854` at `8227 Trubidor Way`; the `11:42:44Z` deployment blocks sibling-incident repair when both sides have incompatible concrete membership anchors, except for strong police BOLO identity linkage.
- Negative proof caught and fixed: post-`11:42:44Z` row `29695` showed the first sibling-location guard was still too weak because sibling repair merged unrelated single-call medical incidents `3856`, `3858`, and `3857` into stale lift-assist incident `3854`. The `11:58:36Z` deployment lets weak transcript location hints block sibling repair and prevents already internally conflicting target incidents from absorbing more siblings.
- Early clean signal after the `11:58:36Z` weak-location sibling guard: rows `29696` and `29697` showed no new sibling repair, created a grounded single-call MVC incident `3859`, and left stale bad membership on `3854` unchanged rather than expanding it.
- Negative proof caught and fixed: row `29703` rejected a supported Green Pond Road rollover with injuries and power lines down because `Greenpond` and `Green Pond` transcript variants failed to share a concrete location anchor. The `12:29:07Z` deployment adds compacted concrete-location part comparison so spacing and punctuation variants can match without weakening the event, time, verifier, and final membership checks.
- Early clean signal before the `12:29:07Z` deploy: row `29704` created a supported I-75 South mile-marker-20 MVC incident with consistent vehicle, patient, and exit-ramp continuation evidence.
- Proven improvement: after the `12:29:07Z` deployment, rows `29705` and `29706` created and retained the Green Pond rollover incident `3861` with the expected police, fire, EMS, and Lifeforce call set.
- Negative proof caught and fixed: rows `29708`, `29710`, and `29711` showed the I-75 MVC incident could still be destabilized by false concrete locations from traffic-lane or exit-instruction wording and by missed highway-marker phrasings such as `I-75 South at the 20.6`. The `12:51:04Z` deployment rejects those roadway-instruction fragments as concrete locations and broadens route-first highway-marker extraction.
- Early post-boundary signal after the `12:51:04Z` roadway-fragment deployment: row `29716` kept Green Pond incident `3861` stable on its supported five-call set, and row `29717` rejected a weak mixed candidate instead of expanding `3861`. The stale I-75 MVC incident `3860` has not yet received a proving live update.
- Mixed signal and targeted fix: post-`12:51:04Z` rows `29720`, `29721`, `29728`, and `29729` showed I-75 incident `3860` partially recovering by restoring call `791977` and removing false lane-fragment call `792061`, but still dropping same-event blue-Ultima, female-patient, airbag-detail call `792105`. The `13:14:47Z` deployment adds a guarded same-talkgroup traffic-crash detail link for close crash-scene plus vehicle/patient/airbag-detail pairs with shared distinctive identity tokens.
- Negative proof caught and fixed: post-`13:14:47Z` row `29732` rejected a likely true unconscious-person event at `4830 Highway 58 apartment 118` because `118 between Oakwood Drive` was treated as a conflicting concrete location and route number `58` was lost during concrete-location comparison. The `13:29:25Z` deployment filters numeric relation-to-road fragments from concrete locations and preserves route numbers while stripping leading house numbers for compatibility checks.
- Negative proof caught and fixed: post-`13:29:25Z` row `29737` rejected a concrete Amazon or box-truck driver hit-fence report near `134 North Market Street` because fixed-object wording did not allow possessive determiners such as `their`. The `13:44:08Z` deployment broadens fixed-object wording and specific fallback grounding for possessive fixed-object collision reports.
- Negative proof caught and fixed: post-`13:44:08Z` row `29749` merged unrelated medical incidents at `82820 E 26th Street`, `18 W 28th Street`, and `3730 Fagan Street` into fall incident `3864` through server sibling repair. The `13:59:56Z` deployment requires sibling-repair unions to pass final server-owned membership validation without pruning before any repair is persisted.
- Early post-boundary signal after the `13:59:56Z` sibling-repair final-validation deployment: rows `29755` and `29756` accepted the Highway 58 unconscious-male incident `3866` with three supported calls, rows `29757` and `29758` were justified routine or weak rejects on transcript review, and no sibling repair attempted a bad union. The sibling-repair gate remains unproven because no post-boundary repair row has exercised it.
- Negative proof caught and fixed: post-`13:59:56Z` row `29769` rejected a concrete welfare check at `1 Covenant Drive NE` for a little girl screaming and possibly in distress because routine radio wording such as `copy` triggered the single-call routine filter before the validator considered the welfare-check signal and location hint. The `14:28:23Z` deployment keeps truly noisy text rejected but stops routine/status words from suppressing a strong single-call dispatch that has a concrete anchor or location hint.
- Early post-boundary signal after the `14:28:23Z` routine-word refinement: rows `29772`, `29773`, and `29777` still rejected weak single-call traffic, trespass callback, and animal-complaint/service-call candidates rather than over-accepting routine chatter. No row-`29769` class welfare-check acceptance has appeared yet, so the fix remains pending positive live proof.
- Negative proof caught and fixed: post-`14:28:23Z` row `29781` showed the routine-word refinement was insufficient because `one Covenant Drive` still did not produce a validator-visible address anchor for the same welfare-check dispatch. The `14:58:48Z` deployment adds spoken single-digit street-address anchors while keeping generic weak `location_hint` rows excluded from concrete conflict and merge decisions.
- Negative proof caught and fixed: post-`14:58:48Z` row `29787` rejected a true I-75 northbound rear-end traffic-crash event because `rear-ended` was not recognized as vehicle-accident evidence for validation or narrative fallback. The `15:13:55Z` deployment adds rear-end crash wording across validation, narrative grounding, event-class handling, and category normalization without restoring bare `1050` as a standalone trigger.
- Negative proof caught and fixed: post-`15:13:55Z` row `29791` correctly rejected the mixed pair of I-75 MVC call `794517` and unrelated Highway 193 call `794615`, but failed to recover the valid single-call I-75 MVC subset because `794517` had strong crash and roadway evidence without a validator-visible location anchor. The `15:29:27Z` deployment lets strong single-call traffic-crash candidates use guarded roadway/highway context as the location hint needed for standalone recovery.
- Negative proof caught and fixed: post-`15:29:27Z` rows `29796` and `29797` proved single-call I-75 MVC recovery but saved incident `3871` with unsupported `Oliver Ln/Hwy 193` title/location text from pruned unrelated call `794615`; rows `29800` through `29802` then merged likely same-scene rear-end call `794575` while the stale unsupported title remained. The `15:43:53Z` deployment prevents unsupported fallback titles from using model title/detail locations that are not present in retained calls, and makes compound claimed locations fail narrative support unless retained evidence contains a distinctive location token.
- Negative proof caught and fixed: after the `15:43:53Z` deployment, normal processing rewrote incident `3871` away from unsupported `Oliver Ln/Hwy 193`, proving that fix, but exposed a narrower title regression by saving `Vehicle accident at Left Side Of The Road` from retained call `794575`. The `15:57:25Z` deployment prevents generic roadway-position phrases from being used as incident title locations unless they include concrete highway or route context.
- Early post-boundary signal after the `15:57:25Z` generic roadway-title-location deployment: service health, queue, AI, embeddings, and Qdrant stayed clean; rows `29812`, `29813`, and `29815` created and updated a grounded `1335 Sonora Lane` unconscious-male incident; rows `29814` and `29816` correctly rejected weak THP tag, tire, shoulder, and mile-marker traffic chatter. Incident `3871` has not yet received a proving live update and still has stale title text.
- Continued post-boundary clean signal after the `15:57:25Z` deployment: rows `29817` and `29819` created and updated a supported `9607 Barbie Road Unit 83` possible-struggle incident; row `29818` rejected THP traffic/tag chatter; row `29820` rejected explicit non-emergency/convalescent medical transport context. No post-boundary row touched incident `3871`, so the generic roadway-position title fix remains pending live proof.
- Negative proof caught and fixed: post-`15:57:25Z` row `29824` rejected a same-event Memorial Hospital automatic-fire-alarm pair because noisy address variants `2525 Dittles Avenue` and `25 25 the sales Avenue` looked like conflicting concrete locations; row `29823` rejected a concrete overdose dispatch at Avery-Johns Park because routine update calls made the candidate look like a status rollup. The `16:47:52Z` deployment adds guarded named-landmark plus event-concept bridging for noisy concrete-location conflicts and lets one strong single-call dispatch recover after pruning routine/status update calls.
- Early post-boundary signal after the `16:47:52Z` deployment: service health, queue, AI, embeddings, and Qdrant stayed clean; rows `29834` and `29835` recovered a standalone `2626 Walker Road` heart-problems incident from a mixed candidate; rows `29845` and `29846` rejected a broader sibling merge whose union would have retained an unlinked call. The Memorial Hospital and Avery-Johns Park fixes still need direct live proof.
- Negative proof caught and fixed: rows `29847` through `29849` created a semantic/RAG-only four-call medical sibling from unrelated hospital handoff, `6698 Palms Court` bleeding, and Parkridge handoff calls, then merged it into `2626 Walker Road` incident `3877`. The final `17:17:53Z` deployment makes validator-unmatched final membership require strong server-side linkage, blocks sibling repair when validation only proves a subset, and rejects unanchored transport/hospital-handoff-heavy clusters unless one concrete non-transport dispatch can be recovered.
- Early post-boundary signal after the `17:17:53Z` deployment: rows `29858` and `29859` kept incident `3880` on the single concrete South State Creek elevated-heart-rate dispatch and excluded unrelated Erlanger hospital handoff calls, so the new transport/handoff guard did not immediately over-merge those semantic neighbors.
- Negative proof caught and fixed: row `29860` rejected a concrete `6519 Ringgold Road` medical dispatch for a 65-year-old male with heart or pulse racing symptoms because the medical event taxonomy recognized heart problems and chest pain but not palpitations or heart-rhythm wording. The `17:28:39Z` deployment adds guarded rhythm/palpitation wording to validation, medical event-class compatibility, and specific `Heart problems` fallback grounding.
- Proven improvement: after the `17:28:39Z` deployment, rows `29865` and `29866` created incident `3882` as `Heart problems at 6519 Ringgold Road` from call `796647`, and rows `29867` and `29868` rejected a bad sibling merge with unrelated South State Creek incident `3880`.
- Negative proof caught and fixed: rows `29887` and `29888` let seizure incident `3884` drop strong Jane Manor call `796969` and retain different-location Buckley Street call `796973` while preserving the Jane Manor title. Rows `29892` and `29893` self-corrected that membership before deploy, and the `18:04:28Z` deployment blocked existing incident updates from replacing source-backed concrete location support with conflicting located calls. Rows `29898` and `29899` were the first clean post-deploy signal, keeping `3884` on the Jane Manor set and excluding unrelated/handoff calls.
- Negative proof caught and fixed: rows `29900` and `29901` showed the first existing-anchor guard was too narrow because chest-pain incident `3883` kept its Callaway Court calls but also absorbed separate East Brainerd chest-pain call `797833`. The `18:12:47Z` deployment extends the guard to additive conflicting located calls, not only replacements of the protected source call.
- Negative proof caught and fixed: rows `29905` through `29911` showed the additive guard still missed existing-source locations that only appeared through transcript extraction; incident `3883` was overwritten from Callaway Court to East Brainerd under the old incident id. The `18:27:53Z` deployment uses weak transcript address, intersection, and location hints as conflict blockers inside the existing-incident guard only, not as positive membership anchors.
- Proven improvement: after the `18:27:53Z` deployment, rows `29913` and `29919` rejected East Brainerd updates against the Callaway Court chest-pain incident, and the current incident `3883` membership is back on Callaway calls `797519`, `797529`, `797537`, plus likely Medic 15 follow-up `798151`, with East Brainerd calls excluded.
- Continued clean signal: rows `29924` and `29925` again tried to attach an East Brainerd chest-pain set to Callaway incident `3883`, and final membership rejected it with the existing-anchor conflict guard. Current incident `3883` still excludes the East Brainerd calls.
- Continued clean signal: rows `29938` and `29939` kept MVC incident `3888` on the Black Fox Road / South Lee Highway call set and excluded unrelated Highway 64 / Melbourne Brewer Road call `798321`; rows `29941` and `29942` kept seizure incident `3887` stable while excluding weak cafeteria-detail call `798409`.
- Negative proof caught and fixed: row `29947` merged unrelated Borders Loop / Dayton Pike fire-assistance call `799031` with Anabela Lane lift-assist calls `799059` and `799089` through sibling repair. The `19:32:55Z` deployment lets call and incident narrative location anchors block sibling repair across incompatible locations, and adds `Loop` as a supported street suffix so `Borders Loop` can participate in address conflict checks.
- Early post-boundary signal after the `19:32:55Z` sibling narrative-location deployment: row `29963` created a supported South Washington Street tree or limb on power line fire incident with no sibling repair, queue issue, or immediate location-blocker overreach.
- Continued post-boundary clean signal after the `19:32:55Z` deployment: rows `29964` through `29970` updated chest-pain incident `3889` with a plausible transport/return-to-service continuation call and excluded a weaker on-scene/command fragment; no post-boundary sibling repair row appeared.
- Watch-only transient after the `19:32:55Z` deployment: rows `29971` through `29980` briefly considered or accepted separate St. Elmer / FEMA Avenue wires call `799877` into South Washington power-line incident `3895`, but current incident state self-corrected to calls `799715`, `799731`, and `799839` only.
- Continued watch-only transient: rows `29985` and `29986` again considered separate wires or utility calls `799877` and `799951` against incident `3895`, but the current incident view still has only the supported South Washington calls and the local-model failure cluster has recovered.
- Negative proof caught and fixed: rows `29990` and `29991` dropped same-event South Washington on-scene call `799839` from incident `3895` after a mixed verifier pass, while rows `29992` and `29993` continued to encounter unrelated utility/fire calls in the candidate set. The `20:46:09Z` deployment retains prior existing calls that still support the existing incident's concrete narrative street location and event evidence, without using those narrative or weak anchors as positive evidence for new calls.
- Early post-boundary signal after the `20:46:09Z` deployment is observational only: health, queue, AI routing, embeddings, and Qdrant stayed clean, but no post-boundary incident operation touched `3895` or exercised the new retention safeguard.
- Negative proof caught and fixed: row `30002` created false incident `3900`, `Traffic control on street`, from generic Cleveland Police traffic/status chatter without any concrete event anchor. The `21:14:20Z` deployment rejects generic traffic-control/status chatter unless retained evidence has a strong incident signal or a concrete or landmark event anchor; post-deploy row `30010` still accepted a supported McCauley/Derby MVC update.
- Negative proof caught and fixed: post-`21:14:20Z` rows `30013` and `30014` rejected a true tractor-trailer or tanker versus guardrail crash and a concrete `1720 Newcastle Drive NE` vehicle-in-roadway hazard because the event taxonomy did not recognize those wordings. The `21:30:03Z` deployment adds bounded road-hazard and vehicle-versus-fixed-object crash wording across validation, title grounding, event concepts, dominant event class, and category normalization.
- Mixed proof caught and fixed: post-`21:30:03Z` rows `30030` and `30031` recovered the I-24 truck-versus-guardrail event but preserved generic title `Traffic control on street`, and row `30032` still rejected `1720, Newcastle Drive, northeast` because comma-separated directional addresses did not become anchors. The `21:43:15Z` deployment adds comma-tolerant directional address extraction and forces generic traffic-control titles to fall back to specific retained evidence when available.
- New watch-only title-quality candidate: incident `3889` was created from supported chest-pain calls, but the title phrase `2500 South Southeast` is awkward transcript-normalized location text. Do not change title normalization from one grounded but awkward example.
- New watch-only title-quality candidate: incident `3891` was created from supported fall-dispatch calls, but the title `Fall at Lake Peninsula And Sandu Drive` reflects noisy transcript location text. Treat this like the `3889` title issue unless repeated examples show a general normalization failure.
- New watch-only taxonomy candidate: row `29920` rejected a possible single-vehicle Highway 64 / Melbourne Brewer Road traffic event with EMS on scene. One ambiguous `single vehicle` transcript is not enough to safely broaden crash evidence because that phrase can also appear in non-crash traffic contexts.
- New watch-only taxonomy candidate: row `29638` rejected call `790825`, a concrete-address sick-party dispatch requesting police, as lacking a strong event signal. This is not enough evidence for a broad medical taxonomy change yet, but repeated concrete sick-party rejects should be revisited.
- New watch-only taxonomy candidate: row `29696` rejected call `791537`, a concrete-address dementia/walked-off-and-returned dispatch that may be welfare-check-adjacent, but the current evidence is too cancellation/resolution-heavy for a safe acceptance change.
- Early positive signal: row `29626` created incident `3848` for the Glenwood Drive / Citico Avenue unconscious-person call set with supported not-responsive patient evidence and no obvious false join in transcript review.
- Early positive signal: after the `04:37:45Z` BOLO identity deploy, incident `3831` stayed stable and did not absorb weak BOLO-adjacent calls that lacked shared identity evidence.
- Still uncertain: whether the `07:21:28Z` bare-fire refinement restores `789371` or similar noisy BOLO relays, whether the `07:55:16Z` missing-person identity broadening restores `789831` or equivalent relays without false joins, whether the `08:07:37Z` single-call domestic and weapon correction creates the intended incidents without admitting routine calls, whether the `08:37:11Z` medical-symptom correction creates the intended incidents without accepting weak symptom-like wording, whether the `09:21:45Z` mixed-target recovery and cardiac fallback changes create the intended Hixson Pike, carbon monoxide, Highway 193 cardiac, or equivalent events without creating false incidents from unrelated leftovers, whether the `09:36:51Z` sibling-merge guard prevents incompatible merges without leaving true duplicates unmerged, whether the `10:11:39Z` split-dispatch-link correction restores `790405` or similar symptom/location split calls without false joins, whether the `10:54:17Z` numeric 10-code correction prevents address-triggered traffic titles without missing real separated 10-code events, whether the corrected `11:27:09Z` same-talkgroup patient-detail branch restores similar patient-detail calls without false adjacent-dispatch joins, whether the `11:58:36Z` weak-location sibling repair guard blocks bad sibling repair without leaving true duplicates unmerged, whether the `12:51:04Z` roadway-fragment and route-first highway-marker extraction fix restores or at least stops worsening incident `3860`, whether the `13:14:47Z` traffic-crash detail link restores `792105` or similar detail calls without admitting tag-only or administrative calls, whether the `13:29:25Z` location-detail and route-number compatibility fix allows row-`29732`-class incidents without same-route false joins, whether the `13:44:08Z` fixed-object possessive wording fix accepts row-`29737`-class fence or pole collisions without admitting generic property-damage chatter, whether the `13:59:56Z` sibling-repair final-validation gate blocks bad sibling unions without leaving true duplicates unmerged, whether verifier truncation starts causing missed joins, and whether the `06:08:04Z` single-call taxonomy change accepts the intended welfare-check and vehicle-hit-fixed-object cases without admitting routine checks.
- Still needs attention if it repeats after the latest boundary: sibling repair merging calls across conflicting concrete locations, stale internally conflicting incidents absorbing more siblings, true duplicate siblings left unmerged because weak concrete anchors do not normalize cleanly, same-talkgroup patient or response details still dropped from split dispatches, response-resource wording creating false primary event signals, false joins from immediate patient-detail linkage when adjacent details belong to separate events, address-like values producing traffic titles or strong event evidence, assault-like dispatches still getting unsupported traffic titles or generic fallbacks, same-talkgroup split address detail still dropping symptom-bearing calls, false joins from split dispatch detail when an address belongs to a separate event, unrelated calls merging through sibling repair or continuation links, concrete single-call welfare checks or vehicle-hit-fixed-object reports still rejected as weak, false single-call incidents caused by generic check or hit wording, validator-unmatched calls saved without concrete server-side linkage, legitimate continuation calls dropped by the final subset rule, broad same-category cross-talkgroup semantic joins, missed legitimate cross-talkgroup continuations, stale or awkward titles that do not get rewritten on later updates, unsupported concrete event types in titles, emergency incidents still rejected as routine status rollups, and false joins caused by nearby same-emergency, continuation-token, cross-agency same-address, spoken-number address, or BOLO identity linkage.
