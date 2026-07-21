using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NLog;

namespace LogForesight;

/// <summary>
/// 以人可編輯的 JSON 檔（rules.json）儲存規則表。這是 <see cref="IKnownIssueRuleStore"/> 的預設實作；
/// 換成 DB 後端時只需新增另一個實作類別，呼叫端不需修改（與 JsonlAnalysisRecordStore 同一模式）。
///
/// 容錯設計（見 docs/RULES-PLAN.md 陷阱 3）：整檔 JSON 語法錯誤時 Load 失敗且**不覆寫使用者的壞檔**，
/// 讓使用者能看著原檔修正；單一規則物件解析失敗（欄位型別不合、enum 打錯字）只跳過該條，
/// 其餘規則照常載入——手動編輯打錯一條不該讓整份規則表失效。
/// </summary>
public class JsonKnownIssueRuleStore : IKnownIssueRuleStore
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly string _filePath;
    private readonly JsonSerializerOptions _options;

    public JsonKnownIssueRuleStore(string? filePath = null)
    {
        // 預設放執行檔同目錄（與 appsettings.json/history.txt 一致），用 AppContext.BaseDirectory
        // 而非 CurrentDirectory——排程執行時後者可能是 system32
        _filePath = filePath ?? Path.Combine(AppContext.BaseDirectory, "rules.json");

        _options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            Converters = { new JsonStringEnumConverter() }
        };

        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    public string Location => _filePath;

    public bool Exists => File.Exists(_filePath);

    public RuleLoadOutcome Load()
    {
        if (!Exists)
        {
            return RuleLoadOutcome.Fail("檔案不存在");
        }

        string text;
        try
        {
            text = File.ReadAllText(_filePath, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            return RuleLoadOutcome.Fail($"讀取檔案失敗：{ex.Message}");
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
            return RuleLoadOutcome.Fail($"JSON 格式錯誤：{ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;

            int schemaVersion = TryGetInt(root, "SchemaVersion") ?? 1;
            if (schemaVersion > RuleFileContent.CurrentSchemaVersion)
            {
                return RuleLoadOutcome.Fail(
                    $"rules.json 的 SchemaVersion（{schemaVersion}）高於本程式支援的版本" +
                    $"（{RuleFileContent.CurrentSchemaVersion}），請升級程式後再讀取此檔案");
            }

            int seedVersion = TryGetInt(root, "SeedVersion") ?? 0;

            var rules = new List<KnownIssueRule>();
            var skipped = new List<string>();

            if (root.TryGetProperty("Rules", out var rulesElement) && rulesElement.ValueKind == JsonValueKind.Array)
            {
                int index = 0;
                foreach (var element in rulesElement.EnumerateArray())
                {
                    index++;
                    try
                    {
                        var rule = element.Deserialize<KnownIssueRule>(_options);
                        if (rule != null)
                        {
                            rules.Add(rule);
                        }
                        else
                        {
                            skipped.Add($"第 {index} 條解析為 null");
                        }
                    }
                    catch (Exception ex)
                    {
                        skipped.Add($"第 {index} 條解析失敗：{ex.Message}");
                    }
                }
            }

            if (skipped.Count > 0)
            {
                Log.Warn("rules.json 有 {Count} 條規則物件解析失敗，已跳過（其餘規則照常載入）：{Details}",
                    skipped.Count, string.Join("；", skipped));
            }

            return RuleLoadOutcome.Ok(new RuleFileContent
            {
                SchemaVersion = schemaVersion,
                SeedVersion = seedVersion,
                Rules = rules
            });
        }
    }

    /// <summary>
    /// 原子寫入：先寫暫存檔再改名覆蓋，避免程式在寫入途中被中斷（斷電、被殺）留下半個
    /// 損毀的規則檔——那樣的話下次啟動會被誤判成「整檔壞掉」而降級用內建種子。
    /// UTF-8 with BOM：記事本等工具在無 BOM 時容易誤判編碼，中文內容顯示亂碼是最容易踩的坑。
    /// </summary>
    public void Save(RuleFileContent content)
    {
        var json = JsonSerializer.Serialize(content, _options);
        var tmpPath = _filePath + ".tmp";

        File.WriteAllText(tmpPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        File.Move(tmpPath, _filePath, overwrite: true);
    }

    private static int? TryGetInt(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var el) && el.TryGetInt32(out var value) ? value : null;
}
