# Radio area health reporting

Owner approved the Radio AOR's OT-to-existing-MQTT reporting dependency on
October 7, 2026. Reporting is optional and disabled without an explicit local
configuration path. It does not add SDR queries or require a workstation.

GET `/api/v1/health/radio-summary` follows existing read authorization. It returns
one version-1 snake_case report for `radio`, using existing HealthStatusService
and cached LiveRfStatusService assessments. The report is cached for 60 seconds.
It has at most three summarized exceptions and a 4,096-byte payload limit.
Detailed paths, logs, transcripts and domain diagnostics are not exported.

Coverage includes observed OT processing, receiver, RF assessment and the
currently unconnected SDR 1861. The entire area remains unknown/partial when no
fault is known but required remote evidence is missing. Known component faults
still produce degraded condition. Intentional domain capture stops are visible
as expected inactivity and excluded from receiver/RF required-active coverage.
Protection remains unknown until destination evidence is connected. A newly
generated report does not make stale child assessments current.

`PIZZAD_RADIO_HEALTH_REPORTING_CONFIG` selects a root-managed JSON file containing
enabled, brokerHost, brokerPort, username, passwordFile. No reporting credentials
are added to the existing application JSON, APIs, exports or source. The password
file must be absolute, at least 24 characters long, and not readable/writable by
other Unix users. Use a dedicated identity and system-trusted TLS;
there is no insecure transport or certificate-validation bypass option.

The existing application hosts the publisher. It assesses at most once per
minute, publishes material changes or a five-minute heartbeat, and exports only
the latest report after reconnect. MQTT operations have a ten-second deadline,
retry at one-minute intervals, and cannot block primary host startup or cause
unhandled BackgroundService errors. Errors log their type, not raw secret-bearing
messages. There is no persisted outgoing queue. QoS 1 acknowledgements must be
successful before the schedule advances. MQTT v5 is used to detect denied writes.

Topics: `whiteoak/health/v1/aor/radio/summary` and
`whiteoak/health/v1/aor/radio/availability`. This identity needs write access to
these two exact topics only, with no read, wildcard, command or discovery access.
Summaries and availability are retained; consumers must validate schema, area,
absolute generated/expiry timestamps and report freshness. MQTT Will is offline;
publisher connectivity does not establish capability health. Reports expire after
15 minutes. First-observed times describe this assessor's observation lifetime,
not an asserted historical failure start before assessor startup.

An existing read-only API consumer is the alternative to MQTT. Broker/OT failure
hides estate reporting; it must not stop radio processing, local diagnostics or
existing backups. New publisher credentials/configuration need recovery coverage;
none is claimed here, and no extra backup/retention policy is introduced.

Health review: this capability reuses domain assessments, corrects the overly
broad top-level-ok interpretation, preserves missing remote and backup evidence,
and adds transport freshness without reimplementing RF/transcription thresholds.
The existing HA direct OT feed can remain for detail until deliberately retired.

Deployment evidence: target assembly source revision 332d580769355695906fe6c94c1c2d70fff101c3
has identical deployable pizzad and setup-lmstudio.sh content to starting main
bfd2918. Historical live manifest backend hash 32ec53c88cadbf1714e092b61cfdedd649ade5eb5ca76b610ccbaf2d7d43bce7
does not match a current Windows working-tree hash; source revision/content
comparison resolves that difference. Recheck live revision and artifact digest
before deployment. Deployment remains pending until recorded verification.

Activation hold: the installed broker's unconditional HTTP authorization bypassed
the native two-topic ACL in live negative-write testing. A custom-include plugin
override was rejected during startup. All broker enrollment and OT staging were
rolled back, existing clients recovered, and no backend or publisher was deployed.
The new unused credential and activation files were removed. See the HA repository's
docs/radio-health-reporting-2026-10-07.md and docs/active-work.md. Staging source now
writes enabled=false by default. The owner subsequently approved temporary broad
access for the dedicated Radio account and deferred permission repair. Activate
only after TLS and target verification; no native ACL or plugin override is used.
The fixed publication topics are application behavior, not an enforced security
boundary. See whiteoakHomeAssistant docs/followups/mqtt-radio-permissions.md.
No new SDR queries or external reporting dependency is introduced.

Validation: the final full backend suite passed 821 tests, including twelve
Radio assessment/schedule/configuration cases. Existing SSH.NET advisory
warnings predate this change. There are no web source or asset changes.

Live TLS verification found MQTTnet's online revocation default rejected the
existing private CA with RevocationStatusUnknown/OfflineRevocation. This CA
provides no CRL/OCSP service. Reporting uses NoCheck for revocation only, matching
the existing private-CA trust policy; chain, hostname and expiry validation remain
mandatory. No certificate errors are ignored by the validation handler and no
new revocation service dependency is created. TLS diagnostics log enum flags only.

