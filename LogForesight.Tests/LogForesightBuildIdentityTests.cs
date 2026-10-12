using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public sealed class LogForesightBuildIdentityTests
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private const string Fingerprint = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

    [Theory]
    [InlineData("clean")]
    [InlineData("modified")]
    public void Assess_AcceptsKnownMatchingWebAndCoreStamp(string state)
    {
        var identity = Assess(Assembly("LogForesight.Web", Stamp(state, Fingerprint)),
            Assembly("LogForesight.Core", Stamp(state, Fingerprint)));

        Assert.True(identity.IsReadyForLatestAcceptance);
        Assert.Equal(state, identity.SourceState);
        Assert.Equal(Commit, identity.SourceCommit);
        Assert.Equal(Fingerprint, identity.SourceFingerprint);
    }

    [Fact]
    public void Assess_RejectsOldInformationalVersionSuffixEvenWhenBothAssembliesMatch()
    {
        var identity = Assess(
            Assembly("LogForesight.Web", Stamp("clean", Fingerprint), $"1.0.53.2+{new string('f', 40)}"),
            Assembly("LogForesight.Core", Stamp("clean", Fingerprint), $"1.0.53.2+{new string('f', 40)}"));

        Assert.Equal("version-mismatch", identity.Status);
        Assert.Equal("informational-version-source-mismatch", identity.Reason);
        Assert.False(identity.IsReadyForLatestAcceptance);
    }

    [Fact]
    public void Assess_RejectsMixedWebCoreSourceStamps()
    {
        var identity = Assess(Assembly("LogForesight.Web", Stamp("clean", Fingerprint)),
            Assembly("LogForesight.Core", Stamp("clean", new string('1', 64))));

        Assert.Equal("mixed", identity.Status);
        Assert.Equal("web-core-source-stamp-mismatch", identity.Reason);
    }

    [Fact]
    public void Assess_RejectsUnknownGitSourceEvenWhenFingerprintExists()
    {
        var unknown = $"lfbuild-v1|unknown||{Fingerprint}";
        var identity = Assess(Assembly("LogForesight.Web", unknown), Assembly("LogForesight.Core", unknown));

        Assert.Equal("unknown-source", identity.Status);
        Assert.Equal("git-source-unavailable", identity.Reason);
    }

    [Fact]
    public void Assess_RejectsGlobalSourceRevisionOverrideThatDisagreesWithActualHead()
    {
        var staleOverride = new string('f', 40);
        var identity = Assess(
            Assembly("LogForesight.Web", Stamp("modified", Fingerprint), sourceRevisionOverride: staleOverride),
            Assembly("LogForesight.Core", Stamp("modified", Fingerprint), sourceRevisionOverride: staleOverride));

        Assert.Equal("version-mismatch", identity.Status);
        Assert.Equal("informational-version-source-mismatch", identity.Reason);
    }

    [Fact]
    public void Assess_TokenIncludesBothLoadedBinaryIdentities()
    {
        var web = Assembly("LogForesight.Web", Stamp("modified", Fingerprint));
        var core = Assembly("LogForesight.Core", Stamp("modified", Fingerprint));
        var baseline = Assess(web, core);
        var changedCore = Assess(web, core with { FileSha256 = new string('a', 64) });

        Assert.NotEqual(baseline.Token, changedCore.Token);
    }

    private static LogForesightBuildIdentity Assess(LogForesightBuildAssemblyIdentity web,
        LogForesightBuildAssemblyIdentity core) => LogForesightBuildIdentity.Assess(web, core);

    private static string Stamp(string state, string fingerprint) => $"lfbuild-v1|{state}|{Commit}|{fingerprint}";

    private static LogForesightBuildAssemblyIdentity Assembly(string name, string stamp,
        string? version = null, string? sourceRevisionOverride = null) => new(
        name,
        version ?? $"1.0.53.2+{Commit}",
        Guid.NewGuid().ToString("D"),
        new string(name == "LogForesight.Web" ? 'b' : 'c', 64),
        stamp,
        sourceRevisionOverride);
}
