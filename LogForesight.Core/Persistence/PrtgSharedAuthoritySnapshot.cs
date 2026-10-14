using System.Text.Json;
using LogForesight.Core.Models;
using LogForesight.Core.Service;
using Microsoft.EntityFrameworkCore;
using LogForesight.Core.Persistence.Sql;
using Microsoft.Data.Sqlite;

namespace LogForesight.Core.Persistence;

/// <summary>One transaction readset of bounded shared documents on which PRTG source authority depends.</summary>
public sealed record PrtgSharedAuthoritySnapshot(SystemSettings Settings, PrtgMonitoringPolicy Policy,
    long SettingsBlobVersion, long PolicyBlobVersion)
{
    public const int MaximumDocumentCharacters = PrtgTrustedSamplingBindingStore.MaximumAuthorityCharacters;
    public const string UnavailableReason = "shared-authority-document-unavailable";

    public static PrtgSharedAuthoritySnapshot Read(StorageBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        using var context = backend.CreateContext();
        context.Database.OpenConnection();
        using var sqliteTransaction = context.Database.GetDbConnection() is SqliteConnection sqlite
            ? sqlite.BeginTransaction(deferred:true) : null;
        using var transaction = sqliteTransaction is null
            ? context.Database.BeginTransaction(System.Data.IsolationLevel.RepeatableRead)
            : context.Database.UseTransaction(sqliteTransaction);
        var settingsRow = EfJsonBlobStore.ReadBoundedInContext(context,"system_settings",MaximumDocumentCharacters);
        var policyRow = EfJsonBlobStore.ReadBoundedInContext(context,PrtgMonitoringPolicyStore.BlobKey,MaximumDocumentCharacters);
        if (settingsRow.ReportedLength > MaximumDocumentCharacters || policyRow.ReportedLength > MaximumDocumentCharacters)
            throw new InvalidDataException("PRTG shared authority document exceeds its bounded read limit.");
        var settings = settingsRow.Prefix is null && settingsRow.Version == 0 ? new SystemSettings()
            : JsonSerializer.Deserialize<SystemSettings>(settingsRow.Prefix!, LfJsonOptions.Pretty)
                ?? throw new JsonException("PRTG system settings authority is null.");
        var policy = policyRow.Prefix is null && policyRow.Version == 0 ? new PrtgMonitoringPolicy()
            : JsonSerializer.Deserialize<PrtgMonitoringPolicy>(policyRow.Prefix!, LfJsonOptions.Pretty)
                ?? throw new JsonException("PRTG monitoring policy authority is null.");
        if (policy.HostIds is null || policy.SensorIds is null ||
            typeof(PrtgMonitoringPolicy).GetProperties().Any(property =>
                property.PropertyType == typeof(string) && property.GetValue(policy) is null))
            throw new JsonException("PRTG monitoring policy contains null authority fields.");
        return new(settings, policy, settingsRow.Version, policyRow.Version);
    }

    public static bool TryRead(StorageBackend backend, out PrtgSharedAuthoritySnapshot? snapshot,
        out string? reason)
    {
        try { snapshot = Read(backend); reason = null; return true; }
        catch (Exception error) when (error is InvalidDataException or JsonException)
        { snapshot = null; reason = UnavailableReason; return false; }
    }
}
