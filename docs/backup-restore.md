# Backup and Restore

PizzaWave backups are created from `System > Backup`.

The archive includes, when present:

- the PizzaWave SQLite database;
- recorded call audio under the configured audio root, limited by the selected
  preset window;
- PizzaWave app data, excluding backup/staging scratch directories and raw RF
  capture blobs from RF validation;
- Qdrant storage;
- PizzaWave config and admin token;
- trunk-recorder config and talkgroup CSV.

The SQLite database is snapshotted with SQLite before it is added to the
archive. Qdrant and audio files are copied from disk as they exist at backup
time, so create backups during quiet periods when possible.

App data cache directories such as `cache` and `.cache` are intentionally
excluded. Raw RF validation captures such as `.cs16`, `.u8`, `.iq`, and `.raw`
files under `appdata/rf-surveys` are also excluded; the JSON evidence and
survey metadata are kept. Portable backups should contain PizzaWave state, not
transient model downloads, temporary RF samples, or symlink-heavy package
caches that can be recreated.

## Estimate

`System > Backup` shows an estimated backup size before creation. This is a
source-size estimate across the configured files and directories for the
selected recorded-audio window; the final compressed archive size can differ.

PizzaWave does not enforce backup size or age caps. Backup creation includes all
core configuration/state and the full SQLite database. Recorded call audio can
be limited to one of the operator presets: last 24 hours, 7 days, 30 days, 60
days, or all. The audio window is based on audio file timestamps. If the
estimate is too large for the target system or available time, choose a smaller
audio window, purge old data, or run maintenance before creating the backup.

Existing backup archives can be downloaded or deleted from the same page. Delete
only removes the local archive; it does not touch live PizzaWave data.

## Restore Flow

Restore is staged first. Uploading a backup archive validates its manifest and
checks file sizes and SHA-256 hashes. The staged restore remains on
`System > Backup` until the operator applies or cancels it.

Applying restore overwrites the backed-up PizzaWave/TR files, restarts Qdrant
and trunk-recorder when present, and schedules a pizzad restart. When the app
comes back, `System > Backup` shows the restore result. Setup is only required
if the restored state itself requires host prerequisites or site setup work.

When the SQLite database is restored, PizzaWave removes stale SQLite WAL/SHM
sidecar files before and after copying the database snapshot. Those sidecars
belong to the previous live database and must not be reused with the restored
snapshot.

Canceling a staged restore clears the pending restore state and removes the
temporary staging directory. It does not change live PizzaWave, trunk-recorder,
database, audio, or Qdrant files.

Do not treat restore as a backfill path. It is for disaster recovery or cloning
a known PizzaWave state.

## Reset Flow

`System > Backup > Reset` replaces the old clean-install and migration entry
points. Presets are multi-selectable:

- `Data Only`: clears historical calls, audio, incidents, transcripts, vectors,
  queue/job/metric history, and related operational data while preserving the
  current site/configuration. Audit/setup history can be preserved.
- `Site Reset`: clears operational data plus site-specific state such as
  monitored areas, talkgroup catalog/CSV, TR config, RF validation history, and
  Setup activity/evidence. Use this when the device is relocating or the
  monitored site plan should start clean.
- `Full Reset`: performs a site reset and returns PizzaWave to the first-run
  prerequisite wizard. Backup archives remain available.

After `Site Reset`, open Setup to choose location, systems/sites, talkgroups, RF
path, SDR/source planning, RF validation, and final Apply & Resume. After
`Full Reset`, complete first-run host prerequisites first, then Setup.
