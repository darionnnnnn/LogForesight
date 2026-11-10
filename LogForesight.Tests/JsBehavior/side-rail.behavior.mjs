/**
 * 第 66 輪 D2：右側面板側軌（core/side-rail.js）與 recordDetailUrl（core/paths.js）的 production 行為測試。
 * 直接 import production 模組，只替換 DOM；由 SideRailBehaviorTests 以 `node side-rail.behavior.mjs <案例名>` 逐案執行。
 */

import assert from 'node:assert/strict';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { installDom } from './mini-dom.mjs';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const jsRoot = path.join(scriptDir, '..', '..', 'LogForesight.Web', 'wwwroot', 'js');

const dom = installDom();
globalThis.window = { LF_BASE: '', matchMedia: () => ({ matches: false }) };

// 替身節點沒有 focus／scrollIntoView：統一記錄被呼叫的元素
const focused = [];
const scrolled = [];
function make(tag, className = '') {
    const el = dom.newElement(tag);
    el.className = className;
    el.focus = () => { focused.push(el); };
    el.scrollIntoView = () => { scrolled.push(el); };
    return el;
}
const realCreate = document.createElement;
document.createElement = tag => { const el = realCreate(tag); el.focus = () => { focused.push(el); }; el.scrollIntoView = () => { scrolled.push(el); }; return el; };

const { createSideRail } = await import(pathToFileURL(path.join(jsRoot, 'core', 'side-rail.js')).href);
const { recordDetailUrl } = await import(pathToFileURL(path.join(jsRoot, 'core', 'paths.js')).href);

function card(titleText) {
    const section = make('section', 'lf-card');
    const header = make('div', 'lf-card__header');
    const title = make('h2', 'lf-card__title');
    title.textContent = titleText;
    header.appendChild(title);
    section.appendChild(header);
    section._title = title;
    return section;
}

function build() {
    focused.length = 0; scrolled.length = 0;
    const layout = make('div', 'lf-detail-layout');
    const side = make('aside', 'lf-detail-side');
    side.id = 'record-side';
    const sideBody = make('div', 'lf-detail-side__body');
    const handling = card('處理狀態');
    const log = card('處理歷程');
    sideBody.append(handling, log);
    side.appendChild(sideBody);
    layout.appendChild(side);
    const rail = createSideRail({
        layout, side,
        items: [
            { key: 'handling', label: '處理狀態', target: handling },
            { key: 'log', label: '處理歷程', target: log }
        ]
    });
    const nav = side.children[0];
    return { layout, side, sideBody, handling, log, rail, nav, buttons: nav.children };
}
const collapseOf = handling => handling.querySelector('.lf-side-rail__collapse');
const click = el => (el.listeners.click || []).forEach(h => h({ stopPropagation() { } }));
const OPEN = 'lf-detail-layout--side-open';

