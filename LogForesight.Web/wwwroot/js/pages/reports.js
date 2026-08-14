/**
 * 報表（docs/WEB-SPEC.md §9.6）——主管的主要畫面。
 *
 * §8.4 的驗收標準在這頁兌現：**任何一個數字，最多兩次點擊就能看到組成它的風險日清單**。
 * 實作方式是「組出帶篩選條件的網址再導頁」——問題查詢頁已支援 URL 同步，
 * 所以下鑽不需要在明細端寫任何程式碼。
 *
 * 2026-07-27 改版（docs/archive/HISTORY.md #6）：
 *   - 原本獨占一張大卡的「風險層級占比」改與新增的「受影響主機占比」「處理進度」
 *     並列成一列三顆小圖，騰出的版面讓主機告警排行放寬成整列。
 *   - 圖表註冊表 + 自訂圖表 modal：使用者自行決定要顯示哪些圖表，勾選狀態存 localStorage，
 *     隱藏的圖不呼叫 render（省一次 Chart.js 建構），重新勾選時才 lazy render。
 *   - 列印沿用畫面狀態：隱藏的圖表卡片是 d-none，本來就不會出現在列印結果裡。
 */

import { api, getDisplaySettings } from '../core/api.js';
import { statCard } from '../core/ui.js';
import { formatNumber, CATEGORY_NAMES, severityName, SEVERITY_ORDER, toLocalDateString, analysisAnchorLocal } from '../core/format.js';
import * as charts from '../core/charts.js';

let currentData = null;
// docs/archive/FEEDBACK-3-PLAN.md #8：資料母體已在後端 RecordRepository 過濾（KPI/排行表格數值
// 本來就正確），只有趨勢圖需要主動隱藏被藏等級的 series——否則 legend 仍會列出一條
// 圖例但整條線恆為 0，容易被誤讀成「這期間真的沒有中風險日」而不是「被設定藏起來」
let visibleDayRisk = new Set(['高', '中', '低']);
const chartInstances = {};

// ── 圖表可見性（自訂圖表 modal）──────────────────────────────────────────────

const VISIBLE_CHARTS_STORAGE_KEY = 'lf.reports.visibleCharts';

/** {id, title, sectionId, render}：id 是 localStorage 與 checkbox 的鍵，sectionId 是外層 col 的容器 id */
const CHART_REGISTRY = [
    { id: 'trend', title: '告警數量趨勢', sectionId: 'trend-section', render: renderTrendChart },
    { id: 'category', title: '風險類型分布', sectionId: 'category-section', render: renderCategoryChart },
    { id: 'host', title: '主機告警排行', sectionId: 'host-section', render: renderHostChart },
    { id: 'risk', title: '風險層級占比', sectionId: 'risk-section', render: renderRiskChart },
    { id: 'affected-hosts', title: '受影響主機占比', sectionId: 'affected-hosts-section', render: renderAffectedHostsChart },
    { id: 'handling-progress', title: '處理進度', sectionId: 'handling-progress-section', render: renderHandlingProgressChart }
];

/** 壞資料（手改過的 localStorage、舊格式）一律當作未設定，退回全開，不讓一筆壞值把整頁圖表藏光 */
function loadVisibleCharts() {
    try {
        const stored = JSON.parse(localStorage.getItem(VISIBLE_CHARTS_STORAGE_KEY));
        if (Array.isArray(stored)) return new Set(stored);
    } catch { /* 忽略壞資料 */ }
    return new Set(CHART_REGISTRY.map(c => c.id));
}

let visibleCharts = loadVisibleCharts();

function saveVisibleCharts() {
    localStorage.setItem(VISIBLE_CHARTS_STORAGE_KEY, JSON.stringify([...visibleCharts]));
}

/** scope≠all 時「處理進度」無資訊量（母體已抽掉已處理，恆 0%/100%）——不論自訂圖表是否勾選都隱藏 */
function isChartHidden(chart) {
    if (chart.id === 'handling-progress' && currentScope !== 'all') return true;
    return !visibleCharts.has(chart.id);
}

function applyChartVisibility() {
    for (const chart of CHART_REGISTRY) {
        document.getElementById(chart.sectionId).classList.toggle('d-none', isChartHidden(chart));
    }
}

/** 只對目前可見的圖表呼叫 render——隱藏的圖不必花這次 Chart.js 建構的成本 */
function renderVisibleCharts() {
    applyChartVisibility();
    for (const chart of CHART_REGISTRY) {
        if (!isChartHidden(chart)) chart.render();
    }
}

