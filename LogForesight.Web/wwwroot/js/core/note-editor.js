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
import { confirmAction, showDetailModal, toast } from './ui.js';

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
 * @param {{draftKey?: string, maxLength?: number, ai?: {context: () => ({issueLabel?: string, hostCount?: number})},
 *          reuse?: {issueKey: () => (string|null)}, phrases?: boolean}} options
 *        draftKey 空＝不存草稿；ai 有值才顯示「AI 整理」按鈕（AI 不可用時按鈕整個不出現）；
 *        reuse 有值且 issueKey() 非 null 時顯示「沿用此問題上次的說明」；phrases 為 true 時顯示「常用語」選單
 * @returns {{clearDraft: () => void, setValue: (text: string) => void}}
 */
export function attachNoteEditor(textarea, { draftKey, maxLength = 1000, ai, reuse, phrases } = {}) {
    const counter = document.createElement('div');
    counter.className = 'form-text';
    counter.setAttribute('aria-live', 'polite');

    // 計數列：有附加工具時是「計數＋沿用／常用語（左）＋AI 整理按鈕（右）」一列，否則就是計數本身
    let counterRow = counter;
    let leftTools = null;
    if (ai || reuse || phrases) {
        counterRow = document.createElement('div');
        counterRow.className = 'd-flex justify-content-between align-items-center gap-2';
        leftTools = document.createElement('div');
        leftTools.className = 'd-flex align-items-center flex-wrap gap-2';
        leftTools.appendChild(counter);
        counterRow.appendChild(leftTools);
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

    if (reuse) attachReuseLink(textarea, leftTools, reuse.issueKey, setValue);
    if (phrases) attachPhraseMenu(textarea, leftTools, setValue);

    return { clearDraft, setValue };
}

/**
 * 「沿用此問題上次的說明」（回饋第 50 輪 C-4）：取同一問題簽章在自己可見主機內最新一筆說明。
 * issueKey() 為 null（沒勾或勾了多個問題）時連結不出現。
 */
function attachReuseLink(textarea, leftTools, issueKey, setValue) {
    if (!issueKey()) return;

    const hint = document.createElement('span');
    hint.className = 'small text-muted';
    hint.setAttribute('aria-live', 'polite');

    const link = linkButton('沿用此問題上次的說明', async () => {
        const key = issueKey();
        if (!key) return;
        hint.textContent = '';
        link.disabled = true;
        let found = null;
        try {
            found = await api.get(`/api/handling/last-note?${new URLSearchParams({ issueKey: key })}`);
        } catch {
            return;   // 錯誤訊息已由 api.js 顯示
        } finally {
            link.disabled = false;
        }
        if (!found || !found.note) {
            hint.textContent = '此問題還沒有其他人寫過說明';
            return;
        }
        if (textarea.value.trim()) {
            const preview = found.note.length > 60 ? `${found.note.slice(0, 60)}…` : found.note;
            const confirmed = await confirmAction({
                title: `要以 ${found.hostName} ${found.date} 的說明取代目前內容嗎？`,
                message: `目前說明欄的內容會被取代為：\n${preview}`,
                confirmText: '取代',
                confirmVariant: 'primary'
            });
            if (!confirmed) return;
        }
        setValue(found.note);
    });
    link.classList.remove('ms-2');
    leftTools.append(link, hint);
}

/**
 * 常用語（模組內快取，存 Promise：同頁多個說明欄共用一次請求；失敗時清掉快取、下次展開再試）。
 * 「管理常用語…」儲存後以伺服器回傳的清單換掉快取，其他說明欄下次展開即看到新清單。
 */
let phrasesPromise = null;

function loadPhrases() {
    if (!phrasesPromise) {
        phrasesPromise = api.get('/api/me/note-phrases').catch(err => {
            phrasesPromise = null;
            throw err;
        });
    }
    return phrasesPromise;
}

/** 插入常用語：原本有焦點就插在游標處，否則附加在結尾（前面自動補換行） */
function insertPhrase(textarea, phrase, hadFocus, setValue) {
    const value = textarea.value;
    let next;
    let caret;
    if (hadFocus.focused) {
        const start = hadFocus.start;
        const end = hadFocus.end;
        next = value.slice(0, start) + phrase + value.slice(end);
        caret = start + phrase.length;
    } else {
        const prefix = value && !value.endsWith('\n') ? '\n' : '';
        next = value + prefix + phrase;
        caret = next.length;
    }
    setValue(next);   // 走 input 事件：計數與草稿一併更新
    textarea.focus();
    textarea.setSelectionRange(caret, caret);
}

function attachPhraseMenu(textarea, leftTools, setValue) {
    const wrap = document.createElement('div');
    wrap.className = 'dropdown';

    const toggle = document.createElement('button');
    toggle.type = 'button';
    toggle.className = 'btn btn-link btn-sm p-0 align-baseline dropdown-toggle';
    toggle.setAttribute('data-bs-toggle', 'dropdown');
    toggle.setAttribute('aria-expanded', 'false');
    toggle.textContent = '常用語';

    const menu = document.createElement('ul');
    menu.className = 'dropdown-menu';
    wrap.append(toggle, menu);
    leftTools.appendChild(wrap);

    // 點開選單前記下說明欄是否有焦點與游標位置——按下按鈕後焦點就移走了
    const hadFocus = { focused: false, start: 0, end: 0 };
    toggle.addEventListener('pointerdown', () => {
        hadFocus.focused = document.activeElement === textarea;
        hadFocus.start = textarea.selectionStart;
        hadFocus.end = textarea.selectionEnd;
    });
    toggle.addEventListener('keydown', () => {
        hadFocus.focused = false;   // 鍵盤操作時焦點本來就在按鈕上
    });

    function menuItem(text, onClick, extraClass) {
        const li = document.createElement('li');
        const btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'dropdown-item text-wrap' + (extraClass ? ` ${extraClass}` : '');
        btn.textContent = text;
        btn.addEventListener('click', onClick);
        li.appendChild(btn);
        return li;
    }

    function renderMenu(data) {
        const items = [];
        for (const phrase of data.phrases ?? []) {
            items.push(menuItem(phrase, () => insertPhrase(textarea, phrase, hadFocus, setValue)));
        }
        if (items.length === 0) {
            const empty = document.createElement('li');
            const text = document.createElement('span');
            text.className = 'dropdown-item-text small text-muted';
            text.textContent = '還沒有常用語';
            empty.appendChild(text);
            items.push(empty);
        }
        const divider = document.createElement('li');
        const hr = document.createElement('hr');
        hr.className = 'dropdown-divider';
        divider.appendChild(hr);
        items.push(divider, menuItem('管理常用語…', () => openPhraseManager(data)));
        menu.replaceChildren(...items);
    }

    wrap.addEventListener('show.bs.dropdown', () => {
        const loading = document.createElement('li');
        const text = document.createElement('span');
        text.className = 'dropdown-item-text small text-muted';
        text.textContent = '載入中…';
        loading.appendChild(text);
        menu.replaceChildren(loading);
        loadPhrases().then(renderMenu).catch(() => {
            text.textContent = '常用語載入失敗，請再試一次';
        });
    });
}

/** 「管理常用語」modal：一行一條，儲存＝個人清單；恢復預設＝刪除個人清單 */
function openPhraseManager(data) {
    const body = document.createElement('div');

    if (data.isDefault) {
        const hint = document.createElement('div');
        hint.className = 'lf-hint mb-2';
        hint.textContent = '目前使用全站預設，儲存後改用你自己的清單';
        body.appendChild(hint);
    }

    const label = document.createElement('label');
    label.className = 'form-label small text-muted';
    label.textContent = '一行一條，最多 20 條、每條最多 200 字';
    const input = document.createElement('textarea');
    input.className = 'form-control form-control-sm mb-3';
    input.rows = 8;
    input.value = (data.phrases ?? []).join('\n');
    label.htmlFor = input.id = `lf-phrases-${Math.random().toString(36).slice(2, 10)}`;

    const actions = document.createElement('div');
    actions.className = 'd-flex gap-2';
    const save = document.createElement('button');
    save.type = 'button';
    save.className = 'btn btn-primary btn-sm';
    save.textContent = '儲存';
    const reset = document.createElement('button');
    reset.type = 'button';
    reset.className = 'btn btn-outline-secondary btn-sm';
    reset.textContent = '恢復預設';
    actions.append(save, reset);
    body.append(label, input, actions);

    async function submit(list) {
        save.disabled = reset.disabled = true;
        try {
            const result = await api.put('/api/me/note-phrases', { phrases: list });
            phrasesPromise = Promise.resolve(result);
            toast(result.isDefault ? '已恢復為全站預設常用語' : '已儲存常用語', 'success');
            bootstrap.Modal.getInstance(body.closest('.modal')).hide();
        } catch {
            // 驗證失敗（超過條數或字數）的訊息已由 api.js 顯示，留在 modal 讓使用者修改
        } finally {
            save.disabled = reset.disabled = false;
        }
    }

    save.addEventListener('click', () => submit(input.value.split('\n')));
    reset.addEventListener('click', () => submit([]));

    showDetailModal({ title: '管理常用語', body });
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
