import React, { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { createRoot } from "react-dom/client";
import { Activity, Bell, Gauge, Radio, Settings, ShieldAlert } from "lucide-react";
import { api, rangeBody, rangeQuery } from "./api";
import type { AlertMatch, BarStat, CategoryPage, Dashboard, EngineCall, EngineHealth, HourCategory, Incident, Job, JobLog, LocationHeat, ProcessingProfile, ProfileState, QualityAuditGroup, QualityAuditSample, QualityHour, QueueSnapshot, SetupArtifactReport, SetupCalibrationPlan, SetupSdrDetection, SetupStatus, SetupTalkgroupPreview, SetupTalkgroupRow, SetupTrConfigDraft, SetupValidationResult, StatusSummary, SystemRecommendations, TalkgroupCatalogDocument, TalkgroupCatalogItem, TalkgroupCatalogResponse, TalkgroupCatalogSaveResult, TalkgroupOption, TalkgroupTrCsvResult, TokenUsageReport, TopTalkgroup, TranscriptionBenchmarkResult, TrHealthChart, TrHealthMetric, TrRfAnalysis, TrTroubleshoot } from "./types";
import "./style.css";

const categories = ["police", "fire", "ems", "traffic", "other"] as const;
type Page = "dashboard" | "system" | "settings" | typeof categories[number];
type CategoryViewMode = "incidents" | "raw";
type DashboardTab = "around" | "stats" | "alerts";
const categoryColors: Record<string, string> = {
  police: "#5aa7ff",
  fire: "#ff6b5a",
  ems: "#54d68a",
  traffic: "#f7c948",
  other: "#b58cff"
};
const qualityColors: Record<keyof Omit<QualityHour, "hour">, string> = {
  inaudible: "#5aa7ff",
  short: "#f7c948",
  empty: "#9faab5",
  failure: "#ff6b5a"
};

function App() {
  const [page, setPage] = useState<Page>("dashboard");
  const [rangeHours, setRangeHours] = useState(24);
  const [theme, setTheme] = useState(() => localStorage.getItem("pizzawave-theme") || "blue");
  const [categoryViewMode, setCategoryViewMode] = useState<CategoryViewMode>(() => {
    const saved = localStorage.getItem("pizzawave-category-view");
    return saved === "raw" ? saved : "incidents";
  });
  const [status, setStatus] = useState("Starting");
  const [dashboard, setDashboard] = useState<Dashboard | null>(null);
  const [category, setCategory] = useState<CategoryPage | null>(null);
  const [jobs, setJobs] = useState<Job[]>([]);
  const [engineHealth, setEngineHealth] = useState<EngineHealth | null>(null);
  const [statusSummary, setStatusSummary] = useState<StatusSummary | null>(null);
  const [profileState, setProfileState] = useState<ProfileState | null>(null);
  const [setupStatus, setSetupStatus] = useState<SetupStatus | null>(null);
  const [troubleshoot, setTroubleshoot] = useState<TrTroubleshoot | null>(null);
  const [recommendations, setRecommendations] = useState<SystemRecommendations | null>(null);
  const [settingsSections, setSettingsSections] = useState<Record<string, any>>({});
  const [settingsLoadState, setSettingsLoadState] = useState<{ loading: boolean; version: number; message: string; error: boolean }>({ loading: false, version: 0, message: "", error: false });
  const settingsFileInputRef = useRef<HTMLInputElement | null>(null);
  const refreshStatusRef = useRef<() => Promise<void>>(async () => { });
  const refreshVisiblePageRef = useRef<() => Promise<void>>(async () => { });

  const fetchSettingsSections = useCallback(async () => {
    const [engine, transcription, aiInsights, sftp, tr, alerts] = await Promise.all([
      api.request<any>("/api/v1/settings/engine"),
      api.request<any>("/api/v1/settings/transcription"),
      api.request<any>("/api/v1/settings/ai-insights"),
      api.request<any>("/api/v1/settings/sftp"),
      api.request<any>("/api/v1/settings/tr"),
      api.request<any>("/api/v1/settings/alerts")
    ]);
    return {
      engine: engine.values,
      transcription: transcription.values,
      "ai-insights": aiInsights.values,
      sftp: sftp.values,
      tr: tr.values,
      alerts: alerts.values
    };
  }, []);

  const loadSettings = useCallback(async () => {
    setSettingsLoadState(current => ({ ...current, loading: true, message: "Loading settings...", error: false }));
    try {
      setSettingsSections(await fetchSettingsSections());
      setSettingsLoadState(current => ({ loading: false, version: current.version + 1, message: "Settings loaded", error: false }));
    } catch (error) {
      setSettingsLoadState(current => ({ loading: false, version: current.version, message: error instanceof Error ? error.message : "Settings load failed", error: true }));
    }
  }, [fetchSettingsSections]);

  async function exportSettingsFile() {
    setSettingsLoadState(current => ({ ...current, loading: true, message: "Exporting settings...", error: false }));
    try {
      const sections = await fetchSettingsSections();
      const json = JSON.stringify(settingsFileFromSections(sections), null, 2);
      const url = URL.createObjectURL(new Blob([json], { type: "application/json" }));
      const link = document.createElement("a");
      link.href = url;
      link.download = `pizzawave-settings-${new Date().toISOString().replace(/[:.]/g, "-")}.json`;
      document.body.appendChild(link);
      link.click();
      link.remove();
      URL.revokeObjectURL(url);
      setSettingsLoadState(current => ({ loading: false, version: current.version, message: "Settings exported", error: false }));
    } catch (error) {
      setSettingsLoadState(current => ({ loading: false, version: current.version, message: error instanceof Error ? error.message : "Settings export failed", error: true }));
    }
  }

  async function loadSettingsFile(file: File | undefined) {
    if (!file) return;
    setSettingsLoadState(current => ({ ...current, loading: true, message: `Loading ${file.name}...`, error: false }));
    try {
      const text = await file.text();
      const parsed = JSON.parse(text);
      setSettingsSections(settingsSectionsFromFile(parsed));
      setSettingsLoadState(current => ({ loading: false, version: current.version + 1, message: `Loaded ${file.name}. Save each changed section to apply it.`, error: false }));
    } catch (error) {
      setSettingsLoadState(current => ({ loading: false, version: current.version, message: error instanceof Error ? error.message : "Settings file load failed", error: true }));
    } finally {
      if (settingsFileInputRef.current)
        settingsFileInputRef.current.value = "";
    }
  }

  const refreshStatusData = useCallback(async () => {
    const setup = await api.request<SetupStatus>("/api/v1/setup/status");
    setSetupStatus(setup);
    if (!setup.completed) {
      const healthStatus = await api.request<EngineHealth>("/api/v1/health");
      setEngineHealth(healthStatus);
      setStatus("Setup");
      return;
    }

    const [healthStatus, jobRows, summary, profiles, recs] = await Promise.all([
      api.request<EngineHealth>("/api/v1/health"),
      api.request<Job[]>("/api/v1/jobs"),
      api.request<StatusSummary>(`/api/v1/status?${rangeQuery(rangeHours)}`),
      api.request<ProfileState>("/api/v1/profiles"),
      api.request<SystemRecommendations>("/api/v1/system/recommendations")
    ]);
    setEngineHealth(healthStatus);
    setJobs(jobRows);
    setStatusSummary(summary);
    setProfileState(profiles);
    setRecommendations(recs);
    setStatus("Live");
  }, [rangeHours]);

  const refreshVisiblePage = useCallback(async () => {
    if (page === "dashboard") {
      setDashboard(await api.request<Dashboard>(`/api/v1/dashboard?${rangeQuery(rangeHours)}`));
    } else if (categories.includes(page as any)) {
      setCategory(await api.request<CategoryPage>(`/api/v1/categories/${page}?${rangeQuery(rangeHours)}`));
    } else if (page === "system") {
      setTroubleshoot(await api.request<TrTroubleshoot>(`/api/v1/troubleshoot?${rangeQuery(rangeHours)}&bySystem=false&baseline=7d`));
    }
  }, [page, rangeHours]);

  const load = useCallback(async () => {
    try {
      await refreshStatusData();
      await refreshVisiblePage();
    } catch (error) {
      setStatus(error instanceof Error ? error.message : "Error");
    }
  }, [refreshStatusData, refreshVisiblePage]);

  useEffect(() => { refreshStatusRef.current = refreshStatusData; }, [refreshStatusData]);
  useEffect(() => { refreshVisiblePageRef.current = refreshVisiblePage; }, [refreshVisiblePage]);

  useEffect(() => { void load(); }, [load]);
  useEffect(() => { if (page === "settings") void loadSettings(); }, [page, loadSettings]);

  useEffect(() => {
    document.documentElement.dataset.theme = theme;
    localStorage.setItem("pizzawave-theme", theme);
  }, [theme]);

  useEffect(() => {
    localStorage.setItem("pizzawave-category-view", categoryViewMode);
  }, [categoryViewMode]);

  useEffect(() => {
    const events = new EventSource("/api/v1/events/stream");
    let statusTimer = 0;
    let pageTimer = 0;
    const scheduleStatus = (delayMs: number) => {
      window.clearTimeout(statusTimer);
      statusTimer = window.setTimeout(() => {
        void refreshStatusRef.current().catch(error => setStatus(error instanceof Error ? error.message : "Error"));
      }, delayMs);
    };
    const schedulePage = (delayMs: number) => {
      window.clearTimeout(pageTimer);
      pageTimer = window.setTimeout(() => {
        void refreshVisiblePageRef.current().catch(error => setStatus(error instanceof Error ? error.message : "Error"));
      }, delayMs);
    };
    const refreshCurrentView = () => {
      scheduleStatus(500);
      schedulePage(900);
    };
    const refreshCurrentViewSoon = () => {
      scheduleStatus(900);
      schedulePage(1500);
    };
    events.addEventListener("connected", () => {
      setStatus("Live");
      refreshCurrentView();
    });
    events.addEventListener("call_ingested", refreshCurrentViewSoon);
    events.addEventListener("call_transcribed", refreshCurrentViewSoon);
    events.addEventListener("alert_matched", refreshCurrentView);
    events.addEventListener("summary_updated", refreshCurrentView);
    events.addEventListener("job_updated", refreshCurrentView);
    events.addEventListener("health_updated", refreshCurrentView);
    events.onerror = () => setStatus("Reconnecting");
    return () => {
      window.clearTimeout(statusTimer);
      window.clearTimeout(pageTimer);
      events.close();
    };
  }, []);

  const nav = useMemo(() => ["dashboard", ...categories, "system", "settings"] as Page[], []);
  const activeProfile = profileState?.profiles.find(p => p.id === profileState.activeProfileId);
  const visibleNav = nav.filter(item => !categories.includes(item as any) || profileIncludes(activeProfile, item));
  const activeJobCount = jobs.filter(j => j.status === "running" || j.status === "queued" || j.status === "paused").length;
  const queueDepth = engineHealth?.queueDepth ?? 0;
  const queueBlockedNotes = [engineHealth?.importWorkBlockedReason, engineHealth?.aiWorkBlockedReason].filter(Boolean);
  const queueHealth = queueDepth <= 0 ? "clear" : engineHealth?.queueUnderPressure ? "pressure" : "draining";
  const audioTranscribedPerMinute = engineHealth?.recentAudioSecondsTranscribedPerMinute ?? 0;
  const audioIngestedPerMinute = engineHealth?.recentAudioSecondsIngestedPerMinute ?? 0;
  const queueRateSuffix = engineHealth ? ` (${audioTranscribedPerMinute.toFixed(0)}s audio/min)` : "";
  const queuePressureNote = queueBlockedNotes.length ? `; ${[
    engineHealth?.importWorkBlockedReason ? "imports paused" : "",
    engineHealth?.aiWorkBlockedReason ? "AI paused" : ""
  ].filter(Boolean).join(", ")}` : "";
  const ingestPaused = Boolean(engineHealth?.ingest?.paused);
  const queueHealthText = queueHealth === "clear"
    ? `Queue OK ${queueDepth.toLocaleString()}${queueRateSuffix}`
    : queueHealth === "pressure"
      ? `Queue pressure ${queueDepth.toLocaleString()}${queueRateSuffix}${queuePressureNote}`
      : `Queue draining ${queueDepth.toLocaleString()}${queueRateSuffix}`;
  const queueHealthTitle = engineHealth
    ? `${engineHealth.recentAudioSecondsTranscribed.toLocaleString()} audio seconds transcribed (${audioTranscribedPerMinute.toFixed(0)}s/min) and ${engineHealth.recentAudioSecondsIngested.toLocaleString()} audio seconds ingested (${audioIngestedPerMinute.toFixed(0)}s/min) in the last ${engineHealth.throughputWindowMinutes} minutes. Calls: ${engineHealth.recentCallsTranscribed.toLocaleString()} done (${engineHealth.recentTranscribedPerMinute.toFixed(1)}/min), ${engineHealth.recentCallsIngested.toLocaleString()} in (${engineHealth.recentIngestPerMinute.toFixed(1)}/min). Local workers: ${engineHealth.liveTranscriptionWorkers} x ${engineHealth.whisperThreadsPerWorker} thread(s). ${queueBlockedNotes.join(" ")}`.trim()
    : "Transcription queue is clear.";

  const inSetup = Boolean(setupStatus && !setupStatus.completed);

  return (
    <div className={`app ${inSetup ? "setup-mode" : ""}`}>
      <header className="topbar">
        <div className="brand">
          <img src="/logo-small.png" alt="" />
          <span className="brand-text"><strong>PizzaWave</strong><small>{engineHealth?.stackName ?? "PizzaWave"}</small></span>
        </div>
        <select value={rangeHours} onChange={e => setRangeHours(Number(e.target.value))}>
          <option value={24}>24h</option>
          <option value={48}>2d</option>
          <option value={168}>Week</option>
        </select>
        <select aria-label="Color scheme" value={theme} onChange={e => setTheme(e.target.value)}>
          <option value="blue">Blue</option>
          <option value="orange">Orange</option>
        </select>
        {profileState && <ProfileSwitcher state={profileState} onChange={async id => {
          const next = { ...profileState, activeProfileId: id };
          setProfileState(next);
          await api.request<ProfileState>("/api/v1/profiles", { method: "POST", body: JSON.stringify(next) });
          await load();
        }} />}
        {page === "settings" && <>
          <input ref={settingsFileInputRef} type="file" accept="application/json,.json" hidden onChange={e => void loadSettingsFile(e.target.files?.[0])} />
          <button disabled={settingsLoadState.loading} onClick={() => settingsFileInputRef.current?.click()}>{settingsLoadState.loading ? "Loading..." : "Load Settings"}</button>
          <button disabled={settingsLoadState.loading} onClick={() => void exportSettingsFile()}>Export Settings</button>
        </>}
        {categories.includes(page as any) && <div className="segmented" aria-label="Category view mode">
          <button className={categoryViewMode === "incidents" ? "active" : ""} onClick={() => setCategoryViewMode("incidents")}>Incidents</button>
          <button className={categoryViewMode === "raw" ? "active" : ""} onClick={() => setCategoryViewMode("raw")}>Raw Calls</button>
        </div>}
        <span className="pill" title="Live means the browser is connected to pizzad and receiving server-sent refresh events.">{status}</span>
        {ingestPaused && <span className="pill ingest-paused" title={`New live callstream payloads are being dropped. ${engineHealth?.ingest?.reason ?? ""}`}>Ingest paused</span>}
        <span className="pill" title="REST loads the current view; SSE triggers live refreshes when calls, jobs, alerts, summaries, or health change.">REST+SSE</span>
      </header>
      {!inSetup && <aside className="nav">
        {visibleNav.map(item => (
          <button className={item === page ? "active" : ""} onClick={() => setPage(item)} key={item}>
            {navIcon(item)} {label(item)}
            {item === "system" && recommendations && recommendations.openCount > 0 && <span className={`nav-badge ${recommendations.highCount > 0 ? "high" : recommendations.mediumCount > 0 ? "medium" : "low"}`}>{recommendations.openCount}</span>}
          </button>
        ))}
      </aside>}
      <main className={`main ${inSetup ? "setup-main" : ""}`}>
        {inSetup && setupStatus && <SetupWizard status={setupStatus} reload={load} />}
        {setupStatus?.completed && page === "dashboard" && <DashboardView data={dashboard} rangeHours={rangeHours} reload={load} />}
        {setupStatus?.completed && categories.includes(page as any) && <CategoryView data={category} mode={categoryViewMode} />}
        {setupStatus?.completed && page === "system" && <SystemView data={troubleshoot} jobs={jobs} rangeHours={rangeHours} reload={load} engineHealth={engineHealth} recommendations={recommendations} setRecommendations={setRecommendations} />}
        {setupStatus?.completed && page === "settings" && <SettingsView settingsSections={settingsSections} settingsLoadState={settingsLoadState} reload={load} profileState={profileState} setProfileState={setProfileState} />}
      </main>
      {!inSetup && <footer className="statusbar">
        <span className="pill">Profile: {profileState?.profiles.find(p => p.id === profileState.activeProfileId)?.name ?? "Default"}</span>
        <span className="status-separator">|</span>
        <span className="pill">Calls {statusSummary?.calls?.toLocaleString() ?? "--"}</span>
        <span className="pill">Incidents {statusSummary?.incidents?.toLocaleString() ?? "--"}</span>
        <span className="pill">Alerts {statusSummary?.alerts?.toLocaleString() ?? "--"}</span>
        <span className={`pill queue-health queue-${queueHealth}`} title={queueHealthTitle}>{queueHealthText}</span>
        {activeJobCount > 0 && <span className="pill">Jobs {activeJobCount}</span>}
      </footer>}
    </div>
  );
}

function DashboardView({ data, rangeHours, reload }: { data: Dashboard | null; rangeHours: number; reload: () => Promise<void> }) {
  const [focusedLocationKey, setFocusedLocationKey] = useState<string | null>(null);
  const [tab, setTab] = useState<DashboardTab>("around");
  if (!data) return <div className="pane">Loading dashboard...</div>;
  const hiddenKpis = new Set(["alert rate", "token usage", "incidents", "top problem system", "tr decode 0%", "tr worst decode", "busiest hour", "unique talkgroups"]);
  const visibleKpis = data.kpis.filter(k => !hiddenKpis.has(k.label.trim().toLowerCase()));
  const incidentLocationMap = buildIncidentLocationMap(data.locationHeat);
  const incidentLocationRows = data.locationHeat.filter(row => row.incidentLinks?.length > 0);
  return (
    <div className="dashboard-shell">
      <div className="dashboard-tabs">
        <button className={tab === "around" ? "active" : ""} onClick={() => setTab("around")}>Around me</button>
        <button className={tab === "stats" ? "active" : ""} onClick={() => setTab("stats")}>Statistics</button>
        <button className={tab === "alerts" ? "active" : ""} onClick={() => setTab("alerts")}>Alerts</button>
      </div>
      {tab === "around" && <div className="dashboard dashboard-around">
        <section className="pane dashboard-map-pane">
          <div className="section"><h3>Geolocated Incident Map</h3><LocationHeatMap rows={incidentLocationRows} focusedKey={focusedLocationKey} onFocusKey={setFocusedLocationKey} /></div>
        </section>
        <section className="pane dashboard-incidents-pane">
          <div className="section"><h2><ShieldAlert size={16} /> Incident Explorer</h2><Incidents rows={data.incidents} locationMap={incidentLocationMap} onShowLocation={setFocusedLocationKey} /></div>
        </section>
      </div>}
      {tab === "stats" && <div className="dashboard dashboard-stats">
        <section className="pane dashboard-kpis-pane">
          <div className="section kpis">{visibleKpis.map(k => <Kpi key={k.label} {...k} />)}</div>
        </section>
        <section className="pane dashboard-charts-pane">
          <div className="section"><h3>Volume Patterns</h3><VolumeByHourChart rows={data.volumeByHourCategory} /></div>
        </section>
      </div>}
      {tab === "alerts" && <div className="dashboard dashboard-alerts">
        <section className="pane dashboard-alerts-pane"><h2><Bell size={16} /> Alerts</h2><Alerts rows={data.alerts} /></section>
      </div>}
    </div>
  );
}

function locationKey(row: LocationHeat) {
  return `${row.areaId}|${row.locationText}`.toLowerCase();
}

function buildIncidentLocationMap(rows: LocationHeat[]) {
  const map = new Map<number, LocationHeat>();
  for (const row of rows) {
    for (const link of row.incidentLinks ?? []) {
      if (!map.has(link.incidentId)) map.set(link.incidentId, row);
    }
  }
  return map;
}

function ProfileSwitcher({ state, onChange }: { state: ProfileState; onChange: (id: string) => Promise<void> }) {
  return <select aria-label="Processing profile" value={state.activeProfileId} onChange={e => void onChange(e.target.value)}>
    {state.profiles.map(profile => <option value={profile.id} key={profile.id}>{profile.name}</option>)}
  </select>;
}

function Kpi({ label, value, subtext }: { label: string; value: string; subtext: string }) {
  return <div className="kpi"><div className="label">{label}</div><div className="value">{value}</div><div className="sub">{subtext}</div></div>;
}

function Bars({ title, rows }: { title: string; rows: BarStat[] }) {
  return <div className="card"><h4>{title}</h4>{rows.length ? rows.map(r => <div className="bar-row" key={r.label}><span>{r.label}</span><div className="bar"><span style={{ width: `${Math.round(r.ratio * 100)}%` }} /></div><span>{r.valueText}</span></div>) : <span className="muted">No data</span>}</div>;
}

function VolumeByHourChart({ rows }: { rows: HourCategory[] }) {
  const hours = Array.from({ length: 24 }, (_, i) => i);
  const byCategory = categories.map(category => ({
    category,
    values: hours.map(hour => rows.find(r => r.hour === hour && r.category === category)?.count ?? 0)
  }));
  const max = Math.max(1, ...byCategory.flatMap(c => c.values));
  const points = (values: number[]) => values
    .map((value, hour) => `${36 + hour * 19},${158 - value / max * 118}`)
    .join(" ");
  return <div className="card chart-card"><h4>Calls by Hour and Category</h4><div className="chart-with-legend"><svg className="chart" viewBox="0 0 500 190" preserveAspectRatio="xMinYMin meet" role="img" aria-label="Calls by hour and category"><line className="axis" x1="32" y1="158" x2="482" y2="158" /><line className="axis" x1="32" y1="28" x2="32" y2="158" />{[0, 6, 12, 18, 23].map(hour => <text className="chart-label" x={36 + hour * 19} y="178" key={hour}>{hour}</text>)}<text className="chart-label" x="4" y="34">{max}</text>{byCategory.map(series => <polyline key={series.category} fill="none" stroke={categoryColors[series.category]} strokeWidth="2.5" points={points(series.values)} />)}</svg><Legend items={byCategory.map(c => [label(c.category), categoryColors[c.category]])} /></div></div>;
}

function QualityByHourChart({ rows }: { rows: QualityHour[] }) {
  const hours = Array.from({ length: 24 }, (_, i) => i);
  const keys: (keyof Omit<QualityHour, "hour">)[] = ["inaudible", "short", "empty", "failure"];
  const totals = hours.map(hour => {
    const row = rows.find(r => r.hour === hour);
    return row ? keys.reduce((sum, key) => sum + row[key], 0) : 0;
  });
  const max = Math.max(1, ...totals);
  return <div className="card chart-card"><h4>Quality Problems by Hour</h4><svg className="chart" viewBox="0 0 500 190" preserveAspectRatio="xMinYMin meet" role="img" aria-label="Quality problems by hour"><line className="axis" x1="32" y1="158" x2="482" y2="158" /><line className="axis" x1="32" y1="28" x2="32" y2="158" /><text className="chart-label" x="4" y="34">{max}</text>{[0, 6, 12, 18, 23].map(hour => <text className="chart-label" x={36 + hour * 19} y="178" key={hour}>{hour}</text>)}{hours.map(hour => {
    const row = rows.find(r => r.hour === hour);
    let y = 158;
    return keys.map(key => {
      const value = row?.[key] ?? 0;
      const height = value / max * 118;
      y -= height;
      return <rect key={`${hour}-${key}`} x={31 + hour * 19} y={y} width="11" height={height} fill={qualityColors[key]} />;
    });
  })}</svg><Legend items={keys.map(k => [label(k), qualityColors[k]])} /></div>;
}

function Legend({ items }: { items: string[][] }) {
  return <div className="legend">{items.map(([name, color]) => <span key={name}><i style={{ background: color }} />{name}</span>)}</div>;
}

function LocationHeatMap({ rows, focusedKey, onFocusKey }: { rows: LocationHeat[]; focusedKey?: string | null; onFocusKey?: (key: string | null) => void }) {
  const mapRef = useRef<HTMLDivElement | null>(null);
  const defaultCenter = useMemo(() => defaultMapCenter(rows), [rows]);
  const areaKey = useMemo(() => Array.from(new Set(rows.map(row => row.areaId))).sort().join("|"), [rows]);
  const lastAreaKey = useRef(areaKey);
  const [mapSize, setMapSize] = useState({ width: 760, height: 520 });
  const [zoom, setZoom] = useState(9);
  const [center, setCenter] = useState(defaultCenter);
  const [selected, setSelected] = useState<LocationHeat | null>(null);
  useEffect(() => {
    const element = mapRef.current;
    if (!element) return;
    const update = () => setMapSize({ width: Math.max(320, element.clientWidth), height: Math.max(260, element.clientHeight) });
    update();
    const observer = new ResizeObserver(update);
    observer.observe(element);
    return () => observer.disconnect();
  }, []);
  useEffect(() => {
    if (areaKey === lastAreaKey.current) return;
    lastAreaKey.current = areaKey;
    setCenter(defaultMapCenter(rows));
    setZoom(9);
    setSelected(null);
  }, [areaKey, rows]);
  useEffect(() => {
    if (!focusedKey) return;
    const row = rows.find(r => locationKey(r) === focusedKey);
    if (row) focusLocation(row);
  }, [focusedKey, rows]);

  if (!rows.length) {
    return <div className="card location-heat-card">
      <p className="muted">No geolocated incidents detected in the selected range.</p>
    </div>;
  }
  const viewport = buildMapViewport(zoom, center, mapSize);
  const tiles = mapTiles(viewport);
  const points = rows.map(row => ({ row, point: projectHeatPoint(row, viewport) }));

  function focusLocation(row: LocationHeat) {
    setSelected(row);
    setCenter(approximateHeatLatLon(row));
    setZoom(current => Math.max(current, 12));
    onFocusKey?.(locationKey(row));
  }

  function handleWheel(event: React.WheelEvent<HTMLDivElement>) {
    event.preventDefault();
    setZoom(current => Math.max(8, Math.min(14, current + (event.deltaY < 0 ? 1 : -1))));
  }

  return <div className="card location-heat-card">
    <div className="location-heat-note">Incidents are plotted from geocoded source-call location references within the monitored system area. Popup details show the matched geocoder result and source calls.</div>
    <div className={`location-map-shell ${selected ? "has-selection" : ""}`}>
    <div className="location-map" ref={mapRef} role="img" aria-label="Geolocated incident map" onWheel={handleWheel}>
      <div className="map-zoom-controls"><button onClick={() => setZoom(current => Math.min(14, current + 1))} aria-label="Zoom in">+</button><button onClick={() => setZoom(current => Math.max(8, current - 1))} aria-label="Zoom out">-</button><span>{zoom}</span></div>
      {tiles.map(tile => <img
        src={`https://tile.openstreetmap.org/${tile.z}/${tile.x}/${tile.y}.png`}
        style={{ left: tile.left, top: tile.top }}
        alt=""
        draggable={false}
        key={`${tile.z}-${tile.x}-${tile.y}`}
      />)}
      {Object.entries(monitoredAreaBounds).map(([areaId, bounds]) => {
        const box = projectBounds(bounds, viewport);
        const label = rows.find(row => row.areaId === areaId)?.areaLabel;
        if (!label) return null;
        return <div className="map-area-box" style={{ left: box.left, top: box.top, width: box.width, height: box.height }} key={areaId}>
          <span>{label}</span>
        </div>;
      })}
      {points.map(({ row, point }) => {
        const size = 22 + row.intensity * 36;
        return <button
          className={`heat-dot map-heat-dot category-${row.category || "other"} ${selected && locationKey(selected) === locationKey(row) ? "active" : ""}`}
          style={{ left: `${point.x}%`, top: `${point.y}%`, width: size, height: size }}
          title={`${row.locationText}: ${row.count} call${row.count === 1 ? "" : "s"}; latest ${new Date(row.lastHeard * 1000).toLocaleString()}; calls ${row.callIds.join(", ")}`}
          onClick={() => focusLocation(row)}
          key={`${row.areaId}-${row.locationText}`}
        >
          <span>{row.count}</span>
        </button>;
      })}
      <a className="map-attribution" href="https://www.openstreetmap.org/copyright" target="_blank" rel="noreferrer">OpenStreetMap</a>
    </div>
    {selected && <div className="map-popup side-panel">
      <button className="map-popup-close" aria-label="Close location details" onClick={() => { setSelected(null); onFocusKey?.(null); }}>x</button>
        <strong>{selected.locationText}</strong>
        <span>{selected.areaLabel} / {label(selected.category)}</span>
        <span>{selected.count} call{selected.count === 1 ? "" : "s"}; latest {relativeTime(selected.lastHeard)}</span>
        <span className="map-geocode">Matched: {selected.geocodeDisplayName}</span>
        <span className="muted">{selected.geocodeProvider} / {selected.geocodePrecision} / {(selected.geocodeConfidence * 100).toFixed(0)}% confidence</span>
      {selected.incidentLinks?.length > 0
        ? <div className="map-popup-incidents">{selected.incidentLinks.map(link => <a href={`#incident-${link.incidentId}`} key={link.incidentId}>{link.title}</a>)}</div>
        : <span className="muted">No incident currently contains these exact source calls.</span>}
      {selected.sourceCalls?.length > 0 && <div className="map-popup-calls">
        {selected.sourceCalls.map(call => <div key={call.callId}>
          <strong>Call {call.callId}</strong>
          <span>{relativeTime(call.rawTimestamp)} / {label(call.category)} / {call.talkgroupName}</span>
          <p>{call.transcript}</p>
          {call.audioUrl && <audio controls preload="metadata" src={call.audioUrl} />}
        </div>)}
      </div>}
      <span className="muted">Calls: {selected.callIds.join(", ")}</span>
    </div>}
    </div>
    <div className="location-heat-list">
      {rows.slice(0, 8).map(row => <button type="button" onClick={() => focusLocation(row)} key={`${row.areaId}-${row.locationText}-list`}>
        <strong>{row.locationText}</strong>
        <span>{relativeTime(row.lastHeard)}</span>
        <span>{row.count} call{row.count === 1 ? "" : "s"}</span>
        {row.incidentLinks?.length > 0 && <span className="location-incident-badge">Incident</span>}
      </button>)}
    </div>
  </div>;
}

type GeoBounds = { north: number; south: number; west: number; east: number };
type GeoPoint = { lat: number; lon: number };
type MapViewport = { zoom: number; width: number; height: number; centerWorldX: number; centerWorldY: number };
const monitoredAreaBounds: Record<string, GeoBounds> = {
  "hamilton-county-tn": { north: 35.47, south: 34.98, west: -85.47, east: -84.98 },
  "bradley-county-tn": { north: 35.33, south: 34.90, west: -85.10, east: -84.55 },
  "cleveland-tn": { north: 35.24, south: 35.07, west: -84.96, east: -84.78 }
};

function defaultMapCenter(rows: LocationHeat[]): GeoPoint {
  const bounds = rows
    .map(row => monitoredAreaBounds[row.areaId])
    .filter(Boolean);
  if (!bounds.length)
    return { lat: 35.18, lon: -85.02 };
  const north = Math.max(...bounds.map(b => b.north), 35.47);
  const south = Math.min(...bounds.map(b => b.south), 34.90);
  const west = Math.min(...bounds.map(b => b.west), -85.47);
  const east = Math.max(...bounds.map(b => b.east), -84.55);
  return { lat: (north + south) / 2, lon: (west + east) / 2 };
}

function buildMapViewport(zoom: number, centerPoint: GeoPoint, size: { width: number; height: number }): MapViewport {
  const center = latLonToWorld(centerPoint.lat, centerPoint.lon, zoom);
  return { zoom, width: size.width, height: size.height, centerWorldX: center.x, centerWorldY: center.y };
}

function mapTiles(viewport: MapViewport) {
  const tileSize = 256;
  const startX = Math.floor((viewport.centerWorldX - viewport.width / 2) / tileSize);
  const endX = Math.floor((viewport.centerWorldX + viewport.width / 2) / tileSize);
  const startY = Math.floor((viewport.centerWorldY - viewport.height / 2) / tileSize);
  const endY = Math.floor((viewport.centerWorldY + viewport.height / 2) / tileSize);
  const tiles: { x: number; y: number; z: number; left: number; top: number }[] = [];
  const max = 2 ** viewport.zoom;
  for (let x = startX; x <= endX; x++) {
    for (let y = startY; y <= endY; y++) {
      if (y < 0 || y >= max) continue;
      tiles.push({
        x: ((x % max) + max) % max,
        y,
        z: viewport.zoom,
        left: Math.round(x * tileSize - viewport.centerWorldX + viewport.width / 2),
        top: Math.round(y * tileSize - viewport.centerWorldY + viewport.height / 2)
      });
    }
  }
  return tiles;
}

function projectHeatPoint(row: LocationHeat, viewport: MapViewport) {
  const world = latLonToWorldPoint(approximateHeatLatLon(row), viewport.zoom);
  return {
    x: (world.x - viewport.centerWorldX + viewport.width / 2) / viewport.width * 100,
    y: (world.y - viewport.centerWorldY + viewport.height / 2) / viewport.height * 100
  };
}

function approximateHeatLatLon(row: LocationHeat): GeoPoint {
  return { lat: row.latitude, lon: row.longitude };
}

function latLonToWorldPoint(point: GeoPoint, zoom: number) {
  return latLonToWorld(point.lat, point.lon, zoom);
}

function projectBounds(bounds: GeoBounds, viewport: MapViewport) {
  const nw = latLonToWorld(bounds.north, bounds.west, viewport.zoom);
  const se = latLonToWorld(bounds.south, bounds.east, viewport.zoom);
  const left = (nw.x - viewport.centerWorldX + viewport.width / 2) / viewport.width * 100;
  const top = (nw.y - viewport.centerWorldY + viewport.height / 2) / viewport.height * 100;
  const right = (se.x - viewport.centerWorldX + viewport.width / 2) / viewport.width * 100;
  const bottom = (se.y - viewport.centerWorldY + viewport.height / 2) / viewport.height * 100;
  return { left: `${left}%`, top: `${top}%`, width: `${right - left}%`, height: `${bottom - top}%` };
}

function latLonToWorld(lat: number, lon: number, zoom: number) {
  const sinLat = Math.sin(lat * Math.PI / 180);
  const scale = 256 * 2 ** zoom;
  return {
    x: (lon + 180) / 360 * scale,
    y: (0.5 - Math.log((1 + sinLat) / (1 - sinLat)) / (4 * Math.PI)) * scale
  };
}

function TopTalkgroups({ rows }: { rows: TopTalkgroup[] }) {
  return <table className="table top-talkgroups"><thead><tr><th>Talkgroup</th><th>Calls</th><th>Share</th><th>Trend</th></tr></thead><tbody>{rows.map(r => <tr key={r.talkgroup}><td>{r.label}</td><td>{r.count}</td><td>{(r.share * 100).toFixed(1)}%</td><td><div className="trend-bars" aria-label={`${r.label} trend, ${r.trendBucketLabel}`}>{r.trend.map((v, i) => <span className="trend" title={`${r.trendLabels?.[i] ?? "Bucket"}: ${r.trendCounts?.[i] ?? 0} calls`} style={{ height: 4 + v * 30 }} key={i} />)}</div><div className="muted">Hourly volume; hover bars for counts</div></td></tr>)}</tbody></table>;
}

function Alerts({ rows }: { rows: AlertMatch[] }) {
  if (!rows.length) return <p className="muted">No alert matches in selected range.</p>;
  return <div className="alerts-list">{rows.map(a => {
    const match = alertMatchText(a.detail);
    return <div className={`alert-card category-${a.category}`} key={a.id}>
      <div className="alert-title">
        <strong>{a.ruleName || "Alert"}</strong>
        <span>{new Date(a.matchedAt * 1000).toLocaleString()}</span>
      </div>
      <div className="alert-meta">
        <span>{alertDetailLabel(a.detail)}</span>
        <span>{a.talkgroupName || `TG ${a.talkgroup}`}</span>
        <span>{a.systemShortName || "unknown system"}</span>
        <span>Call {a.callId}</span>
      </div>
      <div className="alert-badges">
        {a.isImported && <span className="pill">Imported</span>}
        {a.notificationSuppressed && <span className="pill">Notification suppressed</span>}
        {a.qualityReason && a.qualityReason !== "ok" && <span className="pill">{a.transcriptionStatus}: {a.qualityReason}</span>}
      </div>
      <p className="alert-transcript">{highlightText(a.transcription || "No transcript available.", match)}</p>
      {a.audioUrl && <audio controls preload="metadata" src={a.audioUrl} />}
    </div>;
  })}</div>;
}

function Incidents({ rows, locationMap, onShowLocation }: { rows: Incident[]; locationMap?: Map<number, LocationHeat>; onShowLocation?: (key: string) => void }) {
  const [expanded, setExpanded] = useState(false);
  if (!rows.length) return <div className="card"><p className="muted">No incidents detected.</p></div>;
  const sortedRows = sortIncidents(rows);
  return <div className="incident-explorer">
    <div className="incident-toolbar">
      <strong>Recent Incidents <span className="muted">{sortedRows.length}</span></strong>
      <button onClick={() => setExpanded(v => !v)}>{expanded ? "Collapse All" : "Expand All"}</button>
    </div>
    {sortedRows.map(i => {
      const linkedLocation = locationMap?.get(i.id);
      return <details id={`incident-${i.id}`} className={`incident-card category-${i.category || "other"}`} key={i.id} open={expanded}>
      <IncidentSummary incident={i} linkedLocation={linkedLocation} onShowLocation={onShowLocation} />
      <p>{i.detail}</p>
      <div className="incident-details">
        <div className="muted">Related calls</div>
        {i.calls.map(c => <IncidentSourceCall call={c} key={c.callId} />)}
      </div>
    </details>;
    })}
  </div>;
}

function CategoryView({ data, mode }: { data: CategoryPage | null; mode: CategoryViewMode }) {
  if (!data) return <div className="category-page">Loading...</div>;
  return <div className="category-split-page" data-category={data.category}>
    <section className="pane insights-pane category-pane">
      {mode === "raw" && <><h2>{label(data.category)} Raw Calls</h2><RawCallList groups={data.groups} /></>}
      {mode === "incidents" && <><h2>{label(data.category)} Incidents</h2><CategoryIncidents rows={data.incidents} category={data.category} /></>}
    </section>
    <section className="pane calls-pane category-pane">
      <h2>Calls by Talkgroup</h2>
      <CategoryCallGroups groups={data.groups} category={data.category} />
    </section>
  </div>;
}

function CategoryIncidents({ rows, category }: { rows: Incident[]; category: string }) {
  if (!rows.length) return <div className="card"><p className="muted">No incidents available for this category and time range.</p></div>;
  const sortedRows = sortIncidents(rows);
  return <div className="incident-explorer category-incident-list">
    {sortedRows.map(i => <details className={`incident-card category-${category}`} key={i.id}>
      <IncidentSummary incident={i} />
      <p>{i.detail}</p>
      <div className="incident-details">
        <div className="muted">Related calls across all categories</div>
        {i.calls.map(c => <IncidentSourceCall call={c} key={c.callId} />)}
      </div>
    </details>)}
  </div>;
}

function IncidentSourceCall({ call }: { call: Incident["calls"][number] }) {
  const category = call.category || "other";
  const transcript = call.transcript?.trim();
  return <div className={`incident-call category-${category}`}>
    <div className="incident-call-head">
      <span>{new Date(call.rawTimestamp * 1000).toLocaleString()}</span>
      <span>{label(category)} / Call {call.callId}</span>
    </div>
    <div className="transcript-block">{transcript || "No transcript stored for this source call."}</div>
    <audio controls preload="metadata" src={call.audioUrl} />
  </div>;
}

function IncidentSummary({ incident, linkedLocation, onShowLocation }: { incident: Incident; linkedLocation?: LocationHeat; onShowLocation?: (key: string) => void }) {
  return <summary>
    <span className="incident-title">{incident.title}</span>
    <span className="incident-summary-meta">
      {linkedLocation && <button type="button" className="geo-badge" title={`Show ${linkedLocation.locationText} on map`} onClick={event => { event.preventDefault(); event.stopPropagation(); onShowLocation?.(locationKey(linkedLocation)); }}>Map</button>}
      {incident.status && incident.status !== "active" && <span className="pill">{label(incident.status)}</span>}
      <span className="incident-time">{relativeIncidentTime(incident)}</span>
      <span className="muted">{incident.calls.length} calls</span>
      <strong className={`confidence confidence-circle ${confidenceClass(incident.confidence)}`}>{Math.round(incident.confidence * 100)}</strong>
    </span>
  </summary>;
}

function CategoryCallGroups({ groups, category }: { groups: CategoryPage["groups"]; category?: string }) {
  if (!groups.length) return <div className="card"><p className="muted">No raw calls available for this category.</p></div>;
  return <>{groups.map(group => <CollapsibleCallGroup group={group} category={category} key={group.label} />)}</>;
}

function RawCallList({ groups }: { groups: CategoryPage["groups"] }) {
  const calls = groups.flatMap(g => g.calls).sort((a, b) => b.startTime - a.startTime);
  if (!calls.length) return <div className="card"><p className="muted">No raw calls available for this category.</p></div>;
  return <>{calls.map(call => <CallRow call={call} key={call.id} />)}</>;
}

function CollapsibleCallGroup({ group, category }: { group: CategoryPage["groups"][number]; category?: string }) {
  const [open, setOpen] = useState(false);
  const groupCategory = category ?? group.calls[0]?.category ?? "other";
  return <details className={`call-group category-${groupCategory}`} open={open} onToggle={e => setOpen(e.currentTarget.open)}><summary><span>{group.label}</span><span className="muted">{group.calls.length} calls</span></summary>{open && group.calls.map(c => <CallRow call={c} key={c.id} />)}</details>;
}

function CallRow({ call }: { call: EngineCall }) {
  const status = call.qualityReason && call.qualityReason !== "ok" ? `${call.transcriptionStatus}: ${call.qualityReason}` : call.transcriptionStatus;
  const transcript = call.transcription?.trim();
  const missingText = call.transcriptionStatus === "pending"
    ? "Pending transcription"
    : `No transcript available (${status || "not transcribed"}).`;
  return <div className={`call category-${call.category}`}><div className="call-head"><strong>{call.talkgroupName || `TG ${call.talkgroup}`}</strong><span>{new Date(call.startTime * 1000).toLocaleString()}</span><span>{status}</span>{call.isImported && <span className="pill">Imported</span>}</div><div>{transcript || missingText}</div>{call.audioPath && <audio controls preload="metadata" src={`/api/v1/calls/${call.id}/audio`} />}</div>;
}

function sortIncidents(rows: Incident[]) {
  return [...rows].sort((a, b) => (b.lastSeen - a.lastSeen) || (b.confidence - a.confidence));
}

function incidentTimeRange(incident: Incident) {
  const first = new Date(incident.firstSeen * 1000);
  const last = new Date(incident.lastSeen * 1000);
  if (first.toDateString() === last.toDateString()) {
    return `${first.toLocaleString()} - ${last.toLocaleTimeString()}`;
  }
  return `${first.toLocaleString()} - ${last.toLocaleString()}`;
}

function relativeIncidentTime(incident: Incident) {
  return relativeTime(incident.lastSeen);
}

function relativeTime(unixSeconds: number) {
  const seconds = Math.max(0, Math.floor(Date.now() / 1000) - unixSeconds);
  if (seconds < 60) return "just now";
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes} minute${minutes === 1 ? "" : "s"} ago`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours} hour${hours === 1 ? "" : "s"} ago`;
  const days = Math.floor(hours / 24);
  if (days < 30) return `${days} day${days === 1 ? "" : "s"} ago`;
  const months = Math.floor(days / 30);
  if (months < 12) return `${months} month${months === 1 ? "" : "s"} ago`;
  const years = Math.floor(days / 365);
  return `${years} year${years === 1 ? "" : "s"} ago`;
}

