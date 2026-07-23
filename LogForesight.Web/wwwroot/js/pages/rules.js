/**
 * 規則維護（docs/WEB-SPEC.md §9.7）。
 *
 * 四層保護在 UI 上的體現：
 *   - builtin 不顯示刪除鈕（只能停用）
 *   - builtin 被改過時顯示「已修改」徽章與「回復預設」鈕
 *   - 儲存前先跑後端驗證，不合格不寫入
 */

import { api } from '../core/api.js';
import { renderTable, renderLoading, toast, confirmAction, withBusy, button, bindTabs, renderChips } from '../core/ui.js';
import { severityBadge, statusBadge, formatDate } from '../core/format.js';

const CATEGORY_NAMES = {
    Storage: '儲存裝置', Hardware: '硬體', Security: '安全', Service: '服務',
    Backup: '備份', Config: '設定', Resource: '資源', Other: '其他'
};

const SEVERITY_ORDER = ['Critical', 'High', 'Medium', 'Low'];

// chip 篩選狀態（§5.1 D-2）：狀態/來源/抑制為單選（含「全部」＝空字串），嚴重度/類別為多選（空集合＝不限）
const chipFilters = {
    status: '',
    origin: '',
    suppression: '',
    severities: new Set(),
    categories: new Set()
};

const ruleModal = new bootstrap.Modal(document.getElementById('rule-modal'));
const restoreModal = new bootstrap.Modal(document.getElementById('restore-modal'));
const suppressModal = new bootstrap.Modal(document.getElementById('suppress-modal'));

let rules = [];
let suppressions = [];
let editingRule = null;
let restoringRuleId = null;
let suppressingRuleId = null;
let hostOptionsLoaded = false;   // 抑制 modal 的主機下拉延遲載入

const kbCollapse = new bootstrap.Collapse(document.getElementById('rule-kb'), { toggle: false });

bindTabs(document.getElementById('rule-tabs'));

async function load() {
    renderLoading(document.getElementById('rule-list'), 8);

    [rules, suppressions] = await Promise.all([
        api.get('/api/rules'),
        api.get('/api/rules/suppressions')
    ]);

    renderRules();
    renderSuppressions();
}

/**
 * 篩選 toolbar（§5.1 D-2）：狀態/來源/抑制單選 chip，嚴重度/類別多選 chip，取代舊版單一下拉——
 * 舊版下拉一次只能選一種條件（例如「已修改」跟「自訂規則」不能同時看），chip 各自獨立可疊加。
 */
function setupToolbar() {
    renderChips(document.getElementById('rule-status-chips'), {
        items: [
            { value: '', label: '全部' },
            { value: 'enabled', label: '已啟用' },
            { value: 'disabled', label: '已停用' },
            { value: 'modified', label: '已修改' }
        ],
        attr: 'status',
        activeValues: [chipFilters.status],
        multi: false,
        onToggle: value => { chipFilters.status = value; renderRules(); }
    });

    renderChips(document.getElementById('rule-origin-chips'), {
        items: [
            { value: '', label: '全部' },
            { value: 'builtin', label: '內建' },
            { value: 'custom', label: '自訂' }
        ],
        attr: 'origin',
        activeValues: [chipFilters.origin],
        multi: false,
        onToggle: value => { chipFilters.origin = value; renderRules(); }
    });

    renderChips(document.getElementById('rule-suppression-chips'), {
        items: [
            { value: '', label: '全部' },
            { value: 'suppressed', label: '已抑制' },
            { value: 'none', label: '未抑制' }
        ],
        attr: 'suppression',
        activeValues: [chipFilters.suppression],
        multi: false,
        onToggle: value => { chipFilters.suppression = value; renderRules(); }
    });

    renderChips(document.getElementById('rule-severity-chips'), {
        items: SEVERITY_ORDER.map(s => ({ value: s, label: s })),
        attr: 'severity',
        activeValues: [...chipFilters.severities],
        multi: true,
        onToggle: (value, active) => {
            if (active) chipFilters.severities.add(value); else chipFilters.severities.delete(value);
            renderRules();
        }
    });

    renderChips(document.getElementById('rule-category-chips'), {
        items: Object.entries(CATEGORY_NAMES).map(([value, label]) => ({ value, label })),
        attr: 'category',
        activeValues: [...chipFilters.categories],
        multi: true,
        onToggle: (value, active) => {
            if (active) chipFilters.categories.add(value); else chipFilters.categories.delete(value);
            renderRules();
        }
    });

    document.getElementById('rule-sort').addEventListener('change', renderRules);
}

