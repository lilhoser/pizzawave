# Deployment

PizzaWave deployment is package-first. The normal artifact is a self-contained
`.deb` containing `pizzad`, the bundled web UI, scripts, native runtime
dependencies, and systemd unit files.

## Supported Targets

| Target | Status | RID |
| --- | --- | --- |
| Ubuntu 24.04 LTS x64 | Primary | `linux-x64` |
| Raspberry Pi OS/Debian ARM64 | Supported | `linux-arm64` |
| Windows desktop | Development only | n/a |

## Build a Package

From the repo root:

```bash
./scripts/pizzawave build-deb --rid linux-x64
./scripts/pizzawave build-deb --rid linux-arm64
```

Packages are written under `artifacts/packages/`.

## Install or Upgrade

```bash
sudo apt install ./artifacts/packages/pizzawave_0.1.0_amd64.deb
```

On ARM64:

```bash
sudo apt install ./artifacts/packages/pizzawave_0.1.0_arm64.deb
```

The package is self-contained; the target host does not need a separate .NET
runtime for `pizzad`.

## Service Layout

| Item | Path |
| --- | --- |
| Application | `/opt/pizzawave/pizzad` |
| Config | `/etc/pizzawave/pizzad.json` |
| Token | `/etc/pizzawave/pizzad.token` |
| Database | `/var/lib/pizzawave/pizzad.db` |
| Audio | `/var/lib/pizzawave/audio` |
| Import cache | `/var/lib/pizzawave/import-cache` |
| Service | `pizzad.service` |

## Service Operations

```bash
sudo systemctl restart pizzad
sudo systemctl stop pizzad
sudo systemctl start pizzad
sudo systemctl status pizzad --no-pager
journalctl -u pizzad -f
```

The web UI also exposes service restart actions in **System** when sudoers
support is configured.

## Trunk-Recorder Integration

`pizzad` expects callstream to deliver completed call payloads to:

```text
127.0.0.1:9123
```

Setup can patch an existing trunk-recorder config and creates a timestamped
backup before writing. A fresh PizzaWave-managed config omits `captureDir` by
default because `pizzad` owns the canonical audio store.

If side-loading onto an existing trunk-recorder system, keep the existing config
until Setup has backed it up and validated callstream.

## Imports

PizzaWave supports two import sources:

- **Local import** from existing trunk-recorder recordings on the same host.
- **SFTP import** from a remote archive, such as a Synology SFTP share.

Imports copy audio into the local PizzaWave audio store and write normal call
records into SQLite. They do not modify the source archive. Large imports run as
jobs with guardrails; imported calls suppress live/email alert notifications.

## Fast Development Deploys

For rapid iteration during development, the repo includes an automatic direct
deploy helper. It compares deployable hashes with the live host and selects the
smallest valid operation.

The standard development command handles backend and frontend changes:

```powershell
.\scripts\deploy_pizzad_tar.ps1 -HostName user@host -Rid linux-x64
.\scripts\deploy_pizzad_tar.ps1 -HostName ocroot@10.0.0.115 -SshKey 'G:\My Drive\Backups\creds\pizzapi_rpi_test_ed25519' -Rid linux-arm64
```

Its paths are:

- no changed deployable inputs: verify live health only;
- frontend-only change: rebuild when required, upload only `wwwroot`, and do not
  restart `pizzad`;
- backend change: publish the requested RID, reuse exact cached artifacts where
  possible, install the backend, restart `pizzad`, and verify health.

The helper records local cache metadata under ignored `artifacts/` paths and a
small `.pizzawave-deploy.json` manifest beside the live application. Every
build, archive, upload, remote install, and health check fails closed and emits
stage timing.

`-WebOnly` and `-BackendOnly` override automatic selection. `-ForceBuild`
invalidates local build caches, while `-ForceDeploy` repeats the smallest valid
deployment. The `deploy_pizzad_web.ps1` wrapper remains available as an explicit
web-only convenience command.

This is not the preferred release path. Use `.deb` packages for normal deploys.

## Rollback

Before risky changes:

```bash
sudo systemctl stop pizzad
sudo cp /etc/pizzawave/pizzad.json /etc/pizzawave/pizzad.json.backup
sudo cp /var/lib/pizzawave/pizzad.db /var/lib/pizzawave/pizzad.db.backup
sudo systemctl start pizzad
```

For package rollback, reinstall the prior `.deb` and restore the saved config or
database only if required.
