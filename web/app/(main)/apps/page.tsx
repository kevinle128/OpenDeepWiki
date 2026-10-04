"use client";

import { useState, useEffect, useCallback } from "react";
import Link from "next/link";
import { AppLayout } from "@/components/app-layout";
import { useTranslations } from "@/hooks/use-translations";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { AppWindow, Plus, Loader2, MoreVertical, Pencil, Trash2 } from "lucide-react";
import { useAuth } from "@/contexts/auth-context";
import { getUserApps, deleteApp, ChatAppDto } from "@/lib/apps-api";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
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
import { AppFormDialog } from "@/components/apps/app-form-dialog";

export default function AppsPage() {
  const t = useTranslations();
  const { isAuthenticated, isLoading: authLoading } = useAuth();
  const [activeItem, setActiveItem] = useState(t("sidebar.apps"));
  const [apps, setApps] = useState<ChatAppDto[]>([]);
  const [isLoading, setIsLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [isFormOpen, setIsFormOpen] = useState(false);
  const [editingApp, setEditingApp] = useState<ChatAppDto | null>(null);
  const [deletingApp, setDeletingApp] = useState<ChatAppDto | null>(null);
  const [isDeleting, setIsDeleting] = useState(false);

  const fetchApps = useCallback(() => {
    return getUserApps().then(data => {
      setApps(data);
      setError(null);
    }).catch(err => {
      setError(err instanceof Error ? err.message : "Failed to load apps");
    }).finally(() => {
      setIsLoading(false);
    });
  }, []);

  useEffect(() => {
    if (!authLoading && isAuthenticated) {
      fetchApps();
    }
  }, [authLoading, isAuthenticated, fetchApps]);

  const handleCreateSuccess = useCallback(() => {
    setIsFormOpen(false);
    setEditingApp(null);
    setIsLoading(true);
    setError(null);
    fetchApps();
  }, [fetchApps]);

  const handleEdit = (app: ChatAppDto) => {
    setEditingApp(app);
    setIsFormOpen(true);
  };

  const handleDelete = async () => {
    if (!deletingApp) return;
    
    setIsDeleting(true);
    try {
      await deleteApp(deletingApp.id);
      setApps(prev => prev.filter(a => a.id !== deletingApp.id));
      setDeletingApp(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : t("apps.detail.deleteFailed"));
    } finally {
      setIsDeleting(false);
    }
  };

  const formatDate = (dateStr: string) => {
    const date = new Date(dateStr);
    return date.toLocaleDateString();
  };

  // Show login prompt if not authenticated
  if (!authLoading && !isAuthenticated) {
    return (
      <AppLayout activeItem={activeItem} onItemClick={setActiveItem}>
        <div className="flex flex-1 flex-col items-center justify-center gap-4 p-4 md:p-6">
          <AppWindow className="h-16 w-16 text-muted-foreground" />
          <h2 className="text-xl font-semibold">{t("apps.loginRequired")}</h2>
          <p className="text-muted-foreground">{t("apps.loginHint")}</p>
        </div>
      </AppLayout>
    );
  }

  return (
    <AppLayout activeItem={activeItem} onItemClick={setActiveItem}>
      <div className="flex flex-1 flex-col gap-4 p-4 md:p-6">
        <div className="flex items-center justify-between">
          <div className="space-y-2">
            <h1 className="text-3xl font-bold tracking-tight">{t("apps.title")}</h1>
            <p className="text-muted-foreground">{t("apps.description")}</p>
          </div>
          <Button className="gap-2" onClick={() => setIsFormOpen(true)}>
            <Plus className="h-4 w-4" />
            {t("apps.createApp")}
          </Button>
        </div>

        {error && (
          <div className="bg-destructive/10 text-destructive px-4 py-2 rounded-md">
            {error}
          </div>
        )}

        {isLoading || authLoading ? (
          <div className="flex items-center justify-center py-12">
            <Loader2 className="h-8 w-8 animate-spin text-muted-foreground" />
          </div>
        ) : apps.length === 0 ? (
          <div className="flex flex-col items-center justify-center py-12 gap-4">
            <AppWindow className="h-16 w-16 text-muted-foreground" />
            <h2 className="text-xl font-semibold">{t("apps.empty")}</h2>
            <p className="text-muted-foreground">{t("apps.emptyHint")}</p>
            <Button className="gap-2" onClick={() => setIsFormOpen(true)}>
              <Plus className="h-4 w-4" />
              {t("apps.createApp")}
            </Button>
          </div>
        ) : (
          <div className="grid gap-4 md:grid-cols-2 lg:grid-cols-3">
            {apps.map((app) => (
              <Link key={app.id} href={`/apps/${app.id}`} className="block">
                <Card className="hover:shadow-lg transition-shadow cursor-pointer h-full">
                  <CardHeader>
                    <div className="flex items-start justify-between">
                      <div className="flex-1 min-w-0">
                        <div className="flex items-center gap-2">
                          <AppWindow className="h-4 w-4 text-primary flex-shrink-0" />
                          <CardTitle className="text-lg truncate">{app.name}</CardTitle>
                        </div>
                        <CardDescription className="text-sm text-muted-foreground mt-1 truncate">
                          {app.providerType}
                        </CardDescription>
                      </div>
                      <DropdownMenu>
                        <DropdownMenuTrigger asChild>
                          <Button
                            variant="ghost"
                            size="sm"
                            className="h-8 w-8 p-0 flex-shrink-0"
                            onClick={(e) => e.preventDefault()}
                          >
                            <MoreVertical className="h-4 w-4" />
                          </Button>
                        </DropdownMenuTrigger>
                        <DropdownMenuContent align="end">
                          <DropdownMenuItem
                            onClick={(e) => {
                              e.preventDefault();
                              handleEdit(app);
                            }}
                          >
                            <Pencil className="h-4 w-4 mr-2" />
                            {t("common.edit")}
                          </DropdownMenuItem>
                          <DropdownMenuItem
                            className="text-destructive"
                            onClick={(e) => {
                              e.preventDefault();
                              setDeletingApp(app);
                            }}
                          >
                            <Trash2 className="h-4 w-4 mr-2" />
                            {t("common.delete")}
                          </DropdownMenuItem>
                        </DropdownMenuContent>
                      </DropdownMenu>
                    </div>
                  </CardHeader>
                  <CardContent>
                    <p className="text-sm mb-3 line-clamp-2">
                      {app.description || t("apps.emptyHint")}
                    </p>
                    <div className="flex items-center justify-between text-sm text-muted-foreground">
                      <span className={app.isActive ? "text-green-600" : "text-gray-500"}>
                        {app.isActive ? t("apps.card.active") : t("apps.card.inactive")}
                      </span>
                      <span>{t("apps.card.created")} {formatDate(app.createdAt)}</span>
                    </div>
                  </CardContent>
                </Card>
              </Link>
            ))}
          </div>
        )}
      </div>

      {/* Create/Edit Dialog */}
      <AppFormDialog
        open={isFormOpen}
        onOpenChange={(open) => {
          setIsFormOpen(open);
          if (!open) setEditingApp(null);
        }}
        app={editingApp}
        onSuccess={handleCreateSuccess}
      />

      {/* Delete Confirmation Dialog */}
      <AlertDialog open={!!deletingApp} onOpenChange={(open) => !open && setDeletingApp(null)}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>{t("apps.detail.deleteApp")}</AlertDialogTitle>
            <AlertDialogDescription>
              {t("apps.detail.deleteConfirm")}
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel disabled={isDeleting}>{t("common.cancel")}</AlertDialogCancel>
            <AlertDialogAction
              onClick={handleDelete}
              disabled={isDeleting}
              className="bg-destructive text-destructive-foreground hover:bg-destructive/90"
            >
              {isDeleting ? <Loader2 className="h-4 w-4 animate-spin mr-2" /> : null}
              {t("common.delete")}
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </AppLayout>
  );
}
