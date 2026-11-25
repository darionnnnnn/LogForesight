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

function env({ response, fail = false, capabilities = ['Assign', 'ViewAll'] } = {}) {
    const dom = installDom();
    const { statusNote, disclosure, roleText } = loadElements(dom);
    const currentUser = { capabilities };
    const hasCapability = (user, capability) => user.capabilities.includes(capability);
    const el = tag => document.createElement(tag);
    const remindersContainer = el('div');
    const appUrl = p => '/lf' + p;
    const linkButton = (text, href) => { const a = el('a'); a.href = href; a.className = 'btn btn-sm btn-outline-primary'; a.textContent = text; return a; };
    const formatNumber = n => String(n);
    const formatDateTime = v => `T:${v}`;
    const UNASSIGNABLE_LABELS = { no_pool: '沒有派工池成員' };
    const api = { get: async url => { if (fail) throw new Error('boom'); api.last = url; return response; } };
    const factory = new Function('document', 'remindersContainer', 'statusNote', 'linkButton', 'appUrl', 'formatNumber', 'api', 'disclosure', 'formatDateTime', 'UNASSIGNABLE_LABELS', 'roleText', 'currentUser', 'hasCapability',
        `${SOURCES}\nreturn { renderReminders, loadReminders };`);
    const fns = factory(document, remindersContainer, statusNote, linkButton, appUrl, formatNumber, api, disclosure, formatDateTime, UNASSIGNABLE_LABELS, roleText, currentUser, hasCapability);
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

    // 沒有 Assign 的檢視者（主管、開發人員）：多一行直說誰能處理；有 Assign 的人不多這行
    readOnlyViewerSeesWhoCanAct() {
        const viewer = env({ capabilities: ['ViewAll'] });
        viewer.renderReminders(data({ escalatedCount: 1 }));
        assert.ok(viewer.remindersContainer.textContent.includes('只有系統管理員可以處理或恢復'));
        const admin = env({ capabilities: ['Assign', 'ViewAll'] });
        admin.renderReminders(data({ escalatedCount: 1 }));
        assert.ok(!admin.remindersContainer.textContent.includes('只有系統管理員可以處理或恢復'));
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
        const rows = find(remindersContainer, n => n.tagName === 'LI' && n.className.includes('lf-list-row'));
        assert.equal(rows.length, 3);
        assert.ok(rows[0].textContent.includes('上報超過 3 天沒人處理：2 筆'));
        assert.ok(rows[1].textContent.includes('派不出去的問題：3 筆'));
        assert.ok(rows[2].textContent.includes('沒有任何收件人的單：1 筆'));
        const links = find(remindersContainer, n => n.tagName === 'A');
        assert.deepEqual(links.map(a => a.href), ['/lf/work-orders?status=escalated', '/lf/work-orders#gaps', '/lf/work-orders/17', '/lf/work-orders/17']);   // 最後一個是明細裡的單號連結
    },

    // 只有一類不是 0：只出現那一行
    onlyNonZeroClassIsListed() {
        const { renderReminders, remindersContainer } = env();
        renderReminders(data({ unassignableCount: 4 }));
        const rows = find(remindersContainer, n => n.tagName === 'LI');
        assert.equal(rows.length, 1);
        assert.ok(rows[0].textContent.includes('4 筆'));
    },

    // 今天新上報：只有這一類也要顯示提醒區；多一行「今天新上報：N 筆」並連到上報中篩選，與「超過 N 天」分開
    newEscalatedAloneShowsRegionWithLinkToEscalatedFilter() {
        const { renderReminders, remindersContainer } = env();
        renderReminders(data({ newEscalatedCount: 2, newEscalated: [{ workOrderId: 9, issueLabel: 'disk 153', hostName: 'SRV1', days: 0 }] }));
        const rows = find(remindersContainer, n => n.tagName === 'LI' && n.className.includes('lf-list-row'));
        assert.equal(rows.length, 1);
        assert.ok(rows[0].textContent.includes('今天新上報：2 筆'), rows[0].textContent);
        assert.ok(!rows[0].textContent.includes('超過'), '不與逾期類混在一起');
        const links = find(remindersContainer, n => n.tagName === 'A').map(a => a.href);
        assert.equal(links[0], '/lf/work-orders?status=escalated');
        assert.ok(links.includes('/lf/work-orders/9'), '明細有單號連結');
        assert.ok(!remindersContainer.textContent.includes('上報 0 天'), '今天新上報的明細不寫「上報 0 天」');
    },

    // 今天新上報與逾期兩類並存：兩行，今天新上報在前
    newEscalatedAndOverdueAreSeparateRows() {
        const { renderReminders, remindersContainer } = env();
        renderReminders(data({ newEscalatedCount: 1, escalatedCount: 4 }));
        const rows = find(remindersContainer, n => n.tagName === 'LI' && n.className.includes('lf-list-row'));
        assert.equal(rows.length, 2);
        assert.ok(rows[0].textContent.includes('今天新上報：1 筆'));
        assert.ok(rows[1].textContent.includes('上報超過 3 天沒人處理：4 筆'));
    },

    // 反覆結案又復發：只有這一類也要顯示提醒區；一行「反覆結案又復發：N 筆」，連到交辦總覽進行中（沒有專屬篩選）；0 時不出現
    recurrenceAloneShowsRowLinkedToActiveList() {
        const { renderReminders, remindersContainer } = env();
        renderReminders(data({ recurrenceCount: 2, recurrence: [{ workOrderId: 9, issueLabel: 'disk 153', hostName: 'SRV1', recurrenceCount: 4 }] }));
        const rows = find(remindersContainer, n => n.tagName === 'LI' && n.className.includes('lf-list-row'));
        assert.equal(rows.length, 1);
        assert.ok(rows[0].textContent.includes('反覆結案又復發：2 筆'), rows[0].textContent);
        const links = find(remindersContainer, n => n.tagName === 'A').map(a => a.href);
        assert.equal(links[0], '/lf/work-orders?status=active', '沒有對應篩選：連到交辦總覽進行中');
        assert.ok(links.includes('/lf/work-orders/9'), '明細有單號連結');
        assert.ok(remindersContainer.textContent.includes('復發 4 次') && remindersContainer.textContent.includes('SRV1'));
    },

    recurrenceZeroOrMissingAddsNoRow() {
        const { renderReminders, remindersContainer } = env();
        renderReminders(data({ escalatedCount: 1, recurrenceCount: 0 }));
        assert.ok(!remindersContainer.textContent.includes('反覆結案又復發'));
        renderReminders(data({ escalatedCount: 1 }));
        assert.ok(!remindersContainer.textContent.includes('反覆結案又復發'), '舊回應沒有欄位也不出現');
        renderReminders(data({ recurrenceCount: 0 }));
        assert.equal(remindersContainer.children.length, 0, '只有復發類且為 0 時整區不顯示');
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
    // 明細：每類有一顆預設收合的揭露鈕，上報明細的單號連到該單，派不出去標彙整時間與原因
    detailListsAreCollapsedAndLinked() {
        const { renderReminders, remindersContainer } = env();
        renderReminders(data({
            escalatedCount: 2, unassignableCount: 1, unassignableAt: '2026-11-16T02:10:00',
            escalated: [{ workOrderId: 5, issueLabel: 'disk 153', hostName: 'SRV1', days: 4 }, { workOrderId: 6, issueLabel: 'disk 7', hostName: 'SRV2', days: 9 }],
            unassignable: [{ issueLabel: 'nic 27', reason: 'no_pool', hosts: 3 }]
        }));
        const toggles = find(remindersContainer, n => n.tagName === 'BUTTON');
        assert.equal(toggles.length, 2);
        assert.ok(toggles.every(b => b.getAttribute('aria-expanded') === 'false'));
        const targets = find(remindersContainer, n => n.classList.contains('lf-disclosure-target'));
        assert.ok(targets.every(t => t.classList.contains('d-none')), '明細預設收合');
        const hrefs = find(remindersContainer, n => n.tagName === 'A').map(a => a.href);
        assert.ok(hrefs.includes('/lf/work-orders/5') && hrefs.includes('/lf/work-orders/6'));
        const text = remindersContainer.textContent;
        assert.ok(text.includes('disk 153') && text.includes('SRV2') && text.includes('上報 9 天'));
        assert.ok(text.includes('nic 27') && text.includes('沒有派工池成員') && text.includes('3 台'));
        assert.ok(text.includes('彙整於 T:2026-11-16T02:10:00'));
    },

    // 彙整時間為 null 不寫；筆數大於明細時註明只列前幾筆
    unassignableAtNullAndTruncationNote() {
        const { renderReminders, remindersContainer } = env();
        renderReminders(data({ unassignableCount: 80, unassignableAt: null, unassignable: [{ issueLabel: 'x', reason: 'no_pool', hosts: 1 }] }));
        const text = remindersContainer.textContent;
        assert.ok(!text.includes('彙整於'));
        assert.ok(text.includes('只列前 1 筆，共 80 筆'));
    },

    // 提醒信間隔為 0：常駐說明；非 0 不出現
    reminderDaysZeroShowsPersistentNote() {
        const off = env();
        off.renderReminders(data({ escalatedCount: 1, reminderDays: 0 }));
        assert.ok(off.remindersContainer.textContent.includes('提醒信已關閉（設定為 0），此處仍會顯示'));
        const on = env();
        on.renderReminders(data({ escalatedCount: 1, reminderDays: 3 }));
        assert.ok(!on.remindersContainer.textContent.includes('提醒信已關閉'));
    },

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

// ── 第 81 輪批次 F（F11-1）：管理員待辦的新增類別（昨晚取數結果、可派未派、處理人停用或暫停仍有單、未分組主機）──
Object.assign(cases, {
    // 四個新類別各一行、連到處理點；明細只有「處理人」類有
    todoCategoriesShowRowsWithLinks() {
        const { renderReminders, remindersContainer } = env();
        renderReminders(data({
            fetchFailedCount: 1, fetchFailed: { endedAt: '2026-11-25T02:00:00', trigger: 'schedule' },
            dispatchableCount: 4, dispatchableKnown: true,
            inactiveHandlerCount: 1, inactiveHandlers: [{ userId: 9, name: '王小明(wang)', disabled: true, activeWorkOrders: 2, activeMembers: 5 }],
            ungroupedHostCount: 3
        }));
        const rows = find(remindersContainer, n => n.tagName === 'LI' && n.className.includes('lf-list-row'));
        assert.equal(rows.length, 4);
        assert.ok(rows[0].textContent.includes('最近一趟取數失敗（T:2026-11-25T02:00:00）'), rows[0].textContent);
        assert.ok(rows[1].textContent.includes('可派未派的問題：4 個'));
        assert.ok(rows[2].textContent.includes('處理人已停用或暫停接單、名下仍有單：1 人'));
        assert.ok(rows[2].textContent.includes('王小明(wang)') && rows[2].textContent.includes('帳號已停用') && rows[2].textContent.includes('2 張、5 台'));
        assert.ok(rows[3].textContent.includes('未分組主機：3 台'));
        const links = find(remindersContainer, n => n.tagName === 'A').map(a => a.href);
        assert.deepEqual(links, ['/lf/runs', '/lf/work-orders#gaps', '/lf/work-orders?status=active&handlerInactive=1', '/lf/admin/hosts?status=ungrouped']);
    },

    // 邊界：只有新類別不是 0 也要顯示提醒區；全 0 與缺欄位（舊回應）不顯示
    onlyNewCategoryShowsRegionAndZeroOrMissingDoesNot() {
        const only = env();
        only.renderReminders(data({ ungroupedHostCount: 1 }));
        assert.ok(only.remindersContainer.children.length > 0);
        const zero = env();
        zero.renderReminders(data({ fetchFailedCount: 0, dispatchableCount: 0, inactiveHandlerCount: 0, ungroupedHostCount: 0 }));
        assert.equal(zero.remindersContainer.children.length, 0);
        const missing = env();
        missing.renderReminders(data());
        assert.equal(missing.remindersContainer.children.length, 0);
    },

    // 取數失敗但沒帶時間（舊資料）也要有一行，不擲例外
    fetchFailedWithoutTimeStillListed() {
        const { renderReminders, remindersContainer } = env();
        renderReminders(data({ fetchFailedCount: 1, fetchFailed: null }));
        const rows = find(remindersContainer, n => n.tagName === 'LI' && n.className.includes('lf-list-row'));
        assert.equal(rows.length, 1);
        assert.ok(rows[0].textContent.includes('最近一趟取數失敗'));
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
