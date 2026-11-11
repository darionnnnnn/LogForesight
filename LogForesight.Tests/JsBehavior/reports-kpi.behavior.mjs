/**
 * 報表頁（pages/reports.js）第一排四格、處理進度條、排行標籤與風險趨勢圖的行為測試。
 *
 * 從 production 原始碼切出函式並執行，只替換 DOM、Chart 包裝層與 api 邊界；
 * 文字角色與 metaLine／fact 載入 production 的 core/elements.js，下鑽網址用 production 的 recordsUrl。
 * 由 ReportsKpiBehaviorTests 以 `node reports-kpi.behavior.mjs <案例名>` 逐案執行。
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
const { recordsUrl } = await import(pathToFileURL(path.join(jsRoot, 'core', 'paths.js')).href);
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

/** 單行 const 宣告（PRESET_TITLES、SCOPE_LABEL 這類對照表），直接用 production 那一份 */
function extractConst(name) {
    const match = new RegExp(`const ${name} = [^;]+;`).exec(source);
    assert(match, `找不到 production 常數：${name}`);
    return match[0];
}

/** 以 production 函式原文建立可執行實例；prelude 提供該函式用到的外部變數與替身 */
function build(declarations, prelude, exportsExpr, args = {}) {
    const names = Object.keys(args);
    const body = declarations.map(d => d.startsWith('const ') ? extractConst(d.slice(6)) : extractFunction(d)).join('\n');
    return new Function(...names, `${prelude}\n${body}\nreturn ${exportsExpr};`)(...names.map(n => args[n]));
}

const formatNumber = n => (n === null || n === undefined ? '' : Number(n).toLocaleString('zh-TW'));

const KPI_FUNCTIONS = [
    'function laggingText(', 'function coverageCard(', 'function openRiskCard(',
    'function comparisonBadge(', 'function compareSub(', 'function scopeDrillParams(', 'function buildKpiCards('
];

/** 第一排四格：以指定的資料與顯示範圍建出卡片陣列 */
function makeKpi(data, scope = 'all') {
    return build(KPI_FUNCTIONS, `
        const currentData = data;
        let currentScope = scope;
    `, '{ buildKpiCards, coverageCard, openRiskCard, comparisonBadge, setScope: s => { currentScope = s; } }',
        { data, scope, formatNumber, recordsUrl, roleText });
}

function baseData(overrides = {}) {
    return {
        from: '2026-10-12',
        to: '2026-11-10',
        totalHosts: 12,
        kpi: {
            highRiskDays: 134, highRiskDaysPrevious: 56,
            affectedHosts: 11, affectedHostsPrevious: 9,
            totalIssues: 655
        },
        handling: { openCount: 200, inProgressCount: 10, resolvedCount: 0, overdueCount: 3, totalCount: 210 },
        coverage: { missingHostDays: 0, dataThrough: '2026-11-10', lagging: false, noData: false },
        ...overrides
    };
}

const coverageOf = (data, over) => makeKpi(baseData({ coverage: { missingHostDays: 0, dataThrough: '2026-11-10', lagging: false, noData: false, ...over } }))
    .coverageCard({ missingHostDays: 0, dataThrough: '2026-11-10', lagging: false, noData: false, ...over });

const has = (node, cls) => node.classList.contains(cls);
const walk = (node, visit) => { visit(node); node.children.forEach(c => walk(c, visit)); };
const collect = (root, predicate) => { const found = []; walk(root, n => { if (predicate(n)) found.push(n); }); return found; };

function makeHandling(data, visibleRisk = ['高', '中', '低']) {
    const body = dom.newElement('div');
    dom.byId.set('handling-body', body);
    const charts = {
        statusColors: () => ({ success: '#16a34a', inProgress: '#0284c7', unhandled: '#94a3b8', neutral: '#cbd5e1' }),
        riskColors: () => ({ 高: '#dc2626', 中: '#d97706', 低: '#64748b' })
    };
    const inst = build(['function progressBlock(', 'function renderHandlingProgress('], `
        const currentData = data;
        const visibleDayRisk = new Set(visibleRisk);
    `, '{ renderHandlingProgress }', { data, visibleRisk, charts, formatNumber, fact, metaLine });
    inst.body = body;
    return inst;
}

function makeTrend(points, { visibleRisk = ['高', '中', '低'] } = {}) {
    for (const id of ['trend-wrapper', 'trend-toolbar', 'trend-chart']) dom.byId.set(id, dom.newElement(id === 'trend-chart' ? 'canvas' : 'div'));
    const calls = { bar: [], line: 0, noData: 0, toolbar: 0 };
    const charts = {
        riskColors: () => ({ 高: '#dc2626', 中: '#d97706', 低: '#64748b' }),
        bar: (canvas, spec) => { calls.bar.push(spec); return { destroy() { } }; },
        line: () => { calls.line++; },
        renderNoData: () => { calls.noData++; },
        attachToolbar: () => { calls.toolbar++; }
    };
    const inst = build(['function renderTrendChart(', 'function mountToolbar(', 'function clearToolbar('], `
        const currentData = { trend: points };
        const visibleDayRisk = new Set(visibleRisk);
        const chartInstances = {};
    `, '{ renderTrendChart }', { points, visibleRisk, charts, recordsUrl });
    inst.calls = calls;
    return inst;
}

