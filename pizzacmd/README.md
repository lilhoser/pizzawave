
# Introduction
<img align="right" src="http://github.com/lilhoser/pizzawave/raw/main/docs/logo-med.png"> `pizzacmd` is a .NET command line application built on top of the [`pizzalib`](https://github.com/lilhoser/pizzawave/tree/main/pizzalib) library.

<img align="center" src="http://github.com/lilhoser/pizzawave/raw/main/docs/screenshot4.png">

# Requirements
* [Requirements as specified in the `pizzawave` README](https://github.com/lilhoser/pizzawave)
* [Requirements as specified in the `pizzalib` README](https://github.com/lilhoser/pizzawave/tree/main/pizzalib)
* A supported operating system (Win, Lin, Mac) running .NET 8 or later

# Configuration
`pizzacmd` currently has no settings beyond what is contained in `pizzalib`.

# Running on WSL2

## Port forwarding

Remember that `trunk-recorder` needs to be configured to communicate with the server. If you're running `pizzacmd` from within a linux OS in WSL2 on Windows, you'll need to make sure the WSL2 instance is configured to receive the network traffic:

```
netsh interface portproxy add v4tov4 listenport=[PORT] listenaddress=0.0.0.0 connectport=[PORT] connectaddress=[WSL_IP]
```

Replace `[PORT]` with your listen port, such as `9123` and `[WSL_IP]` with your WSL instance IP address, eg `172.23.192.16`.

## Whisper issues

If you receive an error like this:

```
Whisper Error: 1 : 3/22/2024 5:12 PM: Failed to transcribe WAV data: Failed to load native whisper library. Error: Unknown error
```

It most likely means you have the wrong `Whisper.net` runtime installed.  For linux, you must install either `cublas` or revert to CPU only (`Whisper.net.Runtime`).