# Callstream version 3 capture completeness

Status: implemented, tested, and linked successfully against Trunk Recorder on
RPI, 2026-07-30. Production deployment remains separately gated.

## Problem

Trunk Recorder records whether it created a call from the original channel
grant or from a later channel-grant update. Before version 3, that fact was
written to the Trunk Recorder log but discarded by the plugin completion
contract. PizzaWave therefore treated a possibly incomplete first transmission
as an ordinary observed beginning.

The two-hour RPI trial contained 483 stored calls and 937 transmissions. Trunk
Recorder logged 203 `UPDATE not GRANT` conclusions. Of the 172 conclusions that
mapped to calls inside the fixed PizzaWave window, the calls contained 340
transmissions and 1,034.28 seconds of audio. Only 21 of those calls contained
less than one second of total audio. Capture completeness is therefore not only
a short-fragment filter.

## Version 3 contract

Trunk Recorder now exposes its existing `was_update` value through `Call` and
`Call_Data_t`. Callstream version 3 sends:

- `ChannelAssignmentStart`: `grant` or `update`;
- `BeginsChannelAssignment`: true only for `grant`; and
- `PossiblyIncompleteTransmissionStartTimeMs`: the original first
  transmission's start time, even if that transmission is later omitted; and
- `StartStatus` on every retained transmission.

For a call created from an update, only the first original transmission is
`possibly_incomplete`. Later decoder-delimited transmissions are
`observed_boundary`. If an empty first transmission is omitted before transport,
the next retained transmission is not incorrectly relabeled as incomplete.

PizzaWave rejects contradictory version 3 metadata, including a grant marked as
not beginning the assignment or a later transmission marked incomplete. Version
1 and version 2 payloads remain accepted as legacy evidence.

## Admission rules

An update-started call cannot establish a new incident by itself.

When it contains one retained transmission and less than 1,000 milliseconds of
audio, PizzaWave looks for a strict predecessor. A predecessor must have:

- the same radio system;
- the same talkgroup;
- the same decoded radio identifier;
- a complete or legacy transmission boundary; and
- no more than 3,000 milliseconds between its last transmission and the
  fragment.

If found, PizzaWave stores the fragment as `attached_incomplete_fragment` with
the predecessor call identifier. It does not transcribe the fragment or use it
as an incident seed. If no predecessor is found, PizzaWave stores only the call
and transmission metadata as `suppressed_incomplete_fragment`; it does not
persist the audio, transcribe it, or expose its source identifier to participant
linkage.

Longer update-started calls and calls containing later complete transmissions
remain available for transcription and may support another call or active
incident. They cannot independently seed a new incident. Participant linkage
ignores every `possibly_incomplete` transmission but retains later
`observed_boundary` transmissions from the same call.

## Verification gates

Before production activation:

1. Build Trunk Recorder and Callstream version 3 together against the exact RPI
   source lineage. Completed in an isolated RPI build directory on 2026-07-30.
2. Install PizzaWave first and verify version 1 and version 2 compatibility.
3. Schedule the Trunk Recorder and plugin replacement together; their interface
   changed and requires a Trunk Recorder restart.
4. Run a two-hour shadow window without changing `callTimeout`.
5. Report grant-started and update-started calls, strict attachments,
   suppressions, retained later transmissions, incident-seed exclusions, and
   participant links removed because their only evidence was incomplete.
6. Manually inspect five cases: two suppressed fragments, one strict attachment,
   and two update-started calls with later complete transmissions.

Do not publish or rewrite incidents during this shadow window.
