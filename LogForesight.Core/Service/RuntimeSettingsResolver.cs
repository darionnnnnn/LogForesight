using System.Text.Json;
using NLog;

namespace LogForesight.Core.Service;

/// <summary>
/// 「系統管理 > 設定」頁（DB）覆寫 <see cref="AppSettings"/> 的共用邏輯（docs/archive/WEB-SCHEDULER-PLAN.md
/// §1.4.2「每次執行重建服務」）：console 每次啟動、Web 排程每次觸發前都呼叫一次，才能反映
/// 使用者在設定頁剛存的新值，不會把啟動時的快照用到天荒地老。
///
/// 原本寫在批次 <c>Program.cs</c> 裡，Phase 3 抽出以供 Web 的 <c>SchedulerHostedService</c> 共用，
/// 避免兩處分別維護一份會漂移的合併邏輯。
/// </summary>
public static class RuntimeSettingsResolver
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// 就地覆寫 <paramref name="settings"/> 的 AI／權限監控／分析參數，並回傳依 DB 值算出的保留天數。
    /// DB 讀取失敗（例如尚未初始化）時安靜回退到程式內建預設值，不擋執行。
    ///
    /// §12（回饋第九輪）起，這些參數的**唯一事實來源是 DB**（「系統管理 > 設定」頁）——
    /// appsettings.json 的 Ai／Permissions／Analysis 區段已退役，傳入的 <paramref name="settings"/>
    /// 帶的是程式內建出廠值（＝原 appsettings 的值），本方法把 DB 值疊上去。
    /// </summary>
    public static RetentionOptions ApplySystemSettingsOverrides(AppSettings settings, ISystemSettingsStore systemSettingsStore)
    {
        var retention = new RetentionOptions();
        try
        {
            var systemSettings = systemSettingsStore.Get();

            // 「從未在設定頁存過」（UpdatedAt==null）與「存過但刻意清空」要分開：前者沿用
            // 內建預設值（既有部署升級後行為不變），後者空字串＝真的停用 AI（設定頁明講留空停用），
            // AI 呼叫失敗時各日自動降級為統計模式，規則/趨勢/關聯偵測不受影響
            if (systemSettings.UpdatedAt != null)
                settings.Ai.BaseUrl = systemSettings.AiBaseUrl.Trim();
            if (CryptoHelper.IsEncrypted(systemSettings.AiApiKeyEnc))
                settings.Ai.ApiKey = CryptoHelper.Decrypt(systemSettings.AiApiKeyEnc);

            settings.Ai.Provider = AiProviders.Normalize(systemSettings.AiProvider);
            settings.Ai.Model = string.IsNullOrWhiteSpace(systemSettings.AiModel)
                ? AiProviders.DefaultModel(settings.Ai.Provider)
                : systemSettings.AiModel.Trim();
            settings.Ai.AzureDeployment = systemSettings.AiAzureDeployment?.Trim() ?? "";
            settings.Ai.AzureApiVersion = string.IsNullOrWhiteSpace(systemSettings.AiAzureApiVersion)
                ? "2024-10-21"
                : systemSettings.AiAzureApiVersion.Trim();

            ApplyAiAdvanced(settings.Ai, systemSettings);

            // 權限監控資料夾與分析參數（§12）：DB 是唯一事實來源
            settings.Permissions.WatchedFolders = new List<string>(systemSettings.WatchedFolders);
            settings.Permissions.FieldMappings = PermissionFieldMappings.FromSystemSettings(systemSettings);
            settings.Analysis.ServerDescription = systemSettings.ServerDescription;
            if (systemSettings.CheckupIntervalDays >= 1)
                settings.Analysis.CheckupIntervalDays = systemSettings.CheckupIntervalDays;
            settings.Analysis.Channels = new List<string>(systemSettings.AnalysisChannels);

            if (systemSettings.RetentionDays >= systemSettings.InitialHistoryDays)
            {
                retention = retention with { InitialHistoryDays = systemSettings.InitialHistoryDays, RetentionDays = systemSettings.RetentionDays };
            }
            else
            {
                Log.Warn("系統設定的歷史資料保留天數（{RetentionDays}）小於首次回補天數（{InitialHistoryDays}），改用內建預設值。",
                    systemSettings.RetentionDays, systemSettings.InitialHistoryDays);
            }

            retention = retention with { RunLogRetentionDays = systemSettings.RunLogRetentionDays, AuditRetentionDays = systemSettings.AuditRetentionDays };

            if (systemSettings.RawEventRetentionDays >= 1 && systemSettings.RawEventRetentionDays <= retention.RetentionDays)
            {
                retention = retention with { RawEventRetentionDays = systemSettings.RawEventRetentionDays };
            }
            else
            {
                // 回退值同樣不可大於 RetentionDays（同 PrtgRetentionDays 的修正理由）：
                // 內建預設 120 在 RetentionDays 調成 90 時會違反「原始事件內容 ≤ 歷史資料」的不變式
                var fallback = Math.Min(SystemSettings.DefaultRawEventRetentionDays, retention.RetentionDays);
                Log.Warn("系統設定的原始事件內容保留天數（{RawEventRetentionDays}）超出合理範圍（1~{RetentionDays}），改用 {Fallback} 天。",
                    systemSettings.RawEventRetentionDays, retention.RetentionDays, fallback);
                retention = retention with { RawEventRetentionDays = fallback };
            }

            // 上限收斂為歷史資料保留天數：報告全文改存資料庫後，超過 RetentionDays 的報告
            // 在站上已無入口可點（分析紀錄已被清除），留著只是佔空間。
            // 舊部署可能存著大於 RetentionDays 的值（早期版本兩者互不約束），**取小**而不是
            // 退回預設值——退回預設會把使用者刻意調短的設定悄悄調長，方向剛好相反。
            if (systemSettings.ReportRetentionDays >= SystemSettings.MinRetentionDays)
            {
                var effective = Math.Min(systemSettings.ReportRetentionDays, retention.RetentionDays);
                if (effective != systemSettings.ReportRetentionDays)
                {
                    Log.Info("系統設定的報告保留天數（{ReportRetentionDays}）大於歷史資料保留天數（{RetentionDays}），" +
                             "本次以 {Effective} 天計算——超過歷史資料保留天數的報告在站上已無入口可點。",
                        systemSettings.ReportRetentionDays, retention.RetentionDays, effective);
                }
                retention = retention with { ReportRetentionDays = effective };
            }
            else
            {
                // 回退值同樣要與 RetentionDays 取小（同 PrtgRetentionDays 的修正理由）：
                // 只記 log 不改值會讓它停在內建預設 180，在 RetentionDays 較小時
                // 反而變成「報告留得比分析紀錄久」，違反上面那條上限收斂的不變式
                var fallback = Math.Min(SystemSettings.DefaultReportRetentionDays, retention.RetentionDays);
                Log.Warn("系統設定的報告保留天數（{ReportRetentionDays}）低於下限（{MinRetentionDays}），改用 {Fallback} 天。",
                    systemSettings.ReportRetentionDays, SystemSettings.MinRetentionDays, fallback);
                retention = retention with { ReportRetentionDays = fallback };
            }

            if (systemSettings.PrtgRetentionDays >= SystemSettings.MinRetentionDays)
            {
                var effective = Math.Min(systemSettings.PrtgRetentionDays, retention.RetentionDays);
                if (effective != systemSettings.PrtgRetentionDays)
                {
                    Log.Info("系統設定的 PRTG 保留天數（{PrtgRetentionDays}）大於歷史資料保留天數（{RetentionDays}），" +
                             "本次以 {Effective} 天計算——不可大於分析紀錄保留期。",
                        systemSettings.PrtgRetentionDays, retention.RetentionDays, effective);
                }
                retention = retention with { PrtgRetentionDays = effective };
            }
            else
            {
                // 真的要改成預設值，而且預設值同樣不可大於分析紀錄保留期——
                // 只記 log 不改值的話，低於下限的設定反而會讓它停在 record 預設的 180，
                // 在 RetentionDays 較小時變成「PRTG 留得比分析紀錄還久」，正好違反上面那條不變式。
                var fallback = Math.Min(SystemSettings.DefaultPrtgRetentionDays, retention.RetentionDays);
                Log.Warn("系統設定的 PRTG 保留天數（{PrtgRetentionDays}）低於下限（{MinRetentionDays}），改用 {Fallback} 天。",
                    systemSettings.PrtgRetentionDays, SystemSettings.MinRetentionDays, fallback);
                retention = retention with { PrtgRetentionDays = fallback };
            }
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "讀取系統設定（AI 參數／權限監控／分析參數／保留天數）失敗，改用內建預設值：{0}", ex.Message);
        }

        return retention;
    }

    /// <summary>
    /// AI 進階參數（§12：自 appsettings 的 Ai 區段遷入 DB）。各值只在合理範圍內才套用——
    /// 設定損毀（手改 DB、舊 blob 缺欄位反序列化成 0）不該讓 AI 呼叫變成 0 秒逾時這種更糟的狀態，
    /// 越界時保留內建出廠值並記警告，與其餘設定「壞值不擋執行」的一貫作法一致。
    ///
    /// public：Web 的互動情境（<c>WebAiService</c>）也要用同一份 DB 值，避免批次與互動兩處
    /// 各自解讀同一組設定而漂移。
    /// </summary>
    public static void ApplyAiAdvanced(AiSettings ai, SystemSettings db)
    {
        if (db.AiTimeoutSeconds >= 1) ai.TimeoutSeconds = db.AiTimeoutSeconds;
        if (db.AiRetryCount >= 0) ai.RetryCount = db.AiRetryCount;
        if (db.AiRetryDelaySeconds >= 0) ai.RetryDelaySeconds = db.AiRetryDelaySeconds;
        if (db.AiJsonRetryCount >= 0) ai.JsonRetryCount = db.AiJsonRetryCount;
        if (db.AiMaxTokens >= 0) ai.MaxTokens = db.AiMaxTokens;
        if (db.AiDeepDiveMaxTokens >= 0) ai.DeepDiveMaxTokens = db.AiDeepDiveMaxTokens;
        if (db.AiFrequencyPenalty >= 0) ai.FrequencyPenalty = db.AiFrequencyPenalty;
        if (db.AiPresencePenalty >= 0) ai.PresencePenalty = db.AiPresencePenalty;

        ai.ExtraRequestFields = ParseExtraRequestFields(db.AiExtraRequestFieldsJson);
    }

    /// <summary>
    /// 額外請求欄位的 JSON 文字 → 字典。空字串＝不附加任何欄位（合法的「清空」意圖，非錯誤）；
    /// 格式壞掉時記警告並回 null（等同不附加），不讓一段壞設定把整次分析擋下來。
    /// 設定頁存檔時已先驗證過格式，這裡是防禦性解析（手改 DB／舊資料）。
    /// </summary>
    private static Dictionary<string, JsonElement>? ParseExtraRequestFields(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
        }
        catch (JsonException ex)
        {
            Log.Warn("系統設定的「AI 額外請求欄位」不是合法的 JSON 物件，本次不附加任何欄位：{0}", ex.Message);
            return null;
        }
    }
}
