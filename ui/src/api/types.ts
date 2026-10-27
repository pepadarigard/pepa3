// Типы ответов локального API (зеркало Pepa.Host/Api/Dtos.cs и модели сценария).

export type Json = null | boolean | number | string | Json[] | { [key: string]: Json };

export type VariableType = "any" | "text" | "number" | "boolean" | "list" | "object";

export interface VariableDefinition {
  name: string;
  type: VariableType;
  default: Json | null;
  description: string;
  isInput: boolean;
  /** Секретное значение (пароль): вводится скрыто, в истории процесса — «••••». */
  secret?: boolean;
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
  | "url"
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

export type AttentionKind = "none" | "unknownStep" | "stepFailed" | "stepLimit" | "engineError" | "restoredFromBackup";
export type PauseReason = "none" | "user" | "restart" | "breakpoint";
export type ProcessAction = "pause" | "cancel" | "continue" | "verify" | "retry" | "markSucceeded" | "markFailed" | "skip" | "runWithoutPauses";
export type ResumeAction = "continue" | "verify" | "retry" | "markSucceeded" | "markFailed" | "skip" | "runWithoutPauses";

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

/** Вопрос процесса («Спросить пользователя»), который ждёт ответа. Варианты отвечаются номером. */
export interface Question {
  id: number;
  process: ProcessRef;
  text: string;
  options: string[];
  freeText: boolean;
  secret: boolean;
  hasImage: boolean;
  askedAt: string;
  expiresAt: string | null;
  note: string | null;
}

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
  /** Ресурс заняла сама Pepa (например, «запись действий в окнах Windows»). */
  heldBy?: string | null;
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
  /** Блоки, где сейчас ветви процесса (у параллельного участка их несколько). */
  activeNodeIds: string[];
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
    /** База восстановлена из копии: столько процессов ждут решения. */
    restored: number;
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
  /** Сообщать в Telegram («Избранное»), когда процесс ждёт решения. */
  notifyTelegram: boolean;
  /** Сколько дней хранить завершённые процессы; 0 — всегда. */
  historyDays: number;
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
  proxy: TelegramProxy;
}

export type TelegramProxyKind = "none" | "socks5" | "mtproxy";

/** Сохранённый прокси: пароль и секрет не отдаются, только признак, что они есть. */
export interface TelegramProxy {
  kind: TelegramProxyKind;
  host: string;
  port: number;
  username: string | null;
  hasPassword: boolean;
  hasSecret: boolean;
  /** Настройки не прочитались — подключения нет, пока прокси не задан заново. */
  problem: string | null;
}

/** Прокси для сохранения или проверки: ссылкой или полями. Пароль/секрет null — оставить сохранённый. */
export interface TelegramProxyRequest {
  kind?: TelegramProxyKind;
  host?: string;
  port?: number;
  username?: string | null;
  password?: string | null;
  secret?: string | null;
  link?: string;
}

export interface RecorderState {
  recording: boolean;
  browser: "pepa" | "adspower" | null;
  profileId: string | null;
  startUrl: string | null;
  actions: import("../lib/recording").RecordedAction[];
}

export interface BackupInfo {
  name: string;
  /** daily — ежедневная, manual — по кнопке, migration — перед обновлением базы. */
  kind: "daily" | "manual" | "migration";
  createdAt: string;
  size: number;
}

export interface Backups {
  directory: string;
  /** Почему не удалась последняя автоматическая копия. */
  lastError: string | null;
  items: BackupInfo[];
}

/** Папка, за которой следит Pepa: новые (и, по желанию, изменённые) файлы — события «files». */
export interface FolderWatch {
  /** Пусто у новой строки — номер выдаст Pepa. */
  id: string;
  folder: string;
  pattern: string;
  subfolders: boolean;
  changes: boolean;
  enabled: boolean;
}

export interface FolderWatchStatus {
  /** Как сохранено (а не как сейчас в форме). */
  enabled: boolean;
  files: number;
  /** Файлы, которые ещё дописываются. */
  pending: number;
  lastScanAt: string | null;
  error: string | null;
  note: string | null;
}

export interface Watches {
  watches: (FolderWatch & { status: FolderWatchStatus | null })[];
  /** Источник событий о файлах («files»). */
  source: string;
}

/** Записанный шаг в окнах Windows — готовый блок. */
export interface RecordedDesktopStep {
  type: string;
  config: Record<string, Json>;
  label: string | null;
  /** Что проверить в шаге (ненадёжный поиск, сокращённый заголовок окна, пропущенные действия). */
  note: string | null;
  /** Введён пароль: в блоке секретная переменная secretVariable. */
  secret: boolean;
  secretVariable?: string | null;
}

export interface DesktopRecorderState {
  /** Почему запись в окнах недоступна (null — доступна). */
  unavailableReason: string | null;
  recording: boolean;
  steps: RecordedDesktopStep[];
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

/** Элемент окна для выбора в конструкторе (в порядке обхода дерева, depth — вложенность). */
export interface DesktopElement {
  depth: number;
  name: string;
  controlType: string;
  automationId: string;
  enabled: boolean;
  /** Подпись в окне одна такая — по ней элемент найдётся однозначно. */
  uniqueName: boolean;
  uniqueId: boolean;
}

export interface DesktopElements {
  window: string;
  process: string;
  /** Показаны не все элементы (сложное окно). */
  truncated: boolean;
  elements: DesktopElement[];
}

export interface AdsPowerProfile {
  id: string;
  number: string | null;
  name: string;
  group: string | null;
}

export interface AssistantStatus {
  configured: boolean;
  /** Последние символы ключа (сам ключ не отдаётся). */
  keyHint: string | null;
  model: string;
}

/** Черновик помощника: блоки для вставки в схему. */
/** Объяснение сценария помощником: в целом, по блокам и что проверить. */
export interface AssistantExplanation {
  summary: string;
  /** blockId — блок сценария (null — помощник назвал блок, которого нет). */
  steps: { blockId: string | null; text: string }[];
  cautions: string[];
  usage: { inputTokens: number; outputTokens: number; cacheReadTokens: number };
}

export interface AssistantDraft {
  summary: string;
  questions: string[];
  variables: VariableDefinition[];
  nodes: NodeDefinition[];
  edges: EdgeDefinition[];
  entryNodeId: string | null;
  /** Ошибки проверки, которые помощник не исправил: черновик можно вставить и поправить. */
  problems: string[];
  requests: number;
  usage: { inputTokens: number; outputTokens: number; cacheReadTokens: number };
}

/** Расширение (папка в plugins): что загружено или почему нет. */
export interface PluginInfo {
  id: string;
  name: string;
  version: string | null;
  nodeTypes: string[];
  /** Не загружено: причина (блоков этого расширения нет, остальная Pepa работает). */
  error: string | null;
}

export interface Plugins {
  /** Папка, куда кладут расширения. */
  directory: string;
  plugins: PluginInfo[];
}
