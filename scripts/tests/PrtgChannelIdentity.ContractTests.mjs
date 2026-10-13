import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
const jsRoot = new URL('../../LogForesight.Web/wwwroot/js/', import.meta.url);
const identitySource = await readFile(new URL('prtg-channel-identity.js', jsRoot), 'utf8');
const identityUrl = 'data:text/javascript;base64,' + Buffer.from(identitySource).toString('base64');
const { canonicalPrtgChannelId } = await import(identityUrl);
for (const value of [0, '0', 7, '7', '9007199254740993', '9223372036854775807']) {
    assert.equal(canonicalPrtgChannelId(value), String(value));
}
for (const value of ['007', '+7', '-0', -0, '9223372036854775808', 9007199254740992, null, 1.5, '-5']) {
    assert.equal(canonicalPrtgChannelId(value, true), null);
}
for (const value of ['-1', '-2', '-3', '-4']) {
    assert.equal(canonicalPrtgChannelId(value), null);
    assert.equal(canonicalPrtgChannelId(value, true), value);
}
const discoverySource = (await readFile(new URL('prtg-channel-discovery-outcomes.js', jsRoot), 'utf8'))
    .replace("'./prtg-channel-identity.js'", JSON.stringify(identityUrl));
const { applyReadOnlyChannelDiscovery } = await import('data:text/javascript;base64,' + Buffer.from(discoverySource).toString('base64'));
const controls = new Map();
const doc = {
    createElement: () => ({ value: '', textContent: '', disabled: false }),
    getElementById: id => {
        if (!controls.has(id)) controls.set(id, { textContent: '', options: [], replaceChildren(...items) { this.options = items; } });
        return controls.get(id);
    }
};
const discovery = { status: 'completed', readOnly: true, authorizesQualification: false, authorizesProfile: false,
    channels: ['0', '9007199254740993', '-1', '-2', '-3', '-4', '007', '9223372036854775808'].map(channelObjectId => ({channelObjectId})) };
const applied = applyReadOnlyChannelDiscovery({ doc, discovery, requestedSensorId: 11, selectedSensorId: 11 });
assert.deepEqual(applied.channels.map(item => item.channelObjectId), ['0', '9007199254740993', '-1', '-2', '-3', '-4']);
const options = controls.get('prtg-profile-binding-channel').options;
assert.deepEqual(options.filter(item => item.disabled).map(item => item.value), ['-1', '-2', '-3', '-4']);
assert.equal(options[0].value, '');
assert.match(options[3].textContent, /僅唯讀查證/);
assert.equal(applyReadOnlyChannelDiscovery({ doc, discovery, requestedSensorId: 11, selectedSensorId: 12 }), null);
assert.equal(applyReadOnlyChannelDiscovery({ doc, discovery: {...discovery, authorizesProfile: true}, requestedSensorId: 11, selectedSensorId: 11 }), null);
console.log('PASS canonical channel identities and read-only discovery UI');
