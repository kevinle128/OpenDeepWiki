"use client";

import { useEffect, useState } from "react";
import { useTranslations } from "@/hooks/use-translations";
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Textarea } from "@/components/ui/textarea";
import { Switch } from "@/components/ui/switch";
import { Label } from "@/components/ui/label";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { Loader2 } from "lucide-react";
import {
  createApp,
  updateApp,
  ChatAppDto,
  CreateChatAppDto,
  UpdateChatAppDto,
  AppAiModel,
  AppAiProvider,
  getAppAiModels,
  getAppAiProviders,
} from "@/lib/apps-api";

interface AppFormDialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  app?: ChatAppDto | null;
  onSuccess: () => void;
}

export function AppFormDialog({
  open,
  onOpenChange,
  app,
  onSuccess,
}: AppFormDialogProps) {
  const t = useTranslations();
  const isEditing = !!app;

  const [isSubmitting, setIsSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const [name, setName] = useState("");
  const [description, setDescription] = useState("");
  const [iconUrl, setIconUrl] = useState("");
  const [enableDomainValidation, setEnableDomainValidation] = useState(false);
  const [allowedDomains, setAllowedDomains] = useState("");
  const [aiProviders, setAiProviders] = useState<AppAiProvider[]>([]);
  const [aiModels, setAiModels] = useState<AppAiModel[]>([]);
  const [aiProviderId, setAiProviderId] = useState("");
  const [defaultModel, setDefaultModel] = useState("");
  const [rateLimitPerMinute, setRateLimitPerMinute] = useState("");
  const [isActive, setIsActive] = useState(true);

  const [formSource, setFormSource] = useState<Pick<AppFormDialogProps, "open" | "app">>({ open: false, app: undefined });
  if (open && (formSource.open !== open || formSource.app !== app)) {
    setFormSource({ open, app });
    if (app) {
      setName(app.name);
      setDescription(app.description || "");
      setIconUrl(app.iconUrl || "");
      setEnableDomainValidation(app.enableDomainValidation);
      setAllowedDomains(app.allowedDomains.join("\n"));
      setAiProviderId(app.aiProviderId || "");
      setDefaultModel(app.defaultModel || "");
      setRateLimitPerMinute(app.rateLimitPerMinute?.toString() || "");
      setIsActive(app.isActive);
    } else {
      setName("");
      setDescription("");
      setIconUrl("");
      setEnableDomainValidation(false);
      setAllowedDomains("");
      setAiProviderId("");
      setDefaultModel("");
      setRateLimitPerMinute("");
      setIsActive(true);
    }

    setError(null);
  } else if (formSource.open !== open) {
    setFormSource({ open, app });
  }

  const [modelsProviderId, setModelsProviderId] = useState(aiProviderId);
  if (modelsProviderId !== aiProviderId) {
    setModelsProviderId(aiProviderId);
    setAiModels([]);
    if (!aiProviderId) setDefaultModel("");
  }

  useEffect(() => {
    if (!open) return;
    let isMounted = true;

    getAppAiProviders()
      .then((providers) => {
        if (!isMounted) return;
        setAiProviders(providers);
        setAiProviderId((current) => current || app?.aiProviderId || providers[0]?.id || "");
      })
      .catch(() => setError("Failed to load AI providers"));

    return () => {
      isMounted = false;
    };
  }, [open, app?.aiProviderId]);

  useEffect(() => {
    if (!aiProviderId) return;

    let isMounted = true;

    getAppAiModels(aiProviderId)
      .then((models) => {
        if (!isMounted) return;
        setAiModels(models);
        setDefaultModel((current) =>
          current && models.some((model) => model.modelId === current)
            ? current
            : models.find((model) => model.isDefault)?.modelId || models[0]?.modelId || ""
        );
      })
      .catch(() => setError("Failed to load AI models"));

    return () => {
      isMounted = false;
    };
  }, [aiProviderId]);

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();

    if (!name.trim()) {
      setError(t("apps.form.nameRequired"));
      return;
    }

    if (!aiProviderId) {
      setError("Please select an AI provider");
      return;
    }

    if (!defaultModel.trim()) {
      setError("Please select a default model");
      return;
    }

    setIsSubmitting(true);
    setError(null);

    try {
      const domainsArray = allowedDomains
        .split("\n")
        .map((domain) => domain.trim())
        .filter(Boolean);
      const modelsArray = aiModels.map((model) => model.modelId);
      const selectedProvider = aiProviders.find((provider) => provider.id === aiProviderId);

      if (isEditing && app) {
        const updateDto: UpdateChatAppDto = {
          name: name.trim(),
          description: description.trim() || undefined,
          iconUrl: iconUrl.trim() || undefined,
          enableDomainValidation,
          allowedDomains: domainsArray,
          aiProviderId,
          providerType: selectedProvider?.providerType,
          availableModels: modelsArray,
          defaultModel: defaultModel.trim(),
          rateLimitPerMinute: rateLimitPerMinute
            ? parseInt(rateLimitPerMinute, 10)
            : undefined,
          isActive,
        };
        await updateApp(app.id, updateDto);
      } else {
        const createDto: CreateChatAppDto = {
          name: name.trim(),
          description: description.trim() || undefined,
          iconUrl: iconUrl.trim() || undefined,
          enableDomainValidation,
          allowedDomains: domainsArray,
          aiProviderId,
          providerType: selectedProvider?.providerType || "OpenAI",
          availableModels: modelsArray,
          defaultModel: defaultModel.trim(),
          rateLimitPerMinute: rateLimitPerMinute
            ? parseInt(rateLimitPerMinute, 10)
            : undefined,
        };
        await createApp(createDto);
      }

      onSuccess();
    } catch (err) {
      setError(
        err instanceof Error
          ? err.message
          : isEditing
            ? t("apps.form.updateFailed")
            : t("apps.form.createFailed")
      );
    } finally {
      setIsSubmitting(false);
    }
  };

  const modelOptions = aiModels.map((model) => model.modelId);

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-2xl max-h-[90vh] overflow-y-auto">
        <DialogHeader>
          <DialogTitle>
            {isEditing ? t("apps.form.editTitle") : t("apps.form.createTitle")}
          </DialogTitle>
        </DialogHeader>

        <form onSubmit={handleSubmit} className="space-y-6">
          {error && (
            <div className="bg-destructive/10 text-destructive px-4 py-2 rounded-md text-sm">
              {error}
            </div>
          )}

          <div className="space-y-4">
            <div className="space-y-2">
              <Label htmlFor="name">{t("apps.form.name")} *</Label>
              <Input
                id="name"
                value={name}
                onChange={(e) => setName(e.target.value)}
                placeholder={t("apps.form.namePlaceholder")}
              />
            </div>

            <div className="space-y-2">
              <Label htmlFor="description">{t("apps.form.description")}</Label>
              <Textarea
                id="description"
                value={description}
                onChange={(e) => setDescription(e.target.value)}
                placeholder={t("apps.form.descriptionPlaceholder")}
                rows={2}
              />
            </div>

            <div className="space-y-2">
              <Label htmlFor="iconUrl">{t("apps.form.iconUrl")}</Label>
              <Input
                id="iconUrl"
                value={iconUrl}
                onChange={(e) => setIconUrl(e.target.value)}
                placeholder={t("apps.form.iconUrlPlaceholder")}
              />
            </div>
          </div>

          <div className="space-y-4 border-t pt-4">
            <div className="flex items-center justify-between">
              <div className="space-y-0.5">
                <Label>{t("apps.form.domainValidation")}</Label>
                <p className="text-sm text-muted-foreground">
                  {t("apps.form.domainValidationHint")}
                </p>
              </div>
              <Switch
                checked={enableDomainValidation}
                onCheckedChange={setEnableDomainValidation}
              />
            </div>

            {enableDomainValidation && (
              <div className="space-y-2">
                <Label htmlFor="allowedDomains">
                  {t("apps.form.allowedDomains")}
                </Label>
                <Textarea
                  id="allowedDomains"
                  value={allowedDomains}
                  onChange={(e) => setAllowedDomains(e.target.value)}
                  placeholder={t("apps.form.allowedDomainsPlaceholder")}
                  rows={3}
                />
              </div>
            )}
          </div>

          <div className="space-y-4 border-t pt-4">
            <h3 className="font-medium">{t("apps.form.aiConfig")}</h3>

            <div className="grid grid-cols-2 gap-4">
              <div className="space-y-2">
                <Label>AI Provider *</Label>
                <Select value={aiProviderId} onValueChange={setAiProviderId}>
                  <SelectTrigger className="w-full">
                    <SelectValue placeholder="Select AI provider" />
                  </SelectTrigger>
                  <SelectContent>
                    {aiProviders.map((provider) => (
                      <SelectItem key={provider.id} value={provider.id}>
                        {provider.name}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              </div>

              <div className="space-y-2">
                <Label>{t("apps.form.defaultModel")} *</Label>
                <Select
                  value={defaultModel}
                  onValueChange={setDefaultModel}
                  disabled={!aiProviderId || aiModels.length === 0}
                >
                  <SelectTrigger className="w-full">
                    <SelectValue
                      placeholder={t("apps.form.defaultModelPlaceholder")}
                    />
                  </SelectTrigger>
                  <SelectContent>
                    {modelOptions.map((model) => (
                      <SelectItem key={model} value={model}>
                        {model}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              </div>
            </div>

            <p className="text-sm text-muted-foreground">
              Endpoint and API key are read from the selected provider.
            </p>

            <div className="grid grid-cols-2 gap-4">
              <div className="space-y-2">
                <Label htmlFor="rateLimit">
                  {t("apps.form.rateLimitPerMinute")}
                </Label>
                <Input
                  id="rateLimit"
                  type="number"
                  min="0"
                  value={rateLimitPerMinute}
                  onChange={(e) => setRateLimitPerMinute(e.target.value)}
                  placeholder={t("apps.form.rateLimitPlaceholder")}
                />
              </div>
            </div>
          </div>

          {isEditing && (
            <div className="flex items-center justify-between border-t pt-4">
              <Label>{t("apps.form.isActive")}</Label>
              <Switch checked={isActive} onCheckedChange={setIsActive} />
            </div>
          )}

          <div className="flex justify-end gap-2 border-t pt-4">
            <Button
              type="button"
              variant="outline"
              onClick={() => onOpenChange(false)}
              disabled={isSubmitting}
            >
              {t("common.cancel")}
            </Button>
            <Button type="submit" disabled={isSubmitting}>
              {isSubmitting && <Loader2 className="h-4 w-4 animate-spin mr-2" />}
              {t("common.save")}
            </Button>
          </div>
        </form>
      </DialogContent>
    </Dialog>
  );
}