Activation completed after the owner approved the temporary broad-access exception.
Dedicated whiteoak_radio_aor / whiteoak-radio-aor-ot connected over trusted TLS 1.3;
an actual 795-byte summary appeared in Home Assistant's Radio operations row.
It correctly showed degraded reception, partial coverage and unknown protection.
No SDR 1861 query was added. Final live assembly SHA-256:
5916e5780ef60eb8ba9019fce5a904450adb213a746c313d65e4c1f52a21754c.
Final source f1e681a backend package SHA-256:
73fd2049ca16c3a366e41ee377787a650429ae306a1ffa40432a1be16ced5ad1.
Package checksum was checked before installation. Three pizzad restarts were
needed across initial activation, safe TLS diagnostics and private-CA correction;
Trunk Recorder remained active. The final 821-test suite passed. Complete receipt
and deferred account restriction/recovery checks are in whiteoakHomeAssistant
(see docs/radio-health-reporting-2026-10-07.md and
 docs/followups/mqtt-radio-permissions.md). Historical activation-hold text above
records the earlier failed attempt, not current state.

## Useful operational findings

The area report now carries up to three specific findings: affected capability or
RF site, domain-owned evidence, operational consequence, next investigation step,
and source diagnostic link. Critical capture/reception issues lead ahead of less
severe processing warnings. RF uses the existing structured decode/gap/retune
assessments exposed by LiveRfStatusService; no thresholds or network checks are
duplicated. Recovering reception reports the domain recovery hold explicitly.
Potential missing calls are described as a risk; no unmeasured lost-call count or
proven loss is claimed. First-observed times remain local reporter observations.

These are backward-compatible optional exception fields on contract version 1.
The publisher detects changes in explanation/evidence as material, remains bounded
to 4,096 bytes and the existing publication schedule, and never polls SDR 1861.
Names and domain evidence are byte-bounded without exporting combined diagnostics,
paths, transcript content, credentials or raw exception messages. Per-site issue
identities use stable hashes to avoid collisions after display-name truncation.

HA shows the leading finding with evidence, consequence, observation age and a
source link; a detailed page shows the three exported findings with next steps.
Backup & recovery and Reporting coverage are labeled separately. Missing evidence
stays unknown instead of an outage or a whole-area Healthy badge. Other AOR feeds
and backup evidence remain outside this Radio summary improvement.

Useful findings activated from clean source d0842d7. Checksum-verified backend
archive: 4fb89f394b6b0bd051a512028613da1725b2e26deb7d997bfa5453ad8af87887.
Live assembly: 42e814631237591a3c5e667f566d936e05c321bf72c1988b07cf71f857c4f9bc.
One pizzad restart; Trunk Recorder remained active. The actual 1,754-byte report
reached Home Assistant and identified North Bradley decoding failure, potential
unquantified missed calls and the domain evidence. Cleveland's retune warning
was visible separately; no new SDR queries were added. 826 backend tests passed.
HA native stale/unavailable/current fixtures and main/detail rendering passed.
Complete receipt: whiteoakHomeAssistant docs/useful-radio-status-2026-10-07.md.

## Estate escalation policy correction

Individual RF sites are no longer estate alarms. North Bradley's expected failure
must not dominate broad operational reporting. The area summary now projects only
active High/Critical System Recommendations problems, excluding RF targets,
dormant/quiet findings, accepted known issues, closed/dismissed findings and
improvements. It does not duplicate thresholds from HealthDto or RF status.
Recommendations owns ingest, transcription, incident/AI and dependency judgments.
Unresolved investigating/monitoring findings still escalate when active High+.

Use the shared five-minute Recommendations assessment/cache, including its real
generation time; reject stale/future source data. A completed shared build can be
reused after a cancelled waiter. Do not publish arbitrary detail/action/error
text: title, severity, evidence window and safe finding reference are sufficient
for the broad board; the complete diagnosis belongs in the web app.

Read-only UI links accept page=system and allowlisted tab/metric/finding IDs.
They override remembered landing tabs and optionally open a finding review panel;
invalid IDs or action/reset parameters cannot execute anything. The backend's
finding URL points to System / Recommendations, not an API dump. OT and SDR 1861
web assets need the same navigation support. The SDR update is frontend only,
319,142 compressed bytes, with no new agent, poll, backend change or restart.
Complete plan/evidence: whiteoakHomeAssistant docs/pipeline-escalation-2026-10-07.md.

Pipeline correction activated and verified. Final source fc0f7ec package checksum:
cf37e4ab84a996052af2e6a5a58d96191348dee6783dc8931889b49b396d131c.
Final OT assembly e74d7dd566b42a2009e1053a6d3ae86ffcdf74286941b53dee9c289e514fd552.
Two OT pizzad restarts included a final observation-time correction; Trunk Recorder
remained active. SDR web-only package checksum:
5f9bf5af50757218995e1ed177230452db877dafa5393cf8639fb64a24a7384a.
Its original backend hash/process IDs/start times remained unchanged. Web transfer
was 319,142 bytes with no added unattended traffic. Both deployed apps now support
read-only System URL navigation; OT finding review panels were verified in-browser.
835 backend tests, web build and ten navigation checks passed. The real 631-byte
summary exported no pipeline alerts while three High RF findings stayed in the
Recommendations UI. Healthy operation of unobserved SDR/backup/other AORs is not
claimed. Full receipt: whiteoakHomeAssistant docs/pipeline-escalation-2026-10-07.md.


