/**
 * handler-detail.js 列上「回覆」入口與下一張 carry 的行為測試。
 *
 * 這裡抽出並執行 production 的 expandOrderRow、replyOrder、openWholeOrderReply、rejectedReply、buildMemberPanel，
 * 只替換它們碰到的 DOM、API 與 UI 邊界；不重寫入口分流或 carry 的傳遞演算法。
 */

import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { installDom } from './mini-dom.mjs';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(scriptDir, '..', '..');
const handlerPath = path.join(repoRoot, 'LogForesight.Web', 'wwwroot', 'js', 'pages', 'handler-detail.js');
const handlerSource = fs.readFileSync(handlerPath, 'utf8').replaceAll('\r\n', '\n');

function extractFunction(declaration) {
    const start = handlerSource.indexOf(declaration);
    assert(start >= 0, `找不到 production 函式：${declaration}`);
    const open = handlerSource.indexOf('{', start);
    let depth = 0;
    for (let i = open; i < handlerSource.length; i++) {
        if (handlerSource[i] === '{') depth++;
        else if (handlerSource[i] === '}' && --depth === 0) return handlerSource.slice(start, i + 1);
    }
    throw new Error(`production 函式未閉合：${declaration}`);
}

// 「可回覆台數」的 production 判定（core/handling-statuses.js）：切出本體執行，不在測試裡重寫
const statusesSource = fs.readFileSync(path.join(repoRoot, 'LogForesight.Web', 'wwwroot', 'js', 'core', 'handling-statuses.js'), 'utf8').replaceAll('\r\n', '\n');
function extractFrom(source, declaration) {
    const start = source.indexOf(declaration);
    assert(start >= 0, `找不到 production 函式：${declaration}`);
    const open = source.indexOf('{', start);
    let depth = 0;
    for (let i = open; i < source.length; i++) {
        if (source[i] === '{') depth++;
        else if (source[i] === '}' && --depth === 0) return source.slice(start, i + 1);
    }
    throw new Error(`production 函式未閉合：${declaration}`);
}