function alertMatchText(detail: string) {
  const value = detail || "";
  const separator = value.indexOf(":");
  return separator >= 0 ? value.slice(separator + 1).trim() : value.trim();
}

function alertDetailLabel(detail: string) {
  const value = detail || "alert match";
  const separator = value.indexOf(":");
  if (separator < 0) return value;
  const kind = value.slice(0, separator).trim();
  const match = value.slice(separator + 1).trim();
  return `${label(kind)}: ${match}`;
}

function highlightText(text: string, needle: string) {
  if (!needle) return text;
  const index = text.toLowerCase().indexOf(needle.toLowerCase());
  if (index < 0) return text;
  return <>
    {text.slice(0, index)}
    <mark>{text.slice(index, index + needle.length)}</mark>
    {text.slice(index + needle.length)}
  </>;
}

function confidenceClass(score: number) {
  if (score >= 0.75) return "confidence-high";
  if (score >= 0.45) return "confidence-mid";
  return "confidence-low";
}

function SystemView({ data, jobs, rangeHours, reload, engineHealth, recommendations, setRecommendations }: { data: TrTroubleshoot | null; jobs: Job[]; rangeHours: number; reload: () => Promise<void>; engineHealth: EngineHealth | null; recommendations: SystemRecommendations | null; setRecommendations: (value: SystemRecommendations | null) => void }) {
  const [topTab, setTopTab] = useState<"recommendations" | "pizzad" | "tr" | "tokens">("recommendations");
  const [pizzadTab, setPizzadTab] = useState<"service" | "storage" | "imports" | "jobs" | "quality">("service");
  const [trTab, setTrTab] = useState<"summary" | "metrics" | "rf" | "logs" | "insights">("summary");
  const [bySystem, setBySystem] = useState(false);
  const [baseline, setBaseline] = useState("7d");
  const [metricsData, setMetricsData] = useState<TrTroubleshoot | null>(null);
  const [runtime, setRuntime] = useState<any | null>(null);
  const [tokenUsage, setTokenUsage] = useState<TokenUsageReport | null>(null);
  const [insightText, setInsightText] = useState("");
  const [insightBusy, setInsightBusy] = useState(false);
  const [restartBusy, setRestartBusy] = useState<"" | "pizzad" | "trunk-recorder">("");
  const [restartMessages, setRestartMessages] = useState<Record<string, string>>({});
  const [ingestBusy, setIngestBusy] = useState(false);
  const [ingestMessage, setIngestMessage] = useState("");
  const [recommendationBusy, setRecommendationBusy] = useState(false);

  useEffect(() => {
    if (topTab !== "pizzad") return;
    void api.request<any>("/api/v1/system/runtime").then(setRuntime).catch(() => setRuntime(null));
  }, [topTab, pizzadTab, jobs.length]);
  useEffect(() => {
    if (topTab !== "tr" || trTab !== "metrics") return;
    void api.request<TrTroubleshoot>(`/api/v1/troubleshoot?${rangeQuery(rangeHours)}&bySystem=${bySystem}&baseline=${baseline}`)
      .then(setMetricsData)
      .catch(() => setMetricsData(null));
  }, [topTab, trTab, bySystem, baseline, rangeHours]);
  useEffect(() => {
    if (topTab !== "tokens") return;
    void api.request<TokenUsageReport>(`/api/v1/system/token-usage?${rangeQuery(rangeHours)}`).then(setTokenUsage).catch(() => setTokenUsage(null));
  }, [topTab, rangeHours]);

  if (!data) return <div className="trouble-page">Loading system data...</div>;
  const active = metricsData ?? data;
  async function generateTroubleshootInsights() {
    setInsightBusy(true);
    try {
      const response = await api.request<{ text: string }>("/api/v1/troubleshoot/insights", {
        method: "POST",
        body: JSON.stringify({ ...rangeBody(rangeHours), bySystem, baseline })
      });
      setInsightText(response.text);
    } catch (error) {
      setInsightText(error instanceof Error ? error.message : String(error));
    } finally {
      setInsightBusy(false);
    }
  }
  async function restartService(service: "pizzad" | "trunk-recorder") {
    setRestartBusy(service);
    setRestartMessages(current => ({ ...current, [service]: `Restarting ${service === "pizzad" ? "pizzad" : "trunk-recorder"}...` }));
    try {
      const job = await api.request<Job>(`/api/v1/system/services/${service}/restart`, { method: "POST" });
      setRestartMessages(current => ({ ...current, [service]: `Restart queued as job ${job.id}.` }));
      setTimeout(() => void reload(), service === "pizzad" ? 6000 : 1500);
    } catch (error) {
      setRestartMessages(current => ({ ...current, [service]: error instanceof Error ? error.message : "Restart failed." }));
    } finally {
      setRestartBusy("");
    }
  }
  async function setIngestPaused(pause: boolean, untilQueueClear = false) {
    setIngestBusy(true);
    setIngestMessage("");
    try {
      const result = await api.request<EngineHealth["ingest"]>("/api/v1/system/ingest", {
        method: "POST",
        body: JSON.stringify({
          pause,
          untilQueueClear,
          reason: untilQueueClear ? "Paused from System until transcription queue clears." : "Paused from System."
        })
      });
      setIngestMessage(result.paused ? "Live ingest is paused. New callstream payloads will be dropped." : "Live ingest resumed.");
      await reload();
    } catch (error) {
      setIngestMessage(error instanceof Error ? error.message : "Failed to update ingest control.");
    } finally {
      setIngestBusy(false);
    }
  }
  async function refreshRecommendations() {
    setRecommendationBusy(true);
    try {
      setRecommendations(await api.request<SystemRecommendations>("/api/v1/system/recommendations"));
    } finally {
      setRecommendationBusy(false);
    }
  }
  function openRecommendationTarget(target: { topTab: string; subTab: string }) {
    if (target.topTab === "pizzad" || target.topTab === "tr" || target.topTab === "tokens" || target.topTab === "recommendations")
      setTopTab(target.topTab);
    if (target.topTab === "pizzad" && ["service", "storage", "imports", "jobs", "quality"].includes(target.subTab))
      setPizzadTab(target.subTab as any);
    if (target.topTab === "tr" && ["summary", "metrics", "tools", "logs", "insights"].includes(target.subTab))
      setTrTab(target.subTab as any);
  }
  async function setRecommendationState(id: string, action: "snooze" | "dismiss" | "clear") {
    setRecommendationBusy(true);
    try {
      setRecommendations(await api.request<SystemRecommendations>(`/api/v1/system/recommendations/${encodeURIComponent(id)}/state`, {
        method: "POST",
        body: JSON.stringify({ action })
      }));
    } finally {
      setRecommendationBusy(false);
    }
  }
  async function applyRecommendation(id: string, action: string, talkgroups?: number[]) {
    setRecommendationBusy(true);
    try {
      const response = await api.request<{ applied: boolean; message: string; recommendations: SystemRecommendations }>(`/api/v1/system/recommendations/${encodeURIComponent(id)}/apply`, {
        method: "POST",
        body: JSON.stringify({ action, talkgroups })
      });
      setRecommendations(response.recommendations);
      setInsightText(response.message);
      await reload();
    } catch (error) {
      setInsightText(error instanceof Error ? error.message : String(error));
    } finally {
      setRecommendationBusy(false);
    }
  }
  return (
    <div className="trouble-page">
      <div className="trouble-tabs">
        <button className={topTab === "recommendations" ? "active" : ""} onClick={() => setTopTab("recommendations")}>Recommendations{recommendations && recommendations.openCount > 0 ? ` (${recommendations.openCount})` : ""}</button>
        <button className={topTab === "pizzad" ? "active" : ""} onClick={() => setTopTab("pizzad")}>Pizzad</button>
        <button className={topTab === "tr" ? "active" : ""} onClick={() => setTopTab("tr")}>Trunk Recorder</button>
        <button className={topTab === "tokens" ? "active" : ""} onClick={() => setTopTab("tokens")}>Token Usage</button>
        <button onClick={() => void reload()}>Refresh</button>
      </div>
      {topTab === "recommendations" && <RecommendationsPanel recommendations={recommendations} busy={recommendationBusy} message={insightText} onRefresh={refreshRecommendations} onOpen={openRecommendationTarget} onState={setRecommendationState} onApply={applyRecommendation} />}
      {topTab === "pizzad" && <div className="trouble-panel">
        <div className="trouble-tabs nested">
          <button className={pizzadTab === "service" ? "active" : ""} onClick={() => setPizzadTab("service")}>Service Manager</button>
          <button className={pizzadTab === "storage" ? "active" : ""} onClick={() => setPizzadTab("storage")}>DB / Storage</button>
          <button className={pizzadTab === "imports" ? "active" : ""} onClick={() => setPizzadTab("imports")}>Imports</button>
          <button className={pizzadTab === "jobs" ? "active" : ""} onClick={() => setPizzadTab("jobs")}>Queue / Jobs</button>
          <button className={pizzadTab === "quality" ? "active" : ""} onClick={() => setPizzadTab("quality")}>Pizzad Quality</button>
        </div>
        {pizzadTab === "service" && <PizzadServiceManager runtime={runtime} restartBusy={restartBusy === "pizzad"} restartMessage={restartMessages.pizzad ?? ""} onRestart={() => restartService("pizzad")} ingestBusy={ingestBusy} ingestMessage={ingestMessage} onSetIngestPaused={setIngestPaused} />}
        {pizzadTab === "storage" && <PizzadStorageManager runtime={runtime} />}
        {pizzadTab === "imports" && <ImportsPanel reload={reload} engineHealth={engineHealth} />}
        {pizzadTab === "jobs" && <JobsPanel jobs={jobs} reload={reload} engineHealth={engineHealth} />}
        {pizzadTab === "quality" && <QualityAuditView data={data} />}
      </div>}
      {topTab === "tr" && <div className="trouble-panel">
        <div className="system-action-bar">
          <div>
            <strong>Trunk Recorder</strong>
            <small>Restart after TR config, source, or calibration changes.</small>
          </div>
          <button className="danger-button" disabled={restartBusy === "trunk-recorder"} onClick={() => void restartService("trunk-recorder")}>{restartBusy === "trunk-recorder" ? "Restarting..." : "Restart Trunk Recorder"}</button>
          {restartMessages["trunk-recorder"] && <span className={restartMessages["trunk-recorder"].includes("failed") || restartMessages["trunk-recorder"].includes("Unsupported") ? "settings-message error" : "settings-message ok"}>{restartMessages["trunk-recorder"]}</span>}
        </div>
        <div className="trouble-tabs nested">
          <button className={trTab === "summary" ? "active" : ""} onClick={() => setTrTab("summary")}>Health Summary</button>
          <button className={trTab === "metrics" ? "active" : ""} onClick={() => setTrTab("metrics")}>Metrics</button>
          <button className={trTab === "rf" ? "active" : ""} onClick={() => setTrTab("rf")}>RF Analysis</button>
          <button className={trTab === "logs" ? "active" : ""} onClick={() => setTrTab("logs")}>Log Output</button>
          <button className={trTab === "insights" ? "active" : ""} onClick={() => setTrTab("insights")}>Insights</button>
        </div>
        {trTab === "summary" && <TrHealthSummaryView data={data} />}
        {trTab === "metrics" && <div>
          <div className="metric-controls">
            <label><input type="checkbox" checked={bySystem} onChange={e => setBySystem(e.target.checked)} /> By system</label>
            <label>Compare against baseline <select value={baseline} onChange={e => setBaseline(e.target.value)}><option>7d</option><option>14d</option><option>30d</option></select></label>
          </div>
          <div className="tr-chart-grid">{active.health.charts.map(c => <TrHealthChartView chart={c} key={c.title} />)}</div>
        </div>}
        {trTab === "rf" && <RfAnalysisPanel data={data} rangeHours={rangeHours} />}
        {trTab === "logs" && <pre className="log-box">{data.logOutput}</pre>}
        {trTab === "insights" && <div className="card">
          <button disabled={insightBusy} onClick={() => void generateTroubleshootInsights()}>{insightBusy ? "Generating..." : "Generate Recommendation"}</button>
          <p className="muted">Sends the current health summary, system rows, chart series, and quality snapshot to the configured AI insights endpoint.</p>
          <pre className="log-box">{insightText || data.insightsText}</pre>
        </div>}
      </div>}
      {topTab === "tokens" && <TokenUsagePanel report={tokenUsage} />}
    </div>
  );
}

function PizzadServiceManager({ runtime, restartBusy, restartMessage, onRestart, ingestBusy, ingestMessage, onSetIngestPaused }: { runtime: any | null; restartBusy: boolean; restartMessage: string; onRestart: () => void; ingestBusy: boolean; ingestMessage: string; onSetIngestPaused: (pause: boolean, untilQueueClear?: boolean) => Promise<void> }) {
  if (!runtime) return <div className="card">Loading service status...</div>;
  const services = [runtime.service?.pizzad, runtime.service?.trunkRecorder].filter(Boolean);
  const ingest = runtime.queues?.ingest;
  const droppedThisPause = Number(ingest?.droppedCallsThisPause ?? 0);
  const droppedTotal = Number(ingest?.droppedCalls ?? 0);
  return <div className="system-manager-grid">
    <div className="system-action-bar">
      <div>
        <strong>Pizzad</strong>
        <small>Restart after settings that affect engine startup, transcription workers, or service environment.</small>
      </div>
      <button className="danger-button" disabled={restartBusy} onClick={onRestart}>{restartBusy ? "Restarting..." : "Restart Pizzad"}</button>
      {restartMessage && <span className={restartMessage.includes("failed") || restartMessage.includes("Unsupported") ? "settings-message error" : "settings-message ok"}>{restartMessage}</span>}
    </div>
    <div className="system-action-bar">
      <div>
        <strong>Live Ingest</strong>
        <small>{ingest?.paused ? `Paused: ${ingest.reason || "new live calls are being dropped"}` : "Accepting callstream payloads from trunk-recorder."}</small>
      </div>
      {ingest?.paused
        ? <button disabled={ingestBusy} onClick={() => void onSetIngestPaused(false)}>{ingestBusy ? "Updating..." : "Resume Live Ingest"}</button>
        : <>
          <button className="danger-button" disabled={ingestBusy} onClick={() => void onSetIngestPaused(true, true)}>{ingestBusy ? "Updating..." : "Pause Until Queue Clear"}</button>
          <button className="danger-button" disabled={ingestBusy} onClick={() => void onSetIngestPaused(true, false)}>Pause Live Ingest</button>
        </>}
      <span className={ingest?.paused ? "section-status error" : "section-status ok"}>{ingest?.paused ? `${droppedThisPause.toLocaleString()} dropped this pause` : "Running"}</span>
      {ingestMessage && <span className={ingestMessage.toLowerCase().includes("fail") ? "settings-message error" : "settings-message ok"}>{ingestMessage}</span>}
    </div>
    <div className="audit-kpis">
      <Kpi label="Pizzad" value={runtime.service?.pizzad?.active || "unknown"} subtext={runtime.service?.pizzad?.enabled || "systemd enabled state"} />
      <Kpi label="TR Service" value={runtime.service?.trunkRecorder?.active || "unknown"} subtext={runtime.service?.trunkRecorder?.unit || "configured trunk-recorder unit"} />
      <Kpi label="CPU Time" value={`${Number(runtime.process?.totalProcessorTimeSeconds || 0).toFixed(0)}s`} subtext={`${runtime.process?.threadCount ?? 0} thread(s)`} />
      <Kpi label="Memory" value={formatBytes(runtime.process?.workingSetBytes || 0)} subtext={`PID ${runtime.process?.pid ?? "--"}`} />
      <Kpi label="Live Ingest" value={ingest?.paused ? "Paused" : "Running"} subtext={ingest?.paused ? `${droppedThisPause.toLocaleString()} dropped this pause, ${droppedTotal.toLocaleString()} total; ${ingest.untilQueueClear ? "auto-resume at clear" : "manual resume"}` : "callstream accepted"} />
      <Kpi label="Queue Health" value={runtime.queues?.underPressure ? "Pressure" : runtime.queues?.transcriptionQueueDepth > 0 ? "Draining" : "OK"} subtext={`${(runtime.queues?.pendingTranscriptions ?? 0).toLocaleString()} pending, ${(runtime.queues?.priorityLiveQueueDepth ?? 0).toLocaleString()} priority`} />
      <Kpi label="Transcription Rate" value={`${Number(runtime.queues?.recentCallsTranscribed || 0).toFixed(0)} / ${Number(runtime.queues?.recentCallsIngested || 0).toFixed(0)}`} subtext={`${Number(runtime.queues?.recentTranscribedPerMinute || 0).toFixed(1)}/min done vs ${Number(runtime.queues?.recentIngestPerMinute || 0).toFixed(1)}/min in`} />
      <Kpi label="Transcription Latency" value={`${Number(runtime.queues?.averageTranscriptionSeconds || 0).toFixed(1)}s`} subtext={`${Number(runtime.queues?.averageAudioSeconds || 0).toFixed(1)}s audio avg; ${Number(runtime.queues?.averageTranscriptionRealtimeFactor || 0).toFixed(1)}x realtime`} />
      <Kpi label="Whisper Shape" value={`${runtime.queues?.liveTranscriptionWorkers ?? 0} x ${runtime.queues?.whisperThreadsPerWorker ?? 0}`} subtext="workers x threads per worker" />
    </div>
    <div className="card">
      <h3>Services</h3>
      <table className="table"><thead><tr><th>Unit</th><th>Active</th><th>Enabled</th><th>Substate</th><th>Main PID</th><th>Started</th></tr></thead><tbody>{services.map((svc: any) => <tr key={svc.unit}>
        <td>{svc.unit}</td>
        <td><span className={`job-status ${svc.ok ? "status-completed" : "status-failed"}`}>{svc.active || "unknown"}</span></td>
        <td>{svc.enabled || "--"}</td>
        <td>{svc.detail?.SubState || "--"}</td>
        <td>{svc.detail?.MainPID || "--"}</td>
        <td>{svc.detail?.ActiveEnterTimestamp || "--"}</td>
      </tr>)}</tbody></table>
    </div>
  </div>;
}

