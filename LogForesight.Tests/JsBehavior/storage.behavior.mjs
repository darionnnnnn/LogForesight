import { safeStorage } from '../../LogForesight.Web/wwwroot/js/core/storage.js';

function throwingStore() {
    const fail = () => { throw new Error('SecurityError: 模擬隱私模式'); };
    return { getItem: fail, setItem: fail, removeItem: fail };
}

function withStore(store, body) {
    const had = Object.prototype.hasOwnProperty.call(globalThis, 'localStorage');
    const original = globalThis.localStorage;
    globalThis.localStorage = store;
    try {
        return body();
    } finally {
        if (had) globalThis.localStorage = original;
        else delete globalThis.localStorage;
    }
}

function memoryStore() {
    const map = new Map();
    return {
        getItem: (k) => (map.has(k) ? map.get(k) : null),
        setItem: (k, v) => { map.set(k, String(v)); },
        removeItem: (k) => { map.delete(k); },
    };
}

function expectEqual(actual, expected, label) {
    if (actual !== expected) {
        throw new Error(`${label}：預期 ${JSON.stringify(expected)} 實際 ${JSON.stringify(actual)}`);
    }
}

const cases = {
    getThrows() {
        withStore(throwingStore(), () => {
            expectEqual(safeStorage.get('k'), null, '讀取擲例外時的回傳值');
        });
    },

    setThrows() {
        withStore(throwingStore(), () => {
            safeStorage.set('k', 'v');
            safeStorage.remove('k');
        });
    },

    roundTrip() {
        withStore(memoryStore(), () => {
            safeStorage.set('k', 'v1');
            expectEqual(safeStorage.get('k'), 'v1', '寫入後讀回');
            safeStorage.remove('k');
            expectEqual(safeStorage.get('k'), null, '移除後讀回');
        });
    },
};

const name = process.argv[2];
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
