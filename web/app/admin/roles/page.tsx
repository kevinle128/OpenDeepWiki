"use client";

import React, { useCallback, useEffect, useState } from "react";
import {
  Edit,
  Plus,
  RefreshCw,
  Shield,
  ShieldCheck,
  Trash2,
  Users,
} from "lucide-react";
import { toast } from "sonner";
import { useLocale } from "next-intl";

import { Card } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Textarea } from "@/components/ui/textarea";
import { Badge } from "@/components/ui/badge";
import { Skeleton } from "@/components/ui/skeleton";
import {
  Empty,
  EmptyHeader,
  EmptyMedia,
  EmptyTitle,
} from "@/components/ui/empty";
import {
  Dialog,
  DialogContent,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
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
import { PageHeader } from "@/components/admin/page-header";
import {
  getRoles,
  createRole,
  updateRole,
  deleteRole,
  AdminRole,
} from "@/lib/admin-api";
import { useTranslations } from "@/hooks/use-translations";

export default function AdminRolesPage() {
  const [roles, setRoles] = useState<AdminRole[]>([]);
  const [loading, setLoading] = useState(true);
  const [showDialog, setShowDialog] = useState(false);
  const [editingRole, setEditingRole] = useState<AdminRole | null>(null);
  const [deleteId, setDeleteId] = useState<string | null>(null);
  const [formData, setFormData] = useState({ name: "", description: "" });
  const t = useTranslations();
  const locale = useLocale();
  const dateLocale = locale === "zh" ? "zh-CN" : locale;

  const loadData = useCallback(() => {
    return getRoles().then((result) => {
      setRoles(result);
    }).catch(error => {
      console.error("Failed to fetch roles:", error);
      toast.error(t("admin.toast.fetchRoleFailed"));
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

  const openCreateDialog = () => {
    setEditingRole(null);
    setFormData({ name: "", description: "" });
    setShowDialog(true);
  };

  const openEditDialog = (role: AdminRole) => {
    setEditingRole(role);
    setFormData({ name: role.name, description: role.description || "" });
    setShowDialog(true);
  };

  const handleSave = async () => {
    if (!formData.name.trim()) {
      toast.error(t("admin.toast.enterRoleName"));
      return;
    }
    try {
      if (editingRole) {
        await updateRole(editingRole.id, formData);
        toast.success(t("admin.toast.updateSuccess"));
      } else {
        await createRole(formData);
        toast.success(t("admin.toast.createSuccess"));
      }
      setShowDialog(false);
      fetchData();
    } catch {
      toast.error(
        editingRole ? t("admin.toast.updateFailed") : t("admin.toast.createFailed")
      );
    }
  };

  const handleDelete = async () => {
    if (!deleteId) return;
    try {
      await deleteRole(deleteId);
      toast.success(t("admin.toast.deleteSuccess"));
      setDeleteId(null);
      fetchData();
    } catch (error) {
      toast.error(
        error instanceof Error ? error.message : t("admin.toast.deleteFailed")
      );
    }
  };

  return (
    <div className="space-y-5">
      <PageHeader
        title={t("admin.roles.title")}
        actions={
          <>
            <Button variant="outline" onClick={fetchData} disabled={loading}>
              <RefreshCw
                className={`mr-1.5 h-4 w-4 ${loading ? "animate-spin" : ""}`}
              />
              {t("admin.common.refresh")}
            </Button>
            <Button onClick={openCreateDialog}>
              <Plus className="mr-1.5 h-4 w-4" />
              {t("admin.roles.createRole")}
            </Button>
          </>
        }
      />

      {loading ? (
        <div className="grid gap-4 md:grid-cols-2 lg:grid-cols-3">
          {Array.from({ length: 6 }).map((_, i) => (
            <Skeleton key={i} className="h-[170px] rounded-lg" />
          ))}
        </div>
      ) : roles.length === 0 ? (
        <Empty>
          <EmptyHeader>
            <EmptyMedia variant="icon">
              <Shield className="h-5 w-5" />
            </EmptyMedia>
            <EmptyTitle>{t("admin.shared.noData")}</EmptyTitle>
          </EmptyHeader>
        </Empty>
      ) : (
        <div className="grid gap-4 md:grid-cols-2 lg:grid-cols-3">
          {roles.map((role) => (
            <Card
              key={role.id}
              className="gap-0 rounded-lg p-5 shadow-none transition-colors hover:border-foreground/20"
            >
              <div className="flex items-start justify-between gap-2">
                <div className="flex min-w-0 items-center gap-3">
                  <div
                    className={`rounded-md p-2 ${
                      role.isSystem
                        ? "bg-violet-500/10 text-violet-600 dark:text-violet-400"
                        : "bg-primary/10 text-primary"
                    }`}
                  >
                    {role.isSystem ? (
                      <ShieldCheck className="h-4 w-4" />
                    ) : (
                      <Shield className="h-4 w-4" />
                    )}
                  </div>
                  <div className="min-w-0">
                    <h3 className="truncate font-semibold">{role.name}</h3>
                    {role.isSystem && (
                      <Badge
                        variant="secondary"
                        className="mt-0.5 text-[10px] font-normal"
                      >
                        {t("admin.roles.systemRole")}
                      </Badge>
                    )}
                  </div>
                </div>
                {!role.isSystem && (
                  <div className="flex shrink-0 gap-1">
                    <Button
                      variant="ghost"
                      size="icon"
                      className="h-8 w-8"
                      onClick={() => openEditDialog(role)}
                    >
                      <Edit className="h-4 w-4" />
                    </Button>
                    <Button
                      variant="ghost"
                      size="icon"
                      className="h-8 w-8"
                      onClick={() => setDeleteId(role.id)}
                    >
                      <Trash2 className="h-4 w-4 text-red-500" />
                    </Button>
                  </div>
                )}
              </div>
              <p className="mt-3 line-clamp-2 min-h-10 text-sm text-muted-foreground">
                {role.description || "—"}
              </p>
              <div className="mt-3 flex items-center justify-between border-t pt-3 text-xs text-muted-foreground">
                <span className="inline-flex items-center gap-1.5">
                  <Users className="h-3.5 w-3.5" />
                  {t("admin.roles.usersCount", { count: role.userCount })}
                </span>
                <span>
                  {t("admin.roles.createdAt", {
                    date: new Date(role.createdAt).toLocaleDateString(dateLocale),
                  })}
                </span>
              </div>
            </Card>
          ))}
        </div>
      )}

      {/* 新增/编辑对话框 */}
      <Dialog open={showDialog} onOpenChange={setShowDialog}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>
              {editingRole ? t("admin.roles.editRole") : t("admin.roles.createRole")}
            </DialogTitle>
          </DialogHeader>
          <div className="space-y-4">
            <div className="space-y-1.5">
              <label className="text-sm font-medium">
                {t("admin.roles.roleName")} *
              </label>
              <Input
                value={formData.name}
                onChange={(e) => setFormData({ ...formData, name: e.target.value })}
                placeholder={t("admin.roles.enterRoleName")}
                disabled={editingRole?.isSystem}
              />
            </div>
            <div className="space-y-1.5">
              <label className="text-sm font-medium">
                {t("admin.roles.description")}
              </label>
              <Textarea
                value={formData.description}
                onChange={(e) =>
                  setFormData({ ...formData, description: e.target.value })
                }
                placeholder={t("admin.roles.enterRoleDesc")}
                rows={3}
              />
            </div>
          </div>
          <DialogFooter>
            <Button variant="outline" onClick={() => setShowDialog(false)}>
              {t("admin.common.cancel")}
            </Button>
            <Button onClick={handleSave}>
              {editingRole ? t("admin.common.save") : t("admin.common.create")}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      {/* 删除确认对话框 */}
      <AlertDialog open={!!deleteId} onOpenChange={() => setDeleteId(null)}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>{t("admin.common.confirmDelete")}</AlertDialogTitle>
            <AlertDialogDescription>
              {t("admin.roles.deleteWarning")}
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>{t("admin.common.cancel")}</AlertDialogCancel>
            <AlertDialogAction
              onClick={handleDelete}
              className="bg-red-600 hover:bg-red-700"
            >
              {t("admin.common.delete")}
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </div>
  );
}
