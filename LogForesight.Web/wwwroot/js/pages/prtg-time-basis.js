import { api, getCurrentUser, hasCapability } from '../core/api.js';

import { confirmAction } from '../core/ui.js';

const form = document.getElementById('prtg-time-basis-form');
const status = document.getElementById('prtg-time-basis-status');
const saveButton = document.getElementById('prtg-time-basis-save');
const reloadButton = document.getElementById('prtg-time-basis-reload');
let currentPolicyRevision = '';
let canEdit = false;
let loading = false;
let saving = false;

function setEditingEnabled(enabled) {
    canEdit = enabled;
    for (const input of form.querySelectorAll('input')) input.disabled = !enabled;
    saveButton.disabled = !enabled || loading || saving || !currentPolicyRevision;
    reloadButton.disabled = loading || saving;
}

async function load({ confirmDiscard = false } = {}) {
    if (loading || saving) return;
    const needsConfirmation = confirmDiscard && canEdit;
    loading = true;
    setEditingEnabled(false);
    if (needsConfirmation && !await confirmAction({ title: '重新讀取時間基準',
        message: '重新讀取會捨棄尚未保存的修改。要繼續嗎？', confirmText: '重新讀取', confirmVariant: 'primary' })) {
        loading = false;
        setEditingEnabled(true);
        return;
    }
    status.classList.remove('text-danger', 'text-warning-emphasis');
    status.textContent = '正在讀取目前保存狀態…';
    try {
        const user = await getCurrentUser();
        if (!hasCapability(user, 'ViewAll')) {
            currentPolicyRevision = '';
            status.textContent = '此帳號沒有業務資料檢視權限，不能讀取或編輯時間基準。請由同時具備 Maintain 與 ViewAll 的一般維護管理者處理。';
            status.classList.add('text-warning-emphasis');
            loading = false;
            setEditingEnabled(false);
            reloadButton.disabled = true;
            return;
        }
        const policy = await api.get('/api/prtg/monitoring');
        currentPolicyRevision = policy.revision || '';
        document.getElementById('prtg-time-basis-raw-zone').value = policy.rawTimestampTimeZoneId || '';
        document.getElementById('prtg-time-basis-analysis-zone').value = policy.analysisTimeZoneId || '';
        document.getElementById('prtg-time-basis-reference').value = policy.timeBasisEvidenceReference || '';
        document.getElementById('prtg-time-basis-confirm').checked = false;
        if (policy.enabled) {
            currentPolicyRevision = '';
            status.textContent = 'PRTG 來源目前啟用中。請先返回 PRTG 維護停用來源，再重新讀取並確認時間基準；目前不能修改。';
            status.classList.add('text-warning-emphasis');
            loading = false;
            setEditingEnabled(false);
            return;
        }
        if (!policy.ready) {
            currentPolicyRevision = '';
            status.textContent = '請先返回 PRTG 維護，保存有效的來源身分與完整作用範圍，再重新讀取時間基準；目前不能修改。';
            status.classList.add('text-warning-emphasis');
            loading = false;
            setEditingEnabled(false);
            return;
        }
        status.textContent = policy.timeBasisConfigured
            ? '已有操作者輸入已保存；來源帳號讀回與 native sample 驗證仍分開判定。'
            : '尚未保存完整時間基準；文字訊息、數值與 raw evidence 仍各自依其權威檢查。';
        loading = false;
        setEditingEnabled(true);
        reloadButton.disabled = false;
    } catch (error) {
        currentPolicyRevision = '';
        loading = false;
        setEditingEnabled(false);
        if (error.status === 403 || error.code === 'forbidden') {
            status.textContent = '此帳號沒有業務資料檢視權限，不能讀取或編輯時間基準。請由同時具備 Maintain 與 ViewAll 的一般維護管理者處理。';
            status.classList.add('text-warning-emphasis');
            reloadButton.disabled = true;
        } else {
            status.textContent = `${error.message || '目前不可讀取時間基準設定。'} 請重新讀取狀態後再編輯。`;
            status.classList.add('text-danger');
            reloadButton.disabled = false;
        }
    }
}

reloadButton.addEventListener('click', () => { void load({ confirmDiscard: true }); });

form.addEventListener('submit', async event => {
    event.preventDefault();
    if (saving || loading || !canEdit || !form.reportValidity() || !currentPolicyRevision) return;
    saving = true;
    setEditingEnabled(false);
    status.classList.remove('text-danger');
    status.textContent = '正在以目前政策修訂保存…';
    try {
        const result = await api.put('/api/prtg/monitoring/time-basis', {
            expectedPolicyRevision: currentPolicyRevision,
            rawTimestampTimeZoneId: document.getElementById('prtg-time-basis-raw-zone').value,
            analysisTimeZoneId: document.getElementById('prtg-time-basis-analysis-zone').value,
            timeBasisEvidenceReference: document.getElementById('prtg-time-basis-reference').value,
            operatorConfirmed: document.getElementById('prtg-time-basis-confirm').checked
        });
        currentPolicyRevision = result.revision;
        status.textContent = '已保存操作者輸入。這不是遠端帳號讀回或 native qualification；請繼續執行既有來源探測與資格流程。';
        saving = false;
        setEditingEnabled(true);
    } catch (error) {
        currentPolicyRevision = '';
        saving = false;
        status.textContent = `${error.message || '保存結果尚未確認。'} 請先重新讀取目前設定與修訂；未確認前不會重送。`;
        status.classList.add('text-danger');
        reloadButton.disabled = false;
        setEditingEnabled(false);
    }
});

void load();
