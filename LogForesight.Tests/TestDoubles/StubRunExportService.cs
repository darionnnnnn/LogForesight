using LogForesight.Web.Services;

namespace LogForesight.Tests;

/// <summary>測試替身：IRunExportService。可指定要擲的例外、要回的結果，或在執行中阻塞（驗證單一併發）</summary>
internal sealed class StubRunExportService : IRunExportService
{
    public Func<int, CancellationToken, RunExportResult>? Handler { get; set; }
    public List<int> Calls { get; } = new();

    public RunExportResult Build(int days, CancellationToken ct)
    {
        Calls.Add(days);
        if (Handler != null) return Handler(days, ct);
        return new RunExportResult(new byte[] { 0x7B, 0x7D }, days, 0, 0, 0, 0, new Dictionary<string, int>());
    }
}
