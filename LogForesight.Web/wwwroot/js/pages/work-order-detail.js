﻿/**
 * 交辦單詳情頁（回饋第 47 輪）：
 * 包含單號標頭、成員清單、處理歷程時間軸與管理動作（改派、取消交辦、代為結案、拆出主機、複製 CSV）。
 */

import { api, getCurrentUser, hasCapability, canViewHandlerPage, getDisplaySettings } from '../core/api.js';
import { statusesWhere, statusBadgeMeta, isNoFixClosure, replyableCount } from '../core/handling-statuses.js';
import { mailNotifyText } from '../core/mail-notify.js';
import { exclusive, createSeqGuard } from '../core/async-guards.js';
import { appUrl } from '../core/paths.js';
import {
    renderTable,
    renderPagination,
    renderLoading,
    renderEmpty,
    renderError,
    guardLoad,
    toast,
    confirmActionWithReason,
    confirmAction,
    showDetailModal,
    withBusy,
    searchableUserSelect,
    button,
    linkButton,
    icon,
    candidatesToUserOptions,
    createPageSelection
} from '../core/ui.js';
import { metaLine, roleText, disclosure, fact, statusNote } from '../core/elements.js';
import { formatDate, formatDateTime, statusBadge, workOrderClosedReasonText, originBadge, hostCountText } from '../core/format.js';
import { openWorkOrderReplyModal, toastReplyResult, describeReplyRejection, orderMembersUrl, remainingActiveCount } from './issue-status-reply.js';
import { createListUpdateNotice } from '../core/list-update-notice.js';
import { copyText } from '../core/clipboard.js';
import { openIssueMuteModal } from './issue-mute-modal.js';
import { openHideTriage } from './hide-triage-panel.js';
import { guidancePanel, sampleMessagesPanel, issueShortcuts, adminCloseScopeOptions, defaultAdminCloseScope } from './work-order-reference.js';

// DOM 元素
const rootEl = document.getElementById('wo-detail');
const titleEl = document.getElementById('wo-title');
const badgesEl = document.getElementById('wo-badges');
const actionsContainer = document.getElementById('wo-actions');
const headerContainer = document.getElementById('wo-header');

const memberStatusSelect = document.getElementById('wo-member-status');
const memberCsvBtn = document.getElementById('wo-member-csv');
const memberSplitBtn = document.getElementById('wo-member-split');
const memberReplyBtn = document.getElementById('wo-member-reply');
const memberNoteEl = document.getElementById('wo-member-note');
const membersContainer = document.getElementById('wo-members');
const memberPagerContainer = document.getElementById('wo-member-pager');
const memberSearchInput = document.getElementById('wo-member-search');

const timelineContainer = document.getElementById('wo-timeline');

const workOrderId = Number(rootEl?.dataset.workOrderId);
const PAGE_SIZE = 50;

let currentUser = null;
let currentDetail = null;
let memberPage = 1;
/** 目前已載入的這一頁成員（勾選全選與空狀態判斷看它） */
let currentMemberItems = [];
/** 成員表的勾選（caseId → 成員列）：與回覆彈窗的主機面板同一個共用元件，換頁、搜尋都保留，改狀態篩選才清除 */
const selection = createPageSelection({
    keyOf: item => item.caseId,
    isSelectable: item => !item.closedAt,
    getPageItems: () => currentMemberItems,
    labels: { row: '選取這台主機', all: '全選本頁可處理主機' },
    bar: { barId: 'wo-member-selected', countId: 'wo-member-selected', unit: '台' },
    onSelectAll: () => { renderMemberRows(); },
    onChange: () => { updateSplitBtn(); updateReplyBtn(); }
});
const selectedCaseIds = selection.selected;
/** 成員表的主機名篩選（完全相符）；空字串＝不篩選 */
let memberHostName = '';
/** 目前已載入的成員：caseId → hostId（改派／拆出算人選看不看得到主機用；只含已載入的分頁） */
const loadedMemberHosts = new Map();

// ── 成員狀態文字與徽章顏色（同一件事只寫一份）─────────────────────────────

function renderMemberStatusBadge(status) {
    const meta = statusBadgeMeta(status) || { label: status || '未知', variant: 'neutral' };
    return statusBadge(meta.label, meta.variant);
}

/** 上次回覆說明在成員表最多顯示幾個字；全文放 title */
const LAST_REPLY_NOTE_MAX = 24;
/** 一鍵結案寫進回覆的說明 */
const QUIET_CLOSE_NOTE = '觀察期滿未再發生';

/** 新案件的「先前判定不處理」：上一件不處理類結案、期滿 30 天仍在發生才重新派工（判定在伺服器，這裡只組文字） */
function priorDismissalText(prior) {
    const date = String(prior.closedAt || '').slice(5, 10);
    const who = prior.account ? `，${prior.account}` : '';
    return `先前判定不處理（${date}${who}），已滿 30 天仍在發生`;
}

/** 「MM-DD 狀態：說明」（說明過長截斷，全文由呼叫端放 title）；日期取自結案時間的月-日 */
function lastReplyParts(lastReply) {
    const meta = statusBadgeMeta(lastReply.status);
    const head = `${String(lastReply.date || '').slice(5, 10)} ${meta ? meta.label : lastReply.status || ''}`.trim();
    const note = lastReply.note ? String(lastReply.note) : '';
    const shown = note.length > LAST_REPLY_NOTE_MAX ? `${note.slice(0, LAST_REPLY_NOTE_MAX)}…` : note;
    return { text: note ? `${head}：${shown}` : head, full: note ? `${head}：${note}` : '' };
}

/** 觀察中成員的「觀察期間再發生」文字：null＝觀察期間沒有任何分析紀錄（無資料，不等於沒有再發生） */
function observedRecurText(days) {
    if (days == null) return '無資料';
    return days === 0 ? '沒有再發生' : `觀察期間再發生 ${days} 天`;
}

/**
 * 成員表的狀態格：狀態徽章，復發的成員附「復發 N 次」與上一件的回覆（日期、狀態、說明；說明一律 textContent，
 * 過長截斷並以 title 呈現全文），觀察中的成員附觀察期間再發生的天數。附註都是判斷層文字，沒有就只有徽章。
 */
function memberStatusCell(item) {
    const wrap = document.createElement('div');
    wrap.className = 'd-flex flex-column align-items-start gap-1';
    wrap.appendChild(renderMemberStatusBadge(item.status));
    if (item.isRecurrence) {
        wrap.appendChild(statusBadge(`復發 ${item.recurrenceCount ?? 1} 次`, 'warning', { title: '同一台主機的同一個問題，近 30 天內已處理結案後又再出現' }));
        if (item.lastReply) {
            const parts = lastReplyParts(item.lastReply);
            const line = roleText('lf-text-meta', parts.text);
            line.classList.add('text-wrap');
            if (parts.full) line.title = parts.full;
            wrap.appendChild(line);
        }
    }
    if (item.priorDismissal) {
        const line = roleText('lf-text-meta', priorDismissalText(item.priorDismissal));
        line.classList.add('text-wrap');
        wrap.appendChild(line);
    }
    if (item.status === 'observing' && !item.closedAt) {
        wrap.appendChild(roleText('lf-text-meta', observedRecurText(item.observedRecurDays)));
    }
    return wrap;
}

// ── 處理人選單 modal（改派與拆出共用，同一件事只寫一份）───────────────────

/** 派工人選（共用端點）：主機為該操作涉及的主機，問題取自這張單；回傳可直接餵 searchableUserSelect 的選項 */
async function fetchCandidates(hostIds) {
    const candidates = await api.post('/api/work-orders/candidates', {
        hostIds,
        source: currentDetail?.source ?? null,
        eventId: currentDetail?.eventId ?? null
    }, { silent: true });
    return candidatesToUserOptions(candidates);
}

