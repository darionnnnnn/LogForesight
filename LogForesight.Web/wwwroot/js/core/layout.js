/**
 * 主版面的共用行為（docs/WEB-SPEC.md §8.5）：側欄選單、目前使用者、登出。
 *
 * 選單依能力顯示，但這**只是顯示層的方便**——真正的防線在後端的 PermissionFilter。
 * 前端藏起來的按鈕擋不住任何人，藏起來只是為了不讓使用者點到必定失敗的功能。
 */

import { api, getCurrentUser, hasCapability } from './api.js';
import { appUrl, appPath } from './paths.js';
import { icon } from './ui.js';
import { formatUserName, formatNumber, formatDateTime } from './format.js';
import { initBrandAlign } from './brand-align.js';
import { clearAllDraftsForUser } from './note-editor.js';

/**
 * 選單分組（requires 為 null 代表所有已登入者可見）。分組讓 11 個項目按用途歸類，
 * 避免管理與監控功能平鋪成一長串。空 section（例如一般使用者看不到任何系統管理項）不渲染標題。
 */
const NAV_SECTIONS = [
    {
        label: '監控作業',
        items: [
            // 動態 href：連到「自己」的處理人工作頁——處理人每天的起點，擺在第一位。
            // ServerAdmin 帳號 userId=0，沒有對應的 WebUser，同 BUSINESS_PAGES 的既有邏輯隱藏（hideForServerAdmin）
            { href: user => `/handlers/${user.userId}`, label: '我的交辦', icon: 'inbox', requires: null, hideForServerAdmin: true },
            { href: '/', label: '總覽儀表板', icon: 'speedometer2', requires: null },
            { href: '/records', label: '問題查詢', icon: 'search', requires: null },
            { href: '/work-orders', label: '交辦總覽', icon: 'inbox', requires: ['Assign', 'ViewAll'] },
            { href: '/permission-changes', label: '權限異動檢核', icon: 'clipboard-check', requires: 'ConfirmPermission' },
            { href: '/reports', label: '報表', icon: 'file-earmark-text', requires: null }
        ]
    },
    {
        label: '系統管理',
        // 項目最多的一組，視窗矮時最先被自動收合（見 autoCollapseIfNeeded）
        collapsible: true,
        items: [
            { href: '/admin/rules', label: '規則維護', icon: 'sliders', requires: 'Maintain' },
            { href: '/admin/hosts', label: '主機', icon: 'hdd-network', requires: 'Maintain' },
            { href: '/admin/users', label: '使用者', icon: 'people', requires: 'Maintain' },
            { href: '/admin/groups', label: '群組與授權', icon: 'diagram-3', requires: 'Maintain' },
            // 問題檔案（回饋十八輪批次F 建立「問題負責人」、回饋十九輪批次F 擴充機房結論）：
            // 以 (Source,EventId) 為鍵指派跨主機負責人＋記錄機房結論——放在主機／群組之後，
            // 同屬「誰負責什麼」這條動線
            { href: '/admin/issue-owners', label: '問題檔案', icon: 'people', requires: 'Maintain' },
            { href: '/admin/imports', label: '資料匯入', icon: 'upload', requires: 'Maintain' },
            { href: '/admin/netiq', label: 'NetIQ 維護', icon: 'link-45deg', requires: 'Maintain' },
            { href: '/admin/prtg', label: 'PRTG 維護', icon: 'diagram-3', requires: 'Maintain' },
            { href: '/admin/settings', label: '設定', icon: 'gear', requires: 'Maintain' }
        ]
    },
    {
        label: '系統',
        items: [
            // 排程作業（docs/archive/FEEDBACK-6-PLAN.md §2）：陣列＝任一能力即可見（dev 的執行監控與
            // admin/serverAdmin 的排程設定共用同一頁，serverAdmin 有 Maintain 卻沒有 DevMonitor，
            // 沒有這個入口就搆不到全新環境的排程初始設定）
            { href: '/runs', label: '排程作業', icon: 'activity', requires: ['DevMonitor', 'Maintain'] },
            { href: '/audit', label: '操作紀錄', icon: 'journal-text', requires: 'ViewAudit' },
            // 操作說明書（docs/archive/FEEDBACK-15-PLAN.md 批次E）：刻意放側欄最下方——
            // 僅 Maintain 顯示，不是日常監控/管理動線的一部分，擺最後才不會擠掉更常用的項目
            { href: '/help/manual', label: '操作說明書', icon: 'info-circle', requires: 'Maintain' }
        ]
    }
];