function RecommendationsPanel({ recommendations, busy, message, onRefresh, onOpen, onState, onApply }: { recommendations: SystemRecommendations | null; busy: boolean; message: string; onRefresh: () => Promise<void>; onOpen: (target: { topTab: string; subTab: string }) => void; onState: (id: string, action: "snooze" | "dismiss" | "clear") => Promise<void>; onApply: (id: string, action: string, talkgroups?: number[]) => Promise<void> }) {
  const [activeRunbookId, setActiveRunbookId] = useState<string | null>(null);
  if (!recommendations) return <div className="card">Loading recommendations...</div>;
  const counts = `${recommendations.highCount} high, ${recommendations.mediumCount} medium, ${recommendations.lowCount} low`;
  const activeRunbook = recommendations.items.find(item => item.id === activeRunbookId);
  return <div className="trouble-panel recommendations-panel">
    <div className="card recommendation-summary">
      <div>
        <h3>System Recommendations</h3>
        <p className="muted">Deterministic recommendations from queue pressure, audio load, TR health, ingest state, imports, and AI gating.</p>
      </div>
      <Kpi label="Open" value={recommendations.openCount.toString()} subtext={counts} />
      <button disabled={busy} onClick={() => void onRefresh()}>{busy ? "Refreshing..." : "Refresh Recommendations"}</button>
    </div>
    {message && <div className={message.toLowerCase().includes("fail") || message.toLowerCase().includes("error") ? "settings-message error" : "settings-message ok"}>{message}</div>}
    {activeRunbook?.runbook && <RunbookDetail item={activeRunbook} busy={busy} onClose={() => setActiveRunbookId(null)} onOpen={onOpen} onApply={onApply} onState={onState} />}
    {!activeRunbook && recommendations.items.length === 0 ? <div className="card"><h3>No Open Recommendations</h3><p className="muted">No system-level issues or priority candidates were detected.</p></div> :
      !activeRunbook &&
      <div className="recommendation-list">
        {recommendations.items.map(item => <div className={`card recommendation-card severity-${item.severity}`} key={item.id}>
          <div className="recommendation-head">
            <span className={`recommendation-severity ${item.severity}`}>{item.severity}</span>
            <span className="muted">{label(item.section)}</span>
          </div>
          <h3>{item.title}</h3>
          <p>{item.detail}</p>
          <div className="recommendation-action">{item.action}</div>
          {(item.runbook || item.actions?.some(action => action.kind !== "open")) && <div className="recommendation-buttons">
            {item.runbook && <button disabled={busy} onClick={() => setActiveRunbookId(item.id)}>Troubleshoot Now</button>}
            {item.actions.filter(action => action.kind !== "open").map(action => <button disabled={busy} key={action.kind} onClick={() => void onApply(item.id, action.kind)}>{action.label}</button>)}
          </div>}
          <div className="recommendation-buttons muted-actions">
            <button disabled={busy} onClick={() => void onState(item.id, "snooze")}>Snooze 24h</button>
            <button disabled={busy} onClick={() => void onState(item.id, "dismiss")}>Dismiss</button>
          </div>
        </div>)}
      </div>}
  </div>;
}

function RunbookDetail({ item, busy, onClose, onOpen, onApply, onState }: { item: NonNullable<SystemRecommendations["items"][number]>; busy: boolean; onClose: () => void; onOpen: (target: { topTab: string; subTab: string }) => void; onApply: (id: string, action: string, talkgroups?: number[]) => Promise<void>; onState: (id: string, action: "snooze" | "dismiss" | "clear") => Promise<void> }) {
  const runbook = item.runbook;
  const [selectedTalkgroups, setSelectedTalkgroups] = useState<number[]>([]);
  useEffect(() => {
    if (!runbook?.talkgroupCandidates?.length) return;
    setSelectedTalkgroups(runbook.talkgroupCandidates.filter(tg => !tg.alreadyDeferred).map(tg => tg.talkgroup));
  }, [item.id, runbook?.talkgroupCandidates]);
  if (!runbook) return null;
  const deferAction = item.actions?.find(action => action.kind === "apply-defer-talkgroups");
  const toggleTalkgroup = (talkgroup: number, checked: boolean) => {
    setSelectedTalkgroups(current => checked ? Array.from(new Set([...current, talkgroup])) : current.filter(tg => tg !== talkgroup));
  };
  return <div className={`card runbook-detail severity-${item.severity}`}>
    <div className="recommendation-head">
      <div>
        <span className={`recommendation-severity ${item.severity}`}>{item.severity}</span>
        <h3>{runbook.title}</h3>
      </div>
      <button onClick={onClose}>Back to Recommendations</button>
    </div>
    <p>{runbook.goal}</p>
    {runbook.evidence.length > 0 && <div className="runbook-evidence">
      <strong>Use this evidence</strong>
      <ul>{runbook.evidence.map(row => <li key={row}>{row}</li>)}</ul>
    </div>}
    {runbook.diagnostics?.length > 0 && <div className="runbook-diagnostics">
      <div className="recommendation-head">
        <div>
          <h3>Current System Snapshot</h3>
          <p className="muted">These values are computed from the running engine, current config, and recent local database samples.</p>
        </div>
      </div>
      <div className="diagnostic-grid">
        {runbook.diagnostics.map(row => <div className={`diagnostic-tile ${row.status}`} key={`${row.label}-${row.value}`}>
          <span>{row.label}</span>
          <strong>{row.value}</strong>
          <p>{row.detail}</p>
        </div>)}
      </div>
    </div>}
    {runbook.talkgroupCandidates?.length > 0 && <div className="runbook-workbench">
      <div className="recommendation-head">
        <div>
          <h3>Talkgroup Priority Workbench</h3>
          <p className="muted">Select the talkgroups to defer. Deferred TGs are still transcribed, but only after priority and normal live calls.</p>
        </div>
        {deferAction && <button disabled={busy || selectedTalkgroups.length === 0} onClick={() => void onApply(item.id, deferAction.kind, selectedTalkgroups)}>{busy ? "Applying..." : `Defer ${selectedTalkgroups.length} Selected`}</button>}
      </div>
      <table className="table runbook-tg-table">
        <thead><tr><th>Defer</th><th>Talkgroup</th><th>Category</th><th>Recent Load</th><th>Pending</th><th>Reason</th></tr></thead>
        <tbody>{runbook.talkgroupCandidates.map(row => <tr key={`${row.systemShortName}-${row.talkgroup}`}>
          <td><input type="checkbox" checked={row.alreadyDeferred || selectedTalkgroups.includes(row.talkgroup)} disabled={row.alreadyDeferred || busy} onChange={e => toggleTalkgroup(row.talkgroup, e.target.checked)} /></td>
          <td>{row.talkgroupName || `TG ${row.talkgroup}`}<br /><span className="muted">{row.systemShortName} / {row.talkgroup}{row.alreadyDeferred ? " / already deferred" : ""}</span></td>
          <td><span className={`category-chip category-${row.category}`}>{label(row.category)}</span></td>
          <td>{formatDurationMinutes(row.audioSeconds / 60)}<br /><span className="muted">{row.calls.toLocaleString()} calls, {row.averageAudioSeconds.toFixed(1)}s avg</span></td>
          <td>{row.pendingCalls.toLocaleString()} calls<br /><span className="muted">{formatDurationMinutes(row.pendingAudioSeconds / 60)}</span></td>
          <td>{row.reason}</td>
        </tr>)}</tbody>
      </table>
    </div>}
    <div className="runbook-steps">
      {runbook.steps.map((step, index) => <div className="runbook-step" key={`${step.title}-${index}`}>
        <span className="runbook-step-index">{index + 1}</span>
        <div>
          <h4>{step.title}</h4>
          <p>{step.detail}</p>
        </div>
      </div>)}
    </div>
    {runbook.caveat && <div className="settings-message">{runbook.caveat}</div>}
    <div className="recommendation-buttons muted-actions">
      <button disabled={busy} onClick={() => void onState(item.id, "snooze")}>Snooze 24h</button>
      <button disabled={busy} onClick={() => void onState(item.id, "dismiss")}>Dismiss</button>
    </div>
  </div>;
}

function PizzadStorageManager({ runtime }: { runtime: any | null }) {
  if (!runtime) return <div className="card">Loading storage status...</div>;
  const tables = Object.entries(runtime.tables ?? {}).sort(([a], [b]) => a.localeCompare(b));
  return <div className="system-manager-grid">
    <div className="audit-kpis">
      <Kpi label="Database" value={formatBytes(runtime.storage?.databaseBytes || 0)} subtext={runtime.storage?.databasePath || "SQLite WAL store"} />
      <Kpi label="Audio Store" value={formatBytes(runtime.storage?.sampledAudioBytes || 0)} subtext={`${(runtime.storage?.sampledAudioFiles || 0).toLocaleString()} sampled file(s)${runtime.storage?.audioSampleTruncated ? " (sample capped)" : ""}`} />
      <Kpi label="Queue" value={(runtime.queues?.transcriptionQueueDepth || 0).toLocaleString()} subtext={`${(runtime.queues?.liveQueueDepth ?? 0).toLocaleString()} live, ${(runtime.queues?.backlogQueueDepth ?? 0).toLocaleString()} backlog`} />
    </div>
    <div className="card">
      <h3>Database Tables</h3>
      <table className="table compact-table"><thead><tr><th>Table</th><th>Rows</th></tr></thead><tbody>{tables.map(([name, count]) => <tr key={name}><td>{name}</td><td>{Number(count).toLocaleString()}</td></tr>)}</tbody></table>
    </div>
  </div>;
}

function TokenUsagePanel({ report }: { report: TokenUsageReport | null }) {
  if (!report) return <div className="trouble-panel"><div className="card">Loading token usage...</div></div>;
  return <div className="trouble-panel token-usage-panel">
    <div className="audit-kpis">
      <Kpi label="Total Tokens" value={formatCompact(report.summary.totalTokens)} subtext={`${report.summary.requests.toLocaleString()} LM request(s)`} />
      <Kpi label="Prompt Tokens" value={formatCompact(report.summary.promptTokens)} subtext="Input/context tokens" />
      <Kpi label="Completion Tokens" value={formatCompact(report.summary.completionTokens)} subtext="Generated output tokens" />
      <Kpi label="Estimated Cost" value={`$${report.summary.estimatedStandardCost.toFixed(2)}`} subtext="Standard estimate at $2/$8 per 1M" />
    </div>
    <div className="tr-chart-grid">
      <TokenBarChart title="Tokens by Day" rows={report.byDay} />
      <TokenBarChart title="Tokens by Activity" rows={report.byTrigger} />
    </div>
    <div className="card">
      <h3>Recorded Usage</h3>
      <p className="muted">{report.ledger}</p>
      <table className="table jobs-table"><thead><tr><th>Time</th><th>Activity</th><th>Status</th><th>Model</th><th>Prompt</th><th>Completion</th><th>Total</th><th>Finish</th></tr></thead><tbody>{report.entries.map(row => <tr key={row.id}>
        <td>{new Date(row.timestampUtc).toLocaleString()}</td>
        <td>{row.triggerActivity}</td>
        <td>{row.success ? "ok" : row.error || "fail"}</td>
        <td>{row.responseModel || row.requestModel}</td>
        <td>{row.promptTokens.toLocaleString()}</td>
        <td>{row.completionTokens.toLocaleString()}</td>
        <td>{(row.totalTokens || row.promptTokens + row.completionTokens).toLocaleString()}</td>
        <td>{row.finishReason}</td>
      </tr>)}</tbody></table>
    </div>
  </div>;
}

function TokenBarChart({ title, rows }: { title: string; rows: { label: string; totalTokens: number; requests: number }[] }) {
  const max = Math.max(1, ...rows.map(r => r.totalTokens));
  return <div className="card audit-table-card"><h3>{title}</h3>{rows.length ? rows.map(row => <div className="bar-row" key={row.label}><span>{row.label}</span><div className="bar"><span style={{ width: `${Math.max(2, row.totalTokens / max * 100)}%` }} /></div><span>{formatCompact(row.totalTokens)} / {row.requests}</span></div>) : <p className="muted">No recorded usage.</p>}</div>;
}

function ImportsPanel({ reload, engineHealth }: { reload: () => Promise<void>; engineHealth: EngineHealth | null }) {
  const blockedReason = engineHealth?.importWorkBlockedReason ?? "";
  return <div className="trouble-panel imports-panel">
    {blockedReason && <div className="settings-message error import-blocker">{blockedReason}</div>}
    <div className="settings-flow">
      <div className="card settings-card wide">
        <div className="settings-card-meta">
          <h3>Local TR Import</h3>
          <p>Imports existing .bin recordings from the active trunk-recorder captureDir on this machine. Source files are read-only; imported calls are copied into the PizzaWave audio store.</p>
        </div>
        <div className="settings-fields"><LocalImport reload={reload} blockedReason={blockedReason} /></div>
      </div>
      <div className="card settings-card wide">
        <div className="settings-card-meta">
          <h3>SFTP Import</h3>
          <p>Imports .bin recordings from a remote SFTP archive. Use this for Synology or other long-term archive stores.</p>
        </div>
        <div className="settings-fields"><SftpImport reload={reload} blockedReason={blockedReason} /></div>
      </div>
    </div>
  </div>;
}

function JobsPanel({ jobs, reload, engineHealth }: { jobs: Job[]; reload: () => Promise<void>; engineHealth: EngineHealth | null }) {
  const [message, setMessage] = useState("");
  const [page, setPage] = useState(1);
  const [queue, setQueue] = useState<QueueSnapshot | null>(null);
  const [queueError, setQueueError] = useState("");
  const [benchmarkBusy, setBenchmarkBusy] = useState(false);
  const [benchmark, setBenchmark] = useState<TranscriptionBenchmarkResult | null>(null);
  const [benchmarkMessage, setBenchmarkMessage] = useState("");
  const pageSize = 12;
  const totalPages = Math.max(1, Math.ceil(jobs.length / pageSize));
  const pageSafe = Math.min(page, totalPages);
  const pageJobs = jobs.slice((pageSafe - 1) * pageSize, pageSafe * pageSize);

  useEffect(() => {
    let canceled = false;
    api.request<QueueSnapshot>("/api/v1/system/queue")
      .then(snapshot => { if (!canceled) { setQueue(snapshot); setQueueError(""); } })
      .catch(error => { if (!canceled) setQueueError(error instanceof Error ? error.message : "Queue load failed"); });
    return () => { canceled = true; };
  }, [engineHealth?.serverTimeUtc, jobs.length]);

  useEffect(() => {
    setPage(current => Math.min(current, totalPages));
  }, [totalPages]);

  async function control(id: number, action: string) {
    setMessage(`${label(action)} job ${id}...`);
    try {
      await api.request(`/api/v1/jobs/${id}/control`, { method: "POST", body: JSON.stringify({ action }) });
      setMessage(`${label(action)} sent for job ${id}`);
      await reload();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : String(error));
    }
  }

  async function deleteJob(id: number) {
    setMessage(`Deleting job ${id}...`);
    try {
      await api.request(`/api/v1/jobs/${id}`, { method: "DELETE" });
      setMessage(`Deleted job ${id}`);
      await reload();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : String(error));
    }
  }

  async function runBenchmark(sampleCount: number) {
    setBenchmarkBusy(true);
    setBenchmarkMessage(`Running ${sampleCount}-call transcription pipeline benchmark...`);
    try {
      const result = await api.request<TranscriptionBenchmarkResult>("/api/v1/system/queue/transcription-benchmark", {
        method: "POST",
        body: JSON.stringify({ sampleCount, lookbackHours: rangeHoursForBenchmark() })
      });
      setBenchmark(result);
      setBenchmarkMessage(`Benchmark complete: ${result.warmCallsPerMinute.toFixed(1)} warm calls/min, ${result.whisperSharePercent.toFixed(0)}% of measured time in Whisper.`);
    } catch (error) {
      setBenchmarkMessage(error instanceof Error ? error.message : String(error));
    } finally {
      setBenchmarkBusy(false);
    }
  }

  return <div className="trouble-panel jobs-panel">
    <QueuePane queue={queue} engineHealth={engineHealth} error={queueError} benchmark={benchmark} benchmarkBusy={benchmarkBusy} benchmarkMessage={benchmarkMessage} onRunBenchmark={runBenchmark} />
    <div className="card jobs-card">
        <div className="jobs-card-head">
          <h3>Jobs</h3>
          <p>Background work created by imports, diagnostics, transcription experiments, and summary generation.</p>
          <span className="muted">{jobs.length.toLocaleString()} total</span>
          {message && <span className={message.toLowerCase().includes("fail") || message.toLowerCase().includes("error") ? "section-status error" : "section-status ok"}>{message}</span>}
        </div>
        <div className="jobs-table-wrap">
          <JobsTable jobs={pageJobs} onControl={control} onDelete={deleteJob} />
        </div>
        <div className="pagination-row">
          <button disabled={pageSafe <= 1} onClick={() => setPage(1)}>First</button>
          <button disabled={pageSafe <= 1} onClick={() => setPage(pageSafe - 1)}>Prev</button>
          <span>Page {pageSafe} of {totalPages}</span>
          <button disabled={pageSafe >= totalPages} onClick={() => setPage(pageSafe + 1)}>Next</button>
          <button disabled={pageSafe >= totalPages} onClick={() => setPage(totalPages)}>Last</button>
        </div>
    </div>
  </div>;
}

function rangeHoursForBenchmark() {
  return 4;
}

function QueuePane({ queue, engineHealth, error, benchmark, benchmarkBusy, benchmarkMessage, onRunBenchmark }: { queue: QueueSnapshot | null; engineHealth: EngineHealth | null; error: string; benchmark: TranscriptionBenchmarkResult | null; benchmarkBusy: boolean; benchmarkMessage: string; onRunBenchmark: (sampleCount: number) => Promise<void> }) {
  const q = queue;
  const depth = q?.queueDepth ?? engineHealth?.queueDepth ?? 0;
  const pending = q?.pendingTranscriptions ?? engineHealth?.pendingTranscriptions ?? 0;
  const live = q?.liveQueueDepth ?? engineHealth?.liveQueueDepth ?? 0;
  const priority = q?.priorityLiveQueueDepth ?? engineHealth?.priorityLiveQueueDepth ?? 0;
  const backlog = q?.backlogQueueDepth ?? engineHealth?.backlogQueueDepth ?? 0;
  const transcribed = q?.recentTranscribedPerMinute ?? engineHealth?.recentTranscribedPerMinute ?? 0;
  const ingested = q?.recentIngestPerMinute ?? engineHealth?.recentIngestPerMinute ?? 0;
  const audioIn = q?.recentAudioSecondsIngestedPerMinute ?? engineHealth?.recentAudioSecondsIngestedPerMinute ?? 0;
  const audioOut = q?.recentAudioSecondsTranscribedPerMinute ?? engineHealth?.recentAudioSecondsTranscribedPerMinute ?? 0;
  const pendingAudioSeconds = q?.pendingAudioSeconds ?? engineHealth?.pendingAudioSeconds ?? 0;
  const queueState = depth <= 0 ? "OK" : q?.queueUnderPressure || engineHealth?.queueUnderPressure ? "Pressure" : audioOut >= audioIn ? "Draining" : "Growing";
  const etaMinutes = pendingAudioSeconds > 0 && audioOut > audioIn ? pendingAudioSeconds / Math.max(0.1, audioOut - audioIn) : 0;
  const blockers = [q?.importWorkBlockedReason ?? engineHealth?.importWorkBlockedReason, q?.aiWorkBlockedReason ?? engineHealth?.aiWorkBlockedReason].filter(Boolean);
  return <div className="queue-jobs-layout">
    <div className="card queue-card">
      <div className="jobs-card-head">
        <h3>Transcription Queue</h3>
        <span className={`job-status status-${queueState === "Pressure" || queueState === "Growing" ? "failed" : queueState === "Draining" ? "running" : "completed"}`}>{queueState}</span>
        {error && <span className="section-status error">{error}</span>}
      </div>
      <div className="audit-kpis queue-kpis">
        <Kpi label="Queued" value={depth.toLocaleString()} subtext={`${pending.toLocaleString()} pending in database`} />
        <Kpi label="Pending Audio" value={formatDurationMinutes(pendingAudioSeconds / 60)} subtext={`${pendingAudioSeconds.toLocaleString()} audio seconds queued`} />
        <Kpi label="Composition" value={`${live.toLocaleString()} live`} subtext={`${priority.toLocaleString()} priority, ${backlog.toLocaleString()} backlog`} />
        <Kpi label="Audio Throughput" value={`${audioOut.toFixed(0)}s/min`} subtext={`${audioIn.toFixed(0)}s/min in over ${q?.throughputWindowMinutes ?? engineHealth?.throughputWindowMinutes ?? 10}m`} />
        <Kpi label="Call Throughput" value={`${transcribed.toFixed(1)}/min`} subtext={`${ingested.toFixed(1)}/min in; useful but duration-blind`} />
        <Kpi label="Latency" value={`${Number(q?.averageTranscriptionSeconds ?? engineHealth?.averageTranscriptionSeconds ?? 0).toFixed(1)}s`} subtext={`${Number(q?.averageAudioSeconds ?? engineHealth?.averageAudioSeconds ?? 0).toFixed(1)}s avg audio`} />
        <Kpi label="Workers" value={`${q?.liveTranscriptionWorkers ?? engineHealth?.liveTranscriptionWorkers ?? 0} x ${q?.whisperThreadsPerWorker ?? engineHealth?.whisperThreadsPerWorker ?? 0}`} subtext="workers x threads" />
        <Kpi label="ETA" value={etaMinutes > 0 ? formatDurationMinutes(etaMinutes) : depth > 0 ? "Unknown" : "Clear"} subtext={depth > 0 && audioOut <= audioIn ? "Audio queue is not currently outrunning ingest" : "Based on recent net audio drain"} />
      </div>
      {blockers.length > 0 && <div className="queue-blockers">{blockers.map((text, i) => <div className="settings-message error" key={i}>{text}</div>)}</div>}
      {q?.ingest?.paused && <div className="settings-message error">Live ingest is paused{q.ingest.untilQueueClear ? " until the queue clears" : ""}. Dropped this pause: {(q.ingest.droppedCallsThisPause ?? 0).toLocaleString()} ({q.ingest.droppedCalls.toLocaleString()} total since service start).</div>}
    </div>
    <div className="card queue-card">
      <h3>Top Audio Talkgroups</h3>
      <p className="muted">Chattiest talkgroups by total audio duration in the queue window. This is the best view for priority/defer decisions.</p>
      {q?.topAudioTalkgroups?.length ? <table className="table pending-calls-table"><thead><tr><th>Talkgroup</th><th>Category</th><th>Calls</th><th>Audio</th><th>Avg</th><th>Pending</th></tr></thead><tbody>{q.topAudioTalkgroups.map(row => <tr key={`${row.systemShortName}-${row.talkgroup}`}>
        <td>{row.talkgroupName || `TG ${row.talkgroup}`}<br /><span className="muted">{row.systemShortName} / {row.talkgroup}</span></td>
        <td><span className={`category-chip category-${row.category}`}>{label(row.category)}</span></td>
        <td>{row.calls.toLocaleString()}</td>
        <td>{formatDurationMinutes(row.audioSeconds / 60)}</td>
        <td>{row.averageAudioSeconds.toFixed(1)}s</td>
        <td>{row.pendingCalls.toLocaleString()} / {formatDurationMinutes(row.pendingAudioSeconds / 60)}</td>
      </tr>)}</tbody></table> : <p className="muted">No talkgroup load data.</p>}
    </div>
    <div className="card queue-card">
      <h3>Pending Calls</h3>
      <p className="muted">Oldest pending transcription records, shown in expected processing order.</p>
      {q?.pendingCalls?.length ? <table className="table pending-calls-table"><thead><tr><th>Time</th><th>Category</th><th>Talkgroup</th><th>Source</th></tr></thead><tbody>{q.pendingCalls.map(call => <tr key={call.callId}>
        <td>{new Date(call.startTime * 1000).toLocaleTimeString()}</td>
        <td><span className={`category-chip category-${call.category}`}>{label(call.category)}</span></td>
        <td>{call.talkgroupName || `TG ${call.talkgroup}`}{call.isImported && <span className="muted"> Imported</span>}</td>
        <td>{call.systemShortName || "--"}</td>
      </tr>)}</tbody></table> : <p className="muted">No pending transcription calls.</p>}
    </div>
    <div className="card queue-card queue-benchmark-card">
      <div className="jobs-card-head">
        <h3>Pipeline Benchmark</h3>
        <span className="muted">Read-only production sample</span>
      </div>
      <p className="muted">Runs recent completed calls through the local Whisper pipeline and times read, 16 kHz normalization, Whisper inference, quality scoring, and a scratch DB write. It does not update call records.</p>
      <div className="setup-button-row">
        <button disabled={benchmarkBusy} onClick={() => void onRunBenchmark(5)}>{benchmarkBusy ? "Running..." : "Run 5 Calls"}</button>
        <button disabled={benchmarkBusy} onClick={() => void onRunBenchmark(10)}>Run 10 Calls</button>
        <button disabled={benchmarkBusy} onClick={() => void onRunBenchmark(25)}>Run 25 Calls</button>
      </div>
      {benchmarkMessage && <div className={benchmarkMessage.toLowerCase().includes("fail") || benchmarkMessage.toLowerCase().includes("error") ? "settings-message error" : "settings-message ok"}>{benchmarkMessage}</div>}
      {benchmark && <BenchmarkResultView result={benchmark} />}
    </div>
  </div>;
}

function BenchmarkResultView({ result }: { result: TranscriptionBenchmarkResult }) {
  return <div className="benchmark-result">
    <div className="audit-kpis queue-kpis">
      <Kpi label="Warm Rate" value={`${result.warmCallsPerMinute.toFixed(1)}/min`} subtext={`${result.sampleCount} calls, ${result.whisperThreads} thread(s)`} />
      <Kpi label="Total Time" value={`${result.totalSeconds.toFixed(1)}s`} subtext={`${result.processCpuSeconds.toFixed(1)} CPU seconds`} />
      <Kpi label="Average Call" value={`${result.averageTotalSeconds.toFixed(1)}s`} subtext={`${result.averageAudioSeconds.toFixed(1)}s audio; ${result.averageRealtimeFactor.toFixed(2)}x realtime`} />
      <Kpi label="Whisper Share" value={`${result.whisperSharePercent.toFixed(0)}%`} subtext={`${result.averageWhisperSeconds.toFixed(2)}s avg inference`} />
      <Kpi label="Overhead" value={`${(result.averageTotalSeconds - result.averageWhisperSeconds).toFixed(2)}s`} subtext={`read ${result.averageReadSeconds.toFixed(2)}, normalize ${result.averageNormalizeSeconds.toFixed(2)}, db ${result.averageScratchWriteSeconds.toFixed(2)}`} />
      <Kpi label="Failures" value={result.failureCount.toString()} subtext={`model init ${result.modelInitSeconds.toFixed(2)}s`} />
    </div>
    <table className="table benchmark-table">
      <thead><tr><th>Call</th><th>Audio</th><th>Total</th><th>Read</th><th>Normalize</th><th>Whisper</th><th>Quality</th><th>DB</th><th>Result</th></tr></thead>
      <tbody>{result.rows.map(row => <tr key={row.callId}>
        <td>{row.callId}<br /><span className="muted">{row.systemShortName}</span></td>
        <td>{row.audioSeconds}s</td>
        <td>{row.totalSeconds.toFixed(2)}s</td>
        <td>{row.readSeconds.toFixed(2)}</td>
        <td>{row.normalizeSeconds.toFixed(2)}</td>
        <td>{row.whisperSeconds.toFixed(2)}</td>
        <td>{row.qualitySeconds.toFixed(3)}</td>
        <td>{row.scratchWriteSeconds.toFixed(3)}</td>
        <td>{row.error ? <span className="section-status error">{row.error}</span> : <><span className={`confidence ${row.includeInSummaries ? "good" : "bad"}`}>{row.qualityReason}</span><p className="muted benchmark-preview">{row.preview}</p></>}</td>
      </tr>)}</tbody>
    </table>
  </div>;
}

