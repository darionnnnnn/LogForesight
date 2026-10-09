using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgHistoricXmlReaderTests
{
    private const string Item = "<item><datetime_raw>46288.5</datetime_raw><value channel=\"Free Space\" channelid=\"3\">24 %</value><value_raw channel=\"Free Space\" channelid=\"3\">24</value_raw><value_raw channel=\"Downtime\" channelid=\"0\">0</value_raw></item>";
    private static string Document(string content = Item, string count = "1") =>
        $"<histdata totalcount=\"{count}\"><prtg-version>24.1.92.1554+</prtg-version>{content}</histdata>";

    [Theory]
    [InlineData("24")]
    [InlineData("24.125")]
    [InlineData("-24")]
    [InlineData("0")]
    [InlineData("1e2")]
    public void KeepsActualChannelIdentityAndRawOaClockWithoutGuessing(string raw)
    {
        var result = PrtgHistoricXmlReader.Parse(Document(Item.Replace(">24</value_raw>", $">{raw}</value_raw>")));
        Assert.Equal("24.1.92.1554+", result.Version);
        var sample = Assert.Single(result.Samples);
        Assert.Equal(46288.5, sample.MeasuredOaDate);
        Assert.Equal(2, sample.Channels.Count);
        Assert.Equal("3", sample.Channels[0].ChannelId);
        Assert.Equal("Free Space", sample.Channels[0].Caption);
        Assert.Equal(double.Parse(raw, System.Globalization.CultureInfo.InvariantCulture), sample.Channels[0].RawValue);
        Assert.Equal("24 %", sample.Channels[0].DisplayValue);
        Assert.Equal("0", sample.Channels[1].ChannelId);
    }

    public static IEnumerable<object[]> InvalidDocuments()
    {
        yield return ["<html>login</html>"];
        yield return ["<!DOCTYPE histdata [<!ENTITY x SYSTEM 'file:///private'>]><histdata>&x;</histdata>"];
        yield return [Document(Item, "2")];
        yield return [Document(Item, "-1")];
        yield return [Document(Item, "broken")];
        yield return [Document(Item + Item, "2")];
        yield return [Document(Item.Replace("46288.5", "NaN"))];
        yield return [Document(Item.Replace("46288.5", "Infinity"))];
        yield return [Document(Item.Replace("46288.5", "2958466"))];
        yield return [Document(Item.Replace("46288.5", "-657436"))];
        yield return [Document(Item.Replace("<datetime_raw>46288.5</datetime_raw>", ""))];
        yield return [Document(Item.Replace("</datetime_raw>", "</datetime_raw><datetime_raw>46289</datetime_raw>"))];
        yield return [Document(Item.Replace("channelid=\"3\"", "channelid=\"abc\""))];
        yield return [Document(Item.Replace("channelid=\"3\"", "channelid=\"-1\""))];
        yield return [Document(Item.Replace("channelid=\"0\"", "channelid=\"3\""))];
        yield return [Document(Item.Replace("channel=\"Free Space\"", "channel=\"\""))];
        yield return [Document(Item.Replace(">24</value_raw>", ">NaN</value_raw>"))];
        yield return [Document(Item.Replace(">24</value_raw>", ">Infinity</value_raw>"))];
        yield return [Document(Item.Replace("<value channel=\"Free Space\"", "<value channel=\"Other\""))];
        yield return [Document(Item.Replace("</item>", "<value channel=\"Free Space\" channelid=\"3\">24 %</value></item>"))];
        yield return [Document(Item.Replace("channel=\"Free Space\"", $"channel=\"{new string('a', 257)}\""))];
        yield return [Document(Item.Replace("<item>", "<item><a><b><c><d><e><f><g><h><i/>").Replace("</item>", "</h></g></f></e></d></c></b></a></item>"))];
        yield return [new string('x', PrtgHistoricXmlReader.MaximumBytes + 1)];
        yield return [Document(string.Concat(Enumerable.Range(0, 513).Select(i => Item.Replace("46288.5", (46288.0 + i / 1440.0).ToString("R", System.Globalization.CultureInfo.InvariantCulture)))), "513")];
        yield return [Document(Item.Replace("</item>", string.Concat(Enumerable.Range(4, 65).Select(i => $"<value_raw channel=\"X\" channelid=\"{i}\">1</value_raw>")) + "</item>"))];
        yield return [Document(Item + Item.Replace("46288.5", "46288.6").Replace("Free Space", "Other"), "2")];
    }

    [Theory, MemberData(nameof(InvalidDocuments))]
    public void RejectsMalformedAmbiguousIncompleteOrUnboundedSource(string xml) =>
        Assert.Throws<InvalidDataException>(() => PrtgHistoricXmlReader.Parse(xml));
}
