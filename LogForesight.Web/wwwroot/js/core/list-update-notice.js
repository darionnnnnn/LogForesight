/**
 * 「清單有更新」提示：我的交辦與交辦單頁共用。
 *
 * 不另開輪詢——訂閱 core/badges.js 全站唯一的徽章計時器發出的 `lf:badges` 事件，
 * 比對「我進行中的台數與單數」與頁面載入時記下的數字：不同才在頁面頂端出現提示與「重新載入」，
 * 相同（或更新後又回到原數字）就隱藏。數字取不到（沒有 Handle 能力、徽章載入失敗）時不提示。
 * 頁面自己重新載入清單（回覆成功、按「重新載入」）後呼叫 `rebase()`：下一次事件才是新基準，
 * 否則自己的回覆造成的數字變動會被當成別人的更新。
 */

import { statusNote } from './elements.js';
import { button } from './ui.js';
import { currentBadgeCounts } from './badges.js';

const keyOf = counts => (counts?.myWork == null || counts?.myWorkOrders == null) ? null : `${counts.myWork}:${counts.myWorkOrders}`;

/**
 * @param {object} options
 * @param {Element} options.anchor 提示放在它的前面（頁面頂端）
 * @param {() => void} options.onReload 按「重新載入」時重新載入頁面資料
 * @returns {{ rebase: () => void }}
 */
export function createListUpdateNotice({ anchor, onReload }) {
    const box = document.createElement('div');
    box.id = 'lf-list-update-notice';
    box.className = 'd-flex align-items-center gap-2 flex-wrap mb-3 d-none';
    box.appendChild(statusNote('info', '清單有更新'));
    box.appendChild(button('重新載入', {
        variant: 'outline-primary',
        onClick: () => {
            rebase();
            onReload();
        }
    }));
    anchor.before(box);

    // 頁面剛載入時徽章多半已取過一次：直接以它為基準；還沒取到就等第一次事件
    let baseline = keyOf(currentBadgeCounts()) ?? undefined;

    function rebase() {
        baseline = undefined;
        box.classList.add('d-none');
    }

    window.addEventListener('lf:badges', event => {
        const key = keyOf(event.detail);
        if (key === null) return;
        if (baseline === undefined) {
            baseline = key;
            return;
        }
        box.classList.toggle('d-none', key === baseline);
    });

    return { rebase };
}
