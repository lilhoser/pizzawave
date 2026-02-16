# Getting Started with SDRs

## Online Streaming

Before starting your own at-home SDR setup to record broadcasts, it's useful to explore the online platforms where hobbyists and enthusiasts upload broadcast recordings. Start with these:

* [LiveATC](https://www.liveatc.net/) - Air traffic control recordings
* [OpenMHz](https://www.openmhz.com) - Open source trunking recordings
* [Broadcastify](https://www.broadcastify.com/) - Public safety radio feeds

## Decide on Your Hardware and Software

### Host Device Options

What device will host your SDR devices? Consider these options:

| Device | Pros | Cons |
|--------|------|------|
| **Raspberry Pi 5** | Low power, compact, runs trunk-recorder + pizzawave | Limited CPU for heavy transcription |
| **Linux PC** | Full power, flexible | Higher power consumption |
| **Windows PC** | Easy setup with pizzaui | Windows-only UI |
| **Docker** | Isolated, reproducible | More complex setup |

**Recommended for beginners**: Start with a Raspberry Pi 5 (8GB) running Raspberry Pi OS. It can run both trunk-recorder and pizzawave (via the `.deb` package) on the same device.

### Pre-built Images

Consider pre-baked images for faster setup:

* [Broadcastify RPI image](https://www.broadcastify.com/rpi/) - Raspberry Pi image
* [trunk-recorder Docker images](https://github.com/robotastic/trunk-recorder/blob/rc/v5.0/Dockerfile) - Container deployment

### Decoding Software

You'll need software to decode radio signals into audio streams:

* [RTLSDR-Airband](https://github.com/charlie-foxtrot/RTLSDR-Airband) - Simple AM/NFM receiver
* [trunk-recorder](https://trunkrecorder.com/docs/intro) - Full-featured P25 trunking recorder (recommended for pizzawave)

## SDR Hardware

### Getting Started

For SDR hardware, the [RTL-SDR Store](https://www.rtl-sdr.com/buy-rtl-sdr-dvb-t-dongles/) is an excellent starting point. They sell beginner kits and recommend compatible accessories.

**Recommended starter kit**:
* RTL-SDR Blog V4 dongle
* Dipole antenna kit
* SMA connectors and adapters

### Multiple SDR Setup

If you want to monitor multiple frequency ranges simultaneously, you'll need multiple SDRs. Here's what to consider:

1. **USB Hub**: Use a powered USB 3.0 hub with good port spacing
2. **Antenna Splitter**: Split antenna signal to multiple SDRs
   * Passive splitters cut signal in half per output (-3dB)
   * Powered splitters maintain signal strength
3. **LNA (Low Noise Amplifier)**: Compensate for splitter loss
   * Install close to antenna for best results
   * Requires bias-tee power injection

**Signal loss calculation**:
- Splitter loss: ~3dB per split (8-way = ~9dB)
- Cable loss: ~6-7dB per 100 feet
- Total loss should be compensated with LNA gain

## Preparing an RTL-SDR Device

### Driver Notes

RTL-SDR Blog V4 dongles come with instructions to uninstall standard `librtlsdr` drivers. This is **no longer necessary** - most software now supports V4 natively. Follow the [trunk-recorder Dockerfile](https://github.com/robotastic/trunk-recorder/blob/rc/v5.0/Dockerfile) for recommended dependencies.

### Setting Serial Numbers

When using multiple SDRs, each needs a unique serial number:

```bash
# Flash serial number (run for each dongle)
rtl_eeprom -s 00000001  # First dongle
rtl_eeprom -s 00000002  # Second dongle
# etc.
```

Use 8-digit numbers with leading zeros for consistency.

### Bias-Tee for LNA Power

If you have an LNA with bias-tee power, enable it in your device string:

```json
{
  "device": "rtl=00000006,buflen=65536,bias=1"
}
```

The `bias=1` parameter activates the bias-tee. This must be specified each time since standard drivers don't persist the setting.

## Antenna Considerations

### Types

| Antenna Type | Best For | Notes |
|-------------|----------|-------|
| **Discone** | Wideband scanning | Excellent for trunking (covers 25-1300 MHz) |
| **Dipole** | Specific bands | Tuned for specific frequencies |
| **Yagi** | Directional | Good for weak signals, requires aiming |
| **Ground Plane** | VHF/UHF | Simple, effective |

### Placement

* **Height**: Higher is better (20+ feet recommended)
* **Location**: Away from electronics to reduce interference
* **Cable**: Use quality coax (RG-6 or better for long runs)

### Example Setup

See [My trunk-recorder/SDR setup](README.md#my-trunk-recordersdr-setup) in the main README for a detailed parts list and installation photos.

## Next Steps

Once you have your SDR hardware ready:

1. **Install trunk-recorder** - Follow the [trunk-recorder setup guide](https://trunkrecorder.com/docs/CONFIGURE)
2. **Configure callstream plugin** - Enable audio streaming to pizzawave
3. **Install pizzawave** - See [Quick Start Guide](quickstart.md)
4. **Configure talkgroups** - Import from RadioReference database
5. **Start monitoring** - Receive and transcribe calls in real-time

## Resources

* [trunk-recorder documentation](https://trunkrecorder.com/docs/)
* [RTL-SDR.com tutorials](https://www.rtl-sdr.com/category/tutorials/)
* [RadioReference forums](https://forums.radioreference.com/)
* [The Art of Electronics](https://www.amazon.com/Art-Electronics-Paul-Horowitz/dp/0521809266) - For deeper electronics knowledge
