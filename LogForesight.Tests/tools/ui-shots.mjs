// UI 截圖與版面量測工具（docs/FEEDBACK-69-PLAN.md A-0）。
//
// 以無頭 Edge 加 Node 內建 WebSocket 走 Chrome DevTools Protocol，零第三方相依。
// 逐情境 × 寬度 × 字級截整頁圖，並量測六項「0 才通過」的版面指標：
//   hscroll  頁面水平捲軸
//   overflow 元素超出所屬卡片或表格格子右緣（中間有宣告捲動或裁切的容器不算）
//   target   可點擊元素小於 24×24（行內文字連結依 WCAG 2.5.8 例外不算）
//   console  console error、未攔截例外、失敗或 4xx/5xx 的網路請求
//   stuck    頁面停在骨架列（載入中）或錯誤狀態
//   assert   情境步驟中的斷言失敗
//
// 用法：
//   node ui-shots.mjs --base http://localhost:5099 --scenarios scenarios.json --out <目錄>
//     [--fixture <資料集目錄>] [--matrix full|widths|quick] [--only 情境名稱子字串] [--edge <msedge.exe>] [--no-fail]
// 情境檔格式見 ui-scenarios.json；--fixture 另讀資料集產生的 ui-fixture-scenarios.json（路徑含相對今天的日期，不進版控）。
// 矩陣：full＝五個寬度＋375／1280 的小、大字級；widths＝五個寬度、字級中；quick＝1280 一格。輸出 <out>/summary.json 與 <out>/<情境>/<寬>-<字級>[-print].png。
// 任一量測項非零時結束代碼為 1（--no-fail 時一律 0）。

import { spawn } from 'node:child_process';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const args = parseArgs(process.argv.slice(2));
const base = (args.base || 'http://localhost:5041').replace(/\/$/, '');
const outDir = args.out || join(tmpdir(), 'lf-ui-shots');
const edgePath = args.edge || 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe';
const fixtureScenarios = args.fixture
    ? JSON.parse(readFileSync(join(args.fixture, 'ui-fixture-scenarios.json'), 'utf8')).scenarios
        // 停用與已合併的主機本來就打不開（詳情 API 回 404、畫面顯示錯誤態），不是版面情境
        .filter(s => !['host.disabled', 'host.tomb'].includes(s.Key))
        .map(s => ({ name: s.Key, role: s.Role || 'ui-admin', path: s.Path }))
    : [];
// 情境檔的 path 可寫成 "fixture:<資料集情境鍵>"，執行時換成資料集裡的實際網址（日期相對今天，不寫死在情境檔）
const fixturePaths = new Map(fixtureScenarios.map(s => [s.name, s.path]));
const scenarios = [...JSON.parse(readFileSync(args.scenarios || new URL('./ui-scenarios.json', import.meta.url), 'utf8')), ...fixtureScenarios]
    .filter(s => !args.only || s.name.includes(args.only))
    .filter(s => !s.path.startsWith('fixture:') || fixturePaths.has(s.path.slice(8)))
    .map(s => (s.path.startsWith('fixture:') ? { ...s, path: fixturePaths.get(s.path.slice(8)) } : s));

const FULL_MATRIX = [
    ...[375, 768, 1280, 1920, 2560].map(width => ({ width, scale: 'medium' })),
    ...[375, 1280].flatMap(width => ['small', 'large'].map(scale => ({ width, scale })))
];
const WIDTHS_MATRIX = [375, 768, 1280, 1920, 2560].map(width => ({ width, scale: 'medium' }));
const QUICK_MATRIX = [{ width: 1280, scale: 'medium' }];
const VIEWPORT_HEIGHT = 900;
const MAX_SHOT_HEIGHT = 16000;
const METRICS = ['hscroll', 'overflow', 'target', 'console', 'stuck', 'assert'];

const sleep = ms => new Promise(r => setTimeout(r, ms));

