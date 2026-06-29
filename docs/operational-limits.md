# Operational Limits

PizzaWave is designed to keep the radio pipeline durable first. Expensive work
such as transcription, AI summaries, incident generation, and imports must be
bounded so live call ingest does not become fragile.

## Queue Pressure

The status bar shows queue health. A healthy system should usually show a stable
or draining transcription queue. On small ARM systems, short bursts are normal;
continuous growth means the system is not keeping up.

Useful indicators:

- pending transcription count;
- recent ingest rate;
- recent transcription rate;
- estimated drain direction;
- dropped calls while ingest is paused.

The call count includes persisted calls, including calls that are pending,
failed, or quality-pruned. It is not the same as “valid transcriptions”.

## Transcription Throughput

Raspberry Pi-class hardware may not keep up with high daytime traffic using
larger Whisper models. Prefer:

- faster-whisper where supported;
- tiny/base models for live catch-up;
- larger fallback models only for failed or low-quality calls;
- low concurrency on constrained devices;
- pausing ingestion only as an explicit emergency action.

## AI Guardrails

AI summary and incident generation can be much more expensive than
transcription. PizzaWave limits AI work with `aiInsights` lookback/budget
settings.

Large historical imports should not trigger full-range AI summarization
automatically. Generate summaries only for intentional ranges.

## Import Limits

SFTP and local imports can create large transcription backlogs. The UI should
disable or warn on imports while queue pressure is high. Large imports should
estimate work before running and support cancellation.

## RF Health

Poor decode quality, retunes, recorder exhaustion, or bad source coverage can
increase call fragmentation and transcription failures. Use **System > Trunk
Recorder** and RF Analysis to compare:

- control-channel decode rate;
- decode-zero rate;
- retunes;
- no-transmission outcomes;
- update-not-grant counts;
- recorder/source coverage.

## Storage

Audio is stored indefinitely by default. Monitor `/var/lib/pizzawave/audio` and
database size. Retention policies are a future operational feature; do not rely
on automatic cleanup unless it is explicitly configured.
