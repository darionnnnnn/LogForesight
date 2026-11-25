/**
 * 主機詳情「這台的交辦」：這台主機每個進行中的案件（問題）一列——問題、嚴重度、處理人、狀態、單號、上次回覆、是否復發。
 * 檢視者是處理人的列可勾選，一次回覆多個問題：沿用回覆彈窗（issue-status-reply.js），每張單各送一次回覆、
 * caseIds 只帶這台主機的案件；其中一張失敗（例如剛被改派）不影響其他張，訊息列出失敗的單與原因。
 *
 * 資料來自 GET /api/host-detail/{id}/orders（可見範圍與案件授與由伺服器過濾，這裡不再判斷）。
 */

import { api } from '../core/api.js';
import { appUrl } from '../core/paths.js';
import { renderTable, button, toast, createPageSelection } from '../core/ui.js';
import { roleText } from '../core/elements.js';
import { statusBadge, severityBadge } from '../core/format.js';
import { statusBadgeMeta } from '../core/handling-statuses.js';
import { openWorkOrderReplyModal, replyOutcomeText } from './issue-status-reply.js';

/** 上次回覆說明最多顯示幾個字；全文放 title */
const LAST_REPLY_NOTE_MAX = 24;

/** 表格沒有任何列時的文字（0 列的定義：這台沒有進行中的交辦） */
export const HOST_ORDERS_EMPTY_TEXT = '這台主機目前沒有進行中的交辦';

/** 勾選的列依單號分組：orderId → 該單在這台主機的 caseId 清單（保持勾選順序） */
export function groupCasesByOrder(rows) {
    const groups = new Map();
    for (const row of rows) {
        if (!groups.has(row.workOrderId)) groups.set(row.workOrderId, []);
        groups.get(row.workOrderId).push(row.caseId);
    }
    return groups;
}

/**
 * 每張單各送一次回覆（POST /api/work-orders/{id}/reply，caseIds 只帶這台主機的案件），依單號由小到大逐張送。
 * 單張失敗不中斷其他張：結果逐張記錄。呼叫端的 post 要自己加 `{ silent: true }`（失敗訊息統一在彙整後出）。
 * @returns {Promise<{succeeded: Array<{workOrderId: number, cases: number, workOrderClosed: boolean, escalationNotifiedAdmins: number|null}>, failed: Array<{workOrderId: number, message: string, error: Error}>}>}
 */
export async function submitHostReplies(rows, payload, post) {
    const groups = groupCasesByOrder(rows);
    const succeeded = [];
    const failed = [];
    for (const orderId of [...groups.keys()].sort((a, b) => a - b)) {
        try {
            const result = await post(orderId, { caseIds: groups.get(orderId), ...payload });
            succeeded.push({ workOrderId: orderId, cases: result?.cases ?? groups.get(orderId).length, workOrderClosed: result?.workOrderClosed === true,
                escalationNotifiedAdmins: Number.isInteger(result?.escalationNotifiedAdmins) ? result.escalationNotifiedAdmins : null });
        } catch (error) {
            failed.push({ workOrderId: orderId, message: error?.message || '未知原因', error });
        }
    }
    return { succeeded, failed };
}

/**
 * 回覆結果的訊息：全部成功＝成功訊息；有失敗＝警告，逐張列出失敗的單與原因（成功的已寫入）。
 * @returns {{kind: 'success'|'warning', text: string}}
 */
export function hostRepliesMessage(result, payload) {
    const total = result.succeeded.reduce((sum, item) => sum + item.cases, 0);
    if (result.failed.length === 0) {
        const closed = result.succeeded.filter(item => item.workOrderClosed).length;
        const scope = `${total} 個問題`;
        // 上報通知人數：取各張單回報的最大值——每張單通知的是同一批管理員，加總會重複計算（都沒回報＝不多說，與其他回覆入口同一句型）
        const counts = result.succeeded.map(item => item.escalationNotifiedAdmins).filter(Number.isInteger);
        const notified = counts.length > 0 ? Math.max(...counts) : undefined;
        return { kind: 'success', text: `${replyOutcomeText(payload, scope, notified)}${closed > 0 ? `，其中 ${closed} 張單結案` : ''}` };
    }
    const failures = result.failed.map(item => `#${item.workOrderId} 失敗：${item.message}`).join('；');
    return { kind: 'warning', text: `已完成 ${result.succeeded.length} 張單；${failures}。失敗的單仍在清單中，可再回覆一次。` };
}

