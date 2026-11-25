/**
 * 「不再顯示這個問題…」分流（第 81 輪批次 N，F16-1）：三個問句 → 既有機制與端點的對照表。
 * 純資料與純函式、不碰 DOM、不發請求（發請求的 applyTriage 由呼叫端注入 api），所以「每個答案組合呼叫哪個端點、帶哪些參數」
 * 可以用測試逐格固定（hide-triage.behavior.mjs）。不新增任何儲存：三種機制都是既有的——
 *   靜音＝IssueOwnerAdminService.SetMute；機房結論＝IssueOwnerAdminService.SetConclusion（之後新出現的主機日自動套用）；
 *   規則抑制＝RuleAdminService.AddSuppression（範圍＝主機群組或單一主機）。
 * 與 core/hide-guide.js 的比較表同一組機制，這裡負責「依答案選一個」。
 *
 * 三個問句：
 *   多久（duration）：temporary 暫時（選天數）／permanent 長期；
 *   範圍（scope）：site 全站／group 指定主機群組／host 單一主機；
 *   已交辦的單（orders）：pause 暫停／close 代為結案／keep 不動（這個問題沒有進行中交辦單時不問，用 defaultOrders）。
 * 三問共 2×3×3＝18 個組合；每個組合不是「有對應的機制」就是「不提供，並附原因」，沒有第三種。
 */

export const TRIAGE_DURATIONS = [
    { value: 'temporary', label: '暫時', hint: '選天數，到期自動恢復' },
    { value: 'permanent', label: '長期', hint: '不設期限，要解除時再手動解除' }
];

export const TRIAGE_SCOPES = [
    { value: 'site', label: '全站', hint: '所有主機都不再顯示' },
    { value: 'group', label: '指定主機群組', hint: '只對某個主機群組' },
    { value: 'host', label: '單一主機', hint: '只對一台主機' }
];

export const TRIAGE_ORDERS = [
    { value: 'pause', label: '暫停', hint: '單先擱著，到期自動恢復' },
    { value: 'close', label: '代為結案', hint: '以管理者身分替處理人結案' },
    { value: 'keep', label: '不動', hint: '單與成員維持原狀' }
];

/** 三種機制的中文名稱（後果摘要第一行與比較表用語一致） */
export const TRIAGE_MECHANISM_NAMES = {
    mute: '靜音',
    conclusion: '機房結論',
    suppression: '規則抑制'
};

/** 靜音與抑制的天數範圍（後端 IssueOwnerAdminService.MaxMuteDays 同值） */
export const TRIAGE_MIN_DAYS = 1;
export const TRIAGE_MAX_DAYS = 365;

/** 長期＋全站可下的結論（機房結論四態中分流只開放這兩種：已知雜訊、不處理） */
export const TRIAGE_CONCLUSIONS = [
    { value: 'known_noise', label: '已知雜訊' },
    { value: 'wont_fix', label: '不處理' }
];

const MUTE_PATH = '/api/admin/issue-owners/{source}/{eventId}/mute';
const CONCLUSION_PATH = '/api/admin/issue-owners/{source}/{eventId}/conclusion';
const SUPPRESS_PATH = '/api/rules/{ruleId}/suppressions';
const ADMIN_CLOSE_PATH = '/api/work-orders/{workOrderId}/admin-close';

const REASON_SITE_KEEP = '靜音期間單會自動顯示為暫停，沒有「完全不動」的靜音；請選「暫停」。';
const REASON_PERMANENT_PAUSE = '長期設定沒有到期日，單不會自動恢復；請選「不動」或「代為結案」。';
const REASON_PARTIAL_ORDERS = '只對部分主機設定抑制時，單與成員維持原狀；請選「不動」。';

/**
 * 步驟範本：值為 '$名稱' 的字串會在 buildTriagePlan 時換成實際參數（days、reason、hostGroupId、host、conclusion）；
 * path 裡的 {source}、{eventId}、{ruleId}、{workOrderId} 換成問題與規則（admin-close 對每張進行中的單各發一次，each:'orders'）。
 */
const muteCell = existingOrders => ({
    mechanism: 'mute',
    unavailable: null,
    steps: [{ method: 'PUT', path: MUTE_PATH, body: { days: '$days', until: null, reason: '$reason', existingOrders } }]
});

