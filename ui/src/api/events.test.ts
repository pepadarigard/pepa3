import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { QueryKey } from "@tanstack/react-query";
import { createInvalidator, FLUSH_MS, keysFor } from "./events";

/** Имитация QueryClient: запоминает перечитывания и «идущие» запросы по первому элементу ключа. */
function fakeClient() {
  const calls: { key: QueryKey; cancelRefetch: boolean | undefined }[] = [];
  const fetching = new Set<string>();
  return {
    calls,
    fetching,
    invalidateQueries: (filters?: { queryKey?: QueryKey }, options?: { cancelRefetch?: boolean }) => {
      calls.push({ key: filters!.queryKey!, cancelRefetch: options?.cancelRefetch });
      return Promise.resolve();
    },
    isFetching: (filters?: { queryKey?: QueryKey }) => (fetching.has(String(filters!.queryKey![0])) ? 1 : 0),
  };
}

describe("поток изменений", () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it("пачка сообщений о процессах перечитывает каждый список один раз и не отменяет идущие запросы", () => {
    const client = fakeClient();
    const invalidator = createInvalidator(client);
    for (let i = 0; i < 30; i++) invalidator.add(keysFor({ type: "process", id: `p${i % 3}` }));
    expect(client.calls).toHaveLength(0);

    vi.advanceTimersByTime(FLUSH_MS);
    const lists = client.calls.filter((c) => c.key[0] === "processes");
    expect(lists).toHaveLength(1);
    expect(client.calls.filter((c) => c.key[0] === "overview")).toHaveLength(1);
    expect(client.calls.filter((c) => c.key[0] === "process")).toHaveLength(3);
    expect(client.calls.every((c) => c.cancelRefetch === false)).toBe(true);
  });

  it("пока прежний запрос идёт, список не перечитывается, а после него — перечитывается (последнее изменение не теряется)", () => {
    const client = fakeClient();
    const invalidator = createInvalidator(client);
    client.fetching.add("overview");
    invalidator.add(keysFor({ type: "overview", id: null }));

    vi.advanceTimersByTime(FLUSH_MS * 5);
    expect(client.calls).toHaveLength(0);

    client.fetching.delete("overview");
    vi.advanceTimersByTime(FLUSH_MS);
    expect(client.calls.map((c) => c.key)).toEqual([["overview"]]);
  });

  it("после остановки ничего не перечитывается", () => {
    const client = fakeClient();
    const invalidator = createInvalidator(client);
    invalidator.add(keysFor({ type: "scenarios", id: null }));
    invalidator.dispose();
    vi.advanceTimersByTime(FLUSH_MS * 2);
    expect(client.calls).toHaveLength(0);
  });

  it("сообщение о сценарии без номера ничего не перечитывает", () => {
    expect(keysFor({ type: "scenario", id: null })).toEqual([]);
    expect(keysFor({ type: "unknown", id: "x" })).toEqual([]);
  });
});
