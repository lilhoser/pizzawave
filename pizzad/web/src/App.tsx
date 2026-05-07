import React, { useCallback, useEffect, useMemo, useState } from "react";
import { createRoot } from "react-dom/client";
import { Activity, Bell, Gauge, Radio, Settings, ShieldAlert } from "lucide-react";
import { api, rangeBody, rangeQuery } from "./api";
import type { AlertMatch, BarStat, CategoryInsight, CategoryPage, Dashboard, EngineCall, EngineHealth, HourCategory, Incident, Job, QualityHour, TopTalkgroup, TrHealthChart, TrHealthMetric, TrTroubleshoot } from "./types";
import "./style.css";

const categories = ["police", "fire", "ems", "traffic", "other"] as const;
type Page = "dashboard" | "troubleshoot" | "settings" | typeof categories[number];
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
  const [categoryViewMode, setCategoryViewMode] = useState(() => localStorage.getItem("pizzawave-category-view") || "split");
  const [status, setStatus] = useState("Starting");
  const [dashboard, setDashboard] = useState<Dashboard | null>(null);
  const [category, setCategory] = useState<CategoryPage | null>(null);
  const [jobs, setJobs] = useState<Job[]>([]);
  const [engineHealth, setEngineHealth] = useState<EngineHealth | null>(null);
  const [troubleshoot, setTroubleshoot] = useState<TrTroubleshoot | null>(null);
  const [settingsSections, setSettingsSections] = useState<Record<string, any>>({});

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
      } else if (page === "settings") {
        const [engine, transcription, aiInsights, sftp, tr, auth, alerts] = await Promise.all([
          api.request<any>("/api/v1/settings/engine"),
          api.request<any>("/api/v1/settings/transcription"),
          api.request<any>("/api/v1/settings/ai-insights"),
          api.request<any>("/api/v1/settings/sftp"),
          api.request<any>("/api/v1/settings/tr"),
          api.request<any>("/api/v1/settings/auth"),
          api.request<any>("/api/v1/settings/alerts")
        ]);
        setJobs(jobRows);
        setSettingsSections({
          engine: engine.values,
          transcription: transcription.values,
          "ai-insights": aiInsights.values,
          sftp: sftp.values,
          tr: tr.values,
          auth: auth.values,
          alerts: alerts.values
        });
      }
      setStatus("Live");
    } catch (error) {
      setStatus(error instanceof Error ? error.message : "Error");
    }
  }, [page, rangeHours]);

  useEffect(() => { void load(); }, [load]);

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
        {categories.includes(page as any) && <div className="segmented" aria-label="Category view mode">
          <button className={categoryViewMode === "split" ? "active" : ""} onClick={() => setCategoryViewMode("split")}>Insights + Calls</button>
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
        {page === "settings" && <SettingsView jobs={jobs} settingsSections={settingsSections} rangeHours={rangeHours} reload={load} />}
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
        <div className="section"><h3>Quality</h3><QualityByHourChart rows={data.qualityByHour} /><Bars title="Inaudible by System" rows={data.inaudibleBySystem} /><Bars title="Problem Talkgroups" rows={data.problemTalkgroups} /></div>
        <div className="section"><h3>Distribution</h3><Bars title="Category Share" rows={data.categoryShare} /></div>
        <div className="section"><h3>Exploration</h3><p className="muted">Server-computed dashboard data keeps browser behavior consistent with native clients.</p></div>
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
  return <div className="card chart-card"><h4>Calls by Hour and Category</h4><svg className="chart" viewBox="0 0 500 190" role="img" aria-label="Calls by hour and category"><line className="axis" x1="32" y1="158" x2="482" y2="158" /><line className="axis" x1="32" y1="28" x2="32" y2="158" />{[0, 6, 12, 18, 23].map(hour => <text className="chart-label" x={36 + hour * 19} y="178" key={hour}>{hour}</text>)}<text className="chart-label" x="4" y="34">{max}</text>{byCategory.map(series => <polyline key={series.category} fill="none" stroke={categoryColors[series.category]} strokeWidth="2.5" points={points(series.values)} />)}</svg><Legend items={byCategory.map(c => [label(c.category), categoryColors[c.category]])} /></div>;
}

