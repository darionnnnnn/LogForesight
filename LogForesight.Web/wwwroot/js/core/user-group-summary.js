/**
 * 使用者編輯視窗的群組即時摘要（第 81 輪批次 U，F18-1）：依目前勾選、尚未儲存的群組顯示
 * 能力（中文）、可見主機台數、是否在派工池。資料來自 GET api/admin/users/group-preview，這裡只負責顯示：
 * 不算任何規則，也不寫入。連點勾選時以請求序號守門，舊回應不得蓋掉新結果。
 *
 * 只依賴 async-guards（純函式），所以行為測試可以直接匯入。
 */
import { createSeqGuard } from './async-guards.js';

/** 摘要的三行文字（純函式，行為測試直接驗）；preview 為後端的 UserGroupPreviewDto */
export function summaryLines(preview) {
    const capabilities = preview?.capabilities ?? [];
    const lines = [
        ['能力', capabilities.length > 0 ? capabilities.join('、') : '沒有任何能力（沒有勾選啟用中的群組，或帳號已停用）'],
        ['可見主機', `${preview?.visibleHostCount ?? 0} 台`]
    ];
    let pool;
    if (preview?.inPool) pool = preview.canHandle ? '在派工池（會被自動派工）' : '在派工池，但沒有處理能力，不會被派單';
    else pool = '不在派工池';
    lines.push(['派工池', pool]);
    if ((preview?.inactiveGroupCount ?? 0) > 0)
        lines.push(['提醒', `勾選的群組中有 ${preview.inactiveGroupCount} 個已停用，不給能力也不給可見範圍`]);
    return lines;
}

/**
 * @param {{ host: HTMLElement, fetchPreview: (args: { userId: number|null, groupIds: number[], active: boolean }) => Promise<object> }} options
 * @returns {{ refresh(args: { userId: number|null, groupIds: number[], active: boolean }): Promise<void>, clear(): void }}
 */
export function createUserGroupSummary({ host, fetchPreview }) {
    const guard = createSeqGuard();

    function clear() {
        guard.next();   // 在途的舊請求作廢
        host.replaceChildren();
    }

    async function refresh(args) {
        const seq = guard.next();
        let preview;
        try {
            preview = await fetchPreview(args);
        } catch {
            if (!guard.isCurrent(seq)) return;
            host.replaceChildren(document.createTextNode('暫時無法算出群組摘要，不影響儲存。'));
            return;
        }
        if (!guard.isCurrent(seq)) return;
        const children = summaryLines(preview).map(([label, text]) => {
            const row = document.createElement('div');
            const name = document.createElement('span');
            name.className = 'lf-text-label';
            name.textContent = label;
            row.append(name, ' ', text);
            return row;
        });
        host.replaceChildren(...children);
    }

    return { refresh, clear };
}
