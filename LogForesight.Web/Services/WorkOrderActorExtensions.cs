using LogForesight.Core.Models;
using LogForesight.Web.Auth;

namespace LogForesight.Web.Services;

/// <summary>目前使用者 → 交辦單操作者（協調器用）的唯一一份組法；系統身分（UserId 0）的 ActorId 為 null</summary>
public static class WorkOrderActorExtensions
{
    public static WorkOrderActor ToWorkOrderActor(this ICurrentUser currentUser) => new()
    {
        ActorId = currentUser.UserId > 0 ? currentUser.UserId : null,
        ActorAccount = currentUser.Account,
        OccurredAt = DateTime.Now
    };
}
