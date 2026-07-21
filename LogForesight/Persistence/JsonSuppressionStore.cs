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

    private readonly string _filePath;
    private readonly JsonSerializerOptions _options;

    public JsonSuppressionStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(AppContext.BaseDirectory, "suppressions.json");

        _options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    public string Location => _filePath;

    public List<RuleSuppression> LoadAll()
    {
        if (!File.Exists(_filePath))
        {
            return new List<RuleSuppression>();
        }

        string text;
        try
        {
            text = File.ReadAllText(_filePath, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "suppressions.json 讀取失敗，本次視為無抑制設定：{Path}", _filePath);
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
            Log.Warn("suppressions.json 格式錯誤，本次視為無抑制設定：{Path}，原因：{Error}", _filePath, ex.Message);
            return new List<RuleSuppression>();
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                Log.Warn("suppressions.json 根節點不是陣列，本次視為無抑制設定：{Path}", _filePath);
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

    /// <summary>原子寫入：寫暫存檔後改名，與 JsonKnownIssueRuleStore 同一套做法</summary>
    public void SaveAll(List<RuleSuppression> suppressions)
    {
        var json = JsonSerializer.Serialize(suppressions, _options);
        var tmpPath = _filePath + ".tmp";

        File.WriteAllText(tmpPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        File.Move(tmpPath, _filePath, overwrite: true);
    }
}
