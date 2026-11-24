/**
 * 排程作業頁（runs.js）：沒有 Maintain（開發維運）時，PRTG 設定、結構同步、回填三個需要 Maintain 的端點
 * 不送請求，三處顯示替代文字；有 Maintain 時照送。切出 loadSchedule 與兩個 refresh 函式、注入替身執行。
 */
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { installDom } from './mini-dom.mjs';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const jsRoot = path.resolve(scriptDir, '..', '..', 'LogForesight.Web', 'wwwroot', 'js');
const dom = installDom();

const src = fs.readFileSync(path.join(jsRoot, 'pages', 'runs.js'), 'utf8').replaceAll('\r\n', '\n');
const extract = decl => {
    const start = src.indexOf(decl);
    assert(start >= 0, `找不到 ${decl}`);
    const open = src.indexOf('{', src.indexOf(')', start));
    let depth = 0;
    for (let i = open; i < src.length; i++) {
        if (src[i] === '{') depth++;
        else if (src[i] === '}' && --depth === 0) return src.slice(start, i + 1);
    }
    throw new Error(`未閉合 ${decl}`);
};
const constLine = name => {
    const m = new RegExp(`^const ${name} = .*;$`, 'm').exec(src);
    // 缺常數時不讓測試在載入階段就掛掉：交給行為斷言（沒有替代文字）去紅
    return m ? m[0] : `const ${name} = undefined;`;
};

function load(capabilities) {
    const spy = { gets: [], moduleState: [] };
    const els = new Map();
    const el = id => { if (!els.has(id)) els.set(id, dom.newElement('span')); return els.get(id); };
    const fakeDocument = {
        ...document,
        getElementById: id => el(id),
        querySelectorAll: () => []
    };
    const stubs = {
        document: fakeDocument,
        api: {
            get: async (url, opts) => {
                spy.gets.push(url);
                if (url === '/api/admin/settings') return { prtgEnabled: true, prtgValueFetchScope: 'x', prtgFetchStrategy: 'conservative', prtgCredentialState: 'ok', prtgUrl: 'http://p', prtgBackfillDays: 7 };
                if (url.endsWith('prtg-structure-sync/status')) return { isRunning: false, lastCompletedAt: null };
                if (url.endsWith('prtg-backfill/status')) return { isRunning: false, completedAt: null };
                if (url === '/api/admin/schedule/options') return {};
                return {};
            }
        },
        getCurrentUser: async () => ({ capabilities }),
        hasCapability: (user, cap) => user.capabilities.includes(cap),
        getAiAvailable: async () => false,
        applyAiScheduleStatus: () => { }, applyScheduleOptions: () => { },
        renderPrtgModuleState: (enabled, scope, strategy, cred) => spy.moduleState.push({ enabled, scope, strategy, cred }),
        renderPrtgSyncSummary: status => { el('prtg-sync-summary').textContent = status.lastCompletedAt ? '已同步' : '尚未同步'; },
        renderPrtgBackfillStatus: status => { el('prtg-backfill-status').textContent = '尚未回填'; },
        pollWhile: () => ({ stop() { } }),
        // 排程狀態（精簡投影）：DevMonitor 的 PRTG 模組狀態由它帶；case 可換成回 null
        refreshScheduleStatus: async () => spy.scheduleStatus ?? null
    };
    const names = Object.keys(stubs);
    const body = `
        let canMaintainSchedule = false, aiAvailable = null, lastAiScheduleStatus = null;
        let prtgFetchStrategy = null, prtgConnectionConfigured = false, prtgSyncTimer = null, prtgBackfillPollTimer = null;
        let prtgBackfillCancelTarget = null;
        ${constLine('NEEDS_MAINTAIN_TEXT')}
        ${extract('async function loadSchedule')}
        ${extract('async function refreshPrtgSyncStatus')}
        ${extract('async function refreshPrtgBackfillStatus')}
        return { loadSchedule, refreshPrtgSyncStatus, refreshPrtgBackfillStatus };`;
    const fns = new Function(...names, body)(...names.map(n => stubs[n]));
    return { ...fns, spy, el };
}

const MAINTAIN_ONLY = ['/api/admin/settings', '/api/admin/settings/prtg-structure-sync/status', '/api/admin/settings/prtg-backfill/status'];
const TEXT = '需要維護權限才能檢視';

const cases = {
    // DevMonitor：載入與輪詢都不送三個端點；沒有精簡投影可用時三處顯示替代文字
    async devMonitorSendsNoMaintainRequestsAndShowsText() {
        const { loadSchedule, refreshPrtgSyncStatus, refreshPrtgBackfillStatus, spy, el } = load(['DevMonitor', 'ViewAll']);
        await loadSchedule();
        await refreshPrtgSyncStatus({ background: true });
        await refreshPrtgBackfillStatus({ background: true });

        for (const url of MAINTAIN_ONLY) assert.equal(spy.gets.includes(url), false, `不該送 ${url}`);
        assert.ok(spy.gets.includes('/api/admin/schedule/options'), 'DevMonitor 可讀的端點照送');
        assert.equal(el('prtg-module-state').textContent, TEXT);
        assert.equal(el('prtg-sync-summary').textContent, TEXT);
        assert.equal(el('prtg-backfill-status').textContent, TEXT);
    },

    // DevMonitor 但排程狀態帶了 PRTG 精簡投影：模組狀態用投影、仍不送 settings
    async devMonitorUsesStatusProjectionForModuleState() {
        const ctx = load(['DevMonitor']);
        ctx.spy.scheduleStatus = { prtgEnabled: true, prtgValueFetchScope: 'all', prtgFetchStrategy: 'conservative', prtgCredentialState: 'ok', prtgBackfillDays: 3 };
        await ctx.loadSchedule();
        assert.equal(ctx.spy.gets.includes('/api/admin/settings'), false);
        assert.equal(ctx.spy.moduleState.length, 1);
        assert.equal(ctx.spy.moduleState[0].enabled, true);
    },

    // Maintain：三個端點照送，不顯示替代文字
    async maintainSendsAllThreeRequests() {
        const { loadSchedule, refreshPrtgSyncStatus, refreshPrtgBackfillStatus, spy, el } = load(['Maintain', 'ViewAll']);
        await loadSchedule();
        await refreshPrtgSyncStatus({ background: true });
        await refreshPrtgBackfillStatus({ background: true });

        for (const url of MAINTAIN_ONLY) assert.ok(spy.gets.includes(url), `應送 ${url}`);
        assert.notEqual(el('prtg-sync-summary').textContent, TEXT);
        assert.notEqual(el('prtg-backfill-status').textContent, TEXT);
        assert.equal(spy.moduleState.length, 1, 'Maintain 以設定端點結果畫模組狀態');
    }
};

const name = process.argv[2];
if (!name) { Object.keys(cases).forEach(key => console.log(key)); process.exit(0); }
if (!cases[name]) { console.error(`未知案例：${name}`); process.exit(2); }
await cases[name]();
console.log(`PASS ${name}`);
