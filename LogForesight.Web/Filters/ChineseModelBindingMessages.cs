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

    /// <summary>沒寫 ErrorMessage 的 [Required]（含非 nullable 參考型別的隱含必填）補上中文訊息</summary>
    private sealed class ImplicitRequiredMessageProvider : IValidationMetadataProvider
    {
        public void CreateValidationMetadata(ValidationMetadataProviderContext context)
        {
            foreach (var required in context.ValidationMetadata.ValidatorMetadata.OfType<RequiredAttribute>())
            {
                if (string.IsNullOrEmpty(required.ErrorMessage) && required.ErrorMessageResourceType == null)
                    required.ErrorMessage = "「{0}」為必填。";
            }
        }
    }
}
