/**
 * 首次啟動精靈（回饋十八輪批次H）：checklist 即精靈骨架。
 *
 * 混合制：「完成」由後端依系統狀態自動判定（見 SetupReadinessService），畫面只讀不算；
 * 「跳過」由使用者手動決定、隨時可逆。每次載入都重新拉 /api/admin/setup/status——
 * 從其他頁按「前往設定」做完事回來，精靈頁本身沒有跨頁狀態同步的負擔，重新整理就拿到最新判定。
 */

import { api } from '../core/api.js';
import { appUrl } from '../core/paths.js';
import { toast, withBusy, guardLoad } from '../core/ui.js';
import { statusBadge } from '../core/format.js';

const stepsContainer = document.getElementById('setup-steps');
let status = null;
let settingsSnapshot = null;

/**
 * 伺服器依一行一台 trim、去空白、去重
 */
export function parseServers(raw) {
    if (!raw) return [];
    const lines = raw.split('\n').map(s => s.trim()).filter(s => s.length > 0);
    return [...new Set(lines)];
}

/**
 * 收件人依一行一位 trim、去空白、去重
 */
export function parseRecipients(raw) {
    if (!raw) return [];
    const lines = raw.split('\n').map(s => s.trim()).filter(s => s.length > 0);
    return [...new Set(lines)];
}

