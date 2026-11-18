/**
 * 主機群組成員（groups.js）：目前成員跨頁勾選與移出、加入預覽命中 0 台時的按鈕狀態。
 *
 * 做法：groups.js 模組頂層有大量事件掛載（getElementById／querySelector 各十餘處），切函式要注入的替身過多，
 * 所以整支讀入、去掉 import 行後以替身參數執行；document 換成「依 id 自動建立元素」的版本，
 * 事件處理常式從元素的 listeners 取出直接呼叫（可 await）。理由：保留真實的模組內狀態（selectedCurrent 等）才測得到跨頁行為。
 */
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { installDom } from './mini-dom.mjs';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const jsRoot = path.resolve(scriptDir, '..', '..', 'LogForesight.Web', 'wwwroot', 'js');
const dom = installDom();

const tick = () => new Promise(resolve => setImmediate(resolve));
const settle = async () => { for (let i = 0; i < 8; i++) await tick(); };

/** 載入 groups.js；members 以頁碼對應該頁資料，posts 記下所有 POST */
function load({ pages, previewResult = null, failPages = new Set(), delays = {} }) {
    const src = fs.readFileSync(path.join(jsRoot, 'pages', 'groups.js'), 'utf8').replaceAll('\r\n', '\n');
    const imports = src.match(/^import .*;$/gm) ?? [];
    assert.equal(imports.length, 5, 'groups.js 的匯入行數變了，替身要跟著檢查');
    const body = src.replace(/^import .*;$/gm, '');

    const els = new Map();
    const el = key => {
        if (!els.has(key)) els.set(key, dom.newElement('div'));
        return els.get(key);
    };
    const fakeDocument = {
        ...document,
        getElementById: id => el(`#${id}`),
        querySelector: sel => el(sel),
        querySelectorAll: () => [],
        createElement: tag => document.createElement(tag)
    };
    const spy = { posts: [], gets: [], pagers: new Map(), confirms: 0, errors: [] };
    const stubs = {
        document: fakeDocument,
        bootstrap: { Modal: class { show() { } hide() { } } },
        api: {
            get: async url => {
                spy.gets.push(url);
                const page = Number(new URLSearchParams(url.split('?')[1]).get('page'));
                if (delays[page]) await delays[page];
                if (url.includes('/members?') && failPages.has(page)) throw new Error('boom');
                if (url.includes('/members?')) return pages[page];
                return [];
            },
            post: async (url, bodyArg) => {
                spy.posts.push({ url, body: bodyArg });
                if (url.endsWith('/members/preview')) return previewResult(bodyArg);
                return { addedCount: 0 };
            }
        },
        renderTable: () => { }, renderLoading: () => { }, renderEmpty: () => { }, toast: () => { },
        renderError: (container, opts) => spy.errors.push(opts),
        createSeqGuard: () => { let latest = 0; return { next: () => ++latest, isCurrent: seq => seq === latest }; },
        confirmAction: async () => { spy.confirms++; return true; },
        withBusy: () => () => { },
        bindTabs: () => { }, checkboxList: () => { }, button: () => dom.newElement('button'),
        guardLoad: () => { },
        renderPagination: (container, opts) => spy.pagers.set(container, opts),
        formatNumber: n => String(n),
        roleText: (cls, text) => { const n = dom.newElement('span'); n.textContent = text; return n; },
        statusNote: (type, text) => { const n = dom.newElement('div'); n.textContent = text; return n; }
    };
    const names = Object.keys(stubs);
    const module = new Function(...names, `${body}\nreturn { openMembersModal, loadCurrentMembers };`)(...names.map(n => stubs[n]));
    return { ...module, el, spy };
}

const group = { groupId: 7, groupName: '資料庫群', hostCount: 3 };
const member = (hostId, ip) => ({ hostId, hostName: `h${hostId}`, ipAddress: ip, otherGroupCount: 1 });
const pageOf = (page, items) => ({ items, page, pageSize: 50, total: 120 });
const checkbox = (container, hostId) =>
    container.find(n => n.dataset?.hostId === String(hostId) && n.className.includes('lf-current-member'));
const tickBox = (container, hostId) => {
    const box = checkbox(container, hostId);
    assert.ok(box, `找不到主機 ${hostId} 的勾選框`);
    box.checked = true;
    box.dispatch('change');
};