async function openSelectHandlerModal({ title, submitText, currentHandlerId, onSubmit, showNotifyNow = false, hostIds = [] }) {
    let modal = null;   // showDetailModal 回傳的控制代碼，送出成功後以 close() 關閉
    const users = await fetchCandidates(hostIds);
    // 停用帳號後端一定拒絕，不擺一個必定失敗的選項
    const filteredUsers = (users || []).filter(u => u.userId !== currentHandlerId && u.active);

    const body = document.createElement('div');
    const form = document.createElement('form');

    const label = document.createElement('label');
    label.className = 'form-label';
    label.textContent = '選擇新處理人';

    const { element: selectWrap, getValue } = searchableUserSelect(filteredUsers, {
        selectedId: null,
        includeNone: true,
        noneLabel: '請選擇處理人…'
    });

    const submitBtn = button(submitText || '確定', { variant: 'primary', size: null, type: 'submit' });
    submitBtn.classList.add('mt-3');

    // 立即通知（預設不勾：通知照交辦摘要時刻寄出）；只有會寄通知的操作（改派）才顯示
    const notifyNowCheck = document.createElement('input');
    notifyNowCheck.type = 'checkbox';
    notifyNowCheck.className = 'form-check-input mt-0';
    const notifyNowLabel = document.createElement('label');
    notifyNowLabel.className = 'form-check-label d-flex align-items-center gap-1 mt-3';
    notifyNowLabel.append(notifyNowCheck, document.createTextNode('立即通知處理人（不等摘要時刻）'));

    form.append(label, selectWrap);
    if (showNotifyNow) form.appendChild(notifyNowLabel);
    form.appendChild(submitBtn);

    form.addEventListener('submit', async event => {
        event.preventDefault();
        const selectedVal = getValue();
        if (!selectedVal) {
            toast('請選擇處理人', 'warning');
            return;
        }
        const handlerId = Number(selectedVal);
        const restore = withBusy(submitBtn, '處理中…');
        try {
            const closeModal = () => modal?.close();
            await onSubmit(handlerId, closeModal, { notifyNow: showNotifyNow && notifyNowCheck.checked });
        } catch {
            restore();
        }
    });

    body.appendChild(form);
    modal = showDetailModal({ title, body });
}

// ── 標頭渲染 ───────────────────────────────────────────────────────────────

/** 狀態徽章（已結案／暫停／恢復／進行中）、來源與自動續掛：身分資訊，放在標題同一列 */
function renderBadges(detail) {
    const badges = [];
    if (detail.closedAt) {
        badges.push(statusBadge('已結案', 'neutral', { title: workOrderClosedReasonText(detail.closedReason) }));
    } else {
        if (detail.paused) {
            badges.push(statusBadge(`暫停（靜音至 ${detail.mutedUntil || ''}）`, 'warning'));
        }
        if (detail.resumedFromMuteAt) {
            badges.push(statusBadge(`${detail.resumedFromMuteAt} 自靜音恢復`, 'info'));
        }
        if (!detail.paused && !detail.resumedFromMuteAt) {
            badges.push(statusBadge('進行中', 'primary'));
        }
    }
    badges.push(originBadge(detail.origin));
    if (detail.autoAttach) {
        badges.push(statusBadge('自動續掛', 'info'));
    }
    badgesEl.replaceChildren(...badges);
}

/**
 * 成員表的主機格：有主機頁就連過去；停用主機上的進行中成員多一枚「主機已停用」標記（結案成員不標）。
 * showOccurrence（「全部」篩選）：同主機的新舊案件並列時標「第 N 次」，新舊才對得起來；
 * multiOnPage＝同一頁裡這台主機有不只一筆，第 1 次也標。
 */
function renderMemberHostCell(item, showOccurrence = false, multiOnPage = false) {
    const name = item.hostId ? document.createElement('a') : roleText('lf-text-title', item.hostName || '');
    if (item.hostId) {
        name.href = appUrl('/hosts/' + item.hostId);
        name.className = 'lf-text-title';
        name.textContent = item.hostName || '';
    }
    const occurrence = item.occurrence ?? 1;
    const occurrenceTag = showOccurrence && (occurrence > 1 || multiOnPage) ? roleText('lf-text-meta', `第 ${occurrence} 次`) : null;
    // 停用主機上的進行中成員不計入處理人的台數：成員列標出來，管理者才知道為什麼數字對不上
    const disabledTag = item.hostDisabled && !item.closedAt
        ? statusBadge('主機已停用', 'neutral', { title: '這台主機已停用，不計入處理人的台數；重新啟用後恢復計入。' })
        : null;
    // 同主機在同一頁有多列時標出登錄類型（Error／Warning 等不同登錄類型各自是一件案件，否則看起來是重複列）
    const logNameTag = multiOnPage && item.logName ? roleText('lf-text-meta', `登錄類型：${item.logName}`) : null;
    if (!occurrenceTag && !disabledTag && !logNameTag) return name;
    // 名稱連結與標記同一行：用既有的 lf-meta-line（行內連結點擊區至少 24×24，小字級也點得到）
    const wrap = document.createElement('div');
    wrap.className = 'lf-meta-line';
    wrap.append(name);
    if (logNameTag) wrap.append(logNameTag);
    if (occurrenceTag) wrap.append(occurrenceTag);
    if (disabledTag) wrap.append(disabledTag);
    return wrap;
}

/** 檢視者是管理側（具 Assign 或 ViewAll）：處理人視角不顯示寫給管理者的標示 */
function viewerIsAdminLike() {
    return hasCapability(currentUser, 'Assign') || hasCapability(currentUser, 'ViewAll');
}

/**
 * 成員台數旁的補充事實（有值才出現）：停用主機上的成員不計入左邊的台數，處理人目前看不到的台數是讀取時算的。
 * 兩者都是 0 時回空陣列，頁首不多一個字。
 */
function memberGapFacts(detail) {
    const facts = [];
    const disabled = detail.counts?.activeOnDisabledHosts ?? 0;
    if (disabled > 0) facts.push(fact('主機已停用', `${disabled} 台（不計入處理人的台數）`));
    // 「處理人看不到」是寫給管理者的訊息：處理人本人看了只會困惑（他無從處理），只對具 Assign／ViewAll 的檢視者出現
    const unseen = detail.handlerUnseenMemberCount ?? 0;
    if (unseen > 0 && viewerIsAdminLike()) facts.push(fact('處理人目前看不到', `${unseen} 台`));
    return facts;
}

/** 續掛已停止：只在後端標了 continuationStopped（進行中單）時出現，顯示原因的顯示名稱 */
function continuationStopNote(detail) {
    if (!detail.continuationStopped || !viewerIsAdminLike()) return null;   // 說明是寫給管理者的（恢復資格是管理動作）
    const label = detail.continuationStoppedLabel || detail.continuationStoppedReason || '';
    const when = detail.continuationStoppedAt ? `（${formatDate(detail.continuationStoppedAt)}）` : '';
    return statusNote('info', `續掛已停止${when}：${label}。夜間派工不再把新主機續掛進這張單，處理人恢復資格後，之後新出現的主機照常續掛。`);
}

/**
 * 頁首「成員」事實的文字：台數口徑（進行中台數、歷來台數與件數，見 hostCountText）、狀態分項（件數）、
 * 有復發主機時補「其中 N 台是復發」。
 */
function memberSummaryText(detail) {
    const counts = detail.counts || {};
    const memberParts = [];
    if (counts.inProgress > 0) memberParts.push(`處理中 ${counts.inProgress}`);
    if (counts.observing > 0) memberParts.push(`觀察 ${counts.observing}`);
    if (counts.open > 0) memberParts.push(`未處理 ${counts.open}`);
    if (counts.escalated > 0) memberParts.push(`上報 ${counts.escalated}`);
    if (counts.daySyncPending > 0) memberParts.push(`逐日同步中 ${counts.daySyncPending} 台`);
    const recurrence = detail.recurrenceHosts > 0 ? `；其中 ${detail.recurrenceHosts} 台是復發` : '';
    const prior = detail.priorDismissalHosts > 0 ? `；其中 ${detail.priorDismissalHosts} 台先前判定不處理、已滿 30 天仍在發生` : '';
    return hostCountText(detail) + recurrence + prior + (memberParts.length > 0 ? `（${memberParts.join('、')}）` : '');
}

