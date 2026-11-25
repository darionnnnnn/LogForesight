/**
 * 第 76-2 輪批次 D-2：停用主機標記、處理人看不到的台數、續掛已停止、停用／暫停處理人後的進行中單提示。
 * 以切函式＋替身執行 production 頁面模組的函式（work-order-detail.js、work-orders.js、users.js），
 * 只替換外部相依（statusBadge、appUrl、toast）；fact、statusNote、roleText 用 production 的 core/elements.js。
 * 由 HandlerAvailabilityBehaviorTests 以 `node handler-availability.behavior.mjs <案例名>` 逐案執行。
 */
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { installDom } from './mini-dom.mjs';
import { loadElements } from './load-elements.mjs';

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

const detailSource = read('work-order-detail.js');
const listSource = read('work-orders.js');
const usersSource = read('users.js');

function env(capabilities = ['Assign', 'ViewAll']) {
    const dom = installDom();
    const { fact, statusNote, roleText } = loadElements(dom);
    const statusBadge = (text, variant, opts = {}) => {
        const s = document.createElement('span');
        s.className = `lf-badge lf-badge--${variant}`;
        s.textContent = text;
        if (opts.title) s.title = opts.title;
        return s;
    };
    const toasts = [];
    const toast = (message, type, delay, options) => toasts.push({ message, type, delay, options });
    const appUrl = p => `/lf${p}`;
    const currentUser = { userId: 1, capabilities };
    const hasCapability = (user, capability) => Array.isArray(user?.capabilities) && user.capabilities.includes(capability);
    return { fact, statusNote, roleText, statusBadge, toast, toasts, appUrl, currentUser, hasCapability };
}

const detailFns = e => new Function('fact', 'statusNote', 'roleText', 'statusBadge', 'appUrl', 'document', 'hasCapability', 'currentUser',
    `${extractFunction(detailSource, 'function viewerIsAdminLike(')}\n${extractFunction(detailSource, 'function memberGapFacts(')}\n${extractFunction(detailSource, 'function continuationStopNote(')}\n` +
    `${extractFunction(detailSource, 'function renderMemberHostCell(')}\nreturn { memberGapFacts, continuationStopNote, renderMemberHostCell };`)(
    e.fact, e.statusNote, e.roleText, e.statusBadge, e.appUrl, document, e.hasCapability, e.currentUser);

const listBadge = e => new Function('statusBadge',
    `${extractFunction(listSource, 'function continuationStoppedBadge(')}\nreturn continuationStoppedBadge;`)(e.statusBadge);

const usersNotice = e => new Function('toast', 'appUrl', 'URLSearchParams', 'getCurrentUser', 'hasCapability',
    `${extractFunction(usersSource, 'async function notifyActiveWorkOrders(')}\nreturn notifyActiveWorkOrders;`)(
    e.toast, e.appUrl, URLSearchParams, async () => e.currentUser, e.hasCapability);

