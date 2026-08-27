namespace LogForesight.Core.Analysis;

/// <summary>
/// 登入失敗欄位（LogonType／ReasonCode）的白話文字對照——全系統唯一出處。
/// 刻意是 public：判定器 <see cref="ResidualCredentialDetector"/> 是 internal、Web 看不到，
/// 但風險日詳情頁要顯示同一套白話文字，故比照 <see cref="CorrelationPatternIds"/> 的作法，
/// 把「兩邊共用的那一小塊」獨立成公開類別，而不是把整個判定器外露。
/// </summary>
public static class LoginFailureTextFormatter
{
    public static string FormatReason(string? reasonCode) => reasonCode?.ToLowerInvariant() switch
    {
        "bad_password" => "密碼錯誤",
        "password_expired" => "密碼已過期",
        "account_locked" => "帳號已鎖定",
        "account_disabled" => "帳號已停用",
        "account_locked_or_disabled" => "帳號已鎖定或停用",
        "account_expired" => "帳號已到期",
        "logon_time_restriction" => "登入時段限制",
        "workstation_restriction" => "工作站限制",
        _ => "原因不明"
    };

    public static string FormatLogonType(int logonType) => logonType switch
    {
        3 => "網路登入",
        4 => "排程工作",
        5 => "服務",
        _ => $"登入類型 {logonType}"
    };
}
