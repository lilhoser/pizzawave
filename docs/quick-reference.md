# Quick Reference

Common commands, paths, ports, environment variables, and other frequently-needed information for pizzawave.

## Default Ports

| Service | Port | Protocol | Description |
|---------|------|----------|-------------|
| callstream/pizzawave | 9123 | TCP | Audio streaming from trunk-recorder |

## Configuration Paths

| Platform | Settings File | Logs | Models | Captures |
|----------|---------------|------|--------|----------|
| Windows | `%APPDATA%\pizzawave\settings.json` | `%APPDATA%\pizzawave\Logs\` | `%APPDATA%\pizzawave\model\` | `%APPDATA%\pizzawave\` |
| Linux | `~/.config/pizzawave/settings.json` | `~/.config/pizzawave/Logs/` | `~/.config/pizzawave/model/` | `~/.config/pizzawave/` |
| macOS | `~/.config/pizzawave/settings.json` | `~/.config/pizzawave/model/` | `~/.config/pizzawave/model/` | `~/.config/pizzawave/` |
| System Install | `/etc/pizzapi/appsettings.json` | `/var/log/pizzapi/` | `/var/lib/pizzapi/model/` | `/var/lib/pizzapi/` |

## Common Commands

### Service Management (Linux)

```bash
# Start pizzapi service
sudo systemctl start pizzapi

# Stop pizzapi service
sudo systemctl stop pizzapi

# Restart pizzapi service
sudo systemctl restart pizzapi

# Enable auto-start on boot
sudo systemctl enable pizzapi

# View service status
sudo systemctl status pizzapi

# View live logs
journalctl -u pizzapi -f

# View last 50 log lines
journalctl -u pizzapi -n 50
```

### trunk-recorder Service

```bash
# Restart trunk-recorder
sudo systemctl restart trunk-recorder

# View live logs
sudo journalctl -u trunk-recorder -f

# Check service status
sudo systemctl status trunk-recorder
```

### Network Diagnostics

```bash
# Check if port 9123 is listening
netstat -tlnp | grep 9123
# or
sudo ss -tlnp | grep 9123

# Test connectivity to pizzawave
telnet <pizzawave-ip> 9123
# or
nc -zv <pizzawave-ip> 9123

# Find local IP address
ip addr show | grep inet

# Check firewall rules (ufw)
sudo ufw status
sudo ufw allow 9123/tcp
```

### Build Commands

```bash
# Restore dependencies
dotnet restore pizzawave.sln

# Build all projects (Debug)
dotnet build pizzawave.sln

# Build all projects (Release)
dotnet build pizzawave.sln -c Release

# Publish for Raspberry Pi
dotnet publish pizzapi/pizzapi.csproj -c Release -r linux-arm64 --self-contained true -o ./publish

# Publish for Linux x64
dotnet publish pizzapi/pizzapi.csproj -c Release -r linux-x64 --self-contained true -o ./publish

# Clean build artifacts
dotnet clean pizzawave.sln
```

### Package Installation (Linux)

```bash
# Install .deb package
sudo dpkg -i pizzapi_*.deb

# Fix missing dependencies
sudo apt-get install -f -y

# Remove package
sudo dpkg -r pizzapi
```

## Environment Variables

| Variable | Description | Default |
|----------|-------------|---------|
| `DOTNET_ENVIRONMENT` | Runtime environment | `Production` |
| `DOTNET_ROOT` | .NET installation path | System default |
| `DISPLAY` | X11 display (WSL2/GUI) | `:0` |

## Talkgroups CSV Format

Generate the talkgroups CSV manually:
1. Visit the RadioReference talkgroups section for the system you are monitoring (example: https://www.radioreference.com/db/sid/4879).
2. Copy the HTML table for the talkgroups you want.
3. Convert that HTML table to CSV using a tool like convertcsv.com or an AI interface.
4. Remove any invalid/extra columns in Excel, Google Sheets, or a similar tool.

The CSV must include this header line:
```csv
Decimal,Mode,Alpha Tag,Description,Tag,Category
1,D,FDISPATCH,Fire Dispatch,Fire,Dispatch
2,D,FDISPATCH2,Fire Dispatch 2,Fire,Dispatch
3,D,PDISPATCH,Police Dispatch,Police,Dispatch
```

| Column | Description |
|--------|-------------|
| `Decimal` | Talkgroup ID (decimal) |
| `Mode` | D=Digital, A=Analog |
| `Alpha Tag` | Short display name |
| `Description` | Full description |
| `Tag` | Category tag for filtering |
| `Category` | Sub-category |

## Minimum Required Settings

```json
{
  "listenPort": 9123,
  "TraceLevelApp": "Information",
  "AutostartListener": true
}
```

## Trace Levels

| Level | Description |
|-------|-------------|
| `Verbose` | Maximum detail, high disk I/O |
| `Debug` | Debug-level messages |
| `Information` | Normal operational messages (recommended) |
| `Warning` | Warnings only |
| `Error` | Errors only |
| `Critical` | Critical errors only |
| `None` | No logging |

## System Dependencies

### Ubuntu/Debian (Minimum)
```bash
libicu-dev libssl3 zlib1g
```

### Ubuntu/Debian (with GUI)
```bash
libicu-dev libssl3 zlib1g libx11-6 libxext6 libxrender1 libxtst6 libxi6 libfontconfig1 libx11-xcb1 libxcb1 libxfixes3
```

## Useful Resources

| Resource | URL |
|----------|-----|
| RadioReference | https://www.radioreference.com/ |
| SDR Calculator | https://alertapi.alertpage.net/sdr/ |
| trunk-recorder Docs | https://trunkrecorder.com/docs/ |
| Whisper Models | https://huggingface.co/ggerganov/whisper.cpp |

## See Also

- [Quick Start Guide](quickstart.md) - 5-minute setup
- [Deployment Guide](deployment.md) - Production deployment
- [Building Guide](building.md) - Build from source
- [Main README](README.md) - Project overview
