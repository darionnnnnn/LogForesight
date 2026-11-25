namespace LogForesight.Tests.Authorization;

/// <summary>
/// 授權涵蓋守門的三份清單（與 <c>AuthorizationCoverageTests</c> 對帳）。
/// 鍵格式與 <c>Fixtures/controller-route-snapshot.txt</c> 相同：「方法 路由樣板」。
///
/// 全站 API／頁面 action 的涵蓋來源（聯集必須等於全部 action）：
/// <list type="number">
/// <item><see cref="ReadMatrix.Keys"/>：讀取矩陣（AuthorizationReadMatrix.cs）。</item>
/// <item><see cref="WriteMatrix.Keys"/>：寫入矩陣（AuthorizationWriteMatrix.cs），受限者能進的寫入端點。</item>
/// <item>有 <c>[Permission]</c>／<c>[RequireViewAll]</c> 標註、且受限 A 一定被擋（A 的能力集合不滿足）的 action：由角色閘門測試（AuthorizationRoleGateHttpTests）以反射資料驅動涵蓋，不逐條登錄。</item>
/// <item><see cref="Exempt"/>：免測並附理由（匿名端點、只碰自己的偏好、頁面殼）。</item>
/// </list>
/// 讀取矩陣、寫入矩陣與豁免三份互斥；有標註的 action 可同時在矩陣內（例如權限異動確認）。
/// 新增端點沒登錄就紅；清單裡的端點已不存在也紅。
/// </summary>
public static class AuthorizationCoverageLists
{
    public static readonly IReadOnlyDictionary<string, string> Exempt = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["POST /api/auth/login"] = "匿名登入端點（[AllowAnonymous]）；防護在 LoginThrottle 與 IdentityService，另有登入測試",
        ["GET /api/auth/options"] = "匿名：登入頁初始化，只回驗證方式與是否需要密碼",
        ["POST /api/csp-report"] = "匿名：瀏覽器自動回報 CSP 違規，防護在端點內（8KB 上限、每 IP 節流、去重）",
        ["GET /api/health"] = "匿名存活檢查，回應刻意不含內部細節",
        ["GET /login"] = "匿名頁面殼",
        ["GET /error"] = "匿名錯誤頁殼",
        ["POST /api/auth/logout"] = "只作用於目前登入者自己的 token，不碰業務資料",
        ["GET /api/auth/me"] = "只回目前登入者自己的身分與能力，不碰業務資料",
        ["GET /api/me/note-phrases"] = "使用者自己的偏好（常用語），不碰主機資料",
        ["GET /api/me/setup-guide"] = "使用者自己的偏好（導覽進度），不碰主機資料",
        ["GET /api/settings/display"] = "全站顯示偏好（郵件布林、字級等），不含主機或問題資料",
        ["GET /api/health/freshness"] = "全站排程新鮮度狀態，內容與使用者無關（快取鍵不帶授權維度的理由寫在控制器），不含主機資料",
        ["GET /api/help/ask-available"] = "說明書問答是否可用的旗標，不含業務資料",
        ["GET /api/help/manual"] = "操作說明書靜態內容，不含業務資料",
        ["POST /api/admin/settings/prtg-scope-purge/confirm"] = "刻意不掛 [Permission]（Maintain 撤銷後建立者仍要能讀進度），改在動作內以耐久授權與建立者檢查；他人或不存在的預覽一律 4xx，由 AuthorizationRoleGateHttpTests.Self_authorized_purge_endpoints_reject_foreign_callers 驗證",
        ["GET /api/admin/settings/prtg-scope-purge/jobs/{jobId}"] = "同上：動作內檢查作業建立者與讀取授權，由 Self_authorized_purge_endpoints_reject_foreign_callers 驗證",
        ["POST /api/admin/settings/prtg-scope-purge/jobs/{jobId}/resume"] = "同上：動作內檢查作業建立者與維護授權，由 Self_authorized_purge_endpoints_reject_foreign_callers 驗證",
        ["GET /"] = "頁面殼：只回 View，資料由 API 層授權（API 都在矩陣或待測清單）",
        ["GET /access-denied"] = "頁面殼：權限不足說明頁",
        ["GET /help/manual"] = "頁面殼：只回 View",
        ["GET /records"] = "頁面殼：只回 View，資料由 /api/records 授權（讀取矩陣）",
        ["GET /records/{hostId:long}/{date}"] = "頁面殼：只回 View，資料由 /api/records/{hostId}/{date} 授權（讀取矩陣）",
        ["GET /reports"] = "頁面殼：只回 View，資料由 /api/reports/summary 授權（讀取矩陣）",
        ["GET /hosts/{hostId:long}"] = "頁面殼：只回 View，資料由 /api/host-detail 授權（讀取矩陣）",
        ["GET /handlers/{userId:long}"] = "頁面殼：只回 View，資料由 /api/handlers 授權（讀取矩陣）",
        ["GET /work-orders/{id:long}"] = "頁面殼：只回 View，資料由 /api/work-orders/{id} 授權（讀取矩陣）",
        ["GET /api/handling/issue-cases/close-preview"] = "統一標記預覽（O1）：能力只要 Handle，受限 A 能進，所以角色閘門打不到；範圍在服務層——具 Assign 者不限，只具 Handle 者限自己負責的主機，非負責主機列入略過（原因「不是你負責的主機」），資料來源是 RecordRepository 的可見範圍。由 HandlingServiceTests 的 O1_ 系列驗證",
        ["POST /api/handling/issue-cases/bulk-close"] = "統一標記（O1）：同上；負責人只標到自己負責的主機、其他主機略過、帶機房結論（AutoApply）被拒（仍需 Assign）、不具 Handle 被擋（PermissionFilter 以真實 controller 標註驗證）、Admin 行為不變。由 HandlingServiceTests 的 O1_ 系列驗證",
        ["GET /permission-changes"] = "頁面殼：只回 View（掛 ConfirmPermission，受限 A 本來就有該能力，所以角色閘門打不到）；資料由 /api/permission-changes 授權（讀取矩陣）",
    };
}
