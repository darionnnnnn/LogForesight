// 多人同時操作測試驅動器（docs/archive/FEEDBACK-81-CONCURRENCY-PLAN.md）。
//
// 一個 Chrome 行程內建立多個彼此隔離的瀏覽器工作階段（等同多個無痕視窗，各自獨立 cookie），
// 每個工作階段以不同帳號登入站台、載入真正的頁面，由情境腳本同時操控；每一步記錄時間、使用者、動作、
// 期間送出的 API（方法、路徑、狀態、毫秒、錯誤碼）、提示訊息、彈窗、主控台錯誤與畫面觀察值。
//
// 用法：node multi-user.mjs --base http://localhost:5099 --out <目錄> [--only MU01,MU04] [--chrome <路徑>] [--headed]
// 情境定義在 multi-user-scenarios.mjs。站台需以 Stub 登入（測試資料集）。

import { spawn } from 'node:child_process';
import { mkdirSync, mkdtempSync, rmSync, writeFileSync, appendFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const args = Object.fromEntries(process.argv.slice(2).reduce((acc, v, i, all) => {
    if (v.startsWith('--')) acc.push([v.slice(2), all[i + 1] && !all[i + 1].startsWith('--') ? all[i + 1] : true]);
    return acc;
}, []));
const base = String(args.base || 'http://localhost:5099').replace(/\/$/, '');
const outDir = String(args.out || join(tmpdir(), 'lf-multi-user'));
const chromePath = String(args.chrome || '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome');
const sleep = ms => new Promise(r => setTimeout(r, ms));

// ── CDP 連線 ────────────────────────────────────────────────────────────────

class Cdp {
    constructor(ws) {
        this.ws = ws; this.nextId = 1; this.pending = new Map(); this.listeners = new Map();
        ws.addEventListener('message', ev => {
            const msg = JSON.parse(ev.data);
            if (msg.id) {
                const p = this.pending.get(msg.id);
                if (!p) return;
                this.pending.delete(msg.id);
                msg.error ? p.reject(new Error(`${p.method}: ${msg.error.message}`)) : p.resolve(msg.result);
            } else if (msg.method) {
                for (const fn of this.listeners.get(msg.sessionId || '') || []) fn(msg.method, msg.params);
            }
        });
    }
    send(method, params = {}, sessionId) {
        const id = this.nextId++;
        return new Promise((resolve, reject) => {
            this.pending.set(id, { resolve, reject, method });
            this.ws.send(JSON.stringify({ id, method, params, ...(sessionId ? { sessionId } : {}) }));
        });
    }
    on(sessionId, fn) {
        const list = this.listeners.get(sessionId) || [];
        list.push(fn); this.listeners.set(sessionId, list);
    }
}

async function launch() {
    const profile = mkdtempSync(join(tmpdir(), 'lf-mu-'));
    const port = 9400 + Math.floor(Math.random() * 400);
    const flags = [`--remote-debugging-port=${port}`, `--user-data-dir=${profile}`, '--no-first-run', '--no-default-browser-check',
        '--disable-gpu', '--window-size=1440,900'];
    if (!args.headed) flags.unshift('--headless=new');
    const proc = spawn(chromePath, flags, { stdio: 'ignore' });
    let version = null;
    for (let i = 0; i < 60 && !version; i++) {
        await sleep(250);
        try { version = await (await fetch(`http://127.0.0.1:${port}/json/version`)).json(); } catch { /* 尚未就緒 */ }
    }
    if (!version) { proc.kill(); throw new Error('Chrome 沒有在時限內開啟偵錯埠'); }
    const ws = new WebSocket(version.webSocketDebuggerUrl);
    await new Promise((res, rej) => { ws.addEventListener('open', res); ws.addEventListener('error', rej); });
    return { cdp: new Cdp(ws), close: () => { try { ws.close(); } catch { /* 已關 */ } proc.kill(); try { rmSync(profile, { recursive: true, force: true, maxRetries: 10, retryDelay: 200 }); } catch { /* Chrome 還在收尾，暫存目錄留給系統清 */ } } };
}

// ── 頁面內輔助（每個文件載入時注入）──────────────────────────────────────────

const IN_PAGE = `(() => {
  if (window.__mu) return;
  const toasts = [];
  const seen = new WeakSet();
  const scan = () => document.querySelectorAll('.toast').forEach(t => {
    if (seen.has(t)) return; seen.add(t);
    const text = (t.innerText || '').replace(/\\s+/g, ' ').trim(); if (text) toasts.push(text);
  });
  const watch = () => document.documentElement ? new MutationObserver(scan).observe(document.documentElement, { childList: true, subtree: true }) : setTimeout(watch, 20);
  watch();
  const vis = el => !!el && el.offsetParent !== null;
  const norm = s => (s || '').replace(/\\s+/g, ' ').trim();
  const scopeOf = sel => (typeof sel === 'string' ? document.querySelector(sel) : sel) || document;
  const m = {
    drainToasts() { scan(); return toasts.splice(0); },
    modal() { return document.querySelector('.modal.show'); },
    modalText() { const d = m.modal(); return d ? norm(d.innerText).slice(0, 400) : null; },
    find(text, scope, tags = 'button, a, [role=button], summary, label') {
      const list = [...scopeOf(scope).querySelectorAll(tags)].filter(vis);
      return list.find(e => norm(e.innerText) === text) || list.find(e => norm(e.innerText).startsWith(text)) || null;
    },
    click(text, scope) {
      const el = m.find(text, scope); if (!el) throw new Error('找不到可點的元素：' + text);
      if (el.disabled) throw new Error('元素停用中：' + text);
      el.click(); return true;
    },
    row(text, scope) { return [...scopeOf(scope).querySelectorAll('tbody tr')].filter(vis).find(r => norm(r.innerText).includes(text)) || null; },
    clickInRow(rowText, text) { const r = m.row(rowText); if (!r) throw new Error('找不到列：' + rowText); return m.click(text, r); },
    checkRow(rowText) { const r = m.row(rowText); if (!r) throw new Error('找不到列：' + rowText); const c = r.querySelector('input[type=checkbox]'); if (!c.checked) c.click(); return true; },
    setValue(el, value) {
      const proto = el.tagName === 'TEXTAREA' ? HTMLTextAreaElement.prototype : el.tagName === 'SELECT' ? HTMLSelectElement.prototype : HTMLInputElement.prototype;
      Object.getOwnPropertyDescriptor(proto, 'value').set.call(el, value);
      el.dispatchEvent(new Event('input', { bubbles: true })); el.dispatchEvent(new Event('change', { bubbles: true }));
    },
    selectIn(scope, valueOrText) {
      const sel = [...scopeOf(scope).querySelectorAll('select')].filter(vis)[0]; if (!sel) throw new Error('找不到下拉');
      const opt = [...sel.options].find(o => o.value === valueOrText || norm(o.text).includes(valueOrText)); if (!opt) throw new Error('沒有選項：' + valueOrText);
      m.setValue(sel, opt.value); return true;
    },
    /** 選結論：有可見下拉就用下拉；回覆彈窗的結論是一排按鈕（.lf-handling__chips），依按鈕文字或 aria-label 點選 */
    pickStatus(scope, text) {
      const sel = [...scopeOf(scope).querySelectorAll('select')].filter(vis)[0];
      if (sel && [...sel.options].some(o => o.value === text || norm(o.text).includes(text))) return m.selectIn(scope, text);
      const chips = [...scopeOf(scope).querySelectorAll('.lf-handling__chips button, [role=radiogroup] button, [role=group] button')].filter(vis);
      const chip = chips.find(b => norm(b.innerText) === text) || chips.find(b => norm(b.innerText).includes(text) || (b.getAttribute('aria-label') || '').includes(text));
      if (!chip) throw new Error('找不到結論：' + text);
      chip.click(); return true;
    },
    /** 回覆彈窗的送出鈕：一般結論是「送出」，處理中是「更新說明（不結案）」 */
    submitReply(scope) {
      const el = m.find('送出', scope) || m.find('更新說明', scope);
      if (!el) throw new Error('找不到送出鈕');
      if (el.disabled) throw new Error('元素停用中：' + norm(el.innerText));
      el.click(); return true;
    },
    typeIn(scope, text, selector = 'textarea') {
      const el = [...scopeOf(scope).querySelectorAll(selector)].filter(vis)[0]; if (!el) throw new Error('找不到輸入欄：' + selector);
      el.focus(); m.setValue(el, text); return true;
    },
    text(sel, max = 300) { const el = document.querySelector(sel); return el ? norm(el.innerText).slice(0, max) : null; },
    async waitFor(fn, ms = 8000) { const end = Date.now() + ms; while (Date.now() < end) { try { const v = fn(); if (v) return v; } catch {} await new Promise(r => setTimeout(r, 100)); } throw new Error('等待逾時'); },
    api(method, url, body) {
      return fetch(url, { method, credentials: 'same-origin', headers: { 'X-Requested-By': 'LogForesight', ...(body ? { 'Content-Type': 'application/json' } : {}) },
        body: body ? JSON.stringify(body) : undefined }).then(async r => ({ status: r.status, body: await r.json().catch(() => null) }));
    }
  };
  window.__mu = m;
})();`;

// ── 使用者（一個隔離的瀏覽器工作階段）────────────────────────────────────────

class User {
    constructor(run, account, label) {
        this.run = run; this.account = account; this.label = label || account;
        this.requests = new Map(); this.done = []; this.consoleErrors = []; this.inflight = 0;
    }

    async open() {
        const { cdp } = this.run;
        const { browserContextId } = await cdp.send('Target.createBrowserContext', { disposeOnDetach: true });
        const { targetId } = await cdp.send('Target.createTarget', { url: 'about:blank', browserContextId, width: 1440, height: 900 });
        const { sessionId } = await cdp.send('Target.attachToTarget', { targetId, flatten: true });
        this.sid = sessionId;
        cdp.on(sessionId, (method, p) => this.onEvent(method, p));
        await this.cdp('Page.enable'); await this.cdp('Runtime.enable'); await this.cdp('Network.enable');
        await this.cdp('Page.addScriptToEvaluateOnNewDocument', { source: IN_PAGE });
        await this.cdp('Emulation.setDeviceMetricsOverride', { width: 1440, height: 900, deviceScaleFactor: 1, mobile: false });
    }

    cdp(method, params) { return this.run.cdp.send(method, params, this.sid); }

    onEvent(method, p) {
        if (method === 'Network.requestWillBeSent' && p.request.url.includes('/api/')) {
            this.inflight++;
            this.requests.set(p.requestId, { method: p.request.method, path: p.request.url.replace(base, ''), start: Date.now() });
        } else if (method === 'Network.responseReceived' && this.requests.has(p.requestId)) {
            this.requests.get(p.requestId).status = p.response.status;
        } else if ((method === 'Network.loadingFinished' || method === 'Network.loadingFailed') && this.requests.has(p.requestId)) {
            const r = this.requests.get(p.requestId); this.requests.delete(p.requestId); this.inflight--;
            r.ms = Date.now() - r.start; if (method === 'Network.loadingFailed') r.status = r.status || 'failed';
            const entry = { method: r.method, path: r.path, status: r.status, ms: r.ms };
            this.done.push(entry);
            if (typeof r.status === 'number' && r.status >= 400) {
                this.cdp('Network.getResponseBody', { requestId: p.requestId }).then(b => {
                    try { const j = JSON.parse(b.body); entry.error = `${j.error?.code || ''} ${j.error?.message || ''}`.trim(); } catch { entry.error = (b.body || '').slice(0, 120); }
                }).catch(() => { });
            }
        } else if (method === 'Runtime.exceptionThrown') {
            this.consoleErrors.push((p.exceptionDetails.exception?.description || p.exceptionDetails.text || '').slice(0, 200));
        } else if (method === 'Runtime.consoleAPICalled' && p.type === 'error') {
            this.consoleErrors.push(p.args.map(a => a.value ?? a.description ?? '').join(' ').slice(0, 200));
        }
    }

    /**
     * 在這個使用者的頁面內執行情境腳本提供的運算式（經偵錯協定的 Runtime.evaluate）。
     * 內容只來自本工具與 multi-user-scenarios.mjs，對象是測試站台；不接受外部輸入。
     */
    async inPage(expression) {
        const r = await this.cdp('Runtime.evaluate', { expression: `(async () => { ${IN_PAGE}\n ${expression} })()`, awaitPromise: true, returnByValue: true });
        if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description?.split('\n')[0] || r.exceptionDetails.text);
        return r.result.value;
    }

    /** 等到沒有進行中的 API 請求並穩定一小段時間 */
    async settle(quiet = 350, max = 15000) {
        const end = Date.now() + max; let calm = Date.now();
        while (Date.now() < end) {
            if (this.inflight > 0) calm = Date.now();
            if (Date.now() - calm >= quiet) return;
            await sleep(50);
        }
    }

    async goto(path) {
        await this.cdp('Page.navigate', { url: base + path });
        // 以文件狀態輪詢判斷載入完成（事件監聽會被前一頁的載入事件誤觸發）
        const end = Date.now() + 20000;
        while (Date.now() < end) {
            await sleep(100);
            try {
                const r = await this.cdp('Runtime.evaluate', { expression: `document.readyState === 'complete' && location.href.startsWith(${JSON.stringify(base)})`, returnByValue: true });
                if (r.result.value === true) break;
            } catch { /* 換頁中 */ }
        }
        await this.settle();
    }

    async login() {
        await this.goto('/login');
        const r = await this.inPage(`return __mu.api('POST', '/api/auth/login', { account: ${JSON.stringify(this.account)}, password: 'multi-user' });`);
        if (r.status !== 200) throw new Error(`以 ${this.account} 登入失敗（${r.status}）`);
        this.userId = r.body.data.userId; this.landing = r.body.data.landingPath;
        return r.body.data;
    }

    /**
     * 記錄一步。fn 內做實際操作；observe 是步驟後在頁面內求值的運算式（回傳要記下的畫面文字）。
     * 失敗不擲出（記 ok=false 與訊息），讓情境繼續並把失敗留在時間軸上。
     */
    async step(name, action, fn, observe) {
        this.done = []; this.consoleErrors = [];
        const t = Date.now() - this.run.t0; const started = Date.now();
        let ok = true, error = null;
        try { await fn(); await this.settle(); } catch (e) { ok = false; error = String(e.message || e).slice(0, 300); }
        await sleep(120);   // 讓錯誤回應的內容抓取完成
        let toast = [], dialog = null, seen = null;
        try { toast = await this.inPage('return __mu.drainToasts();'); dialog = await this.inPage('return __mu.modalText();'); } catch { /* 頁面換頁中 */ }
        if (observe) { try { seen = await this.inPage(observe); } catch (e) { seen = `（觀察失敗：${String(e.message).slice(0, 80)}）`; } }
        const rec = {
            t, user: this.label, step: name, action, ok, ms: Date.now() - started, error,
            api: this.done.filter(a => !/\/api\/(run-activity|handlers\/me\/badge|work-orders\/unread-count|health\/|auth\/me|me\/setup-guide|admin\/setup\/status|settings\/display|ai\/status)/.test(a.path) || (typeof a.status === 'number' && a.status >= 400)),
            bg: this.done.length, toast, dialog, console: this.consoleErrors.slice(), observe: seen
        };
        this.run.record(rec);
        return rec;
    }
}

