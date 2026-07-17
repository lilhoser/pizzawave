# Incident Event-State Corpus Protocol

Date: 2026-07-17

Status: protocol draft to freeze before corpus extraction or model review

## Purpose

This protocol prevents the blank-slate incident experiment from inheriting the
unproven assumptions of V3 or overfitting to its documented failures. It
defines how source observations, development material, held-out material, and
human reference interpretations are created before proposer or critic output is
reviewed.

The primary corpus represents ordinary radio traffic. Previously reported V3
failures form a separate challenge corpus. A V3 decision, incident record,
label, or retrospective explanation is never treated as reference truth.

## Prohibited Selection Inputs

Primary-corpus selection must not inspect or filter on:

- existing incident membership, title, summary, or category;
- V3 plans, scores, action labels, rejection reasons, or audit outcomes;
- alert matches or application-derived call categories;
- talkgroup identity, talkgroup name, radio-system identity, or a maintained
  list of supposedly relevant sources;
- transcript words, regex matches, embeddings, model classifications, or
  manually assigned event labels;
- whether a call resembles a known failure.

Those fields may be preserved in an observation bundle with their origin
identified. They cannot determine whether the observation enters the primary
corpus.

## Source Freeze

1. Work from an immutable database and audio snapshot, not a changing live
   store.
2. Record a snapshot identifier, source database hash, audio-manifest hash,
   software revision, extraction configuration identity, and extraction time.
3. Inventory only timestamps, row availability, audio availability, and record
   integrity before selecting periods. Do not query incident or model-output
   tables during inventory.
4. Declare the eligible calendar range and any operational outages before
   model output is generated.
5. Exclude observations only when source material is missing or structurally
   invalid. Record every exclusion and its source-integrity reason.

## Primary Corpus Selection

The selection unit is a contiguous time block, not an individually interesting
call. This preserves ordinary context and allows reviewers to identify event
relationships without selecting on their meaning.

Before extraction, freeze the following parameters in a versioned manifest:

- eligible start and end timestamps;
- time-block duration;
- deterministic selection seed and algorithm identity;
- number of blocks;
- minimum separation between development and held-out blocks;
- rules for periods affected by recorder downtime or missing audio;
- the exact development/held-out split.

Select blocks deterministically from the eligible time range using only block
timestamps and source-availability facts. Export every call in each selected
block. Do not remove quiet, unrelated, repetitive, low-information, or
apparently non-incident calls after selection.

Development and held-out blocks must be separated by time sufficiently to avoid
sharing the same unfolding traffic. The held-out manifest and content hashes
are frozen before prototype results are reviewed. Held-out model output remains
sealed until the approach and evaluation gates are fixed.

## Challenge Corpus

Known or suspected V3 failures are useful as adversarial coverage, but they are
not evidence that the old diagnosis was correct.

- Store challenge observations in a distinct corpus and report their results
  separately from the primary held-out result.
- Select each challenge from source call and audio identifiers, then include
  surrounding context without importing the old incident decision.
- Hide the historical failure description from initial reviewers.
- Re-adjudicate the source evidence under the same open-world protocol as
  ordinary traffic.
- Retain disagreements between the new adjudication and the historical handoff
  as findings rather than forcing either account to win.
- Do not tune the architecture until every challenge passes. Improvements must
  generalize to the primary development corpus and are accepted or rejected by
  the predeclared held-out gates.

## Human Adjudication

At least two reviewers independently inspect each selected time block. A third
reviewer reconciles disagreements without erasing the original reviews.

Reviewers receive:

- call audio and transcript candidates;
- call timing and source-record radio metadata;
- explicit origin markers for any application-derived metadata;
- stable observation identifiers and tools for citing exact transcript text or
  audio intervals.

Reviewers do not receive existing incident records, V3 decisions, model output,
challenge labels, or another reviewer's interpretation during the independent
pass.

Each review is open-world and records:

- zero or more possible real-world event accounts in natural language;
- observation membership for each possible account;
- supported claims with exact provenance;
- plausible alternatives and contradictions;
- unresolved questions and missing evidence;
- uncertainty without collapsing it to incident/non-incident;
- possible continuation, merge, split, or identity change in descriptive
  language, without choosing from a fixed event or responder taxonomy.

The reconciliation record includes both source reviews, the reason for each
resolved choice, and any disagreement that remains. Reference data is
append-only; a correction supersedes an earlier record.

## Evaluation Freeze

Before opening held-out output, record:

- the proposer, critic, and projection model and prompt identities;
- decoding and context configuration;
- run count and randomness controls;
- primary membership, story-coherence, provenance, uncertainty, and
  contradiction measures;
- acceptable regression limits for ordinary traffic;
- how reviewer disagreement affects scoring;
- latency and cost reporting rules;
- the decision rule for rejecting an approach.

Report challenge performance, primary development performance, and primary
held-out performance independently. No weighted score may allow strong results
on known failures to conceal regressions on ordinary traffic.

## Required Inputs Before Extraction

Corpus extraction should not begin until the owner supplies or approves:

1. the immutable source database and audio snapshot;
2. the eligible calendar range and known recorder-outage periods;
3. the time-block and development/held-out split parameters;
4. the reviewer workflow and who can perform independent adjudication;
5. the storage location and handling requirements for audio-bearing corpus
   artifacts.

