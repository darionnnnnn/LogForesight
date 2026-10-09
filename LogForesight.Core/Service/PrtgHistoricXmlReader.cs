using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace LogForesight.Core.Service;

public sealed record PrtgHistoricXmlChannel(string ChannelId, string Caption, double RawValue, string? DisplayValue);
public sealed record PrtgHistoricXmlSample(double MeasuredOaDate, IReadOnlyList<PrtgHistoricXmlChannel> Channels);
public sealed record PrtgHistoricXmlDocument(string? Version, int? DeclaredCount, IReadOnlyList<PrtgHistoricXmlSample> Samples);

/// <summary>Preserves actual historic channel IDs. Raw OA wall times remain unqualified until their clock is proved.</summary>
public static class PrtgHistoricXmlReader
{
    public const int MaximumBytes = 512 * 1024;
    public const int MaximumSamples = 512;
    public const int MaximumChannels = 64;

    public static PrtgHistoricXmlDocument Parse(string xml)
    {
        if (Encoding.UTF8.GetByteCount(xml) > MaximumBytes)
            throw new InvalidDataException("historic-xml-byte-limit");
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = MaximumBytes, MaxCharactersFromEntities = 0
        };
        try
        {
            using (var check = XmlReader.Create(new StringReader(xml), settings))
                while (check.Read())
                    if (check.Depth > 8) throw new InvalidDataException("historic-xml-depth-limit");
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            var doc = XDocument.Load(reader, LoadOptions.None);
            if (doc.Root?.Name != "histdata") throw new InvalidDataException("historic-xml-root-invalid");
            var countText = (string?)doc.Root.Attribute("totalcount");
            int? declared = countText is null ? null : ParseCount(countText);
            var items = doc.Root.Elements("item").Take(MaximumSamples + 1).ToArray();
            if (items.Length > MaximumSamples) throw new InvalidDataException("historic-xml-row-limit");
            if (declared.HasValue && declared.Value != items.Length)
                throw new InvalidDataException("historic-xml-count-incomplete");
            var samples = new List<PrtgHistoricXmlSample>(items.Length);
            var timestamps = new HashSet<double>();
            var captions = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var item in items)
            {
                var times = item.Elements("datetime_raw").Take(2).ToArray();
                if (times.Length != 1 || !Number(times[0].Value, out var oa) || oa is < -657435 or >= 2958466)
                    throw new InvalidDataException("historic-xml-measurement-time-invalid");
                _ = DateTime.FromOADate(oa);
                if (!timestamps.Add(oa)) throw new InvalidDataException("historic-xml-duplicate-measurement");
                var values = item.Elements("value_raw").Take(MaximumChannels + 1).ToArray();
                if (values.Length is < 1 or > MaximumChannels) throw new InvalidDataException("historic-xml-channel-count-invalid");
                var ids = new HashSet<string>(StringComparer.Ordinal);
                var channels = new List<PrtgHistoricXmlChannel>(values.Length);
                foreach (var value in values)
                {
                    var id = (string?)value.Attribute("channelid");
                    var caption = (string?)value.Attribute("channel");
                    if (id is null || !long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var numericId) || numericId < 0 ||
                        string.IsNullOrWhiteSpace(caption) || caption.Length > 256 || !ids.Add(id) || !Number(value.Value, out var raw))
                        throw new InvalidDataException("historic-xml-channel-identity-or-value-invalid");
                    if (captions.TryGetValue(id, out var prior) && prior != caption)
                        throw new InvalidDataException("historic-xml-channel-caption-drift");
                    captions[id] = caption;
                    var display = item.Elements("value").Where(e => (string?)e.Attribute("channelid") == id).Take(2).ToArray();
                    if (display.Length > 1 || display.Length == 1 && (string?)display[0].Attribute("channel") != caption)
                        throw new InvalidDataException("historic-xml-display-channel-identity-invalid");
                    var formatted = display.Length == 1 && display[0].Value.Length <= 256 ? display[0].Value : null;
                    channels.Add(new(id, caption, raw, formatted));
                }
                samples.Add(new(oa, channels));
            }
            var versions = doc.Root.Elements("prtg-version").Take(2).ToArray();
            if (versions.Length > 1) throw new InvalidDataException("historic-xml-version-ambiguous");
            return new(versions.Length == 1 && versions[0].Value.Length <= 64 ? versions[0].Value : null, declared, samples);
        }
        catch (Exception ex) when (ex is XmlException or ArgumentException)
        { throw new InvalidDataException("historic-xml-invalid", ex); }
    }

    private static int ParseCount(string value) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) &&
        n >= 0 ? n : throw new InvalidDataException("historic-xml-declared-count-invalid");
    private static bool Number(string text, out double n) => double.TryParse(text, NumberStyles.Float,
        CultureInfo.InvariantCulture, out n) && double.IsFinite(n);
}
