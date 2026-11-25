/**
 * 第 81 輪批次 U：就緒檢查與上線輔助的前端行為。
 *  - 使用者編輯視窗的群組即時摘要（core/user-group-summary.js，直接匯入 production 模組）。
 *  - 健康頁「派工就緒」卡（settings.js renderDispatchReadiness）、主機群組清單的「看得到的派工池群組」欄
 *    （groups.js visiblePoolGroupsCell）、批次設定群組後的提示（hosts.js noPoolGroupsNotice）：切 production 函式本體執行。
 * 由 DispatchReadinessBehaviorTests 以 `node dispatch-readiness.behavior.mjs <案例名>` 逐案執行。
 */
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { installDom } from './mini-dom.mjs';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const jsRoot = path.resolve(scriptDir, '..', '..', 'LogForesight.Web', 'wwwroot', 'js');
const read = rel => fs.readFileSync(path.join(jsRoot, rel), 'utf8').replaceAll('\r\n', '\n').replace(/^﻿/, '');

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
const { createUserGroupSummary, summaryLines } = await import('../../LogForesight.Web/wwwroot/js/core/user-group-summary.js');
const textOf = node => node.textContent;
const find = (node, predicate) => { const out = []; node.find(n => { if (predicate(n)) out.push(n); return false; }); return out; };

const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

/** 依勾選的群組算出摘要（模擬後端）：群組 1＝部門（有處理能力、10 台）、群組 2＝派工池 */
function fakePreview({ groupIds }) {
    const caps = groupIds.length > 0 ? ['處理（標記狀態、回覆交辦）', '確認權限異動'] : [];
    return Promise.resolve({
        capabilities: caps,
        visibleHostCount: groupIds.includes(1) ? 10 : 0,
        inPool: groupIds.includes(2),
        canHandle: groupIds.length > 0,
        inactiveGroupCount: 0
    });
}

// ── 群組即時摘要 ───────────────────────────────────────────

const cases = {
    async summaryShowsCapabilitiesHostsAndPool() {
        const host = dom.newElement('div');
        const summary = createUserGroupSummary({ host, fetchPreview: fakePreview });
        await summary.refresh({ userId: null, groupIds: [1, 2], active: true });
        const text = textOf(host);
        assert(text.includes('處理（標記狀態、回覆交辦）、確認權限異動'), text);
        assert(text.includes('10 台'), text);
        assert(text.includes('在派工池（會被自動派工）'), text);
    },

    async summaryUpdatesWhenGroupsToggle() {
        const host = dom.newElement('div');
        const summary = createUserGroupSummary({ host, fetchPreview: fakePreview });
        await summary.refresh({ userId: 5, groupIds: [1, 2], active: true });
        assert(textOf(host).includes('10 台'));
        // 取消勾選群組 1：可見主機歸零、仍在派工池（群組 2）
        await summary.refresh({ userId: 5, groupIds: [2], active: true });
        assert(textOf(host).includes('0 台') && !textOf(host).includes('10 台'), textOf(host));
        assert(textOf(host).includes('在派工池'));
        // 全部取消：沒有能力、不在池
        await summary.refresh({ userId: 5, groupIds: [], active: true });
        assert(textOf(host).includes('沒有任何能力'), textOf(host));
        assert(textOf(host).includes('不在派工池'), textOf(host));
    },

    async summaryDropsStaleResponse() {
        const host = dom.newElement('div');
        let slowResolve;
        const slow = new Promise(resolve => { slowResolve = resolve; });
        let call = 0;
        const summary = createUserGroupSummary({
            host,
            fetchPreview: args => (++call === 1 ? slow : fakePreview(args))
        });
        const first = summary.refresh({ userId: null, groupIds: [1], active: true });   // 慢的舊請求
        await summary.refresh({ userId: null, groupIds: [], active: true });            // 較新的請求先回來
        slowResolve({ capabilities: ['舊資料'], visibleHostCount: 99, inPool: false, canHandle: false, inactiveGroupCount: 0 });
        await first;
        assert(!textOf(host).includes('舊資料') && !textOf(host).includes('99'), textOf(host));
        assert(textOf(host).includes('沒有任何能力'), textOf(host));
    },

    async summaryFailureShowsNoteAndKeepsPageUsable() {
        const host = dom.newElement('div');
        const summary = createUserGroupSummary({ host, fetchPreview: () => Promise.reject(new Error('boom')) });
        await summary.refresh({ userId: null, groupIds: [1], active: true });
        assert(textOf(host).includes('暫時無法算出群組摘要'), textOf(host));
    },

    summaryLinesWarnPoolMemberWithoutHandle() {
        const lines = summaryLines({ capabilities: ['檢視全部主機'], visibleHostCount: 3, inPool: true, canHandle: false, inactiveGroupCount: 1 });
        const joined = lines.map(([k, v]) => `${k}${v}`).join('|');
        assert(joined.includes('在派工池，但沒有處理能力，不會被派單'), joined);
        assert(joined.includes('1 個已停用'), joined);
    },

    // ── 健康頁派工就緒卡 ────────────────────────────────────

    renderReadinessShowsFailuresWithFixLinks() {
        const { render, host } = readinessEnv();
        render({
            failedCount: 1, mailChecked: true,
            items: [
                { id: 'schedule-off', title: '排程已啟用', passed: true, skipped: false, affected: 0, detail: '排程已啟用。', examples: [], fixUrl: '/runs#settings', fixLabel: '前往排程設定' },
                { id: 'host-no-group', title: '主機都有群組', passed: false, skipped: false, affected: 7, detail: '有 7 台啟用的主機沒有加入任何主機群組。', examples: ['A', 'B'], fixUrl: '/admin/hosts', fixLabel: '前往主機頁' }
            ]
        });
        const text = textOf(host);
        assert(text.includes('有 1 項會讓派工靜默失效'), text);
        assert(text.includes('不通過') && text.includes('通過'), text);
        assert(text.includes('（A、B 等 7 項）'), text);
        const links = find(host, n => n.tagName === 'A');
        assert.equal(links.length, 1, '只有不通過的項目有修正連結');
        assert.equal(links[0].href, '/app/admin/hosts');
        assert(!text.includes('郵件通知沒有開啟'));
    },

    renderReadinessAllPassedAndMailOffNote() {
        const { render, host } = readinessEnv();
        render({ failedCount: 0, mailChecked: false, items: [
            { id: 'auto-dispatch-off', title: '自動派工已開啟', passed: true, skipped: false, affected: 0, detail: '夜間分析後會自動派工。', examples: [], fixUrl: '/admin/settings#dispatch', fixLabel: '前往派工設定' }
        ] });
        const text = textOf(host);
        assert(text.includes('派工所需的設定都已就緒'), text);
        assert(text.includes('郵件通知沒有開啟'), text);
        assert.equal(find(host, n => n.tagName === 'A').length, 0);
    },

    renderReadinessSkippedItemHasNoFailureBadge() {
        const { render, host } = readinessEnv();
        render({ failedCount: 0, mailChecked: true, items: [
            { id: 'host-group-no-pool', title: '主機群組都有派工池看得到', passed: true, skipped: true, affected: 0, detail: '派工池目前沒有可接單的成員，無法判定。', examples: [], fixUrl: '/admin/groups', fixLabel: '前往群組與授權' }
        ] });
        const text = textOf(host);
        assert(text.includes('無法判定'), text);
        assert(!text.includes('不通過'), text);
    },

    renderReadinessNullClearsCard() {
        const { render, host } = readinessEnv();
        host.appendChild(dom.newElement('p'));
        render(null);
        assert.equal(host.children.length, 0);
    },

    // ── 主機群組欄與批次設定群組提示 ────────────────────────

    groupCellZeroWarnsWithAuthorizeLink() {
        const cell = groupCellFn()({ visiblePoolGroups: 0 });
        assert(textOf(cell).includes('0 個，不會被自動派工'), textOf(cell));
        const link = find(cell, n => n.tagName === 'A')[0];
        assert(link && link.href === '#access' && textOf(link) === '去授權');
    },

    groupCellPositiveAndNotApplicable() {
        const fn = groupCellFn();
        assert.equal(textOf(fn({ visiblePoolGroups: 2 })), '2 個');
        assert.equal(textOf(fn({ visiblePoolGroups: null })), '—');
        assert.equal(textOf(fn({})), '—');
    },

    batchNoticeNamesGroupsAndLinksToAccess() {
        const notice = noticeFn()([{ groupId: 1, groupName: '機房甲' }, { groupId: 2, groupName: '機房乙' }]);
        assert(notice.message.includes('機房甲、機房乙'), notice.message);
        assert(notice.message.includes('不會被自動派工'));
        assert.equal(notice.link.text, '去授權');
        assert.equal(notice.link.href, '/app/admin/groups#access');
    },

    batchNoticeNullWhenNothingToWarn() {
        assert.equal(noticeFn()([]), null);
        assert.equal(noticeFn()(undefined), null);
    }
};