/**
 * serverAdmin 只有維護與稽核能力，沒有業務資料檢視能力——
 * 對它隱藏業務頁面，避免點進去看到一片空白（那不是壞掉，是刻意的最小授權）。
 */
const BUSINESS_PAGES = ['/', '/records', '/reports'];

async function init() {
    let user;
    try {
        user = await getCurrentUser();
    } catch {
        return;   // 401 已由 api.js 導向登入頁
    }

    renderNav(user);
    renderCurrentUser(user);
    bindLogout(user);
    initHelpPopovers();
    renderSetupReturnBanner();
    refreshRunActivity();   // 執行中告示：取得使用者成功之後才開始（未登入時上面已提前返回）
    loadHealthBanner(user);

    if (user.needsAdminSetup) {
        const { toast } = await import('./ui.js');
        toast('目前尚未指派任何 admin 成員，請至「使用者」頁將管理者加入 admin 群組。', 'warning', 10000);
    }
}

function renderNav(user) {
    const nav = document.getElementById('lf-nav');
    if (!nav) return;

    const currentPath = appPath();

    for (const section of NAV_SECTIONS) {
        // href 可以是函式（依目前使用者算出連結，例如「我的交辦」連到自己的處理人頁）——
        // 先一次解析好存成 resolvedHref，下面的可見性判斷與實際渲染都用同一個值，
        // 不必對同一個 item 呼叫函式兩次
        const resolved = section.items.map(item => ({
            ...item,
            resolvedHref: typeof item.href === 'function' ? item.href(user) : item.href
        }));
        const visible = resolved.filter(item => {
            // requires 可以是單一能力字串，也可以是能力陣列（任一命中即可見，見上方「排程作業」）
            if (item.requires) {
                const needed = Array.isArray(item.requires) ? item.requires : [item.requires];
                if (!needed.some(cap => hasCapability(user, cap))) return false;
            }
            if (user.isServerAdmin && (BUSINESS_PAGES.includes(item.resolvedHref) || item.hideForServerAdmin)) return false;
            return true;
        });
        if (visible.length === 0) continue;   // 整組不可見就連標題一起省略

        const itemsWrap = document.createElement('div');
        itemsWrap.className = 'lf-sidebar__section-items';

        if (section.collapsible) {
            const toggle = document.createElement('button');
            toggle.type = 'button';
            toggle.className = 'lf-sidebar__section lf-sidebar__section--toggle';
            toggle.dataset.section = section.label;

            const label = document.createElement('span');
            label.textContent = section.label;
            toggle.append(label, icon('chevron-down'));

            nav.appendChild(toggle);
            bindSectionToggle(toggle, itemsWrap, section.label);
        } else {
            const heading = document.createElement('div');
            heading.className = 'lf-sidebar__section';
            heading.textContent = section.label;
            nav.appendChild(heading);
        }

        for (const item of visible) {
            const link = document.createElement('a');
            link.href = appUrl(item.resolvedHref);
            link.className = 'lf-sidebar__link';
            link.appendChild(icon(item.icon));

            const label = document.createElement('span');
            label.textContent = item.label;
            link.appendChild(label);

            const isActive = item.resolvedHref === '/'
                ? currentPath === '/'
                : currentPath.startsWith(item.resolvedHref);
            if (isActive) link.classList.add('is-active');

            // 「我的交辦」未結案數（體檢 M7）：一般使用者是每天用得最頻繁的角色，
            // 但過去唯一知道自己有工作的方法是主動點進去——首頁 KPI 顯示的是全站待辦，
            // 指派之後日狀態已推進成處理中，他手上有幾件在畫面上完全看不出來
            if (item.label === '我的交辦' && user?.userId > 0) {
                link.dataset.badgeSource = 'my-work';
            }

            itemsWrap.appendChild(link);
        }

        nav.appendChild(itemsWrap);
    }

    loadMyWorkBadge(user);

    if (document.querySelector('.lf-sidebar__section--toggle')) {
        autoCollapseIfNeeded();

        // 兩條路徑都掛，不是二選一：ResizeObserver 抓得到側欄本身框變化的所有成因
        // （字級偏好切換、瀏覽器縮放，不只是拉視窗），window resize 則是最基本的保底，
        // 兩者都很便宜，沒有理由只留一條
        const debounced = debounce(autoCollapseIfNeeded, 150);
        const sidebar = document.querySelector('.lf-sidebar');
        if (sidebar && window.ResizeObserver) {
            new ResizeObserver(debounced).observe(sidebar);
        }
        window.addEventListener('resize', debounced);
    }
}

