import React, { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { createPortal, flushSync } from "react-dom";
import { createRoot } from "react-dom/client";
import { Activity, Bell, BellOff, Camera, CheckCircle2, ChevronDown, ChevronRight, Database, Gauge, Info, Link2, Play, Radio, RefreshCw, Search, Settings, Square, Wrench, X } from "lucide-react";
import { api, rangeBody, rangeQuery } from "./api";
import type { AuthTokenRequest } from "./api";
import { usePersistentRefresh } from "./refresh";
import type { RefreshState } from "./refresh";
import type { IncidentDecisionChainPage, IncidentDecisionGroup } from "./types";
import type { AlertMatch, AlertTalkgroupRef, BackupArchive, BackupEstimate, BackupRestoreApplyResult, BackupRestoreCancelResult, BackupRestorePreview, BarStat, CallVolumeBucket, CategoryPage, Dashboard, EngineCall, EngineHealth, Incident, IncidentDecisionPerformance, IncidentOperationAuditRow, Job, JobLog, LocationHeat, ProcessingProfile, ProfileState, ProfileTalkgroupSetting, QualityAuditGroup, QualityAuditSample, QualityHour, QueueSnapshot, RemoteBandwidthReport, RfSurveyApplySourceDraftResponse, RfSurveyCancelExperimentResult, RfSurveyConfigDraft, RfSurveyDetail, RfSurveyExperiment, RfSurveyExperimentPlan, RfSurveyPathProfile, RfSurveyProfile, RfSurveySource, RfSurveySweepCandidateProgress, RfSurveySweepProgress, RfSurveySweepProgressRow, RfSurveySystem, RfSurveyToolPrep, RfSurveyWaterfallStatus, SetupAreaBoundaryCandidate, SetupAreaBoundaryResponse, SetupArtifactReport, SetupCalibrationPlan, SetupRfHistory, SetupRfHistoryRow, SetupSdrDetection, SetupStatus, SetupTalkgroupSyncResult, SetupTrConfigDraft, SetupTrConfigSite, SetupTrConfigSites, SetupValidationResult, SiteSetup, SiteSetupActivity, SiteSetupConfig, SiteSetupMonitoredArea, SiteSetupPendingChange, SiteSetupSourcePlanOption, SiteSetupSourcePlanProjection, StatusSummary, SystemCpuSnapshot, SystemRecommendation, SystemRecommendations, SystemRecommendationSummary, SystemResetResult, SystemRuntimeResourceSample, TalkgroupCatalogDocument, TalkgroupCatalogImport, TalkgroupCatalogItem, TalkgroupCatalogPage, TalkgroupCatalogResponse, TokenUsageReport, TopTalkgroup, TranscriptionGroup, TranscriptionLatencyBucket, TranscriptionOutcomeBucket, TranscriptionPerformance, TrConfigEditor, TrConfigEditorApplyResult, TrConfigViewer, TrHealthChart, TrHealthMetric, TrLogPage, TrMetricAssessment, TrRfAnalysis, TrTroubleshoot } from "./types";
import "./style.css";

const categories = ["police", "fire", "ems", "traffic", "utilities", "other"] as const;
const talkgroupCategoryOptions = [...categories];
const themeOptions = [
  { value: "blue", label: "Blue" },
  { value: "orange", label: "Orange" },
  { value: "red", label: "Red" },
  { value: "purple", label: "Purple" },
  { value: "green", label: "Green" }
] as const;
type Theme = typeof themeOptions[number]["value"];
function normalizeTheme(value: string | null): Theme {
  return themeOptions.some(option => option.value === value) ? value as Theme : "blue";
}
const siteSetupApi = "/api/v1/setup/site";
const siteSetupRfApi = `${siteSetupApi}/rf`;
const rfDetailUrl = (apiBase: string, id: string, compact = true) => `${apiBase}/${encodeURIComponent(id)}${compact ? "?compact=true" : ""}`;
const waterfallStopUrl = (apiBase: string, surveyId: string) => `${apiBase}/${encodeURIComponent(surveyId)}/waterfall/stop`;
type Page = "dashboard" | "setup" | "system" | "settings" | typeof categories[number];
type DashboardMode = "incidents" | "alerts";
type CategorySortMode = "name" | "tgid" | "recent" | "frequent";
type AuthPromptState = { request: AuthTokenRequest; resolve: (token: string | null) => void; token: string; message: string };
const categoryColors: Record<string, string> = {
  police: "#5aa7ff",
  fire: "#ff6b5a",
  ems: "#54d68a",
  traffic: "#f7c948",
  utilities: "#35c2a1",
  other: "#b58cff"
};
const qualityColors: Record<keyof Omit<QualityHour, "hour">, string> = {
  inaudible: "#5aa7ff",
  short: "#f7c948",
  empty: "#9faab5",
  failure: "#ff6b5a"
};
type AutoplayContext = { key: string; kind: "alert" | "incident" | "traffic" | "police"; callId: number; incidentId?: number; label: string };

function radioReferenceSitesCacheKey(sid: string) {
  return `pizzawave-setup-rr-sites-${sid.trim()}`;
}

function readCachedRadioReferenceSites(sid: string): SetupTrConfigSites | null {
  if (!sid.trim()) return null;
  try {
    const raw = localStorage.getItem(radioReferenceSitesCacheKey(sid));
    if (!raw) return null;
    const parsed = JSON.parse(raw) as SetupTrConfigSites;
    return parsed && Array.isArray(parsed.sites) ? { ...parsed, radioReferenceSid: parsed.radioReferenceSid || sid.trim() } : null;
  } catch {
    return null;
  }
}

function writeCachedRadioReferenceSites(sid: string, sites: SetupTrConfigSites) {
  if (!sid.trim()) return;
  try {
    localStorage.setItem(radioReferenceSitesCacheKey(sid), JSON.stringify(sites));
  } catch {
    // Cache is only a convenience; explicit reload still works if storage is full or blocked.
  }
}

function downloadFileFromResponse(response: Response, fallbackName: string): Promise<string> {
  return response.blob().then(blob => {
    const disposition = response.headers.get("Content-Disposition") || "";
    const fileName = filenameFromContentDisposition(disposition) || fallbackName;
    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = url;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    link.remove();
    URL.revokeObjectURL(url);
    return fileName;
  });
}

function filenameFromContentDisposition(disposition: string): string {
  const encoded = /filename\*=UTF-8''([^;]+)/i.exec(disposition)?.[1];
  if (encoded) {
    try {
      return decodeURIComponent(encoded).replaceAll(/[\\/]/g, "-");
    } catch {
      return encoded.replaceAll(/[\\/]/g, "-");
    }
  }
  const quoted = /filename="([^"]+)"/i.exec(disposition)?.[1];
  if (quoted) return quoted.replaceAll(/[\\/]/g, "-");
  const plain = /filename=([^;]+)/i.exec(disposition)?.[1]?.trim();
  return plain ? plain.replaceAll(/[\\/]/g, "-") : "";
}

function categoryPageKey(page: Page, rangeHours: number, search: string) {
  return `${page}|${rangeHours}|${search.trim()}`;
}

function formatRefreshTime(timestamp: number | null) {
  return timestamp ? new Date(timestamp).toLocaleTimeString([], { hour: "numeric", minute: "2-digit", second: "2-digit" }) : "not yet";
}

function RefreshNotice({ state, hasData, onRetry }: { state: RefreshState; hasData: boolean; onRetry: () => Promise<unknown> }) {
  if (!hasData && !state.error)
    return <div className="page-load-state" role="status"><RefreshCw size={16} className="spin" /> Loading current data...</div>;
  if (!hasData && state.error)
    return <div className="page-refresh-notice error" role="alert"><span>Unable to load this page: {state.error}</span><button onClick={() => void onRetry()}>Retry</button></div>;
  if (state.error)
    return <div className="page-refresh-notice error" role="alert"><span>Update failed. Showing data last updated at {formatRefreshTime(state.lastUpdatedAt)}. {state.error}</span><button onClick={() => void onRetry()}>Retry now</button></div>;
  return <div className="page-refresh-notice" role="status">
    <span>{state.refreshing ? "Refreshing..." : `Updated ${formatRefreshTime(state.lastUpdatedAt)}`}</span>
    <button disabled={state.refreshing} onClick={() => void onRetry()}>{state.refreshing ? "Refreshing..." : "Refresh"}</button>
  </div>;
}

function SettingsLoadNotice({ state, hasData, dirty, onReload }: { state: RefreshState; hasData: boolean; dirty: boolean; onReload: () => Promise<unknown> }) {
  if (!hasData && !state.error)
    return <div className="page-load-state" role="status"><RefreshCw size={16} className="spin" /> Loading server settings...</div>;
  if (!hasData)
    return <div className="page-refresh-notice error" role="alert"><span>Settings are unavailable: {state.error}</span><button onClick={() => void onReload()}>Retry</button></div>;
  return <div className={state.error ? "page-refresh-notice error" : "page-refresh-notice"} role={state.error ? "alert" : "status"}>
    <span>{state.error ? `Reload failed. The current draft is still based on server settings loaded at ${formatRefreshTime(state.lastUpdatedAt)}. ${state.error}` : `Server settings loaded ${formatRefreshTime(state.lastUpdatedAt)}`}</span>
    <button disabled={dirty || state.refreshing} title={dirty ? "Save or discard changes before reloading server settings." : ""} onClick={() => void onReload()}>{state.refreshing ? "Reloading..." : "Reload from server"}</button>
  </div>;
}

function PanelLoadState({ label: panelLabel, state, hasData, onRetry, showUpdated = false }: { label: string; state: RefreshState; hasData: boolean; onRetry: () => Promise<unknown>; showUpdated?: boolean }) {
  if (!hasData && state.loading)
    return <div className="panel-load-state" role="status"><RefreshCw size={14} className="spin" /> Loading {panelLabel}...</div>;
  if (!state.error)
    return showUpdated && hasData ? <div className="page-refresh-notice" role="status"><span>{panelLabel} updated {formatRefreshTime(state.lastUpdatedAt)}</span></div> : null;
  return <div className="page-refresh-notice error panel-refresh-notice" role="alert">
    <span>{hasData ? `${panelLabel} update failed. Showing data from ${formatRefreshTime(state.lastUpdatedAt)}. ${state.error}` : `Unable to load ${panelLabel}: ${state.error}`}</span>
    <button onClick={() => void onRetry()}>Retry</button>
  </div>;
}

function PageSearch({ value, onChange, placeholder }: { value: string; onChange: (value: string) => void; placeholder: string }) {
  return <label className="global-search page-search">
    <Search size={15} aria-hidden="true" />
    <input type="search" value={value} placeholder={placeholder} aria-label={placeholder} onChange={event => onChange(event.target.value)} />
  </label>;
}

function App() {
  const [page, setPageState] = useState<Page>(() => normalizePage(localStorage.getItem("pizzawave-page")));
  const [rangeHours, setRangeHours] = useState(24);
  const [theme, setTheme] = useState<Theme>(() => normalizeTheme(localStorage.getItem("pizzawave-theme")));
  const [appNotice, setAppNotice] = useState("");
  const [jobs, setJobs] = useState<Job[]>([]);
  const [engineHealth, setEngineHealth] = useState<EngineHealth | null>(null);
  const [statusSummary, setStatusSummary] = useState<StatusSummary | null>(null);
  const [profileState, setProfileState] = useState<ProfileState | null>(null);
  const [pendingProfileHides, setPendingProfileHides] = useState<ProfileTalkgroupSetting[]>([]);
  const [setupStatus, setSetupStatus] = useState<SetupStatus | null>(null);
  const [monitoringCheckedAt, setMonitoringCheckedAt] = useState<number | null>(null);
  const [cpuSnapshot, setCpuSnapshot] = useState<SystemCpuSnapshot | null>(null);
  const [settingsSections, setSettingsSections] = useState<Record<string, any>>({});
  const [settingsLoadState, setSettingsLoadState] = useState<{ loading: boolean; version: number; message: string; error: boolean }>({ loading: false, version: 0, message: "", error: false });
  const [settingsDirty, setSettingsDirty] = useState(false);
  const [activeAlertCount, setActiveAlertCount] = useState(0);
  const [systemProblemCount, setSystemProblemCount] = useState(0);
  const [alertSettings, setAlertSettings] = useState<any>(null);
  const [autoplayMuted, setAutoplayMuted] = useState(() => localStorage.getItem("pizzawave-autoplay-muted") === "1");
  const [autoplayMenuOpen, setAutoplayMenuOpen] = useState(false);
  const [activeAutoplay, setActiveAutoplay] = useState<AutoplayContext | null>(null);
  const [focusedIncidentId, setFocusedIncidentId] = useState<number | null>(null);
  const [focusedHashTarget, setFocusedHashTarget] = useState("");
  const [dashboardMode, setDashboardMode] = useState<DashboardMode>("incidents");
  const [pageSearches, setPageSearches] = useState<Record<string, string>>({});
  const [debouncedCategorySearch, setDebouncedCategorySearch] = useState("");
  const [updateDelayOpen, setUpdateDelayOpen] = useState(false);
  const [refreshClock, setRefreshClock] = useState(Date.now());
  const [setupTrOperation, setSetupTrOperation] = useState("");
  const [systemTargetTab, setSystemTargetTab] = useState<SystemTopTab | null>(null);
  const [systemRefreshSignal, setSystemRefreshSignal] = useState(0);
  const [setupTargetSection, setSetupTargetSection] = useState<string | null>(null);
  const settingsFileInputRef = useRef<HTMLInputElement | null>(null);
  const pageRef = useRef<Page>(page);
  const rangeHoursRef = useRef(rangeHours);
  const lastStatusRefreshAtRef = useRef(0);
  const lastSummaryRefreshAtRef = useRef(0);
  const lastPageRefreshAtRef = useRef(0);
  const lastSetupStatusRefreshAtRef = useRef(0);
  const setupStatusRef = useRef<SetupStatus | null>(null);
  const setupWaterfallActiveRef = useRef(false);
  const playedAudioRef = useRef<Set<string>>(new Set());
  const activeAudioRef = useRef<Set<HTMLAudioElement>>(new Set());
  const knownIncidentIdsRef = useRef<Set<number>>(new Set());
  const lastAutoplayAtRef = useRef(0);
  const alertSettingsRef = useRef<any>(null);
  const autoplayMutedRef = useRef(autoplayMuted);
  const pendingAuthPromptRef = useRef<Promise<string | null> | null>(null);
  const [authPrompt, setAuthPrompt] = useState<AuthPromptState | null>(null);
  useEffect(() => {
    let stopped = false;
    async function checkAppVersion() {
      try {
        const response = await fetch("/api/v1/app-version", { cache: "no-store" });
        if (!response.ok) return;
        const body = await response.json() as { version?: string };
        const version = String(body.version || "");
        if (!version || stopped) return;
        const key = "pizzawave-app-version";
        const current = sessionStorage.getItem(key);
        if (!current) {
          sessionStorage.setItem(key, version);
          return;
        }
        if (current !== version) {
          sessionStorage.setItem(key, version);
          window.location.reload();
        }
      } catch {
        // Best effort only. Normal API errors should not interrupt live monitoring.
      }
    }
    void checkAppVersion();
    const handle = window.setInterval(checkAppVersion, 30000);
    return () => {
      stopped = true;
      window.clearInterval(handle);
    };
  }, []);
  function setPage(next: Page) {
    setPageState(next);
    localStorage.setItem("pizzawave-page", next);
  }

  const fetchSettingsSections = useCallback(async () => {
    const [engine, transcription, aiInsights, embeddings, tr, alerts, auth] = await Promise.all([
      api.request<any>("/api/v1/settings/engine"),
      api.request<any>("/api/v1/settings/transcription"),
      api.request<any>("/api/v1/settings/ai-insights"),
      api.request<any>("/api/v1/settings/embeddings"),
      api.request<any>("/api/v1/settings/tr"),
      api.request<any>("/api/v1/settings/alerts"),
      api.request<any>("/api/v1/settings/auth")
    ]);
    return {
      engine: engine.values,
      transcription: transcription.values,
      "ai-insights": aiInsights.values,
      embeddings: embeddings.values,
      tr: tr.values,
      alerts: alerts.values,
      auth: auth.values
    };
  }, []);

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
      const imported = settingsSectionsFromFile(parsed);
      setSettingsSections(imported);
      settingsResource.setData(imported);
      setSettingsLoadState(current => ({ loading: false, version: current.version + 1, message: `Loaded ${file.name}. Save each changed section to apply it.`, error: false }));
    } catch (error) {
      setSettingsLoadState(current => ({ loading: false, version: current.version, message: error instanceof Error ? error.message : "Settings file load failed", error: true }));
    } finally {
      if (settingsFileInputRef.current)
        settingsFileInputRef.current.value = "";
    }
  }

  const refreshMonitoringData = useCallback(async () => {
    if (pageRef.current === "setup" && setupWaterfallActiveRef.current)
      return;
    let setup = setupStatusRef.current;
    const now = Date.now();
    const shouldRefreshSetupStatus =
      !setup ||
      !setup.completed ||
      now - lastSetupStatusRefreshAtRef.current >= 60_000;
    if (shouldRefreshSetupStatus) {
      setup = await api.request<SetupStatus>("/api/v1/setup/status");
      setupStatusRef.current = setup;
      lastSetupStatusRefreshAtRef.current = now;
      setSetupStatus(setup);
    }
    if (!setup)
      throw new Error("Setup status unavailable.");
    const healthStatus = await api.request<EngineHealth>("/api/v1/health");
    setEngineHealth(healthStatus);
    setMonitoringCheckedAt(Date.now());
  }, []);

  const refreshSummaryData = useCallback(async () => {
    if (!setupStatus?.completed)
      return;

    const [jobRows, summary, profiles, alertRows, alertConfig, cpu, recommendations] = await Promise.all([
      api.request<Job[]>("/api/v1/jobs"),
      api.request<StatusSummary>(`/api/v1/status?${rangeQuery(rangeHours)}`),
      api.request<ProfileState>("/api/v1/profiles"),
      api.request<AlertMatch[]>(`/api/v1/alerts?${rangeQuery(rangeHours)}`),
      api.request<any>("/api/v1/settings/alerts"),
      api.request<SystemCpuSnapshot>("/api/v1/system/cpu").catch(() => null),
      api.request<SystemRecommendationSummary>("/api/v1/system/recommendations/summary").catch(() => null)
    ]);
    setCpuSnapshot(cpu);
    setJobs(jobRows);
    setStatusSummary(summary);
    setProfileState(profiles);
    setAlertSettings(alertConfig.values ?? alertConfig);
    setActiveAlertCount(alertRows.filter(alert => alert.active !== false).length);
    if (recommendations)
      setSystemProblemCount(recommendations.problemCount);
    const latestActiveAlert = alertRows.find(alert => alert.active !== false && !alert.notificationSuppressed);
    if (latestActiveAlert && autoplayAllows(alertConfig.values ?? alertConfig, "alert"))
      playCallAudio(latestActiveAlert.callId, "alert", undefined, alertPlaybackLabel(latestActiveAlert));
  }, [rangeHours, setupStatus?.completed]);
  const currentSearch = pageSearches[page] ?? "";
  useEffect(() => {
    if (!categories.includes(page as any)) return;
    const handle = window.setTimeout(() => setDebouncedCategorySearch(currentSearch.trim()), 300);
    return () => window.clearTimeout(handle);
  }, [currentSearch, page]);

  const statusResource = usePersistentRefresh({
    key: "monitoring-status",
    enabled: true,
    load: async () => {
      lastStatusRefreshAtRef.current = Date.now();
      await refreshMonitoringData();
      return true;
    }
  });
  const summaryResource = usePersistentRefresh({
    key: `operational-summary|${rangeHours}`,
    enabled: setupStatus?.completed === true,
    load: async () => {
      lastSummaryRefreshAtRef.current = Date.now();
      await refreshSummaryData();
      return true;
    }
  });
  const refreshSharedStatus = useCallback(async () => {
    await Promise.all([statusResource.refresh(), summaryResource.refresh()]);
  }, [statusResource.refresh, summaryResource.refresh]);
  const dashboardResource = usePersistentRefresh({
    key: `dashboard|${rangeHours}`,
    enabled: page === "dashboard" && setupStatus?.completed !== false,
    load: () => api.request<Dashboard>(`/api/v1/dashboard?${rangeQuery(rangeHours)}`),
    onSuccess: maybeAutoplayDashboard
  });
  const categoryResource = usePersistentRefresh({
    key: categoryPageKey(page, rangeHours, debouncedCategorySearch),
    enabled: categories.includes(page as any) && setupStatus?.completed !== false,
    load: async () => {
      const search = debouncedCategorySearch.trim();
      const data = await api.request<CategoryPage>(`/api/v1/categories/${page}?${rangeQuery(rangeHours)}${search ? `&q=${encodeURIComponent(search)}` : ""}`);
      return { page, search, data };
    }
  });
  const setupResource = usePersistentRefresh({
    key: "site-setup",
    enabled: page === "setup" && setupStatus?.completed !== false && !setupTrOperation,
    load: () => api.request<SiteSetup>(siteSetupApi)
  });
  const settingsResource = usePersistentRefresh({
    key: "settings",
    enabled: page === "settings" && setupStatus?.completed !== false && !settingsDirty,
    load: fetchSettingsSections,
    onSuccess: sections => {
      setSettingsSections(sections);
      setSettingsLoadState(current => ({ loading: false, version: current.version + 1, message: "Settings loaded from server", error: false }));
    }
  });
  const dashboard = dashboardResource.data;
  const categoryResult = categoryResource.data;
  const category = categoryResult?.page === page ? categoryResult.data : null;
  const siteSetup = setupResource.data;

  const load = useCallback(async () => {
    lastPageRefreshAtRef.current = Date.now();
    if (pageRef.current === "dashboard") await dashboardResource.refresh();
    else if (categories.includes(pageRef.current as any)) await categoryResource.refresh();
    else if (pageRef.current === "setup" && !setupWaterfallActiveRef.current) await setupResource.refresh();
    else if (pageRef.current === "system") setSystemRefreshSignal(current => current + 1);
    else if (pageRef.current === "settings" && !settingsDirty) await settingsResource.refresh();
  }, [categoryResource.refresh, dashboardResource.refresh, settingsDirty, settingsResource.refresh, setupResource.refresh]);

  useEffect(() => { pageRef.current = page; }, [page]);
  useEffect(() => { rangeHoursRef.current = rangeHours; }, [rangeHours]);
  useEffect(() => { setupStatusRef.current = setupStatus; }, [setupStatus]);
  useEffect(() => {
    api.setAuthTokenProvider(request => {
      if (pendingAuthPromptRef.current)
        return pendingAuthPromptRef.current;
      const promise = new Promise<string | null>(resolve => {
        setAuthPrompt({
          request,
          resolve,
          token: "",
          message: "Enter the PizzaWave admin token for this rig."
        });
      }).finally(() => {
        pendingAuthPromptRef.current = null;
      });
      pendingAuthPromptRef.current = promise;
      return promise;
    });
    return () => api.setAuthTokenProvider(null);
  }, []);
  useEffect(() => {
    const site = (engineHealth?.stackName || setupStatus?.values?.branding?.stackName || "").trim();
    document.title = site && site !== "PizzaWave" ? `PizzaWave - ${site}` : "PizzaWave";
  }, [engineHealth?.stackName, setupStatus?.values?.branding?.stackName]);
  useEffect(() => {
    const applyHash = () => {
      const target = parseCardHash();
      setFocusedHashTarget(target.raw);
      if (!target.raw) {
        return;
      }
      if (pageRef.current === "settings" && !confirmDiscardUnappliedSettings()) return;
      setPage("dashboard");
      setFocusedIncidentId(target.incidentId);
    };
    applyHash();
    window.addEventListener("hashchange", applyHash);
    return () => window.removeEventListener("hashchange", applyHash);
  }, []);
  useEffect(() => { alertSettingsRef.current = alertSettings; }, [alertSettings]);
  useEffect(() => {
    autoplayMutedRef.current = autoplayMuted;
    localStorage.setItem("pizzawave-autoplay-muted", autoplayMuted ? "1" : "0");
  }, [autoplayMuted]);

  useEffect(() => {
    const handle = window.setInterval(() => setRefreshClock(Date.now()), 30_000);
    return () => window.clearInterval(handle);
  }, []);
  useEffect(() => {
    document.documentElement.dataset.theme = theme;
    localStorage.setItem("pizzawave-theme", theme);
  }, [theme]);

  useEffect(() => {
    const events = new EventSource("/api/v1/events/stream");
    let connectedOnce = false;
    let statusTimer = 0;
    let summaryTimer = 0;
    let pageTimer = 0;
    const scheduleStatus = (delayMs: number) => {
      if (pageRef.current === "setup" && setupWaterfallActiveRef.current)
        return;
      window.clearTimeout(statusTimer);
      const elapsed = Date.now() - lastStatusRefreshAtRef.current;
      delayMs = Math.max(delayMs, elapsed >= 5_000 ? 0 : 5_000 - elapsed);
      statusTimer = window.setTimeout(() => {
        void statusResource.refresh();
      }, delayMs);
    };
    const scheduleSummary = (delayMs: number) => {
      window.clearTimeout(summaryTimer);
      const elapsed = Date.now() - lastSummaryRefreshAtRef.current;
      delayMs = Math.max(delayMs, elapsed >= 30_000 ? 0 : 30_000 - elapsed);
      summaryTimer = window.setTimeout(() => {
        void summaryResource.refresh();
      }, delayMs);
    };
    const schedulePage = (delayMs: number) => {
      if (pageRef.current === "setup" && setupWaterfallActiveRef.current)
        return;
      window.clearTimeout(pageTimer);
      const minIntervalMs = pageRef.current === "dashboard" ? 30_000 : 5_000;
      const elapsed = Date.now() - lastPageRefreshAtRef.current;
      delayMs = Math.max(delayMs, elapsed >= minIntervalMs ? 0 : minIntervalMs - elapsed);
      pageTimer = window.setTimeout(() => {
        void load();
      }, delayMs);
    };
    const refreshCallDataSoon = () => {
      scheduleStatus(900);
      scheduleSummary(3000);
      if (pageRef.current === "dashboard" || categories.includes(pageRef.current as any))
        schedulePage(3000);
    };
    const refreshDashboardData = () => {
      scheduleStatus(500);
      scheduleSummary(900);
      if (pageRef.current === "dashboard")
        schedulePage(900);
    };
    events.addEventListener("connected", () => {
      if (connectedOnce) {
        scheduleStatus(0);
        scheduleSummary(0);
        if (pageRef.current !== "settings")
          schedulePage(0);
      }
      connectedOnce = true;
    });
    events.addEventListener("call_ingested", refreshCallDataSoon);
    events.addEventListener("call_transcribed", event => {
      refreshCallDataSoon();
      try {
        const payload = JSON.parse((event as MessageEvent).data || "{}");
        if (payload.callId && !payload.notificationSuppressed && autoplayAllows(alertSettingsRef.current, "police"))
          void api.request<EngineCall>(`/api/v1/calls/${payload.callId}`).then(call => {
            if (call.category === "police")
              playCallAudio(call.id, "police", undefined, callPlaybackLabel(call));
          }).catch(() => { });
      } catch { }
    });
    events.addEventListener("alert_matched", refreshDashboardData);
    events.addEventListener("summary_updated", refreshDashboardData);
    events.addEventListener("job_updated", () => { scheduleStatus(900); scheduleSummary(900); });
    events.addEventListener("health_updated", () => scheduleStatus(900));
    return () => {
      window.clearTimeout(statusTimer);
      window.clearTimeout(summaryTimer);
      window.clearTimeout(pageTimer);
      events.close();
    };
  }, [load, statusResource.refresh, summaryResource.refresh]);
  const nav = useMemo(() => ["dashboard", ...categories, "setup", "system", "settings"] as Page[], []);
  const activeProfile = profileState?.profiles.find(p => p.id === profileState.activeProfileId);
  const visibleNav = nav.filter(item => !categories.includes(item as any) || profileIncludes(activeProfile, item));
  const activeJobCount = jobs.filter(j => j.status === "running" || j.status === "queued" || j.status === "paused").length;
  const trCoveragePausedByJob = jobs.some(j => j.status === "running" && j.type === "setup_tr_calibration_sweep");
  const trCoveragePaused = trCoveragePausedByJob || Boolean(setupTrOperation);
  const queueDepth = engineHealth?.queueDepth ?? 0;
  const aiCompletionIssue = engineHealth?.aiCompletionHealth && !["ok", "unknown"].includes(engineHealth.aiCompletionHealth.status) ? engineHealth.aiCompletionHealth.message : "";
  const embeddingIssue = engineHealth?.embeddingHealth?.enabled && !["ok", "disabled", "unknown"].includes(engineHealth.embeddingHealth.status)
    ? (engineHealth.embeddingHealth.lastError || (engineHealth.embeddingHealth.embeddingEndpointOk ? "Embedding pipeline health is degraded." : "Embedding endpoint health check failed."))
    : "";
  const queueBlockedNotes = [engineHealth?.aiWorkBlockedReason, aiCompletionIssue, embeddingIssue].filter(Boolean);
  const queueHealth = aiCompletionIssue || embeddingIssue ? "blocked" : queueDepth <= 0 ? "clear" : engineHealth?.queueUnderPressure ? "pressure" : "draining";
  const audioTranscribedPerMinute = engineHealth?.recentAudioSecondsTranscribedPerMinute ?? 0;
  const audioIngestedPerMinute = engineHealth?.recentAudioSecondsIngestedPerMinute ?? 0;
  const ingestPaused = Boolean(engineHealth?.ingest?.paused);
  const liveTrActivity = engineHealth?.liveTrActivity;
  const trIntentionallyStopped = liveTrActivity?.status === "stopped";
  const monitoringOutdated = Boolean(statusResource.state.error && monitoringCheckedAt && refreshClock - monitoringCheckedAt >= 90_000);
  const monitoringTone = monitoringOutdated
    ? "live-status-warning"
    : trCoveragePaused
      ? "ingest-paused"
      : liveTrActivity?.status === "fault" || liveTrActivity?.stale
        ? "live-status-error"
        : trIntentionallyStopped || liveTrActivity?.status === "warming" || !liveTrActivity
          ? "live-status-warning"
          : "live-status-ok";
  const livePillClass = [
    "pill",
    monitoringTone
  ].filter(Boolean).join(" ");
  const livePillText = monitoringOutdated
    ? `Monitoring last checked ${formatRefreshTime(monitoringCheckedAt)}`
    : trCoveragePaused
      ? "Monitoring temporarily paused"
      : liveTrActivity?.status === "fault"
        ? "Monitoring faulted"
        : liveTrActivity?.stale
          ? "Monitoring stale"
          : trIntentionallyStopped
            ? "Monitoring intentionally stopped"
            : liveTrActivity?.status === "warming" || !liveTrActivity
              ? "Monitoring warming up"
              : "Monitoring active";
  const livePillTitle = `${trCoveragePaused
    ? setupTrOperation || "trunk-recorder is temporarily paused or restarting while Setup runs an RF validation job."
    : liveTrActivity?.message || "Waiting for the first monitoring health result."} Last checked: ${formatRefreshTime(monitoringCheckedAt)}.`;
  const cpuPillClass = ["pill", "status-pill-button", `cpu-health-${cpuSnapshot?.severity ?? "unknown"}`].join(" ");
  const cpuPillText = cpuSnapshot?.latest
    ? `CPU ${cpuSnapshot.hostCpuPercent?.toFixed(0) ?? "--"}% / ${cpuSnapshot.latest.hostTempC.toFixed(0)}C`
    : "CPU --";
  const cpuPillTitle = cpuSnapshot?.latest
    ? `Host CPU ${cpuSnapshot.hostCpuPercent?.toFixed(0) ?? "--"}%, load ${cpuSnapshot.latest.hostLoad1.toFixed(2)}, temperature ${cpuSnapshot.latest.hostTempC.toFixed(1)} C.`
    : "No recent host resource sample.";
  const queueHealthText = queueHealth === "blocked"
    ? `Queue blocked ${queueDepth.toLocaleString()}`
    : queueHealth === "clear"
      ? `Queue OK ${queueDepth.toLocaleString()}`
      : queueHealth === "pressure"
        ? `Queue pressure ${queueDepth.toLocaleString()}`
        : `Queue draining ${queueDepth.toLocaleString()}`;
  const queueHealthTitle = engineHealth
    ? `${engineHealth.recentAudioSecondsTranscribed.toLocaleString()} audio seconds transcribed (${audioTranscribedPerMinute.toFixed(0)}s/min) and ${engineHealth.recentAudioSecondsIngested.toLocaleString()} audio seconds ingested (${audioIngestedPerMinute.toFixed(0)}s/min) in the last ${engineHealth.throughputWindowMinutes} minutes. Calls: ${engineHealth.recentCallsTranscribed.toLocaleString()} done (${engineHealth.recentTranscribedPerMinute.toFixed(1)}/min), ${engineHealth.recentCallsIngested.toLocaleString()} in (${engineHealth.recentIngestPerMinute.toFixed(1)}/min). Local workers: ${engineHealth.liveTranscriptionWorkers} x ${engineHealth.whisperThreadsPerWorker} thread(s). ${queueBlockedNotes.join(" ")}`.trim()
    : "Transcription queue is clear.";
  const updateLiveCpuSnapshot = useCallback((sample: SystemRuntimeResourceSample) => {
    setCpuSnapshot(current => current ? {
      ...current,
      hostCpuPercent: sample.hostCpuPercent,
      hostMemory: sample.hostMemory,
      processes: sample.processes
    } : current);
  }, []);

  const activePageRefresh = page === "dashboard"
    ? { label: "Dashboard", state: dashboardResource.state }
    : categories.includes(page as any)
      ? { label: `${label(page)} calls`, state: categoryResource.state }
      : page === "setup"
        ? { label: "Setup", state: setupResource.state }
        : null;
  const delayedResources = [
    { label: "Shared operational status", state: statusResource.state },
    activePageRefresh
  ].filter((entry): entry is { label: string; state: RefreshState } => Boolean(
    entry && entry.state.failureStartedAt && refreshClock - entry.state.failureStartedAt >= 90_000
  ));
  const longestDelayStartedAt = delayedResources.reduce<number | null>((oldest, entry) => {
    const started = entry.state.failureStartedAt;
    return started && (!oldest || started < oldest) ? started : oldest;
  }, null);
  const updateDelayMinutes = longestDelayStartedAt ? Math.max(1, Math.floor((refreshClock - longestDelayStartedAt) / 60_000)) : 0;

  const inSetup = Boolean(setupStatus && !setupStatus.completed);
  function autoplayAllows(config: any, kind: "alert" | "incident" | "traffic" | "police") {
    const playback = config?.playback ?? {};
    if (autoplayMutedRef.current || !playback.enabled) return false;
    if (kind === "alert") return playback.alertMatches !== false;
    if (kind === "incident") return !!playback.newIncidents;
    if (kind === "traffic") return !!playback.trafficIncidents;
    if (kind === "police") return !!playback.policeCalls;
    return false;
  }
  function canAutoplayNow() {
    const cooldown = Math.max(1, Number(alertSettingsRef.current?.playback?.cooldownSeconds ?? 15)) * 1000;
    const now = Date.now();
    if (now - lastAutoplayAtRef.current < cooldown)
      return false;
    lastAutoplayAtRef.current = now;
    return true;
  }
  function playCallAudio(callId: number, reason: string, incidentId?: number, labelText?: string) {
    const key = `${reason}:${callId}`;
    if (playedAudioRef.current.has(key) || !canAutoplayNow())
      return;
    playedAudioRef.current.add(key);
    const audio = new Audio(`/api/v1/calls/${callId}/audio`);
    audio.preload = "auto";
    activeAudioRef.current.add(audio);
    setActiveAutoplay({ key, kind: autoplayKind(reason), callId, incidentId, label: labelText?.trim() || `Call ${callId}` });
    const clearActive = () => setActiveAutoplay(current => current?.key === key ? null : current);
    const repeatCount = Math.max(1, Math.min(10, Number(alertSettingsRef.current?.playback?.repeatCount ?? 1)));
    let played = 1;
    audio.addEventListener("ended", () => {
      if (played < repeatCount && !autoplayMutedRef.current) {
        played += 1;
        audio.currentTime = 0;
        void audio.play().catch(() => {
          activeAudioRef.current.delete(audio);
          clearActive();
        });
        return;
      }
      activeAudioRef.current.delete(audio);
      clearActive();
    });
    audio.addEventListener("error", () => {
      activeAudioRef.current.delete(audio);
      clearActive();
    });
    void audio.play().catch(() => {
      activeAudioRef.current.delete(audio);
      clearActive();
    });
  }
function autoplayKind(reason: string): AutoplayContext["kind"] {
    if (reason === "alert") return "alert";
    if (reason === "incident") return "incident";
    if (reason === "traffic-incident") return "traffic";
    return "police";
  }
  function stopAutoplayAudio() {
    for (const audio of activeAudioRef.current) {
      audio.pause();
      audio.currentTime = 0;
    }
    activeAudioRef.current.clear();
    setActiveAutoplay(null);
  }
  async function openAutoplayTarget(target: "alert" | "incident") {
    setAutoplayMenuOpen(false);
    if (page === "settings" && !confirmDiscardUnappliedSettings()) return;
    const context = activeAutoplay;
    setPage("dashboard");
    try {
      const nextDashboard = await api.request<Dashboard>(`/api/v1/dashboard?${rangeQuery(rangeHours)}`);
      dashboardResource.setData(nextDashboard);
      const incidentId = context?.incidentId
        ?? nextDashboard.incidents.find(incident => incident.calls.some(call => call.callId === context?.callId))?.id
        ?? null;
      if (incidentId) {
        setFocusedIncidentId(incidentId);
        window.setTimeout(() => document.getElementById(`incident-${incidentId}`)?.scrollIntoView({ block: "center", behavior: "smooth" }), 50);
      } else if (target === "incident") {
        setAppNotice("Incident not visible yet");
      }
    } catch (error) {
      setAppNotice(error instanceof Error ? error.message : "Dashboard refresh failed");
    }
  }
  function maybeAutoplayDashboard(nextDashboard: Dashboard) {
    const known = knownIncidentIdsRef.current;
    for (const incident of nextDashboard.incidents ?? []) {
      const firstCall = incident.calls?.[0];
      if (!firstCall) continue;
      if (!known.has(incident.id)) {
        if (known.size > 0) {
          const recentEnoughForNotification = Date.now() - incident.lastSeen * 1000 <= 60 * 60 * 1000;
          if (recentEnoughForNotification && autoplayAllows(alertSettingsRef.current, "incident"))
            playCallAudio(firstCall.callId, "incident", incident.id, incident.title);
          else if (recentEnoughForNotification && incident.category === "traffic" && autoplayAllows(alertSettingsRef.current, "traffic"))
            playCallAudio(firstCall.callId, "traffic-incident", incident.id, incident.title);
        }
        known.add(incident.id);
      }
    }
  }
  async function switchActiveProfile(profileId: string) {
    if (!profileState || profileId === profileState.activeProfileId) return;
    if (!confirmDiscardUnappliedSettings()) return;
    const next = { ...profileState, activeProfileId: profileId };
    setProfileState(next);
    try {
      const saved = await api.request<ProfileState>("/api/v1/profiles/active", { method: "POST", body: JSON.stringify({ activeProfileId: profileId }) });
      setProfileState(saved);
      await load();
    } catch (error) {
      setProfileState(profileState);
      setAppNotice(error instanceof Error ? error.message : "Unable to switch profile.");
    }
  }
  function goDashboard(mode: DashboardMode) {
    if (page === "settings" && !confirmDiscardUnappliedSettings()) return;
    setDashboardMode(mode);
    setPage("dashboard");
  }
  function goSystem(tab: SystemTopTab) {
    if (page === "settings" && !confirmDiscardUnappliedSettings()) return;
    setSystemTargetTab(tab);
    setPage("system");
  }
  function goSetup(section?: string) {
    if (page === "settings" && !confirmDiscardUnappliedSettings()) return;
    setSetupTargetSection(section ?? null);
    setPage("setup");
  }
  function submitAuthToken(event?: React.FormEvent) {
    event?.preventDefault();
    if (!authPrompt) return;
    const token = authPrompt.token.trim();
    if (!token) {
      setAuthPrompt(current => current ? { ...current, message: "Token is required for this action." } : current);
      return;
    }
    const resolve = authPrompt.resolve;
    setAuthPrompt(null);
    resolve(token);
  }
  function cancelAuthToken() {
    if (!authPrompt) return;
    const resolve = authPrompt.resolve;
    setAuthPrompt(null);
    resolve(null);
  }

  return (
    <div className={`app ${inSetup ? "setup-mode" : ""}`}>
      {authPrompt && <form className="modal-backdrop auth-token-backdrop" onSubmit={submitAuthToken}>
        <div className="card auth-token-dialog">
          <h3>Admin Token Required</h3>
          <p className="muted">This action needs the PizzaWave admin token. On the rig, run: <code>sudo cat /etc/pizzawave/pizzad.token</code></p>
          <p className="muted">Request: {authPrompt.request.method} {authPrompt.request.path}</p>
          <input
            autoFocus
            type="password"
            value={authPrompt.token}
            aria-label="PizzaWave admin token"
            onChange={event => setAuthPrompt(current => current ? { ...current, token: event.target.value, message: "" } : current)}
          />
          {authPrompt.message && <p className="settings-message">{authPrompt.message}</p>}
          <div className="settings-card-actions">
            <button type="submit">Use Token</button>
            <button type="button" onClick={cancelAuthToken}>Cancel</button>
          </div>
        </div>
      </form>}
      <header className="topbar">
        <div className="brand">
          <img src="/logo-small.png" alt="" />
          <span className="brand-text"><strong>PizzaWave</strong><small>{engineHealth?.stackName ?? "PizzaWave"}</small></span>
        </div>
        <select aria-label="Call data window" title="Call, incident, and related dashboard window" value={rangeHours} onChange={e => setRangeHours(Number(e.target.value))}>
          <option value={24}>24h</option>
          <option value={48}>2d</option>
          <option value={168}>Week</option>
        </select>
        <select aria-label="Color scheme" value={theme} onChange={e => setTheme(normalizeTheme(e.target.value))}>
          {themeOptions.map(option => <option value={option.value} key={option.value}>{option.label}</option>)}
        </select>
        {profileState && profileState.profiles.length > 0 && <select aria-label="Active profile" value={profileState.activeProfileId} onChange={e => void switchActiveProfile(e.target.value)}>
          {profileState.profiles.map(profile => <option value={profile.id} key={profile.id}>{profile.name}</option>)}
        </select>}
        {page === "settings" && <>
          <input ref={settingsFileInputRef} type="file" accept="application/json,.json" hidden onChange={e => void loadSettingsFile(e.target.files?.[0])} />
          <button disabled={settingsLoadState.loading} onClick={() => settingsFileInputRef.current?.click()}>{settingsLoadState.loading ? "Loading..." : "Load Settings"}</button>
          <button disabled={settingsLoadState.loading} onClick={() => void exportSettingsFile()}>Export Settings</button>
        </>}
        <button type="button" className={`${livePillClass} status-pill-button`} title={livePillTitle} onClick={() => goSystem("services")}>{livePillText}</button>
        {delayedResources.length > 0 && <div className="autoplay-menu-wrap update-delay-wrap">
          <button type="button" className="pill live-status-warning status-pill-button" onClick={() => setUpdateDelayOpen(value => !value)}>Updates delayed · {updateDelayMinutes}m</button>
          {updateDelayOpen && <div className="autoplay-menu update-delay-menu">
            {delayedResources.map(entry => <div key={entry.label}>
              <strong>{entry.label}</strong>
              <span>{entry.state.error || "Refresh has not completed."}</span>
              <small>Last updated {formatRefreshTime(entry.state.lastUpdatedAt)}</small>
            </div>)}
          </div>}
        </div>}
        {appNotice && <button type="button" className="pill live-status-warning status-pill-button" title="Dismiss" onClick={() => setAppNotice("")}>{appNotice}</button>}
        {activeAutoplay && <span className="pill playback-status" title={activeAutoplay.label}>Playing "{activeAutoplay.label}"</span>}
        {alertSettings?.playback?.enabled && <div className="autoplay-menu-wrap">
          <button type="button" className={autoplayMuted ? "icon-button muted-button" : "icon-button"} title={autoplayMuted ? "Autoplay muted or blocked. Open playback menu." : "Autoplay enabled. Open playback menu."} onClick={() => setAutoplayMenuOpen(v => !v)}>{autoplayMuted ? <BellOff size={16} /> : <Bell size={16} />}</button>
          {autoplayMenuOpen && <div className="autoplay-menu">
            {activeAutoplay?.kind === "alert" && <button type="button" onClick={() => void openAutoplayTarget("alert")}>Go to alert</button>}
            {(activeAutoplay?.kind === "incident" || activeAutoplay?.kind === "traffic") && <button type="button" onClick={() => void openAutoplayTarget("incident")}>Go to incident</button>}
            {activeAutoplay && <button type="button" onClick={() => { stopAutoplayAudio(); setAutoplayMenuOpen(false); }}>Stop playback</button>}
            <button type="button" onClick={() => { if (!autoplayMuted) stopAutoplayAudio(); setAutoplayMuted(!autoplayMuted); setAutoplayMenuOpen(false); }}>{autoplayMuted ? "Unmute" : "Mute all"}</button>
          </div>}
        </div>}
        {ingestPaused && <span className="pill ingest-paused" title={`New live callstream payloads are being dropped. ${engineHealth?.ingest?.reason ?? ""}`}>Ingest paused</span>}
      </header>
      {!inSetup && <aside className="nav">
        {visibleNav.map(item => (
          <React.Fragment key={item}>
            {item === "setup" && <div className="nav-divider" />}
            <button className={item === page ? "active" : ""} onClick={() => {
              if (page === "settings" && item !== "settings" && !confirmDiscardUnappliedSettings()) return;
              setPage(item);
            }}>
              {navIcon(item)} {label(item)}
              {item === "dashboard" && activeAlertCount > 0 && <span className="nav-badge high">{activeAlertCount}</span>}
              {item === "setup" && (siteSetup?.pendingChanges.length ?? 0) > 0 && <span className="nav-badge medium">{siteSetup?.pendingChanges.length}</span>}
              {item === "system" && systemProblemCount > 0 && <span className="nav-badge problem" aria-label={`${systemProblemCount} System problem${systemProblemCount === 1 ? "" : "s"}`}>{systemProblemCount}</span>}
            </button>
          </React.Fragment>
        ))}
      </aside>}
      <main className={`main ${inSetup ? "setup-main" : ""}`}>
        {!setupStatus && <RefreshNotice state={statusResource.state} hasData={false} onRetry={statusResource.refresh} />}
        {inSetup && setupStatus && <SetupWizard status={setupStatus} reload={load} onComplete={() => setPage("setup")} />}
        {setupStatus?.completed && page === "dashboard" && <div className="refresh-page-shell">
          <RefreshNotice state={dashboardResource.state} hasData={Boolean(dashboard)} onRetry={dashboardResource.refresh} />
          <DashboardView data={dashboard} rangeHours={rangeHours} reload={load} focusedIncidentId={focusedIncidentId} focusedHashTarget={focusedHashTarget} clearFocusedIncident={() => setFocusedIncidentId(null)} clearFocusedHashTarget={() => setFocusedHashTarget("")} mode={dashboardMode} setMode={setDashboardMode} searchQuery={currentSearch} onSearchChange={value => setPageSearches(searches => ({ ...searches, [page]: value }))} hiddenIncidentCount={statusSummary?.hiddenIncidents ?? 0} />
        </div>}
        {setupStatus?.completed && categories.includes(page as any) && <div className="refresh-page-shell">
          <RefreshNotice state={categoryResource.state} hasData={Boolean(category)} onRetry={categoryResource.refresh} />
          <CategoryView
          data={category}
          rangeHours={rangeHours}
          searchQuery={currentSearch}
          onSearchChange={value => setPageSearches(searches => ({ ...searches, [page]: value }))}
          profileState={profileState}
          setProfileState={setProfileState}
          reload={load}
          onOpenProfiles={(settings) => {
            if (settings?.length) {
              setPendingProfileHides(current => mergeProfileTalkgroupSettings(current, settings));
            }
            localStorage.setItem("pizzawave-settings-tab", "profiles");
            setPage("settings");
          }}
        /></div>}
        {setupStatus?.completed && page === "setup" && <div className="refresh-page-shell">
          <RefreshNotice state={setupResource.state} hasData={Boolean(siteSetup)} onRetry={setupResource.refresh} />
          <SiteSetupView setup={siteSetup} reload={load} targetSection={setupTargetSection} clearTargetSection={() => setSetupTargetSection(null)} onTrOperationChange={value => {
          setupWaterfallActiveRef.current = value.toLowerCase().includes("waterfall");
          setSetupTrOperation(value);
          if (!value)
            void refreshSharedStatus();
        }} /></div>}
        {setupStatus?.completed && page === "system" && <div className="refresh-page-shell">
          <SystemView rangeHours={rangeHours} engineHealth={engineHealth} refreshSharedStatus={refreshSharedStatus} refreshSignal={systemRefreshSignal} targetTab={systemTargetTab} clearTargetTab={() => setSystemTargetTab(null)} onLiveResources={updateLiveCpuSnapshot} onOpenSetup={goSetup} onOpenIncident={incidentId => {
            setFocusedIncidentId(incidentId);
            setFocusedHashTarget(`incident-${incidentId}`);
            setDashboardMode("incidents");
            setPage("dashboard");
          }} onOpenTalkgroup={row => {
            const category = categories.includes(row.category as any) ? row.category as typeof categories[number] : "other";
            setPageSearches(searches => ({ ...searches, [category]: String(row.talkgroup) }));
            setPage(category);
          }} />
        </div>}
        {setupStatus?.completed && page === "settings" && <div className="refresh-page-shell">
          <SettingsLoadNotice state={settingsResource.state} hasData={Object.keys(settingsSections).length > 0} dirty={settingsDirty} onReload={settingsResource.refresh} />
          {Object.keys(settingsSections).length > 0 && <SettingsView settingsSections={settingsSections} settingsLoadState={settingsLoadState} reload={refreshSharedStatus} pendingProfileHides={pendingProfileHides} setPendingProfileHides={setPendingProfileHides} onDirtyChange={setSettingsDirty} />}
        </div>}
      </main>
      {!inSetup && <footer className="statusbar">
        <span className="pill" title="Calls in the selected call-data window">Calls {statusSummary?.calls?.toLocaleString() ?? "--"}</span>
        <button type="button" className="pill status-pill-button" title="Open incidents" onClick={() => goDashboard("incidents")}>Incidents {statusSummary?.incidents?.toLocaleString() ?? "--"}</button>
        <button type="button" className="pill status-pill-button" title="Open alerts" onClick={() => goDashboard("alerts")}>Alerts {statusSummary?.alerts?.toLocaleString() ?? "--"}</button>
        <button type="button" className={`pill status-pill-button queue-health queue-${queueHealth}`} title={queueHealthTitle} onClick={() => goSystem("queue")}>{queueHealthText}</button>
        <button type="button" className={cpuPillClass} title={cpuPillTitle} onClick={() => goSystem("services")}>{cpuPillText}</button>
        {activeJobCount > 0 && <button type="button" className="pill status-pill-button" title="Open active jobs" onClick={() => goSystem("jobs")}>Jobs {activeJobCount}</button>}
      </footer>}
    </div>
  );
}

type SiteSetupRfStage = "preparation" | "spectrum" | "control" | "coverage" | "calls" | "verdict";

const siteSetupRfStages: Array<{ id: SiteSetupRfStage; label: string }> = [
  { id: "preparation", label: "Preparation" },
  { id: "spectrum", label: "Spectrum Inspection" },
  { id: "control", label: "Control-Channel Proof" },
  { id: "coverage", label: "Source Coverage" },
  { id: "calls", label: "Call & Transcription Proof" },
  { id: "verdict", label: "Verdict" }
];

function storedSiteSetupRfStage(): SiteSetupRfStage {
  const saved = localStorage.getItem("pizzawave-site-setup-rf-validation-subpage");
  if (saved === "sweep") return "control";
  if (saved === "waterfall") return "spectrum";
  return siteSetupRfStages.some(stage => stage.id === saved) ? saved as SiteSetupRfStage : "preparation";
}

function SiteSetupView({ setup, reload, targetSection, clearTargetSection, onTrOperationChange }: { setup: SiteSetup | null; reload: () => Promise<void>; targetSection?: string | null; clearTargetSection?: () => void; onTrOperationChange: (value: string) => void }) {
  const [current, setCurrent] = useState<SiteSetup | null>(setup);
  const currentRef = useRef<SiteSetup | null>(setup);
  const saveQueueRef = useRef<Promise<void>>(Promise.resolve());
  const [saveState, setSaveState] = useState<{ field: string; status: "idle" | "saving" | "saved" | "error"; message: string }>({ field: "", status: "idle", message: "" });
  const [localPendingChanges, setLocalPendingChanges] = useState<SiteSetupPendingChange[]>([]);
  const sections = ["Location", "Systems & Sites", "Talkgroups", "Hardware & RF Path", "RF Validation", "Apply & Resume"];
  const enabledSections = new Set(sections);
  const [section, setSectionState] = useState(() => {
    const saved = localStorage.getItem("pizzawave-site-setup-section") || "Location";
    return enabledSections.has(saved) ? saved : "Location";
  });
  const [rfValidationStage, setRfValidationStageState] = useState<SiteSetupRfStage>(storedSiteSetupRfStage);
  const [applySubPage, setApplySubPageState] = useState<"source" | "review">(() => localStorage.getItem("pizzawave-site-setup-apply-subpage") === "review" ? "review" : "source");
  const setSection = (value: string) => {
    setSectionState(value);
    localStorage.setItem("pizzawave-site-setup-section", value);
  };
  const setRfValidationStage = (value: SiteSetupRfStage) => {
    setRfValidationStageState(value);
    localStorage.setItem("pizzawave-site-setup-rf-validation-subpage", value);
  };
  const setApplySubPage = (value: "source" | "review") => {
    setApplySubPageState(value);
    localStorage.setItem("pizzawave-site-setup-apply-subpage", value);
  };
  useEffect(() => {
    currentRef.current = setup;
    setCurrent(setup);
    setLocalPendingChanges([]);
  }, [setup]);
  useEffect(() => {
    if (!targetSection) return;
    setSection(targetSection);
    if (targetSection === "RF Validation") setRfValidationStage(storedSiteSetupRfStage());
    clearTargetSection?.();
  }, [targetSection, clearTargetSection]);
  if (!current) return null;
  async function saveDesired(patch: Partial<SiteSetupConfig>, field: string) {
    setLocalPendingChanges(field === "rfPath" ? [{ category: "RF path", summary: "RF path edits are being saved." }] : []);
    setSaveState({ field, status: "saving", message: "Saving" });
    const save = async () => {
      const base = currentRef.current;
      if (!base) return;
      try {
        const next = await api.request<SiteSetup>(siteSetupApi, {
          method: "PATCH",
          body: JSON.stringify({ expectedVersion: base.desired.desiredVersion, patch, source: "ui" })
        });
        currentRef.current = next;
        setCurrent(next);
        setLocalPendingChanges([]);
        setSaveState({ field, status: "saved", message: "Saved" });
      } catch (error) {
        let message = error instanceof Error ? error.message : "Save failed";
        if (message.toLowerCase().includes("setup changed after this screen loaded")) {
          try {
            const latest = await api.request<SiteSetup>(siteSetupApi);
            currentRef.current = latest;
            setCurrent(latest);
            message = `${message} Current Setup values have been reloaded.`;
          } catch {
            // Keep the original conflict message when the reload also fails.
          }
        }
        setSaveState({ field, status: "error", message });
      }
    };
    const queued = saveQueueRef.current.catch(() => undefined).then(save);
    saveQueueRef.current = queued;
    await queued;
  }

  return <div className="site-setup-shell">
    <section className="pane site-setup-pane">
      <div className="site-setup-head">
        <div>
          <h2>Site Setup</h2>
        </div>
      </div>
      <SiteSetupChangeStrip setup={current} localPendingChanges={localPendingChanges} />

      <div className="site-setup-layout">
        <section className="site-setup-section-nav" aria-label="Site Setup sections">
          {sections.map((item, index) => <div className="site-setup-nav-group" key={item}>
            <button type="button" className={item === section ? "active" : ""} disabled={!enabledSections.has(item)} onClick={() => setSection(item)}>
              <span>{index + 1}</span>
              <strong>{item}</strong>
            </button>
            {item === "RF Validation" && <div className="site-setup-subnav">
              {siteSetupRfStages.map(stage => <button type="button" className={section === item && rfValidationStage === stage.id ? "active" : ""} key={stage.id} onClick={() => { setSection(item); setRfValidationStage(stage.id); }}>{stage.label}</button>)}
            </div>}
          </div>)}
        </section>

        <section className="site-setup-panel">
          {section === "Location" && <SiteSetupLocationSection setup={current} saveState={saveState} onSave={saveDesired} />}
          {section === "Systems & Sites" && <SiteSetupSystemsSection setup={current} saveState={saveState} onSave={saveDesired} />}
          {section === "Talkgroups" && <SiteSetupTalkgroupsSection setup={current} reload={reload} onSave={saveDesired} />}
          {section === "Hardware & RF Path" && <SiteSetupHardwareSection setup={current} saveState={saveState} onSave={saveDesired} />}
          <div style={section === "RF Validation" ? undefined : { display: "none" }} aria-hidden={section === "RF Validation" ? undefined : "true"}>
            <SiteSetupRfValidationSection
              setup={current}
              active={section === "RF Validation"}
              stage={rfValidationStage}
              onSave={saveDesired}
              onTrOperationChange={onTrOperationChange}
              onServerSetupChanged={(next) => { currentRef.current = next; setCurrent(next); }}
            />
          </div>
          {section === "Apply & Resume" && <SiteSetupApplySection setup={current} subPage="review" setSubPage={setApplySubPage} onSave={saveDesired} onSetupChanged={(next) => { currentRef.current = next; setCurrent(next); }} onApplied={(next) => { currentRef.current = next; setCurrent(next); void reload(); }} />}
        </section>
      </div>
    </section>
  </div>;
}

function SiteSetupChangeStrip({ setup, localPendingChanges = [] }: { setup: SiteSetup; localPendingChanges?: SiteSetupPendingChange[] }) {
  const pendingChanges = setup.pendingChanges.length ? setup.pendingChanges : localPendingChanges;
  const guidance = setup.guidance;
  const scope = guidance?.scope ?? { state: "info", value: `${setup.desired.systems.length} selected sites`, detail: `${setup.desired.sources.length} SDR sources` };
  const validation = guidance?.validation ?? { state: "warning", value: "Review RF Validation", detail: "Continue with the next incomplete proof stage." };
  const apply = pendingChanges.length
    ? { state: "warning", value: `${pendingChanges.length} change${pendingChanges.length === 1 ? "" : "s"} to apply`, detail: "Review the final configuration before Apply & Resume." }
    : guidance?.applyAndMonitoring ?? { state: siteSetupMonitoringTone(setup.status.monitoringState), value: "Configuration current", detail: setup.status.message };
  return <div className="site-setup-change-strip" aria-label="Setup operator guidance">
    <section className={scope.state}>
      <span>Current scope</span>
      <strong>{scope.value}</strong>
      <small>{scope.detail}</small>
    </section>
    <section className={validation.state}>
      <span>Validation next</span>
      <strong>{validation.value}</strong>
      <small>{validation.detail}</small>
    </section>
    <section className={apply.state}>
      <span>Apply & monitoring</span>
      <strong>{apply.value}</strong>
      <small>{apply.detail}</small>
    </section>
  </div>;
}

function siteSetupMonitoringTone(state: string) {
  const value = state.toLowerCase();
  if (value === "active") return "ok";
  if (value === "paused" || value === "stopped") return "warning";
  if (value === "stale" || value === "failed" || value === "error") return "danger";
  return "neutral";
}

type AuditHistoryCategory = "change" | "job" | "finding";
type AuditHistoryEntry = {
  key: string;
  timestampUtc: string;
  category: AuditHistoryCategory;
  event: string;
  summary: string;
  outcome: string;
  tone: "ok" | "warning" | "danger" | "neutral";
  detailsJson?: string;
  findingId?: string;
  evidenceStartUtc?: string;
  evidenceEndUtc?: string;
  findingKind?: string;
  findingSeverity?: string;
};

function AuditHistoryPanel({ refreshToken, onOpenJobs }: { refreshToken: number; onOpenJobs: () => void }) {
  const [entries, setEntries] = useState<AuditHistoryEntry[]>([]);
  const [windowHours, setWindowHours] = useState(24 * 7);
  const [category, setCategory] = useState<"all" | AuditHistoryCategory>("all");
  const [page, setPage] = useState(1);
  const [openEntryKey, setOpenEntryKey] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  const pageSize = 25;
  useEffect(() => { void loadHistory(); }, [refreshToken]);
  useEffect(() => { setPage(1); setOpenEntryKey(null); }, [windowHours, category]);
  useEffect(() => setOpenEntryKey(null), [page]);

  async function loadHistory() {
    setBusy(true);
    setMessage("");
    try {
      const [activities, jobs, recommendations] = await Promise.all([
        api.request<SiteSetupActivity[]>(`${siteSetupApi}/activity?limit=500`),
        api.request<Job[]>("/api/v1/jobs"),
        api.request<SystemRecommendations>("/api/v1/system/recommendations")
      ]);
      const activityEntries = activities.map(activityAuditEntry);
      const jobEntries = jobs.filter(job => isTerminalJob(job)).map(jobAuditEntry);
      const findingEntries = recommendations.history.map(findingAuditEntry);
      setEntries([...activityEntries, ...jobEntries, ...findingEntries].sort((a, b) => new Date(b.timestampUtc).getTime() - new Date(a.timestampUtc).getTime()));
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Unable to load audit history.");
    } finally {
      setBusy(false);
    }
  }

  const cutoff = Date.now() - windowHours * 60 * 60 * 1000;
  const matching = entries.filter(entry => {
    const timestamp = new Date(entry.timestampUtc).getTime();
    return Number.isFinite(timestamp) && timestamp >= cutoff && (category === "all" || entry.category === category);
  });
  const filtered = compactAuditFindings(matching);
  const pageCount = Math.max(1, Math.ceil(filtered.length / pageSize));
  const safePage = Math.min(page, pageCount);
  const visible = filtered.slice((safePage - 1) * pageSize, safePage * pageSize);

  return <div className="audit-history-panel">
    <SystemPageHeaderControls><div className="audit-history-controls header-controls">
      <label>History window<select value={windowHours} onChange={event => setWindowHours(Number(event.target.value))}>
        <option value={24}>Last 24 hours</option><option value={48}>Last 2 days</option><option value={168}>Last 7 days</option><option value={720}>Last 30 days</option><option value={2160}>Last 90 days</option>
      </select></label>
      <label>Event type<select value={category} onChange={event => setCategory(event.target.value as typeof category)}>
        <option value="all">All important events</option><option value="change">Setup and settings</option><option value="job">Job outcomes</option><option value="finding">Resolved findings</option>
      </select></label>
      <span>{matching.length === filtered.length ? `${filtered.length.toLocaleString()} matching event${filtered.length === 1 ? "" : "s"}` : `${matching.length.toLocaleString()} events consolidated into ${filtered.length.toLocaleString()} rows`}{busy ? " · Refreshing..." : ""}</span>
      <button onClick={onOpenJobs}>Open Jobs</button>
    </div></SystemPageHeaderControls>
    {message && <div className="settings-message error">{message}</div>}
    <div className="card audit-history-table-wrap">
      {pageCount > 1 && <div className="pagination-row table-top-pagination">
        <button disabled={safePage <= 1} onClick={() => setPage(1)}>First</button><button disabled={safePage <= 1} onClick={() => setPage(safePage - 1)}>Prev</button><span>Page {safePage} of {pageCount}</span><button disabled={safePage >= pageCount} onClick={() => setPage(safePage + 1)}>Next</button><button disabled={safePage >= pageCount} onClick={() => setPage(pageCount)}>Last</button>
      </div>}
      <table className="table compact-table audit-history-table">
        <thead><tr><th>Time</th><th>Type</th><th>Event</th><th>What happened</th><th>Outcome</th></tr></thead>
        <tbody>{visible.flatMap(entry => {
          const open = openEntryKey === entry.key;
          return [<tr className={`audit-history-row${open ? " open" : ""}`} key={entry.key} role="button" tabIndex={0} aria-expanded={open} onClick={() => setOpenEntryKey(open ? null : entry.key)} onKeyDown={event => {
            if (event.key !== "Enter" && event.key !== " ") return;
            event.preventDefault();
            setOpenEntryKey(open ? null : entry.key);
          }}>
            <td>{formatActivityDate(entry.timestampUtc)}</td><td>{auditCategoryLabel(entry.category)}</td><td>{entry.event}</td><td>{entry.summary}</td><td><span className={`section-status ${entry.tone}`}>{entry.outcome}</span></td>
          </tr>, ...(open ? [<tr className="audit-evidence-detail" key={`${entry.key}-evidence`}><td colSpan={5}><div className="audit-evidence-shelf"><strong>Evidence</strong>{auditEvidenceContent(entry)}</div></td></tr>] : [])];
        })}
        {!visible.length && <tr><td colSpan={5}>No matching audit events were recorded in this history window.</td></tr>}</tbody>
      </table>
    </div>
  </div>;
}

function activityAuditEntry(row: SiteSetupActivity): AuditHistoryEntry {
  return { key: `activity-${row.id}`, timestampUtc: row.timestampUtc, category: "change", event: label(row.action), summary: row.summary || "Configuration activity recorded.", outcome: "Recorded", tone: "neutral", detailsJson: row.detailsJson };
}

function jobAuditEntry(job: Job): AuditHistoryEntry {
  const outcome = label(job.status);
  return { key: `job-${job.id}`, timestampUtc: job.finishedAtUtc || job.updatedAtUtc || job.createdAtUtc, category: "job", event: `${jobDisplayName(job.type)} #${job.id}`, summary: job.message || `${jobDisplayName(job.type)} ${outcome.toLowerCase()}.`, outcome, tone: job.status === "completed" ? "ok" : job.status === "failed" ? "danger" : "warning" };
}

function findingAuditEntry(item: SystemRecommendation): AuditHistoryEntry {
  const resolution = item.resolution || "Current evidence no longer meets the activation threshold.";
  return { key: `finding-${item.id}-${item.resolvedAtUtc}`, timestampUtc: item.resolvedAtUtc, category: "finding", event: item.title, summary: resolution, outcome: "Resolved", tone: "ok", findingId: item.id, evidenceStartUtc: item.firstSeenUtc, evidenceEndUtc: item.lastSeenUtc, findingKind: label(item.kind), findingSeverity: label(item.severity), detailsJson: JSON.stringify({ type: label(item.kind), priority: label(item.severity), evidencePeriod: `${formatActivityDate(item.firstSeenUtc)} – ${formatActivityDate(item.lastSeenUtc)}` }) };
}

function compactAuditFindings(entries: AuditHistoryEntry[]) {
  const ordinary = entries.filter(entry => !entry.findingId);
  const groups = new Map<string, AuditHistoryEntry[]>();
  entries.filter(entry => entry.findingId).forEach(entry => groups.set(entry.findingId!, [...(groups.get(entry.findingId!) ?? []), entry]));
  const findings = Array.from(groups.values()).map(group => {
    const ordered = [...group].sort((a, b) => new Date(b.timestampUtc).getTime() - new Date(a.timestampUtc).getTime());
    const latest = ordered[0];
    if (ordered.length === 1) return latest;
    const evidenceStarts = ordered.map(entry => new Date(entry.evidenceStartUtc || entry.timestampUtc).getTime()).filter(Number.isFinite);
    const evidenceEnds = ordered.map(entry => new Date(entry.evidenceEndUtc || entry.timestampUtc).getTime()).filter(Number.isFinite);
    const firstEvidence = evidenceStarts.length ? new Date(Math.min(...evidenceStarts)).toISOString() : latest.timestampUtc;
    const lastEvidence = evidenceEnds.length ? new Date(Math.max(...evidenceEnds)).toISOString() : latest.timestampUtc;
    return {
      ...latest,
      key: `finding-group-${latest.findingId}`,
      summary: `Resolved ${ordered.length.toLocaleString()} times in this window. Latest resolution: ${latest.summary}`,
      outcome: `Resolved ×${ordered.length.toLocaleString()}`,
      detailsJson: JSON.stringify({ type: latest.findingKind, priority: latest.findingSeverity, occurrences: ordered.length, evidenceSpan: `${formatActivityDate(firstEvidence)} – ${formatActivityDate(lastEvidence)}` })
    };
  });
  return [...ordinary, ...findings].sort((a, b) => new Date(b.timestampUtc).getTime() - new Date(a.timestampUtc).getTime());
}

function auditCategoryLabel(category: AuditHistoryCategory) {
  if (category === "change") return "Setup / settings";
  if (category === "job") return "Job";
  return "Finding";
}

function isTerminalJob(job: Job) {
  return job.status === "completed" || job.status === "failed" || job.status === "canceled" || job.status === "cancelled";
}

function formatActivityDate(value: string) {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString();
}

function auditEvidenceContent(entry: AuditHistoryEntry) {
  const text = (entry.detailsJson || "").trim();
  if (!text || text === "{}") return <p className="muted">No additional evidence is stored for this event. Open Runtime / Jobs for detailed job progress and logs.</p>;
  let pretty = text;
  try {
    pretty = JSON.stringify(JSON.parse(text), null, 2);
  } catch {
    // Keep raw detail if it is not JSON.
  }
  return <pre>{pretty}</pre>;
}

function SiteSetupLocationSection({ setup, saveState, onSave }: { setup: SiteSetup; saveState: { field: string; status: "idle" | "saving" | "saved" | "error"; message: string }; onSave: (patch: Partial<SiteSetupConfig>, field: string) => Promise<void> }) {
  const [siteLabel, setSiteLabel] = useState(setup.desired.siteLabel);
  const [locationNotes, setLocationNotes] = useState(setup.desired.locationNotes);
  const [areas, setAreas] = useState<SiteSetupMonitoredArea[]>(() => normalizeSiteSetupAreas(setup.desired.monitoredAreas));
  const [areaBoundaryCandidates, setAreaBoundaryCandidates] = useState<Record<string, SetupAreaBoundaryCandidate[]>>({});
  const [areaLookupBusy, setAreaLookupBusy] = useState("");
  useEffect(() => {
    setSiteLabel(setup.desired.siteLabel);
    setLocationNotes(setup.desired.locationNotes);
    setAreas(normalizeSiteSetupAreas(setup.desired.monitoredAreas));
  }, [setup.desired.siteLabel, setup.desired.locationNotes, setup.desired.monitoredAreas, setup.desired.desiredVersion]);
  const locationContext = setup.locationContext ?? { derivedLocations: [], legacyAreaCount: 0 };
  const overrideRows = areas.map((area, index) => ({ area, index })).filter(row => row.area.isOverride);

  function statusFor(field: string) {
    if (saveState.field !== field || saveState.status === "idle") return null;
    return <span className={saveState.status === "error" ? "settings-message error" : saveState.status === "saved" ? "settings-message ok" : "settings-message"}>{saveState.message}</span>;
  }
  function saveSiteLabel() {
    const value = siteLabel.trim();
    if (value === setup.desired.siteLabel) return;
    void onSave({ siteLabel: value }, "siteLabel");
  }
  function saveLocationNotes() {
    const value = locationNotes.trim();
    if (value === setup.desired.locationNotes) return;
    void onSave({ locationNotes: value }, "locationNotes");
  }
  function saveAreas(nextAreas = areas) {
    void onSave({ monitoredAreas: normalizeSiteSetupAreas(nextAreas) }, "monitoredAreas");
  }
  function updateArea(index: number, patch: Partial<SiteSetupMonitoredArea>, save = false) {
    setAreas(current => {
      const next = current.map((area, i) => i === index ? { ...area, ...patch } : area);
      if (save)
        saveAreas(next);
      return next;
    });
  }
  function removeArea(index: number) {
    const next = areas.filter((_, i) => i !== index);
    setAreas(next);
    saveAreas(next);
  }
  function addOverride(context?: SiteSetup["locationContext"]["derivedLocations"][number]) {
    const siteShortNames = context?.siteShortNames ?? [];
    const systemShortName = siteShortNames[0] ?? "";
    const next = [...areas, normalizeSiteSetupArea({
      areaId: createClientId(),
      areaLabel: context?.label ?? "",
      systemShortName,
      north: 0,
      south: 0,
      east: 0,
      west: 0,
      aliases: Array.from(new Set([...siteShortNames, context?.catalogSystemShortName ?? ""].filter(Boolean))),
      isOverride: true,
      contextKey: context?.key ?? ""
    })];
    setAreas(next);
  }
  async function lookupAreaBoundary(index: number, key: string) {
    const area = areas[index];
    const query = String(area?.areaLabel || area?.systemShortName || "").trim();
    if (!query)
      return;
    setAreaLookupBusy(key);
    try {
      const result = await api.request<SetupAreaBoundaryResponse>("/api/v1/setup/areas/boundaries", {
        method: "POST",
        body: JSON.stringify({ query })
      });
      if (result.candidates.length === 1) {
        applyAreaBoundary(index, result.candidates[0]);
        setAreaBoundaryCandidates(current => ({ ...current, [key]: [] }));
      } else {
        setAreaBoundaryCandidates(current => ({ ...current, [key]: result.candidates }));
      }
    } finally {
      setAreaLookupBusy("");
    }
  }
  function applyAreaBoundary(index: number, candidate: SetupAreaBoundaryCandidate) {
    const area = areas[index];
    if (!area) return;
    const next = areas.map((item, i) => i === index
      ? normalizeSiteSetupArea({
        ...item,
        areaLabel: candidate.label,
        aliases: item.systemShortName ? Array.from(new Set([...(item.aliases ?? []), item.systemShortName])) : item.aliases,
        north: candidate.north,
        south: candidate.south,
        east: candidate.east,
        west: candidate.west
      })
      : item);
    setAreas(next);
    saveAreas(next);
  }

  return <div className="site-setup-form">
    <label className="setting-field">
      <span>Site name</span>
      <input value={siteLabel} onChange={event => setSiteLabel(event.target.value)} onBlur={saveSiteLabel} onKeyDown={event => { if (event.key === "Enter") event.currentTarget.blur(); }} />
      {statusFor("siteLabel")}
    </label>
    <label className="setting-field">
      <span>Location notes</span>
      <textarea value={locationNotes} onChange={event => setLocationNotes(event.target.value)} onBlur={saveLocationNotes} rows={4} />
      {statusFor("locationNotes")}
    </label>
    <div className="settings-subsection setup-location-context">
      <div className="setup-job-head">
        <div>
          <strong>Derived location context</strong>
          <small>RadioReference talkgroup jurisdiction is used first; selected-site geography is the fallback.</small>
        </div>
      </div>
      {locationContext.derivedLocations.length === 0 && <div className="setup-note">Import talkgroups and select sites to derive location context.</div>}
      {locationContext.derivedLocations.length > 0 && <details className="rf-technical-details"><summary>{locationContext.derivedLocations.length} imported and fallback context{locationContext.derivedLocations.length === 1 ? "" : "s"}</summary><div className="setup-location-context-list">
        {locationContext.derivedLocations.map(context => <div className="setup-location-context-row" key={context.key}>
          <div>
            <strong>{context.label}</strong>
            <small>{context.source}{context.talkgroupCount > 0 ? ` / ${context.talkgroupCount} talkgroups` : ""}</small>
          </div>
          <span>{context.siteLabels.join(", ") || context.siteShortNames.join(", ")}</span>
          <button type="button" disabled={context.hasOverride} onClick={() => addOverride(context)}>{context.hasOverride ? "Override added" : "Add override"}</button>
        </div>)}
      </div></details>}
      {locationContext.legacyAreaCount > 0 && <div className="setup-note">{locationContext.legacyAreaCount} legacy area record{locationContext.legacyAreaCount === 1 ? " is" : "s are"} preserved for compatibility but no longer shown as Setup location authority.</div>}
    </div>
    <div className="settings-subsection">
      <div className="setup-job-head">
        <div>
          <strong>Location overrides</strong>
          <small>Add one only when imported jurisdiction or selected-site geography is missing or incorrect.</small>
        </div>
        <button type="button" onClick={() => addOverride()}>Add manual override</button>
        {statusFor("monitoredAreas")}
      </div>
      {overrideRows.length === 0 && <div className="setup-note">No location overrides. Imported context remains authoritative.</div>}
      {overrideRows.map(({ area, index }) => {
        const key = areaDraftKey(area, index);
        const candidates = areaBoundaryCandidates[key] ?? [];
        return <div className="setup-area" key={key}>
          <div className="setup-area-head">
            <div>
              <span>Applies to site/system</span>
              <code>{area.systemShortName || "--"}</code>
            </div>
            <button type="button" className="danger-button" onClick={() => removeArea(index)}>Remove</button>
          </div>
          <div className="area-label-row">
            <SettingInput label="Override boundary" description="County or city boundary used instead of imported context." value={area.areaLabel} onChange={value => updateArea(index, { areaLabel: value })} />
            <button type="button" disabled={Boolean(areaLookupBusy) || !String(area.areaLabel || area.systemShortName || "").trim()} onClick={() => void lookupAreaBoundary(index, key)}>
              {areaLookupBusy === key ? "Finding..." : "Find boundary"}
            </button>
          </div>
          {candidates.length > 0 && <div className="area-boundary-candidates">
            {candidates.map(candidate => <button type="button" key={`${candidate.kind}-${candidate.geoId}`} onClick={() => applyAreaBoundary(index, candidate)}>
              <strong>{candidate.label}</strong>
              <small>{candidate.kind} / {candidate.source} / N {candidate.north.toFixed(4)}, S {candidate.south.toFixed(4)}, E {candidate.east.toFixed(4)}, W {candidate.west.toFixed(4)}</small>
            </button>)}
          </div>}
          {hasUsableAreaBounds(area) ? <AreaMapPreview area={area} /> : <div className="setup-note">Find and review the boundary before saving this override.</div>}
          <div className="area-coordinate-grid">
            <SettingInput label="North" description="Northern latitude boundary." value={String(area.north ?? "")} onChange={value => updateArea(index, { north: numberOrZero(value) })} />
            <SettingInput label="South" description="Southern latitude boundary." value={String(area.south ?? "")} onChange={value => updateArea(index, { south: numberOrZero(value) })} />
            <SettingInput label="East" description="Eastern longitude boundary." value={String(area.east ?? "")} onChange={value => updateArea(index, { east: numberOrZero(value) })} />
            <SettingInput label="West" description="Western longitude boundary." value={String(area.west ?? "")} onChange={value => updateArea(index, { west: numberOrZero(value) })} />
          </div>
          <div className="setup-button-row">
            <button type="button" disabled={!hasUsableAreaBounds(area)} onClick={() => saveAreas()}>Save override</button>
          </div>
        </div>;
      })}
    </div>
  </div>;
}

function SiteSetupSystemsSection({ setup, saveState, onSave }: { setup: SiteSetup; saveState: { field: string; status: "idle" | "saving" | "saved" | "error"; message: string }; onSave: (patch: Partial<SiteSetupConfig>, field: string) => Promise<void> }) {
  const [newSid, setNewSid] = useState("");
  const [catalogs, setCatalogs] = useState<SetupTrConfigSites[]>([]);
  const [siteSearch, setSiteSearch] = useState("");
  const [busySid, setBusySid] = useState("");
  const [message, setMessage] = useState("");
  const selectedSystems = setup.desired.systems;
  const selectedSids = setupRadioReferenceSids(setup);
  const selectedSidSignature = selectedSids.join("|");

  useEffect(() => {
    let stopped = false;
    async function loadSelectedCatalogs() {
      if (!selectedSids.length) {
        setCatalogs([]);
        return;
      }
      setBusySid("selected");
      try {
        const loaded = await Promise.all(selectedSids.map(loadRadioReferenceSiteCatalog));
        if (!stopped)
          setCatalogs(loaded.sort((left, right) => left.systemName.localeCompare(right.systemName)));
      } catch (error) {
        if (!stopped)
          setMessage(error instanceof Error ? error.message : "Unable to load selected RR systems.");
      } finally {
        if (!stopped)
          setBusySid("");
      }
    }
    void loadSelectedCatalogs();
    return () => { stopped = true; };
  }, [selectedSidSignature]);

  function statusFor(field: string) {
    if (saveState.field !== field || saveState.status === "idle") return null;
    return <span className={saveState.status === "error" ? "settings-message error" : saveState.status === "saved" ? "settings-message ok" : "settings-message"}>{saveState.message}</span>;
  }

  async function addCatalog() {
    const sid = newSid.trim();
    if (!sid) return;
    if (catalogs.some(row => row.radioReferenceSid === sid)) {
      setMessage(`RR ${sid} is already loaded.`);
      return;
    }
    setBusySid(sid);
    setMessage("");
    try {
      const loaded = await loadRadioReferenceSiteCatalog(sid);
      setCatalogs(current => [...current, loaded].sort((left, right) => left.systemName.localeCompare(right.systemName)));
      setNewSid("");
      setMessage(`${loaded.systemName} loaded with ${loaded.sites.length.toLocaleString()} site(s).`);
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Unable to load RR system.");
    } finally {
      setBusySid("");
    }
  }

  async function saveSelectedSystems(systems: RfSurveySystem[]) {
    const ordered = [...systems].sort((left, right) => left.shortName.localeCompare(right.shortName));
    const names = ordered.map(system => system.shortName);
    const validNames = new Set(names.map(name => name.toLowerCase()));
    const sourceAssignments = Object.fromEntries(Object.entries(setup.desired.sourceAssignments).filter(([name]) => validNames.has(name.toLowerCase())));
    const selectedControlChannels = new Set(ordered.flatMap(system => system.controlChannelsHz).map(Math.round));
    const rfSelections = setup.desired.rfSelections.filter(selection => selectedControlChannels.has(Math.round(selection.frequencyHz)));
    await onSave({
      systemShortNames: names,
      sourcePlanSystemShortNames: names,
      sourcePlanMode: setup.desired.sourcePlanMode || "full",
      sourceAssignments,
      rfSelections,
      systems: ordered
    }, "systems");
  }

  async function toggleSite(catalog: SetupTrConfigSites, site: SetupTrConfigSite, checked: boolean) {
    const shortName = site.shortName || site.name;
    const matchesIdentity = (system: RfSurveySystem) => stringEqualsIgnoreCase(system.shortName, shortName) && system.radioReferenceSid === catalog.radioReferenceSid;
    if (!checked) {
      await saveSelectedSystems(selectedSystems.filter(system => !matchesIdentity(system)));
      return;
    }
    const shortNameConflict = selectedSystems.find(system => stringEqualsIgnoreCase(system.shortName, shortName) && system.radioReferenceSid && system.radioReferenceSid !== catalog.radioReferenceSid);
    if (shortNameConflict) {
      setMessage(`${shortName} is already selected from RR ${shortNameConflict.radioReferenceSid || "unknown"}. TR short names must be unique.`);
      return;
    }
    const existing = selectedSystems.find(matchesIdentity);
    if (existing) return;
    const existingWithoutSid = selectedSystems.find(system => stringEqualsIgnoreCase(system.shortName, shortName) && !system.radioReferenceSid);
    const selectedSystem: RfSurveySystem = {
      shortName,
      siteLabel: site.name || shortName,
      controlChannelsHz: site.controlChannelsMhz.map(value => Math.round(value * 1_000_000)),
      voiceFrequenciesHz: existingWithoutSid?.voiceFrequenciesHz ?? [],
      radioReferenceSid: catalog.radioReferenceSid,
      talkgroupSystemShortName: existingWithoutSid?.talkgroupSystemShortName ?? ""
    };
    await saveSelectedSystems(existingWithoutSid
      ? selectedSystems.map(system => system === existingWithoutSid ? selectedSystem : system)
      : [...selectedSystems, selectedSystem]);
  }

  async function removeSelectedSystem(system: RfSurveySystem) {
    await saveSelectedSystems(selectedSystems.filter(row => !(stringEqualsIgnoreCase(row.shortName, system.shortName) && row.radioReferenceSid === system.radioReferenceSid)));
  }

  async function removeCatalog(catalog: SetupTrConfigSites) {
    const selected = selectedSystems.filter(system => system.radioReferenceSid === catalog.radioReferenceSid);
    if (selected.length && !confirmAction(`Remove ${catalog.systemName}?`, `This also removes ${selected.length.toLocaleString()} selected site${selected.length === 1 ? "" : "s"} from RR ${catalog.radioReferenceSid}.`))
      return;
    if (selected.length)
      await saveSelectedSystems(selectedSystems.filter(system => system.radioReferenceSid !== catalog.radioReferenceSid));
    setCatalogs(current => current.filter(row => row.radioReferenceSid !== catalog.radioReferenceSid));
  }

  const query = siteSearch.trim().toLowerCase();
  const selectedKeys = new Set(selectedSystems.map(system => setupSystemIdentity(system)));
  const loadedSelectedKeys = new Set(catalogs.flatMap(catalog => catalog.sites.map(site => `${catalog.radioReferenceSid}:${(site.shortName || site.name).toLowerCase()}`)));
  const unavailableSelectedSystems = selectedSystems.filter(system => !loadedSelectedKeys.has(setupSystemIdentity(system)));

  return <div className="site-setup-form site-setup-systems">
    <div className="site-setup-inline-fields">
      <label className="setting-field">
        <span>Add RR system ID</span>
        <input value={newSid} inputMode="numeric" onChange={event => setNewSid(event.target.value)} onKeyDown={event => { if (event.key === "Enter") void addCatalog(); }} />
      </label>
      <button type="button" disabled={Boolean(busySid) || !newSid.trim()} onClick={() => void addCatalog()}>{busySid && busySid !== "selected" ? "Loading" : "Add RR System"}</button>
    </div>
    <div className="site-setup-source-list">
      {catalogs.map(catalog => <button type="button" className="site-setup-source-chip" disabled={Boolean(busySid)} onClick={() => void removeCatalog(catalog)} title={`Remove RR ${catalog.radioReferenceSid}`} key={catalog.radioReferenceSid}>
        <span>{catalog.systemName}</span><small>RR {catalog.radioReferenceSid}</small><b aria-hidden="true">x</b>
      </button>)}
      {busySid === "selected" && <span className="settings-message">Loading selected RR systems...</span>}
    </div>
    {message && <div className={message.toLowerCase().includes("unable") || message.toLowerCase().includes("error") || message.toLowerCase().includes("must") ? "settings-message error" : "settings-message ok"}>{message}</div>}
    <label className="setting-field">
      <span>Search sites</span>
      <input value={siteSearch} onChange={event => setSiteSearch(event.target.value)} placeholder="Name, short name, control channel, or RR ID" />
      {statusFor("systems")}
    </label>
    <div className="site-setup-selection-summary">
      <strong>{selectedSystems.length.toLocaleString()} selected across {selectedSids.length.toLocaleString()} RR system{selectedSids.length === 1 ? "" : "s"}</strong>
      <div className="site-setup-selected-chips">
        {selectedSystems.length ? selectedSystems.map(system => {
          const labelText = system.siteLabel || system.shortName;
          return <button type="button" className="rf-selected-site-chip" key={setupSystemIdentity(system)} onClick={() => void removeSelectedSystem(system)} title={`Remove ${labelText}`}>{labelText} <small>RR {system.radioReferenceSid || "--"}</small> <span aria-hidden="true">x</span></button>;
        }) : <span>No systems selected</span>}
      </div>
    </div>
    <div className="site-setup-site-table-wrap">
      <table className="table compact-table site-setup-site-table">
        <thead><tr><th></th><th>Site</th><th>System / RR ID</th><th>Short name</th><th>Control channels</th></tr></thead>
        {catalogs.map(catalog => {
          const sites = catalog.sites.filter(site => !query || [catalog.systemName, catalog.radioReferenceSid, site.name, site.shortName, ...site.controlChannelsMhz.map(formatMhz)].some(value => value.toLowerCase().includes(query)));
          return <tbody key={catalog.radioReferenceSid}>
            <tr className="table-group-row"><td colSpan={5}><strong>{catalog.systemName}</strong> <span className="muted">RR {catalog.radioReferenceSid}</span></td></tr>
            {sites.length === 0 && <tr><td colSpan={5}>No matching sites.</td></tr>}
            {sites.map(site => {
              const identity = `${catalog.radioReferenceSid}:${(site.shortName || site.name).toLowerCase()}`;
              const checked = selectedKeys.has(identity);
              return <tr key={identity} className={checked ? "selected" : ""}>
                <td><input type="checkbox" checked={checked} onChange={event => void toggleSite(catalog, site, event.currentTarget.checked)} aria-label={`Select ${site.name || site.shortName} from RR ${catalog.radioReferenceSid}`} /></td>
                <td>{site.name || site.shortName}</td>
                <td>{catalog.systemName} / RR {catalog.radioReferenceSid}</td>
                <td>{site.shortName}</td>
                <td>{site.controlChannelsMhz.length ? site.controlChannelsMhz.map(formatMhz).join(", ") : "--"}</td>
              </tr>;
            })}
          </tbody>;
        })}
        {unavailableSelectedSystems.length > 0 && <tbody>
          <tr className="table-group-row"><td colSpan={5}><strong>Selected sites with unavailable RR catalog data</strong></td></tr>
          {unavailableSelectedSystems.map(system => <tr key={setupSystemIdentity(system)} className="selected">
            <td></td><td>{system.siteLabel || system.shortName}</td><td>RR {system.radioReferenceSid || "--"}</td><td>{system.shortName}</td><td>{system.controlChannelsHz.length ? formatFrequencyList(system.controlChannelsHz) : "--"}</td>
          </tr>)}
        </tbody>}
        {catalogs.length === 0 && unavailableSelectedSystems.length === 0 && <tbody><tr><td colSpan={5}>Add an RR system to select sites.</td></tr></tbody>}
      </table>
    </div>
  </div>;
}

async function loadRadioReferenceSiteCatalog(sid: string) {
  const result = await api.request<SetupTrConfigSites>("/api/v1/setup/tr-config/sites", {
    method: "POST",
    body: JSON.stringify({ radioReferenceSid: sid })
  });
  writeCachedRadioReferenceSites(result.radioReferenceSid, result);
  return result;
}

function setupSystemIdentity(system: Pick<RfSurveySystem, "shortName" | "radioReferenceSid">) {
  return `${String(system.radioReferenceSid || "").trim()}:${String(system.shortName || "").trim().toLowerCase()}`;
}

function setupRadioReferenceSids(setup: SiteSetup) {
  return Array.from(new Set(setup.desired.systems.map(system => String(system.radioReferenceSid || "").trim()).filter(Boolean))).sort((left, right) => left.localeCompare(right));
}

function selectedSetupSystemNames(setup: SiteSetup) {
  const names = setup.desired.systemShortNames.length
    ? setup.desired.systemShortNames
    : setup.desired.systems.map(system => system.shortName);
  return Array.from(new Set(names.filter(Boolean))).sort((a, b) => a.localeCompare(b));
}

function SiteSetupHardwareSection({ setup, saveState, onSave }: { setup: SiteSetup; saveState: { field: string; status: "idle" | "saving" | "saved" | "error"; message: string }; onSave: (patch: Partial<SiteSetupConfig>, field: string) => Promise<void> }) {
  const [rfPath, setRfPath] = useState<RfSurveyPathProfile>(() => normalizeSetupRfPath(setup.desired.rfPath));
  const [sdrDetection, setSdrDetection] = useState<SetupSdrDetection | null>(null);
  const [sdrBusy, setSdrBusy] = useState(false);
  const [sdrMessage, setSdrMessage] = useState("");
  const rfPathRef = useRef(rfPath);
  const saveTimerRef = useRef<number | null>(null);
  const onSaveRef = useRef(onSave);
  useEffect(() => {
    const next = normalizeSetupRfPath(setup.desired.rfPath);
    rfPathRef.current = next;
    setRfPath(next);
  }, [setup.desired.rfPath, setup.desired.desiredVersion]);
  useEffect(() => {
    onSaveRef.current = onSave;
  }, [onSave]);
  useEffect(() => () => {
    if (saveTimerRef.current !== null) {
      window.clearTimeout(saveTimerRef.current);
      void onSaveRef.current({ rfPath: rfPathRef.current }, "rfPath");
    }
  }, []);

  function statusFor(field: string) {
    if (saveState.field !== field || saveState.status === "idle") return null;
    return <span className={saveState.status === "error" ? "settings-message error" : saveState.status === "saved" ? "settings-message ok" : "settings-message"}>{saveState.message}</span>;
  }
  function queueRfPathSave(next: RfSurveyPathProfile) {
    if (saveTimerRef.current !== null)
      window.clearTimeout(saveTimerRef.current);
    saveTimerRef.current = window.setTimeout(() => {
      saveTimerRef.current = null;
      void onSaveRef.current({ rfPath: next }, "rfPath");
    }, 800);
  }
  function updateRfPath(update: React.SetStateAction<RfSurveyPathProfile>) {
    const next = typeof update === "function"
      ? (update as (current: RfSurveyPathProfile) => RfSurveyPathProfile)(rfPathRef.current)
      : update;
    rfPathRef.current = normalizeSetupRfPath(next);
    setRfPath(rfPathRef.current);
    queueRfPathSave(rfPathRef.current);
  }
  async function runSdrInventory() {
    if (!confirmAction("Pause monitoring for SDR inventory?", "This action stops trunk-recorder briefly, inventories connected receivers, and starts monitoring again. It will not change saved source selections.")) return;
    setSdrBusy(true);
    setSdrMessage("");
    try {
      const result = await api.request<SetupSdrDetection>("/api/v1/setup/sdrs", { method: "POST", body: JSON.stringify({ confirmed: true }) });
      setSdrDetection(result);
      setSdrMessage(`${result.message} Review the detected devices before using them as saved sources.`);
    } catch (error) {
      setSdrMessage(error instanceof Error ? error.message : "SDR inventory failed.");
    } finally {
      setSdrBusy(false);
    }
  }
  return <div className="site-setup-form site-setup-hardware">
    <div className="site-setup-hardware-inventory">
      <div className="setup-job-head">
        <div><strong>SDR hardware</strong><small>Detect connected receivers before documenting or validating the RF path.</small></div>
        <div className="rf-primary-actions">
          <button type="button" className="primary" disabled={sdrBusy} onClick={() => void runSdrInventory()}>{sdrBusy ? "Monitoring paused; inspecting..." : sdrDetection ? "Rerun SDR Inventory" : "Pause Monitoring & Run SDR Inventory"}</button>
          {sdrDetection && <button type="button" disabled={sdrBusy || sdrDetection.devices.length === 0} onClick={() => {
            const detectedSources = setupSourcesFromSdrDetection(setup, sdrDetection);
            void onSave({ sources: detectedSources, selectedSourceIndexes: detectedSources.map(source => source.index) }, "sources");
          }}>Use Detected SDRs</button>}
        </div>
      </div>
      {sdrMessage && <span className={sdrMessage.toLowerCase().includes("fail") || sdrMessage.toLowerCase().includes("unable") ? "settings-message error" : "settings-message ok"}>{sdrMessage}</span>}
      {sdrDetection && <SetupSdrInventorySummary detection={sdrDetection} />}
    </div>
    <RfPathStep
      path={rfPath}
      setPath={updateRfPath}
      onTouched={() => undefined}
      onLoadPrevious={async () => {
        const next = normalizeSetupRfPath(setup.desired.rfPath);
        updateRfPath(next);
      }}
      busy=""
      headerMode="actions"
    />
    {statusFor("rfPath")}
  </div>;
}

function normalizeSetupRfPath(path?: RfSurveyPathProfile | null): RfSurveyPathProfile {
  const value = path ?? ({} as RfSurveyPathProfile);
  const chain = (value.chain ?? []).map(normalizeRfChainItem);
  return {
    antenna: value.antenna ?? "",
    antennaType: value.antennaType ?? "",
    antennaMount: value.antennaMount ?? "",
    antennaPolarization: value.antennaPolarization ?? "",
    aimedAtSite: value.aimedAtSite ?? "",
    positionNotes: value.positionNotes ?? "",
    connectorChain: value.connectorChain ?? "",
    coax: value.coax ?? "",
    splitterOrMulticoupler: value.splitterOrMulticoupler ?? "",
    lna: value.lna ?? "",
    filters: value.filters ?? "",
    sdrNotes: value.sdrNotes ?? "",
    observations: value.observations ?? "",
    chain: chain.length ? chain : [newRfChainItem()]
  };
}

type SiteSetupTalkgroupSource = { key: string; radioReferenceSid: string; catalogSystem: string; siteShortName?: string; siteLabel?: string };

function SetupSdrInventorySummary({ detection }: { detection: SetupSdrDetection }) {
  const rows = detection.devices.map(device => ({
    key: `${device.index}-${device.serial || device.usbLine}`,
    title: `${device.type || "SDR"} ${device.serial || device.label || device.index}`,
    detail: [device.deviceArgs, device.label || device.usbLine, device.defaultSampleRate ? `${device.defaultSampleRate} sps` : "", device.defaultGain ? `${device.defaultGain} ${device.gainMode}` : "", device.warning].filter(Boolean).join(" / ")
  }));
  return <div className={rows.length ? "rf-inventory-summary passed" : "rf-inventory-summary failed"}>
    <div className="rf-inventory-head">
      <div>
        <strong>SDR Inventory: {rows.length ? "Ready" : "No devices"}</strong>
        <span>{detection.message}</span>
      </div>
    </div>
    <div className="rf-inventory-device-list">
      {rows.map(row => <div className="rf-inventory-device-row" key={row.key}>
        <strong>{row.title}</strong>
        <span>{row.detail || "--"}</span>
      </div>)}
      {rows.length === 0 && <div className="setup-note">No SDR devices were reported in inventory evidence.</div>}
    </div>
    {detection.rawOutput && <details><summary>Raw SDR output</summary><pre className="log-box">{detection.rawOutput}</pre></details>}
  </div>;
}

function SiteSetupRfValidationSection({ setup, active, stage, onSave, onTrOperationChange, onServerSetupChanged }: { setup: SiteSetup; active: boolean; stage: SiteSetupRfStage; onSave: (patch: Partial<SiteSetupConfig>, field: string) => Promise<void>; onTrOperationChange: (value: string) => void; onServerSetupChanged: (next: SiteSetup) => void }) {
  const [targetSystem] = useState(() => localStorage.getItem("pizzawave-site-setup-rf-target-system") || "");
  const [detail, setDetail] = useState<RfSurveyDetail | null>(null);
  const [busy, setBusy] = useState("");
  const [message, setMessage] = useState("");
  const [showHistory, setShowHistory] = useState(false);
  const [experimentName, setExperimentName] = useState("");
  const [experimentHypothesis, setExperimentHypothesis] = useState("");
  const [experimentPhysicalChange, setExperimentPhysicalChange] = useState("");
  const [activeControlChannelHz, setActiveControlChannelHz] = useState(0);
  const [duration, setDuration] = useState("45");
  const [waterfallSweepSelections, setWaterfallSweepSelections] = useState<WaterfallSweepSelection[]>([]);
  const [details, setDetails] = useState<{ title: string; body: React.ReactNode } | null>(null);
  const softwareCheckAttemptRef = useRef("");
  const systems = siteSetupSystems(setup);
  const sources = siteSetupSources(setup);
  const selectedSourceIndexes = setup.desired.selectedSourceIndexes.length
    ? setup.desired.selectedSourceIndexes
    : sources.map(source => source.index);
  const controlChannels = normalizeControlChannelSelection(systems.flatMap(system => system.controlChannelsHz));
  const signature = JSON.stringify({
    siteLabel: setup.desired.siteLabel,
    sids: setupRadioReferenceSids(setup),
    systems,
    sources,
    selectedSourceIndexes,
    rfPath: setup.desired.rfPath,
    appliedConfigHash: setup.status.appliedConfigHash
  });
  useEffect(() => {
    if (!active)
      return;
    let stopped = false;
    async function loadWorkspace() {
      setBusy("workspace");
      setMessage("");
      try {
        const current = await prepareSiteSetupRfWorkspace();
        if (!stopped) {
          setDetail(current);
          const targetSystem = localStorage.getItem("pizzawave-site-setup-rf-target-system") || "";
          const targetCc = current.profile.systems.find(system => system.shortName.toLowerCase() === targetSystem.toLowerCase())?.controlChannelsHz[0];
          const firstCc = targetCc ?? normalizeControlChannelSelection(current.profile.systems.flatMap(system => system.controlChannelsHz))[0] ?? controlChannels[0] ?? 0;
          setActiveControlChannelHz(currentValue => currentValue || firstCc);
          if (targetSystem) localStorage.removeItem("pizzawave-site-setup-rf-target-system");
        }
      } catch (error) {
        if (!stopped)
          setMessage(error instanceof Error ? error.message : "Unable to prepare RF validation session.");
      } finally {
        if (!stopped)
          setBusy("");
      }
    }
    void loadWorkspace();
    return () => { stopped = true; };
  }, [active, signature]);
  useEffect(() => () => onTrOperationChange(""), [onTrOperationChange]);
  async function refreshWorkspace() {
    if (!detail) return;
    const next = await api.request<RfSurveyDetail>(rfDetailUrl(siteSetupRfApi, detail.session.id));
    setDetail(next);
  }
  async function runExperiment(type: string, _estimate: string, controlChannelHz?: number, extraRequest?: Record<string, unknown>) {
    if (!detail) return undefined;
    setBusy(type);
    setMessage("");
    if (type === "rf_power_scan" || type === "rf_validation_sweep")
      onTrOperationChange("trunk-recorder is temporarily paused while Setup runs RF validation.");
    try {
      const experiment = await api.request<RfSurveyExperiment>(`${siteSetupRfApi}/${encodeURIComponent(detail.session.id)}/experiments/run`, {
        method: "POST",
        body: JSON.stringify({
          type,
          durationSeconds: type === "rf_validation_sweep" ? 300 : 45,
          controlChannelHz,
          name: experimentName.trim(),
          hypothesis: experimentHypothesis.trim(),
          physicalChange: experimentPhysicalChange.trim(),
          ...extraRequest
        })
      });
      await refreshWorkspace();
      setExperimentName("");
      setExperimentHypothesis("");
      setExperimentPhysicalChange("");
      return experiment;
    } catch (error) {
      setMessage(error instanceof Error ? error.message : `${label(type)} failed.`);
      return undefined;
    } finally {
      if (type === "rf_power_scan" || type === "rf_validation_sweep")
        onTrOperationChange("");
      setBusy("");
    }
  }
  async function runP25(controlChannelHz?: number) {
    await runExperiment("control_channel_p25_probe", "45 seconds", controlChannelHz);
  }
  async function runSdrInventoryExperiment() {
    if (!confirmAction("Pause monitoring for SDR inventory?", "This explicit RF Preparation action stops trunk-recorder briefly, inventories connected receivers, and starts monitoring again. The result is recorded as evidence and will not rewrite saved source selections.")) return;
    await runExperiment("sdr_inventory", "about 15 seconds");
  }
  async function runSoftwareCheck(force = false) {
    if (!detail) return;
    setBusy("software_check");
    setMessage("");
    try {
      const toolPrep = await api.request<RfSurveyToolPrep>(`${siteSetupRfApi}/${encodeURIComponent(detail.session.id)}/software-check${force ? "?force=true" : ""}`, { method: "POST" });
      setDetail(current => current ? { ...current, toolPrep } : current);
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Required software check failed.");
    } finally {
      setBusy("");
    }
  }
  async function runCallAndTranscriptionProof(rerun = false) {
    if (!detail) return;
    setBusy("call_transcription_proof");
    setMessage("");
    try {
      let voice = voiceCapture;
      if (rerun || voice?.status !== "passed") {
        voice = await api.request<RfSurveyExperiment>(`${siteSetupRfApi}/${encodeURIComponent(detail.session.id)}/experiments/run`, {
          method: "POST",
          body: JSON.stringify({ type: "voice_capture_trial", durationSeconds: 180 })
        });
      }
      if (voice?.status !== "passed") {
        setMessage(voice?.blockingIssue || voice?.resultSummary || "Call capture did not pass. Transcription was not checked.");
        await refreshWorkspace();
        return;
      }
      setMessage("Call capture passed. Checking transcription...");
      const transcription = await api.request<RfSurveyExperiment>(`${siteSetupRfApi}/${encodeURIComponent(detail.session.id)}/experiments/run`, {
        method: "POST",
        body: JSON.stringify({ type: "transcription_gate", durationSeconds: 120 })
      });
      setMessage(transcription.status === "passed"
        ? "Call and transcription proof passed. Continue to Verdict."
        : transcription.blockingIssue || transcription.resultSummary || "Transcription proof did not pass.");
      await refreshWorkspace();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Call and transcription proof failed.");
      await refreshWorkspace();
    } finally {
      setBusy("");
    }
  }
  const handleWaterfallStatusChange = useCallback((status: RfSurveyWaterfallStatus | null) => {
    onTrOperationChange(waterfallTrOperationText(status));
  }, [onTrOperationChange]);
  const effectiveSystems = detail?.profile.systems?.length ? detail.profile.systems : systems;
  const effectiveSources = detail?.profile.sources?.length ? detail.profile.sources : sources;
  const effectiveControlChannels = normalizeControlChannelSelection(effectiveSystems.flatMap(system => system.controlChannelsHz));
  useEffect(() => {
    const desiredSelections = normalizeWaterfallSweepSelections(setup.desired.rfSelections ?? []);
    setWaterfallSweepSelections(desiredSelections);
  }, [setup.desired.desiredVersion]);
  const updateWaterfallSweepSelections = (values: WaterfallSweepSelection[]) => {
    const normalized = normalizeWaterfallSweepSelections(values);
    setWaterfallSweepSelections(normalized);
    void onSave({ rfSelections: normalized.map(toSiteSetupRfSelectionPayload) }, "rfSelections");
  };
  async function acceptSweepCandidate(source: RfSurveySource, result: SweepResult, candidate: SweepCandidate) {
    const desiredSources = siteSetupSources(setup);
    const sourcePosition = desiredSources.findIndex(row => row.index === source.index);
    if (sourcePosition < 0)
      throw new Error(`Source ${source.index} is no longer part of Setup. Refresh SDR Inventory before accepting this result.`);

    const gain = result.gain.trim() || source.gain;
    const defaultSourceIndex = setup.desired.selectedSourceIndexes[0] ?? desiredSources[0]?.index ?? source.index;
    const nextSources = desiredSources.map(row => row.index === source.index
      ? { ...row, errorHz: candidate.errorHz, gain }
      : row);
    const nextSelections = normalizeWaterfallSweepSelections(waterfallSweepSelections).map(row =>
      (row.sourceIndex ?? defaultSourceIndex) === source.index
        ? { ...row, sourceIndex: source.index, errorHz: candidate.errorHz, gain }
        : row);
    await onSave({
      sources: nextSources,
      rfSelections: nextSelections.map(toSiteSetupRfSelectionPayload)
    }, "rfCalibration");
  }
  const latestExperiment = (type: string) => [...(detail?.experiments ?? [])]
    .filter(experiment => experiment.type === type)
    .sort((a, b) => (b.createdAtUtc || "").localeCompare(a.createdAtUtc || ""))[0];
  const ccQualityRuns = (detail?.experiments ?? [])
    .filter(experiment => experiment.type === "control_channel_quality")
    .sort((a, b) => (b.createdAtUtc || "").localeCompare(a.createdAtUtc || ""));
  const ccQuality = latestExperiment("control_channel_quality");
  const inventory = latestExperiment("sdr_inventory");
  const powerScan = latestExperiment("rf_power_scan");
  const validationSweep = latestExperiment("rf_validation_sweep");
  const p25 = latestExperiment("control_channel_p25_probe");
  const sweep = latestExperiment("error_gain_sweep");
  const voiceCapture = latestExperiment("voice_capture_trial");
  const transcriptionGate = latestExperiment("transcription_gate");
  const stabilityVerdict = latestExperiment("stability_verdict");
  useEffect(() => {
    if (!active || stage !== "preparation" || !detail || (detail.toolPrep?.tools.length ?? 0) > 0)
      return;
    const key = `${detail.session.id}:${setup.status.appliedConfigHash || "unapplied"}`;
    if (softwareCheckAttemptRef.current === key)
      return;
    softwareCheckAttemptRef.current = key;
    void runSoftwareCheck();
  }, [active, stage, detail?.session.id, detail?.toolPrep?.tools.length, setup.status.appliedConfigHash]);
  return <div className="site-setup-form site-setup-rf-validation">
    {targetSystem && <div className="setup-note"><strong>Reviewing {trSystemDisplayName(targetSystem)}</strong><span>Control-channel evidence is preselected for this site; other configured sites remain visible for comparison.</span></div>}
    {busy === "workspace" && <div className="setup-note">Preparing RF validation session...</div>}
    {message && <div className={message.toLowerCase().includes("unable") || message.toLowerCase().includes("failed") || message.toLowerCase().includes("blocked") || message.toLowerCase().includes("did not pass") ? "settings-message error" : "settings-message ok"}>{message}</div>}
    {stage !== "calls" && <div className="rf-history-toolbar">
      <button type="button" className={showHistory ? "primary" : ""} onClick={() => setShowHistory(value => !value)}>{showHistory ? "Back to validation" : "Experiments & Evidence"}</button>
      {!showHistory && <details className="rf-experiment-context"><summary>Name or describe the next experiment</summary><div className="rf-experiment-context-fields">
        <label><span>Name</span><input value={experimentName} onChange={event => setExperimentName(event.target.value)} placeholder="Automatic descriptive name" /></label>
        <label><span>Hypothesis</span><input value={experimentHypothesis} onChange={event => setExperimentHypothesis(event.target.value)} placeholder="What are you testing?" /></label>
        <label><span>Physical change</span><input value={experimentPhysicalChange} onChange={event => setExperimentPhysicalChange(event.target.value)} placeholder="Antenna, cable, filter, placement, or none" /></label>
      </div></details>}
    </div>}
    {showHistory && stage !== "calls"
      ? <SetupRfHistoryViewer currentSite={setup.desired.siteLabel} />
      : detail
      ? <>
        <SiteSetupRfStageBanner stage={stage} detail={detail} setup={setup} validationSweep={validationSweep} voiceCapture={voiceCapture} transcriptionGate={transcriptionGate} />
        {stage === "preparation" && <SiteSetupRfPreparationStage
          detail={detail}
          systems={effectiveSystems}
          sources={effectiveSources}
          inventory={inventory}
          inventoryBusy={busy === "sdr_inventory"}
          softwareCheckBusy={busy === "software_check"}
          onSoftwareCheck={() => void runSoftwareCheck(true)}
          onRunInventory={() => void runSdrInventoryExperiment()}
          onShowDetails={setDetails}
        />}
        <div style={stage === "spectrum" ? undefined : { display: "none" }} aria-hidden={stage === "spectrum" ? undefined : "true"}>
          <WaterfallStep
            apiBase={siteSetupRfApi}
            surveyId={detail.session.id}
            visible={active && stage === "spectrum"}
            locked={effectiveSources.length === 0 || effectiveControlChannels.length === 0}
            sources={effectiveSources}
            selectedSources={selectedSourceIndexes}
            systems={effectiveSystems}
            controlChannels={effectiveControlChannels}
            activeControlChannelHz={activeControlChannelHz || effectiveControlChannels[0] || 0}
            waterfallSweepSelections={waterfallSweepSelections}
            onWaterfallSweepSelections={updateWaterfallSweepSelections}
            onRunExperiment={runExperiment}
            onReload={refreshWorkspace}
            onStatusChange={handleWaterfallStatusChange}
          />
        </div>
        {stage === "control" && <SiteValidationStep
          apiBase={siteSetupRfApi}
          activeOperation="power"
          busy={busy}
          ccQuality={ccQuality}
          ccQualityRuns={ccQualityRuns}
          inventory={inventory}
          powerScan={powerScan}
          validationSweep={validationSweep}
          p25={p25}
          sweep={sweep}
          nextExperiments={detail.nextExperiments ?? []}
          surveyId={detail.session.id}
          systemShortName={effectiveSystems[0]?.shortName ?? ""}
          systems={effectiveSystems}
          sources={effectiveSources}
          controlChannels={effectiveControlChannels}
          activeControlChannelHz={activeControlChannelHz || effectiveControlChannels[0] || 0}
          setActiveControlChannelHz={setActiveControlChannelHz}
          duration={duration}
          setDuration={setDuration}
          selectedSources={selectedSourceIndexes}
          setSdrSources={() => undefined}
          onSdrTouched={() => undefined}
          onStopAndInventory={runSdrInventoryExperiment}
          onRunP25={runP25}
          onRunExperiment={runExperiment}
          onReload={refreshWorkspace}
          onShowDetails={setDetails}
          onOpenRunLog={() => undefined}
          waterfallSweepSelections={waterfallSweepSelections}
          onWaterfallSweepSelections={updateWaterfallSweepSelections}
          onAcceptSweepCandidate={acceptSweepCandidate}
          inventoryRequired={false}
        />}
        {stage === "coverage" && <SiteSetupRfCoverageStage setup={setup} detail={detail} onServerSetupChanged={onServerSetupChanged} />}
        {stage === "calls" && <SiteSetupRfCallProofStage voiceCapture={voiceCapture} transcriptionGate={transcriptionGate} nextExperiments={detail.nextExperiments ?? []} busy={busy === "call_transcription_proof"} onRunProof={runCallAndTranscriptionProof} />}
        {stage === "verdict" && <SiteSetupRfVerdictStage detail={detail} stabilityVerdict={stabilityVerdict} busy={busy} onRunVerdict={() => void runExperiment("stability_verdict", "5 minutes", undefined, { durationSeconds: 300 })} />}
        {details && <div className="modal-backdrop" onClick={() => setDetails(null)}>
          <div className="rf-details-modal" onClick={event => event.stopPropagation()}>
            <div className="settings-header"><h3>{details.title}</h3><button onClick={() => setDetails(null)}>Close</button></div>
            {details.body}
          </div>
        </div>}
      </>
      : !busy && <div className="setup-warning-list"><div>RF Validation needs at least one selected site/control channel and one SDR source.</div></div>}
  </div>;
}

function SetupRfHistoryViewer({ currentSite }: { currentSite: string }) {
  const [history, setHistory] = useState<SetupRfHistory>({ rows: [] });
  const [query, setQuery] = useState("");
  const [currentSiteOnly, setCurrentSiteOnly] = useState(true);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  const [comparison, setComparison] = useState<string[]>([]);
  const [annotation, setAnnotation] = useState<Record<string, string>>({});
  useEffect(() => { void load(); }, [currentSiteOnly, currentSite]);

  async function load() {
    setBusy(true);
    setMessage("");
    try {
      const params = new URLSearchParams({ limit: "150" });
      if (currentSiteOnly && currentSite.trim()) params.set("site", currentSite.trim());
      if (query.trim()) params.set("q", query.trim());
      setHistory(await api.request<SetupRfHistory>(`${siteSetupApi}/rf-history?${params}`));
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Unable to load experiment history.");
    } finally {
      setBusy(false);
    }
  }

  function toggleCompare(id: string, checked: boolean) {
    setComparison(current => checked ? [...current.filter(value => value !== id), id].slice(-2) : current.filter(value => value !== id));
  }

  async function saveAnnotation(row: SetupRfHistoryRow) {
    const text = (annotation[row.experiment.id] || "").trim();
    if (!text) return;
    try {
      await api.request(`${siteSetupRfApi}/${encodeURIComponent(row.session.id)}/annotations`, { method: "POST", body: JSON.stringify({ text: `${row.experiment.name || label(row.experiment.type)} (${row.experiment.id}): ${text}` }) });
      setAnnotation(current => ({ ...current, [row.experiment.id]: "" }));
      setMessage("Annotation saved without changing the measurement.");
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Unable to save annotation.");
    }
  }

  const compared = comparison.map(id => history.rows.find(row => row.experiment.id === id)).filter(Boolean) as SetupRfHistoryRow[];
  return <div className="rf-history-viewer">
    <div className="rf-history-filters">
      <label><span>Search</span><input value={query} onChange={event => setQuery(event.target.value)} onKeyDown={event => { if (event.key === "Enter") void load(); }} placeholder="Name, type, hypothesis, physical change, or result" /></label>
      <label className="compact-toggle"><input type="checkbox" checked={currentSiteOnly} onChange={event => setCurrentSiteOnly(event.currentTarget.checked)} /> Current site only</label>
      <button type="button" disabled={busy} onClick={() => void load()}>{busy ? "Loading..." : "Search"}</button>
    </div>
    {message && <div className={message.toLowerCase().includes("unable") ? "settings-message error" : "settings-message ok"}>{message}</div>}
    {compared.length > 0 && <div className="rf-history-comparison">
      {compared.map(row => <RfHistorySummary row={row} key={row.experiment.id} />)}
    </div>}
    <div className="rf-history-list">
      {history.rows.map(row => <details className="rf-history-row" key={row.experiment.id}>
        <summary>
          <input type="checkbox" aria-label={`Compare ${row.experiment.name || row.experiment.type}`} checked={comparison.includes(row.experiment.id)} onClick={event => event.stopPropagation()} onChange={event => toggleCompare(row.experiment.id, event.currentTarget.checked)} />
          <span><strong>{row.experiment.name || label(row.experiment.type)}</strong><small>{row.session.siteLabel} / {label(row.experiment.type)} / {new Date(row.experiment.createdAtUtc).toLocaleString()}</small></span>
          <span className={`section-status ${row.experiment.status === "passed" ? "ok" : row.experiment.status === "failed" ? "error" : "warning"}`}>{label(row.experiment.status)}</span>
          <span>{row.evidence.length} evidence file{row.evidence.length === 1 ? "" : "s"}</span>
        </summary>
        <RfHistorySummary row={row} />
        <div className="rf-history-annotation"><input value={annotation[row.experiment.id] || ""} onChange={event => setAnnotation(current => ({ ...current, [row.experiment.id]: event.target.value }))} placeholder="Add an operator annotation" /><button type="button" disabled={!(annotation[row.experiment.id] || "").trim()} onClick={() => void saveAnnotation(row)}>Save annotation</button></div>
      </details>)}
      {!busy && history.rows.length === 0 && <div className="setup-note">No experiments match this view.</div>}
    </div>
  </div>;
}

function RfHistorySummary({ row }: { row: SetupRfHistoryRow }) {
  return <div className="rf-history-summary">
    <div><span>Result</span><strong>{row.experiment.resultSummary || row.experiment.blockingIssue || "No summary recorded"}</strong></div>
    <div><span>Hypothesis</span><strong>{row.experiment.hypothesis || "Not recorded"}</strong></div>
    <div><span>Physical change</span><strong>{row.experiment.physicalChange || "None recorded"}</strong></div>
    <div><span>Captured</span><strong>{row.experiment.startedAtUtc ? new Date(row.experiment.startedAtUtc).toLocaleString() : new Date(row.experiment.createdAtUtc).toLocaleString()}</strong></div>
    <div className="rf-history-evidence"><span>Evidence</span>{row.evidence.length ? row.evidence.map(item => <code title={item.contentHash} key={item.id}>{item.filePath} / {formatBytes(item.sizeBytes)} / {item.mediaType}</code>) : <strong>No indexed evidence</strong>}</div>
    <small>RF path revision {row.evidence[0]?.rfPathRevision || "--"} / source plan revision {row.evidence[0]?.sourcePlanRevision || "--"}</small>
  </div>;
}

function SiteSetupRfStageBanner({ stage, detail, setup, validationSweep, voiceCapture, transcriptionGate }: { stage: SiteSetupRfStage; detail: RfSurveyDetail; setup: SiteSetup; validationSweep?: RfSurveyExperiment; voiceCapture?: RfSurveyExperiment; transcriptionGate?: RfSurveyExperiment }) {
  const descriptions: Record<SiteSetupRfStage, string> = {
    preparation: "Confirm selected sites, source hardware, and required tools before taking measurements.",
    spectrum: "Inspect the live spectrum and retain candidate control channels for measured validation.",
    control: "Prove control-channel reception, P25 decoding, and stability across the selected hardware and settings.",
    coverage: "Review which known and observed frequencies each source can monitor before building configuration.",
    calls: "Require real call capture and useful transcription evidence before declaring the setup ready.",
    verdict: "Review the combined RF, coverage, call, transcription, and stability result."
  };
  const toolReady = detail.toolPrep?.readyForControlChannelTests === true;
  const sourcePlanReady = setup.desired.sourcePlanSystemShortNames.length > 0 && setup.desired.selectedSourceIndexes.length > 0;
  const stageState: Record<SiteSetupRfStage, { text: string; tone: string }> = {
    preparation: detail.profile.sources.length > 0 && toolReady ? { text: "Ready", tone: "ok" } : { text: "Needs review", tone: "warning" },
    spectrum: setup.desired.rfSelections.length > 0 ? { text: "Evidence retained", tone: "ok" } : { text: "Available", tone: "" },
    control: validationSweep ? { text: label(validationSweep.status), tone: validationSweep.status === "passed" ? "ok" : validationSweep.status === "failed" ? "error" : "warning" } : { text: "Not run", tone: "" },
    coverage: detail.session.sourcePlanSummary ? { text: "Applied", tone: "ok" } : sourcePlanReady ? { text: "Ready to review", tone: "warning" } : { text: "Needs inputs", tone: "" },
    calls: transcriptionGate ? { text: label(transcriptionGate.status), tone: transcriptionGate.status === "passed" ? "ok" : "warning" } : voiceCapture ? { text: `Capture ${label(voiceCapture.status)}`, tone: voiceCapture.status === "passed" ? "ok" : "warning" } : { text: "Not proved", tone: "" },
    verdict: detail.session.verdict && detail.session.verdict !== "not_started" ? { text: label(detail.session.verdict), tone: detail.session.verdict.toLowerCase().includes("pass") ? "ok" : "warning" } : { text: "Not ready", tone: "" }
  };
  return <div className="rf-workflow-banner">
    <div className="rf-workflow-heading">
      <div><span>RF Validation</span><strong>{siteSetupRfStages.find(item => item.id === stage)?.label}</strong></div>
      <p>{descriptions[stage]}</p>
    </div>
    <div className="rf-workflow-stage-strip" aria-label="RF Validation stage status">
      {siteSetupRfStages.map((item, index) => <div className={item.id === stage ? "active" : ""} key={item.id}>
        <span>{index + 1}</span>
        <strong>{item.label}</strong>
        <small className={`section-status ${stageState[item.id].tone}`.trim()}>{stageState[item.id].text}</small>
      </div>)}
    </div>
  </div>;
}

function SiteSetupRfPreparationStage({ detail, systems, sources, inventory, inventoryBusy, softwareCheckBusy, onSoftwareCheck, onRunInventory, onShowDetails }: { detail: RfSurveyDetail; systems: RfSurveySystem[]; sources: RfSurveySource[]; inventory?: RfSurveyExperiment; inventoryBusy: boolean; softwareCheckBusy: boolean; onSoftwareCheck: () => void; onRunInventory: () => void; onShowDetails: (value: { title: string; body: React.ReactNode } | null) => void }) {
  const prep = detail.toolPrep;
  const checkComplete = (prep?.tools.length ?? 0) > 0;
  const missingRequired = (prep?.tools ?? []).filter(tool => tool.required && !tool.installed);
  const warnings = checkComplete ? prep?.warnings ?? [] : [];
  const hasIssues = missingRequired.length > 0 || warnings.length > 0 || !prep?.readyForControlChannelTests || !prep?.readyForVoiceCapture || !prep?.readyForTranscriptionGate;
  const softwareStatus = softwareCheckBusy ? "Checking" : checkComplete ? hasIssues ? "Needs attention" : "Ready" : "Not checked";
  return <div className="rf-step-stack rf-stage-summary">
    <div className="rf-stage-status-list">
      <div><span>Setup scope</span><strong>{systems.length} site{systems.length === 1 ? "" : "s"} / {sources.length} source{sources.length === 1 ? "" : "s"}</strong><small>{systems.map(system => system.siteLabel || system.shortName).join(", ") || "Choose sites in Systems & Sites."}</small></div>
      <div><span>Required software</span><strong className={hasIssues && checkComplete ? "warning-text" : ""}>{softwareStatus}</strong><small>{checkComplete ? `Checked ${new Date(prep!.generatedAtUtc).toLocaleString()}` : softwareCheckBusy ? "Checking software availability without accessing SDR hardware." : "The first Preparation visit checks this applied Setup once."}</small></div>
      <div><span>SDR inventory</span><strong>{inventory ? label(inventory.status) : "Not run"}</strong><small>{inventory?.resultSummary || "Hardware detection remains a separate explicit operation."}</small></div>
    </div>
    <div className="rf-primary-actions">
      {checkComplete && hasIssues && <button type="button" className="danger-button" disabled={softwareCheckBusy || inventoryBusy} onClick={onSoftwareCheck}>{softwareCheckBusy ? "Checking..." : "Recheck required software"}</button>}
      {!checkComplete && !softwareCheckBusy && <button type="button" className="danger-button" disabled={inventoryBusy} onClick={onSoftwareCheck}>Retry required software check</button>}
      {!inventory && checkComplete && !hasIssues && <button type="button" className="danger-button" disabled={inventoryBusy || softwareCheckBusy} onClick={onRunInventory}>{inventoryBusy ? "Running..." : "Run SDR Inventory"}</button>}
      {inventory && <button type="button" disabled={inventoryBusy || softwareCheckBusy} onClick={onRunInventory}>{inventoryBusy ? "Running..." : "Rerun SDR Inventory"}</button>}
      {inventory && <button type="button" onClick={() => onShowDetails({ title: "SDR Inventory Details", body: <pre className="log-box">{inventory.evidenceJson}</pre> })}>View Inventory Evidence</button>}
    </div>
    {inventory && <SdrInventorySummary experiment={inventory} />}
    {warnings.length ? <div className="setup-warning-list">{warnings.map(warning => <div key={warning}>{warning}</div>)}</div> : null}
    {checkComplete && <details className="rf-technical-details" open={missingRequired.length > 0}>
      <summary>{missingRequired.length ? `${missingRequired.length} required software issue${missingRequired.length === 1 ? "" : "s"}` : "Software check details"}</summary>
      <div className="rf-tool-status-list compact">
        {prep!.tools.map(tool => <div className="rf-tool-row" key={tool.id}>
          <strong>{tool.label}</strong>
          <span className={`section-status ${tool.installed ? "ok" : tool.required ? "error" : "warning"}`}>{tool.installed ? "Available" : tool.required ? "Required" : "Optional"}</span>
          <span>{tool.installed ? tool.version || tool.purpose : tool.installHint || tool.purpose}</span>
        </div>)}
      </div>
    </details>}
  </div>;
}

function SiteSetupRfCoverageStage({ setup, detail, onServerSetupChanged }: { setup: SiteSetup; detail: RfSurveyDetail; onServerSetupChanged: (next: SiteSetup) => void }) {
  return <div className="rf-step-stack rf-stage-summary">
    <div className="rf-stage-intro">Choose a server-calculated source plan that covers the selected sites with the detected SDR hardware.</div>
    <ServerSourceCoverage setup={setup} onServerSetupChanged={onServerSetupChanged} />
    {detail.session.sourcePlanSummary && <details className="rf-technical-details"><summary>Previously applied source-plan evidence</summary><div className="rf-evidence-line"><span>Recorded plan</span><strong>{detail.session.sourcePlanSummary}</strong></div></details>}
  </div>;
}

function ServerSourceCoverage({ setup, onServerSetupChanged }: { setup: SiteSetup; onServerSetupChanged: (next: SiteSetup) => void }) {
  const [projection, setProjection] = useState<SiteSetupSourcePlanProjection | null>(null);
  const [busy, setBusy] = useState("");
  const [message, setMessage] = useState("");
  useEffect(() => {
    let stopped = false;
    async function load() {
      setBusy("load");
      setMessage("");
      try {
        const next = await api.request<SiteSetupSourcePlanProjection>(`${siteSetupApi}/source-plan`);
        if (!stopped) setProjection(next);
      } catch (error) {
        if (!stopped) setMessage(error instanceof Error ? error.message : "Unable to calculate Source Coverage.");
      } finally {
        if (!stopped) setBusy("");
      }
    }
    void load();
    return () => { stopped = true; };
  }, [setup.desired.desiredVersion]);

  async function select(option: SiteSetupSourcePlanOption) {
    if (!projection || !option.fits) return;
    setBusy(option.id);
    setMessage("");
    try {
      const next = await api.request<SiteSetup>(`${siteSetupApi}/source-plan/select`, {
        method: "POST",
        body: JSON.stringify({
          expectedVersion: setup.desired.desiredVersion,
          projectionVersion: projection.projectionVersion,
          optionId: option.id,
          sampleRateHz: projection.sampleRateHz,
          sourceAssignments: option.sourceAssignments
        })
      });
      onServerSetupChanged(next);
      setMessage("Source plan saved for final review.");
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Unable to select the source plan.");
    } finally {
      setBusy("");
    }
  }

  if (busy === "load" && !projection) return <StepProgressIndicator label="Calculating Source Coverage on the server" />;
  if (!projection) return <div className="settings-message error">{message || "Source Coverage is unavailable."}</div>;
  const recommended = projection.options.find(option => option.id === projection.recommendedOptionId);
  const selectedSystems = setup.desired.sourcePlanSystemShortNames;
  const selectedMode = setup.desired.sourcePlanMode;
  return <>
    {projection.warnings.length > 0 && <div className="setup-warning-list">{projection.warnings.map(warning => <div key={warning}>{warning}</div>)}</div>}
    {recommended
      ? <SourcePlanProjectionCard option={recommended} projection={projection} selected={selectedMode === recommended.mode && sameStringSet(selectedSystems, recommended.systemShortNames)} busy={busy === recommended.id} onSelect={select} recommended />
      : <div className="rf-recommended-action blocked"><div><span>Blocking issue</span><strong>No source plan fits</strong><small>Adjust selected sites, SDR hardware, or sample rate before continuing.</small></div></div>}
    {message && <div className={message.toLowerCase().includes("unable") || message.toLowerCase().includes("changed") ? "settings-message error" : "settings-message ok"}>{message}</div>}
    <details className="rf-technical-details"><summary>Alternatives, assumptions, and exact windows</summary>
      <div className="rf-source-projection-list">
        {projection.options.filter(option => option.id !== projection.recommendedOptionId).map(option => <SourcePlanProjectionCard key={option.id} option={option} projection={projection} selected={selectedMode === option.mode && sameStringSet(selectedSystems, option.systemShortNames)} busy={busy === option.id} onSelect={select} />)}
      </div>
      <div className="setup-note">{projection.assumptions.join(" ")} Projection {projection.projectionVersion.slice(0, 12)} tied to Setup version {projection.desiredVersion}.</div>
    </details>
  </>;
}

function SourcePlanProjectionCard({ option, projection, selected, busy, onSelect, recommended = false }: { option: SiteSetupSourcePlanOption; projection: SiteSetupSourcePlanProjection; selected: boolean; busy: boolean; onSelect: (option: SiteSetupSourcePlanOption) => void; recommended?: boolean }) {
  return <div className={`rf-source-projection-card ${option.fits ? "" : "blocked"} ${selected ? "selected" : ""}`.trim()}>
    <div>
      <span>{recommended ? "Recommended plan" : "Alternative"}</span>
      <strong>{option.label}</strong>
      <small>{option.siteLabels.join(", ")} / {option.mode === "control" ? "control channels only" : "full known frequencies"}</small>
    </div>
    <div className="rf-source-projection-windows">
      {option.windows.map((window, index) => <span key={`${option.id}-${index}`}><strong>Source {option.selectedSourceIndexes[index] ?? index}</strong><code>{formatRfHz(window.centerHz)}</code><small>{formatRfHz(window.lowHz)}-{formatRfHz(window.highHz)} / {window.frequencyCount} frequencies</small></span>)}
    </div>
    <div className={option.fits ? "section-status ok" : "section-status warning"}>{option.reason}</div>
    <button type="button" className={recommended ? "primary" : ""} disabled={!option.fits || selected || busy} onClick={() => void onSelect(option)}>{busy ? "Saving..." : selected ? "Selected" : "Select plan"}</button>
    <small>{option.coveredFrequenciesHz.length} covered / {option.missedFrequenciesHz.length} missed / {(projection.sampleRateHz / 1_000_000).toFixed(3).replace(/0+$/, "").replace(/\.$/, "")} MHz</small>
  </div>;
}

function SiteSetupRfCallProofStage({ voiceCapture, transcriptionGate, nextExperiments, busy, onRunProof }: { voiceCapture?: RfSurveyExperiment; transcriptionGate?: RfSurveyExperiment; nextExperiments: RfSurveyExperimentPlan[]; busy: boolean; onRunProof: (rerun?: boolean) => Promise<void> }) {
  const voicePlan = nextExperiments.find(plan => plan.type === "voice_capture_trial");
  const transcriptionPlan = nextExperiments.find(plan => plan.type === "transcription_gate");
  const nextPlan = !voiceCapture || voiceCapture.status !== "passed" ? voicePlan : transcriptionPlan;
  const proofPassed = voiceCapture?.status === "passed" && transcriptionGate?.status === "passed";
  const actionBlocked = nextPlan?.enabled === false;
  return <div className="rf-step-stack rf-stage-summary">
    <div className={`rf-recommended-action ${actionBlocked ? "blocked" : ""}`.trim()}>
      <div>
        <span>{actionBlocked ? "Current blocker" : proofPassed ? "Proof complete" : "Required next task"}</span>
        <strong>{actionBlocked ? "Resolve the prerequisite before running proof" : proofPassed ? "Call and transcription proof passed" : "Run call and transcription proof"}</strong>
        <small>{nextPlan?.blockingIssue || (proofPassed ? "Both required checks passed. Continue to Verdict." : "PizzaWave listens for calls for up to 3 minutes, then checks completed transcription. Monitoring stays active. Allow about 5 minutes total.")}</small>
      </div>
    </div>
    <div className="rf-stage-status-list">
      <div><span>Call audio</span><strong>{voiceCapture ? label(voiceCapture.status) : "Not run"}</strong><small>{voiceCapture?.resultSummary || voiceCapture?.blockingIssue || "PizzaWave will wait for live call audio."}</small></div>
      <div><span>Transcription</span><strong>{transcriptionGate ? label(transcriptionGate.status) : "Not run"}</strong><small>{transcriptionGate?.resultSummary || transcriptionGate?.blockingIssue || "Runs automatically after call audio passes."}</small></div>
    </div>
    <div className="rf-primary-actions">
      <button type="button" className="primary" disabled={busy || actionBlocked} onClick={() => void onRunProof(proofPassed)}>{busy ? "Running proof..." : proofPassed ? "Rerun proof" : voiceCapture?.status === "passed" ? "Continue transcription proof" : "Run call & transcription proof"}</button>
    </div>
    {(voiceCapture || transcriptionGate) && <details className="rf-technical-details"><summary>Proof evidence</summary>
      {voiceCapture && <ExperimentSummary experiment={voiceCapture} />}
      {transcriptionGate && <ExperimentSummary experiment={transcriptionGate} />}
    </details>}
  </div>;
}

function SiteSetupRfVerdictStage({ detail, stabilityVerdict, busy, onRunVerdict }: { detail: RfSurveyDetail; stabilityVerdict?: RfSurveyExperiment; busy: string; onRunVerdict: () => void }) {
  const verdict = detail.session.verdict && detail.session.verdict !== "not_started" ? label(detail.session.verdict) : "Not ready";
  const stability = detail.session.stability && detail.session.stability !== "unknown" ? label(detail.session.stability) : "Not proved";
  const blockers = Array.from(new Set(detail.nextExperiments.filter(plan => !plan.enabled && plan.blockingIssue).map(plan => plan.blockingIssue)));
  const verdictPlan = detail.nextExperiments.find(plan => plan.type === "stability_verdict");
  return <div className="rf-step-stack rf-stage-summary">
    <div className="rf-verdict-summary">
      <div><span>Verdict</span><strong>{verdict}</strong></div>
      <div><span>Stability</span><strong>{stability}</strong></div>
    </div>
    {blockers.length > 0
      ? <div className="rf-recommended-action blocked"><div><span>Next blocker</span><strong>Complete the next required proof</strong><small>{blockers[0]}</small></div></div>
      : <div className="rf-recommended-action"><div><span>Next task</span><strong>{stabilityVerdict?.status === "passed" ? "Verdict complete" : "Compute the stability verdict"}</strong><small>{stabilityVerdict?.resultSummary || verdictPlan?.purpose || "No server-declared blockers remain."}</small></div>{stabilityVerdict?.status !== "passed" && <button type="button" className="primary" disabled={!verdictPlan?.enabled || Boolean(busy)} onClick={onRunVerdict}>{busy === "stability_verdict" ? "Computing..." : "Compute Verdict"}</button>}</div>}
    {blockers.length > 1 && <details className="rf-technical-details"><summary>{blockers.length - 1} additional blocker{blockers.length === 2 ? "" : "s"}</summary><div className="setup-warning-list">{blockers.slice(1).map(blocker => <div key={blocker}>{blocker}</div>)}</div></details>}
    {stabilityVerdict && <details className="rf-technical-details"><summary>Verdict evidence</summary><ExperimentSummary experiment={stabilityVerdict} /></details>}
    <small className="muted">Updated {new Date(detail.session.updatedAtUtc).toLocaleString()}</small>
  </div>;
}

async function prepareSiteSetupRfWorkspace() {
  return api.request<RfSurveyDetail>(siteSetupRfApi);
}

function SiteSetupApplySection({ setup, subPage, setSubPage, onSave, onSetupChanged, onApplied }: { setup: SiteSetup; subPage: "source" | "review"; setSubPage: (value: "source" | "review") => void; onSave: (patch: Partial<SiteSetupConfig>, field: string) => Promise<void>; onSetupChanged: (next: SiteSetup) => void; onApplied: (next: SiteSetup) => void }) {
  const [detail, setDetail] = useState<RfSurveyDetail | null>(null);
  const [draft, setDraft] = useState<RfSurveyConfigDraft | null>(null);
  const [busy, setBusy] = useState<"" | "load" | "save-plan" | "apply" | "discard">("load");
  const [message, setMessage] = useState("");
  const systems = siteSetupSystems(setup);
  const sources = siteSetupSources(setup);
  const defaultSelectedSources = setup.desired.selectedSourceIndexes.length ? setup.desired.selectedSourceIndexes : sources.map(source => source.index);
  const defaultSourcePlanSystems = setup.desired.sourcePlanSystemShortNames.length
    ? setup.desired.sourcePlanSystemShortNames
    : selectedSetupSystemNames(setup);
  const [selectedSourceIndexes, setSelectedSourceIndexes] = useState<number[]>(defaultSelectedSources);
  const [sourcePlanSystems, setSourcePlanSystems] = useState<string[]>(defaultSourcePlanSystems);
  const [sourcePlanMode, setSourcePlanMode] = useState<"full" | "control">(setup.desired.sourcePlanMode === "control" ? "control" : "full");
  const [sourceAssignments, setSourceAssignments] = useState<Record<string, number>>(() => normalizeSourceAssignmentsForUi(setup.desired.sourceAssignments, systems, sources));
  const [sdrSources, setSdrSources] = useState<RfSurveySource[] | null>(null);
  const signature = JSON.stringify({
    siteLabel: setup.desired.siteLabel,
    sids: setupRadioReferenceSids(setup),
    systems,
    sources,
    selectedSourceIndexes,
    sourceAssignments,
    sourcePlanSystems,
    sourcePlanMode,
    sdrSources,
    rfPath: setup.desired.rfPath,
    desiredVersion: setup.desired.desiredVersion
  });
  const parsedDraft = useMemo(() => {
    try {
      return draft?.configJson ? JSON.parse(draft.configJson) : null;
    } catch {
      return null;
    }
  }, [draft?.configJson]);
  const parsedLiveDraft = useMemo(() => {
    try {
      return draft?.liveConfigJson ? JSON.parse(draft.liveConfigJson) : null;
    } catch {
      return null;
    }
  }, [draft?.liveConfigJson]);
  const draftSystems = Array.isArray(parsedDraft?.systems) ? parsedDraft.systems : [];
  const draftSources = Array.isArray(parsedDraft?.sources) ? parsedDraft.sources : [];
  const draftWarnings = useMemo(() => siteSetupDraftWarningGroups(draft?.summary.warnings ?? []), [draft?.summary.warnings]);
  const draftReviewRows = useMemo(() => buildSiteSetupConfigReviewRows(parsedLiveDraft, parsedDraft), [parsedLiveDraft, parsedDraft]);
  const discardInProgressRef = useRef(false);
  const callProofBlockers = useMemo(() => {
    if (!detail) return ["RF Validation has not loaded yet."];
    const latest = (type: string) => [...detail.experiments]
      .filter(experiment => experiment.type === type)
      .sort((a, b) => (b.createdAtUtc || "").localeCompare(a.createdAtUtc || ""))[0];
    const blockers: string[] = [];
    if (latest("voice_capture_trial")?.status !== "passed") blockers.push("Call capture proof must pass in RF Validation Step 5.");
    if (latest("transcription_gate")?.status !== "passed") blockers.push("Transcription proof must pass in RF Validation Step 5.");
    return blockers;
  }, [detail?.session.id, detail?.experiments]);

  useEffect(() => {
    setSelectedSourceIndexes(defaultSelectedSources);
    setSourcePlanSystems(defaultSourcePlanSystems);
    setSourcePlanMode(setup.desired.sourcePlanMode === "control" ? "control" : "full");
    setSourceAssignments(normalizeSourceAssignmentsForUi(setup.desired.sourceAssignments, systems, sources));
    setSdrSources(null);
  }, [setup.desired.desiredVersion]);
  useEffect(() => {
    let stopped = false;
    async function loadWorkspace() {
      setBusy("load");
      setMessage("");
      try {
        if (discardInProgressRef.current)
          return;
        if (subPage === "review") {
          await saveSourcePlan();
          if (stopped || discardInProgressRef.current)
            return;
        }
        const workspace = await prepareSiteSetupRfWorkspace();
        if (!stopped) {
          setDetail(workspace);
          if (subPage === "review") {
            const nextDraft = await api.request<RfSurveyConfigDraft>(`${siteSetupRfApi}/${encodeURIComponent(workspace.session.id)}/config-draft`);
            if (!stopped)
              setDraft(nextDraft);
          }
        }
      } catch (error) {
        if (!stopped)
          setMessage(error instanceof Error ? error.message : "Unable to prepare Setup source planner.");
      } finally {
        if (!stopped)
          setBusy("");
      }
    }
    void loadWorkspace();
    return () => { stopped = true; };
  }, [signature, subPage]);

  useEffect(() => {
    if (subPage !== "review" || draft || busy !== "") return;
    void reloadDraft();
  }, [subPage]);

  type SetupSourcePlanState = {
    sourcePlanSystemShortNames: string[];
    sourcePlanMode: "full" | "control";
    selectedSourceIndexes: number[];
    sourceAssignments: Record<string, number>;
    sources: RfSurveySource[];
  };

  async function saveSourcePlan(overrides?: SetupSourcePlanState) {
    const nextPlan = {
      sourcePlanSystemShortNames: overrides?.sourcePlanSystemShortNames ?? (sourcePlanSystems.length ? sourcePlanSystems : selectedSetupSystemNames(setup)),
      sourcePlanMode: overrides?.sourcePlanMode ?? sourcePlanMode,
      selectedSourceIndexes: overrides?.selectedSourceIndexes ?? (selectedSourceIndexes.length ? selectedSourceIndexes : sources.map(source => source.index)),
      sourceAssignments: overrides?.sourceAssignments ?? sourceAssignments,
      sources: overrides?.sources ?? (sdrSources ?? sources)
    };
    const currentPlan = {
      sourcePlanSystemShortNames: setup.desired.sourcePlanSystemShortNames,
      sourcePlanMode: setup.desired.sourcePlanMode,
      selectedSourceIndexes: setup.desired.selectedSourceIndexes,
      sourceAssignments: setup.desired.sourceAssignments,
      sources: setup.desired.sources
    };
    if (JSON.stringify(nextPlan) === JSON.stringify(currentPlan))
      return setup;
    await onSave(nextPlan, "sourcePlan");
    return setup;
  }

  async function reloadDraft() {
    setBusy("load");
    setMessage("");
    try {
      await saveSourcePlan();
      const workspace = await prepareSiteSetupRfWorkspace();
      const nextDraft = await api.request<RfSurveyConfigDraft>(`${siteSetupRfApi}/${encodeURIComponent(workspace.session.id)}/config-draft`);
      setDetail(workspace);
      setDraft(nextDraft);
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Unable to refresh Setup config draft.");
    } finally {
      setBusy("");
    }
  }

  async function reviewSourcePlan() {
    setBusy("save-plan");
    setMessage("");
    try {
      await reloadDraft();
      setSubPage("review");
    } finally {
      setBusy("");
    }
  }

  async function reviewAppliedSourcePlan(plan: SetupSourcePlanState) {
    setBusy("save-plan");
    setMessage("");
    try {
      await saveSourcePlan(plan);
      const workspace = await prepareSiteSetupRfWorkspace();
      const nextDraft = await api.request<RfSurveyConfigDraft>(`${siteSetupRfApi}/${encodeURIComponent(workspace.session.id)}/config-draft`);
      setDetail(workspace);
      setDraft(nextDraft);
      setSubPage("review");
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Unable to save source plan.");
    } finally {
      setBusy("");
    }
  }

  async function applyDraft() {
    if (!detail || !draft) return;
    if (!confirmAction("Apply Site Setup config?", "This writes the reviewed TR config, creates the normal backup, restarts trunk-recorder, and records a Setup audit event.")) return;
    setBusy("apply");
    setMessage("");
    try {
      const response = await api.request<RfSurveyApplySourceDraftResponse>(`${siteSetupRfApi}/${encodeURIComponent(detail.session.id)}/tr/apply-source-draft`, {
        method: "POST",
        body: JSON.stringify({
          expectedVersion: draft.desiredVersion,
          draftHash: draft.draftHash,
          restartTr: true,
          preserveRfValidationEvidence: true
        })
      });
      onApplied(response.setup);
      setMessage(`${response.result.message}${response.result.serviceOutput ? ` ${response.result.serviceOutput.trim()}` : ""}`);
      setDraft(null);
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Unable to apply Site Setup config.");
    } finally {
      setBusy("");
    }
  }

  async function discardPendingChanges() {
    if (setup.pendingChanges.length === 0) {
      setMessage("Setup has no pending changes to discard.");
      return;
    }
    if (!confirmAction("Discard pending Setup changes?", "This resets Setup's desired state to the current live TR config. TR will not be restarted.")) return;
    discardInProgressRef.current = true;
    setBusy("discard");
    setMessage("");
    setSubPage("source");
    setDraft(null);
    setDetail(null);
    try {
      const next = await api.request<SiteSetup>(`${siteSetupApi}/discard`, {
        method: "POST",
        body: JSON.stringify({
          summary: "Discarded pending Site Setup changes and reset desired state from live TR config.",
          source: "ui"
        })
      });
      onSetupChanged(next);
      setMessage("Discarded pending Setup changes.");
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Unable to discard pending Setup changes.");
    } finally {
      discardInProgressRef.current = false;
      setBusy("");
    }
  }

  return <div className="site-setup-form site-setup-apply">
    <div className="site-setup-apply-toolbar">
      {subPage === "review" && <button type="button" onClick={() => void reloadDraft()} disabled={busy !== ""}>{busy === "load" ? "Refreshing..." : "Refresh Draft"}</button>}
      {subPage === "review" && <button type="button" onClick={() => void discardPendingChanges()} disabled={busy !== "" || setup.pendingChanges.length === 0}>{busy === "discard" ? "Discarding..." : "Discard"}</button>}
      {subPage === "review" && callProofBlockers.length > 0 && <div className="settings-message error" role="status"><strong>Apply blocked.</strong> {callProofBlockers.join(" ")}</div>}
      {subPage === "review" && <button type="button" className="danger-button" onClick={() => void applyDraft()} disabled={busy !== "" || !detail || !draft || callProofBlockers.length > 0}>{busy === "apply" ? "Applying..." : "Apply & Resume Monitoring"}</button>}
    </div>
    {message && <div className={message.toLowerCase().includes("unable") || message.toLowerCase().includes("fail") || message.toLowerCase().includes("denied") ? "settings-message error" : "settings-message ok"}>{message}</div>}
    {subPage === "source" && detail && <SourcePlannerStep
      profile={detail.profile}
      experiments={detail.experiments}
      selectedSources={selectedSourceIndexes}
      setSelectedSources={setSelectedSourceIndexes}
      sourcePlanSystems={sourcePlanSystems}
      setSourcePlanSystems={setSourcePlanSystems}
      sourcePlanMode={sourcePlanMode}
      setSourcePlanMode={setSourcePlanMode}
      sourceAssignments={sourceAssignments}
      setSourceAssignments={setSourceAssignments}
      setSdrSources={setSdrSources}
      onSdrTouched={() => undefined}
      showSampleRateControl={false}
      onPlanApplied={plan => void reviewAppliedSourcePlan(plan)}
      onPlanSelected={() => undefined}
    />}
    {subPage === "source" && !detail && busy !== "load" && <div className="setup-warning-list"><div>Setup needs selected systems, control channels, and SDR source information before it can plan sources.</div></div>}
    {subPage === "review" && draft && <>
      {draftWarnings.coverage.length > 0 && <div className="setup-warning-list site-setup-draft-notes">{draftWarnings.coverage.map(warning => <div key={warning}>{warning}</div>)}</div>}
      <SiteSetupConfigReviewTable rows={draftReviewRows} />
      {parsedDraft && <TrConfigReviewCoverage systems={draftSystems} sources={draftSources} />}
      <details className="site-setup-config-json">
        <summary>TR config JSON</summary>
        <pre className="log-box">{draft.configJson}</pre>
      </details>
    </>}
    {subPage === "review" && !draft && busy !== "load" && <div className="setup-warning-list"><div>Setup needs selected systems, control channels, and SDR source information before it can build a TR config draft.</div></div>}
  </div>;
}

function siteSetupDraftWarningGroups(warnings: string[]) {
  const coverage: string[] = [];
  const planning: string[] = [];
  const other: string[] = [];
  for (const warning of warnings) {
    const voice = /^(.+): no imported voice channel list was available; Config Draft is using (\d+) observed PizzaWave call frequenc/.exec(warning);
    if (voice) {
      planning.push(`${voice[1]}: no RadioReference voice-channel list is stored, so Setup is estimating source coverage from ${voice[2]} recently observed call frequenc${voice[2] === "1" ? "y" : "ies"}.`);
      continue;
    }
    const windows = /^The selected (?:site|systems) need[s]? (\d+) source window\(s\) at (\d+) sps, but only (\d+) source\(s\) are selected\./.exec(warning);
    if (windows) {
      coverage.push(`Source coverage warning: the selected systems span ${windows[1]} SDR tuning window${windows[1] === "1" ? "" : "s"} at ${formatHz(Number(windows[2]))}, but Setup has ${windows[3]} selected source${windows[3] === "1" ? "" : "s"}. Add/select another source, reduce the selected systems, or use control-channel-only planning if full voice-frequency coverage is not the goal.`);
      continue;
    }
    if (warning.includes("Control-channel source plan selected")) {
      planning.push("Control-channel-only planning is selected. Setup will center sources around validated control channels; voice traffic may still need a wider full-coverage plan later.");
      continue;
    }
    other.push(warning);
  }
  return { coverage, planning, other };
}

type SiteSetupConfigReviewRow = {
  area: string;
  setting: string;
  current: string;
  next: string;
  tone?: "added" | "removed" | "changed";
};

function SiteSetupConfigReviewTable({ rows }: { rows: SiteSetupConfigReviewRow[] }) {
  return <div className="site-setup-config-review-table">
    <table className="table compact-table">
      <thead><tr><th>Area</th><th>Setting</th><th>Current</th><th>Will apply</th></tr></thead>
      <tbody>
        {rows.length
          ? rows.map((row, index) => <tr className={row.tone ?? "changed"} key={`${row.area}-${row.setting}-${index}`}>
            <td>{row.area}</td>
            <td>{siteSetupSettingLabel(row.setting)}</td>
            <td><code>{row.current}</code></td>
            <td><code>{row.next}</code></td>
          </tr>)
          : <tr><td colSpan={4}>No TR config changes detected.</td></tr>}
      </tbody>
    </table>
  </div>;
}

function buildSiteSetupConfigReviewRows(current: any, next: any): SiteSetupConfigReviewRow[] {
  if (!current || !next)
    return [];
  const rows: SiteSetupConfigReviewRow[] = [];
  const add = (area: string, setting: string, before: unknown, after: unknown, kind?: SiteSetupConfigReviewRow["tone"]) => {
    const currentText = formatSiteSetupConfigValue(setting, before);
    const nextText = formatSiteSetupConfigValue(setting, after);
    if (currentText !== nextText)
      rows.push({ area, setting, current: currentText, next: nextText, tone: kind ?? "changed" });
  };

  for (const field of ["ver", "defaultMode", "logLevel", "frequencyFormat", "statusAsString", "broadcastSignals", "audioStreaming", "controlRetuneLimit", "controlWarnRate", "logFile", "logDir", "tempDir"])
    add("Root", field, current[field], next[field]);

  const currentSources = Array.isArray(current.sources) ? current.sources : [];
  const nextSources = Array.isArray(next.sources) ? next.sources : [];
  for (let index = 0; index < Math.max(currentSources.length, nextSources.length); index++) {
    const before = currentSources[index];
    const after = nextSources[index];
    const area = `Source #${index}`;
    if (!before && after) {
      rows.push({ area, setting: "Source", current: "Not present", next: formatSiteSetupSourceSummary(after), tone: "added" });
      continue;
    }
    if (before && !after) {
      rows.push({ area, setting: "Source", current: formatSiteSetupSourceSummary(before), next: "Removed", tone: "removed" });
      continue;
    }
    if (!before || !after)
      continue;
    for (const field of ["device", "driver", "rate", "center", "error", "gain", "lnaGain", "mixGain", "ifGain", "digitalRecorders", "analogRecorders", "agc"])
      add(area, field, before[field], after[field]);
  }

  const currentSystems = mapByShortName(current.systems);
  const nextSystems = mapByShortName(next.systems);
  for (const key of sortedUnion(Object.keys(currentSystems), Object.keys(nextSystems))) {
    const before = currentSystems[key];
    const after = nextSystems[key];
    const area = `System ${key}`;
    if (!before && after) {
      rows.push({ area, setting: "System", current: "Not present", next: "Added", tone: "added" });
      add(area, "control channels", [], after.control_channels);
      add(area, "voice channels", [], after.channels);
      add(area, "talkgroups file", undefined, after.talkgroupsFile);
      continue;
    }
    if (before && !after) {
      rows.push({ area, setting: "System", current: "Present", next: "Removed", tone: "removed" });
      continue;
    }
    if (!before || !after)
      continue;
    add(area, "control channels", before.control_channels, after.control_channels);
    add(area, "voice channels", before.channels, after.channels);
    for (const field of ["type", "modulation", "talkgroupsFile", "multiSite", "recordUnknown", "recordUUVCalls", "hideEncrypted", "hideUnknownTalkgroups", "minDuration", "minTransmissionDuration", "talkgroupDisplayFormat"])
      add(area, field, before[field], after[field]);
  }

  add("Plugins", "callstream streams", siteSetupCallstreamStreams(current), siteSetupCallstreamStreams(next));
  return rows;
}

function siteSetupSettingLabel(setting: string) {
  return setting.toLowerCase() === "error" ? "Frequency correction" : setting;
}

function formatSiteSetupSourceSummary(source: any) {
  const parts = [
    source?.device || "source",
    source?.center ? `center ${formatRfHz(readTrFrequencyHz(source.center))}` : "",
    source?.rate ? `rate ${formatHz(Number(source.rate))}` : "",
    source?.error !== undefined ? `frequency correction ${formatSignedHz(Number(source.error))}` : "",
    siteSetupSourceGainText(source)
  ].filter(Boolean);
  return parts.join(" / ") || "Source";
}

function siteSetupSourceGainText(source: any) {
  if (source?.gain !== undefined && source?.gain !== null && String(source.gain).trim())
    return `gain ${source.gain}`;
  const stage = ["lnaGain", "mixGain", "ifGain"]
    .filter(field => source?.[field] !== undefined && source?.[field] !== null)
    .map(field => `${field.replace("Gain", "")} ${source[field]}`);
  return stage.length ? `gain ${stage.join(", ")}` : "";
}

function siteSetupCallstreamStreams(root: any) {
  const plugins = Array.isArray(root?.plugins) ? root.plugins : [];
  const callstream = plugins.find((plugin: any) => String(plugin?.name ?? "").toLowerCase() === "callstream");
  const streams = Array.isArray(callstream?.streams) ? callstream.streams : [];
  return streams.map((stream: any) => `${stream?.shortName ?? "unknown"}:${stream?.TGID ?? 0}`).sort();
}

function formatSiteSetupConfigValue(setting: string, value: unknown): string {
  if (value === undefined || value === null || value === "")
    return "Not set";
  if (Array.isArray(value)) {
    if (value.length === 0)
      return "None";
    if (setting.toLowerCase().includes("channel"))
      return value.map(item => formatRfHz(readTrFrequencyHz(item))).join(", ");
    return value.map(item => formatSiteSetupConfigValue(setting, item)).join(", ");
  }
  if (typeof value === "boolean")
    return value ? "Yes" : "No";
  if (typeof value === "number") {
    const lower = setting.toLowerCase();
    if (lower.includes("center") || lower.includes("channel"))
      return formatRfHz(readTrFrequencyHz(value));
    if (lower === "rate")
      return formatHz(value);
    if (lower.includes("error"))
      return `${value >= 0 ? "+" : ""}${value} Hz`;
    return String(value);
  }
  if (typeof value === "object")
    return JSON.stringify(value);
  return String(value);
}

function sdrTypeFromDeviceLabel(device: string) {
  const value = (device || "").toLowerCase();
  if (value.includes("airspy")) return "Airspy";
  if (value.includes("rtl")) return "RTL-SDR";
  return value ? "SDR" : "";
}

function siteSetupSystems(setup: SiteSetup): RfSurveySystem[] {
  return setup.desired.systems.length
    ? setup.desired.systems
    : selectedSetupSystemNames(setup).map(name => ({ shortName: name, siteLabel: name, controlChannelsHz: [], voiceFrequenciesHz: [] }));
}

function siteSetupSources(setup: SiteSetup): RfSurveySource[] {
  const sources = setup.desired.sources.length
    ? setup.desired.sources
    : setup.applied.sources.map(source => ({
      index: source.index,
      device: source.device,
      serial: source.serial,
      sdrType: sdrTypeFromDeviceLabel(source.device),
      centerHz: source.centerHz,
      sampleRate: source.sampleRate,
      errorHz: source.errorHz,
      gain: source.gain
    }));
  return sources.map(source => ({
    ...source,
    gain: normalizeSetupWaterfallGain(source)
  }));
}

function setupSourcesFromSdrDetection(setup: SiteSetup, detection: SetupSdrDetection): RfSurveySource[] {
  const devices = detection.devices ?? [];
  if (!devices.length)
    return siteSetupSources(setup);
  const existing = siteSetupSources(setup);
  const systems = siteSetupSystems(setup);
  const fallbackCenterHz = existing.find(source => source.centerHz > 0)?.centerHz
    ?? systems.flatMap(system => system.controlChannelsHz).find(frequency => frequency > 0)
    ?? setup.applied.controlChannelsHz.find(frequency => frequency > 0)
    ?? 0;
  return devices.map(device => {
    const sdrType = setupSdrTypeLabel(device.type, device.deviceArgs);
    const matching = findExistingSetupSourceForDevice(existing, device, sdrType);
    const deviceArgs = device.deviceArgs || matching?.device || defaultSetupSdrDeviceArgs(device, sdrType);
    const defaultSampleRate = Number(device.defaultSampleRate) > 0 ? Number(device.defaultSampleRate) : isAirspyRfSource({ sdrType, device: deviceArgs }) ? 6_000_000 : 2_400_000;
    const gain = matching?.gain || device.defaultGain || (isAirspyRfSource({ sdrType, device: deviceArgs }) ? "15" : "32");
    return {
      index: Number.isFinite(Number(device.index)) ? Number(device.index) : devices.indexOf(device),
      device: deviceArgs,
      serial: device.serial || matching?.serial || "",
      sdrType,
      centerHz: matching?.centerHz || fallbackCenterHz,
      sampleRate: matching?.sampleRate || defaultSampleRate,
      errorHz: matching?.errorHz ?? 0,
      gain: normalizeSetupWaterfallGain({ gain, sdrType, device: deviceArgs })
    };
  }).sort((a, b) => a.index - b.index);
}

function setupSdrTypeLabel(type: string, deviceArgs: string) {
  const value = `${type || ""} ${deviceArgs || ""}`.toLowerCase();
  if (value.includes("airspy")) return "Airspy";
  if (value.includes("rtl")) return "RTL-SDR";
  return type || "SDR";
}

function findExistingSetupSourceForDevice(sources: RfSurveySource[], device: SetupSdrDetection["devices"][number], sdrType: string) {
  const serial = (device.serial || "").trim();
  if (serial) {
    const serialMatch = sources.find(source => source.serial && source.serial === serial);
    if (serialMatch)
      return serialMatch;
    const deviceArgMatch = sources.find(source => source.device && source.device.includes(serial));
    if (deviceArgMatch)
      return deviceArgMatch;
  }
  const index = Number(device.index);
  if (Number.isFinite(index)) {
    const indexMatch = sources.find(source => source.index === index);
    if (indexMatch)
      return indexMatch;
  }
  return sources.find(source => setupSdrTypeLabel(source.sdrType, source.device) === sdrType);
}

function defaultSetupSdrDeviceArgs(device: SetupSdrDetection["devices"][number], sdrType: string) {
  const serialOrIndex = device.serial || String(device.index ?? 0);
  return sdrType === "Airspy"
    ? `airspy=${serialOrIndex}`
    : sdrType === "RTL-SDR"
      ? `rtl=${serialOrIndex},buflen=65536`
      : serialOrIndex;
}

function siteSetupRfSurveyRequest(setup: SiteSetup, systems: RfSurveySystem[], sources: RfSurveySource[], selectedSourceIndexes: number[]) {
  const systemNames = systems.map(system => system.shortName).filter(Boolean);
  return {
    systemShortName: systemNames[0] || undefined,
    systemShortNames: systemNames,
    sourcePlanSystemShortNames: setup.desired.sourcePlanSystemShortNames.length ? setup.desired.sourcePlanSystemShortNames : systemNames,
    sourcePlanMode: setup.desired.sourcePlanMode || "full",
    systemDefinitions: systems,
    siteLabel: setup.desired.siteLabel || "Site Setup",
    radioReferenceSid: setupRadioReferenceSids(setup).join(",") || undefined,
    mode: "guided",
    groundTruthSource: "site-setup",
    rfPath: normalizeSetupRfPath(setup.desired.rfPath),
    selectedSourceIndexes,
    sourceAssignments: setup.desired.sourceAssignments ?? {},
    sdrSources: sources,
    currentStep: 2,
    measurementMode: "guided",
    probeDurationSeconds: 45
  };
}

function normalizeSetupWaterfallGain(source: Pick<RfSurveySource, "gain" | "sdrType" | "device">) {
  const gain = String(source.gain ?? "").trim();
  if (!isAirspyRfSource(source))
    return gain;
  return validateAirspyLinearityGain(gain) ? gain : "15";
}

function waterfallTrOperationText(status: RfSurveyWaterfallStatus | null | undefined) {
  if (!status)
    return "";
  if (status.trRestartError)
    return `TR restart failed after waterfall: ${status.trRestartError}`;
  if (status.active && status.trWasActive)
    return "TR paused by Setup waterfall";
  return "";
}

function initialTalkgroupSources(setup: SiteSetup): SiteSetupTalkgroupSource[] {
  const systemNames = selectedSetupSystemNames(setup);
  const fallbackSystem = setup.desired.siteLabel || systemNames[0] || "";
  const sids = setupRadioReferenceSids(setup);
  const selectedSystems: RfSurveySystem[] = setup.desired.systems.length
    ? setup.desired.systems
    : systemNames.map(name => ({ shortName: name, siteLabel: name, controlChannelsHz: [], voiceFrequenciesHz: [] }));
  const rows: SiteSetupTalkgroupSource[] = [];
  for (const system of selectedSystems) {
    const sid = radioReferenceSidForSetupSystem(system, sids);
    if (!sid) continue;
    const catalogSystem = catalogSystemForSetupSystem(system, sid, fallbackSystem);
    rows.push({
      key: `source-${system.shortName || system.siteLabel || rows.length}-${sid}-${catalogSystem}`,
      radioReferenceSid: sid,
      catalogSystem,
      siteShortName: system.shortName,
      siteLabel: system.siteLabel || system.shortName
    });
  }
  if (rows.length > 0)
    return rows;
  if (sids.length === 0)
    return [{ key: "source-0", radioReferenceSid: "", catalogSystem: fallbackSystem }];
  return sids.map(sid => ({
    key: `source-${sid}`,
    radioReferenceSid: sid,
    catalogSystem: catalogSystemForRadioReferenceSid(sid, fallbackSystem || `rr-${sid}`)
  }));
}

function catalogSystemForSetupSystem(system: RfSurveySystem, sid: string, fallback: string) {
  const explicit = normalizeTalkgroupSystem(system.talkgroupSystemShortName || "");
  if (explicit && !isGenericTalkgroupSystemName(explicit, sid))
    return explicit;
  return catalogSystemForRadioReferenceSid(sid, system.shortName || system.siteLabel || fallback);
}

function catalogSystemForRadioReferenceSid(sid: string, fallback: string) {
  const cached = readCachedRadioReferenceSites(sid);
  const cachedName = cached?.systemName || "";
  if (cachedName && !isGenericTalkgroupSystemName(cachedName, sid))
    return normalizeTalkgroupSystem(cachedName);
  return `rr-${sid}`;
}

function isGenericTalkgroupSystemName(value: string, sid?: string) {
  const text = normalizeTalkgroupSystem(value);
  if (!text)
    return true;
  if (/^rr-\d+$/i.test(text))
    return !sid || text === `rr-${sid}`;
  if (/^radioreference-sid-\d+$/i.test(text))
    return !sid || text === `radioreference-sid-${sid}`;
  return false;
}

function talkgroupSourceLabel(row: SiteSetupTalkgroupSource) {
  return row.siteLabel || row.siteShortName || (isGenericTalkgroupSystemName(row.catalogSystem, row.radioReferenceSid) ? `RR ${row.radioReferenceSid}` : row.catalogSystem);
}

function talkgroupSourceDetail(row: SiteSetupTalkgroupSource) {
  const system = isGenericTalkgroupSystemName(row.catalogSystem, row.radioReferenceSid) ? "" : row.catalogSystem;
  const rr = row.radioReferenceSid ? `RR ${row.radioReferenceSid}` : "RR --";
  return system ? `${system} / ${rr}` : rr;
}

function parseRadioReferenceSidList(value: string) {
  return Array.from(new Set((value.match(/\d+/g) ?? []).map(row => row.trim()).filter(Boolean)));
}

function radioReferenceSidForSetupSystem(system: Pick<RfSurveySystem, "shortName" | "siteLabel" | "radioReferenceSid" | "talkgroupSystemShortName">, candidateSids: string[]) {
  const explicit = String(system.radioReferenceSid ?? "").trim();
  if (explicit) return explicit;
  const encoded = parseRadioReferenceSidList(system.talkgroupSystemShortName ?? "")[0];
  if (encoded) return encoded;
  const names = [system.shortName, system.siteLabel].map(value => String(value ?? "").trim()).filter(Boolean);
  for (const sid of candidateSids) {
    const cached = readCachedRadioReferenceSites(sid);
    if (cached?.sites.some(site => names.some(name => stringEqualsIgnoreCase(name, site.shortName) || stringEqualsIgnoreCase(name, site.name))))
      return sid;
  }
  return "";
}

function uniqueTalkgroupImportSources(sources: SiteSetupTalkgroupSource[]) {
  const seen = new Set<string>();
  const unique: SiteSetupTalkgroupSource[] = [];
  for (const row of sources) {
    const sid = row.radioReferenceSid.trim();
    const system = normalizeTalkgroupSystem(row.catalogSystem);
    const key = `${sid}:${isGenericTalkgroupSystemName(system, sid) ? "" : system}`;
    if (seen.has(key))
      continue;
    seen.add(key);
    unique.push(row);
  }
  return unique;
}

function SiteSetupTalkgroupsSection({ setup, reload, onSave }: { setup: SiteSetup; reload: () => Promise<void>; onSave: (patch: Partial<SiteSetupConfig>, field: string) => Promise<void> }) {
  const [rrSources, setRrSources] = useState(() => initialTalkgroupSources(setup));
  const [busy, setBusy] = useState("");
  const [message, setMessage] = useState("");
  const [catalogReloadToken, setCatalogReloadToken] = useState(0);
  const setupSourceKey = initialTalkgroupSources(setup).map(row => `${row.siteShortName || row.siteLabel || row.key}:${row.radioReferenceSid}:${row.catalogSystem}`).join("|");
  useEffect(() => {
    const next = initialTalkgroupSources(setup);
    setRrSources(next);
    setMessage("");
  }, [setupSourceKey]);

  function synchronizationSources() {
    return uniqueTalkgroupImportSources(rrSources
      .map(row => ({ ...row, radioReferenceSid: row.radioReferenceSid.trim(), catalogSystem: row.catalogSystem.trim() }))
      .filter(row => row.radioReferenceSid && row.catalogSystem));
  }

  async function saveTalkgroupSystemMapping(imports: TalkgroupCatalogImport[]) {
    const bySid = new Map(imports.map(row => [row.radioReferenceSid.trim(), normalizeTalkgroupSystem(row.systemShortName)] as const));
    if (!setup.desired.systems.length || bySid.size === 0) return;
    let changed = false;
    const systems = setup.desired.systems.map(system => {
      const sid = String(system.radioReferenceSid || "").trim();
      const talkgroupSystemShortName = sid ? bySid.get(sid) || "" : "";
      if (!talkgroupSystemShortName || stringEqualsIgnoreCase(system.talkgroupSystemShortName, talkgroupSystemShortName))
        return system;
      changed = true;
      return { ...system, talkgroupSystemShortName };
    });
    if (changed)
      await onSave({ systems }, "systems");
  }

  async function synchronizeTalkgroups(forceRadioReferenceSid = "") {
    const activeSources = synchronizationSources();
    if (activeSources.length === 0) {
      setMessage("Select at least one RadioReference system under Systems & Sites first.");
      return;
    }
    const busyKey = forceRadioReferenceSid ? `refresh-${forceRadioReferenceSid}` : "import-rr";
    setBusy(busyKey);
    setMessage("");
    try {
      const result = await api.request<SetupTalkgroupSyncResult>("/api/v1/setup/talkgroups/sync", {
        method: "POST",
        body: JSON.stringify({
          sources: activeSources.map(row => ({
            radioReferenceSid: row.radioReferenceSid,
            systemShortName: isGenericTalkgroupSystemName(row.catalogSystem, row.radioReferenceSid) ? undefined : row.catalogSystem
          })),
          forceRadioReferenceSid: forceRadioReferenceSid || undefined
        })
      });
      const importNames = new Map(result.imports.map(row => [row.radioReferenceSid, row.systemShortName]));
      setRrSources(current => current.map(row => {
        const system = importNames.get(row.radioReferenceSid);
        return system ? { ...row, catalogSystem: system } : row;
      }));
      await saveTalkgroupSystemMapping(result.imports);
      if (result.importedSystems > 0)
        setMessage(result.message);
      setCatalogReloadToken(value => value + 1);
      if (result.importedSystems > 0)
        await reload();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "RadioReference talkgroup synchronization failed.");
    } finally {
      setBusy("");
    }
  }

  const messageClass = message.toLowerCase().includes("fail") || message.toLowerCase().includes("error") || message.toLowerCase().includes("enter") || message.toLowerCase().includes("paste")
    ? "settings-message error"
    : "settings-message ok";

  return <div className="site-setup-form site-setup-talkgroups">
    <div className="site-setup-talkgroup-import">
      <div className="site-setup-source-list">
        {rrSources.map(row => <div className="site-setup-source-chip" key={row.key}>
          <span>{talkgroupSourceLabel(row) || "Unmapped system"}</span>
          <small>{talkgroupSourceDetail(row)}</small>
          <button
            type="button"
            className="icon-button"
            disabled={Boolean(busy)}
            aria-label={`Refresh talkgroups for RR ${row.radioReferenceSid}`}
            title={`Fetch the latest talkgroups for RR ${row.radioReferenceSid}`}
            onClick={() => void synchronizeTalkgroups(row.radioReferenceSid)}
          ><RefreshCw size={14} aria-hidden="true" /></button>
        </div>)}
        {busy === "import-rr" && <span className="settings-message">Loading talkgroups...</span>}
      </div>
      {message && <div className={messageClass}>{message}</div>}
    </div>
    <TalkgroupCatalogSettingsCard
      reloadToken={catalogReloadToken}
      embedded
      allowSystemExclusions
      onCatalogChanged={reload}
    />
  </div>;
}

function DashboardView({ data, rangeHours, reload, focusedIncidentId, focusedHashTarget, clearFocusedIncident, clearFocusedHashTarget, mode, setMode, searchQuery, onSearchChange, hiddenIncidentCount = 0 }: { data: Dashboard | null; rangeHours: number; reload: () => Promise<void>; focusedIncidentId?: number | null; focusedHashTarget?: string; clearFocusedIncident?: () => void; clearFocusedHashTarget?: () => void; mode: DashboardMode; setMode: (mode: DashboardMode) => void; searchQuery: string; onSearchChange: (value: string) => void; hiddenIncidentCount?: number }) {
  const [focusedLocationKey, setFocusedLocationKey] = useState<string | null>(null);
  const [selectedLocation, setSelectedLocation] = useState<LocationHeat | null>(null);
  useEffect(() => {
    setSelectedLocation(null);
    setFocusedLocationKey(null);
  }, [mode]);
  if (!data) return null;
  const incidentLocationMap = buildIncidentLocationMap(data.locationHeat);
  const alertLocationMap = buildAlertLocationMap(data.locationHeat, data.alerts);
  const incidentLocationRows = data.locationHeat.filter(row => row.incidentLinks?.length > 0);
  const alertCallIds = new Set(data.alerts.map(alert => alert.callId));
  const alertLocationRows = data.locationHeat.filter(row => row.callIds?.some(callId => alertCallIds.has(callId)));
  const selectedIncidents = selectedLocation
    ? data.incidents
      .filter(incident => selectedLocation.incidentLinks.some(link => link.incidentId === incident.id))
      .filter(incident => matchesIncidentSearch(incident, searchQuery))
    : [];
  const selectedAlerts = selectedLocation
    ? data.alerts
      .filter(alert => selectedLocation.callIds?.some(callId => callId === alert.callId))
      .filter(alert => matchesAlertSearch(alert, searchQuery))
    : [];
  const mapRows = mode === "alerts" ? alertLocationRows : incidentLocationRows;
  return (
    <div className="dashboard-shell">
      <div className="page-context-bar"><PageSearch value={searchQuery} onChange={onSearchChange} placeholder="Search incidents and alerts" /></div>
      <div className="dashboard dashboard-around">
        <section className="pane dashboard-map-pane">
          <LocationHeatMap key={mode} rows={mapRows} incidents={mode === "incidents" ? data.incidents : []} focusedKey={focusedLocationKey} onFocusKey={setFocusedLocationKey} onSelectLocation={setSelectedLocation} emptyText={mode === "alerts" ? "No geolocated alerts detected in the selected range." : "No geolocated incidents detected in the selected range."} />
        </section>
        <section className="pane dashboard-incidents-pane">
          {selectedLocation
            ? mode === "alerts"
              ? <LocationAlertPanel location={selectedLocation} alerts={selectedAlerts} onClose={() => { setSelectedLocation(null); setFocusedLocationKey(null); }} reload={reload} searchQuery={searchQuery} />
              : <LocationIncidentPanel location={selectedLocation} incidents={selectedIncidents} onClose={() => { setSelectedLocation(null); setFocusedLocationKey(null); }} searchQuery={searchQuery} />
            : mode === "alerts"
              ? <AlertsPanel alerts={data.alerts} locationMap={alertLocationMap} reload={reload} mode={mode} setMode={setMode} incidentCount={data.incidents.length} alertCount={data.alerts.length} searchQuery={searchQuery} hiddenIncidentCount={hiddenIncidentCount} />
              : <Incidents rows={data.incidents} alerts={data.alerts} locationMap={incidentLocationMap} onShowLocation={setFocusedLocationKey} reload={reload} focusedIncidentId={focusedIncidentId} focusedHashTarget={focusedHashTarget} clearFocusedIncident={clearFocusedIncident} clearFocusedHashTarget={clearFocusedHashTarget} mode={mode} setMode={setMode} incidentCount={data.incidents.length} alertCount={data.alerts.length} searchQuery={searchQuery} hiddenIncidentCount={hiddenIncidentCount} />}
        </section>
      </div>
    </div>
  );
}

function locationNodeCount(row: LocationHeat, incidents: Incident[] = []) {
  const incidentCount = row.incidentLinks?.length ?? 0;
  const standaloneCount = standaloneLocationCallIds(row, incidents).length;
  return incidentCount + standaloneCount || row.count;
}

function locationNodeCountLabel(row: LocationHeat, incidents: Incident[] = []) {
  const incidentCount = row.incidentLinks?.length ?? 0;
  const standaloneCount = standaloneLocationCallIds(row, incidents).length;
  const count = incidentCount + standaloneCount || row.count;
  if (incidentCount && standaloneCount)
    return `${incidentCount} incident${incidentCount === 1 ? "" : "s"}, ${standaloneCount} call${standaloneCount === 1 ? "" : "s"}`;
  if (incidentCount)
    return `${incidentCount} incident${incidentCount === 1 ? "" : "s"}`;
  return `${count} call${count === 1 ? "" : "s"}`;
}

function standaloneLocationCallIds(row: LocationHeat, incidents: Incident[]) {
  const linkedIncidentIds = new Set((row.incidentLinks ?? []).map(link => link.incidentId));
  const incidentCallIds = new Set(
    incidents
      .filter(incident => linkedIncidentIds.has(incident.id))
      .flatMap(incident => incident.calls.map(call => call.callId))
  );
  return (row.callIds ?? []).filter(callId => !incidentCallIds.has(callId));
}

function standaloneLocationSourceCalls(row: LocationHeat, incidents: Incident[]) {
  const ids = new Set(standaloneLocationCallIds(row, incidents));
  return (row.sourceCalls ?? []).filter(call => ids.has(call.callId));
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

function buildAlertLocationMap(rows: LocationHeat[], alerts: AlertMatch[]) {
  const alertCallIds = new Set(alerts.map(alert => alert.callId));
  const map = new Map<number, LocationHeat>();
  for (const row of rows) {
    for (const callId of row.callIds ?? []) {
      if (alertCallIds.has(callId) && !map.has(callId))
        map.set(callId, row);
    }
  }
  return map;
}

function Kpi({ label, value, subtext, status = "neutral", onClick }: { label: string; value: string; subtext: string; status?: "ok" | "warning" | "error" | "neutral"; onClick?: () => void }) {
  return <div className={`kpi kpi-${status}${onClick ? " clickable" : ""}`} role={onClick ? "button" : undefined} tabIndex={onClick ? 0 : undefined} onClick={onClick} onKeyDown={e => { if (onClick && (e.key === "Enter" || e.key === " ")) onClick(); }}>
    <div className="label">{label}</div><div className="value">{value}</div><div className="sub">{subtext}</div>
  </div>;
}

function DashboardStatisticsPanel({ data, rangeHours, onRangeHoursChange, onOpenTalkgroup }: { data: Dashboard | null; rangeHours: number; onRangeHoursChange: (hours: number) => void; onOpenTalkgroup: (row: { category: string; talkgroup: number }) => void }) {
  if (!data) return <div className="card">Loading statistics...</div>;
  const activity = data.callActivity;
  const durationHours = Math.max(1, (activity.rangeEnd - activity.rangeStart) / 3600);
  const busiestLabel = activity.busiestBucketStart
    ? new Date(activity.busiestBucketStart * 1000).toLocaleString([], { month: "short", day: "numeric", hour: "numeric", minute: "2-digit" })
    : "--";
  return <div className="dashboard-stats-panel">
    <SystemPageHeaderControls>
      <div className="segmented" role="group" aria-label="Call history window">
        {[{ hours: 24, text: "24h" }, { hours: 48, text: "2d" }, { hours: 168, text: "Week" }].map(option => <button type="button" key={option.hours} className={rangeHours === option.hours ? "active" : ""} onClick={() => onRangeHoursChange(option.hours)}>{option.text}</button>)}
      </div>
    </SystemPageHeaderControls>
    <div className="section kpis">
      <Kpi label="Total Calls" value={activity.totalCalls.toLocaleString()} subtext="Calls in this window" />
      <Kpi label="Calls per Hour" value={(activity.totalCalls / durationHours).toLocaleString(undefined, { maximumFractionDigits: 1 })} subtext="Average across the window" />
      <Kpi label="Unique Talkgroups" value={activity.uniqueTalkgroups.toLocaleString()} subtext="Heard in this window" />
      <Kpi label="Busiest Time Bucket" value={busiestLabel} subtext={activity.busiestBucketCalls ? `${activity.busiestBucketCalls.toLocaleString()} calls in ${activity.bucketSeconds / 60} minutes` : "No calls in this window"} />
    </div>
    <SystemCallBreakdownTable rows={data.callsBySystem ?? []} totalCalls={activity.totalCalls} />
    <CallVolumeTimelineChart rows={data.callVolumeTimeline} rangeStart={activity.rangeStart} rangeEnd={activity.rangeEnd} bucketSeconds={activity.bucketSeconds} />
    <TopCallTalkgroupsChart rows={data.topTalkgroups ?? []} onOpenTalkgroup={onOpenTalkgroup} />
    {rangeHours > 24 && <CallActivityHeatmap rows={data.callVolumeTimeline} rangeStart={activity.rangeStart} rangeEnd={activity.rangeEnd} />}
  </div>;
}

function TopCallTalkgroupsChart({ rows, onOpenTalkgroup }: { rows: TopTalkgroup[]; onOpenTalkgroup: (row: { category: string; talkgroup: number }) => void }) {
  const visible = rows.slice(0, 10);
  const max = Math.max(1, ...visible.map(row => row.count));
  return <div className="card chart-card calls-top-talkgroups">
    <h4>Top Talkgroups by Call Volume</h4>
    {visible.length ? <div className="calls-top-talkgroup-list">{visible.map(row => <button type="button" key={row.talkgroupKey} onClick={() => onOpenTalkgroup(row)} title={`Open ${row.label} in ${label(row.category)}`}>
      <span className="calls-top-talkgroup-label"><strong>{row.label}</strong><small>{row.systemShortName} · TG {row.talkgroup}</small></span>
      <span className="calls-top-talkgroup-bar"><i style={{ width: `${row.count / max * 100}%` }} /></span>
      <span className="calls-top-talkgroup-value"><strong>{row.count.toLocaleString()}</strong><small>{(row.share * 100).toFixed(1)}%</small></span>
    </button>)}</div> : <span className="muted">No talkgroup activity in this window.</span>}
  </div>;
}

function CallActivityHeatmap({ rows, rangeStart, rangeEnd }: { rows: CallVolumeBucket[]; rangeStart: number; rangeEnd: number }) {
  const totals = new Map<number, number>();
  rows.forEach(row => totals.set(row.start, (totals.get(row.start) ?? 0) + row.count));
  const firstHour = rangeStart - rangeStart % 3600;
  const hours = Array.from({ length: Math.max(0, Math.ceil((rangeEnd - firstHour) / 3600)) }, (_, index) => firstHour + index * 3600);
  const dayMap = new Map<string, { label: string; values: Array<{ hour: number; count: number; timestamp: number }> }>();
  hours.forEach(timestamp => {
    const date = new Date(timestamp * 1000);
    const key = `${date.getFullYear()}-${date.getMonth()}-${date.getDate()}`;
    if (!dayMap.has(key)) dayMap.set(key, { label: date.toLocaleDateString([], { weekday: "short", month: "numeric", day: "numeric" }), values: [] });
    dayMap.get(key)!.values.push({ hour: date.getHours(), count: totals.get(timestamp) ?? 0, timestamp });
  });
  const days = Array.from(dayMap.values());
  const max = Math.max(1, ...days.flatMap(day => day.values.map(value => value.count)));
  return <div className="card chart-card calls-activity-heatmap-card">
    <h4>Activity by Day and Hour</h4>
    <div className="calls-activity-heatmap-scroll">
      <div className="calls-activity-heatmap" role="img" aria-label="Hourly call activity heatmap">
        <span />{Array.from({ length: 24 }, (_, hour) => <span className="calls-heatmap-hour" key={hour}>{hour % 6 === 0 ? (hour === 0 ? "12a" : hour === 12 ? "12p" : hour > 12 ? `${hour - 12}p` : `${hour}a`) : ""}</span>)}
        {days.map(day => <React.Fragment key={day.label}><strong>{day.label}</strong>{Array.from({ length: 24 }, (_, hour) => {
          const value = day.values.find(item => item.hour === hour);
          const count = value?.count ?? 0;
          const hourLabel = new Date(2000, 0, 1, hour).toLocaleTimeString([], { hour: "numeric" });
          return <span className="calls-heatmap-cell" key={hour} style={{ backgroundColor: count ? `color-mix(in srgb, var(--accent) ${Math.round((0.16 + count / max * 0.84) * 100)}%, transparent)` : undefined }} title={`${day.label}, ${hourLabel}: ${count.toLocaleString()} calls`} aria-label={`${day.label}, ${hourLabel}: ${count} calls`} />;
        })}</React.Fragment>)}
      </div>
    </div>
    <div className="calls-heatmap-scale muted"><span>Fewer calls</span><i /><i /><i /><i /><span>More calls</span></div>
  </div>;
}

function SystemCallBreakdownTable({ rows, totalCalls }: { rows: NonNullable<Dashboard["callsBySystem"]>; totalCalls: number }) {
  return <div className="card">
    <h4>Calls by Site/System</h4>
    {rows.length ? <table className="table compact-table calls-site-table">
      <thead><tr><th>Site/System</th><th>Calls</th><th>Share</th><th>Talkgroups</th><th>Sources</th><th>Frequency span</th><th>Category mix</th><th>Last heard</th></tr></thead>
      <tbody>{rows.map(row => {
        const categoryText = Object.entries(row.categories ?? {})
          .sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]))
          .slice(0, 4)
          .map(([category, count]) => `${label(category)} ${count.toLocaleString()}`)
          .join(", ");
        const freqText = row.minFrequency > 0 && row.maxFrequency > 0
          ? row.minFrequency === row.maxFrequency
            ? formatRfHz(row.minFrequency)
            : `${formatRfHz(row.minFrequency)}-${formatRfHz(row.maxFrequency)}`
          : "--";
        return <tr key={row.systemShortName || "unknown"}>
          <td><strong>{row.systemShortName || "unknown"}</strong></td>
          <td>{row.calls.toLocaleString()}</td>
          <td>{totalCalls ? `${(row.calls * 100 / totalCalls).toFixed(1)}%` : "--"}</td>
          <td>{row.uniqueTalkgroups.toLocaleString()}</td>
          <td>{row.sources.length ? row.sources.map(source => `#${source}`).join(", ") : "--"}</td>
          <td>{freqText}</td>
          <td>{categoryText || "--"}</td>
          <td>{row.lastHeard ? relativeTime(row.lastHeard) : "--"}</td>
        </tr>;
      })}</tbody>
    </table> : <span className="muted">No calls in the selected range.</span>}
  </div>;
}

function Bars({ title, rows, showTitle = true }: { title: string; rows: BarStat[]; showTitle?: boolean }) {
  return <div className="card">{showTitle && <h4>{title}</h4>}{rows.length ? rows.map(r => <div className="bar-row" key={r.label}><span>{r.label}</span><div className="bar"><span style={{ width: `${Math.round(r.ratio * 100)}%` }} /></div><span>{r.valueText}</span></div>) : <span className="muted">No data</span>}</div>;
}

function CallVolumeTimelineChart({ rows, rangeStart, rangeEnd, bucketSeconds }: { rows: CallVolumeBucket[]; rangeStart: number; rangeEnd: number; bucketSeconds: number }) {
  const firstBucket = rangeStart - rangeStart % bucketSeconds;
  const starts = Array.from({ length: Math.max(0, Math.ceil((rangeEnd - firstBucket) / bucketSeconds)) }, (_, index) => firstBucket + index * bucketSeconds);
  const hasCalls = rows.length > 0;
  const byCategory = categories.map(category => ({ category, values: starts.map(start => rows.find(row => row.start === start && row.category === category)?.count ?? 0) }));
  const max = Math.max(1, ...byCategory.flatMap(c => c.values));
  const points = (values: number[]) => values
    .map((value, index) => `${36 + (starts[index] - firstBucket) / Math.max(1, rangeEnd - firstBucket) * 446},${158 - value / max * 118}`)
    .join(" ");
  const labelTimes = Array.from(new Set([0, .25, .5, .75, 1].map(ratio => Math.round(rangeStart + (rangeEnd - rangeStart) * ratio))));
  const labelX = (timestamp: number) => 36 + (timestamp - rangeStart) / Math.max(1, rangeEnd - rangeStart) * 446;
  const formatTime = (timestamp: number) => new Date(timestamp * 1000).toLocaleString([], rangeEnd - rangeStart <= 86400
    ? { hour: "numeric", minute: "2-digit" }
    : { month: "numeric", day: "numeric", hour: "numeric" });
  return <div className="card chart-card"><h4>Call Volume by Time and Category</h4>{hasCalls ? <div className="chart-with-legend"><svg className="chart calls-timeline-chart" viewBox="0 0 500 196" preserveAspectRatio="xMinYMin meet" role="img" aria-label="Call volume over time by category"><line className="axis" x1="32" y1="158" x2="482" y2="158" /><line className="axis" x1="32" y1="28" x2="32" y2="158" />{labelTimes.map(timestamp => <text className="chart-label" textAnchor={timestamp === rangeStart ? "start" : timestamp === rangeEnd ? "end" : "middle"} x={labelX(timestamp)} y="181" key={timestamp}>{formatTime(timestamp)}</text>)}<text className="chart-label" x="4" y="34">{max}</text>{byCategory.map(series => <polyline key={series.category} fill="none" stroke={categoryColors[series.category]} strokeWidth="2.5" points={points(series.values)} />)}</svg><Legend items={byCategory.map(c => [label(c.category), categoryColors[c.category]])} /></div> : <span className="muted">No calls in this window.</span>}</div>;
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

function PlayableAudio({ src }: { src?: string | null }) {
  const [failed, setFailed] = useState(false);
  useEffect(() => setFailed(false), [src]);
  if (!src || failed) return null;
  return <audio controls preload="metadata" src={src} onError={() => setFailed(true)} />;
}

function LocationHeatMap({ rows, incidents, focusedKey, onFocusKey, onSelectLocation, emptyText = "No geolocated incidents detected in the selected range." }: { rows: LocationHeat[]; incidents: Incident[]; focusedKey?: string | null; onFocusKey?: (key: string | null) => void; onSelectLocation?: (row: LocationHeat | null) => void; emptyText?: string }) {
  const mapRef = useRef<HTMLDivElement | null>(null);
  const dragRef = useRef<MapDragState | null>(null);
  const suppressClickRef = useRef(false);
  const defaultCenter = useMemo(() => defaultMapCenter(rows), [rows]);
  const defaultZoom = useMemo(() => defaultMapZoom(rows), [rows]);
  const areaKey = useMemo(() => Array.from(new Set(rows.map(row => row.areaId))).sort().join("|"), [rows]);
  const lastAreaKey = useRef(areaKey);
  const [mapSize, setMapSize] = useState({ width: 760, height: 520 });
  const [zoom, setZoom] = useState(defaultZoom);
  const [center, setCenter] = useState(defaultCenter);
  const [selected, setSelected] = useState<LocationHeat | null>(null);
  const [dragging, setDragging] = useState(false);
  useEffect(() => {
    const element = mapRef.current;
    if (!element) return;
    const update = () => setMapSize({ width: Math.max(320, element.clientWidth), height: Math.max(260, element.clientHeight) });
    update();
    const observer = new ResizeObserver(update);
    observer.observe(element);
    return () => observer.disconnect();
  }, [rows.length]);
  useEffect(() => {
    if (areaKey === lastAreaKey.current) return;
    lastAreaKey.current = areaKey;
    setCenter(defaultMapCenter(rows));
    setZoom(defaultMapZoom(rows));
    setSelected(null);
  }, [areaKey, rows]);
  useEffect(() => {
    if (!focusedKey) {
      if (selected) {
        setSelected(null);
        setCenter(defaultMapCenter(rows));
        setZoom(defaultMapZoom(rows));
      }
      return;
    }
    const row = rows.find(r => locationKey(r) === focusedKey);
    if (row) focusLocation(row);
  }, [focusedKey]);

  if (!rows.length) {
    return <div className="card location-heat-card">
      <p className="muted">{emptyText}</p>
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
    onSelectLocation?.(row);
  }

  function handleWheel(event: React.WheelEvent<HTMLDivElement>) {
    event.preventDefault();
    event.stopPropagation();
    setZoom(current => Math.max(8, Math.min(14, current + (event.deltaY < 0 ? 1 : -1))));
  }

  function resetMapView() {
    setSelected(null);
    setCenter(defaultMapCenter(rows));
    setZoom(defaultMapZoom(rows));
    onFocusKey?.(null);
    onSelectLocation?.(null);
  }

  function handleMapClick(event: React.MouseEvent<HTMLDivElement>) {
    if (suppressClickRef.current) {
      suppressClickRef.current = false;
      return;
    }
    if (isMapControlTarget(event.target)) return;
    const point = mapEventPoint(event);
    if (!point) return;
    setCenter(worldToLatLon(
      viewport.centerWorldX - viewport.width / 2 + point.x,
      viewport.centerWorldY - viewport.height / 2 + point.y,
      zoom));
    setZoom(current => Math.min(14, current + 1));
  }

  function handlePointerDown(event: React.PointerEvent<HTMLDivElement>) {
    if (event.button !== 0 || isMapControlTarget(event.target)) return;
    const point = mapEventPoint(event);
    if (!point) return;
    dragRef.current = {
      pointerId: event.pointerId,
      startX: point.x,
      startY: point.y,
      centerWorldX: viewport.centerWorldX,
      centerWorldY: viewport.centerWorldY,
      moved: false
    };
    event.currentTarget.setPointerCapture(event.pointerId);
  }

  function handlePointerMove(event: React.PointerEvent<HTMLDivElement>) {
    const drag = dragRef.current;
    if (!drag || drag.pointerId !== event.pointerId) return;
    const point = mapEventPoint(event);
    if (!point) return;
    const dx = point.x - drag.startX;
    const dy = point.y - drag.startY;
    if (!drag.moved && Math.hypot(dx, dy) < 4) return;
    drag.moved = true;
    suppressClickRef.current = true;
    setDragging(true);
    setCenter(worldToLatLon(drag.centerWorldX - dx, drag.centerWorldY - dy, zoom));
  }

  function handlePointerUp(event: React.PointerEvent<HTMLDivElement>) {
    const drag = dragRef.current;
    if (!drag || drag.pointerId !== event.pointerId) return;
    dragRef.current = null;
    setDragging(false);
    try {
      event.currentTarget.releasePointerCapture(event.pointerId);
    } catch {
      // Pointer capture can already be gone if the browser canceled the drag.
    }
    if (drag.moved)
      window.setTimeout(() => { suppressClickRef.current = false; }, 0);
  }

  return <div className="card location-heat-card">
    <div className="location-map-shell">
    <div className={`location-map${dragging ? " dragging" : ""}`} ref={mapRef} role="img" aria-label="Geolocated incident map" onWheel={handleWheel} onClick={handleMapClick} onPointerDown={handlePointerDown} onPointerMove={handlePointerMove} onPointerUp={handlePointerUp} onPointerCancel={handlePointerUp}>
      <div className="map-zoom-controls" onClick={event => event.stopPropagation()} onPointerDown={event => event.stopPropagation()}><button type="button" onClick={() => setZoom(current => Math.min(14, current + 1))} aria-label="Zoom in">+</button><button type="button" onClick={() => setZoom(current => Math.max(8, current - 1))} aria-label="Zoom out">-</button><button type="button" onClick={resetMapView} aria-label="Reset map view">Fit</button><span>{zoom}</span></div>
      {tiles.map(tile => <img
        src={`https://tile.openstreetmap.org/${tile.z}/${tile.x}/${tile.y}.png`}
        style={{ left: tile.left, top: tile.top }}
        alt=""
        draggable={false}
        key={`${tile.z}-${tile.x}-${tile.y}`}
      />)}
      {points.map(({ row, point }) => {
        const nodeCount = locationNodeCount(row, incidents);
        const size = 22 + row.intensity * 36;
        return <button
          className={`heat-dot map-heat-dot category-${row.category || "other"} ${selected && locationKey(selected) === locationKey(row) ? "active" : ""}`}
          style={{ left: `${point.x}%`, top: `${point.y}%`, width: size, height: size }}
          title={`${locationDisplayName(row)}: ${locationNodeCountLabel(row, incidents)}; ${row.count} matched call${row.count === 1 ? "" : "s"}; latest ${new Date(row.lastHeard * 1000).toLocaleString()}; calls ${row.callIds.join(", ")}`}
          onClick={event => { event.stopPropagation(); focusLocation(row); }}
          onPointerDown={event => event.stopPropagation()}
          key={`${row.areaId}-${row.locationText}`}
        >
          <span>{nodeCount}</span>
        </button>;
      })}
      <a className="map-attribution" href="https://www.openstreetmap.org/copyright" target="_blank" rel="noreferrer">OpenStreetMap</a>
    </div>
    </div>
    <div className="location-heat-list">
      <span className="location-heat-list-count">{rows.length} geolocated address{rows.length === 1 ? "" : "es"}</span>
    </div>
  </div>;
}

function locationDisplayName(row: LocationHeat) {
  return row.geocodeDisplayName?.trim() || row.geocodeQuery?.trim() || row.locationText;
}

function locationShortName(row: LocationHeat) {
  const display = locationDisplayName(row).trim();
  const parts = display.split(",").map(part => part.trim()).filter(Boolean);
  const primary = parts[0];
  if (primary && /^\d+[a-z]?$/i.test(primary) && parts[1])
    return `${primary} ${parts[1]}`;
  return primary || display || row.locationText;
}

function locationSourceText(row: LocationHeat) {
  const heard = row.locationText.trim().toLocaleLowerCase();
  return heard !== locationDisplayName(row).trim().toLocaleLowerCase()
    && heard !== locationShortName(row).trim().toLocaleLowerCase();
}

type GeoPoint = { lat: number; lon: number };
type MapViewport = { zoom: number; width: number; height: number; centerWorldX: number; centerWorldY: number };
type MapDragState = { pointerId: number; startX: number; startY: number; centerWorldX: number; centerWorldY: number; moved: boolean };

function defaultMapCenter(rows: LocationHeat[]): GeoPoint {
  const points = rows.filter(row => Number.isFinite(row.latitude) && Number.isFinite(row.longitude));
  if (!points.length)
    return { lat: 0, lon: 0 };
  return {
    lat: points.reduce((sum, row) => sum + row.latitude, 0) / points.length,
    lon: points.reduce((sum, row) => sum + row.longitude, 0) / points.length
  };
}

function defaultMapZoom(rows: LocationHeat[]) {
  const points = rows.filter(row => Number.isFinite(row.latitude) && Number.isFinite(row.longitude));
  if (points.length < 2)
    return 12;
  const latSpan = Math.max(...points.map(row => row.latitude)) - Math.min(...points.map(row => row.latitude));
  const lonSpan = Math.max(...points.map(row => row.longitude)) - Math.min(...points.map(row => row.longitude));
  const span = Math.max(latSpan, lonSpan);
  if (span <= 0.15) return 13;
  if (span <= 0.45) return 12;
  if (span <= 1.1) return 11;
  if (span <= 3) return 10;
  if (span <= 8) return 8;
  return 5;
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

function latLonToWorld(lat: number, lon: number, zoom: number) {
  const sinLat = Math.sin(lat * Math.PI / 180);
  const scale = 256 * 2 ** zoom;
  return {
    x: (lon + 180) / 360 * scale,
    y: (0.5 - Math.log((1 + sinLat) / (1 - sinLat)) / (4 * Math.PI)) * scale
  };
}

function worldToLatLon(x: number, y: number, zoom: number): GeoPoint {
  const scale = 256 * 2 ** zoom;
  const wrappedX = ((x % scale) + scale) % scale;
  const clampedY = Math.max(0, Math.min(scale, y));
  const lon = wrappedX / scale * 360 - 180;
  const n = Math.PI - 2 * Math.PI * clampedY / scale;
  const lat = 180 / Math.PI * Math.atan(Math.sinh(n));
  return { lat: Math.max(-85.0511, Math.min(85.0511, lat)), lon };
}

function isMapControlTarget(target: EventTarget | null) {
  return target instanceof HTMLElement && Boolean(target.closest(".map-zoom-controls, .map-heat-dot, .map-attribution"));
}

function mapEventPoint(event: { currentTarget: HTMLDivElement; clientX: number; clientY: number }) {
  const rect = event.currentTarget.getBoundingClientRect();
  if (!rect.width || !rect.height) return null;
  return {
    x: event.clientX - rect.left,
    y: event.clientY - rect.top
  };
}

function TopTalkgroups({ rows }: { rows: TopTalkgroup[] }) {
  return <table className="table top-talkgroups"><thead><tr><th>Talkgroup</th><th>Calls</th><th>Share</th><th>Trend</th></tr></thead><tbody>{rows.map(r => <tr key={r.talkgroup}><td>{r.label}</td><td>{r.count}</td><td>{(r.share * 100).toFixed(1)}%</td><td><div className="trend-bars" aria-label={`${r.label} trend, ${r.trendBucketLabel}`}>{r.trend.map((v, i) => <span className="trend" title={`${r.trendLabels?.[i] ?? "Bucket"}: ${r.trendCounts?.[i] ?? 0} calls`} style={{ height: 4 + v * 30 }} key={i} />)}</div><div className="muted">Hourly volume; hover bars for counts</div></td></tr>)}</tbody></table>;
}

type DashboardIncidentListItem = { kind: "incident"; incident: Incident } | { kind: "alert"; alert: AlertMatch };
type PendingCardFocus = CardHashTarget & { key: string; page: number };

function Incidents({ rows, alerts = [], locationMap, onShowLocation, reload, focusedIncidentId, focusedHashTarget, clearFocusedIncident, clearFocusedHashTarget, mode, setMode, incidentCount, alertCount, searchQuery, hiddenIncidentCount = 0 }: { rows: Incident[]; alerts?: AlertMatch[]; locationMap?: Map<number, LocationHeat>; onShowLocation?: (key: string) => void; reload?: () => Promise<void>; focusedIncidentId?: number | null; focusedHashTarget?: string; clearFocusedIncident?: () => void; clearFocusedHashTarget?: () => void; mode: DashboardMode; setMode: (mode: DashboardMode) => void; incidentCount: number; alertCount: number; searchQuery: string; hiddenIncidentCount?: number }) {
  const [expanded, setExpanded] = useState(false);
  const [geolocatedOnly, setGeolocatedOnlyState] = useState(() => readDashboardGeolocatedOnly("incidents"));
  const [page, setPage] = useState(1);
  const [pendingFocus, setPendingFocus] = useState<PendingCardFocus | null>(null);
  const handledFocusRef = useRef("");
  const focusTarget = parseCardHash(focusedHashTarget);
  const filteredRows = geolocatedOnly
    ? rows.filter(i => locationMap?.has(i.id))
    : rows;
  const searchedRows = filteredRows.filter(incident => matchesIncidentSearch(incident, searchQuery));
  const standaloneAlerts: AlertMatch[] = [];
  const sortedRows = sortIncidents(searchedRows);
  const listItems = sortDashboardIncidentItems([
    ...sortedRows.map(incident => ({ kind: "incident" as const, incident })),
    ...standaloneAlerts.map(alert => ({ kind: "alert" as const, alert }))
  ]);
  const pageSize = 10;
  const pageCount = Math.max(1, Math.ceil(listItems.length / pageSize));
  const focusIndex = focusTarget.raw
    ? listItems.findIndex(item => dashboardItemMatchesFocus(item, focusTarget, focusTarget.incidentId))
    : -1;
  const focusPage = focusIndex >= 0 ? Math.floor(focusIndex / pageSize) + 1 : null;
  const currentPage = Math.min(pendingFocus?.page ?? focusPage ?? page, pageCount);
  const visibleItems = listItems.slice((currentPage - 1) * pageSize, currentPage * pageSize);
  const startRow = listItems.length === 0 ? 0 : (currentPage - 1) * pageSize + 1;
  const endRow = Math.min(listItems.length, currentPage * pageSize);
  const listFocusKey = listItems.map(item => item.kind === "incident" ? `i${item.incident.id}` : `a${item.alert.id}`).join(",");
  function clearFocusForManualNavigation() {
    setPendingFocus(null);
    clearFocusedIncident?.();
    clearFocusedHashTarget?.();
  }
  function setGeolocatedOnly(value: boolean) {
    localStorage.setItem("pizzawave-dashboard-geolocated-incidents", value ? "1" : "0");
    setGeolocatedOnlyState(value);
  }
  const activeFocusTarget = pendingFocus ?? focusTarget;
  useEffect(() => {
    const target = parseCardHash(focusedHashTarget);
    const focusKey = target.raw || (focusedIncidentId ? `incident-${focusedIncidentId}` : "");
    if (!focusKey || handledFocusRef.current === focusKey) return;
    const targetIncidentId = target.incidentId ?? focusedIncidentId ?? null;
    const index = listItems.findIndex(item => dashboardItemMatchesFocus(item, target, targetIncidentId));
    if (index < 0) return;
    handledFocusRef.current = focusKey;
    const targetPage = Math.floor(index / pageSize) + 1;
    const targetId = target.targetId || (targetIncidentId ? `incident-${targetIncidentId}` : "");
    setGeolocatedOnly(false);
    setPage(targetPage);
    setExpanded(false);
    setPendingFocus({ ...target, key: focusKey, page: targetPage, targetId });
  }, [focusedIncidentId, focusedHashTarget, listFocusKey]);

  useEffect(() => {
    if (!pendingFocus || currentPage !== pendingFocus.page) return;
    window.setTimeout(() => {
      const target = pendingFocus.targetId ? document.getElementById(pendingFocus.targetId) : null;
      if (!target) return;
      target.scrollIntoView({ block: "center", behavior: "smooth" });
      setPendingFocus(current => current?.key === pendingFocus.key ? null : current);
      clearFocusedIncident?.();
      clearFocusedHashTarget?.();
    }, 80);
  }, [pendingFocus, currentPage, visibleItems, clearFocusedIncident, clearFocusedHashTarget]);
  if (!rows.length && !standaloneAlerts.length) return <div className="card"><p className="muted">No incidents or alert matches detected.</p></div>;
  return <div className="incident-explorer">
    <div className="incident-toolbar">
      <div className="incident-toolbar-left">
        <DashboardModeToggle mode={mode} setMode={setMode} incidentCount={incidentCount} alertCount={alertCount} hiddenIncidentCount={hiddenIncidentCount} />
        <div className="incident-filter-row">
          <label className="compact-toggle"><input type="checkbox" checked={expanded} onChange={event => { clearFocusForManualNavigation(); setExpanded(event.currentTarget.checked); }} /> {expanded ? "Collapse all" : "Expand all"}</label>
          <label className="compact-toggle"><input type="checkbox" checked={geolocatedOnly} onChange={event => { clearFocusForManualNavigation(); setPage(1); setExpanded(false); setGeolocatedOnly(event.currentTarget.checked); }} /> Geolocated</label>
        </div>
      </div>
      <div className="incident-toolbar-actions">
        {listItems.length > pageSize && <div className="incident-pagination top">
          <span className="muted">{startRow}-{endRow} of {listItems.length}</span>
          <button disabled={currentPage <= 1} onClick={() => { clearFocusForManualNavigation(); setExpanded(false); setPage(Math.max(1, currentPage - 1)); }}>Previous</button>
          <span>{currentPage} / {pageCount}</span>
          <button disabled={currentPage >= pageCount} onClick={() => { clearFocusForManualNavigation(); setExpanded(false); setPage(Math.min(pageCount, currentPage + 1)); }}>Next</button>
        </div>}
      </div>
    </div>
    {listItems.length === 0 && <div className="card"><p className="muted">No geolocated incidents in this range.</p></div>}
    {visibleItems.map(item => {
      if (item.kind === "alert")
        return <StandaloneAlertCard alert={item.alert} expanded={expanded || activeFocusTarget.callId === item.alert.callId} onDismissAlert={reload} searchQuery={searchQuery} key={`alert-${item.alert.id}`} />;
      const linkedLocation = locationMap?.get(item.incident.id);
      return <IncidentCard incident={item.incident} linkedLocation={linkedLocation} onShowLocation={onShowLocation} expanded={expanded} forceOpen={focusedIncidentId === item.incident.id || incidentMatchesFocus(item.incident, activeFocusTarget)} onDismissAlert={reload} searchQuery={searchQuery} key={`incident-${item.incident.id}`} />;
    })}
  </div>;
}

function dashboardItemMatchesFocus(item: DashboardIncidentListItem, target: CardHashTarget, fallbackIncidentId: number | null) {
  if (item.kind === "alert")
    return target.callId === item.alert.callId;
  return item.incident.id === fallbackIncidentId || incidentMatchesFocus(item.incident, target);
}

function incidentMatchesFocus(incident: Incident, target: CardHashTarget) {
  return incident.id === target.incidentId || Boolean(target.callId && incident.calls.some(call => call.callId === target.callId));
}

function sortDashboardIncidentItems(items: DashboardIncidentListItem[]) {
  return [...items].sort((a, b) => {
    const aActive = dashboardItemHasActiveAlert(a) ? 1 : 0;
    const bActive = dashboardItemHasActiveAlert(b) ? 1 : 0;
    if (aActive !== bActive) return bActive - aActive;
    return dashboardItemTimestamp(b) - dashboardItemTimestamp(a);
  });
}

function dashboardItemHasActiveAlert(item: DashboardIncidentListItem) {
  return item.kind === "alert" ? item.alert.active !== false : hasActiveAlert(item.incident);
}

function dashboardItemTimestamp(item: DashboardIncidentListItem) {
  return item.kind === "alert" ? item.alert.matchedAt : item.incident.lastSeen;
}

function LocationIncidentPanel({ location, incidents, onClose, searchQuery }: { location: LocationHeat; incidents: Incident[]; onClose: () => void; searchQuery: string }) {
  const standaloneCalls = standaloneLocationSourceCalls(location, incidents).filter(call => matchesTextSearch([call.talkgroupName, call.transcript, call.category, String(call.callId)], searchQuery));
  return <div className="location-incident-panel">
    <div className="location-panel-head">
      <div>
        <strong title={locationDisplayName(location)}>{locationShortName(location)}</strong>
        <span>{location.areaLabel} / {location.count} call{location.count === 1 ? "" : "s"} / latest {relativeTime(location.lastHeard)}</span>
      </div>
      <button aria-label="Close location detail" onClick={onClose}>x</button>
    </div>
    {incidents.length > 0
      ? <div className="incident-explorer">{sortIncidents(incidents).map(incident => <IncidentCard incident={incident} searchQuery={searchQuery} key={incident.id} />)}</div>
      : <div className="card"><p className="muted">No incident currently contains these exact source calls.</p></div>}
    {standaloneCalls.length > 0 && <div className="location-source-calls">
      {standaloneCalls.map(call => <LocationSourceCall call={call} searchQuery={searchQuery} key={call.callId} />)}
    </div>}
  </div>;
}

function AlertsPanel({ alerts, locationMap, reload, mode, setMode, incidentCount, alertCount, searchQuery, hiddenIncidentCount = 0 }: { alerts: AlertMatch[]; locationMap: Map<number, LocationHeat>; reload?: () => Promise<void>; mode: DashboardMode; setMode: (mode: DashboardMode) => void; incidentCount: number; alertCount: number; searchQuery: string; hiddenIncidentCount?: number }) {
  const [geolocatedOnly, setGeolocatedOnlyState] = useState(() => readDashboardGeolocatedOnly("alerts"));
  const [page, setPage] = useState(1);
  function setGeolocatedOnly(value: boolean) {
    localStorage.setItem("pizzawave-dashboard-geolocated-alerts", value ? "1" : "0");
    setGeolocatedOnlyState(value);
  }
  const rows = alerts
    .filter(alert => !geolocatedOnly || locationMap.has(alert.callId))
    .filter(alert => matchesAlertSearch(alert, searchQuery))
    .sort((a, b) => {
      const active = Number(b.active !== false) - Number(a.active !== false);
      return active || b.matchedAt - a.matchedAt;
    });
  const pageSize = 10;
  const pageCount = Math.max(1, Math.ceil(rows.length / pageSize));
  const currentPage = Math.min(page, pageCount);
  const visibleRows = rows.slice((currentPage - 1) * pageSize, currentPage * pageSize);
  const startRow = rows.length === 0 ? 0 : (currentPage - 1) * pageSize + 1;
  const endRow = Math.min(rows.length, currentPage * pageSize);
  return <div className="incident-explorer">
    <div className="incident-toolbar">
      <div className="incident-toolbar-left">
        <DashboardModeToggle mode={mode} setMode={setMode} incidentCount={incidentCount} alertCount={alertCount} hiddenIncidentCount={hiddenIncidentCount} />
        <div className="incident-filter-row">
          <label className="compact-toggle"><input type="checkbox" checked={geolocatedOnly} onChange={event => { setPage(1); setGeolocatedOnly(event.currentTarget.checked); }} /> Geolocated</label>
        </div>
      </div>
      <div className="incident-toolbar-actions">
        {rows.length > pageSize && <div className="incident-pagination top">
          <span className="muted">{startRow}-{endRow} of {rows.length}</span>
          <button disabled={currentPage <= 1} onClick={() => setPage(Math.max(1, currentPage - 1))}>Previous</button>
          <span>{currentPage} / {pageCount}</span>
          <button disabled={currentPage >= pageCount} onClick={() => setPage(Math.min(pageCount, currentPage + 1))}>Next</button>
        </div>}
      </div>
    </div>
    {visibleRows.length
      ? visibleRows.map(alert => <StandaloneAlertCard alert={alert} onDismissAlert={reload} searchQuery={searchQuery} key={`alert-${alert.id}`} />)
      : <div className="card"><p className="muted">{geolocatedOnly ? "No geolocated alert matches in this range." : "No alert matches in this range."}</p></div>}
  </div>;
}

function DashboardModeToggle({ mode, setMode, incidentCount, alertCount, hiddenIncidentCount = 0 }: { mode: DashboardMode; setMode: (mode: DashboardMode) => void; incidentCount: number; alertCount: number; hiddenIncidentCount?: number }) {
  return <div className="dashboard-view-toggle compact" role="group" aria-label="Dashboard view">
    <button type="button" className={mode === "incidents" ? "active" : ""} onClick={() => setMode("incidents")}>Incidents ({incidentCount.toLocaleString()})</button>
    {hiddenIncidentCount > 0 && <span className="dashboard-hidden-policy-note">{hiddenIncidentCount.toLocaleString()} hidden by TG policy</span>}
    <button type="button" className={mode === "alerts" ? "active" : ""} onClick={() => setMode("alerts")}>Alerts ({alertCount.toLocaleString()})</button>
  </div>;
}

function readDashboardGeolocatedOnly(kind: DashboardMode) {
  return localStorage.getItem(`pizzawave-dashboard-geolocated-${kind}`) === "1";
}

function LocationAlertPanel({ location, alerts, onClose, reload, searchQuery }: { location: LocationHeat; alerts: AlertMatch[]; onClose: () => void; reload?: () => Promise<void>; searchQuery: string }) {
  return <div className="location-incident-panel">
    <div className="location-panel-head">
      <div>
        <strong title={locationDisplayName(location)}>{locationShortName(location)}</strong>
        <span>{location.areaLabel} / {alerts.length} alert{alerts.length === 1 ? "" : "s"} / latest {relativeTime(location.lastHeard)}</span>
      </div>
      <button aria-label="Close location detail" onClick={onClose}>x</button>
    </div>
    {alerts.length
      ? <div className="incident-explorer">{alerts.sort((a, b) => b.matchedAt - a.matchedAt).map(alert => <StandaloneAlertCard alert={alert} onDismissAlert={reload} searchQuery={searchQuery} key={`alert-${alert.id}`} />)}</div>
      : <div className="card"><p className="muted">No alerts currently match this map node.</p></div>}
  </div>;
}

function LocationSourceCall({ call, searchQuery = "" }: { call: LocationHeat["sourceCalls"][number]; searchQuery?: string }) {
  const category = call.category || "other";
  const title = [label(category), call.talkgroupName || "Unknown TG"].filter(Boolean).join(" / ");
  return <div id={`call-${call.callId}`} className={`location-source-call category-${category}`}>
    <div className="location-source-call-head">
      <strong><HighlightedText text={title} query={searchQuery} /> <CopyCardLink targetId={`call-${call.callId}`} label="Copy call link" /></strong>
      <span>{relativeTime(call.rawTimestamp)}</span>
    </div>
    <div className="muted">Call {call.callId}</div>
    <p><HighlightedText text={call.transcript || "No transcript stored for this source call."} query={searchQuery} /></p>
    <PlayableAudio src={call.audioUrl} />
  </div>;
}

function CategoryView({ data, rangeHours, searchQuery, onSearchChange, profileState, setProfileState, reload, onOpenProfiles }: { data: CategoryPage | null; rangeHours: number; searchQuery: string; onSearchChange: (value: string) => void; profileState: ProfileState | null; setProfileState: React.Dispatch<React.SetStateAction<ProfileState | null>>; reload: () => Promise<void>; onOpenProfiles: (settings?: ProfileTalkgroupSetting[]) => void }) {
  const [sortMode, setSortModeState] = useState<CategorySortMode>(() => normalizeCategorySort(localStorage.getItem("pizzawave-category-sort")));
  const [hideWeakCalls, setHideWeakCallsState] = useState(() => localStorage.getItem("pizzawave-hide-weak-category-calls") !== "0");
  const [selectionMode, setSelectionMode] = useState(false);
  const [categoryMenuOpen, setCategoryMenuOpen] = useState(false);
  const [selectedTalkgroupKeys, setSelectedTalkgroupKeys] = useState<Set<string>>(() => new Set());
  const [selectionOrderKeys, setSelectionOrderKeys] = useState<string[]>([]);
  const [hidingSelected, setHidingSelected] = useState(false);
  const activeProfile = profileState?.profiles.find(profile => profile.id === profileState.activeProfileId);
  function setSortMode(value: CategorySortMode) {
    setSortModeState(value);
    localStorage.setItem("pizzawave-category-sort", value);
  }
  function setHideWeakCalls(value: boolean) {
    setHideWeakCallsState(value);
    localStorage.setItem("pizzawave-hide-weak-category-calls", value ? "1" : "0");
  }
  function setTalkgroupSelected(group: CategoryPage["groups"][number], selected: boolean) {
    const key = categoryGroupKey(group);
    setSelectedTalkgroupKeys(current => {
      const next = new Set(current);
      if (selected)
        next.add(key);
      else
        next.delete(key);
      return next;
    });
  }
  function clearSelection() {
    setSelectionMode(false);
    setSelectedTalkgroupKeys(new Set());
    setSelectionOrderKeys([]);
  }
  function toggleSelectionMode(sortedGroups: CategoryPage["groups"]) {
    if (selectionMode) {
      clearSelection();
      return;
    }
    setSelectionOrderKeys(sortedGroups.map(categoryGroupKey));
    setSelectionMode(true);
  }
  async function hideSelectedTalkgroups(groups: CategoryPage["groups"]) {
    const selectedGroups = groups.filter(group => selectedTalkgroupKeys.has(categoryGroupKey(group)));
    if (!selectedGroups.length)
      return;
    const settings = selectedGroups.map(group => profileSettingForGroup(group, data?.category ?? "other"));
    if (!profileState || !activeProfile) {
      onOpenProfiles(settings);
      return;
    }
    if (isDefaultProfile(activeProfile)) {
      if (confirmAction("Default profile is read-only", `Create a new profile to hide ${selectedGroups.length.toLocaleString()} selected talkgroup${selectedGroups.length === 1 ? "" : "s"}?`))
        onOpenProfiles(settings);
      return;
    }
    if (!confirmAction("Hide selected talkgroups from profile?", `Hide ${selectedGroups.length.toLocaleString()} talkgroup${selectedGroups.length === 1 ? "" : "s"} from ${activeProfile.name}? Calls remain captured and transcribed, but this profile will not see those TGs in user-facing views or downstream processing.`))
      return;
    setHidingSelected(true);
    try {
      const saved = await api.request<ProfileState>(`/api/v1/profiles/${encodeURIComponent(activeProfile.id)}/talkgroups/hide`, {
        method: "POST",
        body: JSON.stringify({ talkgroups: settings })
      });
      setProfileState(saved);
      clearSelection();
      await reload();
    } finally {
      setHidingSelected(false);
    }
  }
  if (!data) return null;
  const visibleSourceGroups = hideWeakCalls ? data.groups.filter(group => group.strongCount > 0) : data.groups;
  const autoSortedGroups = sortCategoryGroups(visibleSourceGroups, sortMode);
  const sortedGroups = selectionMode && selectionOrderKeys.length
    ? stableCategoryGroupOrder(visibleSourceGroups, selectionOrderKeys)
    : autoSortedGroups;
  const filteredGroups = sortedGroups.filter(group => matchesCategoryGroupSearch(group, searchQuery));
  const selectedCount = filteredGroups.filter(group => selectedTalkgroupKeys.has(categoryGroupKey(group))).length;
  return <div className="category-page category-mode-page" data-category={data.category}>
    <section className="pane category-pane raw-category">
      <div className="category-header">
        <div className="category-title-row">
          <h2>{label(data.category)} Calls by Talkgroup</h2>
          <div className="segmented category-sort-toggle" role="group" aria-label="Sort talkgroups">
            <button type="button" disabled={selectionMode} title={selectionMode ? "Exit talkgroup selection to change sorting." : undefined} className={sortMode === "name" ? "active" : ""} onClick={() => setSortMode("name")}>Name</button>
            <button type="button" disabled={selectionMode} title={selectionMode ? "Exit talkgroup selection to change sorting." : undefined} className={sortMode === "tgid" ? "active" : ""} onClick={() => setSortMode("tgid")}>TG ID</button>
            <button type="button" disabled={selectionMode} title={selectionMode ? "Exit talkgroup selection to change sorting." : undefined} className={sortMode === "recent" ? "active" : ""} onClick={() => setSortMode("recent")}>Recent</button>
            <button type="button" disabled={selectionMode} title={selectionMode ? "Exit talkgroup selection to change sorting." : undefined} className={sortMode === "frequent" ? "active" : ""} onClick={() => setSortMode("frequent")}>Frequent</button>
          </div>
          <PageSearch value={searchQuery} onChange={onSearchChange} placeholder={`Search ${label(data.category)} calls`} />
          <div className="autoplay-menu-wrap category-more-menu-wrap">
            <button type="button" aria-label="More category options" onClick={() => setCategoryMenuOpen(value => !value)}>More <ChevronDown size={14} /></button>
            {categoryMenuOpen && <div className="autoplay-menu category-more-menu">
              <label className="category-quality-toggle">
              <input type="checkbox" checked={hideWeakCalls} onChange={event => setHideWeakCalls(event.currentTarget.checked)} />
              <span>Hide weak calls</span>
              </label>
              <button type="button" onClick={() => { toggleSelectionMode(autoSortedGroups); setCategoryMenuOpen(false); }}>{selectionMode ? "Exit talkgroup selection" : "Select talkgroups"}</button>
            </div>}
          </div>
        </div>
      </div>
      {selectionMode && <div className="category-selection-bar">
        <span>{selectedCount.toLocaleString()} talkgroup{selectedCount === 1 ? "" : "s"} selected</span>
        <button type="button" className="danger-button" disabled={!selectedCount || hidingSelected} onClick={() => void hideSelectedTalkgroups(filteredGroups)}>{hidingSelected ? "Hiding..." : "Hide selected from profile"}</button>
        <button type="button" disabled={hidingSelected} onClick={clearSelection}>Cancel</button>
      </div>}
      <CategoryCallGroups groups={filteredGroups} category={data.category} rangeHours={rangeHours} searchQuery={searchQuery} hideWeakCalls={hideWeakCalls} selectionMode={selectionMode} selectedTalkgroupKeys={selectedTalkgroupKeys} onToggleSelected={setTalkgroupSelected} />
    </section>
  </div>;
}

function StandaloneAlertCard({ alert, expanded, onDismissAlert, searchQuery = "" }: { alert: AlertMatch; expanded?: boolean; onDismissAlert?: () => Promise<void>; searchQuery?: string }) {
  const [localOpen, setLocalOpen] = useState(false);
  const category = normalizeUiCategory(alert.category);
  const active = alert.active !== false;
  useEffect(() => {
    if (expanded !== undefined)
      setLocalOpen(expanded);
  }, [expanded]);
  async function dismissAlert(event: React.MouseEvent<HTMLButtonElement>) {
    event.preventDefault();
    event.stopPropagation();
    await api.request(`/api/v1/alerts/${alert.id}/dismiss`, { method: "POST" });
    await onDismissAlert?.();
  }
  return <details
    id={`call-${alert.callId}`}
    className={`incident-card standalone-alert-card category-${category} incident-alert${active ? " active-alert" : ""}`}
    open={localOpen}
    onToggle={event => setLocalOpen(event.currentTarget.open)}
  >
    <summary>
      <span className="incident-title-wrap">
        <span className="incident-category-ribbons" aria-hidden="true"><span className={`category-${category}`} /></span>
      <span className="incident-title-with-link">
        <span className="incident-title"><HighlightedText text={alert.talkgroupName || `${label(category)} alert`} query={searchQuery} /></span>
        <CopyCardLink targetId={`call-${alert.callId}`} label="Copy alert call link" />
      </span>
      </span>
      <span className="incident-summary-meta">
        <span className={active ? "alert-badge active" : "alert-badge"} title={`${alert.ruleName}: ${alert.detail}`}>{active ? "Active alert" : "Alert"}</span>
        {alert.notificationSuppressed && <span className="section-status neutral" title="The match was processed more than 60 minutes after the call. Real-time email and playback were suppressed.">Recovered history</span>}
        {active && <button type="button" className="dismiss-alert-button" onClick={dismissAlert}>Dismiss</button>}
        <span className="incident-time">{relativeTime(alert.matchedAt)}</span>
        <span className="muted">Call {alert.callId}</span>
      </span>
    </summary>
    {localOpen && <div className={`incident-call category-${category}`}>
      <div className="incident-call-head">
        <span><HighlightedText text={alert.ruleName} query={searchQuery} /></span>
        <span><HighlightedText text={alert.detail} query={searchQuery} /></span>
      </div>
      <div className="incident-call-meta">
        {alert.systemShortName && <span>{alert.systemShortName}</span>}
        <span>{new Date(alert.matchedAt * 1000).toLocaleString()}</span>
        <span>{label(category)}</span>
      </div>
      <div className="transcript-block"><HighlightedText text={alert.transcription?.trim() || "No transcript stored for this alert match."} query={searchQuery} /></div>
      <PlayableAudio src={alert.audioUrl} />
    </div>}
  </details>;
}

function IncidentCard({ incident, linkedLocation, onShowLocation, expanded, forceOpen, categoryOverride, onDismissAlert, searchQuery = "" }: { incident: Incident; linkedLocation?: LocationHeat; onShowLocation?: (key: string) => void; expanded?: boolean; forceOpen?: boolean; categoryOverride?: string; onDismissAlert?: () => Promise<void>; searchQuery?: string }) {
  const [localOpen, setLocalOpen] = useState(false);
  const [playingCallId, setPlayingCallId] = useState<number | null>(null);
  const playlistAudioRef = useRef<HTMLAudioElement | null>(null);
  const playlistRunRef = useRef(0);
  const displayCategory = categoryOverride || normalizeUiCategory(incident.category);
  const stripeCategories = incidentCategoryStripe(incident);
  const stopPlaylist = useCallback(() => {
    playlistRunRef.current += 1;
    if (playlistAudioRef.current) {
      playlistAudioRef.current.pause();
      playlistAudioRef.current.currentTime = 0;
      playlistAudioRef.current = null;
    }
    setPlayingCallId(null);
  }, []);
  useEffect(() => {
    function stopOtherPlaylist() {
      stopPlaylist();
    }
    window.addEventListener("pizzawave-stop-incident-playback", stopOtherPlaylist);
    return () => {
      window.removeEventListener("pizzawave-stop-incident-playback", stopOtherPlaylist);
      stopPlaylist();
    };
  }, [stopPlaylist]);
  useEffect(() => {
    if (expanded !== undefined)
      setLocalOpen(expanded);
  }, [expanded]);
  useEffect(() => {
    if (forceOpen)
      setLocalOpen(true);
  }, [forceOpen]);
  function toggleIncidentPlayback(event: React.MouseEvent<HTMLButtonElement>) {
    event.preventDefault();
    event.stopPropagation();
    if (playingCallId !== null) {
      stopPlaylist();
      return;
    }
    const playableCalls = [...(incident.calls ?? [])]
      .filter(call => call.audioUrl || call.callId)
      .sort((a, b) => a.rawTimestamp - b.rawTimestamp);
    if (!playableCalls.length)
      return;
    window.dispatchEvent(new Event("pizzawave-stop-incident-playback"));
    flushSync(() => setLocalOpen(true));
    const runId = playlistRunRef.current + 1;
    playlistRunRef.current = runId;
    void playIncidentCallSequence(playableCalls, 0, runId);
  }
  async function playIncidentCallSequence(calls: Incident["calls"], index: number, runId: number): Promise<void> {
    if (playlistRunRef.current !== runId)
      return;
    const call = calls[index];
    if (!call) {
      setPlayingCallId(null);
      return;
    }
    const audio = document.querySelector<HTMLAudioElement>(`#incident-${incident.id} #call-${call.callId} audio`);
    if (!audio) {
      await playIncidentCallSequence(calls, index + 1, runId);
      return;
    }
    playlistAudioRef.current = audio;
    setPlayingCallId(call.callId);
    document.getElementById(`call-${call.callId}`)?.scrollIntoView({ block: "nearest", behavior: "smooth" });
    audio.currentTime = 0;
    await new Promise<void>(resolve => {
      const done = () => resolve();
      audio.addEventListener("ended", done, { once: true });
      audio.addEventListener("error", done, { once: true });
      void audio.play().catch(done);
    });
    if (playlistRunRef.current !== runId || playlistAudioRef.current !== audio)
      return;
    playlistAudioRef.current = null;
    await playIncidentCallSequence(calls, index + 1, runId);
  }
  return <details
    id={`incident-${incident.id}`}
    className={`incident-card category-${displayCategory}${hasAlertMatch(incident) ? " incident-alert" : ""}${hasActiveAlert(incident) ? " active-alert" : ""}`}
    open={localOpen}
    onToggle={event => {
      setLocalOpen(event.currentTarget.open);
      if (!event.currentTarget.open)
        stopPlaylist();
    }}
  >
    <IncidentSummary incident={incident} linkedLocation={linkedLocation} onShowLocation={onShowLocation} stripeCategories={stripeCategories} onDismissAlert={onDismissAlert} onTogglePlayback={toggleIncidentPlayback} isPlaying={playingCallId !== null} searchQuery={searchQuery} />
    {localOpen && <>
      <div className="incident-expanded-meta">{incident.calls.length} source call{incident.calls.length === 1 ? "" : "s"} / {Math.round(incident.confidence * 100)}% confidence</div>
      {incident.detail && <p className="incident-detail-text"><HighlightedText text={incident.detail} query={searchQuery} /></p>}
      <div className="incident-call-list">
        {incident.calls.map(c => <IncidentSourceCall call={c} incidentId={incident.id} playing={playingCallId === c.callId} searchQuery={searchQuery} key={c.callId} />)}
      </div>
    </>}
  </details>;
}

function incidentCategoryStripe(incident: Incident) {
  const counts = new Map<string, number>();
  for (const call of incident.calls ?? []) {
    const category = normalizeUiCategory(call.category);
    counts.set(category, (counts.get(category) ?? 0) + 1);
  }
  if (!counts.size)
    counts.set(normalizeUiCategory(incident.category), 0);
  return categories
    .filter(category => counts.has(category))
    .map(category => ({ category, count: counts.get(category) ?? 0 }));
}

function hasAlertMatch(incident: Incident) {
  return incident.calls?.some(call => call.hasAlertMatch) ?? false;
}

function hasActiveAlert(incident: Incident) {
  return incident.calls?.some(call => call.hasActiveAlert) ?? false;
}

function normalizeUiCategory(category: string) {
  const normalized = (category || "").trim().toLowerCase();
  return categories.includes(normalized as typeof categories[number]) ? normalized : "other";
}

function confirmAction(title: string, detail: string) {
  return window.confirm(`${title}\n\n${detail}`);
}

function confirmDiscardUnappliedSettings() {
  const dirtyTalkgroups = localStorage.getItem("pizzawave-unapplied-talkgroups") === "1";
  const dirtySettings = localStorage.getItem("pizzawave-unapplied-settings") === "1";
  if (!dirtyTalkgroups && !dirtySettings)
    return true;
  const detail = dirtyTalkgroups && dirtySettings
    ? "You have unapplied settings and talkgroup changes. Leaving Settings will discard the local drafts."
    : dirtyTalkgroups
      ? "You have unapplied changes in Setup > Talkgroups. Leaving this page will discard the local draft."
      : "You have unapplied settings changes. Leaving Settings will discard the local draft.";
  const ok = confirmAction("Discard unapplied settings?", detail);
  if (ok) {
    localStorage.removeItem("pizzawave-unapplied-talkgroups");
    localStorage.removeItem("pizzawave-unapplied-settings");
  }
  return ok;
}

function IncidentSourceCall({ call, incidentId, playing, searchQuery = "" }: { call: Incident["calls"][number]; incidentId: number; playing?: boolean; searchQuery?: string }) {
  const category = call.category || "other";
  const transcript = call.transcript?.trim();
  const talkgroup = call.talkgroupName?.trim() || "Unknown talkgroup";
  const system = call.systemShortName?.trim();
  return <div id={`call-${call.callId}`} className={`incident-call category-${category}${playing ? " playing" : ""}`}>
    <div className="incident-call-head">
      <span><HighlightedText text={talkgroup} query={searchQuery} /> <CopyCardLink targetId={`call-${call.callId}`} hashTarget={`incident-${incidentId}:call-${call.callId}`} label="Copy call link" /></span>
      <span>{label(category)} / Call {call.callId}</span>
    </div>
    <div className="incident-call-meta">
      {system && <span>{system}</span>}
      <span>{new Date(call.rawTimestamp * 1000).toLocaleString()}</span>
    </div>
    <div className="transcript-block"><HighlightedText text={transcript || "No transcript stored for this source call."} query={searchQuery} /></div>
    <PlayableAudio src={call.audioUrl} />
  </div>;
}

function IncidentSummary({ incident, linkedLocation, onShowLocation, stripeCategories, onDismissAlert, onTogglePlayback, isPlaying, searchQuery = "" }: { incident: Incident; linkedLocation?: LocationHeat; onShowLocation?: (key: string) => void; stripeCategories: { category: string; count: number }[]; onDismissAlert?: () => Promise<void>; onTogglePlayback?: (event: React.MouseEvent<HTMLButtonElement>) => void; isPlaying?: boolean; searchQuery?: string }) {
  const alertRules = Array.from(new Set((incident.calls ?? [])
    .flatMap(call => (call.alertRules ?? "").split(","))
    .map(rule => rule.trim())
    .filter(Boolean)));
  async function dismissAlert(event: React.MouseEvent<HTMLButtonElement>) {
    event.preventDefault();
    event.stopPropagation();
    await api.request(`/api/v1/incidents/${incident.id}/alerts/dismiss`, { method: "POST" });
    await onDismissAlert?.();
  }
  return <summary>
    <span className="incident-title-wrap">
      <span className="incident-category-ribbons" aria-hidden="true">
        {stripeCategories.map(stripe => <span className={`category-${stripe.category}`} key={stripe.category}>{stripe.count || ""}</span>)}
      </span>
      <span className="incident-title-with-link">
        <span className="incident-title"><HighlightedText text={incident.title} query={searchQuery} /></span>
      </span>
    </span>
    <span className="incident-summary-side">
      <span className="incident-summary-meta">
        {linkedLocation && <button type="button" className="geo-badge" title={`Show ${linkedLocation.locationText} on map`} onClick={event => { event.preventDefault(); event.stopPropagation(); onShowLocation?.(locationKey(linkedLocation)); }}>Map</button>}
        {hasAlertMatch(incident) && <span className={hasActiveAlert(incident) ? "alert-badge active" : "alert-badge"} title={alertRules.join(", ") || "Alert match"}>{hasActiveAlert(incident) ? "Active alert" : "Alert"}</span>}
        {hasActiveAlert(incident) && <button type="button" className="dismiss-alert-button" onClick={dismissAlert}>Dismiss</button>}
        {incident.status && incident.status !== "active" && <span className="pill">{label(incident.status)}</span>}
        <span className="incident-time">{relativeIncidentTime(incident)}</span>
      </span>
      <span className="incident-summary-actions">
        <CopyCardLink targetId={`incident-${incident.id}`} label="Copy incident link" />
        <button type="button" className={isPlaying ? "play-incident-button active" : "play-incident-button"} title={isPlaying ? "Stop incident playback" : "Play incident calls oldest to newest"} aria-label={isPlaying ? "Stop incident playback" : "Play incident calls"} onClick={onTogglePlayback}>{isPlaying ? <Square size={17} /> : <Play size={18} />}</button>
      </span>
    </span>
  </summary>;
}

function CategoryCallGroups({ groups, category, rangeHours, searchQuery, hideWeakCalls, selectionMode, selectedTalkgroupKeys, onToggleSelected }: { groups: CategoryPage["groups"]; category: string; rangeHours: number; searchQuery: string; hideWeakCalls: boolean; selectionMode: boolean; selectedTalkgroupKeys: Set<string>; onToggleSelected: (group: CategoryPage["groups"][number], selected: boolean) => void }) {
  if (!groups.length) return <div className="card"><p className="muted">No raw calls available for this category.</p></div>;
  return <>{groups.map(group => <CollapsibleCallGroup group={group} category={category} rangeHours={rangeHours} searchQuery={searchQuery} hideWeakCalls={hideWeakCalls} selectionMode={selectionMode} selected={selectedTalkgroupKeys.has(categoryGroupKey(group))} onToggleSelected={onToggleSelected} key={`${group.talkgroupKey || group.talkgroup}-${group.label}`} />)}</>;
}

function CollapsibleCallGroup({ group, category, rangeHours, searchQuery, hideWeakCalls, selectionMode, selected, onToggleSelected }: { group: CategoryPage["groups"][number]; category: string; rangeHours: number; searchQuery: string; hideWeakCalls: boolean; selectionMode: boolean; selected: boolean; onToggleSelected: (group: CategoryPage["groups"][number], selected: boolean) => void }) {
  const [open, setOpen] = useState(false);
  const [calls, setCalls] = useState<EngineCall[]>(group.calls ?? []);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState("");
  const count = group.count || calls.length;
  const visibleCount = hideWeakCalls ? group.strongCount : count;
  useEffect(() => {
    setCalls(group.calls ?? []);
    setError("");
    setLoading(false);
  }, [category, group.talkgroupKey, group.talkgroup, rangeHours]);
  useEffect(() => {
    if (!open || calls.length || loading || group.talkgroup === undefined) return;
    setLoading(true);
    setError("");
    const key = encodeURIComponent(group.talkgroupKey || String(group.talkgroup));
    api.request<CategoryPage["groups"][number]>(`/api/v1/categories/${category}/talkgroup-keys/${key}/calls?${rangeQuery(rangeHours)}&limit=150`)
      .then(result => setCalls(result.calls ?? []))
      .catch(err => setError(err instanceof Error ? err.message : "Failed to load calls"))
      .finally(() => setLoading(false));
  }, [open, calls.length, loading, category, group.talkgroupKey, group.talkgroup, rangeHours]);
  const visibleCalls = calls.filter(call => (!hideWeakCalls || isStrongCall(call)) && matchesCallSearch(call, searchQuery));
  const technicalLabel = [
    group.catalogSystemShortName?.toUpperCase(),
    group.talkgroup ? `TG ${group.talkgroup}` : "",
    group.alphaTag
  ].filter(Boolean).join(" · ");
  return <details className={`call-group category-${category}`} open={open} onToggle={e => setOpen(e.currentTarget.open)}>
    <summary>
      <span className="call-group-title">
        {selectionMode && <input
          type="checkbox"
          className="call-group-select"
          checked={selected}
          aria-label={`Select ${group.label}`}
          onClick={event => event.stopPropagation()}
          onChange={event => onToggleSelected(group, event.currentTarget.checked)}
        />}
        <span className="call-group-title-copy">
          <strong><HighlightedText text={group.label} query={searchQuery} /></strong>
          {technicalLabel && <small>{technicalLabel}</small>}
        </span>
      </span>
      <span className="muted">{hideWeakCalls ? `${visibleCount.toLocaleString()} of ${count.toLocaleString()} calls shown` : `${count.toLocaleString()} calls`}{group.lastHeard ? `; latest ${relativeTime(group.lastHeard)}` : ""}</span>
    </summary>
    {open && loading && <div className="call-group-status">Loading calls...</div>}
    {open && error && <div className="call-group-status error">{error}</div>}
    {open && visibleCalls.map(c => <CallRow call={c} talkgroupLabel={group.label} searchQuery={searchQuery} key={c.id} />)}
    {open && calls.length > 0 && visibleCalls.length === 0 && <div className="call-group-status">{hideWeakCalls ? "No loaded strong calls match this view." : "No loaded calls match this search."}</div>}
    {open && calls.length >= 150 && <div className="call-group-status">Showing latest 150 calls.</div>}
  </details>;
}

function CallRow({ call, talkgroupLabel, searchQuery = "" }: { call: EngineCall; talkgroupLabel?: string; searchQuery?: string }) {
  const status = call.qualityReason && call.qualityReason !== "ok" ? `${call.transcriptionStatus}: ${call.qualityReason}` : call.transcriptionStatus;
  const transcript = call.transcription?.trim();
  const missingText = call.transcriptionStatus === "pending"
    ? "Pending transcription"
    : `No transcript available (${status || "not transcribed"}).`;
  return <div id={`call-${call.id}`} className={`call category-${call.category}`}><div className="call-head"><strong><HighlightedText text={talkgroupLabel || call.talkgroupName || `TG ${call.talkgroup}`} query={searchQuery} /> <CopyCardLink targetId={`call-${call.id}`} label="Copy call link" /></strong><span>{new Date(call.startTime * 1000).toLocaleString()}</span><span>{status}</span>{call.isImported && <span className="pill">Imported</span>}</div><div><HighlightedText text={transcript || missingText} query={searchQuery} /></div><PlayableAudio src={call.audioPath ? `/api/v1/calls/${call.id}/audio` : ""} /></div>;
}

function CopyCardLink({ targetId, hashTarget, label }: { targetId: string; hashTarget?: string; label: string }) {
  const [copied, setCopied] = useState(false);
  async function copy(event: React.MouseEvent<HTMLButtonElement>) {
    event.preventDefault();
    event.stopPropagation();
    const url = `${window.location.origin}${window.location.pathname}${window.location.search}#${encodeURIComponent(hashTarget || targetId)}`;
    try {
      await navigator.clipboard.writeText(url);
    } catch {
      const input = document.createElement("input");
      input.value = url;
      document.body.appendChild(input);
      input.select();
      document.execCommand("copy");
      input.remove();
    }
    setCopied(true);
    window.setTimeout(() => setCopied(false), 1200);
  }
  return <button type="button" className="copy-link-button" aria-label={label} title={copied ? "Copied" : label} onClick={copy}><Link2 size={17} /></button>;
}

type CardHashTarget = { raw: string; incidentId: number | null; callId: number | null; targetId: string };

function parseCardHash(rawHash?: string): CardHashTarget {
  const hash = rawHash ?? decodeURIComponent(window.location.hash.replace(/^#/, ""));
  const incident = /(?:^|:)incident-(\d+)(?::|$)/.exec(hash);
  const call = /(?:^|:)call-(\d+)(?::|$)/.exec(hash);
  return {
    raw: hash,
    incidentId: incident ? Number(incident[1]) : null,
    callId: call ? Number(call[1]) : null,
    targetId: hashScrollTargetId(hash)
  };
}

function hashScrollTargetId(hash = decodeURIComponent(window.location.hash.replace(/^#/, ""))) {
  if (!hash) return "";
  const parts = hash.split(":");
  return parts[parts.length - 1] || "";
}

function callPlaybackLabel(call: EngineCall) {
  return `${call.talkgroupName || `TG ${call.talkgroup}`} Call ${call.id}`;
}

function alertPlaybackLabel(alert: AlertMatch) {
  return `${alert.talkgroupName || label(alert.category || "other")} Call ${alert.callId}`;
}

function sortIncidents(rows: Incident[]) {
  return [...rows].sort((a, b) => (b.lastSeen - a.lastSeen) || (b.confidence - a.confidence));
}

function normalizeCategorySort(value: string | null): CategorySortMode {
  return value === "name" || value === "tgid" || value === "recent" || value === "frequent" ? value : "recent";
}

function sortCategoryGroups(groups: CategoryPage["groups"], mode: CategorySortMode) {
  return [...groups].sort((a, b) => {
    if (mode === "name")
      return categoryGroupNameSortKey(a).localeCompare(categoryGroupNameSortKey(b), undefined, { sensitivity: "base", numeric: true }) || (a.talkgroup - b.talkgroup);
    if (mode === "tgid")
      return (a.talkgroup - b.talkgroup) || categoryGroupNameSortKey(a).localeCompare(categoryGroupNameSortKey(b), undefined, { sensitivity: "base", numeric: true });
    if (mode === "frequent")
      return (b.count - a.count) || (b.lastHeard - a.lastHeard) || categoryGroupNameSortKey(a).localeCompare(categoryGroupNameSortKey(b), undefined, { sensitivity: "base", numeric: true });
    return (b.lastHeard - a.lastHeard) || (b.count - a.count) || categoryGroupNameSortKey(a).localeCompare(categoryGroupNameSortKey(b), undefined, { sensitivity: "base", numeric: true });
  });
}

function categoryGroupNameSortKey(group: CategoryPage["groups"][number]) {
  const labelText = (group.label || `TG ${group.talkgroup}`).trim();
  const escapedId = String(group.talkgroup).replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  return labelText
    .replace(new RegExp(`^\\s*(?:tg\\s*)?${escapedId}\\s*(?:[-:|/]|\\s+-\\s+)?\\s*`, "i"), "")
    .replace(/^\s*(?:tg\s*)?\d{2,8}\s*(?:[-:|/]|[.)])?\s*/i, "")
    .trim()
    .toLowerCase() || labelText.toLowerCase();
}

function stableCategoryGroupOrder(groups: CategoryPage["groups"], orderKeys: string[]) {
  const order = new Map(orderKeys.map((key, index) => [key, index]));
  return [...groups].sort((a, b) => {
    const aRank = order.get(categoryGroupKey(a));
    const bRank = order.get(categoryGroupKey(b));
    if (aRank !== undefined && bRank !== undefined) return aRank - bRank;
    if (aRank !== undefined) return -1;
    if (bRank !== undefined) return 1;
    return (a.label || `TG ${a.talkgroup}`).localeCompare(b.label || `TG ${b.talkgroup}`, undefined, { sensitivity: "base", numeric: true });
  });
}

function searchTokens(query: string) {
  return query
    .trim()
    .toLowerCase()
    .split(/\s+/)
    .filter(token => token.length > 0);
}

function matchesTextSearch(values: Array<string | number | undefined | null>, query: string) {
  const tokens = searchTokens(query);
  if (!tokens.length)
    return true;
  const haystack = values
    .filter(value => value !== undefined && value !== null)
    .join(" ")
    .toLowerCase();
  return tokens.every(token => haystack.includes(token));
}

function matchesIncidentSearch(incident: Incident, query: string) {
  return matchesTextSearch([
    incident.title,
    incident.detail,
    incident.category,
    incident.status,
    incident.id,
    incident.incidentKey,
    ...incident.calls.flatMap(call => [call.talkgroupName, call.transcript, call.category, call.systemShortName, call.callId])
  ], query);
}

function matchesAlertSearch(alert: AlertMatch, query: string) {
  return matchesTextSearch([
    alert.ruleName,
    alert.detail,
    alert.talkgroupName,
    alert.transcription,
    alert.category,
    alert.systemShortName,
    alert.callId
  ], query);
}

function matchesCallSearch(call: EngineCall, query: string) {
  return matchesTextSearch([
    call.talkgroupName,
    call.transcription,
    call.category,
    call.systemShortName,
    call.id,
    call.talkgroup,
    call.transcriptionStatus,
    call.qualityReason
  ], query);
}

function isStrongCall(call: EngineCall) {
  return call.transcriptionStatus?.toLowerCase() === "complete" && call.qualityReason?.toLowerCase() === "ok";
}

function matchesCategoryGroupSearch(group: CategoryPage["groups"][number], query: string) {
  return matchesTextSearch([group.label, group.jurisdiction, group.alphaTag, group.catalogSystemShortName, group.talkgroup, group.count], query)
    || (group.calls ?? []).some(call => matchesCallSearch(call, query));
}

function HighlightedText({ text, query }: { text: string; query: string }) {
  const tokens = Array.from(new Set(searchTokens(query))).filter(token => token.length >= 2);
  if (!tokens.length || !text)
    return <>{text}</>;
  const regex = new RegExp(`(${tokens.map(escapeRegex).join("|")})`, "ig");
  const parts = text.split(regex);
  return <>{parts.map((part, index) => tokens.some(token => part.toLowerCase() === token.toLowerCase())
    ? <mark className="search-highlight" key={index}>{part}</mark>
    : <React.Fragment key={index}>{part}</React.Fragment>)}</>;
}

function escapeRegex(value: string) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
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

function confidenceClass(score: number) {
  if (score >= 0.75) return "confidence-high";
  if (score >= 0.45) return "confidence-mid";
  return "confidence-low";
}

type SystemTopTab = "recommendations" | "services" | "queue" | "jobs" | "storage" | "backup" | "reset" | "audit" | "tr" | "metrics";
type SystemTrTab = "summary" | "config" | "logs";
type SystemArea = "recommendations" | "runtime" | "data" | "trunk-recorder" | "performance";

function normalizeSystemTopTab(value: string | null): SystemTopTab {
  if (value === "service" || value === "qdrant" || value === "cpu") return "services";
  return ["recommendations", "services", "queue", "jobs", "storage", "backup", "reset", "audit", "tr", "metrics"].includes(value ?? "")
    ? value as SystemTopTab
    : "recommendations";
}

function systemAreaForTab(tab: SystemTopTab): SystemArea {
  if (["services", "queue", "jobs"].includes(tab)) return "runtime";
  if (["storage", "backup", "reset", "audit"].includes(tab)) return "data";
  if (tab === "tr") return "trunk-recorder";
  if (tab === "metrics") return "performance";
  return "recommendations";
}

function defaultSystemAreaTab(area: SystemArea): SystemTopTab {
  if (area === "runtime") return "services";
  if (area === "data") return "storage";
  if (area === "trunk-recorder") return "tr";
  if (area === "performance") return "metrics";
  return "recommendations";
}

function rememberedSystemAreaTab(area: SystemArea): SystemTopTab {
  const saved = normalizeSystemTopTab(localStorage.getItem(`pizzawave-system-${area}-tab`));
  return systemAreaForTab(saved) === area ? saved : defaultSystemAreaTab(area);
}

function systemPageIdentity(topTab: SystemTopTab, trTab: SystemTrTab, metricsTab: string) {
  if (topTab === "recommendations") return { area: "System", title: "Recommendations", purpose: "Current problems and worthwhile improvements across the PizzaWave stack.", icon: Gauge, tone: "recommendations" };
  if (topTab === "services") return { area: "Runtime", title: "Services", purpose: "Live service state, host resources, and safe service controls in one place.", icon: Activity, tone: "runtime" };
  if (topTab === "queue") return { area: "Runtime", title: "Queue", purpose: "Current transcription flow, backlog composition, throughput, and ingest controls.", icon: Activity, tone: "runtime" };
  if (topTab === "jobs") return { area: "Runtime", title: "Jobs", purpose: "Explicit background operations, safe cancellation, recovery tools, and retained outcomes.", icon: Wrench, tone: "runtime" };
  if (topTab === "storage") return { area: "Data", title: "Storage", purpose: "PizzaWave disk use, retained datasets, and automatic maintenance history.", icon: Database, tone: "data" };
  if (topTab === "backup") return { area: "Data", title: "Backup and Restore", purpose: "Create, inspect, and deliberately restore complete PizzaWave backups.", icon: Database, tone: "data" };
  if (topTab === "reset") return { area: "Data", title: "Reset", purpose: "Guarded recovery actions with explicit scope and confirmation.", icon: Wrench, tone: "data" };
  if (topTab === "audit") return { area: "Data", title: "Audit History", purpose: "Important setup, settings, and operational changes without duplicating job history.", icon: Search, tone: "data" };
  if (topTab === "tr") {
    if (trTab === "config") return { area: "Trunk Recorder", title: "Configuration Viewer", purpose: "Compare the active configuration with drafts, backups, and experimental artifacts.", icon: Settings, tone: "tr" };
    if (trTab === "logs") return { area: "Trunk Recorder", title: "Logs", purpose: "Paginated raw Trunk Recorder logs with an independent time window.", icon: Search, tone: "tr" };
    return { area: "Trunk Recorder", title: "Summary", purpose: "Explore configured sites, source assignments, and active Trunk Recorder structure.", icon: Radio, tone: "tr" };
  }
  if (metricsTab === "transcription") return { area: "Performance", title: "Transcription", purpose: "Completion, quality, latency, affected talkgroups, and endpoint availability over time.", icon: Gauge, tone: "performance" };
  if (metricsTab === "rf") return { area: "Performance", title: "Radio Frequency", purpose: "Per-site RF stability, decode behavior, retunes, and capture evidence.", icon: Radio, tone: "performance" };
  if (metricsTab === "incidents") return { area: "Performance", title: "Incidents", purpose: "Incident-pipeline decisions and the quality of generated operator output.", icon: Info, tone: "performance" };
  if (metricsTab === "ai") return { area: "Performance", title: "AI Usage", purpose: "Model requests, failures, token consumption, and completion behavior.", icon: Gauge, tone: "performance" };
  if (metricsTab === "bandwidth") return { area: "Performance", title: "Bandwidth", purpose: "PizzaWave-attributed remote transcription and AI network use.", icon: Activity, tone: "performance" };
  return { area: "Performance", title: "Calls", purpose: "Call volume, timing, categories, sites, and talkgroup activity patterns.", icon: Activity, tone: "performance" };
}

function SystemPageIdentity({ area, title, purpose, icon: Icon, tone }: ReturnType<typeof systemPageIdentity>) {
  return <header className={`system-page-identity tone-${tone}`}>
    <span className="system-page-identity-icon" aria-hidden="true"><Icon size={20} /></span>
    <div className="system-page-identity-copy"><span className="system-page-identity-area">{area}</span><h2>{title}</h2><p>{purpose}</p></div>
    <div id="system-page-identity-controls" className="system-page-identity-controls" />
  </header>;
}

function SystemPageHeaderControls({ children }: { children: React.ReactNode }) {
  const [target, setTarget] = useState<HTMLElement | null>(null);
  useEffect(() => setTarget(document.getElementById("system-page-identity-controls")), []);
  return target ? createPortal(children, target) : null;
}

function SystemHistoryWindow({ value, label: ariaLabel, onChange }: { value: number; label: string; onChange: (hours: number) => void }) {
  return <div className="segmented" role="group" aria-label={ariaLabel}>{[
    { hours: 24, text: "24h" }, { hours: 48, text: "2d" }, { hours: 168, text: "Week" }
  ].map(option => <button type="button" key={option.hours} className={value === option.hours ? "active" : ""} onClick={() => onChange(option.hours)}>{option.text}</button>)}</div>;
}

function SystemSectionHeader({ title, description, meta, actions }: { title: string; description?: string; meta?: React.ReactNode; actions?: React.ReactNode }) {
  return <div className="system-section-header">
    <div><h3>{title}</h3>{description && <p>{description}</p>}</div>
    {meta && <div className="system-section-header-meta">{meta}</div>}
    {actions && <div className="system-section-header-actions">{actions}</div>}
  </div>;
}

type RfChartCategory = "all" | "decode" | "activity" | "events";

function SystemView({ rangeHours, engineHealth, refreshSharedStatus, refreshSignal, targetTab, clearTargetTab, onLiveResources, onOpenSetup, onOpenTalkgroup, onOpenIncident }: { rangeHours: number; engineHealth: EngineHealth | null; refreshSharedStatus: () => Promise<unknown>; refreshSignal: number; targetTab?: SystemTopTab | null; clearTargetTab?: () => void; onLiveResources?: (sample: SystemRuntimeResourceSample) => void; onOpenSetup?: (section?: string) => void; onOpenTalkgroup: (row: { category: string; talkgroup: number }) => void; onOpenIncident: (incidentId: number) => void }) {
  const [topTab, setTopTabState] = useState<SystemTopTab>(() => normalizeSystemTopTab(localStorage.getItem("pizzawave-system-tab")));
  const [trTab, setTrTabState] = useState<SystemTrTab>(() => {
    const saved = localStorage.getItem("pizzawave-system-tr-tab");
    return saved === "logs" || saved === "config" ? saved : "summary";
  });
  const [metricsTab, setMetricsTabState] = useState<"calls" | "transcription" | "rf" | "incidents" | "ai" | "bandwidth">(() => {
    const saved = localStorage.getItem("pizzawave-system-metrics-tab");
    return (["calls", "transcription", "rf", "incidents", "ai", "bandwidth"].includes(saved ?? "") ? saved as any : "calls");
  });
  const [baseline, setBaseline] = useState("7d");
  const pageIdentity = systemPageIdentity(topTab, trTab, metricsTab);
  const [rfChartCategory, setRfChartCategory] = useState<RfChartCategory>("all");
  const [selectedRfFinding, setSelectedRfFinding] = useState<SystemRecommendation | null>(null);
  const [rfPerformanceHours, setRfPerformanceHours] = useState(() => {
    const saved = Number(localStorage.getItem("pizzawave-system-rf-performance-hours"));
    return [2, 6, 24, 72, 168].includes(saved) ? saved : 2;
  });
  const [callsPerformanceHours, setCallsPerformanceHours] = useState(() => {
    const saved = Number(localStorage.getItem("pizzawave-system-calls-performance-hours"));
    return [24, 48, 168].includes(saved) ? saved : 24;
  });
  const [transcriptionPerformanceHours, setTranscriptionPerformanceHours] = useState(() => {
    const saved = Number(localStorage.getItem("pizzawave-system-transcription-performance-hours"));
    return [24, 48, 168].includes(saved) ? saved : 24;
  });
  const [incidentPerformanceHours, setIncidentPerformanceHours] = useState(() => {
    const saved = Number(localStorage.getItem("pizzawave-system-incident-performance-hours"));
    return [24, 48, 168].includes(saved) ? saved : 24;
  });
  const [aiPerformanceHours, setAiPerformanceHours] = useState(() => {
    const saved = Number(localStorage.getItem("pizzawave-system-ai-performance-hours"));
    return [24, 48, 168].includes(saved) ? saved : 24;
  });
  const [aiUsagePage, setAiUsagePage] = useState(1);
  const [bandwidthPerformanceHours, setBandwidthPerformanceHours] = useState(() => {
    const saved = Number(localStorage.getItem("pizzawave-system-bandwidth-performance-hours"));
    return [24, 48, 168].includes(saved) ? saved : 24;
  });
  const [bandwidthUsagePage, setBandwidthUsagePage] = useState(1);
  const [serviceResourceHistory, setServiceResourceHistory] = useState<SystemRuntimeResourceSample[]>([]);
  const [restartBusy, setRestartBusy] = useState<"" | "pizzad" | "trunk-recorder" | "qdrant">("");
  const [restartMessages, setRestartMessages] = useState<Record<string, string>>({});
  const [ingestBusy, setIngestBusy] = useState(false);
  const [ingestMessage, setIngestMessage] = useState("");
  const [panelRefreshToken, setPanelRefreshToken] = useState(0);
  const observedRefreshSignal = useRef(refreshSignal);
  const setTopTab = (value: typeof topTab) => {
    setTopTabState(value);
    localStorage.setItem("pizzawave-system-tab", value);
    localStorage.setItem(`pizzawave-system-${systemAreaForTab(value)}-tab`, value);
  };
  const setTrTab = (value: typeof trTab) => { setTrTabState(value); localStorage.setItem("pizzawave-system-tr-tab", value); };
  const setMetricsTab = (value: typeof metricsTab) => { setMetricsTabState(value); localStorage.setItem("pizzawave-system-metrics-tab", value); };
  useEffect(() => {
    localStorage.setItem("pizzawave-system-tab", topTab);
    localStorage.setItem(`pizzawave-system-${systemAreaForTab(topTab)}-tab`, topTab);
  }, [topTab]);
  useEffect(() => {
    if (!targetTab) return;
    setTopTab(targetTab);
    clearTargetTab?.();
  }, [targetTab, clearTargetTab]);

  const recommendationsResource = usePersistentRefresh({
    key: "system-health",
    enabled: topTab === "recommendations",
    load: () => api.request<SystemRecommendations>("/api/v1/system/recommendations")
  });
  const servicesRuntimeResource = usePersistentRefresh({
    key: "system-services-runtime",
    enabled: topTab === "services",
    load: () => api.request<any>("/api/v1/system/runtime")
  });
  const cpuResource = usePersistentRefresh({
    key: "system-processor",
    enabled: topTab === "services",
    load: () => api.request<SystemCpuSnapshot>("/api/v1/system/cpu")
  });
  const liveServiceResource = usePersistentRefresh({
    key: "system-services-live-resources",
    enabled: topTab === "services",
    load: () => api.request<SystemRuntimeResourceSample>("/api/v1/system/resources/live")
  });
  const jobsResource = usePersistentRefresh({
    key: "system-jobs",
    enabled: topTab === "jobs",
    load: () => api.request<Job[]>("/api/v1/jobs")
  });
  const storageResource = usePersistentRefresh({
    key: "system-storage",
    enabled: topTab === "storage",
    load: async () => {
      const [snapshot, jobs] = await Promise.all([
        api.request<any>("/api/v1/system/storage"),
        api.request<Job[]>("/api/v1/jobs")
      ]);
      return { snapshot, jobs: jobs.filter(job => job.type === "system_storage_maintenance").slice(0, 10) };
    }
  });
  const receiverSummaryResource = usePersistentRefresh({
    key: "system-tr-summary|24",
    enabled: topTab === "tr" && trTab === "summary",
    load: () => api.request<TrTroubleshoot>(`/api/v1/troubleshoot?${rangeQuery(24)}&bySystem=false&baseline=7d`)
  });
  const performanceSummaryResource = usePersistentRefresh({
    key: `system-performance-transcription|${transcriptionPerformanceHours}`,
    enabled: topTab === "metrics" && metricsTab === "transcription",
    load: () => api.request<TranscriptionPerformance>(`/api/v1/system/transcription-performance?${rangeQuery(transcriptionPerformanceHours)}`)
  });
  const metricsDataResource = usePersistentRefresh({
    key: `system-metrics-rf|${rfPerformanceHours}|${baseline}`,
    enabled: topTab === "metrics" && metricsTab === "rf",
    load: () => api.request<TrTroubleshoot>(`/api/v1/troubleshoot?${rangeQuery(rfPerformanceHours)}&bySystem=true&baseline=${baseline}`)
  });
  const callsDashboardResource = usePersistentRefresh({
    key: `system-metrics-calls|${callsPerformanceHours}`,
    enabled: topTab === "metrics" && metricsTab === "calls",
    load: () => api.request<Dashboard>(`/api/v1/dashboard?${rangeQuery(callsPerformanceHours)}`)
  });
  const incidentDashboardResource = usePersistentRefresh({
    key: `system-metrics-incidents-dashboard|${incidentPerformanceHours}`,
    enabled: topTab === "metrics" && metricsTab === "incidents",
    load: () => api.request<Dashboard>(`/api/v1/dashboard?${rangeQuery(incidentPerformanceHours)}`)
  });
  const tokenUsageResource = usePersistentRefresh({
    key: `system-metrics-ai|${aiPerformanceHours}|${aiUsagePage}`,
    enabled: topTab === "metrics" && metricsTab === "ai",
    load: () => api.request<TokenUsageReport>(`/api/v1/system/token-usage?${rangeQuery(aiPerformanceHours)}&page=${aiUsagePage}&pageSize=20`)
  });
  const bandwidthUsageResource = usePersistentRefresh({
    key: `system-metrics-bandwidth|${bandwidthPerformanceHours}|${bandwidthUsagePage}`,
    enabled: topTab === "metrics" && metricsTab === "bandwidth",
    load: () => api.request<RemoteBandwidthReport>(`/api/v1/system/remote-bandwidth?${rangeQuery(bandwidthPerformanceHours)}&page=${bandwidthUsagePage}&pageSize=20`)
  });

  const recommendations = recommendationsResource.data;
  const servicesRuntime = servicesRuntimeResource.data;
  const cpuSnapshot = cpuResource.data;
  const jobs = jobsResource.data;
  const storageRuntime = storageResource.data;
  const receiverSummary = receiverSummaryResource.data;
  const performanceSummary = performanceSummaryResource.data;
  const metricsData = metricsDataResource.data;
  const callsDashboard = callsDashboardResource.data;
  const incidentDashboard = incidentDashboardResource.data;
  const tokenUsage = tokenUsageResource.data;
  const bandwidthUsage = bandwidthUsageResource.data;
  const systemArea = systemAreaForTab(topTab);
  useEffect(() => {
    if (topTab !== "services" || !liveServiceResource.data) return;
    const sample = liveServiceResource.data;
    onLiveResources?.(sample);
    setServiceResourceHistory(current => {
      if (current.at(-1)?.generatedAtUtc === sample.generatedAtUtc) return current;
      return [...current, sample].slice(-60);
    });
  }, [topTab, liveServiceResource.data, onLiveResources]);
  useEffect(() => {
    if (topTab !== "services") return;
    const liveTimer = window.setInterval(() => void liveServiceResource.refresh(), 5000);
    const stateTimer = window.setInterval(() => void servicesRuntimeResource.refresh(), 15000);
    return () => {
      window.clearInterval(liveTimer);
      window.clearInterval(stateTimer);
    };
  }, [topTab, liveServiceResource.refresh, servicesRuntimeResource.refresh]);
  async function refreshVisibleSystemPanel() {
    const refreshes: Promise<unknown>[] = [];
    if (topTab === "recommendations") refreshes.push(recommendationsResource.refresh());
    if (topTab === "services") refreshes.push(servicesRuntimeResource.refresh(), cpuResource.refresh(), liveServiceResource.refresh());
    if (topTab === "jobs") refreshes.push(jobsResource.refresh());
    if (topTab === "storage") refreshes.push(storageResource.refresh());
    if (topTab === "queue" || topTab === "backup" || topTab === "reset" || topTab === "audit" || (topTab === "tr" && ["config", "logs"].includes(trTab)))
      setPanelRefreshToken(current => current + 1);
    if (topTab === "tr" && trTab === "summary") refreshes.push(receiverSummaryResource.refresh());
    if (topTab === "metrics" && metricsTab === "transcription") refreshes.push(performanceSummaryResource.refresh());
    if (topTab === "metrics" && metricsTab === "rf") refreshes.push(metricsDataResource.refresh());
    if (topTab === "metrics" && metricsTab === "calls") refreshes.push(callsDashboardResource.refresh());
    if (topTab === "metrics" && metricsTab === "incidents") { refreshes.push(incidentDashboardResource.refresh()); setPanelRefreshToken(current => current + 1); }
    if (topTab === "metrics" && metricsTab === "ai") refreshes.push(tokenUsageResource.refresh());
    if (topTab === "metrics" && metricsTab === "bandwidth") refreshes.push(bandwidthUsageResource.refresh());
    await Promise.all(refreshes);
  }
  async function restartService(service: "pizzad" | "trunk-recorder" | "qdrant") {
    if (!confirmAction(`Restart ${label(service)}?`, "This interrupts the service briefly. Live ingestion or processing may pause while it restarts.")) return;
    setRestartBusy(service);
    setRestartMessages(current => ({ ...current, [service]: `Restarting ${service}...` }));
    try {
      const job = await api.request<Job>(`/api/v1/system/services/${service}/restart`, { method: "POST" });
      setRestartMessages(current => ({ ...current, [service]: `Restart queued as job ${job.id}.` }));
      setTimeout(() => void Promise.all([servicesRuntimeResource.refresh(), liveServiceResource.refresh(), refreshSharedStatus()]), service === "pizzad" ? 6000 : 1500);
    } catch (error) {
      setRestartMessages(current => ({ ...current, [service]: error instanceof Error ? error.message : "Restart failed." }));
    } finally {
      setRestartBusy("");
    }
  }
  async function stopTrService() {
    if (!confirmAction("Stop TR?", "This gracefully stops trunk-recorder so SDR hardware can be swapped. Live capture remains stopped until you restart TR.")) return;
    setRestartBusy("trunk-recorder");
    setRestartMessages(current => ({ ...current, "trunk-recorder": "Stopping trunk-recorder..." }));
    try {
      const job = await api.request<Job>("/api/v1/system/services/trunk-recorder/stop", { method: "POST" });
      setRestartMessages(current => ({ ...current, "trunk-recorder": `Stop queued as job ${job.id}.` }));
      setTimeout(() => void Promise.all([servicesRuntimeResource.refresh(), liveServiceResource.refresh(), refreshSharedStatus()]), 1500);
    } catch (error) {
      setRestartMessages(current => ({ ...current, "trunk-recorder": error instanceof Error ? error.message : "Stop failed." }));
    } finally {
      setRestartBusy("");
    }
  }
  async function setIngestPaused(pause: boolean, untilQueueClear = false) {
    if (pause && !confirmAction("Pause live ingest?", untilQueueClear ? "New live callstream payloads will be dropped until the transcription queue clears." : "New live callstream payloads will be dropped until you resume ingest.")) return;
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
      setPanelRefreshToken(current => current + 1);
      await refreshSharedStatus();
    } catch (error) {
      setIngestMessage(error instanceof Error ? error.message : "Failed to update ingest control.");
    } finally {
      setIngestBusy(false);
    }
  }
  function openRecommendationTarget(item: SystemRecommendations["items"][number]) {
    const target = item.target;
    const candidates = item.candidates;
    if (target.topTab === "recommendations") setTopTab("recommendations");
    if (target.topTab === "runtime") {
      if (target.subTab === "jobs") {
        if (target.anchor === "transcription-recovery-tools") localStorage.setItem("pizzawave-system-open-recovery-tools", "1");
        setTopTab("jobs");
      } else if (target.subTab === "queue") setTopTab("queue");
      else setTopTab("services");
    }
    if (target.topTab === "setup") {
      if (target.subTab === "talkgroups" && candidates.length > 0) {
        localStorage.setItem("pizzawave-setup-talkgroup-candidates", JSON.stringify(candidates.map(row => ({ systemShortName: row.systemShortName, id: row.talkgroup }))));
      }
      onOpenSetup?.(target.subTab === "talkgroups" ? "Talkgroups" : target.subTab);
      return;
    }
    if (target.topTab === "cpu") setTopTab("services");
    if (target.topTab === "pizzad") {
      if (target.subTab === "storage") setTopTab("storage");
      else if (target.subTab === "jobs") setTopTab("queue");
      else if (target.subTab === "quality") { setTopTab("metrics"); setMetricsTab("transcription"); }
      else setTopTab("services");
    }
    if (target.topTab === "metrics") {
      const metricTabs = ["calls", "transcription", "rf", "incidents", "ai", "bandwidth"] as const;
      const metricTab = metricTabs.find(value => value === target.subTab);
      setTopTab("metrics");
      if (metricTab) setMetricsTab(metricTab);
      return;
    }
    if (target.topTab === "tr") {
      if (target.subTab === "metrics" || target.subTab === "rf") {
        const latestEpisode = [...(item.episodes ?? [])].sort((a, b) => new Date(b.endUtc).getTime() - new Date(a.endUtc).getTime())[0];
        const ageHours = latestEpisode ? Math.max(0, (Date.now() - new Date(latestEpisode.startUtc).getTime()) / 3_600_000) : 2;
        const chartHours = [2, 6, 24, 72, 168].find(hours => ageHours <= hours) ?? 168;
        setSelectedRfFinding(item);
        if (target.anchor) localStorage.setItem("pizzawave-system-rf-selected-site", target.anchor);
        setRfPerformanceHours(chartHours); localStorage.setItem("pizzawave-system-rf-performance-hours", String(chartHours)); setRfChartCategory("all"); setTopTab("metrics"); setMetricsTab("rf");
      }
      else { setTopTab("tr"); setTrTab(target.subTab === "logs" ? "logs" : "summary"); }
    }
    if (target.topTab === "qdrant") setTopTab("services");
    if (target.topTab === "stats") { setTopTab("metrics"); setMetricsTab("calls"); }
    if (target.topTab === "tokens") { setTopTab("metrics"); setMetricsTab("ai"); }
    if (target.topTab === "bandwidth") { setTopTab("metrics"); setMetricsTab("bandwidth"); }
  }
  useEffect(() => {
    if (observedRefreshSignal.current === refreshSignal) return;
    observedRefreshSignal.current = refreshSignal;
    void refreshVisibleSystemPanel();
  }, [refreshSignal]);

  function selectSystemArea(area: SystemArea) {
    setTopTab(rememberedSystemAreaTab(area));
  }

  function openRfPerformance(category: RfChartCategory) {
    setRfChartCategory(category);
    setTopTab("metrics");
    setMetricsTab("rf");
  }

  return (
    <div className="trouble-page system-view">
      <div className="trouble-tabs">
        <button className={systemArea === "recommendations" ? "active" : ""} onClick={() => selectSystemArea("recommendations")}>Recommendations{recommendations && recommendations.openCount > 0 ? ` (${recommendations.openCount})` : ""}</button>
        <button className={systemArea === "runtime" ? "active" : ""} onClick={() => selectSystemArea("runtime")}>Runtime</button>
        <button className={systemArea === "data" ? "active" : ""} onClick={() => selectSystemArea("data")}>Data</button>
        <button className={systemArea === "trunk-recorder" ? "active" : ""} onClick={() => selectSystemArea("trunk-recorder")}>Trunk Recorder</button>
        <button className={systemArea === "performance" ? "active" : ""} onClick={() => selectSystemArea("performance")}>Performance</button>
      </div>
      {systemArea === "runtime" && <div className="trouble-tabs nested">
        <button className={topTab === "services" ? "active" : ""} onClick={() => setTopTab("services")}>Services</button>
        <button className={topTab === "queue" ? "active" : ""} onClick={() => setTopTab("queue")}>Queue</button>
        <button className={topTab === "jobs" ? "active" : ""} onClick={() => setTopTab("jobs")}>Jobs</button>
      </div>}
      {systemArea === "data" && <div className="trouble-tabs nested">
        <button className={topTab === "storage" ? "active" : ""} onClick={() => setTopTab("storage")}>Storage</button>
        <button className={topTab === "backup" ? "active" : ""} onClick={() => setTopTab("backup")}>Backup and Restore</button>
        <button className={topTab === "reset" ? "active" : ""} onClick={() => setTopTab("reset")}>Reset</button>
        <button className={topTab === "audit" ? "active" : ""} onClick={() => setTopTab("audit")}>Audit History</button>
      </div>}
      {topTab !== "tr" && topTab !== "metrics" && <SystemPageIdentity {...pageIdentity} />}
      {topTab === "recommendations" && <div className="trouble-panel"><PanelLoadState label="recommendations" state={recommendationsResource.state} hasData={Boolean(recommendations)} onRetry={recommendationsResource.refresh} /><RecommendationsPanel recommendations={recommendations} onOpen={openRecommendationTarget} onChanged={recommendationsResource.refresh} /></div>}
      {topTab === "services" && <div className="trouble-panel"><PanelLoadState label="service status" state={servicesRuntimeResource.state} hasData={Boolean(servicesRuntime)} onRetry={servicesRuntimeResource.refresh} /><PanelLoadState label="resource status" state={cpuResource.state} hasData={Boolean(cpuSnapshot)} onRetry={cpuResource.refresh} /><PanelLoadState label="live service resources" state={liveServiceResource.state} hasData={serviceResourceHistory.length > 0} onRetry={liveServiceResource.refresh} />{servicesRuntime && <ServicesManager runtime={servicesRuntime} snapshot={cpuSnapshot} history={serviceResourceHistory} restartBusy={restartBusy} restartMessages={restartMessages} onRestart={restartService} onStopTr={stopTrService} />}</div>}
      {topTab === "queue" && <QueuePanel engineHealth={engineHealth} ingestBusy={ingestBusy} ingestMessage={ingestMessage} onSetIngestPaused={setIngestPaused} refreshToken={panelRefreshToken} />}
      {topTab === "jobs" && <div className="trouble-panel"><PanelLoadState label="jobs" state={jobsResource.state} hasData={Boolean(jobs)} onRetry={jobsResource.refresh} />{jobs && <JobsPanel jobs={jobs} reload={async () => { await jobsResource.refresh(); }} />}</div>}
      {topTab === "storage" && <div className="trouble-panel"><PanelLoadState label="storage status" state={storageResource.state} hasData={Boolean(storageRuntime)} onRetry={storageResource.refresh} />{storageRuntime && <PizzadStorageManager snapshot={storageRuntime.snapshot} maintenanceJobs={storageRuntime.jobs} />}</div>}
      {topTab === "backup" && <div className="trouble-panel"><BackupRestorePanel reload={async () => { await refreshSharedStatus(); }} refreshToken={panelRefreshToken} /></div>}
      {topTab === "reset" && <div className="trouble-panel"><SystemResetPanel reload={async () => { await refreshSharedStatus(); }} /></div>}
      {topTab === "audit" && <div className="trouble-panel"><AuditHistoryPanel refreshToken={panelRefreshToken} onOpenJobs={() => setTopTab("jobs")} /></div>}
      {topTab === "tr" && <div className="trouble-panel">
        <div className="trouble-tabs nested">
          <button className={trTab === "summary" ? "active" : ""} onClick={() => setTrTab("summary")}>Summary</button>
          <button className={trTab === "config" ? "active" : ""} onClick={() => setTrTab("config")}>Config</button>
          <button className={trTab === "logs" ? "active" : ""} onClick={() => setTrTab("logs")}>Logs</button>
        </div>
        <SystemPageIdentity {...pageIdentity} />
        {trTab === "summary" && <PanelLoadState label="Trunk Recorder summary" state={receiverSummaryResource.state} hasData={Boolean(receiverSummary)} onRetry={receiverSummaryResource.refresh} />}
        {trTab === "summary" && receiverSummary && <TrConfigurationSummaryView data={receiverSummary} onOpenSetup={onOpenSetup} />}
        {trTab === "config" && <TrConfigViewerPanel onOpenSetup={onOpenSetup} refreshToken={panelRefreshToken} />}
        {trTab === "logs" && <TrLogsPanel refreshToken={panelRefreshToken} />}
      </div>}
      {topTab === "metrics" && <div className="trouble-panel">
        <div className="trouble-tabs nested">
          <button className={metricsTab === "calls" ? "active" : ""} onClick={() => setMetricsTab("calls")}>Calls</button>
          <button className={metricsTab === "transcription" ? "active" : ""} onClick={() => setMetricsTab("transcription")}>Transcription</button>
          <button className={metricsTab === "rf" ? "active" : ""} onClick={() => setMetricsTab("rf")}>Radio Frequency</button>
          <button className={metricsTab === "incidents" ? "active" : ""} onClick={() => setMetricsTab("incidents")}>Incidents</button>
          <button className={metricsTab === "ai" ? "active" : ""} onClick={() => setMetricsTab("ai")}>AI Usage</button>
          <button className={metricsTab === "bandwidth" ? "active" : ""} onClick={() => setMetricsTab("bandwidth")}>Bandwidth</button>
        </div>
        <SystemPageIdentity {...pageIdentity} />
        {metricsTab === "calls" && <><PanelLoadState label="call metrics" state={callsDashboardResource.state} hasData={Boolean(callsDashboard)} onRetry={callsDashboardResource.refresh} /><DashboardStatisticsPanel data={callsDashboard} rangeHours={callsPerformanceHours} onRangeHoursChange={hours => { setCallsPerformanceHours(hours); localStorage.setItem("pizzawave-system-calls-performance-hours", String(hours)); }} onOpenTalkgroup={onOpenTalkgroup} /></>}
        {metricsTab === "transcription" && <><PanelLoadState label="transcription performance" state={performanceSummaryResource.state} hasData={Boolean(performanceSummary)} onRetry={performanceSummaryResource.refresh} />{performanceSummary && <TranscriptionPerformancePanel data={performanceSummary} rangeHours={transcriptionPerformanceHours} onRangeHoursChange={hours => { setTranscriptionPerformanceHours(hours); localStorage.setItem("pizzawave-system-transcription-performance-hours", String(hours)); }} onOpenTalkgroup={onOpenTalkgroup} onExcludeTalkgroup={row => { localStorage.setItem("pizzawave-setup-talkgroup-candidates", JSON.stringify([{ systemShortName: row.systemShortName, id: row.talkgroup }])); localStorage.setItem("pizzawave-setup-talkgroup-exclusion-targets", JSON.stringify([{ systemShortName: row.systemShortName, id: row.talkgroup }])); onOpenSetup?.("Talkgroups"); }} />}</>}
        {metricsTab === "rf" && <><PanelLoadState label="radio frequency metrics" state={metricsDataResource.state} hasData={Boolean(metricsData)} onRetry={metricsDataResource.refresh} />{metricsData && <RfMetricsPanel data={metricsData} rangeHours={rfPerformanceHours} baseline={baseline} category={rfChartCategory} finding={selectedRfFinding} clearFinding={() => setSelectedRfFinding(null)} setRangeHours={value => { setRfPerformanceHours(value); localStorage.setItem("pizzawave-system-rf-performance-hours", String(value)); }} setBaseline={setBaseline} setCategory={setRfChartCategory} />}</>}
        {metricsTab === "incidents" && <><PanelLoadState label="incident output" state={incidentDashboardResource.state} hasData={Boolean(incidentDashboard)} onRetry={incidentDashboardResource.refresh} /><IncidentMetricsPanel dashboard={incidentDashboard} rangeHours={incidentPerformanceHours} refreshToken={panelRefreshToken} onRangeHoursChange={hours => { setIncidentPerformanceHours(hours); localStorage.setItem("pizzawave-system-incident-performance-hours", String(hours)); }} /></>}
        {metricsTab === "ai" && <><PanelLoadState label="AI usage" state={tokenUsageResource.state} hasData={Boolean(tokenUsage)} onRetry={tokenUsageResource.refresh} /><TokenUsagePanel report={tokenUsage} rangeHours={aiPerformanceHours} onRangeHoursChange={hours => { setAiUsagePage(1); setAiPerformanceHours(hours); localStorage.setItem("pizzawave-system-ai-performance-hours", String(hours)); }} onPageChange={setAiUsagePage} /></>}
        {metricsTab === "bandwidth" && <><PanelLoadState label="PizzaWave bandwidth" state={bandwidthUsageResource.state} hasData={Boolean(bandwidthUsage)} onRetry={bandwidthUsageResource.refresh} /><RemoteBandwidthPanel report={bandwidthUsage} rangeHours={bandwidthPerformanceHours} onRangeHoursChange={hours => { setBandwidthUsagePage(1); setBandwidthPerformanceHours(hours); localStorage.setItem("pizzawave-system-bandwidth-performance-hours", String(hours)); }} onPageChange={setBandwidthUsagePage} /></>}
      </div>}
    </div>
  );
}

function TrConfigViewerPanel({ onOpenSetup, refreshToken }: { onOpenSetup?: (section?: string) => void; refreshToken: number }) {
  const [artifactId, setArtifactId] = useState("");
  const [artifactKind, setArtifactKind] = useState("active");
  const [artifactSearch, setArtifactSearch] = useState("");
  const [copied, setCopied] = useState(false);
  const viewerResource = usePersistentRefresh({
    key: `system-tr-config-viewer|${refreshToken}|${artifactId}`,
    enabled: true,
    load: () => api.request<TrConfigViewer>(`/api/v1/system/tr-config/viewer${artifactId ? `?artifactId=${encodeURIComponent(artifactId)}` : ""}`)
  });
  const viewer = viewerResource.data;
  const selected = viewer?.selected;
  const groups = [
    { kind: "active", label: "Active" },
    { kind: "draft", label: "Drafts" },
    { kind: "backup", label: "Safety backups" },
    { kind: "experiment", label: "Experiments" }
  ];
  const comparison = viewer && selected && !selected.artifact.isActive
    ? summarizeTrConfigDiff(viewer.activeConfigJson, selected.configJson)
    : [];
  const kindArtifacts = viewer?.artifacts.filter(row => row.kind === artifactKind) ?? [];
  const normalizedSearch = artifactSearch.trim().toLowerCase();
  const visibleArtifacts = normalizedSearch
    ? kindArtifacts.filter(row => `${row.name} ${row.path} ${row.workflow} ${row.reason} ${row.relatedActivity}`.toLowerCase().includes(normalizedSearch))
    : kindArtifacts;

  function selectArtifactKind(kind: string) {
    setArtifactKind(kind);
    setArtifactSearch("");
    const first = viewer?.artifacts.find(row => row.kind === kind);
    if (first) setArtifactId(first.id);
  }

  async function copyConfig() {
    if (!selected?.configJson) return;
    try {
      await navigator.clipboard.writeText(selected.configJson);
    } catch {
      const field = document.createElement("textarea");
      field.value = selected.configJson;
      field.style.position = "fixed";
      field.style.opacity = "0";
      document.body.appendChild(field);
      field.select();
      document.execCommand("copy");
      field.remove();
    }
    setCopied(true);
    window.setTimeout(() => setCopied(false), 1400);
  }

  if (!viewer) return <PanelLoadState label="Trunk Recorder configuration viewer" state={viewerResource.state} hasData={false} onRetry={viewerResource.refresh} />;
  return <div className="tr-config-viewer">
    <PanelLoadState label="Trunk Recorder configuration viewer" state={viewerResource.state} hasData={true} onRetry={viewerResource.refresh} />
    <SystemPageHeaderControls><div className="config-viewer-controls header-controls">
      <label>Config class
        <select aria-label="Config class" value={artifactKind} onChange={event => selectArtifactKind(event.target.value)}>
          {groups.map(group => <option key={group.kind} value={group.kind} disabled={!viewer.artifacts.some(row => row.kind === group.kind)}>{group.label} ({viewer.artifacts.filter(row => row.kind === group.kind).length.toLocaleString()})</option>)}
        </select>
      </label>
      <label className="config-viewer-search">Find within {groups.find(group => group.kind === artifactKind)?.label.toLowerCase() || "artifacts"}
        <input type="search" value={artifactSearch} onChange={event => setArtifactSearch(event.target.value)} placeholder="Name, workflow, activity, or path" disabled={kindArtifacts.length <= 1} />
      </label>
      <label className="config-viewer-selector">Select a config
        <select aria-label="Select a config" value={viewer.selectedArtifactId} onChange={event => setArtifactId(event.target.value)}>
          {!visibleArtifacts.some(row => row.id === viewer.selectedArtifactId) && selected && <option value={selected.artifact.id}>Current selection · {selected.artifact.name}</option>}
          {visibleArtifacts.map(row => {
            const artifactFile = row.path.split(/[\\/]/).filter(Boolean).at(-1) || "";
            return <option key={row.id} value={row.id}>{row.name}{row.relatedActivity ? ` · ${row.relatedActivity}` : ""}{row.kind === "experiment" && artifactFile ? ` · ${artifactFile}` : ""} · {new Date(row.createdAtUtc).toLocaleString()}</option>;
          })}
        </select>
      </label>
      <button disabled={!selected?.configJson} onClick={() => void copyConfig()}>{copied ? "Copied" : "Copy Config"}</button>
      {onOpenSetup && <button onClick={() => onOpenSetup("Systems & Sites")}>Open Setup</button>}
    </div></SystemPageHeaderControls>

    {selected && <>
      <section className="card config-viewer-metadata system-content-section">
        <SystemSectionHeader title={selected.artifact.name} description={selected.artifact.reason} meta={<span className={`section-status ${selected.artifact.kind === "active" ? "ok" : selected.artifact.kind === "backup" ? "info" : "warning"}`}>{selected.artifact.state}</span>} />
        <div className="config-viewer-facts">
          <div><span>File date</span><strong>{new Date(selected.artifact.createdAtUtc).toLocaleString()}</strong></div>
          <div><span>Workflow</span><strong>{selected.artifact.workflow}</strong></div>
          <div><span>Related activity</span><strong>{selected.artifact.relatedActivity || (selected.artifact.hasRecordedOrigin ? "Not applicable" : "Origin unavailable")}</strong></div>
          <div><span>File</span><code title={selected.artifact.path}>{selected.artifact.path}</code></div>
        </div>
        <div className="config-viewer-summary"><span>{selected.summary.systems.length.toLocaleString()} systems</span><span>{selected.summary.sources.length.toLocaleString()} sources</span><span>{formatBytes(selected.artifact.bytes)}</span><span className={selected.parseOk ? "ok-text" : "error-text"}>{selected.parseOk ? "Valid JSON" : selected.parseMessage}</span></div>
      </section>
      {selected.summary.warnings.length ? <div className="setup-warning-list">{selected.summary.warnings.map(warning => <div key={warning}>{warning}</div>)}</div> : null}
      {comparison.length > 0 && <section className="card config-viewer-comparison system-content-section"><SystemSectionHeader title="Changes from active" /><pre>{comparison.join("\n")}</pre></section>}
      <section className="card config-viewer-json system-content-section"><SystemSectionHeader title="Configuration JSON" meta={<span>Read only</span>} /><pre>{selected.configJson || "No configuration content was returned."}</pre></section>
    </>}
  </div>;
}

function TrLogsPanel({ refreshToken }: { refreshToken: number }) {
  const initialEnd = Math.floor(Date.now() / 1000);
  const [preset, setPreset] = useState("1");
  const [range, setRange] = useState({ start: initialEnd - 3600, end: initialEnd });
  const [customStart, setCustomStart] = useState(() => trLogDateTimeLocal(initialEnd - 3600));
  const [customEnd, setCustomEnd] = useState(() => trLogDateTimeLocal(initialEnd));
  const [cursorStack, setCursorStack] = useState<string[]>([]);
  const [copied, setCopied] = useState(false);
  const observedRefresh = useRef(refreshToken);
  const cursor = cursorStack.at(-1) ?? "";
  const logsResource = usePersistentRefresh({
    key: `system-tr-logs|${refreshToken}|${range.start}|${range.end}|${cursor}`,
    enabled: true,
    load: () => {
      const query = new URLSearchParams({ start: String(range.start), end: String(range.end), pageSize: "250" });
      if (cursor) query.set("cursor", cursor);
      return api.request<TrLogPage>(`/api/v1/system/tr-logs?${query.toString()}`);
    }
  });
  const page = logsResource.data;
  const visibleText = (page?.entries ?? []).map(trLogDisplayLine).join("\n");

  useEffect(() => {
    if (observedRefresh.current === refreshToken) return;
    observedRefresh.current = refreshToken;
    setCursorStack([]);
    if (preset !== "custom") {
      const end = Math.floor(Date.now() / 1000);
      setRange({ start: end - Number(preset) * 3600, end });
    }
  }, [refreshToken]);

  function changePreset(value: string) {
    setPreset(value);
    setCursorStack([]);
    if (value === "custom") return;
    const end = Math.floor(Date.now() / 1000);
    const next = { start: end - Number(value) * 3600, end };
    setRange(next);
    setCustomStart(trLogDateTimeLocal(next.start));
    setCustomEnd(trLogDateTimeLocal(next.end));
  }

  function applyCustomRange() {
    const start = Math.floor(new Date(customStart).getTime() / 1000);
    const end = Math.floor(new Date(customEnd).getTime() / 1000);
    if (!Number.isFinite(start) || !Number.isFinite(end) || end <= start) return;
    setCursorStack([]);
    setRange({ start, end });
  }

  async function copyVisibleLogs() {
    if (!visibleText) return;
    try {
      await navigator.clipboard.writeText(visibleText);
    } catch {
      const field = document.createElement("textarea");
      field.value = visibleText;
      field.style.position = "fixed";
      field.style.opacity = "0";
      document.body.appendChild(field);
      field.select();
      document.execCommand("copy");
      field.remove();
    }
    setCopied(true);
    window.setTimeout(() => setCopied(false), 1400);
  }

  return <div className="tr-log-viewer">
    <PanelLoadState label="Trunk Recorder logs" state={logsResource.state} hasData={Boolean(page)} onRetry={logsResource.refresh} />
    <SystemPageHeaderControls><div className="tr-log-toolbar header-controls">
      <div className="tr-log-controls">
        <label>Range<select aria-label="Log range" value={preset} onChange={event => changePreset(event.target.value)}><option value="1">Last hour</option><option value="6">Last 6 hours</option><option value="24">Last 24 hours</option><option value="custom">Custom</option></select></label>
        {preset === "custom" && <><label>Start<input aria-label="Log start" type="datetime-local" value={customStart} onChange={event => setCustomStart(event.target.value)} /></label><label>End<input aria-label="Log end" type="datetime-local" value={customEnd} onChange={event => setCustomEnd(event.target.value)} /></label><button onClick={applyCustomRange}>Apply</button></>}
      </div>
      <div className="tr-log-actions"><span>Page {cursorStack.length + 1} · {(page?.entries.length ?? 0).toLocaleString()} lines</span><button disabled={!visibleText} onClick={() => void copyVisibleLogs()}>{copied ? "Copied" : "Copy"}</button><button disabled={cursorStack.length === 0 || logsResource.state.loading} onClick={() => setCursorStack(current => current.slice(0, -1))}>Newer</button><button disabled={!page?.hasOlder || !page.olderCursor || logsResource.state.loading} onClick={() => page?.olderCursor && setCursorStack(current => [...current, page.olderCursor])}>Older</button></div>
    </div></SystemPageHeaderControls>
    {page?.error && <p className="settings-message error">{page.error}</p>}
    {page && page.entries.length > 0 ? <pre className="log-box tr-log-output">{visibleText}</pre> : !logsResource.state.loading && <p className="muted">No Trunk Recorder log lines were returned for this range.</p>}
  </div>;
}

function trLogDateTimeLocal(unixSeconds: number) {
  const date = new Date(unixSeconds * 1000);
  const local = new Date(date.getTime() - date.getTimezoneOffset() * 60_000);
  return local.toISOString().slice(0, 16);
}

function trLogDisplayLine(entry: TrLogPage["entries"][number]) {
  const timestamp = new Date(entry.timestampUtc).toLocaleString();
  const process = entry.identifier ? `${entry.identifier}${entry.processId ? `[${entry.processId}]` : ""}:` : "";
  return [timestamp, entry.host, process, entry.message].filter(Boolean).join(" ");
}

function MetricsOverviewPanel({ data, dashboard, engineHealth, navigate }: { data: TrTroubleshoot; dashboard: Dashboard | null; engineHealth: EngineHealth | null; navigate: (top: SystemTopTab, sub?: string) => void }) {
  const audit = data.qualityAudit;
  const rfIssues = data.health.metrics.filter(row => row.isIssue).length + data.health.systems.filter(row => row.isIssue).length;
  const queueDepth = engineHealth?.queueDepth ?? 0;
  const queueState = engineHealth?.ingest?.paused ? "Paused" : queueDepth <= 0 ? "OK" : engineHealth?.queueUnderPressure ? "Pressure" : "Draining";
  const incidentCount = dashboard?.incidents.length ?? 0;
  return <div className="metrics-overview">
    <div className="audit-kpis">
      <Kpi label="Live Queue" value={queueState} status={queueState === "OK" ? "ok" : queueState === "Pressure" || queueState === "Paused" ? "error" : "warning"} subtext={`${queueDepth.toLocaleString()} queued, ${formatDurationMinutes((engineHealth?.pendingAudioSeconds ?? 0) / 60)} pending audio`} onClick={() => navigate("queue")} />
      <Kpi label="Transcript Quality" value={`${audit.problemPercent.toFixed(1)}% problem`} status={audit.problemPercent > 25 ? "error" : audit.problemPercent > 10 ? "warning" : "ok"} subtext={`${audit.problemCalls.toLocaleString()} of ${audit.totalCalls.toLocaleString()} calls flagged`} onClick={() => navigate("metrics", "transcription")} />
      <Kpi label="RF Health" value={rfIssues > 0 ? "Watch" : "OK"} status={rfIssues > 0 ? "warning" : "ok"} subtext={`${rfIssues.toLocaleString()} health issue row(s)`} onClick={() => navigate("metrics", "rf")} />
      <Kpi label="Incidents" value={incidentCount.toLocaleString()} subtext="incident volume in selected range" onClick={() => navigate("metrics", "incidents")} />
    </div>
  </div>;
}

const emptyRfPath = (): RfSurveyPathProfile => ({
  antenna: "",
  antennaType: "yagi",
  antennaMount: "unknown",
  antennaPolarization: "unknown",
  aimedAtSite: "unknown",
  positionNotes: "",
  connectorChain: "",
  coax: "",
  splitterOrMulticoupler: "",
  lna: "",
  filters: "",
  sdrNotes: "",
  observations: "",
  chain: [
    { type: "antenna", label: "Yagi", connectorIn: "", connectorOut: "", length: "", loss: "", power: "", notes: "", connectorOutType: "unknown", connectorOutGender: "unknown", gainDb: "", groundPlane: "unknown" },
    { type: "sdr", label: "Configured SDR", connectorIn: "", connectorOut: "", length: "", loss: "", power: "", notes: "", connectorInType: "unknown", connectorInGender: "unknown" }
  ]
});

function normalizeRfPathProfile(value?: Partial<RfSurveyPathProfile> | null): RfSurveyPathProfile {
  return {
    ...emptyRfPath(),
    ...(value ?? {}),
    chain: value?.chain?.length ? value.chain : emptyRfPath().chain
  };
}

function hasMeaningfulRfPath(value?: RfSurveyPathProfile | null) {
  if (!value) return false;
  return value.antennaMount !== "unknown" ||
    value.antennaPolarization !== "unknown" ||
    value.aimedAtSite !== "unknown" ||
    value.positionNotes.trim() ||
    value.observations.trim() ||
    value.chain.some(item =>
      item.type !== "antenna" && item.type !== "sdr" ||
      !["", "Yagi", "Configured SDR"].includes((item.label ?? "").trim()) ||
      !["", "unknown"].includes((item.connectorIn ?? "").trim()) ||
      !["", "unknown"].includes((item.connectorOut ?? "").trim()) ||
      !["", "unknown"].includes((item.connectorInType ?? "").trim()) ||
      !["", "unknown"].includes((item.connectorOutType ?? "").trim()) ||
      (item.length ?? "").trim() ||
      (item.loss ?? "").trim() ||
      (item.power ?? "").trim() ||
      (item.notes ?? "").trim() ||
      (item.gainDb ?? "").trim() ||
      (item.passband ?? "").trim());
}

function buildSurveySystemDefinitions(selectedNames: string[], scopePlan: SetupCalibrationPlan | null, rrSites: SetupTrConfigSites | null, existing: RfSurveySystem[], radioReferenceSid = ""): RfSurveySystem[] {
  const definitions: RfSurveySystem[] = [];
  const add = (definition: RfSurveySystem) => {
    if (!definition.shortName || definitions.some(row => row.shortName.toLowerCase() === definition.shortName.toLowerCase()))
      return;
    definitions.push(definition);
  };
  const rrCatalogLoaded = Boolean(rrSites);
  for (const name of selectedNames) {
    const rr = rrSites?.sites.find(site => stringEqualsIgnoreCase(site.shortName, name) || stringEqualsIgnoreCase(site.name, name));
    if (rr) {
      add({
        shortName: rr.shortName || rr.name,
        siteLabel: rr.name || rr.shortName,
        controlChannelsHz: rr.controlChannelsMhz.map(value => Math.round(value * 1_000_000)),
        voiceFrequenciesHz: [],
        radioReferenceSid: radioReferenceSid.trim()
      });
      continue;
    }
    const previous = existing.find(system => stringEqualsIgnoreCase(system.shortName, name));
    if (previous) {
      add(previous);
      continue;
    }
    if (rrCatalogLoaded)
      continue;
    const live = scopePlan?.systems.find(system => stringEqualsIgnoreCase(system.shortName, name));
    if (live) {
      add({ shortName: live.shortName, siteLabel: live.shortName, controlChannelsHz: live.controlChannelsHz, voiceFrequenciesHz: live.voiceFrequenciesHz });
      continue;
    }
  }
  return definitions;
}

function radioReferenceCatalogContainsAnySystem(sites: SetupTrConfigSites | null, systemNames: string[]) {
  if (!sites || systemNames.length === 0)
    return false;
  const catalogNames = new Set(sites.sites.flatMap(site => [site.shortName, site.name]).filter(Boolean).map(value => value.toLowerCase()));
  return systemNames.some(name => catalogNames.has(name.toLowerCase()));
}

function buildSiteSourceCoverageRows(systems: RfSurveySystem[], sources: RfSurveySource[]) {
  return systems.map(system => {
    const controlChannels = uniqueSortedFrequencies(system.controlChannelsHz);
    const matchingSources = sources.map(source => {
      const covered = controlChannels.filter(frequency => sourceCoversFrequency(source, frequency));
      return { source, covered };
    }).filter(row => row.covered.length > 0);
    const coveredCount = new Set(matchingSources.flatMap(row => row.covered)).size;
    const firstSource = matchingSources[0]?.source;
    const window = firstSource ? formatFrequencyRange(firstSource.centerHz - firstSource.sampleRate / 2, firstSource.centerHz + firstSource.sampleRate / 2) : "--";
    return {
      shortName: system.shortName,
      label: system.siteLabel || system.shortName,
      controlChannels: controlChannels.map(formatRfHz).join(", ") || "--",
      sources: matchingSources.map(row => `Source ${row.source.index}`).join(", "),
      covered: controlChannels.length > 0 && coveredCount === controlChannels.length,
      partial: coveredCount > 0 && coveredCount < controlChannels.length,
      window
    };
  });
}

function sourceCoversFrequency(source: Pick<RfSurveySource, "centerHz" | "sampleRate">, frequencyHz: number) {
  const rate = Math.max(1, source.sampleRate || 1);
  return frequencyHz >= source.centerHz - rate / 2 && frequencyHz <= source.centerHz + rate / 2;
}

function RfPathStep({ path, setPath, onTouched, onLoadPrevious, busy, headerMode = "full" }: { path: RfSurveyPathProfile; setPath: React.Dispatch<React.SetStateAction<RfSurveyPathProfile>>; onTouched: () => void; onLoadPrevious: () => Promise<void>; busy: string; headerMode?: "full" | "actions" }) {
  const updateChain = (index: number, patch: Partial<RfSurveyPathProfile["chain"][number]>) => { onTouched(); setPath(current => ({ ...current, chain: current.chain.map((item, i) => i === index ? { ...item, ...patch } : item) })); };
  const updatePath = (patch: Partial<RfSurveyPathProfile>) => { onTouched(); setPath(current => ({ ...current, ...patch })); };
  const addItem = () => { onTouched(); setPath(current => ({ ...current, chain: [...current.chain, newRfChainItem()] })); };
  return <div className="rf-step-stack">
    {headerMode === "full" && <div className="rf-chain-head">
      <div><strong>Documented RF hardware path</strong><span>List the physical hardware in signal order from antenna to receiver.</span></div>
      <div className="rf-primary-actions">
        <button disabled={busy === "load-rf-path"} onClick={() => void onLoadPrevious()}>{busy === "load-rf-path" ? "Loading..." : "Load Previous"}</button>
        <button onClick={addItem}>Add hardware item</button>
      </div>
    </div>}
    {headerMode === "actions" && <div className="rf-chain-head"><div><strong>Documented RF hardware path</strong><span>List the physical hardware in signal order from antenna to receiver.</span></div><button type="button" onClick={addItem}>Add hardware item</button></div>}
    <div className="rf-path-notes-grid">
      <label>
        <span>Position notes</span>
        <textarea
          value={path.positionNotes || ""}
          onChange={e => updatePath({ positionNotes: e.target.value })}
          placeholder="Antenna placement, room, window/wall side, height, aim, or recent physical change"
          rows={3}
        />
      </label>
      <label>
        <span>Observations</span>
        <textarea
          value={path.observations || ""}
          onChange={e => updatePath({ observations: e.target.value })}
          placeholder="What changed, why this validation was run, or what the operator saw"
          rows={3}
        />
      </label>
    </div>
    <div className="rf-chain-list">
      {path.chain.map((item, index) => <RfChainItemEditor item={normalizeRfChainItem(item)} index={index} key={index} update={patch => updateChain(index, patch)} remove={() => { onTouched(); setPath(current => ({ ...current, chain: current.chain.filter((_, i) => i !== index) })); }} />)}
    </div>
  </div>;
}

const rfChainTypes = ["antenna", "coax", "splitter", "multicoupler", "lna", "filter", "sdr", "other"];
const rfConnectorTypes = ["n/a", "unknown", "SMA", "RP-SMA", "BNC", "TNC", "N", "F", "PL-259/SO-239", "MCX", "MMCX", "UHF", "FME", "SMP", "bare wire", "other"];
function newRfChainItem(): RfSurveyPathProfile["chain"][number] {
  return { type: "lna", label: "", connectorIn: "", connectorOut: "", length: "", loss: "", power: "", notes: "", connectorInType: "unknown", connectorInGender: "unknown", connectorOutType: "unknown", connectorOutGender: "unknown", powerMethod: "unknown" };
}

function normalizeRfChainItem(item: RfSurveyPathProfile["chain"][number]): RfSurveyPathProfile["chain"][number] {
  return {
    ...item,
    type: rfChainTypes.includes(item.type) ? item.type : "other",
    connectorInType: item.connectorInType || item.connectorIn || (item.type === "antenna" ? "n/a" : "unknown"),
    connectorOutType: item.connectorOutType || item.connectorOut || (item.type === "sdr" ? "n/a" : "unknown"),
    groundPlane: item.groundPlane || "unknown",
    powerPass: item.powerPass || "unknown",
    powerMethod: item.powerMethod || item.power || "unknown"
  };
}

function RfChainItemEditor({ item, index, update, remove }: { item: RfSurveyPathProfile["chain"][number]; index: number; update: (patch: Partial<RfSurveyPathProfile["chain"][number]>) => void; remove: () => void }) {
  const showInput = item.type !== "antenna";
  const showOutput = item.type !== "sdr";
  const sharedConnector = item.type === "splitter";
  return <div className="rf-chain-editor">
    <div className="rf-chain-main">
      <span className="rf-chain-index">{index + 1}</span>
      <label><span>Type</span><select value={item.type} onChange={e => update({ type: e.target.value })}>{rfChainTypes.map(value => <option key={value} value={value}>{label(value)}</option>)}</select></label>
      <label><span>Description / model</span><input value={item.label} onChange={e => update({ label: e.target.value })} placeholder={rfChainDescriptionPlaceholder(item.type)} /></label>
      <ConnectorField labelText="Input connector" type={showInput ? item.connectorInType || "unknown" : "n/a"} disabled={!showInput} update={type => update(sharedConnector ? { connectorInType: type, connectorOutType: type, connectorIn: type, connectorOut: type } : { connectorInType: type, connectorIn: type })} />
      <ConnectorField labelText="Output connector" type={showOutput ? item.connectorOutType || "unknown" : "n/a"} disabled={!showOutput} update={type => update(sharedConnector ? { connectorInType: type, connectorOutType: type, connectorIn: type, connectorOut: type } : { connectorOutType: type, connectorOut: type })} />
      <TypeSpecificRfFields item={item} update={update} />
      <button className="rf-chain-remove" onClick={remove}>Remove</button>
    </div>
  </div>;
}

function ConnectorField({ labelText, type, disabled, update }: { labelText: string; type: string; disabled?: boolean; update: (type: string) => void }) {
  return <label><span>{labelText}</span><select disabled={disabled} value={type} onChange={e => update(e.target.value)}>{rfConnectorTypes.map(value => <option key={value} value={value}>{value}</option>)}</select></label>;
}

function TypeSpecificRfFields({ item, update }: { item: RfSurveyPathProfile["chain"][number]; update: (patch: Partial<RfSurveyPathProfile["chain"][number]>) => void }) {
  if (item.type === "antenna") return <div className="rf-chain-specific">
    <label><span>Gain dBi</span><input value={item.gainDb || ""} onChange={e => update({ gainDb: e.target.value })} placeholder="Gain dBi" /></label>
    <label><span>Ground plane</span><select value={item.groundPlane || ""} onChange={e => update({ groundPlane: e.target.value || "unknown" })}><option value="" disabled>Ground plane?</option>{["unknown", "not needed", "present", "missing"].map(value => <option key={value} value={value}>{label(value)}</option>)}</select></label>
  </div>;
  if (item.type === "splitter") return <div className="rf-chain-specific">
    <label><span>Outputs</span><select value={item.portCount || "2"} onChange={e => update({ portCount: e.target.value })}>{["2", "3", "4", "6", "8", "other", "unknown"].map(value => <option key={value} value={value}>{value}</option>)}</select></label>
    <label><span>DC pass</span><select value={item.powerPass || ""} onChange={e => update({ powerPass: e.target.value || "unknown" })}><option value="" disabled>DC pass?</option>{["unknown", "no", "all ports", "one port"].map(value => <option key={value} value={value}>{label(value)}</option>)}</select></label>
  </div>;
  if (item.type === "multicoupler") return <div className="rf-chain-specific">
    <label><span>Outputs</span><select value={item.portCount || "4"} onChange={e => update({ portCount: e.target.value })}>{["2", "4", "8", "16", "other", "unknown"].map(value => <option key={value} value={value}>{value}</option>)}</select></label>
    <label><span>Power</span><select value={item.powerMethod || ""} onChange={e => update({ powerMethod: e.target.value || "unknown", power: e.target.value || "unknown" })}><option value="" disabled>Power?</option>{["unknown", "passive", "external power", "usb", "bias tee"].map(value => <option key={value} value={value}>{label(value)}</option>)}</select></label>
  </div>;
  if (item.type === "lna") return <div className="rf-chain-specific">
    <label><span>Gain dB</span><input value={item.gainDb || ""} onChange={e => update({ gainDb: e.target.value })} placeholder="Gain dB" /></label>
    <label><span>Power</span><select value={item.powerMethod || ""} onChange={e => update({ powerMethod: e.target.value || "unknown", power: e.target.value || "unknown" })}><option value="" disabled>Power?</option>{["unknown", "bias tee", "usb", "external power", "inline powered"].map(value => <option key={value} value={value}>{label(value)}</option>)}</select></label>
  </div>;
  if (item.type === "coax") return <div className="rf-chain-specific">
    <label><span>Length (ft)</span><input value={item.length || ""} onChange={e => update({ length: e.target.value })} placeholder="feet" /></label>
    <label><span>Loss</span><input value={item.loss || ""} onChange={e => update({ loss: e.target.value })} placeholder="Loss, optional" /></label>
  </div>;
  if (item.type === "filter") return <div className="rf-chain-specific">
    <label><span>Passband</span><input value={item.passband || ""} onChange={e => update({ passband: e.target.value })} placeholder="e.g. 851-869 MHz" /></label>
    <label><span>Length (ft)</span><input value={item.length || ""} onChange={e => update({ length: e.target.value })} placeholder="optional cable tail in feet" /></label>
  </div>;
  if (item.type === "sdr") return <div className="rf-chain-specific rf-chain-specific-empty"><span>Use Description / model for SDR type or model.</span></div>;
  return <div className="rf-chain-specific">
    <label><span>Length (ft)</span><input value={item.length || ""} onChange={e => update({ length: e.target.value })} placeholder="only if relevant" /></label>
    <label><span>Notes</span><input value={item.notes || ""} onChange={e => update({ notes: e.target.value })} placeholder="what makes this item relevant" /></label>
  </div>;
}

function rfChainDescriptionPlaceholder(type: string) {
  switch (type) {
    case "antenna": return "Yagi 806-900 MHz";
    case "coax": return "LMR-240";
    case "splitter": return "2-way splitter";
    case "multicoupler": return "Active multicoupler";
    case "lna": return "LNA model";
    case "filter": return "Band-pass filter";
    case "sdr": return "RTL-SDR Blog V4";
    default: return "Part description";
  }
}

type SweepPrecision = "quick" | "balanced" | "deep" | "custom";
type SweepSourceInput = { precision: SweepPrecision; gain: string; errorHz: string; ppm: string; rangeHz: string; stepHz: string; warmupSec: string; durationSec: string };
type SweepCandidate = {
  errorHz: number;
  totalDecode?: number;
  hasDecodeSamples?: boolean;
  avgDecodeRate: number;
  maxDecodeRate: number;
  decode0Pct: number;
  retunes: number;
  callsStarted: number;
  callsConcluded: number;
  noTxRecorded: number;
  metricWarning?: string;
  score: number;
};
type SweepResult = { jobId?: number; sourceIndex: number; serial: string; gain: string; outputDir: string; summaryPath: string; bestPath: string; best?: SweepCandidate | null; candidates: SweepCandidate[] };
type SweepInsight = { recommendation: string; confidence: string; rationale: string; nextActions: string[]; rawText: string };
type SweepHistoryEntry = { jobId?: number; capturedAtUtc: string; sourceIndex: number; bestErrorHz: number; bestScore: number; bestAvgDecodeRate: number; bestTotalDecode: number; bestHasDecodeSamples: boolean };
type RfRunLogLine = { id: string; level: "info" | "result" | "error"; text: string; createdAtUtc: string };

const AIRSPY_LINEARITY_GAIN_MAX = 21;

const sweepPrecisionPresets: Record<Exclude<SweepPrecision, "custom">, Pick<SweepSourceInput, "rangeHz" | "stepHz" | "warmupSec" | "durationSec">> = {
  quick: { rangeHz: "600", stepHz: "300", warmupSec: "5", durationSec: "20" },
  balanced: { rangeHz: "600", stepHz: "150", warmupSec: "10", durationSec: "45" },
  deep: { rangeHz: "900", stepHz: "100", warmupSec: "15", durationSec: "90" }
};

function sweepReliabilityLabel(input: SweepSourceInput) {
  if (input.precision === "deep") return "Deep: slower, best confidence";
  if (input.precision === "balanced") return "Balanced: moderate confidence";
  if (input.precision === "quick") return "Quick: triage only";
  return "Custom";
}

function defaultSweepInput(source?: Pick<RfSurveySource, "gain" | "errorHz" | "sdrType" | "device">): SweepSourceInput {
  return {
    precision: "quick",
    gain: source?.gain || (source && isAirspyRfSource(source) ? "15" : ""),
    errorHz: source?.errorHz ? String(source.errorHz) : "",
    ppm: "",
    ...sweepPrecisionPresets.quick
  };
}

function sweepPassCountFor(input: SweepSourceInput) {
  return Math.floor((Math.max(0, numberOrDefault(input.rangeHz, 600)) * 2) / Math.max(1, numberOrDefault(input.stepHz, 300))) + 1;
}

function sweepSecondsFor(input: SweepSourceInput) {
  return sweepPassCountFor(input) * (Math.max(0, numberOrDefault(input.warmupSec, 5)) + Math.max(1, numberOrDefault(input.durationSec, 20)));
}

function buildSweepSourceStates(sourceIndexes: number[], job: Job | null, logs: JobLog[]) {
  const states: Record<string, string> = {};
  sourceIndexes.forEach(index => { states[String(index)] = job ? "Queued" : "Pending"; });
  for (const log of logs) {
    const started = log.text.match(/Starting source\s+(\d+)/i);
    if (started) states[started[1]] = "Running";
    const completed = log.text.match(/Completed sweep for source\s+(\d+)/i);
    if (completed) states[completed[1]] = "Completed";
  }
  if (job?.status === "failed") {
    const running = Object.entries(states).find(([, state]) => state === "Running");
    if (running) states[running[0]] = "Failed";
  }
  if (job?.status === "completed") {
    sourceIndexes.forEach(index => {
      if (states[String(index)] !== "Completed")
        states[String(index)] = "Completed";
    });
  }
  return states;
}

function measurementDone(status?: string) {
  return status === "passed" || status === "completed";
}

function parseSweepResults(logs: JobLog[]) {
  const results: Record<string, SweepResult> = {};
  for (const log of logs) {
    const marker = "SWEEP_RESULT_JSON ";
    const index = log.text.indexOf(marker);
    if (index < 0) continue;
    try {
      const parsed = JSON.parse(log.text.slice(index + marker.length)) as SweepResult;
      const candidates = [...(parsed.candidates ?? [])].map(normalizeSweepCandidate).sort((a, b) => b.score - a.score);
      const best = parsed.best ? normalizeSweepCandidate(parsed.best) : candidates[0] ?? null;
      results[String(parsed.sourceIndex)] = { ...parsed, jobId: log.jobId, best, candidates };
    } catch {
      // Ignore malformed result logs; raw output remains available in the job feed.
    }
  }
  return results;
}

function normalizeSweepCandidate(candidate: SweepCandidate): SweepCandidate {
  const totalDecode = candidate.totalDecode ?? 0;
  const hasDecodeSamples = candidate.hasDecodeSamples ?? totalDecode > 0;
  const decode0Pct = hasDecodeSamples ? (candidate.decode0Pct ?? 0) : 0;
  const avgDecodeRate = hasDecodeSamples ? (candidate.avgDecodeRate ?? 0) : 0;
  return {
    ...candidate,
    totalDecode,
    hasDecodeSamples,
    decode0Pct,
    avgDecodeRate,
    maxDecodeRate: hasDecodeSamples ? (candidate.maxDecodeRate ?? 0) : 0,
    metricWarning: hasDecodeSamples ? candidate.metricWarning : candidate.metricWarning || "No parser-visible control-channel message-rate samples were found in this measurement window; call counts are informational, but frequency-correction ranking is advisory until a rerun captures comparable samples.",
    score: hasDecodeSamples ? (1000 - decode0Pct) * 1_000_000 + avgDecodeRate * 1000 + (candidate.callsConcluded ?? 0) : -1_000_000 + (candidate.callsConcluded ?? 0)
  };
}

function formatSweepRate(candidate: SweepCandidate) {
  return candidate.hasDecodeSamples ? `${candidate.avgDecodeRate.toFixed(2)}/sec` : "N/A";
}

function formatSweepPercent(candidate: SweepCandidate) {
  return candidate.hasDecodeSamples ? `${candidate.decode0Pct.toFixed(1)}%` : "N/A";
}

function loadJsonStorage<T>(key: string, fallback: T): T {
  try {
    const raw = localStorage.getItem(key);
    return raw ? JSON.parse(raw) as T : fallback;
  } catch {
    return fallback;
  }
}

function parseGainSequence(value: string) {
  const parsed = value
    .split(",")
    .map(item => item.trim())
    .filter(Boolean);
  return parsed.length ? parsed : ["0", "8", "14", "20", "21"];
}

function isAirspyRfSource(source: Pick<RfSurveySource, "sdrType" | "device">) {
  return source.sdrType?.toLowerCase() === "airspy" || source.device?.toLowerCase().includes("airspy=");
}

function effectivePowerGainSequence(gains: string[], sources: RfSurveySource[]) {
  if (!sources.length || !sources.every(isAirspyRfSource))
    return gains;
  const airspyGains = gains.filter(gain => {
    const value = Number(gain);
    return Number.isInteger(value) && value >= 0 && value <= AIRSPY_LINEARITY_GAIN_MAX;
  });
  return airspyGains.length ? airspyGains : ["15"];
}

function validateAirspyLinearityGain(value: string) {
  const parsed = Number(value);
  return Number.isInteger(parsed) && parsed >= 0 && parsed <= AIRSPY_LINEARITY_GAIN_MAX;
}

function airspyGainNotice(sources: RfSurveySource[]) {
  return sources.some(isAirspyRfSource)
    ? `Airspy detected: RF captures use linearity gain 0-${AIRSPY_LINEARITY_GAIN_MAX}.`
    : "";
}

function parseIntegerSequence(value: string, fallback: number[]) {
  const parsed = value
    .split(",")
    .map(item => Number(item.trim()))
    .filter(value => Number.isFinite(value))
    .map(value => Math.round(value));
  return parsed.length ? Array.from(new Set(parsed)) : fallback;
}

function sweepHistoryEntry(result: SweepResult): SweepHistoryEntry | null {
  const best = result.candidates[0];
  if (!best) return null;
  return {
    jobId: result.jobId,
    capturedAtUtc: new Date().toISOString(),
    sourceIndex: result.sourceIndex,
    bestErrorHz: best.errorHz,
    bestScore: best.score,
    bestAvgDecodeRate: best.avgDecodeRate,
    bestTotalDecode: best.totalDecode ?? 0,
    bestHasDecodeSamples: best.hasDecodeSamples ?? false
  };
}

function compareSweepRun(current?: SweepCandidate, previous?: SweepHistoryEntry) {
  if (!current || !previous) return null;
  if (!(current.hasDecodeSamples ?? false))
    return { tone: "warning", text: "Comparison is limited because this run did not capture comparable CC message-rate samples." };
  if (!previous.bestHasDecodeSamples)
    return null;
  const currentAvg = current.avgDecodeRate ?? 0;
  const delta = currentAvg - previous.bestAvgDecodeRate;
  if (delta < -1)
    return { tone: "warning", text: `This run looks worse than the prior best: frequency correction ${formatSignedHz(current.errorHz)} averaged ${currentAvg.toFixed(1)}/sec versus ${formatSignedHz(previous.bestErrorHz)} at ${previous.bestAvgDecodeRate.toFixed(1)}/sec. Consider returning to ${formatSignedHz(previous.bestErrorHz)} or sweeping the other direction.` };
  if (delta > 1)
    return { tone: "ok", text: `This run improved on the prior best: frequency correction ${formatSignedHz(current.errorHz)} averaged ${currentAvg.toFixed(1)}/sec versus ${formatSignedHz(previous.bestErrorHz)} at ${previous.bestAvgDecodeRate.toFixed(1)}/sec.` };
  return { tone: "neutral", text: `This run is roughly tied with the prior best frequency correction (${formatSignedHz(previous.bestErrorHz)}). Prefer the candidate with more samples, fewer zero-decode windows, and fewer retunes.` };
}

type RfRefinementSubpage = "path" | "cc" | "inventory" | "waterfall" | "power" | "p25" | "sweep";

function normalizeControlChannelSelection(values: unknown): number[] {
  return Array.from(new Set((Array.isArray(values) ? values : [])
    .map(value => Number(value))
    .filter(value => Number.isFinite(value) && value > 0)
    .map(value => Math.round(value))))
    .sort((left, right) => left - right);
}

type WaterfallSweepSelection = {
  frequencyHz: number;
  sourceIndex?: number;
  sourceSerial?: string;
  gain?: string;
  sampleRateHz?: number;
  errorHz?: number;
  offsetHz?: number;
  snrDb?: number;
  confidence?: number;
};

function normalizeWaterfallSweepSelections(values: unknown): WaterfallSweepSelection[] {
  const items = Array.isArray(values) ? values : [];
  const selections = new Map<string, WaterfallSweepSelection>();
  for (const item of items) {
    const rawFrequency = typeof item === "object" && item != null
      ? Number((item as Record<string, unknown>).frequencyHz ?? (item as Record<string, unknown>).controlChannelHz)
      : Number(item);
    if (!Number.isFinite(rawFrequency) || rawFrequency <= 0)
      continue;
    const frequencyHz = Math.round(rawFrequency);
    if (typeof item !== "object" || item == null) {
      selections.set(`unassigned:${frequencyHz}`, { frequencyHz });
      continue;
    }
    const row = item as Record<string, unknown>;
    const sourceIndex = Number(row.sourceIndex);
    const sourceSerial = typeof row.sourceSerial === "string" ? row.sourceSerial.trim() : "";
    const sampleRateHz = Number(row.sampleRateHz);
    const errorHz = Number(row.measuredSignalOffsetHz ?? row.errorHz ?? row.offsetHz);
    const snrDb = Number(row.snrDb);
    const confidence = Number(row.confidence);
    const sourceKey = Number.isFinite(sourceIndex) ? `index:${Math.round(sourceIndex)}` : sourceSerial ? `serial:${sourceSerial.toLowerCase()}` : "unassigned";
    selections.set(`${sourceKey}:${frequencyHz}`, {
      frequencyHz,
      ...(Number.isFinite(sourceIndex) ? { sourceIndex: Math.round(sourceIndex) } : {}),
      ...(sourceSerial ? { sourceSerial } : {}),
      ...(typeof row.gain === "string" && row.gain.trim() ? { gain: row.gain.trim() } : {}),
      ...(Number.isFinite(sampleRateHz) && sampleRateHz > 0 ? { sampleRateHz: Math.round(sampleRateHz) } : {}),
      ...(Number.isFinite(errorHz) ? { errorHz: Math.round(errorHz) } : {}),
      ...(Number.isFinite(snrDb) ? { snrDb } : {}),
      ...(Number.isFinite(confidence) ? { confidence: clamp01(confidence) } : {})
    });
  }
  return [...selections.values()].sort((left, right) => (left.sourceIndex ?? Number.MAX_SAFE_INTEGER) - (right.sourceIndex ?? Number.MAX_SAFE_INTEGER) || left.frequencyHz - right.frequencyHz);
}

function toSiteSetupRfSelectionPayload(row: WaterfallSweepSelection) {
  return {
    frequencyHz: row.frequencyHz,
    ...(row.sourceIndex != null ? { sourceIndex: row.sourceIndex } : {}),
    ...(row.sourceSerial ? { sourceSerial: row.sourceSerial } : {}),
    gain: row.gain ?? "",
    ...(row.sampleRateHz != null ? { sampleRateHz: row.sampleRateHz } : {}),
    ...(row.errorHz != null ? { measuredSignalOffsetHz: row.errorHz } : {}),
    ...(row.snrDb != null ? { snrDb: row.snrDb } : {}),
    ...(row.confidence != null ? { confidence: row.confidence } : {})
  };
}

function formatSignedHz(value: number) {
  return `${value >= 0 ? "+" : ""}${formatFixed(value, 0)} Hz`;
}

function SiteValidationStep({
  apiBase = siteSetupRfApi,
  activeOperation,
  busy,
  ccQuality,
  staleCcQuality,
  ccQualityRuns,
  inventory,
  powerScan,
  validationSweep,
  stalePowerScan,
  p25,
  staleP25,
  sweep,
  staleSweep,
  nextExperiments,
  surveyId,
  systemShortName,
  systems,
  sources,
  controlChannels,
  activeControlChannelHz,
  setActiveControlChannelHz,
  duration,
  setDuration,
  selectedSources,
  setSdrSources,
  onSdrTouched,
  onStopAndInventory,
  onRunP25,
  onRunExperiment,
  onReload,
  onShowDetails,
  onOpenRunLog,
  waterfallSweepSelections,
  onWaterfallSweepSelections,
  onAcceptSweepCandidate,
  onSweepRecovered,
  inventoryRequired = true
}: {
  apiBase?: string;
  activeOperation: Exclude<RfRefinementSubpage, "path">;
  busy: string;
  ccQuality?: RfSurveyExperiment;
  staleCcQuality?: RfSurveyExperiment;
  ccQualityRuns: RfSurveyExperiment[];
  inventory?: RfSurveyExperiment;
  powerScan?: RfSurveyExperiment;
  validationSweep?: RfSurveyExperiment;
  stalePowerScan?: RfSurveyExperiment;
  p25?: RfSurveyExperiment;
  staleP25?: RfSurveyExperiment;
  sweep?: RfSurveyExperiment;
  staleSweep?: RfSurveyExperiment;
  nextExperiments: RfSurveyExperimentPlan[];
  surveyId: string;
  systemShortName: string;
  systems: RfSurveySystem[];
  sources: RfSurveySource[];
  controlChannels: number[];
  activeControlChannelHz: number;
  setActiveControlChannelHz: (value: number) => void;
  duration: string;
  setDuration: (value: string) => void;
  selectedSources: number[];
  setSdrSources: React.Dispatch<React.SetStateAction<RfSurveySource[] | null>>;
  onSdrTouched: () => void;
  onStopAndInventory: () => Promise<void>;
  onRunP25: (controlChannelHz?: number) => Promise<void>;
  onRunExperiment: (type: string, estimate: string, controlChannelHz?: number, extraRequest?: Record<string, unknown>) => Promise<RfSurveyExperiment | undefined>;
  onReload: () => Promise<void>;
  onShowDetails: (value: { title: string; body: React.ReactNode } | null) => void;
  onOpenRunLog: () => void;
  waterfallSweepSelections?: WaterfallSweepSelection[];
  onWaterfallSweepSelections?: (values: WaterfallSweepSelection[]) => void;
  onAcceptSweepCandidate?: (source: RfSurveySource, result: SweepResult, candidate: SweepCandidate) => Promise<void>;
  onSweepRecovered?: (status: string) => void;
  inventoryRequired?: boolean;
}) {
  const [sweepBusy, setSweepBusy] = useState("");
  const [sweepJob, setSweepJob] = useState<Job | null>(null);
  const [sweepLogs, setSweepLogs] = useState<JobLog[]>([]);
  const [sweepDrawerOpen, setSweepDrawerOpen] = useState(false);
  const [sweepMessage, setSweepMessage] = useState("");
  const [sweepInputs, setSweepInputs] = useState<Record<string, SweepSourceInput>>({});
  const [selectedSweepCandidates, setSelectedSweepCandidates] = useState<Record<string, number>>({});
  const [sweepInsights, setSweepInsights] = useState<Record<string, SweepInsight>>({});
  const [highlightedSweepSource, setHighlightedSweepSource] = useState<number | null>(null);
  const [sweepHistory, setSweepHistory] = useState<SweepHistoryEntry[]>([]);
  const [powerGainSequence, setPowerGainSequence] = useState("0,8,14,20,21");
  const [validationErrorOffsets, setValidationErrorOffsets] = useState("auto");
  const [validationMetricsCandidates, setValidationMetricsCandidates] = useState("2");
  const [validationProgress, setValidationProgress] = useState<RfSurveySweepProgress | null>(null);
  const [validationTargetSystem, setValidationTargetSystem] = useState<RfSurveySystem | null>(null);
  const [validationCancelMessage, setValidationCancelMessage] = useState("");
  const selectedCcHz = activeControlChannelHz ? String(activeControlChannelHz) : "";
  const selectedCcNumber = Number(selectedCcHz) || undefined;
  const updateSelectedCc = (value: string) => setActiveControlChannelHz(Number(value) || 0);
  const sweepLogLastId = useRef(0);
  const sweepSources = sources.filter(source => selectedSources.includes(source.index));
  const effectiveSweepSources = sweepSources.length ? sweepSources : sources.slice(0, 1);
  const effectivePowerSources = effectiveSweepSources;
  const powerGains = parseGainSequence(powerGainSequence);
  const effectivePowerGains = effectivePowerGainSequence(powerGains, effectivePowerSources);
  const airspyPowerGainMessage = airspyGainNotice(effectivePowerSources);
  const airspyPowerGainInvalid = effectivePowerSources.some(isAirspyRfSource) && powerGains.some(gain => !validateAirspyLinearityGain(gain));
  const selectedSampleRates = effectivePowerSources.map(source => source.sampleRate).filter(rate => Number.isFinite(rate) && rate > 0);
  const defaultSweepSampleRate = selectedSampleRates.length ? selectedSampleRates[0] : 2_400_000;
  const sweepSampleRatesSame = selectedSampleRates.length > 0 && selectedSampleRates.every(rate => rate === defaultSweepSampleRate);
  const [validationSampleRateMhz, setValidationSampleRateMhz] = useState(() => sweepSampleRatesSame ? formatMhzInput(defaultSweepSampleRate) : "");
  const parsedValidationSampleRateHz = Math.round(Number(validationSampleRateMhz) * 1_000_000);
  const validationSampleRateMessage = validateRfSweepSampleRate(parsedValidationSampleRateHz);
  const validationSampleRateOk = !validationSampleRateMessage;
  const validationMetricCount = Math.max(1, Math.min(3, Number(validationMetricsCandidates) || 2));
  const validationRfCandidateLimit = 3;
  const selectedWaterfallSweepSelections = normalizeWaterfallSweepSelections(waterfallSweepSelections);
  const selectedWaterfallSweepControlChannels = normalizeControlChannelSelection(selectedWaterfallSweepSelections.map(row => row.frequencyHz));
  const hasWaterfallSweepHandoff = selectedWaterfallSweepSelections.length > 0;
  const validationRunControlChannels = selectedWaterfallSweepControlChannels.length ? selectedWaterfallSweepControlChannels : controlChannels;
  const selectedWaterfallSourceIndexes = uniqueNonNegativeIntegers(selectedWaterfallSweepSelections.map(row => row.sourceIndex));
  const selectedWaterfallSourceMeasurements = selectedWaterfallSweepSelections
    .filter(row => row.sourceIndex != null)
    .map(row => ({
      sourceIndex: row.sourceIndex!,
      sourceSerial: row.sourceSerial ?? "",
      controlChannelHz: row.frequencyHz,
      gain: row.gain ?? "",
      ...(row.sampleRateHz != null ? { sampleRateHz: row.sampleRateHz } : {}),
      ...(row.errorHz != null ? { measuredSignalOffsetHz: row.errorHz } : {}),
      ...(row.snrDb != null ? { snrDb: row.snrDb } : {}),
      ...(row.confidence != null ? { confidence: row.confidence } : {})
    }));
  const invalidWaterfallSourceSelection = selectedWaterfallSweepSelections.find(row => {
    if (row.sourceIndex == null)
      return true;
    const source = effectivePowerSources.find(candidate => candidate.index === row.sourceIndex);
    return !source || Boolean(row.sourceSerial && source.serial && row.sourceSerial.toLowerCase() !== source.serial.toLowerCase());
  });
  const selectedWaterfallPowerSources = selectedWaterfallSourceIndexes.length
    ? effectivePowerSources.filter(source => selectedWaterfallSourceIndexes.includes(source.index))
    : [];
  const validationPowerSources = selectedWaterfallPowerSources.length ? selectedWaterfallPowerSources : effectivePowerSources;
  const validationHandoffSourceIndex = selectedWaterfallSourceIndexes.length === 1 ? selectedWaterfallSourceIndexes[0] : undefined;
  const validationOffsets = parseIntegerSequence(validationErrorOffsets, [-300, 0, 300]);
  const validationAutoError = validationErrorOffsets.trim().toLowerCase() === "auto";
  const validationFormErrorSearch = validationErrorOffsets;
  const selectedWaterfallGains = uniqueCaseInsensitive(selectedWaterfallSweepSelections.map(row => row.gain ?? ""));
  const waterfallSeedGainSequence = selectedWaterfallSourceIndexes.length <= 1 && selectedWaterfallGains.length ? effectivePowerGainSequence(selectedWaterfallGains, validationPowerSources).join(",") : "";
  const validationPowerGains = effectivePowerGainSequence(parseGainSequence(powerGainSequence), validationPowerSources);
  const selectedWaterfallSampleRates = uniqueSortedFrequencies(selectedWaterfallSweepSelections.map(row => row.sampleRateHz ?? 0));
  const waterfallSampleRateHz = selectedWaterfallSourceIndexes.length <= 1 && selectedWaterfallSampleRates.length === 1 ? selectedWaterfallSampleRates[0] : 0;
  const waterfallSeedSampleRateMhz = waterfallSampleRateHz ? formatMhzInput(waterfallSampleRateHz) : "";
  const validationRequestSampleRateHz = validationSampleRateOk ? parsedValidationSampleRateHz : 0;
  const validationRequestSampleRateOk = selectedWaterfallSourceMeasurements.length
    ? selectedWaterfallSourceMeasurements.every(row => Number.isFinite(row.sampleRateHz) && (row.sampleRateHz ?? 0) > 0)
    : validationSampleRateOk;
  const validationFormSampleRateMhz = validationSampleRateMhz;
  const validationFormGainSequence = powerGainSequence;
  const validationPowerGainInvalid = selectedWaterfallSourceMeasurements.length
    ? selectedWaterfallSourceMeasurements.some(row => {
      const source = validationPowerSources.find(candidate => candidate.index === row.sourceIndex);
      return Boolean(source && isAirspyRfSource(source) && !validateAirspyLinearityGain(row.gain || source.gain));
    })
    : validationPowerSources.some(isAirspyRfSource) && validationPowerGains.some(gain => !validateAirspyLinearityGain(gain));
  const powerSweepPasses = selectedWaterfallSourceMeasurements.length
    ? selectedWaterfallSourceMeasurements.length
    : Math.max(1, validationPowerSources.length) * Math.max(1, validationRunControlChannels.length) * Math.max(1, validationPowerGains.length);
  const validationP25SeedCount = Math.max(1, Math.min(validationRfCandidateLimit, powerSweepPasses, validationRunControlChannels.length + (systems.length || 1)));
  const validationProbePasses = validationP25SeedCount * Math.max(1, validationOffsets.length);
  const validationMetricRunCount = hasWaterfallSweepHandoff ? Math.min(1, validationP25SeedCount) : Math.max(1, Math.min(validationMetricCount, validationP25SeedCount));
  const validationVoiceCandidateCount = hasWaterfallSweepHandoff
    ? Math.min(1, validationP25SeedCount)
    : Math.max(2, Math.min(3, systems.length || 1));
  const validationVoiceSeconds = 45;
  const validationTrStartupAllowanceSeconds = 60;
  const validationEstimatePadSeconds = hasWaterfallSweepHandoff ? 15 : 45;
  const validationEstimateSeconds = powerSweepPasses * 2 + validationProbePasses * 10 + validationMetricRunCount * 15 + validationVoiceCandidateCount * (validationVoiceSeconds + validationTrStartupAllowanceSeconds) + validationEstimatePadSeconds;
  const validationRunning = busy === "rf_validation_sweep";
  const validationPlan = nextExperiments.find(plan => plan.type === "rf_validation_sweep");
  const validationSweepStatus = rfValidationEffectiveStatus(validationSweep, systems);
  const validationBlocked = validationPlan?.enabled === false || Boolean(invalidWaterfallSourceSelection);
  const validationBlocker = invalidWaterfallSourceSelection
    ? `A Waterfall result at ${formatRfHz(invalidWaterfallSourceSelection.frequencyHz)} is not attached to an available SDR. Clear it and select Use again from the SDR that will receive this site.`
    : validationPlan?.blockingIssue ?? "";
  const validationResultRows = useMemo(() => rfSweepProgressRowsFromExperiment(validationSweep), [validationSweep?.id, validationSweep?.evidenceJson]);
  const validationResultCandidates = useMemo(() => rfSweepCandidateProgressFromExperiment(validationSweep), [validationSweep?.id, validationSweep?.evidenceJson]);
  const showLiveValidationProgress = validationRunning || validationProgress?.active === true;
  const activeValidationTarget = showLiveValidationProgress ? validationTargetSystem : null;
  const activeValidationControlChannels = activeValidationTarget?.controlChannelsHz?.length ? activeValidationTarget.controlChannelsHz : validationRunControlChannels;
  const activeValidationPowerSources = activeValidationTarget ? effectivePowerSources : validationPowerSources;
  const activeValidationPowerGains = activeValidationTarget ? effectivePowerGains : validationPowerGains;
  const activeValidationPowerPasses = activeValidationTarget
    ? Math.max(1, activeValidationPowerSources.length) * Math.max(1, activeValidationControlChannels.length) * Math.max(1, activeValidationPowerGains.length)
    : powerSweepPasses;
  const activeValidationP25SeedCount = Math.max(1, Math.min(validationRfCandidateLimit, activeValidationPowerPasses, activeValidationControlChannels.length + (activeValidationTarget ? 1 : systems.length || 1)));
  const activeValidationProbePasses = activeValidationP25SeedCount * Math.max(1, validationOffsets.length);
  const activeValidationMetricRunCount = !activeValidationTarget && hasWaterfallSweepHandoff ? Math.min(1, activeValidationP25SeedCount) : Math.max(1, Math.min(validationMetricCount, activeValidationP25SeedCount));
  const activeValidationVoiceCandidateCount = activeValidationTarget ? Math.max(1, Math.min(2, activeValidationControlChannels.length)) : validationVoiceCandidateCount;
  const activeValidationEstimateSeconds = activeValidationPowerPasses * 2 + activeValidationProbePasses * 10 + activeValidationMetricRunCount * 15 + activeValidationVoiceCandidateCount * (validationVoiceSeconds + validationTrStartupAllowanceSeconds) + (activeValidationTarget ? 30 : validationEstimatePadSeconds);
  const validationProgressRows = showLiveValidationProgress ? validationProgress?.rows ?? [] : validationResultRows;
  const validationProgressCandidates = showLiveValidationProgress ? validationProgress?.candidates ?? [] : validationResultCandidates;
  const hideStaleSiteReadiness = showLiveValidationProgress && !activeValidationTarget;
  const sweepStorageKey = `pizzawave-setup-sweep-job-${surveyId}`;
  const sweepInsightStorageKey = `pizzawave-setup-sweep-insights-${surveyId}`;
  const sweepHistoryStorageKey = `pizzawave-setup-sweep-history-${surveyId}`;
  const sweepRecoveryKey = useRef("");
  const validationHandoffSeedKey = useRef("");
  const inventorySatisfied = inventoryRequired === false || Boolean(inventory);
  useEffect(() => {
    if (controlChannels.length && !controlChannels.includes(activeControlChannelHz))
      setActiveControlChannelHz(controlChannels[0]);
  }, [activeControlChannelHz, controlChannels.join(",")]);
  useEffect(() => {
    setSweepInputs(current => {
      const next: Record<string, SweepSourceInput> = {};
      for (const source of effectiveSweepSources)
        next[String(source.index)] = current[String(source.index)] ?? defaultSweepInput(source);
      return JSON.stringify(next) === JSON.stringify(current) ? current : next;
    });
  }, [effectiveSweepSources.map(source => `${source.index}:${source.gain}:${source.errorHz}`).join("|")]);
  useEffect(() => {
    setValidationSampleRateMhz(sweepSampleRatesSame ? formatMhzInput(defaultSweepSampleRate) : "");
  }, [effectiveSweepSources.map(source => `${source.index}:${source.sampleRate}`).join("|")]);
  useEffect(() => {
    if (!hasWaterfallSweepHandoff)
      return;
    const seedKey = selectedWaterfallSweepSelections
      .map(row => `${row.frequencyHz}:${row.sourceIndex ?? ""}:${row.sourceSerial ?? ""}:${row.gain ?? ""}:${row.sampleRateHz ?? ""}:${row.errorHz ?? ""}:${row.snrDb ?? ""}`)
      .join("|");
    if (!seedKey || validationHandoffSeedKey.current === seedKey)
      return;
    validationHandoffSeedKey.current = seedKey;
    if (waterfallSeedGainSequence)
      setPowerGainSequence(waterfallSeedGainSequence);
    if (waterfallSeedSampleRateMhz)
      setValidationSampleRateMhz(waterfallSeedSampleRateMhz);
    setValidationErrorOffsets("0");
  }, [hasWaterfallSweepHandoff, selectedWaterfallSweepSelections.map(row => `${row.frequencyHz}:${row.sourceIndex ?? ""}:${row.sourceSerial ?? ""}:${row.gain ?? ""}:${row.sampleRateHz ?? ""}:${row.errorHz ?? ""}:${row.snrDb ?? ""}`).join("|"), waterfallSeedGainSequence, waterfallSeedSampleRateMhz]);
  useEffect(() => {
    const saved = Number(localStorage.getItem(sweepStorageKey));
    const evidenceJobId = sweep?.evidenceJson ? Number(parseExperimentJson<any>(sweep.evidenceJson)?.job?.id) : 0;
    const jobId = Number.isFinite(saved) && saved > 0 ? saved : evidenceJobId;
    if (Number.isFinite(jobId) && jobId > 0)
      void refreshSweepJob(jobId, true);
  }, [sweepStorageKey, sweep?.id, sweep?.evidenceJson]);
  useEffect(() => {
    setSweepInsights(loadJsonStorage<Record<string, SweepInsight>>(sweepInsightStorageKey, {}));
    setSweepHistory(loadJsonStorage<SweepHistoryEntry[]>(sweepHistoryStorageKey, []));
  }, [sweepInsightStorageKey, sweepHistoryStorageKey]);
  const seconds = Number(duration) || 45;
  const sweepPassCount = effectiveSweepSources.reduce((total, source) => total + sweepPassCountFor(sweepInputs[String(source.index)] ?? defaultSweepInput(source)), 0);
  const sweepEstimateSeconds = effectiveSweepSources.reduce((total, source) => total + sweepSecondsFor(sweepInputs[String(source.index)] ?? defaultSweepInput(source)), 0);
  const sweepJobRunning = sweepJob != null && ["queued", "running", "paused"].includes(sweepJob.status);
  const persistedSweepStatus = sweep?.status === "running" ? undefined : sweep?.status;
  const sweepSourceStates = useMemo(() => buildSweepSourceStates(effectiveSweepSources.map(source => source.index), sweepJob, sweepLogs), [effectiveSweepSources.map(source => source.index).join(","), sweepJob?.status, sweepLogs]);
  const sweepResults = useMemo(() => parseSweepResults(sweepLogs), [sweepLogs]);
  useEffect(() => {
    localStorage.setItem(sweepInsightStorageKey, JSON.stringify(sweepInsights));
  }, [sweepInsightStorageKey, sweepInsights]);
  useEffect(() => {
    if (sweepJob?.status !== "completed" || Object.keys(sweepResults).length === 0) return;
    setSweepHistory(current => {
      let next = [...current];
      for (const result of Object.values(sweepResults)) {
        const entry = sweepHistoryEntry(result);
        if (!entry) continue;
        if (next.some(row => row.jobId === entry.jobId && row.sourceIndex === entry.sourceIndex)) continue;
        next = [entry, ...next].slice(0, 24);
      }
      localStorage.setItem(sweepHistoryStorageKey, JSON.stringify(next));
      return next;
    });
  }, [sweepJob?.status, sweepHistoryStorageKey, JSON.stringify(Object.values(sweepResults).map(result => `${result.jobId}:${result.sourceIndex}:${result.candidates[0]?.errorHz ?? ""}`))]);
  useEffect(() => {
    if (!sweepJob?.id || !sweepJobRunning) return;
    const timer = window.setInterval(() => void refreshSweepJob(sweepJob.id), 2000);
    return () => window.clearInterval(timer);
  }, [sweepJob?.id, sweepJobRunning]);
  useEffect(() => {
    if (!powerScan) return;
    const interpretation = parseExperimentJson<any>(powerScan.interpretationJson);
    const best = interpretation?.best;
    const selectedCc = Number(interpretation?.selectedControlChannelHz ?? best?.controlChannelHz);
    if (Number.isFinite(selectedCc) && selectedCc > 0 && selectedCc !== activeControlChannelHz)
      setActiveControlChannelHz(selectedCc);
    const selectedGain = interpretation?.selectedGain ?? best?.gain;
    const selectedSource = Number(best?.index);
    if (selectedGain != null && Number.isFinite(selectedSource))
      updateSweepInput(selectedSource, { gain: String(selectedGain) });
  }, [powerScan?.id]);
  useEffect(() => {
    if (!validationSweep) return;
    const interpretation = parseExperimentJson<any>(validationSweep.interpretationJson);
    const evidence = parseExperimentJson<any>(validationSweep.evidenceJson);
    const best = interpretation?.best ?? evidence?.candidates?.[0];
    const selectedCc = Number(interpretation?.selectedControlChannelHz ?? best?.controlChannelHz);
    if (Number.isFinite(selectedCc) && selectedCc > 0 && selectedCc !== activeControlChannelHz)
      setActiveControlChannelHz(selectedCc);
  }, [validationSweep?.id]);
  useEffect(() => {
    if (activeOperation !== "power" && !validationRunning) return;
    let stopped = false;
    async function refresh() {
      try {
        const progress = await api.request<RfSurveySweepProgress>(`${apiBase}/${encodeURIComponent(surveyId)}/sweep-progress`);
        if (!stopped)
          setValidationProgress(progress);
      } catch {
        if (!stopped)
          setValidationProgress(current => current);
      }
    }
    void refresh();
    if (!validationRunning && validationProgress?.active !== true)
      return () => { stopped = true; };
    const timer = window.setInterval(() => void refresh(), 1500);
    return () => {
      stopped = true;
      window.clearInterval(timer);
    };
  }, [surveyId, activeOperation, validationRunning, validationProgress?.active]);
  const updateSweepInput = (sourceIndex: number, patch: Partial<SweepSourceInput>) =>
    setSweepInputs(current => ({ ...current, [String(sourceIndex)]: { ...(current[String(sourceIndex)] ?? defaultSweepInput(sources.find(source => source.index === sourceIndex))), ...patch } }));
  const updateSweepField = (sourceIndex: number, patch: Partial<SweepSourceInput>) =>
    updateSweepInput(sourceIndex, { precision: "custom", ...patch });
  const updateValidationSampleRate = (value: string) => {
    setValidationSampleRateMhz(value);
  };
  const powerScanRequest = () => ({
      ...(validationHandoffSourceIndex !== undefined ? { sourceIndex: validationHandoffSourceIndex } : {}),
      parameters: {
        scanAllControlChannels: selectedWaterfallSweepControlChannels.length === 0,
        ...(selectedWaterfallSweepControlChannels.length ? { controlChannelsHz: selectedWaterfallSweepControlChannels } : {}),
        ...(selectedWaterfallSourceMeasurements.length ? { sourceMeasurements: selectedWaterfallSourceMeasurements } : {}),
        gainSequence: validationPowerGains,
        sampleRateHz: validationRequestSampleRateHz || undefined
      }
  });
  const runPowerScan = () => onRunExperiment("rf_power_scan", `about ${powerSweepPasses * 5} seconds`, undefined, powerScanRequest());
  const validationSweepRequest = (targetSystem?: RfSurveySystem) => {
    const selectedChannels = selectedWaterfallSweepControlChannels.length ? selectedWaterfallSweepControlChannels : [];
    const targetControlChannels = targetSystem?.controlChannelsHz?.length ? targetSystem.controlChannelsHz : selectedChannels;
    const targetControlChannelCount = Math.max(1, targetControlChannels.length || validationRunControlChannels.length);
    const targetPowerSourceCount = targetSystem ? effectivePowerSources.length : validationPowerSources.length;
    const targetGainCount = targetSystem ? effectivePowerGains.length : validationPowerGains.length;
    const targetRfPasses = !targetSystem && selectedWaterfallSourceMeasurements.length
      ? selectedWaterfallSourceMeasurements.length
      : Math.max(1, targetPowerSourceCount) * targetControlChannelCount * Math.max(1, targetGainCount);
    const targetP25SeedCount = Math.max(1, Math.min(validationRfCandidateLimit, targetRfPasses, targetControlChannelCount + (targetSystem ? 1 : systems.length || 1)));
    const targetVoiceCandidateCount = targetSystem ? Math.max(1, Math.min(2, targetControlChannelCount)) : validationVoiceCandidateCount;
    const targetMetricCandidateCount = targetSystem ? Math.max(1, Math.min(validationMetricCount, targetP25SeedCount)) : validationMetricRunCount;
    return {
      ...(!targetSystem && validationHandoffSourceIndex !== undefined ? { sourceIndex: validationHandoffSourceIndex } : {}),
      parameters: {
        scanAllControlChannels: !targetSystem && selectedChannels.length === 0,
        ...(targetSystem ? {
          targetSystemShortName: targetSystem.shortName,
          controlChannelsHz: targetSystem.controlChannelsHz
        } : selectedChannels.length ? { controlChannelsHz: selectedChannels } : {}),
        ...(!targetSystem && selectedWaterfallSourceMeasurements.length ? { sourceMeasurements: selectedWaterfallSourceMeasurements } : {}),
        gainSequence: !targetSystem ? validationPowerGains : effectivePowerGains,
        sampleRateHz: !targetSystem ? validationRequestSampleRateHz || undefined : validationSampleRateOk ? parsedValidationSampleRateHz : undefined,
        errorOffsetsHz: validationOffsets,
        errorDiscovery: validationAutoError,
        rfDurationSeconds: 2,
        p25DurationSeconds: 10,
        metricsDurationSeconds: 15,
        rfCandidateLimit: validationRfCandidateLimit,
        p25CandidateLimit: targetP25SeedCount,
        metricsCandidateLimit: targetMetricCandidateCount,
        voiceCandidateLimit: targetVoiceCandidateCount,
        voiceDurationSeconds: validationVoiceSeconds
      }
    };
  };
  const runValidationSweep = () => {
    setValidationTargetSystem(null);
    setValidationProgress({ active: true, directory: "", rows: [], candidates: [] });
    setValidationCancelMessage("");
    setSweepMessage("");
    if (!validationRequestSampleRateOk) {
      setSweepMessage(validationSampleRateMessage);
      setValidationProgress(null);
      return;
    }
    if (validationPowerGainInvalid) {
      setSweepMessage(`Airspy RF Sweep gain must be whole-number linearity gain 0-${AIRSPY_LINEARITY_GAIN_MAX}.`);
      setValidationProgress(null);
      return;
    }
    return onRunExperiment("rf_validation_sweep", `about ${formatElapsed(validationEstimateSeconds)}`, undefined, validationSweepRequest());
  };
  const runValidationSweepForSite = (system: RfSurveySystem) => {
    setValidationTargetSystem(system);
    setValidationProgress({ active: true, directory: "", rows: [], candidates: [] });
    setValidationCancelMessage("");
    setSweepMessage("");
    if (!validationSampleRateOk) {
      setSweepMessage(validationSampleRateMessage);
      setValidationProgress(null);
      return;
    }
    const controlCount = Math.max(1, system.controlChannelsHz.length);
    const siteEstimateSeconds = Math.max(1, effectivePowerSources.length) * controlCount * Math.max(1, effectivePowerGains.length) * 2 +
      Math.max(1, controlCount + 1) * Math.max(1, validationOffsets.length) * 10 +
      validationMetricCount * 15 +
      Math.max(1, Math.min(2, controlCount)) * validationVoiceSeconds +
      30;
    return onRunExperiment("rf_validation_sweep", `about ${formatElapsed(siteEstimateSeconds)}`, undefined, validationSweepRequest(system));
  };
  async function loadValidationProgress() {
    const progress = await api.request<RfSurveySweepProgress>(`${apiBase}/${encodeURIComponent(surveyId)}/sweep-progress`);
    setValidationProgress(progress);
  }
  async function cancelValidationSweep() {
    setSweepBusy("cancel-validation");
    setValidationCancelMessage("");
    try {
      const result = await api.request<RfSurveyCancelExperimentResult>(`${apiBase}/${encodeURIComponent(surveyId)}/experiments/cancel`, { method: "POST" });
      setValidationCancelMessage(result.message);
      await loadValidationProgress();
      await onReload();
    } catch (error) {
      setValidationCancelMessage(error instanceof Error ? error.message : "RF Sweep cancel failed.");
    } finally {
      setSweepBusy("");
    }
  }
  const applySweepPrecision = (sourceIndex: number, precision: SweepPrecision) => {
    if (precision === "custom") {
      updateSweepInput(sourceIndex, { precision });
      return;
    }
    updateSweepInput(sourceIndex, { precision, ...sweepPrecisionPresets[precision] });
  };
  async function refreshSweepJob(jobId: number, resetLogs = false) {
    const afterId = resetLogs ? 0 : sweepLogLastId.current;
    const [jobs, logs] = await Promise.all([
      api.request<Job[]>("/api/v1/jobs"),
      api.request<JobLog[]>(`/api/v1/jobs/${jobId}/logs?afterId=${afterId}`)
    ]);
    const job = jobs.find(row => row.id === jobId);
    if (job) {
      setSweepJob(job);
      onSweepRecovered?.(job.status);
    }
    if (resetLogs) {
      setSweepLogs(logs);
      sweepLogLastId.current = logs.length ? logs[logs.length - 1].id : 0;
    } else if (logs.length) {
      setSweepLogs(current => [...current, ...logs]);
      sweepLogLastId.current = logs[logs.length - 1].id;
    }
  }
  async function recoverHistoricalSweepJob() {
    try {
      const jobs = await api.request<Job[]>("/api/v1/jobs");
      const candidates = jobs
        .filter(job => job.type === "setup_tr_calibration_sweep" && job.status === "completed")
        .slice(0, 20);
      for (const job of candidates) {
        const logs = await api.request<JobLog[]>(`/api/v1/jobs/${job.id}/logs?afterId=0`);
        const text = logs.map(row => row.text).join("\n");
        if (!text.includes(`Running calibration sweep batch for ${systemShortName}`))
          continue;
        const hasSelectedSources = effectiveSweepSources.every(source =>
          text.includes(`"sourceIndex": ${source.index}`) ||
          text.includes(`source ${source.index}`) ||
          text.includes(`Source ${source.index}`));
        if (!hasSelectedSources)
          continue;
        setSweepJob(job);
        onSweepRecovered?.(job.status);
        setSweepLogs(logs);
        sweepLogLastId.current = logs.length ? logs[logs.length - 1].id : 0;
        localStorage.setItem(sweepStorageKey, String(job.id));
        setSweepMessage(`Recovered completed sweep job ${job.id} from job history.`);
        break;
      }
    } catch {
      // Recovery is best-effort; a normal rerun can create a fresh associated job.
    }
  }
  useEffect(() => {
    if (sweepJob?.id || sweep?.id || !systemShortName || effectiveSweepSources.length === 0)
      return;
    const key = `${surveyId}:${systemShortName}:${effectiveSweepSources.map(source => source.index).join(",")}`;
    if (sweepRecoveryKey.current === key)
      return;
    sweepRecoveryKey.current = key;
    void recoverHistoricalSweepJob();
  }, [surveyId, systemShortName, effectiveSweepSources.map(source => source.index).join(","), sweep?.id, sweepJob?.id]);
  async function beginSweep() {
    if (!effectiveSweepSources.length || !systemShortName || !selectedCcNumber) {
      setSweepMessage("Sweep requires a site, control channel, and selected SDR source.");
      return;
    }
    const invalidAirspySource = effectiveSweepSources.find(source => {
      const input = sweepInputs[String(source.index)] ?? defaultSweepInput(source);
      return isAirspyRfSource(source) && !validateAirspyLinearityGain(input.gain.trim() || source.gain || "15");
    });
    if (invalidAirspySource) {
      setSweepMessage(`Source ${invalidAirspySource.index} is Airspy; sweep gain must be whole-number linearity gain 0-${AIRSPY_LINEARITY_GAIN_MAX}.`);
      return;
    }
    setSweepBusy("start");
    setSweepMessage("");
    try {
      const parameters = buildGuidedSweepBatchParameters(
        systemShortName,
        "qpsk",
        effectiveSweepSources.map(source => ({
          index: source.index,
          serial: source.serial,
          sdrType: source.sdrType,
          device: source.device,
          centerFrequency: source.centerHz,
          sampleRate: source.sampleRate,
          errorHz: source.errorHz,
          gain: source.gain,
          input: sweepInputs[String(source.index)] ?? defaultSweepInput(source)
        })),
        selectedCcNumber,
      );
      const experiment = await api.request<RfSurveyExperiment>(`${apiBase}/${encodeURIComponent(surveyId)}/experiments/run`, {
        method: "POST",
        body: JSON.stringify({ type: "error_gain_sweep", durationSeconds: sweepEstimateSeconds, controlChannelHz: selectedCcNumber, parameters })
      });
      const evidence = parseExperimentJson<any>(experiment.evidenceJson);
      const job = evidence?.job as Job | undefined;
      if (!job?.id)
        throw new Error("Frequency-correction and gain sweep did not return a job handle.");
      setSweepJob(job);
      setSweepLogs([]);
      sweepLogLastId.current = 0;
      localStorage.setItem(sweepStorageKey, String(job.id));
      setSweepMessage(`Started sweep batch job ${job.id} for ${effectiveSweepSources.length} SDR source${effectiveSweepSources.length === 1 ? "" : "s"}.`);
      await refreshSweepJob(job.id, true);
    } catch (error) {
      setSweepMessage(error instanceof Error ? error.message : "Sweep failed to start.");
    } finally {
      setSweepBusy("");
    }
  }
  async function cancelSweep() {
    setSweepBusy("cancel");
    setSweepMessage("");
    try {
      const experiment = await api.request<RfSurveyExperiment>(`${apiBase}/${encodeURIComponent(surveyId)}/experiments/run`, {
        method: "POST",
        body: JSON.stringify({ type: "error_gain_sweep_cancel" })
      });
      const job = parseExperimentJson<any>(experiment.evidenceJson)?.job as Job | undefined;
      setSweepMessage(job?.id ? `Requested sweep stop with job ${job.id}.` : "Requested sweep stop.");
    } catch (error) {
      setSweepMessage(error instanceof Error ? error.message : "Sweep stop failed.");
    } finally {
      setSweepBusy("");
    }
  }
  async function applySweepCandidate(source: RfSurveySource, result: SweepResult, candidate: SweepCandidate) {
    setSweepBusy(`apply-${source.index}`);
    setSweepMessage("");
    try {
      if (!onAcceptSweepCandidate)
        throw new Error("This RF refinement view is not connected to Setup.");
      await onAcceptSweepCandidate(source, result, candidate);
      setSweepMessage(`Accepted Source ${source.index} gain ${result.gain || source.gain || "unchanged"} and frequency correction ${formatSignedHz(candidate.errorHz)} as pending Setup values. Live monitoring is unchanged until Apply & Resume.`);
    } catch (error) {
      setSweepMessage(error instanceof Error ? error.message : "Unable to accept the sweep candidate into Setup.");
    } finally {
      setSweepBusy("");
    }
  }
  async function analyzeSweepCandidate(source: RfSurveySource, result: SweepResult, candidate: SweepCandidate) {
    setSweepBusy(`ai-${source.index}`);
    setSweepMessage("");
    try {
      const insight = await api.request<SweepInsight>(`${apiBase}/${encodeURIComponent(surveyId)}/sweep-insights`, {
        method: "POST",
        body: JSON.stringify({
          surveyId,
          systemShortName,
          sourceIndex: source.index,
          sweepResult: result,
          selectedCandidate: candidate
        })
      });
      setSweepInsights(current => ({ ...current, [String(source.index)]: insight }));
    } catch (error) {
      setSweepMessage(error instanceof Error ? error.message : "Unable to analyze sweep results with AI Insights.");
    } finally {
      setSweepBusy("");
    }
  }
  const substeps = [
    {
      id: "cc",
      title: "TR CC Metrics",
      status: ccQuality?.status,
      estimate: `${seconds} seconds`,
      locked: false,
      action: "Run",
      busyKey: "control_channel_quality",
      begin: () => onRunExperiment("control_channel_quality", `${seconds} seconds`, selectedCcNumber),
      result: ccQuality,
      body: <div className="rf-step-stack">
        <p>Known control channels: {controlChannels.length ? controlChannels.map(formatRfHz).join(", ") : "none"}; primary candidate: {controlChannels[0] ? formatRfHz(controlChannels[0]) : "--"}.</p>
        <div className="rf-cc-runline">
          <label><span>Control channel</span><select value={selectedCcHz} onChange={event => updateSelectedCc(event.target.value)} disabled={controlChannels.length <= 1}>
            {controlChannels.map(freq => <option value={String(freq)} key={freq}>{formatRfHz(freq)}</option>)}
          </select></label>
          <label><span>Measure seconds</span><input inputMode="numeric" value={duration} onChange={event => setDuration(event.target.value)} /></label>
          <button className="danger-button" disabled={Boolean(busy)} onClick={() => void onRunExperiment("control_channel_quality", `${seconds} seconds`, selectedCcNumber)}>{busy === "control_channel_quality" ? "Running..." : "Run"}</button>
        </div>
        {!ccQuality && staleCcQuality && <StaleExperimentNotice experiment={staleCcQuality} activeControlChannelHz={selectedCcNumber} />}
        <CcMetricRunHistory runs={ccQualityRuns} selectedCcHz={selectedCcNumber} onDetails={run => onShowDetails({ title: "TR CC Metrics Details", body: <CcQualityDetails experiment={run} /> })} />
      </div>
    },
    {
      id: "inventory",
      title: "SDR Inventory",
      status: inventory?.status,
      estimate: "about 15 seconds",
      locked: false,
      action: "Run",
      busyKey: "inventory",
      begin: onStopAndInventory,
      result: inventory,
      body: null
    },
    {
      id: "waterfall",
      title: "Waterfall",
      status: undefined,
      estimate: "live",
      locked: !inventorySatisfied,
      action: "Start",
      busyKey: "waterfall",
      begin: undefined,
      result: undefined,
      body: <WaterfallStep
        apiBase={apiBase}
        surveyId={surveyId}
        locked={!inventorySatisfied}
        sources={sources}
        selectedSources={selectedSources}
        systems={systems}
        controlChannels={controlChannels}
        activeControlChannelHz={activeControlChannelHz}
        waterfallSweepSelections={selectedWaterfallSweepSelections}
        onWaterfallSweepSelections={onWaterfallSweepSelections}
        onRunExperiment={onRunExperiment}
        onReload={onReload}
      />
    },
    {
      id: "power",
      title: "RF Sweep",
      status: validationSweepStatus,
      estimate: `about ${formatElapsed(validationEstimateSeconds)}`,
      locked: !inventorySatisfied,
      action: "Run",
      busyKey: "rf_validation_sweep",
      begin: runValidationSweep,
      result: validationSweep,
      body: <div className="rf-step-stack">
        <div className="rf-sweep-compact">
          <div className="rf-recommended-run">
            <div>
              <span>Recommended run</span>
              <strong>{activeValidationTarget ? activeValidationTarget.siteLabel || activeValidationTarget.shortName : `${systems.length} selected site${systems.length === 1 ? "" : "s"}`}</strong>
              <small>{activeValidationPowerSources.length} source{activeValidationPowerSources.length === 1 ? "" : "s"} / {activeValidationControlChannels.length} control channel{activeValidationControlChannels.length === 1 ? "" : "s"} / about {formatElapsed(activeValidationEstimateSeconds)}</small>
            </div>
            <div className="rf-primary-actions">
              <button className="danger-button" disabled={Boolean(busy) || validationBlocked || !validationRequestSampleRateOk || validationPowerGainInvalid} onClick={() => void runValidationSweep()}>{busy === "rf_validation_sweep" ? "Running..." : validationSweep ? "Rerun proof" : "Run proof"}</button>
              {(validationRunning || validationProgress?.active) && <button disabled={sweepBusy === "cancel-validation"} onClick={() => void cancelValidationSweep()}>{sweepBusy === "cancel-validation" ? "Canceling..." : "Cancel"}</button>}
            </div>
          </div>
          <details className="rf-advanced-settings">
            <summary>Advanced settings</summary>
            <div className="rf-cc-runline compact">
              <label><span>Sample rate MHz</span><input className={validationRequestSampleRateOk ? "rf-short-input" : "rf-short-input invalid"} size={8} inputMode="decimal" value={validationFormSampleRateMhz} onChange={event => updateValidationSampleRate(event.target.value)} /></label>
              <label><span>Gain sequence</span><input className={validationPowerGainInvalid ? "invalid" : ""} value={validationFormGainSequence} onChange={event => setPowerGainSequence(event.target.value)} /></label>
              <label><span>Correction change (Hz)</span><input className="rf-short-input" size={6} value={validationFormErrorSearch} onChange={event => setValidationErrorOffsets(event.target.value)} /></label>
              <label><span>Metric candidates</span><input className="rf-short-input" size={6} inputMode="numeric" value={validationMetricsCandidates} onChange={event => setValidationMetricsCandidates(event.target.value)} /></label>
            </div>
            {validationSampleRateMessage && !waterfallSampleRateHz && <div className="settings-message error">{validationSampleRateMessage}</div>}
            {airspyPowerGainMessage && <div className={validationPowerGainInvalid ? "settings-message error" : "setup-note"}>{validationPowerGainInvalid ? `${airspyPowerGainMessage} Remove values above ${AIRSPY_LINEARITY_GAIN_MAX}.` : airspyPowerGainMessage}</div>}
            <div className="rf-waterfall-sweep-selection">
              <strong>RF Sweep CCs</strong>
              <span>{selectedWaterfallSourceMeasurements.length
                ? selectedWaterfallSourceMeasurements.map(row => `source ${row.sourceIndex}${row.sourceSerial ? ` (${row.sourceSerial})` : ""}: ${formatRfHz(row.controlChannelHz)}, gain ${row.gain || "saved"}, measured signal offset ${formatSignedHz(row.measuredSignalOffsetHz ?? 0)}${row.sampleRateHz ? `, ${formatMhzInput(row.sampleRateHz)} MS/s` : ""}`).join("; ")
                : "All requested control channels"}</span>
              {selectedWaterfallSweepControlChannels.length > 0 && <button type="button" onClick={() => onWaterfallSweepSelections?.([])}>Clear</button>}
            </div>
            {selectedWaterfallSourceMeasurements.length > 0 && <div className="setup-note">Each Waterfall result runs only on the SDR that measured it. RF Sweep reconciles the measured offsets into one crystal correction per SDR and falls back to its saved correction when the observations are weak.</div>}
          </details>
          <div className="rf-sweep-callout" role="status" aria-live="polite">
            <span>Estimated time</span>
            <strong>{formatElapsed(activeValidationEstimateSeconds)}</strong>
            {validationRunning ? <StepProgressIndicator label="RF Sweep is running" /> : <span className="rf-step-progress-track idle" aria-hidden="true"><span /></span>}
          </div>
        </div>
        <div className="rf-config-subsection rf-sweep-primary">
          {activeValidationTarget && <div className="setup-note">Rerunning RF Sweep for {activeValidationTarget.siteLabel || activeValidationTarget.shortName} only. Previous site readiness remains shown until the targeted run finishes.</div>}
          {hideStaleSiteReadiness
            ? <div className="setup-note">Site readiness is reset for this RF Sweep. Results will repopulate as this run reaches candidate ranking.</div>
            : validationSweep && <RfValidationRecommendations experiment={validationSweep} systems={systems} busy={Boolean(busy)} onRefreshSite={system => { void runValidationSweepForSite(system); }} />}
          {validationBlocked && validationBlocker && <div className="settings-message error">{validationBlocker}</div>}
          {validationCancelMessage && <div className="setup-note">{validationCancelMessage}</div>}
          {sweepMessage && <div className="setup-note">{sweepMessage}</div>}
        </div>
        <details className="rf-technical-details rf-sweep-plan-details" open={validationRunning || validationProgress?.active === true}>
          <summary>Permutation plan and technical results</summary>
          <div className="rf-sweep-plan" aria-label="RF sweep permutation plan">
            <div className="rf-sweep-plan-head">
              <strong>{activeValidationTarget ? "Targeted plan and results" : "Plan and results"}</strong>
              <span>{activeValidationTarget ? `Only ${activeValidationTarget.siteLabel || activeValidationTarget.shortName} is included in this rerun.` : "The strongest candidates receive P25, monitoring, and voice follow-up checks."}</span>
            </div>
            <div className="rf-sweep-plan-grid">
              <div><span>SDR sources</span><code>{activeValidationPowerSources.map(source => `${source.sdrType || "SDR"} ${source.index}${source.serial ? ` (${source.serial})` : ""}`).join(", ") || "None"}</code></div>
              <div><span>Sample rate</span><code>{validationRequestSampleRateOk ? `${formatMhzInput(validationRequestSampleRateHz)} MHz` : "Invalid"}</code></div>
              <div><span>RF screens</span><code>{!activeValidationTarget && selectedWaterfallSourceMeasurements.length ? `${selectedWaterfallSourceMeasurements.length} source-bound Waterfall choice(s)` : `${activeValidationPowerSources.length} source(s) x ${activeValidationControlChannels.length} CC x ${activeValidationPowerGains.length} gain = ${activeValidationPowerPasses}`}</code></div>
              <div><span>P25 probes</span><code>{activeValidationP25SeedCount} seed(s) x {validationOffsets.length} correction change(s) = {activeValidationProbePasses}</code></div>
              <div><span>Follow-up limits</span><code>{activeValidationMetricRunCount} monitoring candidate(s); {activeValidationVoiceCandidateCount} voice candidate(s)</code></div>
            </div>
            <RfSweepPermutationResults
              sources={activeValidationPowerSources}
              controlChannels={activeValidationControlChannels}
              gains={activeValidationPowerGains}
              rows={validationProgressRows}
              candidates={validationProgressCandidates}
              active={validationRunning || validationProgress?.active === true}
            />
          </div>
        </details>
      </div>
    },
    {
      id: "p25",
      title: "P25 Probe",
      status: p25?.status,
      estimate: `${seconds} seconds per control channel`,
      locked: !inventory,
      action: "Run",
      busyKey: "p25",
      begin: () => onRunP25(selectedCcNumber),
      result: p25,
      body: <div className="rf-step-stack">
        <p>Runs OP25/P25 against the primary control channel using the selected SDR source. Passing means real P25 sync/control-channel messages were captured by tooling.</p>
        <div className="rf-form-grid"><SettingInput label="Probe seconds" description="Shown before running each control-channel probe." value={duration} onChange={setDuration} inputMode="numeric" /></div>
        <div className="rf-cc-runline">
          <label><span>Control channel</span><select value={selectedCcHz} onChange={event => updateSelectedCc(event.target.value)} disabled={controlChannels.length <= 1}>
            {controlChannels.map(freq => <option value={String(freq)} key={freq}>{formatRfHz(freq)}</option>)}
          </select></label>
        </div>
        {!p25 && staleP25 && <StaleExperimentNotice experiment={staleP25} activeControlChannelHz={selectedCcNumber} />}
      </div>
    },
    {
      id: "sweep",
      title: "Frequency Correction & Gain Refinement",
      status: sweepJobRunning ? "running" : sweepJob?.status || persistedSweepStatus,
      estimate: `about ${formatElapsed(sweepEstimateSeconds)} for ${sweepPassCount} passes`,
      locked: !inventory,
      action: measurementDone(sweepJob?.status || persistedSweepStatus) ? "Rerun" : "Run",
      busyKey: "sweep",
      begin: beginSweep,
      result: undefined,
      body: <div className="rf-step-stack">
        <p>Searches nearby source frequency-correction settings for steadier control-channel decoding after RF Sweep has found a usable control-channel and gain condition.</p>
        <div className="setup-note">Accepting a candidate updates the pending Setup source values. Apply & Resume remains the only action that changes live monitoring.</div>
        {!sweep && staleSweep && <StaleExperimentNotice experiment={staleSweep} activeControlChannelHz={selectedCcNumber} />}
        <div className="rf-sweep-table">
          <div className="rf-sweep-header">
            <span>SDR</span>
            <span>Precision</span>
            <span>Saved correction (Hz)</span>
            <span>Gain</span>
            <span>Range Hz</span>
            <span>Step Hz</span>
            <span>Measure</span>
            <span>Passes</span>
            <span>Status</span>
          </div>
          {effectiveSweepSources.map(source => {
            const input = sweepInputs[String(source.index)] ?? defaultSweepInput(source);
            const sourceState = sweepSourceStates[String(source.index)] ?? "Pending";
            const sourceIsAirspy = isAirspyRfSource(source);
            const sourceGainInvalid = sourceIsAirspy && !validateAirspyLinearityGain(input.gain.trim() || source.gain || "15");
            return <div className={highlightedSweepSource === source.index ? "rf-sweep-row flash" : "rf-sweep-row"} key={source.index}>
              <div><strong>Source {source.index}</strong><small>{source.serial || source.device}</small></div>
              <label><span>{sweepReliabilityLabel(input)}</span><select value={input.precision} onChange={event => applySweepPrecision(source.index, event.target.value as SweepPrecision)}>
                <option value="quick">Quick</option>
                <option value="balanced">Balanced</option>
                <option value="deep">Deep</option>
                <option value="custom">Custom</option>
              </select></label>
              <label><span>Saved correction (Hz)</span><input inputMode="numeric" value={input.errorHz} onChange={event => updateSweepField(source.index, { errorHz: event.target.value })} /></label>
              <label><span>{sourceIsAirspy ? `Linearity gain 0-${AIRSPY_LINEARITY_GAIN_MAX}` : "Gain"}</span><input className={sourceGainInvalid ? "invalid" : ""} value={input.gain} onChange={event => updateSweepField(source.index, { gain: event.target.value })} /></label>
              <label><span>Range Hz</span><input inputMode="numeric" value={input.rangeHz} onChange={event => updateSweepField(source.index, { rangeHz: event.target.value })} /></label>
              <label><span>Step Hz</span><input inputMode="numeric" value={input.stepHz} onChange={event => updateSweepField(source.index, { stepHz: event.target.value })} /></label>
              <label><span>{input.warmupSec}s warmup</span><input inputMode="numeric" value={input.durationSec} onChange={event => updateSweepField(source.index, { durationSec: event.target.value })} /></label>
              <code>{sweepPassCountFor(input)}</code>
              <span className={sourceState === "Completed" ? "section-status ok" : sourceState === "Failed" ? "section-status error" : sourceState === "Running" ? "section-status info" : "section-status"}>{sourceState}</span>
            </div>;
          })}
        </div>
        <div className="setup-review">
          <div><span>Control channel</span><code>{selectedCcNumber ? formatRfHz(selectedCcNumber) : "--"}</code></div>
          <div><span>Sweep order</span><code>{effectiveSweepSources.map(source => source.index).join(" -> ") || "--"}</code></div>
          <div><span>Total passes</span><code>{sweepPassCount}</code></div>
        </div>
        {Object.keys(sweepResults).length > 0 && <div className="rf-sweep-results">
          {effectiveSweepSources.map(source => {
            const result = sweepResults[String(source.index)];
            if (!result) return null;
            const selectedError = selectedSweepCandidates[String(source.index)] ?? result.candidates[0]?.errorHz ?? result.best?.errorHz;
            const selectedCandidate = result.candidates.find(candidate => candidate.errorHz === selectedError) ?? result.candidates[0] ?? result.best;
            const insight = sweepInsights[String(source.index)];
            const hasComparableDecode = result.candidates.some(candidate => candidate.hasDecodeSamples);
            const previousBest = sweepHistory.find(row => row.sourceIndex === source.index && row.jobId !== result.jobId && row.bestHasDecodeSamples);
            const sweepComparison = compareSweepRun(result.candidates[0], previousBest);
            return <div className="rf-sweep-result-card" key={`result-${source.index}`}>
              <div className="setup-job-head">
                <strong>Source {source.index} Sweep Results</strong>
                <span className="muted">{result.candidates.length} candidate row{result.candidates.length === 1 ? "" : "s"}; top-ranked frequency correction {result.candidates[0] ? formatSignedHz(result.candidates[0].errorHz) : "unknown"}</span>
              </div>
              <div className="setup-note">Artifacts: {result.summaryPath || result.outputDir}</div>
              {result.candidates[0] && <div className="rf-sweep-recommendation">
                <strong>Recommended frequency correction: {formatSignedHz(result.candidates[0].errorHz)}</strong>
                <span>{result.candidates[0].hasDecodeSamples ? `${result.candidates[0].avgDecodeRate.toFixed(1)}/sec average across ${result.candidates[0].totalDecode ?? 0} CC message-rate sample(s)` : "No comparable CC message-rate samples captured"}</span>
              </div>}
              {sweepComparison && <div className={`rf-sweep-comparison ${sweepComparison.tone}`}>{sweepComparison.text}</div>}
              {!hasComparableDecode && <div className="setup-warning-list">
                <div>This sweep result has no parser-visible control-channel message-rate samples. Calls may still start/end during the window, but frequency-correction ranking is advisory until a rerun captures comparable samples with the current Trunk Recorder diagnostic settings.</div>
              </div>}
              <div className="rf-sweep-candidate-table">
                <div className="rf-sweep-candidate-header"><span></span><span>Frequency correction</span><span>Control average</span><span>Control samples</span><span>% zero-decode</span><span>Retunes</span><span>Calls started/ended</span><span>No transmission</span></div>
                {result.candidates.slice(0, 8).map(candidate => <label className={candidate.errorHz === selectedError ? "selected" : ""} key={`${source.index}-${candidate.errorHz}`}>
                  <input type="radio" name={`sweep-candidate-${source.index}`} checked={candidate.errorHz === selectedError} onChange={() => setSelectedSweepCandidates(current => ({ ...current, [String(source.index)]: candidate.errorHz }))} />
                  <code>{formatSignedHz(candidate.errorHz)}</code>
                  <span>{formatSweepRate(candidate)}</span>
                  <span>{candidate.totalDecode ?? 0}</span>
                  <span>{formatSweepPercent(candidate)}</span>
                  <span>{candidate.retunes}</span>
                  <span>{candidate.callsStarted}/{candidate.callsConcluded}</span>
                  <span>{candidate.noTxRecorded}</span>
                </label>)}
              </div>
              <div className="rf-inline-actions">
                <button disabled={!selectedCandidate || sweepBusy === `apply-${source.index}`} onClick={() => selectedCandidate && void applySweepCandidate(source, result, selectedCandidate)}>{sweepBusy === `apply-${source.index}` ? "Accepting..." : "Use in Setup"}</button>
                {selectedCandidate && <button title="Uses the selected frequency correction as the new center and reduces the range and step for a follow-up sweep. It does not run anything until you click Rerun." onClick={() => {
                  updateSweepInput(source.index, { precision: "custom", errorHz: String(selectedCandidate.errorHz), rangeHz: "300", stepHz: "100" });
                  setHighlightedSweepSource(source.index);
                  window.setTimeout(() => setHighlightedSweepSource(current => current === source.index ? null : current), 1800);
                  setSweepMessage(`Prepared Source ${source.index} for a narrower follow-up sweep centered on frequency correction ${formatSignedHz(selectedCandidate.errorHz)}. Click Rerun to execute it.`);
                }}>Narrow Sweep</button>}
                <button disabled={!selectedCandidate || sweepBusy === `ai-${source.index}`} onClick={() => selectedCandidate && void analyzeSweepCandidate(source, result, selectedCandidate)}>{sweepBusy === `ai-${source.index}` ? "Analyzing..." : "Analyze"}</button>
              </div>
              {insight && <div className="rf-sweep-insight">
                <div className="setup-job-head">
                  <strong>AI Recommendation</strong>
                  <span className="muted">{label(insight.confidence || "inconclusive")}</span>
                </div>
                <p>{insight.recommendation}</p>
                <small>{insight.rationale}</small>
                {insight.nextActions?.length > 0 && <ul>{insight.nextActions.map(action => <li key={action}>{action}</li>)}</ul>}
              </div>}
            </div>;
          })}
        </div>}
        {sweepJob?.status === "completed" && Object.keys(sweepResults).length === 0 && <div className="setup-note">Sweep completed, but no parsed candidate table was found. Open the job feed to inspect the run output, or rerun the sweep to populate results here.</div>}
        {sweepJob && <div className="setup-note">Sweep job {sweepJob.id}: {label(sweepJob.status)} - {sweepJob.message}</div>}
        {sweepMessage && <div className="setup-note">{sweepMessage}</div>}
        {sweepJob && <div className="rf-inline-actions">
          <button onClick={() => setSweepDrawerOpen(true)}>Job Feed</button>
          {sweepJobRunning && <button className="danger-button" disabled={sweepBusy === "cancel"} onClick={() => void cancelSweep()}>{sweepBusy === "cancel" ? "Stopping..." : "Stop sweep"}</button>}
        </div>}
        {sweepJob && <SetupJobDrawer job={sweepJob} logs={sweepLogs} running={sweepJobRunning} onStopCalibration={cancelSweep} stopping={sweepBusy === "cancel"} open={sweepDrawerOpen} setOpen={setSweepDrawerOpen} />}
      </div>
    }
  ];
  const requiredSteps = substeps.filter(item => item.id === "power" || (inventoryRequired !== false && item.id === "inventory"));
  const nextRequired = requiredSteps.find(item => !measurementDone(item.status)) ?? requiredSteps.at(-1)!;
  const actionLabelFor = (item: typeof substeps[number]) =>
    busy === item.busyKey || (item.busyKey === "sweep" && sweepBusy === "start")
      ? "Running..."
      : item.result?.status === "passed" || measurementDone(item.status)
        ? "Rerun"
        : item.action;
  const current = substeps.find(item => item.id === activeOperation) ?? nextRequired;
  const waterfallSubstep = substeps.find(item => item.id === "waterfall");
  const canBeginCurrent = Boolean(current.begin) && !current.locked && !busy && !sweepBusy && !sweepJobRunning;
  const currentActionLabel = actionLabelFor(current);
  const currentRunning = busy === current.busyKey || (current.busyKey === "sweep" && (sweepBusy === "start" || sweepJobRunning));
  const detailsAction = current.result?.type === "control_channel_quality"
    ? () => onShowDetails({ title: "TR CC Metrics Details", body: <CcQualityDetails experiment={current.result!} /> })
    : current.result?.type === "control_channel_p25_probe"
      ? () => onShowDetails({ title: "P25 Probe Details", body: <pre className="log-box">{current.result?.evidenceJson}</pre> })
      : current.result?.type === "sdr_inventory"
        ? () => onShowDetails({ title: "SDR Inventory Details", body: <pre className="log-box">{current.result?.evidenceJson}</pre> })
        : undefined;
  const hideSubpageHeader = current.id === "inventory" || current.id === "power" || current.id === "waterfall";
  return <div className="rf-step-stack">
    {!hideSubpageHeader && <div className="rf-section-headline">
      <div>
        <h4>{current.title}</h4>
      </div>
    </div>}
    {current.id !== "cc" && !hideSubpageHeader && <div className="rf-subpage-meta">
      <span>Estimated time: {current.estimate}</span>
      <span>Recommended next: {nextRequired.title}</span>
    </div>}
    {currentRunning && current.id !== "power" && current.id !== "waterfall" && <StepProgressIndicator label={`${current.title} is running`} />}
    {current.id !== "waterfall" ? current.body : null}
    {waterfallSubstep?.body && <div style={current.id === "waterfall" ? undefined : { display: "none" }} aria-hidden={current.id === "waterfall" ? undefined : "true"}>{waterfallSubstep.body}</div>}
    {current.id !== "cc" && current.id !== "power" && current.result && <ExperimentSummary experiment={current.result} onDetails={detailsAction} />}
    {current.id !== "cc" && current.id !== "power" && current.id !== "waterfall" && <div className="rf-subpage-actions">
      <button className="danger-button" disabled={!canBeginCurrent} onClick={() => void current.begin?.()}>{currentActionLabel}</button>
      {current.locked && <span className="muted">Run SDR inventory first</span>}
    </div>}
  </div>;
}

function WaterfallStep({
  apiBase = siteSetupRfApi,
  surveyId,
  visible = true,
  locked,
  sources,
  selectedSources,
  systems,
  controlChannels,
  activeControlChannelHz,
  waterfallSweepSelections,
  onWaterfallSweepSelections,
  onRunExperiment,
  onReload,
  onStatusChange,
  showSweepSelection = true
}: {
  apiBase?: string;
  surveyId: string;
  visible?: boolean;
  locked: boolean;
  sources: RfSurveySource[];
  selectedSources: number[];
  systems: RfSurveySystem[];
  controlChannels: number[];
  activeControlChannelHz: number;
  waterfallSweepSelections?: WaterfallSweepSelection[];
  onWaterfallSweepSelections?: (values: WaterfallSweepSelection[]) => void;
  onRunExperiment: (type: string, estimate: string, controlChannelHz?: number, extraRequest?: Record<string, unknown>) => Promise<RfSurveyExperiment | undefined>;
  onReload: () => Promise<void>;
  onStatusChange?: (status: RfSurveyWaterfallStatus | null) => void;
  showSweepSelection?: boolean;
}) {
  const controlStorageKey = `pizzawave-setup-waterfall-controls-${surveyId}`;
  const savedControls = loadJsonStorage<Record<string, unknown>>(controlStorageKey, {});
  const effectiveSources = sources.filter(source => selectedSources.includes(source.index));
  const sourceOptions = effectiveSources.length ? effectiveSources : sources.slice(0, 1);
  const defaultSource = sourceOptions[0] ?? sources[0];
  const [sourceIndex, setSourceIndex] = useState(() => {
    const saved = Number(savedControls.sourceIndex);
    return Number.isFinite(saved) && sources.some(source => source.index === saved) ? saved : defaultSource?.index ?? 0;
  });
  const selectedSource = sources.find(source => source.index === sourceIndex) ?? defaultSource;
  const selectedSourceIsAirspy = selectedSource ? isAirspyRfSource(selectedSource) : false;
  const defaultFrequency = activeControlChannelHz || controlChannels[0] || selectedSource?.centerHz || 0;
  const [frequencyMhz, setFrequencyMhz] = useState(() => typeof savedControls.frequencyMhz === "string" && savedControls.frequencyMhz.trim() ? savedControls.frequencyMhz : defaultFrequency ? formatMhzInput(defaultFrequency) : "");
  const [sampleRateMhz, setSampleRateMhz] = useState(() => typeof savedControls.sampleRateMhz === "string" && savedControls.sampleRateMhz.trim() ? savedControls.sampleRateMhz : formatMhzInput(defaultWaterfallSampleRate(selectedSource)));
  const [gain, setGain] = useState(() => typeof savedControls.gain === "string" && savedControls.gain.trim() ? savedControls.gain : selectedSource?.gain || "15");
  const [spectrumSpanDb, setSpectrumSpanDb] = useState(() => {
    const saved = Number(savedControls.spectrumSpanDb);
    return [20, 35, 50, 70].includes(saved) ? saved : 35;
  });
  const [showControlChannelLines, setShowControlChannelLines] = useState(() => typeof savedControls.showControlChannelLines === "boolean" ? savedControls.showControlChannelLines : true);
  const [spectrumHover, setSpectrumHover] = useState<SpectrumHover | null>(null);
  const [ccSignalRows, setCcSignalRows] = useState<WaterfallCcSignalRow[]>([]);
  const [otherDetectedCcRows, setOtherDetectedCcRows] = useState<WaterfallDetectedCcTrack[]>([]);
  const [identifyOverlayMessage, setIdentifyOverlayMessage] = useState("");
  const [identifyResults, setIdentifyResults] = useState<Record<string, WaterfallIdentifyResult>>({});
  const [status, setStatus] = useState<RfSurveyWaterfallStatus | null>(null);
  const [message, setMessage] = useState("");
  const [busy, setBusy] = useState("");
  const [frequencyMenuOpen, setFrequencyMenuOpen] = useState(false);
  const spectrumCanvasRef = useRef<HTMLCanvasElement | null>(null);
  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  const frequencyComboRef = useRef<HTMLLabelElement | null>(null);
  const smoothedSpectrumRef = useRef<number[]>([]);
  const heldSpectrumRef = useRef<number[]>([]);
  const spectrumScaleRef = useRef<SpectrumDisplayScale | null>(null);
  const waterfallScaleRef = useRef<SpectrumDisplayScale | null>(null);
  const spectrumAxisRef = useRef<SpectrumAxis | null>(null);
  const peakHistoryRef = useRef<Map<string, SpectrumPeakTrack>>(new Map());
  const otherDetectedCcHistoryRef = useRef<Map<string, WaterfallDetectedCcTrack>>(new Map());
  const ccSignalHistoryRef = useRef<Map<string, WaterfallCcSignalTrack>>(new Map());
  const spectrumMarkersRef = useRef<SpectrumMarker[]>([]);
  const lastFrameRef = useRef("");
  const lastWaterfallStatusUiAtRef = useRef(0);
  const lastWaterfallTableUiAtRef = useRef(0);
  const hasGoodWaterfallFrameRef = useRef(false);
  const frequencyHz = Math.round(Number(frequencyMhz) * 1_000_000);
  const sampleRateHz = Math.round(Number(sampleRateMhz) * 1_000_000);
  const controlChannelOptions = uniqueSortedFrequencies([
    ...controlChannels,
    ...systems.flatMap(system => system.controlChannelsHz),
    activeControlChannelHz
  ]);
  const selectedSweepSelections = normalizeWaterfallSweepSelections(waterfallSweepSelections);
  const selectedSweepControlChannels = normalizeControlChannelSelection(selectedSweepSelections.map(row => row.frequencyHz));
  const selectedSweepControlChannelSet = new Set(selectedSweepSelections
    .filter(row => row.sourceIndex === sourceIndex || (row.sourceIndex == null && row.sourceSerial && row.sourceSerial === selectedSource?.serial))
    .map(row => row.frequencyHz));
  const frequencyOk = Number.isFinite(frequencyHz) && frequencyHz > 0;
  const sampleRateOk = Number.isFinite(sampleRateHz) && sampleRateHz > 0;
  const gainOk = !selectedSourceIsAirspy || validateAirspyLinearityGain(gain.trim() || selectedSource?.gain || "15");
  const identifyRunning = busy === "identify";
  const controlsDisabled = identifyRunning;
  const canStart = !locked && !busy && sourceOptions.length > 0 && frequencyOk && sampleRateOk && gainOk;

  useEffect(() => {
    const saved = loadJsonStorage<Record<string, unknown>>(controlStorageKey, {});
    const savedSourceIndex = Number(saved.sourceIndex);
    setSourceIndex(Number.isFinite(savedSourceIndex) && sources.some(source => source.index === savedSourceIndex) ? savedSourceIndex : defaultSource?.index ?? 0);
    setFrequencyMhz(typeof saved.frequencyMhz === "string" && saved.frequencyMhz.trim() ? saved.frequencyMhz : defaultFrequency ? formatMhzInput(defaultFrequency) : "");
    setSampleRateMhz(typeof saved.sampleRateMhz === "string" && saved.sampleRateMhz.trim() ? saved.sampleRateMhz : formatMhzInput(defaultWaterfallSampleRate(selectedSource)));
    setGain(typeof saved.gain === "string" && saved.gain.trim() ? saved.gain : selectedSource?.gain || "15");
    const savedSpan = Number(saved.spectrumSpanDb);
    setSpectrumSpanDb([20, 35, 50, 70].includes(savedSpan) ? savedSpan : 35);
    setShowControlChannelLines(typeof saved.showControlChannelLines === "boolean" ? saved.showControlChannelLines : true);
  }, [controlStorageKey]);

  useEffect(() => {
    localStorage.setItem(controlStorageKey, JSON.stringify({
      sourceIndex,
      frequencyMhz,
      sampleRateMhz,
      gain,
      spectrumSpanDb,
      showControlChannelLines
    }));
  }, [controlStorageKey, sourceIndex, frequencyMhz, sampleRateMhz, gain, spectrumSpanDb, showControlChannelLines]);

  useEffect(() => {
    onStatusChange?.(status);
    return () => onStatusChange?.(null);
  }, [status?.active, status?.trWasActive, status?.trRestartError, status?.status, onStatusChange]);

  useEffect(() => {
    if (!selectedSource) return;
    setSampleRateMhz(current => current || formatMhzInput(defaultWaterfallSampleRate(selectedSource)));
    setGain(current => current || selectedSource.gain || "15");
  }, [selectedSource?.index]);

  useEffect(() => {
    if (!activeControlChannelHz) return;
    setFrequencyMhz(current => current || formatMhzInput(activeControlChannelHz));
  }, [activeControlChannelHz]);

  useEffect(() => {
    if (!frequencyMenuOpen)
      return;
    const closeOnOutsideClick = (event: MouseEvent) => {
      if (!frequencyComboRef.current?.contains(event.target as Node))
        setFrequencyMenuOpen(false);
    };
    document.addEventListener("mousedown", closeOnOutsideClick);
    return () => document.removeEventListener("mousedown", closeOnOutsideClick);
  }, [frequencyMenuOpen]);

  useEffect(() => {
    spectrumScaleRef.current = null;
  }, [spectrumSpanDb]);

  function resetWaterfallDrawingState(clearCanvases: boolean) {
    lastFrameRef.current = "";
    hasGoodWaterfallFrameRef.current = false;
    smoothedSpectrumRef.current = [];
    heldSpectrumRef.current = [];
    peakHistoryRef.current.clear();
    otherDetectedCcHistoryRef.current.clear();
    ccSignalHistoryRef.current.clear();
    spectrumMarkersRef.current = [];
    spectrumScaleRef.current = null;
    waterfallScaleRef.current = null;
    spectrumAxisRef.current = null;
    lastWaterfallStatusUiAtRef.current = 0;
    lastWaterfallTableUiAtRef.current = 0;
    setOtherDetectedCcRows([]);
    setSpectrumHover(null);
    setCcSignalRows([]);
    if (clearCanvases) {
      clearWaterfallCanvas(spectrumCanvasRef.current);
      clearWaterfallCanvas(canvasRef.current);
    }
  }

  function drawRetainedWaterfallNotice(next: RfSurveyWaterfallStatus | null) {
    if (hasGoodWaterfallFrameRef.current)
      return;
    const hasFrames = Boolean(next?.frame) || Boolean(next?.frames?.length);
    if (hasFrames)
      return;
    const text = next?.active
      ? "Waiting for waterfall samples..."
      : "No retained waterfall frames. Click Start to begin a new capture.";
    drawWaterfallNotice(spectrumCanvasRef.current, text, true);
    drawWaterfallNotice(canvasRef.current, text, false);
  }

  function scheduleWaterfallPaint(next: RfSurveyWaterfallStatus | null, reset = false) {
    if (!visible)
      return;
    if (reset)
      resetWaterfallDrawingState(true);
    window.requestAnimationFrame(() => {
      window.requestAnimationFrame(() => {
        renderWaterfallStatus(next);
        drawRetainedWaterfallNotice(next);
      });
    });
  }

  function renderWaterfallStatus(next: RfSurveyWaterfallStatus | null) {
    if (!visible)
      return;
    const frames = next?.frames?.length ? next.frames : next?.frame ? [next.frame] : [];
    const forceTableUpdate = frames.length > 1 || !next?.active;
    for (const frame of frames) {
      const renderKey = `${frame.sequence}:${spectrumSpanDb}:${showControlChannelLines ? "cc" : "no-cc"}:${controlChannels.join(",")}`;
      if (renderKey === lastFrameRef.current)
        continue;
      lastFrameRef.current = renderKey;
      if (frame.powersDb.length === 0) {
        const detail = frame.output || next?.message || "Waterfall capture did not return IQ samples.";
        if (!hasGoodWaterfallFrameRef.current) {
          setMessage(detail);
          drawWaterfallNotice(spectrumCanvasRef.current, detail, true);
          drawWaterfallNotice(canvasRef.current, "No spectrum samples to display.", false);
        } else {
          setMessage("");
        }
        continue;
      }
      hasGoodWaterfallFrameRef.current = true;
      const smoothed = smoothSpectrumPowers(smoothedSpectrumRef.current, frame.powersDb, 0.22);
      smoothedSpectrumRef.current = smoothed;
      heldSpectrumRef.current = holdSpectrumPowers(heldSpectrumRef.current, smoothed);
      const axis = spectrumAxisRef.current ?? { startHz: frame.startHz, sampleRate: frame.sampleRate };
      spectrumAxisRef.current = axis;
      spectrumScaleRef.current = buildSpectrumDisplayScale(frame, smoothed, spectrumSpanDb, spectrumScaleRef.current);
      waterfallScaleRef.current = buildWaterfallDisplayScale(frame, waterfallScaleRef.current);
      const consistentPeaks = updateConsistentSpectrumPeaks(peakHistoryRef.current, frame, smoothed, axis);
      const positionedPeaks = positionSpectrumPeaks(consistentPeaks, spectrumScaleRef.current, axis);
      const nextCcSignalRows = buildWaterfallCcSignalRows(systems, controlChannelOptions, smoothed, frame, axis, ccSignalHistoryRef.current);
      const frameAtMs = Date.parse(frame.capturedAtUtc) || Date.now();
      const nextOtherDetectedCcRows = updateVisibleOtherDetectedCcRows(otherDetectedCcHistoryRef.current, positionedPeaks, systems, controlChannelOptions, frameAtMs);
      const nextSpectrumSuspectedCcRows = activeSpectrumSuspectedRows(otherDetectedCcHistoryRef.current);
      spectrumMarkersRef.current = showControlChannelLines
        ? buildSpectrumMarkers(nextCcSignalRows, nextSpectrumSuspectedCcRows, axis)
        : [];
      const now = Date.now();
      if (forceTableUpdate || now - lastWaterfallTableUiAtRef.current >= 750) {
        lastWaterfallTableUiAtRef.current = now;
        setCcSignalRows(nextCcSignalRows);
        setOtherDetectedCcRows(nextOtherDetectedCcRows);
      }
      drawSpectrumFrame(spectrumCanvasRef.current, frame, smoothed, spectrumScaleRef.current, axis, {
        controlChannelsHz: controlChannelOptions,
        showControlChannels: showControlChannelLines,
        peaks: positionedPeaks,
        suspectedControlChannels: nextSpectrumSuspectedCcRows
      });
      drawWaterfallFrame(canvasRef.current, smoothWaterfallBins(frame.powersDb), waterfallScaleRef.current);
    }
  }

  useEffect(() => {
    if (!visible)
      return;
    let stopped = false;
    async function loadStatus() {
      try {
        const next = await api.request<RfSurveyWaterfallStatus>(`${apiBase}/${encodeURIComponent(surveyId)}/waterfall?history=true`);
        if (stopped) return;
        scheduleWaterfallPaint(next, true);
        setStatus({ ...next, frames: null });
        if (next.active) {
          if (Number.isFinite(next.sourceIndex))
            setSourceIndex(next.sourceIndex);
          if (next.centerHz > 0)
            setFrequencyMhz(formatMhzInput(next.centerHz));
          if (next.sampleRate > 0)
            setSampleRateMhz(formatMhzInput(next.sampleRate));
          if (next.gain?.trim())
            setGain(next.gain);
        }
        setMessage(shouldShowWaterfallMessage(next.message, next) ? next.message : "");
      } catch (error) {
        if (!stopped)
          setMessage(error instanceof Error ? error.message : "Waterfall status refresh failed.");
      }
    }
    void loadStatus();
    return () => {
      stopped = true;
    };
  }, [apiBase, surveyId, visible]);

  useEffect(() => {
    if (!visible || !status?.active) return;
    let stopped = false;
    async function poll() {
      try {
        const next = await api.request<RfSurveyWaterfallStatus>(`${apiBase}/${encodeURIComponent(surveyId)}/waterfall`);
        if (!stopped) {
          scheduleWaterfallPaint(next);
          const now = Date.now();
          if (!next.active || now - lastWaterfallStatusUiAtRef.current >= 500) {
            lastWaterfallStatusUiAtRef.current = now;
            setStatus(next);
          }
          if (shouldShowWaterfallMessage(next.message, next))
            setMessage(next.message);
        }
      } catch (error) {
        if (!stopped)
          setMessage(error instanceof Error ? error.message : "Waterfall status refresh failed.");
      }
    }
    const timer = window.setInterval(() => void poll(), 160);
    return () => {
      stopped = true;
      window.clearInterval(timer);
    };
  }, [apiBase, surveyId, status?.active, visible]);

  useEffect(() => {
    if (!visible)
      return;
    renderWaterfallStatus(status);
    drawRetainedWaterfallNotice(status);
  }, [visible, status?.frame?.sequence, spectrumSpanDb, showControlChannelLines, controlChannels.join(","), systems.map(system => `${system.shortName}:${system.controlChannelsHz.join("/")}`).join("|")]);

  async function startWaterfall() {
    if (!gainOk) {
      setMessage(`Airspy waterfall gain must be whole-number linearity gain 0-${AIRSPY_LINEARITY_GAIN_MAX}.`);
      return;
    }
    setBusy("start");
    setMessage("");
    resetWaterfallDrawingState(true);
    setIdentifyOverlayMessage("");
    try {
      const next = await api.request<RfSurveyWaterfallStatus>(`${apiBase}/${encodeURIComponent(surveyId)}/waterfall/start`, {
        method: "POST",
        body: JSON.stringify({
          sourceIndex,
          frequencyHz,
          sampleRateHz,
          gain,
          binCount: 2048,
          captureMilliseconds: 60,
          refreshMilliseconds: 120
        })
      });
      scheduleWaterfallPaint(next);
      setStatus(next);
      setMessage(shouldShowWaterfallMessage(next.message, next) ? next.message : "");
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Waterfall failed to start.");
    } finally {
      setBusy("");
    }
  }

  async function stopWaterfall() {
    setBusy("stop");
    setMessage("");
    try {
      const next = await api.request<RfSurveyWaterfallStatus>(waterfallStopUrl(apiBase, surveyId), { method: "POST" });
      lastFrameRef.current = "";
      scheduleWaterfallPaint(next);
      setStatus(next);
      setMessage(shouldShowWaterfallMessage(next.message, next) ? next.message : "");
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Waterfall failed to stop.");
    } finally {
      setBusy("");
    }
  }

  async function runP25Identify(peak: PositionedSpectrumPeak) {
    const tuneFrequency = Math.round(peak.tuneFrequencyHz ?? peak.frequencyHz);
    const measuredFrequency = Math.round(peak.measuredFrequencyHz ?? peak.frequencyHz);
    const publishedTargetFrequency = Math.round(peak.targetFrequencyHz ?? 0);
    const nearestTarget = publishedTargetFrequency > 0
      ? nearestTargetControlChannel(publishedTargetFrequency, systems, controlChannelOptions, CONTROL_CHANNEL_MATCH_TOLERANCE_HZ)
      : nearestTargetControlChannel(measuredFrequency, systems, controlChannelOptions, CONTROL_CHANNEL_MATCH_TOLERANCE_HZ);
    const targetFrequency = publishedTargetFrequency > 0 ? publishedTargetFrequency : nearestTarget?.frequencyHz ?? 0;
    const offset = targetFrequency > 0 ? Math.round(measuredFrequency - targetFrequency) : 0;
    const targetLabel = nearestTarget ? `${nearestTarget.siteLabel} ${formatRfHz(nearestTarget.frequencyHz)}` : "";
    const resumeWaterfall = status?.active === true;
    const startedAtUtc = new Date().toISOString();
    const runningResult: WaterfallIdentifyResult = {
      key: peak.key,
      peak,
      frequencyHz: tuneFrequency,
      measuredFrequencyHz: measuredFrequency,
      targetFrequencyHz: targetFrequency,
      status: "running",
      summary: "P25 Identify running",
      detail: targetLabel
        ? `Matched saved control channel ${targetLabel}; measured signal offset ${formatSignedHz(offset)}.`
        : "This peak is not within 20 kHz of a selected Setup control channel.",
      targetLabel,
      offsetHz: offset,
      createdAtUtc: startedAtUtc
    };
    setIdentifyResults(current => ({ ...current, [peak.key]: runningResult }));
    setBusy("identify");
    setSpectrumHover(null);
    setIdentifyOverlayMessage(`Stopping waterfall and probing ${formatRfHz(tuneFrequency)}...`);
    try {
      if (resumeWaterfall) {
        const next = await api.request<RfSurveyWaterfallStatus>(waterfallStopUrl(apiBase, surveyId), { method: "POST" });
        setStatus(next);
      }
      const identifyDemods = ["fsk4", "cqpsk"];
      const probeErrorHz = targetFrequency > 0 ? offset : 0;
      const attempts: RfSurveyExperiment[] = [];
      let experiment: RfSurveyExperiment | undefined;
      for (const demod of identifyDemods) {
        setIdentifyOverlayMessage(`Probing ${formatRfHz(tuneFrequency)} with ${demod.toUpperCase()}...`);
        const attempt = await onRunExperiment("control_channel_p25_probe", "about 20 seconds", tuneFrequency, {
          sourceIndex,
          durationSeconds: 10,
          parameters: {
            p25Demod: demod,
            waterfallIdentify: true,
            waterfallMeasuredFrequencyHz: measuredFrequency,
            waterfallNearestTargetHz: targetFrequency,
            waterfallPeakOffsetHz: offset,
            probeGain: gain.trim() || selectedSource?.gain || "",
            probeSampleRateHz: sampleRateHz,
            probeErrorHz
          }
        });
        if (attempt)
          attempts.push(attempt);
        experiment = attempt ?? experiment;
        if (attempt?.status === "passed" || attempt?.status === "blocked")
          break;
      }
      setIdentifyResults(current => ({
        ...current,
        [peak.key]: summarizeWaterfallIdentifyResult(runningResult, experiment, attempts.map(attempt => parseP25IdentifyFields(attempt).demod).filter(Boolean))
      }));
      await onReload();
    } catch (error) {
      setIdentifyResults(current => ({
        ...current,
        [peak.key]: {
          ...runningResult,
          status: "failed",
          summary: "P25 Identify failed",
          detail: error instanceof Error ? error.message : "P25 Identify failed before the probe result was returned."
        }
      }));
    } finally {
      if (resumeWaterfall) {
        setIdentifyOverlayMessage("Restarting waterfall...");
        try {
          const next = await api.request<RfSurveyWaterfallStatus>(`${apiBase}/${encodeURIComponent(surveyId)}/waterfall/start`, {
            method: "POST",
            body: JSON.stringify({
              sourceIndex,
              frequencyHz,
              sampleRateHz,
              gain,
              binCount: 2048,
              captureMilliseconds: 60,
              refreshMilliseconds: 120
            })
          });
          setStatus(next);
          setMessage(shouldShowWaterfallMessage(next.message, next) ? next.message : "");
        } catch (error) {
          setMessage(error instanceof Error ? `P25 Identify completed, but waterfall restart failed: ${error.message}` : "P25 Identify completed, but waterfall restart failed.");
        }
      }
      setIdentifyOverlayMessage("");
      setBusy("");
    }
  }

  function toggleSweepControlChannel(row: WaterfallCandidateRow, selected: boolean) {
    if (!showSweepSelection)
      return;
    const frequencyHz = Math.round(row.sweepFrequencyHz);
    const current = normalizeWaterfallSweepSelections(waterfallSweepSelections);
    const remaining = current.filter(item => !(item.frequencyHz === frequencyHz &&
      (item.sourceIndex === sourceIndex || (item.sourceIndex == null && item.sourceSerial && item.sourceSerial === selectedSource?.serial))));
    if (selected) {
      remaining.push({
        frequencyHz,
        sourceIndex,
        sourceSerial: selectedSource?.serial || undefined,
        gain: gain.trim() || selectedSource?.gain || "",
        sampleRateHz: sampleRateOk ? sampleRateHz : undefined,
        errorHz: Number.isFinite(row.offsetHz) ? Math.round(row.offsetHz) : undefined,
        snrDb: Number.isFinite(row.snrDb) ? row.snrDb : undefined,
        confidence: Number.isFinite(row.confidence) ? row.confidence : undefined
      });
    }
    onWaterfallSweepSelections?.(remaining);
  }

  function downloadWaterfallReport() {
    const spectrumCanvas = spectrumCanvasRef.current;
    const waterfallCanvas = canvasRef.current;
    if (!spectrumCanvas || !waterfallCanvas)
      return;
    const capturedAt = new Date().toISOString();
    const stats = [
      ["Status", status ? label(status.status) : "Stopped"],
      ["Center", frame ? formatRfHz(frame.centerHz) : frequencyOk ? formatRfHz(frequencyHz) : "--"],
      ["Sample rate", frame ? `${formatFixed(frame.sampleRate / 1_000_000, 3)} MS/s` : sampleRateOk ? `${formatFixed(sampleRateHz / 1_000_000, 3)} MS/s` : "--"],
      ["Gain", selectedSourceIsAirspy ? `Linearity ${gain || "--"}` : gain || "--"],
      ["Peak", frame ? `${formatRfHz(frame.peakFrequencyHz)} / ${formatFixed(frame.peakDb, 1)} dB` : "--"],
      ["SNR", Number.isFinite(peakSnrDb) ? `${formatFixed(peakSnrDb, 1)} dB` : "--"]
    ];
    const reportOtherRows = [...otherDetectedCcRows];
    for (const result of Object.values(identifyResults)) {
      if (result.key.startsWith("requested:") || result.status !== "passed")
        continue;
      if (!reportOtherRows.some(row => row.key === result.key))
        reportOtherRows.push({
          ...result.peak,
          displayHits: Math.max(1, result.peak.hits),
          displayMisses: 0,
          observedMs: SUSPECTED_CC_RATING_WINDOW_MS,
          missingMs: 0,
          lastFrameAtMs: Date.parse(result.createdAtUtc) || Date.now(),
          promoted: true
        });
    }
    const reportCandidates = buildWaterfallCandidateRows(ccSignalRows, reportOtherRows, systems, controlChannelOptions, identifyResults);
    const candidateRows = reportCandidates.length
      ? reportCandidates.map(row => {
        const identify = identifyResults[row.identifyPeak.key];
        const evidence = [
          `SNR ${formatFixed(row.snrDb, 1)} dB`,
          Number.isFinite(row.offsetHz) ? `measured signal offset ${formatSignedHz(row.offsetHz)}` : "",
          `confidence ${Math.round(row.confidence * 100)}%`,
          identify ? waterfallIdentifyReportText(identify) : ""
        ].filter(Boolean).join(" / ");
        return [row.siteLabel, row.targetFrequencyHz > 0 ? formatRfHz(row.targetFrequencyHz) : "--", row.detectedFrequencyHz > 0 ? formatRfHz(row.detectedFrequencyHz) : "--", waterfallCandidateSourceLabel(row, identify), evidence];
      })
      : [["--", "--", "--", "--", "No candidate rows captured."]];
    const width = 1280;
    const margin = 28;
    const imageWidth = width - margin * 2;
    const spectrumHeight = Math.round(spectrumCanvas.height * imageWidth / spectrumCanvas.width);
    const waterfallHeight = Math.round(waterfallCanvas.height * imageWidth / waterfallCanvas.width);
    const height = margin + 34 + 34 + 108
      + 28 + spectrumHeight + 28
      + 28 + waterfallHeight + 28
      + reportTableHeight(candidateRows.length)
      + margin;
    const reportCanvas = document.createElement("canvas");
    reportCanvas.width = width;
    reportCanvas.height = height;
    const ctx = reportCanvas.getContext("2d");
    if (!ctx) {
      return;
    }
    ctx.fillStyle = "#111418";
    ctx.fillRect(0, 0, width, height);
    let y = margin;
    ctx.fillStyle = "#e9ecef";
    ctx.font = "700 24px Segoe UI, Arial, sans-serif";
    ctx.fillText("PizzaWave RF Waterfall Capture", margin, y + 26);
    y += 34;
    ctx.fillStyle = "#9faab5";
    ctx.font = "14px Segoe UI, Arial, sans-serif";
    ctx.fillText(`${capturedAt} / Setup RF session ${surveyId}`, margin, y + 18);
    y += 34;
    drawReportStats(ctx, stats, margin, y, imageWidth);
    y += 108;
    y = drawReportImage(ctx, "RF Spectrum", spectrumCanvas, margin, y, imageWidth, spectrumHeight);
    y = drawReportImage(ctx, "Waterfall", waterfallCanvas, margin, y, imageWidth, waterfallHeight);
    y = drawReportTable(ctx, "Control Channel Candidates", ["Site", "Matched CC", "Detected", "Source", "Evidence"], candidateRows, margin, y, imageWidth);
    reportCanvas.toBlob(blob => {
      if (!blob)
        return;
      const url = URL.createObjectURL(blob);
      const anchor = document.createElement("a");
      anchor.href = url;
      anchor.download = `pizzawave-waterfall-${surveyId}-${capturedAt.replace(/[:.]/g, "-")}.jpg`;
      document.body.appendChild(anchor);
      anchor.click();
      anchor.remove();
      URL.revokeObjectURL(url);
    }, "image/jpeg", 0.9);
  }

  function handleSpectrumMouseMove(event: React.MouseEvent<HTMLCanvasElement>) {
    const canvas = spectrumCanvasRef.current;
    const axis = spectrumAxisRef.current;
    if (!canvas || !axis)
      return;
    const rect = canvas.getBoundingClientRect();
    const xCanvas = (event.clientX - rect.left) * canvas.width / Math.max(1, rect.width);
    const nearest = nearestSpectrumMarker(xCanvas, spectrumMarkersRef.current);
    if (!nearest) {
      setSpectrumHover(null);
      return;
    }
    const xCss = nearest.x * rect.width / canvas.width;
    const popupWidth = Math.min(460, rect.width * 0.92);
    const safeXCss = Math.max(popupWidth / 2 + 4, Math.min(rect.width - popupWidth / 2 - 4, xCss));
    setSpectrumHover({
      left: canvas.offsetLeft + safeXCss,
      top: canvas.offsetTop + 8,
      text: `${nearest.kind === "selected" ? nearest.label : "Suspected CC"} / ${formatSpectrumTickHz(nearest.frequencyHz)} / ${label(nearest.rating)} / SNR ${formatFixed(nearest.snrDb, 1)} dB / stability ${Math.round(nearest.confidence * 100)}%`
    });
  }

  const frame = status?.frame;
  const visibleMessage = shouldShowWaterfallMessage(message, status) ? message : "";
  const peakSnrDb = frame && Number.isFinite(frame.peakDb) && Number.isFinite(frame.noiseFloorDb) ? frame.peakDb - frame.noiseFloorDb : NaN;
  const displayedOtherDetectedCcRows = [...otherDetectedCcRows];
  for (const result of Object.values(identifyResults)) {
    if (result.key.startsWith("requested:") || (result.status !== "running" && result.status !== "passed"))
      continue;
    if (!displayedOtherDetectedCcRows.some(row => row.key === result.key))
      displayedOtherDetectedCcRows.push({
        ...result.peak,
        displayHits: Math.max(1, result.peak.hits),
        displayMisses: 0,
        observedMs: SUSPECTED_CC_RATING_WINDOW_MS,
        missingMs: 0,
        lastFrameAtMs: Date.parse(result.createdAtUtc) || Date.now(),
        promoted: true
      });
  }
  const waterfallCandidates = buildWaterfallCandidateRows(ccSignalRows, displayedOtherDetectedCcRows, systems, controlChannelOptions, identifyResults);
  async function toggleCandidateForSweep(row: WaterfallCandidateRow, selected: boolean) {
    if (!showSweepSelection)
      return;
    toggleSweepControlChannel(row, selected);
  }
  return <div className="rf-waterfall-panel">
    <div className="rf-waterfall-controls rf-waterfall-primary-controls">
      <label><span>Source</span><select value={String(sourceIndex)} disabled={controlsDisabled || locked || status?.active || sourceOptions.length <= 1} onChange={event => setSourceIndex(Number(event.target.value))}>
        {sourceOptions.map(source => <option value={String(source.index)} key={source.index}>Source {source.index} / {source.sdrType || "SDR"}{source.serial ? ` / ${source.serial}` : source.device ? ` / ${source.device}` : ""}</option>)}
      </select></label>
      <label className="rf-frequency-combo" ref={frequencyComboRef}><span>Frequency MHz</span><div className="rf-frequency-combo-input"><input className={frequencyOk ? "" : "invalid"} disabled={controlsDisabled} inputMode="decimal" value={frequencyMhz} onChange={event => setFrequencyMhz(event.target.value)} onFocus={() => setFrequencyMenuOpen(true)} /><button type="button" disabled={controlsDisabled} aria-label="Show saved control channels" title="Show saved control channels" onClick={() => setFrequencyMenuOpen(open => !open)}><ChevronDown size={14} aria-hidden="true" /></button></div>{frequencyMenuOpen && <div className="rf-frequency-menu" role="listbox" aria-label="Saved control channels">{controlChannelOptions.length === 0 ? <div className="rf-frequency-menu-empty">No saved CCs</div> : controlChannelOptions.map(value => <button type="button" role="option" aria-selected={formatMhzInput(value) === frequencyMhz} key={value} onMouseDown={event => event.preventDefault()} onClick={() => { setFrequencyMhz(formatMhzInput(value)); setFrequencyMenuOpen(false); }}>{formatMhzInput(value)}<span>{formatRfHz(value)}</span></button>)}</div>}</label>
      <button type="button" className="danger-button" disabled={!canStart || status?.active === true} onClick={() => void startWaterfall()}>{busy === "start" ? "Starting..." : "Start"}</button>
      <button type="button" disabled={controlsDisabled || !status?.active || busy === "stop"} onClick={() => void stopWaterfall()}>{busy === "stop" ? "Stopping..." : "Stop"}</button>
      <button type="button" className="icon-button" disabled={controlsDisabled || !frame} aria-label="Download waterfall screen grab" title="Download waterfall screen grab" onClick={downloadWaterfallReport}><Camera size={16} aria-hidden="true" /></button>
    </div>
    <details className="rf-advanced-settings rf-waterfall-advanced">
      <summary>Advanced display and capture settings</summary>
      <div className="rf-waterfall-controls">
      <label><span>Rate MHz</span><input className={sampleRateOk ? "" : "invalid"} disabled={controlsDisabled} inputMode="decimal" value={sampleRateMhz} onChange={event => setSampleRateMhz(event.target.value)} /></label>
      <label><span>{selectedSourceIsAirspy ? `Lin gain 0-${AIRSPY_LINEARITY_GAIN_MAX}` : "Gain"}</span><input className={gainOk ? "" : "invalid"} disabled={controlsDisabled} inputMode={selectedSourceIsAirspy ? "numeric" : undefined} value={gain} onChange={event => setGain(event.target.value)} /></label>
      <label><span>Power span</span><select value={String(spectrumSpanDb)} disabled={controlsDisabled} onChange={event => setSpectrumSpanDb(Number(event.target.value))}>
        <option value="20">20 dB</option>
        <option value="35">35 dB</option>
        <option value="50">50 dB</option>
        <option value="70">70 dB</option>
      </select></label>
      <label className="rf-waterfall-check"><input type="checkbox" disabled={controlsDisabled} checked={showControlChannelLines} onChange={event => setShowControlChannelLines(event.target.checked)} /><span>CC lines</span></label>
      </div>
    </details>
    {locked && <div className="setup-note">Run SDR Inventory first.</div>}
    {visibleMessage && <div className="settings-message error">{visibleMessage}</div>}
    <div className="rf-waterfall-stage">
      <div className={identifyRunning ? "rf-waterfall-display busy" : "rf-waterfall-display"} onMouseLeave={() => setSpectrumHover(null)}>
        <canvas className="rf-spectrum-canvas" ref={spectrumCanvasRef} width={1024} height={120} aria-label="RF spectrum" onMouseMove={handleSpectrumMouseMove} />
        {spectrumHover && <div className="rf-spectrum-hover" style={{ left: spectrumHover.left, top: spectrumHover.top }} onMouseDown={event => event.preventDefault()}>
          <span>{spectrumHover.text}</span>
        </div>}
        <canvas className="rf-waterfall-canvas" ref={canvasRef} width={1024} height={300} aria-label="RF waterfall" />
        {identifyRunning && <div className="rf-waterfall-identify-overlay" role="status" aria-live="polite">
          <strong>P25 Identify</strong>
          <span>{identifyOverlayMessage || "Probing selected peak..."}</span>
        </div>}
      </div>
      <div className="rf-waterfall-readout">
        <div><span>Status</span><code>{status ? label(status.status) : "Stopped"}</code></div>
        <div><span>Center</span><code>{frame ? formatRfHz(frame.centerHz) : frequencyOk ? formatRfHz(frequencyHz) : "--"}</code></div>
        <div><span>Span</span><code>{frame ? `${formatFixed(frame.sampleRate / 1_000_000, 3)} MHz` : "--"}</code></div>
        <div><span>Rate</span><code>{frame ? `${formatFixed(frame.sampleRate / 1_000_000, 3)} MS/s` : sampleRateOk ? `${formatFixed(sampleRateHz / 1_000_000, 3)} MS/s` : "--"}</code></div>
        <div><span>Gain</span><code>{selectedSourceIsAirspy ? `Linearity ${gain || "--"}` : gain || "--"}</code></div>
        <div><span>Peak</span><code>{frame ? `${formatRfHz(frame.peakFrequencyHz)} / ${formatFixed(frame.peakDb, 1)} dB` : "--"}</code></div>
        <div><span>Floor</span><code>{frame ? `${formatFixed(frame.noiseFloorDb, 1)} dB` : "--"}</code></div>
        <div><span>SNR</span><code>{Number.isFinite(peakSnrDb) ? `${formatFixed(peakSnrDb, 1)} dB` : "--"}</code></div>
        <div><span>Clip</span><code>{frame ? `${formatFixed(frame.clipPct, 2)}%${frame.overload ? " overload" : ""}` : "--"}</code></div>
        <div><span>TR</span><code>{status?.active && status.trWasActive ? "Paused" : status?.trRestartError ? "Restart failed" : "Normal"}</code></div>
      </div>
    </div>
    <div className="rf-waterfall-cc-panel">
      <div className="rf-waterfall-cc-head"><span>Control Channel Candidates</span><small>Sorted by site name. Unidentified carriers must persist for about 8 seconds; ratings settle over a 12-second window.</small></div>
      <div className="rf-waterfall-candidate-table">
        <div className={showSweepSelection ? "rf-waterfall-candidate-row header" : "rf-waterfall-candidate-row header no-sweep-selection"}>
          {showSweepSelection && <span>Use</span>}<span>Site</span><span>Matched control channel</span><span>Detected</span><span>Signal-to-noise</span><span>Measured signal offset</span><span>Rating</span><span>Source</span><span>Action</span>
        </div>
        {waterfallCandidates.length === 0 ? <div className="rf-waterfall-cc-empty">Start waterfall to inspect selected and nearby RR control channels.</div> : waterfallCandidates.map(row => {
          const identify = identifyResults[row.identifyPeak.key];
          const selected = selectedSweepControlChannelSet.has(row.sweepFrequencyHz);
          const rating = waterfallCandidateRating(row);
          return <div className={`${showSweepSelection ? "rf-waterfall-candidate-row" : "rf-waterfall-candidate-row no-sweep-selection"} ${row.origin} signal-${rating} ${identify ? `identified ${identify.status}` : ""}`.trim()} key={row.key}>
            {showSweepSelection && <label className="rf-waterfall-use-check">
              <input type="checkbox" checked={selected} onChange={event => void toggleCandidateForSweep(row, event.target.checked)} aria-label={`Use ${formatRfHz(row.sweepFrequencyHz)} for RF Sweep`} />
              <span>{selected ? "Use" : ""}</span>
            </label>}
            <span title={row.siteLabel}>{row.siteLabel}</span>
            <code>{row.targetFrequencyHz > 0 ? formatRfHz(row.targetFrequencyHz) : "--"}</code>
            <code>{row.detectedFrequencyHz > 0 ? formatRfHz(row.detectedFrequencyHz) : "--"}</code>
            <strong>{Number.isFinite(row.snrDb) ? `${formatFixed(row.snrDb, 1)} dB` : "--"}</strong>
            <span>{Number.isFinite(row.offsetHz) ? formatSignedHz(row.offsetHz) : "--"}</span>
            <span className={`rf-signal-rating ${rating}`}>{label(rating)}</span>
            <span>{waterfallCandidateSourceLabel(row, identify)}</span>
            <button type="button" disabled={identifyRunning || row.identifyPeak.frequencyHz <= 0} onClick={() => void runP25Identify(row.identifyPeak)}>{identify?.status === "running" ? "Running..." : identify ? "P25 ID Again" : "P25 ID"}</button>
            {identify && <WaterfallIdentifyDetail result={identify} />}
          </div>;
        })}
      </div>
    </div>
  </div>;
}

function buildWaterfallCandidateRows(
  requestedRows: WaterfallCcSignalRow[],
  otherRows: WaterfallDetectedCcTrack[],
  selectedSystems: RfSurveySystem[],
  fallbackControlChannels: number[],
  identifyResults: Record<string, WaterfallIdentifyResult>
): WaterfallCandidateRow[] {
  const selectedRows: WaterfallCandidateRow[] = requestedRows.map(row => {
    const identifyPeak = peakFromWaterfallCcSignalRow(row);
    const detected = row.status === "not-seen" ? 0 : Math.round(row.peakFrequencyHz);
    return {
      key: identifyPeak.key,
      origin: "selected" as const,
      siteLabel: row.siteLabel,
      systemShortName: row.systemShortName,
      targetFrequencyHz: Math.round(row.frequencyHz),
      detectedFrequencyHz: detected,
      sweepFrequencyHz: Math.round(row.frequencyHz),
      snrDb: detected > 0 && Number.isFinite(row.snrDb) ? row.snrDb : Number.NaN,
      offsetHz: detected > 0 && Number.isFinite(row.offsetHz) ? Math.round(row.offsetHz) : Number.NaN,
      confidence: clamp01(row.confidence),
      hits: Math.max(1, Math.round(row.confidence * 20)),
      identifyPeak,
      system: selectedSystems.find(system => system.shortName === row.systemShortName)
    };
  });
  const selectedNames = new Set(selectedSystems.map(system => system.shortName.toLowerCase()));
  const selectedTargets = selectedSystems.flatMap(system => system.controlChannelsHz.map(frequencyHz => ({
    systemShortName: system.shortName,
    siteLabel: system.siteLabel || system.shortName,
    frequencyHz,
    system
  })));
  const fallbackTargets = fallbackControlChannels.map(frequencyHz => ({ systemShortName: "", siteLabel: "Selected CC", frequencyHz, system: undefined as RfSurveySystem | undefined }));
  const matchableTargets = selectedTargets.length ? selectedTargets : fallbackTargets;
  const otherCandidateRows: WaterfallCandidateRow[] = otherRows.map(row => {
    const fallbackTarget = nearestFrequencyTarget(row.frequencyHz, matchableTargets, CONTROL_CHANNEL_MATCH_TOLERANCE_HZ);
    const matchedSelected = Boolean(fallbackTarget?.systemShortName && selectedNames.has(fallbackTarget.systemShortName.toLowerCase()));
    const targetFrequencyHz = matchedSelected ? Math.round(fallbackTarget!.frequencyHz) : 0;
    const origin: WaterfallCandidateRow["origin"] = matchedSelected ? "selected" : "unknown";
    const identifyPeak: PositionedSpectrumPeak = {
      ...row,
      tuneFrequencyHz: matchedSelected ? targetFrequencyHz : Math.round(row.frequencyHz),
      measuredFrequencyHz: Math.round(row.frequencyHz),
      targetFrequencyHz: matchedSelected ? targetFrequencyHz : 0
    };
    return {
      key: row.key,
      origin,
      siteLabel: matchedSelected ? fallbackTarget?.siteLabel ?? "Selected CC" : "Unidentified carrier",
      systemShortName: matchedSelected ? fallbackTarget?.systemShortName ?? "" : "",
      targetFrequencyHz,
      detectedFrequencyHz: Math.round(row.frequencyHz),
      sweepFrequencyHz: matchedSelected ? targetFrequencyHz : Math.round(row.frequencyHz),
      snrDb: Number.isFinite(row.snrDb) ? row.snrDb : Number.NEGATIVE_INFINITY,
      offsetHz: matchedSelected ? Math.round(row.frequencyHz - targetFrequencyHz) : Number.NaN,
      confidence: waterfallOtherDetectedConfidence(row),
      hits: row.displayHits,
      identifyPeak,
      system: matchedSelected ? fallbackTarget?.system : undefined
    };
  });
  const rows: WaterfallCandidateRow[] = [...selectedRows, ...otherCandidateRows];
  for (const result of Object.values(identifyResults)) {
    if (rows.some(row => row.key === result.key))
      continue;
    const measuredFrequencyHz = Math.round(result.measuredFrequencyHz || result.frequencyHz);
    const fallbackTarget = nearestFrequencyTarget(measuredFrequencyHz, matchableTargets, CONTROL_CHANNEL_MATCH_TOLERANCE_HZ);
    const matchedSelected = Boolean(fallbackTarget?.systemShortName && selectedNames.has(fallbackTarget.systemShortName.toLowerCase()));
    const targetFrequencyHz = matchedSelected ? Math.round(fallbackTarget!.frequencyHz) : 0;
    const origin: WaterfallCandidateRow["origin"] = matchedSelected ? "selected" : "unknown";
    rows.push({
      key: result.key,
      origin,
      siteLabel: matchedSelected ? fallbackTarget?.siteLabel ?? "Selected CC" : "Unidentified carrier",
      systemShortName: matchedSelected ? fallbackTarget?.systemShortName ?? "" : "",
      targetFrequencyHz,
      detectedFrequencyHz: measuredFrequencyHz,
      sweepFrequencyHz: matchedSelected ? targetFrequencyHz : measuredFrequencyHz,
      snrDb: result.peak.snrDb,
      offsetHz: matchedSelected ? Math.round(measuredFrequencyHz - targetFrequencyHz) : Number.NaN,
      confidence: clamp01(result.peak.hits / 30),
      hits: result.peak.hits,
      identifyPeak: result.peak,
      system: matchedSelected ? fallbackTarget?.system : undefined
    });
  }
  return rows.sort((left, right) =>
    left.siteLabel.localeCompare(right.siteLabel, undefined, { sensitivity: "base" })
    || left.systemShortName.localeCompare(right.systemShortName, undefined, { sensitivity: "base" })
    || (left.targetFrequencyHz || left.detectedFrequencyHz) - (right.targetFrequencyHz || right.detectedFrequencyHz));
}

function nearestFrequencyTarget<T extends { frequencyHz: number }>(frequencyHz: number, targets: T[], maxDistanceHz = Number.POSITIVE_INFINITY) {
  return targets
    .map(target => ({ target, distance: Math.abs(target.frequencyHz - frequencyHz) }))
    .filter(row => row.distance <= maxDistanceHz)
    .sort((left, right) => left.distance - right.distance)[0]?.target ?? null;
}


function waterfallCandidateSourceLabel(row: WaterfallCandidateRow, identify?: WaterfallIdentifyResult) {
  const prefix = row.origin === "selected" ? "Selected site" : "Spectrum suspect";
  if (identify?.status === "passed")
    return `${prefix} / P25`;
  if (identify?.status === "failed")
    return `${prefix} / RF only`;
  if (identify?.status === "blocked")
    return `${prefix} / blocked`;
  return prefix;
}

function peakFromWaterfallCcSignalRow(row: WaterfallCcSignalRow): PositionedSpectrumPeak {
  const useMeasuredPeak = row.status !== "not-seen" && Number.isFinite(row.peakFrequencyHz) && row.peakFrequencyHz > 0;
  return {
    key: `requested:${row.systemShortName}:${row.frequencyHz}`,
    frequencyHz: Math.round(row.frequencyHz),
    tuneFrequencyHz: Math.round(row.frequencyHz),
    measuredFrequencyHz: Math.round(useMeasuredPeak ? row.peakFrequencyHz : row.frequencyHz),
    targetFrequencyHz: Math.round(row.frequencyHz),
    powerDb: Number.isFinite(row.powerDb) ? row.powerDb : 0,
    snrDb: Number.isFinite(row.snrDb) ? row.snrDb : 0,
    hits: Math.max(1, Math.round(row.confidence * 20)),
    misses: 0,
    x: 0,
    y: 0
  };
}

function waterfallCcSignalDisplayLabel(row: WaterfallCcSignalRow, identify?: WaterfallIdentifyResult) {
  if (identify?.status === "running")
    return "P25 running";
  if (identify?.status === "passed")
    return "P25 confirmed";
  if (identify?.status === "failed")
    return row.status === "not-seen" ? "P25 failed" : "RF only";
  if (identify?.status === "blocked")
    return "P25 blocked";
  return row.status === "candidate" ? "RF candidate" : row.label;
}

function WaterfallIdentifyDetail({ result }: { result: WaterfallIdentifyResult }) {
  const fields = result.fields;
  const chips = fields ? [
    fields.nac ? `NAC 0x${fields.nac}` : "",
    fields.wacn ? `WACN ${fields.wacn}` : "",
    fields.systemId ? `Sys ${fields.systemId}` : "",
    fields.rfss ? `RFSS ${fields.rfss}` : "",
    fields.site ? `Site ${fields.site}` : "",
    fields.decodedControlChannelHz > 0 ? `Decoded CC ${formatRfHz(fields.decodedControlChannelHz)}` : "",
    fields.tsbkCount > 0 ? `${fields.tsbkCount} TSBK` : "",
    fields.grantCount > 0 ? `${fields.grantCount} grant markers` : ""
  ].filter(Boolean) : [];
  return <div className={`rf-waterfall-identify-detail ${result.status}`}>
    <span className="rf-waterfall-identify-summary">P25 Identify: {label(result.status)}</span>
    {chips.length > 0 && <span className="rf-waterfall-identify-chips">{chips.map(chip => <code key={chip}>{chip}</code>)}</span>}
    <span>{result.detail}</span>
  </div>;
}

function waterfallIdentifyReportText(result: WaterfallIdentifyResult) {
  const fields = result.fields;
  const chips = fields ? [
    fields.nac ? `NAC 0x${fields.nac}` : "",
    fields.wacn ? `WACN ${fields.wacn}` : "",
    fields.systemId ? `Sys ${fields.systemId}` : "",
    fields.rfss ? `RFSS ${fields.rfss}` : "",
    fields.site ? `Site ${fields.site}` : "",
    fields.decodedControlChannelHz > 0 ? `Decoded CC ${formatRfHz(fields.decodedControlChannelHz)}` : ""
  ].filter(Boolean).join(", ") : "";
  return `P25 Identify ${label(result.status)}${chips ? ` (${chips})` : ""}: ${result.detail}`;
}

function defaultWaterfallSampleRate(source?: RfSurveySource) {
  return source?.sdrType?.toLowerCase() === "airspy" || source?.device?.toLowerCase().includes("airspy")
    ? 6_000_000
    : source?.sampleRate || 2_400_000;
}

function shouldShowWaterfallMessage(message: string | undefined, status: RfSurveyWaterfallStatus | null | undefined) {
  const text = (message ?? "").trim();
  if (!text)
    return false;
  if (status?.trRestartError || status?.status === "failed")
    return true;
  return /\b(fail(?:ed|ure)?|error|invalid|requires?|must|unable|blocked|too small|not created|no spectrum|no samples|did not)\b/i.test(text);
}

function clearWaterfallCanvas(canvas: HTMLCanvasElement | null) {
  if (!canvas) return;
  const ctx = canvas.getContext("2d");
  if (!ctx) return;
  ctx.fillStyle = "#00356f";
  ctx.fillRect(0, 0, canvas.width, canvas.height);
}

function drawWaterfallNotice(canvas: HTMLCanvasElement | null, message: string, spectrum: boolean) {
  if (!canvas) return;
  const ctx = canvas.getContext("2d");
  if (!ctx) return;
  ctx.clearRect(0, 0, canvas.width, canvas.height);
  ctx.fillStyle = spectrum ? "#202225" : "#00356f";
  ctx.fillRect(0, 0, canvas.width, canvas.height);
  ctx.fillStyle = "rgba(230, 237, 243, .86)";
  ctx.font = "13px Segoe UI, system-ui, sans-serif";
  ctx.textAlign = "center";
  ctx.textBaseline = "middle";
  ctx.fillText(truncateCanvasText(ctx, message, canvas.width - 36), canvas.width / 2, canvas.height / 2);
}

function truncateCanvasText(ctx: CanvasRenderingContext2D, value: string, maxWidth: number) {
  const text = value.trim() || "Waterfall capture failed.";
  if (ctx.measureText(text).width <= maxWidth)
    return text;
  let low = 0;
  let high = text.length;
  while (low < high) {
    const mid = Math.ceil((low + high) / 2);
    if (ctx.measureText(`${text.slice(0, mid)}...`).width <= maxWidth)
      low = mid;
    else
      high = mid - 1;
  }
  return `${text.slice(0, low)}...`;
}

function smoothSpectrumPowers(previous: number[], next: number[], alpha: number) {
  if (!previous.length || previous.length !== next.length)
    return next.slice();
  const attack = Math.max(alpha, 0.58);
  const decay = Math.min(alpha, 0.16);
  return next.map((value, index) => {
    const previousValue = previous[index];
    const weight = value > previousValue ? attack : decay;
    return previousValue * (1 - weight) + value * weight;
  });
}

function holdSpectrumPowers(previous: number[], next: number[]) {
  if (!previous.length || previous.length !== next.length)
    return next.slice();
  const decayDbPerFrame = 1.6;
  return next.map((value, index) => Math.max(value, previous[index] - decayDbPerFrame));
}

function smoothWaterfallBins(powers: number[]) {
  if (powers.length < 3)
    return powers.slice();
  return powers.map((value, index) => {
    const left = index > 0 ? powers[index - 1] : value;
    const right = index < powers.length - 1 ? powers[index + 1] : value;
    return left * 0.22 + value * 0.56 + right * 0.22;
  });
}

type SpectrumDisplayScale = { lowDb: number; highDb: number };
type SpectrumAxis = { startHz: number; sampleRate: number };
type SpectrumPeakTrack = { key: string; frequencyHz: number; powerDb: number; snrDb: number; hits: number; misses: number };
type PositionedSpectrumPeak = SpectrumPeakTrack & { x: number; y: number; tuneFrequencyHz?: number; measuredFrequencyHz?: number; targetFrequencyHz?: number };
type WaterfallDetectedCcTrack = PositionedSpectrumPeak & { displayHits: number; displayMisses: number; observedMs: number; missingMs: number; lastFrameAtMs: number; promoted: boolean };
type P25IdentifyFields = { nac: string; wacn: string; systemId: string; rfss: string; site: string; decodedControlChannelHz: number; adjacentSites: string[]; secondaryControlChannels: string[]; tsbkCount: number; grantCount: number; demod: string; sourceIndex?: number; exitCode?: number; timedOut: boolean };
type WaterfallIdentifyResult = { key: string; peak: PositionedSpectrumPeak; frequencyHz: number; measuredFrequencyHz: number; targetFrequencyHz: number; status: "running" | "passed" | "failed" | "blocked"; summary: string; detail: string; targetLabel: string; offsetHz: number; createdAtUtc: string; experimentId?: string; fields?: P25IdentifyFields };
type WaterfallSignalRating = "strong" | "steady" | "weak";
type SpectrumHover = { left: number; top: number; text: string };
type SpectrumMarker = { x: number; frequencyHz: number; snrDb: number; confidence: number; rating: WaterfallSignalRating; kind: "selected" | "suspected"; label: string };
type SpectrumDrawOptions = { controlChannelsHz: number[]; showControlChannels: boolean; peaks: PositionedSpectrumPeak[]; suspectedControlChannels: WaterfallDetectedCcTrack[] };
type WaterfallCcSignalRow = { systemShortName: string; siteLabel: string; frequencyHz: number; status: "candidate" | "weak-trace" | "not-seen"; label: string; peakFrequencyHz: number; offsetHz: number; snrDb: number; powerDb: number; confidence: number };
type WaterfallCandidateRow = { key: string; origin: "selected" | "unknown"; siteLabel: string; systemShortName: string; targetFrequencyHz: number; detectedFrequencyHz: number; sweepFrequencyHz: number; snrDb: number; offsetHz: number; confidence: number; hits: number; identifyPeak: PositionedSpectrumPeak; system?: RfSurveySystem };
type WaterfallCcSignalTrack = { signalScore: number; hitCount: number; frameCount: number; peakFrequencyHz: number; offsetHz: number; snrDb: number; powerDb: number };

const CONTROL_CHANNEL_MATCH_TOLERANCE_HZ = 8_000;
const SPECTRUM_SUSPECT_MIN_OBSERVED_MS = 5_000;
const SUSPECTED_CC_PROMOTION_MS = 8_000;
const SUSPECTED_CC_RATING_WINDOW_MS = 12_000;
const SUSPECTED_CC_RETENTION_MS = 12_000;

function buildSpectrumDisplayScale(frame: NonNullable<RfSurveyWaterfallStatus["frame"]>, powers: number[], spanDb = 45, previous?: SpectrumDisplayScale | null): SpectrumDisplayScale {
  if (previous)
    return previous;
  const span = Math.max(15, Math.min(90, spanDb));
  const finitePowers = powers.filter(Number.isFinite);
  const peak = Number.isFinite(frame.peakDb) ? frame.peakDb : finitePowers.length ? Math.max(...finitePowers) : -40;
  const noise = Number.isFinite(frame.noiseFloorDb) ? frame.noiseFloorDb : finitePowers.length ? percentile(finitePowers, 0.5) : peak - span * 0.55;
  const high = Math.ceil(Math.max(peak + 3, noise + Math.min(span * 0.72, 18)) / 5) * 5;
  return { lowDb: high - span, highDb: high };
}

function buildWaterfallDisplayScale(frame: NonNullable<RfSurveyWaterfallStatus["frame"]>, previous?: SpectrumDisplayScale | null): SpectrumDisplayScale {
  if (previous)
    return previous;
  const noise = Number.isFinite(frame.noiseFloorDb) ? frame.noiseFloorDb : frame.minDb;
  const peak = Number.isFinite(frame.peakDb) ? frame.peakDb : frame.maxDb;
  const low = Math.floor((noise + 1) / 2) * 2;
  const span = Math.max(18, Math.min(34, peak - low + 6));
  return { lowDb: low, highDb: low + span };
}

function buildWaterfallCcSignalRows(systems: RfSurveySystem[], fallbackControlChannels: number[], powers: number[], frame: NonNullable<RfSurveyWaterfallStatus["frame"]>, axis: SpectrumAxis, history: Map<string, WaterfallCcSignalTrack>): WaterfallCcSignalRow[] {
  const targets = systems.length
    ? systems.flatMap(system => system.controlChannelsHz.map(frequencyHz => ({
      systemShortName: system.shortName || system.siteLabel || "System",
      siteLabel: system.siteLabel || system.shortName || "Site",
      frequencyHz
    })))
    : fallbackControlChannels.map(frequencyHz => ({ systemShortName: "Target", siteLabel: "Target", frequencyHz }));
  const uniqueTargets = new Map<string, (typeof targets)[number]>();
  for (const target of targets)
    uniqueTargets.set(`${target.systemShortName}:${target.frequencyHz}`, target);
  const visibleTargets = [...uniqueTargets.values()]
    .filter(target => target.frequencyHz >= axis.startHz && target.frequencyHz <= axis.startHz + axis.sampleRate);
  const currentKeys = new Set(visibleTargets.map(target => `${target.systemShortName}:${target.frequencyHz}`));
  for (const key of [...history.keys()]) {
    if (!currentKeys.has(key))
      history.delete(key);
  }
  return visibleTargets.map(target => {
    const key = `${target.systemShortName}:${target.frequencyHz}`;
    const nearest = nearestPeakAroundFrequency(target.frequencyHz, powers, frame, axis, CONTROL_CHANNEL_MATCH_TOLERANCE_HZ);
    const previous = history.get(key);
    const isSignalFrame = Boolean(nearest && nearest.snrDb >= 6);
    const signalScore = clamp01((previous?.signalScore ?? 0) * (isSignalFrame ? 0.9 : 0.94) + (isSignalFrame ? 0.1 : 0));
    const hitCount = Math.min(200, (previous?.hitCount ?? 0) * 0.985 + (isSignalFrame ? 1 : 0));
    const frameCount = Math.min(200, (previous?.frameCount ?? 0) * 0.985 + 1);
    const confidence = frameCount > 0 ? clamp01(hitCount / frameCount) : 0;
    const nextTrack: WaterfallCcSignalTrack = {
      signalScore,
      hitCount,
      frameCount,
      peakFrequencyHz: nearest && isSignalFrame ? ema(previous?.peakFrequencyHz, nearest.frequencyHz, 0.18) : previous?.peakFrequencyHz ?? target.frequencyHz,
      offsetHz: nearest && isSignalFrame ? ema(previous?.offsetHz, nearest.frequencyHz - target.frequencyHz, 0.18) : previous?.offsetHz ?? 0,
      snrDb: nearest && isSignalFrame ? ema(previous?.snrDb, nearest.snrDb, 0.18) : (previous?.snrDb ?? 0) * 0.97,
      powerDb: nearest && isSignalFrame ? ema(previous?.powerDb, nearest.powerDb, 0.18) : previous?.powerDb ?? 0
    };
    history.set(key, nextTrack);
    const status = nextTrack.signalScore >= 0.48 && nextTrack.snrDb >= 10 && confidence >= 0.35
      ? "candidate"
      : nextTrack.signalScore >= 0.18 && nextTrack.snrDb >= 6
        ? "weak-trace"
        : "not-seen";
    return {
      ...target,
      status,
      label: status === "candidate" ? "candidate" : status === "weak-trace" ? "weak trace" : "not seen",
      peakFrequencyHz: nextTrack.peakFrequencyHz,
      offsetHz: nextTrack.offsetHz,
      snrDb: nextTrack.snrDb,
      powerDb: nextTrack.powerDb,
      confidence
    };
  });
}

function ema(previous: number | undefined, next: number, alpha: number) {
  return Number.isFinite(previous) ? previous! * (1 - alpha) + next * alpha : next;
}

function clamp01(value: number) {
  return Math.max(0, Math.min(1, value));
}

function nearestPeakAroundFrequency(frequencyHz: number, powers: number[], frame: NonNullable<RfSurveyWaterfallStatus["frame"]>, axis: SpectrumAxis, searchHz: number) {
  if (!powers.length || frequencyHz < axis.startHz || frequencyHz > axis.startHz + axis.sampleRate)
    return null;
  const noiseFloorDb = Number.isFinite(frame.noiseFloorDb) ? frame.noiseFloorDb : percentile(powers.filter(Number.isFinite), 0.5);
  const binWidth = axis.sampleRate / Math.max(1, powers.length);
  const centerIndex = Math.round((frequencyHz - axis.startHz) / Math.max(1, binWidth) - 0.5);
  const radius = Math.max(1, Math.ceil(searchHz / Math.max(1, binWidth)));
  let bestIndex = -1;
  let bestPower = -Infinity;
  const start = Math.max(0, centerIndex - radius);
  const end = Math.min(powers.length - 1, centerIndex + radius);
  for (let index = start; index <= end; index++) {
    const value = powers[index];
    const candidateFrequencyHz = axis.startHz + (index + 0.5) * binWidth;
    if (Math.abs(candidateFrequencyHz - frequencyHz) > searchHz)
      continue;
    if (Number.isFinite(value) && value > bestPower) {
      bestPower = value;
      bestIndex = index;
    }
  }
  if (bestIndex < 0)
    return null;
  const peakFrequencyHz = axis.startHz + (bestIndex + 0.5) * binWidth;
  return {
    frequencyHz: peakFrequencyHz,
    powerDb: bestPower,
    snrDb: bestPower - noiseFloorDb
  };
}

function nearestTargetControlChannel(frequencyHz: number, systems: RfSurveySystem[], fallbackControlChannels: number[], maxDistanceHz: number) {
  const targets = systems.length
    ? systems.flatMap(system => system.controlChannelsHz.map(targetHz => ({
      systemShortName: system.shortName,
      siteLabel: system.siteLabel || system.shortName || "Site",
      frequencyHz: targetHz
    })))
    : fallbackControlChannels.map(targetHz => ({ systemShortName: "Target", siteLabel: "Target", frequencyHz: targetHz }));
  return targets
    .map(target => ({ ...target, distance: Math.abs(target.frequencyHz - frequencyHz) }))
    .filter(target => target.distance <= maxDistanceHz)
    .sort((left, right) => left.distance - right.distance)[0] ?? null;
}

function summarizeWaterfallIdentifyResult(base: WaterfallIdentifyResult, experiment: RfSurveyExperiment | undefined, attemptedDemods: string[] = []): WaterfallIdentifyResult {
  if (!experiment)
    return {
      ...base,
      status: "failed",
      summary: "P25 Identify did not return a result",
      detail: base.targetLabel
        ? `${base.detail} No experiment result was returned.`
        : "The peak is not in the saved CC list, and no experiment result was returned."
    };
  const status = experiment.status === "passed" || experiment.status === "blocked" || experiment.status === "failed"
    ? experiment.status
    : "failed";
  const p25Summary = experiment.blockingIssue || experiment.resultSummary || label(experiment.status);
  const fields = parseP25IdentifyFields(experiment);
  const hasStrongIdentityEvidence = p25IdentifyHasStrongEvidence(fields);
  const effectiveStatus: WaterfallIdentifyResult["status"] = status === "passed" && !hasStrongIdentityEvidence ? "failed" : status;
  const demodDetail = attemptedDemods.length > 1 ? `Tried demods: ${attemptedDemods.join(", ")}.` : "";
  const targetDetail = base.targetLabel
    ? `Matched saved control channel ${base.targetLabel}; measured signal offset ${formatSignedHz(base.offsetHz)}.`
    : "Not in the selected Setup control-channel list; probed the measured peak directly.";
  const identitySummary = p25IdentifySummary(fields);
  const measuredFrequency = base.measuredFrequencyHz || base.frequencyHz;
  const tunedFrequency = base.frequencyHz;
  const tunedOffset = fields.decodedControlChannelHz > 0
    ? Math.round(measuredFrequency - fields.decodedControlChannelHz)
    : 0;
  const decodedDetail = fields.decodedControlChannelHz > 0
    ? `Tuned ${formatRfHz(tunedFrequency)}; decoded CC ${formatRfHz(fields.decodedControlChannelHz)}; waterfall peak ${formatRfHz(measuredFrequency)} (${formatSignedHz(tunedOffset)} from decoded CC).`
    : `Tuned ${formatRfHz(tunedFrequency)}; waterfall peak ${formatRfHz(measuredFrequency)}.`;
  const activityDetail = [
    fields.tsbkCount > 0 ? `${fields.tsbkCount} TSBK message${fields.tsbkCount === 1 ? "" : "s"}` : "",
    fields.grantCount > 0 ? `${fields.grantCount} grant/message marker${fields.grantCount === 1 ? "" : "s"}` : "",
    fields.adjacentSites.length ? `Adjacent ${fields.adjacentSites.slice(0, 3).join(", ")}` : "",
    fields.secondaryControlChannels.length ? `Secondary CC ${fields.secondaryControlChannels.slice(0, 3).join(", ")}` : ""
  ].filter(Boolean).join("; ");
  return {
    ...base,
    status: effectiveStatus,
    summary: effectiveStatus === "passed"
      ? identitySummary || "P25 frame evidence captured"
      : effectiveStatus === "blocked"
        ? "P25 Identify blocked"
        : status === "passed"
          ? "Weak/invalid P25 evidence"
          : "No P25 frame evidence",
    detail: effectiveStatus === "passed"
      ? [decodedDetail, targetDetail, activityDetail, demodDetail].filter(Boolean).join(" ")
      : [status === "passed" && !hasStrongIdentityEvidence ? "Probe only reported weak evidence such as NAC without decoded P25 control-channel messages." : p25Summary, targetDetail, demodDetail].filter(Boolean).join(" "),
    experimentId: experiment.id,
    fields
  };
}

function parseP25IdentifyFields(experiment: RfSurveyExperiment | undefined): P25IdentifyFields {
  const evidence = parseExperimentJson<any>(experiment?.evidenceJson ?? "{}");
  const output = String(evidence?.output ?? "");
  const fields: P25IdentifyFields = {
    nac: "",
    wacn: "",
    systemId: "",
    rfss: "",
    site: "",
    decodedControlChannelHz: 0,
    adjacentSites: [],
    secondaryControlChannels: [],
    tsbkCount: 0,
    grantCount: 0,
    demod: String(evidence?.demod ?? ""),
    sourceIndex: Number.isFinite(Number(evidence?.sourceIndex)) ? Number(evidence.sourceIndex) : undefined,
    exitCode: Number.isFinite(Number(evidence?.exitCode)) ? Number(evidence.exitCode) : undefined,
    timedOut: /timed out|was canceled/i.test(output)
  };
  const nac = /\bNAC\s+0x([0-9a-f]+)/i.exec(output);
  if (nac)
    fields.nac = nac[1].toUpperCase();
  const netStatus = /net_sts_bcst:\s*wacn:\s*([0-9a-f]+)\s+syid:\s*([0-9a-f]+)/i.exec(output);
  if (netStatus) {
    fields.wacn = netStatus[1].toUpperCase();
    fields.systemId = netStatus[2].toUpperCase();
  }
  const rfssStatus = /rfss_sts_bcst:\s*syid:\s*([0-9a-f]+)\s+rfid:\s*([0-9a-f]+)\s+stid:\s*([0-9a-f]+)\s+ch1:\s*([0-9a-f]+)\(([^)]*)\)/ig;
  let rfssMatch: RegExpExecArray | null;
  while ((rfssMatch = rfssStatus.exec(output)) !== null) {
    fields.systemId ||= rfssMatch[1].toUpperCase();
    fields.rfss ||= rfssMatch[2].toUpperCase();
    fields.site ||= rfssMatch[3].toUpperCase();
    const decoded = p25ChannelTextToHz(rfssMatch[5]);
    if (decoded > 0)
      fields.decodedControlChannelHz ||= decoded;
  }
  const adjacentStatus = /adj_sts_bcst:\s*rfid:\s*([0-9a-f]+)\s+stid:\s*([0-9a-f]+)\s+ch1:\s*[0-9a-f]+\(([^)]*)\)/ig;
  let adjacentMatch: RegExpExecArray | null;
  while ((adjacentMatch = adjacentStatus.exec(output)) !== null) {
    const frequency = p25ChannelTextToHz(adjacentMatch[3]);
    fields.adjacentSites.push(`${adjacentMatch[1].toUpperCase()}-${adjacentMatch[2].toUpperCase()}${frequency > 0 ? ` ${formatRfHz(frequency)}` : ""}`);
  }
  const secondaryStatus = /sccb:\s*rfid:\s*([0-9a-f]+)\s+stid:\s*([0-9a-f]+)\s+ch1:\s*[0-9a-f]+\(([^)]*)\)(?:\s+ch2:\s*[0-9a-f]+\(([^)]*)\))?/ig;
  let secondaryMatch: RegExpExecArray | null;
  while ((secondaryMatch = secondaryStatus.exec(output)) !== null) {
    for (const value of [secondaryMatch[3], secondaryMatch[4]]) {
      const frequency = p25ChannelTextToHz(value);
      if (frequency > 0)
        fields.secondaryControlChannels.push(formatRfHz(frequency));
    }
  }
  fields.adjacentSites = Array.from(new Set(fields.adjacentSites)).slice(0, 6);
  fields.secondaryControlChannels = Array.from(new Set(fields.secondaryControlChannels)).slice(0, 6);
  fields.tsbkCount = (output.match(/\bTSBK\b/ig) ?? []).length + (output.match(/\btsbk\(/ig) ?? []).length;
  fields.grantCount = (output.match(/voice grant|grp_v_ch_grant|grg_add_cmd|grant/ig) ?? []).length;
  return fields;
}

function p25ChannelTextToHz(value: string | undefined) {
  const text = (value ?? "").trim();
  const mhz = /^(\d{3,4}\.\d+)$/.exec(text);
  return mhz ? Math.round(Number(mhz[1]) * 1_000_000) : 0;
}

function p25IdentifySummary(fields: P25IdentifyFields) {
  const parts = [
    fields.nac ? `NAC 0x${fields.nac}` : "",
    fields.wacn ? `WACN ${fields.wacn}` : "",
    fields.systemId ? `Sys ${fields.systemId}` : "",
    fields.rfss ? `RFSS ${fields.rfss}` : "",
    fields.site ? `Site ${fields.site}` : ""
  ].filter(Boolean);
  return parts.join(" / ");
}

function p25IdentifyHasStrongEvidence(fields: P25IdentifyFields) {
  return Boolean(
    fields.wacn ||
    fields.systemId ||
    fields.rfss ||
    fields.site ||
    fields.decodedControlChannelHz > 0 ||
    fields.tsbkCount > 0 ||
    fields.grantCount > 0 ||
    fields.adjacentSites.length ||
    fields.secondaryControlChannels.length
  );
}

function waterfallOtherDetectedConfidence(row: Pick<WaterfallDetectedCcTrack, "observedMs">) {
  return clamp01(row.observedMs / SUSPECTED_CC_RATING_WINDOW_MS);
}

function waterfallSignalRating(snrDb: number, confidence: number): WaterfallSignalRating {
  if (snrDb >= 12 && confidence >= 0.65)
    return "strong";
  if (snrDb >= 6 && confidence >= 0.45)
    return "steady";
  return "weak";
}

function waterfallCandidateRating(row: Pick<WaterfallCandidateRow, "snrDb" | "confidence">) {
  return waterfallSignalRating(row.snrDb, row.confidence);
}

function updateVisibleOtherDetectedCcRows(history: Map<string, WaterfallDetectedCcTrack>, peaks: PositionedSpectrumPeak[], systems: RfSurveySystem[], fallbackControlChannels: number[], frameAtMs: number) {
  const activeKeys = new Set<string>();
  for (const track of history.values())
    track.displayMisses += 1;
  for (const peak of peaks.filter(row => !nearestTargetControlChannel(row.frequencyHz, systems, fallbackControlChannels, CONTROL_CHANNEL_MATCH_TOLERANCE_HZ))) {
    activeKeys.add(peak.key);
    const previous = history.get(peak.key);
    const elapsedMs = previous ? Math.max(0, Math.min(1_000, frameAtMs - previous.lastFrameAtMs)) : 0;
    const nextObservedMs = Math.min(SUSPECTED_CC_RATING_WINDOW_MS * 2, (previous?.observedMs ?? 0) + elapsedMs);
    history.set(peak.key, {
      ...peak,
      frequencyHz: ema(previous?.frequencyHz, peak.frequencyHz, 0.24),
      powerDb: ema(previous?.powerDb, peak.powerDb, 0.32),
      snrDb: ema(previous?.snrDb, peak.snrDb, 0.32),
      x: ema(previous?.x, peak.x, 0.28),
      y: ema(previous?.y, peak.y, 0.28),
      hits: peak.hits,
      misses: peak.misses,
      displayHits: Math.min(200, (previous?.displayHits ?? 0) + 1),
      displayMisses: 0,
      observedMs: nextObservedMs,
      missingMs: 0,
      lastFrameAtMs: frameAtMs,
      promoted: Boolean(previous?.promoted) || nextObservedMs >= SUSPECTED_CC_PROMOTION_MS
    });
  }
  for (const [key, track] of history.entries()) {
    if (!activeKeys.has(key)) {
      const elapsedMs = Math.max(0, Math.min(1_000, frameAtMs - track.lastFrameAtMs));
      track.displayHits = Math.max(0, track.displayHits - 0.35);
      track.observedMs = Math.max(0, track.observedMs - elapsedMs * 0.25);
      track.missingMs += elapsedMs;
      track.lastFrameAtMs = frameAtMs;
    }
    if (track.missingMs > SUSPECTED_CC_RETENTION_MS)
      history.delete(key);
  }
  return [...history.values()]
    .filter(track => track.promoted && track.missingMs <= SUSPECTED_CC_RETENTION_MS)
    .sort((left, right) => right.snrDb - left.snrDb)
    .slice(0, 8);
}

function activeSpectrumSuspectedRows(history: Map<string, WaterfallDetectedCcTrack>) {
  return [...history.values()]
    .filter(track => track.observedMs >= SPECTRUM_SUSPECT_MIN_OBSERVED_MS && track.missingMs <= 1_200)
    .sort((left, right) => left.frequencyHz - right.frequencyHz)
    .slice(0, 16);
}

function updateConsistentSpectrumPeaks(history: Map<string, SpectrumPeakTrack>, frame: NonNullable<RfSurveyWaterfallStatus["frame"]>, powers: number[], axis: SpectrumAxis) {
  const noiseFloorDb = Number.isFinite(frame.noiseFloorDb) ? frame.noiseFloorDb : percentile(powers.filter(Number.isFinite), 0.5);
  const candidates: SpectrumPeakTrack[] = [];
  for (let index = 2; index < powers.length - 2; index++) {
    const value = powers[index];
    if (!Number.isFinite(value))
      continue;
    const snrDb = value - noiseFloorDb;
    if (snrDb < 8)
      continue;
    if (value < powers[index - 1] || value < powers[index + 1] || value < powers[index - 2] || value < powers[index + 2])
      continue;
    const frequencyHz = axis.startHz + (index + 0.5) * axis.sampleRate / Math.max(1, powers.length);
    const key = String(Math.round(frequencyHz / 5_000) * 5_000);
    candidates.push({ key, frequencyHz, powerDb: value, snrDb, hits: 1, misses: 0 });
  }

  for (const track of history.values()) {
    track.misses += 1;
    track.hits = Math.max(0, track.hits - 1);
  }

  for (const candidate of candidates.sort((left, right) => right.snrDb - left.snrDb).slice(0, 16)) {
    const previous = history.get(candidate.key);
    history.set(candidate.key, previous
      ? {
        ...previous,
        frequencyHz: previous.frequencyHz * 0.65 + candidate.frequencyHz * 0.35,
        powerDb: previous.powerDb * 0.55 + candidate.powerDb * 0.45,
        snrDb: previous.snrDb * 0.55 + candidate.snrDb * 0.45,
        hits: Math.min(8, previous.hits + 2),
        misses: 0
      }
      : candidate);
  }

  for (const [key, track] of history.entries()) {
    if (track.misses > 12 || track.hits <= 0)
      history.delete(key);
  }

  return [...history.values()]
    .filter(track => track.hits >= 4 && track.misses <= 2 && track.snrDb >= 8)
    .sort((left, right) => right.snrDb - left.snrDb)
    .slice(0, 10);
}

function positionSpectrumPeaks(peaks: SpectrumPeakTrack[], scale: SpectrumDisplayScale, axis: SpectrumAxis): PositionedSpectrumPeak[] {
  const { margin, plotWidth, plotHeight } = spectrumGeometry();
  const low = scale.lowDb;
  const high = Math.max(low + 1, scale.highDb);
  const yFor = (value: number) => margin.top + (1 - Math.max(0, Math.min(1, (value - low) / Math.max(1, high - low)))) * plotHeight;
  return peaks
    .map(peak => ({
      ...peak,
      x: margin.left + (peak.frequencyHz - axis.startHz) / Math.max(1, axis.sampleRate) * plotWidth,
      y: yFor(peak.powerDb)
    }))
    .filter(peak => peak.x >= margin.left && peak.x <= margin.left + plotWidth && peak.y >= margin.top && peak.y <= margin.top + plotHeight);
}

function buildSpectrumMarkers(requestedRows: WaterfallCcSignalRow[], suspectedRows: WaterfallDetectedCcTrack[], axis: SpectrumAxis): SpectrumMarker[] {
  const { margin, plotWidth } = spectrumGeometry();
  const selected = requestedRows
    .filter(row => row.frequencyHz >= axis.startHz && row.frequencyHz <= axis.startHz + axis.sampleRate)
    .map(row => {
      const seen = row.status !== "not-seen";
      const snrDb = seen ? row.snrDb : 0;
      const confidence = seen ? row.confidence : 0;
      return {
        x: margin.left + (row.frequencyHz - axis.startHz) / Math.max(1, axis.sampleRate) * plotWidth,
        frequencyHz: row.frequencyHz,
        snrDb,
        confidence,
        rating: waterfallSignalRating(snrDb, confidence),
        kind: "selected" as const,
        label: `${row.siteLabel} selected CC`
      };
    });
  const suspected = suspectedRows.map(row => {
    const confidence = waterfallOtherDetectedConfidence(row);
    return {
      x: row.x,
      frequencyHz: row.frequencyHz,
      snrDb: row.snrDb,
      confidence,
      rating: waterfallSignalRating(row.snrDb, confidence),
      kind: "suspected" as const,
      label: "Suspected CC"
    };
  });
  return [...selected, ...suspected];
}

function nearestSpectrumMarker(xCanvas: number, markers: SpectrumMarker[]) {
  let nearest: SpectrumMarker | null = null;
  let nearestDistance = Infinity;
  for (const marker of markers) {
    const distance = Math.abs(marker.x - xCanvas);
    if (distance < nearestDistance) {
      nearest = marker;
      nearestDistance = distance;
    }
  }
  return nearest && nearestDistance <= 12 ? nearest : null;
}

function spectrumGeometry(width = 1024, height = 150) {
  const horizontalScale = width / 1024;
  const margin = { left: 58 * horizontalScale, right: 10 * horizontalScale, top: 10, bottom: 30 };
  return {
    margin,
    plotWidth: width - margin.left - margin.right,
    plotHeight: height - margin.top - margin.bottom
  };
}

function drawSpectrumFrame(canvas: HTMLCanvasElement | null, frame: NonNullable<RfSurveyWaterfallStatus["frame"]>, powers: number[], scale: SpectrumDisplayScale, axis: SpectrumAxis, options?: SpectrumDrawOptions) {
  if (!canvas || powers.length === 0) return;
  const ctx = canvas.getContext("2d");
  if (!ctx) return;
  const width = 1024;
  const height = 150;
  if (canvas.width !== width) canvas.width = width;
  if (canvas.height !== height) canvas.height = height;
  const { margin, plotWidth, plotHeight } = spectrumGeometry(width, height);
  const low = scale.lowDb;
  const high = Math.max(low + 1, scale.highDb);
  const yFor = (value: number) => margin.top + (1 - Math.max(0, Math.min(1, (value - low) / Math.max(1, high - low)))) * plotHeight;
  const noiseFloorDb = Number.isFinite(frame.noiseFloorDb) ? frame.noiseFloorDb : Math.min(...powers.filter(Number.isFinite));
  const noiseY = yFor(noiseFloorDb);
  ctx.clearRect(0, 0, width, height);
  ctx.fillStyle = "#202225";
  ctx.fillRect(0, 0, width, height);
  ctx.font = "11px Segoe UI, system-ui, sans-serif";
  ctx.lineWidth = 1;
  ctx.strokeStyle = "rgba(230, 237, 243, .24)";
  ctx.strokeRect(margin.left, margin.top, plotWidth, plotHeight);
  ctx.fillStyle = "rgba(230, 237, 243, .86)";
  ctx.save();
  ctx.translate(14, margin.top + plotHeight / 2);
  ctx.rotate(-Math.PI / 2);
  ctx.textAlign = "center";
  ctx.fillText("Power (dB)", 0, 0);
  ctx.restore();
  ctx.textAlign = "right";
  ctx.textBaseline = "middle";
  for (let t = 0; t <= 4; t++) {
    const value = low + (high - low) * t / 4;
    const y = yFor(value);
    ctx.strokeStyle = t === 0 || t === 4 ? "rgba(230, 237, 243, .22)" : "rgba(230, 237, 243, .10)";
    ctx.beginPath();
    ctx.moveTo(margin.left, y);
    ctx.lineTo(margin.left + plotWidth, y);
    ctx.stroke();
    ctx.fillStyle = "rgba(230, 237, 243, .8)";
    ctx.fillText(`${Math.round(value)}`, margin.left - 6, y);
  }
  ctx.strokeStyle = "rgba(255, 223, 53, .95)";
  ctx.lineWidth = 1.6;
  ctx.beginPath();
  ctx.moveTo(margin.left, noiseY);
  ctx.lineTo(margin.left + plotWidth, noiseY);
  ctx.stroke();
  ctx.strokeStyle = "rgba(230, 237, 243, .38)";
  ctx.beginPath();
  ctx.moveTo(margin.left + plotWidth / 2, margin.top);
  ctx.lineTo(margin.left + plotWidth / 2, margin.top + plotHeight);
  ctx.stroke();
  ctx.textAlign = "center";
  ctx.textBaseline = "top";
  for (let t = 0; t <= 4; t++) {
    const x = margin.left + plotWidth * t / 4;
    const frequency = axis.startHz + axis.sampleRate * t / 4;
    ctx.strokeStyle = "rgba(230, 237, 243, .28)";
    ctx.beginPath();
    ctx.moveTo(x, margin.top + plotHeight);
    ctx.lineTo(x, margin.top + plotHeight + 5);
    ctx.stroke();
    ctx.fillStyle = t === 2 ? "#ffdf35" : "rgba(230, 237, 243, .84)";
    ctx.fillText(formatSpectrumTickHz(frequency), x, margin.top + plotHeight + 8);
  }
  if (options?.showControlChannels) {
    ctx.textBaseline = "top";
    for (const suspected of options.suspectedControlChannels) {
      const confidence = waterfallOtherDetectedConfidence(suspected);
      const rating = waterfallSignalRating(suspected.snrDb, confidence);
      ctx.strokeStyle = rating === "strong" ? "rgba(255, 223, 53, .95)" : rating === "steady" ? "rgba(255, 223, 53, .72)" : "rgba(255, 223, 53, .48)";
      ctx.lineWidth = rating === "strong" ? 1.8 : rating === "steady" ? 1.4 : 1;
      ctx.beginPath();
      ctx.moveTo(suspected.x, margin.top);
      ctx.lineTo(suspected.x, margin.top + plotHeight);
      ctx.stroke();
    }
    for (const frequency of options.controlChannelsHz) {
      if (frequency < axis.startHz || frequency > axis.startHz + axis.sampleRate)
        continue;
      const x = margin.left + (frequency - axis.startHz) / Math.max(1, axis.sampleRate) * plotWidth;
      ctx.strokeStyle = frequency === frame.centerHz ? "rgba(255, 70, 70, .95)" : "rgba(255, 70, 70, .62)";
      ctx.lineWidth = frequency === frame.centerHz ? 1.6 : 1.1;
      ctx.beginPath();
      ctx.moveTo(x, margin.top);
      ctx.lineTo(x, margin.top + plotHeight);
      ctx.stroke();
    }
  }
  const points: Array<[number, number]> = [];
  for (let x = margin.left; x <= margin.left + plotWidth; x++) {
    const start = Math.floor((x - margin.left) / Math.max(1, plotWidth) * powers.length);
    const end = Math.max(start + 1, Math.ceil((x - margin.left + 1) / Math.max(1, plotWidth) * powers.length));
    const value = pooledSpectrumPower(powers, start, end);
    points.push([x, yFor(value)]);
  }
  const baselineY = margin.top + plotHeight;
  const fill = ctx.createLinearGradient(0, margin.top, 0, baselineY);
  fill.addColorStop(0, "rgba(118, 236, 255, .72)");
  fill.addColorStop(0.55, "rgba(51, 195, 214, .36)");
  fill.addColorStop(1, "rgba(10, 98, 126, .08)");
  ctx.beginPath();
  ctx.moveTo(points[0][0], baselineY);
  for (const [x, y] of points)
    ctx.lineTo(x, y);
  ctx.lineTo(points[points.length - 1][0], baselineY);
  ctx.closePath();
  ctx.fillStyle = fill;
  ctx.fill();
  ctx.beginPath();
  for (const [index, point] of points.entries()) {
    if (index === 0) ctx.moveTo(point[0], point[1]);
    else ctx.lineTo(point[0], point[1]);
  }
  ctx.strokeStyle = "#8cf3ff";
  ctx.lineWidth = 1.2;
  ctx.stroke();

  if (options?.peaks.length) {
    const labelSlots: Array<[number, number]> = [];
    ctx.font = "11px Segoe UI, system-ui, sans-serif";
    for (const peak of options.peaks) {
      const text = (peak.frequencyHz / 1_000_000).toFixed(4);
      const labelWidth = ctx.measureText(text).width + 8;
      const labelX = Math.max(margin.left + 2, Math.min(margin.left + plotWidth - labelWidth - 2, peak.x - labelWidth / 2));
      const labelY = Math.max(margin.top + 2, peak.y - 18);
      if (labelSlots.some(([left, right]) => labelX < right + 6 && labelX + labelWidth > left - 6))
        continue;
      labelSlots.push([labelX, labelX + labelWidth]);
      ctx.strokeStyle = "rgba(255, 223, 53, .78)";
      ctx.lineWidth = 1;
      ctx.beginPath();
      ctx.moveTo(peak.x, peak.y - 2);
      ctx.lineTo(peak.x, labelY + 13);
      ctx.stroke();
      ctx.fillStyle = "rgba(13, 17, 23, .82)";
      ctx.fillRect(labelX, labelY, labelWidth, 14);
      ctx.strokeStyle = "rgba(255, 223, 53, .48)";
      ctx.strokeRect(labelX, labelY, labelWidth, 14);
      ctx.fillStyle = "#ffdf35";
      ctx.textAlign = "center";
      ctx.textBaseline = "middle";
      ctx.fillText(text, labelX + labelWidth / 2, labelY + 7);
    }
  }
}

function percentile(values: number[], quantile: number) {
  if (values.length === 0)
    return NaN;
  const sorted = values.slice().sort((left, right) => left - right);
  const index = Math.max(0, Math.min(sorted.length - 1, Math.round((sorted.length - 1) * quantile)));
  return sorted[index];
}

function pooledSpectrumPower(powers: number[], start: number, end: number) {
  const left = Math.max(0, Math.min(powers.length - 1, start));
  const right = Math.max(left + 1, Math.min(powers.length, end));
  let max = -Infinity;
  let total = 0;
  let count = 0;
  for (let index = left; index < right; index++) {
    const value = powers[index];
    if (!Number.isFinite(value))
      continue;
    max = Math.max(max, value);
    total += value;
    count += 1;
  }
  if (count === 0)
    return powers[left] ?? 0;
  return max * 0.7 + total / count * 0.3;
}

function formatSpectrumTickHz(value: number) {
  return `${(value / 1_000_000).toFixed(3)} MHz`;
}

function drawReportStats(ctx: CanvasRenderingContext2D, stats: string[][], x: number, y: number, width: number) {
  const gap = 10;
  const columns = 3;
  const cardWidth = (width - gap * (columns - 1)) / columns;
  const cardHeight = 46;
  stats.forEach((item, index) => {
    const left = x + (index % columns) * (cardWidth + gap);
    const top = y + Math.floor(index / columns) * (cardHeight + gap);
    ctx.fillStyle = "#171b20";
    ctx.strokeStyle = "#30363d";
    ctx.lineWidth = 1;
    ctx.fillRect(left, top, cardWidth, cardHeight);
    ctx.strokeRect(left, top, cardWidth, cardHeight);
    ctx.fillStyle = "#9faab5";
    ctx.font = "12px Segoe UI, Arial, sans-serif";
    ctx.fillText(item[0] ?? "", left + 10, top + 17);
    ctx.fillStyle = "#e9ecef";
    ctx.font = "700 14px Segoe UI, Arial, sans-serif";
    ctx.fillText(fitCanvasText(ctx, item[1] ?? "", cardWidth - 20), left + 10, top + 36);
  });
}

function drawReportImage(ctx: CanvasRenderingContext2D, title: string, source: HTMLCanvasElement, x: number, y: number, width: number, height: number) {
  ctx.fillStyle = "#e9ecef";
  ctx.font = "700 18px Segoe UI, Arial, sans-serif";
  ctx.fillText(title, x, y + 18);
  y += 28;
  ctx.fillStyle = "#171a1d";
  ctx.fillRect(x, y, width, height);
  ctx.drawImage(source, x, y, width, height);
  ctx.strokeStyle = "#30363d";
  ctx.lineWidth = 1;
  ctx.strokeRect(x, y, width, height);
  return y + height + 28;
}

function drawReportTable(ctx: CanvasRenderingContext2D, title: string, headers: string[], rows: string[][], x: number, y: number, width: number) {
  const rowHeight = reportTableRowHeight();
  const headerHeight = 30;
  const columns = [0.17, 0.13, 0.13, 0.16, 0.41];
  const columnWidths = columns.map(value => value * width);
  ctx.fillStyle = "#e9ecef";
  ctx.font = "700 18px Segoe UI, Arial, sans-serif";
  ctx.fillText(title, x, y + 18);
  y += 28;
  let left = x;
  ctx.font = "700 12px Segoe UI, Arial, sans-serif";
  for (let index = 0; index < headers.length; index++) {
    const columnWidth = columnWidths[index] ?? width / headers.length;
    ctx.fillStyle = "#20252a";
    ctx.strokeStyle = "#30363d";
    ctx.fillRect(left, y, columnWidth, headerHeight);
    ctx.strokeRect(left, y, columnWidth, headerHeight);
    ctx.fillStyle = getComputedStyle(document.documentElement).getPropertyValue("--accent-strong").trim() || "#d7ecff";
    ctx.fillText(headers[index] ?? "", left + 8, y + 20);
    left += columnWidth;
  }
  y += headerHeight;
  ctx.font = "12px Segoe UI, Arial, sans-serif";
  for (const row of rows) {
    left = x;
    for (let index = 0; index < headers.length; index++) {
      const columnWidth = columnWidths[index] ?? width / headers.length;
      ctx.fillStyle = "#171b20";
      ctx.strokeStyle = "#30363d";
      ctx.fillRect(left, y, columnWidth, rowHeight);
      ctx.strokeRect(left, y, columnWidth, rowHeight);
      ctx.fillStyle = "#d7dde3";
      drawReportCellText(ctx, String(row[index] ?? ""), left + 8, y + 18, columnWidth - 16, 3);
      left += columnWidth;
    }
    y += rowHeight;
  }
  return y + 28;
}

function reportTableRowHeight() {
  return 72;
}

function reportTableHeight(rowCount: number) {
  return 28 + 30 + Math.max(1, rowCount) * reportTableRowHeight() + 28;
}

function drawReportCellText(ctx: CanvasRenderingContext2D, text: string, x: number, y: number, maxWidth: number, maxLines: number) {
  const words = text.split(/\s+/).filter(Boolean);
  const lines: string[] = [];
  let line = "";
  for (const word of words) {
    const candidate = line ? `${line} ${word}` : word;
    if (ctx.measureText(candidate).width <= maxWidth) {
      line = candidate;
      continue;
    }
    if (line)
      lines.push(line);
    line = word;
    if (lines.length >= maxLines)
      break;
  }
  if (line && lines.length < maxLines)
    lines.push(line);
  for (let index = 0; index < lines.length; index++) {
    const value = index === maxLines - 1 && words.join(" ").length > lines.join(" ").length
      ? fitCanvasText(ctx, lines[index], maxWidth)
      : lines[index];
    ctx.fillText(value, x, y + index * 16);
  }
}

function fitCanvasText(ctx: CanvasRenderingContext2D, text: string, maxWidth: number) {
  if (ctx.measureText(text).width <= maxWidth)
    return text;
  let value = text;
  while (value.length > 1 && ctx.measureText(`${value}...`).width > maxWidth)
    value = value.slice(0, -1);
  return `${value}...`;
}

function drawWaterfallFrame(canvas: HTMLCanvasElement | null, powers: number[], scale: SpectrumDisplayScale) {
  if (!canvas || powers.length === 0) return;
  const ctx = canvas.getContext("2d");
  if (!ctx) return;
  const pixelRatio = typeof window === "undefined" ? 1 : Math.max(1, Math.min(2, window.devicePixelRatio || 1));
  const displayWidth = Math.ceil(canvas.getBoundingClientRect().width * pixelRatio);
  const width = Math.max(4096, powers.length, displayWidth);
  const resized = canvas.width !== width || canvas.height < 120;
  if (canvas.width !== width)
    canvas.width = width;
  if (canvas.height < 120)
    canvas.height = 260;
  if (resized) {
    ctx.fillStyle = "#00356f";
    ctx.fillRect(0, 0, canvas.width, canvas.height);
  }
  const scrollRows = 4;
  ctx.drawImage(canvas, 0, 0, canvas.width, canvas.height - scrollRows, 0, scrollRows, canvas.width, canvas.height - scrollRows);
  const row = ctx.createImageData(canvas.width, scrollRows);
  const finitePowers = powers.filter(Number.isFinite);
  if (finitePowers.length === 0)
    return;
  const low = scale.lowDb;
  const high = Math.max(low + 1, scale.highDb);
  const { margin, plotWidth } = spectrumGeometry(canvas.width, 150);
  const plotStart = Math.round(margin.left);
  const plotEnd = Math.round(margin.left + plotWidth);
  for (let x = 0; x < canvas.width; x++) {
    const inPlot = x >= plotStart && x <= plotEnd;
    const position = inPlot ? (x - plotStart) / Math.max(1, plotEnd - plotStart) * (powers.length - 1) : 0;
    const left = Math.floor(position);
    const right = Math.min(powers.length - 1, left + 1);
    const mix = position - left;
    const value = powers[left] * (1 - mix) + powers[right] * mix;
    const color = inPlot ? waterfallColor((value - low) / Math.max(1, high - low)) : [0, 53, 111];
    for (let y = 0; y < scrollRows; y++) {
      const index = (y * canvas.width + x) * 4;
      row.data[index] = color[0];
      row.data[index + 1] = color[1];
      row.data[index + 2] = color[2];
      row.data[index + 3] = 255;
    }
  }
  ctx.putImageData(row, 0, 0);
}

function waterfallColor(value: number): [number, number, number] {
  const t = Math.max(0, Math.min(1, value));
  if (t < 0.18)
    return [0, 53, 111];
  if (t < 0.45) {
    const p = (t - 0.18) / 0.27;
    return [0, Math.round(70 + p * 120), Math.round(145 + p * 100)];
  }
  if (t < 0.62) {
    const p = (t - 0.45) / 0.17;
    return [Math.round(p * 80), Math.round(190 + p * 35), Math.round(245 - p * 170)];
  }
  if (t < 0.82) {
    const p = (t - 0.62) / 0.2;
    return [Math.round(80 + p * 175), Math.round(225 + p * 5), Math.round(75 - p * 40)];
  }
  const p = (t - 0.82) / 0.18;
  return [255, Math.round(230 - p * 140), Math.round(35 - p * 15)];
}

function StepProgressIndicator({ label }: { label: string }) {
  return <div className="rf-step-progress" role="status" aria-live="polite" aria-label={label}>
    <span className="rf-step-spinner" aria-hidden="true" />
    <span>{label}</span>
    <span className="rf-step-progress-track" aria-hidden="true"><span /></span>
  </div>;
}

function SourcePlannerStep({
  profile,
  experiments,
  selectedSources,
  setSelectedSources,
  sourcePlanSystems,
  setSourcePlanSystems,
  sourcePlanMode,
  setSourcePlanMode,
  sourceAssignments,
  setSourceAssignments,
  setSdrSources,
  onSdrTouched,
  showSampleRateControl = true,
  onPlanApplied,
  onPlanSelected
}: {
  profile: RfSurveyProfile;
  experiments: RfSurveyExperiment[];
  selectedSources: number[];
  setSelectedSources: React.Dispatch<React.SetStateAction<number[]>>;
  sourcePlanSystems: string[];
  setSourcePlanSystems: React.Dispatch<React.SetStateAction<string[]>>;
  sourcePlanMode: "full" | "control";
  setSourcePlanMode: React.Dispatch<React.SetStateAction<"full" | "control">>;
  sourceAssignments: Record<string, number>;
  setSourceAssignments: React.Dispatch<React.SetStateAction<Record<string, number>>>;
  setSdrSources: React.Dispatch<React.SetStateAction<RfSurveySource[] | null>>;
  onSdrTouched: () => void;
  showSampleRateControl?: boolean;
  onPlanApplied?: (plan: { sourcePlanSystemShortNames: string[]; sourcePlanMode: "full" | "control"; selectedSourceIndexes: number[]; sourceAssignments: Record<string, number>; sources: RfSurveySource[] }) => void;
  onPlanSelected: () => void;
}) {
  const supportedRateOptions = useMemo(() => sourcePlannerSupportedSampleRates(profile, experiments), [profile.sources, experiments]);
  const validRates = profile.sources.map(source => source.sampleRate).filter(rate => isValidTrSourcePlannerSampleRate(rate));
  const defaultRate = supportedRateOptions.length ? Math.max(...supportedRateOptions) : Math.max(...validRates, 2_400_000);
  const [sampleRateMhz, setSampleRateMhz] = useState((defaultRate / 1_000_000).toFixed(3).replace(/0+$/, "").replace(/\.$/, ""));
  const parsedSampleRateHz = Math.round(Number(sampleRateMhz) * 1_000_000);
  const rateValidation = validateSourcePlannerSampleRate(parsedSampleRateHz, supportedRateOptions);
  const effectiveSampleRateHz = rateValidation.ok ? parsedSampleRateHz : defaultRate;
  const plannerSystems = useMemo(() => systemsWithValidatedControlChannels(profile.systems ?? [], experiments), [profile.systems, experiments]);
  const validationSummaries = useMemo(() => sourcePlanValidationSummaries(profile.systems ?? [], plannerSystems), [profile.systems, plannerSystems]);
  const allSystemNames = useMemo(() => plannerSystems.map(system => system.shortName), [plannerSystems]);
  const activeSourcePlanSystems = sourcePlanSystems.length ? sourcePlanSystems.filter(name => allSystemNames.includes(name)) : allSystemNames;
  const [customSystems, setCustomSystems] = useState<string[]>([]);
  const [customMode, setCustomMode] = useState<"full" | "control">("full");
  const customSeedKey = allSystemNames.join("|");
  const customSeedRef = useRef("");
  const effectiveCustomSystems = customSystems.filter(name => allSystemNames.includes(name));
  const alternatives = useMemo(() => buildSourcePlanAlternatives(plannerSystems, effectiveSampleRateHz, profile.sources.length), [plannerSystems, effectiveSampleRateHz, profile.sources.length]);
  const currentCustom = useMemo(() => buildCustomSourcePlanOption(plannerSystems, effectiveCustomSystems, customMode, effectiveSampleRateHz, profile.sources.length), [plannerSystems, effectiveCustomSystems.join("|"), customMode, effectiveSampleRateHz, profile.sources.length]);
  const activePlan = useMemo(() => buildCustomSourcePlanOption(plannerSystems, activeSourcePlanSystems, sourcePlanMode, effectiveSampleRateHz, profile.sources.length), [plannerSystems, activeSourcePlanSystems.join("|"), sourcePlanMode, effectiveSampleRateHz, profile.sources.length]);
  const normalizedAssignments = useMemo(() => normalizeSourceAssignmentsForUi(sourceAssignments, plannerSystems, profile.sources), [JSON.stringify(sourceAssignments), plannerSystems.map(system => system.shortName).join("|"), profile.sources.map(source => source.index).join("|")]);
  useEffect(() => {
    if (!customSeedKey || customSeedRef.current === customSeedKey) return;
    customSeedRef.current = customSeedKey;
    setCustomSystems(activeSourcePlanSystems);
    setCustomMode(sourcePlanMode);
  }, [customSeedKey]);
  const applySourcePlanSystems = (systems: string[], mode: "full" | "control" = sourcePlanMode) => {
    onSdrTouched();
    const next = systems.filter(name => allSystemNames.includes(name));
    setSourcePlanSystems(next.length ? next : allSystemNames);
    setSourcePlanMode(mode);
  };
  const applyOption = (option: SourcePlanOption) => {
    if (!option.fits) return;
    applySourcePlanSystems(option.systems, option.mode);
    const nextSources = rateValidation.ok
      ? profile.sources.map(source => ({ ...source, sampleRate: effectiveSampleRateHz }))
      : profile.sources;
    const nextSelected = profile.sources.slice(0, Math.max(1, option.windows.length)).map(source => source.index);
    if (rateValidation.ok)
      setSdrSources(nextSources);
    setSelectedSources(nextSelected);
    onPlanApplied?.({
      sourcePlanSystemShortNames: option.systems,
      sourcePlanMode: option.mode,
      selectedSourceIndexes: nextSelected,
      sourceAssignments: normalizedAssignments,
      sources: nextSources
    });
    onPlanSelected();
  };
  const updateSampleRate = (value: string) => {
    setSampleRateMhz(value);
    const hz = Math.round(Number(value) * 1_000_000);
    if (!validateSourcePlannerSampleRate(hz, supportedRateOptions).ok) return;
    onSdrTouched();
    setSdrSources(profile.sources.map(source => ({ ...source, sampleRate: hz })));
  };
  const reviewAssignedPlan = () => {
    const assignedSources = Object.values(normalizedAssignments).filter(value => Number.isFinite(value));
    const nextSelected = Array.from(new Set([...selectedSources, ...assignedSources])).filter(index => profile.sources.some(source => source.index === index)).sort((a, b) => a - b);
    const nextSources = rateValidation.ok
      ? profile.sources.map(source => ({ ...source, sampleRate: effectiveSampleRateHz }))
      : profile.sources;
    if (rateValidation.ok)
      setSdrSources(nextSources);
    setSelectedSources(nextSelected.length ? nextSelected : selectedSources);
    onPlanApplied?.({
      sourcePlanSystemShortNames: activeSourcePlanSystems,
      sourcePlanMode,
      selectedSourceIndexes: nextSelected.length ? nextSelected : selectedSources,
      sourceAssignments: normalizedAssignments,
      sources: nextSources
    });
    onPlanSelected();
  };
  return <div className="rf-step-stack">
    <div className="rf-source-planner-plain">
      {showSampleRateControl && <div className="rf-source-calculator">
        <label>Sample rate (MHz)<input className={rateValidation.ok ? "" : "invalid"} inputMode="decimal" value={sampleRateMhz} onChange={event => updateSampleRate(event.target.value)} /></label>
        <div className={rateValidation.ok ? "setup-note" : "settings-message error"}>
          {rateValidation.ok
            ? `Planning uses RF Sweep validated control channels when available and TR usable bandwidth of ${formatHz(trUsableSpanHz(effectiveSampleRateHz))}.`
            : rateValidation.message}
          {supportedRateOptions.length > 0 && <small>Supported by detected SDR inventory: {supportedRateOptions.map((rate: number) => (rate / 1_000_000).toFixed(3).replace(/0+$/, "").replace(/\.$/, "")).join(", ")} MHz.</small>}
        </div>
      </div>}
      <SelectedSourceCenterTable
        sources={profile.sources}
        selectedSources={selectedSources}
        plan={activePlan}
        sampleRateHz={effectiveSampleRateHz}
      />
      <SiteSourceAssignmentTable
        systems={plannerSystems}
        sources={profile.sources}
        selectedSystems={activeSourcePlanSystems}
        assignments={normalizedAssignments}
        setAssignments={setSourceAssignments}
        onReview={reviewAssignedPlan}
      />
      <div className="rf-source-validation-summary">
        {validationSummaries.map(row => <div key={row.shortName} className={row.validated.length ? "" : "warning"}>
          <strong>{row.label}</strong>
          <span>Used for planning: {row.validated.length ? row.validated.map(formatRfHz).join(", ") : "no validated CC"}</span>
          <small>{row.excluded.length ? `Excluded: ${row.excluded.map(formatRfHz).join(", ")}` : "No other CCs excluded."}</small>
        </div>)}
      </div>
      <SourcePlanOptionsTable
        alternatives={alternatives}
        custom={currentCustom}
        systems={plannerSystems}
        selectedSystems={activeSourcePlanSystems}
        selectedMode={sourcePlanMode}
        customSystems={effectiveCustomSystems}
        customMode={customMode}
        onCustomSystems={setCustomSystems}
        onCustomMode={setCustomMode}
        detectedSources={profile.sources.length}
        onApply={applyOption}
      />
    </div>
  </div>;
}

function normalizeSourceAssignmentsForUi(assignments: Record<string, number> | undefined, systems: RfSurveySystem[], sources: RfSurveySource[]) {
  const validSystems = new Set(systems.map(system => system.shortName.toLowerCase()));
  const validSources = new Set(sources.map(source => source.index));
  const normalized: Record<string, number> = {};
  for (const [key, value] of Object.entries(assignments ?? {})) {
    const system = systems.find(row => row.shortName.toLowerCase() === key.toLowerCase());
    const sourceIndex = Number(value);
    if (!system || !validSystems.has(system.shortName.toLowerCase()) || !validSources.has(sourceIndex))
      continue;
    normalized[system.shortName] = sourceIndex;
  }
  return normalized;
}

function SiteSourceAssignmentTable({
  systems,
  sources,
  selectedSystems,
  assignments,
  setAssignments,
  onReview
}: {
  systems: RfSurveySystem[];
  sources: RfSurveySource[];
  selectedSystems: string[];
  assignments: Record<string, number>;
  setAssignments: React.Dispatch<React.SetStateAction<Record<string, number>>>;
  onReview: () => void;
}) {
  const activeSystems = systems.filter(system => selectedSystems.includes(system.shortName));
  const assign = (systemShortName: string, value: string) => {
    setAssignments(current => {
      const next = normalizeSourceAssignmentsForUi(current, systems, sources);
      if (value === "") {
        delete next[systemShortName];
        return next;
      }
      const sourceIndex = Number(value);
      return Number.isFinite(sourceIndex)
        ? { ...next, [systemShortName]: sourceIndex }
        : next;
    });
  };
  if (activeSystems.length === 0 || sources.length === 0)
    return null;
  return <div className="rf-selected-source-table">
    <div className="rf-selected-source-row header">
      <span>Site</span>
      <span>Control channels</span>
      <span>Assigned source</span>
      <span>Source device</span>
      <span>Action</span>
    </div>
    {activeSystems.map(system => {
      const assigned = assignments[system.shortName];
      const source = sources.find(row => row.index === assigned);
      return <div className="rf-selected-source-row" key={system.shortName}>
        <span><strong>{system.siteLabel || system.shortName}</strong></span>
        <span>{system.controlChannelsHz.length ? system.controlChannelsHz.map(formatRfHz).join(", ") : "--"}</span>
        <span><select value={assigned ?? ""} onChange={event => assign(system.shortName, event.target.value)}>
          <option value="">Auto</option>
          {sources.map(row => <option value={row.index} key={row.index}>Source {row.index}</option>)}
        </select></span>
        <span>{source ? `${source.sdrType || "SDR"} ${source.serial || source.device || source.index}` : "Planner chooses"}</span>
        <span>{assigned == null ? "Auto" : `Source ${assigned}`}</span>
      </div>;
    })}
    <div className="rf-selected-source-row">
      <span><strong>Review</strong></span>
      <span>{Object.keys(assignments).length} assignment{Object.keys(assignments).length === 1 ? "" : "s"}</span>
      <span>--</span>
      <span>--</span>
      <span><button type="button" className="primary" onClick={onReview}>Review Assigned Plan</button></span>
    </div>
  </div>;
}

function SelectedSourceCenterTable({ sources, selectedSources, plan, sampleRateHz }: { sources: RfSurveySource[]; selectedSources: number[]; plan: SourcePlanOption; sampleRateHz: number }) {
  const activeIndexes = selectedSources.length ? selectedSources : sources.map(source => source.index);
  const rows = activeIndexes
    .map((sourceIndex, index) => {
      const source = sources.find(row => row.index === sourceIndex);
      const window = plan.windows[Math.min(index, Math.max(0, plan.windows.length - 1))];
      return { sourceIndex, source, window };
    })
    .filter(row => row.source);
  if (rows.length === 0)
    return null;
  return <div className="rf-selected-source-table">
    <div className="rf-selected-source-row header">
      <span>Source</span>
      <span>Device</span>
      <span>Calculated center</span>
      <span>Usable window</span>
      <span>Rate</span>
    </div>
    {rows.map(row => <div className="rf-selected-source-row" key={row.sourceIndex}>
      <span><strong>Source {row.sourceIndex}</strong></span>
      <span>{row.source?.serial || row.source?.device || row.source?.sdrType || "--"}</span>
      <span><code>{row.window ? formatRfHz(row.window.centerHz) : "--"}</code></span>
      <span>{row.window ? `${formatRfHz(row.window.lowHz)}-${formatRfHz(row.window.highHz)}` : "--"}</span>
      <span>{formatHz(sampleRateHz)}</span>
    </div>)}
  </div>;
}

function uniqueSortedFrequencies(values: number[]) {
  return [...new Set(values.filter(value => Number.isFinite(value) && value > 0).map(value => Math.round(value)))].sort((a, b) => a - b);
}

function uniqueNonNegativeIntegers(values: Array<number | undefined>) {
  return [...new Set(values
    .filter((value): value is number => typeof value === "number" && Number.isFinite(value) && value >= 0)
    .map(value => Math.round(value)))]
    .sort((a, b) => a - b);
}

function uniqueCaseInsensitive(values: string[]) {
  const seen = new Set<string>();
  const result: string[] = [];
  for (const value of values) {
    const trimmed = value.trim();
    const key = trimmed.toLowerCase();
    if (!trimmed || seen.has(key))
      continue;
    seen.add(key);
    result.push(trimmed);
  }
  return result;
}

function surveySystemDefinitionsSignature(systems: RfSurveySystem[]) {
  return systems
    .map(system => ({
      shortName: (system.shortName ?? "").trim().toLowerCase(),
      siteLabel: (system.siteLabel ?? "").trim(),
      controlChannelsHz: uniqueSortedFrequencies(system.controlChannelsHz ?? []),
      voiceFrequenciesHz: uniqueSortedFrequencies(system.voiceFrequenciesHz ?? [])
    }))
    .sort((left, right) => left.shortName.localeCompare(right.shortName))
    .map(system => `${system.shortName}:${system.siteLabel}:${system.controlChannelsHz.join(",")}:${system.voiceFrequenciesHz.join(",")}`)
    .join("|");
}

function stringEqualsIgnoreCase(left: string | undefined | null, right: string | undefined | null) {
  return (left ?? "").trim().toLowerCase() === (right ?? "").trim().toLowerCase();
}

function isValidTrSourcePlannerSampleRate(sampleRateHz: number) {
  return Number.isFinite(sampleRateHz) && sampleRateHz > 0;
}

function formatMhzInput(sampleRateHz: number) {
  return (sampleRateHz / 1_000_000).toFixed(6).replace(/0+$/, "").replace(/\.$/, "");
}

function validateRfSweepSampleRate(sampleRateHz: number) {
  if (!Number.isFinite(sampleRateHz) || sampleRateHz <= 0)
    return "Enter a sample rate in MHz.";
  return "";
}

function validateSourcePlannerSampleRate(sampleRateHz: number, _supportedRates: number[]) {
  if (!Number.isFinite(sampleRateHz) || sampleRateHz <= 0)
    return { ok: false, message: "Enter a sample rate in MHz." };
  return { ok: true, message: "" };
}

function sourcePlannerSupportedSampleRates(profile: RfSurveyProfile, experiments: RfSurveyExperiment[]): number[] {
  const latest = [...experiments]
    .filter(experiment => experiment.type === "sdr_inventory")
    .sort((a, b) => a.createdAtUtc.localeCompare(b.createdAtUtc))
    .at(-1);
  const evidence = latest ? parseExperimentJson<any>(latest.evidenceJson) : null;
  const devices = Array.isArray(evidence?.detection?.devices) ? evidence.detection.devices : [];
  const selectedIndexes = profile.selectedSourceIndexes.length ? profile.selectedSourceIndexes : profile.sources.map(source => source.index);
  const selectedSources = profile.sources.filter(source => selectedIndexes.includes(source.index));
  const rateSets: number[][] = selectedSources
    .map(source => {
      const device = devices.find((row: any) =>
        Number(row.index) === source.index ||
        (source.serial && String(row.serial || "").trim() === source.serial) ||
        (source.device && String(row.deviceArgs || "").trim() === source.device));
      const options: number[] = Array.isArray(device?.sampleRateOptions) ? device.sampleRateOptions.map(Number).filter(isValidTrSourcePlannerSampleRate) : [];
      return options.length > 0 ? options : [];
    })
    .filter(options => options.length > 0);
  if (rateSets.length === 0)
    return [];
  return rateSets
    .reduce((shared: number[], options: number[]) => shared.filter((rate: number) => options.includes(rate)))
    .sort((a: number, b: number) => a - b);
}

function sameStringSet(left: string[], right: string[]) {
  const a = left.map(value => value.toLowerCase()).sort();
  const b = right.map(value => value.toLowerCase()).sort();
  return a.length === b.length && a.every((value, index) => value === b[index]);
}

function trUsableSpanHz(sampleRateHz: number) {
  return Math.max(1, Math.floor(Math.max(0, sampleRateHz) * 0.46875) * 2);
}

function buildSuggestedSourcePlan(frequencies: number[], sampleRateHz: number, priorityFrequencies: number[] = []) {
  const span = trUsableSpanHz(sampleRateHz);
  const halfSpan = Math.max(1, Math.floor(span / 2));
  const priorities = uniqueSortedFrequencies(priorityFrequencies);
  const rows: { lowHz: number; centerHz: number; highHz: number; count: number }[] = [];
  let index = 0;
  while (index < frequencies.length) {
    const start = frequencies[index];
    let endIndex = index;
    while (endIndex + 1 < frequencies.length && frequencies[endIndex + 1] - start <= span) endIndex += 1;
    const end = frequencies[endIndex];
    const midpoint = Math.round((start + end) / 2);
    const minCenter = end - halfSpan;
    const maxCenter = start + halfSpan;
    const priority = priorities
      .filter(value => value >= start && value <= end)
      .sort((left, right) => Math.abs(left - midpoint) - Math.abs(right - midpoint))[0];
    const center = minCenter > maxCenter
      ? midpoint
      : Math.min(maxCenter, Math.max(minCenter, priority || midpoint));
    rows.push({ lowHz: Math.round(center - halfSpan), centerHz: center, highHz: Math.round(center + halfSpan), count: endIndex - index + 1 });
    index = endIndex + 1;
  }
  return rows;
}

function systemPlanFrequencies(system: RfSurveySystem) {
  return uniqueSortedFrequencies([...system.controlChannelsHz, ...system.voiceFrequenciesHz]);
}

type SourcePlanOption = {
  id: string;
  label: string;
  mode: "full" | "control";
  systems: string[];
  siteLabels: string[];
  frequencies: number[];
  windows: { lowHz: number; centerHz: number; highHz: number; count: number }[];
  frequencyCount: number;
  fits: boolean;
  reason: string;
};

function buildSourcePlanOption(label: string, id: string, systems: RfSurveySystem[], selectedNames: string[], mode: "full" | "control", sampleRateHz: number, availableSources: number): SourcePlanOption {
  const included = systems.filter(system => selectedNames.includes(system.shortName));
  const frequencies = uniqueSortedFrequencies(included.flatMap(system => mode === "control" ? system.controlChannelsHz : systemPlanFrequencies(system)));
  const priorityFrequencies = mode === "control" ? uniqueSortedFrequencies(included.flatMap(system => system.controlChannelsHz)) : [];
  const windows = buildSuggestedSourcePlan(frequencies, sampleRateHz, priorityFrequencies);
  const missingCc = included.filter(system => system.controlChannelsHz.length === 0).map(system => system.siteLabel || system.shortName);
  const reason = missingCc.length > 0
    ? `No RF-validated control channel for ${missingCc.join(", ")}.`
    : windows.length > availableSources
      ? `Needs ${windows.length} SDR source windows; ${availableSources} detected.`
      : mode === "control" ? "Fits CC coverage; voice coverage may be incomplete." : "Fits detected SDR hardware.";
  return {
    id,
    label,
    mode,
    systems: selectedNames,
    siteLabels: included.map(system => system.siteLabel || system.shortName),
    frequencies,
    windows,
    frequencyCount: frequencies.length,
    fits: missingCc.length === 0 && windows.length > 0 && windows.length <= availableSources,
    reason
  };
}

function buildSourcePlanAlternatives(systems: RfSurveySystem[], sampleRateHz: number, availableSources: number): SourcePlanOption[] {
  const all = systems.map(system => system.shortName);
  const combos: string[][] = [];
  const addCombo = (values: string[]) => {
    const clean = values.filter(Boolean);
    if (clean.length && !combos.some(existing => sameStringSet(existing, clean)))
      combos.push(clean);
  };
  addCombo(all);
  for (const system of systems)
    addCombo([system.shortName]);
  for (let i = 0; i < systems.length; i += 1)
    for (let j = i + 1; j < systems.length; j += 1)
      addCombo([systems[i].shortName, systems[j].shortName]);

  const rows = combos.map(combo => {
    const included = systems.filter(system => combo.includes(system.shortName));
    const siteLabels = included.map(system => system.siteLabel || system.shortName);
    const full = combo.length === systems.length;
    const single = combo.length === 1;
    return buildSourcePlanOption(
      full ? "All selected sites" : single ? `Focus: ${siteLabels[0]}` : `Prioritize ${combo.length} sites`,
      combo.join("|"),
      systems,
      combo,
      "full",
      sampleRateHz,
      availableSources);
  });
  for (const combo of combos.filter(combo => combo.length > 1)) {
    const control = buildSourcePlanOption(`CC coverage: ${combo.length} sites`, `control-${combo.join("|")}`, systems, combo, "control", sampleRateHz, availableSources);
    rows.push(control);
  }
  const bestFit = rows
    .filter(row => row.fits)
    .sort((a, b) => b.systems.length - a.systems.length || b.frequencyCount - a.frequencyCount || a.windows.length - b.windows.length)[0];
  if (bestFit && bestFit.systems.length < systems.length)
    return rows.map(row => row.id === bestFit.id ? { ...row, label: "Broadest fit now" } : row);
  return rows.filter((row, index, list) => list.findIndex(other => other.id === row.id) === index);
}

function buildCustomSourcePlanOption(systems: RfSurveySystem[], selectedNames: string[], mode: "full" | "control", sampleRateHz: number, availableSources: number) {
  return buildSourcePlanOption("Custom", "custom", systems, selectedNames, mode, sampleRateHz, availableSources);
}

function systemsWithValidatedControlChannels(systems: RfSurveySystem[], experiments: RfSurveyExperiment[]) {
  const latest = [...experiments]
    .filter(experiment => experiment.type === "rf_validation_sweep")
    .sort((a, b) => a.createdAtUtc.localeCompare(b.createdAtUtc))
    .at(-1);
  if (!latest) return systems;
  const candidates = rfValidationCandidates(latest);
  if (!candidates.length) return systems;
  const groups = rfValidationSiteReadiness(candidates, systems);
  const bestBySystem = new Map(groups.map(group => [group.key, group.best]));
  const hadCandidates = new Set(groups.filter(group => group.candidates.length > 0).map(group => group.key));
  return systems.map(system => {
    const best = bestBySystem.get(system.shortName);
    if (best && rfValidationCandidatePassed(best)) {
      const cc = Math.round(Number(best.controlChannelHz));
      return { ...system, controlChannelsHz: cc > 0 ? [cc] : [] };
    }
    return hadCandidates.has(system.shortName)
      ? { ...system, controlChannelsHz: [] }
      : system;
  });
}

function sourcePlanValidationSummaries(originalSystems: RfSurveySystem[], plannerSystems: RfSurveySystem[]) {
  return originalSystems.map(system => {
    const planner = plannerSystems.find(row => row.shortName === system.shortName);
    const validated = uniqueSortedFrequencies(planner?.controlChannelsHz ?? []);
    const excluded = uniqueSortedFrequencies(system.controlChannelsHz.filter(freq => !validated.includes(freq)));
    return {
      shortName: system.shortName,
      label: system.siteLabel || system.shortName,
      validated,
      excluded
    };
  });
}

function SourcePlanOptionsTable({
  alternatives,
  custom,
  systems,
  selectedSystems,
  selectedMode,
  customSystems,
  customMode,
  onCustomSystems,
  onCustomMode,
  detectedSources,
  onApply
}: {
  alternatives: SourcePlanOption[];
  custom: SourcePlanOption;
  systems: RfSurveySystem[];
  selectedSystems: string[];
  selectedMode: "full" | "control";
  customSystems: string[];
  customMode: "full" | "control";
  onCustomSystems: React.Dispatch<React.SetStateAction<string[]>>;
  onCustomMode: React.Dispatch<React.SetStateAction<"full" | "control">>;
  detectedSources: number;
  onApply: (option: SourcePlanOption) => void;
}) {
  const selectedKey = selectedSystems.slice().sort().join("|");
  const selectedPlanKey = `${selectedMode}:${selectedKey}`;
  const rows = [...alternatives, custom];
  const selectedPresetId = alternatives.find(option => option.fits && sameStringSet(option.systems, selectedSystems) && option.mode === selectedMode)?.id ?? "";
  const toggleCustomSystem = (shortName: string, checked: boolean) => {
    const next = checked
      ? [...new Set([...customSystems, shortName])]
      : customSystems.filter(name => name !== shortName);
    onCustomSystems(next.length ? next : systems.map(system => system.shortName));
  };
  return <div className="rf-source-plan-table">
    <div className="rf-source-plan-row header">
      <span>Option</span>
      <span>Sites</span>
      <span>Validated CC / frequencies</span>
      <span>Source centers / windows</span>
      <span>Fit</span>
      <span>Select</span>
    </div>
    {rows.map(option => {
      const isCustom = option.id === "custom";
      const selected = !isCustom && option.id === selectedPresetId;
      return <div className={`rf-source-plan-row ${selected ? "selected" : ""} ${option.fits ? "" : "disabled"}`.trim()} key={option.id}>
        <span><strong>{option.label}</strong><small>{option.mode === "control" ? "Validated control channels only" : "Full known site frequencies"}{isCustom ? " / choose sites below" : ""}</small></span>
        <span>
          {isCustom
            ? <span className="rf-source-custom-sites">
              {systems.map(system => <label key={system.shortName}>
                <input type="checkbox" checked={customSystems.includes(system.shortName)} onChange={event => toggleCustomSystem(system.shortName, event.target.checked)} />
                {system.siteLabel || system.shortName}
              </label>)}
              <label>
                <input type="checkbox" checked={customMode === "control"} onChange={event => onCustomMode(event.target.checked ? "control" : "full")} />
                CC coverage only
              </label>
            </span>
            : option.siteLabels.join(", ")}
        </span>
        <span>
          <code>{option.frequencies.filter(freq => systems.some(system => option.systems.includes(system.shortName) && system.controlChannelsHz.includes(freq))).map(formatRfHz).join(", ") || "--"}</code>
          <small>{option.frequencyCount} {option.mode === "control" ? "validated CC" : "total frequency"} entr{option.frequencyCount === 1 ? "y" : "ies"}</small>
        </span>
        <span>{option.windows.length ? option.windows.map((window, index) => <span className="rf-source-window-cell" key={`${option.id}-${index}`}>
          <code>{formatRfHz(window.centerHz)}</code>
          <small>{formatRfHz(window.lowHz)}-{formatRfHz(window.highHz)}</small>
        </span>) : "--"}</span>
        <span className={option.fits ? "section-status ok" : "section-status warning"}>{option.reason}</span>
        <span>{isCustom
          ? <button className="primary" disabled={!option.fits || selectedPlanKey === `${option.mode}:${option.systems.slice().sort().join("|")}`} onClick={() => onApply(option)}>Apply Custom</button>
          : <button className="primary" disabled={!option.fits || selectedPlanKey === `${option.mode}:${option.systems.slice().sort().join("|")}`} onClick={() => onApply(option)}>{selected ? "Selected" : "Select"}</button>}</span>
      </div>;
    })}
    {detectedSources === 0 && <div className="rf-source-plan-row disabled">
      <span>No SDR inventory</span><span>--</span><span>--</span><span>--</span><span className="section-status warning">Run SDR Inventory before source planning.</span><span>--</span>
    </div>}
  </div>;
}

function ExperimentSummary({ experiment, onDetails }: { experiment: RfSurveyExperiment; onDetails?: () => void }) {
  if (experiment.type === "sdr_inventory")
    return <SdrInventorySummary experiment={experiment} onDetails={onDetails} />;
  return <div className={experiment.status === "passed" ? "rf-experiment-summary passed" : experiment.status === "failed" ? "rf-experiment-summary failed" : "rf-experiment-summary"}>
    <strong>{label(experiment.type)}: {label(experiment.status)}</strong>
    <span>{experiment.blockingIssue || experiment.resultSummary || experiment.hypothesis}</span>
    {onDetails && <button onClick={onDetails}>Details</button>}
  </div>;
}

function SdrInventorySummary({ experiment, onDetails }: { experiment: RfSurveyExperiment; onDetails?: () => void }) {
  const evidence = parseExperimentJson<any>(experiment.evidenceJson);
  const detected = Array.isArray(evidence?.detection?.devices) ? evidence.detection.devices : [];
  const configured = Array.isArray(evidence?.configuredSources) ? evidence.configuredSources : [];
  const outputs = Array.isArray(evidence?.outputs) ? evidence.outputs : [];
  const rows = detected.length > 0
    ? detected.map((device: any, index: number) => ({
      key: `${device.type || "sdr"}-${device.serial || index}`,
      title: `${device.type || "SDR"} ${device.serial || device.label || index}`,
      detail: [device.deviceArgs, device.label, device.sampleRateOptions?.length ? `${device.sampleRateOptions.join(", ")} sps` : "", device.warning].filter(Boolean).join(" / ")
    }))
    : configured.map((source: any, index: number) => ({
      key: `${source.sdrType || "source"}-${source.serial || index}`,
      title: `${source.sdrType || "Source"} ${source.serial || source.index}`,
      detail: [source.device, source.sampleRate ? `${source.sampleRate} sps` : "", source.gain ? `${source.gain} gain` : ""].filter(Boolean).join(" / ")
    }));
  return <div className={experiment.status === "passed" ? "rf-inventory-summary passed" : experiment.status === "failed" ? "rf-inventory-summary failed" : "rf-inventory-summary"}>
    <div className="rf-inventory-head">
      <div>
        <strong>SDR Inventory: {label(experiment.status)}</strong>
        <span>{experiment.blockingIssue || experiment.resultSummary || evidence?.detection?.message || experiment.hypothesis}</span>
      </div>
      {onDetails && <button onClick={onDetails}>Details</button>}
    </div>
    <div className="rf-inventory-device-list">
      {rows.map((row: { key: string; title: string; detail: string }) => <div className="rf-inventory-device-row" key={row.key}>
        <strong>{row.title}</strong>
        <span>{row.detail || "--"}</span>
      </div>)}
      {rows.length === 0 && <div className="setup-note">No SDR devices were reported in inventory evidence.</div>}
    </div>
    {outputs.length > 0 && <div className="rf-tool-status-list compact">
      {outputs.map((row: any, index: number) => <div className="rf-tool-row" key={`${row.tool || "tool"}-${index}`}>
        <strong>{row.tool || "tool"}</strong>
        <span className={row.status === "ran" ? "section-status ok" : row.status === "missing" ? "section-status error" : "section-status"}>{label(row.status || "unknown")}</span>
        <span>{summarizeSdrToolOutput(row.output || "")}</span>
      </div>)}
    </div>}
  </div>;
}

function summarizeSdrToolOutput(output: string) {
  const text = String(output || "").trim();
  const serial = /Serial Number:\s*(0x)?([A-Fa-f0-9]+)/.exec(text);
  if (serial) return `Serial ${serial[2]}`;
  const found = /(Found [^\n]+)/i.exec(text);
  if (found) return found[1];
  const noDevice = /(No supported devices found\.)/i.exec(text);
  if (noDevice) return noDevice[1];
  return trimLogText(text, 160).replace(/\s+/g, " ") || "--";
}

function InfoTip({ text }: { text: string }) {
  return <span className="info-tip" tabIndex={0} aria-label={text}>
    <Info size={13} />
    <span>{text}</span>
  </span>;
}

function trimLogText(value: string, max = 3500) {
  value = (value ?? "").trim();
  return value.length > max ? value.slice(0, max).trimEnd() + "\n...[truncated]" : value;
}

function rfValidationCandidates(experiment: RfSurveyExperiment) {
  const evidence = parseExperimentJson<any>(experiment.evidenceJson);
  return Array.isArray(evidence?.candidates) ? evidence.candidates : [];
}

function rfValidationPowerExperiment(experiment: RfSurveyExperiment): RfSurveyExperiment | null {
  const evidence = parseExperimentJson<any>(experiment.evidenceJson);
  if (!evidence?.power?.rows) return null;
  return {
    ...experiment,
    type: "rf_power_scan",
    status: Array.isArray(evidence.power.rows) && evidence.power.rows.some((row: any) => row.status === "measured") ? "passed" : "failed",
    evidenceJson: JSON.stringify(evidence.power),
    interpretationJson: JSON.stringify({ best: evidence.candidates?.[0], selectedControlChannelHz: evidence.selectedControlChannelHz })
  };
}

function rfSweepProgressRowsFromExperiment(experiment?: RfSurveyExperiment): RfSurveySweepProgressRow[] {
  if (!experiment) return [];
  const evidence = parseExperimentJson<any>(experiment.evidenceJson);
  const rows = Array.isArray(evidence?.power?.rows) ? evidence.power.rows : Array.isArray(evidence?.rows) ? evidence.rows : [];
  return rows.map((row: any) => ({
    sourceIndex: Number(row.sourceIndex ?? row.index ?? 0),
    controlChannelHz: Number(row.controlChannelHz ?? 0),
    gain: row.gain == null ? "" : String(row.gain),
    status: String(row.status || ""),
    issue: String(row.issue || ""),
    snrDb: numericOrNull(row.snrDb),
    peakOffsetHz: numericOrNull(row.peakOffsetHz),
    overload: Boolean(row.overload)
  }));
}

function rfSweepCandidateProgressFromExperiment(experiment?: RfSurveyExperiment): RfSurveySweepCandidateProgress[] {
  if (!experiment) return [];
  const evidence = parseExperimentJson<any>(experiment.evidenceJson);
  const candidates = Array.isArray(evidence?.candidates) ? evidence.candidates : [];
  return candidates.map((candidate: any) => ({
    id: String(candidate.id || `${candidate.sourceIndex}-${candidate.controlChannelHz}-${candidate.gain}-${candidate.errorHz}`),
    sourceIndex: Number(candidate.sourceIndex ?? 0),
    controlChannelHz: Number(candidate.controlChannelHz ?? 0),
    gain: candidate.gain == null ? "" : String(candidate.gain),
    errorHz: Number(candidate.errorHz ?? 0),
    p25Status: String(candidate.p25Status || ""),
    p25Summary: String(candidate.p25Summary || ""),
    metricsStatus: String(candidate.metricsStatus || ""),
    metricsSummary: String(candidate.metricsSummary || ""),
    voiceStatus: String(candidate.voiceStatus || ""),
    voiceSummary: String(candidate.voiceSummary || ""),
    voiceTotalCalls: Number(candidate.voiceTotalCalls ?? 0),
    voiceRealCalls: Number(candidate.voiceRealCalls ?? 0)
  }));
}

function numericOrNull(value: unknown) {
  const number = Number(value);
  return Number.isFinite(number) ? number : null;
}

function RfSweepPermutationResults({
  sources,
  controlChannels,
  gains,
  rows,
  candidates,
  active
}: {
  sources: RfSurveySource[];
  controlChannels: number[];
  gains: string[];
  rows: RfSurveySweepProgressRow[];
  candidates: RfSurveySweepCandidateProgress[];
  active: boolean;
}) {
  const [expandedGroups, setExpandedGroups] = useState<Record<string, boolean>>({});
  const resultsByKey = new Map(rows.map(row => [`${row.sourceIndex}|${row.controlChannelHz}|${row.gain}`, row]));
  const candidatesByKey = new Map<string, RfSurveySweepCandidateProgress[]>();
  for (const candidate of candidates) {
    const key = `${candidate.sourceIndex}|${candidate.controlChannelHz}|${candidate.gain}`;
    candidatesByKey.set(key, [...(candidatesByKey.get(key) ?? []), candidate]);
  }
  const groups = sources.flatMap(source =>
    controlChannels.map(controlChannel => ({
      key: `${source.index}|${controlChannel}`,
      source,
      controlChannel,
      rows: gains.map(gain => ({ gain, result: resultsByKey.get(`${source.index}|${controlChannel}|${gain}`), candidates: candidatesByKey.get(`${source.index}|${controlChannel}|${gain}`) ?? [] }))
    }))
  );
  const plannedCount = groups.length * gains.length;
  const measured = rows.filter(row => row.status === "measured").length;
  const failed = rows.filter(row => row.status && row.status !== "measured").length;
  const toggleGroup = (key: string) => setExpandedGroups(current => ({ ...current, [key]: !current[key] }));
  return <div className="rf-sweep-plan-results">
    <div className="rf-sweep-results-summary">
      <span>{measured} measured</span>
      <span>{failed} failed</span>
      <span>{Math.max(0, plannedCount - rows.length)} pending</span>
    </div>
    <div className="rf-sweep-plan-table" role="table" aria-label="RF sweep permutation results">
      <div className="rf-sweep-plan-row header" role="row">
        <span></span>
        <span>Status</span>
        <span>Source</span>
        <span>Control channel</span>
        <span>Gains</span>
        <span>SNR</span>
        <span>Measured signal offset</span>
        <span>Issue</span>
      </div>
      {groups.length === 0 && <div className="rf-sweep-plan-row neutral" role="row">
        <span></span>
        <span className="section-status">Pending</span>
        <span>--</span>
        <code>--</code>
        <span>--</span>
        <span>--</span>
        <span>--</span>
        <span>Select SDR sources and sites before running RF Sweep.</span>
      </div>}
      {groups.map(group => {
        const results = group.rows.map(row => row.result).filter(Boolean) as RfSurveySweepProgressRow[];
        const groupCandidates = group.rows.flatMap(row => row.candidates);
        const groupMeasured = results.filter(row => row.status === "measured");
        const groupFailed = results.filter(row => row.status && row.status !== "measured");
        const pendingCount = Math.max(0, group.rows.length - results.length);
        const best = groupMeasured.slice().sort((a, b) => Number(b.snrDb ?? -999) - Number(a.snrDb ?? -999))[0];
        const groupStage = rfSweepCandidateStage(groupCandidates);
        const status = groupStage.status || (groupMeasured.length > 0 ? "measured" : pendingCount > 0 && active ? "pending" : groupFailed.length > 0 ? "failed" : "not_run");
        const statusTone = rfSweepStatusTone(status, groupStage.label);
        const issue = best?.overload ? "Overload risk" : best?.issue || groupFailed[0]?.issue || (pendingCount > 0 && active ? "Waiting for remaining gains" : "--");
        const expanded = expandedGroups[group.key] === true;
        return <React.Fragment key={group.key}>
          <div className={`rf-sweep-plan-row rf-sweep-plan-group ${rfSweepRowClass(status, groupStage.label)}`} role="row">
            <button type="button" className="icon-button rf-sweep-expand" aria-label={`${expanded ? "Collapse" : "Expand"} ${formatRfHz(group.controlChannel)}`} aria-expanded={expanded} onClick={() => toggleGroup(group.key)}>
              {expanded ? <ChevronDown size={15} /> : <ChevronRight size={15} />}
            </button>
            <span className={`section-status ${statusTone}`.trim()}>{groupStage.label || (status === "not_run" ? "Not run" : status === "measured" ? "RF measured" : label(status))}</span>
            <span>Source {group.source.index}</span>
            <code>{formatRfHz(group.controlChannel)}</code>
            <span>{groupMeasured.length} measured / {groupFailed.length} failed / {pendingCount} pending</span>
            <span>{best?.snrDb == null ? "--" : `${formatFixed(best.snrDb, 1)} dB`}</span>
            <span>{best?.peakOffsetHz == null ? "--" : `${formatFixed(best.peakOffsetHz, 0)} Hz`}</span>
            <span>{issue}</span>
          </div>
          {expanded && group.rows.map(item => {
            const result = item.result;
            const rowStage = rfSweepCandidateStage(item.candidates);
            const rowStatus = rowStage.status || result?.status || (active ? "pending" : "not_run");
            const rowTone = rfSweepStatusTone(rowStatus, rowStage.label);
            const rowIssue = result?.overload ? "Overload risk" : result?.issue || (rowStatus === "pending" ? "Waiting for this gain" : "--");
            return <React.Fragment key={`${group.key}-${item.gain}`}>
              <div className={`rf-sweep-plan-row rf-sweep-plan-detail ${rfSweepRowClass(rowStatus, rowStage.label)}`} role="row">
                <span></span>
                <span className={`section-status ${rowTone}`.trim()}>{rowStage.label || (rowStatus === "not_run" ? "Not run" : rowStatus === "measured" ? "RF measured" : label(rowStatus))}</span>
                <span>Source {group.source.index}</span>
                <code>{formatRfHz(group.controlChannel)}</code>
                <span>{item.gain || "auto"}</span>
                <span>{result?.snrDb == null ? "--" : `${formatFixed(result.snrDb, 1)} dB`}</span>
                <span>{result?.peakOffsetHz == null ? "--" : `${formatFixed(result.peakOffsetHz, 0)} Hz`}</span>
                <span>{rowStage.summary || rowIssue}</span>
              </div>
              {item.candidates.map(candidate => {
                const stage = rfSweepCandidateStage([candidate]);
                return <div className={`rf-sweep-plan-followup ${rfSweepRowClass(stage.status, stage.label)}`} key={candidate.id || `${group.key}-${item.gain}-${candidate.errorHz}`}>
                  <span></span>
                  <span className={`section-status ${rfSweepStatusTone(stage.status, stage.label)}`.trim()}>{stage.label || "P25 probe"}</span>
                  <span>Frequency correction {formatSignedHz(candidate.errorHz)}</span>
                  <span>{stage.status ? label(stage.status) : "pending"}</span>
                  <span>{stage.summary || "Waiting for follow-up result."}</span>
                </div>;
              })}
            </React.Fragment>;
          })}
        </React.Fragment>;
      })}
    </div>
  </div>;
}

function rfSweepCandidateStage(candidates: RfSurveySweepCandidateProgress[]) {
  const ordered = candidates.slice().sort((a, b) => rfSweepStageRank(b) - rfSweepStageRank(a));
  const candidate = ordered[0];
  if (!candidate) return { label: "", status: "", summary: "" };
  if (candidate.voiceStatus) {
    const voiceStatus = candidate.voiceStatus === "failed" && candidate.metricsStatus === "passed" ? "unknown" : candidate.voiceStatus;
    return { label: "Voice trial", status: voiceStatus, summary: candidate.voiceSummary || `${candidate.voiceRealCalls}/${candidate.voiceTotalCalls} real calls with audio.` };
  }
  if (candidate.metricsStatus)
    return { label: "TR scan", status: candidate.metricsStatus, summary: candidate.metricsSummary };
  if (candidate.p25Status)
    return { label: "P25 probe", status: candidate.p25Status, summary: candidate.p25Summary };
  return { label: "P25 probe", status: "pending", summary: "Selected for follow-up; waiting for P25 probe." };
}

function rfSweepStageRank(candidate: RfSurveySweepCandidateProgress) {
  if (candidate.voiceStatus) return 4;
  if (candidate.metricsStatus) return 3;
  if (candidate.p25Status) return 2;
  return 1;
}

function rfSweepStatusTone(status: string, stageLabel = "") {
  const normalized = String(status || "").toLowerCase();
  if (!stageLabel && normalized === "measured") return "";
  if (normalized === "passed") return "ok";
  if (normalized === "running" || normalized === "pending") return "info";
  if (normalized === "blocked" || normalized === "unknown" || normalized === "inconclusive") return "warning";
  if (normalized === "failed") return "error";
  return "";
}

function rfSweepRowClass(status: string, stageLabel = "") {
  const normalized = String(status || "").toLowerCase();
  if (!stageLabel && normalized === "measured") return "neutral";
  if (normalized === "passed") return "measured";
  if (normalized === "running" || normalized === "pending") return "pending";
  if (normalized === "not_run" || normalized === "not run" || normalized === "") return "neutral";
  if (normalized === "blocked" || normalized === "unknown" || normalized === "inconclusive") return "pending";
  return "failed";
}

function RfValidationSummary({ experiment, onDetails }: { experiment: RfSurveyExperiment; onDetails: () => void }) {
  const evidence = parseExperimentJson<any>(experiment.evidenceJson);
  const best = rfValidationCandidates(experiment)[0];
  const tone = experiment.status === "passed" ? "passed" : "failed";
  const applied = !!evidence?.appliedCandidateToLiveTr;
  const siteReadiness = Array.isArray(evidence?.siteReadiness) ? evidence.siteReadiness : [];
  const monitorableSites = siteReadiness.filter((site: any) => !!site.monitorable).length;
  return <div className={`rf-power-summary ${tone}`}>
    <div>
      <strong>{siteReadiness.length ? `${monitorableSites}/${siteReadiness.length} selected site(s) monitorable` : best ? `Best source ${best.sourceIndex}: ${formatFixed(best.snrDb, 1)} dB RF / ${label(best.p25Status || "p25 pending")} / TR ${label(best.metricsStatus || "pending")} / voice ${label(best.voiceStatus || "pending")}` : "RF Sweep complete"}</strong>
      <span>{experiment.blockingIssue || experiment.resultSummary}</span>
    </div>
    <div className={`rf-quality ${experiment.status === "passed" ? "ok" : best?.p25Frames ? "warning" : "error"}`}>
      <strong>{applied ? "Validated and applied" : experiment.status === "passed" ? "Site readiness validated" : best?.p25Frames ? "Partially proven" : "Not validated"}</strong>
      <span>{evidence?.warning || "Review site readiness before source planning."}</span>
    </div>
    <button onClick={onDetails}>Details</button>
  </div>;
}

function rfValidationStageClass(status: unknown) {
  const normalized = String(status || "not_run").toLowerCase();
  if (normalized === "passed" || normalized === "measured")
    return "section-status ok";
  if (normalized === "blocked" || normalized === "inconclusive" || normalized === "voice_inconclusive" || normalized === "unknown")
    return "section-status warning";
  if (normalized === "not_run" || normalized === "not run" || normalized === "")
    return "section-status";
  return "section-status error";
}

function rfValidationCandidateBlocker(candidate: any) {
  if (!candidate) return "";
  if (candidate.voiceStatus === "blocked" && candidate.voiceSummary) return `Voice blocked: ${candidate.voiceSummary}`;
  if (candidate.metricsStatus === "blocked" && candidate.metricsSummary) return `TR metrics blocked: ${candidate.metricsSummary}`;
  if (candidate.p25Status === "failed" && candidate.p25Summary) return `P25 failed: ${candidate.p25Summary}`;
  if (candidate.p25Status === "not_run" && candidate.p25Summary) return candidate.p25Summary;
  if (candidate.metricsStatus === "failed" && candidate.metricsSummary) return `TR metrics failed: ${candidate.metricsSummary}`;
  if (candidate.voiceStatus === "failed" && candidate.voiceSummary) return `Voice failed: ${candidate.voiceSummary}`;
  return "";
}

function rfValidationTechnicalBlocker(experiment: RfSurveyExperiment, candidates: any[]) {
  const evidence = parseExperimentJson<any>(experiment.evidenceJson);
  if (typeof evidence?.technicalBlocker === "string" && evidence.technicalBlocker.trim())
    return evidence.technicalBlocker.trim();
  const powerRows = Array.isArray(evidence?.power?.rows) ? evidence.power.rows : [];
  const powerBlockers = powerRows
    .filter((row: any) => row?.status && row.status !== "measured")
    .map((row: any) => {
      const output = String(row?.output || "").trim().replace(/\s+/g, " ");
      const issue = String(row?.issue || "").trim();
      const detail = output || issue;
      return detail ? `RF screen blocked: Source ${row.index ?? 0}, ${row.controlChannelHz ? formatRfHz(Number(row.controlChannelHz)) : "--"}, gain ${row.gain ?? "auto"}, rate ${row.sampleRate ? Number(row.sampleRate).toLocaleString() : "--"} sps: ${detail}` : "";
    })
    .filter(Boolean)
    .filter((text: string, index: number, all: string[]) => all.findIndex(other => other.toLowerCase() === text.toLowerCase()) === index);
  if (powerBlockers.length)
    return powerBlockers.slice(0, 3).join(" ");
  const blockers = candidates
    .map(candidate => {
      if (String(candidate?.metricsStatus || "").toLowerCase() === "blocked" && candidate?.metricsSummary)
        return `TR metrics blocked: ${candidate.metricsSummary}`;
      if (String(candidate?.voiceStatus || "").toLowerCase() === "blocked" && candidate?.voiceSummary)
        return `Voice blocked: ${candidate.voiceSummary}`;
      if (String(candidate?.p25Status || "").toLowerCase() === "blocked" && candidate?.p25Summary)
        return `P25 blocked: ${candidate.p25Summary}`;
      return "";
    })
    .map(text => text.trim())
    .filter(Boolean)
    .filter((text, index, all) => all.findIndex(other => other.toLowerCase() === text.toLowerCase()) === index);
  return blockers.slice(0, 3).join(" ");
}

function rfValidationCandidateSystem(candidate: any, systems: RfSurveySystem[]) {
  const explicit = String(candidate?.systemShortName || "").trim();
  const byName = explicit ? systems.find(system => system.shortName === explicit) : undefined;
  if (byName) return byName;
  const cc = Number(candidate?.controlChannelHz);
  return systems.find(system => system.controlChannelsHz?.includes(cc));
}

function rfValidationCandidatePassed(candidate: any) {
  return (candidate?.rfStatus === "measured" || Number.isFinite(Number(candidate?.snrDb))) &&
    (candidate?.p25Frames || candidate?.p25Status === "passed") &&
    candidate?.metricsStatus === "passed";
}

function rfValidationCandidateVoiceStatus(candidate: any) {
  if (!candidate) return "not_run";
  if (candidate.voiceStatus === "passed") return "passed";
  if (rfValidationCandidatePassed(candidate)) return "unknown";
  return candidate.voiceStatus || "not_run";
}

function rfValidationEffectiveStatus(experiment?: RfSurveyExperiment, systems: RfSurveySystem[] = []) {
  if (!experiment) return undefined;
  if (experiment.type !== "rf_validation_sweep") return experiment.status;
  if (experiment.status === "passed") return "passed";
  const candidates = rfValidationCandidates(experiment);
  const groups = rfValidationSiteReadiness(candidates, systems);
  if (groups.length > 0 && groups.every(group => group.best && rfValidationCandidatePassed(group.best)))
    return "passed";
  if (groups.some(group => group.best && rfValidationCandidatePassed(group.best)))
    return "partial";
  const evidence = parseExperimentJson<any>(experiment.evidenceJson);
  const siteReadiness = Array.isArray(evidence?.siteReadiness) ? evidence.siteReadiness : [];
  if (siteReadiness.length > 0 && siteReadiness.every((site: any) => site?.monitorable === true))
    return "passed";
  if (siteReadiness.some((site: any) => site?.monitorable === true))
    return "partial";
  return experiment.status;
}

function RfValidationStageSummary({ candidate, experiment, systems = [] }: { candidate: any; experiment: RfSurveyExperiment; systems?: RfSurveySystem[] }) {
  if (!candidate) return null;
  const system = rfValidationCandidateSystem(candidate, systems);
  const stages = [
    { label: "RF screen", value: candidate.rfStatus || "measured", detail: candidate.snrDb == null ? "--" : `${formatFixed(candidate.snrDb, 1)} dB` },
    { label: "P25 probe", value: candidate.p25Frames ? "passed" : candidate.p25Status || "not_run", detail: candidate.p25Frames ? "Frame evidence" : candidate.p25Summary || "" },
    { label: "TR metrics", value: candidate.metricsStatus || "not_run", detail: candidate.metricsSummary || "" },
    { label: "Voice trial", value: rfValidationCandidateVoiceStatus(candidate), detail: candidate.voiceRealCalls ? `${candidate.voiceRealCalls} real call(s)` : candidate.voiceSummary || "" }
  ];
  return <div className="rf-validation-stage-card">
    <div>
      <strong>{experiment.status === "passed" ? "Validated Candidate" : "Current Best Candidate"}</strong>
      <span>{system?.siteLabel || system?.shortName || candidate.systemShortName || "Unknown site"} / Source {candidate.sourceIndex}, {candidate.controlChannelHz ? formatRfHz(Number(candidate.controlChannelHz)) : "--"}, gain {candidate.gain ?? "auto"}, frequency correction {formatSignedHz(Number(candidate.errorHz ?? 0))}</span>
    </div>
    <div className="rf-validation-stage-strip">
      {stages.map(stage => <div className="rf-validation-stage" key={stage.label}>
        <span>{stage.label}</span>
        <strong className={rfValidationStageClass(stage.value)}>{label(stage.value || "not run")}</strong>
        <small>{trimLogText(stage.detail || "--", 150)}</small>
      </div>)}
    </div>
  </div>;
}

function RfValidationRecommendations({
  experiment,
  systems = [],
  busy = false,
  onRefreshSite
}: {
  experiment: RfSurveyExperiment;
  systems?: RfSurveySystem[];
  busy?: boolean;
  onRefreshSite?: (system: RfSurveySystem) => void | Promise<void>;
}) {
  const candidates = rfValidationCandidates(experiment);
  const siteGroups = rfValidationSiteReadiness(candidates, systems);
  const technicalBlocker = rfValidationTechnicalBlocker(experiment, candidates);
  if (!siteGroups.length) return <div className="rf-validation-results">
    <div className="setup-job-head">
      <strong>Site Readiness by Selected Site</strong>
      <span className="muted">RF Sweep did not reach site candidate ranking.</span>
    </div>
    {technicalBlocker
      ? <div className="rf-site-readiness-blocker" role="status"><strong>Blocked</strong><span>{technicalBlocker}</span></div>
      : <div className="settings-message error">{experiment.blockingIssue || experiment.resultSummary || "No site-readiness candidates were produced."}</div>}
  </div>;
  return <div className="rf-validation-results">
    <div className="setup-job-head">
      <strong>Site Readiness by Selected Site</strong>
      <span className="muted">RF Sweep checks each selected site one candidate at a time. Config Draft uses this evidence to decide which sites can be planned for monitoring.</span>
    </div>
    {technicalBlocker && <div className="rf-site-readiness-blocker" role="status">
      <strong>Blocked</strong>
      <span>{technicalBlocker}</span>
    </div>}
    <div className="rf-site-readiness-list">
      {siteGroups.map(group => {
        const best = group.best;
        const passed = best && rfValidationCandidatePassed(best);
        return <div className={`rf-site-readiness-card ${passed ? "passed" : "failed"}`} key={group.key}>
          <div className="rf-site-readiness-head">
            <div>
              <strong>{group.label}</strong>
              <span>{passed ? `Best usable CC: ${formatRfHz(Number(best.controlChannelHz))}` : "No fully usable control channel proven yet"}</span>
            </div>
            <div className="rf-site-readiness-actions">
              <span className={`section-status ${passed ? "ok" : group.hasP25 ? "warning" : "error"}`}>{passed ? "Monitorable" : group.hasP25 ? "Partial" : "Not proven"}</span>
              {group.system && onRefreshSite && <button
                type="button"
                className="icon-button"
                disabled={busy}
                title={`Rerun RF Sweep for ${group.label}`}
                aria-label={`Rerun RF Sweep for ${group.label}`}
                onClick={() => void onRefreshSite(group.system!)}
              ><RefreshCw size={15} /></button>}
            </div>
          </div>
          {best && <div className="rf-site-best">
            <span>Best evidence</span>
            <code>{formatRfHz(Number(best.controlChannelHz))} / source {best.sourceIndex} / gain {best.gain ?? "auto"} / frequency correction {formatSignedHz(Number(best.errorHz ?? 0))}</code>
            <span>{rfValidationCandidateOutcome(best)}</span>
          </div>}
          <div className="rf-site-readiness-table">
            <div className="rf-site-readiness-row header"><span>Control channel</span><span>RF</span><span>P25</span><span>TR scan</span><span>Voice</span><span>Best candidate</span><span>Outcome</span></div>
            {group.controlChannels.map(row => {
              const candidate = row.best;
              return <div className="rf-site-readiness-row" key={`${group.key}-${row.controlChannelHz}`}>
                <code>{formatRfHz(row.controlChannelHz)}</code>
                <span className={rfValidationStageClass(candidate?.rfStatus || (candidate ? "measured" : ""))}>{candidate?.snrDb == null ? "--" : `${formatFixed(candidate.snrDb, 1)} dB`}</span>
                <span className={rfValidationStageClass(candidate?.p25Frames ? "passed" : candidate?.p25Status)}>{label(candidate?.p25Status || "not run")}</span>
                <span className={rfValidationStageClass(candidate?.metricsStatus)}>{label(candidate?.metricsStatus || "not run")}</span>
                <span className={rfValidationStageClass(rfValidationCandidateVoiceStatus(candidate))}>{label(rfValidationCandidateVoiceStatus(candidate))}{candidate?.voiceRealCalls ? ` (${candidate.voiceRealCalls})` : ""}</span>
                <span>{candidate ? `gain ${candidate.gain ?? "auto"}, frequency correction ${formatSignedHz(Number(candidate.errorHz ?? 0))}` : "--"}</span>
                <span>{candidate ? rfValidationCandidateOutcome(candidate) : "No candidate tested"}</span>
              </div>;
            })}
          </div>
        </div>;
      })}
    </div>
  </div>;
}

function rfValidationSiteReadiness(candidates: any[], systems: RfSurveySystem[]) {
  type SiteReadinessGroup = { key: string; label: string; system?: RfSurveySystem; candidates: any[] };
  const systemKeys: SiteReadinessGroup[] = systems.map(system => ({
    key: system.shortName,
    label: system.siteLabel || system.shortName,
    system,
    candidates: []
  }));
  const unknown = new Map<string, { key: string; label: string; system?: RfSurveySystem; candidates: any[] }>();
  for (const candidate of candidates) {
    const system = rfValidationCandidateSystem(candidate, systems);
    if (systems.length > 0 && !system)
      continue;
    const key = system?.shortName || String(candidate.systemShortName || candidate.siteLabel || "unknown");
    let group = systemKeys.find(row => row.key === key);
    if (!group) {
      if (!unknown.has(key))
        unknown.set(key, { key, label: system?.siteLabel || system?.shortName || candidate.siteLabel || candidate.systemShortName || "Unknown site", system, candidates: [] });
      group = unknown.get(key)!;
    }
    group.candidates.push(candidate);
  }
  return [...systemKeys, ...unknown.values()].map(group => {
    const controlChannels = (group.system?.controlChannelsHz?.length ? group.system.controlChannelsHz : Array.from(new Set(group.candidates.map(candidate => Number(candidate.controlChannelHz)).filter(Boolean))))
      .map(controlChannelHz => {
        const ccCandidates = group.candidates.filter(candidate => Number(candidate.controlChannelHz) === Number(controlChannelHz));
        return { controlChannelHz: Number(controlChannelHz), best: rfValidationBestCandidate(ccCandidates) };
      });
    const best = rfValidationBestCandidate(group.candidates);
    return { ...group, best, controlChannels, hasP25: group.candidates.some(candidate => candidate.p25Frames || candidate.p25Status === "passed") };
  });
}

function rfValidationBestCandidate(candidates: any[]) {
  return candidates.slice().sort((a, b) => rfValidationCandidateReadinessScore(b) - rfValidationCandidateReadinessScore(a))[0];
}

function rfValidationCandidateReadinessScore(candidate: any) {
  let score = 0;
  if (candidate?.rfStatus === "measured" || Number.isFinite(Number(candidate?.snrDb))) score += 100;
  if (candidate?.p25Frames || candidate?.p25Status === "passed") score += 200;
  if (candidate?.metricsStatus === "passed") score += 300;
  if (candidate?.voiceStatus === "passed") score += 150;
  if (candidate?.voiceStatus === "inconclusive") score += 25;
  if (candidate?.voiceStatus === "failed") score += 10;
  score += Math.min(50, Math.max(0, Number(candidate?.snrDb ?? 0)));
  score -= Math.abs(Number(candidate?.errorHz ?? 0)) / 100000;
  return score;
}

function rfValidationCandidateOutcome(candidate: any) {
  if (rfValidationCandidatePassed(candidate) && candidate.voiceStatus === "passed") return "Usable for source planning; voice observed";
  if (rfValidationCandidatePassed(candidate)) return "Usable for source planning; voice inconclusive";
  if (candidate.voiceStatus === "failed" && candidate.voiceSummary) return `Voice failed: ${candidate.voiceSummary}`;
  if (candidate.voiceStatus === "inconclusive" && candidate.voiceSummary) return `Voice inconclusive: ${candidate.voiceSummary}`;
  if (candidate.metricsStatus === "passed") return "TR decode passed; voice not proven";
  if (candidate.metricsStatus === "failed" && candidate.metricsSummary) return `TR failed: ${candidate.metricsSummary}`;
  if (candidate.p25Frames || candidate.p25Status === "passed") return "P25 decode only";
  if (candidate.p25Status === "failed" && candidate.p25Summary) return `P25 failed: ${candidate.p25Summary}`;
  if (candidate.rfStatus === "measured") return "RF measured only";
  return rfValidationCandidateBlocker(candidate) || "Not proven";
}

function RfValidationDetails({ experiment }: { experiment: RfSurveyExperiment }) {
  const evidence = parseExperimentJson<any>(experiment.evidenceJson);
  const interpretation = parseExperimentJson<any>(experiment.interpretationJson);
  return <div className="rf-power-details">
    <div className={experiment.status === "passed" ? "setup-note" : "setup-warning-list"}>
      <div>{experiment.blockingIssue || experiment.resultSummary}</div>
    </div>
    <div className="setup-review">
      <div><span>System</span><code>{evidence?.systemShortName || "--"}</code></div>
      <div><span>RF seconds</span><code>{evidence?.parameters?.rfDuration ?? evidence?.parameters?.rfDurationSeconds ?? "--"}</code></div>
      <div><span>P25 seconds</span><code>{evidence?.parameters?.p25Duration ?? evidence?.parameters?.p25DurationSeconds ?? "--"}</code></div>
      <div><span>Metric seconds</span><code>{evidence?.parameters?.metricsDuration ?? evidence?.parameters?.metricsDurationSeconds ?? "--"}</code></div>
      <div><span>Applied to TR</span><code>{evidence?.appliedCandidateToLiveTr ? evidence?.appliedCandidate?.candidateId || "yes" : "no"}</code></div>
      <div><span>Recommendation</span><code>{interpretation?.recommendation || "--"}</code></div>
    </div>
    <RfValidationRecommendations experiment={experiment} />
    <pre className="log-box compact">{JSON.stringify({ interpretation, candidates: rfValidationCandidates(experiment) }, null, 2)}</pre>
  </div>;
}

function rfPowerHasMeasuredRows(experiment: RfSurveyExperiment) {
  const evidence = parseExperimentJson<any>(experiment.evidenceJson);
  const rows = Array.isArray(evidence?.rows) ? evidence.rows : [];
  return rows.some((row: any) => row.status === "measured");
}

function rfPowerBestMeasuredRow(experiment: RfSurveyExperiment) {
  const evidence = parseExperimentJson<any>(experiment.evidenceJson);
  const rows = Array.isArray(evidence?.rows) ? evidence.rows : [];
  return rows.filter((row: any) => row.status === "measured")
    .sort((a: any, b: any) => Number(Boolean(a.overload)) - Number(Boolean(b.overload)) || Number(b.snrDb ?? -999) - Number(a.snrDb ?? -999))[0];
}

function rfPowerQuality(row: any) {
  const snr = Number(row?.snrDb);
  const ccPeak = Number(row?.peakDb);
  const strongestPeak = Number(row?.strongestPeakDb);
  const strongestOffset = Number(row?.strongestPeakOffsetHz);
  const adjacentStronger = Number.isFinite(strongestOffset) && Math.abs(strongestOffset) > 25_000 && (!Number.isFinite(ccPeak) || !Number.isFinite(strongestPeak) || strongestPeak > ccPeak + 3);
  const adjacentNote = adjacentStronger
    ? ` Stronger adjacent peak at ${formatFixed(strongestOffset, 0)} Hz offset is present.`
    : "";
  if (row?.overload)
    return {
      label: "Overloaded",
      tone: "warning",
      recommendation: `Reduce SDR gain, remove excess amplification, or bypass an LNA/multicoupler path, then rerun.${adjacentNote}`
    };
  if (!Number.isFinite(snr))
    return {
      label: row?.status === "measured" ? "Unknown" : "No measurement",
      tone: "neutral",
      recommendation: row?.issue || "Rerun the scan after checking SDR access and tool output."
    };
  if (snr >= 20)
    return {
      label: "Excellent",
      tone: "ok",
      recommendation: `Strong control-channel RF margin. Continue to P25 probe and call-quality validation.${adjacentNote}`
    };
  if (snr >= 14)
    return {
      label: "Good",
      tone: "ok",
      recommendation: `Good control-channel RF margin. Continue, but still verify P25 and live call quality.${adjacentNote}`
    };
  if (snr >= 8)
    return {
      label: "Usable",
      tone: "info",
      recommendation: `Minimum usable control-channel RF margin. Proceed cautiously and compare against CC metrics/P25 results.${adjacentNote}`
    };
  if (adjacentStronger)
    return {
      label: "Off Channel",
      tone: "warning",
      recommendation: `The tuned control-channel window is weak, while a stronger adjacent peak is ${formatFixed(strongestOffset, 0)} Hz away. Confirm the active CC, try another configured CC, or inspect spectrum before P25.`
    };
  if (snr >= 3)
    return {
      label: "Marginal",
      tone: "warning",
      recommendation: "RF is present but weak. Try antenna aim, RF path simplification, gain changes, or another control channel."
    };
  return {
    label: "Poor",
    tone: "error",
    recommendation: "Not enough RF margin. Fix antenna/path/source coverage before treating decoder results as meaningful."
  };
}

function RfPowerScanSummary({ experiment, onDetails }: { experiment: RfSurveyExperiment; onDetails: () => void }) {
  const best = rfPowerBestMeasuredRow(experiment);
  if (!best) return null;
  const quality = rfPowerQuality(best);
  const displayStatus = experiment.status === "blocked" ? "failed" : experiment.status;
  return <div className={displayStatus === "passed" ? "rf-power-summary passed" : displayStatus === "failed" ? "rf-power-summary failed" : "rf-power-summary"}>
    <div>
      <strong>{`Best source ${best.index}: ${formatFixed(best.snrDb, 1)} dB CC SNR`}</strong>
      <span>{experiment.blockingIssue || experiment.resultSummary}</span>
    </div>
    <div className={`rf-quality ${quality.tone}`}>
      <strong>{quality.label}</strong>
      <span>{quality.recommendation}</span>
    </div>
    <button onClick={onDetails}>Details</button>
  </div>;
}

type RfAidBand = { label: string; tone: "error" | "warning" | "info" | "ok" | "neutral"; weight: number };

function RfPowerVisualAid({ experiment }: { experiment: RfSurveyExperiment }) {
  const best = rfPowerBestMeasuredRow(experiment);
  if (!best) return null;
  const snr = Number(best.snrDb);
  const strongestPeak = Number(best.strongestPeakDb ?? best.peakDb);
  const clipPct = Number(best.clipPct ?? 0);
  const ccOffset = Number(best.peakOffsetHz);
  const strongestOffset = Number(best.strongestPeakOffsetHz);
  const adjacentText = Number.isFinite(strongestOffset) && Math.abs(strongestOffset) > 25_000
    ? `Strongest carrier is ${formatFixed(strongestOffset, 0)} Hz from center. Judge decode readiness from CC SNR, then use spectrum context if CC is weak.`
    : "Strongest carrier is close to the tuned control channel.";
  return <div className="rf-power-aid">
    <div className="rf-aid-head">
      <strong>Interpreting the RF Sweep Results</strong>
      <span>Good tuning keeps CC SNR high while leaving headroom below overload.</span>
    </div>
    <RfAidBar
      label="CC SNR"
      value={Number.isFinite(snr) ? `${formatFixed(snr, 1)} dB` : "--"}
      marker={markerPct(snr, 0, 30)}
      bands={[
        { label: "<3 poor", tone: "error", weight: 3 },
        { label: "3-8 marginal", tone: "warning", weight: 5 },
        { label: "8-14 usable", tone: "info", weight: 6 },
        { label: "14-20 good", tone: "ok", weight: 6 },
        { label: "20+ excellent", tone: "ok", weight: 10 }
      ]}
      note="This is the main go/no-go RF margin for the selected control channel."
    />
    <RfAidBar
      label="Strongest peak"
      value={Number.isFinite(strongestPeak) ? `${formatFixed(strongestPeak, 1)} dBFS` : "--"}
      marker={markerPct(strongestPeak, -60, 0)}
      bands={[
        { label: "quiet", tone: "neutral", weight: 42 },
        { label: "strong", tone: "ok", weight: 12 },
        { label: "near limit", tone: "warning", weight: 3 },
        { label: ">-3 overload", tone: "error", weight: 3 }
      ]}
      note="Use this for gain headroom. Near 0 dBFS can overload even when clipping is still 0%."
    />
    <RfAidBar
      label="Clipping"
      value={`${formatFixed(clipPct, 2)}%`}
      marker={markerPct(clipPct, 0, 2)}
      bands={[
        { label: "0 clean", tone: "ok", weight: 1 },
        { label: "watch", tone: "warning", weight: 1 },
        { label: "1%+ overload", tone: "error", weight: 2 }
      ]}
      note="Any repeated clipping means gain or external amplification is too high."
    />
    <div className="rf-aid-context">
      <div><span>Measured signal offset</span><code>{Number.isFinite(ccOffset) ? formatSignedHz(ccOffset) : "--"}</code></div>
      <div><span>Noise floor</span><code>{best.noiseFloorDb == null ? "--" : `${formatFixed(best.noiseFloorDb, 1)} dBFS`}</code></div>
      <p>{adjacentText}</p>
    </div>
  </div>;
}

function RfAidBar({ label, value, marker, bands, note }: { label: string; value: string; marker: number | null; bands: RfAidBand[]; note: string }) {
  return <div className="rf-aid-row">
    <div className="rf-aid-label"><strong>{label}</strong><code>{value}</code></div>
    <div className="rf-aid-track">
      {bands.map(band => <span className={`rf-aid-band ${band.tone}`} style={{ flex: band.weight }} key={`${label}-${band.label}`}>{band.label}</span>)}
      {marker != null && <i className="rf-aid-marker" style={{ left: `${marker}%` }} />}
    </div>
    <small>{note}</small>
  </div>;
}

function markerPct(value: number, min: number, max: number) {
  if (!Number.isFinite(value) || max <= min) return null;
  return Math.max(0, Math.min(100, ((value - min) / (max - min)) * 100));
}

function RfPowerSweepMatrix({ experiment }: { experiment: RfSurveyExperiment }) {
  const evidence = parseExperimentJson<any>(experiment.evidenceJson);
  const rows = Array.isArray(evidence?.rows) ? evidence.rows : [];
  const measured = rows.filter((row: any) => row.status === "measured");
  if (measured.length <= 1) return null;
  return <div className="rf-power-matrix">
    <div className="setup-job-head">
      <strong>CC x Gain Sweep</strong>
      <span className="muted">{measured.length} measured condition{measured.length === 1 ? "" : "s"}</span>
    </div>
    <div className="rf-power-matrix-grid">
      {measured.map((row: any, index: number) => {
        const quality = rfPowerQuality(row);
        return <div className={`rf-power-matrix-cell ${quality.tone}`} key={`${row.index}-${row.controlChannelHz}-${row.gain}-${index}`}>
          <strong>{row.controlChannelHz ? formatRfHz(Number(row.controlChannelHz)) : "CC"}</strong>
          <span>gain {row.gain === "" || row.gain == null ? "auto" : String(row.gain)}</span>
          <code>{formatFixed(row.snrDb, 1)} dB</code>
          <small>{row.overload ? "overload" : quality.label}</small>
        </div>;
      })}
    </div>
  </div>;
}

function RfPowerScanDetails({ experiment }: { experiment: RfSurveyExperiment }) {
  const evidence = parseExperimentJson<any>(experiment.evidenceJson);
  const interpretation = parseExperimentJson<any>(experiment.interpretationJson);
  const rows = Array.isArray(evidence?.rows) ? evidence.rows : [];
  return <div className="rf-power-details">
    <div className="setup-review">
      <div><span>Control channels</span><code>{Array.isArray(evidence?.controlChannelsHz) ? evidence.controlChannelsHz.map((hz: number) => formatRfHz(Number(hz))).join(", ") : evidence?.controlChannelHz ? formatRfHz(Number(evidence.controlChannelHz)) : "--"}</code></div>
      <div><span>Duration</span><code>{evidence?.durationSeconds ?? "--"}s</code></div>
      <div><span>Gains</span><code>{Array.isArray(evidence?.gainSequence) && evidence.gainSequence.length ? evidence.gainSequence.join(", ") : "--"}</code></div>
      <div><span>Recommendation</span><code>{interpretation?.recommendation || "--"}</code></div>
    </div>
    <details className="rf-calculation-note">
      <summary>How these RF scan results are calculated</summary>
      <div>
        <p>PizzaWave runs the displayed SDR command against the selected control channel and writes a short IQ capture into the Setup RF evidence folder. RTL-SDR captures are treated as unsigned 8-bit I/Q samples; Airspy captures are treated as signed 16-bit I/Q samples.</p>
        <p>The analyzer reads the first analysis window from that file, removes DC bias, applies a Hamming window, then computes a simple FFT. CC peak/SNR use the strongest bin inside the tuned control-channel window. Noise floor is the median of the lower 80% of FFT-bin power values. Strongest offset is the strongest bin anywhere in the capture. Clip percentage counts samples near the ADC rails, and a very high strongest peak flags overload risk.</p>
        <p>These are quick relative measurements for comparing RF path changes and SDR settings. They are not calibrated dBm/dBFS lab measurements, and decode/call quality still has to be proven by the later P25 and call-quality steps.</p>
      </div>
    </details>
    <div className="rf-power-table">
      <div className="rf-power-row header"><span>SDR</span><span>Control channel</span><span>Status</span><span>Quality</span><span>Gain</span><span>Control SNR</span><span>Control peak</span><span>Noise</span><span>Measured signal offset</span><span>Strongest</span><span>Clip</span><span>Output</span></div>
      {rows.map((row: any, index: number) => {
        const quality = rfPowerQuality(row);
        return <div className={row.overload ? "rf-power-row warning" : row.status === "measured" ? "rf-power-row" : "rf-power-row failed"} key={`${row.index}-${index}`}>
          <span>Source {row.index} {row.sdrType ? `(${row.sdrType})` : ""}</span>
          <span>{row.controlChannelHz ? formatRfHz(Number(row.controlChannelHz)) : "--"}</span>
          <span className={row.status === "measured" ? "section-status ok" : "section-status error"}>{row.overload ? "overload" : row.status === "blocked" ? "Unavailable" : label(row.status || "unknown")}</span>
          <span className={`rf-quality-label ${quality.tone}`} title={quality.recommendation}>{quality.label}</span>
          <span>{row.gain === "" || row.gain == null ? "auto" : String(row.gain)}</span>
          <span>{row.snrDb == null ? "--" : `${formatFixed(row.snrDb, 1)} dB`}</span>
          <span>{row.peakDb == null ? "--" : `${formatFixed(row.peakDb, 1)} dB`}</span>
          <span>{row.noiseFloorDb == null ? "--" : `${formatFixed(row.noiseFloorDb, 1)} dB`}</span>
          <span>{row.peakOffsetHz == null ? "--" : `${formatFixed(row.peakOffsetHz, 0)} Hz`}</span>
          <span>{row.strongestPeakOffsetHz == null ? "--" : `${formatFixed(row.strongestPeakOffsetHz, 0)} Hz`}</span>
          <span>{formatFixed(row.clipPct ?? 0, 2)}%</span>
          <span>{row.issue || (row.output ? "Captured" : "--")}</span>
        </div>;
      })}
    </div>
    <div className="rf-command-log-list">
      {rows.map((row: any, index: number) => <div className="rf-command-log" key={`command-${row.index}-${index}`}>
        <div className="setup-job-head">
          <strong>Source {row.index} Command</strong>
          <span className={row.status === "measured" ? "section-status ok" : "section-status error"}>{row.overload ? "overload" : label(row.status || "unknown")}</span>
        </div>
        <pre className="log-box compact">{[`$ ${row.command || "--"}`, row.output ? trimLogText(row.output) : row.issue || "No command output captured."].join("\n\n")}</pre>
      </div>)}
    </div>
    {interpretation?.followUps?.length > 0 && <div className="setup-warning-list">{interpretation.followUps.map((item: string) => <div key={item}>{item}</div>)}</div>}
  </div>;
}

function CcMetricRunHistory({ runs, selectedCcHz, onDetails }: { runs: RfSurveyExperiment[]; selectedCcHz?: number; onDetails: (run: RfSurveyExperiment) => void }) {
  if (!runs.length) return <div className="setup-note">No CC metric runs yet.</div>;
  return <div className="cc-run-history">
    <div className="cc-run-row header">
      <span>Run</span>
      <span>Status</span>
      <span>Focused CC</span>
      <span>Samples / avg</span>
      <span>Result</span>
      <span></span>
    </div>
    {runs.map(run => {
      const evidence = parseExperimentJson<any>(run.evidenceJson);
      const row = selectedCcRow(evidence, selectedCcHz);
      const focused = Number(evidence?.selectedControlChannelHz || row?.frequencyHz || 0);
      return <div className={run.status === "passed" ? "cc-run-row passed" : run.status === "failed" || run.status === "blocked" ? "cc-run-row failed" : "cc-run-row"} key={run.id}>
        <span>{formatShortDate(run.createdAtUtc)}</span>
        <span className={run.status === "passed" ? "section-status ok" : run.status === "failed" || run.status === "blocked" ? "section-status error" : "section-status"}>{label(run.status)}</span>
        <code>{focused ? formatRfHz(focused) : "--"}</code>
        <span>{row ? `${row.samples ?? 0} / ${formatFixed(row.avgDecodeRate, 1)}/sec` : "--"}</span>
        <span>{run.blockingIssue || run.resultSummary || "--"}</span>
        <button onClick={() => onDetails(run)}>Details</button>
      </div>;
    })}
  </div>;
}

function selectedCcRow(evidence: any, selectedCcHz?: number) {
  const rows = Array.isArray(evidence?.ccRows) ? evidence.ccRows : [];
  const selected = Number(evidence?.selectedControlChannelHz || selectedCcHz || 0);
  return rows.find((row: any) => Number(row.frequencyHz) === selected) ?? evidence?.evaluatedRow ?? rows.find((row: any) => row.known) ?? rows[0];
}

function CcQualityDetails({ experiment }: { experiment: RfSurveyExperiment }) {
  const evidence = parseExperimentJson<any>(experiment.evidenceJson);
  const interpretation = parseExperimentJson<any>(experiment.interpretationJson);
  if (!evidence) return <pre className="log-box">{experiment.evidenceJson || "No CC metric details were stored."}</pre>;
  const aggregate = evidence.aggregate ?? {};
  const rows = Array.isArray(evidence.ccRows) ? evidence.ccRows : [];
  return <div className="cc-quality-details">
    <div className={experiment.status === "passed" ? "setup-note" : "setup-warning-list"}>
      <div>{experiment.blockingIssue || experiment.resultSummary || "CC quality measurement completed."}</div>
    </div>
    {interpretation?.recommendation && <div className="setup-note"><strong>Recommendation:</strong> {interpretation.recommendation}</div>}
    <div className="setup-review">
      <div><span>System</span><code>{evidence.systemShortName || "--"}</code></div>
      <div><span>Window</span><code>{evidence.durationSeconds ?? "--"} sec</code></div>
      <div><span>Decode lines</span><code>{aggregate.ccSummaryDecodeLines ?? 0}</code></div>
      <div><span>Avg decode</span><code>{formatFixed(aggregate.ccSummaryAvgDecodeRate, 1)} msg/sec</code></div>
      <div><span>Zero decode</span><code>{formatFixed(aggregate.ccSummaryDecodeZeroPct, 1)}%</code></div>
      <div><span>Retunes</span><code>{aggregate.retunes ?? 0}</code></div>
      <div><span>Calls</span><code>{aggregate.callStarts ?? 0} started / {aggregate.callConclusions ?? 0} ended</code></div>
      <div><span>No TX</span><code>{aggregate.noTx ?? 0}</code></div>
    </div>
    <div className="cc-quality-table">
      <div className="cc-quality-row header">
        <span>Control channel</span>
        <span>Known</span>
        <span>Samples</span>
        <span>Avg</span>
        <span>Min / Max</span>
        <span>Zero</span>
        <span>Low</span>
        <span>Score</span>
      </div>
      {rows.length === 0 && <div className="cc-quality-empty">No per-control-channel decode summary rows were captured in this window.</div>}
      {rows.map((row: any) => <div className={row.known ? "cc-quality-row known" : "cc-quality-row"} key={row.frequencyHz}>
        <code>{formatRfHz(Number(row.frequencyHz) || 0)}</code>
        <span>{row.known ? "Yes" : "No"}</span>
        <span>{row.samples ?? 0}</span>
        <span>{formatFixed(row.avgDecodeRate, 1)}/sec</span>
        <span>{formatFixed(row.minDecodeRate, 1)} / {formatFixed(row.maxDecodeRate, 1)}</span>
        <span>{formatFixed(row.zeroDecodePct, 1)}%</span>
        <span>{formatFixed(row.lowDecodePct, 1)}%</span>
        <span>{formatFixed(row.score, 0)}</span>
      </div>)}
    </div>
    {Array.isArray(interpretation?.followUps) && interpretation.followUps.length > 0 && <div className="setup-warning-list">
      {interpretation.followUps.map((item: string) => <div key={item}>{item}</div>)}
    </div>}
  </div>;
}

function parseExperimentJson<T>(value: string): T | null {
  try {
    return value ? JSON.parse(value) as T : null;
  } catch {
    return null;
  }
}

function experimentControlChannelHz(experiment?: RfSurveyExperiment | null): number | null {
  if (!experiment) return null;
  const evidence = parseExperimentJson<any>(experiment.evidenceJson);
  const direct = Number(evidence?.controlChannelHz ?? evidence?.selectedControlChannelHz ?? evidence?.parameters?.controlChannelHz);
  if (Number.isFinite(direct) && direct > 0) return Math.round(direct);
  const commandText = [evidence?.command, evidence?.preview?.command].filter(Boolean).join("\n");
  const commandMatch = /(?:^|\s)-f\s+(\d{6,})\b/.exec(commandText);
  if (commandMatch) return Number(commandMatch[1]);
  const summaryMatch = /(?:cc|controlChannelHz)["':,\s]+(\d{6,})/i.exec(experiment.resultSummary || "");
  return summaryMatch ? Number(summaryMatch[1]) : null;
}

function experimentMatchesControlChannel(experiment: RfSurveyExperiment, controlChannelHz: number) {
  if (!controlChannelHz) return true;
  const experimentCc = experimentControlChannelHz(experiment);
  return experimentCc == null || Math.abs(experimentCc - controlChannelHz) <= 1;
}

function StaleExperimentNotice({ experiment, activeControlChannelHz }: { experiment: RfSurveyExperiment; activeControlChannelHz?: number }) {
  const experimentCc = experimentControlChannelHz(experiment);
  return <div className="setup-note stale">
    Prior {label(experiment.type)} result is for {experimentCc ? formatRfHz(experimentCc) : "another control channel"} and does not count for {activeControlChannelHz ? formatRfHz(activeControlChannelHz) : "the selected control channel"}. Rerun this sub-step for the active CC.
  </div>;
}

function formatShortDate(value: string) {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString([], { month: "short", day: "numeric", hour: "numeric", minute: "2-digit" });
}

function formatFixed(value: unknown, digits: number) {
  const numeric = Number(value);
  return Number.isFinite(numeric) ? numeric.toFixed(digits) : "--";
}

function formatRfHz(value: number) {
  if (!value) return "--";
  return value >= 1_000_000 ? `${(value / 1_000_000).toFixed(6)} MHz` : `${value.toLocaleString()} Hz`;
}

function RfMetricsPanel({ data, rangeHours, baseline, category, finding, clearFinding, setRangeHours, setBaseline, setCategory }: { data: TrTroubleshoot; rangeHours: number; baseline: string; category: RfChartCategory; finding?: SystemRecommendation | null; clearFinding?: () => void; setRangeHours: (value: number) => void; setBaseline: (value: string) => void; setCategory: (value: RfChartCategory) => void }) {
  const systems = data.health.systemSummaries ?? [];
  const initialSystem = systems.find(system => system.status !== "Healthy")?.systemShortName ?? systems[0]?.systemShortName ?? "";
  const [selectedSystem, setSelectedSystem] = useState(() => localStorage.getItem("pizzawave-system-rf-selected-site") || initialSystem);
  useEffect(() => {
    if (systems.some(system => system.systemShortName === selectedSystem)) return;
    setSelectedSystem(initialSystem);
  }, [selectedSystem, initialSystem, systems]);
  useEffect(() => {
    if (!finding?.ownerKey || !systems.some(system => system.systemShortName.toLowerCase() === finding.ownerKey.toLowerCase())) return;
    setSelectedSystem(finding.ownerKey);
  }, [finding?.findingId, finding?.ownerKey, systems]);
  function selectSite(system: string, nextCategory?: RfChartCategory) {
    setSelectedSystem(system);
    localStorage.setItem("pizzawave-system-rf-selected-site", system);
    if (nextCategory) setCategory(nextCategory);
  }
  const scopedCharts = data.health.charts.map(chart => {
    const series = chart.series.filter(row => !row.scope || row.scope.toLowerCase() === selectedSystem.toLowerCase());
    if (chart.title === "Decode Rate" && chart.labels.length) {
      series.push({ label: "Strong reference", values: chart.labels.map(() => 40), isBaseline: true, scope: selectedSystem });
    }
    return { ...chart, series };
  });
  const primaryTitles = new Set(["Decode Rate", "Zero-Decode Samples", "Calls Without Audio", "Control-Channel Retunes"]);
  const visibleCharts = scopedCharts
    .filter(chart => category === "all" ? primaryTitles.has(chart.title) : rfChartCategoryForTitle(chart.title) === category)
    .filter(chart => chart.title !== "Capture Interruptions" || chart.series.some(series => series.values.some(value => value > 0)));
  const baselineNote = scopedCharts.find(chart => chart.baselineNote)?.baselineNote;
  const annotations = finding?.ownerKey.toLowerCase() === selectedSystem.toLowerCase() ? finding.episodes : [];
  return <div className="rf-metrics-panel">
    <RfHealthStatusPanel data={data} onSelectSite={system => selectSite(system)} onSelectCategory={setCategory} />
    <SystemPageHeaderControls><div className="metric-controls rf-chart-controls header-controls">
      <label>Window <select value={rangeHours} onChange={event => setRangeHours(Number(event.target.value))}><option value={2}>Last 2 hours</option><option value={6}>Last 6 hours</option><option value={24}>Last 24 hours</option><option value={72}>Last 3 days</option><option value={168}>Last 7 days</option></select></label>
      <label>Site <select value={selectedSystem} onChange={event => selectSite(event.target.value)}>{systems.map(system => <option value={system.systemShortName} key={system.systemShortName}>{trSystemDisplayName(system.systemShortName)}</option>)}</select></label>
      <label>Charts <select value={category} onChange={e => setCategory(e.target.value as RfChartCategory)}><option value="all">Primary</option><option value="decode">Signal</option><option value="activity">Capture</option><option value="events">Events</option></select></label>
      <label>Compare against baseline <select value={baseline} onChange={e => setBaseline(e.target.value)}><option>7d</option><option>14d</option><option>30d</option></select></label>
    </div></SystemPageHeaderControls>
    {finding && <div className="card rf-finding-context"><div><strong>Finding evidence overlay</strong><p>{finding.title} · {finding.episodeCount.toLocaleString()} recorded episode(s). Shaded chart regions are immutable episode intervals linked to finding #{finding.findingId}.</p></div>{clearFinding && <button type="button" onClick={clearFinding}>Clear overlay</button>}</div>}
    {baselineNote && <p className="baseline-note rf-baseline-summary">{baselineNote}</p>}
    <div className="tr-chart-grid">{visibleCharts.map(chart => <TrHealthChartView chart={chart} annotations={annotations} showBaselineNote={false} key={chart.title} />)}</div>
  </div>;
}

function rfChartCategoryForTitle(title: string): Exclude<RfChartCategory, "all"> {
  if (/decode/i.test(title)) return "decode";
  if (/calls/i.test(title)) return "activity";
  return "events";
}

function IncidentMetricsPanel({ dashboard, rangeHours, refreshToken, onRangeHoursChange }: { dashboard: Dashboard | null; rangeHours: number; refreshToken: number; onRangeHoursChange: (hours: number) => void }) {
  const [page, setPage] = useState(1);
  const [openChain, setOpenChain] = useState<string | null>(null);
  useEffect(() => { setPage(1); setOpenChain(null); }, [rangeHours]);
  const chainResource = usePersistentRefresh({
    key: `incident-chains|${rangeHours}|${page}|${refreshToken}`,
    enabled: true,
    load: () => api.request<IncidentDecisionChainPage>(`/api/v1/incidents/chains?hours=${rangeHours}&page=${page}&pageSize=20`)
  });
  const chains = chainResource.data;
  if (!dashboard || !chains) return <div className="card">Loading incident performance...</div>;

  const categoryCounts = Array.from(new Set(dashboard.incidents.map(incident => incident.category))).map(category => ({ label: label(category), value: dashboard.incidents.filter(incident => incident.category === category).length })).sort((a, b) => b.value - a.value);
  const categoryBars: BarStat[] = categoryCounts.map(row => ({ label: row.label, value: row.value, ratio: dashboard.incidents.length ? row.value / dashboard.incidents.length : 0, valueText: row.value.toLocaleString() }));
  const callBands = [
    { label: "1 call", value: dashboard.incidents.filter(incident => incident.calls.length === 1).length },
    { label: "2-3 calls", value: dashboard.incidents.filter(incident => incident.calls.length >= 2 && incident.calls.length <= 3).length },
    { label: "4-6 calls", value: dashboard.incidents.filter(incident => incident.calls.length >= 4 && incident.calls.length <= 6).length },
    { label: "7+ calls", value: dashboard.incidents.filter(incident => incident.calls.length >= 7).length }
  ];
  const callBars: BarStat[] = callBands.map(row => ({ label: row.label, value: row.value, ratio: dashboard.incidents.length ? row.value / dashboard.incidents.length : 0, valueText: row.value.toLocaleString() }));
  const creationChart: TrHealthChart = {
    title: "Incident Creation Volume", yAxisLabel: "Created incidents", valueFormat: "F0",
    labels: chains.buckets.map(bucket => new Date(bucket.start * 1000).toISOString()),
    series: [{ label: "Created", values: chains.buckets.map(bucket => bucket.accepted), isBaseline: false, scope: "" }], baselineNote: ""
  };
  const pageCount = Math.max(1, Math.ceil(chains.totalGroups / chains.pageSize));

  return <div className="incident-metrics-panel">
    <SystemPageHeaderControls><div className="segmented" role="group" aria-label="Incident performance window">{[24, 48, 168].map(hours => <button className={rangeHours === hours ? "active" : ""} onClick={() => onRangeHoursChange(hours)} key={hours}>{hours === 24 ? "24h" : hours === 48 ? "2d" : "Week"}</button>)}</div></SystemPageHeaderControls>
    <div className="incident-outcome-summary" aria-label="Incident pipeline outcomes">
      <span><strong>{chains.created.toLocaleString()}</strong> created</span><span><strong>{chains.updated.toLocaleString()}</strong> updated</span><span><strong>{chains.dropped.toLocaleString()}</strong> dropped</span><span><strong>{chains.totalGroups.toLocaleString()}</strong> grouped outcomes</span>
    </div>
    <section className="incident-performance-section system-content-section"><SystemSectionHeader title="Incident Creation Volume" /><TrHealthChartView chart={creationChart} showBaselineNote={false} showTitle={false} /></section>
    <div className="incident-distribution-grid">
      <section className="system-content-section"><SystemSectionHeader title="Incident Categories" /><Bars title="Incident Categories" rows={categoryBars} showTitle={false} /></section>
      <section className="system-content-section"><SystemSectionHeader title="Retained Calls per Incident" /><Bars title="Retained Calls per Incident" rows={callBars} showTitle={false} /></section>
    </div>
    <section className="card incident-chain-card system-content-section">
      <SystemSectionHeader title="Incident Pipeline Inspector" description="Persisted incidents and dropped candidates, grouped by operator-facing outcome. Open a row to follow each call through the recorded decisions to its result." actions={pageCount > 1 ? <div className="pagination-row table-top-pagination"><button disabled={page <= 1} onClick={() => setPage(1)}>First</button><button disabled={page <= 1} onClick={() => setPage(page - 1)}>Prev</button><span>Page {page} of {pageCount}</span><button disabled={page >= pageCount} onClick={() => setPage(page + 1)}>Next</button><button disabled={page >= pageCount} onClick={() => setPage(pageCount)}>Last</button></div> : null} />
      <PanelLoadState label="incident decision chains" state={chainResource.state} hasData={Boolean(chains)} onRetry={chainResource.refresh} />
      <div className="incident-chain-list">{chains.groups.map(group => {
        const preview = group.evidenceCalls.find(call => call.transcriptSnippet) ?? group.evidenceCalls[0];
        const persisted = group.createdCount + group.updatedCount > 0;
        const activity = persisted ? [group.createdCount ? `${group.createdCount} creation` : "", group.updatedCount ? `${group.updatedCount} update${group.updatedCount === 1 ? "" : "s"}` : ""].filter(Boolean).join(" · ") : "Dropped before persistence";
        return <React.Fragment key={group.groupKey}><button type="button" className="incident-chain-row" aria-expanded={openChain === group.groupKey} onClick={() => setOpenChain(openChain === group.groupKey ? null : group.groupKey)}><span className={`section-status ${persisted ? "ok" : "warning"}`}>{persisted ? "Incident" : "Dropped"}</span><span className="incident-chain-identity"><strong>{group.displayTitle}</strong><small>{label(group.category)} · {trSystemDisplayName(group.systemShortName)} · {new Date(group.latestTimestampUtc).toLocaleString()}</small>{preview && <em>{preview.talkgroupName || `TG ${preview.talkgroup}`} · “{preview.transcriptSnippet || "No transcript text recorded"}”</em>}</span><span className="incident-chain-activity"><strong>{activity}</strong><small>{group.evidenceCalls.length.toLocaleString()} evidence call{group.evidenceCalls.length === 1 ? "" : "s"}</small></span></button>{openChain === group.groupKey && <IncidentEvidenceDag group={group} />}</React.Fragment>;
      })}</div>
      {!chains.groups.length && <p className="muted">No terminal incident outcomes were recorded in this window.</p>}
    </section>
  </div>;
}

function IncidentEvidenceDag({ group }: { group: IncidentDecisionGroup }) {
  const chains = [...group.chains].sort((left, right) => new Date(left.timestampUtc).getTime() - new Date(right.timestampUtc).getTime());
  const startsWithExistingIncident = chains[0]?.outcome === "updated";
  let priorCallIds = new Set<number>(startsWithExistingIncident ? chains[0].callIds : []);
  let priorSaved = { title: "", detail: "" };
  const events = chains.map(chain => {
    const currentCallIds = new Set(chain.callIds);
    const addedCallIds = chain.callIds.filter(callId => !priorCallIds.has(callId));
    const removedCallIds = [...priorCallIds].filter(callId => !currentCallIds.has(callId));
    const metadata = chain.steps.map(step => parseIncidentStoryMetadata(step.metadataJson));
    const proposed = metadata.find(row => row.title || row.detail) ?? { title: "", detail: "" };
    const saved = [...metadata].reverse().find(row => row.title || row.detail) ?? proposed;
    const savedWordingChanged = Boolean(priorSaved.title || priorSaved.detail) && (saved.title !== priorSaved.title || saved.detail !== priorSaved.detail);
    const proposedWordingChanged = Boolean(proposed.title && saved.title && proposed.title !== saved.title);
    const addedCalls = addedCallIds.map(callId => group.evidenceCalls.find(call => call.callId === callId)).filter((call): call is IncidentDecisionGroup["evidenceCalls"][number] => Boolean(call));
    const event = { chain, addedCallIds, removedCallIds, addedCalls, priorCount: priorCallIds.size, saved, proposed, savedWordingChanged, proposedWordingChanged };
    priorCallIds = currentCallIds;
    if (saved.title || saved.detail) priorSaved = saved;
    return event;
  });
  const createdEvent = events.find(event => event.chain.outcome === "created");
  const laterAdded = events.reduce((sum, event) => sum + (event === createdEvent ? 0 : event.addedCallIds.length), 0);
  const unchangedReviews = events.filter((event, index) => index > 0 && event.addedCallIds.length === 0 && event.removedCallIds.length === 0 && !event.savedWordingChanged).length;
  const storyEvents = events.reduce<Array<{ signature: string; events: typeof events }>>((groups, event) => {
    const signature = JSON.stringify({
      outcome: event.chain.outcome,
      calls: event.chain.callIds,
      added: event.addedCallIds,
      removed: event.removedCallIds,
      priorCount: event.priorCount,
      proposedTitle: event.proposed.title,
      saved: event.saved,
      savedWordingChanged: event.savedWordingChanged,
      proposedWordingChanged: event.proposedWordingChanged,
      decisions: event.chain.steps.map((step, index) => ({ label: incidentStoryDecisionLabel(step.reason, index === event.chain.steps.length - 1, event.chain.outcome), text: incidentStoryDecisionText(step.reason) }))
    });
    const prior = groups.at(-1);
    if (prior?.signature === signature) prior.events.push(event);
    else groups.push({ signature, events: [event] });
    return groups;
  }, []);
  const storySummary = createdEvent
    ? `Created from ${createdEvent?.chain.callIds.length ?? 0} call${createdEvent?.chain.callIds.length === 1 ? "" : "s"}.${laterAdded ? ` ${laterAdded} later call${laterAdded === 1 ? " was" : "s were"} added.` : ""}${unchangedReviews ? ` Re-evaluated ${unchangedReviews} time${unchangedReviews === 1 ? "" : "s"} without an operator-visible change.` : ""} The current incident contains ${chains.at(-1)?.callIds.length ?? 0} calls.`
    : group.updatedCount
      ? `This incident already existed before the selected window. It was re-evaluated ${events.length} time${events.length === 1 ? "" : "s"} here and currently contains ${chains.at(-1)?.callIds.length ?? 0} calls.`
    : `This candidate was evaluated from ${chains.at(-1)?.callIds.length ?? 0} call${chains.at(-1)?.callIds.length === 1 ? "" : "s"} and was not persisted.`;
  return <div className="incident-evidence-shelf incident-dag-shelf">
    <p className="incident-story-summary"><strong>What happened</strong><span>{storySummary}</span></p>
    <div className="incident-story-timeline">{storyEvents.map((storyEvent, index) => {
      const event = storyEvent.events.at(-1)!;
      const { chain } = event;
      const noMembershipChange = (index > 0 || startsWithExistingIncident) && event.addedCallIds.length === 0 && event.removedCallIds.length === 0;
      const eventCount = storyEvent.events.length;
      const firstEventTime = new Date(storyEvent.events[0].chain.timestampUtc).toLocaleString();
      const lastEventTime = new Date(chain.timestampUtc).toLocaleString();
      const resultTitle = chain.outcome === "created"
        ? "Incident created"
        : chain.outcome === "dropped"
          ? "Candidate dropped"
          : event.addedCallIds.length
            ? `${event.addedCallIds.length} call${event.addedCallIds.length === 1 ? "" : "s"} added`
            : event.removedCallIds.length
              ? `${event.removedCallIds.length} call${event.removedCallIds.length === 1 ? "" : "s"} removed`
              : event.savedWordingChanged
                ? "Incident wording updated"
                : "No operator-visible change";
      const resultDetail = chain.outcome === "dropped"
        ? "Nothing was saved to the incident dashboard."
        : `${chain.callIds.length} call${chain.callIds.length === 1 ? "" : "s"} attached${event.proposedWordingChanged ? "; existing wording retained" : ""}.`;
      return <article className="incident-dag-attempt" key={chain.chainKey}>
        <div className="incident-dag-attempt-head"><strong>{index + 1}. {chain.outcome === "created" ? "Created" : chain.outcome === "dropped" ? "Rejected" : noMembershipChange ? "Re-evaluated" : "Updated"}{eventCount > 1 ? ` ${eventCount} times` : ""}</strong><span>{eventCount > 1 ? `${firstEventTime}–${lastEventTime}` : lastEventTime}</span></div>
        {!chain.completeTrace && <p className="muted incident-dag-legacy">Only the final recorded decision is available for this historical event.</p>}
        <div className="incident-dag-attempt-path" aria-label={`${chain.outcome} evidence path for ${group.displayTitle}`}>
          <div className="incident-dag-call-stack"><small>Evidence change</small>{event.addedCalls.map(call => <div className="incident-dag-node call" key={call.callId}><strong>{call.talkgroupName || `TG ${call.talkgroup}`}</strong><span>New call · TG {call.talkgroup} · {new Date(call.timestamp * 1000).toLocaleTimeString()}</span><p>{call.transcriptSnippet || "No transcript text recorded."}</p></div>)}{!event.addedCalls.length && <div className="incident-dag-node call quiet"><strong>No new retained call</strong><span>{event.priorCount.toLocaleString()} existing call{event.priorCount === 1 ? " was" : "s were"} reconsidered.</span></div>}{event.removedCallIds.length > 0 && <div className="incident-dag-node call removed"><strong>{event.removedCallIds.length} prior call{event.removedCallIds.length === 1 ? "" : "s"} removed</strong></div>}</div>
          <div className="incident-dag-arrow" aria-hidden="true">→</div>
          <div className="incident-dag-gates"><small>Why PizzaWave chose this</small>{chain.steps.map((step, stepIndex) => <React.Fragment key={step.id}><div className="incident-dag-node gate"><strong>{incidentStoryDecisionLabel(step.reason, stepIndex === chain.steps.length - 1, chain.outcome)}</strong><p>{incidentStoryDecisionText(step.reason)}</p></div>{stepIndex < chain.steps.length - 1 && <div className="incident-dag-stage-arrow" aria-hidden="true">↓</div>}</React.Fragment>)}{event.proposedWordingChanged && <div className="incident-story-wording"><span>Candidate wording</span><strong>{event.proposed.title}</strong><span>Saved wording</span><strong>{event.saved.title}</strong></div>}</div>
          <div className="incident-dag-arrow" aria-hidden="true">→</div>
          <div className={`incident-dag-node outcome ${chain.outcome}`}><small>Effect</small><strong>{resultTitle}</strong><p>{resultDetail}</p></div>
        </div>
      </article>;
    })}</div>
  </div>;
}

function parseIncidentStoryMetadata(value: string) {
  try {
    const parsed = JSON.parse(value || "{}");
    return { title: typeof parsed.title === "string" ? parsed.title.trim() : "", detail: typeof parsed.detail === "string" ? parsed.detail.trim() : "" };
  } catch {
    return { title: "", detail: "" };
  }
}

function incidentStoryDecisionLabel(reason: string, terminal: boolean, outcome: "created" | "updated" | "dropped") {
  const value = reason.replace(/^accepted:|^rejected:/i, "");
  if (/^assembler retained/i.test(value)) return "Candidate selection";
  if (/^final membership retained/i.test(value)) return "Call membership check";
  if (terminal) return outcome === "created" ? "Creation decision" : outcome === "updated" ? "Existing-incident match" : "Rejection decision";
  return "Pipeline decision";
}

function incidentStoryDecisionText(reason: string) {
  const value = reason.replace(/^accepted:|^rejected:/i, "");
  const assembler = /^assembler retained (\d+)\/(\d+) verifier call\(s\):.*?(?:excluded weak\/unrelated calls (.+))?$/i.exec(value);
  if (assembler) {
    const excluded = Number(assembler[2]) - Number(assembler[1]);
    return `Kept ${assembler[1]} of ${assembler[2]} candidate calls${excluded > 0 ? ` and excluded ${excluded} as weak or unrelated` : ""}.`;
  }
  const membership = /^final membership retained (\d+)\/(\d+) assembler call\(s\):.*?validator matched (\d+)\/(\d+) call\(s\) but did not rewrite membership/i.exec(value);
  if (membership) return `Kept all ${membership[1]} retained calls. A secondary check supported ${membership[3]} of them but did not find enough reason to remove the others.`;
  if (/^create incident/i.test(value)) return /rewrote unsupported model narrative/i.test(value)
    ? "Created a new incident from the retained calls and replaced unsupported generated wording with wording grounded in the transcripts."
    : "Created a new incident from the retained calls.";
  if (/^update incident/i.test(value)) return "Matched the retained calls to this existing incident and regenerated its saved title and detail from that evidence.";
  if (/unsupported narrative lacked a specific evidence-backed fallback/i.test(value)) return "Rejected the candidate because its description was not supported by a specific transcript passage.";
  return value
    .replace(/server-owned/gi, "PizzaWave-selected")
    .replace(/incident_id/gi, "incident")
    .replace(/final membership/gi, "final call-membership check")
    .replace(/assembler/gi, "candidate selection")
    .replace(/verifier/gi, "evidence check")
    .replace(/retained/gi, "kept")
    .replace(/narrative/gi, "wording")
    .replace(/call\(s\)/gi, "calls");
}

function ResourceSummary({ snapshot, live }: { snapshot: SystemCpuSnapshot; live?: SystemRuntimeResourceSample | null }) {
  const latest = snapshot.latest;
  const peak = snapshot.peaks;
  const throttle = snapshot.insights.find(i => i.label === "Throttle flags");
  const memory = live?.hostMemory ?? snapshot.hostMemory;
  const hostCpuPercent = live?.hostCpuPercent ?? snapshot.hostCpuPercent;
  const usb = snapshot.usb;
  return <div className="audit-kpis runtime-kpis">
      <Kpi label="Host CPU" value={hostCpuPercent == null ? "--" : `${hostCpuPercent.toFixed(0)}%`} status={hostCpuPercent == null ? "neutral" : hostCpuPercent >= 90 ? "error" : hostCpuPercent >= 75 ? "warning" : "ok"} subtext="Current total use across all processors" />
      <Kpi label="1-Minute Load" value={latest ? latest.hostLoad1.toFixed(2) : "--"} status={!latest ? "neutral" : latest.hostLoadHostPercent >= 150 ? "warning" : "ok"} subtext={latest ? `${snapshot.processorCount.toLocaleString()} logical CPUs; above ${snapshot.processorCount.toFixed(2)} indicates queued or I/O-blocked work` : "No resource sample"} />
      <Kpi label="Temperature" value={latest ? `${latest.hostTempC.toFixed(1)} °C` : "--"} status={!latest ? "neutral" : latest.hostTempC >= 80 ? "error" : latest.hostTempC >= 70 ? "warning" : "ok"} subtext={latest ? `2h peak ${peak.hostTempC.toFixed(1)} °C` : "No thermal sample"} />
      <Kpi label="Host Memory" value={memory?.totalMb ? `${memory.usedPercent.toFixed(0)}%` : "--"} status={!memory?.totalMb ? "neutral" : memory.availableMb / memory.totalMb <= 0.1 ? "error" : memory.availableMb / memory.totalMb <= 0.2 ? "warning" : "ok"} subtext={memory?.totalMb ? `${memory.availableMb.toLocaleString()} MB available of ${memory.totalMb.toLocaleString()} MB` : "Host memory unavailable"} />
      <Kpi label="Throttle" value={latest?.hostThrottledFlags || "none"} status={throttle?.status === "error" ? "error" : throttle?.status === "warning" ? "warning" : "ok"} subtext="Pi thermal/power flags when available" />
      <Kpi label="USB Errors" value={(usb?.kernelErrors.length ?? 0).toLocaleString()} status={usb?.status === "warning" ? "warning" : usb?.status === "unavailable" ? "neutral" : "ok"} subtext={usb?.message ?? "Passive kernel evidence unavailable"} />
  </div>;
}

function ResourceEvidence({ snapshot }: { snapshot: SystemCpuSnapshot }) {
  const usb = snapshot.usb;
  return <details className="card" open={(usb?.currentIssueCount ?? 0) > 0}>
      <summary>USB evidence ({usb?.devices.length ?? 0} devices, {usb?.kernelErrors.length ?? 0} event(s) {usb?.evidencePeriod || "in available evidence"})</summary>
      <p>{usb?.message}</p>
      <p className="muted">Read-only evidence only. PizzaWave does not open, reset, probe, or detach USB devices on this page.</p>
      <h4>Kernel USB warnings and errors</h4>
      {usb?.kernelErrors.length ? <pre className="runtime-usb-log">{usb.kernelErrors.join("\n")}</pre> : <p className="muted">No USB-related warning or error lines found.</p>}
      <p className="muted">Source: {usb?.kernelEvidenceSource}</p>
      <h4>lsusb facts</h4>
      {usb?.devices.length ? <pre className="runtime-usb-log">{usb.devices.join("\n")}</pre> : <p className="muted">No lsusb output is available.</p>}
    </details>;
}

const serviceResourceColors: Record<string, string> = {
  "PizzaWave": "#5aa7ff",
  "Trunk Recorder": "#54d68a",
  "Local LM Studio service": "#b18cff",
  "Qdrant": "#f7c948"
};

function ServiceResourceChart({ history }: { history: SystemRuntimeResourceSample[] }) {
  const components = Object.keys(serviceResourceColors);
  const points = history.length > 0 ? history : [];
  const resources = points.flatMap(sample => sample.processes);
  const maxCpu = Math.max(25, Math.ceil(Math.max(0, ...resources.map(row => row.hostCpuPercent)) / 25) * 25);
  const maxMemory = Math.max(256, Math.ceil(Math.max(0, ...resources.map(row => row.rssMb)) / 256) * 256);
  const left = 54;
  const right = 742;
  const cpuTop = 24;
  const cpuBottom = 126;
  const memoryTop = 164;
  const memoryBottom = 266;
  const tickRatios = [1, 0.75, 0.5, 0.25, 0];
  const tickY = (ratio: number, top: number, bottom: number) => bottom - ratio * (bottom - top);
  const x = (index: number) => points.length <= 1 ? right : left + index / (points.length - 1) * (right - left);
  const linePoints = (component: string, value: (row: SystemRuntimeResourceSample["processes"][number]) => number, top: number, bottom: number, max: number) => points.map((sample, index) => {
    const row = sample.processes.find(resource => resource.component === component);
    const amount = row?.status === "running" ? value(row) : 0;
    return `${x(index)},${bottom - Math.min(max, amount) / max * (bottom - top)}`;
  }).join(" ");
  const timeLabels = points.length < 2 ? points.map((sample, index) => ({ index, label: "Now" })) : [0, Math.floor((points.length - 1) / 2), points.length - 1].map(index => ({
    index,
    label: new Date(points[index].generatedAtUtc).toLocaleTimeString([], { hour: "numeric", minute: "2-digit", second: "2-digit" })
  }));
  return <div className="service-resource-plot">
    {points.length === 0 ? <p className="muted">Waiting for the first live resource sample.</p> : <svg viewBox="0 0 760 298" preserveAspectRatio="xMidYMid meet" role="img" aria-label="Near-real-time CPU and memory use by PizzaWave service">
      <line className="axis" x1={left} y1={cpuTop} x2={left} y2={cpuBottom}/><line className="axis" x1={left} y1={cpuBottom} x2={right} y2={cpuBottom}/>
      <line className="axis" x1={left} y1={memoryTop} x2={left} y2={memoryBottom}/><line className="axis" x1={left} y1={memoryBottom} x2={right} y2={memoryBottom}/>
      <text className="chart-label service-axis-title" x="5" y="18">CPU</text>
      {tickRatios.map(ratio => <g key={`cpu-${ratio}`}><line className="service-chart-gridline" x1={left} y1={tickY(ratio, cpuTop, cpuBottom)} x2={right} y2={tickY(ratio, cpuTop, cpuBottom)}/><text className="chart-label" textAnchor="end" x={left - 6} y={tickY(ratio, cpuTop, cpuBottom) + 4}>{Math.round(maxCpu * ratio)}%</text></g>)}
      <text className="chart-label service-axis-title" x="5" y={memoryTop - 7}>Memory</text>
      {tickRatios.map(ratio => <g key={`memory-${ratio}`}><line className="service-chart-gridline" x1={left} y1={tickY(ratio, memoryTop, memoryBottom)} x2={right} y2={tickY(ratio, memoryTop, memoryBottom)}/><text className="chart-label" textAnchor="end" x={left - 6} y={tickY(ratio, memoryTop, memoryBottom) + 4}>{Math.round(maxMemory * ratio)} MB</text></g>)}
      {components.map(component => <React.Fragment key={component}>
        <polyline fill="none" stroke={serviceResourceColors[component]} strokeWidth="2.5" points={linePoints(component, row => row.hostCpuPercent, cpuTop, cpuBottom, maxCpu)}/>
        <polyline fill="none" stroke={serviceResourceColors[component]} strokeWidth="2.5" points={linePoints(component, row => row.rssMb, memoryTop, memoryBottom, maxMemory)}/>
      </React.Fragment>)}
      {timeLabels.map(({ index, label: timeLabel }) => <text className="chart-label" textAnchor={index === 0 ? "start" : index === points.length - 1 ? "end" : "middle"} x={x(index)} y="290" key={`${index}-${timeLabel}`}>{timeLabel}</text>)}
    </svg>}
  </div>;
}

function ServicesManager({ runtime, snapshot, history, restartBusy, restartMessages, onRestart, onStopTr }: { runtime: any | null; snapshot: SystemCpuSnapshot | null; history: SystemRuntimeResourceSample[]; restartBusy: "" | "pizzad" | "trunk-recorder" | "qdrant"; restartMessages: Record<string, string>; onRestart: (service: "pizzad" | "trunk-recorder" | "qdrant") => void; onStopTr: () => void }) {
  if (!runtime) return <div className="card">Loading service status...</div>;
  const embeddings = runtime.queues?.embeddings;
  const aiCompletion = runtime.aiCompletion;
  const aiHealth = aiCompletion?.health;
  const trIntentionallyStopped = runtime.liveTrActivity?.status === "stopped";
  const aiStatus = !aiCompletion?.enabled ? "Disabled" : aiHealth?.status === "ok" || runtime.service?.lmStudio?.ok ? "Running" : aiHealth?.status === "error" ? "Unavailable" : "No recent activity";
  const vectorStatus = runtime.service?.qdrant?.ok && embeddings?.qdrantOk ? "Running" : embeddings?.enabled ? "Unavailable" : "Disabled";
  const latest = history.at(-1);
  const currentResource = (component: string) => latest?.processes.find(row => row.component === component) ?? snapshot?.processes.find(row => row.component === component);
  const statusClass = (status: string) => status === "Running" ? "status-completed" : status === "Unavailable" ? "status-failed" : "status-paused";
  const serviceRows = [
    { component: "PizzaWave", title: "PizzaWave", status: runtime.service?.pizzad?.ok ? "Running" : "Unavailable", detail: embeddings?.enabled ? `Embeddings: ${embeddings.queueDepth ?? 0} queued, ${(embeddings.failedCalls ?? 0).toLocaleString()} failed` : "Embeddings disabled", action: <button className="danger-button" disabled={restartBusy === "pizzad"} onClick={() => onRestart("pizzad")}>{restartBusy === "pizzad" ? "Restarting..." : "Restart"}</button>, message: restartMessages.pizzad },
    { component: "Trunk Recorder", title: "Trunk Recorder", status: trIntentionallyStopped ? "Stopped" : runtime.service?.trunkRecorder?.ok ? "Running" : "Unavailable", detail: trIntentionallyStopped ? "Stopped by operator" : "Live capture service", action: <><button className="danger-button" disabled={restartBusy === "trunk-recorder"} onClick={() => onRestart("trunk-recorder")}>{restartBusy === "trunk-recorder" ? "Restarting..." : "Restart"}</button><button className="danger-button secondary" disabled={restartBusy === "trunk-recorder"} onClick={onStopTr}>{restartBusy === "trunk-recorder" ? "Working..." : "Stop"}</button></>, message: restartMessages["trunk-recorder"] },
    { component: "Local LM Studio service", title: "LM Studio / AI", status: aiStatus, detail: !aiCompletion?.enabled ? "AI Insights disabled" : `${label(aiCompletion.executionMode || "local")} execution; ${aiHealth?.requests ?? 0} requests over ${aiHealth?.windowMinutes ?? 30}m`, action: null, message: "" },
    { component: "Qdrant", title: "Vector Search", status: vectorStatus, detail: embeddings?.enabled ? `${embeddings.collection || "Collection"}; search ${Number(embeddings.lastSearchMs || 0).toFixed(0)}ms` : "Embedding search disabled", action: <button className="danger-button" disabled={restartBusy === "qdrant"} onClick={() => onRestart("qdrant")}>{restartBusy === "qdrant" ? "Restarting..." : "Restart"}</button>, message: restartMessages.qdrant }
  ];
  return <div className="system-manager-grid">
    {snapshot && <ResourceSummary snapshot={snapshot} live={latest} />}
    <section className="card service-resource-card">
      <SystemSectionHeader title="Service Resource Use" description="CPU and resident memory across the local PizzaWave stack." />
      <div className="service-resource-content">
        <ServiceResourceChart history={history} />
        <div className="service-resource-table-wrap"><table className="service-resource-table"><thead><tr><th>Service</th><th>State</th><th>CPU</th><th>Memory</th><th>Controls</th></tr></thead><tbody>{serviceRows.map(row => {
          const resource = currentResource(row.component);
          return <React.Fragment key={row.component}><tr>
            <td><div className="service-resource-name"><span><i className="service-resource-swatch" style={{ background: serviceResourceColors[row.component] }}/><strong>{row.title}</strong></span><small>{row.detail}</small></div></td>
            <td><span className={`job-status ${statusClass(row.status)}`}>{row.status}</span></td>
            <td className="service-resource-value">{resource?.status === "running" ? `${resource.hostCpuPercent.toFixed(1)}%` : "--"}</td>
            <td className="service-resource-value">{resource?.status === "running" ? `${resource.rssMb.toFixed(0)} MB` : "--"}</td>
            <td><div className="service-card-actions">{row.action}</div></td>
          </tr>{row.message && <tr className="service-resource-message"><td colSpan={5}><span className={row.message.includes("failed") || row.message.includes("Unsupported") ? "settings-message error" : "settings-message ok"}>{row.message}</span></td></tr>}</React.Fragment>;
        })}</tbody></table></div>
      </div>
    </section>
    {snapshot && <ResourceEvidence snapshot={snapshot} />}
  </div>;
}

function PizzadServiceManager({ runtime, restartBusy, restartMessage, onRestart }: { runtime: any | null; restartBusy: boolean; restartMessage: string; onRestart: () => void }) {
  if (!runtime) return <div className="card">Loading service status...</div>;
  const services = [runtime.service?.pizzad].filter(Boolean);
  const embeddings = runtime.queues?.embeddings;
  return <div className="system-manager-grid">
    <div className="system-action-bar">
      <strong>Pizzad</strong>
      <button className="danger-button" disabled={restartBusy} onClick={onRestart}>{restartBusy ? "Restarting..." : "Restart Pizzad"}</button>
      {restartMessage && <span className={restartMessage.includes("failed") || restartMessage.includes("Unsupported") ? "settings-message error" : "settings-message ok"}>{restartMessage}</span>}
    </div>
    <div className="audit-kpis">
      <Kpi label="Pizzad" value={runtime.service?.pizzad?.active || "unknown"} status={runtime.service?.pizzad?.ok ? "ok" : "error"} subtext={runtime.service?.pizzad?.enabled || "systemd enabled state"} />
      <Kpi label="CPU Time" value={`${Number(runtime.process?.totalProcessorTimeSeconds || 0).toFixed(0)}s`} status="ok" subtext={`${runtime.process?.threadCount ?? 0} thread(s)`} />
      <Kpi label="Memory" value={formatBytes(runtime.process?.workingSetBytes || 0)} status={Number(runtime.process?.workingSetBytes || 0) > 1024 * 1024 * 1024 ? "warning" : "ok"} subtext={`PID ${runtime.process?.pid ?? "--"}`} />
      <Kpi label="HTTP API" value="Running" status="ok" subtext="System page loaded from pizzad" />
    </div>
    {embeddings && <div className="audit-kpis">
      <Kpi label="Embeddings" value={embeddings.enabled ? label(embeddings.status || "unknown") : "Disabled"} status={!embeddings.enabled ? "neutral" : embeddings.status === "ok" ? "ok" : "warning"} subtext={`${embeddings.queueDepth ?? 0} queued, ${(embeddings.pendingCalls ?? 0).toLocaleString()} pending`} />
      <Kpi label="Qdrant" value={embeddings.qdrantOk ? "OK" : "Problem"} status={!embeddings.enabled ? "neutral" : embeddings.qdrantOk ? "ok" : "error"} subtext={embeddings.collection || "collection"} />
      <Kpi label="Embedded Calls" value={(embeddings.embeddedCalls ?? 0).toLocaleString()} status={(embeddings.failedCalls ?? 0) > 0 ? "warning" : "ok"} subtext={`${(embeddings.failedCalls ?? 0).toLocaleString()} failed, model ${embeddings.model || "--"}`} />
      <Kpi label="Vector Latency" value={`${Number(embeddings.lastSearchMs || 0).toFixed(0)}ms`} status="ok" subtext={`upsert ${Number(embeddings.lastUpsertMs || 0).toFixed(0)}ms, dim ${embeddings.vectorSize || "--"}`} />
    </div>}
    {embeddings?.lastError && <div className="card"><h3>Embedding Pipeline</h3><p className="settings-message error">{embeddings.lastError}</p></div>}
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

function QdrantServiceManager({ runtime, restartBusy, restartMessage, onRestart }: { runtime: any | null; restartBusy: boolean; restartMessage: string; onRestart: () => void }) {
  if (!runtime) return <div className="card">Loading vector database status...</div>;
  const embeddings = runtime.queues?.embeddings;
  const services = [runtime.service?.qdrant].filter(Boolean);
  const storageBytes = Number(runtime.storage?.qdrantBytes || 0);
  return <div className="system-manager-grid">
    <div className="system-action-bar">
      <strong>Qdrant Vector DB</strong>
      <button className="danger-button" disabled={restartBusy} onClick={onRestart}>{restartBusy ? "Restarting..." : "Restart Qdrant"}</button>
      {restartMessage && <span className={restartMessage.includes("failed") || restartMessage.includes("Unsupported") ? "settings-message error" : "settings-message ok"}>{restartMessage}</span>}
    </div>
    <div className="audit-kpis">
      <Kpi label="Qdrant Service" value={runtime.service?.qdrant?.active || "unknown"} status={runtime.service?.qdrant?.ok ? "ok" : "error"} subtext={runtime.service?.qdrant?.enabled || "systemd enabled state"} />
      <Kpi label="HTTP API" value={embeddings?.qdrantOk ? "OK" : "Problem"} status={embeddings?.enabled ? embeddings.qdrantOk ? "ok" : "error" : "neutral"} subtext={embeddings?.qdrantOk ? embeddings.collection || "collection reachable" : "collection or service not reachable"} />
      <Kpi label="Storage" value={formatBytes(storageBytes)} status="ok" subtext={runtime.storage?.qdrantPath || "/var/lib/pizzawave/qdrant"} />
      <Kpi label="Vector Latency" value={`${Number(embeddings?.lastSearchMs || 0).toFixed(0)}ms`} status="ok" subtext={`upsert ${Number(embeddings?.lastUpsertMs || 0).toFixed(0)}ms`} />
    </div>
    <div className="audit-kpis">
      <Kpi label="Embeddings" value={embeddings?.enabled ? label(embeddings.status || "unknown") : "Disabled"} status={!embeddings?.enabled ? "neutral" : embeddings.status === "ok" ? "ok" : "warning"} subtext={`${embeddings?.queueDepth ?? 0} queued, ${(embeddings?.pendingCalls ?? 0).toLocaleString()} pending`} />
      <Kpi label="Embedded Calls" value={(embeddings?.embeddedCalls ?? 0).toLocaleString()} status={(embeddings?.failedCalls ?? 0) > 0 ? "warning" : "ok"} subtext={`${(embeddings?.failedCalls ?? 0).toLocaleString()} failed, model ${embeddings?.model || "--"}`} />
    </div>
    {embeddings?.lastError && <div className="card"><h3>Embedding Pipeline</h3><p className="settings-message error">{embeddings.lastError}</p></div>}
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

function TrunkRecorderServiceManager({ runtime, data, restartBusy, restartMessage, onRestart }: { runtime: any | null; data: TrTroubleshoot; restartBusy: boolean; restartMessage: string; onRestart: () => void }) {
  if (!runtime) return <div className="card">Loading service status...</div>;
  const services = [runtime.service?.trunkRecorder].filter(Boolean);
  const healthIssues = data.health.metrics.filter(row => row.isIssue).length + data.health.systems.filter(row => row.isIssue).length;
  return <div className="system-manager-grid">
    <div className="system-action-bar">
      <strong>Trunk Recorder</strong>
      <button className="danger-button" disabled={restartBusy} onClick={onRestart}>{restartBusy ? "Restarting..." : "Restart Trunk Recorder"}</button>
      {restartMessage && <span className={restartMessage.includes("failed") || restartMessage.includes("Unsupported") ? "settings-message error" : "settings-message ok"}>{restartMessage}</span>}
    </div>
    <div className="audit-kpis">
      <Kpi label="TR Service" value={runtime.service?.trunkRecorder?.active || "unknown"} status={runtime.service?.trunkRecorder?.ok ? "ok" : "error"} subtext={runtime.service?.trunkRecorder?.enabled || "systemd enabled state"} />
      <Kpi label="Capture Health" value={healthIssues > 0 ? "Watch" : "OK"} status={healthIssues > 0 ? "warning" : "ok"} subtext={`${healthIssues.toLocaleString()} current issue row(s)`} />
      <Kpi label="Config" value={data.config?.ok ? "OK" : "Problem"} status={data.config?.ok ? "ok" : "error"} subtext={data.config?.path || "TR config path"} />
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

function RecommendationsPanel({ recommendations, onOpen, onChanged }: { recommendations: SystemRecommendations | null; onOpen: (item: SystemRecommendation) => void; onChanged: () => Promise<unknown> }) {
  const [tab, setTab] = useState<"active" | "known" | "history">("active");
  const [selectedFindingId, setSelectedFindingId] = useState<number | null>(null);
  const [activityPage, setActivityPage] = useState(1);
  const [statusDraft, setStatusDraft] = useState<Record<number, string>>({});
  const [noteDraft, setNoteDraft] = useState<Record<number, string>>({});
  const [busy, setBusy] = useState<number | null>(null);
  const [message, setMessage] = useState("");
  if (!recommendations) return <div className="card">Loading recommendations...</div>;
  const items = tab === "active" ? recommendations.items : tab === "known" ? (recommendations.knownIssues ?? []) : recommendations.history;
  const allItems = [...recommendations.items, ...(recommendations.knownIssues ?? []), ...recommendations.history];
  const selectedItem = allItems.find(item => item.findingId === selectedFindingId) ?? null;
  const activityPageSize = 5;
  const activityPageCount = Math.max(1, Math.ceil((selectedItem?.audit.length ?? 0) / activityPageSize));
  const currentActivityPage = Math.min(activityPage, activityPageCount);
  const visibleActivity = selectedItem?.audit.slice((currentActivityPage - 1) * activityPageSize, currentActivityPage * activityPageSize) ?? [];
  async function changeState(item: SystemRecommendation) {
    setBusy(item.findingId); setMessage("");
    try {
      await api.request(`/api/v1/system/recommendations/findings/${item.findingId}/state`, { method: "POST", body: JSON.stringify({ status: statusDraft[item.findingId] ?? item.workflowStatus, reviewInDays: (statusDraft[item.findingId] ?? item.workflowStatus) === "known_issue" ? 7 : null }) });
      await onChanged();
    } catch (error) { setMessage(error instanceof Error ? error.message : "Unable to update the finding."); }
    finally { setBusy(null); }
  }
  async function addNote(item: SystemRecommendation) {
    const note = noteDraft[item.findingId]?.trim();
    if (!note) return;
    setBusy(item.findingId); setMessage("");
    try {
      await api.request(`/api/v1/system/recommendations/findings/${item.findingId}/notes`, { method: "POST", body: JSON.stringify({ note }) });
      setNoteDraft(current => ({ ...current, [item.findingId]: "" }));
      await onChanged();
    } catch (error) { setMessage(error instanceof Error ? error.message : "Unable to add the note."); }
    finally { setBusy(null); }
  }
  return <div className="trouble-panel recommendations-panel">
    <div className="recommendation-tabs" role="tablist"><button className={tab === "active" ? "active" : ""} onClick={() => setTab("active")}>Active ({recommendations.items.length})</button><button className={tab === "known" ? "active" : ""} onClick={() => setTab("known")}>Known Issues ({recommendations.knownIssues?.length ?? 0})</button><button className={tab === "history" ? "active" : ""} onClick={() => setTab("history")}>History ({recommendations.history.length})</button></div>
    {message && <p className="settings-message error">{message}</p>}
    {items.length === 0 ? <div className="card"><h3>No {tab === "active" ? "Active Findings" : tab === "known" ? "Known Issues" : "Finding History"}</h3><p className="settings-message ok">Nothing is in this view.</p></div> : tab === "history" ?
      <div className="recommendation-history-ledger">{recommendationHistoryGroups(items).map(group => <article className="recommendation-history-row" key={group.key}>
        <div className="recommendation-history-identity"><span className="recommendation-history-icon"><RecommendationTypeIcon item={group.latest} /></span><div><strong>{group.latest.title}</strong><span>{recommendationTypeLabel(group.latest)} · {group.recurrences.toLocaleString()} recurrence{group.recurrences === 1 ? "" : "s"} · {group.records.toLocaleString()} record{group.records === 1 ? "" : "s"}</span></div></div>
        <div className="recommendation-history-date"><span>First seen</span><strong>{formatShortDate(group.firstSeen)}</strong></div>
        <div className="recommendation-history-date"><span>Last seen</span><strong>{formatRelativeAge(group.lastSeen)}</strong><small>{formatShortDate(group.lastSeen)}</small></div>
        <div className="recommendation-history-outcome"><span>{label(group.latest.workflowStatus)}</span><strong>{label(group.latest.severity)}</strong></div>
        <button type="button" className="secondary-button" onClick={() => { setActivityPage(1); setSelectedFindingId(group.latest.findingId); }}>Review</button>
      </article>)}</div> :
      <div className="recommendation-list">
        {items.map(item => <article className={`card recommendation-card severity-${item.severity} kind-${item.kind}${item.activityState === "quiet" ? " is-dormant" : ""}`} key={`${item.findingId}-${item.id}`}>
          <div className="recommendation-head"><div className="recommendation-type"><RecommendationTypeIcon item={item} /><span>{recommendationTypeLabel(item)}</span></div><div className="recommendation-state">{item.activityState === "quiet" && <span>Dormant</span>}{item.workflowStatus !== "new" && <span>{label(item.workflowStatus)}</span>}<strong>{label(item.severity)}</strong></div></div>
          <div className="recommendation-card-body"><h3>{item.title}</h3>
            <div className="recommendation-facts">{recommendationFacts(item).map(fact => <div key={fact.label}><span>{fact.label}</span><strong>{fact.value}</strong>{fact.detail && <small>{fact.detail}</small>}</div>)}</div>
            {item.reviewDue && <p className="settings-message warning">Known Issue review is due.</p>}
            <div className="recommendation-buttons"><button onClick={() => onOpen(item)}>Open {item.destinationLabel}</button><button className="secondary-button" onClick={() => { setActivityPage(1); setSelectedFindingId(item.findingId); }}>Review finding</button></div>
          </div>
        </article>)}
      </div>}
    {selectedItem && createPortal(<div className="finding-drawer-backdrop" onMouseDown={event => { if (event.target === event.currentTarget) setSelectedFindingId(null); }}><aside className="finding-drawer" role="dialog" aria-modal="true" aria-labelledby="finding-drawer-title">
      <header className={`finding-drawer-head severity-${selectedItem.severity}`}><div><span>{recommendationTypeLabel(selectedItem)} finding #{selectedItem.findingId}</span><h2 id="finding-drawer-title">{selectedItem.title}</h2></div><button type="button" className="icon-button" aria-label="Close finding" onClick={() => setSelectedFindingId(null)}><X size={18} /></button></header>
      <div className="finding-drawer-content">
        <section className="finding-drawer-section facts"><h3><Info size={17} />Core facts</h3><div className="recommendation-facts drawer-facts">{recommendationFacts(selectedItem, 6).map(fact => <div key={fact.label}><span>{fact.label}</span><strong>{fact.value}</strong>{fact.detail && <small>{fact.detail}</small>}</div>)}</div></section>
        <section className="finding-drawer-section next-step"><h3><ChevronRight size={18} />Next step</h3><p>{selectedItem.action}</p><button onClick={() => { onOpen(selectedItem); setSelectedFindingId(null); }}>Open {selectedItem.destinationLabel}</button></section>
        {selectedItem.hypotheses.length > 0 && <section className="finding-drawer-section hypotheses"><h3><Search size={17} />Cause hypotheses</h3>{selectedItem.hypotheses.slice(0, 4).map(row => <div className="finding-hypothesis" key={row.kind}><div><strong>{row.label}</strong><span>{label(row.status)} · {label(row.confidence)} confidence</span></div><p>{row.rationale}</p></div>)}</section>}
        {selectedItem.episodes.length > 0 && <section className="finding-drawer-section episodes"><h3><Activity size={17} />Episode patterns</h3><div className="finding-episode-list">{recommendationEpisodeGroups(selectedItem).map(row => <div key={row.signature}><strong>{row.count.toLocaleString()} × {label(row.signature)}</strong><span>Latest {formatShortDate(row.latest)}{row.conditions ? ` · ${row.conditions}` : ""}</span></div>)}</div>{selectedItem.episodeCount > selectedItem.episodes.length && <small className="muted">Pattern summaries use the latest {selectedItem.episodes.length.toLocaleString()} of {selectedItem.episodeCount.toLocaleString()} recorded episodes.</small>}</section>}
        <section className="finding-drawer-section operator-notes"><h3><Settings size={17} />Operator notes</h3><div className="finding-workflow"><select value={statusDraft[selectedItem.findingId] ?? selectedItem.workflowStatus} onChange={event => setStatusDraft(current => ({ ...current, [selectedItem.findingId]: event.target.value }))}><option value="new">New</option><option value="unresolved">Unresolved</option><option value="investigating">Investigating</option><option value="known_issue">Known Issue</option><option value="monitoring">Monitoring</option><option value="resolved">Resolved</option><option value="dismissed">Dismissed</option></select><button disabled={busy === selectedItem.findingId} onClick={() => void changeState(selectedItem)}>Update status</button></div><div className="finding-note"><textarea value={noteDraft[selectedItem.findingId] ?? ""} onChange={event => setNoteDraft(current => ({ ...current, [selectedItem.findingId]: event.target.value }))} placeholder="Add an append-only operator note" /><button disabled={busy === selectedItem.findingId || !(noteDraft[selectedItem.findingId] ?? "").trim()} onClick={() => void addNote(selectedItem)}>Add note</button></div></section>
        {selectedItem.audit.length > 0 && <section className="finding-drawer-section activity"><h3><Database size={17} />Recent activity</h3><div className="finding-audit">{visibleActivity.map(event => <div key={event.id}><strong>{label(event.eventType)}</strong><span>{event.actor} · {formatShortDate(event.createdAtUtc)}</span><p>{event.detail}</p></div>)}</div>{activityPageCount > 1 && <div className="finding-activity-pagination"><button type="button" disabled={currentActivityPage === 1} onClick={() => setActivityPage(page => Math.max(1, page - 1))}>Previous</button><span>Page {currentActivityPage} of {activityPageCount}</span><button type="button" disabled={currentActivityPage === activityPageCount} onClick={() => setActivityPage(page => Math.min(activityPageCount, page + 1))}>Next</button></div>}</section>}
      </div>
    </aside></div>, document.body)}
  </div>;
}

function RecommendationTypeIcon({ item }: { item: SystemRecommendation }) {
  if (item.target.subTab === "rf" || item.target.subTab === "metrics" || item.section === "trunk-recorder") return <Radio size={18} aria-hidden="true" />;
  if (item.target.subTab === "bandwidth" || item.section === "network") return <Gauge size={18} aria-hidden="true" />;
  if (item.target.topTab === "setup" || item.kind === "improvement") return <Wrench size={18} aria-hidden="true" />;
  if (item.target.subTab === "transcription" || item.target.subTab === "jobs") return <Activity size={18} aria-hidden="true" />;
  if (item.section === "qdrant") return <Database size={18} aria-hidden="true" />;
  return <Info size={18} aria-hidden="true" />;
}

function recommendationTypeLabel(item: SystemRecommendation) {
  if (item.target.subTab === "rf" || item.target.subTab === "metrics") return "RF";
  if (item.target.subTab === "bandwidth" || item.section === "network") return "Bandwidth";
  if (item.target.subTab === "transcription" || item.target.subTab === "jobs") return "Transcription";
  if (item.section === "trunk-recorder") return "Trunk Recorder";
  if (item.section === "ai") return "AI";
  return item.kind === "improvement" ? "Improvement" : label(item.section);
}

function recommendationFacts(item: SystemRecommendation, limit = 4) {
  const facts: { label: string; value: string; detail?: string }[] = [];
  if (item.episodeCount > 0) facts.push({ label: "Episodes", value: item.episodeCount.toLocaleString() });
  facts.push({ label: "Confidence", value: label(item.confidence) });
  for (const diagnostic of item.runbook?.diagnostics ?? []) {
    if (facts.some(fact => fact.label.toLowerCase() === diagnostic.label.toLowerCase())) continue;
    facts.push({ label: diagnostic.label, value: diagnostic.value });
    if (facts.length >= limit) break;
  }
  if (facts.length < limit && item.evidenceWindow) facts.push({ label: "Evidence", value: item.evidenceWindow });
  if (facts.length < limit && item.lastSeenUtc) facts.push({ label: "Last seen", value: formatRelativeAge(item.lastSeenUtc), detail: formatShortDate(item.lastSeenUtc) });
  return facts.slice(0, limit);
}

function formatRelativeAge(value: string) {
  const timestamp = new Date(value).getTime();
  if (!Number.isFinite(timestamp)) return "Unknown";
  const minutes = Math.max(0, Math.floor((Date.now() - timestamp) / 60_000));
  if (minutes < 1) return "Just now";
  if (minutes < 60) return `${minutes} minute${minutes === 1 ? "" : "s"} ago`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours} hour${hours === 1 ? "" : "s"} ago`;
  const days = Math.floor(hours / 24);
  return `${days} day${days === 1 ? "" : "s"} ago`;
}

function recommendationEpisodeGroups(item: SystemRecommendation) {
  const groups = new Map<string, { signature: string; count: number; latest: string; conditions: Set<string> }>();
  for (const episode of item.episodes) {
    const group = groups.get(episode.signature) ?? { signature: episode.signature, count: 0, latest: episode.endUtc, conditions: new Set<string>() };
    group.count += 1;
    if (new Date(episode.endUtc).getTime() > new Date(group.latest).getTime()) group.latest = episode.endUtc;
    episode.conditions.forEach(condition => group.conditions.add(label(condition)));
    groups.set(episode.signature, group);
  }
  return [...groups.values()].sort((a, b) => b.count - a.count).map(group => ({ ...group, conditions: [...group.conditions].slice(0, 3).join(", ") }));
}

function recommendationHistoryGroups(items: SystemRecommendation[]) {
  const groups = new Map<string, SystemRecommendation[]>();
  for (const item of items) {
    const key = `${item.id}|${item.ownerType}|${item.ownerKey}`;
    groups.set(key, [...(groups.get(key) ?? []), item]);
  }
  return [...groups.entries()].map(([key, rows]) => {
    const ordered = [...rows].sort((a, b) => new Date(b.lastSeenUtc || b.resolvedAtUtc).getTime() - new Date(a.lastSeenUtc || a.resolvedAtUtc).getTime());
    const firstSeen = ordered.reduce((earliest, row) => !earliest || new Date(row.firstSeenUtc).getTime() < new Date(earliest).getTime() ? row.firstSeenUtc : earliest, "");
    const lastSeen = ordered.reduce((latest, row) => !latest || new Date(row.lastSeenUtc).getTime() > new Date(latest).getTime() ? row.lastSeenUtc : latest, "");
    return { key, latest: ordered[0], firstSeen, lastSeen, records: rows.length, recurrences: new Set(rows.map(row => row.findingId)).size };
  }).sort((a, b) => new Date(b.lastSeen).getTime() - new Date(a.lastSeen).getTime());
}

function BackupRestorePanel({ reload, refreshToken }: { reload: () => Promise<void>; refreshToken: number }) {
  const [audioWindow, setAudioWindow] = useState("7d");
  const [busy, setBusy] = useState("");
  const [message, setMessage] = useState("");
  const [backupJob, setBackupJob] = useState<Job | null>(null);
  const [preview, setPreview] = useState<BackupRestorePreview | null>(null);
  const fileRef = useRef<HTMLInputElement | null>(null);
  const handledBackupJobId = useRef<number | null>(null);
  const inventoryResource = usePersistentRefresh({
    key: `system-backups|${audioWindow}|${refreshToken}`,
    enabled: true,
    load: async () => {
      const [rows, estimate, pending] = await Promise.all([
        api.request<BackupArchive[]>("/api/v1/system/backups"),
        loadBackupEstimateValue(),
        api.request<BackupRestorePreview | null>("/api/v1/system/backups/restore/pending")
      ]);
      return { rows, estimate, pending };
    }
  });
  const rows = inventoryResource.data?.rows ?? [];
  const estimate = inventoryResource.data?.estimate ?? null;
  useEffect(() => {
    if (!preview && inventoryResource.data?.pending)
      setPreview(inventoryResource.data.pending);
  }, [inventoryResource.data?.pending, preview]);
  useEffect(() => {
    let canceled = false;
    const pollMs = backupJob && isActiveJob(backupJob) ? 2000 : 5000;
    async function loadBackupJob() {
      try {
        const jobs = await api.request<Job[]>("/api/v1/jobs");
        const current = backupJob?.id
          ? jobs.find(job => job.id === backupJob.id)
          : jobs.find(job => job.type === "system_backup" && isActiveJob(job));
        if (canceled)
          return;
        if (current) {
          setBackupJob(current);
          if (!isActiveJob(current) && handledBackupJobId.current !== current.id) {
            handledBackupJobId.current = current.id;
            setMessage(current.message || `Backup job #${current.id} ${current.status}.`);
            await loadBackups();
          }
        } else if (!backupJob || isActiveJob(backupJob)) {
          setBackupJob(null);
        }
      } catch {
        // The status bar already reports fetch failures; keep the last visible backup job state here.
      }
    }
    void loadBackupJob();
    const timer = window.setInterval(() => void loadBackupJob(), pollMs);
    return () => {
      canceled = true;
      window.clearInterval(timer);
    };
  }, [backupJob?.id, backupJob?.status]);

  const audioWindowOptions = [
    { value: "24h", label: "Last 24h" },
    { value: "7d", label: "Last 7d" },
    { value: "30d", label: "Last 30d" },
    { value: "60d", label: "Last 60d" },
    { value: "all", label: "All" }
  ];
  const audioWindowLabel = audioWindowOptions.find(option => option.value === audioWindow)?.label ?? "All";
  const activeBackupJob = backupJob && isActiveJob(backupJob);
  const locked = Boolean(busy) || Boolean(activeBackupJob);

  function isActiveJob(job: Job) {
    return ["queued", "running", "paused", "canceling"].includes((job.status ?? "").toLowerCase());
  }

  function jobTone(job: Job | null) {
    if (!job)
      return message.toLowerCase().includes("fail") || message.toLowerCase().includes("unable") ? "error" : "ok";
    const status = (job.status ?? "").toLowerCase();
    if (status === "failed")
      return "error";
    if (status === "canceled" || status === "canceling")
      return "warning";
    return status === "completed" ? "ok" : "info";
  }

  async function loadBackups() {
    await inventoryResource.refresh();
  }

  async function loadBackupEstimateValue() {
    return api.request<BackupEstimate>(`/api/v1/system/backups/estimate?audioWindow=${encodeURIComponent(audioWindow)}`);
  }

  async function createBackup() {
    const sizeText = estimate ? ` Estimated source size is ${formatBytes(estimate.bytes)} across ${estimate.fileCount.toLocaleString()} file(s); compressed archive size may differ.` : "";
    if (!confirmAction("Create backup?", `This archives PizzaWave SQLite data, ${audioWindowLabel.toLowerCase()} of recorded call audio, app data, Qdrant storage, TR config, talkgroups, and PizzaWave config.${sizeText} It can take a while on rigs with lots of audio.`)) return;
    setBusy("create");
    setMessage("Starting backup job...");
    try {
      const job = await api.request<Job>("/api/v1/system/backups", { method: "POST", body: JSON.stringify({ audioWindow }) });
      handledBackupJobId.current = null;
      setBackupJob(job);
      setMessage(`Backup job #${job.id} started. You can monitor or stop it from this page or System > Jobs.`);
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Backup failed.");
    } finally {
      setBusy("");
    }
  }

  async function stopBackupJob() {
    if (!backupJob || !isActiveJob(backupJob))
      return;
    if (!confirmAction("Stop backup job?", `This requests cancellation for backup job #${backupJob.id}. A partial archive may be discarded.`)) return;
    setBusy("cancel-backup");
    try {
      const job = await api.request<Job>(`/api/v1/jobs/${backupJob.id}/control`, { method: "POST", body: JSON.stringify({ action: "cancel" }) });
      setBackupJob(job);
      setMessage(`Stop requested for backup job #${job.id}.`);
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Unable to stop backup job.");
    } finally {
      setBusy("");
    }
  }

  async function stageRestore() {
    const file = fileRef.current?.files?.[0];
    if (!file) {
      setMessage("Choose a PizzaWave backup archive first.");
      return;
    }
    if (!confirmAction("Stage restore archive?", "This does not overwrite live data yet. It validates and stages the archive so you can review it here before applying.")) return;
    const form = new FormData();
    form.append("file", file);
    setBusy("restore");
    setMessage("Uploading and validating restore archive...");
    try {
      const result = await api.request<BackupRestorePreview>("/api/v1/system/backups/restore", { method: "POST", body: form });
      setPreview(result);
      setMessage(`Restore staged from ${result.manifest.stackName}. Review the preview below before applying.`);
      await reload();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Restore staging failed.");
    } finally {
      setBusy("");
    }
  }

  async function stageLocalRestore(row: BackupArchive) {
    if (!confirmAction("Stage local backup restore?", `This stages ${row.name} from ${row.path}. It does not overwrite live data yet; review the archive preview before applying.`)) return;
    setBusy(`restore-${row.name}`);
    setMessage(`Staging ${row.name} for restore...`);
    try {
      const result = await api.request<BackupRestorePreview>(`/api/v1/system/backups/${encodeURIComponent(row.name)}/restore`, { method: "POST" });
      setPreview(result);
      setMessage(`Restore staged from ${result.manifest.stackName}. Review the preview below before applying.`);
      await reload();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Restore staging failed.");
    } finally {
      setBusy("");
    }
  }

  async function applyRestore() {
    if (!preview)
      return;
    if (preview.checks.some(check => !check.ok)) {
      setMessage("Restore archive verification failed. Do not apply this backup.");
      return;
    }
    if (!confirmAction("Apply staged restore?", "This overwrites backed-up PizzaWave/TR files and may restart services. Continue only if this archive is the intended restore source.")) return;
    setBusy("apply-restore");
    setMessage("Applying staged restore...");
    try {
      const result = await api.request<BackupRestoreApplyResult>("/api/v1/system/backups/restore/apply", { method: "POST" });
      setMessage(result.message || "Restore applied. Review service status before resuming monitoring.");
      setPreview(null);
      await reload();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Restore apply failed.");
    } finally {
      setBusy("");
    }
  }

  async function cancelRestore() {
    if (!preview)
      return;
    if (!confirmAction("Cancel staged restore?", "This clears the staged restore files. No live data is changed.")) return;
    setBusy("cancel-restore");
    try {
      const result = await api.request<BackupRestoreCancelResult>("/api/v1/system/backups/restore/cancel", { method: "POST" });
      setPreview(null);
      setMessage(result.message || "Restore canceled.");
      await reload();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Restore cancel failed.");
    } finally {
      setBusy("");
    }
  }

  return <>
  <PanelLoadState label="backup inventory" state={inventoryResource.state} hasData={Boolean(inventoryResource.data)} onRetry={inventoryResource.refresh} />
  {(message || backupJob) && <div className={`backup-job-banner section-status ${jobTone(backupJob)}`}>
    <div>
      {backupJob
        ? <strong>Backup job #{backupJob.id}: {label(backupJob.status)}</strong>
        : <strong>Backup / Restore</strong>}
      <span>{backupJob?.message || message}</span>
      {backupJob && <small>{backupJob.completed.toLocaleString()} / {backupJob.total.toLocaleString()} step(s)</small>}
    </div>
    {backupJob && isActiveJob(backupJob) && <button className="danger-button" disabled={Boolean(busy)} onClick={() => void stopBackupJob()}>{busy === "cancel-backup" ? "Stopping..." : "Stop Backup"}</button>}
  </div>}
  {preview ? <section className="card staged-restore-card">
    <div className="jobs-card-head">
      <div><h3>Staged Restore Review</h3><p>Nothing has been overwritten. Verify this archive before applying it to the live PizzaWave stack.</p></div>
      <div className="setup-button-row">
        <button className="danger-button" disabled={locked || preview.checks.some(check => !check.ok)} onClick={() => void applyRestore()}>{busy === "apply-restore" ? "Applying..." : "Apply Staged Restore"}</button>
        <button disabled={locked} onClick={() => void cancelRestore()}>{busy === "cancel-restore" ? "Canceling..." : "Cancel Staged Restore"}</button>
      </div>
    </div>
    <BackupRestorePreviewCard preview={preview} />
  </section> : <div className="system-manager-grid backup-restore-layout">
    <section className="card backup-create-card">
      <div className="jobs-card-head">
        <div><h3>Create Backup</h3><p>Creates a portable full-state archive; only recorded-audio history is windowed.</p></div>
        <button disabled={locked} onClick={() => void createBackup()}>{busy === "create" ? "Starting..." : "Create Backup"}</button>
      </div>
      <label className="setting-field compact-setting">
        <span>Recorded audio scope<small>Database, configuration, app data, Qdrant, Trunk Recorder config, and talkgroups are always included in full.</small></span>
        <select disabled={locked} value={audioWindow} onChange={event => setAudioWindow(event.target.value)}>{audioWindowOptions.map(option => <option key={option.value} value={option.value}>{option.label}</option>)}</select>
      </label>
      {estimate && <div className="backup-preview">
        <div className="audit-kpis backup-estimate-kpis">
          <Kpi label="Estimated Source Size" value={formatBytes(estimate.bytes)} status="ok" subtext={`${estimate.fileCount.toLocaleString()} source file(s); compressed size may differ`} />
          <Kpi label="Recorded Audio" value={audioWindowLabel} status="ok" subtext="Selected by audio file timestamp" />
        </div>
        {estimate.kinds.length > 0 && <details><summary>Backup contents by area</summary><table className="table compact-table"><thead><tr><th>Area</th><th>Files</th><th>Source Size</th></tr></thead><tbody>{estimate.kinds.map(kind => <tr key={kind.kind}>
          <td>{label(kind.kind)}</td><td>{kind.fileCount.toLocaleString()}</td><td>{formatBytes(kind.bytes)}</td>
        </tr>)}</tbody></table></details>}
        {estimate.warnings.length > 0 && <div className="setup-warning-list">{estimate.warnings.map(warning => <div key={warning}>{warning}</div>)}</div>}
      </div>}
    </section>
    <section className="card available-backups-card">
      <div className="jobs-card-head"><div><h3>Available Backups</h3><p>Download an archive for safekeeping or stage one for deliberate review.</p></div><span className="job-status status-completed">{rows.length.toLocaleString()} local</span></div>
      {rows.length === 0 ? <p className="muted">No local backups found.</p> : <div className="table-scroll"><table className="table backups-table"><thead><tr><th>Backup</th><th>Created</th><th>Size</th><th>Actions</th></tr></thead><tbody>{rows.map(row => <tr key={row.name}>
        <td><strong>{row.name}</strong><small>{row.path}</small></td>
        <td>{new Date(row.createdUtc).toLocaleString()}</td>
        <td>{formatBytes(row.bytes)}</td>
        <td><div className="table-action-row"><a className="button-link" href={`/api/v1/system/backups/${encodeURIComponent(row.name)}`}>Download</a><button disabled={locked} onClick={() => void stageLocalRestore(row)}>{busy === `restore-${row.name}` ? "Staging..." : "Stage Restore"}</button><button className="danger-button" disabled={locked} onClick={() => void deleteBackup(row.name)}>Delete</button></div></td>
      </tr>)}</tbody></table></div>}
    </section>
    <section className="card upload-backup-card">
      <div><h3>Upload Backup Archive</h3><p>Upload a PizzaWave archive from another location. Staging validates it without changing live files.</p></div>
      <div className="backup-upload-controls"><input ref={fileRef} disabled={locked} type="file" accept=".zip,application/zip" /><button disabled={locked} onClick={() => void stageRestore()}>{busy === "restore" ? "Staging..." : "Stage Uploaded Archive"}</button></div>
    </section>
  </div>}
  </>;

  async function deleteBackup(name: string) {
    if (!confirmAction("Delete backup?", `This permanently deletes ${name} from local backup storage. Download it first if you need to keep a copy.`)) return;
    setBusy("delete");
    setMessage(`Deleting ${name}...`);
    try {
      await api.request(`/api/v1/system/backups/${encodeURIComponent(name)}`, { method: "DELETE" });
      setMessage(`Deleted ${name}.`);
      await loadBackups();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Delete failed.");
    } finally {
      setBusy("");
    }
  }
}

function SystemResetPanel({ reload }: { reload: () => Promise<void> }) {
  const [preset, setPreset] = useState("data-only");
  const [createBackup, setCreateBackup] = useState(true);
  const [preserveAudit, setPreserveAudit] = useState(true);
  const [audioWindow, setAudioWindow] = useState("7d");
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  const audioOptions = [
    { value: "24h", label: "Last 24h" },
    { value: "7d", label: "Last 7d" },
    { value: "30d", label: "Last 30d" },
    { value: "60d", label: "Last 60d" },
    { value: "all", label: "All" }
  ];
  const presets = [
    { id: "data-only", title: "Data Only", detail: "Clears calls, audio, incidents, transcripts, AI/vector history, jobs, metrics, and recommendations. Current site and configuration remain." },
    { id: "site-reset", title: "Site Reset", detail: "Also clears site/location state, Trunk Recorder configuration, generated talkgroups, catalog policy, and RF evidence. Host and processing settings remain." },
    { id: "full-reset", title: "Full Reset", detail: "Returns PizzaWave to first-run prerequisite mode and regenerates the admin token. Existing backup archives remain available." }
  ];
  const selected = presets.find(row => row.id === preset) ?? presets[0];

  async function runReset() {
    const backupText = createBackup ? ` A backup with ${audioOptions.find(row => row.value === audioWindow)?.label.toLowerCase() ?? "selected"} recorded audio will be created first.` : " No backup will be created first.";
    if (!confirmAction(`Run ${selected.title}?`, `${selected.detail}${backupText} PizzaWave ingest will pause immediately before data removal; Trunk Recorder will continue. Recovery requires restoring an archive from Backup and Restore.`)) return;
    setBusy(true);
    setMessage(`Running ${selected.title}...`);
    try {
      const result = await api.request<SystemResetResult>("/api/v1/system/reset", {
        method: "POST",
        body: JSON.stringify({
          presets: [preset],
          createBackup,
          backupAudioWindow: audioWindow,
          preserveAuditHistory: preset === "data-only" && preserveAudit
        })
      });
      const backupResult = result.backup ? ` Backup: ${result.backup.name}.` : "";
      setMessage(`${result.message}${backupResult}${result.warnings.length ? " Warnings: " + result.warnings.join(" ") : ""}`);
      await reload();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Reset failed.");
    } finally {
      setBusy(false);
    }
  }

  return <div className="system-manager-grid reset-page-layout">
    {message && <div className={`section-status ${message.toLowerCase().includes("fail") ? "error" : "warning"}`}>{message}</div>}
    <section className="card reset-scope-card">
      <div className="jobs-card-head"><div><h3>Choose Reset Scope</h3><p>Reset scopes are mutually exclusive. Review exactly what will be removed before continuing.</p></div></div>
      <div className="reset-option-grid">{presets.map(row => <label className={`reset-option ${preset === row.id ? "selected" : ""}`} key={row.id}>
        <input type="radio" name="reset-scope" value={row.id} checked={preset === row.id} disabled={busy} onChange={() => setPreset(row.id)} />
        <span><strong>{row.title}</strong><small>{row.detail}</small></span>
      </label>)}</div>
      <p className="reset-live-impact"><strong>Live-operation impact:</strong> PizzaWave ingest pauses immediately before data removal and remains paused afterward. Trunk Recorder is not stopped. Resume ingest from Runtime / Queue after reset and any required setup are complete.</p>
      <div className="reset-safety-panel">
        <div className="jobs-card-head"><div><h3>Recovery Safeguard</h3><p>Confirm the recovery boundary before running the selected reset.</p></div><button className="danger-button" disabled={busy} onClick={() => void runReset()}>{busy ? "Resetting..." : `Run ${selected.title}`}</button></div>
        <div className="reset-safety-controls">
          <SettingCheckbox label="Create backup before reset" description="Recommended. Backup creation finishes before PizzaWave pauses ingest or removes data." checked={createBackup} onChange={setCreateBackup} disabled={busy} />
          {createBackup && <label className="setting-field compact-setting reset-audio-window"><span>Recorded audio in backup<small>All non-audio state is backed up in full.</small></span><select disabled={busy} value={audioWindow} onChange={event => setAudioWindow(event.target.value)}>{audioOptions.map(option => <option key={option.value} value={option.value}>{option.label}</option>)}</select></label>}
          {preset === "data-only" && <SettingCheckbox label="Preserve audit and setup history" description="Keeps configuration/setup activity history while operational history is cleared." checked={preserveAudit} onChange={setPreserveAudit} disabled={busy} />}
        </div>
        {!createBackup && <p className="section-status warning">No recovery archive will be created before this reset.</p>}
      </div>
    </section>
  </div>;
}

function BackupRestorePreviewCard({ preview }: { preview: BackupRestorePreview }) {
  const failed = preview.checks.filter(check => !check.ok).length;
  const byKind = preview.manifest.entries.reduce<Record<string, number>>((acc, entry) => {
    acc[entry.kind] = (acc[entry.kind] ?? 0) + 1;
    return acc;
  }, {});
  return <div className="backup-preview">
    <div className="audit-kpis">
      <Kpi label="Archive" value={preview.manifest.stackName || "PizzaWave"} status={failed ? "error" : "ok"} subtext={`created ${new Date(preview.manifest.createdUtc).toLocaleString()}`} />
      <Kpi label="Files" value={preview.manifest.entries.length.toLocaleString()} status="ok" subtext={Object.entries(byKind).map(([k, v]) => `${k}: ${v}`).join(", ")} />
      <Kpi label="Verification" value={failed ? `${failed} failed` : "OK"} status={failed ? "error" : "ok"} subtext={`${preview.checks.length.toLocaleString()} checksum check(s)`} />
    </div>
    {preview.manifest.warnings.length > 0 && <div className="setup-warning-list">{preview.manifest.warnings.map(warning => <div key={warning}>{warning}</div>)}</div>}
    <div className="restore-review-grid">
      <div><h4>Included Areas</h4><table className="table compact-table"><thead><tr><th>Area</th><th>Files</th></tr></thead><tbody>{Object.entries(byKind).map(([kind, count]) => <tr key={kind}><td>{label(kind)}</td><td>{count.toLocaleString()}</td></tr>)}</tbody></table></div>
      <div><h4>Verification Checks</h4><table className="table compact-table"><thead><tr><th>Check</th><th>Result</th><th>Evidence</th></tr></thead><tbody>{preview.checks.map(check => <tr className={check.ok ? "" : "issue-row"} key={check.name}><td>{check.name}</td><td><span className={`job-status ${check.ok ? "status-completed" : "status-failed"}`}>{check.ok ? "Passed" : "Failed"}</span></td><td>{check.message}</td></tr>)}</tbody></table></div>
    </div>
    <details><summary>Technical archive paths</summary><p className="muted">Source host: <code>{preview.manifest.hostname}</code>; database: <code>{preview.manifest.databasePath}</code>; audio: <code>{preview.manifest.audioRoot}</code>; Trunk Recorder config: <code>{preview.manifest.trConfigPath}</code>.</p></details>
  </div>;
}

function PizzadStorageManager({ snapshot, maintenanceJobs }: { snapshot: any | null; maintenanceJobs: Job[] }) {
  if (!snapshot) return <div className="card">Loading storage status...</div>;
  const tables = Object.entries(snapshot.tables ?? {}).sort(([a], [b]) => a.localeCompare(b));
  const storage = snapshot.storage ?? {};
  const diskTotal = Number(storage.diskTotalBytes || 0);
  const diskFree = Number(storage.diskFreeBytes || 0);
  const diskFreePercent = diskTotal > 0 ? diskFree / diskTotal * 100 : 100;
  const readErrors = Number(storage.audioReadErrors || 0) + Number(storage.qdrantReadErrors || 0);
  const storageStatus = readErrors > 0 ? "warning" : "ok";
  return <div className="system-manager-grid">
    <div className="audit-kpis storage-kpis">
      <Kpi label="PizzaWave Data" value={formatBytes(storage.pizzaWaveBytes || 0)} status={storageStatus} subtext={readErrors > 0 ? `${readErrors.toLocaleString()} file read error(s); total is partial` : "Database, recorded audio, and vector data"} />
      <Kpi label="Recorded Audio" value={formatBytes(storage.audioBytes || 0)} status={Number(storage.audioReadErrors || 0) > 0 ? "warning" : "ok"} subtext={`${Number(storage.audioFiles || 0).toLocaleString()} file(s)`} />
      <Kpi label="Database" value={formatBytes(storage.databaseBytes || 0)} status="ok" subtext="SQLite database, WAL, and shared-memory files" />
      <Kpi label="Vector Data" value={formatBytes(storage.qdrantBytes || 0)} status={Number(storage.qdrantReadErrors || 0) > 0 ? "warning" : "ok"} subtext={`${Number(storage.qdrantFiles || 0).toLocaleString()} Qdrant file(s)`} />
      <Kpi label="Disk Free" value={formatBytes(diskFree)} status={diskFreePercent < 10 ? "error" : diskFreePercent < 20 ? "warning" : "ok"} subtext={`${diskFreePercent.toFixed(0)}% free on ${storage.diskRoot || "application volume"}`} />
    </div>
    <div className="card storage-maintenance-card">
      <h3>Automatic Maintenance</h3>
      <p>PizzaWave runs lightweight SQLite optimization and removes completed job history older than 30 days once per day.</p>
      {maintenanceJobs.length === 0 ? <p className="muted">No automatic maintenance run has been recorded yet.</p> :
        <table className="table compact-table storage-maintenance-table"><thead><tr><th>Run</th><th>Status</th><th>Duration</th><th>Outcome</th></tr></thead><tbody>{maintenanceJobs.map(job => <tr key={job.id}>
          <td>{new Date(job.startedAtUtc || job.createdAtUtc).toLocaleString()}</td>
          <td><span className={`job-status status-${job.status}`}>{label(job.status)}</span></td>
          <td>{jobDurationSummary(job)}</td>
          <td>{job.message}</td>
        </tr>)}</tbody></table>}
    </div>
    <details className="card">
      <summary>Technical database table counts</summary>
      <table className="table compact-table"><thead><tr><th>Table</th><th>Rows</th></tr></thead><tbody>{tables.map(([name, count]) => <tr key={name}><td>{name}</td><td>{Number(count).toLocaleString()}</td></tr>)}</tbody></table>
      <p className="muted">Measured {new Date(snapshot.generatedAtUtc).toLocaleString()} in {Number(snapshot.scanDurationMs || 0).toLocaleString()} ms. Database: <code>{storage.databasePath}</code>; audio: <code>{storage.audioRoot}</code>; vectors: <code>{storage.qdrantPath}</code>.</p>
    </details>
  </div>;
}

function TokenUsagePanel({ report, rangeHours, onRangeHoursChange, onPageChange }: { report: TokenUsageReport | null; rangeHours: number; onRangeHoursChange: (hours: number) => void; onPageChange: (page: number) => void }) {
  if (!report) return <div className="trouble-panel"><div className="card">Loading token usage...</div></div>;
  const totalPages = Math.max(1, Math.ceil(report.entryTotal / Math.max(1, report.entryPageSize)));
  const pageSafe = Math.min(report.entryPage, totalPages);
  const rows = report.entries;
  const failures = report.recentFailures;
  const failureKind = (row: TokenUsageReport["entries"][number]) => {
    if (row.success) return "OK";
    if (row.finishReason?.toLowerCase() === "length" || row.error?.toLowerCase().includes("truncat")) return "Truncated";
    if (row.error?.toLowerCase().includes("timeout") || row.error?.toLowerCase().includes("timed out") || row.error?.toLowerCase().includes("request was aborted") || row.error?.toLowerCase().includes("taskcanceled")) return "Timeout";
    if (!row.promptTokens && !row.completionTokens && !row.totalTokens) return "No valid result";
    if (row.error?.toLowerCase().includes("cancel")) return "Canceled";
    return "Error";
  };
  const successRate = report.summary.requests ? report.summary.successes / report.summary.requests * 100 : 100;
  const healthStatus = successRate < 95 ? "error" : successRate < 99 ? "warning" : "ok";
  const usageSummary = (title: string, summary: TokenUsageReport["summary"]) =>
    <Kpi label={title} value={formatCompact(summary.totalTokens)} subtext={`${summary.requests.toLocaleString()} requests · OpenAI equivalent $${summary.estimatedStandardCost.toFixed(2)}`} />;
  return <div className="trouble-panel token-usage-panel">
    <SystemPageHeaderControls><SystemHistoryWindow value={rangeHours} label="AI usage window" onChange={onRangeHoursChange} /></SystemPageHeaderControls>
    <div className="audit-kpis ai-usage-kpis">
      {usageSummary("Selected Range", report.summary)}
      <Kpi label="Request Health" value={`${successRate.toFixed(1)}%`} status={healthStatus} subtext={`${report.summary.successes.toLocaleString()} succeeded · ${report.summary.failures.toLocaleString()} failed`} />
      {usageSummary("This Month", report.monthlySummary)}
      {usageSummary("All Time", report.allTimeSummary)}
    </div>
    <section className="system-content-section">
      <SystemSectionHeader title="AI Activity Over Time" description={`Requests and token use in ${report.bucketSeconds / 3600}-hour buckets across the complete selected window.`} />
      <div className="tr-chart-grid ai-usage-chart-grid">
        <AiRequestOutcomeChart report={report} />
        <AiTokenTimelineChart report={report} />
      </div>
    </section>
    <section className="system-content-section">
      <SystemSectionHeader title="Usage by Activity" description="Which PizzaWave workflows consumed model capacity in this window." />
      <TokenBarChart title="Tokens and Requests" rows={report.byTrigger} />
    </section>
    {report.failuresByKind.length > 0 && <section className="system-content-section">
      <SystemSectionHeader title="AI Failures" description="Grouped failure conditions with recent individual evidence available below." meta={<span>{report.summary.failures.toLocaleString()} failed</span>} />
      <div className="card ai-failure-card">
        <table className="table compact-ai-table"><thead><tr><th>Class</th><th>Requests</th><th>Latest</th><th>Example</th></tr></thead><tbody>{report.failuresByKind.map(row => <tr key={row.kind}>
          <td>{label(row.kind)}</td>
          <td>{row.requests.toLocaleString()}</td>
          <td>{new Date(row.latestUtc).toLocaleString()}</td>
          <td>{row.example || row.kind}</td>
        </tr>)}</tbody></table>
        {failures.length > 0 && <details className="ai-failure-occurrences"><summary>Recent individual failures ({failures.length.toLocaleString()})</summary><div className="ai-failure-scroll"><table className="table compact-ai-table"><thead><tr><th>Time</th><th>Activity</th><th>Class</th><th>Model</th><th>Error</th></tr></thead><tbody>{failures.map(row => <tr key={row.id}>
          <td>{new Date(row.timestampUtc).toLocaleString()}</td>
          <td>{row.triggerActivity}</td>
          <td>{failureKind(row)}</td>
          <td>{row.responseModel || row.requestModel}</td>
          <td>{row.error || row.finishReason || "failed"}</td>
        </tr>)}</tbody></table></div></details>}
      </div>
    </section>}
    <details className="card ai-usage-ledger">
      <summary>Recorded Usage <small>{report.entryTotal.toLocaleString()} request{report.entryTotal === 1 ? "" : "s"} in this window</small></summary>
      <div className="jobs-card-head ai-ledger-head">
        <p className="muted">Request-level token evidence and completion status.</p>
        <div className="pagination-row table-top-pagination">
          <button disabled={pageSafe <= 1} onClick={() => onPageChange(1)}>First</button>
          <button disabled={pageSafe <= 1} onClick={() => onPageChange(pageSafe - 1)}>Prev</button>
          <span>Page {pageSafe} of {totalPages}</span>
          <button disabled={pageSafe >= totalPages} onClick={() => onPageChange(pageSafe + 1)}>Next</button>
          <button disabled={pageSafe >= totalPages} onClick={() => onPageChange(totalPages)}>Last</button>
        </div>
      </div>
      <div className="ai-ledger-table-wrap"><table className="table jobs-table ai-usage-table"><thead><tr><th>Time</th><th>Activity</th><th>Status</th><th>Model</th><th>Prompt</th><th>Completion</th><th>Total</th><th>Finish</th></tr></thead><tbody>{rows.map(row => <tr key={row.id}>
        <td>{new Date(row.timestampUtc).toLocaleString()}</td>
        <td>{row.triggerActivity}</td>
        <td>{failureKind(row)}</td>
        <td>{row.responseModel || row.requestModel}</td>
        <td>{row.promptTokens.toLocaleString()}</td>
        <td>{row.completionTokens.toLocaleString()}</td>
        <td>{(row.totalTokens || row.promptTokens + row.completionTokens).toLocaleString()}</td>
        <td>{row.finishReason}</td>
      </tr>)}</tbody></table></div>
      <details><summary>Technical provenance</summary><p className="muted">{report.ledger}</p></details>
    </details>
  </div>;
}

function AiRequestOutcomeChart({ report }: { report: TokenUsageReport }) {
  const left = 44;
  const right = 664;
  const top = 18;
  const bottom = 170;
  const max = Math.max(1, ...report.byTime.map(row => row.requests));
  const slot = (right - left) / Math.max(1, Math.ceil((report.rangeEnd - report.rangeStart) / report.bucketSeconds));
  const barWidth = Math.max(2, slot - 2);
  const x = (start: number) => left + (start - report.rangeStart) / Math.max(1, report.rangeEnd - report.rangeStart) * (right - left);
  const height = (value: number) => value / max * (bottom - top);
  return <div className="card tr-chart-card ai-usage-chart-card"><h3>Request Outcomes</h3><p className="muted">Successful and failed model requests per bucket.</p><svg className="chart" viewBox="0 0 690 215" preserveAspectRatio="none" role="img" aria-label="AI request outcomes over time"><line className="axis" x1={left} y1={top} x2={left} y2={bottom}/><line className="axis" x1={left} y1={bottom} x2={right} y2={bottom}/><text className="chart-label" x="8" y="23">{max}</text><text className="chart-label" x="24" y="174">0</text>{report.byTime.map(row => { const successHeight = height(row.successes); const failureHeight = height(row.failures); const barX = x(row.start); return <g key={row.start}><rect x={barX} y={bottom - successHeight} width={barWidth} height={successHeight} fill="#54d68a"><title>{transcriptionChartTime(row.start, report.rangeStart, report.rangeEnd)} · {row.successes} succeeded</title></rect><rect x={barX} y={bottom - successHeight - failureHeight} width={barWidth} height={failureHeight} fill="#f05d5e"><title>{transcriptionChartTime(row.start, report.rangeStart, report.rangeEnd)} · {row.failures} failed</title></rect></g>; })}{transcriptionChartLabels(report.rangeStart, report.rangeEnd).map(timestamp => <text className="chart-label" textAnchor={timestamp === report.rangeStart ? "start" : timestamp === report.rangeEnd ? "end" : "middle"} x={left + (timestamp - report.rangeStart) / Math.max(1, report.rangeEnd - report.rangeStart) * (right - left)} y="198" key={timestamp}>{roundedTranscriptionChartTime(timestamp, report.rangeStart, report.rangeEnd)}</text>)}</svg><Legend items={[["Succeeded", "#54d68a"], ["Failed", "#f05d5e"]]} /></div>;
}

function AiTokenTimelineChart({ report }: { report: TokenUsageReport }) {
  const left = 44;
  const right = 664;
  const top = 18;
  const bottom = 170;
  const max = Math.max(1, ...report.byTime.map(row => row.promptTokens + row.completionTokens));
  const slot = (right - left) / Math.max(1, Math.ceil((report.rangeEnd - report.rangeStart) / report.bucketSeconds));
  const barWidth = Math.max(2, slot - 2);
  const x = (start: number) => left + (start - report.rangeStart) / Math.max(1, report.rangeEnd - report.rangeStart) * (right - left);
  const height = (value: number) => value / max * (bottom - top);
  return <div className="card tr-chart-card ai-usage-chart-card"><h3>Token Use</h3><p className="muted">Input/context and generated output tokens per bucket.</p><svg className="chart" viewBox="0 0 690 215" preserveAspectRatio="none" role="img" aria-label="AI token use over time"><line className="axis" x1={left} y1={top} x2={left} y2={bottom}/><line className="axis" x1={left} y1={bottom} x2={right} y2={bottom}/><text className="chart-label" x="3" y="23">{formatCompact(max)}</text><text className="chart-label" x="24" y="174">0</text>{report.byTime.map(row => { const promptHeight = height(row.promptTokens); const completionHeight = height(row.completionTokens); const barX = x(row.start); return <g key={row.start}><rect x={barX} y={bottom - promptHeight} width={barWidth} height={promptHeight} fill="var(--accent)"><title>{transcriptionChartTime(row.start, report.rangeStart, report.rangeEnd)} · {row.promptTokens.toLocaleString()} prompt tokens</title></rect><rect x={barX} y={bottom - promptHeight - completionHeight} width={barWidth} height={completionHeight} fill="#8f7cf4"><title>{transcriptionChartTime(row.start, report.rangeStart, report.rangeEnd)} · {row.completionTokens.toLocaleString()} completion tokens</title></rect></g>; })}{transcriptionChartLabels(report.rangeStart, report.rangeEnd).map(timestamp => <text className="chart-label" textAnchor={timestamp === report.rangeStart ? "start" : timestamp === report.rangeEnd ? "end" : "middle"} x={left + (timestamp - report.rangeStart) / Math.max(1, report.rangeEnd - report.rangeStart) * (right - left)} y="198" key={timestamp}>{roundedTranscriptionChartTime(timestamp, report.rangeStart, report.rangeEnd)}</text>)}</svg><Legend items={[["Prompt", "var(--accent)"], ["Completion", "#8f7cf4"]]} /></div>;
}

function RemoteBandwidthPanel({ report, rangeHours, onRangeHoursChange, onPageChange }: { report: RemoteBandwidthReport | null; rangeHours: number; onRangeHoursChange: (hours: number) => void; onPageChange: (page: number) => void }) {
  if (!report) return <div className="trouble-panel"><div className="card">Loading bandwidth usage...</div></div>;
  const totalPages = Math.max(1, Math.ceil(report.entryTotal / Math.max(1, report.entryPageSize)));
  const pageSafe = Math.min(report.entryPage, totalPages);
  const rows = report.entries;
  const usageSummary = (title: string, summary: RemoteBandwidthReport["summary"]) =>
    <Kpi label={title} value={formatBytes(summary.totalBytes)} status={summary.missingAudioFiles > 0 ? "warning" : "ok"} subtext={`${summary.requests.toLocaleString()} request(s), ${formatBytes(summary.requestBytes)} up, ${formatBytes(summary.responseBytes)} down`} />;

  return <div className="trouble-panel token-usage-panel">
    <SystemPageHeaderControls><SystemHistoryWindow value={rangeHours} label="Bandwidth window" onChange={onRangeHoursChange} /></SystemPageHeaderControls>
    <div className="audit-kpis">
      {usageSummary("Selected Range", report.summary)}
      {usageSummary("This Month", report.monthlySummary)}
      {usageSummary("All Time", report.allTimeSummary)}
    </div>
    <section className="system-content-section">
      <SystemSectionHeader title="Bandwidth Over Time" description={`PizzaWave-attributed transfer and request volume in ${report.bucketSeconds / 3600}-hour buckets.`} />
      <div className="tr-chart-grid bandwidth-chart-grid">
        <BandwidthTimelineChart report={report} value="bytes" />
        <BandwidthTimelineChart report={report} value="requests" />
      </div>
    </section>
    <section className="system-content-section">
      <SystemSectionHeader title="Usage by Activity" description="Remote transcription and AI traffic attributed by the PizzaWave workflow that produced it." />
      <BandwidthBarChart title="Transferred Bytes and Requests" rows={report.byActivity} />
    </section>
    <details className="card bandwidth-ledger">
      <summary>Bandwidth Activity <small>{report.entryTotal.toLocaleString()} request{report.entryTotal === 1 ? "" : "s"} in this window</small></summary>
      <div className="jobs-card-head bandwidth-ledger-head">
        <p className="muted">Request-level upload, download, and estimation evidence.</p>
        <div className="pagination-row table-top-pagination">
          <button disabled={pageSafe <= 1} onClick={() => onPageChange(1)}>First</button>
          <button disabled={pageSafe <= 1} onClick={() => onPageChange(pageSafe - 1)}>Prev</button>
          <span>Page {pageSafe} of {totalPages}</span>
          <button disabled={pageSafe >= totalPages} onClick={() => onPageChange(pageSafe + 1)}>Next</button>
          <button disabled={pageSafe >= totalPages} onClick={() => onPageChange(totalPages)}>Last</button>
        </div>
      </div>
      <div className="bandwidth-ledger-table-wrap"><table className="table jobs-table ai-usage-table"><thead><tr><th>Time</th><th>Activity</th><th>Upload</th><th>Download</th><th>Total</th><th>Basis</th></tr></thead><tbody>{rows.map((row, index) => <tr key={`${row.timestampUtc}-${row.activity}-${index}`}>
        <td>{new Date(row.timestampUtc).toLocaleString()}</td>
        <td>{row.activity}</td>
        <td>{formatBytes(row.requestBytes)}</td>
        <td>{formatBytes(row.responseBytes)}</td>
        <td>{formatBytes(row.totalBytes)}</td>
        <td>{row.basis}</td>
      </tr>)}</tbody></table></div>
    </details>
    <details className="card"><summary>Measurement basis and technical provenance</summary><p>{report.notes}</p><p className="muted">Source: {report.ledger}</p><p className="muted">Configured endpoints: {report.aiEndpoint || "AI local/not configured"}; {report.transcriptionEndpoint || "transcription local/not configured"}</p></details>
  </div>;
}

function bandwidthActivityColor(activity: string) {
  if (activity.toLowerCase().includes("transcription")) return "var(--accent)";
  if (activity.toLowerCase().includes("ai")) return "#8f7cf4";
  return "#7f8c98";
}

function BandwidthTimelineChart({ report, value }: { report: RemoteBandwidthReport; value: "bytes" | "requests" }) {
  const left = 44;
  const right = 664;
  const top = 18;
  const bottom = 170;
  const starts = Array.from(new Set(report.byTimeActivity.map(row => row.start))).sort((a, b) => a - b);
  const activities = Array.from(new Set(report.byTimeActivity.map(row => row.activity)));
  const totals = starts.map(start => report.byTimeActivity.filter(row => row.start === start).reduce((sum, row) => sum + (value === "bytes" ? row.totalBytes : row.requests), 0));
  const max = Math.max(1, ...totals);
  const slot = (right - left) / Math.max(1, Math.ceil((report.rangeEnd - report.rangeStart) / report.bucketSeconds));
  const barWidth = Math.max(2, slot - 2);
  const x = (start: number) => left + (start - report.rangeStart) / Math.max(1, report.rangeEnd - report.rangeStart) * (right - left);
  const height = (amount: number) => amount / max * (bottom - top);
  const title = value === "bytes" ? "Data Transferred" : "Request Volume";
  return <div className="card tr-chart-card bandwidth-timeline-card"><h3>{title}</h3><p className="muted">{value === "bytes" ? "Estimated upload and response payloads" : "Attributed remote operations"} by activity.</p><svg className="chart" viewBox="0 0 690 215" preserveAspectRatio="none" role="img" aria-label={`${title} over time`}><line className="axis" x1={left} y1={top} x2={left} y2={bottom}/><line className="axis" x1={left} y1={bottom} x2={right} y2={bottom}/><text className="chart-label" x="3" y="23">{value === "bytes" ? formatBytes(max) : max.toLocaleString()}</text><text className="chart-label" x="24" y="174">0</text>{starts.map(start => { let y = bottom; return report.byTimeActivity.filter(row => row.start === start).map(row => { const amount = value === "bytes" ? row.totalBytes : row.requests; const segmentHeight = height(amount); y -= segmentHeight; return <rect key={`${start}-${row.activity}`} x={x(start)} y={y} width={barWidth} height={segmentHeight} fill={bandwidthActivityColor(row.activity)}><title>{transcriptionChartTime(start, report.rangeStart, report.rangeEnd)} · {row.activity}: {value === "bytes" ? formatBytes(amount) : `${amount.toLocaleString()} requests`}</title></rect>; }); })}{transcriptionChartLabels(report.rangeStart, report.rangeEnd).map(timestamp => <text className="chart-label" textAnchor={timestamp === report.rangeStart ? "start" : timestamp === report.rangeEnd ? "end" : "middle"} x={left + (timestamp - report.rangeStart) / Math.max(1, report.rangeEnd - report.rangeStart) * (right - left)} y="198" key={timestamp}>{roundedTranscriptionChartTime(timestamp, report.rangeStart, report.rangeEnd)}</text>)}</svg><Legend items={activities.map(activity => [activity, bandwidthActivityColor(activity)])} /></div>;
}

function BandwidthBarChart({ title, rows }: { title: string; rows: { label: string; totalBytes: number; requests: number }[] }) {
  const max = Math.max(1, ...rows.map(r => r.totalBytes));
  return <div className="card audit-table-card"><h3>{title}</h3>{rows.length ? rows.map(row => <div className="bar-row" key={row.label}><span>{row.label}</span><div className="bar"><span style={{ width: `${Math.max(2, row.totalBytes / max * 100)}%` }} /></div><span>{formatBytes(row.totalBytes)} / {row.requests}</span></div>) : <p className="muted">No remote bandwidth usage in this range.</p>}</div>;
}

function TokenBarChart({ title, rows }: { title: string; rows: { label: string; totalTokens: number; requests: number }[] }) {
  const max = Math.max(1, ...rows.map(r => r.totalTokens));
  return <div className="card audit-table-card"><h3>{title}</h3>{rows.length ? rows.map(row => <div className="bar-row" key={row.label}><span>{row.label}</span><div className="bar"><span style={{ width: `${Math.max(2, row.totalTokens / max * 100)}%` }} /></div><span>{formatCompact(row.totalTokens)} / {row.requests}</span></div>) : <p className="muted">No recorded usage.</p>}</div>;
}

function JobsPanel({ jobs, reload }: { jobs: Job[]; reload: () => Promise<void> }) {
  const [message, setMessage] = useState("");
  const [historyHours, setHistoryHours] = useState(24);
  const [historyPage, setHistoryPage] = useState(1);
  const [typeFilter, setTypeFilter] = useState("all");
  const [outcomeFilter, setOutcomeFilter] = useState("all");
  const [recoveryHours, setRecoveryHours] = useState(24);
  const [recoveryAvailable, setRecoveryAvailable] = useState<number | null>(null);
  const [recoveryBusy, setRecoveryBusy] = useState(false);
  const [recoveryToolsOpen, setRecoveryToolsOpen] = useState(() => localStorage.getItem("pizzawave-system-open-recovery-tools") === "1");
  const pageSize = 12;
  const activeJobs = jobs.filter(isActiveJob);
  const terminalJobs = jobs.filter(job => !isActiveJob(job));
  const historyCutoff = Date.now() - historyHours * 60 * 60 * 1000;
  const historyJobs = terminalJobs.filter(job => new Date(job.finishedAtUtc ?? job.createdAtUtc).getTime() >= historyCutoff);
  const operationTypes = Array.from(new Set(terminalJobs.map(job => job.type))).sort((a, b) => jobDisplayName(a).localeCompare(jobDisplayName(b)));
  const filteredHistory = historyJobs.filter(job =>
    (typeFilter === "all" || job.type === typeFilter) &&
    (outcomeFilter === "all" || job.status === outcomeFilter));
  const totalPages = Math.max(1, Math.ceil(filteredHistory.length / pageSize));
  const pageSafe = Math.min(historyPage, totalPages);
  const pageJobs = filteredHistory.slice((pageSafe - 1) * pageSize, pageSafe * pageSize);
  const runningCount = activeJobs.filter(job => job.status === "running" || job.status === "canceling").length;
  const waitingCount = activeJobs.filter(job => job.status === "queued" || job.status === "paused").length;
  const needsAttention = jobs.filter(job => jobNeedsAttention(job, jobs));
  const recentCompleted = historyJobs.filter(job => job.status === "completed").length;
  const recentFailed = historyJobs.filter(job => job.status === "failed").length;

  useEffect(() => {
    setHistoryPage(current => Math.min(current, totalPages));
  }, [totalPages]);

  useEffect(() => setHistoryPage(1), [historyHours, typeFilter, outcomeFilter]);

  useEffect(() => {
    if (!recoveryToolsOpen) return;
    localStorage.removeItem("pizzawave-system-open-recovery-tools");
    window.requestAnimationFrame(() => document.getElementById("transcription-recovery-tools")?.scrollIntoView({ block: "center", behavior: "smooth" }));
  }, [recoveryToolsOpen]);

  useEffect(() => {
    let canceled = false;
    api.request<{ hours: number; failedCalls: number }>(`/api/v1/system/transcription-recovery?hours=${recoveryHours}`)
      .then(result => { if (!canceled) setRecoveryAvailable(result.failedCalls); })
      .catch(() => { if (!canceled) setRecoveryAvailable(null); });
    return () => { canceled = true; };
  }, [recoveryHours, jobs]);

  useEffect(() => {
    const timer = window.setInterval(() => void reload(), activeJobs.length > 0 ? 2000 : 10000);
    return () => window.clearInterval(timer);
  }, [activeJobs.length, reload]);

  async function control(id: number, action: string) {
    const actionLabel = action === "cancel" ? "Stop" : label(action);
    if (["pause", "cancel"].includes(action) && !confirmAction(`${actionLabel} job ${id}?`, "This changes a running or queued background job.")) return;
    setMessage(`${actionLabel} job ${id}...`);
    try {
      await api.request(`/api/v1/jobs/${id}/control`, { method: "POST", body: JSON.stringify({ action }) });
      setMessage(`${actionLabel} sent for job ${id}`);
      await reload();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : String(error));
    }
  }

  async function startTranscriptionRecovery() {
    if (!recoveryAvailable || recoveryBusy) return;
    if (!confirmAction(`Retry ${recoveryAvailable.toLocaleString()} failed transcription(s)?`, "Recovery uses retained audio and GPU capacity. It runs one call at a time, yields while live or existing backlog work is waiting, and may still contend briefly with a live call already arriving.")) return;
    setRecoveryBusy(true);
    setMessage("Starting failed-transcription recovery...");
    try {
      const job = await api.request<Job>("/api/v1/jobs/transcription-recovery", { method: "POST", body: JSON.stringify({ hours: recoveryHours }) });
      setMessage(`Started ${jobDisplayName(job.type)} #${job.id}.`);
      await reload();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : String(error));
    } finally {
      setRecoveryBusy(false);
    }
  }

  return <div className="trouble-panel jobs-panel">
    <div className="audit-kpis runtime-kpis">
      <Kpi label="Running" value={runningCount.toLocaleString()} status="ok" subtext="Executing or finishing a safe cancellation" />
      <Kpi label="Waiting" value={waitingCount.toLocaleString()} status={waitingCount > 0 ? "neutral" : "ok"} subtext="Queued or intentionally paused" />
      <Kpi label="Needs Attention" value={needsAttention.length.toLocaleString()} status={needsAttention.length > 0 ? "error" : "ok"} subtext="Unsuperseded failures or stalled cancellation" />
      <Kpi label="Recent Completion" value={`${recentCompleted.toLocaleString()} / ${recentFailed.toLocaleString()}`} status={recentFailed > 0 ? "warning" : "ok"} subtext={`Completed / failed in the selected ${formatJobWindow(historyHours)}`} />
    </div>
    {needsAttention.length > 0 && <div className="card job-attention-card">
      <h3>Job attention required</h3>
      <p>{needsAttention.map(job => `${jobDisplayName(job.type)} #${job.id}: ${job.message || label(job.status)}`).join(" ")}</p>
      <p><strong>Next action:</strong> Open the affected job details. A later successful run of the same operation clears the earlier failure from this current-attention summary.</p>
    </div>}
    {message && <div className={message.toLowerCase().includes("fail") || message.toLowerCase().includes("error") ? "settings-message error" : "settings-message ok"}>{message}</div>}
    <div className="card jobs-card active-jobs-card">
      <div className="jobs-card-head"><div><h3>Active Jobs</h3><p>Running work is normal. Stop appears only where the producer owns a safe cancellation boundary.</p></div><span className="muted">{activeJobs.length.toLocaleString()} active</span></div>
      <div className="jobs-table-wrap"><JobsTable jobs={activeJobs} onControl={control} emptyMessage="No active jobs." /></div>
    </div>
    <details id="transcription-recovery-tools" className="card recovery-tools" open={recoveryToolsOpen} onToggle={event => setRecoveryToolsOpen(event.currentTarget.open)}>
      <summary>Recovery Tools{recoveryAvailable ? ` (${recoveryAvailable.toLocaleString()} failed transcription${recoveryAvailable === 1 ? "" : "s"} in the selected window)` : ""}</summary>
      <div className="transcription-recovery-card">
        <div><h3>Failed transcription recovery</h3><p>Optionally retry terminal engine failures that still have audio. Recovery is low priority, processes one call at a time, and yields to queued live and backlog work.</p></div>
        <div className="transcription-recovery-controls"><label>Failed-call window<select value={recoveryHours} onChange={event => setRecoveryHours(Number(event.target.value))}><option value={24}>24 hours</option><option value={48}>2 days</option><option value={168}>7 days</option><option value={720}>30 days</option></select></label><button className="danger-button" disabled={recoveryBusy || !recoveryAvailable || jobs.some(job => job.type === "transcription_failure_recovery" && isActiveJob(job))} onClick={() => void startTranscriptionRecovery()}>{recoveryBusy ? "Starting..." : `Retry ${recoveryAvailable?.toLocaleString() ?? "--"} failed`}</button></div>
        {recoveryAvailable === 0 && <p className="recovery-window-empty muted">No recoverable failures in the selected window.{recoveryHours < 720 ? ` Older failures may be available; select ${recoveryHours < 48 ? "2 days, 7 days, or 30 days" : recoveryHours < 168 ? "7 days or 30 days" : "30 days"}.` : ""}</p>}
        <small>This is intentionally not automatic or required. Recovered calls retain their original event times; notifications older than 60 minutes are suppressed.</small>
      </div>
    </details>
    <div className="card jobs-card job-history-card">
      <div className="jobs-card-head job-history-head">
        <div><h3>Job History</h3><p>Completed job records are retained for 30 days. Automatic maintenance appears here with setup, backup, and service operations.</p></div>
        <div className="job-history-controls">
          <label>History window<select value={historyHours} onChange={event => setHistoryHours(Number(event.target.value))}>
            <option value={24}>24 hours</option><option value={48}>2 days</option><option value={168}>7 days</option><option value={720}>30 days</option>
          </select></label>
          <label>Operation<select value={typeFilter} onChange={event => setTypeFilter(event.target.value)}><option value="all">All operations</option>{operationTypes.map(type => <option value={type} key={type}>{jobDisplayName(type)}</option>)}</select></label>
          <label>Outcome<select value={outcomeFilter} onChange={event => setOutcomeFilter(event.target.value)}><option value="all">All outcomes</option><option value="completed">Completed</option><option value="failed">Failed</option><option value="canceled">Canceled</option></select></label>
        </div>
        <span className="muted">{filteredHistory.length.toLocaleString()} in the selected {formatJobWindow(historyHours)}</span>
        {totalPages > 1 && <div className="pagination-row table-top-pagination">
          <button disabled={pageSafe <= 1} onClick={() => setHistoryPage(1)}>First</button>
          <button disabled={pageSafe <= 1} onClick={() => setHistoryPage(pageSafe - 1)}>Prev</button>
          <span>Page {pageSafe} of {totalPages}</span>
          <button disabled={pageSafe >= totalPages} onClick={() => setHistoryPage(pageSafe + 1)}>Next</button>
          <button disabled={pageSafe >= totalPages} onClick={() => setHistoryPage(totalPages)}>Last</button>
        </div>}
      </div>
      <div className="jobs-table-wrap"><JobsTable jobs={pageJobs} onControl={control} emptyMessage={`No job history in the selected ${formatJobWindow(historyHours)}${typeFilter !== "all" || outcomeFilter !== "all" ? " with the current filters" : ""}.`} /></div>
    </div>
  </div>;
}

function QueuePanel({ engineHealth, ingestBusy, ingestMessage, onSetIngestPaused, refreshToken }: { engineHealth: EngineHealth | null; ingestBusy: boolean; ingestMessage: string; onSetIngestPaused: (pause: boolean, untilQueueClear?: boolean) => Promise<void>; refreshToken: number }) {
  const [pendingPage, setPendingPage] = useState(1);
  const queueResource = usePersistentRefresh({
    key: `system-queue|${engineHealth?.serverTimeUtc ?? "initial"}|${refreshToken}`,
    enabled: true,
    load: () => api.request<QueueSnapshot>("/api/v1/system/queue")
  });
  const q = queueResource.data;
  const depth = q?.queueDepth ?? engineHealth?.queueDepth ?? 0;
  const pending = q?.pendingTranscriptions ?? engineHealth?.pendingTranscriptions ?? 0;
  const live = q?.liveQueueDepth ?? engineHealth?.liveQueueDepth ?? 0;
  const priority = q?.priorityLiveQueueDepth ?? engineHealth?.priorityLiveQueueDepth ?? 0;
  const backlog = q?.backlogQueueDepth ?? engineHealth?.backlogQueueDepth ?? 0;
  const deferred = q?.deferredLiveQueueDepth ?? engineHealth?.deferredLiveQueueDepth ?? 0;
  const standardLive = Math.max(0, live - priority - deferred);
  const ingested = q?.recentIngestPerMinute ?? engineHealth?.recentIngestPerMinute ?? 0;
  const recentCallsIngested = q?.recentCallsIngested ?? engineHealth?.recentCallsIngested ?? 0;
  const recentAudioSecondsIngested = q?.recentAudioSecondsIngested ?? engineHealth?.recentAudioSecondsIngested ?? 0;
  const audioIn = q?.recentAudioSecondsIngestedPerMinute ?? engineHealth?.recentAudioSecondsIngestedPerMinute ?? 0;
  const audioOut = q?.recentAudioSecondsTranscribedPerMinute ?? engineHealth?.recentAudioSecondsTranscribedPerMinute ?? 0;
  const throughputWindowMinutes = q?.throughputWindowMinutes ?? engineHealth?.throughputWindowMinutes ?? 10;
  const pendingAudioSeconds = q?.pendingAudioSeconds ?? engineHealth?.pendingAudioSeconds ?? 0;
  const pressure = Boolean(q?.queueUnderPressure || engineHealth?.queueUnderPressure);
  const ingest = q?.ingest ?? engineHealth?.ingest;
  const queueState = ingest?.paused ? "Ingest Paused" : depth <= 0 ? "OK" : pressure ? "Pressure" : audioOut >= audioIn ? "Draining" : "Growing";
  const queueStatus = queueState === "OK" || queueState === "Draining" ? "ok" : queueState === "Growing" ? "warning" : "error";
  const liveTrActivity = engineHealth?.liveTrActivity;
  const etaMinutes = pendingAudioSeconds > 0 && audioOut > audioIn ? pendingAudioSeconds / Math.max(0.1, audioOut - audioIn) : 0;
  const ingestStatus = liveTrActivity?.stale || ingest?.paused ? "error" : recentCallsIngested > 0 ? "ok" : "neutral";
  const ingestSubtext = liveTrActivity?.stale
    ? liveTrActivity.message
    : `${recentCallsIngested.toLocaleString()} call${recentCallsIngested === 1 ? "" : "s"} / ${recentAudioSecondsIngested.toLocaleString()} audio seconds over ${throughputWindowMinutes}m`;
  const aiPauseNote = q?.aiWorkBlockedReason ?? engineHealth?.aiWorkBlockedReason;
  const queueCondition = ingest?.paused
    ? {
      tone: "error",
      title: "Live ingest is paused",
      detail: `New incoming calls are being discarded while Trunk Recorder continues running. ${(ingest.droppedCallsThisPause ?? 0).toLocaleString()} call(s) have been dropped during this pause.`,
      action: ingest.untilQueueClear ? "PizzaWave will resume ingest automatically when the queue clears." : "Resume ingest when you are ready to accept new calls."
    }
    : pressure
      ? {
        tone: "error",
        title: "The transcription queue is under pressure",
        detail: `${depth.toLocaleString()} item(s) are queued against a pressure threshold of ${(q?.queuePressureThreshold ?? engineHealth?.queuePressureThreshold ?? 0).toLocaleString()}; audio is entering at ${audioIn.toFixed(0)}s/min and completing at ${audioOut.toFixed(0)}s/min.${aiPauseNote ? ` ${aiPauseNote}` : ""}`,
        action: audioOut < audioIn ? "If growth continues, pause ingest until the queue clears." : "The queue is currently draining; continue monitoring it."
      }
      : depth > 0 && audioOut < audioIn
        ? {
          tone: "warning",
          title: "The transcription backlog is growing",
          detail: `${depth.toLocaleString()} item(s) and ${formatDurationMinutes(pendingAudioSeconds / 60)} of audio are pending. Audio is entering at ${audioIn.toFixed(0)}s/min and completing at ${audioOut.toFixed(0)}s/min.${aiPauseNote ? ` ${aiPauseNote}` : ""}`,
          action: "Monitor the trend; pause ingest until clear if preserving transcription capacity becomes more important than accepting new calls."
        }
        : null;
  const pendingCalls = q?.pendingCalls ?? [];
  const pendingPageSize = 10;
  const pendingPages = Math.max(1, Math.ceil(pendingCalls.length / pendingPageSize));
  const pendingPageSafe = Math.min(Math.max(1, pendingPage), pendingPages);
  const visiblePendingCalls = pendingCalls.slice((pendingPageSafe - 1) * pendingPageSize, pendingPageSafe * pendingPageSize);
  return <div className="queue-jobs-layout">
    <SystemPageHeaderControls><div className="queue-action-bar header-controls">
      {ingest?.paused
        ? <button disabled={ingestBusy} onClick={() => void onSetIngestPaused(false)}>{ingestBusy ? "Updating..." : "Resume Live Ingest"}</button>
        : <><button className="danger-button" disabled={ingestBusy || depth <= 0} onClick={() => void onSetIngestPaused(true, true)}>{ingestBusy ? "Updating..." : "Pause Until Queue Clear"}</button><button className="danger-button" disabled={ingestBusy} onClick={() => void onSetIngestPaused(true, false)}>Pause Until Resumed</button></>}
      <span className={ingest?.paused ? "section-status error" : "section-status ok"}>{ingest?.paused ? "Paused" : "Accepting Calls"}</span>
    </div></SystemPageHeaderControls>
    <div className="card queue-card">
      <div className="jobs-card-head">
        <h3>Transcription Queue</h3>
        <span className={`job-status status-${queueState === "Pressure" || queueState === "Growing" || queueState === "Ingest Paused" ? "failed" : queueState === "Draining" ? "running" : "completed"}`}>{queueState}</span>
      </div>
      <PanelLoadState label="queue details" state={queueResource.state} hasData={Boolean(q)} onRetry={queueResource.refresh} />
      <div className="queue-ingest-note">
        <small>Pausing discards new incoming calls; Trunk Recorder continues running.</small>
        {ingestMessage && <span className={ingestMessage.toLowerCase().includes("fail") ? "settings-message error" : "settings-message ok"}>{ingestMessage}</span>}
      </div>
      <div className="audit-kpis queue-kpis">
        <Kpi label="Queued" value={depth.toLocaleString()} status={queueStatus} subtext={`${pending.toLocaleString()} pending in database`} />
        <Kpi label="Pending Audio" value={formatDurationMinutes(pendingAudioSeconds / 60)} status={queueStatus} subtext={`${pendingAudioSeconds.toLocaleString()} audio seconds queued`} />
        <Kpi label="Live Ingest" value={`${ingested.toFixed(1)}/min`} status={ingestStatus} subtext={ingestSubtext} />
        <Kpi label="Audio Throughput" value={`${audioOut.toFixed(0)}s/min`} status={depth > 0 && audioOut < audioIn ? "warning" : "ok"} subtext={`${audioIn.toFixed(0)}s/min in over ${throughputWindowMinutes}m`} />
        <Kpi label="ETA" value={etaMinutes > 0 ? formatDurationMinutes(etaMinutes) : depth > 0 ? "Unknown" : "Clear"} subtext={depth > 0 && audioOut <= audioIn ? "Queue is not currently draining faster than ingest" : "Based on recent net audio drain"} />
      </div>
      {queueCondition && <div className={`card queue-condition queue-condition-${queueCondition.tone}`}>
        <h3>{queueCondition.title}</h3>
        <p>{queueCondition.detail}</p>
        <p><strong>Next action:</strong> {queueCondition.action}</p>
      </div>}
      <div className="card queue-composition-card">
        <h3>Queue Composition</h3>
        <table className="table compact-table queue-composition-table"><thead><tr><th>Lane</th><th>Count</th><th>Purpose</th></tr></thead><tbody>
          <tr><td>Priority live</td><td>{priority.toLocaleString()}</td><td>New live calls promoted while the live queue is under pressure.</td></tr>
          <tr><td>Standard live</td><td>{standardLive.toLocaleString()}</td><td>Normal incoming calls waiting for live transcription workers.</td></tr>
          <tr><td>Deferred live</td><td>{deferred.toLocaleString()}</td><td>Configured deferred talkgroups waiting behind standard live work.</td></tr>
          <tr><td>Backlog</td><td>{backlog.toLocaleString()}</td><td>Imported, retry, or older persisted calls assigned to backlog workers.</td></tr>
          <tr><td>Database pending</td><td>{pending.toLocaleString()}</td><td>Persisted calls without completed transcription; this can overlap the in-memory lanes.</td></tr>
        </tbody></table>
      </div>
      {pendingCalls.length > 0 && <div className="card queue-pending-card">
        <div className="queue-detail-head"><h3>Oldest Pending Transcriptions</h3><span className="muted">Showing the oldest {pendingCalls.length.toLocaleString()} of {pending.toLocaleString()}</span></div>
        {pendingPages > 1 && <div className="pagination-row table-top-pagination">
          <button disabled={pendingPageSafe <= 1} onClick={() => setPendingPage(1)}>First</button>
          <button disabled={pendingPageSafe <= 1} onClick={() => setPendingPage(pendingPageSafe - 1)}>Prev</button>
          <span>Page {pendingPageSafe} of {pendingPages}</span>
          <button disabled={pendingPageSafe >= pendingPages} onClick={() => setPendingPage(pendingPageSafe + 1)}>Next</button>
          <button disabled={pendingPageSafe >= pendingPages} onClick={() => setPendingPage(pendingPages)}>Last</button>
        </div>}
        <table className="table compact-table queue-pending-table"><thead><tr><th>Age</th><th>System</th><th>Talkgroup</th><th>Category</th><th>Source</th></tr></thead><tbody>{visiblePendingCalls.map(call => <tr key={call.callId}>
          <td>{formatPendingAge(call.startTime)}</td><td>{call.systemShortName}</td><td>{call.talkgroupName || `TG ${call.talkgroup}`}</td><td>{label(call.category)}</td><td>{call.isImported ? "Imported/offline" : "Live"}</td>
        </tr>)}</tbody></table>
      </div>}
    </div>
  </div>;
}

function JobsTable({ jobs, onControl, emptyMessage }: { jobs: Job[]; onControl: (id: number, action: string) => Promise<void>; emptyMessage: string }) {
  const [openJobId, setOpenJobId] = useState<number | null>(null);
  if (!jobs.length) return <div className="jobs-empty-state"><strong>{emptyMessage}</strong><span>Jobs are created by setup and calibration, backups, service operations, and automatic maintenance.</span></div>;
  return <table className="table jobs-table">
    <thead><tr><th>Operation</th><th>Status</th><th>Progress</th><th>Timing</th><th>Result</th><th>Actions</th></tr></thead>
    <tbody>{jobs.flatMap(job => {
      const supported = new Set(job.supportedOperations ?? []);
      const open = openJobId === job.id;
      return [<tr key={job.id}>
        <td><strong>{jobDisplayName(job.type)}</strong><span className="job-secondary">Job #{job.id}</span></td>
        <td><span className={`job-status status-${job.status}`}>{label(job.status)}</span></td>
        <td><JobProgress job={job} /></td>
        <td className="job-times">{jobTimingSummary(job)}<span>{jobDurationSummary(job)}</span></td>
        <td className="job-result-cell">{job.message || "No result message recorded."}</td>
        <td className="job-actions-cell"><div>
          <button onClick={() => setOpenJobId(open ? null : job.id)}>{open ? "Hide Details" : "Details"}</button>
          {supported.has("pause") && <button onClick={() => void onControl(job.id, "pause")}>Pause</button>}
          {supported.has("resume") && <button onClick={() => void onControl(job.id, "resume")}>Resume</button>}
          {supported.has("cancel") && <button className="danger-button" disabled={job.status === "canceling"} onClick={() => void onControl(job.id, "cancel")}>{job.status === "canceling" ? "Canceling" : "Stop"}</button>}
        </div></td>
      </tr>,
      ...(open ? [<tr className="job-detail-row" key={`${job.id}-details`}><td colSpan={6}><JobDetails job={job} /></td></tr>] : [])];
    })}</tbody>
  </table>;
}

function JobProgress({ job }: { job: Job }) {
  if (job.total <= 0) return <span className="muted">Not measured</span>;
  const percent = Math.min(100, Math.max(0, (job.completed + job.failed) / job.total * 100));
  return <div className="job-progress"><span>{job.completed.toLocaleString()} of {job.total.toLocaleString()}</span><progress max={100} value={percent} />{job.failed > 0 && <span className="job-progress-failed">{job.failed.toLocaleString()} failed</span>}</div>;
}

function JobDetails({ job }: { job: Job }) {
  const [logPage, setLogPage] = useState(1);
  const logsResource = usePersistentRefresh({
    key: `job-details-${job.id}-${job.status}`,
    enabled: true,
    load: () => loadCompleteJobLog(job.id)
  });
  const logs = logsResource.data ?? [];
  const pageSize = 20;
  const totalPages = Math.max(1, Math.ceil(logs.length / pageSize));
  const pageSafe = Math.min(logPage, totalPages);
  const visibleLogs = logs.slice((pageSafe - 1) * pageSize, pageSafe * pageSize);
  useEffect(() => setLogPage(current => Math.min(current, totalPages)), [totalPages]);
  return <div className="job-detail-panel">
    <div className="job-detail-facts">
      <div><span>Initiated from</span><strong>{jobOrigin(job.type)}</strong></div>
      <div><span>Request summary</span><strong>{logs[0]?.text || job.message || "No request summary recorded."}</strong></div>
      <div><span>Created</span><strong>{formatJobDate(job.createdAtUtc)}</strong></div>
      <div><span>Started</span><strong>{formatJobDate(job.startedAtUtc)}</strong></div>
      <div><span>Finished</span><strong>{formatJobDate(job.finishedAtUtc)}</strong></div>
      <div><span>Cancellation behavior</span><strong>{jobCancellationBehavior(job)}</strong></div>
    </div>
    <PanelLoadState label={`logs for job ${job.id}`} state={logsResource.state} hasData={Boolean(logsResource.data)} onRetry={logsResource.refresh} />
    <div className="job-log-head"><h4>Raw Job Log</h4>{totalPages > 1 && <div className="pagination-row table-top-pagination"><button disabled={pageSafe <= 1} onClick={() => setLogPage(pageSafe - 1)}>Prev</button><span>Page {pageSafe} of {totalPages}</span><button disabled={pageSafe >= totalPages} onClick={() => setLogPage(pageSafe + 1)}>Next</button></div>}</div>
    {visibleLogs.length ? <table className="table compact-table job-log-table"><thead><tr><th>Time</th><th>Stream</th><th>Entry</th></tr></thead><tbody>{visibleLogs.map(log => <tr key={log.id}><td>{formatJobDate(log.timestampUtc)}</td><td><span className={`section-status ${log.stream === "error" ? "error" : log.stream === "warning" ? "warning" : "ok"}`}>{label(log.stream)}</span></td><td>{log.text}</td></tr>)}</tbody></table> : <span className="muted">No job log entries were recorded.</span>}
  </div>;
}

async function loadCompleteJobLog(jobId: number) {
  const logs: JobLog[] = [];
  let afterId = 0;
  for (let page = 0; page < 20; page += 1) {
    const batch = await api.request<JobLog[]>(`/api/v1/jobs/${jobId}/logs?afterId=${afterId}`);
    logs.push(...batch);
    if (batch.length < 500) break;
    afterId = batch[batch.length - 1].id;
  }
  return logs;
}

function isActiveJob(job: Job) {
  return job.status === "queued" || job.status === "running" || job.status === "paused" || job.status === "canceling";
}

function jobNeedsAttention(job: Job, allJobs: Job[]) {
  if (job.status === "canceling") {
    const updated = new Date(job.updatedAtUtc ?? job.createdAtUtc).getTime();
    return Number.isFinite(updated) && Date.now() - updated > 5 * 60 * 1000;
  }
  if (job.status !== "failed") return false;
  return !allJobs.some(other => other.type === job.type && other.status === "completed" && other.id > job.id);
}

function jobDisplayName(type: string) {
  const names: Record<string, string> = {
    system_backup: "System Backup",
    system_storage_maintenance: "Automatic Storage Maintenance",
    system_restart_tr: "Restart Trunk Recorder",
    system_stop_tr: "Stop Trunk Recorder",
    system_restart_qdrant: "Restart Vector Search",
    setup_restart_pizzad: "Restart PizzaWave",
    setup_tr_calibration_sweep: "TR Calibration Sweep",
    setup_tr_source_build: "Build Trunk Recorder",
    setup_backup_existing_tr: "Back Up Existing TR",
    setup_prepare_existing_tr: "Prepare Existing TR",
    setup_remove_legacy_apps: "Remove Legacy Apps",
    setup_lmstudio_prime: "Prepare LM Studio",
    setup_qdrant_prime: "Prepare Vector Search",
    setup_faster_whisper_prime: "Prepare Faster Whisper",
    setup_sdr_prime: "Prepare SDR Tools",
    setup_diagnostic_tools_prime: "Prepare Diagnostic Tools",
    transcription_failure_recovery: "Failed Transcription Recovery"
  };
  return names[type] ?? label(type);
}

function jobOrigin(type: string) {
  if (type === "transcription_failure_recovery") return "System / Jobs";
  if (type === "system_backup") return "System / Backup";
  if (type.startsWith("system_restart_") || type === "system_stop_tr" || type === "setup_restart_pizzad") return "System / Runtime / Services";
  if (type.includes("maintenance")) return "Automatic maintenance";
  if (type.includes("calibration") || type.includes("sdr") || type.includes("tr_")) return "Radio Setup";
  if (type.startsWith("setup_")) return "Setup";
  return "PizzaWave background operation";
}

function jobCancellationBehavior(job: Job) {
  if (job.type === "transcription_failure_recovery") return "Stop prevents additional failed calls from being queued. One in-flight transcription may finish.";
  if (job.type === "setup_tr_calibration_sweep") return "Stop ends the calibration process tree immediately and restores Trunk Recorder.";
  if (job.type === "system_backup") return "Stop cancels archive creation and records any partial-file cleanup outcome.";
  if (job.type.startsWith("setup_") || job.type.startsWith("system_")) return "Stop waits for the current package or install transaction to reach a safe cancellation boundary.";
  return "This job producer does not declare a safe cancellation operation.";
}

function jobTimingSummary(job: Job) {
  if (isActiveJob(job)) return job.startedAtUtc ? `Started ${formatJobDate(job.startedAtUtc)}` : `Queued ${formatJobDate(job.createdAtUtc)}`;
  return job.finishedAtUtc ? `Finished ${formatJobDate(job.finishedAtUtc)}` : `Created ${formatJobDate(job.createdAtUtc)}`;
}

function jobDurationSummary(job: Job) {
  const start = new Date(job.startedAtUtc ?? job.createdAtUtc).getTime();
  const end = job.finishedAtUtc ? new Date(job.finishedAtUtc).getTime() : Date.now();
  if (!Number.isFinite(start) || !Number.isFinite(end) || end < start) return "Duration unavailable";
  const seconds = Math.max(0, Math.round((end - start) / 1000));
  if (seconds < 60) return `${seconds}s duration`;
  if (seconds < 3600) return `${Math.round(seconds / 60)}m duration`;
  return `${Math.floor(seconds / 3600)}h ${Math.round(seconds % 3600 / 60)}m duration`;
}

function formatJobWindow(hours: number) {
  return hours === 24 ? "24 hours" : hours === 48 ? "2 days" : hours === 168 ? "7 days" : hours === 720 ? "30 days" : `${hours} hours`;
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

function formatPendingAge(startTime: number) {
  return formatDurationMinutes(Math.max(0, (Date.now() / 1000 - startTime) / 60));
}

function TrConfigEditorPanel({
  reload,
  onOpenRadioSetup,
  rawOnly = false,
  applyLabel = "Save & Restart",
  onApplied,
  applyOverride,
  loadEditor,
  workspaceDraftLabel
}: {
  reload: () => Promise<void>;
  onOpenRadioSetup?: () => void;
  rawOnly?: boolean;
  applyLabel?: string;
  onApplied?: () => Promise<void>;
  applyOverride?: (configText: string) => Promise<void>;
  loadEditor?: () => Promise<TrConfigEditor>;
  workspaceDraftLabel?: string;
}) {
  const [editor, setEditor] = useState<TrConfigEditor | null>(null);
  const [configText, setConfigText] = useState("");
  const [message, setMessage] = useState("");
  const [busy, setBusy] = useState<"" | "load" | "save" | "draft" | "rr">("load");
  const [rrSid, setRrSid] = useState("");
  const [rrSites, setRrSites] = useState("");
  const [rrCandidate, setRrCandidate] = useState<SetupTrConfigDraft | null>(null);
  const lastSavedDraftRef = useRef("");

  useEffect(() => {
    let canceled = false;
    setBusy("load");
    (loadEditor ? loadEditor() : api.request<TrConfigEditor>("/api/v1/system/tr-config/editor"))
      .then(result => {
        if (canceled) return;
        setEditor(result);
        setConfigText(result.configJson);
        lastSavedDraftRef.current = result.configJson;
        setMessage(!rawOnly && result.hasDraft ? `${workspaceDraftLabel || "Config draft"} loaded.` : "");
      })
      .catch(error => !canceled && setMessage(error instanceof Error ? error.message : "Unable to load TR config."))
      .finally(() => !canceled && setBusy(""));
    return () => { canceled = true; };
  }, []);

  useEffect(() => {
    const normalizedText = normalizeDraftText(configText);
    if (loadEditor) return;
    if (!editor || busy === "load" || normalizedText === lastSavedDraftRef.current) return;
    const handle = window.setTimeout(() => {
      api.request<TrConfigEditor>("/api/v1/system/tr-config/editor/draft", {
        method: "POST",
        body: JSON.stringify({ configJson: configText })
      })
        .then(result => {
          lastSavedDraftRef.current = normalizedText;
          setEditor(result);
          setMessage(result.parseOk ? "Draft autosaved." : `Draft saved, but JSON is invalid: ${result.parseMessage}`);
        })
        .catch(error => setMessage(error instanceof Error ? error.message : "Draft autosave failed."));
    }, 900);
    return () => window.clearTimeout(handle);
  }, [configText, editor, busy]);

  const parsed = useMemo(() => parseTrConfig(configText), [configText]);
  const parseError = parsed.ok ? "" : parsed.error;
  const root = parsed.ok ? parsed.value : null;
  const systems = Array.isArray(root?.systems) ? root!.systems : [];
  const sources = Array.isArray(root?.sources) ? root!.sources : [];

  function updateRoot(mutator: (draft: any) => void) {
    const base = parsed.ok && root ? cloneJson(root) : {};
    mutator(base);
    setConfigText(JSON.stringify(base, null, 2));
  }

  function updateSystem(index: number, field: string, value: any) {
    updateRoot(draft => {
      draft.systems = Array.isArray(draft.systems) ? draft.systems : [];
      draft.systems[index] = { ...(draft.systems[index] ?? {}), [field]: value };
    });
  }

  function updateSource(index: number, field: string, value: any) {
    updateRoot(draft => {
      draft.sources = Array.isArray(draft.sources) ? draft.sources : [];
      draft.sources[index] = { ...(draft.sources[index] ?? {}), [field]: value };
    });
  }

  async function draftFromRadioReference() {
    if (!rrSid.trim()) return;
    setBusy("rr");
    setMessage("");
    try {
      const draft = await api.request<SetupTrConfigDraft>("/api/v1/setup/tr-config/draft", {
        method: "POST",
        body: JSON.stringify({
          radioReferenceSid: rrSid.trim(),
          siteNames: rrSites.trim()
        })
      });
      setRrCandidate(draft);
      setMessage(`RadioReference candidate ready: ${draft.diagnostics}`);
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "RadioReference draft failed.");
    } finally {
      setBusy("");
    }
  }

  async function saveAndRestart() {
    if (!confirmAction(`${applyLabel} TR config?`, "This writes the active trunk-recorder config, creates the normal backup, and briefly interrupts live capture.")) return;
    setBusy("save");
    setMessage("");
    try {
      if (applyOverride) {
        await applyOverride(configText);
        setMessage("Applied targeted Setup source changes.");
        return;
      }
      const result = await api.request<TrConfigEditorApplyResult>("/api/v1/system/tr-config/editor/apply", {
        method: "POST",
        body: JSON.stringify({ configJson: configText })
      });
      setEditor(result.editor);
      setConfigText(result.editor.configJson);
      lastSavedDraftRef.current = result.editor.configJson;
      setMessage(result.message + (result.restartJob ? ` Restart job ${result.restartJob.id}.` : ""));
      await reload();
      if (result.ok && onApplied) {
        setBusy("");
        await onApplied();
      }
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Save and restart failed.");
    } finally {
      setBusy("");
    }
  }

  if (busy === "load" && !editor) return <div className="card">Loading TR config...</div>;

  if (rawOnly) return <div className="tr-config-editor raw-review">
    <div className="tr-config-editor-actions raw-review-actions">
      <button className="danger-button" disabled={busy === "save" || !!parseError} onClick={() => void saveAndRestart()}>{busy === "save" ? "Applying..." : applyLabel}</button>
    </div>
    {message && <p className={message.toLowerCase().includes("invalid") || message.toLowerCase().includes("fail") ? "settings-message error" : "settings-message ok"}>{message}</p>}
    {parseError && <p className="settings-message error">Raw JSON is invalid: {parseError}</p>}
    {parsed.ok && <TrConfigReviewCoverage systems={systems} sources={sources} />}
    <div className="tr-config-raw review-only">
      <textarea aria-label="Setup TR config JSON" value={configText} spellCheck={false} onChange={e => setConfigText(e.target.value)} />
    </div>
  </div>;

  return <div className="tr-config-editor">
    <div className="tr-config-editor-toolbar">
      <div>
        <h3>Active TR Config</h3>
        <div className="muted">Live: {editor?.livePath || "--"}</div>
        {editor?.hasDraft && <div className="settings-message">{rawOnly ? "Setup draft" : "Draft"}: {editor.draftPath}</div>}
      </div>
      <div className="tr-config-editor-actions">
        {onOpenRadioSetup && <button type="button" onClick={onOpenRadioSetup}>Open Setup</button>}
        <button className="danger-button" disabled={busy === "save" || !!parseError} onClick={() => void saveAndRestart()}>{busy === "save" ? "Applying..." : applyLabel}</button>
      </div>
    </div>
    {onOpenRadioSetup && <div className="setup-note">This is the expert config-draft surface. For new SDRs, site changes, RF path changes, or validation work, use Setup.</div>}
    {message && <p className={message.toLowerCase().includes("invalid") || message.toLowerCase().includes("fail") ? "settings-message error" : "settings-message ok"}>{message}</p>}
    {parseError && <p className="settings-message error">Raw JSON is invalid: {parseError}</p>}
    <div className="tr-config-editor-grid">
      <div className="tr-config-controls">
        <section className="card">
          <h3>Systems</h3>
          <p className="muted">These site blocks drive Setup ground truth, source planning, health checks, and troubleshooting.</p>
          <div className="tr-config-mini-table">
            <table className="table compact-table">
              <thead><tr><th>Short name</th><th>Mod</th><th>Control channels Hz</th><th>Voice channels Hz</th><th>Talkgroups</th><th></th></tr></thead>
              <tbody>{systems.map((system: any, index: number) => <tr key={index}>
                <td><input value={system.shortName ?? ""} onChange={e => updateSystem(index, "shortName", e.target.value)} /></td>
                <td><select value={system.modulation ?? "qpsk"} onChange={e => updateSystem(index, "modulation", e.target.value)}><option value="qpsk">QPSK</option><option value="cqpsk">CQPSK</option><option value="fsk4">FSK4</option></select></td>
                <td><input value={formatNumberList(system.control_channels)} onChange={e => updateSystem(index, "control_channels", parseNumberList(e.target.value))} /></td>
                <td><input value={formatNumberList(system.channels)} onChange={e => updateSystem(index, "channels", parseNumberList(e.target.value))} /></td>
                <td><input value={system.talkgroupsFile ?? ""} onChange={e => updateSystem(index, "talkgroupsFile", e.target.value)} /></td>
                <td><button onClick={() => updateRoot(draft => draft.systems.splice(index, 1))}>Delete</button></td>
              </tr>)}</tbody>
            </table>
          </div>
          <button onClick={() => updateRoot(draft => { draft.systems = Array.isArray(draft.systems) ? draft.systems : []; draft.systems.push({ type: "p25", shortName: "site", modulation: "qpsk", control_channels: [], channels: [] }); })}>Add system</button>
        </section>
        <section className="card">
          <h3>SDR Sources</h3>
          <p className="muted">Source center and rate determine which site frequencies each receiver can cover.</p>
          <div className="tr-config-mini-table">
            <table className="table compact-table">
              <thead><tr><th>#</th><th>Device</th><th>Serial</th><th>Center Hz</th><th>Rate</th><th>Error</th><th>Gain</th><th></th></tr></thead>
              <tbody>{sources.map((source: any, index: number) => <tr key={index}>
                <td>{index}</td>
                <td><input value={source.device ?? ""} onChange={e => updateSource(index, "device", e.target.value)} /></td>
                <td><input value={source.digitalRecorders ?? ""} onChange={e => updateSource(index, "digitalRecorders", e.target.value)} /></td>
                <td><input inputMode="numeric" value={source.center ?? ""} onChange={e => updateSource(index, "center", numericOrText(e.target.value))} /></td>
                <td><input inputMode="numeric" value={source.rate ?? ""} onChange={e => updateSource(index, "rate", numericOrText(e.target.value))} /></td>
                <td><input inputMode="numeric" value={source.error ?? ""} onChange={e => updateSource(index, "error", numericOrText(e.target.value))} /></td>
                <td><input value={source.gain ?? ""} onChange={e => updateSource(index, "gain", numericOrText(e.target.value))} /></td>
                <td><button onClick={() => updateRoot(draft => draft.sources.splice(index, 1))}>Delete</button></td>
              </tr>)}</tbody>
            </table>
          </div>
          <button onClick={() => updateRoot(draft => { draft.sources = Array.isArray(draft.sources) ? draft.sources : []; draft.sources.push({ device: "rtl", center: 0, rate: 2400000, error: 0, gain: 32 }); })}>Add source</button>
        </section>
        <details className="card">
          <summary>RadioReference refresh</summary>
          <p className="muted">Draft a replacement from RadioReference, then review the JSON before applying. This reuses the Setup TR config builder.</p>
          <div className="tr-config-rr-grid">
            <label>SID <input value={rrSid} onChange={e => setRrSid(e.target.value)} /></label>
            <label>Site filters <input value={rrSites} onChange={e => setRrSites(e.target.value)} placeholder="Hamilton, Cleveland" /></label>
            <button disabled={busy === "rr" || !rrSid.trim()} onClick={() => void draftFromRadioReference()}>{busy === "rr" ? "Drafting..." : "Preview RR Candidate"}</button>
          </div>
          {rrCandidate && <div className="tr-config-candidate">
            <div className="setup-job-head">
              <div>
                <strong>RadioReference candidate</strong>
                <p className="muted">{rrCandidate.diagnostics}</p>
              </div>
              <div className="setup-button-row">
                <button onClick={() => { setConfigText(rrCandidate.configJson); setRrCandidate(null); }}>Merge into Draft</button>
                <button onClick={() => setRrCandidate(null)}>Dismiss</button>
              </div>
            </div>
            {rrCandidate.warnings.length > 0 && <div className="setup-warning-list">{rrCandidate.warnings.map(warning => <div key={warning}>{warning}</div>)}</div>}
            <pre className="command-box">{summarizeTrConfigDiff(configText, rrCandidate.configJson).join("\n")}</pre>
          </div>}
        </details>
      </div>
      <div className="tr-config-raw">
        <div className="card">
          <h3>Raw JSON</h3>
          <textarea value={configText} spellCheck={false} onChange={e => setConfigText(e.target.value)} />
        </div>
      </div>
    </div>
  </div>;
}

function TrConfigReviewCoverage({ systems, sources }: { systems: any[]; sources: any[] }) {
  const sourceWindows = sources.map((source, index): { index: number; device: string; center: number; rate: number; low: number; high: number } => {
    const center = readTrFrequencyHz(source?.center);
    const rate = Math.round(Number(source?.rate) || 0);
    const half = Math.max(0, Math.floor(rate / 2) - 50_000);
    return {
      index,
      device: String(source?.device ?? ""),
      center,
      rate,
      low: center - half,
      high: center + half
    };
  });
  const rows: { shortName: string; channels: number[]; covered: number[]; uncovered: number[] }[] = systems.map(system => {
    const channels = Array.isArray(system?.control_channels) ? system.control_channels.map(readTrFrequencyHz).filter((value: number) => value > 0) : [];
    const mapped = channels.map((channel: number) => ({
      channel,
      source: sourceWindows.find(source => source.rate > 0 && channel >= source.low && channel <= source.high)
    }));
    const covered: number[] = Array.from(new Set<number>(mapped.filter((row: { channel: number; source?: { index: number } }) => row.source).map((row: { channel: number; source?: { index: number } }) => row.source!.index)));
    const uncovered = mapped.filter((row: { channel: number; source?: { index: number } }) => !row.source).map((row: { channel: number; source?: { index: number } }) => row.channel);
    return {
      shortName: String(system?.shortName || system?.name || "<unnamed>"),
      channels,
      covered,
      uncovered
    };
  });
  return <div className="tr-config-review-coverage">
    <div className="tr-config-review-summary">
      <strong>{sources.length} TR source window{sources.length === 1 ? "" : "s"} for {systems.length} system{systems.length === 1 ? "" : "s"}</strong>
      <span>A source is an SDR tuning window. One source can cover multiple systems when their control channels are inside the same sampled span.</span>
    </div>
    <div className="tr-config-review-source-table">
      <div className="tr-config-review-source-row header"><span>Source</span><span>Center</span><span>Window</span><span>Rate</span></div>
      {sourceWindows.map(source => <div className="tr-config-review-source-row" key={source.index}>
        <span><strong>#{source.index}</strong><small>{source.device || "--"}</small></span>
        <span><code>{formatRfHz(source.center)}</code></span>
        <span>{formatRfHz(source.low)}-{formatRfHz(source.high)}</span>
        <span>{formatHz(source.rate)}</span>
      </div>)}
    </div>
    <div className="tr-config-review-table">
      <div className="tr-config-review-row header"><span>System</span><span>Control channels</span><span>Covered by source</span></div>
      {rows.map(row => <div className={row.uncovered.length ? "tr-config-review-row warning" : "tr-config-review-row"} key={row.shortName}>
        <span><strong>{row.shortName}</strong></span>
        <span>{row.channels.length ? row.channels.map(formatRfHz).join(", ") : "--"}</span>
        <span>
          {row.covered.length
            ? row.covered.map(index => <code key={index}>#{index}</code>)
            : "--"}
          {row.uncovered.length > 0 && <small>Uncovered: {row.uncovered.map(formatRfHz).join(", ")}</small>}
        </span>
      </div>)}
    </div>
  </div>;
}

function readTrFrequencyHz(value: unknown) {
  const numeric = Number(value);
  if (!Number.isFinite(numeric) || numeric <= 0) return 0;
  return Math.round(numeric > 0 && numeric < 1_000_000 ? numeric * 1_000_000 : numeric);
}

function parseTrConfig(text: string): { ok: true; value: any } | { ok: false; error: string } {
  try {
    const value = JSON.parse(text || "{}");
    if (!value || typeof value !== "object" || Array.isArray(value)) return { ok: false, error: "TR config root must be an object." };
    return { ok: true, value };
  } catch (error) {
    return { ok: false, error: error instanceof Error ? error.message : "Invalid JSON." };
  }
}

function cloneJson(value: any) {
  return JSON.parse(JSON.stringify(value ?? {}));
}

function normalizeDraftText(text: string) {
  return `${text.replace(/\r\n/g, "\n").replace(/\r/g, "\n").trimEnd()}\n`;
}

function formatNumberList(value: any) {
  return Array.isArray(value) ? value.join(", ") : "";
}

function parseNumberList(value: string) {
  return value.split(/[,\s]+/).map(part => Number(part.trim())).filter(part => Number.isFinite(part) && part > 0);
}

function summarizeTrConfigDiff(currentText: string, candidateText: string) {
  const current = parseTrConfig(currentText);
  const candidate = parseTrConfig(candidateText);
  if (!current.ok) return [`Current draft is invalid: ${current.error}`];
  if (!candidate.ok) return [`Candidate config is invalid: ${candidate.error}`];
  const lines: string[] = [];
  const currentSystems = mapByShortName(current.value.systems);
  const candidateSystems = mapByShortName(candidate.value.systems);
  for (const key of sortedUnion(Object.keys(currentSystems), Object.keys(candidateSystems))) {
    const before = currentSystems[key];
    const after = candidateSystems[key];
    if (!before && after) {
      lines.push(`+ system ${key}: ${formatNumberList(after.control_channels)} control channel(s)`);
      continue;
    }
    if (before && !after) {
      lines.push(`- system ${key}`);
      continue;
    }
    const beforeCc = formatNumberList(before.control_channels);
    const afterCc = formatNumberList(after.control_channels);
    if (beforeCc !== afterCc) lines.push(`~ system ${key} control_channels: ${beforeCc || "none"} -> ${afterCc || "none"}`);
    const beforeChannels = formatNumberList(before.channels);
    const afterChannels = formatNumberList(after.channels);
    if (beforeChannels !== afterChannels) lines.push(`~ system ${key} channels: ${beforeChannels || "none"} -> ${afterChannels || "none"}`);
  }
  const currentSources = Array.isArray(current.value.sources) ? current.value.sources : [];
  const candidateSources = Array.isArray(candidate.value.sources) ? candidate.value.sources : [];
  if (currentSources.length !== candidateSources.length) lines.push(`~ sources count: ${currentSources.length} -> ${candidateSources.length}`);
  for (let i = 0; i < Math.max(currentSources.length, candidateSources.length); i++) {
    const before = currentSources[i];
    const after = candidateSources[i];
    if (!before && after) lines.push(`+ source ${i}: ${after.device || "device"} @ ${after.center || 0} Hz`);
    else if (before && !after) lines.push(`- source ${i}: ${before.device || "device"} @ ${before.center || 0} Hz`);
    else if (before && after) {
      for (const field of ["device", "center", "rate", "gain", "error", "digitalRecorders"]) {
        if (String(before[field] ?? "") !== String(after[field] ?? "")) lines.push(`~ source ${i} ${field}: ${before[field] ?? "unset"} -> ${after[field] ?? "unset"}`);
      }
    }
  }
  return lines.length ? lines : ["No system/source differences detected."];
}

function mapByShortName(value: any) {
  const result: Record<string, any> = {};
  if (!Array.isArray(value)) return result;
  for (const row of value) result[String(row?.shortName || "(unnamed)") || "(unnamed)"] = row;
  return result;
}

function sortedUnion(left: string[], right: string[]) {
  return Array.from(new Set([...left, ...right])).sort((a, b) => a.localeCompare(b));
}

function numericOrText(value: string) {
  const trimmed = value.trim();
  if (!trimmed) return "";
  const numeric = Number(trimmed);
  return Number.isFinite(numeric) ? numeric : value;
}

function TrConfigurationSummaryView({ data, onOpenSetup }: { data: TrTroubleshoot; onOpenSetup?: (section?: string) => void }) {
  const cfg: any = data.config ?? {};
  const configSources: any[] = cfg.sourceCoverage?.sources ?? [];
  const configSystems: any[] = cfg.systems ?? [];
  const coverageSystems: any[] = cfg.sourceCoverage?.systems ?? [];
  const planIssues = data.health.sourcePlan.filter(plan => plan.isIssue);
  const assignedSiteCount = data.health.sourcePlan.filter(plan => plan.assignedSourceIndex != null).length;
  return <div className="tr-config-summary">
    <section className="system-content-section"><SystemSectionHeader title="Source Assignments" description="How configured SDR tuning windows, recorder capacity, and monitored sites fit together." meta={<><span>{data.health.sourceCoverage.length.toLocaleString()} source{data.health.sourceCoverage.length === 1 ? "" : "s"}</span><span>{assignedSiteCount.toLocaleString()} of {data.health.sourcePlan.length.toLocaleString()} sites assigned</span></>} actions={onOpenSetup ? <button onClick={() => { localStorage.setItem("pizzawave-site-setup-rf-validation-subpage", "coverage"); onOpenSetup("RF Validation"); }}>Edit in Setup</button> : null} />
      <div className="tr-source-topology">{data.health.sourceCoverage.map(source => {
        const configured = configSources.find(row => row.index === source.index);
        const assigned = data.health.sourcePlan.filter(plan => plan.assignedSourceIndex === source.index);
        return <article className="card tr-source-topology-card" key={source.index}>
          <div className="tr-source-topology-head"><div><h3>Source {source.index}</h3><span>{trDeviceLabel(source.device)}</span></div><strong>{source.digitalRecorders.toLocaleString()} recorders</strong></div>
          <div className="tr-source-topology-facts"><div><span>Center</span><strong>{source.centerMhz.toFixed(6)} MHz</strong></div><div><span>Sample rate</span><strong>{configured?.sampleRate ? `${(configured.sampleRate / 1_000_000).toFixed(1)} MHz` : `${(source.highMhz - source.lowMhz).toFixed(1)} MHz`}</strong></div><div><span>Usable window</span><strong>{configured?.lowHz ? `${(configured.lowHz / 1_000_000).toFixed(3)}–${(configured.highHz / 1_000_000).toFixed(3)} MHz` : `${source.lowMhz.toFixed(3)}–${source.highMhz.toFixed(3)} MHz`}</strong></div></div>
          <div className="tr-source-site-list">{assigned.map(plan => {
            const configSystem = configSystems.find(row => row.shortName === plan.systemShortName);
            const coverageSystem = coverageSystems.find(row => row.shortName === plan.systemShortName);
            const channels = (coverageSystem?.controlChannelsHz ?? []).map((hz: number) => `${(hz / 1_000_000).toFixed(6)} MHz`).join(", ");
            const talkgroupsFile = String(configSystem?.talkgroupsFile ?? "");
            const talkgroupsName = talkgroupsFile.split(/[\\/]/).filter(Boolean).at(-1) || "--";
            return <div key={plan.systemShortName}>
              <strong>{trSystemDisplayName(plan.systemShortName)}</strong>
              <div className="tr-source-site-facts">
                <span><small>Type</small><b>{String(configSystem?.type ?? "--").toUpperCase()}</b></span>
                <span><small>Control channel</small><b>{channels || "--"}</b></span>
                <span><small>Observed range</small><b>{plan.lowMhz.toFixed(3)}–{plan.highMhz.toFixed(3)} MHz</b></span>
              </div>
              <small title={talkgroupsFile}>Talkgroup file: {talkgroupsName}</small>
              <small>{plan.notes}</small>
            </div>;
          })}{assigned.length === 0 && <p className="muted">No monitored site is assigned to this source.</p>}</div>
        </article>;
      })}</div>
    </section>
    {(!cfg.ok || planIssues.length > 0) && <div className="card warning"><h3>Configuration needs review</h3><p>{cfg.error || planIssues.map(plan => `${trSystemDisplayName(plan.systemShortName)}: ${plan.notes}`).join(" ")}</p></div>}
  </div>;
}

function RfHealthStatusPanel({ data, onSelectSite, onSelectCategory }: { data: TrTroubleshoot; onSelectSite: (system: string) => void; onSelectCategory: (category: RfChartCategory) => void }) {
  const systems = data.health.systemSummaries ?? [];
  return <div className="tr-summary rf-health-status-panel">
    <div className="tr-site-card-grid">{systems.map(system => {
      return <article className="card tr-site-card" key={system.systemShortName} onClickCapture={() => onSelectSite(system.systemShortName)}>
        <div className="tr-site-card-head"><h3>{trSystemDisplayName(system.systemShortName)}</h3><span>Current window</span></div>
        <div className="tr-site-facts">
          <TrSiteFact label="Decode" value={system.ccSummarySamples ? `${system.ccSummaryAvgDecodeRate.toFixed(1)} msg/s` : "N/A"} caption={system.decodeAssessment.baselineValue != null ? `Local ${system.decodeAssessment.baselineValue.toFixed(1)} · strong 40 msg/s` : "40 msg/s strong reference"} onClick={() => onSelectCategory("decode")} />
          <TrSiteFact label="Zero decode" value={system.ccSummarySamples ? `${system.ccSummaryDecodeZeroPercent.toFixed(1)}%` : "N/A"} caption={system.zeroDecodeAssessment.baselineValue != null ? `Local ${system.zeroDecodeAssessment.baselineValue.toFixed(1)}% · ${system.ccSummarySamples.toLocaleString()} samples` : `${system.ccSummarySamples.toLocaleString()} summary samples`} onClick={() => onSelectCategory("decode")} />
          <TrSiteFact label="Calls" value={system.callsConcluded.toLocaleString()} caption={system.callsAssessment.baselineValue != null ? `${system.callsPerHour.toFixed(1)}/hr · local ${system.callsAssessment.baselineValue.toFixed(1)}/hr` : `${system.callsPerHour.toFixed(1)}/hr in window`} onClick={() => onSelectCategory("activity")} />
          <TrSiteFact label="No audio" value={system.noTxRecorded.toLocaleString()} caption={system.callsConcluded ? `${(system.noTxRecorded * 100 / system.callsConcluded).toFixed(1)}%${system.noAudioAssessment.baselineValue != null ? ` · local ${system.noAudioAssessment.baselineValue.toFixed(1)}%` : " of calls"}` : "no concluded calls"} onClick={() => onSelectCategory("activity")} />
          <TrSiteFact label="Retunes" value={system.retunes.toLocaleString()} caption={system.retunesAssessment.baselineValue != null ? `${system.retunesPerHour.toFixed(1)}/hr · local ${system.retunesAssessment.baselineValue.toFixed(1)}/hr` : `${system.retunesPerHour.toFixed(1)}/hr in window`} onClick={() => onSelectCategory("events")} />
        </div>
        <small className="tr-site-freshness">Latest evidence: {new Date(system.lastWindowEndUtc).toLocaleString()}</small>
      </article>;
    })}{systems.length === 0 && <div className="card"><p className="muted">No system-scoped Trunk Recorder health samples are available for this window.</p></div>}</div>
  </div>;
}

function TrSiteFact({ label: factLabel, value, caption, onClick }: { label: string; value: string; caption: string; onClick: () => void }) {
  return <button type="button" className="tr-site-fact" aria-label={`${factLabel}: ${value}. Open matching Performance charts.`} onClick={onClick}><span>{factLabel}</span><strong>{value}</strong><small>{caption}</small></button>;
}

function trSystemTone(status: string) {
  if (status === "Unavailable" || status === "Critical") return "error";
  if (status === "Needs review") return "warning";
  if (status === "Healthy") return "ok";
  return "neutral";
}

function trSystemDisplayName(value: string) {
  return value.split("-").filter(Boolean).map(word => ["ms", "etv"].includes(word.toLowerCase()) ? word.toUpperCase() : word[0]?.toUpperCase() + word.slice(1)).join(" ");
}

function trDeviceLabel(value: string) {
  return value.replace(/^airspy=/i, "Airspy ").replace(/^rtl=/i, "RTL-SDR ");
}

function TranscriptionPerformancePanel({ data, rangeHours, onRangeHoursChange, onOpenTalkgroup, onExcludeTalkgroup }: { data: TranscriptionPerformance; rangeHours: number; onRangeHoursChange: (hours: number) => void; onOpenTalkgroup: (row: { category: string; talkgroup: number }) => void; onExcludeTalkgroup: (row: TranscriptionGroup) => void }) {
  const usableStatus = transcriptionUsableStatus(data.usablePercent, data.baselineUsablePercent, data.totalCalls);
  const endpoint = data.endpointHealth;
  const emptyOutageHint = rangeHours < 168 ? ` Older outages may be available; select ${rangeHours < 48 ? "2d or Week" : "Week"}.` : "";
  return <div className="transcription-performance-panel">
    <SystemPageHeaderControls><div className="calls-performance-toolbar header-controls">
      {endpoint?.configured && <span className={`section-status ${endpoint.outageConfirmed ? "error" : endpoint.healthy ? "ok" : "info"}`}>{endpoint.outageConfirmed ? "Endpoint unavailable" : endpoint.healthy ? "Endpoint healthy" : "Checking endpoint"}</span>}
      <div className="segmented" role="group" aria-label="Transcription history window">
        {[{ hours: 24, text: "24h" }, { hours: 48, text: "2d" }, { hours: 168, text: "Week" }].map(option => <button type="button" key={option.hours} className={rangeHours === option.hours ? "active" : ""} onClick={() => onRangeHoursChange(option.hours)}>{option.text}</button>)}
      </div>
    </div></SystemPageHeaderControls>
    <details className="card transcription-outage-history"><summary>Endpoint Outage History ({data.endpointOutages?.length ?? 0} in this window)</summary>{data.endpointOutages?.length ? <div className="transcription-table-scroll"><table className="table compact-table"><thead><tr><th>Started</th><th>Duration</th><th>State</th><th>Model</th><th>Administrative email</th><th>Last evidence</th></tr></thead><tbody>{data.endpointOutages.map(outage => { const started = new Date(outage.startedAtUtc); const recovered = outage.recoveredAtUtc ? new Date(outage.recoveredAtUtc) : null; const durationMinutes = Math.max(0, ((recovered?.getTime() ?? Date.now()) - started.getTime()) / 60000); return <tr key={outage.id}><td>{started.toLocaleString()}</td><td>{formatDurationMinutes(durationMinutes)}</td><td><span className={`section-status ${recovered ? "ok" : "error"}`}>{recovered ? "Recovered" : "Active"}</span></td><td>{outage.reportedModel || outage.expectedModel || "--"}</td><td>{outage.administrativeEmailSent ? "Sent" : "Not sent"}</td><td>{outage.lastError || "--"}</td></tr>; })}</tbody></table></div> : <p className="muted">No remote transcription endpoint outages were recorded in the selected window.{emptyOutageHint}</p>}</details>
    <div className="section kpis transcription-kpis">
      <Kpi label="Transcription Completion" value={`${data.completedCalls.toLocaleString()} / ${data.totalCalls.toLocaleString()}`} status={data.totalCalls < 10 ? "neutral" : data.completionPercent >= 99 ? "ok" : data.completionPercent >= 95 ? "warning" : "error"} subtext={`${data.completionPercent.toFixed(1)}% · PizzaWave reference ≥99%`} />
      <Kpi label="Usable Transcripts" value={`${data.usableCalls.toLocaleString()} / ${data.totalCalls.toLocaleString()}`} status={usableStatus} subtext={`${data.usablePercent.toFixed(1)}% · local ${data.baselineUsablePercent.toFixed(1)}% · strong reference ≥80%`} />
      <Kpi label="Engine Failures" value={data.engineFailureCalls.toLocaleString()} status={data.totalCalls < 10 ? "neutral" : data.engineFailurePercent <= 1 ? "ok" : data.engineFailurePercent <= 5 ? "warning" : "error"} subtext={`${data.engineFailurePercent.toFixed(1)}% · expected ≤1%`} />
      <Kpi label="Unusable Audio" value={data.unusableAudioCalls.toLocaleString()} status={data.totalCalls < 10 ? "neutral" : data.unusableAudioPercent > 20 ? "error" : data.unusableAudioPercent > 10 ? "warning" : "ok"} subtext={`${data.unusableAudioPercent.toFixed(1)}% inaudible, blank, or marker-only`} />
    </div>
    <div className="transcription-chart-grid">
      <TranscriptionOutcomeChart rows={data.outcomes} rangeStart={data.rangeStart} rangeEnd={data.rangeEnd} bucketSeconds={data.bucketSeconds} />
      <TranscriptionLatencyChart rows={data.latency} rangeStart={data.rangeStart} rangeEnd={data.rangeEnd} />
    </div>
    <div className="transcription-detail-grid">
      <TranscriptionReasonTable rows={data.reasons} />
      <TranscriptionGroupTable title="Site/System Comparison" rows={data.systems} />
      <div className="transcription-detail-wide">
        <TranscriptionGroupTable title="Affected Talkgroups" rows={data.talkgroups} talkgroups samples={data.samples} onOpenTalkgroup={onOpenTalkgroup} onExcludeTalkgroup={onExcludeTalkgroup} />
      </div>
    </div>
  </div>;
}

function transcriptionUsableStatus(rate: number, baseline: number, total: number): "ok" | "warning" | "error" | "neutral" {
  if (total < 10) return "neutral";
  if (rate < 60 || (baseline > 0 && baseline - rate >= 15)) return "error";
  if (rate >= 80 && (baseline <= 0 || rate >= baseline - 5)) return "ok";
  return "warning";
}

function qualityReasonLabel(reason: string) {
  const labels: Record<string, string> = { transcription_error: "Engine failure", inaudible: "Inaudible audio", blank_audio: "Blank audio", marker_only: "Marker-only audio", empty: "Empty transcript", too_short: "Too short", numeric_noise: "Numeric noise", repetitive: "Repetitive output", low_information: "Low-information output", overexpanded: "Overexpanded output" };
  return labels[reason] ?? label(reason || "unknown");
}

function qualityReasonAction(reason: string) {
  if (reason === "transcription_error") return "Review provider availability and Queue/Jobs evidence.";
  if (["inaudible", "blank_audio", "marker_only"].includes(reason)) return "Review source audio and the affected site or talkgroup.";
  if (reason === "empty") return "Confirm audio contains speech and inspect the selected model.";
  if (reason === "too_short") return "Often brief radio traffic; review concentration before acting.";
  return "Review samples for a repeated model or audio pattern.";
}

function TranscriptionReasonTable({ rows }: { rows: TranscriptionPerformance["reasons"] }) {
  return <div className="card transcription-table-card"><h3>Outcome Breakdown</h3>{rows.length ? <table className="table transcription-reason-table"><thead><tr><th>Outcome</th><th>Calls</th><th>Share</th><th>Operator action</th></tr></thead><tbody>{rows.map(row => <tr key={row.reason}><td>{qualityReasonLabel(row.reason)}</td><td>{row.calls.toLocaleString()}</td><td>{row.sharePercent.toFixed(1)}%</td><td>{qualityReasonAction(row.reason)}</td></tr>)}</tbody></table> : <p className="muted">No unusable or failed outcomes in this window.</p>}</div>;
}

function TranscriptionGroupTable({ title, rows, talkgroups = false, samples = [], onOpenTalkgroup, onExcludeTalkgroup }: { title: string; rows: TranscriptionGroup[]; talkgroups?: boolean; samples?: QualityAuditSample[]; onOpenTalkgroup?: (row: { category: string; talkgroup: number }) => void; onExcludeTalkgroup?: (row: TranscriptionGroup) => void }) {
  const storageKey = "pizzawave-system-transcription-open-talkgroup";
  const [openGroup, setOpenGroup] = useState(() => talkgroups ? localStorage.getItem(storageKey) || "" : "");
  const [openSample, setOpenSample] = useState<number | null>(null);
  function rememberOpenGroup(key: string) {
    setOpenGroup(key);
    if (!talkgroups) return;
    if (key) localStorage.setItem(storageKey, key);
    else localStorage.removeItem(storageKey);
  }
  useEffect(() => {
    if (!talkgroups || !openGroup || !rows.some(row => `${row.systemShortName.toLowerCase()}:${row.talkgroup}` === openGroup)) return;
    let secondHandle = 0;
    const firstHandle = window.requestAnimationFrame(() => {
      secondHandle = window.requestAnimationFrame(() => {
        const row = document.querySelector<HTMLElement>(`[data-transcription-group-key="${CSS.escape(openGroup)}"]`);
        const container = row?.closest<HTMLElement>(".trouble-page.system-view");
        if (!row || !container) return;
        const top = row.getBoundingClientRect().top - container.getBoundingClientRect().top + container.scrollTop - Math.max(20, container.clientHeight * .2);
        container.scrollTo({ top: Math.max(0, top), behavior: "auto" });
      });
    });
    return () => { window.cancelAnimationFrame(firstHandle); window.cancelAnimationFrame(secondHandle); };
  }, [talkgroups, openGroup, rows]);
  return <div className="card transcription-table-card"><h3>{title}</h3>{rows.length ? <div className="transcription-table-scroll"><table className="table transcription-group-table"><thead><tr><th>{talkgroups ? "Talkgroup" : "Site/System"}</th><th>Calls</th><th>Completion</th><th>Usable</th><th>Engine failures</th><th>Unusable audio</th><th>Local usable</th>{talkgroups && <th>Actions</th>}</tr></thead><tbody>{rows.map(row => { const key = `${row.systemShortName.toLowerCase()}:${row.talkgroup}`; const evidence = samples.filter(sample => sample.systemShortName.toLowerCase() === row.systemShortName.toLowerCase() && sample.talkgroup === row.talkgroup); const problemCount = row.engineFailureCalls + row.unusableAudioCalls + row.otherQualityCalls; return <React.Fragment key={key}><tr data-transcription-group-key={key} className={talkgroups ? "clickable-table-row" : ""} tabIndex={talkgroups ? 0 : undefined} onClick={() => talkgroups && rememberOpenGroup(openGroup === key ? "" : key)} onKeyDown={event => { if (talkgroups && (event.key === "Enter" || event.key === " ")) rememberOpenGroup(openGroup === key ? "" : key); }}><td><strong>{row.label}</strong>{talkgroups && <small>{row.systemShortName} · TG {row.talkgroup} · {problemCount.toLocaleString()} problem calls</small>}</td><td>{row.totalCalls.toLocaleString()}</td><td>{row.completionPercent.toFixed(1)}%</td><td><span className={`metric-value-${transcriptionUsableStatus(row.usablePercent, row.baselineUsablePercent, row.totalCalls)}`}>{row.usablePercent.toFixed(1)}%</span></td><td>{row.engineFailureCalls.toLocaleString()} ({row.engineFailurePercent.toFixed(1)}%)</td><td>{row.unusableAudioCalls.toLocaleString()} ({row.unusableAudioPercent.toFixed(1)}%)</td><td>{row.baselineUsablePercent ? `${row.baselineUsablePercent.toFixed(1)}%` : "Insufficient"}</td>{talkgroups && <td><div className="table-action-row"><button type="button" className="tiny-button" onClick={event => { event.stopPropagation(); rememberOpenGroup(key); onOpenTalkgroup?.(row); }}>View calls</button><button type="button" className="tiny-button danger-button" onClick={event => { event.stopPropagation(); rememberOpenGroup(key); onExcludeTalkgroup?.(row); }}>Exclude</button></div></td>}</tr>{talkgroups && openGroup === key && <tr className="transcription-group-evidence-row"><td colSpan={8}><div className="transcription-group-evidence"><div className="card-heading-row"><strong>Problem call evidence</strong><span className="muted">Showing {evidence.length} of {problemCount.toLocaleString()}</span></div>{evidence.length ? <table className="table compact-table transcription-evidence-table"><thead><tr><th>Time</th><th>Classification</th><th>Source</th><th>Audio duration</th></tr></thead><tbody>{evidence.map(sample => <React.Fragment key={sample.callId}><tr className="clickable-table-row" onClick={() => setOpenSample(current => current === sample.callId ? null : sample.callId)}><td>{new Date(sample.startTime * 1000).toLocaleString()}</td><td>{qualityReasonLabel(sample.qualityReason)}</td><td>#{sample.source}</td><td>{sample.durationSeconds < 1 ? `${sample.durationSeconds.toFixed(1)}s` : formatDuration(sample.durationSeconds)}</td></tr>{openSample === sample.callId && <tr className="transcription-evidence-detail-row"><td colSpan={4}><div className="transcription-evidence-shelf"><div><strong>{qualityReasonLabel(sample.qualityReason)}</strong><span className="muted">{sample.transcriptionStatus} · call {sample.callId}</span></div><p>{sample.transcription || "No transcript was produced."}</p><PlayableAudio src={sample.audioUrl} /></div></td></tr>}</React.Fragment>)}</tbody></table> : <p className="muted">No sample rows are available for this talkgroup.</p>}</div></td></tr>}</React.Fragment>; })}</tbody></table></div> : <p className="muted">No eligible calls in this window.</p>}</div>;
}

function transcriptionChartLabels(rangeStart: number, rangeEnd: number) {
  return Array.from(new Set([0, .25, .5, .75, 1].map(ratio => Math.round(rangeStart + (rangeEnd - rangeStart) * ratio))));
}

function roundedTranscriptionChartTime(timestamp: number, rangeStart: number, rangeEnd: number) {
  return transcriptionChartTime(Math.ceil(timestamp / 3600) * 3600, rangeStart, rangeEnd);
}

function transcriptionChartTime(timestamp: number, rangeStart: number, rangeEnd: number) {
  return new Date(timestamp * 1000).toLocaleString([], rangeEnd - rangeStart <= 86400 ? { hour: "numeric", minute: "2-digit" } : { month: "numeric", day: "numeric", hour: "numeric" });
}

function TranscriptionOutcomeChart({ rows, rangeStart, rangeEnd, bucketSeconds }: { rows: TranscriptionOutcomeBucket[]; rangeStart: number; rangeEnd: number; bucketSeconds: number }) {
  const first = rangeStart - rangeStart % bucketSeconds;
  const starts = Array.from({ length: Math.max(0, Math.ceil((rangeEnd - first) / bucketSeconds)) }, (_, index) => first + index * bucketSeconds);
  const byStart = new Map(rows.map(row => [row.start, row]));
  const colors = { usable: "#54d68a", audio: "#5aa7ff", quality: "#f7c948", failure: "#ff6b5a", pending: "#9faab5" };
  const x = (timestamp: number) => 44 + (timestamp - first) / Math.max(1, rangeEnd - first) * 620;
  const width = Math.max(2, 620 / Math.max(1, starts.length) - 1);
  return <div className="card tr-chart-card transcription-chart-card"><h3>Transcription Outcomes Over Time</h3><p className="muted">Percentage of eligible calls in each {bucketSeconds / 60}-minute bucket.</p><svg className="chart" viewBox="0 0 690 215" preserveAspectRatio="none" role="img" aria-label="Transcription outcomes over time"><line className="axis" x1="44" y1="18" x2="44" y2="170"/><line className="axis" x1="44" y1="170" x2="664" y2="170"/><text className="chart-label" x="8" y="23">100%</text><text className="chart-label" x="24" y="174">0</text>{starts.map(start => { const row = byStart.get(start); const total = row?.totalCalls ?? 0; const segments = [{ key: "usable", value: row?.usableCalls ?? 0 }, { key: "audio", value: row?.unusableAudioCalls ?? 0 }, { key: "quality", value: row?.otherQualityCalls ?? 0 }, { key: "failure", value: row?.engineFailureCalls ?? 0 }, { key: "pending", value: row?.pendingCalls ?? 0 }]; let y = 170; return segments.map(segment => { const height = total ? segment.value / total * 152 : 0; y -= height; return <rect key={`${start}-${segment.key}`} x={x(start)} y={y} width={width} height={height} fill={colors[segment.key as keyof typeof colors]}><title>{transcriptionChartTime(start, rangeStart, rangeEnd)} · {qualityReasonLabel(segment.key)}: {segment.value}/{total}</title></rect>; }); })}{transcriptionChartLabels(rangeStart, rangeEnd).map(timestamp => <text className="chart-label" textAnchor={timestamp === rangeStart ? "start" : timestamp === rangeEnd ? "end" : "middle"} x={44 + (timestamp - rangeStart) / Math.max(1, rangeEnd - rangeStart) * 620} y="198" key={timestamp}>{roundedTranscriptionChartTime(timestamp, rangeStart, rangeEnd)}</text>)}</svg><Legend items={[["Usable", colors.usable], ["Unusable audio", colors.audio], ["Other quality", colors.quality], ["Engine failure", colors.failure], ["Pending", colors.pending]]} /></div>;
}

function TranscriptionLatencyChart({ rows, rangeStart, rangeEnd }: { rows: TranscriptionLatencyBucket[]; rangeStart: number; rangeEnd: number }) {
  const max = Math.max(1, ...rows.map(row => row.p95Seconds));
  const x = (timestamp: number) => 44 + (timestamp - rangeStart) / Math.max(1, rangeEnd - rangeStart) * 620;
  const y = (value: number) => 170 - value / max * 145;
  const points = (pick: (row: TranscriptionLatencyBucket) => number) => rows.map(row => `${x(row.start)},${y(pick(row))}`).join(" ");
  const scaleLabel = max >= 120 ? `${(max / 60).toFixed(1)}m` : `${max.toFixed(0)}s`;
  return <div className="card tr-chart-card transcription-chart-card"><h3>Transcription Latency Over Time</h3><p className="muted">PizzaWave ingest to completed transcription for live calls; imported/offline calls are excluded.</p><svg className="chart" viewBox="0 0 690 215" preserveAspectRatio="none" role="img" aria-label="Median and 95th percentile transcription latency over time"><line className="axis" x1="44" y1="18" x2="44" y2="170"/><line className="axis" x1="44" y1="170" x2="664" y2="170"/><text className="chart-label" x="5" y="23">{scaleLabel}</text><text className="chart-label" x="24" y="174">0</text><polyline points={points(row => row.p95Seconds)} fill="none" stroke="#f7c948" strokeWidth="2"/><polyline points={points(row => row.medianSeconds)} fill="none" stroke="#54d68a" strokeWidth="2.5"/>{rows.map(row => <circle key={row.start} cx={x(row.start)} cy={y(row.p95Seconds)} r="2.5" fill="#f7c948"><title>{transcriptionChartTime(row.start, rangeStart, rangeEnd)} · p95 {formatDuration(row.p95Seconds)} · median {formatDuration(row.medianSeconds)} · {row.calls} calls</title></circle>)}{transcriptionChartLabels(rangeStart, rangeEnd).map(timestamp => <text className="chart-label" textAnchor={timestamp === rangeStart ? "start" : timestamp === rangeEnd ? "end" : "middle"} x={44 + (timestamp - rangeStart) / Math.max(1, rangeEnd - rangeStart) * 620} y="198" key={timestamp}>{roundedTranscriptionChartTime(timestamp, rangeStart, rangeEnd)}</text>)}</svg><Legend items={[["Median", "#54d68a"], ["95th percentile", "#f7c948"]]} /></div>;
}

function QualityAuditView({ data, rangeHours }: { data: TrTroubleshoot; rangeHours: number }) {
  const audit = data.qualityAudit;
  const [previous, setPrevious] = useState<TrTroubleshoot["qualityAudit"] | null>(null);
  const [showAllSamples, setShowAllSamples] = useState(false);
  const visibleSamples = showAllSamples ? audit.samples : audit.samples.slice(0, 10);
  useEffect(() => {
    let canceled = false;
    const end = Math.floor(Date.now() / 1000) - Math.max(1, rangeHours) * 3600;
    const start = end - Math.max(1, rangeHours) * 3600;
    api.request<TrTroubleshoot>(`/api/v1/troubleshoot?start=${start}&end=${end}`)
      .then(result => { if (!canceled) setPrevious(result.qualityAudit); })
      .catch(() => { if (!canceled) setPrevious(null); });
    return () => { canceled = true; };
  }, [rangeHours]);
  const problemDelta = previous ? audit.problemPercent - previous.problemPercent : 0;
  const inaudibleDelta = previous ? audit.inaudiblePercent - previous.inaudiblePercent : 0;
  return <div className="quality-audit">
    <div className="audit-kpis">
      <Kpi label="Problem Calls" value={`${audit.problemCalls.toLocaleString()} / ${audit.totalCalls.toLocaleString()}`} status={audit.problemPercent > 25 ? "error" : audit.problemPercent > 10 ? "warning" : "ok"} subtext={`${audit.problemPercent.toFixed(1)}% poor-quality, failed, empty, short, or inaudible`} />
      <Kpi label="Inaudible Calls" value={audit.inaudibleCalls.toLocaleString()} status={audit.inaudiblePercent > 15 ? "error" : audit.inaudiblePercent > 5 ? "warning" : "ok"} subtext={`${audit.inaudiblePercent.toFixed(1)}% of calls in selected range`} />
      <Kpi label="Previous Window" value={previous ? `${problemDelta >= 0 ? "+" : ""}${problemDelta.toFixed(1)} pts` : "Loading"} status={!previous ? "neutral" : problemDelta > 5 ? "warning" : problemDelta < -5 ? "ok" : "neutral"} subtext={previous ? `problem rate vs prior ${rangeHours}h window; inaudible ${inaudibleDelta >= 0 ? "+" : ""}${inaudibleDelta.toFixed(1)} pts` : "equal lookback comparison"} />
      <Kpi label="Review Samples" value={audit.samples.length.toLocaleString()} status={audit.samples.length > 0 ? "warning" : "ok"} subtext="collapsed evidence available for review" />
    </div>
    <div className="audit-grid">
      <AuditTable title="Reasons" rows={audit.byReason} mode="reason" />
      <AuditTable title="Systems" rows={audit.bySystem} />
      <AuditTable title="Talkgroups" rows={audit.byTalkgroup} />
      <QualityAuditHourChart rows={audit.byHour} />
    </div>
    <div className="card">
      <div className="card-heading-row"><h3>Sample Problem Calls</h3>{audit.samples.length > 10 && <button onClick={() => setShowAllSamples(current => !current)}>{showAllSamples ? "Show first 10" : `Show all ${audit.samples.length}`}</button>}</div>
      <p className="muted">Open only the samples needed to confirm the dominant quality pattern.</p>
      {audit.samples.length ? visibleSamples.map(sample => <QualityAuditSampleCard sample={sample} key={sample.callId} />) : <p className="muted">No problem calls in the selected range.</p>}
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
    <PlayableAudio src={sample.audioUrl} />
  </details>;
}

function MetricTable({ title, rows, issuesFirst = false, technicalNotes = false }: { title: string; rows: TrHealthMetric[]; issuesFirst?: boolean; technicalNotes?: boolean }) {
  const issueRows = rows.filter(row => row.isIssue);
  const normalRows = rows.filter(row => !row.isIssue);
  const renderRows = (items: TrHealthMetric[]) => items.map(row => <tr className={row.isIssue ? "issue-row" : ""} key={row.metric}><td>{row.metric}</td><td>{row.value}</td><td>{technicalNotes ? <details><summary>View metrics</summary><p>{row.notes}</p></details> : row.notes}</td></tr>);
  return <div className="card"><h3>{title}</h3>{issuesFirst && issueRows.length === 0 ? <p className="settings-message ok">No issues detected.</p> : <table className="table metric-table"><thead><tr><th>Signal</th><th>Current value</th><th>Meaning</th></tr></thead><tbody>{renderRows(issuesFirst ? issueRows : rows)}</tbody></table>}{issuesFirst && normalRows.length > 0 && <details><summary>{normalRows.length} normal signal{normalRows.length === 1 ? "" : "s"}</summary><table className="table metric-table"><thead><tr><th>Signal</th><th>Current value</th><th>Meaning</th></tr></thead><tbody>{renderRows(normalRows)}</tbody></table></details>}</div>;
}

function TrHealthChartView({ chart, annotations = [], showBaselineNote = true, showTitle = true }: { chart: TrHealthChart; annotations?: SystemRecommendation["episodes"]; showBaselineNote?: boolean; showTitle?: boolean }) {
  const colors = ["#62c6ff", "#ffcf5a", "#7ee081", "#ff6b5a"];
  const rawMax = Math.max(1, ...chart.series.flatMap(s => s.values));
  const magnitude = Math.pow(10, Math.floor(Math.log10(rawMax / 4 || 1)));
  const normalized = rawMax / 4 / magnitude;
  const tickStep = (normalized <= 1.5 ? 1 : normalized <= 3 ? 2 : normalized <= 7 ? 5 : 10) * magnitude;
  const max = Math.max(tickStep, Math.ceil(rawMax / tickStep) * tickStep);
  const w = 680, h = 215, left = 54, top = 18, bottom = 48, right = 16;
  const plotW = w - left - right, plotH = h - top - bottom;
  const x = (i: number, len: number) => left + (len <= 1 ? 0 : (i / (len - 1)) * plotW);
  const y = (v: number) => top + plotH - (v / max) * plotH;
  const yTicks = Array.from({ length: 5 }, (_, index) => index * max / 4);
  const xIndexes = Array.from(new Set([0, .25, .5, .75, 1].map(position => Math.round(position * Math.max(0, chart.labels.length - 1)))));
  const labelTimes = chart.labels.map(value => new Date(value).getTime());
  const chartStart = labelTimes[0] ?? 0;
  const chartEnd = labelTimes[labelTimes.length - 1] ?? chartStart;
  const annotationBands = annotations.filter(row => new Date(row.endUtc).getTime() >= chartStart && new Date(row.startUtc).getTime() <= chartEnd);
  const xTime = (value: string) => left + Math.max(0, Math.min(1, (new Date(value).getTime() - chartStart) / Math.max(1, chartEnd - chartStart))) * plotW;
  const seriesColor = (series: TrHealthChart["series"][number], index: number) => series.label === "Strong reference" ? "#9faab5" : series.isBaseline ? "#d7ecff" : colors[index % colors.length];
  return <div className="card tr-chart-card">
    {showTitle && <h3>{chart.title}</h3>}
    <p className="muted">{chart.yAxisLabel} · {chart.labels.length.toLocaleString()} time buckets</p>
    {showBaselineNote && chart.baselineNote && <p className="baseline-note">{chart.baselineNote}</p>}
    <svg className="chart" viewBox={`0 0 ${w} ${h}`} preserveAspectRatio="xMidYMid meet" role="img" aria-label={`${chart.title} over time`}>
      {annotationBands.map(row => { const startX = xTime(row.startUtc); const endX = xTime(row.endUtc); return <rect className={`finding-episode-band severity-${row.severity}`} x={startX} y={top} width={Math.max(3, endX - startX)} height={plotH} key={row.episodeKey}><title>Finding episode: {new Date(row.startUtc).toLocaleString()} to {new Date(row.endUtc).toLocaleString()} · {row.conditions.map(label).join(", ")}</title></rect>; })}
      <line className="axis" x1={left} y1={top} x2={left} y2={top + plotH} />
      <line className="axis" x1={left} y1={top + plotH} x2={left + plotW} y2={top + plotH} />
      {yTicks.map(value => <g key={value}><line className="chart-grid-line" x1={left} y1={y(value)} x2={left + plotW} y2={y(value)} /><text className="chart-label" x={left - 7} y={y(value) + 4} textAnchor="end">{formatChartValue(value, chart.valueFormat)}</text></g>)}
      {chart.series.map((s, si) => <React.Fragment key={`${s.scope}-${s.label}-${si}`}><polyline points={s.values.map((v, i) => `${x(i, s.values.length)},${y(v)}`).join(" ")} fill="none" stroke={seriesColor(s, si)} strokeWidth={s.isBaseline ? "2.5" : "2"} strokeDasharray={s.isBaseline ? "6 5" : undefined} />{!s.isBaseline && s.values.map((value, index) => <circle className="chart-point" key={index} cx={x(index, s.values.length)} cy={y(value)} r="3.5" fill={seriesColor(s, si)}><title>{formatRfChartTime(chart.labels[index], chart.labels)} · {s.label}: {formatChartValue(value, chart.valueFormat)} {chart.yAxisLabel.toLowerCase()}</title></circle>)}</React.Fragment>)}
      {xIndexes.map((index, position) => <text className="chart-label" x={x(index, chart.labels.length)} y={h - 14} textAnchor={position === 0 ? "start" : position === xIndexes.length - 1 ? "end" : "middle"} key={index}>{formatRfChartTime(chart.labels[index], chart.labels)}</text>)}
    </svg>
    <div className="legend">{chart.series.map((s, i) => <span className={s.isBaseline ? "baseline-legend" : ""} key={`${s.scope}-${s.label}-${i}`}><i style={{ background: seriesColor(s, i) }} />{s.label || "Current"}</span>)}</div>
  </div>;
}

function formatRfChartTime(value: string, labels: string[]) {
  const timestamp = new Date(value).getTime();
  if (!Number.isFinite(timestamp)) return value;
  const parsed = labels.map(label => new Date(label).getTime()).filter(Number.isFinite);
  const step = parsed.length > 1 ? Math.max(15 * 60_000, parsed[1] - parsed[0]) : 60 * 60_000;
  let roundedTimestamp = Math.ceil(timestamp / step) * step;
  if (parsed.length > 1 && timestamp === parsed[0] && roundedTimestamp === Math.ceil(parsed[1] / step) * step)
    roundedTimestamp = Math.floor(timestamp / step) * step;
  const rounded = new Date(roundedTimestamp);
  const spansDays = parsed.length > 1 && new Date(parsed[0]).toLocaleDateString() !== new Date(parsed[parsed.length - 1]).toLocaleDateString();
  return rounded.toLocaleString([], spansDays ? { month: "numeric", day: "numeric", hour: "numeric", minute: "2-digit" } : { hour: "numeric", minute: "2-digit" });
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

function firstRunStepId(id?: string) {
  const value = String(id || "").trim();
  if (!value || value === "stack")
    return "tr";
  return ["tr", "lm-link", "qdrant", "finish"].includes(value) ? value : "tr";
}

function SetupWizard({ status, reload, onComplete }: { status: SetupStatus; reload: () => Promise<void>; onComplete?: () => void }) {
  const [draft, setDraft] = useState<any>(() => setupDraftFromStatus(status));
  const [message, setMessage] = useState("Complete the first-run prerequisites, then PizzaWave will open Site Setup for monitoring configuration.");
  const [busy, setBusy] = useState("");
  const [artifactReport, setArtifactReport] = useState<SetupArtifactReport | null>(null);
  const [setupJob, setSetupJob] = useState<Job | null>(null);
  const [setupJobContext, setSetupJobContext] = useState("");
  const [setupLogs, setSetupLogs] = useState<JobLog[]>([]);
  const [wizardStep, setWizardStep] = useState(firstRunStepId(status.currentStep));
  const [expandedSetupStep, setExpandedSetupStep] = useState(firstRunStepId(status.currentStep));
  const [jobDrawerOpen, setJobDrawerOpen] = useState(false);
  const [trInstallMode, setTrInstallMode] = useState((status.values?.setup?.installMode === "freshTr" ? "freshTr" : "reuseExistingTr") as "reuseExistingTr" | "freshTr");
  const [confirmFreshBuild, setConfirmFreshBuild] = useState(false);
  const setupLogLastId = useRef(0);
  const setupDraftDirty = useRef(false);
  const setupJobRunning = Boolean(setupJob && ["queued", "running", "paused"].includes(setupJob.status));

  useEffect(() => {
    if (!setupDraftDirty.current)
      setDraft(setupDraftFromStatus(status));
    if (!setupJobRunning)
      setWizardStep(current => firstRunStepId(current || status.currentStep));
    setTrInstallMode(status.values?.setup?.installMode === "freshTr" ? "freshTr" : "reuseExistingTr");
  }, [status, setupJobRunning]);
  useEffect(() => {
    if (!setupJobRunning && status.currentStep)
      setExpandedSetupStep(firstRunStepId(status.currentStep));
  }, [status.currentStep, setupJobRunning]);
  useEffect(() => {
    if (setupJob)
      setJobDrawerOpen(true);
  }, [setupJob?.id]);
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

  function update(path: string[], value: any) {
    setupDraftDirty.current = true;
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
      setMessage("Setup values saved.");
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Save failed.");
    } finally {
      setBusy("");
    }
  }

  async function saveSetupValues() {
    const values: any = cloneSettings(draft);
    const currentWizardStep = effectiveSetupStepId();
    values.setup = { ...(values.setup ?? {}), currentStep: currentWizardStep, installMode: trInstallMode };
    const saved = await api.request<SetupStatus>("/api/v1/setup/save", { method: "POST", body: JSON.stringify({ values }) });
    setupDraftDirty.current = false;
    setDraft(setupDraftFromStatus(saved));
    return saved;
  }

  function effectiveSetupStepId(id = wizardStep) {
    return firstRunStepId(id);
  }

  async function validateSetupSection(section: string, saveFirst = true) {
    if (saveFirst) await saveSetupValues();
    const result = await api.request<SetupValidationResult>("/api/v1/setup/validate-required", { method: "POST" });
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

  async function finishSetup() {
    setBusy("finish-setup");
    try {
      if (requiredOpen.length > 0)
        throw new Error("Complete the required setup checks before finishing.");
      await saveSetupValues();
      const validation = await api.request<SetupValidationResult>("/api/v1/setup/validate-required", { method: "POST" });
      if (!validation.ok)
        throw new Error(validation.message);
      const completed = await api.request<SetupStatus>("/api/v1/setup/complete", { method: "POST" });
      setMessage("Setup complete. Opening Site Setup...");
      await reload();
      onComplete?.();
      return completed;
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Setup could not be completed.");
    } finally {
      setBusy("");
    }
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

  const checks = status.checks ?? [];
  const requiredOpen = checks.filter(c => c.required && !c.ok);
  const tr = draft.trunkRecorder ?? {};
  const embeddings = draft.embeddings ?? {};
  const lmStudioDetection = (status.detection as any)?.lmStudio;
  const qdrantDetection = (status.detection as any)?.qdrant;
  const lmStudioInstalled = Boolean(lmStudioDetection?.found || lmStudioDetection?.serviceEnabled || lmStudioDetection?.binaryPath);
  const qdrantInstalled = Boolean(qdrantDetection?.found || qdrantDetection?.serviceEnabled || qdrantDetection?.binaryPath || qdrantDetection?.storageExists);
  const checkStepMap: Record<string, string> = {
    tr: "tr",
    "lm-link": "lm-link",
    qdrant: "qdrant"
  };
  const baseSetupSteps = [
    { id: "tr", title: "Trunk Recorder" },
    { id: "lm-link", title: "LM Link" },
    { id: "qdrant", title: "Qdrant" },
    { id: "finish", title: "Finish" }
  ];
  const setupSteps = baseSetupSteps.map(step => {
    const stepChecks = checks.filter(check => checkStepMap[check.id] === step.id);
    const requiredMissing = stepChecks.some(check => check.required && !check.ok);
    let ok = stepChecks.length > 0 && stepChecks.every(check => check.ok);
    let blocked = requiredMissing;
    if (step.id === "tr")
      ok = Boolean(status.detection?.found);
    if (step.id === "lm-link")
      ok = lmStudioInstalled;
    if (step.id === "qdrant")
      ok = qdrantInstalled;
    if (step.id === "finish") {
      ok = requiredOpen.length === 0 && !setupJobRunning;
      blocked = requiredOpen.length > 0 || setupJobRunning;
    }
    return { ...step, checks: stepChecks, ok, blocked };
  });
  const effectiveWizardStep = firstRunStepId(wizardStep);
  const stepIndex = Math.max(0, setupSteps.findIndex(step => step.id === effectiveWizardStep));
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

  function selectSetupStep(id: string, toggle = true) {
    setExpandedSetupStep(current => toggle && current === id ? "" : id);
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
    if (currentStep.id === "tr") {
      await validateSetupSection("tr");
      return;
    }

    if (currentStep.id === "lm-link" || currentStep.id === "qdrant") {
      await saveSetupValues();
      setMessage(`${currentStep.title} choice saved. You can configure and validate the feature later from Settings.`);
    }
  }

  return <div className="setup-page">
    <div className="setup-hero">
      <div>
        <h1>PizzaWave Setup</h1>
        <p>First-run setup prepares the host software. Site, talkgroup, RF, AI, and embedding configuration continue in Setup and Settings after this wizard.</p>
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
                  ? step.checks.map(check => {
                    const displayCheck = setupCheckDisplay(check, trInstallMode);
                    return <button type="button" className={`setup-check compact ${check.ok ? "ok" : check.required ? "blocked" : "optional"}`} key={check.id} onClick={() => selectSetupStep(checkStepMap[check.id] ?? step.id, false)}>
                      <span>{check.ok ? "OK" : check.required ? "Required" : "Optional"}</span>
                      <strong>{displayCheck.label}</strong>
                      <small>{displayCheck.message}</small>
                    </button>;
                  })
                  : <div className="setup-note">{step.id === "stack"
                    ? "Choose whether to reuse the existing TR install or build a fresh one."
                    : step.id === "finish"
                      ? (requiredOpen.length > 0 ? `${requiredOpen.length} required prerequisite check(s) still need validation.` : "Finish opens Site Setup.")
                      : "This step has no validation checks yet."}</div>}
              </div>}
            </div>;
          })}
        </div>
      </div>
      <div className="setup-step-panel">
        {currentStep.id === "tr" && <SetupSection title="Trunk Recorder" description="Install trunk-recorder or confirm that an existing install can be reused. Monitoring sites, talkgroups, RF tuning, and generated TR config are handled later in Site Setup.">
          <div className="setup-choice-grid">
            <ChoiceCard active={trInstallMode === "reuseExistingTr"} title="Reuse existing TR" description="Use a trunk-recorder install already present on this system." onClick={() => { setTrInstallMode("reuseExistingTr"); update(["setup", "installMode"], "reuseExistingTr"); }} />
            <ChoiceCard active={trInstallMode === "freshTr"} title="Build TR" description="Build and install trunk-recorder from the bundled setup helper." onClick={() => { setTrInstallMode("freshTr"); update(["setup", "installMode"], "freshTr"); }} />
          </div>
          <div className="setup-detection-card">
            <strong>{status.detection?.found ? "Trunk Recorder is available" : "Trunk Recorder not detected yet"}</strong>
            <small>Detection checks for the trunk-recorder binary, service, or a reusable configured TR path.</small>
            <small>{status.detection?.binaryPath ? `Binary: ${status.detection.binaryPath}` : "Binary was not found on PATH."}</small>
            <small>{status.detection?.configExists ? `Reusable config: ${status.detection.configPath}` : `Configured path: ${status.detection?.configPath ?? tr.configPath ?? "/etc/trunk-recorder/config.json"}`}</small>
            <small>{status.detection?.serviceActive ? "TR service is currently running." : "TR service is not active."}</small>
          </div>
          {trInstallMode === "freshTr" && <>
            <div className="setup-note">The source build can install OS packages and service files. It does not create the final monitoring configuration; Site Setup handles that after first-run.</div>
            {artifactReport && <ArtifactReport report={artifactReport} />}
            {artifactReport?.hasBlockingArtifacts && <SettingCheckbox label="I reviewed the existing artifacts and want to source-build anyway" description="Use this only after removing or intentionally keeping the listed files." checked={confirmFreshBuild} onChange={setConfirmFreshBuild} />}
            <button disabled={Boolean(busy) || (artifactReport?.hasBlockingArtifacts && !confirmFreshBuild)} onClick={() => void startSetupJob("tr-source-build", confirmFreshBuild)}>{busy === "tr-source-build" ? "Building..." : "Build trunk-recorder from source"}</button>
          </>}
          <div className="setup-button-row">
            <button disabled={Boolean(busy)} onClick={() => void detect()}>{busy === "detect" ? "Detecting..." : "Detect TR"}</button>
            <button disabled={Boolean(busy) || setupJobRunning} onClick={() => void validate("tr")}>{busy === "tr" ? "Checking..." : "Check prerequisite"}</button>
          </div>
        </SetupSection>}

        {currentStep.id === "lm-link" && <SetupSection title="LM Link" description="Optional host support for AI Insights. Configure models and endpoints later from Settings.">
          {lmStudioInstalled ? <div className="setup-detection-card">
            <strong>{lmStudioDetection?.apiReachable ? "LM Studio API is reachable" : lmStudioDetection?.serviceActive ? "LM Link support is present" : "LM Studio tooling is installed"}</strong>
            <small>Detection looks for LM Studio CLI/service support and probes the configured OpenAI-compatible API URL when available.</small>
            <small>{lmStudioDetection?.serviceEnabled ? `${lmStudioDetection.service} is enabled for autostart.` : "LM Studio autostart service is not enabled."}</small>
            <small>{lmStudioDetection?.binaryPath ? `CLI: ${lmStudioDetection.binaryPath}` : "LM Studio CLI path was not detected."}</small>
            {lmStudioDetection?.apiBaseUrl && <small>{lmStudioDetection.apiReachable ? `API reachable at ${lmStudioDetection.apiBaseUrl}.` : `API not reachable at ${lmStudioDetection.apiBaseUrl}.`}</small>}
          </div> : <>
            <div className="setup-note">Prepare LM Link only if this system should expose or use LM Studio-compatible AI support. This does not enable AI Insights by itself.</div>
            <button disabled={Boolean(busy) || setupJobRunning} onClick={() => void startSetupJob("lmstudio-prime")}>{busy === "lmstudio-prime" || (setupJobContext === "lm-link" && setupJobRunning) ? "Preparing..." : "Prepare LM Link support"}</button>
          </>}
        </SetupSection>}

        {currentStep.id === "qdrant" && <SetupSection title="Qdrant" description="Optional native vector database support. Configure embeddings later from Settings.">
          {qdrantDetection && <div className="setup-detection-card">
            <strong>{qdrantDetection.serviceActive ? "Qdrant service is running" : qdrantInstalled ? "Qdrant is installed" : "Qdrant not detected"}</strong>
            <small>Detection checks for the Qdrant binary, systemd service state, and configured storage path.</small>
            <small>{qdrantDetection.serviceEnabled ? `${qdrantDetection.service} is enabled for autostart.` : "Qdrant autostart service is not enabled."}</small>
            <small>{qdrantDetection.binaryPath ? `Binary: ${qdrantDetection.binaryPath}` : "qdrant binary was not found on PATH."}</small>
            <small>{qdrantDetection.storageExists ? `Storage: ${qdrantDetection.storagePath}` : `Storage will be created at ${embeddings.qdrantStoragePath ?? "/var/lib/pizzawave/qdrant"}`}</small>
          </div>}
          {!qdrantInstalled && <button disabled={Boolean(busy) || setupJobRunning} onClick={() => void startSetupJob("qdrant-prime")}>{busy === "qdrant-prime" || (setupJobContext === "qdrant" && setupJobRunning) ? "Installing..." : "Install native Qdrant"}</button>}
          {qdrantInstalled && <div className="setup-note">Qdrant support is present. Embedding enablement, model, endpoint, collection, and vector size are configured in Settings.</div>}
        </SetupSection>}

        {currentStep.id === "finish" && <SetupSection title="Finish" description="This completes first-run prerequisites and opens Site Setup for monitoring configuration.">
          <SetupReview status={status} requiredOpen={requiredOpen} />
          <div className="setup-button-row"><button disabled={Boolean(busy) || setupJobRunning || requiredOpen.length > 0} onClick={() => void finishSetup()}>{busy === "finish-setup" ? "Finishing..." : "Finish setup"}</button></div>
        </SetupSection>}

        <div className="setup-nav-row">
          <button disabled={stepIndex === 0 || Boolean(busy) || setupJobRunning} onClick={previousStep}>Back</button>
          {stepIndex < setupSteps.length - 1 && <button
            disabled={Boolean(busy) || setupJobRunning}
            onClick={() => void nextStep()}
          >
            {busy === `advance-${currentStep.id}` ? "Working..." : "Next"}
          </button>}
        </div>
      </div>
    </div>
    <SetupJobDrawer job={setupJob} logs={setupLogs} running={setupJobRunning} onStopCalibration={stopCalibrationJob} stopping={busy === "tr-calibration-cancel"} open={jobDrawerOpen} setOpen={setJobDrawerOpen} />
  </div>;
}

function SetupSection({ title, description, children }: { title: string; description: string; children: React.ReactNode }) {
  return <div className="card setup-section"><h3>{title}</h3><p>{description}</p><div className="settings-fields">{children}</div></div>;
}

function setupCheckDisplay(check: { id: string; label: string; message: string }, trInstallMode: "reuseExistingTr" | "freshTr") {
  if (trInstallMode !== "freshTr")
    return check;
  if (check.id === "tr")
    return { ...check, label: "TR config generated", message: "PizzaWave must create and validate the trunk-recorder config for the selected RadioReference sites." };
  if (check.id === "callstream")
    return { ...check, label: "Callstream target applied", message: "The generated TR config must send completed calls to the PizzaWave listener shown in TR Config." };
  if (check.id === "health")
    return { ...check, label: "Generated TR config readable", message: "PizzaWave must be able to read the generated TR config and service logs before setup can finish." };
  return check;
}

function ChoiceCard({ active, title, description, disabled, onClick }: { active: boolean; title: string; description: string; disabled?: boolean; onClick: () => void }) {
  return <button type="button" className={`setup-choice ${active ? "active" : ""}`} disabled={disabled} onClick={onClick}>
    <span className="setup-choice-toggle">{active ? "On" : "Off"}</span>
    <strong>{title}</strong>
    <small>{description}</small>
  </button>;
}

function SetupReview({ status, requiredOpen }: { status: SetupStatus; requiredOpen: SetupStatus["checks"] }) {
  const values: any = status.values ?? {};
  const tr = values.trunkRecorder ?? {};
  const lmStudio = (status.detection as any)?.lmStudio;
  const qdrant = (status.detection as any)?.qdrant;
  return <div className="setup-review">
    <h4>First-Run Prerequisites</h4>
    <div><span>Trunk Recorder</span><code>{status.detection?.found ? "available" : "missing"}</code></div>
    <div><span>TR config path</span><code>{tr.configPath ?? "/etc/trunk-recorder/config.json"}</code></div>
    <div><span>LM Link</span><code>{lmStudio?.found || lmStudio?.serviceEnabled ? "present" : "optional"}</code></div>
    <div><span>Qdrant</span><code>{qdrant?.found || qdrant?.serviceEnabled || qdrant?.storageExists ? "present" : "optional"}</code></div>
    {requiredOpen.length > 0
      ? <small>{requiredOpen.length} required prerequisite{requiredOpen.length === 1 ? "" : "s"} still need validation.</small>
      : <small>Completing first-run opens Site Setup for TR config, RF validation, talkgroups, and geolocation.</small>}
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

function formatMhz(value: number) {
  return value.toFixed(6).replace(/0+$/, "").replace(/\.$/, "");
}

function AreaMapPreview({ area }: { area: any }) {
  const bounds = areaBounds(area);
  if (!bounds)
    return null;
  const viewport = osmViewport(bounds, 360, 190);
  return <div className="area-map-card">
    <div className="area-map-frame">
      {viewport.tiles.map(tile => <img
        key={`${tile.z}-${tile.x}-${tile.y}`}
        src={`https://tile.openstreetmap.org/${tile.z}/${tile.x}/${tile.y}.png`}
        alt=""
        loading="lazy"
        style={{ left: tile.left, top: tile.top }}
      />)}
      <span className="area-map-boundary" style={boundaryOverlayStyle(bounds, viewport)} />
      <span className="area-map-credit">OpenStreetMap</span>
    </div>
    <div className="area-map-meta">
      <strong>{area.areaLabel || area.systemShortName || "Monitored area"}</strong>
      <small>N {bounds.north.toFixed(4)} / S {bounds.south.toFixed(4)} / E {bounds.east.toFixed(4)} / W {bounds.west.toFixed(4)}</small>
    </div>
  </div>;
}

function areaDraftKey(area: any, index: number) {
  return String(area.areaId || `${area.systemShortName || "area"}-${index}`);
}

function normalizeSiteSetupAreas(areas?: SiteSetupMonitoredArea[] | null): SiteSetupMonitoredArea[] {
  return (areas ?? [])
    .map(normalizeSiteSetupArea)
    .filter(area => area.areaLabel || area.systemShortName);
}

function normalizeSiteSetupArea(area: Partial<SiteSetupMonitoredArea>): SiteSetupMonitoredArea {
  const systemShortName = String(area.systemShortName ?? "").trim();
  const aliases = Array.from(new Set([...(area.aliases ?? []), systemShortName].map(value => String(value ?? "").trim()).filter(Boolean)));
  return {
    areaId: String(area.areaId || createClientId()),
    areaLabel: String(area.areaLabel ?? "").trim(),
    systemShortName,
    north: finiteNumber(area.north),
    south: finiteNumber(area.south),
    east: finiteNumber(area.east),
    west: finiteNumber(area.west),
    aliases,
    isOverride: area.isOverride === true,
    contextKey: String(area.contextKey ?? "").trim()
  };
}

function finiteNumber(value: unknown) {
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : 0;
}

function areaBounds(area: any) {
  const north = Number(area?.north);
  const south = Number(area?.south);
  const east = Number(area?.east);
  const west = Number(area?.west);
  if (!Number.isFinite(north) || !Number.isFinite(south) || !Number.isFinite(east) || !Number.isFinite(west) || north <= south || east <= west)
    return null;
  return { north, south, east, west };
}

function hasUsableAreaBounds(area: any) {
  return areaBounds(area) !== null;
}

function osmViewport(bounds: { north: number; south: number; east: number; west: number }, width: number, height: number) {
  const tileSize = 256;
  let zoom = 5;
  for (let candidate = 5; candidate <= 15; candidate++) {
    const boundsWidth = Math.abs(lonToPixelX(bounds.east, candidate) - lonToPixelX(bounds.west, candidate));
    const boundsHeight = Math.abs(latToPixelY(bounds.south, candidate) - latToPixelY(bounds.north, candidate));
    if (boundsWidth <= width * 0.72 && boundsHeight <= height * 0.72)
      zoom = candidate;
    else
      break;
  }

  const centerX = (lonToPixelX(bounds.west, zoom) + lonToPixelX(bounds.east, zoom)) / 2;
  const centerY = (latToPixelY(bounds.north, zoom) + latToPixelY(bounds.south, zoom)) / 2;
  const originX = centerX - width / 2;
  const originY = centerY - height / 2;
  const maxTile = Math.pow(2, zoom);
  const minX = Math.floor(originX / tileSize);
  const maxX = Math.floor((originX + width) / tileSize);
  const minY = Math.floor(originY / tileSize);
  const maxY = Math.floor((originY + height) / tileSize);
  const tiles = [];
  for (let x = minX; x <= maxX; x++) {
    for (let y = minY; y <= maxY; y++) {
      if (y < 0 || y >= maxTile)
        continue;
      const wrappedX = ((x % maxTile) + maxTile) % maxTile;
      tiles.push({ z: zoom, x: wrappedX, y, left: x * tileSize - originX, top: y * tileSize - originY });
    }
  }

  return { zoom, width, height, originX, originY, tiles };
}

function lonToPixelX(lon: number, zoom: number) {
  return ((lon + 180) / 360) * 256 * Math.pow(2, zoom);
}

function latToPixelY(lat: number, zoom: number) {
  const clampedLat = Math.max(-85.05112878, Math.min(85.05112878, lat));
  const rad = clampedLat * Math.PI / 180;
  return (1 - Math.log(Math.tan(rad) + 1 / Math.cos(rad)) / Math.PI) / 2 * 256 * Math.pow(2, zoom);
}

function boundaryOverlayStyle(bounds: { north: number; south: number; east: number; west: number }, viewport: ReturnType<typeof osmViewport>): React.CSSProperties {
  const left = lonToPixelX(bounds.west, viewport.zoom) - viewport.originX;
  const right = lonToPixelX(bounds.east, viewport.zoom) - viewport.originX;
  const top = latToPixelY(bounds.north, viewport.zoom) - viewport.originY;
  const bottom = latToPixelY(bounds.south, viewport.zoom) - viewport.originY;
  return {
    left,
    top,
    width: Math.max(6, right - left),
    height: Math.max(6, bottom - top)
  };
}

function suggestAreaLabelFromSite(siteName: string) {
  const value = String(siteName ?? "").trim().replace(/[_-]+/g, " ");
  const stateMatch = value.match(/^(?<label>.+?),\s*(?<state>[A-Z]{2})$/i) ?? value.match(/^(?<label>.+?)\s+(?<state>[A-Z]{2})$/i);
  if (!stateMatch?.groups)
    return value;
  const stateName = stateNameByAbbreviation[stateMatch.groups.state.toUpperCase()];
  if (!stateName)
    return value;
  let label = stateMatch.groups.label
    .replace(/\b(simulcast|subsite|tower|site|rfss)\b/gi, " ")
    .replace(/\s+/g, " ")
    .trim();
  if (!label)
    return value;
  if (/\bcounty\b/i.test(label))
    return `${titleWords(label)}, ${stateName}`;
  const words = label.split(/\s+/).filter(Boolean);
  const place = words[0] ?? label;
  return `${titleWords(place)}, ${stateName}`;
}

function titleWords(value: string) {
  return value.toLowerCase().replace(/\b[a-z]/g, letter => letter.toUpperCase());
}

const stateNameByAbbreviation: Record<string, string> = {
  AL: "Alabama", AK: "Alaska", AZ: "Arizona", AR: "Arkansas", CA: "California", CO: "Colorado",
  CT: "Connecticut", DE: "Delaware", DC: "District Of Columbia", FL: "Florida", GA: "Georgia",
  HI: "Hawaii", ID: "Idaho", IL: "Illinois", IN: "Indiana", IA: "Iowa", KS: "Kansas",
  KY: "Kentucky", LA: "Louisiana", ME: "Maine", MD: "Maryland", MA: "Massachusetts",
  MI: "Michigan", MN: "Minnesota", MS: "Mississippi", MO: "Missouri", MT: "Montana",
  NE: "Nebraska", NV: "Nevada", NH: "New Hampshire", NJ: "New Jersey", NM: "New Mexico",
  NY: "New York", NC: "North Carolina", ND: "North Dakota", OH: "Ohio", OK: "Oklahoma",
  OR: "Oregon", PA: "Pennsylvania", RI: "Rhode Island", SC: "South Carolina", SD: "South Dakota",
  TN: "Tennessee", TX: "Texas", UT: "Utah", VT: "Vermont", VA: "Virginia", WA: "Washington",
  WV: "West Virginia", WI: "Wisconsin", WY: "Wyoming"
};

function SettingsView({ settingsSections, settingsLoadState, reload, pendingProfileHides, setPendingProfileHides, onDirtyChange }: { settingsSections: Record<string, any>; settingsLoadState: { loading: boolean; version: number; message: string; error: boolean }; reload: () => Promise<void>; pendingProfileHides: ProfileTalkgroupSetting[]; setPendingProfileHides: React.Dispatch<React.SetStateAction<ProfileTalkgroupSetting[]>>; onDirtyChange: (dirty: boolean) => void }) {
  const settingsTabs = [
    ["transcription", "Transcription"],
    ["ai", "AI"],
    ["embeddings", "Embeddings"],
    ["alerts", "Alerts"],
    ["profiles", "Profiles"],
    ["admin", "Security"],
    ["system", "System Info"]
  ] as const;
  const normalizeSettingsTab = (tab: string | null | undefined) => settingsTabs.some(([id]) => id === tab) ? tab! : "transcription";
  const [settingsTab, setSettingsTab] = useState(() => normalizeSettingsTab(localStorage.getItem("pizzawave-settings-tab")));
  const [sections, setSections] = useState<Record<string, any>>({});
  const [baselineSections, setBaselineSections] = useState<Record<string, any>>({});
  const [message, setMessage] = useState("");
  const [messageKind, setMessageKind] = useState<"info" | "error">("info");
  const [savingSection, setSavingSection] = useState("");
  const [sectionStatus, setSectionStatus] = useState<Record<string, { kind: "ok" | "error" | "info"; text: string }>>({});
  const [testingSection, setTestingSection] = useState("");
  const [showAiHelp, setShowAiHelp] = useState(false);
  const [modelBusy, setModelBusy] = useState("");
  const [profilesDirty, setProfilesDirty] = useState(false);

  useEffect(() => {
    const next = withSettingsDefaults(settingsSections);
    setSections(next);
    setBaselineSections(cloneSettings(next));
    if (settingsLoadState.message) {
      setMessageKind(settingsLoadState.error ? "error" : "info");
      setMessage(settingsLoadState.message);
    }
  }, [settingsSections, settingsLoadState.version]);
  const modelsResource = usePersistentRefresh({
    key: "settings-transcription-models",
    enabled: settingsTab === "transcription",
    load: () => api.request<any[]>("/api/v1/settings/transcription/models")
  });
  const aiModelsResource = usePersistentRefresh({
    key: `settings-ai-models|${sections["ai-insights"]?.openAiBaseUrl ?? ""}`,
    enabled: settingsTab === "ai" && Boolean(sections["ai-insights"]?.enabled),
    load: () => api.request<any>("/api/v1/settings/ai-insights/models")
  });
  const models = modelsResource.data ?? [];
  const aiModelsResult = aiModelsResource.data;
  const aiModels = Array.isArray(aiModelsResult?.models) ? aiModelsResult.models as string[] : [];
  const aiModelsMessage = aiModelsResult?.message ?? (aiModels.length ? `Found ${aiModels.length} model(s).` : "No models returned.");
  useEffect(() => {
    if (!modelBusy) return;
    const timer = window.setInterval(() => void modelsResource.refresh(), 1500);
    return () => window.clearInterval(timer);
  }, [modelBusy, modelsResource.refresh]);
  useEffect(() => localStorage.setItem("pizzawave-settings-tab", settingsTab), [settingsTab]);
  const dirtySections = useMemo(() => {
    const names = Object.keys(sections).filter(section => section !== "profiles");
    return names.filter(section => JSON.stringify(sections[section] ?? {}) !== JSON.stringify(baselineSections[section] ?? {}));
  }, [sections, baselineSections]);
  const hasUnsavedSectionChanges = dirtySections.length > 0;
  const hasUnsavedSettingsChanges = hasUnsavedSectionChanges || profilesDirty;
  useEffect(() => {
    if (hasUnsavedSettingsChanges)
      localStorage.setItem("pizzawave-unapplied-settings", "1");
    else
      localStorage.removeItem("pizzawave-unapplied-settings");
    if (!hasUnsavedSettingsChanges) return;
    const handler = (event: BeforeUnloadEvent) => {
      event.preventDefault();
      event.returnValue = "You have unapplied settings changes.";
    };
    window.addEventListener("beforeunload", handler);
    return () => window.removeEventListener("beforeunload", handler);
  }, [hasUnsavedSettingsChanges]);
  useEffect(() => {
    onDirtyChange(hasUnsavedSettingsChanges);
    return () => onDirtyChange(false);
  }, [hasUnsavedSettingsChanges, onDirtyChange]);

  const engine = sections.engine ?? {};
  const transcription = sections.transcription ?? {};
  const aiInsights = sections["ai-insights"] ?? {};
  const embeddings = sections.embeddings ?? {};
  const tr = sections.tr ?? {};
  const alerts = sections.alerts ?? {};
  const auth = sections.auth ?? {};
  const [tokenDisplay, setTokenDisplay] = useState("");

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
      const values = section === "alerts"
        ? { ...(sections[section] ?? {}), _rulesExplicit: true }
        : sections[section] ?? {};
      await api.request(`/api/v1/settings/${section}`, { method: "POST", body: JSON.stringify({ values }) });
      setBaselineSections(current => ({ ...current, [section]: cloneSettings(sections[section] ?? {}) }));
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

  async function downloadModel(model: string) {
    setModelBusy(model);
    try {
      const result = await api.request<any>(`/api/v1/settings/transcription/models/${model}/download`, { method: "POST" });
      setSectionStatus(current => ({ ...current, transcription: { kind: result.ok ? "ok" : "error", text: result.message ?? "Download complete" } }));
      if (result.ok !== false && result.path && model.startsWith("whisper-"))
        update("transcription", ["whisperModelFile"], result.path);
      await modelsResource.refresh();
    } finally {
      setModelBusy("");
    }
  }

  async function deleteModel(model: string) {
    if (!confirmAction("Remove transcription model?", `This deletes the local model file or directory for ${model}. Future use will require downloading it again.`)) return;
    setModelBusy(model);
    try {
      const result = await api.request<any>(`/api/v1/settings/transcription/models/${model}`, { method: "DELETE" });
      setSectionStatus(current => ({ ...current, transcription: { kind: result.ok ? "ok" : "error", text: result.message ?? "Removed" } }));
      await modelsResource.refresh();
    } finally {
      setModelBusy("");
    }
  }

  async function installFasterWhisper() {
    setModelBusy("faster-whisper");
    setSectionStatus(current => ({ ...current, transcription: { kind: "info", text: "Installing faster-whisper support..." } }));
    try {
      const job = await api.request<Job>("/api/v1/setup/jobs", { method: "POST", body: JSON.stringify({ action: "faster-whisper-prime", confirmed: true }) });
      setSectionStatus(current => ({ ...current, transcription: { kind: "info", text: `Started faster-whisper install job ${job.id}. Watch System > Jobs for logs.` } }));
    } catch (error) {
      const text = error instanceof Error ? error.message : String(error);
      setSectionStatus(current => ({ ...current, transcription: { kind: "error", text } }));
    } finally {
      setModelBusy("");
    }
  }

  async function revealAdminToken() {
    setSectionStatus(current => ({ ...current, auth: { kind: "info", text: "Reading token..." } }));
    try {
      const result = await api.request<{ token: string; tokenFile: string }>("/api/v1/settings/auth/token");
      if (result.token) {
        localStorage.setItem("pizzawave-admin-token", result.token);
        setTokenDisplay(result.token);
        setSectionStatus(current => ({ ...current, auth: { kind: "ok", text: "Admin token revealed and stored for this browser" } }));
      } else {
        setTokenDisplay("");
        setSectionStatus(current => ({ ...current, auth: { kind: "error", text: `Token file ${result.tokenFile || "returned no token"}` } }));
      }
    } catch (error) {
      const text = error instanceof Error && error.message ? error.message : "Unable to reveal token.";
      setSectionStatus(current => ({ ...current, auth: { kind: "error", text } }));
    }
  }

  async function rotateAdminToken() {
    if (!confirmAction("Rotate admin token?", "Other browsers and scripts will lose write access until they use the new token.")) return;
    const result = await api.request<{ token: string; tokenFile: string }>("/api/v1/settings/auth/regenerate-token", { method: "POST" });
    if (result.token) {
      localStorage.setItem("pizzawave-admin-token", result.token);
      setTokenDisplay(result.token);
    }
    setSectionStatus(current => ({ ...current, auth: { kind: "ok", text: "Admin token rotated" } }));
  }
  function updateAiEnabled(enabled: boolean) {
    if (!enabled && !confirmAction("Disable AI Insights?", "Disabling AI usage stops LLM call summaries, incident extraction/update, evidence verification, AI-assisted troubleshooting, and recommendation explanations. The dashboard will keep raw calls and existing stored incidents, but PizzaWave loses most higher-level situational awareness until AI is re-enabled."))
      return;
    update("ai-insights", ["enabled"], enabled);
  }

  return <div className="settings-page">
    <div className="settings-header">
      <h2>Settings</h2>
      <div className="settings-header-actions">
        {hasUnsavedSettingsChanges && <span className="section-status info">Unsaved: {[...dirtySections.map(label), ...(profilesDirty ? ["Profiles"] : [])].join(", ")}</span>}
        <span className={messageKind === "error" ? "settings-message error" : "settings-message"}>{message || "Changes save by section."}</span>
      </div>
    </div>

    <div className="settings-layout">
      <div className="settings-nav">
        {settingsTabs.map(([id, text]) => <button key={id} className={settingsTab === id ? "active" : ""} onClick={() => setSettingsTab(id)}>{text}</button>)}
      </div>

    <div className="settings-flow">
      {settingsTab === "transcription" && <SettingsCard title="Transcription" description="Controls how individual calls become text. This is separate from AI summaries and incidents." busy={savingSection === "transcription"} testing={testingSection === "transcription"} status={sectionStatus.transcription} onSave={() => save("transcription")} onTest={() => test("transcription")}>
        <PanelLoadState label="installed transcription models" state={modelsResource.state} hasData={Boolean(modelsResource.data)} onRetry={modelsResource.refresh} />
        <SettingSelect label="Engine" description="Required provider for turning new calls into text." value={transcription.provider} options={["whisper", "faster-whisper", "remote-faster-whisper", "vosk", "lmstudio", "openai"]} onChange={v => update("transcription", ["provider"], v)} />
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
        {(transcription.provider === "lmstudio" || transcription.provider === "openai" || transcription.provider === "remote-faster-whisper") && <>
          <SettingInput label="Base URL" description={transcription.provider === "remote-faster-whisper" ? "Remote faster-whisper /v1 endpoint, for example http://gpu-host:9187/v1." : "OpenAI-compatible audio transcription endpoint base URL."} value={transcription.openAiBaseUrl} onChange={v => update("transcription", ["openAiBaseUrl"], v)} />
          <SettingInput label="Model" description={transcription.provider === "remote-faster-whisper" ? "Model loaded by the remote server, usually small or medium." : "Model name sent to the audio transcription endpoint."} value={transcription.openAiModel} onChange={v => update("transcription", ["openAiModel"], v)} />
          <SettingInput label="API key" description="Optional bearer token for remote transcription endpoints." type="password" value={transcription.openAiApiKey} onChange={v => update("transcription", ["openAiApiKey"], v)} />
        </>}
      </SettingsCard>}

      {settingsTab === "ai" && <SettingsCard title="AI Insights" description="One switch controls all LLM usage: call summaries, incidents, and troubleshooting recommendations." busy={savingSection === "ai-insights"} testing={testingSection === "ai-insights"} status={sectionStatus["ai-insights"]} onSave={() => save("ai-insights")} onTest={() => test("ai-insights")}>
        <div className="setting-inline-actions"><button type="button" onClick={() => setShowAiHelp(true)}>Help</button></div>
        {showAiHelp && <div className="modal-backdrop" onClick={() => setShowAiHelp(false)}>
          <div className="card ai-help-modal" onClick={event => event.stopPropagation()}>
            <div className="recommendation-head"><h3>AI Insights Help</h3><button type="button" onClick={() => setShowAiHelp(false)}>Close</button></div>
            <p>AI Insights is the LLM pipeline. PizzaWave uses it after transcription for incident extraction and updates, evidence verification, call/incident summaries, AI-assisted troubleshooting text, and recommendation explanations.</p>
            <p>Disabling AI usage does not stop raw call capture, transcription, embeddings, Qdrant retrieval, alert keyword matching, or service health collection. It does stop new AI-created/updated incidents and most higher-level narrative context.</p>
            <p>Use this switch only for maintenance, endpoint outages, or cost/load isolation. For normal operation it should stay enabled.</p>
          </div>
        </div>}
        <SettingCheckbox label="Enable AI usage" description="When off, pizzad will not call LM Studio or other LLM endpoints." checked={aiInsights.enabled} onChange={updateAiEnabled} />
        <SettingSelect label="Execution" description="Operator intent. The base URL still decides which OpenAI-compatible server receives requests." value={aiInsights.executionMode ?? "local"} options={["local", "remote", "lmlink"]} onChange={v => update("ai-insights", ["executionMode"], v)} />
        <SettingInput label="Base URL" description="OpenAI-compatible chat endpoint. Use a remote host/Tailnet URL for GPU LLMs; avoid localhost unless this rig should run the LLM." value={aiInsights.openAiBaseUrl} onChange={v => update("ai-insights", ["openAiBaseUrl"], v)} />
        <PanelLoadState label="available AI models" state={aiModelsResource.state} hasData={Boolean(aiModelsResource.data)} onRetry={aiModelsResource.refresh} />
        {aiInsights.enabled && aiModels.length > 0
          ? <><div className="setting-field model-refresh-field"><span>Model<small>Chat model id used for summaries, incidents, and recommendations. LM Studio lists loadable model ids, not runtime aliases.</small></span><div><select value={aiModels.includes(aiInsights.openAiModel) ? aiInsights.openAiModel : ""} onChange={e => update("ai-insights", ["openAiModel"], e.target.value)}><option value="" disabled>Choose model</option>{aiModels.map(model => <option value={model} key={model}>{model}</option>)}</select><button type="button" onClick={() => void aiModelsResource.refresh()}>Discover models</button><small>{aiModels.length} model{aiModels.length === 1 ? "" : "s"}</small></div></div>{aiInsights.openAiModel && !aiModels.includes(aiInsights.openAiModel) && <p className="settings-message error">Current saved model is not in the loadable model-id list: {aiInsights.openAiModel}</p>}</>
          : <SettingInput label="Model" description="Chat model id used for summaries, incidents, and recommendations." value={aiInsights.openAiModel} onChange={v => update("ai-insights", ["openAiModel"], v)} />}
        {aiInsights.enabled && aiModels.length === 0 && !aiModelsResource.state.error && <div className="setting-inline-actions"><span className="muted">{aiModelsResource.state.loading ? "Discovering models..." : aiModelsMessage}</span></div>}
        <SettingInput label="API key" description="Optional bearer token. LM Studio local/link setups may leave this blank." type="password" value={aiInsights.openAiApiKey} onChange={v => update("ai-insights", ["openAiApiKey"], v)} />
        <SettingInput label="Timeout (ms)" description="Maximum wait for a single LLM request." type="number" value={aiInsights.timeoutMs} onChange={v => update("ai-insights", ["timeoutMs"], numberOrZero(v))} />
        <SettingInput label="Retries" description="Retry attempts after a failed LLM request." type="number" value={aiInsights.maxRetries} onChange={v => update("ai-insights", ["maxRetries"], numberOrZero(v))} />
        <SettingInput label="Max queue depth to run" description="Blocks manual generation while transcription backlog is above this value. Use 0 to disable this check." type="number" value={aiInsights.maxQueueDepthForManualSummary ?? 100} onChange={v => update("ai-insights", ["maxQueueDepthForManualSummary"], numberOrZero(v))} />
        <div className="settings-subsection">
          <h4>Incident Generation</h4>
          <SettingInput label="Run interval (sec)" description="How often live incident extraction considers recent transcribed calls." type="number" value={aiInsights.incidentRunIntervalSeconds ?? 300} onChange={v => update("ai-insights", ["incidentRunIntervalSeconds"], numberOrZero(v))} />
          <SettingInput label="Candidate cap" description="Maximum candidate calls included before prompt construction." type="number" value={aiInsights.incidentPromptCandidateLimit ?? 18} onChange={v => update("ai-insights", ["incidentPromptCandidateLimit"], numberOrZero(v))} />
          <SettingInput label="New RAG limit" description="Vector matches considered for new incident candidates." type="number" value={aiInsights.incidentNewVectorQueryLimit ?? 8} onChange={v => update("ai-insights", ["incidentNewVectorQueryLimit"], numberOrZero(v))} />
          <SettingInput label="Active RAG limit" description="Vector matches considered for active incident updates." type="number" value={aiInsights.incidentActiveVectorQueryLimit ?? 6} onChange={v => update("ai-insights", ["incidentActiveVectorQueryLimit"], numberOrZero(v))} />
          <SettingInput label="Verifier RAG limit" description="Maximum extra vector/RAG candidates reviewed around one incident." type="number" value={aiInsights.evidenceVerifierRagCandidateLimit ?? 5} onChange={v => update("ai-insights", ["evidenceVerifierRagCandidateLimit"], numberOrZero(v))} />
          <SettingInput label="Verifier max calls" description="Maximum calls the evidence verifier may retain/review for one incident." type="number" value={aiInsights.evidenceVerifierMaxCalls ?? 8} onChange={v => update("ai-insights", ["evidenceVerifierMaxCalls"], numberOrZero(v))} />
        </div>
      </SettingsCard>}

      {settingsTab === "embeddings" && <SettingsCard title="Embeddings / Qdrant" description="Local vector retrieval for incident matching. Runs as a separate post-transcription pipeline." busy={savingSection === "embeddings"} testing={testingSection === "embeddings"} status={sectionStatus.embeddings} onSave={() => save("embeddings")} onTest={() => test("embeddings")}>
        <SettingSelect label="Execution" description="Local keeps embedding generation on this rig; remote points embedding generation at another OpenAI-compatible endpoint." value={embeddings.executionMode ?? "local"} options={["local", "remote", "lmlink"]} onChange={v => update("embeddings", ["executionMode"], v)} />
        <SettingInput label="Embedding base URL" description="OpenAI-compatible /embeddings endpoint. Local LM Studio normally uses http://localhost:1234/v1." value={embeddings.openAiBaseUrl ?? "http://localhost:1234/v1"} onChange={v => update("embeddings", ["openAiBaseUrl"], v)} />
        <SettingInput label="Embedding model" description="Small CPU-friendly embedding model name. Local LM Studio startup preloads this model only when execution is local." value={embeddings.openAiModel ?? "nomic-embed-text"} onChange={v => update("embeddings", ["openAiModel"], v)} />
        <SettingInput label="Embedding API key" description="Optional bearer token for the embedding endpoint." type="password" value={embeddings.openAiApiKey ?? ""} onChange={v => update("embeddings", ["openAiApiKey"], v)} />
        <SettingInput label="Qdrant URL" description="Local Qdrant HTTP endpoint on this rig." value={embeddings.qdrantBaseUrl ?? "http://localhost:6333"} onChange={v => update("embeddings", ["qdrantBaseUrl"], v)} />
        <SettingInput label="Qdrant API key" description="Optional Qdrant API key." type="password" value={embeddings.qdrantApiKey ?? ""} onChange={v => update("embeddings", ["qdrantApiKey"], v)} />
        <SettingInput label="Qdrant service" description="Systemd service name used for status and restart." value={embeddings.qdrantServiceName ?? "qdrant"} onChange={v => update("embeddings", ["qdrantServiceName"], v)} />
        <SettingInput label="Qdrant storage" description="Native Qdrant data path on this rig." value={embeddings.qdrantStoragePath ?? "/var/lib/pizzawave/qdrant"} onChange={v => update("embeddings", ["qdrantStoragePath"], v)} />
        <SettingInput label="Collection" description="Qdrant collection used for call vectors." value={embeddings.collection ?? "pizzawave_calls"} onChange={v => update("embeddings", ["collection"], v)} />
        <SettingInput label="Vector size" description="Must match the embedding model output dimension." type="number" value={embeddings.vectorSize ?? 768} onChange={v => update("embeddings", ["vectorSize"], numberOrZero(v))} />
        <SettingInput label="Search limit" description="Maximum vector matches considered before local reranking." type="number" value={embeddings.searchLimit ?? 40} onChange={v => update("embeddings", ["searchLimit"], numberOrZero(v))} />
        <SettingInput label="Search window minutes" description="Recent window used for live incident vector retrieval." type="number" value={embeddings.searchWindowMinutes ?? 120} onChange={v => update("embeddings", ["searchWindowMinutes"], numberOrZero(v))} />
      </SettingsCard>}

      {settingsTab === "alerts" && <SettingsCard title="Alerts / Email" description="Outbound notification settings for live alert matches." busy={savingSection === "alerts"} testing={testingSection === "alerts"} status={sectionStatus.alerts} onSave={() => save("alerts")} onTest={() => test("alerts")}>
        <SettingCheckbox label="Enable email alerts" description="Turns live outbound email delivery on or off." checked={alerts.emailEnabled} onChange={v => update("alerts", ["emailEnabled"], v)} />
        <SettingSelect label="Email provider" description="SMTP preset used by the alert sender." value={alerts.emailProvider} options={["gmail", "yahoo"]} onChange={v => update("alerts", ["emailProvider"], v)} />
        <SettingInput label="Email address" description="Sender account used for alert delivery." value={alerts.emailUser} onChange={v => update("alerts", ["emailUser"], v)} />
        <SettingInput label="App password" description="Provider-specific app password or SMTP credential." type="password" value={alerts.emailPassword} onChange={v => update("alerts", ["emailPassword"], v)} />
        <div className="settings-subsection">
          <h4>Administrative outage alerts</h4>
          <SettingCheckbox label="Email on infrastructure outages" description="Emails an administrator when the configured remote transcription endpoint remains unavailable." checked={!!alerts.administrativeEmailEnabled} onChange={v => update("alerts", ["administrativeEmailEnabled"], v)} />
          <SettingInput label="Administrator recipients" description="Comma-separated email addresses for outage and recovery notices." value={alerts.administrativeEmailRecipients ?? ""} onChange={v => update("alerts", ["administrativeEmailRecipients"], v)} />
          <SettingInput className="three-digit-input" label="Outage delay (minutes)" description="Wait this long before sending one outage notice. Recovery sends a second notice." type="number" value={alerts.administrativeOutageDelayMinutes ?? 2} onChange={v => update("alerts", ["administrativeOutageDelayMinutes"], Math.max(1, Math.min(60, numberOrZero(v))))} />
        </div>
        <div className="settings-subsection playback-settings">
          <h4>Autoplay</h4>
          <SettingCheckbox label="Enable autoplay" description="Browser-side playback for selected live events. Use the bell in the top bar to mute/unmute locally." checked={!!alerts.playback?.enabled} onChange={v => update("alerts", ["playback", "enabled"], v)} />
          <SettingCheckbox label="Alert matches" description="Play the matching call when an active alert rule matches." checked={alerts.playback?.alertMatches !== false} onChange={v => update("alerts", ["playback", "alertMatches"], v)} />
          <SettingCheckbox label="New incidents" description="Play the first call when a new incident appears on the dashboard." checked={!!alerts.playback?.newIncidents} onChange={v => update("alerts", ["playback", "newIncidents"], v)} />
          <SettingCheckbox label="Traffic incidents" description="Play the first call when a new traffic incident appears." checked={!!alerts.playback?.trafficIncidents} onChange={v => update("alerts", ["playback", "trafficIncidents"], v)} />
          <SettingCheckbox label="All police calls" description="Play newly transcribed police-category calls. Use cautiously on busy systems." checked={!!alerts.playback?.policeCalls} onChange={v => update("alerts", ["playback", "policeCalls"], v)} />
          <SettingInput className="three-digit-input" label="Cooldown seconds" description="Minimum time between browser autoplay attempts." type="number" value={alerts.playback?.cooldownSeconds ?? 15} onChange={v => update("alerts", ["playback", "cooldownSeconds"], numberOrZero(v))} />
          <SettingInput className="three-digit-input" label="Repeat" description="How many times to play each selected call, from 1 to 10." type="number" value={alerts.playback?.repeatCount ?? 1} onChange={v => update("alerts", ["playback", "repeatCount"], Math.max(1, Math.min(10, numberOrZero(v))))} />
        </div>
        <AlertRulesEditor rules={alerts.rules ?? []} baselineRules={baselineSections.alerts?.rules ?? []} onChange={rules => update("alerts", ["rules"], rules)} />
      </SettingsCard>}

      {settingsTab === "profiles" && <ProfilesSettingsCard reload={reload} pendingHides={pendingProfileHides} setPendingHides={setPendingProfileHides} onDirtyChange={setProfilesDirty} />}

      {settingsTab === "admin" && <SettingsCard title="Security" description="Protects write, setup, and service-control actions. Dashboard reads stay open unless read auth is enabled." busy={savingSection === "auth"} status={sectionStatus.auth} onSave={() => save("auth")}>
        <SettingSelect label="Mode" description="Use token for normal deployments. None disables PizzaWave API authorization." value={auth.mode ?? "token"} options={["token", "none"]} onChange={v => update("auth", ["mode"], v)} />
        <SettingCheckbox label="Require token for writes" description="Recommended. Protects settings, setup, service restarts, and other admin actions." checked={!!auth.writeRequiresAuth} onChange={v => update("auth", ["writeRequiresAuth"], v)} />
        <SettingCheckbox label="Require token for reads" description="Optional. Enables a private dashboard, but increases browser/token friction." checked={!!auth.readRequiresAuth} onChange={v => update("auth", ["readRequiresAuth"], v)} />
        <SettingValue label="Token file" value={auth.tokenFile ?? "/etc/pizzawave/pizzad.token"} />
        {tokenDisplay && <SettingValue label="Current token" value={tokenDisplay} />}
        <div className="setting-inline-actions">
          <button type="button" onClick={() => void revealAdminToken()}>Reveal token</button>
          <button type="button" onClick={() => void rotateAdminToken()}>Rotate token</button>
          <span className="muted">Rotation invalidates other browsers and scripts until they use the new token.</span>
        </div>
      </SettingsCard>}

      {settingsTab === "system" && <SettingsCard title="System Info" description="High-level identity, installer-owned paths, and listener settings." busy={savingSection === "engine"} status={sectionStatus.engine} onSave={() => save("engine")}>
          <SettingInput label="Site name" description="Shown beneath the PizzaWave logo." value={engine.branding?.stackName ?? "PizzaWave"} onChange={v => update("engine", ["branding", "stackName"], v)} />
          <SettingValue label="Web endpoint" value={`${engine.server?.httpBind ?? "0.0.0.0"}:${engine.server?.httpPort ?? 8080}`} />
          <SettingValue label="Callstream endpoint" value={`${engine.ingest?.callstreamBind ?? "127.0.0.1"}:${engine.ingest?.callstreamPort ?? 9123}`} />
          <SettingValue label="Database path" value={engine.storage?.databasePath} />
          <SettingValue label="Audio root" value={engine.storage?.audioRoot} />
          <SettingValue label="TR config path" value={tr.configPath} />
          <SettingValue label="Talkgroup catalog" value={tr.talkgroupCatalogPath} />
          <SettingValue label="Talkgroups CSV" value={tr.talkgroupsPath} />
          <SettingValue label="TR service name" value={tr.logServiceName} />
          <SettingValue label="Transcription provider" value={transcription.provider} />
          <SettingValue label="AI endpoint" value={aiInsights.openAiBaseUrl} />
          <SettingValue label="AI model" value={aiInsights.openAiModel} />
          <SettingValue label="Qdrant endpoint" value={embeddings.qdrantBaseUrl} />
          <SettingValue label="Qdrant storage" value={embeddings.qdrantStoragePath} />
          <SettingValue label="Security mode" value={`${auth.mode ?? "token"} / writes ${auth.writeRequiresAuth ? "protected" : "open"} / reads ${auth.readRequiresAuth ? "protected" : "open"}`} />
          <SettingValue label="Token file" value={auth.tokenFile} />
      </SettingsCard>}
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
        <button className="danger-button" disabled={busy || testing} onClick={() => void onSave()}>{busy ? "Saving..." : "Save"}</button>
      </div>
      {status && <span className={`section-status ${status.kind}`}>{status.text}</span>}
    </div>
    <div className="settings-fields">{children}</div>
  </div>;
}

function ProfilesSettingsCard({ reload, pendingHides, setPendingHides, onDirtyChange }: { reload: () => Promise<void>; pendingHides: ProfileTalkgroupSetting[]; setPendingHides: React.Dispatch<React.SetStateAction<ProfileTalkgroupSetting[]>>; onDirtyChange: (dirty: boolean) => void }) {
  type ProfileTalkgroupSetting = NonNullable<ProcessingProfile["talkgroups"]>[number];
  const [profileState, setProfileState] = useState<ProfileState | null>(null);
  const [baselineProfileState, setBaselineProfileState] = useState<ProfileState | null>(null);
  const [catalog, setCatalog] = useState<TalkgroupCatalogDocument | null>(null);
  const [selectedProfileId, setSelectedProfileId] = useState("");
  const [filter, setFilter] = useState("");
  const [categoryFilter, setCategoryFilter] = useState("all");
  const [visibilityFilter, setVisibilityFilter] = useState<"all" | "shown" | "hidden" | "tr-excluded">("all");
  const [busy, setBusy] = useState("");
  const [message, setMessage] = useState("");
  const [draftProfileId, setDraftProfileId] = useState("");
  const appliedPendingKeyRef = useRef("");

  const profilesResource = usePersistentRefresh({
    key: "settings-profiles",
    enabled: true,
    load: async () => {
      const [profiles, catalogResponse] = await Promise.all([
        api.request<ProfileState>("/api/v1/profiles"),
        api.request<TalkgroupCatalogResponse>("/api/v1/talkgroups/catalog")
      ]);
      return { profiles, catalog: catalogResponse.document };
    },
    onSuccess: result => {
      setProfileState(result.profiles);
      setBaselineProfileState(cloneSettings(result.profiles));
      setCatalog(result.catalog);
      setSelectedProfileId(result.profiles.activeProfileId || result.profiles.profiles[0]?.id || "");
      setMessage("");
    }
  });
  useEffect(() => {
    if (!pendingHides.length || !profileState)
      return;
    const pendingKey = pendingHides.map(profileSettingKey).sort().join("|");
    if (appliedPendingKeyRef.current === pendingKey && draftProfileId && profileState.profiles.some(profile => profile.id === draftProfileId))
      return;
    const base = profileState.profiles.find(profile => profile.id === profileState.activeProfileId) ?? profileState.profiles[0];
    let targetId = draftProfileId;
    let created = false;
    const existingTarget = targetId ? profileState.profiles.find(profile => profile.id === targetId) : null;
    const profiles = [...profileState.profiles];
    if (!existingTarget || isDefaultProfile(existingTarget)) {
      const profile: ProcessingProfile = {
        ...(base ? cloneSettings(base) : {
          includePolice: true,
          includeFire: true,
          includeEMS: true,
          includeTraffic: true,
          includeUtilities: true,
          includeOther: true,
          talkgroups: []
        }),
        id: newClientGuid(),
        name: uniqueProfileName(profileState.profiles, "New Profile"),
        createdAtUtc: new Date().toISOString(),
        updatedAtUtc: new Date().toISOString()
      };
      targetId = profile.id;
      created = true;
      profiles.push(profile);
      setDraftProfileId(targetId);
    }

    const pendingKeys = new Set(pendingHides.map(profileSettingKey));
    const nextProfiles = profiles.map(profile => {
      if (profile.id !== targetId)
        return profile;
      const without = (profile.talkgroups ?? []).filter(setting => !pendingKeys.has(profileSettingKey(setting)));
      return {
        ...profile,
        talkgroups: [...without, ...pendingHides],
        updatedAtUtc: new Date().toISOString()
      };
    });
    setProfileState({ ...profileState, profiles: nextProfiles });
    setSelectedProfileId(targetId);
    setVisibilityFilter("hidden");
    setMessage(`${created ? "Created" : "Updated"} draft profile with ${pendingHides.length.toLocaleString()} selected TG${pendingHides.length === 1 ? "" : "s"} to hide. Rename it, then Save Profiles.`);
    appliedPendingKeyRef.current = pendingKey;
  }, [pendingHides, profileState, draftProfileId, setPendingHides]);

  const profiles = profileState?.profiles ?? [];
  const selectedProfile = profiles.find(profile => profile.id === selectedProfileId) ?? profiles[0];
  const catalogItems = catalog?.items ?? [];
  const categoryOptions = Array.from(new Set(catalogItems.map(item => item.opsCategory || "other"))).sort();
  const rows = catalogItems
    .filter(item => categoryFilter === "all" || (item.opsCategory || "other") === categoryFilter)
    .filter(item => {
      const hidden = isProfileHidden(selectedProfile, item);
      if (visibilityFilter === "hidden") return hidden;
      if (visibilityFilter === "shown") return !hidden && item.enabled;
      if (visibilityFilter === "tr-excluded") return !item.enabled;
      return true;
    })
    .filter(item => {
      const needle = filter.trim().toLowerCase();
      return !needle ||
        String(item.id).includes(needle) ||
        (item.alphaTag ?? "").toLowerCase().includes(needle) ||
        (item.description ?? "").toLowerCase().includes(needle) ||
        (item.systemShortName ?? "").toLowerCase().includes(needle) ||
        (item.tag ?? "").toLowerCase().includes(needle) ||
        (item.opsCategory ?? "").toLowerCase().includes(needle);
    })
    .sort((a, b) => (a.systemShortName || "").localeCompare(b.systemShortName || "", undefined, { sensitivity: "base" }) || a.id - b.id)
    .slice(0, 500);
  const hiddenCount = selectedProfile?.talkgroups?.filter(setting => setting.enabled === false).length ?? 0;
  const trExcludedCount = catalogItems.filter(item => !item.enabled).length;
  const defaultSelected = isDefaultProfile(selectedProfile);
  const profilesDirty = Boolean(profileState && baselineProfileState && JSON.stringify(profileState) !== JSON.stringify(baselineProfileState));
  useEffect(() => {
    onDirtyChange(profilesDirty);
    return () => onDirtyChange(false);
  }, [profilesDirty, onDirtyChange]);

  function updateSelectedProfile(update: (profile: ProcessingProfile) => ProcessingProfile) {
    if (!profileState || !selectedProfile || defaultSelected) return;
    setProfileState({
      ...profileState,
      profiles: profileState.profiles.map(profile => profile.id === selectedProfile.id ? update(profile) : profile)
    });
  }

  function createProfile() {
    if (!profileState) return;
    const base = selectedProfile ?? profileState.profiles[0];
    const profile: ProcessingProfile = {
      ...(base ? cloneSettings(base) : {
        includePolice: true,
        includeFire: true,
        includeEMS: true,
        includeTraffic: true,
        includeUtilities: true,
        includeOther: true,
        talkgroups: []
      }),
      id: newClientGuid(),
      name: uniqueProfileName(profileState.profiles, base ? `${base.name} Copy` : "New Profile"),
      createdAtUtc: new Date().toISOString(),
      updatedAtUtc: new Date().toISOString()
    };
    setProfileState({ ...profileState, profiles: [...profileState.profiles, profile] });
    setSelectedProfileId(profile.id);
    setDraftProfileId(profile.id);
  }

  function deleteProfile() {
    if (!profileState || !selectedProfile || defaultSelected || selectedProfile.id === profileState.activeProfileId || profileState.profiles.length <= 1) return;
    if (!confirmAction("Delete profile?", `Delete ${selectedProfile.name}? Profile-local hidden talkgroup rules for this profile will be removed.`)) return;
    const remaining = profileState.profiles.filter(profile => profile.id !== selectedProfile.id);
    setProfileState({ ...profileState, profiles: remaining });
    setSelectedProfileId(profileState.activeProfileId);
  }

  function renameProfile(name: string) {
    updateSelectedProfile(profile => ({ ...profile, name, updatedAtUtc: new Date().toISOString() }));
  }

  function setProfileCategory(category: keyof Pick<ProcessingProfile, "includePolice" | "includeFire" | "includeEMS" | "includeTraffic" | "includeUtilities" | "includeOther">, value: boolean) {
    updateSelectedProfile(profile => ({ ...profile, [category]: value, updatedAtUtc: new Date().toISOString() }));
  }

  function setHidden(item: TalkgroupCatalogItem, hidden: boolean) {
    const key = talkgroupCatalogKey(item);
    if (!hidden)
      setPendingHides(current => current.filter(setting => profileSettingKey(setting) !== key));
    else if (selectedProfile?.id === draftProfileId)
      setPendingHides(current => mergeProfileTalkgroupSettings(current, [{
        key,
        systemShortName: item.systemShortName,
        id: item.id,
        enabled: false,
        label: item.alphaTag || item.description || "",
        category: item.opsCategory || "other",
        incidentEligible: null
      }]));
    updateSelectedProfile(profile => {
      const existing = profile.talkgroups ?? [];
      const without = existing.filter(setting => profileSettingKey(setting) !== key);
      const nextSetting: ProfileTalkgroupSetting = {
        key,
        systemShortName: item.systemShortName,
        id: item.id,
        enabled: hidden ? false : null,
        label: item.alphaTag || item.description || "",
        category: item.opsCategory || "other",
        incidentEligible: null
      };
      return {
        ...profile,
        talkgroups: hidden ? [...without, nextSetting] : without,
        updatedAtUtc: new Date().toISOString()
      };
    });
  }

  async function saveProfiles() {
    if (!profileState) return;
    setBusy("save");
    try {
      const editedProfileId = selectedProfileId;
      const activeProfileId = profileState.activeProfileId || profileState.profiles[0]?.id;
      const saved = await api.request<ProfileState>("/api/v1/profiles", {
        method: "POST",
        body: JSON.stringify({ activeProfileId, profiles: profileState.profiles })
      });
      setProfileState(saved);
      setBaselineProfileState(cloneSettings(saved));
      setSelectedProfileId(saved.profiles.some(profile => profile.id === editedProfileId) ? editedProfileId : saved.activeProfileId);
      setDraftProfileId("");
      appliedPendingKeyRef.current = "";
      setPendingHides([]);
      setMessage(saved.message || "Profiles saved.");
      await reload();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Unable to save profiles.");
    } finally {
      setBusy("");
    }
  }

  return <section className="card settings-card wide profiles-settings-card">
    <div className="settings-card-meta">
      <h3>Profiles</h3>
      <p>Profiles hide talkgroups from user-facing views and downstream processing without changing TR capture.</p>
      <div className="settings-card-actions">
        <button className="danger-button" disabled={!profileState || busy === "save"} onClick={() => void saveProfiles()}>{busy === "save" ? "Saving..." : "Save Profiles"}</button>
      </div>
      {message && <span className={message.toLowerCase().includes("unable") || message.toLowerCase().includes("fail") ? "section-status error" : "section-status ok"}>{message}</span>}
    </div>
    <PanelLoadState label="profiles and talkgroups" state={profilesResource.state} hasData={Boolean(profileState && catalog)} onRetry={profilesResource.refresh} />
    {!selectedProfile ? <p className="muted">No profiles loaded.</p> : <div className="settings-fields">
      <div className="profile-editor-grid">
        <label className="setting-field"><span>Profile</span><div><select value={selectedProfile.id} onChange={event => setSelectedProfileId(event.target.value)}>{profiles.map(profile => <option value={profile.id} key={profile.id}>{profile.name}{profile.id === profileState?.activeProfileId ? " (active)" : ""}</option>)}</select></div></label>
        <label className={`setting-field profile-name-field ${!defaultSelected && selectedProfile.id === draftProfileId ? "needs-name" : ""}`}>
          <span>Name{!defaultSelected && <small>{selectedProfile.id === draftProfileId ? "Type a name for this new profile before saving." : "Display name for this profile."}</small>}</span>
          <div>
            <input value={selectedProfile.name} disabled={defaultSelected} autoFocus={!defaultSelected && selectedProfile.id === draftProfileId} onChange={event => renameProfile(event.target.value)} />
            {!defaultSelected && selectedProfile.id === draftProfileId && <span className="info-tip" tabIndex={0}>i<span>Rename this draft to describe what it hides, then save the profile.</span></span>}
          </div>
        </label>
        <div className="setting-inline-actions">
          <button type="button" onClick={createProfile}>Duplicate</button>
          <button type="button" className="danger-button" disabled={defaultSelected || selectedProfile.id === profileState?.activeProfileId || profiles.length <= 1} title={selectedProfile.id === profileState?.activeProfileId ? "Activate another profile before deleting this one." : undefined} onClick={deleteProfile}>Delete</button>
        </div>
      </div>
      {!defaultSelected && <ProfileHiddenTalkgroupsReview profile={selectedProfile} catalogItems={catalogItems} onShow={setting => {
        const key = profileSettingKey(setting);
        setPendingHides(current => current.filter(row => profileSettingKey(row) !== key));
        updateSelectedProfile(profile => ({
          ...profile,
          talkgroups: (profile.talkgroups ?? []).filter(row => profileSettingKey(row) !== key),
          updatedAtUtc: new Date().toISOString()
        }));
      }} />}
      <div className="settings-subsection profile-category-controls">
        <h4>Category Visibility</h4>
        <div className="alert-rule-checks">
          <label><input type="checkbox" disabled={defaultSelected} checked={selectedProfile.includePolice} onChange={event => setProfileCategory("includePolice", event.currentTarget.checked)} /> Police</label>
          <label><input type="checkbox" disabled={defaultSelected} checked={selectedProfile.includeFire} onChange={event => setProfileCategory("includeFire", event.currentTarget.checked)} /> Fire</label>
          <label><input type="checkbox" disabled={defaultSelected} checked={selectedProfile.includeEMS} onChange={event => setProfileCategory("includeEMS", event.currentTarget.checked)} /> EMS</label>
          <label><input type="checkbox" disabled={defaultSelected} checked={selectedProfile.includeTraffic} onChange={event => setProfileCategory("includeTraffic", event.currentTarget.checked)} /> Traffic</label>
          <label><input type="checkbox" disabled={defaultSelected} checked={selectedProfile.includeUtilities} onChange={event => setProfileCategory("includeUtilities", event.currentTarget.checked)} /> Utilities</label>
          <label><input type="checkbox" disabled={defaultSelected} checked={selectedProfile.includeOther} onChange={event => setProfileCategory("includeOther", event.currentTarget.checked)} /> Other</label>
        </div>
      </div>
      <div className="talkgroup-catalog-table">
        <div className="table-top-pagination">
          <input placeholder="Search TGs" value={filter} onChange={event => setFilter(event.target.value)} />
          <select value={categoryFilter} onChange={event => setCategoryFilter(event.target.value)}>
            <option value="all">All categories</option>
            {categoryOptions.map(category => <option value={category} key={category}>{label(category)}</option>)}
          </select>
          <select value={visibilityFilter} onChange={event => setVisibilityFilter(event.target.value as typeof visibilityFilter)}>
            <option value="all">All TGs</option>
            <option value="shown">Shown in profile</option>
            <option value="hidden">Hidden in profile</option>
            <option value="tr-excluded">TR excluded</option>
          </select>
          <span className="muted">{rows.length.toLocaleString()} shown / {hiddenCount.toLocaleString()} hidden / {trExcludedCount.toLocaleString()} TR excluded</span>
        </div>
        <table className="table compact-table">
          <thead><tr><th>Name</th><th>TG ID</th><th>System</th><th>Category</th><th>Profile</th><th>TR</th></tr></thead>
          <tbody>{rows.map(item => {
            const hidden = isProfileHidden(selectedProfile, item);
            return <tr className={!item.enabled || hidden ? "excluded-row" : ""} key={talkgroupCatalogKey(item)}>
              <td className="tg-name-cell">{item.alphaTag || item.description || "--"}</td>
              <td>{item.id}</td>
              <td>{item.systemShortName || "--"}</td>
              <td>{label(item.opsCategory || "other")}</td>
              <td><button type="button" disabled={defaultSelected || !item.enabled} className={hidden ? "danger-button" : ""} title={defaultSelected ? "Default is read-only. Duplicate it to edit TG visibility." : undefined} onClick={() => setHidden(item, !hidden)}>{hidden ? "Show" : "Hide"}</button></td>
              <td>{item.enabled ? "Included" : "Excluded"}</td>
            </tr>;
          })}</tbody>
        </table>
      </div>
    </div>}
  </section>;
}

function isProfileHidden(profile: ProcessingProfile | undefined, item: TalkgroupCatalogItem) {
  if (!profile) return false;
  return (profile.talkgroups ?? []).some(setting => profileSettingKey(setting) === talkgroupCatalogKey(item) && setting.enabled === false);
}

function ProfileHiddenTalkgroupsReview({ profile, catalogItems, onShow }: { profile: ProcessingProfile; catalogItems: TalkgroupCatalogItem[]; onShow: (setting: ProfileTalkgroupSetting) => void }) {
  const hidden = (profile.talkgroups ?? [])
    .filter(setting => setting.enabled === false)
    .sort((a, b) => (a.systemShortName || "").localeCompare(b.systemShortName || "", undefined, { sensitivity: "base" }) || a.id - b.id);
  if (!hidden.length)
    return <div className="settings-message info">No TGs are hidden in this profile yet.</div>;
  const catalogByKey = new Map(catalogItems.map(item => [talkgroupCatalogKey(item), item]));
  return <div className="profile-hidden-review">
    <div className="profile-hidden-review-head">
      <h4>TGs Hidden By This Profile</h4>
      <span className="muted">{hidden.length.toLocaleString()} selected</span>
    </div>
    <div className="profile-hidden-pill-list">
      {hidden.map(setting => {
        const item = catalogByKey.get(profileSettingKey(setting));
        const name = setting.label || item?.alphaTag || item?.description || `TG ${setting.id}`;
        const category = setting.category || item?.opsCategory || "other";
        return <span className="profile-hidden-pill" key={profileSettingKey(setting)}>
          <strong>{name}</strong>
          <small>{setting.systemShortName || item?.systemShortName || "--"} / TG {setting.id} / {label(category)}</small>
          <button type="button" title="Show this TG in the profile" onClick={() => onShow(setting)}>x</button>
        </span>;
      })}
    </div>
  </div>;
}

function isDefaultProfile(profile?: Pick<ProcessingProfile, "name"> | null) {
  return (profile?.name ?? "").trim().toLowerCase() === "default";
}

function profileSettingKey(setting: { key?: string; systemShortName?: string; id: number }) {
  return setting.key?.trim().toLowerCase() || `${normalizeTalkgroupSystem(setting.systemShortName || "")}:${setting.id}`;
}

function mergeProfileTalkgroupSettings(current: ProfileTalkgroupSetting[], incoming: ProfileTalkgroupSetting[]) {
  const byKey = new Map<string, ProfileTalkgroupSetting>();
  for (const setting of current)
    byKey.set(profileSettingKey(setting), setting);
  for (const setting of incoming)
    byKey.set(profileSettingKey(setting), setting);
  return Array.from(byKey.values());
}

function categoryGroupKey(group: Pick<CategoryPage["groups"][number], "talkgroupKey" | "systemShortName" | "talkgroup">) {
  const key = String(group.talkgroupKey ?? "").trim().toLowerCase();
  if (key) return key;
  const system = normalizeTalkgroupSystem(group.systemShortName || "");
  return system ? `${system}:${group.talkgroup}` : String(group.talkgroup);
}

function profileSettingForGroup(group: CategoryPage["groups"][number], category: string): ProfileTalkgroupSetting {
  const systemShortName = normalizeTalkgroupSystem(group.systemShortName || "");
  const key = categoryGroupKey(group);
  return {
    key,
    systemShortName,
    id: group.talkgroup,
    enabled: false,
    label: group.label || `TG ${group.talkgroup}`,
    category: category || "other",
    incidentEligible: null
  };
}

function newClientGuid() {
  return typeof crypto !== "undefined" && "randomUUID" in crypto
    ? crypto.randomUUID()
    : "10000000-1000-4000-8000-100000000000".replace(/[018]/g, char =>
      (Number(char) ^ Math.floor(Math.random() * 16) >> Number(char) / 4).toString(16));
}

function uniqueProfileName(profiles: ProcessingProfile[], base: string) {
  const names = new Set(profiles.map(profile => profile.name.trim().toLowerCase()));
  if (!names.has(base.trim().toLowerCase())) return base;
  for (let i = 2; i < 100; i++) {
    const candidate = `${base} ${i}`;
    if (!names.has(candidate.trim().toLowerCase())) return candidate;
  }
  return `${base} ${Date.now()}`;
}

function AlertRulesEditor({ rules, baselineRules, onChange }: { rules: any[]; baselineRules: any[]; onChange: (rules: any[]) => void }) {
  const savedIds = new Set((baselineRules ?? []).map(rule => String(rule.id ?? "")));
  const emptyDraft = {
    name: "",
    enabled: true,
    matchType: "keyword",
    keywords: "",
    policeCodes: "",
    email: "",
    frequency: "realtime",
    autoplay: true,
    talkgroups: [] as AlertTalkgroupRef[]
  };
  const [draft, setDraft] = useState<any>(emptyDraft);
  const [editingIndex, setEditingIndex] = useState<number | null>(null);
  const [talkgroupSearch, setTalkgroupSearch] = useState("");
  const [talkgroupCandidateKey, setTalkgroupCandidateKey] = useState("");
  const scopeResource = usePersistentRefresh({
    key: "settings-alert-scope",
    enabled: true,
    load: async () => {
      const [profiles, catalogResponse] = await Promise.all([
        api.request<ProfileState>("/api/v1/profiles"),
        api.request<TalkgroupCatalogResponse>("/api/v1/talkgroups/catalog")
      ]);
      return { profiles, catalog: catalogResponse.document };
    }
  });
  const activeProfile = scopeResource.data?.profiles.profiles.find(profile => profile.id === scopeResource.data?.profiles.activeProfileId);
  const catalogItems = (scopeResource.data?.catalog.items ?? []).filter(item => item.enabled);
  const selectedTalkgroupKeys = new Set((draft.talkgroups as AlertTalkgroupRef[]).map(alertTalkgroupKey));
  const talkgroupCandidates = catalogItems
    .filter(item => {
      const needle = talkgroupSearch.trim().toLowerCase();
      return !needle || [item.systemShortName, item.id, item.alphaTag, item.description].some(value => String(value ?? "").toLowerCase().includes(needle));
    })
    .filter(item => !selectedTalkgroupKeys.has(talkgroupCatalogKey(item)))
    .sort((left, right) => (left.systemShortName || "").localeCompare(right.systemShortName || "", undefined, { sensitivity: "base" }) || left.id - right.id)
    .slice(0, 100);
  const normalizedDraftMatchType = normalizeAlertMatchType(draft.matchType);
  const draftHasCriteria = normalizedDraftMatchType === "keyword"
    ? Boolean(String(draft.keywords ?? "").trim())
    : normalizedDraftMatchType === "police_code"
      ? Boolean(String(draft.policeCodes ?? "").trim())
      : Boolean(String(draft.keywords ?? "").trim() || String(draft.policeCodes ?? "").trim());

  useEffect(() => {
    if (editingIndex !== null && editingIndex >= rules.length) {
      setEditingIndex(null);
      setDraft(emptyDraft);
    }
  }, [editingIndex, rules.length]);

  function updateRule(index: number, patch: Record<string, any>) {
    onChange(rules.map((rule, i) => i === index ? { ...rule, ...patch } : rule));
  }

  function patchDraft(patch: Record<string, any>) {
    setDraft((current: any) => ({ ...current, ...patch }));
  }

  function normalizeDraftRule(existing?: any) {
    const matchType = normalizeAlertMatchType(draft.matchType);
    return {
      ...(existing ?? {}),
      id: existing?.id ?? createClientId(),
      name: (draft.name || "New alert").trim(),
      enabled: draft.enabled !== false,
      matchType,
      keywords: draft.keywords ?? "",
      policeCodes: draft.policeCodes ?? "",
      email: draft.email ?? "",
      frequency: draft.frequency || "realtime",
      autoplay: draft.autoplay !== false,
      talkgroups: (draft.talkgroups ?? []).map((talkgroup: AlertTalkgroupRef) => ({ systemShortName: talkgroup.systemShortName, id: talkgroup.id }))
    };
  }

  function addOrUpdateRule() {
    if (editingIndex === null) {
      onChange([...rules, normalizeDraftRule()]);
    } else {
      onChange(rules.map((rule, index) => index === editingIndex ? normalizeDraftRule(rule) : rule));
    }
    setEditingIndex(null);
    setDraft(emptyDraft);
  }

  function editRule(index: number) {
    const rule = rules[index] ?? {};
    setEditingIndex(index);
    setDraft({
      name: rule.name ?? "",
      enabled: rule.enabled !== false,
      matchType: normalizeAlertMatchType(rule.matchType),
      keywords: rule.keywords ?? "",
      policeCodes: rule.policeCodes ?? "",
      email: rule.email ?? "",
      frequency: rule.frequency ?? "realtime",
      autoplay: rule.autoplay !== false,
      talkgroups: normalizeAlertTalkgroups(rule.talkgroups)
    });
  }

  function clearForm() {
    setEditingIndex(null);
    setDraft(emptyDraft);
  }

  function addTalkgroup() {
    const item = catalogItems.find(row => talkgroupCatalogKey(row) === talkgroupCandidateKey);
    if (!item) return;
    patchDraft({ talkgroups: [...normalizeAlertTalkgroups(draft.talkgroups), { systemShortName: item.systemShortName, id: item.id }] });
    setTalkgroupCandidateKey("");
  }

  function removeTalkgroup(talkgroup: AlertTalkgroupRef) {
    patchDraft({ talkgroups: normalizeAlertTalkgroups(draft.talkgroups).filter(row => alertTalkgroupKey(row) !== alertTalkgroupKey(talkgroup)) });
  }

  function deleteRule(index: number) {
    if (!confirmAction("Delete alert rule?", "This removes the rule from the settings draft. It will not take effect until you save Alerts.")) return;
    onChange(rules.filter((_, i) => i !== index));
    if (editingIndex === index)
      clearForm();
  }
  return <div className="alert-rules-editor">
    <div className="recommendation-head">
      <h4>Alert rules</h4>
    </div>
    <PanelLoadState label="alert scope" state={scopeResource.state} hasData={Boolean(scopeResource.data)} onRetry={scopeResource.refresh} />
    <div className="settings-message info">Alert rules apply only to calls allowed by the active profile: <strong>{activeProfile?.name ?? "Unavailable"}</strong>.</div>
    <div className="alert-rule-form">
      <div className="settings-subsection-title">
        <strong>{editingIndex === null ? "New alert" : "Edit alert"}</strong>
        <span>{editingIndex === null ? "Fill out the rule, then add it to the settings draft." : "Update the selected rule, then save Alerts to apply it."}</span>
      </div>
      <div className="alert-rule-grid">
        <SettingInput label="Name" description="Short operator-facing label." value={draft.name} onChange={value => patchDraft({ name: value })} />
        <SettingInput label="Email recipient" description="Optional. Leave empty for UI-only alerts." value={draft.email} onChange={value => patchDraft({ email: value })} />
        <SettingSelect label="Match type" description="Match a keyword, a police code, or either configured criterion." value={draft.matchType} options={["keyword", "police_code", "keyword_or_police_code"]} onChange={value => patchDraft({ matchType: value })} />
        <SettingSelect label="Frequency" description="Limits repeated notification delivery." value={draft.frequency} options={["realtime", "hourly", "daily"]} onChange={value => patchDraft({ frequency: value })} />
        {(draft.matchType === "keyword" || draft.matchType === "keyword_or_police_code") && <SettingTextarea label="Keywords" description="Comma-separated keywords or phrases." value={draft.keywords} onChange={value => patchDraft({ keywords: value })} />}
        {(draft.matchType === "police_code" || draft.matchType === "keyword_or_police_code") && <SettingTextarea label="Police codes" description="Comma-separated codes, for example 10-50, 10-80." value={draft.policeCodes} onChange={value => patchDraft({ policeCodes: value })} />}
      </div>
      <div className="settings-subsection">
        <h4>Talkgroup scope</h4>
        <p className="setting-note">Leave empty for every talkgroup allowed by the active profile. Scoped rules require both system and TG identity.</p>
        <div className="setting-inline-actions">
          <input placeholder="Search system, TG ID, or name" value={talkgroupSearch} onChange={event => setTalkgroupSearch(event.target.value)} />
          <select value={talkgroupCandidateKey} onChange={event => setTalkgroupCandidateKey(event.target.value)}>
            <option value="">Choose talkgroup</option>
            {talkgroupCandidates.map(item => <option value={talkgroupCatalogKey(item)} key={talkgroupCatalogKey(item)}>{item.systemShortName} / TG {item.id} / {item.alphaTag || item.description || "Unnamed"}</option>)}
          </select>
          <button type="button" disabled={!talkgroupCandidateKey} onClick={addTalkgroup}>Add</button>
        </div>
        {normalizeAlertTalkgroups(draft.talkgroups).length > 0 && <div className="profile-hidden-pill-list">
          {normalizeAlertTalkgroups(draft.talkgroups).map(talkgroup => {
            const item = catalogItems.find(row => talkgroupCatalogKey(row) === alertTalkgroupKey(talkgroup));
            return <span className="profile-hidden-pill" key={alertTalkgroupKey(talkgroup)}><strong>{item?.alphaTag || item?.description || `TG ${talkgroup.id}`}</strong><small>{talkgroup.systemShortName} / TG {talkgroup.id}</small><button type="button" title="Remove talkgroup scope" onClick={() => removeTalkgroup(talkgroup)}>x</button></span>;
          })}
        </div>}
      </div>
      <div className="alert-rule-checks">
        <label><input type="checkbox" checked={draft.enabled !== false} onChange={event => patchDraft({ enabled: event.currentTarget.checked })} /> Enabled</label>
        <label><input type="checkbox" checked={draft.autoplay !== false} onChange={event => patchDraft({ autoplay: event.currentTarget.checked })} /> Autoplay when globally enabled</label>
      </div>
      <div className="alert-rule-form-actions">
        <button type="button" className="danger-button" disabled={!draftHasCriteria} title={!draftHasCriteria ? "Add at least one criterion for the selected match type." : undefined} onClick={addOrUpdateRule}>{editingIndex === null ? "Add Alert" : "Update Alert"}</button>
        <button type="button" onClick={clearForm}>Clear Form</button>
      </div>
    </div>
    {rules.length === 0 && <p className="setting-note">No alert rules configured.</p>}
    {rules.length > 0 && <p className="setting-note">Keyword alerts use comma-separated keywords or phrases. Police-code alerts use comma-separated codes.</p>}
    {rules.length > 0 && <div className="alert-rule-list">
      {rules.map((rule, index) => {
        const isDraft = !savedIds.has(String(rule.id ?? ""));
        const matchText = [rule.keywords, rule.policeCodes].filter(Boolean).join(" / ") || "No match text";
        return <div className={`alert-rule-row ${editingIndex === index ? "active" : ""} ${isDraft ? "draft-row" : ""}`} key={rule.id ?? index}>
          <div>
            <strong>{rule.name || "Unnamed alert"}</strong>
            <span>{matchText}</span>
            <small>{normalizeAlertTalkgroups(rule.talkgroups).length ? normalizeAlertTalkgroups(rule.talkgroups).map(alertTalkgroupKey).join(", ") : "All active-profile talkgroups"}</small>
          </div>
          <div className="alert-rule-meta">
            {isDraft ? <span className="draft-badge">Draft</span> : <span className="muted">Saved</span>}
            <span>{label(rule.matchType ?? "keyword")}</span>
            <label><input type="checkbox" checked={rule.enabled !== false} onChange={event => updateRule(index, { enabled: event.currentTarget.checked })} /> Enabled</label>
          </div>
          <div className="alert-rule-actions">
            <button type="button" onClick={() => editRule(index)}>Edit</button>
            <button type="button" className="danger-button" onClick={() => deleteRule(index)}>{isDraft ? "Remove draft" : "Delete"}</button>
          </div>
        </div>;
      })}
    </div>}
  </div>;
}

function normalizeAlertMatchType(value: unknown) {
  const normalized = String(value ?? "keyword").trim().toLowerCase().replaceAll("-", "_");
  if (normalized === "police_code") return "police_code";
  if (normalized === "both" || normalized === "keyword_or_police_code") return "keyword_or_police_code";
  return "keyword";
}

function normalizeAlertTalkgroups(value: unknown): AlertTalkgroupRef[] {
  if (!Array.isArray(value)) return [];
  const byKey = new Map<string, AlertTalkgroupRef>();
  for (const candidate of value) {
    if (!candidate || typeof candidate !== "object" || Array.isArray(candidate)) continue;
    const row = candidate as Partial<AlertTalkgroupRef>;
    const systemShortName = normalizeTalkgroupSystem(row.systemShortName ?? "");
    const id = Number(row.id ?? 0);
    if (!systemShortName || !Number.isSafeInteger(id) || id <= 0) continue;
    const talkgroup = { systemShortName, id };
    byKey.set(alertTalkgroupKey(talkgroup), talkgroup);
  }
  return Array.from(byKey.values());
}

function alertTalkgroupKey(talkgroup: AlertTalkgroupRef) {
  return `${normalizeTalkgroupSystem(talkgroup.systemShortName)}:${talkgroup.id}`;
}

function TalkgroupCatalogSettingsCard({ reloadToken = 0, embedded = false, allowSystemExclusions = false, onCatalogChanged }: { reloadToken?: number; embedded?: boolean; allowSystemExclusions?: boolean; onCatalogChanged?: () => Promise<void> }) {
  const [catalogPage, setCatalogPage] = useState<TalkgroupCatalogPage | null>(null);
  const [filter, setFilterState] = useState(() => embedded ? localStorage.getItem("pizzawave-setup-talkgroup-filter") || "" : "");
  const [candidateTargets, setCandidateTargets] = useState<AlertTalkgroupRef[]>(() => {
    if (!embedded) return [];
    try { return normalizeAlertTalkgroups(JSON.parse(localStorage.getItem("pizzawave-setup-talkgroup-candidates") || "[]")); }
    catch { return []; }
  });
  const [exclusionTargets, setExclusionTargets] = useState<AlertTalkgroupRef[]>(() => {
    if (!embedded) return [];
    try { return normalizeAlertTalkgroups(JSON.parse(localStorage.getItem("pizzawave-setup-talkgroup-exclusion-targets") || "[]")); }
    catch { return []; }
  });
  const [debouncedFilter, setDebouncedFilter] = useState(filter);
  const [enabledFilter, setEnabledFilter] = useState<"all" | "included" | "excluded">("all");
  const [categoryFilter, setCategoryFilter] = useState("all");
  const [sortKey, setSortKey] = useState<"state" | "id" | "name" | "category">("id");
  const [sortDir, setSortDir] = useState<"asc" | "desc">("asc");
  const [page, setPage] = useState(1);
  const [showAllRows, setShowAllRows] = useState(false);
  const [selectedTalkgroupKeys, setSelectedTalkgroupKeys] = useState<Set<string>>(() => new Set(exclusionTargets.map(alertTalkgroupKey)));
  const [busy, setBusy] = useState("");
  const [message, setMessage] = useState("");
  const loadSerialRef = useRef(0);

  useEffect(() => {
    const handle = window.setTimeout(() => setDebouncedFilter(filter), 250);
    return () => window.clearTimeout(handle);
  }, [filter]);
  useEffect(() => { setPage(1); }, [debouncedFilter, enabledFilter, categoryFilter, sortKey, sortDir, showAllRows, candidateTargets]);
  useEffect(() => { setSelectedTalkgroupKeys(new Set()); }, [reloadToken]);
  useEffect(() => { void loadCatalog(); }, [reloadToken, debouncedFilter, enabledFilter, categoryFilter, sortKey, sortDir, page, showAllRows, candidateTargets]);

  function setFilter(value: string) {
    setFilterState(value);
    if (embedded)
      localStorage.setItem("pizzawave-setup-talkgroup-filter", value);
  }

  async function loadCatalog() {
    const serial = ++loadSerialRef.current;
    setBusy("load");
    try {
      const parameters = new URLSearchParams({
        query: debouncedFilter,
        state: enabledFilter,
        category: categoryFilter,
        sort: sortKey,
        direction: sortDir,
        page: String(page),
        pageSize: String(showAllRows ? 10_000 : 50)
      });
      if (candidateTargets.length > 0)
        parameters.set("targets", candidateTargets.map(target => `${normalizeTalkgroupSystem(target.systemShortName)}:${target.id}`).join(","));
      const path = `/api/v1/talkgroups/catalog/page?${parameters}`;
      let loaded: TalkgroupCatalogPage;
      try {
        loaded = await api.request<TalkgroupCatalogPage>(path);
      } catch (error) {
        const transientNetworkFailure = error instanceof TypeError && error.message === "Failed to fetch";
        if (!transientNetworkFailure || serial !== loadSerialRef.current)
          throw error;
        await new Promise(resolve => window.setTimeout(resolve, 400));
        if (serial !== loadSerialRef.current)
          return;
        loaded = await api.request<TalkgroupCatalogPage>(path);
      }
      if (serial === loadSerialRef.current) {
        setCatalogPage(loaded);
        if (exclusionTargets.length > 0)
          setSelectedTalkgroupKeys(new Set(loaded.items.map(talkgroupCatalogKey)));
        setMessage("");
      }
    } catch (error) {
      if (serial === loadSerialRef.current)
        setMessage(error instanceof Error ? error.message : "Unable to load talkgroup catalog.");
    } finally {
      if (serial === loadSerialRef.current)
        setBusy("");
    }
  }

  async function updateCatalogPolicy(targetKeys: string[], patch: { enabled?: boolean; opsCategory?: string }, successMessage: string) {
    const result = await api.request<{ updated: number; message: string }>("/api/v1/talkgroups/catalog/policy", {
      method: "POST",
      body: JSON.stringify({
        targets: targetKeys.map(key => ({ key })),
        ...patch,
        source: "setup-talkgroups"
      })
    });
    setMessage(result.updated > 0 ? successMessage : result.message);
    await loadCatalog();
    if (result.updated > 0)
      await onCatalogChanged?.();
  }

  async function setSystemExcluded(item: TalkgroupCatalogItem, excluded: boolean) {
    if (busy) return;
    setBusy(`catalog-${talkgroupCatalogKey(item)}`);
    setMessage("");
    try {
      const key = talkgroupCatalogKey(item);
      await updateCatalogPolicy([key], { enabled: !excluded }, excluded ? "Talkgroup excluded from generated TR CSV." : "Talkgroup restored to generated TR CSV.");
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Unable to update talkgroup catalog.");
    } finally {
      setBusy("");
    }
  }

  async function setTalkgroupCategory(item: TalkgroupCatalogItem, category: string) {
    if (busy) return;
    const key = talkgroupCatalogKey(item);
    setBusy(`category-${key}`);
    setMessage("");
    try {
      await updateCatalogPolicy([key], { opsCategory: category }, `Talkgroup category changed to ${label(category)}.`);
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Unable to update talkgroup category.");
    } finally {
      setBusy("");
    }
  }

  async function applySelectedCategory(category: string) {
    if (busy || !category || selectedTalkgroupKeys.size === 0) return;
    const targetKeys = [...selectedTalkgroupKeys];
    const targetCount = targetKeys.length;
    if (!confirmAction(`Assign selected talkgroups to ${label(category)}?`, `This will update ${targetCount.toLocaleString()} catalog row(s) and affect new calls going forward.`))
      return;
    setBusy("selected-category");
    setMessage("");
    try {
      await updateCatalogPolicy(targetKeys, { opsCategory: category }, `Assigned ${targetCount.toLocaleString()} selected talkgroup row(s) to ${label(category)}.`);
      setSelectedTalkgroupKeys(new Set());
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Unable to update selected talkgroup categories.");
    } finally {
      setBusy("");
    }
  }

  const categoryOptions = Array.from(new Set([...talkgroupCategoryOptions, ...Object.keys(catalogPage?.categoryCounts ?? {})]));
  function sortBy(key: typeof sortKey) {
    if (sortKey === key)
      setSortDir(current => current === "asc" ? "desc" : "asc");
    else {
      setSortKey(key);
      setSortDir("asc");
    }
  }
  const visibleRows = catalogPage?.items ?? [];
  const currentPage = catalogPage?.page ?? page;
  const pageCount = catalogPage?.pageCount ?? 1;
  const filteredRows = catalogPage?.filteredRows ?? 0;
  const startRow = filteredRows === 0 ? 0 : (currentPage - 1) * (catalogPage?.pageSize ?? 50) + 1;
  const endRow = filteredRows === 0 ? 0 : Math.min(filteredRows, startRow + visibleRows.length - 1);
  const visibleKeys = visibleRows.map(talkgroupCatalogKey);
  const visibleSelectedCount = visibleKeys.filter(key => selectedTalkgroupKeys.has(key)).length;
  const allVisibleSelected = visibleKeys.length > 0 && visibleSelectedCount === visibleKeys.length;
  const enabledCount = catalogPage?.enabledCount ?? 0;
  const excludedCount = catalogPage?.excludedCount ?? 0;
  const loadingCatalog = busy === "load";
  const hasSystemScopedRows = visibleRows.some(item => item.systemShortName);
  const topCategoryCounts = Object.entries(catalogPage?.categoryCounts ?? {})
    .sort(([aCategory, aCount], [bCategory, bCount]) => bCount - aCount || aCategory.localeCompare(bCategory))
    .slice(0, 5);
  function setTalkgroupSelected(key: string, selected: boolean) {
    setSelectedTalkgroupKeys(current => {
      const next = new Set(current);
      if (selected)
        next.add(key);
      else
        next.delete(key);
      return next;
    });
  }
  function clearCandidateFilter() {
    localStorage.removeItem("pizzawave-setup-talkgroup-candidates");
    localStorage.removeItem("pizzawave-setup-talkgroup-exclusion-targets");
    setCandidateTargets([]);
    setExclusionTargets([]);
    setSelectedTalkgroupKeys(new Set());
  }
  function setVisibleTalkgroupsSelected(selected: boolean) {
    setSelectedTalkgroupKeys(current => {
      const next = new Set(current);
      for (const key of visibleKeys) {
        if (selected)
          next.add(key);
        else
          next.delete(key);
      }
      return next;
    });
  }
  return <div className={`${embedded ? "site-setup-catalog-editor" : "card settings-card wide"} talkgroups-settings-card`}>
    <div className="settings-fields">
      {message && <span className={message.toLowerCase().includes("fail") || message.toLowerCase().includes("unable") ? "section-status error" : "section-status ok"}>{message}</span>}
      <div className="talkgroup-catalog-table" aria-busy={loadingCatalog}>
        {candidateTargets.length > 0 && <div className="candidate-filter-banner">
          <span><strong>{exclusionTargets.length ? "Exclusion review" : "Recommendation candidates"}</strong> {exclusionTargets.length ? "The requested talkgroup is selected. Review it, choose Exclude from TR, then use Apply & Resume when you are ready to change live monitoring." : `Showing only the ${candidateTargets.length.toLocaleString()} noisy talkgroup candidate(s) identified by current evidence.`}</span>
          <button type="button" onClick={clearCandidateFilter}>Clear candidate filter</button>
        </div>}
        {topCategoryCounts.length > 0 && <div className="talkgroup-category-summary">
          {topCategoryCounts.map(([category, count]) => <span className={`pill talkgroup-category-pill category-${normalizeTalkgroupSystem(category) || "other"}`} key={category}>{label(category)} {count.toLocaleString()}</span>)}
        </div>}
        <div className="table-top-pagination">
          <input placeholder="Filter TGs" value={filter} onChange={e => setFilter(e.target.value)} />
          <select value={enabledFilter} onChange={e => setEnabledFilter(e.target.value as "all" | "included" | "excluded")}>
            <option value="all">All TGs</option>
            <option value="included">Included only</option>
            <option value="excluded">Excluded only</option>
          </select>
          <select value={categoryFilter} onChange={e => setCategoryFilter(e.target.value)}>
            <option value="all">All categories</option>
            {categoryOptions.map(category => <option value={category} key={category}>{label(category)}</option>)}
          </select>
          <span className="muted">{startRow}-{endRow} of {filteredRows} rows / {enabledCount} included / {excludedCount} excluded</span>
          {selectedTalkgroupKeys.size > 0 && <span className="selected-category-action">
            <span>{selectedTalkgroupKeys.size.toLocaleString()} selected</span>
            <select value="" disabled={Boolean(busy)} onChange={e => { const value = e.target.value; if (value) void applySelectedCategory(value); }} aria-label="Set selected talkgroup category">
              <option value="">Set category...</option>
              {categoryOptions.map(category => <option value={category} key={category}>{label(category)}</option>)}
            </select>
            <button disabled={Boolean(busy)} onClick={() => setSelectedTalkgroupKeys(new Set())}>Clear</button>
          </span>}
          <button disabled={loadingCatalog || currentPage <= 1} onClick={() => setPage(1)}>First</button>
          <button disabled={loadingCatalog || currentPage <= 1} onClick={() => setPage(currentPage - 1)}>Prev</button>
          <span>{currentPage} / {pageCount}</span>
          <button disabled={loadingCatalog || currentPage >= pageCount} onClick={() => setPage(currentPage + 1)}>Next</button>
          <button disabled={loadingCatalog || currentPage >= pageCount} onClick={() => setPage(pageCount)}>Last</button>
          <button disabled={loadingCatalog} onClick={() => { setShowAllRows(current => !current); setPage(1); }}>{showAllRows ? "Paginate" : "Show all"}</button>
        </div>
        <table className="table compact-table talkgroup-catalog-grid">
          <colgroup>
            <col className="tg-select-col" />
            <col className="tg-policy-col" />
            {hasSystemScopedRows && <col className="tg-system-col" />}
            <col className="tg-id-col" />
            <col className="tg-name-col" />
            <col className="tg-category-col" />
            <col className="tg-source-col" />
          </colgroup>
          <thead><tr>
            <th><input type="checkbox" aria-label="Select visible talkgroups" checked={allVisibleSelected} ref={input => { if (input) input.indeterminate = visibleSelectedCount > 0 && !allVisibleSelected; }} onChange={e => setVisibleTalkgroupsSelected(e.currentTarget.checked)} /></th>
            <th><button type="button" className="sort-header" onClick={() => sortBy("state")}>TR Policy {sortKey === "state" ? sortDir : ""}</button></th>
            {hasSystemScopedRows && <th>System</th>}
            <th><button type="button" className="sort-header" onClick={() => sortBy("id")}>TG ID {sortKey === "id" ? sortDir : ""}</button></th>
            <th><button type="button" className="sort-header" onClick={() => sortBy("name")}>Name {sortKey === "name" ? sortDir : ""}</button></th>
            <th><button type="button" className="sort-header" onClick={() => sortBy("category")}>Category {sortKey === "category" ? sortDir : ""}</button></th>
            <th>Source</th>
          </tr></thead>
          <tbody>{visibleRows.map((item, index) => {
            const key = talkgroupCatalogKey(item);
            return <tr className={item.enabled ? "" : "excluded-row"} key={`${talkgroupCatalogKey(item)}-${index}`}>
              <td><input type="checkbox" aria-label={`Select TG ${item.id}`} checked={selectedTalkgroupKeys.has(key)} onChange={e => setTalkgroupSelected(key, e.currentTarget.checked)} /></td>
              <td>
                <span className="talkgroup-policy-cell"><span>{item.enabled ? "Included" : "Excluded"}</span>
                {allowSystemExclusions && <button
                  type="button"
                  className={item.enabled ? "tiny-button" : "tiny-button danger-button"}
                  disabled={Boolean(busy)}
                  onClick={() => void setSystemExcluded(item, item.enabled)}
                >{item.enabled ? "Exclude from TR" : "Restore"}</button>}</span>
              </td>
              {hasSystemScopedRows && <td>{item.systemShortName || "--"}</td>}
              <td>{item.id}</td>
              <td>{item.alphaTag || item.description || "--"}</td>
              <td><select value={item.opsCategory || "other"} disabled={Boolean(busy)} onChange={e => void setTalkgroupCategory(item, e.target.value)}>
                {categoryOptions.map(category => <option value={category} key={category}>{label(category)}</option>)}
              </select></td>
              <td>{item.source || "--"}</td>
            </tr>;
          })}</tbody>
        </table>
      </div>
    </div>
  </div>;
}

function SettingInput({ label: text, description, value, onChange, type = "text", className = "", inputMode, disabled = false }: { label: string; description: string; value: any; onChange: (value: string) => void; type?: string; className?: string; inputMode?: "text" | "decimal" | "numeric" | "search" | "email" | "tel" | "url"; disabled?: boolean }) {
  return <label className="setting-field">
    <span>{text}<small>{description}</small></span>
    <input className={className} type={type} inputMode={inputMode} value={value ?? ""} disabled={disabled} onChange={e => onChange(e.target.value)} />
  </label>;
}

function SettingTextarea({ label: text, description, value, onChange }: { label: string; description: string; value: any; onChange: (value: string) => void }) {
  return <label className="setting-field">
    <span>{text}<small>{description}</small></span>
    <textarea value={value ?? ""} onChange={e => onChange(e.target.value)} />
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

function SettingCheckbox({ label: text, description, checked, onChange, disabled = false }: { label: string; description: string; checked: any; onChange: (value: boolean) => void; disabled?: boolean }) {
  return <label className="setting-checkbox">
    <input type="checkbox" disabled={disabled} checked={Boolean(checked)} onChange={e => onChange(e.target.checked)} />
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

function createClientId() {
  if (typeof crypto !== "undefined" && typeof crypto.randomUUID === "function")
    return crypto.randomUUID();
  const bytes = new Uint8Array(16);
  if (typeof crypto !== "undefined" && typeof crypto.getRandomValues === "function") {
    crypto.getRandomValues(bytes);
  } else {
    for (let i = 0; i < bytes.length; i++)
      bytes[i] = Math.floor(Math.random() * 256);
  }
  bytes[6] = (bytes[6] & 0x0f) | 0x40;
  bytes[8] = (bytes[8] & 0x3f) | 0x80;
  const hex = Array.from(bytes, byte => byte.toString(16).padStart(2, "0"));
  return `${hex.slice(0, 4).join("")}-${hex.slice(4, 6).join("")}-${hex.slice(6, 8).join("")}-${hex.slice(8, 10).join("")}-${hex.slice(10).join("")}`;
}

function normalizeTalkgroupSystem(value?: string | null) {
  return String(value ?? "").trim().replace(/\s+/g, "-").toLowerCase();
}

function talkgroupCatalogKey(item: Pick<TalkgroupCatalogItem, "key" | "systemShortName" | "id">) {
  const key = String(item.key ?? "").trim().toLowerCase();
  if (key) return key;
  const system = normalizeTalkgroupSystem(item.systemShortName);
  return system ? `${system}:${item.id}` : String(item.id);
}

function withSettingsDefaults(value: Record<string, any>) {
  const sections = cloneSettings(value);
  sections.engine = {
    branding: { stackName: "PizzaWave", ...(sections.engine?.branding ?? {}) },
    server: { httpBind: "0.0.0.0", httpPort: 8080, ...(sections.engine?.server ?? {}) },
    ingest: { callstreamBind: "127.0.0.1", callstreamPort: 9123, maxConcurrentClients: 4, ...(sections.engine?.ingest ?? {}) },
    storage: {
      databasePath: "/var/lib/pizzawave/pizzad.db",
      audioRoot: "/var/lib/pizzawave/audio",
      appDataRoot: "/var/lib/pizzawave/appdata",
      ...(sections.engine?.storage ?? {})
    }
  };
  sections.transcription = {
    provider: "whisper",
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
  sections.transcription.provider = !sections.transcription.provider || sections.transcription.provider === "none" ? "whisper" : sections.transcription.provider;
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
    executionMode: inferExecutionMode(sections["ai-insights"]?.executionMode, sections["ai-insights"]?.openAiBaseUrl),
    openAiBaseUrl: "",
    openAiApiKey: "",
    openAiModel: "",
    batchSize: 50,
    maxPendingCalls: 1000,
    timeoutMs: 600000,
    maxRetries: 2,
    ...(sections["ai-insights"] ?? {})
  };
  sections.embeddings = {
    enabled: false,
    executionMode: inferExecutionMode(sections.embeddings?.executionMode, sections.embeddings?.openAiBaseUrl),
    openAiBaseUrl: "http://localhost:1234/v1",
    openAiApiKey: "",
    openAiModel: "nomic-embed-text",
    qdrantBaseUrl: "http://localhost:6333",
    qdrantApiKey: "",
    qdrantServiceName: "qdrant",
    qdrantStoragePath: "/var/lib/pizzawave/qdrant",
    collection: "pizzawave_calls",
    vectorSize: 768,
    workers: 1,
    maxQueueDepthWhenTranscriptionBusy: 25,
    searchLimit: 40,
    searchWindowMinutes: 120,
    ...(sections.embeddings ?? {})
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
    administrativeEmailEnabled: false,
    administrativeEmailRecipients: "",
    administrativeOutageDelayMinutes: 2,
    rules: [],
    playback: {
      enabled: false,
      alertMatches: true,
      newIncidents: false,
      trafficIncidents: false,
      policeCalls: false,
      cooldownSeconds: 15,
      repeatCount: 1
    },
    ...(sections.alerts ?? {})
  };
  sections.alerts.playback = {
    enabled: false,
    alertMatches: true,
    newIncidents: false,
    trafficIncidents: false,
    policeCalls: false,
    cooldownSeconds: 15,
    repeatCount: 1,
    ...(sections.alerts.playback ?? {})
  };
  sections.auth = {
    mode: "token",
    readRequiresAuth: false,
    writeRequiresAuth: true,
    tokenFile: "/etc/pizzawave/pizzad.token",
    ...(sections.auth ?? {})
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
    embeddings: input.embeddings ?? {},
    tr: input.tr ?? input.trunkRecorder ?? {},
    auth: input.auth ?? {},
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
    embeddings: normalized.embeddings,
    trunkRecorder: normalized.tr,
    auth: normalized.auth,
    alerts: normalized.alerts
  };
}

function setupDraftFromStatus(status: SetupStatus) {
  const values = cloneSettings(status.values ?? {});
  values.branding = { stackName: "PizzaWave", ...(values.branding ?? {}) };
  values.setup = {
    installOptionalDiagnosticTools: false,
    diagnosticToolsSkippedOrInstalled: true,
    calibrationSkippedOrCompleted: true,
    radioReferenceSid: "",
    ...(values.setup ?? {})
  };
  values.trunkRecorder = {
    configPath: "/etc/trunk-recorder/config.json",
    talkgroupCatalogPath: "/var/lib/pizzawave/appdata/talkgroups.json",
    talkgroupsPath: "/etc/trunk-recorder/talkgroups.csv",
    logServiceName: "trunk-recorder",
    healthWindowMinutes: 5,
    ...(values.trunkRecorder ?? {})
  };
  values.ingest = {
    callstreamBind: "127.0.0.1",
    callstreamPort: 9123,
    maxConcurrentClients: 4,
    ...(values.ingest ?? {})
  };
  values.transcription = {
    provider: "whisper",
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
    executionMode: "local",
    openAiBaseUrl: "",
    openAiApiKey: "",
    openAiModel: "",
    batchSize: 20,
    maxPendingCalls: 1000,
    timeoutMs: 600000,
    maxRetries: 2,
    ...(values.aiInsights ?? {})
  };
  values.embeddings = {
    enabled: false,
    executionMode: "local",
    openAiBaseUrl: "http://localhost:1234/v1",
    openAiApiKey: "",
    openAiModel: "nomic-embed-text",
    qdrantBaseUrl: "http://localhost:6333",
    qdrantApiKey: "",
    qdrantServiceName: "qdrant",
    qdrantStoragePath: "/var/lib/pizzawave/qdrant",
    collection: "pizzawave_calls",
    vectorSize: 768,
    workers: 1,
    maxQueueDepthWhenTranscriptionBusy: 25,
    searchLimit: 40,
    searchWindowMinutes: 120,
    ...(values.embeddings ?? {})
  };
  values.alerts = {
    emailEnabled: false,
    emailProvider: "gmail",
    emailUser: "",
    emailPassword: "",
    administrativeEmailEnabled: false,
    administrativeEmailRecipients: "",
    administrativeOutageDelayMinutes: 2,
    rules: [],
    ...(values.alerts ?? {})
  };
  values.locations = { monitoredAreas: [], ...(values.locations ?? {}) };
  if (!Array.isArray(values.locations.monitoredAreas)) values.locations.monitoredAreas = [];
  return values;
}

function numberOrZero(value: string) {
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : 0;
}

function inferExecutionMode(value: any, baseUrl: any) {
  const mode = String(value ?? "").trim().toLowerCase();
  if (["local", "remote", "lmlink"].includes(mode)) return mode;
  try {
    const host = new URL(String(baseUrl ?? "")).hostname.toLowerCase();
    return host && !["localhost", "127.0.0.1", "::1"].includes(host) ? "remote" : "local";
  } catch {
    return "local";
  }
}

function normalizeSetupLocationNumbers(values: any) {
  const areas = values?.locations?.monitoredAreas;
  if (!Array.isArray(areas)) return;
  for (const area of areas) {
    for (const key of ["north", "south", "east", "west"]) {
      const parsed = Number(area[key]);
      area[key] = Number.isFinite(parsed) ? parsed : 0;
    }
  }
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

function numericField(value: string) {
  if (!value.trim()) return null;
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : null;
}

function numberOrDefault(value: string, fallback: number) {
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : fallback;
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

function buildGuidedSweepBatchParameters(
  systemShortName: string,
  modulation: string,
  sources: Array<{ index: number; serial: string; sdrType?: string; device?: string; centerFrequency: number; sampleRate: number; errorHz: number; gain: string; input: SweepSourceInput }>,
  controlFrequencyHz: number,
  templateSerial = ""
) {
  return {
    systemShortName,
    modulation: modulation || "qpsk",
    controlChannelHz: controlFrequencyHz,
    sources: sources.map(source => {
      const error = numericField(source.input.errorHz);
      const ppm = numericField(source.input.ppm);
      const baseError = error !== null
        ? Math.round(error)
        : ppm !== null
          ? Math.abs(Math.round(source.centerFrequency * ppm / 1_000_000))
          : source.errorHz || 0;
      return {
        sourceIndex: source.index,
        serial: source.serial || String(source.index),
        baseErrorHz: baseError,
        rangeHz: Math.max(0, numberOrDefault(source.input.rangeHz, 600)),
        stepHz: Math.max(1, numberOrDefault(source.input.stepHz, 300)),
        warmupSec: Math.max(0, numberOrDefault(source.input.warmupSec, 5)),
        durationSec: Math.max(1, numberOrDefault(source.input.durationSec, 20)),
        gain: source.input.gain.trim() || (source.gain && source.gain !== "0" ? source.gain : isAirspyRfSource({ sdrType: source.sdrType ?? "", device: source.device ?? "" }) ? "15" : "32"),
        templateSerial
      };
    })
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

function normalizePage(value: string | null): Page {
  return value === "dashboard" || value === "setup" || value === "system" || value === "settings" || categories.includes(value as any)
    ? value as Page
    : "dashboard";
}

function navIcon(item: Page) {
  if (item === "dashboard") return <Gauge size={15} />;
  if (item === "setup") return <Wrench size={15} />;
  if (item === "settings") return <Settings size={15} />;
  if (item === "system") return <Activity size={15} />;
  return <Radio size={15} />;
}

function label(value: string) {
  if (!value) return "--";
  if (value === "inconclusive" || value === "voice_inconclusive" || value === "unknown") return "Unknown";
  if (value === "ems") return "EMS";
  if (value === "system") return "System";
  if (value === "setup") return "Setup";
  return value[0].toUpperCase() + value.slice(1).replaceAll("_", " ");
}

createRoot(document.getElementById("root")!).render(<App />);
