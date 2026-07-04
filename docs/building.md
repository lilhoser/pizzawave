# Building

PizzaWave targets .NET 9. The active build artifact is `pizzad`, a Worker +
ASP.NET Core host with a bundled React/TypeScript web UI.

## Prerequisites

- .NET 9 SDK.
- Node.js/npm for the web UI.
- Bash environment for package scripts.
- `dpkg-deb` when building `.deb` packages.

## Solution Projects

| Project | Purpose |
| --- | --- |
| `pizzad` | Active engine, API, service, and bundled web UI |

## Build Everything

```bash
dotnet build pizzawave.sln --configuration Release
```

## Build the Web UI

```bash
cd pizzad/web
npm install
npm run build
```

The web build writes static assets consumed by `pizzad`.

## Publish `pizzad`

```bash
dotnet publish pizzad/pizzad.csproj \
  --configuration Release \
  --runtime linux-x64 \
  --self-contained true \
  -p:SelfContained=true \
  -p:PublishSingleFile=false \
  -o artifacts/pizzad-linux-x64
```

ARM64:

```bash
dotnet publish pizzad/pizzad.csproj \
  --configuration Release \
  --runtime linux-arm64 \
  --self-contained true \
  -p:SelfContained=true \
  -p:PublishSingleFile=false \
  -o artifacts/pizzad-linux-arm64
```

## Build Packages

```bash
./scripts/pizzawave build-deb --rid linux-x64
./scripts/pizzawave build-deb --rid linux-arm64
```

## Local Development Run

Use the `pizzad local` launch profile or run:

```bash
dotnet run --project pizzad/pizzad.csproj --launch-profile "pizzad local"
```

Local defaults:

- Web/API: `http://127.0.0.1:18080`
- Callstream listener: `127.0.0.1:19123`
- SQLite/audio state: `artifacts/`

Smoke test:

```powershell
.\scripts\smoke_pizzad.ps1 -UseRunningServer -HttpPort 18080 -CallstreamPort 19123
```

## Build Hygiene

Do not check in `artifacts/`, generated package files, remote deploy scratch
content, or one-off test outputs. The checked-in web assets under
`pizzad/wwwroot` should correspond to the current `pizzad/web` source.

## Development Deploy Helpers

Use the web-only deploy for frontend-only changes:

```powershell
.\scripts\deploy_pizzad_web.ps1 -HostName ocroot@10.0.0.115 -SshKey 'G:\My Drive\Backups\creds\pizzapi_rpi_test_ed25519'
```

This skips `npm ci`, copies only `wwwroot`, and does not restart `pizzad`.
Pass `-NpmCi` when `package-lock.json` changed.

Use the full tar deploy when backend/runtime files changed:

```powershell
.\scripts\deploy_pizzad_tar.ps1 -HostName ocroot@10.0.0.115 -SshKey 'G:\My Drive\Backups\creds\pizzapi_rpi_test_ed25519' -Rid linux-arm64
```
