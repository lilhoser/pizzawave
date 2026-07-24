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

### RPI Raymond paired-capture result

Raymond produced the required complete pair at 2026-07-21 00:47:31 EDT. The
wide file contains the full ten seconds before and twenty seconds after trigger:
25,714,285 complex samples at 857,142.857 samples/sec, 205,714,280 bytes. The
narrow file contains the full 30/60-second window: 8,709,676 complex samples at
96,774.194 samples/sec, 69,677,408 bytes. RPI remained healthy with zero TR
restarts. Stable SHA-256 values are:

- narrow IQ: `303e111dfcdcf4e914dfcb976c021277b9621cc52763876953b81cfe539c44bb`;
- narrow JSON: `076baabcdaa7d42f6896730dac3364c8ef0fc65fc5a16392d3487e6194d05717`;
- wide IQ: `671e6eda0de6586816a3015f6bed270f8a461ead187c22d154aa8ddac5f9c3e1`;
- wide JSON: `5e1a65ee162ea3419cfdf1f7d0edf1bfe544b12967381c95ae94ac7e2f4c582d`.

Both independent decoders collapsed together on the unchanged 773.781250 MHz
primary. The pre-trigger live/shadow sequence descended from 15/15 and 12/12
to 4/4, 1/1, 3/3, and finally 3/4 at trigger. This time there was no meaningful
amplitude fade. Comparing the six pre-trigger intervals at <=3 live frames/s
with the two at >=10 frames/s, raw wide-band power differed by 0.13 dB, the
control-channel/outer-band ratio by 0.24 dB, and outer-band power by 0.16 dB.
No neighboring 12.5 kHz component appeared at onset. Every sample was finite,
with no zero, repeated-adjacent, or clipped values.

Recovery again separated decoder policy from onset. While live cycled all
three alternate control channels and remained at 1 frame/s, the fixed-primary
shadow varied between 3 and 12 frames/s. Both reached 15 frames/s about 23
seconds after trigger when live returned to the primary, but neither exceeded
18 frames/s during the full minute and both later fell again. Retuning clearly
extended the first recovery, but the retained primary waveform itself remained
intermittently impaired.

Raymond therefore corroborates the OT mechanism rather than the earlier idea
of one simple amplitude fade. Across North Bradley, Hamilton, and Raymond,
collapse occurs inside the P25 channel while surrounding spectrum and sample
delivery remain stable; control-channel energy may fall, rise, or remain nearly
unchanged. The common failure is modulation quality. Dynamic simulcast/multipath
superposition is the leading physical explanation because it naturally changes
the composite symbol trajectory without requiring a broadband power change. A
precisely overlapping co-channel interferer remains the principal alternative.
Frequency centering, antenna quality, host capacity, tuner gain, USB/sample
loss, queue accounting, and recovery grace do not explain the shared onset.

### OT instrumentation stability

The paired experiment itself is too expensive to leave active on OT. After
deployment, TR repeatedly logged GNU Radio `gardner_cc` failures with
`mmse_fir_interpolator_cc: imu out of bounds`; Source 2 or Source 4 then stopped
delivering samples and TR exited. By 21:10 EDT systemd had restarted it 13
times. No matching error or source stall occurred in the 17:00-19:23
pre-deployment journal window. This is an experiment-induced service stability
problem, separate from the RF evidence retained in the completed files. OT's
wide branches were removed after preserving the captures. At 2026-07-20
22:34 EDT, the pre-wide coherent binary, standard plugins, and configuration
were restored from
`/var/backups/pizzawave/collapse-wide-20260720T231500Z-ot`. The restored binary
SHA-256 is
`368a068a973ae5fe9fd3f6b3b3734e8bcf0eb765ce0bdde645fde45aa5b326f1`;
`libcallstream.so` remained unchanged at
`72ea0030c23456b511e0678215176fc668e22ed722e5593fe97f0056f57681cb`.
TR restarted cleanly with zero automatic restarts and approximately 196 MiB
of cgroup memory, down from about 1 GiB with the two wide branches. PizzaWave
and live TR activity were healthy.

RPI was restored at approximately 2026-07-21 08:10 EDT after its qualifying
Raymond pair completed. The pre-wide binary, standard plugins, and
configuration came from
`/var/backups/pizzawave/collapse-wide-20260720T233600Z-rpi`; all completed IQ
and JSON evidence was preserved. The restored binary SHA-256 is
`15824b7f8083d4b824d10d6e06a22b81a75eca93b4a7b8aa8499e76d299e0449`
and `libcallstream.so` is
`31ac526d66664e4fed8d0a43acf0549dac12ad6a35ec38b25704b53e1fc31450`.
The three wide-capture settings are absent, while the bounded 30/60-second
narrow recorder remains. TR was active as PID 421296 with zero automatic
restarts; PizzaWave and live TR activity were healthy.

