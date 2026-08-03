# Incident Purity Determinism Replay

This diagnostic utility submits one frozen purity scope repeatedly. Every
iteration uses a new HTTP client, records the exact outbound request SHA-256,
preserves the raw model response, and saves atomically after each request. It
does not query or write PizzaWave.

```powershell
dotnet run --project utilities\IncidentPurityDeterminismReplay -- `
  artifacts\trial\cases.json `
  artifacts\trial\incident-7969-determinism.json `
  1599386 20 `
  http://127.0.0.1:12435/v1 `
  qwen/qwen3.6-35b-a3b@q8_0 `
  incident
```

The result reports request-body and disposition invariance. Raw request content
is not duplicated in the result because the frozen snapshot already preserves
the evidence; the exact transmitted body is bound by its SHA-256.
