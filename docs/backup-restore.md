# Backup, Restore, and Support Packages

PizzaWave has two artifact types with different purposes:

- A **Backup** is an encrypted, same-system recovery archive. It can be
  downloaded for safekeeping, but it is not a migration or cloning format.
- A **Support Package** is a secret-free diagnostic ZIP that an operator can
  share. It cannot be restored.

## Backup

Create backups from `System > Backup`. New backups use the `.pwbak` format and
require an operator-entered passphrase of at least 12 characters, entered
twice. PizzaWave does not store the passphrase. The archive is immediately
unlocked and its manifest, sizes, and SHA-256 hashes are verified before the
job is marked complete. If PizzaWave stops during creation, the partial archive
is discarded and the job must be started again.

Every backup includes, when configured and present:

- the PizzaWave SQLite database, captured with an online SQLite snapshot;
- PizzaWave configuration, credentials, and authentication token;
- PizzaWave app data, excluding working/cache directories and raw RF captures;
- Trunk Recorder configuration and talkgroups;
- a Qdrant online collection snapshot, downloaded and checksum-verified through
  Qdrant's snapshot API when embeddings are enabled; and
- the selected recorded-audio window: none, 24 hours, 7 days, 30 days, 60
  days, or all.

The audio selection is the only component choice. Restore applies the scope
recorded in the backup and does not present another component mixer. Backups
are never automatically deleted.

Existing plaintext PizzaWave `.zip` backups remain supported as-is. They are
listed, downloadable, stageable, and restorable without conversion or special
warnings.

## Restore

Restore has a nondisruptive staging phase and a disruptive apply phase.
Uploading or choosing a local backup, entering its passphrase when encrypted,
decrypting it, validating the manifest, and checking every file do not change
live data or restart services. A staged restore remains visible until applied
or canceled. Uploaded archives use verified 4 MiB chunks. An incomplete upload
can resume after a browser reload or disconnect when the operator reselects the
same file; incomplete sessions expire after 24 hours. Whole-file SHA-256 is
verified before decryption begins.

Restore destinations are derived from the current same-system configuration;
absolute destination paths supplied by an archive are never trusted. Canceling
a staged restore deletes staging files and changes nothing live.

Apply is destructive. Before apply, PizzaWave must create and verify a backup
of the current state using the restore passphrase. The final confirmation must
name every service that will stop or restart, including Trunk Recorder. Apply
then verifies the restored manifest, SQLite integrity, configuration load,
PizzaWave health, and included dependent services. Outcomes are `Completed`,
`Completed with warnings`, or `Failed`, with a durable stage log and guided
rollback rather than an automatic restart loop.

Restore is same-system disaster recovery. It is not a backfill, migration, or
cross-system cloning path.

## Support Package

`System > Backup > Support Package` creates a shareable diagnostic ZIP. The
default window is the last 24 hours. Its manifest lists the evidence categories
and sizes, collection failures, exclusions, and redaction count.

The default package contains redacted PizzaWave and Trunk Recorder
configuration, PizzaWave/TR logs, version/runtime information, and database
table counts without database records. It excludes database files, Qdrant
data, credentials, authentication tokens, call audio, and transcript text.
PizzaWave scans the collected files after redaction and does not publish a
package if authentication-shaped material remains.

Call audio or transcript text can be added only through the explicit private-
evidence opt-in and acknowledgement. That scope is limited to 24 hours, and
transcripts are capped at 5,000 records. The manifest lists these privacy
inclusions prominently before download.

Support packages expire after seven days by default. Deleting a support
package never affects live PizzaWave data or backups.

## Reset

Reset scopes are mutually exclusive:

- `Data Only` briefly pauses PizzaWave ingest, clears operational history, and
  resumes ingest. It does not stop or restart Trunk Recorder.
- `Site Reset` also clears site/TR/RF setup state, stops Trunk Recorder, and
  leaves capture stopped until Setup is deliberately applied.
- `Full Reset` has the same capture behavior as Site Reset and returns
  PizzaWave to first-run prerequisites.

When `Create backup before reset` is selected, the passphrase is required and
the encrypted backup must complete verification before destructive work starts.
Reset without a backup requires the stronger no-recovery confirmation. Reset
and restore-apply must be tested only in automated or isolated environments
unless the operator explicitly authorizes a live recovery drill.
