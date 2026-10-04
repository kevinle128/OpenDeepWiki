import { NextRequest, NextResponse } from 'next/server';

// 缓存环境变量和上次加载时间
let cachedApiUrl: string | null = null;
let lastLoadTime = 0;
const CACHE_TTL = 5000; // 5秒缓存，方便热更新

/**
 * 动态加载 .env 文件获取 API_PROXY_URL
 * 优先级：系统环境变量 > .env.local > .env
 */
function getApiProxyUrl(): string {
  // 优先使用系统环境变量（Docker/K8s 传入）
  if (process.env.API_PROXY_URL) {
    return process.env.API_PROXY_URL;
  }
  
  const now = Date.now();
  // 使用缓存
  if (cachedApiUrl !== null && (now - lastLoadTime) < CACHE_TTL) {
    return cachedApiUrl;
  }
  
  try {
    // 使用 require 避免构建时打包 Node.js 模块
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const fs = require('fs');
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const path = require('path');
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const dotenv = require('dotenv');
    
    // 动态读取 .env 文件
    const rootDir = process.cwd();
    const envLocalPath = path.resolve(rootDir, '.env.local');
    const envPath = path.resolve(rootDir, '.env');
    
    // 优先加载 .env.local
    if (fs.existsSync(envLocalPath)) {
      const result = dotenv.config({ path: envLocalPath });
      if (result.parsed?.API_PROXY_URL) {
        cachedApiUrl = result.parsed.API_PROXY_URL;
        lastLoadTime = now;
        return cachedApiUrl!;
      }
    }
    
    // 其次加载 .env
    if (fs.existsSync(envPath)) {
      const result = dotenv.config({ path: envPath });
      if (result.parsed?.API_PROXY_URL) {
        cachedApiUrl = result.parsed.API_PROXY_URL;
        lastLoadTime = now;
        return cachedApiUrl!;
      }
    }
  } catch {
    // 模块加载失败
  }
  
  cachedApiUrl = '';
  lastLoadTime = now;
  return '';
}

// 生成请求 ID
function generateRequestId(): string {
  return `req_${Date.now()}_${Math.random().toString(36).substring(2, 8)}`;
}

// 格式化时间戳
function formatTimestamp(): string {
  return new Date().toISOString();
}

// 计算耗时
function formatDuration(startTime: number): string {
  return `${Date.now() - startTime}ms`;
}

// 强制刷新的日志输出（生产环境下 console.log 可能被缓冲）
function log(message: string): void {
  process.stdout.write(message + '\n');
}

function logError(message: string): void {
  process.stderr.write(message + '\n');
}

async function proxyRequest(request: NextRequest) {
  const requestId = generateRequestId();
  const startTime = Date.now();
  const apiUrl = getApiProxyUrl();
  const pathname = request.nextUrl.pathname;
  const searchParams = request.nextUrl.search;

  log(`[${formatTimestamp()}] [${requestId}] ➡️  ${request.method} ${pathname}${searchParams}`);
  
  // 检查环境变量是否配置
  if (!apiUrl) {
    logError(`[${formatTimestamp()}] [${requestId}] ❌ API_PROXY_URL 环境变量未配置`);
    return NextResponse.json(
      {
        error: 'API_PROXY_URL_NOT_CONFIGURED',
        message: '后端 API 地址未配置，请设置 API_PROXY_URL 环境变量',
        requestId,
        timestamp: formatTimestamp(),
      },
      { status: 503 }
    );
  }

  const targetUrl = `${apiUrl}${pathname}${searchParams}`;
  log(`[${formatTimestamp()}] [${requestId}] 🎯 转发目标: ${targetUrl}`);

  try {
    // 构建转发请求的 headers
    const headers = new Headers();
    request.headers.forEach((value, key) => {
      // 跳过 host 相关的 header
      if (!['host', 'connection'].includes(key.toLowerCase())) {
        headers.set(key, value);
      }
    });

    // 转发请求
    log(`[${formatTimestamp()}] [${requestId}] 🚀 开始转发请求...`);
    const response = await fetch(targetUrl, {
      method: request.method,
      headers,
      body: request.body,
      signal: request.signal,
      // @ts-expect-error duplex is required for streaming body
      duplex: 'half',
    });

    log(`[${formatTimestamp()}] [${requestId}] ✅ 后端响应: ${response.status} ${response.statusText} [${formatDuration(startTime)}]`);

    // 构建响应 headers
    const responseHeaders = new Headers();
    response.headers.forEach((value, key) => {
      // 跳过一些不应该转发的 header
      if (!['content-encoding', 'transfer-encoding'].includes(key.toLowerCase())) {
        responseHeaders.set(key, value);
      }
    });

    // 添加代理信息到响应头
    responseHeaders.set('X-Proxy-Request-Id', requestId);
    responseHeaders.set('X-Proxy-Duration', formatDuration(startTime));

    // 返回响应
    return new NextResponse(response.body, {
      status: response.status,
      statusText: response.statusText,
      headers: responseHeaders,
    });
  } catch (error) {
    const duration = formatDuration(startTime);
    const errorMessage = error instanceof Error ? error.message : '未知错误';
    const errorStack = error instanceof Error ? error.stack : undefined;
    
    logError(`[${formatTimestamp()}] [${requestId}] ❌ 代理请求失败 [${duration}]`);
    logError(`[${formatTimestamp()}] [${requestId}] 📛 错误信息: ${errorMessage}`);
    if (errorStack) {
      logError(`[${formatTimestamp()}] [${requestId}] 📚 错误堆栈:\n${errorStack}`);
    }
    
    // 判断错误类型
    const isConnectionError = errorMessage.includes('ECONNREFUSED') || 
                              errorMessage.includes('ETIMEDOUT') ||
                              errorMessage.includes('fetch failed') ||
                              errorMessage.includes('ENOTFOUND');

    if (isConnectionError) {
      logError(`[${formatTimestamp()}] [${requestId}] 🔌 连接错误: 无法连接到 ${apiUrl}`);
      return NextResponse.json(
        {
          error: 'BACKEND_CONNECTION_FAILED',
          message: `无法连接到后端服务: ${apiUrl}`,
          detail: errorMessage,
          requestId,
          timestamp: formatTimestamp(),
          duration,
        },
        { status: 502 }
      );
    }

    return NextResponse.json(
      {
        error: 'PROXY_ERROR',
        message: '代理请求失败',
        detail: errorMessage,
        requestId,
        timestamp: formatTimestamp(),
        duration,
      },
      { status: 500 }
    );
  }
}

export async function GET(request: NextRequest) {
  return proxyRequest(request);
}

export async function POST(request: NextRequest) {
  return proxyRequest(request);
}

export async function PUT(request: NextRequest) {
  return proxyRequest(request);
}

export async function DELETE(request: NextRequest) {
  return proxyRequest(request);
}

export async function PATCH(request: NextRequest) {
  return proxyRequest(request);
}

export async function OPTIONS(request: NextRequest) {
  return proxyRequest(request);
}
