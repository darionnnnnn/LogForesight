using System.Net;
using System.Text.Json;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Tests.Authorization;
using LogForesight.Web.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 79 輪驗收缺口（整站組裝層）：B6 權限不足一律寫稽核、C12 執行總表單日端點的日期界限。
/// 共用授權矩陣的整站 fixture（真的站台組裝，受限角色 A、案件授與 C、管理者）。
/// </summary>
[Collection(AuthorizationHttpCollection.Name)]
public class Round79GapHttpTests
{
    private readonly AuthorizationHttpFixture _fx;
    public Round79GapHttpTests(AuthorizationHttpFixture fx) => _fx = fx;

    private int AccessDeniedCount(string path)
    {
        var store = _fx.Services.GetRequiredService<AuditLogStore>();
        return store.Query(new AuditQuery
        {
            Actions = new List<string> { AuditActions.AccessDenied },
            TargetId = path,
            From = DateTime.Today.AddDays(-1),
            PageSize = 200
        }).Items.Count(e => e.Result == AuditResult.Denied && e.TargetId == path);
    }

    private static string ErrorCode(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("error").GetProperty("code").GetString()!;
    }

    [Fact]
    public async Task B6_有維護權限但沒有全站檢視的角色打RequireViewAll端點_回403信封且寫入一筆AccessDenied稽核()
    {
        // 端點整個類別要 Maintain（A 會先被 Permission 擋下，那是另一條稽核路徑）；serverAdmin 有 Maintain 但沒有 ViewAll，
        // 因此剛好是被 RequireViewAll 擋下的角色
        const string path = "/api/prtg/effectiveness";
        var before = AccessDeniedCount(path);

        var (status, body) = await _fx.GetAsync(_fx.ServerAdmin, path);

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal(ApiErrorCodes.Forbidden, ErrorCode(body));
        Assert.Equal(before + 1, AccessDeniedCount(path));
    }

    [Fact]
    public async Task B6_服務擲DomainException_Forbidden_回403信封且寫入一筆AccessDenied稽核()
    {
        // 交辦單 C 與 A 無關：WorkOrderQueryService.Authorize 擲 DomainException.Forbidden
        var path = $"/api/work-orders/{_fx.WorkOrderC.Id}";
        var before = AccessDeniedCount(path);

        var (status, body) = await _fx.GetAsync(_fx.A, path);

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal(ApiErrorCodes.Forbidden, ErrorCode(body));
        Assert.Equal(before + 1, AccessDeniedCount(path));
    }

    [Fact]
    public async Task B6_管理者打同一個RequireViewAll端點_不寫AccessDenied稽核()
    {
        const string path = "/api/prtg/effectiveness";
        var before = AccessDeniedCount(path);

        var (status, _) = await _fx.GetAsync(_fx.Admin, path);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(before, AccessDeniedCount(path));
    }

    [Theory]
    [InlineData(2, false)]      // 未來超過一天
    [InlineData(1, true)]       // 未來一天（邊界內）
    [InlineData(0, true)]
    public async Task C12_執行總表單日端點_未來日期的界限(int daysFromToday, bool inRange)
    {
        var date = DateTime.Today.AddDays(daysFromToday).ToString("yyyy-MM-dd");

        var (status, body) = await _fx.GetAsync(_fx.Admin, $"/api/runs/day/{date}");

        if (inRange) Assert.Equal(HttpStatusCode.OK, status);
        else
        {
            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Equal(ApiErrorCodes.ValidationFailed, ErrorCode(body));
        }
    }

    [Theory]
    [InlineData(0, true)]       // 剛好十年前（邊界內）
    [InlineData(-1, false)]     // 十年又一天前
    public async Task C12_執行總表單日端點_過去日期的界限(int extraDays, bool inRange)
    {
        var date = DateTime.Today.AddYears(-10).AddDays(extraDays).ToString("yyyy-MM-dd");

        var (status, body) = await _fx.GetAsync(_fx.Admin, $"/api/runs/day/{date}");

        if (inRange) Assert.Equal(HttpStatusCode.OK, status);
        else
        {
            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Equal(ApiErrorCodes.ValidationFailed, ErrorCode(body));
        }
    }

