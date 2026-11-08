import { useMemo, useState } from "react";
import { PencilLine } from "lucide-react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "../api/client";
import type { Json } from "../api/types";
import { initialText, isConcealed, isMultiline } from "../lib/fixRetry";
import { ru } from "../i18n/ru";
import { Dialog, useFeedback } from "./feedback";
import { parseInput } from "./RunDialog";
import { Button, Callout, Loading } from "./ui";

/**
 * «Исправить и повторить» (D-085): переменные процесса с текущими значениями, изменённые отправляются, и тот же шаг
 * выполняется ещё раз. Секретные значения не показываются: пустое поле — без изменений. Так же — переменная, в значении
 * которой есть секрет (сервер показывает его маской «••••»): отправить маску обратно значило бы записать её вместо секрета.
 */
export function FixAndRetryDialog({ processId, onClose }: { processId: string; onClose: () => void }) {
  const details = useQuery({ queryKey: ["process", processId], queryFn: () => api.process(processId) });
  const queryClient = useQueryClient();
  const feedback = useFeedback();
  const variables = details.data?.definition.variables ?? [];
  const hidden = useMemo(() => new Set(details.data?.hiddenVariables ?? []), [details.data]);
  const concealed = (v: (typeof variables)[number]) => isConcealed(v, hidden);
  const initial = useMemo(
    () => Object.fromEntries(variables.map((v) => [v.name, initialText(v, details.data?.variables[v.name], hidden)])),
    [variables, details.data, hidden],
  );
  const [values, setValues] = useState<Record<string, string | boolean> | null>(null);
  const current = values ?? initial;
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const changed = variables.filter((v) => (concealed(v) ? current[v.name] !== "" : current[v.name] !== initial[v.name]));

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
          {variable.type === "boolean" && !concealed(variable) ? (
            <span className="check">
              <input type="checkbox" checked={current[variable.name] === true}
                onChange={(e) => setValues({ ...current, [variable.name]: e.target.checked })} />
              {current[variable.name] === true ? "Да" : "Нет"}
            </span>
          ) : !variable.secret && isMultiline(variable, initial[variable.name]) ? (
            // Многострочный текст и JSON — в многострочном поле: обычное поле убрало бы переносы строк.
            <textarea className={variable.type === "text" ? "textarea" : "textarea mono"} value={String(current[variable.name] ?? "")}
              placeholder={hidden.has(variable.name) ? hiddenHint : undefined}
              onChange={(e) => setValues({ ...current, [variable.name]: e.target.value })} />
          ) : (
            <input className="input" type={concealed(variable) ? "password" : variable.type === "number" ? "number" : "text"}
              autoComplete={concealed(variable) ? "off" : undefined}
              placeholder={variable.secret ? "секретное значение не показывается — пусто: без изменений" : hidden.has(variable.name) ? hiddenHint : undefined}
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

const hiddenHint = "в значении есть секрет, оно не показывается — пусто: без изменений; новое значение станет секретом";
