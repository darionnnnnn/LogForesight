using LogForesight.Web.Models;

namespace LogForesight.Web.Auth;

/// <summary>
/// 全站管理端點的授權單一出口：是否持有 <see cref="Capability.ViewAll"/>。
/// 不讀可見主機集合也不讀主機表——停用或合併墓碑主機不應讓全站管理者被拒絕。
/// </summary>
public static class CurrentUserAuthorization
{
    public static bool HasViewAll(this ICurrentUser? user) =>
        user is { IsAuthenticated: true } && user.Has(Capability.ViewAll);

    public static void RequireViewAll(this ICurrentUser? user, string message)
    {
        if (!user.HasViewAll())
            throw DomainException.Forbidden(message);
    }
}
