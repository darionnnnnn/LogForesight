/**
 * 告警工具決策表（靜音／抑制／統一標記／不再打擾）（docs/archive/FEEDBACK-47-PLAN.md 15.3 (4)）。
 * 規則頁「告警抑制」頁籤與設定頁「自動派工」段共用這一份：
 * 四列文字只寫在這裡，兩頁都 import 同一個函式，避免兩處說法日後各改各的。
 * 全部以 DOM API 建立（不走 HTML 字串解析），連結走 appUrl()。
 */

import { appUrl } from './paths.js';

const HEADERS = ['工具', '適用情境', '在哪裡設定', '範圍與期限'];

const ROWS = [
    ['靜音', '這個問題暫時不用看，到期自動恢復', '問題檔案、問題查詢「依問題」的「靜音」', '全機房；有期限（最長 365 天）'],
    ['抑制', '某些主機或群組的這個問題永遠不告警', '規則維護「告警抑制」', '指定主機／群組／全站；可設天數或永久'],
    ['統一標記', '這個問題在沒人接手的主機上一次下結論', '問題查詢「依問題」的「統一標記」', '一次性；可勾自動套用到之後的新日子'],
    ['不再打擾', '這台主機的這個問題已知，別再派工', '風險日詳情頁把問題標為不處理、誤報或已知雜訊', '單一主機；之後再出現不自動派工']
];

/** 在 container 內建立收合式決策表；container 為 null 時不做事 */
export function renderAlertToolsTable(container) {
    if (!container) return;

    const details = document.createElement('details');
    details.className = 'mb-3';

    const summary = document.createElement('summary');
    summary.className = 'fw-semibold small';
    summary.textContent = '什麼情況用哪個工具？';
    details.appendChild(summary);

    const table = document.createElement('table');
    table.className = 'table table-sm small mb-1';

    const thead = document.createElement('thead');
    const headRow = document.createElement('tr');
    for (const text of HEADERS) {
        const th = document.createElement('th');
        th.scope = 'col';
        th.textContent = text;
        headRow.appendChild(th);
    }
    thead.appendChild(headRow);
    table.appendChild(thead);

    const tbody = document.createElement('tbody');
    for (const cells of ROWS) {
        const tr = document.createElement('tr');
        for (const text of cells) {
            const td = document.createElement('td');
            td.textContent = text;
            tr.appendChild(td);
        }
        tbody.appendChild(tr);
    }
    table.appendChild(tbody);
    details.appendChild(table);

    const more = document.createElement('div');
    more.className = 'small';
    const link = document.createElement('a');
    link.href = appUrl('/help/manual') + '#alert-tools';
    link.textContent = '完整說明見操作說明書';
    more.appendChild(link);
    details.appendChild(more);

    container.replaceChildren(details);
}
