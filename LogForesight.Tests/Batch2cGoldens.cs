namespace LogForesight.Tests;

/// <summary>改動前（HEAD 8225380d）的乾淨副本跑 <see cref="Batch2cScenarios"/> 取得的輸出。
/// 執行總表輸出約 100 KB，只記長度與 SHA-256；其餘直接記全文。</summary>
internal static class Batch2cGoldens
{
    public const int RunMonitorLength = 104074;
    public const string RunMonitorSha256 = "f28ed2cc4508c0c9ec5156ad30d9752dd4fc5dbe4dc9c7567471e00a752c249d";

    public const string Occurrence = """
HOST-1|Application|disk|11|1|Processing|case=case-first|handling=/|inherited=False
host-2|Application|cpu|22|1|Resolved|case=|handling=resolved/第一筆|inherited=False
host-2|Application|Net|33|1|Open|case=|handling=/|inherited=False
host-2|PRTG|PRTG:disk_free_trend|0|2|sensor-1|Resolved|case=|handling=false_positive/|inherited=True
host-2|Application|Mem|44|1|Processing|case=case-with-row|handling=/|inherited=False
host-2|Application|None|55|1|Open|case=|handling=/|inherited=False
HOST-1|Application|Cpu|22|1|Open|case=|handling=/|inherited=False
""";

    public const string Digest = """
【新交辦】
  單號 1：Schannel 36888（2 台）
    主機：host-01、host-03
    交辦說明：備註-Schannel 36888
  單號 2：DCOM 10016（1 台）
    主機：host-01
    交辦說明：備註-DCOM 10016
  單號 3：Disk 7（1 台）
    主機：host-01
    交辦說明：備註-Disk 7

新交辦請回覆處理狀態：開啟單後依實際情形標示處理中、已處理或無法處理（無法處理請寫明原因）。
請至站台的「我的交辦」檢視上列單號。

""";
}