## Repeatable offline analysis and mitigation direction

### Hardware-path correction

OT uses RTL-SDR receivers, not Airspy receivers. Its antenna feed passes through
an MCA208M active multicoupler before the receivers. The MCA208M provides a
broadband LNA and input protection, but its standard filter is a 25 MHz
high-pass filter across a nominal 25 MHz-to-1 GHz receive path, not a narrow
700/800 MHz preselector. A BPF-800-M band-pass filter is available but is not
installed on OT. RPI instead uses two Airspy receivers and no MCA208M. The
common collapse signature therefore spans different tuner and front-end
hardware; neither the multicoupler nor one receiver model is a common cause.

`scripts/analyze_p25_collapse_iq.py` now turns a completed paired-wide capture
into a reproducible JSON result. It validates sample count and narrow/wide
pair identity, aligns one-second power measurements with the decoder timeline,
checks sample integrity and neighboring 12.5 kHz bands, and applies an
explicitly bounded broadband-versus-channel-local classification. It does not
claim that power-spectrum measurements can distinguish multipath from an
exactly co-channel interferer.

Validation against Raymond group `1784609251021` reproduced the manual result:
25,714,285 wide samples, a complete matching pair, no invalid, zero, repeated,
or clipped samples, and low-minus-baseline changes of -0.14 dB in raw power,
-0.25 dB in control-channel/outer-band ratio, and -0.18 dB in the outer-band
floor. It found no neighboring-band change and classified the event as
`channel_local_modulation_impairment`.

The next implementation should use the retained IQ, not another antenna or
another live wide recorder:

1. Expose the existing OP25 Gardner timing `quality()` value and bounded
   carrier/timing error statistics to an offline replay result, alongside valid
   P25 frame count and acquisition latency.
2. Replay each retained event through the unchanged decoder and a small set of
   same-IQ candidates: alternate carrier/timing loop parameters and an adaptive
   complex equalizer at the channelizer/demodulator boundary. Score healthy and
   collapsed intervals separately.
3. Accept an equalizer or loop change only if it increases valid frames during
   all retained collapse intervals without materially reducing healthy-window
   decode. This is the direct no-new-antenna mitigation for time-varying
   simulcast/multipath distortion.
4. If no single parameterization wins, test two passive demodulators fed from
   the same channelized IQ and select only CRC/frame-valid P25 messages at one
   ownership boundary. Do not feed duplicate messages into live trunking state.

This sequence tests the leading mechanism and a remedy together. Recovery
grace, control-channel ranking, and graph reset remain secondary safeguards;
they do not explain or correct the initial modulation collapse.

## RPI Airspy gain experiment

RPI produced another complete natural Raymond narrow capture at 2026-07-21
08:11:32 EDT, before any gain change. Capture `1784635892019` contains the full
30/60-second window: 8,709,676 complex samples at 96,774.194 samples/sec and
69,677,408 bytes. Its live and fixed-primary shadow rates moved together from
as high as 27 frames/sec to 1 at trigger and later to zero. Stable SHA-256
values are:

- IQ: `ff33c4ae99fb6ecac1ce8ee58dd1687493ebb9ddc07459b5770611c6ffc75831`;
- JSON: `fd6ee510f7eb0e98998fa1b3c4d388380a6ea925043b8411a82f968462d7f3a3`.

Raymond remained in a sustained zero-decode cycle at the configured maximum
Airspy LNA gain of 15. This allowed a bounded recovery test against the same
live outage:

1. An unchanged gain-15 TR restart briefly produced 2-4 frames/sec, then
   returned to the zero-decode cycle; restart alone did not restore normal
   service.
2. Only Raymond's source was changed to LNA gain 12. The separate 855 MHz
   Airspy remained at gain 15. Raymond produced zero frames/sec throughout the
   first observed minute.
3. Raymond was then changed to LNA gain 9, six decibels below the original
   setting. It again produced zero frames/sec throughout the observed window.
4. The exact gain-15 configuration was restored from
   `/var/backups/pizzawave/rpi-airspy-gain-20260721T0835EDT`. Its SHA-256 is
   `6bcb77f651fd76c6036528275f3ec088cf0d56a4667b169d0f47536260facc02`.
   TR was active as PID 430741 with zero automatic restarts and PizzaWave live
   TR telemetry was current.

