// 系統健康「主機日恢復」列的文字（settings.js hostDayRecoveryStatus）：切出 production 函式本體執行
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const source = fs.readFileSync(path.join(scriptDir, '..', '..', 'LogForesight.Web', 'wwwroot', 'js', 'pages', 'settings.js'), 'utf8')
    .replaceAll('\r\n', '\n');
const start = source.indexOf('export function hostDayRecoveryStatus');
assert(start >= 0, '找不到 production 函式 hostDayRecoveryStatus');
const open = source.indexOf('{', source.indexOf(')', start));
let depth = 0, end = -1;
for (let i = open; i < source.length; i++) {
    if (source[i] === '{') depth++;
    else if (source[i] === '}' && --depth === 0) { end = i + 1; break; }
}
const body = source.slice(start, end).replace(/^export /, '');
const status = new Function('formatNumber', `${body}\nreturn hostDayRecoveryStatus;`)(n => String(n));

const cases = {
    noneWaitingIsNormal() {
        assert.equal(status({ waiting: 0, needsAttention: 0, byReason: {} }), '正常');
    },
    queuedOversizedIsNotAProblem() {
        const text = status({ waiting: 2, needsAttention: 0, byReason: { 'recovery-payload-over-limit': 2 } });
        assert(text.startsWith('排隊處理中 2 筆'), text);
        assert(text.includes('內容過大 2'), text);
        assert(!text.includes('需要檢查'), text);
    },
    invalidRowsNeedAttention() {
        const text = status({ waiting: 3, needsAttention: 1, byReason: { 'recovery-payload-invalid': 1, 'recovery-payload-over-limit': 2 } });
        assert(text.startsWith('需要檢查 1 筆'), text);
        assert(text.includes('內容無法解析 1'), text);
    },
    unknownReasonShownAsIs() {
        assert(status({ waiting: 1, needsAttention: 1, byReason: { 'future-code': 1 } }).includes('future-code 1'));
    }
};
const name = process.argv[2];
if (!name) { console.log(Object.keys(cases).join('\n')); process.exit(0); }
assert(cases[name], `未知的案例：${name}`);
cases[name]();
console.log(`PASS ${name}`);
