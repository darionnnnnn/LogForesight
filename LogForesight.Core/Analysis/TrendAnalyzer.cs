namespace LogForesight.Core.Analysis;

public enum IssueTrend
{
    Unknown,    // 尚無歷史可比對
    New,        // 歷史中從未出現過
    Rising,     // 頻率明顯上升（今日 >= 歷史基準 2 倍且達最低次數門檻）
    Recurring,  // 歷史中重複出現，頻率相近
    Declining   // 頻率明顯下降
}

/// <summary>
/// 程式端的確定性頻率比對：拿當日各事件簽章的發生次數，對照前一日與近期歷史的基準值，
/// 標記趨勢並在頻率上升時自動升級嚴重度。趨勢偵測不依賴 AI——數字比較程式做得又快又準，
/// AI 只負責解讀「為什麼會上升、代表什麼」。
///
/// **歷史基準改用中位數**（回饋十三輪 E，取代原本的平均值）：單日爆量一次會把平均墊高到
/// 之後兩週都達不到 <see cref="RisingFactor"/> 倍門檻、告警因此靜音兩週——2000 台環境下
/// 事件量級差三個數量級，這個失真被放大。中位數對單一極端值不敏感，同一批比對邏輯
/// （Rising/Declining 門檻、整體錯誤量／稽核量突增）不必跟著改，只是「基準」這個數字
/// 的算法換了。<see cref="LogIssueSignature.HistoryDailyAverage"/> 屬性名維持不變
/// （ContentJson 序列化相容，舊紀錄的欄位值仍是當時寫入時的平均值，語意隨欄位一起讀出，
/// 不會說謊——舊紀錄未來再被讀取／重算時才會換成中位數）。
///
/// **整體錯誤量／稽核量的基準進一步改用「非零日」中位數**（回饋十四輪 A1）：簽章層的
/// 中位數天生只吃非零值（一個簽章的歷史紀錄只在它真的出現過的日子才有），但總量層原本是
/// 對含 0 的完整可靠歷史取中位數——錯誤只在部分日子出現的主機，中位數會落在 0，
/// 0 × <see cref="RisingFactor"/> 恆為 0，倍率條件恆真，規則悄悄退化成「今日 ≥10 筆」的
/// 固定門檻，且告警文字會誤導性地印出「基準 0 筆」。改成只用非零日計算後，兩層的基準
/// 語意才真正一致；歷史中一筆非零日都沒有時退回固定門檻，但文案誠實說「多數日無事件」。
/// </summary>
public static class TrendAnalyzer
{
    /// <summary>今日次數需達此值才可能被判為 Rising，避免 1 次變 2 次這種雜訊觸發告警</summary>
    private const int RisingMinCount = 5;

    /// <summary>今日次數達歷史基準的幾倍視為頻率上升</summary>
    private const double RisingFactor = 2.0;

    /// <summary>
    /// Low 簽章（升級前）頻率上升的「爆量例外」倍率門檻（回饋十六輪批次B-1）：Rising 嚴重度
    /// 閘門（見下方 preEscalationSeverity 判斷）要求升級前 &gt;= Medium 才產生告警，未命中任何
    /// 規則的簽章一律 Low，因此天生被閘門靜音——但一個平常量很小的未知簽章突然暴增，仍是
    /// 值得看見的訊號（見 docs/DETECTION-SPEC.md 的「Low 簽章趨勢出口」小節）。門檻刻意遠高於
    /// 一般 Rising 的 <see cref="RisingFactor"/>（2 倍）：Low 簽章天然雜訊多、日常 2~3 倍波動
    /// 很常見，只有真正的爆量（10 倍）才該打破閘門。
    /// </summary>
    private const double SurgeFactor = 10.0;

    /// <summary>爆量例外的絕對量門檻，與 <see cref="SurgeFactor"/> 滿足其一即觸發：歷史基準較大
    /// 時單靠倍率會把量級已經很誇張的暴增擋在外面（基準 15 → 10 倍要 150 筆，今日 100 筆的
    /// 真暴增反而不觸發），絕對量門檻兜底這種情境；基準很小時 10 倍本來就容易達到，
    /// 走倍率條件即可。</summary>
    private const int SurgeMinCount = 100;

