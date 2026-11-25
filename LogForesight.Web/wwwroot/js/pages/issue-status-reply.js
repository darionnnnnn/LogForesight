/**
 * 回覆處理狀態 modal（共用元件）：欄位、必填規則與狀態值域只有這一份——
 * 值域與風險日詳情的問題層級狀態一致（core 的 IssueHandlingStatuses）。
 *
 * 分工：`openWorkOrderReplyModal` 只負責「填什麼、怎麼驗」；「回覆哪些對象、打哪支端點」
 * 由呼叫端以 `submit` 帶進來。處理人工作頁的兩條動線（整張單／單內選取主機）與
 * 問題查詢的「依問題」視角都走這顆 modal，兩邊各寫一份遲早會漂移成兩套必填規則。
 * 入口的顯示條件（「這個問題／這張單的處理人是自己」）由呼叫端判斷——後端另有同一條限制。
 */

import { api, getCurrentUser } from '../core/api.js';
import { setFieldError, clearFieldError } from '../core/field-errors.js';
import { toast, withBusy, showDetailModal, confirmAction, bindPastDateCheck, renderChips, button as makeButton } from '../core/ui.js';
import { copyText } from '../core/clipboard.js';
import { createHostPicker } from './reply-host-picker.js';
import { attachNoteEditor } from '../core/note-editor.js';
import { statusMeta, statusesWhere, isNoFixClosure, replyableCount, MAX_DUE_DATE_DAYS } from '../core/handling-statuses.js';
import { statusNote } from '../core/elements.js';
import { todayLocal, toLocalDateString, dateAfterDays } from '../core/format.js';

/**
 * 處理人可回覆的狀態：由 core/handling-statuses.js 的 replyable 衍生（不含「未處理」——
 * 要退回請由管理者處理，後端 IssueStatusValidation.ValidateReply 也會擋）。
 * escalated：「我處理不了，需要上報」——非結案，後端會即時通知 admin 群組決定結案或重新指派
 */
const STATUS_OPTIONS = statusesWhere(s => s.replyable).map(s => ({ value: s.value, label: s.label, shortLabel: s.shortLabel ?? s.label }));

/** 觀察中的快捷天數與預設天數（與風險日處理面板同一組：今天＋N 天） */
const OBSERVE_QUICK_DAYS = [3, 7, 14];
const OBSERVE_DEFAULT_DAYS = 7;

/** 復發建議「請管理員評估」預填的說明（使用者已輸入說明時不覆蓋） */
const RECURRENCE_ESCALATE_NOTE = '重複發生，請評估調整規則或靜音';

/**
 * 不處理類（不處理、誤報、已知雜訊）的後果說明：處理人單方面結案，之後不會再派給任何人。
 * 處理人回覆表單與風險日詳情的處理面板共用這一句，不各寫一份。
 * multiHosts＝一次涵蓋多台主機或多張單（「這些主機的這個問題」）；
 * multiIssues＝一次涵蓋同一台主機的多個問題（「這台主機的這些問題」）。
 * releases＝這些問題的「不再打擾」滿 30 天會自動解除（高嚴重度或重大，由伺服器的 dismissalReleases 布林決定，前端不判斷嚴重度）：
 * 改寫成「30 天內不再派工，之後仍在發生會重新派工」；多個問題只要有一個不適用（永久）就維持原句，不對永久的說成會重新派工。
 */
export function noFixConsequenceText({ multiHosts = false, multiIssues = false, releases = false } = {}) {
    const subject = multiHosts ? '這些主機的這個問題' : (multiIssues ? '這台主機的這些問題' : '這台主機的這個問題');
    if (releases) return `${subject}30 天內不再派工，之後仍在發生會重新派工。`;
    return `${subject}之後不會再派給任何人；需要恢復請找管理者。`;
}

