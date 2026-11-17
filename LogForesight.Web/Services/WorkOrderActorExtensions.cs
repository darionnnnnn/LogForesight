using LogForesight.Core.Models;
using LogForesight.Web.Auth;

namespace LogForesight.Web.Services;

/// <summary>目前使用者 → 交辦單操作者（協調器用）的唯一一份組法；系統身分（UserId 0）的 ActorId 為 null。
/// 發生時間取自 <paramref name="clock"/>（省略＝系統時鐘）。</summary>
public static class WorkOrderActorExtensions
{
    public static WorkOrderActor ToWorkOrderActor(this ICurrentUser currentUser, TimeProvider? clock = null) => new()
    {
        ActorId = currentUser.UserId > 0 ? currentUser.UserId : null,
        ActorAccount = currentUser.Account,
        OccurredAt = (clock ?? TimeProvider.System).LocalNow()
    };
}
