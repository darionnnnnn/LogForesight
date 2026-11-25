/**
 * 第 81 輪批次 F（F-2）：交辦總覽待派頁的三段（可以派／派不出去／不會派）、「授權給…」、單題派工與整頁派工的預覽確認、
 * 靜音中頁籤改用輕量計數、不再自動派工清單預設收合（work-orders.js）。
 * 以 extract 切出 production 函式本體，只替換外部相依（API、確認框、共用元件）。
 * 由 WorkOrdersGapsBehaviorTests 以 `node work-orders-gaps.behavior.mjs <案例名>` 逐案執行。
 */
import assert from 'node:assert/strict';
import { installDom } from './mini-dom.mjs';
import { loadButton, loadElements } from './load-elements.mjs';
import { read, extract, all } from './load-reply-modal.mjs';

const source = read('pages', 'work-orders.js');

/** 切出 `const NAME = {...};` 的整段（物件字面值） */
function extractConst(name) {
    const start = source.indexOf(`const ${name} = {`);
    assert(start >= 0, `找不到 production 常數：${name}`);
    let depth = 0;
    for (let i = source.indexOf('{', start); i < source.length; i++) {
        if (source[i] === '{') depth++;
        else if (source[i] === '}' && --depth === 0) return source.slice(start, i + 1) + ';';
    }
    throw new Error(`production 常數未閉合：${name}`);
}

const FUNCTIONS = [
    'function gapSectionText(', 'function renderUnassignableSummary(', 'function renderUnseenGroups(', 'function renderGrantControl(',
    'async function grantHostGroup(', 'function renderGapSectionBody(', 'function renderGapSectionRows(', 'function gapColumns(',
    'function dispatchPreviewText(', 'function dispatchResultNote(', 'async function runDispatch(',
    'async function loadDismissedCount(', 'function openDismissed(', 'function setupDismissedToggle(',
    'async function loadMutedCount(', 'function updateMutedTabTitle(', 'async function fetchMutedIssues(', 'function loadTab(',
    'function buildGapSection('
].map(d => extract(source, d)).join('\n');
const CONSTS = ['SECTION_META', 'UNASSIGNABLE_HINTS', 'UNASSIGNABLE_LABELS', 'EXCLUDED_LABELS'].map(extractConst).join('\n');

const NAMES = [
    'document', 'Node', 'button', 'disclosure', 'roleText', 'linkButton', 'appUrl', 'formatNumber', 'renderTable', 'renderEmpty', 'renderPagination',
    'renderLoading', 'renderIssueCell', 'confirmAction', 'api', 'toast', 'withBusy', 'loadGaps', 'loadGapSectionRows', 'createSeqGuard', 'runDispatchStub',
    'guardLoad', 'loadActive', 'loadLoadBoard', 'loadMuted', 'gapsContainer', 'gapsFromInput', 'gapsToInput', 'gapsAutoDispatchBtn', 'dismissedCountEl',
    'dismissedBody', 'dismissedToggleSlot', 'dismissedCard', 'dismissedContainer', 'dismissedPagerContainer', 'loadDismissed', 'mutedTabItem',
    'activeListContainer', 'loadContainer', 'mutedContainer', 'loadedTabs', 'currentUserRef', 'hasCapability', 'formatDate'
];