/** 自訂圖表 modal：checkbox 逐圖勾選，切換即時生效（存 localStorage＋切換可見度＋新顯示的圖 lazy render） */
function renderChartPickerBody() {
    const body = document.getElementById('chart-picker-body');
    body.replaceChildren();

    for (const chart of CHART_REGISTRY) {
        // docs/archive/FEEDBACK-5-PLAN.md §7：modal-body 是 row row-cols-2 grid，每個選項要包一層 col
        const col = document.createElement('div');
        col.className = 'col';

        const wrap = document.createElement('div');
        wrap.className = 'form-check';

        const input = document.createElement('input');
        input.type = 'checkbox';
        input.className = 'form-check-input';
        input.id = `chart-picker-${chart.id}`;
        input.checked = visibleCharts.has(chart.id);

        const label = document.createElement('label');
        label.className = 'form-check-label';
        label.htmlFor = input.id;
        label.textContent = chart.title;

        input.addEventListener('change', () => {
            if (input.checked) {
                visibleCharts.add(chart.id);
            } else {
                visibleCharts.delete(chart.id);
            }
            saveVisibleCharts();
            applyChartVisibility();
            // 重新顯示時才 render：render 函式內部已對 chartInstances 做 destroy-then-create，
            // 重複呼叫是安全的，不需要額外判斷「是否已建立過」
            if (input.checked) chart.render();
        });

        wrap.append(input, label);
        col.appendChild(wrap);
        body.appendChild(col);
    }
}

// 處理狀態顯示範圍（§5）：單選，存 URL（可分享）；不入 localStorage——報表以「全部」為誠實預設
let currentScope = new URLSearchParams(location.search).get('handlingScope') || 'all';

async function load() {
    const from = document.getElementById('report-from').value;
    const to = document.getElementById('report-to').value;

    const [data, displaySettings] = await Promise.all([
        api.get(`/api/reports/summary?from=${from}&to=${to}&handlingScope=${currentScope}`),
        getDisplaySettings()
    ]);
    currentData = data;
    currentScope = data.handlingScope || 'all';
    visibleDayRisk = new Set(displaySettings?.visibleDayRiskLevels ?? ['高', '中', '低']);

    // 同步 URL（可複製分享）：scope=all 時不留參數，保持網址乾淨
    const params = new URLSearchParams(location.search);
    if (currentScope === 'all') params.delete('handlingScope'); else params.set('handlingScope', currentScope);
    history.replaceState(null, '', params.toString() ? `?${params}` : location.pathname);

    document.getElementById('print-title').textContent =
        `LogForesight 風險報表　${currentData.from} ～ ${currentData.to}` +
        (currentScope !== 'all' ? `（${SCOPE_LABEL[currentScope]}）` : '');

    // scope≠all 時「處理進度」小圖無資訊量（母體已抽掉已處理）——由 applyChartVisibility 統一隱藏
    renderKpi();
    renderVisibleCharts();
}

const SCOPE_LABEL = { unresolved: '未結案', open: '未處理', unassigned: '未指派' };

/** 依 scope 附加到下鑽 URL 的處理狀態條件——點進去的清單筆數與卡片數字對得上（§5） */
function scopeDrillParams() {
    if (currentScope === 'unresolved') return '&statuses=open,in_progress';
    if (currentScope === 'open') return '&statuses=open';
    if (currentScope === 'unassigned') return '&unassigned=true';
    return '';
}

/** KPI 卡：帶與前一等長期間的對比——主管要的不是數字本身，是「變好還是變壞」 */
function renderKpi() {
    const kpi = currentData.kpi;
    const cards = [
        {
            label: '問題總數',
            value: kpi.totalIssues,
            previous: kpi.totalIssuesPrevious,
            // 問題總數含低風險日的問題，下鑽顯式帶全部風險層級，
            // 免得問題查詢的「預設隱藏低風險」把數字對不上
            url: `/records?riskLevels=${encodeURIComponent('高,中,低')}&from=${currentData.from}&to=${currentData.to}`
        },
        {
            label: '高風險日',
            value: kpi.highRiskDays,
            previous: kpi.highRiskDaysPrevious,
            url: `/records?riskLevels=${encodeURIComponent('高')}&from=${currentData.from}&to=${currentData.to}`
        },
        {
            label: '受影響主機',
            value: kpi.affectedHosts,
            previous: kpi.affectedHostsPrevious,
            url: `/records?riskLevels=${encodeURIComponent('高,中')}&from=${currentData.from}&to=${currentData.to}`
        },
        {
            label: '涵蓋率缺口天數',
            value: kpi.coverageGapDays,
            previous: null,
            hint: '資料不完整或 Security log 未讀取——沒告警不等於沒問題',
            url: null
        }
    ];

    const container = document.getElementById('report-kpi');
    container.replaceChildren();

    for (const card of cards) {
        const col = document.createElement('div');
        col.className = 'col-6 col-lg-3';
        col.appendChild(statCard({
            value: formatNumber(card.value),
            label: card.label,
            // 下鑽帶上目前顯示範圍（§5）：點進去的清單與卡片數字對得上（KPI 卡本身不帶 statuses，附加不衝突）
            url: card.url ? card.url + scopeDrillParams() : null,
            hint: card.hint,
            centered: false,
            extra: (card.previous !== null && card.previous !== undefined)
                ? comparisonBadge(card.value, card.previous)
                : undefined
        }));
        container.appendChild(col);
    }
}

