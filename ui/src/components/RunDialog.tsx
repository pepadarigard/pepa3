import { useState } from "react";
import { Play } from "lucide-react";
import { useQueryClient } from "@tanstack/react-query";
import { api, ApiError } from "../api/client";
import type { Json, VariableDefinition } from "../api/types";
import { ru } from "../i18n/ru";
import { navigate } from "../router";
import { Dialog, useFeedback } from "./feedback";
import { Button, Callout } from "./ui";
import { AdsPowerPicker } from "../pages/editor/FieldEditor";
import { newId } from "../lib/graph";
import { batchRuns, manyCandidates, tableColumns, tableRuns } from "../lib/runMany";

/** Запуск сценария: если есть входные переменные — сначала спросить значения. */
export function useRunScenario() {
  const feedback = useFeedback();
  const queryClient = useQueryClient();
  const [dialog, setDialog] = useState<{
    id: string; name: string; inputs: VariableDefinition[]; stepMode: boolean; values?: Record<string, Json> | null;
  } | null>(null);

  const start = async (id: string, inputs?: Record<string, Json>, stepMode = false) => {
    try {
      const result = await api.run(id, inputs, stepMode);
      queryClient.invalidateQueries({ queryKey: ["processes"] });
      queryClient.invalidateQueries({ queryKey: ["overview"] });
      feedback.toast(stepMode ? "Процесс запущен пошагово: пауза перед каждым блоком" : "Процесс запущен");
      navigate({ page: "process", id: result.processId });
      return true;
    } catch (e) {
      if (e instanceof ApiError && e.validation) {
        feedback.toast(`${e.message} Откройте сценарий в конструкторе: ошибок — ${e.validation.errorCount}.`, "error");
      } else {
        feedback.error(e);
      }
      return false;
    }
  };

  /**
   * <paramref name="stepMode"/> — отладка: пауза перед каждым блоком. <paramref name="values"/> — входные данные прошлого
   * запуска («Запустить снова»): подставляются в окно запуска, секретные поля остаются пустыми.
   */
  const run = async (id: string, options?: { stepMode?: boolean; values?: Record<string, Json> | null }) => {
    const stepMode = options?.stepMode === true;
    try {
      const scenario = await api.scenario(id);
      const inputs = scenario.definition.variables.filter((v) => v.isInput);
      if (inputs.length === 0) {
        await start(id, undefined, stepMode);
      } else {
        setDialog({ id, name: scenario.name, inputs, stepMode, values: options?.values });
      }
    } catch (e) {
      feedback.error(e);
    }
  };

  const startMany = async (id: string, runs: Record<string, Json>[], batchId: string) => {
    try {
      const result = await api.runMany(id, runs, batchId);
      queryClient.invalidateQueries({ queryKey: ["processes"] });
      queryClient.invalidateQueries({ queryKey: ["overview"] });
      feedback.toast(`Запущено процессов: ${result.processIds.length}`);
      navigate({ page: "runs" });
      return true;
    } catch (e) {
      if (e instanceof ApiError && e.validation) {
        feedback.toast(`${e.message} Откройте сценарий в конструкторе: ошибок — ${e.validation.errorCount}.`, "error");
      } else {
        feedback.error(e);
      }
      return false;
    }
  };

  const element = dialog && (
    <RunDialog
      name={dialog.name}
      inputs={dialog.inputs}
      previous={dialog.values}
      allowMany={!dialog.stepMode}
      onCancel={() => setDialog(null)}
      onRun={async (values) => {
        if (await start(dialog.id, values, dialog.stepMode)) setDialog(null);
      }}
      onRunMany={async (runs, batchId) => {
        if (await startMany(dialog.id, runs, batchId)) setDialog(null);
      }}
    />
  );

  return { run, element };
}

