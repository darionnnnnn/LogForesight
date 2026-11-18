/**
 * 字級偏好（core/layout.js 的 initFontScale／applyFontScale）：
 * 儲存值不認得時退回 medium，且只有對應的按鈕有 active。切出函式本體、注入替身執行。
 */
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { installDom } from './mini-dom.mjs';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const jsRoot = path.resolve(scriptDir, '..', '..', 'LogForesight.Web', 'wwwroot', 'js');
const dom = installDom();
const { normalizeFontScale, FONT_SCALES } = await import(pathToFileURL(path.join(jsRoot, 'core', 'font-scale.js')).href);

const src = fs.readFileSync(path.join(jsRoot, 'core', 'layout.js'), 'utf8').replaceAll('\r\n', '\n');
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

/** 以指定的儲存值執行 initFontScale，回傳 <html> 的 dataset 與五顆按鈕 */
function run(stored) {
    const buttons = FONT_SCALES.map(scale => {
        const b = dom.newElement('button');
        b.dataset.scale = scale;
        return b;
    });
    // 預先把一顆不相干的按鈕標成 active：沒有逐顆切換就會殘留
    buttons[2].classList.add('active');
    const html = { dataset: {} };
    const fakeDocument = {
        documentElement: html,
        getElementById: () => null,
        querySelectorAll: selector => {
            assert.equal(selector, '#lf-font-scale [data-scale]');
            return buttons;
        }
    };
    const stubs = {
        document: fakeDocument,
        window: { dispatchEvent: () => true },
        CustomEvent: class { constructor(type) { this.type = type; } },
        FONT_SCALE_EVENT: 'lf:font-scale',
        safeStorage: { get: () => stored, set: () => { } },
        normalizeFontScale
    };
    const names = Object.keys(stubs);
    const init = new Function(...names, `${extract('function initFontScale')}\n${extract('function applyFontScale')}\nreturn initFontScale;`)(...names.map(n => stubs[n]));
    init();
    return { html, buttons };
}

const activeScales = buttons => buttons.filter(b => b.classList.contains('active')).map(b => b.dataset.scale);

const cases = {
    unknownStoredValueFallsBackToMedium() {
        for (const stored of ['huge', 'XXLARGE', '', null]) {
            const { html, buttons } = run(stored);
            assert.equal(html.dataset.fontScale, 'medium', `儲存值 ${JSON.stringify(stored)}`);
            assert.deepEqual(activeScales(buttons), ['medium'], `儲存值 ${JSON.stringify(stored)}`);
        }
    },

    xxlargeStoredValueActivatesXxlargeButton() {
        const { html, buttons } = run('xxlarge');
        assert.equal(html.dataset.fontScale, 'xxlarge');
        assert.deepEqual(activeScales(buttons), ['xxlarge']);
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
