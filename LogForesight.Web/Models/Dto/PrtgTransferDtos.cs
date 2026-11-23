namespace LogForesight.Web.Models.Dto;

/// <summary>建立可續傳診斷匯入的請求（POST api/admin/settings/prtg-import-transfers）。
/// 放在 Models 命名空間下，全域 <c>DefaultInputLimitsFilter</c> 才會對字串欄位套預設上限。</summary>
public sealed record PrtgTransferCreateRequestDto(
    Guid TransferId,
    long DeclaredBytes,
    int ChunkCount,
    string PackageSha256);
