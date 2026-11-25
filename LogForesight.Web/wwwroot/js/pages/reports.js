/**
 * 報表（docs/WEB-SPEC.md §9.6）——主管與更高階主管看的期間面。
 *
 * 同一頁三個視角（分段控制，網址 view=）：總覽（這支檔案）、處理成效（reports-effectiveness.js）、
 * 問題類型（reports-category.js）。處理成效與問題類型延後載入——切過去才打各自的端點；期間條件改變時
 * 只重載目前的視角，其他視角標記為待重載。各視角的載入序號守門與列印補載入在 reports-common.js。
 *
 * 總覽的版面由上而下、由左而右就是閱讀順序：現在要不要擔心（第一排四格）→ 變好還是變壞（前期對比、風險趨勢）
 * → 風險集中在哪（主機排行、問題排行）→ 有沒有人在處理（處理進度）→ 這些數字可不可信（資料完整度）。
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
import { kpiStrip, toast, guardLoad, renderLoading, button, cssVar } from '../core/ui.js';
import {
    formatNumber, categoryName, severityName,
    issueBaselineText, toLocalDateString
} from '../core/format.js';
import * as chartLib from '../core/charts.js';
import { bindRangeChips, rangeFromDays } from '../core/date-range.js';
import { createSeqGuard } from '../core/async-guards.js';
import { filterDisclosure, roleText, fact, metaLine } from '../core/elements.js';
import { createWall } from './reports-wall.js';
import {
    VIEWS, viewFromSearch, searchWithView, normalizeView, rangeProblem, createViewState, loadViewData,
    printAllViews, createWallViewKeeper
} from './reports-common.js';
import { createEffectivenessView } from './reports-effectiveness.js';
import { createCategoryView } from './reports-category.js';

let currentData = null;
// 三個視角各自的載入狀態（序號守門、是否載入過、是否失敗、是否待重載）；總覽的資料在 currentData
const views = { overview: createViewState(), effectiveness: createViewState(), category: createViewState() };
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
    bar: (canvas, spec) => chartLib.bar(canvas, adaptChartSpec(spec)),
    line: (canvas, spec) => chartLib.line(canvas, adaptChartSpec(spec))
};

function adaptChartSpec(spec) {
    const adapted = { ...spec };
    if (wall.isActive()) delete adapted.drillTo;
    if (!chartAnimate) adapted.options = { ...spec.options, animation: false };
    return adapted;
}

/**
 * 總覽的載入入口（其他視角走 loadOtherView）。
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
    views.overview.dirty = false;   // 發出時就清：在途期間別的視角改了期間，重新標上的待重載不能被這次回應抹掉
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
        views.overview.dirty = true;   // 畫面上仍是舊條件的資料，切回總覽要再載入
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

    renderAll(true);
    return true;
}

const SCOPE_LABEL = { unresolved: '未結案', open: '未處理', unassigned: '未指派' };

/** 列印標題：三個視角一起印，顯示範圍只套用在總覽，紙上要寫明（畫面上的提示在不列印的切換列裡） */
function printTitle(from, to) {
    return `LogForesight 風險報表　${from} ～ ${to}` +
        (currentScope !== 'all' ? `（總覽：${SCOPE_LABEL[currentScope]}；處理成效與問題類型不套用顯示範圍）` : '');
}

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
    if (loaded) {
        views.overview.loaded = true;   // 總覽跟著這次成功載入更新（含大螢幕自動更新）；待重載標記在發出請求時已清
        views.overview.failed = false;
        // 列印補載入失敗時標上的「此部分未能載入」要跟著清掉，否則之後重載成功、再列印仍只印那一句
        document.getElementById('view-overview').classList.remove('lf-report-view--failed');
        // 列印補載入隱藏中的總覽時不動共用的標題與期間（那些屬於目前顯示的視角）
        if (currentView === 'overview') {
            document.getElementById('print-title').textContent = printTitle(currentData.from, currentData.to);
            noteAppliedRange(currentData.from, currentData.to);
        }
        wall.dataLoaded();
    }
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

