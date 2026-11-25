/**
 * 交辦單頁單頭的參考資訊與捷徑：
 * - 處置參考（規則的影響、可能原因、處置步驟）與訊息樣本（成員中最近一次出現的樣本，最多 3 則），兩者都收在可展開區，
 *   展開不離開交辦單頁。規則不存在（多問題單、未命中規則）時處置參考不顯示，訊息樣本照常。
 * - 這個問題的捷徑：其他交辦單（連到已篩選的交辦總覽）、靜音這個問題、調整這條規則（直接開該規則的編輯）。
 *   沒有 Maintain 時只有「其他交辦單」；能不能看交辦總覽由呼叫端依 Assign／ViewAll 決定。
 *
 * 外部字串（訊息樣本、規則內文）一律 textContent，不解析 HTML。
 */

import { appUrl, ruleEditUrl } from '../core/paths.js';
import { roleText } from '../core/elements.js';
import { button, linkButton } from '../core/ui.js';

/** 一組「小標＋清單」；沒有項目就不畫。ordered＝有先後的步驟 */
function listSection(parent, label, items, ordered) {
    if (!items || items.length === 0) return;
    const title = document.createElement('div');
    title.className = 'lf-guidance__label';
    title.textContent = label;
    parent.appendChild(title);

    const list = document.createElement(ordered ? 'ol' : 'ul');
    list.className = 'lf-guidance__list';
    for (const item of items) {
        const li = document.createElement('li');
        li.textContent = item;
        list.appendChild(li);
    }
    parent.appendChild(list);
}

/**
 * 處置參考面板：影響、可能原因、處置步驟（白話說明已在單頭上方，不重複）。
 * 沒有內容（規則缺席、或規則只有白話說明）回 null——呼叫端就不放「處置參考」展開鈕。
 */
export function guidancePanel(guidance) {
    if (!guidance) return null;
    const wrap = document.createElement('div');
    wrap.className = 'lf-guidance';

    if (guidance.impact) {
        const label = document.createElement('div');
        label.className = 'lf-guidance__label';
        label.textContent = '影響';
        const text = document.createElement('div');
        text.textContent = guidance.impact;
        wrap.append(label, text);
    }
    listSection(wrap, '可能原因', guidance.likelyCauses, false);
    listSection(wrap, '處置步驟', guidance.nextSteps, true);

    return wrap.children.length > 0 ? wrap : null;
}

/** 訊息樣本面板：逐則一段（等寬字型、保留原始換行）；沒有樣本回 null（呼叫端不放展開鈕） */
export function sampleMessagesPanel(messages) {
    const items = (messages ?? []).filter(m => typeof m === 'string' && m.trim() !== '');
    if (items.length === 0) return null;

    const wrap = document.createElement('div');
    wrap.className = 'lf-sample-messages';
    wrap.appendChild(roleText('lf-text-meta', '成員中最近一次出現的訊息樣本'));
    for (const message of items) {
        const block = document.createElement('pre');
        block.className = 'lf-sample-messages__item';
        block.textContent = message;
        wrap.appendChild(block);
    }
    return wrap;
}

/** 交辦總覽篩到這個問題的連結（來源＋事件）；多問題單沒有單一問題，回 null */
export function otherOrdersHref(detail) {
    if (!detail?.source || detail.eventId === null || detail.eventId === undefined) return null;
    return appUrl('/work-orders') + '?' + new URLSearchParams({ source: detail.source, eventId: String(detail.eventId) });
}

/** 「調整這條規則」的連結；沒有命中規則回 null */
export function ruleEditHref(detail) {
    return detail?.ruleId ? appUrl(ruleEditUrl(detail.ruleId)) : null;
}

/**
 * 這個問題的捷徑列。
 * @param {object} detail 交辦單詳情
 * @param {{canListOrders: boolean, canMaintain: boolean, onMute: () => void}} options
 *        canListOrders＝能看交辦總覽（Assign 或 ViewAll）；canMaintain＝具 Maintain；onMute＝開靜音視窗
 * @returns {HTMLElement|null} 一個捷徑都沒有（多問題單、或沒有任何權限）回 null
 */
export function issueShortcuts(detail, { canListOrders, canMaintain, onMute }) {
    const nodes = [];
    const ordersHref = otherOrdersHref(detail);
    if (ordersHref && canListOrders) {
        nodes.push(linkButton('這個問題的其他交辦單', ordersHref, { variant: 'outline-secondary', title: '到交辦總覽，只看這個問題的交辦單' }));
    }
    if (ordersHref && canMaintain && !detail.closedAt) {
        nodes.push(button('靜音這個問題', { variant: 'outline-secondary', opens: 'dialog', title: '暫時不看這個問題（到期自動恢復）', onClick: () => onMute() }));
    }
    const ruleHref = ruleEditHref(detail);
    if (ruleHref && canMaintain) {
        nodes.push(linkButton('調整這條規則', ruleHref, { variant: 'outline-secondary', title: '到規則維護開啟這條規則的編輯' }));
    }
    if (nodes.length === 0) return null;

    const row = document.createElement('div');
    row.className = 'd-flex flex-wrap gap-2 mt-2 lf-no-print';
    row.append(...nodes);
    return row;
}

/**
 * 代為結案可選的範圍：全部進行中（整張單結案，現行行為）、只有上報中的、勾選的成員。
 * 後兩者只有在確實有上報成員／有勾選時才出現；沒有任何額外範圍時只剩「全部」，畫面不必顯示選項。
 * caseIds 為 null＝不帶案件清單（伺服器端是全部進行中成員）。
 * @param {{activeCount: number, escalatedCaseIds?: string[], selectedCaseIds?: string[]}} input activeCount 含停用主機上的進行中成員
 */
export function adminCloseScopeOptions({ activeCount, escalatedCaseIds = [], selectedCaseIds = [] }) {
    const options = [{ value: 'all', name: '全部進行中的成員', label: `全部進行中（${activeCount} 台）`, count: activeCount, caseIds: null }];
    if (escalatedCaseIds.length > 0) {
        options.push({ value: 'escalated', name: '上報中的成員', label: `只有上報中的（${escalatedCaseIds.length} 台）`, count: escalatedCaseIds.length, caseIds: [...escalatedCaseIds] });
    }
    if (selectedCaseIds.length > 0) {
        options.push({ value: 'selected', name: '勾選的成員', label: `勾選的成員（${selectedCaseIds.length} 台）`, count: selectedCaseIds.length, caseIds: [...selectedCaseIds] });
    }
    return options;
}

/** 預設範圍：有上報成員時是「只有上報中的」（上報的是處理人處理不了、等管理者決定的那幾台），否則全部 */
export function defaultAdminCloseScope(options) {
    return options.some(option => option.value === 'escalated') ? 'escalated' : 'all';
}
