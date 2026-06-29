# PizzaWave Testing Strategy

PizzaWave uses two test lanes. The goal is fast signal on every change without
pretending that slow or environment-heavy checks belong in the pull-request
gate.

## BVT Lane

The BVT lane runs on pull requests and pushes to `main` through
`.github/workflows/validate.yml`.

It should stay deterministic and cheap:

- restore and compile the .NET solution;
- build the React web UI;
- run unit tests that do not require live trunk-recorder, Qdrant, LM Studio,
  remote transcription, Nominatim, SDR hardware, or a browser;
- publish-check Linux x64 and Linux arm64 packages;
- compile-check the CUDA build profile.

Run the same local BVT core with:

```powershell
npm ci --prefix C:\projects\pizzawave\pizzad\web
npm run build --prefix C:\projects\pizzawave\pizzad\web
dotnet build C:\projects\pizzawave\pizzawave.sln --configuration Release -p:PIZZAD_SKIP_WEB_BUILD=true
dotnet test C:\projects\pizzawave\pizzad.Tests\pizzad.Tests.csproj --configuration Release --no-build --filter "Category!=Feature"
```

High-value BVT tests are pure unit tests for callstream parsing, settings
normalization, auth/write validation, profile policy, incident validation,
transcript quality gates, dashboard model shaping, and other code that can fail
without needing live services.

## Feature Lane

The feature lane runs through `.github/workflows/feature-tests.yml` on manual
dispatch and a daily schedule. It is intentionally not a PR blocker unless a
change is specifically touching that area.

Feature tests may start a temp in-process API host and use temp SQLite/config
state, but still avoid live production services. Current feature coverage
includes:

- API health and auth-init smoke checks;
- API 404 behavior for unknown `/api/*` routes;
- write-token enforcement;
- settings validation rejecting disabled transcription.

Good candidates for future feature tests:

- settings profile/talkgroup save flow with generated config artifacts;
- fake LLM/vector incident extraction using canned candidates and deterministic
  responses;
- dashboard API shape checks over seeded incident/call/location data;
- setup wizard validation against temp configs and fake command runners;
- a small Playwright smoke test that verifies dashboard/settings pages render
  without requiring real rig data.

Feature tests should still be bounded. Do not add tests that require real
radio traffic, live Qdrant, production LM Studio, deployed rigs, or long-running
backfills.
