namespace LogForesight.Core.Persistence;

/// <summary>
/// 遷移失敗時存進狀態的說明。狀態會被遷移閘門的回應、健康頁與導覽告示讀出給使用者看，
/// 所以只存固定說明與例外類別名，例外原文（含資料表名、SQL、檔案路徑）只進記錄檔——在來源處理一次，
/// 三個出口就不必各自過濾。
/// </summary>
public static class MigrationFailureText
{
    public static string For(Exception exception) =>
        $"資料搬移失敗（{exception.GetType().Name}），詳細原因已寫入站台記錄檔，請聯繫系統管理員。";
}
