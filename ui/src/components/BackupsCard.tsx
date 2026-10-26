import { useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Archive, FolderOpen } from "lucide-react";
import { api } from "../api/client";
import type { BackupInfo } from "../api/types";
import { formatDateTime, formatSize } from "../lib/format";
import { native } from "../lib/native";
import { useFeedback } from "./feedback";
import { Badge, Button, Callout, Card, Spinner } from "./ui";

const kindText: Record<BackupInfo["kind"], string> = {
  daily: "ежедневная",
  manual: "вручную",
  migration: "перед обновлением",
};

/** Резервные копии базы: какие есть, сделать сейчас, как восстановить. */
export function BackupsCard() {
  const client = useQueryClient();
  const feedback = useFeedback();
  const backups = useQuery({ queryKey: ["backups"], queryFn: api.backups });
  const [busy, setBusy] = useState(false);

  const create = async () => {
    setBusy(true);
    try {
      const made = await api.backupCreate();
      await client.invalidateQueries({ queryKey: ["backups"] });
      feedback.toast(`Копия сделана: ${made.name}`, "success");
    } catch (e) {
      feedback.error(e);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Card title={<h2 className="row"><Archive size={16} /> Резервные копии</h2>}>
      <div className="card-body stack" style={{ gap: 10 }} data-testid="backups-card">
        <p className="small muted">
          Pepa копирует свою базу — сценарии, процессы, историю и настройки — раз в сутки и перед обновлением базы новой версией.
          Хранятся последние 7 ежедневных копий, 10 сделанных вручную и 3 перед обновлением. Рабочая папка с файлами не копируется.
        </p>
        {backups.isLoading && <Spinner />}
        {backups.error && <Callout tone="danger">{backups.error instanceof Error ? backups.error.message : String(backups.error)}</Callout>}
        {backups.data?.lastError && (
          <Callout tone="attention" title="Последняя автоматическая копия не удалась">{backups.data.lastError}</Callout>
        )}
        {backups.data && (
          <>
            <div className="row">
              <Button size="small" icon={Archive} busy={busy} onClick={() => void create()}>Сделать копию сейчас</Button>
              {native.available() && (
                <Button size="small" variant="ghost" icon={FolderOpen}
                  onClick={() => native.openFolder(backups.data!.directory).catch((e) => feedback.error(e))}>
                  Открыть папку
                </Button>
              )}
            </div>
            {backups.data.items.length === 0 ? (
              <p className="small faint">Копий пока нет.</p>
            ) : (
              <ul className="backup-list">
                {backups.data.items.slice(0, 8).map((b) => (
                  <li key={b.name}>
                    <span>{formatDateTime(b.createdAt)}</span>
                    <Badge tone="neutral" dot={false}>{kindText[b.kind]}</Badge>
                    <span className="small faint">{formatSize(b.size)}</span>
                    <code className="small faint truncate">{b.name}</code>
                  </li>
                ))}
              </ul>
            )}
            <details className="small muted">
              <summary>Как восстановить из копии</summary>
              <ol>
                <li>Закройте Pepa (процессы, которые сейчас идут, будут приостановлены).</li>
                <li>В папке данных Pepa (<code>{backups.data.directory.replace(/[\\/]backups$/, "")}</code>) переименуйте
                  <code> pepa.db</code>, а также <code>pepa.db-wal</code> и <code>pepa.db-shm</code>, если они есть, — например, добавьте «.old».</li>
                <li>Скопируйте нужную копию из папки копий на место базы и назовите её <code>pepa.db</code>.</li>
                <li>Запустите Pepa. Она узнает, что база из копии: каждый незавершённый процесс остановится и будет ждать вашего
                  решения — что он сделал после копии, Pepa не знает. Проверьте это во внешних программах, прежде чем продолжать.</li>
              </ol>
            </details>
          </>
        )}
      </div>
    </Card>
  );
}
