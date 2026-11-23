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


// ── 頁籤替身（I3）──────────────────────────────────────────────────────────

/** 可控的 Bootstrap Tab：只模擬 show() 的結果（切 active、發 shown.bs.tab 事件） */
function makeTabsEnv({ names, panelNames, hash = '', extraPanels = [] }) {
    const listeners = {};
    const instances = new Map();
    const classSet = () => {
        const set = new Set();
        return { set, contains: name => set.has(name), toggle: (name, force) => { const on = force === undefined ? !set.has(name) : !!force; if (on) set.add(name); else set.delete(name); return on; }, add: n => set.add(n), remove: n => set.delete(n) };
    };
    const node = dataset => {
        const attrs = {};
        const el = { dataset, id: '', classList: classSet(), setAttribute(n, v) { if (n === 'id') el.id = String(v); else attrs[n] = String(v); }, getAttribute(n) { return n === 'id' ? (el.id || null) : (attrs[n] ?? null); } };
        return el;
    };
    const buttons = names.map(name => node({ tab: name }));
    buttons[0].classList.add('active');
    const uniquePanels = panelNames ?? names;
    const panels = uniquePanels.map((name, index) => { const p = node({ panel: name }); if (index > 0) p.classList.add('d-none'); return p; });
    const allPanels = [...panels, ...extraPanels.map(name => node({ panel: name }))];
    const tabsEl = {
        id: 'demo-tabs',
        parentElement: { querySelectorAll: () => allPanels },
        querySelectorAll: () => buttons,
        addEventListener(type, handler) { listeners[type] = handler; },
    };
    globalThis.bootstrap.Tab = {
        getOrCreateInstance(btn) {
            if (!instances.has(btn)) {
                instances.set(btn, {
                    show() {
                        if (btn.classList.contains('active')) return;
                        for (const other of buttons) other.classList.remove('active');
                        btn.classList.add('active');
                        listeners['shown.bs.tab']?.({ target: btn });
                    },
                });
            }
            return instances.get(btn);
        },
    };
    const replaced = [];
    const hashListeners = [];
    globalThis.location = { hash, pathname: '/', search: '', href: '' };
    globalThis.history = { replaceState: (_s, _t, url) => replaced.push(url) };
    window.history = globalThis.history;
    window.addEventListener = (type, handler) => { if (type === 'hashchange') hashListeners.push(handler); };
    return {
        tabsEl, buttons, panels, allPanels, instances, replaced,
        userShow: key => (typeof key === 'number' ? buttons[key] : buttons.find(b => b.dataset.tab === key)) && globalThis.bootstrap.Tab.getOrCreateInstance(typeof key === 'number' ? buttons[key] : buttons.find(b => b.dataset.tab === key)).show(),
        fireHashChange: () => hashListeners.forEach(h => h()),
    };
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

    async 焦點在modal內時退路的textarea掛進該modal() {
        // Bootstrap 把焦點鎖在開著的 modal 內：掛在 body 的 textarea 選取會落空，copy 指令卻照樣回成功
        setNavigator({});
        globalThis.window.isSecureContext = false;
        const record = installClipboardDocument(true);
        let inModal = 0;
        const modal = { appendChild() { inModal += 1; } };
        globalThis.document.activeElement = {
            focus() { record.refocused = true; },
            closest(selector) { return selector === '.modal' ? modal : null; },
        };
        const result = await copyText('報告全文');
        expectEqual(result.ok, true, '複製結果');
        expectEqual(inModal, 1, 'textarea 掛進 modal');
        expectEqual(record.appended, 0, '不掛在 body');
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

    // I2 整列導向：列內真連結＋延伸點擊區，列保有 <tr> 語意
    整列連結放在第一個沒有互動元素的儲存格() {
        window.addEventListener ||= () => {};   // renderTable 的捲動提示會監聽 resize
        const container = dom.newElement('div');
        const link = text => { const a = dom.newElement('a'); a.textContent = text; return a; };
        ui.renderTable(container, {
            columns: [
                { key: 'name', title: '名稱', render: r => link(r.name) },
                { key: 'ip', title: 'IP' },
            ],
            rows: [{ name: '甲', ip: '10.0.0.1', id: 7 }],
            rowHref: r => `/hosts/${r.id}`,
        });
        const tr = container.find(n => n.tagName === 'TR' && n.classList.contains('lf-row-link'));
        assert(tr, '要有整列連結的列');
        assert(tr.getAttribute('role') === null, '列不可被改成 button 角色');
        assert(tr.getAttribute('tabindex') === null, '列本身不進 Tab 順序，焦點在連結上');
        const anchor = tr.find(n => n.classList.contains('lf-row-link__anchor'));
        assert(anchor && anchor.tagName === 'A', '要有真正的 <a>');
        expectEqual(anchor.getAttribute('href'), '/lf/hosts/7', '連結用 appUrl 組');
        expectEqual(anchor._parent._parent, tr, '第一格有既有連結，延伸連結放進第二格（IP 欄）');
        expectEqual(anchor.textContent, '10.0.0.1', '可見文字就是連結名稱');
    },

    整列連結每格都有互動元素時放空連結並帶名稱() {
        window.addEventListener ||= () => {};
        const container = dom.newElement('div');
        const button = text => { const b = dom.newElement('button'); b.textContent = text; return b; };
        ui.renderTable(container, {
            columns: [{ key: 'a', title: 'A', render: r => button(r.a) }, { key: 'b', title: 'B', render: r => button(r.b) }],
            rows: [{ a: '一', b: '二' }],
            rowHref: () => '/x',
        });
        const anchor = container.find(n => n.classList.contains('lf-row-link__anchor'));
        assert(anchor, '仍要有整列連結');
        expectEqual(anchor.children.length, 0, '不包住既有按鈕');
        expectEqual(anchor.getAttribute('aria-label'), '一', '以首格文字當連結名稱');
    },

    沒有rowHref時不產生整列連結() {
        window.addEventListener ||= () => {};
        const container = dom.newElement('div');
        ui.renderTable(container, { columns: [{ key: 'a', title: 'A' }], rows: [{ a: '1' }], rowHref: () => null });
        assert(!container.find(n => n.classList.contains('lf-row-link__anchor')), '回 null 不放連結');
        assert(!container.find(n => n.classList.contains('lf-row-link')), '不加 lf-row-link');
    },

    // 整列連結下的截斷欄：完整文字的 title 掛在連結內的 span，不掛 td（td 抬高會切碎延伸點擊區）
    整列連結所在格就是截斷欄時提示在主連結內() {
        window.addEventListener ||= () => {};
        const container = dom.newElement('div');
        const full = '很長很長的主機名稱.example.com';
        ui.renderTable(container, {
            columns: [{ key: 'name', title: '主機', truncate: true }, { key: 'ip', title: 'IP' }],
            rows: [{ name: full, ip: '10.0.0.1', id: 7 }],
            rowHref: r => `/hosts/${r.id}`,
        });
        const tr = container.find(n => n.tagName === 'TR' && n.classList.contains('lf-row-link'));
        const td = tr.children[0];
        assert(td.classList.contains('text-truncate'), '截斷欄仍帶 text-truncate');
        assert(!td.title, '有整列連結時 td 不帶 title');
        const holder = tr.find(n => n.title === full);
        assert(holder && holder.tagName === 'SPAN', '完整文字的 title 在 span 上');
        const link = holder._parent;
        assert(link.tagName === 'A' && link.classList.contains('lf-row-link__anchor'), 'span 在主連結之內');
        expectEqual(link.getAttribute('href'), '/lf/hosts/7', '主連結指到列網址');
        expectEqual(link._parent, td, '主連結就在截斷欄');
    },

    整列連結在別格時截斷欄包次要連結並帶提示() {
        window.addEventListener ||= () => {};
        const container = dom.newElement('div');
        const full = '很長很長的說明文字，被省略號截掉';
        ui.renderTable(container, {
            columns: [
                { key: 'name', title: '名稱' },
                { key: 'note', title: '說明', truncate: true },
            ],
            rows: [{ name: '甲', note: full, id: 7 }],
            rowHref: r => `/hosts/${r.id}`,
        });
        const tr = container.find(n => n.tagName === 'TR' && n.classList.contains('lf-row-link'));
        const td = tr.children[1];
        assert(!td.title, 'td 不帶 title');
        const holder = tr.find(n => n.title === full);
        assert(holder && holder.tagName === 'SPAN', '完整文字的 title 在 span 上');
        const secondary = holder._parent;
        assert(secondary.tagName === 'A', 'span 在次要連結之內');
        expectEqual(secondary.getAttribute('href'), '/lf/hosts/7', '次要連結與列同網址');
        expectEqual(secondary.getAttribute('tabindex'), '-1', '不新增 Tab 停駐點');
        expectEqual(secondary.getAttribute('aria-hidden'), 'true', '不讓輔助科技重複念');
        expectEqual(secondary._parent, td, '次要連結在截斷欄內');
    },

    整列連結的截斷欄有按鈕時按鈕不被連結包住() {
        window.addEventListener ||= () => {};
        const container = dom.newElement('div');
        const button = text => { const b = dom.newElement('button'); b.textContent = text; return b; };
        ui.renderTable(container, {
            columns: [
                { key: 'name', title: '名稱' },
                { key: 'act', title: '動作', truncate: true, render: r => button(r.act) },
            ],
            rows: [{ name: '甲', act: '重新整理這台主機的資料', id: 7 }],
            rowHref: r => `/hosts/${r.id}`,
        });
        const tr = container.find(n => n.tagName === 'TR' && n.classList.contains('lf-row-link'));
        const btn = tr.find(n => n.tagName === 'BUTTON');
        let node = btn._parent;
        while (node && node !== tr) { assert(node.tagName !== 'A', '按鈕不可在 <a> 之內'); node = node._parent; }
        expectEqual(tr.children[1].title, '重新整理這台主機的資料', '含互動元素的格退回 td 的 title');
    },

    沒有rowHref時截斷欄的title仍在td上() {
        window.addEventListener ||= () => {};
        const container = dom.newElement('div');
        ui.renderTable(container, {
            columns: [{ key: 'a', title: 'A', truncate: true }],
            rows: [{ a: '一段很長的文字' }],
        });
        const td = container.find(n => n.tagName === 'TD');
        expectEqual(td.title, '一段很長的文字', 'title 在 td 上');
        assert(!container.find(n => n.tagName === 'SPAN' && n.title), '不多包 span');
    },

    // I3 頁籤：Bootstrap Tab 外掛＋hash 同步
    頁籤配上外掛屬性並建立外掛實例() {
        const env = makeTabsEnv({ names: ['a', 'b'] });
        ui.bindTabs(env.tabsEl);
        for (const btn of env.buttons) {
            expectEqual(btn.getAttribute('data-bs-toggle'), 'tab', 'data-bs-toggle');
            const panel = env.panels.find(p => p.dataset.panel === btn.dataset.tab);
            expectEqual(btn.getAttribute('data-bs-target'), '#' + panel.id, 'data-bs-target 指到面板 id');
            expectEqual(btn.getAttribute('aria-controls'), panel.id, 'aria-controls');
            assert(btn.id, '按鈕要有 id，面板的 aria-labelledby 才指得到');
        }
        expectEqual(env.instances.size, 2, '每顆按鈕都建立外掛實例（角色與方向鍵由外掛提供）');
    },

    切換頁籤時同步面板與標籤與回呼與hash() {
        const env = makeTabsEnv({ names: ['a', 'b'] });
        const changes = [];
        ui.bindTabs(env.tabsEl, { hash: true, onChange: name => changes.push(name) });
        env.userShow('b');
        expectEqual(env.panels[0].classList.contains('d-none'), true, '舊面板隱藏');
        expectEqual(env.panels[1].classList.contains('d-none'), false, '新面板顯示');
        expectEqual(env.panels[1].getAttribute('aria-labelledby'), env.buttons[1].id, '面板標籤指向目前頁籤');
        expectEqual(changes.join(','), 'b', 'onChange');
        expectEqual(env.replaced.join(','), '#b', 'replaceState 同步 hash');
    },

    網址hash指到頁籤時開啟對應頁籤() {
        const env = makeTabsEnv({ names: ['a', 'b'], hash: '#b' });
        const changes = [];
        ui.bindTabs(env.tabsEl, { hash: true, onChange: name => changes.push(name) });
        expectEqual(env.buttons[1].classList.contains('active'), true, '依 hash 切到 b');
        expectEqual(changes.join(','), 'b', 'onChange');
    },

    網址hash指到目前頁籤時仍呼叫一次回呼() {
        const env = makeTabsEnv({ names: ['a', 'b'], hash: '#a' });
        const changes = [];
        ui.bindTabs(env.tabsEl, { hash: true, onChange: name => changes.push(name) });
        expectEqual(changes.join(','), 'a', '預設頁籤被 hash 指到時頁面仍要載入它');
        expectEqual(env.replaced.length, 0, '沒有切換就不改網址');
    },

    網址hash含特殊字元時不擲例外且不切換() {
        const env = makeTabsEnv({ names: ['a', 'b'], hash: '#b"]' });
        ui.bindTabs(env.tabsEl, { hash: true });
        expectEqual(env.buttons[0].classList.contains('active'), true, '停在預設頁籤');
    },

    hashchange時跟著切頁籤() {
        const env = makeTabsEnv({ names: ['a', 'b'] });
        ui.bindTabs(env.tabsEl, { hash: true });
        globalThis.location.hash = '#b';
        env.fireHashChange();
        expectEqual(env.buttons[1].classList.contains('active'), true, 'hashchange 後切到 b');
    },

    多顆頁籤共用同一面板時標籤跟著目前頁籤() {
        const env = makeTabsEnv({ names: ['rules', 'rules', 'other'], panelNames: ['rules', 'other'] });
        const seen = [];
        ui.bindTabs(env.tabsEl, { onChange: (name, btn) => seen.push(btn.id) });
        env.userShow(1);
        expectEqual(env.panels[0].getAttribute('aria-labelledby'), env.buttons[1].id, '共用面板的標籤指向實際點的那顆');
        expectEqual(seen[0], env.buttons[1].id, 'onChange 帶出按鈕');
    },

    只認配得到按鈕的面板() {
        const env = makeTabsEnv({ names: ['a', 'b'], extraPanels: ['current'] });
        ui.bindTabs(env.tabsEl);
        env.userShow('b');
        const extra = env.allPanels.find(p => p.dataset.panel === 'current');
        expectEqual(extra.classList.contains('d-none'), false, '別組頁籤的面板不受影響');
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
