using System.Net;
using Xunit;
using Xunit.Abstractions;

namespace LogForesight.Tests.Authorization;

/// <summary>
/// 授權寫入矩陣：受限者（有 Handle／ConfirmPermission 或不要求能力）能進的寫入端點，
/// 對「未授權主機」的資料不得有任何寫入。真 JWT＋真路由＋真 <c>VisibilityService</c>，資料經站台自己的 store 讀寫。
///
/// 每個案例＝（端點, 身分, 目標）：
/// <list type="bullet">
/// <item>未授權目標（主機 2／3／4、別人的交辦單）：回 403／404（批次端點：200 且 updatedCount=0），
/// 回應不含未授權主機的哨兵，且<b>相關資料</b>（見各列 <see cref="WritePlan.Snapshot"/>）與<b>全域指紋</b>
/// （<see cref="AuthorizationDataProbe.Global"/>）前後逐字相同。</item>
/// <item>自己的目標（主機 1、自己的交辦單）：2xx 且該列相關資料真的變了（證明判準比得到東西，不是什麼都不寫的假綠）；
/// 寫完復原，避免干擾其他列與讀取矩陣。</item>
/// </list>
/// 發現受限者能寫到未授權主機時不修正式碼，測試保持紅燈並指出端點、身分與目標。
/// </summary>
[Collection(AuthorizationHttpCollection.Name)]
public class AuthorizationWriteMatrixHttpTests
{
    private readonly AuthorizationHttpFixture _fx;
    private readonly AuthorizationDataProbe _probe;
    private readonly ITestOutputHelper _output;

    public AuthorizationWriteMatrixHttpTests(AuthorizationHttpFixture fx, ITestOutputHelper output)
    {
        _fx = fx;
        _output = output;
        _probe = new AuthorizationDataProbe(fx);
    }

    public static IEnumerable<object[]> Cases() =>
        WriteMatrix.Rows.SelectMany(r => r.Cases.Select(c => new object[] { r.Key, c.Identity, c.Label }));

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Row_enforces_authorization(string key, string identityName, string label)
    {
        var row = WriteMatrix.Rows.Single(r => r.Key == key);
        var id = _fx.Identity(identityName);
        var ctx = new WriteCtx { Fx = _fx, Probe = _probe, Identity = id, Label = label };
        var plan = row.Plan(ctx);

        var globalBefore = _probe.Global();
        var rowBefore = plan.Snapshot();
        var othersBefore = label == "self" ? _probe.OthersPreferences(id) : null;

        var (status, body, _) = await _fx.SendAsync(id, plan.Method, plan.Url, plan.Body);

        _output.WriteLine($"{plan.Method} {plan.Url} 身分={identityName} 目標={label} 期望={plan.Expect} 實際={(int)status}");
        var globalAfter = _probe.Global();
        var rowAfter = plan.Snapshot();
        var problems = new List<string>();
        void Expect(bool ok, string message) { if (!ok) problems.Add(message); }

        try
        {
            switch (plan.Expect)
            {
                case WriteExpect.Reject:
                {
                    var rejected = plan.RejectStatuses.Contains(status)
                                   || status == HttpStatusCode.OK && plan.SoftRejectText != null
                                   && body.Contains(plan.SoftRejectText, StringComparison.Ordinal);
                    Expect(rejected, $"未授權的目標應被拒（{string.Join("/", plan.RejectStatuses.Select(s => (int)s))}" +
                                     $"{(plan.SoftRejectText != null ? $"，或 200 且含 {plan.SoftRejectText}" : "")}），實際 {(int)status}");
                    Expect(rowAfter == rowBefore, $"被拒的請求仍改了相關資料\n  前：{Clip(rowBefore)}\n  後：{Clip(rowAfter)}");
                    Expect(globalAfter == globalBefore, "被拒的請求改了全域資料（全域指紋不同）");
                    foreach (var n in plan.ForbiddenHosts)
                        Expect(!Sentinels.ContainsAny(body, n), $"回應含未授權主機 {n} 的哨兵：{Sentinels.Prefix(n)}*");
                    Expect(!body.Contains("SENTINEL-WO-", StringComparison.Ordinal) || label is "own" or "-",
                        "回應含別人交辦單的哨兵");
                    break;
                }

                case WriteExpect.Write:
                    Expect((int)status is >= 200 and < 300, $"自己的目標應成功（2xx），實際 {(int)status}");
                    Expect(rowAfter != rowBefore, "請求成功但相關資料沒有變化——判準比不到東西");
                    break;

                case WriteExpect.OkNoWrite:
                    Expect((int)status is >= 200 and < 300, $"應回 2xx，實際 {(int)status}");
                    Expect(rowAfter == rowBefore, "這個端點對該身分不該寫資料，但資料變了");
                    break;
            }

            if (othersBefore != null)
                Expect(_probe.OthersPreferences(id) == othersBefore, "只該寫到自己，但別人的偏好也變了");
        }
        finally
        {
            plan.Restore?.Invoke();
        }

        Assert.True(problems.Count == 0,
            $"[{key}] 身分={identityName} 目標={label}\n{plan.Method} {plan.Url}\n" +
            string.Join("\n", problems.Select(p => "  - " + p)) +
            $"\n回應（前 600 字）：{Clip(body, 600)}");
    }

    private static string Clip(string text, int max = 400) => text.Length > max ? text[..max] + "…" : text;
}
