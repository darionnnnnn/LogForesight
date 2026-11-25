/**
 * 「不顯示設定總覽」每個儲存格的文字（第 81 輪批次 N，F16-4）。純函式、不碰 DOM，
 * 數字全由後端 GET api/admin/issue-owners/hide-summary 算好，這裡只組句子；沒有該設定回 null（畫面顯示「—」）。
 */

/** 靜音：「靜音至 2026-12-10」（迄日含當天） */
export function muteText(row) {
    if (!row.mute) return null;
    return `靜音至 ${String(row.mute.to).slice(0, 10)}`;
}

/** 機房結論：結論文字，自動套用中另註明 */
export function conclusionText(row) {
    if (!row.conclusion) return null;
    return `${row.conclusion.statusText}${row.conclusion.autoApply ? '（自動套用中）' : ''}`;
}

/** 抑制：「3 條：全站 1、主機群組 1、單一主機 1」，沒有的範圍不列 */
export function suppressionText(row) {
    const s = row.suppression;
    if (!s) return null;
    const parts = [];
    if (s.siteCount > 0) parts.push(`全站 ${s.siteCount}`);
    if (s.groupCount > 0) parts.push(`主機群組 ${s.groupCount}`);
    if (s.hostCount > 0) parts.push(`單一主機 ${s.hostCount}`);
    return `${s.total} 條：${parts.join('、')}`;
}

/** 不再打擾：「3 台」，有會自動恢復的再補「其中 2 台會恢復，最早 21 天後」 */
export function dismissedText(row) {
    const d = row.dismissed;
    if (!d) return null;
    if (d.releasingHosts > 0) {
        return `${d.hosts} 台，其中 ${d.releasingHosts} 台會自動恢復，最早 ${d.nextReleaseInDays} 天後`;
    }
    return `${d.hosts} 台，不會自動恢復`;
}