/**
 * 與前期對比。注意方向：告警數上升是**變壞**，所以上升用紅色——
 * 一般儀表板「上升＝綠色」的直覺在這裡是反的。
 */
function comparisonBadge(current, previous) {
    const wrap = document.createElement('div');
    wrap.className = 'small mt-2';

    if (previous === 0 && current === 0) {
        wrap.className += ' text-muted';
        wrap.textContent = '與前期相同';
        return wrap;
    }

    if (previous === 0) {
        wrap.className += ' text-danger';
        wrap.textContent = `↑ 前期為 0`;
        return wrap;
    }

    const delta = current - previous;
    const percent = Math.round((delta / previous) * 100);

    if (delta === 0) {
        wrap.className += ' text-muted';
        wrap.textContent = '與前期持平';
    } else if (delta > 0) {
        wrap.className += ' text-danger';
        wrap.textContent = `↑ ${percent}%（前期 ${formatNumber(previous)}）`;
    } else {
        wrap.className += ' text-success';
        wrap.textContent = `↓ ${Math.abs(percent)}%（前期 ${formatNumber(previous)}）`;
    }

    return wrap;
}

function renderTrendChart() {
    const points = currentData.trend;
    const wrapper = document.getElementById('trend-wrapper');

    if (points.length === 0) {
        charts.renderNoData(wrapper);
        return;
    }

    const risk = charts.riskColors();

    // 中風險被顯示設定藏起來時整條 series 不畫——資料母體已在後端過濾，mediumRisk 恆為 0，
    // 留著這條線只會是一條貼底的平線，圖例卻仍暗示「有這個類別」，容易誤讀成真的沒有中風險日
    const datasets = [
        {
            label: '高風險',
            data: points.map(p => p.highRisk),
            borderColor: risk['高'],
            backgroundColor: risk['高'],
            tension: .3
        }
    ];
    if (visibleDayRisk.has('中')) {
        datasets.push({
            label: '中風險',
            data: points.map(p => p.mediumRisk),
            borderColor: risk['中'],
            backgroundColor: risk['中'],
            tension: .3
        });
    }

    chartInstances.trend?.destroy();
    chartInstances.trend = charts.line(document.getElementById('trend-chart'), {
        data: {
            labels: points.map(p => p.date.slice(5)),
            datasets
        },
        // 下鑽：點某天的資料點 → 該日該風險層級的清單（datasetIndex 1 只在中風險 series 存在時出現）
        drillTo: point => {
            const day = points[point.index];
            const level = point.datasetIndex === 0 ? '高' : '中';
            return `/records?riskLevels=${encodeURIComponent(level)}&from=${day.date}&to=${day.date}`;
        }
    });

    charts.attachToolbar(document.getElementById('trend-toolbar'), {
        canvasWrapper: wrapper,
        title: '告警數量趨勢',
        tableColumns: ['日期', '高風險', '中風險', '錯誤數'],
        tableRows: points.map(p => [p.date, p.highRisk, p.mediumRisk, p.errorCount])
    });
}

