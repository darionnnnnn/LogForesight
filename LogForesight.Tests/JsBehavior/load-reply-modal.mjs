/**
 * 載入 production 的 pages/issue-status-reply.js（回覆彈窗與成功訊息函式）給行為測試用。
 *
 * 做法：整份原始碼去掉 import／export 後以 new Function 執行，只把它匯入的邊界換成替身——
 * 狀態目錄、statusNote、button、renderChips、withBusy、日期函式都用 production 版（從原始碼切出），
 * api／toast／showDetailModal／attachNoteEditor／copyText／confirmAction 是可觀察的替身。
 * issue-status-reply.js 新增匯入時，這裡的 STUBS 要跟著補，否則載入就會明確報錯。
 */
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { installDom } from './mini-dom.mjs';
import { loadElements, loadButton } from './load-elements.mjs';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
export const jsRoot = path.resolve(scriptDir, '..', '..', 'LogForesight.Web', 'wwwroot', 'js');
export const read = (...p) => fs.readFileSync(path.join(jsRoot, ...p), 'utf8').replaceAll('\r\n', '\n').replace(/^﻿+/, '');
export const catalog = await import(pathToFileURL(path.join(jsRoot, 'core', 'handling-statuses.js')).href);
const { createSeqGuard } = await import(pathToFileURL(path.join(jsRoot, 'core', 'async-guards.js')).href);

/** 切出一個函式本體（`export function` 的 export 一併去掉） */
export function extract(source, declaration) {
    const start = source.indexOf(declaration);
    assert(start >= 0, `找不到 production 函式：${declaration}`);
    const open = source.indexOf('{', source.indexOf(')', start));
    let depth = 0;
    for (let i = open; i < source.length; i++) {
        if (source[i] === '{') depth++;
        else if (source[i] === '}' && --depth === 0) return source.slice(start, i + 1).replace(/^export /, '');
    }
    throw new Error(`production 函式未閉合：${declaration}`);
}

export const all = node => { const out = []; const walk = n => { out.push(n); n.children.forEach(walk); }; walk(node); return out; };
export const byId = (root, id) => all(root).find(n => n.id === id) ?? null;
export const hidden = n => n.classList.contains('d-none');
export const textOf = n => n.textContent;

/** 觸發某節點的事件（帶事件物件）；回傳所有處理函式的 Promise，async 處理函式可以 await */
export function fire(node, type, event = {}) {
    const ev = { preventDefault() { ev.defaultPrevented = true; }, ...event };
    return Promise.all((node.listeners[type] || []).map(h => h(ev))).then(() => ev);
}

const IMPORT_LINES = /^import .*;$/gm;

/**
 * @param {object} [options]
 * @param {(url: string) => any} [options.apiGet] api.get 的替身
 * @param {boolean} [options.confirmResult] confirmAction 的回覆
 * @param {{ok: boolean, reason?: string}} [options.copyResult] copyText 的回覆
 */
