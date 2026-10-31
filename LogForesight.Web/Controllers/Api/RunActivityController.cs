using LogForesight.Core.Analysis;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using LogForesight.Web.Auth;
using LogForesight.Web.Configuration;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;
using static LogForesight.Web.Controllers.Api.QueryStringParsing;

namespace LogForesight.Web.Controllers.Api;

/// <summary>
/// 執行中告示（docs/archive/SCALE-FIX-PLAN-2026-08-06.md S-3）：**任何登入者**都讀得到，
/// 刻意不掛 <c>[Permission]</c>——分析與站台跑在同一個行程，變慢的是所有人的畫面，
/// 只讓維運看得到原因等於沒有配套。回傳只有「在不在跑、跑到哪」，不含排程設定與上次成敗
/// （那些在 <c>/api/admin/schedule/status</c>，維運視角）；**觸發者**（誰按的）只對具
/// DevMonitor 或 Maintain 的使用者填，一般使用者拿到 null——告示對他們仍照常出現，只是不講是誰。
///
/// 路由刻意不放在 <c>api/admin/</c> 底下：那個前綴在本專案是「需要管理權限」的訊號，
/// 一般使用者讀得到的東西擺進去，只會讓下一個人誤判這支的權限範圍。
/// </summary>
[ApiController]
[Route("api/run-activity")]
public class RunActivityController : ControllerBase
{
    private readonly SchedulerRunState _runState;
    private readonly AiAnalysisRunState _aiRunState;
    private readonly IUserStore _users;
    private readonly IUserDisplayNameService _userDisplayNames;
    private readonly ICurrentUser _currentUser;
    private readonly IIssueCaseStore _cases;

    public RunActivityController(
        SchedulerRunState runState,
        AiAnalysisRunState aiRunState,
        IUserStore users,
        IUserDisplayNameService userDisplayNames,
        ICurrentUser currentUser,
        IIssueCaseStore cases)
    {
        _runState = runState;
        _aiRunState = aiRunState;
        _users = users;
        _userDisplayNames = userDisplayNames;
        _currentUser = currentUser;
        _cases = cases;
    }

    /// <summary>觸發者姓名屬維運資訊：與 <c>/api/admin/schedule/status</c> 同一道門檻，其餘角色為 null</summary>
    private bool CanSeeTrigger => _currentUser.Has(Capability.DevMonitor) || _currentUser.Has(Capability.Maintain);

    [HttpGet]
    public ApiResponse<RunActivityDto> Get()
    {
        // 單一告示只能顯示一條進度，主／本機軌的取捨由 SchedulerRunState.LatestActivity
        // 單點決定（回饋十四輪 UI-6 體檢）——這支與健康診斷共用同一個選擇邏輯，不各自記得。
        var s = _runState.Snapshot();
        var (phase, done, total) = (s.ActivityPhase, s.ActivityDone, s.ActivityTotal);

        if (s.IsRunning || phase != null)
        {
            return ApiResponse<RunActivityDto>.Ok(new RunActivityDto
            {
                IsRunning = s.IsRunning,
                Done = done,
                Total = total,
                // 本機路徑是逐日回補（天），NetIQ 機房路徑是逐台主機（台）——「主機日」這個單位
                // 一般使用者沒有概念。
                UnitText = phase switch
                {
                    "local" => "天",
                    "netiq" => "台",
                    _ => null
                },
                // 誰觸發的：與排程頁共用 RunTriggerText，兩邊對同一個 Trigger 值算出相同文字。
                // 沒在跑時不講（閒置沒有「觸發者」可言）。
                TriggerText = s.IsRunning && CanSeeTrigger
                    ? (s.Trigger is null ? "閒置" : RunTriggerText.Of(s.Trigger, _userDisplayNames, _users))
                    : null,
                CaseDaySyncPending = _cases.CountDaySyncPending(),
                // 取數分支：互斥判斷要分得出「取數在跑」與「只有 AI 在跑」（見 DTO 註解）
                IsFetchRun = s.IsRunning
            });
        }

        // 取數閒置時輪到 AI 排程（回饋三十五輪批次D：AI 補寫拆成獨立排程後，
        // 它單獨在跑時全站也要看得到——優先序 取數 > AI，兩者同時跑時取數較有代表性）
        var ai = _aiRunState.Snapshot();
        return ApiResponse<RunActivityDto>.Ok(new RunActivityDto
        {
            IsRunning = ai.IsRunning,
            Done = ai.ProgressDone,
            Total = ai.ProgressTotal,
            UnitText = ai.IsRunning ? "件" : null,
            TriggerText = ai.IsRunning && CanSeeTrigger
                ? (ai.Trigger is null ? "閒置" : RunTriggerText.Of(ai.Trigger, _userDisplayNames, _users))
                : null,
            CaseDaySyncPending = _cases.CountDaySyncPending(),
            // AI 分支：取數此時必定閒置（上面的分支沒進來），主機更新不該被擋
            IsFetchRun = false
        });
    }
}