function JobsTable({ jobs, onControl, onDelete }: { jobs: Job[]; onControl: (id: number, action: string) => Promise<void>; onDelete: (id: number) => Promise<void> }) {
  if (!jobs.length) return <span className="muted">No jobs</span>;
  return <table className="table jobs-table">
    <thead><tr><th>ID</th><th>Type</th><th>Status</th><th>Progress</th><th>Timestamps</th><th>Message</th><th>Actions</th></tr></thead>
    <tbody>{jobs.map(job => {
      const active = isActiveJob(job);
      return <tr key={job.id}>
        <td>{job.id}</td>
        <td>{label(job.type)}</td>
        <td><span className={`job-status status-${job.status}`}>{job.status}</span></td>
        <td>{job.completed.toLocaleString()}/{job.total.toLocaleString()}<br /><span className="muted">{job.failed.toLocaleString()} failed</span></td>
        <td className="job-times">
          <span>Created {formatJobDate(job.createdAtUtc)}</span>
          {job.startedAtUtc && <span>Started {formatJobDate(job.startedAtUtc)}</span>}
          {job.finishedAtUtc && <span>Finished {formatJobDate(job.finishedAtUtc)}</span>}
        </td>
        <td>{job.message}</td>
        <td className="job-actions-cell"><div>
          {active ? <>
            {job.type !== "summary_generation" && job.status === "running" && <button onClick={() => void onControl(job.id, "pause")}>Pause</button>}
            {job.type !== "summary_generation" && job.status === "paused" && <button onClick={() => void onControl(job.id, "resume")}>Resume</button>}
            {(job.status === "queued" || job.status === "running" || job.status === "paused") && <button onClick={() => void onControl(job.id, "cancel")}>Cancel</button>}
          </> : <button onClick={() => void onDelete(job.id)}>Delete</button>}
        </div></td>
      </tr>;
    })}</tbody>
  </table>;
}

function isActiveJob(job: Job) {
  return job.status === "queued" || job.status === "running" || job.status === "paused" || job.status === "canceling";
}

function formatJobDate(value?: string | null) {
  if (!value) return "--";
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString();
}

function formatDurationMinutes(minutes: number) {
  if (minutes < 1) return "<1m";
  if (minutes < 90) return `${Math.round(minutes)}m`;
  return `${Math.floor(minutes / 60)}h ${Math.round(minutes % 60)}m`;
}

function TrHealthSummaryView({ data }: { data: TrTroubleshoot }) {
  const cfg = data.config;
  return <div className="tr-summary">
    <div className="card">
      <h2>{data.health.title}</h2>
      <p className="muted">{data.health.window}</p>
      <p className="muted">{data.health.lastWindow}</p>
      <p className="muted">{data.health.source}</p>
      <p className="muted">Health summary is computed from the last 24 hours only.</p>
    </div>
    <div className="card">
      <h3>Configuration</h3>
      <div>{cfg?.ok ? "OK" : "Problem"}: {cfg?.path}</div>
      <div className="muted">Callstream target {cfg?.callstreamTarget}; configured {cfg?.callstreamConfigured ? "yes" : "no"}</div>
    </div>
    <MetricTable title="Health Metrics" rows={data.health.metrics} />
    <MetricTable title="Systems" rows={data.health.systems} />
    <SourcePlanTable rows={data.health.sourcePlan ?? []} />
    <SourceCoverageTable rows={data.health.sourceCoverage ?? []} />
    <div className="remedy-list"><h3>Suggested Remedies</h3>{data.health.remedies.map(r => <div className={`remedy ${r.isIssue ? "issue" : ""}`} key={r.metric}><strong>{r.metric}</strong><p>{r.notes}</p></div>)}</div>
    <details className="card"><summary>Raw health samples</summary><table className="table"><thead><tr><th>Window</th><th>Scope</th><th>CC summary</th><th>CC zero</th><th>Low warnings</th><th>Warning zero</th><th>Retunes</th><th>No TX</th><th>Recorder exhausted</th><th>Stops</th></tr></thead><tbody>{data.health.samples.map(r => <tr key={r.id}><td>{new Date(r.windowStartUtc).toLocaleString()}</td><td>{r.scope}</td><td>{r.ccSummaryDecodeLines ? r.ccSummaryAvgDecodeRate.toFixed(2) : "N/A"}</td><td>{r.ccSummaryDecodeLines ? `${r.ccSummaryDecodeZeroPct.toFixed(1)}%` : "N/A"}</td><td>{r.lowDecodeWarningLines.toLocaleString()}</td><td>{r.lowDecodeWarningLines ? `${r.lowDecodeWarningZeroPct.toFixed(1)}%` : "N/A"}</td><td>{r.retunes}</td><td>{r.noTxRecorded}</td><td>{r.recorderExhausted}</td><td>{r.sampleStops}</td></tr>)}</tbody></table></details>
  </div>;
}

function RfAnalysisPanel({ data, rangeHours }: { data: TrTroubleshoot; rangeHours: number }) {
  const systems = Array.from(new Set((data.health.systems ?? []).map(row => row.metric).filter(Boolean)));
  const [system, setSystem] = useState(systems[0] ?? "");
  const [hours, setHours] = useState(String(Math.min(Math.max(rangeHours, 2), 72)));
  const [analysis, setAnalysis] = useState<TrRfAnalysis | null>(null);
  const [message, setMessage] = useState("");
  const [busy, setBusy] = useState(false);
  useEffect(() => {
    if (!system && systems[0]) setSystem(systems[0]);
  }, [system, systems]);
  async function run() {
    if (!system) return;
    setBusy(true);
    setMessage("");
    try {
      const end = Math.floor(Date.now() / 1000);
      const start = end - Math.max(1, Number(hours) || 24) * 3600;
      const result = await api.request<TrRfAnalysis>(`/api/v1/troubleshoot/rf-analysis?system=${encodeURIComponent(system)}&start=${start}&end=${end}`);
      setAnalysis(result);
    } catch (error) {
      setMessage(error instanceof Error ? error.message : String(error));
    } finally {
      setBusy(false);
    }
  }
  return <div className="rf-analysis">
    <div className="card">
      <h3>RF Analysis</h3>
      <p className="muted">Uses stored TR health rows for corrected CC summary decode metrics, plus bounded journald parsing for recent retune target detail.</p>
      <div className="settings-grid">
        <label>System <select value={system} onChange={e => setSystem(e.target.value)}>{systems.map(s => <option key={s}>{s}</option>)}</select></label>
        <label>Window <select value={hours} onChange={e => setHours(e.target.value)}><option value="2">Last 2h</option><option value="6">Last 6h</option><option value="24">Last 24h</option><option value="72">Last 3d</option></select></label>
      </div>
      <div className="setup-button-row"><button disabled={busy || !system} onClick={() => void run()}>{busy ? "Analyzing..." : "Run Analysis"}</button></div>
      {message && <p className="settings-message error">{message}</p>}
      {analysis && <p className={analysis.hasEnoughPostChangeData ? "settings-message ok" : "settings-message"}>{analysis.summary}</p>}
      {analysis && !analysis.hasEnoughPostChangeData && <p className="muted">Post-change RF analysis is more useful after at least a few hours of corrected health rows; for configuration changes, prefer 48-72h before drawing conclusions.</p>}
    </div>
    {analysis && <div className="system-manager-grid">
      <MetricTable title="RF Metrics" rows={analysis.metrics} />
      <MetricTable title="Previous Window Comparison" rows={analysis.comparison} />
      <MetricTable title="Retune Targets" rows={analysis.retuneTargets} />
      <MetricTable title="Recommended Next Checks" rows={analysis.recommendations} />
    </div>}
  </div>;
}

function SourcePlanTable({ rows }: { rows: NonNullable<TrTroubleshoot["health"]["sourcePlan"]> }) {
  if (!rows.length) return null;
  return <div className="card">
    <h3>Suggested Source Plan</h3>
    <p className="muted">Built from TR control channels, any configured voice frequencies, and observed call frequencies in the selected range. Ranges use the same 90% sample-rate windowing logic as the setup wizard.</p>
    <table className="table">
      <thead><tr><th>System</th><th>Desired range</th><th>Center</th><th>Assigned source</th><th>Notes</th></tr></thead>
      <tbody>{rows.map((row, i) => <tr className={row.isIssue ? "issue-row" : ""} key={`${row.systemShortName}-${row.lowMhz}-${i}`}>
        <td>{row.systemShortName}</td>
        <td>{row.lowMhz.toFixed(3)}-{row.highMhz.toFixed(3)} MHz</td>
        <td>{row.recommendedCenterMhz.toFixed(3)} MHz</td>
        <td>{row.assignedSourceIndex == null ? "None" : `Source ${row.assignedSourceIndex}`}<br /><span className="muted">{row.assignedDevice}</span></td>
        <td>{row.notes}</td>
      </tr>)}</tbody>
    </table>
  </div>;
}

function SourceCoverageTable({ rows }: { rows: NonNullable<TrTroubleshoot["health"]["sourceCoverage"]> }) {
  if (!rows.length) return null;
  return <div className="card">
    <h3>SDR Source Coverage</h3>
    <p className="muted">Calls are assigned to the first configured source window that covers their frequency. A source with coverable calls but no first-match calls is probably shadowed by an earlier overlapping source.</p>
    <table className="table">
      <thead><tr><th>Source</th><th>Window</th><th>Recorders</th><th>First-match calls</th><th>Coverable calls</th><th>Unique freqs</th><th>Notes</th></tr></thead>
      <tbody>{rows.map(row => <tr className={row.isIssue ? "issue-row" : ""} key={row.index}>
        <td>{row.index >= 0 ? `Source ${row.index}` : "Uncovered"}<br /><span className="muted">{row.device}</span></td>
        <td>{row.index >= 0 ? `${row.lowMhz.toFixed(3)}-${row.highMhz.toFixed(3)} MHz` : "--"}<br />{row.index >= 0 && <span className="muted">center {row.centerMhz.toFixed(3)} MHz</span>}</td>
        <td>{row.digitalRecorders}</td>
        <td>{row.firstMatchCalls.toLocaleString()}</td>
        <td>{row.coverableCalls.toLocaleString()}</td>
        <td>{row.uniqueFrequencies.toLocaleString()}</td>
        <td>{row.notes}</td>
      </tr>)}</tbody>
    </table>
  </div>;
}

function QualityAuditView({ data }: { data: TrTroubleshoot }) {
  const audit = data.qualityAudit;
  return <div className="quality-audit">
    <div className="audit-kpis">
      <Kpi label="Problem Calls" value={`${audit.problemCalls.toLocaleString()} / ${audit.totalCalls.toLocaleString()}`} subtext={`${audit.problemPercent.toFixed(1)}% poor-quality, failed, empty, short, or inaudible`} />
      <Kpi label="Inaudible Calls" value={audit.inaudibleCalls.toLocaleString()} subtext={`${audit.inaudiblePercent.toFixed(1)}% of calls in selected range`} />
    </div>
    <div className="audit-grid">
      <AuditTable title="Reasons" rows={audit.byReason} mode="reason" />
      <AuditTable title="Systems" rows={audit.bySystem} />
      <AuditTable title="Talkgroups" rows={audit.byTalkgroup} />
      <QualityAuditHourChart rows={audit.byHour} />
    </div>
    <div className="card">
      <h3>Sample problem calls</h3>
      {audit.samples.length ? audit.samples.map(sample => <QualityAuditSampleCard sample={sample} key={sample.callId} />) : <p className="muted">No problem calls in the selected range.</p>}
    </div>
  </div>;
}

function AuditTable({ title, rows, mode = "default" }: { title: string; rows: QualityAuditGroup[]; mode?: "default" | "reason" }) {
  const reasonMode = mode === "reason";
  return <div className="card audit-table-card">
    <h3>{title}</h3>
    {rows.length ? <table className="table"><thead><tr><th>{reasonMode ? "Reason" : "Name"}</th><th>{reasonMode ? "Calls" : "Total"}</th><th>{reasonMode ? "Share" : "Problems"}</th>{!reasonMode && <th>Inaudible</th>}</tr></thead><tbody>{rows.map(row => <tr key={row.label}>
      <td>{row.label}</td>
      <td>{row.totalCalls}</td>
      <td>{reasonMode ? <span>{row.problemPercent.toFixed(1)}%</span> : <span>{row.problemCalls} <span className="muted">({row.problemPercent.toFixed(1)}%)</span></span>}</td>
      {!reasonMode && <td>{row.inaudibleCalls} <span className="muted">({row.inaudiblePercent.toFixed(1)}%)</span></td>}
    </tr>)}</tbody></table> : <p className="muted">No problem calls.</p>}
  </div>;
}

function QualityAuditHourChart({ rows }: { rows: TrTroubleshoot["qualityAudit"]["byHour"] }) {
  const hours = Array.from({ length: 24 }, (_, i) => i);
  const max = Math.max(1, ...rows.map(r => Math.max(r.problemCalls, r.inaudibleCalls)));
  return <div className="card chart-card audit-hour-card">
    <h3>Problems by Hour</h3>
    <svg className="chart" viewBox="0 0 500 190" preserveAspectRatio="xMinYMin meet" role="img" aria-label="Problem and inaudible calls by hour">
      <line className="axis" x1="32" y1="158" x2="482" y2="158" />
      <line className="axis" x1="32" y1="28" x2="32" y2="158" />
      <text className="chart-label" x="4" y="34">{max}</text>
      {[0, 6, 12, 18, 23].map(hour => <text className="chart-label" x={36 + hour * 19} y="178" key={hour}>{hour}</text>)}
      {hours.map(hour => {
        const row = rows.find(r => r.hour === hour);
        const problemHeight = ((row?.problemCalls ?? 0) / max) * 118;
        const inaudibleHeight = ((row?.inaudibleCalls ?? 0) / max) * 118;
        return <g key={hour}>
          <rect x={30 + hour * 19} y={158 - problemHeight} width="7" height={problemHeight} fill="#ff6b5a" />
          <rect x={38 + hour * 19} y={158 - inaudibleHeight} width="7" height={inaudibleHeight} fill="#5aa7ff" />
        </g>;
      })}
    </svg>
    <Legend items={[["Problems", "#ff6b5a"], ["Inaudible", "#5aa7ff"]]} />
  </div>;
}

function QualityAuditSampleCard({ sample }: { sample: QualityAuditSample }) {
  return <details className={`audit-sample category-${sample.category}`}>
    <summary>
      <span>{new Date(sample.startTime * 1000).toLocaleString()}</span>
      <strong>{sample.qualityReason}</strong>
      <span>{sample.talkgroupName || `TG ${sample.talkgroup}`}</span>
      <span>{sample.systemShortName} / source {sample.source}</span>
      <span>{formatDuration(sample.durationSeconds)}</span>
    </summary>
    <p>{sample.transcription || "No transcript available."}</p>
    <audio controls preload="metadata" src={sample.audioUrl} />
  </details>;
}

function MetricTable({ title, rows }: { title: string; rows: TrHealthMetric[] }) {
  return <div className="card"><h3>{title}</h3><table className="table metric-table"><thead><tr><th>Metric</th><th>Value</th><th>Notes</th></tr></thead><tbody>{rows.map(r => <tr className={r.isIssue ? "issue-row" : ""} key={r.metric}><td>{r.metric}</td><td>{r.value}</td><td>{r.notes}</td></tr>)}</tbody></table></div>;
}

function TrHealthChartView({ chart }: { chart: TrHealthChart }) {
  const colors = ["#62c6ff", "#ffcf5a", "#7ee081", "#ff6b5a"];
  const max = Math.max(1, ...chart.series.flatMap(s => s.values));
  const w = 680, h = 190, left = 44, top = 18, bottom = 38, right = 16;
  const plotW = w - left - right, plotH = h - top - bottom;
  const x = (i: number, len: number) => left + (len <= 1 ? 0 : (i / (len - 1)) * plotW);
  const y = (v: number) => top + plotH - (v / max) * plotH;
  return <div className="card tr-chart-card">
    <h3>{chart.title}</h3>
    <p className="muted">{chart.yAxisLabel}</p>
    {chart.baselineNote && <p className="baseline-note">{chart.baselineNote}</p>}
    <svg className="chart" viewBox={`0 0 ${w} ${h}`} preserveAspectRatio="none">
      <line className="axis" x1={left} y1={top} x2={left} y2={top + plotH} />
      <line className="axis" x1={left} y1={top + plotH} x2={left + plotW} y2={top + plotH} />
      <text className="chart-label" x={left - 6} y={top + 4} textAnchor="end">{formatChartValue(max, chart.valueFormat)}</text>
      <text className="chart-label" x={left - 6} y={top + plotH} textAnchor="end">0</text>
      {chart.series.map((s, si) => <polyline key={s.label || si} points={s.values.map((v, i) => `${x(i, s.values.length)},${y(v)}`).join(" ")} fill="none" stroke={s.isBaseline ? "#d7ecff" : colors[si % colors.length]} strokeWidth={s.isBaseline ? "2.5" : "2"} strokeDasharray={s.isBaseline ? "6 5" : undefined} />)}
      <text className="chart-label" x={left} y={h - 10}>{chart.labels[0] ?? ""}</text>
      <text className="chart-label" x={left + plotW} y={h - 10} textAnchor="end">{chart.labels[chart.labels.length - 1] ?? ""}</text>
    </svg>
    <div className="legend">{chart.series.map((s, i) => <span className={s.isBaseline ? "baseline-legend" : ""} key={s.label || i}><i style={{ background: s.isBaseline ? "#d7ecff" : colors[i % colors.length] }} />{s.label || "current"}</span>)}</div>
  </div>;
}

function formatChartValue(value: number, format: string) {
  return format === "F1" ? value.toFixed(1) : Math.round(value).toLocaleString();
}

function formatDuration(seconds: number) {
  const total = Math.max(0, Math.round(seconds));
  const minutes = Math.floor(total / 60);
  return `${minutes}:${String(total % 60).padStart(2, "0")}`;
}

function formatHz(value: number) {
  if (!Number.isFinite(value) || value <= 0) return "unknown";
  if (value >= 1_000_000)
    return `${(value / 1_000_000).toFixed(6).replace(/0+$/, "").replace(/\.$/, "")} MHz`;
  if (value >= 1_000)
    return `${(value / 1_000).toFixed(1)} kHz`;
  return `${value} Hz`;
}