/**
 * @param {object} options
 * @param {string} options.title modal 標題
 * @param {string} options.targetText 最上方的對象說明（呼叫端決定，例如「本單 96 台中的 5 台」）
 * @param {(payload: {status: string, note: string|null, dueDate: string|null}) => Promise<any>} options.submit
 *        呼叫端提供的送出函式；成功與否的訊息由呼叫端自己出。回覆請求要帶 `{ silent: true }`：
 *        被拒（400／403）的訊息由這個彈窗常駐顯示，其他 API 錯誤由這裡補 toast
 * @param {() => void} [options.onApplied] 送出成功後的重新載入
 * @param {(error: Error) => (Promise<string|void>|string|void)} [options.onRejected]
 *        被拒（400 單已結案／成員已非進行中、403 單已改派）時呼叫：呼叫端在這裡重新載入頁面資料，
 *        可回傳一段取代伺服器訊息的說明（例如補上新處理人）；草稿不清
 * @param {string} options.draftKey 草稿識別（必填）：可區分回覆對象的字串，下次打開同一批對象會還原草稿
 * @param {{issueLabel?: string, hostCount?: number}} [options.aiContext] 「AI 整理」的問題脈絡；
 *        一次回覆多張不同問題的單時不帶 issueLabel
 * @param {string|null} [options.reuseIssueKey] 「沿用此問題上次的說明」的完整問題簽章；
 *        沒有單一明確的簽章（例如一次回覆多張單）時傳 null，不顯示沿用連結
 * @param {(payload: {status: string, note: string|null, dueDate: string|null}) => void} [options.onNext]
 *        有值時在「送出」旁多一顆「送出並回覆下一張」：送出成功後關閉彈窗並以這次送出的內容呼叫它；
 *        送出失敗或必填沒過時停在原彈窗
 * @param {boolean} [options.dismissalReleases] 這次涵蓋的問題「不再打擾」滿 30 天會自動解除（伺服器的 dismissalReleases；多張單要全部都是才傳 true）：
 *        不處理類的後果說明改寫成「30 天內不再派工，之後仍在發生會重新派工」
 * @param {boolean} [options.multiTarget] 這次涵蓋多台主機或多張單：不處理類的後果說明改用複數說法
 * @param {boolean} [options.multiIssues] 這次涵蓋同一台主機的多個問題（主機詳情「這台的交辦」）：後果說明改指「這台主機的這些問題」
 * @param {string} [options.initialStatus] 結論按鈕的初始選取（「下一張」沿用上一張的結論）；不傳＝不預選，必須自己選
 * @param {string|null} [options.previousNote] 上一張的說明：有值時說明欄上方顯示「帶入上一張的說明」連結
 * @param {{load: (params: {page: number, pageSize: number, hostNames: string[]|null}) => Promise<object>}} [options.picker]
 *        有值時對象是「全部進行中主機」的彈窗多一個「只回覆部分主機」切換：就地面板列出進行中成員讓人勾選或貼上主機名；
 *        勾選後送出時 submit 的第二個參數是勾選的 caseId 陣列（沒切換時為 undefined，呼叫端用預設對象）
 * @param {boolean} [options.partialTarget] 呼叫端傳進來的對象本身就是部分主機（例如成員表勾選的幾台）
 * @param {{hosts: number}} [options.recurrence] 這次涵蓋的主機裡有幾台是反覆發生（復發）的問題：hosts > 0 時彈窗頂端提示，
 *        並在結論按鈕前列三個建議（還在處理、觀察 7 日、請管理員評估）；沒有或 0 台不顯示。建議只是預選結論並預填，不擋其他結論
 * @param {{remaining: (result: any) => Promise<number>, submitRest: (payload: object) => Promise<any>}} [options.followUp]
 *        部分主機以結案類結論回覆成功、單還有進行中主機時，彈窗不關閉，改問「其餘 N 台要一起設為…」：
 *        remaining 取剩下的進行中台數，submitRest 以 caseIds 為空（＝全部進行中，也就是剩下的）送出；
 *        第一次是非結案類（處理中、觀察中、上報）時不提示、直接關閉。需要 submit 回傳 API 回應
 */
