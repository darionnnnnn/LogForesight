/** 從環境探測的 Type 分布區段擷取實際觀察到的感測器類型。 */
export function parseProbeSensorTypes(output) {
    if (!Array.isArray(output)) return [];
    const header = output.findIndex(line => typeof line === 'string' && line.includes('[Type 分布明細]'));
    if (header < 0) return [];

    const types = [];
    const seen = new Set();
    for (const line of output.slice(header + 1)) {
        if (typeof line !== 'string') break;
        const parts = line.trim().split(/\s+\|\s+/);
        if (parts.length < 5 || !/^\d+$/.test(parts[1]) || !/^\d+(?:\.\d+)?%$/.test(parts[2])) break;
        const type = parts[0].trim();
        if (!type || type.length > 128 || /[\r\n|]/.test(type)) continue;
        const key = type.toLocaleLowerCase();
        if (!seen.has(key)) {
            seen.add(key);
            types.push(type);
        }
    }
    return types;
}
