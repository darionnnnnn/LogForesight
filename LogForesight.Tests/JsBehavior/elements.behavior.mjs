// core/elements.js 的 production 行為（docs/FEEDBACK-69-PLAN.md A-3、A-4）。
// 整檔載入（load-elements.mjs），只把 ui.js 的 icon 換成替身。
import assert from 'node:assert/strict';
import { installDom } from './mini-dom.mjs';
import { loadElements } from './load-elements.mjs';

const dom = installDom();
const { disclosure, metaLine, statusNote } = loadElements(dom);

function click(button) {
    let stopped = 0;
    (button.listeners.click || []).forEach(h => h({ stopPropagation: () => { stopped++; } }));
    return stopped;
}
const labelOf = button => button.children.find(c => c.tagName === 'SPAN').textContent;

const cases = {
    disclosureTogglesTargetAndAria() {
        const target = dom.newElement('div');
        const button = disclosure('佐證', target);
        assert.equal(button.type, 'button');
        assert.equal(button.getAttribute('aria-expanded'), 'false');
        assert.equal(button.getAttribute('aria-controls'), target.id);
        assert(target.id, '目標沒有 id 時要自動補');
        assert(target.classList.contains('d-none'));
        click(button);
        assert.equal(button.getAttribute('aria-expanded'), 'true');
        assert(!target.classList.contains('d-none'));
        assert.equal(labelOf(button), '佐證', '展開後標籤文字不變');
        click(button);
        assert.equal(button.getAttribute('aria-expanded'), 'false');
        assert(target.classList.contains('d-none'));
    },
    disclosureStopsPropagation() {
        const button = disclosure('全文', dom.newElement('div'));
        assert.equal(click(button), 1, '放在可展開列裡時不得連帶觸發列展開');
    },
    disclosureInitiallyExpandedAndCallback() {
        const target = dom.newElement('div');
        const seen = [];
        const button = disclosure('佐證', target, { expanded: true, onToggle: open => seen.push(open) });
        assert.equal(button.getAttribute('aria-expanded'), 'true');
        assert(!target.classList.contains('d-none'));
        click(button);
        assert.deepEqual(seen, [false]);
    },
    disclosureUnmanagedTargetUntouched() {
        const target = dom.newElement('div');
        const button = disclosure('全文', target, { manageTarget: false });
        click(button);
        assert(!target.classList.contains('d-none'), 'manageTarget: false 時不動目標的 class');
        assert.equal(button.getAttribute('aria-expanded'), 'true');
    },
    disclosureNotPrinted() {
        assert(disclosure('佐證', dom.newElement('div')).classList.contains('lf-no-print'));
    },
    metaLineEmptyReturnsNull() {
        assert.equal(metaLine([]), null);
        assert.equal(metaLine(null), null);
        assert.equal(metaLine(['', null, undefined, false]), null);
    },
    metaLineSkipsEmptyItems() {
        const node = dom.newElement('span');
        const line = metaLine(['次數 3', '', null, node, 0]);
        assert.equal(line.children.length, 3, '空字串與 null 略過，數字 0 保留');
        assert.equal(line.children[0].textContent, '次數 3');
        assert.equal(line.children[1], node);
        assert.equal(line.children[2].textContent, '0');
        assert(line.classList.contains('lf-text-meta'));
    },
    metaLineTextIsNotMarkup() {
        const line = metaLine(['<b>x</b>']);
        assert.equal(line.children[0].textContent, '<b>x</b>');
        assert.equal(line.children[0].children.length, 0);
    },
    statusNoteRoles() {
        assert.equal(statusNote('error', '連線失敗').getAttribute('role'), 'alert');
        assert.equal(statusNote('success', '已儲存').getAttribute('role'), 'status');
        assert.equal(statusNote('info', '提示').getAttribute('role'), 'status');
        const warn = statusNote('warning', '可能不完整');
        assert.equal(warn.getAttribute('role'), 'status');
        assert(warn.classList.contains('lf-status-note--warning'));
        const unknown = statusNote('weird', '未知類型');
        assert.equal(unknown.getAttribute('role'), 'status');
        assert(unknown.classList.contains('lf-status-note--info'));
    },
    statusNoteTextIsNotMarkup() {
        const note = statusNote('error', '<script>alert(1)</script>');
        const span = note.children.find(c => c.tagName === 'SPAN');
        assert.equal(span.textContent, '<script>alert(1)</script>');
    }
};

const name = process.argv[2];
if (!name) { console.log(Object.keys(cases).join('\n')); process.exit(0); }
if (!cases[name]) { console.error(`未知案例：${name}`); process.exit(2); }
cases[name]();
console.log(`PASS ${name}`);
