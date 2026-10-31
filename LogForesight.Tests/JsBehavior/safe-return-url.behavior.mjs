import { isSafeReturnUrl } from '../../LogForesight.Web/wwwroot/js/core/safe-return-url.js';

const cases = {
    controlTab: ['/\t/evil.com', false],
    controlLf: ['/\n/evil', false],
    controlCr: ['/\r/evil', false],
    doubleSlash: ['//evil.com', false],
    backslash: ['/\\evil.com', false],
    absoluteHttp: ['http://evil.com', false],
    noLeadingSlash: ['evil.com', false],
    nullValue: [null, false],
    emptyString: ['', false],
    root: ['/', true],
    pathQuery: ['/records?x=1', true],
    pathHash: ['/hosts/12#t', true],
};

const name = process.argv[2];
if (!(name in cases)) {
    console.log(`FAIL ${name} 未知案例`);
    process.exit(1);
}
const [input, expected] = cases[name];
const actual = isSafeReturnUrl(input);
if (actual === expected) {
    console.log(`PASS ${name}`);
} else {
    console.log(`FAIL ${name} 預期 ${expected} 實際 ${actual}`);
    process.exit(1);
}
