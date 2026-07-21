namespace LogForesight;

/// <summary>
/// 規則欄位的長度上限：單點定義，與未來 DB schema 的 nvarchar 上限使用同一組數字
/// （JSON 檔階段沒有資料庫約束替你擋，這裡就是唯一的防線）。順便替 prompt 預算把關——
/// 規則的 Description 會進每日分析 prompt，自訂規則塞超長文字會稀釋小模型的注意力。
/// </summary>
public static class RuleSchemaLimits
{
    public const int IdMaxLength = 100;
    public const int SourcePatternMaxLength = 100;
    public const int DescriptionMaxLength = 500;
    public const int PlainExplanationMaxLength = 1000;
    public const int ImpactMaxLength = 1000;
    public const int CauseOrStepMaxLength = 500;
}