function SetupWizard({ status, reload }: { status: SetupStatus; reload: () => Promise<void> }) {
  const [draft, setDraft] = useState<any>(() => setupDraftFromStatus(status));
  const [trConfigJson, setTrConfigJson] = useState("");
  const [talkgroupsCsv, setTalkgroupsCsv] = useState("");
  const [talkgroupSid, setTalkgroupSid] = useState("");
  const [includeExcludedTalkgroups, setIncludeExcludedTalkgroups] = useState(false);
  const [talkgroupPreview, setTalkgroupPreview] = useState<SetupTalkgroupPreview | null>(null);
  const [trDraftSid, setTrDraftSid] = useState("");
  const [trDraftUrl, setTrDraftUrl] = useState("");
  const [trDraftHtml, setTrDraftHtml] = useState("");
  const [trDraftSites, setTrDraftSites] = useState("");
  const [trDraftSerials, setTrDraftSerials] = useState("");
  const [trDraftRate, setTrDraftRate] = useState("2400000");
  const [trDraft, setTrDraft] = useState<SetupTrConfigDraft | null>(null);
  const [message, setMessage] = useState("Complete the required sections to unlock normal PizzaWave operation.");
  const [busy, setBusy] = useState("");
  const [artifactReport, setArtifactReport] = useState<SetupArtifactReport | null>(null);
  const [setupJob, setSetupJob] = useState<Job | null>(null);
  const [setupJobContext, setSetupJobContext] = useState("");
  const [setupLogs, setSetupLogs] = useState<JobLog[]>([]);
  const [sdrDetection, setSdrDetection] = useState<SetupSdrDetection | null>(null);
  const [sftpAvailability, setSftpAvailability] = useState<any>(null);
  const [models, setModels] = useState<any[]>([]);
  const [aiModels, setAiModels] = useState<string[]>([]);
  const [modelBusy, setModelBusy] = useState("");
  const [restartVerified, setRestartVerified] = useState(status.completed);
  const [wizardStep, setWizardStep] = useState(status.currentStep || "stack");
  const [expandedSetupStep, setExpandedSetupStep] = useState(status.currentStep || "stack");
  const [jobDrawerOpen, setJobDrawerOpen] = useState(false);
  const [trInstallMode, setTrInstallMode] = useState((status.values?.setup?.installMode === "freshTr" ? "freshTr" : "reuseExistingTr") as "reuseExistingTr" | "freshTr");
  const [trConfigMode, setTrConfigMode] = useState((status.values?.setup?.trConfigMode === "pasteJson" ? "pasteJson" : "radioReference") as "radioReference" | "pasteJson");
  const [confirmFreshBuild, setConfirmFreshBuild] = useState(false);
  const [calibrationMode, setCalibrationMode] = useState<"skip" | "prepare">("skip");
  const [calibrationPlan, setCalibrationPlan] = useState<SetupCalibrationPlan | null>(null);
  const [calibrationInputs, setCalibrationInputs] = useState<Record<string, { gain: string; errorHz: string; ppm: string }>>({});
  const [calibrationSweep, setCalibrationSweep] = useState({ rangeHz: "1200", stepHz: "300", warmupSec: "20", durationSec: "240" });
  const setupLogLastId = useRef(0);
  const setupJobRunning = Boolean(setupJob && ["queued", "running", "paused"].includes(setupJob.status));
  const calibrationJobRunning = setupJobRunning && setupJobContext === "calibration";

  useEffect(() => {
    setDraft(setupDraftFromStatus(status));
    setRestartVerified(status.completed);
    if (!setupJobRunning)
      setWizardStep(current => current || status.currentStep || "stack");
    setTrInstallMode(status.values?.setup?.installMode === "freshTr" ? "freshTr" : "reuseExistingTr");
    setTrConfigMode(status.values?.setup?.trConfigMode === "pasteJson" ? "pasteJson" : "radioReference");
  }, [status, setupJobRunning]);
  useEffect(() => {
    if (!setupJobRunning && status.currentStep)
      setExpandedSetupStep(status.currentStep);
  }, [status.currentStep, setupJobRunning]);
  useEffect(() => {
    if (setupJob)
      setJobDrawerOpen(true);
  }, [setupJob?.id]);
  useEffect(() => { void loadModels(); }, []);
  useEffect(() => {
    if (!modelBusy) return;
    const timer = window.setInterval(() => void loadModels(), 1500);
    return () => window.clearInterval(timer);
  }, [modelBusy]);
  useEffect(() => {
    if (trInstallMode === "freshTr" && !artifactReport && !busy) void loadArtifacts();
  }, [trInstallMode]);
  useEffect(() => {
    if (!setupJob || !["queued", "running", "paused"].includes(setupJob.status)) return;
    const timer = window.setInterval(() => {
      void refreshSetupJob(setupJob.id);
    }, 1400);
    return () => window.clearInterval(timer);
  }, [setupJob]);
  useEffect(() => {
    if (wizardStep === "calibration") void loadCalibrationPlan();
  }, [wizardStep, draft.trunkRecorder?.configPath]);

  function update(path: string[], value: any) {
    setDraft((current: any) => {
      const next = cloneSettings(current);
      let target = next;
      for (const key of path.slice(0, -1)) {
        const actualKey: any = /^\d+$/.test(key) ? Number(key) : key;
        target[actualKey] = target[actualKey] && typeof target[actualKey] === "object" ? target[actualKey] : {};
        target = target[actualKey];
      }
      const leaf: any = /^\d+$/.test(path[path.length - 1]) ? Number(path[path.length - 1]) : path[path.length - 1];
      target[leaf] = value;
      return next;
    });
  }

  async function save() {
    setBusy("save");
    setMessage("Saving setup values...");
    try {
      await saveSetupValues();
      setRestartVerified(false);
      setMessage("Setup values saved.");
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Save failed.");
    } finally {
      setBusy("");
    }
  }

  async function saveSetupValues() {
    const values: any = cloneSettings(draft);
    values.setup = { ...(values.setup ?? {}), currentStep: wizardStep, installMode: trInstallMode, trConfigMode };
    if (trConfigJson.trim()) values.trConfigJson = trConfigJson;
    if (talkgroupsCsv.trim()) values.talkgroupsCsv = talkgroupsCsv;
    return await api.request<SetupStatus>("/api/v1/setup/save", { method: "POST", body: JSON.stringify({ values }) });
  }

  async function validateSetupSection(section: string, saveFirst = true) {
    if (saveFirst) await saveSetupValues();
    const result = await api.request<SetupValidationResult>(`/api/v1/setup/validate/${section}`, { method: "POST" });
    setMessage(result.message);
    await reload();
    if (!result.ok) throw new Error(result.message);
    return result;
  }

  async function validate(section: string) {
    setBusy(section);
    setMessage(`Validating ${label(section)}...`);
    try {
      await save();
      await validateSetupSection(section);
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Validation failed.");
    } finally {
      setBusy("");
    }
  }

  async function detect() {
    setBusy("detect");
    try {
      const result = await api.request<any>("/api/v1/setup/detect-tr");
      setMessage(result.found ? `TR detected. Config: ${result.configPath}` : "TR was not detected. Install trunk-recorder before completing setup.");
      await reload();
    } finally {
      setBusy("");
    }
  }

  async function loadModels() {
    try {
      setModels(await api.request<any[]>("/api/v1/settings/transcription/models"));
    } catch {
      setModels([]);
    }
  }

  async function downloadModel(model: string) {
    setModelBusy(model);
    try {
      const result = await api.request<any>(`/api/v1/settings/transcription/models/${model}/download`, { method: "POST" });
      setMessage(result.message ?? "Model download completed.");
      if (result.ok !== false && result.path && model.startsWith("whisper-"))
        update(["transcription", "whisperModelFile"], result.path);
      await loadModels();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Model download failed.");
    } finally {
      setModelBusy("");
    }
  }

  async function deleteModel(model: string) {
    setModelBusy(model);
    try {
      const result = await api.request<any>(`/api/v1/settings/transcription/models/${model}`, { method: "DELETE" });
      setMessage(result.message ?? "Model removed.");
      await loadModels();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Model removal failed.");
    } finally {
      setModelBusy("");
    }
  }

  async function complete() {
    setBusy("complete");
    try {
      await save();
      if (!restartVerified && !status.completed)
        throw new Error("Run Apply & Restart first, then complete setup.");
      await api.request<SetupStatus>("/api/v1/setup/complete", { method: "POST" });
      setMessage("Setup complete. Loading PizzaWave...");
      await reload();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Setup could not be completed.");
    } finally {
      setBusy("");
    }
  }

  async function finishSetup() {
    setBusy("finish-setup");
    try {
      if (requiredOpen.length > 0)
        throw new Error("Complete the required setup checks before finishing.");
      await applyRestartAndValidateInline();
      const completed = await api.request<SetupStatus>("/api/v1/setup/complete", { method: "POST" });
      setMessage("Setup complete. Loading PizzaWave...");
      await reload();
      return completed;
    } catch (error) {
      setRestartVerified(false);
      setMessage(error instanceof Error ? error.message : "Setup could not be completed.");
    } finally {
      setBusy("");
    }
  }

  async function applyRestartAndValidate() {
    setBusy("restart-pizzad");
    try {
      await applyRestartAndValidateInline();
    } catch (error) {
      setRestartVerified(false);
      setMessage(error instanceof Error ? error.message : "Restart validation failed.");
    } finally {
      setBusy("");
    }
  }

  async function applyRestartAndValidateInline() {
    await saveSetupValues();
    setMessage("Applying settings and restarting pizzad...");
    const job = await beginSetupJob("restart-pizzad", true, "finish");
    await refreshSetupJob(job.id);
    await waitForHealth();
    setMessage("pizzad is back. Re-running required setup validations...");
    const result = await api.request<SetupValidationResult>("/api/v1/setup/validate-required", { method: "POST" });
    setRestartVerified(result.ok);
    setMessage(result.message);
    await reload();
    if (!result.ok) throw new Error(result.message);
    return result;
  }

  async function waitForHealth() {
    await new Promise(resolve => window.setTimeout(resolve, 1800));
    let lastError = "";
    for (let i = 0; i < 45; i++) {
      try {
        await api.request<EngineHealth>("/api/v1/health");
        return;
      } catch (error) {
        lastError = error instanceof Error ? error.message : "health check failed";
        await new Promise(resolve => window.setTimeout(resolve, 1000));
      }
    }
    throw new Error(`pizzad did not become healthy after restart: ${lastError}`);
  }

  async function loadArtifacts() {
    setBusy("artifacts");
    try {
      const report = await api.request<SetupArtifactReport>("/api/v1/setup/tr-artifacts");
      setArtifactReport(report);
      setMessage(report.hasBlockingArtifacts ? "TR artifacts found. Review them before starting a source-build install." : "No blocking TR install artifacts found.");
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Artifact check failed.");
    } finally {
      setBusy("");
    }
  }

  async function detectSdrs() {
    setBusy("sdr-detect");
    try {
      const result = await api.request<SetupSdrDetection>("/api/v1/setup/sdrs");
      setSdrDetection(result);
      if (result.devices.some(device => device.serial)) {
        setTrDraftSerials(result.devices.map(device => device.serial).filter(Boolean).join(","));
      }
      setMessage(result.message);
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "SDR detection failed.");
    } finally {
      setBusy("");
    }
  }

  async function loadCalibrationPlan() {
    setBusy("calibration-plan");
    try {
      const plan = await api.request<SetupCalibrationPlan>("/api/v1/setup/calibration/plan");
      setCalibrationPlan(plan);
      setCalibrationInputs(current => {
        const next = { ...current };
        for (const source of plan.sources) {
          const key = String(source.index);
          next[key] = next[key] ?? { gain: source.gain ?? "", errorHz: source.errorHz ? String(source.errorHz) : "", ppm: "" };
        }
        return next;
      });
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Calibration plan could not be loaded.");
    } finally {
      setBusy("");
    }
  }

  async function openGqrx(sourceIndex: number, frequencyHz: number) {
    setBusy(`gqrx-${sourceIndex}`);
    try {
      const result = await api.request<SetupValidationResult>("/api/v1/setup/calibration/open-gqrx", {
        method: "POST",
        body: JSON.stringify({ sourceIndex, frequencyHz })
      });
      setMessage(result.message);
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Unable to open gqrx.");
    } finally {
      setBusy("");
    }
  }

  async function startSetupJob(action: string, confirmed = false) {
    setBusy(action);
    try {
      const job = await beginSetupJob(action, confirmed, currentStep.id);
      setMessage(`Started setup job ${job.id}: ${job.message}`);
      await refreshSetupJob(job.id);
      await reload();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Setup job failed to start.");
    } finally {
      setBusy("");
    }
  }

  async function beginSetupJob(action: string, confirmed = false, context = currentStep.id, parameters?: any) {
    const job = await api.request<Job>("/api/v1/setup/jobs", { method: "POST", body: JSON.stringify({ action, confirmed, parameters }) });
    setSetupJob(job);
    setSetupJobContext(context);
    setupLogLastId.current = 0;
    setSetupLogs([]);
    return job;
  }

  async function runSetupJobToCompletion(action: string, confirmed = false, context = currentStep.id) {
    const job = await beginSetupJob(action, confirmed, context);
    setMessage(`Running ${label(action)}...`);
    for (let i = 0; i < 180; i++) {
      await refreshSetupJob(job.id);
      const jobs = await api.request<Job[]>("/api/v1/jobs");
      const current = jobs.find(row => row.id === job.id);
      if (current) setSetupJob(current);
      if (current?.status === "completed") return current;
      if (current?.status === "failed" || current?.status === "canceled")
        throw new Error(current.message || `${label(action)} failed.`);
      await new Promise(resolve => window.setTimeout(resolve, 1000));
    }
    throw new Error(`${label(action)} did not finish in time. Check System > Jobs.`);
  }

  async function runCalibrationSweep(parameters: any) {
    setBusy("tr-calibration-sweep");
    try {
      setWizardStep("calibration");
      update(["setup", "currentStep"], "calibration");
      const job = await beginSetupJob("tr-calibration-sweep", true, "calibration", parameters);
      setMessage(`Started calibration sweep job ${job.id}. Watch the live output below; the wizard will keep buttons disabled while it runs.`);
      await refreshSetupJob(job.id);
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Calibration sweep failed to start.");
    } finally {
      setBusy("");
    }
  }

  async function stopCalibrationJob() {
    setBusy("tr-calibration-cancel");
    try {
      const job = await beginSetupJob("tr-calibration-cancel", true, "calibration");
      setMessage(`Requested calibration stop with job ${job.id}.`);
      await refreshSetupJob(job.id);
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Calibration stop failed to start.");
    } finally {
      setBusy("");
    }
  }

  async function refreshSetupJob(jobId: number) {
    const [jobs, logs] = await Promise.all([
      api.request<Job[]>("/api/v1/jobs"),
      api.request<JobLog[]>(`/api/v1/jobs/${jobId}/logs?afterId=${setupLogLastId.current}`)
    ]);
    const job = jobs.find(j => j.id === jobId);
    if (job) setSetupJob(job);
    if (logs.length) {
      setupLogLastId.current = logs[logs.length - 1].id;
      setSetupLogs(current => [...current, ...logs]);
    }
  }

  async function previewTalkgroups(source: "csv" | "rr") {
    setBusy(`talkgroups-${source}`);
    try {
      const preview = await api.request<SetupTalkgroupPreview>("/api/v1/setup/talkgroups/preview", {
        method: "POST",
        body: JSON.stringify(source === "rr"
          ? { radioReferenceSid: talkgroupSid, includeNormallyExcluded: includeExcludedTalkgroups }
          : { csvText: talkgroupsCsv, includeNormallyExcluded: includeExcludedTalkgroups })
      });
      setTalkgroupPreview(preview);
      setMessage(`Previewed ${preview.rows.length.toLocaleString()} talkgroups: ${preview.includedCount.toLocaleString()} included, ${preview.excludedCount.toLocaleString()} excluded.`);
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Talkgroup preview failed.");
    } finally {
      setBusy("");
    }
  }

  async function saveTalkgroupPreview() {
    if (!talkgroupPreview) return;
    setBusy("talkgroups-save");
    try {
      await saveTalkgroupPreviewInline();
      await validateSetupSection("talkgroups");
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Talkgroup save failed.");
    } finally {
      setBusy("");
    }
  }

  async function saveTalkgroupPreviewInline() {
    if (!talkgroupPreview) throw new Error("Preview or import talkgroups before continuing.");
    const saved = await api.request<SetupTalkgroupPreview>("/api/v1/setup/talkgroups/save", { method: "POST", body: JSON.stringify({ rows: talkgroupPreview.rows }) });
    setTalkgroupPreview(saved);
    setMessage(saved.diagnostics);
    return saved;
  }

  function updateTalkgroupRow(index: number, patch: Partial<SetupTalkgroupRow>) {
    setTalkgroupPreview(current => {
      if (!current) return current;
      const rows = current.rows.map((row, i) => i === index ? { ...row, ...patch } : row);
      const included = rows.filter(row => row.included);
      const includedByCategory = included.reduce<Record<string, number>>((acc, row) => {
        const key = row.opsCategory || "other";
        acc[key] = (acc[key] ?? 0) + 1;
        return acc;
      }, {});
      return { ...current, rows, includedCount: included.length, excludedCount: rows.length - included.length, includedByCategory };
    });
  }

  function addTalkgroupRow() {
    setTalkgroupPreview(current => {
      const rows = current?.rows ?? [];
      const nextId = rows.reduce((max, row) => Math.max(max, row.id), 0) + 1;
      const nextRows = [...rows, { id: nextId, mode: "D", alphaTag: "", description: "", tag: "", category: "other", opsCategory: "other", included: true, exclusionReason: "" }];
      return {
        rows: nextRows,
        includedByCategory: nextRows.filter(row => row.included).reduce<Record<string, number>>((acc, row) => {
          const key = row.opsCategory || "other";
          acc[key] = (acc[key] ?? 0) + 1;
          return acc;
        }, {}),
        includedCount: nextRows.filter(row => row.included).length,
        excludedCount: nextRows.filter(row => !row.included).length,
        diagnostics: current?.diagnostics ?? "Manual talkgroup rows."
      };
    });
  }

  async function draftTrConfig() {
    setBusy("tr-config-draft");
    try {
      const draftResult = await api.request<SetupTrConfigDraft>("/api/v1/setup/tr-config/draft", {
        method: "POST",
        body: JSON.stringify({
          radioReferenceSid: trDraftSid,
          radioReferenceUrl: trDraftUrl,
          htmlText: trDraftHtml,
          siteNames: trDraftSites,
          sdrSerials: trDraftSerials,
          sampleRate: Number(trDraftRate) || 2400000
        })
      });
      setTrDraft(draftResult);
      setTrConfigJson(draftResult.configJson);
      setMessage(`${draftResult.diagnostics} Review the generated JSON before saving.`);
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "TR config draft failed.");
    } finally {
      setBusy("");
    }
  }

  async function saveDraftTrConfig() {
    const configJson = trConfigJson.trim() || trDraft?.configJson || "";
    if (!configJson) return;
    setBusy("tr-config-save");
    try {
      const result = await api.request<SetupValidationResult>("/api/v1/setup/tr-config/save", { method: "POST", body: JSON.stringify({ configJson }) });
      setMessage(result.message);
      await validate("tr");
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "TR config save failed.");
    } finally {
      setBusy("");
    }
  }

  async function patchCallstream() {
    setBusy("patch-callstream");
    try {
      await patchCallstreamInline();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Callstream patch failed.");
    } finally {
      setBusy("");
    }
  }

  async function patchCallstreamInline() {
    await saveSetupValues();
    const result = await api.request<SetupValidationResult>("/api/v1/setup/tr-config/patch-callstream", { method: "POST", body: JSON.stringify({ restartTr: false, disableCaptureDir: trInstallMode === "freshTr" }) });
    setMessage(result.message);
    await validateSetupSection("callstream");
    if (!result.ok) throw new Error(result.message);
    return result;
  }

  async function loadSftpAvailability() {
    setBusy("sftp-availability");
    try {
      await save();
      const result = await api.request<any>("/api/v1/imports/sftp/availability");
      setSftpAvailability(result);
      setMessage(result.message ?? "SFTP availability checked.");
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "SFTP availability check failed.");
    } finally {
      setBusy("");
    }
  }

  async function queueQuickSftpImport() {
    setBusy("sftp-quick-import");
    try {
      await save();
      const latest = sftpAvailability?.latestLocal ? new Date(sftpAvailability.latestLocal) : new Date();
      const start = new Date(latest.getTime() - 48 * 3600_000);
      const job = await api.request<Job>("/api/v1/imports/sftp/import", {
        method: "POST",
        body: JSON.stringify({ startLocal: start.toISOString(), endLocal: latest.toISOString(), confirmLargeImport: false })
      });
      setMessage(`Queued quick SFTP import job ${job.id}.`);
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Quick SFTP import failed.");
    } finally {
      setBusy("");
    }
  }

  async function loadAiModels() {
    setBusy("ai-models");
    try {
      await loadAiModelsInline();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Unable to load AI model list.");
    } finally {
      setBusy("");
    }
  }

  async function loadAiModelsInline() {
    await saveSetupValues();
    const result = await api.request<any>("/api/v1/settings/ai-insights/models");
    const modelRows = result.models ?? [];
    setAiModels(modelRows);
    setMessage(result.message ?? "Model list loaded.");
    let selectedModel = "";
    if (modelRows.length > 0 && !ai.openAiModel) {
      selectedModel = modelRows[0];
      update(["aiInsights", "openAiModel"], modelRows[0]);
      const values: any = cloneSettings(draft);
      values.aiInsights = { ...(values.aiInsights ?? {}), openAiModel: modelRows[0] };
      values.setup = { ...(values.setup ?? {}), currentStep: wizardStep, installMode: trInstallMode, trConfigMode };
      await api.request<SetupStatus>("/api/v1/setup/save", { method: "POST", body: JSON.stringify({ values }) });
    }
    return selectedModel;
  }

  function skipOptional(section: "ai-insights" | "alerts" | "sftp" | "calibration") {
    if (section === "ai-insights") update(["aiInsights", "enabled"], false);
    if (section === "alerts") update(["alerts", "emailEnabled"], false);
    if (section === "sftp") update(["sftpImport", "enabled"], false);
    if (section === "calibration") update(["setup", "calibrationSkippedOrCompleted"], true);
    setMessage(`${label(section)} skipped. Save progress to persist this choice.`);
  }

  const checks = status.checks ?? [];
  const requiredOpen = checks.filter(c => c.required && !c.ok);
  const tr = draft.trunkRecorder ?? {};
  const transcription = draft.transcription ?? {};
  const ai = draft.aiInsights ?? {};
  const alerts = draft.alerts ?? {};
  const sftp = draft.sftpImport ?? {};
  const branding = draft.branding ?? {};
  const locations = draft.locations?.monitoredAreas ?? [];
  const checkStepMap: Record<string, string> = {
    tr: "tr",
    callstream: "tr",
    health: "tr",
    talkgroups: "talkgroups",
    transcription: "transcription",
    locations: "areas",
    "ai-insights": "ai",
    alerts: "alerts",
    sftp: "sftp",
    calibration: "calibration"
  };
  const baseSetupSteps = [
    { id: "stack", title: "Stack" },
    { id: "tr", title: "TR Config" },
    { id: "talkgroups", title: "Talkgroups" },
    { id: "transcription", title: "Transcription" },
    { id: "areas", title: "Areas" },
    { id: "ai", title: "AI Insights" },
    { id: "alerts", title: "Alerts" },
    { id: "sftp", title: "Imports" },
    { id: "calibration", title: "Calibration" },
    { id: "finish", title: "Finish" }
  ];
  const setupSteps = baseSetupSteps.map(step => {
    const stepChecks = checks.filter(check => checkStepMap[check.id] === step.id);
    const requiredMissing = stepChecks.some(check => check.required && !check.ok);
    let ok = stepChecks.length > 0 && stepChecks.every(check => check.ok);
    let blocked = requiredMissing;
    if (step.id === "stack") {
      ok = Boolean(status.detection?.found) || trInstallMode === "freshTr";
      blocked = !ok;
    }
    if (step.id === "calibration") {
      ok = ok && !calibrationJobRunning;
    }
    if (step.id === "finish") {
      ok = requiredOpen.length === 0 && restartVerified && !setupJobRunning;
      blocked = requiredOpen.length > 0 || setupJobRunning;
    }
    return { ...step, checks: stepChecks, ok, blocked };
  });
  const stepIndex = Math.max(0, setupSteps.findIndex(step => step.id === wizardStep));
  const currentStep = setupSteps[stepIndex] ?? setupSteps[0];

  function goStep(id: string) {
    if (setupJobRunning) {
      setMessage("A setup job is running. Stop or wait for it before changing steps.");
      return;
    }
    setWizardStep(id);
    update(["setup", "currentStep"], id);
    if (setupJobContext && setupJobContext !== id && !["queued", "running", "paused"].includes(setupJob?.status ?? "")) {
      setSetupJob(null);
      setSetupLogs([]);
      setSetupJobContext("");
    }
  }

  function selectSetupStep(id: string) {
    setExpandedSetupStep(current => current === id && wizardStep !== id ? "" : id);
    goStep(id);
  }

  async function nextStep() {
    const nextId = setupSteps[Math.min(setupSteps.length - 1, stepIndex + 1)].id;
    setBusy(`advance-${currentStep.id}`);
    try {
      await performCurrentStepWork();
      goStep(nextId);
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "This step could not be completed.");
    } finally {
      setBusy("");
    }
  }

  function previousStep() {
    goStep(setupSteps[Math.max(0, stepIndex - 1)].id);
  }

  async function performCurrentStepWork() {
    if (currentStep.id === "stack") {
      await saveSetupValues();
      setMessage("Backing up existing TR files before continuing...");
      await runSetupJobToCompletion("backup-existing-tr", true);
      if (trInstallMode === "freshTr")
        await loadArtifacts();
      setMessage("TR backup complete. Review the job output below.");
      return;
    }

    if (currentStep.id === "tr") {
      await validateSetupSection("tr");
      await patchCallstreamInline();
      await validateSetupSection("health");
      return;
    }

    if (currentStep.id === "talkgroups") {
      if (talkgroupPreview)
        await saveTalkgroupPreviewInline();
      await validateSetupSection("talkgroups");
      return;
    }

    if (currentStep.id === "transcription") {
      await validateSetupSection("transcription");
      return;
    }

    if (currentStep.id === "areas") {
      await validateSetupSection("locations");
      return;
    }

    if (currentStep.id === "ai") {
      let modelWasSaved = false;
      if (ai.enabled && !ai.openAiModel && ai.openAiBaseUrl)
        modelWasSaved = Boolean(await loadAiModelsInline());
      await validateSetupSection("ai-insights", !modelWasSaved);
      return;
    }

    if (currentStep.id === "alerts") {
      await validateSetupSection("alerts");
      return;
    }

    if (currentStep.id === "sftp") {
      await validateSetupSection("sftp");
      return;
    }

    if (currentStep.id === "calibration") {
      if (calibrationMode === "prepare") {
        const plan = calibrationPlan ?? await api.request<SetupCalibrationPlan>("/api/v1/setup/calibration/plan");
        if (!plan.systems.length || !plan.sources.length)
          throw new Error("Calibration needs a valid TR config with systems and SDR sources before it can prepare guidance.");
        await runSetupJobToCompletion("tr-calibration-prime", true, "calibration");
        update(["setup", "calibrationSkippedOrCompleted"], true);
        await saveSetupValues();
      } else {
        update(["setup", "calibrationSkippedOrCompleted"], true);
        await saveSetupValues();
      }
    }
  }

  return <div className="setup-page">
    <div className="setup-hero">
      <div>
        <h1>PizzaWave Setup</h1>
        <p>First-run setup walks through one decision at a time. Progress saves automatically as you move through the wizard.</p>
      </div>
    </div>
    <div className="setup-message">{message}</div>
    <div className="setup-wizard-layout">
      <div className="card setup-checklist">
        <h3>Completion Gate</h3>
        <div className="setup-step-list">
          {setupSteps.map((step, i) => {
            const expanded = expandedSetupStep === step.id;
            return <div className={`setup-step-item ${step.id === currentStep.id ? "active" : ""} ${step.ok ? "ok" : ""} ${step.blocked ? "blocked" : ""} ${expanded ? "expanded" : ""}`} key={step.id}>
              <button type="button" className="setup-step-button" onClick={() => selectSetupStep(step.id)}>
                <span>{i + 1}</span>
                <strong>{step.title}</strong>
                <em>{step.ok ? "OK" : step.blocked ? "Required" : "Open"}</em>
              </button>
              {expanded && <div className="setup-step-checks">
                {step.checks.length > 0
                  ? step.checks.map(check => <div className={`setup-check compact ${check.ok ? "ok" : check.required ? "blocked" : "optional"}`} key={check.id}>
                    <span>{check.ok ? "OK" : check.required ? "Required" : "Optional"}</span>
                    <strong>{check.label}</strong>
                    <small>{check.message}</small>
                  </div>)
                  : <div className="setup-note">{step.id === "stack"
                    ? "Choose whether to reuse the existing TR install or build a fresh one."
                    : step.id === "finish"
                      ? (requiredOpen.length > 0 ? `${requiredOpen.length} required check(s) still need validation.` : restartVerified ? "Restart verification has passed." : "Finish applies settings and verifies the restart.")
                      : "This step has no validation checks yet."}</div>}
              </div>}
            </div>;
          })}
        </div>
      </div>
      <div className="setup-step-panel">
        {currentStep.id === "stack" && <SetupSection title="Stack" description="Choose whether this machine already has a working trunk-recorder install or should be built fresh.">
          <SettingInput label="PizzaWave name" description="Shown under the PizzaWave logo." value={branding.stackName} onChange={v => update(["branding", "stackName"], v)} />
          <div className="setup-choice-grid">
            <ChoiceCard active={trInstallMode === "reuseExistingTr"} title="Reuse existing TR" description="Best for an existing Raspberry Pi rig. PizzaWave will validate the config, patch callstream, and leave your working TR install in place." onClick={() => { setTrInstallMode("reuseExistingTr"); update(["setup", "installMode"], "reuseExistingTr"); }} />
            <ChoiceCard active={trInstallMode === "freshTr"} title="Fresh TR setup" description="Build trunk-recorder from source. Existing TR files are backed up automatically; blocking artifacts are listed before you proceed." onClick={() => { setTrInstallMode("freshTr"); update(["setup", "installMode"], "freshTr"); }} />
          </div>
          <div className="setup-detection-card">
            <strong>{status.detection?.found ? "Existing trunk-recorder detected" : "No working trunk-recorder install detected yet"}</strong>
            <small>{status.detection?.configExists ? `Config: ${status.detection.configPath}` : "Config file was not found at the configured path."}</small>
            <small>{status.detection?.serviceActive ? "TR service is currently running." : "TR service is not active."}</small>
          </div>
          {trInstallMode === "freshTr" && <>
            <div className="setup-note">Fresh setup will back up existing TR config/service files automatically before building. PizzaWave will not delete artifacts for you; if something blocks install, it will show what you need to remove.</div>
            {artifactReport && <ArtifactReport report={artifactReport} />}
            {artifactReport?.hasBlockingArtifacts && <SettingCheckbox label="I reviewed the existing artifacts and want to source-build anyway" description="Use this only after removing or intentionally keeping the listed files." checked={confirmFreshBuild} onChange={setConfirmFreshBuild} />}
            <button disabled={Boolean(busy) || (artifactReport?.hasBlockingArtifacts && !confirmFreshBuild)} onClick={() => void startSetupJob("tr-source-build", confirmFreshBuild)}>{busy === "tr-source-build" ? "Building..." : "Build trunk-recorder from source"}</button>
          </>}
          {trInstallMode === "reuseExistingTr" && <div className="setup-note">When you click Next, PizzaWave backs up TR files automatically and leaves trunk-recorder itself in place.</div>}
        </SetupSection>}

        {currentStep.id === "tr" && <SetupSection title="TR Config" description={trInstallMode === "reuseExistingTr" ? "Using the existing TR config. Draft/import tools are hidden because they are not relevant in reuse mode." : "Create or import the trunk-recorder config for a fresh install."}>
          <SettingInput label="TR config path" description="Existing or generated trunk-recorder config.json." value={tr.configPath} onChange={v => update(["trunkRecorder", "configPath"], v)} />
          <SettingInput label="TR log service" description="Systemd service name used for health collection." value={tr.logServiceName} onChange={v => update(["trunkRecorder", "logServiceName"], v)} />
          {trInstallMode === "freshTr" && <div className="setup-choice-grid">
            <ChoiceCard active={trConfigMode === "radioReference"} title="Draft from RadioReference" description="Fetch or paste a RadioReference page and let PizzaWave draft systems, sources, and center frequencies." onClick={() => { setTrConfigMode("radioReference"); update(["setup", "trConfigMode"], "radioReference"); }} />
            <ChoiceCard active={trConfigMode === "pasteJson"} title="Paste config JSON" description="Use an already prepared trunk-recorder config.json." onClick={() => { setTrConfigMode("pasteJson"); update(["setup", "trConfigMode"], "pasteJson"); }} />
          </div>}
          {trInstallMode === "freshTr" && trConfigMode === "radioReference" && <>
            <SettingInput label="RadioReference SID" description="Fetch directly when possible. Paste page HTML below if RadioReference blocks automated fetch." value={trDraftSid} onChange={setTrDraftSid} />
            <SettingInput label="RadioReference URL" description="Optional full system URL. Used instead of SID when supplied." value={trDraftUrl} onChange={setTrDraftUrl} />
            <SettingInput label="Site names" description="Comma-separated site names to include. Leave blank to infer site names from the page." value={trDraftSites} onChange={setTrDraftSites} />
            <SettingInput label="RTL-SDR serials" description="Comma-separated serials. Detection temporarily stops TR if it is running, then restarts it afterward." value={trDraftSerials} onChange={setTrDraftSerials} />
            <button disabled={Boolean(busy)} onClick={() => void detectSdrs()}>{busy === "sdr-detect" ? "Detecting..." : "Detect SDR serials"}</button>
            {sdrDetection && <SdrDetectionPanel detection={sdrDetection} />}
            <SettingInput label="Sample rate" description="Default 2400000. Used to calculate center frequencies and omitted channels." value={trDraftRate} onChange={setTrDraftRate} />
            <textarea value={trDraftHtml} onChange={e => setTrDraftHtml(e.target.value)} placeholder="Optional pasted RadioReference page HTML/source" />
            <div className="setup-button-row"><button disabled={Boolean(busy) || (!trDraftSid.trim() && !trDraftUrl.trim() && !trDraftHtml.trim())} onClick={() => void draftTrConfig()}>{busy === "tr-config-draft" ? "Drafting..." : "Draft config"}</button><button disabled={Boolean(busy) || !trConfigJson.trim()} onClick={() => void saveDraftTrConfig()}>{busy === "tr-config-save" ? "Saving..." : "Save TR config"}</button></div>
            {trDraft && <TrConfigDraftPreview draft={trDraft} />}
          </>}
          {trInstallMode === "freshTr" && trConfigMode === "pasteJson" && <textarea value={trConfigJson} onChange={e => setTrConfigJson(e.target.value)} placeholder="{ ... trunk-recorder config.json ... }" />}
          <div className="setup-note">{trInstallMode === "freshTr" ? "Click Next to validate the TR config, patch callstream, remove captureDir so PizzaWave owns call persistence, and verify health access." : "Click Next to validate the TR config, patch callstream, and verify health access. Existing captureDir settings are preserved in reuse mode for local import compatibility."}</div>
        </SetupSection>}

        {currentStep.id === "talkgroups" && <SetupSection title="Talkgroups" description="Talkgroup CSV is required. Import from RadioReference or paste a PizzaWave talkgroup CSV, then review rows before saving.">
          <SettingInput label="Talkgroups CSV path" description="Required before setup can complete." value={tr.talkgroupsPath} onChange={v => update(["trunkRecorder", "talkgroupsPath"], v)} />
          <SettingInput label="RadioReference SID" description="Example: 6355 from https://www.radioreference.com/db/sid/6355." value={talkgroupSid} onChange={setTalkgroupSid} />
          <SettingCheckbox label="Include encrypted/unknown/unwanted rows" description="Normally excluded by default. You can still include individual rows below." checked={includeExcludedTalkgroups} onChange={setIncludeExcludedTalkgroups} />
          <button disabled={Boolean(busy) || !talkgroupSid.trim()} onClick={() => void previewTalkgroups("rr")}>{busy === "talkgroups-rr" ? "Fetching..." : "Fetch RR talkgroups"}</button>
          <textarea value={talkgroupsCsv} onChange={e => setTalkgroupsCsv(e.target.value)} placeholder="Decimal,Alpha Tag,Description,Category" />
          <div className="setup-button-row"><button disabled={Boolean(busy) || !talkgroupsCsv.trim()} onClick={() => void previewTalkgroups("csv")}>{busy === "talkgroups-csv" ? "Parsing..." : "Preview CSV"}</button><button disabled={Boolean(busy)} onClick={addTalkgroupRow}>Add row</button></div>
          {talkgroupPreview && <TalkgroupPreviewTable preview={talkgroupPreview} updateRow={updateTalkgroupRow} />}
          <div className="setup-note">Click Next to save included rows and validate the talkgroup CSV.</div>
        </SetupSection>}

        {currentStep.id === "transcription" && <SetupSection title="Transcription" description="Required. Choose a transcription engine and run an actual sample transcription test.">
          <SettingSelect label="Engine" description="Provider for turning calls into text." value={transcription.provider} options={["none", "whisper", "faster-whisper", "lmstudio", "openai"]} onChange={v => update(["transcription", "provider"], v)} />
          {transcription.provider === "whisper" && <>
            <SettingSelect label="Whisper model" description="Base or medium are the recommended setup choices." value={modelIdForPath(models, "whisper", transcription.whisperModelFile)} options={modelOptions(models, "whisper")} onChange={v => update(["transcription", "whisperModelFile"], modelPath(models, v))} />
            <ModelManager engine="whisper" rows={models} busy={modelBusy} selectedPath={transcription.whisperModelFile} onUse={path => update(["transcription", "whisperModelFile"], path)} onDownload={downloadModel} onDelete={deleteModel} />
          </>}
          {transcription.provider === "faster-whisper" && <>
            <SettingSelect label="Model" description="Tiny/int8 is the safest first choice on Raspberry Pi." value={transcription.fasterWhisperModel ?? "tiny"} options={["tiny", "base", "small", "medium"]} onChange={v => update(["transcription", "fasterWhisperModel"], v)} />
            <SettingSelect label="Compute type" description="CPU quantization mode." value={transcription.fasterWhisperComputeType ?? "int8"} options={["int8", "int8_float16", "float32"]} onChange={v => update(["transcription", "fasterWhisperComputeType"], v)} />
            <SettingInput label="Python runtime" description="Managed optional runtime path." value={transcription.fasterWhisperPythonPath ?? "/opt/pizzawave/venv/faster-whisper/bin/python"} onChange={v => update(["transcription", "fasterWhisperPythonPath"], v)} />
            <button disabled={Boolean(busy) || setupJobRunning} onClick={() => void startSetupJob("faster-whisper-prime")}>{busy === "faster-whisper-prime" ? "Installing..." : "Install faster-whisper support"}</button>
          </>}
          {(transcription.provider === "lmstudio" || transcription.provider === "openai") && <>
            <SettingInput label="Base URL" description="OpenAI-compatible audio transcription endpoint." value={transcription.openAiBaseUrl} onChange={v => update(["transcription", "openAiBaseUrl"], v)} />
            <SettingInput label="Model" description="Audio transcription model name." value={transcription.openAiModel} onChange={v => update(["transcription", "openAiModel"], v)} />
            <SettingInput label="API key" description="Optional bearer token." type="password" value={transcription.openAiApiKey} onChange={v => update(["transcription", "openAiApiKey"], v)} />
          </>}
          <div className="setup-note">Click Next to test the selected transcription provider. Fresh installs with no calls yet will validate the provider/model and skip the sample call.</div>
        </SetupSection>}

        {currentStep.id === "areas" && <SetupSection title="Monitored Areas" description="Required for geocoding/map context. Each TR system shortName needs one monitored area.">
          {locations.map((area: any, i: number) => <div className="setup-area" key={area.areaId ?? i}>
            <SettingInput label="System short name" description="Must match TR system shortName or alias." value={area.systemShortName} onChange={v => update(["locations", "monitoredAreas", String(i), "systemShortName"], v)} />
            <SettingInput label="Area label" description="County/city label used by map searches." value={area.areaLabel} onChange={v => update(["locations", "monitoredAreas", String(i), "areaLabel"], v)} />
          </div>)}
          <div className="setup-note">Click Next to validate monitored areas against the TR system short names.</div>
        </SetupSection>}

        {currentStep.id === "ai" && <SetupSection title="AI Insights / LM Link" description="Optional. Required only for summaries, incidents, and LLM troubleshooting suggestions. LM Link must be linked with `lms login` before remote models appear.">
          <div className="setup-note">The deb includes the LM Studio setup script but does not install/link LM Studio until you choose to prepare it here.</div>
          <button disabled={Boolean(busy) || setupJobRunning} onClick={() => void startSetupJob("lmstudio-prime")}>{busy === "lmstudio-prime" || (setupJobContext === "ai" && setupJobRunning) ? "Preparing..." : "Prepare LM Link support"}</button>
          <SettingCheckbox label="Enable AI insights" description="Used for summaries, incidents, and troubleshooting suggestions." checked={ai.enabled} onChange={v => update(["aiInsights", "enabled"], v)} />
          <SettingInput label="Insights base URL" description="Usually LM Studio /v1 endpoint." value={ai.openAiBaseUrl} onChange={v => update(["aiInsights", "openAiBaseUrl"], v)} />
          {aiModels.length > 0
            ? <SettingSelect label="Insights model" description="Loaded from the configured LM Link/OpenAI-compatible endpoint." value={ai.openAiModel} options={aiModels} onChange={v => update(["aiInsights", "openAiModel"], v)} />
            : <SettingInput label="Insights model" description="Chat model for summaries/incidents. Use Load models when LM Link is reachable." value={ai.openAiModel} onChange={v => update(["aiInsights", "openAiModel"], v)} />}
          <div className="setup-note">{ai.enabled ? "Click Next to load available models when needed and test AI insights." : "AI insights are disabled. Click Next to mark this optional step skipped."}</div>
        </SetupSection>}

        {currentStep.id === "alerts" && <SetupSection title="Email Alerts" description="Optional. Live alert emails need an address and app password. Imported/historical alert matches never send email.">
          <SettingCheckbox label="Enable email alerts" description="Optional live alert notifications." checked={alerts.emailEnabled} onChange={v => update(["alerts", "emailEnabled"], v)} />
          {alerts.emailEnabled && <>
            <SettingSelect label="Email provider" description="SMTP preset used by the alert sender." value={alerts.emailProvider} options={["gmail", "yahoo"]} onChange={v => update(["alerts", "emailProvider"], v)} />
            <SettingInput label="Email address" description="Sender account used for alert delivery." value={alerts.emailUser} onChange={v => update(["alerts", "emailUser"], v)} />
            <SettingInput label="App password" description="Provider-specific app password or SMTP credential." type="password" value={alerts.emailPassword} onChange={v => update(["alerts", "emailPassword"], v)} />
          </>}
          <div className="setup-note">{alerts.emailEnabled ? "Click Next to validate email alert settings." : "Email alerts are disabled. Click Next to mark this optional step skipped."}</div>
        </SetupSection>}

        {currentStep.id === "sftp" && <SetupSection title="Imports" description="Optional. Import existing TR .bin recordings into the local PizzaWave store. Local import uses the current TR captureDir; SFTP import reads a remote archive without modifying it.">
          <div className="setup-nested">
            <h4>Local TR recordings</h4>
            <LocalImport reload={reload} />
          </div>
          <div className="setup-nested">
            <h4>SFTP archive</h4>
          <SettingCheckbox label="Enable SFTP import" description="Optional import from existing .bin archives." checked={sftp.enabled} onChange={v => update(["sftpImport", "enabled"], v)} />
          {sftp.enabled && <>
            <SettingInput label="SFTP host" description="Archive host or IP. The source is read-only; imported calls are copied locally." value={sftp.host} onChange={v => update(["sftpImport", "host"], v)} />
            <SettingInput label="SFTP port" description="Normally 22." type="number" value={sftp.port} onChange={v => update(["sftpImport", "port"], numberOrZero(v))} />
            <SettingInput label="SFTP username" description="Account used to read archive files." value={sftp.username} onChange={v => update(["sftpImport", "username"], v)} />
            <SettingSelect label="SFTP auth mode" description="Private key is preferred; password auth is supported." value={sftp.authMode} options={["privateKey", "password"]} onChange={v => update(["sftpImport", "authMode"], v)} />
            {sftp.authMode === "password"
              ? <SettingInput label="Password" description="Stored in pizzad settings if used." type="password" value={sftp.password} onChange={v => update(["sftpImport", "password"], v)} />
              : <SettingInput label="Private key path" description="Path on the Linux host." value={sftp.privateKeyPath} onChange={v => update(["sftpImport", "privateKeyPath"], v)} />}
            <SettingInput label="Remote root" description="Top-level TR archive directory." value={sftp.remoteRoot} onChange={v => update(["sftpImport", "remoteRoot"], v)} />
          <div className="setup-button-row"><button disabled={Boolean(busy)} onClick={() => void loadSftpAvailability()}>{busy === "sftp-availability" ? "Checking..." : "Check archive"}</button><button disabled={Boolean(busy) || !sftpAvailability?.available} onClick={() => void queueQuickSftpImport()}>{busy === "sftp-quick-import" ? "Queueing..." : "Queue last 48h import"}</button></div>
          {sftpAvailability && <div className="setup-note">{sftpAvailability.message} {sftpAvailability.available ? `${sftpAvailability.fileCount?.toLocaleString?.() ?? sftpAvailability.fileCount} file(s), ${formatBytes(sftpAvailability.totalBytes ?? 0)} visible.` : ""}</div>}
        </>}
          </div>
          <div className="setup-note">{sftp.enabled ? "Click Next to validate SFTP settings." : "SFTP import is disabled. Click Next to mark this optional step skipped."}</div>
        </SetupSection>}

        {currentStep.id === "calibration" && <SetupSection title="TR Calibration" description="Optional. This prepares the exact commands and checks needed for tuning. It does not change TR config unless you later choose to apply findings.">
          <div className="setup-note">PizzaWave reads the TR config and builds the tuning plan from known systems, control channels, configured source windows, and SDR assignments. SDR priming installs RTL-SDR utilities and the available GQRX package when the OS repository provides one; some Raspberry Pi images may require a separate ARM-compatible GQRX install.</div>
          {status.detection?.serviceActive && <div className="setup-warning-list">
            <div>trunk-recorder is currently running. GQRX usually cannot open the SDRs while TR owns them. Stop TR before using the GQRX buttons; the sweep job will restart TR repeatedly for its test passes.</div>
            <button disabled={Boolean(busy) || setupJobRunning} onClick={() => void startSetupJob("tr-stop-for-calibration")}>{busy === "tr-stop-for-calibration" ? "Stopping..." : "Stop TR for calibration"}</button>
          </div>}
          <a href="https://gqrx.dk/doc/practical-tricks-and-tips" target="_blank" rel="noreferrer">GQRX tuning reference</a>
          <div className="setup-choice-grid">
            <ChoiceCard disabled={Boolean(busy) || setupJobRunning} active={calibrationMode === "skip"} title="Skip for now" description="Finish setup without calibration. Calibration tools remain available later in System." onClick={() => setCalibrationMode("skip")} />
            <ChoiceCard disabled={Boolean(busy) || setupJobRunning} active={calibrationMode === "prepare"} title="Guided tuning plan" description="Step through each configured system/source, open GQRX with the right frequency context, and prepare tr_tune.sh commands." onClick={() => setCalibrationMode("prepare")} />
          </div>
          {calibrationMode === "prepare" && <>
            <div className="setup-note">This plan is loaded from the current TR config when you enter the calibration step. It will update automatically after the TR config step is changed.</div>
            <div className="settings-grid">
              <SettingInput label="Sweep range Hz" description="Default 1200: tests base error plus/minus this amount." value={calibrationSweep.rangeHz} onChange={v => setCalibrationSweep(current => ({ ...current, rangeHz: v }))} />
              <SettingInput label="Step Hz" description="Default 300: spacing between error candidates." value={calibrationSweep.stepHz} onChange={v => setCalibrationSweep(current => ({ ...current, stepHz: v }))} />
              <SettingInput label="Warmup seconds" description="Default 20 seconds per candidate before metrics are counted." value={calibrationSweep.warmupSec} onChange={v => setCalibrationSweep(current => ({ ...current, warmupSec: v }))} />
              <SettingInput label="Measure seconds" description="Default 240 seconds per candidate. Shorter test runs are possible later." value={calibrationSweep.durationSec} onChange={v => setCalibrationSweep(current => ({ ...current, durationSec: v }))} />
            </div>
            <GuidedCalibrationPlan
              plan={calibrationPlan}
              inputs={calibrationInputs}
              sweep={calibrationSweep}
              busy={Boolean(busy) || setupJobRunning}
              setInput={(sourceIndex, key, value) => setCalibrationInputs(current => ({ ...current, [sourceIndex]: { ...(current[sourceIndex] ?? { gain: "", errorHz: "", ppm: "" }), [key]: value } }))}
              openGqrx={openGqrx}
              runSweep={runCalibrationSweep}
            />
            <div className="setup-note">Next runs only the preflight: it verifies the tuning helper exists and checks whether TR owns the SDRs. The full per-system sweep commands above are intentionally presented for review before you run them.</div>
          </>}
          <div className="setup-note">{calibrationMode === "prepare" ? "Enter gain and error/PPM as you work through each proposed SDR. If an SDR source has no error yet, the command uses its current TR config error as the baseline." : "Click Next to skip calibration for now."}</div>
        </SetupSection>}

        {currentStep.id === "finish" && <SetupSection title="Finish" description="This applies the saved settings, restarts pizzad, re-validates the required checks, then exits setup mode so normal ingest and processing can start.">
          <SetupReview status={status} requiredOpen={requiredOpen} restartVerified={restartVerified} />
          <div className="setup-button-row"><button disabled={Boolean(busy) || setupJobRunning || requiredOpen.length > 0} onClick={() => void finishSetup()}>{busy === "finish-setup" ? "Finishing..." : "Finish setup"}</button></div>
        </SetupSection>}

        <div className="setup-nav-row">
          <button disabled={stepIndex === 0 || Boolean(busy) || setupJobRunning} onClick={previousStep}>Back</button>
          {stepIndex < setupSteps.length - 1 && <button disabled={Boolean(busy) || setupJobRunning} onClick={() => void nextStep()}>{busy === `advance-${currentStep.id}` ? "Working..." : "Next"}</button>}
        </div>
      </div>
    </div>
    <SetupJobDrawer job={setupJob} logs={setupLogs} running={setupJobRunning} onStopCalibration={stopCalibrationJob} stopping={busy === "tr-calibration-cancel"} open={jobDrawerOpen} setOpen={setJobDrawerOpen} />
  </div>;
}

