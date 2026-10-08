# Useful Radio evidence for the estate board

Radio now exports a bounded projection of the existing HealthStatusService:
capture and transcription counts/window, pending transcription, capture source
time, incident-analysis pending count/latest analyzed source-call time and recent
AI request/failure counts. These facts do not turn unknown estate coverage into
an overall healthy verdict. Existing source/cache/probe cadence is reused; no
new SDR collector, polling, credentials or endpoint dependency.

Unresolved High/Medium non-RF problem findings are exported separately, including
dormant findings. Current High/Critical escalation policy is unchanged. Accepted
known, resolved/dismissed, RF and improvement findings stay outside the survey.
Each item links to the real System Recommendations finding. Safe bounded fields
exclude raw error/action strings, audio, transcripts and private paths. Pipeline
evidence is rejected if old/future. The total finding count survives truncation
when needed to maintain the existing 4 KiB payload ceiling.

Checkpoint 29f843a backend package SHA256:
6770fe9f7caee2cab05156d0adfcb3054c24c4163149cf1c120f7484decaa2ae.
Automatic deploy helper verified the archive before OT installation. Final live
assembly b1419038a4fa9e6cf2220ca2133a05bd5d02299d7f5b87d29b0a051f57381127.
One pizzad restart; Trunk Recorder stayed active with PID 2942209 and the same
September 19 start time. No SDR deployment or queries. Backend suite: 840 passed.
Frontend source was unchanged; its tracked source-hash marker reflects the build.
Source HealthDto also reads existing embedding health; its own probe gate/cadence
remains authoritative rather than adding a separate probe schedule.

HA showed real processing counts, AI failures and three unresolved pipeline
findings: High dormant capture staleness, Medium dormant AI degradation and active
Medium resource pressure. Quiet is not resolved. Native links reached specific
finding destinations. Health review retains partial/unassessed SDR and recovery
coverage. HA-owned consumer/presentation evidence is in
whiteoakHomeAssistant/docs/operational-board-2026-10-08.md.
