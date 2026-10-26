import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "./client";
import type { ResumeAction, NodeTypeDescriptor } from "./types";
import { useMemo } from "react";

export const useInfo = () => useQuery({ queryKey: ["info"], queryFn: api.info, staleTime: 60_000 });

export const useNodeTypes = () =>
  useQuery({ queryKey: ["node-types"], queryFn: api.nodeTypes, staleTime: Infinity });

/** Описания блоков по типу. */
export function useDescriptors(): Map<string, NodeTypeDescriptor> {
  const { data } = useNodeTypes();
  return useMemo(() => new Map((data?.nodeTypes ?? []).map((d) => [d.type, d])), [data]);
}

export const useOverview = () =>
  useQuery({ queryKey: ["overview"], queryFn: api.overview, refetchInterval: 15_000 });

export const useScenarios = () => useQuery({ queryKey: ["scenarios"], queryFn: api.scenarios });

export const useScenario = (id: string | undefined) =>
  useQuery({
    queryKey: ["scenario", id],
    queryFn: () => api.scenario(id!),
    enabled: !!id,
    staleTime: Infinity,
    refetchOnMount: "always",
    refetchOnWindowFocus: false,
  });

export const useProcesses = (view: "active" | "finished" | "attention" | "all", scenarioId?: string) =>
  useQuery({
    queryKey: ["processes", view, scenarioId ?? ""],
    queryFn: () => api.processes(view, scenarioId, view === "active" ? 500 : 300),
    refetchInterval: view === "active" ? 10_000 : false,
  });

export const useProcess = (id: string | undefined) =>
  useQuery({ queryKey: ["process", id], queryFn: () => api.process(id!), enabled: !!id, refetchInterval: 10_000 });

export const useSettings = () => useQuery({ queryKey: ["settings"], queryFn: api.settings });

export const useEvents = (view: "open" | "conflict" | "unmatched" | "all" = "all") =>
  useQuery({ queryKey: ["events", view], queryFn: () => api.events(view), refetchInterval: 15_000 });

export const useResources = () => useQuery({ queryKey: ["resources"], queryFn: api.resources, refetchInterval: 15_000 });

export const useTelegram = () => useQuery({ queryKey: ["telegram"], queryFn: api.telegram, refetchInterval: 60_000 });

export const useTriggers = (scenarioId?: string) =>
  useQuery({ queryKey: ["triggers", scenarioId ?? ""], queryFn: () => api.triggers(scenarioId), refetchInterval: 30_000 });

/** Управление процессом: пауза, остановка, решения пользователя. */
export function useProcessControl() {
  const client = useQueryClient();
  const invalidate = (id: string) => {
    client.invalidateQueries({ queryKey: ["process", id] });
    client.invalidateQueries({ queryKey: ["processes"] });
    client.invalidateQueries({ queryKey: ["overview"] });
  };
  return useMutation({
    mutationFn: async ({ id, action }: { id: string; action: "pause" | "cancel" | ResumeAction }) => {
      if (action === "pause") return api.pause(id);
      if (action === "cancel") return api.cancel(id);
      return api.resume(id, action);
    },
    onSettled: (_data, _error, variables) => invalidate(variables.id),
  });
}