export function openWorkOrderReplyModal({ title, targetText, draftKey, submit, onApplied, onRejected, aiContext = {}, reuseIssueKey = null, onNext, initialStatus, previousNote, multiTarget = false, picker: pickerOptions, partialTarget = false, followUp, recurrence, dismissalReleases = false, multiIssues = false }) {
    let modal = null;   // showDetailModal 回傳的控制代碼，送出成功後以 close() 關閉
    const body = document.createElement('div');
    const form = document.createElement('form');
    form.noValidate = true;   // 超過字數的 customValidity 走下方手動驗證，不跳原生泡泡

    const hint = document.createElement('div');
    hint.className = 'lf-hint mb-3';
    form.appendChild(hint);

    // 部分主機：切換成就地面板（不另開 modal）；rest＝部分回覆結案後問「其餘 N 台」
    let picking = false;
    let mode = 'whole';
    let restCount = 0;
    const picker = pickerOptions ? createHostPicker({ load: pickerOptions.load, onChange: () => { syncHint(); syncRecurrence(); syncSubmitState(); } }) : null;
    const pickToggle = makeButton('只回覆部分主機', { onClick: () => togglePicking() });
    pickToggle.id = 'wo-reply-pick-toggle';
    pickToggle.classList.add('mb-3', picker ? 'd-inline-block' : 'd-none');
    form.appendChild(pickToggle);
    if (picker) form.appendChild(picker.element);
    function syncHint() {
        const target = mode === 'rest'
            ? `其餘 ${restCount} 台進行中主機`
            : (picking ? `選取的 ${picker.selected.size} 台主機` : targetText);
        hint.textContent = `套用對象：${target}；每台主機的交辦會連同它涵蓋的日期一起更新。`;
    }
    syncHint();
    function togglePicking() {
        picking = !picking;
        picker.element.classList.toggle('d-none', !picking);
        pickToggle.textContent = picking ? '改為回覆全部進行中主機' : '只回覆部分主機';
        if (picking) picker.open();
        syncHint();
        syncRecurrence();
        syncSubmitState();
    }

    // 被拒訊息：常駐到使用者按「關閉訊息」為止（不是自動消失的 toast），草稿與表單內容保留
    const rejectBox = document.createElement('div');
    rejectBox.id = 'wo-reply-rejected';
    rejectBox.className = 'mb-3 d-none';
    form.appendChild(rejectBox);

    // 復發提示與三個建議（有復發主機才有）：放在結論按鈕之前，點了等於選對應結論並預填
    const recurrenceHosts = recurrence?.hosts > 0 ? recurrence.hosts : 0;
    const recurrenceText = count => `其中 ${count} 台是反覆發生的問題。結案後又出現，建議先看原因：`;
    const recurrenceHint = recurrenceHosts > 0 ? statusNote('info', recurrenceText(recurrenceHosts)) : null;
    const suggestRow = recurrenceHosts > 0 ? document.createElement('div') : null;
    if (recurrenceHint) {
        recurrenceHint.id = 'wo-reply-recurrence';
        recurrenceHint.classList.add('mb-2');
        suggestRow.id = 'wo-reply-suggest';
        suggestRow.className = 'd-flex flex-wrap gap-2 mb-3';
        form.append(recurrenceHint, suggestRow);
    }

    /**
     * 提示與建議跟著「回覆對象」重算：整單沿用呼叫端給的台數；切到部分主機後是勾選成員中 isRecurrence 的不重複主機數
     * （同一台的多個簽章只算一台）；「其餘 N 台」續問不適用（復發台數是原對象的）。0 台時提示與三個建議都收起。
     */
    function syncRecurrence() {
        if (!recurrenceHint) return;
        let count = recurrenceHosts;
        if (mode === 'rest') count = 0;
        else if (picking) {
            const hosts = new Set();
            for (const item of picker.selected.values()) {
                if (item?.isRecurrence) hosts.add(String(item.hostName ?? item.hostId ?? item.caseId).toUpperCase());
            }
            count = hosts.size;
        }
        if (count > 0) recurrenceHint.textContent = recurrenceText(count);
        recurrenceHint.classList.toggle('d-none', count === 0);
        suggestRow.classList.toggle('d-none', count === 0);
    }

    // 結論：一排單選按鈕（與風險日處理面板同一個 renderChips 與 .lf-handling__chips 樣式），預設不選
    const statusLabel = document.createElement('div');
    statusLabel.className = 'form-label lf-text-label';
    statusLabel.id = 'wo-reply-status-label';
    statusLabel.textContent = '這次的結論';
    const statusGroup = document.createElement('div');
    statusGroup.id = 'wo-reply-status';
    statusGroup.className = 'lf-handling__chips mb-3';
    statusGroup.setAttribute('role', 'group');
    statusGroup.setAttribute('aria-labelledby', 'wo-reply-status-label');
    let selectedStatus = initialStatus && STATUS_OPTIONS.some(option => option.value === initialStatus) ? initialStatus : null;
    // 選到不處理類時說明後果：這是處理人單方面的決定，之後不會再派給任何人（管理者代為結案才有「是否停派」可選）
    const noFixNote = statusNote('warning', noFixConsequenceText({ multiHosts: multiTarget, multiIssues, releases: dismissalReleases }));
    noFixNote.classList.add('mb-3');
    noFixNote.classList.add('d-none');
    noFixNote.id = 'wo-reply-no-fix-note';
    form.append(statusLabel, statusGroup, noFixNote);

    const noteHead = document.createElement('div');
    noteHead.className = 'd-flex align-items-end justify-content-between gap-2';
    const noteLabel = document.createElement('label');
    noteLabel.className = 'form-label lf-text-label';
    noteLabel.textContent = '處理說明';
    noteLabel.htmlFor = 'wo-reply-note';
    // 單被改派（403）後說明留在草稿：給一顆複製鈕，貼給新處理人或另開單用
    const copyNoteBtn = makeButton('複製說明', { onClick: copyNote });
    copyNoteBtn.classList.add('d-none', 'mb-1');
    copyNoteBtn.id = 'wo-reply-copy-note';
    noteHead.append(noteLabel, copyNoteBtn);
    const noteInput = document.createElement('textarea');
    noteInput.id = 'wo-reply-note';
    noteInput.className = 'form-control form-control-sm';
    noteInput.rows = 3;
    form.append(noteHead);
    // 下一張：說明預設清空，上一張的說明改成一鍵帶入（不同單的說明不一定能照抄）
    let previousNoteLink = null;
    if (previousNote) {
        previousNoteLink = makeButton('帶入上一張的說明');
        previousNoteLink.classList.add('mb-1', 'd-block');
        form.appendChild(previousNoteLink);
    }
    form.appendChild(noteInput);
    // 必填說明欄的即時錯誤（狀態切換時一併更新標示）；aria-live 讓讀屏軟體念出錯誤。
    // 交給編輯器放在尾區之後（[說明欄, 尾區, 錯誤訊息]）
    const noteError = document.createElement('div');
    noteError.className = 'invalid-feedback d-block mb-3';
    noteError.setAttribute('aria-live', 'polite');
    const noteEditor = attachNoteEditor(noteInput, {
        draftKey: `wo-reply:${draftKey}`,
        ai: { context: () => aiContext },
        reuse: { issueKey: () => reuseIssueKey },
        phrases: true,
        errorEl: noteError
    });
    noteInput.addEventListener('input', () => {
        if (noteInput.value.trim()) clearFieldError(noteInput);
    });
    function syncNoteField() {
        const meta = statusMeta(selectedStatus);
        noteLabel.textContent = meta ? meta.noteLabel : '處理說明';
        clearFieldError(noteInput);
    }
    if (previousNoteLink) {
        previousNoteLink.addEventListener('click', () => noteEditor.setValue(previousNote));
    }

    // 處理中的預計完成日／觀察中的觀察至日期共用同一個欄位（後端同一個 DueDate）
    const dueLabel = document.createElement('label');
    dueLabel.className = 'form-label lf-text-label';
    dueLabel.textContent = '預計完成日／觀察至';
    dueLabel.htmlFor = 'wo-reply-due';
    const dueInput = document.createElement('input');
    dueInput.id = 'wo-reply-due';
    dueInput.type = 'date';
    dueInput.min = todayLocal();   // 不可早於今天（本機日期；bindPastDateCheck 仍負責手打的情況）
    const dueMaxDate = new Date();
    dueMaxDate.setDate(dueMaxDate.getDate() + MAX_DUE_DATE_DAYS);
    const dueMax = toLocalDateString(dueMaxDate);
    dueInput.max = dueMax;   // 後端最多只收今天起 90 天內；手打超過的情況在送出前擋下
    dueInput.className = 'form-control form-control-sm mb-3';
    const dueError = document.createElement('div');
    dueError.className = 'invalid-feedback d-block';
    dueError.setAttribute('aria-live', 'polite');
    // 觀察中的快捷：3／7／14 日（今天＋N 天，與風險日處理面板一致）
    const quickRow = document.createElement('div');
    quickRow.className = 'd-flex gap-2 mb-2';
    quickRow.id = 'wo-reply-due-quick';
    for (const days of OBSERVE_QUICK_DAYS) {
        quickRow.appendChild(makeButton(`${days} 日`, {
            onClick: () => {
                dueInput.value = dateAfterDays(days);
                dueAutofilled = false;
                dueCheck.validate();
            }
        }));
    }
    form.append(dueLabel, quickRow, dueInput, dueError);

    const needsDue = () => selectedStatus === 'in_progress' || selectedStatus === 'observing';
    // 預計完成日不可早於今天：change／blur 即時檢查，送出前再檢查一次（後端另有同一條防線）
    const dueCheck = bindPastDateCheck(dueInput, dueError, { message: '預計完成日不可早於今天', isActive: needsDue });
    // 選到觀察中時自動填入預設 7 日；使用者沒動過就換到別的結論時收回，不把觀察日期帶進「處理中」
    let dueAutofilled = false;
    dueInput.addEventListener('input', () => { dueAutofilled = false; });
    function syncDueVisibility() {
        dueLabel.classList.toggle('d-none', !needsDue());
        dueInput.classList.toggle('d-none', !needsDue());
        quickRow.classList.toggle('d-none', selectedStatus !== 'observing');
        if (selectedStatus === 'observing' && !dueInput.value) {
            dueInput.value = dateAfterDays(OBSERVE_DEFAULT_DAYS);
            dueAutofilled = true;
        } else if (selectedStatus !== 'observing' && dueAutofilled) {
            dueInput.value = '';
            dueAutofilled = false;
        }
        dueCheck.validate();
    }

    // 沒選結論不能送出：按鈕停用並說明原因；處理中不結案，按鈕文字直說
    const statusRequiredHint = document.createElement('div');
    statusRequiredHint.className = 'lf-hint mb-2';
    statusRequiredHint.id = 'wo-reply-status-required';
    statusRequiredHint.setAttribute('aria-live', 'polite');
    statusRequiredHint.textContent = '請選擇這次的結論';
    form.appendChild(statusRequiredHint);

    const submitBtn = makeButton('送出', { variant: 'primary', type: 'submit' });
    form.appendChild(submitBtn);

    let nextBtn = null;
    if (onNext) {
        nextBtn = makeButton('送出並回覆下一張', { variant: 'outline-secondary' });
        nextBtn.classList.add('ms-2');
        form.appendChild(nextBtn);
        nextBtn.addEventListener('click', () => send(nextBtn, true));
    }

    function syncSubmitState() {
        const noStatus = !selectedStatus;
        const noHosts = !noStatus && picking && picker.selected.size === 0;
        const missing = noStatus || noHosts;
        submitBtn.disabled = missing;
        if (nextBtn) nextBtn.disabled = missing;
        statusRequiredHint.textContent = noHosts ? '請勾選要回覆的主機' : '請選擇這次的結論';
        statusRequiredHint.classList.toggle('d-none', !missing);
        submitBtn.textContent = selectedStatus === 'in_progress' ? '更新說明（不結案）' : '送出';
    }

    /** 結論變動（點選、方向鍵、初始值）後同步所有跟著結論走的欄位與按鈕 */
    function onStatusChanged() {
        noFixNote.classList.toggle('d-none', !isNoFixClosure(selectedStatus));
        syncNoteField();
        syncDueVisibility();
        syncSubmitState();
    }

    function renderStatusChips() {
        renderChips(statusGroup, {
            items: STATUS_OPTIONS.map(option => ({ value: option.value, label: option.shortLabel, title: option.label, ariaLabel: option.label })),
            attr: 'status',
            activeValues: [selectedStatus],
            multi: false,
            onToggle: value => {
                selectedStatus = value;
                onStatusChanged();
            }
        });
    }
    renderStatusChips();
    onStatusChanged();

    /** 以程式選定結論（復發建議用）：與點結論按鈕同一條同步路徑 */
    function chooseStatus(value) {
        selectedStatus = value;
        renderStatusChips();
        onStatusChanged();
    }
    if (suggestRow) {
        const addSuggestion = (id, label, onPick) => {
            const btn = makeButton(label, { onClick: onPick });
            btn.id = id;
            suggestRow.appendChild(btn);
        };
        addSuggestion('wo-reply-suggest-progress', '還在處理（設預計完成日）', () => {
            chooseStatus('in_progress');
            dueInput.focus();
        });
        addSuggestion('wo-reply-suggest-observe', `觀察 ${OBSERVE_DEFAULT_DAYS} 日`, () => {
            chooseStatus('observing');
            dueInput.value = dateAfterDays(OBSERVE_DEFAULT_DAYS);
            dueAutofilled = false;
            dueCheck.validate();
        });
        // 「上報管理者」是既有結論（目錄裡的 escalated，必填說明）：預填一句，已經寫了說明就不覆蓋
        addSuggestion('wo-reply-suggest-escalate', '請管理員評估調整規則或靜音', () => {
            chooseStatus('escalated');
            if (!noteInput.value.trim()) noteEditor.setValue(RECURRENCE_ESCALATE_NOTE);
        });
    }

    // 方向鍵在結論按鈕群組內切換選取（左／上＝前一個、右／下＝後一個，頭尾相接）
    statusGroup.addEventListener('keydown', event => {
        const step = { ArrowRight: 1, ArrowDown: 1, ArrowLeft: -1, ArrowUp: -1 }[event.key];
        if (!step) return;
        event.preventDefault();
        const chips = Array.from(statusGroup.querySelectorAll('.lf-chip'));
        if (chips.length === 0) return;
        const current = chips.findIndex(chip => chip.classList.contains('active'));
        const from = current >= 0 ? current : (step > 0 ? -1 : 0);
        const target = chips[(from + step + chips.length) % chips.length];
        target.click();
        target.focus();
    });

    // Ctrl／⌘＋Enter 在說明欄內送出（輸入法組字中的 Enter 不算）；沒選結論時與按鈕一樣不動作
    noteInput.addEventListener('keydown', event => {
        if (event.key !== 'Enter' || !(event.ctrlKey || event.metaKey) || event.isComposing) return;
        event.preventDefault();
        if (submitBtn.disabled) return;
        send(submitBtn, false);
    });

    form.addEventListener('submit', event => {
        event.preventDefault();
        if (submitBtn.disabled) return;
        send(submitBtn, false);
    });

    /** 顯示常駐的被拒訊息；withCopy＝403（單已改派）時同時提供「複製說明」 */
    function showRejection(message, withCopy) {
        const dismiss = makeButton('關閉訊息', {
            onClick: () => {
                rejectBox.classList.add('d-none');
                rejectBox.replaceChildren();
            }
        });
        dismiss.classList.add('mt-2');
        rejectBox.replaceChildren(statusNote('error', message), dismiss);
        rejectBox.classList.remove('d-none');
        copyNoteBtn.classList.toggle('d-none', !withCopy);
    }

    /** 複製說明欄：navigator.clipboard 不可用或被拒時，改選取文字讓使用者自己複製 */
    async function copyNote() {
        const result = await copyText(noteInput.value);
        if (result.ok) {
            toast('已複製說明', 'success');
            return;
        }
        noteInput.focus();
        noteInput.select?.();
        toast(result.reason || '無法自動複製，已選取文字，請自行複製', 'warning');
    }

    /** 送出失敗：400／403 是「被拒」（單已結案、成員已非進行中、單已改派），常駐顯示並通知呼叫端重新載入；其餘 API 錯誤補 toast */
    async function handleFailure(error) {
        const status = error?.status;
        if (status === 400 || status === 403) {
            const withCopy = status === 403;
            showRejection(error.message || '這次回覆被拒絕', withCopy);
            let better;
            try { better = await onRejected?.(error); } catch { better = undefined; }
            if (typeof better === 'string' && better) showRejection(better, withCopy);
            return;
        }
        // 有數字狀態碼的是 api.js 的錯誤（回覆請求靜音，訊息在這裡出）；沒有的（例如部分失敗的 partial）呼叫端已自己出過訊息
        if (typeof status === 'number') toast(error.message || '送出失敗，請再試一次', 'danger');
    }

    /** 部分回覆結案後的第二步：同一個彈窗改問「其餘 N 台要一起設為…」，結論重選、說明清空，以 caseIds 為空送出 */
    function enterRestMode(count) {
        mode = 'rest';
        restCount = count;
        picking = false;
        picker?.element.classList.add('d-none');
        pickToggle.classList.add('d-none');
        pickToggle.classList.remove('d-inline-block');
        rejectBox.classList.add('d-none');
        rejectBox.replaceChildren();
        // 復發主機數是原對象的數字，不適用於「其餘」：mode 先設成 rest，syncRecurrence 會把提示與建議收起
        syncRecurrence();
        statusLabel.textContent = `其餘 ${count} 台要一起設為…`;
        selectedStatus = null;
        renderStatusChips();
        noteEditor.setValue('');
        noteEditor.clearDraft();
        syncHint();
        onStatusChanged();
    }

    /** 驗證→送出→關閉；goNext＝成功後接著開下一張（失敗或必填沒過都停在原彈窗） */
    async function send(button, goNext) {
        if (!selectedStatus) return;
        // 必填說明由目錄決定（不處理、無法處理）：管理員收到上報通知要據此決定結案或改派。
        // 送出前把所有錯誤一次標出來，focus 到第一個錯誤欄位
        const meta = statusMeta(selectedStatus);
        const errors = [];
        if (meta?.noteRequired && !noteInput.value.trim()) {
            const reasonText = meta.noteLabel.replace(/（.*）$/, '');
            setFieldError(noteInput, `標記為「${meta.shortLabel}」時請填寫${reasonText}`, { feedback: noteError });
            errors.push(noteInput);
        } else if (!noteInput.checkValidity()) {
            setFieldError(noteInput, noteInput.validationMessage, { feedback: noteError });   // 超過字數上限（attachNoteEditor 設的）
            errors.push(noteInput);
        }
        if (!dueCheck.validate()) errors.push(dueInput);
        else if (needsDue() && dueInput.value > dueMax) {
            setFieldError(dueInput, `預計完成日最多只能設在 ${MAX_DUE_DATE_DAYS} 天內`, { feedback: dueError });
            errors.push(dueInput);
        }
        if (selectedStatus === 'observing' && !dueInput.value) {
            setFieldError(dueInput, '標記為「觀察中」時請指定觀察至日期', { feedback: dueError });
            errors.push(dueInput);
        }
        if (errors.length > 0) {
            errors[0].focus();
            return;
        }

        const payload = {
            status: selectedStatus,
            note: noteInput.value.trim() || null,
            dueDate: dueInput.value || null
        };
        const restore = withBusy(button, '送出中');
        submitBtn.disabled = true;
        if (nextBtn) nextBtn.disabled = true;
        // 部分主機回覆（勾選或呼叫端指定）且結論是結案類：成功後單若還有進行中主機，改問「其餘 N 台」
        const partial = mode === 'whole' && (picking || partialTarget);
        let result;
        try {
            result = mode === 'rest'
                ? await followUp.submitRest(payload)
                : await submit(payload, picking ? picker.selectedIds() : undefined);
        } catch (error) {
            restore();
            syncSubmitState();
            await handleFailure(error);
            return;
        }

        noteEditor.clearDraft();
        if (partial && !goNext && followUp && statusMeta(payload.status)?.closed && !result?.workOrderClosed) {
            let remaining = 0;
            try { remaining = await followUp.remaining(result); } catch { remaining = 0; }
            if (remaining > 0) {
                restore();
                onApplied?.();   // 剛回覆的那幾台先更新到畫面；彈窗留著等「其餘」的決定
                enterRestMode(remaining);
                return;
            }
        }
        closeConfirmed = true;
        // 下一張等這個彈窗完全關閉才開：兩個 modal 的淡出／淡入交疊時，前一個收尾會拿掉頁面的捲動鎖定
        if (goNext) modalEl.addEventListener('hidden.bs.modal', () => onNext(payload), { once: true });
        modal?.close();
        onApplied?.();
    }

    body.appendChild(form);
    modal = showDetailModal({ title, body });

    // 關閉保護（DESIGN-SYSTEM §6b）：Esc／點遮罩／關閉鈕都走 hide.bs.modal，說明欄有字時先問；草稿一律保留
    let closeConfirmed = false;
    const modalEl = modal.element;
    modalEl?.addEventListener('hide.bs.modal', async event => {
        if (closeConfirmed || !noteInput.value.trim()) return;
        event.preventDefault();
        const confirmed = await confirmAction({
            title: '放棄未送出的處理說明？',
            message: '草稿已自動保留，下次打開同一張單會還原。',
            confirmText: '關閉'
        });
        if (!confirmed) return;
        closeConfirmed = true;
        modal.close();
    });
}

