// Frontend timing probe (1.46.0 lane M): first render of a library root and a big folder, and a long list's render and scroll,
// in a real Chromium (Playwright). Counts and timings only - no screenshots, no text from the page.
//
//   MP_PERF_PASSWORD=... node frontend_probe.mjs <base> <user> <libraryId> <bigFolderId> <runs> <out.json>
//
// Needs the `playwright` package (e.g. in mcr.microsoft.com/playwright:v1.64.0-noble with `npm i playwright@1.63.0`). Use it
// ONLY against an instance you run yourself.
import { chromium } from 'playwright';
import { writeFileSync } from 'node:fs';

const [base, user, lib, big, runsArg, out] = process.argv.slice(2);
const runs = Number(runsArg || 5);
const password = process.env.MP_PERF_PASSWORD || '';

const pct = (xs, q) => {
  if (!xs.length) return null;
  const v = [...xs].sort((a, b) => a - b);
  return Math.round(v[Math.max(0, Math.min(v.length - 1, Math.ceil(q * v.length) - 1))] * 10) / 10;
};

async function instrument(page) {
  await page.addInitScript(() => {
    window.__mp = { longTasks: [], frames: [], recording: false };
    try {
      new PerformanceObserver((list) => {
        for (const e of list.getEntries()) window.__mp.longTasks.push({ start: e.startTime, dur: e.duration });
      }).observe({ type: 'longtask', buffered: true });
    } catch { /* not supported */ }
    let last = 0;
    const tick = (t) => {
      if (window.__mp.recording && last) window.__mp.frames.push(t - last);
      last = t;
      requestAnimationFrame(tick);
    };
    requestAnimationFrame(tick);
  });
}

async function cards(page) {
  return page.evaluate(() => document.querySelectorAll('a.node-card, app-node-row').length);
}

async function timeView(page, url) {
  const t0 = Date.now();
  await page.goto(url, { waitUntil: 'commit' });
  await page.waitForSelector('a.node-card, app-node-row', { timeout: 60000 });
  const firstCard = Date.now() - t0;
  // First page complete: the card count stops growing for 300 ms.
  let n = await cards(page), stableSince = Date.now();
  while (Date.now() - stableSince < 300 && Date.now() - t0 < 60000) {
    await page.waitForTimeout(50);
    const m = await cards(page);
    if (m !== n) { n = m; stableSince = Date.now(); }
  }
  const firstPage = stableSince - t0;
  const nav = await page.evaluate(() => {
    const e = performance.getEntriesByType('navigation')[0];
    const res = performance.getEntriesByType('resource');
    const js = res.filter((r) => r.initiatorType === 'script' || r.name.endsWith('.js'));
    return {
      domContentLoaded: e ? Math.round(e.domContentLoadedEventEnd) : null,
      load: e ? Math.round(e.loadEventEnd) : null,
      jsTransferBytes: js.reduce((s, r) => s + (r.transferSize || 0), 0),
      jsDecodedBytes: js.reduce((s, r) => s + (r.decodedBodySize || 0), 0),
      apiRequests: res.filter((r) => r.name.includes('/api/v1/') && !r.name.includes('/cover')).length,
      coverRequests: res.filter((r) => r.name.includes('/cover')).length,
      longTaskMs: window.__mp.longTasks.reduce((s, t) => s + t.dur, 0),
      longTasks: window.__mp.longTasks.length,
      heap: performance.memory ? performance.memory.usedJSHeapSize : null,
      domNodes: document.getElementsByTagName('*').length,
    };
  });
  return { firstCard, firstPage, cards: n, ...nav };
}

async function longList(page, url, target = 500) {
  await page.goto(url, { waitUntil: 'commit' });
  await page.waitForSelector('a.node-card, app-node-row', { timeout: 60000 });
  await page.evaluate(() => { window.__mp.frames = []; window.__mp.longTasks = []; window.__mp.recording = true; });
  const t0 = Date.now();
  const marks = [];
  let n = await cards(page), idleSince = Date.now(), nextMark = 100;
  while (n < target && Date.now() - idleSince < 8000) {
    await page.mouse.wheel(0, 2400);
    await page.waitForTimeout(100);
    const m = await cards(page);
    if (m > n) { n = m; idleSince = Date.now(); }
    while (n >= nextMark) { marks.push({ cards: nextMark, ms: Date.now() - t0 }); nextMark += 100; }
  }
  const stats = await page.evaluate(() => {
    window.__mp.recording = false;
    return {
      frames: window.__mp.frames,
      longTaskMs: window.__mp.longTasks.reduce((s, t) => s + t.dur, 0),
      longTasks: window.__mp.longTasks.length,
      heap: performance.memory ? performance.memory.usedJSHeapSize : null,
      domNodes: document.getElementsByTagName('*').length,
    };
  });
  return {
    cards: n, ms: Date.now() - t0, marks,
    frameP50: pct(stats.frames, 0.5), frameP95: pct(stats.frames, 0.95), frameMax: pct(stats.frames, 1),
    framesOver50: stats.frames.filter((f) => f > 50).length, frames: stats.frames.length,
    longTaskMs: Math.round(stats.longTaskMs), longTasks: stats.longTasks, heap: stats.heap, domNodes: stats.domNodes,
  };
}

const browser = await chromium.launch({ args: ['--enable-precise-memory-info'] });
const result = { runs: [], warm: [], long: [] };
for (let r = 0; r < runs; r++) {
  const ctx = await browser.newContext({ viewport: { width: 1280, height: 900 } });
  const page = await ctx.newPage();
  await instrument(page);
  const login = await page.request.post(`${base}/api/v1/auth/login`, { data: { username: user, password } });
  if (!login.ok()) throw new Error(`login ${login.status()}`);
  const root = await timeView(page, `${base}/libraries/${lib}`);
  const folder = await timeView(page, `${base}/libraries/${lib}/browse/${big}`);
  result.runs.push({ root, folder });
  // Warm: the same views again in the same tab (bundle and API answers cached by the browser where the server allows).
  const rootWarm = await timeView(page, `${base}/libraries/${lib}`);
  const folderWarm = await timeView(page, `${base}/libraries/${lib}/browse/${big}`);
  result.warm.push({ root: rootWarm, folder: folderWarm });
  if (r < 3) result.long.push(await longList(page, `${base}/libraries/${lib}`));
  await ctx.close();
}
await browser.close();

const sum = (rows, k) => ({ p50: pct(rows.map((x) => x[k]).filter((x) => x != null), 0.5), max: pct(rows.map((x) => x[k]).filter((x) => x != null), 1) });
const summary = {};
for (const [name, rows] of [['root_cold', result.runs.map((x) => x.root)], ['folder_cold', result.runs.map((x) => x.folder)],
  ['root_warm', result.warm.map((x) => x.root)], ['folder_warm', result.warm.map((x) => x.folder)]]) {
  summary[name] = Object.fromEntries(['firstCard', 'firstPage', 'cards', 'domContentLoaded', 'jsTransferBytes', 'jsDecodedBytes',
    'apiRequests', 'coverRequests', 'longTaskMs', 'longTasks', 'heap', 'domNodes'].map((k) => [k, sum(rows, k)]));
}
writeFileSync(out, JSON.stringify({ summary, result }, null, 1));
console.log(JSON.stringify(summary, null, 1));
console.log(JSON.stringify(result.long.map(({ marks, ...rest }) => rest)));
