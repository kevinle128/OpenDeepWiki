"use client";

import React, { useEffect, useState, useCallback } from "react";
import { Card } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Textarea } from "@/components/ui/textarea";
import { Switch } from "@/components/ui/switch";
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
  DialogFooter,
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
import {
  getMcpConfigs,
  createMcpConfig,
  updateMcpConfig,
  deleteMcpConfig,
  McpConfig,
} from "@/lib/admin-api";
import {
  Loader2,
  Trash2,
  Edit,
  RefreshCw,
  Plus,
  Server,
  CheckCircle,
  XCircle,
} from "lucide-react";
import { toast } from "sonner";
import { useTranslations } from "@/hooks/use-translations";
import { PageHeader } from "@/components/admin/page-header";

export default function AdminMcpsPage() {
  const [configs, setConfigs] = useState<McpConfig[]>([]);
  const [loading, setLoading] = useState(true);
  const [showDialog, setShowDialog] = useState(false);
  const [editingConfig, setEditingConfig] = useState<McpConfig | null>(null);
  const [deleteId, setDeleteId] = useState<string | null>(null);
  const [formData, setFormData] = useState({
    name: "",
    description: "",
    serverUrl: "",
    apiKey: "",
    isActive: true,
    sortOrder: 0,
  });
  const t = useTranslations();

  const loadData = useCallback(() => {
    return getMcpConfigs().then((result) => {
      setConfigs(result);
    }).catch(error => {
      console.error("Failed to fetch MCP configs:", error);
      toast.error(t('admin.toast.fetchMcpFailed'));
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
    setEditingConfig(null);
    setFormData({
      name: "",
      description: "",
      serverUrl: "",
      apiKey: "",
      isActive: true,
      sortOrder: 0,
    });
    setShowDialog(true);
  };

  const openEditDialog = (config: McpConfig) => {
    setEditingConfig(config);
    setFormData({
      name: config.name,
      description: config.description || "",
      serverUrl: config.serverUrl,
      apiKey: config.apiKey || "",
      isActive: config.isActive,
      sortOrder: config.sortOrder,
    });
    setShowDialog(true);
  };

  const handleSave = async () => {
    if (!formData.name.trim() || !formData.serverUrl.trim()) {
      toast.error(t('admin.toast.fillRequired'));
      return;
    }
    try {
      if (editingConfig) {
        await updateMcpConfig(editingConfig.id, formData);
        toast.success(t('admin.toast.updateSuccess'));
      } else {
        await createMcpConfig(formData);
        toast.success(t('admin.toast.createSuccess'));
      }
      setShowDialog(false);
      fetchData();
    } catch {
      toast.error(editingConfig ? t('admin.toast.updateFailed') : t('admin.toast.createFailed'));
    }
  };

  const handleDelete = async () => {
    if (!deleteId) return;
    try {
      await deleteMcpConfig(deleteId);
      toast.success(t('admin.toast.deleteSuccess'));
      setDeleteId(null);
      fetchData();
    } catch {
      toast.error(t('admin.toast.deleteFailed'));
    }
  };

  const handleToggleActive = async (config: McpConfig) => {
    try {
      await updateMcpConfig(config.id, { isActive: !config.isActive });
      toast.success(config.isActive ? t('admin.mcps.disabled') : t('admin.mcps.enabled'));
      fetchData();
    } catch {
      toast.error(t('admin.toast.operationFailed'));
    }
  };

  return (
    <div className="space-y-5">
      <PageHeader
        title={t('admin.mcps.title')}
        actions={
          <>
            <Button variant="outline" onClick={fetchData}>
              <RefreshCw className="mr-1.5 h-4 w-4" />
              {t('admin.common.refresh')}
            </Button>
            <Button onClick={openCreateDialog}>
              <Plus className="mr-1.5 h-4 w-4" />
              {t('admin.mcps.createMcp')}
            </Button>
          </>
        }
      />

      {/* 配置列表 */}
      {loading ? (
        <div className="flex h-64 items-center justify-center">
          <Loader2 className="h-8 w-8 animate-spin text-muted-foreground" />
        </div>
      ) : configs.length === 0 ? (
        <Card className="flex h-64 items-center justify-center">
          <div className="text-center">
            <Server className="mx-auto h-12 w-12 text-muted-foreground" />
            <p className="mt-4 text-muted-foreground">{t('admin.mcps.noMcps')}</p>
            <Button className="mt-4" onClick={openCreateDialog}>
              <Plus className="mr-2 h-4 w-4" />
              {t('admin.mcps.addFirst')}
            </Button>
          </div>
        </Card>
      ) : (
        <div className="grid gap-4 md:grid-cols-2 lg:grid-cols-3">
          {configs.map((config) => (
            <Card key={config.id} className="p-6">
              <div className="flex items-start justify-between">
                <div className="flex items-center gap-3">
                  <div className={`rounded-full p-2 ${config.isActive ? "bg-green-100 dark:bg-green-900" : "bg-gray-100 dark:bg-gray-800"}`}>
                    <Server className={`h-5 w-5 ${config.isActive ? "text-green-600 dark:text-green-400" : "text-gray-500"}`} />
                  </div>
                  <div>
                    <h3 className="font-semibold">{config.name}</h3>
                    <div className="flex items-center gap-1 text-xs">
                      {config.isActive ? (
                        <><CheckCircle className="h-3 w-3 text-green-500" /> {t('admin.mcps.enabled')}</>
                      ) : (
                        <><XCircle className="h-3 w-3 text-gray-400" /> {t('admin.mcps.disabled')}</>
                      )}
                    </div>
                  </div>
                </div>
                <div className="flex gap-1">
                  <Button variant="ghost" size="icon" onClick={() => openEditDialog(config)}>
                    <Edit className="h-4 w-4" />
                  </Button>
                  <Button variant="ghost" size="icon" onClick={() => setDeleteId(config.id)}>
                    <Trash2 className="h-4 w-4 text-red-500" />
                  </Button>
                </div>
              </div>
              {config.description && (
                <p className="mt-3 text-sm text-muted-foreground line-clamp-2">
                  {config.description}
                </p>
              )}
              <p className="mt-3 text-xs text-muted-foreground truncate">
                {config.serverUrl}
              </p>
              <div className="mt-4 flex items-center justify-between">
                <span className="text-xs text-muted-foreground">
                  {t('admin.mcps.sortOrder')}: {config.sortOrder}
                </span>
                <Switch
                  checked={config.isActive}
                  onCheckedChange={() => handleToggleActive(config)}
                />
              </div>
            </Card>
          ))}
        </div>
      )}

      {/* 新增/编辑对话框 */}
      <Dialog open={showDialog} onOpenChange={setShowDialog}>
        <DialogContent className="max-w-md">
          <DialogHeader>
            <DialogTitle>{editingConfig ? t('admin.mcps.editMcp') : t('admin.mcps.createMcp')}</DialogTitle>
          </DialogHeader>
          <div className="space-y-4">
            <div>
              <label className="text-sm font-medium">{t('admin.mcps.name')} *</label>
              <Input
                value={formData.name}
                onChange={(e) => setFormData({ ...formData, name: e.target.value })}
                placeholder={t('admin.mcps.mcpName')}
              />
            </div>
            <div>
              <label className="text-sm font-medium">{t('admin.mcps.serverUrl')} *</label>
              <Input
                value={formData.serverUrl}
                onChange={(e) => setFormData({ ...formData, serverUrl: e.target.value })}
                placeholder="https://example.com/mcp"
              />
            </div>
            <div>
              <label className="text-sm font-medium">{t('admin.mcps.apiKey')}</label>
              <Input
                type="password"
                value={formData.apiKey}
                onChange={(e) => setFormData({ ...formData, apiKey: e.target.value })}
                placeholder={t('admin.mcps.optional')}
              />
            </div>
            <div>
              <label className="text-sm font-medium">{t('admin.mcps.description')}</label>
              <Textarea
                value={formData.description}
                onChange={(e) => setFormData({ ...formData, description: e.target.value })}
                placeholder={t('admin.mcps.mcpDesc')}
                rows={2}
              />
            </div>
            <div className="flex items-center justify-between">
              <div>
                <label className="text-sm font-medium">{t('admin.mcps.sortOrder')}</label>
                <Input
                  type="number"
                  value={formData.sortOrder}
                  onChange={(e) => setFormData({ ...formData, sortOrder: parseInt(e.target.value) || 0 })}
                  className="mt-1 w-24"
                />
              </div>
              <div className="flex items-center gap-2">
                <label className="text-sm font-medium">{t('admin.mcps.enable')}</label>
                <Switch
                  checked={formData.isActive}
                  onCheckedChange={(checked) => setFormData({ ...formData, isActive: checked })}
                />
              </div>
            </div>
          </div>
          <DialogFooter>
            <Button variant="outline" onClick={() => setShowDialog(false)}>{t('admin.common.cancel')}</Button>
            <Button onClick={handleSave}>{editingConfig ? t('admin.common.save') : t('admin.common.create')}</Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      {/* 删除确认对话框 */}
      <AlertDialog open={!!deleteId} onOpenChange={() => setDeleteId(null)}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>{t('admin.common.confirmDelete')}</AlertDialogTitle>
            <AlertDialogDescription>
              {t('admin.mcps.deleteWarning')}
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>{t('admin.common.cancel')}</AlertDialogCancel>
            <AlertDialogAction onClick={handleDelete} className="bg-red-600 hover:bg-red-700">
              {t('admin.common.delete')}
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </div>
  );
}
