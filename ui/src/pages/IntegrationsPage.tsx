import { AppWindow, BellRing, Bot, Copy, FolderOpen, Globe, Keyboard, Send, type LucideIcon } from "lucide-react";
import { getToken } from "../api/client";
import { useInfo } from "../api/hooks";
import { useFeedback } from "../components/feedback";
import { Badge, Button } from "../components/ui";
import { native } from "../lib/native";
import { navigate } from "../router";

interface Integration {
  icon: LucideIcon;
  title: string;
  status: "ready" | "planned";
  description: string;
  details: string;
}

const integrations: Integration[] = [
  {
    icon: Globe,
    title: "Браузер (Playwright)",
    status: "planned",
    description: "Открытие страниц, клики, ввод, ожидание элементов, чтение данных.",
    details: "Этап 4. Обычный браузер — в пределах поддерживаемых способов подключения.",
  },
  {
    icon: Bot,
    title: "AdsPower",
    status: "planned",
    description: "Запуск существующих профилей через Local API и подключение к ним браузерных действий.",
    details:
      "Этап 4. Перед реализацией — проверка документации Local API и его доступности на вашем тарифе. Профиль будет ресурсом: один активный процесс на профиль (менеджер ресурсов уже работает).",
  },
  {
    icon: Send,
    title: "Telegram",
    status: "planned",
    description: "Сообщения и ожидание ответов: через официальный API или через Telegram Desktop.",
    details: "Этап 4. Bot API и пользовательский аккаунт (MTProto) — разные возможности, их нельзя путать.",
  },
  {
    icon: AppWindow,
    title: "Windows-приложения",
    status: "planned",
    description: "Поиск элементов окон через UI Automation, нажатия и ввод.",
    details: "Этап 4. Если приложение не отдаёт элементы — запасной вариант мышью и клавиатурой, с пометкой о меньшей надёжности.",
  },
  {
    icon: Keyboard,
    title: "Мышь и клавиатура",
    status: "planned",
    description: "Действия физическим вводом — как общий ресурс, который нельзя занять двум процессам сразу.",
    details: "Менеджер ресурсов готов; сами действия мышью и клавиатурой — этап 4.",
  },
];

export function IntegrationsPage() {
  const info = useInfo();
  const feedback = useFeedback();
  const endpoint = `${window.location.origin}/api/events`;
  const example = `curl -X POST ${endpoint} -H "X-Pepa-Token: ${getToken() ?? "…"}" -H "Content-Type: application/json" -d "{\"source\":\"http\",\"type\":\"order\",\"correlationKey\":\"A-100\",\"payload\":{\"text\":\"готово\"}}"`;
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(example);
      feedback.toast("Пример скопирован");
    } catch {
      feedback.toast("Не удалось скопировать — выделите текст вручную.", "error");
    }
  };
  return (
    <div className="page">
      <header className="page-header">
        <div>
          <h1>Интеграции</h1>
          <p className="subtitle">Через что сценарии взаимодействуют с внешним миром. Готовность указана честно.</p>
        </div>
      </header>

      <div className="card integration-card">
        <div className="integration-icon" style={{ background: "var(--success-soft)", color: "var(--success)" }}>
          <FolderOpen size={20} />
        </div>
        <div className="stack" style={{ gap: 6, flex: 1 }}>
          <div className="row">
            <h2>Файлы и папки</h2>
            <Badge tone="success">Работает</Badge>
          </div>
          <p className="muted small">
            Запись, чтение, копирование, перемещение, удаление файлов, создание папок, проверка наличия и список файлов. Каждое
            действие с эффектом умеет проверить, выполнилось ли оно, — это защищает от повторов после сбоя.
          </p>
          <p className="small">
            Действия работают только внутри рабочей папки: <code>{info.data?.workspaceRoot ?? "…"}</code>
          </p>
          <div className="row">
            <Button size="small" onClick={() => navigate({ page: "settings" })}>Изменить рабочую папку</Button>
            {native.available() && info.data && (
              <Button size="small" variant="ghost" onClick={() => native.openFolder(info.data!.workspaceRoot).catch(() => undefined)}>Открыть папку</Button>
            )}
          </div>
        </div>
      </div>

      <div className="card integration-card">
        <div className="integration-icon" style={{ background: "var(--success-soft)", color: "var(--success)" }}>
          <BellRing size={20} />
        </div>
        <div className="stack" style={{ gap: 6, flex: 1, minWidth: 0 }}>
          <div className="row">
            <h2>События</h2>
            <Badge tone="success">Работает</Badge>
          </div>
          <p className="muted small">
            Процессы ждут события блоком «Ждать событие» и отправляют их блоком «Отправить событие». Событие можно отправить
            вручную на странице «Выполнение» или из своей программы на этом компьютере — запросом к локальному адресу Pepa.
            Повтор с тем же «ключом от повторов» не создаёт второе событие.
          </p>
          <pre className="code-block" title={example}>{example}</pre>
          <p className="small faint">
            Токен в заголовке меняется при каждом запуске Pepa и доступен только с этого компьютера. Постоянный ключ для внешних
            программ появится вместе с интеграциями (этап 4).
          </p>
          <div className="row">
            <Button size="small" icon={Copy} onClick={copy}>Скопировать пример</Button>
            <Button size="small" variant="ghost" onClick={() => navigate({ page: "runs" })}>Открыть события</Button>
          </div>
        </div>
      </div>

      <div className="grid-2">
        {integrations.map((item) => (
          <div key={item.title} className="card integration-card">
            <div className="integration-icon">
              <item.icon size={20} />
            </div>
            <div className="stack" style={{ gap: 6 }}>
              <div className="row">
                <h2>{item.title}</h2>
                <Badge tone="neutral">Запланировано</Badge>
              </div>
              <p className="muted small">{item.description}</p>
              <p className="small faint">{item.details}</p>
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}
