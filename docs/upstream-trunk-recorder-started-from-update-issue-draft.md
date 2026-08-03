# Draft upstream issue: expose update-started call provenance

Status: published as [Trunk Recorder issue #1152](https://github.com/TrunkRecorder/trunk-recorder/issues/1152).

## Proposed issue title

Expose whether a call started from a channel update

## Proposed issue body

### Problem

Trunk Recorder already tracks whether `Call_impl` was created from the original
channel grant or from a later update. It uses that state for the existing
`Call was UPDATE not GRANT` log, but the fact is not available to call-end
plugins or consumers of call JSON.

A plugin therefore cannot distinguish these two cases from the current call
contract:

1. Trunk Recorder observed the original grant that created the call.
2. Trunk Recorder first observed an update for a call that was already in
   progress.

This should remain factual provenance. Starting from an update does not, by
itself, prove that recorded audio is truncated or otherwise incomplete.

### Requested capability

Expose a boolean such as `started_from_update` to call-end plugins and in call
JSON, populated from the existing `Call_impl::was_update` state.

The ordered transmission list already contains the first observed transmission
timestamp, so this capability should not add a second timestamp or encode an
inference about missing audio.

### Plugin compatibility

`Call_Data_t` is passed by value across the C++ plugin boundary, and the current
plugin interface has no version negotiation or extension mechanism. Adding a
field changes the struct size even if the field is appended to preserve all
existing member offsets. Core and plugins must therefore be rebuilt from a
compatible source pair.

If maintainers prefer a versioned plugin-data extension instead of extending
`Call_Data_t`, that would address the ABI concern more generally, but it is a
larger interface change than exposing this existing state.

### Acceptance criteria

- Calls created from an original `GRANT` report `started_from_update=false`.
- Calls created from an `UPDATE` report `started_from_update=true`.
- Call-end plugins and call JSON receive the same factual value.
- No receiver-specific completeness conclusion or redundant timestamp is added
  to the core contract.
- GRANT-versus-UPDATE behavior has focused automated coverage.

### Proposed implementation

An accompanying pull request implements the minimal `Call_Data_t`, `Call`, and
call-JSON changes and adds focused tests.

Pull request: [#1153](https://github.com/TrunkRecorder/trunk-recorder/pull/1153)

### Validation completed for the proposed implementation

- Full build of Trunk Recorder, bundled plugins, and Callstream: passed.
- CTest: 2/2 passed, including GRANT/UPDATE classification and Callstream
  provenance behavior.
- Staged Callstream installation rewrote its RUNPATH to
  `/usr/local/lib/trunk-recorder`.
