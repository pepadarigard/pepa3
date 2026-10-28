import { useEffect, useState } from "react";
import { BellRing, Fingerprint, FolderOpen, Monitor, Moon, PlugZap, Save, Sun } from "lucide-react";
import { useQueryClient } from "@tanstack/react-query";
import { api } from "../api/client";
import { useInfo, useSettings } from "../api/hooks";
import type { Settings } from "../api/types";
import { AssistantCard } from "../components/AssistantCard";
import { BackupsCard } from "../components/BackupsCard";
import { PluginsCard } from "../components/PluginsCard";
import { WatchesCard } from "../components/WatchesCard";
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
  const [checking, setChecking] = useState(false);
  const [check, setCheck] = useState<{ ok: boolean; message: string } | null>(null);
  const [testing, setTesting] = useState(false);

  useEffect(() => {
    if (settings.data && !form) setForm(settings.data);
  }, [settings.data, form]);

  if (settings.isLoading || !form) return <div className="page">{settings.error ? <ErrorState error={settings.error} /> : <Loading />}</div>;

  const changed = JSON.stringify(form) !== JSON.stringify(settings.data);

  const checkAdsPower = async () => {
    setChecking(true);
    setCheck(null);
    try {
      setCheck(await api.checkAdsPower());
    } catch (e) {
      setCheck({ ok: false, message: e instanceof Error ? e.message : String(e) });
    } finally {
      setChecking(false);
    }
  };

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

  const sendTest = async () => {
    setTesting(true);
    try {
      feedback.toast((await api.notificationTest()).message, "success");
    } catch (e) {
      feedback.error(e);
    } finally {
      setTesting(false);
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

  const number = (key: "maxParallelProcesses" | "stepBudget" | "actionTimeoutSeconds" | "historyDays") => (e: React.ChangeEvent<HTMLInputElement>) =>
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

      <WatchesCard />

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
            <label className="field">
              <span className="field-label">Хранить историю завершённых процессов, дней</span>
              <input className="input" type="number" min={0} max={3650} value={form.historyDays} onChange={number("historyDays")} />
              <span className="field-help">
                0 — хранить всегда. Незавершённые процессы и связанные с ними не удаляются никогда. У старых событий
                удаляется содержимое; сами события (по ним узнаются повторы) — через год.
              </span>
            </label>
          </div>
        </div>
      </Card>

      <Card title={<h2 className="row"><Fingerprint size={16} /> AdsPower</h2>}>
        <div className="card-body stack">
          <p className="small muted">
            Блок «Открыть профиль AdsPower» работает через Local API запущенного приложения AdsPower. Адрес и ключ видны в
            AdsPower: Настройки → Local API. Ключ нужен, только если в AdsPower включена проверка ключа.
          </p>
          <div className="grid-2">
            <label className="field">
              <span className="field-label">Адрес Local API</span>
              <input className="input mono" value={form.adsPowerUrl ?? ""} placeholder="http://127.0.0.1:50325"
                onChange={(e) => setForm({ ...form, adsPowerUrl: e.target.value })} />
            </label>
            <label className="field">
              <span className="field-label">Ключ API</span>
              <input className="input mono" type="password" autoComplete="off" value={form.adsPowerApiKey ?? ""}
                placeholder={form.adsPowerHasKey ? "сохранён — оставьте пустым, чтобы не менять" : "не задан"}
                onChange={(e) => setForm({ ...form, adsPowerApiKey: e.target.value === "" ? null : e.target.value })} />
              {form.adsPowerHasKey && (
                <button type="button" className="link-button small" style={{ color: "var(--danger)", alignSelf: "flex-start" }}
                  onClick={() => setForm({ ...form, adsPowerApiKey: "", adsPowerHasKey: false })}>
                  Удалить сохранённый ключ
                </button>
              )}
            </label>
          </div>
          <div className="row wrap">
            <Button size="small" icon={PlugZap} busy={checking} onClick={checkAdsPower} disabled={changed} title={changed ? "Сначала сохраните настройки" : undefined}>
              Проверить связь
            </Button>
            {check && <Badge tone={check.ok ? "success" : "danger"}>{check.ok ? "На связи" : "Нет связи"}</Badge>}
            {check && <span className="small muted">{check.message}</span>}
          </div>
        </div>
      </Card>

      <Card title={<h2 className="row"><BellRing size={16} /> Уведомления</h2>}>
        <div className="card-body stack" data-testid="notifications-card">
          <p className="small muted">
            Когда процесс останавливается и ждёт вашего решения, кнопка Pepa на панели задач мигает (если окно не перед вами). Можно
            получать и сообщение в Telegram — в «Избранное» вашего аккаунта, подключённого в «Интеграциях»: удобно, когда вас нет у
            компьютера. Несколько остановок подряд приходят одним сообщением; о каждой — один раз.
          </p>
          <label className="row small" style={{ gap: 8 }}>
            <input type="checkbox" checked={form.notifyTelegram} onChange={(e) => setForm({ ...form, notifyTelegram: e.target.checked })} />
            Сообщать в Telegram («Избранное»), когда процесс ждёт решения
          </label>
          <div>
            <Button size="small" variant="ghost" busy={testing} onClick={() => void sendTest()}>Отправить пробное сообщение</Button>
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

      <AssistantCard />

      <BackupsCard />

      <PluginsCard />

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
