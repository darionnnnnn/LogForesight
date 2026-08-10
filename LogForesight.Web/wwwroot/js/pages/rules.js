/**
 * 規則維護（docs/WEB-SPEC.md §9.7）。
 *
 * 四層保護在 UI 上的體現：
 *   - builtin 不顯示刪除鈕（只能停用）
 *   - builtin 被改過時顯示「已修改」徽章與「回復預設」鈕
 *   - 儲存前先跑後端驗證，不合格不寫入
 */

import { api } from '../core/api.js';
import {
    renderTable, renderLoading, renderSpinner, toast, confirmAction, withBusy, button, bindTabs, renderChips,
    renderPagination, sortRows, loadPageSize, savePageSize
} from '../core/ui.js';
import { severityBadge, elevatesBadge, statusBadge, formatDate, severityName } from '../core/format.js';

const CATEGORY_NAMES = {
    Storage: '儲存裝置', Hardware: '硬體', Security: '安全', Service: '服務',
    Backup: '備份', Config: '設定', Resource: '資源', Other: '其他'
};

// docs/archive/HISTORY.md #1（B1 三級化）：Critical 收斂進 High
const SEVERITY_ORDER = ['High', 'Medium', 'Low'];

// chip 篩選狀態（§5.1 D-2）：狀態/來源/抑制/重大為單選（含「全部」＝空字串），嚴重度/類別為多選（空集合＝不限）
const chipFilters = {
    status: '',
    origin: '',
    suppression: '',
    elevates: '',
    severities: new Set(),
    categories: new Set()
};

// docs/LINUX-RULES.md §5.1：Windows規則／Linux規則兩個 tab 共用同一套清單/篩選/排序，
// 只差 Platform 過濾——currentPlatform 由 tab 上的 data-platform 決定，不是獨立的分頁狀態。
let currentPlatform = 'windows';
let suppressionPlatform = '';   // 告警抑制分頁的平台篩選；空字串＝全部

// 表頭點擊排序（取代原本的獨立排序下拉）＋本地分頁
let ruleSort = { key: 'id', dir: 'asc' };
let rulePage = 1;
let rulePageSize = loadPageSize('rules');
let suppressionSort = { key: 'ruleId', dir: 'asc' };
let suppressionPage = 1;
let suppressionPageSize = loadPageSize('suppressions');

const ruleModal = new bootstrap.Modal(document.getElementById('rule-modal'));
const restoreModal = new bootstrap.Modal(document.getElementById('restore-modal'));
const suppressModal = new bootstrap.Modal(document.getElementById('suppress-modal'));

let rules = [];
let suppressions = [];
let editingRule = null;
let restoringRuleId = null;
let suppressingRuleId = null;
let hostOptions = null;   // 抑制 modal 的主機下拉候選（延遲載入、依規則平台過濾），null = 尚未載入
let groupOptions = null;  // 抑制 modal 的主機群組下拉候選（範圍選 Group 時用），null = 尚未載入

const kbCollapse = new bootstrap.Collapse(document.getElementById('rule-kb'), { toggle: false });

