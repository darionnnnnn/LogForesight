/**
 * PRTG 維護頁的主機選取一律走伺服器端搜尋（第 77 輪 F-4）：
 * - 不再呼叫已移除的全部主機端點；三處（回補勾選清單、指派 modal、衝突批次指派）都經同一份 createHostSearcher。
 * - createHostSearcher 的 debounce、請求序號（慢回應不蓋新結果）、查詢參數。
 */
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(scriptDir, '..', '..');
const jsRoot = path.join(repoRoot, 'LogForesight.Web', 'wwwroot', 'js');
const read = rel => fs.readFileSync(path.join(jsRoot, rel), 'utf8').replaceAll('\r\n', '\n');
const { createSeqGuard } = await import(pathToFileURL(path.join(jsRoot, 'core', 'async-guards.js')).href);

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

function loadSearcher(api) {
    const body = extractFunction(read('core/ui.js'), 'export function createHostSearcher').replace(/^export /, '');
    return new Function('api', 'createSeqGuard', `${body}\nreturn createHostSearcher;`)(api, createSeqGuard);
}

const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

const cases = {
    prtgAdminNoHostsAllAndSingleSearchPath() {
        const admin = read('pages/prtg-admin.js');
        const ui = read('core/ui.js');
        const removed = ['hosts', 'all'].join('/'); // 拆開寫，避免本檔自己讓全專案 grep 命中
        assert(!admin.includes(removed), 'prtg-admin.js 不得再呼叫已移除的全部主機端點');
        assert(!/api\.get\(\s*[`'"]\/api\/admin\/hosts/.test(admin), 'prtg-admin.js 不得自行打主機清單端點，要經共用搜尋函式');
        assert(!/cachedHosts/.test(admin), '不得再整批快取全部主機');
        // 三處：勾選清單＝createHostSearcher；指派 modal、批次指派＝searchableHostSelect
        assert.equal((admin.match(/createHostSearcher\(/g) || []).length, 1, '回補勾選清單用 createHostSearcher');
        assert.equal((admin.match(/searchableHostSelect\(/g) || []).length, 2, '指派 modal 與批次指派各用一次 searchableHostSelect');
        assert(admin.includes("document.getElementById('prtg-assign-host-search')"), '指派 modal 掛搜尋式下拉');
        assert(admin.includes("hostPlaceholder.replaceWith(widget.element)"), '批次指派掛搜尋式下拉');
        // 共用元件內部也走同一份 createHostSearcher，debounce＋序號只有一份
        const widget = extractFunction(ui, 'export function searchableHostSelect');
        assert(widget.includes('createHostSearcher('), 'searchableHostSelect 必須經 createHostSearcher');
        assert(!widget.includes('setTimeout('), 'searchableHostSelect 不得自帶 debounce');
        assert.equal((ui.match(/createSeqGuard\(\)/g) || []).length, 1, 'ui.js 的請求序號只有一份');
    },
    searchParamsAndLimit() {
        const calls = [];
        const api = { get: async url => { calls.push(url); return { items: [], total: 0 }; } };
        const createHostSearcher = loadSearcher(api);
        return (async () => {
            await createHostSearcher({ platform: null, status: 'assignable' }).run('web 01');
            await createHostSearcher({ platform: 'linux' }).run('x');
            const first = new URL(calls[0], 'http://x');
            assert.equal(first.pathname, '/api/admin/hosts');
            assert.equal(first.searchParams.get('query'), 'web 01');
            assert.equal(first.searchParams.get('pageSize'), '50');
            assert.equal(first.searchParams.get('status'), 'assignable');
            assert.equal(first.searchParams.has('os'), false, 'platform 為 null 不帶 os');
            const second = new URL(calls[1], 'http://x');
            assert.equal(second.searchParams.get('os'), 'linux');
            assert.equal(second.searchParams.has('status'), false);
        })();
    },
    debounceAndStaleResponseDropped() {
        const pending = [];
        const api = { get: url => new Promise(resolve => pending.push({ url, resolve })) };
        const createHostSearcher = loadSearcher(api);
        const got = [];
        const searcher = createHostSearcher({ delay: 20, onResult: (r, k) => got.push(k) });
        return (async () => {
            // debounce：連續輸入只送最後一次
            searcher.search('a'); searcher.search('ab'); searcher.search('abc');
            await sleep(60);
            assert.equal(pending.length, 1);
            assert(pending[0].url.includes('query=abc'));
            // 慢回應：先發 abc、再發 abcd，abcd 先回、abc 後回，只採用 abcd
            searcher.search('abcd');
            await sleep(60);
            assert.equal(pending.length, 2);
            pending[1].resolve({ items: [], total: 0 });
            await sleep(5);
            pending[0].resolve({ items: [], total: 0 });
            await sleep(5);
            assert.deepEqual(got, ['abcd']);
            // cancel 之後在途回應也丟棄
            const p = searcher.run('zzz');
            searcher.cancel();
            pending[2].resolve({ items: [], total: 0 });
            assert.equal(await p, null);
            assert.deepEqual(got, ['abcd']);
        })();
    }
};

const name = process.argv[2];
if (!name) {
    console.log(Object.keys(cases).join('\n'));
    process.exit(0);
}
assert(cases[name], `未知的案例：${name}`);
await cases[name]();
console.log(`PASS ${name}`);
