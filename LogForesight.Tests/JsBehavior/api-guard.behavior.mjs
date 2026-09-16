/**
 * core/api.js 的 GET 逾時（B7 C1）與 core/ui.js guardLoad 失敗狀態重試鈕（B7 C2）的行為測試。
 *
 * 由 FrontendB7BehaviorTests 以 `node api-guard.behavior.mjs <案例名>` 逐案執行；
 * 成功時印出 `PASS <案例名>` 並以 0 結束，失敗時印出原因並以 1 結束。
 */

import { installDom } from './mini-dom.mjs';

const dom = installDom();
// paths.js 在模組載入當下就讀 window.LF_BASE，替身必須先裝好才 import
const { api, ApiError } = await import('../../LogForesight.Web/wwwroot/js/core/api.js');
const ui = await import('../../LogForesight.Web/wwwroot/js/core/ui.js');

function assert(condition, message) {
    if (!condition) throw new Error(message);
}

/** 永不自行結束的假 fetch：只有被 AbortController 中止時才 reject */
function hangingFetch(record) {
    return (url, init) => new Promise((_resolve, reject) => {
        record.signal = init?.signal ?? null;
        init?.signal?.addEventListener('abort', () => {
            const error = new Error('The operation was aborted.');
            error.name = 'AbortError';
            reject(error);
        });
    });
}

/** delayMs 之後才成功回應的假 fetch */
function slowFetch(record, delayMs) {
    return (url, init) => new Promise((resolve, reject) => {
        record.signal = init?.signal ?? null;
        record.method = init?.method;
        init?.signal?.addEventListener('abort', () => {
            record.aborted = true;
            const error = new Error('The operation was aborted.');
            error.name = 'AbortError';
            reject(error);
        });
        setTimeout(() => resolve({
            ok: true,
            status: 200,
            json: async () => ({ success: true, data: { ok: 1 } })
        }), delayMs);
    });
}

const cases = {
    /** GET 超過逾時 → 拋出可辨識為逾時的 ApiError（code = timeout） */
    async GET超過逾時拋出可辨識的逾時錯誤() {
        const record = {};
        globalThis.fetch = hangingFetch(record);

        let caught = null;
        try {
            await api.get('/api/slow', { timeoutMs: 30 });
        } catch (error) {
            caught = error;
        }

        assert(caught instanceof ApiError, `應拋出 ApiError，實際為 ${caught}`);
        assert(caught.code === 'timeout', `錯誤分類應為 timeout，實際為 ${caught.code}`);
        assert(caught.message.includes('逾時'), `訊息應講出逾時，實際為 ${caught.message}`);
        assert(record.signal !== null, 'GET 應帶 AbortSignal');
    },

    /** 反例：POST 超過同樣時間**不會**被中止，照常拿到結果 */
    async POST超過同樣時間不會被中止() {
        const record = {};
        globalThis.fetch = slowFetch(record, 120);

        const data = await api.post('/api/long-write', { a: 1 }, { timeoutMs: 30 });

        assert(record.method === 'POST', `方法應為 POST，實際為 ${record.method}`);
        assert(record.signal == null, 'POST 不得帶 AbortSignal（客戶端中止無法叫停伺服器端的寫入）');
        assert(record.aborted !== true, 'POST 不得被中止');
        assert(data?.ok === 1, `POST 應照常取得結果，實際為 ${JSON.stringify(data)}`);
    },

    /** 非 silent 時逾時發一次 toast */
    async 逾時在非silent時發一次toast() {
        globalThis.fetch = hangingFetch({});
        dom.toasts.length = 0;

        try { await api.get('/api/slow', { timeoutMs: 20 }); } catch { /* 預期會拋 */ }

        assert(dom.toasts.length === 1, `應發一次 toast，實際 ${dom.toasts.length} 次`);
    },

    /** silent 時逾時不發 toast */
    async 逾時在silent時不發toast() {
        globalThis.fetch = hangingFetch({});
        dom.toasts.length = 0;

        try { await api.get('/api/slow', { timeoutMs: 20, silent: true }); } catch { /* 預期會拋 */ }

        assert(dom.toasts.length === 0, `silent 不應發 toast，實際 ${dom.toasts.length} 次`);
    },

    /** C2：失敗狀態有重試鈕，按下會先回骨架再重跑同一段載入流程 */
    async 載入失敗顯示重試鈕且按下會重跑() {
        const container = dom.newElement('div');
        let calls = 0;
        let skeletonWhenRetried = false;

        const load = async () => {
            calls++;
            if (calls === 1) {
                const error = new ApiError('timeout', '查詢逾時，請縮小查詢範圍或稍後再試。', 0);
                throw error;
            }
            skeletonWhenRetried = container.querySelector('.lf-skeleton') !== null;
        };

        await ui.guardLoad(container, load);

        const retryBtn = container.find(n => n.tagName === 'BUTTON' && n.textContent.includes('重試'));
        assert(retryBtn !== null, '可重試的失敗狀態應有重試鈕');

        retryBtn.dispatch('click');
        await new Promise(resolve => setTimeout(resolve, 0));

        assert(calls === 2, `按下重試應重跑載入流程，實際呼叫 ${calls} 次`);
        assert(skeletonWhenRetried, '重試時應先回到骨架狀態再跑');
    },

    /** C2 反例：404 不顯示重試鈕（重試永遠不會成功） */
    async 找不到資料不顯示重試鈕() {
        const container = dom.newElement('div');

        await ui.guardLoad(container, async () => {
            throw new ApiError('not_found', '找不到這筆資料，可能已被刪除。', 404);
        });

        assert(container.textContent.includes('找不到資料'), '404 應顯示找不到資料的狀態');
        const retryBtn = container.find(n => n.tagName === 'BUTTON' && n.textContent.includes('重試'));
        assert(retryBtn === null, '404 分支不得顯示重試鈕');
    }
};

const name = process.argv[2];
if (!name) {
    console.log(Object.keys(cases).join('\n'));
    process.exit(0);
}

const target = cases[name];
if (!target) {
    console.error(`找不到案例：${name}`);
    process.exit(1);
}

try {
    await target();
    console.log(`PASS ${name}`);
    process.exit(0);
} catch (error) {
    console.error(`FAIL ${name}: ${error.message}`);
    process.exit(1);
}
