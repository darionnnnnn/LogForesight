/**
 * 前端共用出口（第 79 輪 I 段）的行為測試：剪貼簿、欄位錯誤的無障礙屬性、數字欄位、
 * 網址 hash 跳脫、文字排序、對話框關閉方法。
 *
 * 由 SharedFrontBehaviorTests 以 `node shared-front.behavior.mjs <案例名>` 逐案執行；
 * 成功時印出 `PASS <案例名>` 並以 0 結束，失敗時印出原因並以 1 結束。
 */

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { installDom } from './mini-dom.mjs';

const dom = installDom();
window.LF_BASE = '/lf';
const jsRoot = '../../LogForesight.Web/wwwroot/js/core/';
const { copyText } = await import(jsRoot + 'clipboard.js');
const { setFieldError, clearFieldError } = await import(jsRoot + 'field-errors.js');
const { readNumberField, readNumberFields } = await import(jsRoot + 'number-field.js');
const { escapeSelectorValue, currentHashId, findByData } = await import(jsRoot + 'selectors.js');
const { compareText } = await import(jsRoot + 'collation.js');
const ui = await import(jsRoot + 'ui.js');

function assert(condition, message) {
    if (!condition) throw new Error(message);
}
function expectEqual(actual, expected, label) {
    if (actual !== expected) throw new Error(`${label}：預期 ${JSON.stringify(expected)} 實際 ${JSON.stringify(actual)}`);
}

// ── 剪貼簿替身 ──────────────────────────────────────────────────────────────

function setNavigator(value) {
    Object.defineProperty(globalThis, 'navigator', { value, configurable: true, writable: true });
}

/** execCommand 退路用的 document 替身；記錄 textarea 的建立、選取與移除 */
function installClipboardDocument(execResult) {
    const record = { appended: 0, removed: 0, commands: [], selectedText: null, refocused: false };
    const focusTarget = { focus() { record.refocused = true; } };
    globalThis.document = {
        activeElement: focusTarget,
        body: { appendChild() { record.appended += 1; } },
        createElement() {
            const node = {
                value: '',
                classList: { add() {} },
                setAttribute() {},
                select() { record.selectedText = node.value; },
                setSelectionRange() {},
                remove() { record.removed += 1; },
            };
            return node;
        },
        execCommand(command) {
            record.commands.push(command);
            if (typeof execResult === 'function') return execResult();
            return execResult;
        },
    };
    return record;
}

function field(id, value) {
    const parent = dom.newElement('div');
    const input = dom.newElement('input');
    parent.appendChild(input);
    input.setAttribute('id', id);
    input.value = value;
    dom.byId.set(id, input);
    return input;
}

