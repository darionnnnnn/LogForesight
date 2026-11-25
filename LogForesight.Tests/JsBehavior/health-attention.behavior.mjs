/**
 * 第 76-2 輪批次 C-4：健康整體狀態的兩個消費端。
 *  - 健康頁頂端（settings.js 的 renderHealthOverall）：顯示狀態與原因；正常時不顯示原因清單。
 *  - 導覽健康告示（core/layout.js 的 loadHealthBanner）：狀態不正常時列出原因；沒有維護與開發監看能力的角色不發請求。
 * 以 production 原始碼切出函式本體執行，只替換外部相依（API／能力判斷／共用元件）。
 * 由 HealthAttentionBehaviorTests 以 `node health-attention.behavior.mjs <案例名>` 逐案執行。
 */
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { installDom } from './mini-dom.mjs';
import { loadElements } from './load-elements.mjs';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const jsRoot = path.resolve(scriptDir, '..', '..', 'LogForesight.Web', 'wwwroot', 'js');
const read = rel => fs.readFileSync(path.join(jsRoot, rel), 'utf8').replaceAll('\r\n', '\n').replace(/^﻿/, '');

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

const find = (node, predicate) => { const out = []; node.find(n => { if (predicate(n)) out.push(n); return false; }); return out; };

// ── 健康頁頂端 ─────────────────────────────────────────────

function overallEnv() {
    const dom = installDom();
    const { statusNote } = loadElements(dom);
    const host = document.createElement('div');
    const docStub = { getElementById: id => (id === 'health-overall' ? host : null), createElement: t => document.createElement(t) };
    const source = read('pages/settings.js');
    const factory = new Function('document', 'statusNote', 'formatNumber',
        `${extractFunction(source, 'function renderHealthOverall(')}\nreturn renderHealthOverall;`);
    return { render: factory(docStub, statusNote, n => String(n)), host };
}

const reasonRows = host => find(host, n => n.tagName === 'LI');

// ── 導覽告示 ───────────────────────────────────────────────

function bannerEnv({ capabilities, freshness = { stale: false }, attention = { status: 'ok', reasons: [] }, attentionFails = false }) {
    const dom = installDom();
    const { disclosure } = loadElements(dom);
    const calls = [];
    let bar = 'untouched';
    const api = {
        get: async url => {
            calls.push(url);
            if (url === '/api/health/attention') { if (attentionFails) throw new Error('boom'); return attention; }
            return freshness;
        }
    };
    const source = read('core/layout.js');
    const body = ['function freshnessReason(', 'async function loadHealthBanner('].map(d => extractFunction(source, d)).join('\n');
    const factory = new Function('api', 'hasCapability', 'registerAlert', 'formatDateTime', 'appUrl', 'document', 'disclosure',
        `${body}\nreturn loadHealthBanner;`);
    const load = factory(api, (_, c) => capabilities.includes(c), (_, node) => { bar = node; }, v => String(v), p => p, document, disclosure);
    return { load, calls, bar: () => bar };
}

const reasons = [
    { key: 'retention', text: '保留清除有 1 段失敗（歷史紀錄），其餘段已照常執行。' },
    { key: 'lastRun', text: '最近一趟分析執行失敗。' },
    { key: 'freshness', text: '排程資料已超過 48 小時沒有成功更新。' }
];

