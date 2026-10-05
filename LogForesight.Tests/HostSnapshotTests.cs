using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using Xunit;

namespace LogForesight.Tests;

public sealed class HostSnapshotTests : IDisposable
{
    private readonly EfSqliteFixture _fixture = new();

    [Fact]
    public void MissingBlobIsAValidEmptySnapshotButExistingInvalidBlobsFailClosed()
    {
        var missing = new HostStore(_fixture.Blob("missing-host-snapshot")).CapturePrtgSnapshot();
        Assert.Empty(missing.Hosts);
        Assert.Equal(0, missing.Version);

        foreach (var (key, raw) in new[]
        {
            ("blank-host-snapshot", " "),
            ("null-host-snapshot", "null"),
            ("malformed-host-snapshot", "[{"),
            ("duplicate-host-snapshot", "[{\"HostId\":7,\"HostName\":\"A\"},{\"HostId\":7,\"HostName\":\"B\"}]")
        })
        {
            var blob = _fixture.Blob(key);
            blob.Mutate(_ => (raw, true));
            Assert.ThrowsAny<Exception>(() => new HostStore(blob).CapturePrtgSnapshot());
        }
    }

    [Fact]
    public void HostBlobAtUtf16LimitIsAcceptedAndTheNextCharacterIsRejected()
    {
        const string prefix = "[{\"HostId\":1,\"HostName\":\"A\",\"RoleDesc\":\"";
        const string suffix = "\"}]";
        var exact = prefix + new string('x', PrtgHostSnapshot.MaximumTextCharacters - prefix.Length - suffix.Length) + suffix;
        Assert.Equal(PrtgHostSnapshot.MaximumTextCharacters, exact.Length);
        var blob = _fixture.Blob("exact-bound-host-snapshot");
        blob.Mutate(_ => (exact, true));
        Assert.Equal(1, Assert.Single(new HostStore(blob).CapturePrtgSnapshot().Hosts).HostId);

        var over = _fixture.Blob("over-bound-host-snapshot");
        over.Mutate(_ => ("[]" + new string(' ', PrtgHostSnapshot.MaximumTextCharacters - 1), true));
        Assert.Throws<InvalidDataException>(() => new HostStore(over).CapturePrtgSnapshot());
    }

    [Fact]
    public void CaptureIncludesThreeThousandRowsAndCopiesMutableAuthorizationLists()
    {
        var blob = _fixture.Blob("large-host-snapshot");
        blob.Mutate(_ => (System.Text.Json.JsonSerializer.Serialize(Enumerable.Range(1, 3000).Select(i =>
            new WebHost { HostId = i, HostName = $"host-{i:D4}", GroupIds = new() { i }, OwnerUserIds = new() { i + 10_000 } })), true));
        var store = new HostStore(blob);

        var snapshot = store.CapturePrtgSnapshot();
        Assert.Equal(3000, snapshot.Hosts.Count);
        Assert.Equal(store.DataVersion, snapshot.Version);
        var entry = snapshot.Find(1)!;
        var original = store.Get(1)!;
        original.HostName = "changed-after-capture";
        original.GroupIds.Add(999);
        Assert.Equal("host-0001", entry.HostName);
        Assert.Equal(new long[] { 1 }, entry.GroupIds);
        Assert.Equal(new long[] { 10_001 }, entry.OwnerUserIds);
        Assert.Throws<NotSupportedException>(() => ((IList<long>)entry.GroupIds)[0] = 999);

        var oversizedFake = new FakeHostStore();
        oversizedFake.Upsert(new WebHost { HostName = "host", RoleDesc = new string('x', PrtgHostSnapshot.MaximumTextCharacters / 6) });
        Assert.Throws<InvalidDataException>(() => ((IHostStore)oversizedFake).CapturePrtgSnapshot());
    }

    [Fact]
    public void FakeCompatibilityAdapterReadsAllOnceAndRejectsVersionRace()
    {
        var fake = new FakeHostStore();
        fake.Upsert(new WebHost { HostName = "one" });
        var captured = ((IHostStore)fake).CapturePrtgSnapshot();
        Assert.Single(captured.Hosts);
        Assert.Equal(1, fake.GetAllCallCount);

        var racing = new VersionRaceHostStore();
        Assert.Throws<InvalidDataException>(() => ((IHostStore)racing).CapturePrtgSnapshot());
        Assert.Equal(1, racing.GetAllCallCount);
    }

    public void Dispose() => _fixture.Dispose();

    private sealed class VersionRaceHostStore : IHostStore
    {
        private int _versionReads;
        public int GetAllCallCount { get; private set; }
        public List<WebHost> GetAll() { GetAllCallCount++; return new() { new WebHost { HostId = 1, HostName = "host" } }; }
        public long DataVersion => ++_versionReads;
        public WebHost? Get(long hostId) => throw new NotSupportedException();
        public WebHost? FindByName(string hostName) => throw new NotSupportedException();
        public WebHost Upsert(WebHost host) => throw new NotSupportedException();
        public WebHost Touch(string hostName, DateTime reportedAt, string source = "local") => throw new NotSupportedException();
        public WebHost? TouchNetiq(long hostId, string? displayName, DateTime reportedAt) => throw new NotSupportedException();
        public void SetGroups(long hostId, IEnumerable<long> groupIds) => throw new NotSupportedException();
        public void SetHighVolume(long hostId, bool isHighVolume) => throw new NotSupportedException();
        public HostGroupsBatchResult SetGroupsBatch(IEnumerable<long> hostIds, IEnumerable<long> groupIds, bool replace) => throw new NotSupportedException();
        public void SetOwners(long hostId, IEnumerable<long> userIds) => throw new NotSupportedException();
        public void Merge(long sourceHostId, long targetHostId) => throw new NotSupportedException();
        public void Unmerge(long hostId) => throw new NotSupportedException();
        public TResult MutateBatch<TResult>(Func<List<WebHost>, TResult> mutation) => throw new NotSupportedException();
        public void MutateBatch(Action<List<WebHost>> mutation) => throw new NotSupportedException();
    }
}