const conclusionCell = closeOrders => ({
    mechanism: 'conclusion',
    unavailable: null,
    steps: [
        { method: 'PUT', path: CONCLUSION_PATH, body: { status: '$conclusion', note: '$reason', autoApply: true } },
        ...(closeOrders
            ? [{ each: 'orders', method: 'POST', path: ADMIN_CLOSE_PATH, body: { status: '$conclusion', reason: '$reason', blockRedispatch: true, caseIds: null } }]
            : [])
    ]
});

const suppressionCell = (scopeName, permanent) => ({
    mechanism: 'suppression',
    unavailable: null,
    steps: [{
        method: 'POST', path: SUPPRESS_PATH,
        body: {
            scope: scopeName,
            host: scopeName === 'Host' ? '$host' : null,
            hostGroupId: scopeName === 'Group' ? '$hostGroupId' : null,
            reason: '$reason',
            days: permanent ? null : '$days'
        }
    }]
});

const unavailable = (mechanism, reason) => ({ mechanism, unavailable: reason, steps: [] });

/**
 * 對照表：鍵是 `${duration}|${scope}|${orders}`，18 格逐格列出（不用迴圈產生，讓「少一格、多一格」在讀程式與測試時一眼看得出來）。
 * mechanism 即使 unavailable 也標出「這個答案原本會落到哪一種」，供畫面解釋原因。
 */
export const TRIAGE_TABLE = {
    'temporary|site|pause': muteCell('pause'),
    'temporary|site|close': muteCell('close'),
    'temporary|site|keep': unavailable('mute', REASON_SITE_KEEP),
    'temporary|group|pause': unavailable('suppression', REASON_PARTIAL_ORDERS),
    'temporary|group|close': unavailable('suppression', REASON_PARTIAL_ORDERS),
    'temporary|group|keep': suppressionCell('Group', false),
    'temporary|host|pause': unavailable('suppression', REASON_PARTIAL_ORDERS),
    'temporary|host|close': unavailable('suppression', REASON_PARTIAL_ORDERS),
    'temporary|host|keep': suppressionCell('Host', false),
    'permanent|site|pause': unavailable('conclusion', REASON_PERMANENT_PAUSE),
    'permanent|site|close': conclusionCell(true),
    'permanent|site|keep': conclusionCell(false),
    'permanent|group|pause': unavailable('suppression', REASON_PARTIAL_ORDERS),
    'permanent|group|close': unavailable('suppression', REASON_PARTIAL_ORDERS),
    'permanent|group|keep': suppressionCell('Group', true),
    'permanent|host|pause': unavailable('suppression', REASON_PARTIAL_ORDERS),
    'permanent|host|close': unavailable('suppression', REASON_PARTIAL_ORDERS),
    'permanent|host|keep': suppressionCell('Host', true)
};

/** 沒有進行中交辦單時不問第三題：暫時＋全站是靜音（暫停＝沒有單可暫停），其餘都是「不動」 */
export function defaultOrders(duration, scope) {
    return duration === 'temporary' && scope === 'site' ? 'pause' : 'keep';
}

export function cellFor({ duration, scope, orders }) {
    return TRIAGE_TABLE[`${duration}|${scope}|${orders}`] ?? null;
}

/**
 * 這個答案組合能不能用。ctx：{ ruleId, canClose, activeOrders }。
 * 理由一律是可直接顯示給使用者的一句話；available＝true 時 reason 為 null。
 */
export function triageAvailability(answers, ctx) {
    const orders = ctx.activeOrders > 0 ? answers.orders : defaultOrders(answers.duration, answers.scope);
    const cell = cellFor({ duration: answers.duration, scope: answers.scope, orders });
    if (!cell) return { available: false, reason: '沒有對應的做法。', mechanism: null };
    if (answers.scope !== 'site' && !ctx.ruleId) {
        return { available: false, mechanism: 'suppression', reason: '這個問題沒有對應的規則，無法只對群組或單一主機設定；可改選「全站」。' };
    }
    if (cell.unavailable) return { available: false, reason: cell.unavailable, mechanism: cell.mechanism };
    if (orders === 'close' && !ctx.canClose) {
        return { available: false, mechanism: cell.mechanism, reason: '代為結案需要同時具備指派與處理權限。' };
    }
    return { available: true, reason: null, mechanism: cell.mechanism };
}

