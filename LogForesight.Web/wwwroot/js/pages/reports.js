/**
 * 報表（docs/WEB-SPEC.md §9.6）——主管的主要畫面。
 *
 * §8.4 的驗收標準在這頁兌現：**任何一個數字，最多兩次點擊就能看到組成它的風險日清單**。
 * 實作方式是「組出帶篩選條件的網址再導頁」——問題查詢頁已支援 URL 同步，
 * 所以下鑽不需要在明細端寫任何程式碼。
 */

import { api } from '../core/api.js';
import { renderTable, renderLoading, renderEmpty, toast } from '../core/ui.js';
import { formatNumber, severityBadge } from '../core/format.js';
import * as charts from '../core/charts.js';

const CATEGORY_NAMES = {
    Storage: '儲存裝置', Hardware: '硬體', Security: '安全', Service: '服務',
    Backup: '備份', Config: '設定', Resource: '資源', Other: '其他'
};

let currentData = null;
const chartInstances = {};

async function load() {
    const from = document.getElementById('report-from').value;
    const to = document.getElementById('report-to').value;

    currentData = await api.get(`/api/reports/summary?from=${from}&to=${to}`);

    document.getElementById('print-title').textContent =
        `LogForesight 風險報表　${currentData.from} ～ ${currentData.to}`;

    renderKpi();
    renderTrendChart();
    renderCategoryChart();
    renderHostChart();
    renderRiskChart();
}

