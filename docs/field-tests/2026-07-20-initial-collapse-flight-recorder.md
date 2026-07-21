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

Implemented and committed on 2026-07-20 and deliberately kept on isolated
integration branches for maintainer review and possible upstream submission:

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

The candidates were initially validated without changing production. After
deployment ownership was explicitly assigned, coherent experimental artifact
sets were installed at 17:25 EDT on 2026-07-20 and `collapseShadow` was enabled
for Raymond, North Bradley, and Hamilton. Trunk Recorder was restarted once on
each host. PizzaWave was not redeployed.

- OT PID `2181680`, zero restarts after activation, binary SHA-256
  `368a068a973ae5fe9fd3f6b3b3734e8bcf0eb765ce0bdde645fde45aa5b326f1`;
- RPI PID `158920`, zero restarts after activation, binary SHA-256
  `15824b7f8083d4b824d10d6e06a22b81a75eca93b4a7b8aa8499e76d299e0449`;
- each host received the standard plugins from the same build as its
  executable; the separately built PizzaWave `libcallstream.so` was preserved;
- both PizzaWave health endpoints returned `status: ok` and live TR activity
  recovered after the normal post-restart interval;
- OT immediately produced matched live/shadow baselines of approximately
  27-42 valid frames/s on both monitored systems.

## First live shadow event

Raymond produced a qualifying natural event only 19 seconds after the RPI
restart. The recorder triggered at 2026-07-20 17:26:05.019 EDT and completed at
17:27:05.022 EDT:

`1784582765019-etv-raymond-hinds-automatic.fc32`

The file contains 1,713,426 available pre-trigger samples and the requested
5,806,451 post-trigger samples at 96,774.1935 samples/sec. The shorter-than-
configured prehistory is expected because the process had not yet been alive
for 30 seconds. The file is 60,159,016 bytes and has SHA-256
`952196947658ba2c2ab2438ee4c44fc6f66311ee14f8f89f3dd8ee1ad7ce271e`.
Its JSON contains 78 one-second live/shadow samples.

At the trigger, the unchanged primary was 773.781250 MHz, the live three-second
rate was 0 msg/s, and the concurrent fixed-primary shadow rate was 1 frame/s.
The one-second timeline shows both decoders descending together from a healthy
42 frames/s to 1 frame/s before the trigger. This is direct evidence that this
onset was present in the sample stream delivered to both independent decoders;
it was not created solely by accumulated state in the live decoder.

The behavior immediately after onset is different. One second after the
trigger, the live decoder began cycling alternate control channels and stayed
at 1 frame/s. The shadow remained on 773.781250 MHz and recovered to 22, 28,
and 36 frames/s over the next three seconds. Similar fixed-primary recoveries
to 18-24 frames/s occurred during the later live alternate-channel cycle.
When the live decoder returned to the primary, the two rates converged again.

The current interpretation therefore has two parts:

1. the initial Raymond collapse is a real shared RF/sample-delivery impairment,
   not merely queue accounting or a poisoned long-running decoder;
2. brute-force alternate-channel hopping can materially extend the live outage
   after the primary becomes decodable again.

This does not yet identify propagation, interference, or front-end behavior as
the cause of the shared onset, and one Raymond event does not establish that OT
has the same cause. The shadow experiment remains active on OT specifically to
obtain that geographic discriminator. No recovery-policy change should be made
until an OT event is captured and compared with the Raymond result.

An isolated fresh-process replay of the captured IQ produced 84 one-second
samples, spanning 0-42 frames/s in both decoders. Both reached zero in the same
six intervals; only four samples differed at all. Replay also logged seven
failed requests for alternate frequencies outside the narrowband file source,
so its changing live-frequency labels do not represent actual retunes. The
replay independently confirms that the retained primary-channel IQ contains
the repeated decode losses, while the production wideband shadow timeline is
the valid evidence for the recovery cost of real live retuning.

## OT cross-geography shadow event

North Bradley produced the required OT event at 2026-07-20 17:48:25.012 EDT:

`1784584105012-whiteoakmt-nbradley-automatic.fc32`