function sortRules(list) {
    const by = document.getElementById('rule-sort').value;
    const sorted = [...list];

    if (by === 'severity') {
        sorted.sort((a, b) => SEVERITY_ORDER.indexOf(a.severity) - SEVERITY_ORDER.indexOf(b.severity));
    } else if (by === 'category') {
        sorted.sort((a, b) => (CATEGORY_NAMES[a.category] ?? a.category).localeCompare(CATEGORY_NAMES[b.category] ?? b.category, 'zh-Hant'));
    } else if (by === 'threshold') {
        sorted.sort((a, b) => b.countThreshold - a.countThreshold);
    } else {
        sorted.sort((a, b) => a.id.localeCompare(b.id));
    }
    return sorted;
}

function renderRules() {
    const keyword = document.getElementById('rule-search').value.trim().toLowerCase();

    let filtered = rules;
    if (keyword) {
        filtered = filtered.filter(r =>
            r.id.toLowerCase().includes(keyword) ||
            r.sourcePattern.toLowerCase().includes(keyword) ||
            r.description.toLowerCase().includes(keyword) ||
            r.eventIds.some(id => String(id).includes(keyword)));
    }

    if (chipFilters.status === 'enabled') filtered = filtered.filter(r => r.enabled);
    if (chipFilters.status === 'disabled') filtered = filtered.filter(r => !r.enabled);
    if (chipFilters.status === 'modified') filtered = filtered.filter(r => r.isModified);

    if (chipFilters.origin === 'builtin') filtered = filtered.filter(r => r.origin !== 'custom');
    if (chipFilters.origin === 'custom') filtered = filtered.filter(r => r.origin === 'custom');

    if (chipFilters.suppression === 'suppressed') filtered = filtered.filter(r => !!r.suppression);
    if (chipFilters.suppression === 'none') filtered = filtered.filter(r => !r.suppression);

    if (chipFilters.severities.size > 0) filtered = filtered.filter(r => chipFilters.severities.has(r.severity));
    if (chipFilters.categories.size > 0) filtered = filtered.filter(r => chipFilters.categories.has(r.category));

    filtered = sortRules(filtered);

    document.getElementById('rule-count').textContent = `共 ${filtered.length} 條`;

    renderTable(document.getElementById('rule-list'), {
        columns: [
            { title: '規則', render: r => ruleCell(r) },
            { title: '比對', render: r => matchCell(r) },
            { title: '類別', render: r => CATEGORY_NAMES[r.category] ?? r.category },
            { title: '嚴重度', render: r => severityBadge(r.severity) },
            { title: '門檻', className: 'text-end', render: r => String(r.countThreshold) },
            { title: '狀態', render: r => statusCell(r) },
            { title: '', className: 'text-end', render: r => actionsCell(r) }
        ],
        rows: filtered,
        empty: { title: '沒有符合條件的規則', hint: '請調整搜尋或篩選條件。' }
    });
}

function ruleCell(rule) {
    const wrap = document.createElement('div');

    const id = document.createElement('div');
    id.className = 'font-monospace small';
    id.textContent = rule.id;
    wrap.appendChild(id);

    const desc = document.createElement('div');
    desc.textContent = rule.description;
    wrap.appendChild(desc);

    return wrap;
}

function matchCell(rule) {
    const wrap = document.createElement('div');

    const source = document.createElement('div');
    source.className = 'font-monospace small';
    source.textContent = rule.sourcePattern;
    wrap.appendChild(source);

    const ids = document.createElement('div');
    ids.className = 'small text-muted';
    ids.textContent = rule.matchAllEventIds ? '全部事件' : rule.eventIds.join(', ');
    wrap.appendChild(ids);

    return wrap;
}

function statusCell(rule) {
    const wrap = document.createElement('div');
    wrap.className = 'd-flex flex-column gap-1 align-items-start';

    wrap.appendChild(statusBadge(rule.enabled ? '啟用' : '停用', rule.enabled ? 'success' : 'neutral'));

    if (rule.origin === 'custom') {
        wrap.appendChild(statusBadge('自訂', 'info'));
    } else if (rule.isModified) {
        // builtin 被改過要標示出來：程式改版時這條不會自動跟進新種子
        wrap.appendChild(statusBadge('已修改', 'warning', {
            title: rule.modifiedByName
                ? `由 ${rule.modifiedByName} 於 ${formatDate(rule.modifiedAt)} 修改`
                : '已被修改過'
        }));
    }

    if (rule.seedHasNewerVersion) {
        wrap.appendChild(statusBadge('種子有新版', 'primary', {
            title: '程式內建種子有更新的內容，可用「回復預設」套用'
        }));
    }

    if (rule.suppression) {
        wrap.appendChild(statusBadge(rule.suppression.isExpired ? '抑制已到期' : '已抑制', 'dark', {
            title: `${rule.suppression.host}：${rule.suppression.reason}`
        }));
    }

    return wrap;
}