function env({ confirm = true, preview = null, result = null, canAssign = true } = {}) {
    const dom = installDom();
    const button = loadButton(dom);
    const { disclosure, roleText } = loadElements(dom);
    const el = tag => document.createElement(tag);
    const log = { confirms: [], posts: [], puts: [], gets: [], toasts: [], loads: 0, rowLoads: [], empties: [], tables: [], dispatches: [], guardLoads: [] };
    const linkButton = (text, href) => { const a = el('a'); a.href = href; a.textContent = text; return a; };
    const renderEmpty = (container, opts) => { log.empties.push(opts); const n = el('div'); n.textContent = opts.title; container.appendChild(n); };
    const renderTable = (container, { columns, rows }) => {
        const table = el('table');
        const trs = rows.map(row => {
            const tr = el('tr');
            for (const c of columns) { const td = el('td'); const out = c.render(row); td.appendChild(out instanceof Node ? out : document.createTextNode(String(out ?? ''))); tr.appendChild(td); }
            table.appendChild(tr);
            return tr;
        });
        container.appendChild(table);
        log.tables.push({ columns, rows, trs });
    };
    const api = {
        get: async url => { log.gets.push(url); return url.includes('/muted-count') ? { count: 3 } : url.includes('/dismissed/count') ? { issues: 2, hosts: 5 } : []; },
        put: async (url, body) => { log.puts.push({ url, body }); return {}; },
        post: async (url, body) => {
            log.posts.push({ url, body });
            return url.endsWith('/preview') ? preview : result;
        }
    };
    const inputs = { from: { value: '2026-11-18' }, to: { value: '2026-11-24' } };
    const gapsAutoDispatchBtn = el('button');
    const currentUser = { capabilities: canAssign ? ['Assign'] : [] };
    const dismissedCountEl = el('span');
    const dismissedBody = el('div');
    const dismissedToggleSlot = el('span');
    const dismissedCard = el('section');
    dismissedCard.scrollIntoView = () => { log.scrolled = true; };
    const mutedBtn = el('button'); mutedBtn.dataset = { tab: 'muted' };
    const mutedTabItem = { querySelector: () => mutedBtn };
    const loadedTabs = new Set();
    const guardLoad = async (container, fn) => { log.guardLoads.push(fn.name); return fn(); };
    const loadActive = async function loadActive() { };
    const loadLoadBoard = async function loadLoadBoard() { };
    const loadMuted = async function loadMuted() { };
    const loadDismissed = async function loadDismissed() { log.loads++; };
    const loadGaps = async () => { log.gapReloads = (log.gapReloads ?? 0) + 1; };
    const loadGapSectionRows = state => { log.rowLoads.push(state.name); };
    const createSeqGuard = () => { let n = 0; return { next: () => ++n, isCurrent: v => v === n }; };
    const hasCapability = (user, cap) => user.capabilities.includes(cap);
    const args = {
        document, Node, button, disclosure, roleText, linkButton, appUrl: p => '/lf' + p, formatNumber: n => String(n), renderTable, renderEmpty,
        renderPagination: () => { }, renderLoading: () => { }, renderIssueCell: label => { const d = el('div'); d.textContent = label; return d; },
        confirmAction: async opts => { log.confirms.push(opts); return confirm; }, api, toast: (message, kind) => log.toasts.push({ message, kind }),
        withBusy: () => () => { }, loadGaps, loadGapSectionRows, createSeqGuard, runDispatchStub: null, guardLoad, loadActive, loadLoadBoard, loadMuted,
        gapsContainer: el('div'), gapsFromInput: inputs.from, gapsToInput: inputs.to, gapsAutoDispatchBtn, dismissedCountEl, dismissedBody, dismissedToggleSlot,
        dismissedCard, dismissedContainer: el('div'), dismissedPagerContainer: el('div'), loadDismissed, mutedTabItem, activeListContainer: el('div'),
        loadContainer: el('div'), mutedContainer: el('div'), loadedTabs, currentUserRef: currentUser, hasCapability, formatDate: v => String(v).slice(0, 10)
    };
    const factory = new Function(...NAMES, `
        const currentUser = currentUserRef;
        const gapSections = {};
        let gapsCanAssign = false;
        let gapsMeta = null;
        let dismissedOpen = false, dismissedLoaded = false, dismissedToggleBtn = null, dismissedPage = 1;
        const dispatchLog = [];
        const GAP_PAGE_SIZE = 50;
        ${CONSTS}
        ${FUNCTIONS.replace('async function runDispatch(', 'async function runDispatch(')}
        return {
            renderUnassignableSummary, renderUnseenGroups, renderGapSectionBody, gapColumns, dispatchPreviewText, dispatchResultNote, runDispatch,
            loadDismissedCount, setupDismissedToggle, openDismissed, loadMutedCount, fetchMutedIssues, loadTab, buildGapSection, grantHostGroup,
            setCanAssign: v => { gapsCanAssign = v; }, getDismissedOpen: () => dismissedOpen, getDismissedLoaded: () => dismissedLoaded,
            sections: gapSections
        };`);
    const fns = factory(...NAMES.map(n => args[n]));
    fns.setCanAssign(canAssign);
    return { ...fns, args, log, inputs, loadedTabs };
}

const flat = node => all(node);
const text = node => node.textContent;
const buttonsOf = (node, label) => flat(node).filter(n => n.tagName === 'BUTTON' && (label === undefined || n.textContent === label));
const click = async node => Promise.all((node.listeners.click || []).map(h => h({ preventDefault() { }, stopPropagation() { } })));
const change = node => (node.listeners.change || []).forEach(h => h());

