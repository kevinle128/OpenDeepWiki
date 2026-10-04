"use client";

import React, { useCallback, useEffect, useMemo, useState } from "react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { Switch } from "@/components/ui/switch";
import { Textarea } from "@/components/ui/textarea";
import { ModelIcon, ProviderIcon } from "@/components/admin/provider-icons";
import { cn } from "@/lib/utils";
import {
  AiModelConfig,
  AiProviderConfig,
  ModelConfig,
  createModelConfig,
  deleteModelConfig,
  getAiModels,
  getAiProviders,
  getModelConfigs,
  getSettings,
  updateModelConfig,
  updateSettings,
} from "@/lib/admin-api";
import { useTranslations } from "@/hooks/use-translations";
import { PageHeader } from "@/components/admin/page-header";
import {
  AlertCircle,
  BookOpen,
  Boxes,
  CheckCircle2,
  FileText,
  Languages,
  Loader2,
  LucideIcon,
  Network,
  Plus,
  RefreshCw,
  Save,
  Settings2,
  ShieldCheck,
  Sparkles,
  Trash2,
} from "lucide-react";
import { toast } from "sonner";

type SystemBindingId = "content" | "catalog" | "translation" | "graphify";

interface SystemBindingDefinition {
  id: SystemBindingId;
  titleKey: string;
  groupKey: string;
  descriptionKey: string;
  providerKey: string;
  modelKey: string;
  icon: LucideIcon;
  tone: string;
  recommendationKey: string;
}

interface TaskBindingValue {
  providerId: string;
  modelId: string;
}

const SYSTEM_MODEL_BINDINGS: SystemBindingDefinition[] = [
  {
    id: "content",
    titleKey: "admin.modelConfigs.tasks.content.title",
    groupKey: "admin.modelConfigs.tasks.content.group",
    descriptionKey: "admin.modelConfigs.tasks.content.description",
    providerKey: "WIKI_CONTENT_PROVIDER_ID",
    modelKey: "WIKI_CONTENT_MODEL_ID",
    icon: FileText,
    tone: "border-sky-500/25 bg-sky-500/10 text-sky-600 dark:text-sky-300",
    recommendationKey: "admin.modelConfigs.tasks.content.recommendation",
  },
  {
    id: "catalog",
    titleKey: "admin.modelConfigs.tasks.catalog.title",
    groupKey: "admin.modelConfigs.tasks.catalog.group",
    descriptionKey: "admin.modelConfigs.tasks.catalog.description",
    providerKey: "WIKI_CATALOG_PROVIDER_ID",
    modelKey: "WIKI_CATALOG_MODEL_ID",
    icon: BookOpen,
    tone: "border-amber-500/25 bg-amber-500/10 text-amber-600 dark:text-amber-300",
    recommendationKey: "admin.modelConfigs.tasks.catalog.recommendation",
  },
  {
    id: "translation",
    titleKey: "admin.modelConfigs.tasks.translation.title",
    groupKey: "admin.modelConfigs.tasks.translation.group",
    descriptionKey: "admin.modelConfigs.tasks.translation.description",
    providerKey: "WIKI_TRANSLATION_PROVIDER_ID",
    modelKey: "WIKI_TRANSLATION_MODEL_ID",
    icon: Languages,
    tone: "border-emerald-500/25 bg-emerald-500/10 text-emerald-600 dark:text-emerald-300",
    recommendationKey: "admin.modelConfigs.tasks.translation.recommendation",
  },
  {
    id: "graphify",
    titleKey: "admin.modelConfigs.tasks.graphify.title",
    groupKey: "admin.modelConfigs.tasks.graphify.group",
    descriptionKey: "admin.modelConfigs.tasks.graphify.description",
    providerKey: "GRAPHIFY_PROVIDER_ID",
    modelKey: "GRAPHIFY_MODEL_ID",
    icon: Network,
    tone: "border-violet-500/25 bg-violet-500/10 text-violet-600 dark:text-violet-300",
    recommendationKey: "admin.modelConfigs.tasks.graphify.recommendation",
  },
];

const emptyBindingForm = {
  name: "",
  aiProviderId: "",
  modelId: "",
  isDefault: false,
  isActive: true,
  description: "",
};

function createEmptyTaskBindings(): Record<SystemBindingId, TaskBindingValue> {
  return {
    content: { providerId: "", modelId: "" },
    catalog: { providerId: "", modelId: "" },
    translation: { providerId: "", modelId: "" },
    graphify: { providerId: "", modelId: "" },
  };
}

