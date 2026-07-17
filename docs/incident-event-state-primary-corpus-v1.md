# Incident Event-State Primary Corpus V1

Date frozen: 2026-07-17

Status: extracted locally; development material available; held-out material
sealed from prototype review

## Source

- Host: OT
- Database: `/var/lib/pizzawave/pizzad.db`
- Audio root: `/var/lib/pizzawave/audio`
- Consistent SQLite snapshot time: 2026-07-17 20:13:02 UTC
- Snapshot SHA-256:
  `02A0F4F99D9B9344FD20ED2035D87BF0BB1ED24ADE1E198EA94C4456A613C143`
- Snapshot size: 104,800,256 bytes
- SQLite integrity check: `ok`
- Snapshot call range: 2026-07-10 13:42:48 UTC through 2026-07-17
  20:12:50 UTC
- Snapshot calls: 38,807
- Referenced audio files checked on OT: 38,807
- Missing referenced audio files: 0

The transactionally consistent snapshot was created through SQLite's backup
API while OT remained online. PizzaWave and trunk-recorder were not paused or
restarted. Only temporary snapshot and transfer files created for this export
were removed from OT afterward.

## Selection Freeze

The machine-readable plan is
`docs/incident-event-state-primary-corpus-v1-plan.json`. Its selection-plan
SHA-256 is:

`68BC06733BD09FF2AFADDACA6F0D4C647B5FA022F8F02C2016411FB8B914C883`

Selection used only UTC block timestamps, fixed split seeds, and six-hour UTC
time bands. Call counts were inspected only after the block timestamps were
frozen. No transcript, incident, alert, category, talkgroup, radio-system,
embedding, model output, or known-failure information entered selection.

Development and held-out periods are separated by the complete UTC day of
2026-07-15.

## Extracted Material

The uncommitted audio-bearing artifacts are stored outside the repository at:

`C:\projects\pizzawave-incident-corpus-source-20260717`

### Development

- Five 30-minute blocks
- 417 calls
- Corpus SHA-256:
  `F3166316953B4C22DFBA26AF5DBA5CF4BEAB4FA53A69A80F5696884B51F7EF23`
- Audio archive entries: 417
- Audio archive SHA-256:
  `D157D45EF6E9C10FC74067F77CAF81A8CE3451D7AF10BCCF7DC762528865E0FA`

The timestamp-only selection produced one block with no captured calls and one
block with two captured calls. They remain in V1. Replacing them after observing
their traffic volume would bias the corpus toward busy periods. They also
preserve an operationally important case: the event-state system must not
invent a story when observations are absent or sparse.

### Held-Out, Sealed

- Three 30-minute blocks
- 302 calls
- Corpus SHA-256:
  `F72A674B1631D99EA8AAC77AD838C2A3E503BF342734E4EA75A4E2EED4DA3685`
- Audio archive entries: 302
- Audio archive SHA-256:
  `1F87AF8E6A50939087302AD3925336496F30D02AD286F64E6DE4EFF9A3619505`

Prototype prompts, examples, and architectural adjustments must use only the
development split. The held-out corpus must not be opened for model evaluation
until the approach, run configuration, and acceptance gates are frozen.

## OT Verification

After extraction, `/api/v1/health` remained `ok`, ingest was not paused, queue
depth and pending transcriptions were zero, and recent live trunk-recorder
activity remained current. Nothing was deployed and no production incident
data was changed.
