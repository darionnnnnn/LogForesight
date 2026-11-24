/**
 * 我的交辦空狀態（handler-detail.js renderOrdersEmpty）三分支：沒授權主機／可見範圍有未處理問題／真的沒事，
 * 另守：只在本人頁＋進行中＋沒勾暫停才補查、過期回應不寫回、取不到或 0 個時維持原文。
 * 切出函式本體、注入替身執行。
 */
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { installDom } from './mini-dom.mjs';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const jsRoot = path.resolve(scriptDir, '..', '..', 'LogForesight.Web', 'wwwroot', 'js');
const dom = installDom();

const src = fs.readFileSync(path.join(jsRoot, 'pages', 'handler-detail.js'), 'utf8').replaceAll('\r\n', '\n').replace(/^﻿+/, '');
const extract = decl => {
    const start = src.indexOf(decl);
    assert(start >= 0, `找不到 ${decl}`);
    const open = src.indexOf('{', src.indexOf(')', start));
    let depth = 0;
    for (let i = open; i < src.length; i++) {
        if (src[i] === '{') depth++;
        else if (src[i] === '}' && --depth === 0) return src.slice(start, i + 1);
    }
    throw new Error(`未閉合 ${decl}`);
};

const tick = () => new Promise(resolve => setImmediate(resolve));
const settle = async () => { for (let i = 0; i < 5; i++) await tick(); };

function load({ isSelf = true, status = 'active', paused = false, visibleHostCount = 5, summary = null, failSummary = false, stored = null }) {
    const spy = { gets: [], empties: [] };
    const ordersEl = { firstElementChild: null };
    const stubs = {
        document,
        ordersEl,
        statusSelect: { value: status },
        pausedCheckbox: { checked: paused },
        renderEmpty: (container, { title, hint }) => {
            const el = dom.newElement('div');
            el.titleText = title; el.hintText = hint;
            container.firstElementChild = el;
            spy.empties.push({ title, hint });
        },
        appUrl: p => `/lf${p}`,
        recordsUrl: filters => `/records?${new URLSearchParams(filters)}`,
        formatNumber: n => String(n),
        safeStorage: { get: () => stored },
        api: {
            get: async (url, opts) => {
                spy.gets.push({ url, opts });
                if (failSummary) throw new Error('boom');
                return typeof summary === 'function' ? summary() : summary;
            }
        }
    };
    const names = Object.keys(stubs);
    const body = `
        let isSelf = ${isSelf};
        let summary = { visibleHostCount: ${visibleHostCount} };
        ${extract('function renderOrdersEmpty')}
        ${extract('async function showVisibleIssuesInEmpty')}
        const bump = () => { emptyHintGuard.next(); };
        return { renderOrdersEmpty, bump };`;
    // emptyHintGuard 是模組層 const：與 production 同一份宣告（createSeqGuard 換成替身）
    const decl = src.match(/^const emptyHintGuard = createSeqGuard\(\);.*$/m);
    assert(decl, '找不到 const emptyHintGuard 宣告');
    const fns = new Function(...names, 'createSeqGuard', decl[0] + '\n' + body)(...names.map(n => stubs[n]),
        () => { let latest = 0; return { next: () => ++latest, isCurrent: seq => seq === latest }; });
    return { ...fns, spy, ordersEl };
}

const todo = (open, hosts) => ({ from: '2026-11-18', to: '2026-11-24', issueTodo: { openIssueCount: open, affectedHostCount: hosts } });
const linkOf = ordersEl => ordersEl.firstElementChild.children.at(-1).children[0];

