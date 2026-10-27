import { useEffect, useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { BellRing, CalendarClock, Pencil, Play, Plus, Trash2 } from "lucide-react";
import { api } from "../api/client";
import { useTriggers } from "../api/hooks";
import type { Json, SaveTrigger, Trigger, TriggerKind, VariableDefinition } from "../api/types";
import { ru } from "../i18n/ru";
import { formatMoment, plural, relative } from "../lib/format";
import { navigate } from "../router";
import { Dialog, useFeedback } from "./feedback";
import { parseInput } from "./RunDialog";
import { Badge, Button, Callout, Empty, Loading } from "./ui";

const DAYS = ["пн", "вт", "ср", "чт", "пт", "сб", "вс"];

/** Источники событий Pepa (можно вписать и свой). */
const EVENT_SOURCES = [
  ["http", "Запрос к API Pepa"],
  ["manual", "Отправлено вручную"],
  ["telegram", "Сообщение в Telegram"],
  ["files", "Файл в отслеживаемой папке"],
] as const;

/** Диалог «Запуск по расписанию и событиям» для сценария. */
export function useTriggersDialog() {
  const [target, setTarget] = useState<{ id: string; name: string } | null>(null);
  const element = target && <TriggersDialog scenarioId={target.id} scenarioName={target.name} onClose={() => setTarget(null)} />;
  return { open: (id: string, name: string) => setTarget({ id, name }), element };
}

function useInvalidateTriggers() {
  const client = useQueryClient();
  return () => {
    client.invalidateQueries({ queryKey: ["triggers"] });
    client.invalidateQueries({ queryKey: ["overview"] });
  };
}

function TriggersDialog({ scenarioId, scenarioName, onClose }: { scenarioId: string; scenarioName: string; onClose: () => void }) {
  const triggers = useTriggers(scenarioId);
  const [variables, setVariables] = useState<VariableDefinition[] | null>(null);
  const [editing, setEditing] = useState<Trigger | "new-schedule" | "new-event" | null>(null);
  const feedback = useFeedback();

  useEffect(() => {
    api.scenario(scenarioId).then((s) => setVariables(s.definition.variables.filter((v) => v.isInput)), feedback.error);
  }, [scenarioId, feedback.error]);

  if (editing && variables) {
    return (
      <TriggerEditor
        scenarioId={scenarioId}
        scenarioName={scenarioName}
        inputs={variables}
        trigger={typeof editing === "string" ? null : editing}
        kind={typeof editing === "string" ? (editing === "new-event" ? "event" : "schedule") : editing.kind}
        onDone={() => setEditing(null)}
      />
    );
  }

  const list = triggers.data ?? [];
  return (
    <Dialog title={`Автозапуск: ${scenarioName}`} onClose={onClose} width={720}
      footer={<Button onClick={onClose}>{ru.common.close}</Button>}>
      <p className="muted small">
        Сценарий может запускаться сам — по расписанию или когда приходит событие. Если Pepa была закрыта в момент запуска,
        пропущенные запуски не выполняются сами: они будут показаны на главной, и вы решите, запускать ли их.
      </p>
      {triggers.isLoading ? (
        <Loading />
      ) : list.length === 0 ? (
        <Empty icon={CalendarClock} title="Автозапуска нет">Сценарий запускается только вручную.</Empty>
      ) : (
        <div className="stack">
          {list.map((t) => <TriggerRow key={t.id} trigger={t} onEdit={() => setEditing(t)} />)}
        </div>
      )}
      <div className="row wrap">
        <Button icon={CalendarClock} onClick={() => setEditing("new-schedule")} disabled={!variables}>По расписанию</Button>
        <Button icon={BellRing} onClick={() => setEditing("new-event")} disabled={!variables}>По событию</Button>
      </div>
    </Dialog>
  );
}

export function TriggerRow({ trigger: t, onEdit, showScenario }: { trigger: Trigger; onEdit?: () => void; showScenario?: boolean }) {
  const feedback = useFeedback();
  const invalidate = useInvalidateTriggers();
  const [busy, setBusy] = useState<string | null>(null);

  const act = async (name: string, action: () => Promise<unknown>, message?: string) => {
    setBusy(name);
    try {
      await action();
      if (message) feedback.toast(message);
    } catch (e) {
      feedback.error(e);
    } finally {
      setBusy(null);
      invalidate();
    }
  };

  const runNow = () => act("run", async () => {
    const result = await api.runTrigger(t.id);
    navigate({ page: "process", id: result.processId });
  }, "Процесс запущен");

  const toggle = () => act("toggle", () => api.updateTrigger(t.id, toSave(t, { enabled: !t.enabled })),
    t.enabled ? "Автозапуск выключен" : "Автозапуск включён");

  const remove = async () => {
    if (!(await feedback.confirm({ title: "Удалить автозапуск?", text: `«${t.name || t.description}» больше не будет запускать сценарий.`, confirmLabel: "Удалить", danger: true }))) return;
    await act("delete", () => api.deleteTrigger(t.id), "Удалено");
  };

  return (
    <div className="trigger-row">
      <div className="trigger-icon">{t.kind === "schedule" ? <CalendarClock size={16} /> : <BellRing size={16} />}</div>
      <div className="stack" style={{ gap: 3, flex: 1, minWidth: 0 }}>
        <div className="row wrap" style={{ gap: 8 }}>
          <strong>{t.name || (t.kind === "schedule" ? "По расписанию" : "По событию")}</strong>
          {!t.enabled && <Badge tone="muted">Выключено</Badge>}
          {t.missedRuns > 0 && <Badge tone="attention">Пропущено: {t.missedRuns}</Badge>}
        </div>
        <div className="small muted">
          {showScenario && <>«{t.scenarioName}» · </>}
          {t.description}
          {t.kind === "schedule" && t.enabled && t.nextRunAt && <> · следующий {formatMoment(t.nextRunAt)} ({relative(t.nextRunAt)})</>}
        </div>
        <div className="small faint">
          {t.lastRunAt ? <>Последний запуск {formatMoment(t.lastRunAt)}</> : "Ещё не запускался"}
          {t.lastProcessId && <> · <a href={`#/process/${t.lastProcessId}`}>процесс #{t.lastProcessId.slice(-6)}</a></>}
          {t.skippedRuns > 0 && <> · не запускался, пока работал предыдущий: {t.skippedRuns}</>}
        </div>
        {t.missedRuns > 0 && (
          <div className="small" style={{ color: "var(--attention)" }}>
            {t.missedRuns} {plural(t.missedRuns, "запуск пропущен", "запуска пропущено", "запусков пропущено")}, пока Pepa была закрыта
            {t.lastMissedAt ? ` (первый — ${formatMoment(t.lastMissedAt)})` : ""}. Сами они не выполнятся.
          </div>
        )}
        {t.lastError && <div className="small" style={{ color: "var(--danger)" }}>Ошибка последнего запуска: {t.lastError}</div>}
      </div>
      <div className="row wrap" style={{ justifyContent: "flex-end" }}>
        {t.missedRuns > 0 && (
          <Button size="small" busy={busy === "dismiss"} onClick={() => act("dismiss", () => api.dismissTrigger(t.id))}>Не запускать</Button>
        )}
        {t.kind === "schedule" && (
          <Button size="small" icon={Play} variant={t.missedRuns > 0 ? "primary" : "default"} busy={busy === "run"} onClick={runNow}
            title="Запустить сейчас (один раз)">
            Запустить сейчас
          </Button>
        )}
        <Button size="small" busy={busy === "toggle"} onClick={toggle}>{t.enabled ? "Выключить" : "Включить"}</Button>
        {onEdit && <Button size="small" icon={Pencil} iconOnly title="Изменить" onClick={onEdit} />}
        <Button size="small" icon={Trash2} iconOnly variant="ghost" title="Удалить" busy={busy === "delete"} onClick={remove} />
      </div>
    </div>
  );
}

function toSave(t: Trigger, patch: Partial<SaveTrigger> = {}): SaveTrigger {
  return {
    scenarioId: t.scenarioId,
    kind: t.kind,
    enabled: t.enabled,
    name: t.name,
    config: t.config,
    inputs: t.inputs,
    overlap: t.overlap,
    ...patch,
  };
}

// ───────────── Редактор ─────────────

function TriggerEditor({ scenarioId, scenarioName, inputs, trigger, kind, onDone }: {
  scenarioId: string;
  scenarioName: string;
  inputs: VariableDefinition[];
  trigger: Trigger | null;
  kind: TriggerKind;
  onDone: () => void;
}) {
  const feedback = useFeedback();
  const invalidate = useInvalidateTriggers();
  const config = trigger?.config ?? {};
  const [name, setName] = useState(trigger?.name ?? "");
  const [enabled, setEnabled] = useState(trigger?.enabled ?? true);
  const [overlap, setOverlap] = useState<"skip" | "allow">(trigger?.overlap ?? "skip");
  const [mode, setMode] = useState(String(config.mode ?? "daily"));
  const [everyMinutes, setEveryMinutes] = useState(String(config.everyMinutes ?? 60));
  const [time, setTime] = useState(String(config.time ?? "09:00"));
  const [days, setDays] = useState<number[]>(Array.isArray(config.days) ? (config.days as number[]) : []);
  const [at, setAt] = useState(String(config.at ?? ""));
  const [source, setSource] = useState(String(config.source ?? "http"));
  const [eventType, setEventType] = useState(String(config.eventType ?? ""));
  const [correlationKey, setCorrelationKey] = useState(String(config.correlationKey ?? ""));
  const [filter, setFilter] = useState(String(config.filter ?? ""));
  const [values, setValues] = useState<Record<string, string>>(() =>
    Object.fromEntries(inputs.map((v) => {
      const current = trigger?.inputs?.[v.name];
      if (kind === "event") return [v.name, typeof current === "string" ? current : ""];
      const value = current ?? v.default;
      return [v.name, value === null || value === undefined ? "" : typeof value === "string" ? value : JSON.stringify(value)];
    })),
  );
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const save = async () => {
    let builtConfig: Record<string, Json>;
    if (kind === "schedule") {
      builtConfig =
        mode === "interval" ? { mode, everyMinutes: Number(everyMinutes) }
        : mode === "once" ? { mode, at }
        : { mode, time, days: [...days].sort((a, b) => a - b) };
    } else {
      builtConfig = { source: source.trim(), eventType: eventType.trim(), correlationKey: correlationKey.trim(), filter: filter.trim() };
    }

    const builtInputs: Record<string, Json> = {};
    for (const variable of inputs) {
      const raw = values[variable.name] ?? "";
      if (kind === "event") {
        if (raw.trim()) builtInputs[variable.name] = raw.trim();
        continue;
      }
      if (raw === "" && variable.type !== "text") continue;
      const parsed = parseInput(variable, raw);
      if ("error" in parsed) {
        setError(`«${variable.name}»: ${parsed.error}`);
        return;
      }
      builtInputs[variable.name] = parsed.value;
    }

    const request: SaveTrigger = {
      scenarioId, kind, enabled, name: name.trim(), config: builtConfig,
      inputs: Object.keys(builtInputs).length ? builtInputs : null, overlap,
    };
    setBusy(true);
    setError(null);
    try {
      if (trigger) await api.updateTrigger(trigger.id, request);
      else await api.createTrigger(request);
      feedback.toast("Автозапуск сохранён");
      invalidate();
      onDone();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Dialog title={`${trigger ? "Изменить" : "Новый"} запуск ${kind === "schedule" ? "по расписанию" : "по событию"} · ${scenarioName}`}
      onClose={onDone} width={620}
      footer={
        <>
          <Button onClick={onDone}>{ru.common.back}</Button>
          <Button variant="primary" busy={busy} onClick={save} icon={trigger ? undefined : Plus}>{ru.common.save}</Button>
        </>
      }>
      <label className="field">
        <span className="field-label">Название</span>
        <input className="input" value={name} placeholder={kind === "schedule" ? "Например: Каждое утро" : "Например: Новый заказ"} onChange={(e) => setName(e.target.value)} />
      </label>

      {kind === "schedule" ? (
        <>
          <div className="segmented" style={{ alignSelf: "flex-start" }}>
            {[["interval", "Каждые N минут"], ["daily", "По дням недели"], ["once", "Один раз"]].map(([value, label]) => (
              <button key={value} className={mode === value ? "active" : ""} onClick={() => setMode(value)}>{label}</button>
            ))}
          </div>
          {mode === "interval" && (
            <label className="field">
              <span className="field-label">Каждые, минут<span className="req">*</span></span>
              <input className="input" type="number" min={1} value={everyMinutes} onChange={(e) => setEveryMinutes(e.target.value)} />
              <span className="field-help">Отсчёт от начала суток: «каждые 30 минут» — в :00 и :30.</span>
            </label>
          )}
          {mode === "daily" && (
            <>
              <label className="field">
                <span className="field-label">Время<span className="req">*</span></span>
                <input className="input" type="time" value={time} onChange={(e) => setTime(e.target.value)} style={{ width: 140 }} />
              </label>
              <div className="field">
                <span className="field-label">Дни недели</span>
                <div className="row wrap" style={{ gap: 6 }}>
                  {DAYS.map((label, i) => {
                    const day = i + 1;
                    const on = days.includes(day);
                    return (
                      <button key={day} type="button" className={`chip day${on ? " active" : ""}`}
                        onClick={() => setDays(on ? days.filter((d) => d !== day) : [...days, day])}>
                        {label}
                      </button>
                    );
                  })}
                </div>
                <span className="field-help">{days.length === 0 ? "Ничего не выбрано — каждый день." : "Только в выбранные дни."}</span>
              </div>
            </>
          )}
          {mode === "once" && (
            <label className="field">
              <span className="field-label">Дата и время<span className="req">*</span></span>
              <input className="input" type="datetime-local" value={at} onChange={(e) => setAt(e.target.value)} style={{ width: 240 }} />
            </label>
          )}
          <label className="check">
            <input type="checkbox" checked={overlap === "skip"} onChange={(e) => setOverlap(e.target.checked ? "skip" : "allow")} />
            Не запускать, пока предыдущий запуск ещё работает
          </label>
        </>
      ) : (
        <>
          <div className="grid-2">
            <label className="field">
              <span className="field-label">Источник<span className="req">*</span></span>
              <input className="input" value={source} list="event-sources" onChange={(e) => setSource(e.target.value)} />
              <datalist id="event-sources">
                {EVENT_SOURCES.map(([value, label]) => <option key={value} value={value}>{label}</option>)}
              </datalist>
            </label>
            <label className="field">
              <span className="field-label">Вид события</span>
              <input className="input" value={eventType} placeholder="любой" onChange={(e) => setEventType(e.target.value)} />
            </label>
          </div>
          <label className="field">
            <span className="field-label">Ключ</span>
            <input className="input" value={correlationKey} placeholder="любой" onChange={(e) => setCorrelationKey(e.target.value)} />
            <span className="field-help">Необязательно. Запуск — только для событий с этим ключом (точное совпадение).</span>
          </label>
          {source.trim().toLowerCase() === "files" && (
            <Callout tone="info">
              Файлы из «Настройки» → «Слежение за папками». Вид: <code>created</code> — новый файл, <code>changed</code> —
              изменённый. Ключ — папка слежения, например <code>входящие</code>. Во входные значения можно передать{" "}
              <code>event.payload.path</code> (путь в рабочей папке) и <code>event.payload.name</code> (имя файла).
            </Callout>
          )}
          <label className="field">
            <span className="field-label">Условие</span>
            <textarea className="textarea mono" style={{ minHeight: 50 }} value={filter} spellCheck={false}
              placeholder="event.payload.total > 100" onChange={(e) => setFilter(e.target.value)} />
            <span className="field-help">Необязательно. Выражение JavaScript от event: запуск — только если получится true.</span>
          </label>
          <Callout tone="info">
            Если событие ждёт уже работающий процесс, событие достанется ему, и новый процесс не запускается. Одно событие
            запускает сценарий не больше одного раза.
          </Callout>
        </>
      )}

      {inputs.length > 0 && (
        <div className="stack" style={{ gap: 8 }}>
          <strong className="small">Входные значения</strong>
          {inputs.map((v) => (
            <label key={v.name} className="field">
              <span className="field-label">{v.name}<span className="faint">· {ru.variableTypes[v.type]}</span></span>
              <input className={`input${kind === "event" ? " mono" : ""}`} value={values[v.name] ?? ""}
                placeholder={kind === "event" ? "event.payload.id" : "значение по умолчанию"}
                onChange={(e) => setValues({ ...values, [v.name]: e.target.value })} />
              {kind === "event" && <span className="field-help">Выражение от event; пусто — значение по умолчанию.</span>}
            </label>
          ))}
        </div>
      )}

      <label className="check">
        <input type="checkbox" checked={enabled} onChange={(e) => setEnabled(e.target.checked)} />
        Включено
      </label>
      {error && <Callout tone="danger">{error}</Callout>}
    </Dialog>
  );
}
