# pizzapi - Cross-Platform UI

`pizzapi` is the Avalonia UI for PizzaWave on Linux, macOS, and Windows.
It is the primary and recommended UI going forward.

## What It Does

- Receives live callstream audio from trunk-recorder and transcribes calls
- Shows live radio traffic with filtering, grouping, search, and export
- Manages alerts and optional email notifications
- Provides AI Insights summaries (LM Link) with persisted summary history

## Requirements

- .NET 9 runtime (or self-contained publish)
- trunk-recorder + callstream plugin sending to your `listenPort` (default `9123`)
- GUI environment (X11/Wayland on Linux)

## Install

### Debian / Raspberry Pi / Ubuntu (.deb)

```bash
sudo dpkg -i pizzapi_*_arm64.deb   # or *_amd64.deb
sudo apt-get install -f
```

### Build / publish from source

```bash
git clone https://github.com/lilhoser/pizzawave.git
cd pizzawave
dotnet publish pizzapi/pizzapi.csproj -c Release -r <RID> --self-contained true -o ./publish
./publish/pizzapi
```

Common RIDs: `linux-arm64`, `linux-x64`, `osx-arm64`, `osx-x64`, `win-x64`.

## Configuration

Shared config file path:

- Windows: `%APPDATA%\pizzawave\settings.json`
- Linux/macOS: `~/.config/pizzawave/settings.json`

Important keys:

- `listenPort`
- `transcriptionEngine`, `transcriptionModelPreset`
- `emailProvider` (`gmail` or `yahoo`), `emailUser`, `emailPassword` (app password)
- `lmLinkEnabled`, `lmLinkBaseUrl`, `lmLinkModel`, `lmLinkApiKey`
- `dailyInsightsDigestEnabled`
- SFTP archive keys: `archiveSftpEnabled`, `archiveSftpHost`, `archiveSftpRemoteRoot`, `archiveLocalCachePath`
- `AutoCleanupCalls`, `MaxCallsToKeep`

## UI Overview

Top menu buttons:

- `Radio`: call views (`24h`, `2d`, `Week`, `Range`)
- `Insights`: summary views (`Today`, `24h`, `2d`, `Week`, `Range`)
- `View`, `Alerts`, `Settings`, `Cleanup`

Notes:

- `Radio` defaults to `24h`.
- `Live` radio submenu option has been removed.
- On startup, `24h` is primed from persisted capture history, then updated by incoming live calls.
- `24h` view is a rolling in-memory window.
- `2d`, `Week`, and `Range` are historical disk-backed views.
- `Archive...` opens the SFTP archive browser when `Settings -> Archives` is configured.
- Archive sessions are isolated from live/local captures and show `ARCHIVE` until you choose `Return to Live + Local`.
- Opening `Insights` auto-selects `Today`.
- `Today` can generate summaries from current live backlog if needed.
- `24h/2d/Week/Range` load persisted summaries only.

## Insights Behavior (Current)

- Summaries are persisted only to:
  - `%APPDATA%/pizzawave/insights/<YYYY>/<MM>/<DD>/<HHmm>.json`
- No separate `daily` or `index` folder is used for storage.
- Live summarization is heuristic-driven:
  - starts when 50 unsummarized live calls accumulate
  - adaptive batch size at higher load
  - retry backoff on LM failures
- Failed LM runs are not persisted.
- Footer status shows progress such as `Next insight in N calls`.

## SFTP Archives

`pizzapi` can browse and load archived Trunk Recorder `.bin` call data from an SFTP server.

Configure the server in `Settings -> Archives`:

- Enable SFTP archive source
- Host, port, username
- Authentication mode: password or private key
- Remote root path
- Local archive cache path

Use `Test Connection` to validate the root path. Use `Archive...` from the Radio side menu or the Archives settings tab to open the archive browser. The browser can filter by date range, list remote archive folders/files, download the selected item, and load it through the existing offline call loader.

Archive data is intentionally kept separate from normal live/local history:

- Live/local history reads from `%APPDATA%/pizzawave/captures` on Windows or the platform equivalent.
- SFTP archive data defaults to `%APPDATA%/pizzawave/offline/sftp-cache`.
- Archive mode reads only the selected cached archive folder.
- New live calls may continue to arrive in the background, but they do not appear in the archive view until you return to `Live + Local`.

## Email

- Gmail and Yahoo are supported using SMTP + app passwords.
- Use `Settings -> Test` to validate credentials.
- Diagnostics include SMTP host/provider/status on failure.

## Runtime Flags

`pizzapi` supports optional command-line flags:

- `--no-listener`
- `--no-transcribe`
- `--no-audio-encode`
- Linux tuning: `--x11-tuned`, `--software-rendering`

## See Also

- [Main docs](README.md)
- [pizzapi app README](../pizzapi/README.md)
- [Raspberry Pi walk-through](../pizzapi/WALK-THROUGH.md)
- [pizzalib settings](../pizzalib/README.md)
