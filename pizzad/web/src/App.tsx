import React, { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { createRoot } from "react-dom/client";
import { Activity, Bell, Gauge, Radio, Settings, ShieldAlert } from "lucide-react";
import { api, rangeBody, rangeQuery } from "./api";
import type { AlertMatch, BarStat, CategoryInsight, CategoryPage, Dashboard, DiagnosticModel, DiagnosticToolResult, EngineCall, EngineHealth, HourCategory, Incident, Job, QualityAuditGroup, QualityAuditSample, QualityHour, TopTalkgroup, TrHealthChart, TrHealthMetric, TrTroubleshoot } from "./types";
import "./style.css";

const categories = ["police", "fire", "ems", "traffic", "other"] as const;
type Page = "dashboard" | "troubleshoot" | "settings" | typeof categories[number];
type CategoryViewMode = "incidents" | "summaries" | "raw";
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
    return saved === "summaries" || saved === "raw" ? saved : "incidents";
  });
  const [status, setStatus] = useState("Starting");
  const [dashboard, setDashboard] = useState<Dashboard | null>(null);
  const [category, setCategory] = useState<CategoryPage | null>(null);
  const [jobs, setJobs] = useState<Job[]>([]);
  const [engineHealth, setEngineHealth] = useState<EngineHealth | null>(null);
  const [troubleshoot, setTroubleshoot] = useState<TrTroubleshoot | null>(null);
  const [settingsSections, setSettingsSections] = useState<Record<string, any>>({});
  const [settingsLoadState, setSettingsLoadState] = useState<{ loading: boolean; version: number; message: string; error: boolean }>({ loading: false, version: 0, message: "", error: false });
  const settingsFileInputRef = useRef<HTMLInputElement | null>(null);

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

  const load = useCallback(async () => {
    try {
      const [healthStatus, jobRows] = await Promise.all([
        api.request<EngineHealth>("/api/v1/health"),
        api.request<Job[]>("/api/v1/jobs")
      ]);
      setEngineHealth(healthStatus);
      setJobs(jobRows);
      if (page === "dashboard") {
        setDashboard(await api.request<Dashboard>(`/api/v1/dashboard?${rangeQuery(rangeHours)}`));
      } else if (categories.includes(page as any)) {
        setCategory(await api.request<CategoryPage>(`/api/v1/categories/${page}?${rangeQuery(rangeHours)}`));
      } else if (page === "troubleshoot") {
        setTroubleshoot(await api.request<TrTroubleshoot>(`/api/v1/troubleshoot?${rangeQuery(rangeHours)}&bySystem=false&baseline=7d`));
      }
      setStatus("Live");
    } catch (error) {
      setStatus(error instanceof Error ? error.message : "Error");
    }
  }, [page, rangeHours]);

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
    for (const type of ["call_ingested", "call_transcribed", "alert_matched", "job_updated", "summary_updated", "health_updated"]) {
      events.addEventListener(type, () => void load());
    }
    events.addEventListener("connected", () => setStatus("Live"));
    events.onerror = () => setStatus("Reconnecting");
    return () => events.close();
  }, [load]);

  const nav = useMemo(() => ["dashboard", ...categories, "troubleshoot", "settings"] as Page[], []);

  return (
    <div className="app">
      <header className="topbar">
        <div className="brand"><Radio size={18} /> PizzaWave Engine</div>
        <select value={rangeHours} onChange={e => setRangeHours(Number(e.target.value))}>
          <option value={24}>24h</option>
          <option value={48}>2d</option>
          <option value={168}>Week</option>
        </select>
        <select aria-label="Color scheme" value={theme} onChange={e => setTheme(e.target.value)}>
          <option value="blue">Blue</option>
          <option value="orange">Orange</option>
        </select>
        {page === "settings" && <>
          <input ref={settingsFileInputRef} type="file" accept="application/json,.json" hidden onChange={e => void loadSettingsFile(e.target.files?.[0])} />
          <button disabled={settingsLoadState.loading} onClick={() => settingsFileInputRef.current?.click()}>{settingsLoadState.loading ? "Loading..." : "Load Settings"}</button>
          <button disabled={settingsLoadState.loading} onClick={() => void exportSettingsFile()}>Export Settings</button>
        </>}
        {categories.includes(page as any) && <div className="segmented" aria-label="Category view mode">
          <button className={categoryViewMode === "incidents" ? "active" : ""} onClick={() => setCategoryViewMode("incidents")}>Incidents</button>
          <button className={categoryViewMode === "summaries" ? "active" : ""} onClick={() => setCategoryViewMode("summaries")}>AI Summaries</button>
          <button className={categoryViewMode === "raw" ? "active" : ""} onClick={() => setCategoryViewMode("raw")}>Raw Calls</button>
        </div>}
        <span className="pill">{status}</span>
        <span className="pill">REST + SSE</span>
      </header>
      <aside className="nav">
        {nav.map(item => (
          <button className={item === page ? "active" : ""} onClick={() => setPage(item)} key={item}>
            {navIcon(item)} {label(item)}
          </button>
        ))}
      </aside>
      <main className="main">
        {page === "dashboard" && <DashboardView data={dashboard} rangeHours={rangeHours} reload={load} />}
        {categories.includes(page as any) && <CategoryView data={category} mode={categoryViewMode} rangeHours={rangeHours} reload={load} />}
        {page === "troubleshoot" && <TroubleshootView data={troubleshoot} rangeHours={rangeHours} reload={load} />}
        {page === "settings" && <SettingsView jobs={jobs} settingsSections={settingsSections} settingsLoadState={settingsLoadState} reload={load} />}
      </main>
      <footer className="statusbar">
        <span className="pill">Queue {engineHealth?.queueDepth ?? "--"}</span>
        <span className="pill">Jobs {jobs.filter(j => j.status === "running" || j.status === "queued" || j.status === "paused").length}</span>
        <span className="muted">{dashboard ? `${dashboard.incidents.length} incidents in selected range` : "Incidents are generated automatically from live transcribed call batches."}</span>
      </footer>
    </div>
  );
}

