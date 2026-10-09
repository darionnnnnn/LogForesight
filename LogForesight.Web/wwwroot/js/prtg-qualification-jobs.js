import { api } from './core/api.js';

const base = '/api/prtg/monitoring/trusted-sampling/qualification-jobs';
const activeStatuses = new Set(['initializing', 'running', 'waiting-capacity']);
const statusText = {
    initializing: '正在建立受限分頁', running: '資格作業執行中',
    'waiting-capacity': '等待容量核准', completed: '作業完成',
    'completed-with-waiting': '作業結束，仍有未合格或未綁定 sensor', expired: '期限已到',
    cancelled: '已取消', 'failed-stale': '來源或設定已變更'
};

export function initializePrtgQualificationJobs(root = document.querySelector('[data-qualification-jobs]')) {
    if (!root) return;
    const status = root.querySelector('[data-role="status"]');
    const page = root.querySelector('[data-role="page"]');
    const duration = root.querySelector('#prtg-qualification-duration');
    const attempts = root.querySelector('#prtg-qualification-attempts');
    const idsInput = root.querySelector('#prtg-qualification-pilot-ids');
    const cancelButton = root.querySelector('[data-action="cancel"]');
    const startButton = root.querySelector('[data-action="start"]');
    const resumeButton = root.querySelector('[data-action="resume"]');
    const pilotButton = root.querySelector('[data-action="pilot"]');
    let current = null;
    let contract = null;
    let pageOffset = 0;
    let busy = false;
    let rendering = false;
    let notice = '';

    const show = (value) => { status.textContent = value; };
    const updateButtons = () => {
        const blocked = busy || rendering;
        cancelButton.disabled = blocked || !current || !activeStatuses.has(current.status);
        startButton.disabled = blocked || !contract || Boolean(current && activeStatuses.has(current.status));
        resumeButton.disabled = blocked || !contract || !current || activeStatuses.has(current.status) || current.status === 'failed-stale';
        pilotButton.disabled = blocked || !contract;
        root.querySelector('[data-action="reload"]').disabled = blocked;
        for (const button of page.querySelectorAll('button[data-action]'))
            button.disabled = blocked || button.dataset.unavailable === 'true';
        root.setAttribute('aria-busy', String(blocked));
    };
    const clearSensitiveState = () => {
        current = null;
        contract = null;
        pageOffset = 0;
        page.replaceChildren();
        cancelButton.disabled = true;
        startButton.disabled = true;
        resumeButton.disabled = true;
        pilotButton.disabled = true;
    };
    const parseIds = () => {
        const values = idsInput.value.split(',').map(value => value.trim()).filter(Boolean);
        if (values.length < 1 || values.length > 5 || values.some(value => !/^\d+$/.test(value)))
            throw new Error('請輸入 1 至 5 個正整數 sensor ID。');
        if (values.some(value => !Number.isSafeInteger(Number(value)) || Number(value) <= 0))
            throw new Error('sensor ID 必須可由瀏覽器安全表示且大於零。');
        return values.map(value => Number(value));
    };
    const render = async () => {
        if (rendering) return;
        rendering = true;
        updateButtons();
        try {
        let scopeNotice = '';
        try { current = await api.get(`${base}/current`, { silent: true }); }
        catch (error) {
            clearSensitiveState();
            notice = '';
            if (error.code === 'stale_job_scope') scopeNotice = error.message;
            else throw error;
        }
        try { contract = await api.get(`${base}/contract`, { silent: true }); }
        catch (error) {
            contract = null;
            notice = '';
            if (error.status === 403) throw error;
            if (!scopeNotice) scopeNotice = `容量准入契約目前無法讀取：${error.message}`;
        }
        updateButtons();
        const pilot = contract ? (contract.capacityPilotCurrent ? '容量探測有效' : '需要重新執行容量探測') : '容量准入暫不可用';
        show(current
            ? `${statusText[current.status] || current.status}；第 ${current.wave} 輪已取得 raw 資格證據 ${current.qualified}/${current.selected}，待核 eligible ${current.eligible}，等待 ${current.waiting}，本輪失敗或未授權 ${current.failed}，累計來源嘗試 ${current.attempts}；這不代表已發布正式 profile。${pilot}。期限 ${current.deadlineUtc}。${scopeNotice ? ` ${scopeNotice}` : ''}${notice ? ` ${notice}` : ''}`
            : `${scopeNotice || '尚無資格作業'}；${contract ? `完整政策範圍 ${contract.selectedSensors} 個 sensor；` : ''}${pilot}。${notice ? ` ${notice}` : ''}`);
        if (current) await renderPage(current, pageOffset);
        else page.replaceChildren();
        } catch (error) {
            clearSensitiveState();
            throw error;
        } finally { rendering = false; updateButtons(); }
    };
    const renderPage = async (job, offset) => {
        let result;
        try {
            result = await api.get(`${base}/${encodeURIComponent(job.jobId)}/page?offset=${offset}&limit=100`, { silent: true });
        } catch (error) {
            clearSensitiveState();
            throw error;
        }
        pageOffset = result.offset;
        const table = document.createElement('table');
        table.className = 'table table-sm';
        table.innerHTML = '<thead><tr><th>Sensor</th><th>狀態</th><th>嘗試</th><th>原因</th><th>下次嘗試</th></tr></thead>';
        const body = document.createElement('tbody');
        for (const row of result.rows) {
            const tr = document.createElement('tr');
            const reason = row.previousReason ? `${row.reason}；上輪：${row.previousReason}` : row.reason;
            for (const value of [row.sensorObjid, row.status, row.attempts, reason, row.nextAttemptUtc || '']) {
                const td = document.createElement('td'); td.textContent = String(value); tr.append(td);
            }
            body.append(tr);
        }
        table.append(body);
        const navigation = document.createElement('div');
        navigation.className = 'd-flex flex-wrap align-items-center gap-2';
        const previous = document.createElement('button');
        previous.className = 'btn btn-sm btn-outline-secondary';
        previous.type = 'button'; previous.dataset.action = 'page-prev'; previous.textContent = '上一頁';
        previous.dataset.unavailable = String(result.offset === 0 || result.rows.length === 0);
        const range = document.createElement('span');
        range.textContent = result.rows.length ? `${result.offset + 1}–${result.offset + result.rows.length} / ${result.total}` :
            `${result.status === 'initializing' ? '分頁準備中' : '目前沒有可顯示資料'}；共 ${result.total} 個 sensor`;
        const next = document.createElement('button');
        next.className = 'btn btn-sm btn-outline-secondary';
        next.type = 'button'; next.dataset.action = 'page-next'; next.textContent = '下一頁';
        next.dataset.unavailable = String(result.nextOffset == null);
        navigation.append(previous, range, next);
        page.replaceChildren(table, navigation);
        updateButtons();
    };
    root.addEventListener('click', async event => {
        const action = event.target.closest('[data-action]')?.dataset.action;
        if (!action || busy || rendering) return;
        busy = true;
        updateButtons();
        try {
            if (action === 'reload') { await render(); return; }
            if (action === 'page-prev' && current) { pageOffset = Math.max(0, pageOffset - 100); await renderPage(current, pageOffset); return; }
            if (action === 'page-next' && current) { pageOffset += 100; await renderPage(current, pageOffset); return; }
            if (action === 'pilot') {
                show('執行有界 raw 容量探測；請勿重複送出。');
                const result = await api.post(`${base}/pilot`, { sensorObjids: parseIds() }, { timeoutMs: 600000 });
                notice = result.status === 'qualified'
                    ? '真實有界 raw 容量探測已完成；尚未建立正式 profile。'
                    : `容量探測等待：${result.reason}`;
                await render();
            } else if (action === 'start') {
                const hours = Number(duration.value);
                const maximumAttempts = Number(attempts.value);
                if (!Number.isInteger(hours) || hours < 1 || hours > 720) throw new Error('期限必須為 1 至 720 小時。');
                if (!Number.isInteger(maximumAttempts) || maximumAttempts < 1 || maximumAttempts > 3)
                    throw new Error('每個 sensor 本輪最多探測次數必須為 1 至 3。');
                if (current && activeStatuses.has(current.status)) throw new Error('目前作業仍在執行；請先載入狀態或取消。');
                const request = { durationHours: hours, expectedSettingsRevision: contract.settingsRevision,
                    expectedPolicyRevision: contract.policyRevision, expectedScopeFingerprint: contract.scopeFingerprint,
                    maximumAttempts };
                if (current && activeStatuses.has(current.status)) throw new Error('目前作業仍在執行。');
                const result = await api.post(`${base}/start`, request);
                notice = result.status === 'waiting-capacity'
                    ? `${result.reason}；需求 ${result.capacity?.requiredSeconds ?? '未知'} 秒，可用 ${result.capacity?.availableSeconds ?? hours * 3600} 秒；最大嘗試 ${maximumAttempts}。`
                    : '';
                pageOffset = 0;
                await render();
            } else if (action === 'resume' && current) {
                const hours = Number(duration.value);
                const maximumAttempts = Number(attempts.value);
                if (!Number.isInteger(hours) || hours < 1 || hours > 720) throw new Error('期限必須為 1 至 720 小時。');
                if (!Number.isInteger(maximumAttempts) || maximumAttempts < 1 || maximumAttempts > 3)
                    throw new Error('每個 sensor 本輪最多探測次數必須為 1 至 3。');
                const result = await api.post(`${base}/${encodeURIComponent(current.jobId)}/resume`,
                    { expectedVersion: current.version, durationHours: hours, maximumAttempts });
                notice = result.status === 'waiting-capacity'
                    ? `${result.reason}；需求 ${result.capacity?.requiredSeconds ?? '未知'} 秒，可用 ${result.capacity?.availableSeconds ?? hours * 3600} 秒；最大嘗試 ${maximumAttempts}。`
                    : '已在同一作業上續跑，保留 qualified pages、累計嘗試與上一輪原因。';
                await render();
            } else if (action === 'cancel' && current) {
                await api.post(`${base}/${encodeURIComponent(current.jobId)}/cancel`, { expectedVersion: current.version, expectedWave: current.wave });
                notice = '已送出取消；worker 會在短間隔檢查並停止後續工作。已送出的 HTTP 請求可能仍在來源端執行；已送出的 Historic token 會在共享 60 秒窗口內保留。';
                await render();
            }
        } catch (error) { notice = error.message || '資格作業操作失敗。'; show(notice); }
        finally { busy = false; updateButtons(); }
    });
    busy = true;
    updateButtons();
    render().catch(error => show(error.message || '無法讀取資格作業。')).finally(() => { busy = false; updateButtons(); });
    setInterval(() => {
        if (!busy && !document.hidden) render().catch(error => show(error.message || '無法更新資格作業狀態。'));
    }, 5000);
}
