using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;

namespace LogForesight.Web.Filters;

/// <summary>
/// 模型繫結與隱含必填的預設訊息改成中文：驗證失敗的信封訊息會直接顯示給使用者，
/// 框架預設是英文（例如查詢參數型別錯誤、非 nullable 參考型別缺值）。
/// </summary>
public static class ChineseModelBindingMessages
{
    /// <summary>
    /// 有中文預設訊息的驗證屬性型別（沒寫 ErrorMessage 時由 <see cref="ImplicitRequiredMessageProvider"/> 補上）。
    /// 反射守門（<c>ValidationCoverageGuardTests</c>）掃 Web Models 內所有沒寫 ErrorMessage 的驗證屬性，
    /// 型別不在這張表內就紅：新增別種驗證屬性時要在 <see cref="DefaultMessageFor"/> 補訊息並登錄進來。
    /// </summary>
    internal static readonly IReadOnlySet<Type> CoveredAttributeTypes = new HashSet<Type>
    {
        typeof(RequiredAttribute), typeof(StringLengthAttribute), typeof(MaxLengthAttribute), typeof(MinLengthAttribute),
        typeof(LengthAttribute), typeof(RangeAttribute), typeof(EmailAddressAttribute), typeof(RegularExpressionAttribute)
    };

    /// <summary>
    /// 沒寫 ErrorMessage 時的中文預設訊息；不涵蓋的型別回 null（維持框架原訊息）。框架預設是英文且 {0} 代入屬性名
    /// （C# 欄位名會直接露給使用者），所以沒有 [Display] 時訊息不帶欄位名、只講允許範圍；有 [Display] 才以「顯示名稱」開頭。
    /// 格式參數依各屬性：StringLength {1}=上限 {2}=下限、MaxLength／MinLength {1}、Length {1}=下限 {2}=上限、Range {1}=下限 {2}=上限。
    /// </summary>
    internal static string? DefaultMessageFor(ValidationAttribute attribute, Type modelType, string? displayName)
    {
        var unit = typeof(string).IsAssignableFrom(modelType) ? "個字" : "筆";
        var body = attribute switch
        {
            RequiredAttribute => "為必填。",
            StringLengthAttribute { MinimumLength: > 0 } => "長度必須介於 {2} 到 {1} 個字。",
            StringLengthAttribute => "長度不可超過 {1} 個字。",
            MaxLengthAttribute => $"長度不可超過 {{1}} {unit}。",
            MinLengthAttribute => $"長度不可少於 {{1}} {unit}。",
            LengthAttribute => $"長度必須介於 {{1}} 到 {{2}} {unit}。",
            RangeAttribute => "輸入值必須介於 {1} 到 {2}。",
            EmailAddressAttribute => "電子郵件格式不正確。",
            RegularExpressionAttribute => "格式不正確。",
            _ => null
        };
        if (body == null) return null;
        // [Required] 維持既有的「{0}」寫法（沒有 [Display] 時 {0} 是屬性名，是既有行為，不在這次收斂範圍）
        if (attribute is RequiredAttribute) return "「{0}」" + body;
        return string.IsNullOrEmpty(displayName) ? (attribute is RangeAttribute ? body : "輸入內容" + body) : "「{0}」" + body;
    }

    public static void Apply(MvcOptions options)
    {
        var m = options.ModelBindingMessageProvider;
        m.SetAttemptedValueIsInvalidAccessor((value, field) => $"「{field}」的值「{value}」格式不正確。");
        m.SetNonPropertyAttemptedValueIsInvalidAccessor(value => $"「{value}」格式不正確。");
        m.SetUnknownValueIsInvalidAccessor(field => $"「{field}」的值格式不正確。");
        m.SetNonPropertyUnknownValueIsInvalidAccessor(() => "輸入的值格式不正確。");
        m.SetValueIsInvalidAccessor(value => $"「{value}」不是有效的值。");
        m.SetValueMustBeANumberAccessor(field => $"「{field}」必須是數字。");
        m.SetNonPropertyValueMustBeANumberAccessor(() => "必須是數字。");
        m.SetValueMustNotBeNullAccessor(value => $"「{value}」不可為空。");
        m.SetMissingBindRequiredValueAccessor(field => $"缺少必要的「{field}」。");
        m.SetMissingKeyOrValueAccessor(() => "缺少必要的值。");
        m.SetMissingRequestBodyRequiredValueAccessor(() => "請求內容不可為空。");
        options.ModelMetadataDetailsProviders.Add(new ImplicitRequiredMessageProvider());
    }

    /// <summary>沒寫 ErrorMessage 的驗證屬性（含非 nullable 參考型別的隱含必填）補上中文訊息；已手寫的維持原樣</summary>
    private sealed class ImplicitRequiredMessageProvider : IValidationMetadataProvider
    {
        public void CreateValidationMetadata(ValidationMetadataProviderContext context)
        {
            var displayName = context.Attributes.OfType<DisplayAttribute>().FirstOrDefault()?.Name;
            foreach (var attribute in context.ValidationMetadata.ValidatorMetadata.OfType<ValidationAttribute>())
            {
                if (!string.IsNullOrEmpty(attribute.ErrorMessage) || attribute.ErrorMessageResourceType != null) continue;
                var message = DefaultMessageFor(attribute, context.Key.ModelType, displayName);
                if (message != null) attribute.ErrorMessage = message;
            }
        }
    }
}
