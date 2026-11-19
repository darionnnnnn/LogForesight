using System.Diagnostics.Eventing.Reader;
using System.Runtime.Versioning;
using System.Text;
using LogForesight.Core.Analysis;
using Xunit;
using Xunit.Abstractions;

namespace LogForesight.Tests;

/// <summary>
/// 讀 Windows 事件提供者中繼資料（<see cref="ProviderMetadata"/>）取得「規則事件到底是什麼等級」的事實，
/// 作為頻道政策「資訊等級也收」明列清單（<see cref="ChannelCatalog.LegacyInformationalAllowList"/>）的依據：
/// 7045／104 是資訊等級所以要明列；NTFS 98 同編號卻有資訊等級的健康訊息，所以不能整個編號收。
/// 只在 Windows 執行（非 Windows 略過）；提供者在該機器上沒安裝時標為「查不到」、不讓測試失敗。
/// 斷言失敗訊息與測試輸出都附上實際讀到的所有版本（提供者、版本、等級、訊息樣板）——
/// 在 CI Windows 的記錄裡找本類別名稱即可取得事實。
/// </summary>
[SupportedOSPlatform("windows")]
public class EventProviderMetadataWindowsTests
{
    /// <summary>EventLevel.Value：win:Informational。</summary>
    private const int InformationalLevel = 4;

    private readonly ITestOutputHelper _output;

    public EventProviderMetadataWindowsTests(ITestOutputHelper output) => _output = output;

    private sealed record EventVersion(string Provider, byte Version, int LevelValue, string LevelName, string Template);

    /// <summary>讀一組候選提供者裡指定編號的所有版本；提供者不存在者列入 <paramref name="missing"/>。</summary>
    private static List<EventVersion> ReadVersions(string[] providers, int eventId, List<string> missing)
    {
        var versions = new List<EventVersion>();
        foreach (var name in providers)
        {
            ProviderMetadata metadata;
            try
            {
                metadata = new ProviderMetadata(name);
            }
            catch (Exception ex) when (ex is EventLogNotFoundException or EventLogException or UnauthorizedAccessException)
            {
                missing.Add(name);
                continue;
            }

            using (metadata)
            {
                IEnumerable<EventMetadata> events;
                try
                {
                    events = metadata.Events;
                }
                catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
                {
                    missing.Add($"{name}（讀不到事件清單：{ex.GetType().Name}）");
                    continue;
                }

                foreach (var e in events.Where(e => e.Id == eventId))
                {
                    versions.Add(new EventVersion(
                        name,
                        e.Version,
                        e.Level?.Value ?? -1,
                        e.Level?.Name ?? "(無)",
                        (e.Description ?? "(無樣板)").Replace("\r", " ").Replace("\n", " ")));
                }
            }
        }
        return versions;
    }

