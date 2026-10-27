import { useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { AlertTriangle, Inbox, Lock, LockOpen, RefreshCw, Send, SkipForward, Square, UserCheck } from "lucide-react";
import { api } from "../api/client";
import type { Json, PepaEvent, Resource } from "../api/types";
import { ru } from "../i18n/ru";
import { formatMoment, preview } from "../lib/format";
import { eventTone } from "../lib/status";
import { Dialog, useFeedback } from "./feedback";
import { Badge, Button, Callout, Card, Empty } from "./ui";

export function EventStateBadge({ event }: { event: PepaEvent }) {
  return (
    <span title={ru.eventStateHint[event.state]}>
      <Badge tone={eventTone[event.state]}>{ru.eventState[event.state]}</Badge>
    </span>
  );
}

function useInvalidatePlatform() {
  const client = useQueryClient();
  return () => {
    client.invalidateQueries({ queryKey: ["events"] });
    client.invalidateQueries({ queryKey: ["resources"] });
    client.invalidateQueries({ queryKey: ["processes"] });
    client.invalidateQueries({ queryKey: ["overview"] });
  };
}

function ProcessLink({ id, label }: { id: string; label: string }) {
  return (
    <a href={`#/process/${id}`} onClick={(e) => e.stopPropagation()}>
      {label}
    </a>
  );
}

// ───────────── Конфликты событий ─────────────

/** События, которые одинаково подошли нескольким процессам: Pepa никому их не отдала, решает пользователь. */
export function EventConflicts({ events }: { events: PepaEvent[] }) {
  const feedback = useFeedback();
  const invalidate = useInvalidatePlatform();
  const [busy, setBusy] = useState<string | null>(null);

  const resolve = async (event: PepaEvent, subscriptionId: number | null) => {
    if (subscriptionId === null &&
        !(await feedback.confirm({ title: "Пропустить событие?", text: "Событие не получит ни один процесс. Оно останется в списке событий.", confirmLabel: "Пропустить" }))) {
      return;
    }
    setBusy(`${event.id}:${subscriptionId}`);
    try {
      const result = await api.resolveEvent(event.id, subscriptionId);
      feedback.toast(result.message);
    } catch (e) {
      feedback.error(e);
    } finally {
      setBusy(null);
      invalidate();
    }
  };

  if (events.length === 0) return null;
  return (
    <div className="stack" style={{ padding: 14 }}>
      {events.map((event) => (
        <div key={event.id} className="callout attention">
          <AlertTriangle size={18} className="callout-icon" />
          <div className="stack" style={{ gap: 8, flex: 1, minWidth: 0 }}>
            <div className="row wrap">
              <strong>Событие #{event.id} «{event.source}{event.type ? ` / ${event.type}` : ""}»</strong>
              {event.correlationKey && <span className="faint small">ключ «{event.correlationKey}»</span>}
              <span className="faint small">· {formatMoment(event.receivedAt)}</span>
            </div>
            <div className="small muted">
              Оно одинаково подходит нескольким процессам, поэтому Pepa никому его не отдала. Выберите процесс, которому оно
              предназначено, или пропустите событие.
            </div>
            {event.payload !== null && <div className="small mono truncate" title={preview(event.payload, 2000)}>Данные: {preview(event.payload, 160)}</div>}
            <div className="row wrap">
              {event.candidates.map((c) => (
                <Button key={c.subscriptionId} size="small" icon={UserCheck} variant="primary" disabled={!c.stillWaiting}
                  busy={busy === `${event.id}:${c.subscriptionId}`}
                  title={c.stillWaiting ? "Отдать событие этому процессу" : "Этот процесс уже не ждёт событие"}
                  onClick={() => resolve(event, c.subscriptionId)}>
                  {c.process.scenarioName} #{c.process.shortId}
                </Button>
              ))}
              <Button size="small" icon={SkipForward} busy={busy === `${event.id}:null`} onClick={() => resolve(event, null)}>
                Пропустить
              </Button>
            </div>
          </div>
        </div>
      ))}
    </div>
  );
}

// ───────────── Список событий ─────────────

export function EventsTable({ events }: { events: PepaEvent[] }) {
  const feedback = useFeedback();
  const invalidate = useInvalidatePlatform();
  const [busy, setBusy] = useState<number | null>(null);

  const reroute = async (event: PepaEvent) => {
    setBusy(event.id);
    try {
      feedback.toast((await api.rerouteEvent(event.id)).message, "info");
    } catch (e) {
      feedback.error(e);
    } finally {
      setBusy(null);
      invalidate();
    }
  };

  if (events.length === 0) {
    return (
      <Empty icon={Inbox} title="Событий пока не было">
        События приходят от других процессов, по адресу API или их можно отправить вручную.
      </Empty>
    );
  }

  return (
    <div className="table-wrap">
      <table className="table">
        <thead>
          <tr>
            <th>Состояние</th>
            <th>Событие</th>
            <th>Данные</th>
            <th>Кому</th>
            <th>Пришло</th>
            <th />
          </tr>
        </thead>
        <tbody>
          {events.map((event) => (
            <tr key={event.id}>
              <td><EventStateBadge event={event} /></td>
              <td>
                <div style={{ fontWeight: 560 }}>{event.source}{event.type ? ` / ${event.type}` : ""}</div>
                <div className="faint small">
                  #{event.id}
                  {event.correlationKey ? ` · ключ «${event.correlationKey}»` : ""}
                  {event.externalId ? ` · от повторов «${event.externalId}»` : ""}
                </div>
              </td>
              <td className="mono small" style={{ maxWidth: 220 }}>
                <div className="truncate" title={preview(event.payload, 2000)}>{preview(event.payload, 60)}</div>
              </td>
              <td className="small" style={{ maxWidth: 260 }}>
                {event.process ? (
                  <ProcessLink id={event.process.id} label={`${event.process.scenarioName} #${event.process.shortId}`} />
                ) : event.startedProcesses.length > 0 ? (
                  <span>
                    Запущено:{" "}
                    {event.startedProcesses.map((id, i) => (
                      <span key={id}>{i > 0 && ", "}<ProcessLink id={id} label={`#${id.slice(-6)}`} /></span>
                    ))}
                  </span>
                ) : (
                  <span className="muted truncate" title={event.note ?? undefined}>{event.note ?? "—"}</span>
                )}
              </td>
              <td className="muted small">{formatMoment(event.receivedAt)}</td>
              <td style={{ textAlign: "right" }}>
                {event.state === "unmatched" && (
                  <Button size="small" icon={RefreshCw} busy={busy === event.id} title="Сопоставить ещё раз с процессами, которые ждут сейчас"
                    onClick={() => reroute(event)}>
                    Сопоставить снова
                  </Button>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

// ───────────── Отправка события вручную ─────────────

export function useSendEvent() {
  const [open, setOpen] = useState(false);
  const element = open && <SendEventDialog onClose={() => setOpen(false)} />;
  return { open: () => setOpen(true), element };
}

function SendEventDialog({ onClose }: { onClose: () => void }) {
  const feedback = useFeedback();
  const invalidate = useInvalidatePlatform();
  const [source, setSource] = useState("manual");
  const [type, setType] = useState("reply");
  const [correlationKey, setCorrelationKey] = useState("");
  const [externalId, setExternalId] = useState("");
  const [payload, setPayload] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const submit = async () => {
    if (!source.trim()) {
      setError("Укажите источник события.");
      return;
    }
    let data: Json | null = null;
    const text = payload.trim();
    if (text) {
      try {
        data = JSON.parse(text) as Json;
      } catch {
        data = text; // обычный текст
      }
    }
    setBusy(true);
    setError(null);
    try {
      const result = await api.publishEvent({
        source: source.trim(),
        type: type.trim(),
        correlationKey: correlationKey || null,
        externalId: externalId.trim() || null,
        payload: data,
      });
      feedback.toast(result.duplicate ? `Такое событие уже было (#${result.eventId}) — повтор не создан.` : `Событие #${result.eventId} отправлено.`,
        result.duplicate ? "info" : "success");
      invalidate();
      onClose();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Dialog title="Отправить событие" onClose={onClose} width={560}
      footer={
        <>
          <Button onClick={onClose}>{ru.common.cancel}</Button>
          <Button variant="primary" icon={Send} busy={busy} onClick={submit}>Отправить</Button>
        </>
      }>
      <p className="muted small">
        Событие получит процесс, который ждёт его по правилам блока «Ждать событие» (источник, вид, ключ), — или
        запустится сценарий с запуском по событию. Если подходящих нет, событие просто сохранится.
      </p>
      <div className="grid-2">
        <label className="field">
          <span className="field-label">Источник<span className="req">*</span></span>
          <input className="input" value={source} onChange={(e) => setSource(e.target.value)} />
        </label>
        <label className="field">
          <span className="field-label">Вид события</span>
          <input className="input" value={type} placeholder="любой" onChange={(e) => setType(e.target.value)} />
        </label>
      </div>
      <label className="field">
        <span className="field-label">Ключ сопоставления</span>
        <input className="input" value={correlationKey} placeholder="например, номер заказа" onChange={(e) => setCorrelationKey(e.target.value)} />
        <span className="field-help">Должен точно совпасть с ключом, который ждёт процесс.</span>
      </label>
      <label className="field">
        <span className="field-label">Данные</span>
        <textarea className="textarea mono" value={payload} placeholder={'Текст или JSON, например {"text": "готово"}'} onChange={(e) => setPayload(e.target.value)} />
      </label>
      <label className="field">
        <span className="field-label">Ключ от повторов</span>
        <input className="input" value={externalId} placeholder="необязательно" onChange={(e) => setExternalId(e.target.value)} />
        <span className="field-help">Событие с тем же источником и этим ключом второй раз не создаётся.</span>
      </label>
      {error && <Callout tone="danger">{error}</Callout>}
    </Dialog>
  );
}

// ───────────── Ресурсы ─────────────

export function ResourcesCard({ resources }: { resources: Resource[] }) {
  const feedback = useFeedback();
  const invalidate = useInvalidatePlatform();
  const [busy, setBusy] = useState<string | null>(null);
  const used = resources.filter((r) => r.holder || r.heldBy || r.queue.length > 0);

  // Запись действий в окнах держит мышь и клавиатуру: её можно остановить и отсюда (записанное не добавляется в сценарий).
  const stopRecording = async (resource: Resource) => {
    if (!(await feedback.confirm({
      title: "Остановить запись действий?",
      text: "Записанные шаги не добавятся в сценарий. Процессы, которые ждут мышь и клавиатуру, продолжат работу.",
      confirmLabel: "Остановить запись",
      danger: true,
    }))) {
      return;
    }
    setBusy(resource.key);
    try {
      await api.desktopRecorderStop();
      feedback.toast("Запись остановлена");
    } catch (e) {
      feedback.error(e);
    } finally {
      setBusy(null);
      invalidate();
    }
  };

  const release = async (resource: Resource) => {
    if (!(await feedback.confirm({
      title: `Освободить «${resource.title}»?`,
      text:
        `Ресурс занят процессом «${resource.holder?.scenarioName}» #${resource.holder?.shortId}. Освобождайте вручную, только если ` +
        "уверены, что процесс им больше не пользуется (например, он приостановлен и вы закрыли профиль сами). " +
        (resource.queue.length > 0 ? "Ресурс сразу получит следующий в очереди." : ""),
      confirmLabel: "Освободить",
      danger: true,
    }))) {
      return;
    }
    setBusy(resource.key);
    try {
      feedback.toast((await api.releaseResource(resource.key)).message);
    } catch (e) {
      feedback.error(e);
    } finally {
      setBusy(null);
      invalidate();
    }
  };

  return (
    <Card title={`Ресурсы · занято ${resources.filter((r) => r.holder || r.heldBy).length}`}>
      {used.length === 0 ? (
        <Empty icon={LockOpen} title="Все ресурсы свободны">
          Процесс занимает ресурс блоком «Занять ресурс» — например, профиль браузера — и другие процессы ждут своей очереди.
        </Empty>
      ) : (
        <div className="table-wrap">
          <table className="table">
            <thead>
              <tr>
                <th>Ресурс</th>
                <th>Занят процессом</th>
                <th>С</th>
                <th>Очередь</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {used.map((r) => (
                <tr key={r.key}>
                  <td>
                    <div className="row" style={{ gap: 6, fontWeight: 560 }}>
                      {r.holder || r.heldBy ? <Lock size={14} /> : <LockOpen size={14} />} {r.title}
                    </div>
                    {r.title !== r.key && <div className="faint small mono">{r.key}</div>}
                  </td>
                  <td className="small">
                    {r.holder ? (
                      <>
                        <ProcessLink id={r.holder.id} label={`${r.holder.scenarioName} #${r.holder.shortId}`} />
                        <div className="faint">{ru.processStatus[r.holder.status]}</div>
                      </>
                    ) : r.heldBy ? (
                      <span>{r.heldBy} <span className="faint">— освободится, когда она закончится</span></span>
                    ) : (
                      <span className="muted">свободен</span>
                    )}
                  </td>
                  <td className="muted small">{formatMoment(r.acquiredAt)}</td>
                  <td className="small">
                    {r.queue.length === 0
                      ? <span className="muted">—</span>
                      : r.queue.map((w, i) => (
                          <div key={w.process.id}>
                            {i + 1}. <ProcessLink id={w.process.id} label={`${w.process.scenarioName} #${w.process.shortId}`} />
                            {w.process.status !== "waiting" && <span className="faint"> · {ru.processStatus[w.process.status].toLowerCase()}, пропускается</span>}
                          </div>
                        ))}
                  </td>
                  <td style={{ textAlign: "right" }}>
                    {r.holder && (
                      <Button size="small" icon={LockOpen} busy={busy === r.key} onClick={() => release(r)}>
                        Освободить
                      </Button>
                    )}
                    {!r.holder && r.heldBy && r.key === "desktop:input" && (
                      <Button size="small" icon={Square} busy={busy === r.key} onClick={() => void stopRecording(r)}>
                        Остановить запись
                      </Button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Card>
  );
}