function renderCategoryChart() {
    const categories = currentData.categories;
    const wrapper = document.getElementById('category-wrapper');

    if (categories.length === 0) {
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
            labels: categories.map(c => CATEGORY_NAMES[c.category] ?? c.category),
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
            return `/records?categories=${category.category}&severity=${severityValue}` +
                   `&riskLevels=${encodeURIComponent('高,中,低')}` +
                   `&from=${currentData.from}&to=${currentData.to}`;
        }
    });

    charts.attachToolbar(document.getElementById('category-toolbar'), {
        canvasWrapper: wrapper,
        title: '風險類型分布',
        // docs/archive/HISTORY.md #1（B1 三級化）：嚴重度欄位收斂為三級，「嚴重」欄移除
        tableColumns: ['類型', '高', '中', '低', '問題數', '主機數'],
        tableRows: categories.map(c => [
            CATEGORY_NAMES[c.category] ?? c.category,
            c.highCount, c.mediumCount, c.lowCount, c.issueCount, c.affectedHosts
        ])
    });
}

/**
 * 排行卡的視角（docs/archive/FEEDBACK-11-PLAN.md §8-2）：'host' ｜ 'issue'。
 * 狀態存 localStorage，預設主機——既有畫面零變化，要看問題排行的人切一次就記住。
 */
const RANK_MODE_KEY = 'lf.reports.rankMode';
let rankMode = localStorage.getItem(RANK_MODE_KEY) === 'issue' ? 'issue' : 'host';

function bindRankModeToggle() {
    const toggle = document.getElementById('rank-mode-toggle');
    if (!toggle) return;

    for (const btn of toggle.querySelectorAll('[data-rank-mode]')) {
        btn.classList.toggle('active', btn.dataset.rankMode === rankMode);
        btn.addEventListener('click', () => {
            if (rankMode === btn.dataset.rankMode) return;
            rankMode = btn.dataset.rankMode;
            localStorage.setItem(RANK_MODE_KEY, rankMode);
            for (const other of toggle.querySelectorAll('[data-rank-mode]')) {
                other.classList.toggle('active', other.dataset.rankMode === rankMode);
            }
            renderHostChart();
        });
    }
}