function makeContext(members) {
    const dom = installDom();
    const createElement = dom.newElement;
    const originalCreateElement = document.createElement;
    document.createElement = tag => {
        const element = originalCreateElement(tag);
        element.prepend = (...nodes) => element.children.unshift(...nodes);
        element.focus = () => { };
        element.classList.toggle = (name, force) => {
            const has = element.classList.contains(name);
            const shouldHave = force === undefined ? !has : Boolean(force);
            const names = new Set(String(element.className || '').split(' ').filter(Boolean));
            if (shouldHave) names.add(name);
            else names.delete(name);
            element.className = [...names].join(' ');
            return shouldHave;
        };
        return element;
    };

    const expandOrderRow = extractFunction('function expandOrderRow(');
    const replyOrder = extractFunction('async function replyOrder(');
    const buildMemberPanel = extractFunction('function buildMemberPanel(');
    const openSingleReply = extractFunction('function openSingleReply(');
    const openWholeOrderReply = extractFunction('function openWholeOrderReply(');
    const rejectedReply = extractFunction('function rejectedReply(');

    const replyableCountSource = extractFrom(statusesSource, 'export function replyableCount(').replace('export function', 'function');
    const code = `
${replyableCountSource}
let lastMembersUrl = null;
const pendingReplyCarry = new Map();
const rows = new Map();
let loadPromise = Promise.resolve();
let lastModal = null;
const handledOrderIds = new Set();
let lastRefresh = Promise.resolve();
let refreshCalls = 0;
const posts = [];
const toasts = [];
const replyToasts = [];
const guardLoad = (_element, load) => { loadPromise = Promise.resolve(load()); };
const canReply = true;
const MEMBER_PAGE_SIZE = 20;
const MEMBER_STATUS_OPTIONS = [{ value: 'active', label: '進行中' }];
const MEMBER_STATUS_META = { active: { label: '進行中', variant: 'success' } };
// 成員狀態徽章改由 core/handling-statuses.js 的 statusBadgeMeta 衍生；替身以同一份對照回應
const statusBadgeMeta = status => MEMBER_STATUS_META[status] ?? null;
const memberItems = ${JSON.stringify(members ?? [{ caseId: 501, issueKey: 'issue-a', hostName: 'SRV-A', status: 'active', closedAt: null }])};
const api = {
    get: async url => ({
        items: (lastMembersUrl = url, memberItems),
        page: 1,
        pageSize: 20,
        total: 1,
        hiddenMemberCount: 0
    }),
    post: async (url, body, options) => { posts.push({ url, body, options }); return { cases: 20, workOrderClosed: false }; }
};
const appUrl = path => path;
const formatDate = value => value || '';
const formatDateTime = value => String(value || '');
const ORIGIN_LABELS = {};
const statusBadge = label => String(label);
// 共用元素工廠（core/ui.js button、core/elements.js roleText／metaLine）的最小替身；本測試只看 carry 的傳遞
const roleText = (role, text) => { const span = document.createElement('span'); span.className = role; span.textContent = text ?? ''; return span; };
const metaLine = items => {
    const valid = (items || []).filter(item => item !== null && item !== undefined && item !== '' && item !== false);
    if (valid.length === 0) return null;
    const line = document.createElement('div');
    for (const item of valid) line.appendChild(typeof item === 'object' ? item : roleText('', String(item)));
    return line;
};
const button = (text, { onClick } = {}) => {
    const el = document.createElement('button');
    el.className = 'btn';
    el.textContent = text;
    if (onClick) el.addEventListener('click', onClick);
    return el;
};
const renderLoading = () => { };
const renderPagination = () => { };
const toastReplyResult = (result, payload) => { replyToasts.push({ result, payload }); };
const scheduleRefresh = () => { refreshCalls++; return Promise.resolve(); };
const describeReplyRejection = async (error, workOrderId) => error.status === 403 ? '改派給 h-chen（單 ' + workOrderId + '）' : undefined;
const toast = (message, type) => { toasts.push({ message, type }); };
const openWorkOrderReplyModal = options => { lastModal = options; };
const orderRowEl = workOrderId => rows.get(workOrderId)?.tr ?? null;
const expandOrderRow = ${expandOrderRow.toString().replace(/^function expandOrderRow/, 'function expandOrderRow')};
const buildMemberPanel = ${buildMemberPanel.toString().replace(/^function buildMemberPanel/, 'function buildMemberPanel')};
const openNextOrder = () => { };
const openSingleReply = ${openSingleReply};
const openWholeOrderReply = ${openWholeOrderReply};
const rejectedReply = ${rejectedReply};
const replyOrder = ${replyOrder.toString().replace(/^async function replyOrder/, 'async function replyOrder')};

function renderTable(container, { columns, rows: items }) {
    const table = document.createElement('table');
    const head = document.createElement('thead');
    const headRow = document.createElement('tr');
    for (const column of columns) {
        const cell = document.createElement('th');
        const content = column.renderHeader ? column.renderHeader() : column.title;
        if (content && typeof content === 'object') cell.appendChild(content);
        else cell.textContent = content || '';
        headRow.appendChild(cell);
    }
    head.appendChild(headRow);
    table.appendChild(head);
    const body = document.createElement('tbody');
    for (const item of items) {
        const row = document.createElement('tr');
        for (const column of columns) {
            const cell = document.createElement('td');
            const content = column.render ? column.render(item) : '';
            if (content && typeof content === 'object') cell.appendChild(content);
            else cell.textContent = content || '';
            row.appendChild(cell);
        }
        body.appendChild(row);
    }
    table.appendChild(body);
    container.replaceChildren(table);
}

function register(row) {
    const tr = document.createElement('tr');
    const detailRow = document.createElement('tr');
    const cell = document.createElement('td');
    detailRow.appendChild(cell);
    tr.nextElementSibling = detailRow;
    tr.setAttribute('aria-expanded', 'false');
    tr.click = () => {
        tr.setAttribute('aria-expanded', 'true');
        const carry = pendingReplyCarry.get(row.workOrderId) || null;
        pendingReplyCarry.delete(row.workOrderId);
        buildMemberPanel(row, cell, carry);
    };
    rows.set(row.workOrderId, { tr, cell });
    return { tr, cell };
}

function waitForLoad() { return loadPromise; }
function getPanel(row) { return rows.get(row.workOrderId).cell.querySelector('.handler-wo-member-panel'); }
function getLastModal() { return lastModal; }
function clearLastModal() { lastModal = null; }
function getMembersUrl() { return lastMembersUrl; }
return { posts, toasts, replyToasts, refreshCalls: () => refreshCalls, rejectedReply, getMembersUrl, expandOrderRow, replyOrder, pendingReplyCarry, register, waitForLoad, getPanel, getLastModal, clearLastModal };
`;

    return new Function('document', 'Node', 'path', code)(document, Node, path);
}

