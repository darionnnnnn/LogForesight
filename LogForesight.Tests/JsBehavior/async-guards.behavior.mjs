import { createSeqGuard, pollWhile } from '../../LogForesight.Web/wwwroot/js/core/async-guards.js';

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

function expectEqual(actual, expected, label) {
    if (actual !== expected) {
        throw new Error(`${label}：預期 ${expected} 實際 ${actual}`);
    }
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
