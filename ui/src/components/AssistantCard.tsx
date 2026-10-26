import { useState } from "react";
import { KeyRound, PlugZap, Sparkles, Trash2 } from "lucide-react";
import { useQueryClient } from "@tanstack/react-query";
import { api } from "../api/client";
import { useAssistant } from "../api/hooks";
import { useFeedback } from "./feedback";
import { Badge, Button, Card, ErrorState, Loading } from "./ui";

/** Настройки ИИ-помощника: ключ API Anthropic (хранится зашифрованным, наружу не отдаётся). */
export function AssistantCard() {
  const status = useAssistant();
  const feedback = useFeedback();
  const queryClient = useQueryClient();
  const [key, setKey] = useState("");
  const [busy, setBusy] = useState<"save" | "check" | "clear" | null>(null);
  const [check, setCheck] = useState<{ ok: boolean; message: string } | null>(null);

  const run = async (what: "save" | "check" | "clear") => {
    setBusy(what);
    setCheck(null);
    try {
      if (what === "check") {
        setCheck(await api.assistantCheck());
        return;
      }

      const next = what === "save" ? await api.assistantSetKey(key) : await api.assistantClearKey();
      queryClient.setQueryData(["assistant"], next);
      setKey("");
      feedback.toast(what === "save" ? "Ключ сохранён" : "Ключ удалён");
    } catch (e) {
      feedback.error(e);
    } finally {
      setBusy(null);
    }
  };

  return (
    <Card title={<h2 className="row"><Sparkles size={16} /> ИИ-помощник</h2>}>
      <div className="card-body stack">
        {status.isLoading ? <Loading /> : status.error ? <ErrorState error={status.error} /> : (
          <div className="row wrap">
            {status.data?.configured
              ? <Badge tone="success">Подключён · ключ {status.data.keyHint}</Badge>
              : <Badge tone="neutral">Не подключён</Badge>}
            <span className="small muted">Модель: {status.data?.model}</span>
          </div>
        )}
        <p className="small muted">
          Помощник составляет блоки по описанию обычными словами (кнопка «ИИ-помощник» в конструкторе). Он необязателен: всё
          работает и без него. Предложенные блоки проходят ту же проверку, что и сценарий, и ничего не запускают — вы
          смотрите, правите и сохраняете сами.
        </p>
        <p className="small muted">
          Нужен свой ключ API Anthropic (выдают на console.anthropic.com, оплачивается по расходу; сколько ушло на
          черновик, помощник показывает). Ключ хранится на этом компьютере зашифрованным для вашей учётной записи Windows. В
          Anthropic уходят только ваше описание, имена переменных сценария и список блоков Pepa — не файлы, не история
          и не данные процессов.
        </p>
        <label className="field">
          <span className="field-label">Ключ API Anthropic</span>
          <input className="input mono" type="password" autoComplete="off" value={key} placeholder={status.data?.configured ? "сохранён — введите новый, чтобы заменить" : "sk-ant-…"}
            onChange={(e) => setKey(e.target.value)} />
        </label>
        <div className="row wrap">
          <Button size="small" variant="primary" icon={KeyRound} busy={busy === "save"} disabled={key.trim().length === 0} onClick={() => run("save")}>
            Сохранить ключ
          </Button>
          <Button size="small" icon={PlugZap} busy={busy === "check"} disabled={!status.data?.configured} onClick={() => run("check")}>
            Проверить
          </Button>
          {status.data?.configured && (
            <Button size="small" variant="ghost" icon={Trash2} busy={busy === "clear"} onClick={() => run("clear")}>Удалить ключ</Button>
          )}
          {check && <Badge tone={check.ok ? "success" : "danger"}>{check.ok ? "Работает" : "Не работает"}</Badge>}
          {check && <span className="small muted">{check.message}</span>}
        </div>
      </div>
    </Card>
  );
}
