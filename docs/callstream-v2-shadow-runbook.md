# Callstream Version 2 Shadow Runbook

Status: implementation verification plan, 2026-07-30.

Callstream version 2 preserves Trunk Recorder's decoder-delimited transmission
records in PizzaWave. This runbook deliberately stops before deployment. It
does not authorize a Trunk Recorder restart, a production incident write, or an
incident backfill.

## Deployment order when a shadow is authorized

1. Install the PizzaWave consumer first. It remains compatible with version 1
   payloads.
2. Verify PizzaWave health and version 1 call ingestion without changing Trunk
   Recorder.
3. Build the version 2 Callstream plugin against the exact Trunk Recorder source
   lineage used by the target site. The implemented contract was checked against
   the documented RPI Trunk Recorder candidate `ca787409`, which contains the
   required millisecond timestamps and completed-call transmission list.
4. Schedule the separately authorized Trunk Recorder plugin change. A restart
   is required to load a rebuilt plugin and is not part of this implementation
   phase.
5. After the first version 2 payload, query
   `/api/v1/calls/{callId}/transmissions` and compare it with the corresponding
   Trunk Recorder completed-call transmission lines.
6. Open several ordinary call cards in PizzaWave. **View radios in this call**
   shows neutral radio activity beside the existing full-call player. Confirm
   that repeated identifiers receive consistent visual treatment and that no
   dispatcher or responder role is inferred.

## Frozen transport checks

For a bounded sample, record:

- parent calls received as version 1 and version 2;
- version 2 calls by `exact_live`, `exact_reconstructed`, and `unavailable`;
- Trunk Recorder transmission count versus stored PizzaWave transmission count;
- complete PCM coverage failures;
- missing source-identifier count and rate among retained transmissions;
- distinct transmitting radios per call;
- decoder errors and spikes by transmission; and
- any parent call retained without exact transmission audio mapping.

Acceptance requires:

- no parent recording containing an identified radio or acoustic content is
  silently lost because of transmission metadata;
- every exact mapping starts at sample zero and covers the PCM payload once;
- every sampled Trunk Recorder transmission maps to one stored observation,
  except a source-less fragment that Callstream proves contains only decoder-floor audio;
- unavailable source identifiers remain explicit null values whenever their
  transmissions contain acoustic content or cannot be safely inspected;
- the legacy top-level `Source` field is never counted as a participant; and
- no incident publication or membership behavior changes during the shadow.

## Bounded five-call trial

Freeze the following procedure before the first version 2 recording is examined:

1. Capture five consecutive version 2 calls without changing `callTimeout`.
2. Produce a temporary offline report from the stored calls and transmission
   rows. Do not add a permanent operator page or production control for this
   one-time engineering trial.
3. Compare the transmission count for every sampled call with Trunk Recorder's
   completed-call log. Listen to each full recording and use the diagnostic
   per-transmission endpoint only where necessary to confirm a declared
   boundary.
4. Record the exact-audio rate, reconstructed-audio rate, unknown-radio rate,
   several-radio rate, decoding errors, and audio spikes by system. Radio
   identifier availability is an observed characteristic, not a reason to drop
   a transmission.
5. Replay the same frozen incident-candidate windows twice: once without source
   linkage and once with the source-linkage service enabled before candidate
   selection. Keep every other input and setting unchanged.
6. Give the one reviewer the resulting windows in randomized order. Record
   elapsed review time, included calls, unclear decisions, and corrections.
   Compare the reviewer's decisions between the two versions and inspect every
   changed join or split. The reviewer still decides membership at the call
   level; individual transmissions are supporting evidence only.

The transport portion passes only if no parent recording is lost, every stored
transmission count matches Trunk Recorder, and at least 99 percent of sampled
recordings have exact audio mapping. Investigate every non-exact recording.

Five calls are a functional check, not enough evidence for percentage-based
quality claims. Proceed to a larger frozen trial only if the source identifiers
are present, the boundaries map correctly, and none of the five cases exposes a
clear false-link mechanism.

## Rollback

The PizzaWave consumer can continue accepting version 1 after rollback. Restore
the prior Callstream plugin binary using the existing deployment rollback
procedure. Do not delete version 2 transmission rows; they are append-only
capture evidence and do not affect existing incident ownership.
