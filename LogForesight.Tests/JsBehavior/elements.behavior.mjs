// core/elements.js 的 production 行為（docs/archive/FEEDBACK-69-PLAN.md A-3、A-4）。
// 整檔載入（load-elements.mjs），只把 ui.js 的 icon 換成替身。
import assert from 'node:assert/strict';
import { installDom } from './mini-dom.mjs';
import { loadElements } from './load-elements.mjs';

const dom = installDom();
const { disclosure, filterDisclosure, metaLine, statusNote } = loadElements(dom);

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
        assert(!target.classList.contains('lf-disclosure-target'), '呼叫端自理顯示的目標不標記列印全顯示');
        click(button);
        assert(!target.classList.contains('d-none'), 'manageTarget: false 時不動目標的 class');
        assert.equal(button.getAttribute('aria-expanded'), 'true');
    },
    disclosureNotPrinted() {
        const target = dom.newElement('div');
        assert(disclosure('佐證', target).classList.contains('lf-no-print'));
        assert(target.classList.contains('lf-disclosure-target'), '由按鈕管理的目標要標記，列印時一律印出');
    },
    filterDisclosureCountsApplied() {
        const target = dom.newElement('div');
        let applied = 0;
        const { button, refresh } = filterDisclosure(target, () => applied);
        assert.equal(labelOf(button), '更多條件', '0 個條件時不寫數字');
        assert(!target.classList.contains('lf-disclosure-target'), '篩選欄位收合時列印不印出');
        assert.equal(button.getAttribute('aria-expanded'), 'false', '沒有套用中的條件時預設收合');
        assert(target.classList.contains('d-none'));
        applied = 2;
        refresh();
        assert.equal(labelOf(button), '更多條件 2');
        click(button);
        assert.equal(labelOf(button), '更多條件 2', '展開前後標籤不變');
        applied = 0;
        refresh();
        assert.equal(labelOf(button), '更多條件');
    },
    filterDisclosureExpandedWhenAppliedOnLoad() {
        const target = dom.newElement('div');
        const { button } = filterDisclosure(target, () => 1, { label: '其他條件' });
        assert.equal(button.getAttribute('aria-expanded'), 'true', '進頁已有套用中的條件要看得到');
        assert(!target.classList.contains('d-none'));
        assert.equal(labelOf(button), '其他條件 1');
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
        assert.equal(metaLine(['只有一項']).children.length, 1, '單一項目不產生分隔節點');
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
