/**
 * 處理人詳情（handler-detail.js 的 loadAdvanced）與使用者詳情（user-detail.js 的 reloadWorkload）的工作負載分頁：
 * 連續換頁與過期回應。切出函式本體、注入替身執行。
 */
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { installDom } from './mini-dom.mjs';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const jsRoot = path.resolve(scriptDir, '..', '..', 'LogForesight.Web', 'wwwroot', 'js');
installDom();

const read = relative => fs.readFileSync(path.join(jsRoot, relative), 'utf8').replaceAll('\r\n', '\n');
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
const seqGuard = () => { let latest = 0; return { next: () => ++latest, isCurrent: seq => seq === latest }; };
const tick = () => new Promise(resolve => setImmediate(resolve));
const pagesOf = url => { const q = new URLSearchParams(url.split('?')[1]); return { cases: q.get('casesPage'), days: q.get('daysPage') }; };

/** 使用者詳情的 reloadWorkload；respond(url) 決定每個請求的回應（可延遲、可擲例外） */
function loadUserDetail(respond) {
    const spy = { urls: [], rendered: [], errors: [] };
    const stubs = {
        document: { getElementById: id => ({ id, replaceChildren() { }, appendChild() { } }) },
        userId: 5, WORKLOAD_PAGE_SIZE: 20,
        renderOpenWork: workload => spy.rendered.push(workload),
        renderError: (container, opts) => spy.errors.push(opts),
        createSeqGuard: seqGuard,
        api: { get: async url => { spy.urls.push(url); return respond(url); } }
    };
    const src = read('pages/user-detail.js');
    const names = Object.keys(stubs);
    const body = [
        extract(src, 'function workloadUrl'),
        'let casesPage = 1; let daysPage = 1;',
        'const workloadSeq = createSeqGuard();',
        extract(src, 'async function reloadWorkload'),
        'return { reloadWorkload };'
    ].join('\n');
    return { ...new Function(...names, body)(...names.map(n => stubs[n])), spy };
}

/** 處理人詳情的 loadAdvanced，外面包真實的 guardLoad（core/ui.js） */
function loadHandlerDetail(respond) {
    const state = { casesEl: 'init', daysEl: 'init' };
    const box = name => ({ name, appendChild() { }, replaceChildren() { } });
    const casesEl = box('casesEl'), daysEl = box('daysEl');
    const stubs = {
        renderEmpty: (container, opts) => { state[container.name] = `ERROR:${opts.title}`; },
        retryNode: () => ({}), backLinkNode: () => ({}),
        renderLoading: container => { state[container.name] = 'loading'; },
        renderCases: cases => { state.casesEl = `TABLE ${cases[0]}`; },
        renderDays: days => { state.daysEl = `TABLE ${days[0]}`; },
        renderPagination() { }, pagerFor: () => ({}),
        createSeqGuard: seqGuard,
        casesEl, daysEl, userId: 5, includeResolvedDays: false, advancedLoaded: false,
        console: { error() { } }, WORKLOAD_PAGE_SIZE: 20,
        api: { get: async url => respond(url) }
    };
    const names = Object.keys(stubs);
    const body = [
        extract(read('core/ui.js'), 'export async function guardLoad').replace('export ', ''),
        'let casesPage = 1; let daysPage = 1;',
        'const workloadSeq = { cases: createSeqGuard(), days: createSeqGuard() };',
        extract(read('pages/handler-detail.js'), 'async function loadAdvanced'),
        'return { loadAdvanced, guardLoad, setCasesPage: value => { casesPage = value; } };'
    ].join('\n');
    return { ...new Function(...names, body)(...names.map(n => stubs[n])), state, casesEl };
}

const cases = {
    // 使用者詳情：案件表換到第 2 頁（回應慢），緊接著風險日換到第 3 頁——後一次請求要帶著前一次剛換的頁碼
    async userDetailConsecutivePagingKeepsBothPages() {
        let release;
        const slow = new Promise(resolve => { release = resolve; });
        const { reloadWorkload, spy } = loadUserDetail(async url => {
            const p = pagesOf(url);
            if (p.cases === '2' && p.days === '1') await slow;
            return { cases: [], days: [], casesTotal: 100, daysTotal: 100, shown: p };
        });
        const first = reloadWorkload({ casesPage: 2 });
        await tick();
        await reloadWorkload({ daysPage: 3 });
        release();
        await first;
        assert.deepEqual(pagesOf(spy.urls[1]), { cases: '2', days: '3' }, '第二次請求要帶著第一次的頁碼');
        assert.deepEqual(spy.rendered.at(-1).shown, { cases: '2', days: '3' });
        assert.equal(spy.rendered.length, 1, '過期的第一次回應不畫');
    },

    // 使用者詳情：資料變少後頁碼超出範圍，退到最後一頁重取一次（不停在「沒有資料」卻標題寫 N 筆）
    async userDetailPageBeyondLastFallsBackOnce() {
        const { reloadWorkload, spy } = loadUserDetail(async url => {
            const p = pagesOf(url);
            return { cases: [], days: [], casesTotal: 45, daysTotal: 5, shown: p };
        });
        await reloadWorkload({ casesPage: 9 });
        assert.equal(spy.urls.length, 2, '只退一次');
        assert.deepEqual(pagesOf(spy.urls[1]), { cases: '3', days: '1' });
        assert.deepEqual(spy.rendered.at(-1).shown, { cases: '3', days: '1' });
    },

    // 處理人詳情：第 2 頁的請求慢且最後失敗，第 3 頁已成功畫出——過期的失敗不得把畫面換成「載入失敗」
    async handlerDetailStaleFailureDoesNotReplaceNewerPage() {
        let fail;
        const gate = new Promise(resolve => { fail = resolve; });
        const m = loadHandlerDetail(async url => {
            const p = pagesOf(url);
            if (p.cases === '2') { await gate; throw Object.assign(new Error('boom'), { status: 500 }); }
            return { cases: [`page ${p.cases}`], days: [], casesTotal: 100, daysTotal: 0 };
        });
        m.setCasesPage(2);
        const second = m.guardLoad(m.casesEl, () => m.loadAdvanced('cases'));
        await tick();
        m.setCasesPage(3);
        await m.guardLoad(m.casesEl, () => m.loadAdvanced('cases'));
        assert.equal(m.state.casesEl, 'TABLE page 3');
        fail();
        await second;
        assert.equal(m.state.casesEl, 'TABLE page 3', '過期請求的失敗不得蓋掉較新的畫面');
    },

    // 處理人詳情：最新一次請求失敗時仍要顯示錯誤（守門不得把真正的失敗也吞掉）
    async handlerDetailCurrentFailureStillShowsError() {
        const m = loadHandlerDetail(async () => { throw Object.assign(new Error('boom'), { status: 500 }); });
        await m.guardLoad(m.casesEl, () => m.loadAdvanced('cases'));
        assert.ok(m.state.casesEl.startsWith('ERROR:'), m.state.casesEl);
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