/**
 * 分組收合／展開（目前只有「系統管理」啟用，見 NAV_SECTIONS 的 collapsible 旗標）。
 * 使用者手動點過的狀態記 localStorage、跨頁保留；沒手動點過時交給 autoCollapseIfNeeded
 * 依視窗高度決定，兩者用同一個 class 切換，互不衝突（自動收合不寫 localStorage，
 * 才不會讓「這次視窗矮」的暫時判斷變成往後永遠收合）。
 */
function sectionStorageKey(label) {
    return `lf.sidebar.collapsed.${label}`;
}

/**
 * 側欄「我的交辦」的進行中徽章（體檢 M7）。
 *
 * 資料來自單一聚合端點 `handlers/me/badge`（`HandlerSummaryDto`）——一次拿齊單數、台數、
 * 逾期與未回覆，不必為了一顆徽章打多支 API。ServerAdmin 後端直接回全 0，這裡不另外判斷。
 * **失敗完全靜默**：徽章是加值資訊，載不到就不顯示；為了一個數字讓側欄出現錯誤提示
 * （或更糟：擋住選單渲染）是本末倒置。
 */
async function loadMyWorkBadge(user) {
    const link = document.querySelector('[data-badge-source="my-work"]');
    if (!link || !user?.userId) return;

    // 回饋十三輪 A4：沒有 Handle 能力的人（如只有 ViewAll 的主管角色）不載入徽章——
    // 「我的交辦」頁本身仍可進（全角色可看任何人的處理人頁），但未結案「數字」是
    // 處理人視角的待辦提醒，對無法動手處理的角色只是噪音
    if (!hasCapability(user, 'Handle')) return;

    try {
        const summary = await api.get('/api/handlers/me/badge', { silent: true });
        // 數字取「手上進行中的主機數」：一張單可能含多台，台數才是實際還有多少事要處理
        const count = summary.activeMembers ?? 0;
        if (count <= 0) return;

        const badge = document.createElement('span');
        badge.className = 'lf-sidebar__badge';
        badge.textContent = String(count);
        badge.title = `進行中交辦單 ${summary.activeWorkOrders ?? 0} 張、${count} 台；`
            + `逾期 ${summary.overdueMembers ?? 0} 台、未回覆 ${summary.unrepliedWorkOrders ?? 0} 張`;
        link.appendChild(badge);

        if ((summary.overdueMembers ?? 0) > 0) badge.classList.add('lf-sidebar__badge--overdue');
    } catch {
        // 靜默：見函式註解
    }
}

function bindSectionToggle(toggle, itemsWrap, label) {
    // aria-controls／aria-expanded（體檢 L9）：過去只在點擊後才設 aria-expanded，
    // 預設展開時屬性根本不存在——螢幕閱讀器讀到的是一顆沒有狀態的按鈕。
    // 這裡先給明確的初始值，並把按鈕與它控制的區塊關聯起來。
    if (!itemsWrap.id) itemsWrap.id = `lf-nav-section-${Math.random().toString(36).slice(2, 8)}`;
    toggle.setAttribute('aria-controls', itemsWrap.id);
    toggle.setAttribute('aria-expanded', 'true');

    const manual = localStorage.getItem(sectionStorageKey(label));
    if (manual === 'true') setSectionCollapsed(toggle, itemsWrap, true);

    toggle.addEventListener('click', () => {
        // 以 class 而非 aria-expanded 屬性判斷目前狀態——預設（未收合）時屬性根本不存在，
        // 用屬性的有無反推狀態容易搞反第一次點擊的方向
        const collapsed = itemsWrap.classList.contains('is-collapsed');
        setSectionCollapsed(toggle, itemsWrap, !collapsed);
        localStorage.setItem(sectionStorageKey(label), String(!collapsed));
    });
}

function setSectionCollapsed(toggle, itemsWrap, collapsed) {
    toggle.setAttribute('aria-expanded', String(!collapsed));
    itemsWrap.classList.toggle('is-collapsed', collapsed);
}

