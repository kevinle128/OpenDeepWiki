"use client";

import { useEffect, useRef, useState, useCallback, useMemo, useSyncExternalStore } from "react";
import { Download, ZoomIn, ZoomOut, RotateCcw, List, Network } from "lucide-react";
import { useTheme } from "next-themes";
import { useTranslations } from "@/hooks/use-translations";

interface MindMapViewerProps {
  content: string;
  owner: string;
  repo: string;
  branch?: string;
  gitUrl?: string;
}

const subscribe = () => () => {};

type ViewMode = "mindmap" | "list";

interface TreeNode {
  title: string;
  filePath?: string;
  level: number;
  children: TreeNode[];
}

// 多彩配色方案 - 更柔和的颜色
const BRANCH_COLORS = [
  { line: "#ec4899", bg: "#fce7f3", border: "#ec4899", text: "#be185d" }, // 粉色
  { line: "#f59e0b", bg: "#fef3c7", border: "#f59e0b", text: "#b45309" }, // 黄色
  { line: "#10b981", bg: "#d1fae5", border: "#10b981", text: "#047857" }, // 绿色
  { line: "#3b82f6", bg: "#dbeafe", border: "#3b82f6", text: "#1d4ed8" }, // 蓝色
  { line: "#8b5cf6", bg: "#ede9fe", border: "#8b5cf6", text: "#6d28d9" }, // 紫色
  { line: "#14b8a6", bg: "#ccfbf1", border: "#14b8a6", text: "#0f766e" }, // 青色
  { line: "#ef4444", bg: "#fee2e2", border: "#ef4444", text: "#b91c1c" }, // 红色
  { line: "#6366f1", bg: "#e0e7ff", border: "#6366f1", text: "#4338ca" }, // 靛蓝
];

// 深色模式配色
const BRANCH_COLORS_DARK = [
  { line: "#f472b6", bg: "#831843", border: "#f472b6", text: "#fbcfe8" },
  { line: "#fbbf24", bg: "#78350f", border: "#fbbf24", text: "#fef3c7" },
  { line: "#34d399", bg: "#064e3b", border: "#34d399", text: "#d1fae5" },
  { line: "#60a5fa", bg: "#1e3a8a", border: "#60a5fa", text: "#dbeafe" },
  { line: "#a78bfa", bg: "#4c1d95", border: "#a78bfa", text: "#ede9fe" },
  { line: "#2dd4bf", bg: "#134e4a", border: "#2dd4bf", text: "#ccfbf1" },
  { line: "#f87171", bg: "#7f1d1d", border: "#f87171", text: "#fee2e2" },
  { line: "#818cf8", bg: "#312e81", border: "#818cf8", text: "#e0e7ff" },
];

// 中心节点颜色
const CENTER_COLOR = { bg: "#f3e8ff", border: "#a855f7", text: "#7c3aed" };
const CENTER_COLOR_DARK = { bg: "#581c87", border: "#c084fc", text: "#e9d5ff" };

/**
 * 解析思维导图内容为树形结构
 */