function actionsCell(rule) {
    const wrap = document.createElement('div');
    wrap.className = 'd-flex gap-1 justify-content-end flex-wrap';

    wrap.appendChild(button('', { variant: 'outline-primary', icon: 'pencil', title: '編輯', onClick: () => openRuleModal(rule) }));
    wrap.appendChild(button('', {
        variant: 'outline-secondary',
        icon: rule.enabled ? 'slash-circle' : 'plus-lg',
        title: rule.enabled ? '停用' : '啟用',
        onClick: () => toggleEnabled(rule)
    }));
    wrap.appendChild(button('', { variant: 'outline-dark', icon: 'bell-slash', title: '抑制', onClick: () => openSuppressModal(rule) }));

    if (rule.canRestore) {
        wrap.appendChild(button('', { variant: 'outline-warning', icon: 'arrow-counterclockwise', title: '回復預設', onClick: () => openRestoreModal(rule) }));
    }

    // builtin 沒有刪除鈕——不需要它時請停用（可隨時恢復）
    if (rule.canDelete) {
        wrap.appendChild(button('', { variant: 'outline-danger', icon: 'trash', title: '刪除', onClick: () => deleteRule(rule) }));
    }

    return wrap;
}

// ── 編輯 ─────────────────────────────────────────────────────────────────────

function openRuleModal(rule) {
    editingRule = rule;
    document.getElementById('rule-validation').replaceChildren();

    document.getElementById('rule-modal-title').textContent = rule ? `編輯規則 ${rule.id}` : '新增規則';
    document.getElementById('rule-id').value = rule?.id ?? 'custom-';
    document.getElementById('rule-id').disabled = !!rule;   // Id 是穩定識別鍵，建立後不可改
    document.getElementById('rule-id-hint').textContent = rule
        ? 'Id 一經建立即不可變更（seed 同步與抑制設定都靠它比對）。'
        : '新規則必須以 custom- 開頭。';

    document.getElementById('rule-source').value = rule?.sourcePattern ?? '';
    document.getElementById('rule-event-ids').value = rule?.eventIds.join(', ') ?? '';
    document.getElementById('rule-match-all').checked = rule?.matchAllEventIds ?? false;
    document.getElementById('rule-category').value = rule?.category ?? 'Other';
    document.getElementById('rule-severity').value = rule?.severity ?? 'Medium';
    document.getElementById('rule-description').value = rule?.description ?? '';
    document.getElementById('rule-threshold').value = rule?.countThreshold ?? 1;
    document.getElementById('rule-plain').value = rule?.plainExplanation ?? '';
    document.getElementById('rule-impact').value = rule?.impact ?? '';
    document.getElementById('rule-causes').value = rule?.likelyCauses.join('\n') ?? '';
    document.getElementById('rule-steps').value = rule?.nextSteps.join('\n') ?? '';
    document.getElementById('rule-enabled').checked = rule?.enabled ?? true;

    // 處置知識庫預設收合（漸進揭露）；已填內容的規則自動展開，摘要行顯示填了幾欄
    const kbFilled = [rule?.plainExplanation, rule?.impact, rule?.likelyCauses?.length, rule?.nextSteps?.length]
        .filter(Boolean).length;
    document.getElementById('rule-kb-summary').textContent = kbFilled > 0 ? `已填 ${kbFilled}/4 欄` : '未填寫';
    const kbToggle = document.querySelector('[data-bs-target="#rule-kb"]');
    kbToggle.setAttribute('aria-expanded', kbFilled > 0 ? 'true' : 'false');
    if (kbFilled > 0) kbCollapse.show(); else kbCollapse.hide();

    ruleModal.show();
}

