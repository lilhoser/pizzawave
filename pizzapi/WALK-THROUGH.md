# Dual RTL-SDR Blog V4 + Trunk-Recorder + PizzaPi on Raspberry Pi 5

## 0. Research

### Pick a system to monitor.
Use [RadioReference](https://www.radioreference.com/) to find frequencies, talkgroups, and system details for your area. For this example, we’ll use the [White Oak Mountain / Hamilton County P25 system in Tennessee](https://www.radioreference.com/db/sid/6355).

### Find center frequencies and control channels.

Copy all channels and control channels from RadioReference table into this page:
https://alertapi.alertpage.net/sdr/

You'll use the green highlighted center frequencies and control channels in your config file later.

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