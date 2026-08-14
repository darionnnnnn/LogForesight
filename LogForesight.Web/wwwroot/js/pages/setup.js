/**
 * 首次啟動精靈（回饋十八輪批次H）：checklist 即精靈骨架。
 *
 * 混合制：「完成」由後端依系統狀態自動判定（見 SetupReadinessService），畫面只讀不算；
 * 「跳過」由使用者手動決定、隨時可逆。每次載入都重新拉 /api/admin/setup/status——
 * 從其他頁按「前往設定」做完事回來，精靈頁本身沒有跨頁狀態同步的負擔，重新整理就拿到最新判定。
 */

import { api } from '../core/api.js';
import { toast, withBusy, guardLoad } from '../core/ui.js';
import { statusBadge } from '../core/format.js';

const stepsContainer = document.getElementById('setup-steps');
let status = null;

async function load() {
    status = await api.get('/api/admin/setup/status');
    render();

    // 深連結（§ URL 反映狀態）：重整或分享網址時捲到同一步
    const hashId = location.hash.replace(/^#/, '');
    if (hashId) {
        const target = document.querySelector(`[data-step-row="${hashId}"]`);
        target?.scrollIntoView({ block: 'center', behavior: 'smooth' });
    }
}

function render() {
    renderProgress();
    renderSteps();
    renderSettledCard();
}

function renderProgress() {
    const total = status.steps.length;
    const done = status.steps.filter(s => s.done).length;
    const skipped = status.steps.filter(s => s.skipped && !s.done).length;
    const settled = done + skipped;

    document.getElementById('setup-progress-text').textContent = `${settled} / ${total} 步已完成`;
    document.getElementById('setup-progress-detail').textContent =
        skipped > 0 ? `已完成 ${done} 步，跳過 ${skipped} 步` : `已完成 ${done} 步`;

    const bar = document.getElementById('setup-progress-bar');
    bar.style.width = `${total === 0 ? 0 : Math.round((settled / total) * 100)}%`;
    bar.classList.toggle('bg-success', settled === total && total > 0);
}

// 三態共用一份定義（回饋十八輪體檢輪修正）：狀態字典 dot 顏色與 lf-badge 變體都從同一個
// stepState() 結果查表，兩者不會各自判斷而彼此漂移。dot 顏色走 site.css tokens（終檢輪修正：
// 原本硬編字面值，site.css 換色時這裡會漂移）。
const STEP_STATE_META = {
    done: { dot: 'var(--lf-success)', badgeVariant: 'success', badgeText: '已完成' },
    skipped: { dot: 'var(--lf-gray-400)', badgeVariant: 'secondary', badgeText: '已跳過' },
    pending: { dot: 'var(--lf-gray-200)', badgeVariant: 'warning', badgeText: '待設定' }
};

function renderSteps() {
    stepsContainer.replaceChildren();

    const list = document.createElement('div');
    list.className = 'list-group list-group-flush';

    status.steps.forEach((step, index) => {
        const row = document.createElement('div');
        row.className = 'list-group-item py-3';
        row.dataset.stepRow = step.id;
        row.id = `step-${step.id}`;

        const top = document.createElement('div');
        top.className = 'd-flex align-items-start gap-3';

        const meta = STEP_STATE_META[stepState(step)];

        const dot = document.createElement('span');
        dot.className = 'rounded-circle flex-shrink-0 mt-1';
        dot.style.width = '14px';
        dot.style.height = '14px';
        dot.style.display = 'inline-block';
        dot.style.background = meta.dot;

        const body = document.createElement('div');
        body.className = 'flex-grow-1';

        const titleRow = document.createElement('div');
        titleRow.className = 'd-flex align-items-center gap-2 flex-wrap';

        const title = document.createElement('span');
        title.className = 'fw-semibold';
        title.textContent = `${index + 1}. ${step.title}`;
        titleRow.appendChild(title);

        titleRow.appendChild(statusBadge(meta.badgeText, meta.badgeVariant));
        body.appendChild(titleRow);

        const detail = document.createElement('div');
        detail.className = 'small text-muted mt-1';
        detail.textContent = step.detail;
        body.appendChild(detail);

        const actions = document.createElement('div');
        actions.className = 'd-flex gap-2 mt-2';

        if (step.targetUrl && !step.done) {
            const goButton = document.createElement('a');
            goButton.className = 'btn btn-sm btn-primary';
            goButton.href = `${step.targetUrl}?from=setup`;
            goButton.textContent = '前往設定';
            actions.appendChild(goButton);
        }

        if (step.canSkip && !step.done) {
            const skipButton = document.createElement('button');
            skipButton.type = 'button';
            skipButton.className = 'btn btn-sm btn-outline-secondary';
            skipButton.textContent = step.skipped ? '取消跳過' : '跳過此步';
            skipButton.addEventListener('click', () => toggleSkip(step, skipButton));
            actions.appendChild(skipButton);
        }

        if (actions.children.length > 0) body.appendChild(actions);

        top.append(dot, body);
        row.appendChild(top);
        list.appendChild(row);
    });

    stepsContainer.appendChild(list);
}

function stepState(step) {
    if (step.done) return 'done';
    if (step.skipped) return 'skipped';
    return 'pending';
}

async function toggleSkip(step, button) {
    const restore = withBusy(button, '處理中');
    try {
        status = await api.post(`/api/admin/setup/skip/${encodeURIComponent(step.id)}`, { skipped: !step.skipped });
        render();
    } catch {
        // 錯誤已由 api.js 顯示
    } finally {
        restore();
    }
}

function renderSettledCard() {
    const card = document.getElementById('setup-settled-card');
    const checkbox = document.getElementById('setup-hidden-checkbox');

    card.classList.toggle('d-none', !status.allSettled);
    if (!status.allSettled) return;

    checkbox.checked = status.hidden;
    checkbox.onchange = async () => {
        const wanted = checkbox.checked;
        checkbox.disabled = true;
        try {
            status = await api.post('/api/admin/setup/hidden', { hidden: wanted });
            toast(wanted ? '已隱藏啟動精靈入口' : '已重新顯示啟動精靈入口', 'success');
        } catch {
            checkbox.checked = !wanted;
        } finally {
            checkbox.disabled = false;
        }
    };
}

window.addEventListener('hashchange', () => {
    const hashId = location.hash.replace(/^#/, '');
    document.querySelector(`[data-step-row="${hashId}"]`)?.scrollIntoView({ block: 'center', behavior: 'smooth' });
});

guardLoad(stepsContainer, load);
