/**
 * 第 81 輪批次 G：交辦總覽的處理人篩選與批次改派（work-orders.js、work-orders-bulk.js），
 * 以及停用／暫停接單前的確認（users.js）。以切函式＋替身執行 production 函式，只替換外部相依。
 * 由 WorkOrdersBulkReassignBehaviorTests 以 `node work-orders-bulk.behavior.mjs <案例名>` 逐案執行。
 */
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { installDom } from './mini-dom.mjs';
import { loadButton, loadElements } from './load-elements.mjs';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const jsRoot = path.resolve(scriptDir, '..', '..', 'LogForesight.Web', 'wwwroot', 'js', 'pages');
const read = file => fs.readFileSync(path.join(jsRoot, file), 'utf8').replaceAll('\r\n', '\n').replace(/^﻿+/, '');

function extractFunction(source, declaration) {
    const start = source.indexOf(declaration);
    assert(start >= 0, `找不到 production 函式：${declaration}`);
    const open = source.indexOf('{', source.indexOf(')', start));
    let depth = 0;
    for (let i = open; i < source.length; i++) {
        if (source[i] === '{') depth++;
        else if (source[i] === '}' && --depth === 0) return source.slice(start, i + 1);
    }
    throw new Error(`production 函式未閉合：${declaration}`);
}

const listSource = read('work-orders.js');
const bulkSource = read('work-orders-bulk.js');
const usersSource = read('users.js');

/** 簡易下拉替身：value 與 options，appendChild 加選項 */
function fakeSelect(value = '') {
    return { value, options: [], appendChild(opt) { this.options.push(opt); return opt; } };
}

/** 頁面層環境（work-orders.js 的函式用到的外部相依與狀態） */
function listEnv({ canAssign = true, selected = [] } = {}) {
    const dom = installDom();
    const button = loadButton(dom);
    const { roleText } = loadElements(dom);
    const log = { showTab: [], loads: 0, closed: 0, cleared: 0, panelOpened: 0 };
    const activeHandlerSelect = fakeSelect();
    const activeStatusSelect = fakeSelect('all');
    const selectedMap = new Map(selected.map(item => [item.workOrderId, item]));
    const selection = { selected: selectedMap, clear() { selectedMap.clear(); log.cleared++; }, renderBar() { } };
    const bulkPanel = { close() { log.closed++; }, open() { log.panelOpened++; } };
    const bulkHintEl = { textContent: '' };
    const bulkBarActions = document.createElement('span');
    const loadedTabs = new Set();
    const appUrl = p => `/lf${p}`;
    const body = [
        extractFunction(listSource, 'function currentHandlerValue('),
        extractFunction(listSource, 'function setHandlerFilterValue('),
        extractFunction(listSource, 'function applyHandlerFilter('),
        extractFunction(listSource, 'function consumeAutoSelect('),
        extractFunction(listSource, 'function handlerFilterLink('),
        extractFunction(listSource, 'function loadBoardActions('),
        extractFunction(listSource, 'function updateBulkBar(')
    ].join('\n');
    const api = new Function('activeHandlerSelect', 'activeStatusSelect', 'selection', 'bulkPanel', 'bulkHintEl', 'bulkBarActions',
        'loadedTabs', 'showTab', 'guardLoad', 'activeListContainer', 'loadActive', 'appUrl', 'button', 'roleText', 'document', 'BULK_REASSIGN_MAX',
        `let activePage = 4; let pendingAutoSelect = false; let canAssign = ${canAssign};\n${body}\n` +
        'return { applyHandlerFilter, consumeAutoSelect, handlerFilterLink, loadBoardActions, updateBulkBar, setHandlerFilterValue,' +
        ' get activePage() { return activePage; }, get pendingAutoSelect() { return pendingAutoSelect; } };')(
        activeHandlerSelect, activeStatusSelect, selection, bulkPanel, bulkHintEl, bulkBarActions,
        loadedTabs, (id, name) => log.showTab.push([id, name]), () => { log.loads++; }, {}, () => { }, appUrl, button, roleText, document, 100);
    // 不能用展開：activePage 等是 getter，展開會在這裡凍結成當下的值
    return Object.assign(api, { log, activeHandlerSelect, activeStatusSelect, selection, selectedMap, bulkHintEl, bulkBarActions, loadedTabs });
}

