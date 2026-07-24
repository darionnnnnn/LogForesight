using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// <see cref="IAnalysisRecordStore"/> 的合約測試基底（docs/DB-PLAN.md 一致性機制 #3）。
/// 見 <see cref="EfAnalysisRecordStoreContractTests"/>：SQL 實作跑同一組案例，
/// 一致性由測試強制，不靠 code review 肉眼比對。
///
/// 尤其是 <see cref="IAnalysisRecordReader.ReadRecent"/> 的錨定窗語意：它決定趨勢基準的
/// 計算範圍，兩個後端只要有一點差異，同一天的分析在換後端後就會得出不同的風險判定。
/// </summary>
public abstract class AnalysisRecordStoreContractTests : IDisposable
{
    protected abstract IAnalysisRecordStore CreateStore();

    public virtual void Dispose() { }

    protected static DailyAnalysisRecord Record(DateTime date, string risk = "低") => new()
    {
        Date = date,
        RiskLevel = risk,
        Headline = $"{date:MM-dd}"
    };

    // ── ReadRecent：錨定日期窗 ────────────────────────────────────────────────

    [Fact]
    public void ReadRecent_取錨定日往回N天()
    {
        var store = CreateStore();
        var anchor = new DateTime(2026, 7, 21);
        store.Append(Record(anchor.AddDays(-1)));
        store.Append(Record(anchor.AddDays(-2)));

        Assert.Equal(2, store.ReadRecent(anchor, 3).Count);
    }

    /// <summary>
    /// 窗外的較舊紀錄不得補位。若實作寫成「最近 N 筆」，缺漏日會讓更舊的紀錄墊進來，
    /// 14 日平均的分母就不誠實了——原本該判「首次出現」的簽章會被誤判成「重複發生」。
    /// </summary>
    [Fact]
    public void ReadRecent_窗外較舊紀錄不回傳()
    {
        var store = CreateStore();
        var anchor = new DateTime(2026, 7, 21);
        store.Append(Record(anchor.AddDays(-1)));
        store.Append(Record(anchor.AddDays(-30)));

        var result = store.ReadRecent(anchor, 14);

        Assert.Single(result);
        Assert.Equal(anchor.AddDays(-1), result[0].Date.Date);
    }

    /// <summary>
    /// **中間缺漏日 bug 的迴歸測試**：回補流程會分析「已經有後續紀錄」的日子
    /// （某天執行中斷、之後幾天照常執行）。錨定日之後的紀錄若混進基準，
    /// 等於拿未來的資料判斷過去那一天——趨勢分析最不該發生的事。
    /// </summary>
    [Fact]
    public void ReadRecent_錨定日之後的紀錄不回傳()
    {
        var store = CreateStore();
        var anchor = new DateTime(2026, 7, 21);
        store.Append(Record(anchor.AddDays(-1)));
        store.Append(Record(anchor.AddDays(1)));
        store.Append(Record(anchor.AddDays(2)));

        var result = store.ReadRecent(anchor, 14);

        Assert.Single(result);
        Assert.Equal(anchor.AddDays(-1), result[0].Date.Date);
    }

    /// <summary>體檢在當日分析寫入之後執行，窗口必須含當天剛寫入的那筆</summary>
    [Fact]
    public void ReadRecent_含錨定當日()
    {
        var store = CreateStore();
        var anchor = new DateTime(2026, 7, 21);
        store.Append(Record(anchor));

        Assert.Single(store.ReadRecent(anchor, 7));
    }

    [Fact]
    public void ReadRecent_依日期升冪()
    {
        var store = CreateStore();
        var anchor = new DateTime(2026, 7, 21);
        store.Append(Record(anchor.AddDays(-1)));
        store.Append(Record(anchor.AddDays(-3)));
        store.Append(Record(anchor.AddDays(-2)));

        var dates = store.ReadRecent(anchor, 7).Select(r => r.Date.Date).ToList();

        Assert.Equal(dates.OrderBy(d => d), dates);
    }

