﻿/**
 * records.js search() 的競態與失敗態行為測試（第 59 輪 G2）。
 *
 * 抽出並執行 production 的 search，搭配 production 的 createSeqGuard，只替換 api、畫面邊界；
 * 由 RecordsSearchBehaviorTests 以 `node records-search.behavior.mjs <案例名>` 逐案執行。
 */

import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { installDom } from './mini-dom.mjs';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const jsRoot = path.join(scriptDir, '..', '..', 'LogForesight.Web', 'wwwroot', 'js');
const source = fs.readFileSync(path.join(jsRoot, 'pages', 'records.js'), 'utf8').replaceAll('\r\n', '\n');
const { createSeqGuard } = await import(pathToFileURL(path.join(jsRoot, 'core', 'async-guards.js')).href);

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

function makeContext() {
    const pending = [];
    const state = { rendered: [], errors: [], loading: 0 };
    const api = {
        get: (url) => new Promise((resolve, reject) => pending.push({ url, resolve, reject }))
    };
    const factory = new Function('api', 'createSeqGuard', 'renderError', 'renderLoading', 'onRender', `
        let currentView = 'detail';
        let currentPage = 1;
        let lastResult = null;
        const listContainer = { id: 'list' };
        const ENDPOINT = { detail: '/api/records', issue: '/api/records/by-issue' };
        const history = { replaceState() {} };
        const location = { pathname: '/records' };
        const document = { getElementById: () => ({ replaceChildren() {} }) };
        const collectFilters = () => ({});
        const buildQueryString = () => 'q=1';
        const renderActiveConditions = () => {};
        const render = () => onRender(currentView, lastResult);
        const searchGuard = createSeqGuard();
        ${extractFunction('async function search')}
        return { search, setView: v => { currentView = v; }, getResult: () => lastResult };
    `);
    const exported = factory(api, createSeqGuard,
        (container, opts) => state.errors.push(opts),
        () => { state.loading++; },
        (view, result) => state.rendered.push({ view, result }));
    return { ...exported, state, pending };
}

function extractConst(declaration) {
    const start = source.indexOf(declaration);
    assert(start >= 0, `找不到 production 常數：${declaration}`);
    const end = source.indexOf('\n};', start);
    assert(end >= 0, `production 常數未閉合：${declaration}`);
    return source.slice(start, end + 3);
}

/** 條件列／查詢字串：執行 production 的 viewSupports、buildQueryString、renderActiveConditions */
function makeFilterContext() {
    const dom = installDom();
    const children = [];
    const classes = new Set(['d-none']);
    const container = {
        children,
        classList: { toggle: (name, force) => { if (force) classes.add(name); else classes.delete(name); } },
        replaceChildren() { children.length = 0; },
        appendChild(node) { children.push(node); }
    };
    dom.byId.set('active-conditions', container);
    const factory = new Function('document', 'severityName', 'search', 'history', 'location', `
        let currentView = 'detail';
        let currentPage = 1;
        const sort = { key: '', dir: 'desc' };
        const pageSize = 20;
        ${extractConst('const VIEW_FILTER_SUPPORT')}
        ${extractFunction('function viewSupports')}
        ${extractFunction('function buildQueryString')}
        ${extractFunction('function renderActiveConditions')}
        return {
            setView: v => { currentView = v; },
            viewSupports, buildQueryString, renderActiveConditions
        };
    `);
    const api = factory(globalThis.document, v => v, () => {}, { replaceState() {} }, { search: '' });
    const hint = () => children.find(c => c.getAttribute?.('data-role') === 'unsupported-filters-hint') ?? null;
    const tagLabels = () => children.filter(c => c !== hint()).map(c => c.textContent);
    return { ...api, hint, tagLabels, isHidden: () => classes.has('d-none') };
}

const baseFilters = () => ({
    hostIds: [], groupIds: [], riskLevels: ['高'], categories: [], from: '', to: '', eventId: '',
    severity: '', statuses: 'open', overdue: 'true', unassigned: true, source: ''
});

const tick = () => new Promise(r => setTimeout(r, 0));

