using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NLog;

namespace LogForesight.Core.Persistence;

/// <summary>
/// 規則表的儲存邏輯（容錯解析／原子寫入語意）。這是 <see cref="IKnownIssueRuleStore"/> 的實作，
/// 透過注入的 <see cref="EfJsonBlobStore"/> 不受底層是檔案或 DB blob 影響。
///
/// 容錯設計（見 docs/RULES-SPEC.md 陷阱 3）：整檔 JSON 語法錯誤時 Load 失敗且**不覆寫使用者的壞檔**，
/// 讓使用者能看著原檔修正；單一規則物件解析失敗（欄位型別不合、enum 打錯字）只跳過該條，
/// 其餘規則照常載入——手動編輯打錯一條不該讓整份規則表失效。
/// </summary>
public class KnownIssueRuleStore : IKnownIssueRuleStore
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly EfJsonBlobStore _blob;
    private readonly JsonSerializerOptions _options;

    public KnownIssueRuleStore(EfJsonBlobStore blob)
    {
        _blob = blob;
        _options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            Converters = { new JsonStringEnumConverter() }
        };
    }

    public string Location => _blob.Location;

    public bool Exists => _blob.Exists();

    public RuleLoadOutcome Load()
    {
        return LoadText(_blob.Read());
    }

    public RuleLoadOutcome LoadBounded(int maximumCharacters)
    {
        if (maximumCharacters is < 1 or > int.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
        var (prefix, _, reportedLength) = _blob.ReadBoundedWithVersion(maximumCharacters);
        if (reportedLength > maximumCharacters || (prefix?.Length ?? 0) > maximumCharacters)
            return RuleLoadOutcome.CapacityFailure($"rules blob 超過 {maximumCharacters} 字元校準上限");
        if (prefix != null && !HasBoundedRuleShape(prefix, out var shapeError, out var capacityExceeded))
            return capacityExceeded ? RuleLoadOutcome.CapacityFailure(shapeError) : RuleLoadOutcome.Fail(shapeError);
        return LoadText(prefix);
    }

    private static bool HasBoundedRuleShape(string json, out string error, out bool capacityExceeded)
    {
        const int maxTokens = 50_000;
        capacityExceeded = false;
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json), new JsonReaderOptions
        {
            MaxDepth = 64,
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });
        var arrays = new Stack<(string? Name, int Depth, int Count)>();
        string? propertyName = null;
        var tokens = 0;
        try
        {
            while (reader.Read())
            {
                if (++tokens > maxTokens)
                {
                    capacityExceeded = true;
                    error = $"rules JSON 超過校準節點上限（{maxTokens}）";
                    return false;
                }
                if (arrays.Count > 0 && reader.CurrentDepth == arrays.Peek().Depth + 1 &&
                    reader.TokenType is not (JsonTokenType.EndArray or JsonTokenType.EndObject))
                {
                    var frame = arrays.Pop();
                    frame.Count++;
                    var cap = frame.Name?.Equals("Rules", StringComparison.OrdinalIgnoreCase) == true ? 10_000 : 1_024;
                    if (frame.Count > cap)
                    {
                        capacityExceeded = true;
                        error = $"rules JSON 的 {frame.Name ?? "nested"} 陣列超過校準項目上限（{cap}）";
                        return false;
                    }
                    arrays.Push(frame);
                }
                if (reader.TokenType == JsonTokenType.PropertyName) propertyName = reader.GetString();
                else if (reader.TokenType == JsonTokenType.StartArray)
                {
                    arrays.Push((propertyName, reader.CurrentDepth, 0));
                    propertyName = null;
                }
                else if (reader.TokenType == JsonTokenType.EndArray)
                {
                    if (arrays.Count == 0) break;
                    arrays.Pop();
                }
                else propertyName = null;
            }
        }
        catch (JsonException)
        {
            error = "rules JSON 結構無效，校準拒絕解析";
            return false;
        }
        error = string.Empty;
        return true;
    }

    private RuleLoadOutcome LoadText(string? text)
    {
        if (text == null)
        {
            return RuleLoadOutcome.Fail("檔案不存在");
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

            // 舊資料相容（docs/archive/HISTORY.md #1，B1 三級化）：單一咽喉點，批次的
            // RuleBootstrapper 與 Web 的 RuleAdminService/RecordDetailQueryService 都經由 Load() 讀取，
            // 在這裡正規化一次即可涵蓋全部呼叫端，不必各自記得處理舊值
            KnownIssueCatalog.NormalizeLegacyCriticalSeverity(rules);

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
    public void Save(RuleFileContent content) =>
        _blob.Mutate<object?>(_ => (JsonSerializer.Serialize(content, _options), null));

    private static int? TryGetInt(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var el) && el.TryGetInt32(out var value) ? value : null;
}
