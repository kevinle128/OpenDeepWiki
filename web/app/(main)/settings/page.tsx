"use client";

import { useState, useEffect, useCallback } from "react";
import { useRouter } from "next/navigation";
import { AppLayout } from "@/components/app-layout";
import { Button } from "@/components/ui/button";
import { Label } from "@/components/ui/label";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Switch } from "@/components/ui/switch";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { useTranslations } from "@/hooks/use-translations";
import { useAuth } from "@/contexts/auth-context";
import { getUserSettings, updateUserSettings, UserSettings, getSystemVersion, SystemVersion, getUserApiKeys, createUserApiKey, revokeUserApiKey, UserApiKeyListItem, UserApiKeyCreateResult } from "@/lib/profile-api";
import { Loader2, Bell, Globe, Palette, ArrowLeft, Key, Plus, Trash2, Copy, Check, AlertTriangle } from "lucide-react";
import { uiLocales } from "@/i18n/config";
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogFooter } from "@/components/ui/dialog";
import { AlertDialog, AlertDialogAction, AlertDialogCancel, AlertDialogContent, AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle } from "@/components/ui/alert-dialog";
import { Input } from "@/components/ui/input";
import { Badge } from "@/components/ui/badge";
import { toast } from "sonner";
import Link from "next/link";
import { useTheme } from "next-themes";

