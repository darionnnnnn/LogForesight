namespace LogForesight.Tests;

/// <summary>改動前（HEAD eacb2448）的乾淨副本跑 <see cref="PrtgBatch2bScenarios"/> 取得的輸出。</summary>
internal static class PrtgBatch2bGoldens
{
    public const string PurgeEmpty = """
status=complete;stop=;delV=0;delS=0;remV=0;remS=0;recon=True;otherV=0;otherS=0;candV=0;candS=0;curV=600/600;curS=300/300
values=600/s11=200;states=300/s11=150
""";

    public const string PurgeCand = """
status=complete;stop=;delV=400;delS=150;remV=0;remS=0;recon=True;otherV=0;otherS=0;candV=400;candS=150;curV=600/600;curS=300/300
values=200/s11=200;states=150/s11=150
""";

    public const string PurgeCandFirst = """
status=running;stop=;delV=166;delS=125;remV=234;remS=25;recon=False;otherV=0;otherS=0;candV=400;candS=150;curV=250/600;curS=250/300;values=434/s11=200;states=175/s11=150
""";

    public const string PurgeEmptyScope = """
status=stopped;stop=scope-changed;delV=0;delS=0;remV=0;remS=0;recon=True;otherV=0;otherS=0;candV=0;candS=0;curV=0/600;curS=0/300
values=600/s11=200;states=300/s11=150
""";

    public const string Eval1 = """
== 20260830
merged=4;warn=規則代碼 down 有多條啟用規則，採用 builtin-prtg-down
1|11|down|1440|builtin-prtg-down-availability|availability|[1] 11（Ping）持續 Down 達 1440 分鐘（自 2026-08-29 21:00:00 起）；同裝置另有 3 顆 sensor 同時 Down 或震盪（已合併）
1|12|down|1440|builtin-prtg-down-availability|availability|[1] 12（Ping）持續 Down 達 1440 分鐘（自 2026-08-29 21:00:00 起）
1|16|warning|900|builtin-prtg-warning-disk|disk|[1] 16（SNMP Disk Free）持續 Warning 累計達 900 分鐘
2|21|down|1440|builtin-prtg-down|traffic|[2] 21（SNMP Traffic 64bit）持續 Down 達 1440 分鐘（自 2026-08-29 21:00:00 起）
2|22|down|1440|builtin-prtg-down|traffic|[2] 22（SNMP Traffic 64bit）持續 Down 達 1440 分鐘（自 2026-08-29 21:00:00 起）
3|32|down|1440|builtin-prtg-down|traffic|[3] 32（SNMP Traffic 64bit）持續 Down 達 1440 分鐘（自 2026-08-29 21:00:00 起）
4|41|down|1440|builtin-prtg-down-availability|availability|[4] 41（Ping）持續 Down 達 1440 分鐘（自 2026-08-29 21:00:00 起）；同裝置另有 1 顆 sensor 同時 Down 或震盪（已合併）
5||silent|1|builtin-prtg-silent||[5] 連線檢查正常，但其餘 1 個未暫停 sensor 皆為 Unknown 或無狀態
""";

