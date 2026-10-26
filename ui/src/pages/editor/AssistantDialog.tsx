import { useState } from "react";
import { RotateCcw, Sparkles } from "lucide-react";
import { api } from "../../api/client";
import { useAssistant, useDescriptors } from "../../api/hooks";
import type { AssistantDraft, VariableDefinition } from "../../api/types";
import { Dialog } from "../../components/feedback";
import { Button, Callout, Spinner } from "../../components/ui";
import { draftToBlocks } from "../../lib/assistant";
import type { BlockClipboard } from "../../lib/clipboard";
import { navigate } from "../../router";

const DRAFT_KEY = "pepa.assistant.description";

const example =
  "Открыть профиль AdsPower (ID спросить при запуске), зайти на example.com/login, нажать «Получить код», написать в Telegram " +
  "@ivan «Код запрошен» и ждать от него ответ до 10 минут. Код из ответа ввести на сайте в поле «Код» и нажать «Войти». " +
  "Если ответа нет — закончить с ошибкой «Код не пришёл». В конце закрыть профиль.";

function loadDescription(): string {
  try {
    return sessionStorage.getItem(DRAFT_KEY) ?? "";
  } catch {
    return "";
  }
}

function saveDescription(text: string) {
  try {
    sessionStorage.setItem(DRAFT_KEY, text);
  } catch {
    // Черновик описания — только удобство.
  }
}

/**
 * ИИ-помощник: описание словами → черновик блоков. Черновик показывается целиком (что делает, какие блоки, что уточнить,
 * какие ошибки остались) и вставляется в схему только по кнопке — дальше как обычные блоки.
 */
export function AssistantDialog({ variables, onInsert, onClose }: {
  variables: VariableDefinition[];
  onInsert: (blocks: BlockClipboard, entryId: string | null) => void;
  onClose: () => void;
}) {
  const status = useAssistant();
  const descriptors = useDescriptors();
  const [description, setDescription] = useState(loadDescription);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [draft, setDraft] = useState<AssistantDraft | null>(null);

  const compose = async () => {
    setBusy(true);
    setError(null);
    setDraft(null);
    try {
      setDraft(await api.assistantDraft(description, variables));
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  const insert = () => {
    if (!draft) return;
    onInsert(draftToBlocks(draft), draft.entryNodeId);
    onClose();
  };

  const configured = status.data?.configured === true;
  return (
    <Dialog
      title={<span className="row"><Sparkles size={18} /> ИИ-помощник</span>}
      width={720}
      onClose={() => (busy ? undefined : onClose())}
      footer={
        draft ? (
          <>
            <Button icon={RotateCcw} onClick={() => setDraft(null)}>Изменить описание</Button>
            <Button onClick={onClose}>Закрыть</Button>
            <Button variant="primary" onClick={insert} disabled={draft.nodes.length === 0}>Вставить в схему</Button>
          </>
        ) : (
          <>
            <Button onClick={onClose} disabled={busy}>Закрыть</Button>
            <Button variant="primary" icon={Sparkles} busy={busy} disabled={!configured || description.trim().length === 0} onClick={() => void compose()}>
              Составить блоки
            </Button>
          </>
        )
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
      {error && <Callout tone="danger">{error}</Callout>}

      {!draft && (
        <div className="stack" style={{ gap: 10 }}>
          <p className="small muted">
            Опишите обычными словами, что должен делать сценарий: где, что нажать или ввести, кому написать, чего ждать и что
            делать, если что-то пошло не так. Помощник предложит блоки — вы проверите их перед сохранением. Ничего не запускается.
          </p>
          <textarea
            className="textarea"
            style={{ minHeight: 160 }}
            value={description}
            placeholder={example}
            disabled={busy}
            onChange={(e) => {
              setDescription(e.target.value);
              saveDescription(e.target.value);
            }}
          />
          {description.trim().length === 0 && (
            <button type="button" className="link-button small" style={{ alignSelf: "flex-start" }} onClick={() => setDescription(example)}>
              Подставить пример
            </button>
          )}
          {busy && (
            <div className="row small muted"><Spinner /> Помощник составляет блоки и проверяет их — обычно это занимает до минуты-двух.</div>
          )}
        </div>
      )}

      {draft && (
        <div className="stack" style={{ gap: 12 }}>
          {draft.summary && <p>{draft.summary}</p>}
          {draft.problems.length > 0 && (
            <Callout tone="attention" title="Проверка нашла ошибки, которые помощник не исправил">
              Черновик можно вставить и поправить вручную — те же ошибки покажет проверка сценария.
              <ul className="assistant-list">{draft.problems.map((p, i) => <li key={i}>{p}</li>)}</ul>
            </Callout>
          )}
          {draft.questions.length > 0 && (
            <Callout tone="info" title="Уточните и проверьте">
              <ul className="assistant-list">{draft.questions.map((q, i) => <li key={i}>{q}</li>)}</ul>
            </Callout>
          )}
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
          {draft.variables.length > 0 && (
            <p className="small muted">
              Новые переменные: {draft.variables.map((v) => `${v.name}${v.isInput ? " (спрашивается при запуске)" : ""}`).join(", ")}.
            </p>
          )}
          <p className="small faint">
            Запросов к модели: {draft.requests} · токенов: {draft.usage.inputTokens.toLocaleString("ru-RU")} на входе
            {draft.usage.cacheReadTokens > 0 ? ` (+${draft.usage.cacheReadTokens.toLocaleString("ru-RU")} из кеша)` : ""},
            {" "}{draft.usage.outputTokens.toLocaleString("ru-RU")} на выходе.
          </p>
        </div>
      )}
    </Dialog>
  );
}
