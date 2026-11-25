/**
 * AI 測試連線（批次 D：A1b／F22-2）：設定頁 AI 頁籤的「測試連線」與初始設定精靈的「儲存並測試」
 * 走同一個明確的同步探測端點（POST /api/admin/setup/ai-probe），不靠狀態端點取得新結果。
 * 切出 settings.js 的 bindAiTest／aiProbeStatusText 注入替身執行；精靈的呼叫順序以原始碼順序斷言。
 */
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const jsRoot = path.resolve(scriptDir, '..', '..', 'LogForesight.Web', 'wwwroot', 'js', 'pages');
const read = name => fs.readFileSync(path.join(jsRoot, name), 'utf8').replaceAll('\r\n', '\n');
const settingsSrc = read('settings.js');
const setupSrc = read('setup.js');

const extract = (src, decl) => {
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

function loadBindAiTest(post) {
    const spy = { posts: [], busy: [], restored: 0, notes: null };
    const listeners = {};
    const button = { addEventListener: (type, h) => { listeners[type] = h; } };
    const resultEl = { replaceChildren: (...nodes) => { spy.notes = nodes; } };
    const stubs = {
        document: { getElementById: id => id === 'ai-test-btn' ? button : id === 'ai-test-result' ? resultEl : null },
        api: { post: async (url, body, opts) => { spy.posts.push({ url, body, opts }); return post(); } },
        withBusy: (_btn, text) => { spy.busy.push(text); return () => { spy.restored++; }; },
        statusNote: (tone, text) => ({ tone, text })
    };
    const names = Object.keys(stubs);
    const fn = new Function(...names, `${extract(settingsSrc, 'function bindAiTest')}\nreturn bindAiTest;`)(...names.map(n => stubs[n]));
    fn();
    return { click: () => listeners.click(), spy };
}

function loadRecheck(initialStatus, responses) {
    const spy = { gets: [], renders: 0, progress: 0, polls: 0, finished: null };
    const queue = [...responses];
    const views = new Map();
    // 就地更新用的節點替身：badgeSlot 記錄最後放進去的徽章，detail／dot 記錄文字與樣式
    const makeView = () => ({
        dot: { style: { setProperty() { } } },
        badgeSlot: { child: null, replaceChildren(c) { this.child = c; } },
        detail: { textContent: '' }
    });
    const stubs = {
        api: { get: async (url, opts) => { spy.gets.push({ url, opts }); return queue.shift(); } },
        // pollWhile 替身：立刻連續執行到 fn 回傳 false
        pollWhile: (fn) => {
            spy.polls++;
            let stopped = false;
            spy.finished = (async () => { while (!stopped) { if ((await fn()) === false) break; } })();
            return { stop() { stopped = true; } };
        },
        render: () => { spy.renders++; },
        renderProgress: () => { spy.progress++; },
        statusBadge: (text, variant) => ({ text, variant })
    };
    const consts = ['const CHECKING_RECHECK_INTERVAL_MS', 'const CHECKING_RECHECK_MAX_ATTEMPTS', 'let recheckTimer']
        .map(prefix => setupSrc.split('\n').find(line => line.startsWith(prefix)) ?? assert.fail(`找不到 ${prefix}`)).join('\n');
    const metaStart = setupSrc.indexOf('const STEP_STATE_META = {');
    const metaBlock = setupSrc.slice(metaStart, setupSrc.indexOf('\n};', metaStart) + 3);
    const body = [extract(setupSrc, 'function stepState'), extract(setupSrc, 'function updateStepInPlace'),
        extract(setupSrc, 'function applyRecheckResult'), extract(setupSrc, 'function scheduleCheckingRecheck')].join('\n');
    const names = Object.keys(stubs);
    const code = `${consts}\n${metaBlock}\nlet status = initial; let recheckExhausted = false; let aiFormDirty = false; const stepViews = views;\n${body}\n`
        + 'return { schedule: scheduleCheckingRecheck, getStatus: () => status, setDirty: v => { aiFormDirty = v; }, exhausted: () => recheckExhausted };';
    const api = new Function('initial', 'views', ...names, code)(initialStatus, views, ...names.map(n => stubs[n]));
    views.set('ai', makeView());
    return { schedule: api.schedule, setDirty: api.setDirty, exhausted: api.exhausted, getStatus: api.getStatus, view: views.get('ai'), spy };
}

const cases = {
    async clickPostsExplicitProbeAndShowsSuccess() {
        const { click, spy } = loadBindAiTest(() => ({ isReady: true, status: 'Ready', detail: '連線正常' }));
        await click();
        assert.equal(spy.posts.length, 1);
        assert.equal(spy.posts[0].url, '/api/admin/setup/ai-probe');
        assert.deepEqual(spy.notes, [{ tone: 'success', text: '連線正常' }]);
        assert.equal(spy.restored, 1, '按鈕忙碌狀態要還原');
    },
    async failedProbeShowsError() {
        const { click, spy } = loadBindAiTest(() => ({ isReady: false, status: 'Failed', detail: 'AI 服務探活逾時' }));
        await click();
        assert.deepEqual(spy.notes, [{ tone: 'error', text: 'AI 服務探活逾時' }]);
    },
    async notConfiguredShowsWarningNotError() {
        const { click, spy } = loadBindAiTest(() => ({ isReady: false, status: 'NotConfigured', detail: '尚未設定 AI 服務位址' }));
        await click();
        assert.equal(spy.notes[0].tone, 'warning');
    },
    async requestFailureShowsMessageAndRestores() {
        const { click, spy } = loadBindAiTest(() => { throw new Error('伺服器忙碌'); });
        await click();
        assert.deepEqual(spy.notes, [{ tone: 'error', text: '伺服器忙碌' }]);
        assert.equal(spy.restored, 1);
    },
    // 精靈「儲存並測試」：先送明確探測、再讀狀態（狀態端點只回上次探測結果，不能拿來判斷剛存的設定）
    saveAndTestProbesBeforeReadingStatus() {
        const handlerStart = setupSrc.indexOf("withBusy(saveTestBtn, '儲存並測試中')");
        assert(handlerStart >= 0, '找不到儲存並測試流程');
        const flow = setupSrc.slice(handlerStart);
        const probeAt = flow.indexOf("api.post('/api/admin/setup/ai-probe'");
        const statusAt = flow.indexOf("api.get('/api/admin/setup/status')");
        const checkAt = flow.indexOf('checkAiProbeResult(newStatus)');
        assert(probeAt >= 0, '儲存並測試要送明確的探測請求');
        assert(statusAt > probeAt, '讀狀態要在探測之後');
        assert(checkAt > statusAt, '判定要用探測後的狀態');
    },
    // 精靈 AI 步驟「檢查中」：重抓有次數上限、請求標 background、取得結果後依結果更新並停止
    async checkingRecheckStopsWhenResultArrives() {
        const checking = { steps: [{ id: 'ai', checking: true }] };
        const { schedule, spy, getStatus } = loadRecheck(checking,
            [checking, { steps: [{ id: 'ai', checking: false, done: true }] }]);
        schedule();
        await spy.finished;
        assert.equal(spy.gets.length, 2);
        assert.equal(spy.renders, 1, '表單沒被動過：拿到結果整個重畫一次');
        assert.equal(getStatus().steps[0].done, true);
        for (const g of spy.gets) {
            assert.equal(g.url, '/api/admin/setup/status');
            assert.equal(g.opts.background, true, '背景輪詢要標 background');
        }
    },
    // 使用者已動過 AI 表單：結果回來不重畫（不清掉已輸入的值），只更新徽章與說明
    async checkingRecheckKeepsDirtyFormAndUpdatesBadgeOnly() {
        const checking = { steps: [{ id: 'ai', checking: true }] };
        const { schedule, setDirty, spy, view } = loadRecheck(checking,
            [{ steps: [{ id: 'ai', checking: false, done: false, detail: 'AI 服務探活失敗。' }] }]);
        setDirty(true);
        schedule();
        await spy.finished;
        assert.equal(spy.renders, 0, '動過表單不得整個重畫');
        assert.equal(spy.progress, 1);
        assert.equal(view.badgeSlot.child.text, '待設定');
        assert.equal(view.detail.textContent, 'AI 服務探活失敗。');
    },
    // 重抓到上限仍沒有結果：徽章改終態文字，不一直停在「檢查中」
    async checkingRecheckHasAttemptCap() {
        const always = { steps: [{ id: 'ai', checking: true, detail: '尚未探活' }] };
        const { schedule, spy, view, exhausted } = loadRecheck(always, Array(50).fill(always));
        schedule();
        await spy.finished;
        assert.equal(spy.gets.length, 5, '最多重抓 5 次');
        assert.equal(spy.renders, 0);
        assert.equal(exhausted(), true);
        assert.equal(view.badgeSlot.child.text, '尚未取得結果，請重新整理');
    },
    async notCheckingSchedulesNothing() {
        const { schedule, spy } = loadRecheck({ steps: [{ id: 'ai', done: true }] }, []);
        schedule();
        assert.equal(spy.polls, 0);
    },
    healthBackgroundRowTextCoversAllProbeStates() {
        const fn = new Function(`${extract(settingsSrc, 'function aiProbeStatusText')}\nreturn aiProbeStatusText;`)();
        assert.equal(fn({ aiProbeStatus: 'Ready' }), '連線正常');
        assert.ok(fn({ aiProbeStatus: 'Failed' }).startsWith('連線失敗'));
        assert.ok(fn({ aiProbeStatus: 'NotConfigured' }).includes('尚未設定'));
        assert.equal(fn({}), '尚未探測');
    }
};

const name = process.argv[2];
if (!name) { Object.keys(cases).forEach(key => console.log(key)); process.exit(0); }
if (!cases[name]) { console.error(`未知案例：${name}`); process.exit(2); }
await cases[name]();
console.log(`PASS ${name}`);