export function loadReplyModule({ apiGet = async () => ({}), confirmResult = true, copyResult = { ok: true } } = {}) {
    const dom = installDom();
    const originalCreate = document.createElement;
    document.createElement = tag => {
        const el = originalCreate(tag);
        el.focus = () => { el.focused = true; };
        el.select = () => { el.selected = true; };
        el.click = () => { fire(el, 'click'); };
        if (tag === 'textarea' || tag === 'input') {
            el.value = '';
            el.checkValidity = () => true;
        }
        return el;
    };
    const { statusNote, roleText } = loadElements(dom);
    const button = loadButton(dom);

    const uiSource = read('core', 'ui.js');
    const formatSource = read('core', 'format.js');
    const renderChips = new Function(`${extract(uiSource, 'export function renderChips(')}\nreturn renderChips;`)();
    const withBusy = new Function(`${extract(uiSource, 'export function withBusy(')}\nreturn withBusy;`)();
    const createPageSelection = new Function(`${extract(uiSource, 'export function createPageSelection(')}\nreturn createPageSelection;`)();
    const formatFns = new Function(`${extract(formatSource, 'export function toLocalDateString(')}
${extract(formatSource, 'export function todayLocal(')}
${extract(formatSource, 'export function dateAfterDays(')}
return { toLocalDateString, todayLocal, dateAfterDays };`)();

    const env = {
        dom, toasts: [], posts: [], gets: [], confirms: 0, copied: [], drafts: {}, shown: [], closed: 0, fieldErrors: []
    };
    // 彈窗尚未接上文件：依 id 在已開的彈窗本體裡找（createPageSelection 的選取列靠 getElementById 定位）
    const baseGetById = document.getElementById;
    document.getElementById = id => env.shown.map(entry => byId(entry.body, id)).find(Boolean) ?? baseGetById(id);

    // 主機面板（reply-host-picker.js）也用 production 版載入
    const pickerSource = read('pages', 'reply-host-picker.js');
    const pickerImports = [...pickerSource.matchAll(/^import \{([^}]*)\} from '[^']+';$/gm)]
        .flatMap(m => m[1].split(',').map(x => x.trim().split(/\s+as\s+/).pop()).filter(Boolean));
    // 共用表格與分頁列的最小替身（本測試只看面板的列與換頁行為；真版在 core/ui.js）
    const renderTable = (container, { columns, rows, empty }) => {
        const table = dom.newElement('table');
        const headRow = dom.newElement('tr');
        for (const column of columns) {
            const th = dom.newElement('th');
            const head = column.renderHeader ? column.renderHeader() : column.title;
            if (head && typeof head === 'object') th.appendChild(head); else th.textContent = head || '';
            headRow.appendChild(th);
        }
        const body = dom.newElement('tbody');
        for (const row of rows) {
            const tr = dom.newElement('tr');
            for (const column of columns) {
                const td = dom.newElement('td');
                const content = column.render(row);
                if (content && typeof content === 'object') td.appendChild(content); else td.textContent = content || '';
                tr.appendChild(td);
            }
            body.appendChild(tr);
        }
        table.append(headRow, body);
        if (rows.length === 0) { const e = dom.newElement('div'); e.textContent = empty.title; container.replaceChildren(e); } else container.replaceChildren(table);
    };
    const renderPagination = (container, { page, totalPages, onPage }) => {
        if (!totalPages || totalPages <= 1) { container.replaceChildren(); return; }
        const prev = button('上一頁', { onClick: () => onPage(page - 1) });
        const next = button('下一頁', { onClick: () => onPage(page + 1) });
        container.replaceChildren(prev, next);
    };
    const PICKER_STUBS = { createPageSelection, renderTable, renderPagination, button, createSeqGuard, roleText, statusBadgeMeta: catalog.statusBadgeMeta };
    for (const name of pickerImports) assert(name in PICKER_STUBS, `reply-host-picker.js 匯入了 ${name}，load-reply-modal.mjs 沒有對應替身`);
    const pickerModule = new Function(...Object.keys(PICKER_STUBS),
        `${pickerSource.replace(/^import .*;$/gm, '').replace(/^export /gm, '')}\nreturn { createHostPicker, parseHostNames };`)(...Object.values(PICKER_STUBS));

    const STUBS = {
        createHostPicker: pickerModule.createHostPicker,
        api: {
            get: async url => { env.gets.push(url); return apiGet(url); },
            post: async (url, body, options) => { env.posts.push({ url, body, options }); return {}; }
        },
        getCurrentUser: async () => ({ userId: 1 }),
        setFieldError: (input, message) => { env.fieldErrors.push(message); input.invalid = true; },
        clearFieldError: input => { input.invalid = false; },
        toast: (message, type) => env.toasts.push({ message, type }),
        withBusy,
        showDetailModal: ({ title, body }) => {
            const element = dom.newElement('div');
            const entry = { title, body, element, close() { env.closed++; } };
            env.shown.push(entry);
            return { close: entry.close, element };
        },
        confirmAction: async () => { env.confirms++; return confirmResult; },
        bindPastDateCheck: () => ({ validate: () => true }),
        renderChips,
        makeButton: button,
        copyText: async text => { env.copied.push(text); return copyResult; },
        // 草稿以 draftKey 存在 env.drafts：輸入即寫入、clearDraft 才刪除，用來看「被拒／Esc 後草稿仍在」
        attachNoteEditor: (textarea, { draftKey }) => {
            textarea.addEventListener('input', () => { env.drafts[draftKey] = textarea.value; });
            return {
                clearDraft: () => { delete env.drafts[draftKey]; },
                setValue: text => { textarea.value = text; env.drafts[draftKey] = text; }
            };
        },
        statusMeta: catalog.statusMeta,
        statusesWhere: catalog.statusesWhere,
        isNoFixClosure: catalog.isNoFixClosure,
        replyableCount: catalog.replyableCount,
        MAX_DUE_DATE_DAYS: catalog.MAX_DUE_DATE_DAYS,
        statusNote,
        todayLocal: formatFns.todayLocal,
        toLocalDateString: formatFns.toLocalDateString,
        dateAfterDays: formatFns.dateAfterDays
    };

    let source = read('pages', 'issue-status-reply.js');
    const imported = [...source.matchAll(/^import \{([^}]*)\} from '[^']+';$/gm)]
        .flatMap(m => m[1].split(',').map(x => x.trim().split(/\s+as\s+/).pop()).filter(Boolean));
    for (const name of imported) assert(name in STUBS, `issue-status-reply.js 匯入了 ${name}，load-reply-modal.mjs 沒有對應替身`);
    source = source.replace(IMPORT_LINES, '').replace(/^export /gm, '');
    const exported = ['orderMembersUrl', 'remainingActiveCount', 'openWorkOrderReplyModal', 'toastReplyResult', 'toastReplyManyResult', 'replyOutcomeText', 'describeReplyRejection', 'workOrdersDraftKey', 'noFixConsequenceText'];
    const module = new Function(...Object.keys(STUBS), `${source}\nreturn { ${exported.join(', ')} };`)(...Object.values(STUBS));
    return { env, ...module, formatFns, ...pickerModule };
}