/**
 * 視窗矮到選單放不下時，自動收合「系統管理」（目前唯一可收合的分組）——
 * 側欄現在貼齊視窗高度（見 site.css），選單项目一多就會被裁切、只能内部捲動，
 * 這裡讓最常不需要天天點的一組先讓路，而不是預設就要捲動才看得到「報表」在下面。
 * 只在使用者**沒有手動設定過**這組的展開狀態時才自動介入，不覆蓋使用者的明確選擇。
 */
function autoCollapseIfNeeded() {
    const nav = document.getElementById('lf-nav');
    const toggle = document.querySelector('.lf-sidebar__section--toggle');
    if (!nav || !toggle) return;

    const label = toggle.dataset.section;
    if (localStorage.getItem(sectionStorageKey(label)) !== null) return;   // 使用者已手動設定過，不介入

    const itemsWrap = toggle.nextElementSibling;
    const overflowing = nav.scrollHeight > nav.clientHeight;

    // 已經是收合狀態就不用再判斷是否要展開回去——那是使用者要手動做的事，
    // 自動邏輯只負責「不夠高就收」，不負責「夠高了就展開」（避免視窗邊緣抖動時反覆跳動）
    if (overflowing && itemsWrap && !itemsWrap.classList.contains('is-collapsed')) {
        setSectionCollapsed(toggle, itemsWrap, true);
    }
}

function debounce(fn, delayMs) {
    let timer;
    return (...args) => {
        clearTimeout(timer);
        timer = setTimeout(() => fn(...args), delayMs);
    };
}

/**
 * 統一初始化頁面上的說明 popover（§8.6）——把大段 alert 文字收進 popover，
 * 各頁只要在 cshtml 標 data-bs-toggle="popover" 即可，不需自己寫 inline script。
 */
function initHelpPopovers() {
    const triggers = document.querySelectorAll('[data-bs-toggle="popover"]');
    for (const el of triggers) {
        // hover 補 focus（原本只有 focus，需要點擊/Tab 到才看得到）——docs/archive/FEEDBACK-5-PLAN.md §6：
        // 常駐說明文字收斂進 icon 後，滑鼠滑過就要能看到，不能還要求使用者先點一下
        new bootstrap.Popover(el, { trigger: 'hover focus', html: false });
    }
}

function renderCurrentUser(user) {
    const el = document.getElementById('lf-current-user');
    if (!el) return;

    // 「顯示名稱(帳號)」是全站唯一的使用者顯示格式（docs/archive/FEEDBACK-10-PLAN.md §2）——
    // 這裡原本只顯示 displayName、把完整格式藏在 title 裡，是全站唯一的例外，已對齊。
    // 側欄寬度有限，過長由既有的 text-truncate 收尾，title 保留同值供滑過查看
    el.textContent = formatUserName(user.displayName, user.account);
    el.title = el.textContent;
}

function bindLogout(user) {
    const button = document.getElementById('lf-logout');
    if (!button) return;

    button.addEventListener('click', async () => {
        button.disabled = true;
        try {
            await api.post('/api/auth/logout');
            // 主動登出才清草稿；工作階段逾時被導回登入頁不清（重新登入後要能還原）
            clearAllDraftsForUser(user.userId);
        } finally {
            location.href = appUrl('/login');
        }
    });
}

/**
 * 字級偏好（小／中／大）：套在 <html> 的 data-font-scale 上，乘進根字級的縮放倍率
 * （見 site.css）。與登入狀態無關，所以在 init 的 await 之前先跑，避免等 /api/auth/me
 * 回來才縮放造成閃動。中＝預設，不覆寫倍率。
 */
function initFontScale() {
    applyFontScale(localStorage.getItem('lf.fontScale') || 'medium');

    const group = document.getElementById('lf-font-scale');
    if (!group) return;

    group.addEventListener('click', event => {
        const button = event.target.closest('[data-scale]');
        if (!button) return;
        applyFontScale(button.dataset.scale);
        localStorage.setItem('lf.fontScale', button.dataset.scale);
    });
}

function applyFontScale(scale) {
    document.documentElement.dataset.fontScale = scale;
    for (const button of document.querySelectorAll('#lf-font-scale [data-scale]')) {
        button.classList.toggle('active', button.dataset.scale === scale);
    }
}