Reducing front-end gain by three or six decibels did not cure an established
collapse. This argues against a simple gain-compression condition that remains
recoverable merely by backing off the Airspy LNA.

The separate prevention test began only after Raymond had naturally returned
to the 773.781250 MHz primary and produced eleven consecutive samples between
13 and 26 frames/sec. At 2026-07-21 08:46:14 EDT (Unix ms
`1784637974086`), only Raymond's LNA gain changed from 15 to 12. Source 1
remained at gain 15. The gain-12 config SHA-256 was
`69678b83fd206daa98c016b7e92cce20c440b9c6d21dbd3f9bcaedc0997492e6`,
TR started as PID 433591 with zero automatic restarts, and startup logs
confirmed gains 12 and 15.

Gain 12 immediately reduced primary decode to 2-4 frames/sec and repeatedly
forced alternate-channel cycling. Raymond then spent long intervals at zero.
At the five-minute boundary it had recovered only to 7 frames/sec, not the
required three consecutive samples at or above 10. After the boundary it
briefly produced intermittent 9-11 frame/sec samples, but did not return to
the pre-test baseline. This made gain 12 unsuitable for an overnight
prevention test.

The exact healthy gain-15 config was restored at 08:52:18 EDT (Unix ms
`1784638338341`) from
`/var/backups/pizzawave/rpi-airspy-gain-20260721T0845EDT/config.gain15-healthy.json`.
Its SHA-256 is
`6bcb77f651fd76c6036528275f3ec088cf0d56a4667b169d0f47536260facc02`.
Both startup gain reports were 15. The new process, PID 435796 with zero
automatic restarts, stayed on the primary and produced 9, 23, 24, 28, 24, 30,
14, 15, and 16 frames/sec in its first nine samples. PizzaWave live activity
was current.

This prevention attempt does not prove that gain 15 can never overload, because
the RF path varies with time and the rollback included a process restart. It
does show that a three-step LNA reduction costs too much Raymond margin under
otherwise healthy conditions. Gain 12 and gain 9 should not be used as the
standing configuration.

### Gain-14 prevention result

The final bounded gain test changed only Raymond from LNA gain 15 to 14 at
2026-07-21 08:56:58 EDT (Unix ms `1784638618508`). Source 1 remained at gain
15. Gain 14 passed the healthy-window gate: it stayed on the 773.781250 MHz
primary without retuning and sustained approximately 11-32 frames/sec for five
minutes, with the final minutes mostly between 16 and 32. The active config
SHA-256 was
`651e37dd6806832afbe46a6932efcd75dcd176024ba7a18fc6374c4c431ce0ba`;
TR ran as PID 437761 with zero automatic restarts.

A natural Raymond collapse began at 09:14:17 EDT, approximately 17 minutes
after gain-14 activation. Capture `1784639657025` completed at 09:15:17 EDT
and retained 69,677,408 bytes of narrow IQ. Stable SHA-256 values are:

- IQ: `3ca8e02039960886e49f1bffd4488ee2fc15bf030ac5144248e9d56ebe1247cb`;
- JSON: `f688faf2359b950371acdfeec30675362ecd2f5f8328ada7ea24a5c2d518690d`.

The gain-14 onset reproduced the established signature. Live and shadow fell
together on the unchanged primary; trigger metadata was 1/3 frames/sec and the
trigger-aligned timeline sample was 3/3. Narrow IQ onset power was only 0.02 dB
below the capture's pre-event median. There were no non-finite, zero,
repeated-adjacent, or clipped samples. This is modulation failure with retained
channel energy, not a broadband front-end or sample-delivery collapse.

Recovery was not better than the gain-15 controls. The fixed-primary shadow
briefly reached 12, 30, and 29 frames/sec about five to seven seconds after
trigger while live cycled alternates. Live then spent 56 post-trigger timeline
samples at 0-1 frames/sec, performed 20 control-channel retunes, and never
produced three consecutive samples at or above 10 within the retained minute.
For comparison, gain-15 capture `1784635892019` spent 31 post-trigger samples
at 0-1, recovered three consecutive live samples at or above 10 after about ten
seconds, and contained eight retunes. Gain-15 paired capture `1784609251021`
spent 45 samples at 0-1 and contained 14 retunes. One natural event cannot show
that gain 14 caused the greater severity, but it does show that gain 14 did not
prevent or materially weaken the collapse.