/**
 * 交辦單成員端點的網址（交辦單頁、我的交辦展開列、回覆彈窗的主機面板共用）。
 * hostNames＝主機名篩選（完全相符、不分大小寫），以重複的 hostName 參數送出；null／空陣列＝不篩選。
 */
export function orderMembersUrl(workOrderId, { status = 'all', page = 1, pageSize = 50, hostNames = null } = {}) {
    const params = new URLSearchParams({ status, page: String(page), pageSize: String(pageSize) });
    for (const name of hostNames ?? []) params.append('hostName', name);
    return `/api/work-orders/${workOrderId}/members?${params}`;
}

/** 這張單目前還有幾台進行中主機（含停用主機上的）：部分回覆結案後決定要不要問「其餘 N 台」 */
export async function remainingActiveCount(workOrderId) {
    const detail = await api.get(`/api/work-orders/${workOrderId}`, { silent: true });
    return replyableCount(detail?.counts);
}

/**
 * 回覆被拒後的說明：403＝這張單已改派，重新讀單取得新處理人名稱（讀不到——例如原處理人已無權檢視——就不指名）；
 * 其餘狀態碼回 undefined，沿用伺服器訊息。給單張交辦單的呼叫端用（reply-many 涵蓋多張單，不指名）。
 */
export async function describeReplyRejection(error, workOrderId) {
    if (error?.status !== 403) return undefined;
    let handlerName = null;
    try {
        const detail = await api.get(`/api/work-orders/${workOrderId}`, { silent: true });
        handlerName = detail?.handlerName || null;
    } catch {
        // 讀單失敗：退回不指名的說法
    }
    return `這張單剛改派給 ${handlerName || '其他處理人'}，你的說明仍保留在草稿`;
}