function parseArgs(list) {
    const out = {};
    for (let i = 0; i < list.length; i++) {
        if (!list[i].startsWith('--')) continue;
        const key = list[i].slice(2);
        const next = list[i + 1];
        if (next === undefined || next.startsWith('--')) out[key] = true;
        else { out[key] = next; i++; }
    }
    return out;
}

// ── CDP 連線 ───────────────────────────────────────────────────────────────

async function launchBrowser() {
    const profile = mkdtempSync(join(tmpdir(), 'lf-edge-'));
    const port = 9300 + Math.floor(Math.random() * 500);
    const proc = spawn(edgePath, ['--headless=new', `--remote-debugging-port=${port}`, `--user-data-dir=${profile}`,
        '--no-first-run', '--no-default-browser-check', '--disable-gpu', '--disable-extensions', '--disable-component-extensions-with-background-pages', '--hide-scrollbars', '--force-color-profile=srgb',
        '--font-render-hinting=none', 'about:blank'], { stdio: 'ignore' });
    let page;
    for (let i = 0; i < 100 && !page; i++) {
        try { page = (await (await fetch(`http://127.0.0.1:${port}/json/list`)).json()).find(t => t.type === 'page'); } catch { /* 還沒起來 */ }
        if (!page) await sleep(200);
    }
    if (!page) throw new Error('Edge 沒有在 20 秒內開出可連線的分頁，確認 --edge 路徑');
    const ws = new WebSocket(page.webSocketDebuggerUrl);
    await new Promise((resolve, reject) => { ws.addEventListener('open', resolve, { once: true }); ws.addEventListener('error', reject, { once: true }); });

    let seq = 0;
    const pending = new Map();
    const listeners = new Set();
    ws.addEventListener('message', e => {
        const msg = JSON.parse(e.data);
        if (msg.id && pending.has(msg.id)) {
            const { resolve, reject } = pending.get(msg.id);
            pending.delete(msg.id);
            msg.error ? reject(new Error(`${msg.error.message}`)) : resolve(msg.result);
        } else if (msg.method) {
            for (const fn of listeners) fn(msg);
        }
    });
    const send = (method, params = {}) => new Promise((resolve, reject) => {
        const id = ++seq;
        pending.set(id, { resolve, reject });
        ws.send(JSON.stringify({ id, method, params }));
    });
    const close = () => {
        try { ws.close(); } catch { /* 已關 */ }
        proc.kill();
        setTimeout(() => { try { rmSync(profile, { recursive: true, force: true }); } catch { /* Edge 還鎖著檔，留在暫存區 */ } }, 1500);
    };
    await Promise.all(['Page.enable', 'Runtime.enable', 'Network.enable', 'Log.enable'].map(m => send(m)));
    return { send, on: fn => listeners.add(fn), off: fn => listeners.delete(fn), close };
}

async function evaluate(browser, expression) {
    const r = await browser.send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
    if (r.exceptionDetails) throw new Error(`頁面內執行失敗：${r.exceptionDetails.exception?.description || r.exceptionDetails.text}`);
    return r.result.value;
}

/**
 * 等網路靜止：連續 500ms 沒有「剛發出的」請求（上限 20 秒），再等字型；有 canvas 時多等圖表動畫。
 * 在途超過 2 秒的請求不算（頁面的長輪詢、徽章輪詢會一直掛著，等它們就永遠等不到靜止）。
 */
async function settle(browser, inflight) {
    const deadline = Date.now() + 20000;
    let quietSince = Date.now();
    while (Date.now() < deadline) {
        const now = Date.now();
        const fresh = [...inflight.values()].some(started => now - started < 2000);
        if (fresh) quietSince = now;
        else if (now - quietSince >= 500) break;
        await sleep(100);
    }
    await evaluate(browser, 'document.fonts ? document.fonts.ready.then(() => true) : true');
    if (await evaluate(browser, '!!document.querySelector("canvas")')) await sleep(1200);
    else await sleep(200);
}

// ── 頁面內量測（序列化後在頁面執行）──────────────────────────────────────

