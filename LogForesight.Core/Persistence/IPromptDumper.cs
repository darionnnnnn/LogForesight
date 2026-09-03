using NLog;

namespace LogForesight.Core.Persistence;

/// <summary>
/// 驗證期用的 prompt/回應完整輸出（排程設定的「AI 診斷傾印」開關，
/// docs/archive/WEB-SCHEDULER-PLAN.md §1.4.10）。刻意跟平常的診斷 log（NLog）分開：
/// nlog.config 刻意不記錄完整 prompt 與 AI 回應全文（避免 log 檔隨呼叫次數線性增長），
/// 但驗證新環境時常常需要看到完整內容，所以另開一個明確、預設關閉的輸出管道。
/// </summary>
public interface IPromptDumper
{
    void Dump(string label, string systemPrompt, string prompt, string response);
}

/// <summary>預設實作：不做任何事。平常執行走這個，完全零成本</summary>
internal class NullPromptDumper : IPromptDumper
{
    public void Dump(string label, string systemPrompt, string prompt, string response)
    {
    }
}

/// <summary>「AI 診斷傾印」開啟時使用：每次 AI 呼叫（含 JSON 重試的每次嘗試）各輸出一個檔案到 diag/</summary>
internal class FilePromptDumper : IPromptDumper
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private const int MaxFilesPerRun = 2000;

    private readonly string _dir;
    private int _sequence;
    private int _warned;

    public FilePromptDumper(string? dir = null)
    {
        _dir = dir ?? Path.Combine(AppContext.BaseDirectory, "diag");
        Directory.CreateDirectory(_dir);
    }

    public void Dump(string label, string systemPrompt, string prompt, string response)
    {
        var seq = Interlocked.Increment(ref _sequence);
        if (seq > MaxFilesPerRun)
        {
            if (Interlocked.CompareExchange(ref _warned, 1, 0) == 0)
            {
                Log.Warn("AI 診斷傾印已達單次執行上限 {0} 個檔案，後續診斷傾印已停止寫入。", MaxFilesPerRun);
            }
            return;
        }

        var safeLabel = string.Join("_", label.Split(Path.GetInvalidFileNameChars()));
        var path = Path.Combine(_dir, $"{DateTime.Now:yyyyMMdd_HHmmss}_{seq:000}_{safeLabel}.txt");

        var content = $"=== SYSTEM PROMPT ===\n{systemPrompt}\n\n=== PROMPT ===\n{prompt}\n\n=== RESPONSE ===\n{response}\n";
        File.WriteAllText(path, content);
    }

    internal static int Prune(int retentionDays, string? dir = null)
    {
        if (retentionDays < 1)
            return 0;

        var targetDir = dir ?? Path.Combine(AppContext.BaseDirectory, "diag");
        if (!Directory.Exists(targetDir))
            return 0;

        var cutoff = DateTime.Now.AddDays(-retentionDays);
        var deletedCount = 0;

        // 目錄層級的例外（權限不足等）刻意不吞：由呼叫端（夜間批次清理段）記錄警告，
        // 在這裡吞掉會讓那段 Log.Warn 永遠不會發生，清理靜默失效也看不出來。
        var directoryInfo = new DirectoryInfo(targetDir);
        foreach (var file in directoryInfo.EnumerateFiles("*.txt", SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (file.LastWriteTime < cutoff)
                {
                    file.Delete();
                    deletedCount++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 單一檔案刪除失敗（例如正被寫入而鎖住）不中斷整批，繼續刪下一個
            }
        }

        return deletedCount;
    }
}
