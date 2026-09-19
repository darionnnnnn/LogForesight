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

import { api, getCurrentUser } from './api.js';
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

/**
 * AI 狀態（/api/ai/status）模組內快取：同頁多個說明欄只問一次。存的是 Promise，
 * 同時初始化的編輯器共用同一個請求；失敗時清掉快取、這次當不可用（按鈕不出現）。
 */
let aiStatusPromise = null;

function loadAiStatus() {
    if (!aiStatusPromise) {
        aiStatusPromise = api.get('/api/ai/status', { silent: true }).catch(() => {
            aiStatusPromise = null;
            return null;
        });
    }
    return aiStatusPromise;
}

const AI_FAILED_TEXT = 'AI 目前無法回應，請稍後再試';
const AI_TOO_SHORT_TEXT = 'AI 整理是把你已經寫下的內容整理成條列，不會補上你沒提到的事。內容太短時請直接填寫。';

function mutedHint(text) {
    const el = document.createElement('span');
    el.className = 'small text-muted ms-2';
    el.textContent = text;
    return el;
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
 * @param {{draftKey?: string, maxLength?: number, ai?: {context: () => ({issueLabel?: string, hostCount?: number})}}} options
 *        draftKey 空＝不存草稿；ai 有值才顯示「AI 整理」按鈕（AI 不可用時按鈕整個不出現）
 * @returns {{clearDraft: () => void, setValue: (text: string) => void}}
 */
export function attachNoteEditor(textarea, { draftKey, maxLength = 1000, ai } = {}) {
    const counter = document.createElement('div');
    counter.className = 'form-text';
    counter.setAttribute('aria-live', 'polite');

    // 計數列：有 AI 選項時是「計數（左）＋AI 整理按鈕（右）」一列，否則就是計數本身
    let counterRow = counter;
    if (ai) {
        counterRow = document.createElement('div');
        counterRow.className = 'd-flex justify-content-between align-items-center gap-2';
        counterRow.appendChild(counter);
    }
    textarea.after(counterRow);

    let storageKey = null;
    let timer = null;
    let notice = null;
    let aiControls = null;

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
        counterRow.before(notice);
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
        if (aiControls) aiControls.resetUndo();   // 送出成功：這次整理的復原狀態一併失效
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

    if (ai) {
        loadAiStatus().then(status => {
            if (!status || !status.available) return;   // 不可用：按鈕整個不出現（不是灰色停用）
            aiControls = attachAiTidy(textarea, counterRow, status, ai.context, setValue);
        });
    }

    return { clearDraft, setValue };
}

/**
 * 「AI 整理」按鈕（回饋第 50 輪批次C-3）：把說明欄目前的內容整理成四段條列、填回說明欄，
 * 使用者檢查修改後再送出。輸出只當文字框內容（setValue），不以 HTML 呈現。
 */
function attachAiTidy(textarea, counterRow, status, context, setValue) {
    const box = document.createElement('div');
    box.className = 'd-flex align-items-center flex-shrink-0';

    const emptyHint = document.createElement('span');
    emptyHint.className = 'small text-danger me-2';

    if (status.external) box.appendChild(mutedHint('內容會送至外部 AI 服務'));
    if (status.batchBusy) box.appendChild(mutedHint('AI 分析排程執行中，可能較慢'));

    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'btn btn-sm btn-outline-secondary ms-2';
    button.textContent = 'AI 整理';

    let requestSeq = 0;
    const abandon = linkButton('放棄', () => {
        requestSeq++;   // 這次回應到了也不採用
        setIdle();
    });
    abandon.classList.add('d-none');

    box.prepend(emptyHint);
    box.append(button, abandon);
    counterRow.appendChild(box);

    // 結果訊息列：放在計數列下方
    const message = document.createElement('div');
    message.className = 'small text-muted';
    message.setAttribute('aria-live', 'polite');
    counterRow.after(message);

    let undoText = null;

    function setIdle() {
        button.disabled = false;
        button.textContent = 'AI 整理';
        abandon.classList.add('d-none');
    }

    function showMessage(...parts) {
        message.replaceChildren(...parts);
    }

    function resetUndo() {
        undoText = null;
        showMessage();
    }

    textarea.addEventListener('input', () => {
        emptyHint.textContent = '';
    });

    button.addEventListener('click', async () => {
        const text = textarea.value;
        if (!text.trim()) {
            emptyHint.textContent = '請先輸入要整理的內容';
            return;
        }
        emptyHint.textContent = '';
        showMessage();

        const seq = ++requestSeq;
        button.disabled = true;
        button.textContent = '整理中…';
        abandon.classList.remove('d-none');

        let result = null;
        let errorText = null;
        try {
            result = await api.post('/api/ai/tidy-note', { text, ...context() }, { silent: true });
        } catch (err) {
            // 節流（429）與輸入驗證的伺服器訊息對使用者有意義；其餘一律同一句
            errorText = err.status === 429 || err.code === 'validation_failed' ? err.message : AI_FAILED_TEXT;
        }
        if (seq !== requestSeq) return;   // 已按「放棄」或又送了新的一次
        setIdle();

        if (errorText) {
            showMessage(errorText);
            return;
        }
        if (result && result.tooShort) {
            showMessage(AI_TOO_SHORT_TEXT);
            return;
        }
        if (!result || result.unavailable || result.failed || !result.text) {
            showMessage(AI_FAILED_TEXT);
            return;
        }

        undoText = textarea.value;   // 最近一次整理前的原文（等待期間若有修改，以修改後為準）
        setValue(result.text);
        const undo = linkButton('復原', () => {
            if (undoText === null) return;
            const original = undoText;
            resetUndo();
            setValue(original);
        });
        showMessage(result.truncated ? '已整理，請檢查後再送出（內容過長已截斷）' : '已整理，請檢查後再送出', undo);
    });

    return { resetUndo };
}
