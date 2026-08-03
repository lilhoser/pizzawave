# Incident Target Membership Replay

This utility exercises the exact-existing-incident adapter without changing
PizzaWave data.

Run one saved case:

```powershell
dotnet run --project utilities\IncidentTargetMembershipReplay -- case.json
```

Use `-` instead of a filename to read one replay object from standard input.

Collect eligible cases from a read-only SQLite database:

```text
sqlite3 -readonly database.db \
  -cmd ".parameter set :start_unix 1785543618" \
  -cmd ".parameter set :end_unix 1785550818" \
  ".read utilities/IncidentTargetMembershipReplay/collect-eligible.sql"
```

The collector requires a same-system, same-talkgroup, adjacent appearance of
the same transmitting-radio identifier within 60 seconds. It excludes possibly
incomplete transmission boundaries and candidates without usable transcripts.
The directly linked call must belong to an unmerged incident containing one to
five completely usable calls. Candidates already in that incident are excluded.

The query emits complete replay objects but does not expose the source identifier
to the model. It does not write to the database.
