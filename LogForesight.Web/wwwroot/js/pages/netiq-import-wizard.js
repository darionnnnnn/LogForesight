/**
 * NetIQ 掃描匯入（docs/WEB-SPEC.md §9.9a「匯入」分頁）。
 *
 * §1（回饋第十一輪）自 `imports.js` 整段搬來並抽成獨立模組：Sentinel 的設定與掃描是
 * 同一件事的兩半，本來分在兩頁；搬過來後若直接併進 `netiq.js`（原本已 400 行）
 * 會變成一支千行檔案，所以精靈自成一個模組，由 `netiq.js` 呼叫 `initNetiqImportTab()` 掛載。
 * 行為與搬遷前逐字相同（掃描 → 網段勾選 → 群組指派 → 匯入），API 零改動。
 */

import { api } from '../core/api.js';
import { renderTable, renderLoading, renderSpinner, toast, withBusy, guardLoad } from '../core/ui.js';
import { formatDateTime, formatUserName } from '../core/format.js';

let scanPicker = null;
let discoverableSentinels = [];

/**
 * 由 netiq.js 的 Sentinel 清單載入完成後呼叫（新增／編輯／停用／刪除後也會再呼叫一次）。
 * **接收已取回的清單而不是自己再打一次 API**：設定與匯入現在同一頁，兩邊各查一次
 * `/api/admin/sentinels` 只是白費一趟往返，剛補完帳密的 Sentinel 也要立刻出現在下拉裡。
 */
export function refreshScanPicker(sentinels) {
    if (!scanPicker) return;

    discoverableSentinels = sentinels.filter(s => s.active && s.canDiscover);
    renderScanPicker(sentinels);
}

/** 匯入成功後請 netiq.js 重載 Sentinel 清單（主機數會變），它會回頭呼叫 refreshScanPicker */
let onSentinelsChanged = null;

export function initNetiqImportTab({ reloadSentinels } = {}) {
    scanPicker = document.getElementById('netiq-scan-picker');
    if (!scanPicker) return;

    onSentinelsChanged = reloadSentinels;
    renderLoading(scanPicker, 1);   // 等 netiq.js 的清單載入完會由 refreshScanPicker 取代
    bindWizardControls();
    guardLoad(document.getElementById('netiq-import-logs'), loadImportLogs);
}

function renderScanPicker(allSentinels) {
    scanPicker.replaceChildren();

    if (allSentinels.length === 0) {
        scanPicker.appendChild(pickerHint('尚無 Sentinel，請至「設定」分頁新增。'));
        return;
    }

    if (discoverableSentinels.length === 0) {
        scanPicker.appendChild(pickerHint('目前沒有帳密齊備且啟用中的 Sentinel，請至「設定」分頁補上探索帳密。'));
        return;
    }

    const row = document.createElement('div');
    row.className = 'd-flex align-items-center gap-2';

    const select = document.createElement('select');
    select.className = 'form-select';
    select.style.maxWidth = '240px';
    select.id = 'scan-sentinel-select';
    for (const sentinel of discoverableSentinels) {
        select.appendChild(new Option(sentinel.name, sentinel.name));
    }
    row.appendChild(select);

    // 掃描是「查一個網段」不是盲掃全站（docs/NETIQ-API-REFERENCE.md §3.4）——網段必填，
    // 前端先擋空值，格式細節（CIDR 位元數等）交給後端 SentinelQueryBuilder 統一驗證
    const subnetInput = document.createElement('input');
    subnetInput.type = 'text';
    subnetInput.className = 'form-control';
    subnetInput.style.maxWidth = '220px';
    subnetInput.id = 'scan-subnet-input';
    subnetInput.placeholder = '網段，例：192.168.0';
    row.appendChild(subnetInput);

    const scanButton = document.createElement('button');
    scanButton.type = 'button';
    scanButton.className = 'btn btn-primary';
    scanButton.textContent = '掃描匯入';
    scanButton.addEventListener('click', () => {
        const sentinel = discoverableSentinels.find(s => s.name === select.value);
        const subnetPrefix = subnetInput.value.trim();
        if (!subnetPrefix) {
            toast('請輸入要掃描的網段', 'warning');
            return;
        }
        if (sentinel) openWizard(sentinel, subnetPrefix);
    });
    row.appendChild(scanButton);

    scanPicker.appendChild(row);
}

