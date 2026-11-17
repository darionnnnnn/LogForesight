/**
 * 第 76-2 輪批次 C-2：交辦總覽的管理者提醒區（work-orders.js 的 renderReminders、loadReminders）。
 * 以 extractFunction 切出 production 函式本體，只替換外部相依（API／共用元件）。
 * 由 WorkOrdersRemindersBehaviorTests 以 `node work-orders-reminders.behavior.mjs <案例名>` 逐案執行。
 */
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { installDom } from './mini-dom.mjs';
import { loadElements } from './load-elements.mjs';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(scriptDir, '..', '..');
const source = fs.readFileSync(path.join(repoRoot, 'LogForesight.Web', 'wwwroot', 'js', 'pages', 'work-orders.js'), 'utf8')
    .replaceAll('\r\n', '\n').replace(/^﻿/, '');

function extractFunction(declaration) {
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

const SOURCES = ['function renderReminders(', 'async function loadReminders('].map(extractFunction).join('\n');

function env({ response, fail = false } = {}) {
    const dom = installDom();
    const { statusNote } = loadElements(dom);
    const el = tag => document.createElement(tag);
    const remindersContainer = el('div');
    const appUrl = p => '/lf' + p;
    const linkButton = (text, href) => { const a = el('a'); a.href = href; a.className = 'btn btn-sm btn-outline-primary'; a.textContent = text; return a; };
    const formatNumber = n => String(n);
    const api = { get: async url => { if (fail) throw new Error('boom'); api.last = url; return response; } };
    const factory = new Function('document', 'remindersContainer', 'statusNote', 'linkButton', 'appUrl', 'formatNumber', 'api',
        `${SOURCES}\nreturn { renderReminders, loadReminders };`);
    const fns = factory(document, remindersContainer, statusNote, linkButton, appUrl, formatNumber, api);
    return { ...fns, remindersContainer, api };
}

const find = (node, predicate) => { const out = []; node.find(n => { if (predicate(n)) out.push(n); return false; }); return out; };
const data = (o = {}) => ({ thresholdDays: 3, escalatedCount: 0, unassignableCount: 0, noRecipientCount: 0, noRecipient: [], ...o });

const cases = {
    // 三類都是 0：整區不顯示（容器是空的）
    allZeroShowsNothing() {
        const { renderReminders, remindersContainer } = env();
        renderReminders(data());
        assert.equal(remindersContainer.children.length, 0);
        assert.equal(remindersContainer.textContent, '');
    },

    // 沒有資料（取不到）也不顯示
    nullShowsNothing() {
        const { renderReminders, remindersContainer } = env();
        renderReminders(null);
        assert.equal(remindersContainer.children.length, 0);
    },

    // 三類都有：一個警告訊息＋三行，每行「N 筆」並各有連結；連結指到對應位置
    allThreeClassesShowRowsWithLinks() {
        const { renderReminders, remindersContainer } = env();
        renderReminders(data({ escalatedCount: 2, unassignableCount: 3, noRecipientCount: 1, noRecipient: [{ workOrderId: 17, issueLabel: 'disk 153' }] }));
        const notes = find(remindersContainer, n => (n.className || '').includes('lf-status-note--warning'));
        assert.equal(notes.length, 1);
        assert.equal(notes[0].getAttribute('role'), 'status');
        const rows = find(remindersContainer, n => n.tagName === 'LI');
        assert.equal(rows.length, 3);
        assert.ok(rows.every(r => r.className.includes('lf-list-row')));
        assert.ok(rows[0].textContent.includes('上報超過 3 天沒人處理：2 筆'));
        assert.ok(rows[1].textContent.includes('派不出去的問題：3 筆'));
        assert.ok(rows[2].textContent.includes('沒有任何收件人的單：1 筆'));
        const links = find(remindersContainer, n => n.tagName === 'A');
        assert.deepEqual(links.map(a => a.href), ['/lf/work-orders?status=escalated', '/lf/work-orders#gaps', '/lf/work-orders/17']);
    },

    // 只有一類不是 0：只出現那一行
    onlyNonZeroClassIsListed() {
        const { renderReminders, remindersContainer } = env();
        renderReminders(data({ unassignableCount: 4 }));
        const rows = find(remindersContainer, n => n.tagName === 'LI');
        assert.equal(rows.length, 1);
        assert.ok(rows[0].textContent.includes('4 筆'));
    },

    // 重畫會清掉舊內容：先有數字、再變 0，提醒區消失（不留殘影）
    rerenderClearsPreviousContent() {
        const { renderReminders, remindersContainer } = env();
        renderReminders(data({ escalatedCount: 1 }));
        assert.ok(remindersContainer.children.length > 0);
        renderReminders(data());
        assert.equal(remindersContainer.children.length, 0);
    },

    // 載入：打彙整端點並畫出；端點失敗時提醒區維持空白、不擲例外
    async loadFetchesEndpointAndRenders() {
        const { loadReminders, remindersContainer, api } = env({ response: data({ escalatedCount: 1 }) });
        await loadReminders();
        assert.equal(api.last, '/api/work-orders/admin-reminders');
        assert.equal(find(remindersContainer, n => n.tagName === 'LI').length, 1);
    },

    async loadFailureLeavesAreaEmpty() {
        const { loadReminders, remindersContainer } = env({ fail: true });
        await loadReminders();
        assert.equal(remindersContainer.children.length, 0);
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
