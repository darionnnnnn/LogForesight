namespace LogForesight.Tests;

/// <summary>
/// 測試用 <see cref="NightlyDispatch"/> 組裝：案件／逐日列／歷程／主機／問題檔案 store 沿用呼叫端傳入的
/// （與 <see cref="IssueCaseCoordinator"/> 同一組），交辦單與雜訊記憶用記憶體替身、候選池為空、系統設定取預設。
/// 空候選池＝負責人規則與自動派工都找不到人，給「不測派工」的管線測試用。
/// </summary>
internal static class NightlyDispatchFakes
{
    public static NightlyDispatch Create(
        IIssueCaseStore cases, IIssueHandlingStore issueHandlings, IRecordHandlingStore handlingLog,
        IHostStore hosts, IIssueOwnerStore issueProfiles, IssueCaseCoordinator caseCoordinator)
    {
        var orders = new FakeWorkOrderStore(new FakeIssueCaseStore());
        var ctx = DispatchContext.Build(
            new DispatchCandidatePool(), issueProfiles, orders, cases, new FakeNoiseMarkStore(), new SystemSettings(), DateTime.Now);
        var coordinator = new WorkOrderCoordinator(orders, cases, issueHandlings, caseCoordinator, handlingLog, hosts);
        return new NightlyDispatch(coordinator, ctx, hosts);
    }
}
