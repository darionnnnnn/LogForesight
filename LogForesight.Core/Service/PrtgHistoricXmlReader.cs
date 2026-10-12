using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace LogForesight.Core.Service;

public sealed record PrtgHistoricXmlChannel(string ChannelId, string Caption, double RawValue, string? DisplayValue,
    string QuantityKind = "unknown");
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
            var captions = new Dictionary<(string Id, string QuantityKind), string>();
            var quantityKindsById = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var volumeAndSpeedCoexistence = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in items)
            {
                var times = item.Elements("datetime_raw").Take(2).ToArray();
                if (times.Length != 1 || !Number(times[0].Value, out var oa) || oa is < -657435 or >= 2958466)
                    throw new InvalidDataException("historic-xml-measurement-time-invalid");
                _ = DateTime.FromOADate(oa);
                if (!timestamps.Add(oa)) throw new InvalidDataException("historic-xml-duplicate-measurement");
                var values = item.Elements("value_raw").Take(MaximumChannels + 1).ToArray();
                if (values.Length is < 1 or > MaximumChannels) throw new InvalidDataException("historic-xml-channel-count-invalid");
                var displayValues = item.Elements("value").Take(MaximumChannels + 1).ToArray();
                if (displayValues.Length > MaximumChannels) throw new InvalidDataException("historic-xml-channel-count-invalid");
                var identities = new HashSet<(string Id, string QuantityKind)>();
                var sampleQuantityKindsById = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
                var channels = new List<PrtgHistoricXmlChannel>(values.Length);
                foreach (var value in values)
                {
                    var id = (string?)value.Attribute("channelid");
                    var caption = (string?)value.Attribute("channel");
                    if (id is null || !PrtgChannelIdPolicy.TryParseCanonical(id, out _))
                        throw new InvalidDataException("historic-xml-channel-id-invalid");
                    if (string.IsNullOrWhiteSpace(caption) || caption.Length > 256)
                        throw new InvalidDataException("historic-xml-channel-caption-invalid");
                    if (!Number(value.Value, out var raw))
                        throw new InvalidDataException("historic-xml-channel-value-invalid");
                    var quantityKind = ClassifyQuantity(caption);
                    var identity = (id, quantityKind);
                    if (!identities.Add(identity))
                        throw new InvalidDataException("historic-xml-duplicate-channel-quantity");
                    if (!sampleQuantityKindsById.TryGetValue(id, out var sampleKinds))
                        sampleQuantityKindsById[id] = sampleKinds = new HashSet<string>(StringComparer.Ordinal);
                    sampleKinds.Add(quantityKind);
                    if (captions.TryGetValue(identity, out var prior) && prior != caption)
                        throw new InvalidDataException("historic-xml-channel-caption-drift");
                    captions[identity] = caption;
                    if (!quantityKindsById.TryGetValue(id, out var allKinds))
                        quantityKindsById[id] = allKinds = new HashSet<string>(StringComparer.Ordinal);
                    allKinds.Add(quantityKind);
                    var displayRows = displayValues.Where(e => (string?)e.Attribute("channelid") == id).ToArray();
                    var display = displayRows.Where(e => (string?)e.Attribute("channel") == caption).Take(2).ToArray();
                    if (display.Length > 1 || displayRows.Length > 0 && display.Length == 0)
                        throw new InvalidDataException("historic-xml-display-channel-identity-invalid");
                    var formatted = display.Length == 1 && display[0].Value.Length <= 256 ? display[0].Value : null;
                    channels.Add(new(id, caption, raw, formatted, quantityKind));
                }
                foreach (var pair in sampleQuantityKindsById)
                {
                    if (pair.Value.Count > 1)
                    {
                        if (!pair.Value.SetEquals(new[] { "volume", "speed" }))
                            throw new InvalidDataException("historic-xml-channel-quantity-drift");
                        volumeAndSpeedCoexistence.Add(pair.Key);
                    }
                }
                samples.Add(new(oa, channels));
            }
            foreach (var pair in quantityKindsById)
                if (pair.Value.Count > 1 && (!pair.Value.SetEquals(new[] { "volume", "speed" }) ||
                    !volumeAndSpeedCoexistence.Contains(pair.Key)))
                    throw new InvalidDataException("historic-xml-channel-quantity-drift");
            var versions = doc.Root.Elements("prtg-version").Take(2).ToArray();
            if (versions.Length > 1) throw new InvalidDataException("historic-xml-version-ambiguous");
            return new(versions.Length == 1 && versions[0].Value.Length <= 64 ? versions[0].Value : null, declared, samples);
        }
        catch (Exception ex) when (ex is XmlException or ArgumentException)
        { throw new InvalidDataException("historic-xml-invalid", ex); }
    }

    private static int ParseCount(string value) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) &&
        n >= 0 ? n : throw new InvalidDataException("historic-xml-declared-count-invalid");
    private static string ClassifyQuantity(string caption) =>
        caption.EndsWith(" (volume)", StringComparison.OrdinalIgnoreCase) ? "volume" :
        caption.EndsWith(" (speed)", StringComparison.OrdinalIgnoreCase) ? "speed" : "unknown";
    private static bool Number(string text, out double n) => double.TryParse(text, NumberStyles.Float,
        CultureInfo.InvariantCulture, out n) && double.IsFinite(n);
}