const cases = {
    async 舊回應晚到不渲染() {
        const ctx = makeContext();
        const first = ctx.search();
        const second = ctx.search();
        ctx.pending[1].resolve({ tag: 'new' });
        await second;
        ctx.pending[0].resolve({ tag: 'old' });
        await first;
        assert.equal(ctx.state.rendered.length, 1, '舊回應不得再渲染');
        assert.equal(ctx.state.rendered[0].result.tag, 'new');
        assert.equal(ctx.getResult().tag, 'new', '舊回應不得蓋掉新結果');
    },
    async 視角切換後舊回應不進render() {
        const ctx = makeContext();
        const issueSearch = ctx.search();   // 依問題視角（慢）
        // 視角切換：先改 currentView 再 search
        ctx.setView('detail');
        const detailSearch = ctx.search();
        ctx.pending[1].resolve({ tag: 'detail-shape' });
        await detailSearch;
        ctx.pending[0].resolve({ tag: 'issue-shape' });
        await issueSearch;
        assert.deepEqual(ctx.state.rendered.map(r => r.result.tag), ['detail-shape']);
    },
    async 同序號但視角已變也丟棄() {
        const ctx = makeContext();
        const run = ctx.search();
        ctx.setView('issue');   // 沒有重新 search 就換了視角
        ctx.pending[0].resolve({ tag: 'detail-shape' });
        await run;
        assert.equal(ctx.state.rendered.length, 0, '視角已變的回應不得渲染');
    },
    async api擲錯呼叫renderError() {
        const ctx = makeContext();
        const run = ctx.search();
        ctx.pending[0].reject(new Error('boom'));
        await run;
        await tick();
        assert.equal(ctx.state.errors.length, 1, '失敗必須顯示失敗態');
        assert.equal(typeof ctx.state.errors[0].onRetry, 'function', '失敗態必須提供重試');
        assert.equal(ctx.state.rendered.length, 0);
    },
    async 舊請求失敗不蓋掉新結果() {
        const ctx = makeContext();
        const first = ctx.search();
        const second = ctx.search();
        ctx.pending[1].resolve({ tag: 'new' });
        await second;
        ctx.pending[0].reject(new Error('late boom'));
        await first;
        assert.equal(ctx.state.errors.length, 0, '過期請求的失敗不得蓋掉新結果');
    },
    async 依主機視角不送逾期不顯示標籤且提示被略過() {
        const ctx = makeFilterContext();
        const filters = baseFilters();
        ctx.setView('host');

        const query = new URLSearchParams(ctx.buildQueryString(filters, 1));
        ctx.renderActiveConditions(filters);

        assert.equal(query.has('overdue'), false, '依主機視角不得送出 overdue');
        assert.equal(query.has('unassigned'), false);
        assert.equal(query.has('statuses'), false);
        assert.deepEqual(ctx.tagLabels(), [], '不支援的條件不得顯示可移除標籤');
        assert(ctx.hint(), '要有提示元素');
        assert(ctx.hint().textContent.includes('只看逾期'), ctx.hint().textContent);
        assert.equal(ctx.isHidden(), false);

        // 網址同步保留全部條件，切回支援的視角時才能恢復
        const kept = new URLSearchParams(ctx.buildQueryString(filters, 1, { keepUnsupported: true }));
        assert.equal(kept.get('overdue'), 'true');
        assert.equal(kept.get('unassigned'), 'true');
        assert.equal(kept.get('statuses'), 'open');
    },
    async 切回明細恢復逾期與標籤且提示消失() {
        const ctx = makeFilterContext();
        const filters = baseFilters();
        ctx.setView('host');
        ctx.renderActiveConditions(filters);
        assert(ctx.hint());

        ctx.setView('detail');
        const query = new URLSearchParams(ctx.buildQueryString(filters, 1));
        ctx.renderActiveConditions(filters);

        assert.equal(query.get('overdue'), 'true');
        assert.equal(query.get('statuses'), 'open');
        assert.equal(query.get('unassigned'), 'true');
        assert.deepEqual(ctx.tagLabels(), ['只看逾期', '僅未指派']);
        assert.equal(ctx.hint(), null, '全部支援時不得留著提示');
    },
    async 依問題視角只略過逾期() {
        const ctx = makeFilterContext();
        const filters = baseFilters();
        ctx.setView('issue');

        const query = new URLSearchParams(ctx.buildQueryString(filters, 1));
        ctx.renderActiveConditions(filters);

        assert.equal(query.has('overdue'), false);
        assert.equal(query.get('statuses'), 'open');
        assert.equal(query.get('unassigned'), 'true');
        assert.deepEqual(ctx.tagLabels(), [], '依問題視角的未指派由 chip 呈現，不另出標籤');
        const text = ctx.hint().textContent;
        assert(text.includes('只看逾期'));
        assert(!text.includes('僅未指派'), '只列實際被略過的條件');
    },
    async 支援矩陣與後端一致() {
        const ctx = makeFilterContext();
        const expected = {
            detail: { statuses: true, overdue: true, unassigned: true },
            issue: { statuses: true, overdue: false, unassigned: true },
            host: { statuses: false, overdue: false, unassigned: false },
            date: { statuses: false, overdue: false, unassigned: false }
        };
        for (const [view, keys] of Object.entries(expected)) {
            for (const [key, want] of Object.entries(keys)) {
                assert.equal(ctx.viewSupports(view, key), want, `${view}/${key}`);
            }
        }
    },
};

const name = process.argv[2];
const fn = cases[name];
assert(fn, `未知案例：${name}`);
await fn();
console.log(`PASS ${name}`);
process.exit(0);
