/**
 * 交辦總覽的批次改派（A12、F14-3）：進行中清單勾選多張單 →「改派給…」→ 預覽 → 套用。
 *
 * 流程在頁面內的就地面板完成（不開 modal，避免疊開）：選新處理人 → 預覽（每張是原單換人、併入哪張或略過，
 * 以授與補上的台數，目標是否暫停或沒有 email）→ 套用，逐張回報成功或失敗原因。
 * 預覽與套用在伺服器共用同一份分類，這裡只呈現後端的數字，不自己算。
 * 候選人沿用全站手動派工的 `candidatesToUserOptions`（`POST /api/work-orders/candidates`），不另寫一套標記。
 */

import { api } from '../core/api.js';
import { appUrl } from '../core/paths.js';
import {
    button, renderTable, renderLoading, searchableUserSelect, candidatesToUserOptions, toast, withBusy
} from '../core/ui.js';
import { roleText, statusNote } from '../core/elements.js';
import { statusBadge } from '../core/format.js';
import { createSeqGuard } from '../core/async-guards.js';

/** 一次最多改派的張數（與伺服器 WorkOrderBulkReassignService.MaxOrders 一致；伺服器為準，這裡只是先提示） */
export const BULK_REASSIGN_MAX = 100;

/** 預覽頂端的說明：數字全取自伺服器預覽；回傳 [{ type: 'info'|'warning', text }] */
export function bulkPreviewLines(preview) {
    const lines = [{
        type: 'info',
        text: `共 ${preview.totalOrders} 張：原單換人 ${preview.newCount} 張、併入對方既有的單 ${preview.mergeCount} 張、略過 ${preview.skipCount} 張；會移動 ${preview.movedMembers} 台。`
    }];
    const name = preview.handlerName;
    if (preview.grantHostCount > 0) {
        lines.push({ type: 'info', text: `${name} 目前看不到其中 ${preview.grantHostCount} 台主機，改派後會以交辦授與補上檢視範圍。` });
    }
    if (preview.handlerPaused) {
        lines.push({ type: 'warning', text: `${name} 目前暫停接單：改派仍會成功，只是夜間自動派工不會再派新單給他。` });
    }
    if (preview.handlerHasNoEmail) {
        lines.push({ type: 'warning', text: `${name} 沒有設定 email，不會收到通知，請自行告知。` });
    }
    if (preview.handlerCannotHandle) {
        lines.push({ type: 'warning', text: `${name} 沒有處理能力，改派後他無法回覆這些單。` });
    }
    return lines;
}

/** 套用結果的一句話摘要與等級（有失敗就是 warning） */
export function bulkResultSummary(result) {
    const parts = [`已改派 ${result.succeededCount} 張（原單換人 ${result.newCount}、併入既有單 ${result.mergeCount}，共 ${result.movedMembers} 台）`];
    if (result.skippedCount > 0) parts.push(`略過 ${result.skippedCount} 張`);
    if (result.failedCount > 0) parts.push(`失敗 ${result.failedCount} 張`);
    return { type: result.failedCount > 0 ? 'warning' : 'success', text: `${parts.join('，')}。` };
}

/** 預覽列的「改派方式」文字 */
export function bulkOutcomeText(row) {
    if (row.outcome === 'new') return '原單換處理人';
    if (row.outcome === 'merge') return `併入 #${row.targetWorkOrderId}`;
    return `略過：${row.reason || ''}`;
}

function orderLink(workOrderId) {
    const a = document.createElement('a');
    a.href = appUrl('/work-orders/' + workOrderId);
    a.className = 'lf-text-data';
    a.textContent = '#' + workOrderId;
    return a;
}

/**
 * 建立批次改派面板。
 * @param {object} opts
 *   container       面板容器（預設 d-none；開啟時移除、結束時清空並隱藏）
 *   getSelectedIds  () => 目前勾選的單號陣列
 *   onApplied       套用完成（含部分失敗）後呼叫：頁面清除勾選、重載清單與負載看板
 */
