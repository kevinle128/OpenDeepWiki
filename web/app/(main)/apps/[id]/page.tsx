"use client";

import { useState, useEffect, useCallback } from "react";
import { useParams, useRouter } from "next/navigation";
import { AppLayout } from "@/components/app-layout";
import { useTranslations } from "@/hooks/use-translations";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import {
  ArrowLeft,
  Loader2,
  Copy,
  Eye,
  EyeOff,
  RefreshCw,
  Pencil,
  Trash2,
} from "lucide-react";
import { useAuth } from "@/contexts/auth-context";
import {
  getAppById,
  deleteApp,
  regenerateAppSecret,
  ChatAppDto,
} from "@/lib/apps-api";
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import { AppFormDialog, AppStatisticsChart, AppLogsTable } from "@/components/apps";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";

export default function AppDetailPage() {
  const t = useTranslations();
  const router = useRouter();
  const params = useParams();
  const appId = params.id as string;
  const { isAuthenticated, isLoading: authLoading } = useAuth();

  const [activeItem, setActiveItem] = useState(t("sidebar.apps"));
  const [app, setApp] = useState<ChatAppDto | null>(null);
  const [isLoading, setIsLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [showSecret, setShowSecret] = useState(false);
  const [isRegenerating, setIsRegenerating] = useState(false);
  const [isFormOpen, setIsFormOpen] = useState(false);
  const [showDeleteDialog, setShowDeleteDialog] = useState(false);
  const [showRegenerateDialog, setShowRegenerateDialog] = useState(false);
  const [isDeleting, setIsDeleting] = useState(false);
  const [loadingAppId, setLoadingAppId] = useState(appId);
  if (loadingAppId !== appId) {
    setLoadingAppId(appId);
    setIsLoading(true);
  }

  const fetchApp = useCallback(() => {
    if (!appId) return;
    return getAppById(appId).then(data => {
      setApp(data);
      setError(null);
    }).catch(err => {
      setError(err instanceof Error ? err.message : "App not found");
    }).finally(() => {
      setIsLoading(false);
    });
  }, [appId]);

  useEffect(() => {
    if (!authLoading && isAuthenticated) {
      fetchApp();
    }
  }, [authLoading, isAuthenticated, fetchApp]);

  const handleCopy = async (text: string) => {
    try {
      await navigator.clipboard.writeText(text);
      // Could add a toast notification here
    } catch {
      setError(t("apps.detail.copyFailed"));
    }
  };

  const handleRegenerateSecret = async () => {
    if (!app) return;
    setIsRegenerating(true);
    try {
      const result = await regenerateAppSecret(app.id);
      setApp((prev) => (prev ? { ...prev, appSecret: result.appSecret } : null));
      setShowRegenerateDialog(false);
    } catch (err) {
      setError(
        err instanceof Error ? err.message : t("apps.detail.regenerateFailed")
      );
    } finally {
      setIsRegenerating(false);
    }
  };

  const handleDelete = async () => {
    if (!app) return;
    setIsDeleting(true);
    try {
      await deleteApp(app.id);
      router.push("/apps");
    } catch (err) {
      setError(
        err instanceof Error ? err.message : t("apps.detail.deleteFailed")
      );
    } finally {
      setIsDeleting(false);
    }
  };

  const handleEditSuccess = () => {
    setIsFormOpen(false);
    setIsLoading(true);
    setError(null);
    fetchApp();
  };

  const getEmbedScript = () => {
    if (!app) return "";
    const baseUrl = typeof window !== "undefined" ? window.location.origin : "";
    return `<script
  src="${baseUrl}/embed.js"
  data-app-id="${app.appId}"${app.iconUrl ? `\n  data-icon="${app.iconUrl}"` : ""}
></script>`;
  };

  if (!authLoading && !isAuthenticated) {
    return (
      <AppLayout activeItem={activeItem} onItemClick={setActiveItem}>
        <div className="flex flex-1 flex-col items-center justify-center gap-4 p-4 md:p-6">
          <p className="text-muted-foreground">{t("apps.loginRequired")}</p>
        </div>
      </AppLayout>
    );
  }

  if (isLoading || authLoading) {
    return (
      <AppLayout activeItem={activeItem} onItemClick={setActiveItem}>
        <div className="flex flex-1 items-center justify-center">
          <Loader2 className="h-8 w-8 animate-spin text-muted-foreground" />
        </div>
      </AppLayout>
    );
  }

  if (!app) {
    return (
      <AppLayout activeItem={activeItem} onItemClick={setActiveItem}>
        <div className="flex flex-1 flex-col items-center justify-center gap-4 p-4 md:p-6">
          <p className="text-muted-foreground">{t("apps.detail.notFound")}</p>
          <Button variant="outline" onClick={() => router.push("/apps")}>
            <ArrowLeft className="h-4 w-4 mr-2" />
            {t("apps.detail.backToList")}
          </Button>
        </div>
      </AppLayout>
    );
  }

  return (
    <AppLayout activeItem={activeItem} onItemClick={setActiveItem}>
      <div className="flex flex-1 flex-col gap-4 p-4 md:p-6">
        {/* Header */}
        <div className="flex items-center justify-between">
          <div className="flex items-center gap-4">
            <Button
              variant="ghost"
              size="sm"
              onClick={() => router.push("/apps")}
            >
              <ArrowLeft className="h-4 w-4 mr-2" />
              {t("apps.detail.backToList")}
            </Button>
            <div>
              <h1 className="text-2xl font-bold">{app.name}</h1>
              {app.description && (
                <p className="text-muted-foreground">{app.description}</p>
              )}
            </div>
          </div>
          <div className="flex items-center gap-2">
            <Button variant="outline" onClick={() => setIsFormOpen(true)}>
              <Pencil className="h-4 w-4 mr-2" />
              {t("common.edit")}
            </Button>
            <Button
              variant="destructive"
              onClick={() => setShowDeleteDialog(true)}
            >
              <Trash2 className="h-4 w-4 mr-2" />
              {t("common.delete")}
            </Button>
          </div>
        </div>

        {error && (
          <div className="bg-destructive/10 text-destructive px-4 py-2 rounded-md">
            {error}
          </div>
        )}

        {/* App Info Card */}
        <Card>
          <CardHeader>
            <CardTitle>{t("apps.detail.title")}</CardTitle>
          </CardHeader>
          <CardContent className="space-y-4">
            {/* App ID */}
            <div className="space-y-2">
              <label className="text-sm font-medium">{t("apps.detail.appId")}</label>
              <div className="flex items-center gap-2">
                <Input value={app.appId} readOnly className="font-mono" />
                <Button
                  variant="outline"
                  size="icon"
                  onClick={() => handleCopy(app.appId)}
                >
                  <Copy className="h-4 w-4" />
                </Button>
              </div>
            </div>

            {/* App Secret */}
            <div className="space-y-2">
              <label className="text-sm font-medium">
                {t("apps.detail.appSecret")}
              </label>
              <div className="flex items-center gap-2">
                <Input
                  type={showSecret ? "text" : "password"}
                  value={app.appSecret || ""}
                  readOnly
                  className="font-mono"
                />
                <Button
                  variant="outline"
                  size="icon"
                  onClick={() => setShowSecret(!showSecret)}
                >
                  {showSecret ? (
                    <EyeOff className="h-4 w-4" />
                  ) : (
                    <Eye className="h-4 w-4" />
                  )}
                </Button>
                <Button
                  variant="outline"
                  size="icon"
                  onClick={() => app.appSecret && handleCopy(app.appSecret)}
                >
                  <Copy className="h-4 w-4" />
                </Button>
                <Button
                  variant="outline"
                  onClick={() => setShowRegenerateDialog(true)}
                  disabled={isRegenerating}
                >
                  {isRegenerating ? (
                    <Loader2 className="h-4 w-4 animate-spin mr-2" />
                  ) : (
                    <RefreshCw className="h-4 w-4 mr-2" />
                  )}
                  {t("apps.detail.regenerateSecret")}
                </Button>
              </div>
            </div>

            {/* Embed Script */}
            <div className="space-y-2">
              <label className="text-sm font-medium">
                {t("apps.detail.embedScript")}
              </label>
              <p className="text-sm text-muted-foreground">
                {t("apps.detail.embedScriptHint")}
              </p>
              <div className="relative">
                <pre className="bg-muted p-4 rounded-md overflow-x-auto text-sm">
                  <code>{getEmbedScript()}</code>
                </pre>
                <Button
                  variant="outline"
                  size="sm"
                  className="absolute top-2 right-2"
                  onClick={() => handleCopy(getEmbedScript())}
                >
                  <Copy className="h-4 w-4" />
                </Button>
              </div>
            </div>

            {/* Configuration Summary */}
            <div className="grid grid-cols-2 md:grid-cols-4 gap-4 pt-4 border-t">
              <div>
                <p className="text-sm text-muted-foreground">
                  {t("apps.form.providerType")}
                </p>
                <p className="font-medium">{app.providerType}</p>
              </div>
              <div>
                <p className="text-sm text-muted-foreground">
                  {t("apps.form.defaultModel")}
                </p>
                <p className="font-medium">{app.defaultModel || "-"}</p>
              </div>
              <div>
                <p className="text-sm text-muted-foreground">
                  {t("apps.form.domainValidation")}
                </p>
                <p className="font-medium">
                  {app.enableDomainValidation
                    ? t("apps.card.active")
                    : t("apps.card.inactive")}
                </p>
              </div>
              <div>
                <p className="text-sm text-muted-foreground">
                  {t("apps.form.isActive")}
                </p>
                <p
                  className={`font-medium ${
                    app.isActive ? "text-green-600" : "text-gray-500"
                  }`}
                >
                  {app.isActive ? t("apps.card.active") : t("apps.card.inactive")}
                </p>
              </div>
            </div>
          </CardContent>
        </Card>

        {/* Statistics and Logs Tabs */}
        <Tabs defaultValue="statistics" className="w-full">
          <TabsList>
            <TabsTrigger value="statistics">
              {t("apps.statistics.title")}
            </TabsTrigger>
            <TabsTrigger value="logs">{t("apps.logs.title")}</TabsTrigger>
          </TabsList>
          <TabsContent value="statistics" className="mt-4">
            <AppStatisticsChart appId={app.id} />
          </TabsContent>
          <TabsContent value="logs" className="mt-4">
            <AppLogsTable appId={app.id} />
          </TabsContent>
        </Tabs>
      </div>

      {/* Edit Dialog */}
      <AppFormDialog
        open={isFormOpen}
        onOpenChange={setIsFormOpen}
        app={app}
        onSuccess={handleEditSuccess}
      />

      {/* Regenerate Secret Confirmation */}
      <AlertDialog
        open={showRegenerateDialog}
        onOpenChange={setShowRegenerateDialog}
      >
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>
              {t("apps.detail.regenerateSecret")}
            </AlertDialogTitle>
            <AlertDialogDescription>
              {t("apps.detail.regenerateConfirm")}
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel disabled={isRegenerating}>
              {t("common.cancel")}
            </AlertDialogCancel>
            <AlertDialogAction
              onClick={handleRegenerateSecret}
              disabled={isRegenerating}
            >
              {isRegenerating && (
                <Loader2 className="h-4 w-4 animate-spin mr-2" />
              )}
              {t("common.confirm")}
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>

      {/* Delete Confirmation */}
      <AlertDialog open={showDeleteDialog} onOpenChange={setShowDeleteDialog}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>{t("apps.detail.deleteApp")}</AlertDialogTitle>
            <AlertDialogDescription>
              {t("apps.detail.deleteConfirm")}
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel disabled={isDeleting}>
              {t("common.cancel")}
            </AlertDialogCancel>
            <AlertDialogAction
              onClick={handleDelete}
              disabled={isDeleting}
              className="bg-destructive text-destructive-foreground hover:bg-destructive/90"
            >
              {isDeleting && <Loader2 className="h-4 w-4 animate-spin mr-2" />}
              {t("common.delete")}
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </AppLayout>
  );
}
