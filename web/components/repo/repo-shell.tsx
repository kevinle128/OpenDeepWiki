"use client";

import React, { useEffect, useState } from "react";
import { usePathname, useSearchParams } from "next/navigation";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { ChevronRight, Download, Home, Network, Sparkles } from "lucide-react";
import type { RepoBranchesResponse, RepoTreeNode } from "@/types/repository";
import { BranchLanguageSelector } from "./branch-language-selector";
import { fetchRepoBranches, fetchRepoTree } from "@/lib/repository-api";
import { ChatAssistant, buildCatalogMenu } from "@/components/chat";
import { WithAnnouncement } from "@/components/with-announcement";
import { buildRepoBasePath, buildRepoDocPath, buildRepoGraphifyPath, buildRepoMindMapPath } from "@/lib/repo-route";
import { cn } from "@/lib/utils";

interface RepoShellProps {
  owner: string;
  repo: string;
  initialNodes: RepoTreeNode[];
  children: React.ReactNode;
  initialBranches?: RepoBranchesResponse;
  initialBranch?: string;
  initialLanguage?: string;
  initialHasGraphifyArtifact?: boolean;
}

interface SidebarTreeLabels {
  expandSection: string;
  collapseSection: string;
}

function collectExpandableSlugs(items: RepoTreeNode[]) {
  const expanded = new Set<string>();

  const walk = (tree: RepoTreeNode[]) => {
    for (const item of tree) {
      const children = item.children ?? [];
      if (children.length === 0) {
        continue;
      }

      expanded.add(item.slug);
      walk(children);
    }
  };

  walk(items);
  return expanded;
}

function collectParentSlugs(items: RepoTreeNode[], targetPath: string) {
  const parents = new Set<string>();

  const walk = (tree: RepoTreeNode[]): boolean => {
    for (const item of tree) {
      if (item.slug === targetPath) {
        return true;
      }

      const children = item.children ?? [];
      if (children.length > 0 && walk(children)) {
        parents.add(item.slug);
        return true;
      }
    }

    return false;
  };

  if (targetPath) {
    walk(items);
  }

  return parents;
}

function findFirstLeafSlug(node: RepoTreeNode): string | null {
  const children = node.children ?? [];
  if (children.length === 0) {
    return node.slug;
  }

  for (const child of children) {
    const slug = findFirstLeafSlug(child);
    if (slug) {
      return slug;
    }
  }

  return null;
}

