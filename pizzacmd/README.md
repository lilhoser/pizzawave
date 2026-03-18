# pizzacmd - Command Line Application

`pizzacmd` is the console host for PizzaWave, built on `pizzalib`.

## Requirements

- .NET 9 runtime (or self-contained publish)
- trunk-recorder + callstream plugin
- valid `settings.json`

## Build

```bash
git clone https://github.com/lilhoser/pizzawave.git
cd pizzawave
dotnet publish pizzacmd/pizzacmd.csproj -c Release -r <RID> --self-contained true -o ./publish
```

## Usage

```bash
pizzacmd --help
pizzacmd --settings=/path/to/settings.json
pizzacmd --talkgroups=/path/to/talkgroups.csv
```

Notes:

- `--settings` path must point to an existing file.
- If no args are provided, the default settings path is used.
- `--talkgroups` imports CSV talkgroups and persists them to settings.

## CLI Caveats (Current Parser)

- The current argument parser processes only the first recognized option in a run.
- Example: `pizzacmd --settings=... --talkgroups=...` will apply only the first recognized argument.
- Safest approach:
  1. Run once with `--settings=...`
  2. Run again with `--talkgroups=...`

Example sequence:

```bash
pizzacmd --settings=/path/to/settings.json
pizzacmd --talkgroups=/path/to/talkgroups.csv
```

## Configuration

Shared path:

- Windows: `%APPDATA%\pizzawave\settings.json`
- Linux/macOS: `~/.config/pizzawave/settings.json`

Important settings include:

- `listenPort`
- `transcriptionEngine`, `transcriptionModelPreset`
- `emailProvider`, `emailUser`, `emailPassword` (app password)
- `Alerts`
- optional Insights keys (`lmLink*`, `dailyInsightsDigestEnabled`)

## Troubleshooting

- Verify listener port is open (`listenPort`, default `9123`).
- Verify trunk-recorder points to the correct host/port.
- If transcription fails, check runtime dependencies and model selection in settings.

## See Also

- [Main docs](../docs/README.md)
- [pizzapi docs](../docs/pizzapi.md)
- [pizzalib docs](../pizzalib/README.md)
