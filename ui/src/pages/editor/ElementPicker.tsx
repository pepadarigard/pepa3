import { useEffect, useMemo, useState } from "react";
import { ScanSearch } from "lucide-react";
import { api } from "../../api/client";
import type { DesktopElement, DesktopElements } from "../../api/types";
import { Dialog } from "../../components/feedback";
import { Badge, Button, Callout, Spinner } from "../../components/ui";
import { canPick, elementReliability } from "../../lib/desktop";

/**
 * Элементы открытого окна — выбрать, вместо того чтобы угадывать подпись. Окно ищется так же, как в блоке; показываются
 * подсказки, по чему элемент найдётся однозначно.
 */
export function ElementPicker({ window: title, process, typeLabels, onPick, onClose }: {
  window: string;
  process: string;
  typeLabels: Map<string, string>;
  onPick: (element: DesktopElement) => void;
  onClose: () => void;
}) {
  const [state, setState] = useState<{ loading: boolean; data: DesktopElements | null; error: string | null }>({ loading: true, data: null, error: null });
  const [filter, setFilter] = useState("");
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    let cancelled = false;
    setState({ loading: true, data: null, error: null });
    api.desktopElements(title, process).then(
      (data) => !cancelled && setState({ loading: false, data, error: null }),
      (e) => !cancelled && setState({ loading: false, data: null, error: e instanceof Error ? e.message : String(e) }),
    );
    return () => {
      cancelled = true;
    };
  }, [title, process, attempt]);

  const shown = useMemo(() => {
    const q = filter.trim().toLowerCase();
    const all = state.data?.elements ?? [];
    return q ? all.filter((e) => `${e.name} ${e.automationId} ${e.controlType} ${typeLabels.get(e.controlType) ?? ""}`.toLowerCase().includes(q)) : all;
  }, [filter, state.data, typeLabels]);

  return (
    <Dialog
      title={<span className="row"><ScanSearch size={18} /> Элементы окна</span>}
      width={760}
      onClose={onClose}
      footer={
        <>
          <Button onClick={() => setAttempt((a) => a + 1)} disabled={state.loading}>Обновить</Button>
          <Button onClick={onClose}>Закрыть</Button>
        </>
      }
    >
      {state.loading && <div className="row small muted"><Spinner /> Pepa читает элементы окна…</div>}
      {state.error && <Callout tone="danger">{state.error}</Callout>}
      {state.data && (
        <div className="stack" style={{ gap: 10 }} data-testid="element-picker">
          <p className="small muted">
            Окно «{state.data.window}» · {state.data.process}. Выберите элемент — подпись, код и тип подставятся в блок. Надёжнее всего
            элементы с уникальным кодом или подписью.
          </p>
          {state.data.truncated && (
            <Callout tone="attention">Элементов очень много — показаны не все. Найдите нужный по подписи в поиске ниже.</Callout>
          )}
          <input className="input" autoFocus placeholder="Найти: подпись, код, тип…" value={filter} onChange={(e) => setFilter(e.target.value)} />
          {shown.length === 0 && <p className="small faint">Ничего не найдено.</p>}
          <ul className="element-list">
            {shown.map((element, i) => {
              const reliability = elementReliability(element);
              return (
                <li key={i}>
                  <button type="button" className={element.enabled ? "" : "disabled-element"} style={{ paddingLeft: 8 + Math.min(element.depth - 1, 10) * 14 }}
                    disabled={!canPick(element)}
                    title={canPick(element) ? undefined : "Без подписи и кода блок этот элемент не найдёт — используйте «Мышь в точке окна»."}
                    onClick={() => { onPick(element); onClose(); }}>
                    <span className="recorded-kind">{typeLabels.get(element.controlType) ?? element.controlType}</span>
                    <span className="truncate">{element.name || <span className="faint">(без подписи)</span>}</span>
                    {element.automationId && <code className="small">{element.automationId}</code>}
                    <span className="spacer" />
                    {reliability === "id" && <Badge tone="success" dot={false}>уникальный код</Badge>}
                    {reliability === "name" && <Badge tone="info" dot={false}>уникальная подпись</Badge>}
                    {reliability === "weak" && <Badge tone="warn" dot={false}>не уникален</Badge>}
                  </button>
                </li>
              );
            })}
          </ul>
        </div>
      )}
    </Dialog>
  );
}
