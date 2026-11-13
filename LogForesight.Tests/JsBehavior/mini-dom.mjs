/**
 * 前端行為測試用的極簡 DOM 替身（回饋第 45 輪 B7）。
 *
 * 為什麼不是整檔字串比對：C1（GET 逾時／POST 不逾時）與 C2（失敗狀態的重試鈕）是**行為**，
 * 「原始碼裡有 AbortController 這幾個字」證明不了 POST 沒被中止、也證明不了按下重試會重跑。
 * 專案沒有瀏覽器測試環境，這裡只補齊 core/api.js 與 core/ui.js 那幾支函式實際會碰到的
 * DOM 介面——刻意不做成通用 DOM，缺什麼就會直接爆錯，比默默通過好。
 */

class FakeClassList {
    constructor(el) { this.el = el; }
    add(...names) {
        const set = new Set(String(this.el.className || '').split(' ').filter(Boolean));
        names.forEach(n => set.add(n));
        this.el.className = [...set].join(' ');
    }
    remove(...names) {
        const drop = new Set(names);
        this.el.className = String(this.el.className || '').split(' ').filter(n => n && !drop.has(n)).join(' ');
    }
    contains(name) { return String(this.el.className || '').split(' ').includes(name); }
    toggle(name, force) {
        const on = force === undefined ? !this.contains(name) : Boolean(force);
        if (on) this.add(name); else this.remove(name);
        return on;
    }
}

class FakeNode {
    constructor(tag) {
        this.tagName = String(tag).toUpperCase();
        this.children = [];
        this.attributes = {};
        // style.setProperty 供 CSS 變數（--lf-*）使用：ui.js 以 token 傳動態值而不寫 inline 屬性
        this.style = { setProperty(name, value) { this[name] = String(value); }, getPropertyValue(name) { return this[name] ?? ''; } };
        this.className = '';
        this._text = '';
        this.listeners = {};
        this.dataset = {};
        this.classList = new FakeClassList(this);
    }

    // 是否掛在文件上：沿 _parent 往上走到根，根是 body 才是 true；沒掛到 body 時回個別案例指定的值
    // （預設 undefined＝未知——既有案例的替身節點從不掛樹，ui.js 以 === false 判斷卸下，不能一律回 false）
    get isConnected() {
        let node = this;
        while (node._parent) node = node._parent;
        return node.tagName === 'BODY' ? true : this._connected;
    }
    set isConnected(value) { this._connected = value; }

    get textContent() {
        return this._text || this.children.map(c => c.textContent).join('');
    }
    set textContent(value) { this._text = String(value); this.children = []; }

    set innerHTML(value) { this._html = String(value); this.children = []; this._text = ''; }
    get innerHTML() { return this._html || ''; }

    // 真瀏覽器的 appendChild／append 是「搬移」：節點已在別的父節點下時先移除，否則同一節點會在兩處出現
    appendChild(child) { this._detach(child); this.children.push(child); if (child) child._parent = this; return child; }
    prepend(...nodes) { nodes = nodes.map(n => this._asNode(n)); nodes.forEach(n => this._detach(n)); this.children.unshift(...nodes); nodes.forEach(n => { if (n) n._parent = this; }); }
    // append／prepend 收到字串時，真瀏覽器會轉成文字節點（runs.js 的 notRunCell 以 append(span, '＋…') 組字）
    _asNode(n) { if (typeof n !== 'string') return n; const t = new FakeNode('#text'); t.textContent = n; return t; }
    append(...nodes) { nodes.map(n => this._asNode(n)).forEach(n => { this._detach(n); this.children.push(n); if (n) n._parent = this; }); }
    replaceChildren(...nodes) { nodes = nodes.map(n => this._asNode(n)); this.children = [...nodes]; this._text = ''; nodes.forEach(n => { if (n) n._parent = this; }); }
    remove() { if (this._parent) this._parent.children = this._parent.children.filter(c => c !== this); this._parent = null; }
    // 兄弟節點操作（D2／E 段的就地重繪與插入會用到）：依 _parent.children 順序定位
    _detach(node) { if (node && node._parent && node._parent !== undefined) node._parent.children = node._parent.children.filter(c => c !== node); }
    _insertAt(offset, nodes) {
        const parent = this._parent;
        if (!parent) throw new Error('mini-dom：節點尚未掛到父節點，無法插入兄弟節點');
        nodes.forEach(n => this._detach(n));
        const index = parent.children.indexOf(this) + offset;
        parent.children.splice(index, 0, ...nodes);
        nodes.forEach(n => { n._parent = parent; });
    }
    after(...nodes) { this._insertAt(1, nodes); }
    before(...nodes) { this._insertAt(0, nodes); }
    // 既有行為測試會直接指定 nextElementSibling（手工搭出展開列），所以保留可寫；沒指定時才依父節點順序推導
    set nextElementSibling(value) { this._nextOverride = value; }
    set previousElementSibling(value) { this._previousOverride = value; }
    get nextElementSibling() {
        if (this._nextOverride !== undefined) return this._nextOverride;
        const siblings = this._parent ? this._parent.children : [];
        return siblings[siblings.indexOf(this) + 1] ?? null;
    }
    get previousElementSibling() {
        if (this._previousOverride !== undefined) return this._previousOverride;
        const siblings = this._parent ? this._parent.children : [];
        const index = siblings.indexOf(this);
        return index > 0 ? siblings[index - 1] : null;
    }
    insertAdjacentElement(position, el) {
        if (position === 'afterend') this.after(el);
        else if (position === 'beforebegin') this.before(el);
        else if (position === 'beforeend') this.appendChild(el);
        else if (position === 'afterbegin') { this._detach(el); this.children.unshift(el); el._parent = this; }
        else throw new Error(`mini-dom：不支援的 insertAdjacentElement 位置 ${position}`);
        return el;
    }
    replaceWith(...nodes) {
        const parent = this._parent;
        if (!parent) throw new Error('mini-dom：節點尚未掛到父節點，無法 replaceWith');
        nodes.forEach(n => this._detach(n));
        const index = parent.children.indexOf(this);
        parent.children.splice(index, 1, ...nodes);
        nodes.forEach(n => { n._parent = parent; });
        this._parent = null;
    }
    setAttribute(name, value) { this.attributes[name] = String(value); }
    setAttributeNS(_ns, name, value) { this.attributes[name] = String(value); }
    getAttribute(name) { return this.attributes[name] ?? null; }
    addEventListener(type, handler) { (this.listeners[type] ||= []).push(handler); }
    dispatch(type) { (this.listeners[type] || []).forEach(h => h()); }

