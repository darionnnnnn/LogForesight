// Channel IDs are Int64 identities, never floating-point measurements.
export function canonicalPrtgChannelId(value, allowSpecial = false) {
    if (typeof value === 'number') {
        if (!Number.isSafeInteger(value) || Object.is(value, -0)) return null;
        value = String(value);
    }
    if (typeof value !== 'string' || !/^(0|[1-9][0-9]*|-[1-4])$/.test(value)) return null;
    if (value.startsWith('-')) return allowSpecial ? value : null;
    if (value.length > 19 || (value.length === 19 && value > '9223372036854775807')) return null;
    return value;
}