const ruleTabsEl = document.getElementById('rule-tabs');
bindTabs(ruleTabsEl);
// Windows規則／Linux規則兩個 tab 共用 data-tab="rules"（同一個 data-panel），
// bindTabs 只負責切換面板顯示；平台切換靠這裡另外偵測 data-platform。
ruleTabsEl.addEventListener('click', event => {
    const btn = event.target.closest('[data-platform]');
    if (!btn) return;
    currentPlatform = btn.dataset.platform;
    updateSearchPlaceholder();
    rulePage = 1;
    renderRules();
});

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
        onToggle: value => { chipFilters.status = value; rulePage = 1; renderRules(); }
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
        onToggle: value => { chipFilters.origin = value; rulePage = 1; renderRules(); }
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
        onToggle: value => { chipFilters.suppression = value; rulePage = 1; renderRules(); }
    });

    renderChips(document.getElementById('rule-severity-chips'), {
        items: SEVERITY_ORDER.map(s => ({ value: s, label: severityName(s) })),
        attr: 'severity',
        activeValues: [...chipFilters.severities],
        multi: true,
        onToggle: (value, active) => {
            if (active) chipFilters.severities.add(value); else chipFilters.severities.delete(value);
            rulePage = 1;
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
            rulePage = 1;
            renderRules();
        }
    });

    // docs/archive/HISTORY.md #1：「重大」快篩——命中即列為高風險日的規則
    renderChips(document.getElementById('rule-elevates-chips'), {
        items: [
            { value: '', label: '全部' },
            { value: 'yes', label: '重大' },
            { value: 'no', label: '一般' }
        ],
        attr: 'elevates',
        activeValues: [chipFilters.elevates],
        multi: false,
        onToggle: value => { chipFilters.elevates = value; rulePage = 1; renderRules(); }
    });

    renderChips(document.getElementById('suppression-platform-chips'), {
        items: [
            { value: '', label: '全部' },
            { value: 'windows', label: 'Windows' },
            { value: 'linux', label: 'Linux' }
        ],
        attr: 'suppressionPlatform',
        activeValues: [suppressionPlatform],
        multi: false,
        onToggle: value => { suppressionPlatform = value; suppressionPage = 1; renderSuppressions(); }
    });
}

/** 搜尋框 placeholder 依平台調整（docs/LINUX-RULES.md §5.1）：Windows 找來源/Event ID，Linux 找 program/訊息 */
function updateSearchPlaceholder() {
    document.getElementById('rule-search').placeholder = currentPlatform === 'linux'
        ? '搜尋 program、訊息關鍵字、說明'
        : '搜尋來源、Event ID、說明';
}

const RULE_COLUMNS = [
    { title: '規則', sortKey: 'id', sortValue: r => r.id, render: r => ruleCell(r) },
    { title: '比對', render: r => matchCell(r) },
    {
        title: '類別', sortKey: 'category', sortValue: r => CATEGORY_NAMES[r.category] ?? r.category,
        render: r => CATEGORY_NAMES[r.category] ?? r.category
    },
    {
        title: '嚴重度', sortKey: 'severity', sortDefaultDir: 'asc',
        sortValue: r => SEVERITY_ORDER.indexOf(r.severity), render: r => severityCell(r)
    },
    {
        title: '門檻', className: 'text-end', sortKey: 'threshold', sortDefaultDir: 'desc',
        sortValue: r => r.countThreshold, render: r => String(r.countThreshold)
    },
    { title: '狀態', render: r => statusCell(r) },
    { title: '', className: 'text-end', render: r => actionsCell(r) }
];