## October 28: durable manual ingest holds and explicit capture warmup

Owner authorized OT deployment of pause recovery and HASS startup visibility.
The fixed singleton `ingest_control_state` table lives in the existing engine
SQLite database. Pause/resume writes commit with synchronous FULL before an API
success is returned; no separate file, credential, broker or workstation is
required. Pause mode, operator reason and original paused-at time survive a new
process. Reassertion preserves the original hold time. Explicit Resume persists
the release, so a later restart cannot resurrect an old hold.

An unreadable/invalid saved state starts paused with stateStatus=unavailable;
review storage and explicitly resume after recovery. A failed resume write keeps
the old hold. Automatic queue-clear release must also commit before applying;
a failed write leaves the hold for explicit review. A restored queue-clear pause
cannot auto-release against startup's empty RAM queue: Program marks recovery
complete only after pending transcription recovery. Existing reset operations
continue to use the same durable Pause/Resume service. Dropped-call counters
remain per-process measurements; no per-call persistence or counter replay is
introduced. A pause retains its existing behavior of declining incoming live
payloads, so production is not deliberately paused for validation.

The bounded Radio pipeline adds capture_status, capture_started_at,
ingest_state_status and ingest_paused_at from native health. These retain native
source times. Warming is distinct from active capture and from stale/fault/
stopped/unknown. The publisher treats capture-state and pause-state availability
changes as material updates. The existing 4 KiB wire limit remains: lower-priority
finding details may be omitted while total counts, highest-priority exception,
source URLs and pipeline evidence remain. Remote SDR/backup coverage is not
silently upgraded by this change. HASS shows the warmup explicitly and warns on
unavailable pause state through the existing native Radio report. The legacy
REST sensor continues to describe application health.

Recovery review: power/process loss preserves acknowledged holds through SQLite;
HASS, broker, cloud, network and arbitrary recovery order cannot release them.
Unreadable/invalid or missing saved rows pause live ingest until reviewed.
Removing/replacing the entire engine database is a separate destructive
reinitialization and cannot recover a prior hold from absent data. Local diagnostics,
DB/WAL recovery and the existing authenticated Resume endpoint remain available
without HASS or broker. The new row travels with existing whole-engine database
backups; this work does not establish new backup/restore acceptance or retention.
No host reboot, Trunk Recorder restart, production pause or synthetic MQTT state
is required for verification. Backend-only deployment preserves installed web
assets. `-PackageOnly` prepares the same checked artifact for isolated validation;
`scripts/test_ingest_restart.py` uses temporary paths and loopback-only access,
disables outbound Radio reporting and verifies crash/restart plus explicit resume.


Acceptance on October 28: 848 backend tests passed. The final Linux artifact
passed an isolated three-process test on OT: native warmup, durable manual pause,
forced process termination, original time/reason restored, explicit resume and
another restart without resurrecting the hold. Outbound Radio reporting was
removed from that test process environment; temporary DB/config/token/artifact
copies were removed. No production Pause/Resume was issued for validation.

The guarded production upgrade preserved its genuine unpaused intent in the new
table, restarted only pizzad and reached a passing health check. Native health
reports stateStatus=durable and active capture; the one saved row is schema 1,
paused=false, until_queue_clear=false, paused_at_utc=NULL. Engine and TR config
hashes, installed web tree and TR PID 1979/start time (14:05:27 Eastern) matched
preflight. HASS received capture_status=ok, capture_started_at equal to native
process start, ingest_state_status=durable and ingest_paused=false. Partial SDR
coverage and unrelated existing AI/REST observations remain separate concerns.

Installed assembly SHA-256:
`1affa5ade19c8de7ed3a3f106282245a02cc728d3a5ae172836b3567a9ee26a0`.
Deployment package SHA-256:
`80c93d81507922f7169bdb7aa7157eccfab2f09a3784520efa9a7f20d4b96b5a`.
The build embeds task source revision 7f811d3. The final consolidated milestone
has identical deployable inputs; subsequent PizzaWave edits only record this
acceptance and clarify the HASS reporting path. PDB document hashes verified all
133 pre-change backend source files against the selected remote-main baseline.
The unrelated primary checkout/local SSH commit and older archive/RF branches
were preserved rather than included in this deployment.

The actual HASS browser view confirmed current OT/SDR counters and preserved
real findings, including the separate AI incident-generation degradation. No
warning was dismissed or manufactured. Physical host reboot remains untested;
the isolated process crash/restart test establishes the durable hold behavior.