function parseMindMapContent(content: string): TreeNode[] {
  const lines = content.split("\n").filter(line => line.trim());
  const root: TreeNode[] = [];
  const stack: { node: TreeNode; level: number }[] = [];

  for (const line of lines) {
    const match = line.match(/^(#{1,6})\s+(.+)$/);
    if (!match) continue;

    const level = match[1].length;
    const titlePart = match[2].trim();
    
    const colonIndex = titlePart.lastIndexOf(":");
    let title: string;
    let filePath: string | undefined;
    
    if (colonIndex > 0 && !titlePart.substring(colonIndex).includes(" ")) {
      title = titlePart.substring(0, colonIndex).trim();
      filePath = titlePart.substring(colonIndex + 1).trim();
    } else {
      title = titlePart;
    }

    const node: TreeNode = { title, filePath, level, children: [] };

    while (stack.length > 0 && stack[stack.length - 1].level >= level) {
      stack.pop();
    }

    if (stack.length === 0) {
      root.push(node);
    } else {
      stack[stack.length - 1].node.children.push(node);
    }

    stack.push({ node, level });
  }

  return root;
}

interface LayoutNode {
  title: string;
  x: number;
  y: number;
  width: number;
  height: number;
  color: { line: string; bg: string; border: string; text: string };
  children: LayoutNode[];
  isCenter?: boolean;
  isLeft?: boolean;
}

// 布局常量
const NODE_HEIGHT = 32;
const NODE_MIN_WIDTH = 60;
const LEVEL_GAP_H = 120; // 水平层级间距
const NODE_GAP_V = 8; // 垂直节点间距
const CENTER_HEIGHT = 40;

/**
 * 测量文本宽度
 */
function measureText(text: string, isCenter: boolean): number {
  const fontSize = isCenter ? 14 : 11;
  const charWidth = fontSize * 0.6;
  const padding = 24;
  return Math.min(Math.max(text.length * charWidth + padding, isCenter ? 100 : NODE_MIN_WIDTH), 150);
}

/**
 * 计算子树高度
 */
function getSubtreeHeight(node: LayoutNode): number {
  if (node.children.length === 0) {
    return node.height;
  }
  const childrenHeight = node.children.reduce(
    (sum, child) => sum + getSubtreeHeight(child) + NODE_GAP_V,
    -NODE_GAP_V
  );
  return Math.max(node.height, childrenHeight);
}

/**
 * 递归构建布局节点
 */
function buildLayoutTree(
  node: TreeNode,
  color: { line: string; bg: string; border: string; text: string },
  isLeft: boolean
): LayoutNode {
  const width = measureText(node.title, false);
  const layoutNode: LayoutNode = {
    title: node.title,
    x: 0,
    y: 0,
    width,
    height: NODE_HEIGHT,
    color,
    children: [],
    isLeft,
  };

  if (node.children.length > 0) {
    layoutNode.children = node.children.map(child =>
      buildLayoutTree(child, color, isLeft)
    );
  }

  return layoutNode;
}

/**
 * 递归定位节点
 */
function positionSubtree(
  node: LayoutNode,
  x: number,
  centerY: number,
  isLeft: boolean
) {
  node.x = x;
  node.y = centerY - node.height / 2;

  if (node.children.length > 0) {
    const totalHeight = node.children.reduce(
      (sum, child) => sum + getSubtreeHeight(child) + NODE_GAP_V,
      -NODE_GAP_V
    );
    
    let currentY = centerY - totalHeight / 2;
    
    node.children.forEach(child => {
      const childHeight = getSubtreeHeight(child);
      const childCenterY = currentY + childHeight / 2;
      const childX = isLeft
        ? x - LEVEL_GAP_H - child.width
        : x + node.width + LEVEL_GAP_H;
      
      positionSubtree(child, childX, childCenterY, isLeft);
      currentY += childHeight + NODE_GAP_V;
    });
  }
}

/**
 * 计算节点布局
 */
function calculateLayout(
  nodes: TreeNode[],
  repoName: string,
  isDark: boolean
): { root: LayoutNode; width: number; height: number } {
  const colors = isDark ? BRANCH_COLORS_DARK : BRANCH_COLORS;
  const centerColor = isDark ? CENTER_COLOR_DARK : CENTER_COLOR;

  // 获取要分布的节点
  let nodesToDistribute: TreeNode[] = nodes;
  if (nodes.length === 1 && nodes[0].children.length > 0) {
    nodesToDistribute = nodes[0].children;
  }

  // 创建中心节点
  const centerWidth = measureText(repoName, true);
  const centerNode: LayoutNode = {
    title: repoName,
    x: 0,
    y: 0,
    width: centerWidth,
    height: CENTER_HEIGHT,
    color: { ...centerColor, line: centerColor.border },
    children: [],
    isCenter: true,
  };

  // 分配左右节点 - 简单交替分配
  const leftNodes: TreeNode[] = [];
  const rightNodes: TreeNode[] = [];
  
  nodesToDistribute.forEach((node, i) => {
    if (i % 2 === 0) {
      leftNodes.push(node);
    } else {
      rightNodes.push(node);
    }
  });

  // 构建左右子树
  const leftLayoutNodes = leftNodes.map((n, i) =>
    buildLayoutTree(n, colors[i % colors.length], true)
  );
  const rightLayoutNodes = rightNodes.map((n, i) =>
    buildLayoutTree(n, colors[(i + leftNodes.length) % colors.length], false)
  );

  // 计算左右两边的总高度
  const leftHeight = leftLayoutNodes.reduce(
    (sum, n) => sum + getSubtreeHeight(n) + NODE_GAP_V * 2,
    0
  );
  const rightHeight = rightLayoutNodes.reduce(
    (sum, n) => sum + getSubtreeHeight(n) + NODE_GAP_V * 2,
    0
  );
  // 定位中心节点
  centerNode.x = 0;
  centerNode.y = 0;

  // 定位左边节点
  if (leftLayoutNodes.length > 0) {
    let currentY = -leftHeight / 2;
    leftLayoutNodes.forEach(node => {
      const subtreeHeight = getSubtreeHeight(node);
      const nodeCenterY = currentY + subtreeHeight / 2;
      const nodeX = -LEVEL_GAP_H - node.width;
      positionSubtree(node, nodeX, nodeCenterY, true);
      currentY += subtreeHeight + NODE_GAP_V * 2;
    });
  }

  // 定位右边节点
  if (rightLayoutNodes.length > 0) {
    let currentY = -rightHeight / 2;
    rightLayoutNodes.forEach(node => {
      const subtreeHeight = getSubtreeHeight(node);
      const nodeCenterY = currentY + subtreeHeight / 2;
      const nodeX = centerWidth + LEVEL_GAP_H;
      positionSubtree(node, nodeX, nodeCenterY, false);
      currentY += subtreeHeight + NODE_GAP_V * 2;
    });
  }

  centerNode.children = [...leftLayoutNodes, ...rightLayoutNodes];

  // 计算画布尺寸
  const getAllNodes = (node: LayoutNode): LayoutNode[] => {
    return [node, ...node.children.flatMap(getAllNodes)];
  };

  const allNodes = getAllNodes(centerNode);
  let minX = 0, maxX = centerWidth, minY = -CENTER_HEIGHT / 2, maxY = CENTER_HEIGHT / 2;
  
  allNodes.forEach(node => {
    minX = Math.min(minX, node.x);
    maxX = Math.max(maxX, node.x + node.width);
    minY = Math.min(minY, node.y);
    maxY = Math.max(maxY, node.y + node.height);
  });

  const padding = 40;
  const width = maxX - minX + padding * 2;
  const height = maxY - minY + padding * 2;

  // 偏移所有节点
  const offsetX = -minX + padding;
  const offsetY = -minY + padding;

  const offsetNodes = (node: LayoutNode) => {
    node.x += offsetX;
    node.y += offsetY;
    node.children.forEach(offsetNodes);
  };

  offsetNodes(centerNode);

  return { root: centerNode, width, height };
}

/**
 * 绘制思维导图
 */
function drawMindMap(
  ctx: CanvasRenderingContext2D,
  root: LayoutNode
) {
  // 绘制连接线 - 从父节点到子节点
  const drawConnections = (parent: LayoutNode) => {
    parent.children.forEach(child => {
      const isLeft = child.isLeft;
      
      // 父节点连接点
      const parentX = isLeft ? parent.x : parent.x + parent.width;
      const parentY = parent.y + parent.height / 2;
      
      // 子节点连接点
      const childX = isLeft ? child.x + child.width : child.x;
      const childY = child.y + child.height / 2;

      // 绘制平滑曲线
      ctx.beginPath();
      ctx.moveTo(parentX, parentY);
      
      const midX = (parentX + childX) / 2;
      ctx.bezierCurveTo(midX, parentY, midX, childY, childX, childY);
      
      ctx.strokeStyle = child.color.line;
      ctx.lineWidth = 2;
      ctx.stroke();

      // 递归绘制子节点的连接线
      drawConnections(child);
    });
  };

  // 绘制节点
  const drawNode = (node: LayoutNode) => {
    const { x, y, width, height, color, title, isCenter } = node;
    const radius = height / 2;

    // 绘制胶囊形状
    ctx.beginPath();
    ctx.moveTo(x + radius, y);
    ctx.lineTo(x + width - radius, y);
    ctx.arc(x + width - radius, y + height / 2, radius, -Math.PI / 2, Math.PI / 2);
    ctx.lineTo(x + radius, y + height);
    ctx.arc(x + radius, y + height / 2, radius, Math.PI / 2, -Math.PI / 2);
    ctx.closePath();

    ctx.fillStyle = color.bg;
    ctx.fill();
    ctx.strokeStyle = color.border;
    ctx.lineWidth = 2;
    ctx.stroke();

    // 绘制文字
    ctx.fillStyle = color.text;
    ctx.font = `${isCenter ? "600 13px" : "500 11px"} system-ui, -apple-system, sans-serif`;
    ctx.textAlign = "center";
    ctx.textBaseline = "middle";
    
    // 截断过长文字
    let displayTitle = title;
    const maxWidth = width - 16;
    while (ctx.measureText(displayTitle).width > maxWidth && displayTitle.length > 3) {
      displayTitle = displayTitle.slice(0, -4) + "...";
    }
    
    ctx.fillText(displayTitle, x + width / 2, y + height / 2 + 1);

    // 递归绘制子节点
    node.children.forEach(drawNode);
  };

  // 先绘制连接线，再绘制节点
  drawConnections(root);
  drawNode(root);
}

/**
 * 列表视图节点
 */
function ListNodeItem({ node, gitUrl, branch }: { node: TreeNode; gitUrl: string; branch: string }) {
  const [expanded, setExpanded] = useState(true);
  const hasChildren = node.children.length > 0;

  const levelColors = [
    "border-l-pink-500",
    "border-l-amber-500",
    "border-l-emerald-500",
    "border-l-blue-500",
    "border-l-violet-500",
    "border-l-teal-500",
  ];

  return (
    <div className={`border-l-2 ${levelColors[(node.level - 1) % levelColors.length]} pl-3 my-1`}>
      <div
        className="flex items-center gap-2 py-1 cursor-pointer hover:bg-fd-muted/50 rounded px-2 -ml-2"
        onClick={() => hasChildren && setExpanded(!expanded)}
      >
        <span className={`text-sm ${hasChildren ? "font-medium" : ""}`}>{node.title}</span>
        {node.filePath && (
          <a
            href={buildFileUrl(gitUrl, branch, node.filePath)}
            target="_blank"
            rel="noopener noreferrer"
            onClick={(e) => e.stopPropagation()}
            className="text-xs text-fd-muted-foreground hover:text-fd-foreground"
          >
            <code className="bg-fd-muted px-1 rounded">{node.filePath}</code>
          </a>
        )}
      </div>
      {hasChildren && expanded && (
        <div className="ml-2">
          {node.children.map((child, i) => (
            <ListNodeItem key={`${child.title}-${i}`} node={child} gitUrl={gitUrl} branch={branch} />
          ))}
        </div>
      )}
    </div>
  );
}

function buildFileUrl(gitUrl: string, branch: string, filePath: string): string {
  let url = gitUrl.replace(/\.git$/, "").trim();
  if (url.startsWith("git@")) {
    url = url.replace("git@", "https://").replace(":", "/");
  }
  url = url.replace(/\/$/, "");

  if (url.includes("github.com")) return `${url}/blob/${branch}/${filePath}`;
  if (url.includes("gitlab")) return `${url}/-/blob/${branch}/${filePath}`;
  if (url.includes("gitee.com")) return `${url}/blob/${branch}/${filePath}`;
  if (url.includes("bitbucket.org")) return `${url}/src/${branch}/${filePath}`;
  return `${url}/blob/${branch}/${filePath}`;
}

export function MindMapViewer({ content, owner, repo, branch = "main", gitUrl }: MindMapViewerProps) {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const containerRef = useRef<HTMLDivElement>(null);
  const [viewMode, setViewMode] = useState<ViewMode>("mindmap");
  const [scale, setScale] = useState(1);
  const [baseScale, setBaseScale] = useState(1);
  const mounted = useSyncExternalStore(subscribe, () => true, () => false);
  const { resolvedTheme } = useTheme();
  const t = useTranslations();

  const defaultGitUrl = gitUrl || `https://github.com/${owner}/${repo}`;
  const repoName = `${owner}/${repo}`;
  const treeNodes = useMemo(() => parseMindMapContent(content), [content]);
  const isDark = resolvedTheme === "dark";

  // 鼠标滚轮缩放
  useEffect(() => {
    if (!mounted || viewMode !== "mindmap" || !containerRef.current) return;

    const container = containerRef.current;
    const handleWheel = (e: WheelEvent) => {
      if (e.ctrlKey || e.metaKey) {
        e.preventDefault();
        const delta = e.deltaY > 0 ? -0.1 : 0.1;
        setScale(s => Math.min(Math.max(s + delta, 0.3), 3));
      }
    };

    container.addEventListener("wheel", handleWheel, { passive: false });
    return () => container.removeEventListener("wheel", handleWheel);
  }, [mounted, viewMode]);

  // 绘制思维导图
  useEffect(() => {
    if (!mounted || viewMode !== "mindmap" || !canvasRef.current || !containerRef.current) return;

    const canvas = canvasRef.current;
    const container = containerRef.current;
    const ctx = canvas.getContext("2d");
    if (!ctx) return;

    const { root, width, height } = calculateLayout(treeNodes, repoName, isDark);
    
    // 计算自适应缩放
    const observer = new ResizeObserver(() => {
      const containerWidth = container.clientWidth - 32;
      const containerHeight = Math.min(window.innerHeight * 0.65, 550);
      setBaseScale(Math.min(containerWidth / width, containerHeight / height, 1));
    });
    observer.observe(container);

    // 设置高清画布
    const dpr = window.devicePixelRatio || 1;
    canvas.width = width * dpr;
    canvas.height = height * dpr;
    canvas.style.width = `${width}px`;
    canvas.style.height = `${height}px`;
    ctx.scale(dpr, dpr);

    // 清空并绘制背景
    ctx.clearRect(0, 0, width, height);
    ctx.fillStyle = isDark ? "#0f172a" : "#ffffff";
    ctx.fillRect(0, 0, width, height);

    // 绘制思维导图
    drawMindMap(ctx, root);
    return () => observer.disconnect();
  }, [mounted, viewMode, treeNodes, repoName, isDark]);

  const actualScale = baseScale * scale;
  const handleZoomIn = useCallback(() => setScale(s => Math.min(s + 0.2, 3)), []);
  const handleZoomOut = useCallback(() => setScale(s => Math.max(s - 0.2, 0.3)), []);
  const handleResetZoom = useCallback(() => setScale(1), []);

  const handleDownloadPng = useCallback(() => {
    if (!canvasRef.current) return;
    const link = document.createElement("a");
    link.download = `${owner}-${repo}-mindmap.png`;
    link.href = canvasRef.current.toDataURL("image/png");
    link.click();
  }, [owner, repo]);

  const handleDownloadSvg = useCallback(() => {
    if (!canvasRef.current) return;
    const canvas = canvasRef.current;
    const dataUrl = canvas.toDataURL("image/png");
    const svgContent = `<?xml version="1.0" encoding="UTF-8"?>
<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" 
     width="${canvas.width}" height="${canvas.height}">
  <image xlink:href="${dataUrl}" width="${canvas.width}" height="${canvas.height}"/>
</svg>`;
    const blob = new Blob([svgContent], { type: "image/svg+xml" });
    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.download = `${owner}-${repo}-mindmap.svg`;
    link.href = url;
    link.click();
    URL.revokeObjectURL(url);
  }, [owner, repo]);

  if (treeNodes.length === 0 && !content.trim()) {
    return (
      <div className="text-center py-12 text-fd-muted-foreground">
        <p>{t("mindmap.emptyContent")}</p>
      </div>
    );
  }

  return (
    <div className="space-y-4 px-4 md:px-6 lg:px-8">
      {/* 工具栏 */}
      <div className="flex items-center justify-between gap-4 p-3 bg-fd-muted/30 rounded-lg flex-wrap">
        <div className="flex items-center gap-2">
          <button
            onClick={() => setViewMode("mindmap")}
            className={`flex items-center gap-1.5 px-3 py-1.5 rounded-md text-sm transition-colors ${
              viewMode === "mindmap" ? "bg-fd-primary text-fd-primary-foreground" : "hover:bg-fd-muted"
            }`}
          >
            <Network className="h-4 w-4" />
            {t("mindmap.toolbar.mindmap")}
          </button>
          <button
            onClick={() => setViewMode("list")}
            className={`flex items-center gap-1.5 px-3 py-1.5 rounded-md text-sm transition-colors ${
              viewMode === "list" ? "bg-fd-primary text-fd-primary-foreground" : "hover:bg-fd-muted"
            }`}
          >
            <List className="h-4 w-4" />
            {t("mindmap.toolbar.list")}
          </button>
        </div>

        {viewMode === "mindmap" && (
          <div className="flex items-center gap-2">
            <div className="flex items-center gap-1 border rounded-md">
              <button onClick={handleZoomOut} className="p-1.5 hover:bg-fd-muted rounded-l-md" title={t("mindmap.toolbar.zoomOut")}>
                <ZoomOut className="h-4 w-4" />
              </button>
              <span className="px-2 text-sm min-w-[3rem] text-center">{Math.round(actualScale * 100)}%</span>
              <button onClick={handleZoomIn} className="p-1.5 hover:bg-fd-muted" title={t("mindmap.toolbar.zoomIn")}>
                <ZoomIn className="h-4 w-4" />
              </button>
              <button onClick={handleResetZoom} className="p-1.5 hover:bg-fd-muted rounded-r-md border-l" title={t("mindmap.toolbar.reset")}>
                <RotateCcw className="h-4 w-4" />
              </button>
            </div>
            <div className="flex items-center gap-1 border rounded-md">
              <button onClick={handleDownloadPng} className="flex items-center gap-1.5 px-3 py-1.5 hover:bg-fd-muted rounded-l-md text-sm">
                <Download className="h-4 w-4" />PNG
              </button>
              <button onClick={handleDownloadSvg} className="flex items-center gap-1.5 px-3 py-1.5 hover:bg-fd-muted rounded-r-md border-l text-sm">
                <Download className="h-4 w-4" />SVG
              </button>
            </div>
          </div>
        )}
      </div>

      {/* 思维导图视图 */}
      {viewMode === "mindmap" && (
        <div className="border rounded-lg overflow-hidden bg-fd-card">
          <div ref={containerRef} className="overflow-auto flex items-center justify-center p-4 relative" style={{ minHeight: "calc(100vh - 280px)" }}>
            <div className="transition-transform duration-200" style={{ transform: `scale(${actualScale})`, transformOrigin: "center center" }}>
              <canvas ref={canvasRef} />
            </div>
            <div className="absolute bottom-2 left-1/2 -translate-x-1/2 text-xs text-fd-muted-foreground bg-fd-background/80 px-2 py-1 rounded">
              Ctrl/Cmd + {t("mindmap.tips.scroll")}
            </div>
          </div>
        </div>
      )}

      {/* 列表视图 */}
      {viewMode === "list" && (
        <div className="border rounded-lg p-4 bg-fd-card">
          <div className="text-sm text-fd-muted-foreground mb-4">{t("mindmap.tips.expand")} • {t("mindmap.tips.link")}</div>
          <div className="space-y-1">
            {treeNodes.map((node, index) => (
              <ListNodeItem key={`${node.title}-${index}`} node={node} gitUrl={defaultGitUrl} branch={branch} />
            ))}
          </div>
        </div>
      )}
    </div>
  );
}
