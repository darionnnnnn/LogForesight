/**
 * Chart.js 的包裝層（docs/WEB-SPEC.md §8.3）。
 *
 * **頁面模組不得直接呼叫 Chart.js**，一律經過這裡。這一層負責四件事：
 *   1. 注入設計 token 的色盤與字型（語意色全站一致，§8.2 原則 3）
 *   2. 統一 tooltip / legend / 座標軸樣式
 *   3. 接上下鑽（§8.4）：點擊資料點 → 導向帶篩選條件的明細頁
 *   4. 提供折線/長條圖的「表格」切換（attachToolbar）
 *
 * 換圖表庫時只需要重寫這個模組——這是防廢棄的實際手段，不是原則宣示。
 */

import { appUrl } from './paths.js';
import { formatNumber } from './format.js';
import { cssVar } from './ui.js';
import { FONT_SCALE_EVENT } from './reveal.js';

/** 8 類風險類型的固定色盤：同一類別在所有圖表中同色 */
export function categoryColors() {
    return {
        Storage: cssVar('--lf-cat-storage', '#1d4ed8'),
        Hardware: cssVar('--lf-cat-hardware', '#7c3aed'),
        Security: cssVar('--lf-cat-security', '#dc2626'),
        Service: cssVar('--lf-cat-service', '#ea580c'),
        Backup: cssVar('--lf-cat-backup', '#0d9488'),
        Config: cssVar('--lf-cat-config', '#64748b'),
        Resource: cssVar('--lf-cat-resource', '#db2777'),
        Other: cssVar('--lf-cat-other', '#94a3b8')
    };
}

/** docs/archive/HISTORY.md #1（B1 三級化）：Critical 收斂進 High，色盤剩三級 */
export function severityColors() {
    return {
        High: cssVar('--lf-severity-high', '#ea580c'),
        Medium: cssVar('--lf-severity-medium', '#0284c7'),
        Low: cssVar('--lf-severity-low', '#94a3b8')
    };
}

export function riskColors() {
    return {
        高: cssVar('--lf-risk-high', '#dc2626'),
        中: cssVar('--lf-risk-mid', '#d97706'),
        低: cssVar('--lf-risk-low', '#64748b')
    };
}

/**
 * 處理狀態用色（報表處理進度條）：已處理＝成功綠、處理中＝資訊藍、未處理＝中性灰。
 * 刻意不用風險色（紅／琥珀）——同一張卡的第二條是風險層級，兩條用同色系會互相混淆。
 */
export function statusColors() {
    return {
        success: cssVar('--lf-success', '#16a34a'),
        inProgress: cssVar('--lf-info', '#0284c7'),
        unhandled: cssVar('--lf-gray-400', '#94a3b8')
    };
}

/**
 * 圖表文字的字級（DESIGN-SYSTEM §3）：座標、圖例用 --lf-chart-text-size，提示框用 --lf-chart-tooltip-size。
 * 對應到哪一級寫在樣式表（一般模式 xs／sm，大螢幕模式改指 sm／base），這裡只負責讀出實際 px。
 */
const pxCache = new Map();

/**
 * 圖表文字實際的 px（kind：'text' 座標與圖例、'tooltip' 提示框）。用樣式表的離屏探針 .lf-chart-probe__* 讀計算值——
 * 偏好倍率、瀏覽器字級設定、12px 下限、大螢幕倍率都寫在樣式表，JS 不重寫一份換算規則。
 * 根字級與 <html> 類別相同時結果不變，以兩者當快取鍵（圖表每畫一次會讀很多次）。
 */
export function textPx(kind) {
    const root = document.documentElement;
    const key = `${getComputedStyle(root).fontSize}|${root.className}|${kind}`;
    if (!pxCache.has(key)) {
        const probe = document.createElement('span');
        probe.className = `lf-chart-probe lf-chart-probe__${kind}`;
        document.body.appendChild(probe);
        // 讀不到探針（樣式表未載入）時退回根字級，不在 JS 寫字級數字
        pxCache.set(key, parseFloat(getComputedStyle(probe).fontSize) || parseFloat(getComputedStyle(root).fontSize));
        probe.remove();
    }
    return pxCache.get(key);
}

const fontFamily = () => cssVar('--lf-font-family', 'system-ui, sans-serif');
/** Chart.js 的字型設定接受函式：每次重畫時重新解析，字級偏好改變後 update() 就會換成新字級 */
const textFont = () => ({ family: fontFamily(), size: textPx('text') });
const tooltipFont = () => ({ family: fontFamily(), size: textPx('tooltip') });

/**
 * 字級偏好切換時，現存的圖表重新解析字級並重畫；已銷毀的圖表不在 Chart.instances 裡，不會被呼叫。
 * 只用到色盤的頁面（儀表板）會匯入本模組卻沒有載入 Chart.js，那時沒有圖表可重畫
 */
export function refreshFonts() {
    if (typeof Chart === 'undefined') return;
    for (const chart of Object.values(Chart.instances ?? {})) chart.update('none');
}
if (typeof window !== 'undefined') window.addEventListener(FONT_SCALE_EVENT, refreshFonts);

