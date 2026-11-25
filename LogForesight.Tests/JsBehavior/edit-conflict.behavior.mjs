/**
 * 第 81 輪批次 M：使用者視窗一次送出、規則與使用者視窗收到 409 時保留輸入並常駐提示（users.js、rules.js）。
 *
 * 做法同 groups-members.behavior.mjs：整支頁面模組讀入、去掉 import（含多行）後以替身參數執行，
 * document 換成「依 id 自動建立元素」的版本；匯入的名稱除特別指定的替身外一律給空函式，
 * 頁面新增匯入時不必回頭補這裡。提交處理常式從元素的 listeners 取出直接呼叫（可 await）。
 */
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { installDom } from './mini-dom.mjs';
import { loadButton, loadElements } from './load-elements.mjs';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const jsRoot = path.resolve(scriptDir, '..', '..', 'LogForesight.Web', 'wwwroot', 'js');
const dom = installDom();
const { statusNote } = loadElements(dom);
const realButton = loadButton(dom);

const tick = () => new Promise(resolve => setImmediate(resolve));
const settle = async () => { for (let i = 0; i < 10; i++) await tick(); };

const IMPORT_RE = /^import\s[\s\S]*?\sfrom\s+'[^']+';$/gm;

function importedNames(src) {
    const names = [];
    for (const match of src.matchAll(IMPORT_RE)) {
        const clause = match[0].replace(/^import\s+/, '').replace(/\s+from\s+'[^']+';$/, '');
        for (const part of clause.replace(/[{}]/g, '').split(',')) {
            const name = part.trim().split(/\s+as\s+/).pop();
            if (name) names.push(name);
        }
    }
    return names;
}

class ApiErrorStub extends Error {
    constructor(status, message) { super(message); this.name = 'ApiError'; this.status = status; this.code = status === 409 ? 'conflict' : 'validation_failed'; }
}

/** 載入頁面模組；apiHandlers 以 `METHOD url` 為鍵（可為函式）；tail 是附加在模組尾端、回傳測試要用的內部函式 */
function loadPage(file, tail, { apiHandlers = {}, checked = [] } = {}) {
    const src = fs.readFileSync(path.join(jsRoot, 'pages', file), 'utf8').replaceAll('\r\n', '\n');
    const names = [...new Set(importedNames(src))];
    const body = src.replace(IMPORT_RE, '');

    const els = new Map();
    const el = key => {
        if (!els.has(key)) {
            const node = dom.newElement('div');
            node.value = '';
            node.checked = false;
            node.disabled = false;
            els.set(key, node);
        }
        return els.get(key);
    };
    const fakeDocument = {
        ...document,
        getElementById: id => el(`#${id}`),
        querySelector: sel => el(sel),
        querySelectorAll: sel => (sel === '#user-groups input:checked' ? checked : []),
        createElement: tag => document.createElement(tag)
    };
    const spy = { calls: [], toasts: [], hides: 0, shows: 0, confirms: 0 };
    const respond = async (method, url, bodyArg) => {
        spy.calls.push({ method, url, body: bodyArg });
        const handler = apiHandlers[`${method} ${url}`];
        if (handler !== undefined) return typeof handler === 'function' ? handler(bodyArg) : handler;
        if (method === 'GET') return [];
        return {};
    };
    const stubs = Object.fromEntries(names.map(n => [n, () => ({})]));
    Object.assign(stubs, {
        api: {
            get: (url) => respond('GET', url),
            post: (url, bodyArg) => respond('POST', url, bodyArg),
            put: (url, bodyArg) => respond('PUT', url, bodyArg)
        },
        toast: (message, type) => spy.toasts.push({ message, type }),
        withBusy: () => () => { },
        guardLoad: async (_container, fn) => fn(),
        confirmAction: async () => { spy.confirms++; return true; },
        statusNote,
        button: (text, opts = {}) => realButton(text, opts),
        loadPageSize: () => 20,
        sortRows: rows => rows,
        createSeqGuard: () => { let n = 0; return { next: () => ++n, isCurrent: v => v === n }; },
        SEVERITY_ORDER: [],
        CATEGORY_NAMES: {},
        checkboxList: () => { },
        formatDateTime: v => String(v),
        appUrl: p => p,
        roleText: () => dom.newElement('span')
    });
    const modalClass = class { show() { spy.shows++; } hide() { spy.hides++; } };
    const all = [...Object.keys(stubs), 'document', 'bootstrap'];
    const module = new Function(...all, `${body}\n${tail}`)(
        ...Object.keys(stubs).map(n => stubs[n]), fakeDocument,
        { Modal: modalClass, Collapse: modalClass, Popover: modalClass, Tooltip: modalClass, Tab: modalClass });
    return { ...module, el, spy };
}

const SAVED_URL = '/api/admin/users';
const user = { userId: 5, account: 'DOMAIN\\u1', displayName: '原名', email: 'a@x.com', active: true, groupIds: [1], version: 'v-user-1' };

