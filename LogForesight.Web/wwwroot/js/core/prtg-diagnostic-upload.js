import { api } from './api.js';
import { appUrl } from './paths.js';

export const DIAGNOSTIC_CHUNK_BYTES = 4 * 1024 * 1024;
const base = '/api/admin/settings/prtg-import-transfers';
const checkCancelled = signal => {
    if (signal?.aborted) throw new DOMException('作業已取消；已接受的片段仍可續傳。', 'AbortError');
};

/** 每次僅保留一片；worker 的 transferable buffer 避免把大檔留在頁面記憶體。 */
export async function hashDiagnosticFile(file, signal, progress = () => {},
    workerFactory = () => new Worker(appUrl('/js/core/sha256-worker.js'), { type: 'module' })) {
    checkCancelled(signal);
    const worker = workerFactory();
    let sequence = 0;
    let rejectPending;
    const abort = () => rejectPending?.(new DOMException('作業已取消。', 'AbortError'));
    signal?.addEventListener('abort', abort, { once: true });
    const send = (type, bytes) => new Promise((resolve, reject) => {
        rejectPending = reject;
        const id = ++sequence;
        worker.onmessage = ({ data }) => {
            if (data.id !== id) return;
            rejectPending = null;
            data.ok ? resolve(data.hash) : reject(new Error(data.message || '診斷檔雜湊失敗。'));
        };
        worker.onerror = () => reject(new Error('診斷檔雜湊工作無法執行。'));
        worker.postMessage({ id, type, bytes }, bytes ? [bytes] : []);
    });
    try {
        await send('reset');
        for (let offset = 0; offset < file.size; offset += DIAGNOSTIC_CHUNK_BYTES) {
            checkCancelled(signal);
            const bytes = await file.slice(offset, offset + DIAGNOSTIC_CHUNK_BYTES).arrayBuffer();
            checkCancelled(signal);
            await send('append', bytes);
            progress(Math.min(file.size, offset + DIAGNOSTIC_CHUNK_BYTES), file.size);
        }
        checkCancelled(signal);
        return await send('finish');
    } finally {
        worker.terminate();
        signal?.removeEventListener('abort', abort);
        rejectPending = null;
    }
}

/** 續傳重送每個 ordinal；不以 ReceivedChunks 誤推斷缺片位置。SQL 對相同片段冪等。 */
export async function uploadDiagnosticFile(file, { signal, resume, progress = () => {}, onSession = () => {},
    hashFile = hashDiagnosticFile, client = api, createId = () => crypto.randomUUID() } = {}) {
    if (!Number.isSafeInteger(file?.size) || file.size < 1 ||
        Math.ceil(file.size / DIAGNOSTIC_CHUNK_BYTES) > 2147483647) throw new Error('診斷檔大小無效或超過傳輸上限。');
    const options = { signal, silent: true };
    const hash = await hashFile(file, signal, (done, total) => progress('hashing', done, total));
    checkCancelled(signal);
    if (resume && (resume.packageSha256 !== hash || resume.declaredBytes !== file.size))
        throw new Error('續傳檔案的大小或 SHA-256 不同；請選取原檔或明確放棄原傳輸。');
    const session = { transferId: resume?.transferId || createId(), declaredBytes: file.size, packageSha256: hash,
        chunkCount: Math.ceil(file.size / DIAGNOSTIC_CHUNK_BYTES) };
    onSession(session);
    // Create 同一 ID/宣告的重送亦冪等；不確定第一次回應時仍可查詢與续傳。
    const status = await client.post(base, session, options);
    checkCancelled(signal);
    if (status.state === 'complete') return status;
    if (status.state !== 'receiving') {
        if (status.state === 'validating') return await client.post(`${base}/${session.transferId}/complete`, {}, options);
        throw new Error(`原傳輸狀態為 ${status.state}；請查詢原因或明確放棄後重新建立。`);
    }
    for (let ordinal = 0; ordinal < session.chunkCount; ordinal++) {
        checkCancelled(signal);
        const offset = ordinal * DIAGNOSTIC_CHUNK_BYTES;
        const body = file.slice(offset, offset + DIAGNOSTIC_CHUNK_BYTES);
        await client.putBytes(`${base}/${session.transferId}/chunks/${ordinal}`, body, options);
        checkCancelled(signal);
        progress('uploading', Math.min(file.size, offset + DIAGNOSTIC_CHUNK_BYTES), file.size);
    }
    progress('validating', file.size, file.size);
    checkCancelled(signal);
    return await client.post(`${base}/${session.transferId}/complete`, {}, options);
}

export const getDiagnosticTransfer = id => api.get(`${base}/${id}`, { silent: true });
export const abandonDiagnosticTransfer = id => api.post(`${base}/${id}/abandon`, {}, { silent: true });