function SetupSection({ title, description, children }: { title: string; description: string; children: React.ReactNode }) {
  return <div className="card setup-section"><h3>{title}</h3><p>{description}</p><div className="settings-fields">{children}</div></div>;
}

function ChoiceCard({ active, title, description, disabled, onClick }: { active: boolean; title: string; description: string; disabled?: boolean; onClick: () => void }) {
  return <button type="button" className={`setup-choice ${active ? "active" : ""}`} disabled={disabled} onClick={onClick}>
    <span className="setup-choice-toggle">{active ? "On" : "Off"}</span>
    <strong>{title}</strong>
    <small>{description}</small>
  </button>;
}

function CalibrationPlanCard({ plan }: { plan: ReturnType<typeof buildCalibrationPlan> }) {
  return <div className="calibration-plan">
    <div className="setup-job-head">
      <strong>Planned tr_tune.sh workflow</strong>
      <span className="pill">{plan.baseError === null ? "Needs base error" : `${plan.passCount} passes`}</span>
      <span className="pill">{plan.estimatedSeconds > 0 ? `About ${formatElapsed(plan.estimatedSeconds)}` : "Time unknown"}</span>
    </div>
    <div className="calibration-plan-grid">
      <div><span>Base error</span><strong>{plan.baseError === null ? "Not calculated" : `${plan.baseError} Hz`}</strong><small>{plan.baseSource}</small></div>
      <div><span>Error candidates</span><strong>{plan.candidateSummary}</strong><small>centered on the GQRX/default value</small></div>
      <div><span>Per pass</span><strong>{plan.warmupSec}s warmup + {plan.durationSec}s measure</strong><small>TR restarts for each candidate</small></div>
    </div>
    <ol className="calibration-step-list">
      <li>Stop trunk-recorder for the candidate pass so the selected RTL-SDR can be claimed cleanly.</li>
      <li>Patch a temporary TR config with the candidate error, modulation, control channel, device serial, and optional gain.</li>
      <li>Start TR, wait for warmup, then collect journald metrics for the measurement window.</li>
      <li>Score decode samples, zero-decode share, average/max decode rate, retunes, no-transmission, update-not-grant, started calls, and concluded calls.</li>
      <li>Restore the baseline config unless the sweep is explicitly run with a leave-last-config option, then show findings for user review.</li>
    </ol>
    {plan.ppmCommand && <div>
      <strong>PPM conversion command</strong>
      <pre className="command-box">{plan.ppmCommand}</pre>
    </div>}
    <div>
      <strong>Error sweep command</strong>
      <pre className="command-box">{plan.errorSweepCommand || "Enter system shortName, control channel, RTL-SDR serial, and either base error or center Hz + PPM to generate the command."}</pre>
    </div>
    <small className="setup-note">A decode rate above 2 msg/sec is acceptable but marginal. Prefer the candidate with stable decode, low zero-decode share, and fewer retunes over a single noisy peak.</small>
  </div>;
}

function GuidedCalibrationPlan({
  plan,
  inputs,
  sweep,
  busy,
  setInput,
  openGqrx,
  runSweep
}: {
  plan: SetupCalibrationPlan | null;
  inputs: Record<string, { gain: string; errorHz: string; ppm: string }>;
  sweep: { rangeHz: string; stepHz: string; warmupSec: string; durationSec: string };
  busy: boolean;
  setInput: (sourceIndex: string, key: "gain" | "errorHz" | "ppm", value: string) => void;
  openGqrx: (sourceIndex: number, frequencyHz: number) => Promise<void>;
  runSweep: (parameters: any) => Promise<void>;
}) {
  if (!plan) return <div className="calibration-plan"><div className="setup-note">Loading TR-derived calibration plan...</div></div>;
  const sourceByIndex = new Map(plan.sources.map(source => [source.index, source]));
  const rangeHz = Math.max(0, numberOrDefault(sweep.rangeHz, 1200));
  const stepHz = Math.max(1, numberOrDefault(sweep.stepHz, 300));
  const warmupSec = Math.max(0, numberOrDefault(sweep.warmupSec, 20));
  const durationSec = Math.max(1, numberOrDefault(sweep.durationSec, 240));
  const passCount = Math.floor((rangeHz * 2) / stepHz) + 1;
  const estimatedSeconds = passCount * (warmupSec + durationSec);
  return <div className="calibration-plan">
    <div className="setup-job-head">
      <strong>TR-derived tuning plan</strong>
      <span className="pill">{plan.systems.length} system(s)</span>
      <span className="pill">{plan.sources.length} SDR source(s)</span>
      <span className="pill">{passCount} passes/tuner</span>
      <span className="pill">About {formatElapsed(estimatedSeconds)} each</span>
    </div>
    <div className="setup-note">{plan.diagnostics}</div>
    {plan.warnings.length > 0 && <div className="setup-warning-list">{plan.warnings.map(warning => <div key={warning}>{warning}</div>)}</div>}
    <div className="calibration-guided-grid">
      {plan.systems.map(system => <div className="calibration-system-card" key={system.shortName}>
        <div className="setup-job-head">
          <strong>{system.shortName}</strong>
          <span className="pill">{system.modulation || "qpsk"}</span>
          <span className="pill">Needs {system.requiredSdrCount} SDR tuner{system.requiredSdrCount === 1 ? "" : "s"}</span>
        </div>
        <div className="calibration-plan-grid">
          <div><span>Control channels</span><strong>{formatFrequencyList(system.controlChannelsHz)}</strong><small>TR rotates among these; the active one is whichever control channel is currently carrying decode messages. Start with the first listed channel, then try the others if decode is poor.</small></div>
          <div><span>Voice frequencies</span><strong>{system.voiceFrequenciesHz.length ? formatFrequencyList(system.voiceFrequenciesHz) : "Not needed for control-channel tuning"}</strong><small>TR configs normally do not list every voice frequency. Calibration tunes control-channel decode, so this is not a blocker.</small></div>
          <div><span>Required tuner coverage</span><strong>{system.requiredRanges.map(range => formatFrequencyRange(range.lowHz, range.highHz)).join("; ") || "None"}</strong><small>Calculated from known control channels and configured SDR sample rate.</small></div>
        </div>
        {system.warnings.length > 0 && <div className="setup-warning-list">{system.warnings.map(warning => <div key={warning}>{warning}</div>)}</div>}
        {system.proposedSourceIndexes.map(sourceIndex => {
          const source = sourceByIndex.get(sourceIndex);
          if (!source) return null;
          const input = inputs[String(source.index)] ?? { gain: source.gain ?? "", errorHz: source.errorHz ? String(source.errorHz) : "", ppm: "" };
          const frequency = system.controlChannelsHz[0] ?? source.centerFrequency;
          const command = buildGuidedSweepCommand(system.shortName, system.modulation, source, input, frequency, sweep);
          const parameters = buildGuidedSweepParameters(system.shortName, system.modulation, source, input, frequency, sweep);
          return <div className="calibration-source-card" key={`${system.shortName}-${source.index}`}>
            <div className="setup-job-head">
              <strong>Source {source.index}</strong>
              <span className="pill">{source.serial ? `rtl=${source.serial}` : source.device || "rtl source"}</span>
              <span className="pill">{formatHz(source.centerFrequency)} center</span>
              <span className="pill">{formatHz(source.sampleRate)} rate</span>
            </div>
            <div className="settings-grid">
              <SettingInput label="GQRX gain" description={`Current config value: ${source.gain || "blank"}.`} value={input.gain} onChange={v => setInput(String(source.index), "gain", v)} />
              <SettingInput label="Error Hz" description={`Current config value: ${source.errorHz || 0}. Overrides PPM if supplied.`} value={input.errorHz} onChange={v => setInput(String(source.index), "errorHz", v)} />
              <SettingInput label="PPM" description="Optional. Used only if Error Hz is blank." value={input.ppm} onChange={v => setInput(String(source.index), "ppm", v)} />
            </div>
            <div className="setup-button-row">
              <button disabled={busy} onClick={() => void openGqrx(source.index, frequency)}>Open GQRX at control channel</button>
              <button disabled={busy} onClick={() => void openGqrx(source.index, source.centerFrequency)}>Open GQRX at SDR center</button>
            </div>
            <strong>Planned sweep</strong>
            <pre className="command-box">{command}</pre>
            <button disabled={busy} onClick={() => void runSweep(parameters)}>Run this sweep in wizard</button>
          </div>;
        })}
      </div>)}
    </div>
  </div>;
}

function SetupReview({ status, requiredOpen, restartVerified }: { status: SetupStatus; requiredOpen: SetupStatus["checks"]; restartVerified: boolean }) {
  const values: any = status.values ?? {};
  const tr = values.trunkRecorder ?? {};
  const transcription = values.transcription ?? {};
  const ai = values.aiInsights ?? {};
  const sftp = values.sftpImport ?? {};
  return <div className="setup-review">
    <h4>Review Before Complete</h4>
    <div><span>TR config</span><code>{tr.configPath ?? "/etc/trunk-recorder/config.json"}</code></div>
    <div><span>Talkgroups</span><code>{tr.talkgroupsPath ?? "/etc/trunk-recorder/talkgroups.csv"}</code></div>
    <div><span>Transcription</span><code>{transcription.provider ?? "none"}</code></div>
    <div><span>AI insights</span><code>{ai.enabled ? ai.openAiModel || "enabled" : "disabled"}</code></div>
    <div><span>SFTP import</span><code>{sftp.enabled ? sftp.host || "enabled" : "disabled"}</code></div>
    {requiredOpen.length > 0
      ? <small>{requiredOpen.length} required section{requiredOpen.length === 1 ? "" : "s"} still need validation.</small>
      : restartVerified
        ? <small>Restart verification passed. Completing setup enables normal operation.</small>
        : <small>Run Apply & Restart before completing setup.</small>}
  </div>;
}

function ArtifactReport({ report }: { report: SetupArtifactReport }) {
  return <div className="setup-artifacts">
    <strong>{report.hasBlockingArtifacts ? "Existing TR artifacts found" : "Artifact check"}</strong>
    {report.hasBlockingArtifacts && <p className="setup-note">These only block a clean source-build reinstall. They are expected when side-loading an existing working TR setup.</p>}
    {report.artifacts.map(artifact => <div className={artifact.exists ? "found" : "ok"} key={artifact.path}>
      <span>{artifact.exists ? "Found" : "Missing"}</span>
      <code>{artifact.path}</code>
      <small>{artifact.notes}</small>
    </div>)}
    {report.manualCommands.length > 0 && <pre>{report.manualCommands.join("\n")}</pre>}
  </div>;
}

function SetupJobPanel({ job, logs }: { job: Job | null; logs: JobLog[] }) {
  if (!job) return null;
  return <div className="setup-job-panel">
    <div className="setup-job-head">
      <strong>Job {job.id}: {label(job.type)}</strong>
      <span className={`job-status ${job.status}`}>{job.status}</span>
      <span>{job.completed}/{job.total}</span>
    </div>
    <div className="muted">{job.message}</div>
    <pre>{logs.length ? logs.map(log => `[${new Date(log.timestampUtc).toLocaleTimeString()}] ${log.stream}: ${log.text}`).join("\n") : "Waiting for job output..."}</pre>
  </div>;
}

function SetupJobDrawer({ job, logs, running, onStopCalibration, stopping, open, setOpen }: { job: Job | null; logs: JobLog[]; running: boolean; onStopCalibration: () => Promise<void>; stopping: boolean; open: boolean; setOpen: (open: boolean) => void }) {
  const isCalibration = Boolean(job?.type.includes("calibration"));
  return <div className={`setup-job-drawer ${open ? "open" : ""}`}>
    <button type="button" className="setup-job-tab" onClick={() => setOpen(!open)}>{open ? "Hide logs" : "Logs"}</button>
    <div className="setup-job-head">
      <strong>{job ? `Job ${job.id}: ${label(job.type)}` : "Setup logs"}</strong>
      {job && <span className={`job-status ${job.status}`}>{job.status}</span>}
      {job && <span>{job.completed}/{job.total}</span>}
      <span className="muted">{job?.message ?? "No setup job has run in this session."}</span>
      {isCalibration && running && <button disabled={stopping} onClick={() => void onStopCalibration()}>{stopping ? "Stopping..." : "Stop calibration job"}</button>}
    </div>
    <pre>{logs.length ? logs.map(log => `[${new Date(log.timestampUtc).toLocaleTimeString()}] ${log.stream}: ${log.text}`).join("\n") : job ? "Waiting for job output..." : "No setup job output yet."}</pre>
  </div>;
}

function TrConfigDraftPreview({ draft }: { draft: SetupTrConfigDraft }) {
  return <div className="tr-config-draft">
    <div className="setup-job-head">
      <strong>{draft.systems.length} system/site draft(s)</strong>
      <span>{draft.sources.length} SDR source(s)</span>
    </div>
    <div className="muted">{draft.diagnostics}</div>
    {draft.warnings.length > 0 && <div className="setup-warning-list">{draft.warnings.map(warning => <div key={warning}>{warning}</div>)}</div>}
    <table className="table">
      <thead><tr><th>Site</th><th>Short name</th><th>Serial</th><th>Center</th><th>Control</th><th>Coverage</th><th>Warning</th></tr></thead>
      <tbody>{draft.systems.map(system => {
        const source = draft.sources.find(s => s.label === system.shortName);
        return <tr key={`${system.shortName}-${system.siteName}`}>
          <td>{system.siteName}</td>
          <td>{system.shortName}</td>
          <td>{system.assignedSerial || "unassigned"}</td>
          <td>{formatHz(system.centerFrequency)}</td>
          <td>{system.controlChannelsMhz.map(f => f.toFixed(4)).join(", ")}</td>
          <td>{source ? `${source.coveredFrequenciesMhz.length} covered / ${source.omittedFrequenciesMhz.length} omitted` : `${system.frequenciesMhz.length} frequencies`}</td>
          <td>{system.warning}</td>
        </tr>;
      })}</tbody>
    </table>
  </div>;
}

function SdrDetectionPanel({ detection }: { detection: SetupSdrDetection }) {
  return <div className="sdr-detection">
    <strong>{detection.message}</strong>
    {detection.devices.length > 0 && <table className="table">
      <thead><tr><th>#</th><th>Serial</th><th>Device</th><th>Warning</th></tr></thead>
      <tbody>{detection.devices.map(device => <tr key={`${device.index}-${device.serial || device.usbLine}`}>
        <td>{device.index}</td>
        <td>{device.serial || "unknown"}</td>
        <td>{device.label || device.usbLine}</td>
        <td>{device.warning}</td>
      </tr>)}</tbody>
    </table>}
    <details><summary>Raw SDR output</summary><pre>{detection.rawOutput}</pre></details>
  </div>;
}

function TalkgroupPreviewTable({ preview, updateRow }: { preview: SetupTalkgroupPreview; updateRow: (index: number, patch: Partial<SetupTalkgroupRow>) => void }) {
  const categories = ["police", "fire", "ems", "traffic", "other"];
  return <div className="talkgroup-preview">
    <div className="setup-job-head">
      <strong>{preview.includedCount.toLocaleString()} included / {preview.excludedCount.toLocaleString()} excluded</strong>
      {Object.entries(preview.includedByCategory).map(([category, count]) => <span className={`pill category-${category}`} key={category}>{label(category)} {count}</span>)}
    </div>
    <div className="muted">{preview.diagnostics}</div>
    <div className="talkgroup-preview-table">
      <table className="table">
        <thead><tr><th>Use</th><th>ID</th><th>Mode</th><th>Alpha</th><th>Description</th><th>Tag</th><th>Category</th><th>Reason</th></tr></thead>
        <tbody>{preview.rows.slice(0, 500).map((row, index) => <tr className={row.included ? "" : "excluded-row"} key={row.id}>
          <td><input type="checkbox" checked={row.included} onChange={e => updateRow(index, { included: e.target.checked })} /></td>
          <td><input type="number" value={row.id} onChange={e => updateRow(index, { id: numberOrZero(e.target.value) })} /></td>
          <td>{row.mode}</td>
          <td><input value={row.alphaTag} onChange={e => updateRow(index, { alphaTag: e.target.value })} /></td>
          <td><input value={row.description} onChange={e => updateRow(index, { description: e.target.value })} /></td>
          <td><input value={row.tag} onChange={e => updateRow(index, { tag: e.target.value })} /></td>
          <td><select value={row.opsCategory} onChange={e => updateRow(index, { opsCategory: e.target.value })}>{categories.map(c => <option key={c} value={c}>{label(c)}</option>)}</select></td>
          <td>{row.exclusionReason}</td>
        </tr>)}</tbody>
      </table>
      {preview.rows.length > 500 && <div className="muted">Showing first 500 rows. Save still writes all included rows.</div>}
    </div>
  </div>;
}

