/**
 * 報表（docs/WEB-SPEC.md §9.6）——主管與更高階主管看的期間面。
 *
 * 版面由上而下、由左而右就是閱讀順序：現在要不要擔心（第一排四格）→ 變好還是變壞（前期對比、風險趨勢）
 * → 風險集中在哪（主機排行、類型分布）→ 有沒有人在處理（處理進度）→ 這些數字可不可信（資料完整度）。
 *
 * §8.4 的驗收標準在這頁兌現：**任何一個數字，最多兩次點擊就能看到組成它的風險日清單**。
 * 實作方式是「組出帶篩選條件的網址再導頁」——問題查詢頁已支援 URL 同步，
 * 所以下鑽不需要在明細端寫任何程式碼。
 *
 * 結構約定（大螢幕模式與重新整理依賴它）：
 *   - 資料只經 loadReport() 載入（序號守門），載入成功後呼叫 renderAll()；
 *   - 所有圖表的建立集中在 renderCharts()，要重畫整頁只呼叫 renderAll()；
 *   - 工具列（#report-toolbar）、第一排（#report-kpi-area）、圖表區（#report-charts）各自可單獨隱藏。
 */

import { api as baseApi, getDisplaySettings, clearDisplaySettingsCache } from '../core/api.js';
import { appUrl, recordsUrl } from '../core/paths.js';
import { kpiStrip, toast, guardLoad, renderLoading, button } from '../core/ui.js';
import {
    formatNumber, categoryName, severityName, SEVERITY_ORDER,
    issueBaselineText, toLocalDateString
} from '../core/format.js';
import * as chartLib from '../core/charts.js';
import { bindRangeChips, rangeFromDays } from '../core/date-range.js';
import { createSeqGuard } from '../core/async-guards.js';
import { safeStorage } from '../core/storage.js';
import { filterDisclosure, roleText, fact, metaLine } from '../core/elements.js';
import { createWall } from './reports-wall.js';

let currentData = null;
// 保留天數來自第一次 summary 回覆；在那之前不做自動切換（寫死一個猜測值反而會猜錯）
let retentionDays = null;

// 「長區間」門檻：區間夠長才值得看年比而不是前一期。這是區間長度的語意，
// 與保留天數無關（以往這兩件事被混在同一個 180）
const LONG_RANGE_DAYS = 180;
// docs/archive/FEEDBACK-3-PLAN.md #8：資料母體已在後端 RecordRepository 過濾（KPI/排行表格數值
// 本來就正確），只有趨勢圖需要主動隱藏被藏等級的 series——否則圖例仍會列出一組
// 但整組恆為 0，容易被誤讀成「這期間真的沒有中風險日」而不是「被設定藏起來」
let visibleDayRisk = new Set(['高', '中', '低']);
const chartInstances = {};

// 處理狀態顯示範圍（§5）：單選，存 URL（可分享）；不入 localStorage——報表以「全部」為誠實預設
let currentScope = new URLSearchParams(location.search).get('handlingScope') || 'all';
// 使用者點了 chip 但尚未載入成功的範圍：查詢先用它，成功後才成為 currentScope、chip 才亮起
let requestedScope = null;

// 目前選的是哪一顆快捷期間（天數）；手動改日期後為 null（＝自訂期間）
let activePresetDays = null;
// 畫面上的資料是用哪個快捷期間與比較模式取回的（載入成功時記下）。條件文字只讀這份：
// 直接讀輸入欄的話，改了還沒按「套用」的值會被重繪（進大螢幕、視窗縮放）畫成已套用
let appliedView = { presetDays: null, compare: 'previous' };
// 「更多條件」揭露鈕（setupMoreFilters 建立）；條件變動後 refresh() 更新已套用數
let filterMore = null;

// ── 大螢幕模式的轉接（狀態機與排程在 reports-wall.js，見 §9.6「大螢幕模式」）──────────────

/** 大螢幕模式下 API 錯誤不跳 toast：沒有人在操作，通知只會在畫面上堆積；失敗狀態由標頭列呈現 */
const api = {
    ...baseApi,   // 其他方法照原樣，只覆寫 get
    get: (url, options) => baseApi.get(url, wall.isActive() ? { ...options, silent: true } : options)
};

// 本次重繪要不要做圖表進場動畫（renderAll 依模式決定）
let chartAnimate = true;

/** 圖表包裝層外加一道轉接：大螢幕模式生效時圖表不可下鑽；自動更新與縮放的重繪不做進場動畫 */
const charts = {
    ...chartLib,
    bar: (canvas, spec) => chartLib.bar(canvas, adaptChartSpec(spec))
};

function adaptChartSpec(spec) {
    const adapted = { ...spec };
    if (wall.isActive()) delete adapted.drillTo;
    if (!chartAnimate) adapted.options = { ...spec.options, animation: false };
    return adapted;
}

/**
 * 區間檢查先做、再放骨架列（回饋第 45 輪 B7）：這頁的 summary 是全站最慢的查詢之一，
 * 過去按下查詢到資料回來之間畫面完全沒有變化，使用者只能猜有沒有按到。
 * 檢查不過是直接 return，順序反了會留下一片永遠不會被換掉的骨架列。
 */
async function load() {
    const from = document.getElementById('report-from').value;
    const to = document.getElementById('report-to').value;

    if (from && to) {
        if (from > to) {
            requestedScope = null;   // 沒發出查詢就不留下想切的範圍，否則之後按「查詢」會用沒亮起的範圍
            toast('起日不可晚於迄日，請調整日期範圍', 'danger');
            return;
        }
        const fromDate = new Date(from);
        const toDate = new Date(to);
        const diffDays = Math.round((toDate - fromDate) / 86400000) + 1;
        if (diffDays > 366) {
            requestedScope = null;
            toast('查詢區間不可超過 366 天，請縮小範圍', 'danger');
            return;
        }
    }

    const kpiContainer = document.getElementById('report-kpi');
    renderLoading(kpiContainer, 4);
    // loadReport 成功渲染才回傳 true；失敗或被新查詢取代時 chip 不動，維持上一次成功的範圍
    if (await guardLoad(kpiContainer, loadReport)) syncScopeChips();
}

const reportGuard = createSeqGuard();   // 連按查詢或切換範圍時，慢的舊回應不得蓋掉新結果

