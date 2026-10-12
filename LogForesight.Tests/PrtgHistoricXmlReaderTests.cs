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

    // Official Paessler channel_settings ID variants, represented as a controlled XML variation;
    // this is not a captured site response.
    [Theory]
    [InlineData("-1")]
    [InlineData("-2")]
    [InlineData("-3")]
    [InlineData("-4")]
    [InlineData("0")]
    [InlineData("9223372036854775807")]
    public void PreservesCanonicalOfficialChannelIds(string channelId)
    {
        var variation = Item.Replace("channelid=\"0\"", "channelid=\"9\"")
            .Replace("channelid=\"3\"", $"channelid=\"{channelId}\"");
        var xml = Document(variation);
        var result = PrtgHistoricXmlReader.Parse(xml);
        Assert.Equal(channelId, result.Samples[0].Channels[0].ChannelId);
    }

    // Controlled reproduction of Paessler's documented historicdata.xml example:
    // https://helpdesk.paessler.com/en/support/solutions/articles/76000068661-get-the-network-volume-with-the-api
    // The published sample uses the same ID for volume and speed, plus -1 sum and -4 downtime.
    [Fact]
    public void OfficialTrafficFormatKeepsSameIdVolumeAndSpeedAsSeparateQuantities()
    {
        const string item = "<item><datetime_raw>43263.875</datetime_raw>" +
            "<value channel=\"Traffic Total (volume)\" channelid=\"-1\">3.944 MByte</value>" +
            "<value_raw channel=\"Traffic Total (volume)\" channelid=\"-1\">4135535619.0572</value_raw>" +
            "<value channel=\"Traffic Total (speed)\" channelid=\"-1\">0,38 Mbit/s</value>" +
            "<value_raw channel=\"Traffic Total (speed)\" channelid=\"-1\">47868.8351</value_raw>" +
            "<value channel=\"Traffic In (volume)\" channelid=\"0\">651 MByte</value>" +
            "<value_raw channel=\"Traffic In (volume)\" channelid=\"0\">682854522.3689</value_raw>" +
            "<value channel=\"Traffic In (speed)\" channelid=\"0\">0,06 Mbit/s</value>" +
            "<value_raw channel=\"Traffic In (speed)\" channelid=\"0\">7904.0428</value_raw>" +
            "<value channel=\"Downtime\" channelid=\"-4\">0 %</value>" +
            "<value_raw channel=\"Downtime\" channelid=\"-4\">0.0000</value_raw>" +
            "<coverage>100 %</coverage><coverage_raw>0000009999</coverage_raw></item>";

        var sample = Assert.Single(PrtgHistoricXmlReader.Parse(Document(item)).Samples);
        Assert.Collection(sample.Channels,
            totalVolume => { Assert.Equal("-1", totalVolume.ChannelId); Assert.Equal("volume", totalVolume.QuantityKind); Assert.Equal(4135535619.0572, totalVolume.RawValue); },
            totalSpeed => { Assert.Equal("-1", totalSpeed.ChannelId); Assert.Equal("speed", totalSpeed.QuantityKind); Assert.Equal(47868.8351, totalSpeed.RawValue); },
            inVolume => { Assert.Equal("0", inVolume.ChannelId); Assert.Equal("volume", inVolume.QuantityKind); },
            inSpeed => { Assert.Equal("0", inSpeed.ChannelId); Assert.Equal("speed", inSpeed.QuantityKind); },
            downtime => { Assert.Equal("-4", downtime.ChannelId); Assert.Equal("unknown", downtime.QuantityKind); });
    }

    [Fact]
    public void TrueDuplicateIdAndQuantityIsRejected()
    {
        const string duplicate = "<item><datetime_raw>46288.5</datetime_raw>" +
            "<value_raw channel=\"Traffic Total (volume)\" channelid=\"-1\">3</value_raw>" +
            "<value_raw channel=\"Traffic Total (volume)\" channelid=\"-1\">4</value_raw></item>";
        Assert.Throws<InvalidDataException>(() => PrtgHistoricXmlReader.Parse(Document(duplicate)));
    }

    [Theory]
    [InlineData("CPU", "CPU (speed)")]
    [InlineData("Traffic (volume)", "Traffic (speed)")]
    public void RejectsPerChannelQuantityDriftAcrossSamples(string firstCaption, string secondCaption)
    {
        var first = QuantityItem("46288.5", firstCaption);
        var second = QuantityItem("46288.6", secondCaption);

        var error = Assert.Throws<InvalidDataException>(() =>
            PrtgHistoricXmlReader.Parse(Document(first + second, "2")));

        Assert.Equal("historic-xml-channel-quantity-drift", error.Message);
    }

    [Fact]
    public void SameIdVolumeAndSpeedMayCoexistAcrossSamples()
    {
        var item = "<item><datetime_raw>46288.5</datetime_raw>" +
            "<value_raw channel=\"Traffic Total (volume)\" channelid=\"-1\">3</value_raw>" +
            "<value_raw channel=\"Traffic Total (speed)\" channelid=\"-1\">4</value_raw></item>";
        var next = item.Replace("46288.5", "46288.6", StringComparison.Ordinal);

        var result = PrtgHistoricXmlReader.Parse(Document(item + next, "2"));

        Assert.Equal(2, result.Samples.Count);
        Assert.All(result.Samples, sample => Assert.Equal(new[] { "volume", "speed" },
            sample.Channels.Select(channel => channel.QuantityKind)));
    }

    private static string QuantityItem(string oaDate, string caption) =>
        $"<item><datetime_raw>{oaDate}</datetime_raw><value_raw channel=\"{caption}\" channelid=\"5\">1</value_raw></item>";

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
        yield return [Document(Item.Replace("channelid=\"3\"", "channelid=\"-5\""))];
        yield return [Document(Item.Replace("channelid=\"3\"", "channelid=\"+3\""))];
        yield return [Document(Item.Replace("channelid=\"3\"", "channelid=\"03\""))];
        yield return [Document(Item.Replace("channelid=\"3\"", "channelid=\"9223372036854775808\""))];
        yield return [Document(Item.Replace("channelid=\"0\"", "channelid=\"03\"").Replace("channelid=\"3\"", "channelid=\"3\""))];
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
