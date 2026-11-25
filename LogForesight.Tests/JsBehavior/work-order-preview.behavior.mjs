/**
 * 階段 A-2：交辦預覽的主機表格（狀態欄、全選、逐台重開）、可見範圍提示、候選人標記。
 * 以 extractFunction 切出 records.js 與 core/ui.js 的 production 函式，只替換外部相依。
 * 由 WorkOrderPreviewBehaviorTests 以 `node work-order-preview.behavior.mjs <案例名>` 逐案執行。
 */
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { installDom } from './mini-dom.mjs';
import { loadElements } from './load-elements.mjs';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const jsRoot = path.resolve(scriptDir, '..', '..', 'LogForesight.Web', 'wwwroot', 'js');
const read = rel => fs.readFileSync(path.join(jsRoot, rel), 'utf8').replaceAll('\r\n', '\n').replace(/^﻿/, '');
const records = read('pages/records.js');
const ui = read('core/ui.js');
const { statusMeta } = await import(pathToFileURL(path.join(jsRoot, 'core', 'handling-statuses.js')).href);

function extractFunction(source, declaration) {
    const start = source.indexOf(declaration);
    assert(start >= 0, `找不到 production 函式：${declaration}`);
    const open = source.indexOf('{', source.indexOf(')', start));
    let depth = 0;
    for (let i = open; i < source.length; i++) {
        if (source[i] === '{') depth++;
        else if (source[i] === '}' && --depth === 0) return source.slice(start, i + 1).replace(/^export /, '');
    }
    throw new Error(`production 函式未閉合：${declaration}`);
}

const dom = installDom();
const { statusNote } = loadElements(dom);
const formatNumber = n => String(n);
const renderTable = new Function('document', 'renderEmpty', 'bindScrollAffordance',
    `${extractFunction(ui, 'export function renderTable')}\nreturn renderTable;`)(document, () => { }, () => { });
const recordsFns = ['export function workOrderHostStatusText', 'export function renderWorkOrderHostTable', 'function renderWorkOrderPreviewSummary']
    .map(d => extractFunction(records, d)).join('\n');
const { workOrderHostStatusText, renderWorkOrderHostTable, renderWorkOrderPreviewSummary } =
    new Function('document', 'statusMeta', 'statusNote', 'formatNumber', 'renderTable',
        `${recordsFns}\nreturn { workOrderHostStatusText, renderWorkOrderHostTable, renderWorkOrderPreviewSummary };`)(document, statusMeta, statusNote, formatNumber, renderTable);
const candidatesToUserOptions = new Function(`${extractFunction(ui, 'export function candidatesToUserOptions')}\nreturn candidatesToUserOptions;`)();

const host = (id, extra = {}) => ({ hostId: id, hostName: `H${id}`, ...extra });
const build = (hosts, state, onChange = () => { }) => {
    const container = dom.newElement('div');
    renderWorkOrderHostTable(container, hosts, state, onChange);
    return container;
};
const checkOf = (table, id) => table.find(n => n.id === `wo-host-${id}`);
const headCheck = table => table.find(n => n.tagName === 'INPUT');
const change = (el, checked) => { el.checked = checked; el.dispatch('change'); };

