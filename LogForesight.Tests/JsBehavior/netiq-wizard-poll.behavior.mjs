/**
 * netiq-import-wizard.js 輪詢掃描工作的容錯行為測試（第 59 輪 F5）。
 *
 * 抽出並執行 production 的 stopPolling、startPolling，只替換 api、toast、計時器與畫面邊界；
 * 由 NetiqWizardPollBehaviorTests 以 `node netiq-wizard-poll.behavior.mjs <案例名>` 逐案執行。
 */

import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const wizardPath = path.join(scriptDir, '..', '..', 'LogForesight.Web', 'wwwroot', 'js', 'pages', 'netiq-import-wizard.js');
const source = fs.readFileSync(wizardPath, 'utf8').replaceAll('\r\n', '\n');

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

class FakeApiError extends Error {
    constructor(code, message, status) { super(message); this.code = code; this.status = status; }
}

/** responses：依序回應；Error 實例代表擲出，物件代表回傳的工作 */
function makeContext(responses) {
    const timers = [];
    const state = { toasts: [], hidden: 0, rendered: 0, calls: 0, completed: 0 };
    const api = {
        get: async () => {
            const next = responses[Math.min(state.calls++, responses.length - 1)];
            if (next instanceof Error) throw next;
            return next;
        }
    };
    const factory = new Function('api', 'toast', 'setTimeout', 'clearTimeout', 'wizardModal', 'renderScanProgress',
        'onCompleted', `
        let currentJobId = null;
        let pollTimer = null;
        let wizardScan = null, wizardServer = null;
        const wizardPrimaryButton = { disabled: true };
        const renderCoverageNote = () => onCompleted();
        const renderSubnetSelection = () => {};
        ${extractFunction('function stopPolling')}
        ${extractFunction('function startPolling')}
        return { startPolling, stopPolling, getJobId: () => currentJobId };
    `);
    const exported = factory(
        api,
        (message, kind) => state.toasts.push({ message, kind }),
        fn => { timers.push(fn); return timers.length; },
        () => {},
        { hide: () => { state.hidden++; } },
        () => { state.rendered++; },
        () => { state.completed++; });
    return { ...exported, state, timers, tick: async () => { const fn = timers.shift(); assert(fn, '沒有排定的下一次輪詢'); await fn(); } };
}

const running = { status: 'running', stage: '主掃描中', hostsFound: 1 };
const transient = () => new FakeApiError('server_error', '系統發生未預期的錯誤', 502);

const cases = {
    async 一次502後繼續輪詢() {
        const ctx = makeContext([transient(), running, { status: 'completed', result: { server: 'S' } }]);
        ctx.startPolling('job1');
        await ctx.tick();
        assert.equal(ctx.state.hidden, 0, '單次 502 不可關精靈');
        assert.equal(ctx.getJobId(), 'job1', '單次 502 不可丟掉 jobId');
        assert.equal(ctx.timers.length, 1, '失敗後必須排定下一次輪詢');
        await ctx.tick();
        await ctx.tick();
        assert.equal(ctx.state.completed, 1, '之後成功完成應走完成流程');
    },
    async 連續三次失敗才停止並提示() {
        const ctx = makeContext([transient()]);
        ctx.startPolling('job1');
        await ctx.tick();
        await ctx.tick();
        assert.equal(ctx.state.hidden, 0, '前兩次失敗不可關精靈');
        await ctx.tick();
        assert.equal(ctx.state.hidden, 1, '第三次連續失敗應關精靈');
        assert(ctx.state.toasts.some(t => t.message.includes('暫時無法取得掃描進度')), '應提示暫時無法取得掃描進度');
        assert.equal(ctx.timers.length, 0, '停止後不再排定輪詢');
    },
    async 成功一次歸零連續失敗計數() {
        const ctx = makeContext([transient(), transient(), running, transient(), transient(), running]);
        ctx.startPolling('job1');
        for (let i = 0; i < 6; i++) await ctx.tick();
        assert.equal(ctx.state.hidden, 0, '失敗不連續達三次，不可關精靈');
        assert.equal(ctx.getJobId(), 'job1');
    },
    async 工作不存在或已逾期立即停止() {
        const gone = new FakeApiError('validation_failed', '掃描工作不存在或已逾期，請重新掃描。', 400);
        const ctx = makeContext([gone]);
        ctx.startPolling('job1');
        await ctx.tick();
        assert.equal(ctx.state.hidden, 1, '工作已逾期應立即關精靈');
        assert.equal(ctx.getJobId(), null, '工作已逾期應清掉 jobId');
        assert(ctx.state.toasts.some(t => t.message.includes('已逾期')), '應顯示後端訊息');
    }
};

const name = process.argv[2];
if (!name) {
    console.log(Object.keys(cases).join('\n'));
    process.exit(0);
}

try {
    await cases[name]();
    console.log(`PASS ${name}`);
} catch (error) {
    console.error(`FAIL ${name}: ${error.stack || error.message}`);
    process.exitCode = 1;
}
