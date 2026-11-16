using System.Net;
using LogForesight.Web.Auth;
using Xunit;

namespace LogForesight.Tests.Authorization;

/// <summary>
/// 授權讀取矩陣：真 JWT＋真路由＋真 <c>VisibilityService</c>（站台組裝見 <see cref="AuthorizationHttpFixture"/>）。
///
/// 一張資料驅動的表（<see cref="ReadMatrix.Rows"/>）× 六種身分（A 受限、B 負責人、C 案件授與、Admin、
/// serverAdmin、匿名）× 四台主機（1、2、3 啟用，4 停用）。判準：
/// <list type="bullet">
/// <item>匿名：一律 401，回應不含任何哨兵。</item>
/// <item>受限者查未授權主機：編號直取回 404；清單／彙總回 200 但全文不含該主機的任何哨兵。</item>
/// <item>受限者查自己的主機：200 且含該主機的哨兵（證明判準比得到東西，不是空資料恆綠）。</item>
/// <item>Admin：啟用主機含哨兵；停用主機 4 的哨兵不得出現。</item>
/// <item>案件授與 C：主機 3 的問題 A 可見，其餘哨兵（整日敘事、問題 B、處理備註、報告全文…）不得出現。</item>
/// <item>serverAdmin：沒有任何業務資料檢視能力，等同看不到任何主機。</item>
/// </list>
/// 發現授權漏洞（受限者拿到未授權哨兵）時不修正式碼，測試保持紅燈並指出端點、身分與看到的哨兵。
/// </summary>
[Collection(AuthorizationHttpCollection.Name)]
public class AuthorizationReadMatrixHttpTests
{
    private readonly AuthorizationHttpFixture _fx;

    public AuthorizationReadMatrixHttpTests(AuthorizationHttpFixture fx) => _fx = fx;

    private static readonly string[] UserTargets = { "A受限", "B負責人", "C案件授與", "Admin" };

    private static readonly string[] IdentityNames = { "A受限", "B負責人", "C案件授與", "Admin", "serverAdmin", "anonymous" };

