/**
 * 規則編輯的「試算影響」（rules.js）：把後端算好的數字組成逐行說明——現在 → 試算、下限估計、新規則、不支援試算。
 * 切出 ruleTrialLines 本體執行（純函式）。
 */
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const src = fs.readFileSync(path.join(scriptDir, '..', '..', 'LogForesight.Web', 'wwwroot', 'js', 'pages', 'rules.js'), 'utf8').replaceAll('\r\n', '\n');
const start = src.indexOf('function ruleTrialLines');
assert(start >= 0, '找不到 ruleTrialLines');
const open = src.indexOf('{', src.indexOf(')', start));
let depth = 0, end = open;
for (; end < src.length; end++) { if (src[end] === '{') depth++; else if (src[end] === '}' && --depth === 0) break; }
const ruleTrialLines = new Function(`${src.slice(start, end + 1)}\nreturn ruleTrialLines;`)();

const base = {
    supported: true, windowDays: 30, isNewRule: false, isLowerBound: false, messageConditionNotRecomputed: false,
    hitHostDaysBefore: 7, hitHostDaysAfter: 4, dispatchPairsBefore: 3, dispatchPairsAfter: 2,
    elevatedHostDaysBefore: 0, elevatedHostDaysAfter: 0
};

const cases = {
    showsBeforeArrowAfterForBothNumbers() {
        const lines = ruleTrialLines(base);
        assert.equal(lines[0], '近 30 天命中主機日：7 → 4');
        assert.equal(lines[1], '會被派工的（主機, 問題）：3 → 2');
        assert.equal(lines[2], '含靜音中的問題（試算不排除靜音）。');
        assert.equal(lines.length, 3, '沒有高風險日變化、不是下限、不是新規則時只有兩行數字加一句靜音說明');
    },
    lowerBoundAddsExplanationOnlyWhenLowerBound() {
        assert(ruleTrialLines({ ...base, isLowerBound: true }).some(l => l.includes('下限估計')));
        assert(!ruleTrialLines({ ...base, isLowerBound: false }).some(l => l.includes('下限')));
    },
    elevatedLineOnlyWhenChanged() {
        assert(ruleTrialLines({ ...base, elevatedHostDaysBefore: 0, elevatedHostDaysAfter: 5 }).some(l => l.includes('重大旗標') && l.includes('0 → 5')));
        assert(!ruleTrialLines({ ...base, elevatedHostDaysBefore: 2, elevatedHostDaysAfter: 2 }).some(l => l.includes('重大旗標')));
    },
    newRuleAndMessageConditionNotes() {
        assert(ruleTrialLines({ ...base, isNewRule: true }).some(l => l.includes('新規則')));
        assert(ruleTrialLines({ ...base, messageConditionNotRecomputed: true }).some(l => l.includes('訊息內容條件')));
    },
    unsupportedShowsOnlyReason() {
        assert.deepEqual(ruleTrialLines({ supported: false, unsupportedReason: '原因 X' }), ['原因 X']);
        assert.deepEqual(ruleTrialLines({ supported: false }), ['這條規則無法試算。']);
    }
};

const name = process.argv[2];
if (!name) { console.log(Object.keys(cases).join('\n')); process.exit(0); }
assert(cases[name], `未知案例：${name}`);
await cases[name]();
console.log(`PASS ${name}`);
