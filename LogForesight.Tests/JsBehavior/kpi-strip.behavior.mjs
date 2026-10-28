/**
 * core/ui.js kpiStrip 的行為測試：抽出並執行 production 函式，只替換 DOM 與 appUrl。
 * 守的是結構前提——空組不輸出、每張卡一定有副標列（同列等高）、連結卡經 appUrl、variant 套到數字。
 */
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { installDom } from './mini-dom.mjs';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(scriptDir, '..', '..');
const source = fs.readFileSync(path.join(repoRoot, 'LogForesight.Web', 'wwwroot', 'js', 'core', 'ui.js'), 'utf8').replaceAll('\r\n', '\n');

function extractFunction(declaration) {
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

function load() {
    const dom = installDom();
    // 替身 appUrl：加上站台前綴，用來證明 href 真的經過它
    const appUrl = p => `/site${p}`;
    const body = extractFunction('export function kpiStrip').replace(/^export /, '');
    const kpiStrip = new Function('document', 'appUrl', `${body}\nreturn kpiStrip;`)(globalThis.document, appUrl);
    return { kpiStrip, dom };
}

const has = (node, cls) => node.classList.contains(cls);
const cardsOf = strip => {
    const found = [];
    const walk = n => { if (has(n, 'lf-kpi-card')) found.push(n); n.children.forEach(walk); };
    walk(strip);
    return found;
};
const subOf = card => card.find(n => has(n, 'lf-kpi-card__sub'));

const cases = {
    emptyGroupsAreNotRendered() {
        const { kpiStrip } = load();
        const strip = kpiStrip([
            { title: '空組', cards: [] },
            { title: '有卡', cards: [{ value: '1', label: 'A' }] },
            { title: '缺 cards' }
        ]);
        const groups = strip.children.filter(c => has(c, 'lf-kpi-group'));
        assert.equal(groups.length, 1);
        assert.equal(groups[0].find(n => has(n, 'lf-kpi-group__title')).textContent, '有卡');
        assert.equal(has(groups[0], 'lf-kpi-group--n1'), true);

        const none = kpiStrip([{ cards: [] }]);
        assert.equal(none.children.length, 0, '所有組都沒有卡時回傳空容器');
        assert.equal(kpiStrip(undefined).children.length, 0);
    },
    cardWithoutSubStillHasSubPlaceholder() {
        const { kpiStrip } = load();
        const strip = kpiStrip([{ cards: [
            { value: '3', label: '無副標' },
            { value: '4', label: '有副標', sub: '影響 2 台', title: '完整說明' }
        ] }]);
        const [plain, withSub] = cardsOf(strip);
        const plainSub = subOf(plain);
        assert(plainSub, '沒有副標的卡也要有副標佔位節點');
        assert.equal(plainSub.textContent, '');
        assert.equal(subOf(withSub).textContent, '影響 2 台');
        assert.equal(withSub.attributes.title ?? withSub.title, '完整說明');
    },
    linkCardIsAnchorWithAppUrlHref() {
        const { kpiStrip } = load();
        const strip = kpiStrip([{ cards: [
            { value: '1', label: '連結', url: '/records?x=1#t' },
            { value: '2', label: '不可點' }
        ] }]);
        const [link, plain] = cardsOf(strip);
        assert.equal(link.tagName, 'A');
        assert.equal(link.href, '/site/records?x=1#t');
        assert.equal(has(link, 'lf-kpi-card--link'), true);
        assert.equal(plain.tagName, 'DIV');
        assert.equal(plain.href, undefined);
    },
    variantAppliesToValue() {
        const { kpiStrip } = load();
        const strip = kpiStrip([{ cards: [
            { value: '9', label: '危險', variant: 'danger' },
            { value: '0', label: '無色' }
        ] }]);
        const [danger, none] = cardsOf(strip);
        const dangerValue = danger.find(n => has(n, 'lf-stat__value'));
        assert.equal(has(dangerValue, 'text-danger'), true);
        assert.equal(dangerValue.textContent, '9');
        const noneValue = none.find(n => has(n, 'lf-stat__value'));
        assert.equal(noneValue.className.includes('text-'), false);
    },
    nodeSubIsAppendedNotParsed() {
        const { kpiStrip, dom } = load();
        const badge = dom.newElement('div');
        badge.textContent = '↑ 20%';
        const [card] = cardsOf(kpiStrip([{ cards: [
            { value: '1', label: 'x', sub: badge },
            ]}]));
        assert.equal(subOf(card).children[0], badge);
        const [text] = cardsOf(kpiStrip([{ cards: [{ value: '1', label: 'x', sub: '<b>x</b>' }] }]));
        assert.equal(subOf(text).textContent, '<b>x</b>', '字串副標走 textContent，不當 HTML 解析');
        assert.equal(subOf(text).innerHTML, '');
    }
};

const name = process.argv[2];
assert(cases[name], `未知的案例：${name}`);
cases[name]();
console.log(`PASS ${name}`);