/** 實際的取數與渲染；失敗與重試由 load() 的 guardLoad 負責 */
async function loadReport() {
    const scope = requestedScope ?? currentScope;
    const from = document.getElementById('report-from').value;
    const to = document.getElementById('report-to').value;
    const compare = document.getElementById('report-compare').value;
    const presetAtRequest = activePresetDays;

    const seq = reportGuard.next();
    let data;
    let displaySettings;
    try {
        [data, displaySettings] = await Promise.all([
            // 報表允許 366 天區間、非「全部」範圍走記憶體推導，大站台會超過預設 60 秒
            api.get(`/api/reports/summary?from=${from}&to=${to}&handlingScope=${scope}&compare=${compare}`, { timeoutMs: 180000 }),
            getDisplaySettings()
        ]);
    } catch (err) {
        // 過期請求的失敗不處理：失敗態只該屬於最新一次查詢
        if (!reportGuard.isCurrent(seq)) return;
        requestedScope = null;   // 失敗時 chip 與資料都停在上次成功的範圍，不留下沒生效的選擇
        throw err;
    }
    // 回應回來時已有更新的查詢就整份丟掉，不得寫入任何頁面狀態
    if (!reportGuard.isCurrent(seq)) return;
    currentData = data;
    appliedView = { presetDays: presetAtRequest, compare };
    retentionDays = data.retentionDays ?? retentionDays;
    updateYoyOptionLabel();
    // 範圍只在載入成功後才生效，失敗時 chip 與 currentScope 都維持上一次成功的狀態
    currentScope = data.handlingScope || 'all';
    requestedScope = null;
    visibleDayRisk = new Set(displaySettings?.visibleDayRiskLevels ?? ['高', '中', '低']);

    // 同步 URL（可複製分享）：scope=all 時不留參數，保持網址乾淨
    const params = new URLSearchParams(location.search);
    if (currentScope === 'all') params.delete('handlingScope'); else params.set('handlingScope', currentScope);
    history.replaceState(null, '', params.toString() ? `?${params}` : location.pathname);

    document.getElementById('print-title').textContent =
        `LogForesight 風險報表　${currentData.from} ～ ${currentData.to}` +
        (currentScope !== 'all' ? `（${SCOPE_LABEL[currentScope]}）` : '');

    renderAll(true);
    return true;
}

const SCOPE_LABEL = { unresolved: '未結案', open: '未處理', unassigned: '未指派' };

/** 依 scope 附加到下鑽 URL 的處理狀態條件——點進去的清單筆數與卡片數字對得上（§5） */
function scopeDrillParams() {
    if (currentScope === 'unresolved') return '&statuses=open,in_progress';
    if (currentScope === 'open') return '&statuses=open';
    if (currentScope === 'unassigned') return '&unassigned=true';
    return '';
}

/**
 * 重畫整頁的唯一入口（依 currentData）：工具列的條件文字、第一排四格、所有圖表。
 * 重新整理或切換大螢幕模式時只呼叫它，不必各自記得要重畫哪些區塊。
 * loaded：這次重繪來自一次成功載入（而非縮放或切換模式），完成後通知大螢幕記下更新時間。
 */
function renderAll(loaded = false) {
    // 大螢幕模式下，首次成功載入之後的重繪（自動更新、視窗縮放）只換數字與圖，不做進場動畫
    chartAnimate = !(wall.isActive() && wall.status().loads > 0);
    renderToolbarSummary();
    renderKpi();
    renderCharts();
    if (loaded) wall.dataLoaded();
}

// ── 工具列 ───────────────────────────────────────────────────────────────────

const PRESET_TITLES = { 1: '昨日', 7: '近 7 天', 30: '近 30 天', 90: '近 90 天' };

function shortDate(date, withYear) {
    return withYear ? date.replaceAll('-', '/') : date.slice(5).replace('-', '/');
}

/**
 * 工具列左側「目前條件」的兩段文字（純函式）：期間名稱（醒目）＋日期、比較基準與非「全部」的顯示範圍（次要）。
 * 例：「近 30 天」＋「10/12–11/10 · 對比前一期」；跨年的區間日期帶年份，避免 01/05–12/20 這種讀不出年的寫法。
 */
function periodSummary({ presetDays, from, to, compare, scope }) {
    const title = PRESET_TITLES[presetDays] ?? '自訂期間';
    const withYear = from.slice(0, 4) !== to.slice(0, 4);
    const range = from === to
        ? shortDate(from, withYear)
        : `${shortDate(from, withYear)}–${shortDate(to, withYear)}`;
    const parts = [range, compare === 'yoy' ? '對比去年同期' : '對比前一期'];
    if (scope && scope !== 'all' && SCOPE_LABEL[scope]) parts.push(`僅${SCOPE_LABEL[scope]}`);
    return { title, meta: parts.join(' · ') };
}

function renderToolbarSummary() {
    const summary = periodSummary({
        presetDays: appliedView.presetDays,
        from: currentData.from,
        to: currentData.to,
        compare: appliedView.compare,
        scope: currentScope
    });
    document.getElementById('report-period-title').textContent = summary.title;
    document.getElementById('report-period-meta').textContent = summary.meta;
    // 大螢幕標頭列顯示同一份條件文字
    document.getElementById('wall-period-title').textContent = summary.title;
    document.getElementById('wall-period-meta').textContent = summary.meta;
    filterMore?.refresh();
}

/** 「更多條件」已套用幾個：自訂日期 1、比較模式非預設 1、顯示範圍非「全部」1 */
function appliedFilterCount() {
    let count = 0;
    if (activePresetDays == null) count++;
    const days = rangeDays(document.getElementById('report-from').value, document.getElementById('report-to').value);
    if (document.getElementById('report-compare').value !== defaultCompareFor(days)) count++;
    if ((requestedScope ?? currentScope) !== 'all') count++;
    return count;
}

/** 次要條件收進「更多條件」：進頁已有套用中的條件（網址帶範圍、自訂日期）就預設展開 */
function setupMoreFilters() {
    const panel = document.getElementById('report-filter-panel');
    filterMore = filterDisclosure(panel, appliedFilterCount);
    document.getElementById('report-more-slot').replaceWith(filterMore.button);
}

