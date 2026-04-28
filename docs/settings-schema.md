# Settings Schema

Canonical `settings.json` keys for current builds.

Primary path:
- Windows: `%APPDATA%\pizzawave\settings.json`
- Linux/macOS: `~/.config/pizzawave/settings.json`

## Core Keys

| Key | Type | Default | Notes |
|---|---|---|---|
| `listenPort` | number | `9123` | callstream listener port |
| `TraceLevelApp` | number/enum | `Error` | app logging level |
| `AutostartListener` | bool | `true` | starts listener on app start |
| `transcriptionEngine` | string | `"whisper"` | `whisper` or `vosk` |
| `transcriptionModelPreset` | string | `""` | preset model alias |
| `talkgroups` / `Talkgroups` | array | `[]` | legacy/interop field; PizzaPi resolves via `talkgroup-mappings.json` |
| `Alerts` | array | `[]` | alert rules |
| `analogChannels` | number | `1` | PCM metadata |
| `analogBitDepth` | number | `16` | PCM metadata |
| `analogSamplingRate` | number | `8000` | PCM metadata |

## Email / Alerts

| Key | Type | Default | Notes |
|---|---|---|---|
| `emailProvider` | string | `"gmail"` | `gmail` or `yahoo` |
| `emailUser` | string | `""` | sender email |
| `emailPassword` | string | `""` | app password (not account password) |
| `AutoplayAlerts` | bool | `false` | play matched alert audio |
| `SnoozeDurationMinutes` | number | `15` | alert audio snooze duration |

## Insights / LM Link

| Key | Type | Default | Notes |
|---|---|---|---|
| `lmLinkEnabled` | bool | `false` | enables insights via LM Link |
| `lmLinkBaseUrl` | string | `""` | LM Link host/base URL |
| `lmLinkApiKey` | string | `""` | optional bearer token |
| `lmLinkModel` | string | `""` | OpenAI-compatible model name |
| `lmLinkTimeoutMs` | number | `600000` | request timeout |
| `lmLinkMaxRetries` | number | `2` | retry count |
| `dailyInsightsDigestEnabled` | bool | `false` | requires LM Link + email creds |

## SFTP Archives

These keys configure the optional `pizzapi` SFTP archive browser for Trunk Recorder `.bin` call archives. Downloaded archive data is cached separately from normal live/local captures and is loaded only while the UI is in archive mode.

| Key | Type | Default | Notes |
|---|---|---|---|
| `archiveSftpEnabled` | bool | `false` | enables the SFTP archive source |
| `archiveSftpHost` | string | `""` | SFTP host name or IP address |
| `archiveSftpPort` | number | `22` | SFTP port |
| `archiveSftpUsername` | string | `""` | SFTP username |
| `archiveSftpAuthMode` | string | `"password"` | `password` or `privatekey` |
| `archiveSftpPassword` | string | `""` | password for password auth |
| `archiveSftpPrivateKeyPath` | string | `""` | local private key path for private key auth |
| `archiveSftpPrivateKeyPassphrase` | string | `""` | optional private key passphrase |
| `archiveSftpRemoteRoot` | string | `""` | remote folder containing archive folders or `.bin` files |
| `archiveLocalCachePath` | string | `%APPDATA%/pizzawave/offline/sftp-cache` | local cache root for downloaded archive data |

## UI / Performance

| Key | Type | Default | Notes |
|---|---|---|---|
| `SortMode` | number | `0` | UI call ordering |
| `GroupMode` | number | `0` | UI grouping mode |
| `FontSize` | number | `14.0` | base display font size |
| `AutoCleanupCalls` | bool | `true` | in-memory call cleanup |
| `MaxCallsToKeep` | number | `100` | max calls retained in UI list |

## Notes

- Use the exact key casing above for new configs.
- Legacy mixed-case duplicates (example `ListenPort`) should be removed.
- Keep `settings.json` permissions restricted; it may contain sensitive credentials.
