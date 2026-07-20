# Initial control-channel collapse flight recorder

Date: 2026-07-20 (America/New_York)

## Question

Previous work tested recovery behavior after control-channel decode had already
collapsed. This experiment instead preserves the signal at onset and asks two
foundational questions:

1. Does the same IQ delivered to the live decoder actually degrade when live
   decode first reaches zero?
2. Can a fresh decoder still decode those exact samples?

The recorder does not extend retune grace, change source centers, score
alternate channels, or induce an outage. It waits for a natural event while
the existing system is healthy.

## Activation and trigger

A bounded GNU Radio branch receives the same complex source stream as the live
P25 graph. It translates the configured primary control channel to baseband,
decimates to approximately 96 ksps, and retains a 30-second in-memory ring.

An automatic event is eligible only after the live primary has first reported
healthy decode. It triggers when a later three-second health interval reports
less than 2 msg/s while the live decoder is still tuned to that same primary.
The capture then writes the preceding 30 seconds and following 60 seconds as
complex-float IQ plus JSON metadata. Calibration captures do not consume the
one-event automatic quota. `SIGUSR2` provides the calibration trigger only.

The monitored systems are deliberately narrow:

- RPI: `etv-raymond-hinds` only;
- OT: `whiteoakmt-nbradley` and `whiteoakmt-hamilton` only;
- excluded: disconnected RPI Jackson and consistently healthy OT Cleveland.

Implementation branches and commits:

- current OT telemetry lineage: `codex/initial-collapse-capture-live`,
  `50b82fd` plus ownership correction `e86300d`;
- exact RPI telemetry lineage: `codex/initial-collapse-capture-rpi`,
  `8bcda833` plus ownership correction `4965b804`;
- consolidated experimental lineage: `codex/initial-collapse-capture`,
  `e786f34` plus ownership correction `c4d8aef`.

No branch was merged into upstream Trunk Recorder `master`.

## Calibration

RPI's live calibration triggered at 2026-07-20 15:27:53.023 EDT and wrote:

`1784575673023-etv-raymond-hinds-calibration.fc32`

The file contains exactly 2,903,225 pre-trigger and 5,806,451 requested
post-trigger samples at 96,774.1935 samples/sec (69,677,408 bytes). A separate
fresh Trunk Recorder process decoded RFSS 002, site 008, system 2AD, WACN
BEE00, and NAC 2A4 from it at generally 20-40 msg/s. Production TR remained
active during replay.

OT's five-source live graph initially aborted after the capture branches were
added. A bounded AddressSanitizer reproduction showed that the free occurred
inside the installed `unit_script` plugin, not the recorder:

`Unit_Script::~Unit_Script -> plugman_voice_codec_data -> p25p1_fdma::process_voice`

Only the new executable had been installed, leaving an older standard plugin
ABI beside it. The same sanitizer run completed cleanly for its full 25-second
window when the executable loaded the standard plugins from the same build.
OT was therefore armed as one coherent binary/plugin build. The separately
built PizzaWave `libcallstream.so`, already matched to the telemetry API, was
preserved. This deployment lesson is part of the experiment because the
single-binary installation produced misleading failures on both hardware
architectures.

At final activation:

- RPI PID `121912`, zero restarts, Raymond capture armed at 96,774.2 samples/sec;
- OT PID `2038314`, zero restarts, North Bradley and Hamilton armed at 96,000
  samples/sec;
- deployed binary SHA-256 values: RPI
  `42d31cc1fad56512b06c5603362a8ad2a4c99e9413db3484cf0f28a81591b979`
  and OT
  `bdc8dd87146badecef534d02c1f54a1d147f96d35ec6a0f435bf80f156ca40a0`;
- all capture directories are owned by `trunk-recorder:trunk-recorder`, mode
  `0750`;
- OT decoded all three sites strongly for more than 30 seconds after arming.

## First natural Raymond event

The currently armed RPI process captured a natural initial collapse at
2026-07-20 15:49:58.023 EDT:

`1784576998023-etv-raymond-hinds-automatic.fc32`

The metadata records:

- live primary and configured primary: 773.781250 MHz;
- source center: 771.931250 MHz at 6 Msps;
- live decode rate: 0 msg/s;
- raw messages in the live three-second interval: 3;
- 2,903,225 pre-trigger and 5,806,451 post-trigger samples;
- completed 60.002 seconds after trigger;
- file size: 69,677,408 bytes.

A fresh decoder replayed the complete file. It correctly identified Raymond
immediately. In the seconds approaching the trigger, replay decode was
38, 38, and 30 msg/s, then fell to 8 msg/s at the trigger-aligned interval.
Unlike the live decoder, it remained decodable: the next intervals were 19,
22, and 38 msg/s. The later 0 msg/s at replay EOF is not part of the event.

One-second spectral measurements independently show a real received-power dip.
The seven seconds before the sharp edge averaged approximately -66.39 dBFS;
seconds 27-29 of the file averaged -69.84 dBFS, a 3.45 dB reduction, with a
minimum of -70.25 dBFS. A relative in-band/outer-band spectral measure fell by
about 3.8 dB. The signal centroid moved by only about 70 Hz, so this event is
not explained by control-channel centering or abrupt frequency drift.

## Interpretation

