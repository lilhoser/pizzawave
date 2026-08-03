# Incident pipeline finalization - August 3, 2026

## Decision

The existing Qwen-driven incident pipeline is the supported PizzaWave incident
implementation. The multi-day replacement research is closed. There will be no
additional broad model comparison, long-running collection, or mandatory
small-model training program before this work is considered complete.

This decision accepts a useful, imperfect system. Qwen may vary on ambiguous
inputs. PizzaWave limits the effect of that variation by owning source identity,
validating final membership, rejecting conflicting concrete locations, keeping
failed work in its durable queue, and refusing malformed output. Qwen already
serves production presentation and incident analysis; this is not a second
parallel model deployment.

## Evidence considered

- OT completed 1,107 incident decisions in the latest inspected 24-hour window:
  599 accepted and 508 rejected.
- The latest 20 AI requests had no failures or timeouts, and transcription and
  embedding queues were current at inspection time.
- The radio-linked observation saw 664 calls and 596 usable transcripts but
  produced only two eligible decisions, accepted none, and improved no incident.
- Qwen correctly handled all five labeled mixed/clean examples in the saved
  teacher check, while identical ambiguous requests could still change answers
  across separate runs. That variability is acceptable for proposals behind
  application validation; it is not acceptable as a standalone permission
  switch.
- The preserved archive has 18 directly reviewed call relationships. The newer
  saved material has 105 unique candidate-plus-incident examples but only eight
  direct reviewer answers. This does not support an honest fine-tuned-model
  release.

These counts describe operation and available evidence, not a claim of perfect
semantic accuracy. Current incident membership remains context rather than
ground truth.

## Supported settings

- `aiInsights.incidentAnalysisExecutionEnabled=true`
- `aiInsights.incidentParticipantLinkCandidateEnabled=false`
- `aiInsights.incidentTargetMembershipShadowEnabled=false`
- all retired constructor, relationship, verification, and alternative
  incident-analysis experiments disabled

The source-radio transmission ledger remains enabled independently because it
supports call inspection and future diagnosis without deciding membership.

## Code closure

PizzaWave now avoids loading participant-link candidate evidence when neither
the production consumer nor the explicitly bounded observation consumer is
enabled. This removes needless database and candidate-selection work from the
normal incident path while preserving the evidence subsystem.

## Acceptance and rollback

The finalization is acceptable when the full automated test suite passes, OT
health remains `ok`, recent AI completions have no repeated failures, incident
analysis remains within its configured 60-minute live window, and Trunk
Recorder remains active without being restarted.

Rollback is the preceding PizzaWave commit plus the same supported settings.
No incident backfill, deletion, or membership rewrite is part of deployment or
rollback.

## Deployment verification

The finalized backend was deployed to OT and RPI on August 3. Both health
endpoints returned `ok` after restart. OT reported 23 recent AI requests and RPI
reported 12, with no failures, timeouts, or invalid results. Incident analysis
was current on both systems and neither service had new warning-level log
entries. Trunk Recorder was not restarted: OT retained PID 126817 and RPI
retained PID 3826478.
