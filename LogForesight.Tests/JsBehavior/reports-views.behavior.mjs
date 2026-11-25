/**
 * 報表頁三個視角（總覽／處理成效／問題類型，docs/WEB-SPEC.md §9.6）的行為測試。
 *
 * - reports-common.js、reports-effectiveness.js、reports-category.js 直接以 production 原檔匯入，只替換 DOM 與圖表包裝層；
 * - reports.js 的視角切換與延後載入（setView／reload／loadSubView 等）沿用切出 production 函式原文的作法，
 *   只替換 DOM、api 與期間輸入欄。
 * 由 ReportsViewsBehaviorTests 以 `node reports-views.behavior.mjs <案例名>` 逐案執行。
 */
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { installDom } from './mini-dom.mjs';
import { loadElements } from './load-elements.mjs';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const jsRoot = path.join(scriptDir, '..', '..', 'LogForesight.Web', 'wwwroot', 'js');
const source = fs.readFileSync(path.join(jsRoot, 'pages', 'reports.js'), 'utf8').replaceAll('\r\n', '\n');

const dom = installDom();
globalThis.window.LF_LABELS = { categories: { Storage: '儲存裝置', Security: '資安' } };   // 版面由伺服器注入類別中文名
globalThis.window.addEventListener = () => { };
globalThis.bootstrap.Popover = class { constructor(el) { this.el = el; } };   // helpIcon 建立說明氣泡用   // renderTable 的橫向捲動提示在沒有 ResizeObserver 時綁 resize
const load = file => import(pathToFileURL(path.join(jsRoot, ...file.split('/'))).href);
const common = await load('pages/reports-common.js');
const { recordsUrl } = await load('core/paths.js');
const { createEffectivenessView, bucketDrillUrl, recurrenceSummary, agingSwatches, unresolvedBreakdownLines } = await load('pages/reports-effectiveness.js');
const { createCategoryView, cellDrillUrl, cellAriaLabel } = await load('pages/reports-category.js');
const { roleText, fact, metaLine } = loadElements(dom);