function pickerHint(text) {
    const p = document.createElement('p');
    p.className = 'text-muted small mb-0';
    p.textContent = text;
    return p;
}

// ── 掃描匯入紀錄（本分頁只列 NetIQ 來源；完整紀錄仍在「資料匯入」頁） ───────────

async function loadImportLogs() {
    const container = document.getElementById('netiq-import-logs');
    renderLoading(container, 3);

    const logs = (await api.get('/api/imports/logs')).filter(l => l.kind === 'Netiq');

    renderTable(container, {
        columns: [
            { title: '時間', render: l => formatDateTime(l.createdAt) },
            { title: '來源', render: l => l.fileName },
            { title: '操作者', render: l => formatUserName(l.displayName, l.account) },
            {
                title: '結果',
                render: l => `新增 ${l.addedCount}、更新 ${l.updatedCount}` +
                             (l.revivedCount > 0 ? `、復活 ${l.revivedCount}` : '') +
                             (l.createdGroups?.length ? `（新建群組：${l.createdGroups.join('、')}）` : '')
            }
        ],
        rows: logs,
        empty: { title: '尚無掃描匯入紀錄', hint: '選一台 Sentinel、輸入網段掃描匯入後，這裡會留下每次的結果。' }
    });
}

// ── 掃描精靈（docs/archive/HISTORY.md 定案 7-8） ───────────────────────

let wizardModal = null;
let wizardTitle = null;
let wizardHint = null;
let wizardBackButton = null;
let wizardPrimaryButton = null;

let wizardPane = 'subnets';       // 'subnets' | 'groups'
let wizardScan = null;            // 最近一次掃描結果（NetiqScanResultDto）
let wizardServer = null;          // 目前掃描的 Sentinel 名稱

function bindWizardControls() {
    wizardModal = new bootstrap.Modal(document.getElementById('netiq-wizard-modal'));
    wizardTitle = document.getElementById('wizard-title');
    wizardHint = document.getElementById('wizard-hint');
    wizardBackButton = document.getElementById('wizard-back');
    wizardPrimaryButton = document.getElementById('wizard-primary');

    wizardBackButton.addEventListener('click', () => {
        if (wizardPane !== 'groups') return;
        wizardPane = 'subnets';
        renderWizardPane();
    });

    wizardPrimaryButton.addEventListener('click', () => {
        if (wizardPane === 'subnets') {
            wizardAdvanceToGroups();
        } else {
            wizardSubmitImport();
        }
    });

    // 「全選新主機」＝回到預設勾選狀態（新主機與可復活的勾、既有使用中的不勾），不是無條件全選——
    // 無條件全選會把既有主機的歸屬一併改掉，那是另一件事，不該藏在「全選」這個字眼底下。
    document.getElementById('wizard-select-new').addEventListener('click', () => {
        if (!wizardScan) return;

        // 先建 IP → 主機的索引再跑迴圈：掃描結果可達數百上千台，
        // 在迴圈內 flatMap+find 會是 O(n²) 且每輪重建一次完整陣列，大網段直接卡住畫面
        const hostByIp = new Map(wizardScan.subnets.flatMap(s => s.hosts).map(h => [h.ipAddress, h]));

        for (const box of document.querySelectorAll('#wizard-scan-result input.lf-wizard-host:not(:disabled)')) {
            const host = hostByIp.get(box.dataset.ip);
            box.checked = host ? (host.orphanOverlap || !host.exists) : false;
        }
        updateSubnetSelectionHint();
    });

    document.getElementById('wizard-select-none').addEventListener('click', () => {
        for (const box of document.querySelectorAll('#wizard-scan-result input.lf-wizard-host:not(:disabled)')) {
            box.checked = false;
        }
        updateSubnetSelectionHint();
    });
}