const cases = {
    // ── 資料完整度（第一排第四格）──────────────────────────────────────────
    資料完整度_無資料() {
        const card = coverageOf(null, { noData: true });
        assert.equal(card.value, '無資料');
        assert.equal(card.variant, 'secondary');
        assert.equal(card.sub, '');
        assert.equal(card.url, null, '資料完整度不可下鑽');
    },
    資料完整度_完整() {
        const card = coverageOf(null, {});
        assert.equal(card.value, '完整');
        assert.equal(card.variant, undefined, '正常狀態不上語意色');
        assert.equal(card.sub, '');
        assert.equal(card.url, null);
    },
    資料完整度_缺漏() {
        const card = coverageOf(null, { missingHostDays: 5 });
        assert.equal(card.value, '缺 5');
        assert.equal(card.variant, 'warning');
        assert.equal(card.sub.textContent, '主機日沒有分析資料', '單位放副標，數字保持短');
        assert(!has(card.sub, 'lf-text-alert'), '沒落後時副標不是風險文字');
    },
    資料完整度_落後() {
        const card = coverageOf(null, { lagging: true, dataThrough: '2026-11-08' });
        assert.equal(card.value, '完整', '沒有缺漏時值仍是完整');
        assert.equal(card.variant, 'warning');
        assert.equal(card.sub.textContent, '資料至 11/08');
        assert(has(card.sub, 'lf-text-alert'), '落後用風險狀態文字');
    },
    資料完整度_缺漏且落後() {
        const card = coverageOf(null, { missingHostDays: 3, lagging: true, dataThrough: '2026-11-08' });
        assert.equal(card.value, '缺 3');
        assert.equal(card.variant, 'warning');
        assert.equal(card.sub.textContent, '主機日沒有分析資料・資料至 11/08');
        assert(card.sub.children.some(c => has(c, 'lf-text-alert') && c.textContent === '資料至 11/08'), '落後那段用風險狀態文字');
    },

    // ── 未結案風險日 ──────────────────────────────────────────────────────
    未結案_有逾期副標寫逾期() {
        const card = makeKpi(baseData()).openRiskCard();
        assert.equal(card.value, '210', '值＝未處理＋處理中');
        assert.equal(card.sub.textContent, '逾期 3');
        assert(has(card.sub, 'lf-text-alert'));
    },
    未結案_無逾期不出現逾期字樣() {
        const data = baseData({ handling: { openCount: 5, inProgressCount: 2, resolvedCount: 1, overdueCount: 0, totalCount: 8 } });
        const card = makeKpi(data).openRiskCard();
        assert.equal(card.value, '7');
        assert.equal(card.sub, '', '沒有逾期時副標是空的');
        assert(!String(card.sub?.textContent ?? card.sub).includes('逾期'));
    },
    未結案_四種顯示範圍的下鑽網址() {
        const expected = {
            all: { statuses: 'open,in_progress', unassigned: null },
            unresolved: { statuses: 'open,in_progress', unassigned: null },
            open: { statuses: 'open', unassigned: null },
            unassigned: { statuses: 'open,in_progress', unassigned: 'true' }
        };
        for (const [scope, want] of Object.entries(expected)) {
            const card = makeKpi(baseData(), scope).openRiskCard();
            const url = new URL(card.url, 'http://x');
            assert.equal(url.pathname, '/records', scope);
            assert.equal(url.searchParams.get('statuses'), want.statuses, `${scope} 的 statuses`);
            assert.equal(url.searchParams.get('unassigned'), want.unassigned, `${scope} 的 unassigned`);
            assert.equal(url.searchParams.get('riskLevels'), '高,中', scope);
            assert.equal(url.searchParams.get('from'), '2026-10-12', scope);
            assert.equal(url.searchParams.get('to'), '2026-11-10', scope);
            assert.equal(url.searchParams.getAll('statuses').length, 1, `${scope} 不得重複帶 statuses`);
        }
    },

    // ── 受影響主機與前期對比 ──────────────────────────────────────────────
    受影響主機寫成N除以M台() {
        const cards = makeKpi(baseData()).buildKpiCards();
        assert.deepEqual(cards.map(c => c.label), ['高風險日', '受影響主機', '未結案風險日', '資料完整度']);
        assert.equal(cards[1].value, '11 / 12 台');
        assert.equal(cards[0].value, '134');
    },
    前期為0與兩期皆0的對比文字() {
        const { comparisonBadge } = makeKpi(baseData());
        const fromZero = comparisonBadge(5, 0);
        assert.equal(fromZero.textContent, '↑ 前期為 0');
        assert(has(fromZero, 'lf-text-alert'));
        const bothZero = comparisonBadge(0, 0);
        assert.equal(bothZero.textContent, '與前期相同');
        assert(has(bothZero, 'lf-text-meta'), '沒變化不上警示色');
        assert.equal(comparisonBadge(10, 5).textContent, '↑ 100%（前期 5）');
        assert.equal(comparisonBadge(3, 6).textContent, '↓ 50%（前期 6）');
        assert.equal(comparisonBadge(4, 4).textContent, '與前期持平');
    },
    前期為空時對比副標留空() {
        const data = baseData();
        data.kpi.highRiskDaysPrevious = null;
        const cards = makeKpi(data).buildKpiCards();
        assert.equal(cards[0].sub, undefined);
    },

    // ── 處理進度條 ────────────────────────────────────────────────────────
    處理進度_總數為0顯示空狀態且沒有條() {
        const data = baseData({ handling: { openCount: 0, inProgressCount: 0, resolvedCount: 0, overdueCount: 0, totalCount: 0 }, trend: [] });
        const h = makeHandling(data);
        h.renderHandlingProgress();
        assert.equal(h.body.textContent, '此期間沒有風險日');
        assert.equal(collect(h.body, n => has(n, 'lf-stack-bar')).length, 0, '總數為 0 不畫條');
    },
    處理進度_三段相加等於總數且替代文字含三段數字() {
        const data = baseData({
            handling: { openCount: 130, inProgressCount: 30, resolvedCount: 50, overdueCount: 0, totalCount: 210 },
            trend: [{ highRisk: 134, mediumRisk: 76 }]
        });
        const h = makeHandling(data);
        h.renderHandlingProgress();

        const bars = collect(h.body, n => has(n, 'lf-stack-bar'));
        assert.equal(bars.length, 2, '處理狀態一條、風險層級一條');
        const status = bars[0];
        assert.equal(status.getAttribute('role'), 'img');
        const label = status.getAttribute('aria-label');
        const m = /已處理 (\d+)、處理中 (\d+)、未處理 (\d+)，共 (\d+)/.exec(label);
        assert(m, `aria-label 要寫出三段數字與總數：${label}`);
        assert.equal(Number(m[1]) + Number(m[2]) + Number(m[3]), Number(m[4]), '三段相加等於總數');
        assert.equal(Number(m[4]), 210);

        // 數字本身也在畫面上（不只靠顏色與替代文字）
        const text = h.body.textContent;
        for (const s of ['已處理 50', '處理中 30', '未處理 130', '高 134', '中 76']) assert(text.includes(s), `畫面上要有「${s}」：${text}`);

        // 寬度以 CSS 變數設定，三段合計 100%
        const shares = status.children.map(seg => Number(seg.style['--lf-share']));
        assert.equal(shares.length, 3);
        assert(Math.abs(shares.reduce((a, b) => a + b, 0) - 100) < 1e-9);
        assert(status.children.every(seg => seg.style['--lf-swatch']), '顏色以 --lf-swatch 傳入');
        assert.equal(status.children[0].getAttribute('style'), null, '不得用 style 屬性');
    },
    處理進度_零值段不畫但數字仍列出() {
        const data = baseData({
            handling: { openCount: 210, inProgressCount: 0, resolvedCount: 0, overdueCount: 0, totalCount: 210 },
            trend: [{ highRisk: 134, mediumRisk: 76 }]
        });
        const h = makeHandling(data);
        h.renderHandlingProgress();
        const status = collect(h.body, n => has(n, 'lf-stack-bar'))[0];
        assert.equal(status.children.length, 1, '只有未處理一段');
        assert(h.body.textContent.includes('已處理 0') && h.body.textContent.includes('處理中 0'));
        assert.match(status.getAttribute('aria-label'), /已處理 0、處理中 0、未處理 210/);
    },
    處理進度_中風險被隱藏時風險層級只列高() {
        const data = baseData({
            handling: { openCount: 5, inProgressCount: 0, resolvedCount: 0, overdueCount: 0, totalCount: 5 },
            trend: [{ highRisk: 5, mediumRisk: 0 }]
        });
        const h = makeHandling(data, ['高']);
        h.renderHandlingProgress();
        const risk = collect(h.body, n => has(n, 'lf-stack-bar'))[1];
        assert.equal(risk.getAttribute('aria-label'), '風險層級：高 5');
        assert(!risk._parent.textContent.includes('中 '), '中風險被隱藏時風險層級那一塊不列中');
        assert.equal(risk.children.length, 1);
    },

    // ── 排行標籤 ──────────────────────────────────────────────────────────
    排行標籤_有顯示名() {
        const { hostRankLabel } = build(['function hostRankLabel('], '', '{ hostRankLabel }');
        assert.equal(hostRankLabel({ hostName: '10.1.2.3', displayName: '檔案伺服器' }), '檔案伺服器（10.1.2.3）');
    },
    排行標籤_沒有顯示名() {
        const { hostRankLabel } = build(['function hostRankLabel('], '', '{ hostRankLabel }');
        assert.equal(hostRankLabel({ hostName: 'SRV-01', displayName: null }), 'SRV-01');
        assert.equal(hostRankLabel({ hostName: 'SRV-01' }), 'SRV-01');
        assert.equal(hostRankLabel({ hostName: 'SRV-01', displayName: '' }), 'SRV-01');
    },

    // ── 風險趨勢 ──────────────────────────────────────────────────────────
    趨勢圖是堆疊長條且沒有tension() {
        const points = [
            { date: '2026-11-08', highRisk: 3, mediumRisk: 1, errorCount: 0 },
            { date: '2026-11-09', highRisk: 0, mediumRisk: 2, errorCount: 1 }
        ];
        const t = makeTrend(points);
        t.renderTrendChart();
        assert.equal(t.calls.bar.length, 1, '要經 charts.bar 建立長條圖');
        assert.equal(t.calls.line, 0, '不得再用 charts.line');
        const spec = t.calls.bar[0];
        assert.deepEqual(spec.data.datasets.map(d => d.label), ['高風險', '中風險']);
        for (const ds of spec.data.datasets) {
            assert(!('tension' in ds), '長條圖不該有 tension');
            assert(!('borderColor' in ds), '長條圖不用折線的 borderColor');
        }
        assert.equal(spec.options.scales.x.stacked, true);
        assert.equal(spec.options.scales.y.stacked, true);
        assert.equal(spec.options.scales.x.ticks.autoSkip, true, '長期間靠 autoSkip 省略刻度');
        assert.deepEqual(spec.data.labels, ['11-08', '11-09']);
        // 下鑽：第一組是高、第二組是中
        const high = new URL(spec.drillTo({ index: 0, datasetIndex: 0 }), 'http://x');
        assert.equal(high.searchParams.get('riskLevels'), '高');
        assert.equal(high.searchParams.get('from'), '2026-11-08');
        assert.equal(new URL(spec.drillTo({ index: 1, datasetIndex: 1 }), 'http://x').searchParams.get('riskLevels'), '中');
        assert.equal(t.calls.toolbar, 1, '保留表格切換');
    },
    趨勢圖_中風險被隱藏時只畫高() {
        const t = makeTrend([{ date: '2026-11-08', highRisk: 3, mediumRisk: 0, errorCount: 0 }], { visibleRisk: ['高'] });
        t.renderTrendChart();
        assert.deepEqual(t.calls.bar[0].data.datasets.map(d => d.label), ['高風險']);
    },
    趨勢圖_期間全為0顯示無資料() {
        const t = makeTrend([
            { date: '2026-11-08', highRisk: 0, mediumRisk: 0, errorCount: 0 },
            { date: '2026-11-09', highRisk: 0, mediumRisk: 0, errorCount: 0 }
        ]);
        t.renderTrendChart();
        assert.equal(t.calls.bar.length, 0, '全為 0 不畫圖');
        assert.equal(t.calls.noData, 1);
    },

    // ── 工具列條件文字 ────────────────────────────────────────────────────
    工具列條件文字() {
        const { periodSummary } = build(['const PRESET_TITLES', 'const SCOPE_LABEL', 'function shortDate(', 'function periodSummary('], '', '{ periodSummary }');
        assert.deepEqual(
            periodSummary({ presetDays: 30, from: '2026-10-12', to: '2026-11-10', compare: 'previous', scope: 'all' }),
            { title: '近 30 天', meta: '10/12–11/10 · 對比前一期' });
        assert.deepEqual(
            periodSummary({ presetDays: null, from: '2026-10-01', to: '2026-10-05', compare: 'yoy', scope: 'unresolved' }),
            { title: '自訂期間', meta: '10/01–10/05 · 對比去年同期 · 僅未結案' });
        assert.equal(periodSummary({ presetDays: 1, from: '2026-11-10', to: '2026-11-10', compare: 'previous', scope: 'all' }).meta, '11/10 · 對比前一期');
        assert.equal(periodSummary({ presetDays: 90, from: '2025-12-12', to: '2026-03-10', compare: 'previous', scope: 'all' }).meta,
            '2025/12/12–2026/03/10 · 對比前一期', '跨年的區間要帶年份');
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
