export function trustedQualificationOutcome(result, selectedSensorId) {
    if (result?.status === 'queued') {
        const sensor = result.proofRefresh?.sensorObjid ?? selectedSensorId;
        return { kind: 'queued', destination: 'prtg-profile-refresh-title',
            message: `原始資格證據 profile refresh 已耐久排入佇列（sensor #${sensor}）；這只代表通知已保存，profile 尚未更新完成。請在下方 Trusted sampling profile 更新進度按「重新載入」查看。` };
    }
    if (result?.status === 'requires-durable-qualification') {
        const scopeSize = Number.isInteger(result.durableSelectedSensors) ? result.durableSelectedSensors : '目前完整政策範圍';
        const job = result.durableJobId
            ? `目前相同範圍的資格作業 ${result.durableJobId} 為 ${result.durableJobStatus || 'unknown'}，期限 ${result.durableDeadlineUtc || '請至作業面板確認'}。${result.requiresExplicitStart ? '請在面板明確續跑或開始新作業。' : '請在面板確認進度；這次單顆請求沒有加入作業。'}`
            : '目前沒有相同範圍的作業；請先檢查期限，再使用面板中的「明確開始新資格作業」。';
        return { kind: 'requires-durable-qualification', destination: 'prtg-qualification-jobs',
            message: `原始資格證據尚未保存；這次沒有發送來源請求，也沒有建立或排入作業。單顆核驗需要完整範圍 ${scopeSize} 個 sensor 的 durable qualification。${job} 已導向下方資格作業面板。` };
    }
    return null;
}

export function trustedProbeErrorMessage(error) {
    if (error?.code === 'maintenance_capacity_wait')
        return `維護容量尚不足，這次沒有發送來源 GET。${error.message || '請等待目前准入計畫可容納此探測，再重新執行。'} 草稿已保留。`;
    if (error?.status === 409)
        return '版本或目錄已改變；探測未確認。請先重新載入核對目前綁定與資格狀態，再決定是否重送。';
    return 'Probe 結果未確認；草稿保留。請先重新載入核對已保存版本與來源狀態，再決定是否重送。';
}