// ── 第一排四格 ────────────────────────────────────────────────────────────────

/**
 * 第一排：高風險日／受影響主機／未結案風險日／資料完整度。
 * 語意色只用在需要注意的狀態（上升、逾期、缺漏、落後），正常狀態一律一般文字色。
 */
function renderKpi() {
    const container = document.getElementById('report-kpi');
    container.replaceChildren();

    // 兩種超出互斥（後端保證）：全部超出→數字不具參考價值；部分超出→數字偏低
    const retentionWarning = currentData.comparisonOutOfRetention
        ? '比較期間的資料已超過保留期，對比數字不具參考價值。'
        : currentData.comparisonPartiallyOutOfRetention
            ? '比較期間有一部分早於保留期，那段的資料已被清除，對比數字會偏低。'
            : null;

    if (retentionWarning) {
        const warning = document.createElement('div');
        warning.className = 'col-12';
        const msg = document.createElement('div');
        msg.className = 'small px-3 py-2 rounded lf-callout-warning';
        msg.textContent = retentionWarning;
        warning.appendChild(msg);
        container.appendChild(warning);
    }

    const col = document.createElement('div');
    col.className = 'col-12';
    // 一屏化：四張卡排同一排，不佔兩排高度（卡不夠寬時才自動換行）
    // 大螢幕上的數字不可點：卡片不帶下鑽網址，kpiStrip 就不會輸出連結
    const cards = buildKpiCards().map(card => (wall.isActive() ? { ...card, url: null } : card));
    col.appendChild(kpiStrip([{ cards }], { singleRow: true }));
    container.appendChild(col);
}

/** 與前期對比的副標；沒有前期資料時回 undefined（kpiStrip 保留空副標列） */
function compareSub(current, previous) {
    return (previous !== null && previous !== undefined) ? comparisonBadge(current, previous) : undefined;
}

function buildKpiCards() {
    const kpi = currentData.kpi;
    const range = { from: currentData.from, to: currentData.to };
    return [
        {
            label: '高風險日',
            value: formatNumber(kpi.highRiskDays),
            sub: compareSub(kpi.highRiskDays, kpi.highRiskDaysPrevious),
            // 下鑽帶上目前顯示範圍（§5）：點進去的清單與卡片數字對得上
            url: recordsUrl({ riskLevels: '高', ...range }) + scopeDrillParams()
        },
        {
            label: '受影響主機',
            value: `${formatNumber(kpi.affectedHosts)} / ${formatNumber(currentData.totalHosts)} 台`,
            sub: compareSub(kpi.affectedHosts, kpi.affectedHostsPrevious),
            url: recordsUrl({ riskLevels: '高,中', ...range }) + scopeDrillParams()
        },
        openRiskCard(),
        coverageCard(currentData.coverage)
    ];
}

/**
 * 未結案風險日＝處理中＋未處理（逾期是其中一部分，只在有逾期時才在副標提醒）。
 * 下鑽網址自帶處理狀態條件，並隨顯示範圍調整，所以不再附加 scopeDrillParams（會重複帶 statuses）。
 */
function openRiskCard() {
    const handling = currentData.handling;
    const overdue = handling.overdueCount;
    return {
        label: '未結案風險日',
        value: formatNumber(handling.openCount + handling.inProgressCount),
        sub: overdue > 0 ? roleText('lf-text-alert', `逾期 ${formatNumber(overdue)}`) : '',
        url: recordsUrl({
            statuses: currentScope === 'open' ? 'open' : 'open,in_progress',
            riskLevels: '高,中',
            from: currentData.from,
            to: currentData.to,
            unassigned: currentScope === 'unassigned' ? 'true' : null
        })
    };
}

/** 「資料至 MM/DD」：dataThrough 是 yyyy-MM-dd；沒有值（整段期間都沒資料）時只說落後 */
function laggingText(dataThrough) {
    return dataThrough ? `資料至 ${dataThrough.slice(5).replace('-', '/')}` : '資料落後';
}

/**
 * 資料完整度（純函式，回傳 kpiStrip 的一張卡）。「可不可信」是主管最後一個問題：
 *   - 整段期間都沒資料 → 「無資料」（次要灰）；
 *   - 缺漏主機日（期間內某台主機某天沒有分析紀錄）→ 「缺 N 主機日」（警示琥珀）；
 *   - 資料落後（最新資料早於應有的最後一天）→ 副標「資料至 MM/DD」（風險狀態文字），
 *     沒缺漏但落後時值仍是「完整」、卡片轉警示色，提醒「完整」只到那一天；
 *   - 其餘 → 「完整」，一般文字色。
 * 不可下鑽：它回答的是「這份報表可不可信」，不是一份清單。
 */
function coverageCard(coverage) {
    const card = {
        label: '資料完整度',
        title: '缺漏主機日：期間內某台主機某天沒有分析資料。資料落後：最新資料早於應有的最後一天。沒告警不等於沒問題。',
        url: null,
        sub: ''
    };

    if (coverage.noData) {
        return { ...card, value: '無資料', variant: 'secondary' };
    }

    const missing = coverage.missingHostDays > 0;
    // 數字要短（KPI 卡的大字折成兩行會把整排撐高）：單位與落後說明放副標
    const parts = [];
    if (missing) parts.push(roleText('lf-text-meta', '主機日沒有分析資料'));
    if (coverage.lagging) parts.push(roleText('lf-text-alert', laggingText(coverage.dataThrough)));
    let sub = '';
    if (parts.length === 1) sub = parts[0];
    else if (parts.length > 1) { sub = document.createElement('div'); sub.append(...parts.flatMap((p, i) => (i ? [document.createTextNode('・'), p] : [p]))); }
    return {
        ...card,
        value: missing ? `缺 ${formatNumber(coverage.missingHostDays)}` : '完整',
        variant: (missing || coverage.lagging) ? 'warning' : undefined,
        sub
    };
}

/**
 * 與前期對比。注意方向：告警數上升是**變壞**，所以上升用紅色——
 * 一般儀表板「上升＝綠色」的直覺在這裡是反的。
 */
