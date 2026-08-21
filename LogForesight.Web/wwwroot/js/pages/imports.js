/**
 * 資料匯入（docs/WEB-SPEC.md §9.9）：上傳 → 預覽 → 套用，＋歷次匯入紀錄。
 *
 * §2a（回饋第十一輪）起本頁只剩「負責人」一種 CSV——使用者／主機／群組授權三種已退役，
 * NetIQ 掃描匯入搬到「NetIQ 維護 > 匯入」分頁（§1）。匯入紀錄仍留在本頁且含全部來源
 * （含已退役類型的歷史紀錄）：匯入紀錄是稽核性質的歷史事實，不隨功能退役消失。
 */

import { api } from '../core/api.js';
import { renderTable, renderLoading, toast, confirmAction, withBusy, guardLoad } from '../core/ui.js';
import { formatDateTime, formatUserName } from '../core/format.js';

const previewCard = document.getElementById('preview-card');
const previewFile = document.getElementById('preview-file');
const previewSummary = document.getElementById('preview-summary');
const previewWarnings = document.getElementById('preview-warnings');
const previewRows = document.getElementById('preview-rows');
const applyButton = document.getElementById('preview-apply');

// 已退役類型（Users／Hosts／GroupAccess）保留顯示名稱：歷次匯入紀錄裡還有它們的資料列
const KIND_NAMES = { Users: '使用者', Hosts: '主機', GroupAccess: '群組授權', Owners: '負責人', Netiq: 'NetIQ 掃描匯入' };
const ACTION_META = {
    Add: { text: '新增', variant: 'success' },
    Update: { text: '更新', variant: 'primary' },
    Unchanged: { text: '不變', variant: 'light' },
    Error: { text: '錯誤', variant: 'danger' }
};

let currentPlan = null;
let currentKind = null;

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
            // 檔案上傳同樣走 api.js（它認得 FormData，會讓瀏覽器自己帶 multipart boundary）：
            // CSRF 標頭、信封解析、401 導頁、掛載前綴都在那裡一次處理好
            // silent：下面的 catch 自己跳 toast（含較長的停留時間），不加會跳兩次
            currentPlan = await api.post(`/api/imports/${kind}/preview`, formData, { silent: true });
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
    previewFile.textContent = `${KIND_NAMES[currentKind] ?? currentKind}／${currentPlan.fileName}`;

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
        { label: '錯誤', value: currentPlan.errorCount, variant: 'danger' }
    ];

    const wrap = document.createElement('div');
    wrap.className = 'd-flex flex-wrap gap-3';

    for (const item of items) {
        if (item.value === 0 && item.label === '錯誤') continue;

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

    if (currentPlan.newUsers && currentPlan.newUsers.length > 0) {
        const note = document.createElement('div');
        note.className = 'alert alert-info mt-3 mb-0';
        note.textContent = `套用時將自動建立 ${currentPlan.newUsers.length} 個使用者帳號：${currentPlan.newUsers.join('、')}`;
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

// ── 套用 ──────────────────────────────────────────────────────────────────────

applyButton.addEventListener('click', async () => {
    const confirmed = await confirmAction({
        title: `套用${KIND_NAMES[currentKind] ?? currentKind}匯入`,
        message: `將新增 ${currentPlan.addCount} 筆、更新 ${currentPlan.updateCount} 筆資料。`,
        confirmText: '套用',
        confirmVariant: 'primary'
    });
    if (!confirmed) return;

    const restore = withBusy(applyButton, '套用中');
    try {
        const result = await api.post(`/api/imports/${currentKind}/apply`, { token: currentPlan.token });

        toast(`匯入完成：新增 ${result.added}、更新 ${result.updated}`, 'success', 6000);

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

// ── 匯入紀錄（含 CSV 與 NetIQ 掃描匯入的全部歷史） ────────────────────────────

async function loadLogs() {
    const container = document.getElementById('import-logs');
    renderLoading(container, 3);

    const logs = await api.get('/api/imports/logs');

    renderTable(container, {
        columns: [
            { title: '時間', render: l => formatDateTime(l.createdAt) },
            { title: '類型', render: l => KIND_NAMES[l.kind] ?? l.kind },
            { title: '檔案／來源', render: l => l.fileName },
            { title: '操作者', render: l => formatUserName(l.displayName, l.account) },
            {
                title: '結果',
                render: l => `新增 ${l.addedCount}、更新 ${l.updatedCount}` +
                             (l.removedCount > 0 ? `、移除 ${l.removedCount}` : '') +
                             (l.revivedCount > 0 ? `、復活 ${l.revivedCount}` : '') +
                             (l.createdGroups?.length ? `（新建群組：${l.createdGroups.join('、')}）` : '')
            }
        ],
        rows: logs,
        empty: { title: '尚無匯入紀錄', hint: '上傳並套用負責人 CSV，或於「NetIQ 維護 > 匯入」掃描匯入後，這裡會留下每次匯入的結果。' }
    });
}

guardLoad(document.getElementById('import-logs'), loadLogs);