/** 排行卡：依 rankMode 分派給主機或問題兩種呈現（同一張卡、同一個 canvas 容器） */
function renderHostChart() {
    document.getElementById('rank-title').textContent = rankMode === 'issue' ? '問題排行' : '主機告警排行';
    if (rankMode === 'issue') {
        renderIssueRankChart();
        return;
    }

    const hosts = currentData.hostRanking;
    const others = currentData.others;
    const wrapper = document.getElementById('host-wrapper');

    renderHostRankMeta();

    if (hosts.length === 0) {
        charts.renderNoData(wrapper, '此期間沒有風險主機');
        return;
    }

    // 主機量大時，Top 10 之外的主機併成一條「其他 N 台」——尾端主機不會完全隱形，
    // 也看得出前 10 佔整體多少。這一條不可下鑽（它是彙總，不是單一主機）。
    const labels = hosts.map(h => h.hostName);
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
                y: { stacked: true, grid: { display: false } }
            }
        },
        drillTo: point => {
            const host = hosts[point.index];   // 「其他」條沒有對應 hosts 元素，回 null 不下鑽
            return host && host.hostId > 0 ? `/hosts/${host.hostId}` : null;
        }
    });

    // 表格模式（工具列切換）：Top 10 逐台＋「其他」彙總列；完整逐台清單走「查看全部」
    // 連到問題查詢的依主機視角（伺服器端排行只回 Top 10＋彙總，不整包搬運）
    const tableRows = hosts.map(h => [h.hostName, h.highRiskDays, h.mediumRiskDays, h.correlationDays, h.latestHeadline]);
    if (others) {
        tableRows.push([`其他 ${others.hostCount} 台（彙總）`, others.highRiskDays, others.mediumRiskDays, '', '']);
    }

    charts.attachToolbar(document.getElementById('host-toolbar'), {
        canvasWrapper: wrapper,
        title: '主機告警排行',
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

    renderIssueRankMeta();

    if (issues.length === 0) {
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
                y: { grid: { display: false } }
            }
        },
        drillTo: point => {
            const issue = issues[point.index];   // 「其他」條是彙總，不下鑽
            return issue
                ? `/records?view=issue&source=${encodeURIComponent(issue.source)}&eventId=${issue.eventId}` +
                  `&from=${currentData.from}&to=${currentData.to}`
                : null;
        }
    });

    const tableRows = issues.map(i => [
        `${i.source} (${i.eventId})`, CATEGORY_NAMES[i.category] ?? i.category,
        severityName(i.maxSeverity), i.hostCount, i.dayCount, i.totalCount
    ]);
    if (others) {
        tableRows.push([`其他 ${others.issueCount} 個問題（彙總）`, '', '', others.hostCount, '', others.totalCount]);
    }

    charts.attachToolbar(document.getElementById('host-toolbar'), {
        canvasWrapper: wrapper,
        title: '問題排行',
        tableColumns: ['問題', '分類', '最高嚴重度', '主機數', '風險日數', '事件次數'],
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
    const concludedNote = currentData.concludedIssueCount > 0
        ? `；另有 ${currentData.concludedIssueCount} 個問題已有結論（未列入）`
        : '';

    subtitle.textContent = count > 0 ? `共 ${count} 個問題${scopeNote}${pendingNote}${concludedNote}` : '';

    if (currentData.issueOthers) {
        viewAll.href = `/records?view=issue&from=${currentData.from}&to=${currentData.to}`;
        viewAll.classList.remove('d-none');
    } else {
        viewAll.classList.add('d-none');
    }
}

/** 排行榜標題副說明（共 N 台）與「查看全部」連結——連到問題查詢的依主機視角，同一段期間 */
function renderHostRankMeta() {
    const subtitle = document.getElementById('host-rank-subtitle');
    const viewAll = document.getElementById('host-view-all');
    const count = currentData.rankedHostCount;

    subtitle.textContent = count > 0 ? `共 ${count} 台有風險日` : '';

    // 有主機被 Top 10 擋在外面時才顯示「查看全部」，沒有就別給多餘的出口
    if (currentData.others) {
        viewAll.href = `/records?view=host&riskLevels=${encodeURIComponent('高,中')}` +
            `&from=${currentData.from}&to=${currentData.to}`;
        viewAll.classList.remove('d-none');
    } else {
        viewAll.classList.add('d-none');
    }
}

function renderRiskChart() {
    const totalDays = currentData.trend.reduce((sum, p) => sum + p.highRisk + p.mediumRisk, 0);
    const wrapper = document.getElementById('risk-wrapper');
    const legend = document.getElementById('risk-legend');

    if (totalDays === 0) {
        charts.renderNoData(wrapper, '此期間沒有風險日');
        legend.replaceChildren();
        return;
    }

    const risk = charts.riskColors();
    const high = currentData.trend.reduce((sum, p) => sum + p.highRisk, 0);
    const medium = currentData.trend.reduce((sum, p) => sum + p.mediumRisk, 0);

    chartInstances.risk?.destroy();
    chartInstances.risk = charts.doughnut(document.getElementById('risk-chart'), {
        data: {
            labels: ['高風險', '中風險'],
            datasets: [{ data: [high, medium], backgroundColor: [risk['高'], risk['中']] }]
        },
        options: { plugins: { legend: { display: false } } },
        drillTo: point => {
            const level = point.index === 0 ? '高' : '中';
            return `/records?riskLevels=${encodeURIComponent(level)}&from=${currentData.from}&to=${currentData.to}`;
        }
    });

    charts.attachDoughnutLegend(legend, [
        { label: '高風險', value: high, color: risk['高'],
            url: `/records?riskLevels=${encodeURIComponent('高')}&from=${currentData.from}&to=${currentData.to}` },
        { label: '中風險', value: medium, color: risk['中'],
            url: `/records?riskLevels=${encodeURIComponent('中')}&from=${currentData.from}&to=${currentData.to}` }
    ]);
}

/**
 * 受影響主機占比（docs/archive/HISTORY.md #6）：本期高／中風險主機數 ÷ 可見主機總數。
 * 分子（Kpi.AffectedHosts）與分母（TotalHosts）同一次回應取得，不會各自查詢對不上。
 */
function renderAffectedHostsChart() {
    const wrapper = document.getElementById('affected-hosts-wrapper');
    const legend = document.getElementById('affected-hosts-legend');
    const total = currentData.totalHosts;
    const affected = currentData.kpi.affectedHosts;

    if (total === 0) {
        charts.renderNoData(wrapper, '尚無主機資料');
        legend.replaceChildren();
        return;
    }

    const status = charts.statusColors();
    const risk = charts.riskColors();
    const remaining = Math.max(total - affected, 0);
    const percent = Math.round((affected / total) * 100);

    chartInstances.affectedHosts?.destroy();
    chartInstances.affectedHosts = charts.doughnut(document.getElementById('affected-hosts-chart'), {
        data: {
            labels: ['受影響', '其餘'],
            datasets: [{ data: [affected, remaining], backgroundColor: [risk['高'], status.neutral] }]
        },
        options: { plugins: { legend: { display: false } } },
        drillTo: point => point.index === 0
            ? `/records?riskLevels=${encodeURIComponent('高,中')}&from=${currentData.from}&to=${currentData.to}`
            : null   // 「其餘」是彙總（沒問題的主機），沒有對應的下鑽清單
    });
    charts.setCenterText(wrapper, `${percent}%`);

    charts.attachDoughnutLegend(legend, [
        { label: '受影響', value: affected, color: risk['高'],
            url: `/records?riskLevels=${encodeURIComponent('高,中')}&from=${currentData.from}&to=${currentData.to}` },
        { label: '其餘', value: remaining, color: status.neutral, url: null }
    ]);
}

/**
 * 處理進度（docs/archive/HISTORY.md #6）：期間內高／中風險日已結案（resolved）的比例。
 * 母體與儀表板待辦同一套 HandlingService.GetTodo 規則（docs/archive/HISTORY.md S3），
 * 不是問題層級的計數——兩個層級的「已處理」語意不同，不能混用。
 */
function renderHandlingProgressChart() {
    const wrapper = document.getElementById('handling-progress-wrapper');
    const legend = document.getElementById('handling-progress-legend');
    const handling = currentData.handling;
    const total = handling.totalCount;

    if (total === 0) {
        charts.renderNoData(wrapper, '此期間沒有高／中風險日');
        legend.replaceChildren();
        return;
    }

    const status = charts.statusColors();
    const remaining = total - handling.resolvedCount;
    const percent = Math.round((handling.resolvedCount / total) * 100);

    chartInstances.handlingProgress?.destroy();
    chartInstances.handlingProgress = charts.doughnut(document.getElementById('handling-progress-chart'), {
        data: {
            labels: ['已處理', '未完成'],
            datasets: [{ data: [handling.resolvedCount, remaining], backgroundColor: [status.success, status.neutral] }]
        },
        options: { plugins: { legend: { display: false } } },
        drillTo: point => point.index === 1
            ? `/records?statuses=open,in_progress&riskLevels=${encodeURIComponent('高,中')}&from=${currentData.from}&to=${currentData.to}`
            : null   // 「已處理」分散在各日，沒有單一篩選條件可以精確對應回這個數字，不下鑽
    });
    charts.setCenterText(wrapper, `${percent}%`);

    charts.attachDoughnutLegend(legend, [
        { label: '已處理', value: handling.resolvedCount, color: status.success, url: null },
        { label: '未完成', value: remaining, color: status.neutral,
            url: `/records?statuses=open,in_progress&riskLevels=${encodeURIComponent('高,中')}&from=${currentData.from}&to=${currentData.to}` }
    ]);
}

// ── 期間控制 ─────────────────────────────────────────────────────────────────

document.getElementById('report-form').addEventListener('submit', event => {
    event.preventDefault();
    load();
});

for (const button of document.querySelectorAll('[data-range]')) {
    button.addEventListener('click', () => {
        setRange(Number(button.dataset.range));
        load();
    });
}

document.getElementById('btn-print-report').addEventListener('click', () => window.print());

// 處理狀態顯示範圍（§5）：單選 chip，切換即重載（母體改變，全圖表跟著變）
document.getElementById('report-scope-chips').addEventListener('click', event => {
    const btn = event.target.closest('button[data-scope]');
    if (!btn || btn.classList.contains('active')) return;
    for (const other of document.querySelectorAll('#report-scope-chips button')) {
        other.classList.toggle('active', other === btn);
    }
    currentScope = btn.dataset.scope;
    load();
});

// 進頁時把 active chip 對齊 URL 帶入的 scope
for (const btn of document.querySelectorAll('#report-scope-chips button')) {
    btn.classList.toggle('active', btn.dataset.scope === currentScope);
}

function setRange(days) {
    // 期間終點錨在昨天，不是今天（回饋十九輪批次C）：分析永遠只產到昨天，
    // 錨在今天會讓「本週/本月/近 90 天」的最後一天必然沒有資料
    const to = new Date();
    to.setDate(to.getDate() - 1);
    const from = new Date(to);
    from.setDate(from.getDate() - days + 1);

    // 本地日期（S12）：toISOString() 取的是 UTC 日期，台灣（UTC+8）凌晨 0~8 點呼叫會少算一天
    document.getElementById('report-from').value = toLocalDateString(from);
    document.getElementById('report-to').value = toLocalDateString(to);
}

document.getElementById('chart-picker-modal').addEventListener('show.bs.modal', renderChartPickerBody);

bindRankModeToggle();
setRange(30);
load();
