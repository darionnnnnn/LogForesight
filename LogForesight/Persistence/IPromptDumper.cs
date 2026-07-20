namespace LogForesight;

/// <summary>
/// 驗證期用的 prompt/回應完整輸出（--debug-dump 模式）。刻意跟平常的診斷 log（NLog）分開：
/// nlog.config 刻意不記錄完整 prompt 與 AI 回應全文（避免 log 檔隨呼叫次數線性增長），
/// 但驗證新環境時常常需要看到完整內容，所以另開一個明確、預設關閉的輸出管道。
/// </summary>
public interface IPromptDumper
{
    void Dump(string label, string systemPrompt, string prompt, string response);
}

/// <summary>預設實作：不做任何事。平常執行走這個，完全零成本</summary>
public class NullPromptDumper : IPromptDumper
{
    public void Dump(string label, string systemPrompt, string prompt, string response)
    {
    }
}

/// <summary>--debug-dump 模式使用：每次 AI 呼叫（含 JSON 重試的每次嘗試）各輸出一個檔案到 diag/</summary>
public class FilePromptDumper : IPromptDumper
{
    private readonly string _dir;
    private int _sequence;

    public FilePromptDumper(string? dir = null)
    {
        _dir = dir ?? Path.Combine(AppContext.BaseDirectory, "diag");
        Directory.CreateDirectory(_dir);
    }

    public void Dump(string label, string systemPrompt, string prompt, string response)
    {
        var seq = Interlocked.Increment(ref _sequence);
        var safeLabel = string.Join("_", label.Split(Path.GetInvalidFileNameChars()));
        var path = Path.Combine(_dir, $"{DateTime.Now:yyyyMMdd_HHmmss}_{seq:000}_{safeLabel}.txt");

        var content = $"=== SYSTEM PROMPT ===\n{systemPrompt}\n\n=== PROMPT ===\n{prompt}\n\n=== RESPONSE ===\n{response}\n";
        File.WriteAllText(path, content);
    }
}
