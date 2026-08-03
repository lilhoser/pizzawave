# Transmission Ledger Architecture

Status: version 2 transport is live and version 3 capture-completeness handling
is implemented for verification, 2026-07-30. Version 3 deployment remains
separately gated.

This note addresses the information lost between Trunk Recorder, Callstream,
and PizzaWave before incident membership is considered. It does not replace the
incident membership model described in
[`incident-pipeline-architecture.md`](incident-pipeline-architecture.md). It
gives that model better source evidence.

## Finding

Trunk Recorder already distinguishes individual push-to-talk transmissions
inside a completed call. For each retained transmission, its current
`Call_Data_t` contains:

- transmitting radio source identifier, when decoded;
- talkgroup;
- TDMA slot and color code where applicable;
- millisecond start and stop times;
- sample count;
- frequency;
- decoder error count and spike count; and
- the ordered position of that transmission in the combined playable audio.

Trunk Recorder's OpenMHz and Rdio Scanner plugins already export an ordered
source-and-position list. Keeping this information is therefore established
behavior, not a novel incident heuristic.

The current Callstream plugin discards that list. It sends one combined audio
stream and a small JSON object. More seriously, the JSON field named `Source`
is populated from `call_info.sys_num`. That is the Trunk Recorder system number,
not a transmitting radio source identifier. PizzaWave parses and stores that
value as `EngineCall.Source`, so the current field cannot be used as participant
evidence.

This is an integration-contract defect. It is separate from transcription
quality and separate from semantic incident membership.

## Comparison with established software

Trunk Recorder keeps a broad call object open while control-channel updates and
audio activity continue. Its transmission sink closes individual transmissions
at decoder termination boundaries and retains an ordered `Transmission` list.
Its configurable `callTimeout` controls when the broader call is abandoned
after relevant activity stops.

SDRTrunk follows the same general separation. Its P25 Phase 2 decoder processes
explicit push-to-talk and end-push-to-talk messages, changes the current `FROM`
radio identifier as talkers change, and can write a discrete MBE call sequence
at end push-to-talk. Its broader audio segment remains associated with the
traffic-channel call until squelch or channel teardown. SDRTrunk also tracks
talkgroups and patch groups explicitly.

SDRTrunk permits simultaneous-call duplicate detection by matching talkgroup or
radio identifier, but warns that radio-identifier matching can have unintended
effects when a dispatcher transmits to more than one talkgroup. This supports
using source overlap as evidence, not as sole membership authority.

## Recommended data model

Preserve three different internal concepts while keeping **call** as the
established user-facing term.

### 1. Transmission observation

A transmission observation is one decoder-delimited push-to-talk interval. It
is the smallest audio and provenance unit PizzaWave should retain.

Required fields:

- application-owned observation identity, assigned during ingestion;
- Trunk Recorder system number and system short name;
- parent Trunk Recorder call number;
- talkgroup and patched talkgroups;
- source radio identifier, nullable when unavailable;
- source-identifier provenance when Trunk Recorder can provide it, such as
  voice-channel decoded, control-channel grant, cached grant, or unknown;
- start and stop time in Unix milliseconds;
- start sample and sample count within the supplied combined PCM audio;
- frequency and TDMA slot;
- decoder error count and spike count; and
- an explicit audio-availability state.

The application-owned observation identity never has to be reproduced by a
model. It is attached after the model response is mapped through the separately
tested source-identity-safe membership contract.

### 2. Conversation segment (user-facing call)

A conversation segment is the broader Trunk Recorder call envelope and the
canonical unit supplied to incident membership. In the PizzaWave interface it
continues to be called a **call**. It preserves:

- the ordered transmission observations;
- the original combined audio for compact playback and contextual listening;
- the configured timeout and capture metadata; and
- gaps between actual transmission times.

This layer explains why a recording can contain a dispatcher and one or more
responding radios without pretending that the whole recording is one speaker.

### 3. Incident candidate window

An incident candidate window is a temporary, application-owned collection used
to retrieve bounded evidence. It may accumulate:

- participant radio identifiers;
- talkgroups and active patch relationships;
- conversation segments and transmission observations;
- first and latest activity times; and
- unresolved observations that need another window.

The ledger does not publish an incident and does not decide membership. It
creates a better candidate set for the local membership model. A dispatcher
console can appear in unrelated events, radio identifiers can be missing or
cached, several radios normally participate in one event, and patches can make
the same transmission appear on multiple talkgroups. These facts prevent any
deterministic `same source means same incident` rule from being reliable.

