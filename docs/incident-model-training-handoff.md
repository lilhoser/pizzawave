# Local Incident Membership Model: Project Handoff

This is the implementation handoff for the next dedicated Codex session. Read
[`incident-pipeline-architecture.md`](incident-pipeline-architecture.md) first.
Do not reopen the completed product interview or treat archived experiment names
as active alternatives.

## Objective

Create a small local model that performs routine incident membership generation
for PizzaWave. Keep Qwen 3.6 35B-A3B for grounded titles, summaries, locations,
and difficult exceptions. The result must replace the current production
membership pipeline on OT and RPI without saturating shared Paxan inference.

In plain language, the small model learns which calls describe the same real
event. Qwen describes an already assembled event to operators.

## Preserved inputs

- Evidence archive:
  `C:\projects\pizzawave-incident-training-evidence-20260727.tar.gz`
- SHA-256:
  `33F1CD7C892482F53C691D518A57BA7706638B2E21BE4DFB04EE0B6DB6CBFFFB`
- Retired review/tooling source:
  Git tag `archive/incident-research-tooling-20260717` at `35937a8`
- Superseded platform source:
  Git tag `archive/platform-refactor-legacy` at `0cf4a82`
- Historical conclusions:
  [`archive/incident-pipeline-2026-07`](archive/incident-pipeline-2026-07/README.md)

The archive contains the sealed corpus source, audio package, human reviews,
model outputs, replay decisions, and transcript-free capacity traces. Large
disposable database clones and compiled outputs were deliberately excluded.

OT and RPI each retain a compressed copy of their small retired configuration
and replay evidence at
`/var/lib/pizzawave/archive/retired-experiments-20260727.tar.gz`. The OT SHA-256
is `651fa08118908c3bb10c5d0c3ad15e525c8cfc05baf30dc592af432ba6e05c68`; the
RPI SHA-256 is
`1e0855b4bdee6988f91a68016e3042c283338085efc57dcddc95b32e55835b11`.
Temporary database clones were excluded because their reusable evidence is in
the local archive.

## Roles and vocabulary

- A **teacher** is a capable model that drafts examples. Qwen is the primary
  teacher; GPT-OSS may provide an independent disagreement signal.
- A **student** is the smaller model learning the specialized task.
- **Distillation** means generating teacher examples, correcting them, and
  using them to teach the student.
- **Fine-tuning** is the training operation that changes the student's weights.
- **Classifier** describes the student's job. It does not imply a hand-written
  rule engine or that its output is only one label.
- **Silver** is useful model-produced data that is not independently proven.
- **Gold** is adjudicated human-reviewed data reserved for trusted evaluation.

## Single bounded work program

1. Inventory and deduplicate the preserved packages without looking at the
   sealed held-out outcomes while designing the training task.
2. Define one source-identity-safe output contract. It must not require the
   model to emit or recall IDs, indices, positions, hashes, or full transcripts.
   Prove the mapping and full-coverage behavior with focused tests before data
   generation.
3. Build the checkbox-oriented review web interface. Review actions must
   automatically produce labels for improper merges, missing members, splits,
   missed incidents, presentation defects, uncertainty, and approval.
4. Run a 50–100-package pilot. Measure median review time, disagreement,
   ambiguity, and reviewer consistency. Freeze the instructions and UI before
   scaling.
5. Generate independent Qwen and GPT-OSS drafts. Use agreement as silver data;
   route disagreement and high-impact cases to people. Do not let either model
   grade itself.
6. Collect approximately 600–1,000 corrected training packages and 300–400
   adjudicated gold packages within the roughly $2,000 budget. Parallel workers
   may be used after the pilot; duplicate review and adjudication are required
   for gold.
7. Select one small base model using fit-for-purpose constraints, not a broad
   bake-off: license, local runtime support, context needed by the frozen task,
   memory beside Qwen, structured-output reliability, and fine-tuning support.
8. Fine-tune once, evaluate on the sealed gold set, and report false merges,
   missed incidents, fragmentation, unresolved rate, identity/coverage errors,
   latency, tokens, memory, and repeatability. Show denominators and severe
   examples.
9. Proceed only if the frozen gates in the architecture document pass. Then run
   one non-mutating OT shadow and measure combined OT/RPI capacity.
10. If the shadow passes, use bounded OT canary, OT ownership, and RPI ownership
    with no-backfill fences and rollback. Do not restart Trunk Recorder.

## Review package

Each reviewer sees the teacher's complete proposed incidents next to playable
calls, transcripts, and extracted presentation. The common action is inspection
and correction, not constructing an incident from scratch.

Controls should include:

- approve package;
- remove a call from an incident;
- add an unassigned call;
- split or combine proposed incidents;
- mark a missed incident or non-incident;
- accept/correct title, summary, and location using selections where possible;
- mark insufficient evidence or defer.

The system converts these actions into training records and quality labels.
Reviewers do not need to know internal schemas or write prose rationales.

## Stop conditions

Stop and report rather than tail-chasing when:

- the source identity contract cannot resolve output reliably;
- pilot reviewers cannot reach acceptable agreement under stable instructions;
- the trained student repeats systematic false merges or singleton
  fragmentation on sealed gold;
- the exception workload would require routine operator babysitting;
- combined production-shaped demand cannot remain current with bounded backlog;
- progress would require changing the settled product requirements.

Model and hardware research after a passing deployment is a separate follow-up.
Qwen 35B on Paxan is the current presentation target; a hardware purchase is
not a prerequisite for beginning this project.
