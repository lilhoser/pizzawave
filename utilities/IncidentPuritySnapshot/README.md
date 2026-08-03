# Incident Purity Snapshot

This utility preserves exact read-only collector results before live incident
membership can change. It reads one complete collector JSON object per line,
validates source identity and complete transcript coverage, deduplicates by
candidate call and incident, and atomically updates one bounded snapshot.

```powershell
collector-output |
  dotnet run --project utilities\IncidentPuritySnapshot -- `
    artifacts\trial\cases.json 150 1785590963 1785634163
```

An existing snapshot must have the same schema and fixed collection window.
One snapshot accepts at most 200 validated examples.
The utility never connects to PizzaWave and never changes incident data.
