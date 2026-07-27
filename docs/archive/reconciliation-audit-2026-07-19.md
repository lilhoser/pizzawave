# Repository And Deployment Reconciliation

Audited: 2026-07-19 16:20 EDT

No branches, worktrees, host data, or deployments were changed during this
audit. All listed PizzaWave and Trunk Recorder worktrees were clean.

## Critical Findings

1. Local PizzaWave `main` is `99adf8a`, 50 commits ahead of
   `origin/main` (`4468224`). The completed Package 9, Package 10, Package 11,
   and RF recovery work therefore lacks a remote `main` copy.
2. RPI was deployed from local `main` during this audit. OT still matches
   `09bffda`, eight commits behind `main`; the hosts no longer run identical
   PizzaWave builds.
3. Seven pre-existing PizzaWave worktrees and three worktrees in the current
   Trunk Recorder checkout remain present. A separate older Trunk Recorder
   clone also exists.
4. Deploy manifests identify source-tree hashes but not the Git commit or
   deploying owner. Concurrent sessions can therefore overwrite one another
   without a clear source-control trail.

## PizzaWave Worktrees

| Worktree | Branch | Commit | Assessment |
| --- | --- | --- | --- |
| `pizzawave-package9-merge` | `main` | `99adf8a` | Authoritative local source |
| `pizzawave-rf-setup-fix` | `codex/rf-setup-recovery` | `09bffda` | Merged; eight commits behind main |
| `pizzawave-package10-recovery` | `codex/package10-recovery` | `8db00da` | Merged |
| `pizzawave-package9-temporal` | `codex/package9-temporal` | `7e75c43` | Merged |
| `pizzawave-package-redesigns` | `codex/package5-on-redesign` | `7ac518b` | Superseded by the Package 5 reapplication on main |
| `pizzawave-rf-degradation-diagnosis` | `codex/rf-degradation-diagnosis` | `d9907cf` | Patch-equivalent commits are on main |
| `pizzawave` | `codex/incident-v3-analysis` | `206400f` | Twenty unique experimental commits; deliberately separate |

The audit worktree `pizzawave-reconciliation` was created from `99adf8a` only
to record this report, as required by the repository worktree rule.

## Other Local And Remote Branches

- `codex/package9-temporal-pre-cleanup`: two commits are patch-equivalent to
  main and can be retired after review.
- `codex/package-redesigns`: four old Package 5 commits are superseded by the
  redesigned Package 5 commits on main; retain until cleanup approval.
- `codex/platform-refactor`: fourteen unique commits from an older proposed
  architecture. Do not merge wholesale into the current product.
- `origin/codex/v3-incident-pipeline-redesign`: twenty unique incident-v3 and
  related operational commits based on an old main line.
- `origin/codex/transcription-model-options`: the same twenty commits plus one
  transcription bakeoff checkpoint.
- `origin/codex/investigate-embedding-warnings-ot`: one unique embedding fix,
  also contained in the two branches above.
- Other remote feature branches contain no commits ahead of current local main.

These unique branches are retained, not lost, but require deliberate
cherry-pick/replay decisions. Their old base makes wholesale merging unsafe.

## Trunk Recorder Source

Current upstream-oriented worktree set:

| Worktree | Branch | Commit | Assessment |
| --- | --- | --- | --- |
| `trunk-recorder-upstream-inspect` | `master` | `382f5f2` | Upstream base used for current experiment |
| `trunk-recorder-retune-hysteresis` | `codex/retune-hysteresis` | `f893508` | One experimental grace-period commit |
| `trunk-recorder-retune-hysteresis-ot` | `codex/retune-hysteresis-ot` | `58b5b3c` | OT compatibility version of the experiment |

`C:\projects\trunk-recorder` is a separate clean clone at upstream commit
`6c00945`. Do not mix its branch state with the experimental worktree set.

## Verified Live State

Both services and live TR activity were healthy during the audit.

| Host | PizzaWave manifest | Trunk Recorder binary |
| --- | --- | --- |
| OT | `097c740a...` / `62e684c5...`, deployed 14:42 EDT | stock `/usr/local/bin/trunk-recorder`, SHA-256 `6502b8cc...`, dated 2026-04-29 |
| RPI | `96594b14...` / `23672a49...`, deployed 16:14 EDT | stock `/usr/local/bin/trunk-recorder`, SHA-256 `7363fcc3...`, dated 2026-05-04 |

The differing TR hashes are expected platform/build differences. Neither hash
matches an installed retune-grace trial artifact.

## Cleanup Gate

Do not remove any worktree or branch until:

1. local `main` is reviewed and safely published;
2. the unique incident/transcription/embedding/platform branches receive an
   explicit disposition;
3. OT and RPI deployed versions are checked before the next deployment.
