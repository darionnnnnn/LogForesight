namespace LogForesight;

/// <summary>
/// 抑制設定的最小維護指令（`--suppress` / `--unsuppress` / `--list-suppressions`）。
/// 手編 suppressions.json 的中文 reason 容易打錯逗號/引號，提供 CLI 是為了讓維護者
/// 不用直接碰 JSON 語法（見 docs/RULES-PLAN.md）。三個指令都只操作本機（Environment.MachineName）
/// 的抑制項目，且都會把結果印到 console，方便排程或手動操作後立即確認。
/// </summary>
public static class SuppressionCli
{
    public static void List(ISuppressionStore store)
    {
        var all = store.LoadAll();
        if (all.Count == 0)
        {
            Console.WriteLine($"目前沒有任何抑制設定（{store.Location}）。");
            return;
        }

        Console.WriteLine($"目前共有 {all.Count} 筆抑制設定（{store.Location}）：");
        foreach (var s in all)
        {
            var expiry = s.ExpiresAt == null
                ? "永久"
                : $"到期於 {s.ExpiresAt:yyyy-MM-dd}" + (s.ExpiresAt.Value <= DateTime.Now ? "（已到期，恢復告警中，可用 --unsuppress 或編輯 suppressions.json 清理）" : "");
            Console.WriteLine($"  - {s.RuleId}｜主機 {s.Host}｜{expiry}｜原因：{s.Reason}｜設定者：{s.SuppressedBy}｜{s.CreatedAt:yyyy-MM-dd HH:mm}");
        }
    }

    /// <param name="knownRuleIds">目前規則庫中存在的 Id 集合，避免抑制一個打錯字/不存在的 Id 而悄悄無效</param>
    public static void Suppress(ISuppressionStore store, HashSet<string> knownRuleIds, string? ruleId, string? reason, string? daysText)
    {
        if (string.IsNullOrWhiteSpace(ruleId))
        {
            Console.WriteLine("請用 --suppress <ruleId> 指定要抑制的規則 Id。");
            return;
        }
        if (!knownRuleIds.Contains(ruleId))
        {
            Console.WriteLine($"規則 Id「{ruleId}」不存在於目前的規則庫，未寫入。可查看 rules.json 確認正確的 Id。");
            return;
        }
        if (string.IsNullOrWhiteSpace(reason))
        {
            Console.WriteLine("請用 --reason \"文字\" 說明抑制原因（供未來回查與管理頁顯示，不可留空）。");
            return;
        }

        DateTime? expiresAt = null;
        if (!string.IsNullOrWhiteSpace(daysText))
        {
            if (!int.TryParse(daysText, out var days) || days <= 0)
            {
                Console.WriteLine($"--days 必須是正整數，實際為「{daysText}」，未寫入。");
                return;
            }
            expiresAt = DateTime.Now.AddDays(days);
        }

        var host = Environment.MachineName;
        var all = store.LoadAll();
        all.RemoveAll(s => s.RuleId.Equals(ruleId, StringComparison.OrdinalIgnoreCase) && s.Host.Equals(host, StringComparison.OrdinalIgnoreCase));
        all.Add(new RuleSuppression
        {
            RuleId = ruleId,
            Host = host,
            Reason = reason,
            SuppressedBy = Environment.UserName,
            CreatedAt = DateTime.Now,
            ExpiresAt = expiresAt
        });
        store.SaveAll(all);

        Console.WriteLine($"已抑制 {ruleId}（主機 {host}），" +
            (expiresAt != null ? $"{expiresAt:yyyy-MM-dd} 到期" : "永久生效，直到手動 --unsuppress") +
            $"。原因：{reason}");
        Console.WriteLine("注意：只影響通知與風險升級，偵測與紀錄照常，關聯層比對也不受影響（見 docs/RULES-PLAN.md）。");
    }

    public static void Unsuppress(ISuppressionStore store, string? ruleId)
    {
        if (string.IsNullOrWhiteSpace(ruleId))
        {
            Console.WriteLine("請用 --unsuppress <ruleId> 指定要恢復告警的規則 Id。");
            return;
        }

        var host = Environment.MachineName;
        var all = store.LoadAll();
        int removed = all.RemoveAll(s => s.RuleId.Equals(ruleId, StringComparison.OrdinalIgnoreCase) && s.Host.Equals(host, StringComparison.OrdinalIgnoreCase));
        if (removed == 0)
        {
            Console.WriteLine($"本機（{host}）目前沒有 {ruleId} 的抑制設定，無需處理。");
            return;
        }

        store.SaveAll(all);
        Console.WriteLine($"已恢復 {ruleId}（主機 {host}）的告警。");
    }
}