/** 回覆多張交辦單的草稿識別：單號排序後以逗號連接，同一批單不論勾選順序都對到同一份草稿 */
export function workOrdersDraftKey(workOrderIds) {
    return `orders:${[...workOrderIds].sort((a, b) => a - b).join(',')}`;
}

/** 觀察至日期顯示成月-日（「12-01」）；後端與表單都是 YYYY-MM-DD */
function monthDay(dueDate) {
    return dueDate ? String(dueDate).slice(5) : '';
}

/**
 * 回覆成功訊息的「結論」部分——三個呼叫端（我的交辦、交辦單頁、依問題視角）共用這一支，
 * 不各寫一份：結案類寫出結論名稱、處理中說明「仍為處理中」、觀察中帶到期日、上報說明已通知管理者。
 * scope＝涵蓋範圍文字（「20 台」「3 張單共 20 台」）；沒有 payload（取不到結論）時退回最簡說法。
 */
export function replyOutcomeText(payload, scope, notifiedAdmins) {
    const meta = statusMeta(payload?.status);
    if (!meta) return `已回覆 ${scope}`;
    if (meta.closed) return `已回覆 ${scope}為${meta.shortLabel}`;
    if (meta.value === 'observing') {
        const until = monthDay(payload.dueDate);
        return `${scope}改為觀察中${until ? `，至 ${until}` : ''}`;
    }
    if (meta.value === 'escalated') return `${scope}已上報管理者${escalationNotifyText(notifiedAdmins)}`;
    return `已更新 ${scope}的說明（仍為${meta.shortLabel}）`;
}