Candidate windows must not claim exclusive ownership of a call. During the
configurable follow period, one call may appear in more than one bounded
candidate window. The membership model and application-owned coverage checks
resolve ownership later. This avoids contaminating every nearby dispatch merely
because the same console radio identifier appears in each one.

## Callstream protocol version 2

Add a versioned JSON envelope while retaining the existing binary framing. The
version 2 PCM payload should be rebuilt as the exact concatenation of retained
transmission audio, or the producer must prove that its live PCM buffer is
sample-for-sample identical to that concatenation. A representative shape is:

```json
{
  "SchemaVersion": 2,
  "SystemNumber": 1,
  "SystemShortName": "example-p25",
  "CallId": 12345,
  "Talkgroup": 1201,
  "PatchedTalkgroups": [1202],
  "Frequency": 851.1125,
  "StartTimeMs": 1785420000123,
  "StopTimeMs": 1785420012456,
  "SampleRate": 8000,
  "Transmissions": [
    {
      "SourceId": 2010241,
      "SourceIdProvenance": "unknown",
      "Talkgroup": 1201,
      "StartTimeMs": 1785420000123,
      "StopTimeMs": 1785420002283,
      "StartSample": 0,
      "SampleCount": 17280,
      "Frequency": 851.1125,
      "TdmaSlot": 0,
      "ErrorCount": 0,
      "SpikeCount": 0
    }
  ]
}
```

`StartSample` is transport metadata for slicing supplied audio. It must never
become something the membership model is asked to emit. The producer derives
it from the ordered retained transmissions and verifies that all ranges are
non-overlapping, in bounds, and completely cover the supplied PCM payload.

Callstream's current live audio callback is connected in parallel with Trunk
Recorder's transmission sink. It has no transmission-start or transmission-end
callback, so exact alignment with the sink's retained audio cannot be assumed.
For the first correct implementation, Callstream should concatenate the
temporary per-transmission WAV data that is still available when its completed
call callback runs. A later Trunk Recorder plugin API extension could provide
boundary-aware PCM directly if measurements show the temporary-file read to be
materially expensive.

Do not reuse the old top-level `Source` name. Version 2 uses `SystemNumber` for
`sys_num` and `SourceId` only for a radio identifier. During migration,
PizzaWave may accept version 1, but it must label the old value as a legacy
system number rather than participant evidence.

The first version can report `SourceIdProvenance` as `unknown`. Trunk Recorder
currently records when a cached grant identifier is substituted, but its
`Transmission` structure does not preserve that provenance. Adding provenance
requires a small upstream structure change and should not delay preservation of
the source identifier itself.

## Configuration decisions

### Preserve grant-versus-update starts

Callstream version 3 adds the Trunk Recorder fact that a call was created from
the original channel grant or from a later update. An update-started call has a
possibly incomplete first transmission; it does not imply that every later
transmission is incomplete or that the recording belongs to the immediately
preceding call. The concrete contract and admission rules are frozen in
[`callstream-v3-capture-completeness.md`](callstream-v3-capture-completeness.md).

### Keep `transmissionArchive` disabled

Permanent per-transmission files are not required. The completed plugin callback
already contains the transmission list and the temporary filenames. Trunk
Recorder writes those temporary transmission files as part of its normal
combine-and-conclude flow even when `transmissionArchive` is disabled. The
setting controls whether those files are retained after processing; it does not
turn their initial creation on or off. Version 2 can consume the temporary data
during the existing completed-call callback, after which Trunk Recorder removes
it normally. PizzaWave retains only the durable artifacts it needs.

Temporarily enabling `transmissionArchive` is appropriate only for a bounded
diagnostic comparison proving that transmitted offsets match archived files.
It should not become the normal architecture.

### Do not change `callTimeout` yet

Changing the timeout before preserving transmission boundaries would merely
change how much information is merged into each opaque recording. Once version
2 is available, `callTimeout` affects conversation-segment packaging rather
than the atomic transmission evidence. Keep the present three-second setting
for the first shadow comparison, then measure whether it splits or joins calls
inconveniently. It should not be used as an incident-membership setting.

## Validation and release sequence

Implement and prove the data boundary before revisiting model training.

1. Add version 2 production in a separate Callstream worktree and version 2
   parsing in PizzaWave.
2. Add contract tests for multiple sources, missing source identifiers, patched
   talkgroups, millisecond precision, malformed ranges, complete audio coverage,
   and version 1 compatibility.
3. Replay synthetic payloads containing two and three talkers and prove exact
   mapping from every transmission to its PCM range. The application must reject
   malformed metadata without losing the parent payload.