function renderRules() {
    const keyword = document.getElementById('rule-search').value.trim().toLowerCase();

    let filtered = rules.filter(r => r.platform === currentPlatform);
    if (keyword) {
        filtered = filtered.filter(r =>
            r.id.toLowerCase().includes(keyword) ||
            r.description.toLowerCase().includes(keyword) ||
            (r.platform === 'linux'
                ? r.programPattern.toLowerCase().includes(keyword) ||
                  r.eventNamePattern.toLowerCase().includes(keyword) ||
                  r.messagePatterns.some(p => p.toLowerCase().includes(keyword))
                : r.sourcePattern.toLowerCase().includes(keyword) ||
                  r.eventIds.some(id => String(id).includes(keyword))));
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
    if (chipFilters.elevates === 'yes') filtered = filtered.filter(r => r.elevatesDayRisk);
    if (chipFilters.elevates === 'no') filtered = filtered.filter(r => !r.elevatesDayRisk);

    filtered = sortRows(filtered, RULE_COLUMNS, ruleSort);

    document.getElementById('rule-count').textContent = `共 ${filtered.length} 條`;

    const totalPages = Math.max(1, Math.ceil(filtered.length / rulePageSize));
    if (rulePage > totalPages) rulePage = totalPages;
    const pageRows = filtered.slice((rulePage - 1) * rulePageSize, rulePage * rulePageSize);

    renderTable(document.getElementById('rule-list'), {
        columns: RULE_COLUMNS,
        rows: pageRows,
        sort: ruleSort,
        onSort: (key, dir) => {
            ruleSort = { key, dir };
            rulePage = 1;
            renderRules();
        },
        empty: { title: '沒有符合條件的規則', hint: '請調整搜尋或篩選條件。' }
    });

    renderPagination(document.getElementById('rule-pager'), {
        page: rulePage,
        totalPages: filtered.length ? totalPages : 0,
        onPage: p => { rulePage = p; renderRules(); },
        pageSize: rulePageSize,
        onPageSize: size => {
            rulePageSize = size;
            savePageSize('rules', size);
            rulePage = 1;
            renderRules();
        }
    });
}

/** 嚴重度徽章＋「重大」旗標（docs/archive/HISTORY.md #1） */
function severityCell(rule) {
    const wrap = document.createElement('span');
    wrap.className = 'd-inline-flex align-items-center gap-1';
    wrap.appendChild(severityBadge(rule.severity));
    if (rule.elevatesDayRisk) wrap.appendChild(elevatesBadge());
    return wrap;
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

    if (rule.platform === 'linux') {
        if (rule.programPattern) {
            const program = document.createElement('div');
            program.className = 'font-monospace small';
            program.textContent = rule.programPattern;
            wrap.appendChild(program);
        }
        if (rule.eventNamePattern) {
            const eventName = document.createElement('div');
            eventName.className = 'small text-muted';
            eventName.textContent = `事件名：${rule.eventNamePattern}`;
            wrap.appendChild(eventName);
        }
        if (rule.messagePatterns.length > 0) {
            const messages = document.createElement('div');
            messages.className = 'small text-muted';
            messages.textContent = rule.messagePatterns.join(' / ');
            wrap.appendChild(messages);
        }
        return wrap;
    }

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
            title: `${suppressionTargetText(rule.suppression)}：${rule.suppression.reason}`
        }));
    }

    return wrap;
}

/** 抑制的生效範圍文字（回饋十三輪 F）：Host 顯示主機名、Group 顯示群組名、Site 就是全站——
 * 規則清單的徽章 tooltip 與「告警抑制」分頁的表格共用同一份判斷，避免兩處各寫一套走鐘。 */
function suppressionTargetText(s) {
    if (s.scope === 'Group') return `群組 ${s.hostGroupName ?? '（群組已刪除）'}`;
    if (s.scope === 'Site') return '全站';
    return `主機 ${s.host}`;
}

