/**
 * Chart.js 的包裝層（docs/WEB-SPEC.md §8.3）。
 *
 * **頁面模組不得直接呼叫 Chart.js**，一律經過這裡。這一層負責四件事：
 *   1. 注入設計 token 的色盤與字型（語意色全站一致，§8.2 原則 3）
 *   2. 統一 tooltip / legend / 座標軸樣式
 *   3. 接上下鑽（§8.4）：點擊資料點 → 導向帶篩選條件的明細頁
 *   4. 提供「表格」切換與 PNG 下載
 *
 * 換圖表庫時只需要重寫這個模組——這是防廢棄的實際手段，不是原則宣示。
 */

/** 自 CSS 變數讀取設計 token：顏色只在 site.css 定義一次 */
function token(name, fallback) {
    const value = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
    return value || fallback;
}

/** 8 類風險類型的固定色盤：同一類別在所有圖表中同色 */
export function categoryColors() {
    return {
        Storage: token('--lf-cat-storage', '#0d6efd'),
        Hardware: token('--lf-cat-hardware', '#6f42c1'),
        Security: token('--lf-cat-security', '#dc3545'),
        Service: token('--lf-cat-service', '#fd7e14'),
        Backup: token('--lf-cat-backup', '#20c997'),
        Config: token('--lf-cat-config', '#6c757d'),
        Resource: token('--lf-cat-resource', '#d63384'),
        Other: token('--lf-cat-other', '#adb5bd')
    };
}

export function severityColors() {
    return {
        Critical: token('--lf-severity-critical', '#dc3545'),
        High: token('--lf-severity-high', '#fd7e14'),
        Medium: token('--lf-severity-medium', '#0dcaf0'),
        Low: token('--lf-severity-low', '#adb5bd')
    };
}

export function riskColors() {
    return {
        高: token('--lf-risk-high', '#dc3545'),
        中: token('--lf-risk-mid', '#ffc107'),
        低: token('--lf-risk-low', '#6c757d')
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
 * 圖卡的工具列：表格切換 ＋ PNG 下載（§8.3 規則 4）。
 *
 * 表格切換不只是「無障礙加分項」——色弱使用者、需要精確讀值的人、
 * 想複製數字到別處的人，都靠它。資料本來就在前端，零後端成本。
 */
export function attachToolbar(container, { chart, canvasWrapper, tableColumns, tableRows, title }) {
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
    toggleButton.setAttribute('aria-label', `以表格檢視「${title}」的數據`);
    toggleButton.addEventListener('click', () => {
        const showingTable = !tableWrapper.classList.contains('d-none');

        tableWrapper.classList.toggle('d-none', showingTable);
        canvasWrapper.classList.toggle('d-none', !showingTable);
        toggleButton.textContent = showingTable ? '表格' : '圖表';
    });

    const downloadButton = document.createElement('button');
    downloadButton.type = 'button';
    downloadButton.className = 'btn btn-sm btn-outline-secondary';
    downloadButton.textContent = 'PNG';
    downloadButton.setAttribute('aria-label', `下載「${title}」圖表`);
    downloadButton.addEventListener('click', () => {
        const link = document.createElement('a');
        link.href = chart.toBase64Image();
        link.download = `${title}.png`;
        link.click();
    });

    toolbar.append(toggleButton, downloadButton);
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

/** 空資料時顯示提示而不是一張空白的圖 */
export function renderNoData(container, message = '此期間沒有資料') {
    const el = document.createElement('div');
    el.className = 'lf-empty';
    el.textContent = message;
    container.replaceChildren(el);
}