/**
 * 上報後的通知交代：後端帶「會收到通知的管理員數」。N＞0 寫明人數；0 寫明沒有可通知的人、已列入交辦總覽提醒；
 * 沒帶（null／undefined，例如這些成員原本就已上報而不重複通知）不多說。
 */
function escalationNotifyText(notifiedAdmins) {
    if (!Number.isInteger(notifiedAdmins) || notifiedAdmins < 0) return '';
    return notifiedAdmins > 0
        ? `，已通知 ${notifiedAdmins} 位管理員`
        : '，目前沒有可通知的管理員，已列入交辦總覽提醒';
}

/** 單張交辦單回覆的成功訊息（POST /api/work-orders/{id}/reply 的回應）；payload＝這次送出的 { status, dueDate } */
export function toastReplyResult(result, payload) {
    const closed = result.workOrderClosed ? '，整張單已結案' : '';
    toast(`${replyOutcomeText(payload, `${result.cases} 台`, result.escalationNotifiedAdmins)}${closed}`, 'success');
    if (result.daySyncPendingCases > 0) {
        toast('逐日同步在背景進行', 'info');
    }
}

/**
 * 多張交辦單回覆的結果訊息（POST /api/work-orders/reply-many 的回應）。
 * 寫入中途某張失敗時後端停在那張、回 200：成功的已寫入，改顯示警告交代失敗與未處理。
 */