// ── 一次執行 ────────────────────────────────────────────────────────────────

class Run {
    constructor(cdp, scenario) { this.cdp = cdp; this.scenario = scenario; this.records = []; this.t0 = Date.now(); this.users = []; }

    async user(account, label) {
        const u = new User(this, account, label); await u.open(); await u.login(); this.users.push(u); return u;
    }

    record(rec) {
        this.records.push(rec);
        appendFileSync(join(outDir, `${this.scenario.id}.jsonl`), JSON.stringify(rec) + '\n');
    }

    /** 情境層級的註記（最終狀態查核、說明），不屬於任何使用者 */
    note(step, observe) { this.record({ t: Date.now() - this.t0, user: '（查核）', step, action: '', ok: true, ms: 0, api: [], toast: [], dialog: null, console: [], observe }); }

    /** 同時觸發：各自的函式在同一輪事件迴圈啟動，回傳各自的紀錄 */
    together(...thunks) { return Promise.all(thunks.map(f => f())); }
}

function cell(v) { return String(v ?? '').replace(/\|/g, '｜').replace(/\n/g, ' '); }

function writeTimeline(run) {
    const s = run.scenario;
    const rows = run.records.slice().sort((a, b) => a.t - b.t);
    const lines = [`# ${s.id} ${s.title}`, '', `- 參與者：${run.users.map(u => u.label).join('、')}`, `- 同時發生的事：${s.what}`,
        `- 執行：${new Date(run.t0).toLocaleString('zh-TW', { hour12: false })}；步驟 ${rows.length}；失敗 ${rows.filter(r => !r.ok).length}；API 錯誤回應 ${rows.reduce((n, r) => n + r.api.filter(a => a.status >= 400).length, 0)}；主控台錯誤 ${rows.reduce((n, r) => n + r.console.length, 0)}`,
        '', '| 秒 | 誰 | 步驟 | 動作 | 結果 | 請求（方法 路徑 → 狀態，毫秒） | 提示／彈窗 | 畫面觀察 |', '|---|---|---|---|---|---|---|---|'];
    for (const r of rows) {
        const api = r.api.map(a => `${a.method} ${a.path.split('?')[0]} → ${a.status}（${a.ms}）${a.error ? `「${a.error}」` : ''}`).join('<br>');
        const msg = [...r.toast.map(t => `提示：${t}`), r.dialog ? `彈窗：${r.dialog.slice(0, 160)}` : '', ...r.console.map(c => `主控台：${c}`)].filter(Boolean).join('<br>');
        const result = r.ok ? `成功（${r.ms}ms）` : `**失敗**：${r.error}`;
        const seen = typeof r.observe === 'string' ? r.observe : r.observe == null ? '' : JSON.stringify(r.observe);
        lines.push(`| ${(r.t / 1000).toFixed(2)} | ${cell(r.user)} | ${cell(r.step)} | ${cell(r.action)} | ${cell(result)} | ${cell(api)} | ${cell(msg)} | ${cell(seen).slice(0, 500)} |`);
    }
    writeFileSync(join(outDir, `${s.id}.md`), lines.join('\n') + '\n');
}