const cases = {
    // 分支一：沒授權任何主機——不補查
    async noAuthorizedHostsDoesNotFetch() {
        const { renderOrdersEmpty, spy } = load({ visibleHostCount: 0, summary: todo(9, 3) });
        renderOrdersEmpty();
        await settle();
        assert.equal(spy.empties.at(-1).title, '尚未被授權任何主機');
        assert.equal(spy.gets.length, 0);
    },

    // 分支二：可見範圍有未處理問題——改寫成 N 個、影響 M 台，連到問題查詢（同儀表板口徑的篩選）
    async openIssuesRewriteTitleAndLink() {
        const { renderOrdersEmpty, spy, ordersEl } = load({ summary: todo(12, 4) });
        renderOrdersEmpty();
        assert.equal(spy.empties.at(-1).title, '目前沒有被指派的工作', '先畫原文，補查回來再改寫');
        await settle();

        assert.equal(spy.empties.at(-1).title, '你可以看到的主機目前有 12 個未處理問題（影響 4 台）');
        const href = linkOf(ordersEl).href;
        assert.ok(href.startsWith('/lf/records?'), href);
        const q = new URLSearchParams(href.split('?')[1]);
        assert.equal(q.get('view'), 'issue');
        assert.equal(q.get('statuses'), 'open');
        assert.equal(spy.gets[0].url, '/api/dashboard/summary?days=7');
        assert.equal(spy.gets[0].opts.silent, true, '補充資訊失敗不跳錯誤');
    },

    // 分支三：真的沒事（0 個未處理問題）——維持原文與「前往總覽儀表板」
    async zeroOpenIssuesKeepsOriginalText() {
        const { renderOrdersEmpty, spy, ordersEl } = load({ summary: todo(0, 0) });
        renderOrdersEmpty();
        await settle();
        assert.equal(spy.empties.length, 1, '不重畫');
        assert.equal(spy.empties[0].title, '目前沒有被指派的工作');
        assert.equal(linkOf(ordersEl).textContent, '前往總覽儀表板');
    },

    // 邊界：剛好 1 個也要改寫（0 與 1 兩側）
    async exactlyOneOpenIssueRewrites() {
        const { renderOrdersEmpty, spy } = load({ summary: todo(1, 1) });
        renderOrdersEmpty();
        await settle();
        assert.equal(spy.empties.at(-1).title, '你可以看到的主機目前有 1 個未處理問題（影響 1 台）');
    },

    // 取不到摘要：維持原文、不擲例外
    async fetchFailureKeepsOriginalText() {
        const { renderOrdersEmpty, spy } = load({ failSummary: true });
        renderOrdersEmpty();
        await settle();
        assert.equal(spy.empties.length, 1);
        assert.equal(spy.empties[0].title, '目前沒有被指派的工作');
    },

    // 補查回來前又畫了新的一頁：過期回應不得寫回
    async staleResponseIsDropped() {
        const { renderOrdersEmpty, bump, spy } = load({ summary: todo(7, 2) });
        renderOrdersEmpty();
        bump();   // renderOrders 重畫（seq 前進）
        await settle();
        assert.equal(spy.empties.length, 1, '過期回應不改寫');
    },

    // 非本人頁／非進行中／勾只看暫停：沿用一般文字、不補查
    async otherFiltersUseGenericTextAndDoNotFetch() {
        for (const opts of [{ isSelf: false }, { status: 'closed' }, { paused: true }]) {
            const { renderOrdersEmpty, spy } = load({ ...opts, summary: todo(9, 3) });
            renderOrdersEmpty();
            await settle();
            assert.equal(spy.empties[0].title, '目前沒有符合條件的交辦單');
            assert.equal(spy.gets.length, 0);
        }
    },

    // 期間沿用儀表板的設定（safeStorage lf.dashboard.days）
    async usesDashboardDaysSetting() {
        const { renderOrdersEmpty, spy } = load({ summary: todo(1, 1), stored: '30' });
        renderOrdersEmpty();
        await settle();
        assert.equal(spy.gets[0].url, '/api/dashboard/summary?days=30');
    }
};

const name = process.argv[2];
if (!name) { Object.keys(cases).forEach(key => console.log(key)); process.exit(0); }
if (!cases[name]) { console.error(`未知案例：${name}`); process.exit(2); }
await cases[name]();
console.log(`PASS ${name}`);
