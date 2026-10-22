import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(scriptDir, '..', '..');
const source = fs.readFileSync(path.join(repoRoot, 'LogForesight.Web', 'wwwroot', 'js', 'pages', 'prtg-admin.js'), 'utf8').replaceAll('\r\n', '\n');

function extractFunction(declaration) {
    const start = source.indexOf(declaration);
    assert(start >= 0, `找不到 production 函式：${declaration}`);
    const open = source.indexOf('{', start);
    let depth = 0;
    for (let i = open; i < source.length; i++) {
        if (source[i] === '{') depth++;
        else if (source[i] === '}' && --depth === 0) return source.slice(start, i + 1);
    }
    throw new Error(`production 函式未閉合：${declaration}`);
}

// 只替身 getElementById：這兩個函式不碰其他 DOM API
function load(elements) {
    const document = { getElementById: id => elements[id] ?? null };
    const factory = new Function('document', `${extractFunction('function fullBackfillRedoChecked()')}
${extractFunction('function syncFullBackfillStart(preview)')}
return { syncFullBackfillStart };`);
    return factory(document);
}

const cases = {
    startButtonFollowsCompletionAndRedo() {
        const start = { disabled: false };
        const hint = { hidden: true };
        const redo = { checked: false };
        const { syncFullBackfillStart } = load({ 'prtg-full-backfill-start': start, 'prtg-full-backfill-redo-hint': hint, 'prtg-full-backfill-redo': redo });

        // 全部完成、沒勾全部重做：後端必拒，按鈕停用並顯示說明
        syncFullBackfillStart({ previewId: 'p', completedDays: 2, dayCount: 2 });
        assert.equal(start.disabled, true);
        assert.equal(hint.hidden, false);

        // 勾了全部重做：可以送出
        redo.checked = true;
        syncFullBackfillStart({ previewId: 'p', completedDays: 2, dayCount: 2 });
        assert.equal(start.disabled, false);
        assert.equal(hint.hidden, true);

        // 只完成部分：不必勾也能送出
        redo.checked = false;
        syncFullBackfillStart({ previewId: 'p', completedDays: 1, dayCount: 2 });
        assert.equal(start.disabled, false);

        // 沒有預覽 id 一律停用
        syncFullBackfillStart({ previewId: '', completedDays: 0, dayCount: 2 });
        assert.equal(start.disabled, true);
    },
    noCompletedDaysHasNoRedoCheckbox() {
        const start = { disabled: true };
        const { syncFullBackfillStart } = load({ 'prtg-full-backfill-start': start });
        syncFullBackfillStart({ previewId: 'p', completedDays: 0, dayCount: 3 });
        assert.equal(start.disabled, false);
    }
};

const name = process.argv[2];
assert(cases[name], `未知的案例：${name}`);
cases[name]();
console.log(`PASS ${name}`);
