"use client";

import React, { useEffect, useState, useCallback } from "react";
import { Card } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import {
  Dialog, DialogContent, DialogHeader, DialogTitle, DialogFooter,
} from "@/components/ui/dialog";
import {
  AlertDialog, AlertDialogAction, AlertDialogCancel, AlertDialogContent,
  AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import {
  Select, SelectContent, SelectItem, SelectTrigger, SelectValue,
} from "@/components/ui/select";
import { Badge } from "@/components/ui/badge";
import {
  getApiKeys, createApiKey, revokeApiKey, ApiKeyListItem, ApiKeyCreateResult,
} from "@/lib/admin-api";
import { getUsers, AdminUser } from "@/lib/admin-api";
import {
  Loader2, Trash2, RefreshCw, Plus, Key, Copy, Check, AlertTriangle,
} from "lucide-react";
import { toast } from "sonner";
import { useTranslations } from "@/hooks/use-translations";
import { PageHeader } from "@/components/admin/page-header";
import { useLocale } from "next-intl";

export default function AdminApiKeysPage() {
  const [apiKeys, setApiKeys] = useState<ApiKeyListItem[]>([]);
  const [users, setUsers] = useState<AdminUser[]>([]);
  const [loading, setLoading] = useState(true);
  const [showCreateDialog, setShowCreateDialog] = useState(false);
  const [showKeyDialog, setShowKeyDialog] = useState(false);
  const [createdKey, setCreatedKey] = useState<ApiKeyCreateResult | null>(null);
  const [copied, setCopied] = useState(false);
  const [deleteId, setDeleteId] = useState<string | null>(null);
  const [formData, setFormData] = useState({
    name: "",
    userId: "",
    scope: "mcp:read",
    expiresInDays: "",
  });
  const t = useTranslations();
  const locale = useLocale();

  const loadData = useCallback(() => {
    return Promise.all([
        getApiKeys(),
        getUsers(1, 100),
      ]).then(([keys, userResult]) => {
      setApiKeys(keys);
      setUsers(userResult.items);
    }).catch(error => {
      console.error("Failed to fetch API keys:", error);
      toast.error(t("admin.apiKeys.fetchFailed"));
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

  const handleCreate = async () => {
    if (!formData.name.trim()) {
      toast.error(t("admin.apiKeys.nameRequired"));
      return;
    }
    if (!formData.userId) {
      toast.error(t("admin.apiKeys.userRequired"));
      return;
    }
    try {
      const result = await createApiKey({
        name: formData.name,
        userId: formData.userId,
        scope: formData.scope || undefined,
        expiresInDays: formData.expiresInDays ? parseInt(formData.expiresInDays) : undefined,
      });
      setCreatedKey(result);
      setShowCreateDialog(false);
      setShowKeyDialog(true);
      setCopied(false);
      fetchData();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : t("admin.apiKeys.createFailed"));
    }
  };

  const handleCopyKey = async () => {
    if (createdKey?.plainTextKey) {
      await navigator.clipboard.writeText(createdKey.plainTextKey);
      setCopied(true);
      toast.success(t("admin.apiKeys.copied"));
      setTimeout(() => setCopied(false), 3000);
    }
  };

  const handleRevoke = async () => {
    if (!deleteId) return;
    try {
      await revokeApiKey(deleteId);
      toast.success(t("admin.apiKeys.revoked"));
      setDeleteId(null);
      fetchData();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : t("admin.apiKeys.revokeFailed"));
    }
  };

  const openCreateDialog = () => {
    setFormData({ name: "", userId: "", scope: "mcp:read", expiresInDays: "" });
    setShowCreateDialog(true);
  };

  return (
    <div className="space-y-5">
      <PageHeader
        title={t("admin.apiKeys.title")}
        description={t("admin.apiKeys.subtitle")}
        actions={
          <>
            <Button variant="outline" onClick={fetchData}>
              <RefreshCw className="mr-1.5 h-4 w-4" />
              {t("admin.common.refresh")}
            </Button>
            <Button onClick={openCreateDialog}>
              <Plus className="mr-1.5 h-4 w-4" />
              {t("admin.apiKeys.createKey")}
            </Button>
          </>
        }
      />

      {/* API Keys Table */}
      {loading ? (
        <div className="flex h-64 items-center justify-center">
          <Loader2 className="h-8 w-8 animate-spin text-muted-foreground" />
        </div>
      ) : apiKeys.length === 0 ? (
        <Card className="flex h-64 flex-col items-center justify-center gap-4 p-6">
          <Key className="h-12 w-12 text-muted-foreground" />
          <p className="text-muted-foreground">{t("admin.apiKeys.noKeys")}</p>
          <Button onClick={openCreateDialog}>
            <Plus className="mr-2 h-4 w-4" />
            {t("admin.apiKeys.createKey")}
          </Button>
        </Card>
      ) : (
        <Card className="overflow-hidden">
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b bg-muted/50">
                  <th className="px-4 py-3 text-left font-medium">{t("admin.apiKeys.name")}</th>
                  <th className="px-4 py-3 text-left font-medium">{t("admin.apiKeys.keyPrefix")}</th>
                  <th className="px-4 py-3 text-left font-medium">{t("admin.apiKeys.user")}</th>
                  <th className="px-4 py-3 text-left font-medium">{t("admin.apiKeys.scope")}</th>
                  <th className="px-4 py-3 text-left font-medium">{t("admin.apiKeys.expires")}</th>
                  <th className="px-4 py-3 text-left font-medium">{t("admin.apiKeys.lastUsed")}</th>
                  <th className="px-4 py-3 text-left font-medium">{t("admin.apiKeys.created")}</th>
                  <th className="px-4 py-3 text-right font-medium">{t("admin.apiKeys.actions")}</th>
                </tr>
              </thead>
              <tbody>
                {apiKeys.map((key) => (
                  <tr key={key.id} className="border-b last:border-0 hover:bg-muted/30">
                    <td className="px-4 py-3 font-medium">{key.name}</td>
                    <td className="px-4 py-3">
                      <code className="rounded bg-muted px-2 py-0.5 text-xs">dwk_{key.keyPrefix}...</code>
                    </td>
                    <td className="px-4 py-3 text-muted-foreground">{key.userEmail || key.userId}</td>
                    <td className="px-4 py-3">
                      <Badge variant="secondary">{key.scope}</Badge>
                    </td>
                    <td className="px-4 py-3 text-muted-foreground">
                      {key.expiresAt
                        ? new Date(key.expiresAt).toLocaleDateString(locale === "zh" ? "zh-CN" : locale)
                        : t("admin.apiKeys.never")}
                    </td>
                    <td className="px-4 py-3 text-muted-foreground">
                      {key.lastUsedAt
                        ? new Date(key.lastUsedAt).toLocaleDateString(locale === "zh" ? "zh-CN" : locale)
                        : t("admin.apiKeys.neverUsed")}
                    </td>
                    <td className="px-4 py-3 text-muted-foreground">
                      {new Date(key.createdAt).toLocaleDateString(locale === "zh" ? "zh-CN" : locale)}
                    </td>
                    <td className="px-4 py-3 text-right">
                      <Button
                        variant="ghost"
                        size="icon"
                        onClick={() => setDeleteId(key.id)}
                      >
                        <Trash2 className="h-4 w-4 text-red-500" />
                      </Button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </Card>
      )}

      {/* Create API Key Dialog */}
      <Dialog open={showCreateDialog} onOpenChange={setShowCreateDialog}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>{t("admin.apiKeys.createKey")}</DialogTitle>
          </DialogHeader>
          <div className="space-y-4">
            <div>
              <label className="text-sm font-medium">{t("admin.apiKeys.name")} *</label>
              <Input
                value={formData.name}
                onChange={(e) => setFormData({ ...formData, name: e.target.value })}
                placeholder={t("admin.apiKeys.namePlaceholder")}
              />
            </div>
            <div>
              <label className="text-sm font-medium">{t("admin.apiKeys.user")} *</label>
              <Select
                value={formData.userId}
                onValueChange={(value) => setFormData({ ...formData, userId: value })}
              >
                <SelectTrigger>
                  <SelectValue placeholder={t("admin.apiKeys.selectUser")} />
                </SelectTrigger>
                <SelectContent>
                  {users.map((user) => (
                    <SelectItem key={user.id} value={user.id}>
                      {user.email || user.name} {user.roles?.includes("Admin") ? "(Admin)" : ""}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>
            <div>
              <label className="text-sm font-medium">{t("admin.apiKeys.scope")}</label>
              <Select
                value={formData.scope}
                onValueChange={(value) => setFormData({ ...formData, scope: value })}
              >
                <SelectTrigger>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="mcp:read">mcp:read</SelectItem>
                </SelectContent>
              </Select>
            </div>
            <div>
              <label className="text-sm font-medium">{t("admin.apiKeys.expiresInDays")}</label>
              <Input
                type="number"
                value={formData.expiresInDays}
                onChange={(e) => setFormData({ ...formData, expiresInDays: e.target.value })}
                placeholder={t("admin.apiKeys.expiresPlaceholder")}
                min="1"
              />
            </div>
          </div>
          <DialogFooter>
            <Button variant="outline" onClick={() => setShowCreateDialog(false)}>
              {t("admin.common.cancel")}
            </Button>
            <Button onClick={handleCreate}>
              {t("admin.common.create")}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      {/* Show Created Key Dialog */}
      <Dialog open={showKeyDialog} onOpenChange={setShowKeyDialog}>
        <DialogContent className="sm:max-w-lg">
          <DialogHeader>
            <DialogTitle className="flex items-center gap-2">
              <AlertTriangle className="h-5 w-5 text-yellow-500" />
              {t("admin.apiKeys.keyCreated")}
            </DialogTitle>
          </DialogHeader>
          <div className="space-y-4">
            <div className="rounded-lg border border-yellow-200 bg-yellow-50 p-4 dark:border-yellow-900 dark:bg-yellow-950">
              <p className="text-sm font-medium text-yellow-800 dark:text-yellow-200">
                {t("admin.apiKeys.keyWarning")}
              </p>
            </div>
            <div>
              <label className="text-sm font-medium">{t("admin.apiKeys.yourKey")}</label>
              <div className="mt-1 flex gap-2">
                <code className="flex-1 rounded border bg-muted p-3 text-xs font-mono break-all">
                  {createdKey?.plainTextKey}
                </code>
                <Button variant="outline" size="icon" onClick={handleCopyKey}>
                  {copied ? <Check className="h-4 w-4 text-green-500" /> : <Copy className="h-4 w-4" />}
                </Button>
              </div>
            </div>
            <div className="text-sm text-muted-foreground">
              <p><strong>{t("admin.apiKeys.name")}:</strong> {createdKey?.name}</p>
              <p><strong>{t("admin.apiKeys.scope")}:</strong> {createdKey?.scope}</p>
              {createdKey?.expiresAt && (
                <p><strong>{t("admin.apiKeys.expires")}:</strong> {new Date(createdKey.expiresAt).toLocaleDateString()}</p>
              )}
            </div>
          </div>
          <DialogFooter>
            <Button onClick={() => setShowKeyDialog(false)}>
              {t("admin.apiKeys.done")}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      {/* Revoke Confirmation Dialog */}
      <AlertDialog open={!!deleteId} onOpenChange={() => setDeleteId(null)}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>{t("admin.apiKeys.revokeTitle")}</AlertDialogTitle>
            <AlertDialogDescription>
              {t("admin.apiKeys.revokeWarning")}
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>{t("admin.common.cancel")}</AlertDialogCancel>
            <AlertDialogAction onClick={handleRevoke} className="bg-red-600 hover:bg-red-700">
              {t("admin.apiKeys.revoke")}
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </div>
  );
}
