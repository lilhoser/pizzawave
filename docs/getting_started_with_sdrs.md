
# Getting started with SDRs

## Online streaming

Before starting your own at-home SDR setup to record broadcasts, it's useful to explore the online platforms where hobbyists and enthusiasts upload broadcast recordings.  Start with these:
* https://www.liveatc.net/
* https://www.openmhz.com
* https://www.broadcastify.com/
   
## Decide on your hardware and software basics

What device will host your SDR devices? A raspberry pi? A linux or windows PC? Consider the advantages of pre-baked images out there, such as Broadcastify's [RPI image](https://www.broadcastify.com/rpi/) and [trunk-recorder's docker images](https://github.com/robotastic/trunk-recorder/blob/rc/v5.0/Dockerfile). Understand the details of how you intend to use the recordings can help you decide on a hardware and software platform.

One you have hardware, you'll need to think about what decoding software to use. This software decodes the airwaves into actual audio streams, so you can post-process or upload them somwhere. Here are two popular ones:
* https://github.com/charlie-foxtrot/RTLSDR-Airband
* https://trunkrecorder.com/docs/intro
  
For the actual SDR hardware, the [RTL-SDR store](https://www.rtl-sdr.com/buy-rtl-sdr-dvb-t-dongles/) is an excellent starting point. Not only do they sell a great beginner kit, but they also recommend alternative SDRs and other hardware that will be useful in your setup.

If you get to the point in your project where you want to use multiple SDRs, you will need to most likely incorporate cable splitters.  But splitting halves your gain for each SDR source. There are powered splitters that can counteract the loss, or you can insert a LNA (low noise amplifier) somewhere upstream close to the antenna to offset the loss. Keep in mind you also get some loss from long cable runs, something on the order of a 6 or 7 dB loss per hundred feet. All of these losses add up, so it's important to consider some sort of amplifier.

## Preparing an RTL-SDR device

Because it's a popular beginner's choice, I'd like to provide some additional advice for configuring your RTL-SDR Blog SDR. The first important thing is to beware of RTL-SDR Blog's instructions. The device will come with an insert that directs you to their [V4 blog post](https://www.rtl-sdr.com/V4/) about setting up a V4 dongle. These instructions suggest that you uninstall the standard `librtlsdr` drivers in favor of their own. This doesn't appear to be needed any longer, as most vendors now natively support V4. As far as drivers and other dependencies for your RTL-SDR, you might choose to follow what `trunk-recorder` does in their [dockerfile setup](https://github.com/robotastic/trunk-recorder/blob/rc/v5.0/Dockerfile).

The first thing you will want to do is [flash a new serial number](https://forums.radioreference.com/threads/change-rtl-sdr-dongle-serial-numbers-for-novice-user.453672/) on your RTL-SDR using `rtl_eeprom -s`.  This is important when you get to the point where you're using multiple SDRs, so that your recording software can distinguish between devices. To avoid ambiguity, it's a good convention to set your serial string to an 8-digit number with leading zeroes, like `00000001`.

Another thing to consider is if you have an upstream LNA on your SDR setup, you might want to activate the bias-tee to remotely power the LNA. Unless you're using RTL-SDR Blog's custom drivers, this must be done each time you use the device by specifying it in the device string, e.g. `"device": "rtl=00000006,buflen=65536,bias=1"`.