    querySelector(selector) {
        // 支援 class 選擇器（toast() 的 .toast-body、renderLoading 的 .lf-skeleton）
        // 與單一屬性等值選擇器 [name="value"]（AI 歸納提示的去重）
        const attr = /^\[([\w-]+)="([^"]*)"\]$/.exec(selector);
        if (attr && this.getAttribute(attr[1]) === attr[2]) return this;
        const name = selector.replace(/^\./, '');
        if (selector.startsWith('.') && this.classList.contains(name)) return this;
        for (const child of this.children) {
            const hit = child.querySelector?.(selector);
            if (hit) return hit;
        }
        // toast() 的 .toast-body 在 innerHTML 字串裡，替身沒有解析 HTML——補一個空節點讓它寫得下去
        if (selector === '.toast-body' && this.innerHTML.includes('toast-body')) {
            const body = new FakeNode('div');
            this.appendChild(body);
            return body;
        }
        return null;
    }

    /** 只支援單一 class 選擇器（.lf-chip 之類），深度優先、不含自己；renderChips 的單選清除會用到 */
    querySelectorAll(selector) {
        const name = selector.replace(/^\./, '');
        if (!selector.startsWith('.') || /[\s,>\[:]/.test(name)) throw new Error(`mini-dom：querySelectorAll 只支援單一 class 選擇器：${selector}`);
        const hits = [];
        const walk = node => { for (const child of node.children) { if (child.classList?.contains(name)) hits.push(child); walk(child); } };
        walk(this);
        return hits;
    }

    /** 測試輔助：深度優先找出第一個符合條件的節點 */
    find(predicate) {
        if (predicate(this)) return this;
        for (const child of this.children) {
            const hit = child.find?.(predicate);
            if (hit) return hit;
        }
        return null;
    }
}

export function installDom() {
    const body = new FakeNode('body');
    const byId = new Map();

    globalThis.Node = FakeNode;
    globalThis.window = globalThis.window || {};
    globalThis.location = globalThis.location || { pathname: '/', search: '', href: '' };
    globalThis.document = {
        body,
        createElement: tag => new FakeNode(tag),
        createElementNS: (_ns, tag) => new FakeNode(tag),
        // 頁面以 createTextNode 接純文字（PRTG 待重評面板的分頁列）；缺這個方法時被切出的函式會在 catch 裡靜默失敗
        createTextNode: text => { const node = new FakeNode('#text'); node.textContent = String(text); return node; },
        getElementById: id => byId.get(id) ?? null,
        querySelectorAll: () => []
    };

    const toasts = [];
    globalThis.bootstrap = {
        Toast: class {
            constructor(el) { this.el = el; toasts.push(el); }
            show() { }
        }
    };

    return { body, byId, toasts, newElement: tag => new FakeNode(tag) };
}