    public static IEnumerable<object[]> Cases()
    {
        foreach (var row in ReadMatrix.Rows)
        {
            foreach (var identity in IdentityNames)
            {
                switch (row.Scope)
                {
                    case RowScope.Global:
                        yield return new object[] { row.Key, identity, "" };
                        break;
                    case RowScope.Host:
                    case RowScope.HostSoft:
                        foreach (var n in new[] { 1, 2, 3, 4 }) yield return new object[] { row.Key, identity, n.ToString() };
                        break;
                    case RowScope.User:
                        foreach (var t in UserTargets) yield return new object[] { row.Key, identity, t };
                        break;
                    case RowScope.WorkOrder:
                        foreach (var t in new[] { "A", "C" }) yield return new object[] { row.Key, identity, t };
                        break;
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Row_enforces_authorization(string key, string identityName, string target)
    {
        var row = ReadMatrix.Rows.Single(r => r.Key == key);
        var id = _fx.Identity(identityName);
        HostFx? host = row.Scope is RowScope.Host or RowScope.HostSoft ? _fx.Host(int.Parse(target)) : null;
        IdentityFx? targetUser = row.Scope == RowScope.User ? _fx.Identity(target) : null;
        WorkOrderFx? order = row.Scope == RowScope.WorkOrder ? (target == "A" ? _fx.WorkOrderA : _fx.WorkOrderC) : null;

        var url = ReadMatrix.BuildUrl(row, _fx, host, targetUser, order);
        var (status, body) = await _fx.GetAsync(id, url);
        var problems = new List<string>();

        void Expect(bool ok, string message) { if (!ok) problems.Add(message); }
        void ExpectStatus(params HttpStatusCode[] allowed) =>
            Expect(allowed.Contains(status), $"狀態碼應為 {string.Join("/", allowed.Select(s => (int)s))}，實際 {(int)status}");

        // 回應全文對「某台主機」的洩漏檢查：依該身分對該主機的期望可見度
        void CheckLeak(int n)
        {
            if (row.AdminSeesInactive && id.Name == "Admin" && !_fx.Host(n).Active) return;
            switch (id.VisOf(n))
            {
                case Visibility.None:
                    Expect(!Sentinels.ContainsAny(body, n), $"不可見的主機 {n} 的哨兵出現在回應中：{Found(body, n)}");
                    break;
                case Visibility.CaseOnly:
                    foreach (var kind in ReadMatrix.CaseForbidden)
                        Expect(!Sentinels.Contains(body, n, kind), $"案件授與者看到不該看的哨兵 {Sentinels.Of(n, kind)}（{kind}）");
                    break;
            }
        }

        void CheckLeakAllHosts()
        {
            foreach (var h in _fx.Hosts) CheckLeak(h.N);
        }

        if (id.Anonymous)
        {
            ExpectStatus(HttpStatusCode.Unauthorized);
            foreach (var h in _fx.Hosts) Expect(!Sentinels.ContainsAny(body, h.N), $"匿名請求拿到主機 {h.N} 的哨兵：{Found(body, h.N)}");
            Expect(!body.Contains("SENTINEL-WO-", StringComparison.Ordinal), "匿名請求拿到交辦單哨兵");
        }
        else if (row.Gate != null && !row.Gate.Any(id.Has))
        {
            ExpectStatus(HttpStatusCode.Forbidden);
            CheckLeakAllHosts();
        }
        else
        {
            switch (row.Scope)
            {
                case RowScope.Global:
                    ExpectStatus(row.OkStatuses);
                    CheckLeakAllHosts();
                    if (status == HttpStatusCode.OK)
                    {
                        var needle = row.GlobalNeedle?.Invoke(id);
                        if (needle != null) Expect(body.Contains(needle, StringComparison.Ordinal), $"彙總回應應含 {needle}");
                        foreach (var h in _fx.Hosts.Where(h => id.VisOf(h.N) == Visibility.Full && row.Positive.Length > 0))
                            Expect(Sentinels.ContainsAnyOf(body, h.N, row.Positive),
                                $"完整可見的主機 {h.N} 沒有出現預期的哨兵（{string.Join("/", row.Positive)}）——判準比不到資料");
                    }
                    break;

                case RowScope.Host:
                    ExpectHost(row, id, host!, status, body, Expect, ExpectStatus);
                    if (status == HttpStatusCode.OK && row.ResponseCheck != null)
                    {
                        var problem = row.ResponseCheck(id, host, _fx.Hosts, body);
                        Expect(problem == null, problem ?? "");
                    }
                    if (id.VisOf(host!.N) == Visibility.Full && status == HttpStatusCode.OK && row.PositiveText != null)
                        Expect(body.Contains(row.PositiveText, StringComparison.Ordinal), $"回應應含 {row.PositiveText}");
                    CheckLeakAllHosts();
                    break;

                case RowScope.HostSoft:
                    ExpectStatus(HttpStatusCode.OK);
                    CheckLeakAllHosts();
                    if (status == HttpStatusCode.OK && row.ResponseCheck != null)
                    {
                        var problem = row.ResponseCheck(id, host, _fx.Hosts, body);
                        Expect(problem == null, problem ?? "");
                    }
                    // 完整可見的主機：回應要含預期哨兵（證明判準比得到東西）
                    if (status == HttpStatusCode.OK && id.VisOf(host!.N) == Visibility.Full && row.Positive.Length > 0)
                        Expect(Sentinels.ContainsAnyOf(body, host.N, row.Positive),
                            $"完整可見的主機 {host.N} 沒有出現預期的哨兵（{string.Join("/", row.Positive)}）——判準比不到資料");
                    break;

                case RowScope.User:
                {
                    var allowed = id.UserId == targetUser!.UserId && id.UserId > 0 || id.Has(Capability.Assign) || id.Has(Capability.ViewAll);
                    ExpectStatus(allowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden);
                    CheckLeakAllHosts();
                    var userNeedle = row.UserNeedle?.Invoke(targetUser!);
                    if (allowed && status == HttpStatusCode.OK && userNeedle != null)
                        Expect(body.Contains(userNeedle, StringComparison.Ordinal), $"預期出現 {userNeedle}，判準比不到資料");
                    // 案件授與者查自己的工作量：被交辦的問題 A（主機 3）要出現，證明判準比得到案件授與的資料
                    if (allowed && status == HttpStatusCode.OK && key.EndsWith("/workload", StringComparison.Ordinal)
                        && id.Name == "C案件授與" && targetUser.Name == "C案件授與")
                        Expect(Sentinels.Contains(body, 3, SentinelKind.IssueA), "案件授與者的工作量沒有出現被交辦的問題（主機 3 問題 A）");
                    break;
                }

                case RowScope.WorkOrder:
                {
                    var allowed = id.Has(Capability.Assign) || id.Has(Capability.ViewAll) || id.UserId == order!.HandlerId && id.UserId > 0;
                    ExpectStatus(allowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden);
                    CheckLeakAllHosts();
                    var needle = row.WorkOrderNeedle?.Invoke(order!);
                    if (allowed && status == HttpStatusCode.OK && needle != null)
                        Expect(body.Contains(needle, StringComparison.Ordinal), $"預期出現 {needle}，判準比不到資料");
                    if (!allowed)
                        Expect(!body.Contains("SENTINEL-WO-", StringComparison.Ordinal), "沒有權限的身分拿到交辦單哨兵");
                    break;
                }
            }
        }

        Assert.True(problems.Count == 0,
            $"[{key}] 身分={identityName} 目標={(target.Length == 0 ? "-" : target)}\nURL: {url}\n" +
            string.Join("\n", problems.Select(p => "  - " + p)) +
            $"\n回應（前 600 字）：{(body.Length > 600 ? body[..600] : body)}");
    }

    /// <summary>next-unhandled 的 ResponseCheck 對「沒有資料」恆真，這裡補一條：至少有一個身分在至少一台主機回非 null，
    /// 證明判準比得到資料（回應形狀若改了，這條會紅）。</summary>
    [Fact]
    public async Task Next_unhandled_returns_data_for_at_least_one_identity()
    {
        var nonNull = 0;
        foreach (var name in new[] { "A受限", "B負責人", "Admin" })
            foreach (var h in _fx.Hosts)
            {
                var row = ReadMatrix.Rows.Single(r => r.Key == "GET /api/records/next-unhandled");
                var (status, body) = await _fx.GetAsync(_fx.Identity(name), ReadMatrix.BuildUrl(row, _fx, h, null, null));
                if (status != HttpStatusCode.OK) continue;
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == System.Text.Json.JsonValueKind.Object) nonNull++;
            }
        Assert.True(nonNull > 0, "next-unhandled 對所有身分、所有主機都回 null——斷言比不到資料");
    }

    /// <summary>handling 回應的欄位名集合快照：一般授權者 A 讀自己主機的完整回應，欄位集合寫死在測試裡。
    /// 新增／移除欄位要同步改這份清單——這是「裁剪（案件授與）只動該動的欄位」的對照基準，
    /// 欄位漂移（例如新增整天結論欄位卻漏了裁剪）會在這裡被看見。</summary>
    private static readonly string[] HandlingFieldNames =
    {
        "assigneeCannotHandle",
        "assigneeHasNoHostAccess",
        "canAssign",
        "canHandle",
        "casesCreated",
        "casesReassigned",
        "casesSkippedHandlerNames",
        "closedIssues",
        "date",
        "derivedStatus",
        "derivedStatusText",
        "dueDate",
        "handlerAccount",
        "handlerHasNoEmail",
        "handlerId",
        "handlerName",
        "hostId",
        "hostName",
        "isOverdue",
        "note",
        "openCaseCount",
        "ownerNames",
        "skippedByReason",
        "status",
        "statusText",
        "totalIssues",
        "updatedAt"
    };

    [Fact]
    public async Task Handling_response_field_names_match_snapshot_for_authorized_reader()
    {
        var row = ReadMatrix.Rows.Single(r => r.Key == "GET /api/records/{hostId:long}/{date}/handling");
        var (status, body) = await _fx.GetAsync(_fx.Identity("A受限"), ReadMatrix.BuildUrl(row, _fx, _fx.Host(1), null, null));
        Assert.Equal(HttpStatusCode.OK, status);
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var names = doc.RootElement.GetProperty("data").EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(HandlingFieldNames.OrderBy(n => n, StringComparer.Ordinal).ToArray(), names);
    }

    private static void ExpectHost(ReadRow row, IdentityFx id, HostFx host, HttpStatusCode status, string body,
        Action<bool, string> expect, Action<HttpStatusCode[]> expectStatus)
    {
        switch (id.VisOf(host.N))
        {
            case Visibility.Full:
                expectStatus(row.OkStatuses);
                if (status == HttpStatusCode.OK && row.Positive.Length > 0)
                    expect(Sentinels.ContainsAnyOf(body, host.N, row.Positive),
                        $"完整可見的主機 {host.N} 沒有出現預期的哨兵（{string.Join("/", row.Positive)}）——判準比不到資料");
                break;

            case Visibility.CaseOnly:
                expectStatus(row.CaseStatuses);
                if (status == HttpStatusCode.OK && row.CasePositive != null)
                    expect(Sentinels.ContainsAnyOf(body, host.N, row.CasePositive),
                        $"案件授與者沒有看到被交辦的問題（{string.Join("/", row.CasePositive)}）");
                break;

            default:
                // 權限異動的停用主機例外（WEB-SPEC §7.1）：ViewAll 的 Admin 與清單同口徑，讀得到停用主機的報告
                if (row.AdminSeesInactive && id.Name == "Admin" && !host.Active)
                {
                    expectStatus(row.OkStatuses);
                    if (status == HttpStatusCode.OK && row.Positive.Length > 0)
                        expect(Sentinels.ContainsAnyOf(body, host.N, row.Positive),
                            $"Admin 讀停用主機 {host.N} 沒有出現預期的哨兵（{string.Join("/", row.Positive)}）");
                    break;
                }
                // 停用主機對 Admin 也不可見（docs/WEB-SPEC.md §7.1），直取一律 404
                expectStatus(new[] { HttpStatusCode.NotFound });
                break;
        }
    }

    private static string Found(string body, int n)
    {
        var hits = Enum.GetValues<SentinelKind>().Where(k => Sentinels.Contains(body, n, k)).Select(k => Sentinels.Of(n, k)).ToList();
        return hits.Count > 0 ? string.Join(", ", hits) : Sentinels.Prefix(n) + "*";
    }
}
