"use client";

import React, { useEffect, useState, useCallback, useMemo } from "react";
import { Card } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Switch } from "@/components/ui/switch";
import { Checkbox } from "@/components/ui/checkbox";
import { Label } from "@/components/ui/label";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import {
  DropdownMenu,
  DropdownMenuCheckboxItem,
  DropdownMenuContent,
  DropdownMenuLabel,
  DropdownMenuSub,
  DropdownMenuSubContent,
  DropdownMenuSubTrigger,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { ScrollArea } from "@/components/ui/scroll-area";
import {
  AiModelConfig,
  AiProviderConfig,
  createAiModel,
  discoverAiModels,
  getAiModels,
  getAiProviders,
  getChatAssistantConfig,
  updateChatAssistantConfig,
  ChatAssistantConfigOptions,
  SelectableModelItem,
} from "@/lib/admin-api";
import { ModelIcon, ProviderIcon } from "@/components/admin/provider-icons";
import { cn } from "@/lib/utils";
import {
  Loader2,
  RefreshCw,
  Save,
  Bot,
  ChevronRight,
  Wrench,
  Sparkles,
  X,
} from "lucide-react";
import { toast } from "sonner";
import { useTranslations } from "@/hooks/use-translations";
import { PageHeader } from "@/components/admin/page-header";

function getModelDisplayName(model: SelectableModelItem): string {
  return model.modelDisplayName || model.modelName || model.modelId || model.name;
}

function getProviderDisplayName(model: SelectableModelItem, unboundProviderLabel: string): string {
  return model.aiProviderName || unboundProviderLabel;
}

function getModelProviderKey(model: SelectableModelItem, unboundProviderLabel: string): string {
  return model.aiProviderId || `${model.aiProviderType || "legacy"}:${getProviderDisplayName(model, unboundProviderLabel)}`;
}

function createDirectModelSelectionId(providerId: string, modelId: string): string {
  return `ai-model:${providerId}:${modelId}`;
}

function formatContextWindow(
  value: number | undefined,
  contextNotSetLabel: string,
  contextUnitLabel: string
): string {
  if (!value) return contextNotSetLabel;
  if (value >= 1_000_000) return `${Number((value / 1_000_000).toFixed(1))}M ${contextUnitLabel}`;
  if (value >= 1_000) return `${Math.round(value / 1_000)}K ${contextUnitLabel}`;
  return `${value} ${contextUnitLabel}`;
}

function getCapabilityLabels(
  model: SelectableModelItem,
  capabilityLabels: {
    thinking: string;
    vision: string;
    tools: string;
    json: string;
    chat: string;
  }
): string[] {
  const result: string[] = [];
  if (model.supportsThinking) result.push(capabilityLabels.thinking);
  if (model.supportsVision) result.push(capabilityLabels.vision);
  if (model.supportsTools) result.push(capabilityLabels.tools);
  if (model.supportsJsonMode) result.push(capabilityLabels.json);
  return result.length > 0 ? result : [capabilityLabels.chat];
}

type ModelGroup = {
  key: string;
  providerId?: string;
  providerName: string;
  providerType?: string;
  isActive: boolean;
  builtinId?: string;
  iconUrl?: string;
  supportsModelDiscovery?: boolean;
  hasApiKey?: boolean;
  models: SelectableModelItem[];
};

export default function AdminChatAssistantPage() {
  const [configOptions, setConfigOptions] = useState<ChatAssistantConfigOptions | null>(null);
  const [aiProviders, setAiProviders] = useState<AiProviderConfig[]>([]);
  const [aiModels, setAiModels] = useState<AiModelConfig[]>([]);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [discoveringProviderId, setDiscoveringProviderId] = useState<string | null>(null);
  const t = useTranslations();
  const unboundProviderLabel = t("admin.chatAssistant.unboundProvider");
  const contextNotSetLabel = t("admin.chatAssistant.contextNotSet");
  const contextUnitLabel = t("admin.chatAssistant.contextUnit");
  const capabilityLabels = useMemo(
    () => ({
      thinking: t("admin.chatAssistant.capabilities.thinking"),
      vision: t("admin.chatAssistant.capabilities.vision"),
      tools: t("admin.chatAssistant.capabilities.tools"),
      json: t("admin.chatAssistant.capabilities.json"),
      chat: t("admin.chatAssistant.capabilities.chat"),
    }),
    [t]
  );

  // 编辑状态
  const [isEnabled, setIsEnabled] = useState(false);
  const [selectedModelIds, setSelectedModelIds] = useState<string[]>([]);
  const [selectedMcpIds, setSelectedMcpIds] = useState<string[]>([]);
  const [selectedSkillIds, setSelectedSkillIds] = useState<string[]>([]);
  const [defaultModelId, setDefaultModelId] = useState<string | undefined>(undefined);

  const loadData = useCallback(() => {
    return Promise.all([
        getChatAssistantConfig(),
        getAiProviders().catch(() => []),
        getAiModels().catch(() => []),
      ]).then(([result, providers, models]) => {
      setConfigOptions(result);
      setAiProviders(providers);
      setAiModels(models);
      // 初始化编辑状态
      setIsEnabled(result.config.isEnabled);
      setSelectedModelIds(result.config.enabledModelIds);
      setSelectedMcpIds(result.config.enabledMcpIds);
      setSelectedSkillIds(result.config.enabledSkillIds);
      setDefaultModelId(result.config.defaultModelId);
    }).catch(error => {
      console.error("Failed to fetch chat assistant config:", error);
      toast.error(t('admin.toast.fetchConfigFailed'));
    }).finally(() => {
      setLoading(false);
    });
  }, [t, setDefaultModelId]);

  const fetchData = useCallback(() => {
    setLoading(true);
    return loadData();
  }, [loadData]);

  useEffect(() => {
    void loadData();
  }, [loadData]);

  const handleSave = async () => {
    setSaving(true);
    try {
      const nextDefaultModelId = defaultModelId && selectedModelIds.includes(defaultModelId)
        ? defaultModelId
        : undefined;
      await updateChatAssistantConfig({
        isEnabled,
        enabledModelIds: selectedModelIds,
        enabledMcpIds: selectedMcpIds,
        enabledSkillIds: selectedSkillIds,
        defaultModelId: nextDefaultModelId,
        enableImageUpload: configOptions?.config.enableImageUpload ?? false,
      });
      toast.success(t('admin.toast.configSaveSuccess'));
      fetchData();
    } catch (error) {
      console.error("Failed to save config:", error);
      toast.error(t('admin.toast.configSaveFailed'));
    } finally {
      setSaving(false);
    }
  };

  const toggleModel = (id: string) => {
    const isSelected = selectedModelIds.includes(id);
    const nextModelIds = isSelected
      ? selectedModelIds.filter((modelId) => modelId !== id)
      : [...selectedModelIds, id];

    setSelectedModelIds(nextModelIds);
    setDefaultModelId((current) => {
      if (isSelected && current === id) {
        return nextModelIds[0];
      }

      if (!isSelected && (!current || !nextModelIds.includes(current))) {
        return id;
      }

      return current;
    });
  };

  const toggleMcp = (id: string) => {
    setSelectedMcpIds((prev) =>
      prev.includes(id) ? prev.filter((i) => i !== id) : [...prev, id]
    );
  };

  const toggleSkill = (id: string) => {
    setSelectedSkillIds((prev) =>
      prev.includes(id) ? prev.filter((i) => i !== id) : [...prev, id]
    );
  };

  const providerById = useMemo(
    () => new Map(aiProviders.map((provider) => [provider.id, provider])),
    [aiProviders]
  );
  const activeProviderIds = useMemo(
    () => new Set(aiProviders.filter((provider) => provider.isActive).map((provider) => provider.id)),
    [aiProviders]
  );
  const directModelItems = useMemo<SelectableModelItem[]>(() => {
    return aiModels.flatMap((model) => {
      const provider = providerById.get(model.providerId);
      if (!provider?.isActive) {
        return [];
      }

      const providerName = provider?.displayName || provider?.name || model.providerName || unboundProviderLabel;
      const modelName = model.displayName || model.name || model.modelId;

      return [{
        id: createDirectModelSelectionId(model.providerId, model.modelId),
        name: `${providerName} / ${modelName}`,
        description: model.description,
        isActive: model.isActive && (provider?.isActive ?? true),
        isSelected: selectedModelIds.includes(createDirectModelSelectionId(model.providerId, model.modelId)),
        aiProviderId: model.providerId,
        aiProviderName: providerName,
        aiProviderType: provider?.providerType,
        aiProviderIsActive: provider?.isActive ?? true,
        modelId: model.modelId,
        modelName: model.name,
        modelDisplayName: model.displayName,
        contextWindow: model.contextWindow,
        supportsThinking: model.supportsThinking,
        supportsVision: model.supportsVision,
        supportsTools: model.supportsTools,
        supportsJsonMode: model.supportsJsonMode,
      }];
    });
  }, [aiModels, providerById, selectedModelIds, unboundProviderLabel]);
  const availableModels = useMemo(
    () => {
      const byId = new Map<string, SelectableModelItem>();
      directModelItems.forEach((model) => byId.set(model.id, model));
      (configOptions?.availableModels ?? []).forEach((model) => {
        if (!byId.has(model.id)) {
          byId.set(model.id, model);
        }
      });
      return Array.from(byId.values());
    },
    [configOptions?.availableModels, directModelItems]
  );
  const selectedModels = useMemo(
    () => availableModels.filter((model) => selectedModelIds.includes(model.id)),
    [availableModels, selectedModelIds]
  );
  const modelGroups = useMemo<ModelGroup[]>(() => {
    const groups = new Map<string, ModelGroup>();

    aiProviders.filter((provider) => provider.isActive).forEach((provider) => {
      groups.set(provider.id, {
        key: provider.id,
        providerId: provider.id,
        providerName: provider.displayName || provider.name,
        providerType: provider.providerType,
        isActive: provider.isActive,
        builtinId: provider.name,
        iconUrl: provider.iconUrl,
        supportsModelDiscovery: provider.supportsModelDiscovery,
        hasApiKey: provider.hasApiKey,
        models: [],
      });
    });

    availableModels.forEach((model) => {
      if (!model.aiProviderId || !activeProviderIds.has(model.aiProviderId)) {
        return;
      }

      const key = getModelProviderKey(model, unboundProviderLabel);
      const current = groups.get(key);
      if (current) {
        current.models.push(model);
      }
    });

    return Array.from(groups.values()).sort((left, right) =>
      left.providerName.localeCompare(right.providerName, "zh-CN")
    );
  }, [activeProviderIds, aiProviders, availableModels, unboundProviderLabel]);

  const handleDiscoverModels = async (group: ModelGroup) => {
    if (!group.providerId) return;

    setDiscoveringProviderId(group.providerId);
    try {
      const discovered = await discoverAiModels(group.providerId);
      const existingIds = new Set(
        aiModels
          .filter((model) => model.providerId === group.providerId)
          .map((model) => model.modelId.toLowerCase())
      );
      const missing = discovered.filter((model) => !existingIds.has(model.modelId.toLowerCase()));

      await Promise.all(
        missing.map((model) => createAiModel({
          providerId: group.providerId!,
          modelId: model.modelId,
          name: model.name || model.modelId,
          displayName: model.displayName,
          modelType: model.modelType || "chat",
          contextWindow: model.contextWindow,
          maxOutputTokens: model.maxOutputTokens,
          inputTokenPrice: model.inputTokenPrice,
          outputTokenPrice: model.outputTokenPrice,
          supportsThinking: model.supportsThinking,
          supportsVision: model.supportsVision,
          supportsTools: model.supportsTools ?? true,
          supportsJsonMode: model.supportsJsonMode,
          isDefault: false,
          isActive: true,
          capabilitiesJson: model.capabilitiesJson,
          thinkingConfigJson: model.thinkingConfigJson,
          requestOverridesJson: model.requestOverridesJson,
          tagsJson: model.tagsJson,
          description: model.description,
          sortOrder: 0,
        }))
      );

      toast.success(
        t("admin.chatAssistant.discoverModelsSuccess", {
          discovered: discovered.length,
          created: missing.length,
        })
      );
      await fetchData();
    } catch (error) {
      console.error("Failed to discover models:", error);
      toast.error(t("admin.chatAssistant.discoverModelsFailed"));
    } finally {
      setDiscoveringProviderId(null);
    }
  };

  const hasChanges = configOptions && (
    isEnabled !== configOptions.config.isEnabled ||
    JSON.stringify([...selectedModelIds].sort()) !== JSON.stringify([...configOptions.config.enabledModelIds].sort()) ||
    JSON.stringify([...selectedMcpIds].sort()) !== JSON.stringify([...configOptions.config.enabledMcpIds].sort()) ||
    JSON.stringify([...selectedSkillIds].sort()) !== JSON.stringify([...configOptions.config.enabledSkillIds].sort()) ||
    defaultModelId !== configOptions.config.defaultModelId
  );

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
        title={t('admin.chatAssistant.title')}
        actions={
          <>
            <Button variant="outline" onClick={fetchData}>
              <RefreshCw className="mr-1.5 h-4 w-4" />
              {t('admin.common.refresh')}
            </Button>
            <Button onClick={handleSave} disabled={saving || !hasChanges}>
              {saving ? (
                <Loader2 className="mr-1.5 h-4 w-4 animate-spin" />
              ) : (
                <Save className="mr-1.5 h-4 w-4" />
              )}
              {t('admin.chatAssistant.saveConfig')}
            </Button>
          </>
        }
      />

      {/* 启用开关 */}
      <Card className="p-6">
        <div className="flex items-center justify-between">
          <div className="space-y-1">
            <Label className="text-base font-medium">{t('admin.chatAssistant.enableAssistant')}</Label>
            <p className="text-sm text-muted-foreground">
              {t('admin.chatAssistant.enableDesc')}
            </p>
          </div>
          <Switch checked={isEnabled} onCheckedChange={setIsEnabled} />
        </div>
      </Card>

      {/* 模型配置 */}
      <Card className="p-6">
        <div className="space-y-4">
          <div className="flex items-center gap-2">
            <Bot className="h-5 w-5" />
            <h2 className="text-lg font-semibold">{t('admin.chatAssistant.availableModels')}</h2>
          </div>
          <p className="text-sm text-muted-foreground">
            {t('admin.chatAssistant.selectModelsDesc')}
          </p>

          {modelGroups.length === 0 ? (
            <p className="text-sm text-muted-foreground py-4">
              {t("admin.chatAssistant.noProviders")}
            </p>
          ) : (
            <div className="space-y-4">
              <DropdownMenu>
                <DropdownMenuTrigger asChild>
                  <Button
                    type="button"
                    variant="outline"
                    className="h-auto min-h-11 w-full justify-between gap-3 px-4 py-3 md:max-w-xl"
                  >
                    <span className="flex min-w-0 items-center gap-2">
                      <Bot className="h-4 w-4 shrink-0" />
                      <span className="truncate">
                        {selectedModels.length > 0
                          ? t("admin.chatAssistant.selectedModelsCount", { count: selectedModels.length })
                          : t("admin.chatAssistant.selectProvidersAndModels")}
                      </span>
                    </span>
                    <ChevronRight className="h-4 w-4 shrink-0 text-muted-foreground" />
                  </Button>
                </DropdownMenuTrigger>
                <DropdownMenuContent
                  align="start"
                  sideOffset={8}
                  className="w-64 overflow-visible p-0"
                >
                  <DropdownMenuLabel className="border-b px-3 py-2 text-xs font-medium text-muted-foreground">
                    {t("admin.chatAssistant.providersLabel")}
                  </DropdownMenuLabel>
                  <ScrollArea type="always" className="h-[360px]">
                    <div className="p-1.5">
                      {modelGroups.map((group) => (
                        <DropdownMenuSub key={group.key}>
                          <DropdownMenuSubTrigger className="gap-2 px-2.5 py-2">
                            <ProviderIcon
                              builtinId={group.builtinId || group.providerName}
                              iconUrl={group.iconUrl}
                              size={16}
                            />
                            <span className="min-w-0 flex-1 truncate">{group.providerName}</span>
                            <span className="ml-auto text-xs text-muted-foreground">{group.models.length}</span>
                          </DropdownMenuSubTrigger>
                          <DropdownMenuSubContent
                            sideOffset={4}
                            alignOffset={-4}
                            className="w-[min(440px,calc(100vw-2rem))] overflow-hidden p-0"
                          >
                            <div className="flex items-center justify-between gap-3 border-b px-4 py-2.5">
                              <div className="min-w-0">
                                <div className="truncate text-sm font-medium">
                                  {group.providerName}
                                </div>
                                <div className="mt-0.5 text-xs text-muted-foreground">
                                  {t("admin.chatAssistant.selectProviderModels")}
                                </div>
                              </div>
                              {group.providerId && group.supportsModelDiscovery && (
                                <Button
                                  type="button"
                                  size="sm"
                                  variant="outline"
                                  onClick={(event) => {
                                    event.preventDefault();
                                    event.stopPropagation();
                                    handleDiscoverModels(group);
                                  }}
                                  disabled={discoveringProviderId === group.providerId}
                                >
                                  {discoveringProviderId === group.providerId ? (
                                    <Loader2 className="mr-2 h-4 w-4 animate-spin" />
                                  ) : (
                                    <RefreshCw className="mr-2 h-4 w-4" />
                                  )}
                                  {t("admin.chatAssistant.discoverModels")}
                                </Button>
                              )}
                            </div>

                            <ScrollArea type="always" className="h-[360px]">
                              {group.models.length === 0 ? (
                                <div className="p-4 text-sm text-muted-foreground">
                                  {t("admin.chatAssistant.noProviderModels")}
                                </div>
                              ) : (
                                <div className="divide-y">
                                  {group.models.map((model) => {
                                    const selected = selectedModelIds.includes(model.id);
                                    const displayName = getModelDisplayName(model);

                                    return (
                                      <DropdownMenuCheckboxItem
                                        key={model.id}
                                        checked={selected}
                                        className={cn(
                                          "cursor-pointer items-start gap-3 rounded-none px-4 py-3 pl-8",
                                          selected ? "bg-primary/5" : "hover:bg-muted/40"
                                        )}
                                        onSelect={(event) => {
                                          event.preventDefault();
                                          toggleModel(model.id);
                                        }}
                                      >
                                        <span className="mt-0.5 flex size-10 shrink-0 items-center justify-center rounded-full border bg-background">
                                          <ModelIcon
                                            modelId={model.modelId}
                                            providerBuiltinId={model.aiProviderName}
                                            size={18}
                                          />
                                        </span>
                                        <div className="min-w-0 flex-1">
                                          <div className="truncate text-sm font-medium">{displayName}</div>
                                          <div className="mt-1 flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
                                            <span>{model.modelId}</span>
                                            <span>{formatContextWindow(model.contextWindow, contextNotSetLabel, contextUnitLabel)}</span>
                                            {getCapabilityLabels(model, capabilityLabels).map((label) => (
                                              <span key={label} className="rounded-full bg-muted px-1.5 py-0.5">
                                                {label}
                                              </span>
                                            ))}
                                          </div>
                                          {model.description && (
                                            <p className="mt-1 line-clamp-2 text-xs text-muted-foreground">
                                              {model.description}
                                            </p>
                                          )}
                                        </div>
                                        {!model.isActive && (
                                          <span className="mt-1 shrink-0 rounded-full bg-yellow-100 px-2 py-0.5 text-xs text-yellow-700 dark:bg-yellow-950 dark:text-yellow-300">
                                            {t('admin.chatAssistant.inactive')}
                                          </span>
                                        )}
                                      </DropdownMenuCheckboxItem>
                                    );
                                  })}
                                </div>
                              )}
                            </ScrollArea>
                          </DropdownMenuSubContent>
                        </DropdownMenuSub>
                      ))}
                    </div>
                  </ScrollArea>
                </DropdownMenuContent>
              </DropdownMenu>

              {selectedModels.length > 0 && (
                <div className="rounded-lg border bg-muted/10 p-3">
                  <div className="mb-2 flex items-center justify-between gap-3">
                    <Label className="text-sm font-medium">{t("admin.chatAssistant.enabledModels")}</Label>
                    <span className="text-xs text-muted-foreground">
                      {t("admin.chatAssistant.enabledModelCount", { count: selectedModels.length })}
                    </span>
                  </div>
                  <div className="flex flex-wrap gap-2">
                    {selectedModels.map((model) => (
                      <span
                        key={model.id}
                        className="inline-flex max-w-full items-center gap-2 rounded-md border bg-background px-2.5 py-1.5 text-sm"
                      >
                        <ModelIcon
                          modelId={model.modelId}
                          providerBuiltinId={model.aiProviderName}
                          size={16}
                        />
                        <span className="min-w-0 truncate">
                          {getProviderDisplayName(model, unboundProviderLabel)} / {getModelDisplayName(model)}
                        </span>
                        <button
                          type="button"
                          className="rounded p-0.5 text-muted-foreground hover:bg-muted hover:text-foreground"
                          aria-label={t("admin.chatAssistant.removeModel", { model: getModelDisplayName(model) })}
                          onClick={() => toggleModel(model.id)}
                        >
                          <X className="h-3.5 w-3.5" />
                        </button>
                      </span>
                    ))}
                  </div>
                </div>
              )}
            </div>
          )}

          {/* 默认模型选择 */}
          {selectedModels.length > 0 && (
            <div className="pt-4 border-t">
              <Label className="text-sm font-medium">{t('admin.chatAssistant.defaultModel')}</Label>
              <p className="text-xs text-muted-foreground mb-2">
                {t('admin.chatAssistant.defaultModelDesc')}
              </p>
              <Select value={defaultModelId} onValueChange={setDefaultModelId}>
                <SelectTrigger className="w-full max-w-xs">
                  <SelectValue placeholder={t('admin.chatAssistant.selectDefaultModel')} />
                </SelectTrigger>
                <SelectContent>
                  {selectedModels.map((model) => (
                    <SelectItem key={model.id} value={model.id}>
                      {getProviderDisplayName(model, unboundProviderLabel)} / {getModelDisplayName(model)}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>
          )}
        </div>
      </Card>

      {/* MCP配置 */}
      <Card className="p-6">
        <div className="space-y-4">
          <div className="flex items-center gap-2">
            <Wrench className="h-5 w-5" />
            <h2 className="text-lg font-semibold">{t('admin.chatAssistant.availableMcps')}</h2>
          </div>
          <p className="text-sm text-muted-foreground">
            {t('admin.chatAssistant.selectMcpsDesc')}
          </p>

          {configOptions?.availableMcps.length === 0 ? (
            <p className="text-sm text-muted-foreground py-4">
              {t('admin.chatAssistant.noMcps')}
            </p>
          ) : (
            <div className="grid gap-3">
              {configOptions?.availableMcps.map((mcp) => (
                <div
                  key={mcp.id}
                  className="flex items-center space-x-3 rounded-lg border p-3"
                >
                  <Checkbox
                    id={`mcp-${mcp.id}`}
                    checked={selectedMcpIds.includes(mcp.id)}
                    onCheckedChange={() => toggleMcp(mcp.id)}
                  />
                  <div className="flex-1">
                    <Label
                      htmlFor={`mcp-${mcp.id}`}
                      className="text-sm font-medium cursor-pointer"
                    >
                      {mcp.name}
                    </Label>
                    {mcp.description && (
                      <p className="text-xs text-muted-foreground">
                        {mcp.description}
                      </p>
                    )}
                  </div>
                  {!mcp.isActive && (
                    <span className="text-xs text-yellow-600 bg-yellow-100 px-2 py-0.5 rounded">
                      {t('admin.chatAssistant.inactive')}
                    </span>
                  )}
                </div>
              ))}
            </div>
          )}
        </div>
      </Card>

      {/* Skills配置 */}
      <Card className="p-6">
        <div className="space-y-4">
          <div className="flex items-center gap-2">
            <Sparkles className="h-5 w-5" />
            <h2 className="text-lg font-semibold">{t('admin.chatAssistant.availableSkills')}</h2>
          </div>
          <p className="text-sm text-muted-foreground">
            {t('admin.chatAssistant.selectSkillsDesc')}
          </p>

          {configOptions?.availableSkills.length === 0 ? (
            <p className="text-sm text-muted-foreground py-4">
              {t('admin.chatAssistant.noSkills')}
            </p>
          ) : (
            <div className="grid gap-3">
              {configOptions?.availableSkills.map((skill) => (
                <div
                  key={skill.id}
                  className="flex items-center space-x-3 rounded-lg border p-3"
                >
                  <Checkbox
                    id={`skill-${skill.id}`}
                    checked={selectedSkillIds.includes(skill.id)}
                    onCheckedChange={() => toggleSkill(skill.id)}
                  />
                  <div className="flex-1">
                    <Label
                      htmlFor={`skill-${skill.id}`}
                      className="text-sm font-medium cursor-pointer"
                    >
                      {skill.name}
                    </Label>
                    {skill.description && (
                      <p className="text-xs text-muted-foreground">
                        {skill.description}
                      </p>
                    )}
                  </div>
                  {!skill.isActive && (
                    <span className="text-xs text-yellow-600 bg-yellow-100 px-2 py-0.5 rounded">
                      {t('admin.chatAssistant.inactive')}
                    </span>
                  )}
                </div>
              ))}
            </div>
          )}
        </div>
      </Card>

      {/* 浮动保存按钮 */}
      {hasChanges && (
        <div className="fixed bottom-6 right-6">
          <Button onClick={handleSave} disabled={saving} size="lg" className="shadow-lg">
            {saving ? (
              <Loader2 className="mr-2 h-4 w-4 animate-spin" />
            ) : (
              <Save className="mr-2 h-4 w-4" />
            )}
            {t('admin.chatAssistant.saveConfig')}
          </Button>
        </div>
      )}
    </div>
  );
}