    [Fact]
    public async Task C12_執行總表單日端點_最大日期回400而不是500()
    {
        var (status, _) = await _fx.GetAsync(_fx.Admin, "/api/runs/day/9999-12-31");

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    // ── C6：稽核與權限異動的迄日改半開區間 ──────────────────────────────────────

    private static string Dash(DateTime day) => day.ToString("yyyy-MM-dd");

    [Fact]
    public async Task C6_稽核查詢_迄日當天235959點500的紀錄查得到_次日零時查不到()
    {
        // 稽核列的 SQL 窄化看的是附加當下的時間，所以用「今天」當迄日：兩筆都在今天附加，差別只在 OccurredAt
        var day = DateTime.Today;
        var audit = _fx.Services.GetRequiredService<AuditLogStore>();
        audit.Append(new AuditEntry
        {
            OccurredAt = day.AddHours(23).AddMinutes(59).AddSeconds(59).AddMilliseconds(500),
            Account = "c6-audit", Action = AuditActions.Logout, TargetKind = "c6", TargetId = "c6-in",
            Summary = "C6-AUDIT-LAST-SECOND"
        });
        audit.Append(new AuditEntry
        {
            OccurredAt = day.AddDays(1),
            Account = "c6-audit", Action = AuditActions.Logout, TargetKind = "c6", TargetId = "c6-out",
            Summary = "C6-AUDIT-NEXT-MIDNIGHT"
        });

        var (status, body) = await _fx.GetAsync(_fx.Admin, $"/api/audit?from={Dash(day)}&to={Dash(day)}&targetKind=c6&pageSize=200");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("C6-AUDIT-LAST-SECOND", body);
        Assert.DoesNotContain("C6-AUDIT-NEXT-MIDNIGHT", body);
    }

    private void SeedPermissionChanges(DateTime day, string inId, string outId)
    {
        var host = _fx.Host(1);
        _fx.Services.GetRequiredService<PermissionChangeStore>().AppendChanges(new[]
        {
            new PermissionChangeRecord
            {
                ChangeId = inId, HostName = host.Name, DetectedAt = day.Date.AddHours(23).AddMinutes(59).AddSeconds(59).AddMilliseconds(500),
                Target = inId + "-target", ChangeType = "成員新增", Before = "-", After = "-", AlertText = inId + " 告警"
            },
            new PermissionChangeRecord
            {
                ChangeId = outId, HostName = host.Name, DetectedAt = day.Date.AddDays(1),
                Target = outId + "-target", ChangeType = "成員新增", Before = "-", After = "-", AlertText = outId + " 告警"
            }
        });
    }

    [Fact]
    public async Task C6_權限異動查詢_迄日當天235959點500查得到_次日零時查不到()
    {
        var day = AuthorizationHttpFixture.Day.AddDays(-20);
        SeedPermissionChanges(day, "c6-perm-list-in", "c6-perm-list-out");

        var (status, body) = await _fx.GetAsync(_fx.Admin, $"/api/permission-changes?from={Dash(day)}&to={Dash(day)}&pageSize=200");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("c6-perm-list-in", body);
        Assert.DoesNotContain("c6-perm-list-out", body);
    }

    [Fact]
    public async Task C6_權限異動全選編號清單_迄日當天235959點500查得到_次日零時查不到()
    {
        var day = AuthorizationHttpFixture.Day.AddDays(-21);
        SeedPermissionChanges(day, "c6-perm-ids-in", "c6-perm-ids-out");

        var (status, body) = await _fx.GetAsync(_fx.Admin, $"/api/permission-changes/ids?from={Dash(day)}&to={Dash(day)}");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("c6-perm-ids-in", body);
        Assert.DoesNotContain("c6-perm-ids-out", body);
    }

    [Fact]
    public void C6_稽核與權限異動的時間欄位在SQLServer模型是datetime2_沒有進位風險()
    {
        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<LogForesight.Core.Persistence.Sql.LfDbContext>()
            .UseSqlServer("Server=.;Database=LfTranslateOnly;Trusted_Connection=True;").Options;
        using var ctx = new LogForesight.Core.Persistence.Sql.LfDbContext(options);

        string ColumnType<TRow>(string property) =>
            Microsoft.EntityFrameworkCore.RelationalPropertyExtensions.GetColumnType(
                ctx.Model.FindEntityType(typeof(TRow))!.FindProperty(property)!);

        // 稽核存在 lf_log_lines（log_key = audit）；datetime 會把毫秒進位到 .000／.003／.007，
        // 迄日當天 23:59:59.999 的紀錄可能被進位到次日零時
        Assert.Equal("datetime2", ColumnType<LogForesight.Core.Persistence.Sql.LogLineRow>(nameof(LogForesight.Core.Persistence.Sql.LogLineRow.CreatedAt)));
        Assert.Equal("datetime2", ColumnType<LogForesight.Core.Persistence.Sql.PermissionChangeRow>(nameof(LogForesight.Core.Persistence.Sql.PermissionChangeRow.DetectedAt)));
    }
}
