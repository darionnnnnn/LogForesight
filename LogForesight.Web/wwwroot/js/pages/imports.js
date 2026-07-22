/**
 * CSV 匯入（docs/WEB-SPEC.md §9.9）：上傳 → 預覽 → 套用。
 *
 * 預覽階段後端不寫入任何資料；套用時以 token 綁定先前的預覽結果，
 * 所以「預覽 A 檔卻套用 B 檔」不可能發生。
 */

import { api } from '../core/api.js';
import { renderTable, renderLoading, toast, confirmAction, withBusy } from '../core/ui.js';
import { formatDateTime } from '../core/format.js';

const previewCard = document.getElementById('preview-card');
const previewFile = document.getElementById('preview-file');
const previewSummary = document.getElementById('preview-summary');
const previewWarnings = document.getElementById('preview-warnings');
const previewRows = document.getElementById('preview-rows');
const applyButton = document.getElementById('preview-apply');

const KIND_NAMES = { Users: '使用者', Hosts: '主機', GroupAccess: '群組授權' };
const ACTION_META = {
    Add: { text: '新增', variant: 'success' },
    Update: { text: '更新', variant: 'primary' },
    Unchanged: { text: '不變', variant: 'light' },
    Remove: { text: '移除', variant: 'warning' },
    Error: { text: '錯誤', variant: 'danger' }
};

let currentPlan = null;
let currentKind = null;

// ── 上傳與預覽 ───────────────────────────────────────────────────────────────

for (const input of document.querySelectorAll('[data-upload]')) {
    input.addEventListener('change', async () => {
        const file = input.files?.[0];
        if (!file) return;

        const kind = input.dataset.upload;
        const formData = new FormData();
        formData.append('file', file);

        renderLoading(previewRows, 4);
        previewCard.classList.remove('d-none');
        previewCard.scrollIntoView({ behavior: 'smooth', block: 'start' });

        try {
            // FormData 上傳不經 core/api.js 的 JSON 包裝，但 CSRF 標頭與信封解析規則相同
            const response = await fetch(`/api/imports/${kind}/preview`, {
                method: 'POST',
                headers: { 'X-Requested-By': 'LogForesight' },
                credentials: 'same-origin',
                body: formData
            });

            const payload = await response.json();
            if (!response.ok || !payload.success) {
                throw new Error(payload?.error?.message ?? '預覽失敗');
            }

            currentPlan = payload.data;
            currentKind = kind;
            renderPreview();
        } catch (error) {
            previewCard.classList.add('d-none');
            toast(error.message, 'danger', 8000);
        } finally {
            input.value = '';   // 清空以便重新選同一個檔案
        }
    });
}

function renderPreview() {
    previewFile.textContent = `${KIND_NAMES[currentKind]}／${currentPlan.fileName}`;

    renderSummary();
    renderWarnings();
    renderRows();

    applyButton.disabled = !currentPlan.canApply;
    applyButton.textContent = currentPlan.canApply ? '套用' : '有錯誤，無法套用';
}

function renderSummary() {
    const items = [
        { label: '新增', value: currentPlan.addCount, variant: 'success' },
        { label: '更新', value: currentPlan.updateCount, variant: 'primary' },
        { label: '不變', value: currentPlan.unchangedCount, variant: 'secondary' },
        { label: '移除', value: currentPlan.removeCount, variant: 'warning' },
        { label: '錯誤', value: currentPlan.errorCount, variant: 'danger' }
    ];

    const wrap = document.createElement('div');
    wrap.className = 'd-flex flex-wrap gap-3';

    for (const item of items) {
        if (item.value === 0 && (item.label === '移除' || item.label === '錯誤')) continue;

        const box = document.createElement('div');
        box.className = 'text-center px-3';

        const value = document.createElement('div');
        value.className = `lf-stat__value text-${item.variant}`;
        value.textContent = String(item.value);

        const label = document.createElement('div');
        label.className = 'lf-stat__label';
        label.textContent = item.label;

        box.append(value, label);
        wrap.appendChild(box);
    }

    previewSummary.replaceChildren(wrap);

    if (currentPlan.newGroups.length > 0) {
        const note = document.createElement('div');
        note.className = 'alert alert-info mt-3 mb-0';
        note.textContent = `套用時將自動建立群組：${currentPlan.newGroups.join('、')}`;
        previewSummary.appendChild(note);
    }
}

