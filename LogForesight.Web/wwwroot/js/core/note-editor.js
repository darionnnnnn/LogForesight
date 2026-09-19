/**
 * 說明輸入增強（docs/DESIGN-SYSTEM.md §6b「長文字輸入自動保留草稿」）：字數計數＋本機草稿。
 *
 * 草稿存在瀏覽器本機儲存區，鍵為 `lf-draft:{userId}:{draftKey}`——帶 userId 是因為同一台
 * 電腦可能輪流登入不同帳號，A 打到一半的內容不能出現在 B 的畫面上。登出時由 layout.js
 * 呼叫 clearAllDraftsForUser 清掉；工作階段逾時（api.js 收到 401 導回登入頁）刻意不清——
 * 那正是草稿要救的情境。
 *
 * 本機儲存區在隱私模式或被政策停用時存取會擲例外：每一處存取都包 try/catch，
 * 不可用就當作沒有草稿，計數與輸入照常運作。
 */

import { getCurrentUser } from './api.js';
import { confirmAction } from './ui.js';

const KEY_PREFIX = 'lf-draft:';
const SAVE_DELAY_MS = 500;

/**
 * 尚未寫入的防抖儲存（鍵 → 立即寫入函式）：表單重建時舊 textarea 的防抖還沒到期，
 * 新的編輯器初始化前先把它寫掉，才不會拿到落後一拍的草稿、誤判成「有不同的草稿」
 */
const pendingSaves = new Map();

function readDraft(key) {
    try {
        return localStorage.getItem(key);
    } catch {
        return null;
    }
}

function writeDraft(key, text) {
    try {
        if (text) localStorage.setItem(key, text);
        else localStorage.removeItem(key);
    } catch {
        // 本機儲存區不可用：略過草稿
    }
}

/** 刪除某使用者的全部草稿（登出時呼叫） */
export function clearAllDraftsForUser(userId) {
    if (userId === null || userId === undefined) return;
    const prefix = `${KEY_PREFIX}${userId}:`;
    try {
        const keys = [];
        for (let i = 0; i < localStorage.length; i++) {
            const key = localStorage.key(i);
            if (key?.startsWith(prefix)) keys.push(key);
        }
        for (const key of keys) localStorage.removeItem(key);
    } catch {
        // 本機儲存區不可用：沒有草稿可清
    }
}

function linkButton(text, onClick) {
    const btn = document.createElement('button');
    btn.type = 'button';   // 放在 form 裡，不指定會變成送出鈕
    btn.className = 'btn btn-link btn-sm p-0 ms-2 align-baseline';
    btn.textContent = text;
    btn.addEventListener('click', onClick);
    return btn;
}

/**
 * @param {HTMLTextAreaElement} textarea 已放進 DOM 的說明欄（計數列插在它之後）
 * @param {{draftKey?: string, maxLength?: number}} options draftKey 空＝不存草稿
 * @returns {{clearDraft: () => void, setValue: (text: string) => void}}
 */
export function attachNoteEditor(textarea, { draftKey, maxLength = 1000 } = {}) {
    const counter = document.createElement('div');
    counter.className = 'form-text';
    counter.setAttribute('aria-live', 'polite');
    textarea.after(counter);

    let storageKey = null;
    let timer = null;
    let notice = null;

    function updateCounter() {
        const length = textarea.value.length;   // 與後端 StringLength 同口徑（UTF-16 字串長度）
        const over = length - maxLength;
        counter.textContent = over > 0
            ? `${length} / ${maxLength}，超過上限 ${over} 字`
            : `${length} / ${maxLength}`;
        counter.classList.toggle('text-danger', over > 0);
        textarea.setCustomValidity(over > 0 ? `處理說明不可超過 ${maxLength} 字` : '');
    }

    function saveNow() {
        clearTimeout(timer);
        timer = null;
        if (!storageKey) return;
        pendingSaves.delete(storageKey);
        writeDraft(storageKey, textarea.value);
    }

    function scheduleSave() {
        if (!storageKey) return;
        clearTimeout(timer);
        timer = setTimeout(saveNow, SAVE_DELAY_MS);
        pendingSaves.set(storageKey, saveNow);
    }

    function removeNotice() {
        notice?.remove();
        notice = null;
    }

    /** 程式帶入文字：走 input 事件讓呼叫端的監聽（表單暫存、清紅框）一併更新，草稿立即寫入 */
    function setValue(text) {
        textarea.value = text ?? '';
        textarea.dispatchEvent(new Event('input', { bubbles: true }));
        saveNow();
    }

    function showNotice(parts) {
        removeNotice();
        notice = document.createElement('div');
        notice.className = 'text-muted small';
        notice.append(...parts);
        counter.before(notice);
    }

    function restoreDraft() {
        const draft = readDraft(storageKey);
        if (!draft || draft === textarea.value) return;

        if (!textarea.value) {
            textarea.value = draft;
            textarea.dispatchEvent(new Event('input', { bubbles: true }));
            // 捨棄＝清空說明欄；setValue 以空值寫入即刪除草稿鍵
            showNotice(['已還原未送出的草稿', linkButton('捨棄', () => {
                removeNotice();
                setValue('');
            })]);
            return;
        }

        const preview = draft.length > 20 ? `${draft.slice(0, 20)}…` : draft;
        showNotice([
            `有未送出的草稿（${preview}）`,
            linkButton('帶入', async () => {
                const confirmed = await confirmAction({
                    title: '要以草稿取代目前的說明嗎？',
                    message: '目前說明欄的內容會被草稿取代。',
                    confirmText: '帶入',
                    confirmVariant: 'primary'
                });
                if (!confirmed) return;
                removeNotice();
                setValue(draft);
            }),
            linkButton('捨棄', () => {
                removeNotice();
                clearDraft();
            })
        ]);
    }

    function clearDraft() {
        clearTimeout(timer);
        timer = null;
        if (!storageKey) return;
        pendingSaves.delete(storageKey);
        writeDraft(storageKey, '');
    }

    textarea.addEventListener('input', () => {
        updateCounter();
        scheduleSave();
    });
    updateCounter();

    if (draftKey) {
        getCurrentUser().then(user => {
            if (user?.userId === null || user?.userId === undefined) return;
            storageKey = `${KEY_PREFIX}${user.userId}:${draftKey}`;
            pendingSaves.get(storageKey)?.();
            restoreDraft();
        }).catch(() => {
            // 取不到使用者（401 已由 api.js 導向登入頁）：不做草稿
        });
    }

    return { clearDraft, setValue };
}