/**
 * 頁首卡（DESIGN-SYSTEM §8.5）：單號與問題為主標，狀態與來源同一列，管理動作在右側；
 * 判斷層（處理人、成員、期限、最近回覆）收成一行 metaLine，
 * 佐證層（範圍、建立、最近新增）放在「詳細資訊」揭露內。
 */
function renderHeader(detail) {
    titleEl.textContent = `#${detail.workOrderId} ${detail.issueLabel || ''}`;
    renderBadges(detail);

    const nodes = [];

    if (detail.plainExplanation) {
        const explanation = document.createElement('p');
        explanation.className = 'mb-2';
        explanation.textContent = detail.plainExplanation;
        nodes.push(explanation);
    }

    // 交辦說明（有值才列）
    if (detail.note) {
        const note = document.createElement('p');
        note.className = 'mb-2';
        note.append(roleText('lf-text-label', '交辦說明'), ' ', detail.note);
        nodes.push(note);
    }

    // 處理人：只有本人或具 Assign／ViewAll 才開得了處理人頁，其餘只輸出姓名文字
    const handlerWrap = document.createElement('span');
    const handlerLink = document.createElement(canViewHandlerPage(currentUser, detail.handlerId) ? 'a' : 'span');
    if (handlerLink.tagName === 'A') handlerLink.href = appUrl('/handlers/' + detail.handlerId);
    handlerLink.textContent = detail.handlerName || '';
    handlerWrap.append(roleText('lf-text-label', '處理人'), ' ', handlerLink);
    if (!detail.handlerActive) handlerWrap.append(' ', statusBadge('已停用', 'secondary'));
    if (detail.handlerPaused) handlerWrap.append(' ', statusBadge('暫停接單', 'warning'));

    const membersText = memberSummaryText(detail);

    const lastReplyText = detail.lastReplyAt
        ? formatDateTime(detail.lastReplyAt)
        : (detail.unrepliedDays != null ? `尚未回覆（${detail.unrepliedDays} 天）` : '尚未回覆');

    nodes.push(metaLine([
        handlerWrap,
        fact('成員', membersText),
        ...memberGapFacts(detail),
        fact('期限', detail.dueDate ? formatDate(detail.dueDate) : '—'),
        fact('最近回覆', lastReplyText)
    ]));

    // 觀察期滿且期間沒有再發生的主機：一顆結案鈕放在判斷層（成員數字）正下方，不與頁面唯一的主要按鈕「回覆這張單」並排
    const quietBtn = quietCloseButton(detail);
    if (quietBtn) {
        const quietRow = document.createElement('div');
        quietRow.className = 'mt-2';
        quietRow.appendChild(quietBtn);
        nodes.push(quietRow);
    }

    const stopNote = continuationStopNote(detail);
    if (stopNote) nodes.push(stopNote);

    // 處置參考與訊息樣本：就地展開、不離開本頁；沒有內容（未命中規則、沒有樣本）就不放展開鈕
    for (const [label, panel] of [['處置參考', guidancePanel(detail.guidance)], ['訊息樣本', sampleMessagesPanel(detail.sampleMessages)]]) {
        if (panel) nodes.push(disclosure(label, panel, { className: 'mt-1' }), panel);
    }
    // 這個問題的捷徑：其他交辦單、靜音、調整規則（沒有 Maintain 時只有其他交辦單）
    const shortcuts = issueShortcuts(detail, {
        canListOrders: hasCapability(currentUser, 'Assign') || hasCapability(currentUser, 'ViewAll'),
        canMaintain: hasCapability(currentUser, 'Maintain'),
        onMute: () => openIssueMuteModal({
            source: detail.source,
            eventId: detail.eventId,
            issueLabel: detail.issueLabel,
            currentMute: null,
            onApplied: () => reloadAll()
        }),
        onHideTriage: () => openHideTriage({
            source: detail.source,
            eventId: detail.eventId,
            issueLabel: detail.issueLabel,
            ruleId: detail.ruleId ?? null,
            onApplied: () => reloadAll()
        })
    });
    if (shortcuts) nodes.push(shortcuts);

    // 範圍
    const scopeKindLower = String(detail.scopeKind).toLowerCase();
    let scopeText;
    switch (scopeKindLower) {
        case 'all':
            scopeText = '全站';
            break;
        case 'groups':
            scopeText = detail.scopeGroups && detail.scopeGroups.length > 0
                ? detail.scopeGroups.map(g => g.groupName).join('、')
                : '主機群組';
            break;
        default:
            scopeText = '指定主機';
    }
    if (scopeKindLower !== 'hosts') {
        scopeText += detail.autoAttach ? '（續掛：開）' : '（續掛：關）';
    }
    const createdBy = detail.createdByAccount ? `（${detail.createdByAccount}）` : '（系統）';
    const evidence = metaLine([
        fact('範圍', scopeText),
        fact('建立', `${formatDateTime(detail.createdAt)} ${createdBy}`),
        fact('最近新增', detail.lastAppendedAt ? formatDateTime(detail.lastAppendedAt) : '—')
    ], { className: 'd-none mt-1' });
    nodes.push(disclosure('詳細資訊', evidence, { className: 'mt-1' }), evidence);

    headerContainer.replaceChildren(...nodes);
}

// ── 動作按鈕 ───────────────────────────────────────────────────────────────

// 處理人回覆的入口在處理人工作頁（/handlers/{id}），詳情頁只放管理動作

/**
 * 勾選框與後果說明指稱的對象（同一份，兩處不各拼一份）：台數含停用主機；
 * 只有單一問題的單（有 source）才能說「這個問題」，多問題單改指稱主機上的問題。
 */
function blockTargetText(activeCount, singleIssue) {
    const hosts = activeCount > 1 ? `這 ${activeCount} 台主機` : '這台主機';
    return singleIssue ? `${hosts}的這個問題` : `${hosts}上的問題`;
}