const gapsData = (extra = {}) => ({
    rows: [], total: 0, sectionTotal: 0, page: 1,
    dispatchable: { issues: 0, hosts: 0 }, unassignableSection: { issues: 0, hosts: 0 }, notDispatched: { issues: 0, hosts: 0 },
    unassignableReasons: [], unseenHostGroups: [], poolGroups: [], canGrantAccess: true, ...extra
});
const state = (name, body) => ({ name, body, page: 1, rowsHost: null, loaded: false, guard: { next: () => 1, isCurrent: () => true } });

const NO_VIS = gapsData({
    unassignableReasons: [{ reason: 'no_visibility', issues: 3, hosts: 7 }, { reason: 'all_paused', issues: 1, hosts: 2 }],
    unseenHostGroups: [{ groupId: 5, groupName: '機房A', hosts: 4, issues: 3 }, { groupId: 6, groupName: '機房B', hosts: 3, issues: 2 }],
    poolGroups: [{ userGroupId: 11, groupName: '值班池' }, { userGroupId: 12, groupName: '備援池' }]
});

const cases = {
    // A4：同一個無人授權的主機群組只在「派不出去」段出現一次（不隨問題重複），三個問題牽涉到它也一樣
    unseenGroupAppearsOnceInUnassignableSection() {
        const e = env();
        const body = document.createElement('div');
        e.renderUnassignableSummary(state('unassignable', body), NO_VIS);
        const rows = flat(body).filter(n => n.dataset?.hostGroupId === '5');
        assert.equal(rows.length, 1);
        assert.ok(text(rows[0]).includes('機房A') && text(rows[0]).includes('4 台') && text(rows[0]).includes('3 個問題'));
        // 每個原因一組說明：no_visibility 的說明只出現一次
        assert.equal(flat(body).filter(n => (n.textContent || '').includes('派工池成員都看不到這些主機。')).length >= 1, true);
        assert.equal(flat(body).filter(n => n.dataset?.hostGroupId).length, 2, '兩個群組各一列');
    },

    // F12-2：有 Maintain＝每個群組旁有「授權給…」選單（列出派工池群組）與停用中的「授權」鈕，選了才啟用
    grantControlListsPoolGroupsAndEnablesOnSelect() {
        const e = env();
        const body = document.createElement('div');
        e.renderUnassignableSummary(state('unassignable', body), NO_VIS);
        const row = flat(body).find(n => n.dataset?.hostGroupId === '5');
        const select = flat(row).find(n => n.tagName === 'SELECT');
        assert.ok(select, '有授權選單');
        assert.deepEqual(select.children.map(o => o.textContent), ['授權給…', '值班池', '備援池']);
        const grant = buttonsOf(row, '授權')[0];
        assert.equal(grant.disabled, true);
        select.value = '11';
        change(select);
        assert.equal(grant.disabled, false);
    },

    // F12-2：授權＝確認框寫出群組與台數 → PUT 授權 grant 該群組 → 整頁重算（該組消失、可派增加由重算決定）
    async grantConfirmsPutsAndReloadsGaps() {
        const e = env();
        const body = document.createElement('div');
        e.renderUnassignableSummary(state('unassignable', body), NO_VIS);
        const row = flat(body).find(n => n.dataset?.hostGroupId === '5');
        const select = flat(row).find(n => n.tagName === 'SELECT');
        select.value = '12'; change(select);
        await click(buttonsOf(row, '授權')[0]);
        assert.equal(e.log.confirms.length, 1);
        assert.ok(e.log.confirms[0].message.includes('機房A') && e.log.confirms[0].message.includes('備援池') && e.log.confirms[0].message.includes('4 台'));
        assert.equal(e.log.confirms[0].confirmVariant, 'primary');
        assert.deepEqual(e.log.puts, [{ url: '/api/admin/access/12', body: { grant: [5], revoke: [] } }]);
        assert.equal(e.log.gapReloads, 1, '授權後整頁重算');
        assert.equal(e.log.toasts[0].kind, 'success');
    },

    // F12-2：按「取消」不授權也不重算
    async grantCancelDoesNothing() {
        const e = env({ confirm: false });
        const body = document.createElement('div');
        e.renderUnassignableSummary(state('unassignable', body), NO_VIS);
        const row = flat(body).find(n => n.dataset?.hostGroupId === '6');
        const select = flat(row).find(n => n.tagName === 'SELECT');
        select.value = '11'; change(select);
        await click(buttonsOf(row, '授權')[0]);
        assert.equal(e.log.puts.length, 0);
        assert.equal(e.log.gapReloads ?? 0, 0);
    },

    // F12-2：沒有 Maintain＝只有連到「群組與授權」的連結，沒有選單與授權鈕
    noMaintainShowsLinkOnly() {
        const e = env();
        const body = document.createElement('div');
        e.renderUnassignableSummary(state('unassignable', body), { ...NO_VIS, canGrantAccess: false });
        const row = flat(body).find(n => n.dataset?.hostGroupId === '5');
        assert.equal(flat(row).filter(n => n.tagName === 'SELECT').length, 0);
        assert.equal(buttonsOf(row, '授權').length, 0);
        const link = flat(row).find(n => n.tagName === 'A');
        assert.equal(link.href, '/lf/admin/groups');
        assert.ok(text(link).includes('群組與授權'));
    },

    // F12-2 邊界：沒有任何派工池群組時，有 Maintain 也只給連結與說明
    noPoolGroupsShowsExplanationAndLink() {
        const e = env();
        const body = document.createElement('div');
        e.renderUnassignableSummary(state('unassignable', body), { ...NO_VIS, poolGroups: [] });
        const row = flat(body).find(n => n.dataset?.hostGroupId === '5');
        assert.equal(flat(row).filter(n => n.tagName === 'SELECT').length, 0);
        assert.ok(text(row).includes('沒有可授權的派工池群組'));
        assert.ok(flat(row).some(n => n.tagName === 'A' && n.href === '/lf/admin/groups'));
    },

    // A3：0 個可派時第一段顯示空狀態文字（有資料時不顯示）
    emptyDispatchableShowsEmptyText() {
        const e = env();
        const body = document.createElement('div');
        e.renderGapSectionBody(state('dispatchable', body), gapsData());
        assert.equal(e.log.empties.length, 1);
        assert.equal(e.log.empties[0].title, '目前沒有可以派的問題');
        assert.equal(e.log.tables.length, 0);
    },

    // A3 邊界：有可派問題＝表格列數等於問題數、不顯示空狀態
    nonEmptyDispatchableRendersTableWithoutEmptyText() {
        const e = env();
        const body = document.createElement('div');
        const rows = [
            { source: 'disk', eventId: 153, issueLabel: 'disk 153', suggested: [{ name: '王小明', hosts: 3, willCreate: true }] },
            { source: 'ntfs', eventId: 55, issueLabel: 'ntfs 55', suggested: [{ name: '李小華', hosts: 2, willCreate: false }] }
        ];
        e.renderGapSectionBody(state('dispatchable', body), gapsData({ rows, sectionTotal: 2, total: 2 }));
        assert.equal(e.log.empties.length, 0);
        assert.equal(e.log.tables[0].trs.length, 2);
        // 可派台數＝建議處理人台數加總（不是缺口台數）
        assert.equal(e.log.tables[0].trs[0].children[1].textContent, '3');
    },

    // A5：可派段每列有「派這個問題」（具 Assign）；按下帶該列的來源與事件編號進單題派工；沒有 Assign 時整欄不出現
    async perRowButtonOnlyWithAssign() {
        const withAssign = env();
        const cols = withAssign.gapColumns('dispatchable');
        assert.equal(cols.length, 4);
        const row = { source: 'disk', eventId: 153, issueLabel: 'disk 153', suggested: [] };
        const btn = cols[3].render(row);
        assert.equal(btn.textContent, '派這個問題');
        assert.ok(btn.className.includes('btn-outline-primary'));

        const noAssign = env({ canAssign: false });
        assert.equal(noAssign.gapColumns('dispatchable').length, 3);
        assert.equal(noAssign.gapColumns('unassignable').length, 2);
        assert.equal(noAssign.gapColumns('notDispatched').length, 2);
    },

    // F12-3：單題派工＝先預覽（帶來源與事件編號）→ 確認框列出「將建立／併入 N 張單、每人 M 台」→ 確認後同一個請求本體落盤
    async singleIssueDispatchPreviewsThenConfirmsThenPosts() {
        const preview = { createOrders: 2, mergeOrders: 1, hosts: 12, perHandler: [{ handlerId: 1, name: '王小明', hosts: 5 }, { handlerId: 2, name: '李小華', hosts: 7 }], unassigned: [], nothingToDispatch: false };
        const result = { createdOrders: 2, mergedOrders: 1, assignedMembers: 12, failedGroups: [], nothingToDispatch: false };
        const e = env({ preview, result });
        await e.runDispatch({ source: 'disk', eventId: 153, label: 'disk 153' });
        assert.deepEqual(e.log.posts.map(p => p.url), ['/api/work-orders/auto-dispatch/preview', '/api/work-orders/auto-dispatch']);
        const body = { from: '2026-11-18', to: '2026-11-24', source: 'disk', eventId: 153 };
        assert.deepEqual(e.log.posts[0].body, body);
        assert.deepEqual(e.log.posts[1].body, body, '預覽與落盤同一個請求本體');
        const message = e.log.confirms[0].message;
        assert.ok(message.includes('將建立 2 張單、併入 1 張單，共 12 台'), message);
        assert.ok(message.includes('王小明　5 台') && message.includes('李小華　7 台'), message);
        assert.ok(message.includes('disk 153'));
        assert.equal(e.log.confirms[0].confirmVariant, 'primary');
        assert.equal(e.log.toasts[0].message, '已建立 2 張、併入 1 張，共 12 台');
        assert.equal(e.log.gapReloads, 1);
    },

    // 整頁立即派工：請求本體不帶問題；確認框同樣列摘要
    async wholePageDispatchOmitsIssueAndShowsSummary() {
        const preview = { createOrders: 5, mergeOrders: 0, hosts: 9, perHandler: [{ handlerId: 1, name: '王小明', hosts: 9 }], unassigned: [{ reason: 'no_visibility', hosts: 4 }], nothingToDispatch: false };
        const e = env({ preview, result: { createdOrders: 5, mergedOrders: 0, assignedMembers: 9, failedGroups: [] } });
        await e.runDispatch(null);
        assert.deepEqual(e.log.posts[0].body, { from: '2026-11-18', to: '2026-11-24' });
        const message = e.log.confirms[0].message;
        assert.ok(message.includes('將建立 5 張單、併入 0 張單，共 9 台'));
        assert.ok(message.includes('另有派不出去的：派工池成員都看不到這些主機 4 台'), message);
        assert.equal(e.log.confirms[0].title, '立即派工');
    },

    // CU-6：預覽就沒有可派項目＝不開確認框、不落盤，直接提示訊息並重算
    async previewNothingToDispatchShowsMessageWithoutConfirm() {
        const preview = { createOrders: 0, mergeOrders: 0, hosts: 0, perHandler: [], unassigned: [], nothingToDispatch: true, message: '沒有需要派的項目（可能剛由其他人派出）' };
        const e = env({ preview });
        await e.runDispatch(null);
        assert.equal(e.log.confirms.length, 0);
        assert.deepEqual(e.log.posts.map(p => p.url), ['/api/work-orders/auto-dispatch/preview']);
        assert.deepEqual(e.log.toasts, [{ message: '沒有需要派的項目（可能剛由其他人派出）', kind: 'info' }]);
        assert.equal(e.log.gapReloads, 1);
    },

    // CU-6：預覽有、落盤時被別人先派完＝結果零成員，訊息是同一句、不是「已建立 0 張」
    async resultNothingToDispatchShowsMessageNotZeroCounts() {
        const preview = { createOrders: 1, mergeOrders: 0, hosts: 3, perHandler: [{ handlerId: 1, name: '王小明', hosts: 3 }], unassigned: [], nothingToDispatch: false };
        const result = { createdOrders: 0, mergedOrders: 0, assignedMembers: 0, failedGroups: [], nothingToDispatch: true, message: '沒有需要派的項目（可能剛由其他人派出）' };
        const e = env({ preview, result });
        await e.runDispatch(null);
        assert.equal(e.log.toasts.length, 1);
        assert.equal(e.log.toasts[0].message, '沒有需要派的項目（可能剛由其他人派出）');
        assert.ok(!e.log.toasts[0].message.includes('已建立'));
        assert.equal(e.log.toasts[0].kind, 'info');
    },

    // 取消確認框：不落盤、不重算
    async cancelledConfirmDoesNotDispatch() {
        const preview = { createOrders: 1, mergeOrders: 0, hosts: 3, perHandler: [], unassigned: [], nothingToDispatch: false };
        const e = env({ preview, confirm: false });
        await e.runDispatch({ source: 'disk', eventId: 153, label: 'disk 153' });
        assert.deepEqual(e.log.posts.map(p => p.url), ['/api/work-orders/auto-dispatch/preview']);
        assert.equal(e.log.toasts.length, 0);
        assert.equal(e.log.gapReloads ?? 0, 0);
    },

    // 部分失敗：warning 並寫出失敗組數
    resultNoteReportsFailedGroups() {
        const e = env();
        const note = e.dispatchResultNote({ createdOrders: 1, mergedOrders: 0, assignedMembers: 4, daySyncPendingCases: 2, failedGroups: [{}, {}], nothingToDispatch: false });
        assert.equal(note.kind, 'warning');
        assert.equal(note.message, '已建立 1 張、併入 0 張，共 4 台，逐日同步在背景完成，2 組失敗');
    },

    // CU-9：頁籤數字只走輕量計數端點（不讀整份問題負責清單）
    async mutedCountUsesLightEndpointOnly() {
        const e = env();
        await e.loadMutedCount();
        assert.deepEqual(e.log.gets, ['/api/admin/issue-owners/muted-count']);
        assert.equal(e.args.mutedTabItem.querySelector().textContent, '靜音中（3）');
    },

    // CU-9：停在「進行中」頁籤（載入頁籤＝只載入進行中清單）時不送問題負責清單的請求；點到「靜音中」才載入
    async activeTabNeverRequestsIssueOwnerList() {
        const e = env();
        await e.loadTab('active');
        assert.deepEqual(e.log.guardLoads, ['loadActive']);
        assert.equal(e.log.gets.some(u => u === '/api/admin/issue-owners'), false);
        await e.loadTab('muted');
        assert.deepEqual(e.log.guardLoads, ['loadActive', 'loadMuted']);
    },

    // CU-9：init 本身不再呼叫整份清單（fetchMutedIssues 只由「靜音中」頁籤的載入使用）
    initDoesNotFetchMutedIssueList() {
        const init = extract(source, 'async function init(');
        assert.ok(!init.includes('fetchMutedIssues('), 'init 不得載入整份靜音清單');
        assert.ok(init.includes('loadMutedCount('), 'init 用輕量計數');
    },

    // CU-2（畫面）：不再自動派工預設收合、展開才載入，標頭只有輕量計數
    async dismissedIsCollapsedAndLoadsOnFirstExpand() {
        const e = env();
        e.setupDismissedToggle();
        const toggle = e.args.dismissedToggleSlot.children[0];
        assert.equal(toggle.getAttribute('aria-expanded'), 'false');
        assert.ok(e.args.dismissedBody.classList.contains('d-none'), '預設收合');
        await e.loadDismissedCount();
        assert.equal(e.args.dismissedCountEl.textContent, '共 2 個問題、5 台');
        assert.equal(e.log.loads, 0, '收合時不載入清單');
        await click(toggle);
        assert.equal(e.log.loads, 1, '展開才載入');
        assert.equal(e.getDismissedOpen(), true);
    },

    // 待派的「不再打擾」台數連結：展開清單並捲到該區
    openDismissedExpandsAndScrolls() {
        const e = env();
        e.setupDismissedToggle();
        const toggle = e.args.dismissedToggleSlot.children[0];
        toggle.click = () => (toggle.listeners.click || []).forEach(h => h({ stopPropagation() { } }));   // 替身沒有 click()
        e.openDismissed();
        assert.equal(e.getDismissedOpen(), true);
        assert.equal(e.log.scrolled, true);
    },

    // 不會派段：預設收合；各段的標題列與統計文字
    sectionStatsText() {
        const e = env();
        const { section } = e.buildGapSection('notDispatched', gapsData({ notDispatched: { issues: 12, hosts: 329 } }));
        assert.ok(text(section).includes('不會派'));
        assert.ok(text(section).includes('12 個問題、329 台'));
        const body = flat(section).find(n => n.id === 'wo-gaps-section-notDispatched');
        assert.ok(body.classList.contains('d-none'), '不會派預設收合');
        const open = e.buildGapSection('dispatchable', gapsData({ dispatchable: { issues: 0, hosts: 0 } }));
        assert.ok(text(open.section).includes('0 個問題'));
        assert.ok(!flat(open.section).find(n => n.id === 'wo-gaps-section-dispatchable').classList.contains('d-none'), '可以派預設展開');
    }
};

const name = process.argv[2];
if (!name) {
    console.log(Object.keys(cases).join('\n'));
    process.exit(0);
}
assert(cases[name], `未知案例：${name}`);
await cases[name]();
console.log(`PASS ${name}`);