function loadUsers(extra = {}) {
    return loadPage('users.js', 'return { openModal, reloadAfterConflict };', {
        apiHandlers: { 'GET /api/admin/users': [user], ...extra.apiHandlers },
        checked: extra.checked ?? []
    });
}

const submit = page => page.el('#user-form').listeners.submit[0]({ preventDefault() { } });
const isHidden = node => node.classList.contains('d-none');

const cases = {
    // 編輯視窗一次送出：只有一個寫入請求，帶版本與所選群組；不再另打 /groups 端點
    async userSaveIsOneRequestWithVersionAndGroups() {
        const page = loadUsers({ checked: [{ value: '2' }, { value: '3' }] });
        await settle();
        page.openModal(user);
        page.el('#user-display-name').value = '新名稱';
        page.el('#user-account').value = 'DOMAIN\\u1';
        await submit(page);
        await settle();

        const writes = page.spy.calls.filter(c => c.method !== 'GET');
        assert.equal(writes.length, 1, `寫入請求數：${JSON.stringify(writes)}`);
        assert.equal(writes[0].url, SAVED_URL);
        assert.equal(writes[0].body.version, 'v-user-1');
        assert.deepEqual(writes[0].body.groupIds, [2, 3]);
        assert.equal(writes[0].body.displayName, '新名稱');
        assert.ok(!page.spy.calls.some(c => c.method !== 'GET' && c.url.endsWith('/groups')), '不得再呼叫群組端點');
        assert.equal(page.spy.hides, 1, '成功要關視窗');
    },

    // 新增使用者沒有舊版本
    async userCreateSendsNullVersion() {
        const page = loadUsers({ checked: [] });
        await settle();
        page.openModal(null);
        page.el('#user-account').value = 'DOMAIN\\new';
        await submit(page);
        await settle();

        const write = page.spy.calls.find(c => c.method === 'POST');
        assert.equal(write.body.version, null);
        assert.deepEqual(write.body.groupIds, []);
    },

    // 409：視窗不關、輸入保留、常駐訊息與重新載入鈕；不跳「已更新」、不重載清單
    async userConflictKeepsInputAndShowsPersistentNotice() {
        const page = loadUsers({
            apiHandlers: { 'POST /api/admin/users': () => { throw new ApiErrorStub(409, '這筆資料剛被其他人修改，請重新載入後再改。'); } }
        });
        await settle();
        page.openModal(user);
        page.el('#user-display-name').value = '我剛填的名稱';
        page.el('#user-account').value = 'DOMAIN\\u1';
        const getsBefore = page.spy.calls.filter(c => c.method === 'GET').length;
        await submit(page);
        await settle();

        const notice = page.el('#user-conflict');
        assert.ok(!isHidden(notice), '要顯示常駐訊息');
        assert.ok(notice.textContent.includes('這筆資料剛被其他人修改，請重新載入後再改'), notice.textContent);
        assert.ok(notice.find(n => n.tagName === 'BUTTON' && n.textContent.includes('重新載入')), '要有重新載入鈕');
        assert.equal(page.el('#user-display-name').value, '我剛填的名稱', '輸入要保留');
        assert.equal(page.spy.hides, 0, '不得自動關視窗');
        assert.ok(!page.spy.toasts.some(t => t.message.includes('已更新')), '不得顯示成功');
        assert.equal(page.spy.calls.filter(c => c.method === 'GET').length, getsBefore, '不得自動重載');
    },

    // 非 409 的錯誤：照舊 toast，不出現衝突訊息
    async userOtherErrorToastsWithoutConflictNotice() {
        const page = loadUsers({
            apiHandlers: { 'POST /api/admin/users': () => { throw new ApiErrorStub(400, 'Email 不合格'); } }
        });
        await settle();
        page.openModal(user);
        page.el('#user-account').value = 'DOMAIN\\u1';
        await submit(page);
        await settle();

        assert.ok(isHidden(page.el('#user-conflict')));
        assert.ok(page.spy.toasts.some(t => t.message === 'Email 不合格' && t.type === 'danger'));
    },

    // 按「重新載入」才換成伺服器最新值：重打清單、以新版本重開視窗、衝突訊息收起
    async userReloadButtonRefreshesVersionAndClearsNotice() {
        let version = 'v-user-1';
        const page = loadUsers({
            apiHandlers: {
                'GET /api/admin/users': () => [{ ...user, version, displayName: version === 'v-user-1' ? '原名' : '別人改的名稱' }],
                'POST /api/admin/users': () => { throw new ApiErrorStub(409, '衝突'); }
            }
        });
        await settle();
        page.openModal(user);
        page.el('#user-account').value = 'DOMAIN\\u1';
        await submit(page);
        await settle();
        const reload = page.el('#user-conflict').find(n => n.tagName === 'BUTTON');

        version = 'v-user-2';
        await reload.listeners.click[0]();
        await settle();

        assert.ok(isHidden(page.el('#user-conflict')), '重新載入後收起訊息');
        assert.equal(page.el('#user-display-name').value, '別人改的名稱');
    },

    // 規則視窗：編輯既有規則送出時帶版本；新增沒有
    async ruleSaveSendsVersionForEditAndNullForCreate() {
        const rule = ruleFixture();
        const page = loadRules();
        await settle();
        page.openRuleModal(rule);
        await submit2(page);
        await settle();
        assert.equal(page.spy.calls.find(c => c.method === 'POST' && c.url === '/api/rules').body.version, 'v-rule-1');

        const created = loadRules();
        await settle();
        created.openRuleModal(null);
        await submit2(created);
        await settle();
        assert.equal(created.spy.calls.find(c => c.method === 'POST' && c.url === '/api/rules').body.version, null);
    },

    // 規則 409：同使用者視窗的行為（保留輸入、常駐訊息、不關視窗、不顯示成功）
    async ruleConflictKeepsInputAndShowsPersistentNotice() {
        const page = loadRules({ 'POST /api/rules': () => { throw new ApiErrorStub(409, '衝突'); } });
        await settle();
        page.openRuleModal(ruleFixture());
        page.el('#rule-description').value = '我剛改的說明';
        await submit2(page);
        await settle();

        const notice = page.el('#rule-conflict');
        assert.ok(!isHidden(notice), '要顯示常駐訊息');
        assert.ok(notice.textContent.includes('這筆資料剛被其他人修改，請重新載入後再改'), notice.textContent);
        assert.ok(notice.find(n => n.tagName === 'BUTTON' && n.textContent.includes('重新載入')));
        assert.equal(page.el('#rule-description').value, '我剛改的說明', '輸入要保留');
        assert.equal(page.spy.hides, 0, '不得自動關視窗');
        assert.ok(!page.spy.toasts.some(t => t.message.includes('已更新')), '不得顯示成功');
    },

    // 規則 409 後重新載入找不到該規則（已被刪除）：提示一句再關視窗
    async ruleReloadWhenRuleDeletedToastsAndCloses() {
        let deleted = false;
        const page = loadRules({
            'GET /api/rules': () => (deleted ? [] : [ruleFixture()]),
            'POST /api/rules': () => { throw new ApiErrorStub(409, '衝突'); }
        });
        await settle();
        page.openRuleModal(ruleFixture());
        await submit2(page);
        await settle();
        const reload = page.el('#rule-conflict').find(n => n.tagName === 'BUTTON');

        deleted = true;
        await reload.listeners.click[0]();
        await settle();

        assert.ok(page.spy.toasts.some(t => t.message === '這條規則已被刪除'), JSON.stringify(page.spy.toasts));
        assert.equal(page.spy.hides, 1, '要關視窗');
        assert.ok(isHidden(page.el('#rule-conflict')));
    },

    // 規則的重新載入鈕：重打清單並以最新版本重開視窗
    async ruleReloadButtonRefreshesAndClearsNotice() {
        let description = '原說明';
        let version = 'v-rule-1';
        const page = loadRules({
            'GET /api/rules': () => [{ ...ruleFixture(), description, version }],
            'POST /api/rules': () => { throw new ApiErrorStub(409, '衝突'); }
        });
        await settle();
        page.openRuleModal(ruleFixture());
        await submit2(page);
        await settle();
        const reload = page.el('#rule-conflict').find(n => n.tagName === 'BUTTON');

        description = '別人改的說明'; version = 'v-rule-2';
        await reload.listeners.click[0]();
        await settle();

        assert.ok(isHidden(page.el('#rule-conflict')));
        assert.equal(page.el('#rule-description').value, '別人改的說明');
    }
};

function ruleFixture() {
    return {
        id: 'custom-a', version: 'v-rule-1', origin: 'custom', enabled: true, platform: 'windows',
        sourcePattern: 'MyApp', eventIds: [9001], matchAllEventIds: false, programPattern: '', eventNamePattern: '',
        messagePatterns: [], category: 'Service', severity: 'Medium', elevatesDayRisk: false, description: '原說明',
        countThreshold: 3, plainExplanation: '白話', impact: '影響', likelyCauses: [], nextSteps: []
    };
}

function loadRules(apiHandlers = {}) {
    return loadPage('rules.js', 'return { openRuleModal };', {
        apiHandlers: { 'GET /api/rules': [ruleFixture()], 'POST /api/rules/validate': { isValid: true, errors: [], warnings: [] }, ...apiHandlers }
    });
}

const submit2 = page => page.el('#rule-form').listeners.submit[0]({ preventDefault() { } });

const name = process.argv[2];
if (!name) {
    Object.keys(cases).forEach(key => console.log(key));
    process.exit(0);
}
if (!cases[name]) { console.error(`未知案例：${name}`); process.exit(2); }
await cases[name]();
console.log(`PASS ${name}`);