const cases = {
    // D8：處理人都看得到（0）→ 頁首事實列不多任何東西
    unseenFactHiddenWhenZero() {
        const { memberGapFacts } = detailFns(env());
        assert.deepEqual(memberGapFacts({ counts: { active: 3, activeOnDisabledHosts: 0 }, handlerUnseenMemberCount: 0 }), []);
        assert.deepEqual(memberGapFacts({ counts: {} }), []);
    },

    // D8：>0 時事實列顯示「處理人目前看不到 N 台」
    unseenFactShownWhenPositive() {
        const { memberGapFacts } = detailFns(env());
        const facts = memberGapFacts({ counts: { active: 3 }, handlerUnseenMemberCount: 2 });
        assert.equal(facts.length, 1);
        assert.equal(facts[0].textContent, '處理人目前看不到 2 台');
    },

    // D6：停用主機上的進行中成員有數字時，事實列說明它們不計入處理人的台數
    disabledHostFactShownWhenPositive() {
        const { memberGapFacts } = detailFns(env());
        const facts = memberGapFacts({ counts: { active: 1, activeOnDisabledHosts: 1 }, handlerUnseenMemberCount: 0 });
        assert.equal(facts.length, 1);
        assert.ok(facts[0].textContent.startsWith('主機已停用 1 台'));
    },

    // D4-b：續掛已停止時詳情出現說明列，內容是原因顯示名稱；沒停止就沒有
    stopNoteOnlyWhenStopped() {
        const { continuationStopNote } = detailFns(env());
        assert.equal(continuationStopNote({ continuationStopped: false }), null);
        assert.equal(continuationStopNote({}), null);
        const note = continuationStopNote({ continuationStopped: true, continuationStoppedLabel: '處理人暫停接單', continuationStoppedReason: 'handler_paused' });
        assert.ok(note.classList.contains('lf-status-note'));
        assert.ok(note.textContent.includes('續掛已停止：處理人暫停接單'));
    },

    // D4-b：總覽列用 lf-badge 標出原因；沒停止就沒有
    listBadgeOnlyWhenStopped() {
        const badge = listBadge(env());
        assert.equal(badge({ continuationStopped: false }), null);
        const b = badge({ continuationStopped: true, continuationStoppedLabel: '處理人已停用', continuationStoppedReason: 'handler_inactive' });
        assert.ok(b.className.includes('lf-badge'));
        assert.equal(b.textContent, '續掛已停止：處理人已停用');
    },

    // D6-b：成員列的主機格——停用主機上進行中的成員標「主機已停用」；啟用中或已結案的不標
    memberHostCellMarksDisabledHost() {
        const { renderMemberHostCell } = detailFns(env());
        const marked = renderMemberHostCell({ hostId: 7, hostName: 'HOST-B', hostDisabled: true, closedAt: null });
        assert.ok(marked.textContent.includes('HOST-B'));
        assert.ok(marked.textContent.includes('主機已停用'));
        const plain = renderMemberHostCell({ hostId: 7, hostName: 'HOST-A', hostDisabled: false, closedAt: null });
        assert.ok(!plain.textContent.includes('主機已停用'));
        const closed = renderMemberHostCell({ hostId: 7, hostName: 'HOST-B', hostDisabled: true, closedAt: '2026-11-01T00:00:00' });
        assert.ok(!closed.textContent.includes('主機已停用'));
    },

    // U1：「暫停接單」只對派工池成員顯示；已暫停的人保留開關，才能取消
    pauseToggleOnlyForDispatchPoolMembers() {
        const make = groups => new Function('groups', `${extractFunction(usersSource, 'function showsDispatchPausedToggle(')}\nreturn showsDispatchPausedToggle;`)(groups);
        const groups = [
            { groupId: 1, dispatchPool: true, active: true },
            { groupId: 2, dispatchPool: false, active: true },
            { groupId: 3, dispatchPool: true, active: false }
        ];
        const shows = make(groups);
        assert.equal(shows({ groupIds: [1] }), true, '在派工池群組內顯示');
        assert.equal(shows({ groupIds: [2, 1] }), true, '多個群組只要其一是派工池');
        assert.equal(shows({ groupIds: [2] }), false, '只在非派工池群組不顯示');
        assert.equal(shows({ groupIds: [3] }), false, '派工池群組已停用不算');
        assert.equal(shows({ groupIds: [] }), false, '沒有群組不顯示');
        assert.equal(shows({}), false, '舊回應沒有 groupIds 不爆');
        assert.equal(shows({ groupIds: [2], dispatchPaused: true }), true, '已暫停的人要能取消');
        assert.ok(/showsDispatchPausedToggle\(u\) \? renderDispatchPausedToggle\(u\)/.test(usersSource), '接單欄要走這個判斷');
    },

    // D7：停用或暫停後 0 張單 → 不提示
    async noticeSilentWhenZero() {
        const e = env();
        const notify = usersNotice(e);
        await notify({ userId: 5, account: 'DOMAIN\\h1' }, 0, '停用');
        await notify({ userId: 5, account: 'DOMAIN\\h1' }, undefined, '暫停接單');
        assert.equal(e.toasts.length, 0);
    },

    // D7：>0 張 → 提示張數，連到已篩選（該處理人、進行中）的交辦總覽，路徑走 appUrl
    async noticeWithLinkWhenPositive() {
        const e = env();
        await usersNotice(e)({ userId: 5, account: 'DOMAIN\\h1' }, 3, '暫停接單');
        assert.equal(e.toasts.length, 1);
        const t = e.toasts[0];
        assert.equal(t.type, 'warning');
        assert.ok(t.message.includes('3 張進行中的交辦單'));
        assert.ok(t.message.includes('暫停接單'));
        assert.equal(t.options.link.href, '/lf/work-orders?handlerId=5&status=active');
        assert.ok(t.options.link.text.length > 0);
    },

    // 救援帳號（只有 Maintain、ViewAudit）進不了交辦總覽：不附連結，改請通知系統管理員
    async noticeWithoutLinkWhenViewerCannotOpenWorkOrders() {
        const e = env(['Maintain', 'ViewAudit']);
        await usersNotice(e)({ userId: 5, account: 'DOMAIN\\h1' }, 3, '停用');
        assert.equal(e.toasts.length, 1);
        const t = e.toasts[0];
        assert.ok(t.message.includes('3 張進行中的交辦單'));
        assert.ok(t.message.includes('請通知系統管理員改派或結案'));
        assert.ok(!t.options?.link, '進不去的人不給連結');
    },

    // 只有 ViewAll（例如主管）進得了交辦總覽：照樣附連結
    async noticeWithLinkWhenViewerHasViewAllOnly() {
        const e = env(['ViewAll']);
        await usersNotice(e)({ userId: 5, account: 'DOMAIN\\h1' }, 2, '停用');
        assert.equal(e.toasts[0].options.link.href, '/lf/work-orders?handlerId=5&status=active');
    },

    // 處理人本人（只有 Handle）看單：不顯示寫給管理者的「處理人目前看不到」與續掛已停止
    handlerViewerDoesNotSeeAdminOnlyMarks() {
        const { memberGapFacts, continuationStopNote } = detailFns(env(['Handle']));
        const facts = memberGapFacts({ counts: { active: 3, activeOnDisabledHosts: 1 }, handlerUnseenMemberCount: 2 });
        assert.deepEqual(facts.map(f => f.textContent.split(' ')[0]), ['主機已停用'], '停用主機的事實仍顯示，看不到台數不顯示');
        assert.equal(continuationStopNote({ continuationStopped: true, continuationStoppedLabel: '處理人暫停接單' }), null);
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
