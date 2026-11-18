/**
 * 執行總表（runs.js）展開某日：renderDayDetailInto 各日期各持有獨立的排序／篩選／分頁狀態。
 * 切出函式本體、注入替身執行。
 */
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { installDom } from './mini-dom.mjs';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const jsRoot = path.resolve(scriptDir, '..', '..', 'LogForesight.Web', 'wwwroot', 'js');
installDom();

const src = fs.readFileSync(path.join(jsRoot, 'pages', 'runs.js'), 'utf8').replaceAll('\r\n', '\n');
const extract = decl => {
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

const tick = () => new Promise(resolve => setImmediate(resolve));
const tickMany = async () => { for (let i = 0; i < 5; i++) await tick(); };

function load() {
    const spy = { urls: [], tables: new Map(), pagers: new Map() };
    const stubs = {
        document,
        DAY_DETAIL_COLUMNS: [],
        STATUS_META: { success: { label: '成功' }, failed: { label: '失敗' }, recheck: { label: '空資料待複查' } },
        loadPageSize: () => 20,
        savePageSize: () => { },
        renderLoading: () => { },
        renderError: () => { },
        renderTable: (container, opts) => spy.tables.set(container, opts),
        renderPagination: (container, opts) => spy.pagers.set(container, opts),
        api: {
            get: async url => {
                spy.urls.push(url);
                return { items: [], page: 1, pageSize: 20, total: 100 };
            }
        }
    };
    const names = Object.keys(stubs);
    const body = `${extract('async function renderDayDetailInto')}\nreturn renderDayDetailInto;`;
    return { render: new Function(...names, body)(...names.map(n => stubs[n])), spy };
}

/** 一個儲存格展開後的三個容器（狀態篩選列、列表、分頁）與目前註冊的處理常式 */
const handles = (spy, cell) => ({
    select: () => cell.children[0].children[1],
    table: () => spy.tables.get(cell.children[1]),
    pager: () => spy.pagers.get(cell.children[2])
});
const query = url => new URLSearchParams(url.split('?')[1]);
const lastUrl = spy => spy.urls[spy.urls.length - 1];

const cases = {
    async eachDayKeepsItsOwnSortAndPage() {
        const { render, spy } = load();
        const cellA = document.createElement('div');
        const cellB = document.createElement('div');
        const A = handles(spy, cellA), B = handles(spy, cellB);

        await render(cellA, '2026-11-01');
        assert.ok(lastUrl(spy).startsWith('/api/runs/day/2026-11-01?'), lastUrl(spy));
        assert.equal(query(lastUrl(spy)).get('page'), '1');
        assert.equal(query(lastUrl(spy)).get('sort'), 'hostName');
        assert.equal(query(lastUrl(spy)).get('dir'), 'asc');

        // 第一天：換到第 2 頁、再改排序（改排序回第 1 頁），之後再換到第 3 頁
        A.pager().onPage(2);
        await tickMany();
        assert.equal(query(lastUrl(spy)).get('page'), '2');
        A.table().onSort('status', 'desc');
        await tickMany();
        assert.equal(query(lastUrl(spy)).get('sort'), 'status');
        assert.equal(query(lastUrl(spy)).get('dir'), 'desc');
        assert.equal(query(lastUrl(spy)).get('page'), '1', '改排序回第 1 頁');
        A.pager().onPage(2);
        await tickMany();

        // 第二天此時才展開：必須是預設狀態，且網址是自己的日期
        const before = spy.urls.length;
        await render(cellB, '2026-11-02');
        assert.equal(spy.urls.length, before + 1);
        const bFirst = lastUrl(spy);
        assert.ok(bFirst.startsWith('/api/runs/day/2026-11-02?'), bFirst);
        assert.equal(query(bFirst).get('page'), '1', '第二天不沿用第一天的頁碼');
        assert.equal(query(bFirst).get('sort'), 'hostName', '第二天不沿用第一天的排序');
        assert.equal(query(bFirst).get('dir'), 'asc');

        // 第二天換頁：只動自己
        B.pager().onPage(3);
        await tickMany();
        assert.ok(lastUrl(spy).startsWith('/api/runs/day/2026-11-02?'));
        assert.equal(query(lastUrl(spy)).get('page'), '3');
        assert.equal(query(lastUrl(spy)).get('sort'), 'hostName');

        // 第一天再動一次：保留自己的排序、日期仍是第一天，不被第二天的頁碼帶走
        A.pager().onPage(4);
        await tickMany();
        const aNext = lastUrl(spy);
        assert.ok(aNext.startsWith('/api/runs/day/2026-11-01?'), aNext);
        assert.equal(query(aNext).get('page'), '4');
        assert.equal(query(aNext).get('sort'), 'status');
        assert.equal(query(aNext).get('dir'), 'desc');

        // 兩個儲存格各有自己的列表／分頁容器
        assert.notEqual(cellA.children[1], cellB.children[1]);
        assert.notEqual(cellA.children[2], cellB.children[2]);
    },

    // F-1b：狀態篩選帶到後端、換狀態回第 1 頁；選回「全部」不帶 status；另一天不受影響
    async statusFilterResetsPageAndStaysPerDay() {
        const { render, spy } = load();
        const cellA = document.createElement('div');
        const cellB = document.createElement('div');
        const A = handles(spy, cellA);
        await render(cellA, '2026-11-01');
        await render(cellB, '2026-11-02');
        assert.equal(query(lastUrl(spy)).get('status'), null, '預設不篩選');

        const labels = A.select().children.map(o => o.textContent);
        assert.deepEqual(labels, ['全部', '成功', '失敗', '空資料待複查']);

        A.pager().onPage(3);
        await tickMany();
        A.select().value = 'failed';
        A.select().dispatch('change');
        await tickMany();
        const filtered = lastUrl(spy);
        assert.ok(filtered.startsWith('/api/runs/day/2026-11-01?'), filtered);
        assert.equal(query(filtered).get('status'), 'failed');
        assert.equal(query(filtered).get('page'), '1', '換狀態回第 1 頁');

        A.select().value = '';
        A.select().dispatch('change');
        await tickMany();
        assert.equal(query(lastUrl(spy)).get('status'), null, '選回全部不帶 status');

        handles(spy, cellB).pager().onPage(2);
        await tickMany();
        assert.ok(lastUrl(spy).startsWith('/api/runs/day/2026-11-02?'));
        assert.equal(query(lastUrl(spy)).get('status'), null, '另一天的篩選不受影響');
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
