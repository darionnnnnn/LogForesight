using System.Reflection;
using System.Security.Cryptography;

namespace LogForesight.Core.Service;

/// <summary>One loaded product assembly's bounded build facts. File paths are intentionally omitted.</summary>
public sealed record LogForesightBuildAssemblyIdentity(
    string Name,
    string InformationalVersion,
    string ModuleVersionId,
    string FileSha256,
    string? SourceStamp,
    string? SourceRevisionIdOverride);

/// <summary>
/// Current Web/Core identity and the reason it can or cannot support latest-build acceptance.
/// A modified source tree remains identifiable by its deterministic fingerprint; an unknown Git
/// source, mixed assemblies, unavailable files, or a mismatched informational suffix is not ready.
/// </summary>
public sealed record LogForesightBuildIdentity(
    string Status,
    string Reason,
    string Token,
    string SourceState,
    string SourceCommit,
    string SourceFingerprint,
    LogForesightBuildAssemblyIdentity Web,
    LogForesightBuildAssemblyIdentity Core)
{
    public bool IsReadyForLatestAcceptance => Status == "verified";

    public static LogForesightBuildIdentity ReadCurrent(Assembly webAssembly, Assembly coreAssembly)
    {
        ArgumentNullException.ThrowIfNull(webAssembly);
        ArgumentNullException.ThrowIfNull(coreAssembly);
        return Assess(ReadAssembly(webAssembly), ReadAssembly(coreAssembly));
    }

    internal static LogForesightBuildIdentity Assess(LogForesightBuildAssemblyIdentity web,
        LogForesightBuildAssemblyIdentity core)
    {
        var tokenText = string.Join("\n", web.Name, web.InformationalVersion, web.ModuleVersionId,
            web.FileSha256, web.SourceStamp, web.SourceRevisionIdOverride, core.Name,
            core.InformationalVersion, core.ModuleVersionId, core.FileSha256, core.SourceStamp,
            core.SourceRevisionIdOverride);
        var token = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(tokenText)));
        var (sourceState, commit, fingerprint) = ParseStamp(web.SourceStamp);
        var (coreState, coreCommit, coreFingerprint) = ParseStamp(core.SourceStamp);

        string status;
        string reason;
        if (web.Name != "LogForesight.Web" || core.Name != "LogForesight.Core")
            (status, reason) = ("invalid", "product-assembly-set-mismatch");
        else if (string.IsNullOrWhiteSpace(web.SourceStamp) || string.IsNullOrWhiteSpace(core.SourceStamp))
            (status, reason) = ("unknown-source", "build-source-stamp-missing");
        else if (sourceState == "unknown" || coreState == "unknown")
            (status, reason) = ("unknown-source", "git-source-unavailable");
        else if (sourceState is not ("clean" or "modified") || coreState is not ("clean" or "modified"))
            (status, reason) = ("invalid", "build-source-state-invalid");
        else if (sourceState != coreState || commit != coreCommit || fingerprint != coreFingerprint)
            (status, reason) = ("mixed", "web-core-source-stamp-mismatch");
        else if (!ValidCommit(commit) || !ValidFingerprint(fingerprint))
            (status, reason) = ("invalid", "build-source-stamp-invalid");
        else if (!ValidAssemblyFile(web) || !ValidAssemblyFile(core))
            (status, reason) = ("unavailable", "product-assembly-file-identity-unavailable");
        else if (web.InformationalVersion != core.InformationalVersion)
            (status, reason) = ("mixed", "web-core-informational-version-mismatch");
        else if (!HasCommitSuffix(web.InformationalVersion, commit) ||
                 !HasCommitSuffix(core.InformationalVersion, commit) ||
                 HasConflictingRevisionOverride(web.SourceRevisionIdOverride, commit) ||
                 HasConflictingRevisionOverride(core.SourceRevisionIdOverride, commit))
            (status, reason) = ("version-mismatch", "informational-version-source-mismatch");
        else
            (status, reason) = ("verified", sourceState == "modified" ? "source-fingerprint-current-dirty-tree" : "source-fingerprint-current");

        return new LogForesightBuildIdentity(status, reason, token, sourceState, commit, fingerprint, web, core);
    }

    private static LogForesightBuildAssemblyIdentity ReadAssembly(Assembly assembly)
    {
        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .GroupBy(attribute => attribute.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal);
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ??
                                   assembly.GetName().Version?.ToString() ?? string.Empty;
        var mvid = assembly.ManifestModule.ModuleVersionId.ToString("D");
        string? fileHash = null;
        try
        {
            var location = assembly.Location;
            if (!string.IsNullOrWhiteSpace(location) && File.Exists(location))
                fileHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(location)));
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or UnauthorizedAccessException) { }

        return new LogForesightBuildAssemblyIdentity(assembly.GetName().Name ?? string.Empty,
            informationalVersion, mvid, fileHash ?? string.Empty,
            metadata.GetValueOrDefault("LogForesight.BuildIdentity"),
            metadata.GetValueOrDefault("LogForesight.BuildSourceRevisionIdOverride"));
    }

    private static (string State, string Commit, string Fingerprint) ParseStamp(string? stamp)
    {
        if (string.IsNullOrWhiteSpace(stamp)) return ("unknown", string.Empty, string.Empty);
        var parts = stamp.Split('|');
        return parts.Length == 4 && parts[0] == "lfbuild-v1"
            ? (parts[1], parts[2], parts[3])
            : ("invalid", string.Empty, string.Empty);
    }

    private static bool ValidCommit(string value) => value.Length == 40 && value.All(Uri.IsHexDigit);
    private static bool ValidFingerprint(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private static bool ValidAssemblyFile(LogForesightBuildAssemblyIdentity value) =>
        Guid.TryParse(value.ModuleVersionId, out _) && value.FileSha256.Length == 64 && value.FileSha256.All(Uri.IsHexDigit);
    private static bool HasCommitSuffix(string value, string commit) =>
        value.EndsWith("+" + commit, StringComparison.OrdinalIgnoreCase);
    private static bool HasConflictingRevisionOverride(string? value, string commit) =>
        !string.IsNullOrWhiteSpace(value) && !string.Equals(value, commit, StringComparison.OrdinalIgnoreCase);
}
