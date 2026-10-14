using System.Text.Json;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgResourceModeBoundedFindTests : IDisposable
{
    private readonly EfSqliteFixture _fixture = new();
    public void Dispose() => _fixture.Dispose();
    private static PrtgResourcePressureModeGrant Grant(long host) => new(host, host * 10,
        PrtgResourceFamily.Cpu, new string('a',64), PrtgResourcePressureEvaluator.RulesVersion,
        "source", "resource", "channel", "1", "semantic", "strategy", "rule", new string('b',64),
        new string('c',64), true, true, DateTime.UtcNow, DateTime.UtcNow.AddHours(24), TrialConsumed:true);
    private PrtgResourcePressureModeStore Store(long host) => new(_fixture.Blob(PrtgResourcePressureModeStore.BlobKey(host)));
    private void Put(long host, string content) => _fixture.Blob(PrtgResourcePressureModeStore.BlobKey(host)).Mutate(_ => (content,true));
    private static string WithPadding(long host, int length)
    {
        var prefix = "{\"Grants\":[" + JsonSerializer.Serialize(Grant(host)) + "],\"Padding\":\"";
        return prefix + new string('x', length-prefix.Length-2) + "\"}";
    }
    [Fact]
    public void FindAcceptsExactCharacterCapButRejectsLargerDocumentAndPreservesOtherHost()
    {
        Put(1, WithPadding(1,PrtgResourcePressureModeStore.MaximumDocumentCharacters));
        Assert.Equal(10, Store(1).Find(1,10,PrtgResourceFamily.Cpu)!.SensorObjid);
        Put(2,JsonSerializer.Serialize(new PrtgResourcePressureModeDocument { Grants=[Grant(2)] }));
        Put(1, WithPadding(1,PrtgResourcePressureModeStore.MaximumDocumentCharacters+1));
        Assert.Throws<InvalidDataException>(() => Store(1).Find(1,10,PrtgResourceFamily.Cpu));
        Assert.Equal(20,Store(2).Find(2,20,PrtgResourceFamily.Cpu)!.SensorObjid);
    }
    [Fact]
    public void FindRejectsMixedHostDocumentEvenWhenSelectedGrantIsValid()
    {
        Put(1,JsonSerializer.Serialize(new PrtgResourcePressureModeDocument { Grants=[Grant(1),Grant(2)] }));
        Assert.Throws<InvalidDataException>(() => Store(1).Find(1,10,PrtgResourceFamily.Cpu));
    }
    [Fact]
    public void FindHandlesMissingAndWrongSensorWithoutInventingAuthority()
    {
        Assert.Null(Store(1).Find(1,10,PrtgResourceFamily.Cpu));
        Put(1,JsonSerializer.Serialize(new PrtgResourcePressureModeDocument { Grants=[Grant(1)] }));
        Assert.Null(Store(1).Find(1,11,PrtgResourceFamily.Cpu));
        Assert.Null(Store(1).Find(0,10,PrtgResourceFamily.Cpu));
    }
}