The exact gain-15 config was restored at 09:17:29 EDT (Unix ms
`1784639849005`) from
`/var/backups/pizzawave/rpi-airspy-gain14-20260721T0857EDT/config.gain15-healthy.json`.
Its SHA-256 is
`6bcb77f651fd76c6036528275f3ec088cf0d56a4667b169d0f47536260facc02`.
Both startup gain reports were 15. TR was active as PID 445238 with zero
automatic restarts, PizzaWave live activity was current, and Raymond sustained
26-40 frames/sec on the primary during verification.

The practical gain conclusion is therefore bounded but decisive: lowering the
Airspy LNA is not the stabilization fix. Gain 12 destroys healthy margin, gain
9 cannot recover an outage, and gain 14 still admits the same natural collapse
class. Keep RPI at gain 15 and stop gain A/B tests. The next experiment is the
offline retained-IQ demodulator/equalizer comparison described above; it tests
whether the distorted symbols can be recovered without another antenna or
another live RF-policy experiment.

An earlier Raymond automatic file at 15:36:20 EDT came from a process replaced
during deployment correction. It remains useful corroborating evidence but is
not the primary result above. Automatic quota is process-local, which explains
why both files exist despite `collapseCaptureMaxEvents: 1`.

## July 22 RPI follow-up: symbol damage, frequency reuse, and weather

The following analysis used read-only RPI telemetry and retained IQ. RPI stayed
on its exact gain-15 CQPSK baseline: TR PID `694645`, zero restarts, binary
SHA-256 `15824b7f8083d4b824d10d6e06a22b81a75eca93b4a7b8aa8499e76d299e0449`,
and config SHA-256
`6bcb77f651fd76c6036528275f3ec088cf0d56a4667b169d0f47536260facc02`.
No live configuration or service was changed.

### Last-24-hour event shape

Raymond entered the large degradation at 2026-07-21 21:13:01 EDT. Three
consecutive 15-second samples were at or below 3 frames/sec and the process did
not produce three consecutive samples at or above 10 until 07:02:57 EDT. It
relapsed one minute later for another 40.5 minutes, then again from 07:51:12 to
08:18:42 EDT. The stable high-rate plateau began around 08:18 EDT. This is not
one binary eleven-hour outage; it is one long severe episode followed by two
shorter relapses during recovery.

The severe retained capture `1784687418024`, triggered at 22:30:18 EDT, has IQ
SHA-256 `025396f7ed22670ef6e8bd82bf24ffcb18b33f255df9f2c79bf891cd9c4cc0d6`.
Across 145 one-second windows from six Raymond captures, 63 healthy
fixed-primary windows at or above 15 frames/sec were compared with 82 failed
windows at or below 1 frame/sec:

| Metric | Healthy median | Failed median | Change |
| --- | ---: | ---: | ---: |
| Differential phase error | 14.30 degrees | 19.02 degrees | +33% |
| Differential amplitude variation | 0.253 | 0.335 | +32% |
| Exact P25 frame-sync detections | 12/sec | 6/sec | -50% |
| Carrier-bias estimate | 419.9 Hz | 421.3 Hz | effectively unchanged |

Broad autocorrelation weakened during failure at delays from roughly 31 to 124
microseconds, but no repeatable single echo emerged. The failed-versus-healthy
spectral-ripple delay proxy was only 0.531 dB, close to the 0.337 dB
healthy-versus-healthy null, and its peak-to-median ratio was not stronger than
the null. The samples therefore show symbol/modulation damage but do not prove
one stable delayed simulcast path. A changing collection of paths or an exact
co-channel signal remains consistent with the evidence.

### Exact-frequency reuse is a concrete candidate