/** range：要顯示的起訖（預設取總覽資料；處理成效、問題類型視角傳入自己回應的起訖） */
function renderToolbarSummary(range = currentData) {
    const summary = periodSummary({
        presetDays: appliedView.presetDays,
        from: range.from,
        to: range.to,
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

    // 上升不上紅色（變化不是風險狀態，DESIGN-SYSTEM §8.1）：靠箭頭辨識
    if (previous === 0) {
        wrap.textContent = `↑ 前期為 0`;
        return wrap;
    }

    const delta = current - previous;
    const percent = Math.round((delta / previous) * 100);

    if (delta === 0) {
        wrap.className = 'lf-text-meta';
        wrap.textContent = '與前期持平';
    } else if (delta > 0) {
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
    renderIssueRankChart();
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
function truncateTick(value) {
    return truncateLabel(this.getLabelForValue(value), this.chart);
}

/** 窄畫布（手機單欄）放不下完整長度的標籤，會被畫布左緣裁掉：依畫布寬度收短 */
function truncateLabel(text, chart) {
    const label = String(text);
    const max = (chart?.width ?? Infinity) < 480 ? NARROW_TICK_CHARS : MAX_TICK_CHARS;
    return label.length > max ? `${label.slice(0, max - 1)}…` : label;
}

/**
 * 風險趨勢：逐日折線，最多三條——高風險實線＋淡面積、中風險實線、前期高風險虛線。
 * 線型不同，不只靠顏色區分（色盲安全）。中風險被顯示設定隱藏時不畫；前期超出保留期（previousTrend 為空）時
 * 不畫前期線，保留期提示仍由第一排上方的警示顯示。期間很長時不另做彙總，交給座標軸 autoSkip 省略刻度。
 * 點資料點下鑽：某天、某風險層級的清單；前期線的點下鑽到前期那一天。
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
    const highSoft = cssVar('--lf-risk-high-soft');

    // 前期以「期間第幾天」與本期對齊：前期比本期短（資料被保留期截掉一部分）時，對不到的天留空
    const previous = new Map((currentData.previousTrend ?? []).map(p => [p.dayIndex, p]));
    const showPrevious = previous.size > 0;
    const showMedium = visibleDayRisk.has('中');   // 中風險被顯示設定藏起來時整條不畫（後端母體已過濾，恆為 0）

    // 資料點小而可點：游標靠近就算命中（pointHitRadius），不必精準對準那個點
    const line = { tension: 0, pointRadius: 2, pointHoverRadius: 5, pointHitRadius: 8 };
    const datasets = [{
        ...line, label: '高風險', kind: 'high', data: points.map(p => p.highRisk),
        borderColor: risk['高'], backgroundColor: highSoft, fill: true, borderWidth: 2
    }];
    if (showMedium) {
        datasets.push({
            ...line, label: '中風險', kind: 'medium', data: points.map(p => p.mediumRisk),
            borderColor: risk['中'], backgroundColor: risk['中'], fill: false, borderWidth: 2
        });
    }
    if (showPrevious) {
        datasets.push({
            ...line, label: '前期高風險', kind: 'previous', pointRadius: 0,
            data: points.map((_, i) => previous.get(i)?.highRisk ?? null),
            borderColor: risk['高'], backgroundColor: risk['高'], fill: false, borderWidth: 1.5, borderDash: [6, 4]
        });
    }
    const kinds = datasets.map(d => d.kind);

    chartInstances.trend?.destroy();
    chartInstances.trend = charts.line(document.getElementById('trend-chart'), {
        data: {
            labels: points.map(p => p.date.slice(5)),
            datasets: datasets.map(({ kind, ...dataset }) => dataset)
        },
        options: {
            // 點哪個資料點就下鑽哪一條線：只在游標貼近資料點時命中，不是整條垂直帶
            interaction: { mode: 'nearest', intersect: true },
            scales: {
                x: { grid: { display: false }, ticks: { autoSkip: true, maxRotation: 0 } },
                y: { beginAtZero: true, ticks: { precision: 0 } }
            }
        },
        drillTo: point => {
            const kind = kinds[point.datasetIndex];
            const day = kind === 'previous' ? previous.get(point.index) : points[point.index];
            if (!day) return null;
            // 前期早於保留線的日子（highRisk 為 null）沒有資料可下鑽
            if (kind === 'previous' && day.highRisk == null) return null;
            // 顯示範圍不是「全部」時，線只算該範圍的風險日：下鑽帶同一個範圍，點進去的筆數才對得上
            const level = kind === 'medium' ? '中' : '高';
            return recordsUrl({ riskLevels: level, from: day.date, to: day.date }) + scopeDrillParams();
        }
    });

    mountToolbar(toolbar, wrapper, {
        title: '風險趨勢',
        tableColumns: showPrevious ? ['日期', '高風險', '中風險', '錯誤數', '前期高風險'] : ['日期', '高風險', '中風險', '錯誤數'],
        tableRows: points.map((p, i) => showPrevious
            ? [p.date, p.highRisk, p.mediumRisk, p.errorCount, previous.get(i)?.highRisk ?? '']
            : [p.date, p.highRisk, p.mediumRisk, p.errorCount])
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

// ── 排行（主機排行、問題排行並排兩張卡）────────────────────────────────────────

/** 排行條收窄、留白加大：排名比較靠長度，不靠一整片實心色塊 */
const RANK_BAR = { barPercentage: 0.62, categoryPercentage: 0.8, maxBarThickness: 28 };

/**
 * 主機在排行上的標籤：有顯示名時為「顯示名（主機名）」，沒有時只寫主機名。
 * NetIQ 主機的主機名是 IP，單看 IP 認不出是哪一台，顯示名補上辨識，主機名留著對得上其他畫面。
 */
function hostRankLabel(host) {
    return host.displayName ? `${host.displayName}（${host.hostName}）` : host.hostName;
}

/** 座標軸上只寫顯示名（有的話），完整的「顯示名（主機名）」留在提示框與表格：括號裡的主機名讓軸標籤長到被截斷 */
function hostAxisLabel(host) {
    return host.displayName || host.hostName;
}

/** 主機排行卡 */
function renderHostChart() {
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
    const axisLabels = hosts.map(hostAxisLabel);
    const high = hosts.map(h => h.highRiskDays);
    const medium = hosts.map(h => h.mediumRiskDays);
    if (others) {
        labels.push(`其他 ${others.hostCount} 台`);
        axisLabels.push(`其他 ${others.hostCount} 台`);
        high.push(others.highRiskDays);
        medium.push(others.mediumRiskDays);
    }

    const risk = charts.riskColors();
    chartInstances.host?.destroy();
    chartInstances.host = charts.bar(document.getElementById('host-chart'), {
        data: {
            labels,
            datasets: [
                { label: '高風險日', data: high, backgroundColor: risk['高'], ...RANK_BAR },
                { label: '中風險日', data: medium, backgroundColor: risk['中'], ...RANK_BAR }
            ]
        },
        options: {
            indexAxis: 'y',
            scales: {
                x: { stacked: true, beginAtZero: true, ticks: { precision: 0 } },
                y: { stacked: true, grid: { display: false }, ticks: { callback(value) { return truncateLabel(axisLabels[value] ?? this.getLabelForValue(value), this.chart); } } }
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
 * 問題排行卡（§8-2）：一條長條＝一個問題（Source＋EventId，與依問題視角同一把分組鍵），
 * 長度＝期間內的事件總次數，下鑽到問題查詢的依問題視角。
 */
function renderIssueRankChart() {
    const issues = currentData.issueRanking ?? [];
    const others = currentData.issueOthers;
    const wrapper = document.getElementById('issue-wrapper');
    const toolbar = document.getElementById('issue-toolbar');

    renderIssueRankMeta();

    if (issues.length === 0) {
        // 沒資料也要收掉上一次的 Chart 實例：canvas 只是藏起來，舊實例不收會一直活著
        chartInstances.issue?.destroy();
        chartInstances.issue = null;
        clearToolbar(toolbar, wrapper);
        charts.renderNoData(wrapper, '此期間沒有問題事件');
        return;
    }

    const labels = issues.map(i => i.issueLabel);
    const counts = issues.map(i => i.totalCount);
    if (others) {
        labels.push(`其他 ${others.issueCount} 個問題`);
        counts.push(others.totalCount);
    }

    chartInstances.issue?.destroy();
    chartInstances.issue = charts.bar(document.getElementById('issue-chart'), {
        data: {
            labels,
            datasets: [{ label: '事件次數', data: counts, backgroundColor: charts.riskColors()['高'], ...RANK_BAR }]
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
        i.issueLabel, i.priorityScore.toFixed(0), categoryName(i.category),
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
    const subtitle = document.getElementById('issue-rank-subtitle');
    const viewAll = document.getElementById('issue-view-all');

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
    subtitle.title = subtitle.textContent;   // 版面窄時單行省略，完整文字在提示

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
    // 主機排行不寫「共 N 台」：第一排「受影響主機」已經是這個數字，重複只會把標頭擠成兩列
    subtitle.textContent = '';

    // 有主機被 Top 10 擋在外面時才顯示「查看全部」，沒有就別給多餘的出口
    if (currentData.others) {
        viewAll.href = appUrl(recordsUrl({ view: 'host', riskLevels: '高,中', from: currentData.from, to: currentData.to }));
        viewAll.classList.remove('d-none');
    } else {
        viewAll.classList.add('d-none');
    }
}

// ── 視角（總覽／處理成效／問題類型）──────────────────────────────────────────────

/** 每個視角載入中與失敗狀態的容器（骨架列與「重試」鈕畫在這裡） */
const VIEW_CONTAINER = {
    overview: 'report-kpi',
    effectiveness: 'eff-status',
    category: 'cat-heat-body'
};

const effectivenessView = createEffectivenessView({ charts, progressBlock, readToken: cssVar });
const categoryView = createCategoryView();

/** 視角渲染的出口：總覽由 renderAll 負責（資料在 currentData） */
const SUBVIEW_RENDER = {
    effectiveness: data => effectivenessView.render(data),
    category: data => categoryView.render(data)
};

const SUBVIEW_ENDPOINT = {
    effectiveness: '/api/reports/effectiveness',
    category: '/api/reports/category-trend'
};

/** 目前顯示的視角；網址 view= 決定（無效值退回總覽） */
let currentView = viewFromSearch(location.search);

/** 取一個視角的資料需要載入嗎：沒載入過、上次失敗、或期間條件已變動（待重載） */
function needsLoad(view) {
    const state = views[view];
    return !state.loaded || state.failed || state.dirty;
}

/** 期間條件（或範圍）變動後：目前視角之外的其他視角標記為待重載，切過去才載入 */
function markOtherViewsDirty() {
    for (const view of VIEWS) {
        if (view !== currentView) views[view].dirty = true;
    }
}

/**
 * 目前的視角以這組起訖成功載入。與上一次不同就把其他視角標成待重載——
 * 輸入欄改了日期、沒按「查詢」就切視角時，新視角會用新日期載入，其他視角不重載的話工具列與各視角的數字會對不起來。
 */
let appliedRange = null;
function noteAppliedRange(from, to) {
    const range = `${from}|${to}`;
    if (appliedRange !== null && appliedRange !== range) markOtherViewsDirty();
    appliedRange = range;
}

/** 「此視角不套用顯示範圍」：在處理成效與問題類型，且顯示範圍不是「全部」時才出現（總覽套用範圍，沒有這個誤會） */
function syncViewNotes() {
    const note = document.getElementById('report-view-note');
    if (note) note.hidden = !(currentView !== 'overview' && currentScope !== 'all');
}

/** 只換面板與切換鈕的狀態，不動網址也不載入（大螢幕進出與初次載入用） */
function applyView(view) {
    currentView = normalizeView(view);
    for (const name of VIEWS) {
        document.getElementById(`view-${name}`).classList.toggle('d-none', name !== currentView);
    }
    for (const btn of document.querySelectorAll('#report-view-switch button')) {
        const on = btn.dataset.view === currentView;
        btn.classList.toggle('active', on);
        btn.setAttribute('aria-pressed', String(on));
    }
    syncViewNotes();
}

/** 使用者切換視角：換面板、寫網址（view=，總覽不留參數）、需要時才載入（延後載入） */
function setView(view) {
    const next = normalizeView(view);
    if (next === currentView) return;
    applyView(next);
    history.replaceState(null, '', searchWithView(location.search, next) || location.pathname);
    if (needsLoad(next)) loadCurrentView();
}

/** 載入目前的視角（不動其他視角的待重載標記：單純切換視角時其他視角的資料仍然有效） */
function loadCurrentView() {
    return currentView === 'overview' ? load() : loadOtherView(currentView);
}

/** 以目前條件重載目前的視角（套用條件、改快捷期間、改顯示範圍）；其他視角標記為待重載，切過去才載入 */
function reload() {
    markOtherViewsDirty();
    return loadCurrentView();
}

/**
 * 處理成效、問題類型的載入入口：與 load() 同一套流程（先檢查區間、再放骨架列、失敗可重試、成功才讓範圍 chip 生效），
 * 檢查文字與 load() 相同，由 rangeProblem 提供。
 */
async function loadOtherView(view) {
    const problem = rangeProblem(document.getElementById('report-from').value, document.getElementById('report-to').value);
    if (problem) {
        requestedScope = null;   // 沒發出查詢就不留下沒生效的範圍
        toast(problem, 'danger');
        return;
    }
    const container = document.getElementById(VIEW_CONTAINER[view]);
    // 載入中與失敗時收起這個視角的圖表：舊期間的數字留在骨架列或「重試」下面，看起來像目前條件的結果
    document.getElementById(`view-${view}`).classList.add('lf-report-view--pending');
    renderLoading(container, 3);
    if (await guardLoad(container, () => loadSubView(view))) syncScopeChips();
}

/**
 * 載入處理成效或問題類型：兩者只看期間（不套用顯示範圍、不分比較模式），各自有序號守門——
 * 快速連切視角或連按套用時，慢的舊回應不得蓋掉新結果。成功才讓範圍 chip 生效（與總覽同一規則）。
 */
async function loadSubView(view) {
    const from = document.getElementById('report-from').value;
    const to = document.getElementById('report-to').value;
    const compare = document.getElementById('report-compare').value;
    const presetAtRequest = activePresetDays;
    const scope = requestedScope ?? currentScope;
    const state = views[view];

    let result;
    try {
        result = await loadViewData(state, () => api.get(`${SUBVIEW_ENDPOINT[view]}?from=${from}&to=${to}`, { timeoutMs: 180000 }));
    } catch (err) {
        requestedScope = null;   // loadViewData 只對最新一次請求擲例外：失敗時不留下沒生效的範圍
        document.getElementById(`view-${view}`).classList.add('lf-report-view--failed');
        throw err;
    }
    if (result.stale) return;   // 已有更新的請求：整份丟掉，不寫入任何頁面狀態

    const { data } = result;
    document.getElementById(`view-${view}`).classList.remove('lf-report-view--failed', 'lf-report-view--pending');
    if (view !== currentView) {
        // 使用者已切到別的視角（或列印補載入隱藏中的視角）：只畫這個面板，不改共用的條件、範圍、網址與工具列摘要
        document.getElementById(VIEW_CONTAINER[view]).replaceChildren();
        SUBVIEW_RENDER[view](data);
        return true;
    }
    appliedView = { presetDays: presetAtRequest, compare };
    currentScope = scope;
    requestedScope = null;
    noteAppliedRange(data.from, data.to);

    // 範圍存網址（與總覽同一規則：all 不留參數）；view 參數不動
    const params = new URLSearchParams(location.search);
    if (currentScope === 'all') params.delete('handlingScope'); else params.set('handlingScope', currentScope);
    history.replaceState(null, '', params.toString() ? `?${params}` : location.pathname);

    document.getElementById('print-title').textContent = printTitle(data.from, data.to);

    renderToolbarSummary(data);
    syncViewNotes();
    document.getElementById(VIEW_CONTAINER[view]).replaceChildren();   // 收掉骨架列
    SUBVIEW_RENDER[view](data);
    return true;
}

// ── 列印：三個視角依序全部印出 ───────────────────────────────────────────────

const PRINT_ALL_CLASS = 'lf-report-print-all';

/** 列印前補載入的單一視角（不動目前的視角、不標記其他視角待重載） */
async function loadForPrint(view) {
    const rendered = view === 'overview' ? await loadReport() : await loadSubView(view);
    // 回應被更新的請求取代（補載入途中使用者切到這個視角又發了一次）：這次沒有畫出任何東西，
    // 當成未能載入，不能讓列印把還在骨架列的畫面當成功印出去
    if (!rendered) throw new Error('列印補載入已被更新的請求取代');
}

/** 等兩個畫面更新（版面與圖表重排）；背景分頁不會觸發 requestAnimationFrame，所以另設上限 */
function nextFrames() {
    return new Promise(resolve => {
        const timer = setTimeout(resolve, 300);
        requestAnimationFrame(() => requestAnimationFrame(() => { clearTimeout(timer); resolve(); }));
    });
}

/** 進行中的列印（連按「列印」時沿用同一次，不重複補載入、不開第二個列印視窗） */
let printInFlight = null;

function printReport() {
    if (printInFlight) return printInFlight;
    const page = document.querySelector('.lf-report-page');
    const cleanup = () => page.classList.remove(PRINT_ALL_CLASS);
    printInFlight = printAllViews({
        views: VIEWS,
        needsLoad: view => needsLoad(view),
        load: loadForPrint,
        onResult: (view, ok) => {
            document.getElementById(`view-${view}`).classList.toggle('lf-report-view--failed', !ok);
            if (!ok) views[view].failed = true;
        },
        // 三個視角都要在輸出裡：列印用的類別讓隱藏中的視角也顯示；印完（或取消）還原。
        // 原本隱藏的視角裡的圖表是在 0 寬度下畫的，顯示後要等版面穩定、讓圖表（responsive）依新尺寸重排才印
        beforePrint: async () => {
            page.classList.add(PRINT_ALL_CLASS);
            window.addEventListener('afterprint', cleanup, { once: true });
            await nextFrames();
        },
        print: () => window.print()
    }).finally(() => { printInFlight = null; });
    return printInFlight;
}

// ── 期間控制 ─────────────────────────────────────────────────────────────────

document.getElementById('report-form').addEventListener('submit', event => {
    event.preventDefault();
    reload();
});

bindRangeChips({ onApply: ({ days }) => { setRange(days); reload(); } });

document.getElementById('report-view-switch').addEventListener('click', event => {
    const btn = event.target.closest('button[data-view]');
    if (btn) setView(btn.dataset.view);
});

document.getElementById('btn-print-report').addEventListener('click', printReport);
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
        reload();
        return;
    }
    requestedScope = btn.dataset.scope;
    reload();
}

/** chip 的 active 一律對齊 currentScope（進頁與每次載入成功後呼叫） */
function syncScopeChips() {
    for (const btn of document.querySelectorAll('#report-scope-chips button')) {
        btn.classList.toggle('active', btn.dataset.scope === currentScope);
    }
    syncViewNotes();
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
    if (activePresetDays != null) {
        setRange(activePresetDays);
        markOtherViewsDirty();   // 跨過午夜期間前進一天：離開大螢幕回到的其他視角也要換成新期間
    }
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
        // 圖表字級跟著根字級（--lf-wall-scale）與樣式表的大螢幕 token 變化，倍率改變後由 rerender 重建圖表
        const rootStyle = document.documentElement.style;
        if (scale === null) rootStyle.removeProperty('--lf-wall-scale');
        else rootStyle.setProperty('--lf-wall-scale', String(scale));
    },
    setExitVisible: visible => document.getElementById('wall-exit').classList.toggle('is-visible', visible),
    onState: syncWallUi,
    // 大螢幕只顯示總覽：進入時先切回總覽（記下原視角），離開時回到原視角；牆上不輪播
    onActiveChange: active => (active ? wallViews.enter() : wallViews.leave())
});

const wallViews = createWallViewKeeper({
    getView: () => currentView,
    show: view => {
        applyView(view);
        // 進入大螢幕時總覽可能還沒載入（從其他視角直接進來）：補載入，並讓大螢幕的排程接手
        if (initialStarted && needsLoad(view)) trackedLoad();
    },
    restore: view => setView(view)
});

/** 載入總覽並通知大螢幕：它要知道這次載入何時結束（成功或失敗），失敗也要排下一輪重試 */
async function trackedLoad() {
    const before = wall.status().loads;
    wall.loadStarted();
    try {
        await reload();
    } finally {
        wall.loadSettled(wall.status().loads > before);
    }
}

/** 首次載入：停在總覽時走大螢幕的載入追蹤；停在其他視角時只載入那個視角（大螢幕只看總覽，與它無關） */
async function initialLoad() {
    initialStarted = true;
    if (currentView === 'overview') return trackedLoad();
    return reload();
}

let initialStarted = false;
applyView(currentView);
setRange(30);
setupMoreFilters();
wall.init();   // 網址帶 display=wall 且寬度足夠時，這裡會先把視角切回總覽
initialLoad();
