/**
 * 主版面的共用行為（docs/WEB-SPEC.md §8.5）：側欄選單、目前使用者、登出。
 *
 * 選單依能力顯示，但這**只是顯示層的方便**——真正的防線在後端的 PermissionFilter。
 * 前端藏起來的按鈕擋不住任何人，藏起來只是為了不讓使用者點到必定失敗的功能。
 */

import { api, getCurrentUser, hasCapability } from './api.js';
import { icon } from './ui.js';

/**
 * 選單分組（requires 為 null 代表所有已登入者可見）。分組讓 11 個項目按用途歸類，
 * 避免管理與監控功能平鋪成一長串。空 section（例如一般使用者看不到任何系統管理項）不渲染標題。
 */
const NAV_SECTIONS = [
    {
        label: '監控作業',
        items: [
            { href: '/', label: '總覽儀表板', icon: 'speedometer2', requires: null },
            { href: '/records', label: '問題查詢', icon: 'search', requires: null },
            { href: '/permission-changes', label: '權限異動待辦', icon: 'clipboard-check', requires: 'ConfirmPermission' },
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
            { href: '/admin/imports', label: '資料匯入', icon: 'upload', requires: 'Maintain' },
            { href: '/admin/netiq', label: 'NetIQ 維護', icon: 'link-45deg', requires: 'Maintain' },
            { href: '/admin/settings', label: '設定', icon: 'gear', requires: 'Maintain' }
        ]
    },
    {
        label: '系統',
        items: [
            { href: '/runs', label: '執行監控', icon: 'activity', requires: 'DevMonitor' },
            { href: '/audit', label: '操作紀錄', icon: 'journal-text', requires: 'ViewAudit' }
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
    bindLogout();
    initHelpPopovers();

    if (user.needsAdminSetup) {
        const { toast } = await import('./ui.js');
        toast('目前尚未指派任何 admin 成員，請至「使用者」頁將管理者加入 admin 群組。', 'warning', 10000);
    }
}

function renderNav(user) {
    const nav = document.getElementById('lf-nav');
    if (!nav) return;

    const currentPath = location.pathname;

    for (const section of NAV_SECTIONS) {
        const visible = section.items.filter(item => {
            if (item.requires && !hasCapability(user, item.requires)) return false;
            if (user.isServerAdmin && BUSINESS_PAGES.includes(item.href)) return false;
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
            link.href = item.href;
            link.className = 'lf-sidebar__link';
            link.appendChild(icon(item.icon));

            const label = document.createElement('span');
            label.textContent = item.label;
            link.appendChild(label);

            const isActive = item.href === '/'
                ? currentPath === '/'
                : currentPath.startsWith(item.href);
            if (isActive) link.classList.add('is-active');

            itemsWrap.appendChild(link);
        }

        nav.appendChild(itemsWrap);
    }

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

function bindSectionToggle(toggle, itemsWrap, label) {
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
        new bootstrap.Popover(el, { trigger: 'focus', html: false });
    }
}

function renderCurrentUser(user) {
    const el = document.getElementById('lf-current-user');
    if (!el) return;

    el.textContent = user.displayName || user.account;
    el.title = user.account;
}

function bindLogout() {
    const button = document.getElementById('lf-logout');
    if (!button) return;

    button.addEventListener('click', async () => {
        button.disabled = true;
        try {
            await api.post('/api/auth/logout');
        } finally {
            location.href = '/login';
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

initFontScale();
init();