function pageMeasure() {
    const path = el => {
        const parts = [];
        for (let n = el; n && n.nodeType === 1 && parts.length < 4; n = n.parentElement) {
            let s = n.tagName.toLowerCase();
            if (n.id) { s += `#${n.id}`; parts.unshift(s); break; }
            const cls = [...n.classList].filter(c => !c.startsWith('d-') && !/^(m|p)[tbsexy]?-\d$/.test(c)).slice(0, 2);
            if (cls.length) s += `.${cls.join('.')}`;
            parts.unshift(s);
        }
        return parts.join(' > ');
    };
    const visible = el => {
        const r = el.getBoundingClientRect();
        if (r.width === 0 && r.height === 0) return false;
        const cs = getComputedStyle(el);
        return cs.visibility !== 'hidden' && cs.display !== 'none' && !el.closest('[hidden], .visually-hidden, .modal:not(.show)');
    };
    const doc = document.documentElement;
    const result = { hscroll: [], overflow: [], target: [], stuck: [] };

    if (doc.scrollWidth > doc.clientWidth + 1) {
        // 一併指出最外層撐出視窗的元素（祖先沒有超出的那一層），修的時候才知道從哪裡下手
        const culprits = [...document.querySelectorAll('body *')].filter(el => {
            if (el.getBoundingClientRect().right <= doc.clientWidth + 1 || !visible(el)) return false;
            for (let n = el.parentElement; n && n !== document.body; n = n.parentElement) {
                if (getComputedStyle(n).overflowX !== 'visible') return false;
                if (n.getBoundingClientRect().right > doc.clientWidth + 1) return false;
            }
            return true;
        }).slice(0, 3).map(el => `${path(el)} 右緣 ${Math.round(el.getBoundingClientRect().right)}`);
        result.hscroll.push(`scrollWidth ${doc.scrollWidth} > clientWidth ${doc.clientWidth}${culprits.length ? '：' + culprits.join('；') : ''}`);
    }

    // 元素超出所屬卡片／表格格子右緣：只看有自己文字或是替換元素的節點，避免父子重複計數
    const leaves = [...document.querySelectorAll('body *')].filter(el =>
        ['IMG', 'SVG', 'svg', 'CANVAS', 'INPUT', 'SELECT', 'TEXTAREA', 'BUTTON'].includes(el.tagName)
        || [...el.childNodes].some(n => n.nodeType === 3 && n.textContent.trim()));
    for (const el of leaves) {
        const box = el.parentElement?.closest('td, th, .lf-card, .card, .modal-content');
        if (!box || !visible(el)) continue;
        let clipped = false;
        for (let n = el.parentElement; n && n !== box; n = n.parentElement) {
            const ox = getComputedStyle(n).overflowX;
            if (ox !== 'visible') { clipped = true; break; }
        }
        if (clipped || getComputedStyle(box).overflowX !== 'visible') continue;
        const r = el.getBoundingClientRect();
        const b = box.getBoundingClientRect();
        if (r.right > b.right + 1) result.overflow.push(`${path(el)} 超出 ${path(box)} ${Math.round(r.right - b.right)}px`);
    }

    const targets = document.querySelectorAll('a[href], button, [role="button"], input:not([type="hidden"]), select, summary, [tabindex="0"]');
    for (const el of targets) {
        if (!visible(el) || el.disabled) continue;
        const cs = getComputedStyle(el);
        if (el.tagName === 'A' && cs.display === 'inline') continue;   // WCAG 2.5.8 行內例外
        // 有可見 <label> 的勾選框／單選鈕：點標籤也會切換，等同擴大的點擊範圍（WCAG 2.5.8 的同等控制例外）
        if (el.tagName === 'INPUT' && /^(checkbox|radio)$/.test(el.type) && [...(el.labels || [])].some(l => visible(l) && l.getBoundingClientRect().height >= 20)) continue;
        const r = el.getBoundingClientRect();
        if (r.width < 23.5 || r.height < 23.5) result.target.push(`${path(el)} ${Math.round(r.width)}×${Math.round(r.height)}`);
    }

    for (const el of document.querySelectorAll('.lf-skeleton, .lf-error')) {
        if (visible(el)) result.stuck.push(path(el));
    }

    const sheets = [...document.styleSheets].reduce((sum, s) => { try { return sum + [...s.cssRules].reduce((a, r) => a + r.cssText.length, 0); } catch { return sum; } }, 0);
    return { ...result, observe: { domNodes: document.getElementsByTagName('*').length, cssChars: sheets } };
}

