﻿using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 任務 F1d-c：在啟動精靈就地完成 AI 服務設定的 UI 結構與行為測試。
/// </summary>
public class SetupAiInlineUiTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { FindRepoRoot() }.Concat(parts).ToArray()));

    private static string ExtractBody(string js, string headerPattern)
    {
        var match = Regex.Match(js, headerPattern);
        Assert.True(match.Success, $"找不到起始樣式: {headerPattern}");

        // 參數本身可能是解構物件；略過參數列直到函式本體的 `) {`。
        var closingParameters = js.IndexOf(')', match.Index + match.Length - 1);
        var open = js.IndexOf('{', closingParameters + 1);
        Assert.True(open >= 0, $"起始樣式之後找不到 '{{': {headerPattern}");

        var depth = 0;
        for (var i = open; i < js.Length; i++)
        {
            var c = js[i];
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return js.Substring(open, i - open + 1);
            }
        }

        Assert.Fail($"大括號未配對: {headerPattern}");
        return string.Empty;
    }

    private static string RunNode(string jsCode)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "node",
            Arguments = "--experimental-default-type=module --input-type=module",
            WorkingDirectory = FindRepoRoot(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new System.Text.UTF8Encoding(false),
            StandardOutputEncoding = new System.Text.UTF8Encoding(false),
            StandardErrorEncoding = new System.Text.UTF8Encoding(false),
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        Assert.NotNull(proc);
        proc!.StandardInput.WriteLine(jsCode);
        proc.StandardInput.Close();

        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        Assert.True(proc.ExitCode == 0, $"Node script failed (exit code {proc.ExitCode}): {stderr}\n{stdout}");
        return stdout;
    }

    [Fact]
    public void SetupCSHTML包含AI就地表單Template與所有必要欄位與按鈕()
    {
        var cshtml = Read("LogForesight.Web", "Views", "Pages", "Setup.cshtml");

        var start = cshtml.IndexOf("id=\"setup-ai-template\"", StringComparison.Ordinal);
        var end = cshtml.IndexOf("</template>", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "找不到 setup-ai-template 區塊");
        var templateBlock = cshtml.Substring(start, end - start);

        Assert.Contains("id=\"setup-ai-form\"", templateBlock);
        Assert.Contains("id=\"setup-ai-provider\"", templateBlock);
        Assert.Contains("id=\"setup-ai-base-url\"", templateBlock);
        Assert.Contains("id=\"setup-ai-model\"", templateBlock);
        Assert.Contains("id=\"setup-ai-azure-deployment\"", templateBlock);
        Assert.Contains("id=\"setup-ai-azure-api-version\"", templateBlock);
        Assert.Contains("id=\"setup-ai-api-key\"", templateBlock);
        Assert.Contains("id=\"setup-ai-save-test-btn\"", templateBlock);
        Assert.Contains("id=\"setup-ai-feedback\"", templateBlock);

        // 各按鈕必須為 type="button"
        var buttons = Regex.Matches(templateBlock, @"<button\b[^>]*>");
        Assert.True(buttons.Count >= 1, "Template 中應至少有儲存並測試按鈕");
        foreach (Match b in buttons)
        {
            Assert.Contains("type=\"button\"", b.Value);
        }

        // 密碼欄位設定 autocomplete="off" 避免瀏覽器自動記憶預填
        Assert.Contains("autocomplete=\"off\"", templateBlock);

        // 顯示 AI 未配置也可用統計模式的明確說明
        Assert.Contains("統計", templateBlock);

        // 密鑰留空沿用既有值；不要新增清除密鑰開關
        Assert.DoesNotContain("clear-api-key", templateBlock);
        Assert.DoesNotContain("clear-ai-api-key", templateBlock);
    }

    [Fact]
    public void AI步驟未完成時走inlinePath且排除前往設定連結_完成或跳過時收起()
    {
        var js = Read("LogForesight.Web", "wwwroot", "js", "pages", "setup.js");
        var renderSteps = ExtractBody(js, @"function\s+renderSteps\s*\(");

        // 未完成且未跳過時渲染就地表單
        Assert.Contains("step.id === 'ai' && !step.done && !step.skipped", renderSteps);
        Assert.Contains("renderAiInlineForm(step)", renderSteps);

        // 前往設定連結排除 AI 步驟
        Assert.Contains("step.id !== 'ai'", renderSteps);

        // 完成或跳過時不展開表單
        Assert.DoesNotContain("if (step.id === 'ai')\n            body.appendChild(renderAiInlineForm", renderSteps);
    }

    [Fact]
    public void 全部Provider選項驗證與欄位切換契約()
    {
        var js = Read("LogForesight.Web", "wwwroot", "js", "pages", "setup.js");
        var updateBody = ExtractBody(js, @"export\s+function\s+updateAiProviderView\s*\(");

        // 驗證三種 Provider 選項均在邏輯中處理
        Assert.Contains("provider === 'OpenAi'", updateBody);
        Assert.Contains("provider === 'AzureOpenAi'", updateBody);
        Assert.Contains("local-model", updateBody);

        // 在 Node 中執行 updateAiProviderView 驗證三種 Provider 的 DOM 切換邏輯
        var jsCode = @"
function createMockElement(id) {
    const classSet = new Set();
    return {
        id,
        textContent: '',
        placeholder: '',
        classList: {
            add: c => classSet.add(c),
            remove: c => classSet.delete(c),
            contains: c => classSet.has(c)
        }
    };
}

function createMockForm() {
    const ids = [
        'setup-ai-provider-hint', 'setup-ai-base-url-wrap', 'setup-ai-base-url-label',
        'setup-ai-base-url', 'setup-ai-base-url-hint', 'setup-ai-model-wrap',
        'setup-ai-model-label', 'setup-ai-model', 'setup-ai-model-hint',
        'setup-ai-azure-deployment-wrap', 'setup-ai-azure-api-version-wrap',
        'setup-ai-api-key-label', 'setup-ai-api-key-hint'
    ];
    const elements = {};
    for (const id of ids) elements['#' + id] = createMockElement(id);
    return {
        querySelector: sel => elements[sel] || null
    };
}

const CLOUD_AI_DECLARATION = '雲端聲明';
" + @"function updateAiProviderView(form, provider, { hasApiKey = false, declaration = '' } = {}) " + updateBody + @"

const assert = (cond, msg) => { if (!cond) throw new Error(msg); };

// 1. 測試 Local Provider
const formLocal = createMockForm();
updateAiProviderView(formLocal, 'Local', { hasApiKey: false });
assert(!formLocal.querySelector('#setup-ai-base-url-wrap').classList.contains('d-none'), 'Local base url should be visible');
assert(formLocal.querySelector('#setup-ai-azure-deployment-wrap').classList.contains('d-none'), 'Local azure dep should be hidden');
assert(formLocal.querySelector('#setup-ai-azure-api-version-wrap').classList.contains('d-none'), 'Local azure ver should be hidden');
assert(formLocal.querySelector('#setup-ai-api-key-label').textContent.includes('選填'), 'Local api key should be optional');

// 2. 測試 OpenAi Provider
const formOpenAi = createMockForm();
updateAiProviderView(formOpenAi, 'OpenAi', { hasApiKey: true });
assert(!formOpenAi.querySelector('#setup-ai-base-url-wrap').classList.contains('d-none'), 'OpenAi base url should be visible');
assert(!formOpenAi.querySelector('#setup-ai-model-wrap').classList.contains('d-none'), 'OpenAi model should be visible');
assert(formOpenAi.querySelector('#setup-ai-model-label').textContent.includes('必填'), 'OpenAi model should be required');
assert(formOpenAi.querySelector('#setup-ai-azure-deployment-wrap').classList.contains('d-none'), 'OpenAi azure dep should be hidden');
assert(formOpenAi.querySelector('#setup-ai-api-key-label').textContent.includes('必填'), 'OpenAi api key should be required');
assert(formOpenAi.querySelector('#setup-ai-api-key-hint').textContent.includes('已設定金鑰'), 'OpenAi key hint should indicate set key');

// 3. 測試 AzureOpenAi Provider
const formAzure = createMockForm();
updateAiProviderView(formAzure, 'AzureOpenAi', { hasApiKey: false });
assert(!formAzure.querySelector('#setup-ai-base-url-wrap').classList.contains('d-none'), 'Azure base url should be visible');
assert(formAzure.querySelector('#setup-ai-base-url-label').textContent.includes('必填'), 'Azure endpoint should be required');
assert(formAzure.querySelector('#setup-ai-model-wrap').classList.contains('d-none'), 'Azure model should be hidden');
assert(!formAzure.querySelector('#setup-ai-azure-deployment-wrap').classList.contains('d-none'), 'Azure dep should be visible');
assert(!formAzure.querySelector('#setup-ai-azure-api-version-wrap').classList.contains('d-none'), 'Azure ver should be visible');
assert(formAzure.querySelector('#setup-ai-api-key-label').textContent.includes('必填'), 'Azure api key should be required');

console.log('PASS_UPDATE_AI_PROVIDER_VIEW');
";
        var output = RunNode(jsCode);
        Assert.Contains("PASS_UPDATE_AI_PROVIDER_VIEW", output);
    }

    [Fact]
    public void API金鑰絕不預填至DOM且留空沿用既有金鑰()
    {
        var js = Read("LogForesight.Web", "wwwroot", "js", "pages", "setup.js");
        var renderForm = ExtractBody(js, @"function\s+renderAiInlineForm\s*\(");

        // API Key 欄位絕不可從 settingsSnapshot 取出預填
        Assert.Contains("apiKeyInput.value = ''", renderForm);
        Assert.Contains("apiKeyInput.setAttribute('autocomplete', 'off')", renderForm);
        Assert.DoesNotContain("apiKeyInput.value = settingsSnapshot", renderForm);
        Assert.DoesNotContain("settingsSnapshot?.aiApiKey", renderForm);

        // 驗證按鈕 type=button
        Assert.Contains("saveTestBtn.type = 'button'", renderForm);
    }

    [Fact]
    public void 儲存後探活回應依done判斷成功非以HTTP200視為探活成功()
    {
        var js = Read("LogForesight.Web", "wwwroot", "js", "pages", "setup.js");
        var checkProbeBody = ExtractBody(js, @"export\s+function\s+checkAiProbeResult\s*\(");

        // 在 Node 中執行 checkAiProbeResult 驗證探活判斷邏輯
        var jsCode = @"function checkAiProbeResult(statusData) " + checkProbeBody + @"
const assert = (cond, msg) => { if (!cond) throw new Error(msg); };

// 1. 成功案例：done 為 true
const okStatus = { steps: [{ id: 'ai', done: true, detail: 'AI 服務探活成功，可正常提供白話摘要。' }] };
const r1 = checkAiProbeResult(okStatus);
assert(r1.isSuccess === true, 'done: true must yield isSuccess: true');
assert(r1.detail.includes('探活成功'), 'detail should match');

// 2. 失敗案例：HTTP 200 返回但 done 為 false
const failStatus = { steps: [{ id: 'ai', done: false, detail: '連接端點超時 (15s)' }] };
const r2 = checkAiProbeResult(failStatus);
assert(r2.isSuccess === false, 'done: false must yield isSuccess: false');
assert(r2.detail === '連接端點超時 (15s)', 'detail should capture fail reason');

// 3. 空狀態防禦
const emptyStatus = { steps: [] };
const r3 = checkAiProbeResult(emptyStatus);
assert(r3.isSuccess === false, 'missing ai step must yield isSuccess: false');

console.log('PASS_CHECK_AI_PROBE_RESULT');
";
        var output = RunNode(jsCode);
        Assert.Contains("PASS_CHECK_AI_PROBE_RESULT", output);

        // 驗證 renderAiInlineForm 中呼叫 checkAiProbeResult 且失敗時不重繪以保留輸入
        var renderForm = ExtractBody(js, @"function\s+renderAiInlineForm\s*\(");
        Assert.Contains("checkAiProbeResult(newStatus)", renderForm);
        Assert.Contains("if (isSuccess) {", renderForm);
        Assert.Contains("await load()", renderForm);
        Assert.Contains("feedback.textContent = `儲存成功但探活失敗：${detail}`", renderForm);
    }

    [Fact]
    public void 最新設定合併包含所有AI欄位且密鑰留空為null()
    {
        var js = Read("LogForesight.Web", "wwwroot", "js", "pages", "setup.js");
        var buildPayloadBody = ExtractBody(js, @"export\s+function\s+buildAiSettingsPayload\s*\(");

        var jsCode = @"function buildAiSettingsPayload(latest, { provider, baseUrl, model, azureDeployment, azureApiVersion, apiKey }) " + buildPayloadBody + @"
const assert = (cond, msg) => { if (!cond) throw new Error(msg); };

const latest = {
    retentionDays: 180,
    adAuthEnabled: true,
    mailEnabled: false,
    otherAdminConfig: 'preserved'
};

// 1. 金鑰留空時傳 null
const p1 = buildAiSettingsPayload(latest, {
    provider: 'Local',
    baseUrl: 'http://localhost:8080',
    model: 'local-model',
    azureDeployment: '',
    azureApiVersion: '',
    apiKey: ''
});
assert(p1.otherAdminConfig === 'preserved', 'other admin config must be preserved');
assert(p1.aiProvider === 'Local', 'aiProvider mismatch');
assert(p1.aiBaseUrl === 'http://localhost:8080', 'aiBaseUrl mismatch');
assert(p1.aiApiKey === null, 'empty apiKey must be converted to null');

// 2. 金鑰填寫時正確帶入
const p2 = buildAiSettingsPayload(latest, {
    provider: 'AzureOpenAi',
    baseUrl: 'https://corp.openai.azure.com',
    model: '',
    azureDeployment: 'gpt-4o',
    azureApiVersion: '2024-10-21',
    apiKey: 'sk-secret-key'
});
assert(p2.aiProvider === 'AzureOpenAi', 'aiProvider mismatch');
assert(p2.aiAzureDeployment === 'gpt-4o', 'azure deployment mismatch');
assert(p2.aiApiKey === 'sk-secret-key', 'apiKey mismatch');

console.log('PASS_BUILD_AI_SETTINGS_PAYLOAD');
";
        var output = RunNode(jsCode);
        Assert.Contains("PASS_BUILD_AI_SETTINGS_PAYLOAD", output);
    }
}
