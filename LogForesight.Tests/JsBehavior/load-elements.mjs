/**
 * 載入 production 的 core/elements.js（disclosure、metaLine、statusNote）給行為測試用。
 * 只把 `import { icon } from './ui.js'` 換成替身——ui.js 牽動 paths／storage，與元素行為無關。
 * 多支測試（elements、collapsible-text、record-detail-issue-cell）共用這一份載入方式，
 * elements.js 的匯入改了只改這裡。
 */
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(scriptDir, '..', '..');
const IMPORT_LINE = "import { icon } from './ui.js';";

export function loadElements(dom) {
    const source = fs.readFileSync(path.join(repoRoot, 'LogForesight.Web', 'wwwroot', 'js', 'core', 'elements.js'), 'utf8').replaceAll('\r\n', '\n');
    assert(source.includes(IMPORT_LINE), 'elements.js 的 ui.js 匯入改了，load-elements.mjs 的替身要跟著改');
    const iconStub = name => { const svg = dom.newElement('svg'); svg.setAttribute('data-icon', name); return svg; };
    const body = source.replace(IMPORT_LINE, '').replace(/^export /gm, '');
    return new Function('icon', `${body}\nreturn { disclosure, metaLine, statusNote };`)(iconStub);
}
