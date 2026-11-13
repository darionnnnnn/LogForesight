/**
 * 第 74 輪批次F：問題查詢依問題視角的列內動作（records.js 的 issueActionsCell／issueActions／
 * issueActionButton／issueMoreMenu）。以 extractFunction 切出 production 函式本體，
 * 只替換外部相依（目前使用者、能力判斷、四個視窗、Bootstrap 下拉選單）。
 * 由 RecordsRowActionsBehaviorTests 以 `node records-row-actions.behavior.mjs <案例名>` 逐案執行。
 */
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { installDom } from './mini-dom.mjs';
import { loadButton } from './load-elements.mjs';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(scriptDir, '..', '..');
const records = fs.readFileSync(path.join(repoRoot, 'LogForesight.Web', 'wwwroot', 'js', 'pages', 'records.js'), 'utf8')
    .replaceAll('\r\n', '\n').replace(/^﻿/, '');

function extractFunction(source, declaration) {
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

const SOURCES = ['function issueActionsCell', 'function issueActions(', 'function issueActionButton', 'function issueMoreMenu']
    .map(declaration => extractFunction(records, declaration)).join('\n');

/** 組一個頁面層環境：caps＝目前使用者的能力，mine＝這一列是不是自己的案件 */
function env({ caps = [], mine = false } = {}) {
    const dom = installDom();
    const button = loadButton(dom);
    const opened = [];
    const hidden = [];
    const currentUser = { userId: 7 };
    const hasCapability = (user, cap) => user === currentUser && caps.includes(cap);
    const bootstrap = { Dropdown: { getOrCreateInstance: toggle => ({ hide: () => hidden.push(toggle) }) } };
    const modal = name => (...args) => opened.push({ name, args, focused: document.activeElement });
    const factory = new Function('document', 'button', 'currentUser', 'hasCapability', 'bootstrap', 'search',
        'openIssueStatusReplyModal', 'openWorkOrderModal', 'openBulkCloseModal', 'openIssueMuteModal',
        `${SOURCES}\nreturn issueActionsCell;`);
    const cell = factory(document, button, currentUser, hasCapability, bootstrap, () => { },
        modal('reply'), modal('assign'), modal('bulk'), modal('mute'));
    const group = { source: 'disk', eventId: 153, handlers: mine ? [{ handlerId: 7 }] : [] };
    return { result: cell(group), opened, hidden };
}

const inlineTexts = wrap => wrap.children.filter(c => c.tagName === 'BUTTON').map(b => b.textContent);
const menuOf = wrap => wrap.children.find(c => c.classList.contains('dropdown')) ?? null;
const menuTexts = wrap => {
    const menu = menuOf(wrap);
    return menu ? menu.find(n => n.classList.contains('dropdown-menu')).children.map(li => li.children[0].textContent) : [];
};

const cases = {
    // 四個都可用：回覆處理狀態留在列上，其餘三個收進「更多」
    fourActionsKeepFirstInlineRestInMenu() {
        const { result } = env({ caps: ['Handle', 'Assign', 'Maintain'], mine: true });
        assert.deepEqual(inlineTexts(result), ['回覆處理狀態']);
        assert.deepEqual(menuTexts(result), ['交辦', '統一標記', '靜音']);
    },
    // 管理者、不是自己的案件：三個動作，交辦留在列上
    threeActionsAdminNotMine() {
        const { result } = env({ caps: ['Handle', 'Assign', 'Maintain'], mine: false });
        assert.deepEqual(inlineTexts(result), ['交辦']);
        assert.deepEqual(menuTexts(result), ['統一標記', '靜音']);
    },
    // 只有兩個：全部橫排，不出選單（不必多點一下）
    twoActionsInlineWithoutMenu() {
        const { result } = env({ caps: ['Assign', 'Maintain'] });
        assert.deepEqual(inlineTexts(result), ['交辦', '靜音']);
        assert.equal(menuOf(result), null);
    },
    oneAction() {
        const { result } = env({ caps: ['Maintain'] });
        assert.deepEqual(inlineTexts(result), ['靜音']);
        assert.equal(menuOf(result), null);
    },
    noActionReturnsEmpty() {
        const { result } = env({ caps: [] });
        assert.equal(result, '');
        // 是自己的案件但沒有處理能力：不擺一顆一定失敗的按鈕
        assert.equal(env({ caps: [], mine: true }).result, '');
    },
    // 選單項：關閉選單、焦點先回到「更多」再開視窗（視窗關閉後焦點回到開啟者），且不觸發整列展開
    menuItemFocusesToggleBeforeOpening() {
        const { result, opened, hidden } = env({ caps: ['Handle', 'Assign', 'Maintain'] });
        const toggle = menuOf(result).children.find(c => c.tagName === 'BUTTON');
        assert(toggle.classList.contains('dropdown-toggle'));
        assert.equal(toggle.getAttribute('data-bs-toggle'), 'dropdown');
        assert.equal(JSON.parse(toggle.getAttribute('data-bs-popper-config')).strategy, 'fixed');
        toggle.focus = () => { document.activeElement = toggle; };
        const menu = menuOf(result).find(n => n.classList.contains('dropdown-menu'));
        const item = menu.children[1].children[0];
        // 選單這一層擋下冒泡（留白也不會觸發整列展開）；「更多」按鈕本身不擋（Bootstrap 收起其他選單要靠冒泡）
        let stopped = false;
        (menu.listeners.click || []).forEach(h => h({ stopPropagation: () => { stopped = true; } }));
        assert.equal(stopped, true, '選單要擋下點擊冒泡');
        assert.equal((toggle.listeners.click || []).length, 0, '「更多」按鈕不得阻止冒泡');
        (item.listeners.click || []).forEach(h => h({ stopPropagation() { }, preventDefault() { } }));
        assert.deepEqual(hidden, [toggle]);
        assert.equal(opened.length, 1);
        assert.equal(opened[0].name, 'mute');
        assert.equal(opened[0].focused, toggle);
    }
};

const name = process.argv[2];
if (!name) {
    console.log(Object.keys(cases).join('\n'));
    process.exit(0);
}
assert(cases[name], `未知案例：${name}`);
await cases[name]();
console.log(`PASS ${name}`);
