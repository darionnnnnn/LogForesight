/**
 * records.js search() 的競態與失敗態行為測試（第 59 輪 G2）。
 *
 * 抽出並執行 production 的 search，搭配 production 的 createSeqGuard，只替換 api、畫面邊界；
 * 由 RecordsSearchBehaviorTests 以 `node records-search.behavior.mjs <案例名>` 逐案執行。
 */

import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

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
};

const name = process.argv[2];
const fn = cases[name];
assert(fn, `未知案例：${name}`);
await fn();
console.log(`PASS ${name}`);
process.exit(0);
