using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LogForesight.Web.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace LogForesight.Tests.Scenarios;

/// <summary>
/// 情境用的假時鐘：只在驅動器明確撥動時才前進，其餘時間凍結。站台內交辦、靜音、觀察中的「現在」「今天」都取自它，
/// 這樣「第 N 天」的人為動作與夜間派工寫下的時間才會落在第 N 天。本地時區沿用 <see cref="TimeZoneInfo.Local"/>，
/// 與正式站台換算「本地今天」的規則一致。
/// </summary>
public sealed class ScenarioClock : TimeProvider
{
    private DateTimeOffset _utcNow = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Local;

    /// <summary>把時鐘撥到指定的本地時刻</summary>
    public void SetLocal(DateTime local) =>
        _utcNow = new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Local)).ToUniversalTime();
}

/// <summary>人為動作打 HTTP API 的結果；失敗（4xx／5xx）照樣是一筆結果，不會讓情境中斷</summary>
public sealed record ActionHttpResult(int StatusCode, string? ErrorCode, string? Message, string? Body = null);

/// <summary>站台寄出的一封信（測試站台以替身取代 SMTP 傳輸，信不會真的出去）</summary>
public sealed record SentMailRecord(IReadOnlyList<string> To, string Subject, string Body);

/// <summary>記錄寄出內容的 SMTP 替身：只記錄、永遠成功</summary>
internal sealed class RecordingSmtpSender : LogForesight.Web.Services.Mail.ISmtpMailSender
{
    private readonly List<SentMailRecord> _sent = new();

    public IReadOnlyList<SentMailRecord> Snapshot()
    {
        lock (_sent) return _sent.ToList();
    }

    public Task SendAsync(LogForesight.Web.Services.Mail.SmtpConnectionSpec connection,
        LogForesight.Web.Services.Mail.MailMessageSpec message, CancellationToken ct = default)
    {
        lock (_sent) _sent.Add(new SentMailRecord(message.To.ToList(), message.Subject, message.Body));
        return Task.CompletedTask;
    }
}

/// <summary>
/// 情境用的整站：<see cref="WebApplicationFactory{TEntryPoint}"/> 起真的站台組裝（Stub 認證、Development 環境、
/// 真 JWT、真路由、真 <c>[Permission]</c> 與可見範圍），全部背景服務移除。
/// 同一個 <see cref="StorageBackend"/>（SQLite 檔案）同時給站台與分析流程使用——分析寫進去的結果站台讀得到。
///
/// 站台設定走行程層級環境變數（Program 在 builder.Build 前就讀組態，WebApplicationFactory 的設定掛勾來不及），
/// 所以使用情境驅動器的測試類別必須標 <c>[Collection("NLogGlobalConfig")]</c>（該集合關閉並行）。
/// </summary>
public sealed class ScenarioSite : IDisposable
{
    private readonly string _dataRoot;
    private readonly Dictionary<string, string?> _savedEnv = new();
    private readonly NLog.Config.LoggingConfiguration? _savedNLogConfig;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly Dictionary<string, string> _tokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly RecordingSmtpSender _mailSender = new();

    /// <summary>站台目前為止寄出的信（依寄出順序）；寄送由站台自己的服務執行，傳輸層換成記錄用替身</summary>
    public IReadOnlyList<SentMailRecord> SentMail => _mailSender.Snapshot();

    /// <summary>站台使用的假時鐘（已取代 DI 的 <see cref="TimeProvider"/>）；驅動器每天開始前把它撥到該天的執行日</summary>
    public ScenarioClock Clock { get; } = new();

    public IServiceProvider Services => _factory.Services;

    public T Get<T>() where T : notnull => _factory.Services.GetRequiredService<T>();

    public ScenarioSite()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "lf-scenario-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);

