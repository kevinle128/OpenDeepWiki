"use client";

import React, { useCallback, useEffect, useMemo, useState } from "react";
import Link from "next/link";
import { Card } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { useAuth } from "@/contexts/auth-context";
import { AppLayout } from "@/components/app-layout";
import {
  getMyDepartments,
  getMyDepartmentRepositories,
  UserDepartment,
  DepartmentRepository,
} from "@/lib/organization-api";
import {
  Loader2,
  Building2,
  GitBranch,
  RefreshCw,
  ExternalLink,
  Clock,
  CheckCircle,
  XCircle,
  AlertCircle,
} from "lucide-react";
import { toast } from "sonner";
import { buildRepoBasePath } from "@/lib/repo-route";
import { useTranslations } from "@/hooks/use-translations";

export default function OrganizationsPage() {
  const { user, isLoading: authLoading } = useAuth();
  const t = useTranslations();
  const [departments, setDepartments] = useState<UserDepartment[]>([]);
  const [repositories, setRepositories] = useState<DepartmentRepository[]>([]);
  const [loading, setLoading] = useState(true);

  const statusConfig = useMemo(
    () => ({
      Pending: { icon: Clock, color: "text-yellow-500", label: t("admin.pending") },
      Processing: { icon: Loader2, color: "text-blue-500", label: t("admin.processing") },
      Completed: { icon: CheckCircle, color: "text-green-500", label: t("admin.completed") },
      Failed: { icon: XCircle, color: "text-red-500", label: t("admin.failed") },
      Unknown: { icon: AlertCircle, color: "text-gray-500", label: t("common.organization.unknown") },
    }),
    [t]
  );

  const fetchData = useCallback(() => {
    if (!user) return;

    return Promise.all([
        getMyDepartments(),
        getMyDepartmentRepositories(),
      ]).then(([depts, repos]) => {
      setDepartments(depts);
      setRepositories(repos);
    }).catch(error => {
      console.error("Failed to fetch organization data:", error);
      toast.error(t("common.organization.fetchFailed"));
    }).finally(() => {
      setLoading(false);
    });
  }, [t, user]);

  useEffect(() => {
    if (user) {
      fetchData();
    }
  }, [user, authLoading, fetchData]);

  const content = () => {
    if (authLoading || (user && loading)) {
      return (
        <div className="flex h-[50vh] items-center justify-center">
          <Loader2 className="h-8 w-8 animate-spin text-muted-foreground" />
        </div>
      );
    }

    if (!user) {
      return (
        <Card className="flex h-64 flex-col items-center justify-center">
          <Building2 className="h-12 w-12 text-muted-foreground/50" />
          <p className="mt-4 text-muted-foreground">{t("common.organization.loginRequired")}</p>
        </Card>
      );
    }

    if (departments.length === 0) {
      return (
        <Card className="flex h-64 flex-col items-center justify-center">
          <Building2 className="h-12 w-12 text-muted-foreground/50" />
          <p className="mt-4 text-muted-foreground">{t("common.organization.noDepartments")}</p>
          <p className="mt-2 text-sm text-muted-foreground">{t("common.organization.contactAdmin")}</p>
        </Card>
      );
    }

    return (
      <div className="space-y-6">
        <div>
          <h2 className="mb-4 text-lg font-semibold">{t("common.organization.myDepartments")}</h2>
          <div className="grid gap-4 md:grid-cols-2 lg:grid-cols-3">
            {departments.map((dept) => (
              <Card key={dept.id} className="p-4">
                <div className="flex items-center gap-3">
                  <div className="rounded-full bg-primary/10 p-2">
                    <Building2 className="h-5 w-5 text-primary" />
                  </div>
                  <div>
                    <h3 className="font-medium">{dept.name}</h3>
                    {dept.isManager && (
                      <span className="text-xs text-primary">{t("common.organization.departmentManager")}</span>
                    )}
                  </div>
                </div>
                {dept.description && (
                  <p className="mt-2 line-clamp-2 text-sm text-muted-foreground">
                    {dept.description}
                  </p>
                )}
              </Card>
            ))}
          </div>
        </div>

        <div>
          <h2 className="mb-4 text-lg font-semibold">{t("common.organization.departmentRepositories")}</h2>
          {repositories.length === 0 ? (
            <Card className="flex h-32 flex-col items-center justify-center">
              <GitBranch className="h-8 w-8 text-muted-foreground/50" />
              <p className="mt-2 text-sm text-muted-foreground">{t("common.organization.noRepositories")}</p>
            </Card>
          ) : (
            <div className="grid gap-4 md:grid-cols-2 lg:grid-cols-3">
              {repositories.map((repo) => {
                const status = statusConfig[repo.statusName as keyof typeof statusConfig] || statusConfig.Unknown;
                const StatusIcon = status.icon;

                return (
                  <Card key={repo.repositoryId} className="p-4">
                    <div className="flex items-start justify-between">
                      <div className="flex items-center gap-3">
                        <div className="rounded-full bg-primary/10 p-2">
                          <GitBranch className="h-5 w-5 text-primary" />
                        </div>
                        <div>
                          <h3 className="font-medium">
                            {repo.orgName}/{repo.repoName}
                          </h3>
                          <span className="text-xs text-muted-foreground">{repo.departmentName}</span>
                        </div>
                      </div>
                      <div className={`flex items-center gap-1 ${status.color}`}>
                        <StatusIcon className={`h-4 w-4 ${repo.statusName === "Processing" ? "animate-spin" : ""}`} />
                        <span className="text-xs">{status.label}</span>
                      </div>
                    </div>
                    <div className="mt-4 flex gap-2">
                      {repo.statusName === "Completed" && (
                        <Link href={buildRepoBasePath(repo.orgName, repo.repoName)}>
                          <Button size="sm" variant="outline">
                            <ExternalLink className="mr-1 h-3 w-3" />
                            {t("common.organization.viewDocs")}
                          </Button>
                        </Link>
                      )}
                    </div>
                  </Card>
                );
              })}
            </div>
          )}
        </div>
      </div>
    );
  };

  return (
    <AppLayout activeItem={t("sidebar.organizations")}>
      <div className="container mx-auto p-6">
        <div className="mb-6 flex items-center justify-between">
          <h1 className="text-2xl font-bold">{t("common.organization.title")}</h1>
          {user && (
            <Button variant="outline" onClick={() => { setLoading(true); void fetchData(); }}>
              <RefreshCw className="mr-2 h-4 w-4" />
              {t("common.organization.refresh")}
            </Button>
          )}
        </div>
        {content()}
      </div>
    </AppLayout>
  );
}
