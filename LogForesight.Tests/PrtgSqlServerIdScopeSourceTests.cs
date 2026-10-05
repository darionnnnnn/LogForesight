using LogForesight.Core.Persistence.Sql;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgSqlServerIdScopeSourceTests
{
    [Fact]
    public void LargeIdScopeSubqueriesComposeIntoCandidateQueryWithoutExpandedIdParameters()
    {
        var options = new DbContextOptionsBuilder<LfDbContext>()
            .UseSqlServer("Server=.;Database=LfTranslateOnly;Trusted_Connection=True;")
            .Options;
        using var context = new LfDbContext(options);

        var hostIds = context.Database.SqlQueryRaw<long>("SELECT [Id] AS [Value] FROM #PrtgReadinessHostIds");
        var sensorIds = context.Database.SqlQueryRaw<long>("SELECT [Id] AS [Value] FROM #PrtgReadinessSensorIds");
        var query = EfPrtgStore.BuildLatestMappedReadinessSensorsQuery(context,
            Enumerable.Range(1, 3000).Select(id => (long)id).ToArray(),
            new DateTime(2026, 10, 3), new DateTime(2026, 9, 3),
            Enumerable.Range(1, 15000).Select(id => (long)id).ToArray(), hostIds, sensorIds);

        var sql = query.ToQueryString();

        Assert.Contains("#PrtgReadinessHostIds", sql, StringComparison.Ordinal);
        Assert.Contains("#PrtgReadinessSensorIds", sql, StringComparison.Ordinal);
        Assert.Contains("MAX(", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OPENJSON(@__hostIds", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OPENJSON(@__filterSensorIds", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@p1000", sql, StringComparison.Ordinal);
    }
}
