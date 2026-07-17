# System Information Contract

The System workspace answers operational questions about the running PizzaWave
installation. It is not a general dump of implementation state. Each page
leads with a concise state, a small set of numeric headline facts, any warning
or action, and at most one compact supporting table. Raw and technical evidence
is available on demand. Do not repeat an explanatory framework above the page.

Status words are `Normal`, `Warning`, `Critical`, `Running`, `Stopped`,
`Unavailable`, and `Disabled`. Estimated, retained, live, and historical
evidence must be identified explicitly.

## Page Inventory

| Area | Page | Operator decision | Ownership boundary |
|---|---|---|---|
| Recommendations | Recommendations | What current problems, risks, or improvements deserve attention? | Cross-System summary with direct links; no Ignore, baseline reset, or mutation shortcuts |
| Runtime | Services | Are PizzaWave, Trunk Recorder, LM Studio or its completion route, vector search, and embeddings available? | One functional card per service; a local LMLink endpoint may relay elsewhere; systemd detail is collapsed |
| Runtime | Resources | Does the PizzaWave stack have compute, thermal, memory, and passive USB error headroom? | Passive kernel, `lsusb`, and sysfs evidence only; never probes or interrupts capture |
| Runtime | Queue | Is audio being ingested and processed as fast as it arrives? | Numeric ingest and audio-throughput evidence; ingest controls remain prominent and explicit |
| Runtime | Jobs | Which explicit background operations are running, failed, completed, or cancellable? | Continuous workers are not jobs; producers serialize conflicting work and declare only real safe-cancellation boundaries |
| Data | Storage | Is there enough space, what is growing, and has automatic maintenance succeeded? | No manual vacuum/optimize/prune controls; maintenance history is job-owned |
| Data | Backup and Restore | Can a complete archive be created, staged, verified, or restored safely? | Comprehensive recovery owns operator restore; bare TR config restore is unsupported |
| Data | Reset | Which deliberate reset scope is required? | Data, Site, and Full are mutually exclusive; backup is the safe default |
| Data | Audit History | Which consequential operator decision changed setup or policy? | Does not duplicate job completion history, maintenance, reads, or routine pipeline activity |
| Trunk Recorder | Summary | Which site, system, or source is decoding or recording poorly? | Current plus compact 24-hour evidence; source attribution is retained with telemetry |
| Trunk Recorder | Active Configuration | What exact TR configuration is applied? | Read-only operational summary; Setup owns edits |
| Trunk Recorder | Logs | What raw TR lines were emitted in an explicit time window? | Own selector, default last hour, pagination/search; no controls or interpreted verdicts |
| Performance | Calls | What traffic did PizzaWave capture? | Traffic only; RF and transcription evidence remain on their pages |
| Performance | Transcription | Are failures caused by pipeline, audio input, or transcript quality? | Causes remain visibly separate and link to the owning evidence |
| Performance | Radio Frequency | Is decode and capture performance stable by site, system, and source? | Own explicit scope/window; read-only; community target and local baseline are both visible |
| Performance | Incidents | How effectively did eligible evidence move through incident creation and association? | Uses recorded pipeline decisions, not superficial transcript-length heuristics |
| Performance | AI Usage | How much AI work ran, how reliably, and at what estimated cost? | Selected range, month, and all-time remain visible; endpoint provenance is collapsed |
| Performance | Bandwidth | How much PizzaWave-attributed payload data moved? | Excludes host/interface totals and protocol overhead; selected range, month, and all-time remain visible |

Performance has no Overview page. Recommendations is the cross-System summary.
Trunk Recorder configuration snapshots remain an internal rollback mechanism;
they are not an operator-facing recovery surface.

## Time Contract

- Label the shared top selector `Activity range`.
- Show it only on Dashboard, category pages, Calls, Transcription, Incidents,
  AI Usage, and Bandwidth.
- RF and Trunk Recorder Logs own explicit page selectors; hide the shared
  selector there. A page uses the shared selector or its own selector, never
  both.
- Recommendations uses fixed, named detection and history windows. Runtime,
  Data, Trunk Recorder Summary/Configuration, and Settings do not inherit the
  shared range.
- One Recommendations card represents one operator condition. Correlated
  counters are evidence within that card when they share the same affected
  scope, destination, and corrective decision; metric names do not create
  separate findings. Keep findings separate when ownership or action differs.
- Charts and tables show exact start, end, and timezone. Time-series charts use
  multiple adaptive X-axis labels and include the date when crossing midnight.

## Content And Layout Rules

- Every System subpage begins with the same compact identity band: a small
  theme-colored icon tile, the page title, and one plain-language purpose line.
  It establishes hierarchy without becoming a tall hero banner or duplicating
  the page's KPIs.
- Do not repeat a metric in an adjacent tile and table unless the table adds a
  materially different window or breakdown.
- Put normal technical detail behind disclosure. Preserve raw evidence where
  the page explicitly owns it, especially Trunk Recorder Logs.
- Keep Setup authoritative for source planning, RF validation, and applied TR
  changes. Keep Dashboard authoritative for live calls and incidents.
- Show units and measurement windows. Do not equate call rate with audio
  throughput; call rate is duration-blind.
- Describe LMLink and localhost endpoints as possible relays. Never infer model
  location from an endpoint address alone.
- Size tables, charts, and pagination to their content. Avoid squished tables,
  sparse full-width blocks, and containers whose controls overflow at desktop
  or narrow breakpoints.
- Performance / Transcription always exposes Endpoint Outage History, including
  an explicit empty state. Failed transcripts with retained audio create one
  medium Recommendations finding for a fixed 24-hour evidence window. Its
  direct link opens the collapsed Runtime / Jobs Recovery Tools. Recovery is an
  optional, resource-intensive explicit job, not a required banner action.
  When a selected outage or recovery window is empty, name the longer available
  windows so operators know older evidence may still exist; do not claim that
  older records exist without querying them.
- The selected Blue, Orange, Red, Purple, or Green theme owns interactive and
  informational accents throughout PizzaWave, including System identity bands.
  Semantic health/severity colors, category colors, and chart-series colors stay
  fixed so their meaning does not change with the cosmetic theme.
