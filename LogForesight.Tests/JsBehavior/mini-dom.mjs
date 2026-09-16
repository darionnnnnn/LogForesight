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
    contains(name) { return String(this.el.className || '').split(' ').includes(name); }
}

class FakeNode {
    constructor(tag) {
        this.tagName = String(tag).toUpperCase();
        this.children = [];
        this.attributes = {};
        this.style = {};
        this.className = '';
        this._text = '';
        this.listeners = {};
        this.classList = new FakeClassList(this);
    }

    get textContent() {
        return this._text || this.children.map(c => c.textContent).join('');
    }
    set textContent(value) { this._text = String(value); this.children = []; }

    set innerHTML(value) { this._html = String(value); }
    get innerHTML() { return this._html || ''; }

    appendChild(child) { this.children.push(child); return child; }
    append(...nodes) { nodes.forEach(n => this.children.push(n)); }
    replaceChildren(...nodes) { this.children = [...nodes]; this._text = ''; }
    remove() { }
    setAttribute(name, value) { this.attributes[name] = String(value); }
    setAttributeNS(_ns, name, value) { this.attributes[name] = String(value); }
    getAttribute(name) { return this.attributes[name] ?? null; }
    addEventListener(type, handler) { (this.listeners[type] ||= []).push(handler); }
    dispatch(type) { (this.listeners[type] || []).forEach(h => h()); }

    querySelector(selector) {
        // 只支援 class 選擇器：toast() 要拿 .toast-body，renderLoading 的骨架列驗證要拿 .lf-skeleton
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