/** 開一個回覆彈窗並回傳常用節點與操作；status 以結論按鈕的 data-status 找 */
export function openReply(loaded, options = {}) {
    const { env, openWorkOrderReplyModal } = loaded;
    const submits = [];
    const submit = options.submit ?? (async payload => { submits.push(payload); });
    openWorkOrderReplyModal({
        title: 't', targetText: '全部進行中主機（20 台）', draftKey: 'order:7:all',
        ...options, submit
    });
    const entry = env.shown[env.shown.length - 1];
    const body = entry.body;
    const form = body.children[0];
    const buttons = () => all(body).filter(n => n.tagName === 'BUTTON');
    return {
        entry, body, form, submits,
        group: byId(body, 'wo-reply-status'),
        note: byId(body, 'wo-reply-note'),
        due: byId(body, 'wo-reply-due'),
        quick: byId(body, 'wo-reply-due-quick'),
        required: byId(body, 'wo-reply-status-required'),
        rejected: byId(body, 'wo-reply-rejected'),
        copyBtn: byId(body, 'wo-reply-copy-note'),
        toggle: byId(body, 'wo-reply-pick-toggle'),
        panel: byId(body, 'wo-reply-picker'),
        search: byId(body, 'wo-reply-pick-search'),
        applyBtn: byId(body, 'wo-reply-pick-apply'),
        missing: byId(body, 'wo-reply-pick-missing'),
        statusLabel: byId(body, 'wo-reply-status-label'),
        hintText: () => textOf(body.children[0].children[0]),
        submitBtn: buttons().find(n => n.type === 'submit'),
        // 結論按鈕在續問（「其餘 N 台」）時會重畫，每次都重新找
        get chips() { return all(body).filter(n => n.classList?.contains('lf-chip')); },
        chip(status) { return this.chips.find(n => n.dataset.status === status); },
        pick(status) { this.chip(status).click(); },
        active() { return this.chips.filter(n => n.classList.contains('active')).map(n => n.dataset.status); },
        write(text) { this.note.value = text; this.note.dispatch('input'); },
        send() { return fire(form, 'submit'); },
        buttonByText: text => buttons().find(n => n.textContent.includes(text))
    };
}

export function deferred() {
    let resolve;
    const promise = new Promise(r => { resolve = r; });
    return { promise, resolve };
}
