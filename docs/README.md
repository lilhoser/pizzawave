
# Introduction
![plot](logo-med.png#right) `pizzawave` is a set of cross-platform .NET tools for processing audio data streamed by the [callstream plugin](https://github.com/lilhoser/callstream) of [trunk-recorder](https://github.com/robotastic/trunk-recorder). The audio data consists of calls recorded by trunk-recorder from conventional and trunked radio systems, such as local fire/rescue/EMS. `pizzawave` tooling transcribes these calls to text using [OpenAI's Whisper AI model](https://openai.com/research/whisper) as exposed through [whisper.net toolchain](https://github.com/sandrohanea/whisper.net). Among other features, the application allows you to monitor and set alerts for keywords of interest.

The `pizzawave` project consists of these tools:
* A windows-only .NET UI (`pizzaui` in source)
* A cross-platform .NET command line application (`pizzacmd` in source)
* A cross-platform .NET library (`pizzalib` in source), used by the UI and CLI application

Please be sure to read the README for each individual tool.

# Requirements

Regardless of whether you choose to use the UI, command line application, or roll your own application that uses the cross-platform library, you will need to observe these requirements:

* A Linux system running trunk-recorder with the [callstream plugin](https://github.com/lilhoser/callstream) configured
* An operating system capable of running .NET 8.0 runtime (e.g, Win, Lin or Mac)
    * The pizzawave tools currently target .NET 8.0, but if you are building from source, earlier versions should work as well.
* The requirements as specified in the tool of choice:
    * `pizzaui`: Windows-only | [README](https://github.com/lilhoser/pizzawave/tree/main/pizzaui)
    * `pizzacmd`: All supported platforms | [README](https://github.com/lilhoser/pizzawave/tree/main/pizzacmd)
    * `pizzalib`: All supported platforms | [README](https://github.com/lilhoser/pizzawave/tree/main/pizzalib)

# Architecture

![plot](pizzawave-architecture.png#right)

As shown in the illustration, pizzawave uses a `server`-`client` model, where the server is either the pizzawave UI or command line application and the client is one or more trunk-recorder systems. This design allows pizzawave to accept radio transmissions from multiple instances of trunk-recorder, which might be recording audio data from separate SDR device arrays monitoring broadcasts from different trunked radio systems.

Pizzawave listens for audio data from trunk-recorder systems, translates the data into textual transcriptions using Whisper AI, and processes alert rules to notify you of interesting broadcasts.

# Building from Source

## Windows
You can use Visual Studio Community Edition for free.

## Mac and Linux

* [Install .NET core](https://learn.microsoft.com/en-us/dotnet/core/install/)
* clone this repo
* CD into repo source
* run `dotnet build --runtime <RID>` where [RID can be found here](https://learn.microsoft.com/en-us/dotnet/core/rid-catalog)

# Configuration

Pizzawave configuration lives in `<user profile>\pizzawave\settings.json`. On Windows, this is `Users\<user>\AppData\Roaming\pizzawave\settings.json`. Please see the READMEs for each individual tool you are using for what settings options are available and how to use them in your setup. `pizzaui` includes a feature that allows you to setup your configuration in a more automated way, but you can always create the file manually. If you run the UI or command line application without a settings file, the default one will be created in the location specified above.

_Important_: Make sure your `trunk-recorder` system is configured to connect to the right IP address. In an exotic scenario where you're running `pizzacmd` from both a Windows host system and a WSL2 Ubuntu system, the host system and the virtual Ubuntu system will have different IP addresses! In this scenario, you might forget to set the correct IP address on the `trunk-recorder` system, and only one of these machines will receive audio data, while the other might be stuck on this:

```
StreamServer Verbose: 1 : 3/22/2024 3:39 PM: Listening on port 9123
```

# Other

## Diagnostics

All logs, model files, settings files, and alert data can be found in your operating system's user profile folder.
* `alerts` - this folder contains WAV data for matched alerts
* `Logs` - this folder contains all log files
* `model` - this folder contains all auto-downloaded GGML model files

If your logs are not detailed enough, adjust the `TraceLevelApp` parameter in `settings.json`.

## What's up with the name?
I dunno, I like pizza and Teenage Mutant Ninja Turtles, so it seemed to work.

## Resources

* If you're struggling to setup trunk-recorder, I recommend [this extremely well-written intro guide](https://www.andrewmohawk.com/2020/06/12/trunked-radio-a-guide/).
* Use [this tool](https://alertapi.alertpage.net/sdr/) to calculate some trunk-recorder configuration parameters like center frequency and to understand how many SDR dongles you will need to cover channels of interest
* Other trunk-recorder related projects performing transcription:
    * [trunk-transcribe](https://github.com/CrimeIsDown/trunk-transcribe)
    * [trunk-recorder-stack](https://github.com/ge0metrix/trunk-recorder-stack)
    * [tr-uploader](https://github.com/TheGreatCodeholio/icad_tr_uploader) and [icad_tone_detection_api](https://github.com/TheGreatCodeholio/icad_tone_detection_api)