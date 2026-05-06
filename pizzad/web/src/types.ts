export type Kpi = { label: string; value: string; subtext: string };
export type BarStat = { label: string; value: number; ratio: number; valueText: string };
export type AlertMatch = {
  id: number;
  callId: number;
  ruleName: string;
  detail: string;
  matchedAt: number;
  isImported: boolean;
  notificationSuppressed: boolean;
};
export type Incident = {
  id: number;
  title: string;
  detail: string;
  firstSeen: number;
  lastSeen: number;
  calls: { callId: number; rawTimestamp: number; transcript: string; audioUrl: string }[];
};
export type TopTalkgroup = {
  label: string;
  talkgroup: number;
  count: number;
  share: number;
  lastHeard: number;
  trend: number[];
  trendStartLabel: string;
  trendBucketLabel: string;
  trendEndLabel: string;
};
export type Dashboard = {
  kpis: Kpi[];
  inaudibleBySystem: BarStat[];
  problemTalkgroups: BarStat[];
  categoryShare: BarStat[];
  topTalkgroups: TopTalkgroup[];
  alerts: AlertMatch[];
  incidents: Incident[];
};
export type EngineCall = {
  id: number;
  startTime: number;
  stopTime: number;
  systemShortName: string;
  talkgroup: number;
  talkgroupName: string;
  category: string;
  audioPath: string;
  transcription: string;
  transcriptionStatus: string;
  isImported: boolean;
  isAlertMatch: boolean;
};
export type CategoryPage = {
  category: string;
  groupBy: string;
  groups: { label: string; calls: EngineCall[] }[];
};
export type Job = {
  id: number;
  type: string;
  status: string;
  total: number;
  completed: number;
  failed: number;
  message: string;
};
export type TrHealth = {
  id: number;
  windowStartUtc: string;
  windowEndUtc: string;
  scope: string;
  decodeZeroPct: number;
  callsStarted: number;
  callsConcluded: number;
  sampleStops: number;
};