/** 範圍選項是否可用（沒有對應規則時，群組與單一主機不可用） */
export function scopeAvailability(scope, ctx) {
    if (scope !== 'site' && !ctx.ruleId) {
        return { available: false, reason: '這個問題沒有對應的規則，無法只對群組或單一主機設定。' };
    }
    return { available: true, reason: null };
}

/** 第三題某個選項是否可用（在已選的多久、範圍下）；沒有進行中的單時不問 */
export function ordersAvailability(orders, { duration, scope }, ctx) {
    const cell = cellFor({ duration, scope, orders });
    if (!cell) return { available: false, reason: '沒有對應的做法。' };
    if (cell.unavailable) return { available: false, reason: cell.unavailable };
    if (orders === 'close' && !ctx.canClose) return { available: false, reason: '代為結案需要同時具備指派與處理權限。' };
    return { available: true, reason: null };
}

/**
 * 答案改變後，把已不可用的第三題換成可用的預設（暫時＋全站是暫停，其餘是不動）。
 * 沒有進行中的單時回傳原值（那題不問）。
 */
export function coerceOrders(answers, ctx) {
    if (!(ctx.activeOrders > 0)) return defaultOrders(answers.duration, answers.scope);
    if (ordersAvailability(answers.orders, answers, ctx).available) return answers.orders;
    return defaultOrders(answers.duration, answers.scope);
}

function fillTokens(value, params) {
    if (typeof value === 'string' && value.startsWith('$')) {
        const v = params[value.slice(1)];
        return v === undefined ? null : v;
    }
    return value;
}

function fillPath(path, ctx, extra = {}) {
    return path
        .replace('{source}', encodeURIComponent(ctx.source))
        .replace('{eventId}', String(ctx.eventId))
        .replace('{ruleId}', encodeURIComponent(ctx.ruleId ?? ''))
        .replace('{workOrderId}', String(extra.workOrderId ?? ''));
}

/** 檢查必填與範圍；回傳第一個錯誤的一句話，沒有錯回 null */
export function validateTriage(answers, params, ctx) {
    const availability = triageAvailability(answers, ctx);
    if (!availability.available) return availability.reason;
    if (!String(params.reason ?? '').trim()) return '請填寫原因——日後才知道為什麼不再顯示。';
    if (answers.duration === 'temporary') {
        const days = Number(params.days);
        if (!Number.isInteger(days) || days < TRIAGE_MIN_DAYS || days > TRIAGE_MAX_DAYS) {
            return `天數請填 ${TRIAGE_MIN_DAYS}～${TRIAGE_MAX_DAYS} 之間的整數。`;
        }
    }
    if (answers.scope === 'group' && !params.hostGroupId) return '請選擇主機群組。';
    if (answers.scope === 'host' && !String(params.host ?? '').trim()) return '請選擇主機。';
    if (answers.scope === 'site' && answers.duration === 'permanent'
        && !TRIAGE_CONCLUSIONS.some(c => c.value === params.conclusion)) {
        return '請選擇結論。';
    }
    return null;
}

/** 依答案、參數與問題背景組出要發的請求清單（展開 each:'orders'）。回傳 null＝這個組合不可用。 */
export function buildTriagePlan(answers, params, ctx) {
    const availability = triageAvailability(answers, ctx);
    if (!availability.available) return null;
    const orders = ctx.activeOrders > 0 ? answers.orders : defaultOrders(answers.duration, answers.scope);
    const cell = cellFor({ duration: answers.duration, scope: answers.scope, orders });
    const requests = [];
    for (const step of cell.steps) {
        const body = {};
        for (const [key, value] of Object.entries(step.body)) body[key] = fillTokens(value, params);
        // 數字欄位：天數與群組編號從輸入框來是字串
        if ('days' in body && body.days !== null) body.days = Number(body.days);
        if ('hostGroupId' in body && body.hostGroupId !== null) body.hostGroupId = Number(body.hostGroupId);
        if (step.each === 'orders') {
            for (const id of ctx.orderIds ?? []) {
                requests.push({ method: step.method, path: fillPath(step.path, ctx, { workOrderId: id }), body: { ...body } });
            }
        } else {
            requests.push({ method: step.method, path: fillPath(step.path, ctx), body });
        }
    }
    return { mechanism: cell.mechanism, orders, requests };
}

