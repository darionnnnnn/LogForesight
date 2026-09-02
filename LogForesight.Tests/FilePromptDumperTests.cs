using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// AI 診斷傾印（FilePromptDumper）硬上限與清理測試
/// </summary>
public class FilePromptDumperTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lf-prompt-dump-" + Guid.NewGuid().ToString("N"));

    public FilePromptDumperTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // 暫存目錄清理失敗不影響測試結論
        }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Prune_超過保留天數的txt被刪除_未超過保留天數的txt保留()
    {
        var oldFile = Path.Combine(_dir, "old.txt");
        var newFile = Path.Combine(_dir, "new.txt");
        File.WriteAllText(oldFile, "old content");
        File.WriteAllText(newFile, "new content");

        File.SetLastWriteTime(oldFile, DateTime.Now.AddDays(-10));
        File.SetLastWriteTime(newFile, DateTime.Now.AddDays(-2));

        var pruned = FilePromptDumper.Prune(retentionDays: 7, dir: _dir);

        Assert.Equal(1, pruned);
        Assert.False(File.Exists(oldFile));
        Assert.True(File.Exists(newFile));
    }

    [Fact]
    public void Prune_目錄不存在時回0且不擲例外且不建立目錄()
    {
        var nonExistent = Path.Combine(_dir, "nonexistent-" + Guid.NewGuid().ToString("N"));
        Assert.False(Directory.Exists(nonExistent));

        var pruned = FilePromptDumper.Prune(retentionDays: 7, dir: nonExistent);

        Assert.Equal(0, pruned);
        Assert.False(Directory.Exists(nonExistent));
    }

    [Fact]
    public void Prune_非txt檔案即使很舊也不被刪除()
    {
        var oldLog = Path.Combine(_dir, "old.log");
        var oldJson = Path.Combine(_dir, "old.json");
        File.WriteAllText(oldLog, "log content");
        File.WriteAllText(oldJson, "json content");

        File.SetLastWriteTime(oldLog, DateTime.Now.AddDays(-30));
        File.SetLastWriteTime(oldJson, DateTime.Now.AddDays(-30));

        var pruned = FilePromptDumper.Prune(retentionDays: 7, dir: _dir);

        Assert.Equal(0, pruned);
        Assert.True(File.Exists(oldLog));
        Assert.True(File.Exists(oldJson));
    }

    [Fact]
    public void Prune_retentionDays小於1_回0且不清()
    {
        var oldFile = Path.Combine(_dir, "old.txt");
        File.WriteAllText(oldFile, "content");
        File.SetLastWriteTime(oldFile, DateTime.Now.AddDays(-30));

        Assert.Equal(0, FilePromptDumper.Prune(0, _dir));
        Assert.Equal(0, FilePromptDumper.Prune(-5, _dir));
        Assert.True(File.Exists(oldFile));
    }

    [Fact]
    public void Prune_不遞迴子目錄()
    {
        var subDir = Path.Combine(_dir, "sub");
        Directory.CreateDirectory(subDir);
        var subOldFile = Path.Combine(subDir, "old.txt");
        File.WriteAllText(subOldFile, "sub content");
        File.SetLastWriteTime(subOldFile, DateTime.Now.AddDays(-30));

        var pruned = FilePromptDumper.Prune(retentionDays: 7, dir: _dir);

        Assert.Equal(0, pruned);
        Assert.True(File.Exists(subOldFile));
    }

    [Fact]
    public void Dump_超過單次執行硬上限2000次後不再寫檔_檔案數等於上限2000()
    {
        var dumper = new FilePromptDumper(_dir);

        for (var i = 0; i < 2005; i++)
        {
            dumper.Dump("test", "sys", "prompt", "resp");
        }

        var files = Directory.GetFiles(_dir, "*.txt");
        Assert.Equal(2000, files.Length);
    }

    [Fact]
    public void Dump_正常寫入格式正確()
    {
        var dumper = new FilePromptDumper(_dir);
        dumper.Dump("mylabel", "system_prompt_text", "prompt_text", "response_text");

        var files = Directory.GetFiles(_dir, "*_mylabel.txt");
        Assert.Single(files);

        var content = File.ReadAllText(files[0]);
        Assert.Contains("=== SYSTEM PROMPT ===\nsystem_prompt_text", content);
        Assert.Contains("=== PROMPT ===\nprompt_text", content);
        Assert.Contains("=== RESPONSE ===\nresponse_text", content);
    }
}
