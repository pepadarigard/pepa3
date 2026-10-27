import { useEffect, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { FolderSearch, Plus, Save, Trash2 } from "lucide-react";
import { api } from "../api/client";
import type { FolderWatch } from "../api/types";
import { formatMoment } from "../lib/format";
import { useFeedback } from "./feedback";
import { Badge, Button, Callout, Card, Spinner } from "./ui";

const blank = (): FolderWatch => ({ id: "", folder: "", pattern: "*", subfolders: false, changes: false, enabled: true });

/** Слежение за папками рабочей папки: новые файлы становятся событиями, по ним запускаются сценарии и продолжаются ожидания. */
export function WatchesCard() {
  const client = useQueryClient();
  const feedback = useFeedback();
  // Время проверки меняется каждые пару секунд — без отдельного оповещения, поэтому — периодически.
  const watches = useQuery({ queryKey: ["watches"], queryFn: api.watches, refetchInterval: 5000 });
  const [rows, setRows] = useState<FolderWatch[] | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (watches.data && rows === null) setRows(watches.data.watches.map(({ status: _, ...w }) => w));
  }, [watches.data, rows]);

  const saved = watches.data?.watches.map(({ status: _, ...w }) => w) ?? [];
  const dirty = rows !== null && JSON.stringify(rows) !== JSON.stringify(saved);
  const update = (i: number, patch: Partial<FolderWatch>) => setRows((r) => r!.map((w, j) => (j === i ? { ...w, ...patch } : w)));

  const save = async () => {
    setBusy(true);
    setError(null);
    try {
      const result = await api.saveWatches(rows!);
      client.setQueryData(["watches"], result);
      setRows(result.watches.map(({ status: _, ...w }) => w));
      feedback.toast("Слежение за папками сохранено", "success");
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Card title={<h2 className="row"><FolderSearch size={16} /> Слежение за папками</h2>}>
      <div className="card-body stack" style={{ gap: 10 }} data-testid="watches-card">
        <p className="small muted">
          Новый файл в папке рабочей папки становится событием (источник <code>files</code>, вид <code>created</code>, ключ — папка),
          когда он дописан. По нему можно запустить сценарий («Автозапуск» → «По событию») или продолжить процесс, который ждёт
          его блоком «Дождаться файла». Что уже лежит в папке, когда слежение включили, — не событие. Файлы, появившиеся, пока
          Pepa была закрыта, ожидающие процессы получат, но сценарии по ним сами не запустятся — их видно в «Выполнение» → «События».
        </p>
        {watches.isLoading && <Spinner />}
        {watches.error && <Callout tone="danger">{watches.error instanceof Error ? watches.error.message : String(watches.error)}</Callout>}
        {error && <Callout tone="danger">{error}</Callout>}
        {rows && (
          <>
            {rows.length === 0 && <p className="small faint">Pepa ни за одной папкой не следит.</p>}
            {rows.map((w, i) => {
              const status = watches.data?.watches.find((x) => x.id === w.id && w.id !== "")?.status ?? null;
              return (
                <div key={i} className="watch-row stack" style={{ gap: 6 }} data-testid="watch-row">
                  <div className="row wrap" style={{ gap: 8 }}>
                    <label className="field" style={{ flex: "2 1 200px" }}>
                      <span className="field-label">Папка в рабочей папке</span>
                      <input className="input mono" value={w.folder} placeholder="входящие" onChange={(e) => update(i, { folder: e.target.value })} />
                    </label>
                    <label className="field" style={{ flex: "1 1 140px" }}>
                      <span className="field-label">Какие файлы</span>
                      <input className="input mono" value={w.pattern} placeholder="*.pdf; *.xlsx" onChange={(e) => update(i, { pattern: e.target.value })} />
                    </label>
                    <Button size="small" variant="ghost" icon={Trash2} onClick={() => setRows(rows.filter((_, j) => j !== i))} title="Убрать">
                      Убрать
                    </Button>
                  </div>
                  <div className="row wrap" style={{ gap: 16 }}>
                    <label className="check">
                      <input type="checkbox" checked={w.enabled} onChange={(e) => update(i, { enabled: e.target.checked })} /> Включено
                    </label>
                    <label className="check">
                      <input type="checkbox" checked={w.subfolders} onChange={(e) => update(i, { subfolders: e.target.checked })} /> И вложенные папки
                    </label>
                    <label className="check">
                      <input type="checkbox" checked={w.changes} onChange={(e) => update(i, { changes: e.target.checked })} /> Также изменённые файлы
                    </label>
                  </div>
                  {status && (
                    <div className="row wrap small" style={{ gap: 8 }}>
                      {status.error ? <Badge tone="danger">ошибка</Badge> : <Badge tone="success">следит</Badge>}
                      <span className="muted">файлов: {status.files}</span>
                      {status.pending > 0 && <span className="muted">дописываются: {status.pending}</span>}
                      {status.lastScanAt && <span className="faint">проверено {formatMoment(status.lastScanAt)}</span>}
                    </div>
                  )}
                  {status?.error && <Callout tone="danger">{status.error}</Callout>}
                  {status?.note && <Callout tone="attention">{status.note}</Callout>}
                </div>
              );
            })}
            <div className="row">
              <Button size="small" icon={Plus} onClick={() => setRows([...rows, blank()])}>Добавить папку</Button>
              <Button size="small" variant="primary" icon={Save} busy={busy} disabled={!dirty} onClick={() => void save()}>Сохранить слежение</Button>
            </div>
            <p className="small faint">
              Шаблон: «*» — любые символы, «?» — один символ, несколько — через «;». Временные файлы (скачиваются, открыты в Office)
              и скрытые не учитываются. Папки проверяются каждые 2 секунды.
            </p>
          </>
        )}
      </div>
    </Card>
  );
}