const cases = {
    // I1 剪貼簿
    async 安全環境走clipboard複製成功() {
        const written = [];
        setNavigator({ clipboard: { writeText: async text => { written.push(text); } } });
        globalThis.window.isSecureContext = true;
        const record = installClipboardDocument(true);
        const result = await copyText('你好');
        expectEqual(result.ok, true, '複製結果');
        expectEqual(result.method, 'clipboard', '走的路徑');
        expectEqual(written[0], '你好', '寫入的文字');
        expectEqual(record.commands.length, 0, '安全環境不該動用退路');
    },

    async 非安全環境無clipboard走退路複製成功() {
        setNavigator({});
        globalThis.window.isSecureContext = false;
        const record = installClipboardDocument(true);
        const result = await copyText('http 內網');
        expectEqual(result.ok, true, '複製結果');
        expectEqual(result.method, 'execCommand', '走的路徑');
        expectEqual(record.commands[0], 'copy', '執行的指令');
        expectEqual(record.selectedText, 'http 內網', '退路選取的文字');
        expectEqual(record.removed, 1, '隱藏 textarea 用完移除');
        expectEqual(record.refocused, true, '焦點還給原本的元素');
    },

    async clipboard被拒絕時退回execCommand() {
        setNavigator({ clipboard: { writeText: async () => { throw new Error('NotAllowedError'); } } });
        globalThis.window.isSecureContext = true;
        installClipboardDocument(true);
        const result = await copyText('x');
        expectEqual(result.ok, true, '複製結果');
        expectEqual(result.method, 'execCommand', '被拒絕後走退路');
    },

    async 兩者都失敗時回報原因_安全環境() {
        setNavigator({ clipboard: { writeText: async () => { throw new Error('denied'); } } });
        globalThis.window.isSecureContext = true;
        installClipboardDocument(false);
        const result = await copyText('x');
        expectEqual(result.ok, false, '複製結果');
        assert(result.reason.includes('不允許'), `原因應指出瀏覽器不允許：${result.reason}`);
    },

    async 兩者都失敗時回報原因_非安全環境() {
        setNavigator({});
        globalThis.window.isSecureContext = false;
        installClipboardDocument(() => { throw new Error('execCommand 不可用'); });
        const result = await copyText('x');
        expectEqual(result.ok, false, '複製結果');
        assert(result.reason.includes('HTTPS'), `原因應指出非 HTTPS 連線：${result.reason}`);
    },

    // I4 欄位錯誤的無障礙屬性
    欄位錯誤帶aria屬性與訊息id() {
        const input = field('f1', '');
        input.setAttribute('aria-describedby', 'f1-hint');
        const el = setFieldError(input, '請輸入數值');
        expectEqual(input.getAttribute('aria-invalid'), 'true', 'aria-invalid');
        expectEqual(el.id, 'f1-error', '訊息元素 id');
        expectEqual(input.getAttribute('aria-describedby'), 'f1-hint f1-error', 'aria-describedby 保留既有說明並加上錯誤');
        expectEqual(el.textContent, '請輸入數值', '訊息文字');
        assert(input.classList.contains('is-invalid'), '紅框');
    },

    欄位錯誤清除後還原屬性() {
        const input = field('f2', '');
        input.setAttribute('aria-describedby', 'f2-hint');
        setFieldError(input, '錯');
        clearFieldError(input);
        expectEqual(input.getAttribute('aria-invalid'), null, '清除後無 aria-invalid');
        expectEqual(input.getAttribute('aria-describedby'), 'f2-hint', '只拿掉錯誤，保留原說明');
        assert(!input.classList.contains('is-invalid'), '紅框移除');
        expectEqual(input._parent.children.length, 1, '自動建立的訊息元素已移除');
    },

    欄位錯誤重複設定只留一個訊息元素() {
        const input = field('f3', '');
        setFieldError(input, '第一次');
        setFieldError(input, '第二次');
        expectEqual(input._parent.children.length, 2, '欄位加一個訊息元素');
        expectEqual(input.getAttribute('aria-describedby'), 'f3-error', 'describedby 不重複');
        expectEqual(input._parent.children[1].textContent, '第二次', '訊息更新');
    },

    欄位沒有id時配一個不重複的id() {
        const parent = dom.newElement('div');
        const a = dom.newElement('input'); parent.appendChild(a);
        const b = dom.newElement('input'); parent.appendChild(b);
        setFieldError(a, 'a'); setFieldError(b, 'b');
        assert(a.id && b.id && a.id !== b.id, '兩個無 id 欄位要拿到不同 id');
    },

    // I6 數字欄位
    空欄位必填時回報欄位錯誤且不給數字() {
        const input = field('n1', '');
        const value = readNumberField(input, { required: true, message: '請輸入逾時秒數' });
        expectEqual(value, null, '空欄位回 null');
        expectEqual(input.getAttribute('aria-invalid'), 'true', '標出欄位錯誤');
        expectEqual(input._parent.children[1].textContent, '請輸入逾時秒數', '錯誤訊息');
    },

    空白字元視同空欄位() {
        const input = field('n2', '   ');
        expectEqual(readNumberField(input, { required: true }), null, '只有空白');
    },

    填0時回0且不標錯() {
        const input = field('n3', '0');
        const value = readNumberField(input, { required: true });
        expectEqual(value, 0, '填 0 要回 0，不是 null');
        expectEqual(input.getAttribute('aria-invalid'), null, '不標錯');
    },

    選填空欄位回null且不標錯() {
        const input = field('n4', '');
        expectEqual(readNumberField(input), null, '選填空欄位回 null');
        expectEqual(input.getAttribute('aria-invalid'), null, '選填不標錯');
    },

    整數限制兩側() {
        const bad = field('n5', '1.5');
        expectEqual(readNumberField(bad, { required: true, integer: true }), null, '1.5 不是整數');
        const ok = field('n6', '3');
        expectEqual(readNumberField(ok, { required: true, integer: true }), 3, '3 是整數');
    },

    欄位集合有空欄位時不通過並聚焦第一個錯誤() {
        field('a', '10'); const b = field('b', ''); field('c', '');
        const result = readNumberFields({ a: 'a', b: 'b', c: 'c' });
        expectEqual(result.ok, false, 'ok');
        expectEqual(result.values.a, 10, '有值的照讀');
        expectEqual(result.values.b, null, '空欄位 null');
        expectEqual(b.focused, true, '焦點在第一個錯誤欄位');
    },

    欄位集合填0時整組通過並送0() {
        field('a', '0'); field('b', '5');
        const result = readNumberFields({ a: 'a', b: 'b' });
        expectEqual(result.ok, true, 'ok');
        expectEqual(result.values.a, 0, '0 保持 0');
        expectEqual(result.values.b, 5, 'b');
    },

    欄位集合選填欄位空時仍通過() {
        field('a', '1'); field('opt', '');
        const result = readNumberFields({ a: 'a', opt: { source: 'opt', required: false } });
        expectEqual(result.ok, true, 'ok');
        expectEqual(result.values.opt, null, '選填空欄位 null');
    },

    // I7 hash 跳脫
    hash含引號與方括號時不擲例外() {
        const strictRoot = {
            // 模擬瀏覽器的選擇器解析：值內未跳脫的引號會提早結束字串，整條選擇器非法
            querySelector(selector) {
                const stripped = selector.replace(/\\./g, '');
                if (!/^\[data-[\w-]+="[^"]*"\]$/.test(stripped)) throw new SyntaxError(`非法選擇器 ${selector}`);
                return null;
            },
        };
        for (const hash of ['#a"]', "#a'b", '#a]b[', '#"][data-x="', `#he said "hi"]'`]) {
            globalThis.location = { hash, pathname: '/', search: '', href: '' };
            const id = currentHashId();
            let raw = null;
            try { strictRoot.querySelector(`[data-step-row="${id}"]`); } catch (e) { raw = e; }
            const found = findByData('step-row', id, strictRoot);   // 不得擲例外
            expectEqual(found, null, `hash ${hash} 找不到元素時回 null`);
            if (hash.includes('"')) assert(raw instanceof SyntaxError, `對照組：未跳脫的 ${hash} 本應擲例外`);
        }
    },

    hash一般字串仍找得到元素() {
        const target = { id: 'step-ad' };
        const root = { querySelector: selector => (selector === '[data-step-row="ad"]' ? target : null) };
        globalThis.location = { hash: '#ad', pathname: '/', search: '', href: '' };
        expectEqual(findByData('step-row', currentHashId(), root), target, '一般 hash 找到元素');
    },

    hash百分比編碼壞掉時回原字串() {
        globalThis.location = { hash: '#%E0%A4%A', pathname: '/', search: '', href: '' };
        expectEqual(currentHashId(), '%E0%A4%A', '解碼失敗不擲例外');
    },

    escapeSelectorValue跳脫特殊字元() {
        const escaped = escapeSelectorValue('a"]\'b');
        assert(!/(^|[^\\])["\]']/.test(escaped), `特殊字元都應有反斜線：${escaped}`);
        expectEqual(escapeSelectorValue('plain-id_1'), 'plain-id_1', '一般字串不變');
    },

    // I8 排序
    數字字串依數值排序() {
        const sorted = ['10', '9', '2', '100'].sort(compareText);
        expectEqual(sorted.join(','), '2,9,10,100', '"9" 排在 "10" 前');
        const hosts = ['主機10', '主機9', '主機2'].sort(compareText);
        expectEqual(hosts.join(','), '主機2,主機9,主機10', '中文加數字');
    },

    中文名稱依語系排序而非碼位() {
        // 碼位順序是 張(5F35) < 李(674E) < 王(738B)；pinyin 與筆畫排序都會把「張」排最後
        const sorted = ['張', '王', '李'].sort(compareText);
        assert(sorted[0] !== '張' && sorted[2] === '張', `中文不該照碼位排：${sorted.join('')}`);
        const reference = ['張', '王', '李'].sort(new Intl.Collator('zh-Hant', { numeric: true }).compare);
        expectEqual(sorted.join(''), reference.join(''), '與 zh-Hant collator 一致');
    },

    null與undefined視為空字串排最前() {
        // Array.sort 規格上 undefined 一律排最後、不經比較函式，所以以物件欄位帶空值來測
        const sorted = [{ v: 'b' }, { v: null }, { v: 'a' }, { v: undefined }].sort((x, y) => compareText(x.v, y.v));
        assert(sorted[0].v == null && sorted[1].v == null, '空值排最前');
        expectEqual(sorted[2].v, 'a', 'a');
    },

    sortRows字串欄位用語系比較() {
        const columns = [{ key: 'name', sortKey: 'name' }];
        const rows = [{ name: '10' }, { name: '9' }, { name: '2' }];
        const asc = ui.sortRows(rows, columns, { key: 'name', dir: 'asc' }).map(r => r.name).join(',');
        expectEqual(asc, '2,9,10', '升冪');
        const desc = ui.sortRows(rows, columns, { key: 'name', dir: 'desc' }).map(r => r.name).join(',');
        expectEqual(desc, '10,9,2', '降冪');
    },

    sortRows數字欄位維持數值比較() {
        const columns = [{ key: 'n', sortKey: 'n' }];
        const rows = [{ n: 10 }, { n: 9 }, { n: 2 }];
        const asc = ui.sortRows(rows, columns, { key: 'n', dir: 'asc' }).map(r => r.n).join(',');
        expectEqual(asc, '2,9,10', '數字升冪');
    },

    // I4 對話框關閉方法
    對話框回傳關閉方法並呼叫hide() {
        const source = fs.readFileSync(path.join(path.dirname(fileURLToPath(import.meta.url)), jsRoot, 'ui.js'), 'utf8').replaceAll('\r\n', '\n');
        const start = source.indexOf('export function showDetailModal');
        const open = source.indexOf('{', source.indexOf(') {', start) - 1);
        let depth = 0, end = open;
        for (; end < source.length; end++) {
            if (source[end] === '{') depth++;
            else if (source[end] === '}' && --depth === 0) break;
        }
        const body = source.slice(start, end + 1).replace(/^export /, '');
        const log = { shown: 0, hidden: 0 };
        const node = () => ({
            classList: { add() {} }, setAttribute() {}, appendChild() {}, focus() {},
            set textContent(v) { this._t = v; }, get textContent() { return this._t; },
        });
        const parts = { '.modal-dialog': node(), '.modal-title': node(), '.modal-body': node() };
        const el = {
            className: '', innerHTML: '',
            setAttribute() {}, addEventListener() {}, remove() {},
            querySelector: selector => parts[selector] ?? node(),
        };
        const fakeDocument = {
            createElement: () => el,
            body: { appendChild() {} },
            activeElement: null,
            contains: () => false,
        };
        class FakeModal { constructor() {} show() { log.shown += 1; } hide() { log.hidden += 1; } }
        const showDetailModal = new Function('document', 'bootstrap', 'HTMLElement', `${body}\nreturn showDetailModal;`)(
            fakeDocument, { Modal: FakeModal }, class {});
        const handle = showDetailModal({ title: '標題', body: node() });
        expectEqual(log.shown, 1, '開啟一次');
        assert(typeof handle?.close === 'function', '要回傳 close 方法');
        handle.close();
        expectEqual(log.hidden, 1, 'close() 呼叫 Bootstrap hide');
    },
};

const name = process.argv[2];
if (!name) {
    console.log(Object.keys(cases).join('\n'));
    process.exit(0);
}
if (!(name in cases)) {
    console.log(`FAIL ${name} 未知案例`);
    process.exit(1);
}
try {
    await cases[name]();
    console.log(`PASS ${name}`);
} catch (err) {
    console.log(`FAIL ${name} ${err && err.message ? err.message : err}`);
    process.exit(1);
}