async function buildAdminCloseModal() {
    let modal = null;   // showDetailModal 回傳的控制代碼，送出成功後以 close() 關閉
    // 結案通知要不要寄取決於交辦單通知開關：依實際設定說明，不承諾不一定寄出的信；讀不到設定就用中性句
    const mailSettings = await getDisplaySettings();
    // 後端 AdminClose 結案所有進行中成員，含停用主機上的：台數要連停用的一起算
    const onDisabled = currentDetail?.counts?.activeOnDisabledHosts ?? 0;
    const activeCount = (currentDetail?.counts?.active ?? 0) + onDisabled;
    // 範圍：全部進行中／只有上報中的／勾選的成員（後兩者有才出現）；有上報成員時預設「只有上報中的」
    const scopeOptions = adminCloseScopeOptions({
        activeCount,
        escalatedCaseIds: currentDetail?.escalatedCaseIds ?? [],
        selectedCaseIds: [...selectedCaseIds.keys()]
    });
    let scope = scopeOptions.find(option => option.value === defaultAdminCloseScope(scopeOptions));
    const body = document.createElement('div');
    const form = document.createElement('form');

    const statusLabel = document.createElement('label');
    statusLabel.className = 'form-label lf-text-label';
    statusLabel.textContent = '結案狀態';
    statusLabel.htmlFor = 'wo-close-status';

    // 範圍選擇（有上報成員或有勾選才顯示）：單選，預設見 defaultAdminCloseScope
    const scopeWrap = document.createElement('fieldset');
    scopeWrap.className = 'mb-3' + (scopeOptions.length > 1 ? '' : ' d-none');
    const scopeLegend = document.createElement('legend');
    scopeLegend.className = 'form-label lf-text-label';
    scopeLegend.textContent = '結案範圍';
    scopeWrap.appendChild(scopeLegend);
    for (const option of scopeOptions) {
        const wrap = document.createElement('div');
        wrap.className = 'form-check';
        const input = document.createElement('input');
        input.type = 'radio';
        input.className = 'form-check-input';
        input.name = 'wo-close-scope';
        input.id = `wo-close-scope-${option.value}`;
        input.value = option.value;
        input.checked = option === scope;
        input.addEventListener('change', () => {
            if (!input.checked) return;
            scope = option;
            syncState();
        });
        const label = document.createElement('label');
        label.className = 'form-check-label';
        label.htmlFor = input.id;
        label.textContent = option.label;
        wrap.append(input, label);
        scopeWrap.appendChild(wrap);
    }

    // 不預選：代為結案影響整張單，結案狀態要管理者自己選一次
    const statusSelect = document.createElement('select');
    statusSelect.id = 'wo-close-status';
    statusSelect.className = 'form-select form-select-sm mb-3';
    const placeholder = document.createElement('option');
    placeholder.value = '';
    placeholder.textContent = '請選擇結案狀態';
    statusSelect.appendChild(placeholder);
    const closeStatuses = statusesWhere(s => s.closed).map(s => ({ value: s.value, label: s.shortLabel }));
    for (const opt of closeStatuses) {
        const option = document.createElement('option');
        option.value = opt.value;
        option.textContent = opt.label;
        statusSelect.appendChild(option);
    }
    statusSelect.value = '';

    // 停派勾選：只有不處理類才有意義（結案為已處理時後端忽略）；預設不勾，改選別的狀態時值清掉
    const blockWrap = document.createElement('div');
    blockWrap.className = 'form-check mb-3 d-none';
    const blockInput = document.createElement('input');
    blockInput.type = 'checkbox';
    blockInput.id = 'wo-close-block';
    blockInput.className = 'form-check-input';
    blockInput.checked = false;
    const blockLabel = document.createElement('label');
    blockLabel.className = 'form-check-label';
    blockLabel.htmlFor = 'wo-close-block';
    // 後果說明指稱的對象依所選範圍的台數；範圍換了要重寫（syncState）
    let targetText = blockTargetText(scope.count, !!currentDetail?.source);
    blockWrap.append(blockInput, blockLabel);

    // 後果說明：通知依設定而定；不處理類再依勾選說明問題再出現時會怎樣
    const consequence = document.createElement('div');
    consequence.className = 'mb-3';
    consequence.id = 'wo-close-consequence';

    const reasonLabel = document.createElement('label');
    reasonLabel.className = 'form-label lf-text-label';
    reasonLabel.textContent = '結案原因（必填）';
    reasonLabel.htmlFor = 'wo-close-reason';

    const reasonInput = document.createElement('textarea');
    reasonInput.id = 'wo-close-reason';
    reasonInput.className = 'form-control form-control-sm mb-3';
    reasonInput.rows = 3;
    reasonInput.required = true;

    const submitBtn = button(`代為結案 ${scope.count} 台`, { variant: 'primary', size: null, type: 'submit', disabled: true });
    const actions = document.createElement('div');
    actions.className = 'lf-form-actions d-flex justify-content-end';
    actions.appendChild(submitBtn);

    function renderConsequence() {
        const notifyText = mailNotifyText(mailSettings, '處理人會在下一封交辦摘要收到結案通知', '目前未啟用交辦單通知，請自行告知處理人');
        const notes = [statusNote('info', `${notifyText}。` + (scope.value === 'all' && onDisabled > 0 ? `含已停用主機 ${onDisabled} 台。` : ''))];
        if (scope.value !== 'all') notes.push(statusNote('info', `只結案${scope.name} ${scope.count} 台，其餘成員維持原狀、單繼續進行。`));
        if (isNoFixClosure(statusSelect.value)) {
            notes.push(statusNote('warning', blockInput.checked
                ? `已勾選：${targetText}之後不會再自動派工。`
                : `未勾選：${targetText}之後再出現時會重新派工。不想再收到，請勾選上方選項，或改用靜音。`));
        }
        consequence.replaceChildren(...notes);
    }
    function syncState() {
        targetText = blockTargetText(scope.count, !!currentDetail?.source);
        blockLabel.textContent = `${targetText}之後不再自動派工（勾選＝永久，不會因時間自動恢復）`;
        submitBtn.children[submitBtn.children.length - 1].textContent = `代為結案 ${scope.count} 台`;   // button() 的文字在最後一個子節點
        const noFix = isNoFixClosure(statusSelect.value);
        blockWrap.classList.toggle('d-none', !noFix);
        if (!noFix) blockInput.checked = false;
        renderConsequence();
        submitBtn.disabled = !statusSelect.value || !reasonInput.value.trim();
    }
    statusSelect.addEventListener('change', syncState);
    blockInput.addEventListener('change', renderConsequence);
    reasonInput.addEventListener('input', syncState);
    syncState();

    form.append(scopeWrap, statusLabel, statusSelect, blockWrap, reasonLabel, reasonInput, consequence, actions);

    form.addEventListener('submit', async event => {
        event.preventDefault();
        const reason = reasonInput.value.trim();
        if (!statusSelect.value) {
            toast('請選擇結案狀態', 'warning');
            statusSelect.focus();
            return;
        }
        if (!reason) {
            toast('請填寫結案原因', 'warning');
            reasonInput.focus();
            return;
        }

        const restore = withBusy(submitBtn, '處理中…');
        try {
            const res = await api.post(`/api/work-orders/${workOrderId}/admin-close`, {
                status: statusSelect.value,
                reason,
                blockRedispatch: isNoFixClosure(statusSelect.value) && blockInput.checked,
                // 範圍＝全部進行中時不帶案件清單（整張單結案）；其餘只結案這些案件
                ...(scope.caseIds ? { caseIds: scope.caseIds } : {})
            });
            modal.close();
            toast(`已代為結案 ${res.closedCases} 台`, 'success');
            await reloadAll();
        } catch {
            restore();
        }
    });

    body.appendChild(form);
    modal = showDetailModal({ title: '代為結案', body });
}

/** 設定請求回來前再按一次不會疊開第二個對話框 */
const openAdminCloseModal = exclusive(buildAdminCloseModal);

/**
 * 以本單的回覆端點回覆指定成員；成功後重新載入本頁。
 * caseIds 為空＝全部進行中主機（端點語意），此時 hostCount 帶實際台數；被拒（單已結案、成員已非進行中、單已改派）
 * 一樣重新載入單頭、成員表與時間軸，訊息與草稿由彈窗保留，403 改寫成含新處理人的說明。
 */
function openMemberReply(caseIds, targetText, hostCount = caseIds.length, recurrenceHosts = null) {
    // 復發主機數：指定成員的（成員表勾選、單一成員）由呼叫端以成員自己的 isRecurrence 數好傳入；整張單回覆取單頭的數字；
    // 指定成員卻沒給數字就不提示（不拿單頭的數字近似——單頭有復發不代表這一台是）
    const recurrenceInReply = recurrenceHosts
        ?? (caseIds.length === 0 ? (currentDetail.recurrenceHosts ?? 0) : 0);
    openWorkOrderReplyModal({
        recurrence: { hosts: recurrenceInReply },
        dismissalReleases: !!currentDetail.dismissalReleases,
        multiTarget: hostCount > 1,
        title: `回覆交辦單 #${workOrderId}`,
        targetText,
        draftKey: caseIds.length > 0
            ? `order:${workOrderId}:cases:${[...caseIds].sort((a, b) => a - b).join(',')}`
            : `order:${workOrderId}:all`,
        aiContext: { issueLabel: currentDetail.issueLabel, hostCount },
        reuseIssueKey: currentDetail.issueKey || null,
        // 對象是全部進行中主機時，彈窗內可切換成「只回覆部分主機」（面板挑的 caseId 以第二個參數傳回）
        picker: caseIds.length === 0
            ? { load: ({ page, pageSize, hostNames }) => api.get(orderMembersUrl(workOrderId, { status: 'active', page, pageSize, hostNames }), { silent: true }) }
            : undefined,
        partialTarget: caseIds.length > 0,
        followUp: {
            remaining: () => remainingActiveCount(workOrderId),
            submitRest: async payload => {
                const result = await api.post(`/api/work-orders/${workOrderId}/reply`, { caseIds: [], ...payload }, { silent: true });
                toastReplyResult(result, payload);
                return result;
            }
        },
        submit: async (payload, picked) => {
            const result = await api.post(`/api/work-orders/${workOrderId}/reply`, { caseIds: picked ?? caseIds, ...payload }, { silent: true });
            toastReplyResult(result, payload);
            return result;
        },
        onRejected: error => {
            reloadAll();   // guardLoad 自行處理載入失敗，不會擲出
            return describeReplyRejection(error, workOrderId);
        },
        onApplied: () => reloadAll()
    });
}

