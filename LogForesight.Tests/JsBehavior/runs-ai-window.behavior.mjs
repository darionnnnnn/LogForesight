/**
 * 排程作業頁（runs.js）「背景補跑窗口」那一列（D1）：沒有設定任何窗口、在窗口內、不在窗口內是三種不同的話。
 * 後端在前兩種都把下一次時間回成 null，畫面要靠 aiWindowConfigured 分辨。切出 renderAiWindowState 注入替身執行。
 */
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { installDom } from './mini-dom.mjs';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const jsRoot = path.resolve(scriptDir, '..', '..', 'LogForesight.Web', 'wwwroot', 'js');
const dom = installDom();

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

function load() {
    const stubs = {
        document,
        appUrl: p => `/app${p}`,
        formatDateTime: v => `T:${v}`
    };
    const names = Object.keys(stubs);
    const body = `${extract('function renderAiWindowState')}\nreturn renderAiWindowState;`;
    return new Function(...names, body)(...names.map(n => stubs[n]));
}

const render = (...args) => {
    const render = load();
    const el = dom.newElement('span');
    render(el, ...args);
    return el;
};

const cases = {
    // 空窗口：後端 nextTriggerTime 為 null、aiWindowConfigured 為 false → 說明不會自動執行，並連到窗口設定
    async noWindowSaysNotAutomaticAndLinks() {
        const el = render(true, null, false);
        assert.ok(el.textContent.includes('未設定窗口，AI 不會自動執行'), el.textContent);
        assert.ok(!el.textContent.includes('窗口內，隨時可跑'), '沒有窗口不能說隨時可跑');
        const link = el.children.find(c => c.tagName?.toLowerCase() === 'a');
        assert.ok(link, '要有連結');
        assert.equal(link.href, '/app/runs#settings');
    },
    // 窗口內：有窗口、下一次時間為 null → 隨時可跑
    async inWindowSaysAnytime() {
        const el = render(true, null, true);
        assert.equal(el.textContent, '窗口內，隨時可跑');
    },
    // 窗口外：有窗口、有下一次時間 → 顯示時間
    async outsideWindowShowsNextTime() {
        const el = render(true, '2026-11-26T01:00:00', true);
        assert.equal(el.textContent, 'T:2026-11-26T01:00:00');
    },
    // 舊回應沒帶 aiWindowConfigured：照有窗口處理，不誤報沒設定
    async missingFlagIsTreatedAsConfigured() {
        const el = render(true, null, undefined);
        assert.equal(el.textContent, '窗口內，隨時可跑');
    },
    // 兩個呼叫點（設定載入、狀態輪詢）都走同一個出口
    async bothCallSitesUseTheSharedExit() {
        assert.ok(src.includes('renderAiWindowState(aiNextTriggerEl, aiAvailable, options.nextAiTriggerTime, options.aiWindowConfigured)'));
        assert.ok(src.includes('renderAiWindowState(nextTriggerEl, aiAvailable, status.nextTriggerTime, status.aiWindowConfigured)'));
        assert.ok(!src.includes("'窗口內，隨時可跑';\n        }\n"), '不得在呼叫點各自寫一份');
    }
};

const name = process.argv[2];
if (!name) { Object.keys(cases).forEach(key => console.log(key)); process.exit(0); }
if (!cases[name]) { console.error(`未知案例：${name}`); process.exit(2); }
await cases[name]();
console.log(`PASS ${name}`);