function actionsCell(rule) {
    const wrap = document.createElement('div');
    wrap.className = 'd-flex gap-1 justify-content-end flex-wrap';

    wrap.appendChild(button('', { variant: 'outline-primary', icon: 'pencil', title: '編輯', onClick: () => openRuleModal(rule) }));
    wrap.appendChild(button('', {
        variant: 'outline-secondary',
        icon: rule.enabled ? 'slash-circle' : 'plus-lg',
        // 停用只關掉分類與知識庫顯示，不影響趨勢層／關聯層偵測（同頁首提示與 modal 內的說明）——
        // 這裡也帶一份，逐列操作時不必先展開編輯 modal 才看得到
        title: rule.enabled ? '停用（不影響趨勢層／關聯層對同一事件的偵測）' : '啟用',
        onClick: () => toggleEnabled(rule)
    }));
    wrap.appendChild(button('', { variant: 'outline-dark', icon: 'bell-slash', title: '抑制', onClick: () => openSuppressModal(rule) }));

    // 回饋十三輪 A10：builtin 規則本身可編輯可回復（見頁首提示），但「改內容」與「照著寫一條
    // 新規則」是兩種不同意圖——後者過去只能手動把每個欄位抄一遍。以此為範本開新規則modal，
    // 帶入來源欄位值但 Id 清空待填（必須以 custom- 開頭，見 openRuleModal 的 asTemplate 分支）。
    if (rule.origin !== 'custom') {
        wrap.appendChild(button('', {
            variant: 'outline-secondary', icon: 'copy', title: '以此為範本建立自訂規則',
            onClick: () => openRuleModal(rule, { asTemplate: true })
        }));
    }

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

/**
 * @param {object|null} rule 編輯／以此為範本時的來源規則；新增規則傳 null
 * @param {{asTemplate?: boolean}} options asTemplate=true：欄位值取自 rule，但視為「新增」——
 *   Id 清空待填（不可沿用來源 Id，會撞重複）、editingRule 不設，儲存時走 POST 新增而非改寫來源規則。
 */
function openRuleModal(rule, { asTemplate = false } = {}) {
    editingRule = asTemplate ? null : rule;
    document.getElementById('rule-validation').replaceChildren();

    // 編輯既有規則／以其為範本皆沿用來源的平台；新增規則採目前所在分頁（Windows規則/Linux規則）
    // 的平台，平台一經建立不可變更（見 RuleAdminService.BuildRule）。
    const platform = rule?.platform ?? currentPlatform;
    applyPlatformBlocks(platform);

    const isNew = !editingRule;

    document.getElementById('rule-modal-title').textContent = asTemplate
        ? `以「${rule.id}」為範本建立自訂規則`
        : rule
            ? `編輯規則 ${rule.id}`
            : `新增${platform === 'linux' ? ' Linux' : ' Windows'}規則`;
    document.getElementById('rule-id').value = asTemplate ? suggestCustomId(rule.id) : (rule?.id ?? 'custom-');
    document.getElementById('rule-id').disabled = !isNew;   // Id 是穩定識別鍵，建立後不可改
    document.getElementById('rule-id-hint').textContent = isNew
        ? '新規則必須以 custom- 開頭。'
        : 'Id 一經建立即不可變更（seed 同步與抑制設定都靠它比對）。';

    document.getElementById('rule-source').value = rule?.sourcePattern ?? '';
    document.getElementById('rule-event-ids').value = rule?.eventIds.join(', ') ?? '';
    document.getElementById('rule-match-all').checked = rule?.matchAllEventIds ?? false;
    document.getElementById('rule-program').value = rule?.programPattern ?? '';
    document.getElementById('rule-event-name').value = rule?.eventNamePattern ?? '';
    document.getElementById('rule-message-patterns').value = rule?.messagePatterns.join('\n') ?? '';
    document.getElementById('rule-category').value = rule?.category ?? 'Other';
    document.getElementById('rule-severity').value = rule?.severity ?? 'Medium';
    document.getElementById('rule-elevates-day-risk').checked = rule?.elevatesDayRisk ?? false;
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

/** builtin Id 慣例是 builtin-xxx（見 KnownIssueSeed）——去掉前綴、換成 custom- 當預設建議值，
 * 使用者仍可自行改，只是不必從空白開始想名字。Id 欄位在範本模式下未鎖定，重複時走既有的
 * 「Id 重複」驗證錯誤，不需要在這裡另外查重。 */
function suggestCustomId(originalId) {
    const stripped = originalId.startsWith('builtin-') ? originalId.slice('builtin-'.length) : originalId;
    return `custom-${stripped}`;
}

/** 依平台顯示/隱藏比對欄位區塊（Windows：來源+Event ID；Linux：Program+事件名+訊息子字串） */
function applyPlatformBlocks(platform) {
    for (const el of document.querySelectorAll('[data-platform-block]')) {
        el.classList.toggle('d-none', el.dataset.platformBlock !== platform);
    }
}

function collectRule() {
    const platform = editingRule?.platform ?? currentPlatform;

    const eventIds = document.getElementById('rule-event-ids').value
        .split(',')
        .map(s => Number(s.trim()))
        .filter(n => Number.isInteger(n) && n > 0);

    return {
        id: document.getElementById('rule-id').value.trim(),
        enabled: document.getElementById('rule-enabled').checked,
        platform,
        sourcePattern: document.getElementById('rule-source').value.trim(),
        eventIds,
        matchAllEventIds: document.getElementById('rule-match-all').checked,
        programPattern: document.getElementById('rule-program').value.trim(),
        eventNamePattern: document.getElementById('rule-event-name').value.trim(),
        messagePatterns: splitLines(document.getElementById('rule-message-patterns').value),
        category: document.getElementById('rule-category').value,
        severity: document.getElementById('rule-severity').value,
        elevatesDayRisk: document.getElementById('rule-elevates-day-risk').checked,
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

async function openSuppressModal(rule) {
    suppressingRuleId = rule.id;
    document.getElementById('suppress-reason').value = '';
    document.getElementById('suppress-days').value = '';
    document.getElementById('suppress-scope').value = 'Host';
    await Promise.all([ensureHostOptions(), ensureGroupOptions()]);
    populateHostOptions(rule.platform);
    populateGroupOptions();
    updateSuppressScopeVisibility();
    suppressModal.show();
}

/** 首次開啟抑制 modal 時載入主機清單（避免要人手打主機名打錯）。與 hosts 頁同一端點、同 Maintain 權限。 */
async function ensureHostOptions() {
    if (hostOptions) return;
    try {
        // §5.4 D-4：/api/admin/hosts 改伺服器端分頁，這裡要看到「全部」主機才能挑選——
        // 拉單頁上限（200）；主機數更多的部署，抑制設定的主機選取之後再視需要改成 autocomplete
        const result = await api.get('/api/admin/hosts?pageSize=200');
        hostOptions = result.items;
    } catch {
        // api.js 已以 toast 顯示錯誤；使用者可稍後重開再試
        hostOptions = [];
    }
}

/** 依規則平台過濾主機下拉（docs/LINUX-RULES.md §5.1）：Linux 規則只列 Linux 主機，反之亦然 */
function populateHostOptions(platform) {
    const select = document.getElementById('suppress-host');
    select.replaceChildren();

    const placeholder = document.createElement('option');
    placeholder.value = '';
    placeholder.textContent = '請選擇主機…';
    select.appendChild(placeholder);

    for (const host of (hostOptions ?? []).filter(h => h.os === platform)) {
        const option = document.createElement('option');
        option.value = host.hostName;
        option.textContent = host.displayName ? `${host.hostName}（${host.displayName}）` : host.hostName;
        select.appendChild(option);
    }
}

/** 首次開啟抑制 modal 時載入主機群組清單（回饋十三輪 F，範圍選「主機群組」時用）。
 * 與群組管理頁同一端點、同 Maintain 權限，不分平台——一個群組本來就可能混合 Windows／Linux 主機。 */
async function ensureGroupOptions() {
    if (groupOptions) return;
    try {
        const result = await api.get('/api/admin/host-groups');
        groupOptions = result.filter(g => g.active);
    } catch {
        groupOptions = [];
    }
}

function populateGroupOptions() {
    const select = document.getElementById('suppress-group');
    select.replaceChildren();

    const placeholder = document.createElement('option');
    placeholder.value = '';
    placeholder.textContent = '請選擇主機群組…';
    select.appendChild(placeholder);

    for (const group of (groupOptions ?? [])) {
        const option = document.createElement('option');
        option.value = String(group.groupId);
        option.textContent = group.groupName;
        select.appendChild(option);
    }
}

/** 範圍下拉切換時，只顯示對應的目標欄位——三選一，其餘兩個連同其必填語意一起隱藏 */
function updateSuppressScopeVisibility() {
    const scope = document.getElementById('suppress-scope').value;
    document.getElementById('suppress-host-wrap').classList.toggle('d-none', scope !== 'Host');
    document.getElementById('suppress-group-wrap').classList.toggle('d-none', scope !== 'Group');
    document.getElementById('suppress-site-wrap').classList.toggle('d-none', scope !== 'Site');
}
document.getElementById('suppress-scope').addEventListener('change', updateSuppressScopeVisibility);

document.getElementById('suppress-form').addEventListener('submit', async event => {
    event.preventDefault();

    const scope = document.getElementById('suppress-scope').value;
    const host = document.getElementById('suppress-host').value.trim();
    const hostGroupId = document.getElementById('suppress-group').value;
    const reason = document.getElementById('suppress-reason').value.trim();

    if (!reason) {
        toast('請填寫原因', 'warning');
        return;
    }
    if (scope === 'Host' && !host) {
        toast('請選擇主機', 'warning');
        return;
    }
    if (scope === 'Group' && !hostGroupId) {
        toast('請選擇主機群組', 'warning');
        return;
    }

    const days = document.getElementById('suppress-days').value;
    await api.post(`/api/rules/${encodeURIComponent(suppressingRuleId)}/suppressions`, {
        scope,
        host: scope === 'Host' ? host : null,
        hostGroupId: scope === 'Group' ? Number(hostGroupId) : null,
        reason,
        days: days ? Number(days) : null
    });

    toast('已建立抑制設定', 'success');
    suppressModal.hide();
    await load();
});

const SUPPRESSION_COLUMNS = [
    { title: '規則', sortKey: 'ruleId', sortValue: s => s.ruleId, render: s => s.ruleId },
    { title: '平台', sortKey: 'platform', sortValue: s => s.platform, render: s => s.platform === 'linux' ? 'Linux' : 'Windows' },
    { title: '範圍', sortKey: 'scope', sortValue: s => s.scope, render: s => suppressionTargetText(s) },
    { title: '原因', render: s => s.reason },
    { title: '到期', sortKey: 'expiresAt', sortValue: s => s.expiresAt ? new Date(s.expiresAt).getTime() : Infinity, render: s => expiryCell(s) },
    { title: '', className: 'text-end', render: s => removeSuppressionButton(s) }
];

function renderSuppressions() {
    const filtered = sortRows(
        suppressionPlatform ? suppressions.filter(s => s.platform === suppressionPlatform) : suppressions,
        SUPPRESSION_COLUMNS, suppressionSort);

    const totalPages = Math.max(1, Math.ceil(filtered.length / suppressionPageSize));
    if (suppressionPage > totalPages) suppressionPage = totalPages;
    const pageRows = filtered.slice((suppressionPage - 1) * suppressionPageSize, suppressionPage * suppressionPageSize);

    renderTable(document.getElementById('suppression-list'), {
        columns: SUPPRESSION_COLUMNS,
        rows: pageRows,
        sort: suppressionSort,
        onSort: (key, dir) => {
            suppressionSort = { key, dir };
            suppressionPage = 1;
            renderSuppressions();
        },
        empty: {
            title: '目前沒有抑制設定',
            hint: '若某條規則在某台主機、某個主機群組、甚至全站已確認是已知雜訊，可於規則列表的「抑制」建立。'
        }
    });

    renderPagination(document.getElementById('suppression-pager'), {
        page: suppressionPage,
        totalPages: filtered.length ? totalPages : 0,
        onPage: p => { suppressionPage = p; renderSuppressions(); },
        pageSize: suppressionPageSize,
        onPageSize: size => {
            suppressionPageSize = size;
            savePageSize('suppressions', size);
            suppressionPage = 1;
            renderSuppressions();
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
            message: `解除後，規則「${suppression.ruleId}」於${suppressionTargetText(suppression)}的告警將恢復。`,
            confirmText: '解除',
            confirmVariant: 'warning'
        });
        if (!confirmed) return;

        // 範圍改用 query string（回饋十三輪 F）：Group/Site 沒有「host」可放進 path segment
        const params = new URLSearchParams({ scope: suppression.scope });
        if (suppression.scope === 'Host') params.set('host', suppression.host);
        if (suppression.scope === 'Group') params.set('hostGroupId', String(suppression.hostGroupId));

        await api.delete(`/api/rules/${encodeURIComponent(suppression.ruleId)}/suppressions?${params.toString()}`);
        toast('已解除抑制', 'success');
        await load();
    } });
}

document.getElementById('btn-new-rule').addEventListener('click', () => openRuleModal(null));
document.getElementById('rule-search').addEventListener('input', () => { rulePage = 1; renderRules(); });

// 詳情頁「誤報」提示連結帶 ?search= 過來（§5.1 D-1 #6）：直接定位到那條規則
const searchParam = new URLSearchParams(location.search).get('search');
if (searchParam) document.getElementById('rule-search').value = searchParam;

updateSearchPlaceholder();
setupToolbar();
load();

// ── 內建規則升級（docs/archive/WEB-SCHEDULER-PLAN.md §1.4.9，承接 --import-rules）──────

const ruleImportModal = new bootstrap.Modal(document.getElementById('rule-import-modal'));

async function checkRuleImportStatus() {
    const status = await api.get('/api/rules/import-status', { silent: true }).catch(() => null);
    if (!status) return;

    const banner = document.getElementById('rule-import-banner');
    if (status.hasUpdate) {
        document.getElementById('rule-import-banner-text').textContent =
            `內建規則有更新（v${status.currentSeedVersion} → v${status.latestSeedVersion}）`;
        banner.classList.remove('d-none');
    } else {
        banner.classList.add('d-none');
    }
}

document.getElementById('rule-import-preview-btn').addEventListener('click', () => {
    document.getElementById('rule-import-overwrite').checked = false;
    loadRuleImportPreview();
    ruleImportModal.show();
});

document.getElementById('rule-import-overwrite').addEventListener('change', loadRuleImportPreview);

async function loadRuleImportPreview() {
    const overwrite = document.getElementById('rule-import-overwrite').checked;
    const summaryEl = document.getElementById('rule-import-summary');
    const itemsEl = document.getElementById('rule-import-items');
    const applyButton = document.getElementById('rule-import-apply-btn');

    renderSpinner(summaryEl, '載入中…');
    itemsEl.replaceChildren();
    applyButton.disabled = true;

    const preview = await api.get(`/api/rules/import-preview?overwriteBuiltin=${overwrite}`, { silent: true }).catch(() => null);
    if (!preview) {
        summaryEl.textContent = '載入預覽失敗，請重新開啟這個對話框再試一次。';
        return;
    }

    summaryEl.textContent =
        `將新增 ${preview.added}、將更新 ${preview.updated}、略過 ${preview.skipped}、衝突 ${preview.conflicts}`;

    renderTable(itemsEl, {
        columns: [
            { title: 'Id', render: i => i.id },
            { title: '動作', render: i => importActionBadge(i.action, i.actionText) },
            { title: '說明', render: i => i.detail }
        ],
        rows: preview.items,
        empty: { title: '目前規則庫已與內建種子完全一致', hint: '沒有任何規則需要新增或更新。' }
    });

    applyButton.disabled = preview.added === 0 && preview.updated === 0;
}

function importActionBadge(action, text) {
    const variants = {
        added: 'success', updated: 'primary', skipped_unchanged: 'light',
        skipped_modified: 'warning', conflict: 'danger'
    };
    const span = document.createElement('span');
    span.className = `lf-badge lf-badge--${variants[action] ?? 'secondary'}`;
    span.textContent = text;
    return span;
}

document.getElementById('rule-import-apply-btn').addEventListener('click', async () => {
    const overwrite = document.getElementById('rule-import-overwrite').checked;
    const applyButton = document.getElementById('rule-import-apply-btn');
    const restore = withBusy(applyButton, '套用中');
    try {
        const result = await api.post('/api/rules/import-apply', { overwriteBuiltin: overwrite });
        toast(
            `已套用：新增 ${result.added}、更新 ${result.updated}` +
            (result.warnings.length > 0 ? `（另有 ${result.warnings.length} 項驗證警告，詳見主控台）` : ''),
            'success'
        );
        // 警告是非阻斷性資訊（遮蔽偵測／規則不合格被跳過），不塞進 toast 洗版，
        // 有需要深入排查的人打開瀏覽器主控台看——與原本 console 版逐行印出 ⚠ 同一份內容
        for (const warning of result.warnings) console.warn(warning);
        ruleImportModal.hide();
        await checkRuleImportStatus();
        await load();
    } catch {
        // 錯誤已由 api.js 顯示
    } finally {
        restore();
    }
});

checkRuleImportStatus();