function collectRule() {
    const eventIds = document.getElementById('rule-event-ids').value
        .split(',')
        .map(s => Number(s.trim()))
        .filter(n => Number.isInteger(n) && n > 0);

    return {
        id: document.getElementById('rule-id').value.trim(),
        enabled: document.getElementById('rule-enabled').checked,
        sourcePattern: document.getElementById('rule-source').value.trim(),
        eventIds,
        matchAllEventIds: document.getElementById('rule-match-all').checked,
        category: document.getElementById('rule-category').value,
        severity: document.getElementById('rule-severity').value,
        description: document.getElementById('rule-description').value.trim(),
        countThreshold: Number(document.getElementById('rule-threshold').value) || 1,
        plainExplanation: document.getElementById('rule-plain').value.trim(),
        impact: document.getElementById('rule-impact').value.trim(),
        likelyCauses: splitLines(document.getElementById('rule-causes').value),
        nextSteps: splitLines(document.getElementById('rule-steps').value)
    };
}

function splitLines(text) {
    return text.split('\n').map(s => s.trim()).filter(Boolean);
}

document.getElementById('rule-validate').addEventListener('click', async () => {
    const result = await api.post('/api/rules/validate', collectRule());
    showValidation(result);

    if (result.isValid && result.warnings.length === 0) toast('這條規則通過驗證', 'success');
});

function showValidation(result) {
    const container = document.getElementById('rule-validation');
    container.replaceChildren();

    if (result.errors.length > 0) {
        container.appendChild(alertBox('danger', '規則不合格，無法儲存', result.errors));
    }
    if (result.warnings.length > 0) {
        container.appendChild(alertBox('warning', '請注意', result.warnings));
    }
    if (result.isValid && result.warnings.length === 0) {
        container.appendChild(alertBox('success', '通過驗證', []));
    }
}

function alertBox(variant, title, items) {
    const box = document.createElement('div');
    box.className = `alert alert-${variant}`;

    const titleEl = document.createElement('div');
    titleEl.className = 'fw-semibold';
    titleEl.textContent = title;
    box.appendChild(titleEl);

    if (items.length > 0) {
        const list = document.createElement('ul');
        list.className = 'mb-0 ps-3 small';
        for (const item of items) {
            const li = document.createElement('li');
            li.textContent = item;
            list.appendChild(li);
        }
        box.appendChild(list);
    }

    return box;
}

document.getElementById('rule-form').addEventListener('submit', async event => {
    event.preventDefault();

    const saveButton = document.getElementById('rule-save');
    const restore = withBusy(saveButton, '儲存中');

    try {
        await api.post('/api/rules', collectRule());
        toast(editingRule ? '已更新規則' : '已新增規則', 'success');
        ruleModal.hide();
        await load();
    } catch {
        // 後端的驗證錯誤已由 api.js 以 toast 顯示
    } finally {
        restore();
    }
});

async function toggleEnabled(rule) {
    await api.put(`/api/rules/${encodeURIComponent(rule.id)}/enabled`, { enabled: !rule.enabled });
    toast(`已${rule.enabled ? '停用' : '啟用'}規則 ${rule.id}`, 'success');
    await load();
}

async function deleteRule(rule) {
    const suppressionCount = suppressions.filter(s => s.ruleId === rule.id).length;

    const confirmed = await confirmAction({
        title: '刪除自訂規則',
        message: `將刪除規則「${rule.id}」（${rule.description}）` +
                 (suppressionCount > 0 ? `及其 ${suppressionCount} 筆抑制設定` : '') +
                 '。此操作無法復原。',
        confirmText: '刪除'
    });
    if (!confirmed) return;

    await api.delete(`/api/rules/${encodeURIComponent(rule.id)}`);
    toast(`已刪除規則 ${rule.id}`, 'success');
    await load();
}

// ── 回復預設 ─────────────────────────────────────────────────────────────────

async function openRestoreModal(rule) {
    restoringRuleId = rule.id;
    const body = document.getElementById('restore-body');
    renderLoading(body, 3);
    restoreModal.show();

    const preview = await api.get(`/api/rules/${encodeURIComponent(rule.id)}/restore-preview`);
    body.replaceChildren();

    if (preview.differences.length === 0) {
        body.appendChild(alertBox('info', '目前內容與程式內建預設相同，回復不會有任何變化。', []));
        return;
    }

    const note = document.createElement('div');
    note.className = 'alert alert-light border small';
    note.textContent = '回復只還原規則內容，會保留您目前的啟用/停用設定。';
    body.appendChild(note);

    const wrap = document.createElement('div');
    wrap.className = 'lf-table-wrap';

    const table = document.createElement('table');
    table.className = 'table table-sm mb-0';
    table.innerHTML = '<thead><tr><th>欄位</th><th>目前內容</th><th>內建預設</th></tr></thead>';

    const tbody = document.createElement('tbody');
    for (const diff of preview.differences) {
        const tr = document.createElement('tr');

        const field = document.createElement('th');
        field.textContent = diff.field;

        const current = document.createElement('td');
        current.className = 'small';
        current.textContent = diff.current || '（空）';

        const seed = document.createElement('td');
        seed.className = 'small text-success';
        seed.textContent = diff.seed || '（空）';

        tr.append(field, current, seed);
        tbody.appendChild(tr);
    }
    table.appendChild(tbody);
    wrap.appendChild(table);
    body.appendChild(wrap);
}

