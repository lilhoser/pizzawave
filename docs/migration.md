# PizzaWave Rig Migration

Use migration when a rig is moving to a different geographic area or RF site and
the old site data should not follow it.

Migration is intentionally different from backup/restore:

- backup/restore makes a rig look like the old rig, including calls, audio,
  vectors, incidents, TR config, talkgroups, and monitored areas;
- migration clears site-specific state and only carries forward the reusable
  settings explicitly selected in the wizard.

## Reset Options

The migration step contains two groups of choices.

`Backup current system` creates a normal, fully restorable PizzaWave backup
before reset. The migration reset uses the `all` audio window for this backup.
If backup creation fails, reset stops before any site data is cleared.

`Carry forward` checkboxes decide which reusable settings are copied into the
new-site setup:

- branding;
- transcription provider, model, worker, and endpoint settings;
- AI Insights endpoint, model, limits, and enablement;
- embedding endpoint and Qdrant service settings;
- alert delivery and playback settings;
- Setup RF validation probe command and timing settings.

Unchecked sections reset to PizzaWave defaults. Runtime plumbing such as the
current config path, database path, audio root, token path, HTTP listener, and
TR file paths remains in place so the running rig does not move its own files or
port during setup.

The migration reset rotates the admin token through the sudo-backed setup
helper.

## Site-Specific State

The migration reset clears or removes:

- call database rows;
- recorded call audio;
- Qdrant call-vector collection;
- incidents, alert matches, jobs, AI usage, RF health, geocode cache, and
  recommendation baselines;
- talkgroup catalog and generated talkgroup CSV;
- monitored areas;
- active profile talkgroup overrides;
- old trunk-recorder config and generated talkgroup file.

Alert rules are always cleared. Transcription deferred-talkgroup lists are
always cleared. Qdrant service settings can be carried forward, but existing
call vectors are always deleted.

## UI Flow

1. Go to `System > Backup`.
2. Click `Begin Migration...`.
3. PizzaWave enters setup mode and opens the migration step.
4. Choose whether to create a full backup and which settings to carry forward.
5. Click the red `Reset & Continue` button only when ready to clear
   site-specific state.
6. Continue through setup:
   - TR config;
   - Talkgroups;
   - Transcription;
   - Monitored areas;
   - AI Insights;
   - Vector DB / Qdrant;
   - Alerts review;
   - Finish.

`Begin Migration...` does not clear data. The destructive action is the red
`Reset & Continue` button on the migration step.

`Cancel Migration` is available from the setup header only before reset. Before
reset, it exits migration and resumes the prior setup state. After reset,
migration cannot be canceled; restore from a backup or continue setup for the
new site.

## CLI Fallback

If the UI is not reachable, use the sudo-backed helper:

```bash
sudo /usr/lib/pizzawave/scripts/pizzawave_setup_admin.sh begin-migration
```

To cancel before resetting site data:

```bash
sudo /usr/lib/pizzawave/scripts/pizzawave_setup_admin.sh cancel-migration
```

Canceling after `Reset & Continue` is rejected. Use a full backup if rollback
is needed.
