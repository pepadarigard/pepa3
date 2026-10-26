import { useMemo, useState } from "react";
import { BookOpenText, PencilLine, RotateCcw, Sparkles } from "lucide-react";
import { api } from "../../api/client";
import { useAssistant, useDescriptors } from "../../api/hooks";
import type { AssistantDraft, AssistantExplanation, ScenarioDefinition } from "../../api/types";
import { Dialog } from "../../components/feedback";
import { Button, Callout, Spinner } from "../../components/ui";
import { draftChanges, draftToBlocks, hasChanges } from "../../lib/assistant";
import type { BlockClipboard } from "../../lib/clipboard";
import { navigate } from "../../router";

type Mode = "new" | "edit" | "explain";

const DRAFT_KEY = "pepa.assistant.description";
const EDIT_KEY = "pepa.assistant.edit";

const example =
  "Открыть профиль AdsPower (ID спросить при запуске), зайти на example.com/login, нажать «Получить код», написать в Telegram " +
  "@ivan «Код запрошен» и ждать от него ответ до 10 минут. Код из ответа ввести на сайте в поле «Код» и нажать «Войти». " +
  "Если ответа нет — закончить с ошибкой «Код не пришёл». В конце закрыть профиль.";

const editExample = "Перед отправкой сообщения подождать 5 секунд, а если ответа нет — написать в журнал «Ответа нет» и закончить с ошибкой.";

function load(key: string): string {
  try {
    return sessionStorage.getItem(key) ?? "";
  } catch {
    return "";
  }
}

function store(key: string, text: string) {
  try {
    sessionStorage.setItem(key, text);
  } catch {
    // Черновик описания — только удобство.
  }
}

type Usage = AssistantDraft["usage"];

function UsageLine({ usage, requests }: { usage: Usage; requests?: number }) {
  return (
    <p className="small faint">
      {requests !== undefined ? `Запросов к модели: ${requests} · ` : ""}токенов: {usage.inputTokens.toLocaleString("ru-RU")} на входе
      {usage.cacheReadTokens > 0 ? ` (+${usage.cacheReadTokens.toLocaleString("ru-RU")} из кеша)` : ""},
      {" "}{usage.outputTokens.toLocaleString("ru-RU")} на выходе.
    </p>
  );
}

/**
 * ИИ-помощник: новые блоки по описанию, правка сценария по просьбе или объяснение сценария. Ответ показывается целиком
 * (что сделано, какие блоки, что уточнить, какие ошибки остались) и попадает в схему только по кнопке — правку можно
 * отменить (Ctrl+Z), как любое изменение.
 */