function assertInPage(spec) {
    const els = [...document.querySelectorAll(spec.sel)];
    const shown = els.filter(el => {
        const r = el.getBoundingClientRect();
        return (r.width > 0 || r.height > 0) && getComputedStyle(el).visibility !== 'hidden';
    });
    if (spec.visible === true && shown.length === 0) return `預期可見：${spec.sel}`;
    if (spec.visible === false && shown.length > 0) return `預期不可見：${spec.sel}（${shown.length} 個可見）`;
    if (spec.count !== undefined && els.length !== spec.count) return `預期 ${spec.count} 個 ${spec.sel}，實際 ${els.length}`;
    if (spec.text !== undefined && !els.some(el => el.textContent.includes(spec.text))) return `${spec.sel} 不含文字「${spec.text}」`;
    if (spec.notText !== undefined && els.some(el => el.textContent.includes(spec.notText))) return `${spec.sel} 不應含文字「${spec.notText}」`;
    if (spec.style) {
        for (const [prop, value] of Object.entries(spec.style)) {
            const el = els[0];
            const actual = el ? getComputedStyle(el).getPropertyValue(prop).trim() : '(無元素)';
            if (actual !== value) return `${spec.sel} 的 ${prop} 預期 ${value}，實際 ${actual}`;
        }
    }
    return null;
}

// ── 步驟 ─────────────────────────────────────────────────────────────────

async function runSteps(browser, steps, inflight, width) {
    const failures = [];
    let marked = null;
    for (const step of steps || []) {
        if (step.click) {
            // scrollIntoView 只在元素不在視窗內時才捲，避免「量捲動是否跳動」的情境被工具自己捲動
            const ok = await evaluate(browser, `(() => { const el = document.querySelector(${JSON.stringify(step.click)}); if (!el) return false; el.scrollIntoView({ block: 'nearest' }); el.click(); return true; })()`);
            if (!ok) failures.push(`找不到可點選的元素：${step.click}`);
            await settle(browser, inflight);
        } else if (step.type) {
            const [sel, text] = step.type;
            const ok = await evaluate(browser, `(() => { const el = document.querySelector(${JSON.stringify(sel)}); if (!el) return false; el.focus(); el.value = ${JSON.stringify(text)}; el.dispatchEvent(new Event('input', { bubbles: true })); el.dispatchEvent(new Event('change', { bubbles: true })); return true; })()`);
            if (!ok) failures.push(`找不到輸入欄：${sel}`);
            await settle(browser, inflight);
        } else if (step.waitFor) {
            const deadline = Date.now() + 10000;
            let found = false;
            while (!found && Date.now() < deadline) {
                found = await evaluate(browser, `!!document.querySelector(${JSON.stringify(step.waitFor)})`);
                if (!found) await sleep(150);
            }
            if (!found) failures.push(`等不到元素：${step.waitFor}`);
        } else if (step.assert || step.assertAt) {
            // assertAt：依寬度給不同斷言（{ "767": {...}, "768": {...} }），沒列到的寬度不驗
            const spec = step.assert || step.assertAt[String(width)];
            if (spec) {
                const msg = await evaluate(browser, `(${assertInPage.toString()})(${JSON.stringify(spec)})`);
                if (msg) failures.push(`${width}px：${msg}`);
            }
        } else if (step.scroll !== undefined) {
            await evaluate(browser, `window.scrollTo(0, ${Number(step.scroll)})`);
        } else if (step.markScroll) {
            marked = await evaluate(browser, 'window.scrollY');
        } else if (step.scrollWithin !== undefined) {
            // 與 markScroll 記下的位置比較：就地重繪後捲動位置不得跳走
            const now = await evaluate(browser, 'window.scrollY');
            if (marked === null) failures.push('scrollWithin 之前沒有 markScroll');
            else if (Math.abs(now - marked) > Number(step.scrollWithin)) failures.push(`捲動位置跳動 ${Math.round(now - marked)}px（上限 ${step.scrollWithin}px）`);
        } else if (step.wait) {
            await sleep(Number(step.wait));
        }
    }
    return failures;
}

