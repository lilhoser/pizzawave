# Backup and Restore

PizzaWave backups are created from `System > Backup`.

The archive includes, when present:

- the PizzaWave SQLite database;
- recorded call audio under the configured audio root, limited by the selected
  preset window;
- PizzaWave app data, excluding backup/staging scratch directories;
- Qdrant storage;
- PizzaWave config and admin token;
- trunk-recorder config and talkgroup CSV.

The SQLite database is snapshotted with SQLite before it is added to the
archive. Qdrant and audio files are copied from disk as they exist at backup
time, so create backups during quiet periods when possible.

App data cache directories such as `cache` and `.cache` are intentionally
excluded. Portable backups should contain PizzaWave state, not transient model
downloads or symlink-heavy package caches that can be recreated.

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

Restore is staged first. Uploading a backup archive validates its manifest,
checks file sizes and SHA-256 hashes, and returns PizzaWave to setup mode with a
`Restore` step at the front of the wizard.

Applying restore overwrites the backed-up PizzaWave/TR files, restarts Qdrant
and trunk-recorder when present, and schedules a pizzad restart. The restored
PizzaWave config is forced back into setup mode so the operator must re-run
sanity checks before normal operation resumes.

When the SQLite database is restored, PizzaWave removes stale SQLite WAL/SHM
sidecar files before and after copying the database snapshot. Those sidecars
belong to the previous live database and must not be reused with the restored
snapshot.

Canceling a staged restore clears the pending restore state and removes the
temporary staging directory. It does not change live PizzaWave, trunk-recorder,
database, audio, or Qdrant files.

After applying restore, complete the wizard checks for:

- trunk-recorder config and health;
- talkgroup CSV/catalog;
- callstream wiring;
- transcription engine;
- monitored areas;
- optional AI, Qdrant/embeddings, alerts, and calibration.

Do not treat restore as a backfill path. It is for disaster recovery or cloning
a known PizzaWave state.

For moving a rig to a different geography, RF site, frequency set, or talkgroup
plan, use [Rig Migration](migration.md) instead of backup/restore. Migration
preserves portable settings while clearing old site-specific calls, audio,
vectors, incidents, TR config, talkgroups, and monitored areas.
