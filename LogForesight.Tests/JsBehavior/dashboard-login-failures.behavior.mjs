/**
 * 第 81 輪補修 B1：儀表板「24 小時登入失敗」卡的下鑽網址（dashboard.js 的 loginFailuresUrl），
 * 以及稽核頁（audit.js 的 init）收到這組參數時的篩選狀態。
 * 切出 production 函式執行，不載入整頁；固定時鐘，不依賴真實的今天。
 * 由 DashboardLoginFailuresBehaviorTests 逐案執行。
 */
import assert from 'node:assert/strict';
import { installDom } from './mini-dom.mjs';
import { read, extract } from './load-reply-modal.mjs';
import { jsRoot } from './load-reply-modal.mjs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

installDom();
// format.js 載入時會碰 window，要在 installDom 之後才匯入
const { toLocalDateString } = await import(pathToFileURL(path.join(jsRoot, 'core', 'format.js')).href);

function loadUrlBuilder() {
    const body = extract(read('pages', 'dashboard.js'), 'function loginFailuresUrl(');
    return new Function('toLocalDateString', `${body}\nreturn loginFailuresUrl;`)(toLocalDateString);
}

const cases = {
    // 下鑽只指定動作、不帶結果；起迄是本地日期的昨天與今天（跨月、跨年也對）
    drillDownUsesActionAndLocalDates() {
        const url = loadUrlBuilder();
        // 取卡片實際寫的 url 運算式，把 loginFailuresUrl 換成固定時鐘版本再求值：卡片沒接上這個函式時一樣會紅
        const source = read('pages', 'dashboard.js');
        const i = source.indexOf("label: '24 小時登入失敗'");
        assert(i >= 0);
        const expression = /\n\s*url: (.+)\n/.exec(source.slice(i))[1];
        // 本地 2026-01-01 00:30，UTC+8 時 toISOString 會是前一天
        const text = new Function('loginFailuresUrl', `return ${expression};`)(() => url(new Date(2026, 0, 1, 0, 30)));
        const params = new URL(text, 'http://x').searchParams;
        assert.equal(new URL(text, 'http://x').pathname, '/audit');
        assert.equal(params.get('actions'), 'login_failed');
        assert.equal(params.get('from'), '2025-12-31');
        assert.equal(params.get('to'), '2026-01-01');
        assert.equal(params.has('result'), false);
        assert.ok(!text.includes('result='));
        assert.equal(url(new Date(2026, 2, 1, 23, 59)).includes('from=2026-02-28'), true);
    },

    // 卡片說明文字（hint）寫明「明細含昨天全日」
    cardHintMentionsYesterday() {
        const source = read('pages', 'dashboard.js');
        const i = source.indexOf("label: '24 小時登入失敗'");
        assert(i >= 0);
        const card = source.slice(i, source.indexOf('});', i));
        assert.ok(card.includes("hint: '明細含昨天全日'"), card);
        assert.ok(card.includes('url: loginFailuresUrl()'), card);
    },

    // 稽核頁以這組參數開啟：動作下拉選到「登入失敗」、結果下拉是「全部」、起迄日是帶入的兩天
    async auditPageAppliesDrillDownParams() {
        const els = {
            'audit-from': { value: '' }, 'audit-to': { value: '' }, 'audit-result': { value: '' },
            'audit-actions': { options: [
                { value: 'login', selected: true }, { value: 'login_failed', selected: false }, { value: 'logout', selected: true }] }
        };
        const body = extract(read('pages', 'audit.js'), 'async function init(');
        let searched = 0;
        const init = new Function('document', 'location', 'toLocalDateString', 'loadActionOptions', 'updateDeniedButtonState', 'search',
            `${body}\nreturn init;`)(
            { getElementById: id => els[id] },
            { search: '?actions=login_failed&from=2025-12-31&to=2026-01-01' },
            toLocalDateString, async () => {}, () => {}, async () => { searched++; });
        await init();
        assert.equal(els['audit-result'].value, '');
        assert.equal(els['audit-from'].value, '2025-12-31');
        assert.equal(els['audit-to'].value, '2026-01-01');
        assert.deepEqual(els['audit-actions'].options.filter(o => o.selected).map(o => o.value), ['login_failed']);
        assert.equal(searched, 1);
    }
};

const name = process.argv[2];
if (!name) {
    console.log(Object.keys(cases).join('\n'));
    process.exit(0);
}
assert(cases[name], `未知案例：${name}`);
await cases[name]();
console.log(`PASS ${name}`);