    /// <summary>
    /// 為當日事件簽章標記趨勢，回傳程式比對出的頻率異常說明（給 prompt 與 console 告警用）。
    /// 不帶抑制旗標與結構化輸出的簡化版——內部委派到下方完整版，丟棄總量抑制與 refs 輸出，
    /// 讓既有呼叫端（不需要總量抑制／結構化導航的路徑）不用跟著改參數列表。
    /// </summary>
    public static List<string> Apply(List<LogIssueSignature> issues, List<DailyAnalysisRecord> history,
        DateTime targetDate, int todayErrorCount, int todayAuditCount) =>
        Apply(issues, history, targetDate, todayErrorCount, todayAuditCount, false, false, out _, out _);

    /// <summary>
    /// 完整版：多兩個總量抑制旗標（回饋十五輪 A-1），多兩個結構化輸出（回饋十五輪 A-5）。
    /// </summary>
    /// <param name="suppressErrorVolume">true＝本機有生效中的整體錯誤量突增抑制
    /// （RuleSuppression.TargetType=Volume, VolumeKind=error）：符合觸發條件的告警文字
    /// 改進 <paramref name="suppressedAlerts"/>，不進回傳值、不影響風險判定。</param>
    /// <param name="suppressAuditVolume">同上，對象是安全稽核事件量突增（VolumeKind=audit）。</param>
    /// <param name="suppressedAlerts">
    /// 因抑制設定（簽章級 <see cref="LogIssueSignature.Suppressed"/> 或上述總量旗標）未進入
    /// 回傳值、但原本會產生的告警文字——抑制關的是「要不要吵」不是「要不要記」，這份清單供
    /// 詳情頁「已抑制的告警」區塊誠實申報用，見 <see cref="DailyAnalysisRecord.SuppressedTrendAlerts"/>。
    /// </param>
    /// <param name="alertRefs">回傳值（未被抑制的告警）的結構化平行資料，同序、逐筆對應，
    /// 供詳情頁頁內導航——見 <see cref="DailyAnalysisRecord.TrendAlertRefs"/>。</param>
    public static List<string> Apply(List<LogIssueSignature> issues, List<DailyAnalysisRecord> history,
        DateTime targetDate, int todayErrorCount, int todayAuditCount,
        bool suppressErrorVolume, bool suppressAuditVolume,
        out List<string> suppressedAlerts, out List<TrendAlertRef> alertRefs)
    {
        var alerts = new List<string>();
        suppressedAlerts = new List<string>();
        alertRefs = new List<TrendAlertRef>();

        if (history.Count == 0)
        {
            foreach (var sig in issues)
            {
                sig.Trend = IssueTrend.Unknown;
            }
            return alerts;
        }

        // DataIncomplete 的日子（事件來源保留歷史不足以涵蓋整天）一律排除在基準計算外，
        // 否則不完整的一天會墊低/墊高平均值，讓之後的正常量被誤判為頻率異常（或反過來把真異常蓋掉）
        var reliableHistory = history.Where(h => !h.DataIncomplete).ToList();

        // 安全稽核事件量（AuditEventCount）幾乎全來自 Security log，該來源本次或歷史上無權限讀取時
        // 這個數字是假的零，不能拿來當基準
        var reliableAuditHistory = reliableHistory.Where(h => h.SecurityLogAvailable != false).ToList();

        var prevRecord = history.FirstOrDefault(h => h.Date.Date == targetDate.Date.AddDays(-1));

        foreach (var sig in issues)
        {
            // 只用「當天實際讀取了該頻道」的歷史日當基準，避免假性零（頻道當天沒讀到）把平均墊低，
            // 造成頻道恢復/上線後的正常量被誤判成「首次出現」或「頻率上升」。這是既有 Security 特例的
            // 一般化：Security 沿用 SecurityLogAvailable 語意，新頻道（Defender/RDP）自動享有同一保護。
            var relevantHistory = reliableHistory.Where(h => ChannelCoverage.WasRead(h, sig.LogName)).ToList();

            // 暖身期：該頻道可靠歷史不足 WarmupDays 天時，趨勢欄位照算（供紀錄與報表），但不產生
            // New/Rising 告警、不做嚴重度升級——防的是新頻道上線第一天「所有簽章都是首次出現」的
            // 切換日告警風暴。既有頻道的可靠歷史遠多於此值，channelWarmingUp 恆為 false，行為零改變。
            bool channelWarmingUp = relevantHistory.Count < ChannelCoverage.WarmupDays;

            var pastCounts = relevantHistory
                .Select(h => h.TopIssues.FirstOrDefault(i => SameIssue(i, sig)))
                .Where(m => m != null)
                .Select(m => m!.Count)
                .ToList();

            sig.DaysSeenInHistory = pastCounts.Count;
            sig.HistoryDailyAverage = pastCounts.Count > 0 ? Math.Round(Median(pastCounts), 1) : null;
            sig.PreviousDayCount = prevRecord == null
                ? null
                : prevRecord.TopIssues.FirstOrDefault(i => SameIssue(i, sig))?.Count ?? 0;

            // 「首次出現」是**存在性**判定，要看全部歷史（含不完整日）——不完整的日子出現過也是
            // 出現過。可靠歷史（relevantHistory）刻意排除不完整日以免墊歪「平均值」，但拿它來判
            // 「有沒有出現過」會誤把「只在不完整日出現過」的簽章當成首次，於是與 PreviousDayCount
            // 自相矛盾（趨勢說首次出現、卻有昨日次數）。兩者要用不同基準：平均看可靠、存在看全部。
            bool everSeen = history.Any(h => h.Date.Date < targetDate.Date &&
                                             h.TopIssues.Any(i => SameIssue(i, sig)));

            // 用物件版重載（含 EventKey 第五段）而非四參數版：Linux 簽章靠 EventKey 把「同一個
            // program 命中不同規則」區分成不同問題（見 SameIssue 的比對邏輯與 IssueDto.IssueKey
            // 的產生方式），四參數版會讓這裡的 key 與前端 issue.issueKey 對不上，點擊導航失效
            var issueKey = IssueSignatureKey.For(sig);

            // 被抑制的簽章仍照算趨勢欄位與嚴重度升級（落紀錄、供頻率報表使用），只是不加入告警
            // 回傳值——抑制關的是「要不要吵」，不是「要不要算」（見 docs/RULES-SPEC.md 的語意邊界）。
            // 文字一律算出來，再依 sig.Suppressed 決定進 alerts 或 suppressedAlerts，兩邊共用同一份
            // 組字邏輯，不會漂移不同步。
            if (pastCounts.Count == 0)
            {
                if (everSeen)
                {
                    // 曾出現過但可靠歷史為空（只在不完整/頻道未讀的日子出現）：不能宣稱首次出現，
                    // 也算不出可靠的頻率基準——標記為重複發生，不觸發「首次出現」告警與升級
                    sig.Trend = IssueTrend.Recurring;
                }
                else
                {
                    sig.Trend = IssueTrend.New;
                    // 暖身期不告警——新頻道上線第一天所有簽章都是首次出現，這是要防的切換日風暴
                    if (!channelWarmingUp)
                    {
                        string? text = null;
                        if (sig.Severity >= IssueSeverity.High)
                        {
                            text = $"首次出現：{sig.SourceEventLabel}（{sig.Severity}）今日 x{sig.Count}，近 {relevantHistory.Count} 日可靠歷史中從未發生";
                        }
                        // 首次出現且爆量的出口（回饋十七輪批次C）：Other 類簽章一律 Low，未命中任何
                        // 規則、上面的 High 門檻天生不會觸發——但一個從未出現過的未知簽章，第一天就
                        // 來 SurgeMinCount 筆以上，仍是值得看見的訊號（見 SurgeMinCount 說明；
                        // 首次出現沒有歷史基準可乘，只用絕對量門檻，不像 Rising 分支的 SurgeFactor
                        // 那樣還有倍率條件）。
                        else if (sig.Count >= SurgeMinCount)
                        {
                            text = $"首次出現且大量：{sig.SourceEventLabel}（{sig.Severity}）今日 x{sig.Count}，近 {relevantHistory.Count} 日可靠歷史中從未發生";
                        }

                        if (text != null)
                        {
                            if (sig.Suppressed)
                            {
                                suppressedAlerts.Add(text);
                            }
                            else
                            {
                                alerts.Add(text);
                                alertRefs.Add(new TrendAlertRef { Text = text, IssueKey = issueKey, Kind = TrendAlertKinds.Signature });
                            }
                        }
                    }
                }
            }
            else if (sig.Count >= RisingMinCount && sig.Count >= sig.HistoryDailyAverage * RisingFactor)
            {
                sig.Trend = IssueTrend.Rising;
                // 暖身期不升級嚴重度、不告警——新頻道歷史太短，倍率比較還不可靠
                if (!channelWarmingUp)
                {
                    // 三級化前（docs/archive/HISTORY.md #1，B1）：High 頻率上升會升級成 Critical，
                    // 直接讓當天判定為高風險日。嚴重度現在封頂 High，改用旗標達成同樣效果——
                    // 判定時機要在 Escalate 之前（看「升級前」是不是 High），
                    // 否則 Medium→High 這種正常升一級也會被誤判成「原本就是 High」
                    var preEscalationSeverity = sig.Severity;
                    if (sig.Severity == IssueSeverity.High) sig.ElevatesDayRisk = true;
                    sig.Severity = Escalate(sig.Severity);

                    // Rising 嚴重度閘門（回饋十五輪 A-4）：Trend/Escalate/ElevatesDayRisk 上面已經
                    // 照算不受影響（供紀錄與頻率報表），但 Low 簽章的頻率上升不產生告警文字、不拉高
                    // 風險——一個本來就不重要的簽章不該有能力把當天拉成中風險（見
                    // LogAnalysisService.ComputeRuleBasedRisk：trendAlerts.Count > 0 直接判中風險）。
                    // 門檻用升級前的嚴重度：Escalate 必定把 Low 拉到 Medium，若用升級後的值判斷，
                    // 這道閘門會恆真、形同沒做。
                    if (preEscalationSeverity >= IssueSeverity.Medium)
                    {
                        var prevText = sig.PreviousDayCount != null ? $"、昨日 x{sig.PreviousDayCount}" : "";
                        var text = $"頻率上升：{sig.SourceEventLabel} 今日 x{sig.Count}，近 {relevantHistory.Count} 日可靠歷史基準 x{sig.HistoryDailyAverage}{prevText}";
                        if (sig.Suppressed)
                        {
                            suppressedAlerts.Add(text);
                        }
                        else
                        {
                            alerts.Add(text);
                            alertRefs.Add(new TrendAlertRef { Text = text, IssueKey = issueKey, Kind = TrendAlertKinds.Signature });
                        }
                    }
                    // 爆量例外（回饋十六輪批次B-1，見 SurgeFactor／SurgeMinCount 說明）：升級前是
                    // Low，一般閘門本該靜音，但今日量體已達基準 10 倍或絕對量 100 筆——未命中任何
                    // 規則的簽章單日暴增仍該被看見（docs/DETECTION-SPEC.md「Low 簽章趨勢出口」）。
                    // 用「頻率暴增」與一般「頻率上升」區分文字，讓讀者知道這是走爆量例外進來的。
                    else if (sig.Count >= sig.HistoryDailyAverage * SurgeFactor || sig.Count >= SurgeMinCount)
                    {
                        var prevText = sig.PreviousDayCount != null ? $"、昨日 x{sig.PreviousDayCount}" : "";
                        var text = $"頻率暴增：{sig.SourceEventLabel} 今日 x{sig.Count}，近 {relevantHistory.Count} 日可靠歷史基準 x{sig.HistoryDailyAverage}{prevText}";
                        if (sig.Suppressed)
                        {
                            suppressedAlerts.Add(text);
                        }
                        else
                        {
                            alerts.Add(text);
                            alertRefs.Add(new TrendAlertRef { Text = text, IssueKey = issueKey, Kind = TrendAlertKinds.Signature });
                        }
                    }
                }
            }
            else if (sig.HistoryDailyAverage >= RisingMinCount && sig.Count * RisingFactor <= sig.HistoryDailyAverage)
            {
                sig.Trend = IssueTrend.Declining;
            }
            else
            {
                sig.Trend = IssueTrend.Recurring;
            }
        }

        // 整體錯誤量突增：個別事件都不顯眼、但總量暴增，也是異常訊號（例如大量不同來源同時出錯）
        // DataIncomplete 的日子排除在基準計算外，避免不完整的一天墊低基準。
        //
        // 基準改用「非零日中位數」（回饋十四輪 A1）：錯誤只在部分日子出現的主機，含零值的
        // 中位數＝0，0×RisingFactor 恆為 0，倍率條件恆真，規則退化成固定門檻「今日 ≥10 筆」，
        // 且告警文字會誤導性地印出「基準 0 筆」。與簽章層 pastCounts（天然只收非零日）語意對齊：
        // 只用實際發生過錯誤的日子算基準，才是「這台主機錯誤發生時通常幾筆」的正確度量。
        // 歷史中一筆非零日都沒有時（nonZeroErrorDays 為空）無基準可算，但這不代表不用管——
        // 平常零錯誤的主機突然冒出 ≥10 筆本來就值得一提，維持同一個絕對門檻觸發告警，
        // 只是文案誠實說「多數日無錯誤」，不再宣稱一個不存在的「基準 0 筆」。
        if (reliableHistory.Count > 0)
        {
            var nonZeroErrorDays = reliableHistory.Select(h => h.ErrorCount).Where(c => c > 0).ToList();
            string? text = null;
            if (nonZeroErrorDays.Count > 0)
            {
                var baselineErrors = Median(nonZeroErrorDays);
                if (todayErrorCount >= 10 && todayErrorCount >= baselineErrors * RisingFactor)
                {
                    text = $"整體錯誤量突增：今日 {todayErrorCount} 筆，近 {reliableHistory.Count} 日可靠歷史基準 {baselineErrors:0.#} 筆";
                }
            }
            else if (todayErrorCount >= 10)
            {
                text = $"整體錯誤量突增：近 {reliableHistory.Count} 日可靠歷史多數日無錯誤，今日出現 {todayErrorCount} 筆";
            }

            if (text != null)
            {
                if (suppressErrorVolume)
                {
                    suppressedAlerts.Add(text);
                }
                else
                {
                    alerts.Add(text);
                    alertRefs.Add(new TrendAlertRef { Text = text, IssueKey = null, Kind = TrendAlertKinds.VolumeError });
                }
            }
        }

        // 安全稽核事件總量突增：稽核事件（如 4625 登入失敗）不計入錯誤數，需獨立比對總量；
        // 額外排除 Security log 無權限的歷史日（假性零會把基準墊低）。基準同樣改用非零日中位數，
        // 理由與上方錯誤量突增一致。
        if (reliableAuditHistory.Count > 0)
        {
            var nonZeroAuditDays = reliableAuditHistory.Select(h => h.AuditEventCount).Where(c => c > 0).ToList();
            string? text = null;
            if (nonZeroAuditDays.Count > 0)
            {
                var baselineAudit = Median(nonZeroAuditDays);
                if (todayAuditCount >= 10 && todayAuditCount >= baselineAudit * RisingFactor)
                {
                    text = $"安全稽核事件量突增：今日 {todayAuditCount} 筆，近 {reliableAuditHistory.Count} 日可靠歷史基準 {baselineAudit:0.#} 筆，需留意入侵嘗試";
                }
            }
            else if (todayAuditCount >= 10)
            {
                text = $"安全稽核事件量突增：近 {reliableAuditHistory.Count} 日可靠歷史多數日無稽核事件，今日出現 {todayAuditCount} 筆，需留意入侵嘗試";
            }

            if (text != null)
            {
                if (suppressAuditVolume)
                {
                    suppressedAlerts.Add(text);
                }
                else
                {
                    alerts.Add(text);
                    alertRefs.Add(new TrendAlertRef { Text = text, IssueKey = null, Kind = TrendAlertKinds.VolumeAudit });
                }
            }
        }

        return alerts;
    }

