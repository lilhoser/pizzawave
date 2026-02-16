# Configuration Examples Explained

This guide walks through the sample trunk-recorder configuration files and explains each important setting for pizzawave integration.

## Sample Configurations

Two sample configurations are provided in the `docs/` folder:

* [sample-config-callstream.json](sample-config-callstream.json) - Basic callstream setup
* [sample-config-openmhz.json](sample-config-openmhz.json) - callstream + OpenMHz upload

---

## Configuration Walkthrough

### Root-Level Settings

```json
{
  "ver": 2,
  "defaultMode": "digital",
  "logFile": true,
  "frequencyFormat": "mhz",
  "callTimeout": 3,
  "controlWarnRate": 10,
  "statusAsString": true,
  "broadcastSignals": true,
  "logLevel": "info",
  "audioStreaming": true
}
```

| Setting | Value | Description |
|---------|-------|-------------|
| `ver` | `2` | Configuration format version |
| `defaultMode` | `"digital"` | Default recorder mode (`digital`, `analog`, `mixed`) |
| `logFile` | `true` | Enable logging to file |
| `frequencyFormat` | `"mhz"` | Display frequencies in MHz |
| `callTimeout` | `3` | Seconds before call considered complete |
| `controlWarnRate` | `10` | Control channel warning threshold |
| `statusAsString` | `true` | Human-readable status messages |
| `broadcastSignals` | `true` | Broadcast control channel signals |
| `logLevel` | `"info"` | Logging verbosity (`debug`, `info`, `warn`, `error`) |
| `audioStreaming` | `true` | **Required for pizzawave** - enables audio streaming |

---

### Systems Configuration

```json
"systems": [
  {
    "shortName": "whiteoakmt",
    "type": "p25",
    "talkgroupsFile": "chatt_talkgroups.csv",
    "control_channels": [
      855212500,
      856237500,
      856762500,
      857237500
    ],
    "modulation": "qpsk",
    "compressWav": false,
    "audioArchive": false,
    "transmissionArchive": false,
    "callLog": true,
    "analogLevels": 8,
    "maxDev": 4000,
    "digitalLevels": 4,
    "recordUnknown": false,
    "recordUUVCalls": true,
    "hideEncrypted": true,
    "hideUnknownTalkgroups": false,
    "minDuration": 5,
    "minTransmissionDuration": 0,
    "talkgroupDisplayFormat": "id_tag"
  }
]
```

#### Key Settings for pizzawave

| Setting | Recommended | Description |
|---------|-------------|-------------|
| `shortName` | `"whiteoakmt"` | Unique identifier for your system. Must match in callstream plugin config. |
| `type` | `"p25"` | System type (`p25`, `dmr`, `nxdn`, `smartnet`, etc.) |
| `talkgroupsFile` | `"chatt_talkgroups.csv"` | CSV file with talkgroup definitions |
| `control_channels` | `[...]` | List of control channel frequencies in Hz |
| `modulation` | `"qpsk"` | P25 modulation type |

#### Archive Settings

| Setting | Value | Description |
|---------|-------|-------------|
| `compressWav` | `false` | Set `true` to save space (requires more CPU) |
| `audioArchive` | `false` | Set `false` when using pizzawave (it handles transcription) |
| `transmissionArchive` | `false` | Set `false` when using pizzawave |
| `callLog` | `true` | Keep call logs for reference |

#### Recording Filters

| Setting | Value | Description |
|---------|-------|-------------|
| `recordUnknown` | `false` | Don't record unknown talkgroups |
| `recordUUVCalls` | `true` | Record unit-to-unit calls |
| `hideEncrypted` | `true` | Skip encrypted transmissions |
| `hideUnknownTalkgroups` | `false` | Show unknown talkgroups in logs |
| `minDuration` | `5` | Minimum call length in seconds |
| `minTransmissionDuration` | `0` | Minimum transmission length |

---

### Sources Configuration

