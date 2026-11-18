/**
 * 問題查詢（records.js）列展開：issueHostDateTags 的日期連結與「另有」、renderIssueOccurrences 的請求與空狀態。
 * 切出函式本體、注入替身執行；另有原始碼守門確認展開不再走明細端點。
 */
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { installDom } from './mini-dom.mjs';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const jsRoot = path.resolve(scriptDir, '..', '..', 'LogForesight.Web', 'wwwroot', 'js');
installDom();

const src = fs.readFileSync(path.join(jsRoot, 'pages', 'records.js'), 'utf8').replaceAll('\r\n', '\n');
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

/** 載入被測函式；替身的呼叫記錄在回傳的 spy 上 */
function load({ apiResult = null } = {}) {
    // from：表單目前的起日（案例可在展開後改它，模擬使用者改了欄位但還沒送出）
    const spy = { detailCalls: [], urls: [], empties: [], tables: [], pagers: [], from: '2026-11-01' };
    const stubs = {
        document,
        appUrl: p => p,
        // 回傳可辨識的字串，同時把參數記下來供斷言
        recordDetailUrl: (hostId, date, opts) => { spy.detailCalls.push({ hostId, date, opts }); return `/records/${hostId}/${date}`; },
        // 後端問題視角的查詢字串含 view／sort／dir／page：展開請求必須把它們剝掉
        buildQueryString: () => `view=issues&sort=count&dir=desc&page=3&from=${spy.from}`,
        collectFilters: () => ({}),
        formatNumber: n => String(n),
        renderLoading: () => { },
        renderError: () => { },
        renderEmpty: (c, o) => spy.empties.push(o),
        renderTable: (c, o) => spy.tables.push(o),
        renderPagination: (c, o) => spy.pagers.push(o),
        roleText: (cls, text) => { const n = document.createElement('span'); n.textContent = text; return n; },
        severityBadge: () => '', handlingBadge: () => '', linkButton: () => '',
        api: {
            get: async url => { spy.urls.push(url); return apiResult; }
        }
    };
    const names = Object.keys(stubs);
    const body = [
        extract('const ISSUE_HOSTS_PAGE_SIZE'),
        extract('function detailForIssue'),
        extract('async function renderIssueOccurrences'),
        extract('function issueHostDateTags'),
        extract('function issueHostDaysCell'),
        'return { issueHostDateTags, renderIssueOccurrences };'
    ].join('\n');
    return { ...new Function(...names, body)(...names.map(n => stubs[n])), spy };
}

const links = box => box.children.filter(c => c.tagName === 'A');
const dates = n => Array.from({ length: n }, (_, i) => `2026-11-${String(20 - i).padStart(2, '0')}`);
const group = { source: 'NetIQ', eventId: 5 };