function baseOptions({ drillTo, onDrill }) {
    const options = {
        responsive: true,
        maintainAspectRatio: false,
        interaction: { mode: 'index', intersect: false },
        plugins: {
            legend: {
                position: 'bottom',
                // 圖例色點與間距跟著字級（Chart.js 不接受函式，建立圖表時取一次；大螢幕倍率改變時頁面會重建圖表）
                labels: { font: textFont, usePointStyle: true, boxWidth: Math.round(textPx('text') * 2 / 3), boxHeight: Math.round(textPx('text') * 2 / 3), padding: Math.round(textPx('text') * 7 / 6) }
            },
            tooltip: {
                backgroundColor: cssVar('--lf-chart-tooltip-bg', '#1b2a41'),
                titleFont: tooltipFont,
                bodyFont: tooltipFont,
                padding: 10,
                displayColors: true
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
                else location.href = appUrl(url);
            }
        },
        onHover: (event, elements) => {
            // 可下鑽時把游標變成手指——沒有這個提示，使用者不會知道圖是可以點的
            event.native.target.classList.toggle('lf-pointer', Boolean(drillTo) && elements.length > 0);
        }
    };

    options.scales = {
        x: { grid: { display: false }, ticks: { font: textFont } },
        y: {
            beginAtZero: true,
            grid: { color: cssVar('--lf-chart-grid', '#0000000f') },
            // 問題數是整數，Y 軸出現 0.5 這種刻度只會讓人困惑
            ticks: { font: textFont, precision: 0 }
        }
    };

    return options;
}

function merge(base, extra) {
    if (!extra) return base;
    const merged = {
        ...base,
        ...extra,
        plugins: { ...base.plugins, ...(extra.plugins ?? {}) }
    };

    merged.scales = { ...base.scales, ...(extra.scales ?? {}) };

    return merged;
}

/**
 * 建立圖表。
 * spec: { type, data, options, drillTo(point) => url|null, tableColumns, tableRows }
 */
export function create(canvas, spec) {
    // 前一次可能顯示過「沒有資料」——重新畫圖時要把提示收掉，並讓 canvas 回到可見狀態
    clearNoData(canvas.parentElement);

    const options = merge(baseOptions(spec), spec.options);
    // 頁面自訂的座標軸會整組取代共用設定，刻度字級就退回 Chart.js 預設 12px（根層的 options.font 對座標軸無效）：
    // 補進沒有自己指定字級的座標軸，頁面自己指定的優先
    for (const [axis, cfg] of Object.entries(options.scales ?? {})) {
        if (cfg?.ticks?.font) continue;
        options.scales[axis] = { ...cfg, ticks: { ...(cfg?.ticks ?? {}), font: textFont } };
    }

    const chart = new Chart(canvas, {
        type: spec.type,
        data: spec.data,
        options
    });

    return chart;
}

export const line = (canvas, spec) => create(canvas, { ...spec, type: 'line' });
export const bar = (canvas, spec) => create(canvas, { ...spec, type: 'bar' });

/**
 * 圖卡的工具列：表格切換（§8.3 規則 4）。
 *
 * 表格切換不只是「無障礙加分項」——色弱使用者、需要精確讀值的人、
 * 想複製數字到別處的人，都靠它。資料本來就在前端，零後端成本。
 *
 * PNG 下載已移除（docs/archive/HISTORY.md #4）：需要圖檔的情境走既有
 * 「列印 / 存成 PDF」，不再逐圖各自下載。
 */
/** 追蹤每個 container 上一次由 attachToolbar 插入的 tableWrapper，供清除用 */
const _toolbarTableMap = new WeakMap();

export function attachToolbar(container, { canvasWrapper, tableColumns, tableRows, title }) {
    // 清除上一次由 attachToolbar 產生的 toolbar（以 data-lf-toolbar 標記）
    container.querySelectorAll('[data-lf-toolbar]').forEach(el => el.remove());

    // 清除上一次由 attachToolbar 插入的 tableWrapper（以 WeakMap 追蹤）
    _toolbarTableMap.get(container)?.remove();

    const tableWrapper = document.createElement('div');
    tableWrapper.className = 'lf-table-wrap d-none';
    tableWrapper.appendChild(buildTable(tableColumns, tableRows));
    canvasWrapper.after(tableWrapper);
    _toolbarTableMap.set(container, tableWrapper);

    const toolbar = document.createElement('div');
    toolbar.className = 'd-flex gap-1 lf-no-print';
    toolbar.dataset.lfToolbar = '';   // 標記，供下次呼叫定位並清除

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

/**
 * 空資料時顯示提示而不是一張空白的圖。
 *
 * **不可以移除 canvas**：容器裡的 canvas 是頁面 markup 的一部分，把它換掉之後
 * 再選一次 `getElementById` 會拿到 null，換一個有資料的期間時圖就再也回不來。
 * 改成疊一層提示並把 canvas 藏起來，由 create() 在下次畫圖時自動還原。
 */
export function renderNoData(container, message = '此期間沒有資料') {
    container.classList.add('lf-chart--empty');

    let el = container.querySelector(':scope > .lf-empty');
    if (!el) {
        el = document.createElement('div');
        el.className = 'lf-empty';
        container.appendChild(el);
    }
    el.textContent = message;

    // 中央百分比是上一次的殘值，沒有資料時不該留著
    container.querySelector(':scope > .lf-chart__center-text')?.remove();
}

/** 還原 renderNoData 的效果（create() 會自動呼叫） */
export function clearNoData(container) {
    if (!container) return;
    container.classList.remove('lf-chart--empty');
    container.querySelector(':scope > .lf-empty')?.remove();
}