async function load() {
    const [statusData, settingsData] = await Promise.all([
        api.get('/api/admin/setup/status'),
        api.get('/api/admin/settings')
    ]);
    status = statusData;
    settingsSnapshot = settingsData;
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

        if (step.id === 'ad' && !step.done) {
            body.appendChild(renderAdInlineForm(step));
        } else if (step.id === 'mail' && !step.done) {
            body.appendChild(renderMailInlineForm(step));
        }

        const actions = document.createElement('div');
        actions.className = 'd-flex gap-2 mt-2';

        if (step.targetUrl && !step.done && step.id !== 'ad' && step.id !== 'mail') {
            const goButton = document.createElement('a');
            goButton.className = 'btn btn-sm btn-primary';
            goButton.href = appUrl(`${step.targetUrl}?from=setup`);
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

function renderAdInlineForm(step) {
    const template = document.getElementById('setup-ad-template');
    const form = template.content.firstElementChild.cloneNode(true);

    const enabledCheckbox = form.querySelector('#setup-ad-auth-enabled');
    const serversInput = form.querySelector('#setup-ad-servers');
    const searchBaseInput = form.querySelector('#setup-ad-search-base');
    const searchFilterInput = form.querySelector('#setup-ad-search-filter');
    const accountInput = form.querySelector('#setup-ad-test-account');
    const passwordInput = form.querySelector('#setup-ad-test-password');
    const testBtn = form.querySelector('#setup-ad-test-btn');
    const testResult = form.querySelector('#setup-ad-test-result');
    const saveBtn = form.querySelector('#setup-ad-save-btn');
    const saveFeedback = form.querySelector('#setup-ad-save-feedback');

    enabledCheckbox.checked = Boolean(settingsSnapshot?.adAuthEnabled);
    serversInput.value = (settingsSnapshot?.adServers ?? []).join('\n');
    searchBaseInput.value = settingsSnapshot?.adSearchBase ?? '';
    searchFilterInput.value = settingsSnapshot?.adSearchFilter ?? '';
    accountInput.value = '';
    passwordInput.value = '';
    passwordInput.setAttribute('autocomplete', 'off');
    testBtn.type = 'button';
    saveBtn.type = 'button';

    testBtn.addEventListener('click', async () => {
        testResult.textContent = '';
        testResult.className = 'mt-2 small';

        const servers = parseServers(serversInput.value);
        const account = accountInput.value.trim();
        const password = passwordInput.value;

        if (servers.length === 0) {
            testResult.className = 'mt-2 small text-danger';
            testResult.textContent = '請至少輸入一台 AD 伺服器。';
            serversInput.focus();
            return;
        }
        if (!account) {
            testResult.className = 'mt-2 small text-danger';
            testResult.textContent = '請輸入測試帳號。';
            accountInput.focus();
            return;
        }
        if (!password) {
            testResult.className = 'mt-2 small text-danger';
            testResult.textContent = '請輸入測試密碼。';
            passwordInput.focus();
            return;
        }

        const searchBase = searchBaseInput.value.trim();
        const searchFilter = searchFilterInput.value.trim();

        const restore = withBusy(testBtn, '測試中');
        try {
            const result = await api.post('/api/admin/settings/ad-test', {
                servers,
                searchBase,
                searchFilter,
                account,
                password
            });
            testResult.className = result.success ? 'mt-2 small text-success' : 'mt-2 small text-danger';
            testResult.textContent = result.message;
        } catch (err) {
            // API exception 由 api.js 顯示（api.js 自動 toast），但表單不可清空
            testResult.className = 'mt-2 small text-danger';
            testResult.textContent = err?.message || '測試連線失敗。';
        } finally {
            restore();
        }
    });

    saveBtn.addEventListener('click', async () => {
        saveFeedback.textContent = '';
        saveFeedback.className = 'small';

        const adAuthEnabled = enabledCheckbox.checked;
        const adServers = parseServers(serversInput.value);
        const adSearchBase = searchBaseInput.value.trim();
        const adSearchFilter = searchFilterInput.value.trim();

        if (adAuthEnabled && adServers.length === 0) {
            saveFeedback.className = 'small text-danger';
            saveFeedback.textContent = '啟用 AD 驗證時，請至少輸入一台 AD 伺服器。';
            serversInput.focus();
            return;
        }

        const restore = withBusy(saveBtn, '儲存中');
        try {
            const latest = await api.get('/api/admin/settings');
            const payload = {
                ...latest,
                adAuthEnabled,
                adServers,
                adSearchBase,
                adSearchFilter
            };
            await api.put('/api/admin/settings', payload);
            toast('已儲存 AD 驗證設定', 'success');
            await load();
            const nextIncomplete = status?.steps?.find(s => !s.done && !s.skipped) || status?.steps?.find(s => !s.done);
            if (nextIncomplete) {
                const target = document.querySelector(`[data-step-row="${nextIncomplete.id}"]`);
                target?.scrollIntoView({ block: 'center', behavior: 'smooth' });
            }
        } catch (err) {
            saveFeedback.className = 'small text-danger';
            saveFeedback.textContent = err?.message || '儲存設定失敗。';
        } finally {
            restore();
        }
    });

    return form;
}

function renderMailInlineForm(step) {
    const template = document.getElementById('setup-mail-template');
    const form = template.content.firstElementChild.cloneNode(true);

    const enabledCheckbox = form.querySelector('#setup-mail-enabled');
    const smtpServerInput = form.querySelector('#setup-smtp-server');
    const smtpPortInput = form.querySelector('#setup-smtp-port');
    const smtpUseTlsCheckbox = form.querySelector('#setup-smtp-use-tls');
    const smtpAccountInput = form.querySelector('#setup-smtp-account');
    const smtpPasswordInput = form.querySelector('#setup-smtp-password');
    const mailFromInput = form.querySelector('#setup-mail-from');
    const mailRecipientsInput = form.querySelector('#setup-mail-recipients');
    const mailOnRunCompletedCheckbox = form.querySelector('#setup-mail-on-run-completed');
    const testBtn = form.querySelector('#setup-mail-test-btn');
    const testResult = form.querySelector('#setup-mail-test-result');
    const saveBtn = form.querySelector('#setup-mail-save-btn');
    const saveFeedback = form.querySelector('#setup-mail-save-feedback');

    enabledCheckbox.checked = Boolean(settingsSnapshot?.mailEnabled);
    smtpServerInput.value = settingsSnapshot?.smtpServer ?? '';
    smtpPortInput.value = settingsSnapshot?.smtpPort || 25;
    smtpUseTlsCheckbox.checked = Boolean(settingsSnapshot?.smtpUseTls);
    smtpAccountInput.value = settingsSnapshot?.smtpAccount ?? '';
    smtpPasswordInput.value = '';
    smtpPasswordInput.setAttribute('autocomplete', 'off');
    mailFromInput.value = settingsSnapshot?.mailFrom ?? '';
    mailRecipientsInput.value = (settingsSnapshot?.mailRecipients ?? []).join('\n');
    mailOnRunCompletedCheckbox.checked = Boolean(settingsSnapshot?.mailOnRunCompleted);
    testBtn.type = 'button';
    saveBtn.type = 'button';

    testBtn.addEventListener('click', async () => {
        testResult.textContent = '';
        testResult.className = 'small';

        const smtpServer = smtpServerInput.value.trim();
        const portVal = smtpPortInput.value.trim();
        const smtpPort = Number(portVal);
        const smtpUseTls = smtpUseTlsCheckbox.checked;
        const smtpAccount = smtpAccountInput.value.trim();
        const smtpPassword = smtpPasswordInput.value;
        const mailFrom = mailFromInput.value.trim();
        const recipients = parseRecipients(mailRecipientsInput.value);

        if (!smtpServer) {
            testResult.className = 'small text-danger';
            testResult.textContent = '請輸入 SMTP 伺服器。';
            smtpServerInput.focus();
            return;
        }

        if (!portVal || !Number.isInteger(smtpPort) || smtpPort < 1 || smtpPort > 65535) {
            testResult.className = 'small text-danger';
            testResult.textContent = 'SMTP Port 必須介於 1~65535。';
            smtpPortInput.focus();
            return;
        }

        if (!mailFrom) {
            testResult.className = 'small text-danger';
            testResult.textContent = '請輸入寄件人。';
            mailFromInput.focus();
            return;
        }

        if (recipients.length === 0) {
            testResult.className = 'small text-danger';
            testResult.textContent = '請至少輸入一位收件人。';
            mailRecipientsInput.focus();
            return;
        }

        const restore = withBusy(testBtn, '寄送中');
        try {
            const result = await api.post('/api/admin/settings/mail-test', {
                smtpServer,
                smtpPort,
                smtpUseTls,
                smtpAccount,
                smtpPassword: smtpPassword || null,
                mailFrom,
                recipients,
                subjectTemplate: settingsSnapshot?.mailSubjectTemplate ?? '',
                bodyIntro: settingsSnapshot?.mailBodyIntro ?? ''
            }, { silent: true });
            testResult.className = result.success ? 'small text-success' : 'small text-danger';
            testResult.textContent = result.message;
        } catch (err) {
            testResult.className = 'small text-danger';
            testResult.textContent = err?.message || '測試寄信失敗。';
        } finally {
            restore();
        }
    });

    saveBtn.addEventListener('click', async () => {
        saveFeedback.textContent = '';
        saveFeedback.className = 'small';

        const mailEnabled = enabledCheckbox.checked;
        const smtpServer = smtpServerInput.value.trim();
        const portVal = smtpPortInput.value.trim();
        const smtpPort = Number(portVal);
        const smtpUseTls = smtpUseTlsCheckbox.checked;
        const smtpAccount = smtpAccountInput.value.trim();
        const smtpPassword = smtpPasswordInput.value;
        const mailFrom = mailFromInput.value.trim();
        const mailRecipients = parseRecipients(mailRecipientsInput.value);
        const mailOnRunCompleted = mailOnRunCompletedCheckbox.checked;

        if (!portVal || !Number.isInteger(smtpPort) || smtpPort < 1 || smtpPort > 65535) {
            saveFeedback.className = 'small text-danger';
            saveFeedback.textContent = 'SMTP Port 必須介於 1~65535。';
            smtpPortInput.focus();
            return;
        }

        if (mailEnabled) {
            if (!smtpServer) {
                saveFeedback.className = 'small text-danger';
                saveFeedback.textContent = '請輸入 SMTP 伺服器。';
                smtpServerInput.focus();
                return;
            }

            if (!mailFrom) {
                saveFeedback.className = 'small text-danger';
                saveFeedback.textContent = '請輸入寄件人。';
                mailFromInput.focus();
                return;
            }

            if (mailRecipients.length === 0) {
                saveFeedback.className = 'small text-danger';
                saveFeedback.textContent = '請至少輸入一位收件人。';
                mailRecipientsInput.focus();
                return;
            }
        }

        const restore = withBusy(saveBtn, '儲存中');
        try {
            const latest = await api.get('/api/admin/settings');

            if (mailEnabled) {
                const hasAnyTrigger = mailOnRunCompleted || Boolean(latest?.mailDailyEnabled || latest?.mailWeeklyEnabled || latest?.mailUrgentEnabled);
                if (!hasAnyTrigger) {
                    saveFeedback.className = 'small text-danger';
                    saveFeedback.textContent = '請至少勾選一項通知觸發（請勾選「執行摘要」）。';
                    mailOnRunCompletedCheckbox.focus();
                    return;
                }
            }

            const payload = {
                ...latest,
                mailEnabled,
                smtpServer,
                smtpPort,
                smtpUseTls,
                smtpAccount,
                smtpPassword: smtpPassword || null,
                mailFrom,
                mailRecipients,
                mailOnRunCompleted
            };

            await api.put('/api/admin/settings', payload);
            toast('已儲存郵件通知設定', 'success');
            await load();
            const nextIncomplete = status?.steps?.find(s => !s.done && !s.skipped) || status?.steps?.find(s => !s.done);
            if (nextIncomplete) {
                const target = document.querySelector(`[data-step-row="${nextIncomplete.id}"]`);
                target?.scrollIntoView({ block: 'center', behavior: 'smooth' });
            }
        } catch (err) {
            saveFeedback.className = 'small text-danger';
            saveFeedback.textContent = err?.message || '儲存設定失敗。';
        } finally {
            restore();
        }
    });

    return form;
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