/**
 * 「把到期且沒有再發生的 N 台結案」：觀察期滿、觀察期間確定沒有再發生的成員（後端 observationExpiredQuietCaseIds）一次結案。
 * 不自動結案——先 confirmAction 說清楚影響，再以既有回覆端點送「已處理」、說明「觀察期滿未再發生」；
 * 成功沿用回覆的共用成功訊息並重載單頭、成員與時間軸。名單為空、檢視者不能回覆或單已結案時回 null。
 */
function quietCloseButton(detail) {
    const caseIds = detail?.observationExpiredQuietCaseIds ?? [];
    if (caseIds.length === 0 || !detail.viewerCanReply || detail.closedAt) return null;
    const btn = button(`把到期且沒有再發生的 ${caseIds.length} 台結案`, { variant: 'outline-secondary', size: null, onClick: async () => {
        const confirmed = await confirmAction({
            title: '結案觀察期滿的主機？',
            message: `將把 ${caseIds.length} 台觀察期已滿、期間沒有再發生的主機設為「已處理」，說明寫「${QUIET_CLOSE_NOTE}」。觀察期間沒有分析資料的主機不在其中。`,
            confirmText: '結案',
            confirmVariant: 'primary'
        });
        if (!confirmed) return;
        const payload = { status: 'resolved', note: QUIET_CLOSE_NOTE, dueDate: null };
        const restore = withBusy(btn, '結案中…');
        try {
            const result = await api.post(`/api/work-orders/${workOrderId}/reply`, { caseIds, ...payload }, { silent: true });
            toastReplyResult(result, payload);
        } catch (error) {
            restore();
            toast(error?.message || '結案失敗，請再試一次', 'danger');
            // 被拒（單已結案、成員已非進行中、單已改派）：畫面資料已過期，重載
            if (error?.status === 400 || error?.status === 403) await reloadAll();
            return;
        }
        restore();
        await reloadAll();
    } });
    return btn;
}

function renderActions(detail, user) {
    actionsContainer.replaceChildren();

    // 原處理人唯讀：不顯示任何動作鈕
    if (detail.viewerIsPreviousHandler && !detail.viewerIsHandler && !detail.viewerCanAssign) {
        return;
    }

    if (detail.closedAt) {
        return;
    }

    if (detail.viewerCanReply) {
        // 與工作頁列上的「回覆」同一套：一律開回覆彈窗（0 台不開）；1 台對象是那一台，多台預設全部進行中主機（caseIds 為空）
        // 處理人在這一頁最常做的事：頁面唯一的實心主要按鈕（管理動作是一般次要按鈕，處理人看不到）
        const replyBtn = button('回覆這張單', { variant: 'primary', size: null, onClick: async () => {
            // 含停用主機上的進行中成員：成員清單與回覆端點都涵蓋它們（與處理人工作頁同一個算法）
            const activeCount = replyableCount(detail.counts);
            if (activeCount === 0) {
                toast('這張單已沒有進行中的主機', 'info');
                return;
            }
            if (activeCount > 1) {
                openMemberReply([], `全部進行中主機（${activeCount} 台）`, activeCount);
                return;
            }
            const params = new URLSearchParams({ status: 'active', page: '1', pageSize: '1' });
            const data = await api.get(`/api/work-orders/${workOrderId}/members?${params}`);
            const [member] = data.items;
            if (!member) {
                toast('這張單已沒有進行中的主機', 'info');
                return;
            }
            openMemberReply([member.caseId], `主機 ${member.hostName}`, 1, member.isRecurrence ? 1 : 0);
        } });
        actionsContainer.appendChild(replyBtn);
    }

    if (!detail.viewerCanAssign) {
        return;
    }

    // 改派按鈕
    const reassignBtn = button('改派', { size: null, opens: 'dialog', onClick: () => {
        openSelectHandlerModal({
            title: '改派交辦單',
            submitText: '確定改派',
            currentHandlerId: detail.handlerId,
            showNotifyNow: true,
            hostIds: [...new Set(loadedMemberHosts.values())],
            onSubmit: async (handlerId, closeModal, { notifyNow }) => {
                const res = await api.post(`/api/work-orders/${workOrderId}/reassign`, { handlerId, notifyNow });
                closeModal();
                const noEmailNote = res.handlerHasNoEmail ? '（處理人未設定 email，不會收到通知）' : '';
                if (res.mergedIntoExisting) {
                    toast(`已改派 ${res.movedCases} 台，併入單號 #${res.targetWorkOrderId}${noEmailNote}`, 'success');
                } else {
                    toast(`已改派 ${res.movedCases} 台${noEmailNote}`, 'success');
                }
                if (res.assigneeCannotHandle && res.assigneeCannotHandle.length > 0) {
                    for (const item of res.assigneeCannotHandle) {
                        toast(`${item.handlerName} 沒有處理權限（${item.hostCount} 台）`, 'warning');
                    }
                }
                if (res.targetWorkOrderId !== workOrderId) {
                    location.href = appUrl('/work-orders/' + res.targetWorkOrderId);
                } else {
                    await reloadAll();
                }
            }
        });
    } });
    actionsContainer.appendChild(reassignBtn);

    // 退回處理人：處理人把成員標成「無法處理」後，管理者可附說明把這些成員退回同一位處理人（改回處理中）
    if ((detail.counts?.escalated ?? 0) > 0 && !detail.closedAt) {
        const returnBtn = button('退回處理人', { size: null, opens: 'dialog', onClick: async () => {
            const notifyText = mailNotifyText(await getDisplaySettings(), '處理人會在下一封交辦摘要收到退回通知', '目前未啟用交辦通知，請自行告知處理人') + '。';
            const note = await confirmActionWithReason({
                title: '退回處理人',
                message: `把這張單中標為「無法處理」的主機退回處理人，狀態改回處理中。${notifyText}`,
                reasonLabel: '退回說明',
                confirmText: '確定退回',
                confirmVariant: 'primary'
            });
            if (!note) return;

            const restore = withBusy(returnBtn, '處理中…');
            try {
                const res = await api.post(`/api/work-orders/${workOrderId}/return`, { note });
                toast(`已退回，${res.returnedCases} 台改回處理中`, 'success');
                await reloadAll();
            } catch {
                // 錯誤訊息已由 api.js 顯示
                restore();
            }
        } });
        actionsContainer.appendChild(returnBtn);
    }

    // 取消交辦按鈕
    const cancelBtn = button('取消交辦', { variant: 'outline-danger', size: null, opens: 'dialog', onClick: async () => {
        // 交辦通知預設關閉：依設定說實話，避免承諾一封不會寄的信；讀不到設定就用中性句
        const notifyText = mailNotifyText(await getDisplaySettings(), '處理人會在下一封交辦摘要收到取消通知', '目前未啟用交辦通知，請自行告知處理人') + '。';
        const reason = await confirmActionWithReason({
            title: '取消交辦',
            message: `取消後這張單的進行中主機會調回未處理，${notifyText}`,
            reasonLabel: '取消原因',
            confirmText: '確定取消',
            confirmVariant: 'danger'
        });
        if (!reason) return;

        const restore = withBusy(cancelBtn, '處理中…');
        try {
            const res = await api.post(`/api/work-orders/${workOrderId}/cancel`, { reason });
            toast(`已取消，${res.closedCases} 台調回未處理`, 'success');
            await reloadAll();
        } catch {
            // 錯誤訊息已由 api.js 顯示
            restore();
        }
    } });
    actionsContainer.appendChild(cancelBtn);

    // 代為結案按鈕
    if (hasCapability(user, 'Assign') && hasCapability(user, 'Handle')) {
        const closeBtn = button('代為結案', { size: null, opens: 'dialog', onClick: () => openAdminCloseModal() });
        actionsContainer.appendChild(closeBtn);
    }
}