function formatCompactNumber(value?: number) {
  if (!value) return "-";
  return new Intl.NumberFormat("en", { notation: "compact" }).format(value);
}

function formatPrice(value?: number) {
  if (value === undefined || value === null) return "-";
  if (value === 0) return "0";
  return `$${value}`;
}

function getProviderLabel(provider?: AiProviderConfig) {
  return provider?.displayName || provider?.name || "";
}

function getModelLabel(model?: AiModelConfig, fallback?: string) {
  return model?.displayName || model?.name || fallback || "";
}

function getCapabilityKeys(model?: AiModelConfig) {
  if (!model) return ["chat"];

  const keys = [];
  if (model.supportsThinking) keys.push("thinking");
  if (model.supportsVision) keys.push("vision");
  if (model.supportsTools) keys.push("tools");
  if (model.supportsJsonMode) keys.push("json");
  return keys.length > 0 ? keys : ["chat"];
}

export default function AdminModelConfigsPage() {
  const t = useTranslations();
  const [providers, setProviders] = useState<AiProviderConfig[]>([]);
  const [models, setModels] = useState<AiModelConfig[]>([]);
  const [bindings, setBindings] = useState<ModelConfig[]>([]);
  const [taskBindings, setTaskBindings] = useState<Record<SystemBindingId, TaskBindingValue>>(
    createEmptyTaskBindings
  );
  const [savedTaskBindings, setSavedTaskBindings] = useState<Record<SystemBindingId, TaskBindingValue>>(
    createEmptyTaskBindings
  );
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [savingTaskId, setSavingTaskId] = useState<SystemBindingId | "all" | null>(null);
  const [bindingDialog, setBindingDialog] = useState<ModelConfig | null | "new">(null);
  const [bindingForm, setBindingForm] = useState(emptyBindingForm);

  const providerById = useMemo(() => {
    return new Map(providers.map((provider) => [provider.id, provider]));
  }, [providers]);

  const localizedTaskBindings = useMemo(
    () =>
      SYSTEM_MODEL_BINDINGS.map((task) => ({
        ...task,
        title: t(task.titleKey),
        group: t(task.groupKey),
        description: t(task.descriptionKey),
        recommendation: t(task.recommendationKey),
      })),
    [t]
  );

  const modelsByProvider = useMemo(() => {
    return models.reduce<Record<string, AiModelConfig[]>>((acc, model) => {
      acc[model.providerId] ??= [];
      acc[model.providerId].push(model);
      return acc;
    }, {});
  }, [models]);

  const modelByProviderAndId = useMemo(() => {
    return new Map(models.map((model) => [`${model.providerId}:${model.modelId}`, model]));
  }, [models]);

  const loadData = useCallback(() => {
    return Promise.all([
        getAiProviders(),
        getAiModels(),
        getModelConfigs(),
        getSettings("ai"),
      ]).then(([providerData, modelData, bindingData, settingsData]) => {

      const values = new Map(settingsData.map((setting) => [setting.key, setting.value || ""]));
      const nextTaskBindings = createEmptyTaskBindings();
      SYSTEM_MODEL_BINDINGS.forEach((task) => {
        nextTaskBindings[task.id] = {
          providerId: values.get(task.providerKey) || "",
          modelId: values.get(task.modelKey) || "",
        };
      });

      setProviders(providerData);
      setModels(modelData);
      setBindings(bindingData);
      setTaskBindings(nextTaskBindings);
      setSavedTaskBindings(nextTaskBindings);
    }).catch(error => {
      console.error(error);
      toast.error(t("admin.modelConfigs.toasts.loadFailed"));
    }).finally(() => {
      setLoading(false);
    });
  }, [t]);

  const fetchData = useCallback(() => {
    setLoading(true);
    return loadData();
  }, [loadData]);

  useEffect(() => {
    void loadData();
  }, [loadData]);

  const getModelsForProvider = useCallback(
    (providerId: string, selectedModelId?: string) => {
      return (modelsByProvider[providerId] ?? []).filter(
        (model) => model.isActive || model.modelId === selectedModelId
      );
    },
    [modelsByProvider]
  );

  const pickDefaultModelId = useCallback(
    (providerId: string) => {
      const provider = providerById.get(providerId);
      const providerModels = getModelsForProvider(providerId);
      if (
        provider?.defaultModelId &&
        providerModels.some((model) => model.modelId === provider.defaultModelId)
      ) {
        return provider.defaultModelId;
      }

      return providerModels.find((model) => model.isDefault)?.modelId || providerModels[0]?.modelId || "";
    },
    [getModelsForProvider, providerById]
  );

  const modelsForBinding = useMemo(() => {
    return getModelsForProvider(bindingForm.aiProviderId, bindingForm.modelId);
  }, [bindingForm.aiProviderId, bindingForm.modelId, getModelsForProvider]);

  const isTaskDirty = useCallback(
    (taskId: SystemBindingId) => {
      const current = taskBindings[taskId];
      const saved = savedTaskBindings[taskId];
      return current.providerId !== saved.providerId || current.modelId !== saved.modelId;
    },
    [savedTaskBindings, taskBindings]
  );

  const dirtyTaskCount = useMemo(
    () => SYSTEM_MODEL_BINDINGS.filter((task) => isTaskDirty(task.id)).length,
    [isTaskDirty]
  );

  const configuredTaskCount = useMemo(
    () =>
      SYSTEM_MODEL_BINDINGS.filter((task) => {
        const current = taskBindings[task.id];
        return current.providerId && current.modelId;
      }).length,
    [taskBindings]
  );

  const activeProviderCount = useMemo(
    () => providers.filter((provider) => provider.isActive).length,
    [providers]
  );

  const openBindingDialog = (binding?: ModelConfig) => {
    setBindingDialog(binding ?? "new");
    setBindingForm(
      binding
        ? {
            name: binding.name,
            aiProviderId: binding.aiProviderId ?? "",
            modelId: binding.modelId,
            isDefault: binding.isDefault,
            isActive: binding.isActive,
            description: binding.description ?? "",
          }
        : {
            ...emptyBindingForm,
            aiProviderId: providers.find((provider) => provider.isActive)?.id ?? providers[0]?.id ?? "",
          }
    );
  };

  const updateTaskProvider = (taskId: SystemBindingId, providerId: string) => {
    setTaskBindings((prev) => ({
      ...prev,
      [taskId]: {
        providerId,
        modelId: pickDefaultModelId(providerId),
      },
    }));
  };

  const updateTaskModel = (taskId: SystemBindingId, modelId: string) => {
    setTaskBindings((prev) => ({
      ...prev,
      [taskId]: {
        ...prev[taskId],
        modelId,
      },
    }));
  };

  const saveTaskBinding = async (task: SystemBindingDefinition) => {
    const current = taskBindings[task.id];
    if (!current.providerId || !current.modelId) {
      toast.error(t("admin.modelConfigs.toasts.selectProviderAndModelForTask", { task: t(task.titleKey) }));
      return;
    }

    setSavingTaskId(task.id);
    try {
      await updateSettings([
        { key: task.providerKey, value: current.providerId },
        { key: task.modelKey, value: current.modelId },
      ]);
      setSavedTaskBindings((prev) => ({ ...prev, [task.id]: { ...current } }));
      toast.success(t("admin.modelConfigs.toasts.taskSaved", { task: t(task.titleKey) }));
    } catch (error) {
      console.error(error);
      toast.error(t("admin.modelConfigs.toasts.taskSaveFailed", { task: t(task.titleKey) }));
    } finally {
      setSavingTaskId(null);
    }
  };

  const saveAllTaskBindings = async () => {
    const dirtyTasks = SYSTEM_MODEL_BINDINGS.filter((task) => isTaskDirty(task.id));
    const incompleteTask = dirtyTasks.find((task) => {
      const current = taskBindings[task.id];
      return !current.providerId || !current.modelId;
    });

    if (incompleteTask) {
      toast.error(t("admin.modelConfigs.toasts.completeTask", { task: t(incompleteTask.titleKey) }));
      return;
    }

    setSavingTaskId("all");
    try {
      await updateSettings(
        dirtyTasks.flatMap((task) => {
          const current = taskBindings[task.id];
          return [
            { key: task.providerKey, value: current.providerId },
            { key: task.modelKey, value: current.modelId },
          ];
        })
      );

      setSavedTaskBindings((prev) => {
        const next = { ...prev };
        dirtyTasks.forEach((task) => {
          next[task.id] = { ...taskBindings[task.id] };
        });
        return next;
      });
      toast.success(t("admin.modelConfigs.toasts.systemBindingsSaved"));
    } catch (error) {
      console.error(error);
      toast.error(t("admin.modelConfigs.toasts.systemBindingsSaveFailed"));
    } finally {
      setSavingTaskId(null);
    }
  };

  const saveBinding = async () => {
    if (!bindingForm.name || !bindingForm.aiProviderId || !bindingForm.modelId) {
      toast.error(t("admin.modelConfigs.toasts.selectProviderAndModel"));
      return;
    }

    setSaving(true);
    try {
      const payload = {
        name: bindingForm.name,
        aiProviderId: bindingForm.aiProviderId,
        provider: providers.find((provider) => provider.id === bindingForm.aiProviderId)?.providerType ?? "OpenAI",
        modelId: bindingForm.modelId,
        isDefault: bindingForm.isDefault,
        isActive: bindingForm.isActive,
        description: bindingForm.description || undefined,
      };

      if (bindingDialog && bindingDialog !== "new") {
        await updateModelConfig(bindingDialog.id, payload);
      } else {
        await createModelConfig(payload);
      }

      toast.success(t("admin.modelConfigs.toasts.generalConfigSaved"));
      setBindingDialog(null);
      await fetchData();
    } catch (error) {
      console.error(error);
      toast.error(t("admin.modelConfigs.toasts.generalConfigSaveFailed"));
    } finally {
      setSaving(false);
    }
  };

  const removeBinding = async (binding: ModelConfig) => {
    if (!window.confirm(t("admin.modelConfigs.confirmDelete", { name: binding.name }))) return;

    try {
      await deleteModelConfig(binding.id);
      toast.success(t("admin.modelConfigs.toasts.deleteSuccess"));
      await fetchData();
    } catch (error) {
      console.error(error);
      toast.error(t("admin.modelConfigs.toasts.deleteFailed"));
    }
  };

  if (loading) {
    return (
      <div className="flex h-64 items-center justify-center">
        <Loader2 className="h-8 w-8 animate-spin text-muted-foreground" />
      </div>
    );
  }

  return (
    <div className="space-y-5">
      <PageHeader
        title={t("admin.modelConfigs.title")}
        description={t("admin.modelConfigs.subtitle")}
      />

      <div className="grid gap-3 sm:grid-cols-3">
        <div className="rounded-lg border border-border/70 bg-card/70 p-4 shadow-sm">
          <div className="flex items-start justify-between gap-3">
            <div>
              <p className="text-xs text-muted-foreground">{t("admin.modelConfigs.heroStats.systemTasks")}</p>
              <p className="mt-2 text-2xl font-semibold tracking-tight tabular-nums">{configuredTaskCount}/4</p>
            </div>
            <span className="flex size-8 shrink-0 items-center justify-center rounded-md bg-muted text-muted-foreground">
              <Sparkles className="h-4 w-4" />
            </span>
          </div>
          <p className="mt-3 truncate text-xs text-muted-foreground">
            {t("admin.modelConfigs.featureCards.binding.description")}
          </p>
        </div>
        <div className="rounded-lg border border-border/70 bg-card/70 p-4 shadow-sm">
          <div className="flex items-start justify-between gap-3">
            <div>
              <p className="text-xs text-muted-foreground">{t("admin.modelConfigs.heroStats.availableProviders")}</p>
              <p className="mt-2 text-2xl font-semibold tracking-tight tabular-nums">{activeProviderCount}</p>
            </div>
            <span className="flex size-8 shrink-0 items-center justify-center rounded-md bg-muted text-muted-foreground">
              <ShieldCheck className="h-4 w-4" />
            </span>
          </div>
          <p className="mt-3 truncate text-xs text-muted-foreground">
            {t("admin.modelConfigs.featureCards.provider.description")}
          </p>
        </div>
        <div className="rounded-lg border border-border/70 bg-card/70 p-4 shadow-sm">
          <div className="flex items-start justify-between gap-3">
            <div>
              <p className="text-xs text-muted-foreground">{t("admin.modelConfigs.heroStats.modelMetadata")}</p>
              <p className="mt-2 text-2xl font-semibold tracking-tight tabular-nums">{models.length}</p>
            </div>
            <span className="flex size-8 shrink-0 items-center justify-center rounded-md bg-muted text-muted-foreground">
              <Boxes className="h-4 w-4" />
            </span>
          </div>
          <p className="mt-3 truncate text-xs text-muted-foreground">
            {t("admin.modelConfigs.featureCards.models.description")}
          </p>
        </div>
      </div>

      <section className="space-y-4">
        <div className="flex flex-col gap-3 md:flex-row md:items-center md:justify-between">
          <div>
            <div className="flex items-center gap-2">
              <Sparkles className="h-5 w-5 text-primary" />
              <h2 className="text-xl font-semibold">{t("admin.modelConfigs.systemBindings.title")}</h2>
            </div>
            <p className="mt-1 text-sm text-muted-foreground">
              {t("admin.modelConfigs.systemBindings.subtitle")}
            </p>
          </div>
          <div className="flex gap-2">
            <Button variant="outline" onClick={fetchData}>
              <RefreshCw className="mr-2 h-4 w-4" />
              {t("admin.modelConfigs.refresh")}
            </Button>
            <Button onClick={saveAllTaskBindings} disabled={dirtyTaskCount === 0 || savingTaskId !== null}>
              {savingTaskId === "all" ? (
                <Loader2 className="mr-2 h-4 w-4 animate-spin" />
              ) : (
                <Save className="mr-2 h-4 w-4" />
              )}
              {t("admin.modelConfigs.saveAll", { count: dirtyTaskCount })}
            </Button>
          </div>
        </div>

        <div className="grid gap-4 xl:grid-cols-2">
          {localizedTaskBindings.map((task) => {
            const Icon = task.icon;
            const current = taskBindings[task.id];
            const provider = providerById.get(current.providerId);
            const selectedModel = modelByProviderAndId.get(`${current.providerId}:${current.modelId}`);
            const providerModels = getModelsForProvider(current.providerId, current.modelId);
            const dirty = isTaskDirty(task.id);
            const missing = !current.providerId || !current.modelId;

            return (
              <Card
                key={task.id}
                className={cn(
                  "group overflow-hidden rounded-3xl border-border/70 bg-card/80 p-0 shadow-sm transition-all hover:-translate-y-0.5 hover:shadow-md",
                  dirty && "border-primary/40 ring-1 ring-primary/15"
                )}
              >
                <div className="grid gap-0 lg:grid-cols-[220px,1fr]">
                  <div className={cn("border-b p-5 lg:border-b-0 lg:border-r", task.tone)}>
                    <div className="flex items-center gap-3">
                      <div className="rounded-2xl border border-current/20 bg-background/70 p-3">
                        <Icon className="h-5 w-5" />
                      </div>
                      <div>
                        <Badge variant="outline" className="bg-background/60">
                          {task.group}
                        </Badge>
                        <h3 className="mt-2 font-semibold text-foreground">{task.title}</h3>
                      </div>
                    </div>
                    <p className="mt-4 text-sm text-muted-foreground">{task.description}</p>
                    <div className="mt-4 flex flex-wrap gap-2">
                      <Badge variant="secondary">{task.recommendation}</Badge>
                      {dirty && <Badge>{t("admin.modelConfigs.status.pendingSave")}</Badge>}
                      {missing && (
                        <Badge variant="outline" className="border-amber-500/40 text-amber-600">
                          {t("admin.modelConfigs.status.incomplete")}
                        </Badge>
                      )}
                    </div>
                  </div>

                  <div className="space-y-4 p-5">
                    <div className="grid gap-3 md:grid-cols-2">
                      <div className="space-y-2">
                        <label className="text-xs font-medium uppercase tracking-wide text-muted-foreground">
                          {t("admin.modelConfigs.fields.provider")}
                        </label>
                        <Select
                          value={current.providerId || undefined}
                          onValueChange={(value) => updateTaskProvider(task.id, value)}
                        >
                          <SelectTrigger className="h-11">
                            <SelectValue placeholder={t("admin.modelConfigs.fields.selectProvider")} />
                          </SelectTrigger>
                          <SelectContent className="max-h-80">
                            {providers
                              .filter((item) => item.isActive || item.id === current.providerId)
                              .map((item) => (
                                <SelectItem key={item.id} value={item.id}>
                                  <span className="flex items-center gap-2">
                                    <ProviderIcon builtinId={item.name} iconUrl={item.iconUrl} size={16} />
                                    <span>{getProviderLabel(item)}</span>
                                  </span>
                                </SelectItem>
                              ))}
                          </SelectContent>
                        </Select>
                      </div>

                      <div className="space-y-2">
                        <label className="text-xs font-medium uppercase tracking-wide text-muted-foreground">
                          {t("admin.modelConfigs.fields.model")}
                        </label>
                        <Select
                          value={current.modelId || undefined}
                          onValueChange={(value) => updateTaskModel(task.id, value)}
                          disabled={!current.providerId || providerModels.length === 0}
                        >
                          <SelectTrigger className="h-11">
                            <SelectValue placeholder={t("admin.modelConfigs.fields.selectModel")} />
                          </SelectTrigger>
                          <SelectContent className="max-h-80">
                            {providerModels.map((model) => (
                              <SelectItem key={model.id} value={model.modelId}>
                                <span className="flex items-center gap-2">
                                  <ModelIcon
                                    modelId={model.modelId}
                                    providerBuiltinId={providerById.get(model.providerId)?.name}
                                    size={16}
                                  />
                                  <span>{getModelLabel(model, model.modelId)}</span>
                                </span>
                              </SelectItem>
                            ))}
                          </SelectContent>
                        </Select>
                      </div>
                    </div>

                    <div className="rounded-2xl border border-border/70 bg-gradient-to-br from-muted/40 to-background p-4">
                      <div className="flex flex-col gap-3 md:flex-row md:items-center md:justify-between">
                        <div className="flex min-w-0 items-center gap-3">
                          <div className="flex h-11 w-11 shrink-0 items-center justify-center rounded-2xl bg-background shadow-sm">
                            {selectedModel ? (
                              <ModelIcon
                                modelId={selectedModel.modelId}
                                providerBuiltinId={provider?.name}
                                size={24}
                              />
                            ) : (
                              <Settings2 className="h-5 w-5 text-muted-foreground" />
                            )}
                          </div>
                          <div className="min-w-0">
                            <p className="truncate text-sm font-semibold">
                              {getModelLabel(selectedModel, current.modelId)}
                            </p>
                            <p className="truncate text-xs text-muted-foreground">
                              {getProviderLabel(provider)}
                              {provider?.hasApiKey ? ` · ${t("admin.modelConfigs.status.apiKeyConfigured")}` : ` · ${t("admin.modelConfigs.status.apiKeyMissing")}`}
                            </p>
                          </div>
                        </div>
                        <div className="flex items-center gap-2">
                          {missing ? (
                            <AlertCircle className="h-4 w-4 text-amber-500" />
                          ) : (
                            <CheckCircle2 className="h-4 w-4 text-emerald-500" />
                          )}
                          <span className="text-xs text-muted-foreground">
                            {missing ? t("admin.modelConfigs.status.waitingBinding") : t("admin.modelConfigs.status.runtimeResolve")}
                          </span>
                        </div>
                      </div>

                      <div className="mt-4 grid gap-2 text-xs md:grid-cols-3">
                        <div className="rounded-xl bg-background/70 px-3 py-2">
                          <p className="text-muted-foreground">{t("admin.modelConfigs.metrics.context")}</p>
                          <p className="font-medium">{formatCompactNumber(selectedModel?.contextWindow)}</p>
                        </div>
                        <div className="rounded-xl bg-background/70 px-3 py-2">
                          <p className="text-muted-foreground">{t("admin.modelConfigs.metrics.maxOutput")}</p>
                          <p className="font-medium">{formatCompactNumber(selectedModel?.maxOutputTokens)}</p>
                        </div>
                        <div className="rounded-xl bg-background/70 px-3 py-2">
                          <p className="text-muted-foreground">{t("admin.modelConfigs.metrics.price")}</p>
                          <p className="font-medium">
                            I {formatPrice(selectedModel?.inputTokenPrice)} / O {formatPrice(selectedModel?.outputTokenPrice)}
                          </p>
                        </div>
                      </div>

                      <div className="mt-3 flex flex-wrap gap-2">
                        {getCapabilityKeys(selectedModel).map((capability) => (
                          <Badge key={capability} variant="outline" className="bg-background/70">
                            {t(`admin.modelConfigs.capabilities.${capability}`)}
                          </Badge>
                        ))}
                      </div>
                    </div>

                    <div className="flex justify-end">
                      <Button
                        variant={dirty ? "default" : "outline"}
                        size="sm"
                        onClick={() => saveTaskBinding(task)}
                        disabled={!dirty || savingTaskId !== null}
                      >
                        {savingTaskId === task.id ? (
                          <Loader2 className="mr-2 h-4 w-4 animate-spin" />
                        ) : (
                          <Save className="mr-2 h-4 w-4" />
                        )}
                        {t("admin.modelConfigs.actions.saveBinding")}
                      </Button>
                    </div>
                  </div>
                </div>
              </Card>
            );
          })}
        </div>
      </section>

      <section className="space-y-4">
        <div className="flex flex-col gap-3 md:flex-row md:items-center md:justify-between">
          <div>
            <div className="flex items-center gap-2">
              <Settings2 className="h-5 w-5 text-primary" />
              <h2 className="text-xl font-semibold">{t("admin.modelConfigs.generalBindings.title")}</h2>
            </div>
            <p className="mt-1 text-sm text-muted-foreground">
              {t("admin.modelConfigs.generalBindings.subtitle")}
            </p>
          </div>
          <Button onClick={() => openBindingDialog()}>
            <Plus className="mr-2 h-4 w-4" />
            {t("admin.modelConfigs.generalBindings.create")}
          </Button>
        </div>

        {bindings.length === 0 ? (
          <Card className="flex min-h-44 items-center justify-center rounded-3xl border-dashed bg-muted/20">
            <div className="text-center">
              <Settings2 className="mx-auto h-10 w-10 text-muted-foreground" />
              <p className="mt-3 font-medium">{t("admin.modelConfigs.generalBindings.emptyTitle")}</p>
              <p className="text-sm text-muted-foreground">{t("admin.modelConfigs.generalBindings.emptyDescription")}</p>
            </div>
          </Card>
        ) : (
          <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
            {bindings.map((binding) => {
              const provider = providerById.get(binding.aiProviderId ?? "");
              const selectedModel = modelByProviderAndId.get(`${binding.aiProviderId}:${binding.modelId}`);

              return (
                <Card
                  key={binding.id}
                  className="group flex flex-col gap-4 rounded-3xl border-border/70 bg-card/80 p-5 shadow-sm transition-all hover:-translate-y-0.5 hover:shadow-md"
                >
                  <div className="flex items-start justify-between gap-3">
                    <div className="flex min-w-0 items-center gap-3">
                      <div className="flex h-11 w-11 shrink-0 items-center justify-center rounded-2xl bg-muted">
                        <ModelIcon
                          modelId={binding.modelId}
                          providerBuiltinId={provider?.name}
                          size={22}
                        />
                      </div>
                      <div className="min-w-0">
                        <h3 className="truncate font-semibold">{binding.name}</h3>
                        <p className="truncate text-xs text-muted-foreground">
                          {binding.aiProviderName || getProviderLabel(provider)}
                        </p>
                      </div>
                    </div>
                    <div className="flex flex-col items-end gap-1">
                      {binding.isDefault && <Badge>{t("admin.modelConfigs.generalBindings.labels.default")}</Badge>}
                      <Badge variant={binding.isActive ? "default" : "secondary"}>
                        {binding.isActive
                          ? t("admin.modelConfigs.generalBindings.labels.enabled")
                          : t("admin.modelConfigs.generalBindings.labels.disabled")}
                      </Badge>
                    </div>
                  </div>

                  <div className="rounded-2xl border bg-muted/30 p-3">
                    <p className="truncate font-mono text-xs">{binding.modelId}</p>
                    <div className="mt-2 flex flex-wrap gap-1.5">
                      {getCapabilityKeys(selectedModel).slice(0, 4).map((capability) => (
                        <Badge key={capability} variant="outline" className="text-[10px]">
                          {t(`admin.modelConfigs.capabilities.${capability}`)}
                        </Badge>
                      ))}
                    </div>
                  </div>

                  {binding.description && (
                    <p className="line-clamp-2 text-sm text-muted-foreground">{binding.description}</p>
                  )}

                  <div className="mt-auto flex items-center justify-between gap-2">
                    <Button variant="outline" size="sm" onClick={() => openBindingDialog(binding)}>
                      {t("admin.modelConfigs.actions.edit")}
                    </Button>
                    <Button variant="ghost" size="icon" onClick={() => removeBinding(binding)}>
                      <Trash2 className="h-4 w-4 text-red-500" />
                    </Button>
                  </div>
                </Card>
              );
            })}
          </div>
        )}
      </section>

      <Dialog open={!!bindingDialog} onOpenChange={() => setBindingDialog(null)}>
        <DialogContent className="max-w-2xl">
          <DialogHeader>
            <DialogTitle>
              {bindingDialog === "new"
                ? t("admin.modelConfigs.dialog.newGeneralTitle")
                : t("admin.modelConfigs.dialog.editGeneralTitle")}
            </DialogTitle>
            <DialogDescription>
              {t("admin.modelConfigs.dialog.generalDescription")}
            </DialogDescription>
          </DialogHeader>
          <div className="space-y-5">
            <div className="grid gap-4 md:grid-cols-2">
              <div className="space-y-2">
                <label className="text-sm font-medium">{t("admin.modelConfigs.dialog.nameLabel")}</label>
                <Input
                  placeholder={t("admin.modelConfigs.dialog.namePlaceholder")}
                  value={bindingForm.name}
                  onChange={(event) => setBindingForm({ ...bindingForm, name: event.target.value })}
                />
              </div>
              <div className="space-y-2">
                <label className="text-sm font-medium">{t("admin.modelConfigs.dialog.statusLabel")}</label>
                <div className="flex h-10 items-center gap-6 rounded-md border px-3">
                  <label className="flex items-center gap-2 text-sm">
                    <Switch
                      checked={bindingForm.isDefault}
                      onCheckedChange={(checked) => setBindingForm({ ...bindingForm, isDefault: checked })}
                    />
                    {t("admin.modelConfigs.dialog.defaultToggle")}
                  </label>
                  <label className="flex items-center gap-2 text-sm">
                    <Switch
                      checked={bindingForm.isActive}
                      onCheckedChange={(checked) => setBindingForm({ ...bindingForm, isActive: checked })}
                    />
                    {t("admin.modelConfigs.dialog.activeToggle")}
                  </label>
                </div>
              </div>
            </div>

            <div className="grid gap-4 md:grid-cols-2">
              <div className="space-y-2">
                <label className="text-sm font-medium">{t("admin.modelConfigs.dialog.providerLabel")}</label>
                <Select
                  value={bindingForm.aiProviderId || undefined}
                  onValueChange={(value) =>
                    setBindingForm({
                      ...bindingForm,
                      aiProviderId: value,
                      modelId: pickDefaultModelId(value),
                    })
                  }
                >
                  <SelectTrigger>
                    <SelectValue placeholder={t("admin.modelConfigs.dialog.providerPlaceholder")} />
                  </SelectTrigger>
                  <SelectContent className="max-h-80">
                    {providers
                      .filter((provider) => provider.isActive || provider.id === bindingForm.aiProviderId)
                      .map((provider) => (
                        <SelectItem key={provider.id} value={provider.id}>
                          <span className="flex items-center gap-2">
                            <ProviderIcon builtinId={provider.name} iconUrl={provider.iconUrl} size={16} />
                            {getProviderLabel(provider)}
                          </span>
                        </SelectItem>
                      ))}
                  </SelectContent>
                </Select>
              </div>

              <div className="space-y-2">
                <label className="text-sm font-medium">{t("admin.modelConfigs.dialog.modelLabel")}</label>
                <Select
                  value={bindingForm.modelId || undefined}
                  onValueChange={(value) => setBindingForm({ ...bindingForm, modelId: value })}
                  disabled={!bindingForm.aiProviderId || modelsForBinding.length === 0}
                >
                  <SelectTrigger>
                    <SelectValue placeholder={t("admin.modelConfigs.dialog.modelPlaceholder")} />
                  </SelectTrigger>
                  <SelectContent className="max-h-80">
                    {modelsForBinding.map((model) => (
                      <SelectItem key={model.id} value={model.modelId}>
                        <span className="flex items-center gap-2">
                          <ModelIcon
                            modelId={model.modelId}
                            providerBuiltinId={providerById.get(model.providerId)?.name}
                            size={16}
                          />
                          {getModelLabel(model, model.modelId)}
                        </span>
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              </div>
            </div>

            <div className="space-y-2">
              <label className="text-sm font-medium">{t("admin.modelConfigs.dialog.descriptionLabel")}</label>
              <Textarea
                placeholder={t("admin.modelConfigs.dialog.descriptionPlaceholder")}
                value={bindingForm.description}
                onChange={(event) => setBindingForm({ ...bindingForm, description: event.target.value })}
              />
            </div>
          </div>
          <DialogFooter>
            <Button variant="outline" onClick={() => setBindingDialog(null)}>
              {t("admin.modelConfigs.dialog.cancel")}
            </Button>
            <Button onClick={saveBinding} disabled={saving}>
              {saving && <Loader2 className="mr-2 h-4 w-4 animate-spin" />}
              {t("admin.modelConfigs.dialog.save")}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  );
}
