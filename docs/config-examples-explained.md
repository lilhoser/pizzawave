# Config Examples Explained

The files in this directory are trunk-recorder examples for PizzaWave-managed
systems. They are not complete site-specific configs.

## callstream Example

`sample-config-callstream.json` demonstrates a trunk-recorder config with the
callstream plugin enabled. In a current PizzaWave setup, callstream should send
completed calls to localhost:

```json
{
  "name": "callstream",
  "library": "libcallstream.so",
  "address": "127.0.0.1",
  "port": 9123
}
```

The wizard can patch an existing config and creates a backup before writing.

## Source Coverage

Each RTL-SDR source has a center frequency and sample rate. The wizard uses the
configured systems, control channels, available SDRs, and desired sample rate to
recommend source coverage. If a system cannot be fully covered, the wizard
should explain what is missing and whether it is safe to continue.

## Recorders

`digitalRecorders` controls how many simultaneous digital voice recordings a
source can handle. Too few recorders can cause missed or failed voice calls even
when control-channel decode looks acceptable.

PizzaWave-managed Setup configs size
`digitalRecorders` per source from the voice frequencies actually covered by
that source window. The generated value uses the covered voice-channel count,
adds two slots of burst headroom per covered system, rounds up to an even
number, keeps a minimum of 4, and caps generated values at 24. Existing configs
with a higher manual value are preserved.

## captureDir

Fresh PizzaWave-managed configs omit `captureDir` by default. Callstream sends
completed calls to `pizzad`, and `pizzad` stores canonical audio. Existing
trunk-recorder rigs may still use `captureDir`; local import can ingest those
recordings.

## Talkgroups

PizzaWave owns a JSON talkgroup catalog. Setup imports RadioReference or CSV
rows into that catalog, then generates the trunk-recorder CSV from enabled rows.

- `trunkRecorder.talkgroupCatalogPath` is the PizzaWave-owned JSON catalog.
- `trunkRecorder.talkgroupsPath` is generated output for trunk-recorder.
- Disabled and deleted talkgroups are excluded from the generated CSV.
- A trunk-recorder restart is required before CSV changes affect ingest.
- Existing calls keep their stored label/category; catalog edits affect future
  calls only.
