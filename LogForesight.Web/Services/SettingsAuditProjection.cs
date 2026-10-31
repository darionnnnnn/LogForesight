using System.Security.Cryptography;
using System.Text;
using LogForesight.Core.Models;

namespace LogForesight.Web.Services;

/// <summary>
/// 系統設定的稽核投影（Before／After 共用）。機密欄位（API 金鑰、SMTP／PRTG 密碼與 token）不出現值，
/// 呼叫端另以 *Changed 布林或 PrtgHas* 表達；品牌圖只記 SHA-256 前 12 碼；URL 照舊遮罩帳密。
/// 新增 <see cref="SystemSettings"/> 欄位時要同步加進來，SettingsAuditProjectionTests 以反射守門。
/// </summary>
public static class SettingsAuditProjection
{
    public static object From(SystemSettings s) => new
    {
            s.BrandName,
            s.BrandSubtitle,
            s.UnhandledSeverities,
            s.SeverityDisplayMode,
            s.VisibleDayRiskLevels,
            s.DefaultNotePhrases,
            s.AiProvider,
            AiBaseUrl = UrlSecrets.Mask(s.AiBaseUrl),
            s.AiModel,
            s.AiAzureDeployment,
            s.AiAzureApiVersion,
            s.AiTimeoutSeconds,
            s.AiRetryCount,
            s.AiRetryDelaySeconds,
            s.AiJsonRetryCount,
            s.AiMaxTokens,
            s.AiDeepDiveMaxTokens,
            s.AiInputPricePerMillion,
            s.AiOutputPricePerMillion,
            s.AiFrequencyPenalty,
            s.AiPresencePenalty,
            s.AiExtraRequestFieldsJson,
            s.WatchedFolders,
            s.ServerDescription,
            s.CheckupIntervalDays,
            s.AnalysisChannels,
            s.PermissionOperatorFields,
            s.PermissionMemberFields,
            s.PermissionGroupFields,
            s.PermissionObjectFields,
            s.ImportMaxFileSizeKb,
            s.ImportMaxRows,
            s.InitialHistoryDays,
            s.RetentionDays,
            s.RunLogRetentionDays,
            s.AuditRetentionDays,
            s.RawEventRetentionDays,
            s.ReportRetentionDays,
            s.AdAuthEnabled,
            s.AdServers,
            s.AdSearchBase,
            s.AdSearchFilter,
            s.AccountDisplayRules,
            s.MailEnabled,
            s.SmtpServer,
            s.SmtpPort,
            s.SmtpUseTls,
            s.SmtpAccount,
            s.MailFrom,
            s.MailRecipients,
            s.MailNotifyHostOwners,
            s.MailMinRiskLevel,
            s.MailOnRunCompleted,
            s.MailDailyEnabled,
            s.MailDailyTime,
            s.MailWeeklyEnabled,
            s.MailWeeklyDayOfWeek,
            s.MailWeeklyTime,
            s.MailUrgentEnabled,
            s.MailSubjectTemplate,
            s.MailBodyIntro,
            s.MailDigestSkipEmpty,
            s.MailNotifyWorkOrders,
            s.MailWorkOrderDigestTimes,
            s.AutoDispatchEnabled,
            s.PrtgEnabled,
            PrtgUrl = UrlSecrets.Mask(s.PrtgUrl),
            s.PrtgAuthMode,
            s.PrtgUsername,
            s.PrtgIgnoreSslErrors,
            s.PrtgTimeoutSeconds,
            s.PrtgFetchConcurrency,
            s.PrtgBackfillDays,
            s.PrtgRetentionDays,
            PrtgSensorTypeWhitelist = string.Join(", ", s.PrtgSensorTypeWhitelist),
            PrtgSensorTypeCategoryOverrides = string.Join(", ", s.PrtgSensorTypeCategoryOverrides),
            s.PrtgFetchStrategy,
            s.PrtgValueFetchScope,
            PrtgValueFetchExtraHosts = string.Join(", ", s.PrtgValueFetchExtraHosts),
            s.PrtgResourceGuardEnabled,
            PrtgResourceGuardSensorObjids = string.Join(", ", s.PrtgResourceGuardSensorObjids),
            s.PrtgResourceGuardCpuPercent,
            s.PrtgResourceGuardMemoryFreePercent,
            s.PrtgResourceGuardCheckSeconds,
            s.PrtgResourceGuardPauseMinutes,
            s.PrtgResourceGuardStrikes,
            s.PrtgResourceGuardMaxPauseMinutes,
            BrandIconSha12 = BrandIconFingerprint(s.BrandIconDataUri),
            PrtgHasApiToken = !string.IsNullOrEmpty(s.PrtgApiTokenEnc),
            PrtgHasPassword = !string.IsNullOrEmpty(s.PrtgPasswordEnc),
            PrtgHasPasshash = !string.IsNullOrEmpty(s.PrtgPasshashEnc)
    };

    private static string BrandIconFingerprint(string? dataUri)
    {
        if (string.IsNullOrEmpty(dataUri)) return "無";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(dataUri));
        return Convert.ToHexString(hash).ToLowerInvariant()[..12];
    }
}
