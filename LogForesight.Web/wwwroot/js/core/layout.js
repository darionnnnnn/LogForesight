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
        items: [
            { href: '/admin/rules', label: '規則維護', icon: 'sliders', requires: 'Maintain' },
            { href: '/admin/hosts', label: '主機', icon: 'hdd-network', requires: 'Maintain' },
            { href: '/admin/users', label: '使用者', icon: 'people', requires: 'Maintain' },
            { href: '/admin/groups', label: '群組與授權', icon: 'diagram-3', requires: 'Maintain' },
            { href: '/admin/imports', label: 'CSV 匯入', icon: 'upload', requires: 'Maintain' }
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

        const heading = document.createElement('div');
        heading.className = 'lf-sidebar__section';
        heading.textContent = section.label;
        nav.appendChild(heading);

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

            nav.appendChild(link);
        }
    }
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

init();
