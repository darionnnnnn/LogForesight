using System.Diagnostics;

namespace LogForesight;

/// <summary>
/// --selftest：部署到新主機時先跑這個，確認確定性三層（規則/趨勢/關聯）在該環境正常，
/// 不用等真的出事才發現某條規則失效。刻意只測這三層——它們是純函數、不做 I/O，
/// 也正是換環境時最需要驗證的部分（AI 判讀本身跟主機環境無關，不在此範圍）。
/// 不寫 history、不呼叫 AI、不讀真實 Event Log，跑完印出應命中/實際命中的比對結果。
/// </summary>
public static class SelfTestRunner
{
    private static int _pass;
    private static int _fail;

    public static bool Run()
    {
        _pass = 0;
        _fail = 0;
        var sw = Stopwatch.StartNew();

        Console.WriteLine("=== LogForesight Self-Test（不寫入 history、不呼叫 AI、不讀取真實 Event Log）===\n");

        RunRuleLayerChecks();
        RunTrendLayerChecks();
        RunSlowTrendLayerChecks();
        RunCorrelationLayerChecks();

        Console.WriteLine($"\n=== 結果：{_pass} 通過、{_fail} 失敗（耗時 {sw.ElapsedMilliseconds}ms）===");
        return _fail == 0;
    }

    private static void Check(string name, bool condition, string detail = "")
    {
        if (condition)
        {
            _pass++;
            Console.WriteLine($"  ✓ {name}");
        }
        else
        {
            _fail++;
            Console.WriteLine($"  ✗ {name}｜{detail}");
        }
    }

    // ── 規則層：逐條規則自動產生剛好達到/低於門檻的合成事件，驗證分類與嚴重度 ──────────

    private static void RunRuleLayerChecks()
    {
        Console.WriteLine("-- 規則層（KnownIssueCatalog.Rules，共 " + KnownIssueCatalog.Rules.Count + " 條）--");

        foreach (var rule in KnownIssueCatalog.Rules)
        {
            var eventId = rule.EventIds.Length > 0 ? rule.EventIds[0] : 9999;
            var source = rule.SourcePattern;
            var entryType = source.Equals("Security-Auditing", StringComparison.OrdinalIgnoreCase) &&
                             eventId is 4625 or 4740
                ? EventLogEntryType.FailureAudit
                : source.Equals("Security-Auditing", StringComparison.OrdinalIgnoreCase)
                    ? EventLogEntryType.SuccessAudit
                    : EventLogEntryType.Error;

            // 剛好達到門檻：完整嚴重度
            var atThreshold = MakeEntries(rule.CountThreshold, source, eventId, entryType);
            var sigAtThreshold = LogAggregator.Aggregate(atThreshold)
                .FirstOrDefault(s => s.Source == source && s.EventId == eventId);
            Check($"{source} #{eventId} 達門檻(x{rule.CountThreshold}) → {rule.Category}/{rule.Severity}",
                sigAtThreshold != null && sigAtThreshold.Category == rule.Category && sigAtThreshold.Severity == rule.Severity,
                sigAtThreshold == null ? "未產生對應簽章" : $"實際={sigAtThreshold.Category}/{sigAtThreshold.Severity}");

            // 未達門檻時應降一級（僅門檻 > 1 的規則才有意義）
            if (rule.CountThreshold > 1)
            {
                var belowThreshold = MakeEntries(1, source, eventId, entryType);
                var sigBelow = LogAggregator.Aggregate(belowThreshold)
                    .FirstOrDefault(s => s.Source == source && s.EventId == eventId);
                var expected = rule.Severity == IssueSeverity.Low ? IssueSeverity.Low : rule.Severity - 1;
                Check($"{source} #{eventId} 未達門檻(x1 < {rule.CountThreshold}) → 降級為 {expected}",
                    sigBelow != null && sigBelow.Severity == expected,
                    sigBelow == null ? "未產生對應簽章" : $"實際={sigBelow.Severity}");
            }
        }
    }

    private static List<EventLogEntryData> MakeEntries(int count, string source, int eventId, EventLogEntryType entryType)
        => Enumerable.Range(0, count)
            .Select(i => new EventLogEntryData
            {
                TimeGenerated = DateTime.Today.AddMinutes(i),
                LogName = "System",
                Source = source,
                EventId = eventId,
                EntryType = entryType,
                Message = $"synthetic selftest event #{i}",
                InstanceId = eventId
            })
            .ToList();