The [national 700 MHz channel table](https://www.caprad.org/NlectcRm/Plans/docs/700_NB_channel_centers-finalversion%20wInterop%20ch%20names%20updated%209-15-15%20%282%29%20r1.pdf)
classifies 773.781250 MHz as a state-license channel, so FCC site search is not
a complete inventory of transmitters using it. The current
[MSWIN site table](https://www.radioreference.com/db/sid/4879)
lists the same four Raymond control-capable frequencies at other MSWIN sites:

| Site | Distance from ETV Raymond | NAC | 773.781250 MHz |
| --- | ---: | ---: | --- |
| ETV Raymond | 0 miles | `2A4` | control capable |
| West, Holmes County | 79.7 miles | `2A2` | control capable |
| Ashcroft, Monroe County | 173.1 miles | `2A0` | control capable |

The focused [West site record](https://www.radioreference.com/db/site/20524)
confirms that 773.031250, 773.281250, 773.531250, and 773.781250 MHz are all
control capable there. This means control-channel rotation within Raymond's
existing list cannot avoid West when the two sites choose the same member.

The severe IQ was replayed through the validated fixed-primary CQPSK decoder
with no expected WACN, system ID, or NAC configured. Config SHA-256 was
`3acbfe3692577dff3cdc736afc805cec39f5eed5f73aef54792ef0342a51b7f5` and log
SHA-256 was
`b2843f2df2d6b592be88855d49ce38a4f37efb9f325f01d4d3c47907ef7ceb8b`.
The replay reproduced the failure, mostly 0-4 frames/sec with a brief peak at
6, and decoded only Raymond's `BEE00 / 2AD / 2A4` identity. It did not decode
West's `2A2` NAC or another system identity.

That negative result rules out a second strong, independently decodable P25
control channel in this capture. It does not rule out a weaker exact-frequency
West signal: two overlapping P25 signals can destroy each other's symbols
while only the stronger Raymond frames occasionally pass CRC and identity
checks. The new bounded conclusion is therefore that dynamic same-network
co-channel interference is now at least as plausible as diffuse simulcast
multipath for Raymond.

### OT exact-frequency reuse and rejected-network-ID replay

The same discriminator was applied to OT before assuming that Raymond's
co-channel candidate explains both locations. The current TACN records identify
real reuse of North Bradley's control channels:

| Site | Distance from North Bradley | NAC | Relevant reuse |
| --- | ---: | ---: | --- |
| [North Bradley](https://www.radioreference.com/db/site/16061) | 0 miles | `2AD` | 769.606250 and 770.531250 MHz controls |
| [Sharps Ridge](https://www.radioreference.com/db/site/15787) | 69.4 miles | `2A3` | both North Bradley controls |
| [Arnold AFB](https://www.radioreference.com/db/site/46243) | 74.7 miles | `2A0` | 769.606250 MHz control |
| [West Point](https://www.radioreference.com/db/site/24701) | 154.9 miles | `2A9` | 769.606250 MHz control |
| [Bethel Springs](https://www.radioreference.com/db/site/22202) | 209.5 miles | `2AA` | 769.606250 MHz control |

Sharps Ridge is the important candidate because it reuses both configured
North Bradley controls. This makes co-channel interference physically possible
at North Bradley, although the records alone do not show that its signal
reached OT during an event.

Hamilton is different. [Chattanooga Simulcast](https://www.radioreference.com/db/site/15541)
uses NAC `2A0` and 855.212500 MHz as a control. No nearby continuous control
site using that exact frequency was identified in the current regional records.
The nearest identified reuse was only as a traffic frequency: the
[Talladega site](https://www.radioreference.com/db/site/35849), NAC `1D0`, at
134.9 miles and [Troup County](https://www.radioreference.com/db/site/45804),
NAC `C41`, at 139.2 miles. Their control channels are on other frequencies, so
they are much weaker explanations for Hamilton's long control-channel events.

Four retained OT files were then replayed with an exact-lineage, offline-only
Trunk Recorder binary instrumented to log P25 network-ID codewords rejected by
BCH correction. No expected WACN, system ID, or NAC was configured. The
instrumented binary SHA-256 was
`74dafd9f6b58d6b7950203f3bd27df4976ed176027e7aebbc11188e25d836555`;
it was never installed or merged into the live service.

| Capture | Rejected NIDs | Exact local NAC | Within 2 bits of local | Exact plausible foreign NAC | Valid identity |
| --- | ---: | ---: | ---: | ---: | --- |
| North Bradley `1784584105012` | 1 | 0 | 1 | 0 | only `BEE00 / 2A5 / 2AD`, site 2-26 |
| North Bradley `1784590157013` | 7 | 3 | 6 | 0 | only `BEE00 / 2A5 / 2AD`, site 2-26 |
| Hamilton `1784598296012` | 77 | 29 | 61 | 0 | only `BEE00 / 2A5 / 2A0`, site 2-10 |
| Hamilton `1784619531022` | 104 | 40 | 83 | 0 | only `BEE00 / 2A5 / 2A0`, site 2-10 |

For North Bradley, none of the rejected words exactly matched Sharps Ridge
`2A3`, Arnold `2A0`, West Point `2A9`, or Bethel Springs `2AA`. Near matches to
West Point are not diagnostic because local `2AD` and `2A9` already differ by
one bit. For Hamilton, neither event had an exact or one-bit match to Talladega
`1D0` or Troup `C41`; one of 181 rejected words was within two bits of Troup,
which is not meaningful without an exact NAC or foreign site identity.

The replay therefore finds no positive evidence of a second independently
identifiable P25 signal at OT. It cannot exclude a weaker overlapping signal
that destroys symbols before its own identity can survive. The bounded result
is nevertheless asymmetric: exact-frequency interference remains a credible
North Bradley alternative because nearby control reuse exists, while Hamilton
has neither a strong reuse candidate nor foreign identity evidence. Combined
with Hamilton's stable outer-band power, increased in-channel energy, and lost
decode, dynamic simulcast/multipath modulation destruction remains the primary
OT explanation.

### July 22 sounding comparison

Surface observations still show an association: the 120 half-hour RF buckets
from July 19-22 had lower decode with high humidity, zero wind, and small
temperature/dew-point spread. Rate correlations were -0.403 with relative
humidity, +0.394 with dew-point spread, and +0.379 with wind. Nighttime decode
averaged 12.50 frames/sec with 48.3% zero samples versus 25.28 and 15.5% during
the day. These are associations with time of day, not proof of propagation.

The official [NWS latest JAN sounding page](https://www.weather.gov/bmx/latestjansounding)
and underlying SPC observed soundings provide a sharper test for this event:

| KJAN sounding | Relation to RF event | Lowest low-layer M gradient | Temperature behavior |
| --- | --- | ---: | --- |
| July 22 00Z | about 1 hour before sustained onset | -65.0 M/km over 91-181 m | temperature fell 0.6 C; no inversion |
| July 22 12Z | during final recovery | -331.5 M/km over the 91-117 m surface layer | temperature rose 1.8 C |

The strong shallow inversion was observed during recovery, while the sounding
nearest onset lacked one. That contradicts a simple explanation in which a
stable nocturnal duct alone turns the outage on and its disappearance turns it
off. Weather may still change the strength of a distant co-channel site between
soundings, but the July 22 profile does not establish that mechanism.

### Durable onset and recovery evidence

PizzaWave now derives confirmed RF episodes from its existing 15-second
`rf_sample` stream; no new collector or Trunk Recorder change was added. An
episode begins after three consecutive samples at or below 3 frames/sec and
recovers after three consecutive samples at or above 10. The authenticated
`/api/v1/system/rf/telemetry-summary` response now includes onset and recovery
timestamps, decode rate, control frequency, frequency error, minimum and
average rate, sample count, duration, and whether recovery was observed. This
automatically preserves both edges for the next retained-IQ capture while
filtering one-sample retunes.

### July 22 restart-boundary captures and persistent rearm

A controlled restart rearmed the original process-lifetime capture quota on
both hosts. It immediately retained two useful events:

| Host/system | Trigger | IQ SHA-256 | Result |
| --- | ---: | --- | --- |
| OT North Bradley | `1784730613018` | `022a960a3ab2fa833281030f978961e2f270fbc637fafa4102e9ea54ba689869` | Live and fixed-primary shadow decode fell together while Hamilton and Cleveland stayed healthy. The shortened startup capture had clean samples and only a 1.40 dB narrow-power difference between low and healthy pre-trigger seconds. |
| RPI Raymond | `1784730680020` | `841ce737467a693a1a6a1997c23dbf265cef0572e4a0fa36abba3cce5c6c893b` | An unchanged restart did not restore the sustained collapse. The 90-second capture had clean samples, a median live rate of 1 frame/sec, 77 low-rate seconds, and 24 live control-frequency changes. |

The first timer-based rearm candidate then retained a cleaner healthy-to-failed
edge on each host:

| Host/system | Trigger | IQ SHA-256 | Onset result |
| --- | ---: | --- | --- |
| OT North Bradley | `1784732498025` | `288a628bd22972d09be27416799b4adf75b7cc68468e9f51936d9853b8abd7c7` | Both decoders fell to zero with only a 0.16 dB pre-trigger narrow-power change, then recovered. |
| RPI Raymond | `1784732675023` | `4ed6805c31b8b25986ccd665dca8735fc50e8fcea3fa4cf3b825b6f20e3d48a4` | Both decoders fell while narrow-channel power dropped 5.09 dB between healthy and low pre-trigger seconds. |

These edges show that the two rigs reach the same decoder-failure state through
more than one RF shape. OT again shows modulation destruction with essentially
unchanged received channel power. Raymond can also experience a real received
control-channel fade. Neither capture contained nonfinite, zero, or repeated
adjacent samples, so neither edge is an SDR sample-delivery artifact.

A second complete North Bradley event at `1784732918021` was initially omitted
from this record. Its IQ file is 69,120,000 bytes with SHA-256
`1d013a8bd266f84eaaee529a5383e828fd411ad76ad1dfdfd08a93e84638b454`;
its JSON SHA-256 is
`d0d98a4a6f7c871de7483d275dd497b6ce937d3bb0d4c0e7638f5d4e056dfbc4`
and `completedUnixMs` is `1784732978271`. Both decoders fell on the fixed
769.606250 MHz primary. Low-window narrow power was 2.47 dB below the healthy
pre-trigger window, Hamilton and Cleveland stayed healthy, and samples were
clean. Live recovered quickly after retuning while the fixed-primary shadow
remained near 1 frame/sec. This is another site-local North Bradley impairment,
not a host-wide or sample-delivery failure.

The first rearm implementation exposed a quota flaw. OT began an incomplete
Hamilton capture at `1784732621084`; North Bradley retune/Gardner scheduler
errors had already begun about seven seconds earlier, and source 3 stopped
delivering samples 21 seconds after the capture began. Systemd restarted TR,
which reset the in-memory quota and allowed another North Bradley capture at
`1784732699026`. The capture did not initiate the scheduler error, but the
restart proved that a process-local timer could permit a capture storm.

The revised isolated TR candidates therefore reconstruct recent automatic
capture history from both completed JSON/IQ pairs and interrupted IQ-only files
in `collapseCaptureDir`. With `collapseCaptureMaxEvents: 1` and
`collapseCaptureQuotaResetSeconds: 21600`, service restarts, deployments, and
reboots no longer rearm the recorder. A new capture becomes eligible after six
hours and only after a fresh healthy boundary. On OT startup the revised build
restored four North Bradley captures and the interrupted Hamilton capture from
disk, created no new file, and held quota as intended. The feature branches
remain separate from upstream TR master: current-lineage OT commit `7e03a80e`
and exact older RPI-lineage commit `2f6ca268`.
The RPI build subsequently restored two recent Raymond captures from disk,
created no new file, and kept TR/PizzaWave healthy at zero restarts. Installed
executable SHA-256 values are
`8141e35ea4be28ba15036755b7287922675e051aaacee8d76fb6549e8f9a9d72`
on OT and
`85e0ee88a86fcbc1327f01953ff952bf2d0c6f7023ba977dfefed90eca29ca8b`
on RPI.

### July 22 post-quota paired captures

The restart-persistent six-hour quota reopened at 17:03-17:08 EDT. The first
eligible natural event on each rig was retained without a service restart:

| Host/system | Trigger | IQ SHA-256 | Decoder and IQ result |
| --- | ---: | --- | --- |
| RPI Raymond | `1784754504018` | `1d679c505fb92060bf8c9f3b28f4f02947f2253cea6226b3fdace81ea1368b86` | Decode fell during a short 6-10 dB received-power dip. Power had already recovered by the trigger, the fixed-primary shadow immediately returned to 27-42 frames/sec, and live alternate-channel cycling extended recovery to about 11 seconds. |
| OT North Bradley | `1784754546018` | `d422e04801f2398fd43c9b105d3f78c415b4d04bc0143105de46f9ff0c2f34ae` | Both decoders fell while in-channel energy was 2.34 dB below the earlier healthy baseline and 12.5-45 kHz outer-band energy changed only +0.03 dB. Live recovered after retune/reset; the fixed-primary shadow remained near 1 frame/sec. |

Both files were complete and stable on two later checks. Raymond's file was
69,677,408 bytes and North Bradley's was 69,120,000 bytes. Neither contained
nonfinite, zero, or repeated adjacent complex samples. Their JSON SHA-256
values were respectively
`0ba96ac83e2ebacd734c74718800157d38d311f9cfdf2bf882ce1071268a10d0`
and
`57f3e7ea722483efadcede98969dff00a6cc3293aac31d2ae3780dcaf4998ac3`.

Raymond's long-window onset average is misleading because it straddles the
rapid recovery. One-second power moved from -62.69 dB at trigger minus 11
seconds to -74.47 dB at minus 7 seconds; decode then stayed at 1 frame/sec
through minus 1 second. At the trigger, power was back to -64.68 dB and the
fixed-primary decoder recovered one second later. This independently confirms
the earlier Raymond fade signature and shows that live control-channel hunting,
not continued RF loss, creates most of the visible outage tail.

North Bradley was site-local. During its event Hamilton decoded at 32-39
frames/sec and Cleveland at 39-41 frames/sec on the same host. The unchanged
outer-band power, clean samples, and healthy peer receivers reject a broadband
front-end, USB, scheduler, or host-load failure. Together with the earlier
0.16 dB onset event, North Bradley has more than one amplitude signature:
dynamic phase/modulation geometry can destroy decode with nearly unchanged
power or with a modest channel-only cancellation. Simulcast multipath remains
the best single explanation; exact-frequency co-channel interference remains
a North Bradley-specific alternative without positive foreign-NAC evidence.

The triggers were 42 seconds apart, but that is not evidence of a shared
geographic interferer. Both systems had frequent short blips, and the recorder
selected the first qualifying event after two independently expiring quota
windows. The RF signatures and recovery behavior are different.

### Updated conclusion and next steps

The likely cause set is narrower, but it is not one identical propagation event
at both sites:

1. A real sample-common RF/P25 modulation impairment starts each collapse. It
   is not queue accounting, antenna mistuning, gain, frequency centering, or
   invalid SDR samples. OT can fail at nearly unchanged power; two independent
   Raymond edges now show a several-decibel received-channel fade.
2. Raymond's most likely cause is a short frequency-selective fade or
   cancellation. A distant MSWIN site reusing the exact control frequencies is
   a concrete physical source, so changing co-channel geometry remains a
   plausible mechanism alongside local multipath. The fixed-primary decoder's
   immediate recovery after power returns makes a persistent decoder failure
   unlikely as the initial cause.
3. OT does not positively corroborate that co-channel mechanism. North Bradley
   has plausible exact-control reuse but no foreign identity in retained IQ;
   Hamilton has no identified nearby continuous-control reuse and strongly
   favors changing simulcast/multipath geometry.
4. The current CQPSK decoder and alternate-frequency cycling can lengthen the
   impairment, but neither creates the initial edge. OT's fixed-primary shadow
   can also remain stuck after the live decoder recovers, so shadow duration is
   not a direct measurement of physical fade duration.

Next:

1. Keep both rigs at their verified baseline RF and decoder settings. Let the
   persistent recorder take at most one automatic capture per system per six
   hours; do not restart TR to rearm it.
2. Treat Raymond's initial edge as a physical fade. The least-invasive hardware
   test is to move the existing antenna or feed point by roughly one-half to one
   wavelength at 770 MHz (about 8-15 inches), without adding an antenna or
   changing gain, then compare episode rate and depth over a full day. A small
   position change is the standard way to move a fixed receiver out of a
   multipath cancellation null.
3. As a secondary discriminator, examine failed or near-valid Raymond P25
   network-ID words for NAC `2A2` (West), `2A0` (Ashcroft), and `2A4`
   (Raymond), including candidates that fail the full message CRC. Correlate the
   episode with the actual control channel in use at West if
   an independent MSWIN status source is available. A matching West control
   channel during Raymond failure would be much stronger evidence than weather
   correlation.
4. For OT, do not add another antenna and do not use the BPF-800-M as the next
   test. The antenna is an in-band PCTEL MFBW7463 mounted about 20 feet high on
   a PVC mast, roughly 16 inches from the metal building, with its complete
   radiator above the roofline. Randomly moving that installation is not the
   next controlled discriminator. First run the two-stage Ventax experiment:
   compare independent decoders against the exact same retained IQ, then, only
   if justified, compare a separate RTL-SDR and USB path from another MCA208M
   output. The authoritative runbook is
   [2026-07-24-ot-ventax-decoder-isolation-handoff.md](2026-07-24-ot-ventax-decoder-isolation-handoff.md).
   Keep co-channel work secondary unless a nonlocal NAC or site identity
   appears.
5. Do not prioritize the BPF-800-M as a root-cause test. An in-band P25
   signal at the exact same frequency will pass that filter; it remains useful
   only as a final check for unrelated out-of-band front-end stress.

## Limits

The three paired events establish a common channel-local failure class, not a
direct measurement of propagation paths. They do not mathematically distinguish
simulcast multipath from an exactly co-channel interferer, nor estimate how often
each may occur. Replay applies another channelizer, so replay behavior alone is
not proof of decoder state. The next useful experiment must measure or diversify
demodulation quality; more centering, grace-period, or retune-policy A/B tests
would not identify the physical onset.
