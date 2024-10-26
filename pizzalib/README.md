
# Introduction
<img align="right" src="http://github.com/lilhoser/pizzawave/raw/main/docs/logo-med.png"> `pizzalib` is a standalone, cross-platform .NET library that provides the following capabilities to a containing application:
* Integration with [callstream plugin](https://github.com/lilhoser/callstream) of [trunk-recorder](https://github.com/robotastic/trunk-recorder) to receive audio data
* Running a simple server that receives and process audio data in preparation for transcription
* Audio to text transcription using [OpenAI's Whisper AI model](https://openai.com/research/whisper) as exposed through [whisper.net toolchain](https://github.com/sandrohanea/whisper.net)
* Email alerting
* Management of talkgroup data and alerting rules

Please be sure to read the [`pizzawave` README page](https://github.com/lilhoser/pizzawave)
 
# Requirements
* [Requirements as specified in the `pizzawave` README](https://github.com/lilhoser/pizzawave)
* A supported operating system (Win, Lin, Mac) running .NET 8 or later
* A .NET program that uses `pizzalib` as a reference (see `pizzaui` and `pizzacmd` as examples)
* The requirements for transcription using AI are discussed in the `Using Whisper for Transcription` section.

# Using Whisper for Transcription

`pizzalib` uses OpenAI's Whisper model for audio transcription, which [supports](https://github.com/sandrohanea/whisper.net/tree/fc1282f9a92f03e854a66c65b39ef2c9dfdc23e4?tab=readme-ov-file#multiple-runtimes-support) the following compute backends, which will be automatically selected at runtime in this order:

* `Whisper.net.Runtime.Cuda` (NVidia devices with all drivers installed)
* `Whisper.net.Runtime.Vulkan` (Windows x64 with Vulkan installed)
* `Whisper.net.Runtime.CoreML` (Apple devices)
* `Whisper.net.Runtime.OpenVino` (Intel devices)
* `Whisper.net.Runtime` (CPU inference)

If using NVidia GPU, be sure to install the [CUDA Toolkit](https://developer.nvidia.com/cuda-downloads).

## Switching base model

By default, `pizzalib` will attempt to download the base whisper GGML model using [Whisper.net's downloader](https://github.com/sandrohanea/whisper.net/blob/main/Whisper.net/Ggml/WhisperGgmlDownloader.cs), which is ~144mb at the time of writing. If you look at the source code in that link, you'll see that this downloader pulls whisper models from huggingface.co at [this url](https://huggingface.co/sandrohanea/whisper.net/tree/main/classic). To use any of the models on this page, simply download them to a folder and provide the full path in your pizzwave settings. It is well known that the latest "large" version of the model performs best but at a storage cost (~3.1gb).

# Configuration

`pizzalib` configuration lives in `<user profile>\pizzawave\settings.json`. On Windows, this is `Users\<user>\AppData\Roaming\pizzawave\settings.json`. This file can be manipulated in `pizzaui` and `pizzacmd`, but please see those tools' README file for details. The UI includes a feature that allows you to setup your configuration in a more automated way, but you can always create the file manually. If you run the UI or command line application without a settings file, the default one will be created in the location specified above.

Supported settings/parameters:
* `TraceLevelApp` (default=`Error`): Controls the verbosity of trace logging:  `All` = -1, `Off` = 0, `Critical` = 1, `Error` = 3, `Warning` = 7, `Information` = 15, `Verbose` = 31
* `WavFileLocation` (default=Off): by default, the audio data streamed from trunk-recorder will only be transcribed in-memory; to save the audio to compressed MP3 files locally on the server side, provide a path here. Note that this can consume significant space over time. It is advisable to periodically clean out these folders.
* `Alerts`: an array of alert rules, with these parameters:
    *  `Id`: a GUID uniquely identifying the rule
     * `Name`: a friendly name for the rule
     * `Email`: comma-separated list of emails to receive the alert
     * `Keywords`: comma-separated list of keywords that trigger the alert
     * `Frequency`: how often the rule should be re-evaluated on incoming call data; `RealTime` = 0, `Hourly` = 1, `Daily` = 2
     * `Talkgroups`: an array of `long` IDs for talkgroups of interest on the trunked system being monitored
     * `CaptureWAV`: should a WAV/MP3 be saved when this rule is triggered
     * `Enabled`: set to `true` to enable, `false` to disable
* `Autostartlistener` (default = `true`): whether or not to automatically start `pizzalib` listener when the program or UI starts
* `gmailUser` and `gmailPassword`: to send alerts via email/email-to-SMS, you must provide an app password to your gmail account. Note that this is *not* the password for your gmail account, but rather an app-specific password. See the Alerting section below for further details. Note that the gmail app password specified here is stored _un-encrypted on-disk_.
* `listenPort` (default=9123): what port should the `pizzalib` server listen on; the trunk-recorder client should be configured to connect to this port
* `analogChannels` (default=1): the number of analog channels in the received audio WAV data
* `analogBitDepth` (default=16): the bit depth of the received audio WAV data
* `analogSamplingRate` (default=8000): the sampling rate of the received audio WAV data, in hertz
* `talkgroups`: an array of talkgroups, with these parameters (see the next section for guidance):
    * `Id`: the decimal ID of the talkgroup
    * `Mode`: the talkgroup mode (`D`: digital, `A`: analog, `M`: mixed, `T`: tdma-capable, `De`: digital/partial encryption, `DE`: digital/full encryption)
    * `AlphaTag`: 16-character description intended as a shortened display on radios
    * `Description`: custom description
    * `Tag`: talkgroup official service tag
    * `Category`: additional category, if available
* `whisperModelFile` - path to the whisper model file to use; leave this blank to use the default "base" model (144mb) or download a different one as mentioned above.

## Talkgroups
A talkgroup is a concept specific to [P25 trunked radio systems](https://en.wikipedia.org/wiki/Trunked_radio_system) and refers to a logical grouping of users communicating on the trunked system. You will need to browse the [RR database](https://www.radioreference.com/db/browse/) to find information on trunk and traditional systems in your area. `pizzalib` needs to know about talkgroups in your area that you're interested in monitoring. Talkgroups are specified in the settings file discussed above. You might want to checkout the [trunk-recorder configuration how-to](https://trunkrecorder.com/docs/CONFIGURE#talkgroupsfile) and the talkgroups section of [this guide](https://www.andrewmohawk.com/2020/06/12/trunked-radio-a-guide/) if you're unsure about talkgroups or want to learn more.

# Alerts

Alerts are well-structured rules that tell `pizzalib` how to process audio data of interest. Read the sections below to find out more about Alerts.

## How do I get an email alert?

You need to [add an app password](https://support.google.com/accounts/answer/185833) to your gmail account and provide it in `pizzalib` settings.

## How do I get a phone alert?

You can send an alert to your phone by using your carrier's email-to-SMS service. As examples:
* ATT: <phone_number>@txt.att.net
* Verizon: <phone_number>@vtext.com
* Sprint: <phone_number>@messaging.sprintpcs.com
* T-Mobile: <phone_number>@tmomail.net

## What keywords do I choose?
A good start is your street name or other locations near you. The next step is to consider 10-codes (e.g., "10-4" typically means "okay") and other police codes specific to your locale. You can find some codes [here](https://www.bearcat1.com/radio.htm), but this requires some work on your part.