    /// <summary>窗長 N 含錨定日本身，所以 anchor-N 那天正好在窗外</summary>
    [Fact]
    public void ReadRecent_窗長邊界()
    {
        var store = CreateStore();
        var anchor = new DateTime(2026, 7, 21);
        store.Append(Record(anchor.AddDays(-6)));
        store.Append(Record(anchor.AddDays(-7)));

        var result = store.ReadRecent(anchor, 7);

        Assert.Single(result);
        Assert.Equal(anchor.AddDays(-6), result[0].Date.Date);
    }

    // ── 存在性判定 ───────────────────────────────────────────────────────────

    /// <summary>
    /// 「有沒有任何歷史」是獨立的問題，不能用 ReadRecent 的窗口間接表達——
    /// 錨定窗下 ReadRecent(today, 1) 問的是「今天有沒有紀錄」，語意完全不同。
    /// </summary>
    [Fact]
    public void HasAnyRecord_空為false_有紀錄為true()
    {
        var store = CreateStore();
        Assert.False(store.HasAnyRecord());

        store.Append(Record(DateTime.Today.AddDays(-30)));
        Assert.True(store.HasAnyRecord());
    }

    [Fact]
    public void HasRecord_同日不同時刻視為同一天()
    {
        var store = CreateStore();
        store.Append(Record(new DateTime(2026, 7, 20, 3, 15, 0)));

        Assert.True(store.HasRecord(new DateTime(2026, 7, 20, 23, 59, 0)));
        Assert.False(store.HasRecord(new DateTime(2026, 7, 21)));
    }

    // ── Prune ────────────────────────────────────────────────────────────────

    /// <summary>邊界日保留：cutoff 當天還在保留期內，不該被誤刪</summary>
    [Fact]
    public void Prune_保留天數邊界_cutoff當天保留()
    {
        var store = CreateStore();
        store.Append(Record(DateTime.Today.AddDays(-90)));
        store.Append(Record(DateTime.Today.AddDays(-91)));

        var removed = store.Prune(90);

        Assert.Equal(1, removed);
        Assert.Equal(DateTime.Today.AddDays(-90), store.ReadRecent(DateTime.Today, 365).Single().Date.Date);
    }

    [Fact]
    public void Prune_無可刪除時回0且不動檔案()
    {
        var store = CreateStore();
        store.Append(Record(DateTime.Today));

        Assert.Equal(0, store.Prune(90));
        Assert.Single(store.ReadRecent(DateTime.Today, 7));
    }

    // ── 週體檢附掛 ───────────────────────────────────────────────────────────

    [Fact]
    public void 週體檢附掛_更新既有當日紀錄且LastWeeklyCheckupDate可讀回()
    {
        var store = CreateStore();
        var date = DateTime.Today;
        store.Append(Record(date));

        Assert.Null(store.LastWeeklyCheckupDate());

        store.AttachWeeklyCheckup(date, new WeeklyCheckupResult
        {
            CheckupDate = date,
            HasFindings = true,
            Conclusion = "本週磁碟錯誤緩慢上升"
        });

        var read = Assert.Single(store.ReadRecent(date, 1));
        Assert.NotNull(read.WeeklyCheckup);
        Assert.True(read.WeeklyCheckup!.HasFindings);
        Assert.Equal("本週磁碟錯誤緩慢上升", read.WeeklyCheckup.Conclusion);
        Assert.Equal(date.Date, store.LastWeeklyCheckupDate());
    }

    /// <summary>
    /// 找不到對應日期時「安靜略過」是**契約**不是實作巧合：呼叫端在分析寫入之後才附掛，
    /// 理論上必定找得到；真的找不到時中斷整個批次比留下一筆 WARN 更糟。
    /// </summary>
    [Fact]
    public void AttachWeeklyCheckup_日期不存在_不擲例外且不新增紀錄()
    {
        var store = CreateStore();
        store.Append(Record(DateTime.Today));

        store.AttachWeeklyCheckup(DateTime.Today.AddDays(-5), new WeeklyCheckupResult
        {
            CheckupDate = DateTime.Today.AddDays(-5),
            Conclusion = "不該被寫入"
        });

        Assert.Single(store.ReadRecent(DateTime.Today, 30));
        Assert.Null(store.LastWeeklyCheckupDate());
    }

