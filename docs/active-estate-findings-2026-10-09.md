# Current estate recommendation reporting

The estate board incorrectly highlighted dormant unresolved findings. Both OT's
RadioHealthSummary and SDR's source script included quiet findings in the three
exported slots; severity ordering could hide current medium findings behind old
critical findings. Require explicit active activity before selection, counting,
ordering and truncation. Keep the existing workflow/severity/RF exclusions,
dates, links, size limits, source expiry and reporting schedules.

Native finding history remains untouched. LastSeenUtc in the older persistence
path can advance during quiet reassessment; it must not be interpreted as proof
of a current failure. No history migration or persistence change is part of this
reporting correction. Only active findings now enter the estate alert feed.

Tests cover quiet exclusion and active selection ahead of the three-item cap.
Deploy OT through its normal verified backend helper; only pizzad needs restart.
Replace only the SDR source script after old/new SHA-256 checks, leaving its
15-minute timer, credentials, transport, byte limit and Trunk Recorder unchanged.
No new publisher, poll, credentials or dependency. HA adds a consumer filter
for older reports. Health contract review: dates and expiry remain source-owned;
the corrected list represents active problems, never whole-system health.

Activation: 30 RadioHealthSummary tests and eight SDR reporter tests passed.
OT normal helper verified package
91023ec7016b8fa923a349d9639efaafe92b99350987aa9ce6709d45013b2138,
restarted pizzad once and confirmed HTTP health. Installed assembly SHA-256
89b1f430e806e93ccfec7a38e639a6840031410d527798bd6b57066e5b07128f.
Trunk Recorder remained PID 2942209. Actual corrected Radio summary published
at 08:50:44 Eastern (1,077 bytes), with live pipeline metrics visible in HA.

SDR script prerequisite
1cf856f2d49d111c47445d72d1f5f11d437acf3a248086bc0dc9eb0740776cc9
matched. Replacement SHA-256
457d797d803366252157ddbefd2c8af4a3dc6f56d60b4cc53efd7fccdf5210ef
was checked before an atomic replacement in the existing directory. Initial
cross-filesystem rename was rejected without changing the script; same-directory
replacement succeeded. Source oneshot then completed successfully. SDR pizzad/TR
PIDs remained 1653/1659. Temporary deployment source removed after install.
Native recommendation history and persistence timestamps remain untouched.
