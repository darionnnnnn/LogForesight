using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using LogForesight.Web.Models;
using Microsoft.AspNetCore.Mvc.Filters;

namespace LogForesight.Web.Filters;

/// <summary>
/// 請求 DTO 的預設輸入上限：字串欄位與集合欄位沒有自己宣告上限（<see cref="StringLengthAttribute"/>、
/// <see cref="MaxLengthAttribute"/>、<see cref="LengthAttribute"/>）時，套用寬鬆的預設值。
/// 一處涵蓋所有請求（含日後新增的欄位），不靠每個欄位記得加屬性；預設值寬到不影響任何正常操作，
/// 只擋惡意或錯誤的超大輸入（例如一次送上百萬個 id）。業務上限仍由各欄位的屬性或服務層決定。
/// 只檢查第一層屬性：巢狀物件的欄位由它自己的屬性負責。
/// </summary>
public sealed class DefaultInputLimitsFilter : IActionFilter
{
    public const int DefaultMaxStringLength = 65_536;
    public const int DefaultMaxCollectionCount = 50_000;

    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> UnboundedProperties = new();

    public void OnActionExecuting(ActionExecutingContext context)
    {
        foreach (var argument in context.ActionArguments.Values)
        {
            if (argument is null || !IsRequestDto(argument.GetType())) continue;
            foreach (var property in UnboundedProperties.GetOrAdd(argument.GetType(), FindUnbounded))
            {
                var value = property.GetValue(argument);
                if (value is string text && text.Length > DefaultMaxStringLength)
                    throw DomainException.Validation($"「{property.Name}」的內容過長（上限 {DefaultMaxStringLength:N0} 字）。");
                if (value is ICollection collection && collection.Count > DefaultMaxCollectionCount)
                    throw DomainException.Validation($"「{property.Name}」的項目過多（上限 {DefaultMaxCollectionCount:N0} 筆）。");
            }
        }
    }

    public void OnActionExecuted(ActionExecutedContext context)
    {
    }

    internal static bool IsRequestDto(Type type) =>
        type.IsClass && type.Namespace?.StartsWith("LogForesight.Web.Models", StringComparison.Ordinal) == true;

    /// <summary>沒有宣告上限的字串與集合屬性（依型別快取；型別數量固定，快取有自然上限）</summary>
    internal static PropertyInfo[] FindUnbounded(Type type) => type
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
        .Where(p => p.PropertyType == typeof(string) || (p.PropertyType != typeof(string) && typeof(ICollection).IsAssignableFrom(p.PropertyType)))
        .Where(p => !p.IsDefined(typeof(StringLengthAttribute), true) && !p.IsDefined(typeof(MaxLengthAttribute), true)
                    && !p.IsDefined(typeof(LengthAttribute), true))
        .ToArray();
}
