namespace LogForesight.Core.Persistence;

/// <summary>
/// PRTG 範圍外清除的規則違反（預覽逾期、範圍或權限在預覽後變更、操作者不符、超過預覽上限…）。
/// 訊息是寫好給操作者看的中文，Web 端原樣回 400。專屬型別讓控制器不必再 catch 通用的
/// <see cref="InvalidOperationException"/>——那個型別同樣會接到 EF／LINQ 的英文原文，原樣回給使用者就是外洩。
/// 繼承 <see cref="InvalidOperationException"/> 只為相容既有的內部 catch。
/// </summary>
public sealed class PrtgScopePurgeRuleException : InvalidOperationException
{
    public PrtgScopePurgeRuleException(string message) : base(message) { }
}