async function openWizard(sentinel, subnetPrefix) {
    wizardPane = 'subnets';
    wizardScan = null;
    wizardServer = sentinel.name;
    // 每次開精靈都依「這台 Sentinel」的 Os 重設，不沿用上一次開精靈時的選擇——
    // OS 是「這一批」的屬性，此環境 Windows／Linux 的 NetIQ 本來就拆成不同 Sentinel（各自單一 OS），
    // 沿用上一台的選擇會讓人不知不覺把 Linux 主機匯成 Windows（規則面整個錯配，畫面上還看不出來）。
    // 下拉仍保留可改，當作混合環境（單一 Sentinel 同時有兩種 OS）的逃生門。
    document.getElementById('wizard-os').value = sentinel.os === 'linux' ? 'linux' : 'windows';
    document.getElementById('wizard-tier').value = 'standard';

    renderWizardPane();
    wizardModal.show();

    document.getElementById('wizard-coverage-note').replaceChildren();
    document.getElementById('wizard-warnings').replaceChildren();
    renderSpinner(document.getElementById('wizard-scan-result'), '掃描中…');
    wizardPrimaryButton.disabled = true;
    try {
        wizardScan = await api.post('/api/admin/netiq/scan', { server: sentinel.name, subnetPrefix });
        renderCoverageNote();
        renderSubnetSelection();
    } catch {
        wizardModal.hide();
    } finally {
        wizardPrimaryButton.disabled = false;
    }
}

// 涵蓋範圍是顯示出來的事實，不是隱藏假設（docs/NETIQ-API-REFERENCE.md §3.4）——
// 網段範圍掃描只涵蓋窗口內有事件回報的主機，這句話必須在結果最上方，不能只藏在 tooltip 裡
function renderCoverageNote() {
    const noteEl = document.getElementById('wizard-coverage-note');
    noteEl.textContent = wizardScan.coverageNote || '';

    const warningsEl = document.getElementById('wizard-warnings');
    warningsEl.replaceChildren();
    if (wizardScan.warnings && wizardScan.warnings.length > 0) {
        const box = document.createElement('div');
        box.className = 'alert alert-warning small mb-0';
        const list = document.createElement('ul');
        list.className = 'mb-0 ps-3';
        for (const warning of wizardScan.warnings) {
            const item = document.createElement('li');
            item.textContent = warning;
            list.appendChild(item);
        }
        box.appendChild(list);
        warningsEl.appendChild(box);
    }
}

function wizardNote(text) {
    const p = document.createElement('p');
    p.className = 'text-muted small';
    p.textContent = text;
    return p;
}

function renderWizardPane() {
    document.getElementById('wizard-pane-subnets').classList.toggle('d-none', wizardPane !== 'subnets');
    document.getElementById('wizard-pane-groups').classList.toggle('d-none', wizardPane !== 'groups');

    wizardBackButton.classList.toggle('d-none', wizardPane !== 'groups');
    wizardHint.textContent = '';

    if (wizardPane === 'subnets') {
        wizardTitle.textContent = `從「${wizardServer}」掃描匯入`;
        wizardPrimaryButton.textContent = '下一步';
        updateSubnetSelectionHint();
    } else {
        wizardTitle.textContent = '指派網段所屬主機群組';
        wizardPrimaryButton.textContent = '完成匯入';
    }
}

function wizardAdvanceToGroups() {
    if (selectedWizardIps().length === 0) {
        toast('請至少勾選一台主機', 'warning');
        return;
    }
    wizardPane = 'groups';
    renderWizardPane();
    renderGroupAssignment();
}