/** 草稿識別：同一台主機、同一批案件（排序後）對到同一份草稿 */
export function hostOrdersDraftKey(hostId, rows) {
    return `host:${hostId}:cases:${rows.map(row => row.caseId).sort().join(',')}`;
}

function lastReplyText(lastReply) {
    const meta = statusBadgeMeta(lastReply.status);
    const head = `${String(lastReply.date || '').slice(5, 10)} ${meta ? meta.label : lastReply.status || ''}`.trim();
    const note = lastReply.note ? String(lastReply.note) : '';
    const shown = note.length > LAST_REPLY_NOTE_MAX ? `${note.slice(0, LAST_REPLY_NOTE_MAX)}…` : note;
    return { text: note ? `${head}：${shown}` : head, full: note ? `${head}：${note}` : '' };
}

/**
 * @param {object} options
 * @param {number} options.hostId
 * @param {string} options.hostName 回覆彈窗標題用
 * @param {HTMLElement} options.container 表格容器
 * @param {object|null} options.currentUser 決定單號要不要連到交辦單頁（能看那張單的人才有連結）
 * @param {(user: object, capability: string) => boolean} options.can 能力判斷（頁面傳入；本模組不自己匯入能力函式，行為測試才能替換）
 * @param {HTMLElement} [options.replyButtonHost] 「回覆選取的問題」按鈕放的位置（卡片標頭）；沒有就不出現批次回覆
 * @param {HTMLElement} [options.selectedCountEl] 「已選 N 個問題」文字節點（id 與 createPageSelection 的 bar 一致）
 */