function SidebarTree({
  nodes,
  owner,
  repo,
  queryString,
  currentPath,
  labels,
  depth = 0,
}: {
  nodes: RepoTreeNode[];
  owner: string;
  repo: string;
  queryString: string;
  currentPath: string;
  labels: SidebarTreeLabels;
  depth?: number;
}) {
  const [expandedSlugs, setExpandedSlugs] = useState<Set<string>>(() => collectExpandableSlugs(nodes));
  const [previousTree, setPreviousTree] = useState({ nodes, currentPath });
  if (previousTree.nodes !== nodes) {
    setPreviousTree({ nodes, currentPath });
    setExpandedSlugs(collectExpandableSlugs(nodes));
  } else if (previousTree.currentPath !== currentPath) {
    setPreviousTree({ nodes, currentPath });
    const parentSlugs = collectParentSlugs(nodes, currentPath);
    setExpandedSlugs((prev) => {
      let changed = false;
      const next = new Set(prev);

      parentSlugs.forEach((slug) => {
        if (!next.has(slug)) {
          next.add(slug);
          changed = true;
        }
      });

      return changed ? next : prev;
    });
  }

  const toggleExpand = (slug: string) => {
    setExpandedSlugs((prev) => {
      const next = new Set(prev);
      if (next.has(slug)) {
        next.delete(slug);
      } else {
        next.add(slug);
      }
      return next;
    });
  };

  return (
    <ul className={cn(depth === 0 ? "space-y-0.5" : "ml-2 mt-0.5 space-y-0.5 border-l border-border/50 pl-2")}>
      {nodes.map((node) => {
        const children = node.children ?? [];
        const isDirectory = children.length > 0;
        const isExpanded = expandedSlugs.has(node.slug);
        const isActive = !isDirectory && currentPath === node.slug;
        const targetSlug = isDirectory ? findFirstLeafSlug(node) : node.slug;
        const href = targetSlug
          ? queryString
            ? `${buildRepoDocPath(owner, repo, targetSlug)}?${queryString}`
            : buildRepoDocPath(owner, repo, targetSlug)
          : null;
        const nodeHref = href ?? buildRepoDocPath(owner, repo, node.slug);

        return (
          <li key={node.slug} className="min-w-0">
            <div className="flex min-w-0 items-center gap-1">
              {isDirectory ? (
                <button
                  type="button"
                  aria-expanded={isExpanded}
                  aria-label={isExpanded ? labels.collapseSection : labels.expandSection}
                  onClick={() => toggleExpand(node.slug)}
                  className="flex size-5 shrink-0 items-center justify-center rounded-md text-muted-foreground transition-colors hover:bg-muted hover:text-foreground"
                >
                  <ChevronRight
                    className={cn(
                      "size-3.5 transition-transform duration-200 ease-out motion-reduce:transition-none",
                      isExpanded && "rotate-90"
                    )}
                  />
                </button>
              ) : (
                <span className="size-5 shrink-0" aria-hidden="true" />
              )}

              {isDirectory && href ? (
                <Link
                  href={nodeHref}
                  title={node.title}
                  className="min-w-0 flex-1 truncate rounded-md px-2 py-1.5 text-left text-[13px] font-medium leading-5 text-foreground/80 transition-colors hover:bg-muted hover:text-foreground"
                >
                  {node.title}
                </Link>
              ) : isDirectory ? (
                <button
                  type="button"
                  title={node.title}
                  onClick={() => toggleExpand(node.slug)}
                  className="min-w-0 flex-1 truncate rounded-md px-2 py-1.5 text-left text-[13px] font-medium leading-5 text-foreground/80 transition-colors hover:bg-muted hover:text-foreground"
                >
                  {node.title}
                </button>
              ) : (
                <Link
                  href={nodeHref}
                  title={node.title}
                  className={cn(
                    "block min-w-0 flex-1 truncate rounded-md px-2 py-1.5 text-[13px] leading-5 transition-colors",
                    isActive
                      ? "bg-primary font-medium text-primary-foreground shadow-sm"
                      : "text-foreground/80 hover:bg-muted hover:text-foreground"
                  )}
                >
                  {node.title}
                </Link>
              )}
            </div>

            {isDirectory && (
              <div
                aria-hidden={!isExpanded}
                inert={isExpanded ? undefined : true}
                className={cn(
                  "grid transition-[grid-template-rows,opacity,transform] duration-200 ease-out motion-reduce:transition-none",
                  isExpanded ? "grid-rows-[1fr] translate-y-0 opacity-100" : "grid-rows-[0fr] -translate-y-1 opacity-0"
                )}
              >
                <div className="min-h-0 overflow-hidden">
                  <SidebarTree
                    nodes={children}
                    owner={owner}
                    repo={repo}
                    queryString={queryString}
                    currentPath={currentPath}
                    labels={labels}
                    depth={depth + 1}
                  />
                </div>
              </div>
            )}
          </li>
        );
      })}
    </ul>
  );
}

