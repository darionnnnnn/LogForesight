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
import { fileURLToPath, pathToFileURL } from 'node:url';

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

/** 設定頁記憶體卡：production 的 renderMemoryHealth 與格式化函式，畫面邊界與 renderTable 為替身 */
function makeMemoryCard() {
    const hosts = {};
    const tables = [];
    const node = () => ({
        className: '', textContent: '', children: [],
        append(...c) { this.children.push(...c); },
        replaceChildren(...c) { this.children = c; }
    });
    for (const id of ['health-memory', 'health-memory-caches']) hosts[id] = node();
    const documentStub = {
        getElementById: (id) => hosts[id],
        createElement: () => node()
    };
    const inst = build('settings.js', ['function formatMegabytes(', 'function formatHitRate(', 'function renderMemoryHealth('], '',
        '{ renderMemoryHealth }', {
            document: documentStub,
            formatNumber: (n) => n.toLocaleString('en-US'),
            renderTable: (_c, opts) => { tables.push(opts); }
        });
    return { inst, hosts, tables };
}

const tick = () => new Promise((r) => setTimeout(r, 0));

/** 主機頁徽章：以 production 的 prtgHintBadge／prtgUnmatchedLink／badge 搭簡易節點 */
function makeHostBadge(canViewAll) {
    const documentStub = { createElement: (tag) => ({ tag, className: '', textContent: '', title: '' }) };
    return build('hosts.js', ['function prtgHintBadge(', 'function prtgUnmatchedLink(', 'function badge('],
        `let canViewAll = ${canViewAll};`,
        '{ prtgHintBadge }', { appUrl: (p) => `/app${p}`, document: documentStub });
}

/** PRTG 維護頁未對應清單：production 的 refreshUnmappedHosts／pin 讀取／清網址／跳轉高亮，api、網址、計時器為替身 */
function makeUnmatched({ search = '', hash = '', items = [], total = 0, fail = null }) {
    const log = [];
    const urls = [];
    const replaced = [];
    const timers = [];
    const row = { classes: new Set() };
    row.classList = { add: (c) => row.classes.add(c), remove: (c) => row.classes.delete(c) };
    const state = { fail, response: { items, total } };
    const api = {
        get: async (url) => {
            urls.push(url);
            if (state.fail) throw state.fail;
            return state.response;
        }
    };
    const documentStub = {
        getElementById: (id) => {
            assert.equal(id, 'prtg-unmatched-table');
            return { querySelector: (selector) => { assert.equal(selector, 'tbody tr'); return row; } };
        }
    };
    const inst = build('prtg-admin.js',
        ['function readUnmatchedFocusHostId(', 'function clearUnmatchedFocusParams(', 'function revealPinnedUnmatchedHost(', 'async function refreshUnmappedHosts('],
        `let unmappedPage = 1;
        let unmappedPageSize = 20;
        let jumpToUnmatchedSection = () => log.push('jump');
        const whenInitialLoadSettled = (action) => action();
        let unmatchedPinHostId = readUnmatchedFocusHostId();
        let unmatchedPinApplied = false;
        const renderUnmappedHostsLoading = () => {};
        const renderUnmappedHosts = () => log.push('render');
        const renderUnmappedHostsPagination = () => {};
        const renderUnmappedHostsError = (message) => log.push('error:' + message);`,
        '{ refreshUnmappedHosts }',
        {
            api, log,
            document: documentStub,
            location: { search, hash, pathname: '/app/admin/prtg' },
            history: { state: null, replaceState: (_s, _t, url) => replaced.push(url) },
            setTimeout: (fn, ms) => { timers.push({ fn, ms }); return timers.length; }
        });
    return {
        inst, log, urls, replaced, timers, row,
        setFail: (error, response) => { state.fail = error; if (response) state.response = response; }
    };
}

/** 可手動決定每個請求何時回應的 api 替身 */
function makePendingApi() {
    const pending = [];
    return { pending, get: (url) => new Promise((resolve, reject) => pending.push({ url, resolve, reject })) };
}

function makeHostPanel() {
    const api = makePendingApi();
    const state = { highlights: [], clears: 0, rendered: 0 };
    const node = () => ({ className: '', textContent: '', appendChild() {}, replaceChildren() {} });
    // 頁面層的高亮歸屬（openPanels／highlightOwner／lastOpenedPanel）一併帶入，兩個面板共用同一份
    const inst = build('host-detail.js', ['function occurrenceDetailPanel(', 'function claimHighlight(', 'function releaseHighlight('], `
        const hostId = 1;
        const currentDays = 30;
        const openPanels = new Set();
        let highlightOwner = null;
        let lastOpenedPanel = null;
    `, '{ occurrenceDetailPanel }', {
        api,
        document: { createElement: node },
        // 面板用到的共用元素工廠（core/elements.js）以最小替身帶入；行為測試只看展開、快取與高亮
        roleText: () => node(),
        statusNote: () => node(),
        metaLine: () => node(),
        renderSpinner: () => {},
        renderOccurrencePanel: () => { state.rendered++; },
        highlightTimeline: (dates) => { state.highlights.push(dates); },
        clearTimelineHighlight: () => { state.clears++; }
    });
    const panel = inst.occurrenceDetailPanel({ source: 'System', eventId: 7 });
    const second = inst.occurrenceDetailPanel({ source: 'System', eventId: 8 });
    return { panel, second, pending: api.pending, state };
}

function makeRuns() {
    const api = makePendingApi();
    const rendered = { summary: [], errors: [], list: [] };
    const guard = () => { let n = 0; return { next: () => ++n, isCurrent: (s) => s === n }; };
    const el = { classList: { add() {}, remove() {}, toggle() {} } };
    const failures = { n: 0 };
    const inst = build('runs.js', ['async function load(', 'async function loadSummary(', 'function hideErrorsNotice(',
        'async function loadErrors(', 'function reloadErrors('], `
        let currentDays = 14;
        let currentSummaryPage = 1;
        let summaryMaxDays = null;
        let errorsMinLevel = 'error';
        const summaryGuard = createSeqGuard();
        const loadGuard = createSeqGuard();
        const errorsGuard = createSeqGuard();
        const document = { getElementById: () => el };
    `, '{ load, loadSummary, loadErrors, reloadErrors, setLevel(v) { errorsMinLevel = v; } }', {
        api, el,
        guardLoad: async (_c, fn) => { try { return await fn(); } catch { failures.n++; return undefined; } },
        createSeqGuard: guard,
        renderLoading: () => {},
        renderError: () => {},
        renderLegend: () => {},
        renderSummary: (d) => rendered.summary.push(d.tag),
        renderErrors: (d) => rendered.errors.push(d.tag),
        renderRunList: (d) => rendered.list.push(d.tag)
    });
    return {
        inst, api, rendered, failures,
        /** 從第 firstPending 個請求起連續三個（summary／errors／list）一起回應 */
        settleLoad(firstPending, tag) {
            for (let i = 0; i < 3; i++) api.pending[firstPending + i].resolve({ tag, groups: [], failedWithoutErrorRuns: 0 }); // errors 端點回 { groups, failedWithoutErrorRuns }，renderErrors 收完整物件
        }
    };
}