// 下一張沿用上一張的結論與說明：多台單的整單彈窗帶 initialStatus／previousNote，且有「送出並回覆下一張」
async function nextMultiHostReplyCarriesStatusAndNote() {
    const context = makeContext();
    const row = { workOrderId: 50, counts: { active: 2, total: 2 }, issueLabel: '問題 A' };
    context.register(row);
    const carry = { status: 'in_progress', note: '上一張說明' };

    await context.replyOrder(row, carry);

    const modal = context.getLastModal();
    assert(modal, '多台單的「回覆」一律開彈窗');
    assert.equal(modal.initialStatus, carry.status);
    assert.equal(modal.previousNote, carry.note);
    assert.equal(typeof modal.onNext, 'function', '整單彈窗要能接著回覆下一張');
}

// 手動點列上的「回覆」（carry 為 null）不可沿用任何上一張的內容
async function manualReplyDoesNotCarry() {
    const context = makeContext();
    const row = { workOrderId: 51, counts: { active: 2, total: 2 }, issueLabel: '問題 B' };
    context.register(row);
    await context.replyOrder(row, { status: 'in_progress', note: '只准下一張使用' });
    context.clearLastModal();

    await context.replyOrder(row, null);
    assert.equal(context.getLastModal().initialStatus, undefined, '手動回覆不可沿用上一張結論');
    assert.equal(context.getLastModal().previousNote, null, '手動回覆不可沿用上一張說明');
}

// 列上「回覆」：1 台開單台彈窗、20 台開整單彈窗且送出的 caseIds 為空（端點語意＝全部進行中）、0 台不開彈窗
async function rowReplyOpensModalForOneManyAndZero() {
    const one = makeContext();
    const oneRow = { workOrderId: 60, counts: { active: 1, total: 1 }, issueLabel: '問題' };
    one.register(oneRow);
    await one.replyOrder(oneRow, null);
    assert(one.getLastModal(), '1 台開彈窗');
    assert(one.getLastModal().targetText.includes('SRV-A'));

    const many = makeContext();
    const manyRow = { workOrderId: 61, counts: { active: 20, total: 20 }, issueLabel: '問題' };
    many.register(manyRow);
    await many.replyOrder(manyRow, null);
    const modal = many.getLastModal();
    assert(modal, '20 台開彈窗（不再只展開該列）');
    assert.equal(modal.targetText, '全部進行中主機（20 台）');
    assert.equal(modal.multiTarget, true);
    assert.equal(many.getMembersUrl(), null, '多台不需要先取成員');
    assert.equal(modal.aiContext.hostCount, 20);
    await modal.submit({ status: 'resolved', note: null, dueDate: null });
    assert.equal(many.posts.length, 1);
    assert.equal(many.posts[0].url, '/api/work-orders/61/reply');
    assert.deepEqual(many.posts[0].body.caseIds, [], '整單回覆 caseIds 為空');
    assert.equal(many.posts[0].options.silent, true, '被拒訊息由彈窗常駐顯示');
    assert.deepEqual(many.replyToasts[0].payload, { status: 'resolved', note: null, dueDate: null }, '成功訊息拿得到結論');

    const none = makeContext();
    const noneRow = { workOrderId: 62, counts: { active: 0, total: 5 }, issueLabel: '問題' };
    none.register(noneRow);
    await none.replyOrder(noneRow, null);
    assert.equal(none.getLastModal(), null, '0 台不開彈窗');
    assert.deepEqual(none.toasts, [{ message: '這張單已沒有進行中的主機', type: 'info' }]);
}

