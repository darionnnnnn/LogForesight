import assert from 'node:assert/strict';
import test from 'node:test';
import fs from 'node:fs';
import vm from 'node:vm';

const source = fs.readFileSync(new URL('../../LogForesight.Web/wwwroot/js/pages/prtg-authority-repair.js', import.meta.url), 'utf8');
const start = source.indexOf('function makeSinglePatch(before, after)');
assert.ok(start >= 0, 'Production patch function must exist');
const patchFor = vm.runInNewContext(`(${source.slice(start).trim()})`);
const apply = (raw, patch) => raw.slice(0, patch.start) + patch.replacement + raw.slice(patch.start + patch.deleteLength);

test('textarea newline normalization alone is not an edit', () => {
    for (const raw of ['{\r\n  "x": 1\r\n}', '{\r  "x": 1\r}', '{\r\n "x":1,\n "y":2\r}']) {
        const patch = patchFor(raw, raw.replace(/\r\n?/g, '\n'));
        assert.equal(patch.deleteLength, 0);
        assert.equal(patch.replacement, '');
        assert.equal(apply(raw, patch), raw);
    }
});

test('one syntax repair maps the textarea offset to exact original CRLF text', () => {
    const raw = '{\r\n  "x": 1\r\n  "y": "中😀"\r\n}';
    const displayed = raw.replaceAll('\r\n', '\n');
    const patch = patchFor(raw, displayed.replace('1\n', '1,\n'));
    assert.equal(patch.start, raw.indexOf('1') + 1);
    assert.equal(patch.deleteLength, 0);
    assert.equal(patch.replacement, ',');
    assert.equal(apply(raw, patch), raw.replace('1\r\n', '1,\r\n'));
    assert.deepEqual(JSON.parse(apply(raw, patch)), {x:1,y:'中😀'});
});

test('replacement preserves untouched mixed newlines and supplementary characters', () => {
    const raw = '{\r\n "x":"😀wrong",\n "y":"中"\r}';
    const displayed = raw.replace(/\r\n?/g, '\n');
    const patch = patchFor(raw, displayed.replace('wrong', 'right'));
    assert.equal(raw.slice(patch.start, patch.start + patch.deleteLength), 'wrong');
    assert.equal(apply(raw, patch), raw.replace('wrong', 'right'));
});

test('inserted multiline span uses the source newline convention', () => {
    const raw = '{\r\n "x":1\r\n}';
    const patch = patchFor(raw, '{\n "x":1,\n "y":2\n}');
    assert.equal(apply(raw, patch), '{\r\n "x":1,\r\n "y":2\r\n}');
});