export function AssistantDialog({ scenario, startId, onInsert, onApply, onSelectNode, onClose }: {
  scenario: ScenarioDefinition;
  startId: string | null;
  onInsert: (blocks: BlockClipboard, entryId: string | null) => void;
  onApply: (draft: AssistantDraft) => void;
  onSelectNode: (nodeId: string) => void;
  onClose: () => void;
}) {
  const status = useAssistant();
  const descriptors = useDescriptors();
  const [mode, setMode] = useState<Mode>("new");
  const [description, setDescription] = useState(() => load(DRAFT_KEY));
  const [request, setRequest] = useState(() => load(EDIT_KEY));
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [draft, setDraft] = useState<AssistantDraft | null>(null);
  const [explanation, setExplanation] = useState<AssistantExplanation | null>(null);

  const blocks = scenario.nodes.filter((n) => n.id !== startId);
  const titleOf = (nodeId: string) => {
    const node = scenario.nodes.find((n) => n.id === nodeId);
    return node ? node.label || descriptors.get(node.type)?.title || node.type : nodeId;
  };
  const changes = useMemo(
    () => (draft && mode === "edit" ? draftChanges(scenario.nodes, scenario.edges, draft, startId) : null),
    [draft, mode, scenario, startId],
  );

  const reset = () => {
    setDraft(null);
    setExplanation(null);
    setError(null);
  };

  const ask = async () => {
    setBusy(true);
    reset();
    try {
      if (mode === "explain") setExplanation(await api.assistantExplain(scenario));
      else if (mode === "edit") setDraft(await api.assistantDraft(request, scenario.variables, scenario));
      else setDraft(await api.assistantDraft(description, scenario.variables));
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  const configured = status.data?.configured === true;
  const answered = draft !== null || explanation !== null;
  const canAsk = configured && (mode === "explain" ? blocks.length > 0 : (mode === "edit" ? request : description).trim().length > 0 &&
    (mode !== "edit" || blocks.length > 0));

  const primary = !answered ? (
    <Button variant="primary" icon={mode === "explain" ? BookOpenText : Sparkles} busy={busy} disabled={!canAsk} onClick={() => void ask()}>
      {mode === "explain" ? "Объяснить" : mode === "edit" ? "Предложить изменения" : "Составить блоки"}
    </Button>
  ) : draft && mode === "new" ? (
    <Button variant="primary" onClick={() => { onInsert(draftToBlocks(draft), draft.entryNodeId); onClose(); }} disabled={draft.nodes.length === 0}>
      Вставить в схему
    </Button>
  ) : draft && mode === "edit" ? (
    <Button variant="primary" onClick={() => { onApply(draft); onClose(); }} disabled={!changes || !hasChanges(changes) || draft.nodes.length === 0}>
      Применить изменения
    </Button>
  ) : null;

  return (
    <Dialog
      title={<span className="row"><Sparkles size={18} /> ИИ-помощник</span>}
      width={720}
      onClose={() => (busy ? undefined : onClose())}
      footer={
        <>
          {answered && mode !== "explain" && <Button icon={RotateCcw} onClick={reset}>Изменить описание</Button>}
          {answered && mode === "explain" && <Button icon={RotateCcw} onClick={reset}>Объяснить заново</Button>}
          <Button onClick={onClose} disabled={busy}>Закрыть</Button>
          {primary}
        </>
      }
    >
      {status.isLoading && <Spinner />}
      {status.data && !configured && (
        <Callout tone="attention" title="Помощник не подключён"
          actions={<Button size="small" onClick={() => { onClose(); navigate({ page: "settings" }); }}>Открыть настройки</Button>}>
          Укажите ключ API Anthropic в «Настройках» → «ИИ-помощник». Без помощника всё работает: блоки можно добавлять вручную
          или записать кнопкой «Записать».
        </Callout>
      )}

      {!answered && (
        <div className="segmented" style={{ alignSelf: "flex-start", marginBottom: 10 }}>
          {([["new", "Новые блоки", Sparkles], ["edit", "Изменить сценарий", PencilLine], ["explain", "Объяснить сценарий", BookOpenText]] as const).map(
            ([value, label, Icon]) => (
              <button key={value} type="button" className={mode === value ? "active" : ""} disabled={busy}
                onClick={() => { setMode(value); setError(null); }}>
                <Icon size={14} /> {label}
              </button>
            ))}
        </div>
      )}
      {error && <Callout tone="danger">{error}</Callout>}

      {!answered && mode === "new" && (
        <div className="stack" style={{ gap: 10 }}>
          <p className="small muted">
            Опишите обычными словами, что должен делать сценарий: где, что нажать или ввести, кому написать, чего ждать и что
            делать, если что-то пошло не так. Помощник предложит блоки — вы проверите их перед сохранением. Ничего не запускается.
          </p>
          <textarea className="textarea" style={{ minHeight: 160 }} value={description} placeholder={example} disabled={busy}
            onChange={(e) => {
              setDescription(e.target.value);
              store(DRAFT_KEY, e.target.value);
            }} />
          {description.trim().length === 0 && (
            <button type="button" className="link-button small" style={{ alignSelf: "flex-start" }} onClick={() => setDescription(example)}>
              Подставить пример
            </button>
          )}
        </div>
      )}

      {!answered && mode === "edit" && (
        <div className="stack" style={{ gap: 10 }}>
          <p className="small muted">
            Напишите, что изменить в этом сценарии. Помощник вернёт сценарий целиком; перед применением вы увидите, какие блоки
            добавятся, удалятся и изменятся. Применённую правку можно отменить (Ctrl+Z).
          </p>
          <textarea className="textarea" style={{ minHeight: 120 }} value={request} placeholder={editExample} disabled={busy}
            aria-label="Что изменить"
            onChange={(e) => {
              setRequest(e.target.value);
              store(EDIT_KEY, e.target.value);
            }} />
        </div>
      )}

      {!answered && mode === "explain" && (
        <p className="small muted">
          Помощник расскажет, что делает этот сценарий, по шагам, и что в нём стоит проверить. Сценарий не меняется.
        </p>
      )}

      {!answered && mode !== "new" && (
        <p className="small faint">
          {blocks.length === 0
            ? "В сценарии пока нет блоков — сначала добавьте их или составьте новыми блоками."
            : "Помощнику уйдут блоки сценария с их настройками (тексты, адреса, имена чатов), имена и описания переменных — без " +
              "значений переменных и без данных процессов."}
        </p>
      )}

      {busy && (
        <div className="row small muted" style={{ marginTop: 10 }}>
          <Spinner /> {mode === "explain" ? "Помощник разбирает сценарий…" : "Помощник составляет блоки и проверяет их — обычно это занимает до минуты-двух."}
        </div>
      )}

      {draft && (
        <div className="stack" style={{ gap: 12 }}>
          {draft.summary && <p>{draft.summary}</p>}
          {draft.problems.length > 0 && (
            <Callout tone="attention" title="Проверка нашла ошибки, которые помощник не исправил">
              {mode === "edit"
                ? "Правку можно применить и поправить вручную — те же ошибки покажет проверка сценария."
                : "Черновик можно вставить и поправить вручную — те же ошибки покажет проверка сценария."}
              <ul className="assistant-list">{draft.problems.map((p, i) => <li key={i}>{p}</li>)}</ul>
            </Callout>
          )}
          {draft.questions.length > 0 && (
            <Callout tone="info" title="Уточните и проверьте">
              <ul className="assistant-list">{draft.questions.map((q, i) => <li key={i}>{q}</li>)}</ul>
            </Callout>
          )}
          {mode === "new" && (
            <div>
              <div className="field-label">Блоки ({draft.nodes.length})</div>
              <ol className="recorded-list">
                {draft.nodes.map((n) => (
                  <li key={n.id}>
                    <span className="recorded-kind">{descriptors.get(n.type)?.title ?? n.type}</span>
                    <span className="truncate">{n.label ?? ""}</span>
                    {n.note && <span className="small" style={{ color: "var(--warn)" }}>{n.note}</span>}
                  </li>
                ))}
              </ol>
            </div>
          )}
          {mode === "edit" && changes && (
            <div className="stack" style={{ gap: 8 }} data-testid="assistant-changes">
              {!hasChanges(changes) && <Callout tone="info">Помощник ничего не изменил. Уточните просьбу.</Callout>}
              {changes.added.length > 0 && (
                <div>
                  <div className="field-label">Добавятся ({changes.added.length})</div>
                  <ul className="assistant-list">
                    {changes.added.map((n) => <li key={n.id}><strong>{descriptors.get(n.type)?.title ?? n.type}</strong>{n.label ? ` — ${n.label}` : ""}</li>)}
                  </ul>
                </div>
              )}
              {changes.changed.length > 0 && (
                <div>
                  <div className="field-label">Изменятся ({changes.changed.length})</div>
                  <ul className="assistant-list">
                    {changes.changed.map(({ node, what }) => (
                      <li key={node.id}>
                        <strong>{titleOf(node.id)}</strong>: {what.map((w) => descriptors.get(node.type)?.fields.find((f) => f.name === w)?.label.toLowerCase() ?? w).join(", ")}
                      </li>
                    ))}
                  </ul>
                </div>
              )}
              {changes.removed.length > 0 && (
                <div>
                  <div className="field-label" style={{ color: "var(--danger)" }}>Удалятся ({changes.removed.length})</div>
                  <ul className="assistant-list">
                    {changes.removed.map((n) => <li key={n.id}><strong>{titleOf(n.id)}</strong></li>)}
                  </ul>
                </div>
              )}
              {(changes.linksAdded > 0 || changes.linksRemoved > 0) && (
                <p className="small muted">Связи: новых — {changes.linksAdded}, убранных — {changes.linksRemoved}.</p>
              )}
            </div>
          )}
          {draft.variables.length > 0 && (
            <p className="small muted">
              Новые переменные: {draft.variables.map((v) => `${v.name}${v.isInput ? " (спрашивается при запуске)" : ""}`).join(", ")}.
            </p>
          )}
          <UsageLine usage={draft.usage} requests={draft.requests} />
        </div>
      )}

      {explanation && (
        <div className="stack" style={{ gap: 12 }} data-testid="assistant-explanation">
          {explanation.summary && <p>{explanation.summary}</p>}
          {explanation.steps.length > 0 && (
            <ol className="assistant-steps">
              {explanation.steps.map((step, i) => (
                <li key={i}>
                  {step.blockId ? (
                    <button type="button" className="link-button" title="Показать блок на схеме"
                      onClick={() => { onSelectNode(step.blockId!); onClose(); }}>
                      {titleOf(step.blockId)}
                    </button>
                  ) : null}
                  {step.blockId ? " — " : ""}{step.text}
                </li>
              ))}
            </ol>
          )}
          {explanation.cautions.length > 0 && (
            <Callout tone="attention" title="Что стоит проверить">
              <ul className="assistant-list">{explanation.cautions.map((c, i) => <li key={i}>{c}</li>)}</ul>
            </Callout>
          )}
          <UsageLine usage={explanation.usage} />
        </div>
      )}
    </Dialog>
  );
}
