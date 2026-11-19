namespace LogForesight.Tests;

/// <summary>第 78 輪批次 4 改動前（HEAD 63f6e2ec）錄下的基準，見 <see cref="PrtgBatch4BaselineTests"/>；日期已換成相對天數。</summary>
internal static class PrtgBatch4Golden
{
    public static readonly Dictionary<(int Lookback, bool Policy), string> Pipeline = new()
    {
        [(1, false)] = Normalize("""
D-0|SRV-B|prtg:down:999|High|False|[DEV-B] Cpu-B3（SNMP CPU Load）持續 Down 達 1440 分鐘（自 D-1 03:00:00 起）
D-0|SRV-B|prtg:flapping:202|Medium|False|[DEV-B] Disk-B1（SNMP Disk Free）狀態頻繁震盪，當日 Down → Up 往返達 6 次
D-0|SRV-B|prtg:warning:203|High|False|[DEV-B] Disk-B2（SNMP Disk Free）持續 Warning 累計達 840 分鐘
D-0|SRV-C|prtg:silent:3|Medium|False|[DEV-C] 連線檢查正常，但其餘 2 個未暫停 sensor 皆為 Unknown 或無狀態
"""),
        [(1, true)] = Normalize("""
D-0|SRV-B|prtg:flapping:202|Medium|False|[DEV-B] Disk-B1（SNMP Disk Free）狀態頻繁震盪，當日 Down → Up 往返達 6 次
D-0|SRV-B|prtg:warning:203|High|False|[DEV-B] Disk-B2（SNMP Disk Free）持續 Warning 累計達 840 分鐘
D-0|SRV-C|prtg:silent:3|Medium|False|[DEV-C] 連線檢查正常，但其餘 2 個未暫停 sensor 皆為 Unknown 或無狀態
"""),
        [(5, false)] = Normalize("""
D-0|SRV-B|prtg:down:999|High|False|[DEV-B] Cpu-B3（SNMP CPU Load）持續 Down 達 1440 分鐘（自 D-1 03:00:00 起）；近 14 日第 2 次，連續第 2 日
D-0|SRV-B|prtg:flapping:202|Medium|False|[DEV-B] Disk-B1（SNMP Disk Free）狀態頻繁震盪，當日 Down → Up 往返達 6 次；近 14 日第 2 次，連續第 2 日
D-0|SRV-B|prtg:warning:203|High|False|[DEV-B] Disk-B2（SNMP Disk Free）持續 Warning 累計達 840 分鐘；近 14 日第 3 次，連續第 1 日
D-0|SRV-C|prtg:silent:3|Medium|False|[DEV-C] 連線檢查正常，但其餘 2 個未暫停 sensor 皆為 Unknown 或無狀態
D-1|SRV-B|prtg:down:999|High|False|[DEV-B] Cpu-B3（SNMP CPU Load）持續 Down 達 1260 分鐘（自 D-1 03:00:00 起）
D-1|SRV-B|prtg:flapping:202|Medium|False|[DEV-B] Disk-B1（SNMP Disk Free）狀態頻繁震盪，當日 Down → Up 往返達 6 次
D-2|SRV-A|prtg:down:101|High|True|[DEV-A] Ping-A（Ping）持續 Down 達 1440 分鐘（自 D-3 22:00:00 起）；近 14 日第 2 次，連續第 2 日
D-2|SRV-B|prtg:warning:203|High|False|[DEV-B] Disk-B2（SNMP Disk Free）持續 Warning 累計達 600 分鐘；近 14 日第 2 次，連續第 2 日
D-3|SRV-A|prtg:down:101|High|True|[DEV-A] Ping-A（Ping）持續 Down 達 120 分鐘（自 D-3 22:00:00 起）
D-3|SRV-B|prtg:down:201|High|False|[DEV-B] Cpu-B（SNMP CPU Load）持續 Down 達 1440 分鐘（自 D-4 20:00:00 起）；近 14 日第 2 次，連續第 2 日
D-3|SRV-B|prtg:warning:203|High|False|[DEV-B] Disk-B2（SNMP Disk Free）持續 Warning 累計達 240 分鐘
D-4|SRV-B|prtg:down:201|High|False|[DEV-B] Cpu-B（SNMP CPU Load）持續 Down 達 240 分鐘（自 D-4 20:00:00 起）
D-4|SRV-B|prtg:down:205|High|False|[DEV-B] Cpu-B2（SNMP CPU Load）持續 Down 達 1440 分鐘（自 D-5 23:00:00 起）
"""),
        [(5, true)] = Normalize("""
D-0|SRV-B|prtg:flapping:202|Medium|False|[DEV-B] Disk-B1（SNMP Disk Free）狀態頻繁震盪，當日 Down → Up 往返達 6 次；近 14 日第 2 次，連續第 2 日
D-0|SRV-B|prtg:warning:203|High|False|[DEV-B] Disk-B2（SNMP Disk Free）持續 Warning 累計達 840 分鐘；近 14 日第 3 次，連續第 1 日
D-0|SRV-C|prtg:silent:3|Medium|False|[DEV-C] 連線檢查正常，但其餘 2 個未暫停 sensor 皆為 Unknown 或無狀態
D-1|SRV-B|prtg:flapping:202|Medium|False|[DEV-B] Disk-B1（SNMP Disk Free）狀態頻繁震盪，當日 Down → Up 往返達 6 次
D-2|SRV-A|prtg:down:101|High|True|[DEV-A] Ping-A（Ping）持續 Down 達 1440 分鐘（自 D-3 22:00:00 起）；近 14 日第 2 次，連續第 2 日
D-2|SRV-B|prtg:warning:203|High|False|[DEV-B] Disk-B2（SNMP Disk Free）持續 Warning 累計達 600 分鐘；近 14 日第 2 次，連續第 2 日
D-3|SRV-A|prtg:down:101|High|True|[DEV-A] Ping-A（Ping）持續 Down 達 120 分鐘（自 D-3 22:00:00 起）
D-3|SRV-B|prtg:down:201|High|False|[DEV-B] Cpu-B（SNMP CPU Load）持續 Down 達 1440 分鐘（自 D-4 20:00:00 起）；近 14 日第 2 次，連續第 2 日
D-3|SRV-B|prtg:warning:203|High|False|[DEV-B] Disk-B2（SNMP Disk Free）持續 Warning 累計達 240 分鐘
D-4|SRV-B|prtg:down:201|High|False|[DEV-B] Cpu-B（SNMP CPU Load）持續 Down 達 240 分鐘（自 D-4 20:00:00 起）
D-4|SRV-B|prtg:down:205|High|False|[DEV-B] Cpu-B2（SNMP CPU Load）持續 Down 達 1440 分鐘（自 D-5 23:00:00 起）
"""),
    };

    public static readonly string Calibration = Normalize("""
sample|down|D-55|10|1001|1440
sample|down|D-30|10|1001|120
sample|flapping|D-10|10|1002|4
sample|down|D-5|10|1001|960
sample|warning|D-3|10|1002|240
sample|warning|D-2|10|1002|720
sample|down|D-0|10|1001|30
hit|{"RuleCode":"down","RuleId":"builtin-prtg-down-availability","SensorCategory":"availability","Threshold":30,"FindingCount":4}
hit|{"RuleCode":"warning","RuleId":"builtin-prtg-warning-disk","SensorCategory":"disk","Threshold":240,"FindingCount":2}
""");

    // raw string literal 不含結尾換行、換行依檔案而定：統一成 \n 並補上結尾換行，與測試台逐行輸出一致
    private static string Normalize(string text) => text.Replace("\r\n", "\n") + "\n";
}
