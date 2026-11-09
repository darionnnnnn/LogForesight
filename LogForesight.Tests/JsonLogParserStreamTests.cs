using System.Text.Json;
using Xunit;

namespace LogForesight.Tests;

public class JsonLogParserStreamTests
{
    private sealed class Item
    {
        public int Id { get; set; }
    }

    [Fact]
    public void ParseStream跳過空白與損毀行()
    {
        var lines = new[] { "{\"Id\":1}", "", "   ", "{壞掉", "{\"Id\":2}", "null" };

        var items = JsonLogParser.ParseStream<Item>(lines, new JsonSerializerOptions()).ToList();

        Assert.Equal(new[] { 1, 2 }, items.Select(i => i.Id));
    }

    [Fact]
    public void ParseStream是惰性的_只取前兩筆不會碰到第三行的例外()
    {
        var items = JsonLogParser.ParseStream<Item>(Throwing(), new JsonSerializerOptions()).Take(2).ToList();

        Assert.Equal(2, items.Count);
    }

    private static IEnumerable<string> Throwing()
    {
        yield return "{\"Id\":1}";
        yield return "{\"Id\":2}";
        throw new InvalidOperationException("不應列舉到第 3 行");
    }
}