    public const string Eval5 = """
== 20260830
merged=4;warn=規則代碼 down 有多條啟用規則，採用 builtin-prtg-down
1|11|down|1440|builtin-prtg-down-availability|availability|[1] 11（Ping）持續 Down 達 1440 分鐘（自 2026-08-29 21:00:00 起）；同裝置另有 3 顆 sensor 同時 Down 或震盪（已合併）
1|12|down|1440|builtin-prtg-down-availability|availability|[1] 12（Ping）持續 Down 達 1440 分鐘（自 2026-08-29 21:00:00 起）
1|16|warning|900|builtin-prtg-warning-disk|disk|[1] 16（SNMP Disk Free）持續 Warning 累計達 900 分鐘
2|21|down|1440|builtin-prtg-down|traffic|[2] 21（SNMP Traffic 64bit）持續 Down 達 1440 分鐘（自 2026-08-29 21:00:00 起）
2|22|down|1440|builtin-prtg-down|traffic|[2] 22（SNMP Traffic 64bit）持續 Down 達 1440 分鐘（自 2026-08-29 21:00:00 起）
3|32|down|1440|builtin-prtg-down|traffic|[3] 32（SNMP Traffic 64bit）持續 Down 達 1440 分鐘（自 2026-08-29 21:00:00 起）
4|41|down|1440|builtin-prtg-down-availability|availability|[4] 41（Ping）持續 Down 達 1440 分鐘（自 2026-08-29 21:00:00 起）；同裝置另有 1 顆 sensor 同時 Down 或震盪（已合併）
5||silent|1|builtin-prtg-silent||[5] 連線檢查正常，但其餘 1 個未暫停 sensor 皆為 Unknown 或無狀態
== 20260829
merged=4;warn=規則代碼 down 有多條啟用規則，採用 builtin-prtg-down
1|11|down|1440|builtin-prtg-down-availability|availability|[1] 11（Ping）持續 Down 達 1440 分鐘（自 2026-08-28 21:00:00 起）；同裝置另有 3 顆 sensor 同時 Down 或震盪（已合併）
1|12|down|1440|builtin-prtg-down-availability|availability|[1] 12（Ping）持續 Down 達 1440 分鐘（自 2026-08-28 21:00:00 起）
1|16|warning|900|builtin-prtg-warning-disk|disk|[1] 16（SNMP Disk Free）持續 Warning 累計達 900 分鐘
2|21|down|1440|builtin-prtg-down|traffic|[2] 21（SNMP Traffic 64bit）持續 Down 達 1440 分鐘（自 2026-08-28 21:00:00 起）
2|22|down|1440|builtin-prtg-down|traffic|[2] 22（SNMP Traffic 64bit）持續 Down 達 1440 分鐘（自 2026-08-28 21:00:00 起）
3|32|down|1440|builtin-prtg-down|traffic|[3] 32（SNMP Traffic 64bit）持續 Down 達 1440 分鐘（自 2026-08-28 21:00:00 起）
4|41|down|1440|builtin-prtg-down-availability|availability|[4] 41（Ping）持續 Down 達 1440 分鐘（自 2026-08-28 21:00:00 起）；同裝置另有 1 顆 sensor 同時 Down 或震盪（已合併）
== 20260828
merged=4;warn=規則代碼 down 有多條啟用規則，採用 builtin-prtg-down
1|11|down|1440|builtin-prtg-down-availability|availability|[1] 11（Ping）持續 Down 達 1440 分鐘（自 2026-08-27 21:00:00 起）；同裝置另有 3 顆 sensor 同時 Down 或震盪（已合併）
1|12|down|1440|builtin-prtg-down-availability|availability|[1] 12（Ping）持續 Down 達 1440 分鐘（自 2026-08-27 21:00:00 起）
1|16|warning|900|builtin-prtg-warning-disk|disk|[1] 16（SNMP Disk Free）持續 Warning 累計達 900 分鐘
2|21|down|1440|builtin-prtg-down|traffic|[2] 21（SNMP Traffic 64bit）持續 Down 達 1440 分鐘（自 2026-08-27 21:00:00 起）
2|22|down|1440|builtin-prtg-down|traffic|[2] 22（SNMP Traffic 64bit）持續 Down 達 1440 分鐘（自 2026-08-27 21:00:00 起）
3|32|down|1440|builtin-prtg-down|traffic|[3] 32（SNMP Traffic 64bit）持續 Down 達 1440 分鐘（自 2026-08-27 21:00:00 起）
4|41|down|1440|builtin-prtg-down-availability|availability|[4] 41（Ping）持續 Down 達 1440 分鐘（自 2026-08-27 21:00:00 起）；同裝置另有 1 顆 sensor 同時 Down 或震盪（已合併）
== 20260827
merged=4;warn=規則代碼 down 有多條啟用規則，採用 builtin-prtg-down
1|11|down|1440|builtin-prtg-down-availability|availability|[1] 11（Ping）持續 Down 達 1440 分鐘（自 2026-08-26 21:00:00 起）；同裝置另有 3 顆 sensor 同時 Down 或震盪（已合併）
1|12|down|1440|builtin-prtg-down-availability|availability|[1] 12（Ping）持續 Down 達 1440 分鐘（自 2026-08-26 21:00:00 起）
1|16|warning|900|builtin-prtg-warning-disk|disk|[1] 16（SNMP Disk Free）持續 Warning 累計達 900 分鐘
2|21|down|1440|builtin-prtg-down|traffic|[2] 21（SNMP Traffic 64bit）持續 Down 達 1440 分鐘（自 2026-08-26 21:00:00 起）
2|22|down|1440|builtin-prtg-down|traffic|[2] 22（SNMP Traffic 64bit）持續 Down 達 1440 分鐘（自 2026-08-26 21:00:00 起）
3|32|down|1440|builtin-prtg-down|traffic|[3] 32（SNMP Traffic 64bit）持續 Down 達 1440 分鐘（自 2026-08-26 21:00:00 起）
4|41|down|1440|builtin-prtg-down-availability|availability|[4] 41（Ping）持續 Down 達 1440 分鐘（自 2026-08-26 21:00:00 起）；同裝置另有 1 顆 sensor 同時 Down 或震盪（已合併）
== 20260826
merged=4;warn=規則代碼 down 有多條啟用規則，採用 builtin-prtg-down
1|11|down|1440|builtin-prtg-down-availability|availability|[1] 11（Ping）持續 Down 達 1440 分鐘（自 2026-08-25 21:00:00 起）；同裝置另有 3 顆 sensor 同時 Down 或震盪（已合併）
1|12|down|1440|builtin-prtg-down-availability|availability|[1] 12（Ping）持續 Down 達 1440 分鐘（自 2026-08-25 21:00:00 起）
1|16|warning|900|builtin-prtg-warning-disk|disk|[1] 16（SNMP Disk Free）持續 Warning 累計達 900 分鐘
2|21|down|1440|builtin-prtg-down|traffic|[2] 21（SNMP Traffic 64bit）持續 Down 達 1440 分鐘（自 2026-08-25 21:00:00 起）
2|22|down|1440|builtin-prtg-down|traffic|[2] 22（SNMP Traffic 64bit）持續 Down 達 1440 分鐘（自 2026-08-25 21:00:00 起）
3|32|down|1440|builtin-prtg-down|traffic|[3] 32（SNMP Traffic 64bit）持續 Down 達 1440 分鐘（自 2026-08-25 21:00:00 起）
4|41|down|1440|builtin-prtg-down-availability|availability|[4] 41（Ping）持續 Down 達 1440 分鐘（自 2026-08-25 21:00:00 起）；同裝置另有 1 顆 sensor 同時 Down 或震盪（已合併）
""";

    public const string Risk = """
101|高|1|pending|歷史風險待重評|True|orig|FC15C22C3DA6
1000|低|1|current||False|-|B9EC5E278C45
1001|低|1|current||False|-|D33EA2A120CA
1002|低|1|current||False|-|7E0C80995E59
""";

    public const string PurgeEmptyBatches = """
batches=3 reads=6
""";

    public const string PurgeCandBatches = """
batches=3 reads=8
""";
}
