using Xunit;

namespace LogForesight.Tests;

/// <summary>風險日詳情「問題」欄的判斷層與列展開的佐證層（docs/archive/FEEDBACK-69-PLAN.md 批次B）。</summary>
public sealed class RecordDetailIssueCellBehaviorTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    [NodeTheory]
    [InlineData("judgmentLayerOrder")]
    [InlineData("identicalTextShownOnce")]
    [InlineData("noGuidanceLeavesNoEmptyNode")]
    [InlineData("judgmentExcludesEvidence")]
    [InlineData("prunedDayShowsHintNotDetails")]
    [InlineData("keyDetailsNone")]
    [InlineData("keyDetailsOneAccount")]
    [InlineData("keyDetailsAccountsAndIps")]
    [InlineData("keyDetailsManyAccountsCollapsible")]
    [InlineData("keyDetailsLegacyWholeMessage")]
    [InlineData("guidancePanelOmitsExplanationLabel")]
    [InlineData("evidencePanelAllSections")]
    [InlineData("evidencePanelSingleSection")]
    [InlineData("evidencePanelStatesMissingSourceObservations")]
    [InlineData("evidencePanelNoneReturnsNull")]
    [InlineData("evidencePanelPrunedKeepsGuidanceOnly")]
    [InlineData("sourceEvidenceList")]
    [InlineData("sourceEvidenceUnknownLinesOmitted")]
    [InlineData("sourceEvidencePartialResolution")]
    [InlineData("sourceEvidenceOwnHostName")]
    [InlineData("sourceEvidenceTruncatedMarker")]
    [InlineData("sourceEvidenceZeroReturnsNull")]
    [InlineData("loginFailureLongAccountCollapses")]
    [InlineData("loginFailureLongSourceAndReasonCollapse")]
    [InlineData("trendCellSplitsWordAndNumbers")]
    public void 問題欄白話說明的production行為(string caseName)
    {
        var scriptDir = Path.Combine(FindRepoRoot(), "LogForesight.Tests", "JsBehavior");
        var script = Path.Combine(scriptDir, "record-detail-issue-cell.behavior.mjs");
        Assert.True(File.Exists(script), $"找不到行為測試腳本: {script}");
        var r = NodeRuntime.RunOrFail(
            new[] { script, caseName }, scriptDir,
            timeout: TimeSpan.FromSeconds(60), label: caseName);
        Assert.True(r.ExitCode == 0, $"行為案例失敗：{caseName}{Environment.NewLine}{r.Stdout}{r.Stderr}");
        Assert.Contains($"PASS {caseName}", r.Stdout);
    }
}