// ── 成員表 ─────────────────────────────────────────────────────────────────

function updateSplitBtn() {
    const canSplit = currentDetail && currentDetail.viewerCanAssign && !currentDetail.closedAt;
    if (!canSplit) {
        memberSplitBtn.classList.add('d-none');
        memberSplitBtn.disabled = true;
        return;
    }
    memberSplitBtn.classList.remove('d-none');
    memberSplitBtn.disabled = selectedCaseIds.size === 0;
}

function updateReplyBtn() {
    const canReply = currentDetail && currentDetail.viewerCanReply;
    if (!canReply) {
        memberReplyBtn.classList.add('d-none');
        memberReplyBtn.disabled = true;
        return;
    }
    memberReplyBtn.classList.remove('d-none');
    memberReplyBtn.disabled = selectedCaseIds.size === 0;
}

/** 最近一次畫出的成員頁資料：全選本頁後原地重畫用 */
let lastMemberData = null;
/** 整單結案後只自動切一次篩選：使用者之後自己選回「進行中」就尊重他的選擇 */
let memberAutoSwitched = false;

function renderMemberRows() {
    if (lastMemberData) renderMembers(lastMemberData);
}

/**
 * 成員表空狀態依篩選組字：有主機名篩選＝沒有符合條件；「進行中」篩選但單上有已結案成員＝直說已結案幾台
 * （不再寫「沒有關聯的成員主機」——單上其實有成員，只是都結案了）。
 */
function memberEmptyState(status, hostName, counts) {
    if (hostName) return { title: '沒有符合條件的主機', hint: '請確認主機名完整相符，或清除主機名篩選。' };
    if (status === 'active' && (counts?.closed ?? 0) > 0) {
        return { title: `沒有進行中的主機（已結案 ${counts.closed} 台）`, hint: '把狀態篩選改為「已結案」或「全部」可查看這些主機。' };
    }
    if (status === 'active') return { title: '沒有進行中的主機', hint: '這張單目前沒有進行中的成員主機。' };
    return { title: '沒有符合條件的主機', hint: '請調整狀態篩選。' };
}

function renderMembers(data) {
    lastMemberData = data;
    currentMemberItems = data.items || [];
    loadedMemberHosts.clear();
    for (const m of data.items || []) {
        if (m.hostId != null) loadedMemberHosts.set(m.caseId, m.hostId);
    }
    if (data.hiddenMemberCount > 0) {
        memberNoteEl.textContent = `另有 ${data.hiddenMemberCount} 台主機不在您的檢視範圍，未列出`;
        memberNoteEl.classList.remove('d-none');
    } else {
        memberNoteEl.textContent = '';
        memberNoteEl.classList.add('d-none');
    }

    const canSplit = currentDetail && currentDetail.viewerCanAssign && !currentDetail.closedAt;
    const canReply = currentDetail && currentDetail.viewerCanReply;
    const showCheckboxes = canSplit || canReply;
    const columns = [];
    // 「全部」篩選下新舊案件並列：同主機的列標「第 N 次」
    const showOccurrence = memberStatusSelect.value === 'all';
    const hostRows = new Map();
    for (const m of data.items || []) hostRows.set(m.hostName, (hostRows.get(m.hostName) ?? 0) + 1);

    if (showCheckboxes) {
        columns.push({
            title: '',
            className: 'text-center text-nowrap',
            renderHeader: () => {
                const chk = selection.selectAllHeaderCheckbox();
                chk.title = canReply && !canSplit ? '全選本頁可回覆主機' : (canSplit && !canReply ? '全選本頁可拆出主機' : '全選本頁可處理主機');
                chk.disabled = currentMemberItems.filter(item => !item.closedAt).length === 0;
                return chk;
            },
            render: item => {
                const cell = selection.rowCheckboxCell(item);
                if (cell.classList) cell.classList.add('wo-member-select');
                return cell;
            }
        });
    }

    columns.push(
        {
            title: '主機',
            truncate: true,
            render: item => renderMemberHostCell(item, showOccurrence, (hostRows.get(item.hostName) ?? 0) > 1)
        },
        {
            title: '狀態',
            render: item => memberStatusCell(item)
        },
        {
            title: '期限',
            className: 'text-nowrap',
            render: item => {
                const wrap = document.createElement('div');
                wrap.className = 'd-flex align-items-center gap-1 text-nowrap';
                wrap.appendChild(roleText('lf-text-data', item.dueDate ? formatDate(item.dueDate) : '—'));
                if (item.overdue) {
                    // 逾期是風險狀態
                    wrap.appendChild(roleText('lf-text-alert', '逾期'));
                }
                return wrap;
            }
        },
        {
            title: '期間',
            className: 'text-nowrap',
            render: item => roleText('lf-text-meta', `${formatDate(item.firstLinkedDate)} ~ ${formatDate(item.lastLinkedDate)}`)
        },
        {
            title: '附註',
            className: 'text-nowrap',
            render: item => {
                const notes = [];
                if (item.daySyncPending) notes.push('逐日同步中');
                if (item.cancelled) notes.push('已取消');
                if (item.closedAt) notes.push(`結案 ${formatDateTime(item.closedAt)}`);
                const wrap = document.createElement('div');
                wrap.className = 'd-flex flex-column align-items-start gap-1';
                const noteText = notes.join('、') || ((item.relatedWorkOrderId || item.note) ? '' : '—');
                if (noteText) wrap.appendChild(roleText('lf-text-meta', noteText));
                // 處理人回覆時寫的說明：上報中的成員這就是上報原因，管理者要處理時第一眼就要看到
                if (item.note) {
                    const reason = item.status === 'escalated' ? '上報原因' : '處理說明';
                    const noteRow = document.createElement('div');
                    noteRow.className = 'lf-text-meta text-wrap';
                    noteRow.append(roleText('lf-text-label', reason), ' ', item.note);
                    wrap.appendChild(noteRow);
                }
                if (item.relatedWorkOrderId) {
                    const link = document.createElement('a');
                    link.href = appUrl('/work-orders/' + item.relatedWorkOrderId);
                    link.textContent = `同主機／sensor 關聯單 #${item.relatedWorkOrderId}`;
                    wrap.appendChild(link);
                }
                if (item.diskTrend) {
                    wrap.appendChild(roleText('lf-text-meta', `同感測器磁碟趨勢（${item.diskTrend.sensorId}）｜資料截至 ${formatDate(item.diskTrend.recordDate)}`));
                    if (item.diskTrend.detail) {
                        const detail = document.createElement('div');
                        detail.textContent = item.diskTrend.detail;
                        wrap.appendChild(detail);
                    }
                }
                if (item.prtgEvidence) {
                    // 佐證層：建案當時的 PRTG 證據預設收合，點「PRTG 建案證據」才展開
                    const evidence = document.createElement('div');
                    const detail = document.createElement('p');
                    detail.className = 'lf-text-meta text-wrap d-none mb-0';
                    const parentLabels = { available: item.prtgEvidenceRecordPath ? '原分析仍可查閱' : '原分析仍保留；您的權限僅限此交辦單證據', 'host-deleted': '主機已刪除',
                        'deleted-or-not-retained': '原分析已刪除或超過保留期', 'detail-pruned': '原分析詳情已精簡',
                        'reanalysed-without-finding': '原日期已重新分析，目前不含此判定' };
                    detail.textContent = `${item.prtgEvidence.summary}（品質：${item.prtgEvidence.quality}）。${parentLabels[item.prtgEvidenceParentStatus] || '原分析狀態未知'}。這是建案時保留的證據；目前狀態請查最新分析紀錄。`;
                    evidence.append(disclosure(`PRTG 建案證據｜${formatDate(item.prtgEvidence.recordDate)}`, detail), detail);
                    if (item.prtgEvidenceRecordPath && item.prtgEvidenceParentStatus === 'available') {
                        const link = document.createElement('a'); link.href = appUrl(item.prtgEvidenceRecordPath);
                        link.textContent = '查看原日期分析與 PRTG 補充問題'; detail.append(' ', link);
                    }
                    wrap.appendChild(evidence);
                }
                return wrap;
            }
        }
    );

    renderTable(membersContainer, {
        columns,
        rows: data.items,
        empty: memberEmptyState(memberStatusSelect.value, memberHostName, currentDetail?.counts)
    });

    renderPagination(memberPagerContainer, {
        page: data.page,
        totalPages: Math.ceil(data.total / data.pageSize),
        pageSize: data.pageSize,
        onPage: newPage => {
            memberPage = newPage;   // 勾選跨頁保留
            guardLoad(membersContainer, loadMembersOnly);
        }
    });

    updateSplitBtn();
    updateReplyBtn();
}

