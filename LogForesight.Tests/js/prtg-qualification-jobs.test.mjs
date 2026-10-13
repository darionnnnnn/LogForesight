import assert from 'node:assert/strict';
import test from 'node:test';
import { qualificationSourceGate } from '../../LogForesight.Web/wwwroot/js/prtg-maintenance-outcomes.js';

test('disabled PRTG source blocks qualification actions and gives the settings prerequisite', () => {
    const gate = qualificationSourceGate({ prtgEnabled: false, capacityPilotCurrent: false });
    assert.equal(gate.enabled, false);
    assert.match(gate.reason, /PRTG 來源目前已停用/);
    assert.match(gate.reason, /設定啟用來源/);
});

test('missing contract fails closed while enabled source permits qualification actions', () => {
    assert.equal(qualificationSourceGate(null).enabled, false);
    assert.equal(qualificationSourceGate({ prtgEnabled: true }).enabled, true);
});
