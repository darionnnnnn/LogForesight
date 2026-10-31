namespace LogForesight.Web.Controllers.Api;

public sealed class PrtgMonitoringRequest
{
    public string Revision { get; set; } = "";
    public List<long> HostIds { get; set; } = [];
    public List<long> SensorIds { get; set; } = [];
    public string CatalogueToken { get; set; } = "";
    public string? EstimateToken { get; set; }
}

public sealed class PrtgMonitoringPageRequest
{
    public string CatalogueToken { get; set; } = "";
    public string? Cursor { get; set; }
    public string? Search { get; set; }
    public List<long> HostIds { get; set; } = [];
    public int Offset { get; set; }
}
