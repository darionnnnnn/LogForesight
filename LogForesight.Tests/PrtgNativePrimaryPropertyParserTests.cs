using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgNativePrimaryPropertyParserTests
{
    [Fact]
    public void AcceptsExactBoundedPrtgXmlEnvelope()
    {
        Assert.Equal("3", PrtgTrustedSamplingProbeService.ParseNativePrimaryProperty(
            "<prtg><result>3</result></prtg>"));
    }

    [Theory]
    [InlineData("3")]
    [InlineData("<!DOCTYPE prtg [<!ENTITY x SYSTEM 'file:///forbidden'>]><prtg><result>&x;</result></prtg>")]
    [InlineData("<prtg><result>3</result><result>4</result></prtg>")]
    public void RejectsPlainDtdAndAmbiguousShapes(string response)
    {
        Assert.Throws<InvalidDataException>(() =>
            PrtgTrustedSamplingProbeService.ParseNativePrimaryProperty(response));
    }
}
