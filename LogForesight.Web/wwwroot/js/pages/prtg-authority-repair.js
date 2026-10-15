import { api, getCurrentUser, hasCapability } from '../core/api.js';

import { confirmAction } from '../core/ui.js';

const panel = document.getElementById('prtg-authority-repair-panel');
if (panel) initialize();

async function initialize() {
    const status = document.getElementById('prtg-authority-repair-status');
    const accessNote = document.getElementById('prtg-authority-repair-access-note');
    const timeBasisLink = document.getElementById('prtg-authority-repair-time-basis-link');
    const kind = document.getElementById('prtg-authority-repair-kind');
    const hostField = document.getElementById('prtg-authority-repair-host-field');
    const hostInput = document.getElementById('prtg-authority-repair-host');
    const content = document.getElementById('prtg-authority-repair-content');
    const loadButton = document.getElementById('prtg-authority-repair-load');
    const removeButton = document.getElementById('prtg-authority-repair-remove');
    const patchButton = document.getElementById('prtg-authority-repair-patch');
    const pointerInput = document.getElementById('prtg-authority-repair-pointer');
    let current = null;

    const setMessage = (message, tone = 'muted') => {
        status.className = `small mb-2 text-${tone}`;
        status.textContent = message;
    };
    const setBusy = busy => {
        loadButton.disabled = busy;
        const hasContent = typeof current?.content === 'string' && current.version > 0;
        removeButton.disabled = busy || !hasContent || current.state === 'InvalidJson';
        patchButton.disabled = busy || !hasContent || current.state !== 'InvalidJson';
        content.readOnly = busy || !hasContent || current.state !== 'InvalidJson';
        kind.disabled = busy;
        hostInput.disabled = busy;
    };
    const route = suffix => {
        const host = kind.value === 'ResourcePressureMode' ? Number(hostInput.value) : null;
        const query = host ? `?hostId=${encodeURIComponent(String(host))}` : '';
        return { hostId: host, url: `/api/admin/settings/prtg-authority-repair/${kind.value}/${suffix}${query}` };
    };

    try {
        const user = await getCurrentUser();
        if (!hasCapability(user, 'Maintain')) return;
        panel.classList.remove('d-none');
        const access = await api.get('/api/admin/settings/prtg-authority-repair/status');
        timeBasisLink.hidden = access.canReadDetails !== true;
        accessNote.textContent = access.canReadDetails
            ? `目前有 ${access.modeDocumentCount} 份主機資格文件；共用磁碟文件：語意 ${access.hasDiskSemanticDocument ? '存在' : '不存在'}、探測 ${access.hasDiskVerificationDocument ? '存在' : '不存在'}。讀取與修復仍逐次核對資料檢視及主機可見權限。`
            : '目前帳號可查看維修項目數量，但沒有業務資料檢視權限；文件內容與修復操作不會提供。';
        if (!access.canReadDetails) {
            loadButton.disabled = true;
            kind.disabled = true;
            accessNote.classList.add('text-warning-emphasis');
        }
    } catch (error) {
        panel.classList.remove('d-none');
        setMessage(`無法載入修復維護狀態：${error.message}`, 'danger');
        loadButton.disabled = true;
        return;
    }

    const updateKind = () => {
        hostField.classList.toggle('d-none', kind.value !== 'ResourcePressureMode');
        current = null;
        pointerInput.value = '';
        content.value = '';
        content.readOnly = true;
        removeButton.disabled = true;
        patchButton.disabled = true;
        setMessage('選擇文件種類後讀取目前版本。');
    };
    kind.addEventListener('change', updateKind);
    hostInput.addEventListener('input', updateKind);

    async function loadDocument() {
        const target = route('document');
        if (kind.value === 'ResourcePressureMode' && (!Number.isSafeInteger(target.hostId) || target.hostId <= 0)) {
            setMessage('請輸入正整數主機 ID；伺服器會再檢查此主機是否在目前可見範圍。', 'warning');
            hostInput.focus();
            return;
        }
        setBusy(true);
        current = null;
        content.value = '';
        content.readOnly = true;
        setMessage('正在以固定讀取上限取得完整文件…');
        try {
            current = await api.get(target.url);
            content.value = current.content ?? '';
            content.readOnly = current.state !== 'InvalidJson';
            const chars = current.contentCharacters == null ? '無法在上限內完整讀取' : `${current.contentCharacters.toLocaleString()} 字元`;
            setMessage(`${current.kind} · ${current.state} · 版本 ${current.version} · ${chars} · 正式上限 ${current.formalLimitCharacters.toLocaleString()}，維修讀取上限 ${current.repairBudgetCharacters.toLocaleString()}。`, current.state === 'WithinLimit' ? 'secondary' : 'warning');
        } catch (error) {
            setMessage(`讀取未完成：${error.message}。請確認資料檢視權限、主機可見範圍及文件版本。`, 'danger');
        } finally {
            setBusy(false);
        }
    }

    loadButton.addEventListener('click', loadDocument);
    hostInput.addEventListener('keydown', event => { if (event.key === 'Enter') loadDocument(); });

    removeButton.addEventListener('click', async () => {
        const pointer = pointerInput.value.trim();
        if (!current || !pointer.startsWith('/') || pointer === '/') {
            setMessage('請輸入指向明確子節點的 JSON Pointer。', 'warning');
            pointerInput.focus();
            return;
        }
        if (!await confirmRepair({ title: '移除資格文件項目',
            message: `將只移除 ${pointer} 指定的項目，並以版本 ${current.version} 寫入。確認繼續？`,
            confirmText: '確認移除' })) return;
        await submitRepair({ removeJsonPaths: [{ pointer }] }, '正在提交明確項目移除…');
    });

    patchButton.addEventListener('click', async () => {
        if (!current || current.state !== 'InvalidJson') return;
        const patch = makeSinglePatch(current.content, content.value);
        if (patch.deleteLength === 0 && patch.replacement.length === 0) {
            setMessage('文字內容尚未修改，沒有可套用的差異。', 'warning');
            return;
        }
        if (patch.deleteLength > 4096 || patch.replacement.length > 4096) {
            setMessage('此次單一文字差異超過 4,096 個 UTF-16 字元。請縮小局部修補範圍後重試。', 'warning');
            return;
        }
        if (!await confirmRepair({ title: '確認資格文件文字修補',
            message: '文字修補成功後，這份文件內的既有正式資格會失效，並需重新探測與確認。其他文件不變。要提交此版本修補嗎？',
            confirmText: '確認修補' })) return;
        try {
            const deleted = current.content.slice(patch.start, patch.start + patch.deleteLength);
            const bytes = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(deleted));
            patch.expectedDeletedTextSha256 = [...new Uint8Array(bytes)].map(byte => byte.toString(16).padStart(2, '0')).join('').toUpperCase();
        } catch {
            setMessage('瀏覽器無法計算安全修補摘要；文件未送出。請改用受支援的安全連線瀏覽器。', 'danger');
            setBusy(false);
            return;
        }
        await submitRepair({ textPatches: [patch] }, '正在檢查完整文件並提交文字修補…');
    });

    async function confirmRepair(options) {
        setBusy(true);
        try {
            const confirmed = await confirmAction(options);
            if (!confirmed) setBusy(false);
            return confirmed;
        } catch (error) {
            setBusy(false);
            throw error;
        }
    }

    async function submitRepair(change, progress) {
        if (!current) { setBusy(false); return; }
        const target = route('repair');
        if (current.kind !== kind.value || current.hostId !== target.hostId) {
            updateKind();
            setMessage('文件目標已變更；請重新讀取目前主機與文件版本。', 'warning');
            setBusy(false);
            return;
        }
        const request = { hostId: target.hostId, expectedVersion: current.version, ...change };
        setBusy(true);
        setMessage(progress);
        try {
            const result = await api.post(target.url.replace(/\?.*$/, ''), request);
            setMessage(`已原子修復版本 ${result.previousVersion}→${result.newVersion}（${result.editCount} 項）。正在重新讀取實際結果。`, 'success');
            pointerInput.value = '';
            await loadDocument();
        } catch (error) {
            setMessage(`修復未確認成功：${error.message}。沒有自動重送；請重新讀取版本與內容後再決定下一步。`, 'danger');
            current = null;
            removeButton.disabled = true;
            patchButton.disabled = true;
        } finally {
            setBusy(false);
        }
    }
}

function makeSinglePatch(before, after) {
    // Textarea values normalize CRLF/CR to LF. Compare that display text, then
    // map the changed range back to the original UTF-16 positions for exact CAS.
    const display = before.replace(/\r\n?/g, '\n');
    const originalPosition = index => {
        let position = 0;
        for (let count = 0; count < index; count++)
            position += before[position] === '\r' && before[position + 1] === '\n' ? 2 : 1;
        return position;
    };
    after = after.replace(/\r\n?/g, '\n');
    let start = 0;
    const limit = Math.min(display.length, after.length);
    while (start < limit && display[start] === after[start]) start++;
    let beforeEnd = display.length;
    let afterEnd = after.length;
    while (beforeEnd > start && afterEnd > start && display[beforeEnd - 1] === after[afterEnd - 1]) {
        beforeEnd--;
        afterEnd--;
    }
    const replacement = after.slice(start, afterEnd).replace(/\n/g, before.includes('\r\n') ? '\r\n' : before.includes('\r') ? '\r' : '\n');
    const originalStart = originalPosition(start);
    return { start: originalStart, deleteLength: originalPosition(beforeEnd) - originalStart, replacement };
}