function SettingsView({ settingsSections, settingsLoadState, reload, profileState, setProfileState }: { settingsSections: Record<string, any>; settingsLoadState: { loading: boolean; version: number; message: string; error: boolean }; reload: () => Promise<void>; profileState: ProfileState | null; setProfileState: (value: ProfileState | null) => void }) {
  const [sections, setSections] = useState<Record<string, any>>({});
  const [message, setMessage] = useState("");
  const [messageKind, setMessageKind] = useState<"info" | "error">("info");
  const [savingSection, setSavingSection] = useState("");
  const [sectionStatus, setSectionStatus] = useState<Record<string, { kind: "ok" | "error" | "info"; text: string }>>({});
  const [testingSection, setTestingSection] = useState("");
  const [models, setModels] = useState<any[]>([]);
  const [aiModels, setAiModels] = useState<string[]>([]);
  const [aiModelsMessage, setAiModelsMessage] = useState("");
  const [modelBusy, setModelBusy] = useState("");

  useEffect(() => {
    setSections(withSettingsDefaults(settingsSections));
    if (settingsLoadState.message) {
      setMessageKind(settingsLoadState.error ? "error" : "info");
      setMessage(settingsLoadState.message);
    }
  }, [settingsSections, settingsLoadState.version]);
  useEffect(() => { void loadModels(); }, []);
  useEffect(() => {
    if (!sections["ai-insights"]?.enabled) return;
    void loadAiModels();
  }, [sections["ai-insights"]?.enabled, sections["ai-insights"]?.openAiBaseUrl]);
  useEffect(() => {
    const current = sections["ai-insights"]?.openAiModel;
    if (!sections["ai-insights"]?.enabled || aiModels.length === 0) return;
    if (!current || !aiModels.includes(current))
      update("ai-insights", ["openAiModel"], aiModels[0]);
  }, [aiModels, sections["ai-insights"]?.enabled, sections["ai-insights"]?.openAiModel]);
  useEffect(() => {
    if (!modelBusy) return;
    const timer = window.setInterval(() => void loadModels(), 1500);
    return () => window.clearInterval(timer);
  }, [modelBusy]);

  const engine = sections.engine ?? {};
  const transcription = sections.transcription ?? {};
  const aiInsights = sections["ai-insights"] ?? {};
  const tr = sections.tr ?? {};
  const alerts = sections.alerts ?? {};

  function update(section: string, path: string[], value: any) {
    setSections(current => {
      const next = cloneSettings(current);
      const root = next[section] ?? {};
      let target = root;
      for (const key of path.slice(0, -1)) {
        target[key] = target[key] && typeof target[key] === "object" ? target[key] : {};
        target = target[key];
      }
      target[path[path.length - 1]] = value;
      next[section] = root;
      return next;
    });
  }

  async function save(section: string) {
    setSavingSection(section);
    setMessageKind("info");
    setMessage(`Saving ${label(section)} settings...`);
    try {
      await api.request(`/api/v1/settings/${section}`, { method: "POST", body: JSON.stringify({ values: sections[section] ?? {} }) });
      setMessageKind("info");
      setMessage(`${label(section)} settings saved`);
      setSectionStatus(current => ({ ...current, [section]: { kind: "ok", text: "Saved" } }));
      await reload();
    } catch (error) {
      const text = error instanceof Error && error.message ? error.message : "Save failed.";
      setMessageKind("error");
      setMessage(`Save failed: ${text}`);
      setSectionStatus(current => ({ ...current, [section]: { kind: "error", text } }));
    } finally {
      setSavingSection("");
    }
  }

  async function test(section: string) {
    setTestingSection(section);
    setSectionStatus(current => ({ ...current, [section]: { kind: "info", text: "Testing..." } }));
    try {
      const result = await api.request<any>(`/api/v1/settings/${section}/test`, { method: "POST" });
      setSectionStatus(current => ({ ...current, [section]: { kind: result.ok ? "ok" : "error", text: result.message ?? "Test complete" } }));
    } catch (error) {
      const text = error instanceof Error && error.message ? error.message : "Test failed.";
      setSectionStatus(current => ({ ...current, [section]: { kind: "error", text } }));
    } finally {
      setTestingSection("");
    }
  }

  async function saveWithTest(section: string) {
    await save(section);
    await test(section);
  }

  async function loadModels() {
    try {
      setModels(await api.request<any[]>("/api/v1/settings/transcription/models"));
    } catch {
      setModels([]);
    }
  }

  async function loadAiModels() {
    setAiModelsMessage("Loading models...");
    try {
      const result = await api.request<any>("/api/v1/settings/ai-insights/models");
      const rows = Array.isArray(result.models) ? result.models : [];
      setAiModels(rows);
      setAiModelsMessage(result.message ?? (rows.length ? `Found ${rows.length} model(s).` : "No models returned."));
    } catch (error) {
      setAiModels([]);
      setAiModelsMessage(error instanceof Error ? error.message : "Unable to load models.");
    }
  }

  async function downloadModel(model: string) {
    setModelBusy(model);
    try {
      const result = await api.request<any>(`/api/v1/settings/transcription/models/${model}/download`, { method: "POST" });
      setSectionStatus(current => ({ ...current, transcription: { kind: result.ok ? "ok" : "error", text: result.message ?? "Download complete" } }));
      if (result.ok !== false && result.path && model.startsWith("whisper-"))
        update("transcription", ["whisperModelFile"], result.path);
      await loadModels();
    } finally {
      setModelBusy("");
    }
  }

  async function deleteModel(model: string) {
    setModelBusy(model);
    try {
      const result = await api.request<any>(`/api/v1/settings/transcription/models/${model}`, { method: "DELETE" });
      setSectionStatus(current => ({ ...current, transcription: { kind: result.ok ? "ok" : "error", text: result.message ?? "Removed" } }));
      await loadModels();
    } finally {
      setModelBusy("");
    }
  }

  async function installFasterWhisper() {
    setModelBusy("faster-whisper");
    setSectionStatus(current => ({ ...current, transcription: { kind: "info", text: "Installing faster-whisper support..." } }));
    try {
      const job = await api.request<Job>("/api/v1/setup/jobs", { method: "POST", body: JSON.stringify({ action: "faster-whisper-prime", confirmed: true }) });
      setSectionStatus(current => ({ ...current, transcription: { kind: "info", text: `Started faster-whisper install job ${job.id}. Watch System > Pizzad > Queue / Jobs for logs.` } }));
    } catch (error) {
      const text = error instanceof Error ? error.message : String(error);
      setSectionStatus(current => ({ ...current, transcription: { kind: "error", text } }));
    } finally {
      setModelBusy("");
    }
  }

  return <div className="settings-page">
    <div className="settings-header">
      <h2>Settings</h2>
      <div className="settings-header-actions">
        <span className={messageKind === "error" ? "settings-message error" : "settings-message"}>{message || "Changes save by section."}</span>
      </div>
    </div>

    <div className="settings-flow">
      <SettingsCard title="Transcription" description="Controls how individual calls become text. This is separate from AI summaries and incidents." busy={savingSection === "transcription"} testing={testingSection === "transcription"} status={sectionStatus.transcription} onSave={() => save("transcription")} onTest={() => saveWithTest("transcription")}>
        <SettingSelect label="Engine" description="Choose the transcription backend for new calls." value={transcription.provider} options={["none", "whisper", "faster-whisper", "vosk", "lmstudio", "openai"]} onChange={v => update("transcription", ["provider"], v)} />
        {transcription.provider === "whisper" && <>
          <SettingSelect label="Whisper model" description="Select a managed model. Download missing models below." value={modelIdForPath(models, "whisper", transcription.whisperModelFile)} options={modelOptions(models, "whisper")} onChange={v => update("transcription", ["whisperModelFile"], modelPath(models, v))} />
          <SettingInput label="Live workers" description="Number of calls transcribed at the same time. Requires pizzad restart. On Raspberry Pi, try 1 or 2; higher values can reduce overall stability." type="number" value={transcription.liveTranscriptionWorkers ?? 1} onChange={v => update("transcription", ["liveTranscriptionWorkers"], numberOrZero(v))} />
          <SettingInput label="Threads per worker" description="CPU threads used by each Whisper worker. Requires pizzad restart. Test worker/thread shape on the target host before increasing concurrency." type="number" value={transcription.whisperThreads ?? 2} onChange={v => update("transcription", ["whisperThreads"], numberOrZero(v))} />
          <SettingInput label="Queue pressure threshold" description="Live queue depth where newer live calls are prioritized over stale pending calls." type="number" value={transcription.livePressureQueueDepth ?? 200} onChange={v => update("transcription", ["livePressureQueueDepth"], numberOrZero(v))} />
          <ModelManager engine="whisper" rows={models} busy={modelBusy} selectedPath={transcription.whisperModelFile} onUse={path => update("transcription", ["whisperModelFile"], path)} onDownload={downloadModel} onDelete={deleteModel} />
        </>}
        {transcription.provider === "vosk" && <>
          <SettingSelect label="Vosk model" description="Select a managed model. Download missing models below." value={modelIdForPath(models, "vosk", transcription.voskModelPath)} options={modelOptions(models, "vosk")} onChange={v => update("transcription", ["voskModelPath"], modelPath(models, v))} />
          <ModelManager engine="vosk" rows={models} busy={modelBusy} selectedPath={transcription.voskModelPath} onUse={path => update("transcription", ["voskModelPath"], path)} onDownload={downloadModel} onDelete={deleteModel} />
        </>}
        {transcription.provider === "faster-whisper" && <>
          <SettingSelect label="Model" description="Hugging Face faster-whisper model name. Tiny/int8 is the RPI-safe default." value={transcription.fasterWhisperModel ?? "tiny"} options={["tiny", "base", "small", "medium"]} onChange={v => update("transcription", ["fasterWhisperModel"], v)} />
          <SettingSelect label="Compute type" description="int8 is fastest/smallest on CPU; float32 is usually too slow for RPI." value={transcription.fasterWhisperComputeType ?? "int8"} options={["int8", "int8_float16", "float32"]} onChange={v => update("transcription", ["fasterWhisperComputeType"], v)} />
          <SettingInput label="Python runtime" description="Managed venv path. Install support creates this automatically." value={transcription.fasterWhisperPythonPath ?? "/opt/pizzawave/venv/faster-whisper/bin/python"} onChange={v => update("transcription", ["fasterWhisperPythonPath"], v)} />
          <SettingInput label="Live workers" description="Number of long-lived faster-whisper workers. Requires pizzad restart." type="number" value={transcription.liveTranscriptionWorkers ?? 1} onChange={v => update("transcription", ["liveTranscriptionWorkers"], numberOrZero(v))} />
          <SettingInput label="CPU threads per worker" description="Passed to ctranslate2. Requires pizzad restart." type="number" value={transcription.whisperThreads ?? 2} onChange={v => update("transcription", ["whisperThreads"], numberOrZero(v))} />
          <SettingInput label="Queue pressure threshold" description="Live queue depth where newer live calls are prioritized over stale pending calls." type="number" value={transcription.livePressureQueueDepth ?? 200} onChange={v => update("transcription", ["livePressureQueueDepth"], numberOrZero(v))} />
          <SettingCheckbox label="VAD filter" description="Optional faster-whisper voice activity filter. Leave off until quality is validated." checked={!!transcription.fasterWhisperVadFilter} onChange={v => update("transcription", ["fasterWhisperVadFilter"], v)} />
          <div className="setting-inline-actions"><button type="button" disabled={modelBusy === "faster-whisper"} onClick={() => void installFasterWhisper()}>{modelBusy === "faster-whisper" ? "Installing..." : "Install faster-whisper support"}</button><span className="muted">Installs the optional Python venv outside the base package.</span></div>
        </>}
        {(transcription.provider === "lmstudio" || transcription.provider === "openai") && <>
          <SettingInput label="Base URL" description="OpenAI-compatible audio transcription endpoint base URL." value={transcription.openAiBaseUrl} onChange={v => update("transcription", ["openAiBaseUrl"], v)} />
          <SettingInput label="Model" description="Model name sent to the audio transcription endpoint." value={transcription.openAiModel} onChange={v => update("transcription", ["openAiModel"], v)} />
          <SettingInput label="API key" description="Optional bearer token for remote transcription endpoints." type="password" value={transcription.openAiApiKey} onChange={v => update("transcription", ["openAiApiKey"], v)} />
        </>}
        {transcription.provider === "none" && <p className="setting-note">New calls will be stored without transcription.</p>}
      </SettingsCard>

      <SettingsCard title="AI Insights" description="One switch controls all LLM usage: call summaries, incidents, and troubleshooting recommendations." busy={savingSection === "ai-insights"} testing={testingSection === "ai-insights"} status={sectionStatus["ai-insights"]} onSave={() => saveWithTest("ai-insights")} onTest={() => saveWithTest("ai-insights")}>
        <SettingCheckbox label="Enable AI usage" description="When off, pizzad will not call LM Studio or other LLM endpoints." checked={aiInsights.enabled} onChange={v => update("ai-insights", ["enabled"], v)} />
        <SettingInput label="Base URL" description="OpenAI-compatible chat endpoint base URL, often an LM Studio server." value={aiInsights.openAiBaseUrl} onChange={v => update("ai-insights", ["openAiBaseUrl"], v)} />
        {aiInsights.enabled && aiModels.length > 0
          ? <SettingSelect label="Model" description="Chat model used for summaries, incidents, and recommendations." value={aiInsights.openAiModel} options={aiModels} onChange={v => update("ai-insights", ["openAiModel"], v)} />
          : <SettingInput label="Model" description="Chat model used for summaries, incidents, and recommendations." value={aiInsights.openAiModel} onChange={v => update("ai-insights", ["openAiModel"], v)} />}
        {aiInsights.enabled && <div className="setting-inline-actions"><button type="button" onClick={() => void loadAiModels()}>Refresh models</button><span className="muted">{aiModelsMessage}</span></div>}
        <SettingInput label="API key" description="Optional bearer token. LM Studio local/link setups may leave this blank." type="password" value={aiInsights.openAiApiKey} onChange={v => update("ai-insights", ["openAiApiKey"], v)} />
        <SettingInput label="Timeout (ms)" description="Maximum wait for a single LLM request." type="number" value={aiInsights.timeoutMs} onChange={v => update("ai-insights", ["timeoutMs"], numberOrZero(v))} />
        <SettingInput label="Retries" description="Retry attempts after a failed LLM request." type="number" value={aiInsights.maxRetries} onChange={v => update("ai-insights", ["maxRetries"], numberOrZero(v))} />
        <SettingInput label="Max manual lookback (hours)" description="Largest recent range allowed for Generate summaries/incidents. Keep this low on constrained hosts or slower remote LLM setups." type="number" value={aiInsights.maxManualLookbackHours ?? 24} onChange={v => update("ai-insights", ["maxManualLookbackHours"], numberOrZero(v))} />
        <SettingInput label="Max manual calls" description="Hard stop before sending too many calls to the LLM in one generation job." type="number" value={aiInsights.maxManualSummaryCalls ?? 300} onChange={v => update("ai-insights", ["maxManualSummaryCalls"], numberOrZero(v))} />
        <SettingInput label="Max manual windows" description="Hard stop on the number of LLM summary windows produced by one generation job." type="number" value={aiInsights.maxManualSummaryWindows ?? 20} onChange={v => update("ai-insights", ["maxManualSummaryWindows"], numberOrZero(v))} />
        <SettingInput label="Max queue depth to run" description="Blocks manual generation while transcription/import backlog is above this value. Use 0 to disable this check." type="number" value={aiInsights.maxQueueDepthForManualSummary ?? 100} onChange={v => update("ai-insights", ["maxQueueDepthForManualSummary"], numberOrZero(v))} />
      </SettingsCard>

      <SettingsCard title="Alerts / Email" description="Outbound notification settings for live alert matches. Imported calls still store matches but suppress live notifications." busy={savingSection === "alerts"} testing={testingSection === "alerts"} status={sectionStatus.alerts} onSave={() => save("alerts")} onTest={() => saveWithTest("alerts")}>
        <SettingCheckbox label="Enable email alerts" description="Turns live outbound email delivery on or off." checked={alerts.emailEnabled} onChange={v => update("alerts", ["emailEnabled"], v)} />
        <SettingSelect label="Email provider" description="SMTP preset used by the alert sender." value={alerts.emailProvider} options={["gmail", "yahoo"]} onChange={v => update("alerts", ["emailProvider"], v)} />
        <SettingInput label="Email address" description="Sender account used for alert delivery." value={alerts.emailUser} onChange={v => update("alerts", ["emailUser"], v)} />
        <SettingInput label="App password" description="Provider-specific app password or SMTP credential." type="password" value={alerts.emailPassword} onChange={v => update("alerts", ["emailPassword"], v)} />
        <p className="setting-note">{alerts.rules?.length ?? 0} alert rule(s) configured. Rule management remains in the alerts settings workflow.</p>
      </SettingsCard>

      <ProfilesSettingsCard profileState={profileState} setProfileState={setProfileState} reload={reload} />
      <TalkgroupCatalogSettingsCard />

      <div className="card settings-card">
        <div className="settings-card-meta">
          <h3>System Info</h3>
          <p>Installer-owned paths and listener settings. Change these through package config, service files, or helper scripts.</p>
        </div>
        <div className="settings-fields">
          <SettingValue label="Web endpoint" value={`${engine.server?.httpBind ?? "0.0.0.0"}:${engine.server?.httpPort ?? 8080}`} />
          <SettingValue label="Callstream endpoint" value={`${engine.ingest?.callstreamBind ?? "127.0.0.1"}:${engine.ingest?.callstreamPort ?? 9123}`} />
          <SettingValue label="Database path" value={engine.storage?.databasePath} />
          <SettingValue label="Audio root" value={engine.storage?.audioRoot} />
          <SettingValue label="TR config path" value={tr.configPath} />
          <SettingValue label="Talkgroup catalog" value={tr.talkgroupCatalogPath} />
          <SettingValue label="Talkgroups CSV" value={tr.talkgroupsPath} />
          <SettingValue label="TR service name" value={tr.logServiceName} />
        </div>
      </div>
    </div>
  </div>;
}

function SettingsCard({ title, description, children, busy, testing, status, onSave, onTest }: { title: string; description: string; children: React.ReactNode; busy?: boolean; testing?: boolean; status?: { kind: "ok" | "error" | "info"; text: string }; onSave: () => Promise<void>; onTest?: () => Promise<void> }) {
  return <div className="card settings-card">
    <div className="settings-card-meta">
      <h3>{title}</h3>
      <p>{description}</p>
      <div className="settings-card-actions">
        {onTest && <button disabled={testing || busy} onClick={() => void onTest()}>{testing ? "Testing..." : "Test"}</button>}
        <button disabled={busy || testing} onClick={() => void onSave()}>{busy ? "Saving..." : "Save"}</button>
      </div>
      {status && <span className={`section-status ${status.kind}`}>{status.text}</span>}
    </div>
    <div className="settings-fields">{children}</div>
  </div>;
}

function TalkgroupCatalogSettingsCard() {
  const [response, setResponse] = useState<TalkgroupCatalogResponse | null>(null);
  const [draft, setDraft] = useState<TalkgroupCatalogDocument | null>(null);
  const [filter, setFilter] = useState("");
  const [busy, setBusy] = useState("");
  const [message, setMessage] = useState("");
  const [restartRecommended, setRestartRecommended] = useState(false);

  useEffect(() => { void loadCatalog(); }, []);

  async function loadCatalog() {
    setBusy("load");
    try {
      const loaded = await api.request<TalkgroupCatalogResponse>("/api/v1/talkgroups/catalog");
      setResponse(loaded);
      setDraft(cloneSettings(loaded.document));
      setRestartRecommended(Boolean(loaded.trRestartRecommended));
      setMessage("");
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Unable to load talkgroup catalog.");
    } finally {
      setBusy("");
    }
  }

  function updateItem(id: number, patch: Partial<TalkgroupCatalogItem>) {
    setDraft(current => current && ({ ...current, items: current.items.map(item => item.id === id ? { ...item, ...patch } : item) }));
  }

  function addItem() {
    const nextId = Math.max(0, ...(draft?.items.map(i => i.id) ?? [0])) + 1;
    const now = new Date().toISOString();
    setDraft(current => ({
      schemaVersion: current?.schemaVersion ?? 1,
      updatedAtUtc: current?.updatedAtUtc ?? now,
      items: [...(current?.items ?? []), { id: nextId, mode: "D", alphaTag: "", description: "", tag: "", sourceCategory: "", opsCategory: "other", enabled: true, source: "manual", notes: "", updatedAtUtc: now }]
    }));
  }

  function deleteItem(id: number) {
    setDraft(current => current && ({ ...current, items: current.items.filter(item => item.id !== id) }));
    setRestartRecommended(true);
  }

  async function saveCatalog() {
    if (!draft) return;
    setBusy("save");
    setMessage("Saving catalog and regenerating trunk-recorder CSV...");
    try {
      const saved = await api.request<TalkgroupCatalogSaveResult>("/api/v1/talkgroups/catalog", { method: "PUT", body: JSON.stringify(draft) });
      setRestartRecommended(saved.trRestartRecommended);
      setMessage(`Saved ${saved.count.toLocaleString()} talkgroups. Generated ${saved.generatedCsvPath}. Restart trunk-recorder before expecting ingest changes.`);
      await loadCatalog();
      setRestartRecommended(true);
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Catalog save failed.");
    } finally {
      setBusy("");
    }
  }

  async function generateCsv() {
    setBusy("csv");
    try {
      const result = await api.request<TalkgroupTrCsvResult>("/api/v1/talkgroups/catalog/generate-tr-csv", { method: "POST" });
      setRestartRecommended(true);
      setMessage(`Generated ${result.path} with ${result.enabledCount.toLocaleString()} enabled talkgroups. Restart trunk-recorder to apply it.`);
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "CSV generation failed.");
    } finally {
      setBusy("");
    }
  }

  async function restartTr() {
    setBusy("restart");
    try {
      const job = await api.request<Job>("/api/v1/system/services/trunk-recorder/restart", { method: "POST" });
      setRestartRecommended(false);
      setMessage(`Started trunk-recorder restart job ${job.id}.`);
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Restart failed.");
    } finally {
      setBusy("");
    }
  }

  const needle = filter.trim().toLowerCase();
  const rows = (draft?.items ?? [])
    .filter(item => !needle ||
      String(item.id).includes(needle) ||
      item.alphaTag.toLowerCase().includes(needle) ||
      item.description.toLowerCase().includes(needle) ||
      item.tag.toLowerCase().includes(needle) ||
      item.opsCategory.toLowerCase().includes(needle))
    .sort((a, b) => a.id - b.id)
    .slice(0, 500);
  const enabledCount = draft?.items.filter(item => item.enabled).length ?? 0;

  return <div className="card settings-card wide">
    <div className="settings-card-meta">
      <h3>Talkgroup Catalog</h3>
      <p>Catalog changes affect future ingest only. Existing calls keep their stored talkgroup label and category.</p>
      <div className="settings-card-actions">
        <button disabled={Boolean(busy)} onClick={() => void loadCatalog()}>{busy === "load" ? "Loading..." : "Reload"}</button>
        <button disabled={Boolean(busy) || !draft} onClick={addItem}>Add</button>
        <button disabled={Boolean(busy) || !draft} onClick={() => void generateCsv()}>{busy === "csv" ? "Generating..." : "Generate CSV"}</button>
        <button disabled={Boolean(busy) || !draft} onClick={() => void saveCatalog()}>{busy === "save" ? "Saving..." : "Save"}</button>
        <button className="danger-button" disabled={Boolean(busy) || !restartRecommended} onClick={() => void restartTr()}>{busy === "restart" ? "Restarting..." : "Restart TR"}</button>
      </div>
      {message && <span className={message.toLowerCase().includes("fail") || message.toLowerCase().includes("unable") ? "section-status error" : "section-status ok"}>{message}</span>}
    </div>
    <div className="settings-fields">
      <div className="settings-grid">
        <SettingValue label="Catalog JSON" value={response?.catalogPath} />
        <SettingValue label="Generated TR CSV" value={response?.generatedCsvPath} />
        <SettingValue label="Rows" value={`${draft?.items.length ?? 0} total / ${enabledCount} enabled`} />
      </div>
      {response?.warning && <p className="settings-message">{response.warning}</p>}
      {restartRecommended && <p className="settings-message error">trunk-recorder restart recommended. Disabled and deleted talkgroups are excluded from the generated CSV and will no longer be ingested.</p>}
      <input placeholder="Filter by ID, label, tag, or category" value={filter} onChange={e => setFilter(e.target.value)} />
      <div className="talkgroup-catalog-table">
        <table className="table compact-table">
          <thead><tr><th>Enabled</th><th>ID</th><th>Mode</th><th>Alpha</th><th>Description</th><th>Tag</th><th>Ops</th><th>Source</th><th>Notes</th><th></th></tr></thead>
          <tbody>{rows.map((item, index) => <tr className={item.enabled ? "" : "excluded-row"} key={`${item.id}-${index}`}>
            <td><input type="checkbox" checked={item.enabled} onChange={e => { updateItem(item.id, { enabled: e.target.checked }); setRestartRecommended(true); }} /></td>
            <td><input type="number" value={item.id} onChange={e => updateItem(item.id, { id: numberOrZero(e.target.value) })} /></td>
            <td><input value={item.mode} onChange={e => updateItem(item.id, { mode: e.target.value })} /></td>
            <td><input value={item.alphaTag} onChange={e => updateItem(item.id, { alphaTag: e.target.value })} /></td>
            <td><input value={item.description} onChange={e => updateItem(item.id, { description: e.target.value })} /></td>
            <td><input value={item.tag} onChange={e => updateItem(item.id, { tag: e.target.value })} /></td>
            <td><select value={item.opsCategory} onChange={e => updateItem(item.id, { opsCategory: e.target.value })}>{categories.map(c => <option value={c} key={c}>{label(c)}</option>)}</select></td>
            <td>{item.source || "--"}</td>
            <td><input value={item.notes} onChange={e => updateItem(item.id, { notes: e.target.value })} /></td>
            <td><button className="danger-button" onClick={() => deleteItem(item.id)}>Delete</button></td>
          </tr>)}</tbody>
        </table>
      </div>
      {(draft?.items.length ?? 0) > 500 && <div className="muted">Showing first 500 matching rows. Save still writes the full catalog.</div>}
    </div>
  </div>;
}

function ProfilesSettingsCard({ profileState, setProfileState, reload }: { profileState: ProfileState | null; setProfileState: (value: ProfileState | null) => void; reload: () => Promise<void> }) {
  const [draft, setDraft] = useState<ProfileState | null>(profileState);
  const [message, setMessage] = useState("");
  const [talkgroups, setTalkgroups] = useState<TalkgroupOption[]>([]);
  const [talkgroupPickerProfile, setTalkgroupPickerProfile] = useState<string | null>(null);
  const [talkgroupFilter, setTalkgroupFilter] = useState("");
  useEffect(() => setDraft(profileState), [profileState]);
  useEffect(() => {
    if (!talkgroupPickerProfile || talkgroups.length) return;
    void api.request<TalkgroupOption[]>("/api/v1/talkgroups").then(setTalkgroups).catch(() => setTalkgroups([]));
  }, [talkgroupPickerProfile, talkgroups.length]);
  if (!draft) return null;

  function updateProfile(id: string, patch: Partial<ProcessingProfile>) {
    setDraft(current => current && ({ ...current, profiles: current.profiles.map(p => p.id === id ? { ...p, ...patch } : p) }));
  }
  function addProfile() {
    const id = crypto.randomUUID();
    setDraft(current => current && ({ ...current, profiles: [...current.profiles, { id, name: "New Profile", includePolice: true, includeFire: true, includeEMS: true, includeTraffic: true, includeOther: true, allowedTalkgroups: [] }] }));
  }
  function deleteProfile(id: string) {
    setDraft(current => {
      if (!current || current.profiles.length <= 1) return current;
      const profiles = current.profiles.filter(p => p.id !== id);
      return { activeProfileId: current.activeProfileId === id ? profiles[0].id : current.activeProfileId, profiles };
    });
  }
  async function saveProfiles() {
    setMessage("Saving profiles...");
    const saved = await api.request<ProfileState>("/api/v1/profiles", { method: "POST", body: JSON.stringify(draft) });
    setDraft(saved);
    setProfileState(saved);
    setMessage("Profiles saved");
    await reload();
  }
  return <div className="card settings-card wide">
    <div className="settings-card-meta">
      <h3>Profiles</h3>
      <p>Profiles control which categories and optional talkgroups are visible and included in dashboard/category calculations.</p>
      <div className="settings-card-actions"><button onClick={addProfile}>Create</button><button onClick={() => void saveProfiles()}>Save</button></div>
      {message && <span className="section-status ok">{message}</span>}
    </div>
    <div className="settings-fields">
      <label className="setting-field"><span>Active profile<small>Applied to navigation and server-computed dashboard data.</small></span><select value={draft.activeProfileId} onChange={e => setDraft({ ...draft, activeProfileId: e.target.value })}>{draft.profiles.map(p => <option value={p.id} key={p.id}>{p.name}</option>)}</select></label>
      {draft.profiles.map(profile => {
        const selectedCount = profile.allowedTalkgroups?.length ?? 0;
        return <div className="profile-editor" key={profile.id}>
        <input value={profile.name} onChange={e => updateProfile(profile.id, { name: e.target.value })} />
        <div className="profile-checks">
          <label><input type="checkbox" checked={profile.includePolice} onChange={e => updateProfile(profile.id, { includePolice: e.target.checked })} /> Police</label>
          <label><input type="checkbox" checked={profile.includeFire} onChange={e => updateProfile(profile.id, { includeFire: e.target.checked })} /> Fire</label>
          <label><input type="checkbox" checked={profile.includeEMS} onChange={e => updateProfile(profile.id, { includeEMS: e.target.checked })} /> EMS</label>
          <label><input type="checkbox" checked={profile.includeTraffic} onChange={e => updateProfile(profile.id, { includeTraffic: e.target.checked })} /> Traffic</label>
          <label><input type="checkbox" checked={profile.includeOther} onChange={e => updateProfile(profile.id, { includeOther: e.target.checked })} /> Other</label>
        </div>
        <div className="talkgroup-select-row">
          <div><strong>Allowed talkgroups</strong><small>{selectedCount ? `${selectedCount} selected` : "All talkgroups in enabled categories"}</small></div>
          <button type="button" onClick={() => setTalkgroupPickerProfile(profile.id)}>Select talkgroups</button>
          {selectedCount > 0 && <button type="button" onClick={() => updateProfile(profile.id, { allowedTalkgroups: [] })}>Clear</button>}
        </div>
        <button disabled={draft.profiles.length <= 1} onClick={() => deleteProfile(profile.id)}>Delete</button>
      </div>;
      })}
    </div>
    {talkgroupPickerProfile && <TalkgroupPicker
      rows={talkgroups}
      filter={talkgroupFilter}
      setFilter={setTalkgroupFilter}
      selected={draft.profiles.find(p => p.id === talkgroupPickerProfile)?.allowedTalkgroups ?? []}
      onClose={() => setTalkgroupPickerProfile(null)}
      onApply={ids => {
        updateProfile(talkgroupPickerProfile, { allowedTalkgroups: ids });
        setTalkgroupPickerProfile(null);
      }}
    />}
  </div>;
}

function TalkgroupPicker({ rows, selected, filter, setFilter, onApply, onClose }: { rows: TalkgroupOption[]; selected: number[]; filter: string; setFilter: (value: string) => void; onApply: (ids: number[]) => void; onClose: () => void }) {
  const [draft, setDraft] = useState<Set<number>>(new Set(selected));
  useEffect(() => setDraft(new Set(selected)), [selected.join(",")]);
  const needle = filter.trim().toLowerCase();
  const filtered = rows.filter(row => !needle || row.label.toLowerCase().includes(needle) || row.category.toLowerCase().includes(needle) || String(row.talkgroup).includes(needle)).slice(0, 500);
  function toggle(id: number, checked: boolean) {
    setDraft(current => {
      const next = new Set(current);
      if (checked) next.add(id); else next.delete(id);
      return next;
    });
  }
  return <div className="modal-backdrop">
    <div className="talkgroup-picker card">
      <div className="talkgroup-picker-head">
        <div><h3>Select Talkgroups</h3><p className="muted">Leave the selection empty to include all talkgroups in enabled categories.</p></div>
        <button onClick={onClose}>Close</button>
      </div>
      <input placeholder="Filter by name, category, or ID" value={filter} onChange={e => setFilter(e.target.value)} />
      <div className="talkgroup-picker-table">
        <table className="table compact-table"><thead><tr><th></th><th>ID</th><th>Name</th><th>Category</th></tr></thead><tbody>{filtered.map(row => <tr key={row.talkgroup}>
          <td><input type="checkbox" checked={draft.has(row.talkgroup)} onChange={e => toggle(row.talkgroup, e.target.checked)} /></td>
          <td>{row.talkgroup}</td>
          <td>{row.label}</td>
          <td><span className={`pill category-${row.category}`}>{label(row.category)}</span></td>
        </tr>)}</tbody></table>
      </div>
      <div className="settings-card-actions"><span className="muted">{draft.size} selected</span><button onClick={() => setDraft(new Set())}>Clear selection</button><button onClick={() => onApply(Array.from(draft).sort((a, b) => a - b))}>Apply</button></div>
    </div>
  </div>;
}

function SettingInput({ label: text, description, value, onChange, type = "text" }: { label: string; description: string; value: any; onChange: (value: string) => void; type?: string }) {
  return <label className="setting-field">
    <span>{text}<small>{description}</small></span>
    <input type={type} value={value ?? ""} onChange={e => onChange(e.target.value)} />
  </label>;
}

function SettingSelect({ label: text, description, value, options, onChange }: { label: string; description: string; value: any; options: string[]; onChange: (value: string) => void }) {
  const selected = options.includes(value) ? value : options[0];
  return <label className="setting-field">
    <span>{text}<small>{description}</small></span>
    <select value={selected} onChange={e => onChange(e.target.value)}>
      {options.map(option => <option value={option} key={option}>{label(option)}</option>)}
    </select>
  </label>;
}

