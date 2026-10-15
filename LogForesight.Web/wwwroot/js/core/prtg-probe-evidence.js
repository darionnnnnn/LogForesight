/**
 * 驗證後端獨立提供的去識別相容性證據 JSON。
 *
 * @param {string} evidenceJson 後端 EvidenceJson 欄位
 * @returns {string|null} 合法 compact JSON 字串，或 null（若不存在、不完整或無效）
 */
export function extractProbeEvidenceJson(evidenceJson) {
    if (!evidenceJson) return null;
    if (Array.isArray(evidenceJson) || typeof evidenceJson !== 'string') return null;
    const rawBlock = evidenceJson.trim();
    if (!rawBlock || rawBlock.length > 65536 || new TextEncoder().encode(rawBlock).byteLength > 65536) return null;

    try {
        const parsed = JSON.parse(rawBlock);
        if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) return null;
        if (!['1.0.0', '1.1.0'].includes(parsed.schema_version) || parsed.evidence_ready !== false) return null;
        if (!['unknown', 'partial', 'error', 'truncated'].includes(parsed.status)) return null;
        if (parsed.schema_version === '1.1.0') {
            const time = parsed.message_time;
            if (!time || typeof time !== 'object' || Array.isArray(time) ||
                !['saved-explicit-authority', 'unconfigured'].includes(time.authority_status) ||
                !['not-observed', 'no-samples', 'sample-compatible', 'sample-mismatch', 'authority-changed'].includes(time.sample_status) ||
                time.remote_account_setting_read !== false ||
                !Number.isInteger(time.samples_checked) || time.samples_checked < 0 || time.samples_checked > 500 ||
                !Number.isInteger(time.samples_compatible) || time.samples_compatible < 0 || time.samples_compatible > time.samples_checked) return null;
            if (time.authority_status === 'saved-explicit-authority' &&
                (!['dmy-24', 'dmy-12', 'mdy-24', 'mdy-12', 'ymd-24', 'ymd-12'].includes(time.date_format) ||
                    typeof time.authority_fingerprint !== 'string' || !/^[0-9a-f]{64}$/i.test(time.authority_fingerprint) ||
                    typeof time.source_timezone !== 'string' || !time.source_timezone ||
                    typeof time.source_culture !== 'string' || !time.source_culture ||
                    !Number.isInteger(time.authority_revision) || time.authority_revision <= 0)) return null;
            if (time.sample_status === 'sample-compatible' &&
                (time.authority_status !== 'saved-explicit-authority' || time.samples_checked < 1 || time.samples_compatible !== time.samples_checked)) return null;
            if (time.sample_status === 'sample-mismatch' &&
                (time.authority_status !== 'saved-explicit-authority' || time.samples_checked < 1 || time.samples_compatible >= time.samples_checked)) return null;
            if (['not-observed', 'no-samples'].includes(time.sample_status) &&
                (time.samples_checked !== 0 || time.samples_compatible !== 0)) return null;
            if (time.sample_status === 'authority-changed' &&
                (time.authority_status !== 'saved-explicit-authority' || time.samples_checked < 1)) return null;
            if (time.authority_status === 'unconfigured' &&
                (time.date_format !== 'unknown' || time.source_timezone !== 'unknown' || time.source_culture !== 'unknown' ||
                    time.authority_revision !== null || time.authority_fingerprint !== 'unknown' ||
                    time.sample_status !== 'not-observed' || time.samples_checked !== 0 || time.samples_compatible !== 0)) return null;
        }
        // CPU、memory、disk、ping、traffic；與後端五類唯讀探測範圍一致。
        if (!Array.isArray(parsed.targets) || parsed.targets.length > 5) return null;
        return rawBlock;
    } catch {
        return null;
    }
}

/**
 * 判斷是否具備可下載的相容性證據。
 *
 * @param {string} evidenceJson
 * @param {boolean} [isRunning]
 * @returns {boolean}
 */
export function isProbeEvidenceDownloadable(evidenceJson, isRunning) {
    if (isRunning) return false;
    return extractProbeEvidenceJson(evidenceJson) !== null;
}
