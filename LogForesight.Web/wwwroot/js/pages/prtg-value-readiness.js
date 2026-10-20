import { api } from '../core/api.js';
import { formatDate, formatNumber } from '../core/format.js';
import { guardLoad, renderSpinner } from '../core/ui.js';

/**
 * 數值資料就緒度卡片（環境探測頁籤）
 */
export function initValueReadiness() {
    const cardEl = document.getElementById('prtg-value-readiness');
    if (!cardEl) return;

    const refreshBtn = document.getElementById('prtg-value-readiness-refresh');
    const backfillBtn = document.getElementById('prtg-value-readiness-backfill');
    const statusEl = document.getElementById('prtg-value-readiness-status');
    const groupsEl = document.getElementById('prtg-value-readiness-groups');
    const bodyEl = document.getElementById('prtg-value-readiness-body');
    const prevBtn = document.getElementById('prtg-value-readiness-prev');
    const nextBtn = document.getElementById('prtg-value-readiness-next');
    const pageInfoEl = document.getElementById('prtg-value-readiness-page-info');

    let page = 1;
    let reason = null;
    let loaded = false;
    let inFlight = null;
    let totalPages = 1;

    function formatCategory(category) {
        if (!category) return '';
        const lower = String(category).toLowerCase();
        switch (lower) {
            case 'cpu': return 'CPU';
            case 'memory': return '記憶體';
            case 'disk': return '磁碟';
            case 'availability': return 'Ping';
            default: return String(category);
        }
    }

    function renderSuccess(data) {
        const population = data.population || 0;
        if (population === 0) {
            statusEl.textContent = '範圍內無可評估 sensor ';
            const mirrorBtn = document.createElement('button');
            mirrorBtn.type = 'button';
            mirrorBtn.className = 'btn btn-link btn-sm p-0 align-baseline';
            mirrorBtn.textContent = '前往鏡像狀態';
            mirrorBtn.addEventListener('click', () => {
                document.querySelector('#prtg-tabs [data-tab="mirror"]')?.click();
            });
            statusEl.appendChild(mirrorBtn);

            groupsEl.replaceChildren();
            bodyEl.replaceChildren();
            if (prevBtn?.parentElement) {
                prevBtn.parentElement.classList.add('d-none');
            }
            backfillBtn?.classList.add('d-none');
            return;
        }

        statusEl.textContent = `母體 ${formatNumber(population)} 顆（評估日 ${formatDate(data.completedDay)}，近 ${data.windowDays} 天）`;
        if (prevBtn?.parentElement) {
            prevBtn.parentElement.classList.remove('d-none');
        }

        // 分組按鈕
        const groupButtons = [];
        const groups = data.groups || [];
        for (const group of groups) {
            const btn = document.createElement('button');
            btn.type = 'button';
            const isReady = group.reason === 'Ready';
            const isCurrent = reason === group.reason;
            btn.className = 'btn btn-sm ' + (isReady ? 'btn-outline-success' : 'btn-outline-secondary') + (isCurrent ? ' active' : '');
            btn.setAttribute('aria-pressed', isCurrent ? 'true' : 'false');
            btn.textContent = `${group.label} ${formatNumber(group.count)}`;
            if ((group.count || 0) === 0) {
                btn.disabled = true;
            }
            btn.addEventListener('click', () => {
                if (reason === group.reason) {
                    reason = null;
                } else {
                    reason = group.reason;
                }
                page = 1;
                load();
            });
            groupButtons.push(btn);
        }
        groupsEl.replaceChildren(...groupButtons);

        // 表格明細
        bodyEl.replaceChildren();
        const rows = data.rows || [];
        if (rows.length === 0) {
            if (reason) {
                const tr = document.createElement('tr');
                const td = document.createElement('td');
                td.colSpan = 6;
                td.className = 'text-center text-muted py-3';
                td.textContent = '此分組沒有 sensor';
                tr.appendChild(td);
                bodyEl.appendChild(tr);
            }
        } else {
            for (const row of rows) {
                const tr = document.createElement('tr');
                const cells = [
                    row.hostName ?? '',
                    row.sensorName ?? '',
                    formatCategory(row.category),
                    `${row.validDays}／${row.requiredDays}`,
                    row.latestValidDay ? formatDate(row.latestValidDay) : '—',
                    row.reasonLabel ?? ''
                ];
                for (const val of cells) {
                    const td = document.createElement('td');
                    td.textContent = String(val);
                    tr.appendChild(td);
                }
                bodyEl.appendChild(tr);
            }
        }

        // 翻頁
        const totalRows = data.totalRows || 0;
        const pageSize = data.pageSize || 1;
        totalPages = Math.max(1, Math.ceil(totalRows / pageSize));
        const currentPage = data.page || 1;
        page = currentPage;

        pageInfoEl.textContent = `第 ${currentPage}／${totalPages} 頁，共 ${formatNumber(totalRows)} 筆`;
        prevBtn.disabled = currentPage <= 1;
        nextBtn.disabled = currentPage >= totalPages;

        // 前往歷史回填
        const insufficientGroup = groups.find(g => g.reason === 'InsufficientDays');
        const hasInsufficient = Boolean(insufficientGroup && insufficientGroup.count > 0);

        let hasRecentValidDay = false;
        const validDays = rows
            .map(r => r.latestValidDay)
            .filter(Boolean);

        if (validDays.length > 0) {
            validDays.sort();
            const earliest = validDays[0];
            const diffMs = Date.now() - new Date(earliest).getTime();
            const diffDays = diffMs / (1000 * 60 * 60 * 24);
            if (diffDays < 28) {
                hasRecentValidDay = true;
            }
        }

        if (hasInsufficient && hasRecentValidDay) {
            backfillBtn?.classList.remove('d-none');
        } else {
            backfillBtn?.classList.add('d-none');
        }
    }

    /** 單次讀取：自己建立 AbortController，讓 guardLoad 的「重試」重跑時也是一次全新的請求 */
    async function fetchAndRender() {
        if (inFlight) {
            inFlight.abort();
            inFlight = null;
        }

        const controller = new AbortController();
        inFlight = controller;

        renderSpinner(statusEl);
        prevBtn.disabled = true;
        nextBtn.disabled = true;

        const url = '/api/prtg/value-readiness?page=' + page + (reason ? '&reason=' + encodeURIComponent(reason) : '');

        try {
            const data = await api.get(url, { signal: controller.signal });
            if (inFlight !== controller) return;
            inFlight = null;
            loaded = true;
            renderSuccess(data);
        } catch (error) {
            if (inFlight !== controller) return;
            inFlight = null;
            if (error?.name === 'AbortError' || controller.signal.aborted) {
                return;
            }
            throw error;
        }
    }

    /** 載入入口：失敗換成錯誤態（含重試），不再自己拼讀取失敗文字 */
    function load() {
        return guardLoad(statusEl, fetchAndRender);
    }

    refreshBtn?.addEventListener('click', () => load());
    backfillBtn?.addEventListener('click', () => {
        document.querySelector('#prtg-tabs [data-tab="selected-backfill"]')?.click();
    });

    prevBtn?.addEventListener('click', () => {
        if (page > 1) {
            page--;
            load();
        }
    });

    nextBtn?.addEventListener('click', () => {
        if (page < totalPages) {
            page++;
            load();
        }
    });

    const probeTab = document.querySelector('#prtg-tabs [data-tab="probe"]');
    probeTab?.addEventListener('click', () => {
        if (!loaded) {
            load();
        }
    });

    if (location.hash === '#probe') {
        load();
    }
}
