import React, { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { flushSync } from "react-dom";
import { createRoot } from "react-dom/client";
import { Activity, Bell, BellOff, Camera, CheckCircle2, ChevronDown, ChevronRight, Gauge, Info, Link2, Play, Radio, RefreshCw, Search, Settings, Square, Wrench } from "lucide-react";
import { api, rangeBody, rangeQuery } from "./api";
import type { AuthTokenRequest } from "./api";
import type { AlertMatch, BackupArchive, BackupCreateResult, BackupEstimate, BackupRestoreApplyResult, BackupRestoreCancelResult, BackupRestorePreview, BarStat, CategoryPage, Dashboard, EngineCall, EngineHealth, HourCategory, Incident, IncidentOperationAuditRow, Job, JobLog, LocationHeat, MigrationActionResult, MigrationResetResult, ProcessingProfile, ProfileState, QualityAuditGroup, QualityAuditSample, QualityHour, QueueSnapshot, RemoteBandwidthReport, RfSurveyCancelExperimentResult, RfSurveyCandidate, RfSurveyCaptureTrialResult, RfSurveyConfigDraft, RfSurveyDetail, RfSurveyExperiment, RfSurveyExperimentPlan, RfSurveyList, RfSurveyP25ProbePreview, RfSurveyPathProfile, RfSurveyProfile, RfSurveySession, RfSurveySource, RfSurveySweepCandidateProgress, RfSurveySweepProgress, RfSurveySweepProgressRow, RfSurveySystem, RfSurveyTrActionResult, RfSurveyWaterfallStatus, SetupAreaBoundaryCandidate, SetupAreaBoundaryResponse, SetupArtifactReport, SetupCalibrationPlan, SetupSdrDetection, SetupStatus, SetupTalkgroupPreview, SetupTalkgroupRow, SetupTrConfigDraft, SetupTrConfigSites, SetupTrConfigSourcePlan, SetupValidationResult, StatusSummary, SystemCpuSnapshot, SystemRecommendations, TalkgroupCatalogDocument, TalkgroupCatalogItem, TalkgroupCatalogResponse, TalkgroupCatalogSaveResult, TokenUsageReport, TopTalkgroup, TrConfigBackup, TrConfigEditor, TrConfigEditorApplyResult, TrConfigRestoreResult, TrHealthChart, TrHealthMetric, TrRfAnalysis, TrTroubleshoot } from "./types";
import "./style.css";

const categories = ["police", "fire", "ems", "traffic", "other"] as const;
const radioSetupApi = "/api/v1/system/radio-setup";
const radioSetupDetailUrl = (id: string, compact = true) => `${radioSetupApi}/${encodeURIComponent(id)}${compact ? "?compact=true" : ""}`;
const waterfallStopUrl = (surveyId: string) => `${radioSetupApi}/${encodeURIComponent(surveyId)}/waterfall/stop`;
type Page = "dashboard" | "tools" | "system" | "settings" | typeof categories[number];
type DashboardMode = "incidents" | "alerts";
type CategorySortMode = "name" | "recent" | "frequent";
type AuthPromptState = { request: AuthTokenRequest; resolve: (token: string | null) => void; token: string; message: string };
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
type AutoplayContext = { key: string; kind: "alert" | "incident" | "traffic" | "police"; callId: number; incidentId?: number; label: string };

function radioReferenceSitesCacheKey(sid: string) {
  return `pizzawave-radio-setup-rr-sites-${sid.trim()}`;
}

function readCachedRadioReferenceSites(sid: string): SetupTrConfigSites | null {
  if (!sid.trim()) return null;
  try {
    const raw = localStorage.getItem(radioReferenceSitesCacheKey(sid));
    if (!raw) return null;
    const parsed = JSON.parse(raw) as SetupTrConfigSites;
    return parsed && Array.isArray(parsed.sites) ? parsed : null;
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

function App() {
  const [page, setPageState] = useState<Page>(() => normalizePage(localStorage.getItem("pizzawave-page")));
  const [rangeHours, setRangeHours] = useState(24);
  const [theme, setTheme] = useState(() => localStorage.getItem("pizzawave-theme") || "blue");
  const [status, setStatus] = useState("Starting");
  const [dashboard, setDashboard] = useState<Dashboard | null>(null);
  const [category, setCategory] = useState<CategoryPage | null>(null);
  const [jobs, setJobs] = useState<Job[]>([]);
  const [engineHealth, setEngineHealth] = useState<EngineHealth | null>(null);
  const [statusSummary, setStatusSummary] = useState<StatusSummary | null>(null);
  const [profileState, setProfileState] = useState<ProfileState | null>(null);
  const [setupStatus, setSetupStatus] = useState<SetupStatus | null>(null);
  const [radioSetupStatus, setRadioSetupStatus] = useState<RfSurveyList | null>(null);
  const [troubleshoot, setTroubleshoot] = useState<TrTroubleshoot | null>(null);
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
  const [globalSearch, setGlobalSearch] = useState("");
  const [systemTargetTab, setSystemTargetTab] = useState<SystemTopTab | null>(null);
  const [settingsTargetTab, setSettingsTargetTab] = useState<string | null>(null);
  const settingsFileInputRef = useRef<HTMLInputElement | null>(null);
  const refreshStatusRef = useRef<() => Promise<void>>(async () => { });
  const refreshVisiblePageRef = useRef<() => Promise<void>>(async () => { });
  const pageRef = useRef<Page>(page);
  const lastDashboardRefreshRef = useRef(0);
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
    const setup = await api.request<SetupStatus>("/api/v1/setup/status");
    setSetupStatus(setup);
    if (!setup.completed) {
      const healthStatus = await api.request<EngineHealth>("/api/v1/health");
      setEngineHealth(healthStatus);
      setStatus("Setup");
      return;
    }

    const [healthStatus, jobRows, summary, profiles, alertRows, alertConfig, cpu] = await Promise.all([
      api.request<EngineHealth>("/api/v1/health"),
      api.request<Job[]>("/api/v1/jobs"),
      api.request<StatusSummary>(`/api/v1/status?${rangeQuery(rangeHours)}`),
      api.request<ProfileState>("/api/v1/profiles"),
      api.request<AlertMatch[]>(`/api/v1/alerts?${rangeQuery(rangeHours)}`),
      api.request<any>("/api/v1/settings/alerts"),
      api.request<SystemCpuSnapshot>("/api/v1/system/cpu").catch(() => null)
    ]);
    setEngineHealth(healthStatus);
    setCpuSnapshot(cpu);
    setJobs(jobRows);
    setStatusSummary(summary);
    setProfileState(profiles);
    setAlertSettings(alertConfig.values ?? alertConfig);
    setActiveAlertCount(alertRows.filter(alert => alert.active !== false).length);
    const latestActiveAlert = alertRows.find(alert => alert.active !== false);
    if (latestActiveAlert && autoplayAllows(alertConfig.values ?? alertConfig, "alert"))
      playCallAudio(latestActiveAlert.callId, "alert", undefined, alertPlaybackLabel(latestActiveAlert));
    setStatus("Live");
    void api.request<SystemRecommendations>("/api/v1/system/recommendations")
      .then(setRecommendations)
      .catch(() => { });
  }, [rangeHours]);

  const refreshVisiblePage = useCallback(async () => {
    if (page === "dashboard") {
      const nextDashboard = await api.request<Dashboard>(`/api/v1/dashboard?${rangeQuery(rangeHours)}`);
      setDashboard(nextDashboard);
      maybeAutoplayDashboard(nextDashboard);
      lastDashboardRefreshRef.current = Date.now();
    } else if (categories.includes(page as any)) {
      const search = globalSearch.trim();
      setCategory(await api.request<CategoryPage>(`/api/v1/categories/${page}?${rangeQuery(rangeHours)}${search ? `&q=${encodeURIComponent(search)}` : ""}`));
    } else if (page === "tools") {
      // Tool pages fetch their own focused data.
    } else if (page === "system") {
      setTroubleshoot(await api.request<TrTroubleshoot>(`/api/v1/troubleshoot?${rangeQuery(rangeHours)}&bySystem=false&baseline=7d`));
    }
  }, [page, rangeHours, globalSearch]);

  const load = useCallback(async () => {
    try {
      await Promise.all([refreshStatusData(), refreshVisiblePage()]);
    } catch (error) {
      setStatus(error instanceof Error ? error.message : "Error");
    }
  }, [refreshStatusData, refreshVisiblePage]);

  useEffect(() => { refreshStatusRef.current = refreshStatusData; }, [refreshStatusData]);
  useEffect(() => { refreshVisiblePageRef.current = refreshVisiblePage; }, [refreshVisiblePage]);
  useEffect(() => { pageRef.current = page; }, [page]);
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

  useEffect(() => { void load(); }, [load]);
  useEffect(() => { if (page === "settings") void loadSettings(); }, [page, loadSettings]);
  useEffect(() => {
    if (!setupStatus?.completed) {
      setRadioSetupStatus(null);
      return;
    }
    void api.request<RfSurveyList>(radioSetupApi).then(setRadioSetupStatus).catch(() => setRadioSetupStatus(null));
  }, [setupStatus?.completed]);

  useEffect(() => {
    document.documentElement.dataset.theme = theme;
    localStorage.setItem("pizzawave-theme", theme);
  }, [theme]);

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
      if (pageRef.current === "dashboard") {
        const elapsed = Date.now() - lastDashboardRefreshRef.current;
        delayMs = Math.max(delayMs, elapsed >= 30_000 ? 0 : 30_000 - elapsed);
      }
      pageTimer = window.setTimeout(() => {
        if (pageRef.current === "dashboard")
          lastDashboardRefreshRef.current = Date.now();
        void refreshVisiblePageRef.current().catch(error => setStatus(error instanceof Error ? error.message : "Error"));
      }, delayMs);
    };
    const refreshCurrentView = () => {
      scheduleStatus(500);
      schedulePage(900);
    };
    const refreshCurrentViewSoon = () => {
      scheduleStatus(900);
      schedulePage(3000);
    };
    const refreshStatusOnly = () => {
      scheduleStatus(900);
    };
    events.addEventListener("connected", () => {
      setStatus("Live");
      refreshCurrentView();
    });
    events.addEventListener("call_ingested", refreshCurrentViewSoon);
    events.addEventListener("call_transcribed", event => {
      refreshCurrentViewSoon();
      try {
        const payload = JSON.parse((event as MessageEvent).data || "{}");
        if (payload.callId && autoplayAllows(alertSettingsRef.current, "police"))
          void api.request<EngineCall>(`/api/v1/calls/${payload.callId}`).then(call => {
            if (call.category === "police")
              playCallAudio(call.id, "police", undefined, callPlaybackLabel(call));
          }).catch(() => { });
      } catch { }
    });
    events.addEventListener("alert_matched", refreshCurrentView);
    events.addEventListener("summary_updated", refreshCurrentView);
    events.addEventListener("job_updated", refreshCurrentView);
    events.addEventListener("health_updated", refreshStatusOnly);
    events.onerror = () => setStatus("Reconnecting");
    return () => {
      window.clearTimeout(statusTimer);
      window.clearTimeout(pageTimer);
      events.close();
    };
  }, []);

  const nav = useMemo(() => ["dashboard", ...categories, "tools", "system", "settings"] as Page[], []);
  const activeProfile = profileState?.profiles.find(p => p.id === profileState.activeProfileId);
  const visibleNav = nav.filter(item => !categories.includes(item as any) || profileIncludes(activeProfile, item));
  const activeJobCount = jobs.filter(j => j.status === "running" || j.status === "queued" || j.status === "paused").length;
  const trCoveragePausedByJob = jobs.some(j => j.status === "running" && j.type === "setup_tr_calibration_sweep");
  const [radioSetupTrOperation, setRadioSetupTrOperation] = useState("");
  const trCoveragePaused = trCoveragePausedByJob || Boolean(radioSetupTrOperation);
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
  const livePillClass = [
    "pill",
    trCoveragePaused ? "ingest-paused" : liveTrActivity?.stale ? "live-status-error" : trIntentionallyStopped ? "live-status-warning" : status === "Reconnecting" ? "live-status-warning" : status === "Live" ? "live-status-ok" : ""
  ].filter(Boolean).join(" ");
  const livePillText = trCoveragePaused ? "TR paused" : liveTrActivity?.stale ? "Live stale" : trIntentionallyStopped ? "TR stopped" : status;
  const livePillTitle = trCoveragePaused
    ? radioSetupTrOperation || "trunk-recorder is temporarily paused or restarting while a Radio Setup job is running."
    : liveTrActivity?.stale || trIntentionallyStopped
    ? liveTrActivity.message
    : "Live means the browser is connected to pizzad and recent TR activity has not crossed the silence threshold.";
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

  const inSetup = Boolean(setupStatus && !setupStatus.completed);
  const showCalibrationBanner = Boolean(setupStatus?.completed && radioSetupStatus && !radioSetupHasStableCandidate(radioSetupStatus));
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
      setDashboard(nextDashboard);
      const incidentId = context?.incidentId
        ?? nextDashboard.incidents.find(incident => incident.calls.some(call => call.callId === context?.callId))?.id
        ?? null;
      if (incidentId) {
        setFocusedIncidentId(incidentId);
        window.setTimeout(() => document.getElementById(`incident-${incidentId}`)?.scrollIntoView({ block: "center", behavior: "smooth" }), 50);
      } else if (target === "incident") {
        setStatus("Incident not visible yet");
      }
    } catch (error) {
      setStatus(error instanceof Error ? error.message : "Dashboard refresh failed");
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
    const saved = await api.request<ProfileState>("/api/v1/profiles", { method: "POST", body: JSON.stringify(next) });
    setProfileState(saved);
    await load();
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
  function goSettings(tab: string) {
    setSettingsTargetTab(tab);
    setPage("settings");
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
    <div className={`app ${inSetup ? "setup-mode" : ""} ${showCalibrationBanner ? "calibration-mode" : ""}`}>
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
        <label className="global-search" title="Search the current page">
          <Search size={15} aria-hidden="true" />
          <input
            type="search"
            value={globalSearch}
            placeholder={page === "dashboard" ? "Search incidents" : categories.includes(page as any) ? "Search calls" : "Search page"}
            aria-label="Search current page"
            onChange={e => setGlobalSearch(e.target.value)}
          />
        </label>
        {page === "settings" && <>
          <input ref={settingsFileInputRef} type="file" accept="application/json,.json" hidden onChange={e => void loadSettingsFile(e.target.files?.[0])} />
          <button disabled={settingsLoadState.loading} onClick={() => settingsFileInputRef.current?.click()}>{settingsLoadState.loading ? "Loading..." : "Load Settings"}</button>
          <button disabled={settingsLoadState.loading} onClick={() => void exportSettingsFile()}>Export Settings</button>
        </>}
        <span className={livePillClass} title={livePillTitle}>{livePillText}</span>
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
        <span className="pill" title="REST loads the current view; SSE triggers live refreshes when calls, jobs, alerts, summaries, or health change.">REST+SSE</span>
      </header>
      {showCalibrationBanner && <RadioSetupCalibrationBanner onOpen={() => setPage("tools")} />}
      {!inSetup && <aside className="nav">
        {visibleNav.map(item => (
          <React.Fragment key={item}>
            {item === "tools" && <div className="nav-divider" />}
            <button className={item === page ? "active" : ""} onClick={() => {
              if (page === "settings" && item !== "settings" && !confirmDiscardUnappliedSettings()) return;
              setPage(item);
            }}>
              {navIcon(item)} {label(item)}
              {item === "dashboard" && activeAlertCount > 0 && <span className="nav-badge high">{activeAlertCount}</span>}
              {item === "system" && recommendations && recommendations.openCount > 0 && <span className={`nav-badge ${recommendations.highCount > 0 ? "high" : recommendations.mediumCount > 0 ? "medium" : "low"}`}>{recommendations.openCount}</span>}
            </button>
          </React.Fragment>
        ))}
      </aside>}
      <main className={`main ${inSetup ? "setup-main" : ""}`}>
        {inSetup && setupStatus && <SetupWizard status={setupStatus} reload={load} onComplete={() => setPage("tools")} />}
        {setupStatus?.completed && page === "dashboard" && <DashboardView data={dashboard} rangeHours={rangeHours} reload={load} focusedIncidentId={focusedIncidentId} focusedHashTarget={focusedHashTarget} clearFocusedIncident={() => setFocusedIncidentId(null)} clearFocusedHashTarget={() => setFocusedHashTarget("")} mode={dashboardMode} setMode={setDashboardMode} searchQuery={globalSearch} />}
        {setupStatus?.completed && categories.includes(page as any) && <CategoryView data={category} rangeHours={rangeHours} searchQuery={globalSearch} />}
        {setupStatus?.completed && page === "tools" && <ToolsView onOpenTalkgroups={() => goSettings("talkgroups")} onTrOperationChange={setRadioSetupTrOperation} />}
        {setupStatus?.completed && page === "system" && <SystemView data={troubleshoot} jobs={jobs} rangeHours={rangeHours} reload={load} engineHealth={engineHealth} cpuSnapshot={cpuSnapshot} recommendations={recommendations} setRecommendations={setRecommendations} targetTab={systemTargetTab} clearTargetTab={() => setSystemTargetTab(null)} onOpenRadioSetup={() => setPage("tools")} />}
        {setupStatus?.completed && page === "settings" && <SettingsView settingsSections={settingsSections} settingsLoadState={settingsLoadState} reload={load} profileState={profileState} setProfileState={setProfileState} targetTab={settingsTargetTab} clearTargetTab={() => setSettingsTargetTab(null)} />}
      </main>
      {!inSetup && <footer className="statusbar">
        <button type="button" className="pill status-pill-button" onClick={() => goSettings("talkgroups")}>Profile: {profileState?.profiles.find(p => p.id === profileState.activeProfileId)?.name ?? "Default"}</button>
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

function radioSetupHasStableCandidate(list: RfSurveyList) {
  return list.sessions.some(row =>
    row.status === "completed" ||
    row.verdict === "pass_candidate" ||
    row.stability === "stable_candidate");
}

function RadioSetupCalibrationBanner({ onOpen }: { onOpen: () => void }) {
  return <div className="calibration-banner">
    <div>
      <strong>TR config needs Radio Setup calibration</strong>
      <span>First-run setup can create a bootable TR config, but RF path, source coverage, P25 decode, and call-quality gates still need a Radio Setup workspace before the config should be treated as validated.</span>
    </div>
    <button className="danger-button" onClick={onOpen}>Open Radio Setup</button>
  </div>;
}

function DashboardView({ data, rangeHours, reload, focusedIncidentId, focusedHashTarget, clearFocusedIncident, clearFocusedHashTarget, mode, setMode, searchQuery }: { data: Dashboard | null; rangeHours: number; reload: () => Promise<void>; focusedIncidentId?: number | null; focusedHashTarget?: string; clearFocusedIncident?: () => void; clearFocusedHashTarget?: () => void; mode: DashboardMode; setMode: (mode: DashboardMode) => void; searchQuery: string }) {
  const [focusedLocationKey, setFocusedLocationKey] = useState<string | null>(null);
  const [selectedLocation, setSelectedLocation] = useState<LocationHeat | null>(null);
  useEffect(() => {
    setSelectedLocation(null);
    setFocusedLocationKey(null);
  }, [mode]);
  if (!data) return <div className="pane">Loading dashboard...</div>;
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
              ? <AlertsPanel alerts={data.alerts} locationMap={alertLocationMap} reload={reload} mode={mode} setMode={setMode} incidentCount={data.incidents.length} alertCount={data.alerts.length} searchQuery={searchQuery} />
              : <Incidents rows={data.incidents} alerts={data.alerts} locationMap={incidentLocationMap} onShowLocation={setFocusedLocationKey} reload={reload} focusedIncidentId={focusedIncidentId} focusedHashTarget={focusedHashTarget} clearFocusedIncident={clearFocusedIncident} clearFocusedHashTarget={clearFocusedHashTarget} mode={mode} setMode={setMode} incidentCount={data.incidents.length} alertCount={data.alerts.length} searchQuery={searchQuery} />}
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
    <VolumeByHourChart rows={data.volumeByHourCategory} />
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

function LocationHeatMap({ rows, incidents, focusedKey, onFocusKey, onSelectLocation, emptyText = "No geolocated incidents detected in the selected range." }: { rows: LocationHeat[]; incidents: Incident[]; focusedKey?: string | null; onFocusKey?: (key: string | null) => void; onSelectLocation?: (row: LocationHeat | null) => void; emptyText?: string }) {
  const mapRef = useRef<HTMLDivElement | null>(null);
  const defaultCenter = useMemo(() => defaultMapCenter(rows), [rows]);
  const defaultZoom = useMemo(() => defaultMapZoom(rows), [rows]);
  const areaKey = useMemo(() => Array.from(new Set(rows.map(row => row.areaId))).sort().join("|"), [rows]);
  const lastAreaKey = useRef(areaKey);
  const [mapSize, setMapSize] = useState({ width: 760, height: 520 });
  const [zoom, setZoom] = useState(defaultZoom);
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
  }, [rows.length]);
  useEffect(() => {
    if (areaKey === lastAreaKey.current) return;
    lastAreaKey.current = areaKey;
    setCenter(defaultMapCenter(rows));
    setZoom(defaultMapZoom(rows));
    setSelected(null);
  }, [areaKey, rows]);
  useEffect(() => {
    if (!focusedKey) return;
    const row = rows.find(r => locationKey(r) === focusedKey);
    if (row) focusLocation(row);
  }, [focusedKey, rows]);
  useEffect(() => {
    if (focusedKey) return;
    setSelected(null);
    setCenter(defaultCenter);
    setZoom(defaultZoom);
  }, [focusedKey, defaultCenter, defaultZoom]);

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
    setZoom(current => Math.max(8, Math.min(14, current + (event.deltaY < 0 ? 1 : -1))));
  }

  return <div className="card location-heat-card">
    <div className="location-map-shell">
    <div className="location-map" ref={mapRef} role="img" aria-label="Geolocated incident map" onWheel={handleWheel}>
      <div className="map-zoom-controls"><button onClick={() => setZoom(current => Math.min(14, current + 1))} aria-label="Zoom in">+</button><button onClick={() => setZoom(current => Math.max(8, current - 1))} aria-label="Zoom out">-</button><span>{zoom}</span></div>
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
          onClick={() => focusLocation(row)}
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

function TopTalkgroups({ rows }: { rows: TopTalkgroup[] }) {
  return <table className="table top-talkgroups"><thead><tr><th>Talkgroup</th><th>Calls</th><th>Share</th><th>Trend</th></tr></thead><tbody>{rows.map(r => <tr key={r.talkgroup}><td>{r.label}</td><td>{r.count}</td><td>{(r.share * 100).toFixed(1)}%</td><td><div className="trend-bars" aria-label={`${r.label} trend, ${r.trendBucketLabel}`}>{r.trend.map((v, i) => <span className="trend" title={`${r.trendLabels?.[i] ?? "Bucket"}: ${r.trendCounts?.[i] ?? 0} calls`} style={{ height: 4 + v * 30 }} key={i} />)}</div><div className="muted">Hourly volume; hover bars for counts</div></td></tr>)}</tbody></table>;
}

type DashboardIncidentListItem = { kind: "incident"; incident: Incident } | { kind: "alert"; alert: AlertMatch };
type PendingCardFocus = CardHashTarget & { key: string; page: number };

function Incidents({ rows, alerts = [], locationMap, onShowLocation, reload, focusedIncidentId, focusedHashTarget, clearFocusedIncident, clearFocusedHashTarget, mode, setMode, incidentCount, alertCount, searchQuery }: { rows: Incident[]; alerts?: AlertMatch[]; locationMap?: Map<number, LocationHeat>; onShowLocation?: (key: string) => void; reload?: () => Promise<void>; focusedIncidentId?: number | null; focusedHashTarget?: string; clearFocusedIncident?: () => void; clearFocusedHashTarget?: () => void; mode: DashboardMode; setMode: (mode: DashboardMode) => void; incidentCount: number; alertCount: number; searchQuery: string }) {
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
        <DashboardModeToggle mode={mode} setMode={setMode} incidentCount={incidentCount} alertCount={alertCount} />
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

function AlertsPanel({ alerts, locationMap, reload, mode, setMode, incidentCount, alertCount, searchQuery }: { alerts: AlertMatch[]; locationMap: Map<number, LocationHeat>; reload?: () => Promise<void>; mode: DashboardMode; setMode: (mode: DashboardMode) => void; incidentCount: number; alertCount: number; searchQuery: string }) {
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
        <DashboardModeToggle mode={mode} setMode={setMode} incidentCount={incidentCount} alertCount={alertCount} />
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

function DashboardModeToggle({ mode, setMode, incidentCount, alertCount }: { mode: DashboardMode; setMode: (mode: DashboardMode) => void; incidentCount: number; alertCount: number }) {
  return <div className="dashboard-view-toggle compact" role="group" aria-label="Dashboard view">
    <button type="button" className={mode === "incidents" ? "active" : ""} onClick={() => setMode("incidents")}>Incidents ({incidentCount.toLocaleString()})</button>
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
    {call.audioUrl && <audio controls preload="metadata" src={call.audioUrl} />}
  </div>;
}

function CategoryView({ data, rangeHours, searchQuery }: { data: CategoryPage | null; rangeHours: number; searchQuery: string }) {
  const [sortMode, setSortModeState] = useState<CategorySortMode>(() => normalizeCategorySort(localStorage.getItem("pizzawave-category-sort")));
  function setSortMode(value: CategorySortMode) {
    setSortModeState(value);
    localStorage.setItem("pizzawave-category-sort", value);
  }
  if (!data) return <div className="category-page">Loading...</div>;
  const sortedGroups = sortCategoryGroups(data.groups, sortMode);
  const filteredGroups = sortedGroups.filter(group => matchesCategoryGroupSearch(group, searchQuery));
  return <div className="category-page category-mode-page" data-category={data.category}>
    <section className="pane category-pane raw-category">
      <div className="category-header">
        <div className="category-title-row">
          <h2>{label(data.category)} Calls by Talkgroup</h2>
          <div className="segmented category-sort-toggle" role="group" aria-label="Sort talkgroups">
            <button type="button" className={sortMode === "name" ? "active" : ""} onClick={() => setSortMode("name")}>Name</button>
            <button type="button" className={sortMode === "recent" ? "active" : ""} onClick={() => setSortMode("recent")}>Recent</button>
            <button type="button" className={sortMode === "frequent" ? "active" : ""} onClick={() => setSortMode("frequent")}>Frequent</button>
          </div>
          <span className="muted">{filteredGroups.length.toLocaleString()} of {data.groups.length.toLocaleString()} talkgroup{data.groups.length === 1 ? "" : "s"}</span>
        </div>
      </div>
      <CategoryCallGroups groups={filteredGroups} category={data.category} rangeHours={rangeHours} searchQuery={searchQuery} />
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
      {alert.audioUrl && <audio controls preload="metadata" src={alert.audioUrl} />}
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
      ? "You have unapplied changes in Settings > Talkgroups. Leaving Settings will discard the local draft."
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
    {call.audioUrl && <audio controls preload="metadata" src={call.audioUrl} />}
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

function CategoryCallGroups({ groups, category, rangeHours, searchQuery }: { groups: CategoryPage["groups"]; category: string; rangeHours: number; searchQuery: string }) {
  if (!groups.length) return <div className="card"><p className="muted">No raw calls available for this category.</p></div>;
  return <>{groups.map(group => <CollapsibleCallGroup group={group} category={category} rangeHours={rangeHours} searchQuery={searchQuery} key={`${group.talkgroup}-${group.label}`} />)}</>;
}

function CollapsibleCallGroup({ group, category, rangeHours, searchQuery }: { group: CategoryPage["groups"][number]; category: string; rangeHours: number; searchQuery: string }) {
  const [open, setOpen] = useState(false);
  const [calls, setCalls] = useState<EngineCall[]>(group.calls ?? []);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState("");
  const count = group.count || calls.length;
  useEffect(() => {
    setCalls(group.calls ?? []);
    setError("");
    setLoading(false);
  }, [category, group.talkgroup, rangeHours]);
  useEffect(() => {
    if (!open || calls.length || loading || group.talkgroup === undefined) return;
    setLoading(true);
    setError("");
    api.request<CategoryPage["groups"][number]>(`/api/v1/categories/${category}/talkgroups/${group.talkgroup}/calls?${rangeQuery(rangeHours)}&limit=150`)
      .then(result => setCalls(result.calls ?? []))
      .catch(err => setError(err instanceof Error ? err.message : "Failed to load calls"))
      .finally(() => setLoading(false));
  }, [open, calls.length, loading, category, group.talkgroup, rangeHours]);
  const visibleCalls = calls.filter(call => matchesCallSearch(call, searchQuery));
  return <details className={`call-group category-${category}`} open={open} onToggle={e => setOpen(e.currentTarget.open)}>
    <summary><span><HighlightedText text={group.label} query={searchQuery} /></span><span className="muted">{count.toLocaleString()} calls{group.lastHeard ? `; latest ${relativeTime(group.lastHeard)}` : ""}</span></summary>
    {open && loading && <div className="call-group-status">Loading calls...</div>}
    {open && error && <div className="call-group-status error">{error}</div>}
    {open && visibleCalls.map(c => <CallRow call={c} searchQuery={searchQuery} key={c.id} />)}
    {open && calls.length > 0 && visibleCalls.length === 0 && <div className="call-group-status">No loaded calls match this search.</div>}
    {open && calls.length >= 150 && <div className="call-group-status">Showing latest 150 calls.</div>}
  </details>;
}

function CallRow({ call, searchQuery = "" }: { call: EngineCall; searchQuery?: string }) {
  const status = call.qualityReason && call.qualityReason !== "ok" ? `${call.transcriptionStatus}: ${call.qualityReason}` : call.transcriptionStatus;
  const transcript = call.transcription?.trim();
  const missingText = call.transcriptionStatus === "pending"
    ? "Pending transcription"
    : `No transcript available (${status || "not transcribed"}).`;
  return <div id={`call-${call.id}`} className={`call category-${call.category}`}><div className="call-head"><strong><HighlightedText text={call.talkgroupName || `TG ${call.talkgroup}`} query={searchQuery} /> <CopyCardLink targetId={`call-${call.id}`} label="Copy call link" /></strong><span>{new Date(call.startTime * 1000).toLocaleString()}</span><span>{status}</span>{call.isImported && <span className="pill">Imported</span>}</div><div><HighlightedText text={transcript || missingText} query={searchQuery} /></div>{call.audioPath && <audio controls preload="metadata" src={`/api/v1/calls/${call.id}/audio`} />}</div>;
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
  return value === "name" || value === "recent" || value === "frequent" ? value : "recent";
}

function sortCategoryGroups(groups: CategoryPage["groups"], mode: CategorySortMode) {
  return [...groups].sort((a, b) => {
    if (mode === "name")
      return (a.label || `TG ${a.talkgroup}`).localeCompare(b.label || `TG ${b.talkgroup}`, undefined, { sensitivity: "base", numeric: true });
    if (mode === "frequent")
      return (b.count - a.count) || (b.lastHeard - a.lastHeard) || (a.label || "").localeCompare(b.label || "", undefined, { sensitivity: "base", numeric: true });
    return (b.lastHeard - a.lastHeard) || (b.count - a.count) || (a.label || "").localeCompare(b.label || "", undefined, { sensitivity: "base", numeric: true });
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

function matchesCategoryGroupSearch(group: CategoryPage["groups"][number], query: string) {
  return matchesTextSearch([group.label, group.talkgroup, group.count], query)
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

function SystemView({ data, jobs, rangeHours, reload, engineHealth, cpuSnapshot, recommendations, setRecommendations, targetTab, clearTargetTab, onOpenRadioSetup }: { data: TrTroubleshoot | null; jobs: Job[]; rangeHours: number; reload: () => Promise<void>; engineHealth: EngineHealth | null; cpuSnapshot: SystemCpuSnapshot | null; recommendations: SystemRecommendations | null; setRecommendations: (value: SystemRecommendations | null) => void; targetTab?: SystemTopTab | null; clearTargetTab?: () => void; onOpenRadioSetup?: () => void }) {
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
    if (topTab !== "metrics" || !["overview", "ai"].includes(metricsTab)) return;
    void api.request<TokenUsageReport>(`/api/v1/system/token-usage?${rangeQuery(rangeHours)}`).then(setTokenUsage).catch(() => setTokenUsage(null));
  }, [topTab, metricsTab, rangeHours]);
  useEffect(() => {
    if (topTab !== "metrics" || !["overview", "bandwidth"].includes(metricsTab)) return;
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

  if (!data) return <div className="trouble-page">Loading system data...</div>;
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
      {topTab === "recommendations" && <RecommendationsPanel recommendations={recommendations} busy={recommendationBusy} message={insightText} onOpen={openRecommendationTarget} onState={setRecommendationState} />}
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
        {trTab === "config" && <TrConfigReadOnlyPanel onOpenRadioSetup={onOpenRadioSetup} />}
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

function TrConfigReadOnlyPanel({ onOpenRadioSetup }: { onOpenRadioSetup?: () => void }) {
  const [editor, setEditor] = useState<TrConfigEditor | null>(null);
  const [surveys, setSurveys] = useState<RfSurveyList | null>(null);
  const [message, setMessage] = useState("");
  const [busy, setBusy] = useState(true);

  useEffect(() => {
    let canceled = false;
    setBusy(true);
    Promise.all([
      api.request<TrConfigEditor>("/api/v1/system/tr-config/editor"),
      api.request<RfSurveyList>(radioSetupApi).catch(() => null)
    ])
      .then(([editorResult, surveyResult]) => {
        if (canceled) return;
        setEditor(editorResult);
        setSurveys(surveyResult);
        setMessage("");
      })
      .catch(error => !canceled && setMessage(error instanceof Error ? error.message : "Unable to load TR config."))
      .finally(() => !canceled && setBusy(false));
    return () => { canceled = true; };
  }, []);

  const liveConfig = editor?.liveConfigJson ?? "";
  const matchingWorkspace = useMemo(() => chooseRadioSetupWorkspaceForConfig(editor, surveys), [editor, surveys]);
  function editInRadioSetup() {
    if (matchingWorkspace) {
      localStorage.setItem("pizzawave-radio-setup-workspace", matchingWorkspace.id);
      localStorage.setItem("pizzawave-radio-setup-wizard-open", "1");
    } else {
      localStorage.removeItem("pizzawave-radio-setup-workspace");
      localStorage.removeItem("pizzawave-radio-setup-wizard-open");
    }
    localStorage.setItem("pizzawave-tools-tab", "radio-setup");
    onOpenRadioSetup?.();
  }

  if (busy && !editor) return <div className="card">Loading TR config...</div>;
  return <div className="tr-config-readonly">
    <div className="setup-job-head">
      <div>
        <h3>Active TR Config</h3>
        <p className="muted">Read-only view of the config currently installed at {editor?.livePath || "--"}.</p>
        {editor?.hasDraft && <p className="settings-message">A standalone editor draft exists at {editor.draftPath}, but this page is showing the live TR config.</p>}
      </div>
      {onOpenRadioSetup && <button className="danger-button" onClick={editInRadioSetup}>Edit</button>}
    </div>
    <div className="setup-note">Edits are routed through Radio Setup so source coverage, RF validation, and call-quality evidence stay attached to the workspace that produced the TR config.</div>
    {matchingWorkspace && <div className="settings-message ok">Edit will open workspace {matchingWorkspace.siteLabel || matchingWorkspace.systemShortName || matchingWorkspace.id}.</div>}
    {!matchingWorkspace && <div className="settings-message">No matching workspace was found. Edit opens the Radio Setup workspace list.</div>}
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

function chooseRadioSetupWorkspaceForConfig(editor: TrConfigEditor | null, surveys: RfSurveyList | null) {
  const sessions = surveys?.sessions ?? [];
  if (!sessions.length) return null;
  const systemNames = new Set((editor?.summary.systems ?? []).map(system => system.shortName).filter(Boolean));
  const sorted = [...sessions].sort((a, b) => b.updatedAtUtc.localeCompare(a.updatedAtUtc));
  return sorted.find(session => systemNames.has(session.systemShortName)) ?? sorted[0] ?? null;
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
      <Kpi label="AI" value={aiFailures > 0 ? aiServiceFailures > 0 ? "Errors" : "Truncated" : tokenUsage ? "OK" : "Loading"} status={aiServiceFailures > 0 ? "error" : aiTruncated > 0 ? "warning" : tokenUsage ? "ok" : "neutral"} subtext={tokenUsage ? `${aiTimeoutFailures.toLocaleString()} timeout, ${aiNoValidFailures.toLocaleString()} no result, ${aiTruncated.toLocaleString()} truncated, ${formatCompact(tokenUsage.summary.totalTokens)} tokens` : "AI usage loading"} onClick={() => navigate("metrics", "ai")} />
      <Kpi label="Bandwidth" value={bandwidthUsage ? formatBytes(bandwidthUsage.summary.totalBytes) : "Loading"} status={bandwidthUsage?.summary.missingAudioFiles ? "warning" : bandwidthUsage ? "ok" : "neutral"} subtext={bandwidthUsage ? `${bandwidthUsage.remoteHost || "no remote host"} - ${bandwidthUsage.summary.requests.toLocaleString()} estimated request(s)` : "remote usage loading"} onClick={() => navigate("metrics", "bandwidth")} />
    </div>
  </div>;
}

function ToolsView({ onOpenTalkgroups, onTrOperationChange }: { onOpenTalkgroups: () => void; onTrOperationChange: (value: string) => void }) {
  const [tab, setTabState] = useState(() => localStorage.getItem("pizzawave-tools-tab") || "radio-setup");
  const [immersive, setImmersive] = useState(false);
  const setTab = (value: string) => {
    setTabState(value);
    localStorage.setItem("pizzawave-tools-tab", value);
  };
  return <div className="tools-page">
    {!immersive && <div className="tools-header">
      <div>
        <h2>Tools</h2>
        <p>Operator workflows for radio setup, diagnostics, validation, and config experiments.</p>
      </div>
    </div>}
    {!immersive && <div className="tools-tabs">
      <button className={tab === "radio-setup" ? "active" : ""} onClick={() => setTab("radio-setup")}>Radio Setup</button>
    </div>}
    {tab === "radio-setup" && <RfSurveyPanel setImmersive={setImmersive} onOpenTalkgroups={onOpenTalkgroups} onTrOperationChange={onTrOperationChange} />}
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

function RfSurveyPanel({ setImmersive, onOpenTalkgroups, onTrOperationChange }: { setImmersive?: (value: boolean) => void; onOpenTalkgroups: () => void; onTrOperationChange: (value: string) => void }) {
  const [surveys, setSurveys] = useState<RfSurveyList | null>(null);
  const [detail, setDetail] = useState<RfSurveyDetail | null>(null);
  const [wizardOpen, setWizardOpen] = useState(false);
  const [scopePlan, setScopePlan] = useState<SetupCalibrationPlan | null>(null);
  const [surveySystem, setSurveySystem] = useState("");
  const [surveySystems, setSurveySystems] = useState<string[]>([]);
  const [sourcePlanSystems, setSourcePlanSystems] = useState<string[]>([]);
  const [sourcePlanMode, setSourcePlanMode] = useState<"full" | "control">("full");
  const [surveySiteLabel, setSurveySiteLabel] = useState("");
  const [radioReferenceSid, setRadioReferenceSid] = useState("");
  const [radioReferenceSites, setRadioReferenceSites] = useState<SetupTrConfigSites | null>(null);
  const [step, setStep] = useState(0);
  const [message, setMessage] = useState("");
  const [busy, setBusy] = useState("");
  const [configApplyInFlight, setConfigApplyInFlight] = useState(false);
  const [details, setDetails] = useState<{ title: string; body: React.ReactNode } | null>(null);
  const [path, setPath] = useState<RfSurveyPathProfile>(() => emptyRfPath());
  const [selectedSources, setSelectedSources] = useState<number[]>([]);
  const [sdrSources, setSdrSources] = useState<RfSurveySource[] | null>(null);
  const [rfPathTouched, setRfPathTouched] = useState(false);
  const [sdrScopeTouched, setSdrScopeTouched] = useState(false);
  const [measurementMode, setMeasurementMode] = useState<"guided" | "desk" | "expert">("guided");
  const [duration, setDuration] = useState("45");
  const [runLogs, setRunLogs] = useState<RfRunLogLine[]>([]);
  const [runLogOpen, setRunLogOpen] = useState(false);
  const [callQualityRunStartedAtUtc, setCallQualityRunStartedAtUtc] = useState("");
  const [operationJob, setOperationJob] = useState<Job | null>(null);
  const [operationJobLogs, setOperationJobLogs] = useState<JobLog[]>([]);
  const autosaveSignatureRef = useRef("");
  const draftDirtyRef = useRef(false);
  const restoredWorkspaceRef = useRef(false);
  const operationLogLastId = useRef(0);
  const rrSitesAutoLoadKeyRef = useRef("");
  const radioReferenceSidEditedRef = useRef(false);
  const siteDefinitionRepairKeyRef = useRef("");
  const activeWorkspaceSystemsRef = useRef<string[]>([]);

  function markDraftDirty() {
    draftDirtyRef.current = true;
  }

  useEffect(() => { void loadSurveys(); void loadRadioSetupContext(); }, []);
  useEffect(() => {
    setImmersive?.(wizardOpen);
    return () => setImmersive?.(false);
  }, [wizardOpen, setImmersive]);
  useEffect(() => () => onTrOperationChange(""), [onTrOperationChange]);
  useEffect(() => {
    if (!operationJob?.id || !["queued", "running", "paused"].includes(operationJob.status)) return;
    const timer = window.setInterval(() => void refreshOperationJob(operationJob.id), 2000);
    return () => window.clearInterval(timer);
  }, [operationJob?.id, operationJob?.status]);

  function appendRunLog(text: string, level: RfRunLogLine["level"] = "info") {
    const line = { id: `${Date.now()}-${Math.random().toString(16).slice(2)}`, level, text, createdAtUtc: new Date().toISOString() };
    setRunLogs(current => [...current, line].slice(-180));
  }

  async function refreshOperationJob(jobId: number, resetLogs = false) {
    const afterId = resetLogs ? 0 : operationLogLastId.current;
    const [jobs, logs] = await Promise.all([
      api.request<Job[]>("/api/v1/jobs"),
      api.request<JobLog[]>(`/api/v1/jobs/${jobId}/logs?afterId=${afterId}`)
    ]);
    const job = jobs.find(row => row.id === jobId);
    if (job) setOperationJob(job);
    if (resetLogs) {
      setOperationJobLogs(logs);
      operationLogLastId.current = logs.length ? logs[logs.length - 1].id : 0;
    } else if (logs.length) {
      setOperationJobLogs(current => [...current, ...logs].slice(-240));
      operationLogLastId.current = logs[logs.length - 1].id;
    }
  }

  async function trackOperationJob(job: Job) {
    setOperationJob(job);
    setOperationJobLogs([]);
    operationLogLastId.current = 0;
    appendRunLog(`Started job ${job.id}: ${job.message}`, "info");
    await refreshOperationJob(job.id, true);
  }
  useEffect(() => {
    if (!detail) return;
    localStorage.setItem(`pizzawave-radio-setup-step-v2-${detail.session.id}`, String(step));
    localStorage.setItem("pizzawave-radio-setup-wizard-open", wizardOpen ? "1" : "0");
  }, [detail?.session.id, step, wizardOpen]);
  useEffect(() => {
    if (!wizardOpen || step !== 1) return;
    const sid = radioReferenceSid.trim();
    if (!sid || radioReferenceSites?.sites.length) return;
    const cached = readCachedRadioReferenceSites(sid);
    if (cached) {
      setRadioReferenceSites(cached);
      return;
    }
    if (!radioReferenceSidEditedRef.current && (detail?.profile.systems?.length ?? 0) > 0) return;
    if (radioReferenceSidEditedRef.current) return;
    if (busy || rrSitesAutoLoadKeyRef.current === sid) return;
    rrSitesAutoLoadKeyRef.current = sid;
    void loadRadioReferenceSites(false);
  }, [wizardOpen, step, radioReferenceSid, radioReferenceSites?.sites.length, busy]);
  useEffect(() => {
    if (!wizardOpen || !detail || configApplyInFlight) return;
    const selectedSystemNames = surveySystems.length ? surveySystems : surveySystem ? [surveySystem] : [];
    const draftRadioReferenceSites = radioReferenceSites ?? readCachedRadioReferenceSites(radioReferenceSid.trim());
    const systemDefinitions = buildSurveySystemDefinitions(selectedSystemNames, scopePlan, draftRadioReferenceSites, detail.profile.systems ?? []);
    const appliedSelectedSources = detail.profile.selectedSourceIndexes?.length
      ? detail.profile.selectedSourceIndexes
      : detail.profile.sources.map(source => source.index);
    const autosaveSelectedSources = selectedSources.length ? selectedSources : appliedSelectedSources;
    const body = {
      systemShortName: selectedSystemNames[0] ?? surveySystem,
      systemShortNames: selectedSystemNames,
      sourcePlanSystemShortNames: sourcePlanSystems.length ? sourcePlanSystems : selectedSystemNames,
      sourcePlanMode,
      systemDefinitions,
      radioReferenceSid: radioReferenceSid.trim() || undefined,
      siteLabel: surveySiteLabel,
      rfPath: path,
      selectedSourceIndexes: autosaveSelectedSources,
      sdrSources: sdrSources ?? undefined,
      currentStep: step,
      measurementMode,
      probeDurationSeconds: Number(duration) || 45
    };
    const signature = JSON.stringify({ id: detail.session.id, ...body });
    if (signature === autosaveSignatureRef.current) return;
    if (!draftDirtyRef.current) return;
    const timer = window.setTimeout(() => {
      autosaveSignatureRef.current = signature;
      void api.request<RfSurveyDetail>(`${radioSetupApi}/${encodeURIComponent(detail.session.id)}/draft`, {
        method: "POST",
        body: JSON.stringify(body)
      }).then(next => {
        draftDirtyRef.current = false;
        setDetail(next);
        void loadSurveys();
      }).catch(error => setMessage(error instanceof Error ? error.message : "Unable to autosave radio setup draft."));
    }, 600);
    return () => window.clearTimeout(timer);
  }, [wizardOpen, detail?.session.id, surveySystem, surveySystems.join("|"), sourcePlanSystems.join("|"), sourcePlanMode, radioReferenceSid, radioReferenceSites?.sites.length, surveySiteLabel, path, selectedSources, sdrSources, step, measurementMode, duration, configApplyInFlight]);

  useEffect(() => {
    if (!wizardOpen || !detail || configApplyInFlight) return;
    const selectedSystemNames = uniqueCaseInsensitive(surveySystems.length
      ? surveySystems
      : surveySystem ? [surveySystem] : detail.profile.systemShortNames?.length ? detail.profile.systemShortNames : detail.profile.systemShortName ? [detail.profile.systemShortName] : []);
    if (selectedSystemNames.length === 0)
      return;
    const sid = radioReferenceSid.trim() || detail.profile.radioReferenceSid || "";
    const selectedDefinitions = buildSurveySystemDefinitions(selectedSystemNames, scopePlan, radioReferenceSites ?? readCachedRadioReferenceSites(sid), detail.profile.systems ?? []);
    const selectedDefinitionNames = new Set(selectedDefinitions.map(system => system.shortName.toLowerCase()));
    const missingSelectedDefinitions = selectedSystemNames.some(name => !selectedDefinitionNames.has(name.toLowerCase()));
    if (missingSelectedDefinitions && sid && !radioReferenceSites?.sites.length && rrSitesAutoLoadKeyRef.current !== `resolve:${sid}`) {
      const cached = readCachedRadioReferenceSites(sid);
      if (cached) {
        rrSitesAutoLoadKeyRef.current = `resolve:${sid}`;
        setRadioReferenceSites(cached);
      } else if (!busy) {
        rrSitesAutoLoadKeyRef.current = `resolve:${sid}`;
        void api.request<SetupTrConfigSites>("/api/v1/setup/tr-config/sites", {
          method: "POST",
          body: JSON.stringify({ radioReferenceSid: sid })
        }).then(result => {
          writeCachedRadioReferenceSites(sid, result);
          setRadioReferenceSites(result);
        }).catch(() => {
          rrSitesAutoLoadKeyRef.current = "";
        });
      }
      return;
    }
    const currentSignature = surveySystemDefinitionsSignature(detail.profile.systems ?? []);
    const resolvedSignature = surveySystemDefinitionsSignature(selectedDefinitions);
    if (!selectedDefinitions.length || currentSignature === resolvedSignature)
      return;
    const repairKey = JSON.stringify({ id: detail.session.id, selectedSystemNames, resolvedSignature });
    if (siteDefinitionRepairKeyRef.current === repairKey)
      return;
    siteDefinitionRepairKeyRef.current = repairKey;
    const appliedSelectedSources = detail.profile.selectedSourceIndexes?.length
      ? detail.profile.selectedSourceIndexes
      : detail.profile.sources.map(source => source.index);
    void api.request<RfSurveyDetail>(`${radioSetupApi}/${encodeURIComponent(detail.session.id)}/draft`, {
      method: "POST",
      body: JSON.stringify({
        systemShortName: selectedSystemNames[0] ?? detail.profile.systemShortName,
        systemShortNames: selectedSystemNames,
        sourcePlanSystemShortNames: sourcePlanSystems.length ? sourcePlanSystems : selectedSystemNames,
        sourcePlanMode,
        systemDefinitions: selectedDefinitions,
        radioReferenceSid: sid || undefined,
        siteLabel: surveySiteLabel,
        rfPath: path,
        selectedSourceIndexes: selectedSources.length ? selectedSources : appliedSelectedSources,
        sdrSources: sdrSources ?? undefined,
        currentStep: step,
        measurementMode,
        probeDurationSeconds: Number(duration) || 45
      })
    }).then(next => {
      setDetail(next);
      draftDirtyRef.current = false;
      void loadSurveys();
    }).catch(error => setMessage(error instanceof Error ? error.message : "Unable to repair selected site definitions."));
  }, [wizardOpen, detail?.session.id, detail?.profile.systems, surveySystem, surveySystems.join("|"), sourcePlanSystems.join("|"), sourcePlanMode, radioReferenceSid, radioReferenceSites?.sites.length, surveySiteLabel, path, selectedSources, sdrSources, step, measurementMode, duration, busy, configApplyInFlight]);

  async function loadSurveys() {
    const next = await api.request<RfSurveyList>(radioSetupApi);
    setSurveys(next);
    if (!restoredWorkspaceRef.current) {
      restoredWorkspaceRef.current = true;
      const restoreId = localStorage.getItem("pizzawave-radio-setup-workspace");
      const restoreOpen = localStorage.getItem("pizzawave-radio-setup-wizard-open") === "1";
      if (restoreOpen && restoreId && next.sessions.some(row => row.id === restoreId))
        void openSurvey(restoreId, true);
    }
  }

  async function loadRadioSetupContext() {
    try {
      const status = await api.request<SetupStatus>("/api/v1/setup/status");
      const sid = String(status.values?.setup?.radioReferenceSid || status.values?.setup?.trRadioReferenceSid || status.values?.setup?.radioReferenceSystemId || "").trim();
      if (activeWorkspaceSystemsRef.current.length > 0)
        return;
      if (sid) {
        radioReferenceSidEditedRef.current = false;
        setRadioReferenceSid(sid);
        const cached = readCachedRadioReferenceSites(sid);
        setRadioReferenceSites(cached);
      } else {
        radioReferenceSidEditedRef.current = false;
        setRadioReferenceSites(null);
      }
    } catch {
      radioReferenceSidEditedRef.current = false;
      setRadioReferenceSid("");
      setRadioReferenceSites(null);
    }
  }

  function updateRadioReferenceSid(value: string) {
    markDraftDirty();
    radioReferenceSidEditedRef.current = true;
    rrSitesAutoLoadKeyRef.current = "";
    setRadioReferenceSid(value);
    const cached = readCachedRadioReferenceSites(value.trim());
    setRadioReferenceSites(cached);
    pruneSelectedSitesToRadioReferenceCatalog(cached);
  }

  function pruneSelectedSitesToRadioReferenceCatalog(sites: SetupTrConfigSites | null) {
    if (!sites)
      return;
    const allowed = new Set(sites.sites.flatMap(site => [site.shortName, site.name]).filter(Boolean).map(value => value.toLowerCase()));
    setSurveySystems(current => current.filter(name => allowed.has(name.toLowerCase())));
    setSurveySystem(current => allowed.has(current.toLowerCase()) ? current : "");
    setSourcePlanSystems(current => current.filter(name => allowed.has(name.toLowerCase())));
  }

  async function loadRadioReferenceSites(announce = true) {
    if (!radioReferenceSid.trim()) {
      setMessage("RadioReference SID is not available from setup. Enter it in first-run setup TR Config, or keep using live TR systems.");
      return;
    }
    setBusy("rr-sites");
    setMessage("");
    try {
      if (announce)
        markDraftDirty();
      const result = await api.request<SetupTrConfigSites>("/api/v1/setup/tr-config/sites", {
        method: "POST",
        body: JSON.stringify({ radioReferenceSid: radioReferenceSid.trim() })
      });
      setRadioReferenceSites(result);
      pruneSelectedSitesToRadioReferenceCatalog(result);
      writeCachedRadioReferenceSites(radioReferenceSid, result);
      if (announce)
        setMessage(`Loaded ${result.sites.length.toLocaleString()} RadioReference site(s) for SID ${radioReferenceSid}.`);
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Unable to load RadioReference sites.");
    } finally {
      setBusy("");
    }
  }

  async function adoptWaterfallSite(system: RfSurveySystem) {
    if (!detail || !system.shortName)
      return;
    const currentSystems = surveySystems.length
      ? surveySystems
      : detail.profile.systemShortNames?.length ? detail.profile.systemShortNames : detail.profile.systemShortName ? [detail.profile.systemShortName] : [];
    if (currentSystems.some(name => name.toLowerCase() === system.shortName.toLowerCase()))
      return;
    const nextSystems = [...currentSystems, system.shortName];
    const nextSourcePlanSystems = sourcePlanSystems.length ? Array.from(new Set([...sourcePlanSystems, system.shortName])) : nextSystems;
    const draftRadioReferenceSites = radioReferenceSites ?? readCachedRadioReferenceSites(radioReferenceSid.trim());
    const systemDefinitions = buildSurveySystemDefinitions(nextSystems, scopePlan, draftRadioReferenceSites, [...(detail.profile.systems ?? []), system]);
    setBusy("adopt-rr-site");
    setMessage("");
    try {
      const next = await api.request<RfSurveyDetail>(`${radioSetupApi}/${encodeURIComponent(detail.session.id)}/draft`, {
        method: "POST",
        body: JSON.stringify({
          systemShortName: nextSystems[0] ?? system.shortName,
          systemShortNames: nextSystems,
          sourcePlanSystemShortNames: nextSourcePlanSystems,
          sourcePlanMode,
          systemDefinitions,
          radioReferenceSid: radioReferenceSid.trim() || detail.profile.radioReferenceSid || undefined,
          siteLabel: surveySiteLabel,
          rfPath: path,
          selectedSourceIndexes: selectedSources.length ? selectedSources : detail.profile.selectedSourceIndexes,
          sdrSources: sdrSources ?? undefined,
          currentStep: step,
          measurementMode,
          probeDurationSeconds: Number(duration) || 45
        })
      });
      setDetail(next);
      setSurveySystem(nextSystems[0] ?? system.shortName);
      setSurveySystems(nextSystems);
      setSourcePlanSystems(nextSourcePlanSystems);
      draftDirtyRef.current = false;
      activeWorkspaceSystemsRef.current = nextSystems;
      await loadSurveys();
      setMessage(`Added ${system.siteLabel || system.shortName} to this RSW profile.`);
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Unable to add RR site to this RSW profile.");
    } finally {
      setBusy("");
    }
  }

  async function openSurvey(id: string, openWizard = true, targetStep?: number, configSubPage?: ConfigDraftSubpage) {
    setBusy(`open-${id}`);
    setMessage("");
    try {
      const next = await api.request<RfSurveyDetail>(radioSetupDetailUrl(id));
      setDetail(next);
      localStorage.setItem("pizzawave-radio-setup-workspace", next.session.id);
      localStorage.setItem("pizzawave-radio-setup-wizard-open", openWizard ? "1" : "0");
      if (configSubPage)
        localStorage.setItem(`pizzawave-radio-setup-config-subpage-${next.session.id}`, configSubPage);
      const restoredPath = normalizeRfPathProfile(next.profile.rfPath);
      const restoredRadioReferenceSid = (next.profile.radioReferenceSid || "").trim();
      setPath(restoredPath);
      setSelectedSources(next.profile.selectedSourceIndexes?.length ? next.profile.selectedSourceIndexes : next.profile.sources.map(source => source.index));
      setSdrSources(next.profile.sourceOverride ? next.profile.sources : null);
      setRfPathTouched(true);
      setSdrScopeTouched(true);
      setMeasurementMode((next.profile.measurementMode as any) || "guided");
      setDuration(String(next.profile.probeDurationSeconds || 45));
      const nextSystems = next.profile.systemShortNames?.length ? next.profile.systemShortNames : next.profile.systemShortName ? [next.profile.systemShortName] : [];
      activeWorkspaceSystemsRef.current = nextSystems;
      const nextSourcePlanSystems = next.profile.sourcePlanSystemShortNames?.length ? next.profile.sourcePlanSystemShortNames : nextSystems;
      const nextSourcePlanMode = next.profile.sourcePlanMode === "control" ? "control" : "full";
      const matchingRadioReferenceSites = radioReferenceSites && radioReferenceCatalogContainsAnySystem(radioReferenceSites, nextSystems);
      const signatureRadioReferenceSites = nextSystems.length > 0 && !matchingRadioReferenceSites ? null : radioReferenceSites;
      const systemDefinitions = buildSurveySystemDefinitions(nextSystems, scopePlan, signatureRadioReferenceSites, next.profile.systems ?? []);
      if (nextSystems.length > 0 && !matchingRadioReferenceSites) {
        radioReferenceSidEditedRef.current = false;
        rrSitesAutoLoadKeyRef.current = "";
        setRadioReferenceSid(restoredRadioReferenceSid);
        setRadioReferenceSites(null);
      } else {
        setRadioReferenceSid(restoredRadioReferenceSid);
      }
      setSurveySystem(nextSystems[0] ?? next.profile.systemShortName);
      setSurveySystems(nextSystems);
      setSourcePlanSystems(nextSourcePlanSystems);
      setSourcePlanMode(nextSourcePlanMode);
      setSurveySiteLabel(next.profile.siteLabel || next.session.siteLabel || next.profile.systemShortName);
      const savedStep = Number(localStorage.getItem(`pizzawave-radio-setup-step-v2-${next.session.id}`));
      const legacyStep = Number(localStorage.getItem(`pizzawave-radio-setup-step-${next.session.id}`));
      const legacyOrProfileStep = Number.isFinite(legacyStep) ? legacyStep : next.profile.currentStep || 0;
      const migratedLegacyStep = legacyOrProfileStep === 4 || legacyOrProfileStep === 5 ? 4 : legacyOrProfileStep > 5 ? legacyOrProfileStep - 1 : legacyOrProfileStep;
      const restoredStep = Math.max(0, Math.min(4, targetStep ?? (Number.isFinite(savedStep) ? savedStep : migratedLegacyStep)));
      autosaveSignatureRef.current = JSON.stringify({
        id: next.session.id,
        systemShortName: nextSystems[0] ?? next.profile.systemShortName,
        systemShortNames: nextSystems,
        sourcePlanSystemShortNames: nextSourcePlanSystems,
        sourcePlanMode: nextSourcePlanMode,
        systemDefinitions,
        radioReferenceSid: restoredRadioReferenceSid || undefined,
        siteLabel: next.profile.siteLabel || next.session.siteLabel || next.profile.systemShortName,
        rfPath: restoredPath,
        selectedSourceIndexes: next.profile.selectedSourceIndexes?.length ? next.profile.selectedSourceIndexes : next.profile.sources.map(source => source.index),
        sdrSources: next.profile.sourceOverride ? next.profile.sources : undefined,
        currentStep: restoredStep,
        measurementMode: (next.profile.measurementMode as any) || "guided",
        probeDurationSeconds: next.profile.probeDurationSeconds || 45
      });
      draftDirtyRef.current = false;
      setWizardOpen(openWizard);
      setStep(restoredStep);
      void loadScopePlan(nextSystems[0] ?? next.profile.systemShortName);
    } catch (error) {
      setMessage(error instanceof Error ? `Unable to open Radio Setup workspace: ${error.message}` : "Unable to open Radio Setup workspace.");
    } finally {
      setBusy("");
    }
  }

  async function applySurveyWorkspace(row: RfSurveyList["sessions"][number]) {
    if (!confirmAction("Apply Radio Setup workspace?", `This writes the ${row.siteLabel || row.systemShortName || row.id} TR config plan to the live trunk-recorder config, creates the normal backup, and restarts TR.`)) return;
    setBusy(`apply-${row.id}`);
    setMessage("");
    try {
      const draft = await api.request<RfSurveyConfigDraft>(`${radioSetupApi}/${encodeURIComponent(row.id)}/config-draft`);
      const result = await api.request<RfSurveyTrActionResult>(`${radioSetupApi}/${encodeURIComponent(row.id)}/tr/apply-source-draft`, {
        method: "POST",
        body: JSON.stringify({ configJson: draft.configJson, restartTr: true, preserveRfValidationEvidence: true })
      });
      setMessage(result.message + (result.serviceOutput ? ` ${result.serviceOutput}` : ""));
      await loadSurveys();
      if (detail?.session.id === row.id)
        await refreshDetail();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Unable to apply Radio Setup workspace.");
    } finally {
      setBusy("");
    }
  }

  async function applyCurrentConfigDraft() {
    if (!detail) return;
    if (!confirmAction("Apply Config Draft?", "This writes the current Radio Setup source plan to the live trunk-recorder config, removes stale live sources, creates the normal backup, and restarts TR.")) return;
    setConfigApplyInFlight(true);
    setBusy("config_apply");
    setMessage("");
    appendRunLog("Applying current Config Draft to live trunk-recorder...");
    try {
      const draft = await api.request<RfSurveyConfigDraft>(`${radioSetupApi}/${encodeURIComponent(detail.session.id)}/config-draft`);
      const result = await api.request<RfSurveyTrActionResult>(`${radioSetupApi}/${encodeURIComponent(detail.session.id)}/tr/apply-source-draft`, {
        method: "POST",
        body: JSON.stringify({ configJson: draft.configJson, restartTr: true, preserveRfValidationEvidence: true })
      });
      appendRunLog(result.message + (result.serviceOutput ? ` ${result.serviceOutput.trim()}` : ""), "result");
      setMessage(result.message + (result.serviceOutput ? ` ${result.serviceOutput}` : ""));
      await refreshDetail();
      await loadSurveys();
    } catch (error) {
      appendRunLog(error instanceof Error ? error.message : "Unable to apply Config Draft.", "error");
      setMessage(error instanceof Error ? error.message : "Unable to apply Config Draft.");
    } finally {
      setBusy("");
      setConfigApplyInFlight(false);
    }
  }

  async function exportSurvey(id: string) {
    setBusy(`export-${id}`);
    setMessage("");
    try {
      const response = await api.download(`${radioSetupApi}/${encodeURIComponent(id)}/export-plan/download`, { method: "POST" });
      const fileName = await downloadFileFromResponse(response, `radio-setup-${id}.md`);
      setMessage(`Downloaded ${fileName}`);
      await loadSurveys();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Export failed.");
    } finally {
      setBusy("");
    }
  }

  async function deleteSurvey(row: RfSurveyList["sessions"][number]) {
    if (!confirmAction("Delete radio setup workspace?", `This permanently deletes ${row.siteLabel || row.systemShortName || row.id} and its saved artifact folder.`)) return;
    setBusy(`delete-${row.id}`);
    setMessage("");
    try {
      await api.request(`${radioSetupApi}/${encodeURIComponent(row.id)}`, { method: "DELETE" });
      if (detail?.session.id === row.id) {
        setDetail(null);
        activeWorkspaceSystemsRef.current = [];
        setWizardOpen(false);
        localStorage.removeItem("pizzawave-radio-setup-workspace");
        localStorage.removeItem("pizzawave-radio-setup-wizard-open");
      }
      setMessage("Radio setup workspace deleted.");
      await loadSurveys();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Delete failed.");
    } finally {
      setBusy("");
    }
  }

  async function loadScopePlan(preferredSystem = surveySystem) {
    const plan = await api.request<SetupCalibrationPlan>(`${radioSetupApi}/source-plan`);
    setScopePlan(plan);
    if (!preferredSystem && plan.systems[0]) {
      setSurveySystem(plan.systems[0].shortName);
      setSurveySystems([plan.systems[0].shortName]);
    }
  }

  async function beginNewSurvey() {
    setBusy("create");
    setMessage("");
    try {
      const plan = await api.request<SetupCalibrationPlan>(`${radioSetupApi}/source-plan`);
      setScopePlan(plan);
      const initialPath = emptyRfPath();
      const created = await api.request<RfSurveyDetail>(radioSetupApi, {
        method: "POST",
        body: JSON.stringify({ siteLabel: "Radio Setup", radioReferenceSid: radioReferenceSid.trim() || undefined, mode: measurementMode, rfPath: initialPath, selectedSourceIndexes: [], currentStep: 0, measurementMode, probeDurationSeconds: Number(duration) || 45, systemShortNames: [] })
      });
      setDetail(created);
      localStorage.setItem("pizzawave-radio-setup-workspace", created.session.id);
      localStorage.setItem("pizzawave-radio-setup-wizard-open", "1");
      localStorage.setItem(`pizzawave-radio-setup-step-v2-${created.session.id}`, "0");
      setPath(initialPath);
      setSelectedSources(created.profile.selectedSourceIndexes ?? []);
      setSdrSources(created.profile.sourceOverride ? created.profile.sources : null);
      const createdSystems = created.profile.systemShortNames?.length ? created.profile.systemShortNames : created.profile.systemShortName ? [created.profile.systemShortName] : [];
      setSurveySystem(createdSystems[0] ?? created.profile.systemShortName);
      setSurveySystems(createdSystems);
      setSourcePlanSystems(created.profile.sourcePlanSystemShortNames?.length ? created.profile.sourcePlanSystemShortNames : createdSystems);
      setSourcePlanMode(created.profile.sourcePlanMode === "control" ? "control" : "full");
      setSurveySiteLabel(created.profile.siteLabel || created.session.siteLabel || created.profile.systemShortName);
      setRfPathTouched(false);
      setSdrScopeTouched(false);
      setWizardOpen(true);
      setStep(0);
      draftDirtyRef.current = false;
      await loadSurveys();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Unable to start radio setup.");
    } finally {
      setBusy("");
    }
  }

  async function runToolPrep() {
    if (!detail) return;
    setBusy("tool-prep");
    setMessage("");
    appendRunLog("Checking Radio Setup prerequisites...");
    try {
      await api.request(`${radioSetupApi}/${encodeURIComponent(detail.session.id)}/tool-prep`, { method: "POST" });
      await refreshDetail();
      appendRunLog("Prerequisite check complete.", "result");
      setMessage("Prerequisite check complete.");
    } catch (error) {
      appendRunLog(error instanceof Error ? error.message : "Prerequisite check failed.", "error");
      setMessage(error instanceof Error ? error.message : "Prerequisite check failed.");
    } finally {
      setBusy("");
    }
  }

  async function installDiagnosticTools() {
    setBusy("install-tools");
    setMessage("");
    appendRunLog("Starting optional diagnostic tool install job...");
    try {
      const job = await api.request<Job>("/api/v1/setup/jobs", { method: "POST", body: JSON.stringify({ action: "diagnostic-tools-prime", confirmed: true }) });
      await trackOperationJob(job);
      setMessage(`Diagnostic tool install started as job ${job.id}. Open System > Jobs for live logs.`);
    } catch (error) {
      appendRunLog(error instanceof Error ? error.message : "Unable to start diagnostic tool install.", "error");
      setMessage(error instanceof Error ? error.message : "Unable to start diagnostic tool install.");
    } finally {
      setBusy("");
    }
  }

  async function refreshDetail() {
    if (!detail) return;
    const next = await api.request<RfSurveyDetail>(radioSetupDetailUrl(detail.session.id));
    setDetail(next);
    setSelectedSources(next.profile.selectedSourceIndexes?.length ? next.profile.selectedSourceIndexes : next.profile.sources.map(source => source.index));
    setSdrSources(next.profile.sourceOverride ? next.profile.sources : null);
    const nextSystems = next.profile.systemShortNames?.length ? next.profile.systemShortNames : next.profile.systemShortName ? [next.profile.systemShortName] : [];
    setSourcePlanSystems(next.profile.sourcePlanSystemShortNames?.length ? next.profile.sourcePlanSystemShortNames : nextSystems);
    setSourcePlanMode(next.profile.sourcePlanMode === "control" ? "control" : "full");
    await loadSurveys();
  }

  async function loadPreviousRfPath() {
    if (!detail) return;
    setBusy("load-rf-path");
    setMessage("");
    try {
      const list = surveys ?? await api.request<RfSurveyList>(radioSetupApi);
      const candidates = [...list.sessions]
        .filter(row => row.id !== detail.session.id)
        .sort((a, b) => b.updatedAtUtc.localeCompare(a.updatedAtUtc));
      for (const session of candidates) {
        const previous = await api.request<RfSurveyDetail>(radioSetupDetailUrl(session.id));
        const previousPath = normalizeRfPathProfile(previous.profile.rfPath);
        if (!hasMeaningfulRfPath(previousPath))
          continue;
        setPath(previousPath);
        setRfPathTouched(true);
        setMessage(`Loaded RF path from ${previous.session.siteLabel || previous.profile.systemShortName || previous.session.id}.`);
        return;
      }
      setMessage("No previous workspace with a saved RF path was found.");
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Unable to load previous RF path.");
    } finally {
      setBusy("");
    }
  }

  async function stopTrAndInventory() {
    if (!detail) return;
    if (!confirmAction("Run SDR inventory?", "Live coverage pauses briefly while Radio Setup claims selected SDR hardware, then TR is restarted automatically.")) return;
    setBusy("inventory");
    setMessage("");
    onTrOperationChange("trunk-recorder is temporarily paused while Radio Setup inventories selected SDR hardware.");
    appendRunLog("Running SDR inventory with bounded TR pause...");
    try {
      const experiment = await api.request<RfSurveyExperiment>(`${radioSetupApi}/${encodeURIComponent(detail.session.id)}/experiments/run`, { method: "POST", body: JSON.stringify({ type: "sdr_inventory" }) });
      appendExperimentLog(experiment, appendRunLog);
      await refreshDetail();
      setMessage("SDR inventory completed and TR restart was handled automatically. Continue to the RF Power Scan step.");
    } catch (error) {
      appendRunLog(error instanceof Error ? error.message : "Inventory failed.", "error");
      setMessage(error instanceof Error ? error.message : "Inventory failed.");
    } finally {
      onTrOperationChange("");
      setBusy("");
    }
  }

  async function runP25(controlChannelHz?: number) {
    if (!detail) return;
    if (!confirmAction("Run P25 probe?", `Estimated time: ${duration || "45"} seconds. Radio Setup pauses TR only for the probe and restarts it afterward.`)) return;
    setBusy("p25");
    setMessage("");
    onTrOperationChange("trunk-recorder is temporarily paused while Radio Setup runs the P25 probe.");
    appendRunLog(`Running P25 probe${controlChannelHz ? ` at ${formatRfHz(controlChannelHz)}` : ""}...`);
    try {
      const sourceIndex = selectedSources.length === 1 ? selectedSources[0] : undefined;
      const experiment = await api.request<RfSurveyExperiment>(`${radioSetupApi}/${encodeURIComponent(detail.session.id)}/experiments/run`, {
        method: "POST",
        body: JSON.stringify({ type: "control_channel_p25_probe", controlChannelHz, durationSeconds: Number(duration) || 45, sourceIndex })
      });
      appendExperimentLog(experiment, appendRunLog);
      await refreshDetail();
      setMessage("P25 probe complete. Review the result summary before proceeding.");
    } catch (error) {
      appendRunLog(error instanceof Error ? error.message : "P25 probe failed.", "error");
      setMessage(error instanceof Error ? error.message : "P25 probe failed.");
    } finally {
      onTrOperationChange("");
      setBusy("");
    }
  }

  async function startTr() {
    setBusy("start-tr");
    appendRunLog("Requesting trunk-recorder restart...");
    try {
      const job = await api.request<Job>("/api/v1/setup/jobs", { method: "POST", body: JSON.stringify({ action: "restart-tr", confirmed: true }) });
      await trackOperationJob(job);
      setMessage("TR restart requested.");
    } catch (error) {
      appendRunLog(error instanceof Error ? error.message : "Unable to restart TR.", "error");
      setMessage(error instanceof Error ? error.message : "Unable to restart TR.");
    } finally {
      setBusy("");
    }
  }

  async function runSimpleExperiment(type: string, estimate: string, controlChannelHz?: number, extraRequest: Record<string, unknown> = {}) {
    if (!detail) return;
    const parameters = extraRequest.parameters as Record<string, unknown> | undefined;
    const skipConfirm = parameters?.waterfallIdentify === true;
    const quietWaterfallIdentify = parameters?.waterfallIdentify === true;
    if (!skipConfirm && type !== "control_channel_quality" && type !== "rf_power_scan" && type !== "rf_validation_sweep" && !["voice_capture_trial", "transcription_gate", "stability_verdict"].includes(type) && !confirmAction(`Run ${label(type)}?`, `Estimated time: ${estimate}.`)) return;
    setBusy(type);
    setMessage("");
    appendRunLog(`Running ${label(type)}...`);
    if (type === "rf_power_scan" || type === "rf_validation_sweep")
      onTrOperationChange("trunk-recorder is temporarily paused while Radio Setup runs RF validation.");
    try {
      const durationSeconds = type === "stability_verdict" ? 900 : type === "control_channel_quality" ? Number(duration) || 60 : type === "rf_power_scan" ? 5 : type === "rf_validation_sweep" ? 300 : 300;
      const experiment = await api.request<RfSurveyExperiment>(`${radioSetupApi}/${encodeURIComponent(detail.session.id)}/experiments/run`, { method: "POST", body: JSON.stringify({ type, durationSeconds, controlChannelHz, ...extraRequest }) });
      appendExperimentLog(experiment, appendRunLog);
      if (quietWaterfallIdentify) {
        // Waterfall P25 Identify renders status on the spectrum and detected-CC row.
      } else if (experiment.status === "failed" || experiment.status === "blocked")
        setMessage(experiment.blockingIssue || experiment.resultSummary || `${label(type)} ${label(experiment.status)}.`);
      else
        setMessage(`${label(type)} ${label(experiment.status)}.`);
      await refreshDetail();
      return experiment;
    } catch (error) {
      appendRunLog(error instanceof Error ? error.message : "Experiment failed.", "error");
      if (!quietWaterfallIdentify)
        setMessage(error instanceof Error ? error.message : "Experiment failed.");
      return undefined;
    } finally {
      if (type === "rf_power_scan" || type === "rf_validation_sweep")
        onTrOperationChange("");
      setBusy("");
    }
  }

  async function runCallQualityPipeline() {
    if (!detail) return;
    const stages = [
      { type: "voice_capture_trial", label: "Call capture", durationSeconds: 180 },
      { type: "transcription_gate", label: "Transcription", durationSeconds: 120 },
      { type: "stability_verdict", label: "Stability", durationSeconds: 300 }
    ];
    setBusy("call_quality");
    setMessage("");
    setCallQualityRunStartedAtUtc(new Date(Date.now() - 1000).toISOString());
    appendRunLog("Running Call Quality. Estimated time: about 5 minutes.");
    try {
      let stoppedAt: string | null = null;
      let stoppedReason = "";
      for (let index = 0; index < stages.length; index += 1) {
        const stage = stages[index];
        appendRunLog(`Running ${stage.label}...`);
        const experiment = await api.request<RfSurveyExperiment>(`${radioSetupApi}/${encodeURIComponent(detail.session.id)}/experiments/run`, {
          method: "POST",
          body: JSON.stringify({ type: stage.type, durationSeconds: stage.durationSeconds })
        });
        appendExperimentLog(experiment, appendRunLog);
        await refreshDetail();
        if (experiment.status !== "passed") {
          stoppedAt = stage.label;
          stoppedReason = experiment.blockingIssue || experiment.resultSummary || "";
          const skipped = stages.slice(index + 1).map(row => row.label);
          if (skipped.length)
            appendRunLog(`${stage.label} did not pass${stoppedReason ? `: ${stoppedReason}` : ""}. Skipped ${skipped.join(" and ")}.`, "result");
          break;
        }
      }
      setMessage(stoppedAt
        ? `Call Quality stopped at ${stoppedAt}${stoppedReason ? `: ${stoppedReason}` : ""}. Later gates were not run.`
        : "Call Quality run complete. Review the gate results below.");
    } catch (error) {
      appendRunLog(error instanceof Error ? error.message : "Call Quality run failed.", "error");
      setMessage(error instanceof Error ? error.message : "Call Quality run failed.");
    } finally {
      setBusy("");
    }
  }

  const sessions = surveys?.sessions ?? [];
  if (wizardOpen && detail) {
    return <RfSurveyWizard
      detail={detail}
      step={step}
      setStep={setStep}
      message={message}
      busy={busy}
      path={path}
      setPath={setPath}
      rfPathTouched={rfPathTouched}
      setRfPathTouched={(value) => { if (value) markDraftDirty(); setRfPathTouched(value); }}
      selectedSources={selectedSources}
      setSelectedSources={setSelectedSources}
      sdrSources={sdrSources}
      setSdrSources={setSdrSources}
      sdrScopeTouched={sdrScopeTouched}
      setSdrScopeTouched={(value) => { if (value) markDraftDirty(); setSdrScopeTouched(value); }}
      measurementMode={measurementMode}
      setMeasurementMode={(value) => { markDraftDirty(); setMeasurementMode(value); }}
      duration={duration}
      setDuration={(value) => { markDraftDirty(); setDuration(value); }}
      scopePlan={scopePlan}
      radioReferenceSid={radioReferenceSid}
      setRadioReferenceSid={updateRadioReferenceSid}
      radioReferenceSites={radioReferenceSites}
      surveySystem={surveySystem}
      surveySystems={surveySystems}
      sourcePlanSystems={sourcePlanSystems}
      setSourcePlanSystems={setSourcePlanSystems}
      sourcePlanMode={sourcePlanMode}
      setSourcePlanMode={setSourcePlanMode}
      setSurveySystem={(value) => {
        const previous = surveySystem;
        markDraftDirty();
        setSurveySystem(value);
        setSurveySystems(value ? [value] : []);
        setSourcePlanSystems(value ? [value] : []);
        setSourcePlanMode("full");
        setSurveySiteLabel(current => !current || current === previous ? value : current);
        setSdrScopeTouched(true);
        const selected = scopePlan?.systems.find(system => system.shortName === value)?.proposedSourceIndexes ?? [];
        setSelectedSources(selected);
      }}
      setSurveySystems={(values) => {
        const previous = surveySystems.length ? surveySystems.join(", ") : surveySystem;
        const next = uniqueCaseInsensitive(values);
        markDraftDirty();
        setSurveySystems(next);
        setSurveySystem(next[0] ?? "");
        setSourcePlanSystems(next);
        setSourcePlanMode("full");
        setSurveySiteLabel(current => !current || current === previous ? next.join(", ") : current);
        setSdrScopeTouched(true);
        const proposed = scopePlan?.systems
          .filter(system => next.includes(system.shortName))
          .flatMap(system => system.proposedSourceIndexes) ?? [];
        setSelectedSources(Array.from(new Set(proposed)).sort((a, b) => a - b));
      }}
      surveySiteLabel={surveySiteLabel}
      setSurveySiteLabel={(value) => { markDraftDirty(); setSurveySiteLabel(value); }}
      setScopePlan={setScopePlan}
      onClose={() => { setWizardOpen(false); localStorage.setItem("pizzawave-radio-setup-wizard-open", "0"); void loadSurveys(); }}
      onLoadScopePlan={loadScopePlan}
      onLoadRadioReferenceSites={loadRadioReferenceSites}
      onToolPrep={runToolPrep}
      onInstallTools={installDiagnosticTools}
      onStopAndInventory={stopTrAndInventory}
      onRunP25={runP25}
      onRunExperiment={runSimpleExperiment}
      onAdoptWaterfallSite={adoptWaterfallSite}
      onRunCallQuality={runCallQualityPipeline}
      onApplyConfigDraft={applyCurrentConfigDraft}
      onConfigApplyStateChange={setConfigApplyInFlight}
      callQualityRunStartedAtUtc={callQualityRunStartedAtUtc}
      onLoadPreviousRfPath={loadPreviousRfPath}
      onReload={loadSurveys}
      onRefreshDetail={refreshDetail}
      onOpenTalkgroups={onOpenTalkgroups}
      onShowDetails={setDetails}
      details={details}
      setDetails={setDetails}
      runLogs={runLogs}
      operationJob={operationJob}
      operationJobLogs={operationJobLogs}
      onClearRunLogs={() => {
        setRunLogs([]);
        setOperationJob(null);
        setOperationJobLogs([]);
        operationLogLastId.current = 0;
      }}
      runLogOpen={runLogOpen}
      setRunLogOpen={setRunLogOpen}
      onOpenRunLog={() => setRunLogOpen(true)}
    />;
  }

  return <div className="rf-tool-page">
    <div className="rf-tool-title">
      <div>
        <h3>Radio Setup Workspaces</h3>
        <button className="danger-button" disabled={busy === "create"} onClick={() => void beginNewSurvey()}>{busy === "create" ? "Starting..." : "New Workspace"}</button>
      </div>
    </div>
    {message && <div className="setup-note">{message}</div>}
    <div className="rf-survey-table-wrap">
      <table className="table rf-survey-table">
        <thead><tr><th>Updated</th><th>Name</th><th>Status</th><th>Verdict</th><th>RF Path</th><th>Actions</th></tr></thead>
        <tbody>
          {!sessions.length && <tr><td colSpan={6}>No radio setup workspaces yet.</td></tr>}
          {sessions.map(row => {
            const canApply = row.status !== "draft" && row.verdict === "pass_candidate";
            const rowBusy = busy.endsWith(`-${row.id}`);
            return <tr key={row.id}>
              <td>{new Date(row.updatedAtUtc).toLocaleDateString()}<br /><span className="muted">{new Date(row.updatedAtUtc).toLocaleTimeString()}</span></td>
              <td><strong>{row.siteLabel || row.systemShortName || row.id}</strong><br /><span className="muted">{row.sdrSummary}</span></td>
              <td>{label(row.status)}</td>
              <td>{label(row.verdict)}<br /><span className="muted">{label(row.stability)}</span></td>
              <td>{row.rfPathSummary || row.bestControlChannel || "--"}</td>
              <td><div className="table-actions">
                <button disabled={rowBusy} onClick={() => void openSurvey(row.id)}>Open</button>
                <button disabled={rowBusy || busy === `export-${row.id}`} onClick={() => void exportSurvey(row.id)}>{busy === `export-${row.id}` ? "Exporting" : "Export"}</button>
                <button disabled={!canApply || rowBusy} onClick={() => void applySurveyWorkspace(row)} title={canApply ? "Apply this workspace's TR config plan to the live trunk-recorder config." : "Apply is available only after a non-draft survey has a pass candidate."}>{busy === `apply-${row.id}` ? "Applying" : "Apply"}</button>
                <button className="danger-button" disabled={rowBusy} onClick={() => void deleteSurvey(row)}>{busy === `delete-${row.id}` ? "Deleting" : "Delete"}</button>
              </div></td>
            </tr>;
          })}
        </tbody>
      </table>
    </div>
  </div>;
}

function buildSurveySystemDefinitions(selectedNames: string[], scopePlan: SetupCalibrationPlan | null, rrSites: SetupTrConfigSites | null, existing: RfSurveySystem[]): RfSurveySystem[] {
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
        voiceFrequenciesHz: []
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

function RfSurveyWizard({
  detail,
  step,
  setStep,
  message,
  busy,
  path,
  setPath,
  rfPathTouched,
  setRfPathTouched,
  selectedSources,
  setSelectedSources,
  sdrSources,
  setSdrSources,
  sdrScopeTouched,
  setSdrScopeTouched,
  measurementMode,
  setMeasurementMode,
  duration,
  setDuration,
  scopePlan,
  radioReferenceSid,
  setRadioReferenceSid,
  radioReferenceSites,
  surveySystem,
  surveySystems,
  sourcePlanSystems,
  setSourcePlanSystems,
  sourcePlanMode,
  setSourcePlanMode,
  setSurveySystem,
  setSurveySystems,
  surveySiteLabel,
  setSurveySiteLabel,
  onClose,
  onLoadScopePlan,
  onLoadRadioReferenceSites,
  onToolPrep,
  onInstallTools,
  onStopAndInventory,
  onRunP25,
  onRunExperiment,
  onAdoptWaterfallSite,
  onRunCallQuality,
  onApplyConfigDraft,
  onConfigApplyStateChange,
  callQualityRunStartedAtUtc,
  onLoadPreviousRfPath,
  onReload,
  onRefreshDetail,
  onOpenTalkgroups,
  onShowDetails,
  details,
  setDetails,
  runLogs,
  operationJob,
  operationJobLogs,
  onClearRunLogs,
  runLogOpen,
  setRunLogOpen,
  onOpenRunLog
}: {
  detail: RfSurveyDetail;
  step: number;
  setStep: (value: number) => void;
  message: string;
  busy: string;
  path: RfSurveyPathProfile;
  setPath: React.Dispatch<React.SetStateAction<RfSurveyPathProfile>>;
  rfPathTouched: boolean;
  setRfPathTouched: (value: boolean) => void;
  selectedSources: number[];
  setSelectedSources: React.Dispatch<React.SetStateAction<number[]>>;
  sdrSources: RfSurveySource[] | null;
  setSdrSources: React.Dispatch<React.SetStateAction<RfSurveySource[] | null>>;
  sdrScopeTouched: boolean;
  setSdrScopeTouched: (value: boolean) => void;
  measurementMode: "guided" | "desk" | "expert";
  setMeasurementMode: (value: "guided" | "desk" | "expert") => void;
  duration: string;
  setDuration: (value: string) => void;
  scopePlan: SetupCalibrationPlan | null;
  radioReferenceSid: string;
  setRadioReferenceSid: (value: string) => void;
  radioReferenceSites: SetupTrConfigSites | null;
  surveySystem: string;
  surveySystems: string[];
  sourcePlanSystems: string[];
  setSourcePlanSystems: React.Dispatch<React.SetStateAction<string[]>>;
  sourcePlanMode: "full" | "control";
  setSourcePlanMode: React.Dispatch<React.SetStateAction<"full" | "control">>;
  setSurveySystem: (value: string) => void;
  setSurveySystems: (values: string[]) => void;
  surveySiteLabel: string;
  setSurveySiteLabel: (value: string) => void;
  setScopePlan: (value: SetupCalibrationPlan | null) => void;
  onClose: () => void;
  onLoadScopePlan: () => Promise<void>;
  onLoadRadioReferenceSites: () => Promise<void>;
  onToolPrep: () => Promise<void>;
  onInstallTools: () => Promise<void>;
  onStopAndInventory: () => Promise<void>;
  onRunP25: (controlChannelHz?: number) => Promise<void>;
  onRunExperiment: (type: string, estimate: string, controlChannelHz?: number, extraRequest?: Record<string, unknown>) => Promise<RfSurveyExperiment | undefined>;
  onAdoptWaterfallSite: (system: RfSurveySystem) => Promise<void>;
  onRunCallQuality: () => Promise<void>;
  onApplyConfigDraft: () => Promise<void>;
  onConfigApplyStateChange: (value: boolean) => void;
  callQualityRunStartedAtUtc: string;
  onLoadPreviousRfPath: () => Promise<void>;
  onReload: () => Promise<void>;
  onRefreshDetail: () => Promise<void>;
  onOpenTalkgroups: () => void;
  onShowDetails: (value: { title: string; body: React.ReactNode } | null) => void;
  details: { title: string; body: React.ReactNode } | null;
  setDetails: (value: { title: string; body: React.ReactNode } | null) => void;
  runLogs: RfRunLogLine[];
  operationJob: Job | null;
  operationJobLogs: JobLog[];
  onClearRunLogs: () => void;
  runLogOpen: boolean;
  setRunLogOpen: (open: boolean) => void;
  onOpenRunLog: () => void;
}) {
  const steps = [
    { title: "Prerequisites", goal: "Confirm the rig has the SDR, P25, TR, and support tools needed before any RF validation starts." },
    { title: "Sites", goal: "Choose every site of interest so Radio Setup can test whether each one is monitorable from this RF path." },
    { title: "RF Path", goal: "Document the hardware chain, inventory SDRs, and learn which selected sites have usable control channels." },
    { title: "Config Draft", goal: "Turn the site-readiness evidence into a concrete TR source plan and apply it to trunk-recorder." },
    { title: "Call Quality", goal: "Validate the applied source plan with real calls when traffic exists, or carry the healthy-TR no-traffic caveat." }
  ];
  const controlChannelStorageKey = `pizzawave-radio-setup-active-cc-${detail.session.id}`;
  const [activeControlChannelHz, setActiveControlChannelHzState] = useState(() => {
    const saved = Number(localStorage.getItem(controlChannelStorageKey));
    return detail.profile.controlChannelsHz.includes(saved) ? saved : detail.profile.controlChannelsHz[0] ?? 0;
  });
  const callQualityRefreshRef = useRef("");
  useEffect(() => {
    setActiveControlChannelHzState(current => {
      const saved = Number(localStorage.getItem(controlChannelStorageKey));
      const next = detail.profile.controlChannelsHz.includes(current)
        ? current
        : detail.profile.controlChannelsHz.includes(saved)
        ? saved
        : detail.profile.controlChannelsHz[0] ?? 0;
      if (next) localStorage.setItem(controlChannelStorageKey, String(next));
      return next;
    });
  }, [controlChannelStorageKey, detail.profile.controlChannelsHz.join(",")]);
  const callQualityPlan = detail.nextExperiments?.find(plan => plan.type === "voice_capture_trial");
  const callQualityReadinessKey = [
    detail.session.id,
    detail.session.status,
    detail.session.sourcePlanSummary,
    callQualityPlan?.enabled === true ? "enabled" : "disabled",
    callQualityPlan?.blockingIssue || ""
  ].join("|");
  useEffect(() => {
    if (step !== 4) {
      callQualityRefreshRef.current = "";
      return;
    }
    if (callQualityRefreshRef.current === callQualityReadinessKey)
      return;
    callQualityRefreshRef.current = callQualityReadinessKey;
    void onRefreshDetail();
  }, [step, callQualityReadinessKey, onRefreshDetail]);
  const setActiveControlChannelHz = (value: number) => {
    setActiveControlChannelHzState(value);
    if (value) localStorage.setItem(controlChannelStorageKey, String(value));
  };
  const latest = (type: string) => [...detail.experiments].filter(row => row.type === type).sort((a, b) => a.createdAtUtc.localeCompare(b.createdAtUtc)).at(-1);
  const history = (type: string) => [...detail.experiments].filter(row => row.type === type).sort((a, b) => b.createdAtUtc.localeCompare(a.createdAtUtc));
  const latestForControlChannel = (type: string) => history(type).find(row => experimentMatchesControlChannel(row, activeControlChannelHz));
  const staleForControlChannel = (type: string) => history(type).find(row => !experimentMatchesControlChannel(row, activeControlChannelHz) && experimentControlChannelHz(row) != null);
  const toolReady = detail.toolPrep?.readyForControlChannelTests === true;
  const ccQuality = latestForControlChannel("control_channel_quality");
  const ccQualityRuns = history("control_channel_quality");
  const staleCcQuality = ccQuality ? undefined : staleForControlChannel("control_channel_quality");
  const inventory = latest("sdr_inventory");
  const powerScan = latestForControlChannel("rf_power_scan");
  const validationSweep = latest("rf_validation_sweep");
  const selectedWorkflowSystemNames = surveySystems.length
    ? surveySystems
    : surveySystem ? [surveySystem] : detail.profile.systemShortNames?.length ? detail.profile.systemShortNames : detail.profile.systemShortName ? [detail.profile.systemShortName] : [];
  const effectiveRadioReferenceSites = radioReferenceSites ?? readCachedRadioReferenceSites(radioReferenceSid);
  const effectiveWorkflowSystems = selectedWorkflowSystemNames.length
    ? buildSurveySystemDefinitions(selectedWorkflowSystemNames, scopePlan, effectiveRadioReferenceSites, detail.profile.systems ?? [])
    : detail.profile.systems ?? [];
  const effectiveWorkflowControlChannels = uniqueSortedFrequencies(effectiveWorkflowSystems.flatMap(system => system.controlChannelsHz));
  const effectiveControlChannels = effectiveWorkflowControlChannels.length ? effectiveWorkflowControlChannels : detail.profile.controlChannelsHz;
  const validationSweepStatus = rfValidationEffectiveStatus(validationSweep, effectiveWorkflowSystems);
  const p25 = latestForControlChannel("control_channel_p25_probe");
  const sweep = latestForControlChannel("error_gain_sweep");
  const stalePowerScan = powerScan ? undefined : staleForControlChannel("rf_power_scan");
  const staleP25 = p25 ? undefined : staleForControlChannel("control_channel_p25_probe");
  const staleSweep = sweep ? undefined : staleForControlChannel("error_gain_sweep");
  const voice = latest("voice_capture_trial");
  const transcription = latest("transcription_gate");
  const stability = latest("stability_verdict");
  const afterCallQualityRunStart = (experiment?: RfSurveyExperiment) => {
    if (!experiment || !callQualityRunStartedAtUtc)
      return experiment;
    const runStarted = Date.parse(callQualityRunStartedAtUtc);
    const experimentCreated = Date.parse(experiment.createdAtUtc);
    if (!Number.isFinite(runStarted) || !Number.isFinite(experimentCreated))
      return experiment;
    return experimentCreated >= runStarted ? experiment : undefined;
  };
  const displayVoice = afterCallQualityRunStart(voice);
  const displayTranscription = afterCallQualityRunStart(transcription);
  const displayStability = afterCallQualityRunStart(stability);
  const rfPathEntered = rfPathTouched && (path.antennaMount !== "unknown" || path.antennaPolarization !== "unknown" || path.aimedAtSite !== "unknown" || path.chain.some(item =>
    item.type !== "antenna" && item.type !== "sdr" ||
    !["", "Yagi", "Configured SDR"].includes(item.label.trim()) ||
    !["", "unknown"].includes(item.connectorIn.trim()) ||
    !["", "unknown"].includes(item.connectorOut.trim()) ||
    item.length.trim() ||
    item.loss.trim() ||
    item.power.trim() ||
    item.notes.trim()));
  const sdrScopeSelected = sdrScopeTouched && selectedSources.length > 0 || Boolean(inventory || p25 || voice || transcription || stability);
  const scopeHasDependentResults = Boolean(ccQuality || inventory || powerScan || p25 || sweep || voice || transcription || stability);
  const sourcePlanApplied = Boolean(
    detail.session.sourcePlanSummary ||
    detail.session.status === "source_plan_applied" ||
    detail.session.status === "completed" ||
    callQualityPlan?.enabled === true);
  const rfPathValidationComplete = validationSweepStatus === "passed" || powerScan?.status === "passed" && p25?.status === "passed" && ccQuality?.status === "passed";
  const openConfigDraftReview = () => {
    localStorage.setItem(`pizzawave-radio-setup-config-subpage-${detail.session.id}`, "review");
    setStep(3);
  };
  const stepComplete = [
    toolReady,
    Boolean(detail.profile.systemShortName && detail.profile.controlChannelsHz.length > 0),
    rfPathValidationComplete,
    sourcePlanApplied,
    voice?.status === "passed" && transcription?.status === "passed" && stability?.status === "passed"
  ];
  const currentStep = steps[step] ?? steps[0];
  return <div className="rf-wizard-page">
    <div className="rf-wizard-top">
      <div>
        <button className="link-button" onClick={onClose}>Back to workspaces</button>
        <label className="rf-workspace-name-field">
          <span>Name</span>
          <input
            aria-label="Workspace name"
            value={surveySiteLabel}
            onChange={event => setSurveySiteLabel(event.target.value)}
            onBlur={() => {
              if (!surveySiteLabel.trim())
                setSurveySiteLabel(detail.session.siteLabel || detail.session.id);
            }}
            placeholder={detail.session.siteLabel || detail.session.id}
          />
        </label>
        <p>{detail.session.id} / {(detail.profile.systemShortNames?.length ? detail.profile.systemShortNames.join(", ") : detail.profile.systemShortName) || "no site selected"}</p>
      </div>
    </div>
    <div className="rf-workspace-note">
      <strong>Radio Setup Lifecycle</strong>
      <span>Prerequisites and site selection establish the RF target. RF path validation happens before source planning inside Config Draft, unless this is an already-stable rig.</span>
    </div>
    <div className="rf-wizard-progress" aria-label="Radio setup phases">
      {steps.map((phase, index) => {
        const complete = stepComplete[index] === true;
        const phaseClass = `${index === step ? "active" : ""} ${complete ? "done" : ""}`.trim();
        return <button key={phase.title} className={phaseClass} onClick={() => setStep(index)} aria-current={index === step ? "step" : undefined}>
          <span className="rf-phase-mark">{complete ? <CheckCircle2 size={13} /> : index + 1}</span>
          <span>{phase.title}</span>
        </button>;
      })}
      <button className="danger-button rf-save-exit" onClick={onClose}>Save and Exit</button>
    </div>
    <div className="rf-wizard-body">
      <section className="rf-wizard-step">
        <div className="rf-step-head">
          <div>
            <h3>{currentStep.title}</h3>
            <span>{currentStep.goal}</span>
          </div>
        </div>
        {message && <div className="setup-note">{message}</div>}
        {step === 0 && <PrereqStep detail={detail} busy={busy} onToolPrep={onToolPrep} onInstallTools={onInstallTools} onShowDetails={onShowDetails} onOpenRunLog={onOpenRunLog} />}
        {step === 1 && <ScopeStep detail={detail} scopePlan={scopePlan} radioReferenceSid={radioReferenceSid} setRadioReferenceSid={setRadioReferenceSid} radioReferenceSites={radioReferenceSites} surveySystem={surveySystem} surveySystems={surveySystems} setSurveySystem={setSurveySystem} setSurveySystems={setSurveySystems} surveySiteLabel={surveySiteLabel} setSurveySiteLabel={setSurveySiteLabel} onTouched={() => setSdrScopeTouched(true)} scopeHasDependentResults={scopeHasDependentResults} onLoadRadioReferenceSites={onLoadRadioReferenceSites} busy={busy} />}
        {step === 2 && <RfPathRefinementStep path={path} setPath={setPath} onRfPathTouched={() => setRfPathTouched(true)} onLoadPreviousRfPath={onLoadPreviousRfPath} busy={busy} ccQuality={ccQuality} staleCcQuality={staleCcQuality} ccQualityRuns={ccQualityRuns} inventory={inventory} powerScan={powerScan} validationSweep={validationSweep} stalePowerScan={stalePowerScan} p25={p25} staleP25={staleP25} sweep={sweep} staleSweep={staleSweep} nextExperiments={detail.nextExperiments ?? []} surveyId={detail.session.id} systemShortName={selectedWorkflowSystemNames[0] ?? detail.profile.systemShortName} systems={effectiveWorkflowSystems} radioReferenceSites={radioReferenceSites} sources={sdrSources ?? detail.profile.sources} setSdrSources={setSdrSources} onSdrTouched={() => setSdrScopeTouched(true)} controlChannels={effectiveControlChannels} activeControlChannelHz={activeControlChannelHz} setActiveControlChannelHz={setActiveControlChannelHz} duration={duration} setDuration={setDuration} selectedSources={selectedSources} onStopAndInventory={onStopAndInventory} onRunP25={onRunP25} onRunExperiment={onRunExperiment} onAdoptWaterfallSite={onAdoptWaterfallSite} onReload={onReload} onShowDetails={onShowDetails} onOpenRunLog={onOpenRunLog} />}
        {step === 3 && <ConfigDraftStep reload={onReload} detail={detail} selectedSources={selectedSources} setSelectedSources={setSelectedSources} sourcePlanSystems={sourcePlanSystems} setSourcePlanSystems={setSourcePlanSystems} sourcePlanMode={sourcePlanMode} setSourcePlanMode={setSourcePlanMode} sdrSources={sdrSources} setSdrSources={setSdrSources} onSdrTouched={() => setSdrScopeTouched(true)} onApplyStateChange={onConfigApplyStateChange} onApplied={async () => { await onRefreshDetail(); setStep(4); }} />}
        {step === 4 && <CallQualityStep busy={busy} voice={displayVoice} transcription={displayTranscription} stability={displayStability} sourcePlanApplied={sourcePlanApplied} sourcePlanSummary={detail.session.sourcePlanSummary} callQualityEnabled={callQualityPlan?.enabled === true} callQualityBlockingIssue={callQualityPlan?.blockingIssue || ""} resetActive={Boolean(callQualityRunStartedAtUtc && busy === "call_quality")} onOpenConfigDraftReview={openConfigDraftReview} onApplyConfigDraft={onApplyConfigDraft} onRun={onRunCallQuality} />}
      </section>
    </div>
    {details && <div className="modal-backdrop" onClick={() => setDetails(null)}>
      <div className="rf-details-modal" onClick={event => event.stopPropagation()}>
        <div className="settings-header"><h3>{details.title}</h3><button onClick={() => setDetails(null)}>Close</button></div>
        {details.body}
      </div>
    </div>}
    <RfRunLogPane detail={detail} logs={runLogs} job={operationJob} jobLogs={operationJobLogs} onClear={onClearRunLogs} open={runLogOpen} setOpen={setRunLogOpen} />
  </div>;
}

function PrereqStep({
  detail,
  busy,
  onToolPrep,
  onInstallTools,
  onShowDetails,
  onOpenRunLog
}: {
  detail: RfSurveyDetail;
  busy: string;
  onToolPrep: () => Promise<void>;
  onInstallTools: () => Promise<void>;
  onShowDetails: (value: { title: string; body: React.ReactNode } | null) => void;
  onOpenRunLog: () => void;
}) {
  const tools = detail.toolPrep?.tools ?? [];
  const hasToolCheck = (detail.toolPrep?.tools ?? []).length > 0;
  const ready = detail.toolPrep?.readyForControlChannelTests === true;
  const warnings = detail.toolPrep?.warnings ?? [];
  const missingTools = tools.filter(tool => !tool.installed);
  const missingRequired = missingTools.filter(tool => tool.required);
  const needsToolPrep = hasToolCheck && (missingTools.length > 0 || warnings.length > 0);
  return <div className="rf-step-stack">
    <p>Check SDR, P25, transcription, and optional diagnostic tooling before choosing sites or running live validation.</p>
    <div className={ready ? "setup-note" : hasToolCheck ? "setup-warning-list" : "setup-note"}>
      {ready
        ? "Prerequisites are ready for site selection and control-channel tests."
        : hasToolCheck
          ? warnings[0] || "Prerequisites need attention before guided radio setup can proceed."
          : "Start with a prerequisite check."}
    </div>
    <div className="rf-primary-actions">
      <button className={needsToolPrep ? undefined : "danger-button"} disabled={Boolean(busy)} onClick={() => void onToolPrep()}>{busy === "tool-prep" ? "Checking..." : hasToolCheck ? "Recheck" : "Begin Check"}</button>
      {hasToolCheck && <button onClick={() => onShowDetails({ title: "Prerequisite Check Details", body: <pre className="log-box">{JSON.stringify({ ready: detail.toolPrep, missingRequired, missingTools, warnings, tools }, null, 2)}</pre> })}>Details</button>}
      <button onClick={onOpenRunLog}>Run Log</button>
      {hasToolCheck && missingTools.length === 0 && warnings.length === 0 && <span className="muted">No tool prep needed</span>}
      {needsToolPrep && <button className="danger-button" disabled={Boolean(busy)} onClick={() => void onInstallTools()}>{busy === "install-tools" ? "Starting..." : "Prepare Tools"}</button>}
    </div>
    {hasToolCheck && <div className="rf-tool-status-list compact">
      {tools.map(tool => <div className="rf-tool-row" key={tool.id}>
        <strong>{tool.label}</strong>
        <span className={tool.installed ? "section-status ok" : tool.required ? "section-status error" : "section-status"}>{tool.installed ? "Ready" : tool.required ? "Required" : "Optional"}</span>
        <span>{tool.purpose || tool.id}</span>
      </div>)}
    </div>}
  </div>;
}

function RfPathStep({ path, setPath, onTouched, onLoadPrevious, busy }: { path: RfSurveyPathProfile; setPath: React.Dispatch<React.SetStateAction<RfSurveyPathProfile>>; onTouched: () => void; onLoadPrevious: () => Promise<void>; busy: string }) {
  const updateChain = (index: number, patch: Partial<RfSurveyPathProfile["chain"][number]>) => { onTouched(); setPath(current => ({ ...current, chain: current.chain.map((item, i) => i === index ? { ...item, ...patch } : item) })); };
  const newChainItem = (): RfSurveyPathProfile["chain"][number] => ({ type: "lna", label: "", connectorIn: "", connectorOut: "", length: "", loss: "", power: "", notes: "", connectorInType: "unknown", connectorInGender: "unknown", connectorOutType: "unknown", connectorOutGender: "unknown", powerMethod: "unknown" });
  return <div className="rf-step-stack">
    <div className="rf-chain-head">
      <div><strong>Ordered RF Chain</strong><span>Capture the exact hardware path from antenna to SDR. Order matters.</span></div>
      <div className="rf-primary-actions">
        <button disabled={busy === "load-rf-path"} onClick={() => void onLoadPrevious()}>{busy === "load-rf-path" ? "Loading..." : "Load Previous"}</button>
        <button onClick={() => { onTouched(); setPath(current => ({ ...current, chain: [...current.chain, newChainItem()] })); }}>Add Chain Item</button>
      </div>
    </div>
    <div className="setup-note">Use RF Path to document the physical antenna/coax/filter/SDR chain. Use SDR Inventory to choose hardware. Use RF Sweep to prove which source, control channel, gain, and error settings can decode before Config Draft builds the monitoring plan.</div>
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

function ScopeStep({ detail, scopePlan, radioReferenceSid, setRadioReferenceSid, radioReferenceSites, surveySystem, surveySystems, setSurveySystem, setSurveySystems, surveySiteLabel, setSurveySiteLabel, onTouched, scopeHasDependentResults, onLoadRadioReferenceSites, busy }: { detail: RfSurveyDetail; scopePlan: SetupCalibrationPlan | null; radioReferenceSid: string; setRadioReferenceSid: (value: string) => void; radioReferenceSites: SetupTrConfigSites | null; surveySystem: string; surveySystems: string[]; setSurveySystem: (value: string) => void; setSurveySystems: (values: string[]) => void; surveySiteLabel: string; setSurveySiteLabel: (value: string) => void; onTouched: () => void; scopeHasDependentResults: boolean; onLoadRadioReferenceSites: () => Promise<void>; busy: string }) {
  const [siteSearch, setSiteSearch] = useState("");
  const effectiveSystems = surveySystems.length
    ? surveySystems
    : detail.profile.systemShortNames?.length ? detail.profile.systemShortNames : surveySystem || detail.profile.systemShortName ? [surveySystem || detail.profile.systemShortName] : [];
  const [draftSystems, setDraftSystems] = useState<string[]>(effectiveSystems);
  const effectiveSystemsKey = effectiveSystems.join("|");
  useEffect(() => setDraftSystems(effectiveSystems), [effectiveSystemsKey]);
  const rrCatalogLoaded = Boolean(radioReferenceSites);
  const liveSystems = scopePlan?.systems.map(system => ({
    shortName: system.shortName,
    label: system.shortName,
    controlCount: system.controlChannelsHz.length,
    voiceCount: system.voiceFrequenciesHz.length,
    controlText: system.controlChannelsHz.map(formatRfHz).join(" "),
    proposedSourceIndexes: system.proposedSourceIndexes
  })) ?? [];
  const rrSystems = (radioReferenceSites?.sites ?? [])
    .map(site => ({
      shortName: site.shortName || site.name,
      label: site.name || site.shortName,
      controlCount: site.controlChannelCount,
      voiceCount: Math.max(0, site.frequencyCount - site.controlChannelCount),
      controlText: site.controlChannelsMhz.map(formatMhz).join(" "),
      proposedSourceIndexes: [] as number[]
    }));
  const siteCandidates = rrCatalogLoaded ? rrSystems : liveSystems;
  const visibleDraftSystems = draftSystems;
  const draftSystemSet = new Set(visibleDraftSystems);
  const confirmScopeChange = () =>
    !scopeHasDependentResults || confirmAction("Change selected sites?", "Changing selected sites clears saved RF measurements, error/gain sweep state, and call-quality results for this workspace.");
  const changeSystems = (next: string[]) => {
    if (!confirmScopeChange()) return;
    const normalized = uniqueCaseInsensitive(next.filter(Boolean));
    onTouched();
    setSurveySystems(normalized);
  };
  const toggleSystem = (shortName: string) => {
    setDraftSystems(current => current.some(name => name.toLowerCase() === shortName.toLowerCase())
      ? current.filter(name => name !== shortName)
      : uniqueCaseInsensitive([...effectiveSystems, ...current, shortName]));
  };
  const applySystemSelection = () => changeSystems(draftSystems);
  const query = siteSearch.trim().toLowerCase();
  const filteredSystems = siteCandidates.filter(system => !query || [
    system.shortName,
    system.label,
    system.controlText
  ].some(value => value.toLowerCase().includes(query)));
  const availableSystems = filteredSystems.filter(system => !draftSystemSet.has(system.shortName));
  const draftChanged = visibleDraftSystems.length !== effectiveSystems.length ||
    visibleDraftSystems.some(name => !effectiveSystems.some(current => current.toLowerCase() === name.toLowerCase()));
  const draftLabels = visibleDraftSystems.map(name => siteCandidates.find(system => system.shortName === name)?.label ?? name);
  const removeDraftSystem = (shortName: string) => setDraftSystems(current => current.filter(name => name !== shortName));
  return <div className="rf-step-stack">
    <p>Choose the sites/systems that are in scope for this workspace.</p>
    <div className="rf-form-grid">
      <label className="setting-field"><span>Workspace name<small>Shown in the Radio Setup table and saved with this workspace.</small></span><input value={surveySiteLabel} onChange={event => setSurveySiteLabel(event.target.value)} placeholder="Workspace name" /></label>
      <label className="setting-field"><span>RadioReference RID<small>System ID used to fetch sites for this workspace.</small></span><input value={radioReferenceSid} onChange={event => setRadioReferenceSid(event.target.value)} placeholder="RadioReference system ID" inputMode="numeric" /></label>
      <label className="setting-field"><span>Search sites<small>{siteCandidates.length.toLocaleString()} site(s). {radioReferenceSid ? `RR SID ${radioReferenceSid}.` : "No RR SID from setup."}</small></span><input value={siteSearch} onChange={event => setSiteSearch(event.target.value)} placeholder="Search site/system or control channel" /></label>
    </div>
    <div className="rf-primary-actions">
      <button disabled={busy === "rr-sites" || !radioReferenceSid} onClick={() => void onLoadRadioReferenceSites()}>{busy === "rr-sites" ? "Loading..." : radioReferenceSites ? "Reload RR Sites" : "Load RR Sites"}</button>
      {radioReferenceSites && <span className="muted">{radioReferenceSites.sites.length.toLocaleString()} RR site(s) loaded.</span>}
    </div>
    <div className="rf-site-selection-summary">
      <div>
        <div className="rf-selected-site-list">
          {draftLabels.length > 0
            ? visibleDraftSystems.map((name, index) => <button type="button" className="rf-selected-site-chip" key={name} onClick={() => removeDraftSystem(name)} title={`Remove ${draftLabels[index]}`}>{draftLabels[index]} <span aria-hidden="true">x</span></button>)
            : <span className="muted">No sites picked</span>}
        </div>
      </div>
      {draftChanged && <button className="danger-button" disabled={visibleDraftSystems.length === 0} onClick={() => changeSystems(visibleDraftSystems)}>Select {visibleDraftSystems.length} site{visibleDraftSystems.length === 1 ? "" : "s"}</button>}
    </div>
    <div className="rf-site-list">
      {availableSystems.map(system => {
        return <label className="rf-site-row" key={system.shortName}>
          <input type="checkbox" checked={false} onChange={() => toggleSystem(system.shortName)} />
          <strong title={system.label}>{system.label}</strong>
          <span>{system.controlCount} CC / {system.voiceCount} voice</span>
          <span>{system.controlText || "No CC listed"}</span>
        </label>;
      })}
      {availableSystems.length === 0 && <div className="setup-note">{filteredSystems.length === 0 ? "No matching sites." : "Matching sites are already selected."}</div>}
    </div>
  </div>;
}

const rfChainTypes = ["antenna", "coax", "splitter", "multicoupler", "lna", "filter", "sdr", "other"];
const rfConnectorTypes = ["n/a", "unknown", "SMA", "RP-SMA", "BNC", "TNC", "N", "F", "PL-259/SO-239", "MCX", "MMCX", "UHF", "FME", "SMP", "bare wire", "other"];
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

function SdrScopeStep({ sources, selectedSources, setSelectedSources, onTouched, controlChannels, compact = false }: { sources: RfSurveySource[]; selectedSources: number[]; setSelectedSources: React.Dispatch<React.SetStateAction<number[]>>; onTouched: () => void; controlChannels: number[]; compact?: boolean }) {
  const toggle = (index: number) => { onTouched(); setSelectedSources(current => current.includes(index) ? current.filter(row => row !== index) : [...current, index]); };
  return <div className="rf-step-stack">
    <div className="rf-sdr-list">
      {sources.map(source => <label className={selectedSources.includes(source.index) ? "rf-sdr-row selected" : "rf-sdr-row"} key={source.index}>
        <input type="checkbox" checked={selectedSources.includes(source.index)} onChange={() => toggle(source.index)} />
        <strong>Source {source.index}</strong>
        <span>{source.sdrType} {source.serial || source.device}</span>
        <span>{formatRfHz(source.centerHz)} center / {source.gain} gain / {source.errorHz} Hz error</span>
      </label>)}
    </div>
  </div>;
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

function RfPathRefinementStep({
  path,
  setPath,
  onRfPathTouched,
  onLoadPreviousRfPath,
  ...props
}: {
  path: RfSurveyPathProfile;
  setPath: React.Dispatch<React.SetStateAction<RfSurveyPathProfile>>;
  onRfPathTouched: () => void;
  onLoadPreviousRfPath: () => Promise<void>;
} & Omit<React.ComponentProps<typeof SiteValidationStep>, "activeOperation" | "waterfallSweepSelections" | "onWaterfallSweepSelections">) {
  const storageKey = `pizzawave-radio-setup-rf-subpage-${props.surveyId}`;
  const ccSelectionStorageKey = `pizzawave-radio-setup-waterfall-sweep-ccs-${props.surveyId}`;
  const sweepSelectionStorageKey = `pizzawave-radio-setup-waterfall-sweep-selections-${props.surveyId}`;
  const [subPage, setSubPageState] = useState<RfRefinementSubpage>(() => normalizeRfRefinementSubpage(localStorage.getItem(storageKey)));
  const [waterfallSweepSelections, setWaterfallSweepSelections] = useState<WaterfallSweepSelection[]>(() => {
    const saved = normalizeWaterfallSweepSelections(loadJsonStorage<unknown>(sweepSelectionStorageKey, []));
    return saved.length ? saved : normalizeWaterfallSweepSelections(loadJsonStorage<unknown>(ccSelectionStorageKey, []));
  });
  const [recoveredSweepStatus, setRecoveredSweepStatus] = useState("");
  const setSubPage = (value: RfRefinementSubpage) => {
    setSubPageState(value);
    localStorage.setItem(storageKey, value);
  };
  const updateWaterfallSweepSelections = (values: WaterfallSweepSelection[]) => {
    const normalized = normalizeWaterfallSweepSelections(values);
    setWaterfallSweepSelections(normalized);
    localStorage.setItem(sweepSelectionStorageKey, JSON.stringify(normalized));
    localStorage.setItem(ccSelectionStorageKey, JSON.stringify(normalized.map(row => row.frequencyHz)));
  };
  const rfPathEntered = path.chain.some(item => item.label?.trim() || item.length?.trim() || item.loss?.trim() || item.notes?.trim() || (item.connectorInType && item.connectorInType !== "unknown") || (item.connectorOutType && item.connectorOutType !== "unknown"));
  const persistedSweepStatus = props.sweep?.status === "running" ? undefined : props.sweep?.status;
  const sweepStatus = recoveredSweepStatus || persistedSweepStatus;
  const validationSweepStatus = rfValidationEffectiveStatus(props.validationSweep, props.systems ?? []);
  const pages: { id: RfRefinementSubpage; title: string; status?: string; optional?: boolean }[] = [
    { id: "path", title: "RF Path", status: rfPathEntered ? "completed" : undefined },
    { id: "inventory", title: "SDR Inventory", status: props.inventory?.status },
    { id: "waterfall", title: "Waterfall", status: undefined, optional: true },
    { id: "power", title: "RF Sweep", status: validationSweepStatus }
  ];
  return <div className="rf-step-stack">
    <div className="rf-subpage-tabs" aria-label="RF path and error/gain sub-pages">
      {pages.map(page => <button key={page.id} className={subPage === page.id ? "active" : measurementDone(page.status) ? "done" : ""} onClick={() => setSubPage(page.id)}>
        <span>{measurementDone(page.status) ? <CheckCircle2 size={12} /> : page.optional ? "Opt" : ""}</span>
        <strong>{page.title}</strong>
        <small>{page.status ? label(page.status) : page.optional ? "Optional" : "Pending"}</small>
      </button>)}
    </div>
    {subPage === "path"
      ? <RfPathStep path={path} setPath={setPath} onTouched={onRfPathTouched} onLoadPrevious={onLoadPreviousRfPath} busy={props.busy} />
      : null}
    <div style={{ display: subPage === "path" ? "none" : undefined }}>
      <SiteValidationStep
        {...props}
        activeOperation={subPage === "path" ? "waterfall" : subPage}
        waterfallSweepSelections={waterfallSweepSelections}
        onWaterfallSweepSelections={updateWaterfallSweepSelections}
        onSweepRecovered={setRecoveredSweepStatus}
      />
    </div>
  </div>;
}

type RfRefinementSubpage = "path" | "cc" | "inventory" | "waterfall" | "power" | "p25" | "sweep";

function normalizeRfRefinementSubpage(value: string | null): RfRefinementSubpage {
  if (value === "cc" || value === "p25" || value === "sweep") return "power";
  return value === "inventory" || value === "waterfall" || value === "power" ? value : "path";
}

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
    selections.set(frequencyHz, {
      frequencyHz,
      ...(Number.isFinite(sourceIndex) ? { sourceIndex: Math.round(sourceIndex) } : {}),
      ...(typeof row.gain === "string" && row.gain.trim() ? { gain: row.gain.trim() } : {}),
      ...(Number.isFinite(sampleRateHz) && sampleRateHz > 0 ? { sampleRateHz: Math.round(sampleRateHz) } : {})
    });
  }
  return [...selections.values()].sort((left, right) => left.frequencyHz - right.frequencyHz);
}

function RfConditionOverview({ path, systemShortName, sources, selectedSources, controlChannels }: { path: RfSurveyPathProfile; systemShortName: string; sources: RfSurveySource[]; selectedSources: number[]; controlChannels: number[] }) {
  const selected = sources.filter(source => selectedSources.includes(source.index));
  const effectiveSources = selected.length ? selected : sources.slice(0, 1);
  return <div className="rf-context-summary" aria-label="Current RF test condition">
    <div className="rf-context-item">
      <span>Site</span>
      <code>{systemShortName || "Not selected"}</code>
    </div>
    <div className="rf-context-item">
      <span>Control channels</span>
      <code>{controlChannels.length ? `${controlChannels.length}: ${controlChannels.slice(0, 2).map(formatRfHz).join(", ")}${controlChannels.length > 2 ? " ..." : ""}` : "None"}</code>
    </div>
    <div className="rf-context-item wide">
      <span>RF path</span>
      <code>{summarizeRfChain(path)}</code>
    </div>
    <div className="rf-context-item wide">
      <span>SDRs</span>
      <code>{effectiveSources.map(source => `Source ${source.index} (${source.errorHz || 0} Hz, gain ${source.gain || "?"})`).join(" | ") || "None"}</code>
    </div>
  </div>;
}

function summarizeRfChain(path: RfSurveyPathProfile) {
  const chain = (path.chain ?? [])
    .map(item => normalizeRfChainItem(item))
    .filter(item => item.type || item.label || item.connectorInType || item.connectorOutType)
    .map(item => item.label?.trim() ? `${label(item.type)}: ${item.label.trim()}` : label(item.type))
    .filter(Boolean);
  return chain.length ? chain.slice(0, 4).join(" -> ") + (chain.length > 4 ? " ..." : "") : "RF chain not entered";
}

function SiteValidationStep({
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
  onSweepRecovered
}: {
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
  onSweepRecovered?: (status: string) => void;
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
  const validationOffsets = parseIntegerSequence(validationErrorOffsets, [-300, 0, 300]);
  const validationAutoError = validationErrorOffsets.trim().toLowerCase() === "auto";
  const validationMetricCount = Math.max(1, Math.min(3, Number(validationMetricsCandidates) || 2));
  const validationRfCandidateLimit = 3;
  const selectedWaterfallSweepSelections = normalizeWaterfallSweepSelections(waterfallSweepSelections).filter(row => controlChannels.includes(row.frequencyHz));
  const selectedWaterfallSweepControlChannels = normalizeControlChannelSelection(selectedWaterfallSweepSelections.map(row => row.frequencyHz));
  const validationRunControlChannels = selectedWaterfallSweepControlChannels.length ? selectedWaterfallSweepControlChannels : controlChannels;
  const selectedWaterfallSourceIndexes = uniqueNonNegativeIntegers(selectedWaterfallSweepSelections.map(row => row.sourceIndex));
  const selectedWaterfallPowerSources = selectedWaterfallSourceIndexes.length
    ? effectivePowerSources.filter(source => selectedWaterfallSourceIndexes.includes(source.index))
    : [];
  const validationPowerSources = selectedWaterfallPowerSources.length ? selectedWaterfallPowerSources : effectivePowerSources;
  const selectedWaterfallGains = uniqueCaseInsensitive(selectedWaterfallSweepSelections.map(row => row.gain ?? ""));
  const validationPowerGains = selectedWaterfallGains.length ? effectivePowerGainSequence(selectedWaterfallGains, validationPowerSources) : effectivePowerGains;
  const selectedWaterfallSampleRates = uniqueSortedFrequencies(selectedWaterfallSweepSelections.map(row => row.sampleRateHz ?? 0));
  const waterfallSampleRateHz = selectedWaterfallSampleRates.length === 1 ? selectedWaterfallSampleRates[0] : 0;
  const validationRequestSampleRateHz = waterfallSampleRateHz || (validationSampleRateOk ? parsedValidationSampleRateHz : 0);
  const validationRequestSampleRateOk = waterfallSampleRateHz > 0 || validationSampleRateOk;
  const validationHandoffSourceIndex = selectedWaterfallSourceIndexes.length === 1 ? selectedWaterfallSourceIndexes[0] : undefined;
  const hasWaterfallSweepHandoff = selectedWaterfallSweepSelections.length > 0;
  const validationFormSampleRateMhz = waterfallSampleRateHz ? formatMhzInput(waterfallSampleRateHz) : validationSampleRateMhz;
  const validationFormGainSequence = hasWaterfallSweepHandoff ? validationPowerGains.join(",") : powerGainSequence;
  const validationPowerGainInvalid = validationPowerSources.some(isAirspyRfSource) && validationPowerGains.some(gain => !validateAirspyLinearityGain(gain));
  const powerSweepPasses = Math.max(1, validationPowerSources.length) * Math.max(1, validationRunControlChannels.length) * Math.max(1, validationPowerGains.length);
  const validationP25SeedCount = Math.max(1, Math.min(validationRfCandidateLimit, powerSweepPasses, validationRunControlChannels.length + (systems.length || 1)));
  const validationProbePasses = validationP25SeedCount * Math.max(1, validationOffsets.length);
  const validationMetricRunCount = Math.max(1, Math.min(validationMetricCount, validationP25SeedCount));
  const validationVoiceCandidateCount = hasWaterfallSweepHandoff
    ? Math.max(1, Math.min(2, validationP25SeedCount))
    : Math.max(2, Math.min(3, systems.length || 1));
  const validationVoiceSeconds = 45;
  const validationEstimateSeconds = powerSweepPasses * 2 + validationProbePasses * 10 + validationMetricRunCount * 15 + validationVoiceCandidateCount * validationVoiceSeconds + 45;
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
  const activeValidationMetricRunCount = Math.max(1, Math.min(validationMetricCount, activeValidationP25SeedCount));
  const activeValidationVoiceCandidateCount = activeValidationTarget ? Math.max(1, Math.min(2, activeValidationControlChannels.length)) : validationVoiceCandidateCount;
  const activeValidationEstimateSeconds = activeValidationPowerPasses * 2 + activeValidationProbePasses * 10 + activeValidationMetricRunCount * 15 + activeValidationVoiceCandidateCount * validationVoiceSeconds + (activeValidationTarget ? 30 : 45);
  const validationProgressRows = showLiveValidationProgress ? validationProgress?.rows ?? [] : validationResultRows;
  const validationProgressCandidates = showLiveValidationProgress ? validationProgress?.candidates ?? [] : validationResultCandidates;
  const hideStaleSiteReadiness = showLiveValidationProgress && !activeValidationTarget;
  const sweepStorageKey = `pizzawave-radio-setup-sweep-job-${surveyId}`;
  const sweepInsightStorageKey = `pizzawave-radio-setup-sweep-insights-${surveyId}`;
  const sweepHistoryStorageKey = `pizzawave-radio-setup-sweep-history-${surveyId}`;
  const sweepRecoveryKey = useRef("");
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
        const progress = await api.request<RfSurveySweepProgress>(`${radioSetupApi}/${encodeURIComponent(surveyId)}/sweep-progress`);
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
    const progress = await api.request<RfSurveySweepProgress>(`${radioSetupApi}/${encodeURIComponent(surveyId)}/sweep-progress`);
    setValidationProgress(progress);
  }
  async function cancelValidationSweep() {
    setSweepBusy("cancel-validation");
    setValidationCancelMessage("");
    try {
      const result = await api.request<RfSurveyCancelExperimentResult>(`${radioSetupApi}/${encodeURIComponent(surveyId)}/experiments/cancel`, { method: "POST" });
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
      const experiment = await api.request<RfSurveyExperiment>(`${radioSetupApi}/${encodeURIComponent(surveyId)}/experiments/run`, {
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
      const experiment = await api.request<RfSurveyExperiment>(`${radioSetupApi}/${encodeURIComponent(surveyId)}/experiments/run`, {
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
      const editor = await api.request<TrConfigEditor>("/api/v1/system/tr-config/editor");
      const draft = JSON.parse(editor.configJson || "{}");
      draft.sources = Array.isArray(draft.sources) ? draft.sources : [];
      const sourceIndex = draft.sources.findIndex((row: any, index: number) =>
        index === source.index ||
        String(row?.device ?? "").includes(`rtl=${result.serial}`) ||
        String(row?.device ?? "").includes(result.serial));
      if (sourceIndex < 0)
        throw new Error(`Unable to find TR source for rtl=${result.serial || source.serial || source.index}.`);
      draft.sources[sourceIndex] = { ...draft.sources[sourceIndex], error: candidate.errorHz };
      if (result.gain)
        draft.sources[sourceIndex].gain = numericOrText(result.gain);
      await api.request<TrConfigEditor>("/api/v1/system/tr-config/editor/draft", {
        method: "POST",
        body: JSON.stringify({ configJson: JSON.stringify(draft, null, 2) })
      });
      setSweepMessage(`Applied Source ${source.index} candidate error ${candidate.errorHz} Hz to Config Draft. After Save & Restart, rerun RF Power Scan, P25 Probe, TR CC Metrics, and Call Quality for this CC/source condition.`);
    } catch (error) {
      setSweepMessage(error instanceof Error ? error.message : "Unable to apply sweep candidate to Config Draft.");
    } finally {
      setSweepBusy("");
    }
  }
  async function analyzeSweepCandidate(source: RfSurveySource, result: SweepResult, candidate: SweepCandidate) {
    setSweepBusy(`ai-${source.index}`);
    setSweepMessage("");
    try {
      const insight = await api.request<SweepInsight>(`${radioSetupApi}/${encodeURIComponent(surveyId)}/sweep-insights`, {
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
      locked: !inventory,
      action: "Start",
      busyKey: "waterfall",
      begin: undefined,
      result: undefined,
      body: <WaterfallStep
        surveyId={surveyId}
        locked={!inventory}
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
      locked: !inventory,
      action: "Run",
      busyKey: "rf_validation_sweep",
      begin: runValidationSweep,
      result: validationSweep,
      body: <div className="rf-step-stack">
        <div className="rf-sweep-compact">
          <div className="rf-sweep-form">
            <div className="rf-cc-runline compact">
              <label><span>Sample rate MHz</span><input className={validationRequestSampleRateOk ? "rf-short-input" : "rf-short-input invalid"} size={8} inputMode="decimal" disabled={waterfallSampleRateHz > 0} value={validationFormSampleRateMhz} onChange={event => updateValidationSampleRate(event.target.value)} /></label>
              <label><span>Gain sequence</span><input className={validationPowerGainInvalid ? "invalid" : ""} disabled={hasWaterfallSweepHandoff} value={validationFormGainSequence} onChange={event => setPowerGainSequence(event.target.value)} /></label>
              <label><span>Error search</span><input className="rf-short-input" size={6} value={validationErrorOffsets} onChange={event => setValidationErrorOffsets(event.target.value)} /></label>
              <label><span>Metric candidates</span><input className="rf-short-input" size={6} inputMode="numeric" value={validationMetricsCandidates} onChange={event => setValidationMetricsCandidates(event.target.value)} /></label>
              <button className="danger-button" disabled={Boolean(busy) || validationBlocked || !validationRequestSampleRateOk || validationPowerGainInvalid} onClick={() => void runValidationSweep()}>{busy === "rf_validation_sweep" ? "Running..." : "Run"}</button>
              {(validationRunning || validationProgress?.active) && <button disabled={sweepBusy === "cancel-validation"} onClick={() => void cancelValidationSweep()}>{sweepBusy === "cancel-validation" ? "Canceling..." : "Cancel"}</button>}
            </div>
            {validationSampleRateMessage && !waterfallSampleRateHz && <div className="settings-message error">{validationSampleRateMessage}</div>}
            {airspyPowerGainMessage && <div className={validationPowerGainInvalid ? "settings-message error" : "setup-note"}>{validationPowerGainInvalid ? `${airspyPowerGainMessage} Remove values above ${AIRSPY_LINEARITY_GAIN_MAX}.` : airspyPowerGainMessage}</div>}
            <div className="rf-waterfall-sweep-selection">
              <strong>RF Sweep CCs</strong>
              <span>{selectedWaterfallSweepControlChannels.length ? `${selectedWaterfallSweepControlChannels.map(formatRfHz).join(", ")} / gain ${validationPowerGains.join(", ")}${waterfallSampleRateHz ? ` / ${formatMhzInput(waterfallSampleRateHz)} MS/s` : ""}${validationHandoffSourceIndex !== undefined ? ` / source ${validationHandoffSourceIndex}` : ""}` : "All requested control channels"}</span>
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
            <div><span>P25 probes</span><code>{activeValidationP25SeedCount} control channel seed(s) x {validationOffsets.length} offset(s) = {activeValidationProbePasses}</code></div>
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
        <div className="setup-note">Applying a candidate changes the source condition. Rerun RF Sweep, P25 Probe, TR CC Metrics, and Call Quality after applying it.</div>
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
                <button className="danger-button" disabled={!selectedCandidate || sweepBusy === `apply-${source.index}`} onClick={() => selectedCandidate && void applySweepCandidate(source, result, selectedCandidate)}>{sweepBusy === `apply-${source.index}` ? "Applying..." : "Apply"}</button>
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
  const requiredSteps = substeps.filter(item => item.id === "inventory" || item.id === "power");
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
  surveyId,
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
  onReload
}: {
  surveyId: string;
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
}) {
  const effectiveSources = sources.filter(source => selectedSources.includes(source.index));
  const sourceOptions = effectiveSources.length ? effectiveSources : sources.slice(0, 1);
  const defaultSource = sourceOptions[0] ?? sources[0];
  const [sourceIndex, setSourceIndex] = useState(() => defaultSource?.index ?? 0);
  const selectedSource = sources.find(source => source.index === sourceIndex) ?? defaultSource;
  const selectedSourceIsAirspy = selectedSource ? isAirspyRfSource(selectedSource) : false;
  const defaultFrequency = activeControlChannelHz || controlChannels[0] || selectedSource?.centerHz || 0;
  const [frequencyMhz, setFrequencyMhz] = useState(() => defaultFrequency ? formatMhzInput(defaultFrequency) : "");
  const [sampleRateMhz, setSampleRateMhz] = useState(() => formatMhzInput(defaultWaterfallSampleRate(selectedSource)));
  const [gain, setGain] = useState(() => selectedSource?.gain || "15");
  const [spectrumSpanDb, setSpectrumSpanDb] = useState(35);
  const [showControlChannelLines, setShowControlChannelLines] = useState(true);
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
  const hasGoodWaterfallFrameRef = useRef(false);
  const frequencyHz = Math.round(Number(frequencyMhz) * 1_000_000);
  const sampleRateHz = Math.round(Number(sampleRateMhz) * 1_000_000);
  const controlChannelOptions = uniqueSortedFrequencies([
    ...controlChannels,
    ...systems.flatMap(system => system.controlChannelsHz),
    activeControlChannelHz
  ]);
  const selectedSweepSelections = normalizeWaterfallSweepSelections(waterfallSweepSelections).filter(row => controlChannelOptions.includes(row.frequencyHz));
  const selectedSweepControlChannels = normalizeControlChannelSelection(selectedSweepSelections.map(row => row.frequencyHz));
  const selectedSweepControlChannelSet = new Set(selectedSweepControlChannels);
  const frequencyOk = Number.isFinite(frequencyHz) && frequencyHz > 0;
  const sampleRateOk = Number.isFinite(sampleRateHz) && sampleRateHz > 0;
  const gainOk = !selectedSourceIsAirspy || validateAirspyLinearityGain(gain.trim() || selectedSource?.gain || "15");
  const identifyRunning = busy === "identify";
  const controlsDisabled = identifyRunning;
  const canStart = !locked && !busy && sourceOptions.length > 0 && frequencyOk && sampleRateOk && gainOk;

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

  function renderWaterfallStatus(next: RfSurveyWaterfallStatus | null) {
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
      setCcSignalRows(nextCcSignalRows);
      setOtherDetectedCcRows(nextOtherDetectedCcRows);
      drawSpectrumFrame(spectrumCanvasRef.current, frame, smoothed, spectrumScaleRef.current, axis, {
        controlChannelsHz: controlChannelOptions,
        showControlChannels: showControlChannelLines,
        peaks: positionedPeaks
      });
      drawWaterfallFrame(canvasRef.current, smoothWaterfallBins(frame.powersDb), waterfallScaleRef.current);
    }
  }

  useEffect(() => {
    let stopped = false;
    async function loadStatus() {
      try {
        const next = await api.request<RfSurveyWaterfallStatus>(`${radioSetupApi}/${encodeURIComponent(surveyId)}/waterfall?history=true`);
        if (stopped) return;
        window.requestAnimationFrame(() => renderWaterfallStatus(next));
        setStatus({ ...next, frames: null });
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
  }, [surveyId]);

  useEffect(() => {
    if (!status?.active) return;
    let stopped = false;
    async function poll() {
      try {
        const next = await api.request<RfSurveyWaterfallStatus>(`${radioSetupApi}/${encodeURIComponent(surveyId)}/waterfall`);
        if (!stopped) {
          setStatus(next);
          if (shouldShowWaterfallMessage(next.message, next))
            setMessage(next.message);
        }
      } catch (error) {
        if (!stopped)
          setMessage(error instanceof Error ? error.message : "Waterfall status refresh failed.");
      }
    }
    const timer = window.setInterval(() => void poll(), 120);
    return () => {
      stopped = true;
      window.clearInterval(timer);
    };
  }, [surveyId, status?.active]);

  useEffect(() => {
    renderWaterfallStatus(status);
  }, [status?.frame?.sequence, spectrumSpanDb, showControlChannelLines, controlChannels.join(","), systems.map(system => `${system.shortName}:${system.controlChannelsHz.join("/")}`).join("|")]);

  async function startWaterfall() {
    if (!gainOk) {
      setMessage(`Airspy waterfall gain must be whole-number linearity gain 0-${AIRSPY_LINEARITY_GAIN_MAX}.`);
      return;
    }
    setBusy("start");
    setMessage("");
    lastFrameRef.current = "";
    hasGoodWaterfallFrameRef.current = false;
    smoothedSpectrumRef.current = [];
    heldSpectrumRef.current = [];
    peakHistoryRef.current.clear();
    otherDetectedCcHistoryRef.current.clear();
    ccSignalHistoryRef.current.clear();
    visiblePeaksRef.current = [];
    setOtherDetectedCcRows([]);
    setSpectrumHover(null);
    setCcSignalRows([]);
    setIdentifyOverlayMessage("");
    spectrumScaleRef.current = null;
    waterfallScaleRef.current = null;
    spectrumAxisRef.current = null;
    clearWaterfallCanvas(spectrumCanvasRef.current);
    clearWaterfallCanvas(canvasRef.current);
    try {
      const next = await api.request<RfSurveyWaterfallStatus>(`${radioSetupApi}/${encodeURIComponent(surveyId)}/waterfall/start`, {
        method: "POST",
        body: JSON.stringify({
          sourceIndex,
          frequencyHz,
          sampleRateHz,
          gain,
          binCount: 4096,
          captureMilliseconds: 60,
          refreshMilliseconds: 120
        })
      });
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
      const next = await api.request<RfSurveyWaterfallStatus>(waterfallStopUrl(surveyId), { method: "POST" });
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
        ? `Matched saved CC ${targetLabel}; waterfall peak offset ${offset >= 0 ? "+" : ""}${offset} Hz.`
        : "This peak is not within 20 kHz of a cached control channel for the current RSW.",
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
        const next = await api.request<RfSurveyWaterfallStatus>(waterfallStopUrl(surveyId), { method: "POST" });
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
          const next = await api.request<RfSurveyWaterfallStatus>(`${radioSetupApi}/${encodeURIComponent(surveyId)}/waterfall/start`, {
            method: "POST",
            body: JSON.stringify({
              sourceIndex,
              frequencyHz,
              sampleRateHz,
              gain,
              binCount: 4096,
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
    const frequencyHz = Math.round(row.sweepFrequencyHz);
    const current = normalizeWaterfallSweepSelections(waterfallSweepSelections);
    const remaining = current.filter(item => item.frequencyHz !== frequencyHz);
    if (selected) {
      remaining.push({
        frequencyHz,
        sourceIndex,
        gain: gain.trim() || selectedSource?.gain || "",
        sampleRateHz: sampleRateOk ? sampleRateHz : undefined
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
    const reportCandidates = buildWaterfallCandidateRows(ccSignalRows, reportOtherRows, systems, controlChannelOptions, identifyResults);
    const candidateRows = reportCandidates.length
      ? reportCandidates.map(row => {
        const identify = identifyResults[row.identifyPeak.key];
        const evidence = [
          `SNR ${formatFixed(row.snrDb, 1)} dB`,
          Number.isFinite(row.offsetHz) ? `offset ${row.offsetHz >= 0 ? "+" : ""}${formatFixed(row.offsetHz, 0)} Hz` : "",
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
    ctx.fillText(`${capturedAt} / Survey ${surveyId}`, margin, y + 18);
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
  const waterfallCandidates = buildWaterfallCandidateRows(ccSignalRows, displayedOtherDetectedCcRows, systems, controlChannelOptions, identifyResults);
  async function toggleCandidateForSweep(row: WaterfallCandidateRow, selected: boolean) {
    if (selected && row.origin !== "selected" && row.system && !systems.some(system => system.shortName.toLowerCase() === row.system?.shortName.toLowerCase()))
      await onAdoptWaterfallSite(row.system);
    toggleSweepControlChannel(row, selected);
  }
  return <div className="rf-waterfall-panel">
    <div className="rf-waterfall-controls">
      <label><span>Source</span><select value={String(sourceIndex)} disabled={controlsDisabled || locked || status?.active || sourceOptions.length <= 1} onChange={event => setSourceIndex(Number(event.target.value))}>
        {sourceOptions.map(source => <option value={String(source.index)} key={source.index}>Source {source.index} / {source.sdrType || "SDR"}</option>)}
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
      <button className="danger-button" disabled={!canStart || status?.active === true} onClick={() => void startWaterfall()}>{busy === "start" ? "Starting..." : "Start"}</button>
      <button disabled={controlsDisabled || !status?.active || busy === "stop"} onClick={() => void stopWaterfall()}>{busy === "stop" ? "Stopping..." : "Stop"}</button>
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
      <div className="rf-waterfall-cc-head"><span>Control Channel Candidates</span><small>Ranked by SNR. Offset is only shown when the peak matches a selected-site control channel.</small></div>
      <div className="rf-waterfall-candidate-table">
        <div className="rf-waterfall-candidate-row header">
          <span>Use</span><span>Site</span><span>Matched CC</span><span>Detected</span><span>SNR</span><span>Offset</span><span>Confidence</span><span>Source</span><span>Action</span>
        </div>
        {waterfallCandidates.length === 0 ? <div className="rf-waterfall-cc-empty">Start waterfall to inspect selected and nearby RR control channels.</div> : waterfallCandidates.map(row => {
          const identify = identifyResults[row.identifyPeak.key];
          const selected = selectedSweepControlChannelSet.has(row.sweepFrequencyHz);
          return <div className={`rf-waterfall-candidate-row ${row.origin} ${identify ? `identified ${identify.status}` : ""}`.trim()} key={row.key}>
            <label className="rf-waterfall-use-check">
              <input type="checkbox" checked={selected} onChange={event => void toggleCandidateForSweep(row, event.target.checked)} aria-label={`Use ${formatRfHz(row.sweepFrequencyHz)} for RF Sweep`} />
              <span>{selected ? "Use" : ""}</span>
            </label>
            <span title={row.siteLabel}>{row.siteLabel}</span>
            <code>{row.targetFrequencyHz > 0 ? formatRfHz(row.targetFrequencyHz) : "--"}</code>
            <code>{row.detectedFrequencyHz > 0 ? formatRfHz(row.detectedFrequencyHz) : "--"}</code>
            <strong>{Number.isFinite(row.snrDb) ? `${formatFixed(row.snrDb, 1)} dB` : "--"}</strong>
            <span>{Number.isFinite(row.offsetHz) ? `${row.offsetHz >= 0 ? "+" : ""}${formatFixed(row.offsetHz, 0)} Hz` : "--"}</span>
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
  const otherCandidateRows: WaterfallCandidateRow[] = otherRows.map(row => {
    const fallbackTarget = nearestFrequencyTarget(row.frequencyHz, matchableTargets, 20_000);
    const matchedSelected = Boolean(fallbackTarget?.systemShortName && selectedNames.has(fallbackTarget.systemShortName.toLowerCase()));
    const targetFrequencyHz = Math.round(fallbackTarget?.frequencyHz ?? row.frequencyHz);
    const origin: WaterfallCandidateRow["origin"] = fallbackTarget?.systemShortName && selectedNames.has(fallbackTarget.systemShortName.toLowerCase())
      ? "selected"
      : "unknown";
    const identifyPeak: PositionedSpectrumPeak = {
      ...row,
      tuneFrequencyHz: matchedSelected ? targetFrequencyHz : Math.round(row.frequencyHz),
      measuredFrequencyHz: Math.round(row.frequencyHz),
      targetFrequencyHz: matchedSelected ? targetFrequencyHz : 0
    };
    return {
      key: row.key,
      origin,
      siteLabel: matchedSelected ? fallbackTarget?.siteLabel ?? "Selected CC" : "Unknown CC",
      systemShortName: matchedSelected ? fallbackTarget?.systemShortName ?? "" : "",
      targetFrequencyHz: matchedSelected ? targetFrequencyHz : 0,
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
    const fallbackTarget = nearestFrequencyTarget(measuredFrequencyHz, matchableTargets, 20_000);
    const matchedSelected = Boolean(fallbackTarget?.systemShortName && selectedNames.has(fallbackTarget.systemShortName.toLowerCase()));
    const targetFrequencyHz = Math.round(fallbackTarget?.frequencyHz ?? result.targetFrequencyHz ?? result.frequencyHz);
    const origin: WaterfallCandidateRow["origin"] = fallbackTarget?.systemShortName && selectedNames.has(fallbackTarget.systemShortName.toLowerCase())
      ? "selected"
      : "unknown";
    rows.push({
      key: result.key,
      origin,
      siteLabel: matchedSelected ? fallbackTarget?.siteLabel ?? result.targetLabel ?? "Selected CC" : "Unknown CC",
      systemShortName: matchedSelected ? fallbackTarget?.systemShortName ?? "" : "",
      targetFrequencyHz: matchedSelected ? targetFrequencyHz : 0,
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
  return rows
    .sort((left, right) => (right.snrDb - left.snrDb) || (right.confidence - left.confidence) || Math.abs(left.offsetHz) - Math.abs(right.offsetHz));
}

function nearestFrequencyTarget<T extends { frequencyHz: number }>(frequencyHz: number, targets: T[], maxDistanceHz = Number.POSITIVE_INFINITY) {
  return targets
    .map(target => ({ target, distance: Math.abs(target.frequencyHz - frequencyHz) }))
    .filter(row => row.distance <= maxDistanceHz)
    .sort((left, right) => left.distance - right.distance)[0]?.target ?? null;
}

function waterfallCandidateSourceLabel(row: WaterfallCandidateRow, identify?: WaterfallIdentifyResult) {
  const prefix = row.origin === "selected" ? "Selected site" : "Unknown CC";
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
    ? `Matched saved CC ${base.targetLabel}; measured offset ${base.offsetHz >= 0 ? "+" : ""}${base.offsetHz} Hz.`
    : "Not in saved CC list for this RSW; probed the measured peak directly.";
  const identitySummary = p25IdentifySummary(fields);
  const measuredFrequency = base.measuredFrequencyHz || base.frequencyHz;
  const tunedFrequency = base.frequencyHz;
  const tunedOffset = fields.decodedControlChannelHz > 0
    ? Math.round(measuredFrequency - fields.decodedControlChannelHz)
    : 0;
  const decodedDetail = fields.decodedControlChannelHz > 0
    ? `Tuned ${formatRfHz(tunedFrequency)}; decoded CC ${formatRfHz(fields.decodedControlChannelHz)}; waterfall peak ${formatRfHz(measuredFrequency)} (${tunedOffset >= 0 ? "+" : ""}${tunedOffset} Hz from decoded CC).`
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

function CallQualityStep({
  busy,
  voice,
  transcription,
  stability,
  sourcePlanApplied,
  sourcePlanSummary,
  callQualityEnabled,
  callQualityBlockingIssue,
  resetActive,
  onOpenConfigDraftReview,
  onApplyConfigDraft,
  onRun
}: {
  busy: string;
  voice?: RfSurveyExperiment;
  transcription?: RfSurveyExperiment;
  stability?: RfSurveyExperiment;
  sourcePlanApplied: boolean;
  sourcePlanSummary: string;
  callQualityEnabled: boolean;
  callQualityBlockingIssue: string;
  resetActive?: boolean;
  onOpenConfigDraftReview: () => void;
  onApplyConfigDraft: () => Promise<void>;
  onRun: () => Promise<void>;
}) {
  const stages = [
    { title: "Real Calls", estimate: "3 minutes", result: voice },
    { title: "Transcription Quality", estimate: "up to 2 minutes", result: transcription },
    { title: "Stability Window", estimate: "uses capture window", result: stability }
  ];
  const complete = measurementDone(stability?.status);
  const running = busy === "call_quality";
  const ready = sourcePlanApplied || callQualityEnabled;
  const blocker = "Apply the current Config Draft source plan to live trunk-recorder before running Call Quality.";
  return <div className="rf-step-stack">
    <p>Validate that the applied Config Draft source plan produces useful PizzaWave output when traffic is present. If no calls occur but TR metrics stay healthy, the step can proceed with a caveat because transcription and call stability were not testable.</p>
    {!ready && <div className="setup-warning-list"><div>{blocker}</div></div>}
    {resetActive && <div className="setup-note">New Call Quality run started. Previous gate results are hidden until fresh results arrive.</div>}
    <div className="rf-next-action">
      <div>
        <span>Estimated time: about 5 minutes</span>
        <strong>{complete ? "Call quality is complete" : ready ? "Run optional voice, transcription, and stability gates" : "Waiting for prerequisite"}</strong>
        {sourcePlanSummary && <small>{sourcePlanSummary}</small>}
        {callQualityEnabled && <small>Call Quality is enabled by the current Radio Setup plan.</small>}
        {!ready && callQualityBlockingIssue && <small>{callQualityBlockingIssue.replace("Apply the Config Draft source plan before Call Quality.", "The selected source plan has not been applied to live trunk-recorder yet.")}</small>}
      </div>
      <div className="rf-next-actions">
        <button type="button" disabled={Boolean(busy)} onClick={() => void onApplyConfigDraft()}>{busy === "config_apply" ? "Applying..." : ready ? "Reapply Config Draft" : "Apply Config Draft"}</button>
        {!ready && <button type="button" disabled={Boolean(busy)} onClick={onOpenConfigDraftReview}>Review JSON</button>}
        <button className="danger-button" disabled={Boolean(busy) || !ready} onClick={() => void onRun()}>{running ? "Running..." : "Run"}</button>
      </div>
    </div>
    {running && <StepProgressIndicator label="Call Quality is running" />}
    <div className="rf-operation-table" role="table" aria-label="Call quality gates">
      <div className="rf-operation-row header" role="row">
        <span>Gate</span>
        <span>Status</span>
        <span>Time</span>
        <span>Latest result</span>
      </div>
      {stages.map(stage => <div className={!measurementDone(stage.result?.status) && !complete ? "rf-operation-row active" : "rf-operation-row"} role="row" key={stage.title}>
        <strong>{stage.title}</strong>
        <span className={measurementDone(stage.result?.status) ? "section-status ok" : stage.result?.status === "failed" || stage.result?.status === "blocked" ? "section-status error" : "section-status"}>{stage.result?.status ? label(stage.result.status) : "Pending"}</span>
        <span>{stage.estimate}</span>
        <span>{stage.result?.blockingIssue || stage.result?.resultSummary || "--"}</span>
      </div>)}
    </div>
  </div>;
}

function StepProgressIndicator({ label }: { label: string }) {
  return <div className="rf-step-progress" role="status" aria-live="polite" aria-label={label}>
    <span className="rf-step-spinner" aria-hidden="true" />
    <span>{label}</span>
    <span className="rf-step-progress-track" aria-hidden="true"><span /></span>
  </div>;
}

function ConfigDraftStep({
  reload,
  detail,
  selectedSources,
  setSelectedSources,
  sourcePlanSystems,
  setSourcePlanSystems,
  sourcePlanMode,
  setSourcePlanMode,
  sdrSources,
  setSdrSources,
  onSdrTouched,
  onApplyStateChange,
  onApplied
}: {
  reload: () => Promise<void>;
  detail: RfSurveyDetail;
  selectedSources: number[];
  setSelectedSources: React.Dispatch<React.SetStateAction<number[]>>;
  sourcePlanSystems: string[];
  setSourcePlanSystems: React.Dispatch<React.SetStateAction<string[]>>;
  sourcePlanMode: "full" | "control";
  setSourcePlanMode: React.Dispatch<React.SetStateAction<"full" | "control">>;
  sdrSources: RfSurveySource[] | null;
  setSdrSources: React.Dispatch<React.SetStateAction<RfSurveySource[] | null>>;
  onSdrTouched: () => void;
  onApplyStateChange: (value: boolean) => void;
  onApplied: () => void | Promise<void>;
}) {
  const storageKey = `pizzawave-radio-setup-config-subpage-${detail.session.id}`;
  const [subPage, setSubPageState] = useState<ConfigDraftSubpage>(() => normalizeConfigDraftSubpage(localStorage.getItem(storageKey)));
  const setSubPage = (value: ConfigDraftSubpage) => {
    setSubPageState(value);
    localStorage.setItem(storageKey, value);
  };
  async function applySourceDraft(configText: string) {
    onApplyStateChange(true);
    try {
      await api.request<RfSurveyTrActionResult>(`${radioSetupApi}/${encodeURIComponent(detail.session.id)}/tr/apply-source-draft`, {
        method: "POST",
        body: JSON.stringify({ configJson: configText, restartTr: true, preserveRfValidationEvidence: true })
      });
      await reload();
      await onApplied();
    } finally {
      onApplyStateChange(false);
    }
  }
  async function saveSourcePlanBeforeReview() {
    const systemNames = detail.profile.systemShortNames?.length
      ? detail.profile.systemShortNames
      : detail.profile.systemShortName ? [detail.profile.systemShortName] : [];
    await api.request<RfSurveyDetail>(`${radioSetupApi}/${encodeURIComponent(detail.session.id)}/draft`, {
      method: "POST",
      body: JSON.stringify({
        systemShortName: systemNames[0] ?? detail.profile.systemShortName,
        systemShortNames: systemNames,
        sourcePlanSystemShortNames: sourcePlanSystems.length ? sourcePlanSystems : systemNames,
        sourcePlanMode,
        systemDefinitions: detail.profile.systems ?? [],
        siteLabel: detail.profile.siteLabel || detail.session.siteLabel,
        rfPath: detail.profile.rfPath,
        selectedSourceIndexes: selectedSources,
        sdrSources: sdrSources ?? (detail.profile.sourceOverride ? detail.profile.sources : undefined),
        currentStep: detail.profile.currentStep,
        measurementMode: detail.profile.measurementMode,
        probeDurationSeconds: detail.profile.probeDurationSeconds
      })
    });
  }
  const tabs = [
    { id: "source" as const, title: "Sources", status: selectedSources.length > 0 ? "Ready" : "Select" },
    { id: "review" as const, title: "Review", status: detail.session.sourcePlanSummary || detail.session.status === "source_plan_applied" || detail.session.status === "completed" ? "Applied" : "Apply JSON" }
  ];
  return <div className="rf-step-stack">
    <div className="rf-subpage-tabs" aria-label="Config Draft sections">
      {tabs.map((tab, index) => <button className={`${subPage === tab.id ? "active" : ""} ${tab.status === "Applied" || tab.status === "Ready" ? "done" : ""}`.trim()} onClick={() => setSubPage(tab.id)} key={tab.id}>
        <span>{index + 1}</span>
        <strong>{tab.title}</strong>
        <small>{tab.status}</small>
      </button>)}
    </div>
    {subPage === "source" && <SourcePlannerStep profile={detail.profile} experiments={detail.experiments} selectedSources={selectedSources} setSelectedSources={setSelectedSources} sourcePlanSystems={sourcePlanSystems} setSourcePlanSystems={setSourcePlanSystems} sourcePlanMode={sourcePlanMode} setSourcePlanMode={setSourcePlanMode} setSdrSources={setSdrSources} onSdrTouched={onSdrTouched} onPlanSelected={() => setSubPage("review")} />}
    {subPage === "review" && <TrConfigEditorPanel
      reload={reload}
      rawOnly
      applyLabel="Apply"
      loadEditor={async () => {
        await saveSourcePlanBeforeReview();
        const draft = await api.request<RfSurveyConfigDraft>(`${radioSetupApi}/${encodeURIComponent(detail.session.id)}/config-draft`);
        return {
          livePath: draft.livePath,
          draftPath: draft.draftPath,
          configJson: draft.configJson,
          liveConfigJson: draft.liveConfigJson,
          hasDraft: true,
          parseOk: true,
          parseMessage: draft.summary.changes.length ? draft.summary.changes.join("; ") : "No selected source changes in workspace draft.",
          summary: { systems: [], sources: [], warnings: draft.summary.warnings }
        };
      }}
      applyOverride={applySourceDraft} />}
  </div>;
}

type ConfigDraftSubpage = "source" | "review";

function normalizeConfigDraftSubpage(value: string | null): ConfigDraftSubpage {
  return value === "review" ? value : "source";
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
  setSdrSources,
  onSdrTouched,
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
  setSdrSources: React.Dispatch<React.SetStateAction<RfSurveySource[] | null>>;
  onSdrTouched: () => void;
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
    if (rateValidation.ok)
      setSdrSources(profile.sources.map(source => ({ ...source, sampleRate: effectiveSampleRateHz })));
    setSelectedSources(profile.sources.slice(0, Math.max(1, option.windows.length)).map(source => source.index));
    onPlanSelected();
  };
  const updateSampleRate = (value: string) => {
    setSampleRateMhz(value);
    const hz = Math.round(Number(value) * 1_000_000);
    if (!validateSourcePlannerSampleRate(hz, supportedRateOptions).ok) return;
    onSdrTouched();
    setSdrSources(profile.sources.map(source => ({ ...source, sampleRate: hz })));
  };
  return <div className="rf-step-stack">
    <div className="rf-source-planner-plain">
      <div className="rf-source-calculator">
        <label>Sample rate (MHz)<input className={rateValidation.ok ? "" : "invalid"} inputMode="decimal" value={sampleRateMhz} onChange={event => updateSampleRate(event.target.value)} /></label>
        <div className={rateValidation.ok ? "setup-note" : "settings-message error"}>
          {rateValidation.ok
            ? `Planning uses RF Sweep validated control channels when available and TR usable bandwidth of ${formatHz(trUsableSpanHz(effectiveSampleRateHz))}.`
            : rateValidation.message}
          {supportedRateOptions.length > 0 && <small>Supported by detected SDR inventory: {supportedRateOptions.map((rate: number) => (rate / 1_000_000).toFixed(3).replace(/0+$/, "").replace(/\.$/, "")).join(", ")} MHz.</small>}
        </div>
      </div>
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

function buildSuggestedSourcePlan(frequencies: number[], sampleRateHz: number) {
  const span = trUsableSpanHz(sampleRateHz);
  const rows: { lowHz: number; centerHz: number; highHz: number; count: number }[] = [];
  let index = 0;
  while (index < frequencies.length) {
    const start = frequencies[index];
    let endIndex = index;
    while (endIndex + 1 < frequencies.length && frequencies[endIndex + 1] - start <= span) endIndex += 1;
    const end = frequencies[endIndex];
    const center = Math.round((start + end) / 2);
    rows.push({ lowHz: Math.round(center - span / 2), centerHz: center, highHz: Math.round(center + span / 2), count: endIndex - index + 1 });
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
  const windows = buildSuggestedSourcePlan(frequencies, sampleRateHz);
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
      <span>Source windows</span>
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
        <span>{option.windows.length ? option.windows.map((window, index) => <code key={`${option.id}-${index}`}>{formatRfHz(window.lowHz)}-{formatRfHz(window.highHz)}</code>) : "--"}</span>
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

function appendExperimentLog(experiment: RfSurveyExperiment, append: (text: string, level?: RfRunLogLine["level"]) => void) {
  append(`${label(experiment.type)} ${label(experiment.status)}: ${experiment.blockingIssue || experiment.resultSummary || experiment.hypothesis}`, experiment.status === "failed" || experiment.status === "blocked" ? "error" : "result");
  const evidence = parseExperimentJson<any>(experiment.evidenceJson);
  if (experiment.type === "sdr_inventory") {
    const outputs = Array.isArray(evidence?.outputs) ? evidence.outputs : [];
    for (const row of outputs)
      append(`${row.tool || "tool"} ${row.status || ""}${row.output ? `\n${trimLogText(row.output)}` : ""}`, row.status === "error" || row.status === "missing" ? "error" : "result");
  } else if (experiment.type === "control_channel_p25_probe") {
    if (evidence?.command) append(`$ ${evidence.command}`, "info");
    if (evidence?.output) append(trimLogText(evidence.output), experiment.status === "passed" ? "result" : "error");
  } else if (experiment.type === "rf_power_scan") {
    const rows = Array.isArray(evidence?.rows) ? evidence.rows : [];
    for (const row of rows) {
      if (row.command) append(`$ ${row.command}`, "info");
      if (row.output) append(trimLogText(row.output), row.status === "measured" ? "result" : "error");
      const metrics = row.status === "measured"
        ? `Source ${row.index}, CC ${row.controlChannelHz ? formatRfHz(Number(row.controlChannelHz)) : "--"}, gain ${row.gain ?? "auto"}: CC SNR ${formatFixed(row.snrDb, 1)} dB, CC peak ${formatFixed(row.peakDb, 1)} dB, noise ${formatFixed(row.noiseFloorDb, 1)} dB, CC offset ${formatFixed(row.peakOffsetHz, 0)} Hz`
        : `Source ${row.index}: ${row.issue || label(row.status || "failed")}`;
      append(metrics, row.status === "measured" ? "result" : "error");
    }
  } else if (experiment.type === "rf_validation_sweep") {
    const candidates = Array.isArray(evidence?.candidates) ? evidence.candidates : [];
    if (!candidates.length) {
      const powerRows = Array.isArray(evidence?.power?.rows) ? evidence.power.rows : [];
      for (const row of powerRows.slice(0, 8)) {
        if (row.command) append(`$ ${row.command}`, "info");
        if (row.output) append(trimLogText(row.output), row.status === "measured" ? "result" : "error");
        append(`RF screen: Source ${row.index}, ${row.controlChannelHz ? formatRfHz(Number(row.controlChannelHz)) : "--"}, rate ${row.sampleRate ? Number(row.sampleRate).toLocaleString() : "--"} sps, gain ${row.gain ?? "auto"}: ${row.status || "failed"}${row.issue ? ` - ${row.issue}` : ""}`, row.status === "measured" ? "result" : "error");
      }
    }
    for (const row of candidates.slice(0, 6)) {
      append(`Candidate ${row.id || ""}: Source ${row.sourceIndex}, ${row.controlChannelHz ? formatRfHz(Number(row.controlChannelHz)) : "--"}, gain ${row.gain ?? "auto"}, error ${row.errorHz ?? 0} Hz, RF ${formatFixed(row.snrDb, 1)} dB, P25 ${label(row.p25Status || "not run")}, TR ${label(row.metricsStatus || "not run")}, voice ${label(rfValidationCandidateVoiceStatus(row))}${row.voiceRealCalls ? ` (${row.voiceRealCalls} real)` : ""}`, rfValidationCandidatePassed(row) ? "result" : "error");
    }
  } else if (experiment.type === "control_channel_quality") {
    const aggregate = evidence?.aggregate;
    if (aggregate)
      append(`CC metrics: ${aggregate.ccSummaryDecodeLines ?? 0} decode lines, avg ${formatFixed(aggregate.ccSummaryAvgDecodeRate, 1)}/sec, zero ${formatFixed(aggregate.ccSummaryDecodeZeroPct, 1)}%, retunes ${aggregate.retunes ?? 0}`, experiment.status === "passed" ? "result" : "error");
  } else if (experiment.type === "voice_capture_trial") {
    const analysis = evidence?.trAnalysis;
    if (analysis) {
      append(`Real Calls clues: TR recorder starts ${analysis.trRecorderStarts ?? 0}, concluded ${analysis.trCallsConcluded ?? 0}, no-source grants ${analysis.noSourceGrantCount ?? 0}, callstream no-sample endings ${analysis.callstreamNoSampleEnds ?? 0}, CC avg ${formatFixed(analysis.avgDecodeRate, 1)}/sec, voice tuning error avg ${formatFixed(analysis.avgTuningErrorAbsHz, 0)} Hz.`, experiment.status === "passed" ? "result" : "error");
      const recommendations = Array.isArray(analysis.recommendations) ? analysis.recommendations.slice(0, 4) : [];
      for (const recommendation of recommendations)
        append(String(recommendation), "info");
    }
  } else if (experiment.type === "transcription_gate") {
    append(`Transcription provider: ${evidence?.provider || "--"} ${evidence?.endpoint || ""} (${evidence?.checkStatus || "not checked"}${evidence?.checkDetail ? `: ${evidence.checkDetail}` : ""}). Real calls ${evidence?.realCalls ?? 0}, usable transcripts ${evidence?.usableTranscripts ?? 0}.`, experiment.status === "passed" ? "result" : "error");
    const errors = Array.isArray(evidence?.recentTranscriptionErrors) ? evidence.recentTranscriptionErrors.slice(0, 3) : [];
    for (const error of errors)
      append(`Transcription error: ${String(error)}`, "error");
  }
}

function trimLogText(value: string, max = 3500) {
  value = (value ?? "").trim();
  return value.length > max ? value.slice(0, max).trimEnd() + "\n...[truncated]" : value;
}

type RfRunLogTab = "timeline" | "experiments" | "jobs";

function RfRunLogPane({ detail, logs, job, jobLogs, onClear, open, setOpen }: { detail: RfSurveyDetail; logs: RfRunLogLine[]; job: Job | null; jobLogs: JobLog[]; onClear: () => void; open: boolean; setOpen: (open: boolean) => void }) {
  const [tab, setTab] = useState<RfRunLogTab>("timeline");
  const [recentJobs, setRecentJobs] = useState<Job[]>([]);
  const [jobLogsById, setJobLogsById] = useState<Record<number, JobLog[]>>({});
  const [selectedJobId, setSelectedJobId] = useState<number | null>(null);
  const [selectedExperimentId, setSelectedExperimentId] = useState("");
  const [loadingJobs, setLoadingJobs] = useState(false);
  const [loadError, setLoadError] = useState("");
  const running = job != null && ["queued", "running", "paused", "canceling"].includes(job.status);
  const experiments = [...detail.experiments].sort((a, b) => b.createdAtUtc.localeCompare(a.createdAtUtc));
  const selectedExperiment = experiments.find(row => row.id === selectedExperimentId) ?? experiments[0];
  const effectiveSelectedJobId = selectedJobId ?? recentJobs[0]?.id ?? job?.id ?? null;
  const selectedJob = recentJobs.find(row => row.id === effectiveSelectedJobId) ?? (job?.id === effectiveSelectedJobId ? job : null);
  const selectedJobLogs = effectiveSelectedJobId ? mergeJobLogs(jobLogsById[effectiveSelectedJobId] ?? [], job?.id === effectiveSelectedJobId ? jobLogs : []) : [];
  const timeline = buildRfRunTimeline(detail, logs, recentJobs, job, jobLogsById, jobLogs);

  const loadRecentJobs = useCallback(async () => {
    setLoadingJobs(true);
    setLoadError("");
    try {
      const allJobs = await api.request<Job[]>("/api/v1/jobs");
      const sessionJobs = filterRfSessionJobs(allJobs, detail.session, job);
      setRecentJobs(sessionJobs);
      setSelectedJobId(current => current ?? sessionJobs[0]?.id ?? job?.id ?? null);
      const pairs = await Promise.all(sessionJobs.slice(0, 18).map(async row => {
        try {
          const rows = await api.request<JobLog[]>(`/api/v1/jobs/${row.id}/logs?afterId=0`);
          return [row.id, rows] as const;
        } catch {
          return [row.id, []] as const;
        }
      }));
      setJobLogsById(Object.fromEntries(pairs));
    } catch (error) {
      setLoadError(error instanceof Error ? error.message : "Unable to load recent job logs.");
    } finally {
      setLoadingJobs(false);
    }
  }, [detail.session.createdAtUtc, detail.session.updatedAtUtc, job?.id, job?.status]);

  useEffect(() => {
    if (!open) return;
    void loadRecentJobs();
  }, [open, loadRecentJobs]);

  useEffect(() => {
    if (!open || !running) return;
    const timer = window.setInterval(() => void loadRecentJobs(), 3000);
    return () => window.clearInterval(timer);
  }, [open, running, loadRecentJobs]);

  useEffect(() => {
    if (selectedExperimentId && experiments.some(row => row.id === selectedExperimentId)) return;
    setSelectedExperimentId(experiments[0]?.id ?? "");
  }, [detail.session.id, experiments.map(row => row.id).join("|"), selectedExperimentId]);

  return <div className={`setup-job-drawer rf-run-log-pane ${open ? "open" : ""}`}>
    <button className="setup-job-tab" onClick={() => setOpen(!open)}>{open ? "Hide Log" : "Run Log"}</button>
    <div className="setup-job-head rf-run-log-head">
      <div>
        <strong>RSW Run Log</strong>
        <span className="muted">{detail.session.siteLabel || detail.session.id}</span>
      </div>
      {running && <span className="pill">updating</span>}
      <button onClick={() => void loadRecentJobs()} disabled={loadingJobs}>{loadingJobs ? "Refreshing..." : "Refresh"}</button>
      <button onClick={() => setOpen(false)}>Hide</button>
      <button onClick={onClear}>Clear Live</button>
    </div>
    <div className="rf-run-log-tabs" role="tablist" aria-label="Run log document sections">
      <button className={tab === "timeline" ? "active" : ""} onClick={() => setTab("timeline")}>Timeline</button>
      <button className={tab === "experiments" ? "active" : ""} onClick={() => setTab("experiments")}>Runs</button>
      <button className={tab === "jobs" ? "active" : ""} onClick={() => setTab("jobs")}>Jobs</button>
    </div>
    {loadError && <div className="settings-message error">{loadError}</div>}
    {tab === "timeline" && <div className="rf-run-doc-body">
      <pre>{timeline || "No session timeline entries yet."}</pre>
    </div>}
    {tab === "experiments" && <div className="rf-run-doc-browser">
      <div className="rf-run-doc-list" aria-label="RSW run documents">
        {experiments.length === 0 && <span className="muted">No persisted RSW runs yet.</span>}
        {experiments.map(experiment => <button key={experiment.id} className={selectedExperiment?.id === experiment.id ? "active" : ""} onClick={() => setSelectedExperimentId(experiment.id)}>
          <strong>{label(experiment.type)}</strong>
          <span>{formatRunLogDate(experiment.createdAtUtc)}</span>
          <small className={`job-status status-${experiment.status}`}>{label(experiment.status)}</small>
        </button>)}
      </div>
      <div className="rf-run-doc-body">
        {selectedExperiment ? <pre>{formatExperimentDocument(selectedExperiment)}</pre> : <pre>No run selected.</pre>}
      </div>
    </div>}
    {tab === "jobs" && <div className="rf-run-doc-browser">
      <div className="rf-run-doc-list" aria-label="Recent job documents">
        {recentJobs.length === 0 && <span className="muted">{loadingJobs ? "Loading jobs..." : "No recent jobs found for this RSW session window."}</span>}
        {recentJobs.map(row => <button key={row.id} className={effectiveSelectedJobId === row.id ? "active" : ""} onClick={() => setSelectedJobId(row.id)}>
          <strong>Job {row.id}: {label(row.type)}</strong>
          <span>{formatRunLogDate(row.createdAtUtc)}</span>
          <small className={`job-status status-${row.status}`}>{label(row.status)}</small>
        </button>)}
      </div>
      <div className="rf-run-doc-body">
        {selectedJob ? <pre>{formatJobDocument(selectedJob, selectedJobLogs)}</pre> : <pre>No job selected.</pre>}
      </div>
    </div>}
  </div>;
}

function filterRfSessionJobs(jobs: Job[], session: RfSurveySession, activeJob: Job | null) {
  const start = Date.parse(session.createdAtUtc) - 10 * 60 * 1000;
  const end = Math.max(Date.now(), Date.parse(session.updatedAtUtc) + 10 * 60 * 1000);
  const activeJobId = activeJob?.id;
  return jobs
    .filter(row => row.id === activeJobId || (Date.parse(row.createdAtUtc) >= start && Date.parse(row.createdAtUtc) <= end))
    .sort((a, b) => b.id - a.id)
    .slice(0, 30);
}

function mergeJobLogs(primary: JobLog[], secondary: JobLog[]) {
  const byId = new Map<number, JobLog>();
  for (const row of [...primary, ...secondary])
    byId.set(row.id, row);
  return [...byId.values()].sort((a, b) => a.id - b.id);
}

function buildRfRunTimeline(detail: RfSurveyDetail, liveLogs: RfRunLogLine[], jobs: Job[], activeJob: Job | null, jobLogsById: Record<number, JobLog[]>, activeJobLogs: JobLog[]) {
  const entries: { at: string; text: string }[] = [];
  entries.push({ at: detail.session.createdAtUtc, text: `Session created: ${detail.session.siteLabel || detail.session.id}` });
  for (const row of detail.experiments)
    entries.push({ at: row.createdAtUtc, text: `Run ${label(row.type)}: ${label(row.status)}${row.blockingIssue ? ` - ${row.blockingIssue}` : row.resultSummary ? ` - ${row.resultSummary}` : ""}` });
  for (const row of liveLogs)
    entries.push({ at: row.createdAtUtc, text: `Live ${row.level.toUpperCase()}: ${row.text}` });
  const mergedJobs = activeJob && !jobs.some(row => row.id === activeJob.id) ? [activeJob, ...jobs] : jobs;
  for (const row of mergedJobs) {
    entries.push({ at: row.createdAtUtc, text: `Job ${row.id} ${label(row.type)}: ${label(row.status)}${row.message ? ` - ${row.message}` : ""}` });
    const logs = mergeJobLogs(jobLogsById[row.id] ?? [], activeJob?.id === row.id ? activeJobLogs : []);
    for (const log of logs.slice(-12))
      entries.push({ at: log.timestampUtc, text: `Job ${log.jobId} ${log.stream.toUpperCase()}: ${log.text}` });
  }
  return entries
    .sort((a, b) => Date.parse(a.at) - Date.parse(b.at))
    .map(row => `[${formatRunLogDate(row.at)}] ${row.text}`)
    .join("\n\n");
}

function formatExperimentDocument(experiment: RfSurveyExperiment) {
  const evidence = parseExperimentJson<any>(experiment.evidenceJson);
  const interpretation = parseExperimentJson<any>(experiment.interpretationJson);
  return [
    `${label(experiment.type)} (${experiment.id})`,
    `Status: ${label(experiment.status)}`,
    `Created: ${formatRunLogDate(experiment.createdAtUtc)}`,
    experiment.startedAtUtc ? `Started: ${formatRunLogDate(experiment.startedAtUtc)}` : "",
    experiment.finishedAtUtc ? `Finished: ${formatRunLogDate(experiment.finishedAtUtc)}` : "",
    experiment.hypothesis ? `Hypothesis: ${experiment.hypothesis}` : "",
    experiment.requiredSetup ? `Required setup: ${experiment.requiredSetup}` : "",
    experiment.resultSummary ? `Result: ${experiment.resultSummary}` : "",
    experiment.blockingIssue ? `Blocking issue: ${experiment.blockingIssue}` : "",
    "",
    "Interpretation",
    JSON.stringify(interpretation ?? {}, null, 2),
    "",
    "Evidence",
    JSON.stringify(evidence ?? {}, null, 2)
  ].filter(line => line !== "").join("\n");
}

function formatJobDocument(job: Job, logs: JobLog[]) {
  return [
    `Job ${job.id}: ${label(job.type)}`,
    `Status: ${label(job.status)}`,
    `Created: ${formatRunLogDate(job.createdAtUtc)}`,
    job.startedAtUtc ? `Started: ${formatRunLogDate(job.startedAtUtc)}` : "",
    job.finishedAtUtc ? `Finished: ${formatRunLogDate(job.finishedAtUtc)}` : "",
    `Progress: ${job.completed}/${job.total}${job.failed ? `, ${job.failed} failed` : ""}`,
    job.message ? `Message: ${job.message}` : "",
    "",
    "Logs",
    logs.length ? logs.map(row => `[${formatRunLogDate(row.timestampUtc)}] ${row.stream.toUpperCase()}: ${row.text}`).join("\n") : "No job log rows captured."
  ].filter(line => line !== "").join("\n");
}

function formatRunLogDate(value: string) {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString();
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
        <span>CC offset</span>
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
  const evidence = parseExperimentJson<any>(experiment.evidenceJson);
  const siteReadiness = Array.isArray(evidence?.siteReadiness) ? evidence.siteReadiness : [];
  if (siteReadiness.length > 0 && siteReadiness.every((site: any) => site?.monitorable === true))
    return "passed";
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
      <div><span>CC offset</span><code>{Number.isFinite(ccOffset) ? `${formatFixed(ccOffset, 0)} Hz` : "--"}</code></div>
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
        <p>PizzaWave runs the displayed SDR command against the selected control channel and writes a short IQ capture into the survey artifact folder. RTL-SDR captures are treated as unsigned 8-bit I/Q samples; Airspy captures are treated as signed 16-bit I/Q samples.</p>
        <p>The analyzer reads the first analysis window from that file, removes DC bias, applies a Hamming window, then computes a simple FFT. CC peak/SNR use the strongest bin inside the tuned control-channel window. Noise floor is the median of the lower 80% of FFT-bin power values. Strongest offset is the strongest bin anywhere in the capture. Clip percentage counts samples near the ADC rails, and a very high strongest peak flags overload risk.</p>
        <p>These are quick relative measurements for comparing RF path changes and SDR settings. They are not calibrated dBm/dBFS lab measurements, and decode/call quality still has to be proven by the later P25 and call-quality steps.</p>
      </div>
    </details>
    <div className="rf-power-table">
      <div className="rf-power-row header"><span>SDR</span><span>CC</span><span>Status</span><span>Quality</span><span>Gain</span><span>CC SNR</span><span>CC Peak</span><span>Noise</span><span>CC Offset</span><span>Strongest</span><span>Clip</span><span>Output</span></div>
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

function RfSurveyConsolePanel({ surveys, reload }: { surveys: RfSurveyList | null; reload: () => Promise<void> }) {
  const [detail, setDetail] = useState<RfSurveyDetail | null>(null);
  const [creating, setCreating] = useState(false);
  const [toolBusy, setToolBusy] = useState(false);
  const [experimentBusy, setExperimentBusy] = useState("");
  const [p25Preview, setP25Preview] = useState<RfSurveyP25ProbePreview | null>(null);
  const [p25Template, setP25Template] = useState("");
  const [p25Saving, setP25Saving] = useState(false);
  const [exportBusy, setExportBusy] = useState(false);
  const [trBusy, setTrBusy] = useState("");
  const [candidateBusy, setCandidateBusy] = useState(false);
  const [captureTrialBusy, setCaptureTrialBusy] = useState(false);
  const [candidate, setCandidate] = useState<RfSurveyCandidate | null>(null);
  const [candidateDraft, setCandidateDraft] = useState({ trialType: "control_channel", controlChannelHz: "", sourceIndex: "0", centerHz: "", gain: "", sampleRate: "", durationSeconds: "60" });
  const [message, setMessage] = useState("");
  const [path, setPath] = useState<RfSurveyPathProfile>({
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
    chain: []
  });
  async function createSurvey() {
    if (!confirmAction("New Radio Setup workspace?", "Radio Setup may briefly pause trunk-recorder during bounded measurement runs. This first step only creates the workspace record and does not stop TR.")) return;
    setCreating(true);
    setMessage("");
    try {
      const created = await api.request<RfSurveyDetail>(radioSetupApi, {
        method: "POST",
        body: JSON.stringify({ mode: "guided", rfPath: path })
      });
      setDetail(created);
      void loadP25SettingsAndPreview(created.session.id);
      await reload();
      setMessage(`Created survey ${created.session.id}.`);
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Unable to create radio setup workspace.");
    } finally {
      setCreating(false);
    }
  }
  async function openSurvey(id: string) {
    setMessage("");
    const next = await api.request<RfSurveyDetail>(`${radioSetupApi}/${encodeURIComponent(id)}`);
    setDetail(next);
    void loadP25SettingsAndPreview(next.session.id);
  }
  async function loadP25SettingsAndPreview(id: string) {
    try {
      const [settings, preview] = await Promise.all([
        api.request<{ section: string; values: any }>("/api/v1/settings/rf-survey"),
        api.request<RfSurveyP25ProbePreview>(`${radioSetupApi}/${encodeURIComponent(id)}/p25-probe-preview`)
      ]);
      setP25Template(settings.values?.p25ProbeCommandTemplate ?? "");
      setP25Preview(preview);
    } catch {
      setP25Preview(null);
    }
  }
  async function saveP25Template() {
    if (!detail) return;
    setP25Saving(true);
    setMessage("");
    try {
      await api.request("/api/v1/settings/rf-survey", {
        method: "POST",
        body: JSON.stringify({ values: { p25ProbeCommandTemplate: p25Template, p25ProbeWorkingDirectory: "/tmp", p25ProbeDurationSeconds: 45, p25ProbeTimeoutSeconds: 90 } })
      });
      await loadP25SettingsAndPreview(detail.session.id);
      setMessage("P25 probe command template saved.");
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Unable to save P25 probe template.");
    } finally {
      setP25Saving(false);
    }
  }
  async function exportPlan() {
    if (!detail) return;
    setExportBusy(true);
    setMessage("");
    try {
      const response = await api.download(`${radioSetupApi}/${encodeURIComponent(detail.session.id)}/export-plan/download`, { method: "POST" });
      const fileName = await downloadFileFromResponse(response, `radio-setup-${detail.session.id}.md`);
      setMessage(`Downloaded ${fileName}`);
      await reload();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Unable to export radio setup plan.");
    } finally {
      setExportBusy(false);
    }
  }
  async function runTrAction(action: "stop" | "apply-temp-config" | "restore-config") {
    if (!detail) return;
    const prompt = action === "stop"
      ? ["Pause trunk-recorder?", "This legacy action briefly pauses and immediately restarts trunk-recorder. Experiments manage this automatically."]
      : action === "apply-temp-config"
        ? ["Apply temporary TR config?", "This backs up the current TR config, installs the survey candidate config, and restarts TR."]
        : ["Restore original TR config?", "This reinstalls the Radio Setup backup and restarts TR to resume the previous configuration."];
    if (!confirmAction(prompt[0], prompt[1])) return;
    setTrBusy(action);
    setMessage("");
    try {
      const path = action === "stop" ? "stop" : action;
      const result = await api.request<RfSurveyTrActionResult>(`${radioSetupApi}/${encodeURIComponent(detail.session.id)}/tr/${path}`, {
        method: "POST",
        body: JSON.stringify({ confirmed: true, restartTr: true })
      });
      setMessage(result.message + (result.backupPath ? ` Backup: ${result.backupPath}` : ""));
      const next = await api.request<RfSurveyDetail>(`${radioSetupApi}/${encodeURIComponent(detail.session.id)}`);
      setDetail(next);
      await reload();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "TR action failed.");
    } finally {
      setTrBusy("");
    }
  }
  async function generateCandidate() {
    if (!detail) return;
    setCandidateBusy(true);
    setMessage("");
    try {
      const generated = await api.request<RfSurveyCandidate>(`${radioSetupApi}/${encodeURIComponent(detail.session.id)}/tr/candidate`, {
        method: "POST",
        body: JSON.stringify({
          trialType: candidateDraft.trialType,
          controlChannelHz: candidateDraft.controlChannelHz ? Number(candidateDraft.controlChannelHz) : null,
          sourceIndex: candidateDraft.sourceIndex ? Number(candidateDraft.sourceIndex) : null,
          centerHz: candidateDraft.centerHz ? Number(candidateDraft.centerHz) : null,
          gain: candidateDraft.gain || null,
          sampleRate: candidateDraft.sampleRate ? Number(candidateDraft.sampleRate) : null
        })
      });
      setCandidate(generated);
      setMessage(generated.summary);
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Unable to generate candidate config.");
    } finally {
      setCandidateBusy(false);
    }
  }
  async function runCaptureTrial() {
    if (!detail) return;
    if (!confirmAction("Run capture trial?", "This applies the candidate TR config, restarts TR, waits for the trial window, evaluates captured calls, then restores the previous config.")) return;
    setCaptureTrialBusy(true);
    setMessage("");
    try {
      const result = await api.request<RfSurveyCaptureTrialResult>(`${radioSetupApi}/${encodeURIComponent(detail.session.id)}/tr/run-capture-trial`, {
        method: "POST",
        body: JSON.stringify({ confirmed: true, restartTr: true, restoreAfter: true, durationSeconds: Number(candidateDraft.durationSeconds) || 60 })
      });
      const next = await api.request<RfSurveyDetail>(`${radioSetupApi}/${encodeURIComponent(detail.session.id)}`);
      setDetail(next);
      await reload();
      setMessage(result.voiceCapture.blockingIssue || result.voiceCapture.resultSummary || `Capture trial waited ${result.waitedSeconds}s.`);
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Capture trial failed.");
    } finally {
      setCaptureTrialBusy(false);
    }
  }
  async function runToolPrep() {
    if (!detail) return;
    setToolBusy(true);
    setMessage("");
    try {
      const toolPrep = await api.request<RfSurveyDetail["toolPrep"]>(`${radioSetupApi}/${encodeURIComponent(detail.session.id)}/tool-prep`, { method: "POST" });
      const next = await api.request<RfSurveyDetail>(`${radioSetupApi}/${encodeURIComponent(detail.session.id)}`);
      setDetail(next);
      await reload();
      setMessage(toolPrep?.readyForControlChannelTests ? "Tool prep complete. Control-channel testing can proceed." : "Tool prep completed with blockers.");
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Tool prep failed.");
    } finally {
      setToolBusy(false);
    }
  }
  async function runExperiment(type: string) {
    if (!detail) return;
    if ((type === "sdr_inventory" || type === "control_channel_p25_probe") &&
      !confirmAction("Run RF experiment?", "Radio Setup pauses trunk-recorder only for the measurement and restarts it automatically afterward.")) return;
    setExperimentBusy(type);
    setMessage("");
    try {
      const experiment = await api.request<RfSurveyDetail["experiments"][number]>(`${radioSetupApi}/${encodeURIComponent(detail.session.id)}/experiments/run`, {
        method: "POST",
        body: JSON.stringify({ type })
      });
      const next = await api.request<RfSurveyDetail>(`${radioSetupApi}/${encodeURIComponent(detail.session.id)}`);
      setDetail(next);
      await reload();
      setMessage(experiment.blockingIssue || experiment.resultSummary || `Experiment ${label(type)} completed.`);
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Experiment failed.");
    } finally {
      setExperimentBusy("");
    }
  }
  const sessions = surveys?.sessions ?? [];
  return <div className="rf-survey-layout">
    <div className="card">
      <div className="setup-job-head">
        <div>
          <h3>Radio Setup</h3>
          <p className="muted">Reusable radio setup workspaces seeded from setup. Guided mode requires AI Insights; passing requires real captured calls and usable transcription.</p>
        </div>
        <button disabled={creating} onClick={() => void createSurvey()}>{creating ? "Creating..." : "New Workspace"}</button>
      </div>
      <div className="settings-fields">
        <SettingInput label="Antenna" description="Brand/model. Yagi is the expected first path, but this remains location agnostic." value={path.antenna} onChange={v => setPath(current => ({ ...current, antenna: v }))} />
        <SettingInput label="Antenna type" description="Examples: yagi, omni, whip, discone." value={path.antennaType} onChange={v => setPath(current => ({ ...current, antennaType: v }))} />
        <SettingInput label="Position notes" description="Aim, bearing, polarization, height, indoor/outdoor, room/window, or other repeatability notes." value={path.positionNotes} onChange={v => setPath(current => ({ ...current, positionNotes: v }))} />
        <SettingInput label="Connectors/adapters" description="Connector and adapter chain from antenna to SDR." value={path.connectorChain} onChange={v => setPath(current => ({ ...current, connectorChain: v }))} />
        <SettingInput label="Coax" description="Type and length." value={path.coax} onChange={v => setPath(current => ({ ...current, coax: v }))} />
        <SettingInput label="Splitter/multicoupler" description="Passive splitter, active multicoupler, or direct path." value={path.splitterOrMulticoupler} onChange={v => setPath(current => ({ ...current, splitterOrMulticoupler: v }))} />
        <SettingInput label="LNA" description="Model, placement, power method, and bias tee state." value={path.lna} onChange={v => setPath(current => ({ ...current, lna: v }))} />
        <SettingInput label="Filters" description="Band-pass or other filtering in the path." value={path.filters} onChange={v => setPath(current => ({ ...current, filters: v }))} />
      </div>
      {message && <p className="setup-note">{message}</p>}
    </div>
    <div className="card">
      <h3>Workspace History</h3>
      <p className="muted">Artifacts: {surveys?.artifactRoot ?? "--"}</p>
      {!sessions.length ? <p className="muted">No Radio Setup workspaces have been created yet.</p> : <div className="job-table">
        <table>
          <thead><tr><th>Updated</th><th>Name</th><th>Verdict</th><th>RF Path</th><th></th></tr></thead>
          <tbody>{sessions.map(row => <tr key={row.id}>
            <td>{new Date(row.updatedAtUtc).toLocaleString()}</td>
            <td><strong>{row.siteLabel || row.systemShortName || row.id}</strong><br /><span className="muted">{row.sdrSummary}</span></td>
            <td>{label(row.verdict)}<br /><span className="muted">{label(row.stability)}</span></td>
            <td>{row.rfPathSummary}</td>
            <td><button onClick={() => void openSurvey(row.id)}>Open</button></td>
          </tr>)}</tbody>
        </table>
      </div>}
    </div>
    {detail && <div className="card">
      <div className="setup-job-head">
        <div>
          <h3>{detail.session.siteLabel || detail.session.id}</h3>
          <p className="muted">{detail.session.id} - {detail.session.artifactPath}</p>
        </div>
        <div className="setup-button-row">
          <button disabled={toolBusy} onClick={() => void runToolPrep()}>{toolBusy ? "Checking..." : "Run Tool Prep"}</button>
          <button disabled={exportBusy} onClick={() => void exportPlan()}>{exportBusy ? "Exporting..." : "Export Plan"}</button>
        </div>
      </div>
      <div className="audit-kpis">
        <Kpi label="Guided Survey" value={detail.toolPrep?.readyForGuidedSurvey ? "Ready" : "Blocked"} status={detail.toolPrep?.readyForGuidedSurvey ? "ok" : "warning"} subtext="AI plus required tools" />
        <Kpi label="P25 Tests" value={detail.toolPrep?.readyForControlChannelTests ? "Ready" : "Blocked"} status={detail.toolPrep?.readyForControlChannelTests ? "ok" : "warning"} subtext="SDR and P25 tooling" />
        <Kpi label="Voice Capture" value={detail.toolPrep?.readyForVoiceCapture ? "Ready" : "Blocked"} status={detail.toolPrep?.readyForVoiceCapture ? "ok" : "warning"} subtext="trunk-recorder acceptance path" />
        <Kpi label="Transcription" value={detail.toolPrep?.readyForTranscriptionGate ? "Ready" : "Blocked"} status={detail.toolPrep?.readyForTranscriptionGate ? "ok" : "error"} subtext="hard pass gate" />
      </div>
      {detail.profile.warnings.length > 0 && <div className="setup-warning-list">{detail.profile.warnings.map(warning => <div key={warning}>{warning}</div>)}</div>}
      {detail.toolPrep?.warnings.length ? <div className="setup-warning-list">{detail.toolPrep.warnings.map(warning => <div key={warning}>{warning}</div>)}</div> : null}
      <h4>Tools</h4>
      <MetricTable title="Tool Prep" rows={(detail.toolPrep?.tools ?? []).map(tool => ({
        metric: tool.label,
        value: tool.installed ? "Installed" : tool.required ? "Missing" : "Optional",
        notes: `${tool.purpose} ${tool.version ? `Version: ${tool.version}` : tool.installHint}`,
        isIssue: tool.required && !tool.installed
      }))} />
      <h4>Setup-Derived Profile</h4>
      <div className="setup-review">
        <div><span>System</span><code>{detail.profile.systemShortName || "--"}</code></div>
        <div><span>Control channels</span><code>{formatFrequencyList(detail.profile.controlChannelsHz)}</code></div>
        <div><span>Voice frequencies</span><code>{formatFrequencyList(detail.profile.voiceFrequenciesHz)}</code></div>
        <div><span>Sources</span><code>{detail.profile.sources.length.toLocaleString()}</code></div>
      </div>
      <h4>Next Experiments</h4>
      <div className="service-action-grid">
        {(detail.nextExperiments ?? []).filter(plan => plan.type !== "tool_prep").map(plan => <div className="service-action-card" key={plan.type}>
          <div>
            <strong>{plan.label}</strong>
            <span className={`job-status ${plan.enabled ? "status-completed" : "status-paused"}`}>{plan.enabled ? "ready" : "blocked"}</span>
          </div>
          <span className="muted">{plan.purpose}</span>
          {plan.blockingIssue && <span className="settings-message error">{plan.blockingIssue}</span>}
          <button disabled={!plan.enabled || Boolean(experimentBusy)} onClick={() => void runExperiment(plan.type)}>{experimentBusy === plan.type ? "Running..." : "Run"}</button>
        </div>)}
      </div>
      <h4>TR Orchestration</h4>
      <div className="service-action-grid">
        <div className="service-action-card">
          <div><strong>Apply temp config</strong><span className="job-status status-running">restart</span></div>
          <span className="muted">Backs up live TR config, installs the survey candidate config, and restarts TR.</span>
          <button disabled={Boolean(trBusy)} onClick={() => void runTrAction("apply-temp-config")}>{trBusy === "apply-temp-config" ? "Applying..." : "Apply Temp Config"}</button>
        </div>
        <div className="service-action-card">
          <div><strong>Restore config</strong><span className="job-status status-completed">rollback</span></div>
          <span className="muted">Restores the saved pre-survey TR config and restarts TR.</span>
          <button disabled={Boolean(trBusy)} onClick={() => void runTrAction("restore-config")}>{trBusy === "restore-config" ? "Restoring..." : "Restore TR Config"}</button>
        </div>
      </div>
      <h4>Candidate Trial</h4>
      <div className="settings-fields">
        <SettingSelect label="Trial type" description="Change exactly one TR variable for the next candidate." value={candidateDraft.trialType} options={["control_channel", "source_center", "source_gain", "sample_rate"]} onChange={v => setCandidateDraft(current => ({ ...current, trialType: v }))} />
        <SettingInput label="Control channel Hz" description="Used for control-channel trials." value={candidateDraft.controlChannelHz} onChange={v => setCandidateDraft(current => ({ ...current, controlChannelHz: v }))} inputMode="numeric" />
        <SettingInput label="Source index" description="TR source index to change." value={candidateDraft.sourceIndex} onChange={v => setCandidateDraft(current => ({ ...current, sourceIndex: v }))} inputMode="numeric" />
        <SettingInput label="Center Hz" description="Used for source-center trials." value={candidateDraft.centerHz} onChange={v => setCandidateDraft(current => ({ ...current, centerHz: v }))} inputMode="numeric" />
        <SettingInput label="Gain" description="Used for source-gain trials." value={candidateDraft.gain} onChange={v => setCandidateDraft(current => ({ ...current, gain: v }))} />
        <SettingInput label="Sample rate" description="Used for sample-rate trials." value={candidateDraft.sampleRate} onChange={v => setCandidateDraft(current => ({ ...current, sampleRate: v }))} inputMode="numeric" />
        <SettingInput label="Trial seconds" description="Capture window after apply/restart." value={candidateDraft.durationSeconds} onChange={v => setCandidateDraft(current => ({ ...current, durationSeconds: v }))} inputMode="numeric" />
      </div>
      <div className="setup-button-row">
        <button disabled={candidateBusy} onClick={() => void generateCandidate()}>{candidateBusy ? "Generating..." : "Generate Candidate"}</button>
        <button disabled={captureTrialBusy || !candidate} onClick={() => void runCaptureTrial()}>{captureTrialBusy ? "Running..." : "Run Capture Trial"}</button>
      </div>
      {candidate && <div className="setup-review">
        <div><span>Candidate</span><code>{candidate.candidatePath}</code></div>
        <div><span>Diff</span><code>{candidate.diffPath}</code></div>
        <div><span>Summary</span><code>{candidate.summary}</code></div>
      </div>}
      {candidate?.diffLines.length ? <pre className="log-box">{candidate.diffLines.join("\n")}</pre> : null}
      <h4>P25 Probe Command</h4>
      <div className="settings-fields">
        <SettingTextarea label="Command template" description="Expert command run by pizzad, with placeholders such as {frequency_hz}, {frequency_mhz}, {sample_rate}, {gain}, {serial}, {duration_seconds}, and {output_dir}." value={p25Template} onChange={setP25Template} />
      </div>
      <div className="setup-job-head">
        <div>
          <p className="muted">Preview: {p25Preview?.ready ? "ready" : p25Preview?.blockingIssue || "not configured"}</p>
          {p25Preview?.command && <code>{p25Preview.command}</code>}
        </div>
        <button disabled={p25Saving} onClick={() => void saveP25Template()}>{p25Saving ? "Saving..." : "Save P25 Template"}</button>
      </div>
      <h4>Experiments</h4>
      {!detail.experiments.length ? <p className="muted">No experiments have run yet. Tool prep is the first executable step.</p> : <MetricTable title="Experiments" rows={detail.experiments.map(experiment => ({ metric: experiment.type, value: experiment.status, notes: experiment.blockingIssue || experiment.resultSummary || experiment.hypothesis, isIssue: Boolean(experiment.blockingIssue) || experiment.status === "failed" }))} />}
    </div>}
  </div>;
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

function RecommendationsPanel({ recommendations, busy, message, onOpen, onState }: { recommendations: SystemRecommendations | null; busy: boolean; message: string; onOpen: (target: { topTab: string; subTab: string }) => void; onState: (id: string, action: "ignore" | "restore" | "reset-baseline") => Promise<void> }) {
  const [activeRunbookId, setActiveRunbookId] = useState<string | null>(null);
  if (!recommendations) return <div className="card">Loading recommendations...</div>;
  const ignoredItems = recommendations.ignoredItems ?? [];
  const activeRunbook = [...recommendations.items, ...ignoredItems].find(item => item.id === activeRunbookId);
  return <div className="trouble-panel recommendations-panel">
    {message && <div className={message.toLowerCase().includes("fail") || message.toLowerCase().includes("error") ? "settings-message error" : "settings-message ok"}>{message}</div>}
    {activeRunbook?.runbook && <RunbookDetail item={activeRunbook} busy={busy} onClose={() => setActiveRunbookId(null)} onOpen={onOpen} onState={onState} />}
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

function RunbookDetail({ item, busy, onClose, onOpen, onState }: { item: NonNullable<SystemRecommendations["items"][number]>; busy: boolean; onClose: () => void; onOpen: (target: { topTab: string; subTab: string }) => void; onState: (id: string, action: "ignore" | "restore" | "reset-baseline") => Promise<void> }) {
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
          <h3>Talkgroup Priority Workbench</h3>
          <p className="muted">Review high-load talkgroups here, then manage disables and category policy in Settings &gt; Talkgroup Catalog.</p>
        </div>
      </div>
      <table className="table runbook-tg-table">
        <thead><tr><th>Talkgroup</th><th>Category</th><th>Recent Load</th><th>Pending</th><th>Reason</th></tr></thead>
        <tbody>{runbook.talkgroupCandidates.map(row => <tr key={`${row.systemShortName}-${row.talkgroup}`}>
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
  const [preview, setPreview] = useState<BackupRestorePreview | null>(null);
  const [showHelp, setShowHelp] = useState(false);
  const fileRef = useRef<HTMLInputElement | null>(null);
  useEffect(() => { void loadBackups(); }, []);
  useEffect(() => { void loadBackupEstimate(); }, [audioWindow]);

  const audioWindowOptions = [
    { value: "24h", label: "Last 24h" },
    { value: "7d", label: "Last 7d" },
    { value: "30d", label: "Last 30d" },
    { value: "60d", label: "Last 60d" },
    { value: "all", label: "All" }
  ];
  const audioWindowLabel = audioWindowOptions.find(option => option.value === audioWindow)?.label ?? "All";

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
    setMessage("Creating backup archive...");
    try {
      const result = await api.request<BackupCreateResult>("/api/v1/system/backups", { method: "POST", body: JSON.stringify({ audioWindow }) });
      setMessage(`Created ${result.name}: ${formatBytes(result.bytes)} across ${result.fileCount.toLocaleString()} file(s).${result.warnings.length ? " Review warnings below." : ""}`);
      await loadBackups();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Backup failed.");
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
    if (!confirmAction("Stage restore archive?", "This does not overwrite live data yet. It validates and stages the archive, then returns PizzaWave to setup mode so the restore can be reviewed and applied from the wizard.")) return;
    const form = new FormData();
    form.append("file", file);
    setBusy("restore");
    setMessage("Uploading and validating restore archive...");
    try {
      const result = await api.request<BackupRestorePreview>("/api/v1/system/backups/restore", { method: "POST", body: form });
      setPreview(result);
      setMessage(`Restore staged from ${result.manifest.stackName}. Setup wizard will open for verification.`);
      await reload();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Restore staging failed.");
    } finally {
      setBusy("");
    }
  }

  async function stageLocalRestore(row: BackupArchive) {
    if (!confirmAction("Stage local backup restore?", `This stages ${row.name} from ${row.path}. It does not overwrite live data yet; the setup wizard will review and verify the archive before apply.`)) return;
    setBusy(`restore-${row.name}`);
    setMessage(`Staging ${row.name} for restore...`);
    try {
      const result = await api.request<BackupRestorePreview>(`/api/v1/system/backups/${encodeURIComponent(row.name)}/restore`, { method: "POST" });
      setPreview(result);
      setMessage(`Restore staged from ${result.manifest.stackName}. Setup wizard will open for verification.`);
      await reload();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Restore staging failed.");
    } finally {
      setBusy("");
    }
  }

  async function beginMigration() {
    if (!confirmAction("Begin migration?", "This does not clear data yet. PizzaWave will enter migration setup mode, pause live ingest, and show migration choices. The destructive reset happens only after a second confirmation inside the wizard.")) return;
    setBusy("migration");
    setMessage("Entering migration mode...");
    try {
      const result = await api.request<MigrationActionResult>("/api/v1/system/migration/begin", { method: "POST" });
      setMessage(result.message);
      await reload();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Unable to begin migration.");
    } finally {
      setBusy("");
    }
  }

  return <div className="system-manager-grid">
    <div className="card">
      <h3>Backup</h3>
      <p className="muted">Create a portable archive of PizzaWave state for migration or disaster recovery.</p>
      <label className="setting-field compact-setting">
        <span>Recorded audio<small>SQLite and configuration are always backed up fully. This controls recorded call audio by file timestamp.</small></span>
        <select value={audioWindow} onChange={event => setAudioWindow(event.target.value)}>{audioWindowOptions.map(option => <option key={option.value} value={option.value}>{option.label}</option>)}</select>
      </label>
      <div className="setup-button-row">
        <button disabled={Boolean(busy)} onClick={() => void createBackup()}>{busy === "create" ? "Creating..." : "Create Backup"}</button>
        <button disabled={Boolean(busy)} onClick={() => void loadBackups()}>Refresh</button>
        <button type="button" onClick={() => setShowHelp(true)}>Help</button>
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
      {message && <p className={message.toLowerCase().includes("fail") || message.toLowerCase().includes("unable") ? "settings-message error" : "settings-message ok"}>{message}</p>}
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
          <p><strong>Restore</strong> is staged first and then reviewed in the setup wizard. Applying restore overwrites backed-up files and restarts services, so setup checks must pass before normal operation resumes.</p>
        </div>
      </div>
    </div>}
    <div className="card">
      <h3>Restore</h3>
      <p className="muted">Restore is staged first, then the first-run wizard performs review and verification before applying the backup.</p>
      <input ref={fileRef} type="file" accept=".zip,application/zip" />
      <div className="setup-button-row">
        <button className="danger-button" disabled={Boolean(busy)} onClick={() => void stageRestore()}>{busy === "restore" ? "Staging..." : "Stage Restore"}</button>
      </div>
      {preview && <BackupRestorePreviewCard preview={preview} />}
    </div>
    <div className="card">
      <h3>Migration</h3>
      <p className="muted">Use migration when this rig is moving to a new geography/site. It preserves portable settings but rebuilds TR systems, frequencies, talkgroups, areas, calls, audio, and vectors from scratch.</p>
      <div className="setup-button-row">
        <button className="danger-button" disabled={Boolean(busy)} onClick={() => void beginMigration()}>{busy === "migration" ? "Starting..." : "Begin Migration..."}</button>
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
          <td><a href={`/api/v1/system/backups/${encodeURIComponent(row.name)}`}>Download</a> <button disabled={Boolean(busy)} onClick={() => void stageLocalRestore(row)}>{busy === `restore-${row.name}` ? "Staging..." : "Restore"}</button> <button className="danger-button" disabled={Boolean(busy)} onClick={() => void deleteBackup(row.name)}>Delete</button></td>
        </tr>)}</tbody></table>}
    </div>
  </div>;

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
  const [rrSerials, setRrSerials] = useState("");
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
          siteNames: rrSites.trim(),
          sdrSerials: rrSerials.trim()
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
        setMessage("Applied targeted Radio Setup source changes.");
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
      <textarea aria-label="Workspace TR config JSON" value={configText} spellCheck={false} onChange={e => setConfigText(e.target.value)} />
    </div>
  </div>;

  return <div className="tr-config-editor">
    <div className="tr-config-editor-toolbar">
      <div>
        <h3>Active TR Config</h3>
        <div className="muted">Live: {editor?.livePath || "--"}</div>
        {editor?.hasDraft && <div className="settings-message">{rawOnly ? "Workspace draft" : "Draft"}: {editor.draftPath}</div>}
      </div>
      <div className="tr-config-editor-actions">
        {onOpenRadioSetup && <button type="button" onClick={onOpenRadioSetup}>Open Radio Setup</button>}
        <button className="danger-button" disabled={busy === "save" || !!parseError} onClick={() => void saveAndRestart()}>{busy === "save" ? "Applying..." : applyLabel}</button>
      </div>
    </div>
    {onOpenRadioSetup && <div className="setup-note">This is the expert config-draft surface. For new SDRs, site changes, RF path changes, or validation work, use the shared Radio Setup workspace.</div>}
    {message && <p className={message.toLowerCase().includes("invalid") || message.toLowerCase().includes("fail") ? "settings-message error" : "settings-message ok"}>{message}</p>}
    {parseError && <p className="settings-message error">Raw JSON is invalid: {parseError}</p>}
    <div className="tr-config-editor-grid">
      <div className="tr-config-controls">
        <section className="card">
          <h3>Systems</h3>
          <p className="muted">These site blocks drive Radio Setup ground truth, source planning, health checks, and troubleshooting.</p>
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
          <p className="muted">Draft a replacement from RadioReference, then review the JSON before applying. This reuses the first-run TR config builder.</p>
          <div className="tr-config-rr-grid">
            <label>SID <input value={rrSid} onChange={e => setRrSid(e.target.value)} /></label>
            <label>Site filters <input value={rrSites} onChange={e => setRrSites(e.target.value)} placeholder="Hamilton, Cleveland" /></label>
            <label>SDR serials <input value={rrSerials} onChange={e => setRrSerials(e.target.value)} /></label>
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
    const half = Math.floor(Math.max(0, rate) * 0.46875);
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
    <div className="tr-config-review-table">
      <div className="tr-config-review-row header"><span>System</span><span>Control channels</span><span>Covered by source</span></div>
      {rows.map(row => <div className={row.uncovered.length ? "tr-config-review-row warning" : "tr-config-review-row"} key={row.shortName}>
        <span><strong>{row.shortName}</strong></span>
        <span>{row.channels.length ? row.channels.map(formatRfHz).join(", ") : "--"}</span>
        <span>
          {row.covered.length
            ? row.covered.map(index => {
              const source = sourceWindows[index];
              return <code key={index}>#{index} {formatRfHz(source.low)}-{formatRfHz(source.high)}</code>;
            })
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
        <p className="muted">Timestamped config backups are created before Radio Setup applies source changes and before TR config editor saves.</p>
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

type MigrationResetOptions = {
  createBackup: boolean;
  backupAudioWindow: string;
  preserveBranding: boolean;
  preserveTranscription: boolean;
  preserveAiInsights: boolean;
  preserveEmbeddings: boolean;
  preserveAlerts: boolean;
  preserveRfSurvey: boolean;
};

const defaultMigrationResetOptions: MigrationResetOptions = {
  createBackup: true,
  backupAudioWindow: "all",
  preserveBranding: true,
  preserveTranscription: true,
  preserveAiInsights: true,
  preserveEmbeddings: true,
  preserveAlerts: true,
  preserveRfSurvey: true
};

function setupWizardDraftKey(status: SetupStatus) {
  const setup = status.values?.setup ?? {};
  return `pizzawave-setup-wizard-draft-v2-${setup.migrationResetAtUtc || setup.migrationStartedAtUtc || "default"}`;
}

function readSetupWizardDraft(status: SetupStatus): Record<string, any> {
  try {
    return JSON.parse(localStorage.getItem(setupWizardDraftKey(status)) || "{}");
  } catch {
    return {};
  }
}

function writeSetupWizardDraft(status: SetupStatus, patch: Record<string, any>) {
  try {
    const key = setupWizardDraftKey(status);
    const current = JSON.parse(localStorage.getItem(key) || "{}");
    localStorage.setItem(key, JSON.stringify({ ...current, ...patch }));
  } catch {
    // Browser storage is best-effort; server-side setup save remains authoritative.
  }
}

function SetupWizard({ status, reload, onComplete }: { status: SetupStatus; reload: () => Promise<void>; onComplete?: () => void }) {
  const [setupWizardDraft] = useState(() => readSetupWizardDraft(status));
  const [draft, setDraft] = useState<any>(() => setupDraftFromStatus(status));
  const [trConfigJson, setTrConfigJson] = useState("");
  const [talkgroupsCsv, setTalkgroupsCsv] = useState(setupWizardDraft.talkgroupsCsv ?? "");
  const [talkgroupSid, setTalkgroupSid] = useState(status.values?.setup?.radioReferenceSid || setupWizardDraft.radioReferenceSid || "");
  const [talkgroupSidManuallyEdited, setTalkgroupSidManuallyEdited] = useState(Boolean(setupWizardDraft.talkgroupSidManuallyEdited));
  const [includeExcludedTalkgroups, setIncludeExcludedTalkgroups] = useState(Boolean(setupWizardDraft.includeExcludedTalkgroups));
  const [talkgroupPreview, setTalkgroupPreview] = useState<SetupTalkgroupPreview | null>(null);
  const [trDraftSid, setTrDraftSid] = useState(status.values?.setup?.radioReferenceSid || setupWizardDraft.radioReferenceSid || "");
  const [trDraftUrl, setTrDraftUrl] = useState("");
  const [trDraftHtml, setTrDraftHtml] = useState("");
  const [trDraftSites, setTrDraftSites] = useState("");
  const [trSiteList, setTrSiteList] = useState<SetupTrConfigSites | null>(setupWizardDraft.trSiteList ?? null);
  const [trSiteSearch, setTrSiteSearch] = useState(setupWizardDraft.trSiteSearch ?? "");
  const [selectedTrSites, setSelectedTrSites] = useState<string[]>(Array.isArray(setupWizardDraft.selectedTrSites) ? setupWizardDraft.selectedTrSites : []);
  const [trConfigStage, setTrConfigStage] = useState<"sites" | "sdr">(setupWizardDraft.trConfigStage === "sdr" ? "sdr" : "sites");
  const [trDraftSerials, setTrDraftSerials] = useState(setupWizardDraft.trDraftSerials ?? "");
  const [trDraftRate, setTrDraftRate] = useState(setupWizardDraft.trDraftRate && setupWizardDraft.trDraftRate !== "2048000" ? setupWizardDraft.trDraftRate : "2400000");
  const [trDraft, setTrDraft] = useState<SetupTrConfigDraft | null>(null);
  const [trSourcePlan, setTrSourcePlan] = useState<SetupTrConfigSourcePlan | null>(null);
  const [trSourcePlanLoading, setTrSourcePlanLoading] = useState(false);
  const [message, setMessage] = useState("Complete the required sections to unlock normal PizzaWave operation.");
  const [busy, setBusy] = useState("");
  const [artifactReport, setArtifactReport] = useState<SetupArtifactReport | null>(null);
  const [setupJob, setSetupJob] = useState<Job | null>(null);
  const [setupJobContext, setSetupJobContext] = useState("");
  const [setupLogs, setSetupLogs] = useState<JobLog[]>([]);
  const [sdrDetection, setSdrDetection] = useState<SetupSdrDetection | null>(null);
  const [sdrDetectionAttempted, setSdrDetectionAttempted] = useState(false);
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
  const [restorePreview, setRestorePreview] = useState<BackupRestorePreview | null>(null);
  const [migrationOptions, setMigrationOptions] = useState<MigrationResetOptions>(defaultMigrationResetOptions);
  const [migrationBackupEstimate, setMigrationBackupEstimate] = useState<BackupEstimate | null>(null);
  const [areaBoundaryCandidates, setAreaBoundaryCandidates] = useState<Record<string, SetupAreaBoundaryCandidate[]>>({});
  const [areaLookupBusy, setAreaLookupBusy] = useState("");
  const [areaAutoSeeded, setAreaAutoSeeded] = useState(false);
  const [areaLabelLookupNeeded, setAreaLabelLookupNeeded] = useState<Record<string, boolean>>({});
  const areaLookupHistory = useRef<Record<string, string>>({});
  const setupLogLastId = useRef(0);
  const setupDraftDirty = useRef(false);
  const setupJobRunning = Boolean(setupJob && ["queued", "running", "paused"].includes(setupJob.status));
  const calibrationJobRunning = setupJobRunning && setupJobContext === "calibration";
  const migrationResetApplied = Boolean(status.values?.setup?.migrationResetAtUtc);
  const detectedSdrSerials = sdrDetection?.devices.map(device => device.serial).filter(Boolean).join(",") ?? "";
  const selectedSdrDevices = sdrDetection && (!trDraftSerials.trim() || trDraftSerials.trim() === detectedSdrSerials)
    ? sdrDetection.devices
    : undefined;

  useEffect(() => {
    if (!setupDraftDirty.current)
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
  useEffect(() => {
    if (wizardStep !== "areas") {
      setAreaAutoSeeded(false);
      return;
    }
    if (areaAutoSeeded)
      return;
    const existingAreas = draft.locations?.monitoredAreas;
    if (Array.isArray(existingAreas) && existingAreas.length > 0) {
      setAreaAutoSeeded(true);
      return;
    }
    void seedAreasFromTrConfig();
  }, [wizardStep, areaAutoSeeded, draft.locations?.monitoredAreas?.length, draft.trunkRecorder?.configPath]);
  useEffect(() => {
    if (wizardStep !== "restore" && !status.values?.setup?.pendingRestorePath) return;
    void loadPendingRestore();
  }, [wizardStep, status.values?.setup?.pendingRestorePath]);
  useEffect(() => {
    writeSetupWizardDraft(status, {
      radioReferenceSid: trDraftSid.trim() || talkgroupSid.trim(),
      talkgroupSid,
      talkgroupSidManuallyEdited,
      talkgroupsCsv,
      includeExcludedTalkgroups,
      trSiteList,
      trSiteSearch,
      selectedTrSites,
      trConfigStage,
      trDraftSerials,
      trDraftRate
    });
  }, [status, trDraftSid, talkgroupSid, talkgroupSidManuallyEdited, talkgroupsCsv, includeExcludedTalkgroups, trSiteList, trSiteSearch, selectedTrSites, trConfigStage, trDraftSerials, trDraftRate]);
  useEffect(() => {
    if (wizardStep !== "talkgroups" || talkgroupSid.trim())
      return;
    const sid = String(draft.setup?.radioReferenceSid ?? trDraftSid ?? "").trim();
    if (sid)
      setTalkgroupSid(sid);
  }, [wizardStep, talkgroupSid, draft.setup?.radioReferenceSid, trDraftSid]);
  useEffect(() => {
    if (wizardStep !== "tr" || trInstallMode !== "freshTr" || !trSiteList)
      return;
    if (!sdrDetection && !sdrDetectionAttempted && busy !== "sdr-detect")
      void detectSdrs(true);
  }, [wizardStep, trInstallMode, trSiteList, sdrDetection, sdrDetectionAttempted, busy]);
  useEffect(() => {
    if (wizardStep !== "tr" || trInstallMode !== "freshTr" || !trSiteList || selectedTrSites.length === 0) {
      setTrSourcePlan(null);
      setTrSourcePlanLoading(false);
      return;
    }
    let canceled = false;
    const serials = trDraftSerials.trim() || detectedSdrSerials || "";
    setTrSourcePlanLoading(true);
    api.request<SetupTrConfigSourcePlan>("/api/v1/setup/tr-config/source-plan", {
      method: "POST",
      body: JSON.stringify({
        radioReferenceSid: trDraftSid.trim(),
        siteNameList: selectedTrSites,
        sdrSerials: serials,
        sdrDevices: selectedSdrDevices,
        sampleRate: Number(trDraftRate) || 2400000
      })
    })
      .then(plan => { if (!canceled) setTrSourcePlan(plan); })
      .catch(error => { if (!canceled) setMessage(error instanceof Error ? error.message : "Source plan preview failed."); })
      .finally(() => { if (!canceled) setTrSourcePlanLoading(false); });
    return () => { canceled = true; };
  }, [wizardStep, trInstallMode, trSiteList, selectedTrSites, trDraftSid, trDraftSerials, sdrDetection, detectedSdrSerials, selectedSdrDevices, trDraftRate]);
  useEffect(() => {
    if (wizardStep !== "migration" || !migrationOptions.createBackup || migrationResetApplied) {
      setMigrationBackupEstimate(null);
      return;
    }
    let canceled = false;
    api.request<BackupEstimate>(`/api/v1/system/backups/estimate?audioWindow=${encodeURIComponent(migrationOptions.backupAudioWindow)}`)
      .then(value => { if (!canceled) setMigrationBackupEstimate(value); })
      .catch(() => { if (!canceled) setMigrationBackupEstimate(null); });
    return () => { canceled = true; };
  }, [wizardStep, migrationOptions.createBackup, migrationOptions.backupAudioWindow, migrationResetApplied]);

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

  function updateMigrationOption<K extends keyof MigrationResetOptions>(key: K, value: MigrationResetOptions[K]) {
    setMigrationOptions(current => ({ ...current, [key]: value }));
  }

  function updateTrRadioReferenceSid(value: string) {
    setTrDraftSid(value);
    if (!talkgroupSidManuallyEdited)
      setTalkgroupSid(value);
    update(["setup", "radioReferenceSid"], value);
    setTrSiteList(null);
    setSelectedTrSites([]);
    setTrSourcePlan(null);
  }

  function updateTalkgroupRadioReferenceSid(value: string) {
    setTalkgroupSid(value);
    setTalkgroupSidManuallyEdited(true);
    update(["setup", "radioReferenceSid"], value);
  }

  async function seedAreasFromTrConfig() {
    setAreaLookupBusy("seed");
    try {
      const plan = await api.request<SetupCalibrationPlan>("/api/v1/setup/calibration/plan");
      const systems = plan.systems.map(system => system.shortName).filter(Boolean);
      if (systems.length === 0) {
        setMessage("No TR systems were found yet. Complete TR Config before setting monitored areas.");
        setAreaAutoSeeded(true);
        return;
      }
      setupDraftDirty.current = true;
      setDraft((current: any) => {
        const next = cloneSettings(current);
        const existing = Array.isArray(next.locations?.monitoredAreas) ? next.locations.monitoredAreas : [];
        if (existing.length > 0)
          return next;
        const sourceSystems = trSourcePlan?.systems ?? trDraft?.systems ?? [];
        const rows = systems.map((shortName, index) => {
          const matchingSystem = sourceSystems.find(system => system.shortName === shortName);
          const selectedSite = selectedTrSites[index] ?? trSiteList?.sites.find(site => site.shortName === shortName)?.name ?? matchingSystem?.siteName ?? "";
          const areaLabel = suggestAreaLabelFromSite(selectedSite || matchingSystem?.siteName || shortName);
          return {
            areaId: createClientId(),
            areaLabel,
            systemShortName: shortName,
            aliases: [shortName],
            north: 0,
            south: 0,
            east: 0,
            west: 0
          };
        });
        next.locations = { ...(next.locations ?? {}), monitoredAreas: rows };
        return next;
      });
      setMessage(`Created ${systems.length.toLocaleString()} monitored area row(s) from the TR config. PizzaWave will look up Census TIGERweb boundaries automatically.`);
      setAreaAutoSeeded(true);
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Could not load TR systems for monitored areas.");
      setAreaAutoSeeded(true);
    } finally {
      setAreaLookupBusy("");
    }
  }

  async function lookupAreaBoundary(index: number, key: string, automatic = false) {
    const area = draft.locations?.monitoredAreas?.[index];
    const query = String(area?.areaLabel || area?.systemShortName || "").trim();
    if (!query) {
      setMessage("Enter an area label before searching for a boundary.");
      return;
    }
    areaLookupHistory.current[key] = query;
    setAreaLookupBusy(key);
    try {
      const result = await api.request<SetupAreaBoundaryResponse>("/api/v1/setup/areas/boundaries", {
        method: "POST",
        body: JSON.stringify({ query })
      });
      setAreaLabelLookupNeeded(current => ({ ...current, [key]: false }));
      if (automatic && result.candidates.length === 1) {
        setAreaBoundaryCandidates(current => ({ ...current, [key]: [] }));
        applyAreaBoundary(index, result.candidates[0], false);
        setMessage(`${result.candidates[0].label} boundary applied from Census TIGERweb.`);
        return;
      }
      setAreaBoundaryCandidates(current => ({ ...current, [key]: result.candidates }));
      setMessage(result.candidates.length
        ? `${result.candidates.length.toLocaleString()} boundary candidate(s) found. Select one to fill the monitored area bounds.`
        : result.diagnostics || "No boundary candidates found.");
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Boundary lookup failed.");
    } finally {
      setAreaLookupBusy("");
    }
  }

  function applyAreaBoundary(index: number, candidate: SetupAreaBoundaryCandidate, showMessage = true) {
    setupDraftDirty.current = true;
    setDraft((current: any) => {
      const next = cloneSettings(current);
      const areas = Array.isArray(next.locations?.monitoredAreas) ? next.locations.monitoredAreas : [];
      if (!areas[index])
        return next;
      const systemShortName = areas[index].systemShortName ?? "";
      areas[index] = {
        ...areas[index],
        areaLabel: candidate.label,
        aliases: systemShortName ? Array.from(new Set([...(areas[index].aliases ?? []), systemShortName])) : areas[index].aliases,
        north: candidate.north,
        south: candidate.south,
        east: candidate.east,
        west: candidate.west
      };
      next.locations = { ...(next.locations ?? {}), monitoredAreas: areas };
      return next;
    });
    if (showMessage)
      setMessage(`${candidate.label} applied to monitored area bounds.`);
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
    normalizeSetupLocationNumbers(values);
    const currentWizardStep = effectiveSetupStepId();
    values.setup = { ...(values.setup ?? {}), currentStep: currentWizardStep, installMode: trInstallMode, trConfigMode: trInstallMode === "freshTr" ? "radioReference" : trConfigMode, radioReferenceSid: trDraftSid.trim() || talkgroupSid.trim() || values.setup?.radioReferenceSid || "" };
    if (trConfigJson.trim()) values.trConfigJson = trConfigJson;
    if (talkgroupsCsv.trim()) values.talkgroupsCsv = talkgroupsCsv;
    const saved = await api.request<SetupStatus>("/api/v1/setup/save", { method: "POST", body: JSON.stringify({ values }) });
    setupDraftDirty.current = false;
    setDraft(setupDraftFromStatus(saved));
    return saved;
  }

  function effectiveSetupStepId(id = wizardStep) {
    return status.values?.setup?.restoreAppliedAtUtc && id === "stack" ? "tr" : id;
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

  async function loadPendingRestore() {
    try {
      setRestorePreview(await api.request<BackupRestorePreview>("/api/v1/setup/restore"));
    } catch {
      setRestorePreview(null);
    }
  }

  async function applyPendingRestore() {
    if (!restorePreview)
      throw new Error("No staged restore is available.");
    if (!restorePreview.checks.every(check => check.ok))
      throw new Error("Restore archive verification failed. Do not apply this backup.");
    if (!confirmAction("Apply staged restore?", "This will overwrite PizzaWave/TR files from the backup and restart services. PizzaWave will reconnect, run the required restore checks automatically, and only keep the wizard open if something needs attention.")) return;
    setBusy("apply-restore");
    try {
      const result = await api.request<BackupRestoreApplyResult>("/api/v1/setup/restore/apply", { method: "POST" });
      setMessage(result.message || "Restore scheduled. Waiting for pizzad to restart...");
      await waitForHealth();
      setMessage("Restore applied. Running required checks...");
      const validation = await api.request<SetupValidationResult>("/api/v1/setup/validate-required", { method: "POST" });
      if (validation.ok) {
        await api.request<SetupStatus>("/api/v1/setup/complete", { method: "POST" });
        setMessage("Restore checks passed. Loading PizzaWave...");
        await reload();
        return;
      }
      setMessage(`Restore applied, but setup needs attention: ${validation.message}`);
      await reload();
    } finally {
      setBusy("");
    }
  }

  async function cancelPendingRestore() {
    if (!confirmAction("Cancel staged restore?", "This clears the staged restore and deletes its temporary files. No live PizzaWave, TR, database, or audio files will be changed.")) return;
    setBusy("cancel-restore");
    try {
      const result = await api.request<BackupRestoreCancelResult>("/api/v1/setup/restore/cancel", { method: "POST" });
      setRestorePreview(null);
      setMessage(result.message || "Restore canceled.");
      await reload();
    } finally {
      setBusy("");
    }
  }

  async function resetForNewSite() {
    if (migrationResetApplied) {
      setMessage("Migration reset has already been applied. Restore from a backup or continue setup for the new site.");
      return false;
    }
    const preserved = [
      migrationOptions.preserveBranding ? "branding" : "",
      migrationOptions.preserveTranscription ? "transcription" : "",
      migrationOptions.preserveAiInsights ? "AI Insights" : "",
      migrationOptions.preserveEmbeddings ? "embedding service settings" : "",
      migrationOptions.preserveAlerts ? "alert delivery/playback" : "",
      migrationOptions.preserveRfSurvey ? "RF survey tooling" : ""
    ].filter(Boolean);
    const backupText = migrationOptions.createBackup
      ? `A full backup with all recorded audio will be created first${migrationBackupEstimate ? `; estimated source size is ${formatBytes(migrationBackupEstimate.bytes)} across ${migrationBackupEstimate.fileCount.toLocaleString()} file(s)` : ""}. If backup creation fails, reset will stop.`
      : "No backup will be created before reset.";
    const preserveText = preserved.length
      ? `The reset will carry forward only: ${preserved.join(", ")}.`
      : "The reset will not carry forward optional service settings.";
    if (!confirmAction("Reset this rig for a new site?", `${backupText} ${preserveText} Calls, recorded audio, incidents, old TR config/talkgroups, monitored areas, jobs, metrics, recommendation baselines, alert rules, and Qdrant call vectors are always cleared and never migrated into the new site.`)) return false;
    setBusy("migration-reset");
    try {
      const result = await api.request<MigrationResetResult>("/api/v1/setup/migration/reset-new-site", { method: "POST", body: JSON.stringify(migrationOptions) });
      const backupMessage = result.backup ? ` Backup: ${result.backup.name} (${formatBytes(result.backup.bytes)}).` : "";
      setMessage(`${result.message}${backupMessage}${result.warnings.length ? " Warnings: " + result.warnings.join(" ") : ""}`);
      await reload();
      return true;
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Migration reset failed.");
      return false;
    } finally {
      setBusy("");
    }
  }

  async function cancelMigration() {
    if (migrationResetApplied) {
      setMessage("Migration reset has already been applied and cannot be canceled. Restore from a backup or continue setup for the new site.");
      return;
    }
    if (!confirmAction("Cancel migration mode?", "This exits migration mode and returns to the previous PizzaWave state. No site data has been cleared.")) return;
    setBusy("migration-cancel");
    try {
      const result = await api.request<MigrationActionResult>("/api/v1/setup/migration/cancel", { method: "POST" });
      setMessage(result.message);
      await reload();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Cancel migration failed.");
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
      setMessage("Setup complete. Opening Radio Setup...");
      await reload();
      onComplete?.();
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

  async function detectSdrs(quiet = false) {
    setBusy("sdr-detect");
    setSdrDetectionAttempted(true);
    try {
      const result = await api.request<SetupSdrDetection>("/api/v1/setup/sdrs");
      setSdrDetection(result);
      if (result.devices.some(device => device.serial)) {
        setTrDraftSerials(result.devices.map(device => device.serial).filter(Boolean).join(","));
      }
      const detectedRates = result.devices.map(device => device.defaultSampleRate).filter(rate => rate > 0);
      if (detectedRates.length > 0 && new Set(detectedRates).size === 1)
        setTrDraftRate(String(detectedRates[0]));
      if (!quiet)
        setMessage(result.message);
    } catch (error) {
      if (!quiet)
        setMessage(error instanceof Error ? error.message : "SDR detection failed.");
    } finally {
      setBusy("");
    }
  }

  async function fetchRadioReferenceSites() {
    if (!trDraftSid.trim()) {
      setMessage("Enter a RadioReference SID first.");
      return;
    }
    setBusy("tr-sites-fetch");
    try {
      const result = await api.request<SetupTrConfigSites>("/api/v1/setup/tr-config/sites", {
        method: "POST",
        body: JSON.stringify({ radioReferenceSid: trDraftSid.trim() })
      });
      setTrSiteList(result);
      setSelectedTrSites([]);
      setTrSiteSearch("");
      setTrConfigStage("sites");
      setSdrDetection(null);
      setSdrDetectionAttempted(false);
      setTrSourcePlan(null);
      setMessage(result.diagnostics);
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "RadioReference site fetch failed.");
    } finally {
      setBusy("");
    }
  }

  function toggleTrSite(siteName: string, checked: boolean) {
    setSelectedTrSites(current => checked
      ? [...current, siteName].filter((value, index, values) => values.indexOf(value) === index)
      : current.filter(value => value !== siteName));
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
          const gain = source.gain && source.gain !== "0" ? source.gain : "32";
          next[key] = next[key] ?? { gain, errorHz: source.errorHz ? String(source.errorHz) : "", ppm: "" };
        }
        return next;
      });
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Calibration plan could not be loaded.");
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

  async function importTalkgroups(source: "csv" | "rr", csvText = talkgroupsCsv) {
    if (source === "rr" && !talkgroupSid.trim()) {
      setMessage("Enter a RadioReference SID first.");
      return;
    }
    if (source === "csv" && !csvText.trim()) {
      setMessage("Choose a talkgroup CSV file first.");
      return;
    }
    setBusy(`talkgroups-${source}`);
    try {
      const preview = await api.request<SetupTalkgroupPreview>("/api/v1/setup/talkgroups/preview", {
        method: "POST",
        body: JSON.stringify(source === "rr"
          ? { radioReferenceSid: talkgroupSid.trim(), includeNormallyExcluded: includeExcludedTalkgroups }
          : { csvText, includeNormallyExcluded: includeExcludedTalkgroups })
      });
      const saved = await api.request<SetupTalkgroupPreview>("/api/v1/setup/talkgroups/save", { method: "POST", body: JSON.stringify({ rows: preview.rows }) });
      setTalkgroupPreview(saved);
      if (source === "rr") {
        update(["setup", "radioReferenceSid"], talkgroupSid.trim());
        if (!trDraftSid.trim()) {
          setTrDraftSid(talkgroupSid.trim());
          setTalkgroupSidManuallyEdited(false);
        }
      }
      setMessage(`${source === "rr" ? "Fetched" : "Imported"} ${saved.includedCount.toLocaleString()} talkgroup row(s). ${saved.diagnostics}`);
      await validateSetupSection("talkgroups");
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Talkgroup import failed.");
    } finally {
      setBusy("");
    }
  }

  async function importTalkgroupCsvFile(file: File | null) {
    if (!file)
      return;
    setBusy("talkgroups-csv");
    try {
      const text = await file.text();
      setTalkgroupsCsv(text);
      await importTalkgroups("csv", text);
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Talkgroup CSV import failed.");
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
          sdrDevices: selectedSdrDevices,
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

  async function draftAndSaveSelectedTrConfigInline() {
    if (!trDraftSid.trim())
      throw new Error("Enter a RadioReference SID before continuing.");
    if (selectedTrSites.length === 0)
      throw new Error("Select at least one RadioReference site before continuing.");
    const blockers = trSourcePlanBlockingWarnings(trSourcePlan);
    if (blockers.length > 0)
      throw new Error(blockers.join(" "));
    const serials = trDraftSerials.trim() || detectedSdrSerials || "";
    const draftResult = await api.request<SetupTrConfigDraft>("/api/v1/setup/tr-config/draft", {
      method: "POST",
      body: JSON.stringify({
        radioReferenceSid: trDraftSid.trim(),
        siteNameList: selectedTrSites,
        sdrSerials: serials,
        sdrDevices: selectedSdrDevices,
        sampleRate: Number(trDraftRate) || 2400000
      })
    });
    setTrDraft(draftResult);
    setTrConfigJson(draftResult.configJson);
    const save = await api.request<SetupValidationResult>("/api/v1/setup/tr-config/save", {
      method: "POST",
      body: JSON.stringify({ configJson: draftResult.configJson })
    });
    setMessage(`${draftResult.diagnostics} ${save.message}`);
    return draftResult;
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
      values.setup = { ...(values.setup ?? {}), currentStep: effectiveSetupStepId(), installMode: trInstallMode, trConfigMode: trInstallMode === "freshTr" ? "radioReference" : trConfigMode, radioReferenceSid: trDraftSid.trim() || talkgroupSid.trim() || values.setup?.radioReferenceSid || "" };
      await api.request<SetupStatus>("/api/v1/setup/save", { method: "POST", body: JSON.stringify({ values }) });
    }
    return selectedModel;
  }

  function skipOptional(section: "ai-insights" | "embeddings" | "alerts" | "calibration") {
    if (section === "ai-insights") update(["aiInsights", "enabled"], false);
    if (section === "embeddings") update(["embeddings", "enabled"], false);
    if (section === "alerts") update(["alerts", "emailEnabled"], false);
    if (section === "calibration") update(["setup", "calibrationSkippedOrCompleted"], true);
    setMessage(`${label(section)} skipped. Save progress to persist this choice.`);
  }

  const checks = status.checks ?? [];
  const requiredOpen = checks.filter(c => c.required && !c.ok);
  const tr = draft.trunkRecorder ?? {};
  const ingest = draft.ingest ?? {};
  const transcription = draft.transcription ?? {};
  const ai = draft.aiInsights ?? {};
  const embeddings = draft.embeddings ?? {};
  const alerts = draft.alerts ?? {};
  const branding = draft.branding ?? {};
  const installOptionalDiagnosticTools = Boolean(draft.setup?.installOptionalDiagnosticTools);
  const locations = draft.locations?.monitoredAreas ?? [];
  const postRestoreValidation = Boolean(status.values?.setup?.restoreAppliedAtUtc);
  const migrationMode = status.currentStep === "migration" || Boolean(status.values?.setup?.migrationMode);
  const lmStudioDetection = (status.detection as any)?.lmStudio;
  const qdrantDetection = (status.detection as any)?.qdrant;
  const lmStudioInstalled = Boolean(lmStudioDetection?.found || lmStudioDetection?.serviceEnabled || lmStudioDetection?.binaryPath);
  const qdrantInstalled = Boolean(qdrantDetection?.found || qdrantDetection?.serviceEnabled || qdrantDetection?.binaryPath || qdrantDetection?.storageExists);
  const visibleTrSites = (trSiteList?.sites ?? []).filter(site => {
    const query = trSiteSearch.trim().toLowerCase();
    if (!query) return true;
    return `${site.name} ${site.shortName} ${site.controlChannelsMhz.join(" ")}`.toLowerCase().includes(query);
  });
  const areaLookupSignature = locations
    .map((area: any, index: number) => `${areaDraftKey(area, index)}:${area.areaLabel ?? ""}:${area.north ?? ""}:${area.south ?? ""}:${area.east ?? ""}:${area.west ?? ""}`)
    .join("|");
  useEffect(() => {
    if (wizardStep !== "areas" || areaLookupBusy || locations.length === 0)
      return;
    const pending = locations
      .map((area: any, index: number) => ({ area, index, key: areaDraftKey(area, index), query: String(area.areaLabel || area.systemShortName || "").trim() }))
      .find((item: { area: any; index: number; key: string; query: string }) =>
        item.query &&
        (areaBoundaryCandidates[item.key]?.length ?? 0) === 0 &&
        areaLookupHistory.current[item.key] !== item.query &&
        (!hasUsableAreaBounds(item.area) || areaLabelLookupNeeded[item.key]));
    if (!pending)
      return;
    const timer = window.setTimeout(() => void lookupAreaBoundary(pending.index, pending.key, true), 500);
    return () => window.clearTimeout(timer);
  }, [wizardStep, areaLookupBusy, areaLookupSignature, areaBoundaryCandidates, areaLabelLookupNeeded]);
  useEffect(() => {
    if (!postRestoreValidation || wizardStep !== "stack") return;
    setWizardStep("tr");
    setExpandedSetupStep("tr");
  }, [postRestoreValidation, wizardStep]);
  const checkStepMap: Record<string, string> = {
    tr: "tr",
    callstream: "tr",
    health: "tr",
    talkgroups: "talkgroups",
    transcription: "transcription",
    locations: "areas",
    "ai-insights": "ai",
    embeddings: "embeddings",
    alerts: "alerts",
    "diagnostic-tools": "radio",
    calibration: "radio"
  };
  const baseSetupSteps = [
    ...(status.currentStep === "restore" || status.values?.setup?.pendingRestorePath ? [{ id: "restore", title: "Restore" }] : []),
    ...(migrationMode ? [{ id: "migration", title: "Migration" }] : []),
    ...(!postRestoreValidation && !migrationMode ? [{ id: "stack", title: "Stack" }] : []),
    { id: "tr", title: "TR Config" },
    { id: "talkgroups", title: "Talkgroups" },
    { id: "transcription", title: "Transcription" },
    { id: "areas", title: "Areas" },
    { id: "ai", title: "AI Insights" },
    { id: "embeddings", title: "Vector DB" },
    { id: "alerts", title: "Alerts" },
    { id: "radio", title: "Radio Setup" },
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
    if (step.id === "restore") {
      ok = restorePreview?.checks.every(check => check.ok) ?? false;
      blocked = !ok;
    }
    if (step.id === "migration") {
      ok = migrationResetApplied;
      blocked = !ok;
    }
    if (step.id === "radio") {
      ok = ok && !calibrationJobRunning;
    }
    if (step.id === "finish") {
      ok = requiredOpen.length === 0 && restartVerified && !setupJobRunning;
      blocked = requiredOpen.length > 0 || setupJobRunning;
    }
    return { ...step, checks: stepChecks, ok, blocked };
  });
  const effectiveWizardStep = migrationMode && !setupSteps.some(step => step.id === wizardStep)
    ? "migration"
    : postRestoreValidation && wizardStep === "stack"
      ? "tr"
      : wizardStep;
  const stepIndex = Math.max(0, setupSteps.findIndex(step => step.id === effectiveWizardStep));
  const currentStep = setupSteps[stepIndex] ?? setupSteps[0];

  function goStep(id: string) {
    if (setupJobRunning) {
      setMessage("A setup job is running. Stop or wait for it before changing steps.");
      return;
    }
    if (id === "tr" && trInstallMode === "freshTr")
      setTrConfigStage("sites");
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
    if (currentStep.id === "migration" && !migrationResetApplied) {
      if (await resetForNewSite())
        goStep(nextId);
      return;
    }
    if (currentStep.id === "tr" && trInstallMode === "freshTr" && trConfigStage === "sites") {
      if (!trSiteList) {
        setMessage("Fetch RadioReference sites before continuing.");
        return;
      }
      if (selectedTrSites.length === 0) {
        setMessage("Select at least one RadioReference site before continuing.");
        return;
      }
      if (trSourcePlanLoading) {
        setMessage("Wait for the source plan preview to finish, then continue.");
        return;
      }
      if (!trSourcePlan) {
        setMessage("Source plan preview is not ready yet. Check the selected sites and SDR detection, then try again.");
        return;
      }
      const blockers = trSourcePlanBlockingWarnings(trSourcePlan);
      if (blockers.length > 0) {
        setMessage(blockers.join(" "));
        return;
      }
      setTrConfigStage("sdr");
      setSdrDetectionAttempted(false);
      setMessage(trSourcePlan.warnings.length > 0 ? trSourcePlan.warnings.join(" ") : "Review detected SDRs and sample rate, then click Next to create the TR config.");
      return;
    }
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
    if (currentStep.id === "tr" && trInstallMode === "freshTr" && trConfigStage === "sdr") {
      setTrConfigStage("sites");
      return;
    }
    goStep(setupSteps[Math.max(0, stepIndex - 1)].id);
  }

  async function performCurrentStepWork() {
    if (currentStep.id === "migration") {
      if (!migrationResetApplied)
        throw new Error("Confirm the migration reset before continuing, or cancel migration mode.");
      return;
    }

    if (currentStep.id === "restore") {
      await applyPendingRestore();
      return;
    }

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
      if (trInstallMode === "freshTr")
        await draftAndSaveSelectedTrConfigInline();
      await validateSetupSection("tr");
      await patchCallstreamInline();
      await validateSetupSection("health");
      return;
    }

    if (currentStep.id === "talkgroups") {
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

    if (currentStep.id === "embeddings") {
      await validateSetupSection("embeddings");
      return;
    }

    if (currentStep.id === "alerts") {
      await validateSetupSection("alerts");
      return;
    }

    if (currentStep.id === "radio") {
      const values: any = cloneSettings(draft);
      normalizeSetupLocationNumbers(values);
      values.setup = {
        ...(values.setup ?? {}),
        currentStep: effectiveSetupStepId(),
        installMode: trInstallMode,
        trConfigMode: trInstallMode === "freshTr" ? "radioReference" : trConfigMode,
        radioReferenceSid: trDraftSid.trim() || talkgroupSid.trim() || values.setup?.radioReferenceSid || "",
        diagnosticToolsSkippedOrInstalled: true,
        calibrationSkippedOrCompleted: true,
        radioSetupHandoff: true
      };
      if (installOptionalDiagnosticTools) {
        await runSetupJobToCompletion("diagnostic-tools-prime", true, "radio");
      }
      const saved = await api.request<SetupStatus>("/api/v1/setup/save", { method: "POST", body: JSON.stringify({ values }) });
      setupDraftDirty.current = false;
      setDraft(setupDraftFromStatus(saved));
    }
  }

  return <div className="setup-page">
    <div className="setup-hero">
      <div>
        <h1>PizzaWave Setup</h1>
        <p>First-run setup walks through one decision at a time. Progress saves automatically as you move through the wizard.</p>
      </div>
      {migrationMode && !migrationResetApplied && <button className="danger-button setup-cancel-migration" disabled={Boolean(busy) || setupJobRunning} onClick={() => void cancelMigration()}>
        {busy === "migration-cancel" ? "Canceling..." : "Cancel Migration"}
      </button>}
    </div>
    <div className="setup-message">{message}</div>
    <div className="setup-wizard-layout">
      <div className="card setup-checklist">
        <h3>Completion Gate</h3>
        {postRestoreValidation && <div className="setup-note">Restore applied. Re-check the restored services and settings, then finish setup to resume normal operation.</div>}
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
            <small>Detection checks the configured TR config path and systemd service state; RF decoding is validated later.</small>
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

        {currentStep.id === "migration" && <SetupSection title="Migration" description="Choose what to back up and which reusable settings to seed into the new site. Site data is never migrated.">
          {!migrationResetApplied && <>
            <SettingCheckbox
              label="Backup current system"
              description={`Creates a full restore archive before reset, including all recorded audio.${migrationBackupEstimate ? ` Estimated source size: ${formatBytes(migrationBackupEstimate.bytes)} across ${migrationBackupEstimate.fileCount.toLocaleString()} file(s).` : ""}`}
              checked={migrationOptions.createBackup}
              onChange={value => updateMigrationOption("createBackup", value)}
            />
            <div className="setup-note">Carry forward only the settings checked below. Unchecked sections reset to PizzaWave defaults.</div>
            <SettingCheckbox label="Branding" description="Copy the current PizzaWave display name." checked={migrationOptions.preserveBranding} onChange={value => updateMigrationOption("preserveBranding", value)} />
            <SettingCheckbox label="Transcription" description="Copy provider, model, worker, and endpoint settings. Deferred talkgroups are cleared." checked={migrationOptions.preserveTranscription} onChange={value => updateMigrationOption("preserveTranscription", value)} />
            <SettingCheckbox label="AI Insights" description="Copy chat endpoint, model, limits, and enablement." checked={migrationOptions.preserveAiInsights} onChange={value => updateMigrationOption("preserveAiInsights", value)} />
            <SettingCheckbox label="Vector DB service" description="Copy embedding endpoint and Qdrant service settings. Existing vectors are deleted." checked={migrationOptions.preserveEmbeddings} onChange={value => updateMigrationOption("preserveEmbeddings", value)} />
            <SettingCheckbox label="Alert delivery" description="Copy email and playback settings. Alert rules are cleared." checked={migrationOptions.preserveAlerts} onChange={value => updateMigrationOption("preserveAlerts", value)} />
            <SettingCheckbox label="RF survey tooling" description="Copy probe command and timing settings. Survey sessions and baselines are cleared." checked={migrationOptions.preserveRfSurvey} onChange={value => updateMigrationOption("preserveRfSurvey", value)} />
            <div className="maintenance-help">
              <p><strong>Always cleared</strong>: calls, recorded audio, incidents, old TR config, talkgroups, monitored areas, jobs, metrics, recommendations, alert rules, and Qdrant call vectors.</p>
            </div>
            <div className="setup-note">Click Reset &amp; Continue to confirm the reset and move to TR Config. Cancel Migration is available until reset runs.</div>
          </>}
          {migrationResetApplied && <>
            <div className="maintenance-help">
              <p><strong>Reset applied</strong>: site data has already been cleared. Migration cannot be canceled after this point.</p>
              <p><strong>Next</strong>: continue to TR Config, or restore from a backup if this reset should be undone.</p>
            </div>
            <div className="setup-note">Click Next to configure the new TR site, frequencies, and talkgroups.</div>
          </>}
        </SetupSection>}

        {currentStep.id === "restore" && <SetupSection title="Restore Backup" description="Review the staged backup before applying it. Applying restore overwrites backed-up PizzaWave/TR files and restarts services, then setup checks must be run again.">
          {!restorePreview ? <div className="setup-note">Loading staged restore...</div> : <BackupRestorePreviewCard preview={restorePreview} />}
          {restorePreview && <div className="maintenance-help">
            <p><strong>Included data</strong> covers the PizzaWave SQLite database, recorded audio, app data, Qdrant storage, TR config/talkgroups, PizzaWave config, and token when those files existed at backup time.</p>
            <p><strong>After restore</strong> the wizard remains active so TR, talkgroups, callstream, transcription, monitored areas, Qdrant, and alerts can be sanity-checked before normal ingest resumes.</p>
          </div>}
          <div className="setup-button-row">
            <button className="danger-button" disabled={busy === "apply-restore" || !restorePreview || restorePreview.checks.some(check => !check.ok)} onClick={() => void applyPendingRestore()}>{busy === "apply-restore" ? "Applying..." : "Apply Restore"}</button>
            <button disabled={busy === "cancel-restore" || !restorePreview} onClick={() => void cancelPendingRestore()}>{busy === "cancel-restore" ? "Canceling..." : "Cancel Restore"}</button>
          </div>
        </SetupSection>}

        {currentStep.id === "tr" && <SetupSection title="TR Config" description={trInstallMode === "reuseExistingTr" ? "Using the existing TR config." : trConfigStage === "sites" ? "Fetch RadioReference sites, then select one or more sites to monitor." : "Review detected SDRs and sample rate before PizzaWave creates the TR config."}>
          <div className="settings-subsection">
            <h4>Callstream</h4>
            <SettingInput label="Listener bind" description="PizzaWave callstream listener address. Use 127.0.0.1 when trunk-recorder runs on this Pi." value={ingest.callstreamBind ?? "127.0.0.1"} onChange={v => update(["ingest", "callstreamBind"], v)} />
            <SettingInput label="Listener port" description="PizzaWave callstream TCP port. The generated TR config targets this value." type="number" value={ingest.callstreamPort ?? 9123} onChange={v => update(["ingest", "callstreamPort"], numberOrZero(v))} />
          </div>
          {trInstallMode === "reuseExistingTr" && <>
            <SettingInput label="TR config path" description="Existing trunk-recorder config.json." value={tr.configPath} onChange={v => update(["trunkRecorder", "configPath"], v)} />
            <SettingInput label="TR log service" description="Systemd service name used for health collection." value={tr.logServiceName} onChange={v => update(["trunkRecorder", "logServiceName"], v)} />
            <div className="setup-note">Click Next to validate the TR config, patch callstream, and verify health access.</div>
          </>}
          {trInstallMode === "freshTr" && trConfigStage === "sites" && <>
            <div className="setup-fetch-row">
              <SettingInput label="RadioReference SID" description="Enter the numeric RadioReference system ID." value={trDraftSid} onChange={updateTrRadioReferenceSid} inputMode="numeric" />
              <button className="danger-button" disabled={Boolean(busy) || !trDraftSid.trim()} onClick={() => void fetchRadioReferenceSites()}>{busy === "tr-sites-fetch" ? "Fetching..." : "Fetch"}</button>
            </div>
            {trSiteList && <>
              <SettingInput label="Search sites" description={`${trSiteList.sites.length.toLocaleString()} site(s) found. Select at least one.`} value={trSiteSearch} onChange={setTrSiteSearch} />
              <div className="rr-site-list">
                {visibleTrSites.map(site => <label className="rr-site-row" key={site.name}>
                  <input type="checkbox" checked={selectedTrSites.includes(site.name)} onChange={event => toggleTrSite(site.name, event.currentTarget.checked)} />
                  <span>
                    <strong>{site.name}</strong>
                    <small>{site.frequencyCount} frequencies / {site.controlChannelCount} control channels{site.controlChannelsMhz.length ? ` / CC ${site.controlChannelsMhz.map(formatMhz).join(", ")}` : ""}</small>
                  </span>
                </label>)}
                {visibleTrSites.length === 0 && <div className="setup-note">No sites match the search.</div>}
              </div>
              <div className="setup-note">{selectedTrSites.length.toLocaleString()} selected. {sdrDetection ? `${sdrDetection.devices.length.toLocaleString()} SDR device(s) detected.` : sdrDetectionAttempted ? "SDR detection did not return devices yet." : "Detecting SDRs..."}</div>
              {trSourcePlanLoading && <div className="setup-note">Planning selected sites against detected SDRs...</div>}
              {trSourcePlan && <TrSourcePlanPreview plan={trSourcePlan} />}
            </>}
          </>}
          {trInstallMode === "freshTr" && trConfigStage === "sdr" && <>
            <div className="setup-button-row">
              <button type="button" onClick={() => setTrConfigStage("sites")}>Edit Sites</button>
            </div>
            <div className="setup-review">
              <h4>Selected Sites</h4>
              {selectedTrSites.map(site => <div key={site}><span>Site</span><code>{site}</code></div>)}
            </div>
            {trSourcePlan && <TrSourcePlanPreview plan={trSourcePlan} />}
            {busy === "sdr-detect" && <div className="setup-note">Detecting SDRs...</div>}
            {sdrDetection && <SdrDetectionPanel detection={sdrDetection} />}
            {!sdrDetection && sdrDetectionAttempted && <div className="setup-note">SDR detection did not complete. Click Back and return to this screen to retry, or continue if the device path can be inferred by index.</div>}
            <SettingInput label="Sample rate" description="Default 2400000. Increase only if the selected site needs a wider tuning window." value={trDraftRate} onChange={setTrDraftRate} inputMode="numeric" />
            <div className="setup-note">Click Next to create and save the TR config, patch callstream, remove captureDir, and verify TR health.</div>
          </>}
        </SetupSection>}

        {currentStep.id === "talkgroups" && <SetupSection title="Talkgroups" description="Import talkgroups from RadioReference or an existing CSV. PizzaWave writes the catalog and regenerates the trunk-recorder CSV.">
          <SettingInput label="Output CSV path" description="trunk-recorder talkgroups.csv generated by PizzaWave." value={tr.talkgroupsPath} onChange={v => update(["trunkRecorder", "talkgroupsPath"], v)} />
          <div className="setup-fetch-row">
            <SettingInput label="RadioReference SID" description="Pre-filled from TR Config when available." value={talkgroupSid} onChange={updateTalkgroupRadioReferenceSid} inputMode="numeric" />
            <button className="danger-button" disabled={Boolean(busy) || !talkgroupSid.trim()} onClick={() => void importTalkgroups("rr")}>{busy === "talkgroups-rr" ? "Fetching..." : "Fetch"}</button>
          </div>
          <SettingCheckbox label="Include normally excluded rows" description="Include encrypted, deprecated, unknown, and other rows that PizzaWave normally skips during import." checked={includeExcludedTalkgroups} onChange={setIncludeExcludedTalkgroups} />
          <label className="setting-field">
            <span>Import existing CSV<small>Load a local RadioReference/PizzaWave talkgroup CSV and generate the configured trunk-recorder CSV.</small></span>
            <input type="file" accept=".csv,text/csv,text/plain" disabled={Boolean(busy)} onChange={event => void importTalkgroupCsvFile(event.currentTarget.files?.[0] ?? null)} />
          </label>
          <div className="setup-note">{talkgroupPreview ? `${talkgroupPreview.includedCount.toLocaleString()} talkgroup row(s) imported. Click Next to continue.` : "Fetch from RadioReference or import a CSV before continuing."}</div>
        </SetupSection>}

        {currentStep.id === "transcription" && <SetupSection title="Transcription" description="Required. Choose a transcription engine and run an actual sample transcription test.">
          <SettingSelect label="Engine" description="Required provider for turning calls into text." value={transcription.provider} options={["whisper", "faster-whisper", "remote-faster-whisper", "lmstudio", "openai"]} onChange={v => update(["transcription", "provider"], v)} />
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
          {(transcription.provider === "lmstudio" || transcription.provider === "openai" || transcription.provider === "remote-faster-whisper") && <>
            <SettingInput label="Base URL" description="OpenAI-compatible audio transcription endpoint." value={transcription.openAiBaseUrl} onChange={v => update(["transcription", "openAiBaseUrl"], v)} />
            <SettingInput label="Model" description="Audio transcription model name." value={transcription.openAiModel} onChange={v => update(["transcription", "openAiModel"], v)} />
            <SettingInput label="API key" description="Optional bearer token." type="password" value={transcription.openAiApiKey} onChange={v => update(["transcription", "openAiApiKey"], v)} />
          </>}
          <div className="setup-note">Click Next to test the selected transcription provider. Fresh installs with no calls yet will validate the provider/model and skip the sample call.</div>
        </SetupSection>}

        {currentStep.id === "areas" && <SetupSection title="Monitored Areas" description="Required for geocoding/map context. Each TR system shortName needs one monitored area.">
          {areaLookupBusy === "seed" && <div className="setup-note">Reading TR systems and creating monitored area rows...</div>}
          {locations.length === 0 && areaLookupBusy !== "seed" && <div className="setup-note">Complete TR Config first, then PizzaWave can create one monitored area row per TR system.</div>}
          {locations.map((area: any, i: number) => {
            const key = areaDraftKey(area, i);
            const candidates = areaBoundaryCandidates[key] ?? [];
            return <div className="setup-area" key={key}>
              <div className="setup-area-head">
                <div>
                  <span>System</span>
                  <code>{area.systemShortName || "--"}</code>
                </div>
                {areaLookupBusy === key && <span className="setup-area-status">Finding boundary...</span>}
                {hasUsableAreaBounds(area) && areaLookupBusy !== key && <span className="setup-area-status ok">Boundary set</span>}
              </div>
              <div className="area-label-row">
                <SettingInput label="Area label" description="County or city label used to search Census TIGERweb boundaries." value={area.areaLabel} onChange={v => {
                  update(["locations", "monitoredAreas", String(i), "areaLabel"], v);
                  delete areaLookupHistory.current[key];
                  setAreaLabelLookupNeeded(current => ({ ...current, [key]: true }));
                  setAreaBoundaryCandidates(current => ({ ...current, [key]: [] }));
                }} />
                <button
                  type="button"
                  className="danger-button"
                  disabled={Boolean(busy) || Boolean(areaLookupBusy) || !String(area.areaLabel || area.systemShortName || "").trim()}
                  onClick={() => void lookupAreaBoundary(i, key, true)}
                >
                  {areaLookupBusy === key ? "Regenerating..." : "Regenerate"}
                </button>
              </div>
              {candidates.length > 0 && <div className="area-boundary-candidates">
                {candidates.map(candidate => <button type="button" key={`${candidate.kind}-${candidate.geoId}`} onClick={() => applyAreaBoundary(i, candidate)}>
                  <strong>{candidate.label}</strong>
                  <small>{candidate.kind} / {candidate.source} / N {candidate.north.toFixed(4)}, S {candidate.south.toFixed(4)}, E {candidate.east.toFixed(4)}, W {candidate.west.toFixed(4)}</small>
                </button>)}
              </div>}
              {hasUsableAreaBounds(area) ? <AreaMapPreview area={area} /> : <div className="setup-note">Boundary lookup is pending for this area.</div>}
              <div className="area-coordinate-grid">
                <SettingInput label="North" description="Northern latitude boundary." value={area.north ?? ""} onChange={v => update(["locations", "monitoredAreas", String(i), "north"], v)} />
                <SettingInput label="South" description="Southern latitude boundary." value={area.south ?? ""} onChange={v => update(["locations", "monitoredAreas", String(i), "south"], v)} />
                <SettingInput label="East" description="Eastern longitude boundary." value={area.east ?? ""} onChange={v => update(["locations", "monitoredAreas", String(i), "east"], v)} />
                <SettingInput label="West" description="Western longitude boundary." value={area.west ?? ""} onChange={v => update(["locations", "monitoredAreas", String(i), "west"], v)} />
              </div>
            </div>;
          })}
          <div className="setup-note">PizzaWave fills exact Census TIGERweb matches automatically. If more than one boundary matches, select the intended candidate before clicking Next.</div>
        </SetupSection>}

        {currentStep.id === "ai" && <SetupSection title="AI Insights / LM Link" description="Optional. Required for summaries, incidents, evidence verification, and LLM troubleshooting suggestions. Use Remote/LM Link when the chat model should run on another host such as Paxan.">
          {lmStudioInstalled ? <div className="setup-detection-card">
            <strong>{lmStudioDetection?.apiReachable ? "LM Studio API is reachable" : lmStudioDetection?.serviceActive ? "LM Studio service is running" : "LM Studio is installed"}</strong>
            <small>Detection looks for the LM Studio CLI/service and probes the configured OpenAI-compatible API URL when available.</small>
            <small>{lmStudioDetection?.serviceEnabled ? `${lmStudioDetection.service} is enabled for autostart.` : "LM Studio autostart service is not enabled."}</small>
            <small>{lmStudioDetection?.binaryPath ? `CLI: ${lmStudioDetection.binaryPath}` : "LM Studio CLI path was not detected."}</small>
            {lmStudioDetection?.apiBaseUrl && <small>{lmStudioDetection.apiReachable ? `API reachable at ${lmStudioDetection.apiBaseUrl}.` : `API not reachable at ${lmStudioDetection.apiBaseUrl}; Next will show the validation error.`}</small>}
          </div> : <>
            <div className="setup-note">The deb includes the LM Studio setup script but does not install/link LM Studio until you choose to prepare it here.</div>
            <button disabled={Boolean(busy) || setupJobRunning} onClick={() => void startSetupJob("lmstudio-prime")}>{busy === "lmstudio-prime" || (setupJobContext === "ai" && setupJobRunning) ? "Preparing..." : "Prepare LM Link support"}</button>
          </>}
          <SettingCheckbox label="Enable AI insights" description="Used for summaries, incidents, and troubleshooting suggestions." checked={ai.enabled} onChange={v => update(["aiInsights", "enabled"], v)} />
          <SettingSelect label="Execution" description="Operator intent. The base URL still decides which OpenAI-compatible server receives requests." value={ai.executionMode ?? "local"} options={["local", "remote", "lmlink"]} onChange={v => update(["aiInsights", "executionMode"], v)} />
          <SettingInput label="Insights base URL" description="Chat /v1 endpoint. Use a remote host/Tailnet URL for GPU LLMs; avoid localhost unless this rig should run the LLM." value={ai.openAiBaseUrl} onChange={v => update(["aiInsights", "openAiBaseUrl"], v)} />
          {aiModels.length > 0
            ? <SettingSelect label="Insights model" description="Use the LM Studio model id, not a loaded runtime alias." value={ai.openAiModel} options={aiModels} onChange={v => update(["aiInsights", "openAiModel"], v)} />
            : <SettingInput label="Insights model" description="Chat model id for summaries/incidents." value={ai.openAiModel} onChange={v => update(["aiInsights", "openAiModel"], v)} />}
          <div className="setup-note">{ai.enabled ? "Click Next to load available models when needed and test AI insights." : "AI insights are disabled. Click Next to mark this optional step skipped."}</div>
        </SetupSection>}

        {currentStep.id === "embeddings" && <SetupSection title="Vector DB / Qdrant" description="Optional but recommended for AI incidents. Qdrant stores vectors locally on this rig; embedding inference may run locally or remotely.">
          {qdrantDetection && <div className="setup-detection-card">
            <strong>{qdrantDetection.serviceActive ? "Qdrant service is running" : qdrantInstalled ? "Qdrant is installed" : "Qdrant not detected"}</strong>
            <small>Detection checks for the Qdrant binary, systemd service state, and configured storage path.</small>
            <small>{qdrantDetection.serviceEnabled ? `${qdrantDetection.service} is enabled for autostart.` : "Qdrant autostart service is not enabled."}</small>
            <small>{qdrantDetection.binaryPath ? `Binary: ${qdrantDetection.binaryPath}` : "qdrant binary was not found on PATH."}</small>
            <small>{qdrantDetection.storageExists ? `Storage: ${qdrantDetection.storagePath}` : `Storage will be created at ${embeddings.qdrantStoragePath ?? "/var/lib/pizzawave/qdrant"}`}</small>
          </div>}
          {!qdrantInstalled && <button disabled={Boolean(busy) || setupJobRunning} onClick={() => void startSetupJob("qdrant-prime")}>{busy === "qdrant-prime" || (setupJobContext === "embeddings" && setupJobRunning) ? "Installing..." : "Install native Qdrant"}</button>}
          <SettingCheckbox label="Enable embeddings" description="When enabled, good live transcripts are embedded and stored in local Qdrant for incident matching." checked={!!embeddings.enabled} onChange={v => update(["embeddings", "enabled"], v)} />
          <SettingSelect label="Execution" description="Local keeps embedding generation on this rig; remote points embedding generation at another OpenAI-compatible endpoint." value={embeddings.executionMode ?? "local"} options={["local", "remote", "lmlink"]} onChange={v => update(["embeddings", "executionMode"], v)} />
          <SettingInput label="Embedding base URL" description="OpenAI-compatible /embeddings endpoint. Local LM Studio normally uses http://localhost:1234/v1." value={embeddings.openAiBaseUrl ?? "http://localhost:1234/v1"} onChange={v => update(["embeddings", "openAiBaseUrl"], v)} />
          <SettingInput label="Embedding model" description="Embedding model name. For local LM Studio, the setup service preloads this model only when execution is local." value={embeddings.openAiModel ?? "nomic-embed-text"} onChange={v => update(["embeddings", "openAiModel"], v)} />
          <SettingInput label="Qdrant URL" description="Local Qdrant HTTP endpoint." value={embeddings.qdrantBaseUrl ?? "http://localhost:6333"} onChange={v => update(["embeddings", "qdrantBaseUrl"], v)} />
          <SettingInput label="Qdrant service" description="Systemd service name for restart/status." value={embeddings.qdrantServiceName ?? "qdrant"} onChange={v => update(["embeddings", "qdrantServiceName"], v)} />
          <SettingInput label="Qdrant storage" description="Native Qdrant data path on this rig." value={embeddings.qdrantStoragePath ?? "/var/lib/pizzawave/qdrant"} onChange={v => update(["embeddings", "qdrantStoragePath"], v)} />
          <SettingInput label="Collection" description="Qdrant collection used for call vectors." value={embeddings.collection ?? "pizzawave_calls"} onChange={v => update(["embeddings", "collection"], v)} />
          <SettingInput label="Vector size" description="Must match the embedding model output dimension." type="number" value={embeddings.vectorSize ?? 768} onChange={v => update(["embeddings", "vectorSize"], numberOrZero(v))} />
          <div className="setup-note">{embeddings.enabled ? "Click Next to test the embedding endpoint and Qdrant. The worker creates the collection if needed." : "Embeddings are disabled. Click Next to mark this optional step skipped."}</div>
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

        {currentStep.id === "radio" && <SetupSection title="Radio Setup" description="First-run now hands radio validation to the common Radio Setup workspace instead of running a separate calibration workflow.">
          <div className="setup-note">The TR config and talkgroups you just created become the starting point for Radio Setup. After Finish, PizzaWave opens Tools &gt; Radio Setup so the same workflow can be resumed later for new sites, new SDRs, RF path changes, or expert TR config experiments.</div>
          <label className="setup-check option-row">
            <input
              type="checkbox"
              checked={installOptionalDiagnosticTools}
              onChange={event => {
                update(["setup", "installOptionalDiagnosticTools"], event.target.checked);
                update(["setup", "diagnosticToolsSkippedOrInstalled"], false);
              }}
            />
            <span>
              <strong>Install optional diagnostic tools</strong>
              <span> Installs OP25/P25 control-channel tooling plus SDR/audio diagnostics used by Radio Setup site validation. This can install OS packages and build OP25 from source, but it does not stop trunk-recorder by itself.</span>
            </span>
          </label>
          <div className="setup-review">
            <div><span>Next workspace phase</span><code>Prerequisites</code></div>
            <div><span>Then</span><code>Sites &gt; RF Path &amp; Error and Gain &gt; Call Quality</code></div>
            <div><span>Config changes</span><code>Config Draft source planner</code></div>
          </div>
          <div className="setup-note">Click Next to save this handoff choice. Finish will restart/validate PizzaWave and open Radio Setup.</div>
        </SetupSection>}

        {currentStep.id === "finish" && <SetupSection title="Finish" description="This applies the saved settings, restarts pizzad, re-validates the required checks, then exits setup mode so normal ingest and processing can start.">
          <SetupReview status={status} requiredOpen={requiredOpen} restartVerified={restartVerified} />
          <div className="setup-button-row"><button disabled={Boolean(busy) || setupJobRunning || requiredOpen.length > 0} onClick={() => void finishSetup()}>{busy === "finish-setup" ? "Finishing..." : "Finish setup"}</button></div>
        </SetupSection>}

        <div className="setup-nav-row">
          <button disabled={stepIndex === 0 || Boolean(busy) || setupJobRunning} onClick={previousStep}>Back</button>
          {stepIndex < setupSteps.length - 1 && <button
            className={currentStep.id === "migration" && !migrationResetApplied ? "danger-button" : undefined}
            disabled={Boolean(busy) || setupJobRunning}
            onClick={() => void nextStep()}
          >
            {busy === "migration-reset"
              ? "Resetting..."
              : busy === `advance-${currentStep.id}`
                ? "Working..."
                : currentStep.id === "migration" && !migrationResetApplied
                  ? "Reset & Continue"
                  : "Next"}
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
      <li>Pause trunk-recorder for the candidate pass so the selected RTL-SDR can be claimed cleanly.</li>
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
  runSweep
}: {
  plan: SetupCalibrationPlan | null;
  inputs: Record<string, { gain: string; errorHz: string; ppm: string }>;
  sweep: { rangeHz: string; stepHz: string; warmupSec: string; durationSec: string };
  busy: boolean;
  setInput: (sourceIndex: string, key: "gain" | "errorHz" | "ppm", value: string) => void;
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
  const coversFrequency = (source: { centerFrequency: number; sampleRate: number }, frequencyHz: number) =>
    frequencyHz >= source.centerFrequency - source.sampleRate / 2 && frequencyHz <= source.centerFrequency + source.sampleRate / 2;
  return <div className="calibration-plan">
    <div className="setup-job-head">
      <strong>TR-derived tuning plan</strong>
      <span className="pill">{plan.systems.length} system(s)</span>
      <span className="pill">{plan.sources.length} SDR source(s)</span>
      <span className="pill">{passCount} passes/tuner</span>
      <span className="pill">About {formatElapsed(estimatedSeconds)} each</span>
    </div>
    <div className="setup-note">{plan.diagnostics}</div>
    <div className="setup-note">GQRX is optional and is not launched from pizzad because the service normally has no desktop display. Leave Error Hz and PPM blank and use the wizard sweep first. Start with a short rough sweep, then narrow around the best row.</div>
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
          const coveredControlChannels = system.controlChannelsHz.filter(frequencyHz => coversFrequency(source, frequencyHz));
          const frequency = coveredControlChannels[0] ?? system.controlChannelsHz[0] ?? source.centerFrequency;
          const canSweepControlChannel = coveredControlChannels.length > 0;
          const templateSource = canSweepControlChannel ? null : plan.sources.find(other => other.index !== source.index && system.controlChannelsHz.some(frequencyHz => coversFrequency(other, frequencyHz)));
          const borrowedFrequency = templateSource ? system.controlChannelsHz.find(frequencyHz => coversFrequency(templateSource, frequencyHz)) ?? frequency : frequency;
          const templateSerial = templateSource?.serial || "";
          const command = buildGuidedSweepCommand(system.shortName, system.modulation, source, input, canSweepControlChannel ? frequency : borrowedFrequency, sweep, templateSerial);
          const parameters = buildGuidedSweepParameters(system.shortName, system.modulation, source, input, canSweepControlChannel ? frequency : borrowedFrequency, sweep, templateSerial);
          return <div className="calibration-source-card" key={`${system.shortName}-${source.index}`}>
            <div className="setup-job-head">
              <strong>Source {source.index}</strong>
              <span className="pill">{source.serial ? `rtl=${source.serial}` : source.device || "rtl source"}</span>
              <span className="pill">{formatHz(source.centerFrequency)} center</span>
              <span className="pill">{formatHz(source.sampleRate)} rate</span>
            </div>
            <div className="settings-grid">
              <SettingInput label="Gain" description={`Current config value: ${source.gain || "blank"}. Optional for sweeps.`} value={input.gain} onChange={v => setInput(String(source.index), "gain", v)} />
              <SettingInput label="Error Hz" description={`Current config value: ${source.errorHz || 0}. Leave blank to sweep around current config.`} value={input.errorHz} onChange={v => setInput(String(source.index), "errorHz", v)} />
              <SettingInput label="PPM" description="Optional shortcut. Used only if Error Hz is blank." value={input.ppm} onChange={v => setInput(String(source.index), "ppm", v)} />
            </div>
            {!canSweepControlChannel && <div className="setup-warning-list"><div>{templateSource ? `This source does not cover a control channel in normal service. The wizard can temporarily borrow Source ${templateSource.index}'s center/rate to calibrate rtl=${source.serial || source.index} against a control channel, then restore the baseline config after the sweep.` : "This source does not cover any configured control channel. Use the source-center GQRX launch for RF inspection, or temporarily test this SDR against a known control-channel-capable source later."}</div></div>}
            <strong>Planned sweep</strong>
            {canSweepControlChannel || templateSource ? <pre className="command-box">{command}</pre> : <pre className="command-box">No control channel falls inside this source window.</pre>}
            <button disabled={busy || (!canSweepControlChannel && !templateSource)} onClick={() => void runSweep(parameters)}>{canSweepControlChannel ? "Run control-channel sweep in wizard" : "Run borrowed-window sweep in wizard"}</button>
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
  return <div className="setup-review">
    <h4>Review Before Complete</h4>
    <div><span>TR config</span><code>{tr.configPath ?? "/etc/trunk-recorder/config.json"}</code></div>
    <div><span>Talkgroups</span><code>{tr.talkgroupsPath ?? "/etc/trunk-recorder/talkgroups.csv"}</code></div>
    <div><span>Transcription</span><code>{transcription.provider ?? "none"}</code></div>
    <div><span>AI insights</span><code>{ai.enabled ? ai.openAiModel || "enabled" : "disabled"}</code></div>
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

function TrSourcePlanPreview({ plan }: { plan: SetupTrConfigSourcePlan }) {
  return <div className="tr-source-plan-preview">
    <div className="setup-review compact">
      <div><span>Required windows</span><code>{plan.requiredSourceCount}</code></div>
      <div><span>Planned windows</span><code>{plan.availableSourceCount}</code></div>
      <div><span>Selected sites</span><code>{plan.systems.length}</code></div>
    </div>
    <div className="setup-note">{plan.diagnostics}</div>
    {plan.warnings.length > 0 && <div className="setup-warning-list">{plan.warnings.map(warning => <div key={warning}>{warning}</div>)}</div>}
    <div className="tr-site-cc-list">
      {plan.systems.map(system => <div className="tr-site-cc-row" key={`${system.shortName}-${system.siteName}`}>
        <strong>{system.siteName}</strong>
        <small>CC {system.controlChannelsMhz.length ? system.controlChannelsMhz.map(formatMhz).join(", ") : "not detected"}</small>
        <span>{system.warning || `Assigned ${system.assignedSerial || "no serial"}`}</span>
      </div>)}
    </div>
    <div className="tr-source-plan-grid">
      {plan.sources.map((source, index) => {
        const low = source.centerFrequency - trUsableHalfBandwidth(source.sampleRate);
        const high = source.centerFrequency + trUsableHalfBandwidth(source.sampleRate);
        const coveredSites = plan.systems
          .filter(system => system.frequenciesMhz.some(frequency => source.coveredFrequenciesMhz.includes(frequency)) || system.controlChannelsMhz.some(frequency => source.coveredFrequenciesMhz.includes(frequency)))
          .map(system => system.shortName);
        return <div className="tr-source-plan-card" key={`${source.centerFrequency}-${index}`}>
          <div className="setup-job-head">
            <strong>Source {index + 1}</strong>
            <span className="pill">{source.type || "SDR"}</span>
            <span className="pill">{source.serial || "unassigned"}</span>
          </div>
          <small>{formatRfHz(low)} to {formatRfHz(high)}</small>
          <code>{formatRfHz(source.centerFrequency)}</code>
          <span>{source.driver || "osmosdr"} {source.deviceArgs}</span>
          <span>{formatHz(source.sampleRate)} rate, gain {source.gain || "default"}</span>
          <span>{source.coveredFrequenciesMhz.length} frequenc{source.coveredFrequenciesMhz.length === 1 ? "y" : "ies"} covered</span>
          <span>{coveredSites.length ? coveredSites.join(", ") : "No selected site coverage"}</span>
        </div>;
      })}
    </div>
  </div>;
}

function trUsableHalfBandwidth(sampleRate: number) {
  return Math.floor(Math.max(0, sampleRate) * 0.46875);
}

function trSourcePlanBlockingWarnings(plan: SetupTrConfigSourcePlan | null) {
  if (!plan)
    return [];
  return plan.warnings.filter(warning =>
    /need \d+ SDR source window|uncovered control channel|fall outside|do not start/i.test(warning));
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
      <thead><tr><th>Site</th><th>Short name</th><th>Device</th><th>Center</th><th>Control</th><th>Coverage</th><th>Warning</th></tr></thead>
      <tbody>{draft.systems.map(system => {
        const sources = draft.sources.filter(s => s.label === system.shortName || s.label.startsWith(`${system.shortName}-`));
        const covered = new Set(sources.flatMap(source => source.coveredFrequenciesMhz.map(f => f.toFixed(6))));
        const omitted = system.frequenciesMhz.filter(f => !covered.has(f.toFixed(6)));
        return <tr key={`${system.shortName}-${system.siteName}`}>
          <td>{system.siteName}</td>
          <td>{system.shortName}</td>
          <td>{sources.map(source => `${source.type || "SDR"} ${source.deviceArgs || source.serial || "unassigned"}`).join(", ") || system.assignedSerial || "unassigned"}</td>
          <td>{sources.map(source => formatHz(source.centerFrequency)).join(", ") || formatHz(system.centerFrequency)}</td>
          <td>{system.controlChannelsMhz.map(formatMhz).join(", ")}</td>
          <td>{sources.length ? `${covered.size} covered / ${omitted.length} omitted` : `${system.frequenciesMhz.length} frequencies`}</td>
          <td>{system.warning}</td>
        </tr>;
      })}</tbody>
    </table>
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

function SdrDetectionPanel({ detection }: { detection: SetupSdrDetection }) {
  return <div className="sdr-detection">
    <strong>{detection.message}</strong>
    <small>Detection temporarily frees claimed SDRs when needed, runs rtl_test, and parses device index/serial lines from its output.</small>
    {detection.devices.length > 0 && <table className="table">
      <thead><tr><th>#</th><th>Type</th><th>Serial</th><th>Device</th><th>Rate</th><th>Gain</th><th>Warning</th></tr></thead>
      <tbody>{detection.devices.map(device => <tr key={`${device.index}-${device.serial || device.usbLine}`}>
        <td>{device.index}</td>
        <td>{device.type || "SDR"}</td>
        <td>{device.serial || "unknown"}</td>
        <td><div>{device.label || device.usbLine}</div><small>{device.driver || "osmosdr"} {device.deviceArgs}</small></td>
        <td>{device.defaultSampleRate ? formatHz(device.defaultSampleRate) : ""}</td>
        <td>{device.defaultGain ? `${device.defaultGain} ${device.gainMode}` : ""}</td>
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

function SettingsView({ settingsSections, settingsLoadState, reload, profileState, setProfileState, targetTab, clearTargetTab }: { settingsSections: Record<string, any>; settingsLoadState: { loading: boolean; version: number; message: string; error: boolean }; reload: () => Promise<void>; profileState: ProfileState | null; setProfileState: (value: ProfileState | null) => void; targetTab?: string | null; clearTargetTab?: () => void }) {
  const [settingsTab, setSettingsTab] = useState(() => localStorage.getItem("pizzawave-settings-tab") || "transcription");
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
  useEffect(() => {
    if (!targetTab) return;
    setSettingsTab(targetTab);
    clearTargetTab?.();
  }, [targetTab, clearTargetTab]);
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
        {[
          ["transcription", "Transcription"],
          ["ai", "AI"],
          ["embeddings", "Embeddings"],
          ["talkgroups", "Talkgroups"],
          ["alerts", "Alerts"],
          ["admin", "Security"],
          ["system", "System Info"]
        ].map(([id, text]) => <button key={id} className={settingsTab === id ? "active" : ""} onClick={() => setSettingsTab(id)}>{text}</button>)}
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

      {settingsTab === "talkgroups" && <TalkgroupCatalogSettingsCard profileState={profileState} setProfileState={setProfileState} reload={reload} />}

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

function TalkgroupCatalogSettingsCard({ profileState, setProfileState, reload }: { profileState: ProfileState | null; setProfileState: (value: ProfileState | null) => void; reload: () => Promise<void> }) {
  const [response, setResponse] = useState<TalkgroupCatalogResponse | null>(null);
  const [draft, setDraft] = useState<TalkgroupCatalogDocument | null>(null);
  const [profileDraft, setProfileDraft] = useState<ProfileState | null>(profileState);
  const [filter, setFilter] = useState("");
  const [enabledFilter, setEnabledFilter] = useState<"all" | "enabled" | "disabled" | "high-load">("all");
  const [categoryFilter, setCategoryFilter] = useState("all");
  const [sortKey, setSortKey] = useState<"state" | "id" | "name" | "category" | "load">("id");
  const [sortDir, setSortDir] = useState<"asc" | "desc">("asc");
  const [page, setPage] = useState(1);
  const [showHelp, setShowHelp] = useState(false);
  const [queue, setQueue] = useState<QueueSnapshot | null>(null);
  const [busy, setBusy] = useState("");
  const [message, setMessage] = useState("");

  useEffect(() => { void loadCatalog(); }, []);
  useEffect(() => { void loadTalkgroupLoad(); }, []);
  useEffect(() => setProfileDraft(profileState), [profileState]);
  useEffect(() => {
    if (enabledFilter === "high-load")
      void loadTalkgroupLoad();
  }, [enabledFilter]);
  useEffect(() => setPage(1), [filter, enabledFilter, categoryFilter, sortKey, sortDir]);

  async function loadCatalog() {
    setBusy("load");
    try {
      const loaded = await api.request<TalkgroupCatalogResponse>("/api/v1/talkgroups/catalog");
      setResponse(loaded);
      setDraft(cloneSettings(loaded.document));
      setMessage("");
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Unable to load talkgroup catalog.");
    } finally {
      setBusy("");
    }
  }

  async function loadTalkgroupLoad() {
    try {
      setQueue(await api.request<QueueSnapshot>("/api/v1/system/queue"));
    } catch {
      setQueue(null);
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
      items: [...(current?.items ?? []), { id: nextId, mode: "D", alphaTag: "", description: "", tag: "", sourceCategory: "", opsCategory: "other", enabled: true, incidentEligible: true, source: "manual", notes: "", updatedAtUtc: now }]
    }));
  }

  function deleteItem(id: number) {
    if (!confirmAction("Delete talkgroup from catalog?", "This will remove the TG from the catalog draft. It will not affect capture until you click Apply, which regenerates the trunk-recorder CSV and restarts services.")) return;
    setDraft(current => current && ({ ...current, items: current.items.filter(item => item.id !== id) }));
  }

  function updateProfile(id: string, patch: Partial<ProcessingProfile>) {
    setProfileDraft(current => current && ({ ...current, profiles: current.profiles.map(p => p.id === id ? { ...p, ...patch } : p) }));
  }

  function addProfile() {
    const name = window.prompt("New profile name")?.trim() ?? "";
    if (!name) {
      setMessage("Profile name is required.");
      return;
    }
    if (profileDraft?.profiles.some(profile => profile.name.trim().toLowerCase() === name.toLowerCase())) {
      setMessage(`Profile '${name}' already exists.`);
      return;
    }
    const id = createClientId();
    setProfileDraft(current => current && ({
      ...current,
      profiles: [...current.profiles, { id, name, includePolice: true, includeFire: true, includeEMS: true, includeTraffic: true, includeOther: true, allowedTalkgroups: [], talkgroups: [] }],
      activeProfileId: id
    }));
    setMessage("New profile added to draft. Click Apply to save it.");
  }

  function deleteProfile(id: string) {
    if (!confirmAction("Delete profile?", "This removes the selected profile from the local draft. It will not take effect until you click Apply.")) return;
    setProfileDraft(current => {
      if (!current || current.profiles.length <= 1) return current;
      const profiles = current.profiles.filter(p => p.id !== id);
      return { activeProfileId: current.activeProfileId === id ? profiles[0].id : current.activeProfileId, profiles };
    });
  }

  async function applyChanges() {
    if (!draft || !profileDraft) return;
    const profileNames = profileDraft.profiles.map(profile => profile.name.trim()).filter(Boolean);
    if (profileNames.length !== profileDraft.profiles.length) {
      setMessage("Every profile needs a name before Apply.");
      return;
    }
    if (new Set(profileNames.map(name => name.toLowerCase())).size !== profileNames.length) {
      setMessage("Profile names must be unique before Apply.");
      return;
    }
    const catalogChanged = catalogDirty();
    const profileChanged = profileDirty();
    if (!catalogChanged && !profileChanged) {
      setMessage("No unapplied changes.");
      return;
    }
    const consequences = [
      profileChanged ? "save profile display/downstream policy" : "",
      catalogChanged ? "update the catalog, regenerate the trunk-recorder CSV, and restart trunk-recorder plus pizzad" : ""
    ].filter(Boolean).join("; ");
    if (!confirmAction("Apply talkgroup changes?", `PizzaWave will ${consequences}. Cancel keeps your draft unchanged.`)) return;
    setBusy("apply");
    setMessage("Applying talkgroup changes...");
    try {
      let csvPath = "";
      if (catalogChanged) {
        const savedCatalog = await api.request<TalkgroupCatalogSaveResult>("/api/v1/talkgroups/catalog", { method: "PUT", body: JSON.stringify(draft) });
        csvPath = savedCatalog.generatedCsvPath;
      }
      if (profileChanged) {
        const savedProfiles = await api.request<ProfileState>("/api/v1/profiles", { method: "POST", body: JSON.stringify(profileDraft) });
        setProfileDraft(savedProfiles);
        setProfileState(savedProfiles);
      }
      if (catalogChanged) {
        await api.request<Job>("/api/v1/system/services/trunk-recorder/restart", { method: "POST" });
        await api.request<Job>("/api/v1/system/services/pizzad/restart", { method: "POST" });
      }
      setMessage(catalogChanged
        ? `Applied changes, regenerated ${csvPath || "talkgroups CSV"}, and queued service restarts.`
        : "Profile policy saved. It applies to dashboard filters, alerts, embeddings, and incident creation without restart.");
      await reload();
      await loadCatalog();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Apply failed.");
    } finally {
      setBusy("");
    }
  }

  function activeProfile() {
    return profileDraft?.profiles.find(p => p.id === profileDraft.activeProfileId) ?? profileDraft?.profiles[0] ?? null;
  }

  function activeSetting(id: number) {
    return activeProfile()?.talkgroups?.find(t => t.id === id);
  }

  function effectiveRow(item: TalkgroupCatalogItem): TalkgroupCatalogItem {
    const setting = activeSetting(item.id);
    return {
      ...item,
      enabled: setting?.enabled ?? item.enabled,
      alphaTag: setting?.label?.trim() || item.alphaTag,
      opsCategory: setting?.category?.trim() || item.opsCategory,
      incidentEligible: setting?.incidentEligible ?? item.incidentEligible
    };
  }

  function updateActiveTalkgroup(id: number, patch: Partial<{ enabled: boolean; label: string; category: string; incidentEligible: boolean }>) {
    if (!profileDraft) return;
    setProfileDraft(current => {
      if (!current) return current;
      const profiles = current.profiles.map(profile => {
        if (profile.id !== current.activeProfileId) return profile;
        const settings = [...(profile.talkgroups ?? [])];
        const index = settings.findIndex(t => t.id === id);
        const next = { ...(index >= 0 ? settings[index] : { id }), ...patch };
        if (index >= 0) settings[index] = next;
        else settings.push(next);
        return { ...profile, talkgroups: settings, allowedTalkgroups: [] };
      });
      return { ...current, profiles };
    });
  }

  const needle = filter.trim().toLowerCase();
  const effectiveItems = (draft?.items ?? []).map(effectiveRow);
  const categoryOptions = Array.from(new Set(effectiveItems.map(item => item.opsCategory).filter(Boolean))).sort();
  const highLoadByTalkgroup = new Map((queue?.topAudioTalkgroups ?? []).map(row => [row.talkgroup, row]));
  function loadFor(item: TalkgroupCatalogItem) {
    return highLoadByTalkgroup.get(item.id)?.audioSeconds ?? 0;
  }
  function compareRows(a: TalkgroupCatalogItem, b: TalkgroupCatalogItem) {
    const direction = sortDir === "asc" ? 1 : -1;
    const value = (() => {
      switch (sortKey) {
        case "state": return Number(a.enabled) - Number(b.enabled);
        case "name": return a.alphaTag.localeCompare(b.alphaTag, undefined, { sensitivity: "base" });
        case "category": return a.opsCategory.localeCompare(b.opsCategory, undefined, { sensitivity: "base" });
        case "load": return loadFor(a) - loadFor(b);
        case "id":
        default: return a.id - b.id;
      }
    })();
    return value === 0 ? a.id - b.id : value * direction;
  }
  function sortBy(key: typeof sortKey) {
    if (sortKey === key)
      setSortDir(current => current === "asc" ? "desc" : "asc");
    else {
      setSortKey(key);
      setSortDir(key === "load" ? "desc" : "asc");
    }
  }
  const rows = effectiveItems
    .filter(item => enabledFilter !== "high-load" || highLoadByTalkgroup.has(item.id))
    .filter(item => enabledFilter === "all" || enabledFilter === "high-load" || (enabledFilter === "enabled" ? item.enabled : !item.enabled))
    .filter(item => categoryFilter === "all" || item.opsCategory === categoryFilter)
    .filter(item => !needle ||
      String(item.id).includes(needle) ||
      item.alphaTag.toLowerCase().includes(needle) ||
      item.description.toLowerCase().includes(needle) ||
      item.tag.toLowerCase().includes(needle) ||
      item.opsCategory.toLowerCase().includes(needle))
    .sort(compareRows);
  const pageSize = 50;
  const pageCount = Math.max(1, Math.ceil(rows.length / pageSize));
  const currentPage = Math.min(page, pageCount);
  const visibleRows = rows.slice((currentPage - 1) * pageSize, currentPage * pageSize);
  const startRow = rows.length === 0 ? 0 : (currentPage - 1) * pageSize + 1;
  const endRow = Math.min(rows.length, currentPage * pageSize);
  const enabledCount = effectiveItems.filter(item => item.enabled).length;
  const active = activeProfile();
  const originalDocumentJson = response ? JSON.stringify(response.document) : "";
  const originalProfileJson = profileState ? JSON.stringify(profileState) : "";
  function catalogDirty() {
    return Boolean(draft && originalDocumentJson && JSON.stringify(draft) !== originalDocumentJson);
  }
  function profileDirty() {
    return Boolean(profileDraft && originalProfileJson && JSON.stringify(profileDraft) !== originalProfileJson);
  }
  const hasUnappliedChanges = catalogDirty() || profileDirty();
  useEffect(() => {
    if (hasUnappliedChanges)
      localStorage.setItem("pizzawave-unapplied-talkgroups", "1");
    else
      localStorage.removeItem("pizzawave-unapplied-talkgroups");
    if (!hasUnappliedChanges) return;
    const handler = (event: BeforeUnloadEvent) => {
      event.preventDefault();
      event.returnValue = "You have unapplied talkgroup changes.";
    };
    window.addEventListener("beforeunload", handler);
    return () => window.removeEventListener("beforeunload", handler);
  }, [hasUnappliedChanges]);

  return <div className="card settings-card wide talkgroups-settings-card">
    <div className="settings-card-meta">
      <h3>Talkgroups</h3>
      <div className="settings-card-actions">
        <button type="button" onClick={() => setShowHelp(true)}>Help</button>
        <button className="danger-button" disabled={Boolean(busy) || !hasUnappliedChanges} onClick={() => void applyChanges()}>{busy === "apply" ? "Applying..." : "Apply"}</button>
      </div>
      {message && <span className={message.toLowerCase().includes("fail") || message.toLowerCase().includes("unable") ? "section-status error" : "section-status ok"}>{message}</span>}
    </div>
    <div className="settings-fields">
      {showHelp && <div className="modal-backdrop" onClick={() => setShowHelp(false)}>
        <div className="card talkgroup-help-modal" onClick={event => event.stopPropagation()}>
          <div className="recommendation-head">
            <h3>Talkgroups Help</h3>
            <button type="button" onClick={() => setShowHelp(false)}>Close</button>
          </div>
          <p>Profiles control dashboard visibility and downstream alert, embedding, and incident participation. Profile enable/disable changes do not stop capture or transcription.</p>
          <p>Deleting a catalog TG is a capture change. Apply saves the draft, regenerates the trunk-recorder CSV, and restarts services only when catalog capture changes exist.</p>
          <p>High Load refreshes queue/load data when selected and filters the same table, so edits happen in one place.</p>
        </div>
      </div>}
      {profileDraft && <>
        <div className="profile-toolbar compact-profile-toolbar">
        <span className="compact-label">Profile</span>
        <select value={profileDraft.activeProfileId} onChange={e => setProfileDraft({ ...profileDraft, activeProfileId: e.target.value })}>{profileDraft.profiles.map(p => <option value={p.id} key={p.id}>{p.name}</option>)}</select>
        <span className="compact-label">Name</span>
        {active && <input value={active.name} onChange={e => updateProfile(active.id, { name: e.target.value })} />}
        <button type="button" className="danger-button" disabled={Boolean(busy)} onClick={addProfile}>Add</button>
        {active && <button type="button" className="danger-button" disabled={profileDraft.profiles.length <= 1} onClick={() => deleteProfile(active.id)}>Delete</button>}
        </div>
        <div className="talkgroup-filters">
          <input placeholder="Filter TGs" value={filter} onChange={e => setFilter(e.target.value)} />
          <select value={enabledFilter} onChange={e => setEnabledFilter(e.target.value as "all" | "enabled" | "disabled" | "high-load")}>
            <option value="all">All TGs</option>
            <option value="enabled">Enabled only</option>
            <option value="disabled">Disabled only</option>
            <option value="high-load">High load</option>
          </select>
          <select value={categoryFilter} onChange={e => setCategoryFilter(e.target.value)}>
            <option value="all">All categories</option>
            {categoryOptions.map(category => <option value={category} key={category}>{label(category)}</option>)}
          </select>
        </div>
      </>}
      {hasUnappliedChanges && <p className="settings-message error">Unapplied changes. Click Apply to save them, or leave this page to discard the draft.</p>}
      <div className="talkgroup-catalog-table">
        <div className="table-top-pagination">
          <span className="muted">{startRow}-{endRow} of {rows.length} rows / {enabledCount} enabled</span>
          <button disabled={currentPage <= 1} onClick={() => setPage(1)}>First</button>
          <button disabled={currentPage <= 1} onClick={() => setPage(currentPage - 1)}>Prev</button>
          <span>{currentPage} / {pageCount}</span>
          <button disabled={currentPage >= pageCount} onClick={() => setPage(currentPage + 1)}>Next</button>
          <button disabled={currentPage >= pageCount} onClick={() => setPage(pageCount)}>Last</button>
        </div>
        <table className="table compact-table">
          <thead><tr>
            <th><button type="button" className="sort-header" onClick={() => sortBy("state")}>State {sortKey === "state" ? sortDir : ""}</button></th>
            <th><button type="button" className="sort-header" onClick={() => sortBy("id")}>TG ID {sortKey === "id" ? sortDir : ""}</button></th>
            <th><button type="button" className="sort-header" onClick={() => sortBy("name")}>Name {sortKey === "name" ? sortDir : ""}</button></th>
            <th><button type="button" className="sort-header" onClick={() => sortBy("category")}>Category {sortKey === "category" ? sortDir : ""}</button></th>
            <th><button type="button" className="sort-header" onClick={() => sortBy("load")}>Recent load {sortKey === "load" ? sortDir : ""}</button></th>
            <th>Catalog</th>
          </tr></thead>
          <tbody>{visibleRows.map((item, index) => <tr className={item.enabled ? "" : "excluded-row"} key={`${item.id}-${index}`}>
            <td><button type="button" onClick={() => updateActiveTalkgroup(item.id, { enabled: !item.enabled })}>{item.enabled ? "Disable" : "Enable"}</button></td>
            <td>{item.id}</td>
            <td><input value={item.alphaTag} onChange={e => updateActiveTalkgroup(item.id, { label: e.target.value })} /></td>
            <td><select value={item.opsCategory} onChange={e => updateActiveTalkgroup(item.id, { category: e.target.value })}>{categories.map(c => <option value={c} key={c}>{label(c)}</option>)}</select></td>
            <td>{highLoadByTalkgroup.has(item.id) ? `${highLoadByTalkgroup.get(item.id)!.calls.toLocaleString()} / ${formatDurationMinutes(highLoadByTalkgroup.get(item.id)!.audioSeconds / 60)}` : "--"}</td>
            <td><button className="danger-button" onClick={() => deleteItem(item.id)}>Delete</button></td>
          </tr>)}</tbody>
        </table>
      </div>
    </div>
  </div>;
}

function SettingInput({ label: text, description, value, onChange, type = "text", className = "", inputMode }: { label: string; description: string; value: any; onChange: (value: string) => void; type?: string; className?: string; inputMode?: "text" | "decimal" | "numeric" | "search" | "email" | "tel" | "url" }) {
  return <label className="setting-field">
    <span>{text}<small>{description}</small></span>
    <input className={className} type={type} inputMode={inputMode} value={value ?? ""} onChange={e => onChange(e.target.value)} />
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
  sweep: { rangeHz: string; stepHz: string; warmupSec: string; durationSec: string },
  templateSerial = ""
) {
  const error = numericField(input.errorHz);
  const ppm = numericField(input.ppm);
  const baseError = error !== null
    ? Math.round(error)
    : ppm !== null
      ? Math.abs(Math.round(source.centerFrequency * ppm / 1_000_000))
      : source.errorHz || 0;
  const gain = input.gain.trim() || (source.gain && source.gain !== "0" ? source.gain : "32");
  const serial = source.serial || String(source.index);
  return [
    "sudo /usr/lib/pizzawave/scripts/tr_tune.sh error-sweep",
    `--system ${systemShortName}`,
    `--control-channel ${controlFrequencyHz}`,
    `--device-serial ${serial}`,
    templateSerial ? `--template-serial ${templateSerial}` : "",
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
  sweep: { rangeHz: string; stepHz: string; warmupSec: string; durationSec: string },
  templateSerial = ""
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
    gain: input.gain.trim() || (source.gain && source.gain !== "0" ? source.gain : "32"),
    templateSerial
  };
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
  return value === "dashboard" || value === "tools" || value === "system" || value === "settings" || categories.includes(value as any)
    ? value as Page
    : "dashboard";
}

function navIcon(item: Page) {
  if (item === "dashboard") return <Gauge size={15} />;
  if (item === "tools") return <Wrench size={15} />;
  if (item === "settings") return <Settings size={15} />;
  if (item === "system") return <Activity size={15} />;
  return <Radio size={15} />;
}

function label(value: string) {
  if (!value) return "--";
  if (value === "inconclusive" || value === "voice_inconclusive" || value === "unknown") return "Unknown";
  if (value === "ems") return "EMS";
  if (value === "system") return "System";
  if (value === "tools") return "Tools";
  return value[0].toUpperCase() + value.slice(1).replaceAll("_", " ");
}

createRoot(document.getElementById("root")!).render(<App />);
