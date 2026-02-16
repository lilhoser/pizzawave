# Deployment Guide

This guide covers deploying pizzawave applications to various platforms, with focus on `pizzapi` for Linux/macOS and `pizzacmd` for headless operation.

## Target Platforms

| Platform | Application | Package Type | Architecture |
|----------|-------------|--------------|--------------|
| Raspberry Pi 5 | pizzapi | .deb | ARM64 |
| WSL2 (Windows) | pizzapi | .deb | x64 |
| Linux Server | pizzacmd/pizzapi | Manual | x64/ARM64 |
| macOS | pizzapi | Manual | ARM64/x64 |
| Windows | pizzaui | Manual | x64 |

---

## Raspberry Pi Deployment

### Prerequisites

* Raspberry Pi 5 (or Pi 4 with 4GB+ RAM)
* Raspberry Pi OS (64-bit) with desktop
* Network connection to trunk-recorder system
* At least 8GB SD card (16GB+ recommended)

### Step 1: Download the Package

From a release tag on GitHub:
```bash
cd ~
wget https://github.com/lilhoser/pizzawave/releases/download/v1.0.5/pizzapi_1.0.5_arm64.deb
```

Or download the latest artifact from CI (for testing):
```bash
# Check GitHub Actions for latest build artifacts
```

### Step 2: Install

```bash
# Install the package
sudo dpkg -i pizzapi_*.deb

# Fix any missing dependencies automatically
sudo apt-get install -f -y
```

The installer will:
1. Copy binaries to `/opt/pizzapi/`
2. Install system dependencies (libicu, libssl, X11 libraries)
3. Create configuration directory at `/etc/pizzapi/`
4. Install systemd service file

### Step 3: Configure

```bash
# Edit the configuration file
sudo nano /etc/pizzapi/appsettings.json
```

Minimum required settings:
```json
{
  "listenPort": 9123,
  "TraceLevelApp": "Information",
  "AutostartListener": true
}
```

### Step 4: Start the Service

```bash
# Enable auto-start on boot
sudo systemctl enable pizzapi

# Start the service
sudo systemctl start pizzapi

# Check status
sudo systemctl status pizzapi
```

### Step 5: Verify Operation

```bash
# View logs
journalctl -u pizzapi -f

# Check listening port
netstat -tlnp | grep 9123

# Verify process is running
ps aux | grep pizzapi
```

### Step 6: Configure trunk-recorder

Update your trunk-recorder `plugins` section:

```json
{
  "plugins": [
    {
      "name": "callstream",
      "library": "libcallstream.so",
      "address": "<raspberry-pi-ip>",
      "port": 9123,
      "streams": [
        {
          "TGID": 0,
          "shortName": "your_system"
        }
      ]
    }
  ]
}
```

Restart trunk-recorder after configuration changes.

---

## WSL2 Deployment (Testing)

Use WSL2 to test pizzapi on Windows before deploying to Raspberry Pi.

### Prerequisites

* Windows 10/11 with WSL2 enabled
* Debian or Ubuntu distribution installed
* .NET 9.0 SDK (for building from source)

### Step 1: Enable WSL2 (if not already)

```powershell
# From Windows PowerShell (Admin)
wsl --install -d Debian
wsl --set-default-version 2
```

### Step 2: Download Package

From within WSL2:
```bash
cd ~
wget https://github.com/lilhoser/pizzawave/releases/latest/download/pizzapi_*_amd64.deb
```

### Step 3: Install

```bash
sudo dpkg -i pizzapi_*.deb
sudo apt-get update
sudo apt-get install -f -y
```

### Step 4: Run (GUI Mode)

WSL2 with WSLg supports GUI applications:

```bash
# Launch pizzapi with display
pizzapi
```

### Step 5: Run (Headless Mode)

For background operation:

```bash
# Run in background
pizzapi --headless &

# Or use nohup
nohup pizzapi --headless > pizzapi.log 2>&1 &
```

### Network Configuration

Ensure trunk-recorder can reach WSL2:

```bash
# Find WSL2 IP address
ip addr show eth0 | grep "inet\b"

# Example output: inet 172.23.192.16/28
```

Configure trunk-recorder to connect to this IP address.

---

## Linux Server Deployment (Headless)

For servers without a desktop environment, use `pizzacmd` or run `pizzapi` in headless mode.

### Option A: Using pizzacmd

```bash
# Build pizzacmd
cd ~/pizzawave
dotnet publish pizzacmd/pizzacmd.csproj \
  -c Release \
  -r linux-x64 \
  --self-contained true \
  -o ~/pizzacmd-publish

# Create systemd service
sudo nano /etc/systemd/system/pizzacmd.service
```

Service file content:
```ini
[Unit]
Description=Pizzawave Command Line
After=network.target

[Service]
Type=simple
ExecStart=/home/username/pizzacmd-publish/pizzacmd --talkgroups=/home/username/talkgroups.csv
WorkingDirectory=/home/username/pizzacmd-publish
Restart=always
User=username

[Install]
WantedBy=multi-user.target
```

