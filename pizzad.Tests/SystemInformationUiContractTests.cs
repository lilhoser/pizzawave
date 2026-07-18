namespace pizzad.Tests;

public sealed class SystemInformationUiContractTests
{
    private static string AppSource() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "UiContract", "App.tsx"));

    private static string StyleSource() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "UiContract", "style.css"));

    private static string LocationHeatMapSource() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "UiContract", "LocationHeatMap.tsx"));

    private static string RecommendationServiceSource() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "UiContract", "SystemRecommendationService.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

    [Fact]
    public void PrimarySystemViewsDoNotRestoreKnownMisleadingOrDuplicatedContent()
    {
        var source = AppSource();

        Assert.DoesNotContain("Open AI metrics for usage details", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Open bandwidth metrics for the slow report", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Recount Storage", source, StringComparison.Ordinal);
        Assert.DoesNotContain("<SourcePlanTable", source, StringComparison.Ordinal);
        Assert.DoesNotContain("<SourceCoverageTable", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Show all {data.health.charts.length} charts", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CC Message-Rate Samples", source, StringComparison.Ordinal);
        Assert.DoesNotContain("function QualityAuditView", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DashboardMapIsAnOwnedLazyLoadedLeafletFeature()
    {
        var app = AppSource();
        var map = LocationHeatMapSource();

        Assert.Contains("React.lazy(() => import(\"./features/dashboard/LocationHeatMap\")", app, StringComparison.Ordinal);
        Assert.Contains("from \"react-leaflet\"", map, StringComparison.Ordinal);
        Assert.Contains("<MapContainer", map, StringComparison.Ordinal);
        Assert.Contains("<TileLayer", map, StringComparison.Ordinal);
        Assert.Contains("<CircleMarker", map, StringComparison.Ordinal);
        Assert.DoesNotContain("function mapTiles", app, StringComparison.Ordinal);
        Assert.DoesNotContain("function projectHeatPoint", app, StringComparison.Ordinal);
    }

    [Fact]
    public void SystemNavigationUsesApprovedOperatorTerminologyAndSurfaces()
    {
        var source = AppSource();

        Assert.Contains(">Recommendations{recommendations", source, StringComparison.Ordinal);
        Assert.Contains(">Runtime</button>", source, StringComparison.Ordinal);
        Assert.Contains(">Trunk Recorder</button>", source, StringComparison.Ordinal);
        Assert.DoesNotContain(">Resources</button>", source, StringComparison.Ordinal);
        Assert.DoesNotContain(">Processing</button>", source, StringComparison.Ordinal);
        Assert.DoesNotContain(">Receiver</button>", source, StringComparison.Ordinal);
        Assert.DoesNotContain(">Overview</button>", source, StringComparison.Ordinal);
        Assert.DoesNotContain(">Restore Config</button>", source, StringComparison.Ordinal);
        Assert.Contains(">Config</button>", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SystemWorkspaceOwnsResponsiveSizingAndLocalOverflow()
    {
        var app = AppSource();
        var styles = StyleSource();

        Assert.Contains("trouble-page system-view", app, StringComparison.Ordinal);
        Assert.Contains(".system-view .trouble-tabs", styles, StringComparison.Ordinal);
        Assert.Contains("flex-wrap: wrap", styles, StringComparison.Ordinal);
        Assert.Contains("repeat(auto-fill, minmax(min(100%, 200px), 280px))", styles, StringComparison.Ordinal);
        Assert.Contains(".service-resource-table", styles, StringComparison.Ordinal);
        Assert.Contains(".system-view .recommendation-list", styles, StringComparison.Ordinal);
        Assert.Contains(".system-view .dashboard-stats-panel > .chart-card", styles, StringComparison.Ordinal);
        Assert.Contains(".system-view .quality-audit > .card", styles, StringComparison.Ordinal);
        Assert.Contains(".system-view .finding-history .table", styles, StringComparison.Ordinal);
        Assert.Contains("@media (pointer: coarse)", styles, StringComparison.Ordinal);
        Assert.Contains(".system-view button", styles, StringComparison.Ordinal);
        Assert.Contains("min-height: 40px", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeUsesConsistentSummaryEvidenceAndSafeControls()
    {
        var source = AppSource();

        Assert.DoesNotContain("Stack Resource Use", source, StringComparison.Ordinal);
        Assert.Contains("currentResource(row.component)", source, StringComparison.Ordinal);
        Assert.Contains("resource.hostCpuPercent.toFixed(1)", source, StringComparison.Ordinal);
        Assert.Contains("<ResourceSummary snapshot={snapshot}", source, StringComparison.Ordinal);
        Assert.Contains("<ResourceEvidence snapshot={snapshot}", source, StringComparison.Ordinal);
        Assert.DoesNotContain("{snapshot.summary}", source, StringComparison.Ordinal);
        Assert.DoesNotContain("How resource measurements work", source, StringComparison.Ordinal);
        Assert.DoesNotContain("snapshot.insights.map", source, StringComparison.Ordinal);
        Assert.Contains("<Kpi label=\"Host CPU\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("<Kpi label=\"TR CPU\"", source, StringComparison.Ordinal);
        Assert.Contains("<Kpi label=\"1-Minute Load\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("latest.hostLoadHostPercent.toFixed(0)}% of host", source, StringComparison.Ordinal);
        Assert.Contains("USB Errors", source, StringComparison.Ordinal);
        Assert.Contains("usb?.evidencePeriod", source, StringComparison.Ordinal);
        Assert.Contains("open={(usb?.currentIssueCount ?? 0) > 0}", source, StringComparison.Ordinal);
        Assert.Contains("PizzaWave does not open, reset, probe, or detach USB devices", source, StringComparison.Ordinal);
        Assert.Contains("LM Studio / AI", source, StringComparison.Ordinal);
        Assert.Contains("/api/v1/system/resources/live", source, StringComparison.Ordinal);
        Assert.Contains("window.setInterval(() => void liveServiceResource.refresh(), 5000)", source, StringComparison.Ordinal);
        Assert.Contains("<ServiceResourceChart history={history}", source, StringComparison.Ordinal);
        Assert.DoesNotContain("<h3>Operator Readout</h3>", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Cancellable Now", source, StringComparison.Ordinal);
        Assert.Contains("producer owns a safe cancellation boundary", source, StringComparison.Ordinal);

        var jobsStart = source.IndexOf("function JobsPanel", StringComparison.Ordinal);
        var jobsEnd = source.IndexOf("function QueuePanel", jobsStart, StringComparison.Ordinal);
        var jobsSource = source[jobsStart..jobsEnd];
        Assert.Contains("<Kpi label=\"Running\"", jobsSource, StringComparison.Ordinal);
        Assert.Contains("<Kpi label=\"Waiting\"", jobsSource, StringComparison.Ordinal);
        Assert.Contains("<Kpi label=\"Needs Attention\"", jobsSource, StringComparison.Ordinal);
        Assert.Contains("<Kpi label=\"Recent Completion\"", jobsSource, StringComparison.Ordinal);
        Assert.Contains("History window", jobsSource, StringComparison.Ordinal);
        Assert.Contains("24 hours", jobsSource, StringComparison.Ordinal);
        Assert.Contains("30 days", jobsSource, StringComparison.Ordinal);
        Assert.Contains("Active Jobs", jobsSource, StringComparison.Ordinal);
        Assert.Contains("Job History", jobsSource, StringComparison.Ordinal);
        Assert.Contains("Recovery Tools", jobsSource, StringComparison.Ordinal);
        Assert.Contains("Optionally retry terminal engine failures", jobsSource, StringComparison.Ordinal);
        Assert.Contains("This is intentionally not automatic or required.", jobsSource, StringComparison.Ordinal);
        Assert.True(
            jobsSource.IndexOf("Active Jobs", StringComparison.Ordinal) < jobsSource.IndexOf("Recovery Tools", StringComparison.Ordinal)
            && jobsSource.IndexOf("Recovery Tools", StringComparison.Ordinal) < jobsSource.IndexOf("Job History", StringComparison.Ordinal),
            "Recovery Tools must remain secondary to active work and ahead of history.");
        Assert.DoesNotContain("rangeHours", jobsSource, StringComparison.Ordinal);
        Assert.Contains("Raw Job Log", source, StringComparison.Ordinal);
        Assert.Contains("jobNeedsAttention", source, StringComparison.Ordinal);

        var queueStart = source.IndexOf("function QueuePanel", StringComparison.Ordinal);
        var controls = source.IndexOf("queue-action-bar", queueStart, StringComparison.Ordinal);
        var kpis = source.IndexOf("audit-kpis queue-kpis", queueStart, StringComparison.Ordinal);
        var queueEnd = source.IndexOf("function JobsTable", queueStart, StringComparison.Ordinal);
        var queueSource = source[queueStart..queueEnd];
        Assert.True(controls > queueStart && controls < kpis, "Live ingest controls must remain above Queue KPIs.");
        Assert.Contains("Pause Until Resumed", queueSource, StringComparison.Ordinal);
        Assert.Contains("Accepting Calls", queueSource, StringComparison.Ordinal);
        Assert.Contains("Pausing discards new incoming calls; Trunk Recorder continues running.", queueSource, StringComparison.Ordinal);
        Assert.Contains("Queue Composition", queueSource, StringComparison.Ordinal);
        Assert.Contains("Oldest Pending Transcriptions", queueSource, StringComparison.Ordinal);
        Assert.Contains("queueCondition &&", queueSource, StringComparison.Ordinal);
        Assert.DoesNotContain("All Queue Warnings And Blockers", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Current Status", queueSource, StringComparison.Ordinal);
        Assert.DoesNotContain("<Kpi label=\"AI Completions\"", queueSource, StringComparison.Ordinal);
        Assert.DoesNotContain("<Kpi label=\"Embedding Link\"", queueSource, StringComparison.Ordinal);
    }

    [Fact]
    public void SystemPagesUseCompactIdentityAndSurfaceTranscriptionRecoveryWithoutPromotingIt()
    {
        var source = AppSource();
        var styles = StyleSource();
        var recommendations = RecommendationServiceSource();

        Assert.Contains("function SystemPageIdentity", source, StringComparison.Ordinal);
        Assert.Contains("system-page-identity", source, StringComparison.Ordinal);
        Assert.Contains("system-page-identity-icon", source, StringComparison.Ordinal);
        Assert.Contains("<p>{purpose}</p>", source, StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 760px)", styles, StringComparison.Ordinal);
        Assert.Contains("recoverable-transcription-failures", recommendations, StringComparison.Ordinal);
        Assert.Contains("new RecommendationTargetDto(\"runtime\", \"jobs\", \"transcription-recovery-tools\")", recommendations, StringComparison.Ordinal);
        Assert.Contains("{ TopTab: \"runtime\", SubTab: \"jobs\" } => \"Runtime / Jobs\"", recommendations, StringComparison.Ordinal);
        Assert.Contains("Endpoint Outage History", source, StringComparison.Ordinal);
        Assert.Contains("Older outages may be available; select", source, StringComparison.Ordinal);
        Assert.Contains("Older failures may be available; select", source, StringComparison.Ordinal);
        Assert.Contains("recoveryAvailable === 0", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectedThemeOwnsUiAccentsWhileSemanticAndSeriesColorsRemainIndependent()
    {
        var source = AppSource();
        var styles = StyleSource();

        Assert.Contains("{ value: \"red\", label: \"Red\" }", source, StringComparison.Ordinal);
        Assert.Contains("{ value: \"purple\", label: \"Purple\" }", source, StringComparison.Ordinal);
        Assert.Contains("{ value: \"green\", label: \"Green\" }", source, StringComparison.Ordinal);
        Assert.Contains(":root[data-theme=\"red\"]", styles, StringComparison.Ordinal);
        Assert.Contains(":root[data-theme=\"purple\"]", styles, StringComparison.Ordinal);
        Assert.Contains(":root[data-theme=\"green\"]", styles, StringComparison.Ordinal);
        Assert.Contains("--identity-accent: var(--accent)", styles, StringComparison.Ordinal);
        Assert.DoesNotContain(".system-page-identity.tone-", styles, StringComparison.Ordinal);
        Assert.Contains("color-mix(in srgb, var(--accent)", styles, StringComparison.Ordinal);
        Assert.Contains(".category-police { --category-color: #5aa7ff", styles, StringComparison.Ordinal);
        Assert.Contains(".recommendation-card.severity-high { border-color: #c85b50", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void ServicesUseOneLiveResourceGraphWithCompactControls()
    {
        var source = AppSource();

        Assert.Contains("Service Resource Use", source, StringComparison.Ordinal);
        Assert.Contains("Near-real-time CPU and memory use by PizzaWave service", source, StringComparison.Ordinal);
        Assert.Contains("service-resource-table", source, StringComparison.Ordinal);
        Assert.Contains("service-resource-content", source, StringComparison.Ordinal);
        Assert.Contains("const tickRatios = [1, 0.75, 0.5, 0.25, 0]", source, StringComparison.Ordinal);
        Assert.DoesNotContain("service-card-grid", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Current receiver issues", source, StringComparison.Ordinal);
        Assert.DoesNotContain("trMetricIssues.map", source, StringComparison.Ordinal);
        Assert.DoesNotContain("trSystemIssues.map", source, StringComparison.Ordinal);
        Assert.DoesNotContain("receiver issue row(s)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("<Kpi label=\"PizzaWave\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Technical Service Details", source, StringComparison.Ordinal);
        Assert.DoesNotContain("How resource measurements work", source, StringComparison.Ordinal);
        Assert.DoesNotContain("system-services-summary", source, StringComparison.Ordinal);
    }

    [Fact]
    public void GlobalStatusControlsAreCompactActionableAndDoNotDuplicateProfileSelection()
    {
        var source = AppSource();

        Assert.DoesNotContain("visibleSystemPageLabel", source, StringComparison.Ordinal);
        Assert.DoesNotContain(">Refresh {", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Profile: {profileState", source, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Call data window\"", source, StringComparison.Ordinal);
        Assert.Contains("onClick={() => goSystem(\"services\")}>{livePillText}</button>", source, StringComparison.Ordinal);
        Assert.Contains("onClick={() => goSystem(\"jobs\")}>Jobs", source, StringComparison.Ordinal);
        Assert.Contains("`Queue blocked ${queueDepth.toLocaleString()}`", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BandwidthIsPresentedAsPizzaWaveAttributedTrafficOnly()
    {
        var source = AppSource();

        Assert.Contains("PizzaWave-attributed remote transcription and AI network use.", source, StringComparison.Ordinal);
        Assert.Contains("Remote transcription and AI traffic attributed by the PizzaWave workflow that produced it.", source, StringComparison.Ordinal);
        Assert.Contains("Selected Range", source, StringComparison.Ordinal);
        Assert.Contains("This Month", source, StringComparison.Ordinal);
        Assert.Contains("All Time", source, StringComparison.Ordinal);
        Assert.Contains("BandwidthTimelineChart", source, StringComparison.Ordinal);
        Assert.Contains("<summary>Bandwidth Activity", source, StringComparison.Ordinal);
        Assert.DoesNotContain("<Kpi label=\"Remote Processing\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("<Kpi label=\"Transcription Uploads\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TrunkRecorderOwnsConfigurationWhileRfPerformanceOwnsSiteHealth()
    {
        var source = AppSource();

        Assert.Contains("system-tr-summary|24", source, StringComparison.Ordinal);
        Assert.Contains("/api/v1/system/tr-logs", source, StringComparison.Ordinal);
        Assert.Contains("Last hour", source, StringComparison.Ordinal);
        Assert.Contains("Last 6 hours", source, StringComparison.Ordinal);
        Assert.Contains("<option value={168}>Last 7 days</option>", source, StringComparison.Ordinal);
        Assert.Contains("Last 24 hours", source, StringComparison.Ordinal);
        Assert.Contains(">Apply</button>", source, StringComparison.Ordinal);
        Assert.Contains("copied ? \"Copied\" : \"Copy\"", source, StringComparison.Ordinal);
        Assert.Contains(">Older</button>", source, StringComparison.Ordinal);
        Assert.Contains(">Newer</button>", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Recent Trunk Recorder Output", source, StringComparison.Ordinal);
        Assert.Contains("function TrConfigurationSummaryView", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Capture topology", source, StringComparison.Ordinal);
        Assert.Contains("Source Assignments", source, StringComparison.Ordinal);
        Assert.Contains("function SystemSectionHeader", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Configured Sites", source, StringComparison.Ordinal);
        Assert.Contains("Control channel", source, StringComparison.Ordinal);
        Assert.Contains("Talkgroup file:", source, StringComparison.Ordinal);
        Assert.Contains("Observed range", source, StringComparison.Ordinal);
        Assert.Contains("Configuration Viewer", source, StringComparison.Ordinal);
        Assert.Contains("Safety backups", source, StringComparison.Ordinal);
        Assert.Contains("Changes from active", source, StringComparison.Ordinal);
        Assert.Contains("Copy Config", source, StringComparison.Ordinal);
        Assert.DoesNotContain("danger-button\" onClick={onOpenSetup}>Open Setup", source, StringComparison.Ordinal);
        Assert.Contains("system-metrics-rf|${rfPerformanceHours}", source, StringComparison.Ordinal);
        Assert.Contains("setRfPerformanceHours(chartHours)", source, StringComparison.Ordinal);
        Assert.Contains("pizzawave-system-rf-performance-hours\", String(chartHours)", source, StringComparison.Ordinal);
        Assert.Contains("function RfHealthStatusPanel", source, StringComparison.Ordinal);
        Assert.Contains("<label>Window <select value={rangeHours}", source, StringComparison.Ordinal);
        Assert.Contains("tr-site-card-grid", source, StringComparison.Ordinal);
        Assert.Contains("40 msg/s strong reference", source, StringComparison.Ordinal);
        Assert.DoesNotContain("monitored sites unavailable", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RF-health window:", source, StringComparison.Ordinal);
        Assert.Contains("preserveAspectRatio=\"xMidYMid meet\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Raw RF-health samples", source, StringComparison.Ordinal);
        Assert.DoesNotContain("<h3>RF Analysis</h3>", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Observed pattern", source, StringComparison.Ordinal);
        Assert.DoesNotContain("rf-observed-pattern", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Review in Setup", source, StringComparison.Ordinal);
        Assert.Contains("TrSiteFact", source, StringComparison.Ordinal);
        Assert.Contains("<label>Charts <select", source, StringComparison.Ordinal);
        Assert.Contains("/decode/i", source, StringComparison.Ordinal);
        Assert.Contains("onSelectCategory(\"decode\")", source, StringComparison.Ordinal);
        Assert.DoesNotContain("function TrHealthSummaryView", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Technical paths", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Stack-wide signals", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Receiver needs review", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Receiver health is normal", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Receiver Signals", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Use the Receiver Summary", source, StringComparison.Ordinal);
        Assert.Contains("pizzawave-system-incident-performance-hours", source, StringComparison.Ordinal);
        Assert.Contains("/api/v1/incidents/chains?hours=", source, StringComparison.Ordinal);
        Assert.Contains("Incident performance window", source, StringComparison.Ordinal);
        Assert.Contains("Incident Pipeline Inspector", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Incident Evidence Paths", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Created incidents over time", source, StringComparison.Ordinal);
        Assert.Contains("function IncidentEvidenceDag", source, StringComparison.Ordinal);
        Assert.Contains("What happened", source, StringComparison.Ordinal);
        Assert.Contains("Evidence change", source, StringComparison.Ordinal);
        Assert.Contains("Why PizzaWave chose this", source, StringComparison.Ordinal);
        Assert.Contains("No operator-visible change", source, StringComparison.Ordinal);
        Assert.Contains("transcriptSnippet", source, StringComparison.Ordinal);
        Assert.Contains("pizzawave-site-setup-rf-validation-subpage\", \"coverage", source, StringComparison.Ordinal);
        Assert.Contains("<label>Config class", source, StringComparison.Ordinal);
        Assert.Contains("<label className=\"config-viewer-selector\">Select a config", source, StringComparison.Ordinal);
        Assert.DoesNotContain("onOpenSetup(\"rf\")", source, StringComparison.Ordinal);
        Assert.Contains("onOpenSetup(\"Systems & Sites\")", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Problematic Incidents", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Pipeline Decisions", source, StringComparison.Ordinal);
        Assert.DoesNotContain("No mapped location", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Incident Association Watchlist", source, StringComparison.Ordinal);
        Assert.DoesNotContain("<Kpi label=\"Weak Evidence\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("<Kpi label=\"Low Confidence\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("<Kpi label=\"Short Evidence\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RecommendationsUseCompactCardsWithFocusedFindingDetails()
    {
        var source = AppSource();

        Assert.DoesNotContain("recommendations-summary", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Current findings</span>", source, StringComparison.Ordinal);
        Assert.Contains("recommendationFacts(item)", source, StringComparison.Ordinal);
        Assert.Contains("Open {item.destinationLabel}", source, StringComparison.Ordinal);
        Assert.Contains("if (target.topTab === \"metrics\")", source, StringComparison.Ordinal);
        Assert.Contains("\"ai\", \"bandwidth\"] as const", source, StringComparison.Ordinal);
        Assert.Contains("Review finding", source, StringComparison.Ordinal);
        Assert.Contains("item.activityState === \"quiet\" && <span>Dormant</span>", source, StringComparison.Ordinal);
        Assert.Contains("item.activityState === \"quiet\" ? \" is-dormant\"", source, StringComparison.Ordinal);
        Assert.Contains(".recommendation-card.is-dormant { opacity: .62", StyleSource(), StringComparison.Ordinal);
        Assert.Contains("formatRelativeAge(item.lastSeenUtc)", source, StringComparison.Ordinal);
        Assert.Contains("detail: formatShortDate(item.lastSeenUtc)", source, StringComparison.Ordinal);
        Assert.Contains("finding-drawer", source, StringComparison.Ordinal);
        Assert.Contains(".finding-drawer { width: min(570px, 94vw)", StyleSource(), StringComparison.Ordinal);
        Assert.Contains("background: #20252a", StyleSource(), StringComparison.Ordinal);
        Assert.Contains("finding-drawer-section next-step", source, StringComparison.Ordinal);
        Assert.Contains("finding-drawer-section operator-notes", source, StringComparison.Ordinal);
        Assert.Contains("Operator notes", source, StringComparison.Ordinal);
        Assert.Contains("finding-activity-pagination", source, StringComparison.Ordinal);
        Assert.Contains("Page {currentActivityPage} of {activityPageCount}", source, StringComparison.Ordinal);
        Assert.Contains("recommendation-history-ledger", source, StringComparison.Ordinal);
        Assert.Contains("recommendationHistoryGroups(items)", source, StringComparison.Ordinal);
        Assert.Contains("new Set(rows.map(row => row.findingId)).size", source, StringComparison.Ordinal);
        Assert.Contains(".finding-drawer-section.facts", StyleSource(), StringComparison.Ordinal);
        Assert.DoesNotContain("Finding details and operator actions", source, StringComparison.Ordinal);
        Assert.DoesNotContain("service-issue-card", source, StringComparison.Ordinal);
        Assert.Contains("Recommendation candidates", source, StringComparison.Ordinal);
        Assert.Contains("Clear candidate filter", source, StringComparison.Ordinal);
        Assert.DoesNotContain("recommendations/${encodeURIComponent(id)}/state", source, StringComparison.Ordinal);
        Assert.DoesNotContain("onClick={() => void onState(item.id, \"ignore\")}", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Exclude all candidates", source, StringComparison.Ordinal);
        Assert.DoesNotContain("<h3>Recently Resolved</h3>", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Finding history — 90 days", source, StringComparison.Ordinal);
        Assert.Contains("function AuditHistoryPanel", source, StringComparison.Ordinal);
        Assert.Contains("recommendations.history.map(findingAuditEntry)", source, StringComparison.Ordinal);
        Assert.Contains("SystemPageHeaderControls", source, StringComparison.Ordinal);
        Assert.Contains("History window", source, StringComparison.Ordinal);
        Assert.Contains("Last 90 days", source, StringComparison.Ordinal);
        Assert.Contains("Resolved findings", source, StringComparison.Ordinal);
        Assert.Contains("compactAuditFindings", source, StringComparison.Ordinal);
        Assert.Contains("events consolidated into", source, StringComparison.Ordinal);
        Assert.Contains("onOpenJobs", source, StringComparison.Ordinal);
        Assert.Contains("audit-history-row", source, StringComparison.Ordinal);
        Assert.Contains("audit-evidence-shelf", source, StringComparison.Ordinal);
        Assert.Contains("aria-expanded={open}", source, StringComparison.Ordinal);
        Assert.DoesNotContain("<th>What happened</th><th>Outcome</th><th>Evidence</th>", source, StringComparison.Ordinal);
        Assert.DoesNotContain("<summary>View</summary>", source, StringComparison.Ordinal);
        Assert.DoesNotContain("rangeHours={rangeHours} onOpenJobs", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SystemNavigationBadgeCountsCurrentProblems()
    {
        var source = AppSource();

        Assert.Contains("systemProblemCount > 0", source, StringComparison.Ordinal);
        Assert.Contains("nav-badge problem", source, StringComparison.Ordinal);
        Assert.Contains("systemProblemCount} System problem", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BackupRestoreUsesOneDeliberateArchiveWorkflowAndSeparateResetPage()
    {
        var source = AppSource();
        var backupStart = source.IndexOf("function BackupRestorePanel", StringComparison.Ordinal);
        var resetStart = source.IndexOf("function SystemResetPanel", backupStart, StringComparison.Ordinal);
        var previewStart = source.IndexOf("function BackupRestorePreviewCard", resetStart, StringComparison.Ordinal);
        var backupSource = source[backupStart..resetStart];
        var resetSource = source[resetStart..previewStart];

        Assert.Contains(">Reset</button>", source, StringComparison.Ordinal);
        Assert.Contains("Create Backup", backupSource, StringComparison.Ordinal);
        Assert.Contains("Available Backups", backupSource, StringComparison.Ordinal);
        Assert.Contains("Stage a Backup", backupSource, StringComparison.Ordinal);
        Assert.Contains("Support Package", backupSource, StringComparison.Ordinal);
        Assert.Contains("Staged Restore Review", backupSource, StringComparison.Ordinal);
        Assert.Contains("Backup contents by area", backupSource, StringComparison.Ordinal);
        Assert.Contains("backup-estimate-kpis", backupSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Backup / Restore Guardrails", backupSource, StringComparison.Ordinal);
        Assert.DoesNotContain("<h3>Reset</h3>", backupSource, StringComparison.Ordinal);
        Assert.Contains("Reset scopes are mutually exclusive", resetSource, StringComparison.Ordinal);
        Assert.Contains("type=\"radio\"", resetSource, StringComparison.Ordinal);
        Assert.Contains("Create backup before reset", resetSource, StringComparison.Ordinal);
        Assert.Contains("Verification Checks", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RelatedRecommendationSignalsCollapseIntoOperatorConditions()
    {
        var source = RecommendationServiceSource();

        Assert.Contains("BuildSystemAssessmentsAsync(healthStart", source, StringComparison.Ordinal);
        Assert.Contains("RfTemporalFindingAnalyzer.Analyze", source, StringComparison.Ordinal);
        Assert.Contains("tr-rf-temporal:{finding.OwnerKey}", source, StringComparison.Ordinal);
        Assert.Contains("recurring RF degradation", source, StringComparison.Ordinal);
        Assert.Contains("GroupBy(row => row.OwnerKey", source, StringComparison.Ordinal);
        Assert.Contains("RF Performance owns the underlying charts", source, StringComparison.Ordinal);
        Assert.Contains("40 msg/s remains the strong-system reference", source, StringComparison.Ordinal);
        Assert.Contains("BuildRfAssessmentDiagnostics(assessment)", source, StringComparison.Ordinal);
        Assert.Contains("Other monitored-system context:", source, StringComparison.Ordinal);
        Assert.Contains("BuildRetuneTargetDiagnosticsAsync(rfEvidenceStart, rfEvidenceEnd", source, StringComparison.Ordinal);
        Assert.Contains("SystemDisplayName(finding.OwnerKey)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("var retuneProblem", source, StringComparison.Ordinal);
        Assert.DoesNotContain("var lowDecodeProblem", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Global periodic control-channel summary decode-zero rate", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new SystemRecommendationDto(\n                \"tr-retunes\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new SystemRecommendationDto(\n                $\"tr-decode-zero:", source, StringComparison.Ordinal);

        Assert.Contains("new SystemRecommendationDto(\n                \"ai-generation-health\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new SystemRecommendationDto(\n                \"ai-service-failures\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new SystemRecommendationDto(\n                \"ai-truncation-pressure\"", source, StringComparison.Ordinal);

        Assert.Contains("new SystemRecommendationDto(\n                \"queue-pressure\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new SystemRecommendationDto(\n                \"ai-blocked-queue\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RecommendationsAreSortedBySeverityAfterFindingLifecycleSync()
    {
        var source = RecommendationServiceSource();

        var sync = source.IndexOf("SyncRecommendationFindingsAsync", StringComparison.Ordinal);
        var postSyncSort = source.IndexOf("findings.Active\n            .OrderByDescending(r => SeverityRank(r.Severity))", sync, StringComparison.Ordinal);
        var kindTieBreak = source.IndexOf(".ThenBy(r => r.Kind == \"improvement\" ? 1 : 0)", postSyncSort, StringComparison.Ordinal);

        Assert.True(sync >= 0, "Finding lifecycle sync must remain present.");
        Assert.True(postSyncSort > sync, "Severity ordering must be reapplied after lifecycle sync.");
        Assert.True(kindTieBreak > postSyncSort, "Finding kind may only break ties after severity.");
    }
}
