using System.Text.Json;
using System.Text.Json.Serialization;

namespace LogForesight.Core.Persistence;

/// <summary>
/// webdata 儲存層共用的 <see cref="JsonSerializerOptions"/>。原本每個 store 各自 new 一份，
/// 10 餘處逐字相同的設定散落各檔——這裡收斂成兩份唯一版本，其餘與這兩份不完全相同的
/// （AuditLogStore／KnownIssueRuleStore／SuppressionStore／IRuleSeedStore 的
/// RuleJsonOptions）刻意不歸併，各自的差異是真實需求（列舉轉換、註解／結尾逗號容忍等），
/// 硬併會改變行為。
/// </summary>
internal static class LfJsonOptions
{
    /// <summary>整份型（清單／單一物件）webdata blob 共用：縮排、大小寫不敏感、列舉存字串</summary>
    public static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>逐列 append-only log 共用：不縮排（每列一筆，縮排沒有意義）、無列舉欄位</summary>
    public static readonly JsonSerializerOptions Compact = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