    // EventKey 恆空的 Windows 事件兩邊都是 ""，這個條件對既有行為零影響；Linux 事件靠它把
    // 「同 program 命中不同規則」（docs/FEEDBACK-12-PLAN.md §4.2）當成不同問題比對趨勢
    private static bool SameIssue(LogIssueSignature a, LogIssueSignature b) =>
        a.LogName == b.LogName && a.Source == b.Source && a.EventId == b.EventId &&
        a.EntryType == b.EntryType && a.EventKey == b.EventKey;

    /// <summary>
    /// 封頂 High（docs/archive/HISTORY.md #1，B1 三級化前可升到 Critical）：
    /// 呼叫端在升級前先看「原本是不是 High」來決定要不要設 ElevatesDayRisk，
    /// 這裡只管數值本身不再往上跳。
    /// </summary>
    private static IssueSeverity Escalate(IssueSeverity s) =>
        s >= IssueSeverity.High ? IssueSeverity.High : s + 1;

    /// <summary>
    /// 歷史基準的中位數（回饋十三輪 E）：純算術，偶數筆數取中間兩值平均。呼叫端已保證非空清單
    /// （<see cref="Apply(List{LogIssueSignature},List{DailyAnalysisRecord},DateTime,int,int,bool,bool,out List{string},out List{TrendAlertRef})"/>
    /// 內三處呼叫點都先檢查 Count &gt; 0），這裡不重複防禦。
    /// </summary>
    private static double Median(IEnumerable<int> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2.0 : sorted[mid];
    }
}