export function toastReplyManyResult(result, payload) {
    if (result.failedWorkOrderId != null) {
        const notProcessed = result.notProcessed?.length ?? 0;
        toast(`已完成 ${result.succeeded?.length ?? 0} 張；#${result.failedWorkOrderId} 失敗：${result.failureMessage || '未知原因'}；`
            + `尚有 ${notProcessed} 張未處理，已保留勾選可直接重送。`, 'warning');
    } else {
        const closed = result.closedWorkOrders > 0 ? `，其中 ${result.closedWorkOrders} 張結案` : '';
        toast(`${replyOutcomeText(payload, `${result.workOrders} 張單共 ${result.cases} 台`, result.escalationNotifiedAdmins)}${closed}`, 'success');
    }
    if (result.daySyncPendingCases > 0) {
        toast('逐日同步在背景進行', 'info');
    }
}

/**
 * 依問題視角的入口：把「這個問題指派給我的進行中交辦單」全部帶進 reply-many。
 * @param {{source: string, eventId: number}} group 問題（Source＋EventId）
 * @param {() => void} onApplied 套用成功後的重新載入
 */
export async function openIssueStatusReplyModal(group, onApplied) {
    const params = new URLSearchParams({ status: 'active', page: '1', pageSize: '100' });
    if (group.source) params.set('source', group.source);
    if (group.eventId !== null && group.eventId !== undefined) params.set('eventId', String(group.eventId));

    // 呼叫端是 click 監聽（沒有人接這個 Promise），取單失敗要在這裡收掉——
    // api.js 已經出過錯誤 toast，往外擲只會變成 unhandled rejection
    let list;
    try {
        const user = await getCurrentUser();
        list = await api.get(`/api/handlers/${user.userId}/work-orders?${params}`);
    } catch {
        return;
    }

    const orders = list?.items ?? [];
    if (orders.length === 0) {
        toast('這個問題目前沒有指派給您的交辦單', 'info');
        return;
    }

    const hosts = orders.reduce((sum, order) => sum + replyableCount(order.counts), 0);
    const workOrderIds = orders.map(order => order.workOrderId);

    openWorkOrderReplyModal({
        multiTarget: orders.length > 1 || hosts > 1,
        title: `回覆處理狀態：${group.issueLabel}`,
        targetText: `${orders.length} 張單共 ${hosts} 台`,
        draftKey: workOrdersDraftKey(workOrderIds),
        aiContext: { issueLabel: group.issueLabel, hostCount: hosts },
        recurrence: { hosts: orders.reduce((sum, order) => sum + (order.recurrenceHosts ?? 0), 0) },
        dismissalReleases: orders.every(order => order.dismissalReleases),
        // 依問題視角只有 Source＋EventId、沒有完整簽章（同一事件可能分屬多個簽章），不顯示沿用
        reuseIssueKey: null,
        submit: async payload => {
            const result = await api.post('/api/work-orders/reply-many', { workOrderIds, ...payload }, { silent: true });
            toastReplyManyResult(result, payload);
        },
        // 被拒（單已結案、成員已非進行中、單已改派）：重新載入問題清單，訊息與草稿由彈窗保留
        onRejected: () => onApplied?.(),
        onApplied
    });
}
