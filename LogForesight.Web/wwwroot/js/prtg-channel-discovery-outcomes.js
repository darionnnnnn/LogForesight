const terminalStatuses = new Set(['failed', 'failed-stale', 'expired']);

export function channelDiscoveryPollOutcome(job) {
    if (job?.status === 'completed') {
        return job.readOnly === true && job.authorizesQualification === false && job.authorizesProfile === false
            ? 'read-only-complete' : 'invalid-completion';
    }
    return terminalStatuses.has(job?.status) ? 'terminal' : 'pending';
}

export function resumableChannelDiscovery(saved, sensorId) {
    return !!saved && typeof saved.jobId === 'string' && /^[0-9a-f]{32}$/i.test(saved.jobId) &&
        String(saved.sensorId) === String(sensorId) && Number.isFinite(Date.parse(saved.deadlineUtc));
}

export function applyReadOnlyChannelDiscovery({ doc, discovery, requestedSensorId, selectedSensorId }) {
    if (String(requestedSensorId) !== String(selectedSensorId) ||
        channelDiscoveryPollOutcome(discovery) !== 'read-only-complete') return null;
    const select = doc.getElementById('prtg-profile-binding-channel');
    const options = [];
    const channels = [];
    for (const item of discovery.channels || []) {
        const rawId = String(item.channelObjectId ?? '');
        if (!/^[0-9]{1,20}$/.test(rawId)) continue;
        const channelId = rawId.replace(/^0+(?=\d)/, '');
        const option = doc.createElement('option');
        option.value = channelId;
        option.textContent = `#${rawId}｜${item.caption || '無 caption'}｜${item.unit || '無單位'}｜值 ${item.rawValue ?? '未知'}｜未核對 primary`;
        options.push(option);
        channels.push(item);
    }
    const prompt = doc.createElement('option');
    prompt.value = '';
    prompt.textContent = '請人工選擇頻道（不自動猜測）';
    select.replaceChildren(prompt, ...options);
    doc.getElementById('prtg-profile-binding-channel-evidence').textContent =
        `唯讀 channel discovery 已完成：${channels.length} 個頻道；這是後台單一 channels Table 讀取，未核對 sensor 前後快照、native primary 或 Historic 原始資格，不授權 profile。Identity epoch ${discovery.identityEpoch ?? '請重新載入'}，Channel generation ${discovery.channelGeneration ?? '請重新載入'}；${discovery.channelsTruncated ? '回應達 100 列上限，不能保存此清單中的 binding。' : '請人工核對來源語意並明確選取。'}`;
    doc.getElementById('prtg-profile-binding-action-status').textContent =
        `唯讀探索完成，回傳 ${channels.length} 個頻道；這不是資格結果，之後仍須分開執行完整核驗。`;
    return { channels, channelsTruncated: discovery.channelsTruncated === true };
}
