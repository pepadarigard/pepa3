import { useMemo, useRef, useState } from "react";
import { AlertTriangle, CopyPlus, Download, Ellipsis, History, Pencil, Play, Plus, Search, Trash2, Upload, Workflow } from "lucide-react";
import { useQueryClient } from "@tanstack/react-query";
import { api } from "../api/client";
import { useScenarios } from "../api/hooks";
import type { ScenarioDefinition, ScenarioSummary } from "../api/types";
import { useFeedback } from "../components/feedback";
import { ProcessStatusBadge } from "../components/process";
import { useRunScenario } from "../components/RunDialog";
import { Badge, Button, Empty, ErrorState, Loading } from "../components/ui";
import { formatMoment, plural } from "../lib/format";
import { navigate } from "../router";

export function blankScenario(name = "Новый сценарий"): Partial<ScenarioDefinition> {
  return {
    schemaVersion: 1,
    name,
    description: "",
    variables: [],
    nodes: [
      { id: "start", type: "start", label: null, position: { x: 0, y: 80 }, config: {}, settings: { retryCount: 0, retryDelaySeconds: 5, timeoutSeconds: null }, note: null },
      { id: "end", type: "end", label: null, position: { x: 420, y: 80 }, config: { outcome: "success" }, settings: { retryCount: 0, retryDelaySeconds: 5, timeoutSeconds: null }, note: null },
    ],
    edges: [{ id: "start-out-end", source: "start", sourcePort: "out", target: "end" }],
  };
}

export function ScenariosPage() {
  const scenarios = useScenarios();
  const feedback = useFeedback();
  const queryClient = useQueryClient();
  const runner = useRunScenario();
  const [query, setQuery] = useState("");
  const fileInput = useRef<HTMLInputElement>(null);

  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase();
    return (scenarios.data ?? []).filter(
      (s) => !q || s.name.toLowerCase().includes(q) || s.description.toLowerCase().includes(q),
    );
  }, [scenarios.data, query]);

  const create = async () => {
    try {
      const created = await api.createScenario(blankScenario());
      queryClient.invalidateQueries({ queryKey: ["scenarios"] });
      navigate({ page: "editor", id: created.id });
    } catch (e) {
      feedback.error(e);
    }
  };

  const importFile = async (file: File) => {
    try {
      const text = await file.text();
      const definition = JSON.parse(text) as ScenarioDefinition;
      if (!definition || !Array.isArray(definition.nodes)) throw new Error("Файл не похож на сценарий Pepa.");
      const created = await api.createScenario({ ...definition, id: "" });
      queryClient.invalidateQueries({ queryKey: ["scenarios"] });
      feedback.toast(`Сценарий «${created.name}» импортирован`);
      navigate({ page: "editor", id: created.id });
    } catch (e) {
      feedback.error(e instanceof SyntaxError ? new Error("Файл повреждён: это не JSON.") : e);
    }
  };

  return (
    <div className="page">
      <header className="page-header">
        <div>
          <h1>Сценарии</h1>
          <p className="subtitle">Постоянные описания автоматизаций. Каждый запуск сценария — отдельный процесс.</p>
        </div>
        <div className="row">
          <Button icon={Upload} onClick={() => fileInput.current?.click()}>Импорт</Button>
          <Button variant="primary" icon={Plus} onClick={create}>Новый сценарий</Button>
          <input ref={fileInput} type="file" accept=".json,application/json" hidden
            onChange={(e) => { const f = e.target.files?.[0]; if (f) void importFile(f); e.target.value = ""; }} />
        </div>
      </header>

      <div className="row">
        <div style={{ position: "relative", width: 320 }}>
          <Search size={15} style={{ position: "absolute", left: 10, top: 10, color: "var(--faint)" }} />
          <input className="input" style={{ paddingLeft: 32 }} placeholder="Поиск по названию и описанию" value={query} onChange={(e) => setQuery(e.target.value)} />
        </div>
        <span className="muted small">
          {filtered.length} {plural(filtered.length, "сценарий", "сценария", "сценариев")}
        </span>
      </div>

      {scenarios.isLoading ? (
        <Loading />
      ) : scenarios.error ? (
        <ErrorState error={scenarios.error} onRetry={() => scenarios.refetch()} />
      ) : filtered.length === 0 ? (
        <div className="card"><Empty icon={Workflow} title={query ? "Ничего не найдено" : "Сценариев пока нет"}>{!query && "Нажмите «Новый сценарий» или импортируйте файл."}</Empty></div>
      ) : (
        <div className="scenario-grid">
          {filtered.map((s) => (
            <ScenarioCard key={s.id} scenario={s} onRun={() => runner.run(s.id)} />
          ))}
        </div>
      )}
      {runner.element}
    </div>
  );
}

