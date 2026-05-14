# SDR and Trunk-Recorder Setup

PizzaWave assumes trunk-recorder performs RF capture and callstream delivers
completed calls to `pizzad`.

## Basic Concepts

| Term | Meaning |
| --- | --- |
| Control channel | P25 channel carrying system control messages |
| Voice channel | Channel used for an actual call |
| Source | One SDR definition in trunk-recorder config |
| Sample rate | Width of spectrum captured by a source |
| Digital recorders | Concurrent digital voice recordings a source can handle |
| Decode rate | Control-channel message decode quality |
| Retune | trunk-recorder switched control-channel target |

## Wizard Flow

The first-run wizard can:

- detect RTL-SDR USB devices;
- detect an existing trunk-recorder config;
- fetch or accept RadioReference-derived system data;
- build or import a talkgroup CSV;
- recommend SDR/source coverage;
- assist with gain/error calibration.

If an existing trunk-recorder service is running, SDR detection or calibration
may require stopping it. The wizard should ask first and restart services on
cancel where possible.

## Calibration

Calibration is intentionally guided:

1. The wizard identifies systems, control channels, source coverage, and SDRs.
2. The user supplies initial gain/error values from GQRX or chooses to skip.
3. PizzaWave runs bounded tuning sweeps and shows log output.
4. The wizard presents findings and lets the user apply or reject changes.

GQRX may require a desktop session. On headless installs, open GQRX manually on
the Pi/host desktop if the service cannot launch it.

## Fresh Config Defaults

For fresh PizzaWave-managed configs:

- use callstream to `127.0.0.1:9123`;
- omit `captureDir`;
- prefer control+voice coverage when enough SDRs exist;
- warn clearly when a system cannot be fully covered;
- use the fewest SDRs that still cover the selected systems.
