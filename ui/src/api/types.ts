// Типы ответов локального API (зеркало Pepa.Host/Api/Dtos.cs и модели сценария).

export type Json = null | boolean | number | string | Json[] | { [key: string]: Json };

export type VariableType = "any" | "text" | "number" | "boolean" | "list" | "object";

export interface VariableDefinition {
  name: string;
  type: VariableType;
  default: Json | null;
  description: string;
  isInput: boolean;
}

export interface NodeSettings {
  retryCount: number;
  retryDelaySeconds: number;
  timeoutSeconds: number | null;
  /** Отладка: пауза перед блоком. */
  breakpoint?: boolean;
}

export interface NodeDefinition {
  id: string;
  type: string;
  label: string | null;
  position: { x: number; y: number };
  config: Record<string, Json>;
  settings: NodeSettings;
  note: string | null;
}

export interface EdgeDefinition {
  id: string;
  source: string;
  sourcePort: string;
  target: string;
}

export interface ScenarioDefinition {
  schemaVersion: number;
  id: string;
  name: string;
  description: string;
  variables: VariableDefinition[];
  nodes: NodeDefinition[];
  edges: EdgeDefinition[];
  editor: Record<string, Json> | null;
}

export type IssueSeverity = "error" | "warning";

export interface ValidationIssue {
  severity: IssueSeverity;
  code: string;
  message: string;
  nodeId: string | null;
  edgeId: string | null;
  field: string | null;
}

export interface ValidationReport {
  issues: ValidationIssue[];
  isValid: boolean;
  errorCount: number;
  warningCount: number;
}

export type NodeEffect = "none" | "readOnly" | "external";
export type PortKind = "normal" | "error" | "optional";
export type FieldKind =
  | "text"
  | "template"
  | "multilineTemplate"
  | "path"
  | "expression"
  | "condition"
  | "number"
  | "boolean"
  | "select"
  | "variable"
  | "time"
  | "matchCondition"
  | "scenario"
  | "seconds"
  | "code";

export interface PortDescriptor {
  id: string;
  label: string;
  kind: PortKind;
  required: boolean;
}

export interface FieldDescriptor {
  name: string;
  label: string;
  kind: FieldKind;
  required: boolean;
  default: Json | null;
  help: string | null;
  placeholder: string | null;
  options: { value: string; label: string }[] | null;
  min: number | null;
  max: number | null;
  visibleWhen: { field: string; values: string[] } | null;
  lookup: string | null;
}

export interface NodeTypeDescriptor {
  type: string;
  category: string;
  title: string;
  description: string;
  icon: string;
  effect: NodeEffect;
  supportsVerification: boolean;
  isStart: boolean;
  isEnd: boolean;
  outputs: PortDescriptor[];
  fields: FieldDescriptor[];
  summaryField: string | null;
  supportsRetry: boolean;
  resourceHint: string | null;
}

export interface NodeCategory {
  id: string;
  title: string;
  order: number;
}

export interface NodeTypes {
  categories: NodeCategory[];
  nodeTypes: NodeTypeDescriptor[];
}

export type ProcessStatus =
  | "running"
  | "waiting"
  | "paused"
  | "needsAttention"
  | "completed"
  | "failed"
  | "cancelled";

export type AttentionKind = "none" | "unknownStep" | "stepFailed" | "stepLimit" | "engineError";
export type PauseReason = "none" | "user" | "restart" | "breakpoint";
export type ProcessAction = "pause" | "cancel" | "continue" | "verify" | "retry" | "markSucceeded" | "skip" | "runWithoutPauses";
export type ResumeAction = "continue" | "verify" | "retry" | "markSucceeded" | "skip" | "runWithoutPauses";

export interface ProcessBrief {
  id: string;
  status: ProcessStatus;
  createdAt: string;
  finishedAt: string | null;
}

export interface ScenarioSummary {
  id: string;
  name: string;
  description: string;
  version: number;
  createdAt: string;
  updatedAt: string;
  nodeCount: number;
  errorCount: number;
  warningCount: number;
  hasInputs: boolean;
  lastRun: ProcessBrief | null;
}

export interface Scenario {
  id: string;
  name: string;
  description: string;
  version: number;
  createdAt: string;
  updatedAt: string;
  definition: ScenarioDefinition;
  validation: ValidationReport;
}

export interface ProcessSummary {
  id: string;
  shortId: string;
  scenarioId: string;
  scenarioName: string;
  scenarioVersion: number;
  status: ProcessStatus;
  attention: AttentionKind;
  pauseReason: PauseReason;
  statusMessage: string | null;
  createdAt: string;
  updatedAt: string;
  finishedAt: string | null;
  stepCount: number;
  currentNodeId: string | null;
  currentNodeLabel: string | null;
  wakeAt: string | null;
  allowedActions: ProcessAction[];
  waitKind: WaitKind;
  waitingFor: string | null;
  parentProcessId: string | null;
}

export type WaitKind = "none" | "timer" | "retry" | "event" | "resource";

export interface ProcessRef {
  id: string;
  shortId: string;
  scenarioName: string;
  status: ProcessStatus;
}

