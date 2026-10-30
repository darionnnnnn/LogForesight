/**
 * 第 59 輪 G4 小項的行為測試：PRTG 維護頁範圍版本守門、詳情頁報告各自失敗、主機篩選共用、
 * 報表起訖日與範圍 chip、說明書首次載入不捲動。
 *
 * 從 production 原始碼抽出目標函式原文執行，只替換 api 與畫面邊界；
 * 由 FrontendMiscBehaviorTests 以 `node misc-pages.behavior.mjs <案例名>` 逐案執行。
 */

import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const jsRoot = path.join(scriptDir, '..', '..', 'LogForesight.Web', 'wwwroot', 'js');

function sourceOf(page) {
    return fs.readFileSync(path.join(jsRoot, 'pages', page), 'utf8').replaceAll('\r\n', '\n');
}

function extractFunction(source, declaration) {
    const start = source.indexOf(declaration);
    assert(start >= 0, `找不到 production 函式：${declaration}`);
    const open = source.indexOf('{', source.indexOf(')', start));
    let depth = 0;
    for (let i = open; i < source.length; i++) {
        if (source[i] === '{') depth++;
        else if (source[i] === '}' && --depth === 0) return source.slice(start, i + 1);
    }
    throw new Error(`production 函式未閉合：${declaration}`);
}

/** prelude 提供外部變數；declarations 為要抽出的函式宣告；returns 為回傳物件的運算式 */
function build(page, declarations, prelude, returns, args = {}) {
    const source = sourceOf(page);
    const names = Object.keys(args);
    const body = declarations.map((d) => extractFunction(source, d)).join('\n');
    const factory = new Function(...names, `${prelude}\n${body}\nreturn ${returns};`);
    return factory(...names.map((n) => args[n]));
}

