namespace LogForesight.Tests.Scenarios;

/// <summary>
/// 常用事件片段的建構輔助。每個輔助回傳一串依時間遞增、全部落在當天內的事件，
/// 同樣的參數永遠產生同樣的內容（確定性）。後續批次需要新片段時在這裡擴充。
/// </summary>
public static class ScenarioEvents
{
    /// <summary>某主機某天 N 筆磁碟錯誤（System／disk，預設 EventId 153＝I/O 重試；命中 builtin-storage-disk-io，重大旗標）</summary>
    public static List<ScenarioEvent> DiskErrors(int count, int eventId = 153, TimeSpan? start = null) =>
        Series(count, start, i => new ScenarioEvent(TimeSpan.Zero, "System", "disk", eventId, ScenarioEventLevel.Error,
            $"The IO operation at logical block address 0x{0x1000 + i:x} for Disk 0 (PDO name: \\Device\\0000{i % 7}) was retried."));

    /// <summary>某主機某天 N 筆登入失敗（Security 4625；達 10 筆命中 builtin-security-bruteforce-4625）</summary>
    public static List<ScenarioEvent> FailedLogons(int count, string account = "administrator", string sourceIp = "10.9.9.9",
        TimeSpan? start = null) =>
        Series(count, start, i => new ScenarioEvent(TimeSpan.Zero, "Security", "Microsoft-Windows-Security-Auditing", 4625,
            ScenarioEventLevel.FailureAudit,
            $"An account failed to log on.\nSubject:\n\tAccount Name:\t\t-\nAccount For Which Logon Failed:\n\tAccount Name:\t\t{account}\n" +
            $"Failure Information:\n\tFailure Reason:\t\tUnknown user name or bad password.\n\tStatus:\t\t\t0xC000006D\n" +
            $"Network Information:\n\tSource Network Address:\t{sourceIp}\n\tSource Port:\t\t{49000 + i}"));

    /// <summary>某台 Linux 主機某天 N 筆 sshd 登入失敗（10 筆命中 builtin-linux-ssh-bruteforce）</summary>
    public static List<ScenarioEvent> LinuxSshFailures(int count, string sourceIp = "10.9.9.9", TimeSpan? start = null) =>
        Series(count, start, i => new ScenarioEvent(TimeSpan.Zero, "Linux", "sshd", 0, ScenarioEventLevel.Information,
            $"Failed password for invalid user test{i} from {sourceIp} port {40000 + i} ssh2")
        { LinuxProgram = "sshd" });

    /// <summary>通用：N 筆同一種事件（訊息帶序號，維持確定性）</summary>
    public static List<ScenarioEvent> Repeat(int count, string logName, string source, int eventId, ScenarioEventLevel level,
        string message, TimeSpan? start = null) =>
        Series(count, start, i => new ScenarioEvent(TimeSpan.Zero, logName, source, eventId, level, $"{message} (#{i})"));

    private static List<ScenarioEvent> Series(int count, TimeSpan? start, Func<int, ScenarioEvent> make)
    {
        var begin = start ?? TimeSpan.FromHours(1);
        // 全部事件塞進 20 小時內，且每筆至少間隔 1 秒；單日超過 72000 筆的情境不在本驅動器的範圍
        var stepSeconds = count <= 1 ? 0 : Math.Clamp(20 * 3600 / count, 1, 120);
        var list = new List<ScenarioEvent>(count);
        for (var i = 0; i < count; i++)
        {
            var at = begin + TimeSpan.FromSeconds((long)stepSeconds * i);
            if (at >= TimeSpan.FromHours(24)) at = TimeSpan.FromHours(24) - TimeSpan.FromSeconds(1);
            list.Add(make(i) with { At = at });
        }
        return list;
    }
}