// ── 主流程 ───────────────────────────────────────────────────────────────

async function login(browser, account, inflight) {
    await browser.send('Page.navigate', { url: `${base}/login` });
    await settle(browser, inflight);
    const ok = await evaluate(browser, `(async () => {
        const res = await fetch(${JSON.stringify(base + '/api/auth/login')}, { method: 'POST', credentials: 'same-origin',
            headers: { 'Content-Type': 'application/json', 'X-Requested-By': 'LogForesight' }, body: JSON.stringify({ account: ${JSON.stringify(account)}, password: 'ui-fixture' }) });
        return res.ok;
    })()`);
    if (!ok) throw new Error(`以 ${account} 登入失敗：確認站台讀的是 UI 資料集、Auth:Provider=Stub`);
}

async function runScenario(browser, scenario, cell, state) {
    const { width, scale } = cell;
    const started = Date.now();
    const errors = [];
    const onEvent = msg => {
        if (msg.method === 'Network.loadingFailed') {
            if (!msg.params.canceled) errors.push(`請求失敗 ${msg.params.errorText}：${state.urls.get(msg.params.requestId) || ''}`);
        } else if (msg.method === 'Network.responseReceived') {
            state.urls.set(msg.params.requestId, msg.params.response.url);
            const status = msg.params.response.status;
            const allowed = (scenario.allowStatus || []).includes(status);
            if (status >= 400 && !allowed) errors.push(`HTTP ${status}：${msg.params.response.url.replace(base, '')}`);
        } else if (msg.method === 'Runtime.consoleAPICalled' && msg.params.type === 'error') {
            errors.push(`console.error：${msg.params.args.map(a => a.value ?? a.description ?? '').join(' ').slice(0, 300)}`);
        } else if (msg.method === 'Runtime.exceptionThrown') {
            errors.push(`未攔截例外：${(msg.params.exceptionDetails.exception?.description || msg.params.exceptionDetails.text || '').slice(0, 300)}`);
        } else if (msg.method === 'Log.entryAdded' && msg.params.entry.level === 'error' && msg.params.entry.source !== 'network') {
            errors.push(`瀏覽器錯誤：${msg.params.entry.text.slice(0, 300)}`);
        }
    };
    browser.on(onEvent);
    try {
        await browser.send('Emulation.setDeviceMetricsOverride', { width, height: VIEWPORT_HEIGHT, deviceScaleFactor: 1, mobile: false });
        await browser.send('Emulation.setEmulatedMedia', {
            media: scenario.media === 'print' ? 'print' : '',
            features: [{ name: 'prefers-reduced-motion', value: 'reduce' }]
        });
        await evaluate(browser, `localStorage.setItem('lf.fontScale', ${JSON.stringify(scale)}); true`);
        await browser.send('Page.navigate', { url: `${base}${scenario.path}` });
        await settle(browser, state.inflight);
        const assertFailures = await runSteps(browser, scenario.steps, state.inflight, width);
        // 骨架列還在就多等最多 5 秒：分批載入的頁面在網路靜止後仍可能有最後一段在渲染，
        // 只在真的卡住時才記為 stuck（否則同一頁兩次量測結果會隨時序跳動）
        for (let i = 0; i < 25 && await evaluate(browser, '!!document.querySelector(".lf-skeleton")'); i++) await sleep(200);
        const measured = await evaluate(browser, `(${pageMeasure.toString()})()`);

        const metrics = await browser.send('Page.getLayoutMetrics');
        const height = Math.min(MAX_SHOT_HEIGHT, Math.ceil(metrics.cssContentSize.height));
        const shotWidth = Math.ceil(Math.max(width, metrics.cssContentSize.width));
        const shot = await browser.send('Page.captureScreenshot', {
            format: 'png', captureBeyondViewport: true, clip: { x: 0, y: 0, width: shotWidth, height, scale: 1 }
        });
        const file = join(outDir, safeName(scenario.name), `${width}-${scale}${scenario.media === 'print' ? '-print' : ''}.png`);
        mkdirSync(join(outDir, safeName(scenario.name)), { recursive: true });
        writeFileSync(file, Buffer.from(shot.data, 'base64'));

        return {
            scenario: scenario.name, role: scenario.role, path: scenario.path, width, scale, media: scenario.media || 'screen', file,
            hscroll: measured.hscroll, overflow: measured.overflow, target: measured.target,
            console: [...new Set(errors)], stuck: measured.stuck, assert: assertFailures,
            observe: { ...measured.observe, elapsedMs: Date.now() - started }
        };
    } finally {
        browser.off(onEvent);
    }
}

