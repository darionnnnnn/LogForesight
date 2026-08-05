/**
 * Chart.js 的包裝層（docs/WEB-SPEC.md §8.3）。
 *
 * **頁面模組不得直接呼叫 Chart.js**，一律經過這裡。這一層負責四件事：
 *   1. 注入設計 token 的色盤與字型（語意色全站一致，§8.2 原則 3）
 *   2. 統一 tooltip / legend / 座標軸樣式
 *   3. 接上下鑽（§8.4）：點擊資料點 → 導向帶篩選條件的明細頁
 *   4. 提供折線/長條圖的「表格」切換，與占比圓餅圖的右側文字條列
 *      （2026-07-28 起不再提供 PNG 下載，見 attachToolbar／attachDoughnutLegend）
 *
 * 換圖表庫時只需要重寫這個模組——這是防廢棄的實際手段，不是原則宣示。
 */

import { formatNumber } from './format.js';

/** 自 CSS 變數讀取設計 token：顏色只在 site.css 定義一次 */
function token(name, fallback) {
    const value = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
    return value || fallback;
}

/** 8 類風險類型的固定色盤：同一類別在所有圖表中同色 */
export function categoryColors() {
    return {
        Storage: token('--lf-cat-storage', '#1d4ed8'),
        Hardware: token('--lf-cat-hardware', '#7c3aed'),
        Security: token('--lf-cat-security', '#dc2626'),
        Service: token('--lf-cat-service', '#ea580c'),
        Backup: token('--lf-cat-backup', '#0d9488'),
        Config: token('--lf-cat-config', '#64748b'),
        Resource: token('--lf-cat-resource', '#db2777'),
        Other: token('--lf-cat-other', '#94a3b8')
    };
}

/** docs/archive/HISTORY.md #1（B1 三級化）：Critical 收斂進 High，色盤剩三級 */
export function severityColors() {
    return {
        High: token('--lf-severity-high', '#ea580c'),
        Medium: token('--lf-severity-medium', '#0284c7'),
        Low: token('--lf-severity-low', '#94a3b8')
    };
}

export function riskColors() {
    return {
        高: token('--lf-risk-high', '#dc2626'),
        中: token('--lf-risk-mid', '#d97706'),
        低: token('--lf-risk-low', '#64748b')
    };
}

/** 占比圖表用的中性色（docs/archive/HISTORY.md #6：受影響主機占比／處理進度） */
export function statusColors() {
    return {
        success: token('--lf-success', '#16a34a'),
        neutral: token('--lf-gray-300', '#cbd5e1')
    };
}

const FONT_FAMILY = '"Segoe UI", "Microsoft JhengHei", system-ui, sans-serif';

function baseOptions({ drillTo, onDrill }) {
    return {
        responsive: true,
        maintainAspectRatio: false,
        interaction: { mode: 'index', intersect: false },
        plugins: {
            legend: {
                position: 'bottom',
                labels: { font: { family: FONT_FAMILY, size: 12 }, usePointStyle: true, boxWidth: 8 }
            },
            tooltip: {
                backgroundColor: 'rgba(27, 42, 65, .92)',
                titleFont: { family: FONT_FAMILY, size: 13 },
                bodyFont: { family: FONT_FAMILY, size: 12 },
                padding: 10,
                displayColors: true
            }
        },
        scales: {
            x: { grid: { display: false }, ticks: { font: { family: FONT_FAMILY, size: 11 } } },
            y: {
                beginAtZero: true,
                grid: { color: 'rgba(0,0,0,.06)' },
                // 問題數是整數，Y 軸出現 0.5 這種刻度只會讓人困惑
                ticks: { font: { family: FONT_FAMILY, size: 11 }, precision: 0 }
            }
        },
        onClick: (event, elements, chart) => {
            if (!drillTo || elements.length === 0) return;

            const element = elements[0];
            const url = drillTo({
                datasetIndex: element.datasetIndex,
                index: element.index,
                label: chart.data.labels?.[element.index],
                datasetLabel: chart.data.datasets?.[element.datasetIndex]?.label
            });

            if (url) {
                if (onDrill) onDrill(url);
                else location.href = url;
            }
        },
        onHover: (event, elements) => {
            // 可下鑽時把游標變成手指——沒有這個提示，使用者不會知道圖是可以點的
            event.native.target.style.cursor = drillTo && elements.length > 0 ? 'pointer' : 'default';
        }
    };
}

function merge(base, extra) {
    if (!extra) return base;
    return {
        ...base,
        ...extra,
        plugins: { ...base.plugins, ...(extra.plugins ?? {}) },
        scales: { ...base.scales, ...(extra.scales ?? {}) }
    };
}

/**
 * 建立圖表。
 * spec: { type, data, options, drillTo(point) => url|null, tableColumns, tableRows }
 */
export function create(canvas, spec) {
    const chart = new Chart(canvas, {
        type: spec.type,
        data: spec.data,
        options: merge(baseOptions(spec), spec.options)
    });

    return chart;
}

