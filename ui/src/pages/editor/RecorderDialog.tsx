import { useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Circle, Square } from "lucide-react";
import { api } from "../../api/client";
import type { RecorderState } from "../../api/types";
import { Dialog } from "../../components/feedback";
import { Button, Callout, Spinner } from "../../components/ui";
import type { BlockClipboard } from "../../lib/clipboard";
import { recordingToBlocks, type RecordedAction } from "../../lib/recording";
import { AdsPowerPicker } from "./FieldEditor";

const kindText: Record<RecordedAction["kind"], string> = {
  goto: "Перейти",
  click: "Нажать",
  fill: "Ввести",
  select: "Выбрать",
  check: "Отметить",
  uncheck: "Снять отметку",
  press: "Клавиша",
  tab: "Новая вкладка",
};

/** Запись действий в браузере: пользователь работает на сайте, Pepa превращает действия в блоки. */
export function RecorderDialog({ onInsert, onClose }: { onInsert: (blocks: BlockClipboard) => void; onClose: () => void }) {
  const client = useQueryClient();
  const query = useQuery({
    queryKey: ["recorder"],
    queryFn: api.recorder,
    refetchInterval: (q) => (q.state.data?.recording ? 1500 : false),
  });
  const [browser, setBrowser] = useState<"pepa" | "adspower">("pepa");
  const [profileId, setProfileId] = useState("");
  const [url, setUrl] = useState("https://");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const state = query.data;

  const apply = (next: RecorderState) => client.setQueryData(["recorder"], next);
  const start = async () => {
    setBusy(true);
    setError(null);
    try {
      apply(await api.recorderStart({ browser, profileId: browser === "adspower" ? profileId.trim() : null, url: url.trim() === "https://" ? null : url.trim() }));
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
      void query.refetch();
    } finally {
      setBusy(false);
    }
  };
  const stop = async (insert: boolean) => {
    setBusy(true);
    setError(null);
    try {
      const result = await api.recorderStop();
      apply(result);
      if (insert && result.actions.length > 0) {
        onInsert(recordingToBlocks(result.actions, { browser: result.browser ?? browser, profileId: result.profileId }));
      }
      onClose();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  const recording = state?.recording === true;
  return (
    <Dialog
      title={recording ? "Идёт запись действий" : "Записать действия в браузере"}
      width={640}
      onClose={() => (recording ? undefined : onClose())}
      footer={
        recording ? (
          <>
            <Button busy={busy} onClick={() => void stop(false)}>Отменить запись</Button>
            <Button variant="primary" icon={Square} busy={busy} onClick={() => void stop(true)} disabled={(state?.actions.length ?? 0) === 0}>
              Остановить и добавить в сценарий
            </Button>
          </>
        ) : (
          <>
            <Button onClick={onClose}>Закрыть</Button>
            <Button variant="primary" icon={Circle} busy={busy} onClick={() => void start()} disabled={browser === "adspower" && profileId.trim() === ""}>
              Начать запись
            </Button>
          </>
        )
      }
    >
      {error && <Callout tone="danger">{error}</Callout>}
      {!state && <Spinner />}
      {state && !recording && (
        <div className="stack" style={{ gap: 12 }}>
          <p className="small muted">
            Откроется браузер. Работайте на сайте как обычно: нажатия, ввод текста, выбор в списках, флажки и клавиши Enter/Escape
            записываются шагами с селекторами. Пароли не записываются — вместо них будет переменная. Потом блоки можно поправить.
          </p>
          <div className="row" style={{ gap: 16 }}>
            <label className="row small" style={{ gap: 6 }}>
              <input type="radio" checked={browser === "pepa"} onChange={() => setBrowser("pepa")} /> Браузер Pepa (вход на сайты сохраняется)
            </label>
            <label className="row small" style={{ gap: 6 }}>
              <input type="radio" checked={browser === "adspower"} onChange={() => setBrowser("adspower")} /> Профиль AdsPower
            </label>
          </div>
          {browser === "adspower" && (
            <label className="field">
              <span className="field-label">ID профиля AdsPower</span>
              <input className="input mono" value={profileId} placeholder="k1bitq6l" onChange={(e) => setProfileId(e.target.value)} />
              <AdsPowerPicker onPick={setProfileId} />
            </label>
          )}
          <label className="field">
            <span className="field-label">Начать с адреса</span>
            <input className="input mono" value={url} onChange={(e) => setUrl(e.target.value)} />
          </label>
        </div>
      )}
      {state && recording && (
        <div className="stack" style={{ gap: 8 }}>
          <p className="small muted">
            Работайте в открывшемся браузере. Шаги появляются здесь по мере действий. Закончив, нажмите «Остановить и добавить в сценарий».
          </p>
          {state.actions.length === 0 ? (
            <p className="small faint">Пока ничего не записано.</p>
          ) : (
            <ol className="recorded-list">
              {state.actions.map((a, i) => (
                <li key={i}>
                  <span className="recorded-kind">{kindText[a.kind]}</span>
                  <span className="truncate">
                    {a.kind === "goto" ? a.url : a.kind === "tab" ? "" : a.label || a.selector}
                    {a.kind === "fill" && !a.secret && a.value ? ` = «${a.value}»` : ""}
                    {a.kind === "select" && a.value ? ` → «${a.value}»` : ""}
                    {a.kind === "press" && a.value ? ` ${a.value}` : ""}
                  </span>
                  {a.selector && a.kind !== "goto" && <code className="small faint truncate">{a.selector}</code>}
                  {a.note && <span className="small" style={{ color: "var(--warn)" }}>{a.note}</span>}
                </li>
              ))}
            </ol>
          )}
        </div>
      )}
    </Dialog>
  );
}