export function RepoShell({
  owner,
  repo,
  initialNodes,
  children,
  initialBranches,
  initialBranch,
  initialLanguage,
  initialHasGraphifyArtifact = false,
}: RepoShellProps) {
  const t = useTranslations("common");
  const searchParams = useSearchParams();
  const pathname = usePathname();
  const urlBranch = searchParams.get("branch");
  const urlLang = searchParams.get("lang");
  const repoBasePath = buildRepoBasePath(owner, repo);

  const [nodes, setNodes] = useState<RepoTreeNode[]>(initialNodes);
  const [branches, setBranches] = useState<RepoBranchesResponse | undefined>(initialBranches);
  const [currentBranch, setCurrentBranch] = useState(initialBranch || "");
  const [currentLanguage, setCurrentLanguage] = useState(initialLanguage || "");
  const [hasGraphifyArtifact, setHasGraphifyArtifact] = useState(initialHasGraphifyArtifact);
  const [isLoading, setIsLoading] = useState(false);
  const [isExporting, setIsExporting] = useState(false);

  const currentDocPath = React.useMemo(() => {
    const encodedPrefix = `${repoBasePath}/`;
    if (pathname.startsWith(encodedPrefix)) {
      return pathname.slice(encodedPrefix.length);
    }

    const rawPrefix = `/${owner}/${repo}/`;
    if (pathname.startsWith(rawPrefix)) {
      return pathname.slice(rawPrefix.length);
    }
    return "";
  }, [pathname, owner, repo, repoBasePath]);

  useEffect(() => {
    const branch = urlBranch || undefined;
    const lang = urlLang || undefined;

    if (!branch && !lang) {
      return;
    }

    if (branch === currentBranch && lang === currentLanguage) {
      return;
    }

    const fetchData = async () => {
      setIsLoading(true);
      try {
        const [treeData, branchesData] = await Promise.all([
          fetchRepoTree(owner, repo, branch, lang),
          fetchRepoBranches(owner, repo),
        ]);

        if (treeData.nodes.length > 0) {
          setNodes(treeData.nodes);
          setCurrentBranch(treeData.currentBranch || "");
          setCurrentLanguage(treeData.currentLanguage || "");
        }
        setHasGraphifyArtifact(treeData.hasGraphifyArtifact === true);
        if (branchesData) {
          setBranches(branchesData);
        }
      } catch (error) {
        console.error("Failed to fetch tree data:", error);
      } finally {
        setIsLoading(false);
      }
    };

    fetchData();
  }, [urlBranch, urlLang, owner, repo, currentBranch, currentLanguage]);

  const queryString = searchParams.toString();

  const mindMapUrl = queryString
    ? `${buildRepoMindMapPath(owner, repo)}?${queryString}`
    : buildRepoMindMapPath(owner, repo);

  const graphifyUrl = queryString
    ? `${buildRepoGraphifyPath(owner, repo)}?${queryString}`
    : buildRepoGraphifyPath(owner, repo);

  const handleExport = async () => {
    if (isExporting) return;

    setIsExporting(true);
    try {
      const params = new URLSearchParams();
      if (currentBranch) params.set("branch", currentBranch);
      if (currentLanguage) params.set("lang", currentLanguage);

      const exportUrl = `/api/v1/repos/${encodeURIComponent(owner)}/${encodeURIComponent(repo)}/export${
        params.toString() ? `?${params.toString()}` : ""
      }`;

      const response = await fetch(exportUrl);
      if (!response.ok) {
        throw new Error(t("repository.exportSkill"));
      }

      const contentDisposition = response.headers.get("content-disposition");
      let fileName = `${owner}-${repo}-${currentBranch || "main"}-${currentLanguage || "zh"}-skill.zip`;
      if (contentDisposition) {
        const fileNameMatch = contentDisposition.match(/filename[^;=\n]*=((['"]).*?\2|[^;\n]*)/);
        if (fileNameMatch?.[1]) {
          fileName = fileNameMatch[1].replace(/['"]/g, "");
        }
      }

      const rawBytes = await response.arrayBuffer();
      const textDecoder = new TextDecoder();
      const textPreview = textDecoder.decode(rawBytes.slice(0, 50));

      let blob: Blob;
      if (textPreview.startsWith('{"') && textPreview.includes("fileContents")) {
        const jsonString = textDecoder.decode(rawBytes);
        const json = JSON.parse(jsonString);
        const base64Content = json.fileContents as string;
        const binaryString = atob(base64Content);
        const byteArray = new Uint8Array(binaryString.length);
        for (let i = 0; i < binaryString.length; i++) {
          byteArray[i] = binaryString.charCodeAt(i);
        }
        blob = new Blob([byteArray], { type: "application/zip" });
      } else {
        blob = new Blob([rawBytes], { type: "application/zip" });
      }

      const url = window.URL.createObjectURL(blob);
      const a = document.createElement("a");
      a.href = url;
      a.download = fileName;
      document.body.appendChild(a);
      a.click();
      window.URL.revokeObjectURL(url);
      document.body.removeChild(a);
    } catch (error) {
      console.error("Export failed:", error);
    } finally {
      setIsExporting(false);
    }
  };

  const title = `${owner}/${repo}`;
  const sidebarLabels = {
    expandSection: t("repository.expandSection"),
    collapseSection: t("repository.collapseSection"),
  };

  const sidebarBanner = (
    <div className="space-y-2.5">
      {branches && (
        <div className="-mx-3 -mt-3">
          <BranchLanguageSelector
            branches={branches}
            currentBranch={currentBranch}
            currentLanguage={currentLanguage}
          />
        </div>
      )}
      <div className="grid gap-1.5">
        <Link
          href={mindMapUrl}
          className="flex items-center gap-2 rounded-lg border border-blue-500/25 bg-blue-500/10 px-2.5 py-1.5 text-[13px] font-medium leading-5 text-blue-700 transition-colors hover:bg-blue-500/15 dark:text-blue-300"
        >
          <Network className="size-3.5" />
          <span>{t("repository.mindMap")}</span>
        </Link>
        <button
          type="button"
          onClick={handleExport}
          disabled={isExporting}
          className="flex w-full items-center gap-2 rounded-lg border border-green-500/25 bg-green-500/10 px-2.5 py-1.5 text-[13px] font-medium leading-5 text-green-700 transition-colors hover:bg-green-500/15 disabled:cursor-not-allowed disabled:opacity-50 dark:text-green-300"
        >
          <Download className="size-3.5" />
          <span>{isExporting ? t("repository.exporting") : t("repository.exportSkill")}</span>
        </button>
        {hasGraphifyArtifact && (
          <Link
            href={graphifyUrl}
            className="flex items-center gap-2 rounded-lg border border-purple-500/25 bg-purple-500/10 px-2.5 py-1.5 text-[13px] font-medium leading-5 text-purple-700 transition-colors hover:bg-purple-500/15 dark:text-purple-300"
          >
            <Sparkles className="size-3.5" />
            <span>{t("repository.graphify")}</span>
          </Link>
        )}
      </div>
    </div>
  );

  return (
    <WithAnnouncement className="h-svh min-h-0 overflow-hidden bg-background">
      <div className="shrink-0 border-b border-border/70 bg-background/95 backdrop-blur">
        <div className="mx-auto flex max-w-7xl items-center justify-between gap-4 px-5 py-3 lg:px-6">
          <div className="min-w-0">
            <div className="text-[11px] uppercase tracking-[0.18em] text-muted-foreground">
              {t("repository.wikiTitle")}
            </div>
            <div className="truncate text-base font-semibold sm:text-lg">{title}</div>
          </div>
          <Link
            href="/"
            aria-label={t("backToHome")}
            className="inline-flex h-9 shrink-0 items-center gap-2 rounded-lg border border-border bg-background px-3 text-sm text-muted-foreground transition-colors hover:border-foreground/30 hover:text-foreground"
          >
            <Home className="h-4 w-4" />
            <span className="hidden sm:inline">{t("backToHome")}</span>
          </Link>
        </div>
      </div>

      <div className="mx-auto flex min-h-0 w-full max-w-7xl flex-1 flex-col gap-5 overflow-hidden px-4 py-5 lg:flex-row lg:px-5">
        <aside className="min-h-0 w-full shrink-0 lg:w-72">
          <div className="flex max-h-[38svh] min-h-0 flex-col overflow-hidden rounded-xl border border-border/70 bg-card p-3 shadow-sm lg:h-full lg:max-h-none">
            {sidebarBanner}
            <div className="wiki-scrollbar mt-3 min-h-0 overflow-y-auto border-t border-border/70 pt-3 pr-1">
              <SidebarTree
                nodes={nodes}
                owner={owner}
                repo={repo}
                queryString={queryString}
                currentPath={currentDocPath}
                labels={sidebarLabels}
              />
            </div>
          </div>
        </aside>

        <main className="min-h-0 min-w-0 flex-1">
          <div className="wiki-scrollbar h-full min-h-0 overflow-y-auto rounded-xl border border-border/70 bg-card p-4 shadow-sm xl:overflow-hidden sm:p-5">
            {isLoading ? (
              <div className="flex h-full items-center justify-center py-20">
                <div className="size-7 animate-spin rounded-full border-b-2 border-primary" />
              </div>
            ) : (
              children
            )}
          </div>
        </main>
      </div>

      <ChatAssistant
        context={{
          owner,
          repo,
          branch: currentBranch,
          language: currentLanguage,
          currentDocPath,
          catalogMenu: buildCatalogMenu(nodes),
        }}
      />
    </WithAnnouncement>
  );
}
