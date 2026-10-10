/**
 * core/api.js 的 GET 逾時（B7 C1）與 core/ui.js guardLoad 失敗狀態重試鈕（B7 C2）的行為測試。
 *
 * 由 ApiTimeoutBehaviorTests 以 `node api-guard.behavior.mjs <案例名>` 逐案執行；
 * 成功時印出 `PASS <案例名>` 並以 0 結束，失敗時印出原因並以 1 結束。
 */

import { installDom } from './mini-dom.mjs';
import { readFile } from 'node:fs/promises';

const dom = installDom();
window.LF_BASE = '/lf';
// paths.js 在模組載入當下就讀 window.LF_BASE，替身必須先裝好才 import
const { api, ApiError } = await import('../../LogForesight.Web/wwwroot/js/core/api.js');
const ui = await import('../../LogForesight.Web/wwwroot/js/core/ui.js');

function assert(condition, message) {
    if (!condition) throw new Error(message);
}

async function downloadPageHarness(downloadJson) {
    const source = await readFile(new URL('../../LogForesight.Web/wwwroot/js/pages/prtg-admin.js', import.meta.url), 'utf8');
    const start = source.indexOf("    acceptanceExport.addEventListener('click',");
    const end = source.indexOf("    document.getElementById('prtg-acceptance-label-form')", start);
    assert(start >= 0 && end > start, '必須載入實際頁面下載 handler');
    let click;
    const button = { disabled: false, addEventListener(_name, handler) { click = handler; } };
    const status = { textContent: '' };
    const links = [];
    const revoked = [];
    const doc = {
        getElementById(id) { return { value: id.endsWith('through') ? '2026-10-10' : '2026-10-09' }; },
        createElement() { const link = { click() { links.push(this); } }; return link; }
    };
    const url = { createObjectURL(blob) { assert(blob instanceof Blob, '只允許完整 Blob'); return 'blob:evidence'; },
        revokeObjectURL(value) { revoked.push(value); } };
    new Function('acceptanceExport', 'status', 'document', 'api', 'URL', 'setTimeout', source.slice(start, end))(
        button, status, doc, { downloadJson }, url, callback => callback());
    return { button, status, links, revoked, click };
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
    async JSON下載已知超限不等待取消確認() {
        const body = new ReadableStream({ cancel() { return new Promise(() => {}); } });
        globalThis.fetch = async () => new Response(body,
            { headers: { 'Content-Type': 'application/json', 'Content-Length': '6' } });
        const caught = await Promise.race([
            api.downloadJson('/api/prtg/acceptance/export', { maxBytes: 5, timeoutMs: 20, silent: true }).catch(error => error),
            new Promise(resolve => setTimeout(() => resolve(null), 100))
        ]);
        assert(caught?.code === 'download_byte_cap', '已知超限必須立即拒絕，不能卡在取消確認');
    },
    async JSON下載非JSON不等待取消確認() {
        const body = new ReadableStream({ cancel() { return new Promise(() => {}); } });
        globalThis.fetch = async () => new Response(body, { headers: { 'Content-Type': 'text/html' } });
        const caught = await Promise.race([
            api.downloadJson('/api/prtg/acceptance/export', { timeoutMs: 20, silent: true }).catch(error => error),
            new Promise(resolve => setTimeout(() => resolve(null), 100))
        ]);
        assert(caught?.code === 'download_invalid_response', '非 JSON 必須立即拒絕，不能卡在取消確認');
    },
    async JSON下載頁面失敗不交付檔案且恢復按鈕() {
        const page = await downloadPageHarness(async () => {
            throw new ApiError('export_output_byte_cap', '超過上限；請縮小日期範圍。', 413);
        });
        await page.click();
        assert(page.links.length === 0 && page.revoked.length === 0, '拒絕時不可下載錯誤或部分檔案');
        assert(!page.button.disabled && page.status.textContent.includes('請縮小日期範圍'), '明確原因及重試按鈕須保留');
    },
    async JSON下載頁面等待完成才交付且釋放資源() {
        let complete;
        const page = await downloadPageHarness(() => new Promise(resolve => { complete = resolve; }));
        const work = page.click();
        assert(page.button.disabled && page.links.length === 0, '等待本文完成期間不應交付檔案');
        complete(new Blob(['{}'], { type: 'application/json' }));
        await work;
        assert(!page.button.disabled && page.links.length === 1 && page.links[0].download === 'prtg-acceptance-2026-10-09-2026-10-10.json', '完成後提供日期檔名並恢復按鈕');
        assert(page.revoked[0] === 'blob:evidence' && page.status.textContent.includes('ScopeComplete'), '釋放資源且保留完整性說明');
    },
    async JSON下載ContentLength超限不讀取本文() {
        let canceled = false;
        const body = new ReadableStream({ cancel() { canceled = true; } });
        globalThis.fetch = async () => new Response(body, { headers: { 'Content-Type': 'application/json', 'Content-Length': '6' } });
        let caught;
        try { await api.downloadJson('/api/prtg/acceptance/export', { maxBytes: 5, silent: true }); } catch (error) { caught = error; }
        assert(caught?.code === 'download_byte_cap' && canceled, '已知超限應立即取消');
    },
    async JSON下載錯誤本文停滯仍有界逾時() {
        let canceled = false;
        globalThis.fetch = async () => new Response(new ReadableStream({ cancel() { canceled = true; } }),
            { status: 413, headers: { 'Content-Type': 'application/json' } });
        let caught;
        try { await api.downloadJson('/api/prtg/acceptance/export', { timeoutMs: 20, silent: true }); } catch (error) { caught = error; }
        assert(caught?.code === 'timeout' && canceled, '錯誤本文也須有界');
    },
    async JSON下載傳輸中斷不提供部分檔案() {
        let chunks = 0;
        globalThis.fetch = async () => new Response(new ReadableStream({
            pull(controller) { if (chunks++ === 0) controller.enqueue(new Uint8Array([123])); else controller.error(new Error('connection lost')); }
        }, { highWaterMark: 0 }), { headers: { 'Content-Type': 'application/json' } });
        let caught;
        try { await api.downloadJson('/api/prtg/acceptance/export', { silent: true }); } catch (error) { caught = error; }
        assert(caught?.code === 'download_failed' && caught.message.includes('未提供部分檔案'), '中斷需明確拒絕');
    },
    async JSON下載登入逾期保留子站導頁() {
        globalThis.fetch = async () => new Response('{}', { status: 401, headers: { 'Content-Type': 'application/json' } });
        let caught;
        try { await api.downloadJson('/api/prtg/acceptance/export', { silent: true }); } catch (error) { caught = error; }
        assert(caught?.code === 'auth_expired' && location.href.startsWith('/lf/login?returnUrl='), '401 須沿用子站登入流程');
    },
    async JSON下載保留原始內容與子站路徑() {
        let captured;
        const text = '{"FormatVersion":2,"ScopeComplete":false}';
        globalThis.fetch = async (url, init) => {
            captured = { url, init };
            return new Response(text, { headers: { 'Content-Type': 'application/json' } });
        };
        const blob = await api.downloadJson('/api/prtg/acceptance/export', { silent: true });
        assert(blob instanceof Blob && await blob.text() === text, '下載須保留原始 JSON，不能期待一般 data 信封');
        assert(captured.url === '/lf/api/prtg/acceptance/export', '路徑必須經既有 appUrl 保留子站前綴');
        assert(captured.init.credentials === 'same-origin' && captured.init.method === 'GET', '下載須使用同源認證與唯讀 GET');
    },
    async JSON下載413保留明確原因且不提供檔案() {
        globalThis.fetch = async () => new Response(JSON.stringify({ success: false,
            error: { code: 'export_output_byte_cap', message: '超過 64 MiB；未產生部分檔案。' } }),
            { status: 413, headers: { 'Content-Type': 'application/json' } });
        let caught;
        try { await api.downloadJson('/api/prtg/acceptance/export', { silent: true }); } catch (error) { caught = error; }
        assert(caught instanceof ApiError && caught.code === 'export_output_byte_cap' && caught.status === 413,
            '413 必須在目前頁面顯示後端原因，不能當成 JSON 檔下載');
        assert(caught.message.includes('未產生部分檔案'), '須保留可採取動作的原因');
    },
    async JSON下載超限取消串流且不提供部分檔案() {
        let canceled = false;
        let reads = 0;
        const body = new ReadableStream({
            pull(controller) { reads++; controller.enqueue(new Uint8Array(4)); },
            cancel() { canceled = true; }
        }, { highWaterMark: 0 });
        globalThis.fetch = async () => new Response(body, { headers: { 'Content-Type': 'application/json' } });
        let caught;
        try { await api.downloadJson('/api/prtg/acceptance/export', { maxBytes: 5, silent: true }); } catch (error) { caught = error; }
        assert(caught instanceof ApiError && caught.code === 'download_byte_cap', '本文超限須拒絕');
        assert(canceled && reads <= 3, '超限須停止讀取，不載入整份或交付部分檔案');
    },
    async JSON下載拒絕登入HTML() {
        globalThis.fetch = async () => new Response('<html>登入</html>', { headers: { 'Content-Type': 'text/html' } });
        let caught;
        try { await api.downloadJson('/api/prtg/acceptance/export', { silent: true }); } catch (error) { caught = error; }
        assert(caught instanceof ApiError && caught.code === 'download_invalid_response', '登入頁或非 JSON 不可保存為證據檔');
    },
    async JSON下載本文逾時會取消串流() {
        let canceled = false;
        const body = new ReadableStream({ cancel() { canceled = true; } });
        globalThis.fetch = async () => new Response(body, { headers: { 'Content-Type': 'application/json' } });
        let caught;
        try { await api.downloadJson('/api/prtg/acceptance/export', { timeoutMs: 20, silent: true }); } catch (error) { caught = error; }
        assert(caught instanceof ApiError && caught.code === 'timeout' && canceled, '收到標頭後本文停滯仍須有界中止');
    },
    async JSON下載本文主動取消保持AbortError() {
        let canceled = false;
        const body = new ReadableStream({ cancel() { canceled = true; } });
        globalThis.fetch = async () => new Response(body, { headers: { 'Content-Type': 'application/json' } });
        dom.toasts.length = 0;
        const controller = new AbortController();
        const query = api.downloadJson('/api/prtg/acceptance/export', { signal: controller.signal });
        setTimeout(() => controller.abort(), 10);
        let caught;
        try { await query; } catch (error) { caught = error; }
        assert(caught?.name === 'AbortError' && canceled && dom.toasts.length === 0, '主動取消不應改成網路失敗或發錯誤 toast');
    },
    async 唯讀POST保留JSON與CSRF且逾時可辨識() {
        let captured;
        globalThis.fetch = (url, init) => {
            captured = { url, init };
            return hangingFetch({})(url, init);
        };
        let caught;
        try {
            await api.readOnlyPost('/api/prtg/monitoring/sensors', { hostIds: [1, 2] },
                { timeoutMs: 20, silent: true });
        } catch (error) { caught = error; }
        assert(captured.init.method === 'POST', '唯讀查詢仍須使用POST body');
        assert(captured.init.headers['X-Requested-By'] === 'LogForesight', '唯讀POST不可遺漏CSRF');
        assert(captured.init.headers['Content-Type'] === 'application/json', '需傳JSON');
        assert(JSON.parse(captured.init.body).hostIds.join(',') === '1,2', '選取範圍需完整保留');
        assert(caught instanceof ApiError && caught.code === 'timeout', '需明確回報查詢逾時');
    },
    async 唯讀POST取消保留AbortError且不發錯誤toast() {
        globalThis.fetch = hangingFetch({});
        dom.toasts.length = 0;
        const controller = new AbortController();
        const query = api.readOnlyPost('/api/prtg/monitoring/sensors', { hostIds: [1] },
            { signal: controller.signal, timeoutMs: 1000 });
        controller.abort();
        let caught;
        try { await query; } catch (error) { caught = error; }
        assert(caught?.name === 'AbortError', '範圍變更取消不可被改成網路失敗');
        assert(dom.toasts.length === 0, '主動取消不應發錯誤toast');
    },
    async 空白403回應仍分類為權限不足() {
        globalThis.fetch = async () => ({ ok: false, status: 403, json: async () => { throw new SyntaxError('empty'); } });
        let caught;
        try { await api.get('/api/restricted', { silent: true }); } catch (error) { caught = error; }
        assert(caught instanceof ApiError && caught.code === 'forbidden' && caught.status === 403, '空白 403 必須保留權限分類與 status');
        assert(caught.message.includes('權限') && !caught.message.includes('未預期'), '已知拒絕不得顯示成未預期系統錯誤');
    },
    async ['403保留後端明確拒絕原因']() {
        globalThis.fetch = async () => ({ ok: false, status: 403, json: async () => ({ success: false, error: { code: 'scope_denied', message: '此主機未授權。' } }) });
        let caught;
        try { await api.get('/api/restricted', { silent: true }); } catch (error) { caught = error; }
        assert(caught instanceof ApiError && caught.code === 'scope_denied' && caught.status === 403, '不得覆蓋後端拒絕分類');
        assert(caught.message === '此主機未授權。', '不得覆蓋後端明確原因');
    },
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