```json
"sources": [
  {
    "center": 855309375,
    "rate": 2048000,
    "error": 0,
    "gain": 40,
    "digitalRecorders": 4,
    "analogRecorders": 0,
    "driver": "osmosdr",
    "device": "rtl=0,buflen=65536",
    "ppm": -2.0,
    "agc": false
  },
  {
    "center": 857600000,
    "rate": 2048000,
    "error": 0,
    "gain": 40,
    "digitalRecorders": 7,
    "analogRecorders": 0,
    "driver": "osmosdr",
    "device": "rtl=1,buflen=65536",
    "ppm": -2.0,
    "agc": false
  }
]
```

#### SDR Settings

| Setting | Example | Description |
|---------|---------|-------------|
| `center` | `855309375` | Center frequency in Hz (855.309375 MHz) |
| `rate` | `2048000` | Sample rate (2.048 MS/s for RTL-SDR) |
| `error` | `0` | Frequency correction (use `rtl_test` to find) |
| `gain` | `40` | RF gain in dB (adjust for signal strength) |
| `driver` | `"osmosdr"` | SDR driver (usually `osmosdr` for RTL-SDR) |
| `device` | `"rtl=0,buflen=65536"` | Device identifier |
| `ppm` | `-2.0` | Parts per million frequency correction |
| `agc` | `false` | Automatic gain control (usually `false`) |

#### Recorder Allocation

| Setting | Value | Description |
|---------|-------|-------------|
| `digitalRecorders` | `4` | Number of digital voice recorders per SDR |
| `analogRecorders` | `0` | Number of analog recorders (0 for P25) |

**Note**: The number of `digitalRecorders` determines how many simultaneous calls this SDR can record. More recorders = more CPU usage.

---

### callstream Plugin Configuration

```json
"plugins": [
  {
    "name": "callstream",
    "library": "libcallstream.so",
    "address": "192.168.1.122",
    "port": 9123,
    "streams": [
      {
        "TGID": 0,
        "shortName": "whiteoakmt"
      }
    ]
  }
]
```

#### Critical Settings for pizzawave

| Setting | Value | Description |
|---------|-------|-------------|
| `name` | `"callstream"` | Plugin name (must be exact) |
| `library` | `"libcallstream.so"` | Plugin library file |
| `address` | `"192.168.1.122"` | **IP address of pizzawave server** |
| `port` | `9123` | **Port pizzawave is listening on** (default: 9123) |
| `streams` | `[...]` | Array of stream configurations |

#### Stream Configuration

| Setting | Value | Description |
|---------|-------|-------------|
| `TGID` | `0` | Talkgroup ID (0 = all talkgroups) |
| `shortName` | `"whiteoakmt"` | Must match system `shortName` above |

---

### OpenMHz Configuration (Optional)

The second sample config adds OpenMHz upload:

```json
{
  "uploadServer": "https://api.openmhz.com",
  "systems": [
    {
      "shortName": "whiteoakmt",
      "apiKey": "<enter your API key here>",
      ...
    }
  ]
}
```

| Setting | Description |
|---------|-------------|
| `uploadServer` | OpenMHz API endpoint |
| `apiKey` | Your OpenMHz API key (get from openmhz.com) |

**Note**: When using both callstream and OpenMHz:
- callstream sends raw audio to pizzawave for transcription
- OpenMHz receives uploaded recordings for online playback
- Both can run simultaneously

---

## pizzawave Configuration

While trunk-recorder handles recording, pizzawave handles transcription and alerts. Here's how to configure pizzawave to work with your trunk-recorder setup.

### Basic pizzawave Settings

```json
{
  "TraceLevelApp": "Information",
  "listenPort": 9123,
  "AutostartListener": true,
  "WavFileLocation": "/home/user/pizzawave/calls",
  "talkgroups": [
    {
      "Id": 1,
      "Mode": "D",
      "AlphaTag": "FDISPATCH",
      "Description": "Fire Dispatch",
      "Tag": "Fire",
      "Category": "Dispatch"
    }
  ],
  "Alerts": [
    {
      "Id": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
      "Name": "Fire Alert",
      "Keywords": "fire,structure fire",
      "Email": "user@gmail.com",
      "Frequency": 0,
      "Talkgroups": [1],
      "CaptureWAV": true,
      "Enabled": true
    }
  ]
}
```