export function createHostOrders({ hostId, hostName, container, currentUser, can, replyButtonHost, selectedCountEl }) {
    let rows = [];
    let replyBtn = null;

    const selection = createPageSelection({
        keyOf: row => row.caseId,
        isSelectable: row => row.canReply === true,
        getPageItems: () => rows,
        labels: { row: '選取這個問題', all: '全選可回覆的問題' },
        bar: { barId: selectedCountEl?.id ?? 'host-orders-selected', countId: selectedCountEl?.id ?? 'host-orders-selected', unit: '個問題' },
        onSelectAll: () => render(),
        onChange: () => syncReplyButton()
    });

    function canOpenOrder(row) {
        if (!currentUser) return false;
        return row.handlerId === currentUser.userId || can(currentUser, 'Assign') || can(currentUser, 'ViewAll');
    }

    function syncReplyButton() {
        if (!replyBtn) return;
        replyBtn.disabled = selection.selected.size === 0;
    }

    function issueCell(row) {
        const wrap = document.createElement('div');
        wrap.className = 'd-flex flex-column align-items-start gap-1';
        wrap.appendChild(roleText('lf-text-title', row.issueLabel));
        // 同主機同問題有多個登錄類型時才標：否則各列看起來一模一樣
        if (row.showLogName && row.logName) wrap.appendChild(roleText('lf-text-meta', `登錄類型：${row.logName}`));
        return wrap;
    }

    function statusCell(row) {
        const wrap = document.createElement('div');
        wrap.className = 'd-flex flex-column align-items-start gap-1';
        const meta = statusBadgeMeta(row.status) || { label: row.status || '未知', variant: 'neutral' };
        wrap.appendChild(statusBadge(meta.label, meta.variant));
        if (row.isRecurrence) wrap.appendChild(roleText('lf-text-meta', '復發'));
        if (row.lastReply) {
            const reply = lastReplyText(row.lastReply);
            const text = roleText('lf-text-meta', `上次回覆 ${reply.text}`);
            if (reply.full) text.title = reply.full;
            wrap.appendChild(text);
        }
        return wrap;
    }

    function orderCell(row) {
        if (!canOpenOrder(row)) return roleText('lf-text-data', `#${row.workOrderId}`);
        const link = document.createElement('a');
        link.href = appUrl(`/work-orders/${row.workOrderId}`);
        link.textContent = `#${row.workOrderId}`;
        return link;
    }

    function render() {
        const anyReplyable = rows.some(row => row.canReply === true);
        const columns = [];
        if (anyReplyable) {
            columns.push({
                title: '',
                className: 'text-center text-nowrap',
                renderHeader: () => selection.selectAllHeaderCheckbox(),
                render: row => selection.rowCheckboxCell(row)
            });
        }
        columns.push(
            { title: '問題', className: 'lf-break-anywhere', render: issueCell },
            { title: '嚴重度', className: 'lf-col-nowrap', render: row => row.severity ? severityBadge(row.severity) : roleText('lf-text-meta', '—') },
            { title: '處理人', className: 'lf-col-nowrap', render: row => roleText('lf-text-meta', row.handlerName) },
            { title: '狀態', render: statusCell },
            { title: '單號', className: 'lf-col-nowrap', render: orderCell }
        );
        renderTable(container, { columns, rows, empty: { title: HOST_ORDERS_EMPTY_TEXT, hint: '這台主機有問題被交辦後，會列在這裡。' } });
        selection.sync();
        syncReplyButton();
    }

    function selectedRows() {
        return rows.filter(row => selection.selected.has(row.caseId));
    }

    function openReply() {
        const picked = selectedRows();
        if (picked.length === 0) return;
        const orderCount = groupCasesByOrder(picked).size;
        openWorkOrderReplyModal({
            title: `回覆這台的交辦：${hostName}`,
            targetText: `${hostName} 的 ${picked.length} 個問題（${orderCount} 張單）`,
            draftKey: hostOrdersDraftKey(hostId, picked),
            multiTarget: false,
            multiIssues: picked.length > 1,
            aiContext: { issueLabel: picked.length === 1 ? picked[0].issueLabel : undefined, hostCount: 1 },
            // 這個入口的單位是「問題」（同一台主機的多個問題），復發提示用「N 個問題」計
            recurrence: { hosts: picked.filter(row => row.isRecurrence).length, unit: 'issue' },
            // 多個問題要全部都適用才換成「30 天後重新派工」的說法，不對永久的說成會重新派工
            dismissalReleases: picked.every(row => row.dismissalReleases === true),
            reuseIssueKey: null,
            submit: async payload => {
                const result = await submitHostReplies(picked, payload,
                    (orderId, body) => api.post(`/api/work-orders/${orderId}/reply`, body, { silent: true }));
                // 全部失敗：整筆當失敗，讓彈窗保留草稿與原因；部分成功：彙整訊息後正常收尾
                if (result.succeeded.length === 0) throw result.failed[0].error;
                const message = hostRepliesMessage(result, payload);
                toast(message.text, message.kind);
                return result;
            },
            // 被拒（單已結案、成員已非進行中、單已改派）：清單已過期，重新載入；訊息與草稿由彈窗保留
            onRejected: () => reload(),
            onApplied: () => reload()
        });
    }

    async function reload() {
        selection.clear();
        await load();
    }

    async function load() {
        const data = await api.get(`/api/host-detail/${hostId}/orders`);
        rows = data?.items ?? [];
        // 重新載入後勾選只保留還在清單且仍可回覆的列
        for (const key of [...selection.selected.keys()]) {
            if (!rows.some(row => row.caseId === key && row.canReply)) selection.selected.delete(key);
        }
        if (replyButtonHost && !replyBtn && currentUser && can(currentUser, 'Handle')) {
            replyBtn = button('回覆選取的問題', { variant: 'primary', size: 'sm', opens: 'dialog', onClick: openReply });
            replyBtn.disabled = true;
            replyButtonHost.appendChild(replyBtn);
        }
        render();
        selection.renderBar();
    }

    return { load, reload, rows: () => rows, selection, openReply };
}
