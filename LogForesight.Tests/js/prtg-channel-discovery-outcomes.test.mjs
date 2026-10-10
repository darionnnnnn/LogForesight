import assert from 'node:assert/strict';
import test from 'node:test';
import { applyReadOnlyChannelDiscovery, channelDiscoveryPollOutcome, resumableChannelDiscovery } from '../../LogForesight.Web/wwwroot/js/prtg-channel-discovery-outcomes.js';

test('queued, running and capacity-waiting discovery remain pending', () => {
    for (const status of ['queued', 'running', 'waiting-capacity'])
        assert.equal(channelDiscoveryPollOutcome({ status }), 'pending');
});

test('completed discovery is accepted only with explicit read-only, non-authorizing markers', () => {
    assert.equal(channelDiscoveryPollOutcome({ status: 'completed', readOnly: true,
        authorizesQualification: false, authorizesProfile: false }), 'read-only-complete');
    assert.equal(channelDiscoveryPollOutcome({ status: 'completed' }), 'invalid-completion');
});

test('failed, stale and expired discovery never become successful results', () => {
    for (const status of ['failed', 'failed-stale', 'expired'])
        assert.equal(channelDiscoveryPollOutcome({ status }), 'terminal');
});

test('reload resumes only the accepted job for the currently selected sensor and live deadline', () => {
    const saved = { jobId: '0123456789abcdef0123456789abcdef', sensorId: '11',
        deadlineUtc: '2026-10-11T00:00:00Z' };
    assert.equal(resumableChannelDiscovery(saved, 11), true);
    assert.equal(resumableChannelDiscovery(saved, 12), false);
    assert.equal(resumableChannelDiscovery({ ...saved, jobId: '../' + 'a'.repeat(28) }, 11), false);
    assert.equal(resumableChannelDiscovery({ ...saved, deadlineUtc: 'invalid' }, 11), false);
});

test('DOM handler populates only the selected sensor dropdown and labels results unqualified', () => {
    const elements = new Map();
    const doc = {
        getElementById(id) {
            if (!elements.has(id)) elements.set(id, { textContent: '', children: [],
                replaceChildren(...children) { this.children = children; } });
            return elements.get(id);
        },
        createElement() { return { value: '', textContent: '' }; }
    };
    const discovery = { status: 'completed', readOnly: true, authorizesQualification: false,
        authorizesProfile: false, identityEpoch: 7, channelGeneration: '', channelsTruncated: false,
        channels: [{ channelObjectId: '9007199254740993', caption: 'CPU Load', unit: '%', rawValue: 3 },
            { channelObjectId: 'bad', caption: 'ignore', unit: '', rawValue: null }] };

    const applied = applyReadOnlyChannelDiscovery({ doc, discovery, requestedSensorId: '11', selectedSensorId: 11 });
    assert.equal(applied.channels.length, 1);
    const dropdown = doc.getElementById('prtg-profile-binding-channel');
    assert.equal(dropdown.children[1].value, '9007199254740993');
    assert.match(dropdown.children[1].textContent, /未核對 primary/);
    assert.match(doc.getElementById('prtg-profile-binding-channel-evidence').textContent, /不授權 profile/);
    assert.match(doc.getElementById('prtg-profile-binding-action-status').textContent, /不是資格結果/);

    const beforeSwitch = dropdown.children;
    assert.equal(applyReadOnlyChannelDiscovery({ doc, discovery, requestedSensorId: '11', selectedSensorId: 12 }), null);
    assert.equal(dropdown.children, beforeSwitch);
});
