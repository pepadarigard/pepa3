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
  | "time";

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
export type PauseReason = "none" | "user" | "restart";
export type ProcessAction = "pause" | "cancel" | "continue" | "verify" | "retry" | "markSucceeded" | "skip";
export type ResumeAction = "continue" | "verify" | "retry" | "markSucceeded" | "skip";

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
}

export interface Info {
  version: string;
  dataDirectory: string;
  workspaceRoot: string;
  isDesktop: boolean;
  startedAt: string;
  recovery: { paused: number; unknownSteps: number; cancelledPlannedSteps: number };
}

export interface Settings {
  workspaceRoot: string;
  maxParallelProcesses: number;
  stepBudget: number;
  actionTimeoutSeconds: number;
  restartRequired: boolean;
}
