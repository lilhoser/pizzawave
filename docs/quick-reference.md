# Quick Reference

## Paths

| Purpose | Path |
| --- | --- |
| Config | `/etc/pizzawave/pizzad.json` |
| Token | `/etc/pizzawave/pizzad.token` |
| Database | `/var/lib/pizzawave/pizzad.db` |
| Audio | `/var/lib/pizzawave/audio` |
| Import cache | `/var/lib/pizzawave/import-cache` |
| Engine files | `/opt/pizzawave/pizzad` |
| Service | `pizzad.service` |

## Service Commands

```bash
sudo systemctl status pizzad --no-pager
sudo systemctl restart pizzad
journalctl -u pizzad -n 100 --no-pager
journalctl -u pizzad -f
```

## Health/API Checks

```bash
curl -fsS http://127.0.0.1:8080/api/v1/health
curl -fsS http://127.0.0.1:8080/api/v1/dashboard
curl -fsS http://127.0.0.1:8080/api/v1/events/stream
```

OpenAPI:

```text
http://<host>:8080/swagger
```

## SQLite Probes

```bash
sqlite3 /var/lib/pizzawave/pizzad.db "select transcription_status, count(*) from calls group by transcription_status;"
sqlite3 /var/lib/pizzawave/pizzad.db "select status, count(*) from jobs group by status;"
sqlite3 /var/lib/pizzawave/pizzad.db "select count(*) from incidents;"
```

## Queue Checks

```bash
curl -fsS http://127.0.0.1:8080/api/v1/health
sqlite3 /var/lib/pizzawave/pizzad.db "select count(*) from calls where transcription_status in ('pending','failed');"
```

Use the web status bar and **System > Queue** for the normal view.

## Build and Package

```bash
dotnet build pizzawave.sln --configuration Release
./scripts/pizzawave build-deb --rid linux-x64
./scripts/pizzawave build-deb --rid linux-arm64
```

## Deploy Development Build

```powershell
.\scripts\deploy_pizzad_tar.ps1 -HostName lilhoser@192.168.1.173 -Rid linux-x64
.\scripts\deploy_pizzad_tar.ps1 -HostName ocroot@100.105.110.92 -SshKey 'G:\My Drive\Backups\creds\pizzapi_rpi_test_ed25519' -Rid linux-arm64
```

The tar helper automatically selects no-op, frontend-only, or backend
deployment. `deploy_pizzad_web.ps1` remains an explicit frontend-only override.
