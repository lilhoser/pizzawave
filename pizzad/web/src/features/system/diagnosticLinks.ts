export type DiagnosticTab = "recommendations" | "services" | "queue" | "jobs" | "storage" | "backup" | "audit" | "tr" | "metrics";
export type DiagnosticMetric = "calls" | "transcription" | "rf" | "incidents" | "ai" | "bandwidth";
export type DiagnosticLink = { tab: DiagnosticTab; metric?: DiagnosticMetric; findingId?: number };

// Navigation only. Query parameters cannot request a mutation, recovery or equipment action.
export function readDiagnosticLink(search: string): DiagnosticLink | null {
  const parameters = new URLSearchParams(search);
  if (parameters.get("page") !== "system") return null;
  const tab = parameters.get("tab") ?? "recommendations";
  if (!["recommendations", "services", "queue", "jobs", "storage", "backup", "audit", "tr", "metrics"].includes(tab)) return null;
  const link: DiagnosticLink = { tab: tab as DiagnosticTab };
  const metric = parameters.get("metric");
  if (tab === "metrics" && ["calls", "transcription", "rf", "incidents", "ai", "bandwidth"].includes(metric ?? "")) link.metric = metric as DiagnosticMetric;
  const finding = parameters.get("finding") ?? "";
  if (tab === "recommendations" && /^[1-9][0-9]*$/.test(finding) && Number.isSafeInteger(Number(finding))) link.findingId = Number(finding);
  return link;
}
