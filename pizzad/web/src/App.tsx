import React, { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { flushSync } from "react-dom";
import { createRoot } from "react-dom/client";
import { Activity, Bell, BellOff, Camera, CheckCircle2, ChevronDown, ChevronRight, Gauge, Info, Link2, Play, Radio, RefreshCw, Search, Settings, Square, Wrench } from "lucide-react";
import { api, rangeBody, rangeQuery } from "./api";
import type { AuthTokenRequest } from "./api";
import { usePersistentRefresh } from "./refresh";
import type { RefreshState } from "./refresh";
import type { AlertMatch, BackupArchive, BackupEstimate, BackupRestoreApplyResult, BackupRestoreCancelResult, BackupRestorePreview, BarStat, CategoryPage, Dashboard, EngineCall, EngineHealth, HourCategory, Incident, IncidentOperationAuditRow, Job, JobLog, LocationHeat, ProcessingProfile, ProfileState, ProfileTalkgroupSetting, QualityAuditGroup, QualityAuditSample, QualityHour, QueueSnapshot, RemoteBandwidthReport, RfSurveyCancelExperimentResult, RfSurveyConfigDraft, RfSurveyDetail, RfSurveyExperiment, RfSurveyExperimentPlan, RfSurveyPathProfile, RfSurveyProfile, RfSurveySource, RfSurveySweepCandidateProgress, RfSurveySweepProgress, RfSurveySweepProgressRow, RfSurveySystem, RfSurveyTrActionResult, RfSurveyWaterfallStatus, SetupAreaBoundaryCandidate, SetupAreaBoundaryResponse, SetupArtifactReport, SetupCalibrationPlan, SetupSdrDetection, SetupStatus, SetupTalkgroupSyncResult, SetupTrConfigDraft, SetupTrConfigSite, SetupTrConfigSites, SetupValidationResult, SiteSetup, SiteSetupConfig, SiteSetupMonitoredArea, SiteSetupPendingChange, StatusSummary, SystemCpuSnapshot, SystemRecommendations, SystemResetResult, TalkgroupCatalogDocument, TalkgroupCatalogImport, TalkgroupCatalogItem, TalkgroupCatalogPage, TalkgroupCatalogResponse, TokenUsageReport, TopTalkgroup, TrConfigBackup, TrConfigEditor, TrConfigEditorApplyResult, TrConfigRestoreResult, TrHealthChart, TrHealthMetric, TrRfAnalysis, TrTroubleshoot } from "./types";
import "./style.css";

const categories = ["police", "fire", "ems", "traffic", "utilities", "other"] as const;
const talkgroupCategoryOptions = [...categories];
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

function readCachedRadioReferenceSitesForSids(sids: string[]): SetupTrConfigSites | null {
  const lists = sids.map(readCachedRadioReferenceSites).filter((value): value is SetupTrConfigSites => Boolean(value));
  if (lists.length === 0)
    return null;
  if (lists.length === 1)
    return lists[0];
  const sitesByKey = new Map<string, SetupTrConfigSite>();
  for (const list of lists) {
    for (const site of list.sites ?? []) {
      const key = (site.shortName || site.name).trim().toLowerCase();
      if (!key || sitesByKey.has(key))
        continue;
      sitesByKey.set(key, site);
    }
  }
  return {
    radioReferenceSid: lists.map(list => list.radioReferenceSid).filter(Boolean).join(","),
    systemName: lists.map(list => list.systemName).filter(Boolean).join(", "),
    sites: [...sitesByKey.values()],
    diagnostics: lists.map(list => list.diagnostics).filter(Boolean).join("\n")
  };
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

function PageSearch({ value, onChange, placeholder }: { value: string; onChange: (value: string) => void; placeholder: string }) {
  return <label className="global-search page-search">
    <Search size={15} aria-hidden="true" />
    <input type="search" value={value} placeholder={placeholder} aria-label={placeholder} onChange={event => onChange(event.target.value)} />
  </label>;
}

function App() {
  const [page, setPageState] = useState<Page>(() => normalizePage(localStorage.getItem("pizzawave-page")));
  const [rangeHours, setRangeHours] = useState(24);
  const [theme, setTheme] = useState(() => localStorage.getItem("pizzawave-theme") || "blue");
  const [appNotice, setAppNotice] = useState("");
  const [jobs, setJobs] = useState<Job[]>([]);
  const [engineHealth, setEngineHealth] = useState<EngineHealth | null>(null);
  const [statusSummary, setStatusSummary] = useState<StatusSummary | null>(null);
  const [profileState, setProfileState] = useState<ProfileState | null>(null);
  const [pendingProfileHides, setPendingProfileHides] = useState<ProfileTalkgroupSetting[]>([]);
  const [setupStatus, setSetupStatus] = useState<SetupStatus | null>(null);
  const [monitoringCheckedAt, setMonitoringCheckedAt] = useState<number | null>(null);
  const [recommendations, setRecommendations] = useState<SystemRecommendations | null>(null);
  const [cpuSnapshot, setCpuSnapshot] = useState<SystemCpuSnapshot | null>(null);
  const [settingsSections, setSettingsSections] = useState<Record<string, any>>({});
  const [settingsLoadState, setSettingsLoadState] = useState<{ loading: boolean; version: number; message: string; error: boolean }>({ loading: false, version: 0, message: "", error: false });
  const [activeAlertCount, setActiveAlertCount] = useState(0);
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
  const [setupTargetSection, setSetupTargetSection] = useState<string | null>(null);
  const settingsFileInputRef = useRef<HTMLInputElement | null>(null);
  const pageRef = useRef<Page>(page);
  const rangeHoursRef = useRef(rangeHours);
  const lastStatusRefreshAtRef = useRef(0);
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
    if (pageRef.current === "setup" && setupWaterfallActiveRef.current)
      return;
    let setup = setupStatusRef.current;
    const now = Date.now();
    const shouldRefreshSetupStatus =
      !setup ||
      !setup.completed ||
      pageRef.current === "setup" ||
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
    if (!setup.completed) {
      return;
    }

    const [jobRows, summary, profiles, alertRows, alertConfig, cpu] = await Promise.all([
      api.request<Job[]>("/api/v1/jobs"),
      api.request<StatusSummary>(`/api/v1/status?${rangeQuery(rangeHours)}`),
      api.request<ProfileState>("/api/v1/profiles"),
      api.request<AlertMatch[]>(`/api/v1/alerts?${rangeQuery(rangeHours)}`),
      api.request<any>("/api/v1/settings/alerts"),
      api.request<SystemCpuSnapshot>("/api/v1/system/cpu").catch(() => null)
    ]);
    setCpuSnapshot(cpu);
    setJobs(jobRows);
    setStatusSummary(summary);
    setProfileState(profiles);
    setAlertSettings(alertConfig.values ?? alertConfig);
    setActiveAlertCount(alertRows.filter(alert => alert.active !== false).length);
    const latestActiveAlert = alertRows.find(alert => alert.active !== false);
    if (latestActiveAlert && autoplayAllows(alertConfig.values ?? alertConfig, "alert"))
      playCallAudio(latestActiveAlert.callId, "alert", undefined, alertPlaybackLabel(latestActiveAlert));
    void api.request<SystemRecommendations>("/api/v1/system/recommendations")
      .then(setRecommendations)
      .catch(() => { });
  }, [rangeHours]);
  const currentSearch = pageSearches[page] ?? "";
  useEffect(() => {
    if (!categories.includes(page as any)) return;
    const handle = window.setTimeout(() => setDebouncedCategorySearch(currentSearch.trim()), 300);
    return () => window.clearTimeout(handle);
  }, [currentSearch, page]);

  const statusResource = usePersistentRefresh({
    key: `shared-status|${rangeHours}`,
    enabled: true,
    load: async () => {
      lastStatusRefreshAtRef.current = Date.now();
      await refreshStatusData();
      return true;
    }
  });
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
  const systemResource = usePersistentRefresh({
    key: `system|${rangeHours}`,
    enabled: page === "system" && setupStatus?.completed !== false,
    load: () => api.request<TrTroubleshoot>(`/api/v1/troubleshoot?${rangeQuery(rangeHours)}&bySystem=false&baseline=7d`)
  });
  const dashboard = dashboardResource.data;
  const categoryResult = categoryResource.data;
  const category = categoryResult?.page === page ? categoryResult.data : null;
  const siteSetup = setupResource.data;
  const troubleshoot = systemResource.data;

  const load = useCallback(async () => {
    lastPageRefreshAtRef.current = Date.now();
    if (pageRef.current === "dashboard") await dashboardResource.refresh();
    else if (categories.includes(pageRef.current as any)) await categoryResource.refresh();
    else if (pageRef.current === "setup" && !setupWaterfallActiveRef.current) await setupResource.refresh();
    else if (pageRef.current === "system") await systemResource.refresh();
    else if (pageRef.current === "settings") await loadSettings();
  }, [categoryResource.refresh, dashboardResource.refresh, loadSettings, setupResource.refresh, systemResource.refresh]);

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

  useEffect(() => { if (page === "settings") void loadSettings(); }, [page, loadSettings]);
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
      if (pageRef.current === "dashboard" || categories.includes(pageRef.current as any))
        schedulePage(3000);
    };
    const refreshDashboardData = () => {
      scheduleStatus(500);
      if (pageRef.current === "dashboard")
        schedulePage(900);
    };
    events.addEventListener("connected", () => {
      if (connectedOnce) {
        scheduleStatus(0);
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
        if (payload.callId && autoplayAllows(alertSettingsRef.current, "police"))
          void api.request<EngineCall>(`/api/v1/calls/${payload.callId}`).then(call => {
            if (call.category === "police")
              playCallAudio(call.id, "police", undefined, callPlaybackLabel(call));
          }).catch(() => { });
      } catch { }
    });
    events.addEventListener("alert_matched", refreshDashboardData);
    events.addEventListener("summary_updated", refreshDashboardData);
    events.addEventListener("job_updated", () => scheduleStatus(900));
    events.addEventListener("health_updated", () => scheduleStatus(900));
    return () => {
      window.clearTimeout(statusTimer);
      window.clearTimeout(pageTimer);
      events.close();
    };
  }, [load, statusResource.refresh]);
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
  const queueRateSuffix = engineHealth ? ` (${audioTranscribedPerMinute.toFixed(0)}s audio/min)` : "";
  const queuePressureNote = queueBlockedNotes.length ? `; ${[
    engineHealth?.aiWorkBlockedReason ? "AI paused" : "",
    aiCompletionIssue ? "AI completion issue" : "",
    embeddingIssue ? "embedding issue" : ""
  ].filter(Boolean).join(", ")}` : "";
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
    ? `CPU ${cpuSnapshot.latest.trCpuHostPercent.toFixed(0)}% ${cpuSnapshot.latest.hostTempC.toFixed(0)}C`
    : "CPU --";
  const cpuPillTitle = cpuSnapshot?.latest
    ? `${cpuSnapshot.summary} TR CPU ${cpuSnapshot.latest.trCpuPercent.toFixed(0)}% (${cpuSnapshot.latest.trCpuHostPercent.toFixed(0)}% of host), load ${cpuSnapshot.latest.hostLoad1.toFixed(2)}, temp ${cpuSnapshot.latest.hostTempC.toFixed(1)} C.`
    : "No recent TR CPU/resource sample.";
  const queueHealthText = queueHealth === "blocked"
    ? `Queue blocked ${queueDepth.toLocaleString()}${queueRateSuffix}${queuePressureNote}`
    : queueHealth === "clear"
      ? `Queue OK ${queueDepth.toLocaleString()}${queueRateSuffix}`
      : queueHealth === "pressure"
        ? `Queue pressure ${queueDepth.toLocaleString()}${queueRateSuffix}${queuePressureNote}`
        : `Queue draining ${queueDepth.toLocaleString()}${queueRateSuffix}`;
  const queueHealthTitle = engineHealth
    ? `${engineHealth.recentAudioSecondsTranscribed.toLocaleString()} audio seconds transcribed (${audioTranscribedPerMinute.toFixed(0)}s/min) and ${engineHealth.recentAudioSecondsIngested.toLocaleString()} audio seconds ingested (${audioIngestedPerMinute.toFixed(0)}s/min) in the last ${engineHealth.throughputWindowMinutes} minutes. Calls: ${engineHealth.recentCallsTranscribed.toLocaleString()} done (${engineHealth.recentTranscribedPerMinute.toFixed(1)}/min), ${engineHealth.recentCallsIngested.toLocaleString()} in (${engineHealth.recentIngestPerMinute.toFixed(1)}/min). Local workers: ${engineHealth.liveTranscriptionWorkers} x ${engineHealth.whisperThreadsPerWorker} thread(s). ${queueBlockedNotes.join(" ")}`.trim()
    : "Transcription queue is clear.";

  const activePageRefresh = page === "dashboard"
    ? { label: "Dashboard", state: dashboardResource.state }
    : categories.includes(page as any)
      ? { label: `${label(page)} calls`, state: categoryResource.state }
      : page === "setup"
        ? { label: "Setup", state: setupResource.state }
        : page === "system"
          ? { label: "System", state: systemResource.state }
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
          if (autoplayAllows(alertSettingsRef.current, "incident"))
            playCallAudio(firstCall.callId, "incident", incident.id, incident.title);
          else if (incident.category === "traffic" && autoplayAllows(alertSettingsRef.current, "traffic"))
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
        <select value={rangeHours} onChange={e => setRangeHours(Number(e.target.value))}>
          <option value={24}>24h</option>
          <option value={48}>2d</option>
          <option value={168}>Week</option>
        </select>
        <select aria-label="Color scheme" value={theme} onChange={e => setTheme(e.target.value)}>
          <option value="blue">Blue</option>
          <option value="orange">Orange</option>
        </select>
        {profileState && profileState.profiles.length > 0 && <select aria-label="Active profile" value={profileState.activeProfileId} onChange={e => void switchActiveProfile(e.target.value)}>
          {profileState.profiles.map(profile => <option value={profile.id} key={profile.id}>{profile.name}</option>)}
        </select>}
        {page === "settings" && <>
          <input ref={settingsFileInputRef} type="file" accept="application/json,.json" hidden onChange={e => void loadSettingsFile(e.target.files?.[0])} />
          <button disabled={settingsLoadState.loading} onClick={() => settingsFileInputRef.current?.click()}>{settingsLoadState.loading ? "Loading..." : "Load Settings"}</button>
          <button disabled={settingsLoadState.loading} onClick={() => void exportSettingsFile()}>Export Settings</button>
        </>}
        <span className={livePillClass} title={livePillTitle}>{livePillText}</span>
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
              {item === "system" && recommendations && recommendations.openCount > 0 && <span className={`nav-badge ${recommendations.highCount > 0 ? "high" : recommendations.mediumCount > 0 ? "medium" : "low"}`}>{recommendations.openCount}</span>}
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
            void statusResource.refresh();
        }} /></div>}
        {setupStatus?.completed && page === "system" && <div className="refresh-page-shell">
          <RefreshNotice state={systemResource.state} hasData={Boolean(troubleshoot)} onRetry={systemResource.refresh} />
          <SystemView data={troubleshoot} jobs={jobs} rangeHours={rangeHours} reload={load} engineHealth={engineHealth} cpuSnapshot={cpuSnapshot} recommendations={recommendations} setRecommendations={setRecommendations} targetTab={systemTargetTab} clearTargetTab={() => setSystemTargetTab(null)} onOpenSetup={goSetup} />
        </div>}
        {setupStatus?.completed && page === "settings" && <SettingsView settingsSections={settingsSections} settingsLoadState={settingsLoadState} reload={load} pendingProfileHides={pendingProfileHides} setPendingProfileHides={setPendingProfileHides} />}
      </main>
      {!inSetup && <footer className="statusbar">
        <button type="button" className="pill status-pill-button" onClick={() => goSetup("Talkgroups")}>Profile: {profileState?.profiles.find(p => p.id === profileState.activeProfileId)?.name ?? "Default"}</button>
        <span className="status-separator">|</span>
        <span className="pill">Calls {statusSummary?.calls?.toLocaleString() ?? "--"}</span>
        <button type="button" className="pill status-pill-button" onClick={() => goDashboard("incidents")}>Incidents {statusSummary?.incidents?.toLocaleString() ?? "--"}</button>
        <button type="button" className="pill status-pill-button" onClick={() => goDashboard("alerts")}>Alerts {statusSummary?.alerts?.toLocaleString() ?? "--"}</button>
        <button type="button" className={`pill status-pill-button queue-health queue-${queueHealth}`} title={queueHealthTitle} onClick={() => goSystem("queue")}>{queueHealthText}</button>
        <button type="button" className={cpuPillClass} title={cpuPillTitle} onClick={() => goSystem("cpu")}>{cpuPillText}</button>
        {activeJobCount > 0 && <span className="pill">Jobs {activeJobCount}</span>}
      </footer>}
    </div>
  );
}

function SiteSetupView({ setup, reload, targetSection, clearTargetSection, onTrOperationChange }: { setup: SiteSetup | null; reload: () => Promise<void>; targetSection?: string | null; clearTargetSection?: () => void; onTrOperationChange: (value: string) => void }) {
  const [current, setCurrent] = useState<SiteSetup | null>(setup);
  const currentRef = useRef<SiteSetup | null>(setup);
  const saveQueueRef = useRef<Promise<void>>(Promise.resolve());
  const [saveState, setSaveState] = useState<{ field: string; status: "idle" | "saving" | "saved" | "error"; message: string }>({ field: "", status: "idle", message: "" });
  const [localPendingChanges, setLocalPendingChanges] = useState<SiteSetupPendingChange[]>([]);
  const sections = ["Location", "Systems & Sites", "Talkgroups", "Hardware & RF Path", "RF Validation", "Apply & Resume", "Activity Log"];
  const enabledSections = new Set(["Location", "Systems & Sites", "Talkgroups", "Hardware & RF Path", "RF Validation", "Apply & Resume", "Activity Log"]);
  const [section, setSectionState] = useState(() => {
    const saved = localStorage.getItem("pizzawave-site-setup-section") || "Location";
    return enabledSections.has(saved) ? saved : "Location";
  });
  const [rfValidationSubPage, setRfValidationSubPageState] = useState<"waterfall" | "sweep">(() => localStorage.getItem("pizzawave-site-setup-rf-validation-subpage") === "sweep" ? "sweep" : "waterfall");
  const [applySubPage, setApplySubPageState] = useState<"source" | "review">(() => localStorage.getItem("pizzawave-site-setup-apply-subpage") === "review" ? "review" : "source");
  const setSection = (value: string) => {
    setSectionState(value);
    localStorage.setItem("pizzawave-site-setup-section", value);
  };
  const setRfValidationSubPage = (value: "waterfall" | "sweep") => {
    setRfValidationSubPageState(value);
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
              <button type="button" className={section === item && rfValidationSubPage === "waterfall" ? "active" : ""} onClick={() => { setSection(item); setRfValidationSubPage("waterfall"); }}>Waterfall</button>
              <button type="button" className={section === item && rfValidationSubPage === "sweep" ? "active" : ""} onClick={() => { setSection(item); setRfValidationSubPage("sweep"); }}>RF Sweep</button>
            </div>}
            {item === "Apply & Resume" && <div className="site-setup-subnav">
              <button type="button" className={section === item && applySubPage === "source" ? "active" : ""} onClick={() => { setSection(item); setApplySubPage("source"); }}>Sources</button>
              <button type="button" className={section === item && applySubPage === "review" ? "active" : ""} onClick={() => { setSection(item); setApplySubPage("review"); }}>Review</button>
            </div>}
          </div>)}
        </section>

        <section className="site-setup-panel">
          {section === "Location" && <SiteSetupLocationSection setup={current} saveState={saveState} onSave={saveDesired} />}
          {section === "Systems & Sites" && <SiteSetupSystemsSection setup={current} saveState={saveState} onSave={saveDesired} />}
          {section === "Talkgroups" && <SiteSetupTalkgroupsSection setup={current} reload={reload} onSave={saveDesired} />}
          {section === "Hardware & RF Path" && <SiteSetupHardwareSection setup={current} saveState={saveState} onSave={saveDesired} />}
          <div style={section === "RF Validation" ? undefined : { display: "none" }} aria-hidden={section === "RF Validation" ? undefined : "true"}>
            <SiteSetupRfValidationSection setup={current} active={section === "RF Validation"} subPage={rfValidationSubPage} onSave={saveDesired} onTrOperationChange={onTrOperationChange} />
          </div>
          {section === "Apply & Resume" && <SiteSetupApplySection setup={current} subPage={applySubPage} setSubPage={setApplySubPage} onSave={saveDesired} onSetupChanged={(next) => { currentRef.current = next; setCurrent(next); }} onApplied={(next) => { currentRef.current = next; setCurrent(next); void reload(); }} />}
          {section === "Activity Log" && <SiteSetupActivityLogSection setup={current} />}
        </section>
      </div>
    </section>
  </div>;
}