function makePrtgNode(tag) {
    return { tagName: tag, className: '', textContent: '', title: '', children: [], appendChild(c) { this.children.push(c); return c; } };
}

/** runs.js 的 PRTG 欄：抽出 renderPrtgBadge／renderPrtgCell 原文，只替換 document 與徽章對照表 */
function makePrtgCell() {
    return build('runs.js', ['function renderPrtgBadge(', 'function renderPrtgCell('], `
        const PRTG_OUTCOME_META = {
            disabled: { text: '未啟用', bg: 'bg-secondary' },
            success: { text: '成功', bg: 'bg-success' }
        };
    `, '{ renderPrtgCell }', {
        document: { createElement: makePrtgNode, createTextNode: (text) => ({ textContent: text, children: [] }) }
    });
}

/** runs.js 的 PRTG 卡模組狀態：文字函式用 production 的 core/prtg-scope-labels.js */
async function makePrtgModuleState() {
    // prtg-scope-labels.js 經 paths.js 讀 window.LF_BASE
    globalThis.window ??= { LF_BASE: '' };
    const labels = await import(pathToFileURL(path.join(jsRoot, 'core', 'prtg-scope-labels.js')).href);
    const els = {
        'prtg-module-state': makeClassEl(),
        'prtg-disabled-hint': makeClassEl(),
        'prtg-credential-hint': makeClassEl()
    };
    const inst = build('runs.js', ['function renderPrtgModuleState('], `
        let prtgModuleEnabled = null;
        let prtgFetchStrategy = null;
    `, '{ renderPrtgModuleState, state: () => ({ enabled: prtgModuleEnabled }) }', {
        document: { getElementById: (id) => els[id] ?? null },
        prtgModuleStateText: labels.prtgModuleStateText
    });
    return { inst, els };
}

function makeClassEl() {
    const classes = new Set(['d-none']);
    return { classes, textContent: '', disabled: false, classList: {
        add: (c) => classes.add(c), remove: (c) => classes.delete(c),
        toggle: (c, on) => { if (on) classes.add(c); else classes.delete(c); } } };
}

function makeErrorsNotice() {
    const box = makeClassEl();
    const text = makeClassEl();
    const els = { 'run-errors-notice': box, 'run-errors-notice-text': text };
    const inst = build('runs.js', ['function renderErrors(', 'function renderErrorsNotice('], '', '{ renderErrors }', {
        document: { getElementById: (id) => els[id] ?? null },
        formatNumber: (n) => String(n),
        renderErrorsTable: () => {}
    });
    return { inst, box, text };
}