// ── 環境組裝 ───────────────────────────────────────────────

function badge(text, variant) {
    const span = dom.newElement('span');
    span.className = `lf-badge lf-badge--${variant}`;
    span.textContent = text;
    return span;
}

function readinessEnv() {
    const host = dom.newElement('div');
    const docStub = { getElementById: id => (id === 'health-dispatch-readiness' ? host : null), createElement: t => document.createElement(t) };
    const note = (type, text) => { const el = dom.newElement('div'); el.textContent = text; el.className = type; return el; };
    // renderTable 替身：逐列逐欄執行 production 的 render，結果平放進容器，足夠檢查文字與連結
    const renderTable = (container, { columns, rows }) => {
        for (const row of rows) for (const col of columns) {
            const content = col.render ? col.render(row) : row[col.key];
            container.appendChild(content instanceof Node ? content : document.createTextNode(String(content ?? '')));
        }
    };
    const source = read('pages/settings.js');
    const body = `${extractFunction(source, 'function renderDispatchReadiness(')}\n${extractFunction(source, 'function dispatchReadinessDetail(')}\nreturn renderDispatchReadiness;`;
    const render = new Function('document', 'statusNote', 'statusBadge', 'renderTable', 'formatNumber', 'appUrl', body)(
        docStub, note, badge, renderTable, n => String(n), p => `/app${p}`);
    return { render, host };
}

function groupCellFn() {
    const source = read('pages/groups.js');
    const body = `${extractFunction(source, 'function visiblePoolGroupsCell(')}\nreturn visiblePoolGroupsCell;`;
    const roleText = (_role, text) => { const s = dom.newElement('span'); s.textContent = text; return s; };
    return new Function('document', 'roleText', 'statusBadge', body)(document, roleText, badge);
}

function noticeFn() {
    const source = read('pages/hosts.js');
    const body = `${extractFunction(source, 'export function noPoolGroupsNotice(')}\nreturn noPoolGroupsNotice;`;
    return new Function('appUrl', body)(p => `/app${p}`);
}

const name = process.argv[2];
if (!name) { console.log(Object.keys(cases).join('\n')); process.exit(0); }
assert(cases[name], `未知的案例：${name}`);
await cases[name]();
console.log(`PASS ${name}`);