    // ── 儲存整形（RecordStorageShaper 是共用規則，兩後端都必須一致）─────────

    [Fact]
    public void 無風險日精簡_保留計數與趨勢數字但砍除範例訊息與KeyDetails()
    {
        var store = CreateStore();
        store.Append(new DailyAnalysisRecord
        {
            Date = DateTime.Today,
            RiskLevel = "低",
            ErrorCount = 3,
            TopIssues = new List<LogIssueSignature>
            {
                new()
                {
                    LogName = "System", Source = "disk", EventId = 153, Count = 7,
                    Severity = IssueSeverity.Critical, Category = IssueCategory.Storage,
                    FirstSeen = "01:00", LastSeen = "05:00",
                    DistinctMessageCount = 4, HistoryDailyAverage = 3.5, DaysSeenInHistory = 5,
                    SampleMessages = new List<string> { "sample A", "sample B", "sample C" },
                    KeyDetails = "相關帳號(2個): admin, guest"
                }
            }
        });

        var issue = Assert.Single(Assert.Single(store.ReadRecent(DateTime.Today, 1)).TopIssues);

        // 數字全留（趨勢基準所需）
        Assert.Equal(7, issue.Count);
        Assert.Equal(IssueSeverity.Critical, issue.Severity);
        Assert.Equal(4, issue.DistinctMessageCount);
        Assert.Equal(3.5, issue.HistoryDailyAverage);
        Assert.Equal(5, issue.DaysSeenInHistory);
        Assert.Equal("01:00", issue.FirstSeen);

        // 文字砍掉（體積大戶，無風險日基準用不到）
        Assert.Empty(issue.SampleMessages);
        Assert.Null(issue.KeyDetails);
    }

    [Fact]
    public void 風險中以上_完整保留範例訊息與KeyDetails()
    {
        var store = CreateStore();
        store.Append(new DailyAnalysisRecord
        {
            Date = DateTime.Today,
            RiskLevel = "高",
            TopIssues = new List<LogIssueSignature>
            {
                new()
                {
                    LogName = "System", Source = "disk", EventId = 153, Count = 7,
                    Severity = IssueSeverity.Critical, Category = IssueCategory.Storage,
                    SampleMessages = new List<string> { "sample A", "sample B" },
                    KeyDetails = "相關帳號(2個): admin, guest"
                }
            }
        });

        var issue = Assert.Single(store.ReadRecent(DateTime.Today, 1).Single().TopIssues);

        Assert.Equal(2, issue.SampleMessages.Count);
        Assert.Equal("相關帳號(2個): admin, guest", issue.KeyDetails);
    }

    [Fact]
    public void HostId與Host與DeepDives可完整序列化與讀回()
    {
        var store = CreateStore();
        store.Append(new DailyAnalysisRecord
        {
            Date = DateTime.Today,
            HostId = 42,
            Host = "SRV-DB01",
            RiskLevel = "高",
            TopIssues = new List<LogIssueSignature>
            {
                new() { LogName = "System", Source = "disk", EventId = 153, Count = 1 }
            },
            DeepDives = new List<CategoryDeepDive>
            {
                new()
                {
                    Category = IssueCategory.Storage,
                    Findings = new List<DeepDiveFinding>
                    {
                        new()
                        {
                            Problem = "磁碟壞軌", Impact = "資料遺失風險",
                            LikelyCauses = new() { "硬碟老化" }, NextSteps = new() { "更換硬碟" }
                        }
                    }
                }
            }
        });

        var read = Assert.Single(store.ReadRecent(DateTime.Today, 1));

        Assert.Equal(42, read.HostId);
        Assert.Equal("SRV-DB01", read.Host);
        var deepDive = Assert.Single(read.DeepDives);
        Assert.Equal(IssueCategory.Storage, deepDive.Category);
        var finding = Assert.Single(deepDive.Findings);
        Assert.Equal("磁碟壞軌", finding.Problem);
        Assert.Equal("更換硬碟", finding.NextSteps.Single());
    }
}