function QualityByHourChart({ rows }: { rows: QualityHour[] }) {
  const hours = Array.from({ length: 24 }, (_, i) => i);
  const keys: (keyof Omit<QualityHour, "hour">)[] = ["inaudible", "short", "empty", "failure"];
  const totals = hours.map(hour => {
    const row = rows.find(r => r.hour === hour);
    return row ? keys.reduce((sum, key) => sum + row[key], 0) : 0;
  });
  const max = Math.max(1, ...totals);
  return <div className="card chart-card"><h4>Quality Problems by Hour</h4><svg className="chart" viewBox="0 0 500 190" role="img" aria-label="Quality problems by hour"><line className="axis" x1="32" y1="158" x2="482" y2="158" /><line className="axis" x1="32" y1="28" x2="32" y2="158" /><text className="chart-label" x="4" y="34">{max}</text>{[0, 6, 12, 18, 23].map(hour => <text className="chart-label" x={36 + hour * 19} y="178" key={hour}>{hour}</text>)}{hours.map(hour => {
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
  return <table className="table"><thead><tr><th>Talkgroup</th><th>Calls</th><th>Share</th><th>Trend</th></tr></thead><tbody>{rows.map(r => <tr key={r.talkgroup}><td>{r.label}</td><td>{r.count}</td><td>{(r.share * 100).toFixed(1)}%</td><td><div>{r.trend.map((v, i) => <span className="trend" style={{ height: 4 + v * 18 }} key={i} />)}</div><div className="muted">{r.trendStartLabel} - {r.trendBucketLabel} - {r.trendEndLabel}</div></td></tr>)}</tbody></table>;
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

function CategoryView({ data, mode, rangeHours, reload }: { data: CategoryPage | null; mode: string; rangeHours: number; reload: () => Promise<void> }) {
  if (!data) return <div className="category-page">Loading...</div>;
  if (mode === "raw") {
    return <div className="category-page raw-category" data-category={data.category}><CategoryCallGroups groups={data.groups} category={data.category} /></div>;
  }
  async function generate() {
    await api.request("/api/v1/incidents/generate", { method: "POST", body: JSON.stringify({ ...rangeBody(rangeHours), confirmLargeRange: rangeHours > 168 }) });
    await reload();
  }
  return <div className="category-split-page" data-category={data.category}><section className="pane insights-pane category-pane"><h2>{label(data.category)} Insights</h2><CategoryInsights rows={data.insights} category={data.category} onGenerate={generate} /></section><section className="pane calls-pane category-pane"><h2>Calls by Talkgroup</h2><CategoryCallGroups groups={data.groups} category={data.category} /></section></div>;
}

function CategoryInsights({ rows, category, onGenerate }: { rows: CategoryInsight[]; category: string; onGenerate: () => Promise<void> }) {
  if (!rows.length) return <div className="card"><p className="muted">No insight summaries available for this category and time range.</p><button onClick={() => void onGenerate()}>Generate summaries now</button></div>;
  return <>{rows.map(item => <details className={`insight-tile category-${category}`} key={item.id} open><summary><span>{item.title}</span><strong className={`confidence ${confidenceClass(item.score)}`}>{Math.round(item.score * 100)}%</strong></summary><div className="insight-time">{new Date(item.firstSeen * 1000).toLocaleString()} - {new Date(item.lastSeen * 1000).toLocaleTimeString()}</div><p>{item.detail}</p><div className="call-actions">{item.calls.map(c => <div className="incident-call" key={c.callId}><div className="incident-call-head"><span>{new Date(c.rawTimestamp * 1000).toLocaleString()}</span><span>Call {c.callId}</span></div><p>{c.transcript}</p><audio controls preload="metadata" src={c.audioUrl} /></div>)}</div></details>)}</>;
}

function CategoryCallGroups({ groups, category }: { groups: CategoryPage["groups"]; category?: string }) {
  if (!groups.length) return <div className="card"><p className="muted">No raw calls available for this category.</p></div>;
  return <>{groups.map(group => <CollapsibleCallGroup group={group} category={category} key={group.label} />)}</>;
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
  const [trTab, setTrTab] = useState<"summary" | "metrics" | "logs" | "diagnostics" | "insights">("summary");
  const [bySystem, setBySystem] = useState(false);
  const [baseline, setBaseline] = useState("7d");
  const [metricsData, setMetricsData] = useState<TrTroubleshoot | null>(null);

  useEffect(() => {
    if (topTab !== "tr" || trTab !== "metrics") return;
    void api.request<TrTroubleshoot>(`/api/v1/troubleshoot?${rangeQuery(rangeHours)}&bySystem=${bySystem}&baseline=${baseline}`)
      .then(setMetricsData)
      .catch(() => setMetricsData(null));
  }, [topTab, trTab, bySystem, baseline, rangeHours]);

  if (!data) return <div className="trouble-page">Loading troubleshoot data...</div>;
  const active = metricsData ?? data;
  return (
    <div className="trouble-page">
      <div className="trouble-tabs">
        <button className={topTab === "pizzad" ? "active" : ""} onClick={() => setTopTab("pizzad")}>Pizzad</button>
        <button className={topTab === "tr" ? "active" : ""} onClick={() => setTopTab("tr")}>Trunk Recorder</button>
        <button onClick={() => void reload()}>Refresh Health</button>
      </div>
      {topTab === "pizzad" && <div className="trouble-panel">
        <h2>Pizzad Diagnostics</h2>
        <pre className="log-box">{data.diagnostics}</pre>
      </div>}
      {topTab === "tr" && <div className="trouble-panel">
        <div className="trouble-tabs nested">
          <button className={trTab === "summary" ? "active" : ""} onClick={() => setTrTab("summary")}>Health Summary</button>
          <button className={trTab === "metrics" ? "active" : ""} onClick={() => setTrTab("metrics")}>Metrics</button>
          <button className={trTab === "logs" ? "active" : ""} onClick={() => setTrTab("logs")}>Log Output</button>
          <button className={trTab === "diagnostics" ? "active" : ""} onClick={() => setTrTab("diagnostics")}>Diagnostics</button>
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
        {trTab === "logs" && <pre className="log-box">{data.logOutput}</pre>}
        {trTab === "diagnostics" && <pre className="log-box">{data.diagnostics}</pre>}
        {trTab === "insights" && <div className="card"><button disabled>Generate Recommendation</button><p className="muted">Uses LM Link to summarize issues and baselines.</p><pre className="log-box">{data.insightsText}</pre></div>}
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

function SettingsView({ jobs, settingsSections, rangeHours, reload }: { jobs: Job[]; settingsSections: Record<string, any>; rangeHours: number; reload: () => Promise<void> }) {
  async function control(id: number, action: string) { await api.request(`/api/v1/jobs/${id}/control`, { method: "POST", body: JSON.stringify({ action }) }); await reload(); }
  async function regenToken() { await api.request("/api/v1/settings/auth/regenerate-token", { method: "POST" }); alert("Token regenerated on server. Update this browser token from the token file."); }
  async function generate() { await api.request("/api/v1/incidents/generate", { method: "POST", body: JSON.stringify({ ...rangeBody(rangeHours), confirmLargeRange: rangeHours > 168 }) }); await reload(); }
  return <div className="settings-page"><h2>Settings</h2><div className="card"><h3>Jobs / Imports</h3>{jobs.length ? jobs.map(j => <div className="job" key={j.id}><strong>{j.type}</strong> - {j.status}<div className="muted">{j.completed}/{j.total} complete, {j.failed} failed - {j.message}</div><button onClick={() => control(j.id, "pause")}>Pause</button><button onClick={() => control(j.id, "resume")}>Resume</button><button onClick={() => control(j.id, "cancel")}>Cancel</button></div>) : <span className="muted">No jobs</span>}</div><div className="card"><h3>SFTP Import</h3><SftpImport reload={reload} /></div><div className="card"><h3>Summaries / Incidents</h3><button onClick={generate}>Generate incidents for selected range</button></div>{["engine", "transcription", "ai-insights", "sftp", "tr", "alerts", "auth"].map(section => <SettingsJsonEditor section={section} value={settingsSections[section]} reload={reload} key={section} />)}<div className="card"><h3>Auth Token</h3><button onClick={regenToken}>Regenerate token</button></div></div>;
}

function SettingsJsonEditor({ section, value, reload }: { section: string; value: any; reload: () => Promise<void> }) {
  const [text, setText] = useState("");
  useEffect(() => { setText(JSON.stringify(value ?? {}, null, 2)); }, [value]);
  async function save() {
    await api.request(`/api/v1/settings/${section}`, { method: "POST", body: JSON.stringify({ values: JSON.parse(text) }) });
    await reload();
  }
  return <div className="card"><h3>{label(section)} Settings</h3><textarea value={text} onChange={e => setText(e.target.value)} /><button onClick={save}>Save {label(section)}</button></div>;
}

function SftpImport({ reload }: { reload: () => Promise<void> }) {
  const [start, setStart] = useState("");
  const [end, setEnd] = useState("");
  const [message, setMessage] = useState("");
  async function estimate() { const r = await api.request<any>("/api/v1/imports/sftp/estimate", { method: "POST", body: JSON.stringify({ startLocal: new Date(start).toISOString(), endLocal: new Date(end).toISOString() }) }); setMessage(r.message); }
  async function run(confirmLargeImport: boolean) { const r = await api.request<Job>("/api/v1/imports/sftp/import", { method: "POST", body: JSON.stringify({ startLocal: new Date(start).toISOString(), endLocal: new Date(end).toISOString(), confirmLargeImport }) }); setMessage(`Queued job ${r.id}`); await reload(); }
  return <><p className="muted">Quick imports are capped at 48h. Larger imports prime the pizzastack as throttled background jobs.</p><input type="datetime-local" value={start} onChange={e => setStart(e.target.value)} /><input type="datetime-local" value={end} onChange={e => setEnd(e.target.value)} /><button onClick={estimate}>Estimate</button><button onClick={() => run(false)}>Quick Import</button><button onClick={() => run(true)}>Prime Pizzastack</button><div className="muted">{message}</div></>;
}

function navIcon(item: Page) {
  if (item === "dashboard") return <Gauge size={15} />;
  if (item === "settings") return <Settings size={15} />;
  if (item === "troubleshoot") return <Activity size={15} />;
  return <Radio size={15} />;
}

function label(value: string) { return value === "ems" ? "EMS" : value[0].toUpperCase() + value.slice(1); }

createRoot(document.getElementById("root")!).render(<App />);