export const line = (canvas, spec) => create(canvas, { ...spec, type: 'line' });
export const bar = (canvas, spec) => create(canvas, { ...spec, type: 'bar' });
export const doughnut = (canvas, spec) => create(canvas, { ...spec, type: 'doughnut' });

/**
 * 圖卡的工具列：表格切換（§8.3 規則 4）。
 *
 * 表格切換不只是「無障礙加分項」——色弱使用者、需要精確讀值的人、
 * 想複製數字到別處的人，都靠它。資料本來就在前端，零後端成本。
 *
 * PNG 下載已移除（docs/archive/HISTORY.md #4）：需要圖檔的情境走既有
 * 「列印 / 存成 PDF」，不再逐圖各自下載。
 */
export function attachToolbar(container, { canvasWrapper, tableColumns, tableRows, title }) {
    const toolbar = document.createElement('div');
    toolbar.className = 'd-flex gap-1 lf-no-print';

    const tableWrapper = document.createElement('div');
    tableWrapper.className = 'lf-table-wrap d-none';
    tableWrapper.appendChild(buildTable(tableColumns, tableRows));
    canvasWrapper.after(tableWrapper);

    const toggleButton = document.createElement('button');
    toggleButton.type = 'button';
    toggleButton.className = 'btn btn-sm btn-outline-secondary';
    toggleButton.textContent = '表格';
    toggleButton.setAttribute('aria-label', `以表格檢視「${title}」的資料`);
    toggleButton.addEventListener('click', () => {
        const showingTable = !tableWrapper.classList.contains('d-none');

        tableWrapper.classList.toggle('d-none', showingTable);
        canvasWrapper.classList.toggle('d-none', !showingTable);
        toggleButton.textContent = showingTable ? '表格' : '圖表';
    });

    toolbar.appendChild(toggleButton);
    container.appendChild(toolbar);
}

/**
 * 圓餅圖左圖右文字條列（docs/archive/HISTORY.md #3）：取代表格切換＋PNG 下載——
 * 圓餅圖本來就沒有 XY 軸，數字已經在條列裡，不需要再切換一次表格模式。
 * 每列「色點＋名稱＋數值＋百分比」，列本身可點（沿用該分段的 drillTo URL，
 * 與點圖同一個下鑽目的地）；沒有 url 的列（如彙總的「其餘」）不做成連結。
 * items: [{ label, value, color, url }]
 */
export function attachDoughnutLegend(container, items) {
    const total = items.reduce((sum, item) => sum + item.value, 0);

    const list = document.createElement('div');
    list.className = 'lf-legend-list';

    for (const item of items) {
        const percent = total > 0 ? Math.round((item.value / total) * 100) : 0;

        const row = document.createElement(item.url ? 'a' : 'div');
        row.className = 'lf-legend-row' + (item.url ? ' lf-legend-row--link' : '');
        if (item.url) row.href = item.url;

        const dot = document.createElement('span');
        dot.className = 'lf-legend-row__dot';
        dot.style.backgroundColor = item.color;

        const label = document.createElement('span');
        label.className = 'lf-legend-row__label';
        label.textContent = item.label;

        const value = document.createElement('span');
        value.className = 'lf-legend-row__value';
        value.textContent = `${formatNumber(item.value)}（${percent}%）`;

        row.append(dot, label, value);
        list.appendChild(row);
    }

    container.replaceChildren(list);
}

function buildTable(columns, rows) {
    const table = document.createElement('table');
    table.className = 'table table-sm mb-0';

    const thead = document.createElement('thead');
    const headRow = document.createElement('tr');
    for (const column of columns) {
        const th = document.createElement('th');
        th.textContent = column;
        headRow.appendChild(th);
    }
    thead.appendChild(headRow);
    table.appendChild(thead);

    const tbody = document.createElement('tbody');
    for (const row of rows) {
        const tr = document.createElement('tr');
        for (const cell of row) {
            const td = document.createElement('td');
            td.textContent = cell === null || cell === undefined ? '' : String(cell);
            tr.appendChild(td);
        }
        tbody.appendChild(tr);
    }
    table.appendChild(tbody);
    return table;
}

/** 空資料時顯示提示而不是一張空白的圖 */
export function renderNoData(container, message = '此期間沒有資料') {
    const el = document.createElement('div');
    el.className = 'lf-empty';
    el.textContent = message;
    container.replaceChildren(el);
}

/**
 * 甜甜圈中央疊加文字（docs/archive/HISTORY.md #6：受影響主機占比／處理進度的百分比）。
 * canvasWrapper 需為 .lf-chart（position: relative）；文字絕對定位置中，不影響圖表本身。
 */
export function setCenterText(canvasWrapper, text) {
    let label = canvasWrapper.querySelector('.lf-chart__center-text');
    if (!label) {
        label = document.createElement('div');
        label.className = 'lf-chart__center-text';
        canvasWrapper.appendChild(label);
    }
    label.textContent = text;
}