function comparisonBadge(current, previous) {
    const wrap = document.createElement('div');

    if (previous === 0 && current === 0) {
        wrap.className = 'lf-text-meta';
        wrap.textContent = '與前期相同';
        return wrap;
    }

    if (previous === 0) {
        wrap.className = 'lf-text-alert';
        wrap.textContent = `↑ 前期為 0`;
        return wrap;
    }

    const delta = current - previous;
    const percent = Math.round((delta / previous) * 100);

    if (delta === 0) {
        wrap.className = 'lf-text-meta';
        wrap.textContent = '與前期持平';
    } else if (delta > 0) {
        wrap.className = 'lf-text-alert';
        wrap.textContent = `↑ ${percent}%（前期 ${formatNumber(previous)}）`;
    } else {
        wrap.className = 'lf-text-meta';
        wrap.textContent = `↓ ${Math.abs(percent)}%（前期 ${formatNumber(previous)}）`;
    }

    return wrap;
}

// ── 圖表區 ───────────────────────────────────────────────────────────────────

/**
 * 圖表區的所有繪製集中在這裡。顯示範圍非「全部」時處理進度卡整張隱藏
 * （母體已抽掉已處理，進度恆為 0%／100%，無資訊量），趨勢卡補滿該列。
 */
function renderCharts() {
    const showHandling = currentScope === 'all';
    document.getElementById('handling-section').classList.toggle('d-none', !showHandling);
    document.getElementById('report-row-top').classList.toggle('lf-report-page__row--solo', !showHandling);

    renderTrendChart();
    if (showHandling) renderHandlingProgress();
    renderHostChart();
    renderCategoryChart();
}

/** 掛上「表格」切換：重畫後一律回到圖表視圖，不留下上一次的「表格」狀態（否則整張卡只剩空白） */
function mountToolbar(container, wrapper, spec) {
    wrapper.classList.remove('d-none');
    charts.attachToolbar(container, { canvasWrapper: wrapper, ...spec });
}

/** 沒資料時收掉上一次的表格切換：舊按鈕留著會讓人切出上一期的數字 */
function clearToolbar(container, wrapper) {
    mountToolbar(container, wrapper, { title: '', tableColumns: [], tableRows: [] });
    container.replaceChildren();
}

/** 長標籤在座標軸上截斷（完整文字仍在提示與表格）；量測不得出現溢出卡片 */
const MAX_TICK_CHARS = 18;
const NARROW_TICK_CHARS = 9;
/** 大螢幕圖表字級的額外倍率：1080 高時座標約 15px、2160 高時約 31px */
const WALL_CHART_FONT_BOOST = 1.4;
function truncateTick(value) {
    const label = String(this.getLabelForValue(value));
    // 窄畫布（手機單欄）放不下完整長度的標籤，會被畫布左緣裁掉：依畫布寬度收短
    const max = (this.chart?.width ?? Infinity) < 480 ? NARROW_TICK_CHARS : MAX_TICK_CHARS;
    return label.length > max ? `${label.slice(0, max - 1)}…` : label;
}

/**
 * 風險趨勢：逐日堆疊長條（高在下、中在上）。期間很長時不另做彙總，交給座標軸 autoSkip 省略刻度。
 * 沒有 tension——那是折線圖的設定，長條圖不該有。
 */
function renderTrendChart() {
    const points = currentData.trend;
    const wrapper = document.getElementById('trend-wrapper');
    const toolbar = document.getElementById('trend-toolbar');

    if (points.length === 0 || points.every(p => p.highRisk + p.mediumRisk === 0)) {
        // 沒資料也要收掉上一次的 Chart 實例：canvas 只是藏起來，舊實例不收會一直活著
        chartInstances.trend?.destroy();
        chartInstances.trend = null;
        clearToolbar(toolbar, wrapper);
        charts.renderNoData(wrapper);
        return;
    }

    const risk = charts.riskColors();

    // 中風險被顯示設定藏起來時整組不畫——資料母體已在後端過濾，mediumRisk 恆為 0
    const datasets = [
        { label: '高風險', data: points.map(p => p.highRisk), backgroundColor: risk['高'] }
    ];
    if (visibleDayRisk.has('中')) {
        datasets.push({ label: '中風險', data: points.map(p => p.mediumRisk), backgroundColor: risk['中'] });
    }

    chartInstances.trend?.destroy();
    chartInstances.trend = charts.bar(document.getElementById('trend-chart'), {
        data: {
            labels: points.map(p => p.date.slice(5)),
            datasets
        },
        options: {
            scales: {
                x: { stacked: true, grid: { display: false }, ticks: { autoSkip: true, maxRotation: 0 } },
                y: { stacked: true, beginAtZero: true, ticks: { precision: 0 } }
            }
        },
        // 下鑽：點某天的長條 → 該日該風險層級的清單（datasetIndex 1 只在中風險存在時出現）
        drillTo: point => {
            const day = points[point.index];
            const level = point.datasetIndex === 0 ? '高' : '中';
            // 顯示範圍不是「全部」時，長條只算該範圍的風險日：下鑽帶同一個範圍，點進去的筆數才對得上
            return recordsUrl({ riskLevels: level, from: day.date, to: day.date }) + scopeDrillParams();
        }
    });

    mountToolbar(toolbar, wrapper, {
        title: '風險趨勢',
        tableColumns: ['日期', '高風險', '中風險', '錯誤數'],
        tableRows: points.map(p => [p.date, p.highRisk, p.mediumRisk, p.errorCount])
    });
}

// ── 處理進度 ─────────────────────────────────────────────────────────────────

/**
 * 一條橫向堆疊條＋條下方的「標籤 數字」。純 HTML：寬度以 CSS 變數 --lf-share（百分比）傳入，
 * 顏色以 --lf-swatch 傳入，不寫 style 屬性、不用 innerHTML。條本身 role="img" 並帶可讀的替代文字，
 * 數字也直接寫在畫面上（不靠顏色分辨）。
 * segments: [{ label, value, color }]；值為 0 的段不畫但仍列出數字。
 */