const cases = {
    // ---- prtg-admin：requireScopeRevision ----
    requireScopeRevision未載入時擋下並提示() {
        const toasts = [];
        const inst = build('prtg-admin.js', ['function requireScopeRevision('],
            'let scopeRevision = null;',
            '{ requireScopeRevision, set: (v) => { scopeRevision = v; } }',
            { toast: (m, t) => toasts.push([m, t]) });
        assert.equal(inst.requireScopeRevision(), false);
        assert.equal(toasts.length, 1);
        assert.match(toasts[0][0], /取數範圍尚未載入完成/);
    },
    requireScopeRevision已載入時放行() {
        const toasts = [];
        const inst = build('prtg-admin.js', ['function requireScopeRevision('],
            'let scopeRevision = null;',
            '{ requireScopeRevision, set: (v) => { scopeRevision = v; } }',
            { toast: (m, t) => toasts.push([m, t]) });
        inst.set(0);   // 0 也是有效版本，不能被當成未載入
        assert.equal(inst.requireScopeRevision(), true);
        inst.set(7);
        assert.equal(inst.requireScopeRevision(), true);
        assert.equal(toasts.length, 0);
    },

    // ---- record-detail：loadReports 各自失敗 ----
    async loadReports一份失敗另一份仍顯示() {
        const set = {};
        const api = {
            get: (url) => url.includes('weekly_checkup')
                ? Promise.resolve({ id: 'checkup' })
                : Promise.reject(new Error('risk 暫時失敗'))
        };
        const inst = build('record-detail.js', ['async function loadReports('], `
            const hostId = 1;
            const date = '2026-10-01';
            const currentDetail = { hasReport: true, riskReportPending: false, weeklyCheckup: { hasReport: true } };
            const riskReportPendingNotice = {};
            const riskReportCard = { setReport: (r) => { set.risk = r; } };
            const checkupReportCard = { setReport: (r) => { set.checkup = r; } };
        `, '{ loadReports }', { api, set });
        await inst.loadReports();   // 不得外擲
        assert.equal(set.risk, null);
        assert.deepEqual(set.checkup, { id: 'checkup' });
    },
    async loadReports兩份都失敗也不外擲() {
        const set = {};
        const api = { get: () => Promise.reject(new Error('boom')) };
        const inst = build('record-detail.js', ['async function loadReports('], `
            const hostId = 1;
            const date = '2026-10-01';
            const currentDetail = { hasReport: true, riskReportPending: false, weeklyCheckup: { hasReport: true } };
            const riskReportPendingNotice = {};
            const riskReportCard = { setReport: (r) => { set.risk = r; } };
            const checkupReportCard = { setReport: (r) => { set.checkup = r; } };
        `, '{ loadReports }', { api, set });
        await inst.loadReports();
        assert.equal(set.risk, null);
        assert.equal(set.checkup, null);
    },

    // ---- hosts：篩選條件共用 ----
    hosts只有prtgMap篩選也算有篩選且帶進查詢() {
        const inst = build('hosts.js',
            ['function activeFilterEntries(', 'function hasActiveFilter(', 'function currentFilterParams('], `
            const searchInput = { value: '' };
            const sentinelFilter = { value: '' };
            const statusMode = '';
            const groupFilter = new Set();
            const osFilter = '';
            let prtgMapFilter = '';
            const sort = { key: 'name', dir: 'asc' };
        `, '{ hasActiveFilter, currentFilterParams, setMap: (v) => { prtgMapFilter = v; } }');
        assert.equal(inst.hasActiveFilter(), false);
        inst.setMap('conflict');
        assert.equal(inst.hasActiveFilter(), true);
        assert.equal(inst.currentFilterParams().get('prtgMap'), 'conflict');
        // 排序參數不算篩選
        inst.setMap('');
        assert.equal(inst.currentFilterParams().get('sort'), 'name');
        assert.equal(inst.hasActiveFilter(), false);
    },

    // ---- reports：起訖日與範圍 ----
    async reports起日晚於迄日時擋下() {
        const toasts = [];
        let loaded = 0;
        const values = { 'report-from': '2026-10-10', 'report-to': '2026-10-01' };
        const inst = build('reports.js', ['async function load('], `
            let currentScope = 'all';
            const document = { getElementById: (id) => ({ value: values[id] ?? '' }) };
            const loadReport = async () => { loaded++; return true; };
            const syncScopeChips = () => {};
        `, '{ load }', {
            values,
            toast: (m, t) => toasts.push([m, t]),
            renderLoading: () => {},
            guardLoad: (_c, fn) => fn()
        });
        await inst.load();
        assert.equal(loaded, 0, '起日晚於迄日不應發出查詢');
        assert.equal(toasts.length, 1);
        assert.match(toasts[0][0], /起日不可晚於迄日/);
    },
    async reports載入失敗時範圍chip與currentScope不變() {
        const synced = [];
        const api = { get: () => Promise.reject(new Error('boom')) };
        const inst = build('reports.js', ['async function load(', 'async function loadReport('], `
            let currentScope = 'all';
            let requestedScope = 'unresolved';   // 使用者剛點了「未結案」chip
            const reportGuard = createSeqGuard();
            const document = { getElementById: (id) => ({ value: id === 'report-compare' ? 'prev' : '2026-10-01' }) };
            const syncScopeChips = () => synced.push(currentScope);
        `, '{ load, scope: () => currentScope }', {
            api,
            synced,
            createSeqGuard: () => { let n = 0; return { next: () => ++n, isCurrent: (s) => s === n }; },
            getDisplaySettings: async () => ({}),
            toast: () => {},
            renderLoading: () => {},
            // 與 production guardLoad 同形：失敗吞掉並回傳 undefined
            guardLoad: async (_c, fn) => { try { return await fn(); } catch { return undefined; } }
        });
        await inst.load();
        assert.equal(inst.scope(), 'all', '失敗時 currentScope 不得被換成想切的範圍');
        assert.deepEqual(synced, [], '失敗時 chip 不得更新');
    },
    async reports載入成功時才以回應範圍更新chip() {
        const synced = [];
        const urls = [];
        const api = { get: async (url) => { urls.push(url); return { handlingScope: 'unresolved', from: 'a', to: 'b' }; } };
        const inst = build('reports.js', ['async function load(', 'async function loadReport('], `
            let currentData = null;
            let retentionDays = 90;
            let currentScope = 'all';
            let requestedScope = 'unresolved';   // 使用者剛點了「未結案」chip
            let visibleDayRisk = null;
            const SCOPE_LABEL = { unresolved: '未結案' };
            const reportGuard = createSeqGuard();
            const document = { getElementById: (id) => ({ value: id === 'report-compare' ? 'prev' : '2026-10-01', set textContent(v) {} }) };
            const location = { search: '', pathname: '/reports' };
            const history = { replaceState() {} };
            const updateYoyOptionLabel = () => {};
            const renderKpi = () => {};
            const renderVisibleCharts = () => {};
            const syncScopeChips = () => synced.push(currentScope);
        `, '{ load, scope: () => currentScope }', {
            api,
            synced,
            createSeqGuard: () => { let n = 0; return { next: () => ++n, isCurrent: (s) => s === n }; },
            getDisplaySettings: async () => ({}),
            toast: () => {},
            renderLoading: () => {},
            guardLoad: async (_c, fn) => { try { return await fn(); } catch { return undefined; } }
        });
        await inst.load();
        assert.match(urls[0], /handlingScope=unresolved/, '查詢要帶使用者點選的範圍');
        assert.equal(inst.scope(), 'unresolved');
        assert.deepEqual(synced, ['unresolved']);
    },

    // ---- help-manual：首次載入不捲動 ----
    說明書首次載入不捲動而一般切章會捲動() {
        const scrolls = { n: 0 };
        const inst = build('help-manual.js', ['function selectChapter('], `
            const chapterById = new Map([['a', { id: 'a', title: 'A' }]]);
            let currentId = null;
            const history = { replaceState() {} };
            const document = {
                querySelectorAll: () => [],
                getElementById: () => ({ set textContent(v) {}, scrollIntoView() { scrolls.n++; } })
            };
            const renderChapterContent = () => {};
            const renderRelated = () => {};
        `, '{ selectChapter }', { scrolls });
        inst.selectChapter('a', { scroll: false });
        assert.equal(scrolls.n, 0, '首次載入不得捲動');
        inst.selectChapter('a');
        assert.equal(scrolls.n, 1, '點擊／hashchange 切章要捲到內文');
    },
};

const name = process.argv[2];
const fn = cases[name];
assert(fn, `未知案例：${name}`);
await fn();
console.log(`PASS ${name}`);
