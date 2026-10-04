"use client";

import React, { useCallback, useEffect, useRef, useState } from "react";
import {
  Check,
  ChevronDown,
  Key,
  Loader2,
  Plus,
  RefreshCw,
  Search,
  Shield,
  SlidersHorizontal,
  Trash2,
} from "lucide-react";
import { toast } from "sonner";
import { useLocale } from "next-intl";

import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Checkbox } from "@/components/ui/checkbox";
import { Badge } from "@/components/ui/badge";
import { Avatar, AvatarFallback, AvatarImage } from "@/components/ui/avatar";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
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
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import { PageHeader } from "@/components/admin/page-header";
import { DataTableShell } from "@/components/admin/data-table";
import { TablePagination } from "@/components/admin/table-pagination";
import { StatusBadge } from "@/components/admin/status-badge";
import {
  getUsers,
  getRoles,
  createUser,
  deleteUser,
  updateUserStatus,
  updateUserRoles,
  resetUserPassword,
  AdminUser,
  AdminRole,
  UserListResponse,
} from "@/lib/admin-api";
import { useTranslations } from "@/hooks/use-translations";

const SEARCH_DEBOUNCE_MS = 400;

export default function AdminUsersPage() {
  const [data, setData] = useState<UserListResponse | null>(null);
  const t = useTranslations();
  const locale = useLocale();
  const dateLocale = locale === "zh" ? "zh-CN" : locale;
  const [roles, setRoles] = useState<AdminRole[]>([]);
  const [loading, setLoading] = useState(true);
  const [statusUpdatingId, setStatusUpdatingId] = useState<string | null>(null);
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(20);
  const [searchInput, setSearchInput] = useState("");
  const [search, setSearch] = useState("");
  const [roleFilter, setRoleFilter] = useState("all");
  const searchTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const queryKey = JSON.stringify([page, pageSize, search, roleFilter]);
  const [loadingQuery, setLoadingQuery] = useState(queryKey);
  if (loadingQuery !== queryKey) {
    setLoadingQuery(queryKey);
    setLoading(true);
  }

  const [deleteId, setDeleteId] = useState<string | null>(null);
  const [showCreateDialog, setShowCreateDialog] = useState(false);
  const [showRolesDialog, setShowRolesDialog] = useState<AdminUser | null>(null);
  const [showPasswordDialog, setShowPasswordDialog] = useState<AdminUser | null>(null);

  const [newUser, setNewUser] = useState({ name: "", email: "", password: "" });
  const [selectedRoles, setSelectedRoles] = useState<string[]>([]);
  const [newPassword, setNewPassword] = useState("");

  const loadData = useCallback(() => {
    return Promise.all([
        getUsers(
          page,
          pageSize,
          search || undefined,
          roleFilter === "all" ? undefined : roleFilter
        ),
        getRoles(),
      ]).then(([usersResult, rolesResult]) => {
      setData(usersResult);
      setRoles(rolesResult);
    }).catch(error => {
      console.error("Failed to fetch users:", error);
      toast.error(t("admin.toast.fetchUserFailed"));
    }).finally(() => {
      setLoading(false);
    });
  }, [page, pageSize, search, roleFilter, t]);

  const fetchData = useCallback(() => {
    setLoading(true);
    return loadData();
  }, [loadData]);

  useEffect(() => {
    void loadData();
  }, [loadData]);

  const handleSearchInput = (value: string) => {
    setSearchInput(value);
    if (searchTimer.current) clearTimeout(searchTimer.current);
    searchTimer.current = setTimeout(() => {
      setPage(1);
      setSearch(value.trim());
    }, SEARCH_DEBOUNCE_MS);
  };

  const handleCreate = async () => {
    if (!newUser.name || !newUser.email || !newUser.password) {
      toast.error(t("admin.users.fillComplete"));
      return;
    }
    try {
      await createUser(newUser);
      toast.success(t("admin.toast.createSuccess"));
      setShowCreateDialog(false);
      setNewUser({ name: "", email: "", password: "" });
      fetchData();
    } catch {
      toast.error(t("admin.toast.createFailed"));
    }
  };

  const handleDelete = async () => {
    if (!deleteId) return;
    try {
      await deleteUser(deleteId);
      toast.success(t("admin.toast.deleteSuccess"));
      setDeleteId(null);
      fetchData();
    } catch {
      toast.error(t("admin.toast.deleteFailed"));
    }
  };

  const handleStatusChange = async (
    id: string,
    newStatus: number,
    currentStatus?: number
  ) => {
    if (currentStatus === newStatus) return;
    setStatusUpdatingId(id);
    try {
      await updateUserStatus(id, newStatus);
      toast.success(t("admin.toast.statusUpdateSuccess"));
      fetchData();
    } catch {
      toast.error(t("admin.toast.statusUpdateFailed"));
    } finally {
      setStatusUpdatingId((prev) => (prev === id ? null : prev));
    }
  };

  const handleRolesUpdate = async () => {
    if (!showRolesDialog) return;
    try {
      await updateUserRoles(showRolesDialog.id, selectedRoles);
      toast.success(t("admin.toast.roleUpdateSuccess"));
      setShowRolesDialog(null);
      fetchData();
    } catch {
      toast.error(t("admin.toast.roleUpdateFailed"));
    }
  };

  const handlePasswordReset = async () => {
    if (!showPasswordDialog || !newPassword) return;
    try {
      await resetUserPassword(showPasswordDialog.id, newPassword);
      toast.success(t("admin.toast.passwordResetSuccess"));
      setShowPasswordDialog(null);
      setNewPassword("");
    } catch {
      toast.error(t("admin.toast.passwordResetFailed"));
    }
  };

  const openRolesDialog = (user: AdminUser) => {
    setSelectedRoles(user.roles || []);
    setShowRolesDialog(user);
  };

  const userStatusLabels: Record<number, string> = {
    1: t("admin.users.normal"),
    0: t("admin.users.disabled"),
  };

  const isEmpty = !loading && (data?.items.length ?? 0) === 0;

  const toolbar = (
    <>
      <div className="relative min-w-[220px] flex-1 sm:max-w-xs">
        <Search className="absolute left-2.5 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" />
        <Input
          placeholder={t("admin.users.searchPlaceholder")}
          value={searchInput}
          onChange={(e) => handleSearchInput(e.target.value)}
          className="h-9 pl-8"
        />
      </div>
      <Select
        value={roleFilter}
        onValueChange={(v) => {
          setRoleFilter(v);
          setPage(1);
        }}
      >
        <SelectTrigger size="sm" className="h-9 w-[160px]">
          <SlidersHorizontal className="mr-1 h-3.5 w-3.5 text-muted-foreground" />
          <SelectValue placeholder={t("admin.users.filterRole")} />
        </SelectTrigger>
        <SelectContent>
          <SelectItem value="all">{t("admin.users.allRoles")}</SelectItem>
          {roles.map((role) => (
            <SelectItem key={role.id} value={role.id}>
              {role.name}
            </SelectItem>
          ))}
        </SelectContent>
      </Select>
      <div className="ml-auto">
        <Button
          variant="ghost"
          size="icon"
          className="h-9 w-9"
          onClick={fetchData}
          disabled={loading}
          title={t("admin.common.refresh")}
        >
          <RefreshCw className={`h-4 w-4 ${loading ? "animate-spin" : ""}`} />
        </Button>
      </div>
    </>
  );

  return (
    <div className="space-y-5">
      <PageHeader
        title={t("admin.users.title")}
        actions={
          <Button onClick={() => setShowCreateDialog(true)}>
            <Plus className="mr-1.5 h-4 w-4" />
            {t("admin.users.createUser")}
          </Button>
        }
      />

      <DataTableShell
        toolbar={toolbar}
        loading={loading}
        empty={isEmpty}
        emptyTitle={t("admin.shared.noData")}
        footer={
          <TablePagination
            page={page}
            pageSize={pageSize}
            total={data?.total ?? 0}
            onPageChange={setPage}
            onPageSizeChange={(size) => {
              setPageSize(size);
              setPage(1);
            }}
          />
        }
      >
        <Table>
          <TableHeader>
            <TableRow className="bg-muted/40 hover:bg-muted/40">
              <TableHead className="pl-4">{t("admin.users.user")}</TableHead>
              <TableHead>{t("admin.users.email")}</TableHead>
              <TableHead>{t("admin.users.role")}</TableHead>
              <TableHead>{t("admin.users.status")}</TableHead>
              <TableHead>{t("admin.users.createdAt")}</TableHead>
              <TableHead className="pr-4 text-right">
                {t("admin.users.operations")}
              </TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {data?.items.map((user) => (
              <TableRow key={user.id}>
                <TableCell className="pl-4">
                  <div className="flex items-center gap-3">
                    <Avatar className="h-8 w-8">
                      <AvatarImage src={user.avatar} />
                      <AvatarFallback className="text-xs">
                        {user.name.charAt(0).toUpperCase()}
                      </AvatarFallback>
                    </Avatar>
                    <span className="font-medium">{user.name}</span>
                  </div>
                </TableCell>
                <TableCell className="text-sm text-muted-foreground">
                  {user.email || "-"}
                </TableCell>
                <TableCell>
                  <div className="flex flex-wrap gap-1">
                    {user.roles?.map((role) => (
                      <Badge
                        key={role}
                        variant="secondary"
                        className="text-xs font-normal"
                      >
                        {role}
                      </Badge>
                    ))}
                  </div>
                </TableCell>
                <TableCell>
                  <DropdownMenu>
                    <DropdownMenuTrigger asChild>
                      <button
                        type="button"
                        disabled={statusUpdatingId === user.id}
                        className="group inline-flex items-center gap-1 rounded-md outline-none disabled:opacity-60"
                      >
                        <StatusBadge
                          tone={user.status === 1 ? "success" : "danger"}
                          label={userStatusLabels[user.status]}
                        />
                        {statusUpdatingId === user.id ? (
                          <Loader2 className="h-3 w-3 animate-spin text-muted-foreground" />
                        ) : (
                          <ChevronDown className="h-3 w-3 text-muted-foreground opacity-0 transition-opacity group-hover:opacity-100" />
                        )}
                      </button>
                    </DropdownMenuTrigger>
                    <DropdownMenuContent align="start" className="w-[150px]">
                      {[1, 0].map((statusValue) => (
                        <DropdownMenuItem
                          key={statusValue}
                          disabled={user.status === statusValue}
                          onClick={() =>
                            handleStatusChange(user.id, statusValue, user.status)
                          }
                          className="justify-between"
                        >
                          <StatusBadge
                            tone={statusValue === 1 ? "success" : "danger"}
                            label={userStatusLabels[statusValue]}
                            className="border-transparent bg-transparent px-0"
                          />
                          {user.status === statusValue ? (
                            <Check className="h-3.5 w-3.5 text-primary" />
                          ) : null}
                        </DropdownMenuItem>
                      ))}
                    </DropdownMenuContent>
                  </DropdownMenu>
                </TableCell>
                <TableCell className="text-sm text-muted-foreground whitespace-nowrap">
                  {new Date(user.createdAt).toLocaleDateString(dateLocale)}
                </TableCell>
                <TableCell className="pr-4 text-right">
                  <div className="flex justify-end gap-1">
                    <Button
                      variant="ghost"
                      size="icon"
                      className="h-8 w-8"
                      title={t("admin.users.assignRoles")}
                      onClick={() => openRolesDialog(user)}
                    >
                      <Shield className="h-4 w-4" />
                    </Button>
                    <Button
                      variant="ghost"
                      size="icon"
                      className="h-8 w-8"
                      title={t("admin.users.resetPassword")}
                      onClick={() => setShowPasswordDialog(user)}
                    >
                      <Key className="h-4 w-4" />
                    </Button>
                    <Button
                      variant="ghost"
                      size="icon"
                      className="h-8 w-8"
                      title={t("admin.common.delete")}
                      onClick={() => setDeleteId(user.id)}
                    >
                      <Trash2 className="h-4 w-4 text-red-500" />
                    </Button>
                  </div>
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </DataTableShell>

      {/* 新增用户对话框 */}
      <Dialog open={showCreateDialog} onOpenChange={setShowCreateDialog}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>{t("admin.users.createUser")}</DialogTitle>
          </DialogHeader>
          <div className="space-y-4">
            <div className="space-y-1.5">
              <label className="text-sm font-medium">
                {t("admin.users.username")} *
              </label>
              <Input
                value={newUser.name}
                onChange={(e) => setNewUser({ ...newUser, name: e.target.value })}
                placeholder={t("admin.users.enterUsername")}
              />
            </div>
            <div className="space-y-1.5">
              <label className="text-sm font-medium">
                {t("admin.users.email")} *
              </label>
              <Input
                type="email"
                value={newUser.email}
                onChange={(e) =>
                  setNewUser({ ...newUser, email: e.target.value })
                }
                placeholder={t("admin.users.enterEmail")}
              />
            </div>
            <div className="space-y-1.5">
              <label className="text-sm font-medium">
                {t("admin.users.password")} *
              </label>
              <Input
                type="password"
                value={newUser.password}
                onChange={(e) =>
                  setNewUser({ ...newUser, password: e.target.value })
                }
                placeholder={t("admin.users.enterPassword")}
              />
            </div>
          </div>
          <DialogFooter>
            <Button variant="outline" onClick={() => setShowCreateDialog(false)}>
              {t("admin.common.cancel")}
            </Button>
            <Button onClick={handleCreate}>{t("admin.common.create")}</Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      {/* 角色分配对话框 */}
      <Dialog
        open={!!showRolesDialog}
        onOpenChange={() => setShowRolesDialog(null)}
      >
        <DialogContent>
          <DialogHeader>
            <DialogTitle>
              {t("admin.users.assignRoles")} - {showRolesDialog?.name}
            </DialogTitle>
          </DialogHeader>
          <div className="space-y-3">
            {roles.map((role) => (
              <div key={role.id} className="flex items-center gap-2">
                <Checkbox
                  id={role.id}
                  checked={selectedRoles.includes(role.name)}
                  onCheckedChange={(checked) => {
                    if (checked) {
                      setSelectedRoles([...selectedRoles, role.name]);
                    } else {
                      setSelectedRoles(
                        selectedRoles.filter((r) => r !== role.name)
                      );
                    }
                  }}
                />
                <label htmlFor={role.id} className="text-sm">
                  {role.name}
                  {role.description && (
                    <span className="ml-2 text-muted-foreground">
                      ({role.description})
                    </span>
                  )}
                </label>
              </div>
            ))}
          </div>
          <DialogFooter>
            <Button variant="outline" onClick={() => setShowRolesDialog(null)}>
              {t("admin.common.cancel")}
            </Button>
            <Button onClick={handleRolesUpdate}>{t("admin.common.save")}</Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      {/* 重置密码对话框 */}
      <Dialog
        open={!!showPasswordDialog}
        onOpenChange={() => setShowPasswordDialog(null)}
      >
        <DialogContent>
          <DialogHeader>
            <DialogTitle>
              {t("admin.users.resetPassword")} - {showPasswordDialog?.name}
            </DialogTitle>
          </DialogHeader>
          <div className="space-y-1.5">
            <label className="text-sm font-medium">
              {t("admin.users.newPassword")}
            </label>
            <Input
              type="password"
              value={newPassword}
              onChange={(e) => setNewPassword(e.target.value)}
              placeholder={t("admin.users.enterNewPassword")}
            />
          </div>
          <DialogFooter>
            <Button
              variant="outline"
              onClick={() => setShowPasswordDialog(null)}
            >
              {t("admin.common.cancel")}
            </Button>
            <Button onClick={handlePasswordReset}>
              {t("admin.users.confirmReset")}
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
              {t("admin.users.deleteWarning")}
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
