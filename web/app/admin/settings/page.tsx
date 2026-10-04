"use client";

import React, { useCallback, useEffect, useMemo, useState } from "react";
import Link from "next/link";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Switch } from "@/components/ui/switch";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Textarea } from "@/components/ui/textarea";
import { cn } from "@/lib/utils";
import {
  getSettings,
  updateSettings,
  SystemSetting,
} from "@/lib/admin-api";
import {
  AlertCircle,
  ArrowUpRight,
  Bot,
  CheckCircle2,
  Eye,
  EyeOff,
  Globe,
  Github,
  Loader2,
  RefreshCw,
  RotateCcw,
  Save,
  Search,
  Settings,
  Shield,
  SlidersHorizontal,
  X,
  type LucideIcon,
} from "lucide-react";
import { toast } from "sonner";
import { useTranslations } from "@/hooks/use-translations";
import { PageHeader } from "@/components/admin/page-header";

type SettingGroupId = "default" | "aiContent" | "aiCatalog" | "aiTranslation" | "aiRuntime" | "aiOther";

interface SettingGroup {
  id: SettingGroupId;
  settings: SystemSetting[];
}

interface CategoryMeta {
  label: string;
  description: string;
  icon: LucideIcon;
  tone: string;
}

const CATEGORY_ORDER = ["general", "ai", "github", "security"];

const HIDDEN_AI_SETTING_KEYS = new Set([
  "WIKI_CATALOG_PROVIDER_ID",
  "WIKI_CATALOG_MODEL_ID",
  "WIKI_CATALOG_MODEL",
  "WIKI_CATALOG_ENDPOINT",
  "WIKI_CATALOG_API_KEY",
  "WIKI_CATALOG_REQUEST_TYPE",
  "WIKI_CONTENT_PROVIDER_ID",
  "WIKI_CONTENT_MODEL_ID",
  "WIKI_CONTENT_MODEL",
  "WIKI_CONTENT_ENDPOINT",
  "WIKI_CONTENT_API_KEY",
  "WIKI_CONTENT_REQUEST_TYPE",
  "WIKI_TRANSLATION_PROVIDER_ID",
  "WIKI_TRANSLATION_MODEL_ID",
  "WIKI_TRANSLATION_MODEL",
  "WIKI_TRANSLATION_ENDPOINT",
  "WIKI_TRANSLATION_API_KEY",
  "WIKI_TRANSLATION_REQUEST_TYPE",
  "GRAPHIFY_PROVIDER_ID",
  "GRAPHIFY_MODEL_ID",
  "GRAPHIFY_MODEL",
]);

const NUMERIC_KEY_PATTERN = /(COUNT|LIMIT|LENGTH|TIMEOUT|INTERVAL|PARALLEL|MAX|MIN|TOKENS|DEPTH|SIZE|DAYS)/i;
const SENSITIVE_KEY_PATTERN = /(API_KEY|SECRET|PASSWORD|TOKEN|PRIVATE_KEY)/i;

function createEditedValues(items: SystemSetting[]) {
  return items.reduce<Record<string, string>>((values, setting) => {
    values[setting.key] = setting.value ?? "";
    return values;
  }, {});
}

function formatSettingLabel(key: string) {
  return key
    .toLowerCase()
    .split("_")
    .map((chunk) => chunk.charAt(0).toUpperCase() + chunk.slice(1))
    .join(" ");
}

function getOptionalTranslation(
  t: (key: string, params?: Record<string, string | number | boolean | Date | null | undefined>) => string,
  key: string
) {
  const translated = t(key);
  return translated === key ? undefined : translated;
}

function isTemplateSetting(setting: SystemSetting, value: string) {
  const lowerKey = setting.key.toLowerCase();
  return value.length > 100 || lowerKey.includes("template") || lowerKey.includes("prompt");
}

function isSensitiveSetting(setting: SystemSetting) {
  return SENSITIVE_KEY_PATTERN.test(setting.key);
}

function isBooleanSetting(setting: SystemSetting) {
  const originalValue = (setting.value ?? "").trim().toLowerCase();
  return originalValue === "true" || originalValue === "false";
}

function isNumericSetting(setting: SystemSetting, value: string) {
  const trimmedValue = value.trim();
  return NUMERIC_KEY_PATTERN.test(setting.key) && (trimmedValue === "" || /^-?\d+(\.\d+)?$/.test(trimmedValue));
}