function makeExport({ capabilities, fail = null, blob = { size: 1 } }) {
    const wrap = makeClassEl();
    const button = makeClassEl();
    const els = { 'runs-export-wrap': wrap, 'runs-export-btn': button };
    const urls = [], opts = [], saved = [], toasts = [], busy = { restored: 0 };
    const inst = build('runs.js', ['async function initExportButton(', 'async function hasExportData(', 'async function exportDiagnostics(', 'function applyDaysSelection('], `
        let currentDays = 14;
        let summaryMaxDays = null;
    `, '{ initExportButton, exportDiagnostics, applyDaysSelection, setMaxDays(v) { summaryMaxDays = v; } }', {
        document: { getElementById: (id) => els[id] ?? null },
        getCurrentUser: async () => ({ capabilities }),
        hasCapability: (u, c) => u.capabilities.includes(c),
        api: {
            download: async (url, o) => {
                urls.push(url); opts.push(o);
                if (fail) throw fail;
                return { blob, filename: null };
            }
        },
        saveBlobAsFile: (blob, filename) => saved.push({ blob, filename }),
        toast: (message, type) => toasts.push({ message, type }),
        withBusy: (b) => { b.disabled = true; return () => { b.disabled = false; busy.restored++; }; }
    });
    return { inst, wrap, button, urls, opts, saved, toasts, busy };
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

    async reports起訖日顛倒時清掉待生效範圍() {
        const inst = build('reports.js', ['async function load('], `
            let currentScope = 'all';
            let requestedScope = 'unresolved';   // 使用者剛點了「未結案」chip
            const document = { getElementById: (id) => ({ value: id === 'report-from' ? '2026-10-10' : '2026-10-01' }) };
            const loadReport = async () => true;
            const syncScopeChips = () => {};
        `, '{ load, requested: () => requestedScope }', {
            toast: () => {}, renderLoading: () => {}, guardLoad: (_c, fn) => fn()
        });
        await inst.load();
        assert.equal(inst.requested(), null, '提前 return 沒發出查詢，不得留下沒生效的範圍');
    },
    async reports載入失敗後清掉待生效範圍() {
        const api = { get: () => Promise.reject(new Error('boom')) };
        const inst = build('reports.js', ['async function load(', 'async function loadReport('], `
            let currentScope = 'all';
            let requestedScope = 'unresolved';
            const reportGuard = createSeqGuard();
            const document = { getElementById: (id) => ({ value: id === 'report-compare' ? 'prev' : '2026-10-01' }) };
            const syncScopeChips = () => {};
        `, '{ load, requested: () => requestedScope, scope: () => currentScope }', {
            api,
            createSeqGuard: () => { let n = 0; return { next: () => ++n, isCurrent: (s) => s === n }; },
            getDisplaySettings: async () => ({}),
            toast: () => {}, renderLoading: () => {},
            guardLoad: async (_c, fn) => { try { return await fn(); } catch { return undefined; } }
        });
        await inst.load();
        assert.equal(inst.requested(), null, '失敗後不得保留沒生效的範圍，否則下次按查詢會用沒亮起的範圍');
        assert.equal(inst.scope(), 'all');
    },
    async reports已亮起的chip可取消待生效範圍() {
        const calls = { loads: 0 };
        const inst = build('reports.js', ['function onScopeChipClick('], `
            let requestedScope = 'unresolved';
            const load = () => { calls.loads++; };
        `, '{ onScopeChipClick, requested: () => requestedScope }', { calls });
        const active = { classList: { contains: () => true }, dataset: { scope: 'all' } };
        inst.onScopeChipClick({ target: { closest: () => active } });
        assert.equal(inst.requested(), null, '點已亮起的 chip 要撤掉待生效範圍');
        assert.equal(calls.loads, 1, '撤掉後以目前範圍重查（在途的舊請求由序號守門丟棄）');
    },
    async reports已亮起的chip無待生效範圍時不重查() {
        const calls = { loads: 0 };
        const inst = build('reports.js', ['function onScopeChipClick('], `
            let requestedScope = null;
            const load = () => { calls.loads++; };
        `, '{ onScopeChipClick, requested: () => requestedScope }', { calls });
        const active = { classList: { contains: () => true }, dataset: { scope: 'all' } };
        inst.onScopeChipClick({ target: { closest: () => active } });
        assert.equal(calls.loads, 0, '沒有待生效範圍時點已亮起的 chip 不重查');
        const other = { classList: { contains: () => false }, dataset: { scope: 'unresolved' } };
        inst.onScopeChipClick({ target: { closest: () => other } });
        assert.equal(inst.requested(), 'unresolved');
        assert.equal(calls.loads, 1, '點未亮起的 chip 要重查');
    },

    // ---- host-detail：展開明細與時間軸高亮 ----
    async hostDetail收合後回應不呼叫highlightTimeline() {
        const h = makeHostPanel();
        h.panel.setOpen(true);
        h.panel.setOpen(false);   // 回應未到前使用者就收合
        h.pending[0].resolve({ occurrences: [{ date: '2026-10-01' }] });
        await tick();
        assert.equal(h.state.highlights.length, 0, '收合後抵達的回應不得替時間軸上色');
        assert.equal(h.state.rendered, 1, '內容仍要渲染（下次展開可直接用）');
        assert.equal(h.state.clears, 0, '尚未高亮過就沒有東西要清（高亮由持有者釋放）');
    },
    async hostDetail仍展開時照常高亮() {
        const h = makeHostPanel();
        h.panel.setOpen(true);
        h.pending[0].resolve({ occurrences: [{ date: '2026-10-01' }, { date: '2026-10-02' }] });
        await tick();
        assert.deepEqual(h.state.highlights, [['2026-10-01', '2026-10-02']]);
    },
    async hostDetail回應未到前收合再展開不重打也不重畫() {
        const h = makeHostPanel();
        h.panel.setOpen(true);
        h.panel.setOpen(false);
        h.panel.setOpen(true);
        assert.equal(h.pending.length, 1, '進行中的請求要沿用');
        h.pending[0].resolve({ occurrences: [{ date: '2026-10-01' }] });
        await tick();
        assert.equal(h.state.rendered, 1, '只渲染一次');
        assert.deepEqual(h.state.highlights, [['2026-10-01']], '仍展開所以要高亮一次');
    },
    async hostDetail先展開列晚到的回應不蓋掉最後展開的列() {
        const h = makeHostPanel();
        h.panel.setOpen(true);    // A
        h.second.setOpen(true);   // B（最後展開）
        h.pending[1].resolve({ occurrences: [{ date: '2026-10-02' }] });   // B 先回
        await tick();
        h.pending[0].resolve({ occurrences: [{ date: '2026-10-01' }] });   // A 後回
        await tick();
        assert.deepEqual(h.state.highlights, [['2026-10-02']], '時間軸停在最後展開的 B');
        h.panel.setOpen(false);   // 收合 A 不得清掉 B 的高亮
        assert.equal(h.state.clears, 0);
        h.second.setOpen(false);
        assert.equal(h.state.clears, 1);
    },
    async hostDetail收合再展開沿用快取並高亮() {
        const h = makeHostPanel();
        h.panel.setOpen(true);
        h.pending[0].resolve({ occurrences: [{ date: '2026-10-01' }] });
        await tick();
        h.panel.setOpen(false);
        h.panel.setOpen(true);
        assert.equal(h.pending.length, 1, '已取得的明細不重打 API');
        assert.equal(h.state.highlights.length, 2, '再展開要重新高亮');
    },

    // ---- runs：天數切換與總表的序號守門 ----
    async runs天數連點舊回應晚到不蓋新結果() {
        const r = makeRuns();
        const first = r.inst.load();
        const second = r.inst.load();
        r.settleLoad(3, 'new');
        await second;
        r.settleLoad(0, 'old');
        await first;
        assert.deepEqual(r.rendered.summary, ['new'], '舊載入的回應不得蓋掉新結果');
        assert.deepEqual(r.rendered.errors, ['new']);
    },
    async runs舊載入失敗不外擲() {
        const r = makeRuns();
        const first = r.inst.load();
        const second = r.inst.load();
        r.settleLoad(3, 'new');
        await second;
        r.api.pending[0].reject(new Error('boom'));
        await first;   // 外擲會讓 guardLoad 把新結果換成失敗態
        assert.deepEqual(r.rendered.summary, ['new']);
    },
    async runs最新載入失敗要外擲() {
        const r = makeRuns();
        const first = r.inst.load();
        r.api.pending[0].reject(new Error('boom'));
        await assert.rejects(first, /boom/);
    },
    async runs慢的舊loadSummary不覆寫新載入的總表() {
        const r = makeRuns();
        const slow = r.inst.loadSummary();   // 先換頁的慢請求（pending[0]）
        const full = r.inst.load();          // 接著換天數的整批載入（pending[1..3]）
        r.settleLoad(1, 'load');
        await full;
        r.api.pending[0].resolve({ tag: 'stale-summary' });
        await slow;
        assert.deepEqual(r.rendered.summary, ['load'], '慢的舊 loadSummary 不得覆寫剛換天數的總表');
    },
    async runs載入期間換總表頁時總表交給較新的請求() {
        const r = makeRuns();
        const full = r.inst.load();            // pending[0..2]
        const paged = r.inst.loadSummary();    // pending[3]
        r.settleLoad(0, 'load');
        await full;
        r.api.pending[3].resolve({ tag: 'paged' });
        await paged;
        assert.deepEqual(r.rendered.summary, ['paged'], '總表只該是較新的換頁結果');
        assert.deepEqual(r.rendered.errors, ['load'], 'errors 與清單仍照常渲染');
    },

    // ---- runs：異常彙總換等級（批次D）----
    async runs整頁載入帶目前選的等級() {
        const r = makeRuns();
        r.inst.setLevel('warn');
        const p = r.inst.load();
        const errorsUrl = r.api.pending.find((x) => x.url.startsWith('/api/runs/errors')).url;
        assert.match(errorsUrl, /minLevel=warn/);
        r.settleLoad(0, 'x');
        await p;
    },
    async runs換等級只重取異常彙總一塊() {
        const r = makeRuns();
        const p = r.inst.load();
        r.settleLoad(0, 'first');
        await p;
        const before = r.api.pending.length;
        r.inst.setLevel('warn');
        const q = r.inst.reloadErrors();
        assert.equal(r.api.pending.length, before + 1, '只多一個請求');
        const last = r.api.pending[before];
        assert.match(last.url, /^\/api\/runs\/errors\?days=14&minLevel=warn$/);
        last.resolve({ tag: 'warn', groups: [], failedWithoutErrorRuns: 0 });
        await q;
        assert.deepEqual(r.rendered.errors, ['first', 'warn']);
        assert.deepEqual(r.rendered.summary, ['first'], '總表不重繪');
        assert.deepEqual(r.rendered.list, ['first'], '執行紀錄不重繪');
    },
    async runs換等級舊回應晚到不覆寫新結果() {
        const r = makeRuns();
        const a = r.inst.reloadErrors();
        r.inst.setLevel('warn');
        const b = r.inst.reloadErrors();
        r.api.pending[1].resolve({ tag: 'warn', groups: [] });
        await b;
        r.api.pending[0].resolve({ tag: 'error-old', groups: [] });
        await a;
        assert.deepEqual(r.rendered.errors, ['warn']);
    },
    async runs整頁載入晚到不覆寫較新的等級結果() {
        const r = makeRuns();
        const full = r.inst.load();            // pending[0..2]，帶 error
        r.inst.setLevel('warn');
        const re = r.inst.reloadErrors();      // pending[3]
        r.api.pending[3].resolve({ tag: 'warn', groups: [] });
        await re;
        r.settleLoad(0, 'old-load');
        await full;
        assert.deepEqual(r.rendered.errors, ['warn'], '整頁載入的舊異常彙總不得蓋掉較新的等級結果');
        assert.deepEqual(r.rendered.summary, ['old-load'], '總表仍照常渲染');
    },
    async runs換等級失敗走失敗態且不影響其他區塊() {
        const r = makeRuns();
        const p = r.inst.load();
        r.settleLoad(0, 'first');
        await p;
        const q = r.inst.reloadErrors();
        r.api.pending[3].reject(new Error('boom'));
        await q;
        assert.equal(r.failures.n, 1, '失敗交給 guardLoad');
        assert.deepEqual(r.rendered.summary, ['first']);
        assert.deepEqual(r.rendered.errors, ['first']);
    },

    // ---- runs：無錯誤紀錄趟次提示（D-3）----
    runs提示N為0不顯示() {
        const t = makeErrorsNotice();
        t.inst.renderErrors({ groups: [], failedWithoutErrorRuns: 0 });
        assert(t.box.classes.has('d-none'), 'N=0 不佔版面');
    },
    runs提示N大於0顯示且帶趟數() {
        const t = makeErrorsNotice();
        t.inst.renderErrors({ groups: [], failedWithoutErrorRuns: 3 });
        assert(!t.box.classes.has('d-none'));
        assert.equal(t.text.textContent, '另有 3 趟中斷或失敗，但沒有錯誤紀錄。');
        t.inst.renderErrors({ groups: [], failedWithoutErrorRuns: 0 });
        assert(t.box.classes.has('d-none'), '之後 N=0 要再藏起來');
    },

    // ---- runs：匯出分析資料（D-1）----
    async runs有ViewAll才顯示匯出鈕() {
        const without = makeExport({ capabilities: ['DevMonitor'] });
        await without.inst.initExportButton();
        assert(without.wrap.classes.has('d-none'), '沒有 ViewAll 要維持隱藏');
        const withAll = makeExport({ capabilities: ['ViewAll'] });
        await withAll.inst.initExportButton();
        assert(!withAll.wrap.classes.has('d-none'));
    },
    async runs匯出帶目前天數並存檔() {
        const t = makeExport({ capabilities: ['ViewAll'] });
        await t.inst.exportDiagnostics();
        assert.deepEqual(t.urls, ['/api/runs/export?days=14']);
        assert.deepEqual(t.opts[0], { timeoutMs: 300000, silent: true });
        assert.equal(t.saved[0].filename, 'run-diagnostics-14d.json', '沒有伺服器檔名時用備用檔名');
        assert.equal(t.toasts[0].type, 'success');
        assert(!t.button.disabled, '完成後按鈕復原');
    },
    async runs異常彙總缺affectedHosts欄位不拋例外() {
        const inst = build('runs.js', ['function hostsOf('], '', '{ hostsOf }');
        assert.deepEqual(inst.hostsOf({}), []);
        assert.deepEqual(inst.hostsOf({ affectedHosts: null }), []);
        assert.deepEqual(inst.hostsOf({ affectedHosts: ['A'] }), ['A']);
    },
    async runs等級值只認warn其餘為error() {
        const inst = build('runs.js', ['function normalizeMinLevel('], '', '{ normalizeMinLevel }');
        assert.equal(inst.normalizeMinLevel('warn'), 'warn');
        assert.equal(inst.normalizeMinLevel('error'), 'error');
        assert.equal(inst.normalizeMinLevel(undefined), 'error');
        assert.equal(inst.normalizeMinLevel('WARN'), 'error');
    },
    async runs匯出沒有資料時提示不同() {
        const empty = makeExport({ capabilities: ['ViewAll'], blob: { text: async () => '{"meta":{"hasData":false}}' } });
        await empty.inst.exportDiagnostics();
        assert.match(empty.toasts[0].message, /沒有執行紀錄/);
        const full = makeExport({ capabilities: ['ViewAll'], blob: { text: async () => '{"meta":{"hasData":true}}' } });
        await full.inst.exportDiagnostics();
        assert.doesNotMatch(full.toasts[0].message, /沒有執行紀錄/);
        const broken = makeExport({ capabilities: ['ViewAll'], blob: { text: async () => 'not json' } });
        await broken.inst.exportDiagnostics();
        assert.equal(broken.toasts[0].type, 'success', '解析失敗不影響已完成的下載');
    },
    async runs切全部時匯出帶後端最大天數() {
        const t = makeExport({ capabilities: ['ViewAll'] });
        assert.equal(t.inst.applyDaysSelection('all'), false, '尚未收到最大天數時不動作');
        t.inst.setMaxDays(90);
        assert.equal(t.inst.applyDaysSelection('all'), true);
        await t.inst.exportDiagnostics();
        assert.deepEqual(t.urls, ['/api/runs/export?days=90']);
        assert.equal(t.saved[0].filename, 'run-diagnostics-90d.json');
    },
    async runs匯出失敗有提示且按鈕復原() {
        const t = makeExport({ capabilities: ['ViewAll'], fail: new Error('資料量太大，請縮小天數') });
        await t.inst.exportDiagnostics();
        assert.equal(t.saved.length, 0);
        assert.equal(t.toasts.length, 1);
        assert.equal(t.toasts[0].type, 'danger');
        assert.match(t.toasts[0].message, /縮小天數/);
        assert(!t.button.disabled, '失敗後按鈕復原');
        assert.equal(t.busy.restored, 1);
    },

    // ---- runs：PRTG 早退原因與憑證無法解密的卡片（第 63-2 輪 A-3）----
    runs清單PRTG未啟用有原因時徽章旁顯示文字() {
        const { renderPrtgCell } = makePrtgCell();
        const note = '憑證無法解密，請到 PRTG 維護頁重新輸入';
        const cell = renderPrtgCell('disabled', null, null, null, null, note);
        assert.equal(cell.children.length, 2, '徽章與原因文字並排');
        const [badge, reason] = cell.children;
        assert.equal(badge.textContent, '未啟用');
        assert.equal(badge.title, note, '原因同時放在徽章的滑鼠提示');
        assert.equal(reason.textContent, note);
        assert.equal(reason.className, 'lf-text-meta', '原因走次要文字角色，不再用 small／text-muted');
    },
    runs清單PRTG未啟用沒有原因時只有徽章() {
        const { renderPrtgCell } = makePrtgCell();
        for (const note of [undefined, null, '']) {
            const cell = renderPrtgCell('disabled', null, null, null, null, note);
            assert.equal(cell.textContent, '未啟用');
            assert.equal(cell.children.length, 0, `舊紀錄沒有原因（${JSON.stringify(note)}）時維持原本只有徽章`);
        }
    },
    runs清單PRTG原因一律走textContent不當HTML() {
        const { renderPrtgCell } = makePrtgCell();
        const hostile = '<img src=x onerror=alert(1)>';
        const cell = renderPrtgCell('disabled', null, null, null, null, hostile);
        assert.equal(cell.children[1].textContent, hostile);
        assert.equal(cell.children[1].innerHTML, undefined, '不得指派 innerHTML');
    },
    runs清單PRTG有成果時不顯示趟次原因() {
        const { renderPrtgCell } = makePrtgCell();
        const cell = renderPrtgCell('success', 5, 0, 2, 'aggressive', '不該出現的原因');
        assert(!cell.children.some((c) => c.textContent === '不該出現的原因'));
    },
    async runs卡片憑證無法解密時顯示已啟用但憑證無法解密並露出連結提示() {
        const t = await makePrtgModuleState();
        t.inst.renderPrtgModuleState(true, 'triggered', 'aggressive', 'undecryptable');
        assert.equal(t.els['prtg-module-state'].textContent, '已啟用（憑證無法解密）');
        assert(!t.els['prtg-credential-hint'].classes.has('d-none'), '憑證提示要顯示');
        assert(t.els['prtg-disabled-hint'].classes.has('d-none'), '已啟用就不再提示去啟用');
        assert.equal(t.inst.state().enabled, true);
    },
    async runs卡片憑證正常或未啟用時不顯示憑證提示() {
        const t = await makePrtgModuleState();
        t.inst.renderPrtgModuleState(true, 'triggered', 'aggressive', 'ok');
        assert.match(t.els['prtg-module-state'].textContent, /^已啟用（數值取數對象/);
        assert(t.els['prtg-credential-hint'].classes.has('d-none'));
        // 沒開的功能不提醒憑證：未啟用時即使狀態是 undecryptable 也只說未啟用
        t.inst.renderPrtgModuleState(false, 'triggered', 'aggressive', 'undecryptable');
        assert.equal(t.els['prtg-module-state'].textContent, '未啟用');
        assert(t.els['prtg-credential-hint'].classes.has('d-none'));
        assert(!t.els['prtg-disabled-hint'].classes.has('d-none'));
        // 舊版後端沒有這個欄位（undefined／null）時退回原本的文字
        t.inst.renderPrtgModuleState(true, 'triggered', 'aggressive');
        assert.match(t.els['prtg-module-state'].textContent, /^已啟用（數值取數對象/);
    },

    // ---- 第 63-2 輪批次D：主機頁「無 PRTG 對應」徽章連結 ----
    hosts徽章有ViewAll時無PRTG對應是連結() {
        const inst = makeHostBadge(true);
        const el = inst.prtgHintBadge({ hostId: 7, prtgHint: 'no-map' });
        assert.equal(el.tag, 'a');
        assert.equal(el.href, '/app/admin/prtg?focus=unmatched&hostId=7#mirror');
        assert.equal(el.title, '前往 PRTG 維護頁的未對應主機清單');
        assert.match(el.className, /\blf-touch-36\b/);
        assert.match(el.className, /\blf-badge\b/);
        assert.equal(el.textContent, '無 PRTG 對應');
    },
    hosts徽章沒有ViewAll時無PRTG對應維持span() {
        const inst = makeHostBadge(false);
        const el = inst.prtgHintBadge({ hostId: 7, prtgHint: 'no-map' });
        assert.equal(el.tag, 'span');
        assert.equal(el.href, undefined);
        assert.equal(el.textContent, '無 PRTG 對應');
        assert.doesNotMatch(el.className, /lf-touch-36/);
    },
    hosts徽章其他prtgHint不因ViewAll變連結() {
        const inst = makeHostBadge(true);
        for (const hint of ['down', 'up', 'unknown']) {
            const el = inst.prtgHintBadge({ hostId: 7, prtgHint: hint });
            assert.equal(el.tag, 'span', hint);
        }
        assert.equal(inst.prtgHintBadge({ hostId: 7, prtgHint: 'ok' }), null);
    },
    hosts載入時依ViewAll能力決定徽章連結() {
        const source = sourceOf('hosts.js');
        assert(source.includes("canViewAll = hasCapability(currentUser, 'ViewAll');"), 'load() 要用目前使用者的 ViewAll 設定 canViewAll');
        assert(source.includes('getCurrentUser()'), 'load() 要走 getCurrentUser 快取');
    },

    // ---- 第 63-2 輪批次D：PRTG 維護頁未對應清單定位 ----
    async prtg未對應帶參數載入用pin跳轉高亮並清網址() {
        const t = makeUnmatched({ search: '?focus=unmatched&hostId=5', hash: '#mirror', items: [{ hostId: 5 }, { hostId: 2 }, { hostId: 9 }], total: 3 });
        await t.inst.refreshUnmappedHosts(1);
        assert.equal(t.urls.length, 1);
        assert(t.urls[0].includes('pinHostId=5'), t.urls[0]);
        assert.deepEqual(t.log, ['render', 'jump']);
        assert.equal(t.row.classes.has('lf-focus-flash'), true);
        assert.deepEqual(t.timers.map(x => x.ms), [1500]);
        t.timers[0].fn();
        assert.equal(t.row.classes.has('lf-focus-flash'), false);
        assert.deepEqual(t.replaced, ['/app/admin/prtg#mirror']);
        // 再載入一次（重新整理清單）仍帶 pin 維持整份排序，但不再跳轉、不再清網址
        await t.inst.refreshUnmappedHosts(1);
        assert(t.urls[1].includes('pinHostId=5'), t.urls[1]);
        assert.equal(t.replaced.length, 1);
        assert.equal(t.log.filter(x => x === 'jump').length, 1);
    },
    prtg未對應跳轉等初次載入全部結束才做() {
        const inst = build('prtg-admin.js', ['function whenInitialLoadSettled(', 'function markInitialLoadSettled('],
            'let initialLoadSettled = false; let afterInitialLoad = null;',
            '{ whenInitialLoadSettled, markInitialLoadSettled }');
        const log = [];
        inst.whenInitialLoadSettled(() => log.push('reveal'));
        assert.deepEqual(log, [], '其他區塊還在載入時不捲動');
        inst.markInitialLoadSettled();
        assert.deepEqual(log, ['reveal']);
        inst.whenInitialLoadSettled(() => log.push('later'));
        assert.deepEqual(log, ['reveal', 'later'], '載入結束後的呼叫立即執行');
    },
    async 過期告示的連結與聯絡人依權限() {
        const render = async (freshness, capabilities) => {
            let bar = null;
            const node = (tag) => ({ tag, className: '', textContent: '', children: [],
                setAttribute() {}, append(...n) { this.children.push(...n); }, appendChild(n) { this.children.push(n); } });
            const { loadHealthBanner } = build('../core/layout.js',
                ['function freshnessReason(', 'async function loadHealthBanner('], '', '{ loadHealthBanner }', {
                    api: { get: async () => freshness },
                    hasCapability: (_, c) => capabilities.includes(c),
                    formatDateTime: (v) => v,
                    appUrl: (p) => p,
                    registerAlert: (_, b) => { bar = b; },
                    document: { createElement: node }
                });
            await loadHealthBanner({});
            return bar;
        };
        const stale = { stale: true, acked: false, lastSuccessAt: 'x', credentialIssue: true, adminContacts: ['甲', '乙'] };
        const hrefs = (bar) => bar.children.filter((c) => c.tag === 'a').map((c) => c.href);

        const maintainer = await render({ ...stale, adminContacts: [] }, ['Maintain']);
        assert.deepEqual(hrefs(maintainer), ['/runs', '/admin/netiq', '/admin/prtg#connection', '/admin/settings#health']);

        const devMonitor = await render(stale, ['DevMonitor']);
        assert.deepEqual(hrefs(devMonitor), ['/runs'], '進不去的維護頁不給連結');
        assert(devMonitor.children[0].textContent.includes('請聯絡：甲、乙'), devMonitor.children[0].textContent);

        const noCredential = await render({ ...stale, credentialIssue: false, lastRunStatus: 'failed', adminContacts: [] }, ['Maintain']);
        assert.deepEqual(hrefs(noCredential), ['/runs', '/admin/settings#health']);

        assert.equal(await render({ ...stale, acked: true }, ['Maintain']), null);
    },
    排程清單的觸發來源只標範圍縮小的趟次() {
        const { runTriggerText } = build('runs.js', ['function runTriggerText('],
            "const RUN_SCOPE_TEXT = { full: '完整', local: '僅本機', 'netiq-hosts': '指定主機' };", '{ runTriggerText }');
        assert.equal(runTriggerText({ triggerText: '手動', scope: 'full' }), '手動');
        assert.equal(runTriggerText({ triggerText: '排程', scope: null }), '排程');
        assert.equal(runTriggerText({ triggerText: '手動', scope: 'local' }), '手動・僅本機');
        assert.equal(runTriggerText({ triggerText: '手動', scope: 'netiq-hosts' }), '手動・指定主機');
    },
    PRTG憑證欄位提示只在目前模式解不開時改寫() {
        const { undecryptableSecretHint: hint } = build('prtg-admin.js', ['function undecryptableSecretHint('], '', '{ undecryptableSecretHint }');
        const bad = { prtgCredentialState: 'undecryptable', prtgAuthMode: 'passhash' };
        assert(hint(bad, 'passhash', 'passhash').includes('無法解密'));
        assert.equal(hint(bad, 'password', '密碼'), null);
        assert(hint({ prtgCredentialState: 'undecryptable' }, 'token', 'API token').includes('無法解密'));
        assert.equal(hint({ prtgCredentialState: 'ok', prtgAuthMode: 'passhash' }, 'passhash', 'passhash'), null);
    },
    過期告示的原因句依憑證與最近一趟狀態() {
        const { freshnessReason } = build('../core/layout.js', ['function freshnessReason('], '', '{ freshnessReason }');
        const credential = freshnessReason({ credentialIssue: true, lastRunStatus: 'failed' }, true);
        assert.equal(credential.credential, true);
        assert(credential.text.includes('憑證無法解密'), credential.text);
        assert(credential.text.includes('；請重新輸入'), credential.text);
        assert(freshnessReason({ credentialIssue: true }, false).text.includes('請通知管理者'));
        assert.equal(freshnessReason({ credentialIssue: false, lastRunStatus: 'running' }, true).text, '最近一趟排程取數正在執行。');
        assert.equal(freshnessReason({ credentialIssue: false, lastRunStatus: 'failed' }).text, '最近一趟排程取數失敗。');
        assert.equal(freshnessReason({ credentialIssue: false, lastRunStatus: 'stopped' }).text, '最近一趟排程取數已停止。');
        assert.equal(freshnessReason({ credentialIssue: false, lastRunStatus: 'stuck' }).text, '最近一趟排程取數異常中斷。');
        for (const lastRunStatus of [null, 'success', 'warning']) {
            const idle = freshnessReason({ credentialIssue: false, lastRunStatus });
            assert.equal(idle.credential, false);
            assert(idle.text.includes('常駐'), idle.text);
        }
    },
    async prtg未對應沒有參數時不pin不跳轉不改網址() {
        const t = makeUnmatched({ search: '', hash: '#mirror', items: [{ hostId: 5 }], total: 1 });
        await t.inst.refreshUnmappedHosts(1);
        assert(!t.urls[0].includes('pinHostId'), t.urls[0]);
        assert.deepEqual(t.log, ['render']);
        assert.deepEqual(t.replaced, []);
        assert.equal(t.timers.length, 0);
    },
    async prtg未對應載入失敗不跳轉但錯誤態顯示且之後不再pin() {
        const t = makeUnmatched({ search: '?focus=unmatched&hostId=5', hash: '#mirror', fail: new Error('403 沒有權限') });
        await t.inst.refreshUnmappedHosts(1);
        assert.deepEqual(t.log, ['error:403 沒有權限']);
        assert.equal(t.row.classes.has('lf-focus-flash'), false);
        assert.equal(t.timers.length, 0);
        // 失敗後重試（錯誤態的重試鈕）不再帶 pin
        t.setFail(null, { items: [{ hostId: 5 }], total: 1 });
        await t.inst.refreshUnmappedHosts(1);
        assert(!t.urls[1].includes('pinHostId'), t.urls[1]);
        assert(!t.log.includes('jump'), t.log.join());
    },
    async prtg未對應清單第一筆不是該台時跳轉但不高亮() {
        const t = makeUnmatched({ search: '?focus=unmatched&hostId=5', hash: '#mirror', items: [{ hostId: 2 }, { hostId: 9 }], total: 2 });
        await t.inst.refreshUnmappedHosts(1);
        assert.deepEqual(t.log, ['render', 'jump']);
        assert.equal(t.row.classes.has('lf-focus-flash'), false);
        assert.equal(t.timers.length, 0);
        assert.deepEqual(t.replaced, ['/app/admin/prtg#mirror']);
    },
    async prtg未對應focus參數不合法視為沒給() {
        for (const search of ['?focus=unmatched&hostId=abc', '?focus=unmatched&hostId=0', '?focus=unmatched&hostId=-3', '?focus=unmatched', '?focus=other&hostId=5', '?hostId=5']) {
            const t = makeUnmatched({ search, hash: '#mirror', items: [{ hostId: 5 }], total: 1 });
            await t.inst.refreshUnmappedHosts(1);
            assert(!t.urls[0].includes('pinHostId'), `${search} → ${t.urls[0]}`);
            assert.deepEqual(t.log, ['render'], search);
            assert.deepEqual(t.replaced, [], search);
        }
    },
    async prtg未對應翻頁仍帶同一個pin_只有第一頁第一次跳轉() {
        const t = makeUnmatched({ search: '?focus=unmatched&hostId=5', hash: '#mirror', items: [{ hostId: 5 }], total: 60 });
        await t.inst.refreshUnmappedHosts(1);
        assert(t.urls[0].includes('pinHostId=5'), t.urls[0]);
        assert.deepEqual(t.log.filter((l) => l === 'jump'), ['jump']);
        assert.equal(t.replaced.length, 1);
        // 第 2 頁仍帶 pin：後端整份排序才一致，不會漏一台、重複一台；但不再跳轉、不再清網址
        await t.inst.refreshUnmappedHosts(2);
        assert(t.urls[1].includes('pinHostId=5'), t.urls[1]);
        assert.deepEqual(t.log.filter((l) => l === 'jump'), ['jump']);
        assert.equal(t.replaced.length, 1);
        await t.inst.refreshUnmappedHosts(1);
        assert(t.urls[2].includes('pinHostId=5'), t.urls[2]);
        assert.deepEqual(t.log.filter((l) => l === 'jump'), ['jump']);
    },
    async prtg未對應清網址保留其他參數與hash() {
        const t = makeUnmatched({ search: '?foo=1&focus=unmatched&hostId=5', hash: '#mirror', items: [{ hostId: 5 }], total: 1 });
        await t.inst.refreshUnmappedHosts(1);
        assert.deepEqual(t.replaced, ['/app/admin/prtg?foo=1#mirror']);
    },

    prtg未對應跳轉共用既有區塊展開與捲動() {
        const calls = [];
        const mk = (name) => ({ name, listeners: {}, attrs: {}, textContent: '', classes: new Set(),
            addEventListener(t, h) { this.listeners[t] = h; }, setAttribute(k, v) { this.attrs[k] = v; },
            scrollIntoView(o) { calls.push(['scroll', name, o.block]); }, focus() { calls.push(['focus', name]); } });
        for (const el of [mk('badge'), mk('section'), mk('collapse'), mk('toggle')]) el.classList = { add: (c) => el.classes.add(c), contains: (c) => el.classes.has(c), remove: (c) => el.classes.delete(c) }, (globalThis.__els ||= {})[el.name] = el;
        const els = globalThis.__els;
        const ids = { 'prtg-mirror-map-unmatched': els.badge, 'prtg-unmatched-section': els.section, 'prtg-unmatched-collapse': els.collapse, 'prtg-unmatched-toggle-btn': els.toggle };
        const inst = build('prtg-admin.js', ['function bindUnmatchedControls('], 'let jumpToUnmatchedSection = null;',
            '{ bind: bindUnmatchedControls, jump: () => jumpToUnmatchedSection }',
            { document: { getElementById: (id) => ids[id] ?? null }, window: {} });
        assert.equal(inst.jump(), null);
        inst.bind();
        assert.equal(typeof inst.jump(), 'function');
        inst.jump()();
        assert.equal(els.collapse.classes.has('show'), true, '要展開未對應區塊');
        assert.deepEqual(calls, [['scroll', 'section', 'nearest'], ['focus', 'section']]);
        // 徽章按鈕仍走同一個跳轉
        calls.length = 0;
        els.badge.listeners.click();
        assert.deepEqual(calls, [['scroll', 'section', 'nearest'], ['focus', 'section']]);
        delete globalThis.__els;
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

    // ---- settings：郵件貢獻帳本健康提示 ----
    郵件帳本提示_未達九成且無滿載紀錄時不顯示() {
        const inst = build('settings.js', ['function mailLedgerNotice('], '',
            '{ mailLedgerNotice }', { formatNumber: (n) => String(n), formatDateTime: (v) => `T(${v})` });
        assert.equal(inst.mailLedgerNotice({ occupiedKeys: 10799, limit: 12000, lastFullAtUtc: null, lastFullRecipient: null }), null);
        assert.equal(inst.mailLedgerNotice(null), null);
    },
    郵件帳本提示_滿載時帶出數字時間與收件人() {
        const inst = build('settings.js', ['function mailLedgerBreakdown(', 'function mailLedgerNotice('], '',
            '{ mailLedgerNotice }', { formatNumber: (n) => String(n), formatDateTime: (v) => `T(${v})` });
        const text = inst.mailLedgerNotice({ occupiedKeys: 12000, limit: 12000, lastFullAtUtc: '2026-10-30T01:02:03Z', lastFullRecipient: 'a@test.local' });
        assert(text.startsWith('郵件貢獻帳本已滿（12000／上限 12000）：最近一次於 T(2026-10-30T01:02:03Z) 略過收件人 a@test.local，該收件人本輪不寄。'));
        assert(!text.includes('案件'));
    },
    郵件帳本提示_顯示五個佔用分項且各有白話說明() {
        const inst = build('settings.js', ['function mailLedgerBreakdown(', 'function mailLedgerNotice('], '',
            '{ mailLedgerNotice }', { formatNumber: (n) => String(n), formatDateTime: (v) => `T(${v})` });
        const text = inst.mailLedgerNotice({
            occupiedKeys: 12000, limit: 12000, lastFullAtUtc: '2026-10-30T01:02:03Z', lastFullRecipient: 'a@test.local',
            acceptedKeys: 11000, heldKeys: 400, reservedKeys: 100, attemptedKeys: 200, unknownKeys: 300
        });
        assert(text.includes('已寄收據 11000（'), text);
        assert(text.includes('舊格式保留 400（'), text);
        assert(text.includes('已預約 100（'), text);
        assert(text.includes('寄送中 200（'), text);
        assert(text.includes('結果不確定 300（寄送結果不確定、為避免重複寄送而保留的項目）'), text);
        assert(text.includes('寄送中或結果不確定的項目不會自動釋放，因為釋放會造成重複寄信。'), text);
        // 接近上限（未滿載紀錄）也要帶分項；舊版回應缺欄位時以 0 計
        const near = inst.mailLedgerNotice({ occupiedKeys: 10800, limit: 12000, lastFullAtUtc: null, lastFullRecipient: null });
        assert(near.includes('結果不確定 0（'), near);
    },
    郵件帳本提示_達九成但無滿載紀錄時顯示接近上限() {
        const inst = build('settings.js', ['function mailLedgerBreakdown(', 'function mailLedgerNotice('], '',
            '{ mailLedgerNotice }', { formatNumber: (n) => String(n), formatDateTime: (v) => `T(${v})` });
        const text = inst.mailLedgerNotice({ occupiedKeys: 10800, limit: 12000, lastFullAtUtc: null, lastFullRecipient: null });
        assert.match(text, /^郵件貢獻帳本接近上限（10800／上限 12000）/);
    },
    // ---- settings：記憶體卡 ----
    記憶體卡_格式化函式處理空值千分位與命中率() {
        const inst = build('settings.js', ['function formatMegabytes(', 'function formatHitRate('], '',
            '{ formatMegabytes, formatHitRate }', { formatNumber: (n) => n.toLocaleString('en-US') });
        assert.equal(inst.formatMegabytes(null), '—');
        const big = inst.formatMegabytes(1048576 * 1536);
        assert(big.includes('1,536') && big.includes('MB'), big);
        assert.equal(inst.formatHitRate(0, 0), '—');
        assert.equal(inst.formatHitRate(3, 1), '75%');
    },
    記憶體卡_警示為真時渲染含接近上限的徽章() {
        const { inst, hosts, tables } = makeMemoryCard();
        inst.renderMemoryHealth({ workingSetBytes: 1048576 * 100, isServerGc: true, gcConserveMemory: 5, caches: [{ name: 'a', entries: 1, elements: 2, hits: 3, misses: 1 }] }, true);
        const warn = hosts['health-memory'].children[0];
        assert(warn.children.some((c) => c.className.includes('badge text-bg-warning')), '要有 warning 徽章');
        assert(warn.children.some((c) => c.textContent.includes('接近上限')), '徽章旁要有文字');
        assert.equal(tables[0].caption, '記憶體指標');
        assert.equal(tables[1].caption, '快取統計');
        const gc = tables[0].rows.find((r) => r.item === 'GC 模式').status;
        assert.equal(gc, 'Server，保守回收 5');
        assert.equal(tables[1].columns[3].render(tables[1].rows[0]), '75%');
    },
    記憶體卡_memory為null時不擲例外且顯示空值() {
        const { inst, hosts, tables } = makeMemoryCard();
        inst.renderMemoryHealth(null, false);
        assert.equal(hosts['health-memory'].children.length, 1, '沒有警示時只有表格');
        assert.equal(tables[0].rows.find((r) => r.item === '工作集').status, '—（峰值 —）');
        assert.equal(tables[0].rows.find((r) => r.item === 'SQLite 堆').status, '不適用');
        assert.equal(tables[0].rows.find((r) => r.item === '.NET 可用記憶體').status, '未設定');
        assert.deepEqual(tables[1].rows, []);
    },
    記憶體卡_有設硬上限時標硬上限與設定百分比() {
        const { inst, tables } = makeMemoryCard();
        inst.renderMemoryHealth({ hardLimitBytes: 1048576 * 1000, hardLimitUsagePercent: 12.5, gcHeapHardLimitPercent: 60, caches: [] }, false);
        const row = tables[0].rows.find((r) => r.item === '.NET 硬上限');
        assert(row, '有設定時列名是「.NET 硬上限」');
        assert(row.status.includes('已用 12.5%') && row.status.includes('設定 60%'), row.status);
    },
    記憶體卡_沒設硬上限時改稱可用記憶體且警示文字不說硬上限() {
        const { inst, hosts, tables } = makeMemoryCard();
        inst.renderMemoryHealth({ hardLimitBytes: 1048576 * 24000, hardLimitUsagePercent: 90, gcHeapHardLimitPercent: null, caches: [] }, true);
        const row = tables[0].rows.find((r) => r.item === '.NET 可用記憶體');
        assert(row && row.status.includes('未設硬上限'), '沒設定時要標「未設硬上限」');
        assert(!tables[0].rows.some((r) => r.item === '.NET 硬上限'), '不可把實體記憶體標成硬上限');
        const text = hosts['health-memory'].children[0].children.map((c) => c.textContent).join('');
        assert(text.includes('本機可用記憶體') && !text.includes('硬上限'), text);
    },
};

const name = process.argv[2];
if (!name) {
    console.log(Object.keys(cases).join('\n'));
    process.exit(0);
}
const fn = cases[name];
assert(fn, `未知案例：${name}`);
await fn();
console.log(`PASS ${name}`);
