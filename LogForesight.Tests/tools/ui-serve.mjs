// 以 UI 驗收資料集啟動站台（docs/FEEDBACK-69-PLAN.md A-0），供 ui-shots.mjs 截圖。
//
// 站台從獨立的發佈目錄執行，不占用專案 bin（站台執行中會鎖住 bin 的 DLL，測試就建置不了）。
// 資料集先以 `LF_UI_FIXTURE=<目錄>` 跑 UiFixtureDataSetTests 產生，<目錄>/ai 與 <目錄>/noai 各一份。
// 每次啟動都把資料集複製到一份拋棄式的工作目錄再讓站台讀寫：站台的背景服務會改寫紀錄，
// 直接讀原檔的話，前後兩次截圖看到的資料就不同，逐像素比對失去意義。
//
// 用法：node ui-serve.mjs --fixture <目錄>/ai [--port 5099] [--publish <發佈目錄>] [--no-build] [--path-base /LogForesight]
// 前景執行，Ctrl+C 結束。

import { spawn, spawnSync } from 'node:child_process';
import { cpSync, existsSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const args = Object.fromEntries(process.argv.slice(2).reduce((acc, v, i, all) => {
    if (v.startsWith('--')) acc.push([v.slice(2), all[i + 1] && !all[i + 1].startsWith('--') ? all[i + 1] : true]);
    return acc;
}, []));

const repo = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const fixture = args.fixture && resolve(String(args.fixture));
if (!fixture || !existsSync(join(fixture, 'Db', 'logforesight.db'))) {
    console.error('找不到資料集：--fixture 要指向含 Db/logforesight.db 的目錄（先以 LF_UI_FIXTURE 跑 UiFixtureDataSetTests）');
    process.exit(2);
}
const port = Number(args.port || 5099);
const publishDir = resolve(String(args.publish || join(tmpdir(), 'lf-ui-site')));

if (!args['no-build']) {
    const pub = spawnSync('dotnet', ['publish', join(repo, 'LogForesight.Web', 'LogForesight.Web.csproj'), '-c', 'Debug', '-o', publishDir, '--nologo', '-v', 'q'],
        { stdio: 'inherit', shell: process.platform === 'win32' });
    if (pub.status !== 0) process.exit(pub.status ?? 1);
}

const dataRoot = `${publishDir}-data`;
rmSync(dataRoot, { recursive: true, force: true });
cpSync(fixture, dataRoot, { recursive: true });

const siteArgs = [join(publishDir, 'LogForesight.Web.dll'), '--urls', `http://localhost:${port}`,
    '--Storage:Type=Sqlite', `--Storage:DataRoot=${dataRoot}`, '--Storage:ConnectionString='];
if (args['path-base']) siteArgs.push(`--Server:PathBase=${args['path-base']}`);

console.log(`站台：http://localhost:${port}${args['path-base'] || ''}　資料集：${fixture}（工作複本 ${dataRoot}）`);
const site = spawn('dotnet', siteArgs, {
    cwd: publishDir, stdio: 'inherit',
    env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development' }
});
process.on('SIGINT', () => site.kill());
site.on('exit', code => process.exit(code ?? 0));