4. Run a non-mutating shadow capture. Compare the version 2 ledger with Trunk
   Recorder's own transmission log for a bounded sample. Do not publish or
   modify incidents.
5. Measure source-identifier availability and apparent cached-identifier use by
   system, site, talkgroup, time of day, decoder errors, and spike count.
6. Rebuild the incident-review pilot from transmission-aware evidence. Keep one
   full-call player and, when useful, show a neutral activity list that gives
   the same radio a consistent visual treatment. Do not label a radio as a
   dispatcher or responder unless that role comes from independent metadata.
7. Only then measure whether participant-aware retrieval reduces ambiguous
   windows, review time, and false joins. The frozen semantic membership gates
   remain unchanged.

Freeze these transport gates before shadow capture:

- every Trunk Recorder transmission in the sampled log has exactly one parsed
  version 2 observation;
- every version 2 PCM sample belongs to exactly one declared transmission;
- no malformed transmission table causes the parent call to be
  silently lost;
- source-less transmissions with acoustic content remain present and explicitly
  unknown, while proven source-less decoder-floor fragments are omitted before
  transport;
- version 1 payloads continue to ingest during the transition; and
- the legacy top-level `Source` value is never counted as a radio participant.

## PizzaWave service and interface boundary

PizzaWave exposes a radio-activity view for a call at
`GET /api/v1/calls/{callId}/transmissions`. It returns a plain-language mapping
state, participant counts, and ordered transmission rows. A separate recording
is available at
`GET /api/v1/calls/{callId}/transmissions/{sequence}/audio` only for
`exact_live` and `exact_reconstructed` mappings. PizzaWave validates the stored
sample range against the parent mono 16-bit PCM WAV before returning a clip.
An unavailable or out-of-range mapping returns no clip; it is never estimated.
This per-transmission endpoint is diagnostic support, not the normal interface.

The existing full recording remains the only audio player on both ordinary call
cards and incident source-call cards. An optional compact radio-activity list
sits directly under it and labels relative timing, clock time, radio identifier,
talkgroup, duration, errors, and spikes. Repeated identifiers receive consistent
neutral colors. The interface neither invents dispatcher and responder roles nor
alters incident membership.

Before semantic or vector candidate selection, PizzaWave examines eligible,
temporally adjacent calls from the same radio system. Consecutive appearances of
the same source identifier become advisory retrieval evidence. Multiple shared
identifiers strengthen that evidence, while the number of nearby calls using the
same identifier warns about a common dispatcher console or shared radio. The raw
identifiers are not sent to the membership model, and a link never automatically
places calls in the same incident.

The default adjacency limit is 60 minutes, matching the production incident
pipeline's maximum incident span. Live use of these links for candidate
selection is separately disabled until the shadow trial supports enabling it.

This placement is deliberate: the later call must first arrive before adjacency
can be observed, but the evidence must exist before the incident pipeline chooses
its bounded candidate set. The current production pipeline only applies linkage
to calls that already pass its incident-eligibility checks. It does not promote
unintelligible or otherwise ineligible recordings into incidents. Changing that
boundary requires a separate unresolved-evidence contract and evaluation.

## Failure cases the ledger must preserve

- A missing or undecodable source identifier with acoustic content remains a
  valid unresolved observation. Callstream discards a source-less fragment only
  after reading its audio and proving that every sample remains at the decoder
  noise floor; duration alone is not sufficient.
- A dispatcher radio used for several unrelated events does not join them.
- Several different responding radios can belong to one incident.
- A radio used in two nearby but unrelated transmissions does not prove a join.
- Patched or simulcast talkgroups retain their relationship without creating
  duplicate incidents.
- Unit-to-unit calls remain distinguishable from talkgroup calls.
- Garbled audio remains linked to its radio, talkgroup, timing, and decoder
  quality evidence even when no useful transcript can be produced.
- Existing combined audio remains playable, so short acknowledgements can be
  understood in context rather than transcribed in isolation only.

## Decision

The strongest next hypothesis is not that source identifiers can replace the
incident membership model. It is that the current pipeline is asking semantic
models and reviewers to recover structure that Trunk Recorder already knew and
Callstream discarded. Preserve the transmission ledger first. Use it to make
candidate windows smaller and more coherent, then determine with frozen shadow
metrics how much semantic membership work remains.

The first fixed live trial is recorded in
[`callstream-v2-two-hour-linkage-trial-2026-07-30.md`](callstream-v2-two-hour-linkage-trial-2026-07-30.md).
It confirms that source identifiers sharply prioritize candidate calls, while
also showing that shared dispatch identifiers and acoustically empty
source-identified transmissions prevent them from serving as membership truth.