    // ── 趨勢層：New / Rising / Declining / Recurring 四分支，以及 DataIncomplete / SecurityLogAvailable 基準排除 ──

    private static void RunTrendLayerChecks()
    {
        Console.WriteLine("\n-- 趨勢層（TrendAnalyzer）--");

        {
            var sig = Sig("System", "disk", 153, 5, IssueSeverity.Critical);
            TrendAnalyzer.Apply(new List<LogIssueSignature> { sig }, new List<DailyAnalysisRecord>(), DateTime.Today, 5, 0);
            Check("空歷史 → Trend=Unknown（尚無基準可比對）", sig.Trend == IssueTrend.Unknown);
        }
        {
            var history = Enumerable.Range(1, 14).Select(d => HistoryDay(DateTime.Today.AddDays(-d), "disk", 153, 2, IssueSeverity.High)).ToList();
            var sig = Sig("System", "disk", 153, 10, IssueSeverity.High);
            var alerts = TrendAnalyzer.Apply(new List<LogIssueSignature> { sig }, history, DateTime.Today, 10, 0);
            Check("14日平均x2、今日x10 → Rising 且嚴重度升級為 Critical",
                sig.Trend == IssueTrend.Rising && sig.Severity == IssueSeverity.Critical,
                $"Trend={sig.Trend}, Severity={sig.Severity}");
            Check("Rising 產生「頻率上升」告警文字", alerts.Any(a => a.Contains("頻率上升")));
        }
        {
            var history = Enumerable.Range(1, 5).Select(d => HistoryDay(DateTime.Today.AddDays(-d), "disk", 153, 20, IssueSeverity.High)).ToList();
            var sig = Sig("System", "disk", 153, 5, IssueSeverity.High);
            TrendAnalyzer.Apply(new List<LogIssueSignature> { sig }, history, DateTime.Today, 5, 0);
            Check("歷史平均x20、今日x5 → Declining", sig.Trend == IssueTrend.Declining, $"Trend={sig.Trend}");
        }
        {
            var history = Enumerable.Range(1, 5).Select(d => HistoryDay(DateTime.Today.AddDays(-d), "disk", 153, 4, IssueSeverity.High)).ToList();
            var sig = Sig("System", "disk", 153, 4, IssueSeverity.High);
            TrendAnalyzer.Apply(new List<LogIssueSignature> { sig }, history, DateTime.Today, 4, 0);
            Check("歷史與今日次數相近 → Recurring", sig.Trend == IssueTrend.Recurring, $"Trend={sig.Trend}");
        }
        {
            var incomplete = HistoryDay(DateTime.Today.AddDays(-1), "disk", 153, 0, IssueSeverity.High);
            incomplete.DataIncomplete = true;
            var normalDays = Enumerable.Range(2, 5).Select(d => HistoryDay(DateTime.Today.AddDays(-d), "disk", 153, 5, IssueSeverity.High));
            var history = new List<DailyAnalysisRecord> { incomplete }.Concat(normalDays).ToList();
            var sig = Sig("System", "disk", 153, 5, IssueSeverity.High);
            TrendAnalyzer.Apply(new List<LogIssueSignature> { sig }, history, DateTime.Today, 5, 0);
            Check("DataIncomplete 的歷史日被排除在基準外（平均應為 5，不被灌 0 拉低）",
                sig.HistoryDailyAverage == 5.0, $"實際平均={sig.HistoryDailyAverage}");
        }
        {
            var noSecurity = HistoryDay(DateTime.Today.AddDays(-1), "Security-Auditing", 4625, 0, IssueSeverity.High, "Security", IssueCategory.Security);
            noSecurity.SecurityLogAvailable = false;
            var normalDays = Enumerable.Range(2, 5)
                .Select(d => HistoryDay(DateTime.Today.AddDays(-d), "Security-Auditing", 4625, 10, IssueSeverity.High, "Security", IssueCategory.Security));
            var history = new List<DailyAnalysisRecord> { noSecurity }.Concat(normalDays).ToList();
            var sig = Sig("Security", "Security-Auditing", 4625, 10, IssueSeverity.High, IssueCategory.Security);
            TrendAnalyzer.Apply(new List<LogIssueSignature> { sig }, history, DateTime.Today, 0, 10);
            Check("Security 無權限的歷史日被排除在 Security 簽章基準外（平均應為 10）",
                sig.HistoryDailyAverage == 10.0, $"實際平均={sig.HistoryDailyAverage}");
        }
    }

    // ── 慢速趨勢層：近 7 天 vs 前 7 天總量比較（2026-07-20 新增，取代原週六全量體檢找慢速斜線的職責）──

