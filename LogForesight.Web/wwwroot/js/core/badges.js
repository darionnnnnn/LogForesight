/**
 * 側欄徽章的共用刷新（docs/WEB-SPEC.md §9.4a）：全站唯一的徽章計時器。
 *
 * 兩顆徽章：
 * - 「我的交辦」：手上進行中的主機數（資料來自單一聚合端點 `handlers/me/badge`，需 Handle 能力）；
 * - 「交辦總覽」：有未讀回覆的單數（`work-orders/unread-count`，需 Assign 或 ViewAll；0 不顯示）。
 *
 * 每 60 秒刷新一次；分頁隱藏時不打 API，回到前景（visibilitychange）立即刷新一次。
 * 各頁**不得另開計時器**——需要跟著徽章更新的頁面訂閱 `lf:badges` 事件
 * （`detail: { myWork, unread }`，取不到的一項維持上次已知值，從未取到則為 null）。
 * **失敗完全靜默**：徽章是加值資訊，載不到就維持原樣；為了一個數字讓側欄出現錯誤提示是本末倒置。
 */

import { api, hasCapability } from './api.js';

const REFRESH_INTERVAL_MS = 60000;

/** 最近一次成功取得的數字；失敗時沿用，避免徽章因單次失敗而閃爍或消失 */
const lastKnown = { myWork: null, unread: null };

let started = false;
let refreshing = false;

/** 啟動徽章輪詢（整個頁面生命週期只會生效一次；重複呼叫直接略過） */
export function startBadgePolling(user) {
    if (started) return;
    started = true;

    const refresh = () => refreshBadges(user);
    setInterval(() => {
        if (!document.hidden) refresh();
    }, REFRESH_INTERVAL_MS);
    document.addEventListener('visibilitychange', () => {
        if (!document.hidden) refresh();
    });
    refresh();
}

async function refreshBadges(user) {
    // 上一輪還沒回來就不再疊一輪（慢站台時避免請求堆積）
    if (refreshing) return;
    refreshing = true;
    try {
        const [myWork, unread] = await Promise.all([loadMyWorkBadge(user), loadUnreadBadge(user)]);
        if (myWork !== undefined) lastKnown.myWork = myWork;
        if (unread !== undefined) lastKnown.unread = unread;
        window.dispatchEvent(new CustomEvent('lf:badges', {
            detail: { myWork: lastKnown.myWork, unread: lastKnown.unread }
        }));
    } finally {
        refreshing = false;
    }
}

/**
 * 「我的交辦」徽章。回傳顯示的台數；不適用（無連結、無 Handle）回 null；取數失敗回 undefined（維持原樣）。
 *
 * 回饋十三輪 A4：沒有 Handle 能力的人（如只有 ViewAll 的主管角色）不載入徽章——
 * 「我的交辦」頁本身仍可進，但未結案「數字」是處理人視角的待辦提醒，對無法動手處理的角色只是噪音。
 */
async function loadMyWorkBadge(user) {
    const link = document.querySelector('[data-badge-source="my-work"]');
    if (!link || !user?.userId) return null;
    if (!hasCapability(user, 'Handle')) return null;

    try {
        const summary = await api.get('/api/handlers/me/badge', { silent: true });
        // 數字取「手上進行中的主機數」：一張單可能含多台，台數才是實際還有多少事要處理
        const count = summary.activeMembers ?? 0;
        const title = `進行中交辦單 ${summary.activeWorkOrders ?? 0} 張、${count} 台；`
            + `逾期 ${summary.overdueMembers ?? 0} 台、未回覆 ${summary.unrepliedWorkOrders ?? 0} 張`;
        setBadge(link, count, title, (summary.overdueMembers ?? 0) > 0);
        return count;
    } catch {
        return undefined;
    }
}

/** 「交辦總覽」未讀回覆徽章。回傳未讀單數；不適用回 null；取數失敗回 undefined（維持原樣） */
async function loadUnreadBadge(user) {
    const link = document.querySelector('[data-badge-source="unread-replies"]');
    if (!link) return null;
    if (!hasCapability(user, 'Assign') && !hasCapability(user, 'ViewAll')) return null;

    try {
        const result = await api.get('/api/work-orders/unread-count', { silent: true });
        const count = result?.count ?? 0;
        setBadge(link, count, `${count} 張交辦單有處理人的新回覆`, false);
        return count;
    } catch {
        return undefined;
    }
}

/** 就地更新徽章（只維持一顆）；數字為 0 則移除 */
function setBadge(link, count, title, overdue) {
    let badge = link.querySelector('.lf-sidebar__badge');
    if (count <= 0) {
        badge?.remove();
        return;
    }
    if (!badge) {
        badge = document.createElement('span');
        badge.className = 'lf-sidebar__badge';
        link.appendChild(badge);
    }
    badge.textContent = String(count);
    badge.title = title;
    badge.classList.toggle('lf-sidebar__badge--overdue', overdue);
}
