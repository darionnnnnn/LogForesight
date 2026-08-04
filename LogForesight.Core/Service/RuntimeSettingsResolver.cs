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
    /// 就地覆寫 <paramref name="settings"/>.Ai 的 BaseUrl／ApiKey，並回傳依 DB 值算出的保留天數。
    /// DB 讀取失敗（例如尚未初始化）時安靜回退到 appsettings.json 內建預設值，不擋執行。
    /// </summary>
    public static RetentionOptions ApplySystemSettingsOverrides(AppSettings settings, ISystemSettingsStore systemSettingsStore)
    {
        var retention = new RetentionOptions();
        try
        {
            var systemSettings = systemSettingsStore.Get();

            // 「從未在設定頁存過」（UpdatedAt==null）與「存過但刻意清空」要分開：前者沿用
            // appsettings 的值（既有部署升級後行為不變），後者空字串＝真的停用 AI（設定頁明講留空停用），
            // AI 呼叫失敗時各日自動降級為統計模式，規則/趨勢/關聯偵測不受影響
            if (systemSettings.UpdatedAt != null)
                settings.Ai.BaseUrl = systemSettings.AiBaseUrl.Trim();
            if (CryptoHelper.IsEncrypted(systemSettings.AiApiKeyEnc))
                settings.Ai.ApiKey = CryptoHelper.Decrypt(systemSettings.AiApiKeyEnc);

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

            if (systemSettings.RiskyEventRetentionDays >= 1 && systemSettings.RiskyEventRetentionDays <= retention.RetentionDays)
            {
                retention = retention with { RiskyEventRetentionDays = systemSettings.RiskyEventRetentionDays };
            }
            else
            {
                Log.Warn("系統設定的風險 log 暫存保留天數（{RiskyEventRetentionDays}）超出合理範圍（1~{RetentionDays}），改用內建預設值。",
                    systemSettings.RiskyEventRetentionDays, retention.RetentionDays);
            }
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "讀取系統設定（AI 位址／金鑰／補充留存天數）失敗，改用內建預設值：{0}", ex.Message);
        }

        return retention;
    }
}
