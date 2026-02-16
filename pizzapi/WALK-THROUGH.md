# Dual RTL-SDR Blog V4 + Trunk-Recorder + PizzaPi on Raspberry Pi 5

## 0. Pre-requisites and Considerations

### Software

Your RPI 5 will be the computing platform that will run Trunk-Recorder and the Callstream plugin, and serve the PizzaPi interface:

1. **Trunk-Recorder**: The core software that will handle the SDR input, decode the trunked system, and manage recordings.
1. **Callstream Plugin**: A plugin for Trunk-Recorder that streams decoded audio and metadata to clients (like PizzaPi) in real-time.
1. **PizzaPi**: A user-friendly interface that displays live and recorded trunked radio communications, allowing you to listen to calls and view talkgroup information.

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

For this example, we’ll use the [White Oak Mountain / Hamilton County P25 system in Tennessee](https://www.radioreference.com/db/sid/6355).

### Find center frequencies and control channels.

The center frequencies and control channels are essential for configuring Trunk Recorder to properly tune into the trunked system.

On the city/county details page you found above, you'll see a large table "Sites and Frequencies", with the frequencies listed in table cells to the far right. Simply highlight/select the frequencies for your system of interest, copy them, and paste them into [this website](https://alertapi.alertpage.net/sdr/).

This website will output the center frequencies and control channels for your system, which you will need to input into your config file later. The center frequencies are the ones highlighted in green, and the control channels are listed in the "Control Channels" section below the table.

### Create a talkgroups file

A talkgroup is simply a virtual channel within the trunked system that groups related users together. For example, all police dispatch communications might be on one talkgroup, while fire dispatch is on another. By creating a talkgroups file and linking it in your config, you can have the PizzaPi UI display the talkgroup names and descriptions instead of just showing "Unknown Talkgroup 12345" for every call.

Talkgroups are listed on the RadioReference page for your system. You can copy and paste them into a CSV file with the following format:
```
Id,Mode,AlphaTag,Description,Tag,Category
1,D,FDISPATCH,Fire Dispatch,Fire,Dispatch
2,D,FDISPATCH2,Fire Dispatch 2,Fire,Dispatch
3,D,PDISPATCH,Police Dispatch,Police,Dispatch
```

And then update your config file to point to this talkgroups file:
```JSON

{
  "talkgroups": [
    {"Id": 1, "Mode": "D", "AlphaTag": "FDISPATCH", "Description": "Fire Dispatch", "Tag": "Fire", "Category": "Dispatch"},
    {"Id": 2, "Mode": "D", "AlphaTag": "FDISPATCH2", "Description": "Fire Dispatch 2", "Tag": "Fire", "Category": "Dispatch"},
    {"Id": 3, "Mode": "D", "AlphaTag": "PDISPATCH", "Description": "Police Dispatch", "Tag": "Police", "Category": "Dispatch"}
  ]
}
```

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
textblacklist dvb_usb_rtl28xxu
blacklist rtl2832
blacklist rtl2830
```

Save (Ctrl+O → Enter → Ctrl+X), then reboot:

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

## 4. Build Trunk-Recorder + callstream Plugin
```bash

sudo apt install -y gnuradio gnuradio-dev gr-osmosdr libboost-all-dev \
  libusb-1.0-0-dev libsndfile1-dev libcurl4-openssl-dev fdkaac sox libssl-dev
```

UHD fix if needed:
```sudo apt install libuhd-dev -y```

```
cd ~/tr5
git clone https://github.com/TrunkRecorder/trunk-recorder.git
```

Add callstream plugin

```
git clone https://github.com/lilhoser/callstream.git
mkdir -p trunk-recorder/plugins/callstream
cp -r callstream/* trunk-recorder/plugins/callstream/

nano trunk-recorder/CMakeLists.txt
→ Add this line in the plugins section:
cmakeadd_subdirectory(plugins/callstream)
```

Build

```bash

cd ~/tr5/trunk-build
cmake ../trunk-recorder
make -j4
sudo make install
sudo ldconfig
```

## 5. Calibrating PPM Error with GQRX (Important!)

RTL-SDR Blog V4 dongles can vary in accuracy and it's important to calibrate the PPM error for each dongle to ensure accurate frequency tuning and decoding. Here's how to do it using GQRX on Raspberry Pi OS.

### Step-by-step Calibration (GQRX 2.17.6 on Raspberry Pi OS)

1. Install GQRX:
   ```sudo apt install gqrx-sdr -y```
1. Calibrate one dongle at a time (unplug the other dongle completely).
1. Open GQRX and do this for dongle 0 first:
 * In the top Device box, type: `rtl=0`
 * Click the small gear icon (⚙) right next to it.
 * In the Configure I/O devices window set:
   * Input rate: `2048000`
   * Decimation: `None` (or 1)
   * Bandwidth: `0.0` (leave default)
   * LNB LO: `0.000000 MHz`
 * Click OK.
1. In the right-hand pane, switch to the Input controls tab.
1. Set these values:
  * Frequency (top of screen): `162550000` (NOAA WXK48 Chattanooga — very strong & accurate)
  * Mode: `WFM`
  * Filter width: `200000`
  * Gain: ~`45` (same as your config)
  * AGC: `OFF`
1. In the Input controls tab, slowly adjust Freq. correction (ppm) until the strong NOAA signal spike is perfectly centered on the 0 Hz line in the waterfall/spectrum.
 * Let it settle 10–20 seconds after each adjustment.
 * Audio should sound clearest when perfectly centered.
1. Write down the exact PPM value shown (e.g. 0.0, -2.3, +1.8, etc.).
1. Repeat steps 3–7 for dongle 1 using device string rtl=1.

If the sound is clear and the signal spike is perfectly centered at 0 Hz with 0.0 ppm error, then your dongle is very accurate and you can set "error": 0 in the config for that dongle.

If yours are not zero, use this formula to convert PPM to the error value (in Hz) for the config:
```
error = PPM × Center_Frequency_in_MHz
```

Examples:

```
-7.5 ppm on 855.309375 MHz → error: -6415
+4.2 ppm on 857.6 MHz → error: +3602
```

Round to nearest 10 Hz.

## 6. Final Config File (sdrhero_pizzapi.json)

```bash

cd ~/tr5/trunk-build
nano sdrhero_pizzapi.json
```

Full final config (with error: 0 on both dongles):

```JSON
{
    "ver": 2,
    "defaultMode": "digital",
    "logFile": true,
    "frequencyFormat": "mhz",
    "statusAsString": true,
    "broadcastSignals": true,
    "logLevel": "info",
    "audioStreaming": true,
    "captureDir": "/home/sdrhero/tr5/recordings",
    "systems": [
        {
            "shortName": "whiteoakmt-hamilton",
            "type": "p25",
            "talkgroupsFile": "/home/sdrhero/tr5/trunk-build/chatt_talkgroups.csv",
            "control_channels": [855212500,856237500,856762500,857237500],
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
            "multiSite": true,
            "squelch": -60
        }
    ],
    "sources": [
        {
            "center": 855309375,
            "rate": 2064000,
            "error": 0,
            "gain": 45,
            "digitalRecorders": 4,
            "analogRecorders": 0,
            "driver": "osmosdr",
            "device": "rtl=0,bias=1,buflen=65536",
            "agc": false
        },
        {
            "center": 857600000,
            "rate": 2064000,
            "error": 0,
            "gain": 45,
            "digitalRecorders": 3,
            "analogRecorders": 0,
            "driver": "osmosdr",
            "device": "rtl=1,bias=1,buflen=65536",
            "agc": false
        }
    ],
    "plugins": [
        {
            "name": "callstream",
            "library": "libcallstream.so",
            "clients": [{"address": "127.0.0.1", "port": 9123}],
            "port": 9123,
            "streams": [{"TGID": 0, "shortName": "whiteoakmt-hamilton"}]
        }
    ]
}
```

## 7. Systemd Service for Trunk-Recorder

```bash

sudo nano /etc/systemd/system/trunk-recorder.service
```

Paste:
```
ini[Unit]
Description=Trunk Recorder - White Oak Mt / Hamilton Co P25
After=network.target

[Service]
Type=simple
User=sdrhero
Group=plugdev
WorkingDirectory=/home/sdrhero/tr5/trunk-build
ExecStart=/home/sdrhero/tr5/trunk-build/trunk-recorder --config=sdrhero_pizzapi.json
Restart=always
RestartSec=3
Nice=-10
LimitNOFILE=65535
Environment="LD_LIBRARY_PATH=/usr/local/lib"

[Install]
WantedBy=multi-user.target
```

Enable:

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now trunk-recorder.service
```

## 8. Setup PizzaPi to view Calls

### Download and run the PizzaPi upgrade script
```
curl -sL https://raw.githubusercontent.com/lilhoser/pizzawave/main/scripts/pizzapi-upgrade.sh > pizzapi-upgrade.sh

chmod +x pizzapi-upgrade.sh

sudo ./pizzapi-upgrade.sh
```

### Autostart the PizzaPi UI on boot

```
mkdir -p ~/.config/autostart
cat > ~/.config/autostart/pizzapi.desktop << EOF
[Desktop Entry]
Name=PizzaPi
Exec=/opt/pizzapi/pizzapi
Type=Application
Terminal=false
X-GNOME-Autostart-enabled=true
EOF
```

## 9. Troubleshooting

### Setup Live Logs with tmux (Auto-starts on boot)

```
cat > ~/start-trunk-logs.sh << 'EOF'
#!/bin/bash
sleep 25
tmux kill-session -t trunklogs 2>/dev/null
tmux new-session -d -s trunklogs "journalctl -u trunk-recorder -f --output=short --no-hostname"
EOF
```

```
chmod +x ~/start-trunk-logs.sh
```

Add to crontab:

```
crontab -e
```

Add this line at the bottom:

```
@reboot /home/sdrhero/start-trunk-logs.sh >> /home/sdrhero/tmux-start.log 2>&1
```

To view live logs, attach to the session:
```tmux attach -t trunklogs```

detach: `Ctrl+B then D`

### Services

```sudo systemctl status trunk-recorder```

```
sudo systemctl restart trunk-recorder
tmux ls
```

### Remote debugging with Visual Studio

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

    1. Tools → Options → Cross Platform → Connection Manager
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
- Debug → Attach to Process
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