function renderWarnings() {
    previewWarnings.replaceChildren();
    if (!currentPlan.warnings || currentPlan.warnings.length === 0) return;

    const box = document.createElement('div');
    box.className = 'alert alert-warning';

    const title = document.createElement('div');
    title.className = 'fw-semibold mb-2';
    title.textContent = '請確認以下事項';
    box.appendChild(title);

    const list = document.createElement('ul');
    list.className = 'mb-0 ps-3';
    for (const warning of currentPlan.warnings) {
        const item = document.createElement('li');
        item.textContent = warning;
        list.appendChild(item);
    }
    box.appendChild(list);

    previewWarnings.appendChild(box);
}

function renderRows() {
    renderTable(previewRows, {
        columns: [
            { title: '行號', render: r => (r.lineNumber > 0 ? String(r.lineNumber) : '—') },
            { title: '動作', render: r => actionBadge(r.action) },
            { title: '對象', render: r => r.key },
            { title: '說明', render: r => r.error ?? r.description }
        ],
        rows: currentPlan.rows,
        empty: { title: '檔案中沒有資料列', hint: '請確認檔案是否只有標題列。' }
    });
}

function actionBadge(action) {
    const meta = ACTION_META[action] ?? { text: action, variant: 'secondary' };
    const span = document.createElement('span');
    span.className = `lf-badge lf-badge--${meta.variant}`;
    span.textContent = meta.text;
    return span;
}

// ── 套用 ─────────────────────────────────────────────────────────────────────

applyButton.addEventListener('click', async () => {
    // 移除是全量取代造成的隱形後果，必須在確認框裡具體講清楚影響幾筆
    const message = currentPlan.removeCount > 0
        ? `將新增 ${currentPlan.addCount} 筆、更新 ${currentPlan.updateCount} 筆，` +
          `並移除 ${currentPlan.removeCount} 筆未列於檔案中的既有授權。移除後相關人員將立即失去對應主機的檢視權限。`
        : `將新增 ${currentPlan.addCount} 筆、更新 ${currentPlan.updateCount} 筆資料。`;

    const confirmed = await confirmAction({
        title: `套用${KIND_NAMES[currentKind]}匯入`,
        message,
        confirmText: '套用',
        confirmVariant: currentPlan.removeCount > 0 ? 'danger' : 'primary'
    });
    if (!confirmed) return;

    const restore = withBusy(applyButton, '套用中');
    try {
        const result = await api.post(`/api/imports/${currentKind}/apply`, { token: currentPlan.token });

        toast(`匯入完成：新增 ${result.added}、更新 ${result.updated}` +
              (result.removed > 0 ? `、移除 ${result.removed}` : ''), 'success', 6000);

        previewCard.classList.add('d-none');
        currentPlan = null;
        await loadLogs();
    } catch {
        // 錯誤已由 api.js 顯示
    } finally {
        restore();
    }
});

document.getElementById('preview-cancel').addEventListener('click', () => {
    previewCard.classList.add('d-none');
    currentPlan = null;
});

// ── 匯入紀錄 ─────────────────────────────────────────────────────────────────

async function loadLogs() {
    const container = document.getElementById('import-logs');
    renderLoading(container, 3);

    const logs = await api.get('/api/imports/logs');

    renderTable(container, {
        columns: [
            { title: '時間', render: l => formatDateTime(l.createdAt) },
            { title: '類型', render: l => KIND_NAMES[l.kind] ?? l.kind },
            { title: '檔案', render: l => l.fileName },
            { title: '操作者', render: l => l.account },
            {
                title: '結果',
                render: l => `新增 ${l.addedCount}、更新 ${l.updatedCount}` +
                             (l.removedCount > 0 ? `、移除 ${l.removedCount}` : '') +
                             (l.createdGroups?.length ? `（新建群組：${l.createdGroups.join('、')}）` : '')
            }
        ],
        rows: logs,
        empty: { title: '尚無匯入紀錄', hint: '上傳並套用 CSV 後，這裡會留下每次匯入的結果。' }
    });
}

loadLogs();
