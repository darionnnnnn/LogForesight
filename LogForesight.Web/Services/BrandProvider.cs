using LogForesight.Web.Models;

namespace LogForesight.Web.Services;

/// <summary>
/// 版面要顯示的品牌三元素（docs/archive/FEEDBACK-10-PLAN.md §1）。
/// <see cref="IconDataUri"/> 為 null 代表沒有自訂圖示，版面沿用內建的向量圖示。
/// </summary>
public record BrandInfo(string Name, string? Subtitle, string? IconDataUri);

/// <summary>
/// 版面（_Layout／Login）取得品牌設定的窄介面。
///
/// 存在的理由是**分層**：View 不該直接注入 <c>ISystemSettingsStore</c>（那是 Persistence，
/// 且整份設定含 AD／AI 金鑰狀態等與版面無關的內容）。這裡只讓 View 看到它真正需要的三個值，
/// 並在此收斂「空值該回退成什麼」的規則，不讓每個 View 各寫一次 <c>?? "LogForesight"</c>。
///
/// 伺服器端渲染而非前端 fetch：側欄品牌是每頁第一眼看到的東西，
/// 等 <c>/api/auth/me</c> 之類的請求回來才替換會造成可見的閃動。
/// </summary>
public interface IBrandProvider
{
    BrandInfo Get();
}

public class BrandProvider : IBrandProvider
{
    private readonly ISystemSettingsStore _settings;

    /// <summary>Scoped 生命週期下的每請求快取：主版面與登入頁各只問一次，
    /// 但將來若有第二個消費端（例如錯誤頁）也不會重複讀 blob</summary>
    private BrandInfo? _cached;

    public BrandProvider(ISystemSettingsStore settings) => _settings = settings;

    public BrandInfo Get()
    {
        if (_cached != null) return _cached;

        var factory = new SystemSettings();

        SystemSettings s;
        try
        {
            s = _settings.Get();
        }
        catch
        {
            // 品牌是裝飾，**不該成為頁面渲染失敗的理由**：主版面現在每頁都會問一次，
            // 資料庫不通時若讓它往上拋，連「沒有權限」「找不到」這種不需要資料的頁面都會變成 500。
            // 退回出廠值讓版面照常渲染；真正的資料庫問題會由該頁自己的查詢誠實報出來。
            _cached = new BrandInfo(factory.BrandName, factory.BrandSubtitle, null);
            return _cached;
        }

        // 名稱空白時回退到型別預設值（而非在這裡寫死字串）——出廠名只有 SystemSettings 一份。
        // 副標與圖示空白是合法選擇（刻意只留產品名／沿用內建圖示），回 null 讓 View 直接略過該節點。
        _cached = new BrandInfo(
            Name: string.IsNullOrWhiteSpace(s.BrandName) ? factory.BrandName : s.BrandName,
            Subtitle: string.IsNullOrWhiteSpace(s.BrandSubtitle) ? null : s.BrandSubtitle,
            IconDataUri: string.IsNullOrWhiteSpace(s.BrandIconDataUri) ? null : s.BrandIconDataUri);

        return _cached;
    }
}
