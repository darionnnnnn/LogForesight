using System.Text.Json;
using LogForesight.Core.Configuration;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgDiskChannelReadSetTests
{
    [Fact]
    public void ReusedPairRejectsChangedVersionAndFreshPairRequiresEveryEntryValid()
    {
        var root=Path.Combine(Path.GetTempPath(),"lf-r54-disk-readset-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var backend=new StorageBackend(new StorageSettings {Type="Sqlite",ConnectionString="Data Source="+
            Path.Combine(root,"owned.db")+";Pooling=False"},root);
        var now=DateTime.UtcNow;
        var evidence=new PrtgDiskSemanticEvidence(11,1,1,"disk","free","Free","%",1,"descending-danger",
            PrtgDiskSemanticEvidenceSource.Automated,null,"bounded readset",now,
            PrtgDiskAssessmentService.ParserSemanticVersion,"source","resource","channel",1);
        var result=new PrtgDiskVerificationResult(11,1,1,"disk","Verified","bounded readset","free","Free","%",1,
            "descending-danger",1,true,now,now.Date,PrtgDiskAssessmentService.ParserSemanticVersion,
            SourceGeneration:"source",ResourceGeneration:"resource",ChannelGeneration:"channel",IdentityEpoch:1);
        var semanticBlob=backend.Blob(PrtgDiskSemanticEvidenceStore.BlobKey);
        var resultBlob=backend.Blob(PrtgDiskVerificationResultStore.BlobKey);
        semanticBlob.Mutate(_ => (JsonSerializer.Serialize(new Dictionary<long,PrtgDiskSemanticEvidence>{{11,evidence}}),true));
        resultBlob.Mutate(_ => (JsonSerializer.Serialize(new Dictionary<long,PrtgDiskVerificationResult>{{11,result}}),true));
        var identity=new PrtgResourceIdentity {SensorId=11,DeviceId=1,HostId=1,Active=true,Epoch=1,
            SourceGeneration="source",Generation="resource",ChannelGeneration="channel"};
        var readset=PrtgResourceQualification.CaptureDiskChannelAuthority(backend);
        Assert.NotNull(readset);
        Assert.True(readset.IsCurrent(11,identity));
        Assert.True(readset.IsCurrent(11,identity));
        semanticBlob.Mutate(raw => (raw!,true));
        Assert.False(readset.IsCurrent(11,identity));
        Assert.True(PrtgResourceQualification.CaptureDiskChannelAuthority(backend)!.IsCurrent(11,identity));
        semanticBlob.Mutate(raw => (raw![..^1]+",\"12\":null}",true));
        var before=semanticBlob.ReadWithVersion();
        Assert.Null(PrtgResourceQualification.CaptureDiskChannelAuthority(backend));
        Assert.Equal(before,semanticBlob.ReadWithVersion());
    }
}