const clickWith = (node) => node.listeners.click[0]({ preventDefault() { node.prevented = true; } });
const items = n => Array.from({ length: n }, (_, i) => ({ workOrderId: i + 1, closedAt: null }));

const cases = {
    // F14-1：負載看板點人名＝處理人下拉帶入該人、回第 1 頁、清勾選、切到進行中並重查，且不跟連結跳頁
    loadBoardNameAppliesHandlerFilter() {
        const e = listEnv({ selected: items(2) });
        const link = e.handlerFilterLink({ userId: 7, name: '王小明' });
        assert.equal(link.textContent, '王小明');
        assert.equal(link.getAttribute('href') ?? link.href, '/lf/work-orders?handlerId=7');
        clickWith(link);
        assert.equal(link.prevented, true, '點人名不可跟著連結跳頁');
        assert.equal(e.activeHandlerSelect.value, '7');
        assert.equal(e.activeStatusSelect.value, 'active', '看的是進行中的單');
        assert.equal(e.activePage, 1);
        assert.equal(e.selectedMap.size, 0, '換篩選要清勾選');
        assert.deepEqual(e.log.showTab, [['wo-tabs', 'active']]);
        assert.equal(e.log.loads, 1);
        assert.equal(e.log.closed, 1, '舊的批次面板要收起');
    },

    // 下拉裡沒有的人（例如網址帶的已不在負載看板）要補一個選項，不能靜默退回「全部」
    unknownHandlerGetsAnOption() {
        const e = listEnv();
        e.setHandlerFilterValue('99');
        assert.equal(e.activeHandlerSelect.value, '99');
        assert.equal(e.activeHandlerSelect.options.length, 1);
        e.setHandlerFilterValue('99');
        assert.equal(e.activeHandlerSelect.options.length, 1, '已有的選項不重複補');
        e.setHandlerFilterValue('');
        assert.equal(e.activeHandlerSelect.value, '', '空字串＝全部');
    },

    // A12：負載看板每列「改派他名下的單…」：有單且具 Assign 才出現；點下去篩選該處理人並預先全選
    loadBoardReassignButtonOnlyWhenAssignableAndHasOrders() {
        const withOrders = listEnv();
        const wrap = withOrders.loadBoardActions({ userId: 7, name: '王小明', activeWorkOrders: 3 });
        const btn = wrap.find(n => n.tagName === 'BUTTON');
        assert.ok(btn, '有進行中單要有按鈕');
        assert.equal(btn.textContent, '改派他名下的單…');
        btn.listeners.click[0]();
        assert.equal(withOrders.activeHandlerSelect.value, '7');
        assert.equal(withOrders.pendingAutoSelect, true);

        const none = listEnv().loadBoardActions({ userId: 7, name: '王小明', activeWorkOrders: 0 });
        assert.equal(none.find(n => n.tagName === 'BUTTON'), null, '沒有進行中單不給按鈕');
        const viewer = listEnv({ canAssign: false }).loadBoardActions({ userId: 7, name: '王小明', activeWorkOrders: 3 });
        assert.equal(viewer.find(n => n.tagName === 'BUTTON'), null, '沒有 Assign 不給按鈕');
        assert.ok(viewer.find(n => n.tagName === 'A'), '處理人工作頁連結仍在');
    },

    // 預先全選：全部放得進一頁時勾好；超過一頁或超過上限時不勾並提示
    autoSelectWhenEverythingFitsOnePage() {
        const e = listEnv();
        e.applyHandlerFilter(7, { autoSelect: true });
        e.consumeAutoSelect({ items: items(17), total: 17 });
        assert.equal(e.selectedMap.size, 17);
        assert.ok(e.bulkHintEl.textContent.includes('改派給'));
        assert.equal(e.pendingAutoSelect, false, '只預選一次');
    },

    autoSelectSkippedWhenMoreThanOnePage() {
        const e = listEnv();
        e.applyHandlerFilter(7, { autoSelect: true });
        e.consumeAutoSelect({ items: items(20), total: 45 });
        assert.equal(e.selectedMap.size, 0);
        assert.ok(e.bulkHintEl.textContent.includes('超過一頁'));
    },

    autoSelectBoundaryAtTheBatchLimit() {
        const at = listEnv();
        at.applyHandlerFilter(7, { autoSelect: true });
        at.consumeAutoSelect({ items: items(100), total: 100 });
        assert.equal(at.selectedMap.size, 100, '剛好 100 張可以預選');

        const over = listEnv();
        over.applyHandlerFilter(7, { autoSelect: true });
        over.consumeAutoSelect({ items: items(101), total: 101 });
        assert.equal(over.selectedMap.size, 0, '101 張超過上限不預選');
    },

    autoSelectDoesNothingWhenNotRequested() {
        const e = listEnv();
        e.consumeAutoSelect({ items: items(3), total: 3 });
        assert.equal(e.selectedMap.size, 0);
    },

    // 勾選列：沒勾＝沒有動作；勾了有「改派給…」；超過 100 停用並說明（兩側各一例）
    bulkBarActionsFollowTheSelection() {
        const empty = listEnv();
        empty.updateBulkBar();
        assert.equal(empty.bulkBarActions.children.length, 0);

        const some = listEnv({ selected: items(100) });
        some.updateBulkBar();
        const btn = some.bulkBarActions.find(n => n.tagName === 'BUTTON' && n.textContent === '改派給…');
        assert.ok(btn);
        assert.ok(!btn.disabled, '100 張可以改派');

        const over = listEnv({ selected: items(101) });
        over.updateBulkBar();
        const blocked = over.bulkBarActions.find(n => n.tagName === 'BUTTON' && n.textContent === '改派給…');
        assert.equal(blocked.disabled, true);
        assert.ok(over.bulkBarActions.textContent.includes('一次最多改派 100 張'));
    },

    // F14-3：預覽說明的每一條旗標（兩側各一例）
    previewLinesCarryEveryFlag() {
        const { bulkPreviewLines } = new Function(`${extractFunction(bulkSource, 'export function bulkPreviewLines(').replace(/^export /, '')}\nreturn { bulkPreviewLines };`)();
        const base = { totalOrders: 17, newCount: 14, mergeCount: 3, skipCount: 0, movedMembers: 34, grantHostCount: 0, handlerName: '乙', handlerPaused: false, handlerHasNoEmail: false, handlerCannotHandle: false };
        const plain = bulkPreviewLines(base);
        assert.equal(plain.length, 1);
        assert.ok(plain[0].text.includes('共 17 張') && plain[0].text.includes('原單換人 14') && plain[0].text.includes('併入對方既有的單 3') && plain[0].text.includes('34 台'));

        const all = bulkPreviewLines({ ...base, grantHostCount: 5, handlerPaused: true, handlerHasNoEmail: true, handlerCannotHandle: true });
        assert.equal(all.length, 5);
        assert.ok(all.some(l => l.text.includes('5 台') && l.text.includes('交辦授與')));
        assert.ok(all.some(l => l.type === 'warning' && l.text.includes('暫停接單')));
        assert.ok(all.some(l => l.type === 'warning' && l.text.includes('沒有設定 email')));
        assert.ok(all.some(l => l.type === 'warning' && l.text.includes('沒有處理能力')));
    },

    // 套用結果：有失敗就是 warning 並寫出張數；全成功是 success
    resultSummaryReportsFailures() {
        const { bulkResultSummary } = new Function(`${extractFunction(bulkSource, 'export function bulkResultSummary(').replace(/^export /, '')}\nreturn { bulkResultSummary };`)();
        const ok = bulkResultSummary({ succeededCount: 17, newCount: 14, mergeCount: 3, movedMembers: 34, skippedCount: 0, failedCount: 0 });
        assert.equal(ok.type, 'success');
        assert.ok(ok.text.includes('已改派 17 張'));
        assert.ok(!ok.text.includes('失敗'));
        const bad = bulkResultSummary({ succeededCount: 16, newCount: 13, mergeCount: 3, movedMembers: 32, skippedCount: 1, failedCount: 1 });
        assert.equal(bad.type, 'warning');
        assert.ok(bad.text.includes('略過 1 張') && bad.text.includes('失敗 1 張'));
    },

    // A12：面板流程——選人預覽送出的請求、預覽後才能套用、套用後回報並通知頁面重載
    async panelPreviewThenApply() {
        const dom = installDom();
        const button = loadButton(dom);
        const { roleText, statusNote } = loadElements(dom);
        const calls = [];
        const api = {
            async post(url, body) {
                calls.push({ url, body });
                if (url.endsWith('/candidates')) return [{ userId: 9, account: 'DOMAIN\\to', displayName: '乙', activeLoad: 0, hiddenHostCount: 0, hasEmail: true }];
                if (url.endsWith('/preview')) return { totalOrders: 2, newCount: 1, mergeCount: 1, skipCount: 0, movedMembers: 4, grantHostCount: 0, handlerName: '乙', handlerPaused: false, handlerHasNoEmail: false, handlerCannotHandle: false, rows: [
                    { workOrderId: 1, issueLabel: 'A', currentHandlerName: '甲', outcome: 'new', memberCount: 2, grantHostCount: 0 },
                    { workOrderId: 2, issueLabel: 'B', currentHandlerName: '甲', outcome: 'merge', targetWorkOrderId: 8, memberCount: 2, grantHostCount: 0 }] };
                return { handlerName: '乙', succeededCount: 2, newCount: 1, mergeCount: 1, movedMembers: 4, skippedCount: 0, failedCount: 0, rows: [] };
            }
        };
        const picker = { element: document.createElement('div'), getValue: () => '9' };
        const renderedTables = [];
        const renderTable = (container, opts) => { renderedTables.push(opts); container.appendChild(document.createElement('table')); };
        const noop = () => { };
        const applied = [];
        const toasts = [];
        const body = [
            extractFunction(bulkSource, 'function bulkPreviewLines(') ,
            extractFunction(bulkSource, 'function bulkResultSummary('),
            extractFunction(bulkSource, 'function bulkOutcomeText('),
            extractFunction(bulkSource, 'function orderLink('),
            extractFunction(bulkSource, 'export function createBulkReassignPanel(').replace(/^export /, '')
        ].join('\n').replace(/export function bulkPreviewLines/, 'function bulkPreviewLines');
        const create = new Function('api', 'appUrl', 'button', 'renderTable', 'renderLoading', 'searchableUserSelect', 'candidatesToUserOptions', 'toast', 'withBusy',
            'roleText', 'statusNote', 'statusBadge', 'createSeqGuard', 'document', 'BULK_REASSIGN_MAX',
            `${body}\nreturn createBulkReassignPanel;`)(
            api, p => p, button, renderTable, noop, () => picker, list => list.map(c => ({ userId: c.userId, active: true })), (m) => toasts.push(m), () => noop,
            roleText, statusNote, (text) => roleText('badge', text), () => ({ n: 0, next() { return ++this.n; }, isCurrent(s) { return s === this.n; } }), document, 100);
        const container = document.createElement('div');
        container.className = 'd-none';
        const panel = create({ container, getSelectedIds: () => [1, 2], onApplied: r => applied.push(r) });

        await panel.open();
        assert.ok(!container.classList.contains('d-none'), '開啟後顯示面板');
        assert.deepEqual(calls.map(c => c.url), ['/api/work-orders/candidates']);
        const previewBtn = container.find(n => n.tagName === 'BUTTON' && n.textContent === '預覽');
        await previewBtn.listeners.click[0]();
        assert.deepEqual(calls[1], { url: '/api/work-orders/bulk-reassign/preview', body: { workOrderIds: [1, 2], handlerId: 9 } });
        assert.equal(calls.length, 2, '預覽階段不套用');
        assert.equal(applied.length, 0);
        const applyBtn = container.find(n => n.tagName === 'BUTTON' && n.textContent === '確定改派 2 張');
        assert.ok(applyBtn, '確定鈕寫出可改派張數（新單＋併入）');
        await applyBtn.listeners.click[0]();
        assert.deepEqual(calls[2], { url: '/api/work-orders/bulk-reassign', body: { workOrderIds: [1, 2], handlerId: 9, notifyNow: false } });
        assert.equal(applied.length, 1, '套用後通知頁面重載');
        assert.ok(container.textContent.includes('已改派 2 張'));
        container.find(n => n.tagName === 'BUTTON' && n.textContent === '完成').listeners.click[0]();
        assert.ok(container.classList.contains('d-none'), '完成後收起面板');
    },

    // 套用請求在等待時對話框被取消（序號已過期）：伺服器已經改派，onApplied（清勾選、重載清單與負載看板）仍要呼叫，
    // 只有結果畫面不渲染
    async panelApplyStaleStillNotifiesPage() {
        const dom = installDom();
        const button = loadButton(dom);
        const { roleText, statusNote } = loadElements(dom);
        const guard = { n: 0, stale: false, next() { return ++this.n; }, isCurrent(s) { return !this.stale && s === this.n; } };
        const api = {
            async post(url) {
                if (url.endsWith('/candidates')) return [{ userId: 9, account: 'DOMAIN\\to', displayName: '乙', activeLoad: 0, hiddenHostCount: 0, hasEmail: true }];
                if (url.endsWith('/preview')) return { totalOrders: 1, newCount: 1, mergeCount: 0, skipCount: 0, movedMembers: 2, grantHostCount: 0, handlerName: '乙', handlerPaused: false, handlerHasNoEmail: false, handlerCannotHandle: false, rows: [
                    { workOrderId: 1, issueLabel: 'A', currentHandlerName: '甲', outcome: 'new', memberCount: 2, grantHostCount: 0 }] };
                guard.stale = true;   // 回應回來之前，對話框已被取消或換了畫面
                return { handlerName: '乙', succeededCount: 1, newCount: 1, mergeCount: 0, movedMembers: 2, skippedCount: 0, failedCount: 0, rows: [] };
            }
        };
        const picker = { element: document.createElement('div'), getValue: () => '9' };
        const renderTable = (container) => { container.appendChild(document.createElement('table')); };
        const noop = () => { };
        const applied = [];
        const body = [
            extractFunction(bulkSource, 'function bulkPreviewLines('),
            extractFunction(bulkSource, 'function bulkResultSummary('),
            extractFunction(bulkSource, 'function bulkOutcomeText('),
            extractFunction(bulkSource, 'function orderLink('),
            extractFunction(bulkSource, 'export function createBulkReassignPanel(').replace(/^export /, '')
        ].join('\n').replace(/export function bulkPreviewLines/, 'function bulkPreviewLines');
        const create = new Function('api', 'appUrl', 'button', 'renderTable', 'renderLoading', 'searchableUserSelect', 'candidatesToUserOptions', 'toast', 'withBusy',
            'roleText', 'statusNote', 'statusBadge', 'createSeqGuard', 'document', 'BULK_REASSIGN_MAX',
            `${body}\nreturn createBulkReassignPanel;`)(
            api, p => p, button, renderTable, noop, () => picker, list => list.map(c => ({ userId: c.userId, active: true })), noop, () => noop,
            roleText, statusNote, (text) => roleText('badge', text), () => guard, document, 100);
        const container = document.createElement('div');
        const panel = create({ container, getSelectedIds: () => [1], onApplied: r => applied.push(r) });

        await panel.open();
        await container.find(n => n.tagName === 'BUTTON' && n.textContent === '預覽').listeners.click[0]();
        await container.find(n => n.tagName === 'BUTTON' && n.textContent === '確定改派 1 張').listeners.click[0]();

        assert.equal(applied.length, 1, '伺服器已改派，過期也要通知頁面重載');
        assert.ok(!container.textContent.includes('已改派 1 張'), '過期的回應不渲染結果畫面');
    },

    // 預覽沒有任何可改派的單（全部略過）時，確定鈕停用
    async panelApplyDisabledWhenNothingToDo() {
        const dom = installDom();
        const button = loadButton(dom);
        const { roleText, statusNote } = loadElements(dom);
        const picker = { element: document.createElement('div'), getValue: () => '9' };
        const api = { async post(url) {
            if (url.endsWith('/candidates')) return [];
            return { totalOrders: 1, newCount: 0, mergeCount: 0, skipCount: 1, movedMembers: 0, grantHostCount: 0, handlerName: '乙', rows: [{ workOrderId: 1, issueLabel: 'A', currentHandlerName: '乙', outcome: 'skip', reason: '已經是乙的單', memberCount: 0, grantHostCount: 0 }] };
        } };
        const body = [
            extractFunction(bulkSource, 'function bulkPreviewLines('), extractFunction(bulkSource, 'function bulkResultSummary('),
            extractFunction(bulkSource, 'function bulkOutcomeText('), extractFunction(bulkSource, 'function orderLink('),
            extractFunction(bulkSource, 'export function createBulkReassignPanel(').replace(/^export /, '')
        ].join('\n');
        const noop = () => { };
        const create = new Function('api', 'appUrl', 'button', 'renderTable', 'renderLoading', 'searchableUserSelect', 'candidatesToUserOptions', 'toast', 'withBusy',
            'roleText', 'statusNote', 'statusBadge', 'createSeqGuard', 'document', 'BULK_REASSIGN_MAX', `${body}\nreturn createBulkReassignPanel;`)(
            api, p => p, button, (c) => c.appendChild(document.createElement('table')), noop, () => picker, () => [], noop, () => noop,
            roleText, statusNote, t => roleText('badge', t), () => ({ n: 0, next() { return ++this.n; }, isCurrent(s) { return s === this.n; } }), document, 100);
        const container = document.createElement('div');
        const panel = create({ container, getSelectedIds: () => [1], onApplied: noop });
        await panel.open();
        await container.find(n => n.tagName === 'BUTTON' && n.textContent === '預覽').listeners.click[0]();
        const applyBtn = container.find(n => n.tagName === 'BUTTON' && n.textContent.startsWith('確定改派'));
        assert.equal(applyBtn.disabled, true);
        assert.equal(applyBtn.textContent, '確定改派 0 張');
    }
};

