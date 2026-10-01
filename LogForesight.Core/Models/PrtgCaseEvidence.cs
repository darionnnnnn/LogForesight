namespace LogForesight.Core.Models;

/// <summary>案件保留的最小證據；不隨原分析或 PRTG 原始資料保留期刪除。</summary>
public sealed record PrtgCaseEvidence(DateTime RecordDate, string EventKey, string? SourceGeneration,
    string? ResourceGeneration, DateTimeOffset? IncidentStartedAt, string Quality, string Summary);