    private static void RunSlowTrendLayerChecks()
    {
        Console.WriteLine("\n-- 慢速趨勢層（SlowTrendAnalyzer）--");

        // 視窗（targetDate=T）：近期＝今日＋T-1..T-6（7 天）、前期＝T-7..T-13（7 天），兩側等長
        {
            // 前 7 天每天 x1（合計 7），近期歷史 6 天每天 x2（合計 12）+ 今日 x5 → recentTotal=17 ≥ 7*1.5 且 ≥10
            var prior = Enumerable.Range(7, 7).Select(d => HistoryDay(DateTime.Today.AddDays(-d), "disk", 153, 1, IssueSeverity.Critical));
            var recent = Enumerable.Range(1, 6).Select(d => HistoryDay(DateTime.Today.AddDays(-d), "disk", 153, 2, IssueSeverity.Critical));
            var history = prior.Concat(recent).ToList();
            var sig = Sig("System", "disk", 153, 5, IssueSeverity.Critical, IssueCategory.Storage);

            var alerts = SlowTrendAnalyzer.Apply(new List<LogIssueSignature> { sig }, history, DateTime.Today);
            Check("近7天累計達前7天x1.5倍且達最低次數 → 觸發慢速惡化告警", alerts.Any(a => a.Contains("慢速惡化")));
        }
        {
            // 兩側視窗等長的不變量：每天固定 x3 的平穩訊號，倍率恰為 1.0，不得觸發
            var prior = Enumerable.Range(7, 7).Select(d => HistoryDay(DateTime.Today.AddDays(-d), "disk", 153, 3, IssueSeverity.Critical));
            var recent = Enumerable.Range(1, 6).Select(d => HistoryDay(DateTime.Today.AddDays(-d), "disk", 153, 3, IssueSeverity.Critical));
            var history = prior.Concat(recent).ToList();
            var sig = Sig("System", "disk", 153, 3, IssueSeverity.Critical, IssueCategory.Storage);

            var alerts = SlowTrendAnalyzer.Apply(new List<LogIssueSignature> { sig }, history, DateTime.Today);
            Check("平穩訊號（兩側視窗等長）不誤觸發", alerts.Count == 0, $"實際告警數={alerts.Count}");
        }
        {
            // 前期資料只有 5 天（不足 7 天），即使近期暴增也不比對，且回報「未評估」供呼叫端申報缺口
            var prior = Enumerable.Range(7, 5).Select(d => HistoryDay(DateTime.Today.AddDays(-d), "disk", 153, 1, IssueSeverity.Critical));
            var recent = Enumerable.Range(1, 6).Select(d => HistoryDay(DateTime.Today.AddDays(-d), "disk", 153, 20, IssueSeverity.Critical));
            var history = prior.Concat(recent).ToList();
            var sig = Sig("System", "disk", 153, 50, IssueSeverity.Critical, IssueCategory.Storage);

            var alerts = SlowTrendAnalyzer.Apply(new List<LogIssueSignature> { sig }, history, DateTime.Today, out bool evaluated);
            Check("前期資料不足七天時不比對且回報未評估", alerts.Count == 0 && !evaluated,
                $"實際告警數={alerts.Count}, evaluated={evaluated}");
        }
    }

    // ── 關聯層：13 種模式各自的最小觸發組合 ──────────────────────────────────