const cases = {
    // 目前成員載入失敗：顯示錯誤與重試（不是卡在骨架列）；重試成功後畫出成員
    async currentMembersLoadFailureShowsRetry() {
        const failPages = new Set([1]);
        const pages = { 1: { items: [{ hostId: 1, hostName: 'A', ipAddress: '10.0.0.1', otherGroupCount: 0 }], total: 1, page: 1, pageSize: 50 } };
        const { loadCurrentMembers, spy } = load({ pages, failPages });
        await loadCurrentMembers(7, 1);
        assert.equal(spy.errors.length, 1, '失敗要顯示錯誤');
        assert.equal(typeof spy.errors[0].onRetry, 'function', '要有重試');
        failPages.clear();
        await spy.errors[0].onRetry();
        assert.equal(spy.errors.length, 1, '重試成功不再顯示錯誤');
    },

    // 連點換頁：先發的第 1 頁晚回來，不得覆蓋後發的第 2 頁（失敗也不得蓋掉）
    async staleCurrentMembersResponseIsDropped() {
        let release;
        const slow = new Promise(resolve => { release = resolve; });
        const pages = {
            1: { items: [], total: 60, page: 1, pageSize: 50 },
            2: { items: [{ hostId: 52, hostName: 'B', ipAddress: '10.0.1.2', otherGroupCount: 0 }], total: 60, page: 2, pageSize: 50 }
        };
        const { loadCurrentMembers, spy } = load({ pages, failPages: new Set([1]), delays: { 1: slow } });
        const first = loadCurrentMembers(7, 1);
        await loadCurrentMembers(7, 2);
        release();
        await first;
        assert.equal(spy.errors.length, 0, '過期的失敗回應不得蓋掉較新的結果');
    },

    async currentMembersSelectionSurvivesPaging() {
        const { openMembersModal, el, spy } = load({
            pages: {
                1: pageOf(1, [member(1, '10.0.0.1'), member(2, '10.0.0.2'), member(3, '10.0.0.3')]),
                2: pageOf(2, [member(51, '10.0.1.1'), member(52, '10.0.1.2')])
            }
        });
        openMembersModal(group);
        await settle();
        const list = el('#current-members-list');
        const summary = el('#current-members-summary');
        const pager = () => spy.pagers.get(el('#current-members-pager'));

        tickBox(list, 1);
        tickBox(list, 3);
        assert.ok(summary.textContent.includes('已選 2 台'), summary.textContent);

        pager().onPage(2);
        await settle();
        assert.ok(checkbox(list, 51), '已換到第 2 頁');
        assert.ok(summary.textContent.includes('已選 2 台'), `換頁後勾選不該消失：${summary.textContent}`);
        tickBox(list, 52);
        assert.ok(summary.textContent.includes('已選 3 台'), summary.textContent);

        // 回到第 1 頁：先前勾的仍是勾選狀態、沒勾的不是
        pager().onPage(1);
        await settle();
        assert.equal(checkbox(list, 1).checked, true);
        assert.equal(checkbox(list, 2).checked, false);
        assert.equal(checkbox(list, 3).checked, true);
        assert.ok(summary.textContent.includes('已選 3 台'), summary.textContent);

        // 移出：送出的正好是跨頁勾的 3 台
        assert.equal(el('#current-members-remove').disabled, false);
        await el('#current-members-remove').listeners.click[0]();
        const remove = spy.posts.find(p => p.url.endsWith('/members/remove'));
        assert.ok(remove, '沒有送出移出請求');
        assert.equal(remove.url, '/api/admin/host-groups/7/members/remove');
        assert.deepEqual([...remove.body.hostIds].sort((a, b) => a - b), [1, 3, 52]);
        assert.equal(spy.confirms, 1);
    },

    async previewWithZeroMatchesDisablesBothApplyButtons() {
        const candidates = [
            { hostId: 11, hostName: 'a', ipAddress: '10.0.0.11', alreadyInTarget: false, currentGroups: [] },
            { hostId: 12, hostName: 'b', ipAddress: '10.0.0.12', alreadyInTarget: false, currentGroups: [] }
        ];
        const { openMembersModal, el } = load({
            pages: { 1: pageOf(1, []) },
            previewResult: body => body.pattern === '10.0.0.0/24'
                ? { matchCount: 2, alreadyInTargetCount: 0, inOtherGroupsCount: 0, candidates, page: 1, pageSize: 50 }
                : { matchCount: 0, alreadyInTargetCount: 0, inOtherGroupsCount: 0, candidates: [], page: 1, pageSize: 50 }
        });
        openMembersModal(group);
        await settle();
        const input = el('#members-input');
        const applyBtn = el('#members-apply');
        const allBtn = el('#members-apply-all');
        const summary = el('#members-summary');
        const search = () => el('#members-search').listeners.click[0]();

        // 先命中 2 台：「全部加入」可按（確認按鈕不是天生 disabled）
        input.value = '10.0.0.0/24';
        await search();
        assert.equal(allBtn.disabled, false, '命中 2 台時「全部加入」要可按');
        assert.ok(allBtn.textContent.includes('2 台'), allBtn.textContent);

        // 改查 0 台：兩顆都 disabled，並有說明
        input.value = '172.16.0.0/24';
        await search();
        assert.equal(applyBtn.disabled, true, '加入勾選的');
        assert.equal(allBtn.disabled, true, '全部加入符合條件的');
        assert.ok(summary.textContent.includes('沒有命中任何主機'), summary.textContent);
        assert.ok(allBtn.textContent.includes('0 台'), allBtn.textContent);
    }
};

const name = process.argv[2];
if (!name) {
    Object.keys(cases).forEach(key => console.log(key));
    process.exit(0);
}
if (!cases[name]) {
    console.error(`未知案例：${name}`);
    process.exit(2);
}
await cases[name]();
console.log(`PASS ${name}`);
