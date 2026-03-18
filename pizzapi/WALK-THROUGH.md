# Dual RTL-SDR Blog V4 + Trunk-Recorder + PizzaPi on Raspberry Pi 5

## Table of Contents

- [0. Pre-requisites and Considerations](#0-pre-requisites-and-considerations)
- [1. System Preparation](#1-system-preparation)
- [2. Install RTL-SDR Blog V4 Drivers](#2-install-rtl-sdr-blog-v4-drivers)
- [3. Set Unique Serial Numbers on Both V4s](#3-set-unique-serial-numbers-on-both-v4s)
- [4. Calibrating PPM Error with GQRX](#4-calibrating-ppm-error-with-gqrx-important)
- [5. Create a Config File](#5-create-a-config-file-sdrhero_pizzapijson)
- [6. Build Trunk-Recorder + callstream Plugin](#6-build-trunk-recorder--callstream-plugin)
- [7. Setup PizzaPi to view Calls](#7-setup-pizzapi-to-view-calls)
- [8. Setup LM Studio for AI Insights](#8-setup-lm-studio-for-ai-insights)
- [9. Troubleshooting](#8-troubleshooting)

## 0. Pre-requisites and Considerations

### Software

Your RPI 5 will run the following software:

1. **Trunk-Recorder**: The core software that will handle the SDR input, decode the trunked system, and manage recordings.
1. **Callstream Plugin**: A plugin for Trunk-Recorder that streams decoded audio and metadata to clients (like PizzaPi) in real-time.
1. **PizzaPi**: A user-friendly interface that displays live and recorded trunked radio communications, allowing you to listen to calls and view talkgroup information.
1. **LMStudio** (optional): Used by Pizzapi to generate AI Insights summaries of call data throughout the day (requires LM Studio's LM Link feature to access an LLM on a remote machine).

### Hardware
1. **Compute**: While other variations might work, I chose to be lazy and selected [this CanaKit Raspberry Pi 5 8GB bundle](https://www.canakit.com/canakit-raspberry-pi-5-desktop-pc-with-ssd.html) (`PI5-8GB-PC512-C4-BLK`) with a pre-installed 512GB microSD card and a nice case with cooling.
1. **SDR**: Software-defined radio hardware that will receive the radio signals from the trunked system. The V4 model is chosen for its native bias tee support, which is essential for powering certain types of antennas. Other SDRs might work, but for my project, you'll need one or more [RTL-SDR Blog V4 dongles](https://www.rtl-sdr.com/buy-rtl-sdr-dvb-t-dongles/) (the only ones with native bias tee support). You'll need to first read the `Pick a system to monitor` section below to know how many dongles you will need to cover the frequencies of your target system. For my example, I needed 2 dongles to cover the 4 control channels of the White Oak Mountain / Hamilton County P25 system.
1. **Powered USB hub**: High-quality power is extremely important to ensure stable power delivery to multiple dongles. I've had success with the [Atolla Powered USB Hub](https://www.amazon.com/dp/B083XTKV8V) (4 ports, 5V/2A per port). This allows me to power both RTL-SDR Blog V4 dongles and have a couple of spare ports for future expansion. There is also a 10-port version of this hub.
1. **Antenna**: A good wideband antenna is crucial for receiving clear signals. In my primary SDR rig, I use this [Discone Antenna](https://www.amazon.com/dp/B00QVPGKHU) mounted on a mast on top of my building. For this project, I am trying out [this smaller portable radio antenna](https://www.amazon.com/dp/B08W8TWTL4). If you buy the RTL-SDR Blog dongle kit, it comes with a small telescopic antenna that can work for testing and very strong local signals, but I recommend upgrading to a better antenna for optimal performance. It goes without saying, but I'll say it: you need access to a window or open air to receive signals. If you try to use this setup in a basement or somewhere with no signal access, you will be very sad.
1. **Cables and adapters**: Depending on your setup, you may need additional SMA cables, USB extension cables, or adapters to connect your SDRs to the antenna and the Raspberry Pi. Make sure to get high-quality cables to minimize signal loss. Always ensure you use [DirecTV splitters](https://www.amazon.com/dp/B01G9AZ78E) for best performance when splitting antenna signals to multiple SDRs. You will need [SMA to F jumpers](https://www.amazon.com/dp/B09GVSHQJX).

Optional display: I chose to be fancy and install a RPI touch display on top of the PI 5 system and house it in a really fancy mount.
* [7" Raspberry Pi Touchscreen Display](https://www.adafruit.com/product/6079)
* [Articulated Pi Display V2 Mount](https://learn.adafruit.com/pi-wall-mount)
* [M2.5 screw variety pack](https://www.amazon.com/dp/B075QKZ8PY) - mostly for the longer 20mm screws needed to mount the display to the mount.

A few thoughts on the printing and assembly process for this display mount:
* The Adafruit mount is really nice and sturdy, but the instructions miss several steps and important requirements. For one, the included screws are not long enough to go through the display, mount, and into the Pi 5 case. I had to buy a variety pack of M2.5 screws and ended up using 20mm length screws to get everything securely fastened.
* The instructions do not mention installing the articulation hinges.
* The printed hinge pins have caps on either end, one must be cut off
* The tolerance between the pin and the hinges is very loose, causing the display to wobble a lot when touched. I ended up wrapping the pins in electrical tape to create a snug fit and eliminate the wobble. You might need to reprint the pins a few times to get a better fit.
* The weight of the USB cable and other wiring will fight you on a level/plum display. You will need to play around with the articulation hinges and the placement of the cables to find a good balance that allows the display to stay level when touched.
* I chose to use the tabletop mount as opposed to the wall mount
* Adafruit's [`3mf` model file](https://adafruit2.autodesk360.com/g/shares/SH30dd5QT870c25f12fc83693632449530a0) contains all of the parts assembled into one object. You will need to use slicer software to decompose the monolithic object into its constituent parts for printing. I used PrusaSlicer and found it pretty easy to do.

### Pick a system to monitor
Use [RadioReference](https://www.radioreference.com/) to find frequencies, talkgroups, and system details for your area. Start with browsing the map to drill down to your state, county and city. Then find the trunked system you want to monitor and click on it to view details.

For this example, we'll use the [White Oak Mountain / Hamilton County P25 system in Tennessee](https://www.radioreference.com/db/sid/6355).

### Find center frequencies and control channels.

The center frequencies and control channels are essential for configuring Trunk Recorder to properly tune into the trunked system.

On the city/county details page you found above, you'll see a large table "Sites and Frequencies", with the frequencies listed in table cells to the far right. Simply highlight/select the frequencies for your system of interest, copy them, and paste them into [this website](https://alertapi.alertpage.net/sdr/).

This website will output the center frequencies and control channels for your system, which you will need to input into your config file later. The center frequencies are the ones highlighted in green, and the control channels are listed in the "Control Channels" section below the table.

### Create a talkgroups file

A talkgroup is simply a virtual channel within the trunked system that groups related users together. For example, all police dispatch communications might be on one talkgroup, while fire dispatch is on another. By creating a talkgroups file and linking it in your config, you can have the PizzaPi UI display the talkgroup names and descriptions instead of just showing "Unknown Talkgroup 12345" for every call.

Talkgroups are listed on the RadioReference page for your system. To generate the CSV:
1. Visit the RadioReference talkgroups section for the system you are monitoring (example: https://www.radioreference.com/db/sid/4879).
1. Copy the HTML table for the talkgroups you want.
1. Convert that HTML table to CSV using a tool like convertcsv.com or an AI interface.
1. Remove any invalid/extra columns in Excel, Google Sheets, or a similar tool.

Example:
```
Decimal,Mode,Alpha Tag,Description,Tag,Category
1,D,FDISPATCH,Fire Dispatch,Fire,Dispatch
2,D,FDISPATCH2,Fire Dispatch 2,Fire,Dispatch
3,D,PDISPATCH,Police Dispatch,Police,Dispatch
```

The talkgroup file will be used in both TR setup and PizzaPi setup, so save it somewhere persistent on your RPI (e.g. `/home/<USER>/tr5/talkgroups.csv`).


## 1. System Preparation

```bash
sudo apt update && sudo apt upgrade -y
sudo apt install git cmake build-essential pkg-config libusb-1.0-0-dev rtl-sdr -y
```
Blacklist DVB-T drivers:
```bash
sudo nano /etc/modprobe.d/blacklist-rtl.conf
```

Paste:
```
blacklist dvb_usb_rtl28xxu
blacklist rtl2832
blacklist rtl2830
```

Save (Ctrl+O -> Enter -> Ctrl+X), then reboot:

```bash
sudo reboot
```

## 2. Install RTL-SDR Blog V4 Drivers
```bash
cd ~
git clone https://github.com/rtlsdrblog/rtl-sdr-blog.git
cd rtl-sdr-blog
mkdir build && cd build
cmake .. -DINSTALL_UDEV_RULES=ON
make -j4
sudo make install
sudo cp ../rtl-sdr.rules /etc/udev/rules.d/
sudo ldconfig
sudo reboot
```

Test:

```bash
rtl_test -t
```

## 3. Set Unique Serial Numbers on Both V4s

(One dongle at a time)
```bash
sudo rtl_eeprom -d 0 -s 00000001   # First dongle
sudo rtl_eeprom -d 0 -s 00000002   # Second dongle (after swapping)
```

## 4. Calibrating PPM Error with GQRX (Important!)

RTL-SDR Blog V4 dongles can vary in accuracy and it's important to calibrate the PPM error for each dongle to ensure accurate frequency tuning and decoding. Here's how to do it using GQRX on Raspberry Pi OS.

### Step-by-step Calibration (GQRX 2.17.6 on Raspberry Pi OS)

1. Install GQRX:
   ```sudo apt install gqrx-sdr -y```
1. Calibrate one dongle at a time (unplug the other dongle completely).
1. Open GQRX and do this for dongle 0 first:
 * In the top Device box, type: `rtl=0`
 * Click the small gear icon ((gear)) right next to it.
 * In the Configure I/O devices window set:
   * Input rate: `2048000`
   * Decimation: `None` (or 1)
   * Bandwidth: `0.0` (leave default)
   * LNB LO: `0.000000 MHz`
 * Click OK.
1. In the right-hand pane, switch to the Input controls tab.
1. Set these values:
  * Frequency (top of screen): `162550000` (NOAA WXK48 Chattanooga - very strong & accurate)
  * Mode: `WFM`
  * Filter width: `200000`
  * Gain: ~`45` (same as your config)
  * AGC: `OFF`
1. In the Input controls tab, slowly adjust Freq. correction (ppm) until the strong NOAA signal spike is perfectly centered on the 0 Hz line in the waterfall/spectrum.
 * Let it settle 10-20 seconds after each adjustment.
 * Audio should sound clearest when perfectly centered.
1. Write down the exact PPM value shown (e.g. 0.0, -2.3, +1.8, etc.).
1. Repeat steps 3-7 for dongle 1 using device string rtl=1.

If the sound is clear and the signal spike is perfectly centered at 0 Hz with 0.0 ppm error, then your dongle is very accurate and you can set "error": 0 in the config for that dongle.

If yours are not zero, use this formula to convert PPM to the error value (in Hz) for the config:
```
error = PPM x Center_Frequency_in_MHz
```

Examples:

```
-7.5 ppm on 855.309375 MHz -> error: -6415
+4.2 ppm on 857.6 MHz -> error: +3602
```

Round to nearest 10 Hz.

## 5. Create a Config File

Example:

```JSON
{
    "ver": 2,
    "defaultMode": "digital",
    "logFile": true,
	"captureDir": "/var/lib/trunk-recorder/recordings",
    "logDir": "/var/log/trunk-recorder",
    "tempDir": "/var/lib/trunk-recorder/tmp",
    "frequencyFormat": "mhz",
    "statusAsString": true,
    "broadcastSignals": true,
    "logLevel": "info",
    "audioStreaming": true,
    "systems": [
        {
            "shortName": "whiteoakmt-hamilton",
            "type": "p25",
            "talkgroupsFile": "/etc/trunk-recorder/talkgroups.csv",
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
            "callLog": false,
            "analogLevels": 8,
            "maxDev": 4000,
            "digitalLevels": 2,
            "recordUnknown": false,
            "recordUUVCalls": true,
            "hideEncrypted": true,
            "hideUnknownTalkgroups": false,
            "minDuration": 5,
            "minTransmissionDuration": 0,
            "talkgroupDisplayFormat": "id_tag",
            "multiSite": true
        }
    ],
    "sources": [
        {
            "center":855309375,
            "rate": 2048000,
            "error": 0,
            "gain": 45,
            "digitalRecorders": 5,
            "analogRecorders": 0,
            "driver": "osmosdr",
            "device": "rtl=0,bias=1,buflen=65536",
            "agc": false
        },
        {
            "center":857600000,
            "rate": 2048000,
            "error": 0,
            "gain": 45,
            "digitalRecorders": 4,
            "analogRecorders": 0,
            "driver": "osmosdr",
            "device": "rtl=1,bias=1,buflen=65536",
            "agc": false
        }
    ],
    "plugins": [
        {
            "name":"callstream",
            "library":"libcallstream.so",
            "clients":[
                {
                    "address":"127.0.0.1",
                    "port":9123
                }
            ],
            "port":9123,
            "streams":[
                {
                    "TGID":0,
                    "shortName":"whiteoakmt-hamilton"
                }
            ],
            "audio_filtering": {
                "enabled": false,
                "spike_clipping": { "enabled": true, "threshold_percent": 58, "clip_factor": 0.10 },
                "smoothing": { "enabled": false, "window_size": 3 },
                "high_pass_filter": { "enabled": true, "cutoff_hz": 45 }
            }
        }
    ]
}
```

## 6. Build Trunk-Recorder + callstream Plugin

Please use the [official build script](scripts/setup_trunk_recorder.sh):

```bash

curl -sL https://raw.githubusercontent.com/lilhoser/pizzawave/main/scripts/setup_trunk_recorder.sh > setup_trunk_recorder.sh

chmod +x setup_trunk_recorder.sh

sudo ./setup_trunk_recorder.sh --config <CONFIG> --talkgroups-file <TALKGROUPS_FILE>
```

This script will setup all pre-requisites, build trunk-recorder and the callstream plugin, and install the final binaries, config file and talkgroups file as a persistent service. It will also autostart a tmux session on boot to show live trunk-recorder logs in a terminal window. The script can also be used to check installation status and clean a prior installation (if an update is needed).

## 7. Setup PizzaPi to view Calls

### Download and run the PizzaPi upgrade script
```
curl -sL https://raw.githubusercontent.com/lilhoser/pizzawave/main/scripts/pizzapi-upgrade.sh > pizzapi-upgrade.sh

chmod +x pizzapi-upgrade.sh

sudo ./pizzapi-upgrade.sh
```

This will install PizzaPi to `/opt/pizzapi` with a default settings file and autostart it on login.

You can start the application and manually update the settings file or use the one below.

```
{
  "ConfigVersion": 2,
  "TraceLevelApp": 15,
  "Alerts": [],
  "AutostartListener": true,
  "emailUser": "",
  "emailPassword": "",
  "AutoplayAlerts": true,
  "SnoozeDurationMinutes": 15,
  "SortMode": 0,
  "GroupMode": 1,
  "FontSize": 24.0,
  "AutoCleanupCalls": false,
  "MaxCallsToKeep": 100,
  "listenPort": 9123,
  "analogChannels": 1,
  "analogBitDepth": 16,
  "analogSamplingRate": 8000,
  "transcriptionEngine": "whisper",
  "transcriptionModelPreset": "whisper-base",
  "emailProvider": "gmail",
  "lmLinkEnabled": true,
  "lmLinkBaseUrl": "http://localhost:1234",
  "lmLinkApiKey": "",
  "lmLinkModel": "qwen/qwen3.5-35b-a3b",
  "lmLinkTimeoutMs": 60000,
  "lmLinkMaxRetries": 2,
  "Talkgroups": []
}
```

If you don't plan on using LM Studio, you can remove those options.

You can manually populate Talkgroups array in the settings file, or use the settings pane in PizzaPi to import them from the same CSV file you used for trunk-recorder.

Note: the model you choose for LM Link must be OpenAI-compatible and support structured output.

## 8. Setup LM Studio for AI Insights (optional)

**Runs entirely in a Virtual Private Network(VPN) using free models that run locally on a machine you control. Everything is end-to-end encrypted via Tailscale. Nothing ever goes to "the cloud".**

### Overview

If you want to use the AI Insights feature in PizzaPi, you will need to setup [LM Studio](https://lmstudio.ai/) and enable LM link on a beefy GPU machine running an LLM. Then you must do the same on the Raspberry Pi running PizzaPi and point it to the LM Studio instance. LM Studio internally uses [tailscale](https://login.tailscale.com/admin/machines) to create a secure network between the RPi and the machine running the LLM, so you will need to create a tailscale account and configure this in LM Studio. The LM Studio docs/instructions are easy and very clear, so I won't repeat this process here. Generally it works like this:
1. LM Studio on the remote machine loads and hosts an LLM of your choice (I use Qwen 3.5 35B from the Qwen series by Alibaba, which is open and free to use on LM Studio). This machine joins the VPN network using Tailscale and LM Studio hosts a Llama server on localhost to respond to requests from the RPI (PizzaPi).
1. LM Studio on the RPI is also configured via Tailscale to join the same private network, so it can see the remote machine running the LLM. LM Studio on the RPI runs a chat completion endpoint locally on localhost, that PizzaPi can send requests to, and it forwards those requests to the LLM server running on the remote machine. 

In this manner, a low-end RPI can use a powerful LLM running on a remote machine to generate AI Insights summaries of call data, without needing to run the LLM locally (similar to just using ChatGPT or another cloud-based LLM, but with better performance and no recurring costs since LM Studio is free and the Qwen models are free).

### Download LM Studio

On the LLM system: [Download Winx64](https://lmstudio.ai/download/latest/win32/x64) or whatever OS

On the RPI: [Download the latest ARM64 AppImage](https://lmstudio.ai/download/latest/linux/arm64?format=AppImage) or headless mode

Note: On either system, you can also install the [headless mode version](https://lmstudio.ai/docs/developer/core/headless) if you don't want the LM Studio UI and just want to run the server in the background. This is a good option for the RPI since it doesn't need to run the UI, and it can save some resources by just running the server.

### Setup LLM server on remote machine

Install LM Studio on the remote machine and configure it to host your desired LLM (e.g. Qwen 3.5 35B). Make sure to enable LM Link in the settings and note the port number it uses (default is 1234). Join the Tailscale network and ensure this machine can communicate with the RPI via Tailscale.

### Setup RPI server

The RPI will run LM Studio in headless mode and just act as a relay to forward requests from PizzaPi to the LLM server running on the remote machine. Run the script `scripts\setup-lmstudio.sh` to setup LM Studio in headless mode (it follows the [official instructions](https://lmstudio.ai/docs/developer/core/headless)).

```bash
curl -sL https://raw.githubusercontent.com/lilhoser/pizzawave/main/scripts/setup-lmstudio.sh > setup-lmstudio.sh

chmod +x setup-lmstudio.sh

sudo ./setup-lmstudio.sh --skip-model-load --user <USER_NAME>
```

## 9. Troubleshooting

### Trunk Recorder
The `setup-trunk-recorder.sh` script will create a tmux session named `trunklogs` that automatically starts trunk-recorder and shows live logs in the terminal. This is very useful for troubleshooting and monitoring the system.

You can attach to the session at anytime via `tmux attach -t trunklogs`.  Detach with `Ctrl+B` then `d`

Check server status with `sudo systemctl status trunk-recorder`

### PizzaPi

#### Logs
Application logs are located at `/var/log/pizzapi.log`. You can view them with `tail -f /var/log/pizzapi.log`

#### Remote debugging with Visual Studio

**Prerequisites**

1. On your Windows PC:
 * Visual Studio 2022 with ".NET desktop development" and "C++ CMake tools" workloads
 * SSH client (included with Git for Windows or Windows 10/11)
2. On your Raspberry Pi:
 * .NET 9.0 Runtime (already installed if pizzapi runs)
 * SSH server (usually pre-installed on Raspbian)

**Step 1: Configure Visual Studio**

See [this link](https://learn.microsoft.com/en-us/visualstudio/debugger/remote-debugging-dotnet-core-linux-with-ssh?view=visualstudio) for more details.

Add SSH Connection:

    1. Tools -> Options -> Cross Platform -> Connection Manager
    2. Click Add
    3. Fill in:
    - Name: Raspberry Pi (or any name)
    - Hostname: 192.168.x.x (your RPi IP)
    - Port: 22
    - Username: pi (or your RPi username)
    - Authentication: Private Key (recommended) or Password

    4. Click Connect to test

Step 2: Deploy and Debug

1. Deploy manually via SCP/SFTP:

```
# From Windows PowerShell
scp -r artifacts\pizzapi\bin\Debug\net9.0\ pi@192.168.x.x:~/pizzapi
```

2. In Visual Studio:
- Debug -> Attach to Process
- Connection type: SSH
- Connection: Select your RPi
- Process: Find pizzapi in the list
- Click Attach

**Alternative: Use VS Code (Lighter Weight)**

1. Install VS Code with C# extension
1. Create `.vscode/launch.json`:
```
{
"version": "0.2.0",
"configurations": [ {
    "name": ".NET Core Launch (Remote)",
    "type": "coreclr",
    "request": "attach",
    "pipeTransport": {
        "pipeCwd": "${workspaceRoot}",
        "pipeProgram": "ssh",
        "pipeArgs": ["pi@192.168.x.x"],
        "debuggerPath": "~/vsdbg/vsdbg"
    },
    "processId": ""
    }]
}
```
3. Deploy and attach using the SSH terminal