export type SubscriptionState = "active" | "fulfilled" | "expired" | "cancelled";

export interface Wait {
  id: number;
  nodeId: string;
  source: string;
  eventType: string | null;
  correlationKey: string | null;
  matchExpression: string | null;
  priority: number;
  purpose: "event" | "childProcess";
  state: SubscriptionState;
  eventId: number | null;
  createdAt: string;
  expiresAt: string | null;
}

export type EventState = "received" | "matched" | "delivered" | "unmatched" | "conflict" | "ignored";

export interface PepaEvent {
  id: number;
  source: string;
  type: string;
  externalId: string | null;
  correlationKey: string | null;
  payload: Json | null;
  receivedAt: string;
  state: EventState;
  process: ProcessRef | null;
  candidates: { subscriptionId: number; process: ProcessRef; stillWaiting: boolean }[];
  startedProcesses: string[];
  note: string | null;
  handledAt: string | null;
}

export interface Resource {
  key: string;
  title: string;
  holder: ProcessRef | null;
  acquiredAt: string | null;
  updatedAt: string;
  queue: { process: ProcessRef; requestedAt: string }[];
}

export type TriggerKind = "schedule" | "event";

export interface Trigger {
  id: string;
  scenarioId: string;
  scenarioName: string;
  kind: TriggerKind;
  enabled: boolean;
  name: string;
  config: Record<string, Json>;
  inputs: Record<string, Json> | null;
  overlap: "skip" | "allow";
  description: string;
  nextRunAt: string | null;
  lastRunAt: string | null;
  lastProcessId: string | null;
  missedRuns: number;
  lastMissedAt: string | null;
  skippedRuns: number;
  lastError: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface SaveTrigger {
  scenarioId: string;
  kind: TriggerKind;
  enabled: boolean;
  name: string;
  config: Record<string, Json>;
  inputs: Record<string, Json> | null;
  overlap: "skip" | "allow";
}

export type StepState =
  | "planned"
  | "running"
  | "waiting"
  | "succeeded"
  | "failed"
  | "unknown"
  | "skipped"
  | "cancelled";

export interface Step {
  id: number;
  nodeId: string;
  nodeType: string;
  nodeLabel: string;
  attempt: number;
  effect: NodeEffect;
  state: StepState;
  input: Record<string, Json> | null;
  output: Record<string, Json> | null;
  port: string | null;
  error: string | null;
  resolution: string | null;
  plannedAt: string;
  startedAt: string | null;
  finishedAt: string | null;
}

export type JournalLevel = "info" | "warning" | "error";

export interface JournalEntry {
  id: number;
  stepId: number | null;
  nodeId: string | null;
  at: string;
  level: JournalLevel;
  message: string;
}

export interface ProcessDetails {
  process: ProcessSummary;
  variables: Record<string, Json>;
  lastError: Record<string, Json> | null;
  steps: Step[];
  journal: JournalEntry[];
  definition: ScenarioDefinition;
  parent: ProcessRef | null;
  children: ProcessSummary[];
  waits: Wait[];
  heldResources: string[];
}

export interface Overview {
  counts: {
    running: number;
    waiting: number;
    paused: number;
    needsAttention: number;
    completedToday: number;
    failedToday: number;
  };
  attention: ProcessSummary[];
  active: ProcessSummary[];
  recent: ProcessSummary[];
  eventConflicts: number;
  missedTriggers: Trigger[];
  upcomingTriggers: Trigger[];
}

export interface Info {
  version: string;
  dataDirectory: string;
  workspaceRoot: string;
  isDesktop: boolean;
  startedAt: string;
  recovery: {
    paused: number;
    unknownSteps: number;
    cancelledPlannedSteps: number;
    releasedResources: number;
    missedRuns: number;
  };
}

export interface Settings {
  workspaceRoot: string;
  maxParallelProcesses: number;
  stepBudget: number;
  actionTimeoutSeconds: number;
  restartRequired: boolean;
  adsPowerUrl: string | null;
  /** При сохранении: null — не менять, "" — удалить. */
  adsPowerApiKey: string | null;
  adsPowerHasKey: boolean;
}

export type TelegramState =
  | "notConfigured"
  | "loginRequired"
  | "connecting"
  | "waitingCode"
  | "waitingPassword"
  | "connected"
  | "disconnected";

export interface TelegramStatus {
  state: TelegramState;
  /** Имя подключённого аккаунта. */
  account: string | null;
  /** Пояснение: что произошло или что нужно сделать. */
  message: string | null;
  /** Сохранённые данные для повторного входа (api_hash не отдаётся). */
  apiId: number | null;
  phone: string | null;
}

export interface RecorderState {
  recording: boolean;
  browser: "pepa" | "adspower" | null;
  profileId: string | null;
  startUrl: string | null;
  actions: import("../lib/recording").RecordedAction[];
}

export interface ScenarioTemplate {
  name: string;
  description: string;
  /** Названия блоков, из которых собран шаблон. */
  blocks: string[];
  definition: ScenarioDefinition;
}

export interface DesktopWindow {
  title: string;
  process: string;
}

export interface AdsPowerProfile {
  id: string;
  number: string | null;
  name: string;
  group: string | null;
}
