# Quick Start Guide

Get pizzawave up and running in 5 minutes. This guide covers the most common setup: running `pizzapi` or `pizzacmd` on Linux to receive audio from trunk-recorder.

## Prerequisites Checklist

Before starting, ensure you have:

- [ ] A trunk-recorder system with callstream plugin configured
- [ ] A Linux system (or WSL2) to run pizzawave
- [ ] .NET 9.0 runtime installed
- [ ] Network connectivity between trunk-recorder and pizzawave

## Step 1: Install .NET 9.0

### Ubuntu/Debian

```bash
wget https://packages.microsoft.com/config/ubuntu/24.04/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb
rm packages-microsoft-prod.deb
sudo apt-get update
sudo apt-get install -y dotnet-sdk-9.0
```

### WSL2 (Debian/Ubuntu)

Same as Ubuntu/Debian above.

### Verify Installation

```bash
dotnet --version
# Should show: 9.0.x
```

## Step 2: Get Pizzawave

### Option A: Download Release (Recommended)

```bash
# Download latest release
cd ~
wget https://github.com/lilhoser/pizzawave/releases/latest/download/pizzapi_linux-x64.zip

# Extract
unzip pizzapi_linux-x64.zip
cd pizzapi
```

### Option B: Build from Source

```bash
git clone https://github.com/lilhoser/pizzawave.git
cd pizzawave
dotnet publish pizzapi/pizzapi.csproj -c Release -r linux-x64 --self-contained true -o ~/pizzapi
cd ~/pizzapi
```

## Step 3: First Run

```bash
# Run pizzapi (will create default config)
./pizzapi
```

On first run, pizzawave creates a configuration file at:
- `~/.config/pizzawave/settings.json`

Press `Ctrl+C` to stop after verifying it starts.

## Step 4: Configure

Edit the configuration file:

```bash
nano ~/.config/pizzawave/settings.json
```

### Minimum Required Settings

```json
{
  "TraceLevelApp": "Information",
  "listenPort": 9123,
  "AutostartListener": true
}
```

### Optional: Add Talkgroups

Create a talkgroups CSV file:

```bash
nano ~/talkgroups.csv
```

```csv
Id,Mode,AlphaTag,Description,Tag,Category
1,D,FDISPATCH,Fire Dispatch,Fire,Dispatch
2,D,FDISPATCH2,Fire Dispatch 2,Fire,Dispatch
3,D,PDISPATCH,Police Dispatch,Police,Dispatch
```

Update settings to reference it:

```json
{
  "talkgroups": [
    {"Id": 1, "Mode": "D", "AlphaTag": "FDISPATCH", "Description": "Fire Dispatch", "Tag": "Fire", "Category": "Dispatch"},
    {"Id": 2, "Mode": "D", "AlphaTag": "FDISPATCH2", "Description": "Fire Dispatch 2", "Tag": "Fire", "Category": "Dispatch"},
    {"Id": 3, "Mode": "D", "AlphaTag": "PDISPATCH", "Description": "Police Dispatch", "Tag": "Police", "Category": "Dispatch"}
  ]
}
```

## Step 5: Configure trunk-recorder

On your trunk-recorder system, edit the configuration to add the callstream plugin:

```json
{
  "plugins": [
    {
      "name": "callstream",
      "library": "libcallstream.so",
      "address": "<pizzawave-ip-address>",
      "port": 9123,
      "streams": [
        {
          "TGID": 0,
          "shortName": "your_system_name"
        }
      ]
    }
  ]
}
```

Replace `<pizzawave-ip-address>` with the IP address of the system running pizzawave.

Restart trunk-recorder:

```bash
sudo systemctl restart trunk-recorder
```

## Step 6: Verify Connection

### On pizzawave system:

```bash
# Check if listening on port 9123
netstat -tlnp | grep 9123

# Should show: tcp 0.0.0.0:9123 LISTEN
```

### On trunk-recorder system:

```bash
# Test connectivity
telnet <pizzawave-ip> 9123

# Should connect successfully
```

## Step 7: Start Listening

```bash
# Run pizzapi
cd ~/pizzapi
./pizzapi
```

You should see output like:

```
StreamServer Verbose: Listening on port 9123
Waiting for connections from trunk-recorder...
```

When a call comes in:

```
Call received from talkgroup 1 (FDISPATCH)
Transcribing...
[Transcription appears here]
```

## What's Next?

### Set Up Alerts

Create alert rules to get notified for keywords:

```json
{
  "Alerts": [
    {
      "Id": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
      "Name": "Fire Alert",
      "Keywords": "fire,structure fire,working fire",
      "Email": "your-email@gmail.com",
      "Frequency": 0,
      "Talkgroups": [1, 2],
      "CaptureWAV": true,
      "Enabled": true
    }
  ]
}
```

### Run as a Service

Create systemd service:

```bash
sudo nano /etc/systemd/system/pizzapi.service
```

```ini
[Unit]
Description=PizzaWave API
After=network.target

[Service]
Type=exec
ExecStart=/home/username/pizzapi/pizzapi
WorkingDirectory=/home/username/pizzapi
Restart=always
User=username

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl daemon-reload
sudo systemctl enable pizzapi
sudo systemctl start pizzapi
```

### Explore the UI

If using `pizzapi` with a display:

```bash
./pizzapi
```

Navigate the UI:
- **File → Call Manager → Start** - Start live capture
- **Edit → Settings** - Configure settings
- **Edit → Alerts** - Manage alert rules
- **View → Export** - Export call data

## Troubleshooting

### No Calls Appearing

1. Verify trunk-recorder is sending data:
   ```bash
   # On trunk-recorder system
   tail -f /path/to/trunk-recorder/logs
   ```

2. Check firewall:
   ```bash
   sudo ufw allow 9123/tcp
   ```

3. Verify IP address in trunk-recorder config matches pizzawave host

### Transcription Not Working

1. Check Whisper model downloaded:
   ```bash
   ls -la ~/.local/share/pizzawave/model/
   ```

2. Increase logging:
   ```json
   {"TraceLevelApp": "Verbose"}
   ```

### High Memory Usage

1. Reduce logging level:
   ```json
   {"TraceLevelApp": "Warning"}
   ```

2. Disable WAV saving if enabled:
   ```json
   {"WavFileLocation": ""}
   ```

## Resources

* [Full Documentation](README.md) - Complete documentation
* [Deployment Guide](deployment.md) - Production deployment
* [Building Guide](building.md) - Build from source
* [Configuration Examples](config-examples-explained.md) - Settings reference
