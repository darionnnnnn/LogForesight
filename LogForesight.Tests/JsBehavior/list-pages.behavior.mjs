/**
 * 清單頁（audit／hosts／permission-changes／reports／dashboard）查詢函式的競態與失敗態行為測試（第 59 輪 G3）。
 *
 * 從 production 原始碼抽出各頁的查詢函式並執行，搭配 production 的 createSeqGuard，
 * 只替換 api 與畫面邊界；由 ListPagesBehaviorTests 以 `node list-pages.behavior.mjs <案例名>` 逐案執行。
 */

import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const jsRoot = path.join(scriptDir, '..', '..', 'LogForesight.Web', 'wwwroot', 'js');
const { createSeqGuard } = await import(pathToFileURL(path.join(jsRoot, 'core', 'async-guards.js')).href);

function sourceOf(page) {
    return fs.readFileSync(path.join(jsRoot, 'pages', page), 'utf8').replaceAll('\r\n', '\n');
}

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

/** 可手動決定每個請求何時回應的 api 替身 */
function makeApi() {
    const pending = [];
    return {
        pending,
        // 儀表板的「我的交辦」數字是附帶請求，直接回 null，不參與序號競態
        get: (url) => url.includes('/handlers/me/badge')
            ? Promise.resolve(null)
            : new Promise((resolve, reject) => pending.push({ url, resolve, reject }))
    };
}

const tick = () => new Promise((r) => setTimeout(r, 0));

/** 以 production 函式原文建立可執行實例：prelude 提供該函式用到的外部變數與替身 */
function build(page, declaration, fnName, prelude, args) {
    const source = sourceOf(page);
    const names = Object.keys(args);
    const factory = new Function(...names, `
        ${prelude}
        ${extractFunction(source, declaration)}
        return { ${fnName}, state: () => state };
    `);
    return factory(...names.map((n) => args[n]));
}

function makeAudit() {
    const api = makeApi();
    const rendered = [];
    const inst = build('audit.js', 'async function search(', 'search', `
        const state = {};
        let currentPage = 1;
        let pageSize = 20;
        let sort = { key: 'occurredAt', dir: 'desc' };
        let lastResult = null;
        const searchGuard = createSeqGuard();
        const document = { getElementById: () => ({ value: '', selectedOptions: [] }) };
        const render = () => onRender(lastResult);
    `, { api, createSeqGuard, renderLoading: () => {}, onRender: (r) => rendered.push(r) });
    return { run: inst.search, api, rendered };
}

function makeHosts() {
    const api = makeApi();
    const rendered = [];
    const inst = build('hosts.js', 'async function search(', 'search', `
        const state = {};
        let currentPage = 1;
        let pageSize = 20;
        let lastResult = null;
        let currentPageHosts = [];
        const listContainer = {};
        const currentFilterParams = () => new URLSearchParams();
        const searchGuard = createSeqGuard();
        const render = () => onRender(lastResult);
    `, { api, createSeqGuard, renderLoading: () => {}, onRender: (r) => rendered.push(r) });
    return { run: inst.search, api, rendered };
}

function makePerm(subnetValue = '') {
    const api = makeApi();
    const rendered = [];
    const subnetErrors = [];
    const inst = build('permission-changes.js', 'async function load(', 'load', `
        const state = {};
        let currentPage = 1;
        let pageSize = 20;
        let lastResult = { items: [{ changeId: 'keep' }], total: 1 };
        const selectedChanges = new Map();
        const subnetInput = { value: subnetValue };
        const loadGuard = createSeqGuard();
        const document = { getElementById: () => ({}) };
        const history = { replaceState() {} };
        const clearSubnetError = () => {};
        const saveStoredFilters = () => {};
        const buildQueryParams = () => new URLSearchParams();
        const renderCategoryChips = () => {};
        const toast = () => {};
        const render = () => onRender(lastResult);
    `, {
        api, createSeqGuard, renderLoading: () => {}, subnetValue,
        showSubnetError: (m) => subnetErrors.push(m),
        onRender: (r) => rendered.push(r)
    });
    return { run: inst.load, api, rendered, subnetErrors };
}

function makeReports() {
    const api = makeApi();
    const writes = [];
    const inst = build('reports.js', 'async function loadReport(', 'loadReport', `
        const state = {};
        let currentData = null;
        let retentionDays = 90;
        let currentScope = 'all';
        let requestedScope = null;
        let visibleDayRisk = null;
        const SCOPE_LABEL = {};
        const reportGuard = createSeqGuard();
        const document = { getElementById: () => ({ value: '', set textContent(v) { onWrite('title'); } }) };
        const location = { search: '', pathname: '/reports' };
        const history = { replaceState() { onWrite('url'); } };
        const updateYoyOptionLabel = () => onWrite('yoy');
        const renderKpi = () => onWrite('kpi');
        const renderVisibleCharts = () => onWrite('charts');
    `, { api, createSeqGuard, getDisplaySettings: async () => ({}), onWrite: (w) => writes.push(w) });
    return { run: inst.loadReport, api, writes };
}

