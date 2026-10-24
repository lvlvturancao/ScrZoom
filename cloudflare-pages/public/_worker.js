/**
 * Cloudflare Pages _worker.js - 水月清影主页浏览与下载量统计
 * 使用 Advanced Mode，替代 functions/ 目录。KV 绑定：SHUIYUE_STATS_KV（wrangler.toml）
 * - 访问 '/' 或 '/index.html' → page_view（主页浏览量）
 * - 下载正式包 '/shuiyue-<版本>.zip' → package_download（下载量）
 *   只认正式包文件名，带 Range 的分段/探测请求不计，否则一次完整性核对会被记成下载。
 * - GET /api/stats → { available, date, pageViewsTotal, downloadsTotal, pageViewsToday, downloadsToday }
 * 累计(total) 与 今日(daily) 两组计数，按 Asia/Shanghai 的当天日期写入。
 */

const STATS_KV_BINDING_NAME = 'SHUIYUE_STATS_KV';

/** 正式包文件名：/shuiyue-<major>.<minor>.<patch>.zip */
const OFFICIAL_PACKAGE = /^\/shuiyue-\d+\.\d+\.\d+\.zip$/i;

export default {
  async fetch(request, env, ctx) {
    const url = new URL(request.url);

    if (url.pathname === '/api/stats') {
      if (request.method !== 'GET') return jsonResponse({ error: 'Method not allowed' }, 405);
      const stats = await readStats(env);
      return jsonResponse(stats, 200, { 'Cache-Control': 'no-store' });
    }

    const response = await env.ASSETS.fetch(request);
    recordPublicStats(request, env, ctx, response);
    return response;
  },
};

function recordPublicStats(request, env, ctx, response) {
  if (request.method !== 'GET' || !response || !response.ok || !ctx) return;
  const pathname = new URL(request.url).pathname;
  if (pathname === '/' || pathname === '/index.html') {
    ctx.waitUntil(incrementStats(env, 'page_view'));
  } else if (OFFICIAL_PACKAGE.test(pathname) && !request.headers.get('Range')) {
    ctx.waitUntil(incrementStats(env, 'package_download'));
  }
}

async function readStats(env) {
  const kv = env[STATS_KV_BINDING_NAME];
  const date = currentStatsDate();
  if (!kv) {
    return {
      available: false,
      date,
      pageViewsTotal: 0,
      downloadsTotal: 0,
      pageViewsToday: 0,
      downloadsToday: 0,
    };
  }

  const [pageViewsTotal, downloadsTotal, pageViewsToday, downloadsToday] = await Promise.all([
    readCounter(kv, 'total:page_view'),
    readCounter(kv, 'total:package_download'),
    readCounter(kv, `daily:${date}:page_view`),
    readCounter(kv, `daily:${date}:package_download`),
  ]);

  return { available: true, date, pageViewsTotal, downloadsTotal, pageViewsToday, downloadsToday };
}

async function incrementStats(env, eventName) {
  const kv = env[STATS_KV_BINDING_NAME];
  if (!kv) return;
  const date = currentStatsDate();
  try {
    await Promise.all([
      incrementCounter(kv, `total:${eventName}`),
      incrementCounter(kv, `daily:${date}:${eventName}`),
    ]);
  } catch (error) {
    console.error('Stats write failed:', error);
  }
}

async function readCounter(kv, key) {
  const number = Number((await kv.get(key)) || '0');
  return Number.isFinite(number) ? number : 0;
}

async function incrementCounter(kv, key) {
  const current = await readCounter(kv, key);
  await kv.put(key, String(current + 1));
}

function currentStatsDate() {
  return new Intl.DateTimeFormat('en-CA', {
    timeZone: 'Asia/Shanghai',
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
  }).format(new Date());
}

function jsonResponse(body, status = 200, headers = {}) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json; charset=utf-8', ...headers },
  });
}