async function wizardSubmitImport() {
    const selectedIps = selectedWizardIps();
    const groupAssignments = collectGroupAssignments();

    const restore = withBusy(wizardPrimaryButton, '匯入中');
    try {
        const result = await api.post('/api/admin/netiq/import', {
            token: wizardScan.token,
            selectedIps,
            groupAssignments,
            os: document.getElementById('wizard-os').value,
            tier: document.getElementById('wizard-tier').value
        });
        toast(`已匯入：新增 ${result.added}、更新 ${result.updated}` +
              (result.revived > 0 ? `、復活 ${result.revived}` : ''), 'success', 6000);
        wizardModal.hide();
        // 匯入會改變主機數，Sentinel 清單（含主機數欄）要跟著更新——由 netiq.js 重載，
        // 它會在完成後回頭呼叫 refreshScanPicker
        await onSentinelsChanged?.();
        await loadImportLogs();
    } catch {
        // 錯誤已由 api.js 顯示
    } finally {
        restore();
    }
}

// ── 精靈步驟 2：網段主機勾選（掃描結果） ─────────────────────────────────────

// 網段主機數超過這個門檻就預設收合——避免一次掃到的長清單把整個精靈撐到要一直捲動；
// summary 上已有的計數（已登錄／可復活）維持可判斷，收合不影響資訊完整性
const WIZARD_SUBNET_COLLAPSE_THRESHOLD = 20;

function renderSubnetSelection() {
    const container = document.getElementById('wizard-scan-result');
    container.replaceChildren();

    const total = document.createElement('div');
    total.className = 'small text-muted mb-2';
    total.textContent = `共掃描到 ${wizardScan.totalCount} 台，分佈於 ${wizardScan.subnets.length} 個網段`;
    container.appendChild(total);

    for (const subnet of wizardScan.subnets) {
        const details = document.createElement('details');
        details.className = 'mb-2 border rounded';
        details.open = subnet.hosts.length <= WIZARD_SUBNET_COLLAPSE_THRESHOLD;

        const summary = document.createElement('summary');
        summary.className = 'px-2 py-1 small';
        summary.style.cursor = 'pointer';

        const segBox = document.createElement('input');
        segBox.type = 'checkbox';
        segBox.className = 'form-check-input me-2';
        segBox.addEventListener('click', e => e.stopPropagation());
        segBox.addEventListener('change', () => {
            for (const box of details.querySelectorAll('input.lf-wizard-host:not(:disabled)')) box.checked = segBox.checked;
            updateSubnetSelectionHint();
        });
        summary.appendChild(segBox);

        const label = document.createElement('span');
        label.textContent = `${subnet.cidr}（${subnet.totalCount} 台` +
            (subnet.existingCount > 0 ? `，${subnet.existingCount} 台已登錄` : '') +
            (subnet.orphanOverlapCount > 0 ? `，${subnet.orphanOverlapCount} 台可復活` : '') + '）';
        summary.appendChild(label);
        details.appendChild(summary);

        const body = document.createElement('div');
        // 多欄 grid 取代原本一台一列的直排——網段常有數十台，直排要捲很久
        body.className = 'px-2 pb-2';
        body.style.display = 'grid';
        body.style.gridTemplateColumns = 'repeat(auto-fill, minmax(240px, 1fr))';
        body.style.columnGap = '0.75rem';
        for (const host of subnet.hosts) {
            body.appendChild(wizardHostRow(host));
        }
        details.appendChild(body);
        container.appendChild(details);
    }
    updateSubnetSelectionHint();
}

function wizardHostRow(host) {
    const row = document.createElement('div');
    row.className = 'd-flex align-items-center gap-1 py-1 small text-truncate';
    // title 掛在整列而非只掛名稱 span：名稱常被截斷但可視寬度只佔一小塊，
    // 掛在單一 span 上要滑鼠精準停在文字正上方才會出現，掛整列讓滑到 checkbox 旁邊
    // 空白處也看得到完整「IP＋主機名稱」（子元素如「可復活」徽章自己的 title 仍優先顯示，不受影響）。
    row.title = `${host.ipAddress}　${host.hostName}`;

    const box = document.createElement('input');
    box.type = 'checkbox';
    box.className = 'form-check-input lf-wizard-host flex-shrink-0';
    box.dataset.ip = host.ipAddress;
    // 新主機與可復活的預設勾選；使用中的既有主機預設不勾（再勾＝更新歸屬）
    box.checked = host.orphanOverlap || (!host.exists);
    box.addEventListener('change', updateSubnetSelectionHint);
    row.appendChild(box);

    const name = document.createElement('span');
    name.className = 'text-truncate';
    name.textContent = `${host.ipAddress}　${host.hostName}`;
    row.appendChild(name);

    if (host.exists) {
        const badge = document.createElement('span');
        badge.className = 'lf-badge lf-badge--secondary flex-shrink-0';
        badge.textContent = '已登錄';
        row.appendChild(badge);
    }
    if (host.orphanOverlap) {
        const badge = document.createElement('span');
        badge.className = 'lf-badge lf-badge--primary flex-shrink-0';
        badge.textContent = '可復活';
        badge.title = `原屬 ${host.orphanedFrom}，因移除而停用`;
        row.appendChild(badge);
    }
    return row;
}

