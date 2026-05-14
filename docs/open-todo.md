# PizzaWave Open TODO

## Immediate Handoff Tasks

- Start the next Codex session rooted at `C:\projects\pizzawave`.
- Pull/checkout branch `codex/pizzawave-engine-cleanbreak`.
- Confirm latest checkpoint:

```powershell
git log -1 --oneline
```

Expected checkpoint: `a03516f Remove pizzalib and own call processing in pizzad`
or a later handoff/status commit.

- Confirm solution projects are only `pizzad` and `pizzad.Tests`.
- Re-run:

```powershell
npm run build --prefix C:\projects\pizzawave\pizzad\web
dotnet test C:\projects\pizzawave\pizzawave.sln --configuration Release
dotnet build C:\projects\pizzawave\pizzawave.sln --configuration Release
```

- The hard cleanup checkpoint has already been committed. Do not redo the
  migration unless a deployed stack was restored from a pre-migration backup.

## Near-Term Operational Follow-Ups

- Watch RPI transcription quality after the faster-whisper rollout.
  - Initial audit completed 2026-05-14: faster-whisper kept up with live traffic
    and reduced headline poor-quality rate versus the equal-length pre-rollout
    window.
  - Added `repetitive` and `numeric_noise` quality reasons after the audit found
    repeated-token hallucinations being marked complete.
  - Continue watching whether the new quality reasons reduce bad inputs to
    summaries/incidents without over-filtering useful short radio traffic.

- Continue monitoring RPI queue health using:
  - pending audio seconds;
  - queue depth;
  - recent audio seconds ingested/transcribed;
  - calls/min.

- Continue monitoring omicrontheta Hamilton retune/decode behavior after recent
  gain/config changes.
  - Use `System > Trunk Recorder > RF Analysis` after 48-72 hours of corrected
    post-change health rows.
  - Compare omicrontheta against RPI for whiteoakmt-hamilton using CC summary
    decode rate, low-decode warnings, retunes, and no-transmission outcomes.

- Revisit faster-whisper quality/performance only if RPI queue pressure returns.

- Watch omicrontheta transcription backlog after the 2026-05-14 deploy.
  - The service was healthy after deploy, but queue depth rose during downtime.
  - Let it drain before running large imports or manual AI backfills.

## Product/Code Follow-Ups

- Clean up and simplify the web UI.
  - The UI has accumulated experimental controls, diagnostics, and nice-to-have
    features.
  - Reorganize around the most important operator workflows and move secondary
    tools out of the primary path.

- Add unit and integration tests for the engine.
  - Basic tests now cover catalog import/generation, callstream parsing, and
    police-code detection.
  - Continue with transcription queue behavior, import guardrails, incident
    generation, settings validation, and dashboard API models.

- Continue watching category/name drift after the 2026-05-14 deployed migration.
  - Final migration dry-runs showed zero remaining changes on omicrontheta and
    RPI after deploying the catalog-based ingest build.
  - Re-run the audit only if a stack is restored from an older backup or a TR
    catalog is replaced outside the PizzaWave settings flow.

- Add an operator-facing catalog drift diagnostic.
  - It should compare stored call category/name values against the current JSON
    catalog and report potential changes without mutating the DB.
  - This replaces the disposable migration script used during the 2026-05-14
    migration.

- Expand documentation as features stabilize.
  - Keep docs PizzaWave-first.
  - Do not reintroduce `PizzaStack` branding.
  - Keep old app history out of the main docs; unsupported app source is
    available from git history.