const cases = {
    statusTextFourKinds() {
        assert.equal(workOrderHostStatusText(host(1)), '未處理');
        assert.equal(workOrderHostStatusText(host(1, { existingHandlerName: '小明' })), '處理中（目前：小明）');
        assert.equal(workOrderHostStatusText(host(1, { closedReason: 'closed', closedStatus: 'resolved', closedByAccount: 'DOM\\bob', closedAt: '2026-11-20T10:00:00' })),
            '已結案：已處理（DOM\\bob，11-20）');
        assert.equal(workOrderHostStatusText(host(1, { closedReason: 'dismissed', closedByAccount: 'bob', closedAt: '2026-03-05T00:00:00' })),
            '先前判定不處理（bob，03-05）');
    },
    statusTextOmitsMissingParts() {
        assert.equal(workOrderHostStatusText(host(1, { closedReason: 'closed', closedStatus: 'resolved' })), '已結案：已處理');
        assert.equal(workOrderHostStatusText(host(1, { closedReason: 'closed', closedStatus: 'resolved', closedByAccount: 'bob' })), '已結案：已處理（bob）');
        assert.equal(workOrderHostStatusText(host(1, { closedReason: 'dismissed', closedAt: '2026-03-05T00:00:00' })), '先前判定不處理（03-05）');
        assert.equal(workOrderHostStatusText(host(1, { closedReason: 'dismissed' })), '先前判定不處理');
    },
    selectAllOnlyOrdinaryHosts() {
        const excluded = new Set([1, 2]), reopen = new Set();
        const hosts = [host(1), host(2), host(3, { closedReason: 'closed', closedStatus: 'resolved' })];
        let changes = 0;
        const table = build(hosts, { excludedHostIds: excluded, reopenHostIds: reopen }, () => changes++);
        change(headCheck(table), true);
        assert.deepEqual([...excluded], []);
        assert.deepEqual([...reopen], [], '全選不得重開已結案的主機');
        assert.equal(checkOf(table, 3).checked, false);
        change(headCheck(table), false);
        assert.deepEqual([...excluded].sort(), [1, 2]);
        assert.deepEqual([...reopen], []);
        assert.equal(changes, 2);
    },
    headerIndeterminateWhenPartial() {
        const state = { excludedHostIds: new Set([2]), reopenHostIds: new Set() };
        const hosts = [host(1), host(2)];
        let head = headCheck(build(hosts, state, () => { }));
        assert.equal(head.indeterminate, true);
        assert.equal(head.checked, false);
        state.excludedHostIds.clear();
        head = headCheck(build(hosts, state, () => { }));
        assert.equal(head.indeterminate, false);
        assert.equal(head.checked, true);
        state.excludedHostIds.add(1); state.excludedHostIds.add(2);
        head = headCheck(build(hosts, state, () => { }));
        assert.equal(head.indeterminate, false);
        assert.equal(head.checked, false);
    },
    closedRowGoesToReopenSet() {
        const state = { excludedHostIds: new Set(), reopenHostIds: new Set() };
        const hosts = [host(1), host(3, { closedReason: 'dismissed' })];
        const table = build(hosts, state, () => { });
        assert.equal(checkOf(table, 3).checked, false, '已結案列預設不勾');
        change(checkOf(table, 3), true);
        assert.deepEqual([...state.reopenHostIds], [3]);
        assert.deepEqual([...state.excludedHostIds], []);
        change(checkOf(table, 3), false);
        assert.deepEqual([...state.reopenHostIds], []);
        assert.deepEqual([...state.excludedHostIds], []);
        // 一般主機取消＝排除
        change(checkOf(table, 1), false);
        assert.deepEqual([...state.excludedHostIds], [1]);
    },
    selectionSurvivesPaging() {
        const state = { excludedHostIds: new Set(), reopenHostIds: new Set() };
        const page1 = [host(1), host(2, { closedReason: 'closed', closedStatus: 'resolved' })];
        const t1 = build(page1, state, () => { });
        change(checkOf(t1, 2), true);
        change(checkOf(t1, 1), false);
        const page2 = build([host(5)], state, () => { });
        assert.equal(checkOf(page2, 5).checked, true);
        const back = build(page1, state, () => { });
        assert.equal(checkOf(back, 2).checked, true);
        assert.equal(checkOf(back, 1).checked, false);
    },
    checkboxHasLabelAndAriaLabel() {
        const table = build([host(1)], { excludedHostIds: new Set(), reopenHostIds: new Set() }, () => { });
        assert.match(checkOf(table, 1).getAttribute('aria-label'), /H1/);
        const label = table.find(n => n.tagName === 'LABEL');
        assert.equal(label.getAttribute('for'), 'wo-host-1');
        assert.equal(label.textContent, 'H1');
        assert.match(table.textContent, /已處理的主機要逐台勾選才會重新交辦/);
    },
    summaryShowsNoAccessWarning() {
        const wrap = dom.newElement('div');
        renderWorkOrderPreviewSummary(wrap, { affectedHosts: 3, assigneeNoAccessTotal: 28, assigneeNoAccess: [{ hostName: 'A' }] });
        assert.match(wrap.textContent, /28 台不在對方的檢視範圍/);
        renderWorkOrderPreviewSummary(wrap, { affectedHosts: 3, assigneeNoAccessTotal: 0 });
        assert.doesNotMatch(wrap.textContent, /檢視範圍/);
    },
    summaryShowsClosedSkips() {
        const wrap = dom.newElement('div');
        renderWorkOrderPreviewSummary(wrap, { closedSkippedHosts: 2, dismissedSkippedHosts: 1 });
        assert.match(wrap.textContent, /已結案略過 2 台/);
        assert.match(wrap.textContent, /先前判定不處理略過 1 台/);
        renderWorkOrderPreviewSummary(wrap, { closedSkippedHosts: 0, dismissedSkippedHosts: 0 });
        assert.doesNotMatch(wrap.textContent, /略過/);
    },
    candidateBadgesAndOrder() {
        const [a, b, c] = candidatesToUserOptions([
            { userId: 1, account: 'a', displayName: 'A', recentlyResolvedSameIssue: true, activeLoad: 5, hiddenHostCount: 2, hasEmail: false, paused: true },
            { userId: 2, account: 'b', displayName: 'B', activeLoad: 0, hiddenHostCount: 0, hasEmail: true },
            { userId: 3, account: 'c', displayName: 'C', activeLoad: 0 }
        ]);
        assert.deepEqual(a.badges, ['最近處理過', '進行中 5 台', '看不到 2 台（將以交辦授與補上）', '沒有 email', '暫停接單']);
        assert.deepEqual(b.badges, ['目前沒有進行中']);
        assert.deepEqual(c.badges, ['目前沒有進行中'], 'hasEmail 未提供時不標沒有 email');
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
