using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// `KnownIssueCatalog.Rules`/`SecurityAuditWatchlist` 是可被 `Initialize()` 覆寫的共用靜態狀態
/// （2026-07-21 規則外部化引入，見 docs/RULES-PLAN.md）。xUnit 預設不同測試類別之間會平行執行，
/// 任何呼叫 `Initialize` 的測試都可能與同時執行、依賴預設完整規則表的測試（如
/// `RiskReportServiceTests` 透過 `KnownIssueCatalog.FindRule` 查表）互相干擾，造成間歇性失敗。
/// 把所有會讀寫這份共用狀態的測試類別放進同一個 collection，xUnit 保證同一 collection 內
/// 序列執行，其餘不相關的測試類別不受影響、仍可平行執行。
/// </summary>
[CollectionDefinition("KnownIssueCatalogState")]
public class KnownIssueCatalogStateCollection
{
}