### Matching trunk-recorder and pizzawave

| trunk-recorder | pizzawave | Must Match? |
|----------------|-----------|-------------|
| `plugins[].address` | (pizzawave host IP) | Yes |
| `plugins[].port` | `listenPort` | Yes |
| `systems[].shortName` | (used in logs) | For reference |
| `talkgroupsFile` | `talkgroups` array | Same talkgroups |

---

## Common Configuration Issues

### Issue: pizzawave Not Receiving Audio

**Symptoms**: pizzawave shows "Listening on port 9123" but no calls arrive

**Solutions**:
1. Verify `address` in trunk-recorder config is correct IP of pizzawave host
2. Check firewall allows port 9123:
   ```bash
   sudo ufw allow 9123/tcp
   ```
3. Test connectivity:
   ```bash
   telnet <pizzawave-ip> 9123
   ```

### Issue: Wrong Talkgroup Labels

**Symptoms**: Calls show wrong talkgroup names

**Solutions**:
1. Ensure talkgroup IDs match between trunk-recorder CSV and pizzawave settings
2. Verify `shortName` matches in callstream plugin config

### Issue: High CPU Usage

**Solutions**:
1. Reduce `digitalRecorders` count if not needed
2. Set `compressWav: true` to reduce I/O
3. Lower `TraceLevelApp` to `Warning` or `Error`

---

## Complete Example: Two-SDR Setup

Here's a complete working configuration for a two-SDR setup monitoring a P25 system:

```json
{
  "ver": 2,
  "defaultMode": "digital",
  "logFile": true,
  "frequencyFormat": "mhz",
  "callTimeout": 3,
  "controlWarnRate": 10,
  "statusAsString": true,
  "broadcastSignals": true,
  "logLevel": "info",
  "audioStreaming": true,
  "systems": [
    {
      "shortName": "county_p25",
      "type": "p25",
      "talkgroupsFile": "talkgroups.csv",
      "control_channels": [855212500, 856237500],
      "modulation": "qpsk",
      "compressWav": false,
      "audioArchive": false,
      "transmissionArchive": false,
      "callLog": true,
      "analogLevels": 8,
      "maxDev": 4000,
      "digitalLevels": 4,
      "recordUnknown": false,
      "recordUUVCalls": true,
      "hideEncrypted": true,
      "hideUnknownTalkgroups": false,
      "minDuration": 5,
      "minTransmissionDuration": 0,
      "talkgroupDisplayFormat": "id_tag"
    }
  ],
  "sources": [
    {
      "center": 855309375,
      "rate": 2048000,
      "error": 0,
      "gain": 40,
      "digitalRecorders": 4,
      "analogRecorders": 0,
      "driver": "osmosdr",
      "device": "rtl=00000001,buflen=65536",
      "ppm": -2.0,
      "agc": false
    },
    {
      "center": 857600000,
      "rate": 2048000,
      "error": 0,
      "gain": 40,
      "digitalRecorders": 4,
      "analogRecorders": 0,
      "driver": "osmosdr",
      "device": "rtl=00000002,buflen=65536",
      "ppm": -1.5,
      "agc": false
    }
  ],
  "plugins": [
    {
      "name": "callstream",
      "library": "libcallstream.so",
      "address": "192.168.1.100",
      "port": 9123,
      "streams": [
        {
          "TGID": 0,
          "shortName": "county_p25"
        }
      ]
    }
  ]
}
```

---

## See Also

* [Quick Start Guide](quickstart.md) - Get started in 5 minutes
* [Deployment Guide](deployment.md) - Production deployment
* [Main README](README.md) - Project overview
* [sample-config-callstream.json](sample-config-callstream.json) - Basic config file
* [sample-config-openmhz.json](sample-config-openmhz.json) - OpenMHz config file
