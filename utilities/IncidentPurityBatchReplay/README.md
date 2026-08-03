# Incident Purity Batch Replay

This read-only utility runs the strict incident and candidate purity decisions
for every exact case in a completed snapshot. It binds results to the snapshot's
SHA-256, verifies model identity, saves atomically after each case, and resumes
only when both the evidence hash and requested model match.

One run accepts at most 200 saved examples. Requests remain sequential so the
diagnostic does not create a burst of competing model work.

```powershell
dotnet run --project utilities\IncidentPurityBatchReplay -- `
  artifacts\trial\cases.json `
  artifacts\trial\qwen35-results.json `
  http://127.0.0.1:12435/v1 `
  qwen/qwen3.6-35b-a3b@q8_0
```

The utility does not write to PizzaWave or expose application identities in a
model prompt.
