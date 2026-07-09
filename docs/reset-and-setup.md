# PizzaWave Reset and Site Setup

Use `System > Backup > Reset` when PizzaWave needs a clean operational state,
a new monitored site plan, or a full return to first-run host prerequisites.

Reset is different from backup/restore:

- backup/restore makes the system look like the backed-up system, including
  historical calls, audio, vectors, incidents, TR config, talkgroups, and
  monitored areas;
- reset intentionally clears selected state and then sends the operator to the
  appropriate next workflow.

## Reset Presets

Presets are multi-selectable.

`Data Only` clears historical call data and operational history while
preserving the current site/configuration. It clears calls, audio, incidents,
transcripts, queue/job/metric history, Qdrant call vectors, and related runtime
data. The operator can preserve audit/setup history so a clean data slate still
retains change accountability.

`Site Reset` includes the data reset and also clears site-specific setup state:
monitored areas, generated talkgroup files/catalog, TR config, RF validation
history, Setup activity/evidence, and old source/site assumptions. Use this
when the device is relocating or the monitored systems/sites should be rebuilt.

`Full Reset` includes the site reset and returns PizzaWave to the first-run
wizard. Use this when host prerequisites should be rechecked from scratch.
Backup archives are preserved.

When `Create backup before reset` is enabled, PizzaWave creates a normal backup
before clearing anything. If backup creation fails, reset stops.

## First-Run Boundary

First-run only prepares host prerequisites:

- trunk-recorder install/reuse;
- optional LM Link support for AI Insights;
- optional native Qdrant support.

After first-run finishes, PizzaWave opens Setup. Site-specific configuration is
not handled by first-run.

## Setup Boundary

Setup owns live monitoring intent and site configuration:

- location label, notes, and optional monitored/geolocation areas;
- systems, sites, RadioReference IDs, control channels, and talkgroup catalog;
- hardware/RF path and SDR inventory;
- waterfall, P25 ID, RF sweep, and call-quality validation;
- SDR source assignment and TR source planning;
- final TR config draft, apply, and monitoring resume;
- setup activity/evidence.

Setup is re-entrant. Browsing Setup does not stop live monitoring. Disruptive
steps such as waterfall, RF sweep, and Apply & Resume stop or coordinate with
trunk-recorder only when needed.

## Restore Boundary

Restore is staged and applied from `System > Backup`. Applying a restore
returns to the Backup page with a result message. If the restored state needs
host prerequisites or site setup, PizzaWave surfaces that through first-run or
Setup rather than treating restore as a migration workflow.
