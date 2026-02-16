# pizzalib - Core Library

<img align="right" src="../docs/logo-med.png">

`pizzalib` is a standalone, cross-platform .NET library that provides the following capabilities to a containing application:

* Integration with [callstream plugin](https://github.com/lilhoser/callstream) of [trunk-recorder](https://github.com/robotastic/trunk-recorder) to receive audio data
* Running a simple server that receives and processes audio data in preparation for transcription
* Audio to text transcription using [OpenAI's Whisper AI model](https://openai.com/research/whisper) as exposed through [whisper.net toolchain](https://github.com/sandrohanea/whisper.net)
* Email alerting
* Management of talkgroup data and alerting rules

Please be sure to read the [`pizzawave` README page](../docs/README.md).

## Requirements

* [Requirements as specified in the `pizzawave` README](../docs/README.md)
* A supported operating system (Windows, Linux, macOS) running .NET 9.0 or later
* A .NET program that uses `pizzalib` as a reference (see `pizzaui`, `pizzapi`, and `pizzacmd` as examples)
* The requirements for transcription using AI are discussed in the "Using Whisper for Transcription" section.

## Using Whisper for Transcription

`pizzalib` uses OpenAI's Whisper model for audio transcription, which [supports](https://github.com/sandrohanea/whisper.net/tree/fc1282f9a92f03e854a66c65b39ef2c9dfdc23e4?tab=readme-ov-file#multiple-runtimes-support) the following compute backends, which will be automatically selected at runtime in this order:

1. `Whisper.net.Runtime.Cuda` - NVIDIA devices with CUDA drivers installed
2. `Whisper.net.Runtime.Vulkan` - Windows x64 with Vulkan installed
3. `Whisper.net.Runtime.CoreML` - Apple Silicon devices
4. `Whisper.net.Runtime.OpenVino` - Intel devices with OpenVINO
5. `Whisper.net.Runtime` - CPU inference (works everywhere)

If using NVIDIA GPU, be sure to install the [CUDA Toolkit](https://developer.nvidia.com/cuda-downloads).

### Switching Base Model

By default, `pizzalib` will attempt to download the base Whisper GGML model using [Whisper.net's downloader](https://github.com/sandrohanea/whisper.net/blob/main/Whisper.net/Ggml/WhisperGgmlDownloader.cs), which is ~144MB at the time of writing. This downloader pulls Whisper models from Hugging Face at [this URL](https://huggingface.co/sandrohanea/whisper.net/tree/main/classic).

To use a different model:
1. Download the model file to a folder
2. Provide the full path in your `whisperModelFile` setting

The "large" version of the model performs best but requires ~3.1GB of storage.

## Configuration

`pizzalib` configuration lives in `<user profile>/pizzawave/settings.json`. Locations by platform:

| Platform | Configuration Path |
|----------|-------------------|
| Windows | `C:\Users\<user>\AppData\Roaming\pizzawave\settings.json` |
| Linux | `~/.config/pizzawave/settings.json` |
| macOS | `~/.config/pizzawave/settings.json` |

This file can be manipulated in `pizzaui`, `pizzapi`, and `pizzacmd`. The UI applications include a graphical settings editor, but you can always create the file manually. If you run the UI or command line application without a settings file, the default one will be created.

### Supported Settings

| Setting | Default | Description |
|---------|---------|-------------|
| `TraceLevelApp` | `Error` | Controls verbosity of trace logging: `All`=-1, `Off`=0, `Critical`=1, `Error`=3, `Warning`=7, `Information`=15, `Verbose`=31 |
| `WavFileLocation` | (empty) | Path to save call audio as MP3 files. Leave empty for in-memory only transcription. |
| `Alerts` | (empty) | Array of alert rules (see below) |
| `AutostartListener` | `true` | Automatically start listener when application starts |
| `gmailUser` | (empty) | Gmail account for sending email alerts |
| `gmailPassword` | (empty) | Gmail app password (not your account password!) |
| `listenPort` | `9123` | Port for callstream plugin to connect |
| `analogChannels` | `1` | Number of analog channels in received audio |
| `analogBitDepth` | `16` | Bit depth of received audio |
| `analogSamplingRate` | `8000` | Sampling rate in Hz |
| `talkgroups` | (empty) | Array of talkgroup definitions |
| `whisperModelFile` | (empty) | Path to Whisper model file (uses default "base" if empty) |

### Alert Rules

Alert rules are defined as an array with these parameters:

| Parameter | Type | Description |
|-----------|------|-------------|
| `Id` | GUID | Uniquely identifies the rule |
| `Name` | string | Friendly name for the rule |
| `Email` | string | Comma-separated list of emails for alerts |
| `Keywords` | string | Comma-separated list of trigger keywords |
| `Frequency` | enum | Re-evaluation frequency: `RealTime`=0, `Hourly`=1, `Daily`=2 |
| `Talkgroups` | array | Talkgroup IDs to monitor |
| `CaptureWAV` | bool | Save WAV/MP3 when rule triggers |
| `Enabled` | bool | Enable or disable the rule |

### Talkgroups

A talkgroup is a concept specific to [P25 trunked radio systems](https://en.wikipedia.org/wiki/Trunked_radio_system) and refers to a logical grouping of users communicating on the trunked system. Browse the [RadioReference database](https://www.radioreference.com/db/browse/) to find systems in your area.

Talkgroup parameters:

| Parameter | Type | Description |
|-----------|------|-------------|
| `Id` | long | Decimal talkgroup ID |
| `Mode` | string | `D`=digital, `A`=analog, `M`=mixed, `T`=TDMA, `De`=partial encryption, `DE`=full encryption |
| `AlphaTag` | string | 16-character short display name |
| `Description` | string | Full description |
| `Tag` | string | Official service tag |
| `Category` | string | Additional category |

See the [trunk-recorder configuration guide](https://trunkrecorder.com/docs/CONFIGURE#talkgroupsfile) and [this guide](https://www.andrewmohawk.com/2020/06/12/trunked-radio-a-guide/) for more information.

## Alerts

Alerts are rules that tell `pizzalib` how to process audio data of interest.

### Getting Email Alerts

1. [Create an app password](https://support.google.com/accounts/answer/185833) for your Gmail account
2. Add it to `pizzalib` settings as `gmailUser` and `gmailPassword`

**Important**: The Gmail app password is stored unencrypted on disk.

### Getting Phone Alerts (SMS)

Use your carrier's email-to-SMS gateway:

| Carrier | Email Format |
|---------|-------------|
| AT&T | `<number>@txt.att.net` |
| Verizon | `<number>@vtext.com` |
| Sprint | `<number>@messaging.sprintpcs.com` |
| T-Mobile | `<number>@tmomail.net` |

### Choosing Keywords

Start with:
* Your street name or neighborhood
* Local landmarks or businesses
* 10-codes (e.g., "10-4" = acknowledgment)
* Local police codes specific to your area

Find codes at [this reference](https://www.bearcat1.com/radio.htm).

## Building from Source

```bash
git clone https://github.com/lilhoser/pizzawave.git
cd pizzawave
dotnet build pizzalib/pizzalib.csproj
```

Build output is organized in the `artifacts/` folder by project.

## See Also

* [pizzaui](../pizzaui/README.md) - Windows UI application
* [pizzapi](../docs/pizzapi.md) - Cross-platform UI application
* [pizzacmd](../pizzacmd/README.md) - Command line application
* [Main README](../docs/README.md) - Project overview
* [Building Guide](../docs/building.md) - Detailed build instructions