const cases = {
    initialCollapsed() {
        const { layout, buttons, nav, side } = build();
        assert.equal(nav.tagName, 'NAV');
        assert.equal(nav.getAttribute('aria-label'), '右側面板');
        assert.equal(buttons.length, 2);
        for (const b of buttons) {
            assert.equal(b.type, 'button');
            assert.equal(b.getAttribute('aria-expanded'), 'false');
            assert.equal(b.getAttribute('aria-controls'), side.id);
        }
        assert(!layout.classList.contains(OPEN));
    },
    clickExpandsAndFocusesHeading() {
        const { layout, buttons, rail, handling } = build();
        click(buttons[0]);
        assert(layout.classList.contains(OPEN));
        assert(rail.isExpanded());
        assert.equal(buttons[0].getAttribute('aria-expanded'), 'true');
        // 欄頂的收合鈕也控制同一個區塊，aria-expanded 要同步
        assert.equal(collapseOf(handling).getAttribute('aria-expanded'), 'true');
        assert(rail.hasUserChoice());
        assert.equal(focused.at(-1), handling._title);
        assert.equal(handling._title.getAttribute('tabindex'), '-1');
        assert(scrolled.includes(handling));
    },
    collapseReturnsFocusToTrigger() {
        const { layout, buttons, rail, handling } = build();
        click(buttons[1]);
        rail.collapse();
        assert(!layout.classList.contains(OPEN));
        assert(!rail.isExpanded());
        assert.equal(buttons[1].getAttribute('aria-expanded'), 'false');
        assert.equal(collapseOf(handling).getAttribute('aria-expanded'), 'false');
        assert.equal(focused.at(-1), buttons[1]);
    },
    collapseButtonMarksUserChoice() {
        const { handling, rail, buttons } = build();
        const collapseBtn = collapseOf(handling);
        assert.equal(collapseBtn.type, 'button');
        assert(collapseBtn.textContent.includes('收合'));
        rail.expand('handling', { focus: false });
        assert(!rail.hasUserChoice());
        click(collapseBtn);
        assert(rail.hasUserChoice());
        assert(!rail.isExpanded());
        assert.equal(focused.at(-1), buttons[0]);
    },
    setSummaryUpdatesButton() {
        const { buttons, rail } = build();
        rail.setSummary('handling', '未處理（0/4 已結案）・逾期');
        assert(buttons[0].textContent.includes('未處理（0/4 已結案）・逾期'));
        assert(buttons[0].getAttribute('title').includes('逾期'));
        assert(!buttons[1].textContent.includes('逾期'));
    },
    expandWithoutFocusDoesNotFocus() {
        const { layout, rail } = build();
        rail.expand('handling', { focus: false });
        assert(layout.classList.contains(OPEN));
        assert.equal(focused.length, 0);
        assert(!rail.hasUserChoice());
    },
    expandWithoutScrollDoesNotScroll() {
        const { rail } = build();
        rail.expand('handling', { focus: false, scroll: false });
        assert.equal(scrolled.length, 0);
    },
    // 斷點只在側軌模組一處：與 site.css 的 lg 斷點同值（992px），頁面層一律問 rail.isRailMode()
    isRailModeFollowsLgBreakpoint() {
        const queries = [];
        const original = globalThis.window.matchMedia;
        try {
            globalThis.window.matchMedia = q => { queries.push(q); return { matches: true }; };
            const { rail } = build();
            queries.length = 0;   // 建立時另會問一次寬螢幕預設展開，這裡只驗 isRailMode 問的斷點
            assert.equal(rail.isRailMode(), true);
            assert.deepEqual(queries, ['(min-width: 992px)']);
            globalThis.window.matchMedia = () => ({ matches: false });
            assert.equal(rail.isRailMode(), false);
            globalThis.window.matchMedia = undefined;
            assert.equal(rail.isRailMode(), false);
        } finally {
            globalThis.window.matchMedia = original;
        }
    },
    wideViewportOpensByDefault() {
        const original = globalThis.window.matchMedia;
        try {
            globalThis.window.matchMedia = q => ({ matches: q.includes('1600') || q.includes('992') });
            const { layout, buttons, rail } = build();
            assert(layout.classList.contains(OPEN), '>= 1600px 預設展開');
            assert.equal(buttons[0].getAttribute('aria-expanded'), 'true');
            assert.equal(focused.length, 0, '預設展開不搶焦點');
            assert.equal(scrolled.length, 0, '預設展開不捲動');
            assert.equal(rail.hasUserChoice(), false, '預設展開不算使用者的選擇');
        } finally {
            globalThis.window.matchMedia = original;
        }
    },
    belowWideKeepsCollapsed() {
        const original = globalThis.window.matchMedia;
        try {
            globalThis.window.matchMedia = q => ({ matches: q.includes('992') });
            const { layout } = build();
            assert(!layout.classList.contains(OPEN), '1599px（側軌模式但未達寬螢幕）維持收合');
        } finally {
            globalThis.window.matchMedia = original;
        }
    },
    wideDefaultThenUserCollapseStaysCollapsed() {
        const original = globalThis.window.matchMedia;
        try {
            globalThis.window.matchMedia = q => ({ matches: q.includes('1600') || q.includes('992') });
            const { layout, handling, rail } = build();
            click(collapseOf(handling));
            assert(!layout.classList.contains(OPEN));
            assert.equal(rail.hasUserChoice(), true);
        } finally {
            globalThis.window.matchMedia = original;
        }
    },
    recordDetailUrlHandle() {
        assert.equal(recordDetailUrl(1, '2026-11-05', { handle: true }), '/records/1/2026-11-05#handling');
        assert.equal(recordDetailUrl(1, '2026-11-05'), '/records/1/2026-11-05');
        assert(!recordDetailUrl(1, '2026-11-05', {}).includes('#'));
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