    private static string Report(string title, int eventId, List<EventVersion> versions, List<string> missing)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[事件提供者中繼資料] {title}（事件 {eventId}）");
        if (missing.Count > 0)
        {
            sb.AppendLine($"  查不到的提供者：{string.Join("、", missing)}");
        }
        if (versions.Count == 0)
        {
            sb.AppendLine("  讀不到任何版本");
        }
        foreach (var v in versions)
        {
            sb.AppendLine($"  提供者={v.Provider} 版本={v.Version} 等級={v.LevelName}({v.LevelValue}) 樣板={v.Template}");
        }
        return sb.ToString();
    }

    /// <summary>讀取並輸出；整組提供者都查不到時回傳 null（呼叫端略過斷言）。</summary>
    private List<EventVersion>? Probe(string title, string[] providers, int eventId)
    {
        var missing = new List<string>();
        var versions = ReadVersions(providers, eventId, missing);
        var report = Report(title, eventId, versions, missing);
        _output.WriteLine(report);

        // 提供者都不存在（例如沒裝該元件）：標為查不到，不讓測試失敗
        if (missing.Count == providers.Length)
        {
            return null;
        }
        return versions;
    }

    [WindowsFact]
    public void 服務控制管理員7045為資訊等級()
    {
        var providers = new[] { "Service Control Manager" };
        var versions = Probe("Service Control Manager", providers, 7045);
        if (versions == null) return;

        var report = Report("Service Control Manager", 7045, versions, new List<string>());
        if (versions.Count > 0)
        {
            Assert.All(versions, v => Assert.True(v.LevelValue == InformationalLevel,
                "7045 預期所有版本為資訊等級（所以頻道政策要明列才收得到）。\n" + report));
        }
        else
        {
            // Service Control Manager 是舊式（訊息檔）提供者，沒有清單式中繼資料可讀：改看 System 記錄裡實際的 7045。
            // 機器上沒有任何 7045 時標為查不到，不讓測試失敗
            var levels = new List<byte?>();
            var query = new System.Diagnostics.Eventing.Reader.EventLogQuery("System",
                System.Diagnostics.Eventing.Reader.PathType.LogName,
                "*[System[Provider[@Name='Service Control Manager'] and (EventID=7045)]]") { ReverseDirection = true };
            using (var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(query))
            {
                for (var i = 0; i < 20 && reader.ReadEvent() is { } record; i++)
                    using (record) levels.Add(record.Level);
            }
            _output.WriteLine($"[System 記錄] Service Control Manager 7045 實際事件 {levels.Count} 筆，等級：{string.Join(",", levels)}");
            if (levels.Count == 0) return;
            Assert.All(levels, level => Assert.True(level is 0 or InformationalLevel,
                $"7045 的實際事件預期為資訊等級（0 或 4）：{string.Join(",", levels)}\n" + report));
        }
        Assert.Contains(ChannelCatalog.LegacyInformationalAllowList,
            e => e.EventId == 7045 && providers[0].Contains(e.ProviderPattern, StringComparison.OrdinalIgnoreCase));
    }

    [WindowsFact]
    public void 事件日誌清除104為資訊等級()
    {
        // 104 的提供者是 Microsoft-Windows-Eventlog；classic 來源名為 EventLog（規則 SourcePattern 同此）
        var providers = new[] { "Microsoft-Windows-Eventlog", "Eventlog" };
        var versions = Probe("Eventlog", providers, 104);
        if (versions == null) return;

        var report = Report("Eventlog", 104, versions, new List<string>());
        Assert.True(versions.Count > 0, "提供者存在但沒有 104 事件定義。\n" + report);
        Assert.All(versions, v => Assert.True(v.LevelValue == InformationalLevel,
            "104 預期所有版本為資訊等級（所以頻道政策要明列才收得到）。\n" + report));
        Assert.Contains(ChannelCatalog.LegacyInformationalAllowList,
            e => e.EventId == 104 && providers.Any(p => p.Contains(e.ProviderPattern, StringComparison.OrdinalIgnoreCase)));
    }

    [WindowsFact]
    public void NTFS98同時存在資訊等級版本所以不能整個編號收()
    {
        // 種子規則 builtin-storage-ntfs-corruption 的 SourcePattern 是 Ntfs；manifest 提供者名是 Microsoft-Windows-Ntfs
        var providers = new[] { "Ntfs", "Microsoft-Windows-Ntfs" };
        var versions = Probe("Ntfs", providers, 98);
        if (versions == null) return;

        var report = Report("Ntfs", 98, versions, new List<string>());
        Assert.True(versions.Count > 0, "提供者存在但沒有 98 事件定義。\n" + report);
        Assert.True(versions.Any(v => v.LevelValue == InformationalLevel),
            "NTFS 98 預期同時存在資訊等級的版本（磁碟區健康訊息）；若沒有，重新評估是否仍需排除整個編號。\n" + report);

        // 明列清單不得以編號整收 NTFS 98／130
        Assert.DoesNotContain(ChannelCatalog.LegacyInformationalAllowList, e => e.EventId is 98 or 130);
    }

    [WindowsFact]
    public void NTFS130等級與訊息樣板記錄()
    {
        // 130 只記錄事實供日後判斷；不做等級斷言，但不得被明列清單整收
        var providers = new[] { "Ntfs", "Microsoft-Windows-Ntfs" };
        var versions = Probe("Ntfs", providers, 130);
        if (versions == null) return;

        Assert.DoesNotContain(ChannelCatalog.LegacyInformationalAllowList, e => e.EventId == 130);
    }
}