This event rejects both simple extremes:

- it is not a total RF outage: the exact retained signal remained decodable in
  a fresh process;
- it is not merely a false live counter: the retained IQ contains a coincident
  3-4 dB degradation and replay throughput falls sharply at the same point.

The best current explanation is an interaction: a modest but real RF-quality
dip crosses a state- or implementation-dependent threshold in the long-running
live decoder, turning an 8 msg/s degradation into a reported 0 msg/s collapse.
The current capture is narrowband and filtered before storage, so it cannot yet
separate propagation, interference, front-end gain behavior, or a beneficial
effect of the capture/replay filter. OT remains armed to determine whether its
next North Bradley or Hamilton event has the same signature.

## Recommended next experiment

Add one passive, always-primary shadow P25 decoder beside the live decoder on
the same SDR sample stream. The shadow must have its own
message queue and count valid P25 frames only; it must not create calls, invoke
plugins, retune, or affect system state.

At the next natural event, retain the existing IQ window and compare live and
shadow frame rates at one-second cadence:

- both collapse together: the delivered RF/sample impairment is sufficient;
- live collapses while shadow continues: live control/decoder interaction is
  the discriminator;
- both stay healthy while the system rate reports zero: queue/accounting is
  the discriminator.

This directly tests the newly observed amplification without collecting
historical control-channel evidence or broad host-metric scaffolding. A wider
pre-channelizer IQ branch should follow only if both decoders collapse and the
remaining question is propagation versus interference/front-end behavior.

## Shadow-decoder implementation candidate

Implemented and committed on 2026-07-20. The current-lineage candidate was
subsequently merged into local Trunk Recorder `master` as `19ae14f`, but was
not pushed, configured, or deployed while another Codex task owns production
deployment:

- OT/current telemetry lineage: `codex/initial-collapse-capture-live` at
  `51920b1`;
- exact RPI telemetry lineage: `codex/initial-collapse-capture-rpi` at
  `c923e02c`.

The per-system `collapseShadow` setting constructs a second existing
`p25_trunking` graph from the same SDR source and fixes it on the first
configured control channel. Its independent queue is drained only for counts;
messages are not parsed and cannot create calls, invoke plugins, retune, or
change system state. OP25 timeout markers are excluded from both one-second
comparison counters. The production graph, production message accounting, and
three-second collapse trigger are unchanged.

Each enabled system emits a one-second `TR_SHADOW` sample containing live and
shadow frequencies, non-timeout queue-message rates, raw counts, and window
duration. A bounded in-memory timeline retains the configured pre-trigger
duration. When
the existing flight recorder triggers, its JSON receives that prehistory and
continues appending samples until IQ capture finishes, thereby producing one
event-local live-versus-shadow sequence with no database or UI dependency.

Offline validation used isolated build and replay directories only:

- OT compiled the complete current-lineage candidate. A manual five-second
  pre/ten-second post capture produced exactly five trigger-and-prehistory
  timeline entries and ten post-trigger entries. A forced live alternate-CC
  sequence changed `liveControlChannelHz` while
  `shadowControlChannelHz` remained fixed on 769.606250 MHz.
- RPI compiled a clean archive of its exact older lineage. A 25-second replay
  of the live Raymond calibration capture identified RFSS 002, site 008,
  system 2AD, WACN BEE00, and NAC 2A4. Live and shadow one-second counts
  matched exactly in every reported interval, including startup at 9 msg/s
  and steady rates from 26-42 msg/s.

Production verification after testing showed the pre-existing deployed hashes
unchanged: OT
`bdc8dd87146badecef534d02c1f54a1d147f96d35ec6a0f435bf80f156ca40a0`
and RPI
`42d31cc1fad56512b06c5603362a8ad2a4c99e9413db3484cf0f28a81591b979`.
Both services were active. No service was restarted for this implementation or
validation.

The next action requires explicit coordination: decide whether and where to
push local `master`, assign one deployment owner, review the coherent
executable/plugin artifact set, and only then enable `collapseShadow` on
Raymond, North Bradley, and Hamilton.

## Artifacts and rollback

RPI evidence is under:

`/var/lib/pizzawave/rf-surveys/manual/20260720-initial-collapse-flight-recorder/rpi`

OT evidence will be written under:

`/var/lib/pizzawave/rf-surveys/manual/20260720-initial-collapse-flight-recorder/ot`

RPI backups are under
`/var/backups/pizzawave/collapse-capture-20260720T192355Z-rpi` and
`collapse-capture-20260720T193900Z-rpi-ownership-fix`. OT's coherent-build
rollback is under
`/var/backups/pizzawave/collapse-capture-20260720T195100Z-ot-coherent-build`.

An earlier Raymond automatic file at 15:36:20 EDT came from a process replaced
during deployment correction. It remains useful corroborating evidence but is
not the primary result above. Automatic quota is process-local, which explains
why both files exist despite `collapseCaptureMaxEvents: 1`.

## Limits

This is one natural event on one site. The narrowband branch changes bandwidth
and replay applies another channelizer, so replay superiority is not by itself
proof of decoder state. OT has not yet produced a qualifying event. The result
does, however, directly establish that Raymond's onset combined a real modest
signal fade with a materially worse live-decoder outcome.