export default function SettingsPage() {
  const t = useTranslations();
  const router = useRouter();
  const { isLoading: authLoading, isAuthenticated } = useAuth();
  const { theme, setTheme } = useTheme();
  const [activeItem, setActiveItem] = useState(t("common.settings.title"));

  const [settings, setSettings] = useState<UserSettings>({
    theme: "system",
    language: "zh",
    emailNotifications: true,
    pushNotifications: false,
  });
  const [systemVersion, setSystemVersion] = useState<SystemVersion | null>(null);
  const [isLoading, setIsLoading] = useState(true);
  const [isSaving, setIsSaving] = useState(false);

  // API Keys state
  const [apiKeys, setApiKeys] = useState<UserApiKeyListItem[]>([]);
  const [apiKeysLoading, setApiKeysLoading] = useState(true);
  const [showCreateDialog, setShowCreateDialog] = useState(false);
  const [newKeyName, setNewKeyName] = useState("");
  const [newKeyExpiry, setNewKeyExpiry] = useState("");
  const [isCreatingKey, setIsCreatingKey] = useState(false);
  const [createdKey, setCreatedKey] = useState<UserApiKeyCreateResult | null>(null);
  const [showKeyReveal, setShowKeyReveal] = useState(false);
  const [keyCopied, setKeyCopied] = useState(false);
  const [revokeTarget, setRevokeTarget] = useState<UserApiKeyListItem | null>(null);
  const [isRevoking, setIsRevoking] = useState(false);

  useEffect(() => {
    if (!authLoading && !isAuthenticated) {
      router.push("/auth?returnUrl=/settings");
    }
  }, [authLoading, isAuthenticated, router]);

  const loadSettings = useCallback(() => {
    return getUserSettings().then(setSettings).catch(() => {
      // 使用默认设置
    }).finally(() => {
      setIsLoading(false);
    });
  }, []);

  useEffect(() => {
    getSystemVersion().then(setSystemVersion).catch(() => {
      // 使用默认版本
    });
  }, []);

  const handleSave = async () => {
    setIsSaving(true);
    try {
      await updateUserSettings(settings);
      toast.success(t("settings.saveSuccess"));
    } catch (err) {
      toast.error(err instanceof Error ? err.message : t("settings.saveFailed"));
    } finally {
      setIsSaving(false);
    }
  };

  const handleThemeChange = (value: string) => {
    setSettings((prev) => ({ ...prev, theme: value as UserSettings["theme"] }));
    setTheme(value);
  };

  const handleLanguageChange = (value: string) => {
    setSettings((prev) => ({ ...prev, language: value }));
    // 语言切换由 LanguageToggle 组件处理，这里只保存设置
  };

  const loadApiKeys = useCallback(() => {
    return getUserApiKeys().then(setApiKeys).catch(() => {
      toast.error(t("settings.apiKeys.fetchFailed"));
    }).finally(() => {
      setApiKeysLoading(false);
    });
  }, [t]);

  useEffect(() => {
    if (isAuthenticated) {
      void loadSettings();
      void loadApiKeys();
    }
  }, [isAuthenticated, loadSettings, loadApiKeys]);

  const handleCreateKey = async () => {
    if (!newKeyName.trim()) {
      toast.error(t("settings.apiKeys.nameRequired"));
      return;
    }
    setIsCreatingKey(true);
    try {
      const expiresInDays = newKeyExpiry ? parseInt(newKeyExpiry, 10) : undefined;
      const result = await createUserApiKey({
        name: newKeyName.trim(),
        scope: "mcp:read",
        expiresInDays,
      });
      setCreatedKey(result);
      setShowCreateDialog(false);
      setShowKeyReveal(true);
      setNewKeyName("");
      setNewKeyExpiry("");
      setApiKeysLoading(true);
      await loadApiKeys();
    } catch (err) {
      toast.error(err instanceof Error ? err.message : t("settings.apiKeys.createFailed"));
    } finally {
      setIsCreatingKey(false);
    }
  };

  const handleRevokeKey = async () => {
    if (!revokeTarget) return;
    setIsRevoking(true);
    try {
      await revokeUserApiKey(revokeTarget.id);
      toast.success(t("settings.apiKeys.revoked"));
      setRevokeTarget(null);
      setApiKeysLoading(true);
      await loadApiKeys();
    } catch (err) {
      toast.error(err instanceof Error ? err.message : t("settings.apiKeys.revokeFailed"));
    } finally {
      setIsRevoking(false);
    }
  };

  const handleCopyKey = async (key: string) => {
    await navigator.clipboard.writeText(key);
    setKeyCopied(true);
    toast.success(t("settings.apiKeys.copied"));
    setTimeout(() => setKeyCopied(false), 2000);
  };

  const formatDate = (dateStr?: string) => {
    if (!dateStr) return t("settings.apiKeys.never");
    return new Date(dateStr).toLocaleDateString();
  };

  if (authLoading || isLoading) {
    return (
      <div className="flex h-screen items-center justify-center">
        <Loader2 className="h-8 w-8 animate-spin text-muted-foreground" />
      </div>
    );
  }

  if (!isAuthenticated) {
    return null;
  }

  return (
    <AppLayout activeItem={activeItem} onItemClick={setActiveItem}>
      <div className="flex flex-1 flex-col p-4 md:p-6 max-w-4xl mx-auto w-full">
        <div className="mb-6">
          <Link
            href="/"
            className="inline-flex items-center gap-2 text-sm text-muted-foreground hover:text-foreground transition-colors"
          >
            <ArrowLeft className="h-4 w-4" />
            {t("common.backToHome")}
          </Link>
        </div>

        <div className="space-y-6">
          {/* Appearance Settings */}
          <Card>
            <CardHeader>
              <div className="flex items-center gap-2">
                <Palette className="h-5 w-5" />
                <CardTitle>{t("settings.appearance")}</CardTitle>
              </div>
              <CardDescription>
                {t("settings.appearanceDescription")}
              </CardDescription>
            </CardHeader>
            <CardContent className="space-y-6">
              <div className="flex items-center justify-between">
                <div className="space-y-0.5">
                  <Label>{t("settings.theme")}</Label>
                  <p className="text-sm text-muted-foreground">
                    {t("settings.themeDescription")}
                  </p>
                </div>
                <Select value={theme || settings.theme} onValueChange={handleThemeChange}>
                  <SelectTrigger className="w-32">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="light">{t("settings.themeLight")}</SelectItem>
                    <SelectItem value="dark">{t("settings.themeDark")}</SelectItem>
                    <SelectItem value="system">{t("settings.themeSystem")}</SelectItem>
                  </SelectContent>
                </Select>
              </div>

              <div className="flex items-center justify-between">
                <div className="space-y-0.5">
                  <Label>{t("settings.language")}</Label>
                  <p className="text-sm text-muted-foreground">
                    {t("settings.languageDescription")}
                  </p>
                </div>
                <Select value={settings.language} onValueChange={handleLanguageChange}>
                  <SelectTrigger className="w-32">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    {uiLocales.map((loc) => (
                      <SelectItem key={loc} value={loc}>
                        {t(`common.languageNames.${loc}`)}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              </div>
            </CardContent>
          </Card>

          {/* Notification Settings */}
          <Card>
            <CardHeader>
              <div className="flex items-center gap-2">
                <Bell className="h-5 w-5" />
                <CardTitle>{t("settings.notifications")}</CardTitle>
              </div>
              <CardDescription>
                {t("settings.notificationsDescription")}
              </CardDescription>
            </CardHeader>
            <CardContent className="space-y-6">
              <div className="flex items-center justify-between">
                <div className="space-y-0.5">
                  <Label>{t("settings.emailNotifications")}</Label>
                  <p className="text-sm text-muted-foreground">
                    {t("settings.emailNotificationsDescription")}
                  </p>
                </div>
                <Switch
                  checked={settings.emailNotifications}
                  onCheckedChange={(checked) =>
                    setSettings((prev) => ({ ...prev, emailNotifications: checked }))
                  }
                />
              </div>

              <div className="flex items-center justify-between">
                <div className="space-y-0.5">
                  <Label>{t("settings.pushNotifications")}</Label>
                  <p className="text-sm text-muted-foreground">
                    {t("settings.pushNotificationsDescription")}
                  </p>
                </div>
                <Switch
                  checked={settings.pushNotifications}
                  onCheckedChange={(checked) =>
                    setSettings((prev) => ({ ...prev, pushNotifications: checked }))
                  }
                />
              </div>
            </CardContent>
          </Card>

          {/* API Keys */}
          <Card>
            <CardHeader>
              <div className="flex items-center justify-between">
                <div className="flex items-center gap-2">
                  <Key className="h-5 w-5" />
                  <CardTitle>{t("settings.apiKeys.title")}</CardTitle>
                </div>
                <Button size="sm" onClick={() => setShowCreateDialog(true)}>
                  <Plus className="h-4 w-4 mr-1" />
                  {t("settings.apiKeys.createKey")}
                </Button>
              </div>
              <CardDescription>
                {t("settings.apiKeys.description")}
              </CardDescription>
            </CardHeader>
            <CardContent>
              {apiKeysLoading ? (
                <div className="flex justify-center py-6">
                  <Loader2 className="h-6 w-6 animate-spin text-muted-foreground" />
                </div>
              ) : apiKeys.length === 0 ? (
                <div className="flex flex-col items-center justify-center py-8 text-muted-foreground">
                  <Key className="h-10 w-10 mb-2 opacity-50" />
                  <p className="text-sm">{t("settings.apiKeys.noKeys")}</p>
                </div>
              ) : (
                <div className="space-y-3">
                  {apiKeys.map((apiKey) => (
                    <div
                      key={apiKey.id}
                      className="flex items-center justify-between rounded-lg border p-3"
                    >
                      <div className="space-y-1 min-w-0 flex-1">
                        <div className="flex items-center gap-2">
                          <span className="font-medium text-sm truncate">{apiKey.name}</span>
                          <Badge variant="secondary" className="text-xs shrink-0">
                            {apiKey.scope}
                          </Badge>
                        </div>
                        <div className="flex flex-wrap gap-x-4 gap-y-1 text-xs text-muted-foreground">
                          <span className="font-mono">{apiKey.keyPrefix}...</span>
                          <span>
                            {t("settings.apiKeys.created")}: {formatDate(apiKey.createdAt)}
                          </span>
                          <span>
                            {t("settings.apiKeys.expires")}: {apiKey.expiresAt ? formatDate(apiKey.expiresAt) : (t("settings.apiKeys.never"))}
                          </span>
                          <span>
                            {t("settings.apiKeys.lastUsed")}: {apiKey.lastUsedAt ? formatDate(apiKey.lastUsedAt) : (t("settings.apiKeys.neverUsed"))}
                          </span>
                        </div>
                      </div>
                      <Button
                        variant="ghost"
                        size="icon"
                        className="shrink-0 text-destructive hover:text-destructive hover:bg-destructive/10"
                        onClick={() => setRevokeTarget(apiKey)}
                      >
                        <Trash2 className="h-4 w-4" />
                      </Button>
                    </div>
                  ))}
                </div>
              )}
            </CardContent>
          </Card>

          {/* Create API Key Dialog */}
          <Dialog open={showCreateDialog} onOpenChange={setShowCreateDialog}>
            <DialogContent>
              <DialogHeader>
                <DialogTitle>{t("settings.apiKeys.createKey")}</DialogTitle>
              </DialogHeader>
              <div className="space-y-4 py-2">
                <div className="space-y-2">
                  <Label>{t("settings.apiKeys.name")}</Label>
                  <Input
                    value={newKeyName}
                    onChange={(e) => setNewKeyName(e.target.value)}
                    placeholder={t("settings.apiKeys.namePlaceholder")}
                  />
                </div>
                <div className="space-y-2">
                  <Label>{t("settings.apiKeys.scope")}</Label>
                  <div>
                    <Badge variant="secondary">mcp:read</Badge>
                  </div>
                </div>
                <div className="space-y-2">
                  <Label>{t("settings.apiKeys.expiresInDays")}</Label>
                  <Input
                    type="number"
                    min="1"
                    value={newKeyExpiry}
                    onChange={(e) => setNewKeyExpiry(e.target.value)}
                    placeholder={t("settings.apiKeys.expiresPlaceholder")}
                  />
                </div>
              </div>
              <DialogFooter>
                <Button variant="outline" onClick={() => setShowCreateDialog(false)}>
                  {t("common.cancel")}
                </Button>
                <Button onClick={handleCreateKey} disabled={isCreatingKey}>
                  {isCreatingKey && <Loader2 className="h-4 w-4 mr-2 animate-spin" />}
                  {t("settings.apiKeys.createKey")}
                </Button>
              </DialogFooter>
            </DialogContent>
          </Dialog>

          {/* Key Reveal Dialog */}
          <Dialog open={showKeyReveal} onOpenChange={(open) => {
            if (!open) {
              setShowKeyReveal(false);
              setCreatedKey(null);
              setKeyCopied(false);
            }
          }}>
            <DialogContent>
              <DialogHeader>
                <DialogTitle>{t("settings.apiKeys.keyCreated")}</DialogTitle>
              </DialogHeader>
              <div className="space-y-4 py-2">
                <div className="flex items-start gap-2 rounded-md bg-yellow-50 dark:bg-yellow-950 border border-yellow-200 dark:border-yellow-800 p-3">
                  <AlertTriangle className="h-5 w-5 text-yellow-600 dark:text-yellow-400 shrink-0 mt-0.5" />
                  <p className="text-sm text-yellow-800 dark:text-yellow-200">
                    {t("settings.apiKeys.keyWarning")}
                  </p>
                </div>
                <div className="space-y-2">
                  <Label>{t("settings.apiKeys.yourKey")}</Label>
                  <div className="flex gap-2">
                    <Input
                      readOnly
                      value={createdKey?.plainTextKey || ""}
                      className="font-mono text-sm"
                    />
                    <Button
                      size="icon"
                      variant="outline"
                      onClick={() => createdKey && handleCopyKey(createdKey.plainTextKey)}
                    >
                      {keyCopied ? <Check className="h-4 w-4" /> : <Copy className="h-4 w-4" />}
                    </Button>
                  </div>
                </div>
              </div>
              <DialogFooter>
                <Button onClick={() => {
                  setShowKeyReveal(false);
                  setCreatedKey(null);
                  setKeyCopied(false);
                }}>
                  {t("settings.apiKeys.done")}
                </Button>
              </DialogFooter>
            </DialogContent>
          </Dialog>

          {/* Revoke Confirmation Dialog */}
          <AlertDialog open={!!revokeTarget} onOpenChange={(open) => !open && setRevokeTarget(null)}>
            <AlertDialogContent>
              <AlertDialogHeader>
                <AlertDialogTitle>{t("settings.apiKeys.revokeTitle")}</AlertDialogTitle>
                <AlertDialogDescription>
                  {t("settings.apiKeys.revokeWarning")}
                </AlertDialogDescription>
              </AlertDialogHeader>
              <AlertDialogFooter>
                <AlertDialogCancel>{t("common.cancel")}</AlertDialogCancel>
                <AlertDialogAction
                  onClick={handleRevokeKey}
                  disabled={isRevoking}
                  className="bg-destructive text-destructive-foreground hover:bg-destructive/90"
                >
                  {isRevoking && <Loader2 className="h-4 w-4 mr-2 animate-spin" />}
                  {t("settings.apiKeys.revoke")}
                </AlertDialogAction>
              </AlertDialogFooter>
            </AlertDialogContent>
          </AlertDialog>

          {/* About */}
          <Card>
            <CardHeader>
              <div className="flex items-center gap-2">
                <Globe className="h-5 w-5" />
                <CardTitle>{t("settings.about")}</CardTitle>
              </div>
            </CardHeader>
            <CardContent>
              <div className="space-y-2 text-sm text-muted-foreground">
                <p>{systemVersion?.productName || "OpenDeepWiki"} v{systemVersion?.version || "1.0.0"}</p>
                <p>{t("settings.aboutDescription")}</p>
              </div>
            </CardContent>
          </Card>

          {/* Save Button */}
          <div className="flex justify-end">
            <Button onClick={handleSave} disabled={isSaving}>
              {isSaving ? (
                <>
                  <Loader2 className="mr-2 h-4 w-4 animate-spin" />
                  {t("common.loading")}
                </>
              ) : (
                t("common.save")
              )}
            </Button>
          </div>
        </div>
      </div>
    </AppLayout>
  );
}
