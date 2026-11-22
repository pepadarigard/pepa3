import type { QueryClient, QueryKey } from "@tanstack/react-query";
import { getToken } from "./client";

export type ConnectionState = "connecting" | "online" | "offline";

/** Какие данные перечитать по сообщению потока. Сообщение — только подсказка {type, id}: данные берутся по REST. */
export function keysFor(event: { type: string; id: string | null }): QueryKey[] {
  switch (event.type) {
    case "process":
      return [
        ["processes"], ["overview"], ["scenarios"],
        // Ресурсы и события меняются вместе с процессами (занят/освобождён, событие применено).
        ["resources"], ["events"], ["questions"],
        ...(event.id ? [["process", event.id]] : []),
      ];
    case "events": return [["events"], ["overview"]];
    case "overview": return [["overview"]];
    case "resources": return [["resources"]];
    case "triggers": return [["triggers"], ["overview"]];
    case "recorder": return [["recorder"], ["recorder-desktop"]];
    case "watches": return [["watches"]];
    case "integrations": return [["telegram"], ["email"]];
    case "scenarios": return [["scenarios"]];
    case "scenario": return event.id ? [["scenario", event.id]] : [];
    default: return [];
  }
}

/** Сколько ждать, собирая сообщения потока в одно перечитывание. */
export const FLUSH_MS = 200;

type Invalidating = Pick<QueryClient, "invalidateQueries" | "isFetching">;

/**
 * Склейка перечитываний. Пока процессы работают, сообщения идут каждые 150 мс и по нескольку сразу; если на каждое
 * перечитывать все списки, отменяя прежний запрос, медленный список (главная, «Выполнение») не обновился бы вовсе, а
 * брошенные запросы заняли бы соединения — и нажатие «Остановить» ждало бы за ними в очереди. Поэтому каждый ключ
 * перечитывается не чаще раза за {@link FLUSH_MS} и не раньше, чем закончится его прежний запрос; идущий запрос не
 * отменяется, а после него данные перечитываются ещё раз — последнее изменение не теряется.
 */
export function createInvalidator(queryClient: Invalidating, flushMs = FLUSH_MS) {
  const pending = new Map<string, QueryKey>();
  let timer: ReturnType<typeof setTimeout> | null = null;

  const flush = () => {
    timer = null;
    for (const [id, key] of [...pending]) {
      if (queryClient.isFetching({ queryKey: key }) > 0) continue; // прежний запрос ещё идёт — перечитаем после него
      pending.delete(id);
      void queryClient.invalidateQueries({ queryKey: key }, { cancelRefetch: false });
    }
    if (pending.size > 0) schedule();
  };
  const schedule = () => {
    if (timer === null) timer = setTimeout(flush, flushMs);
  };

  return {
    add(keys: QueryKey[]) {
      for (const key of keys) pending.set(JSON.stringify(key), key);
      if (pending.size > 0) schedule();
    },
    dispose() {
      if (timer !== null) clearTimeout(timer);
      timer = null;
      pending.clear();
    },
  };
}

/**
 * Поток изменений от движка. При обрыве — переподключение с нарастающей паузой.
 * По сообщениям перечитываются только затронутые данные — склеенно (см. {@link createInvalidator}).
 */
export function startEventStream(queryClient: QueryClient, onState: (state: ConnectionState) => void): () => void {
  let stopped = false;
  let controller: AbortController | null = null;
  let delay = 1000;

  const invalidator = createInvalidator(queryClient);

  const handle = (raw: string) => {
    let event: { type: string; id: string | null };
    try {
      event = JSON.parse(raw);
    } catch {
      return;
    }
    invalidator.add(keysFor(event));
  };

  const connect = async () => {
    while (!stopped) {
      controller = new AbortController();
      onState("connecting");
      try {
        const response = await fetch("/api/stream", {
          headers: { "X-Pepa-Token": getToken() ?? "" },
          signal: controller.signal,
        });
        if (!response.ok || !response.body) {
          throw new Error(`events ${response.status}`);
        }
        onState("online");
        delay = 1000;
        // После переподключения данные могли устареть.
        queryClient.invalidateQueries();
        const reader = response.body.pipeThrough(new TextDecoderStream()).getReader();
        let buffer = "";
        for (;;) {
          const { value, done } = await reader.read();
          if (done) break;
          buffer += value;
          let index: number;
          while ((index = buffer.indexOf("\n\n")) >= 0) {
            const chunk = buffer.slice(0, index);
            buffer = buffer.slice(index + 2);
            for (const line of chunk.split("\n")) {
              if (line.startsWith("data: ")) handle(line.slice(6));
            }
          }
        }
      } catch {
        // Обрыв связи — попробуем снова.
      }
      if (stopped) break;
      onState("offline");
      await new Promise((resolve) => setTimeout(resolve, delay));
      delay = Math.min(delay * 2, 15000);
    }
  };

  void connect();
  return () => {
    stopped = true;
    controller?.abort();
    invalidator.dispose();
  };
}
