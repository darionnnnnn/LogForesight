/**
 * handler-detail.js 的下一張回覆 carry 行為測試。
 *
 * 這裡抽出並執行 production 的 expandOrderRow、replyOrder、buildMemberPanel，
 * 只替換它們碰到的 DOM、API 與 UI 邊界；不重寫 carry 的選擇或傳遞演算法。
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

function makeContext() {
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

    const code = `
const pendingReplyCarry = new Map();
const rows = new Map();
let loadPromise = Promise.resolve();
let lastModal = null;
const handledOrderIds = new Set();
let lastRefresh = Promise.resolve();
const guardLoad = (_element, load) => { loadPromise = Promise.resolve(load()); };
const canReply = true;
const MEMBER_PAGE_SIZE = 20;
const MEMBER_STATUS_OPTIONS = [{ value: 'active', label: '進行中' }];
const MEMBER_STATUS_META = { active: { label: '進行中', variant: 'success' } };
const api = {
    get: async () => ({
        items: [{ caseId: 501, issueKey: 'issue-a', hostName: 'SRV-A', status: 'active', closedAt: null }],
        page: 1,
        pageSize: 20,
        total: 1,
        hiddenMemberCount: 0
    }),
    post: async () => ({ workOrderClosed: false })
};
const appUrl = path => path;
const formatDate = value => value || '';
const statusBadge = label => String(label);
const renderLoading = () => { };
const renderPagination = () => { };
const toastReplyResult = () => { };
const scheduleRefresh = () => Promise.resolve();
const toast = () => { };
const openWorkOrderReplyModal = options => { lastModal = options; };
const orderRowEl = workOrderId => rows.get(workOrderId)?.tr ?? null;
const expandOrderRow = ${expandOrderRow.toString().replace(/^function expandOrderRow/, 'function expandOrderRow')};
const buildMemberPanel = ${buildMemberPanel.toString().replace(/^function buildMemberPanel/, 'function buildMemberPanel')};
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
return { expandOrderRow, replyOrder, pendingReplyCarry, register, waitForLoad, getPanel, getLastModal, clearLastModal };
`;

    return new Function('document', 'Node', 'path', code)(document, Node, path);
}

async function nextMultiHostReplyCarriesStatusAndNoteOnce() {
    const context = makeContext();
    const row = { workOrderId: 50, counts: { active: 2, total: 2 }, issueLabel: '問題 A' };
    context.register(row);
    const carry = { status: 'in_progress', note: '上一張說明' };

    await context.replyOrder(row, carry);
    await context.waitForLoad();

    assert.equal(context.pendingReplyCarry.size, 0, 'carry 已套用後不可留在 pending map');
    const panel = context.getPanel(row);
    assert(panel, '多主機回覆應建立 production member panel');
    const checkbox = panel.find(node => node.className.includes('handler-wo-member-select'));
    assert(checkbox, 'production member panel 應提供可選主機');
    checkbox.checked = true;
    checkbox.dispatch('change');
    const replyButton = panel.find(node => node.tagName === 'BUTTON' && node.textContent.includes('回覆選取的主機'));
    assert(replyButton, 'production member panel 應提供回覆按鈕');
    replyButton.dispatch('click');

    assert.equal(context.getLastModal().initialStatus, carry.status);
    assert.equal(context.getLastModal().previousNote, carry.note);
}

async function manualReplyClearsCarryFromExpandedPanel() {
    const context = makeContext();
    const row = { workOrderId: 51, counts: { active: 2, total: 2 }, issueLabel: '問題 B' };
    context.register(row);
    await context.replyOrder(row, { status: 'in_progress', note: '只准下一張使用' });
    await context.waitForLoad();

    // 已展開的列再次進入下一張路徑時，carry 也只能存在這一次。
    await context.replyOrder(row, { status: 'closed', note: '暫存 carry' });
    assert.equal(context.pendingReplyCarry.size, 0, '已展開 panel 套用 carry 後不可殘留');

    // 手動點列上的「回覆」會以 null 進入 production replyOrder，應清掉舊 carry。
    await context.replyOrder(row, null);
    assert.equal(context.getPanel(row)._replyCarry, null, '手動回覆應清掉 panel carry');
    context.clearLastModal();
    const checkbox = context.getPanel(row).find(node => node.className.includes('handler-wo-member-select'));
    checkbox.checked = true;
    checkbox.dispatch('change');
    const replyButton = context.getPanel(row).find(node => node.tagName === 'BUTTON' && node.textContent.includes('回覆選取的主機'));
    replyButton.dispatch('click');
    assert.equal(context.getLastModal().initialStatus, undefined, '手動回覆不可沿用上一張狀態');
    assert.equal(context.getLastModal().previousNote, null, '手動回覆不可沿用上一張說明');
}

const cases = {
    nextMultiHostReplyCarriesStatusAndNoteOnce,
    manualReplyClearsCarryFromExpandedPanel
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