const cases = {
    fourteenDatesNoMore() {
        const { issueHostDateTags, spy } = load();
        const box = issueHostDateTags({ hostId: 42, dates: dates(14), moreDays: 0 }, group);
        const a = links(box);
        assert.equal(a.length, 14, '14 個日期連結、沒有「另有」');
        assert.ok(!box.textContent.includes('另有'));
        assert.equal(spy.detailCalls.length, 14);
        spy.detailCalls.forEach((c, i) => {
            assert.equal(c.hostId, 42);
            assert.equal(c.date, dates(14)[i]);
            assert.equal(c.opts.handle, true, '日期連結要帶展開處理狀態的參數');
        });
        assert.equal(a[0].href, '/records/42/2026-11-20');
        assert.equal(a[0].textContent, '2026-11-20');
    },

    fifteenthDayBecomesMoreLink() {
        const { issueHostDateTags, spy } = load();
        const box = issueHostDateTags({ hostId: 42, dates: dates(14), moreDays: 1 }, group);
        const a = links(box);
        assert.equal(a.length, 15, '14 個日期連結加一個「另有」');
        assert.equal(spy.detailCalls.length, 14, '「另有」連結不走詳情網址');
        assert.equal(a.slice(0, 14).filter(x => x.textContent.startsWith('2026-')).length, 14);
        const more = a[14];
        assert.equal(more.textContent, '另有 1 天');
        const q = new URLSearchParams(more.href.replace(/^\?/, ''));
        assert.equal(q.get('hostIds'), '42', '鎖定這台主機');
        assert.equal(q.get('source'), 'NetIQ');
        assert.equal(q.get('eventId'), '5');
        assert.equal(q.get('from'), '2026-11-01', '沿用目前篩選');
        assert.ok(!q.has('view') && !q.has('page'));
    },

    async occurrencesEmptyShowsEmptyState() {
        const { renderIssueOccurrences, spy } = load({ apiResult: { items: [], total: 0, page: 1, pageSize: 20 } });
        await renderIssueOccurrences(document.createElement('div'), group);
        assert.equal(spy.empties.length, 1);
        assert.equal(spy.empties[0].title, '此範圍內沒有可展開的主機日');
        assert.equal(spy.tables.length, 0, '空結果不畫表格');
    },

    async occurrencesRequestUsesIssueHostsEndpoint() {
        const { renderIssueOccurrences, spy } = load({
            apiResult: { items: [{ hostId: 1, hostName: 'h', dates: [], moreDays: 0, lastSeen: '2026-11-01', dayCount: 1 }], total: 45, page: 1, pageSize: 20 }
        });
        await renderIssueOccurrences(document.createElement('div'), group);
        assert.equal(spy.urls.length, 1);
        const url = spy.urls[0];
        assert.ok(url.startsWith('/api/records/issue-hosts?'), url);
        const q = new URLSearchParams(url.split('?')[1]);
        assert.equal(q.get('source'), 'NetIQ');
        assert.equal(q.get('eventId'), '5');
        assert.equal(q.get('pageSize'), '20');
        assert.equal(q.get('from'), '2026-11-01', '沿用目前篩選');
        ['view', 'sort', 'dir', 'page'].forEach(k => assert.ok(!q.has(k), `不應帶 ${k}：${url}`));
        // 換頁：第 2 頁才帶 page
        assert.equal(spy.pagers.length, 1);
        assert.equal(spy.pagers[0].totalPages, 3);
        await spy.pagers[0].onPage(2);
        assert.equal(new URLSearchParams(spy.urls[1].split('?')[1]).get('page'), '2');
    },

    // 展開後表單被改但沒送出：換頁仍用展開當下的篩選（同一列的各頁不得混用兩組條件）
    async pagingKeepsFiltersFromExpansion() {
        const { renderIssueOccurrences, spy } = load({
            apiResult: { items: [{ hostId: 1, hostName: 'h', dates: [], moreDays: 0, lastSeen: '2026-11-01', dayCount: 1 }], total: 45, page: 1, pageSize: 20 }
        });
        await renderIssueOccurrences(document.createElement('div'), group);
        spy.from = '2026-10-01';
        await spy.pagers[0].onPage(2);
        const q = new URLSearchParams(spy.urls[1].split('?')[1]);
        assert.equal(q.get('page'), '2');
        assert.equal(q.get('from'), '2026-11-01', '換頁要沿用展開當下的篩選');
    },

    occurrencesDoesNotCallDetailEndpoint() {
        const body = extract('async function renderIssueOccurrences');
        assert.ok(body.includes('/api/records/issue-hosts?'), '展開走依問題的主機端點');
        assert.ok(!body.includes("'/api/records?'"), '不得再呼叫明細端點');
        assert.ok(!body.includes('`/api/records?'), '不得再呼叫明細端點（樣板字串）');
        assert.ok(!body.includes('/api/records?'), '不得再呼叫明細端點');
        assert.ok(!body.includes('pageSize=100'), '不得再一次抓 100 筆明細');
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