function makeDashboard() {
    const api = makeApi();
    const writes = [];
    const inst = build('dashboard.js', 'async function load(', 'load', `
        const state = {};
        let currentDays = 7;
        const loadGuard = createSeqGuard();
        const document = { getElementById: () => ({ set textContent(v) { onWrite('range'); } }) };
        const renderBanner = () => onWrite('banner');
        const renderKpi = () => onWrite('kpi');
        const renderCategories = () => onWrite('categories');
        const renderTopIssues = () => onWrite('issues');
        const renderHosts = () => onWrite('hosts');
        const renderGroupRisk = () => onWrite('groupRisk');
        const renderServerAdminGuide = () => onWrite('guide');
        const loadAiFocus = () => onWrite('ai');
    `, {
        api, createSeqGuard, renderLoading: () => {},
        getCurrentUser: async () => ({ isServerAdmin: false }),
        getDisplaySettings: async () => ({}),
        onWrite: (w) => writes.push(w)
    });
    return { run: inst.load, api, writes };
}

const cases = {
    // ---- audit ----
    async audit舊回應晚到不渲染() {
        const c = makeAudit();
        const first = c.run();
        const second = c.run();
        c.api.pending[1].resolve({ tag: 'new' });
        await second;
        c.api.pending[0].resolve({ tag: 'old' });
        await first;
        assert.deepEqual(c.rendered.map((r) => r.tag), ['new']);
    },
    async audit舊請求失敗不外擲() {
        const c = makeAudit();
        const first = c.run();
        const second = c.run();
        c.api.pending[1].resolve({ tag: 'new' });
        await second;
        c.api.pending[0].reject(new Error('late boom'));
        await first;   // 擲出就會讓本案例失敗
        assert.equal(c.rendered.length, 1);
    },
    async audit最新請求失敗要外擲() {
        const c = makeAudit();
        const run = c.run();
        c.api.pending[0].reject(new Error('boom'));
        await assert.rejects(run, /boom/);
        assert.equal(c.rendered.length, 0);
    },

    // ---- hosts ----
    async hosts舊回應晚到不渲染() {
        const c = makeHosts();
        const first = c.run();
        const second = c.run();
        c.api.pending[1].resolve({ tag: 'new', items: [] });
        await second;
        c.api.pending[0].resolve({ tag: 'old', items: [] });
        await first;
        assert.deepEqual(c.rendered.map((r) => r.tag), ['new']);
    },
    async hosts舊請求失敗不外擲() {
        const c = makeHosts();
        const first = c.run();
        const second = c.run();
        c.api.pending[1].resolve({ tag: 'new', items: [] });
        await second;
        c.api.pending[0].reject(new Error('late boom'));
        await first;
        assert.equal(c.rendered.length, 1);
    },
    async hosts最新請求失敗要外擲() {
        const c = makeHosts();
        const run = c.run();
        c.api.pending[0].reject(new Error('boom'));
        await assert.rejects(run, /boom/);
        assert.equal(c.rendered.length, 0);
    },

    // ---- permission-changes ----
    async perm舊回應晚到不渲染() {
        const c = makePerm();
        const first = c.run();
        const second = c.run();
        c.api.pending[1].resolve({ tag: 'new', items: [], total: 0 });
        await second;
        c.api.pending[0].resolve({ tag: 'old', items: [], total: 0 });
        await first;
        assert.deepEqual(c.rendered.map((r) => r.tag), ['new']);
    },
    async perm一般錯誤不渲染空狀態() {
        const c = makePerm();
        const run = c.run();
        c.api.pending[0].reject(new Error('伺服器忙碌'));
        await assert.rejects(run, /伺服器忙碌/);
        assert.equal(c.rendered.length, 0, '一般錯誤不得渲染（否則會畫成「沒有待確認的權限異動」）');
    },
    async perm網段錯誤顯示欄位錯誤() {
        const c = makePerm('10.0.0.0/99');
        const run = c.run();
        const err = new Error('網段格式不正確');
        err.code = 'validation_failed';
        c.api.pending[0].reject(err);
        await run;
        assert.equal(c.subnetErrors.length, 1);
        assert.equal(c.rendered.length, 1);
    },
    async perm舊請求失敗不外擲() {
        const c = makePerm();
        const first = c.run();
        const second = c.run();
        c.api.pending[1].resolve({ tag: 'new', items: [], total: 0 });
        await second;
        c.api.pending[0].reject(new Error('late boom'));
        await first;
        assert.equal(c.rendered.length, 1);
    },

    // ---- reports ----
    async reports舊回應晚到不寫入狀態() {
        const c = makeReports();
        const first = c.run();
        const second = c.run();
        c.api.pending[1].resolve({ from: 'a', to: 'b' });
        await second;
        const afterNew = c.writes.length;
        assert(afterNew > 0, '最新回應應該渲染');
        c.api.pending[0].resolve({ from: 'x', to: 'y' });
        await first;
        assert.equal(c.writes.length, afterNew, '舊回應不得寫入任何頁面狀態');
    },
    async reports舊請求失敗不外擲() {
        const c = makeReports();
        const first = c.run();
        const second = c.run();
        c.api.pending[1].resolve({ from: 'a', to: 'b' });
        await second;
        c.api.pending[0].reject(new Error('late boom'));
        await first;
    },

    // ---- dashboard ----
    async dashboard舊回應晚到不寫入狀態() {
        const c = makeDashboard();
        const first = c.run();
        await tick();
        const second = c.run();
        await tick();
        c.api.pending[1].resolve({ from: 'a', to: 'b' });
        await second;
        const afterNew = c.writes.length;
        assert(afterNew > 0, '最新回應應該渲染');
        c.api.pending[0].resolve({ from: 'x', to: 'y' });
        await first;
        assert.equal(c.writes.length, afterNew, '舊回應不得寫入任何頁面狀態');
    },
};

const name = process.argv[2];
const fn = cases[name];
assert(fn, `未知案例：${name}`);
await fn();
console.log(`PASS ${name}`);
