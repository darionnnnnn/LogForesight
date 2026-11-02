/**
 * core/reveal.js revealFirstRow 與 dashboard.js 待處理組順序的行為測試。
 * 量測、observer、storage、事件來源全部替身：這裡守判定，不守瀏覽器版面（版面由實測驗收）。
 */
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { installDom } from './mini-dom.mjs';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(scriptDir, '..', '..');
const jsRoot = path.join(repoRoot, 'LogForesight.Web', 'wwwroot', 'js');

const dom = installDom();
const { revealFirstRow, countGridColumns, FONT_SCALE_EVENT } = await import(pathToFileURL(path.join(jsRoot, 'core', 'reveal.js')).href);

function makeGrid(count, alertAt = []) {
    const grid = dom.newElement('div');
    for (let i = 0; i < count; i++) {
        const card = dom.newElement('div');
        card.alert = alertAt.includes(i);
        grid.appendChild(card);
    }
    return grid;
}

function setup({ grids, columns, stored = null, failOnSet = false }) {
    const state = { columns, disconnected: 0 };
    const data = { 'lf.collapse.k': stored };
    const storage = {
        get: key => data[key] ?? null,
        set: (key, value) => { if (!failOnSet) data[key] = value; }
    };
    const handlers = new Set();
    const eventTarget = {
        addEventListener: (type, handler) => { assert.equal(type, FONT_SCALE_EVENT); handlers.add(handler); },
        removeEventListener: (_type, handler) => handlers.delete(handler)
    };
    let observerCallback = null;
    const host = dom.newElement('div');
    const reveal = revealFirstRow({
        grids,
        host,
        key: 'k',
        noun: '項指標',
        isAlert: child => child.alert,
        measure: grid => state.columns.get(grid),
        createObserver: callback => {
            observerCallback = callback;
            return { observe() {}, disconnect: () => { state.disconnected++; } };
        },
        storage,
        eventTarget
    });
    return {
        reveal, host, data, state, handlers,
        toggle: host.find(node => node.tagName === 'BUTTON'),
        bar: host.children[0],
        resize: () => observerCallback(),
        fontScale: () => [...handlers].forEach(handler => handler())
    };
}

const hiddenFlags = grid => grid.children.map(child => child.hidden === true);

