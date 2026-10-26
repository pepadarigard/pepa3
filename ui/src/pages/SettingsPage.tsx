import { useEffect, useState } from "react";
import { FolderOpen, Monitor, Moon, Save, Sparkles, Sun } from "lucide-react";
import { useQueryClient } from "@tanstack/react-query";
import { api } from "../api/client";
import { useInfo, useSettings } from "../api/hooks";
import type { Settings } from "../api/types";
import { useFeedback } from "../components/feedback";
import { Badge, Button, Callout, Card, ErrorState, Loading } from "../components/ui";
import { native } from "../lib/native";
import { loadTheme, saveTheme, type Theme } from "../lib/theme";

export function SettingsPage() {
  const settings = useSettings();
  const info = useInfo();
  const feedback = useFeedback();
  const queryClient = useQueryClient();
  const [form, setForm] = useState<Settings | null>(null);
  const [saving, setSaving] = useState(false);
  const [theme, setTheme] = useState<Theme>(loadTheme());

  useEffect(() => {
    if (settings.data && !form) setForm(settings.data);
  }, [settings.data, form]);

  if (settings.isLoading || !form) return <div className="page">{settings.error ? <ErrorState error={settings.error} /> : <Loading />}</div>;

  const changed = JSON.stringify(form) !== JSON.stringify(settings.data);

  const save = async () => {
    setSaving(true);
    try {
      const saved = await api.saveSettings(form);
      setForm(saved);
      queryClient.setQueryData(["settings"], saved);
      void queryClient.invalidateQueries({ queryKey: ["info"] });
      feedback.toast(saved.restartRequired ? "Сохранено. Параметры движка применятся после перезапуска приложения." : "Сохранено");
    } catch (e) {
      feedback.error(e);
    } finally {
      setSaving(false);
    }
  };

  const pick = async () => {
    try {
      const folder = await native.pickFolder(form.workspaceRoot);
      if (folder) setForm({ ...form, workspaceRoot: folder });
    } catch (e) {
      feedback.error(e);
    }
  };

  const number = (key: "maxParallelProcesses" | "stepBudget" | "actionTimeoutSeconds") => (e: React.ChangeEvent<HTMLInputElement>) =>
    setForm({ ...form, [key]: Math.round(Number(e.target.value) || 0) });

  return (
    <div className="page" style={{ maxWidth: 900 }}>
      <header className="page-header">
        <div>
          <h1>Настройки</h1>
          <p className="subtitle">Поведение движка, разрешения файловых действий, внешний вид.</p>
        </div>
        <Button variant="primary" icon={Save} busy={saving} disabled={!changed} onClick={save}>Сохранить</Button>
      </header>

      {settings.data?.restartRequired && (
        <Callout tone="info" title="Нужен перезапуск">Изменённые параметры движка начнут действовать после перезапуска приложения.</Callout>
      )}

      <Card title="Рабочая папка">
        <div className="card-body stack">
          <p className="small muted">
            Файловые действия сценариев могут читать и менять файлы <strong>только внутри этой папки</strong>. Пути за её пределы,
            через символические ссылки и системные папки запрещены. Это главное разрешение для файлов.
          </p>
          <div className="row">
            <input className="input mono" value={form.workspaceRoot} onChange={(e) => setForm({ ...form, workspaceRoot: e.target.value })} />
            {native.available() && <Button icon={FolderOpen} onClick={pick}>Выбрать…</Button>}
          </div>
          {native.available() && (
            <div>
              <Button size="small" variant="ghost" onClick={() => native.openFolder(settings.data!.workspaceRoot).catch((e) => feedback.error(e))}>
                Открыть в проводнике
              </Button>
            </div>
          )}
        </div>
      </Card>

      <Card title="Движок">
        <div className="card-body stack">
          <div className="grid-2">
            <label className="field">
              <span className="field-label">Одновременно выполняемых процессов</span>
              <input className="input" type="number" min={1} max={32} value={form.maxParallelProcesses} onChange={number("maxParallelProcesses")} />
              <span className="field-help">Процессы, которые ждут, в это число не входят.</span>
            </label>
            <label className="field">
              <span className="field-label">Тайм-аут действия по умолчанию, с</span>
              <input className="input" type="number" min={1} max={86400} value={form.actionTimeoutSeconds} onChange={number("actionTimeoutSeconds")} />
              <span className="field-help">Если действие не уложилось — итог «неизвестен», повтора не будет.</span>
            </label>
            <label className="field">
              <span className="field-label">Лимит шагов процесса</span>
              <input className="input" type="number" min={100} max={10000000} value={form.stepBudget} onChange={number("stepBudget")} />
              <span className="field-help">Защита от бесконечных циклов: по достижении Pepa спросит, продолжать ли.</span>
            </label>
          </div>
        </div>
      </Card>

      <Card title="Внешний вид">
        <div className="card-body row">
          <span className="small muted">Тема:</span>
          <div className="segmented">
            {([["system", "Как в системе", Monitor], ["light", "Светлая", Sun], ["dark", "Тёмная", Moon]] as const).map(([value, label, Icon]) => (
              <button key={value} className={theme === value ? "active" : ""} onClick={() => { setTheme(value); saveTheme(value); }}>
                <span className="row" style={{ gap: 6 }}><Icon size={14} /> {label}</span>
              </button>
            ))}
          </div>
        </div>
      </Card>

      <Card title={<h2 className="row"><Sparkles size={16} /> ИИ-помощник</h2>}>
        <div className="card-body stack">
          <div className="row"><Badge tone="neutral">Не подключён</Badge><span className="small muted">Запланирован на этап 5.</span></div>
          <p className="small muted">
            Помощник будет предлагать сценарии по описанию обычными словами и объяснять существующие. Он необязателен: все
            сценарии работают без него, а предложенный сценарий проходит ту же проверку и остаётся редактируемым.
          </p>
        </div>
      </Card>

      {info.data && (
        <Card title="О программе">
          <div className="card-body">
            <dl className="kv">
              <dt>Версия</dt><dd>{info.data.version}</dd>
              <dt>Папка данных</dt><dd>{info.data.dataDirectory}</dd>
              <dt>Запущено</dt><dd>{new Date(info.data.startedAt).toLocaleString("ru-RU")}</dd>
              <dt>Режим</dt><dd>{info.data.isDesktop ? "Приложение Windows" : "Сервер без окна (браузер)"}</dd>
            </dl>
          </div>
        </Card>
      )}
    </div>
  );
}
