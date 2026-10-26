import { useState } from "react";
import { Play } from "lucide-react";
import { useQueryClient } from "@tanstack/react-query";
import { api, ApiError } from "../api/client";
import type { Json, VariableDefinition } from "../api/types";
import { ru } from "../i18n/ru";
import { navigate } from "../router";
import { Dialog, useFeedback } from "./feedback";
import { Button, Callout } from "./ui";

/** Запуск сценария: если есть входные переменные — сначала спросить значения. */
export function useRunScenario() {
  const feedback = useFeedback();
  const queryClient = useQueryClient();
  const [dialog, setDialog] = useState<{ id: string; name: string; inputs: VariableDefinition[] } | null>(null);

  const start = async (id: string, inputs?: Record<string, Json>) => {
    try {
      const result = await api.run(id, inputs);
      queryClient.invalidateQueries({ queryKey: ["processes"] });
      queryClient.invalidateQueries({ queryKey: ["overview"] });
      feedback.toast("Процесс запущен");
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

  const run = async (id: string) => {
    try {
      const scenario = await api.scenario(id);
      const inputs = scenario.definition.variables.filter((v) => v.isInput);
      if (inputs.length === 0) {
        await start(id);
      } else {
        setDialog({ id, name: scenario.name, inputs });
      }
    } catch (e) {
      feedback.error(e);
    }
  };

  const element = dialog && (
    <RunDialog
      name={dialog.name}
      inputs={dialog.inputs}
      onCancel={() => setDialog(null)}
      onRun={async (values) => {
        if (await start(dialog.id, values)) setDialog(null);
      }}
    />
  );

  return { run, element };
}

function RunDialog({ name, inputs, onRun, onCancel }: { name: string; inputs: VariableDefinition[]; onRun: (values: Record<string, Json>) => Promise<void>; onCancel: () => void }) {
  const [values, setValues] = useState<Record<string, string | boolean>>(() =>
    Object.fromEntries(inputs.map((v) => [v.name, initialText(v)])),
  );
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const submit = async () => {
    const parsed: Record<string, Json> = {};
    for (const variable of inputs) {
      const result = parseInput(variable, values[variable.name]);
      if ("error" in result) {
        setError(`«${variable.name}»: ${result.error}`);
        return;
      }
      parsed[variable.name] = result.value;
    }
    setError(null);
    setBusy(true);
    await onRun(parsed);
    setBusy(false);
  };

  return (
    <Dialog
      title={`Запуск: ${name}`}
      onClose={onCancel}
      footer={
        <>
          <Button onClick={onCancel}>{ru.common.cancel}</Button>
          <Button variant="primary" icon={Play} busy={busy} onClick={submit}>
            {ru.common.run}
          </Button>
        </>
      }
    >
      <p className="muted small">Укажите входные значения. Каждый запуск — отдельный процесс со своими переменными.</p>
      {inputs.map((variable) => (
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
          ) : variable.type === "list" || variable.type === "object" || variable.type === "any" ? (
            <textarea
              className="textarea mono"
              value={String(values[variable.name] ?? "")}
              onChange={(e) => setValues({ ...values, [variable.name]: e.target.value })}
            />
          ) : (
            <input
              className="input"
              type={variable.type === "number" ? "number" : "text"}
              value={String(values[variable.name] ?? "")}
              onChange={(e) => setValues({ ...values, [variable.name]: e.target.value })}
            />
          )}
          {variable.description && <span className="field-help">{variable.description}</span>}
        </label>
      ))}
      {error && <Callout tone="danger">{error}</Callout>}
    </Dialog>
  );
}

function initialText(v: VariableDefinition): string | boolean {
  if (v.type === "boolean") return v.default === true;
  if (v.default === null || v.default === undefined) return "";
  if (v.type === "text" || v.type === "number") return String(v.default);
  return JSON.stringify(v.default, null, 2);
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