/**
 * 回到啟動精靈提示列（回饋十八輪批次H）：從精靈頁「前往設定」點過來時（?from=setup），
 * 在頁頂顯示一條可關閉的提示，點擊回 /setup。集中在 layout.js 而不是逐頁各寫一份——
 * 每個目標頁（設定／使用者／群組／NetIQ／排程）都可能是精靈的跳轉目的地，
 * 這裡是所有頁面共同載入的入口，單點處理不必修改每一個目標頁。
 *
 * 清掉 URL 上的 from 參數（history.replaceState）：同 login.js 的 returnUrl 處理慣例——
 * 重新整理或分享這個網址不該一直帶著「你是從精靈來的」這個一次性狀態。
 */
function renderSetupReturnBanner() {
    const params = new URLSearchParams(location.search);
    if (params.get('from') !== 'setup') return;

    params.delete('from');
    const cleanQuery = params.toString();
    history.replaceState(null, '', location.pathname + (cleanQuery ? `?${cleanQuery}` : '') + location.hash);

    const banner = document.getElementById('lf-setup-return-banner');
    if (!banner) return;

    banner.replaceChildren();
    banner.className = 'alert alert-info d-flex align-items-center justify-content-between mb-0 rounded-0 lf-no-print';

    const text = document.createElement('span');
    text.textContent = '設定完成後，可以回到啟動精靈繼續下一步。';
    banner.appendChild(text);

    const actions = document.createElement('div');
    actions.className = 'd-flex align-items-center gap-2';

    const backLink = document.createElement('a');
    backLink.href = appUrl('/setup');
    backLink.className = 'btn btn-sm btn-primary';
    backLink.textContent = '返回啟動精靈';
    actions.appendChild(backLink);

    const dismiss = document.createElement('button');
    dismiss.type = 'button';
    dismiss.className = 'btn-close';
    dismiss.setAttribute('aria-label', '關閉');
    dismiss.addEventListener('click', () => banner.classList.add('d-none'));
    actions.appendChild(dismiss);

    banner.appendChild(actions);
}

// ── 全站執行中告示（回饋四十五輪批次A3）──────────────────────────────────────

/**
 * 告示的廣播事件名。頁面模組（例如主機詳情要停用「指定主機更新」）訂閱這個事件即可，
 * 不必各自輪詢 /api/run-activity——多一個輪詢就是多一份在慢的時候打站台的負擔。
 * 訂閱端的 event.detail 是整個 activity 物件（沒在跑或取不到時為 null）。
 */
const RUN_ACTIVITY_EVENT = 'lf:run-activity';

/**
 * 執行中告示（原 dashboard.js，回饋四十五輪批次A3 搬到共用版型）。
 *
 * 分析與網站跑在同一個行程，一跑就是數小時，期間**整站**回應變慢——只有儀表板看得到
 * 原因等於沒有配套：使用者可能正停在問題查詢或報表頁，看到的只是「這頁壞了」。
 *
 * 自我重新排程而不是固定 setInterval：下一次的間隔要看這一次的狀態（執行中 30 秒、
 * 閒置 60 秒）。閒置只降頻、**不停掉**——停掉的話停在同一頁不動的使用者永遠等不到
 * 下一次執行的告示（原儀表板版本會停，是因為當時只有那一頁在看）。
 */
async function refreshRunActivity() {
    let activity = null;
    try {
        activity = await api.get('/api/run-activity', { silent: true });
    } catch {
        // 純加值資訊，失敗就當作沒在跑，畫面上不留錯誤訊息；
        // 刻意不做任何停掉輪詢的事——偶發失敗不代表執行結束。
        activity = null;
    }

    renderRunActivity(activity);

    // 保存最後一次狀態：事件只有「發生的當下」在監聽的人收得到。頁面模組雖與本檔同為
    // deferred module、在第一次回應前就註冊好監聽（所以正常動線靠事件就對），
    // 但**之後才載入**的模組（動態 import、面板延後初始化）錯過了那次事件、又要等下一輪
    // 輪詢才校正——它們讀這個值就不必停在初值。
    window.lfRunActivity = activity;
    window.dispatchEvent(new CustomEvent(RUN_ACTIVITY_EVENT, { detail: activity }));

    setTimeout(refreshRunActivity, activity?.isRunning ? 30000 : 60000);
}

/**
 * 排程資料過期告示：只有能處理它的人（Maintain／DevMonitor）才查，進頁查一次、不輪詢。
 * 過期且尚未確認靜音時顯示一條；其餘情況（含呼叫失敗）一律清空，容器零高度。
 */
