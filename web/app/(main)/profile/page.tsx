"use client";

import { useState, useEffect } from "react";
import { useRouter } from "next/navigation";
import { AppLayout } from "@/components/app-layout";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Avatar, AvatarFallback, AvatarImage } from "@/components/ui/avatar";
import { Separator } from "@/components/ui/separator";
import { useTranslations } from "@/hooks/use-translations";
import { useAuth } from "@/contexts/auth-context";
import { updateProfile, changePassword, UpdateProfileRequest, ChangePasswordRequest } from "@/lib/profile-api";
import { Loader2, User, Lock, ArrowLeft } from "lucide-react";
import { toast } from "sonner";
import Link from "next/link";

export default function ProfilePage() {
  const t = useTranslations();
  const router = useRouter();
  const { user, isLoading: authLoading, isAuthenticated, refreshUser } = useAuth();
  const [activeItem, setActiveItem] = useState(t("common.profile"));

  // Profile form state
  const [name, setName] = useState(user?.name || "");
  const [email, setEmail] = useState(user?.email || "");
  const [phone, setPhone] = useState("");
  const [avatar, setAvatar] = useState(user?.avatar || "");
  const [isUpdating, setIsUpdating] = useState(false);

  // Password form state
  const [currentPassword, setCurrentPassword] = useState("");
  const [newPassword, setNewPassword] = useState("");
  const [confirmPassword, setConfirmPassword] = useState("");
  const [isChangingPassword, setIsChangingPassword] = useState(false);
  const [formUser, setFormUser] = useState(user);

  if (formUser !== user) {
    setFormUser(user);
    setName(user?.name || "");
    setEmail(user?.email || "");
    setAvatar(user?.avatar || "");
  }

  useEffect(() => {
    if (!authLoading && !isAuthenticated) {
      router.push("/auth?returnUrl=/profile");
    }
  }, [authLoading, isAuthenticated, router]);

  const handleUpdateProfile = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!name.trim()) {
      toast.error(t("profile.nameRequired") || "用户名不能为空");
      return;
    }

    setIsUpdating(true);
    try {
      const request: UpdateProfileRequest = { name, email, phone, avatar };
      await updateProfile(request);
      await refreshUser();
      toast.success(t("profile.updateSuccess") || "资料更新成功");
    } catch (err) {
      toast.error(err instanceof Error ? err.message : t("profile.updateFailed") || "更新失败");
    } finally {
      setIsUpdating(false);
    }
  };

  const handleChangePassword = async (e: React.FormEvent) => {
    e.preventDefault();
    if (newPassword !== confirmPassword) {
      toast.error(t("profile.passwordMismatch") || "两次密码输入不一致");
      return;
    }
    if (newPassword.length < 6) {
      toast.error(t("profile.passwordTooShort") || "密码长度至少6位");
      return;
    }

    setIsChangingPassword(true);
    try {
      const request: ChangePasswordRequest = { currentPassword, newPassword, confirmPassword };
      await changePassword(request);
      toast.success(t("profile.passwordChanged") || "密码修改成功");
      setCurrentPassword("");
      setNewPassword("");
      setConfirmPassword("");
    } catch (err) {
      toast.error(err instanceof Error ? err.message : t("profile.passwordChangeFailed") || "密码修改失败");
    } finally {
      setIsChangingPassword(false);
    }
  };

  if (authLoading) {
    return (
      <div className="flex h-screen items-center justify-center">
        <Loader2 className="h-8 w-8 animate-spin text-muted-foreground" />
      </div>
    );
  }

  if (!isAuthenticated || !user) {
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
            {t("common.backToHome") || "返回首页"}
          </Link>
        </div>

        <div className="space-y-6">
          {/* Profile Card */}
          <Card>
            <CardHeader>
              <div className="flex items-center gap-2">
                <User className="h-5 w-5" />
                <CardTitle>{t("profile.title") || "个人资料"}</CardTitle>
              </div>
              <CardDescription>
                {t("profile.description") || "管理你的个人信息"}
              </CardDescription>
            </CardHeader>
            <CardContent>
              <form onSubmit={handleUpdateProfile} className="space-y-6">
                <div className="flex items-center gap-6">
                  <Avatar className="h-20 w-20">
                    <AvatarImage src={avatar} alt={name} />
                    <AvatarFallback className="text-2xl">
                      {name.charAt(0).toUpperCase()}
                    </AvatarFallback>
                  </Avatar>
                  <div className="flex-1 space-y-2">
                    <Label htmlFor="avatar">{t("profile.avatarUrl") || "头像链接"}</Label>
                    <Input
                      id="avatar"
                      type="url"
                      placeholder="https://example.com/avatar.png"
                      value={avatar}
                      onChange={(e) => setAvatar(e.target.value)}
                      disabled={isUpdating}
                    />
                  </div>
                </div>

                <Separator />

                <div className="grid gap-4 md:grid-cols-2">
                  <div className="space-y-2">
                    <Label htmlFor="name">{t("profile.name") || "用户名"}</Label>
                    <Input
                      id="name"
                      type="text"
                      value={name}
                      onChange={(e) => setName(e.target.value)}
                      disabled={isUpdating}
                      required
                      minLength={2}
                      maxLength={50}
                    />
                  </div>
                  <div className="space-y-2">
                    <Label htmlFor="email">{t("profile.email") || "邮箱"}</Label>
                    <Input
                      id="email"
                      type="email"
                      value={email}
                      onChange={(e) => setEmail(e.target.value)}
                      disabled={isUpdating}
                      required
                    />
                  </div>
                  <div className="space-y-2">
                    <Label htmlFor="phone">{t("profile.phone") || "手机号"}</Label>
                    <Input
                      id="phone"
                      type="tel"
                      placeholder={t("profile.phonePlaceholder") || "可选"}
                      value={phone}
                      onChange={(e) => setPhone(e.target.value)}
                      disabled={isUpdating}
                    />
                  </div>
                </div>

                <div className="flex justify-end">
                  <Button type="submit" disabled={isUpdating}>
                    {isUpdating ? (
                      <>
                        <Loader2 className="mr-2 h-4 w-4 animate-spin" />
                        {t("common.loading") || "保存中..."}
                      </>
                    ) : (
                      t("common.save") || "保存"
                    )}
                  </Button>
                </div>
              </form>
            </CardContent>
          </Card>

          {/* Password Card */}
          <Card>
            <CardHeader>
              <div className="flex items-center gap-2">
                <Lock className="h-5 w-5" />
                <CardTitle>{t("profile.changePassword") || "修改密码"}</CardTitle>
              </div>
              <CardDescription>
                {t("profile.passwordDescription") || "定期更换密码以保护账户安全"}
              </CardDescription>
            </CardHeader>
            <CardContent>
              <form onSubmit={handleChangePassword} className="space-y-4">
                <div className="space-y-2">
                  <Label htmlFor="currentPassword">
                    {t("profile.currentPassword") || "当前密码"}
                  </Label>
                  <Input
                    id="currentPassword"
                    type="password"
                    value={currentPassword}
                    onChange={(e) => setCurrentPassword(e.target.value)}
                    disabled={isChangingPassword}
                    required
                  />
                </div>
                <div className="grid gap-4 md:grid-cols-2">
                  <div className="space-y-2">
                    <Label htmlFor="newPassword">
                      {t("profile.newPassword") || "新密码"}
                    </Label>
                    <Input
                      id="newPassword"
                      type="password"
                      placeholder={t("profile.passwordPlaceholder") || "至少6位字符"}
                      value={newPassword}
                      onChange={(e) => setNewPassword(e.target.value)}
                      disabled={isChangingPassword}
                      required
                      minLength={6}
                    />
                  </div>
                  <div className="space-y-2">
                    <Label htmlFor="confirmPassword">
                      {t("profile.confirmPassword") || "确认新密码"}
                    </Label>
                    <Input
                      id="confirmPassword"
                      type="password"
                      value={confirmPassword}
                      onChange={(e) => setConfirmPassword(e.target.value)}
                      disabled={isChangingPassword}
                      required
                    />
                  </div>
                </div>
                <div className="flex justify-end">
                  <Button type="submit" variant="secondary" disabled={isChangingPassword}>
                    {isChangingPassword ? (
                      <>
                        <Loader2 className="mr-2 h-4 w-4 animate-spin" />
                        {t("common.loading") || "修改中..."}
                      </>
                    ) : (
                      t("profile.changePassword") || "修改密码"
                    )}
                  </Button>
                </div>
              </form>
            </CardContent>
          </Card>
        </div>
      </div>
    </AppLayout>
  );
}
