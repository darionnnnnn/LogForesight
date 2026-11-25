using LogForesight.Core.Models;
using LogForesight.Web.Auth;

namespace LogForesight.Web.Services;

/// <summary>
/// 「處理人無法接單」的唯一判定：帳號停用、暫停接單，或已不具處理能力（解析出的能力不含 Handle）。
/// 交辦單清單的「處理人離職或停用」篩選（handlerInactive）與管理者待辦「處理人已停用或暫停仍有單」的人數都呼叫這裡，
/// 兩邊才會是同一批人——待辦的數字點進去，清單裡不重複的處理人數必須等於它。
/// <paramref name="capabilities"/> 為 null（測試組裝沒注入解析器）時不看處理能力，只看停用與暫停旗標。
/// </summary>
public static class HandlerInactivity
{
    public static bool IsInactive(WebUser user, UserCapabilityResolver? capabilities) =>
        !user.Active || user.DispatchPaused
        || (capabilities != null && !capabilities.Resolve(user).Contains(Capability.Handle));
}