export function createBulkReassignPanel({ container, getSelectedIds, onApplied }) {
    const guard = createSeqGuard();
    let working = null; // { ids, handlerId, notifyNow }

    function close() {
        guard.next();
        working = null;
        container.replaceChildren();
        container.classList.add('d-none');
    }

    function show(...nodes) {
        container.replaceChildren(...nodes);
        container.classList.remove('d-none');
    }

    function heading(text) {
        const h = document.createElement('div');
        h.className = 'mb-2';
        h.appendChild(roleText('lf-text-title', text));
        return h;
    }

    async function open() {
        const ids = getSelectedIds();
        if (ids.length === 0) return;
        if (ids.length > BULK_REASSIGN_MAX) {
            toast(`一次最多改派 ${BULK_REASSIGN_MAX} 張，請取消部分勾選。`, 'warning');
            return;
        }
        const seq = guard.next();
        const loading = document.createElement('div');
        renderLoading(loading, 2);
        show(loading);

        let candidates = [];
        try {
            candidates = await api.post('/api/work-orders/candidates', { hostIds: [], source: null, eventId: null }, { silent: true });
        } catch {
            candidates = [];
        }
        if (!guard.isCurrent(seq)) return;
        renderChoose(ids, candidatesToUserOptions(candidates));
    }

    function renderChoose(ids, options) {
        working = { ids, handlerId: null, notifyNow: false };

        const picker = searchableUserSelect(options.filter(u => u.active), {
            includeNone: true,
            noneLabel: '請選擇新處理人…'
        });
        const label = document.createElement('label');
        label.className = 'form-label lf-text-label';
        label.textContent = '新處理人';

        const notifyCheck = document.createElement('input');
        notifyCheck.type = 'checkbox';
        notifyCheck.className = 'form-check-input mt-0';
        notifyCheck.id = 'wo-bulk-notify-now';
        const notifyLabel = document.createElement('label');
        notifyLabel.className = 'form-check-label d-flex align-items-center gap-1 mt-2';
        notifyLabel.htmlFor = notifyCheck.id;
        notifyLabel.append(notifyCheck, document.createTextNode('立即通知處理人（不等摘要時刻）'));

        const previewBtn = button('預覽', { variant: 'primary', size: null, onClick: async () => {
            const value = picker.getValue();
            if (!value) {
                toast('請選擇新處理人', 'warning');
                return;
            }
            working.handlerId = Number(value);
            working.notifyNow = notifyCheck.checked === true;
            const restore = withBusy(previewBtn, '預覽中…');
            try {
                await runPreview();
            } finally {
                restore();
            }
        } });
        const cancelBtn = button('取消', { size: null, onClick: close });

        const actions = document.createElement('div');
        actions.className = 'd-flex flex-wrap gap-2 mt-3';
        actions.append(previewBtn, cancelBtn);

        show(heading(`改派 ${ids.length} 張交辦單`), label, picker.element, notifyLabel, actions);
    }

    async function runPreview() {
        const seq = guard.next();
        let preview;
        try {
            preview = await api.post('/api/work-orders/bulk-reassign/preview', {
                workOrderIds: working.ids, handlerId: working.handlerId
            });
        } catch {
            return; // 錯誤訊息已由 api.js 顯示
        }
        if (!guard.isCurrent(seq)) return;
        renderPreview(preview);
    }

    function renderPreview(preview) {
        const notes = document.createElement('div');
        notes.className = 'd-flex flex-column gap-2 mb-3';
        for (const line of bulkPreviewLines(preview)) notes.appendChild(statusNote(line.type, line.text));

        const tableBox = document.createElement('div');
        renderTable(tableBox, {
            caption: '批次改派預覽',
            columns: [
                { title: '單號', className: 'text-nowrap', render: r => orderLink(r.workOrderId) },
                { title: '問題', className: 'lf-minw-lg', render: r => roleText('lf-text-title', r.issueLabel) },
                { title: '目前處理人', className: 'lf-minw-sm', render: r => roleText('lf-text-data', r.currentHandlerName) },
                { title: '改派方式', className: 'lf-minw-md', render: r => (r.outcome === 'skip'
                    ? statusBadge(bulkOutcomeText(r), 'secondary')
                    : roleText('lf-text-data', bulkOutcomeText(r))) },
                { title: '台數', className: 'text-end text-nowrap', render: r => roleText('lf-text-data', r.outcome === 'skip' ? '—' : String(r.memberCount)) },
                { title: '補上授與', className: 'text-end text-nowrap', render: r => roleText('lf-text-data', r.outcome === 'skip' ? '—' : `${r.grantHostCount} 台`) }
            ],
            rows: preview.rows,
            empty: { title: '沒有可預覽的交辦單', hint: '勾選的單可能已被別人改派或結案，請返回重新勾選。' }
        });

        const doable = preview.newCount + preview.mergeCount;
        const applyBtn = button(`確定改派 ${doable} 張`, { variant: 'primary', size: null, disabled: doable === 0, onClick: async () => {
            const restore = withBusy(applyBtn, '改派中…');
            try {
                await runApply();
            } finally {
                restore();
            }
        } });
        const backBtn = button('返回修改', { size: null, onClick: () => open() });
        const cancelBtn = button('取消', { size: null, onClick: close });
        const actions = document.createElement('div');
        actions.className = 'd-flex flex-wrap gap-2 mt-3';
        actions.append(applyBtn, backBtn, cancelBtn);

        show(heading(`改派給 ${preview.handlerName}`), notes, tableBox, actions);
    }

    async function runApply() {
        const seq = guard.next();
        let result;
        try {
            result = await api.post('/api/work-orders/bulk-reassign', {
                workOrderIds: working.ids, handlerId: working.handlerId, notifyNow: working.notifyNow
            });
        } catch {
            // 整批被拒（例如超過上限）：訊息已顯示；已部分執行的情況只會在回應內逐張列出
            return;
        }
        // 伺服器已經改派了：不論對話框在等待期間是否被取消或換了畫面，清勾選與重載清單的回呼一律要呼叫；
        // 只有結果畫面受序號保護（過期的回應不蓋掉較新的畫面）
        if (guard.isCurrent(seq)) renderResult(result);
        onApplied?.(result);
    }

    function renderResult(result) {
        const summary = bulkResultSummary(result);
        const notes = document.createElement('div');
        notes.className = 'd-flex flex-column gap-2 mb-3';
        notes.appendChild(statusNote(summary.type, summary.text));
        if (result.handlerHasNoEmail) notes.appendChild(statusNote('warning', `${result.handlerName} 沒有設定 email，不會收到通知，請自行告知。`));
        if (result.handlerCannotHandle) notes.appendChild(statusNote('warning', `${result.handlerName} 沒有處理能力，無法回覆這些單。`));

        // 只列需要注意的：失敗與略過；成功的張數已在摘要
        const issues = result.rows.filter(r => r.status !== 'ok');
        const tableBox = document.createElement('div');
        if (issues.length > 0) {
            renderTable(tableBox, {
                caption: '批次改派未完成的單',
                columns: [
                    { title: '單號', className: 'text-nowrap', render: r => orderLink(r.workOrderId) },
                    { title: '問題', className: 'lf-minw-lg', render: r => roleText('lf-text-title', r.issueLabel) },
                    { title: '結果', className: 'text-nowrap', render: r => (r.status === 'failed' ? statusBadge('失敗', 'danger') : statusBadge('略過', 'secondary')) },
                    { title: '原因', className: 'lf-minw-lg', render: r => roleText('lf-text-meta', r.reason || '') }
                ],
                rows: issues,
                empty: { title: '沒有需要注意的單', hint: '所有勾選的單都已改派。' }
            });
        }

        const doneBtn = button('完成', { variant: 'primary', size: null, onClick: close });
        const actions = document.createElement('div');
        actions.className = 'mt-3';
        actions.appendChild(doneBtn);
        show(heading('批次改派結果'), notes, tableBox, actions);
    }

    return { open, close };
}