function safeName(name) {
    return name.replace(/[\\/:*?"<>|\s]+/g, '_');
}

async function main() {
    const matrix = args.matrix === 'quick' ? QUICK_MATRIX : args.matrix === 'widths' ? WIDTHS_MATRIX : FULL_MATRIX;
    mkdirSync(outDir, { recursive: true });
    const results = [];
    const roles = [...new Set(scenarios.map(s => s.role || 'ui-admin'))];
    for (const role of roles) {
        const browser = await launchBrowser();
        const state = { inflight: new Map(), urls: new Map() };
        browser.on(msg => {
            if (msg.method === 'Network.requestWillBeSent') state.inflight.set(msg.params.requestId, Date.now());
            else if (msg.method === 'Network.loadingFinished' || msg.method === 'Network.loadingFailed') state.inflight.delete(msg.params.requestId);
        });
        try {
            await login(browser, role, state.inflight);
            for (const scenario of scenarios.filter(s => (s.role || 'ui-admin') === role)) {
                const cells = scenario.matrix === 'quick' ? QUICK_MATRIX : scenario.widths
                    ? scenario.widths.map(width => ({ width, scale: 'medium' })) : matrix;
                for (const cell of cells) {
                    try {
                        const r = await runScenario(browser, scenario, cell, state);
                        results.push(r);
                        const bad = METRICS.filter(m => r[m].length > 0).map(m => `${m}=${r[m].length}`);
                        console.log(`${bad.length ? '✗' : '✓'} ${scenario.name} ${cell.width}/${cell.scale} ${bad.join(' ')}`);
                    } catch (e) {
                        results.push({ scenario: scenario.name, width: cell.width, scale: cell.scale, hscroll: [], overflow: [], target: [], console: [], stuck: [], assert: [`工具執行失敗：${e.message}`], observe: {} });
                        console.log(`✗ ${scenario.name} ${cell.width}/${cell.scale} 工具執行失敗：${e.message}`);
                    }
                }
            }
        } finally {
            browser.close();
        }
    }

    const totals = Object.fromEntries(METRICS.map(m => [m, results.reduce((sum, r) => sum + r[m].length, 0)]));
    writeFileSync(join(outDir, 'summary.json'), JSON.stringify({ base, generatedAt: new Date().toISOString(), totals, results }, null, 2));
    console.log(`\n合計 ${JSON.stringify(totals)}；詳見 ${join(outDir, 'summary.json')}`);
    const failed = METRICS.some(m => totals[m] > 0);
    process.exit(failed && !args['no-fail'] ? 1 : 0);
}

main().catch(e => { console.error(e); process.exit(2); });
