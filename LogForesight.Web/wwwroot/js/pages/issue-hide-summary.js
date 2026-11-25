/**
 * 問題負責與靜音頁的「不顯示設定總覽」（第 81 輪批次 N，F16-4）：每個問題一列，彙總目前生效的靜音、機房結論、
 * 抑制、不再打擾四種設定；每一種旁邊有解除或前往的動作。資料來自 GET api/admin/issue-owners/hide-summary
 * （判定與數字全在後端，重用各來源既有的函式），這裡只畫表格與接動作。解除與前往都是既有的出口：
 * 靜音解除走共用的 confirmAndClearMute、結論解除走既有的 DELETE conclusion、抑制到規則維護、不再打擾到待派頁的清單。
 */

import { api } from '../core/api.js';
import { renderTable, renderLoading, toast, confirmAction, confirmAndClearMute, button, linkButton, guardLoad } from '../core/ui.js';
import { roleText } from '../core/elements.js';
import { appUrl, ruleEditUrl } from '../core/paths.js';
import { createSeqGuard } from '../core/async-guards.js';
import { muteText, conclusionText, suppressionText, dismissedText } from '../core/hide-summary.js';

const seq = createSeqGuard();

function cell(text, ...actions) {
    const wrap = document.createElement('div');
    if (text === null) {
        wrap.appendChild(roleText('lf-text-meta', '—'));
        return wrap;
    }
    wrap.appendChild(roleText('lf-text-data', text));
    if (actions.length > 0) {
        const row = document.createElement('div');
        row.className = 'd-flex flex-wrap gap-1 mt-1';
        row.append(...actions);
        wrap.appendChild(row);
    }
    return wrap;
}

async function clearConclusion(row, onChanged) {
    const confirmed = await confirmAction({
        title: '解除機房結論',
        message: `確定要解除「${row.displayLabel}」的機房結論嗎？已經套用到既有主機日的結果不會回滾。`,
        confirmText: '解除',
        confirmVariant: 'danger'
    });
    if (!confirmed) return;
    try {
        await api.delete(`/api/admin/issue-owners/${encodeURIComponent(row.sourceName)}/${row.eventId}/conclusion`);
        toast('已解除機房結論', 'success');
        onChanged();
    } catch {
        // 錯誤已由 api.js 顯示
    }
}

/** 渲染總覽；onChanged＝解除成功後的重新載入（總覽與下方清單都要更新） */
export function renderHideSummary(container, rows, onChanged) {
    renderTable(container, {
        columns: [
            { title: '問題', className: 'lf-minw-lg', render: r => roleText('lf-text-title lf-break-anywhere', r.displayLabel) },
            {
                title: '靜音',
                className: 'lf-minw-md',
                render: r => cell(muteText(r), button('解除', {
                    variant: 'outline-danger', opens: 'dialog',
                    onClick: async () => { if (await confirmAndClearMute(r)) onChanged(); }
                }))
            },
            {
                title: '機房結論',
                className: 'lf-minw-md',
                render: r => cell(conclusionText(r), button('解除', {
                    variant: 'outline-danger', opens: 'dialog', onClick: () => clearConclusion(r, onChanged)
                }))
            },
            {
                title: '抑制',
                className: 'lf-minw-md',
                render: r => cell(suppressionText(r), linkButton('前往規則維護',
                    appUrl(r.suppression?.ruleId ? ruleEditUrl(r.suppression.ruleId) : '/admin/rules'),
                    { variant: 'outline-secondary', title: '到規則維護的「告警抑制」解除或調整' }))
            },
            {
                title: '不再打擾',
                className: 'lf-minw-md',
                render: r => cell(dismissedText(r), linkButton('前往待派頁',
                    appUrl('/work-orders') + '#gaps',
                    { variant: 'outline-secondary', title: '到待派頁的「不再自動派工」恢復' }))
            }
        ],
        rows,
        empty: {
            title: '目前沒有任何問題被設為不顯示',
            hint: '問題查詢的每個問題「更多」裡有「不再顯示這個問題…」，回答三個問題就能套用。'
        }
    });
}

/** 載入並渲染；請求序號守門，慢回應不會蓋掉新結果 */
export async function loadHideSummary(container, onChanged) {
    const mine = seq.next();
    renderLoading(container, 3);
    const rows = await api.get('/api/admin/issue-owners/hide-summary');
    if (!seq.isCurrent(mine)) return;
    renderHideSummary(container, rows, onChanged);
}

/** 總覽的載入守門：失敗時顯示重試，不讓整頁卡在載入中 */
export function guardHideSummary(container, onChanged) {
    return guardLoad(container, () => loadHideSummary(container, onChanged));
}
