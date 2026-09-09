/**
 * 取數執行進度軌的 phase 文案與單位（docs/WEB-SPEC.md §9.10）。
 *
 * phase 字面值由 Core 的 `RunPhases` 送出、Web 分派、前端在這裡對照——三層共用同一份清單，
 * 完整性由 `RunsPageUiTests` 以反射逐條核對；漏補文案時畫面會印裸 phase 給使用者。
 * 放在 core/ 是因為排程作業頁與 PRTG 維護頁（手動同步的進度）都要用，各留一份會漏改。
 */

export const PROGRESS_PHASE_LABEL = {
    local: '本機分析',
    netiq: 'NetIQ 機房分析',
    'prtg-sync': 'PRTG 結構同步',
    'prtg-sync-devices': 'PRTG 裝置結構同步',
    'prtg-sync-sensors': 'PRTG 感測器結構同步',
    'prtg-sync-messages': 'PRTG 狀態變更同步',
    'prtg-values': 'PRTG 數值取數',
    'prtg-triggered': 'PRTG 觸發式取數',
    'prtg-wait-sync': '等待手動同步完成'
};
export const PROGRESS_PHASE_UNIT = {
    'prtg-sync': 'sensor',
    'prtg-sync-devices': '台',
    'prtg-sync-sensors': '個',
    'prtg-sync-messages': '筆',
    'prtg-values': 'sensor',
    'prtg-triggered': 'sensor',
    'prtg-wait-sync': ''
};
