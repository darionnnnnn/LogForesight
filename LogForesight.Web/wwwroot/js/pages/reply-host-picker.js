/**
 * 回覆彈窗內的「只回覆部分主機」就地面板（不另開 modal）：列出這張單進行中的成員、可勾選（跨頁保留）、
 * 以主機名搜尋或一次貼上多個主機名自動勾選。
 *
 * 勾選狀態用全站共用的 createPageSelection（與主機頁、權限異動頁、交辦單成員表同一套），
 * 這裡只負責清單、搜尋與貼上。資料由呼叫端的 `load` 提供（只取進行中成員）：
 * `load({ page, pageSize, hostNames }) → { items, total, page, pageSize }`，hostNames 為 null 表示不篩選。
 *
 * 搜尋規則：輸入框內容以換行、逗號、空白切開；一個名稱＝以該主機名篩選清單（伺服器端分頁），
 * 兩個以上＝貼上清單：逐批查詢、完全相符（不分大小寫）的主機自動勾選並列在清單，找不到的名稱逐一列出。
 * 貼上（paste）後切出兩個以上名稱時自動執行同一個 apply（與按鈕、Enter 同一條路徑）；只有一個名稱不自動篩選，
 * 避免貼上單一名稱或打字途中就觸發查詢。
 */

import { createPageSelection, renderTable, renderPagination, button } from '../core/ui.js';
import { createSeqGuard } from '../core/async-guards.js';
import { roleText } from '../core/elements.js';
import { statusBadgeMeta } from '../core/handling-statuses.js';

const PAGE_SIZE = 20;
/** 貼上清單查詢的批大小與每批頁大小：查詢字串有長度上限（IIS 預設 2048 字元），名稱分批送 */
const LOOKUP_NAMES_PER_REQUEST = 40;
const LOOKUP_PAGE_SIZE = 200;

/** 輸入框文字切成名稱清單（換行、逗號、頓號、分號、空白皆為分隔），保留順序並以不分大小寫去重 */
export function parseHostNames(text) {
    const seen = new Set();
    const names = [];
    for (const token of String(text ?? '').split(/[\s,，、;；]+/)) {
        const name = token.trim();
        if (!name || seen.has(name.toUpperCase())) continue;
        seen.add(name.toUpperCase());
        names.push(name);
    }
    return names;
}

/**
 * @param {object} options
 * @param {(params: {page: number, pageSize: number, hostNames: string[]|null}) => Promise<{items: object[], total: number, page: number, pageSize: number}>} options.load
 * @param {() => void} [options.onChange] 勾選集合有變時呼叫
 */
