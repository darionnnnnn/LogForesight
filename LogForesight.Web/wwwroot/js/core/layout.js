/**
 * 主版面的共用行為（docs/WEB-SPEC.md §8.5）：側欄選單、目前使用者、登出。
 *
 * 選單依能力顯示，但這**只是顯示層的方便**——真正的防線在後端的 PermissionFilter。
 * 前端藏起來的按鈕擋不住任何人，藏起來只是為了不讓使用者點到必定失敗的功能。
 */

import { api, getCurrentUser, hasCapability } from './api.js';

/** 選單定義：requires 為 null 代表所有已登入者可見 */
const NAV_ITEMS = [
    { href: '/', label: '總覽儀表板', requires: null },
    { href: '/records', label: '問題查詢', requires: null },
    { href: '/permission-changes', label: '權限異動待辦', requires: 'ConfirmPermission' },
    { href: '/reports', label: '報表', requires: null },
    { href: '/runs', label: '執行監控', requires: 'DevMonitor' },
    { href: '/admin/rules', label: '規則維護', requires: 'Maintain' },
    { href: '/admin/users', label: '使用者', requires: 'Maintain' },
    { href: '/admin/hosts', label: '主機', requires: 'Maintain' },
    { href: '/admin/groups', label: '群組與授權', requires: 'Maintain' },
    { href: '/admin/imports', label: 'CSV 匯入', requires: 'Maintain' },
    { href: '/audit', label: '操作紀錄', requires: 'ViewAudit' }
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

    if (user.needsAdminSetup) {
        const { toast } = await import('./ui.js');
        toast('目前尚未指派任何 admin 成員，請至「使用者」頁將管理者加入 admin 群組。', 'warning', 10000);
    }
}

function renderNav(user) {
    const nav = document.getElementById('lf-nav');
    if (!nav) return;

    const currentPath = location.pathname;

    for (const item of NAV_ITEMS) {
        if (item.requires && !hasCapability(user, item.requires)) continue;
        if (user.isServerAdmin && BUSINESS_PAGES.includes(item.href)) continue;

        const link = document.createElement('a');
        link.href = item.href;
        link.className = 'lf-sidebar__link';
        link.textContent = item.label;

        const isActive = item.href === '/'
            ? currentPath === '/'
            : currentPath.startsWith(item.href);
        if (isActive) link.classList.add('is-active');

        nav.appendChild(link);
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
