using System.Text;
using System.Text.Json;
using NLog;

namespace LogForesight;

/// <summary>
/// 以 suppressions.json 儲存主機級抑制設定。缺檔＝空清單（不是錯誤）；整檔損毀時同樣降級為
/// 空清單並記警告——抑制是可選的維運調校，讀取失敗不該讓分析流程整個失敗，只是這次沒有
/// 任何抑制生效（等同「還沒設定過」，比整個程式中斷安全）。
/// </summary>
public class JsonSuppressionStore : ISuppressionStore
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly IJsonBlobStore _blob;
    private readonly JsonSerializerOptions _options;

    public JsonSuppressionStore(IJsonBlobStore blob)
    {
        _blob = blob;
        _options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };
    }

    public string Location => _blob.Location;

    public List<RuleSuppression> LoadAll()
    {
        var text = _blob.Read();
        if (string.IsNullOrWhiteSpace(text))
        {
            return new List<RuleSuppression>();
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
        }
        catch (JsonException ex)
        {
            Log.Warn("suppressions 格式錯誤，本次視為無抑制設定：{Path}，原因：{Error}", _blob.Location, ex.Message);
            return new List<RuleSuppression>();
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                Log.Warn("suppressions 根節點不是陣列，本次視為無抑制設定：{Path}", _blob.Location);
                return new List<RuleSuppression>();
            }

            var result = new List<RuleSuppression>();
            int index = 0;
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                index++;
                try
                {
                    var item = element.Deserialize<RuleSuppression>(_options);
                    if (item != null)
                    {
                        result.Add(item);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("suppressions.json 第 {Index} 條解析失敗，已跳過：{Error}", index, ex.Message);
                }
            }

            return result;
        }
    }

    /// <summary>原子寫入（底層 blob 保證；檔案版寫暫存後改名、DB 版交易）</summary>
    public void SaveAll(List<RuleSuppression> suppressions) =>
        _blob.Mutate<object?>(_ => (JsonSerializer.Serialize(suppressions, _options), null));
}