// ── 時間軸 ─────────────────────────────────────────────────────────────────

/**
 * 事件註記轉畫面用語：回覆與代為結案的註記是「狀態碼：說明」，把開頭的狀態碼換成簡稱；
 * 改派註記後端已直接給處理人姓名（「王小明(wang) → 李四(lee)」），原樣顯示。
 */
function eventNoteText(note) {
    // 系統結案事件的註記就是結案原因代碼本身（all_closed／moved／empty_dispatch…）
    const reason = workOrderClosedReasonText(note);
    if (reason !== note) return reason;
    // 「狀態碼：說明（N 台）」與不填說明的「狀態碼（N 台）」：開頭的狀態碼緊接全形冒號或左括號
    const code = note.match(/^([a-z_]+)(?=[：（])/);
    const meta = code ? statusBadgeMeta(code[1]) : null;
    return meta ? meta.label + note.slice(code[1].length) : note;
}

function renderTimeline(timeline) {
    if (!timeline || timeline.length === 0) {
        renderEmpty(timelineContainer, { title: '尚無歷程', hint: '此交辦單尚未有改派、回覆或結案等稽核紀錄。' });
        return;
    }

    const items = timeline.slice().sort((a, b) => new Date(b.createdAt) - new Date(a.createdAt));
    const list = document.createElement('div');
    list.className = 'list-group list-group-flush';

    for (const ev of items) {
        const item = document.createElement('div');
        item.className = 'list-group-item px-0 py-2';

        const topRow = document.createElement('div');
        topRow.className = 'd-flex flex-wrap align-items-center gap-2';

        const timeSpan = roleText('lf-text-meta lf-text-data', formatDateTime(ev.createdAt));
        topRow.appendChild(timeSpan);

        const actorSpan = roleText('lf-text-title', ev.actorName || '系統');
        topRow.appendChild(actorSpan);

        const actionSpan = document.createElement('span');
        actionSpan.textContent = ev.actionText || '';
        topRow.appendChild(actionSpan);

        if (ev.memberDelta !== 0) {
            const deltaBadge = statusBadge(ev.memberDelta > 0 ? `+${ev.memberDelta} 台` : `−${Math.abs(ev.memberDelta)} 台`, 'light');
            deltaBadge.classList.add('border');
            topRow.appendChild(deltaBadge);
        }

        item.appendChild(topRow);

        if (ev.note) {
            const noteRow = roleText('lf-text-meta', eventNoteText(ev.note));
            noteRow.classList.add('d-block', 'mt-1', 'ps-2', 'border-start');
            item.appendChild(noteRow);
        }

        list.appendChild(item);
    }

    timelineContainer.replaceChildren(list);
}

// ── 事件綁定 ───────────────────────────────────────────────────────────────

memberStatusSelect.addEventListener('change', () => {
    memberPage = 1;
    selection.clear();   // 勾選是對「這個狀態篩選下的清單」做的
    guardLoad(membersContainer, loadMembersOnly);
});

/** 主機名搜尋（完全相符、不分大小寫）：回第一頁，勾選保留 */
memberSearchInput.addEventListener('change', () => {
    memberHostName = memberSearchInput.value.trim();
    memberPage = 1;
    guardLoad(membersContainer, loadMembersOnly);
});

memberSplitBtn.addEventListener('click', () => {
    if (selectedCaseIds.size === 0) return;
    openSelectHandlerModal({
        title: '拆出選取的主機',
        submitText: '確定拆出',
        currentHandlerId: currentDetail?.handlerId,
        hostIds: [...new Set([...selectedCaseIds.values()].map(item => item.hostId).filter(id => id != null))],
        onSubmit: async (handlerId, closeModal) => {
            const caseIds = [...selectedCaseIds.keys()];
            const res = await api.post(`/api/work-orders/${workOrderId}/split`, { caseIds, handlerId });
            closeModal();
            toast(`已拆出 ${res.movedCases} 台到單號 #${res.targetWorkOrderId}${res.handlerHasNoEmail ? '（處理人未設定 email，不會收到通知）' : ''}`, 'success');
            if (res.assigneeCannotHandle && res.assigneeCannotHandle.length > 0) {
                for (const item of res.assigneeCannotHandle) {
                    toast(`${item.handlerName} 沒有處理權限（${item.hostCount} 台）`, 'warning');
                }
            }
            selection.clear();
            await reloadAll();
        }
    });
});

memberReplyBtn.addEventListener('click', () => {
    if (selectedCaseIds.size === 0) return;
    const caseIds = [...selectedCaseIds.keys()];
    const recurrenceHosts = [...selectedCaseIds.values()].filter(item => item?.isRecurrence).length;
    openMemberReply(caseIds, `選取的 ${caseIds.length} 台主機`, caseIds.length, recurrenceHosts);
});

memberCsvBtn.addEventListener('click', async () => {
    const restore = withBusy(memberCsvBtn, '複製中…');
    try {
        const status = memberStatusSelect.value;
        let page = 1;
        const allItems = [];
        while (true) {
            const res = await api.get(`/api/work-orders/${workOrderId}/members?status=${status}&page=${page}&pageSize=200`);
            if (res.items && res.items.length > 0) {
                allItems.push(...res.items);
            }
            // 以後端實際採用的頁大小判斷（後端會夾上限），不假設等於請求值
            if (!res.items || res.items.length < res.pageSize || allItems.length >= res.total) {
                break;
            }
            page++;
        }

        const header = ['主機', '狀態', '期限', '逾期', '首次關聯', '最近關聯', '附註'];
        const escapeCsv = val => {
            const str = val == null ? '' : String(val);
            if (str.includes(',') || str.includes('"') || str.includes('\n') || str.includes('\r')) {
                return `"${str.replace(/"/g, '""')}"`;
            }
            return str;
        };

        const rows = [header.join(',')];
        for (const item of allItems) {
            const statusText = statusBadgeMeta(item.status)?.label || item.status || '';
            const dueText = item.dueDate ? formatDate(item.dueDate) : '';
            const overdueText = item.overdue ? '是' : '否';
            const firstLinked = item.firstLinkedDate ? formatDate(item.firstLinkedDate) : '';
            const lastLinked = item.lastLinkedDate ? formatDate(item.lastLinkedDate) : '';
            const notes = [];
            if (item.daySyncPending) notes.push('逐日同步中');
            if (item.cancelled) notes.push('已取消');
            if (item.closedAt) notes.push(`結案 ${formatDateTime(item.closedAt)}`);
            const noteText = notes.join('、');

            rows.push([
                escapeCsv(item.hostName),
                escapeCsv(statusText),
                escapeCsv(dueText),
                escapeCsv(overdueText),
                escapeCsv(firstLinked),
                escapeCsv(lastLinked),
                escapeCsv(noteText)
            ].join(','));
        }

        const csvContent = rows.join('\r\n');
        const copied = await copyText(csvContent);
        if (copied.ok) toast(`已複製 ${allItems.length} 台`, 'success');
        else toast(`複製失敗：${copied.reason}`, 'danger');
    } catch {
        // api.js 已跳出錯誤提示
    } finally {
        restore();
    }
});