function progressBlock(title, ariaLabel, segments) {
    const block = document.createElement('div');
    block.className = 'lf-report-progress__block';

    const heading = document.createElement('div');
    heading.className = 'lf-text-label';
    heading.textContent = title;

    const sum = segments.reduce((total, seg) => total + seg.value, 0);
    const bar = document.createElement('div');
    bar.className = 'lf-stack-bar';
    bar.setAttribute('role', 'img');
    bar.setAttribute('aria-label', ariaLabel);
    for (const seg of segments) {
        if (seg.value <= 0) continue;
        const el = document.createElement('span');
        el.className = 'lf-stack-bar__seg';
        el.style.setProperty('--lf-share', String((seg.value / sum) * 100));
        el.style.setProperty('--lf-swatch', seg.color);
        bar.appendChild(el);
    }

    const facts = metaLine(segments.map(seg => {
        const item = document.createElement('span');
        const dot = document.createElement('span');
        dot.className = 'lf-progress-dot';
        dot.style.setProperty('--lf-swatch', seg.color);
        item.append(dot, fact(seg.label, formatNumber(seg.value)));
        return item;
    }));

    block.append(heading, bar);
    if (facts) block.appendChild(facts);
    return block;
}

/**
 * 處理進度：兩條堆疊條。
 *   - 第一條：已處理／處理中／未處理（三段相加＝期間內高＋中風險日總數；母體與儀表板待辦同一套規則）。
 *   - 第二條：風險層級（高、中；中風險被顯示設定隱藏時只列高）。
 * 總數為 0 時只寫一句「此期間沒有風險日」，不畫 0% 的條。
 */
function renderHandlingProgress() {
    const body = document.getElementById('handling-body');
    const handling = currentData.handling;
    const total = handling.totalCount;
    body.replaceChildren();

    if (!(total > 0)) {
        const empty = document.createElement('p');
        empty.className = 'mb-0';
        empty.textContent = '此期間沒有風險日';
        body.appendChild(empty);
        return;
    }

    const status = charts.statusColors();
    const risk = charts.riskColors();
    const resolved = handling.resolvedCount;
    const inProgress = handling.inProgressCount;
    const open = Math.max(total - resolved - inProgress, 0);

    body.appendChild(progressBlock(
        '處理狀態',
        `處理狀態：已處理 ${resolved}、處理中 ${inProgress}、未處理 ${open}，共 ${total} 個風險日`,
        [
            { label: '已處理', value: resolved, color: status.success },
            { label: '處理中', value: inProgress, color: status.inProgress },
            { label: '未處理', value: open, color: status.unhandled }
        ]));

    const high = currentData.trend.reduce((sum, p) => sum + p.highRisk, 0);
    const medium = visibleDayRisk.has('中') ? currentData.trend.reduce((sum, p) => sum + p.mediumRisk, 0) : null;
    if (high + (medium ?? 0) > 0) {
        const levels = [{ label: '高', value: high, color: risk['高'] }];
        if (medium !== null) levels.push({ label: '中', value: medium, color: risk['中'] });
        body.appendChild(progressBlock(
            '風險層級',
            `風險層級：${levels.map(l => `${l.label} ${l.value}`).join('、')}`,
            levels));
    }
}

// ── 排行與類型分布 ────────────────────────────────────────────────────────────

function renderCategoryChart() {
    const categories = currentData.categories;
    const wrapper = document.getElementById('category-wrapper');
    const toolbar = document.getElementById('category-toolbar');

    // 標題列的「N 個問題」：原本第一排的「問題總數」搬到這裡，下鑽沿用原本的網址
    const total = document.getElementById('category-issue-total');
    total.textContent = `${formatNumber(currentData.kpi.totalIssues)} 個問題`;
    // 問題總數含低風險日的問題，下鑽顯式帶全部風險層級，免得問題查詢的「預設隱藏低風險」把數字對不上
    // 大螢幕模式下只顯示數字：沒有 href 的 <a> 不可聚焦也不可點
    if (wall.isActive()) total.removeAttribute('href');
    else total.href = appUrl(recordsUrl({ riskLevels: '高,中,低', from: currentData.from, to: currentData.to }) + scopeDrillParams());
    total.hidden = false;

    // 風險類型分布走 SQL 端聚合（回饋十九輪批次D），與依問題視角的問題排行同一個限制：
    // 母體是跨主機跨日的獨立投影，套用「顯示範圍」會把「這個類型有幾個風險資訊」變成
    // 「符合這個處理狀態的日子裡有幾個」，那是另一個問題的答案——這裡照樣講清楚，
    // 不要讓數字看起來像回應了篩選卻其實沒有
    const subtitle = document.getElementById('category-subtitle');
    if (subtitle) subtitle.textContent = currentScope !== 'all' ? '不受「顯示範圍」篩選影響' : '';

    if (categories.length === 0) {
        // 沒資料也要收掉上一次的 Chart 實例：canvas 只是藏起來，舊實例不收會一直活著
        chartInstances.category?.destroy();
        chartInstances.category = null;
        clearToolbar(toolbar, wrapper);
        charts.renderNoData(wrapper);
        return;
    }

    const severity = charts.severityColors();

    // 類別 × 嚴重度的堆疊長條——這正是 lf_record_categories 需要嚴重度分解欄位的原因（§10.3）
    // severity 存英文原值（下鑽 URL 參數、取色皆用它）；label 只用於畫面顯示。
    // 由共用的 SEVERITY_ORDER 推導（S11），不再各頁各寫一份同順序的清單
    const severityKeys = SEVERITY_ORDER.map(s => ({ key: `${s[0].toLowerCase()}${s.slice(1)}Count`, severity: s }));

    chartInstances.category?.destroy();
    chartInstances.category = charts.bar(document.getElementById('category-chart'), {
        data: {
            labels: categories.map(c => categoryName(c.category)),
            datasets: severityKeys.map(s => ({
                label: severityName(s.severity),
                data: categories.map(c => c[s.key]),
                backgroundColor: severity[s.severity]
            }))
        },
        options: {
            indexAxis: 'y',
            scales: {
                x: { stacked: true, beginAtZero: true, ticks: { precision: 0 } },
                y: { stacked: true, grid: { display: false } }
            }
        },
        drillTo: point => {
            const category = categories[point.index];
            const severityValue = severityKeys[point.datasetIndex].severity;
            // 類型分布跨全部風險日統計（嚴重度是問題層級，與當日風險等級無關），
            // 下鑽顯式帶全部風險層級，否則點 Low 段會被預設隱藏低風險過濾成近乎空白
            return recordsUrl({ categories: category.category, severity: severityValue,
                                riskLevels: '高,中,低', from: currentData.from, to: currentData.to });
        }
    });

    mountToolbar(toolbar, wrapper, {
        title: '風險類型分布',
        // 問題數＝去重風險資訊筆數（回饋十九輪批次D），高/中/低三欄依此口徑分桶、三者之和＝問題數
        tableColumns: ['類型', '高', '中', '低', '問題數', '期間累計', '主機數'],
        tableRows: categories.map(c => [
            categoryName(c.category),
            c.highCount, c.mediumCount, c.lowCount, c.riskItemCount, c.cumulativeCount, c.affectedHosts
        ])
    });
}

