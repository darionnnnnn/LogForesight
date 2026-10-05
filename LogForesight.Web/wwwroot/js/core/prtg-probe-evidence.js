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
        if (parsed.schema_version !== '1.0.0' || parsed.evidence_ready !== false) return null;
        if (!['unknown', 'partial', 'error', 'truncated'].includes(parsed.status)) return null;
        if (!Array.isArray(parsed.targets) || parsed.targets.length > 3) return null;
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
