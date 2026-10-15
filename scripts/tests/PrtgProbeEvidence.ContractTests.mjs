import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
const source = await readFile(new URL('../../LogForesight.Web/wwwroot/js/core/prtg-probe-evidence.js', import.meta.url), 'utf8');
const { extractProbeEvidenceJson, isProbeEvidenceDownloadable } = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));
const evidence = { schema_version: '1.0.0', status: 'partial', evidence_ready: false,
    targets: ['cpu', 'memory', 'disk', 'ping', 'traffic'].map(category => ({ category, status: 'missing' })) };
const json = JSON.stringify(evidence);
assert.equal(extractProbeEvidenceJson(json), json);
assert.equal(isProbeEvidenceDownloadable(json, false), true);
assert.equal(isProbeEvidenceDownloadable(json, true), false);
assert.equal(extractProbeEvidenceJson(JSON.stringify({ ...evidence, targets: [...evidence.targets, { category: 'extra' }] })), null);
const messageTime = { authority_status: 'saved-explicit-authority', date_format: 'ymd-24', source_timezone: 'UTC',
    source_culture: 'en-US', authority_revision: 3, authority_fingerprint: 'a'.repeat(64),
    sample_status: 'sample-compatible', sample_source: 'classic-content-messages-api',
    samples_checked: 2, samples_compatible: 2, remote_account_setting_read: false };
const currentEvidence = { ...evidence, schema_version: '1.1.0', message_time: messageTime };
assert.ok(extractProbeEvidenceJson(JSON.stringify(currentEvidence)));
for (const invalidTime of [
    { ...messageTime, remote_account_setting_read: true },
    { ...messageTime, samples_compatible: 1 },
    { ...messageTime, date_format: 'guess' },
    { ...messageTime, sample_status: 'accepted' }
]) assert.equal(extractProbeEvidenceJson(JSON.stringify({ ...currentEvidence, message_time: invalidTime })), null);
assert.equal(extractProbeEvidenceJson(JSON.stringify({ ...evidence, schema_version: '1.1.0' })), null);
for (const status of ['unknown', 'partial', 'error', 'truncated']) {
    assert.ok(extractProbeEvidenceJson(JSON.stringify({ ...evidence, status })));
}
for (const invalid of [null, [], {}, '{', JSON.stringify({ ...evidence, evidence_ready: true }),
    JSON.stringify({ ...evidence, status: 'accepted' }), JSON.stringify({ ...evidence, schema_version: '2' }),
    JSON.stringify({ ...evidence, padding: 'x'.repeat(65536) }),
    JSON.stringify({ ...evidence, padding: '中'.repeat(22000) })]) {
    assert.equal(extractProbeEvidenceJson(invalid), null);
}
console.log('PASS: five-category probe export; running, authorization, malformed and UTF-8 bounds preserved.');