        _savedNLogConfig = NLog.LogManager.Configuration;
        try
        {
            SetEnv("Storage__Type", "Sqlite");
            SetEnv("Storage__DataRoot", _dataRoot);
            SetEnv("Storage__ConnectionString", "");
            SetEnv("Auth__Provider", "Stub");

            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseSetting(Microsoft.AspNetCore.Hosting.WebHostDefaults.EnvironmentKey, "Development");
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IHostedService>();
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton<TimeProvider>(Clock);
                    services.RemoveAll<LogForesight.Web.Services.Mail.ISmtpMailSender>();
                    services.AddSingleton<LogForesight.Web.Services.Mail.ISmtpMailSender>(_mailSender);
                });
            });

            _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = false,
                BaseAddress = new Uri("https://localhost")
            });
            _client.DefaultRequestHeaders.Add("X-Requested-By", "LogForesight");

            // AI：站台的 AI 設定預設指向 localhost:8080（可能真有東西在聽）。情境一律不呼叫 AI，
            // 這裡指到不會有人聽的位址並關重試，萬一有程式路徑碰到也只是立刻失敗，不會對外連線
            Get<ISystemSettingsStore>().Update(s =>
            {
                s.AiBaseUrl = "http://127.0.0.1:1";
                s.AiRetryCount = 0;
                s.AiTimeoutSeconds = 2;
            });
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void SetEnv(string key, string value)
    {
        _savedEnv[key] = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable(key, value);
    }

    /// <summary>登入（Stub 認證免密碼）。失敗（帳號停用等）回 false，其後該帳號的請求以匿名送出，回應自然是 401。</summary>
    public async Task<bool> LoginAsync(string account)
    {
        using var response = await _client.PostAsJsonAsync("api/auth/login", new { account, password = "" });
        if (response.StatusCode != HttpStatusCode.OK) return false;
        var cookie = AuthTestKit.ReadSessionCookiePair(response);
        if (string.IsNullOrEmpty(cookie)) return false;
        _tokens[account] = cookie;
        return true;
    }

    /// <summary>以指定帳號的身分送請求；<paramref name="body"/> 非 null 時以 JSON 送出。</summary>
    public async Task<ActionHttpResult> SendAsync(string account, HttpMethod method, string url, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (_tokens.TryGetValue(account, out var token)) request.Headers.Add("Cookie", token);
        if (body != null) request.Content = JsonContent.Create(body);
        using var response = await _client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        var (code, message) = ParseError(text);
        return new ActionHttpResult((int)response.StatusCode, code, message, text);
    }

    private static (string? Code, string? Message) ParseError(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return (null, null);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (!prop.NameEquals("error") && !string.Equals(prop.Name, "error", StringComparison.OrdinalIgnoreCase)) continue;
                if (prop.Value.ValueKind != JsonValueKind.Object) return (null, null);
                string? code = null, message = null;
                foreach (var inner in prop.Value.EnumerateObject())
                {
                    if (string.Equals(inner.Name, "code", StringComparison.OrdinalIgnoreCase)) code = inner.Value.GetString();
                    else if (string.Equals(inner.Name, "message", StringComparison.OrdinalIgnoreCase)) message = inner.Value.GetString();
                }
                return (code, message);
            }
        }
        catch (JsonException)
        {
            // 非 JSON 回應（例如 401 的空內容）沒有錯誤碼，照實回 null
        }
        return (null, null);
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
        foreach (var (key, value) in _savedEnv) Environment.SetEnvironmentVariable(key, value);
        // 站台主程式結束時在 finally 呼叫 LogManager.Shutdown()（把全域設定清成 null），可能晚於 Dispose 返回才跑：
        // 等 Shutdown 跑完再還原 NLog 設定，上限 10 秒（同授權矩陣 fixture 的作法）
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (NLog.LogManager.Configuration != null && DateTime.UtcNow < deadline) Thread.Sleep(20);
        NLog.LogManager.Configuration = _savedNLogConfig;
        CryptoHelper.ResetForTests();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataRoot, recursive: true); } catch (IOException) { /* SQLite 檔案可能仍被連線池占用，留在暫存目錄無妨 */ }
    }
}