function DashboardView({ data, rangeHours, reload }: { data: Dashboard | null; rangeHours: number; reload: () => Promise<void> }) {
  if (!data) return <div className="pane">Loading dashboard...</div>;
  return (
    <div className="dashboard">
      <section className="pane left-pane">
        <div className="section kpis">{data.kpis.map(k => <Kpi key={k.label} {...k} />)}</div>
        <div className="section"><h3>Volume Patterns</h3><VolumeByHourChart rows={data.volumeByHourCategory} /><TopTalkgroups rows={data.topTalkgroups} /></div>
        <div className="section"><h3>Distribution</h3><Bars title="Category Share" rows={data.categoryShare} /></div>
      </section>
      <section className="pane"><h2><Bell size={16} /> Alerts</h2><Alerts rows={data.alerts} /></section>
      <section className="pane"><h2><ShieldAlert size={16} /> Incident Explorer</h2><Incidents rows={data.incidents} /></section>
    </div>
  );
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
  return <div className="card chart-card"><h4>Calls by Hour and Category</h4><svg className="chart" viewBox="0 0 500 190" preserveAspectRatio="xMinYMin meet" role="img" aria-label="Calls by hour and category"><line className="axis" x1="32" y1="158" x2="482" y2="158" /><line className="axis" x1="32" y1="28" x2="32" y2="158" />{[0, 6, 12, 18, 23].map(hour => <text className="chart-label" x={36 + hour * 19} y="178" key={hour}>{hour}</text>)}<text className="chart-label" x="4" y="34">{max}</text>{byCategory.map(series => <polyline key={series.category} fill="none" stroke={categoryColors[series.category]} strokeWidth="2.5" points={points(series.values)} />)}</svg><Legend items={byCategory.map(c => [label(c.category), categoryColors[c.category]])} /></div>;
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

function Incidents({ rows }: { rows: Incident[] }) {
  const [expanded, setExpanded] = useState(false);
  if (!rows.length) return <div className="card"><p className="muted">No incidents detected.</p></div>;
  return <div className="incident-explorer">
    <div className="incident-toolbar">
      <strong>Active Incidents</strong>
      <button onClick={() => setExpanded(v => !v)}>{expanded ? "Collapse All" : "Expand All"}</button>
    </div>
    {rows.map(i => <details className="incident-card" key={i.id} open={expanded}>
      <summary>
        <span>{i.title}</span>
        <span className="muted">{i.calls.length} calls</span>
      </summary>
      <div className="incident-meta">
        <span>{incidentTimeRange(i)}</span>
        <strong className={`confidence ${confidenceClass(i.confidence)}`}>{Math.round(i.confidence * 100)}%</strong>
      </div>
      <p>{i.detail}</p>
      <div className="incident-details">
        <div className="muted">Related calls</div>
        {i.calls.map(c => <div className="incident-call" key={c.callId}>
          <div className="incident-call-head">
            <span>{new Date(c.rawTimestamp * 1000).toLocaleString()}</span>
            <span>Call {c.callId}</span>
          </div>
          <p>{c.transcript}</p>
          <audio controls preload="metadata" src={c.audioUrl} />
        </div>)}
      </div>
    </details>)}
  </div>;
}

function CategoryView({ data, mode, rangeHours, reload }: { data: CategoryPage | null; mode: CategoryViewMode; rangeHours: number; reload: () => Promise<void> }) {
  if (!data) return <div className="category-page">Loading...</div>;
  async function generate() {
    await api.request("/api/v1/incidents/generate", { method: "POST", body: JSON.stringify({ ...rangeBody(rangeHours), confirmLargeRange: rangeHours > 168 }) });
    await reload();
  }
  return <div className="category-split-page" data-category={data.category}>
    <section className="pane insights-pane category-pane">
      {mode === "summaries" && <><h2>{label(data.category)} AI Summaries</h2><CategoryInsights rows={data.insights} category={data.category} onGenerate={generate} /></>}
      {mode === "raw" && <><h2>{label(data.category)} Raw Calls</h2><RawCallList groups={data.groups} /></>}
      {mode === "incidents" && <><h2>{label(data.category)} Incidents</h2><CategoryIncidents rows={data.incidents} category={data.category} onGenerate={generate} /></>}
    </section>
    <section className="pane calls-pane category-pane">
      <h2>Calls by Talkgroup</h2>
      <CategoryCallGroups groups={data.groups} category={data.category} />
    </section>
  </div>;
}

function CategoryInsights({ rows, category, onGenerate }: { rows: CategoryInsight[]; category: string; onGenerate: () => Promise<void> }) {
  if (!rows.length) return <div className="card"><p className="muted">No single-call AI summaries available for this category and time range.</p><button onClick={() => void onGenerate()}>Generate summaries now</button></div>;
  return <>{rows.map(item => <article className={`insight-tile category-${category}`} key={item.id}><div className="insight-head"><span>{item.title}</span><strong className={`confidence ${confidenceClass(item.score)}`}>{Math.round(item.score * 100)}%</strong></div><div className="insight-time">{new Date((item.calls[0]?.rawTimestamp ?? item.firstSeen) * 1000).toLocaleString()}</div><p>{item.detail}</p>{item.calls.length > 0 && <details className="source-shelf"><summary>Transcript and audio</summary><div className="call-actions">{item.calls.map(c => <div className="incident-call" key={c.callId}><div className="incident-call-head"><span>{new Date(c.rawTimestamp * 1000).toLocaleString()}</span><span>Call {c.callId}</span></div><p>{c.transcript}</p><audio controls preload="metadata" src={c.audioUrl} /></div>)}</div></details>}</article>)}</>;
}

function CategoryIncidents({ rows, category, onGenerate }: { rows: Incident[]; category: string; onGenerate: () => Promise<void> }) {
  if (!rows.length) return <div className="card"><p className="muted">No incidents available for this category and time range.</p><button onClick={() => void onGenerate()}>Generate incidents now</button></div>;
  return <div className="incident-explorer category-incident-list">
    {rows.map(i => <details className={`incident-card category-${category}`} key={i.id}>
      <summary>
        <span>{i.title}</span>
        <span className="muted">{i.calls.length} calls</span>
      </summary>
      <div className="incident-meta">
        <span>{incidentTimeRange(i)}</span>
        <strong className={`confidence ${confidenceClass(i.confidence)}`}>{Math.round(i.confidence * 100)}%</strong>
      </div>
      <p>{i.detail}</p>
      <div className="incident-details">
        <div className="muted">Related calls across all categories</div>
        {i.calls.map(c => <div className="incident-call" key={c.callId}>
          <div className="incident-call-head">
            <span>{new Date(c.rawTimestamp * 1000).toLocaleString()}</span>
            <span>Call {c.callId}</span>
          </div>
          <p>{c.transcript}</p>
          <audio controls preload="metadata" src={c.audioUrl} />
        </div>)}
      </div>
    </details>)}
  </div>;
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
  return <div className={`call category-${call.category}`}><div className="call-head"><strong>{call.talkgroupName || `TG ${call.talkgroup}`}</strong><span>{new Date(call.startTime * 1000).toLocaleString()}</span><span>{status}</span>{call.isImported && <span className="pill">Imported</span>}</div><div>{call.transcription || "Pending transcription"}</div>{call.audioPath && <audio controls preload="metadata" src={`/api/v1/calls/${call.id}/audio`} />}</div>;
}

function incidentTimeRange(incident: Incident) {
  const first = new Date(incident.firstSeen * 1000);
  const last = new Date(incident.lastSeen * 1000);
  if (first.toDateString() === last.toDateString()) {
    return `${first.toLocaleString()} - ${last.toLocaleTimeString()}`;
  }
  return `${first.toLocaleString()} - ${last.toLocaleString()}`;
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

function TroubleshootView({ data, rangeHours, reload }: { data: TrTroubleshoot | null; rangeHours: number; reload: () => Promise<void> }) {
  const [topTab, setTopTab] = useState<"pizzad" | "tr">("tr");
  const [trTab, setTrTab] = useState<"summary" | "metrics" | "tools" | "logs" | "insights">("summary");
  const [bySystem, setBySystem] = useState(false);
  const [baseline, setBaseline] = useState("7d");
  const [metricsData, setMetricsData] = useState<TrTroubleshoot | null>(null);
  const [insightText, setInsightText] = useState("");
  const [insightBusy, setInsightBusy] = useState(false);

  useEffect(() => {
    if (topTab !== "tr" || trTab !== "metrics") return;
    void api.request<TrTroubleshoot>(`/api/v1/troubleshoot?${rangeQuery(rangeHours)}&bySystem=${bySystem}&baseline=${baseline}`)
      .then(setMetricsData)
      .catch(() => setMetricsData(null));
  }, [topTab, trTab, bySystem, baseline, rangeHours]);

  if (!data) return <div className="trouble-page">Loading troubleshoot data...</div>;
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
  return (
    <div className="trouble-page">
      <div className="trouble-tabs">
        <button className={topTab === "pizzad" ? "active" : ""} onClick={() => setTopTab("pizzad")}>Pizzad</button>
        <button className={topTab === "tr" ? "active" : ""} onClick={() => setTopTab("tr")}>Trunk Recorder</button>
        <button onClick={() => void reload()}>Refresh Health</button>
      </div>
      {topTab === "pizzad" && <div className="trouble-panel">
        <h2>Pizzad Quality</h2>
        <QualityAuditView data={data} />
      </div>}
      {topTab === "tr" && <div className="trouble-panel">
        <div className="trouble-tabs nested">
          <button className={trTab === "summary" ? "active" : ""} onClick={() => setTrTab("summary")}>Health Summary</button>
          <button className={trTab === "metrics" ? "active" : ""} onClick={() => setTrTab("metrics")}>Metrics</button>
          <button className={trTab === "tools" ? "active" : ""} onClick={() => setTrTab("tools")}>Tools</button>
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
        {trTab === "tools" && <TroubleshootTools rangeHours={rangeHours} reload={reload} />}
        {trTab === "logs" && <pre className="log-box">{data.logOutput}</pre>}
        {trTab === "insights" && <div className="card">
          <button disabled={insightBusy} onClick={() => void generateTroubleshootInsights()}>{insightBusy ? "Generating..." : "Generate Recommendation"}</button>
          <p className="muted">Sends the current health summary, system rows, chart series, and quality snapshot to the configured AI insights endpoint.</p>
          <pre className="log-box">{insightText || data.insightsText}</pre>
        </div>}
      </div>}
    </div>
  );
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
    <div className="remedy-list"><h3>Suggested Remedies</h3>{data.health.remedies.map(r => <div className={`remedy ${r.isIssue ? "issue" : ""}`} key={r.metric}><strong>{r.metric}</strong><p>{r.notes}</p></div>)}</div>
    <details className="card"><summary>Raw health samples</summary><table className="table"><thead><tr><th>Window</th><th>Scope</th><th>Decode 0%</th><th>Avg decode</th><th>Retunes</th><th>No TX</th><th>Stops</th></tr></thead><tbody>{data.health.samples.map(r => <tr key={r.id}><td>{new Date(r.windowStartUtc).toLocaleString()}</td><td>{r.scope}</td><td>{r.decodeZeroPct.toFixed(1)}%</td><td>{r.decodeLines ? (r.decodeRateTotal / r.decodeLines).toFixed(2) : "N/A"}</td><td>{r.retunes}</td><td>{r.noTxRecorded}</td><td>{r.sampleStops}</td></tr>)}</tbody></table></details>
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

function TroubleshootTools({ rangeHours, reload }: { rangeHours: number; reload: () => Promise<void> }) {
  const [diagnosticModels, setDiagnosticModels] = useState<DiagnosticModel[]>([]);
  const [jobId, setJobId] = useState<number | null>(null);
  const [result, setResult] = useState<DiagnosticToolResult | null>(null);
  const [message, setMessage] = useState("");
  const [busy, setBusy] = useState(false);

  useEffect(() => { void loadModels(); }, []);

  async function loadModels() {
    try {
      setDiagnosticModels(await api.request<DiagnosticModel[]>("/api/v1/troubleshoot/tools/transcription-models"));
    } catch (error) {
      setMessage(`Model discovery failed: ${error instanceof Error ? error.message : String(error)}`);
      setDiagnosticModels([]);
    }
  }

  async function start() {
    setResult(null);
    setBusy(true);
    setMessage("Queueing transcription experiment...");
    try {
      const job = await api.request<Job>("/api/v1/troubleshoot/tools/transcription-experiment", { method: "POST", body: JSON.stringify({ ...rangeBody(rangeHours), sampleCount: 50 }) });
      setJobId(job.id);
      setMessage(`Queued job ${job.id}. It will sample up to 50 recent calls, run each discovered model, and try baseline plus cleanup audio variants.`);
      await reload();
    } catch (error) {
      setMessage(`Experiment failed to start: ${error instanceof Error ? error.message : String(error)}`);
    } finally {
      setBusy(false);
    }
  }

  async function loadResult(id = jobId) {
    if (!id) return;
    const rows = await api.request<DiagnosticToolResult>(`/api/v1/troubleshoot/tools/results/${id}`);
    setResult(rows);
    setMessage(`Loaded ${rows.rows.length} result rows for job ${id}.`);
  }

  return <div className="tools-page">
    <div className="card">
      <h3>Transcription Experiment</h3>
      <p className="muted">Samples up to 50 recent calls from the selected global range. Each discovered transcription model runs against baseline audio plus cleanup variants. Results never update stored transcripts.</p>
      <div className="model-chip-list">
        {diagnosticModels.length ? diagnosticModels.map(model => <span className="model-chip" key={model.id}><strong>{model.label}</strong><small>{model.engine}</small></span>) : <span className="muted">No downloaded local models or configured LM/OpenAI transcription model discovered.</span>}
      </div>
      <div>
        <button disabled={busy || diagnosticModels.length === 0} onClick={() => void start()}>{busy ? "Queueing..." : "Run 50-Call Transcription Experiment"}</button>
        <button onClick={() => void loadModels()}>Refresh Models</button>
      </div>
      <div className="muted">{message}</div>
      <div>
        <input type="number" placeholder="Job ID" value={jobId ?? ""} onChange={e => setJobId(Number(e.target.value) || null)} />
        <button onClick={() => void loadResult()}>Load Result</button>
      </div>
    </div>
    {result && <DiagnosticResultView result={result} />}
  </div>;
}

function DiagnosticResultView({ result }: { result: DiagnosticToolResult }) {
  return <div className="card diagnostic-results">
    <h3>{label(result.tool)} Results</h3>
    <table className="table"><thead><tr><th>Call</th><th>Variant</th><th>Model</th><th>Status</th><th>Score</th><th>Time</th><th>Transcript</th><th>Audio</th></tr></thead><tbody>{result.rows.map((row, index) => <tr key={`${row.callId}-${row.variant}-${row.model}-${index}`} className={row.score > 0 ? "useful-row" : ""}>
      <td>{row.callId}</td>
      <td>{row.variant}</td>
      <td>{row.model}</td>
      <td>{row.status}</td>
      <td>{row.score}</td>
      <td>{(row.durationMs / 1000).toFixed(1)}s</td>
      <td>{row.transcript || row.notes}</td>
      <td>{row.audioUrl ? <audio controls preload="metadata" src={row.audioUrl} /> : <span className="muted">n/a</span>}</td>
    </tr>)}</tbody></table>
  </div>;
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

function SettingsView({ jobs, settingsSections, settingsLoadState, reload }: { jobs: Job[]; settingsSections: Record<string, any>; settingsLoadState: { loading: boolean; version: number; message: string; error: boolean }; reload: () => Promise<void> }) {
  const [sections, setSections] = useState<Record<string, any>>({});
  const [message, setMessage] = useState("");
  const [messageKind, setMessageKind] = useState<"info" | "error">("info");
  const [savingSection, setSavingSection] = useState("");
  const [sectionStatus, setSectionStatus] = useState<Record<string, { kind: "ok" | "error" | "info"; text: string }>>({});
  const [testingSection, setTestingSection] = useState("");
  const [models, setModels] = useState<any[]>([]);
  const [modelBusy, setModelBusy] = useState("");

  useEffect(() => {
    setSections(withSettingsDefaults(settingsSections));
    if (settingsLoadState.message) {
      setMessageKind(settingsLoadState.error ? "error" : "info");
      setMessage(settingsLoadState.message);
    }
  }, [settingsSections, settingsLoadState.version]);
  useEffect(() => { void loadModels(); }, []);

  const engine = sections.engine ?? {};
  const transcription = sections.transcription ?? {};
  const aiInsights = sections["ai-insights"] ?? {};
  const sftp = sections.sftp ?? {};
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

  async function downloadModel(model: string) {
    setModelBusy(model);
    try {
      const result = await api.request<any>(`/api/v1/settings/transcription/models/${model}/download`, { method: "POST" });
      setSectionStatus(current => ({ ...current, transcription: { kind: result.ok ? "ok" : "error", text: result.message ?? "Download complete" } }));
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

  async function control(id: number, action: string) { await api.request(`/api/v1/jobs/${id}/control`, { method: "POST", body: JSON.stringify({ action }) }); await reload(); }
  async function deleteJob(id: number) {
    await api.request(`/api/v1/jobs/${id}`, { method: "DELETE" });
    setMessage(`Deleted job ${id}`);
    await reload();
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
        <SettingSelect label="Engine" description="Choose the transcription backend for new calls." value={transcription.provider} options={["none", "whisper", "vosk", "lmstudio", "openai"]} onChange={v => update("transcription", ["provider"], v)} />
        {transcription.provider === "whisper" && <>
          <SettingSelect label="Whisper model" description="Select a managed model. Download missing models below." value={modelIdForPath(models, "whisper", transcription.whisperModelFile)} options={modelOptions(models, "whisper")} onChange={v => update("transcription", ["whisperModelFile"], modelPath(models, v))} />
          <ModelManager engine="whisper" rows={models} busy={modelBusy} selectedPath={transcription.whisperModelFile} onUse={path => update("transcription", ["whisperModelFile"], path)} onDownload={downloadModel} onDelete={deleteModel} />
        </>}
        {transcription.provider === "vosk" && <>
          <SettingSelect label="Vosk model" description="Select a managed model. Download missing models below." value={modelIdForPath(models, "vosk", transcription.voskModelPath)} options={modelOptions(models, "vosk")} onChange={v => update("transcription", ["voskModelPath"], modelPath(models, v))} />
          <ModelManager engine="vosk" rows={models} busy={modelBusy} selectedPath={transcription.voskModelPath} onUse={path => update("transcription", ["voskModelPath"], path)} onDownload={downloadModel} onDelete={deleteModel} />
        </>}
        {(transcription.provider === "lmstudio" || transcription.provider === "openai") && <>
          <SettingInput label="Base URL" description="OpenAI-compatible audio transcription endpoint base URL." value={transcription.openAiBaseUrl} onChange={v => update("transcription", ["openAiBaseUrl"], v)} />
          <SettingInput label="Model" description="Model name sent to the audio transcription endpoint." value={transcription.openAiModel} onChange={v => update("transcription", ["openAiModel"], v)} />
          <SettingInput label="API key" description="Optional bearer token for remote transcription endpoints." type="password" value={transcription.openAiApiKey} onChange={v => update("transcription", ["openAiApiKey"], v)} />
        </>}
        {transcription.provider === "none" && <p className="setting-note">New calls will be stored without transcription.</p>}
      </SettingsCard>

      <SettingsCard title="Insights (LM Link)" description="One switch controls all LLM usage: call summaries, incidents, and troubleshooting recommendations." busy={savingSection === "ai-insights"} testing={testingSection === "ai-insights"} status={sectionStatus["ai-insights"]} onSave={() => saveWithTest("ai-insights")} onTest={() => test("ai-insights")}>
        <SettingCheckbox label="Enable AI usage" description="When off, pizzad will not call LM Studio or other LLM endpoints." checked={aiInsights.enabled} onChange={v => update("ai-insights", ["enabled"], v)} />
        <SettingInput label="Base URL" description="OpenAI-compatible chat endpoint base URL, often an LM Studio server." value={aiInsights.openAiBaseUrl} onChange={v => update("ai-insights", ["openAiBaseUrl"], v)} />
        <SettingInput label="Model" description="Chat model used for summaries, incidents, and recommendations." value={aiInsights.openAiModel} onChange={v => update("ai-insights", ["openAiModel"], v)} />
        <SettingInput label="API key" description="Optional bearer token. LM Studio local/link setups may leave this blank." type="password" value={aiInsights.openAiApiKey} onChange={v => update("ai-insights", ["openAiApiKey"], v)} />
        <SettingInput label="Timeout (ms)" description="Maximum wait for a single LLM request." type="number" value={aiInsights.timeoutMs} onChange={v => update("ai-insights", ["timeoutMs"], numberOrZero(v))} />
        <SettingInput label="Retries" description="Retry attempts after a failed LLM request." type="number" value={aiInsights.maxRetries} onChange={v => update("ai-insights", ["maxRetries"], numberOrZero(v))} />
      </SettingsCard>

      <SettingsCard title="Alerts / Email" description="Outbound notification settings for live alert matches. Imported calls still store matches but suppress live notifications." busy={savingSection === "alerts"} testing={testingSection === "alerts"} status={sectionStatus.alerts} onSave={() => save("alerts")} onTest={() => saveWithTest("alerts")}>
        <SettingCheckbox label="Enable email alerts" description="Turns live outbound email delivery on or off." checked={alerts.emailEnabled} onChange={v => update("alerts", ["emailEnabled"], v)} />
        <SettingSelect label="Email provider" description="SMTP preset used by the alert sender." value={alerts.emailProvider} options={["gmail", "yahoo"]} onChange={v => update("alerts", ["emailProvider"], v)} />
        <SettingInput label="Email address" description="Sender account used for alert delivery." value={alerts.emailUser} onChange={v => update("alerts", ["emailUser"], v)} />
        <SettingInput label="App password" description="Provider-specific app password or SMTP credential." type="password" value={alerts.emailPassword} onChange={v => update("alerts", ["emailPassword"], v)} />
        <p className="setting-note">{alerts.rules?.length ?? 0} alert rule(s) configured. Rule management remains in the alerts settings workflow.</p>
      </SettingsCard>

      <SettingsCard title="SFTP Import" description="Reads historical trunk-recorder .bin files from an archive and imports them into the normal local call store." busy={savingSection === "sftp"} testing={testingSection === "sftp"} status={sectionStatus.sftp} onSave={() => save("sftp")} onTest={() => saveWithTest("sftp")}>
        <SettingCheckbox label="Enable SFTP import" description="Allows quick imports and larger background priming jobs." checked={sftp.enabled} onChange={v => update("sftp", ["enabled"], v)} />
        <SettingInput label="Host" description="SFTP server hostname or IP address." value={sftp.host} onChange={v => update("sftp", ["host"], v)} />
        <SettingInput label="Port" description="SFTP port, normally 22." type="number" value={sftp.port} onChange={v => update("sftp", ["port"], numberOrZero(v))} />
        <SettingInput label="Username" description="Account used to read archive files." value={sftp.username} onChange={v => update("sftp", ["username"], v)} />
        <SettingSelect label="Auth mode" description="Private key is preferred; password auth is available for simpler archives." value={sftp.authMode} options={["privateKey", "password"]} onChange={v => update("sftp", ["authMode"], v)} />
        <SettingInput label="Password" description="Used only when auth mode is password." type="password" value={sftp.password} onChange={v => update("sftp", ["password"], v)} />
        <SettingInput label="Private key path" description="Path on the pizzad Linux host." value={sftp.privateKeyPath} onChange={v => update("sftp", ["privateKeyPath"], v)} />
        <SettingInput label="Key passphrase" description="Optional passphrase for encrypted private keys." type="password" value={sftp.privateKeyPassphrase} onChange={v => update("sftp", ["privateKeyPassphrase"], v)} />
        <SettingInput label="Remote root" description="Top-level archive directory to search." value={sftp.remoteRoot} onChange={v => update("sftp", ["remoteRoot"], v)} />
        <div className="settings-subsection"><h4>Run Import</h4><SftpImport reload={reload} /></div>
      </SettingsCard>

      <div className="card settings-card wide">
        <div className="settings-card-meta">
          <h3>Jobs / Imports</h3>
          <p>Background work created by SFTP imports, diagnostics, transcription experiments, and summary generation.</p>
        </div>
        <div className="settings-fields">
        {jobs.length ? jobs.map(j => <div className="job" key={j.id}><div className="job-head"><strong>{j.type}</strong><span className={`job-status status-${j.status}`}>{j.status}</span></div><div className="muted">{j.completed}/{j.total} complete, {j.failed} failed - {j.message}</div><div className="job-actions"><button onClick={() => control(j.id, "pause")}>Pause</button><button onClick={() => control(j.id, "resume")}>Resume</button><button onClick={() => control(j.id, "cancel")}>Cancel</button><button onClick={() => deleteJob(j.id)} disabled={j.status === "running" || j.status === "queued" || j.status === "paused"}>Delete</button></div></div>) : <span className="muted">No jobs</span>}
        </div>
      </div>

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
  return <div className="model-manager">
    {rows.map(row => <div className="model-row" key={row.id}>
      <span><strong>{row.label}</strong><small>{row.installed ? `${formatBytes(row.bytes)} installed${row.path === selectedPath ? " - selected" : ""}` : "Not installed"}</small></span>
      <div>
        {row.installed && <button onClick={() => onUse(row.path)}>Use</button>}
        {row.installed ? <button disabled={busy === row.id} onClick={() => void onDelete(row.id)}>{busy === row.id ? "Removing..." : "Remove"}</button> : <button disabled={busy === row.id} onClick={() => void onDownload(row.id)}>{busy === row.id ? "Downloading..." : "Download"}</button>}
      </div>
    </div>)}
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
    openAiBaseUrl: "http://localhost:1234/v1",
    openAiApiKey: "",
    openAiModel: "",
    analogSampleRate: 8000,
    ...(sections.transcription ?? {})
  };
  sections.transcription.provider = sections.transcription.provider || "none";
  sections.transcription.whisperModelFile = sections.transcription.whisperModelFile ?? "";
  sections.transcription.voskModelPath = sections.transcription.voskModelPath ?? "";
  sections.transcription.openAiBaseUrl = sections.transcription.openAiBaseUrl || "http://localhost:1234/v1";
  sections.transcription.openAiApiKey = sections.transcription.openAiApiKey ?? "";
  sections.transcription.openAiModel = sections.transcription.openAiModel ?? "";
  sections.transcription.analogSampleRate = sections.transcription.analogSampleRate || 8000;
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

function SftpImport({ reload }: { reload: () => Promise<void> }) {
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
    <p className="muted">Quick imports are capped at 48h. Larger imports prime the pizzastack as throttled background jobs.</p>
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
    <button disabled={!!busy} onClick={estimate}>{busy === "estimate" ? "Estimating..." : "Estimate"}</button>
    <button disabled={!!busy} onClick={() => void run(false)}>{busy === "quick" ? "Queueing..." : "Quick Import"}</button>
    <button disabled={!!busy} onClick={() => void run(true)}>{busy === "prime" ? "Queueing..." : "Prime Pizzastack"}</button>
    <div className={message.startsWith("Estimate failed") || message.startsWith("Import failed") ? "settings-message error" : "settings-message ok"}>{message}</div>
  </>;
}

function toDateTimeInput(date: Date) {
  const local = new Date(date.getTime() - date.getTimezoneOffset() * 60000);
  return local.toISOString().slice(0, 16);
}

function navIcon(item: Page) {
  if (item === "dashboard") return <Gauge size={15} />;
  if (item === "settings") return <Settings size={15} />;
  if (item === "troubleshoot") return <Activity size={15} />;
  return <Radio size={15} />;
}

function label(value: string) { return value === "ems" ? "EMS" : value[0].toUpperCase() + value.slice(1); }

createRoot(document.getElementById("root")!).render(<App />);