    private static void RunCorrelationLayerChecks()
    {
        Console.WriteLine("\n-- 關聯層（CorrelationAnalyzer，共 12 種模式）--");

        CheckPattern("【入侵鏈】", new()
        {
            Sig("Security", "Security-Auditing", 4625, 15, IssueSeverity.High, IssueCategory.Security),
            Sig("Security", "Security-Auditing", 4720, 1, IssueSeverity.High, IssueCategory.Security)
        });

        CheckPattern("【破解得手】",
            new() { Sig("Security", "Security-Auditing", 4625, 15, IssueSeverity.High, IssueCategory.Security) },
            match: new SuccessfulLogonMatch { MatchedAccounts = new() { "testuser" } });

        CheckPattern("【持久化】", new()
        {
            Sig("Security", "Security-Auditing", 4625, 15, IssueSeverity.High, IssueCategory.Security),
            Sig("System", "Service Control Manager", 7045, 1, IssueSeverity.High, IssueCategory.Security)
        });

        CheckPattern("【滅跡】", new()
        {
            Sig("Security", "Security-Auditing", 1102, 1, IssueSeverity.Critical, IssueCategory.Security),
            Sig("Security", "Security-Auditing", 4720, 1, IssueSeverity.High, IssueCategory.Security)
        });

        CheckPattern("【提權→植入】", new()
        {
            Sig("Security", "Security-Auditing", 4670, 1, IssueSeverity.High, IssueCategory.Security),
            Sig("System", "Service Control Manager", 7045, 1, IssueSeverity.High, IssueCategory.Security)
        });

        CheckPattern("【儲存連鎖】", new()
        {
            Sig("System", "disk", 153, 5, IssueSeverity.Critical, IssueCategory.Storage),
            Sig("System", "Ntfs", 55, 5, IssueSeverity.Critical, IssueCategory.Storage)
        });

        CheckPattern("【儲存→當機】", new()
        {
            Sig("System", "disk", 153, 5, IssueSeverity.Critical, IssueCategory.Storage),
            Sig("System", "Kernel-Power", 41, 1, IssueSeverity.Critical, IssueCategory.Hardware)
        });

        CheckPattern("【硬體不穩】", new()
        {
            Sig("System", "WHEA-Logger", 1, 5, IssueSeverity.Critical, IssueCategory.Hardware),
            Sig("System", "Kernel-Power", 41, 1, IssueSeverity.Critical, IssueCategory.Hardware)
        });

        CheckPattern("【崩潰→服務失敗】", new()
        {
            Sig("Application", "Application Error", 1000, 3, IssueSeverity.Medium, IssueCategory.Service),
            Sig("System", "Service Control Manager", 7031, 3, IssueSeverity.Medium, IssueCategory.Service)
        });

        CheckPattern("【崩潰循環→資源耗盡】", new()
        {
            Sig("System", "Service Control Manager", 7031, 100, IssueSeverity.Medium, IssueCategory.Service),
            Sig("System", "Resource-Exhaustion-Detector", 2004, 1, IssueSeverity.High, IssueCategory.Resource)
        });

        CheckPattern("【時間偏移→驗證失敗】", new()
        {
            Sig("System", "Time-Service", 29, 3, IssueSeverity.Medium, IssueCategory.Config),
            Sig("Security", "Security-Auditing", 4625, 15, IssueSeverity.High, IssueCategory.Security)
        });

        var yesterdayBrute = HistoryDay(DateTime.Today.AddDays(-1), "Security-Auditing", 4625, 15, IssueSeverity.High, "Security", IssueCategory.Security);
        CheckPattern("【跨日入侵鏈】",
            new() { Sig("Security", "Security-Auditing", 4720, 1, IssueSeverity.High, IssueCategory.Security) },
            history: new() { yesterdayBrute });

        var yesterdayStorage = HistoryDay(DateTime.Today.AddDays(-1), "disk", 153, 5, IssueSeverity.Critical, "System", IssueCategory.Storage);
        CheckPattern("【儲存持續劣化】",
            new() { Sig("System", "disk", 153, 5, IssueSeverity.Critical, IssueCategory.Storage) },
            history: new() { yesterdayStorage });
    }

    private static void CheckPattern(string pattern, List<LogIssueSignature> issues,
        List<DailyAnalysisRecord>? history = null, SuccessfulLogonMatch? match = null)
    {
        var findings = CorrelationAnalyzer.Detect(issues, history ?? new List<DailyAnalysisRecord>(), DateTime.Today, match);
        var hit = findings.Any(f => f.Description.Contains(pattern));
        var summary = string.Join("、", findings.Select(f => f.Description.Split('】')[0] + "】"));
        Check($"關聯模式 {pattern} 觸發", hit, hit ? "" : $"實際觸發：[{summary}]");
    }

    private static LogIssueSignature Sig(string logName, string source, int eventId, int count, IssueSeverity severity,
        IssueCategory category = IssueCategory.Other)
        => new()
        {
            LogName = logName,
            Source = source,
            EventId = eventId,
            EntryType = EventLogEntryType.Error,
            Count = count,
            Severity = severity,
            Category = category,
            FirstSeen = "00:00",
            LastSeen = "23:59"
        };

    private static DailyAnalysisRecord HistoryDay(DateTime date, string source, int eventId, int count, IssueSeverity severity,
        string logName = "System", IssueCategory category = IssueCategory.Other)
        => new()
        {
            Date = date.Date,
            RiskLevel = "低",
            TopIssues = new List<LogIssueSignature> { Sig(logName, source, eventId, count, severity, category) }
        };
}
