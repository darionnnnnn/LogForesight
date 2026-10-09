using LogForesight.Core.Models;
using LogForesight.Core.Persistence.Sql;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgManualMapIdentityContractTests
{
    [Fact]
    public void ManualMapDeviceObjid_IsExternallyAssigned_AndPublicUpsertWorksOnSqlite()
    {
        using var fixture = new EfSqliteFixture();
        using (var context = fixture.NewContext())
        {
            var property = context.Model.FindEntityType(typeof(PrtgManualMapRow))!
                .FindProperty(nameof(PrtgManualMapRow.DeviceObjid))!;
            Assert.Equal(ValueGenerated.Never, property.ValueGenerated);
        }

        var now = new DateTime(2026, 10, 10, 12, 30, 0, DateTimeKind.Utc);
        var store = new EfPrtgStore(fixture.NewContext);
        store.UpsertManualMap(new PrtgManualMapRow
        {
            DeviceObjid = 4_100_000_001,
            HostId = 73,
            CreatedBy = "schema-contract",
            Note = "externally assigned key",
            CreatedAt = now
        });

        using var verify = fixture.NewContext();
        var saved = Assert.Single(verify.PrtgManualMaps);
        Assert.Equal(4_100_000_001, saved.DeviceObjid);
        Assert.Equal(73, saved.HostId);
        Assert.Equal("schema-contract", saved.CreatedBy);
        Assert.Equal("externally assigned key", saved.Note);
        Assert.Equal(now, saved.CreatedAt);
    }

    [Fact]
    public void SqlServerIdentityRepairDdl_IsGuardedTransactionalAndCopiesEveryMappingField()
    {
        var ddl = SchemaUpgrader.SqlServerFixPrtgManualMapIdentityDdl;

        Assert.Contains("ISNULL(COLUMNPROPERTY(@table_id, N'device_objid', N'IsIdentity'), 0) <> 1", ddl,
            StringComparison.Ordinal);
        var appLock = ddl.IndexOf("sp_getapplock", StringComparison.Ordinal);
        var firstObjectRead = ddl.IndexOf("DECLARE @table_id int = OBJECT_ID", StringComparison.Ordinal);
        var tableLock = ddl.IndexOf("WITH (TABLOCKX, HOLDLOCK)", StringComparison.Ordinal);
        var lockedObjectRecheck = ddl.IndexOf("SET @table_id = OBJECT_ID", tableLock, StringComparison.Ordinal);
        var drop = ddl.IndexOf("DROP TABLE dbo.lf_prtg_manual_map", StringComparison.Ordinal);
        Assert.True(appLock >= 0 && appLock < firstObjectRead && firstObjectRead < tableLock &&
            tableLock < lockedObjectRecheck && lockedObjectRecheck < drop,
            "The repair must serialize startups and re-read identity after acquiring the table lock.");
        Assert.Contains("BEGIN TRANSACTION", ddl, StringComparison.Ordinal);
        Assert.Contains("BEGIN CATCH", ddl, StringComparison.Ordinal);
        Assert.Contains("IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION", ddl, StringComparison.Ordinal);
        Assert.Contains("HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'VIEW DEFINITION')", ddl,
            StringComparison.Ordinal);
        Assert.Contains("sys.foreign_keys", ddl, StringComparison.Ordinal);
        Assert.Contains("sys.sql_expression_dependencies", ddl, StringComparison.Ordinal);
        Assert.Contains("sys.database_permissions", ddl, StringComparison.Ordinal);
        Assert.Contains("sys.extended_properties", ddl, StringComparison.Ordinal);
        Assert.Contains("principal_id IS NOT NULL", ddl, StringComparison.Ordinal);
        Assert.Contains("sys.partitions", ddl, StringComparison.Ordinal);
        Assert.Contains("sys.security_predicates", ddl, StringComparison.Ordinal);
        Assert.Contains("is_memory_optimized", ddl, StringComparison.Ordinal);
        Assert.Contains("is_tracked_by_cdc", ddl, StringComparison.Ordinal);
        Assert.Contains("is_remote_data_archive_enabled", ddl, StringComparison.Ordinal);
        Assert.Contains("generated_always_type", ddl, StringComparison.Ordinal);
        Assert.Contains("is_masked", ddl, StringComparison.Ordinal);
        Assert.Contains("DATABASEPROPERTYEX(DB_NAME(), 'Collation')", ddl, StringComparison.Ordinal);
        Assert.Contains("is_descending_key = 0", ddl, StringComparison.Ordinal);
        Assert.Contains("lock_escalation <> 0", ddl, StringComparison.Ordinal);
        Assert.Contains("sys.all_columns", ddl, StringComparison.Ordinal);
        Assert.Contains("optimize_for_sequential_key", ddl, StringComparison.Ordinal);
        Assert.DoesNotContain("column_id = 1", ddl, StringComparison.Ordinal);
        Assert.DoesNotContain("column_id = 2", ddl, StringComparison.Ordinal);
        Assert.DoesNotContain("column_id = 3", ddl, StringComparison.Ordinal);
        Assert.DoesNotContain("column_id = 4", ddl, StringComparison.Ordinal);
        Assert.DoesNotContain("column_id = 5", ddl, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE dbo.lf_prtg_manual_map (", ddl, StringComparison.Ordinal);
        Assert.Contains("device_objid bigint NOT NULL,", ddl, StringComparison.Ordinal);
        Assert.DoesNotContain("device_objid bigint NOT NULL IDENTITY", ddl, StringComparison.Ordinal);
        Assert.Contains("device_objid, host_id, created_by, note, created_at", ddl, StringComparison.Ordinal);
        Assert.Contains("CREATE INDEX IX_lf_prtg_manual_map_host ON dbo.lf_prtg_manual_map (host_id)", ddl,
            StringComparison.Ordinal);
        Assert.Contains("EXCEPT SELECT device_objid, host_id, created_by, note, created_at", ddl,
            StringComparison.Ordinal);
        foreach (var guard in new[] { "sys.database_permissions", "sys.extended_properties", "sys.partitions",
                     "sys.security_predicates", "sys.foreign_keys", "sys.triggers",
                     "sys.sql_expression_dependencies", "is_remote_data_archive_enabled", "is_masked" })
            Assert.True(ddl.IndexOf(guard, StringComparison.Ordinal) < drop,
                $"The metadata guard '{guard}' must run before the old table is dropped.");
    }
}
