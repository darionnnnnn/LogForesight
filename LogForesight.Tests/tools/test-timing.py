"""測試耗時分析（第 64 輪 A-5）。

用法：
    dotnet test --logger "trx;LogFileName=full.trx" --results-directory <dir>
    python LogForesight.Tests/tools/test-timing.py <dir>/full.trx [LogForesight.Tests]

輸出：案數、每案加總與牆鐘（xUnit 的 trx 只有 duration，start/end 相同，牆鐘請自己量）、
依類別與依 xUnit 集合的加總、最慢單案、耗時直方圖、使用 StorageBackend 的類別占比。
只讀本機自己產出的 trx；數字是觀察值，不當驗收門檻（受機器負載影響）。
"""
import collections
import glob
import re
import sys
import xml.etree.ElementTree as ET  # 僅解析本機 dotnet test 產出的 trx

NS = '{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}'


def duration(text):
    hours, minutes, seconds = text.split(':')
    return int(hours) * 3600 + int(minutes) * 60 + float(seconds)


def load(trx_path):
    root = ET.parse(trx_path).getroot()
    names = {d.get('id'): (d.find(NS + 'TestMethod').get('className').split('.')[-1], d.find(NS + 'TestMethod').get('name'))
             for d in root.iter(NS + 'UnitTest')}
    rows = []
    for result in root.iter(NS + 'UnitTestResult'):
        cls, name = names.get(result.get('testId'), ('?', '?'))
        rows.append((cls, name, duration(result.get('duration', '0:0:0')), result.get('outcome')))
    return rows


def scan_sources(tests_dir):
    collections_by_class, storage_classes = {}, set()
    for path in glob.glob(tests_dir + '/**/*.cs', recursive=True):
        parts = path.replace('\\', '/').split('/')
        if 'bin' in parts or 'obj' in parts:
            continue
        text = open(path, encoding='utf-8-sig', errors='ignore').read()
        for m in re.finditer(r'\[Collection\("([^"]+)"\)\]\s*(?:\[[^\]]*\]\s*)*public\s+(?:sealed\s+)?class\s+(\w+)', text):
            collections_by_class[m.group(2)] = m.group(1)
        if 'new StorageBackend(' in text:
            for m in re.finditer(r'public\s+(?:sealed\s+)?class\s+(\w+)', text):
                storage_classes.add(m.group(1))
    return collections_by_class, storage_classes


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(2)
    rows = load(sys.argv[1])
    tests_dir = sys.argv[2] if len(sys.argv) > 2 else 'LogForesight.Tests'
    total = sum(r[2] for r in rows)
    outcomes = collections.Counter(r[3] for r in rows)
    print(f"cases={len(rows)} sum={total:.0f}s outcomes={dict(outcomes)}")

    by_class = collections.defaultdict(float)
    count = collections.Counter()
    for cls, _, dur, _ in rows:
        by_class[cls] += dur
        count[cls] += 1
    print("\n== top 30 classes by sum ==")
    for cls, dur in sorted(by_class.items(), key=lambda x: -x[1])[:30]:
        print(f"{dur:8.1f}s {count[cls]:5d}  {cls}")

    print("\n== top 20 single tests ==")
    for cls, name, dur, _ in sorted(rows, key=lambda x: -x[2])[:20]:
        print(f"{dur:7.1f}s  {cls}.{name}")

    bins = [(0, 0.05), (0.05, 0.2), (0.2, 1), (1, 5), (5, 30), (30, float('inf'))]
    print("\n== histogram ==")
    for lo, hi in bins:
        sel = [r[2] for r in rows if lo <= r[2] < hi]
        print(f"{lo}-{hi}: n={len(sel)} sum={sum(sel):.0f}s")

    collections_by_class, storage_classes = scan_sources(tests_dir)
    by_collection = collections.defaultdict(float)
    for cls, dur in by_class.items():
        by_collection[collections_by_class.get(cls, '(parallel)')] += dur
    print("\n== by xUnit collection ==")
    for name, dur in sorted(by_collection.items(), key=lambda x: -x[1]):
        print(f"{dur:8.0f}s  {name}")
    storage_sum = sum(dur for cls, dur in by_class.items() if cls in storage_classes)
    print(f"\nclasses using new StorageBackend: {storage_sum:.0f}s of {total:.0f}s ({storage_sum / total:.0%})" if total else "")


if __name__ == '__main__':
    main()