The completed file contains the full 30-second prehistory and 60-second
post-trigger window: 8,640,000 complex samples at 96,000 samples/sec,
69,120,000 bytes. SHA-256 values are
`845ab28446f73e0726514165ca0f38bbec3cfb5061819b0900ab577bd76c7100`
for IQ and
`842fe59d87151559a1c2c0459cd215668f6d70d7f0b96d97c42da0bbeecaa9c6`
for JSON. The file size remained unchanged across a second check and the JSON
contains 91 one-second live/shadow samples.

The onset signature corroborates Raymond at the decoder boundary but not at
the amplitude boundary. During the six seconds leading into the trigger, both
independent decoders repeatedly fell to 1-4 frames/s on the unchanged
769.606250 MHz primary. The trigger metadata records live 1 msg/s and shadow
4 frames/s; the trigger-aligned one-second sample is 4/4. This again rejects a
live-only counter or accumulated-live-decoder explanation for the onset.

Recovery then diverged in both directions:

- during 12 post-trigger samples on alternate live control channels, live was
  0-1 frames/s while the fixed-primary shadow reached 18 frames/s and exceeded
  live in 11 samples;
- after live returned to 769.606250 MHz, 48 samples had live rates of 1-36 and
  shadow rates of 0-4; live exceeded shadow in 42 samples and ended at 36/1.

This is stronger than a claim that retuning is simply good or bad. The same
transient can drive two decoder graphs fed by the same source into different
long-lived recovery states. Retuning helped the live graph escape the state in
this OT event, whereas Raymond's fixed-primary shadow recovered first. The
initial impairment and the recovery amplification are separate phenomena.

### OT IQ and replay analysis

One-second spectral analysis of the complete IQ file found only a modest
approximately 0.41 dB raw-power decline and 0.48 dB in-band/outer-band decline
from the preceding baseline into the trigger. The outer-band noise floor stayed
near -126.3 dB on the same analysis scale, and the signal centroid varied by
less than about 80 Hz around the edge. All 8,640,000 samples were finite, with
no zero samples, repeated adjacent complex samples, or contemporaneous TR or
kernel USB/overflow messages.

A fresh isolated replay reproduced the waveform's low and erratic decode. In
the trigger-aligned file seconds 25-30, both fresh graphs reported the identical
sequence 4, 1, 3, 1, 1, 1 frames/s. Across all 91 signal-bearing replay
intervals, live and shadow counts matched exactly. The replay's attempted
alternate tune was rejected because the narrowband file covers only the
primary, so both graphs continued decoding the same retained primary waveform.

Hamilton provides a same-host control. Across 95 simultaneous live samples
spanning the North Bradley event, Hamilton remained at 23-42 frames/s with no
zero or <=1 frame/s interval. The OT event is therefore not a host-wide CPU,
service, or sample-flow outage.

The cross-geography conclusion is narrower and more useful than "both sites
fade." Raymond had a coincident 3-4 dB received-power/CNR drop. North Bradley
had nearly stable power and noise but a stored waveform that remained difficult
for a fresh decoder. Both initial collapses are real signal-path impairments,
but they do not share one simple amplitude-fade signature. North Bradley is
more consistent with in-channel modulation distortion such as changing
multipath/simulcast geometry or narrow co-channel interference. A good omni
antenna does not rule out that class of impairment; an omni can preserve
multiple competing paths.

A mistaken local-only merge commit, `19ae14f`, was created while interpreting
"main" as Trunk Recorder's default branch. It was never pushed or deployed and
was removed immediately; local and remote `master` both remain at `382f5f2`.

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
Shadow-deployment rollback sets are under
`/var/backups/pizzawave/collapse-shadow-20260720T220000Z-rpi` and
`/var/backups/pizzawave/collapse-shadow-20260720T220000Z-ot`.

## Paired wide/narrow follow-up

The next discriminator is now implemented on the experimental Trunk Recorder
branches. Each qualifying collapse writes the existing 96,000 sample/sec
primary-channel capture and a synchronized, independently channelized wider
capture from the same SDR source. The wider branch retains approximately
600 kHz of spectrum at 800,000 samples/sec on OT. On RPI its rate is selected
from an integer decimation of the 6 MHz source and is expected to be
approximately 857,143 samples/sec. The paired JSON records share a
`captureGroupUnixMs` value and identify their `narrow` or `wide` variant.

The wide window is deliberately shorter (10 seconds before and 20 seconds
after the trigger) than the narrow 30/60-second window. It is long enough to
cover onset while bounding RAM and disk use. This experiment tests the missing
ownership boundary in the existing evidence:

- a broadband power/noise or spectral change across the wide capture would
  implicate propagation, interference, SDR front-end behavior, or source
  delivery before the narrow channelizer;
- stable neighboring spectrum with degradation confined to the P25 channel
  would favor in-channel multipath/simulcast or co-channel interference;
- clean wide samples but a corrupted narrow product would implicate the
  channelizer or downstream graph.

OT was deployed and armed at 2026-07-20 19:23:31 EDT from experimental commit
`313d247`. The installed binary SHA-256 is
`db7d1920e9fcecd42087d0db0fd133d4167d16ee88c03ff8cc3a3d637c9cc603`.
Both North Bradley and Hamilton logged a 96 kHz narrow rate and 800 kHz paired
wide rate, with zero service restarts and healthy PizzaWave live activity.
Rollback is under
`/var/backups/pizzawave/collapse-wide-20260720T231500Z-ot`.

RPI was deployed and armed at 2026-07-20 19:35:40 EDT from experimental
commit `393a0732`. The installed ARM64 binary SHA-256 is
`be56f18daad7930a552deb0cb432556caa447de13b9457a5ffcf98ef64a2de17`.
The 6 MHz Airspy source selects a 6/7 decimation and logged a paired wide rate
of 857,142.857 samples/sec beside the 96,774.2 sample/sec narrow capture. The
service had zero restarts, PizzaWave live activity was healthy, and
`libcallstream.so` retained SHA-256
`31ac526d66664e4fed8d0a43acf0549dac12ad6a35ec38b25704b53e1fc31450`.
Rollback is under
`/var/backups/pizzawave/collapse-wide-20260720T233600Z-rpi`.

An isolated ARM replay before deployment also produced a complete paired
manual capture with a common group ID. Its file source was only 96,774.2
samples/sec, so both outputs correctly used that available rate; the OT native
validation above is the high-rate branch test. Both validations are plumbing
checks, not RF evidence.

Before deployment, an isolated OT replay verified that a manual trigger wrote
a coherent pair with the same capture group: 3,840,000 bytes at 96 kHz and
32,000,000 bytes at 800 kHz for a two-second prehistory plus three-second
post-trigger window. The validation files are temporary and are not field
evidence.

### OT paired-capture result

The first production pair triggered five seconds after the new process started
and therefore captured recovery but not onset. It was still a useful integrity
check: low-decode and >=30 frame/s seconds had outer-band power within 0.02 dB,
while the control-channel/outer-band ratio was 3.36 dB higher during good
decode. All samples were finite and there were no zero, repeated-adjacent, or
clipped samples.

TR was re-armed from a healthy 36-42 frame/s state. North Bradley then produced
a second pair at 2026-07-20 19:29:17 EDT. The wide file contains the full ten
seconds before and twenty seconds after trigger: 24,000,000 complex samples,
192,000,000 bytes. The narrow file contains approximately 26.34 seconds before
and the full 60 seconds after trigger: 8,288,490 complex samples, 66,307,920
bytes. Stable-file SHA-256 values are:

- narrow IQ: `8140f02771ae6766c0f630707a48f59c0c000ac61ceccb3ff5d47aa82d58451a`;
- narrow JSON: `a1ce45b1f0a2d4394b544d70d35c5b046f6657da16173835cd9b9e2045e063b4`;
- wide IQ: `b8c4622a28d8ed81ac4ceb7c8959db8d1d48f417817063b1080945f9cdb5a208`;
- wide JSON: `805b8fa4f95e013c853978ecf7570144463d8d8fd83ca11377383cd24e437c3b`.

The paired onset is frequency selective. Comparing the 19 one-second intervals
at <=3 live frames/s with the four intervals at >=30 frames/s, outer-band
(150-300 kHz offset) power differed by only 0.14 dB. The control-channel to
outer-band ratio was 1.72 dB higher in the recovered intervals. Raw wide-band
power changed by 0.78 dB, largely because a distinct neighboring transmission
approximately 54 kHz below the control channel appeared during recovery; that
12.5 kHz band rose 10.5 dB and was not present at onset. No clipping, zero,
repeated-adjacent, or non-finite samples occurred.

