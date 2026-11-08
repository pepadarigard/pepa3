import { useMemo, useState } from "react";
import { PencilLine } from "lucide-react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "../api/client";
import type { Json, VariableDefinition } from "../api/types";
import { ru } from "../i18n/ru";
import { Dialog, useFeedback } from "./feedback";
import { parseInput } from "./RunDialog";
import { Button, Callout, Loading } from "./ui";

/**
 * «Исправить и повторить» (D-085): переменные процесса с текущими значениями, изменённые отправляются, и тот же шаг
 * выполняется ещё раз. Секретные значения не показываются: пустое поле — без изменений.
 */
export function FixAndRetryDialog({ processId, onClose }: { processId: string; onClose: () => void }) {
  const details = useQuery({ queryKey: ["process", processId], queryFn: () => api.process(processId) });
  const queryClient = useQueryClient();
  const feedback = useFeedback();
  const variables = details.data?.definition.variables ?? [];
  const initial = useMemo(
    () => Object.fromEntries(variables.map((v) => [v.name, v.secret ? "" : textOf(v, details.data?.variables[v.name])])),
    [variables, details.data],
  );
  const [values, setValues] = useState<Record<string, string | boolean> | null>(null);
  const current = values ?? initial;
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const changed = variables.filter((v) => (v.secret ? current[v.name] !== "" : current[v.name] !== initial[v.name]));

  const submit = async () => {
    const parsed: Record<string, Json> = {};
    for (const variable of changed) {
      const result = parseInput(variable, current[variable.name]);
      if ("error" in result) {
        setError(`«${variable.name}»: ${result.error}`);
        return;
      }
      parsed[variable.name] = result.value;
    }
    setError(null);
    setBusy(true);
    try {
      const result = await api.resume(processId, "retryWithChanges", parsed);
      feedback.toast(result.message, "success");
      void queryClient.invalidateQueries({ queryKey: ["process", processId] });
      void queryClient.invalidateQueries({ queryKey: ["processes"] });
      void queryClient.invalidateQueries({ queryKey: ["overview"] });
      onClose();
    } catch (e) {
      feedback.error(e);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Dialog
      title={ru.actions.retryWithChanges.label}
      onClose={onClose}
      width={620}
      footer={
        <>
          <Button onClick={onClose}>{ru.common.cancel}</Button>
          <Button variant="primary" icon={PencilLine} busy={busy} disabled={changed.length === 0} onClick={submit}>
            Исправить и выполнить шаг
          </Button>
        </>
      }
    >
      <p className="muted small">
        Шаг не выполнился (это проверено), поэтому его можно повторить с исправленными данными. То, что процесс уже сделал до
        этого шага, не повторяется. Каждое исправление запишется в журнал процесса.
      </p>
      {details.isLoading && <Loading />}
      {details.error && <Callout tone="danger">{details.error instanceof Error ? details.error.message : String(details.error)}</Callout>}
      {variables.length === 0 && details.data && <p className="small muted">В сценарии нет переменных.</p>}
      {variables.map((variable) => (
        <label key={variable.name} className="field" data-testid="fix-variable">
          <span className="field-label">
            {variable.name}
            <span className="faint">· {ru.variableTypes[variable.type]}</span>
            {changed.includes(variable) && <span className="faint">· изменено</span>}
          </span>
          {variable.type === "boolean" ? (
            <span className="check">
              <input type="checkbox" checked={current[variable.name] === true}
                onChange={(e) => setValues({ ...current, [variable.name]: e.target.checked })} />
              {current[variable.name] === true ? "Да" : "Нет"}
            </span>
          ) : !variable.secret && (variable.type === "list" || variable.type === "object" || variable.type === "any") ? (
            <textarea className="textarea mono" value={String(current[variable.name] ?? "")}
              onChange={(e) => setValues({ ...current, [variable.name]: e.target.value })} />
          ) : (
            <input className="input" type={variable.secret ? "password" : variable.type === "number" ? "number" : "text"}
              autoComplete={variable.secret ? "off" : undefined}
              placeholder={variable.secret ? "секретное значение не показывается — пусто: без изменений" : undefined}
              value={String(current[variable.name] ?? "")}
              onChange={(e) => setValues({ ...current, [variable.name]: e.target.value })} />
          )}
          {variable.description && <span className="field-help">{variable.description}</span>}
        </label>
      ))}
      {error && <Callout tone="danger">{error}</Callout>}
    </Dialog>
  );
}

/** Значение переменной в поле: текст и число как есть, остальное — JSON (так его поймёт parseInput). */
function textOf(v: VariableDefinition, value: Json | undefined): string | boolean {
  if (v.type === "boolean") return value === true;
  if (value === null || value === undefined) return "";
  if (v.type === "text" || v.type === "number") return String(value);
  return JSON.stringify(value, null, 2);
}