document.getElementById('restore-confirm').addEventListener('click', async () => {
    await api.post(`/api/rules/${encodeURIComponent(restoringRuleId)}/restore`);
    toast(`已將 ${restoringRuleId} 回復為內建預設`, 'success');
    restoreModal.hide();
    await load();
});

// ── 抑制 ─────────────────────────────────────────────────────────────────────

function openSuppressModal(rule) {
    suppressingRuleId = rule.id;
    document.getElementById('suppress-host').value = '';
    document.getElementById('suppress-reason').value = '';
    document.getElementById('suppress-days').value = '';
    ensureHostOptions();
    suppressModal.show();
}

/** 首次開啟抑制 modal 時載入主機清單填入下拉（避免要人手打主機名打錯）。與 hosts 頁同一端點、同 Maintain 權限。 */
async function ensureHostOptions() {
    if (hostOptionsLoaded) return;
    const select = document.getElementById('suppress-host');
    try {
        const hosts = await api.get('/api/admin/hosts');
        for (const host of hosts) {
            const option = document.createElement('option');
            option.value = host.hostName;
            option.textContent = host.displayName ? `${host.hostName}（${host.displayName}）` : host.hostName;
            select.appendChild(option);
        }
        hostOptionsLoaded = true;
    } catch {
        // api.js 已以 toast 顯示錯誤；使用者可稍後重開再試
    }
}

document.getElementById('suppress-form').addEventListener('submit', async event => {
    event.preventDefault();

    const host = document.getElementById('suppress-host').value.trim();
    const reason = document.getElementById('suppress-reason').value.trim();
    if (!host || !reason) {
        toast('請填寫主機與原因', 'warning');
        return;
    }

    const days = document.getElementById('suppress-days').value;
    await api.post(`/api/rules/${encodeURIComponent(suppressingRuleId)}/suppressions`, {
        host,
        reason,
        days: days ? Number(days) : null
    });

    toast('已建立抑制設定', 'success');
    suppressModal.hide();
    await load();
});

function renderSuppressions() {
    renderTable(document.getElementById('suppression-list'), {
        columns: [
            { title: '規則', render: s => s.ruleId },
            { title: '主機', render: s => s.host },
            { title: '原因', render: s => s.reason },
            { title: '到期', render: s => expiryCell(s) },
            { title: '', className: 'text-end', render: s => removeSuppressionButton(s) }
        ],
        rows: suppressions,
        empty: {
            title: '目前沒有抑制設定',
            hint: '若某條規則在某台主機上已確認是已知雜訊，可於規則列表的「抑制」建立。'
        }
    });
}

function expiryCell(suppression) {
    const span = document.createElement('span');

    if (!suppression.expiresAt) {
        span.textContent = '永久（直到手動解除）';
        return span;
    }

    span.textContent = formatDate(suppression.expiresAt);
    if (suppression.isExpired) {
        // 到期後不自動清理、只是恢復告警——這裡標示出來讓人知道可以清掉了
        span.className = 'text-muted';
        span.textContent += '（已到期，告警已恢復）';
    }
    return span;
}

function removeSuppressionButton(suppression) {
    return button('解除', { variant: 'outline-danger', icon: 'trash', onClick: async () => {
        const confirmed = await confirmAction({
            title: '解除抑制',
            message: `解除後，規則「${suppression.ruleId}」在主機「${suppression.host}」上的告警將恢復。`,
            confirmText: '解除',
            confirmVariant: 'warning'
        });
        if (!confirmed) return;

        await api.delete(
            `/api/rules/${encodeURIComponent(suppression.ruleId)}/suppressions/${encodeURIComponent(suppression.host)}`);
        toast('已解除抑制', 'success');
        await load();
    } });
}

document.getElementById('btn-new-rule').addEventListener('click', () => openRuleModal(null));
document.getElementById('rule-search').addEventListener('input', renderRules);

// 詳情頁「誤報」提示連結帶 ?search= 過來（§5.1 D-1 #6）：直接定位到那條規則
const searchParam = new URLSearchParams(location.search).get('search');
if (searchParam) document.getElementById('rule-search').value = searchParam;

setupToolbar();
load();