function selectedWizardIps() {
    return Array.from(document.querySelectorAll('#wizard-scan-result input.lf-wizard-host:checked'))
        .map(box => box.dataset.ip);
}

function updateSubnetSelectionHint() {
    if (wizardPane !== 'subnets') return;
    const count = selectedWizardIps().length;
    wizardHint.textContent = count > 0 ? `已選 ${count} 台` : '';
}

// ── 精靈步驟 3：網段群組指派（只影響本次新增的主機，定案 8） ─────────────────

async function renderGroupAssignment() {
    const container = document.getElementById('wizard-group-assign');
    container.replaceChildren(wizardNote('載入群組清單中…'));

    const hostGroups = await api.get('/api/admin/host-groups');
    const selected = new Set(selectedWizardIps());
    container.replaceChildren();

    for (const subnet of wizardScan.subnets) {
        const selectedInSubnet = subnet.hosts.filter(h => selected.has(h.ipAddress)).length;
        if (selectedInSubnet === 0) continue;

        const row = document.createElement('div');
        row.className = 'row g-2 align-items-center mb-2';
        row.dataset.cidr = subnet.cidr;

        const labelCol = document.createElement('div');
        labelCol.className = 'col-4 small';
        labelCol.textContent = `${subnet.cidr}（${selectedInSubnet} 台）`;
        row.appendChild(labelCol);

        const selectCol = document.createElement('div');
        selectCol.className = 'col-4';
        const select = document.createElement('select');
        select.className = 'form-select form-select-sm lf-wizard-group-mode';
        select.appendChild(new Option('未分組（僅 admin 可見）', 'skip', true, true));
        for (const group of hostGroups) {
            select.appendChild(new Option(group.groupName, `existing:${group.groupId}`));
        }
        select.appendChild(new Option('＋ 建立新群組…', 'new'));
        selectCol.appendChild(select);
        row.appendChild(selectCol);

        const inputCol = document.createElement('div');
        inputCol.className = 'col-4';
        const newNameInput = document.createElement('input');
        newNameInput.type = 'text';
        newNameInput.className = 'form-control form-control-sm lf-wizard-group-new d-none';
        newNameInput.placeholder = '新群組名稱';
        inputCol.appendChild(newNameInput);
        row.appendChild(inputCol);

        select.addEventListener('change', () => {
            newNameInput.classList.toggle('d-none', select.value !== 'new');
        });

        container.appendChild(row);
    }

    if (container.childElementCount === 0) {
        container.appendChild(wizardNote('沒有需要指派的網段。'));
    }
}

function collectGroupAssignments() {
    const assignments = [];
    for (const row of document.querySelectorAll('#wizard-group-assign > .row[data-cidr]')) {
        const mode = row.querySelector('.lf-wizard-group-mode').value;
        const assignment = { cidr: row.dataset.cidr, mode: 'skip' };

        if (mode.startsWith('existing:')) {
            assignment.mode = 'existing';
            assignment.hostGroupId = Number(mode.split(':')[1]);
        } else if (mode === 'new') {
            const name = row.querySelector('.lf-wizard-group-new').value.trim();
            if (name) {
                assignment.mode = 'new';
                assignment.newGroupName = name;
            }
        }
        assignments.push(assignment);
    }
    return assignments;
}