function extractFunction(declaration) {
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

function extractConst(name) {
    const match = new RegExp(`const ${name} = [^;]+;`).exec(source);
    assert(match, `找不到 production 常數：${name}`);
    return match[0];
}

function build(declarations, prelude, exportsExpr, args = {}) {
    const names = Object.keys(args);
    const body = declarations.map(d => d.startsWith('const ') ? extractConst(d.slice(6)) : extractFunction(d)).join('\n');
    return new Function(...names, `${prelude}\n${body}\nreturn ${exportsExpr};`)(...names.map(n => args[n]));
}

const flush = () => new Promise(resolve => setTimeout(resolve, 0));
const walk = (node, visit) => { visit(node); node.children.forEach(c => walk(c, visit)); };
const collect = (root, predicate) => { const found = []; walk(root, n => { if (predicate(n)) found.push(n); }); return found; };
const has = (node, cls) => node.classList.contains(cls);
const formatNumber = n => (n === null || n === undefined ? '' : Number(n).toLocaleString('zh-TW'));
const cssVar = name => `var(${name})`;

// ── 頁面的視角切換與延後載入（reports.js 原文＋替身）─────────────────────────

function fakeNode(id, values) {
    const classes = new Set();
    return {
        id, hidden: false, dataset: {}, get value() { return values[id] ?? ''; }, textContent: '',
        classList: {
            add: n => classes.add(n), remove: (...names) => names.forEach(n => classes.delete(n)),
            contains: n => classes.has(n),
            toggle(n, force) { const on = force === undefined ? !classes.has(n) : Boolean(force); if (on) classes.add(n); else classes.delete(n); return on; }
        },
        replaceChildren() { }, setAttribute() { }
    };
}

/**
 * auto：api 立刻以 { from, to, marker: 網址 } 回應；否則回應由案例用 respond()／fail() 手動完成（測回應順序）。
 */
function makePage({ search = '', auto = true, from = '2026-10-01', to = '2026-10-30', scope = 'all' } = {}) {
    const calls = { get: [], overviewLoads: 0, rendered: [], toasts: [], toolbar: [] };
    const pending = [];
    const location = { search, pathname: '/reports' };
    const history = { replaceState: (_s, _t, url) => { location.search = url.startsWith('?') ? url : ''; } };
    const nodes = new Map();
    const values = { 'report-from': from, 'report-to': to, 'report-compare': 'previous' };
    const document = {
        getElementById: id => { if (!nodes.has(id)) nodes.set(id, fakeNode(id, values)); return nodes.get(id); },
        querySelectorAll: () => []
    };
    const api = {
        get: url => new Promise((resolve, reject) => {
            calls.get.push(url);
            if (auto) resolve({ from, to, marker: url });
            else pending.push({ url, resolve, reject });
        })
    };

    const inst = build([
        'const SCOPE_LABEL', 'const VIEW_CONTAINER', 'const SUBVIEW_ENDPOINT',
        'function needsLoad(', 'function markOtherViewsDirty(', 'function syncViewNotes(', 'function applyView(',
        'function setView(', 'function loadCurrentView(', 'function reload(', 'async function loadOtherView(', 'async function loadSubView(',
        'function noteAppliedRange(', 'function printTitle('
    ], `
        const views = { overview: createViewState(), effectiveness: createViewState(), category: createViewState() };
        let currentView = viewFromSearch(location.search);
        let currentScope = scope;
        let requestedScope = null;
        let activePresetDays = 30;
        let appliedView = null;
        let appliedRange = null;
        const SUBVIEW_RENDER = {
            effectiveness: data => calls.rendered.push(['effectiveness', data]),
            category: data => calls.rendered.push(['category', data])
        };
        const load = async () => { calls.overviewLoads++; return true; };
        const renderLoading = () => { };
        const guardLoad = async (_c, fn) => { try { return await fn(); } catch { return undefined; } };
        const syncScopeChips = () => { };
        const renderToolbarSummary = range => calls.toolbar.push(range);
        const toast = (message, kind) => calls.toasts.push([message, kind]);
    `, `{
        views, needsLoad, setView, reload, loadOtherView, loadSubView, syncViewNotes, applyView,
        view: () => currentView,
        setScope: s => { currentScope = s; },
        requested: () => requestedScope,
        askScope: s => { requestedScope = s; },
        scope: () => currentScope
    }`, {
        calls, api, document, location, history, scope,
        createViewState: common.createViewState, viewFromSearch: common.viewFromSearch, searchWithView: common.searchWithView,
        normalizeView: common.normalizeView, rangeProblem: common.rangeProblem, loadViewData: common.loadViewData,
        VIEWS: common.VIEWS
    });
    inst.calls = calls;
    inst.location = location;
    inst.nodes = nodes;
    inst.pending = pending;
    inst.values = values;
    // 進頁時總覽已載入且是最新的
    inst.views.overview.loaded = true;
    inst.views.overview.dirty = false;
    return inst;
}

// ── 處理成效視角的替身環境 ────────────────────────────────────────────────────

function makeEffectiveness() {
    const ids = ['eff-buckets-help', 'eff-aging-help', 'eff-recur-help', 'eff-buckets-wrapper', 'eff-buckets-toolbar', 'eff-buckets-chart', 'eff-aging-body', 'eff-recur-body'];
    for (const id of ids) dom.byId.set(id, dom.newElement(id === 'eff-buckets-chart' ? 'canvas' : 'div'));
    const rec = { bar: [], toolbar: [], noData: [] };
    const charts = {
        statusColors: () => ({ success: 'c-ok', inProgress: 'c-prog', unhandled: 'c-open' }),
        bar: (canvas, spec) => { rec.bar.push(spec); return { destroy() { } }; },
        attachToolbar: (_c, spec) => { rec.toolbar.push(spec); },
        renderNoData: (_w, message) => { rec.noData.push(message); }
    };
    const progressBlock = build(['function progressBlock('], '', 'progressBlock', { document, fact, metaLine, formatNumber });
    const view = createEffectivenessView({ charts, progressBlock, readToken: cssVar });
    return { view, rec, byId: id => dom.byId.get(id) };
}

function effectivenessData(overrides = {}) {
    return {
        from: '2026-11-01', to: '2026-11-14',
        buckets: [
            { from: '2026-11-01', to: '2026-11-01', days: 1, total: 10, resolved: 4, inProgress: 3, open: 3 },
            { from: '2026-11-02', to: '2026-11-02', days: 1, total: 5, resolved: 1, inProgress: 1, open: 3 }
        ],
        asOf: '2026-11-14',
        aging: [
            { label: '0–7 天', minDays: 0, maxDays: 7, count: 2 },
            { label: '8–14 天', minDays: 8, maxDays: 14, count: 2 },
            { label: '15–30 天', minDays: 15, maxDays: 30, count: 1 },
            { label: '31–60 天', minDays: 31, maxDays: 60, count: 1 },
            { label: '61 天以上', minDays: 61, maxDays: null, count: 0 }
        ],
        unresolvedTotal: 6,
        recurrence: {
            recurred: 3, notRecurred: 7, observing: 2, rate: 0.3,
            topRecurring: [{ source: 'Disk', eventId: 7, label: 'Disk (7)', hostCount: 4, recurredCount: 2 }]
        },
        definitions: { resolution: '定義一', aging: '定義二', recurrence: '定義三' },
        ...overrides
    };
}

const cases = {
    // ── 視角名稱與網址 ────────────────────────────────────────────────────
    view參數_無效值退回總覽() {
        assert.equal(common.viewFromSearch('?view=bogus'), 'overview');
        assert.equal(common.viewFromSearch(''), 'overview');
        assert.equal(common.viewFromSearch('?view=effectiveness'), 'effectiveness');
        assert.equal(common.viewFromSearch('?display=wall&view=category'), 'category');
        assert.equal(makePage({ search: '?view=nonsense' }).view(), 'overview');
    },
    view參數_寫入網址時保留其他參數_總覽不留參數() {
        assert.equal(common.searchWithView('?handlingScope=open', 'effectiveness'), '?handlingScope=open&view=effectiveness');
        assert.equal(common.searchWithView('?view=category&display=wall', 'overview'), '?display=wall');
        assert.equal(common.searchWithView('?view=category', 'overview'), '');
    },
    view參數_重新整理保留() {
        const page = makePage();
        page.setView('effectiveness');
        assert.equal(page.location.search, '?view=effectiveness');
        const afterLoad = makePage({ search: page.location.search });
        assert.equal(afterLoad.view(), 'effectiveness', '重新整理後仍在處理成效');
        page.setView('overview');
        assert.equal(page.location.search, '', '回總覽不留參數');
    },

    // ── 延後載入 ──────────────────────────────────────────────────────────
    async 切視角只打該視角的端點() {
        const page = makePage();
        page.setView('effectiveness');
        await flush();
        assert.equal(page.calls.get.length, 1);
        assert.match(page.calls.get[0], /^\/api\/reports\/effectiveness\?from=2026-10-01&to=2026-10-30$/, '只打處理成效，不連帶打總覽與問題類型');
        page.setView('category');
        await flush();
        assert.equal(page.calls.get.length, 2);
        assert.match(page.calls.get[1], /^\/api\/reports\/category-trend\?from=2026-10-01&to=2026-10-30$/);
        page.setView('effectiveness');
        page.setView('overview');
        await flush();
        assert.equal(page.calls.get.length, 2, '已載入且條件沒變的視角，切回去不重打');
        assert.equal(page.calls.overviewLoads, 0, '總覽已載入，切回去不重載');
    },
    async 初次進入非總覽視角時總覽不載入_切回去才載入() {
        const page = makePage({ search: '?view=category' });
        page.views.overview.loaded = false;
        page.views.overview.dirty = true;
        await page.reload();
        assert.equal(page.calls.get.length, 1);
        assert.match(page.calls.get[0], /category-trend/);
        assert.equal(page.calls.overviewLoads, 0, '沒進總覽就不打 summary');
        page.setView('overview');
        await flush();
        assert.equal(page.calls.overviewLoads, 1, '切到總覽才載入');
    },
    async 期間改變只重載目前視角_其他視角標記待重載() {
        const page = makePage();
        page.setView('effectiveness');
        page.setView('category');
        await flush();
        assert.equal(page.calls.get.length, 2);
        page.setView('effectiveness');
        await flush();
        assert.equal(page.calls.get.length, 2);

        page.values['report-to'] = '2026-10-20';
        await page.reload();   // 在處理成效按「套用」
        assert.equal(page.calls.get.length, 3, '只重載目前的視角');
        assert.match(page.calls.get[2], /effectiveness\?from=2026-10-01&to=2026-10-20/);
        assert.equal(page.views.category.dirty, true, '問題類型標記待重載');
        assert.equal(page.views.overview.dirty, true, '總覽標記待重載');
        assert.equal(page.calls.overviewLoads, 0);

        page.setView('category');
        await flush();
        assert.equal(page.calls.get.length, 4, '切過去才載入');
        assert.match(page.calls.get[3], /category-trend\?from=2026-10-01&to=2026-10-20/);
        page.setView('overview');
        await flush();
        assert.equal(page.calls.overviewLoads, 1, '總覽待重載，切回去才載入');
    },
    async 快速連續重載只畫最後一次() {
        const page = makePage({ auto: false });
        page.setView('effectiveness');
        const first = page.pending[0];
        page.reload();
        const second = page.pending[1];
        assert.equal(page.pending.length, 2);
        second.resolve({ from: 'a', to: 'b', marker: 'new' });
        await flush();
        first.resolve({ from: 'a', to: 'b', marker: 'old' });   // 慢的舊回應晚到
        await flush();
        assert.equal(page.calls.rendered.length, 1, '舊回應不得再畫一次');
        assert.equal(page.calls.rendered[0][1].marker, 'new');
        assert.equal(page.views.effectiveness.data.marker, 'new', '舊回應不得寫入視角狀態');
    },
    async 快速連切視角各視角只畫自己最後一次() {
        const page = makePage({ auto: false });
        page.setView('effectiveness');
        page.setView('category');
        assert.equal(page.pending.length, 2);
        const [eff, cat] = page.pending;
        cat.resolve({ from: 'a', to: 'b', marker: 'cat' });
        eff.resolve({ from: 'a', to: 'b', marker: 'eff' });
        await flush();
        assert.deepEqual(page.calls.rendered.map(r => [r[0], r[1].marker]), [['category', 'cat'], ['effectiveness', 'eff']]);
        assert.equal(page.view(), 'category', '目前視角是最後點的那個');
    },
    async 在途的其他視角舊期間回應不得清掉待重載標記() {
        const page = makePage({ auto: false });
        page.setView('effectiveness');
        const old = page.pending[0];
        page.setView('category');
        page.pending[1].resolve({ from: 'a', to: 'b', marker: 'cat' });
        await flush();
        page.values['report-to'] = '2026-10-20';
        page.reload();   // 在問題類型按套用：處理成效標記待重載
        assert.equal(page.views.effectiveness.dirty, true);
        old.resolve({ from: '2026-10-01', to: '2026-10-30', marker: 'eff-old' });   // 舊期間的處理成效晚到
        await flush();
        assert.equal(page.views.effectiveness.dirty, true, '舊期間回應不得抹掉待重載');
        assert.equal(page.scope(), 'all');
        assert(!page.calls.toolbar.some(r => r.marker === 'eff-old'), '不是目前視角的回應不得改工具列摘要');
        page.setView('effectiveness');
        await flush();
        assert.match(page.calls.get.at(-1), /effectiveness\?from=2026-10-01&to=2026-10-20/, '切回去以新期間重載');
    },
    async 舊請求失敗不影響新請求的結果() {
        const page = makePage({ auto: false });
        page.setView('effectiveness');
        const first = page.pending[0];
        page.reload();
        const second = page.pending[1];
        first.reject(new Error('舊的逾時'));
        second.resolve({ from: 'a', to: 'b', marker: 'new' });
        await flush();
        assert.equal(page.calls.rendered.length, 1);
        assert.equal(page.views.effectiveness.failed, false, '失敗態只屬於最新一次請求');
    },
    async 載入失敗時視角標成失敗_待範圍清掉_下次切過來會重試() {
        const page = makePage({ auto: false });
        page.askScope('open');
        page.setView('effectiveness');
        page.pending[0].reject(new Error('boom'));
        await flush();
        assert.equal(page.requested(), null, '失敗後不留下沒生效的範圍');
        assert.equal(page.views.effectiveness.failed, true);
        assert(page.nodes.get('view-effectiveness').classList.contains('lf-report-view--failed'), '列印時這頁寫「此部分未能載入」');
        assert.equal(page.needsLoad('effectiveness'), true);
        assert.equal(page.calls.rendered.length, 0);
    },
    async 起日晚於迄日時處理成效不發出查詢() {
        const page = makePage({ from: '2026-10-10', to: '2026-10-01' });
        page.askScope('open');
        page.setView('effectiveness');
        await flush();
        assert.equal(page.calls.get.length, 0);
        assert.equal(page.calls.toasts.length, 1);
        assert.match(page.calls.toasts[0][0], /起日不可晚於迄日/);
        assert.equal(page.requested(), null);
    },
    async 載入成功才讓顯示範圍生效() {
        const page = makePage();
        page.askScope('unresolved');
        page.setView('category');
        await flush();
        assert.equal(page.scope(), 'unresolved');
        assert.equal(page.requested(), null);
        assert.match(page.location.search, /handlingScope=unresolved/);
        assert.match(page.location.search, /view=category/, '範圍寫回網址時不丟掉 view');
    },

    // ── 顯示範圍提示 ──────────────────────────────────────────────────────
    顯示範圍提示_unresolved有_all沒有() {
        const page = makePage();
        const note = () => page.nodes.get('report-view-note').hidden;
        page.applyView('effectiveness');
        page.setScope('unresolved');
        page.syncViewNotes();
        assert.equal(note(), false, '處理成效：範圍不是全部要說明');
        page.applyView('category');
        assert.equal(note(), false, '問題類型：範圍不是全部要說明');
        page.setScope('all');
        page.syncViewNotes();
        assert.equal(note(), true, '範圍是全部時沒有提示');
        page.setScope('open');
        page.applyView('overview');
        assert.equal(note(), true, '總覽本來就套用顯示範圍，不出現這句話');
    },

    // ── 處理成效視角 ──────────────────────────────────────────────────────
    處理成效_分桶堆疊條與未處理段下鑽() {
        const { view, rec } = makeEffectiveness();
        view.render(effectivenessData());
        const spec = rec.bar[0];
        assert.deepEqual(spec.data.datasets.map(d => d.label), ['已結案', '處理中', '未處理']);
        assert.deepEqual(spec.data.datasets.map(d => d.backgroundColor), ['c-ok', 'c-prog', 'c-open'], '色用既有處理狀態色盤');
        assert.deepEqual(spec.data.datasets[2].data, [3, 3]);
        assert.deepEqual(spec.data.labels, ['11/01', '11/02'], '日桶寫 MM/DD');
        assert.equal(spec.options.scales.x.stacked, true);
        assert.equal(spec.options.scales.y.stacked, true);
        const url = new URL(spec.drillTo({ index: 1, datasetIndex: 2 }), 'http://x');
        assert.equal(url.pathname, '/records');
        assert.equal(url.searchParams.get('statuses'), 'open');
        assert.equal(url.searchParams.get('riskLevels'), '高,中');
        assert.equal(url.searchParams.get('from'), '2026-11-02');
        assert.equal(url.searchParams.get('to'), '2026-11-02');
        assert.equal(new URL(spec.drillTo({ index: 0, datasetIndex: 0 }), 'http://x').searchParams.get('statuses'), 'resolved');
        assert.equal(new URL(spec.drillTo({ index: 0, datasetIndex: 1 }), 'http://x').searchParams.get('statuses'), 'in_progress');
        assert.deepEqual(rec.toolbar[0].tableColumns, ['時段', '已結案', '處理中', '未處理', '合計'], '保留表格切換');
    },
    處理成效_桶標籤_日週月() {
        assert.equal(common.bucketMode([{ days: 1 }, { days: 1 }]), 'day');
        assert.equal(common.bucketMode([{ days: 3 }, { days: 7 }, { days: 7 }]), 'week');
        assert.equal(common.bucketMode([{ days: 12 }, { days: 30 }, { days: 31 }]), 'month');
        const week = { from: '2026-11-03', to: '2026-11-09', days: 7 };
        assert.equal(common.bucketLabel(week, 'week'), '11/03–11/09');
        assert.equal(common.bucketLabel({ from: '2026-11-09', to: '2026-11-09', days: 1 }, 'week'), '11/09', '只剩一天的週桶寫單日');
        assert.equal(common.bucketLabel({ from: '2026-11-01', to: '2026-11-30', days: 30 }, 'month'), '2026/11');
        assert.equal(common.bucketLabel({ from: '2026-11-01', to: '2026-11-01', days: 1 }, 'day'), '11/01');
    },
    處理成效_帳齡五段色階由淡到深且標題寫未結案N() {
        const { view, byId } = makeEffectiveness();
        view.render(effectivenessData());
        const body = byId('eff-aging-body');
        assert(body.textContent.includes('未結案 6'), `標題列要寫「未結案 N」：${body.textContent}`);
        const segs = collect(body, n => has(n, 'lf-stack-bar__seg'));
        assert.equal(segs.length, 4, '值為 0 的段不畫（最後一段 0）');
        assert.deepEqual(segs.map(s => s.style['--lf-swatch']), ['var(--lf-heat-1)', 'var(--lf-heat-2)', 'var(--lf-heat-3)', 'var(--lf-heat-4)'], '由新到舊色階加深');
        for (const label of ['0–7 天 2', '8–14 天 2', '61 天以上 0']) assert(body.textContent.includes(label.replace(' ', '')) || body.textContent.includes(label), `畫面上要有「${label}」`);
        assert.equal(collect(body, n => has(n, 'lf-stack-bar'))[0].getAttribute('role'), 'img');
        assert.deepEqual(agingSwatches(7, cssVar).slice(4), ['var(--lf-heat-5)', 'var(--lf-heat-5)', 'var(--lf-heat-5)'], '段數多於色階時共用最深一級');
    },
    處理成效_切成表格後重畫回到圖表() {
        const { view, byId } = makeEffectiveness();
        view.render(effectivenessData());
        byId('eff-buckets-wrapper').classList.add('d-none');   // 使用者按了「表格」
        view.render(effectivenessData());
        assert.equal(byId('eff-buckets-wrapper').classList.contains('d-none'), false, '重畫後圖表容器要顯示，否則整張卡空白');
        byId('eff-buckets-wrapper').classList.add('d-none');
        view.render(effectivenessData({ buckets: [] }));
        assert.equal(byId('eff-buckets-wrapper').classList.contains('d-none'), false, '沒資料時也要顯示「沒有風險日」');
    },
    處理成效_沒有未結案時不畫條() {
        const { view, byId } = makeEffectiveness();
        view.render(effectivenessData({ unresolvedTotal: 0, aging: [] }));
        const body = byId('eff-aging-body');
        assert.equal(collect(body, n => has(n, 'lf-stack-bar')).length, 0);
        assert(body.textContent.includes('目前沒有未結案的風險日'));
    },
    處理成效_再發生率分母為0時顯示說明而不是0百分比() {
        const { view, byId } = makeEffectiveness();
        const data = effectivenessData();
        data.recurrence = { recurred: 0, notRecurred: 0, observing: 4, rate: null, topRecurring: [] };
        view.render(data);
        const text = byId('eff-recur-body').textContent;
        assert(text.includes('期間內沒有可評估的已處理問題'), text);
        assert(!text.includes('0%'), `不得出現 0%：${text}`);
        assert(text.includes('另有 4 組仍在觀察期'), '觀察中的組數仍要寫出來');
        assert(text.includes('期間內沒有再發生的問題'), '沒有前 10 名時寫一句，不畫空表');
        assert.deepEqual(recurrenceSummary(data.recurrence), { empty: true, text: '期間內沒有可評估的已處理問題', percent: null });
    },
    處理成效_再發生率大數字與前10名表格() {
        const { view, byId } = makeEffectiveness();
        view.render(effectivenessData());
        const body = byId('eff-recur-body');
        const text = body.textContent;
        assert(text.includes('3 / 10 組'), text);
        assert(text.includes('（30%）'), text);
        assert(text.includes('另有 2 組仍在觀察期'));
        const headers = collect(body, n => n.tagName === 'TH').map(n => n.textContent);
        assert.deepEqual(headers, ['問題', '已處理主機', '再發生']);
        const link = collect(body, n => n.tagName === 'A')[0];
        const url = new URL(link.href, 'http://x');
        assert.equal(url.searchParams.get('view'), 'issue');
        assert.equal(url.searchParams.get('source'), 'Disk');
        assert.equal(url.searchParams.get('eventId'), '7');
        assert.equal(url.searchParams.get('from'), '2026-11-01');
        assert.equal(url.searchParams.get('to'), '2026-11-14');
        const cells = collect(body, n => n.tagName === 'TD').map(n => n.textContent);
        assert.deepEqual(cells.slice(-2), ['4', '2']);
    },
    處理成效_三個指標各有說明圖示且文字取自後端() {
        const { view, byId } = makeEffectiveness();
        view.render(effectivenessData());
        const contents = ['eff-buckets-help', 'eff-aging-help', 'eff-recur-help'].map(id => byId(id).children[0].getAttribute('data-bs-content'));
        assert.deepEqual(contents, ['定義一', '定義二', '定義三'], '說明文字取自後端 definitions');
        for (const id of ['eff-buckets-help', 'eff-aging-help', 'eff-recur-help']) {
            assert.equal(byId(id).children.length, 1, `${id} 要有說明圖示`);
            assert.equal(byId(id).children[0].tabIndex, 0, '說明圖示可用鍵盤聚焦');
        }
        view.render(effectivenessData());
        assert.equal(byId('eff-buckets-help').children.length, 1, '重畫不累積圖示');
    },
    處理成效_期間內沒有風險日顯示無資料() {
        const { view, rec } = makeEffectiveness();
        view.render(effectivenessData({ buckets: [{ from: '2026-11-01', to: '2026-11-01', days: 1, total: 0, resolved: 0, inProgress: 0, open: 0 }] }));
        assert.equal(rec.bar.length, 0);
        assert.deepEqual(rec.noData, ['此期間沒有風險日']);
    },
    處理成效_數字對得起來_帳齡合計等於未結案_分桶合計等於各桶三態相加() {
        const data = effectivenessData();
        assert.equal(data.aging.reduce((s, b) => s + b.count, 0), data.unresolvedTotal);
        for (const b of data.buckets) assert.equal(b.resolved + b.inProgress + b.open, b.total);
        // 畫面讀到的數字與資料一致：未結案標題 N＝帳齡各段合計
        const { view, byId } = makeEffectiveness();
        view.render(data);
        assert(byId('eff-aging-body').textContent.includes(`未結案 ${data.aging.reduce((s, b) => s + b.count, 0)}`));
    },
    處理成效_下鑽網址函式() {
        const url = new URL(bucketDrillUrl({ from: '2026-11-01', to: '2026-11-07' }, 'open'), 'http://x');
        assert.deepEqual([url.searchParams.get('statuses'), url.searchParams.get('riskLevels'), url.searchParams.get('from'), url.searchParams.get('to')],
            ['open', '高,中', '2026-11-01', '2026-11-07']);
    },

    // ── 問題類型視角（熱度表）─────────────────────────────────────────────
    熱度色階_0不上色_最大值落在最高級() {
        assert.equal(common.heatLevel(0, 12), 0);
        assert.equal(common.heatLevel(5, 0), 0);
        assert.equal(common.heatLevel(12, 12), common.HEAT_LEVELS);
        assert.equal(common.heatLevel(1, 100), 1, '有值至少一級');
        const levels = [1, 3, 6, 9, 12].map(v => common.heatLevel(v, 12));
        assert.deepEqual([...levels].sort((a, b) => a - b), levels, '值越大級數不降');
    },
    熱度表_零的格不可點_非零格有下鑽與可讀名稱() {
        for (const id of ['cat-help', 'cat-heat-body']) dom.byId.set(id, dom.newElement('div'));
        const view = createCategoryView();
        view.render({
            from: '2026-11-01', to: '2026-11-14', cellMax: 12,
            buckets: [
                { from: '2026-11-03', to: '2026-11-09', days: 7 },
                { from: '2026-11-10', to: '2026-11-14', days: 5 }
            ],
            rows: [
                { category: 'Storage', total: 12, cells: [12, 0] },
                { category: 'Security', total: 2, cells: [0, 2] }
            ]
        });
        const body = dom.byId.get('cat-heat-body');
        const links = collect(body, n => n.tagName === 'A');
        assert.equal(links.length, 2, '只有非 0 的格是連結');
        const storage = links.find(l => l.getAttribute('aria-label').startsWith('儲存'));
        assert(storage, `每格要有可讀名稱：${links.map(l => l.getAttribute('aria-label'))}`);
        assert.equal(storage.getAttribute('aria-label'), '儲存裝置，11/03–11/09，12 個問題日');
        const url = new URL(storage.href, 'http://x');
        assert.equal(url.pathname, '/records');
        assert.equal(url.searchParams.get('categories'), 'Storage');
        assert.equal(url.searchParams.get('from'), '2026-11-03');
        assert.equal(url.searchParams.get('to'), '2026-11-09');
        assert(has(storage, 'lf-heat__cell--5'), '最大值是最高一級');
        assert.equal(storage.textContent, '12', '格內印數字');
        const zeros = collect(body, n => has(n, 'lf-heat__zero'));
        assert.equal(zeros.length, 2);
        assert(zeros.every(z => z.tagName !== 'A' && !z.href && !String(z.className).includes('lf-heat__cell--')), '0 的格不上色、不可點');
        const headers = collect(body, n => n.tagName === 'TH').map(n => n.textContent);
        assert.deepEqual(headers, ['類型', '11/03–11/09', '11/10–11/14', '合計'], '最右一欄是合計');
        const helpButton = dom.byId.get('cat-help').children[0];
        assert(helpButton && helpButton.getAttribute('data-bs-content').includes('問題日'), '有定義說明');
    },
    熱度表_下鑽網址與名稱函式() {
        const url = new URL(cellDrillUrl('Storage', { from: '2026-11-01', to: '2026-11-30' }), 'http://x');
        assert.equal(url.searchParams.get('categories'), 'Storage');
        assert.equal(url.searchParams.get('riskLevels'), '高,中,低', '問題層級的類別統計，風險層級不預設隱藏低風險');
        assert.equal(cellAriaLabel('硬體', '2026/11', 3), '硬體，2026/11，3 個問題日');
    },

    // ── 列印 ──────────────────────────────────────────────────────────────
    async 列印前補載入尚未載入的視角_全部回應後才呼叫print() {
        const order = [];
        const gates = {};
        const done = [];
        const loaded = new Set(['overview']);
        const printed = common.printAllViews({
            views: common.VIEWS,
            needsLoad: v => !loaded.has(v),
            load: v => new Promise(resolve => { order.push(`load:${v}`); gates[v] = resolve; }),
            onResult: (v, ok) => done.push([v, ok]),
            beforePrint: () => order.push('before'),
            print: () => order.push('print')
        });
        await flush();
        assert.deepEqual(order, ['load:effectiveness', 'load:category'], '已載入的總覽不重載');
        gates.effectiveness();
        await flush();
        assert(!order.includes('print'), '還有視角在載入時不得先印');
        gates.category();
        await printed;
        assert.deepEqual(order.slice(2), ['before', 'print'], 'print 之前一定先補載入完成');
        assert.deepEqual(done, [['effectiveness', true], ['category', true]]);
    },
    async 列印時某視角載入失敗仍會印_並標記失敗() {
        const order = [];
        const done = [];
        await common.printAllViews({
            views: common.VIEWS,
            needsLoad: () => true,
            load: async v => { if (v === 'category') throw new Error('逾時'); },
            onResult: (v, ok) => done.push([v, ok]),
            beforePrint: () => order.push('before'),
            print: () => order.push('print')
        });
        assert.deepEqual(order, ['before', 'print'], '失敗不阻擋列印');
        assert.deepEqual(done.find(d => d[0] === 'category'), ['category', false], '失敗的視角由頁面標成「此部分未能載入」');
        assert.deepEqual(done.filter(d => d[1]).map(d => d[0]).sort(), ['effectiveness', 'overview']);
    },
    async 列印整合_頁面的printReport補載入待重載的視角() {
        const page = makePage();
        page.views.effectiveness.loaded = true;
        page.views.effectiveness.dirty = false;
        const printed = [];
        const loadedSub = [];
        const superseded = [];
        const classes = new Set();
        const listeners = [];
        const inst = build(['const PRINT_ALL_CLASS', 'async function loadForPrint(', 'function nextFrames(', 'function printReport('], `
            const requestAnimationFrame = fn => setTimeout(fn, 0);
            let printInFlight = null;
            const VIEWS = ['overview', 'effectiveness', 'category'];
            const views = pageViews;
            const needsLoad = view => !views[view].loaded || views[view].failed || views[view].dirty;
            const loadSubView = async view => {
                loadedSub.push(view);
                if (superseded.includes(view)) return undefined;   // 回應被更新的請求取代：production 的 loadSubView 回 undefined
                views[view].loaded = true; views[view].dirty = false;
                return true;
            };
            const loadReport = async () => { loadedSub.push('overview'); return true; };
            const document = {
                querySelector: () => ({ classList: { add: n => classes.add(n), remove: n => classes.delete(n) } }),
                getElementById: id => pageNodes(id)
            };
            const window = { print: () => printed.push([...classes]), addEventListener: (t, fn) => listeners.push([t, fn]) };
        `, '{ printReport }', {
            pageViews: page.views, loadedSub, classes, printed, listeners, superseded,
            pageNodes: id => { if (!page.nodes.has(id)) page.nodes.set(id, fakeNode(id, {})); return page.nodes.get(id); },
            printAllViews: common.printAllViews
        });
        const first = inst.printReport();
        const second = inst.printReport();   // 連按：沿用同一次
        assert.equal(second, first, '列印進行中再按沿用同一次');
        await first;
        assert.equal(printed.length, 1, '只開一次列印視窗');
        assert.deepEqual(loadedSub, ['category'], '只補載入還沒載入的視角（問題類型）');
        assert.deepEqual(printed, [['lf-report-print-all']], '列印時三個視角都顯示（列印用類別已加上）');
        assert.equal(listeners[0][0], 'afterprint', '印完還原');

        // 補載入途中使用者切到那個視角又發了一次請求：這次什麼都沒畫，不能當成功印出骨架列
        page.views.category.dirty = true;
        superseded.push('category');
        await inst.printReport();
        assert(page.nodes.get('view-category').classList.contains('lf-report-view--failed'), '被取代的補載入標成「此部分未能載入」');
    },
    async 改了日期沒按查詢就切視角_其他視角標成待重載() {
        const page = makePage({ auto: false });
        page.setView('effectiveness');
        page.pending[0].resolve({ from: '2026-10-01', to: '2026-10-30' });
        await flush();
        assert.equal(page.views.overview.dirty, false, '同一個期間：總覽的資料仍然有效');
        page.values['report-from'] = '2026-09-01';   // 改了起日，沒按「查詢」
        page.setView('category');
        assert.match(page.pending[1].url, /from=2026-09-01/);
        page.pending[1].resolve({ from: '2026-09-01', to: '2026-10-30' });
        await flush();
        // 問題類型已用新期間載入、工具列也寫新期間：總覽與處理成效不重載的話同一頁的數字對不起來
        assert.equal(page.views.overview.dirty, true);
        assert.equal(page.views.effectiveness.dirty, true);
        assert.equal(page.views.category.dirty, false);
    },
    總覽成功載入_清掉列印失敗標記_期間變了其他視角待重載_隱藏中不動共用標題() {
        const page = makePage();
        const overviewNode = fakeNode('view-overview', {});
        page.nodes.set('view-overview', overviewNode);
        const title = fakeNode('print-title', {});
        page.nodes.set('print-title', title);
        const shared = { range: null };
        // build 以參數傳值：currentView／currentData 每段重新 build 才會換值，appliedRange 由 shared 接續
        const run = (view, data) => build(['const SCOPE_LABEL', 'function renderAll(', 'function printTitle(', 'function noteAppliedRange(', 'function markOtherViewsDirty('], `
            const VIEWS = ['overview', 'effectiveness', 'category'];
            const views = pageViews;
            let chartAnimate = true;
            let currentScope = 'unresolved';
            let appliedRange = shared.range;
            const wall = { isActive: () => false, status: () => ({ loads: 0 }), dataLoaded() { } };
            const renderToolbarSummary = () => { }, renderKpi = () => { }, renderCharts = () => { };
            const document = { getElementById: id => pageNodes(id) };
        `, '{ renderAll, range: () => appliedRange }', { pageViews: page.views, pageNodes: id => page.nodes.get(id), currentView: view, currentData: data, shared });

        // 列印補載入失敗時標上的類別，總覽之後重載成功要清掉（否則再列印仍只印「此部分未能載入」）
        overviewNode.classList.add('lf-report-view--failed');
        page.views.effectiveness.loaded = true; page.views.effectiveness.dirty = false;
        let r = run('overview', { from: '2026-10-01', to: '2026-10-30' });
        r.renderAll(true);
        shared.range = r.range();
        assert(!overviewNode.classList.contains('lf-report-view--failed'));
        assert.match(title.textContent, /總覽：未結案；處理成效與問題類型不套用顯示範圍/, '紙上寫明顯示範圍只套用在總覽');
        assert.equal(page.views.effectiveness.dirty, false, '第一次載入不標其他視角');

        // 期間不同的成功載入：其他視角待重載
        r = run('overview', { from: '2026-09-01', to: '2026-10-30' });
        r.renderAll(true);
        shared.range = r.range();
        assert.equal(page.views.effectiveness.dirty, true);

        // 列印補載入隱藏中的總覽：不動共用的標題，也不把目前顯示的視角標成待重載
        page.views.effectiveness.dirty = false;
        title.textContent = '目前視角的標題';
        r = run('effectiveness', { from: '2026-08-01', to: '2026-10-30' });
        r.renderAll(true);
        assert.equal(title.textContent, '目前視角的標題');
        assert.equal(page.views.effectiveness.dirty, false);
    },
    async 視角重載中與失敗時收起舊期間的圖表_成功後還原() {
        const page = makePage({ auto: false });
        const pending = () => page.nodes.get('view-effectiveness').classList.contains('lf-report-view--pending');
        page.setView('effectiveness');
        assert.equal(pending(), true, '載入中不留上一個期間的數字');
        page.pending[0].reject(new Error('逾時'));
        await flush();
        assert.equal(pending(), true, '失敗時「重試」下面也不留舊數字');
        page.loadOtherView('effectiveness');
        page.pending[1].resolve({ from: 'a', to: 'b' });
        await flush();
        assert.equal(pending(), false);
    },

    // ── 大螢幕 ────────────────────────────────────────────────────────────
    大螢幕視角保存_進入切回總覽_離開還原() {
        const log = [];
        let view = 'effectiveness';
        const keeper = common.createWallViewKeeper({
            getView: () => view,
            show: v => { view = v; log.push(`show:${v}`); },
            restore: v => { view = v; log.push(`restore:${v}`); }
        });
        keeper.enter();
        assert.equal(view, 'overview');
        assert.equal(keeper.saved(), 'effectiveness');
        keeper.leave();
        assert.equal(view, 'effectiveness');
        assert.equal(keeper.saved(), null);
        assert.deepEqual(log, ['show:overview', 'restore:effectiveness']);
        // 本來就在總覽：進出都不動
        view = 'overview';
        log.length = 0;
        keeper.enter();
        keeper.leave();
        assert.deepEqual(log, []);
    }
    ,

    // K3：未結案三段各自寫明單位；待派試跑超過上限時直說拆不開，不猜
    '處理成效_未結案三段各寫單位'() {
        const lines = unresolvedBreakdownLines({
            inHandlers: { cases: 188, oldestDays: 40, hostDays: 900 },
            pendingDispatch: { tooLarge: false, issueHosts: 12, hostDays: 300 },
            notNeeded: { hostDays: 826 }
        });
        assert.deepEqual(lines, [
            '在處理人手上：900 個主機日（進行中交辦 188 項，最舊 40 天）',
            '待派：300 個主機日（可派的問題×主機 12 個）',
            '不需處理：826 個主機日（嚴重度未達、已知雜訊、不再打擾、靜音等，不會派工）'
        ]);
        // 邊界：沒有進行中案件時不寫「最舊」
        assert.equal(unresolvedBreakdownLines({ inHandlers: { cases: 0, oldestDays: null, hostDays: 0 }, pendingDispatch: {}, notNeeded: {} })[0],
            '在處理人手上：0 個主機日（進行中交辦 0 項）');
        // 超過上限：兩段合併成一句說明
        const tooLarge = unresolvedBreakdownLines({ inHandlers: { cases: 1, hostDays: 2 }, pendingDispatch: { tooLarge: true }, notNeeded: {} });
        assert.equal(tooLarge.length, 2);
        assert.equal(tooLarge[1], '待派與不需處理：期間太長，無法拆開，請縮短期間');
        assert.deepEqual(unresolvedBreakdownLines(null), []);
    }

};

const name = process.argv[2];
if (!name) {
    console.log(Object.keys(cases).join('\n'));
    process.exit(0);
}
assert(cases[name], `未知的案例：${name}`);
await cases[name]();
console.log(`PASS ${name}`);