export function createHostPicker({ load, onChange }) {
    const element = document.createElement('div');
    element.id = 'wo-reply-picker';
    element.className = 'mb-3 d-none';

    const searchLabel = document.createElement('label');
    searchLabel.className = 'form-label lf-text-label';
    searchLabel.htmlFor = 'wo-reply-pick-search';
    searchLabel.textContent = '搜尋或貼上主機名';
    const search = document.createElement('textarea');
    search.id = 'wo-reply-pick-search';
    search.className = 'form-control form-control-sm';
    search.rows = 2;
    search.placeholder = '輸入一個主機名篩選清單，或貼上多個主機名（換行、逗號、空白分隔）自動勾選';
    const applyBtn = button('篩選／勾選', { onClick: () => apply() });
    applyBtn.id = 'wo-reply-pick-apply';
    applyBtn.classList.add('mt-1');

    const countEl = document.createElement('span');
    countEl.id = 'wo-reply-pick-count';
    countEl.className = 'lf-text-meta ms-2 d-none';
    const toolbar = document.createElement('div');
    toolbar.className = 'd-flex flex-wrap align-items-center gap-1 mb-2';
    toolbar.append(applyBtn, countEl);

    const missingEl = document.createElement('div');
    missingEl.id = 'wo-reply-pick-missing';
    missingEl.className = 'lf-text-meta text-wrap mb-2 d-none';
    missingEl.setAttribute('aria-live', 'polite');

    const tableBox = document.createElement('div');
    const pagerBox = document.createElement('div');
    pagerBox.className = 'mt-2';
    element.append(searchLabel, search, toolbar, missingEl, tableBox, pagerBox);

    let items = [];
    let total = 0;
    let page = 1;
    /** 目前清單的來源：server＝伺服器端篩選分頁；pasted＝貼上清單的比對結果（前端分頁） */
    let mode = 'server';
    let filterNames = null;
    let pastedItems = [];
    /** 這張單進行中成員的總台數：只在「不篩選」的查詢取得（篩選與貼上清單的 total 是相符台數），沒查過為 null */
    let activeTotal = null;
    const seqGuard = createSeqGuard();

    const selection = createPageSelection({
        keyOf: item => item.caseId,
        isSelectable: () => true,
        getPageItems: () => items,
        labels: { row: '選取這台主機', all: '全選本頁主機' },
        bar: { barId: 'wo-reply-pick-count', countId: 'wo-reply-pick-count', unit: '台' },
        onSelectAll: () => render(),
        onChange
    });

    function render() {
        const emptyTitle = mode === 'pasted' || filterNames ? '沒有符合條件的進行中主機' : '這張單沒有進行中的主機';
        renderTable(tableBox, {
            columns: [
                { title: '', className: 'text-center text-nowrap', renderHeader: () => selection.selectAllHeaderCheckbox(), render: item => selection.rowCheckboxCell(item) },
                { title: '主機', render: item => roleText('lf-text-title', item.hostName || '') },
                { title: '狀態', className: 'text-nowrap', render: item => roleText('lf-text-meta', statusBadgeMeta(item.status)?.label ?? item.status ?? '') }
            ],
            rows: items,
            empty: { title: emptyTitle, hint: '可清除搜尋框的內容，或改輸入其他主機名。' }
        });
        selection.sync();
        renderPagination(pagerBox, { page, totalPages: Math.ceil(total / PAGE_SIZE), onPage: goto });
    }

    async function goto(target) {
        if (mode === 'pasted') {
            page = target;
            items = pastedItems.slice((page - 1) * PAGE_SIZE, page * PAGE_SIZE);
            render();
            return;
        }
        const seq = seqGuard.next();
        const unfiltered = !filterNames;
        let data;
        try {
            data = await load({ page: target, pageSize: PAGE_SIZE, hostNames: filterNames });
        } catch {
            return;   // 錯誤提示已由共用請求層顯示；清單維持原樣
        }
        // 不篩選的結果即使因連續操作被視為過時（清單不更新），進行中總台數仍然正確，照記
        if (unfiltered) activeTotal = data.total ?? (data.items || []).length;
        if (!seqGuard.isCurrent(seq)) return;   // 連續操作時慢回來的舊結果不蓋掉新的
        items = data.items || [];
        total = data.total ?? items.length;
        page = data.page ?? target;
        render();
    }

    /** 貼上清單：分批查詢全部相符的進行中成員 */
    async function lookupAll(names) {
        const found = [];
        for (let i = 0; i < names.length; i += LOOKUP_NAMES_PER_REQUEST) {
            const chunk = names.slice(i, i + LOOKUP_NAMES_PER_REQUEST);
            for (let p = 1; ; p++) {
                const data = await load({ page: p, pageSize: LOOKUP_PAGE_SIZE, hostNames: chunk });
                const got = data.items || [];
                found.push(...got);
                const size = data.pageSize ?? LOOKUP_PAGE_SIZE;
                // 取到空頁、不足一頁或已涵蓋總數就停（收斂條件，不靠 total 單獨判斷）
                if (got.length === 0 || got.length < size || p * size >= (data.total ?? 0)) break;
            }
        }
        return found;
    }

    async function apply() {
        const names = parseHostNames(search.value);
        const seq = seqGuard.next();
        missingEl.classList.add('d-none');
        missingEl.textContent = '';

        if (names.length < 2) {
            mode = 'server';
            filterNames = names.length === 1 ? names : null;
            await goto(1);
            return;
        }

        let found;
        try {
            found = await lookupAll(names);
        } catch {
            return;
        }
        if (!seqGuard.isCurrent(seq)) return;
        const unique = new Map(found.map(item => [item.caseId, item]));
        const matchedNames = new Set([...unique.values()].map(item => String(item.hostName || '').toUpperCase()));
        for (const item of unique.values()) selection.selected.set(item.caseId, item);
        selection.renderBar();

        const missing = names.filter(name => !matchedNames.has(name.toUpperCase()));
        if (missing.length > 0) {
            missingEl.textContent = `找不到 ${missing.length} 個進行中的主機：${missing.join('、')}`;
            missingEl.classList.remove('d-none');
        }
        mode = 'pasted';
        pastedItems = [...unique.values()];
        total = pastedItems.length;
        page = 1;
        items = pastedItems.slice(0, PAGE_SIZE);
        render();
    }

    search.addEventListener('keydown', event => {
        // Enter 套用；Shift＋Enter 留給換行（貼上清單時可手動換行）；輸入法組字中的 Enter 不算
        if (event.key !== 'Enter' || event.shiftKey || event.isComposing) return;
        event.preventDefault();
        apply();
    });

    // 貼上時輸入框的值要等事件之後才更新：延到下一個工作再讀；兩個以上名稱才自動套用
    search.addEventListener('paste', () => {
        setTimeout(() => {
            if (parseHostNames(search.value).length >= 2) apply();
        }, 0);
    });

    return {
        element,
        selected: selection.selected,
        /** 第一次顯示面板時載入第一頁 */
        open: () => (items.length === 0 && mode === 'server' ? goto(1) : Promise.resolve()),
        selectedIds: () => [...selection.selected.keys()],
        /** 進行中成員總台數（還沒載入過清單時為 null）：回覆彈窗用來算「其餘 N 台」 */
        activeTotal: () => activeTotal,
        apply
    };
}