/**
 * 排行卡的視角（docs/archive/FEEDBACK-11-PLAN.md §8-2）：'host' ｜ 'issue'。
 * 狀態存 localStorage，預設主機——要看問題排行的人切一次就記住。
 */
const RANK_MODE_KEY = 'lf.reports.rankMode';
let rankMode = safeStorage.get(RANK_MODE_KEY) === 'issue' ? 'issue' : 'host';

function bindRankModeToggle() {
    const toggle = document.getElementById('rank-mode-toggle');
    if (!toggle) return;

    for (const btn of toggle.querySelectorAll('[data-rank-mode]')) {
        btn.classList.toggle('active', btn.dataset.rankMode === rankMode);
        btn.addEventListener('click', () => {
            if (rankMode === btn.dataset.rankMode) return;
            rankMode = btn.dataset.rankMode;
            safeStorage.set(RANK_MODE_KEY, rankMode);
            for (const other of toggle.querySelectorAll('[data-rank-mode]')) {
                other.classList.toggle('active', other.dataset.rankMode === rankMode);
            }
            if (currentData) renderHostChart();   // 資料還沒載入（或首次載入失敗）時只記住選擇，載入後照它畫
        });
    }
}

/**
 * 主機在排行上的標籤：有顯示名時為「顯示名（主機名）」，沒有時只寫主機名。
 * NetIQ 主機的主機名是 IP，單看 IP 認不出是哪一台，顯示名補上辨識，主機名留著對得上其他畫面。
 */
function hostRankLabel(host) {
    return host.displayName ? `${host.displayName}（${host.hostName}）` : host.hostName;
}

/** 排行卡：依 rankMode 分派給主機或問題兩種呈現（同一張卡、同一個 canvas 容器） */
function renderHostChart() {
    document.getElementById('rank-title').textContent = rankMode === 'issue' ? '問題排行' : '主機排行';
    if (rankMode === 'issue') {
        renderIssueRankChart();
        return;
    }

    const hosts = currentData.hostRanking;
    const others = currentData.others;
    const wrapper = document.getElementById('host-wrapper');
    const toolbar = document.getElementById('host-toolbar');

    renderHostRankMeta();

    if (hosts.length === 0) {
        // 沒資料也要收掉上一次的 Chart 實例：canvas 只是藏起來，舊實例不收會一直活著
        chartInstances.host?.destroy();
        chartInstances.host = null;
        clearToolbar(toolbar, wrapper);
        charts.renderNoData(wrapper, '此期間沒有風險主機');
        return;
    }

    // 主機量大時，Top 10 之外的主機併成一條「其他 N 台」——尾端主機不會完全隱形，
    // 也看得出前 10 佔整體多少。這一條不可下鑽（它是彙總，不是單一主機）。
    const labels = hosts.map(hostRankLabel);
    const high = hosts.map(h => h.highRiskDays);
    const medium = hosts.map(h => h.mediumRiskDays);
    if (others) {
        labels.push(`其他 ${others.hostCount} 台`);
        high.push(others.highRiskDays);
        medium.push(others.mediumRiskDays);
    }

    const risk = charts.riskColors();
    chartInstances.host?.destroy();
    chartInstances.host = charts.bar(document.getElementById('host-chart'), {
        data: {
            labels,
            datasets: [
                { label: '高風險日', data: high, backgroundColor: risk['高'] },
                { label: '中風險日', data: medium, backgroundColor: risk['中'] }
            ]
        },
        options: {
            indexAxis: 'y',
            scales: {
                x: { stacked: true, beginAtZero: true, ticks: { precision: 0 } },
                y: { stacked: true, grid: { display: false }, ticks: { callback: truncateTick } }
            }
        },
        drillTo: point => {
            const host = hosts[point.index];   // 「其他」條沒有對應 hosts 元素，回 null 不下鑽
            return host && host.hostId > 0 ? `/hosts/${host.hostId}` : null;
        }
    });

    // 表格模式（工具列切換）：Top 10 逐台＋「其他」彙總列；完整逐台清單走「查看全部」
    // 連到問題查詢的依主機視角（伺服器端排行只回 Top 10＋彙總，不整包搬運）
    const tableRows = hosts.map(h => [hostRankLabel(h), h.highRiskDays, h.mediumRiskDays, h.correlationDays, h.latestHeadline]);
    if (others) {
        tableRows.push([`其他 ${others.hostCount} 台（彙總）`, others.highRiskDays, others.mediumRiskDays, '', '']);
    }

    mountToolbar(toolbar, wrapper, {
        title: '主機排行',
        tableColumns: ['主機', '高風險日', '中風險日', '關聯訊號日', '最新狀況'],
        tableRows
    });
}

/**
 * 問題排行（§8-2）：一條長條＝一個問題（Source＋EventId，與依問題視角同一把分組鍵），
 * 長度＝期間內的事件總次數，下鑽到問題查詢的依問題視角。
 */