function SettingCheckbox({ label: text, description, checked, onChange }: { label: string; description: string; checked: any; onChange: (value: boolean) => void }) {
  return <label className="setting-checkbox">
    <input type="checkbox" checked={Boolean(checked)} onChange={e => onChange(e.target.checked)} />
    <span>{text}<small>{description}</small></span>
  </label>;
}

function SettingValue({ label: text, value }: { label: string; value: any }) {
  return <div className="setting-value"><span>{text}</span><code>{value || "--"}</code></div>;
}

function ModelManager({ engine, rows, busy, selectedPath, onUse, onDownload, onDelete }: { engine: string; rows: any[]; busy: string; selectedPath: string; onUse: (path: string) => void; onDownload: (model: string) => Promise<void>; onDelete: (model: string) => Promise<void> }) {
  rows = rows.filter(row => row.engine === engine);
  if (!rows.length) return null;
  const anyBusy = Boolean(busy);
  return <div className="model-manager">
    {rows.map(row => {
      const status = row.installed
        ? `${formatBytes(row.bytes)} installed${row.path === selectedPath ? " - selected" : ""}`
        : row.activeDownload
          ? `Downloading: ${formatBytes(row.bytes)}`
          : row.downloading
            ? `Partial download: ${formatBytes(row.bytes)}`
            : "Not installed";
      return <div className="model-row" key={row.id}>
      <span><strong>{row.label}</strong><small>{status}</small></span>
      <div>
        {row.installed && <button disabled={anyBusy} onClick={() => onUse(row.path)}>Use</button>}
        {row.installed ? <button disabled={anyBusy} onClick={() => void onDelete(row.id)}>{busy === row.id ? "Removing..." : "Remove"}</button> : <button disabled={anyBusy || row.activeDownload} onClick={() => void onDownload(row.id)}>{busy === row.id || row.activeDownload ? "Downloading..." : row.downloading ? "Retry" : "Download"}</button>}
      </div>
    </div>;
    })}
  </div>;
}

function cloneSettings(value: Record<string, any>) {
  return JSON.parse(JSON.stringify(value ?? {}));
}

function withSettingsDefaults(value: Record<string, any>) {
  const sections = cloneSettings(value);
  sections.engine = {
    server: { httpBind: "0.0.0.0", httpPort: 8080, ...(sections.engine?.server ?? {}) },
    ingest: { callstreamBind: "127.0.0.1", callstreamPort: 9123, maxConcurrentClients: 4, ...(sections.engine?.ingest ?? {}) },
    storage: {
      databasePath: "/var/lib/pizzawave/pizzad.db",
      audioRoot: "/var/lib/pizzawave/audio",
      importCacheRoot: "/var/lib/pizzawave/import-cache",
      appDataRoot: "/var/lib/pizzawave/appdata",
      ...(sections.engine?.storage ?? {})
    }
  };
  sections.transcription = {
    provider: "none",
    whisperModelFile: "",
    voskModelPath: "",
    fasterWhisperPythonPath: "/opt/pizzawave/venv/faster-whisper/bin/python",
    fasterWhisperScriptPath: "",
    fasterWhisperModel: "tiny",
    fasterWhisperDevice: "cpu",
    fasterWhisperComputeType: "int8",
    fasterWhisperWorkers: 1,
    fasterWhisperVadFilter: false,
    openAiBaseUrl: "http://localhost:1234/v1",
    openAiApiKey: "",
    openAiModel: "",
    analogSampleRate: 8000,
    whisperThreads: 2,
    liveTranscriptionWorkers: 1,
    livePressureQueueDepth: 200,
    ...(sections.transcription ?? {})
  };
  sections.transcription.provider = sections.transcription.provider || "none";
  sections.transcription.whisperModelFile = sections.transcription.whisperModelFile ?? "";
  sections.transcription.voskModelPath = sections.transcription.voskModelPath ?? "";
  sections.transcription.fasterWhisperPythonPath = sections.transcription.fasterWhisperPythonPath || "/opt/pizzawave/venv/faster-whisper/bin/python";
  sections.transcription.fasterWhisperScriptPath = sections.transcription.fasterWhisperScriptPath ?? "";
  sections.transcription.fasterWhisperModel = sections.transcription.fasterWhisperModel || "tiny";
  sections.transcription.fasterWhisperDevice = sections.transcription.fasterWhisperDevice || "cpu";
  sections.transcription.fasterWhisperComputeType = sections.transcription.fasterWhisperComputeType || "int8";
  sections.transcription.fasterWhisperWorkers = sections.transcription.fasterWhisperWorkers || 1;
  sections.transcription.fasterWhisperVadFilter = !!sections.transcription.fasterWhisperVadFilter;
  sections.transcription.openAiBaseUrl = sections.transcription.openAiBaseUrl || "http://localhost:1234/v1";
  sections.transcription.openAiApiKey = sections.transcription.openAiApiKey ?? "";
  sections.transcription.openAiModel = sections.transcription.openAiModel ?? "";
  sections.transcription.analogSampleRate = sections.transcription.analogSampleRate || 8000;
  sections.transcription.whisperThreads = sections.transcription.whisperThreads || 2;
  sections.transcription.liveTranscriptionWorkers = sections.transcription.liveTranscriptionWorkers || 1;
  sections.transcription.livePressureQueueDepth = sections.transcription.livePressureQueueDepth || 200;
  sections["ai-insights"] = {
    enabled: false,
    openAiBaseUrl: "",
    openAiApiKey: "",
    openAiModel: "",
    batchSize: 50,
    maxPendingCalls: 1000,
    timeoutMs: 600000,
    maxRetries: 2,
    ...(sections["ai-insights"] ?? {})
  };
  sections.sftp = {
    enabled: false,
    host: "",
    port: 22,
    username: "",
    authMode: "privateKey",
    password: "",
    privateKeyPath: "",
    privateKeyPassphrase: "",
    remoteRoot: "",
    quickImportMaxHours: 48,
    defaultBatchCallCap: 5000,
    defaultBatchByteCap: 21474836480,
    ...(sections.sftp ?? {})
  };
  sections.tr = {
    configPath: "/etc/trunk-recorder/config.json",
    talkgroupCatalogPath: "/var/lib/pizzawave/appdata/talkgroups.json",
    talkgroupsPath: "/etc/trunk-recorder/talkgroups.csv",
    logServiceName: "trunk-recorder",
    healthWindowMinutes: 5,
    ...(sections.tr ?? {})
  };
  sections.alerts = {
    emailEnabled: false,
    emailProvider: "gmail",
    emailUser: "",
    emailPassword: "",
    rules: [],
    ...(sections.alerts ?? {})
  };
  return sections;
}

function settingsSectionsFromFile(input: any) {
  if (!input || typeof input !== "object")
    throw new Error("Settings file must contain a JSON object.");

  return withSettingsDefaults({
    engine: input.engine ?? {
      server: input.server ?? {},
      storage: input.storage ?? {},
      ingest: input.ingest ?? {}
    },
    transcription: input.transcription ?? {},
    "ai-insights": input["ai-insights"] ?? input.aiInsights ?? {},
    sftp: input.sftp ?? input.sftpImport ?? {},
    tr: input.tr ?? input.trunkRecorder ?? {},
    alerts: input.alerts ?? {}
  });
}

function settingsFileFromSections(sections: Record<string, any>) {
  const normalized = withSettingsDefaults(sections);
  return {
    server: normalized.engine.server,
    storage: normalized.engine.storage,
    ingest: normalized.engine.ingest,
    transcription: normalized.transcription,
    aiInsights: normalized["ai-insights"],
    sftpImport: normalized.sftp,
    trunkRecorder: normalized.tr,
    alerts: normalized.alerts
  };
}

function setupDraftFromStatus(status: SetupStatus) {
  const values = cloneSettings(status.values ?? {});
  values.branding = { stackName: "PizzaWave", ...(values.branding ?? {}) };
  values.trunkRecorder = {
    configPath: "/etc/trunk-recorder/config.json",
    talkgroupCatalogPath: "/var/lib/pizzawave/appdata/talkgroups.json",
    talkgroupsPath: "/etc/trunk-recorder/talkgroups.csv",
    logServiceName: "trunk-recorder",
    healthWindowMinutes: 5,
    ...(values.trunkRecorder ?? {})
  };
  values.transcription = {
    provider: "none",
    whisperModelFile: "",
    voskModelPath: "",
    fasterWhisperPythonPath: "/opt/pizzawave/venv/faster-whisper/bin/python",
    fasterWhisperScriptPath: "",
    fasterWhisperModel: "tiny",
    fasterWhisperDevice: "cpu",
    fasterWhisperComputeType: "int8",
    fasterWhisperWorkers: 1,
    fasterWhisperVadFilter: false,
    openAiBaseUrl: "http://localhost:1234/v1",
    openAiApiKey: "",
    openAiModel: "",
    analogSampleRate: 8000,
    ...(values.transcription ?? {})
  };
  values.aiInsights = {
    enabled: false,
    openAiBaseUrl: "",
    openAiApiKey: "",
    openAiModel: "",
    batchSize: 20,
    maxPendingCalls: 1000,
    timeoutMs: 600000,
    maxRetries: 2,
    ...(values.aiInsights ?? {})
  };
  values.alerts = {
    emailEnabled: false,
    emailProvider: "gmail",
    emailUser: "",
    emailPassword: "",
    rules: [],
    ...(values.alerts ?? {})
  };
  values.sftpImport = {
    enabled: false,
    host: "",
    port: 22,
    username: "",
    authMode: "privateKey",
    password: "",
    privateKeyPath: "",
    privateKeyPassphrase: "",
    remoteRoot: "",
    quickImportMaxHours: 48,
    defaultBatchCallCap: 5000,
    defaultBatchByteCap: 21474836480,
    ...(values.sftpImport ?? {})
  };
  values.locations = { monitoredAreas: [], ...(values.locations ?? {}) };
  if (!Array.isArray(values.locations.monitoredAreas) || values.locations.monitoredAreas.length === 0) {
    values.locations.monitoredAreas = [
      { areaId: "hamilton-county-tn", areaLabel: "Hamilton County, TN", systemShortName: "whiteoak-hamilton", aliases: ["whiteoak-hamilton", "whiteoakmt-hamilton", "hamilton"], north: 35.47, south: 34.98, west: -85.47, east: -84.98 },
      { areaId: "bradley-county-tn", areaLabel: "Bradley County, TN", systemShortName: "bradley", aliases: ["bradley", "whiteoakmt-nbradley", "nbradley"], north: 35.33, south: 34.9, west: -85.1, east: -84.55 },
      { areaId: "cleveland-tn", areaLabel: "Cleveland, TN", systemShortName: "cleveland", aliases: ["cleveland", "whiteoakmt-cleveland"], north: 35.24, south: 35.07, west: -84.96, east: -84.78 }
    ];
  }
  return values;
}

function numberOrZero(value: string) {
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : 0;
}

function modelOptions(rows: any[], engine: string) {
  const filtered = rows.filter(row => row.engine === engine);
  return filtered.length ? filtered.map(row => row.id) : ["none"];
}

function modelPath(rows: any[], id: string) {
  return rows.find(row => row.id === id)?.path ?? "";
}

function modelIdForPath(rows: any[], engine: string, path: string) {
  return rows.find(row => row.engine === engine && row.path === path)?.id ?? rows.find(row => row.engine === engine)?.id ?? "none";
}

function buildCalibrationPlan(calibration: {
  system: string;
  controlChannelHz: string;
  deviceSerial: string;
  centerHz: string;
  ppm: string;
  baseErrorHz: string;
  gain: string;
  modulation: string;
  rangeHz: string;
  stepHz: string;
  warmupSec: string;
  durationSec: string;
}) {
  const rangeHz = Math.max(0, numberOrDefault(calibration.rangeHz, 1200));
  const stepHz = Math.max(1, numberOrDefault(calibration.stepHz, 300));
  const warmupSec = Math.max(0, numberOrDefault(calibration.warmupSec, 20));
  const durationSec = Math.max(1, numberOrDefault(calibration.durationSec, 240));
  const explicitBase = numericField(calibration.baseErrorHz);
  const centerHz = numericField(calibration.centerHz);
  const ppm = numericField(calibration.ppm);
  let baseError: number | null = null;
  let baseSource = "Enter base error directly, or provide GQRX center Hz and PPM.";

  if (explicitBase !== null) {
    baseError = Math.round(explicitBase);
    baseSource = "Manual base error from GQRX/TR defaults.";
  } else if (centerHz !== null && ppm !== null) {
    baseError = Math.abs(Math.round(centerHz * ppm / 1_000_000));
    baseSource = `Calculated from ${centerHz.toLocaleString()} Hz x ${ppm} PPM.`;
  }

  const candidates = baseError === null ? [] : buildErrorCandidates(baseError, rangeHz, stepHz);
  const passCount = candidates.length;
  const estimatedSeconds = passCount * (warmupSec + durationSec);
  const candidateSummary = candidates.length
    ? `${candidates[0]} to ${candidates[candidates.length - 1]} Hz, ${stepHz} Hz steps`
    : "Not available yet";
  const script = "/opt/pizzawave/scripts/tr_tune.sh";
  const system = calibration.system.trim();
  const control = calibration.controlChannelHz.trim();
  const serial = calibration.deviceSerial.trim();
  const gain = calibration.gain.trim();
  const modulation = calibration.modulation || "qpsk";
  const ppmCommand = centerHz !== null && ppm !== null
    ? `sudo ${script} ppm-convert --center-hz ${Math.round(centerHz)} --ppm ${ppm} --tr-sign positive`
    : "";
  const errorSweepCommand = baseError !== null && system && control && serial
    ? [
      `sudo ${script} error-sweep`,
      `--system ${system}`,
      `--control-channel ${control}`,
      `--device-serial ${serial}`,
      `--base-error ${baseError}`,
      `--range-hz ${rangeHz}`,
      `--step-hz ${stepHz}`,
      `--modulation ${modulation}`,
      gain ? `--gain ${gain}` : "",
      `--warmup-sec ${warmupSec}`,
      `--duration-sec ${durationSec}`
    ].filter(Boolean).join(" ")
    : "";

  return {
    baseError,
    baseSource,
    candidates,
    candidateSummary,
    passCount,
    estimatedSeconds,
    warmupSec,
    durationSec,
    ppmCommand,
    errorSweepCommand
  };
}

function numericField(value: string) {
  if (!value.trim()) return null;
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : null;
}

function numberOrDefault(value: string, fallback: number) {
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : fallback;
}

function buildErrorCandidates(baseError: number, rangeHz: number, stepHz: number) {
  const start = baseError - rangeHz;
  const end = baseError + rangeHz;
  const candidates: number[] = [];
  for (let value = start; value <= end; value += stepHz)
    candidates.push(Math.round(value));
  if (candidates[candidates.length - 1] !== Math.round(end))
    candidates.push(Math.round(end));
  return candidates;
}

function formatElapsed(seconds: number) {
  if (!Number.isFinite(seconds) || seconds <= 0) return "0s";
  const minutes = Math.floor(seconds / 60);
  const remainingSeconds = Math.round(seconds % 60);
  if (minutes < 60)
    return remainingSeconds ? `${minutes}m ${remainingSeconds}s` : `${minutes}m`;
  const hours = Math.floor(minutes / 60);
  const remainingMinutes = minutes % 60;
  return remainingMinutes ? `${hours}h ${remainingMinutes}m` : `${hours}h`;
}

function buildGuidedSweepCommand(
  systemShortName: string,
  modulation: string,
  source: { index: number; serial: string; centerFrequency: number; sampleRate: number; errorHz: number; gain: string },
  input: { gain: string; errorHz: string; ppm: string },
  controlFrequencyHz: number,
  sweep: { rangeHz: string; stepHz: string; warmupSec: string; durationSec: string }
) {
  const error = numericField(input.errorHz);
  const ppm = numericField(input.ppm);
  const baseError = error !== null
    ? Math.round(error)
    : ppm !== null
      ? Math.abs(Math.round(source.centerFrequency * ppm / 1_000_000))
      : source.errorHz || 0;
  const gain = input.gain.trim() || source.gain;
  const serial = source.serial || String(source.index);
  return [
    "sudo /usr/lib/pizzawave/scripts/tr_tune.sh error-sweep",
    `--system ${systemShortName}`,
    `--control-channel ${controlFrequencyHz}`,
    `--device-serial ${serial}`,
    `--base-error ${baseError}`,
    `--range-hz ${Math.max(0, numberOrDefault(sweep.rangeHz, 1200))}`,
    `--step-hz ${Math.max(1, numberOrDefault(sweep.stepHz, 300))}`,
    `--modulation ${modulation || "qpsk"}`,
    gain ? `--gain ${gain}` : "",
    `--warmup-sec ${Math.max(0, numberOrDefault(sweep.warmupSec, 20))}`,
    `--duration-sec ${Math.max(1, numberOrDefault(sweep.durationSec, 240))}`
  ].filter(Boolean).join(" ");
}

function buildGuidedSweepParameters(
  systemShortName: string,
  modulation: string,
  source: { index: number; serial: string; centerFrequency: number; sampleRate: number; errorHz: number; gain: string },
  input: { gain: string; errorHz: string; ppm: string },
  controlFrequencyHz: number,
  sweep: { rangeHz: string; stepHz: string; warmupSec: string; durationSec: string }
) {
  const error = numericField(input.errorHz);
  const ppm = numericField(input.ppm);
  const baseError = error !== null
    ? Math.round(error)
    : ppm !== null
      ? Math.abs(Math.round(source.centerFrequency * ppm / 1_000_000))
      : source.errorHz || 0;
  return {
    systemShortName,
    modulation: modulation || "qpsk",
    sourceIndex: source.index,
    serial: source.serial || String(source.index),
    controlChannelHz: controlFrequencyHz,
    baseErrorHz: baseError,
    rangeHz: Math.max(0, numberOrDefault(sweep.rangeHz, 1200)),
    stepHz: Math.max(1, numberOrDefault(sweep.stepHz, 300)),
    warmupSec: Math.max(0, numberOrDefault(sweep.warmupSec, 20)),
    durationSec: Math.max(1, numberOrDefault(sweep.durationSec, 240)),
    gain: input.gain.trim() || source.gain
  };
}

function formatFrequencyList(values: number[]) {
  if (!values.length) return "None";
  const shown = values.slice(0, 4).map(formatHz).join(", ");
  return values.length > 4 ? `${shown}, +${values.length - 4} more` : shown;
}

function formatFrequencyRange(lowHz: number, highHz: number) {
  return lowHz === highHz ? formatHz(lowHz) : `${formatHz(lowHz)} - ${formatHz(highHz)}`;
}

function formatBytes(bytes: number) {
  if (!bytes) return "0 B";
  const units = ["B", "KB", "MB", "GB"];
  let value = bytes;
  let index = 0;
  while (value >= 1024 && index < units.length - 1) {
    value /= 1024;
    index++;
  }
  return `${value.toFixed(value >= 10 || index === 0 ? 0 : 1)} ${units[index]}`;
}

function SftpImport({ reload, blockedReason }: { reload: () => Promise<void>; blockedReason?: string }) {
  const [start, setStart] = useState("");
  const [end, setEnd] = useState("");
  const [message, setMessage] = useState("");
  const [busy, setBusy] = useState("");
  const [availability, setAvailability] = useState<any>(null);
  const [availabilityBusy, setAvailabilityBusy] = useState(false);

  useEffect(() => { void loadAvailability(); }, []);

  async function loadAvailability() {
    setAvailabilityBusy(true);
    try {
      setAvailability(await api.request<any>("/api/v1/imports/sftp/availability"));
    } catch (error) {
      setAvailability({ available: false, message: `Availability check failed: ${error instanceof Error ? error.message : String(error)}` });
    } finally {
      setAvailabilityBusy(false);
    }
  }

  function fillAvailableRange(hours?: number) {
    if (!availability?.earliestLocal || !availability?.latestLocal) return;
    const latest = new Date(availability.latestLocal);
    const earliest = hours ? new Date(Math.max(new Date(availability.earliestLocal).getTime(), latest.getTime() - hours * 3600_000)) : new Date(availability.earliestLocal);
    setStart(toDateTimeInput(earliest));
    setEnd(toDateTimeInput(latest));
  }

  function requestBody() {
    if (!start || !end) throw new Error("Choose both start and end dates.");
    const startDate = new Date(start);
    const endDate = new Date(end);
    if (Number.isNaN(startDate.getTime()) || Number.isNaN(endDate.getTime())) throw new Error("Choose a valid date range.");
    if (endDate <= startDate) throw new Error("End must be after start.");
    return { startLocal: startDate.toISOString(), endLocal: endDate.toISOString() };
  }

  async function estimate() {
    setBusy("estimate");
    setMessage("Estimating SFTP import...");
    try {
      const r = await api.request<any>("/api/v1/imports/sftp/estimate", { method: "POST", body: JSON.stringify(requestBody()) });
      setMessage(r.message ?? `Found ${r.candidateCount ?? 0} candidate file(s).`);
    } catch (error) {
      setMessage(`Estimate failed: ${error instanceof Error ? error.message : String(error)}`);
    } finally {
      setBusy("");
    }
  }

  async function run(confirmLargeImport: boolean) {
    setBusy(confirmLargeImport ? "prime" : "quick");
    setMessage(confirmLargeImport ? "Queueing prime import..." : "Queueing quick import...");
    try {
      const r = await api.request<Job>("/api/v1/imports/sftp/import", { method: "POST", body: JSON.stringify({ ...requestBody(), confirmLargeImport }) });
      setMessage(`Queued job ${r.id}`);
      await reload();
    } catch (error) {
      setMessage(`Import failed: ${error instanceof Error ? error.message : String(error)}`);
    } finally {
      setBusy("");
    }
  }

  return <>
    <p className="muted">Quick imports are capped at 48h. Larger imports prime PizzaWave as throttled background jobs.</p>
    <div className="archive-availability">
      <div>
        <strong>Visible archive</strong>
        <p className={availability?.available ? "settings-message ok" : "settings-message error"}>{availabilityBusy ? "Checking SFTP archive..." : availability?.message ?? "Archive availability has not been checked."}</p>
        {availability?.available && <p className="muted">{availability.scannedDirectories?.toLocaleString()} folder(s) scanned; {availability.skippedDirectories?.toLocaleString()} unreadable folder(s) skipped.</p>}
      </div>
      <div className="archive-actions">
        <button disabled={availabilityBusy} onClick={() => void loadAvailability()}>{availabilityBusy ? "Checking..." : "Refresh"}</button>
        <button disabled={!availability?.available} onClick={() => fillAvailableRange(48)}>Use latest 48h</button>
        <button disabled={!availability?.available} onClick={() => fillAvailableRange()}>Use full range</button>
      </div>
    </div>
    <input type="datetime-local" value={start} onChange={e => setStart(e.target.value)} />
    <input type="datetime-local" value={end} onChange={e => setEnd(e.target.value)} />
    {blockedReason && <div className="settings-message error">{blockedReason}</div>}
    <button disabled={!!busy || Boolean(blockedReason)} onClick={estimate}>{busy === "estimate" ? "Estimating..." : "Estimate"}</button>
    <button disabled={!!busy || Boolean(blockedReason)} onClick={() => void run(false)}>{busy === "quick" ? "Queueing..." : "Quick Import"}</button>
    <button disabled={!!busy || Boolean(blockedReason)} onClick={() => void run(true)}>{busy === "prime" ? "Queueing..." : "Prime PizzaWave"}</button>
    <div className={message.startsWith("Estimate failed") || message.startsWith("Import failed") ? "settings-message error" : "settings-message ok"}>{message}</div>
  </>;
}

function LocalImport({ reload, blockedReason }: { reload: () => Promise<void>; blockedReason?: string }) {
  const [start, setStart] = useState("");
  const [end, setEnd] = useState("");
  const [message, setMessage] = useState("");
  const [busy, setBusy] = useState("");
  const [availability, setAvailability] = useState<any>(null);
  const [availabilityBusy, setAvailabilityBusy] = useState(false);

  useEffect(() => { void loadAvailability(); }, []);

  async function loadAvailability() {
    setAvailabilityBusy(true);
    try {
      setAvailability(await api.request<any>("/api/v1/imports/local/availability"));
    } catch (error) {
      setAvailability({ available: false, message: `Availability check failed: ${error instanceof Error ? error.message : String(error)}` });
    } finally {
      setAvailabilityBusy(false);
    }
  }

  function fillAvailableRange(hours?: number) {
    if (!availability?.earliestLocal || !availability?.latestLocal) return;
    const latest = new Date(availability.latestLocal);
    const earliest = hours ? new Date(Math.max(new Date(availability.earliestLocal).getTime(), latest.getTime() - hours * 3600_000)) : new Date(availability.earliestLocal);
    setStart(toDateTimeInput(earliest));
    setEnd(toDateTimeInput(latest));
  }

  function requestBody() {
    if (!start || !end) throw new Error("Choose both start and end dates.");
    const startDate = new Date(start);
    const endDate = new Date(end);
    if (Number.isNaN(startDate.getTime()) || Number.isNaN(endDate.getTime())) throw new Error("Choose a valid date range.");
    if (endDate <= startDate) throw new Error("End must be after start.");
    return { startLocal: startDate.toISOString(), endLocal: endDate.toISOString() };
  }

  async function estimate() {
    setBusy("estimate");
    setMessage("Estimating local import...");
    try {
      const r = await api.request<any>("/api/v1/imports/local/estimate", { method: "POST", body: JSON.stringify(requestBody()) });
      setMessage(r.message ?? `Found ${r.candidateCount ?? 0} candidate file(s).`);
    } catch (error) {
      setMessage(`Estimate failed: ${error instanceof Error ? error.message : String(error)}`);
    } finally {
      setBusy("");
    }
  }

  async function run(confirmLargeImport: boolean) {
    setBusy(confirmLargeImport ? "prime" : "quick");
    setMessage(confirmLargeImport ? "Queueing local prime import..." : "Queueing local quick import...");
    try {
      const r = await api.request<Job>("/api/v1/imports/local/import", { method: "POST", body: JSON.stringify({ ...requestBody(), confirmLargeImport }) });
      setMessage(`Queued job ${r.id}`);
      await reload();
    } catch (error) {
      setMessage(`Import failed: ${error instanceof Error ? error.message : String(error)}`);
    } finally {
      setBusy("");
    }
  }

  return <>
    <p className="muted">Use this when PizzaWave is installed on the same machine as an existing trunk-recorder setup. Quick imports are capped at 48h; larger imports require explicit prime confirmation.</p>
    <div className="archive-availability">
      <div>
        <strong>Local captureDir</strong>
        <p className={availability?.available ? "settings-message ok" : "settings-message error"}>{availabilityBusy ? "Checking local TR recordings..." : availability?.message ?? "Local archive availability has not been checked."}</p>
        {availability?.captureDir && <p className="muted"><code>{availability.captureDir}</code></p>}
        {availability?.available && <p className="muted">{availability.fileCount?.toLocaleString()} file(s), {formatBytes(availability.totalBytes ?? 0)}; {availability.scannedDirectories?.toLocaleString()} folder(s) scanned; {availability.skippedDirectories?.toLocaleString()} unreadable folder(s) skipped.</p>}
      </div>
      <div className="archive-actions">
        <button disabled={availabilityBusy} onClick={() => void loadAvailability()}>{availabilityBusy ? "Checking..." : "Refresh"}</button>
        <button disabled={!availability?.available} onClick={() => fillAvailableRange(48)}>Use latest 48h</button>
        <button disabled={!availability?.available} onClick={() => fillAvailableRange()}>Use full range</button>
      </div>
    </div>
    <input type="datetime-local" value={start} onChange={e => setStart(e.target.value)} />
    <input type="datetime-local" value={end} onChange={e => setEnd(e.target.value)} />
    {blockedReason && <div className="settings-message error">{blockedReason}</div>}
    <button disabled={!!busy || !availability?.available || Boolean(blockedReason)} onClick={estimate}>{busy === "estimate" ? "Estimating..." : "Estimate"}</button>
    <button disabled={!!busy || !availability?.available || Boolean(blockedReason)} onClick={() => void run(false)}>{busy === "quick" ? "Queueing..." : "Quick Import"}</button>
    <button disabled={!!busy || !availability?.available || Boolean(blockedReason)} onClick={() => void run(true)}>{busy === "prime" ? "Queueing..." : "Prime PizzaWave"}</button>
    <div className={message.startsWith("Estimate failed") || message.startsWith("Import failed") ? "settings-message error" : "settings-message ok"}>{message}</div>
  </>;
}

function toDateTimeInput(date: Date) {
  const local = new Date(date.getTime() - date.getTimezoneOffset() * 60000);
  return local.toISOString().slice(0, 16);
}

function parseTalkgroups(value: string) {
  return value.split(/[,\s]+/).map(v => Number(v.trim())).filter(v => Number.isFinite(v) && v > 0);
}

function formatCompact(value: number) {
  if (value >= 1_000_000) return `${(value / 1_000_000).toFixed(1).replace(/\.0$/, "")}m`;
  if (value >= 1_000) return `${(value / 1_000).toFixed(1).replace(/\.0$/, "")}k`;
  return value.toLocaleString();
}

function profileIncludes(profile: ProcessingProfile | undefined, item: string) {
  if (!profile) return true;
  if (item === "police") return profile.includePolice;
  if (item === "fire") return profile.includeFire;
  if (item === "ems") return profile.includeEMS;
  if (item === "traffic") return profile.includeTraffic;
  if (item === "other") return profile.includeOther;
  return true;
}

function navIcon(item: Page) {
  if (item === "dashboard") return <Gauge size={15} />;
  if (item === "settings") return <Settings size={15} />;
  if (item === "system") return <Activity size={15} />;
  return <Radio size={15} />;
}

function label(value: string) {
  if (value === "ems") return "EMS";
  if (value === "system") return "System";
  return value[0].toUpperCase() + value.slice(1).replaceAll("_", " ");
}

createRoot(document.getElementById("root")!).render(<App />);