/** 由實際要發的請求反推機制：與 plan.mechanism、後果摘要所寫的機制比對，確保「說的」與「做的」同一件事 */
export function mechanismOfRequests(requests) {
    const first = requests[0];
    if (!first) return null;
    if (/\/issue-owners\/.+\/mute$/.test(first.path)) return 'mute';
    if (/\/issue-owners\/.+\/conclusion$/.test(first.path)) return 'conclusion';
    if (/\/rules\/.+\/suppressions$/.test(first.path)) return 'suppression';
    return null;
}

function daysText(days) {
    return days === 1 ? '今天一天' : `${days} 天（含今天）`;
}

/**
 * 後果摘要：用白話說這個組合會發生什麼。第一行寫機制名稱，其後每行一件事。
 * 文字只依 plan.mechanism 與 plan.orders 組，不另判斷一次答案——所以摘要寫的機制必然是實際套用的機制。
 * ctx 另需：issueLabel；params 另需：groupName（指定群組時的名稱）。
 */
export function triageSummary(answers, params, ctx, plan) {
    if (!plan) return [];
    const lines = [];
    const label = ctx.issueLabel;
    const orderLine = verb => ctx.activeOrders > 0 ? `這個問題進行中的 ${ctx.activeOrders} 張交辦單：${verb}。` : null;
    if (plan.mechanism === 'mute') {
        const days = Number(params.days);
        lines.push(`做法：${TRIAGE_MECHANISM_NAMES.mute}`);
        lines.push(`「${label}」在全站靜音${Number.isInteger(days) ? ` ${daysText(days)}` : ''}：期間內不列出、不告警、不派工，到期自動恢復。`);
        const orders = orderLine(plan.orders === 'close' ? '代為結案為「不處理」' : '暫停，靜音到期自動恢復');
        if (orders) lines.push(orders);
    } else if (plan.mechanism === 'conclusion') {
        const status = TRIAGE_CONCLUSIONS.find(c => c.value === params.conclusion)?.label ?? '（未選）';
        lines.push(`做法：${TRIAGE_MECHANISM_NAMES.conclusion}`);
        lines.push(`「${label}」標成「${status}」：之後新出現的主機日自動套用、不再派工；已經出現的日子維持原狀。`);
        const orders = orderLine(plan.orders === 'close' ? `代為結案為「${status}」，並停止這些主機之後的自動派工` : '不動');
        if (orders) lines.push(orders);
    } else {
        const scopeText = answers.scope === 'group'
            ? `主機群組「${params.groupName || '（未選）'}」`
            : `主機「${params.host || '（未選）'}」`;
        const days = Number(params.days);
        const period = answers.duration === 'permanent' ? '不設期限' : `${Number.isInteger(days) ? daysText(days) : '（未填天數）'}，到期設定還在但恢復告警與派工`;
        lines.push(`做法：${TRIAGE_MECHANISM_NAMES.suppression}`);
        lines.push(`規則 ${ctx.ruleId} 在${scopeText}抑制（${period}）：該範圍內不告警、不派工。`);
        lines.push('抑制設在規則上：這條規則命中的其他問題，在同一範圍也會一併被抑制。');
        const orders = orderLine('不動');
        if (orders) lines.push(orders);
    }
    return lines;
}

/**
 * 依序發出 plan.requests。api 由呼叫端注入（{ put, post }），失敗即停——已完成的步驟不回滾（各步驟都是獨立、可個別解除的既有設定）。
 * 回傳 { done: 已成功的請求數, results: 各請求的回應, failed: 失敗的請求或 null, error }；不吞例外訊息的顯示，錯誤提示由 api 層負責。
 */
export async function applyTriage(plan, api) {
    const results = [];
    for (const request of plan.requests) {
        try {
            results.push(request.method === 'PUT' ? await api.put(request.path, request.body) : await api.post(request.path, request.body));
        } catch (error) {
            return { done: results.length, results, failed: request, error };
        }
    }
    return { done: results.length, results, failed: null, error: null };
}