function renderIssueRankChart() {
    const issues = currentData.issueRanking ?? [];
    const others = currentData.issueOthers;
    const wrapper = document.getElementById('host-wrapper');
    const toolbar = document.getElementById('host-toolbar');

    renderIssueRankMeta();

    if (issues.length === 0) {
        // 沒資料也要收掉上一次的 Chart 實例：canvas 只是藏起來，舊實例不收會一直活著
        chartInstances.host?.destroy();
        chartInstances.host = null;
        clearToolbar(toolbar, wrapper);
        charts.renderNoData(wrapper, '此期間沒有問題事件');
        return;
    }

    const labels = issues.map(i => `${i.source} (${i.eventId})`);
    const counts = issues.map(i => i.totalCount);
    if (others) {
        labels.push(`其他 ${others.issueCount} 個問題`);
        counts.push(others.totalCount);
    }

    chartInstances.host?.destroy();
    chartInstances.host = charts.bar(document.getElementById('host-chart'), {
        data: {
            labels,
            datasets: [{ label: '事件次數', data: counts, backgroundColor: charts.riskColors()['高'] }]
        },
        options: {
            indexAxis: 'y',
            scales: {
                x: { beginAtZero: true, ticks: { precision: 0 } },
                y: { grid: { display: false }, ticks: { callback: truncateTick } }
            }
        },
        drillTo: point => {
            const issue = issues[point.index];   // 「其他」條是彙總，不下鑽
            return issue
                ? recordsUrl({ view: 'issue', source: issue.source, eventId: issue.eventId,
                               riskLevels: '高,中,低', from: currentData.from, to: currentData.to })
                : null;
        }
    });

    const tableRows = issues.map(i => [
        `${i.source} (${i.eventId})`, i.priorityScore.toFixed(0), categoryName(i.category),
        severityName(i.maxSeverity), i.hostCount, i.dayCount, i.totalCount,
        issueBaselineText(i), i.fleetFirstSeen || i.firstSeen
    ]);
    if (others) {
        tableRows.push([`其他 ${others.issueCount} 個問題（彙總）`, '', '', '', others.hostCount, '', others.totalCount, '', '']);
    }

    mountToolbar(toolbar, wrapper, {
        title: '問題排行',
        // 排序已改依分數（§G3，見 IssueRankingBuilder.Build），這裡只是把數字亮出來讓人看得懂
        // 為什麼是這個順序——不重算，直接顯示後端算好的分數
        tableColumns: ['問題', '分數', '分類', '最高嚴重度', '主機數', '風險日數', '事件次數', 'vs 基準', '首見（機房）'],
        tableRows
    });
}

function renderIssueRankMeta() {
    const subtitle = document.getElementById('host-rank-subtitle');
    const viewAll = document.getElementById('host-view-all');

    const count = currentData.rankedIssueCount ?? 0;

    // **問題排行不受上方「顯示範圍」影響，必須講出來**（體檢 D6）：
    // scope 是**日層級**的過濾（「這一天處理完了沒」），而問題聚合是跨主機跨日的投影；
    // 兩者母體不同，套用 scope 會把「這個問題影響幾台」變成「符合這個處理狀態的日子裡影響幾台」，
    // 那是另一個問題的答案。但頁面上只有一個範圍選擇器，使用者的心智模型是「它管整頁」——
    // 選了「未處理」看到 KPI 歸零、問題排行卻仍是全部時，畫面等於在說謊。
    // 在能以「未處理主機數」正確套用之前（§10.6），至少要誠實說明。
    const scopeNote = currentScope !== 'all' ? '；不受「顯示範圍」篩選影響' : '';

    // 背景整理中時數字偏低但看起來正常（G2）——與 scope 說明合併在同一行副標題，
    // 不另外插入節點（這張卡的容器由圖表渲染接管，多插的節點會被蓋掉）
    const pendingNote = currentData.issueStatsPending
        ? `；${currentData.issueStatsPendingHint ?? '統計整理中，數字可能不完整'}`
        : '';

    // §10.6：全部主機都已有結論的問題不佔用排行版面，卡底同一行誠實說出排除了幾筆
    // 靜音中的問題同樣不列入排行（docs/archive/FEEDBACK-47-PLAN.md 15.3 (2)），同一行續接
    const concludedCount = currentData.concludedIssueCount > 0 ? currentData.concludedIssueCount : 0;
    const mutedCount = currentData.mutedIssueCount > 0 ? currentData.mutedIssueCount : 0;
    const concludedNote = concludedCount && mutedCount
        ? `；另有 ${concludedCount} 個問題已有結論、${mutedCount} 個靜音中（未列入）`
        : concludedCount
            ? `；另有 ${concludedCount} 個問題已有結論（未列入）`
            : mutedCount
                ? `；另有 ${mutedCount} 個問題靜音中（未列入）`
                : '';

    subtitle.textContent = count > 0 ? `共 ${count} 個問題${scopeNote}${pendingNote}${concludedNote}` : '';

    if (currentData.issueOthers) {
        viewAll.href = appUrl(recordsUrl({ view: 'issue', riskLevels: '高,中,低', from: currentData.from, to: currentData.to }));
        viewAll.classList.remove('d-none');
    } else {
        viewAll.classList.add('d-none');
    }
}

/** 主機排行的「查看全部」連結（連到問題查詢的依主機視角，同一段期間）；副標清空 */
function renderHostRankMeta() {
    const subtitle = document.getElementById('host-rank-subtitle');
    const viewAll = document.getElementById('host-view-all');
    // 主機模式不寫「共 N 台」：第一排「受影響主機」已經是這個數字，重複只會把標頭擠成兩列
    subtitle.textContent = '';

    // 有主機被 Top 10 擋在外面時才顯示「查看全部」，沒有就別給多餘的出口
    if (currentData.others) {
        viewAll.href = appUrl(recordsUrl({ view: 'host', riskLevels: '高,中', from: currentData.from, to: currentData.to }));
        viewAll.classList.remove('d-none');
    } else {
        viewAll.classList.add('d-none');
    }
}

// ── 期間控制 ─────────────────────────────────────────────────────────────────

document.getElementById('report-form').addEventListener('submit', event => {
    event.preventDefault();
    load();
});

bindRangeChips({ onApply: ({ days }) => { setRange(days); load(); } });

document.getElementById('btn-print-report').addEventListener('click', () => window.print());
document.getElementById('btn-report-fullscreen').addEventListener('click', () => wall.enter());
document.getElementById('wall-exit').appendChild(button('離開大螢幕', { onClick: () => wall.exit() }));

// 處理狀態顯示範圍（§5）：單選 chip，切換即重載（母體改變，全圖表跟著變）
document.getElementById('report-scope-chips').addEventListener('click', onScopeChipClick);