const cases = {
    collapsesBeyondFirstRow() {
        const grid = makeGrid(4);
        const { toggle } = setup({ grids: [grid], columns: new Map([[grid, 2]]) });
        assert.deepEqual(hiddenFlags(grid), [false, false, true, true]);
        assert.equal(toggle.textContent, '顯示其餘 2 項指標');
        assert.equal(toggle.getAttribute('aria-expanded'), 'false');
        assert.equal(toggle.type, 'button');
    },

    emptyGridsMountNothing() {
        const host = dom.newElement('div');
        const reveal = revealFirstRow({ grids: [], host, key: 'k', noun: '項指標', measure: () => 0, createObserver: () => { throw new Error('不該建 observer'); }, storage: { get: () => null, set() {} }, eventTarget: { addEventListener() { throw new Error('不該掛事件'); }, removeEventListener() {} } });
        assert.equal(host.children.length, 0);
        reveal.dispose();
    },

    zeroColumnsMeansNotLaidOutSoNothingHidden() {
        const grid = makeGrid(4);
        const { bar } = setup({ grids: [grid], columns: new Map([[grid, 0]]) });
        assert.deepEqual(hiddenFlags(grid), [false, false, false, false]);
        assert.equal(bar.hidden, true);
    },

    noButtonWhenEverythingFits() {
        const grid = makeGrid(3);
        const { bar } = setup({ grids: [grid], columns: new Map([[grid, 3]]) });
        assert.deepEqual(hiddenFlags(grid), [false, false, false]);
        assert.equal(bar.hidden, true);
    },

    sharedButtonSpansAllGridsAndCountsAlerts() {
        const a = makeGrid(4, [3]);
        const b = makeGrid(2, [1]);
        const { toggle } = setup({ grids: [a, b], columns: new Map([[a, 2], [b, 1]]) });
        assert.deepEqual(hiddenFlags(a), [false, false, true, true]);
        assert.deepEqual(hiddenFlags(b), [false, true]);
        assert.equal(toggle.textContent, '顯示其餘 3 項指標（2 項告警）');
        assert.equal(toggle.getAttribute('aria-controls'), `${a.id} ${b.id}`);
    },

    toggleExpandsAndRemembers() {
        const grid = makeGrid(4);
        const { toggle, data } = setup({ grids: [grid], columns: new Map([[grid, 2]]) });
        toggle.dispatch('click');
        assert.deepEqual(hiddenFlags(grid), [false, false, false, false]);
        assert.equal(toggle.textContent, '收合');
        assert.equal(toggle.getAttribute('aria-expanded'), 'true');
        assert.equal(data['lf.collapse.k'], 'open');
        toggle.dispatch('click');
        assert.deepEqual(hiddenFlags(grid), [false, false, true, true]);
        assert.equal(data['lf.collapse.k'], 'closed');
    },

    rememberedOpenStartsExpanded() {
        const grid = makeGrid(4);
        const { toggle } = setup({ grids: [grid], columns: new Map([[grid, 2]]), stored: 'open' });
        assert.deepEqual(hiddenFlags(grid), [false, false, false, false]);
        assert.equal(toggle.textContent, '收合');
    },

    storageFailureStillToggles() {
        const grid = makeGrid(4);
        const { toggle } = setup({ grids: [grid], columns: new Map([[grid, 2]]), failOnSet: true });
        toggle.dispatch('click');
        assert.equal(toggle.getAttribute('aria-expanded'), 'true');
    },

    resizeRecomputesAndUnhides() {
        const grid = makeGrid(4);
        const ctx = setup({ grids: [grid], columns: new Map([[grid, 2]]) });
        ctx.state.columns.set(grid, 4);
        ctx.resize();
        assert.deepEqual(hiddenFlags(grid), [false, false, false, false]);
        assert.equal(ctx.bar.hidden, true);
    },

    fontScaleEventRecomputes() {
        const grid = makeGrid(4);
        const ctx = setup({ grids: [grid], columns: new Map([[grid, 2]]) });
        ctx.state.columns.set(grid, 1);
        ctx.fontScale();
        assert.deepEqual(hiddenFlags(grid), [false, true, true, true]);
    },

    disposeReleasesObserverListenerAndBar() {
        const grid = makeGrid(4);
        const ctx = setup({ grids: [grid], columns: new Map([[grid, 2]]) });
        ctx.reveal.dispose();
        assert.equal(ctx.state.disconnected, 1);
        assert.equal(ctx.handlers.size, 0);
        assert.equal(ctx.host.children.length, 0);
        // dispose 後才到的 observer 回呼不得再動畫面
        ctx.state.columns.set(grid, 1);
        ctx.resize();
        assert.deepEqual(hiddenFlags(grid), [false, false, true, true]);
    },

    countsTracksIncludingCollapsedZeroTracks() {
        const styleFor = value => { globalThis.getComputedStyle = () => ({ gridTemplateColumns: value }); };
        styleFor('none');
        assert.equal(countGridColumns({}), 0);
        styleFor('224px 224px');
        assert.equal(countGridColumns({}), 2);
        styleFor('400px 400px 0px 0px');
        assert.equal(countGridColumns({}), 4);
    },

    todoGroupOrder() {
        const source = fs.readFileSync(path.join(jsRoot, 'pages', 'dashboard.js'), 'utf8').replace(/^﻿/, '').replaceAll('\r\n', '\n');
        const body = source.slice(source.indexOf('function renderKpi('), source.indexOf('function mountKpiReveal('));
        let captured = null;
        const render = new Function(
            'hasCapability', 'recordsUrl', 'formatNumber', 'kpiStrip', 'document', 'helpIcon', 'mountKpiReveal', 'DAY_RISK_HELP',
            `${body}\nreturn renderKpi;`)(
            (user, capability) => user.capabilities.includes(capability),
            () => '/records',
            value => String(value),
            groups => { captured = groups; return dom.newElement('div'); },
            { getElementById: () => dom.newElement('div') },
            () => dom.newElement('button'),
            () => {},
            'help');
        const data = {
            highRiskDays: 1, mediumRiskDays: 1, totalHosts: 1, coverageGapDays: 0,
            issueTodo: { affectedHostCount: 1, overdueIssueCount: 0, openIssueCount: 1 },
            todo: { openCount: 1, inProgressCount: 0 },
            silentHostsCount: 0, silentHostsPrtgDownCount: 0,
            pendingPermissionChanges: 0, recentLoginFailures: null
        };
        const badge = { activeWorkOrders: 1, activeMembers: 1, overdueMembers: 0, unrepliedWorkOrders: 0 };
        const display = { visibleDayRiskLevels: ['高', '中'] };
        const user = { capabilities: ['Handle'], userId: 1 };
        const todoCards = () => captured.find(group => group.title === '待處理').cards;

        render({ ...data, pendingDispatch: { issueCount: 2, unassignableIssueCount: 0 } }, user, display, badge);
        assert.deepEqual(todoCards().map(card => card.label), ['未處理問題', '待派工問題', '我的交辦單', '未回報主機']);

        render({ ...data, pendingDispatch: null }, user, display, badge);
        assert.deepEqual(todoCards().map(card => card.label), ['未處理問題', '我的交辦單', '未回報主機']);

        const silent = todoCards().at(-1);
        assert.equal(silent.title, '沒回報 ≠ 沒問題');
        assert.ok(!silent.sub, '沒有 PRTG 失聯時副標留白');

        render({ ...data, pendingDispatch: null, silentHostsPrtgDownCount: 3 }, user, display, badge);
        assert.equal(todoCards().at(-1).sub, '其中 3 台有 PRTG 監測項目異常');
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