// ── 載入流程 ───────────────────────────────────────────────────────────────

/** 成員清單的請求序號：連點分頁或連續換篩選時，慢回來的舊請求不可蓋掉新結果 */
const membersGuard = createSeqGuard();

/** 成員表目前這一頁的請求網址（狀態、頁碼、主機名篩選） */
function memberListUrl() {
    return orderMembersUrl(workOrderId, {
        status: memberStatusSelect.value, page: memberPage, pageSize: PAGE_SIZE,
        hostNames: memberHostName ? [memberHostName] : null
    });
}

async function loadMembersOnly() {
    const seq = membersGuard.next();
    renderLoading(membersContainer, 5);
    const members = await api.get(memberListUrl());
    if (!membersGuard.isCurrent(seq)) return;
    renderMembers(members);
}

/**
 * 檢視者曾是這張單的處理人、單已改派給別人（後端 viewerIsPreviousHandler；此時他仍能看這頁，
 * 代表另有 Assign／ViewAll）：說明這頁為什麼沒有回覆入口，並指回自己的交辦。
 * 只有 Handle 的原處理人拿到的是 403，改由拒絕畫面的提示處理（見 loadAll）。
 */
function renderReassignedHint(detail, user) {
    const existing = rootEl.querySelector('.wo-reassign-hint');
    if (existing) existing.remove();

    if (detail.viewerIsHandler || !detail.viewerIsPreviousHandler) return;

    const hintEl = document.createElement('div');
    hintEl.className = 'lf-hint mb-3 p-3 bg-light rounded border wo-reassign-hint';
    hintEl.appendChild(icon('info-circle'));

    // 同時具 Assign 的人仍能改派這張單，不能對他說「只能檢視」
    const target = detail.handlerName || '其他處理人';
    const span = document.createElement('span');
    span.textContent = detail.viewerCanAssign
        ? `此單已移交給 ${target}。`
        : `此單已移交給 ${target}，你只能檢視。`;

    const link = document.createElement('a');
    link.href = appUrl(`/handlers/${user.userId}`);
    link.textContent = '回到我的交辦';
    link.className = 'ms-2';
    span.appendChild(link);

    hintEl.appendChild(span);
    rootEl.prepend(hintEl);
}

/**
 * 通知後端「這張單已被管理者看過」（未讀回覆判定）：載入詳情成功後只報一次，失敗靜默。
 * 只有具 Assign 的檢視者才會被後端記錄；詳情的 GET 本身不寫任何東西。
 */
let seenReported = false;
function markSeenOnce(detail) {
    if (seenReported || !detail.viewerCanAssign) return;
    seenReported = true;
    api.post(`/api/work-orders/${workOrderId}/seen`, null, { silent: true }).catch(() => {
        // 靜默：已讀只是加值資訊，失敗不打擾使用者
    });
}

/**
 * 沒有權限檢視這張單時，成員與處理歷程兩個區塊整個不顯示（只剩錯誤說明與返回出口）；
 * 載入成功就恢復。區塊是容器所在的 section 卡片，連同標題與篩選列一起收，不留空殼。
 */
function setDetailSectionsDenied(denied) {
    for (const container of [membersContainer, timelineContainer]) {
        container?.closest('section')?.classList.toggle('d-none', denied);
    }
}

async function loadAll() {
    try {
        let [user, detail, members, timeline] = await Promise.all([
            getCurrentUser(),
            api.get(`/api/work-orders/${workOrderId}`, { silent: true }),
            api.get(memberListUrl(), { silent: true }),
            api.get(`/api/work-orders/${workOrderId}/timeline`, { silent: true })
        ]);

        currentUser = user;
        currentDetail = detail;
        // 整單結案後「進行中」篩選只會是空的：自動切到「全部」，讓人看到已結案的成員
        if (detail.closedAt && memberStatusSelect.value === 'active' && !memberAutoSwitched) {
            memberAutoSwitched = true;
            memberStatusSelect.value = 'all';
            memberPage = 1;
            members = await api.get(memberListUrl(), { silent: true });
        }
        markSeenOnce(detail);
        setDetailSectionsDenied(false);

        renderHeader(detail);
        renderReassignedHint(detail, user);
        renderActions(detail, user);
        renderMembers(members);
        renderTimeline(timeline);
    } catch (error) {
        if (error?.status === 403) {
            renderError(headerContainer, { message: error.message || '您沒有權限檢視這張交辦單。' });
            setDetailSectionsDenied(true);
            // 只有 Handle 的原處理人在單被改派後會落到這裡（授權只認目前處理人），而 403 拿不到歷程，
            // 無法確認他是否曾是處理人。出口只給一條：打不開交辦總覽的人回自己的交辦
            // （具 Handle 者附上可能原因），能開的人回交辦清單
            const user = await getCurrentUser().catch(() => null);
            const back = backLinkFor(user);
            const wrap = document.createElement('div');
            wrap.className = 'mt-3';
            if (back.href !== '/work-orders' && hasCapability(user, 'Handle')) {
                const hint = document.createElement('div');
                hint.className = 'lf-hint mb-2 wo-reassign-hint';
                hint.textContent = '若這張單原本交辦給你，可能已改派給其他人。';
                wrap.appendChild(hint);
            }
            wrap.appendChild(linkButton(back.text, appUrl(back.href), { variant: 'outline-secondary' }));
            headerContainer.appendChild(wrap);
            membersContainer.replaceChildren();
            timelineContainer.replaceChildren();
            actionsContainer.replaceChildren();
            badgesEl.replaceChildren();
            return;
        }
        throw error;
    }
}

/** 重新載入完成後要做的事（「清單有更新」提示作廢）；init 登錄 */
const reloadHooks = [];

async function reloadAll() {
    selection.clear();
    await guardedLoadAll();
    for (const hook of reloadHooks) hook();
}

/**
 * 404／403 的返回出口依檢視者而定：打不開交辦總覽（無 Assign／ViewAll）的人回自己的交辦。
 * ui.js 的 backLinkNode 自己會補站台前綴，這裡只給站內路徑，不可先 appUrl。
 */
function backLinkFor(user) {
    const canOpenList = hasCapability(user, 'Assign') || hasCapability(user, 'ViewAll');
    if (!canOpenList && user?.userId > 0) return { href: `/handlers/${user.userId}`, text: '回到我的交辦' };
    return { href: '/work-orders', text: '返回交辦清單' };
}

async function guardedLoadAll() {
    const user = await getCurrentUser().catch(() => null);
    await guardLoad([headerContainer, membersContainer, timelineContainer], loadAll, { backLink: backLinkFor(user) });
}

function init() {
    // 側欄徽章的輪詢發現我的進行中台數或單數與載入時不同，就在頁面頂端提示重新載入（不另開輪詢）
    const listNotice = createListUpdateNotice({ anchor: headerContainer, onReload: () => reloadAll() });
    reloadHooks.push(() => listNotice.rebase());
    renderLoading(headerContainer, 3);
    renderLoading(membersContainer, 5);
    renderLoading(timelineContainer, 3);
    guardedLoadAll();
}

init();