function onScopeChipClick(event) {
    const btn = event.target.closest('button[data-scope]');
    if (!btn) return;
    if (btn.classList.contains('active')) {
        // 已亮起的 chip 也是「取消」出口：有尚未生效的選擇就撤掉，改用目前範圍重查（舊的在途請求由序號守門丟棄）
        if (requestedScope === null) return;
        requestedScope = null;
        load();
        return;
    }
    requestedScope = btn.dataset.scope;
    load();
}

/** chip 的 active 一律對齊 currentScope（進頁與每次載入成功後呼叫） */
function syncScopeChips() {
    for (const btn of document.querySelectorAll('#report-scope-chips button')) {
        btn.classList.toggle('active', btn.dataset.scope === currentScope);
    }
}
syncScopeChips();

let userSelectedCompare = false;

document.getElementById('report-compare').addEventListener('change', () => {
    userSelectedCompare = true;
    filterMore?.refresh();
});

/** 起訖日期字串算出含頭含尾的天數；任一為空回 null */
function rangeDays(from, to) {
    if (!from || !to) return null;
    return Math.round((new Date(to) - new Date(from)) / 86400000) + 1;
}

/**
 * 預設的比較模式。只有「保留天數足以涵蓋去年同期」時，長區間才自動切 yoy：
 * 保留 180 天的站台，去年同期永遠是空的，自動切過去只會得到一頁零。
 * previous 就算殘缺也還有部分資料，配上保留期提示比切到全空模式誠實。
 * retentionDays 還沒拿到（首次查詢前）時一律維持 previous。
 */
function defaultCompareFor(days) {
    if (days == null || days < LONG_RANGE_DAYS) return 'previous';
    if (retentionDays == null) return 'previous';
    return retentionDays >= 365 + days ? 'yoy' : 'previous';
}

/** 去年同期落在保留期外時，在下拉選項上標註——仍可選，只是事先告知會是空的 */
function updateYoyOptionLabel() {
    const option = document.querySelector('#report-compare option[value="yoy"]');
    if (!option) return;

    const base = '對比去年同期';
    const to = document.getElementById('report-to').value;
    if (retentionDays == null || !to) { option.textContent = base; return; }

    // 兩邊都正規化成本地日期字串再比：new Date('YYYY-MM-DD') 是 UTC 午夜、
    // new Date() 帶當下時分，直接比對會在邊界日與後端的純日期判定分岐
    const retentionLine = new Date();
    retentionLine.setDate(retentionLine.getDate() - retentionDays);
    const yoyTo = new Date(to);
    yoyTo.setFullYear(yoyTo.getFullYear() - 1);

    const outOfRetention = toLocalDateString(yoyTo) < toLocalDateString(retentionLine);
    option.textContent = outOfRetention ? `${base}（超出保留期）` : base;
}

function updateDefaultCompare() {
    updateYoyOptionLabel();
    if (userSelectedCompare) return;
    const days = rangeDays(document.getElementById('report-from').value, document.getElementById('report-to').value);
    if (days != null) {
        document.getElementById('report-compare').value = defaultCompareFor(days);
    }
}

// 手動改日期＝自訂期間：快捷期間不再成立，「更多條件」的已套用數要跟著變
function onDateEdited() {
    activePresetDays = null;
    updateDefaultCompare();
    filterMore?.refresh();
}

document.getElementById('report-from').addEventListener('change', onDateEdited);
document.getElementById('report-to').addEventListener('change', onDateEdited);

function setRange(days) {
    // 期間的計算（錨在昨天、本地日期）在 core/date-range.js，三頁共用
    const { from, to } = rangeFromDays(days);
    document.getElementById('report-from').value = from;
    document.getElementById('report-to').value = to;
    activePresetDays = days;

    if (!userSelectedCompare) {
        document.getElementById('report-compare').value = defaultCompareFor(days);
    }
    updateYoyOptionLabel();
    filterMore?.refresh();   // 已套用數跟著輸入欄走，不等載入回來（請求最久 180 秒）
}

// ── 大螢幕模式 ───────────────────────────────────────────────────────────────

/** 大螢幕每一輪自動更新：快捷期間是滾動的（跨過午夜自然前進一天），自訂日期固定不動 */
async function wallRefresh() {
    if (activePresetDays != null) setRange(activePresetDays);
    clearDisplaySettingsCache();   // 大螢幕整天不重新整理：管理者改了顯示的風險等級，下一輪更新就要跟上
    try {
        return (await loadReport()) === true;
    } catch {
        return false;   // 保留畫面上的舊資料；失敗狀態由 wall 記下、標頭列呈現，下一輪照常再試
    }
}

/** 標頭列的更新時間與失敗提示、工具列「大螢幕」鈕的顯示（寬度不足 992px 時不提供） */
function syncWallUi() {
    document.getElementById('btn-report-fullscreen').hidden = !wall.canEnter();
    const { updated, stale } = wall.statusText();
    document.getElementById('wall-updated').textContent = updated;
    document.getElementById('wall-stale').textContent = stale;
}

const wall = createWall({
    win: window,
    doc: document,
    loc: location,
    hist: history,
    now: () => Date.now(),
    setTimer: (fn, ms) => setTimeout(fn, ms),
    clearTimer: handle => clearTimeout(handle),
    refresh: wallRefresh,
    rerender: () => { if (currentData) renderAll(); },
    applyScale: scale => {
        // 圖表字（座標 11px）在 1080 高的大螢幕遠看太小：與根字級同步放大（大螢幕根字級 18px＝一般 15px 的 1.2 倍，圖表再多給一些）
        chartLib.setFontScale(scale === null ? 1 : scale * WALL_CHART_FONT_BOOST);
        const rootStyle = document.documentElement.style;
        if (scale === null) rootStyle.removeProperty('--lf-wall-scale');
        else rootStyle.setProperty('--lf-wall-scale', String(scale));
    },
    setExitVisible: visible => document.getElementById('wall-exit').classList.toggle('is-visible', visible),
    onState: syncWallUi
});

/** 首次載入：大螢幕要知道它何時結束（成功或失敗），失敗也要排下一輪重試 */
async function initialLoad() {
    const before = wall.status().loads;
    wall.loadStarted();
    try {
        await load();
    } finally {
        wall.loadSettled(wall.status().loads > before);
    }
}

bindRankModeToggle();
setRange(30);
setupMoreFilters();
wall.init();
initialLoad();
