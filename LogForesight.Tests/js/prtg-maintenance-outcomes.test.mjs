import assert from 'node:assert/strict';
import test from 'node:test';
import { trustedProbeErrorMessage, trustedQualificationOutcome } from '../../LogForesight.Web/wwwroot/js/prtg-maintenance-outcomes.js';

test('durable proof refresh notice is described as queued, not completed', () => {
    const outcome = trustedQualificationOutcome({ status: 'queued', proofRefresh: { sensorObjid: 11 } }, '11');
    assert.equal(outcome.kind, 'queued');
    assert.equal(outcome.destination, 'prtg-profile-refresh-title');
    assert.match(outcome.message, /通知已保存/);
    assert.match(outcome.message, /profile 尚未更新完成/);
});

test('missing raw proof routes to the existing full-scope job without creating or queueing one', () => {
    const outcome = trustedQualificationOutcome({
        status: 'requires-durable-qualification', durableSelectedSensors: 15000,
        durableJobId: 'job-7', durableJobStatus: 'waiting-capacity',
        durableDeadlineUtc: '2026-10-11T00:00:00Z', requiresExplicitStart: false
    }, '11');
    assert.equal(outcome.kind, 'requires-durable-qualification');
    assert.equal(outcome.destination, 'prtg-qualification-jobs');
    assert.match(outcome.message, /沒有發送來源請求/);
    assert.match(outcome.message, /沒有建立或排入作業/);
    assert.match(outcome.message, /15000 個 sensor/);
    assert.match(outcome.message, /job-7 為 waiting-capacity/);
    assert.match(outcome.message, /這次單顆請求沒有加入作業/);
});

test('missing matching job tells the operator to explicitly start from the job panel', () => {
    const outcome = trustedQualificationOutcome({
        status: 'requires-durable-qualification', durableSelectedSensors: 15000,
        requiresExplicitStart: true
    }, '11');
    assert.match(outcome.message, /目前沒有相同範圍的作業/);
    assert.match(outcome.message, /明確開始新資格作業/);
});

test('maintenance capacity wait explains that the probe sent no GET and retained the draft', () => {
    const message = trustedProbeErrorMessage({
        code: 'maintenance_capacity_wait', status: 409,
        message: '目前 General lane 最早可准入時間超出 30 秒界線。'
    });
    assert.match(message, /沒有發送來源 GET/);
    assert.match(message, /最早可准入時間/);
    assert.match(message, /草稿已保留/);
});
