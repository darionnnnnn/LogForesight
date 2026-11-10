import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// markdown-lite 只用到 createElement／createTextNode／appendChild／classList.toggle／replaceChildren，
// 替身只補這幾個——缺什麼就直接爆錯，比默默通過好
class Node {
    constructor(tag) { this.tagName = tag.toUpperCase(); this.children = []; this.className = ''; this._text = ''; }
    get textContent() { return this._text || this.children.map(c => c.textContent).join(''); }
    set textContent(v) { this._text = String(v); this.children = []; }
    appendChild(c) { this.children.push(c); return c; }
    replaceChildren(...n) { this.children = [...n]; this._text = ''; }
    get classList() {
        const self = this;
        return {
            toggle(name, on) {
                const set = new Set(self.className.split(' ').filter(Boolean));
                if (on) set.add(name); else set.delete(name);
                self.className = [...set].join(' ');
            },
            contains: name => self.className.split(' ').includes(name)
        };
    }
}
globalThis.document = {
    createElement: tag => new Node(tag),
    createTextNode: text => ({ tagName: '#text', textContent: String(text) })
};

const { renderAiText } = await import('../../LogForesight.Web/wwwroot/js/core/markdown-lite.js');

const sample = ['## 常用操作', '1. 第一步', '2. 第二步', '', '**注意：**刪除後無法復原。', '', '一般段落。', '- 甲', '- 乙'].join('\n');
const blocks = container => container.children.map(c => `${c.tagName}.${c.className}`);

const cases = {
    documentMode() {
        const container = new Node('div');
        renderAiText(container, sample, { asDocument: true });
        assert(container.classList.contains('lf-doc'));
        // 段落標題是語意 h3、清單不帶 Bootstrap 間距 class、「注意：」段落是提示框
        assert.deepEqual(blocks(container), ['H3.lf-doc__heading', 'OL.', 'P.lf-doc__note', 'P.', 'UL.']);
        assert.equal(container.children[2].children[0].tagName, 'STRONG');
    },
    defaultModeUnchanged() {
        // AI 回覆等其他呼叫端不受影響：維持原本的緊湊呈現，不加 lf-doc
        const container = new Node('div');
        container.className = 'lf-doc';
        renderAiText(container, sample);
        assert(!container.classList.contains('lf-doc'));
        assert.deepEqual(blocks(container), ['DIV.fw-semibold mb-1', 'OL.mb-2 ps-3', 'DIV.mb-1', 'DIV.mb-1', 'UL.mb-2 ps-3']);
    },
    titleLineStripped() {
        const scriptDir = path.dirname(fileURLToPath(import.meta.url));
        const source = fs.readFileSync(path.resolve(scriptDir, '../../LogForesight.Web/wwwroot/js/pages/help-manual.js'), 'utf8').replaceAll('\r\n', '\n');
        const start = source.indexOf('function withoutTitleLine(content)');
        assert(start >= 0, '找不到 withoutTitleLine');
        const end = source.indexOf('\n}\n', start) + 2;
        const withoutTitleLine = new Function(`${source.slice(start, end)}\nreturn withoutTitleLine;`)();
        // 只拿掉開頭的「# 章名」，段落標題（##）與後續內容不動
        assert.equal(withoutTitleLine('# 名詞解釋\r\n\r\n## 基本單位\r\n內文'), '\r\n## 基本單位\r\n內文');
        assert.equal(withoutTitleLine('## 直接是段落\n內文'), '## 直接是段落\n內文');
        assert.equal(withoutTitleLine(null), '');
    },
    latexArrowConverted() {
        // AI 回覆中的 $\rightarrow$ 換成「→」，粗體裡的也換（docs/FEEDBACK-69-PLAN.md X1）
        const text = flatText(render('嘗試登入 $\\rightarrow$ 修改權限 $\\Rightarrow$ 抹除痕跡，**$\\leq$ 3 次**'));
        assert.equal(text, '嘗試登入 → 修改權限 ⇒ 抹除痕跡，≤ 3 次');
    },
    latexLookalikesUntouched() {
        // 反例：金額、成對 $ 之間是一般文字、認不得的指令、行內代碼——一律原樣
        assert.equal(flatText(render('授權費 $100')), '授權費 $100');
        assert.equal(flatText(render('花了 $5 和 $10')), '花了 $5 和 $10');
        assert.equal(flatText(render('未知 $\\foo$ 指令')), '未知 $\\foo$ 指令');
        assert.equal(flatText(render('用 `$\\rightarrow$` 表示')), '用 $\\rightarrow$ 表示');
    }
};

function render(text) {
    const container = new Node('div');
    renderAiText(container, text);
    return container;
}
function flatText(node) {
    if (node.tagName === '#text') return node.textContent;
    if (node.children?.length) return node.children.map(flatText).join('');
    return node.textContent ?? '';
}

const name = process.argv[2];
if (!name) {
    console.log(Object.keys(cases).join('\n'));
    process.exit(0);
}
assert(cases[name], `未知的案例：${name}`);
cases[name]();
console.log(`PASS ${name}`);