```bash
# Enable and start
sudo systemctl daemon-reload
sudo systemctl enable pizzacmd
sudo systemctl start pizzacmd
```

### Option B: Using pizzapi Headless

```bash
# Install as shown in Raspberry Pi section
sudo dpkg -i pizzapi_*.deb

# Edit systemd service for headless mode
sudo nano /etc/systemd/system/pizzapi.service
```

Ensure service file has:
```ini
[Service]
ExecStart=/opt/pizzapi/pizzapi --headless
Environment=DOTNET_ENVIRONMENT=Production
```

---

## macOS Deployment

### Step 1: Build from Source

```bash
# Clone and build
git clone https://github.com/lilhoser/pizzawave.git
cd pizzawave
dotnet publish pizzapi/pizzapi.csproj \
  -c Release \
  -r osx-arm64 \  # or osx-x64 for Intel Mac
  --self-contained true \
  -o ~/pizzapi-publish
```

### Step 2: Install

```bash
# Copy to Applications
sudo mkdir -p /Applications/pizzapi
sudo cp -r ~/pizzapi-publish/* /Applications/pizzapi/
sudo chmod +x /Applications/pizzapi/pizzapi
```

### Step 3: Create Launch Daemon

```bash
sudo nano /Library/LaunchDaemons/com.pizzawave.pizzapi.plist
```

Content:
```xml
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN"
  "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>com.pizzawave.pizzapi</string>
    <key>ProgramArguments</key>
    <array>
        <string>/Applications/pizzapi/pizzapi</string>
        <string>--headless</string>
    </array>
    <key>RunAtLoad</key>
    <true/>
    <key>KeepAlive</key>
    <true/>
    <key>WorkingDirectory</key>
    <string>/Applications/pizzapi</string>
    <key>StandardOutPath</key>
    <string>/var/log/pizzapi.log</string>
    <key>StandardErrorPath</key>
    <string>/var/log/pizzapi.err</string>
</dict>
</plist>
```

```bash
# Load the daemon
sudo launchctl load /Library/LaunchDaemons/com.pizzawave.pizzapi.plist
```

---

## Troubleshooting

### Service Won't Start

```bash
# Check service status
sudo systemctl status pizzapi

# View detailed logs
journalctl -u pizzapi -n 50 --no-pager

# Test manual start
/opt/pizzapi/pizzapi
```

### Missing Dependencies

```bash
# Debian/Ubuntu
sudo apt-get install -y libicu-dev libssl3 zlib1g \
  libfontconfig1 libx11-6 libx11-xcb1 libxcb1 \
  libxext6 libxfixes3 libxi6 libxrender1 libxtst6
```

### Port Already in Use

```bash
# Find what's using port 9123
sudo netstat -tlnp | grep 9123

# Kill the process if needed
sudo kill -9 <PID>
```

### Permission Issues

```bash
# Fix ownership
sudo chown -R $USER:$USER /opt/pizzapi

# Fix executable permission
sudo chmod +x /opt/pizzapi/pizzapi
```

### trunk-recorder Won't Connect

1. Verify IP address in trunk-recorder config matches pizzapi host
2. Check firewall rules:
   ```bash
   sudo ufw allow 9123/tcp
   ```
3. Test connectivity:
   ```bash
   # From trunk-recorder machine
   telnet <pizzapi-ip> 9123
   ```

### High Memory Usage

If memory usage grows over time:

1. Reduce `TraceLevelApp` to `Warning` or `Error`
2. Disable `WavFileLocation` if not needed
3. Consider running on Raspberry Pi 5 with 8GB RAM

---

## Updating

### From .deb Package

```bash
# Download new version
wget https://github.com/lilhoser/pizzawave/releases/latest/download/pizzapi_*_arm64.deb

# Install (preserves configuration)
sudo dpkg -i pizzapi_*.deb

# Restart service
sudo systemctl restart pizzapi
```

### From Source

```bash
# Pull latest changes
cd ~/pizzawave
git pull

# Rebuild
dotnet publish pizzapi/pizzapi.csproj \
  -c Release \
  -r linux-arm64 \
  --self-contained true \
  -o ./publish

# Copy to installation directory
sudo cp -r ./publish/* /opt/pizzapi/

# Restart
sudo systemctl restart pizzapi
```

---

## Backup and Restore

### Backup Configuration

```bash
# Backup settings
cp ~/.config/pizzawave/settings.json ~/pizzawave-settings-backup.json

# Or for system-wide install
sudo cp /etc/pizzapi/appsettings.json ~/pizzawave-settings-backup.json
```

### Restore Configuration

```bash
# Restore settings
cp ~/pizzawave-settings-backup.json ~/.config/pizzawave/settings.json

# Or for system-wide install
sudo cp ~/pizzawave-settings-backup.json /etc/pizzapi/appsettings.json
```

---

## See Also

* [pizzapi README](pizzapi.md) - Application-specific documentation
* [Main README](README.md) - Project overview
* [Building Guide](building.md) - Build from source instructions
* [Quick Start](quickstart.md) - 5-minute setup guide