function groupSettingsByCategory(categories: string[], source: SystemSetting[]) {
  return categories.reduce<Record<string, SystemSetting[]>>((acc, category) => {
    acc[category] = source.filter((setting) => setting.category === category);
    return acc;
  }, {});
}

export default function AdminSettingsPage() {
  const [settings, setSettings] = useState<SystemSetting[]>([]);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [activeCategory, setActiveCategory] = useState("general");
  const [editedValues, setEditedValues] = useState<Record<string, string>>({});
  const [searchTerm, setSearchTerm] = useState("");
  const [visibleSecrets, setVisibleSecrets] = useState<Record<string, boolean>>({});
  const t = useTranslations();

  const categoryMeta = useMemo<Record<string, CategoryMeta>>(
    () => ({
      general: {
        label: t("admin.settings.general"),
        description: t("admin.settings.generalDescription"),
        icon: Globe,
        tone: "border-sky-500/20 bg-sky-500/10 text-sky-700 dark:text-sky-300",
      },
      ai: {
        label: t("admin.settings.ai"),
        description: t("admin.settings.aiDescription"),
        icon: Bot,
        tone: "border-violet-500/20 bg-violet-500/10 text-violet-700 dark:text-violet-300",
      },
      github: {
        label: t("admin.settings.github"),
        description: t("admin.settings.githubDescription"),
        icon: Github,
        tone: "border-slate-500/20 bg-slate-500/10 text-slate-700 dark:text-slate-300",
      },
      security: {
        label: t("admin.settings.security"),
        description: t("admin.settings.securityDescription"),
        icon: Shield,
        tone: "border-emerald-500/20 bg-emerald-500/10 text-emerald-700 dark:text-emerald-300",
      },
    }),
    [t]
  );

  const getCategoryMeta = useCallback(
    (category: string): CategoryMeta => {
      return (
        categoryMeta[category] ?? {
          label: formatSettingLabel(category),
          description: t("admin.settings.categoryFallbackDescription"),
          icon: Settings,
          tone: "border-border bg-muted/40 text-muted-foreground",
        }
      );
    },
    [categoryMeta, t]
  );

  const getSettingLabel = useCallback(
    (setting: SystemSetting) => {
      return getOptionalTranslation(t, `admin.settings.items.${setting.key}.label`) ?? formatSettingLabel(setting.key);
    },
    [t]
  );

  const getSettingDescription = useCallback(
    (setting: SystemSetting) => {
      return getOptionalTranslation(t, `admin.settings.items.${setting.key}.description`) ?? setting.description;
    },
    [t]
  );

  const resolveAiGroupId = useCallback((key: string): SettingGroupId => {
    const upperKey = key.toUpperCase();
    if (upperKey.includes("TRANSLATION")) return "aiTranslation";
    if (upperKey.includes("CONTENT_") || upperKey === "WIKI_MAX_OUTPUT_TOKENS") return "aiContent";
    if (
      upperKey.includes("CATALOG") ||
      upperKey.includes("DIRECTORY_TREE") ||
      upperKey === "WIKI_LANGUAGES" ||
      upperKey === "WIKI_PROMPTS_DIRECTORY" ||
      upperKey === "WIKI_README_MAX_LENGTH"
    ) {
      return "aiCatalog";
    }
    if (upperKey.includes("PARALLEL") || upperKey.includes("RETRY") || upperKey.includes("TIMEOUT")) {
      return "aiRuntime";
    }
    return "aiOther";
  }, []);

  const getGroupMeta = useCallback(
    (category: string, groupId: SettingGroupId) => {
      if (category !== "ai") {
        return {
          title: t("admin.settings.groupTitles.default"),
          description: t("admin.settings.groupDescriptions.default"),
        };
      }

      switch (groupId) {
        case "aiContent":
          return {
            title: t("admin.settings.groupTitles.aiContent"),
            description: t("admin.settings.groupDescriptions.aiContent"),
          };
        case "aiCatalog":
          return {
            title: t("admin.settings.groupTitles.aiCatalog"),
            description: t("admin.settings.groupDescriptions.aiCatalog"),
          };
        case "aiTranslation":
          return {
            title: t("admin.settings.groupTitles.aiTranslation"),
            description: t("admin.settings.groupDescriptions.aiTranslation"),
          };
        case "aiRuntime":
          return {
            title: t("admin.settings.groupTitles.aiRuntime"),
            description: t("admin.settings.groupDescriptions.aiRuntime"),
          };
        case "aiOther":
          return {
            title: t("admin.settings.groupTitles.aiOther"),
            description: t("admin.settings.groupDescriptions.aiOther"),
          };
        default:
          return {
            title: t("admin.settings.groupTitles.default"),
            description: t("admin.settings.groupDescriptions.default"),
          };
      }
    },
    [t]
  );

  const loadData = useCallback(
    () => {
      return getSettings().then(settingsResult => {
        const result = settingsResult.filter((setting) => !HIDDEN_AI_SETTING_KEYS.has(setting.key));
        setSettings(result);
        setEditedValues(createEditedValues(result));
      }).catch(error => {
        console.error("Failed to fetch settings:", error);
        toast.error(t("admin.toast.fetchSettingsFailed"));
      }).finally(() => {
        setLoading(false);
      });
    },
    [t]
  );

  const fetchData = useCallback(({ showLoader = true }: { showLoader?: boolean } = {}) => {
    if (showLoader) setLoading(true);
    return loadData();
  }, [loadData]);

  useEffect(() => {
    void loadData();
  }, [loadData]);

  const handleFieldChange = useCallback((key: string, value: string) => {
    setEditedValues((prev) => ({
      ...prev,
      [key]: value,
    }));
  }, []);

  const toggleSecretVisibility = useCallback((key: string) => {
    setVisibleSecrets((prev) => ({
      ...prev,
      [key]: !prev[key],
    }));
  }, []);

  const resetChanges = useCallback(() => {
    setEditedValues(createEditedValues(settings));
    toast.info(t("admin.settings.changesDiscarded"));
  }, [settings, t]);

  const handleSave = async () => {
    setSaving(true);
    try {
      const changedSettings = settings
        .filter((setting) => editedValues[setting.key] !== (setting.value ?? ""))
        .map((setting) => ({ key: setting.key, value: editedValues[setting.key] ?? "" }));

      if (changedSettings.length === 0) {
        toast.info(t("admin.settings.noChanges"));
        setSaving(false);
        return;
      }

      await updateSettings(changedSettings);
      toast.success(t("admin.toast.saveSuccess"));
      await fetchData({ showLoader: false });
    } catch {
      toast.error(t("admin.toast.saveFailed"));
    } finally {
      setSaving(false);
    }
  };

  const categories = useMemo(() => {
    return [...new Set(settings.map((setting) => setting.category))].sort((a, b) => {
      const indexA = CATEGORY_ORDER.indexOf(a);
      const indexB = CATEGORY_ORDER.indexOf(b);

      if (indexA === -1 && indexB === -1) return a.localeCompare(b);
      if (indexA === -1) return 1;
      if (indexB === -1) return -1;
      return indexA - indexB;
    });
  }, [settings]);

  const normalizedSearchTerm = searchTerm.trim().toLowerCase();

  const visibleSettings = useMemo(() => {
    if (!normalizedSearchTerm) return settings;

    return settings.filter((setting) => {
      const localizedDescription = getSettingDescription(setting);
      return [
        setting.key,
        setting.value,
        setting.category,
        getSettingLabel(setting),
        localizedDescription,
        getCategoryMeta(setting.category).label,
      ]
        .filter(Boolean)
        .some((value) => value!.toLowerCase().includes(normalizedSearchTerm));
    });
  }, [getCategoryMeta, getSettingDescription, getSettingLabel, normalizedSearchTerm, settings]);

  const settingsByCategory = useMemo(() => groupSettingsByCategory(categories, settings), [categories, settings]);
  const visibleSettingsByCategory = useMemo(
    () => groupSettingsByCategory(categories, visibleSettings),
    [categories, visibleSettings]
  );

  const groupedSettingsByCategory = useMemo(() => {
    return categories.reduce<Record<string, SettingGroup[]>>((acc, category) => {
      const categorySettings = visibleSettingsByCategory[category] ?? [];
      if (category !== "ai") {
        acc[category] = categorySettings.length > 0 ? [{ id: "default", settings: categorySettings }] : [];
        return acc;
      }

      const bucket: Record<SettingGroupId, SystemSetting[]> = {
        aiContent: [],
        aiCatalog: [],
        aiTranslation: [],
        aiRuntime: [],
        aiOther: [],
        default: [],
      };

      categorySettings.forEach((setting) => {
        bucket[resolveAiGroupId(setting.key)].push(setting);
      });

      const order: SettingGroupId[] = ["aiContent", "aiCatalog", "aiTranslation", "aiRuntime", "aiOther"];
      acc[category] = order.filter((id) => bucket[id].length > 0).map((id) => ({ id, settings: bucket[id] }));
      return acc;
    }, {});
  }, [categories, resolveAiGroupId, visibleSettingsByCategory]);

  if (categories.length > 0 && !categories.includes(activeCategory)) {
    setActiveCategory(categories[0] ?? "general");
  }

  const pendingChangeCount = settings.reduce((count, setting) => {
    return count + (editedValues[setting.key] !== (setting.value ?? "") ? 1 : 0);
  }, 0);

  const pendingCountByCategory = useMemo(() => {
    return categories.reduce<Record<string, number>>((acc, category) => {
      acc[category] = (settingsByCategory[category] ?? []).filter(
        (setting) => editedValues[setting.key] !== (setting.value ?? "")
      ).length;
      return acc;
    }, {});
  }, [categories, editedValues, settingsByCategory]);

  const hasChanges = pendingChangeCount > 0;
  const hasSearch = normalizedSearchTerm.length > 0;
  const hasSettings = settings.length > 0;

  const renderSettingControl = (setting: SystemSetting) => {
    const currentValue = editedValues[setting.key] ?? "";

    if (isBooleanSetting(setting)) {
      const checked = currentValue.trim().toLowerCase() === "true";
      return (
        <div className="flex items-center justify-between gap-3 rounded-md border bg-background px-3 py-2">
          <span className="text-sm text-muted-foreground">
            {checked ? t("admin.settings.booleanOn") : t("admin.settings.booleanOff")}
          </span>
          <Switch
            aria-label={getSettingLabel(setting)}
            checked={checked}
            onCheckedChange={(nextChecked) => handleFieldChange(setting.key, String(nextChecked))}
          />
        </div>
      );
    }

    if (isTemplateSetting(setting, currentValue) && !isSensitiveSetting(setting)) {
      return (
        <Textarea
          value={currentValue}
          onChange={(event) => handleFieldChange(setting.key, event.target.value)}
          rows={5}
          className="min-h-[132px] resize-y font-mono text-sm"
        />
      );
    }

    if (isSensitiveSetting(setting)) {
      const isVisible = visibleSecrets[setting.key];
      return (
        <div className="relative">
          <Input
            type={isVisible ? "text" : "password"}
            value={currentValue}
            onChange={(event) => handleFieldChange(setting.key, event.target.value)}
            className="pr-10 font-mono"
          />
          <Button
            type="button"
            variant="ghost"
            size="icon-sm"
            className="absolute right-1 top-1/2 -translate-y-1/2"
            aria-label={isVisible ? t("admin.settings.hideSecret") : t("admin.settings.showSecret")}
            onClick={() => toggleSecretVisibility(setting.key)}
          >
            {isVisible ? <EyeOff className="h-4 w-4" /> : <Eye className="h-4 w-4" />}
          </Button>
        </div>
      );
    }

    return (
      <Input
        type={isNumericSetting(setting, currentValue) ? "number" : "text"}
        value={currentValue}
        onChange={(event) => handleFieldChange(setting.key, event.target.value)}
        className={cn(isNumericSetting(setting, currentValue) && "tabular-nums")}
      />
    );
  };

  if (loading) {
    return (
      <div className="flex min-h-[360px] flex-col items-center justify-center gap-3 text-muted-foreground">
        <Loader2 className="h-8 w-8 animate-spin" />
        <p className="text-sm">{t("admin.settings.loading")}</p>
      </div>
    );
  }

  return (
    <div className="space-y-5">
      <PageHeader
        title={t("admin.settings.title")}
        description={t("admin.settings.description")}
        actions={
          <>
            <Button variant="outline" onClick={() => fetchData()}>
              <RefreshCw className="h-4 w-4" />
              {t("admin.common.refresh")}
            </Button>
            <Button variant="outline" onClick={resetChanges} disabled={!hasChanges || saving}>
              <RotateCcw className="h-4 w-4" />
              {t("admin.settings.resetChanges")}
            </Button>
            <Button onClick={handleSave} disabled={saving || !hasChanges}>
              {saving ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
              {t("admin.settings.saveChanges")}
            </Button>
          </>
        }
      >
        <div className="flex flex-wrap items-center gap-2">
          <Badge variant={hasChanges ? "default" : "secondary"} className="gap-1.5">
            {hasChanges ? <AlertCircle className="h-3.5 w-3.5" /> : <CheckCircle2 className="h-3.5 w-3.5" />}
            {hasChanges
              ? t("admin.settings.unsavedState", { count: pendingChangeCount })
              : t("admin.settings.savedState")}
          </Badge>
          <span className="text-xs text-muted-foreground">{t("admin.settings.saveHint")}</span>
        </div>
      </PageHeader>

      <div className="grid gap-3 sm:grid-cols-3">
        <div className="rounded-lg border border-border/70 bg-card/70 p-4 shadow-sm">
          <p className="text-xs text-muted-foreground">{t("admin.settings.statCategories")}</p>
          <p className="mt-2 text-2xl font-semibold tracking-tight tabular-nums">{categories.length}</p>
        </div>
        <div className="rounded-lg border border-border/70 bg-card/70 p-4 shadow-sm">
          <p className="text-xs text-muted-foreground">{t("admin.settings.statFields")}</p>
          <p className="mt-2 text-2xl font-semibold tracking-tight tabular-nums">{settings.length}</p>
        </div>
        <div className="rounded-lg border border-border/70 bg-card/70 p-4 shadow-sm">
          <p className="text-xs text-muted-foreground">{t("admin.settings.changedFields")}</p>
          <p className="mt-2 text-2xl font-semibold tracking-tight tabular-nums">{pendingChangeCount}</p>
        </div>
      </div>

      {!hasSettings ? (
        <section className="flex min-h-[280px] items-center justify-center rounded-md border bg-card p-8">
          <div className="text-center">
            <Settings className="mx-auto h-10 w-10 text-muted-foreground" />
            <p className="mt-4 text-sm text-muted-foreground">{t("admin.settings.noSettings")}</p>
            <Button variant="outline" className="mt-4" onClick={() => fetchData()}>
              <RefreshCw className="h-4 w-4" />
              {t("admin.common.refresh")}
            </Button>
          </div>
        </section>
      ) : (
        <Tabs value={activeCategory} onValueChange={setActiveCategory} className="space-y-6">
          <div className="grid gap-6 lg:grid-cols-[280px,minmax(0,1fr)]">
            <aside className="space-y-4 lg:sticky lg:top-20 lg:self-start">
              <section className="rounded-md border bg-card p-4">
                <label className="text-xs font-medium uppercase tracking-wide text-muted-foreground">
                  {t("admin.settings.searchLabel")}
                </label>
                <div className="relative mt-2">
                  <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" />
                  <Input
                    value={searchTerm}
                    onChange={(event) => setSearchTerm(event.target.value)}
                    placeholder={t("admin.settings.searchPlaceholder")}
                    className="pl-9 pr-9"
                  />
                  {hasSearch && (
                    <Button
                      type="button"
                      variant="ghost"
                      size="icon-sm"
                      className="absolute right-1 top-1/2 -translate-y-1/2"
                      aria-label={t("admin.settings.clearSearch")}
                      onClick={() => setSearchTerm("")}
                    >
                      <X className="h-4 w-4" />
                    </Button>
                  )}
                </div>
                <p className="mt-2 text-xs text-muted-foreground">
                  {t("admin.settings.searchResults", { count: visibleSettings.length })}
                </p>
              </section>

              <section className="rounded-md border bg-card p-4">
                <div className="mb-3 flex items-center justify-between gap-3">
                  <div>
                    <p className="text-xs font-medium uppercase tracking-wide text-muted-foreground">
                      {t("admin.settings.categoryNavigation")}
                    </p>
                    <p className="text-sm font-medium">{t("admin.settings.categoriesLabel")}</p>
                  </div>
                  <SlidersHorizontal className="h-4 w-4 text-muted-foreground" />
                </div>

                <TabsList className="grid h-auto w-full gap-2 bg-transparent p-0">
                  {categories.map((category) => {
                    const meta = getCategoryMeta(category);
                    const CategoryIcon = meta.icon;
                    const categoryCount = settingsByCategory[category]?.length ?? 0;
                    const visibleCount = visibleSettingsByCategory[category]?.length ?? 0;
                    const pendingCount = pendingCountByCategory[category] ?? 0;

                    return (
                      <TabsTrigger
                        key={category}
                        value={category}
                        className="h-auto w-full justify-start rounded-md border border-border bg-background p-3 text-left shadow-none transition hover:border-primary/40 hover:bg-accent data-[state=active]:border-primary data-[state=active]:bg-primary/5 data-[state=active]:shadow-none"
                      >
                        <div className="flex w-full min-w-0 items-start gap-3">
                          <span className={cn("mt-0.5 rounded-md border p-2", meta.tone)}>
                            <CategoryIcon className="h-4 w-4" />
                          </span>
                          <span className="min-w-0 flex-1">
                            <span className="flex items-center justify-between gap-2">
                              <span className="truncate text-sm font-medium">{meta.label}</span>
                              <Badge variant="secondary" className="shrink-0 text-[11px]">
                                {hasSearch ? visibleCount : categoryCount}
                              </Badge>
                            </span>
                            <span className="mt-1 block line-clamp-2 text-xs font-normal text-muted-foreground">
                              {meta.description}
                            </span>
                            {pendingCount > 0 && (
                              <span className="mt-2 inline-flex text-xs font-medium text-primary">
                                {t("admin.settings.pendingCount", { count: pendingCount })}
                              </span>
                            )}
                          </span>
                        </div>
                      </TabsTrigger>
                    );
                  })}
                </TabsList>
              </section>
            </aside>

            <div className="min-w-0 space-y-6">
              {categories.map((category) => {
                const meta = getCategoryMeta(category);
                const CategoryIcon = meta.icon;
                const categoryGroups = groupedSettingsByCategory[category] ?? [];
                const totalCategorySettings = settingsByCategory[category]?.length ?? 0;
                const visibleCategorySettings = visibleSettingsByCategory[category]?.length ?? 0;
                const categoryPendingCount = pendingCountByCategory[category] ?? 0;

                return (
                  <TabsContent key={category} value={category} className="mt-0 space-y-5">
                    <section className="rounded-md border bg-card p-5">
                      <div className="flex flex-col gap-4 md:flex-row md:items-start md:justify-between">
                        <div className="flex min-w-0 gap-3">
                          <span className={cn("h-fit rounded-md border p-2.5", meta.tone)}>
                            <CategoryIcon className="h-5 w-5" />
                          </span>
                          <div className="min-w-0">
                            <p className="text-xs font-medium uppercase tracking-wide text-muted-foreground">
                              {t("admin.settings.activeCategory")}
                            </p>
                            <h2 className="mt-1 text-xl font-semibold">{meta.label}</h2>
                            <p className="mt-1 max-w-2xl text-sm text-muted-foreground">{meta.description}</p>
                          </div>
                        </div>
                        <div className="flex flex-wrap gap-2">
                          <Badge variant="outline">
                            {t("admin.settings.settingsCount", { count: totalCategorySettings })}
                          </Badge>
                          {hasSearch && (
                            <Badge variant="secondary">
                              {t("admin.settings.searchResults", { count: visibleCategorySettings })}
                            </Badge>
                          )}
                          {categoryPendingCount > 0 && (
                            <Badge>{t("admin.settings.pendingCount", { count: categoryPendingCount })}</Badge>
                          )}
                        </div>
                      </div>
                    </section>

                    {category === "ai" && (
                      <section className="rounded-md border border-sky-500/20 bg-sky-500/5 p-5">
                        <div className="flex flex-col gap-4 md:flex-row md:items-center md:justify-between">
                          <div className="space-y-2">
                            <Badge variant="secondary" className="w-fit">
                              {t("admin.settings.aiBindingsMigratedBadge")}
                            </Badge>
                            <div>
                              <h3 className="text-base font-semibold">{t("admin.settings.aiBindingsMigratedTitle")}</h3>
                              <p className="mt-1 max-w-3xl text-sm text-muted-foreground">
                                {t("admin.settings.aiBindingsMigratedDescription")}
                              </p>
                            </div>
                          </div>
                          <Button asChild>
                            <Link href="/admin/tools/model-configs">
                              {t("admin.settings.goToModelConfigs")}
                              <ArrowUpRight className="h-4 w-4" />
                            </Link>
                          </Button>
                        </div>
                      </section>
                    )}

                    {categoryGroups.length === 0 ? (
                      <section className="rounded-md border bg-card p-8 text-center">
                        <Search className="mx-auto h-9 w-9 text-muted-foreground" />
                        <h3 className="mt-4 text-sm font-semibold">{t("admin.settings.noSearchResults")}</h3>
                        <p className="mt-1 text-sm text-muted-foreground">
                          {t("admin.settings.noSearchResultsDescription")}
                        </p>
                        {hasSearch && (
                          <Button variant="outline" className="mt-4" onClick={() => setSearchTerm("")}>
                            <X className="h-4 w-4" />
                            {t("admin.settings.clearSearch")}
                          </Button>
                        )}
                      </section>
                    ) : (
                      categoryGroups.map((group) => {
                        const groupMeta = getGroupMeta(category, group.id);

                        return (
                          <section key={`${category}-${group.id}`} className="overflow-hidden rounded-md border bg-card">
                            <div className="flex flex-col gap-2 border-b bg-muted/30 px-5 py-4 md:flex-row md:items-start md:justify-between">
                              <div>
                                <h3 className="text-sm font-semibold">{groupMeta.title}</h3>
                                <p className="mt-1 text-sm text-muted-foreground">{groupMeta.description}</p>
                              </div>
                              <Badge variant="secondary" className="w-fit">
                                {t("admin.settings.settingsCount", { count: group.settings.length })}
                              </Badge>
                            </div>

                            <div className="divide-y">
                              {group.settings.map((setting) => {
                                const currentValue = editedValues[setting.key] ?? "";
                                const hasPendingChange = currentValue !== (setting.value ?? "");
                                const isSensitive = isSensitiveSetting(setting);
                                const localizedDescription = getSettingDescription(setting);

                                return (
                                  <div
                                    key={setting.key}
                                    className={cn(
                                      "grid gap-4 px-5 py-4 transition-colors md:grid-cols-[minmax(0,1fr)_minmax(260px,430px)]",
                                      hasPendingChange && "bg-primary/5"
                                    )}
                                  >
                                    <div className="min-w-0 space-y-2">
                                      <div className="flex flex-wrap items-center gap-2">
                                        <p className="text-sm font-medium">{getSettingLabel(setting)}</p>
                                        {isSensitive && (
                                          <Badge variant="outline" className="text-[11px]">
                                            {t("admin.settings.sensitiveValue")}
                                          </Badge>
                                        )}
                                        {hasPendingChange && (
                                          <Badge className="text-[11px]">{t("admin.settings.pendingChange")}</Badge>
                                        )}
                                      </div>
                                      <p className="break-all font-mono text-[11px] uppercase tracking-wide text-muted-foreground">
                                        {setting.key}
                                      </p>
                                      {localizedDescription && (
                                        <p className="max-w-3xl text-sm text-muted-foreground">{localizedDescription}</p>
                                      )}
                                    </div>

                                    <div className="min-w-0 space-y-2">
                                      {renderSettingControl(setting)}
                                      {hasPendingChange && (
                                        <Button
                                          type="button"
                                          variant="ghost"
                                          size="sm"
                                          className="h-7 px-2 text-xs"
                                          onClick={() => handleFieldChange(setting.key, setting.value ?? "")}
                                        >
                                          <RotateCcw className="h-3.5 w-3.5" />
                                          {t("admin.settings.resetField")}
                                        </Button>
                                      )}
                                    </div>
                                  </div>
                                );
                              })}
                            </div>
                          </section>
                        );
                      })
                    )}
                  </TabsContent>
                );
              })}
            </div>
          </div>
        </Tabs>
      )}

      {hasChanges && (
        <div className="sticky bottom-4 z-20 rounded-md border bg-background/95 p-3 shadow-lg backdrop-blur">
          <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
            <div>
              <p className="text-sm font-medium">{t("admin.settings.reviewChanges", { count: pendingChangeCount })}</p>
              <p className="text-xs text-muted-foreground">{t("admin.settings.saveHint")}</p>
            </div>
            <div className="flex gap-2">
              <Button variant="outline" onClick={resetChanges} disabled={saving}>
                <RotateCcw className="h-4 w-4" />
                {t("admin.settings.resetChanges")}
              </Button>
              <Button onClick={handleSave} disabled={saving}>
                {saving ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
                {t("admin.settings.saveChanges")}
              </Button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