function RunDialog({ name, inputs, previous, allowMany, onRun, onRunMany, onCancel }: {
  name: string; inputs: VariableDefinition[]; previous?: Record<string, Json> | null; allowMany: boolean;
  onRun: (values: Record<string, Json>) => Promise<void>;
  onRunMany: (runs: Record<string, Json>[], batchId: string) => Promise<void>;
  onCancel: () => void;
}) {
  const [values, setValues] = useState<Record<string, string | boolean>>(() =>
    Object.fromEntries(inputs.map((v) => [v.name, initialText(v, v.secret ? undefined : previous?.[v.name])])),
  );
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  // «Несколько запусков»: одна входная переменная меняется — по процессу на каждое её значение.
  const candidates = allowMany ? manyCandidates(inputs) : [];
  const [many, setMany] = useState(false);
  const [manyVar, setManyVar] = useState(() => candidates[0]?.name ?? "");
  const [lines, setLines] = useState("");
  // «Таблица»: первая строка — имена переменных, каждая следующая — запуск (можно вставить из Excel).
  const [manyMode, setManyMode] = useState<"list" | "table">("list");
  const [table, setTable] = useState("");
  const [batchId] = useState(() => newId("b"));
  const byTable = many && manyMode === "table";
  const varying = many && !byTable ? inputs.find((v) => v.name === manyVar) : undefined;
  const fromTable = byTable ? tableColumns(table) : [];
  const count = byTable
    ? Math.max(0, table.split("\n").filter((l) => l.trim() !== "").length - 1)
    : lines.split("\n").filter((l) => l.trim() !== "").length;

  const submit = async () => {
    const parsed: Record<string, Json> = {};
    for (const variable of inputs) {
      if (variable === varying || fromTable.includes(variable.name)) continue;
      const result = parseInput(variable, values[variable.name]);
      if ("error" in result) {
        setError(`«${variable.name}»: ${result.error}`);
        return;
      }
      parsed[variable.name] = result.value;
    }
    if (byTable) {
      const runs = tableRuns(table, inputs, parsed, parseInput);
      if ("error" in runs) {
        setError(runs.error);
        return;
      }
      setError(null);
      setBusy(true);
      await onRunMany(runs.runs, batchId);
      setBusy(false);
      return;
    }
    if (varying) {
      const runs = batchRuns(varying, lines, parsed, parseInput);
      if ("error" in runs) {
        setError(runs.error);
        return;
      }
      setError(null);
      setBusy(true);
      await onRunMany(runs.runs, batchId);
      setBusy(false);
      return;
    }
    setError(null);
    setBusy(true);
    await onRun(parsed);
    setBusy(false);
  };

  const addLine = (value: string) => {
    const existing = lines.split("\n").map((l) => l.trim()).filter((l) => l !== "");
    if (!existing.includes(value)) setLines([...existing, value].join("\n"));
  };

  return (
    <Dialog
      title={`Запуск: ${name}`}
      onClose={onCancel}
      footer={
        <>
          <Button onClick={onCancel}>{ru.common.cancel}</Button>
          <Button variant="primary" icon={Play} busy={busy} onClick={submit} disabled={many && count === 0}>
            {many ? `Запустить ${count} ${count === 1 ? "процесс" : count >= 2 && count <= 4 ? "процесса" : "процессов"}` : ru.common.run}
          </Button>
        </>
      }
    >
      <p className="muted small">
        {previous
          ? "Значения — как у прошлого запуска (секретные нужно ввести заново). Каждый запуск — отдельный процесс со своими переменными."
          : "Укажите входные значения. Каждый запуск — отдельный процесс со своими переменными."}
      </p>
      {candidates.length > 0 && (
        <label className="row small" style={{ gap: 8 }} data-testid="run-many">
          <input type="checkbox" checked={many} onChange={(e) => setMany(e.target.checked)} />
          Несколько запусков — по процессу на каждое значение
        </label>
      )}
      {many && (
        <div className="row small" style={{ gap: 12 }}>
          <label className="row" style={{ gap: 6 }}>
            <input type="radio" checked={manyMode === "list"} onChange={() => setManyMode("list")} /> Значения одной переменной
          </label>
          <label className="row" style={{ gap: 6 }}>
            <input type="radio" checked={manyMode === "table"} onChange={() => setManyMode("table")} /> Таблица: строка — запуск
          </label>
        </div>
      )}
      {byTable && (
        <label className="field">
          <span className="field-label">Таблица · запусков: {count}</span>
          <textarea className="textarea mono" data-testid="run-many-table" value={table}
            placeholder={`${inputs.slice(0, 2).map((v) => v.name).join("\t")}\n…`} onChange={(e) => setTable(e.target.value)} />
          <span className="field-help">
            Первая строка — имена входных переменных ({inputs.map((v) => v.name).join(", ")}), каждая следующая — отдельный процесс.
            Можно скопировать из Excel; разделитель — табуляция, «;» или «,». Переменные, которых нет в таблице, — общие (ниже).
          </span>
        </label>
      )}
      {many && !byTable && (
        <div className="stack" style={{ gap: 8 }}>
          <label className="field">
            <span className="field-label">Что меняется</span>
            <select className="select" value={manyVar} onChange={(e) => setManyVar(e.target.value)}>
              {candidates.map((v) => <option key={v.name} value={v.name}>{v.name}</option>)}
            </select>
          </label>
          <label className="field">
            <span className="field-label">Значения «{manyVar}» — по одному на строку · {count}</span>
            <textarea className="textarea mono" data-testid="run-many-values" value={lines} placeholder={"k1a2b3\nk4c5d6"}
              onChange={(e) => setLines(e.target.value)} />
            {varying?.lookup === "adspower-profiles" && <AdsPowerPicker onPick={addLine} />}
            <span className="field-help">
              Каждое значение — отдельный процесс; остальные входные данные — общие. Одновременно работают не больше процессов,
              чем разрешено в «Настройках», а один профиль AdsPower — по очереди. Запускаются все или ни один.
            </span>
          </label>
        </div>
      )}
      {inputs.filter((variable) => variable !== varying && !fromTable.includes(variable.name)).map((variable) => (
        <label key={variable.name} className="field">
          <span className="field-label">
            {variable.name}
            <span className="faint">· {ru.variableTypes[variable.type]}</span>
          </span>
          {variable.type === "boolean" ? (
            <span className="check">
              <input
                type="checkbox"
                checked={values[variable.name] === true}
                onChange={(e) => setValues({ ...values, [variable.name]: e.target.checked })}
              />
              {values[variable.name] === true ? "Да" : "Нет"}
            </span>
          ) : !variable.secret && (variable.type === "list" || variable.type === "object" || variable.type === "any") ? (
            <textarea
              className="textarea mono"
              value={String(values[variable.name] ?? "")}
              onChange={(e) => setValues({ ...values, [variable.name]: e.target.value })}
            />
          ) : (
            <input
              className="input"
              type={variable.secret ? "password" : variable.type === "number" ? "number" : "text"}
              autoComplete={variable.secret ? "off" : undefined}
              value={String(values[variable.name] ?? "")}
              onChange={(e) => setValues({ ...values, [variable.name]: e.target.value })}
            />
          )}
          {variable.lookup === "adspower-profiles" && !variable.secret && (variable.type === "text" || variable.type === "any") && (
            <AdsPowerPicker onPick={(id) => setValues({ ...values, [variable.name]: id })} />
          )}
          {variable.description && <span className="field-help">{variable.description}</span>}
        </label>
      ))}
      {error && <Callout tone="danger">{error}</Callout>}
    </Dialog>
  );
}