async function main() {
    const { scenarios } = await import('./multi-user-scenarios.mjs');
    const only = args.only ? String(args.only).split(',') : null;
    mkdirSync(outDir, { recursive: true });
    const summary = [];
    for (const scenario of scenarios.filter(s => !only || only.includes(s.id))) {
        rmSync(join(outDir, `${scenario.id}.jsonl`), { force: true });
        const browser = await launch();
        const run = new Run(browser.cdp, scenario);
        let crashed = null;
        try { await scenario.run(run, { sleep, base }); } catch (e) { crashed = String(e.stack || e).slice(0, 600); run.note('情境中斷', crashed); }
        writeTimeline(run);
        browser.close();
        const fails = run.records.filter(r => !r.ok).length;
        const apiErr = run.records.reduce((n, r) => n + r.api.filter(a => a.status >= 400).length, 0);
        const slow = Math.max(0, ...run.records.flatMap(r => r.api.map(a => a.ms || 0)));
        summary.push({ id: scenario.id, title: scenario.title, steps: run.records.length, fails, apiErr, slowestMs: slow, crashed: !!crashed });
        console.log(`${scenario.id} ${scenario.title}：步驟 ${run.records.length}、失敗 ${fails}、API 錯誤 ${apiErr}、最慢 ${slow}ms${crashed ? '、情境中斷' : ''}`);
    }
    writeFileSync(join(outDir, 'summary.json'), JSON.stringify(summary, null, 2));
}

main().catch(e => { console.error(e); process.exit(1); });
