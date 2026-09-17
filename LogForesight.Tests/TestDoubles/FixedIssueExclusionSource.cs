using LogForesight.Web.Services;

namespace LogForesight.Tests;

/// <summary>測試用靜音排除來源：固定回傳建構時給的那一份，並記錄被取用次數。</summary>
internal sealed class FixedIssueExclusionSource : IIssueExclusionSource
{
    private readonly IssueExclusion _exclusion;

    public FixedIssueExclusionSource(IssueExclusion exclusion) => _exclusion = exclusion;

    /// <summary>Current() 累計呼叫次數</summary>
    public int CallCount { get; private set; }

    public IssueExclusion Current()
    {
        CallCount++;
        return _exclusion;
    }
}