async function loadHealthBanner(user) {
    const container = document.getElementById('lf-health-banner');
    if (!container) return;
    if (!hasCapability(user, 'Maintain') && !hasCapability(user, 'DevMonitor')) return;

    let freshness;
    try {
        freshness = await api.get('/api/health/freshness', { silent: true });
    } catch {
        container.replaceChildren();
        return;
    }
    if (!freshness?.stale || freshness.acked) {
        container.replaceChildren();
        return;
    }

    const bar = document.createElement('div');
    bar.className = 'alert alert-warning d-flex flex-wrap align-items-center gap-2 py-2 mb-3';
    bar.setAttribute('role', 'status');

    const text = document.createElement('span');
    const lastText = freshness.lastSuccessAt ? formatDateTime(freshness.lastSuccessAt) : '近 14 天沒有紀錄';
    text.textContent = `排程資料已超過 48 小時沒有成功更新（最近一次成功：${lastText}）。`;

    const runsLink = document.createElement('a');
    runsLink.href = appUrl('/runs');
    runsLink.textContent = '查看排程作業';

    bar.append(text, runsLink);

    // 確認靜音在設定頁、需要 Maintain：只有 DevMonitor 的人看得到告示，但不給他進不去的連結
    if (hasCapability(user, 'Maintain')) {
        const ackLink = document.createElement('a');
        ackLink.href = appUrl('/admin/settings#health');
        ackLink.textContent = '確認並靜音';
        bar.appendChild(ackLink);
    }
    container.replaceChildren(bar);
}

/**
 * 逐日同步待處理的告示條；沒有待處理時回 null。
 *
 * 這是與分析執行無關的背景工作，所以不掛 spinner、也不受執行狀態影響——沒有在執行時
 * 一樣要看得到，否則使用者只會覺得「儀表板數字怎麼還沒變」。
 */
function caseDaySyncBar(activity) {
    const pending = activity?.caseDaySyncPending ?? 0;
    if (pending <= 0) return null;

    const bar = document.createElement('div');
    bar.className = 'alert alert-secondary d-flex align-items-center gap-2 py-2 mb-3';
    bar.setAttribute('role', 'status');
    bar.setAttribute('aria-live', 'polite');

    const text = document.createElement('span');
    text.textContent = `案件逐日同步：待處理 ${formatNumber(pending)} 件，背景處理中，儀表板與報表數字稍後會更新。`;
    bar.appendChild(text);
    return bar;
}

/**
 * 告示的畫面：分析執行中一條、逐日同步待處理一條（兩條都在時分析那條在上），
 * 兩者皆無則一律清空（容器不帶 margin/padding，清空即零高度）
 */
function renderRunActivity(activity) {
    const container = document.getElementById('lf-run-activity-banner');
    if (!container) return;

    const syncBar = caseDaySyncBar(activity);

    if (!activity?.isRunning) {
        container.replaceChildren(...(syncBar ? [syncBar] : []));
        return;
    }

    const bar = document.createElement('div');
    bar.className = 'alert alert-info d-flex align-items-center gap-2 py-2 mb-3';
    bar.setAttribute('role', 'status');       // 進行中狀態用 status（polite），不是 alert——
    bar.setAttribute('aria-live', 'polite');  // 這不是需要打斷讀屏的緊急訊息

    const spinner = document.createElement('span');
    spinner.className = 'spinner-border spinner-border-sm flex-shrink-0';
    spinner.setAttribute('aria-hidden', 'true');
    bar.appendChild(spinner);

    // 有分母才講「第 N/M」——total=0 代表還在掃描/清理階段，這時報進度是假的
    const progressText = activity.total > 0
        ? `分析進行中（第 ${formatNumber(activity.done)}／${formatNumber(activity.total)} ${activity.unitText || ''}）`
        : '分析進行中';
    // 觸發者只有後端給得出來時才講（排程自動跑時是「排程」，有人按的話是那個人）
    const triggerText = activity.triggerText ? `，由${activity.triggerText}觸發` : '';

    const text = document.createElement('span');
    text.textContent = `${progressText}${triggerText}，畫面回應可能較慢。資料仍是完整的，分析完成後會自動恢復。`;
    bar.appendChild(text);

    container.replaceChildren(...(syncBar ? [bar, syncBar] : [bar]));
}

initFontScale();
initBrandAlign();
init();