const cases = {
    // C14-a：需要注意 → 一則警告＋逐項原因（每項一行，文字原樣）
    overallShowsStatusAndReasons() {
        const { render, host } = overallEnv();
        render({ status: 'degraded', statusReasons: reasons });
        const notes = find(host, n => (n.className || '').includes('lf-status-note'));
        assert.equal(notes.length, 1);
        assert.ok(notes[0].className.includes('lf-status-note--warning'));
        assert.ok(notes[0].textContent.includes('3 項'), notes[0].textContent);
        const rows = reasonRows(host);
        assert.deepEqual(rows.map(r => r.textContent), reasons.map(r => r.text));
        assert.deepEqual(rows.map(r => r.attributes['data-reason-key']), ['retention', 'lastRun', 'freshness']);
    },

    // C14-a：正常 → 只有「系統狀態正常」，沒有原因清單
    overallNormalShowsNoReasonList() {
        const { render, host } = overallEnv();
        render({ status: 'ok', statusReasons: [] });
        const notes = find(host, n => (n.className || '').includes('lf-status-note'));
        assert.equal(notes.length, 1);
        assert.ok(notes[0].className.includes('lf-status-note--success'));
        assert.ok(notes[0].textContent.includes('系統狀態正常'));
        assert.equal(find(host, n => n.attributes?.['data-health-reasons'] === '1').length, 0);
        assert.equal(reasonRows(host).length, 0);
    },

    // 重畫：先有原因、再變正常，原因清單消失（不留殘影）
    overallRerenderClearsReasons() {
        const { render, host } = overallEnv();
        render({ status: 'degraded', statusReasons: reasons });
        assert.equal(reasonRows(host).length, 3);
        render({ status: 'ok', statusReasons: [] });
        assert.equal(reasonRows(host).length, 0);
    },

    // down：錯誤型訊息（role=alert）
    overallDownUsesErrorNote() {
        const { render, host } = overallEnv();
        render({ status: 'down', statusReasons: [{ key: 'storage', text: '資料庫無法連線，請檢查儲存設定與資料庫服務。' }] });
        const note = find(host, n => (n.className || '').includes('lf-status-note--error'))[0];
        assert.ok(note, '應有錯誤型訊息');
        assert.equal(note.getAttribute('role'), 'alert');
        assert.equal(reasonRows(host).length, 1);
    },

    // 原因文字走 textContent：帶標籤字元的外部字串不被當成 HTML
    overallReasonTextIsPlainText() {
        const { render, host } = overallEnv();
        render({ status: 'degraded', statusReasons: [{ key: 'prtgDevicesPurged', text: '<img src=x onerror=alert(1)>' }] });
        const row = reasonRows(host)[0];
        assert.equal(row.textContent, '<img src=x onerror=alert(1)>');
        assert.equal(find(host, n => n.tagName === 'IMG').length, 0);
    },

    // C14-b：狀態不正常 → 告示列出原因（「排程資料過期」由原本那一條負責，不重複列）
    async bannerListsReasonsWhenNotOk() {
        const { load, bar } = bannerEnv({ capabilities: ['Maintain'], attention: { status: 'degraded', reasons } });
        await load({});
        const node = bar();
        assert.ok(node && node !== 'untouched');
        const items = find(node, n => n.tagName === 'LI').map(n => n.textContent);
        assert.deepEqual(items, [reasons[0].text, reasons[1].text]);
        const links = find(node, n => n.tagName === 'A').map(a => a.href);
        assert.deepEqual(links, ['/admin/settings#health']);
        assert.ok(node.children[0].textContent.includes('需要注意'));
    },

    // 同時有排程過期告示與其他原因：原本那條保留，下面多列原因
    async bannerKeepsStaleBarAndAddsReasons() {
        const { load, bar } = bannerEnv({
            capabilities: ['Maintain'],
            freshness: { stale: true, acked: false, lastSuccessAt: 'x', adminContacts: [] },
            attention: { status: 'degraded', reasons }
        });
        await load({});
        const node = bar();
        assert.ok(node.children[0].textContent.includes('排程資料已超過 48 小時沒有成功更新'));
        assert.equal(find(node, n => n.tagName === 'LI').length, 2);
        assert.deepEqual(find(node, n => n.tagName === 'A').map(a => a.href), ['/runs', '/admin/settings#health']);
    },

    // 原因多於兩項：清單收進揭露（預設收合、按鈕寫明項數），告示不把頁面頂掉；兩項以內照舊直接列出
    async bannerCollapsesReasonsBeyondTwo() {
        const many = [
            { key: 'a', text: '原因甲。' }, { key: 'b', text: '原因乙。' }, { key: 'c', text: '原因丙。' }
        ];
        const { load, bar } = bannerEnv({ capabilities: ['Maintain'], attention: { status: 'degraded', reasons: many } });
        await load({});
        const node = bar();
        const list = find(node, n => n.attributes?.['data-health-reasons'] === '1')[0];
        assert.equal(find(list, n => n.tagName === 'LI').length, 3);
        assert.ok(list.className.includes('d-none'), '預設收合');
        const toggle = find(node, n => n.tagName === 'BUTTON')[0];
        assert.ok(toggle.textContent.includes('3 項'), toggle.textContent);
        assert.equal(toggle.getAttribute('aria-controls'), list.id);
        assert.equal(toggle.getAttribute('aria-expanded'), 'false');
        toggle.listeners.click[0]({ stopPropagation() {} });
        assert.ok(!list.className.includes('d-none'), '展開後可見');
        const few = bannerEnv({ capabilities: ['Maintain'], attention: { status: 'degraded', reasons: many.slice(0, 2) } });
        await few.load({});
        assert.equal(find(few.bar(), n => n.tagName === 'BUTTON').length, 0);
        assert.ok(!find(few.bar(), n => n.attributes?.['data-health-reasons'] === '1')[0].className.includes('d-none'));
    },

    // 正常且排程沒過期 → 不顯示
    async bannerHiddenWhenOk() {
        const { load, bar } = bannerEnv({ capabilities: ['Maintain'] });
        await load({});
        assert.equal(bar(), null);
    },

    // 只有開發監看能力：照樣查、照樣列原因，但不給進不去的設定頁連結
    async bannerDevMonitorSeesReasonsWithoutSettingsLink() {
        const { load, bar, calls } = bannerEnv({ capabilities: ['DevMonitor'], attention: { status: 'degraded', reasons } });
        await load({});
        assert.deepEqual(calls.sort(), ['/api/health/attention', '/api/health/freshness']);
        assert.equal(find(bar(), n => n.tagName === 'LI').length, 2);
        assert.equal(find(bar(), n => n.tagName === 'A').length, 0);
        assert.ok(bar().textContent.includes('請聯絡系統管理員'), '沒有 Maintain 的人看到原因後要有下一步');
    },

    // 主文已寫出「請聯絡：A、B。」就不再多一行「請聯絡系統管理員。」；主文沒有聯絡人才補
    async bannerContactLineNotRepeatedWhenMainTextHasContacts() {
        const stale = { stale: true, acked: false, lastSuccessAt: 'x', adminContacts: ['王小明', '林小華'] };
        const withContacts = bannerEnv({ capabilities: ['DevMonitor'], freshness: stale, attention: { status: 'degraded', reasons } });
        await withContacts.load({});
        const node = withContacts.bar();
        assert.ok(node.children[0].textContent.includes('請聯絡：王小明、林小華。'));
        assert.ok(!node.textContent.includes('請聯絡系統管理員'), '主文已有聯絡人，不可再多一行');
        const without = bannerEnv({ capabilities: ['DevMonitor'], freshness: { ...stale, adminContacts: [] }, attention: { status: 'degraded', reasons } });
        await without.load({});
        assert.ok(without.bar().textContent.includes('請聯絡系統管理員'), '沒有聯絡人時仍補下一步');
    },

    // 有 Maintain 的人有連結可處理，不多寫「請聯絡」
    async bannerMaintainHasNoContactLine() {
        const { load, bar } = bannerEnv({ capabilities: ['Maintain'], attention: { status: 'degraded', reasons } });
        await load({});
        assert.ok(!bar().textContent.includes('請聯絡系統管理員'));
    },

    // C14-b：沒有維護與開發監看能力 → 一個請求都不發，告示向協調器移除
    async bannerNoCapabilityMakesNoRequest() {
        const { load, bar, calls } = bannerEnv({ capabilities: ['ViewAll', 'Assign'], attention: { status: 'degraded', reasons } });
        await load({});
        assert.deepEqual(calls, []);
        assert.equal(bar(), null);
    },

    // 原因端點失敗：當作沒有原因，排程過期告示照常
    async bannerAttentionFailureKeepsStaleBar() {
        const { load, bar } = bannerEnv({
            capabilities: ['Maintain'], attentionFails: true,
            freshness: { stale: true, acked: false, lastSuccessAt: 'x', adminContacts: [] }
        });
        await load({});
        assert.ok(bar().children[0].textContent.includes('排程資料已超過 48 小時'));
        assert.equal(find(bar(), n => n.tagName === 'LI').length, 0);
    },

    // 原因端點失敗且排程正常：不顯示、不擲例外
    async bannerAttentionFailureAloneShowsNothing() {
        const { load, bar } = bannerEnv({ capabilities: ['Maintain'], attentionFails: true });
        await load({});
        assert.equal(bar(), null);
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
