import { createSeqGuard, pollWhile } from '../../LogForesight.Web/wwwroot/js/core/async-guards.js';

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

function expectEqual(actual, expected, label) {
    if (actual !== expected) {
        throw new Error(`${label}：預期 ${expected} 實際 ${actual}`);
    }
}

/** 可控的 document 替身：hidden 狀態、visibilitychange 監聽 */
function installFakeDocument(hidden) {
    const listeners = new Set();
    const doc = {
        hidden,
        addEventListener(type, handler) { if (type === 'visibilitychange') listeners.add(handler); },
        removeEventListener(type, handler) { if (type === 'visibilitychange') listeners.delete(handler); },
    };
    globalThis.document = doc;
    return {
        setHidden(value) { doc.hidden = value; for (const h of [...listeners]) h(); },
        listenerCount: () => listeners.size,
    };
}

const cases = {
    async seqStale() {
        const guard = createSeqGuard();
        const first = guard.next();
        guard.next();
        expectEqual(guard.isCurrent(first), false, '舊序號應為非最新');
    },

    async seqCurrent() {
        const guard = createSeqGuard();
        guard.next();
        const latest = guard.next();
        expectEqual(guard.isCurrent(latest), true, '最新序號應為最新');
    },

    async pollNoOverlap() {
        let inFlight = 0;
        let maxInFlight = 0;
        const handle = pollWhile(async () => {
            inFlight += 1;
            maxInFlight = Math.max(maxInFlight, inFlight);
            await sleep(100);
            inFlight -= 1;
        }, 10);
        await sleep(350);
        handle.stop();
        await sleep(120);
        expectEqual(maxInFlight, 1, '同時在途的 fn 最大數');
    },

    async pollSurvivesThrow() {
        let calls = 0;
        const origWarn = console.warn;
        console.warn = () => {};
        const handle = pollWhile(() => {
            calls += 1;
            if (calls === 1) throw new Error('模擬失敗');
        }, 10);
        await sleep(80);
        handle.stop();
        console.warn = origWarn;
        if (calls < 2) throw new Error(`擲例外後未續排：呼叫次數 ${calls}`);
    },

    async pollStop() {
        let calls = 0;
        const handle = pollWhile(() => { calls += 1; }, 10);
        await sleep(35);
        handle.stop();
        const atStop = calls;
        await sleep(60);
        expectEqual(calls, atStop, 'stop 後的呼叫次數');
    },

    async pollStopInFlight() {
        let calls = 0;
        let handle = null;
        handle = pollWhile(async () => {
            calls += 1;
            handle.stop();
            await sleep(5);
        }, 5);
        await sleep(60);
        expectEqual(calls, 1, 'fn 執行中 stop 後的呼叫次數');
    },

    async pollIntervalFunctionSeesLastResult() {
        const seen = [];
        let calls = 0;
        const handle = pollWhile(() => { calls += 1; return `r${calls}`; },
            (ctx) => { seen.push(ctx); return 10; });
        await sleep(60);
        handle.stop();
        if (seen.length < 2) throw new Error(`間隔函式呼叫次數 ${seen.length}`);
        expectEqual(seen[0].lastResult, undefined, '首次排程時沒有上一輪結果');
        expectEqual(seen[1].lastResult, 'r1', '第二次排程看到上一輪回傳值');
        expectEqual(seen[1].failures, 0, '成功時失敗計數為 0');
    },

    async pollBackoffOnFailureThenRecovers() {
        const origWarn = console.warn;
        console.warn = () => {};
        let calls = 0;
        let shouldFail = true;
        const handle = pollWhile(() => { calls += 1; if (shouldFail) throw new Error('模擬失敗'); }, 10, { backoffMaxMs: 80 });
        await sleep(220);
        const withBackoff = calls;
        const failures = handle.failures;
        // 不退避時 220ms 約 20 次；退避後（10、20、40、80…）應明顯少很多
        if (withBackoff > 8) throw new Error(`失敗退避無效：220ms 內呼叫 ${withBackoff} 次`);
        if (failures < 3) throw new Error(`失敗計數應累計，實際 ${failures}`);
        shouldFail = false;
        await sleep(200);
        handle.stop();
        console.warn = origWarn;
        expectEqual(handle.failures, 0, '成功一次後失敗計數歸零');
    },

    async pollNoBackoffWithoutOption() {
        const origWarn = console.warn;
        console.warn = () => {};
        let calls = 0;
        const handle = pollWhile(() => { calls += 1; throw new Error('模擬失敗'); }, 10);
        await sleep(120);
        handle.stop();
        console.warn = origWarn;
        if (calls < 6) throw new Error(`未設退避時應維持原間隔，120ms 內只呼叫 ${calls} 次`);
    },

    async pollHiddenAtStartDoesNotRunThenRunsOnForeground() {
        const doc = installFakeDocument(true);
        let calls = 0;
        const handle = pollWhile(() => { calls += 1; }, 20, { pauseWhenHidden: true });
        await sleep(80);
        expectEqual(calls, 0, '背景分頁時不執行');
        doc.setHidden(false);
        await sleep(15);
        expectEqual(calls, 1, '回到前景立即執行一次（早於下一個間隔）');
        await sleep(60);
        handle.stop();
        if (calls < 2) throw new Error(`回到前景後應照常續排，呼叫 ${calls} 次`);
    },

    async pollForegroundAfterBackgroundRunsImmediately() {
        const doc = installFakeDocument(false);
        let calls = 0;
        const handle = pollWhile(() => { calls += 1; }, 1000, { pauseWhenHidden: true });
        doc.setHidden(true);
        doc.setHidden(false);
        await sleep(30);
        handle.stop();
        expectEqual(calls, 1, '進過背景再回前景，不等 1000ms 立即補一次');
    },

    async pollVisibleEventWithoutBackgroundDoesNotRunExtra() {
        const doc = installFakeDocument(false);
        let calls = 0;
        const handle = pollWhile(() => { calls += 1; }, 1000, { pauseWhenHidden: true });
        doc.setHidden(false);
        await sleep(30);
        handle.stop();
        expectEqual(calls, 0, '一直在前景時，visibilitychange 不應多跑一次');
    },

    async pollStopRemovesVisibilityListener() {
        const doc = installFakeDocument(true);
        const handle = pollWhile(() => {}, 20, { pauseWhenHidden: true });
        expectEqual(doc.listenerCount(), 1, 'pauseWhenHidden 會掛一個 visibilitychange 監聽');
        handle.stop();
        expectEqual(doc.listenerCount(), 0, 'stop 後移除監聽');
    },

    async pollWithoutPauseOptionIgnoresHidden() {
        installFakeDocument(true);
        let calls = 0;
        const handle = pollWhile(() => { calls += 1; }, 10);
        await sleep(60);
        handle.stop();
        if (calls < 2) throw new Error(`未設 pauseWhenHidden 時應照常輪詢，呼叫 ${calls} 次`);
    },

    async pollFalseStops() {
        let calls = 0;
        pollWhile(() => {
            calls += 1;
            return false;
        }, 10);
        await sleep(60);
        expectEqual(calls, 1, 'fn 回 false 後的呼叫次數');
    },
};

const name = process.argv[2];
if (!name) {
    console.log(Object.keys(cases).join('\n'));
    process.exit(0);
}
if (!(name in cases)) {
    console.log(`FAIL ${name} 未知案例`);
    process.exit(1);
}
try {
    await cases[name]();
    console.log(`PASS ${name}`);
} catch (err) {
    console.log(`FAIL ${name} ${err && err.message ? err.message : err}`);
    process.exit(1);
}