function SiteSetupChangeStrip({ setup, localPendingChanges = [] }: { setup: SiteSetup; localPendingChanges?: SiteSetupPendingChange[] }) {
  const latest = setup.recentActivity[0];
  const pendingChanges = setup.pendingChanges.length ? setup.pendingChanges : localPendingChanges;
  const changedCategories = pendingChanges.map(change => label(change.category));
  return <div className="site-setup-change-strip" aria-label="Setup change summary">
    <section className={pendingChanges.length ? "warning" : "ok"}>
      <span>Config changes</span>
      <strong>{pendingChanges.length ? `${pendingChanges.length} pending` : "None"}</strong>
      <small>{changedCategories.join(", ") || "No unapplied setup changes"}</small>
    </section>
    <section className={latest ? "info" : "neutral"}>
      <span>Last setup change</span>
      <strong>{latest ? label(latest.action) : "No activity"}</strong>
      <small>{latest ? `${latest.summary} / ${new Date(latest.timestampUtc).toLocaleString()}` : "No setup activity recorded"}</small>
    </section>
    <section className={setup.status.pendingApply ? "warning" : siteSetupMonitoringTone(setup.status.monitoringState)}>
      <span>Apply state</span>
      <strong>{setup.status.pendingApply ? "Apply needed" : "Current"}</strong>
      <small>{setup.status.pendingApply ? "Desired setup differs from the running TR config" : "No pending TR config apply"}</small>
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

function SiteSetupActivityLogSection({ setup }: { setup: SiteSetup }) {
  const [rows, setRows] = useState(setup.recentActivity);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  useEffect(() => setRows(setup.recentActivity), [setup.recentActivity]);
  useEffect(() => { void loadActivity(); }, []);

  async function loadActivity() {
    setBusy(true);
    setMessage("");
    try {
      setRows(await api.request<SiteSetup["recentActivity"]>(`${siteSetupApi}/activity?limit=100`));
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Unable to load Setup activity.");
    } finally {
      setBusy(false);
    }
  }

  return <div className="site-setup-form site-setup-activity">
    <div className="site-setup-activity-toolbar">
      <span>{rows.length.toLocaleString()} event{rows.length === 1 ? "" : "s"}</span>
      <button type="button" onClick={() => void loadActivity()} disabled={busy}>{busy ? "Refreshing..." : "Refresh"}</button>
    </div>
    {message && <div className="settings-message error">{message}</div>}
    <div className="site-setup-activity-table">
      <table className="table compact-table">
        <thead><tr><th>Time</th><th>Category</th><th>Action</th><th>Summary</th><th>State</th><th>Details</th></tr></thead>
        <tbody>
          {rows.map(row => <tr key={row.id || `${row.timestampUtc}-${row.action}`}>
            <td>{formatActivityDate(row.timestampUtc)}</td>
            <td>{label(row.category)}</td>
            <td>{label(row.action)}</td>
            <td>{row.summary || "--"}</td>
            <td><span className={`section-status ${siteSetupMonitoringTone(row.monitoringState)}`}>{label(row.monitoringState)}</span></td>
            <td>{activityDetails(row.detailsJson)}</td>
          </tr>)}
          {rows.length === 0 && <tr><td colSpan={6}>No setup activity has been recorded yet.</td></tr>}
        </tbody>
      </table>
    </div>
  </div>;
}

function formatActivityDate(value: string) {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString();
}

function activityDetails(detailsJson: string) {
  const text = (detailsJson || "").trim();
  if (!text || text === "{}") return <span className="muted">--</span>;
  let pretty = text;
  try {
    pretty = JSON.stringify(JSON.parse(text), null, 2);
  } catch {
    // Keep raw detail if it is not JSON.
  }
  return <details className="site-setup-activity-details"><summary>View</summary><pre>{pretty}</pre></details>;
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

  const selectedSystems = setup.desired.sourcePlanSystemShortNames.length
    ? setup.desired.sourcePlanSystemShortNames
    : setup.desired.systemShortNames.length
      ? setup.desired.systemShortNames
      : setup.desired.systems.map(system => system.shortName).filter(Boolean);

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
  function seedAreasFromSelectedSystems() {
    const existingBySystem = new Map(areas.map(area => [area.systemShortName.toLowerCase(), area]));
    const next = selectedSystems.map(systemName => {
      const existing = existingBySystem.get(systemName.toLowerCase());
      if (existing) return existing;
      const system = setup.desired.systems.find(item => item.shortName === systemName);
      return normalizeSiteSetupArea({
        areaId: createClientId(),
        areaLabel: suggestAreaLabelFromSite(system?.siteLabel || systemName),
        systemShortName: systemName,
        north: 0,
        south: 0,
        east: 0,
        west: 0,
        aliases: [systemName]
      });
    });
    setAreas(next);
    saveAreas(next);
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
    <div className="settings-subsection">
      <div className="setup-job-head">
        <strong>Monitored Areas</strong>
        <button type="button" disabled={selectedSystems.length === 0} onClick={seedAreasFromSelectedSystems}>Sync from selected sites</button>
        {statusFor("monitoredAreas")}
      </div>
      {areas.length === 0 && <div className="setup-note">No monitored areas are configured.</div>}
      {areas.map((area, index) => {
        const key = areaDraftKey(area, index);
        const candidates = areaBoundaryCandidates[key] ?? [];
        return <div className="setup-area" key={key}>
          <div className="setup-area-head">
            <div>
              <span>System</span>
              <code>{area.systemShortName || "--"}</code>
            </div>
            <button type="button" className="danger-button" onClick={() => removeArea(index)}>Remove</button>
          </div>
          <div className="area-label-row">
            <SettingInput label="Area label" description="County or city boundary label." value={area.areaLabel} onChange={value => updateArea(index, { areaLabel: value })} />
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
          {hasUsableAreaBounds(area) ? <AreaMapPreview area={area} /> : <div className="setup-note">Boundary lookup is pending for this area.</div>}
          <div className="area-coordinate-grid">
            <SettingInput label="North" description="Northern latitude boundary." value={String(area.north ?? "")} onChange={value => updateArea(index, { north: numberOrZero(value) })} />
            <SettingInput label="South" description="Southern latitude boundary." value={String(area.south ?? "")} onChange={value => updateArea(index, { south: numberOrZero(value) })} />
            <SettingInput label="East" description="Eastern longitude boundary." value={String(area.east ?? "")} onChange={value => updateArea(index, { east: numberOrZero(value) })} />
            <SettingInput label="West" description="Western longitude boundary." value={String(area.west ?? "")} onChange={value => updateArea(index, { west: numberOrZero(value) })} />
          </div>
          <div className="setup-button-row">
            <button type="button" onClick={() => saveAreas()}>Save monitored area</button>
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
    setSdrBusy(true);
    setSdrMessage("");
    try {
      const result = await api.request<SetupSdrDetection>("/api/v1/setup/sdrs");
      setSdrDetection(result);
      const detectedSources = setupSourcesFromSdrDetection(setup, result);
      if (detectedSources.length) {
        const selectedSourceIndexes = detectedSources.map(source => source.index);
        await onSave({ sources: detectedSources, selectedSourceIndexes }, "sources");
      }
      setSdrMessage(result.message);
    } catch (error) {
      setSdrMessage(error instanceof Error ? error.message : "SDR inventory failed.");
    } finally {
      setSdrBusy(false);
    }
  }
  return <div className="site-setup-form site-setup-hardware">
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
    <div className="site-setup-hardware-inventory">
      <div className="rf-primary-actions">
        <button type="button" disabled={sdrBusy} onClick={() => void runSdrInventory()}>{sdrBusy ? "Running..." : sdrDetection ? "Rerun SDR Inventory" : "Run SDR Inventory"}</button>
        {sdrMessage && <span className={sdrMessage.toLowerCase().includes("fail") || sdrMessage.toLowerCase().includes("unable") ? "settings-message error" : "settings-message ok"}>{sdrMessage}</span>}
      </div>
      {sdrDetection && <SetupSdrInventorySummary detection={sdrDetection} />}
    </div>
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

function SiteSetupRfValidationSection({ setup, active, subPage, onSave, onTrOperationChange }: { setup: SiteSetup; active: boolean; subPage: "waterfall" | "sweep"; onSave: (patch: Partial<SiteSetupConfig>, field: string) => Promise<void>; onTrOperationChange: (value: string) => void }) {
  const [detail, setDetail] = useState<RfSurveyDetail | null>(null);
  const [busy, setBusy] = useState("");
  const [message, setMessage] = useState("");
  const [activeControlChannelHz, setActiveControlChannelHz] = useState(0);
  const [duration, setDuration] = useState("45");
  const [waterfallSweepSelections, setWaterfallSweepSelections] = useState<WaterfallSweepSelection[]>([]);
  const [details, setDetails] = useState<{ title: string; body: React.ReactNode } | null>(null);
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
    rfPath: setup.desired.rfPath
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
          const firstCc = normalizeControlChannelSelection(current.profile.systems.flatMap(system => system.controlChannelsHz))[0] ?? controlChannels[0] ?? 0;
          setActiveControlChannelHz(currentValue => currentValue || firstCc);
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
        body: JSON.stringify({ type, durationSeconds: type === "rf_validation_sweep" ? 300 : 45, controlChannelHz, ...extraRequest })
      });
      await refreshWorkspace();
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
    const experiment = await runExperiment("sdr_inventory", "about 15 seconds");
    const evidence = experiment ? parseExperimentJson<any>(experiment.evidenceJson) : null;
    const detection = evidence?.detection as SetupSdrDetection | undefined;
    if (detection?.devices?.length) {
      const detectedSources = setupSourcesFromSdrDetection(setup, detection);
      await onSave({ sources: detectedSources, selectedSourceIndexes: detectedSources.map(source => source.index) }, "sources");
    }
  }
  async function adoptWaterfallSite(system: RfSurveySystem) {
    const existingNames = new Set(setup.desired.systems.map(item => item.shortName.toLowerCase()));
    if (existingNames.has(system.shortName.toLowerCase()))
      return;

    const nextSystems = [...setup.desired.systems, system].sort((left, right) => left.shortName.localeCompare(right.shortName));
    const nextNames = Array.from(new Set([...selectedSetupSystemNames(setup), system.shortName].filter(Boolean))).sort((left, right) => left.localeCompare(right));
    const nextSourcePlanSystems = Array.from(new Set([
      ...(setup.desired.sourcePlanSystemShortNames.length ? setup.desired.sourcePlanSystemShortNames : selectedSetupSystemNames(setup)),
      system.shortName
    ].filter(Boolean))).sort((left, right) => left.localeCompare(right));
    await onSave({
      systems: nextSystems,
      systemShortNames: nextNames,
      sourcePlanSystemShortNames: nextSourcePlanSystems
    }, "systems");
  }
  const handleWaterfallStatusChange = useCallback((status: RfSurveyWaterfallStatus | null) => {
    onTrOperationChange(waterfallTrOperationText(status));
  }, [onTrOperationChange]);
  const effectiveSystems = detail?.profile.systems?.length ? detail.profile.systems : systems;
  const effectiveSources = detail?.profile.sources?.length ? detail.profile.sources : sources;
  const effectiveControlChannels = normalizeControlChannelSelection(effectiveSystems.flatMap(system => system.controlChannelsHz));
  const cachedSites = readCachedRadioReferenceSitesForSids(setupRadioReferenceSids(setup));
  useEffect(() => {
    if (!active)
      return;
    const detection = latestSetupSdrDetection(detail?.experiments ?? []);
    if (!detection?.devices?.length || detection.devices.length <= sources.length)
      return;
    const detectedSources = setupSourcesFromSdrDetection(setup, detection);
    if (detectedSources.length <= sources.length)
      return;
    void onSave({ sources: detectedSources, selectedSourceIndexes: detectedSources.map(source => source.index) }, "sources");
  }, [active, detail?.session.id, detail?.experiments.map(experiment => `${experiment.id}:${experiment.type}:${experiment.createdAtUtc}`).join("|"), sources.length]);
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
  return <div className="site-setup-form site-setup-rf-validation">
    {busy === "workspace" && <div className="setup-note">Preparing RF validation session...</div>}
    {message && <div className={message.toLowerCase().includes("unable") || message.toLowerCase().includes("failed") ? "settings-message error" : "settings-message ok"}>{message}</div>}
    {detail
      ? <>
        <div style={subPage === "waterfall" ? undefined : { display: "none" }} aria-hidden={subPage === "waterfall" ? undefined : "true"}>
          <WaterfallStep
            apiBase={siteSetupRfApi}
            surveyId={detail.session.id}
            visible={active && subPage === "waterfall"}
            locked={effectiveSources.length === 0 || effectiveControlChannels.length === 0}
            sources={effectiveSources}
            selectedSources={selectedSourceIndexes}
            systems={effectiveSystems}
            radioReferenceSites={cachedSites}
            controlChannels={effectiveControlChannels}
            activeControlChannelHz={activeControlChannelHz || effectiveControlChannels[0] || 0}
            waterfallSweepSelections={waterfallSweepSelections}
            onWaterfallSweepSelections={updateWaterfallSweepSelections}
            onAdoptWaterfallSite={adoptWaterfallSite}
            onRunExperiment={runExperiment}
            onReload={refreshWorkspace}
            onStatusChange={handleWaterfallStatusChange}
          />
        </div>
        {subPage === "sweep" && <SiteValidationStep
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
          radioReferenceSites={cachedSites}
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
          onAdoptWaterfallSite={adoptWaterfallSite}
          onReload={refreshWorkspace}
          onShowDetails={setDetails}
          onOpenRunLog={() => undefined}
          waterfallSweepSelections={waterfallSweepSelections}
          onWaterfallSweepSelections={updateWaterfallSweepSelections}
          onAcceptSweepCandidate={acceptSweepCandidate}
          inventoryRequired={false}
        />}
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
      const result = await api.request<RfSurveyTrActionResult>(`${siteSetupRfApi}/${encodeURIComponent(detail.session.id)}/tr/apply-source-draft`, {
        method: "POST",
        body: JSON.stringify({
          configJson: draft.configJson,
          restartTr: true,
          preserveRfValidationEvidence: true
        })
      });
      const next = await api.request<SiteSetup>(`${siteSetupApi}/mark-applied`, {
        method: "POST",
        body: JSON.stringify({
          summary: result.message || "Applied Site Setup TR config and resumed monitoring.",
          details: {
            surveyId: detail.session.id,
            candidatePath: result.candidatePath,
            backupPath: result.backupPath,
            restorePath: result.restorePath,
            serviceOutput: result.serviceOutput,
            draftSummary: draft.summary
          },
          source: "ui"
        })
      });
      onApplied(next);
      setMessage(`${result.message}${result.serviceOutput ? ` ${result.serviceOutput.trim()}` : ""}`);
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
      {subPage === "review" && <button type="button" className="danger-button" onClick={() => void applyDraft()} disabled={busy !== "" || !detail || !draft}>{busy === "apply" ? "Applying..." : "Apply & Resume Monitoring"}</button>}
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
            <td>{row.setting}</td>
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

function formatSiteSetupSourceSummary(source: any) {
  const parts = [
    source?.device || "source",
    source?.center ? `center ${formatRfHz(readTrFrequencyHz(source.center))}` : "",
    source?.rate ? `rate ${formatHz(Number(source.rate))}` : "",
    source?.error !== undefined ? `error ${source.error} Hz` : "",
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

function latestSetupSdrDetection(experiments: RfSurveyExperiment[]): SetupSdrDetection | null {
  const latest = [...experiments]
    .filter(experiment => experiment.type === "sdr_inventory")
    .sort((a, b) => (b.createdAtUtc || "").localeCompare(a.createdAtUtc || ""))[0];
  if (!latest)
    return null;
  const evidence = parseExperimentJson<any>(latest.evidenceJson);
  const detection = evidence?.detection;
  return detection && Array.isArray(detection.devices)
    ? detection as SetupSdrDetection
    : null;
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

function talkgroupSourceImportSignature(sources: SiteSetupTalkgroupSource[]) {
  return sources
    .map(row => `${row.siteShortName || row.siteLabel || row.key}:${row.radioReferenceSid.trim()}`)
    .join("|");
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
  const synchronizedSourceKeyRef = useRef("");
  const setupSourceKey = initialTalkgroupSources(setup).map(row => `${row.siteShortName || row.siteLabel || row.key}:${row.radioReferenceSid}:${row.catalogSystem}`).join("|");
  useEffect(() => {
    const next = initialTalkgroupSources(setup);
    setRrSources(next);
    setMessage("");
    synchronizedSourceKeyRef.current = "";
  }, [setupSourceKey]);
  useEffect(() => {
    const key = talkgroupSourceImportSignature(rrSources);
    if (!key || !rrSources.some(row => row.radioReferenceSid.trim()) || synchronizedSourceKeyRef.current === key)
      return;
    synchronizedSourceKeyRef.current = key;
    void synchronizeTalkgroups();
  }, [talkgroupSourceImportSignature(rrSources)]);

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
      synchronizedSourceKeyRef.current = "";
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

function DashboardStatisticsPanel({ data }: { data: Dashboard | null }) {
  if (!data) return <div className="card">Loading statistics...</div>;
  const hiddenKpis = new Set(["alert rate", "token usage", "incidents", "top problem system", "tr decode 0%", "tr worst decode", "busiest hour", "unique talkgroups"]);
  const visibleKpis = data.kpis.filter(k => !hiddenKpis.has(k.label.trim().toLowerCase()));
  return <div className="dashboard-stats-panel">
    <div className="section kpis">{visibleKpis.map(k => <Kpi key={k.label} {...k} />)}</div>
    <SystemCallBreakdownTable rows={data.callsBySystem ?? []} />
    <VolumeByHourChart rows={data.volumeByHourCategory} />
  </div>;
}

function SystemCallBreakdownTable({ rows }: { rows: NonNullable<Dashboard["callsBySystem"]> }) {
  return <div className="card">
    <h4>Calls by Site/System</h4>
    {rows.length ? <table className="table compact-table">
      <thead><tr><th>Site/System</th><th>Calls</th><th>Talkgroups</th><th>Sources</th><th>Frequency span</th><th>Transcription</th><th>Categories</th><th>Last heard</th></tr></thead>
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
          <td>{row.uniqueTalkgroups.toLocaleString()}</td>
          <td>{row.sources.length ? row.sources.map(source => `#${source}`).join(", ") : "--"}</td>
          <td>{freqText}</td>
          <td>{row.completeCalls.toLocaleString()} complete / {row.pendingCalls.toLocaleString()} pending / {row.failedCalls.toLocaleString()} failed{row.problemCalls ? ` / ${row.problemCalls.toLocaleString()} problem` : ""}</td>
          <td>{categoryText || "--"}</td>
          <td>{row.lastHeard ? relativeTime(row.lastHeard) : "--"}</td>
        </tr>;
      })}</tbody>
    </table> : <span className="muted">No calls in the selected range.</span>}
  </div>;
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

type SystemTopTab = "recommendations" | "services" | "cpu" | "queue" | "jobs" | "storage" | "backup" | "tr" | "metrics";
type SystemTrTab = "summary" | "config" | "restore" | "logs";

function normalizeSystemTopTab(value: string | null): SystemTopTab {
  if (value === "service" || value === "qdrant") return "services";
  return ["recommendations", "services", "cpu", "queue", "jobs", "storage", "backup", "tr", "metrics"].includes(value ?? "")
    ? value as SystemTopTab
    : "recommendations";
}

function SystemView({ data, jobs, rangeHours, reload, engineHealth, cpuSnapshot, recommendations, setRecommendations, targetTab, clearTargetTab, onOpenSetup }: { data: TrTroubleshoot | null; jobs: Job[]; rangeHours: number; reload: () => Promise<void>; engineHealth: EngineHealth | null; cpuSnapshot: SystemCpuSnapshot | null; recommendations: SystemRecommendations | null; setRecommendations: (value: SystemRecommendations | null) => void; targetTab?: SystemTopTab | null; clearTargetTab?: () => void; onOpenSetup?: (section?: string) => void }) {
  const [topTab, setTopTabState] = useState<SystemTopTab>(() => normalizeSystemTopTab(localStorage.getItem("pizzawave-system-tab")));
  const [trTab, setTrTabState] = useState<SystemTrTab>(() => {
    const saved = localStorage.getItem("pizzawave-system-tr-tab");
    return saved === "logs" || saved === "restore" || saved === "config" ? saved : "summary";
  });
  const [metricsTab, setMetricsTabState] = useState<"overview" | "calls" | "transcription" | "rf" | "incidents" | "ai" | "bandwidth">(() => {
    const saved = localStorage.getItem("pizzawave-system-metrics-tab");
    return (saved === "usage" ? "calls" : saved as any) || "overview";
  });
  const [bySystem, setBySystem] = useState(false);
  const [baseline, setBaseline] = useState("7d");
  const [metricsData, setMetricsData] = useState<TrTroubleshoot | null>(null);
  const [runtime, setRuntime] = useState<any | null>(null);
  const [tokenUsage, setTokenUsage] = useState<TokenUsageReport | null>(null);
  const [bandwidthUsage, setBandwidthUsage] = useState<RemoteBandwidthReport | null>(null);
  const [incidentAudit, setIncidentAudit] = useState<IncidentOperationAuditRow[] | null>(null);
  const [dashboardStats, setDashboardStats] = useState<Dashboard | null>(null);
  const [insightText, setInsightText] = useState("");
  const [restartBusy, setRestartBusy] = useState<"" | "pizzad" | "trunk-recorder" | "qdrant">("");
  const [restartMessages, setRestartMessages] = useState<Record<string, string>>({});
  const [ingestBusy, setIngestBusy] = useState(false);
  const [ingestMessage, setIngestMessage] = useState("");
  const [recommendationBusy, setRecommendationBusy] = useState(false);
  const setTopTab = (value: typeof topTab) => { setTopTabState(value); localStorage.setItem("pizzawave-system-tab", value); };
  const setTrTab = (value: typeof trTab) => { setTrTabState(value); localStorage.setItem("pizzawave-system-tr-tab", value); };
  const setMetricsTab = (value: typeof metricsTab) => { setMetricsTabState(value); localStorage.setItem("pizzawave-system-metrics-tab", value); };
  useEffect(() => {
    if (!targetTab) return;
    setTopTab(targetTab);
    clearTargetTab?.();
  }, [targetTab, clearTargetTab]);

  async function loadRuntime() {
    try {
      setRuntime(await api.request<any>("/api/v1/system/runtime"));
    } catch {
      setRuntime(null);
    }
  }

  useEffect(() => {
    if (!["services", "storage"].includes(topTab)) return;
    void loadRuntime();
  }, [topTab, jobs.length]);
  useEffect(() => {
    if (topTab !== "metrics" || metricsTab !== "rf") return;
    void api.request<TrTroubleshoot>(`/api/v1/troubleshoot?${rangeQuery(rangeHours)}&bySystem=${bySystem}&baseline=${baseline}`)
      .then(setMetricsData)
      .catch(() => setMetricsData(null));
  }, [topTab, metricsTab, bySystem, baseline, rangeHours]);
  useEffect(() => {
    if (topTab !== "metrics" || metricsTab !== "ai") return;
    void api.request<TokenUsageReport>(`/api/v1/system/token-usage?${rangeQuery(rangeHours)}`).then(setTokenUsage).catch(() => setTokenUsage(null));
  }, [topTab, metricsTab, rangeHours]);
  useEffect(() => {
    if (topTab !== "metrics" || metricsTab !== "bandwidth") return;
    void api.request<RemoteBandwidthReport>(`/api/v1/system/remote-bandwidth?${rangeQuery(rangeHours)}`).then(setBandwidthUsage).catch(() => setBandwidthUsage(null));
  }, [topTab, metricsTab, rangeHours]);
  useEffect(() => {
    if (topTab !== "metrics" || !["overview", "calls", "incidents"].includes(metricsTab)) return;
    void api.request<Dashboard>(`/api/v1/dashboard?${rangeQuery(rangeHours)}`).then(setDashboardStats).catch(() => setDashboardStats(null));
  }, [topTab, metricsTab, rangeHours]);
  useEffect(() => {
    if (topTab !== "metrics" || metricsTab !== "incidents") return;
    void api.request<IncidentOperationAuditRow[]>(`/api/v1/incidents/audit?hours=${Math.max(1, rangeHours)}&limit=80`).then(setIncidentAudit).catch(() => setIncidentAudit(null));
  }, [topTab, metricsTab, rangeHours]);

  if (!data) return null;
  const active = metricsData ?? data;
  async function restartService(service: "pizzad" | "trunk-recorder" | "qdrant") {
    if (!confirmAction(`Restart ${label(service)}?`, "This interrupts the service briefly. Live ingestion or processing may pause while it restarts.")) return;
    setRestartBusy(service);
    setRestartMessages(current => ({ ...current, [service]: `Restarting ${service}...` }));
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
  async function stopTrService() {
    if (!confirmAction("Stop TR?", "This gracefully stops trunk-recorder so SDR hardware can be swapped. Live capture remains stopped until you restart TR.")) return;
    setRestartBusy("trunk-recorder");
    setRestartMessages(current => ({ ...current, "trunk-recorder": "Stopping trunk-recorder..." }));
    try {
      const job = await api.request<Job>("/api/v1/system/services/trunk-recorder/stop", { method: "POST" });
      setRestartMessages(current => ({ ...current, "trunk-recorder": `Stop queued as job ${job.id}.` }));
      setTimeout(() => void reload(), 1500);
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
      await reload();
    } catch (error) {
      setIngestMessage(error instanceof Error ? error.message : "Failed to update ingest control.");
    } finally {
      setIngestBusy(false);
    }
  }
  function openRecommendationTarget(target: { topTab: string; subTab: string }) {
    if (target.topTab === "recommendations") setTopTab("recommendations");
    if (target.topTab === "setup") {
      onOpenSetup?.(target.subTab === "talkgroups" ? "Talkgroups" : target.subTab);
      return;
    }
    if (target.topTab === "cpu") setTopTab("cpu");
    if (target.topTab === "pizzad") {
      if (target.subTab === "storage") setTopTab("storage");
      else if (target.subTab === "jobs") setTopTab("queue");
      else if (target.subTab === "quality") { setTopTab("metrics"); setMetricsTab("transcription"); }
      else setTopTab("services");
    }
    if (target.topTab === "tr") {
      if (target.subTab === "metrics" || target.subTab === "rf") { setTopTab("metrics"); setMetricsTab("rf"); }
      else { setTopTab("tr"); setTrTab(target.subTab === "logs" ? "logs" : "summary"); }
    }
    if (target.topTab === "qdrant") setTopTab("services");
    if (target.topTab === "stats") { setTopTab("metrics"); setMetricsTab("calls"); }
    if (target.topTab === "tokens") { setTopTab("metrics"); setMetricsTab("ai"); }
    if (target.topTab === "bandwidth") { setTopTab("metrics"); setMetricsTab("bandwidth"); }
  }
  async function setRecommendationState(id: string, action: "ignore" | "restore" | "reset-baseline") {
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
  async function excludeTalkgroupsFromTr(targets: { systemShortName?: string | null; talkgroup: number }[]) {
    const normalizedTargets = targets
      .map(target => ({ systemShortName: normalizeTalkgroupSystem(target.systemShortName || ""), talkgroup: Number(target.talkgroup) }))
      .filter(target => Number.isFinite(target.talkgroup) && target.talkgroup > 0);
    if (normalizedTargets.length === 0) return;
    const targetLabel = normalizedTargets.length === 1 ? `TG ${normalizedTargets[0].talkgroup}` : `${normalizedTargets.length} noisy talkgroup candidates`;
    if (!confirmAction(`Exclude ${targetLabel} from TR?`, "This disables matching talkgroup catalog rows and regenerates TR CSV files. Trunk Recorder must reload the generated CSV before live capture policy changes.")) return;

    setRecommendationBusy(true);
    setInsightText("");
    try {
      const result = await api.request<{ updated: number; message?: string; save?: { generatedCsvPath?: string } }>("/api/v1/talkgroups/catalog/policy", {
        method: "POST",
        body: JSON.stringify({
          targets: normalizedTargets,
          enabled: false
        })
      });
      if (!result.updated) {
        setInsightText(result.message || "No matching enabled talkgroup catalog rows were found for those noise candidates.");
        return;
      }
      const path = result.save?.generatedCsvPath ? ` (${result.save.generatedCsvPath})` : "";
      setInsightText(`${result.updated.toLocaleString()} talkgroup catalog row(s) excluded from generated TR CSVs${path}. Apply/restart TR for capture policy to consume the change.`);
      setRecommendations(await api.request<SystemRecommendations>("/api/v1/system/recommendations"));
    } catch (error) {
      setInsightText(error instanceof Error ? error.message : "Unable to exclude talkgroup candidates.");
    } finally {
      setRecommendationBusy(false);
    }
  }
  return (
    <div className="trouble-page">
      <div className="trouble-tabs">
        <button className={topTab === "recommendations" ? "active" : ""} onClick={() => setTopTab("recommendations")}>Recommendations{recommendations && recommendations.openCount > 0 ? ` (${recommendations.openCount})` : ""}</button>
        <button className={topTab === "services" ? "active" : ""} onClick={() => setTopTab("services")}>Services</button>
        <button className={topTab === "cpu" ? "active" : ""} onClick={() => setTopTab("cpu")}>CPU</button>
        <button className={topTab === "queue" ? "active" : ""} onClick={() => setTopTab("queue")}>Queue</button>
        <button className={topTab === "jobs" ? "active" : ""} onClick={() => setTopTab("jobs")}>Jobs</button>
        <button className={topTab === "storage" ? "active" : ""} onClick={() => setTopTab("storage")}>Storage</button>
        <button className={topTab === "backup" ? "active" : ""} onClick={() => setTopTab("backup")}>Backup</button>
        <button className={topTab === "tr" ? "active" : ""} onClick={() => setTopTab("tr")}>Trunk Recorder</button>
        <button className={topTab === "metrics" ? "active" : ""} onClick={() => setTopTab("metrics")}>Metrics</button>
        <button onClick={() => void reload()}>Refresh</button>
      </div>
      {topTab === "recommendations" && <RecommendationsPanel recommendations={recommendations} busy={recommendationBusy || ingestBusy} message={insightText} onOpen={openRecommendationTarget} onState={setRecommendationState} onExcludeTalkgroups={excludeTalkgroupsFromTr} onAction={async action => {
        if (action.kind === "pause-ingest") await setIngestPaused(true, false);
        if (action.kind === "exclude-talkgroups-from-tr") await excludeTalkgroupsFromTr((action.talkgroups ?? []).map(talkgroup => ({ talkgroup })));
      }} />}
      {topTab === "services" && <div className="trouble-panel"><ServicesManager runtime={runtime} data={data} restartBusy={restartBusy} restartMessages={restartMessages} onRestart={restartService} onStopTr={stopTrService} /></div>}
      {topTab === "cpu" && <CpuPanel snapshot={cpuSnapshot} reload={reload} />}
      {topTab === "queue" && <QueuePanel engineHealth={engineHealth} ingestBusy={ingestBusy} ingestMessage={ingestMessage} onSetIngestPaused={setIngestPaused} />}
      {topTab === "jobs" && <JobsPanel jobs={jobs} reload={reload} />}
      {topTab === "storage" && <div className="trouble-panel"><PizzadStorageManager runtime={runtime} reload={loadRuntime} /></div>}
      {topTab === "backup" && <div className="trouble-panel"><BackupRestorePanel reload={reload} /></div>}
      {topTab === "tr" && <div className="trouble-panel">
        <div className="trouble-tabs nested">
          <button className={trTab === "summary" ? "active" : ""} onClick={() => setTrTab("summary")}>Summary</button>
          <button className={trTab === "config" ? "active" : ""} onClick={() => setTrTab("config")}>Config</button>
          <button className={trTab === "restore" ? "active" : ""} onClick={() => setTrTab("restore")}>Restore Config</button>
          <button className={trTab === "logs" ? "active" : ""} onClick={() => setTrTab("logs")}>Logs</button>
        </div>
        {trTab === "summary" && <TrHealthSummaryView data={data} />}
        {trTab === "config" && <TrConfigReadOnlyPanel onOpenSetup={onOpenSetup} />}
        {trTab === "restore" && <TrConfigRestorePanel />}
        {trTab === "logs" && <pre className="log-box">{data.logOutput}</pre>}
      </div>}
      {topTab === "metrics" && <div className="trouble-panel">
        <div className="trouble-tabs nested">
          <button className={metricsTab === "overview" ? "active" : ""} onClick={() => setMetricsTab("overview")}>Overview</button>
          <button className={metricsTab === "calls" ? "active" : ""} onClick={() => setMetricsTab("calls")}>Calls</button>
          <button className={metricsTab === "transcription" ? "active" : ""} onClick={() => setMetricsTab("transcription")}>Transcription</button>
          <button className={metricsTab === "rf" ? "active" : ""} onClick={() => setMetricsTab("rf")}>RF</button>
          <button className={metricsTab === "incidents" ? "active" : ""} onClick={() => setMetricsTab("incidents")}>Incidents</button>
          <button className={metricsTab === "ai" ? "active" : ""} onClick={() => setMetricsTab("ai")}>AI</button>
          <button className={metricsTab === "bandwidth" ? "active" : ""} onClick={() => setMetricsTab("bandwidth")}>Bandwidth</button>
        </div>
        {metricsTab === "overview" && <MetricsOverviewPanel data={data} dashboard={dashboardStats} engineHealth={engineHealth} tokenUsage={tokenUsage} bandwidthUsage={bandwidthUsage} navigate={(top, sub) => { setTopTab(top); if (top === "metrics" && sub) setMetricsTab(sub as any); }} />}
        {metricsTab === "calls" && <DashboardStatisticsPanel data={dashboardStats} />}
        {metricsTab === "transcription" && <QualityAuditView data={data} rangeHours={rangeHours} />}
        {metricsTab === "rf" && <RfMetricsPanel data={active} rangeHours={rangeHours} bySystem={bySystem} baseline={baseline} setBySystem={setBySystem} setBaseline={setBaseline} />}
        {metricsTab === "incidents" && <IncidentMetricsPanel dashboard={dashboardStats} audit={incidentAudit} />}
        {metricsTab === "ai" && <TokenUsagePanel report={tokenUsage} />}
        {metricsTab === "bandwidth" && <RemoteBandwidthPanel report={bandwidthUsage} />}
      </div>}
    </div>
  );
}

function TrConfigReadOnlyPanel({ onOpenSetup }: { onOpenSetup?: () => void }) {
  const [editor, setEditor] = useState<TrConfigEditor | null>(null);
  const [message, setMessage] = useState("");
  const [busy, setBusy] = useState(true);

  useEffect(() => {
    let canceled = false;
    setBusy(true);
    api.request<TrConfigEditor>("/api/v1/system/tr-config/editor")
      .then(editorResult => {
        if (canceled) return;
        setEditor(editorResult);
        setMessage("");
      })
      .catch(error => !canceled && setMessage(error instanceof Error ? error.message : "Unable to load TR config."))
      .finally(() => !canceled && setBusy(false));
    return () => { canceled = true; };
  }, []);

  const liveConfig = editor?.liveConfigJson ?? "";

  if (busy && !editor) return <div className="card">Loading TR config...</div>;
  return <div className="tr-config-readonly">
    <div className="setup-job-head">
      <div>
        <h3>Active TR Config</h3>
        <p className="muted">Read-only view of the config currently installed at {editor?.livePath || "--"}.</p>
        {editor?.hasDraft && <p className="settings-message">A standalone editor draft exists at {editor.draftPath}, but this page is showing the live TR config.</p>}
      </div>
      {onOpenSetup && <button className="danger-button" onClick={onOpenSetup}>Open Setup</button>}
    </div>
    <div className="setup-note">Use Setup for site changes, source planning, RF validation, and applying monitored TR configuration changes.</div>
    {message && <p className="settings-message error">{message}</p>}
    {editor && <div className="setup-review">
      <div><span>Systems</span><code>{editor.summary.systems.length.toLocaleString()}</code></div>
      <div><span>Sources</span><code>{editor.summary.sources.length.toLocaleString()}</code></div>
      <div><span>Warnings</span><code>{editor.summary.warnings.length.toLocaleString()}</code></div>
      <div><span>Live path</span><code>{editor.livePath || "--"}</code></div>
    </div>}
    {editor?.summary.warnings.length ? <div className="setup-warning-list">{editor.summary.warnings.map(warning => <div key={warning}>{warning}</div>)}</div> : null}
    <div className="tr-config-raw readonly">
      <div className="card">
        <h3>Full JSON</h3>
        <textarea readOnly value={liveConfig} spellCheck={false} />
      </div>
    </div>
  </div>;
}

function MetricsOverviewPanel({ data, dashboard, engineHealth, tokenUsage, bandwidthUsage, navigate }: { data: TrTroubleshoot; dashboard: Dashboard | null; engineHealth: EngineHealth | null; tokenUsage: TokenUsageReport | null; bandwidthUsage: RemoteBandwidthReport | null; navigate: (top: SystemTopTab, sub?: string) => void }) {
  const audit = data.qualityAudit;
  const rfIssues = data.health.metrics.filter(row => row.isIssue).length + data.health.systems.filter(row => row.isIssue).length;
  const queueDepth = engineHealth?.queueDepth ?? 0;
  const queueState = engineHealth?.ingest?.paused ? "Paused" : queueDepth <= 0 ? "OK" : engineHealth?.queueUnderPressure ? "Pressure" : "Draining";
  const incidentCount = dashboard?.incidents.length ?? 0;
  const aiFailures = tokenUsage?.summary.failures ?? 0;
  const aiOtherFailures = tokenUsage?.summary.httpOrOtherErrors ?? 0;
  const aiTimeoutFailures = tokenUsage?.summary.timeoutFailures ?? 0;
  const aiNoValidFailures = tokenUsage?.summary.noValidResultFailures ?? 0;
  const aiServiceFailures = aiOtherFailures + aiTimeoutFailures + aiNoValidFailures;
  const aiTruncated = tokenUsage?.summary.truncated ?? 0;
  return <div className="metrics-overview">
    <div className="audit-kpis">
      <Kpi label="Live Queue" value={queueState} status={queueState === "OK" ? "ok" : queueState === "Pressure" || queueState === "Paused" ? "error" : "warning"} subtext={`${queueDepth.toLocaleString()} queued, ${formatDurationMinutes((engineHealth?.pendingAudioSeconds ?? 0) / 60)} pending audio`} onClick={() => navigate("queue")} />
      <Kpi label="Transcript Quality" value={`${audit.problemPercent.toFixed(1)}% problem`} status={audit.problemPercent > 25 ? "error" : audit.problemPercent > 10 ? "warning" : "ok"} subtext={`${audit.problemCalls.toLocaleString()} of ${audit.totalCalls.toLocaleString()} calls flagged`} onClick={() => navigate("metrics", "transcription")} />
      <Kpi label="RF Health" value={rfIssues > 0 ? "Watch" : "OK"} status={rfIssues > 0 ? "warning" : "ok"} subtext={`${rfIssues.toLocaleString()} health issue row(s)`} onClick={() => navigate("metrics", "rf")} />
      <Kpi label="Incidents" value={incidentCount.toLocaleString()} subtext="incident volume in selected range" onClick={() => navigate("metrics", "incidents")} />
      <Kpi label="AI" value={aiFailures > 0 ? aiServiceFailures > 0 ? "Errors" : "Truncated" : tokenUsage ? "OK" : "Open"} status={aiServiceFailures > 0 ? "error" : aiTruncated > 0 ? "warning" : tokenUsage ? "ok" : "neutral"} subtext={tokenUsage ? `${aiTimeoutFailures.toLocaleString()} timeout, ${aiNoValidFailures.toLocaleString()} no result, ${aiTruncated.toLocaleString()} truncated, ${formatCompact(tokenUsage.summary.totalTokens)} tokens` : "Open AI metrics for usage details"} onClick={() => navigate("metrics", "ai")} />
      <Kpi label="Bandwidth" value={bandwidthUsage ? formatBytes(bandwidthUsage.summary.totalBytes) : "Open"} status={bandwidthUsage?.summary.missingAudioFiles ? "warning" : bandwidthUsage ? "ok" : "neutral"} subtext={bandwidthUsage ? `${bandwidthUsage.remoteHost || "no remote host"} - ${bandwidthUsage.summary.requests.toLocaleString()} estimated request(s)` : "Open bandwidth metrics for the slow report"} onClick={() => navigate("metrics", "bandwidth")} />
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
  return <div className="rf-step-stack">
    {headerMode === "full" && <div className="rf-chain-head">
      <div><strong>Ordered RF Chain</strong><span>Capture the exact hardware path from antenna to SDR. Order matters.</span></div>
      <div className="rf-primary-actions">
        <button disabled={busy === "load-rf-path"} onClick={() => void onLoadPrevious()}>{busy === "load-rf-path" ? "Loading..." : "Load Previous"}</button>
        <button onClick={() => { onTouched(); setPath(current => ({ ...current, chain: [...current.chain, newRfChainItem()] })); }}>Add Chain Item</button>
      </div>
    </div>}
    {headerMode === "actions" && <div className="rf-primary-actions">
      <button type="button" onClick={() => { onTouched(); setPath(current => ({ ...current, chain: [...current.chain, newRfChainItem()] })); }}>Add Chain Item</button>
    </div>}
    {headerMode === "full" && <div className="setup-note">Use RF Path to document the physical antenna/coax/filter/SDR chain. Use SDR Inventory to choose hardware. Use RF Sweep to prove which source, control channel, gain, and error settings can decode before Config Draft builds the monitoring plan.</div>}
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
      <div className="rf-chain-column-header">
        <span>#</span>
        <span>Type</span>
        <span>Description / model</span>
        <span>Connector In</span>
        <span>Connector Out</span>
        <span>Details</span>
        <span></span>
      </div>
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
    metricWarning: hasDecodeSamples ? candidate.metricWarning : candidate.metricWarning || "No parser-visible CC message-rate samples were found in this measurement window; call counts are informational, but error ranking is advisory until a rerun captures CC message-rate samples.",
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
    return { tone: "warning", text: `This run looks worse than the prior best: ${current.errorHz} Hz averaged ${currentAvg.toFixed(1)}/sec vs ${previous.bestErrorHz} Hz at ${previous.bestAvgDecodeRate.toFixed(1)}/sec. Consider returning to ${previous.bestErrorHz} Hz or sweeping the other direction.` };
  if (delta > 1)
    return { tone: "ok", text: `This run improved on the prior best: ${current.errorHz} Hz averaged ${currentAvg.toFixed(1)}/sec vs ${previous.bestErrorHz} Hz at ${previous.bestAvgDecodeRate.toFixed(1)}/sec.` };
  return { tone: "neutral", text: `This run is roughly tied with the prior best (${previous.bestErrorHz} Hz). Prefer the candidate with more samples, fewer zero-decode windows, and fewer retunes.` };
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
  gain?: string;
  sampleRateHz?: number;
  errorHz?: number;
  offsetHz?: number;
  snrDb?: number;
  confidence?: number;
};

type RecommendedWaterfallError = {
  errorHz: number;
  minErrorHz: number;
  maxErrorHz: number;
  spreadHz: number;
  observations: number;
  conflict: boolean;
};

function normalizeWaterfallSweepSelections(values: unknown): WaterfallSweepSelection[] {
  const items = Array.isArray(values) ? values : [];
  const selections = new Map<number, WaterfallSweepSelection>();
  for (const item of items) {
    const rawFrequency = typeof item === "object" && item != null
      ? Number((item as Record<string, unknown>).frequencyHz ?? (item as Record<string, unknown>).controlChannelHz)
      : Number(item);
    if (!Number.isFinite(rawFrequency) || rawFrequency <= 0)
      continue;
    const frequencyHz = Math.round(rawFrequency);
    if (typeof item !== "object" || item == null) {
      selections.set(frequencyHz, { frequencyHz });
      continue;
    }
    const row = item as Record<string, unknown>;
    const sourceIndex = Number(row.sourceIndex);
    const sampleRateHz = Number(row.sampleRateHz);
    const errorHz = Number(row.errorHz ?? row.offsetHz);
    const snrDb = Number(row.snrDb);
    const confidence = Number(row.confidence);
    selections.set(frequencyHz, {
      frequencyHz,
      ...(Number.isFinite(sourceIndex) ? { sourceIndex: Math.round(sourceIndex) } : {}),
      ...(typeof row.gain === "string" && row.gain.trim() ? { gain: row.gain.trim() } : {}),
      ...(Number.isFinite(sampleRateHz) && sampleRateHz > 0 ? { sampleRateHz: Math.round(sampleRateHz) } : {}),
      ...(Number.isFinite(errorHz) ? { errorHz: Math.round(errorHz) } : {}),
      ...(Number.isFinite(snrDb) ? { snrDb } : {}),
      ...(Number.isFinite(confidence) ? { confidence: clamp01(confidence) } : {})
    });
  }
  return [...selections.values()].sort((left, right) => left.frequencyHz - right.frequencyHz);
}

function toSiteSetupRfSelectionPayload(row: WaterfallSweepSelection) {
  return {
    frequencyHz: row.frequencyHz,
    ...(row.sourceIndex != null ? { sourceIndex: row.sourceIndex } : {}),
    gain: row.gain ?? "",
    ...(row.sampleRateHz != null ? { sampleRateHz: row.sampleRateHz } : {}),
    ...(row.errorHz != null ? { errorHz: row.errorHz } : {}),
    ...(row.snrDb != null ? { snrDb: row.snrDb } : {}),
    ...(row.confidence != null ? { confidence: row.confidence } : {})
  };
}

function mergeWaterfallSelectionsIntoSetupSources(setup: SiteSetup, selections: WaterfallSweepSelection[], fallbackSources: RfSurveySource[]) {
  const sources = fallbackSources.length ? fallbackSources : siteSetupSources(setup);
  if (!selections.length || !sources.length)
    return sources;
  const defaultSourceIndex = setup.desired.selectedSourceIndexes[0] ?? sources[0]?.index ?? 0;
  return sources.map(source => {
    const rows = selections.filter(row => (row.sourceIndex ?? defaultSourceIndex) === source.index);
    if (!rows.length)
      return source;
    const latestWithGain = [...rows].reverse().find(row => row.gain && row.gain.trim());
    const latestWithSampleRate = [...rows].reverse().find(row => Number.isFinite(row.sampleRateHz) && (row.sampleRateHz ?? 0) > 0);
    const recommendedError = recommendedWaterfallError(rows);
    const latestWithError = [...rows].reverse().find(row => Number.isFinite(row.errorHz));
    return {
      ...source,
      gain: latestWithGain?.gain?.trim() || source.gain,
      sampleRate: latestWithSampleRate?.sampleRateHz ?? source.sampleRate,
      errorHz: recommendedError?.errorHz ?? latestWithError?.errorHz ?? source.errorHz
    };
  });
}

function recommendedWaterfallError(selections: WaterfallSweepSelection[]): RecommendedWaterfallError | null {
  const observations = selections
    .map(row => ({
      errorHz: Number(row.errorHz ?? row.offsetHz),
      weight: waterfallErrorWeight(row)
    }))
    .filter(row => Number.isFinite(row.errorHz));
  if (!observations.length)
    return null;
  observations.sort((left, right) => left.errorHz - right.errorHz);
  const totalWeight = observations.reduce((sum, row) => sum + row.weight, 0);
  const midpoint = totalWeight / 2;
  let cumulative = 0;
  let median = observations[0].errorHz;
  for (const row of observations) {
    cumulative += row.weight;
    if (cumulative >= midpoint) {
      median = row.errorHz;
      break;
    }
  }
  const minErrorHz = Math.round(observations[0].errorHz);
  const maxErrorHz = Math.round(observations[observations.length - 1].errorHz);
  const spreadHz = maxErrorHz - minErrorHz;
  const errorHz = observations.length === 1 ? Math.round(median) : Math.round(median / 100) * 100;
  return {
    errorHz,
    minErrorHz,
    maxErrorHz,
    spreadHz,
    observations: observations.length,
    conflict: observations.length > 1 && spreadHz > 2_500
  };
}

function waterfallErrorWeight(row: WaterfallSweepSelection) {
  const confidence = Number.isFinite(row.confidence) ? clamp01(row.confidence!) : 0.5;
  const snrDb = Number(row.snrDb);
  const snrWeight = Number.isFinite(snrDb) ? clamp01((snrDb - 6) / 14) : 0.5;
  return Math.max(0.2, (0.35 + confidence * 0.65) * (0.35 + snrWeight * 0.65));
}

function formatSignedHz(value: number) {
  return `${value >= 0 ? "+" : ""}${formatFixed(value, 0)} Hz`;
}

function formatSignedHzInput(value: number) {
  return `${value >= 0 ? "+" : ""}${Math.round(value)}`;
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
  radioReferenceSites,
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
  onAdoptWaterfallSite,
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
  radioReferenceSites: SetupTrConfigSites | null;
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
  onAdoptWaterfallSite: (system: RfSurveySystem) => Promise<void>;
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
  const selectedWaterfallPowerSources = selectedWaterfallSourceIndexes.length
    ? effectivePowerSources.filter(source => selectedWaterfallSourceIndexes.includes(source.index))
    : [];
  const validationPowerSources = selectedWaterfallPowerSources.length ? selectedWaterfallPowerSources : effectivePowerSources;
  const waterfallRecommendedError = recommendedWaterfallError(selectedWaterfallSweepSelections);
  const validationHandoffSourceIndex = selectedWaterfallSourceIndexes.length === 1 ? selectedWaterfallSourceIndexes[0] : undefined;
  const validationErrorBaseSource = validationHandoffSourceIndex !== undefined
    ? validationPowerSources.find(source => source.index === validationHandoffSourceIndex)
    : validationPowerSources.length === 1 ? validationPowerSources[0] : undefined;
  const validationErrorBaseHz = validationErrorBaseSource?.errorHz ?? 0;
  const handoffValidationOffsets = waterfallRecommendedError ? [Math.round(waterfallRecommendedError.errorHz - validationErrorBaseHz)] : [];
  const handoffValidationOffsetText = handoffValidationOffsets.length ? handoffValidationOffsets.map(formatSignedHzInput).join(",") : "";
  const validationOffsets = parseIntegerSequence(validationErrorOffsets, [-300, 0, 300]);
  const validationAutoError = validationErrorOffsets.trim().toLowerCase() === "auto";
  const validationFormErrorSearch = validationErrorOffsets;
  const validationRecommendedOffsetText = handoffValidationOffsets.length
    ? `${handoffValidationOffsets.map(formatSignedHz).join(", ")}${validationErrorBaseSource ? ` from source ${formatSignedHz(validationErrorBaseHz)}` : ""}`
    : "";
  const validationRecommendedErrorText = waterfallRecommendedError
    ? `target error ${formatSignedHz(waterfallRecommendedError.errorHz)}${validationRecommendedOffsetText ? ` / offset ${validationRecommendedOffsetText}` : ""}${waterfallRecommendedError.conflict ? ` (selected peaks range ${formatSignedHz(waterfallRecommendedError.minErrorHz)} to ${formatSignedHz(waterfallRecommendedError.maxErrorHz)})` : ""}`
    : "";
  const selectedWaterfallGains = uniqueCaseInsensitive(selectedWaterfallSweepSelections.map(row => row.gain ?? ""));
  const waterfallSeedGainSequence = selectedWaterfallGains.length ? effectivePowerGainSequence(selectedWaterfallGains, validationPowerSources).join(",") : "";
  const validationPowerGains = effectivePowerGainSequence(parseGainSequence(powerGainSequence), validationPowerSources);
  const selectedWaterfallSampleRates = uniqueSortedFrequencies(selectedWaterfallSweepSelections.map(row => row.sampleRateHz ?? 0));
  const waterfallSampleRateHz = selectedWaterfallSampleRates.length === 1 ? selectedWaterfallSampleRates[0] : 0;
  const waterfallSeedSampleRateMhz = waterfallSampleRateHz ? formatMhzInput(waterfallSampleRateHz) : "";
  const validationRequestSampleRateHz = validationSampleRateOk ? parsedValidationSampleRateHz : 0;
  const validationRequestSampleRateOk = validationSampleRateOk;
  const validationFormSampleRateMhz = validationSampleRateMhz;
  const validationFormGainSequence = powerGainSequence;
  const validationPowerGainInvalid = validationPowerSources.some(isAirspyRfSource) && validationPowerGains.some(gain => !validateAirspyLinearityGain(gain));
  const powerSweepPasses = Math.max(1, validationPowerSources.length) * Math.max(1, validationRunControlChannels.length) * Math.max(1, validationPowerGains.length);
  const validationP25SeedCount = Math.max(1, Math.min(validationRfCandidateLimit, powerSweepPasses, validationRunControlChannels.length + (systems.length || 1)));
  const validationProbePasses = validationP25SeedCount * Math.max(1, validationOffsets.length);
  const validationMetricRunCount = hasWaterfallSweepHandoff ? Math.min(1, validationP25SeedCount) : Math.max(1, Math.min(validationMetricCount, validationP25SeedCount));
  const validationVoiceCandidateCount = hasWaterfallSweepHandoff
    ? Math.min(1, validationP25SeedCount)
    : Math.max(2, Math.min(3, systems.length || 1));
  const validationVoiceSeconds = 45;
  const validationEstimatePadSeconds = hasWaterfallSweepHandoff ? 15 : 45;
  const validationEstimateSeconds = powerSweepPasses * 2 + validationProbePasses * 10 + validationMetricRunCount * 15 + validationVoiceCandidateCount * validationVoiceSeconds + validationEstimatePadSeconds;
  const validationRunning = busy === "rf_validation_sweep";
  const validationPlan = nextExperiments.find(plan => plan.type === "rf_validation_sweep");
  const validationSweepStatus = rfValidationEffectiveStatus(validationSweep, systems);
  const validationBlocked = validationPlan?.enabled === false;
  const validationBlocker = validationPlan?.blockingIssue ?? "";
  const validationResultRows = useMemo(() => rfSweepProgressRowsFromExperiment(validationSweep), [validationSweep?.id, validationSweep?.evidenceJson]);
  const validationResultCandidates = useMemo(() => rfSweepCandidateProgressFromExperiment(validationSweep), [validationSweep?.id, validationSweep?.evidenceJson]);
  const showLiveValidationProgress = validationRunning || validationProgress?.active === true;
  const activeValidationTarget = showLiveValidationProgress ? validationTargetSystem : null;
  const activeValidationControlChannels = activeValidationTarget?.controlChannelsHz?.length ? activeValidationTarget.controlChannelsHz : validationRunControlChannels;
  const activeValidationPowerSources = activeValidationTarget ? effectivePowerSources : validationPowerSources;
  const activeValidationPowerGains = activeValidationTarget ? effectivePowerGains : validationPowerGains;
  const activeValidationPowerPasses = Math.max(1, activeValidationPowerSources.length) * Math.max(1, activeValidationControlChannels.length) * Math.max(1, activeValidationPowerGains.length);
  const activeValidationP25SeedCount = Math.max(1, Math.min(validationRfCandidateLimit, activeValidationPowerPasses, activeValidationControlChannels.length + (activeValidationTarget ? 1 : systems.length || 1)));
  const activeValidationProbePasses = activeValidationP25SeedCount * Math.max(1, validationOffsets.length);
  const activeValidationMetricRunCount = !activeValidationTarget && hasWaterfallSweepHandoff ? Math.min(1, activeValidationP25SeedCount) : Math.max(1, Math.min(validationMetricCount, activeValidationP25SeedCount));
  const activeValidationVoiceCandidateCount = activeValidationTarget ? Math.max(1, Math.min(2, activeValidationControlChannels.length)) : validationVoiceCandidateCount;
  const activeValidationEstimateSeconds = activeValidationPowerPasses * 2 + activeValidationProbePasses * 10 + activeValidationMetricRunCount * 15 + activeValidationVoiceCandidateCount * validationVoiceSeconds + (activeValidationTarget ? 30 : validationEstimatePadSeconds);
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
      .map(row => `${row.frequencyHz}:${row.sourceIndex ?? ""}:${row.gain ?? ""}:${row.sampleRateHz ?? ""}:${row.errorHz ?? ""}:${row.snrDb ?? ""}`)
      .join("|");
    if (!seedKey || validationHandoffSeedKey.current === seedKey)
      return;
    validationHandoffSeedKey.current = seedKey;
    if (waterfallSeedGainSequence)
      setPowerGainSequence(waterfallSeedGainSequence);
    if (waterfallSeedSampleRateMhz)
      setValidationSampleRateMhz(waterfallSeedSampleRateMhz);
    if (handoffValidationOffsetText)
      setValidationErrorOffsets(handoffValidationOffsetText);
  }, [hasWaterfallSweepHandoff, selectedWaterfallSweepSelections.map(row => `${row.frequencyHz}:${row.sourceIndex ?? ""}:${row.gain ?? ""}:${row.sampleRateHz ?? ""}:${row.errorHz ?? ""}:${row.snrDb ?? ""}`).join("|"), waterfallSeedGainSequence, waterfallSeedSampleRateMhz, handoffValidationOffsetText]);
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
    const targetRfPasses = Math.max(1, targetPowerSourceCount) * targetControlChannelCount * Math.max(1, targetGainCount);
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
        throw new Error("Error/gain sweep experiment did not return a job handle.");
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
      setSweepMessage(`Accepted Source ${source.index} gain ${result.gain || source.gain || "unchanged"} and error ${candidate.errorHz} Hz as pending Setup values. Live monitoring is unchanged until Apply & Resume.`);
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
        radioReferenceSites={radioReferenceSites}
        controlChannels={controlChannels}
        activeControlChannelHz={activeControlChannelHz}
        waterfallSweepSelections={selectedWaterfallSweepSelections}
        onWaterfallSweepSelections={onWaterfallSweepSelections}
        onAdoptWaterfallSite={onAdoptWaterfallSite}
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
          <div className="rf-sweep-form">
            <div className="rf-cc-runline compact">
              <label><span>Sample rate MHz</span><input className={validationRequestSampleRateOk ? "rf-short-input" : "rf-short-input invalid"} size={8} inputMode="decimal" value={validationFormSampleRateMhz} onChange={event => updateValidationSampleRate(event.target.value)} /></label>
              <label><span>Gain sequence</span><input className={validationPowerGainInvalid ? "invalid" : ""} value={validationFormGainSequence} onChange={event => setPowerGainSequence(event.target.value)} /></label>
              <label><span>Error offset</span><input className="rf-short-input" size={6} value={validationFormErrorSearch} onChange={event => setValidationErrorOffsets(event.target.value)} /></label>
              <label><span>Metric candidates</span><input className="rf-short-input" size={6} inputMode="numeric" value={validationMetricsCandidates} onChange={event => setValidationMetricsCandidates(event.target.value)} /></label>
              <button className="danger-button" disabled={Boolean(busy) || validationBlocked || !validationRequestSampleRateOk || validationPowerGainInvalid} onClick={() => void runValidationSweep()}>{busy === "rf_validation_sweep" ? "Running..." : "Run"}</button>
              {(validationRunning || validationProgress?.active) && <button disabled={sweepBusy === "cancel-validation"} onClick={() => void cancelValidationSweep()}>{sweepBusy === "cancel-validation" ? "Canceling..." : "Cancel"}</button>}
            </div>
            {validationSampleRateMessage && !waterfallSampleRateHz && <div className="settings-message error">{validationSampleRateMessage}</div>}
            {airspyPowerGainMessage && <div className={validationPowerGainInvalid ? "settings-message error" : "setup-note"}>{validationPowerGainInvalid ? `${airspyPowerGainMessage} Remove values above ${AIRSPY_LINEARITY_GAIN_MAX}.` : airspyPowerGainMessage}</div>}
            <div className="rf-waterfall-sweep-selection">
              <strong>RF Sweep CCs</strong>
              <span>{selectedWaterfallSweepControlChannels.length ? `${selectedWaterfallSweepControlChannels.map(formatRfHz).join(", ")} / gain ${validationPowerGains.join(", ")}${validationRecommendedErrorText ? ` / error ${validationRecommendedErrorText}` : ""}${waterfallSampleRateHz ? ` / ${formatMhzInput(waterfallSampleRateHz)} MS/s` : ""}${validationHandoffSourceIndex !== undefined ? ` / source ${validationHandoffSourceIndex}` : ""}` : "All requested control channels"}</span>
              {selectedWaterfallSweepControlChannels.length > 0 && <button type="button" onClick={() => onWaterfallSweepSelections?.([])}>Clear</button>}
            </div>
          </div>
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
        <div className="rf-sweep-plan" aria-label="RF sweep permutation plan">
          <div className="rf-sweep-plan-head">
            <strong>{activeValidationTarget ? "Targeted Permutation Plan and Results" : "Permutation Plan and Results"}</strong>
            <span>{activeValidationTarget ? `Only ${activeValidationTarget.siteLabel || activeValidationTarget.shortName} is being rerun; other site results are not part of this live pass.` : "Each selected site is screened across its control channels; TR and voice checks run on the strongest candidates to learn whether the site is monitorable."}</span>
          </div>
          <div className="rf-sweep-plan-grid">
            <div><span>SDR sources</span><code>{activeValidationPowerSources.map(source => `${source.sdrType || "SDR"} ${source.index}${source.serial ? ` (${source.serial})` : ""}`).join(", ") || "None"}</code></div>
            <div><span>Sample rate</span><code>{validationRequestSampleRateOk ? `${formatMhzInput(validationRequestSampleRateHz)} MHz` : "Invalid"}</code></div>
            <div><span>RF screens</span><code>{activeValidationPowerSources.length} source(s) x {activeValidationControlChannels.length} CC x {activeValidationPowerGains.length} gain = {activeValidationPowerPasses}</code></div>
            <div><span>P25 probes</span><code>{activeValidationP25SeedCount} control channel seed(s) x {validationOffsets.length} error candidate(s) = {activeValidationProbePasses}</code></div>
            <div><span>Follow-up limits</span><code>{activeValidationMetricRunCount} TR metric candidate(s); {activeValidationVoiceCandidateCount} voice trial candidate(s)</code></div>
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
      title: "Error/Gain Refinement",
      status: sweepJobRunning ? "running" : sweepJob?.status || persistedSweepStatus,
      estimate: `about ${formatElapsed(sweepEstimateSeconds)} for ${sweepPassCount} passes`,
      locked: !inventory,
      action: measurementDone(sweepJob?.status || persistedSweepStatus) ? "Rerun" : "Run",
      busyKey: "sweep",
      begin: beginSweep,
      result: undefined,
      body: <div className="rf-step-stack">
        <p>Searches nearby SDR frequency-error settings for steadier control-channel decoding after RF Sweep has found a usable CC/gain condition.</p>
        <div className="setup-note">Accepting a candidate updates the pending Setup source values. Apply & Resume remains the only action that changes live monitoring.</div>
        {!sweep && staleSweep && <StaleExperimentNotice experiment={staleSweep} activeControlChannelHz={selectedCcNumber} />}
        <div className="rf-sweep-table">
          <div className="rf-sweep-header">
            <span>SDR</span>
            <span>Precision</span>
            <span>Base Error Hz</span>
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
              <label><span>Base error Hz</span><input inputMode="numeric" value={input.errorHz} onChange={event => updateSweepField(source.index, { errorHz: event.target.value })} /></label>
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
                <span className="muted">{result.candidates.length} candidate row{result.candidates.length === 1 ? "" : "s"}; top ranked {result.candidates[0] ? `${result.candidates[0].errorHz} Hz` : "unknown"}</span>
              </div>
              <div className="setup-note">Artifacts: {result.summaryPath || result.outputDir}</div>
              {result.candidates[0] && <div className="rf-sweep-recommendation">
                <strong>Current recommendation: {result.candidates[0].errorHz} Hz</strong>
                <span>{result.candidates[0].hasDecodeSamples ? `${result.candidates[0].avgDecodeRate.toFixed(1)}/sec average across ${result.candidates[0].totalDecode ?? 0} CC message-rate sample(s)` : "No comparable CC message-rate samples captured"}</span>
              </div>}
              {sweepComparison && <div className={`rf-sweep-comparison ${sweepComparison.tone}`}>{sweepComparison.text}</div>}
              {!hasComparableDecode && <div className="setup-warning-list">
                <div>This sweep result has no parser-visible CC message-rate samples. Calls may still start/end during the window, but error ranking is advisory until a rerun captures CC message-rate samples with the current TR diagnostic settings.</div>
              </div>}
              <div className="rf-sweep-candidate-table">
                <div className="rf-sweep-candidate-header"><span></span><span>Error</span><span>CC avg</span><span>CC samples</span><span>% zero-decode</span><span>Retunes</span><span>Calls started/ended</span><span>No TX</span></div>
                {result.candidates.slice(0, 8).map(candidate => <label className={candidate.errorHz === selectedError ? "selected" : ""} key={`${source.index}-${candidate.errorHz}`}>
                  <input type="radio" name={`sweep-candidate-${source.index}`} checked={candidate.errorHz === selectedError} onChange={() => setSelectedSweepCandidates(current => ({ ...current, [String(source.index)]: candidate.errorHz }))} />
                  <code>{candidate.errorHz} Hz</code>
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
                {selectedCandidate && <button title="Uses the selected error value as the new center and reduces the range/step for a follow-up sweep. It does not run anything until you click Rerun." onClick={() => {
                  updateSweepInput(source.index, { precision: "custom", errorHz: String(selectedCandidate.errorHz), rangeHz: "300", stepHz: "100" });
                  setHighlightedSweepSource(source.index);
                  window.setTimeout(() => setHighlightedSweepSource(current => current === source.index ? null : current), 1800);
                  setSweepMessage(`Prepared Source ${source.index} for a narrower follow-up sweep centered on ${selectedCandidate.errorHz} Hz. Click Rerun to execute it.`);
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
  radioReferenceSites,
  controlChannels,
  activeControlChannelHz,
  waterfallSweepSelections,
  onWaterfallSweepSelections,
  onAdoptWaterfallSite,
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
  radioReferenceSites: SetupTrConfigSites | null;
  controlChannels: number[];
  activeControlChannelHz: number;
  waterfallSweepSelections?: WaterfallSweepSelection[];
  onWaterfallSweepSelections?: (values: WaterfallSweepSelection[]) => void;
  onAdoptWaterfallSite: (system: RfSurveySystem) => Promise<void>;
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
  const visiblePeaksRef = useRef<PositionedSpectrumPeak[]>([]);
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
  const selectedSweepControlChannelSet = new Set(selectedSweepControlChannels);
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
    if (!selectedSweepControlChannels.length)
      return;
    const current = normalizeWaterfallSweepSelections(waterfallSweepSelections);
    const selected = new Set(selectedSweepControlChannels);
    const next = current.map(row => selected.has(row.frequencyHz)
      ? {
        ...row,
        sourceIndex,
        gain: gain.trim() || selectedSource?.gain || "",
        sampleRateHz: sampleRateOk ? sampleRateHz : row.sampleRateHz
      }
      : row);
    if (JSON.stringify(next) !== JSON.stringify(current))
      onWaterfallSweepSelections?.(next);
  }, [selectedSweepControlChannels.join(","), sourceIndex, gain, sampleRateHz, sampleRateOk, selectedSource?.gain]);

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
    visiblePeaksRef.current = [];
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
      const nextOtherDetectedCcRows = updateVisibleOtherDetectedCcRows(otherDetectedCcHistoryRef.current, positionedPeaks, systems, controlChannelOptions);
      visiblePeaksRef.current = positionedPeaks;
      const now = Date.now();
      if (now - lastWaterfallTableUiAtRef.current >= 750) {
        lastWaterfallTableUiAtRef.current = now;
        setCcSignalRows(nextCcSignalRows);
        setOtherDetectedCcRows(nextOtherDetectedCcRows);
      }
      drawSpectrumFrame(spectrumCanvasRef.current, frame, smoothed, spectrumScaleRef.current, axis, {
        controlChannelsHz: controlChannelOptions,
        showControlChannels: showControlChannelLines,
        peaks: positionedPeaks
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
      ? nearestTargetControlChannel(publishedTargetFrequency, systems, controlChannelOptions, 20_000)
      : nearestTargetControlChannel(measuredFrequency, systems, controlChannelOptions, 20_000);
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
        ? `Matched saved CC ${targetLabel}; observed error ${formatSignedHz(offset)}.`
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
    const remaining = current.filter(item => item.frequencyHz !== frequencyHz);
    if (selected) {
      remaining.push({
        frequencyHz,
        sourceIndex,
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
      if (result.key.startsWith("requested:"))
        continue;
      if (!reportOtherRows.some(row => row.key === result.key))
        reportOtherRows.push({
          ...result.peak,
          displayHits: Math.max(6, result.peak.hits),
          displayMisses: 0,
          promoted: true
        });
    }
    const reportCandidates = buildWaterfallCandidateRows(ccSignalRows, reportOtherRows, systems, radioReferenceSites, controlChannelOptions, identifyResults);
    const candidateRows = reportCandidates.length
      ? reportCandidates.map(row => {
        const identify = identifyResults[row.identifyPeak.key];
        const evidence = [
          `SNR ${formatFixed(row.snrDb, 1)} dB`,
          Number.isFinite(row.offsetHz) ? `observed error ${formatSignedHz(row.offsetHz)}` : "",
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
    const nearest = nearestSpectrumPeak(xCanvas, visiblePeaksRef.current);
    if (!nearest) {
      setSpectrumHover(null);
      return;
    }
    const xCss = nearest.x * rect.width / canvas.width;
    const yCss = nearest.y * rect.height / canvas.height;
    setSpectrumHover({
      left: canvas.offsetLeft + xCss,
      top: canvas.offsetTop + Math.max(6, yCss - 34),
      peak: nearest,
      text: `${formatSpectrumTickHz(nearest.frequencyHz)} / ${formatFixed(nearest.powerDb, 1)} dB / SNR ${formatFixed(nearest.snrDb, 1)} dB`
    });
  }

  const frame = status?.frame;
  const visibleMessage = shouldShowWaterfallMessage(message, status) ? message : "";
  const peakSnrDb = frame && Number.isFinite(frame.peakDb) && Number.isFinite(frame.noiseFloorDb) ? frame.peakDb - frame.noiseFloorDb : NaN;
  const displayedOtherDetectedCcRows = [...otherDetectedCcRows];
  for (const result of Object.values(identifyResults)) {
    if (result.key.startsWith("requested:"))
      continue;
    if (!displayedOtherDetectedCcRows.some(row => row.key === result.key))
      displayedOtherDetectedCcRows.push({
        ...result.peak,
        displayHits: Math.max(6, result.peak.hits),
        displayMisses: 0,
        promoted: true
      });
  }
  const waterfallCandidates = buildWaterfallCandidateRows(ccSignalRows, displayedOtherDetectedCcRows, systems, radioReferenceSites, controlChannelOptions, identifyResults);
  async function toggleCandidateForSweep(row: WaterfallCandidateRow, selected: boolean) {
    if (!showSweepSelection)
      return;
    if (selected && row.origin !== "selected" && row.system && !systems.some(system => system.shortName.toLowerCase() === row.system?.shortName.toLowerCase()))
      await onAdoptWaterfallSite(row.system);
    toggleSweepControlChannel(row, selected);
  }
  return <div className="rf-waterfall-panel">
    <div className="rf-waterfall-controls">
      <label><span>Source</span><select value={String(sourceIndex)} disabled={controlsDisabled || locked || status?.active || sourceOptions.length <= 1} onChange={event => setSourceIndex(Number(event.target.value))}>
        {sourceOptions.map(source => <option value={String(source.index)} key={source.index}>Source {source.index} / {source.sdrType || "SDR"}{source.serial ? ` / ${source.serial}` : source.device ? ` / ${source.device}` : ""}</option>)}
      </select></label>
      <label className="rf-frequency-combo" ref={frequencyComboRef}><span>Frequency MHz</span><div className="rf-frequency-combo-input"><input className={frequencyOk ? "" : "invalid"} disabled={controlsDisabled} inputMode="decimal" value={frequencyMhz} onChange={event => setFrequencyMhz(event.target.value)} onFocus={() => setFrequencyMenuOpen(true)} /><button type="button" disabled={controlsDisabled} aria-label="Show saved control channels" title="Show saved control channels" onClick={() => setFrequencyMenuOpen(open => !open)}><ChevronDown size={14} aria-hidden="true" /></button></div>{frequencyMenuOpen && <div className="rf-frequency-menu" role="listbox" aria-label="Saved control channels">{controlChannelOptions.length === 0 ? <div className="rf-frequency-menu-empty">No saved CCs</div> : controlChannelOptions.map(value => <button type="button" role="option" aria-selected={formatMhzInput(value) === frequencyMhz} key={value} onMouseDown={event => event.preventDefault()} onClick={() => { setFrequencyMhz(formatMhzInput(value)); setFrequencyMenuOpen(false); }}>{formatMhzInput(value)}<span>{formatRfHz(value)}</span></button>)}</div>}</label>
      <label><span>Rate MHz</span><input className={sampleRateOk ? "" : "invalid"} disabled={controlsDisabled} inputMode="decimal" value={sampleRateMhz} onChange={event => setSampleRateMhz(event.target.value)} /></label>
      <label><span>{selectedSourceIsAirspy ? `Lin gain 0-${AIRSPY_LINEARITY_GAIN_MAX}` : "Gain"}</span><input className={gainOk ? "" : "invalid"} disabled={controlsDisabled} inputMode={selectedSourceIsAirspy ? "numeric" : undefined} value={gain} onChange={event => setGain(event.target.value)} /></label>
      <label><span>Power span</span><select value={String(spectrumSpanDb)} disabled={controlsDisabled} onChange={event => setSpectrumSpanDb(Number(event.target.value))}>
        <option value="20">20 dB</option>
        <option value="35">35 dB</option>
        <option value="50">50 dB</option>
        <option value="70">70 dB</option>
      </select></label>
      <label className="rf-waterfall-check"><input type="checkbox" disabled={controlsDisabled} checked={showControlChannelLines} onChange={event => setShowControlChannelLines(event.target.checked)} /><span>CC lines</span></label>
      <button type="button" className="danger-button" disabled={!canStart || status?.active === true} onClick={() => void startWaterfall()}>{busy === "start" ? "Starting..." : "Start"}</button>
      <button type="button" disabled={controlsDisabled || !status?.active || busy === "stop"} onClick={() => void stopWaterfall()}>{busy === "stop" ? "Stopping..." : "Stop"}</button>
      <button type="button" className="icon-button" disabled={controlsDisabled || !frame} aria-label="Download waterfall screen grab" title="Download waterfall screen grab" onClick={downloadWaterfallReport}><Camera size={16} aria-hidden="true" /></button>
    </div>
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
      <div className="rf-waterfall-cc-head"><span>Control Channel Candidates</span><small>Ranked by SNR. Observed error is measured from the matched selected-site or cached RR control channel.</small></div>
      <div className="rf-waterfall-candidate-table">
        <div className={showSweepSelection ? "rf-waterfall-candidate-row header" : "rf-waterfall-candidate-row header no-sweep-selection"}>
          {showSweepSelection && <span>Use</span>}<span>Site</span><span>Matched CC</span><span>Detected</span><span>SNR</span><span>Observed error</span><span>Confidence</span><span>Source</span><span>Action</span>
        </div>
        {waterfallCandidates.length === 0 ? <div className="rf-waterfall-cc-empty">Start waterfall to inspect selected and nearby RR control channels.</div> : waterfallCandidates.map(row => {
          const identify = identifyResults[row.identifyPeak.key];
          const selected = selectedSweepControlChannelSet.has(row.sweepFrequencyHz);
          return <div className={`${showSweepSelection ? "rf-waterfall-candidate-row" : "rf-waterfall-candidate-row no-sweep-selection"} ${row.origin} ${identify ? `identified ${identify.status}` : ""}`.trim()} key={row.key}>
            {showSweepSelection && <label className="rf-waterfall-use-check">
              <input type="checkbox" checked={selected} onChange={event => void toggleCandidateForSweep(row, event.target.checked)} aria-label={`Use ${formatRfHz(row.sweepFrequencyHz)} for RF Sweep`} />
              <span>{selected ? "Use" : ""}</span>
            </label>}
            <span title={row.siteLabel}>{row.siteLabel}</span>
            <code>{row.targetFrequencyHz > 0 ? formatRfHz(row.targetFrequencyHz) : "--"}</code>
            <code>{row.detectedFrequencyHz > 0 ? formatRfHz(row.detectedFrequencyHz) : "--"}</code>
            <strong>{Number.isFinite(row.snrDb) ? `${formatFixed(row.snrDb, 1)} dB` : "--"}</strong>
            <span>{Number.isFinite(row.offsetHz) ? formatSignedHz(row.offsetHz) : "--"}</span>
            <span>{Math.round(row.confidence * 100)}%</span>
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
  radioReferenceSites: SetupTrConfigSites | null,
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
      snrDb: Number.isFinite(row.snrDb) ? row.snrDb : Number.NEGATIVE_INFINITY,
      offsetHz: Number.isFinite(row.offsetHz) ? Math.round(row.offsetHz) : 0,
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
  const rrTargets = buildRadioReferenceControlChannelTargets(radioReferenceSites, selectedSystems);
  const otherCandidateRows: WaterfallCandidateRow[] = otherRows.map(row => {
    const fallbackTarget = nearestFrequencyTarget(row.frequencyHz, matchableTargets, 20_000);
    const matchedSelected = Boolean(fallbackTarget?.systemShortName && selectedNames.has(fallbackTarget.systemShortName.toLowerCase()));
    const rrTarget = matchedSelected ? null : nearestFrequencyTarget(row.frequencyHz, rrTargets, 20_000);
    const matchedRr = Boolean(rrTarget);
    const targetFrequencyHz = Math.round(fallbackTarget?.frequencyHz ?? rrTarget?.frequencyHz ?? row.frequencyHz);
    const origin: WaterfallCandidateRow["origin"] = matchedSelected
      ? "selected"
      : matchedRr ? "rr" : "unknown";
    const identifyPeak: PositionedSpectrumPeak = {
      ...row,
      tuneFrequencyHz: matchedSelected ? targetFrequencyHz : Math.round(row.frequencyHz),
      measuredFrequencyHz: Math.round(row.frequencyHz),
      targetFrequencyHz: matchedSelected ? targetFrequencyHz : 0
    };
    return {
      key: row.key,
      origin,
      siteLabel: matchedSelected ? fallbackTarget?.siteLabel ?? "Selected CC" : rrTarget?.siteLabel ?? "Unknown CC",
      systemShortName: matchedSelected ? fallbackTarget?.systemShortName ?? "" : rrTarget?.systemShortName ?? "",
      targetFrequencyHz: matchedSelected || matchedRr ? targetFrequencyHz : 0,
      detectedFrequencyHz: Math.round(row.frequencyHz),
      sweepFrequencyHz: matchedSelected || matchedRr ? targetFrequencyHz : Math.round(row.frequencyHz),
      snrDb: Number.isFinite(row.snrDb) ? row.snrDb : Number.NEGATIVE_INFINITY,
      offsetHz: matchedSelected || matchedRr ? Math.round(row.frequencyHz - targetFrequencyHz) : Number.NaN,
      confidence: waterfallOtherDetectedConfidence(row),
      hits: row.displayHits,
      identifyPeak,
      system: matchedSelected ? fallbackTarget?.system : rrTarget?.system
    };
  });
  const rows: WaterfallCandidateRow[] = [...selectedRows, ...otherCandidateRows];
  for (const result of Object.values(identifyResults)) {
    if (rows.some(row => row.key === result.key))
      continue;
    const measuredFrequencyHz = Math.round(result.measuredFrequencyHz || result.frequencyHz);
    const fallbackTarget = nearestFrequencyTarget(measuredFrequencyHz, matchableTargets, 20_000);
    const matchedSelected = Boolean(fallbackTarget?.systemShortName && selectedNames.has(fallbackTarget.systemShortName.toLowerCase()));
    const rrTarget = matchedSelected ? null : nearestFrequencyTarget(measuredFrequencyHz, rrTargets, 20_000);
    const matchedRr = Boolean(rrTarget);
    const targetFrequencyHz = Math.round(fallbackTarget?.frequencyHz ?? rrTarget?.frequencyHz ?? result.targetFrequencyHz ?? result.frequencyHz);
    const origin: WaterfallCandidateRow["origin"] = matchedSelected
      ? "selected"
      : matchedRr ? "rr" : "unknown";
    rows.push({
      key: result.key,
      origin,
      siteLabel: matchedSelected ? fallbackTarget?.siteLabel ?? result.targetLabel ?? "Selected CC" : rrTarget?.siteLabel ?? "Unknown CC",
      systemShortName: matchedSelected ? fallbackTarget?.systemShortName ?? "" : rrTarget?.systemShortName ?? "",
      targetFrequencyHz: matchedSelected || matchedRr ? targetFrequencyHz : 0,
      detectedFrequencyHz: measuredFrequencyHz,
      sweepFrequencyHz: matchedSelected || matchedRr ? targetFrequencyHz : measuredFrequencyHz,
      snrDb: result.peak.snrDb,
      offsetHz: matchedSelected || matchedRr ? Math.round(measuredFrequencyHz - targetFrequencyHz) : Number.NaN,
      confidence: clamp01(result.peak.hits / 30),
      hits: result.peak.hits,
      identifyPeak: result.peak,
      system: matchedSelected ? fallbackTarget?.system : rrTarget?.system
    });
  }
  return rows
    .sort((left, right) => (right.snrDb - left.snrDb) || (right.confidence - left.confidence) || Math.abs(left.offsetHz) - Math.abs(right.offsetHz));
}

function nearestFrequencyTarget<T extends { frequencyHz: number }>(frequencyHz: number, targets: T[], maxDistanceHz = Number.POSITIVE_INFINITY) {
  return targets
    .map(target => ({ target, distance: Math.abs(target.frequencyHz - frequencyHz) }))
    .filter(row => row.distance <= maxDistanceHz)
    .sort((left, right) => left.distance - right.distance)[0]?.target ?? null;
}

function buildRadioReferenceControlChannelTargets(radioReferenceSites: SetupTrConfigSites | null, selectedSystems: RfSurveySystem[] = []): WaterfallFrequencyTarget[] {
  const rows: WaterfallFrequencyTarget[] = [];
  const selectedStates = inferSelectedSetupStates(selectedSystems, radioReferenceSites);
  for (const site of radioReferenceSites?.sites ?? []) {
    const siteLabel = site.name || site.shortName || "RR site";
    if (selectedStates.size > 0) {
      const siteState = inferStateFromText([siteLabel, site.shortName, radioReferenceSites?.systemName]);
      if (!siteState || !selectedStates.has(siteState))
        continue;
    }
    const systemShortName = site.shortName || site.name || "";
    for (const frequencyMhz of site.controlChannelsMhz ?? []) {
      const frequencyHz = Math.round(Number(frequencyMhz) * 1_000_000);
      if (!Number.isFinite(frequencyHz) || frequencyHz <= 0)
        continue;
      rows.push({
        systemShortName,
        siteLabel,
        frequencyHz,
        system: radioReferenceSiteToRfSurveySystem(site, radioReferenceSites?.systemName)
      });
    }
  }
  return rows;
}

function radioReferenceSiteToRfSurveySystem(site: SetupTrConfigSite, systemName?: string): RfSurveySystem {
  const shortName = site.shortName || site.name || "rr-site";
  return {
    shortName,
    siteLabel: site.name || shortName,
    controlChannelsHz: uniqueSortedFrequencies((site.controlChannelsMhz ?? [])
      .map(frequencyMhz => Math.round(Number(frequencyMhz) * 1_000_000))
      .filter(frequencyHz => Number.isFinite(frequencyHz) && frequencyHz > 0)),
    voiceFrequenciesHz: [],
    talkgroupSystemShortName: systemName?.trim() || undefined
  };
}

function inferSelectedSetupStates(selectedSystems: RfSurveySystem[], radioReferenceSites: SetupTrConfigSites | null) {
  const selectedNames = new Set(selectedSystems.map(system => system.shortName.toLowerCase()));
  const values = selectedSystems.flatMap(system => [system.siteLabel, system.shortName]);
  for (const site of radioReferenceSites?.sites ?? []) {
    const siteKey = (site.shortName || site.name || "").toLowerCase();
    if (selectedNames.has(siteKey))
      values.push(site.name, site.shortName, radioReferenceSites?.systemName ?? "");
  }
  return new Set(values.map(value => inferStateFromText([value])).filter((value): value is string => Boolean(value)));
}

function inferStateFromText(values: Array<string | undefined | null>) {
  const text = values.filter(Boolean).join(" ").toLowerCase();
  if (!text.trim())
    return "";
  for (const [token, state] of Object.entries(radioReferenceStateTokens)) {
    if (new RegExp(`(^|[^a-z0-9])${escapeRegex(token)}([^a-z0-9]|$)`, "i").test(text))
      return state;
  }
  return "";
}

const radioReferenceStateTokens: Record<string, string> = {
  al: "alabama",
  alabama: "alabama",
  ak: "alaska",
  alaska: "alaska",
  az: "arizona",
  arizona: "arizona",
  ar: "arkansas",
  arkansas: "arkansas",
  ca: "california",
  california: "california",
  co: "colorado",
  colorado: "colorado",
  ct: "connecticut",
  connecticut: "connecticut",
  de: "delaware",
  delaware: "delaware",
  fl: "florida",
  florida: "florida",
  ga: "georgia",
  georgia: "georgia",
  hi: "hawaii",
  hawaii: "hawaii",
  id: "idaho",
  idaho: "idaho",
  il: "illinois",
  illinois: "illinois",
  in: "indiana",
  indiana: "indiana",
  ia: "iowa",
  iowa: "iowa",
  ks: "kansas",
  kansas: "kansas",
  ky: "kentucky",
  kentucky: "kentucky",
  la: "louisiana",
  louisiana: "louisiana",
  me: "maine",
  maine: "maine",
  md: "maryland",
  maryland: "maryland",
  ma: "massachusetts",
  massachusetts: "massachusetts",
  mi: "michigan",
  michigan: "michigan",
  mn: "minnesota",
  minnesota: "minnesota",
  ms: "mississippi",
  mississippi: "mississippi",
  mo: "missouri",
  missouri: "missouri",
  mt: "montana",
  montana: "montana",
  ne: "nebraska",
  nebraska: "nebraska",
  nv: "nevada",
  nevada: "nevada",
  nh: "new-hampshire",
  "new hampshire": "new-hampshire",
  nj: "new-jersey",
  "new jersey": "new-jersey",
  nm: "new-mexico",
  "new mexico": "new-mexico",
  ny: "new-york",
  "new york": "new-york",
  nc: "north-carolina",
  "north carolina": "north-carolina",
  nd: "north-dakota",
  "north dakota": "north-dakota",
  oh: "ohio",
  ohio: "ohio",
  ok: "oklahoma",
  oklahoma: "oklahoma",
  or: "oregon",
  oregon: "oregon",
  pa: "pennsylvania",
  pennsylvania: "pennsylvania",
  ri: "rhode-island",
  "rhode island": "rhode-island",
  sc: "south-carolina",
  "south carolina": "south-carolina",
  sd: "south-dakota",
  "south dakota": "south-dakota",
  tn: "tennessee",
  tennessee: "tennessee",
  tx: "texas",
  texas: "texas",
  ut: "utah",
  utah: "utah",
  vt: "vermont",
  vermont: "vermont",
  va: "virginia",
  virginia: "virginia",
  wa: "washington",
  washington: "washington",
  wv: "west-virginia",
  "west virginia": "west-virginia",
  wi: "wisconsin",
  wisconsin: "wisconsin",
  wy: "wyoming",
  wyoming: "wyoming"
};

function waterfallCandidateSourceLabel(row: WaterfallCandidateRow, identify?: WaterfallIdentifyResult) {
  const prefix = row.origin === "selected" ? "Selected site" : row.origin === "rr" ? "RR cached site" : "Unknown CC";
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
type WaterfallDetectedCcTrack = PositionedSpectrumPeak & { displayHits: number; displayMisses: number; promoted: boolean };
type P25IdentifyFields = { nac: string; wacn: string; systemId: string; rfss: string; site: string; decodedControlChannelHz: number; adjacentSites: string[]; secondaryControlChannels: string[]; tsbkCount: number; grantCount: number; demod: string; sourceIndex?: number; exitCode?: number; timedOut: boolean };
type WaterfallIdentifyResult = { key: string; peak: PositionedSpectrumPeak; frequencyHz: number; measuredFrequencyHz: number; targetFrequencyHz: number; status: "running" | "passed" | "failed" | "blocked"; summary: string; detail: string; targetLabel: string; offsetHz: number; createdAtUtc: string; experimentId?: string; fields?: P25IdentifyFields };
type SpectrumHover = { left: number; top: number; text: string; peak: PositionedSpectrumPeak };
type SpectrumDrawOptions = { controlChannelsHz: number[]; showControlChannels: boolean; peaks: PositionedSpectrumPeak[] };
type WaterfallCcSignalRow = { systemShortName: string; siteLabel: string; frequencyHz: number; status: "candidate" | "weak-trace" | "not-seen"; label: string; peakFrequencyHz: number; offsetHz: number; snrDb: number; powerDb: number; confidence: number };
type WaterfallFrequencyTarget = { systemShortName: string; siteLabel: string; frequencyHz: number; system?: RfSurveySystem };
type WaterfallCandidateRow = { key: string; origin: "selected" | "rr" | "unknown"; siteLabel: string; systemShortName: string; targetFrequencyHz: number; detectedFrequencyHz: number; sweepFrequencyHz: number; snrDb: number; offsetHz: number; confidence: number; hits: number; identifyPeak: PositionedSpectrumPeak; system?: RfSurveySystem };
type WaterfallCcSignalTrack = { signalScore: number; hitCount: number; frameCount: number; peakFrequencyHz: number; offsetHz: number; snrDb: number; powerDb: number };

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
  const currentKeys = new Set(uniqueTargets.keys());
  for (const key of [...history.keys()]) {
    if (!currentKeys.has(key))
      history.delete(key);
  }
  return [...uniqueTargets.values()].map(target => {
    const key = `${target.systemShortName}:${target.frequencyHz}`;
    const nearest = nearestPeakAroundFrequency(target.frequencyHz, powers, frame, axis, 25_000);
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
    ? `Matched saved CC ${base.targetLabel}; observed error ${formatSignedHz(base.offsetHz)}.`
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

function waterfallOtherDetectedConfidence(row: Pick<WaterfallDetectedCcTrack, "displayHits" | "displayMisses">) {
  return clamp01((row.displayHits - row.displayMisses * 0.5) / 30);
}

function updateVisibleOtherDetectedCcRows(history: Map<string, WaterfallDetectedCcTrack>, peaks: PositionedSpectrumPeak[], systems: RfSurveySystem[], fallbackControlChannels: number[]) {
  const promotionHits = 6;
  const retentionMisses = 36;
  const activeKeys = new Set<string>();
  for (const track of history.values())
    track.displayMisses += 1;
  for (const peak of peaks.filter(row => !nearestTargetControlChannel(row.frequencyHz, systems, fallbackControlChannels, 20_000))) {
    activeKeys.add(peak.key);
    const previous = history.get(peak.key);
    const nextHits = Math.min(30, (previous?.displayHits ?? 0) + 1);
    history.set(peak.key, {
      ...peak,
      frequencyHz: ema(previous?.frequencyHz, peak.frequencyHz, 0.24),
      powerDb: ema(previous?.powerDb, peak.powerDb, 0.32),
      snrDb: ema(previous?.snrDb, peak.snrDb, 0.32),
      x: ema(previous?.x, peak.x, 0.28),
      y: ema(previous?.y, peak.y, 0.28),
      hits: peak.hits,
      misses: peak.misses,
      displayHits: nextHits,
      displayMisses: 0,
      promoted: Boolean(previous?.promoted) || nextHits >= promotionHits
    });
  }
  for (const [key, track] of history.entries()) {
    if (!activeKeys.has(key))
      track.displayHits = Math.max(0, track.displayHits - 0.35);
    if (track.displayMisses > retentionMisses && track.displayHits < promotionHits)
      history.delete(key);
  }
  return [...history.values()]
    .filter(track => track.promoted && track.displayMisses <= retentionMisses)
    .sort((left, right) => right.snrDb - left.snrDb)
    .slice(0, 8);
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

function nearestSpectrumPeak(xCanvas: number, peaks: PositionedSpectrumPeak[]) {
  let nearest: PositionedSpectrumPeak | null = null;
  let nearestDistance = Infinity;
  for (const peak of peaks) {
    const distance = Math.abs(peak.x - xCanvas);
    if (distance < nearestDistance) {
      nearest = peak;
      nearestDistance = distance;
    }
  }
  return nearest && nearestDistance <= 16 ? nearest : null;
}

function spectrumGeometry(width = 1024, height = 150) {
  const margin = { left: 58, right: 10, top: 10, bottom: 30 };
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
  ctx.fillStyle = "#00356f";
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
    ctx.fillStyle = "#d7ecff";
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
  for (let x = 0; x < canvas.width; x++) {
    const position = x / Math.max(1, canvas.width - 1) * (powers.length - 1);
    const left = Math.floor(position);
    const right = Math.min(powers.length - 1, left + 1);
    const mix = position - left;
    const value = powers[left] * (1 - mix) + powers[right] * mix;
    const color = waterfallColor((value - low) / Math.max(1, high - low));
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
        <span>Peak delta</span>
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
                  <span>Tune error {candidate.errorHz} Hz</span>
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
      <span>{system?.siteLabel || system?.shortName || candidate.systemShortName || "Unknown site"} / Source {candidate.sourceIndex}, {candidate.controlChannelHz ? formatRfHz(Number(candidate.controlChannelHz)) : "--"}, gain {candidate.gain ?? "auto"}, tune error {candidate.errorHz ?? 0} Hz</span>
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
            <code>{formatRfHz(Number(best.controlChannelHz))} / source {best.sourceIndex} / gain {best.gain ?? "auto"} / tune error {best.errorHz ?? 0} Hz</code>
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
                <span>{candidate ? `gain ${candidate.gain ?? "auto"}, tune error ${candidate.errorHz ?? 0} Hz` : "--"}</span>
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
      <div><span>Peak delta</span><code>{Number.isFinite(ccOffset) ? `${formatFixed(ccOffset, 0)} Hz` : "--"}</code></div>
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
      <div className="rf-power-row header"><span>SDR</span><span>CC</span><span>Status</span><span>Quality</span><span>Gain</span><span>CC SNR</span><span>CC Peak</span><span>Noise</span><span>Peak delta</span><span>Strongest</span><span>Clip</span><span>Output</span></div>
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

function RfMetricsPanel({ data, rangeHours, bySystem, baseline, setBySystem, setBaseline }: { data: TrTroubleshoot; rangeHours: number; bySystem: boolean; baseline: string; setBySystem: (value: boolean) => void; setBaseline: (value: string) => void }) {
  return <div className="rf-metrics-panel">
    <div className="metric-controls">
      <label><input type="checkbox" checked={bySystem} onChange={e => setBySystem(e.target.checked)} /> By system</label>
      <label>Compare against baseline <select value={baseline} onChange={e => setBaseline(e.target.value)}><option>7d</option><option>14d</option><option>30d</option></select></label>
    </div>
    <div className="tr-chart-grid">{data.health.charts.map(c => <TrHealthChartView chart={c} key={c.title} />)}</div>
    <RfAnalysisPanel data={data} rangeHours={rangeHours} />
  </div>;
}

function IncidentMetricsPanel({ dashboard, audit }: { dashboard: Dashboard | null; audit: IncidentOperationAuditRow[] | null }) {
  if (!dashboard) return <div className="card">Loading incident metrics...</div>;
  const unmapped = dashboard.incidents.filter(incident => !dashboard.locationHeat.some(row => (row.incidentLinks ?? []).some(link => link.incidentId === incident.id))).length;
  const weakEvidence = dashboard.incidents.filter(incident => incident.calls.length < 2).length;
  const lowConfidence = dashboard.incidents.filter(incident => incident.confidence < 0.55).length;
  const shortEvidence = dashboard.incidents.filter(incident => incident.calls.some(call => (call.transcript ?? "").trim().length < 40)).length;
  const issueRows = dashboard.incidents
    .map(incident => {
      const issues = [
        incident.calls.length < 2 ? "weak evidence" : "",
        incident.confidence < 0.55 ? "low confidence" : "",
        !dashboard.locationHeat.some(row => (row.incidentLinks ?? []).some(link => link.incidentId === incident.id)) ? "unmapped" : "",
        incident.calls.some(call => (call.transcript ?? "").trim().length < 40) ? "short transcript evidence" : ""
      ].filter(Boolean);
      return { incident, issues };
    })
    .filter(row => row.issues.length)
    .slice(0, 20);
  return <div className="incident-metrics-panel">
    <div className="audit-kpis">
      <Kpi label="Incidents" value={dashboard.incidents.length.toLocaleString()} subtext="Generated in selected range" />
      <Kpi label="Unmapped" value={unmapped.toLocaleString()} status={unmapped > 0 ? "warning" : "ok"} subtext="No dashboard map link" />
      <Kpi label="Weak Evidence" value={weakEvidence.toLocaleString()} status={weakEvidence > 0 ? "warning" : "ok"} subtext="Fewer than 2 source calls" />
      <Kpi label="Low Confidence" value={lowConfidence.toLocaleString()} status={lowConfidence > 0 ? "warning" : "ok"} subtext="LLM confidence below 55%" />
      <Kpi label="Short Evidence" value={shortEvidence.toLocaleString()} status={shortEvidence > 0 ? "warning" : "ok"} subtext="Incident includes very short transcripts" />
    </div>
    <div className="card">
      <h3>Incident Association Watchlist</h3>
      {issueRows.length ? issueRows.map(({ incident, issues }) => <div className="incident-quality-row" key={incident.id}>
        <strong>{incident.title}</strong>
        <span className="muted">{issues.join(", ")} / {label(incident.category)} / {incident.calls.length.toLocaleString()} source call(s) / confidence {(incident.confidence * 100).toFixed(0)}%</span>
      </div>) : <p className="muted">No incident association issues detected in this range.</p>}
    </div>
    <div className="card">
      <h3>Recent Incident Decisions</h3>
      {!audit ? <p className="muted">Loading incident audit decisions...</p> : audit.length === 0 ? <p className="muted">No recent incident audit decisions.</p> :
        <table className="table compact-ai-table"><thead><tr><th>Time</th><th>System</th><th>Decision</th><th>Reason</th><th>Score</th><th>Calls</th></tr></thead><tbody>{audit.slice(0, 40).map(row => <tr key={row.id}>
          <td>{new Date(row.timestampUtc).toLocaleString()}</td>
          <td>{row.systemShortName}</td>
          <td><span className={row.accepted ? "section-status ok" : "section-status error"}>{row.accepted ? "Accepted" : "Rejected"}</span></td>
          <td>{row.reason.replace(/^accepted:|^rejected:/, "")}</td>
          <td>{row.score.toFixed(2)}</td>
          <td>{row.callIds.length ? row.callIds.join(", ") : "--"}</td>
        </tr>)}</tbody></table>}
    </div>
  </div>;
}

function CpuPanel({ snapshot, reload }: { snapshot: SystemCpuSnapshot | null; reload: () => Promise<void> }) {
  if (!snapshot) return <div className="card">Loading CPU/resource status...</div>;
  const latest = snapshot.latest;
  const peak = snapshot.peaks;
  const statusClass = (status: string) => status === "error" ? "error" : status === "warning" ? "warning" : status === "ok" ? "ok" : "";
  const throttle = snapshot.insights.find(i => i.label === "Throttle flags");
  return <div className="system-cpu-panel">
    <div className={`settings-message ${snapshot.severity === "error" ? "error" : snapshot.severity === "warning" ? "warning" : "ok"}`}>
      {snapshot.summary}
    </div>
    <div className="section kpis">
      <Kpi label="TR CPU" value={latest ? `${latest.trCpuHostPercent.toFixed(0)}%` : "--"} status={!latest ? "neutral" : latest.trCpuHostPercent >= 90 ? "error" : latest.trCpuHostPercent >= 75 ? "warning" : "ok"} subtext={latest ? `${latest.trCpuPercent.toFixed(0)}% process CPU across ${snapshot.processorCount} core(s)` : "No resource sample"} />
      <Kpi label="Host Load" value={latest ? latest.hostLoad1.toFixed(2) : "--"} status={!latest ? "neutral" : latest.hostLoadHostPercent >= 150 ? "warning" : "ok"} subtext={latest ? `${latest.hostLoadHostPercent.toFixed(0)}% of host` : "No resource sample"} />
      <Kpi label="Temperature" value={latest ? `${latest.hostTempC.toFixed(1)} C` : "--"} status={!latest ? "neutral" : latest.hostTempC >= 80 ? "error" : latest.hostTempC >= 70 ? "warning" : "ok"} subtext={latest ? `2h peak ${peak.hostTempC.toFixed(1)} C` : "No thermal sample"} />
      <Kpi label="TR Memory" value={latest ? `${latest.trRssMb.toFixed(0)} MB` : "--"} status={!latest ? "neutral" : latest.trRssMb >= 2048 ? "warning" : "ok"} subtext={latest ? `2h peak ${peak.trRssMb.toFixed(0)} MB RSS` : "No RSS sample"} />
      <Kpi label="TR Threads" value={latest ? latest.trThreadCount.toLocaleString() : "--"} status={!latest ? "neutral" : latest.trThreadCount >= 250 ? "warning" : "ok"} subtext={latest ? `2h peak ${peak.trThreadCount.toLocaleString()} thread(s)` : "No thread sample"} />
      <Kpi label="Throttle" value={latest?.hostThrottledFlags || "none"} status={throttle?.status === "error" ? "error" : throttle?.status === "warning" ? "warning" : "ok"} subtext="Pi thermal/power flags when available" />
    </div>
    <div className="card">
      <div className="card-heading-row">
        <h3>Operator Readout</h3>
        <button onClick={() => void reload()}>Refresh</button>
      </div>
      <table className="table"><thead><tr><th>Signal</th><th>Value</th><th>Status</th><th>Meaning</th></tr></thead><tbody>
        {snapshot.insights.map(row => <tr key={row.label}>
          <td>{row.label}</td>
          <td>{row.value}</td>
          <td><span className={`section-status ${statusClass(row.status)}`.trim()}>{label(row.status)}</span></td>
          <td>{row.detail}</td>
        </tr>)}
      </tbody></table>
    </div>
    <div className="card">
      <h3>Two-Hour Peaks</h3>
      <table className="table compact-ai-table"><tbody>
        <tr><td>TR CPU</td><td>{peak.trCpuPercent.toFixed(0)}% process CPU</td><td>{peak.trCpuHostPercent.toFixed(0)}% of host</td></tr>
        <tr><td>Host load</td><td>{peak.hostLoad1.toFixed(2)}</td><td>{peak.hostLoadHostPercent.toFixed(0)}% of host</td></tr>
        <tr><td>Temperature</td><td>{peak.hostTempC.toFixed(1)} C</td><td>peak thermal sample</td></tr>
        <tr><td>Memory</td><td>{peak.trRssMb.toFixed(0)} MB RSS</td><td>resident memory</td></tr>
        <tr><td>Threads</td><td>{peak.trThreadCount.toLocaleString()}</td><td>capture/DSP worker count</td></tr>
      </tbody></table>
    </div>
  </div>;
}

function ServicesManager({ runtime, data, restartBusy, restartMessages, onRestart, onStopTr }: { runtime: any | null; data: TrTroubleshoot; restartBusy: "" | "pizzad" | "trunk-recorder" | "qdrant"; restartMessages: Record<string, string>; onRestart: (service: "pizzad" | "trunk-recorder" | "qdrant") => void; onStopTr: () => void }) {
  if (!runtime) return <div className="card">Loading service status...</div>;
  const embeddings = runtime.queues?.embeddings;
  const trIntentionallyStopped = runtime.liveTrActivity?.status === "stopped";
  const healthIssues = data.health.metrics.filter(row => row.isIssue).length + data.health.systems.filter(row => row.isIssue).length;
  const services = [
    { key: "pizzad" as const, title: "Pizzad", button: "Restart Pizzad", svc: runtime.service?.pizzad },
    { key: "trunk-recorder" as const, title: "Trunk Recorder", button: "Restart TR", svc: runtime.service?.trunkRecorder },
    { key: "qdrant" as const, title: "Qdrant", button: "Restart Qdrant", svc: runtime.service?.qdrant }
  ];
  const serviceStatusClass = (row: typeof services[number]) =>
    row.key === "trunk-recorder" && trIntentionallyStopped
      ? "status-paused"
      : row.svc?.ok ? "status-completed" : "status-failed";
  return <div className="system-manager-grid">
    <div className="service-action-grid">
      {services.map(row => <div className="service-action-card" key={row.key}>
        <div>
          <strong>{row.title}</strong>
          <span className={`job-status ${serviceStatusClass(row)}`}>{row.svc?.active || "unknown"}</span>
        </div>
        <button className="danger-button" disabled={restartBusy === row.key} onClick={() => onRestart(row.key)}>{restartBusy === row.key ? "Restarting..." : row.button}</button>
        {row.key === "trunk-recorder" && <button className="danger-button secondary" disabled={restartBusy === row.key} onClick={onStopTr}>{restartBusy === row.key ? "Working..." : "Stop TR"}</button>}
        {restartMessages[row.key] && <span className={restartMessages[row.key].includes("failed") || restartMessages[row.key].includes("Unsupported") ? "settings-message error" : "settings-message ok"}>{restartMessages[row.key]}</span>}
      </div>)}
    </div>
    <div className="audit-kpis">
      <Kpi label="Pizzad" value={runtime.service?.pizzad?.active || "unknown"} status={runtime.service?.pizzad?.ok ? "ok" : "error"} subtext={`${formatBytes(runtime.process?.workingSetBytes || 0)} RSS, ${runtime.process?.threadCount ?? 0} thread(s)`} />
      <Kpi label="Trunk Recorder" value={runtime.service?.trunkRecorder?.active || "unknown"} status={trIntentionallyStopped ? "warning" : runtime.service?.trunkRecorder?.ok ? "ok" : "error"} subtext={trIntentionallyStopped ? "Stopped by operator" : healthIssues > 0 ? `${healthIssues} RF/resource issue row(s)` : "RF/resource health OK"} />
      <Kpi label="Qdrant" value={runtime.service?.qdrant?.active || "unknown"} status={runtime.service?.qdrant?.ok && embeddings?.qdrantOk ? "ok" : embeddings?.enabled ? "error" : "neutral"} subtext={embeddings?.collection || "collection"} />
      <Kpi label="Embeddings" value={embeddings?.enabled ? label(embeddings.status || "unknown") : "Disabled"} status={!embeddings?.enabled ? "neutral" : embeddings.status === "ok" ? "ok" : "warning"} subtext={`${embeddings?.queueDepth ?? 0} queued, ${(embeddings?.pendingCalls ?? 0).toLocaleString()} pending, ${(embeddings?.failedCalls ?? 0).toLocaleString()} failed`} />
      <Kpi label="Vector Latency" value={`${Number(embeddings?.lastSearchMs || 0).toFixed(0)}ms`} status="ok" subtext={`upsert ${Number(embeddings?.lastUpsertMs || 0).toFixed(0)}ms, dim ${embeddings?.vectorSize || "--"}`} />
      <Kpi label="Storage" value={formatBytes(Number(runtime.storage?.databaseBytes || 0) + Number(runtime.storage?.qdrantBytes || 0))} status="ok" subtext="database + Qdrant" />
    </div>
    {embeddings?.lastError && <div className="card"><h3>Embedding Pipeline</h3><p className="settings-message error">{embeddings.lastError}</p></div>}
    <div className="card">
      <h3>Service Details</h3>
      <table className="table"><thead><tr><th>Unit</th><th>Active</th><th>Enabled</th><th>Substate</th><th>Main PID</th><th>Started</th></tr></thead><tbody>{services.filter(row => row.svc).map(row => <tr key={row.key}>
        <td>{row.svc.unit}</td>
        <td><span className={`job-status ${serviceStatusClass(row)}`}>{row.svc.active || "unknown"}</span></td>
        <td>{row.svc.enabled || "--"}</td>
        <td>{row.svc.detail?.SubState || "--"}</td>
        <td>{row.svc.detail?.MainPID || "--"}</td>
        <td>{row.svc.detail?.ActiveEnterTimestamp || "--"}</td>
      </tr>)}</tbody></table>
    </div>
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

function RecommendationsPanel({ recommendations, busy, message, onOpen, onState, onAction, onExcludeTalkgroups }: { recommendations: SystemRecommendations | null; busy: boolean; message: string; onOpen: (target: { topTab: string; subTab: string; anchor?: string }) => void; onState: (id: string, action: "ignore" | "restore" | "reset-baseline") => Promise<void>; onAction: (action: SystemRecommendations["items"][number]["actions"][number]) => Promise<void>; onExcludeTalkgroups: (targets: { systemShortName?: string | null; talkgroup: number }[]) => Promise<void> }) {
  const [activeRunbookId, setActiveRunbookId] = useState<string | null>(null);
  if (!recommendations) return <div className="card">Loading recommendations...</div>;
  const ignoredItems = recommendations.ignoredItems ?? [];
  const activeRunbook = [...recommendations.items, ...ignoredItems].find(item => item.id === activeRunbookId);
  return <div className="trouble-panel recommendations-panel">
    {message && <div className={message.toLowerCase().includes("fail") || message.toLowerCase().includes("error") ? "settings-message error" : "settings-message ok"}>{message}</div>}
    {activeRunbook?.runbook && <RunbookDetail item={activeRunbook} busy={busy} onClose={() => setActiveRunbookId(null)} onOpen={onOpen} onState={onState} onExcludeTalkgroups={onExcludeTalkgroups} />}
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
          {item.baseline && <RecommendationBaseline item={item} busy={busy} onReset={() => onState(item.id, "reset-baseline")} />}
          <div className="recommendation-action">{item.action}</div>
          {item.actions?.length > 0 && <div className="recommendation-buttons recommendation-primary-actions">
            {item.actions.map(action => <button key={action.kind} className={action.kind === "pause-ingest" ? "danger-button" : ""} disabled={busy} title={action.description} onClick={() => void onAction(action)}>{action.label}</button>)}
          </div>}
          {item.runbook && <div className="recommendation-buttons">
            {item.runbook && <button disabled={busy} onClick={() => setActiveRunbookId(item.id)}>Troubleshoot Now</button>}
          </div>}
          <div className="recommendation-buttons muted-actions">
            <button disabled={busy} onClick={() => void onState(item.id, "ignore")}>Ignore</button>
          </div>
        </div>)}
      </div>}
    {!activeRunbook && ignoredItems.length > 0 && <div className="card">
      <h3>Ignored Findings</h3>
      <div className="recommendation-list">
        {ignoredItems.map(item => <div className={`card recommendation-card severity-${item.severity}`} key={item.id}>
          <div className="recommendation-head">
            <span className={`recommendation-severity ${item.severity}`}>{item.severity}</span>
            <span className="muted">{label(item.section)}</span>
          </div>
          <h3>{item.title}</h3>
          <p>{item.detail}</p>
          <div className="recommendation-buttons">
            {item.runbook && <button disabled={busy} onClick={() => setActiveRunbookId(item.id)}>Details</button>}
            <button disabled={busy} onClick={() => void onState(item.id, "restore")}>Restore</button>
          </div>
        </div>)}
      </div>
    </div>}
  </div>;
}

function RecommendationBaseline({ item, busy, onReset }: { item: NonNullable<SystemRecommendations["items"][number]>; busy: boolean; onReset: () => Promise<void> }) {
  const baseline = item.baseline;
  if (!baseline) return null;
  const first = new Date(baseline.firstSeenUtc);
  const last = new Date(baseline.lastSeenUtc);
  return <div className={`recommendation-baseline ${baseline.priorityDemoted ? "demoted" : ""}`}>
    <div>
      <strong>{baseline.priorityDemoted ? `Baseline: demoted from ${baseline.originalSeverity}` : "Baseline observation"}</strong>
      <span>{baseline.baselineValue || "No baseline value recorded"}</span>
      <small>First {Number.isNaN(first.getTime()) ? baseline.firstSeenUtc : first.toLocaleString()} / last {Number.isNaN(last.getTime()) ? baseline.lastSeenUtc : last.toLocaleString()} / {Math.round(baseline.ageHours).toLocaleString()}h observed</small>
    </div>
    <button disabled={busy} onClick={() => void onReset()}>Reset Baseline</button>
  </div>;
}

function RunbookDetail({ item, busy, onClose, onOpen, onState, onExcludeTalkgroups }: { item: NonNullable<SystemRecommendations["items"][number]>; busy: boolean; onClose: () => void; onOpen: (target: { topTab: string; subTab: string; anchor?: string }) => void; onState: (id: string, action: "ignore" | "restore" | "reset-baseline") => Promise<void>; onExcludeTalkgroups: (targets: { systemShortName?: string | null; talkgroup: number }[]) => Promise<void> }) {
  const runbook = item.runbook;
  if (!runbook) return null;
  const issueDiagnostics = (runbook.diagnostics ?? []).filter(row => row.status === "issue");
  const contextDiagnostics = (runbook.diagnostics ?? []).filter(row => row.status !== "issue").slice(0, 6);
  const visibleDiagnostics = [...issueDiagnostics, ...contextDiagnostics].slice(0, 8);
  return <div className={`card runbook-detail severity-${item.severity}`}>
    <div className="recommendation-head">
      <div>
        <span className={`recommendation-severity ${item.severity}`}>{item.severity}</span>
        <h3>{runbook.title}</h3>
      </div>
      <button onClick={onClose}>Back to Recommendations</button>
    </div>
    <p>{runbook.goal}</p>
    {item.baseline && <RecommendationBaseline item={item} busy={busy} onReset={() => onState(item.id, "reset-baseline")} />}
    {runbook.evidence.length > 0 && <div className="runbook-evidence">
      <strong>Use this evidence</strong>
      <ul>{runbook.evidence.map(row => <li key={row}>{row}</li>)}</ul>
    </div>}
    {runbook.diagnostics?.length > 0 && <div className="runbook-diagnostics">
      <div className="recommendation-head">
        <div>
          <h3>What Needs Attention</h3>
          <p className="muted">{issueDiagnostics.length ? `${issueDiagnostics.length} issue signal(s) found; showing the strongest supporting context.` : "No active issue signals remain in this snapshot; the finding may clear on the next health refresh."}</p>
        </div>
      </div>
      <div className="diagnostic-grid">
        {visibleDiagnostics.map(row => <div className={`diagnostic-tile ${row.status}`} key={`${row.label}-${row.value}`}>
          <span>{row.label}</span>
          <strong>{row.value}</strong>
          <p>{row.detail}</p>
        </div>)}
      </div>
      {runbook.diagnostics.length > visibleDiagnostics.length && <details className="runbook-all-diagnostics">
        <summary>Show all diagnostic context ({runbook.diagnostics.length})</summary>
        <div className="diagnostic-grid">
          {runbook.diagnostics.map(row => <div className={`diagnostic-tile ${row.status}`} key={`all-${row.label}-${row.value}`}>
            <span>{row.label}</span>
            <strong>{row.value}</strong>
            <p>{row.detail}</p>
          </div>)}
        </div>
      </details>}
    </div>}
    {runbook.talkgroupCandidates?.length > 0 && <div className="runbook-workbench">
      <div className="recommendation-head">
        <div>
          <h3>Talkgroup Noise Candidates</h3>
          <p className="muted">Ranked by recent volume, weak-call rate, transcription failures, repetition hints, pending load, category, and incident yield.</p>
        </div>
        <button type="button" className="danger-button" disabled={busy} onClick={() => void onExcludeTalkgroups(runbook.talkgroupCandidates.map(row => ({ systemShortName: row.systemShortName, talkgroup: row.talkgroup })))}>Exclude all candidates</button>
      </div>
      <table className="table runbook-tg-table">
        <thead><tr><th>Talkgroup</th><th>Score</th><th>Recent Load</th><th>Quality</th><th>Incident Yield</th><th>Reason</th><th>Action</th></tr></thead>
        <tbody>{runbook.talkgroupCandidates.map(row => <tr key={`${row.systemShortName}-${row.talkgroup}`}>
          <td>{row.talkgroupName || `TG ${row.talkgroup}`}<br /><span className="muted">{row.systemShortName} / {row.talkgroup}{row.alreadyDeferred ? " / already deferred" : ""}</span></td>
          <td><strong>{(row.score ?? 0).toFixed(0)}</strong><br /><span className={`category-chip category-${row.category}`}>{label(row.category)}</span></td>
          <td>{formatDurationMinutes(row.audioSeconds / 60)}<br /><span className="muted">{row.calls.toLocaleString()} calls, {row.averageAudioSeconds.toFixed(1)}s avg</span>{row.pendingCalls > 0 && <><br /><span className="muted">{row.pendingCalls.toLocaleString()} pending / {formatDurationMinutes(row.pendingAudioSeconds / 60)}</span></>}</td>
          <td>{(row.weakPct ?? 0).toFixed(0)}% weak<br /><span className="muted">{(row.failedPct ?? 0).toFixed(0)}% failed, {(row.repetitivePct ?? 0).toFixed(0)}% repetitive</span></td>
          <td>{(row.incidentYieldPct ?? 0).toFixed(0)}%<br /><span className="muted">{(row.incidentCalls ?? 0).toLocaleString()} incident calls</span></td>
          <td>{row.reason}</td>
          <td>
            <div className="runbook-row-actions">
              <button type="button" className="danger-button" disabled={busy} onClick={() => void onExcludeTalkgroups([{ systemShortName: row.systemShortName, talkgroup: row.talkgroup }])}>Exclude</button>
              <button type="button" disabled={busy} onClick={() => {
                localStorage.setItem("pizzawave-setup-talkgroup-filter", String(row.talkgroup));
                onOpen({ topTab: "setup", subTab: "talkgroups", anchor: String(row.talkgroup) });
              }}>Open TG</button>
            </div>
          </td>
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
      <button disabled={busy} onClick={() => void onState(item.id, "ignore")}>Ignore</button>
    </div>
  </div>;
}

function BackupRestorePanel({ reload }: { reload: () => Promise<void> }) {
  const [rows, setRows] = useState<BackupArchive[]>([]);
  const [estimate, setEstimate] = useState<BackupEstimate | null>(null);
  const [audioWindow, setAudioWindow] = useState("7d");
  const [busy, setBusy] = useState("");
  const [message, setMessage] = useState("");
  const [backupJob, setBackupJob] = useState<Job | null>(null);
  const [preview, setPreview] = useState<BackupRestorePreview | null>(null);
  const [showHelp, setShowHelp] = useState(false);
  const [resetPresets, setResetPresets] = useState<string[]>(["data-only"]);
  const [resetCreateBackup, setResetCreateBackup] = useState(true);
  const [resetPreserveAudit, setResetPreserveAudit] = useState(true);
  const fileRef = useRef<HTMLInputElement | null>(null);
  const handledBackupJobId = useRef<number | null>(null);
  useEffect(() => { void loadBackups(); }, []);
  useEffect(() => { void loadBackupEstimate(); }, [audioWindow]);
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
    try {
      const [backupRows, backupEstimate] = await Promise.all([api.request<BackupArchive[]>("/api/v1/system/backups"), loadBackupEstimateValue()]);
      setRows(backupRows);
      setEstimate(backupEstimate);
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Unable to load backups.");
    }
  }

  async function loadBackupEstimateValue() {
    return api.request<BackupEstimate>(`/api/v1/system/backups/estimate?audioWindow=${encodeURIComponent(audioWindow)}`);
  }

  async function loadBackupEstimate() {
    try {
      setEstimate(await loadBackupEstimateValue());
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Unable to estimate backup size.");
    }
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

  function toggleResetPreset(id: string, checked: boolean) {
    setResetPresets(current => checked
      ? Array.from(new Set([...current, id]))
      : current.filter(value => value !== id));
  }

  function resetPresetLabel(id: string) {
    if (id === "data-only") return "Data Only";
    if (id === "site-reset") return "Site Reset";
    if (id === "full-reset") return "Full Reset";
    if (id === "custom") return "Custom";
    return label(id);
  }

  async function runReset() {
    if (resetPresets.length === 0) {
      setMessage("Choose at least one reset preset.");
      return;
    }
    const names = resetPresets.map(preset => resetPresetLabel(preset)).join(", ");
    const destructive = resetPresets.includes("site-reset") || resetPresets.includes("full-reset");
    if (!confirmAction("Run reset?", `${names} will ${destructive ? "stop ingest and clear site/operational state according to the selected presets" : "clear historical operating data while preserving current setup"}. ${resetCreateBackup ? "A backup will be created first." : "No backup will be created first."} Continue?`)) return;
    setBusy("reset");
    setMessage("Running reset...");
    try {
      const result = await api.request<SystemResetResult>("/api/v1/system/reset", {
        method: "POST",
        body: JSON.stringify({
          presets: resetPresets,
          createBackup: resetCreateBackup,
          backupAudioWindow: audioWindow,
          preserveAuditHistory: resetPreserveAudit
        })
      });
      const backupText = result.backup ? ` Backup: ${result.backup.name}.` : "";
      setMessage(`${result.message}${backupText}${result.warnings.length ? " Warnings: " + result.warnings.join(" ") : ""}`);
      await reload();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Reset failed.");
    } finally {
      setBusy("");
    }
  }

  return <>
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
  <div className="system-manager-grid">
    <div className="card">
      <h3>Backup</h3>
      <p className="muted">Create a portable archive of PizzaWave state for relocation, cloning, or disaster recovery.</p>
      <label className="setting-field compact-setting">
        <span>Recorded audio<small>SQLite and configuration are always backed up fully. This controls recorded call audio by file timestamp.</small></span>
        <select disabled={locked} value={audioWindow} onChange={event => setAudioWindow(event.target.value)}>{audioWindowOptions.map(option => <option key={option.value} value={option.value}>{option.label}</option>)}</select>
      </label>
      <div className="setup-button-row">
        <button disabled={locked} onClick={() => void createBackup()}>{busy === "create" ? "Starting..." : "Create Backup"}</button>
        <button disabled={locked} onClick={() => void loadBackups()}>Refresh</button>
        <button type="button" disabled={locked} onClick={() => setShowHelp(true)}>Help</button>
      </div>
      {estimate && <div className="backup-preview">
        <div className="audit-kpis">
          <Kpi label="Estimated Size" value={formatBytes(estimate.bytes)} status="ok" subtext={`${estimate.fileCount.toLocaleString()} source file(s); archive compression may differ`} />
          <Kpi label="Largest Area" value={estimate.kinds.length ? estimate.kinds.slice().sort((a, b) => b.bytes - a.bytes)[0].kind : "None"} status="ok" subtext={estimate.kinds.length ? formatBytes(estimate.kinds.slice().sort((a, b) => b.bytes - a.bytes)[0].bytes) : "No files found"} />
        </div>
        {estimate.kinds.length > 0 && <table className="table compact-table"><thead><tr><th>Area</th><th>Files</th><th>Source Size</th></tr></thead><tbody>{estimate.kinds.map(kind => <tr key={kind.kind}>
          <td>{kind.kind}</td>
          <td>{kind.fileCount.toLocaleString()}</td>
          <td>{formatBytes(kind.bytes)}</td>
        </tr>)}</tbody></table>}
        {estimate.warnings.length > 0 && <div className="setup-warning-list">{estimate.warnings.map(warning => <div key={warning}>{warning}</div>)}</div>}
      </div>}
    </div>
    {showHelp && <div className="modal-backdrop" onClick={() => setShowHelp(false)}>
      <div className="modal-card" onClick={event => event.stopPropagation()}>
        <div className="recommendation-head">
          <h3>Backup / Restore Guardrails</h3>
          <button onClick={() => setShowHelp(false)}>Close</button>
        </div>
        <div className="maintenance-help">
          <p><strong>Backup scope</strong> is intentionally simple: PizzaWave creates a full backup of the configured database, app data, Qdrant storage, TR config/talkgroups, PizzaWave config, and token when those files exist. Recorded call audio can be limited to a preset recent window.</p>
          <p><strong>Estimated size</strong> is shown before backup creation as a rough source-size total. The final compressed archive can differ.</p>
          <p><strong>Audio window</strong> uses the recorded audio file timestamp. The SQLite database remains a full snapshot so restore has coherent call metadata, incidents, jobs, settings state, and audit history.</p>
          <p><strong>Configuration and setup assets</strong> are included when present: PizzaWave config/token, TR config, generated talkgroups, app data, and Qdrant storage.</p>
          <p><strong>Restore</strong> is staged first and reviewed on this page. Applying restore overwrites backed-up files and may restart services, then PizzaWave returns here with the result.</p>
        </div>
      </div>
    </div>}
    <div className="card">
      <h3>Restore</h3>
      <p className="muted">Stage a backup, review verification, then apply it from this page.</p>
      <input ref={fileRef} disabled={locked} type="file" accept=".zip,application/zip" />
      <div className="setup-button-row">
        <button className="danger-button" disabled={locked} onClick={() => void stageRestore()}>{busy === "restore" ? "Staging..." : "Stage Restore"}</button>
        {preview && <button className="danger-button" disabled={locked || preview.checks.some(check => !check.ok)} onClick={() => void applyRestore()}>{busy === "apply-restore" ? "Applying..." : "Apply Staged Restore"}</button>}
        {preview && <button disabled={locked} onClick={() => void cancelRestore()}>{busy === "cancel-restore" ? "Canceling..." : "Cancel Restore"}</button>}
      </div>
      {preview && <BackupRestorePreviewCard preview={preview} />}
    </div>
    <div className="card">
      <h3>Reset</h3>
      <p className="muted">Choose one or more reset presets, then confirm. Reset is the replacement for the old clean-install and relocation entry points.</p>
      <div className="settings-fields">
        <label className="setup-check option-row">
          <input type="checkbox" disabled={locked} checked={resetPresets.includes("data-only")} onChange={event => toggleResetPreset("data-only", event.currentTarget.checked)} />
          <span><strong>Data Only</strong><span> Clear calls, audio, incidents, transcripts, AI/vector history, jobs, metrics, and recommendations. Current site/config stays in place.</span></span>
        </label>
        <label className="setup-check option-row">
          <input type="checkbox" disabled={locked} checked={resetPresets.includes("site-reset")} onChange={event => toggleResetPreset("site-reset", event.currentTarget.checked)} />
          <span><strong>Site Reset</strong><span> Clear site/location state, TR config, generated TG files/catalog policy, RF evidence/activity, and all historical operating data.</span></span>
        </label>
        <label className="setup-check option-row">
          <input type="checkbox" disabled={locked} checked={resetPresets.includes("full-reset")} onChange={event => toggleResetPreset("full-reset", event.currentTarget.checked)} />
          <span><strong>Full Reset</strong><span> Return to first-run prerequisite mode. Backup archives are preserved.</span></span>
        </label>
        <SettingCheckbox label="Create backup before reset" description={`Uses the Backup audio window currently set to ${audioWindowLabel}.`} checked={resetCreateBackup} onChange={setResetCreateBackup} disabled={locked} />
        <SettingCheckbox label="Preserve audit/setup history for Data Only" description="Site Reset and Full Reset clear setup/RF activity history regardless of this option." checked={resetPreserveAudit} onChange={setResetPreserveAudit} disabled={locked} />
      </div>
      <div className="setup-button-row">
        <button className="danger-button" disabled={locked || resetPresets.length === 0} onClick={() => void runReset()}>{busy === "reset" ? "Resetting..." : "Run Reset"}</button>
      </div>
    </div>
    <div className="card">
      <h3>Available Backups</h3>
      {rows.length === 0 ? <p className="muted">No local backups found.</p> :
        <table className="table compact-table"><thead><tr><th>Name</th><th>Full Path</th><th>Created</th><th>Size</th><th>Action</th></tr></thead><tbody>{rows.map(row => <tr key={row.name}>
          <td>{row.name}</td>
          <td><code>{row.path}</code></td>
          <td>{new Date(row.createdUtc).toLocaleString()}</td>
          <td>{formatBytes(row.bytes)}</td>
          <td><a href={`/api/v1/system/backups/${encodeURIComponent(row.name)}`}>Download</a> <button disabled={locked} onClick={() => void stageLocalRestore(row)}>{busy === `restore-${row.name}` ? "Staging..." : "Restore"}</button> <button className="danger-button" disabled={locked} onClick={() => void deleteBackup(row.name)}>Delete</button></td>
        </tr>)}</tbody></table>}
    </div>
  </div>
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
  </div>;
}

function PizzadStorageManager({ runtime, reload }: { runtime: any | null; reload: () => Promise<void> }) {
  const [busy, setBusy] = useState("");
  const [message, setMessage] = useState("");
  if (!runtime) return <div className="card">Loading storage status...</div>;
  const tables = Object.entries(runtime.tables ?? {}).sort(([a], [b]) => a.localeCompare(b));
  const diskTotal = Number(runtime.storage?.diskTotalBytes || 0);
  const diskFree = Number(runtime.storage?.diskFreeBytes || 0);
  const diskFreePercent = diskTotal > 0 ? diskFree / diskTotal * 100 : 100;
  async function runMaintenance(action: "vacuum" | "analyze" | "prune-jobs" | "recount") {
    if (action === "recount") {
      setBusy(action);
      setMessage("Refreshing storage counts...");
      await reload();
      setMessage("Storage counts refreshed.");
      setBusy("");
      return;
    }
    const descriptions = {
      vacuum: "This rewrites the SQLite database to reclaim space and can make the database busy during the operation.",
      analyze: "This refreshes SQLite planner statistics. It does not delete data.",
      "prune-jobs": "This deletes completed or canceled jobs and job logs older than 30 days. Active jobs are kept."
    } as Record<string, string>;
    if (!confirmAction(`${label(action)}?`, descriptions[action] ?? "Run maintenance action?")) return;
    setBusy(action);
    setMessage(`${label(action)} running...`);
    try {
      const result = await api.request<{ ok: boolean; message: string }>(`/api/v1/system/storage/maintenance/${action}`, { method: "POST" });
      setMessage(result.message);
      await reload();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Maintenance action failed.");
    } finally {
      setBusy("");
    }
  }
  return <div className="system-manager-grid">
    <div className="audit-kpis">
      <Kpi label="Database" value={formatBytes(runtime.storage?.databaseBytes || 0)} status="ok" subtext={runtime.storage?.databasePath || "SQLite WAL store"} />
      <Kpi label="Audio Store" value={formatBytes(runtime.storage?.sampledAudioBytes || 0)} status={runtime.storage?.audioSampleTruncated ? "warning" : "ok"} subtext={`${(runtime.storage?.sampledAudioFiles || 0).toLocaleString()} sampled file(s)${runtime.storage?.audioSampleTruncated ? " (sample capped)" : ""}`} />
      <Kpi label="Disk Free" value={formatBytes(diskFree)} status={diskFreePercent < 10 ? "error" : diskFreePercent < 20 ? "warning" : "ok"} subtext={`${diskFreePercent.toFixed(0)}% free on ${runtime.storage?.diskRoot || "application volume"}`} />
    </div>
    <div className="card">
      <h3>Maintenance</h3>
      <div className="setup-button-row">
        <button disabled={Boolean(busy)} onClick={() => void runMaintenance("vacuum")}>{busy === "vacuum" ? "Vacuuming..." : "Vacuum Database"}</button>
        <button disabled={Boolean(busy)} onClick={() => void runMaintenance("analyze")}>{busy === "analyze" ? "Optimizing..." : "Optimize Statistics"}</button>
        <button disabled={Boolean(busy)} onClick={() => void runMaintenance("prune-jobs")}>{busy === "prune-jobs" ? "Pruning..." : "Prune Old Jobs"}</button>
        <button disabled={Boolean(busy)} onClick={() => void runMaintenance("recount")}>{busy === "recount" ? "Refreshing..." : "Recount Storage"}</button>
      </div>
      <div className="maintenance-help">
        <p><strong>Vacuum Database</strong> rewrites the SQLite file to reclaim free pages. It can briefly make the database busier; use it only during quiet periods.</p>
        <p><strong>Optimize Statistics</strong> refreshes SQLite planner statistics so common queries pick better indexes. It does not delete data.</p>
        <p><strong>Prune Old Jobs</strong> deletes completed/canceled job rows and job logs older than 30 days. Active jobs are kept.</p>
        <p><strong>Recount Storage</strong> reloads this panel's size and table counts. It does not change data.</p>
      </div>
      {message && <span className={message.toLowerCase().includes("fail") || message.toLowerCase().includes("unsupported") ? "section-status error" : "section-status ok"}>{message}</span>}
    </div>
    <div className="card">
      <h3>Database Tables</h3>
      <table className="table compact-table"><thead><tr><th>Table</th><th>Rows</th></tr></thead><tbody>{tables.map(([name, count]) => <tr key={name}><td>{name}</td><td>{Number(count).toLocaleString()}</td></tr>)}</tbody></table>
    </div>
  </div>;
}

function TokenUsagePanel({ report }: { report: TokenUsageReport | null }) {
  const [page, setPage] = useState(1);
  if (!report) return <div className="trouble-panel"><div className="card">Loading token usage...</div></div>;
  const pageSize = 20;
  const totalPages = Math.max(1, Math.ceil(report.entries.length / pageSize));
  const pageSafe = Math.min(page, totalPages);
  const rows = report.entries.slice((pageSafe - 1) * pageSize, pageSafe * pageSize);
  const failures = report.entries.filter(row => !row.success).slice(0, 12);
  const failureKind = (row: TokenUsageReport["entries"][number]) => {
    if (row.success) return "OK";
    if (row.finishReason?.toLowerCase() === "length" || row.error?.toLowerCase().includes("truncat")) return "Truncated";
    if (row.error?.toLowerCase().includes("timeout") || row.error?.toLowerCase().includes("timed out") || row.error?.toLowerCase().includes("request was aborted") || row.error?.toLowerCase().includes("taskcanceled")) return "Timeout";
    if (!row.promptTokens && !row.completionTokens && !row.totalTokens) return "No valid result";
    if (row.error?.toLowerCase().includes("cancel")) return "Canceled";
    return "Error";
  };
  const usageSummary = (title: string, summary: TokenUsageReport["summary"]) =>
    <Kpi label={title} value={formatCompact(summary.totalTokens)} status={summary.timeoutFailures > 0 || summary.noValidResultFailures > 0 || summary.httpOrOtherErrors > 0 ? "error" : summary.truncated > 0 ? "warning" : "ok"} subtext={`${summary.requests.toLocaleString()} request(s), ${summary.timeoutFailures.toLocaleString()} timeout, ${summary.noValidResultFailures.toLocaleString()} no result, ${summary.truncated.toLocaleString()} truncated, est. $${summary.estimatedStandardCost.toFixed(2)}`} />;
  return <div className="trouble-panel token-usage-panel">
    <div className="audit-kpis">
      {usageSummary("Selected Range", report.summary)}
      {usageSummary("This Month", report.monthlySummary)}
      {usageSummary("All Time", report.allTimeSummary)}
      <Kpi label="Prompt Tokens" value={formatCompact(report.summary.promptTokens)} subtext="Input/context tokens" />
      <Kpi label="Completion Tokens" value={formatCompact(report.summary.completionTokens)} subtext="Generated output tokens" />
    </div>
    {report.failuresByKind.length > 0 && <div className="card">
      <h3>AI Failure Classes</h3>
      <table className="table compact-ai-table"><thead><tr><th>Class</th><th>Requests</th><th>Tokens</th><th>Latest</th><th>Example</th></tr></thead><tbody>{report.failuresByKind.map(row => <tr key={row.kind}>
        <td>{label(row.kind)}</td>
        <td>{row.requests.toLocaleString()}</td>
        <td>{formatCompact(row.totalTokens)}</td>
        <td>{new Date(row.latestUtc).toLocaleString()}</td>
        <td>{row.example || row.kind}</td>
      </tr>)}</tbody></table>
    </div>}
    {failures.length > 0 && <div className="card">
      <h3>Recent AI Failures</h3>
      <table className="table compact-ai-table"><thead><tr><th>Time</th><th>Activity</th><th>Class</th><th>Model</th><th>Error</th></tr></thead><tbody>{failures.map(row => <tr key={row.id}>
        <td>{new Date(row.timestampUtc).toLocaleString()}</td>
        <td>{row.triggerActivity}</td>
        <td>{failureKind(row)}</td>
        <td>{row.responseModel || row.requestModel}</td>
        <td>{row.error || row.finishReason || "failed"}</td>
      </tr>)}</tbody></table>
    </div>}
    <div className="tr-chart-grid">
      <TokenBarChart title="Tokens by Day" rows={report.byDay} />
      <TokenBarChart title="Tokens by Activity" rows={report.byTrigger} />
    </div>
    <div className="card">
      <div className="jobs-card-head">
        <h3>Recorded Usage</h3>
        <p>{report.ledger}</p>
        <div className="pagination-row table-top-pagination">
          <button disabled={pageSafe <= 1} onClick={() => setPage(1)}>First</button>
          <button disabled={pageSafe <= 1} onClick={() => setPage(pageSafe - 1)}>Prev</button>
          <span>Page {pageSafe} of {totalPages}</span>
          <button disabled={pageSafe >= totalPages} onClick={() => setPage(pageSafe + 1)}>Next</button>
          <button disabled={pageSafe >= totalPages} onClick={() => setPage(totalPages)}>Last</button>
        </div>
      </div>
      <table className="table jobs-table ai-usage-table"><thead><tr><th>Time</th><th>Activity</th><th>Status</th><th>Model</th><th>Prompt</th><th>Completion</th><th>Total</th><th>Finish</th></tr></thead><tbody>{rows.map(row => <tr key={row.id}>
        <td>{new Date(row.timestampUtc).toLocaleString()}</td>
        <td>{row.triggerActivity}</td>
        <td>{failureKind(row)}</td>
        <td>{row.responseModel || row.requestModel}</td>
        <td>{row.promptTokens.toLocaleString()}</td>
        <td>{row.completionTokens.toLocaleString()}</td>
        <td>{(row.totalTokens || row.promptTokens + row.completionTokens).toLocaleString()}</td>
        <td>{row.finishReason}</td>
      </tr>)}</tbody></table>
    </div>
  </div>;
}

function RemoteBandwidthPanel({ report }: { report: RemoteBandwidthReport | null }) {
  const [page, setPage] = useState(1);
  if (!report) return <div className="trouble-panel"><div className="card">Loading bandwidth usage...</div></div>;
  const pageSize = 20;
  const totalPages = Math.max(1, Math.ceil(report.entries.length / pageSize));
  const pageSafe = Math.min(page, totalPages);
  const rows = report.entries.slice((pageSafe - 1) * pageSize, pageSafe * pageSize);
  const usageSummary = (title: string, summary: RemoteBandwidthReport["summary"]) =>
    <Kpi label={title} value={formatBytes(summary.totalBytes)} status={summary.missingAudioFiles > 0 ? "warning" : "ok"} subtext={`${summary.requests.toLocaleString()} request(s), ${formatBytes(summary.requestBytes)} up, ${formatBytes(summary.responseBytes)} down`} />;

  return <div className="trouble-panel token-usage-panel">
    <div className="audit-kpis">
      {usageSummary("Selected Range", report.summary)}
      {usageSummary("This Month", report.monthlySummary)}
      {usageSummary("All Time", report.allTimeSummary)}
      <Kpi label="Remote Host" value={report.remoteHost || "None"} status={report.remoteHost ? "ok" : "warning"} subtext={report.aiEndpoint || report.transcriptionEndpoint || "No remote endpoint configured"} />
      <Kpi label="Transcription" value={report.transcriptionIncluded ? "Included" : "Skipped"} status={report.transcriptionIncluded ? "ok" : "neutral"} subtext={report.transcriptionEndpoint || "No transcription endpoint"} />
    </div>
    <div className="card">
      <h3>Estimator Notes</h3>
      <p>{report.notes}</p>
      <p className="muted">{report.ledger}</p>
    </div>
    <div className="tr-chart-grid">
      <BandwidthBarChart title="Bytes by Day" rows={report.byDay} />
      <BandwidthBarChart title="Bytes by Activity" rows={report.byActivity} />
    </div>
    <div className="card">
      <div className="jobs-card-head">
        <h3>Estimated Remote Traffic</h3>
        <p>{report.remoteHost || "Remote host not configured"}</p>
        <div className="pagination-row table-top-pagination">
          <button disabled={pageSafe <= 1} onClick={() => setPage(1)}>First</button>
          <button disabled={pageSafe <= 1} onClick={() => setPage(pageSafe - 1)}>Prev</button>
          <span>Page {pageSafe} of {totalPages}</span>
          <button disabled={pageSafe >= totalPages} onClick={() => setPage(pageSafe + 1)}>Next</button>
          <button disabled={pageSafe >= totalPages} onClick={() => setPage(totalPages)}>Last</button>
        </div>
      </div>
      <table className="table jobs-table ai-usage-table"><thead><tr><th>Time</th><th>Activity</th><th>Upload</th><th>Download</th><th>Total</th><th>Basis</th></tr></thead><tbody>{rows.map((row, index) => <tr key={`${row.timestampUtc}-${row.activity}-${index}`}>
        <td>{new Date(row.timestampUtc).toLocaleString()}</td>
        <td>{row.activity}</td>
        <td>{formatBytes(row.requestBytes)}</td>
        <td>{formatBytes(row.responseBytes)}</td>
        <td>{formatBytes(row.totalBytes)}</td>
        <td>{row.basis}</td>
      </tr>)}</tbody></table>
    </div>
  </div>;
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
  const [page, setPage] = useState(1);
  const pageSize = 12;
  const totalPages = Math.max(1, Math.ceil(jobs.length / pageSize));
  const pageSafe = Math.min(page, totalPages);
  const pageJobs = jobs.slice((pageSafe - 1) * pageSize, pageSafe * pageSize);

  useEffect(() => {
    setPage(current => Math.min(current, totalPages));
  }, [totalPages]);

  async function control(id: number, action: string) {
    if (["pause", "cancel"].includes(action) && !confirmAction(`${label(action)} job ${id}?`, "This changes a running or queued background job.")) return;
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
    if (!confirmAction(`Delete job ${id}?`, "This removes the finished job row and its logs from the local database.")) return;
    setMessage(`Deleting job ${id}...`);
    try {
      await api.request(`/api/v1/jobs/${id}`, { method: "DELETE" });
      setMessage(`Deleted job ${id}`);
      await reload();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : String(error));
    }
  }

  return <div className="trouble-panel jobs-panel">
    <div className="card jobs-card">
        <div className="jobs-card-head">
          <h3>Jobs</h3>
          <p>Background work created by service restarts, maintenance, diagnostics, and summary generation.</p>
          <span className="muted">{jobs.length.toLocaleString()} total</span>
          {message && <span className={message.toLowerCase().includes("fail") || message.toLowerCase().includes("error") ? "section-status error" : "section-status ok"}>{message}</span>}
          <div className="pagination-row table-top-pagination">
            <button disabled={pageSafe <= 1} onClick={() => setPage(1)}>First</button>
            <button disabled={pageSafe <= 1} onClick={() => setPage(pageSafe - 1)}>Prev</button>
            <span>Page {pageSafe} of {totalPages}</span>
            <button disabled={pageSafe >= totalPages} onClick={() => setPage(pageSafe + 1)}>Next</button>
            <button disabled={pageSafe >= totalPages} onClick={() => setPage(totalPages)}>Last</button>
          </div>
        </div>
        <div className="jobs-table-wrap">
          <JobsTable jobs={pageJobs} onControl={control} onDelete={deleteJob} />
        </div>
    </div>
  </div>;
}

function QueuePanel({ engineHealth, ingestBusy, ingestMessage, onSetIngestPaused }: { engineHealth: EngineHealth | null; ingestBusy: boolean; ingestMessage: string; onSetIngestPaused: (pause: boolean, untilQueueClear?: boolean) => Promise<void> }) {
  const [queue, setQueue] = useState<QueueSnapshot | null>(null);
  const [queueError, setQueueError] = useState("");
  const [detail, setDetail] = useState<QueueDetailKey>("status");
  const [issuePage, setIssuePage] = useState(1);
  const showDetail = (next: QueueDetailKey) => {
    setDetail(next);
    setIssuePage(1);
  };
  useEffect(() => {
    let canceled = false;
    api.request<QueueSnapshot>("/api/v1/system/queue")
      .then(snapshot => { if (!canceled) { setQueue(snapshot); setQueueError(""); } })
      .catch(error => { if (!canceled) setQueueError(error instanceof Error ? error.message : "Queue load failed"); });
    return () => { canceled = true; };
  }, [engineHealth?.serverTimeUtc]);
  const q = queue;
  const depth = q?.queueDepth ?? engineHealth?.queueDepth ?? 0;
  const pending = q?.pendingTranscriptions ?? engineHealth?.pendingTranscriptions ?? 0;
  const live = q?.liveQueueDepth ?? engineHealth?.liveQueueDepth ?? 0;
  const priority = q?.priorityLiveQueueDepth ?? engineHealth?.priorityLiveQueueDepth ?? 0;
  const backlog = q?.backlogQueueDepth ?? engineHealth?.backlogQueueDepth ?? 0;
  const transcribed = q?.recentTranscribedPerMinute ?? engineHealth?.recentTranscribedPerMinute ?? 0;
  const ingested = q?.recentIngestPerMinute ?? engineHealth?.recentIngestPerMinute ?? 0;
  const recentCallsIngested = q?.recentCallsIngested ?? engineHealth?.recentCallsIngested ?? 0;
  const recentCallsTranscribed = q?.recentCallsTranscribed ?? engineHealth?.recentCallsTranscribed ?? 0;
  const recentAudioSecondsIngested = q?.recentAudioSecondsIngested ?? engineHealth?.recentAudioSecondsIngested ?? 0;
  const recentAudioSecondsTranscribed = q?.recentAudioSecondsTranscribed ?? engineHealth?.recentAudioSecondsTranscribed ?? 0;
  const audioIn = q?.recentAudioSecondsIngestedPerMinute ?? engineHealth?.recentAudioSecondsIngestedPerMinute ?? 0;
  const audioOut = q?.recentAudioSecondsTranscribedPerMinute ?? engineHealth?.recentAudioSecondsTranscribedPerMinute ?? 0;
  const throughputWindowMinutes = q?.throughputWindowMinutes ?? engineHealth?.throughputWindowMinutes ?? 10;
  const pendingAudioSeconds = q?.pendingAudioSeconds ?? engineHealth?.pendingAudioSeconds ?? 0;
  const aiCompletionHealth = q?.aiCompletionHealth ?? engineHealth?.aiCompletionHealth;
  const aiCompletionIssue = aiCompletionHealth && !["ok", "unknown"].includes(aiCompletionHealth.status) ? aiCompletionHealth.message : "";
  const embeddingHealth = q?.embeddingHealth ?? engineHealth?.embeddingHealth;
  const embeddingIssue = embeddingHealth?.enabled && !["ok", "disabled", "unknown"].includes(embeddingHealth.status)
    ? (embeddingHealth.lastError || (embeddingHealth.embeddingEndpointOk ? "Embedding pipeline health is degraded." : "Embedding endpoint health check failed."))
    : "";
  const queueState = aiCompletionIssue || embeddingIssue ? "Blocked" : depth <= 0 ? "OK" : q?.queueUnderPressure || engineHealth?.queueUnderPressure ? "Pressure" : audioOut >= audioIn ? "Draining" : "Growing";
  const queueStatus = queueState === "OK" || queueState === "Draining" ? "ok" : queueState === "Growing" ? "warning" : "error";
  const liveTrActivity = engineHealth?.liveTrActivity;
  const etaMinutes = pendingAudioSeconds > 0 && audioOut > audioIn ? pendingAudioSeconds / Math.max(0.1, audioOut - audioIn) : 0;
  const ingest = q?.ingest ?? engineHealth?.ingest;
  const ingestStatus = liveTrActivity?.stale || ingest?.paused ? "error" : recentCallsIngested > 0 ? "ok" : "neutral";
  const ingestSubtext = `${recentCallsIngested.toLocaleString()} call${recentCallsIngested === 1 ? "" : "s"} / ${recentAudioSecondsIngested.toLocaleString()} audio seconds over ${throughputWindowMinutes}m`;
  const blockerRows: QueueIssueRow[] = [
    q?.aiWorkBlockedReason ?? engineHealth?.aiWorkBlockedReason
      ? { severity: "Blocker", source: "AI work", message: q?.aiWorkBlockedReason ?? engineHealth?.aiWorkBlockedReason ?? "", status: "error", details: ["status", "ai"] }
      : null,
    aiCompletionIssue
      ? { severity: "Blocker", source: "AI completions", message: aiCompletionIssue, status: "error", details: ["status", "ai"] }
      : null,
    embeddingIssue
      ? { severity: "Blocker", source: "Embedding link", message: embeddingIssue, status: "error", details: ["status", "embedding"] }
      : null
  ].filter((row): row is QueueIssueRow => Boolean(row));
  const warningRows: QueueIssueRow[] = [
    q?.queueUnderPressure || engineHealth?.queueUnderPressure
      ? { severity: "Warning", source: "Queue depth", message: `Queue depth is above the pressure threshold of ${(q?.queuePressureThreshold ?? engineHealth?.queuePressureThreshold ?? 0).toLocaleString()}.`, status: "warning", details: ["status", "queued", "composition"] }
      : null,
    depth > 0 && audioOut < audioIn
      ? { severity: "Warning", source: "Throughput", message: "Recent audio ingest is faster than transcription throughput.", status: "warning", details: ["status", "throughput"] }
      : null,
    ingest?.paused
      ? { severity: "Warning", source: "Live ingest", message: `Live ingest is paused${ingest.untilQueueClear ? " until the queue clears" : ""}.`, status: "warning", details: ["status", "queued"] }
      : null,
    liveTrActivity?.stale
      ? { severity: "Warning", source: "Live TR activity", message: liveTrActivity.message, status: "warning", details: ["status"] }
      : null,
    aiCompletionHealth?.status === "ok" && aiCompletionHealth.failures > 0
      ? { severity: "Warning", source: "AI completions", message: aiCompletionHealth.message, status: "warning", details: ["status", "ai"] }
      : null,
    embeddingHealth?.enabled && embeddingHealth.failedCalls > 0
      ? { severity: "Warning", source: "Embedding history", message: `${embeddingHealth.failedCalls.toLocaleString()} embedding job(s) have failed historically.`, status: "warning", details: ["status", "embedding"] }
      : null
  ].filter((row): row is QueueIssueRow => Boolean(row));
  const issueRows = [...blockerRows, ...warningRows];
  const relatedIssueRows = queueIssuesForDetail(detail, issueRows);
  const blockers = blockerRows.map(row => row.message);
  const warnings = warningRows.map(row => row.message);
  const latestFailure = aiCompletionHealth?.latestFailureUtc ? `${formatJobDate(aiCompletionHealth.latestFailureUtc)} ${aiCompletionHealth.latestFailureKind ? `(${aiCompletionHealth.latestFailureKind})` : ""}` : "None";
  const details = queueDetailRows(detail, {
    queueState,
    blockers,
    warnings,
    depth,
    pending,
    pendingAudioSeconds,
    live,
    priority,
    backlog,
    audioIn,
    audioOut,
    ingested,
    transcribed,
    etaMinutes,
    aiCompletionHealth,
    latestFailure,
    embeddingHealth,
    ingest,
    queue: q,
    engineHealth
  });
  return <div className="queue-jobs-layout">
    <div className="card queue-card">
      <div className="jobs-card-head">
        <h3>Transcription Queue</h3>
        <span className={`job-status status-${queueState === "Pressure" || queueState === "Growing" || queueState === "Blocked" ? "failed" : queueState === "Draining" ? "running" : "completed"}`}>{queueState}</span>
        {queueError && <span className="section-status error">{queueError}</span>}
      </div>
      <div className="audit-kpis queue-kpis">
        <Kpi label="Queued" value={depth.toLocaleString()} status={queueStatus} subtext={`${pending.toLocaleString()} pending in database`} onClick={() => showDetail("queued")} />
        <Kpi label="Pending Audio" value={formatDurationMinutes(pendingAudioSeconds / 60)} status={queueStatus} subtext={`${pendingAudioSeconds.toLocaleString()} audio seconds queued`} onClick={() => showDetail("queued")} />
        <Kpi label="Composition" value={`${live.toLocaleString()} live`} subtext={`${priority.toLocaleString()} priority, ${backlog.toLocaleString()} backlog`} onClick={() => showDetail("composition")} />
        <Kpi label="Live Ingest" value={`${ingested.toFixed(1)}/min`} status={ingestStatus} subtext={ingestSubtext} onClick={() => showDetail("status")} />
        <Kpi label="Audio Throughput" value={`${audioOut.toFixed(0)}s/min`} status={depth > 0 && audioOut < audioIn ? "warning" : "ok"} subtext={`${audioIn.toFixed(0)}s/min in over ${q?.throughputWindowMinutes ?? engineHealth?.throughputWindowMinutes ?? 10}m`} onClick={() => showDetail("throughput")} />
        <Kpi label="Call Throughput" value={`${transcribed.toFixed(1)}/min`} subtext={`${ingested.toFixed(1)}/min in; useful but duration-blind`} onClick={() => showDetail("throughput")} />
        <Kpi label="AI Completions" value={aiCompletionHealth ? label(aiCompletionHealth.status) : "Unknown"} status={aiCompletionHealth?.status === "error" ? "error" : aiCompletionHealth?.status === "warning" ? "warning" : aiCompletionHealth?.status === "ok" ? "ok" : "neutral"} subtext={aiCompletionHealth ? `${aiCompletionHealth.timeoutFailures.toLocaleString()} timeout, ${aiCompletionHealth.noValidResultFailures.toLocaleString()} no result over ${aiCompletionHealth.windowMinutes}m` : "No AI health sample"} onClick={() => showDetail("ai")} />
        <Kpi label="Embedding Link" value={embeddingHealth ? label(embeddingHealth.status) : "Unknown"} status={embeddingHealth?.status === "degraded" ? "error" : embeddingHealth?.status === "ok" ? "ok" : embeddingHealth?.status === "disabled" ? "neutral" : "warning"} subtext={embeddingHealth ? `${embeddingHealth.embeddingEndpointOk ? "endpoint OK" : "endpoint issue"}, ${embeddingHealth.qdrantOk ? "Qdrant OK" : "Qdrant issue"}` : "No embedding health sample"} onClick={() => showDetail("embedding")} />
        <Kpi label="Latency" value={`${Number(q?.averageTranscriptionSeconds ?? engineHealth?.averageTranscriptionSeconds ?? 0).toFixed(1)}s`} subtext={`${Number(q?.averageAudioSeconds ?? engineHealth?.averageAudioSeconds ?? 0).toFixed(1)}s avg audio`} onClick={() => showDetail("workers")} />
        <Kpi label="Workers" value={`${q?.liveTranscriptionWorkers ?? engineHealth?.liveTranscriptionWorkers ?? 0} x ${q?.whisperThreadsPerWorker ?? engineHealth?.whisperThreadsPerWorker ?? 0}`} subtext="workers x threads" onClick={() => showDetail("workers")} />
        <Kpi label="ETA" value={etaMinutes > 0 ? formatDurationMinutes(etaMinutes) : depth > 0 ? "Unknown" : "Clear"} subtext={depth > 0 && audioOut <= audioIn ? "Audio queue is not currently outrunning ingest" : "Based on recent net audio drain"} onClick={() => showDetail("throughput")} />
      </div>
      <div className="queue-detail-panel">
        <div className="queue-detail-head">
          <strong>{queueDetailTitle(detail)}</strong>
          <span className={blockers.length > 0 ? "section-status error" : warnings.length > 0 ? "section-status warning" : "section-status ok"}>
            {blockers.length > 0 ? `${blockers.length} blocker${blockers.length === 1 ? "" : "s"}` : warnings.length > 0 ? `${warnings.length} warning${warnings.length === 1 ? "" : "s"}` : "Normal"}
          </span>
        </div>
        <div className="queue-detail-rows">
          {details.map((row, i) => <div className={`queue-detail-row ${row.status ? `queue-detail-${row.status}` : ""}`} key={`${row.label}-${i}`}>
            <span>{row.label}</span>
            <strong>{row.value}</strong>
          </div>)}
        </div>
      </div>
      <QueueIssueTable rows={relatedIssueRows} detail={detail} page={issuePage} setPage={setIssuePage} />
      <div className="system-action-bar">
        <strong>Live Ingest</strong>
        {ingest?.paused
          ? <button disabled={ingestBusy} onClick={() => void onSetIngestPaused(false)}>{ingestBusy ? "Updating..." : "Resume Live Ingest"}</button>
          : <>
            <button className="danger-button" disabled={ingestBusy} onClick={() => void onSetIngestPaused(true, true)}>{ingestBusy ? "Updating..." : "Pause Until Queue Clear"}</button>
            <button className="danger-button" disabled={ingestBusy} onClick={() => void onSetIngestPaused(true, false)}>Pause Live Ingest</button>
          </>}
        <span className={ingest?.paused ? "section-status error" : "section-status ok"}>{ingest?.paused ? "Paused" : "Running"}</span>
        {ingestMessage && <span className={ingestMessage.toLowerCase().includes("fail") ? "settings-message error" : "settings-message ok"}>{ingestMessage}</span>}
      </div>
      {ingest?.paused && <div className="settings-message error">Live ingest is paused{ingest.untilQueueClear ? " until the queue clears" : ""}. Dropped this pause: {(ingest.droppedCallsThisPause ?? 0).toLocaleString()} ({ingest.droppedCalls.toLocaleString()} total since service start).</div>}
    </div>
  </div>;
}

type QueueDetailKey = "status" | "queued" | "composition" | "throughput" | "ai" | "embedding" | "workers";
type QueueDetailRow = { label: string; value: string; status?: "ok" | "warning" | "error" };
type QueueIssueRow = { severity: "Blocker" | "Warning"; source: string; message: string; status: "warning" | "error"; details: QueueDetailKey[] };

function QueueIssueTable({ rows, detail, page, setPage }: { rows: QueueIssueRow[]; detail: QueueDetailKey; page: number; setPage: (page: number) => void }) {
  const pageSize = 5;
  const totalPages = Math.max(1, Math.ceil(rows.length / pageSize));
  const pageSafe = Math.min(Math.max(1, page), totalPages);
  const visibleRows = rows.slice((pageSafe - 1) * pageSize, pageSafe * pageSize);
  const startRow = rows.length === 0 ? 0 : (pageSafe - 1) * pageSize + 1;
  const endRow = Math.min(rows.length, pageSafe * pageSize);
  return <div className="queue-issue-panel">
    <div className="queue-detail-head">
      <strong>{detail === "status" ? "All Queue Warnings And Blockers" : `${queueDetailTitle(detail)} Warnings And Blockers`}</strong>
      <span className={rows.some(row => row.status === "error") ? "section-status error" : rows.length ? "section-status warning" : "section-status ok"}>
        {rows.length ? `${rows.length} active` : "None"}
      </span>
    </div>
    {rows.length > pageSize && <div className="pagination-row table-top-pagination">
      <button disabled={pageSafe <= 1} onClick={() => setPage(1)}>First</button>
      <button disabled={pageSafe <= 1} onClick={() => setPage(pageSafe - 1)}>Prev</button>
      <span>{startRow}-{endRow} of {rows.length}</span>
      <button disabled={pageSafe >= totalPages} onClick={() => setPage(pageSafe + 1)}>Next</button>
      <button disabled={pageSafe >= totalPages} onClick={() => setPage(totalPages)}>Last</button>
    </div>}
    <table className="table queue-issue-table">
      <thead>
        <tr><th>Severity</th><th>Source</th><th>Current condition</th></tr>
      </thead>
      <tbody>
        {visibleRows.length
          ? visibleRows.map((row, i) => <tr className={`queue-issue-${row.status}`} key={`${row.source}-${startRow + i}`}>
            <td><span className={`section-status ${row.status}`}>{row.severity}</span></td>
            <td>{row.source}</td>
            <td>{row.message}</td>
          </tr>)
          : <tr className="queue-issue-ok"><td><span className="section-status ok">Normal</span></td><td>{queueDetailTitle(detail)}</td><td>No active warnings or blockers for this view.</td></tr>}
      </tbody>
    </table>
  </div>;
}

function queueIssuesForDetail(detail: QueueDetailKey, rows: QueueIssueRow[]) {
  return rows.filter(row => row.details.includes(detail));
}

function queueDetailTitle(detail: QueueDetailKey) {
  return detail === "queued" ? "Queued Work"
    : detail === "composition" ? "Queue Composition"
      : detail === "throughput" ? "Throughput"
        : detail === "ai" ? "AI Completion Health"
          : detail === "embedding" ? "Embedding Health"
            : detail === "workers" ? "Workers And Latency"
              : "Current Status";
}

function queueDetailRows(detail: QueueDetailKey, data: {
  queueState: string;
  blockers: string[];
  warnings: string[];
  depth: number;
  pending: number;
  pendingAudioSeconds: number;
  live: number;
  priority: number;
  backlog: number;
  audioIn: number;
  audioOut: number;
  ingested: number;
  transcribed: number;
  etaMinutes: number;
  aiCompletionHealth?: EngineHealth["aiCompletionHealth"];
  latestFailure: string;
  embeddingHealth?: EngineHealth["embeddingHealth"];
  ingest?: EngineHealth["ingest"];
  queue: QueueSnapshot | null;
  engineHealth: EngineHealth | null;
}): QueueDetailRow[] {
  const status = data.blockers.length > 0 ? "error" : data.warnings.length > 0 ? "warning" : "ok";
  const common: QueueDetailRow[] = [
    { label: "Queue state", value: data.queueState, status },
    { label: "Blockers", value: data.blockers.length ? data.blockers.join(" ") : "None", status: data.blockers.length ? "error" : "ok" },
    { label: "Warnings", value: data.warnings.length ? data.warnings.join(" ") : "None", status: data.warnings.length ? "warning" : "ok" }
  ];
  if (detail === "queued") {
    const pendingCalls = data.queue?.pendingCalls ?? [];
    return [
      { label: "Queued calls", value: data.depth.toLocaleString(), status },
      { label: "Pending transcriptions", value: data.pending.toLocaleString(), status },
      { label: "Pending audio", value: `${data.pendingAudioSeconds.toLocaleString()} seconds` },
      { label: "Oldest pending call", value: pendingCalls[0] ? `${pendingCalls[0].systemShortName} ${pendingCalls[0].callId}` : "None", status: pendingCalls.length ? "warning" : "ok" },
      ...common
    ];
  }
  if (detail === "composition") {
    const topTalkgroups = (data.queue?.topAudioTalkgroups ?? []).slice(0, 3).map(row => `${row.systemShortName} ${row.talkgroupName}: ${row.pendingCalls} pending`).join("; ");
    return [
      { label: "Live queue", value: data.live.toLocaleString() },
      { label: "Priority live queue", value: data.priority.toLocaleString() },
      { label: "Backlog queue", value: data.backlog.toLocaleString() },
      { label: "Top pending talkgroups", value: topTalkgroups || "None" },
      ...common
    ];
  }
  if (detail === "throughput") {
    return [
      { label: "Audio in", value: `${data.audioIn.toFixed(1)} seconds per minute` },
      { label: "Audio out", value: `${data.audioOut.toFixed(1)} seconds per minute`, status: data.depth > 0 && data.audioOut < data.audioIn ? "warning" : "ok" },
      { label: "Calls in", value: `${data.ingested.toFixed(1)} per minute` },
      { label: "Calls out", value: `${data.transcribed.toFixed(1)} per minute` },
      { label: "Estimated clear time", value: data.etaMinutes > 0 ? formatDurationMinutes(data.etaMinutes) : data.depth > 0 ? "Unknown" : "Clear" },
      ...common
    ];
  }
  if (detail === "ai") {
    const ai = data.aiCompletionHealth;
    return [
      { label: "Status", value: ai ? label(ai.status) : "Unknown", status: ai?.status === "error" ? "error" : ai?.status === "warning" ? "warning" : ai?.status === "ok" ? "ok" : undefined },
      { label: "Message", value: ai?.message ?? "No AI health sample" },
      { label: "Requests", value: ai ? ai.requests.toLocaleString() : "0" },
      { label: "Failures", value: ai ? ai.failures.toLocaleString() : "0", status: ai && ai.failures > 0 ? "warning" : "ok" },
      { label: "Consecutive failures", value: ai ? ai.consecutiveFailures.toLocaleString() : "0", status: ai && ai.consecutiveFailures > 0 ? "error" : "ok" },
      { label: "Latest failure", value: data.latestFailure, status: ai?.latestFailureUtc ? "warning" : "ok" },
      { label: "Latest failure detail", value: ai?.latestFailure || "None" }
    ];
  }
  if (detail === "embedding") {
    const embedding = data.embeddingHealth;
    return [
      { label: "Status", value: embedding ? label(embedding.status) : "Unknown", status: embedding?.status === "degraded" ? "error" : embedding?.status === "ok" ? "ok" : undefined },
      { label: "Endpoint", value: embedding ? embedding.embeddingEndpointOk ? "OK" : "Issue" : "Unknown", status: embedding?.embeddingEndpointOk ? "ok" : "error" },
      { label: "Qdrant", value: embedding ? embedding.qdrantOk ? "OK" : "Issue" : "Unknown", status: embedding?.qdrantOk ? "ok" : "error" },
      { label: "Queue depth", value: embedding ? embedding.queueDepth.toLocaleString() : "0" },
      { label: "Pending calls", value: embedding ? embedding.pendingCalls.toLocaleString() : "0" },
      { label: "Last error", value: embedding?.lastError || "None" }
    ];
  }
  if (detail === "workers") {
    return [
      { label: "Live workers", value: `${data.queue?.liveTranscriptionWorkers ?? data.engineHealth?.liveTranscriptionWorkers ?? 0}` },
      { label: "Threads per worker", value: `${data.queue?.whisperThreadsPerWorker ?? data.engineHealth?.whisperThreadsPerWorker ?? 0}` },
      { label: "Average transcription time", value: `${Number(data.queue?.averageTranscriptionSeconds ?? data.engineHealth?.averageTranscriptionSeconds ?? 0).toFixed(2)} seconds` },
      { label: "Average audio length", value: `${Number(data.queue?.averageAudioSeconds ?? data.engineHealth?.averageAudioSeconds ?? 0).toFixed(2)} seconds` },
      { label: "Realtime factor", value: `${Number(data.queue?.averageTranscriptionRealtimeFactor ?? data.engineHealth?.averageTranscriptionRealtimeFactor ?? 0).toFixed(3)}` },
      ...common
    ];
  }
  return common;
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

function TrConfigRestorePanel() {
  const [rows, setRows] = useState<TrConfigBackup[]>([]);
  const [busy, setBusy] = useState("");
  const [message, setMessage] = useState("");
  useEffect(() => { void loadBackups(); }, []);
  async function loadBackups() {
    setBusy("load");
    try {
      setRows(await api.request<TrConfigBackup[]>("/api/v1/system/tr-config/backups"));
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Unable to load TR config backups.");
    } finally {
      setBusy("");
    }
  }
  async function restore(row: TrConfigBackup) {
    if (!confirmAction("Restore TR config backup?", `This installs ${row.name}, creates a backup of the current TR config first, and restarts trunk-recorder.`)) return;
    setBusy(row.path);
    setMessage("");
    try {
      const result = await api.request<TrConfigRestoreResult>("/api/v1/system/tr-config/restore", {
        method: "POST",
        body: JSON.stringify({ backupPath: row.path, restartTr: true })
      });
      setMessage(`${result.message} ${result.serviceOutput}`.trim());
      await loadBackups();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "TR config restore failed.");
    } finally {
      setBusy("");
    }
  }
  return <div className="card">
    <div className="settings-header">
      <div>
        <h3>Restore TR Config</h3>
        <p className="muted">Timestamped config backups are created before Setup applies source changes and before TR config editor saves.</p>
      </div>
      <button disabled={Boolean(busy)} onClick={() => void loadBackups()}>Refresh</button>
    </div>
    {message && <p className={message.toLowerCase().includes("fail") || message.toLowerCase().includes("unable") ? "settings-message error" : "settings-message ok"}>{message}</p>}
    {rows.length === 0 ? <p className="muted">{busy === "load" ? "Loading backups..." : "No TR config backups found."}</p> :
      <table className="table">
        <thead><tr><th>Backup</th><th>Created</th><th>Size</th><th></th></tr></thead>
        <tbody>{rows.map(row => <tr key={row.path}>
          <td><code>{row.name}</code><br /><span className="muted">{row.path}</span></td>
          <td>{new Date(row.createdAtUtc).toLocaleString()}</td>
          <td>{formatBytes(row.bytes)}</td>
          <td><button className="danger-button" disabled={Boolean(busy)} onClick={() => void restore(row)}>{busy === row.path ? "Restoring..." : "Restore"}</button></td>
        </tr>)}</tbody>
      </table>}
  </div>;
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
    <details className="card"><summary>Raw health samples</summary><table className="table"><thead><tr><th>Window</th><th>Scope</th><th>CC summary</th><th>CC zero</th><th>Msg-rate samples</th><th>Msg-rate zero</th><th>Retunes</th><th>No TX</th><th>Recorder exhausted</th><th>Stops</th></tr></thead><tbody>{data.health.samples.map(r => <tr key={r.id}><td>{new Date(r.windowStartUtc).toLocaleString()}</td><td>{r.scope}</td><td>{r.ccSummaryDecodeLines ? r.ccSummaryAvgDecodeRate.toFixed(2) : "N/A"}</td><td>{r.ccSummaryDecodeLines ? `${r.ccSummaryDecodeZeroPct.toFixed(1)}%` : "N/A"}</td><td>{r.lowDecodeWarningLines.toLocaleString()}</td><td>{r.lowDecodeWarningLines ? `${r.lowDecodeWarningZeroPct.toFixed(1)}%` : "N/A"}</td><td>{r.retunes}</td><td>{r.noTxRecorded}</td><td>{r.recorderExhausted}</td><td>{r.sampleStops}</td></tr>)}</tbody></table></details>
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
    <p className="muted">Built from TR control channels, any configured voice frequencies, and observed call frequencies in the selected range. Ranges use the same 90% sample-rate windowing logic as Setup.</p>
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

function QualityAuditView({ data, rangeHours }: { data: TrTroubleshoot; rangeHours: number }) {
  const audit = data.qualityAudit;
  const [previous, setPrevious] = useState<TrTroubleshoot["qualityAudit"] | null>(null);
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
      <Kpi label="Review Samples" value={audit.samples.length.toLocaleString()} status={audit.samples.length > 0 ? "warning" : "ok"} subtext="problem evidence shown by default" />
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
    <PlayableAudio src={sample.audioUrl} />
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
    aliases
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

function SettingsView({ settingsSections, settingsLoadState, reload, pendingProfileHides, setPendingProfileHides }: { settingsSections: Record<string, any>; settingsLoadState: { loading: boolean; version: number; message: string; error: boolean }; reload: () => Promise<void>; pendingProfileHides: ProfileTalkgroupSetting[]; setPendingProfileHides: React.Dispatch<React.SetStateAction<ProfileTalkgroupSetting[]>> }) {
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
  const [models, setModels] = useState<any[]>([]);
  const [aiModels, setAiModels] = useState<string[]>([]);
  const [aiModelsMessage, setAiModelsMessage] = useState("");
  const [showAiHelp, setShowAiHelp] = useState(false);
  const [modelBusy, setModelBusy] = useState("");

  useEffect(() => {
    const next = withSettingsDefaults(settingsSections);
    setSections(next);
    setBaselineSections(cloneSettings(next));
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
    if (!modelBusy) return;
    const timer = window.setInterval(() => void loadModels(), 1500);
    return () => window.clearInterval(timer);
  }, [modelBusy]);
  useEffect(() => localStorage.setItem("pizzawave-settings-tab", settingsTab), [settingsTab]);
  const dirtySections = useMemo(() => {
    const names = Object.keys(sections).filter(section => section !== "profiles");
    return names.filter(section => JSON.stringify(sections[section] ?? {}) !== JSON.stringify(baselineSections[section] ?? {}));
  }, [sections, baselineSections]);
  const hasUnsavedSectionChanges = dirtySections.length > 0;
  useEffect(() => {
    if (hasUnsavedSectionChanges)
      localStorage.setItem("pizzawave-unapplied-settings", "1");
    else
      localStorage.removeItem("pizzawave-unapplied-settings");
    if (!hasUnsavedSectionChanges) return;
    const handler = (event: BeforeUnloadEvent) => {
      event.preventDefault();
      event.returnValue = "You have unapplied settings changes.";
    };
    window.addEventListener("beforeunload", handler);
    return () => window.removeEventListener("beforeunload", handler);
  }, [hasUnsavedSectionChanges]);

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
    if (!confirmAction("Remove transcription model?", `This deletes the local model file or directory for ${model}. Future use will require downloading it again.`)) return;
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
        {hasUnsavedSectionChanges && <span className="section-status info">Unsaved: {dirtySections.map(label).join(", ")}</span>}
        <span className={messageKind === "error" ? "settings-message error" : "settings-message"}>{message || "Changes save by section."}</span>
      </div>
    </div>

    <div className="settings-layout">
      <div className="settings-nav">
        {settingsTabs.map(([id, text]) => <button key={id} className={settingsTab === id ? "active" : ""} onClick={() => setSettingsTab(id)}>{text}</button>)}
      </div>

    <div className="settings-flow">
      {settingsTab === "transcription" && <SettingsCard title="Transcription" description="Controls how individual calls become text. This is separate from AI summaries and incidents." busy={savingSection === "transcription"} testing={testingSection === "transcription"} status={sectionStatus.transcription} onSave={() => save("transcription")} onTest={() => test("transcription")}>
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
        {aiInsights.enabled && aiModels.length > 0
          ? <><div className="setting-field model-refresh-field"><span>Model<small>Chat model id used for summaries, incidents, and recommendations. LM Studio lists loadable model ids, not runtime aliases.</small></span><div><select value={aiModels.includes(aiInsights.openAiModel) ? aiInsights.openAiModel : ""} onChange={e => update("ai-insights", ["openAiModel"], e.target.value)}><option value="" disabled>Choose model</option>{aiModels.map(model => <option value={model} key={model}>{model}</option>)}</select><button type="button" onClick={() => void loadAiModels()}>Refresh</button><small>{aiModels.length} model{aiModels.length === 1 ? "" : "s"}</small></div></div>{aiInsights.openAiModel && !aiModels.includes(aiInsights.openAiModel) && <p className="settings-message error">Current saved model is not in the loadable model-id list: {aiInsights.openAiModel}</p>}</>
          : <SettingInput label="Model" description="Chat model id used for summaries, incidents, and recommendations." value={aiInsights.openAiModel} onChange={v => update("ai-insights", ["openAiModel"], v)} />}
        {aiInsights.enabled && aiModels.length === 0 && <div className="setting-inline-actions"><button type="button" onClick={() => void loadAiModels()}>Refresh</button><span className="muted">{aiModelsMessage}</span></div>}
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

      {settingsTab === "profiles" && <ProfilesSettingsCard reload={reload} pendingHides={pendingProfileHides} setPendingHides={setPendingProfileHides} />}

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

function ProfilesSettingsCard({ reload, pendingHides, setPendingHides }: { reload: () => Promise<void>; pendingHides: ProfileTalkgroupSetting[]; setPendingHides: React.Dispatch<React.SetStateAction<ProfileTalkgroupSetting[]>> }) {
  type ProfileTalkgroupSetting = NonNullable<ProcessingProfile["talkgroups"]>[number];
  const [profileState, setProfileState] = useState<ProfileState | null>(null);
  const [catalog, setCatalog] = useState<TalkgroupCatalogDocument | null>(null);
  const [selectedProfileId, setSelectedProfileId] = useState("");
  const [filter, setFilter] = useState("");
  const [categoryFilter, setCategoryFilter] = useState("all");
  const [visibilityFilter, setVisibilityFilter] = useState<"all" | "shown" | "hidden" | "tr-excluded">("all");
  const [busy, setBusy] = useState("");
  const [message, setMessage] = useState("");
  const [draftProfileId, setDraftProfileId] = useState("");
  const appliedPendingKeyRef = useRef("");

  useEffect(() => { void loadProfiles(); }, []);
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
          includeOther: true,
          allowedTalkgroups: [],
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

  async function loadProfiles() {
    setBusy("load");
    try {
      const [profiles, catalogResponse] = await Promise.all([
        api.request<ProfileState>("/api/v1/profiles"),
        api.request<TalkgroupCatalogResponse>("/api/v1/talkgroups/catalog")
      ]);
      setProfileState(profiles);
      setCatalog(catalogResponse.document);
      setSelectedProfileId(profiles.activeProfileId || profiles.profiles[0]?.id || "");
      setMessage("");
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Unable to load profiles.");
    } finally {
      setBusy("");
    }
  }

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
        includeOther: true,
        allowedTalkgroups: [],
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
    if (!profileState || !selectedProfile || defaultSelected || profileState.profiles.length <= 1) return;
    if (!confirmAction("Delete profile?", `Delete ${selectedProfile.name}? Profile-local hidden talkgroup rules for this profile will be removed.`)) return;
    const remaining = profileState.profiles.filter(profile => profile.id !== selectedProfile.id);
    const activeProfileId = profileState.activeProfileId === selectedProfile.id ? remaining[0].id : profileState.activeProfileId;
    setProfileState({ ...profileState, activeProfileId, profiles: remaining });
    setSelectedProfileId(activeProfileId);
  }

  function renameProfile(name: string) {
    updateSelectedProfile(profile => ({ ...profile, name, updatedAtUtc: new Date().toISOString() }));
  }

  function setProfileCategory(category: keyof Pick<ProcessingProfile, "includePolice" | "includeFire" | "includeEMS" | "includeTraffic" | "includeOther">, value: boolean) {
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
      const activeProfileId = selectedProfileId || profileState.activeProfileId || profileState.profiles[0]?.id;
      const saved = await api.request<ProfileState>("/api/v1/profiles", {
        method: "POST",
        body: JSON.stringify({ activeProfileId, profiles: profileState.profiles })
      });
      setProfileState(saved);
      setSelectedProfileId(saved.activeProfileId);
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
        <button disabled={busy === "load"} onClick={() => void loadProfiles()}>{busy === "load" ? "Loading..." : "Reload"}</button>
        <button className="danger-button" disabled={!profileState || busy === "save"} onClick={() => void saveProfiles()}>{busy === "save" ? "Saving..." : "Save Profiles"}</button>
      </div>
      {message && <span className={message.toLowerCase().includes("unable") || message.toLowerCase().includes("fail") ? "section-status error" : "section-status ok"}>{message}</span>}
    </div>
    {!selectedProfile ? <p className="muted">No profiles loaded.</p> : <div className="settings-fields">
      <div className="profile-editor-grid">
        <label className="setting-field"><span>Profile</span><div><select value={selectedProfile.id} onChange={event => setSelectedProfileId(event.target.value)}>{profiles.map(profile => <option value={profile.id} key={profile.id}>{profile.name}</option>)}</select></div></label>
        <label className={`setting-field profile-name-field ${!defaultSelected && selectedProfile.id === draftProfileId ? "needs-name" : ""}`}>
          <span>Name<small>{defaultSelected ? "Default is read-only. Duplicate it to create an editable profile." : selectedProfile.id === draftProfileId ? "Type a name for this new profile before saving." : "Display name for this profile."}</small></span>
          <div>
            <input value={selectedProfile.name} disabled={defaultSelected} autoFocus={!defaultSelected && selectedProfile.id === draftProfileId} onChange={event => renameProfile(event.target.value)} />
            {!defaultSelected && selectedProfile.id === draftProfileId && <span className="info-tip" tabIndex={0}>i<span>Rename this draft to describe what it hides, then save the profile.</span></span>}
          </div>
        </label>
        <div className="setting-inline-actions">
          <button type="button" onClick={createProfile}>Duplicate</button>
          <button type="button" className="danger-button" disabled={defaultSelected || profiles.length <= 1} onClick={deleteProfile}>Delete</button>
        </div>
      </div>
      {defaultSelected && <div className="settings-message info">Default is read-only and always shows all non-TR-excluded talkgroups. Duplicate it before hiding TGs or changing categories.</div>}
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
    talkgroupsText: ""
  };
  const [draft, setDraft] = useState<any>(emptyDraft);
  const [editingIndex, setEditingIndex] = useState<number | null>(null);

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

  function parseTalkgroups(value: string) {
    return value
      .split(",")
      .map(part => Number(part.trim()))
      .filter(value => Number.isFinite(value));
  }

  function normalizeDraftRule(existing?: any) {
    const matchType = draft.matchType || "keyword";
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
      talkgroups: parseTalkgroups(draft.talkgroupsText ?? "")
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
      matchType: rule.matchType ?? "keyword",
      keywords: rule.keywords ?? "",
      policeCodes: rule.policeCodes ?? "",
      email: rule.email ?? "",
      frequency: rule.frequency ?? "realtime",
      autoplay: rule.autoplay !== false,
      talkgroupsText: Array.isArray(rule.talkgroups) ? rule.talkgroups.join(", ") : ""
    });
  }

  function clearForm() {
    setEditingIndex(null);
    setDraft(emptyDraft);
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
    <div className="alert-rule-form">
      <div className="settings-subsection-title">
        <strong>{editingIndex === null ? "New alert" : "Edit alert"}</strong>
        <span>{editingIndex === null ? "Fill out the rule, then add it to the settings draft." : "Update the selected rule, then save Alerts to apply it."}</span>
      </div>
      <div className="alert-rule-grid">
        <SettingInput label="Name" description="Short operator-facing label." value={draft.name} onChange={value => patchDraft({ name: value })} />
        <SettingInput label="Email recipient" description="Optional. Leave empty for UI-only alerts." value={draft.email} onChange={value => patchDraft({ email: value })} />
        <SettingSelect label="Match type" description="Keyword, police-code, or both." value={draft.matchType} options={["keyword", "police-code", "both"]} onChange={value => patchDraft({ matchType: value })} />
        <SettingSelect label="Frequency" description="Limits repeated notification delivery." value={draft.frequency} options={["realtime", "hourly", "daily"]} onChange={value => patchDraft({ frequency: value })} />
        {(draft.matchType === "keyword" || draft.matchType === "both") && <SettingTextarea label="Keywords" description="Comma-separated keywords or phrases." value={draft.keywords} onChange={value => patchDraft({ keywords: value })} />}
        {(draft.matchType === "police-code" || draft.matchType === "both") && <SettingTextarea label="Police codes" description="Comma-separated codes, for example 10-50, 10-80." value={draft.policeCodes} onChange={value => patchDraft({ policeCodes: value })} />}
        <SettingInput label="Talkgroups" description="Optional comma-separated TG IDs. Leave blank for all profile-eligible TGs." value={draft.talkgroupsText} onChange={value => patchDraft({ talkgroupsText: value })} />
      </div>
      <div className="alert-rule-checks">
        <label><input type="checkbox" checked={draft.enabled !== false} onChange={event => patchDraft({ enabled: event.currentTarget.checked })} /> Enabled</label>
        <label><input type="checkbox" checked={draft.autoplay !== false} onChange={event => patchDraft({ autoplay: event.currentTarget.checked })} /> Autoplay when globally enabled</label>
      </div>
      <div className="alert-rule-form-actions">
        <button type="button" className="danger-button" onClick={addOrUpdateRule}>{editingIndex === null ? "Add Alert" : "Update Alert"}</button>
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

function TalkgroupCatalogSettingsCard({ reloadToken = 0, embedded = false, allowSystemExclusions = false, onCatalogChanged }: { reloadToken?: number; embedded?: boolean; allowSystemExclusions?: boolean; onCatalogChanged?: () => Promise<void> }) {
  const [catalogPage, setCatalogPage] = useState<TalkgroupCatalogPage | null>(null);
  const [filter, setFilterState] = useState(() => embedded ? localStorage.getItem("pizzawave-setup-talkgroup-filter") || "" : "");
  const [debouncedFilter, setDebouncedFilter] = useState(filter);
  const [enabledFilter, setEnabledFilter] = useState<"all" | "included" | "excluded">("all");
  const [categoryFilter, setCategoryFilter] = useState("all");
  const [sortKey, setSortKey] = useState<"state" | "id" | "name" | "category">("id");
  const [sortDir, setSortDir] = useState<"asc" | "desc">("asc");
  const [page, setPage] = useState(1);
  const [showAllRows, setShowAllRows] = useState(false);
  const [selectedTalkgroupKeys, setSelectedTalkgroupKeys] = useState<Set<string>>(() => new Set());
  const [busy, setBusy] = useState("");
  const [message, setMessage] = useState("");
  const loadSerialRef = useRef(0);

  useEffect(() => {
    const handle = window.setTimeout(() => setDebouncedFilter(filter), 250);
    return () => window.clearTimeout(handle);
  }, [filter]);
  useEffect(() => { setPage(1); }, [debouncedFilter, enabledFilter, categoryFilter, sortKey, sortDir, showAllRows]);
  useEffect(() => { setSelectedTalkgroupKeys(new Set()); }, [reloadToken]);
  useEffect(() => { void loadCatalog(); }, [reloadToken, debouncedFilter, enabledFilter, categoryFilter, sortKey, sortDir, page, showAllRows]);

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
