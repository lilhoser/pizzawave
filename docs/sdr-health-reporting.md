# SDR 1861 compact health reporting

Owner approved October 8: report existing local pipeline metrics and relevant
System Recommendations every fifteen minutes, with a 4 KiB payload limit. The
House broker has no existing route from SDR's remote network. Reuse SDR's
working NAS SFTP transport and the NAS's existing MQTT publisher instead of a
new VPN route, public broker listener, credential or full backend deployment.

## Reporting path

The root oneshot reads local `/api/v1/health` and the current `items` array from
`/api/v1/system/recommendations`. Detailed diagnostics, history, episodes,
transcripts, paths and error strings are omitted. The old Recommendations API
includes megabytes of historical data: pinned ijson's Python streaming backend
projects only scalar fields from current items and closes the loopback response
at the end of that array. Its observation time is the local read time; individual
finding last-seen dates and dormant state are preserved. No broad healthy claim.
The API owns assessments; this script adds no anomaly thresholds. Existing
Radio escalation policy excludes RF, accepted known issues and closed findings;
open Medium/High/Critical pipeline findings remain visible as on OT.

ijson 3.4.0.post0 official PyPI ARM64 Python 3.13 wheel is bundled as ijson.whl;
SHA-256 ccddb2894eb7af162ba43b9475ac5825d15d568832f82eb8783036e5d2aebd42.
Only its Python backend is used. No global package installation. Reference:
https://github.com/ICRAR/ijson (incremental parsing interfaces reviewed October 8).

The existing root-only service upload key and NAS host-key pin under
`/etc/whiteoak-machine-backup` send the sanitized JSON to the same restricted
`wo-sdr1861-backup@100.124.70.102` account. Only `/home/sdr-health.json` is
atomically replaced, through a fixed temporary sibling. No repository objects,
backup schedule, key, account permissions or routing policy are changed.
Temporary local health JSON is removed after upload. No history queue.

`whiteoak-sdr-health.timer` runs at quarter-hour boundaries with up to five
seconds jitter. Exactly one SFTP attempt per run; no immediate retry. Local API
reads have deadlines and source/report limits. Failed checks are explicit;
failure does not become zero findings. Reporting cannot restart or stop pizzad
or Trunk Recorder. Root is needed only for the existing token/upload transport;
the isolated unit has no new privileges, read-only system/home protection and
one private writable runtime directory.

NAS validates source identity, authenticated file owner, 4 KiB limit, timestamps,
fields and detail URL, then forwards through its existing MQTT identity/topic
`whiteoak/observations/v1/source/sdr1861/pipeline`. Republish every five minutes
does not change the original source date. Absolute lifetime is twenty minutes
(fifteen-minute cadence plus delivery allowance); HA rejects stale/future data.
NAS/broker/VPN failure affects estate visibility, not capture or transcription.

At most 96 scheduled reports/day × 4 KiB = 384 KiB/day of source payload.
SFTP/VPN connection overhead is additional and has not been measured; no audio,
IQ, historical diagnostics or backend binaries are transferred by the reporter.
The existing NAS timer/identity forwards small JSON on the local network.

## Activation progress

Reporter installed from clean source. Initial package SHA-256
c177a826bff92907a7f6fbbe6f47c06da8cb8a7e2bf3d5d27bab00e8379bb6f1
(10,240 bytes). Current-items correction plus pinned parser package SHA-256
7e5e2b787e655c9d502dfd96fb00e7e376a43abfdb06090b70f3a31a4a5a8628
(163,840 bytes). Installed source hash guarded each update. Initial API reading
had valid pipeline metrics but rejected the large Recommendations body; this was
corrected with streaming rather than raising an unbounded download allowance.

Actual report uploaded October 8 at 19:53 Eastern, 1,354 bytes. Independent SFTP
read-back into a temporary health-only file confirmed pipeline/findings reported,
44 calls received, 46 transcribed, zero awaiting transcription and seven open
pipeline findings (first three explicitly dormant). Temporary verification file
was removed. pizzad/TR PIDs remained 1653/1659; no application restart.
Timer enabled/active, next trigger October 8 20:00:01 Eastern.
Six focused fixtures passed, including streaming past large-history boundaries,
private-field omission, stale health, RF/known-issue exclusions and Unicode bounds.

The owner installed NAS forwarding on October 8. First delivery exposed a
compatibility error: .NET's seven-digit fractional timestamps are rejected by
DSM Python 3.8. This was confirmed on NAS with a public timestamp fixture. The
source now emits six-digit microsecond timestamps for API observation/finding
dates, preserving their instants; no second NAS root update was required.
Final installed reporter SHA-256:
1cf856f2d49d111c47445d72d1f5f11d437acf3a248086bc0dc9eb0740776cc9.
Seven focused source fixtures passed, including that interoperability case.

The real 20:27:07 Eastern source report reached MQTT through NAS's normal 20:30:02
cron cycle: 1,386 forwarded bytes, pipeline and Recommendations both reported,
16 received/16 transcribed, zero pending transcription, seven unresolved pipeline
findings with dormant labels and original last-seen dates. HA rendered these
facts and direct source finding links. The regular quarter-hour source job
finished at 20:30:13 with Result success/exit 0. pizzad/TR PIDs remain 1653/1659.
Source and NAS retain their existing identities/routes; new reporting identity
or app restart was not needed. No complete Radio AOR health verdict is claimed:
OT's existing publisher remains unchanged, and HA displays the SDR child report
separately. Details remain on SDR's own System pages.


Regular end-to-end delivery verified October 8: source generated 20:30:03 Eastern,
job finished 20:30:13; NAS's existing 20:35:02 cron forwarded the source unchanged.
An independent local MQTT subscription read the actual 1,386-byte report:
22 received, 22 transcribed, zero pending transcription, seven open pipeline
findings; pipeline and Recommendations both reported. HA updated to those values.
No extra SDR health request was used for this final recurring-chain verification.
The connection is complete; repository integration and normal task cleanup follow.