/** Начальное значение поля: прошлое значение (если есть) или значение по умолчанию. */
function initialText(v: VariableDefinition, previous?: Json): string | boolean {
  const value = previous !== undefined ? previous : v.default;
  if (v.type === "boolean") return value === true;
  if (value === null || value === undefined) return "";
  if (v.type === "text" || v.type === "number") return String(value);
  // У «любого» текст показывается как есть, только если его нельзя принять за число, да/нет или JSON — иначе с кавычками,
  // чтобы при запуске он остался текстом («12345», «true»).
  if (v.type === "any" && typeof value === "string" && value !== "" && value.trim() === value && !parsesAsJson(value)) return value;
  return JSON.stringify(value, null, 2);
}

function parsesAsJson(text: string): boolean {
  try {
    JSON.parse(text);
    return true;
  } catch {
    return false;
  }
}

export function parseInput(v: VariableDefinition, raw: string | boolean | undefined): { value: Json } | { error: string } {
  switch (v.type) {
    case "boolean":
      return { value: raw === true };
    case "text":
      return { value: String(raw ?? "") };
    case "number": {
      const text = String(raw ?? "").trim().replace(",", ".");
      if (text === "") return { error: "введите число" };
      const n = Number(text);
      return Number.isFinite(n) ? { value: n } : { error: "введите число" };
    }
    default: {
      const text = String(raw ?? "").trim();
      if (text === "") return { value: null };
      try {
        const value = JSON.parse(text) as Json;
        if (v.type === "list" && !Array.isArray(value)) return { error: "нужен список, например [1, 2, 3]" };
        if (v.type === "object" && (typeof value !== "object" || value === null || Array.isArray(value)))
          return { error: 'нужен объект, например {"ключ": "значение"}' };
        return { value };
      } catch {
        if (v.type === "any") return { value: text };
        return { error: "некорректный JSON" };
      }
    }
  }
}
