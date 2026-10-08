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
