# Incident Purity Replay

This read-only utility consumes the JSON objects emitted by the exact-incident
membership collector. It makes two source-safe decisions through the configured
model endpoint:

1. whether all calls in the existing incident describe one event;
2. whether the candidate conversation segment describes one event.

The membership gate opens only when both decisions are `one_event`. A
`multiple_events` or `unresolved` result leaves membership unresolved.

Run from a file or standard input:

```powershell
dotnet run --project utilities\IncidentPurityReplay -- case.json
Get-Content case.json -Raw | dotnet run --project utilities\IncidentPurityReplay -- -
```

The utility does not write to PizzaWave or expose application identities in the
model prompt.
