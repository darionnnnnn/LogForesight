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

const tick = () => new Promise((r) => setTimeout(r, 0));

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
    const el = {};
    const inst = build('runs.js', ['async function load(', 'async function loadSummary('], `
        let currentDays = 14;
        let currentSummaryPage = 1;
        let summaryMaxDays = null;
        const summaryGuard = createSeqGuard();
        const loadGuard = createSeqGuard();
        const document = { getElementById: () => el };
    `, '{ load, loadSummary }', {
        api, el,
        createSeqGuard: guard,
        renderLoading: () => {},
        renderError: () => {},
        renderLegend: () => {},
        renderSummary: (d) => rendered.summary.push(d.tag),
        renderErrors: (d) => rendered.errors.push(d.tag),
        renderRunList: (d) => rendered.list.push(d.tag)
    });
    return {
        inst, api, rendered,
        /** 從第 firstPending 個請求起連續三個（summary／errors／list）一起回應 */
        settleLoad(firstPending, tag) {
            for (let i = 0; i < 3; i++) api.pending[firstPending + i].resolve({ tag });
        }
    };
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
};

const name = process.argv[2];
const fn = cases[name];
assert(fn, `未知案例：${name}`);
await fn();
console.log(`PASS ${name}`);