This rejects a broadband antenna fade, tuner-gain collapse, USB/sample outage,
and the neighboring transmission as explanations for this North Bradley
event. It favors a channel-selective cancellation/fade at the antenna caused
by multipath or simulcast geometry. A channel-local interferer remains
possible, but is less favored because failed decode coincided with lower, not
higher, control-channel energy and no new nearby spectral component appeared.

Decoder state remains a separate recovery amplifier. After the shared onset,
the fixed-primary shadow temporarily recovered to 15 frames/s while live was
retuned, then remained at 1 frame/s while live recovered through 19, 30, 39,
and 36 frames/s on the primary. Neither fixed tuning nor retuning is a universal
fix, and this divergence cannot explain the initial simultaneous collapse.

### Hamilton paired-capture result

Hamilton produced a complete paired event at 2026-07-20 21:44:56 EDT. This is
an especially useful control because its 855.212500 MHz SDR source is centered
exactly on the 855.212500 MHz control channel. Both independent decoders fell
together before trigger: the live/shadow sequence over the last four seconds
was 3/9, 1/3, 2/3, and 3/6 frames/s. The wide file contains the full 10/20
second window (24,000,000 complex samples, 192,000,000 bytes), and the narrow
file contains the full 30/60 second window (8,640,000 complex samples,
69,120,000 bytes). Stable SHA-256 values are:

- narrow IQ: `1a3482fd4a967e85113ca2a93753cb4e756780401c05324d846e27250f41cdcb`;
- narrow JSON: `aa9aa0298a298f74ebf07406c85c67110ab2cdea9aeb8ecd23c63d6d86039357`;
- wide IQ: `b7caecfcbd1eafd5a85fe4076885d2ca55d4eaea7de6cb148b35e5bbddb7ae4a`;
- wide JSON: `e194b6528df922f4044d102c52c798a2b4d43bed6caae665425ca58e80d32568`.

Hamilton rejects a simple fade more directly than North Bradley. During the
shared onset, control-channel energy increased by approximately 2 dB relative
to the stable outer-band floor while decode fell to 1-3 frames/s. Across the
complete wide window, the 16 intervals at <=3 live frames/s had a mean
control-channel/outer-band ratio 1.35 dB *higher* than the seven intervals at
>=15 frames/s; mean outer-band power differed by only 0.10 dB. Several distant
12.5 kHz channels varied independently, but no neighboring spectral component
appeared at onset. All samples were finite with no zero, repeated-adjacent, or
clipped values.

The combined OT evidence therefore points to lost modulation quality rather
than insufficient total control-channel energy. North Bradley's failed decode
coincided with lower channel energy; Hamilton's coincided with higher channel
energy. What they share is an impairment confined to the P25 channel while the
surrounding receiver spectrum remains stable. Dynamic simulcast/multipath
superposition is the leading explanation because it can either cancel or add
energy while closing the modulation eye. A precisely overlapping co-channel
signal remains possible. Source centering, broadband antenna fading, receiver
gain, USB/sample corruption, and queue accounting do not fit both events.

### OT instrumentation stability

The paired experiment itself is too expensive to leave active on OT. After
deployment, TR repeatedly logged GNU Radio `gardner_cc` failures with
`mmse_fir_interpolator_cc: imu out of bounds`; Source 2 or Source 4 then stopped
delivering samples and TR exited. By 21:10 EDT systemd had restarted it 13
times. No matching error or source stall occurred in the 17:00-19:23
pre-deployment journal window. This is an experiment-induced service stability
problem, separate from the RF evidence retained in the completed files. OT's
wide branches should be removed after preserving the captures; the lighter
single-system RPI deployment remained stable with zero restarts.

An earlier Raymond automatic file at 15:36:20 EDT came from a process replaced
during deployment correction. It remains useful corroborating evidence but is
not the primary result above. Automatic quota is process-local, which explains
why both files exist despite `collapseCaptureMaxEvents: 1`.

## Limits

The evidence contains natural events in both geographies, not enough to assign
one universal physical cause or estimate prevalence. The paired capture closes
the wide-versus-channel-local evidence gap for North Bradley, but Raymond's
existing events remain narrowband. Replay applies another channelizer, so
replay behavior is not by itself proof of decoder state. A natural RPI paired
capture remains necessary before deciding whether Raymond shares North
Bradley's frequency-selective signature.