// 被拒（403／400）：重新載入摘要與清單；單張單的 403 回傳含新處理人的說明，其他沿用伺服器訊息
async function rejectedReplyRefreshesListAndNamesHandler() {
    const context = makeContext();
    const row = { workOrderId: 70, counts: { active: 20, total: 20 }, issueLabel: '問題' };
    context.register(row);
    await context.replyOrder(row, null);
    const modal = context.getLastModal();

    assert.equal(context.refreshCalls(), 0);
    const message = await modal.onRejected({ status: 403 });
    assert.equal(context.refreshCalls(), 1, '被拒後重新載入清單與摘要');
    assert.equal(message, '改派給 h-chen（單 70）');
    assert.equal(await modal.onRejected({ status: 400 }), undefined, '400 沿用伺服器訊息');
    assert.equal(context.refreshCalls(), 2);

    // 勾選多張單的 reply-many 不指名新處理人，但一樣重載
    assert.equal(context.rejectedReply({ status: 403 }, null), undefined);
    assert.equal(context.refreshCalls(), 3);
}

// 只剩一台進行中、而且在停用主機上：counts.active 是 0，但回覆端點與成員清單都涵蓋它——要當單台直接開回覆，標題寫對台
async function singleActiveOnDisabledHostOpensSingleReply() {
    const context = makeContext([{ caseId: 601, issueKey: 'issue-d', hostName: 'SRV-OFF', hostDisabled: true, status: 'active', closedAt: null }]);
    const row = { workOrderId: 60, counts: { active: 0, activeOnDisabledHosts: 1, total: 1 }, issueLabel: '問題 D' };
    context.register(row);

    await context.replyOrder(row, null);

    const modal = context.getLastModal();
    assert(modal, '停用主機上只剩一台進行中時應直接開單台回覆彈窗');
    assert(modal.targetText.includes('SRV-OFF'), `彈窗對象應是那台主機：${modal.targetText}`);
    assert(modal.targetText.includes('主機已停用'), `對象說明應標出主機已停用：${modal.targetText}`);
    assert.equal(modal.multiTarget, undefined, '單台回覆不用複數說法');
}

// 一台正常加一台停用：實際會送出 2 台，不能被當成「只剩一台」而抓到第一筆就回覆——開整單彈窗、台數是 2
async function oneActivePlusOneDisabledOpensWholeOrderModal() {
    const context = makeContext([
        { caseId: 701, issueKey: 'issue-e', hostName: 'SRV-ON', status: 'active', closedAt: null },
        { caseId: 702, issueKey: 'issue-e', hostName: 'SRV-OFF', hostDisabled: true, status: 'active', closedAt: null }
    ]);
    const row = { workOrderId: 70, counts: { active: 1, activeOnDisabledHosts: 1, total: 2 }, issueLabel: '問題 E' };
    context.register(row);

    await context.replyOrder(row, null);

    const modal = context.getLastModal();
    assert(modal, '兩台都可回覆時開整單彈窗');
    assert.equal(modal.targetText, '全部進行中主機（2 台）', '台數含停用主機上的進行中');
    assert.equal(context.getMembersUrl(), null, '不可被當成單台而去抓第一筆成員');
}

const cases = {
    singleActiveOnDisabledHostOpensSingleReply,
    oneActivePlusOneDisabledOpensWholeOrderModal,
    nextMultiHostReplyCarriesStatusAndNote,
    manualReplyDoesNotCarry,
    rowReplyOpensModalForOneManyAndZero,
    rejectedReplyRefreshesListAndNamesHandler
};

const name = process.argv[2];
if (!name) {
    console.log(Object.keys(cases).join('\n'));
    process.exit(0);
}

try {
    await cases[name]();
    console.log(`PASS ${name}`);
} catch (error) {
    console.error(`FAIL ${name}: ${error.stack || error.message}`);
    process.exitCode = 1;
}
