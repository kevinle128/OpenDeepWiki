"use client";

import { useEffect, useState, useCallback, useRef } from "react";
import { Card } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Switch } from "@/components/ui/switch";
import {
  Dialog,
  DialogContent,
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
import {
  getSkillConfigs,
  getSkillDetail,
  uploadSkill,
  updateSkillConfig,
  deleteSkillConfig,
  refreshSkills,
  SkillConfig,
  SkillDetail,
} from "@/lib/admin-api";
import {
  Loader2,
  Trash2,
  Eye,
  RefreshCw,
  Upload,
  Sparkles,
  CheckCircle,
  XCircle,
  FolderOpen,
  FileCode,
  FileText,
  Image as ImageIcon,
  Package,
  ExternalLink,
} from "lucide-react";
import { toast } from "sonner";
import { useTranslations } from "@/hooks/use-translations";
import { PageHeader } from "@/components/admin/page-header";

function formatBytes(bytes: number): string {
  if (bytes === 0) return "0 B";
  const k = 1024;
  const sizes = ["B", "KB", "MB", "GB"];
  const i = Math.floor(Math.log(bytes) / Math.log(k));
  return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + " " + sizes[i];
}

export default function AdminSkillsPage() {
  const [configs, setConfigs] = useState<SkillConfig[]>([]);
  const [loading, setLoading] = useState(true);
  const [uploading, setUploading] = useState(false);
  const [detailDialog, setDetailDialog] = useState(false);
  const [selectedDetail, setSelectedDetail] = useState<SkillDetail | null>(null);
  const [deleteId, setDeleteId] = useState<string | null>(null);
  const fileInputRef = useRef<HTMLInputElement>(null);
  const t = useTranslations();

  const loadData = useCallback(() => {
    return getSkillConfigs().then((result) => {
      setConfigs(result);
    }).catch(error => {
      console.error("Failed to fetch Skill configs:", error);
      toast.error(t('admin.toast.fetchSkillFailed'));
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

  const handleUploadClick = () => {
    fileInputRef.current?.click();
  };

  const handleFileChange = async (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    if (!file) return;

    if (!file.name.endsWith(".zip")) {
      toast.error(t('admin.skills.onlyZip'));
      return;
    }

    setUploading(true);
    try {
      await uploadSkill(file);
      toast.success(t('admin.toast.uploadSuccess'));
      fetchData();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : t('admin.toast.uploadFailed'));
    } finally {
      setUploading(false);
      if (fileInputRef.current) {
        fileInputRef.current.value = "";
      }
    }
  };

  const handleViewDetail = async (config: SkillConfig) => {
    try {
      const detail = await getSkillDetail(config.id);
      setSelectedDetail(detail);
      setDetailDialog(true);
    } catch {
      toast.error(t('admin.toast.fetchDetailFailed'));
    }
  };

  const handleDelete = async () => {
    if (!deleteId) return;
    try {
      await deleteSkillConfig(deleteId);
      toast.success(t('admin.toast.deleteSuccess'));
      setDeleteId(null);
      fetchData();
    } catch {
      toast.error(t('admin.toast.deleteFailed'));
    }
  };

  const handleToggleActive = async (config: SkillConfig) => {
    try {
      await updateSkillConfig(config.id, { isActive: !config.isActive });
      toast.success(config.isActive ? t('admin.skills.disabled') : t('admin.skills.enabled'));
      fetchData();
    } catch {
      toast.error(t('admin.toast.operationFailed'));
    }
  };

  const handleRefresh = async () => {
    try {
      await refreshSkills();
      toast.success(t('admin.toast.refreshSuccess'));
      fetchData();
    } catch {
      toast.error(t('admin.toast.refreshFailed'));
    }
  };

  return (
    <div className="space-y-5">
      <PageHeader
        title={t('admin.skills.title')}
        actions={
          <>
            <Button variant="outline" onClick={handleRefresh}>
              <RefreshCw className="mr-1.5 h-4 w-4" />
              {t('admin.skills.refreshFromDisk')}
            </Button>
            <Button onClick={handleUploadClick} disabled={uploading}>
              {uploading ? (
                <Loader2 className="mr-1.5 h-4 w-4 animate-spin" />
              ) : (
                <Upload className="mr-1.5 h-4 w-4" />
              )}
              {t('admin.skills.uploadSkill')}
            </Button>
            <input
              ref={fileInputRef}
              type="file"
              accept=".zip"
              className="hidden"
              onChange={handleFileChange}
            />
          </>
        }
      >
        <p className="-mt-2 text-sm text-muted-foreground">
          {t('admin.skills.subtitle')} <a href="https://agentskills.io" target="_blank" rel="noopener noreferrer" className="text-primary hover:underline">Agent Skills</a>
        </p>
      </PageHeader>

      {/* 上传提示 */}
      <Card className="p-4 bg-muted/50">
        <div className="flex items-start gap-3">
          <Package className="h-5 w-5 text-muted-foreground mt-0.5" />
          <div className="text-sm">
            <p className="font-medium">{t('admin.skills.uploadTip')}</p>
            <p className="text-muted-foreground mt-1">
              {t('admin.skills.uploadDesc')}
            </p>
          </div>
        </div>
      </Card>

      {/* Skills 列表 */}
      {loading ? (
        <div className="flex h-64 items-center justify-center">
          <Loader2 className="h-8 w-8 animate-spin text-muted-foreground" />
        </div>
      ) : configs.length === 0 ? (
        <Card className="flex h-64 items-center justify-center">
          <div className="text-center">
            <Sparkles className="mx-auto h-12 w-12 text-muted-foreground" />
            <p className="mt-4 text-muted-foreground">{t('admin.skills.noSkills')}</p>
            <Button className="mt-4" onClick={handleUploadClick}>
              <Upload className="mr-2 h-4 w-4" />
              {t('admin.skills.uploadFirst')}
            </Button>
          </div>
        </Card>
      ) : (
        <div className="grid gap-4 md:grid-cols-2 lg:grid-cols-3">
          {configs.map((config) => (
            <Card key={config.id} className="p-6">
              <div className="flex items-start justify-between">
                <div className="flex items-center gap-3">
                  <div className={`rounded-full p-2 ${config.isActive ? "bg-purple-100 dark:bg-purple-900" : "bg-gray-100 dark:bg-gray-800"}`}>
                    <Sparkles className={`h-5 w-5 ${config.isActive ? "text-purple-600 dark:text-purple-400" : "text-gray-500"}`} />
                  </div>
                  <div>
                    <h3 className="font-semibold font-mono">{config.name}</h3>
                    <div className="flex items-center gap-2 text-xs text-muted-foreground">
                      <span>v{config.version}</span>
                      {config.author && <span>by {config.author}</span>}
                    </div>
                  </div>
                </div>
                <div className="flex gap-1">
                  <Button variant="ghost" size="icon" onClick={() => handleViewDetail(config)}>
                    <Eye className="h-4 w-4" />
                  </Button>
                  <Button variant="ghost" size="icon" onClick={() => setDeleteId(config.id)}>
                    <Trash2 className="h-4 w-4 text-red-500" />
                  </Button>
                </div>
              </div>

              <p className="mt-3 text-sm text-muted-foreground line-clamp-2">
                {config.description}
              </p>

              {/* 目录标签 */}
              <div className="mt-3 flex flex-wrap gap-1">
                {config.hasScripts && (
                  <span className="inline-flex items-center gap-1 text-xs bg-blue-100 dark:bg-blue-900 text-blue-700 dark:text-blue-300 px-2 py-0.5 rounded">
                    <FileCode className="h-3 w-3" /> scripts
                  </span>
                )}
                {config.hasReferences && (
                  <span className="inline-flex items-center gap-1 text-xs bg-green-100 dark:bg-green-900 text-green-700 dark:text-green-300 px-2 py-0.5 rounded">
                    <FileText className="h-3 w-3" /> references
                  </span>
                )}
                {config.hasAssets && (
                  <span className="inline-flex items-center gap-1 text-xs bg-orange-100 dark:bg-orange-900 text-orange-700 dark:text-orange-300 px-2 py-0.5 rounded">
                    <ImageIcon aria-hidden className="h-3 w-3" /> assets
                  </span>
                )}
              </div>

              <div className="mt-4 flex items-center justify-between text-xs text-muted-foreground">
                <div className="flex items-center gap-2">
                  <FolderOpen className="h-3 w-3" />
                  <span>{formatBytes(config.totalSize)}</span>
                </div>
                <div className="flex items-center gap-2">
                  {config.isActive ? (
                    <CheckCircle className="h-3 w-3 text-green-500" />
                  ) : (
                    <XCircle className="h-3 w-3 text-gray-400" />
                  )}
                  <Switch
                    checked={config.isActive}
                    onCheckedChange={() => handleToggleActive(config)}
                  />
                </div>
              </div>
            </Card>
          ))}
        </div>
      )}

      {/* 详情对话框 */}
      <Dialog open={detailDialog} onOpenChange={setDetailDialog}>
        <DialogContent className="max-w-3xl max-h-[80vh] overflow-y-auto">
          <DialogHeader>
            <DialogTitle className="flex items-center gap-2">
              <Sparkles className="h-5 w-5" />
              {selectedDetail?.name}
            </DialogTitle>
          </DialogHeader>
          {selectedDetail && (
            <div className="space-y-4">
              {/* 基本信息 */}
              <div className="grid grid-cols-2 gap-4 text-sm">
                <div>
                  <span className="text-muted-foreground">{t('admin.skills.version')}:</span>
                  <span className="ml-2">{selectedDetail.version}</span>
                </div>
                {selectedDetail.author && (
                  <div>
                    <span className="text-muted-foreground">{t('admin.skills.author')}:</span>
                    <span className="ml-2">{selectedDetail.author}</span>
                  </div>
                )}
                {selectedDetail.license && (
                  <div>
                    <span className="text-muted-foreground">{t('admin.skills.license')}:</span>
                    <span className="ml-2">{selectedDetail.license}</span>
                  </div>
                )}
                {selectedDetail.compatibility && (
                  <div className="col-span-2">
                    <span className="text-muted-foreground">{t('admin.skills.compatibility')}:</span>
                    <span className="ml-2">{selectedDetail.compatibility}</span>
                  </div>
                )}
                {selectedDetail.allowedTools && (
                  <div className="col-span-2">
                    <span className="text-muted-foreground">{t('admin.skills.allowedTools')}:</span>
                    <span className="ml-2 font-mono text-xs">{selectedDetail.allowedTools}</span>
                  </div>
                )}
              </div>

              {/* 描述 */}
              <div>
                <h4 className="font-medium mb-2">{t('admin.skills.description')}</h4>
                <p className="text-sm text-muted-foreground">{selectedDetail.description}</p>
              </div>

              {/* SKILL.md 内容 */}
              <div>
                <h4 className="font-medium mb-2">SKILL.md</h4>
                <pre className="bg-muted p-4 rounded-lg text-xs overflow-x-auto max-h-64">
                  {selectedDetail.skillMdContent}
                </pre>
              </div>

              {/* 文件列表 */}
              {selectedDetail.scripts.length > 0 && (
                <div>
                  <h4 className="font-medium mb-2 flex items-center gap-2">
                    <FileCode className="h-4 w-4" /> Scripts ({selectedDetail.scripts.length})
                  </h4>
                  <div className="space-y-1">
                    {selectedDetail.scripts.map((file) => (
                      <div key={file.relativePath} className="flex items-center justify-between text-sm bg-muted/50 px-3 py-1.5 rounded">
                        <span className="font-mono text-xs">{file.relativePath}</span>
                        <span className="text-muted-foreground text-xs">{formatBytes(file.size)}</span>
                      </div>
                    ))}
                  </div>
                </div>
              )}

              {selectedDetail.references.length > 0 && (
                <div>
                  <h4 className="font-medium mb-2 flex items-center gap-2">
                    <FileText className="h-4 w-4" /> References ({selectedDetail.references.length})
                  </h4>
                  <div className="space-y-1">
                    {selectedDetail.references.map((file) => (
                      <div key={file.relativePath} className="flex items-center justify-between text-sm bg-muted/50 px-3 py-1.5 rounded">
                        <span className="font-mono text-xs">{file.relativePath}</span>
                        <span className="text-muted-foreground text-xs">{formatBytes(file.size)}</span>
                      </div>
                    ))}
                  </div>
                </div>
              )}

              {selectedDetail.assets.length > 0 && (
                <div>
                  <h4 className="font-medium mb-2 flex items-center gap-2">
                    <ImageIcon aria-hidden className="h-4 w-4" /> Assets ({selectedDetail.assets.length})
                  </h4>
                  <div className="space-y-1">
                    {selectedDetail.assets.map((file) => (
                      <div key={file.relativePath} className="flex items-center justify-between text-sm bg-muted/50 px-3 py-1.5 rounded">
                        <span className="font-mono text-xs">{file.relativePath}</span>
                        <span className="text-muted-foreground text-xs">{formatBytes(file.size)}</span>
                      </div>
                    ))}
                  </div>
                </div>
              )}

              {/* 来源信息 */}
              {selectedDetail.sourceUrl && (
                <div className="flex items-center gap-2 text-sm">
                  <ExternalLink className="h-4 w-4" />
                  <a href={selectedDetail.sourceUrl} target="_blank" rel="noopener noreferrer" className="text-primary hover:underline">
                    {selectedDetail.sourceUrl}
                  </a>
                </div>
              )}
            </div>
          )}
        </DialogContent>
      </Dialog>

      {/* 删除确认对话框 */}
      <AlertDialog open={!!deleteId} onOpenChange={() => setDeleteId(null)}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>{t('admin.common.confirmDelete')}</AlertDialogTitle>
            <AlertDialogDescription>
              {t('admin.skills.deleteWarning')}
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