/** KPI 卡：帶與前一等長期間的對比——主管要的不是數字本身，是「變好還是變壞」 */
function renderKpi() {
    const kpi = currentData.kpi;
    const cards = [
        {
            label: '問題總數',
            value: kpi.totalIssues,
            previous: kpi.totalIssuesPrevious,
            url: `/records?from=${currentData.from}&to=${currentData.to}`
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

        const inner = document.createElement(card.url ? 'a' : 'div');
        inner.className = 'lf-stat';
        if (card.url) inner.href = card.url;

        const box = document.createElement('div');
        box.className = 'lf-card h-100' + (card.url ? ' lf-card--clickable' : '');

        const body = document.createElement('div');
        body.className = 'lf-card__body';

        const value = document.createElement('div');
        value.className = 'lf-stat__value';
        value.textContent = formatNumber(card.value);

        const label = document.createElement('div');
        label.className = 'lf-stat__label';
        label.textContent = card.label;

        body.append(value, label);

        if (card.previous !== null && card.previous !== undefined) {
            body.appendChild(comparisonBadge(card.value, card.previous));
        }
        if (card.hint) box.title = card.hint;

        box.appendChild(body);
        inner.appendChild(box);
        col.appendChild(inner);
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
    chartInstances.trend?.destroy();
    chartInstances.trend = charts.line(document.getElementById('trend-chart'), {
        data: {
            labels: points.map(p => p.date.slice(5)),
            datasets: [
                {
                    label: '高風險',
                    data: points.map(p => p.highRisk),
                    borderColor: risk['高'],
                    backgroundColor: risk['高'],
                    tension: .3
                },
                {
                    label: '中風險',
                    data: points.map(p => p.mediumRisk),
                    borderColor: risk['中'],
                    backgroundColor: risk['中'],
                    tension: .3
                }
            ]
        },
        // 下鑽：點某天的資料點 → 該日該風險層級的清單
        drillTo: point => {
            const day = points[point.index];
            const level = point.datasetIndex === 0 ? '高' : '中';
            return `/records?riskLevels=${encodeURIComponent(level)}&from=${day.date}&to=${day.date}`;
        }
    });

    charts.attachToolbar(document.getElementById('trend-toolbar'), {
        chart: chartInstances.trend,
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
    const severityKeys = [
        { key: 'criticalCount', label: 'Critical' },
        { key: 'highCount', label: 'High' },
        { key: 'mediumCount', label: 'Medium' },
        { key: 'lowCount', label: 'Low' }
    ];

    chartInstances.category?.destroy();
    chartInstances.category = charts.bar(document.getElementById('category-chart'), {
        data: {
            labels: categories.map(c => CATEGORY_NAMES[c.category] ?? c.category),
            datasets: severityKeys.map(s => ({
                label: s.label,
                data: categories.map(c => c[s.key]),
                backgroundColor: severity[s.label]
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
            const severityName = severityKeys[point.datasetIndex].label;
            return `/records?categories=${category.category}&severity=${severityName}` +
                   `&from=${currentData.from}&to=${currentData.to}`;
        }
    });

    charts.attachToolbar(document.getElementById('category-toolbar'), {
        chart: chartInstances.category,
        canvasWrapper: wrapper,
        title: '風險類型分布',
        tableColumns: ['類型', 'Critical', 'High', 'Medium', 'Low', '問題數', '主機數'],
        tableRows: categories.map(c => [
            CATEGORY_NAMES[c.category] ?? c.category,
            c.criticalCount, c.highCount, c.mediumCount, c.lowCount, c.issueCount, c.affectedHosts
        ])
    });
}

function renderHostChart() {
    const hosts = currentData.hostRanking;
    const wrapper = document.getElementById('host-wrapper');

    if (hosts.length === 0) {
        charts.renderNoData(wrapper, '此期間沒有風險主機');
        return;
    }

    const risk = charts.riskColors();
    chartInstances.host?.destroy();
    chartInstances.host = charts.bar(document.getElementById('host-chart'), {
        data: {
            labels: hosts.map(h => h.hostName),
            datasets: [
                { label: '高風險日', data: hosts.map(h => h.highRiskDays), backgroundColor: risk['高'] },
                { label: '中風險日', data: hosts.map(h => h.mediumRiskDays), backgroundColor: risk['中'] }
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
            const host = hosts[point.index];
            return host.hostId > 0 ? `/hosts/${host.hostId}` : null;
        }
    });

    charts.attachToolbar(document.getElementById('host-toolbar'), {
        chart: chartInstances.host,
        canvasWrapper: wrapper,
        title: '主機告警排行',
        tableColumns: ['主機', '高風險日', '中風險日', '關聯訊號日', '最新狀況'],
        tableRows: hosts.map(h => [h.hostName, h.highRiskDays, h.mediumRiskDays, h.correlationDays, h.latestHeadline])
    });
}

function renderRiskChart() {
    const kpi = currentData.kpi;
    const totalDays = currentData.trend.reduce((sum, p) => sum + p.highRisk + p.mediumRisk, 0);
    const wrapper = document.getElementById('risk-wrapper');

    if (totalDays === 0) {
        charts.renderNoData(wrapper, '此期間沒有風險日');
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
        options: { scales: {} },
        drillTo: point => {
            const level = point.index === 0 ? '高' : '中';
            return `/records?riskLevels=${encodeURIComponent(level)}&from=${currentData.from}&to=${currentData.to}`;
        }
    });

    charts.attachToolbar(document.getElementById('risk-toolbar'), {
        chart: chartInstances.risk,
        canvasWrapper: wrapper,
        title: '風險層級占比',
        tableColumns: ['風險層級', '日數'],
        tableRows: [['高風險', high], ['中風險', medium]]
    });
}

// ── 跨主機同簽章查詢 ─────────────────────────────────────────────────────────

document.getElementById('signature-form').addEventListener('submit', async event => {
    event.preventDefault();

    const eventId = document.getElementById('signature-event-id').value;
    if (!eventId) {
        toast('請輸入 Event ID', 'warning');
        return;
    }

    const container = document.getElementById('signature-result');
    renderLoading(container, 3);

    const source = document.getElementById('signature-source').value.trim();
    const hits = await api.get(
        `/api/reports/signature?eventId=${eventId}${source ? `&source=${encodeURIComponent(source)}` : ''}`);

    renderTable(container, {
        columns: [
            { title: '日期', render: h => dateLink(h) },
            { title: '主機', render: h => h.hostName },
            { title: '次數', className: 'text-end', render: h => formatNumber(h.count) },
            { title: '嚴重度', render: h => severityBadge(h.severity) },
            { title: '說明', render: h => h.knownIssue ?? '' }
        ],
        rows: hits,
        empty: {
            title: '沒有找到這個事件簽章',
            hint: '請確認 Event ID 是否正確，或該事件是否出現在您有權檢視的主機上。'
        }
    });
});

function dateLink(hit) {
    if (hit.hostId === 0) return hit.date;

    const link = document.createElement('a');
    link.href = `/records/${hit.hostId}/${hit.date}`;
    link.textContent = hit.date;
    return link;
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

function setRange(days) {
    const to = new Date();
    const from = new Date();
    from.setDate(from.getDate() - days + 1);

    document.getElementById('report-from').value = from.toISOString().slice(0, 10);
    document.getElementById('report-to').value = to.toISOString().slice(0, 10);
}

setRange(30);
load();