// ── L12：停用或暫停接單前的確認（users.js）──────────────────────────────

function usersEnv({ summary, summaryError = false, choice = 'skip' } = {}) {
    installDom();
    const asked = [];
    const toasts = [];
    const location = { href: '' };
    const api = { async get(url) { asked.push(url); if (summaryError) throw new Error('403'); return summary; } };
    const askWorkOrderChoice = async opts => { asked.push(opts); return choice; };
    const body = [
        extractFunction(usersSource, 'function reassignUrl('),
        extractFunction(usersSource, 'async function confirmBeforeAvailabilityChange('),
        extractFunction(usersSource, 'function continueAfterAvailabilityChange(')
    ].join('\n');
    const notices = [];
    const fns = new Function('api', 'askWorkOrderChoice', 'appUrl', 'URLSearchParams', 'location', 'notifyActiveWorkOrders',
        `${body}\nreturn { confirmBeforeAvailabilityChange, continueAfterAvailabilityChange, reassignUrl };`)(
        api, askWorkOrderChoice, p => `/lf${p}`, URLSearchParams, location, (...args) => notices.push(args));
    return { ...fns, asked, toasts, location, notices };
}

Object.assign(cases, {
    // 有單：先說明張數與台數，並帶出三個選擇；「先不處理」仍可停用
    async availabilityAsksWithCountsWhenOrdersExist() {
        const e = usersEnv({ summary: { activeWorkOrders: 3, pausedWorkOrders: 1, activeMembers: 12 }, choice: 'skip' });
        const result = await e.confirmBeforeAvailabilityChange({ userId: 5, account: 'DOMAIN\\h1' }, '停用');
        assert.equal(result, 'informed', '先不處理＝仍可停用，且不再重複提示');
        assert.equal(e.asked[0], '/api/handlers/5/work-orders/summary');
        const dialog = e.asked[1];
        assert.ok(dialog.message.includes('4 張') && dialog.message.includes('12 台'), '張數含暫停單');
        assert.ok(dialog.message.includes('1 張暫停中'));
        assert.ok(dialog.title.includes('停用'));
    },

    async availabilityReassignChoiceGoesToBatchFlow() {
        const e = usersEnv({ summary: { activeWorkOrders: 2, activeMembers: 5 }, choice: 'reassign' });
        const user = { userId: 5, account: 'DOMAIN\\h1' };
        const result = await e.confirmBeforeAvailabilityChange(user, '暫停接單');
        assert.equal(result, 'reassign');
        e.continueAfterAvailabilityChange(result, user, 2, '暫停接單');
        assert.equal(e.location.href, '/lf/work-orders?handlerId=5&status=active&reassign=1');
        assert.equal(e.notices.length, 0);
    },

    async availabilityCancelStopsTheChange() {
        const e = usersEnv({ summary: { activeWorkOrders: 2, activeMembers: 5 }, choice: 'cancel' });
        assert.equal(await e.confirmBeforeAvailabilityChange({ userId: 5, account: 'x' }, '停用'), 'cancel');
    },

    // 沒有單：不問；查不到摘要（沒有檢視交辦能力）也不問，沿用儲存後的提示
    async availabilitySilentWithoutOrdersOrSummary() {
        const none = usersEnv({ summary: { activeWorkOrders: 0, pausedWorkOrders: 0, activeMembers: 0 } });
        assert.equal(await none.confirmBeforeAvailabilityChange({ userId: 5, account: 'x' }, '停用'), 'silent');
        assert.equal(none.asked.length, 1, '沒有單不開對話框');

        const denied = usersEnv({ summaryError: true });
        assert.equal(await denied.confirmBeforeAvailabilityChange({ userId: 5, account: 'x' }, '停用'), 'silent');

        const onlyPaused = usersEnv({ summary: { activeWorkOrders: 0, pausedWorkOrders: 1, activeMembers: 0 }, choice: 'skip' });
        assert.equal(await onlyPaused.confirmBeforeAvailabilityChange({ userId: 5, account: 'x' }, '停用'), 'informed', '只有暫停單也算有單');
    },

    // 接續：看過說明不再重複提示；沒看過（silent）才沿用原提示
    availabilityContinuationNoticeOnlyWhenNotInformed() {
        const e = usersEnv({ summary: {} });
        e.continueAfterAvailabilityChange('informed', { userId: 5 }, 3, '停用');
        assert.equal(e.notices.length, 0);
        e.continueAfterAvailabilityChange('silent', { userId: 5 }, 3, '停用');
        assert.equal(e.notices.length, 1);
    }
});

const name = process.argv[2];
if (!name) {
    console.log(Object.keys(cases).join('\n'));
    process.exit(0);
}
assert(cases[name], `未知案例：${name}`);
await cases[name]();
console.log(`PASS ${name}`);
