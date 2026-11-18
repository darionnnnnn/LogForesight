import { FONT_SCALES, normalizeFontScale } from '../../LogForesight.Web/wwwroot/js/core/font-scale.js';

function expectEqual(actual, expected, label) {
    if (actual !== expected) {
        throw new Error(`${label}：預期 ${JSON.stringify(expected)} 實際 ${JSON.stringify(actual)}`);
    }
}

const cases = {
    knownValuesKept() {
        for (const scale of FONT_SCALES) expectEqual(normalizeFontScale(scale), scale, scale);
        expectEqual(FONT_SCALES.length, 5, '級距數');
    },
    unknownFallsBackToMedium() {
        for (const value of [null, undefined, '', 'huge', 'Large', ' large']) {
            expectEqual(normalizeFontScale(value), 'medium', String(value));
        }
    }
};

const name = process.argv[2];
if (!name) {
    console.log(Object.keys(cases).join('\n'));
    process.exit(0);
}
if (!(name in cases)) {
    console.log(`FAIL ${name} 未知案例`);
    process.exit(1);
}
try {
    cases[name]();
    console.log(`PASS ${name}`);
} catch (err) {
    console.log(`FAIL ${name} ${err && err.message ? err.message : err}`);
    process.exit(1);
}
