# Draft upstream pull request: expose update-started call provenance

Status: published as [Trunk Recorder pull request #1153](https://github.com/TrunkRecorder/trunk-recorder/pull/1153).

## Proposed title

Expose whether a call started from a channel-grant update

## Related issue

Closes [#1152](https://github.com/TrunkRecorder/trunk-recorder/issues/1152).

## Proposed rationale

Trunk Recorder already records whether `Call_impl` was created from a channel
grant update rather than the original grant. That fact is used for the existing
`Call was UPDATE not GRANT` log, but it is not available to plugins or consumers
of call JSON.

This change exposes the existing state as `started_from_update`:

- a default-initialized boolean in `Call_Data_t` for plugins;
- a `Call` getter populated directly from `Call_impl::was_update`; and
- a boolean field in call JSON.

The name describes the observed control-channel event and does not infer that a
recording is truncated or incomplete. The transmission list already contains
the first observed transmission timestamp, so no additional timestamp is added.

`Call_Data_t` is passed by value across the C++ plugin boundary and the current
plugin interface does not provide ABI negotiation. This struct addition should
therefore be called out as requiring plugins to be rebuilt against the same
Trunk Recorder revision. A versioned extension would be preferable for future
binary-compatible plugin evolution, but introducing that larger interface is
not necessary to expose this existing factual state.

## Proposed test evidence

- A clean build from upstream `ebe770d` with the patch applied.
- A clean combined build with Callstream rebuilt against the modified
  `Call_Data_t`.
- Focused assertions covering an original GRANT, the first transmission of an
  UPDATE-started call, and later transmissions in the same call.
- A staged install and RUNPATH inspection, with no deployment or service
  restart.

## Patch under review

- Trunk Recorder branch: `codex/started-from-update`
- Trunk Recorder commit: `50038ff11be5c638c1c3381c070211eda602000d`
- Callstream compatibility branch: `codex/started-from-update`
- Callstream commit: `6a46546bde7728fb870ebcdc7ed64979b42247ea`

The Trunk Recorder commit contains only the receiver-agnostic factual field. It
does not include PizzaWave names, RF experiments, source affinity, or the
experimental inferred timestamp.