function ScenarioCard({ scenario: s, onRun }: { scenario: ScenarioSummary; onRun: () => void }) {
  const feedback = useFeedback();
  const queryClient = useQueryClient();
  const [menu, setMenu] = useState(false);

  const duplicate = async () => {
    setMenu(false);
    try {
      const copy = await api.duplicateScenario(s.id);
      queryClient.invalidateQueries({ queryKey: ["scenarios"] });
      feedback.toast(`Создана копия «${copy.name}»`);
    } catch (e) {
      feedback.error(e);
    }
  };

  const exportFile = async () => {
    setMenu(false);
    try {
      const scenario = await api.scenario(s.id);
      const blob = new Blob([JSON.stringify(scenario.definition, null, 2)], { type: "application/json" });
      const url = URL.createObjectURL(blob);
      const link = document.createElement("a");
      link.href = url;
      link.download = `${s.name.replace(/[\\/:*?"<>|]+/g, "_")}.pepa.json`;
      link.click();
      setTimeout(() => URL.revokeObjectURL(url), 1000);
    } catch (e) {
      feedback.error(e);
    }
  };

  const remove = async () => {
    setMenu(false);
    if (!(await feedback.confirm({ title: "Удалить сценарий?", text: `«${s.name}» будет удалён. История уже выполненных запусков сохранится.`, confirmLabel: "Удалить", danger: true }))) return;
    try {
      await api.deleteScenario(s.id);
      queryClient.invalidateQueries({ queryKey: ["scenarios"] });
      feedback.toast("Сценарий удалён");
    } catch (e) {
      feedback.error(e);
    }
  };

  return (
    <article className="card scenario-card">
      <div className="row" style={{ alignItems: "flex-start" }}>
        <div className="title" style={{ flex: 1 }} onClick={() => navigate({ page: "editor", id: s.id })}>{s.name}</div>
        <div style={{ position: "relative" }}>
          <Button size="small" variant="ghost" iconOnly icon={Ellipsis} title="Ещё" onClick={() => setMenu(!menu)} />
          {menu && (
            <>
              <div style={{ position: "fixed", inset: 0, zIndex: 10 }} onClick={() => setMenu(false)} />
              <div className="menu">
                <button onClick={duplicate}><CopyPlus size={15} /> Дублировать</button>
                <button onClick={exportFile}><Download size={15} /> Экспорт в файл</button>
                <button onClick={() => navigate({ page: "history", scenarioId: s.id })}><History size={15} /> История запусков</button>
                <button className="danger" onClick={remove}><Trash2 size={15} /> Удалить</button>
              </div>
            </>
          )}
        </div>
      </div>
      <div className="desc">{s.description || <span className="faint">Без описания</span>}</div>
      <div className="row wrap small">
        <Badge tone="neutral" dot={false}>{s.nodeCount} {plural(s.nodeCount, "блок", "блока", "блоков")}</Badge>
        {s.errorCount > 0 && <Badge tone="danger" dot={false}><AlertTriangle size={12} /> {s.errorCount} {plural(s.errorCount, "ошибка", "ошибки", "ошибок")}</Badge>}
        {s.errorCount === 0 && s.warningCount > 0 && <Badge tone="warn" dot={false}>{s.warningCount} {plural(s.warningCount, "замечание", "замечания", "замечаний")}</Badge>}
        <span className="faint">версия {s.version}</span>
      </div>
      <div className="row small muted" style={{ minHeight: 22 }}>
        {s.lastRun ? (
          <>
            <ProcessStatusBadge status={s.lastRun.status} />
            <a href={`#/process/${s.lastRun.id}`}>{formatMoment(s.lastRun.createdAt)}</a>
          </>
        ) : (
          <span className="faint">Ещё не запускался</span>
        )}
      </div>
      <div className="row">
        <Button icon={Pencil} onClick={() => navigate({ page: "editor", id: s.id })}>Открыть</Button>
        <span className="spacer" />
        <Button variant="primary" icon={Play} disabled={s.errorCount > 0} title={s.errorCount > 0 ? "Исправьте ошибки в конструкторе" : undefined} onClick={onRun}>
          Запустить
        </Button>
      </div>
    </article>
  );
}
